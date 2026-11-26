using System.Text;
using Blam.Halo1;
using Blam.Halo1.TagData;

namespace Sandbox.Mounting.Blam.Halo1;

/// <summary>
/// Tag data as JSON text resources, for games to read gameplay values (weapons, projectiles,
/// damage, spawns...) straight from the maps (see <see cref="TagExport"/>):
/// <list type="bullet">
/// <item><c>mount://haloce/game.json</c>: everything a multiplayer game reads, from the
/// multiplayer maps.</item>
/// <item><c>mount://haloce/hud.json</c>: the HUD (unit, weapon and grenade HUDs, HUD globals,
/// digits, meters and the bitmaps they draw), from the multiplayer maps.</item>
/// <item><c>mount://haloce/maps/&lt;map&gt;.json</c>: a map's player starts, netgame flags, item
/// spawns, starting equipment and placed vehicles, weapons and equipment.</item>
/// <item><c>mount://haloce/maps/&lt;map&gt;.campaign.json</c>: what a campaign mission's scripts
/// and cinematics work on (see <see cref="TagExport.Campaign"/>).</item>
/// <item><c>mount://haloce/maps/&lt;map&gt;.paths.&lt;bsp&gt;.json</c>: what a campaign mission's AI
/// pathfinding, and a multiplayer map's breakable surfaces, read from one of its structure BSPs
/// (see <see cref="TagExport.CampaignPaths"/>).</item>
/// <item><c>mount://haloce/tags/&lt;tag path&gt;.&lt;class&gt;.json</c>: one tag, e.g.
/// <c>tags/weapons/pistol/pistol.weap.json</c>.</item>
/// <item><c>mount://haloce/anims/&lt;graph tag path&gt;/&lt;animation name&gt;.json</c> and
/// <c>anims/&lt;graph tag path&gt;.json</c>: animations with their frames (see <see cref="AnimationDataResources"/>).</item>
/// </list>
/// </summary>
/// <remarks>
/// Game code can't reference the mount, but it can reach any mounted resource through the
/// (whitelisted) mounting API, and a text resource loads as its string:
/// <code>
/// var loader = Sandbox.Mounting.Directory.Get( "haloce" )?.GetByPath( "mount://haloce/game.json" );
/// var json = loader is null ? null : await loader.GetOrCreate() as string;
/// </code>
/// The engine caches the string after the first load. Documents are built on the calling thread
/// when first asked for (game.json reads the 19 multiplayer maps' tag data, a fraction of a
/// second warm). The editor's asset browser doesn't list them (no asset type for .json).
///
/// A tag comes from the first multiplayer map that has it, else the first map: the pistol, its
/// bullet, the plasma rifle and the globals differ between the campaign and multiplayer maps
/// (BlamInspect <c>exportdiff</c>), and a multiplayer game wants the multiplayer ones.
/// </remarks>
sealed class TagDataResources
{
	readonly Dictionary<string, (string MapPath, uint TagId, bool Multiplayer)> _tags = new( StringComparer.OrdinalIgnoreCase );
	readonly List<(string Name, string MapPath)> _maps = new();
	readonly List<string> _multiplayerMaps = new();
	readonly List<(string Name, string MapPath, int BspCount, bool Campaign)> _levels = new();
	readonly AnimationDataResources _animations = new();

	/// <summary>
	/// Notes the exportable tags of a map, while the mount goes through them.
	/// </summary>
	public void Collect( CacheFile cache )
	{
		// Animation graphs from every map, like the models they play on.
		_animations.Collect( cache );

		if ( cache.ScenarioType == ScenarioType.UserInterface )
			return;

		var multiplayer = cache.ScenarioType == ScenarioType.Multiplayer;
		_maps.Add( (cache.Name, cache.FilePath) );
		if ( multiplayer )
			_multiplayerMaps.Add( cache.FilePath );
		_levels.Add( (cache.Name, cache.FilePath, new global::Blam.Halo1.Tags.ScenarioTag( cache, cache.Scenario ).StructureBsps.Count, !multiplayer) );

		foreach ( var tag in cache.Tags )
		{
			if ( !TagExport.CanExport( tag ) )
				continue;

			var path = TagExport.TagPath( tag );
			if ( !_tags.TryGetValue( path, out var found ) || (multiplayer && !found.Multiplayer) )
				_tags[path] = (cache.FilePath, tag.Id, multiplayer);
		}
	}

	public void Register( MountContext context )
	{
		context.Add( ResourceType.Text, "game.json", new MultiplayerJsonLoader( _multiplayerMaps.ToArray(), TagExport.Game ) );
		context.Add( ResourceType.Text, "hud.json", new MultiplayerJsonLoader( _multiplayerMaps.ToArray(), TagExport.Hud ) );

		foreach ( var (name, mapPath) in _maps )
			context.Add( ResourceType.Text, $"maps/{name}.json", new MapJsonLoader( mapPath ) );

		foreach ( var (name, mapPath, bspCount, campaign) in _levels )
		{
			if ( campaign )
				context.Add( ResourceType.Text, $"maps/{name}.campaign.json", new CampaignJsonLoader( mapPath ) );
			for ( var bsp = 0; bsp < bspCount; bsp++ )
				context.Add( ResourceType.Text, $"maps/{name}.paths.{bsp}.json", new CampaignPathsJsonLoader( mapPath, bsp ) );
		}

		foreach ( var (path, (mapPath, tagId, _)) in _tags )
			context.Add( ResourceType.Text, path, new TagJsonLoader( mapPath, tagId ) );

		_animations.Register( context );
	}

	sealed class TagJsonLoader( string mapPath, uint tagId ) : HaloCeLoader
	{
		protected override object Load()
		{
			var cache = Host.GetCache( mapPath );
			return Encoding.UTF8.GetString( TagExport.Tag( cache, cache.GetTag( tagId ) ) );
		}
	}

	sealed class MapJsonLoader( string mapPath ) : HaloCeLoader
	{
		protected override object Load() => Encoding.UTF8.GetString( TagExport.Map( Host.GetCache( mapPath ) ) );
	}

	sealed class CampaignJsonLoader( string mapPath ) : HaloCeLoader
	{
		protected override object Load() => Encoding.UTF8.GetString( TagExport.Campaign( Host.GetCache( mapPath ) ) );
	}

	sealed class CampaignPathsJsonLoader( string mapPath, int bsp ) : HaloCeLoader
	{
		protected override object Load() => Encoding.UTF8.GetString( TagExport.CampaignPaths( Host.GetCache( mapPath ), bsp ) );
	}

	/// <summary>
	/// Opens the multiplayer maps one at a time rather than through the mount's open caches, so
	/// they don't all stay in memory afterwards.
	/// </summary>
	sealed class MultiplayerJsonLoader( string[] multiplayerMaps, Func<IEnumerable<CacheFile>, byte[]> build ) : HaloCeLoader
	{
		protected override object Load() => Encoding.UTF8.GetString( build( Open() ) );

		IEnumerable<CacheFile> Open()
		{
			foreach ( var mapPath in multiplayerMaps )
			{
				using var cache = CacheFile.Open( mapPath, Host.Bitmaps );
				yield return cache;
			}
		}
	}
}
