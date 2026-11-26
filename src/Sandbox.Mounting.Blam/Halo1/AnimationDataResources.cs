using System.Text;
using Blam.Halo1;
using Blam.Halo1.TagData;
using Blam.Halo1.Tags;

namespace Sandbox.Mounting.Blam.Halo1;

/// <summary>
/// Every animation graph's animations as JSON text resources with their decoded frames, for games
/// that pose bones themselves (see <see cref="AnimationExport"/>):
/// <list type="bullet">
/// <item><c>mount://haloce/anims/&lt;graph tag path&gt;/&lt;animation name&gt;.json</c>: one
/// animation, e.g. <c>anims/weapons/assault rifle/fp/fp/first-person overlays.json</c>.</item>
/// <item><c>mount://haloce/anims/&lt;graph tag path&gt;.json</c>: the graph's bones, sounds, first
/// person slots and animations without frames.</item>
/// </list>
/// </summary>
/// <remarks>
/// Read like the tag data (see <see cref="TagDataResources"/>):
/// <code>
/// var loader = Sandbox.Mounting.Directory.Get( "haloce" )?.GetByPath( "mount://haloce/anims/weapons/pistol/fp/fp/first-person moving.json" );
/// var json = loader is null ? null : await loader.GetOrCreate() as string;
/// </code>
/// Only the names are registered when mounting; an animation is decoded when its document is
/// loaded. A graph comes from the first map that has it, the map the mount builds its models from,
/// so the frames are exactly those of the models' animations.
/// </remarks>
sealed class AnimationDataResources
{
	readonly Dictionary<string, (string MapPath, uint TagId, string[] Animations)> _graphs = new( StringComparer.OrdinalIgnoreCase );
	readonly Dictionary<(string MapPath, uint TagId), AnimationSkeleton> _skeletons = new();

	/// <summary>
	/// Notes the animation graphs of a map and their animations' names.
	/// </summary>
	public void Collect( CacheFile cache )
	{
		foreach ( var tag in cache.Tags )
		{
			if ( tag.Class != TagClasses.ModelAnimations || string.IsNullOrEmpty( tag.Path ) )
				continue;

			var path = AnimationExport.GraphResource( tag );
			if ( !_graphs.ContainsKey( path ) )
				_graphs[path] = (cache.FilePath, tag.Id, AnimationExport.AnimationResources( new ModelAnimationsTag( cache, tag ) ));
		}
	}

	public void Register( MountContext context )
	{
		foreach ( var (path, (mapPath, tagId, animations)) in _graphs )
		{
			context.Add( ResourceType.Text, path, new GraphJsonLoader( this, mapPath, tagId ) );
			for ( var i = 0; i < animations.Length; i++ )
				context.Add( ResourceType.Text, animations[i], new AnimationJsonLoader( this, mapPath, tagId, i ) );
		}
	}

	/// <summary>
	/// The skeleton a graph is written for, found once per graph.
	/// </summary>
	AnimationSkeleton Skeleton( CacheFile cache, string mapPath, uint tagId )
	{
		lock ( _skeletons )
		{
			if ( !_skeletons.TryGetValue( (mapPath, tagId), out var skeleton ) )
				_skeletons[(mapPath, tagId)] = skeleton = AnimationSkeleton.Find( cache, new ModelAnimationsTag( cache, cache.GetTag( tagId ) ) );

			return skeleton;
		}
	}

	sealed class GraphJsonLoader( AnimationDataResources resources, string mapPath, uint tagId ) : HaloCeLoader
	{
		protected override object Load()
		{
			var cache = Host.GetCache( mapPath );
			return Encoding.UTF8.GetString( AnimationExport.Graph( resources.Skeleton( cache, mapPath, tagId ) ) );
		}
	}

	sealed class AnimationJsonLoader( AnimationDataResources resources, string mapPath, uint tagId, int index ) : HaloCeLoader
	{
		protected override object Load()
		{
			var cache = Host.GetCache( mapPath );
			var skeleton = resources.Skeleton( cache, mapPath, tagId );
			try
			{
				return Encoding.UTF8.GetString( AnimationExport.Animation( skeleton, index, HaloCeMount.WorldScale ) );
			}
			catch ( InvalidOperationException e )
			{
				Host.Logger.Warning( $"{Path}: {e.Message}" );
				return null;
			}
		}
	}
}
