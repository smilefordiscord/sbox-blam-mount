using System.Text;
using Blam.Halo2;
using Blam.Halo2.Tags;

namespace Sandbox.Mounting.Blam.Halo2;

/// <summary>
/// Every model tag's animations as JSON text resources with their decoded frames, for games that pose bones themselves
/// (see <see cref="AnimationDataExport"/>), like the Halo CE mount's animation data:
/// <list type="bullet">
/// <item><c>mount://halo2/anims/&lt;hlmt path&gt;.json</c>: bones, labels, blend screens and the animations without
/// frames.</item>
/// <item><c>mount://halo2/anims/&lt;hlmt path&gt;/&lt;animation name, ':' as '/'&gt;.json</c>: one animation, e.g.
/// <c>anims/objects/characters/masterchief/masterchief_mp/combat/rifle/idle.json</c>.</item>
/// </list>
/// </summary>
/// <remarks>
/// <code>
/// var loader = Sandbox.Mounting.Directory.Get( "halo2" )?.GetByPath( "mount://halo2/anims/objects/characters/masterchief/masterchief_mp.json" );
/// var json = loader is null ? null : await loader.GetOrCreate() as string;
/// </code>
/// Only the names are registered when mounting; an animation is decoded when its document is loaded. A model tag's
/// documents come from the copy its .vmdl is built from (the one with the most variants, the first of those), so the
/// frames are exactly the model's.
/// </remarks>
sealed class Halo2AnimationData
{
	readonly Dictionary<string, (string MapPath, uint Id, int Variants, string[] Animations)> _models = new( StringComparer.OrdinalIgnoreCase );
	readonly Dictionary<(string MapPath, uint Id), ModelAnimationSet> _sets = new();

	/// <summary>
	/// Notes a map's model tags with animation graphs and their animations' names.
	/// </summary>
	public void Collect( CacheFile cache )
	{
		foreach ( var tag in cache.Tags )
		{
			if ( tag.Class != ModelTag.Class || string.IsNullOrEmpty( tag.Path ) || tag.Cache != cache || tag.Address == 0 )
				continue;

			var path = Halo2Mount.ResourcePath( tag.Path );
			var variants = ModelTag.VariantCount( tag );
			if ( _models.TryGetValue( path, out var found ) && variants <= found.Variants )
				continue;

			try
			{
				var set = Set( new ModelTag( tag ) );
				if ( set is null || set.Animations.Count == 0 )
					continue;

				_models[path] = (cache.FilePath, tag.Id, variants, AnimationDataExport.AnimationResources( tag, set ));
			}
			catch ( Exception )
			{
				// A graph that doesn't read gets no documents.
			}
		}
	}

	public void Register( MountContext context )
	{
		foreach ( var (path, (mapPath, id, _, animations)) in _models )
		{
			context.Add( ResourceType.Text, $"anims/{path}.json", new GraphJsonLoader( this, mapPath, id ) );
			for ( var i = 0; i < animations.Length; i++ )
				context.Add( ResourceType.Text, animations[i], new AnimationJsonLoader( this, mapPath, id, i ) );
		}
	}

	static ModelAnimationSet Set( ModelTag hlmt ) =>
		hlmt.Animations is { Address: not 0 } graph && graph.Class == AnimationGraphTag.Class
		&& hlmt.RenderModel is { Address: not 0 } renderModel && renderModel.Class == RenderModelTag.Class
			? ModelAnimationSet.For( new RenderModelTag( renderModel ), hlmt )
			: null;

	/// <summary>
	/// A model tag and its animations, read once per tag.
	/// </summary>
	(TagEntry Tag, ModelAnimationSet Set) Get( CacheFile cache, string mapPath, uint id )
	{
		var tag = cache.GetTag( id );
		lock ( _sets )
		{
			if ( !_sets.TryGetValue( (mapPath, id), out var set ) )
				_sets[(mapPath, id)] = set = Set( new ModelTag( tag ) );

			return (tag, set);
		}
	}

	sealed class GraphJsonLoader( Halo2AnimationData resources, string mapPath, uint id ) : Halo2Loader
	{
		protected override object Load()
		{
			var (tag, set) = resources.Get( Host.GetCache( mapPath ), mapPath, id );
			return set is null ? null : Encoding.UTF8.GetString( AnimationDataExport.Graph( tag, set ) );
		}
	}

	sealed class AnimationJsonLoader( Halo2AnimationData resources, string mapPath, uint id, int index ) : Halo2Loader
	{
		protected override object Load()
		{
			var (tag, set) = resources.Get( Host.GetCache( mapPath ), mapPath, id );
			if ( set is null || index >= set.Animations.Count )
				return null;

			try
			{
				lock ( set )
					return Encoding.UTF8.GetString( AnimationDataExport.Animation( tag, set, index, Halo2Mount.WorldScale ) );
			}
			catch ( Exception e )
			{
				Host.Logger.Warning( $"{Path}: {e.Message}" );
				return null;
			}
		}
	}
}
