using System.Text;
using Blam.Halo2;
using Blam.Halo2.TagData;

namespace Sandbox.Mounting.Blam.Halo2;

// Halo 2 tag data as JSON text resources (game.json, hud.json, maps/<map>.json, tags/<tag path>.<class>.json; see TagExport).
// A tag comes from shared.map or a multiplayer map when one has it, since campaign maps hold copies that can differ.
// Documents are built when first loaded; registering them only notes names.
sealed class TagDataResources
{
	readonly Dictionary<string, (string MapPath, uint TagId, bool Multiplayer)> _tags = new( StringComparer.OrdinalIgnoreCase );
	readonly List<(string Name, string MapPath)> _maps = new();
	readonly List<string> _multiplayerMaps = new();

	// Notes the exportable tags of a map (shared.map's included), while the mount goes through them.
	public void Collect( CacheFile cache )
	{
		var multiplayer = cache.Type is CacheType.Multiplayer or CacheType.Shared;
		if ( cache.Type is CacheType.Multiplayer or CacheType.Campaign )
			_maps.Add( (cache.Name, cache.FilePath) );
		if ( cache.Type == CacheType.Multiplayer )
			_multiplayerMaps.Add( cache.FilePath );

		foreach ( var tag in cache.Tags )
		{
			if ( tag.Cache != cache || tag.Address == 0 || !TagExport.CanExport( tag ) )
				continue;

			var path = TagExport.TagPath( tag );
			if ( !_tags.TryGetValue( path, out var found ) || (multiplayer && !found.Multiplayer) )
				_tags[path] = (cache.FilePath, tag.Id, multiplayer);
		}
	}

	public void Register( MountContext context, string sharedPath, IReadOnlyList<string> fontTable )
	{
		context.Add( ResourceType.Text, "game.json", new GameJsonLoader( sharedPath, _multiplayerMaps.ToArray() ) );
		context.Add( ResourceType.Text, "hud.json", new HudJsonLoader( sharedPath, _multiplayerMaps.ToArray(), fontTable ) );

		foreach ( var (name, mapPath) in _maps )
			context.Add( ResourceType.Text, $"maps/{name}.json", new MapJsonLoader( mapPath ) );

		foreach ( var (path, (mapPath, tagId, _)) in _tags )
			context.Add( ResourceType.Text, path, new TagJsonLoader( mapPath, tagId ) );
	}

	sealed class TagJsonLoader( string mapPath, uint tagId ) : Halo2Loader
	{
		protected override object Load() => Encoding.UTF8.GetString( TagExport.Tag( Host.GetCache( mapPath ).GetTag( tagId ) ) );
	}

	sealed class MapJsonLoader( string mapPath ) : Halo2Loader
	{
		protected override object Load() => Encoding.UTF8.GetString( TagExport.Map( Host.GetCache( mapPath ) ) );
	}

	sealed class GameJsonLoader( string sharedPath, string[] multiplayerMaps ) : Halo2Loader
	{
		protected override object Load()
		{
			var shared = Host.GetCache( sharedPath );
			return Encoding.UTF8.GetString( TagExport.Game( shared, OpenEach( shared, multiplayerMaps ), Host.Textures ) );
		}
	}

	sealed class HudJsonLoader( string sharedPath, string[] multiplayerMaps, IReadOnlyList<string> fontTable ) : Halo2Loader
	{
		protected override object Load()
		{
			var shared = Host.GetCache( sharedPath );
			return Encoding.UTF8.GetString( TagExport.Hud( shared, OpenEach( shared, multiplayerMaps ), fontTable ) );
		}
	}

	// Opens the multiplayer maps one at a time rather than through the mount's open caches, so they don't stay in memory.
	// Each map is disposed as the enumeration moves on, so nothing read from it may be kept past its iteration.
	static IEnumerable<CacheFile> OpenEach( CacheFile shared, string[] multiplayerMaps )
	{
		foreach ( var mapPath in multiplayerMaps )
		{
			using var cache = CacheFile.Open( mapPath, shared );
			yield return cache;
		}
	}
}
