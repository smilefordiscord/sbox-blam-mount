using Blam.Halo2;
using Blam.Halo2.Tags;

namespace Sandbox.Mounting.Blam.Halo2;

// A player representation's first person body (FirstPersonBody) as a model on its third person unit's skeleton: the unit's bones in its order, under its names.
// Halo poses it with the unit's own node matrices, so a game bone merges it onto the unit's model (or copies its bones) and it follows the third person animations.
// Every region shows its first permutation, as Halo draws it; materials are the unit's kind, with its variants' change colours as material groups.
// Bones are bound in the body's own default pose, the way its vertices are stored.
static class FirstPersonBodyModel
{
	public static Model Build( string name, FirstPersonBody body, float scale, bool camouflaged )
	{
		var builder = Model.Builder.WithName( name );
		var model = body.Body;
		var materialBase = System.IO.Path.ChangeExtension( name, null );
		var materials = new Dictionary<int, Material>();
		var parts = new ModelParts( builder, materialBase, ModelLighting.Object, named: true, camouflaged, grouped: false )
		{
			Functions = ShaderFunctions.Slots( model.Materials ),
		};

		var vertices = new List<Halo2SkinnedVertex>();
		foreach ( var region in model.Regions.Where( r => r.Permutations.Count > 0 ) )
		{
			parts.AddRegion( model, materials, region, [0], null, skinned: true,
				( permutation, section ) => RenderModelBuilder.ReadVertices( section, model, scale, permutation.HighestSection, permutation.HighestLevel, default ),
				drawn: ( _, drawnVertices, _, drawn ) =>
				{
					if ( drawn )
						vertices.AddRange( drawnVertices );
				} );
		}

		if ( parts.Count == 0 )
			return null;

		RenderModelBuilder.AddVariantColors( builder, model, body.UnitModel, materials, parts.Used, materialBase, ModelLighting.Object, camouflaged, parts.Functions );

		var bones = global::Blam.Halo1.TagData.AnimationSkeleton.UniqueNames( body.Skeleton.Nodes.Select( n => n.Name ) );
		var bind = RenderModelBuilder.DefaultPose( model, scale );
		RenderModelBuilder.AddBones( builder, body.Skeleton.Nodes, bones, bind, vertices );
		RenderModelBuilder.AddMarkers( builder, model, bones, bind, scale );
		return builder.Create();
	}
}

// Every player representation's first person body, noted when mounting and built when loaded: mount://halo2/<body render model path>.vmdl, and _camo.vmdl camouflaged (as CamoModelLoader's models).
// A body several representations share is built for the first of them, a multiplayer map's where there is one, as their units share a skeleton.
// Bodies Halo wouldn't draw on their unit (FirstPersonBody.Drawn) aren't registered.
sealed class FirstPersonBodies
{
	readonly Dictionary<string, (string MapPath, int Representation, bool Multiplayer, IReadOnlyList<string> Functions)> _bodies = new( StringComparer.OrdinalIgnoreCase );

	public void Collect( CacheFile cache )
	{
		var multiplayer = cache.Type is CacheType.Multiplayer or CacheType.Shared;
		foreach ( var body in FirstPersonBody.All( cache ) )
		{
			var name = ResourceName( body );
			if ( !body.Drawn || (_bodies.TryGetValue( name, out var found ) && (found.Multiplayer || !multiplayer)) )
				continue;

			_bodies[name] = (cache.FilePath, body.Representation, multiplayer, ShaderFunctions.Slots( body.Body.Materials ));
		}
	}

	public void Register( MountContext context )
	{
		foreach ( var (name, (mapPath, representation, _, functions)) in _bodies )
		{
			if ( functions.Count > 0 )
				DrivenParameters.RegisterSlots( context, name, functions );
			context.Add( ResourceType.Model, name, new FirstPersonBodyLoader( mapPath, representation, camouflaged: false ) );
			context.Add( ResourceType.Model, CamoModelLoader.ResourceName( name ), new FirstPersonBodyLoader( mapPath, representation, camouflaged: true ) );
		}
	}

	static string ResourceName( FirstPersonBody body ) => Halo2Mount.ResourcePath( body.Body.Tag.Path );
}

// Loads a first person body model, on the main thread like the other models.
sealed class FirstPersonBodyLoader( string mapPath, int representation, bool camouflaged ) : Halo2Loader
{
	protected override Task<object> LoadAsync()
	{
		if ( ThreadSafe.IsMainThread )
			return Task.FromResult( Load() );

		return LoadOnMainThread();
	}

	async Task<object> LoadOnMainThread()
	{
		await GameTask.MainThread();
		return Load();
	}

	protected override object Load()
	{
		try
		{
			var body = FirstPersonBody.All( Host.GetCache( mapPath ) ).FirstOrDefault( b => b.Representation == representation );
			return body is null ? null : FirstPersonBodyModel.Build( Path, body, Halo2Mount.WorldScale, camouflaged );
		}
		catch ( Exception e )
		{
			Host.Logger.Warning( e, $"Couldn't build {Path}: {e.Message}" );
			return null;
		}
	}
}
