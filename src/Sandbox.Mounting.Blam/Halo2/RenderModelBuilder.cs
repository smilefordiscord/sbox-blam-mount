using System.Runtime.InteropServices;
using Blam.Halo2;
using Blam.Halo2.Tags;
using HaloVertex = Sandbox.Mounting.Blam.Halo1.HaloVertex;

namespace Sandbox.Mounting.Blam.Halo2;

/// <summary>
/// A render model vertex: <see cref="HaloVertex"/> plus what Halo 2's PRT shaders light it with.
/// </summary>
[StructLayout( LayoutKind.Sequential )]
struct Halo2ModelVertex
{
	[VertexLayout.Position] public Vector3 Position;
	[VertexLayout.Normal] public Vector3 Normal;
	[VertexLayout.Tangent] public Vector4 Tangent;
	[VertexLayout.TexCoord( 0 )] public Vector2 TexCoord;
	[VertexLayout.TexCoord( 1 )] public Vector2 LightmapTexCoord;

	/// <summary>
	/// The lightmap's incoming light direction (world space) of baked scenery (<see cref="ModelLighting.Baked"/>),
	/// zero elsewhere.
	/// </summary>
	[VertexLayout.TexCoord( 4 )] public Vector3 IncidentLight;

	/// <summary>
	/// The lightmap colour of baked scenery and of the few sections lit per vertex, which the prt_lightmap and
	/// prt_scarab templates add to the PRT light (zero elsewhere).
	/// </summary>
	[VertexLayout.TexCoord( 5 )] public Vector3 LightmapColor;

	/// <summary>
	/// The vertex's radiance transfer, 9 spherical harmonic coefficients in model space (<see cref="RenderModelPrt"/>):
	/// what the PRT templates dot the object's light into. Vertices the PRT data doesn't cover get an unshadowed
	/// surface's (<see cref="SphericalHarmonics.CosineTransfer"/>).
	/// </summary>
	[VertexLayout.TexCoord( 6 )] public Vector3 PrtTransferA;
	[VertexLayout.TexCoord( 7 )] public Vector3 PrtTransferB;
	[VertexLayout.TexCoord( 8 )] public Vector3 PrtTransferC;
}

/// <summary>
/// <see cref="Halo2ModelVertex"/> plus up to four nodes a render model vertex is weighted to.
/// </summary>
[StructLayout( LayoutKind.Sequential )]
struct Halo2SkinnedVertex
{
	[VertexLayout.Position] public Vector3 Position;
	[VertexLayout.Normal] public Vector3 Normal;
	[VertexLayout.Tangent] public Vector4 Tangent;
	[VertexLayout.TexCoord( 0 )] public Vector2 TexCoord;
	[VertexLayout.TexCoord( 1 )] public Vector2 LightmapTexCoord;
	[VertexLayout.TexCoord( 4 )] public Vector3 IncidentLight;
	[VertexLayout.TexCoord( 5 )] public Vector3 LightmapColor;
	[VertexLayout.TexCoord( 6 )] public Vector3 PrtTransferA;
	[VertexLayout.TexCoord( 7 )] public Vector3 PrtTransferB;
	[VertexLayout.TexCoord( 8 )] public Vector3 PrtTransferC;

	[VertexLayout.BlendIndices] public Color32 BlendIndices;
	[VertexLayout.BlendWeight] public Color32 BlendWeights;
}

/// <summary>
/// Turns a Halo 2 render model into an s&amp;box model: every region's permutations at their highest level of
/// detail, a skeleton from its nodes in their default pose with the vertices skinned to it, markers as
/// attachments, and one mesh per material of each section.
/// </summary>
/// <remarks>
/// <para>Models are built per <c>hlmt</c> (model tag), the way the Halo CE mount builds per gbxmodel: the
/// <c>hlmt</c> is what objects point at, and it holds the variants (which permutation each region shows,
/// and which ones it may hide) besides the render model, collision, animations and physics. Regions with a
/// choice to make become body groups (<see cref="ModelPermutations.Groups"/>): one choice per permutation, named
/// after it, and "none" where a variant may hide the region, the model's default variant's choice first (the
/// one s&amp;box shows by default), so game code picks <c>SetBodyGroup( "head", "johnson" )</c> and the scene sets
/// each placed object's picks the same way. Every LOD distance is pinned out of reach: body group choices only go
/// to LOD 0.</para>
///
/// <para>Render model vertices are already in model space in the default pose, for every geometry class,
/// so skinning them to the default pose leaves the model as it is without a skeleton: rigid sections go
/// to their rigid node, rigid boned and skinned vertices to their nodes through the section's node map.
/// Single node models (most scenery) stay static meshes.</para>
///
/// <para>Collision: the drawn geometry as a trace mesh, and as the physics mesh for models with a collision
/// model, so objects Halo lets you walk through stay non-solid. Default choices only. A crate's model has Halo's
/// own physics and collision models instead (<see cref="PhysicsModelBodies"/>).</para>
/// </remarks>
static class RenderModelBuilder
{
	/// <summary>
	/// A section's baked lighting (<see cref="LightmapTag.SceneryLighting"/>): incident directions and colours per
	/// vertex, either one empty when there are none.
	/// </summary>
	public delegate (ArraySegment<System.Numerics.Vector3> IncidentDirections, ArraySegment<System.Numerics.Vector3> Colors) SectionLighting( int section, int vertexCount );

	/// <summary>LOD switch distance no view reaches.</summary>
	public const float NeverSwitchDistance = 1e7f;

	/// <param name="name">The model's resource name; materials are named below it.</param>
	/// <param name="hlmt">The model tag the render model came from, for its variants and collision model; null for a bare render model (the sky).</param>
	/// <param name="scale">s&amp;box units per world unit.</param>
	/// <param name="baked">For <see cref="ModelLighting.Baked"/>: each section's lighting.</param>
	/// <param name="materials">Materials by the model's material index, to share between models built the same
	/// way (one per baked placement); filled as they're made.</param>
	/// <param name="materialBase">The resource name materials are named below (default: <paramref name="name"/>'s).</param>
	/// <param name="layer">Only this region's parts with this material (one of <see cref="Layers"/>); null for all.</param>
	/// <param name="variantColors">Give the model's variants' change colours (<see cref="AddVariantColors"/>).</param>
	/// <param name="camouflaged">The camouflaged variant (<see cref="CamoModelLoader"/>): opaque templates get
	/// halo2_opaque_camo.shader.</param>
	/// <param name="physics">Halo's physics for the model (<see cref="PhysicsModelBodies"/>) in place of the drawn geometry's collision.</param>
	public static Model Build( string name, RenderModelTag model, ModelTag hlmt, float scale, ModelLighting lighting, bool collision = true,
		SectionLighting baked = null, Dictionary<int, Material> materials = null, string materialBase = null, (int Region, int Material)? layer = null,
		bool variantColors = false, bool camouflaged = false, PhysicsModelPlan physics = null )
	{
		var builder = Model.Builder.WithName( name );
		var skinned = HasSkeleton( model ) || Halo2ModelAnimations.NeedsSkeleton( model, hlmt );
		var groups = BodyGroups( model, hlmt ).ToDictionary( g => g.Region );
		var defaults = hlmt is null ? null : ModelPermutations.Likeliest( model, ModelPermutations.DefaultVariant( hlmt ) );
		materials ??= new Dictionary<int, Material>();
		var allVertices = new List<Halo2SkinnedVertex>();
		var collisionVertices = new List<Vector3>();
		var collisionIndices = new List<int>();
		materialBase ??= System.IO.Path.ChangeExtension( name, null );
		var parts = new ModelParts( builder, materialBase, lighting, variantColors, camouflaged, groups.Count > 0 )
		{
			Functions = lighting == ModelLighting.Object ? ShaderFunctions.Slots( model.Materials ) : null,
		};

		for ( var r = 0; r < model.Regions.Count; r++ )
		{
			if ( layer is { } only && only.Region != r )
				continue;

			var region = model.Regions[r];
			groups.TryGetValue( r, out var group );

			// Regions without a choice show the default variant's permutation (the first, or none).
			var shown = defaults?[r] ?? 0;
			var choices = group?.Permutations ?? (shown >= 0 && shown < region.Permutations.Count ? [shown] : []);
			parts.AddRegion( model, materials, region, choices, group, skinned,
				( permutation, section ) => ReadVertices( section, model, scale, permutation.HighestSection, permutation.HighestLevel,
					baked?.Invoke( permutation.HighestSection, section.VertexCount ) ?? default ),
				layer?.Material,
				( choice, vertices, indices, drawn ) =>
				{
					if ( choice != 0 )
						return;

					if ( collision )
					{
						var collisionBase = collisionVertices.Count;
						collisionVertices.AddRange( vertices.Select( v => v.Position ) );
						collisionIndices.AddRange( indices.Select( i => collisionBase + i ) );
					}

					if ( skinned && drawn )
						allVertices.AddRange( vertices );
				} );
		}

		if ( parts.Count == 0 && collisionIndices.Count == 0 )
			return null;

		if ( groups.Count > 0 )
		{
			for ( var lod = 1; lod < 8; lod++ )
				builder.WithLodDistance( lod, NeverSwitchDistance );
		}

		if ( variantColors && hlmt is not null )
			AddVariantColors( builder, model, hlmt, materials, parts.Used, materialBase, lighting, camouflaged, parts.Functions );

		string[] bones = null;
		Transform[] pose = DefaultPose( model, scale );
		if ( skinned )
		{
			bones = global::Blam.Halo1.TagData.AnimationSkeleton.UniqueNames( model.Nodes.Select( n => n.Name ) );
			AddBones( builder, model.Nodes, bones, pose, allVertices );
			Halo2ModelAnimations.Add( builder, model, hlmt, scale );
		}

		AddMarkers( builder, model, bones, pose, scale );

		if ( physics is not null )
		{
			PhysicsModelBodies.Add( builder, physics, hlmt, model, bones, pose, scale );
		}
		else if ( collisionIndices.Count > 0 )
		{
			var hulls = CollisionModelTag.IsCollisionModel( hlmt?.CollisionModel ) && AddCollisionHulls( builder, new CollisionModelTag( hlmt.CollisionModel ), model, pose, scale );
			if ( !hulls && hlmt?.CollisionModel is { Address: not 0 } )
				builder.AddCollisionMesh( collisionVertices, collisionIndices );
			builder.AddTraceMesh( collisionVertices, collisionIndices );
		}

		return builder.Create();
	}

	// Halo's collision model as one body in model space, each region's first permutation's BSPs as hulls (CollisionHulls) where the render model's node of the same name is in its default pose, so the object can move as a prop.
	// Returns false if it has no BSPs.
	static bool AddCollisionHulls( ModelBuilder builder, CollisionModelTag collision, RenderModelTag model, Transform[] pose, float scale )
	{
		var bsps = collision.Regions.Select( r => r.Permutations.FirstOrDefault() ).Where( p => p is not null ).SelectMany( p => p.Bsps ).Where( b => b.Bsp.Surfaces.Count > 0 ).ToList();
		if ( bsps.Count == 0 )
			return false;

		var body = builder.AddBody();
		foreach ( var (node, bsp) in bsps )
		{
			var name = node >= 0 && node < collision.Nodes.Count ? collision.Nodes[node] : null;
			var renderNode = model.Nodes.FirstOrDefault( n => string.Equals( n.Name, name, StringComparison.OrdinalIgnoreCase ) )?.Index ?? -1;
			var nodePose = renderNode >= 0 && renderNode < pose.Length ? pose[renderNode] : Transform.Zero;
			Vector3 ToBody( System.Numerics.Vector3 p ) => nodePose.PointToWorld( (Vector3)p * scale );
			CollisionHulls.Add( body, bsp.ConvexPieces( CollisionHulls.MaximumHalfEdges ), ToBody, () =>
			{
				var indices = bsp.Triangles();
				return (bsp.Vertices.Select( ToBody ).ToArray(), indices.Select( i => (uint)i ).ToArray(), []);
			} );
		}

		return true;
	}

	/// <summary>
	/// A model's drawn parts in the order Halo draws them, as (region, material) pairs: region by region, and within
	/// a region's first permutation each material where its first part is. For <see cref="Build"/>'s layer.
	/// </summary>
	public static IEnumerable<(int Region, int Material)> Layers( RenderModelTag model )
	{
		foreach ( var region in model.Regions )
		{
			var permutation = region.Permutations.FirstOrDefault();
			if ( permutation is null || model.GetSection( permutation.HighestSection ) is not { IsEmpty: false } section )
				continue;

			foreach ( var material in section.Parts.Where( p => p.IsDrawn ).Select( p => p.Material ).Distinct() )
				yield return (region.Index, material);
		}
	}

	/// <summary>
	/// The change colours of the model's variants (an elite's rank: minor blue, major red, zealot gold; Johnson's
	/// dress uniform), for the materials whose templates use them: the default variant's in its materials, for
	/// objects without colours of their own (models a game spawns), and a material group per variant, named after it,
	/// whose copies set that variant's over the object's own. Colours come from the first object using the model
	/// (<see cref="ModelPermutations.ModelOwner"/>), each picked as MCC would for an object created at the origin.
	/// </summary>
	public static void AddVariantColors( ModelBuilder builder, RenderModelTag model, ModelTag hlmt, Dictionary<int, Material> materials, List<Material> used,
		string materialBase, ModelLighting lighting, bool camouflaged = false, IReadOnlyList<string> functions = null )
	{
		var owner = ModelPermutations.ModelOwner( hlmt.Tag );
		if ( owner is null || owner.ChangeColors.Count == 0 || hlmt.Variants.Count == 0 )
			return;

		var tinted = materials
			.Where( m => m.Value is not null && used.Contains( m.Value ) && model.Materials[m.Key] is { Address: not 0 } shader
				&& shader.Class == ShaderTag.Class && (OpaqueMaterial.UsesChangeColors( new ShaderTag( shader ) ) || TransparentMaterial.UsesChangeColors( new ShaderTag( shader ) )) )
			.DistinctBy( m => m.Value )
			.ToList();
		if ( tinted.Count == 0 )
			return;

		var defaultColors = owner.InitialChangeColors( ModelPermutations.DefaultVariant( hlmt )?.Name, System.Numerics.Vector3.Zero );
		foreach ( var (_, material) in tinted )
			OpaqueMaterial.SetVariantChangeColors( material, defaultColors, picked: false );

		// Variants with the same colours share their materials.
		var copies = new Dictionary<string, Material[]>();
		builder.AddMaterialGroup( "default" ).AddMaterials( used.ToArray() );
		var names = new HashSet<string>( StringComparer.OrdinalIgnoreCase ) { "default" };
		foreach ( var variant in hlmt.Variants )
		{
			if ( string.IsNullOrEmpty( variant.Name ) || !names.Add( variant.Name ) )
				continue;

			var colors = owner.InitialChangeColors( variant.Name, System.Numerics.Vector3.Zero );
			var key = string.Join( ";", colors.Select( c => c?.ToString() ?? "-" ) );
			if ( !copies.TryGetValue( key, out var group ) )
			{
				copies[key] = group = used.ToArray();
				foreach ( var (index, material) in tinted )
				{
					var shader = model.Materials[index];
					var copy = ModelMaterials.Create( $"{materialBase}/{Halo2Mount.ResourcePath( shader.Path )}_{variant.Name}.vmat", new ShaderTag( shader ), lighting, named: true, camouflaged: camouflaged,
						functions: functions );
					if ( copy is null )
						continue;

					OpaqueMaterial.SetVariantChangeColors( copy, colors, picked: true );
					group[used.IndexOf( material )] = copy;
				}
			}

			builder.AddMaterialGroup( variant.Name ).AddMaterials( group );
		}
	}

	/// <summary>
	/// The regions that become body groups, with their choices (<see cref="ModelPermutations.Groups"/>), the default
	/// variant's first.
	/// </summary>
	public static IReadOnlyList<RegionChoices> BodyGroups( RenderModelTag model, ModelTag hlmt ) =>
		ModelPermutations.Groups( model, hlmt, hlmt is null ? null : ModelPermutations.DefaultVariant( hlmt ) );

	/// <summary>
	/// The name of a region's body group choice: its permutation's name, or "none" (-1).
	/// </summary>
	public static string ChoiceName( RenderModelRegion region, int permutation ) =>
		permutation < 0 ? NoChoice : string.IsNullOrEmpty( region.Permutations[permutation].Name ) ? $"{permutation}" : region.Permutations[permutation].Name;

	/// <summary>The body group choice of a region that shows nothing.</summary>
	public const string NoChoice = "none";

	/// <summary>
	/// The body group, with one choice, of the parts of a model with body groups that are always drawn. No Halo 2
	/// render model has a region named so.
	/// </summary>
	public const string FixedGroup = "model";

	/// <summary>
	/// Shows a permutation (-1: none) of a body group on a renderer by its choice's name: models built from another
	/// map's copy of the tag may have their choices in another order, or not have the one asked for (left as it is).
	/// </summary>
	public static void SetChoice( ModelRenderer renderer, RenderModelRegion region, int permutation )
	{
		var name = ChoiceName( region, permutation );
		if ( renderer.Model?.Parts.Get( region.Name )?.Choices.Any( c => c.Name == name ) == true )
			renderer.SetBodyGroup( region.Name, name );
	}

	/// <summary>
	/// Models with more than one node get a skeleton, if their nodes come after their parents (bones have
	/// to), as Halo's tools always write them.
	/// </summary>
	static bool HasSkeleton( RenderModelTag model )
	{
		if ( model.Nodes.Count < 2 || model.Nodes.Count > 255 )
			return false;

		for ( var i = 0; i < model.Nodes.Count; i++ )
		{
			if ( model.Nodes[i].Parent >= i )
				return false;
		}

		return true;
	}

	static Rotation ToRotation( System.Numerics.Quaternion q ) => new( q.X, q.Y, q.Z, q.W );

	/// <summary>
	/// The nodes' default pose in model space (s&amp;box units).
	/// </summary>
	public static Transform[] DefaultPose( RenderModelTag model, float scale ) =>
		model.DefaultPose().Select( p => new Transform( (Vector3)p.Translation * scale, ToRotation( p.Rotation ) ) ).ToArray();

	/// <param name="sectionIndex">The section's index in the model and <paramref name="level"/> the level of detail
	/// it's drawn at, which pick its PRT data.</param>
	/// <param name="baked">The section's baked lighting, or empty.</param>
	public static List<Halo2SkinnedVertex> ReadVertices( GeometrySection section, RenderModelTag model, float scale, int sectionIndex, int level,
		(ArraySegment<System.Numerics.Vector3> IncidentDirections, ArraySegment<System.Numerics.Vector3> Colors) baked )
	{
		var transfer = model.HasPrt ? model.Prt?.Transfer( sectionIndex, level, section.VertexCount ) : null;
		const int n = RenderModelPrt.CoefficientCount;

		var nodeCount = Math.Max( 1, model.Nodes.Count );
		int Node( int raw )
		{
			var node = section.NodeMap is { } map ? raw < map.Length ? map[raw] : 0 : raw;
			return node < nodeCount ? node : 0;
		}

		var rigidNode = section.RigidNode >= 0 && section.RigidNode < nodeCount ? section.RigidNode : 0;
		var vertices = new List<Halo2SkinnedVertex>( section.VertexCount );
		for ( var i = 0; i < section.VertexCount; i++ )
		{
			var normal = section.Normals?[i] ?? System.Numerics.Vector3.UnitZ;
			var tangent = section.Tangents?[i] ?? System.Numerics.Vector3.UnitX;
			var binormal = section.Binormals?[i] ?? System.Numerics.Vector3.UnitY;

			var indices = new Color32( (byte)rigidNode, 0, 0, 0 );
			var weights = new Color32( 255, 0, 0, 0 );
			if ( section.NodeIndices is { } nodes && section.NodeWeights is { } w )
			{
				// Weights are out of 255 and sum to 254-256; the first takes up the difference.
				int w0 = w[i * 4], w1 = w[i * 4 + 1], w2 = w[i * 4 + 2], w3 = w[i * 4 + 3];
				w0 = Math.Clamp( 255 - w1 - w2 - w3, 0, 255 );
				indices = new Color32( (byte)Node( nodes[i * 4] ), (byte)Node( nodes[i * 4 + 1] ), (byte)Node( nodes[i * 4 + 2] ), (byte)Node( nodes[i * 4 + 3] ) );
				weights = new Color32( (byte)w0, (byte)w1, (byte)w2, (byte)w3 );
			}

			ReadOnlySpan<float> t = transfer is null ? SphericalHarmonics.CosineTransfer( normal ) : transfer.AsSpan( i * n, n );
			vertices.Add( new Halo2SkinnedVertex
			{
				Position = (Vector3)section.Positions[i] * scale,
				Normal = normal,
				Tangent = HaloVertex.Tangent4( normal, tangent, binormal ),
				TexCoord = section.TexCoords?[i] ?? default,
				IncidentLight = baked.IncidentDirections.Array is null ? default : (Vector3)baked.IncidentDirections[i],
				LightmapColor = baked.Colors.Array is not null ? (Vector3)baked.Colors[i] : section.VertexColors?[i] ?? default,
				PrtTransferA = new Vector3( t[0], t[1], t[2] ),
				PrtTransferB = new Vector3( t[3], t[4], t[5] ),
				PrtTransferC = new Vector3( t[6], t[7], t[8] ),
				BlendIndices = indices,
				BlendWeights = weights,
			} );
		}

		return vertices;
	}

	public static Halo2ModelVertex Unskinned( Halo2SkinnedVertex v ) => new()
	{
		Position = v.Position,
		Normal = v.Normal,
		Tangent = v.Tangent,
		TexCoord = v.TexCoord,
		IncidentLight = v.IncidentLight,
		LightmapColor = v.LightmapColor,
		PrtTransferA = v.PrtTransferA,
		PrtTransferB = v.PrtTransferB,
		PrtTransferC = v.PrtTransferC,
	};

	public static Mesh EmptyMesh( string name, Material material )
	{
		var mesh = new Mesh( name, material );
		mesh.CreateVertexBuffer( 3, new List<HaloVertex> { default, default, default } );
		mesh.CreateIndexBuffer( 3, new List<int> { 0, 1, 2 } );
		mesh.Bounds = new BBox( Vector3.Zero, Vector3.Zero );
		return mesh;
	}

	/// <summary>
	/// Adds the nodes as bones, parents first, each bounded by the vertices it moves (a skinned model's
	/// render bounds are its bones' bounds in their current pose).
	/// </summary>
	public static void AddBones( ModelBuilder builder, IReadOnlyList<RenderModelNode> nodes, string[] names, Transform[] pose, List<Halo2SkinnedVertex> vertices )
	{
		var bounds = new BBox?[names.Length];
		void Include( int bone, int weight, Vector3 position )
		{
			if ( weight == 0 || bone >= names.Length )
				return;

			var point = pose[bone].PointToLocal( position );
			bounds[bone] = bounds[bone] is { } box ? box.AddPoint( point ) : new BBox( point, point );
		}

		foreach ( var v in vertices )
		{
			Include( v.BlendIndices.r, v.BlendWeights.r, v.Position );
			Include( v.BlendIndices.g, v.BlendWeights.g, v.Position );
			Include( v.BlendIndices.b, v.BlendWeights.b, v.Position );
			Include( v.BlendIndices.a, v.BlendWeights.a, v.Position );
		}

		for ( var i = 0; i < names.Length; i++ )
		{
			var parent = nodes[i].Parent;
			builder.AddBone( names[i], pose[i].Position, pose[i].Rotation, bounds[i] ?? new BBox( Vector3.Zero, Vector3.Zero ), parent < 0 ? null : names[parent] );
		}
	}

	/// <summary>
	/// Markers as attachments, on their node's bone (or placed by the node's default pose in a model
	/// without a skeleton). Markers sharing a name (a weapon's several muzzle flashes) are numbered from
	/// the second on: <c>muzzle_flash</c>, <c>muzzle_flash_1</c>...
	/// </summary>
	// A marker named like a bone (the chief's head marker, which H2's first person camera rule measures) is <name>_marker, since an attachment can't share a bone's name.
	// bones holds each node's bone name (null without a skeleton), taken the names in use (default: the bones'), filled as markers are added.
	public static void AddMarkers( ModelBuilder builder, RenderModelTag model, string[] bones, Transform[] pose, float scale, HashSet<string> taken = null )
	{
		taken ??= new HashSet<string>( bones ?? [], StringComparer.OrdinalIgnoreCase );
		var counts = new Dictionary<string, int>( StringComparer.OrdinalIgnoreCase );
		foreach ( var marker in model.Markers )
		{
			if ( string.IsNullOrEmpty( marker.Name ) )
				continue;

			var n = counts.GetValueOrDefault( marker.Name );
			counts[marker.Name] = n + 1;
			var name = n == 0 ? marker.Name : $"{marker.Name}_{n}";
			if ( !taken.Add( name ) && !taken.Add( name = $"{name}_marker" ) )
				continue;

			var node = marker.Node >= 0 && marker.Node < pose.Length ? marker.Node : 0;
			var local = new Transform( (Vector3)marker.Translation * scale, ToRotation( marker.Rotation ) );
			if ( bones is not null )
			{
				builder.AddAttachment( name, local.Position, local.Rotation, bones[node] );
			}
			else
			{
				var world = pose.Length > 0 ? pose[node].ToWorld( local ) : local;
				builder.AddAttachment( name, world.Position, world.Rotation );
			}
		}
	}
}

// Render models' drawn parts as meshes of a model being built: a mesh per section and material, each material made once per shader.
// A template's other passes are copies of the mesh, added in Halo's order: z prepass, the material's pass, the later ones.
sealed class ModelParts( ModelBuilder builder, string materialBase, ModelLighting lighting, bool named, bool camouflaged, bool grouped )
{
	// The model's function slots, which its materials' parameters follow while a game plays (DrivenParameters); null for none.
	public IReadOnlyList<string> Functions { get; init; }

	// The materials in the order meshes first use them, as material groups list them.
	public List<Material> Used { get; } = new();

	// The meshes added with a material of their own (not counting placeholders and extra passes).
	public int Count { get; private set; }

	readonly Dictionary<(uint Model, int Index), (Material Prepass, List<Material> After)> _extraPasses = new();
	Material _placeholder;

	// A region's choices (permutation indices, -1 for none), each at its highest level of detail; group is the region's body group, or null.
	// read gives a permutation's section's vertices, onlyMaterial keeps one material's parts.
	// drawn sees every choice's vertices and each material's indices, and whether that material is drawn.
	public void AddRegion( RenderModelTag model, Dictionary<int, Material> materials, RenderModelRegion region, IReadOnlyList<int> choices, RegionChoices group,
		bool skinned, Func<RenderModelPermutation, GeometrySection, List<Halo2SkinnedVertex>> read, int? onlyMaterial = null,
		Action<int, List<Halo2SkinnedVertex>, List<int>, bool> drawn = null )
	{
		for ( var choice = 0; choice < choices.Count; choice++ )
		{
			var added = 0;
			var permutation = choices[choice] >= 0 ? region.Permutations[choices[choice]] : null;
			var choiceName = RenderModelBuilder.ChoiceName( region, choices[choice] );
			var section = permutation is null ? null : model.GetSection( permutation.HighestSection );
			if ( section is not null && !section.IsEmpty )
			{
				var vertices = read( permutation, section );
				foreach ( var parts in section.Parts.Where( p => p.IsDrawn ).GroupBy( p => p.Material ) )
				{
					if ( onlyMaterial is { } only && only != parts.Key )
						continue;

					var indices = new List<int>();
					foreach ( var part in parts )
					{
						// Halo 2 winds front faces counter-clockwise, as s&box does.
						foreach ( var (a, b, c) in section.Triangles( part ) )
						{
							indices.Add( a );
							indices.Add( b );
							indices.Add( c );
						}
					}

					if ( indices.Count == 0 )
						continue;

					var material = MaterialFor( model, materials, parts.Key );
					drawn?.Invoke( choice, vertices, indices, material is not null );
					if ( material is null )
						continue;

					Mesh MeshWith( Material m )
					{
						var mesh = new Mesh( choiceName, m );
						if ( skinned )
							mesh.CreateVertexBuffer( vertices.Count, vertices );
						else
							mesh.CreateVertexBuffer( vertices.Count, vertices.Select( RenderModelBuilder.Unskinned ).ToList() );

						mesh.CreateIndexBuffer( indices.Count, indices );
						mesh.Bounds = BBox.FromPoints( indices.Select( i => vertices[i].Position ) );
						return mesh;
					}

					var (prepass, after) = ExtraPassesFor( model, parts.Key );
					if ( prepass is not null )
						AddMesh( MeshWith( prepass ), prepass, group, choice );
					AddMesh( MeshWith( material ), material, group, choice );
					foreach ( var pass in after )
						AddMesh( MeshWith( pass ), pass, group, choice );
					added++;
					Count++;
				}
			}

			// Every choice of a body group needs a mesh, or the choices after it would shift: a
			// permutation with nothing drawable (or none) gets one degenerate triangle.
			if ( added == 0 && group is not null )
			{
				_placeholder ??= Material.Create( $"{materialBase}/empty.vmat", OpaqueMaterial.ShaderPath, anonymous: !named );
				AddMesh( RenderModelBuilder.EmptyMesh( choiceName, _placeholder ), _placeholder, group, choice );
			}
		}
	}

	// The material for a model's material index, filling materials (by index) as they're made.
	Material MaterialFor( RenderModelTag model, Dictionary<int, Material> materials, int index )
	{
		if ( materials.TryGetValue( index, out var material ) )
			return material;

		var shaderTag = index >= 0 && index < model.Materials.Count ? model.Materials[index] : null;
		if ( shaderTag is not null && shaderTag.Class == ShaderTag.Class && shaderTag.Address != 0 )
		{
			// One material per shader: render models list some at several indices (the marine's teeth, packs).
			var same = materials.FirstOrDefault( m => m.Value is not null && m.Key >= 0 && m.Key < model.Materials.Count && model.Materials[m.Key]?.Id == shaderTag.Id );
			material = same.Value ?? ModelMaterials.Create( $"{materialBase}/{Halo2Mount.ResourcePath( shaderTag.Path )}.vmat", new ShaderTag( shaderTag ), lighting, named: named, camouflaged: camouflaged,
				functions: Functions );
		}

		return materials[index] = material;
	}

	// A material index's extra passes (Halo2Materials.ExtraPasses), drawn with copies of its meshes.
	(Material Prepass, List<Material> After) ExtraPassesFor( RenderModelTag model, int index )
	{
		if ( !_extraPasses.TryGetValue( (model.Tag.Id, index), out var extra ) )
		{
			var shaderTag = model.Materials[index];
			_extraPasses[(model.Tag.Id, index)] = extra = Halo2Materials.ExtraPasses( $"{materialBase}/{Halo2Mount.ResourcePath( shaderTag.Path )}.vmat", new ShaderTag( shaderTag ),
				lighting != ModelLighting.Unlit, named: named, functions: Functions );
		}

		return extra;
	}

	void AddMesh( Mesh mesh, Material material, RegionChoices group, int choice )
	{
		if ( !Used.Contains( material ) )
			Used.Add( material );

		// In a model with body groups, the parts that are always drawn go in a group of their own: s&box gives a
		// mesh outside every group all of them, which takes over every choice's mask and name (none switches).
		if ( group is null )
		{
			if ( grouped )
				builder.AddMesh( mesh, RenderModelBuilder.FixedGroup, 0 );
			else
				builder.AddMesh( mesh );
			return;
		}

		builder.AddMesh( mesh, group.Name, choice );
	}
}

/// <summary>
/// Loads a model tag (<c>hlmt</c>) as a model resource, <c>mount://halo2/&lt;hlmt path&gt;.vmdl</c>. Models
/// build on the main thread, like the Halo CE mount's.
/// </summary>
// Physics: the model with Halo's physics (PhysicsModelBodies), <hlmt path>_physics.vmdl for crates' and bipeds' models.
sealed class RenderModelLoader( string mapPath, uint tagId, bool physics = false ) : Halo2Loader
{
	protected override Task<object> LoadAsync()
	{
		// On the main thread, build right here: awaiting would make a blocking caller pump the main
		// thread queue in the middle of whatever it's doing.
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
		if ( tag is null || tag.Class != ModelTag.Class )
			return null;

		var hlmt = new ModelTag( tag );
		if ( hlmt.RenderModel is not { Address: not 0 } renderModel || renderModel.Class != RenderModelTag.Class )
			return null;

		try
		{
			return RenderModelBuilder.Build( Path, new RenderModelTag( renderModel ), hlmt, Halo2Mount.WorldScale, ModelLighting.Object, variantColors: true,
				physics: physics ? PhysicsModelPlan.For( hlmt ) : null );
		}
		catch ( Exception e )
		{
			Host.Logger.Warning( e, $"Couldn't build {tag}: {e.Message}" );
			return null;
		}
	}
}
