using System.Text;
using Blam.Halo2;
using Blam.Halo2.Tags;

namespace Sandbox.Mounting.Blam.Halo2;

// A weapon's first person view model for the multiplayer Spartan (FirstPersonWeapon): the hands and the weapon's first person model on one skeleton.
// The bones are the first person graph's nodes, its animations the sequences, named as Halo names them ("first_person:idle", "first_person:dual:fire_1:var0").
// The origin is the camera, x forward and z up, as Halo poses the graph: parent it to the camera with no offset.
// "camera_control" is a bone of its own that some animations move (melee, reloads); it rests at the origin.
// Overlays and replacements are baked over the first frame of their weapon class's idle, so each plays on its own.
// Bones are bound in each model's own default pose, the way the vertices are stored.
static class FirstPersonModel
{
	public static Model Build( string name, FirstPersonSkeleton skeleton, float scale, bool camouflaged )
	{
		var builder = Model.Builder.WithName( name );
		var parts = new ModelParts( builder, System.IO.Path.ChangeExtension( name, null ), ModelLighting.Object, named: false, camouflaged, grouped: true )
		{
			Functions = FirstPersonViews.Functions( skeleton ),
		};
		var vertices = new List<Halo2SkinnedVertex>();
		AddModel( parts, skeleton.HandsModel, skeleton.HandsBones, scale, vertices, arms: true );
		AddModel( parts, skeleton.WeaponModel, skeleton.WeaponBones, scale, vertices, arms: false );
		if ( parts.Count == 0 )
			return null;

		for ( var lod = 1; lod < 8; lod++ )
			builder.WithLodDistance( lod, RenderModelBuilder.NeverSwitchDistance );

		var names = global::Blam.Halo1.TagData.AnimationSkeleton.UniqueNames( skeleton.Nodes.Select( n => n.Name ) );
		var bind = skeleton.Bind.Select( b => new Transform( (Vector3)b.Translation * scale, new Rotation( b.Rotation.X, b.Rotation.Y, b.Rotation.Z, b.Rotation.W ) ) ).ToArray();
		RenderModelBuilder.AddBones( builder, skeleton.Nodes, names, bind, vertices );

		// The weapon's markers first ("primary_trigger", "left_hand" where the left hand holds it), then the hands' under names left.
		var taken = new HashSet<string>( names, StringComparer.OrdinalIgnoreCase );
		RenderModelBuilder.AddMarkers( builder, skeleton.WeaponModel, skeleton.WeaponBones.Select( b => names[b] ).ToArray(), skeleton.WeaponBones.Select( b => bind[b] ).ToArray(), scale, taken );
		RenderModelBuilder.AddMarkers( builder, skeleton.HandsModel, skeleton.HandsBones.Select( b => names[b] ).ToArray(), skeleton.HandsBones.Select( b => bind[b] ).ToArray(), scale, taken );

		var set = skeleton.Animations();
		Halo2ModelAnimations.Add( builder, set, scale, entry => skeleton.BasePose( set, entry ) );
		return builder.Create();
	}

	// A model's regions at their first permutation, skinned to the skeleton's bones.
	// The hands' regions ("left" and "right", an arm each) are body groups that can show "none": the right arm alone is what a dual wielded weapon's own hand needs.
	static void AddModel( ModelParts parts, RenderModelTag model, IReadOnlyList<int> bones, float scale, List<Halo2SkinnedVertex> all, bool arms )
	{
		var materials = new Dictionary<int, Material>();
		foreach ( var region in model.Regions.Where( r => r.Permutations.Count > 0 ) )
		{
			var group = arms ? new RegionChoices( region.Index, region.Name, [0, -1] ) : null;
			parts.AddRegion( model, materials, region, group?.Permutations ?? [0], group, skinned: true,
				( permutation, section ) => OnBones( RenderModelBuilder.ReadVertices( section, model, scale, permutation.HighestSection, permutation.HighestLevel, default ), bones ),
				drawn: ( choice, vertices, _, drawn ) =>
				{
					if ( choice == 0 && drawn )
						all.AddRange( vertices );
				} );
		}
	}

	// Vertices weighted to a model's nodes, weighted to their bones instead.
	static List<Halo2SkinnedVertex> OnBones( List<Halo2SkinnedVertex> vertices, IReadOnlyList<int> bones )
	{
		for ( var i = 0; i < vertices.Count; i++ )
		{
			var v = vertices[i];
			v.BlendIndices = new Color32( (byte)bones[v.BlendIndices.r], (byte)bones[v.BlendIndices.g], (byte)bones[v.BlendIndices.b], (byte)bones[v.BlendIndices.a] );
			vertices[i] = v;
		}

		return vertices;
	}
}

// Every multiplayer weapon's first person view, noted when mounting and built when loaded:
// mount://halo2/<weapon path>_fp.vmdl (FirstPersonModel), _fp_camo.vmdl camouflaged (as CamoModelLoader's models),
// and its animation data (AnimationDataExport): anims/<weapon path>_fp.json and anims/<weapon path>_fp/<animation name, ':' as '/'>.json.
sealed class FirstPersonViews
{
	readonly Dictionary<string, (string MapPath, uint Id, string[] Animations)> _weapons = new( StringComparer.OrdinalIgnoreCase );
	readonly Dictionary<(string MapPath, uint Id), (FirstPersonSkeleton Skeleton, ModelAnimationSet Set)> _sets = new();
	readonly Dictionary<string, IReadOnlyList<string>> _functions = new( StringComparer.OrdinalIgnoreCase );

	// The function slots of a weapon's first person view: those its hands' and its first person model's shaders follow.
	public static IReadOnlyList<string> Functions( FirstPersonSkeleton skeleton ) =>
		ShaderFunctions.Slots( skeleton.HandsModel.Materials.Concat( skeleton.WeaponModel.Materials ) );

	// Notes a map's weapons with a first person view (the first map to have a weapon provides it).
	public void Collect( CacheFile cache )
	{
		foreach ( var tag in cache.Tags )
		{
			if ( !FirstPersonWeapon.IsWeapon( tag ) || string.IsNullOrEmpty( tag.Path ) || tag.Cache != cache )
				continue;

			var name = AnimationDataExport.FirstPersonName( tag );
			if ( _weapons.ContainsKey( name ) || FirstPersonWeapon.Find( tag ) is not { } weapon || FirstPersonSkeleton.Create( weapon ) is not { } skeleton )
				continue;

			_weapons[name] = (cache.FilePath, tag.Id, AnimationDataExport.FirstPersonAnimationResources( skeleton, skeleton.Animations() ));
			if ( Functions( skeleton ) is { Count: > 0 } functions )
				_functions[name] = functions;
		}
	}

	public void Register( MountContext context )
	{
		foreach ( var (name, (mapPath, id, animations)) in _weapons )
		{
			context.Add( ResourceType.Model, name, new FirstPersonModelLoader( mapPath, id, camouflaged: false ) );
			if ( _functions.TryGetValue( name, out var functions ) )
				DrivenParameters.RegisterSlots( context, name, functions );
			context.Add( ResourceType.Model, CamoModelLoader.ResourceName( name ), new FirstPersonModelLoader( mapPath, id, camouflaged: true ) );
			context.Add( ResourceType.Text, $"anims/{name}.json", new GraphJsonLoader( this, mapPath, id ) );
			for ( var i = 0; i < animations.Length; i++ )
				context.Add( ResourceType.Text, animations[i], new AnimationJsonLoader( this, mapPath, id, i ) );
		}
	}

	// A weapon's skeleton and animations for its documents, read once.
	(FirstPersonSkeleton Skeleton, ModelAnimationSet Set) Get( string mapPath, uint id )
	{
		lock ( _sets )
		{
			if ( !_sets.TryGetValue( (mapPath, id), out var found ) )
			{
				var skeleton = Skeleton( mapPath, id );
				_sets[(mapPath, id)] = found = (skeleton, skeleton?.Animations());
			}

			return found;
		}
	}

	public static FirstPersonSkeleton Skeleton( string mapPath, uint id ) =>
		FirstPersonWeapon.Find( Halo2Mount.Current.GetCache( mapPath ).GetTag( id ) ) is { } weapon ? FirstPersonSkeleton.Create( weapon ) : null;

	sealed class GraphJsonLoader( FirstPersonViews views, string mapPath, uint id ) : Halo2Loader
	{
		protected override object Load()
		{
			var (skeleton, set) = views.Get( mapPath, id );
			if ( skeleton is null )
				return null;

			lock ( set )
				return Encoding.UTF8.GetString( AnimationDataExport.FirstPersonGraph( skeleton, set ) );
		}
	}

	sealed class AnimationJsonLoader( FirstPersonViews views, string mapPath, uint id, int index ) : Halo2Loader
	{
		protected override object Load()
		{
			var (skeleton, set) = views.Get( mapPath, id );
			if ( skeleton is null || index >= set.Animations.Count )
				return null;

			try
			{
				lock ( set )
					return Encoding.UTF8.GetString( AnimationDataExport.FirstPersonAnimation( skeleton, set, index, Halo2Mount.WorldScale ) );
			}
			catch ( Exception e )
			{
				Host.Logger.Warning( $"{Path}: {e.Message}" );
				return null;
			}
		}
	}
}

// Loads a weapon's first person view model, on the main thread like the other models.
sealed class FirstPersonModelLoader( string mapPath, uint weaponId, bool camouflaged ) : Halo2Loader
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
			var skeleton = FirstPersonViews.Skeleton( mapPath, weaponId );
			return skeleton is null ? null : FirstPersonModel.Build( Path, skeleton, Halo2Mount.WorldScale, camouflaged );
		}
		catch ( Exception e )
		{
			Host.Logger.Warning( e, $"Couldn't build {Path}: {e.Message}" );
			return null;
		}
	}
}
