using System.Reflection;
using Blam.Halo1;
using Blam.Halo1.Tags;
using Blam.Halo1.TagData;

namespace Sandbox.Mounting.Blam.Halo1;

/// <summary>
/// Turns a Halo CE gbxmodel into an s&amp;box model: every permutation of every region at each level
/// of detail (see <see cref="Meshes"/>), with its markers as attachments (see <see cref="ModelMarkers"/>), and
/// the physics shapes and hitboxes of its objects' collision model (see <see cref="ModelCollision"/>).
/// Models with more than one node get a skeleton, skinning and their animations (see <see cref="GbxSkeleton"/>).
/// </summary>
static class GbxModelBuilder
{
	/// <summary>
	/// Which parts of a model to build. Glass parts can be built as models of their own, drawn like
	/// Halo's glass (see <see cref="ObjectGlass"/>).
	/// </summary>
	public enum Parts
	{
		/// <summary>Everything, glass drawn over the frame buffer copy.</summary>
		All,
		/// <summary>Everything but the glass. The glass is still in the trace mesh.</summary>
		AllButGlass,
		/// <summary>Only the glass, multiplying what's behind it by its tint.</summary>
		GlassTint,
		/// <summary>Only the glass, adding its reflection and blending its diffuse map over that.</summary>
		GlassReflection,
		/// <summary>Only the depth of the parts a self-occluding object hides itself with (see <see cref="ObjectSelfOcclusion"/>).</summary>
		DepthPrepass,
	}

	/// <param name="scale">s&amp;box units per world unit.</param>
	/// <param name="collision">Whether to give the model collision: physics and hitboxes from the
	/// collision model of the objects using it, and a trace mesh of its solid parts.</param>
	/// <param name="animated">Whether to give the model its skeleton and animations.</param>
	/// <param name="region">Only build this region (index into the model's regions).</param>
	/// <param name="meshBounds">Give every mesh these bounds instead of its own.</param>
	/// <param name="parts">Which parts to build. The glass models (<see cref="Parts.GlassTint"/>,
	/// <see cref="Parts.GlassReflection"/>) get no collision or animations (they're bone merged onto
	/// the rest), and bounds around the whole model 1 and 2 units bigger than it, so they sort
	/// nearer than the rest of the model, in that order. The depth prepass model (<see cref="Parts.DepthPrepass"/>)
	/// is bone merged the same way, with the whole model's bounds; the model of an object whose
	/// transparency self-occludes gets bounds 1 unit bigger than that, so it sorts nearer and draws after it.</param>
	/// <param name="aimPoses">Also bake the unit's aiming into sequences (the <c>_aim</c> variant, see <see cref="AimPoses"/>).</param>
	/// <param name="camouflaged">The variant for a camouflaged object (the <c>_camo</c> models, see <see cref="Meshes.Camouflaged"/>).</param>
	/// <param name="forcedShaderPermutation">The variant for an object that forces a shader permutation (the <c>_perm</c> models, see <see cref="Meshes.ForcedShaderPermutation"/>).</param>
	public static Model Build( HaloCeMount host, string name, CacheFile cache, GbxModelTag model, float scale = HaloCeMount.WorldScale, bool collision = true, bool animated = false, int? region = null, BBox? meshBounds = null, Parts parts = Parts.All, bool aimPoses = false, bool camouflaged = false, int forcedShaderPermutation = 0 )
	{
		var builder = Model.Builder.WithName( name );
		var skinned = animated && AnimationSkeleton.HasSkeleton( cache, model );
		var selfOccluding = parts is Parts.All or Parts.AllButGlass && ObjectTags.ModelSelfOccludes( cache, model.Tag );
		var levels = scale == HaloCeMount.WorldScale;
		var meshes = new Meshes( host, name, cache, builder, scale, collision, meshBounds, parts ) { Camouflaged = camouflaged, ForcedShaderPermutation = forcedShaderPermutation, SelfOccluding = selfOccluding, AllLevels = levels };
		meshes.Add( model, region, skinned ? Enumerable.Range( 0, model.Nodes.Count ).ToArray() : null );

		if ( meshes.Count == 0 )
			return null;

		// Objects collide with their collision model, like Halo's; objects without one don't
		// collide at all (flag bases, shrubs), so the render geometry is only a trace mesh.
		var collisionTag = collision && !meshes.DrawOnly ? ObjectTags.FindCollisionModel( cache, model.Tag ) : null;
		meshes.Finish( collisionMesh: false );

		// The CTF flag's cloth, in a body group per team.
		if ( parts == Parts.All && region is null && !skinned )
			FlagCloth.AddTo( host, name, cache, model, builder, scale );

		var bones = skinned ? GbxSkeleton.BoneNames( model.Nodes.Select( n => n.Name ) ) : null;
		if ( skinned )
		{
			var graphs = meshes.DrawOnly
				? new List<ModelAnimationsTag>()
				: GbxSkeleton.FindAnimations( cache, model.Tag ).Select( g => new ModelAnimationsTag( cache, g ) ).ToList();
			GbxSkeleton.Build( builder, model, meshes.DrawOnly ? meshes.AllSkinnedVertices : meshes.SkinnedVertices, graphs, scale, host.Logger, meshes.BoundsPadding, aimPoses );
		}

		// The glass and depth prepass models are bone merged onto the rest, which has the markers and collision.
		if ( !meshes.DrawOnly )
		{
			var pose = GbxSkeleton.DefaultPose( model, scale );
			ModelMarkers.Add( builder, model, bones, pose, scale, new HashSet<string>( bones ?? [], StringComparer.OrdinalIgnoreCase ) );

			// Characters' bodies make a ragdoll (bipeds' models, and their _aim and _camo variants),
			// of capsules alone for the few without a collision model (cutscene pilots).
			var character = collision && skinned && RagdollPlan.IsBipedModel( cache, model.Tag );
			if ( collisionTag is not null || character )
			{
				try
				{
					var collisionModel = collisionTag is not null ? new CollisionModelTag( cache, collisionTag ) : null;
					var ragdoll = character ? RagdollPlan.Build( cache, model, collisionModel, scale * 0.0254f ) : null;
					if ( collisionModel is not null || ragdoll is not null )
						ModelCollision.Add( builder, model, collisionModel, bones, pose, scale, ragdoll );
				}
				catch ( Exception e )
				{
					host.Logger.Warning( $"{name}: couldn't build the collision of {collisionTag?.ToString() ?? "its ragdoll"}: {e.Message}" );
				}
			}
		}

		var built = builder.Create();

		// Which object it belongs to, for lighting it where a game puts it (see MovingObjectLights),
		// and for picking its level of detail (see ModelDetailLevels).
		if ( levels )
		{
			var shape = ObjectLightingShape.ForModel( cache, model.Tag );
			MovingObjectLights.Register( built, name, shape );
			ModelDetailLevels.Register( built, name, model, shape );
		}

		return built;
	}

	// A collision-only model for an object showing other region permutations than the first, with the model's skeleton so it stands in for the model's own physics.
	public static Model BuildCollision( string name, CacheFile cache, GbxModelTag model, CollisionModelTag collision, IReadOnlyList<int> regionPermutations, float scale = HaloCeMount.WorldScale )
	{
		var builder = Model.Builder.WithName( name );
		var pose = GbxSkeleton.DefaultPose( model, scale );
		var bones = AnimationSkeleton.HasSkeleton( cache, model ) ? GbxSkeleton.BoneNames( model.Nodes.Select( n => n.Name ) ) : null;
		if ( bones is not null )
			GbxSkeleton.AddBones( builder, bones, model.Nodes.Select( n => n.ParentIndex ).ToArray(), pose, [] );

		return ModelCollision.Add( builder, model, collision, bones, pose, scale, regionPermutations: regionPermutations ) > 0 ? builder.Create() : null;
	}

	/// <summary>Every LOD a model can have, as a mask of one bit per LOD.</summary>
	// A biped's model refitted onto s&box's human skeleton as a player model (see PlayerBody), or null when its skeleton doesn't fit.
	public static Model BuildPlayer( HaloCeMount host, string name, CacheFile cache, GbxModelTag model, float scale = HaloCeMount.WorldScale )
	{
		var pose = GbxSkeleton.DefaultPose( model, scale );
		var body = PlayerBody.Create( model.Nodes.Select( n => n.Name ).ToArray(), model.Nodes.Select( n => n.ParentIndex ).ToArray(), pose, host.Logger );
		if ( body is null )
			return null;

		var builder = Model.Builder.WithName( name );
		var meshes = new Meshes( host, name, cache, builder, scale, false, null, Parts.All ) { Refit = body, AllLevels = true };
		meshes.Add( model, null, Enumerable.Range( 0, model.Nodes.Count ).ToArray() );
		if ( meshes.Count == 0 )
			return null;

		meshes.Finish( collisionMesh: false );
		GbxSkeleton.AddBones( builder, body.BoneNames, body.BoneParents, body.BonePose, meshes.SkinnedVertices );
		body.AddAttachments( builder );
		body.AddHitboxes( builder );
		body.AddPhysics( builder );
		return builder.Create();
	}

	public const int AllLods = 0xFF;

	static readonly Func<ModelBuilder, Mesh, int, ulong, ModelBuilder> AddMeshToLods = typeof( ModelBuilder )
		.GetMethod( "AddMesh", BindingFlags.NonPublic | BindingFlags.Instance, [typeof( Mesh ), typeof( int ), typeof( ulong )] )
		.CreateDelegate<Func<ModelBuilder, Mesh, int, ulong, ModelBuilder>>();

	static readonly Func<ModelBuilder, string, int, int> GroupChoiceIndex = typeof( ModelBuilder )
		.GetMethod( "EnsureGroupChoice", BindingFlags.NonPublic | BindingFlags.Instance, [typeof( string ), typeof( int )] )
		.CreateDelegate<Func<ModelBuilder, string, int, int>>();

	/// <summary>
	/// Adds a mesh to some of a model's LODs, for every body group or for one choice of one. The
	/// builder's own calls put a body group choice's mesh in one LOD only, and adding the same mesh
	/// once per LOD skins it to the wrong bones (the Warthog's tires followed its gun).
	/// </summary>
	/// <param name="lods">The LODs to draw it at, one bit per LOD.</param>
	public static void AddMesh( ModelBuilder builder, Mesh mesh, int lods, string group = null, int choice = 0 )
	{
		var groups = group is null ? ulong.MaxValue : 1UL << GroupChoiceIndex( builder, group, choice );
		AddMeshToLods( builder, mesh, lods, groups );
	}

	/// <summary>
	/// Adds the meshes of one or more gbxmodels to a model builder: every permutation of every region,
	/// with a material per shader (shared between parts and models), and a trace (and optionally
	/// collision) mesh from the solid parts of each region's first permutation. The parts drawn with
	/// one material at the same levels by the same permutation (or by every region with only one)
	/// are one mesh.
	/// </summary>
	internal sealed class Meshes
	{
		readonly HaloCeMount _host;
		readonly string _name;
		readonly CacheFile _cache;
		readonly ModelBuilder _builder;
		readonly float _scale;
		readonly bool _collision;
		readonly BBox? _meshBounds;
		readonly Parts _parts;

		readonly Dictionary<(uint Shader, int Permutation), Material> _materials = new();
		readonly List<Vector3> _allPositions = new();
		Material _prepassMaterial;
		readonly List<Vector3> _collisionVertices = new();
		readonly List<int> _collisionIndices = new();

		// The parts one mesh draws: those with the same material, body group choice and LODs.
		sealed class Batch
		{
			public Material Material;
			public string Group;
			public int Choice;
			public int Lods;
			public readonly List<HaloVertex> Vertices = new();
			public readonly List<HaloSkinnedVertex> Skinned = new();
			public readonly List<int> Indices = new();
		}

		readonly List<Batch> _batches = new();
		readonly Dictionary<(Material, string, int, int), Batch> _batchesByKey = new();

		/// <summary>
		/// For a camouflaged object (the <c>_camo</c> models): model shaders get their camouflaged
		/// variant (see <see cref="ModelMaterial.Create"/>), transparent shaders and meters stay as
		/// they are (they fade out with the renderer's <c>HaloCamo</c>), and glass is left out.
		/// </summary>
		public bool Camouflaged { get; init; }

		/// <summary>
		/// The shader permutation every part draws with in place of its own, or 0 for each part's
		/// own (see <see cref="ObjectTags.ForcedShaderPermutations"/>).
		/// </summary>
		public int ForcedShaderPermutation { get; init; }

		/// <summary>
		/// A weapon skin (see <see cref="WeaponSkins"/>): model shaders whose base map it has maps for
		/// draw with them.
		/// </summary>
		public WeaponSkin Skin { get; init; }

		// Refits the skinned vertices onto the human skeleton for a player model (see PlayerBody).
		public PlayerBody Refit { get; init; }

		/// <summary>The cube map the skin reflects where a shader has none.</summary>
		public TagEntry SkinCubeMap { get; init; }

		/// <summary>
		/// For an object whose transparency self-occludes (see <see cref="ObjectSelfOcclusion"/>):
		/// every mesh gets the whole model's bounds grown by 1, to draw after its depth prepass.
		/// </summary>
		public bool SelfOccluding { get; init; }

		/// <summary>
		/// Builds every level of detail, each at its model LOD (see <see cref="ModelDetailLevels.Lod"/>),
		/// for <see cref="ModelDetailLevels"/> to pick. Otherwise only the level drawn at normal sizes
		/// (<see cref="GbxModelTag.DrawnLevel"/>) is built, at every LOD.
		/// </summary>
		public bool AllLevels { get; init; }

		/// <summary>1 or 2 for the glass pass models (see <see cref="Parts"/>), 0 otherwise.</summary>
		public int GlassPass { get; }

		/// <summary>Whether it's a model that only draws, bone merged onto the object's own (the glass passes, the depth prepass).</summary>
		public bool DrawOnly => GlassPass > 0 || _parts == Parts.DepthPrepass;

		/// <summary>How much bigger than the whole model the meshes' and bones' bounds are (see <see cref="Parts"/>).</summary>
		public int BoundsPadding => GlassPass > 0 ? GlassPass : SelfOccluding ? 1 : 0;

		bool WholeBounds => DrawOnly || SelfOccluding;

		/// <summary>The number of meshes added.</summary>
		public int Count => _batches.Count;

		/// <summary>The skinned vertices of every mesh added at the level drawn at normal sizes.</summary>
		public List<HaloSkinnedVertex> SkinnedVertices { get; } = new();

		/// <summary>The skinned vertices of every part at that level, drawn or not (the glass models' bones are sized by the whole model).</summary>
		public List<HaloSkinnedVertex> AllSkinnedVertices { get; } = new();

		/// <param name="scale">s&amp;box units per world unit.</param>
		/// <param name="collision">Whether to give the model collision (never for the glass models).</param>
		/// <param name="meshBounds">Give every mesh these bounds instead of its own.</param>
		/// <param name="parts">Which parts to build.</param>
		public Meshes( HaloCeMount host, string name, CacheFile cache, ModelBuilder builder, float scale, bool collision, BBox? meshBounds, Parts parts )
		{
			_host = host;
			_name = name;
			_cache = cache;
			_builder = builder;
			_scale = scale;
			_meshBounds = meshBounds;
			_parts = parts;
			GlassPass = parts switch { Parts.GlassTint => 1, Parts.GlassReflection => 2, _ => 0 };
			_collision = collision && !DrawOnly;
		}

		/// <summary>LOD switch distance no view reaches, so a renderer nothing picks a LOD for draws LOD 0.</summary>
		const float NeverSwitchDistance = 1e7f;

		/// <param name="region">Only build this region (index into the model's regions).</param>
		/// <param name="bones">The bone each of the model's nodes is skinned to, or null for static meshes.</param>
		public void Add( GbxModelTag model, int? region = null, IReadOnlyList<int> bones = null )
		{
			for ( var regionIndex = 0; regionIndex < model.Regions.Count; regionIndex++ )
			{
				var modelRegion = model.Regions[regionIndex];
				if ( modelRegion.Permutations.Count == 0 || (region is not null && region != regionIndex) )
					continue;

				// A region with several permutations (a vehicle's tires and their motion blurred
				// "~blur", a weapon's barrels and "~primary-blur", "~damaged" hulls) becomes a body group
				// named after the region, a choice per permutation in the tag's order; the first is
				// the default. Halo swaps them as the object moves or fires. Only the default counts
				// for the collision, bounds and skeleton; a placed object showing others gets their
				// collision from BuildCollision.
				var choices = modelRegion.Permutations.Count > 1 && !DrawOnly ? modelRegion.Permutations.Count : 1;
				var group = choices > 1 ? modelRegion.Name : null;

				for ( var choice = 0; choice < choices; choice++ )
				{
					var permutation = modelRegion.Permutations[choice];
					foreach ( var (geometry, lods) in Geometries( model, permutation ) )
					{
						if ( geometry < 0 || geometry >= model.Geometries.Count )
							continue;

						var main = choice == 0 && geometry == permutation.DrawnGeometry;
						foreach ( var part in model.Geometries[geometry] )
							AddPart( model, part, bones, group, choice, lods, main );
					}
				}
			}
		}

		// The geometries a permutation draws, each with the LODs it draws at (levels often share one).
		List<(int Geometry, int Lods)> Geometries( GbxModelTag model, ModelPermutation permutation )
		{
			if ( !AllLevels )
				return [(permutation.DrawnGeometry, AllLods)];

			var geometries = new List<(int Geometry, int Lods)>();
			for ( var lod = 0; lod < permutation.Lods.Length; lod++ )
			{
				var geometry = permutation.Lods[ModelDetailLevels.Level( lod, model.DrawnLevel )];
				var index = geometries.FindIndex( g => g.Geometry == geometry );
				if ( index < 0 )
					geometries.Add( (geometry, 1 << lod) );
				else
					geometries[index] = (geometry, geometries[index].Lods | 1 << lod);
			}
			return geometries;
		}

		// main: a part of the first permutation at the level drawn at normal sizes, which gives the collision, bounds and bones.
		void AddPart( GbxModelTag model, ModelPart part, IReadOnlyList<int> bones, string group, int choice, int lods, bool main )
		{
			var modelShader = part.ShaderIndex >= 0 && part.ShaderIndex < model.Shaders.Count ? model.Shaders[part.ShaderIndex] : null;
			var shader = modelShader?.Shader;
			var permutation = ForcedShaderPermutation > 0 ? ForcedShaderPermutation : Math.Max( modelShader?.Permutation ?? 0, 0 );
			if ( shader is null || !HaloMaterials.IsSupportedOnObject( shader.Class ) )
				return;

			var isGlass = shader.Class == TagClasses.ShaderGlass;
			var drawn = _parts switch
			{
				Parts.All => !isGlass || !Camouflaged,
				Parts.AllButGlass => !isGlass,
				Parts.DepthPrepass => ObjectSelfOcclusion.Occludes( _cache, shader ),
				_ => isGlass,
			};
			if ( !drawn && !main )
				return;

			var source = model.ReadVertices( _cache, part );
			var vertices = new List<HaloVertex>( source.Length );
			foreach ( var v in source )
			{
				var normal = (Vector3)v.Normal;
				vertices.Add( new HaloVertex
				{
					Position = (Vector3)v.Position * _scale,
					Normal = normal,
					Tangent = HaloVertex.Tangent4( normal, v.Tangent, v.Binormal ),
					TexCoord = v.TexCoord,
				} );
			}

			// Halo winds triangles the opposite way to s&box.
			var triangles = GbxModelTag.ReadTriangles( _cache, part );
			var indices = new List<int>( triangles.Count );
			for ( var i = 0; i < triangles.Count; i += 3 )
			{
				indices.Add( triangles[i + 2] );
				indices.Add( triangles[i + 1] );
				indices.Add( triangles[i] );
			}

			if ( indices.Count == 0 )
				return;

			if ( _collision && main && HaloMaterials.IsSolid( shader.Class ) )
			{
				var collisionBase = _collisionVertices.Count;
				_collisionVertices.AddRange( vertices.Select( x => x.Position ) );
				_collisionIndices.AddRange( indices.Select( x => collisionBase + x ) );
			}

			var weighted = bones is not null ? GbxSkeleton.Skin( vertices, source, bones ) : null;
			if ( weighted is not null && Refit is not null )
			{
				for ( var i = 0; i < weighted.Count; i++ )
				{
					var v = weighted[i];
					(v.Position, v.Normal, v.Tangent, v.BlendIndices, v.BlendWeights) = Refit.Refit( v.Position, v.Normal, v.Tangent, v.BlendIndices, v.BlendWeights );
					weighted[i] = v;
					vertices[i] = vertices[i] with { Position = v.Position, Normal = v.Normal, Tangent = v.Tangent };
				}
			}

			if ( main )
			{
				_allPositions.AddRange( vertices.Select( x => x.Position ) );
				if ( weighted is not null )
					AllSkinnedVertices.AddRange( weighted );
			}

			if ( !drawn )
				return;

			// One material per shader and shader permutation: parts sharing a shader can show
			// different images of its bitmaps (the assault rifle's two ammo counter digits).
			if ( !_materials.TryGetValue( (shader.Id, permutation), out var material ) )
			{
				var skin = SkinMaps( shader );
				var materialName = $"{_name}/{shader.Path.Replace( '\\', '/' )}{(permutation > 0 ? $"_perm{permutation}" : "")}{(GlassPass > 0 ? $"_pass{GlassPass}" : "")}.vmat";
				material = _parts == Parts.DepthPrepass
					? _prepassMaterial ??= Material.Create( $"{_name}/depth_prepass.vmat", ObjectSelfOcclusion.PrepassShaderPath )
					: GlassPass > 0
					? GlassWaterMaterial.CreateGlass( _host, materialName, _cache, shader, null, GlassPass )
					: skin is not null
						? ModelMaterial.Create( _host, materialName, _cache, shader, permutation, skin: skin, skinCubeMap: SkinCubeMap )
					: Camouflaged && shader.Class == TagClasses.ShaderModel
						? ModelMaterial.Create( _host, materialName, _cache, shader, permutation, camouflaged: true )
						: HaloMaterials.Create( _host, materialName, _cache, shader, onObject: true, permutation: permutation );
				_materials[(shader.Id, permutation)] = material;
			}

			if ( !_batchesByKey.TryGetValue( (material, group, choice, lods), out var batch ) )
			{
				batch = new Batch { Material = material, Group = group, Choice = choice, Lods = lods };
				_batchesByKey[(material, group, choice, lods)] = batch;
				_batches.Add( batch );
			}

			var first = batch.Vertices.Count;
			if ( weighted is not null )
			{
				if ( main )
					SkinnedVertices.AddRange( weighted );
				batch.Skinned.AddRange( weighted );
			}
			batch.Vertices.AddRange( vertices );
			foreach ( var index in indices )
				batch.Indices.Add( first + index );
		}

		/// <summary>
		/// The skin's maps for a model shader's base map, or null.
		/// </summary>
		WeaponSkinMaps SkinMaps( TagEntry shader )
		{
			if ( Skin is null || shader.Class != TagClasses.ShaderModel || GlassPass > 0 )
				return null;

			var baseMap = new ShaderModelTag( _cache, shader ).BaseMap?.Path;
			return baseMap is not null && Skin.Maps.TryGetValue( baseMap, out var maps ) ? maps : null;
		}

		/// <summary>
		/// Adds the meshes (the glass, depth prepass and self-occluding models' bounded by the whole
		/// model grown by <see cref="BoundsPadding"/>) and the solid parts as a trace mesh. Call once,
		/// after the last <see cref="Add"/>.
		/// </summary>
		/// <param name="collisionMesh">Whether the solid parts are the physics collision too.</param>
		public void Finish( bool collisionMesh = true )
		{
			BBox? whole = null;
			if ( WholeBounds && _meshBounds is null && _allPositions.Count > 0 )
			{
				var bounds = BBox.FromPoints( _allPositions );
				whole = new BBox( bounds.Mins - BoundsPadding, bounds.Maxs + BoundsPadding );
			}

			foreach ( var batch in _batches )
			{
				var mesh = new Mesh( batch.Material );
				if ( batch.Skinned.Count > 0 )
					mesh.CreateVertexBuffer( batch.Skinned.Count, batch.Skinned );
				else
					mesh.CreateVertexBuffer( batch.Vertices.Count, batch.Vertices );
				mesh.CreateIndexBuffer( batch.Indices.Count, batch.Indices );
				mesh.Bounds = whole ?? _meshBounds ?? BBox.FromPoints( batch.Vertices.Select( x => x.Position ) );
				AddMesh( _builder, mesh, batch.Lods, batch.Group, batch.Choice );
			}

			if ( AllLevels )
			{
				for ( var lod = 1; lod < 8; lod++ )
					_builder.WithLodDistance( lod, NeverSwitchDistance );
			}

			if ( _collisionIndices.Count > 0 )
			{
				if ( collisionMesh )
					_builder.AddCollisionMesh( _collisionVertices, _collisionIndices );
				_builder.AddTraceMesh( _collisionVertices, _collisionIndices );
			}
		}
	}
}

/// <summary>
/// Loads a gbxmodel tag as a model resource. Models build on the main thread, where their
/// collision's surfaces can load (see <see cref="ModelCollision"/>).
/// </summary>
/// <param name="aimPoses">Whether it's the <c>_aim</c> variant, with the unit's aiming baked into sequences.</param>
/// <param name="parts">Which parts to build: everything, or one of the parts of a model with glass
/// (see <see cref="ObjectGlass"/>).</param>
sealed class GbxModelLoader( string mapPath, uint tagId, bool aimPoses = false, GbxModelBuilder.Parts parts = GbxModelBuilder.Parts.All, bool camouflaged = false, int forcedShaderPermutation = 0 ) : HaloCeLoader
{
	/// <summary>
	/// The resource name of a model's variant drawn with a forced shader permutation (see
	/// <see cref="ObjectTags.ForcedShaderPermutations"/>): the model's resource name plus
	/// <c>_perm</c> and the permutation (<c>characters/elite/elite_perm1</c>).
	/// </summary>
	public static string PermutationResourceName( string modelTagPath, int permutation ) => $"{HaloCeMount.ResourcePath( modelTagPath )}_perm{permutation}";

	/// <summary>
	/// The resource name of a model's camouflaged variant: any model resource name plus <c>_camo</c>
	/// (<c>cyborg_camo</c>, <c>cyborg_aim_camo</c>, <c>pistol_fp_camo</c>).
	/// </summary>
	public static string CamoResourceName( string resourceName ) => $"{resourceName}_camo";

	/// <summary>
	/// The resource name of a model's variant with aim sequences (see <see cref="AimPoses"/>).
	/// </summary>
	public static string AimResourceName( string modelTagPath ) => $"{HaloCeMount.ResourcePath( modelTagPath )}_aim";

	protected override Task<object> LoadAsync()
	{
		// On the main thread, build right here: awaiting would make a blocking caller pump the
		// main thread queue in the middle of whatever it's doing (see HaloSounds).
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
		var cache = Host.GetCache( mapPath );
		var tag = cache.GetTag( tagId );
		return tag is null ? null : GbxModelBuilder.Build( Host, Path, cache, new GbxModelTag( cache, tag ), animated: true, parts: parts, aimPoses: aimPoses, camouflaged: camouflaged, forcedShaderPermutation: forcedShaderPermutation );
	}
}
