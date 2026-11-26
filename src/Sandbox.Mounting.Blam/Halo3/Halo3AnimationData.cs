using System.Text;
using Blam.Halo3;
using Blam.Halo3.Tags;

namespace Sandbox.Mounting.Blam.Halo3;

// Every model tag's animations as JSON text resources with their decoded frames (AnimationDataExport), as Halo2AnimationData: mount://halo3/anims/<hlmt path>.json and mount://halo3/anims/<hlmt path>/<animation name, ':' as '/'>.json.
// Only names are registered when mounting; an animation decodes when its document loads. A model tag's documents come from the copy its .vmdl is built from (Halo3Models' order: live geometry, most variants, map order).
sealed class Halo3AnimationData
{
	readonly Dictionary<string, (string MapPath, uint Id, bool Live, int Variants, string[] Animations)> _models = new( StringComparer.OrdinalIgnoreCase );
	readonly Dictionary<(string MapPath, uint Id), ModelAnimationSet> _sets = new();

	// Notes a map's model tags with animation graphs and their animations' names (tag data only).
	public void Collect( CacheFile cache )
	{
		foreach ( var tag in cache.TagsOf( "hlmt" ) )
		{
			if ( string.IsNullOrEmpty( tag.Path ) || tag.Address == 0 )
				continue;

			var hlmt = new ModelTag( tag );
			if ( hlmt.RenderModel is not { } renderModel || hlmt.Animation is null )
				continue;

			var path = Halo3Mount.ResourcePath( tag.Path );
			var live = cache.Resources.Get( renderModel.Data.U32( RenderGeometry.RenderModelOffset + 0x7C ) ) is not null;
			if ( _models.TryGetValue( path, out var found ) && (found.Live && !live || found.Live == live && hlmt.VariantCount <= found.Variants) )
				continue;

			try
			{
				var set = ModelAnimationSet.For( new RenderModelTag( renderModel ), hlmt );
				if ( set is null || set.Animations.Count == 0 )
					continue;

				_models[path] = (cache.FilePath, tag.Id, live, hlmt.VariantCount, AnimationDataExport.AnimationResources( tag, set ));
			}
			catch ( Exception )
			{
				// A graph that doesn't read gets no documents.
			}
		}
	}

	public void Register( MountContext context )
	{
		foreach ( var (path, (mapPath, id, _, _, animations)) in _models )
		{
			context.Add( ResourceType.Text, $"anims/{path}.json", new GraphJsonLoader( this, mapPath, id ) );
			for ( var i = 0; i < animations.Length; i++ )
				context.Add( ResourceType.Text, animations[i], new AnimationJsonLoader( this, mapPath, id, i ) );
		}
	}

	// A model tag and its animations, read once per tag.
	(TagEntry Tag, ModelAnimationSet Set) Get( CacheFile cache, string mapPath, uint id )
	{
		var tag = cache.GetTag( id );
		lock ( _sets )
		{
			if ( !_sets.TryGetValue( (mapPath, id), out var set ) )
			{
				var hlmt = new ModelTag( tag );
				_sets[(mapPath, id)] = set = hlmt.RenderModel is { } renderModel ? ModelAnimationSet.For( new RenderModelTag( renderModel ), hlmt ) : null;
			}

			return (tag, set);
		}
	}

	sealed class GraphJsonLoader( Halo3AnimationData resources, string mapPath, uint id ) : Halo3Loader
	{
		protected override object Load()
		{
			return Host.Read( () =>
			{
				var (tag, set) = resources.Get( Host.GetCache( mapPath ), mapPath, id );
				return set is null ? null : Encoding.UTF8.GetString( AnimationDataExport.Graph( tag, set ) );
			} );
		}
	}

	sealed class AnimationJsonLoader( Halo3AnimationData resources, string mapPath, uint id, int index ) : Halo3Loader
	{
		protected override object Load()
		{
			try
			{
				return Host.Read( () =>
				{
					var (tag, set) = resources.Get( Host.GetCache( mapPath ), mapPath, id );
					if ( set is null || index >= set.Animations.Count )
						return null;
					lock ( set )
						return Encoding.UTF8.GetString( AnimationDataExport.Animation( tag, set, index, Halo3Mount.WorldScale ) );
				} );
			}
			catch ( Exception e )
			{
				Host.Logger.Warning( $"{Path}: {e.Message}" );
				return null;
			}
		}
	}
}
