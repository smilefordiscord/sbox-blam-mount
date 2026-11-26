using System.Runtime.InteropServices;
using Blam.Halo3;
using Blam.Halo3.Tags;

namespace Sandbox.Mounting.Blam.Halo3;

// Halo3Vertex plus the four nodes a render model vertex is weighted to; the Halo 3 shaders take both.
[StructLayout( LayoutKind.Sequential )]
struct Halo3SkinnedVertex
{
	[VertexLayout.Position] public Vector3 Position;
	[VertexLayout.Normal] public Vector3 Normal;
	[VertexLayout.Tangent] public Vector4 Tangent;
	[VertexLayout.TexCoord( 0 )] public Vector2 TexCoord;
	[VertexLayout.TexCoord( 1 )] public Vector2 LightmapTexCoord;
	[VertexLayout.TexCoord( 4 )] public Vector4 Sh0;
	[VertexLayout.TexCoord( 5 )] public Vector4 Sh1;
	[VertexLayout.TexCoord( 6 )] public Vector4 Sh2;
	[VertexLayout.TexCoord( 7 )] public Vector4 Sh3;
	[VertexLayout.BlendIndices] public Color32 BlendIndices;
	[VertexLayout.BlendWeight] public Color32 BlendWeights;

	public Halo3Vertex Unskinned => new()
	{
		Position = Position,
		Normal = Normal,
		Tangent = Tangent,
		TexCoord = TexCoord,
		LightmapTexCoord = LightmapTexCoord,
		Sh0 = Sh0,
		Sh1 = Sh1,
		Sh2 = Sh2,
		Sh3 = Sh3,
	};
}

// Turns a Halo 3 render model (from an hlmt) into an s&box model, as Halo2/RenderModelBuilder: a skeleton from its nodes in their default pose with the vertices skinned to it, markers as attachments, one mesh per material of each permutation's meshes.
// Contract for placing objects: every region is a body group named after the region, its choices the region's permutations in render model order, each named after its permutation (its index when unnamed).
static class RenderModelBuilder
{
	// LOD switch distance no view reaches: body group choices only go to LOD 0.
	const float NeverSwitchDistance = 1e7f;

	// skyScale > 0 builds a sky for a 3D skybox drawn that many times larger: meshes with a vertex colour stream (slot 2, the sky's baked light: MCC draws them with static_per_vertex_color) are lit by it (Halo3Lighting.VertexColor, colour in Sh0), and the fog measures distances that many times larger.
	// A sky's translucent layers come back in skyLayers as models of their own, the returned model holds the rest: s&box sorts translucent objects, not a model's meshes, by the camera's distance to their bounds, so each layer sorts by where it is (Valhalla's ring, an arc thousands of units out, draws before the cloud layers around the camera, as in MCC).
	public static Model Build( string name, RenderModelTag model, ModelTag hlmt, float scale, float skyScale = 0, List<Model> skyLayers = null )
	{
		var geometry = model.Geometry();
		if ( !geometry.Load() )
			return null;

		var builder = Model.Builder.WithName( name );
		var skinned = HasSkeleton( model ) || Halo3ModelAnimations.NeedsSkeleton( model, hlmt );
		var materialBase = System.IO.Path.ChangeExtension( name, null );
		var materials = new Dictionary<(int, Halo3Lighting, int), Material>();
		var allVertices = new List<Halo3SkinnedVertex>();
		var collisionVertices = new List<Vector3>();
		var collisionIndices = new List<int>();
		var compression = geometry.Compression.Count > 0 ? geometry.Compression[0] : GeometryCompression.Identity;
		var pose = DefaultPose( model, scale );
		Material placeholder = null;
		var meshCount = 0;

		// A sky's translucent meshes, each made a model of its own (skyLayers).
		var skyMeshes = new List<Mesh>();

		// surface: for a pre-lighting decal, the material index of the surface under it, whose lighting terms it takes.
		Material MaterialFor( int index, Halo3Lighting lighting = Halo3Lighting.Probe, int surface = -1 )
		{
			if ( materials.TryGetValue( (index, lighting, surface), out var material ) )
				return material;

			var shader = index >= 0 && index < model.Materials.Count ? model.Materials[index] : null;
			var under = surface >= 0 ? model.Materials[surface] : null;
			if ( shader is { Address: not 0 } )
			{
				// One material per shader: render models list some at several indices.
				var same = materials.FirstOrDefault( m => m.Value is not null && m.Key.Item2 == lighting && model.Materials[m.Key.Item1]?.Id == shader.Id && (m.Key.Item3 < 0 ? null : model.Materials[m.Key.Item3])?.Id == under?.Id );
				var suffix = lighting == Halo3Lighting.Probe ? "" : $"_{lighting.ToString().ToLowerInvariant()}";
				if ( under is not null )
					suffix += $"_on_{System.IO.Path.GetFileName( Halo3Mount.ResourcePath( under.Path ) )}";
				material = same.Value ?? Halo3Materials.Create( $"{materialBase}/{Halo3Mount.ResourcePath( shader.Path )}{suffix}.vmat", shader, lighting, skyScale: skyScale, surface: under );
			}

			return materials[(index, lighting, surface)] = material;
		}

		// The opaque triangles decals lie on, when the model has decals drawn with the surface under them.
		var surfaces = skyScale > 0 ? null : SurfaceTriangles( model, geometry, compression, scale );

		// A surface's material with a multiply decal in its albedo, the decal's uv in the second uv set.
		var albedoMaterials = new Dictionary<(int, int), Material>();
		Material AlbedoMaterial( int decal, int surface )
		{
			if ( albedoMaterials.TryGetValue( (decal, surface), out var material ) )
				return material;
			var under = model.Materials[surface];
			var name = $"{materialBase}/{Halo3Mount.ResourcePath( under.Path )}";
			material = under is { Address: not 0 } ? Halo3Materials.Create( $"{name}_with_{System.IO.Path.GetFileName( Halo3Mount.ResourcePath( model.Materials[decal].Path ) )}.vmat", under, Halo3Lighting.Probe, albedoDecal: model.Materials[decal] ) : null;
			return albedoMaterials[(decal, surface)] = material;
		}

		// s&box allows 64 body group choices in all: models with more (the multiplayer armour sets) keep each region's first permutations, as many as fit.
		// Regions some variant hides (mp_spawn_point's "invisible") get a last choice "none" with nothing in it.
		var hideable = HideableRegions( hlmt );
		var perRegion = ChoicesPerRegion( model, hideable );
		foreach ( var region in model.Regions )
		{
			for ( var choice = 0; choice < Math.Min( region.Permutations.Count, perRegion ); choice++ )
			{
				var permutation = region.Permutations[choice];
				var choiceName = ChoiceName( region, choice );
				var added = 0;
				for ( var m = Math.Max( 0, permutation.Mesh ); permutation.Mesh >= 0 && m < permutation.Mesh + permutation.MeshCount && m < geometry.Meshes.Count; m++ )
				{
					var mesh = geometry.Meshes[m];
					if ( geometry.Vertices( mesh, compression ) is not { Count: > 0 } source )
						continue;

					List<Halo3SkinnedVertex> vertices = null;
					foreach ( var parts in mesh.Parts.Where( p => p.IsDrawn ).GroupBy( p => p.Material ) )
					{
						// Halo 3 winds front faces counter-clockwise, as s&box does.
						var indices = new List<int>();
						foreach ( var part in parts )
						{
							foreach ( var (a, b, c) in geometry.Triangles( mesh, part ) )
							{
								if ( a >= source.Count || b >= source.Count || c >= source.Count )
									continue;
								indices.Add( a );
								indices.Add( b );
								indices.Add( c );
							}
						}

						var colors = skyScale > 0 ? VertexColors( geometry, mesh, source.Count ) : null;
						var lighting = colors is null ? Halo3Lighting.Probe : Halo3Lighting.VertexColor;
						if ( indices.Count == 0 || MaterialFor( parts.Key, lighting ) is not { } partMaterial )
							continue;

						if ( vertices is null )
						{
							vertices = ReadVertices( geometry, mesh, source, model.Nodes.Count, scale );
							var prt = AmbientPrt( geometry, mesh, source.Count );
							for ( var v = 0; ( colors is not null || prt is not null ) && v < vertices.Count; v++ )
							{
								var vertex = vertices[v];
								if ( colors is not null )
									vertex.Sh0 = new Vector4( colors[v].X, colors[v].Y, colors[v].Z, 0 );
								if ( prt is not null )
									vertex.Sh1.w = 1 - prt[v];
								vertices[v] = vertex;
							}
						}

						// Decals over the model's own surfaces, by the surface under each triangle: multiply ones as that surface with the decal in its albedo, other pre-lighting ones lit with its terms.
						var pieces = new List<(Material Material, List<Halo3SkinnedVertex> Vertices, List<int> Indices)>();
						if ( surfaces is not null && IsAlbedoDecal( model, parts.Key ) )
						{
							foreach ( var (surface, piece) in AlbedoPieces( indices, vertices, surfaces ) )
							{
								var albedo = surface >= 0 ? AlbedoMaterial( parts.Key, surface ) : null;
								pieces.Add( albedo is null ? (partMaterial, vertices, indices) : (albedo, piece, Enumerable.Range( 0, piece.Count ).ToList()) );
								if ( albedo is null )
									break;
							}
						}
						else if ( surfaces is not null && IsSurfaceDecal( model, parts.Key ) )
						{
							foreach ( var (surface, pieceIndices) in SplitBySurface( indices, vertices, surfaces ) )
								pieces.Add( (surface < 0 ? partMaterial : MaterialFor( parts.Key, lighting, surface ) ?? partMaterial, vertices, pieceIndices) );
						}
						else
							pieces.Add( (partMaterial, vertices, indices) );

						foreach ( var (material, pieceVertices, pieceIndices) in pieces )
						{
							var drawn = new Mesh( choiceName, material );
							if ( skinned )
								drawn.CreateVertexBuffer( pieceVertices.Count, pieceVertices );
							else
								drawn.CreateVertexBuffer( pieceVertices.Count, pieceVertices.Select( v => v.Unskinned ).ToList() );
							drawn.CreateIndexBuffer( pieceIndices.Count, pieceIndices );
							drawn.Bounds = BBox.FromPoints( pieceIndices.Select( i => pieceVertices[i].Position ) );
							if ( skyLayers is not null && material.Shader?.ResourcePath is Halo3Materials.TransparentShaderPath or Halo3Materials.WaterShaderPath )
								skyMeshes.Add( drawn );
							else
								builder.AddMesh( drawn, region.Name, choice );
							added++;
							meshCount++;
						}

						if ( choice == 0 )
						{
							if ( skinned )
								allVertices.AddRange( vertices );
							var first = collisionVertices.Count;
							collisionVertices.AddRange( vertices.Select( v => v.Position ) );
							collisionIndices.AddRange( indices.Select( i => first + i ) );
						}
					}
				}

				// Every choice needs a mesh, or the choices after it would shift: an empty permutation gets one degenerate triangle.
				if ( added == 0 )
				{
					placeholder ??= Material.Load( "materials/dev/reflectivity_30.vmat" );
					builder.AddMesh( EmptyMesh( choiceName, placeholder ), region.Name, choice );
				}
			}

			// After the permutations: s&box shows a group's first choice by default.
			if ( hideable.Contains( region.Index ) )
			{
				placeholder ??= Material.Load( "materials/dev/reflectivity_30.vmat" );
				builder.AddMesh( EmptyMesh( NoChoice, placeholder ), region.Name, Math.Min( region.Permutations.Count, perRegion ) );
			}
		}

		foreach ( var mesh in skyMeshes )
			skyLayers.Add( Model.Builder.WithName( $"{materialBase}_layer{skyLayers.Count}.vmdl" ).AddMesh( mesh ).Create() );

		if ( meshCount == 0 )
			return null;

		for ( var lod = 1; lod < 8; lod++ )
			builder.WithLodDistance( lod, NeverSwitchDistance );

		string[] bones = null;
		if ( skinned )
		{
			bones = global::Blam.Halo1.TagData.AnimationSkeleton.UniqueNames( model.Nodes.Select( n => n.Name ) );
			AddBones( builder, model, bones, pose, allVertices );
			Halo3ModelAnimations.Add( builder, model, hlmt, scale );
		}

		AddMarkers( builder, model, bones, pose, scale );

		// Physics from Halo's collision model (its BSPs as hulls in their nodes' default pose, surfaces per global material), else the drawn geometry of the default choices when the hlmt has one; traces hit the drawn geometry.
		// With a skeleton each node's BSPs are a body on its bone, so ModelPhysics can move them with the animation (Zanzibar's wheel blades); ModelCollider puts them at their default pose.
		if ( hlmt?.CollisionModel is { } collisionTag && CollisionBsps( new CollisionModelTag( collisionTag ), model, hlmt ) is { Count: > 0 } bsps )
			AddCollisionBodies( builder, model, bsps, bones, pose, scale, BodyPhysics( hlmt, bones is not null ) );
		else if ( hlmt?.CollisionModel is not null && collisionIndices.Count > 0 )
			builder.AddCollisionMesh( collisionVertices, collisionIndices );

		if ( collisionIndices.Count > 0 )
			builder.AddTraceMesh( collisionVertices, collisionIndices );

		return builder.Create();
	}

	// A collision BSP to build, the node it moves with (-1 for none) and each of its surfaces' global material name (null for none).
	sealed record NodeCollision( int Node, CollisionBsp Bsp, string[] Materials );

	// One body per node with collision (one in model space without a skeleton), on its bone at the node's default pose, its BSPs as hulls (CollisionHulls) in the bone's space.
	// A body's surface is the global material with the most area on it; a mesh fallback keeps one per triangle. Bodies on different bones are held together as in the default pose (JoinBodies).
	static void AddCollisionBodies( ModelBuilder builder, RenderModelTag model, List<NodeCollision> bsps, string[] bones, Transform[] pose, float scale, Dictionary<int, PhysicsRigidBody> physics )
	{
		var slots = new Dictionary<string, byte>();
		byte Slot( string material )
		{
			if ( !slots.TryGetValue( material ?? "", out var slot ) )
			{
				slots[material ?? ""] = slot = (byte)Math.Min( slots.Count, 255 );
				builder.AddSurface( Halo3Surfaces.Get( material ) );
			}
			return slot;
		}

		var bodies = new Dictionary<int, int>();
		foreach ( var node in bsps.GroupBy( b => bones is null ? -1 : Math.Max( b.Node, 0 ) ) )
		{
			var bindPose = node.Key >= 0 ? pose[node.Key] : Transform.Zero;
			var areas = new Dictionary<string, float>();
			foreach ( var bsp in node )
			{
				foreach ( var (surface, i) in bsp.Bsp.Surfaces.Select( ( s, i ) => (s, i) ) )
				{
					if ( bsp.Materials[i] is { } material )
						areas[material] = areas.GetValueOrDefault( material ) + Area( bsp.Bsp, surface );
				}
			}

			var body = builder.AddBody( surface: areas.Count > 0 ? Halo3Surfaces.Get( areas.MaxBy( a => a.Value ).Key ) : null, boneName: node.Key >= 0 ? bones[node.Key] : null );
			if ( node.Key >= 0 )
				body.SetBindPose( bindPose );
			if ( physics.TryGetValue( node.Key, out var rigid ) )
			{
				body.Mass = rigid.Mass;
				body.LinearDamping = rigid.LinearDamping;
				body.AngularDamping = rigid.AngularDamping;
			}
			bodies[node.Key] = bodies.Count;

			foreach ( var bsp in node )
			{
				var nodePose = bsp.Node >= 0 ? pose[bsp.Node] : Transform.Zero;
				Vector3 ToBody( System.Numerics.Vector3 p ) => bindPose.PointToLocal( nodePose.PointToWorld( (Vector3)p * scale ) );
				CollisionHulls.Add( body, bsp.Bsp.ConvexPieces( CollisionHulls.MaximumHalfEdges ), ToBody, () =>
				{
					var indices = new List<uint>();
					var triangleSurfaces = new List<byte>();
					for ( var s = 0; s < bsp.Bsp.Surfaces.Count; s++ )
					{
						var corners = bsp.Bsp.Surfaces[s].Corners;
						for ( var i = 1; i + 1 < corners.Length; i++ )
						{
							indices.AddRange( [(uint)corners[0], (uint)corners[i], (uint)corners[i + 1]] );
							triangleSurfaces.Add( Slot( bsp.Materials[s] ) );
						}
					}
					return (bsp.Bsp.Vertices.Select( ToBody ).ToArray(), indices.ToArray(), triangleSurfaces.ToArray());
				} );
			}
		}

		if ( bones is not null )
			JoinBodies( builder, model, bodies, pose );
	}

	// The physics model's mass and damping for each node's body, keyed as AddCollisionBodies groups them (-1 without a skeleton): of each region the first permutation's rigid bodies, as CollisionBsps builds the first permutation's collision.
	static Dictionary<int, PhysicsRigidBody> BodyPhysics( ModelTag hlmt, bool skeleton )
	{
		var result = new Dictionary<int, PhysicsRigidBody>();
		if ( hlmt.PhysicsModel is not { } tag || PhysicsModelTag.For( tag ) is not { } phmo )
			return result;

		var chosen = phmo.RigidBodies.GroupBy( b => b.Region ).SelectMany( r => r.Where( b => b.Permutation == r.Min( c => c.Permutation ) ) );
		foreach ( var node in chosen.GroupBy( b => skeleton ? Math.Max( b.Node, 0 ) : -1 ) )
			result[node.Key] = node.First() with { Mass = node.Sum( b => b.Mass ) };
		return result;
	}

	// Holds a model's bodies together so ModelPhysics moves it as one rigid object (a prop spawned on its own): each body is fixed to its nearest ancestor's with one, else to the first body, as in the default pose.
	static void JoinBodies( ModelBuilder builder, RenderModelTag model, Dictionary<int, int> bodies, Transform[] pose )
	{
		int? root = null;
		foreach ( var (node, body) in bodies.OrderBy( b => b.Value ) )
		{
			var parent = model.Nodes[node].Parent;
			for ( var guard = 0; parent >= 0 && parent < model.Nodes.Count && !bodies.ContainsKey( parent ) && guard < model.Nodes.Count; guard++ )
				parent = model.Nodes[parent].Parent;

			if ( !bodies.ContainsKey( parent ) )
			{
				if ( root is null )
				{
					root = node;
					continue;
				}
				parent = root.Value;
			}

			var frame = pose[parent].ToLocal( pose[node] );
			builder.AddFixedJoint( bodies[parent], body, new Transform( frame.Position, frame.Rotation.Normal ), Transform.Zero );
		}
	}

	static float Area( CollisionBsp bsp, CollisionSurface surface )
	{
		var area = 0f;
		for ( var i = 1; i + 1 < surface.Corners.Length; i++ )
		{
			var a = bsp.Vertices[surface.Corners[0]];
			area += System.Numerics.Vector3.Cross( bsp.Vertices[surface.Corners[i]] - a, bsp.Vertices[surface.Corners[i + 1]] - a ).Length() / 2;
		}
		return area;
	}

	// Each collision region's permutation named like the render region's first permutation (else its first): its BSPs with their nodes and surfaces' global material names.
	static List<NodeCollision> CollisionBsps( CollisionModelTag collision, RenderModelTag model, ModelTag hlmt )
	{
		var result = new List<NodeCollision>();
		var globalMaterials = hlmt.GlobalMaterials();
		foreach ( var region in collision.Regions )
		{
			var shown = model.Regions.FirstOrDefault( r => r.Name == region.Name )?.Permutations.FirstOrDefault()?.Name;
			var permutation = region.Permutations.FirstOrDefault( p => p.Name == shown ) ?? region.Permutations.FirstOrDefault();
			if ( permutation is null )
				continue;

			foreach ( var bsp in permutation.Bsps )
			{
				if ( bsp.Bsp.Surfaces.Count == 0 )
					continue;

				var materials = bsp.Bsp.Surfaces.Select( s =>
					s.Material >= 0 && s.Material < collision.Materials.Count && globalMaterials.TryGetValue( collision.Materials[s.Material], out var global ) ? global : null ).ToArray();
				result.Add( new NodeCollision( bsp.Node >= 0 && bsp.Node < model.Nodes.Count ? bsp.Node : -1, bsp.Bsp, materials ) );
			}
		}

		return result;
	}

	const int MaxChoices = 64;

	// The most choices each region may have so all fit in s&box's 64 (a hideable region's "none" included).
	static int ChoicesPerRegion( RenderModelTag model, HashSet<int> hideable )
	{
		for ( var limit = model.Regions.Select( r => r.Permutations.Count ).DefaultIfEmpty( 0 ).Max(); limit > 1; limit-- )
		{
			if ( model.Regions.Sum( r => Math.Min( r.Permutations.Count, limit ) + (hideable.Contains( r.Index ) ? 1 : 0) ) <= MaxChoices )
				return limit;
		}
		return 1;
	}

	// The body group choice of a region a variant hides.
	public const string NoChoice = "none";

	// Render model regions some variant of the hlmt shows nothing for (a permutation the render model doesn't have).
	static HashSet<int> HideableRegions( ModelTag hlmt )
	{
		var result = new HashSet<int>();
		if ( hlmt is null )
			return result;
		foreach ( var variant in new ObjectModel( hlmt.Tag ).Variants )
		{
			foreach ( var region in variant.Regions )
			{
				if ( region.RenderModelRegion >= 0 && region.Permutations.Any( p => p.RenderModelPermutation < 0 ) )
					result.Add( region.RenderModelRegion );
			}
		}
		return result;
	}

	// A region's body group choice name: its permutation's name, or its index when unnamed.
	public static string ChoiceName( RenderModelRegion region, int permutation ) =>
		string.IsNullOrEmpty( region.Permutations[permutation].Name ) ? $"{permutation}" : region.Permutations[permutation].Name;

	// Models with more than one node get a skeleton, if their nodes come after their parents.
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

	// The nodes' default pose in model space (s&box units).
	// (materials agent) A mesh's vertex colours (slot 2, format 14: three floats), or null.
	static System.Numerics.Vector3[] VertexColors( RenderGeometry geometry, GeometryMesh mesh, int count )
	{
		var slot = mesh.VertexBuffers.Length > GeometryMesh.SlotVertexColor ? mesh.VertexBuffers[GeometryMesh.SlotVertexColor] : -1;
		if ( geometry.Buffer( slot ) is not { } buffer || buffer.Stride != 12 || buffer.Count < count )
			return null;
		var floats = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>( buffer.Data.Span );
		var colors = new System.Numerics.Vector3[count];
		for ( var i = 0; i < count; i++ )
			colors[i] = new System.Numerics.Vector3( floats[i * 3], floats[i * 3 + 1], floats[i * 3 + 2] );
		return colors;
	}

	// A mesh's ambient PRT (slot 3, format 9: a float per vertex, the visibility's L0 SH coefficient) as the factor MCC scales an object's probe light by: the value over the L0 basis constant, 1 unoccluded (Orbital's ad screen backings, 0.1 in their recess). Null without one; linear and quadratic PRT aren't read.
	static float[] AmbientPrt( RenderGeometry geometry, GeometryMesh mesh, int count )
	{
		var slot = mesh.PrtType == 1 && mesh.VertexBuffers.Length > GeometryMesh.SlotPrt ? mesh.VertexBuffers[GeometryMesh.SlotPrt] : -1;
		if ( geometry.Buffer( slot ) is not { Format: 9, Stride: 4 } buffer || buffer.Count < count )
			return null;
		var floats = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>( buffer.Data.Span );
		var result = new float[count];
		for ( var i = 0; i < count; i++ )
			result[i] = Math.Max( floats[i] / 0.282095f, 0 );
		return result;
	}

	public static Transform[] DefaultPose( RenderModelTag model, float scale ) =>
		model.DefaultPose().Select( p => new Transform( (Vector3)p.Translation * scale, new Rotation( p.Rotation.X, p.Rotation.Y, p.Rotation.Z, p.Rotation.W ) ) ).ToArray();

	// Render model vertices are model space in the default pose: rigid meshes go to their rigid node, skinned vertices to their nodes through the mesh's node map.
	// A pre-lighting decal shader (Halo3Materials.LitAsSurface).
	static bool IsSurfaceDecal( RenderModelTag model, int index ) => index >= 0 && index < model.Materials.Count && model.Materials[index] is { Address: not 0 } shader
		&& Halo3Materials.LitAsSurface( ShaderTag.Get( shader ) );

	// A multiply decal shader drawn in its surface's albedo (Halo3Materials.IsAlbedoDecal).
	static bool IsAlbedoDecal( RenderModelTag model, int index ) => index >= 0 && index < model.Materials.Count && model.Materials[index] is { Address: not 0 } shader
		&& Halo3Materials.IsAlbedoDecal( ShaderTag.Get( shader ) );

	// An opaque triangle (bind pose, s&box units), a sphere around it, its material index and its vertices.
	readonly record struct SurfaceTriangle( Vector3 A, Vector3 B, Vector3 C, Vector3 Center, float Radius, int Material, Halo3SkinnedVertex VA, Halo3SkinnedVertex VB, Halo3SkinnedVertex VC );

	// Every opaque, non-decal triangle of the model, or null when it has no decals drawn with the surface under them.
	static List<SurfaceTriangle> SurfaceTriangles( RenderModelTag model, RenderGeometry geometry, GeometryCompression compression, float scale )
	{
		if ( !Enumerable.Range( 0, model.Materials.Count ).Any( i => IsSurfaceDecal( model, i ) || IsAlbedoDecal( model, i ) ) )
			return null;

		var opaque = new Dictionary<int, bool>();
		bool IsOpaque( int index )
		{
			if ( opaque.TryGetValue( index, out var known ) )
				return known;
			var tag = index >= 0 && index < model.Materials.Count && model.Materials[index] is { Address: not 0 } shader ? ShaderTag.Get( shader ) : null;
			return opaque[index] = tag?.Definition is not null && tag.Kind != "decal" && tag.Option( "blend_mode" ) is null or "opaque";
		}

		var triangles = new List<SurfaceTriangle>();
		foreach ( var mesh in geometry.Meshes )
		{
			var parts = mesh.Parts.Where( p => p.IsDrawn && IsOpaque( p.Material ) ).ToList();
			if ( parts.Count == 0 || geometry.Vertices( mesh, compression ) is not { Count: > 0 } source )
				continue;
			var vertices = ReadVertices( geometry, mesh, source, model.Nodes.Count, scale );
			foreach ( var part in parts )
			{
				foreach ( var (a, b, c) in geometry.Triangles( mesh, part ) )
				{
					if ( a >= vertices.Count || b >= vertices.Count || c >= vertices.Count )
						continue;
					var (pa, pb, pc) = (vertices[a].Position, vertices[b].Position, vertices[c].Position);
					var center = (pa + pb + pc) / 3;
					var radius = MathF.Max( center.Distance( pa ), MathF.Max( center.Distance( pb ), center.Distance( pc ) ) );
					triangles.Add( new SurfaceTriangle( pa, pb, pc, center, radius, part.Material, vertices[a], vertices[b], vertices[c] ) );
				}
			}
		}
		return triangles.Count > 0 ? triangles : null;
	}

	// The opaque triangle nearest p (-1 for none).
	static int Nearest( Vector3 p, List<SurfaceTriangle> surfaces )
	{
		var best = float.MaxValue;
		var nearest = -1;
		for ( var k = 0; k < surfaces.Count; k++ )
		{
			var s = surfaces[k];
			var bound = p.Distance( s.Center ) - s.Radius;
			if ( bound > 0 && bound * bound >= best )
				continue;
			var d = p.DistanceSquared( ClosestPoint( p, s.A, s.B, s.C ) );
			if ( d < best )
			{
				best = d;
				nearest = k;
			}
		}
		return nearest;
	}

	// A multiply decal's triangles grouped by the material of the opaque triangle nearest each one's centre, each vertex moved onto that triangle's plane with its uv, normal, tangent and skinning, its own uv kept in the second set.
	static List<(int Surface, List<Halo3SkinnedVertex> Vertices)> AlbedoPieces( List<int> indices, List<Halo3SkinnedVertex> vertices, List<SurfaceTriangle> surfaces )
	{
		var groups = new Dictionary<int, List<Halo3SkinnedVertex>>();
		for ( var t = 0; t + 2 < indices.Count; t += 3 )
		{
			var p = (vertices[indices[t]].Position + vertices[indices[t + 1]].Position + vertices[indices[t + 2]].Position) / 3;
			var nearest = Nearest( p, surfaces );
			var material = nearest < 0 ? -1 : surfaces[nearest].Material;
			if ( !groups.TryGetValue( material, out var list ) )
				groups[material] = list = new List<Halo3SkinnedVertex>();
			for ( var k = 0; k < 3; k++ )
			{
				var v = vertices[indices[t + k]];
				if ( nearest >= 0 )
				{
					var s = surfaces[nearest];
					var (u, w0, w1) = Barycentric( v.Position, s.A, s.B, s.C );
					// On the surface's plane and skinned as its first vertex, so the copy lies exactly over it (the decal's own mesh floats a little off it, and lost or won the depth test by chance).
					v.Position = s.A * u + s.B * w0 + s.C * w1;
					v.BlendIndices = s.VA.BlendIndices;
					v.BlendWeights = s.VA.BlendWeights;
					v.LightmapTexCoord = v.TexCoord;
					v.TexCoord = s.VA.TexCoord * u + s.VB.TexCoord * w0 + s.VC.TexCoord * w1;
					v.Normal = (s.VA.Normal * u + s.VB.Normal * w0 + s.VC.Normal * w1).Normal;
					var tangent = ((Vector3)s.VA.Tangent * u + (Vector3)s.VB.Tangent * w0 + (Vector3)s.VC.Tangent * w1).Normal;
					v.Tangent = new Vector4( tangent, s.VA.Tangent.w );
				}
				list.Add( v );
			}
		}
		return groups.Select( g => (g.Key, g.Value) ).ToList();
	}

	// p's barycentric weights on triangle abc's plane (outside the triangle too, so a uv continues across it).
	static (float U, float V, float W) Barycentric( Vector3 p, Vector3 a, Vector3 b, Vector3 c )
	{
		var v0 = b - a;
		var v1 = c - a;
		var v2 = p - a;
		float d00 = v0.Dot( v0 ), d01 = v0.Dot( v1 ), d11 = v1.Dot( v1 ), d20 = v2.Dot( v0 ), d21 = v2.Dot( v1 );
		var denom = d00 * d11 - d01 * d01;
		if ( MathF.Abs( denom ) < 1e-12f )
			return (1, 0, 0);
		var v = (d11 * d20 - d01 * d21) / denom;
		var w = (d00 * d21 - d01 * d20) / denom;
		return (1 - v - w, v, w);
	}

	// A decal's triangles grouped by the material of the opaque triangle nearest each one's centre.
	static List<(int Surface, List<int> Indices)> SplitBySurface( List<int> indices, List<Halo3SkinnedVertex> vertices, List<SurfaceTriangle> surfaces )
	{
		var groups = new Dictionary<int, List<int>>();
		for ( var t = 0; t + 2 < indices.Count; t += 3 )
		{
			var p = (vertices[indices[t]].Position + vertices[indices[t + 1]].Position + vertices[indices[t + 2]].Position) / 3;
			var nearest = Nearest( p, surfaces );
			var material = nearest < 0 ? -1 : surfaces[nearest].Material;
			if ( !groups.TryGetValue( material, out var list ) )
				groups[material] = list = new List<int>();
			list.Add( indices[t] );
			list.Add( indices[t + 1] );
			list.Add( indices[t + 2] );
		}
		return groups.Select( g => (g.Key, g.Value) ).ToList();
	}

	// The point of triangle abc nearest p (Ericson, Real-Time Collision Detection 5.1.5).
	static Vector3 ClosestPoint( Vector3 p, Vector3 a, Vector3 b, Vector3 c )
	{
		var ab = b - a;
		var ac = c - a;
		var ap = p - a;
		float d1 = ab.Dot( ap ), d2 = ac.Dot( ap );
		if ( d1 <= 0 && d2 <= 0 )
			return a;
		var bp = p - b;
		float d3 = ab.Dot( bp ), d4 = ac.Dot( bp );
		if ( d3 >= 0 && d4 <= d3 )
			return b;
		var vc = d1 * d4 - d3 * d2;
		if ( vc <= 0 && d1 >= 0 && d3 <= 0 )
			return a + ab * (d1 / (d1 - d3));
		var cp = p - c;
		float d5 = ab.Dot( cp ), d6 = ac.Dot( cp );
		if ( d6 >= 0 && d5 <= d6 )
			return c;
		var vb = d5 * d2 - d1 * d6;
		if ( vb <= 0 && d2 >= 0 && d6 <= 0 )
			return a + ac * (d2 / (d2 - d6));
		var va = d3 * d6 - d5 * d4;
		if ( va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0 )
			return b + (c - b) * ((d4 - d3) / ((d4 - d3) + (d5 - d6)));
		var denom = 1 / (va + vb + vc);
		return a + ab * (vb * denom) + ac * (vc * denom);
	}

	static List<Halo3SkinnedVertex> ReadVertices( RenderGeometry geometry, GeometryMesh mesh, GeometryVertices source, int nodeCount, float scale )
	{
		nodeCount = Math.Max( 1, nodeCount );
		var map = mesh.Index < geometry.NodeMaps.Count ? geometry.NodeMaps[mesh.Index] : Array.Empty<byte>();
		int Node( int raw )
		{
			var node = map.Length > 0 ? raw < map.Length ? map[raw] : 0 : raw;
			return node < nodeCount ? node : 0;
		}

		var rigidNode = mesh.RigidNode >= 0 && mesh.RigidNode < nodeCount ? mesh.RigidNode : 0;
		var vertices = new List<Halo3SkinnedVertex>( source.Count );
		for ( var i = 0; i < source.Count; i++ )
		{
			var normal = source.Normals[i];
			var tangent = source.Tangents?[i] ?? System.Numerics.Vector3.UnitX;
			var binormal = source.Binormals?[i] ?? System.Numerics.Vector3.UnitY;

			var indices = new Color32( (byte)rigidNode, 0, 0, 0 );
			var weights = new Color32( 255, 0, 0, 0 );
			if ( source.NodeIndices is { } nodes && source.NodeWeights is { } w )
			{
				// Weights are out of 255 and sum to 254-256; the first takes up the difference.
				int w1 = w[i * 4 + 1], w2 = w[i * 4 + 2], w3 = w[i * 4 + 3];
				var w0 = Math.Clamp( 255 - w1 - w2 - w3, 0, 255 );
				indices = new Color32( (byte)Node( nodes[i * 4] ), (byte)Node( nodes[i * 4 + 1] ), (byte)Node( nodes[i * 4 + 2] ), (byte)Node( nodes[i * 4 + 3] ) );
				weights = new Color32( (byte)w0, (byte)w1, (byte)w2, (byte)w3 );
			}

			vertices.Add( new Halo3SkinnedVertex
			{
				Position = (Vector3)source.Positions[i] * scale,
				Normal = normal,
				Tangent = Halo3Vertex.Tangent4( normal, tangent, binormal ),
				TexCoord = source.TexCoords[i],
				BlendIndices = indices,
				BlendWeights = weights,
			} );
		}

		return vertices;
	}

	static Mesh EmptyMesh( string name, Material material )
	{
		var mesh = new Mesh( name, material );
		mesh.CreateVertexBuffer( 3, new List<Halo3Vertex> { default, default, default } );
		mesh.CreateIndexBuffer( 3, new List<int> { 0, 1, 2 } );
		mesh.Bounds = new BBox( Vector3.Zero, Vector3.Zero );
		return mesh;
	}

	// Adds the nodes as bones, parents first, each bounded by the vertices it moves (a skinned model's render bounds are its bones' bounds in their current pose).
	static void AddBones( ModelBuilder builder, RenderModelTag model, string[] names, Transform[] pose, List<Halo3SkinnedVertex> vertices )
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
			var parent = model.Nodes[i].Parent;
			builder.AddBone( names[i], pose[i].Position, pose[i].Rotation, bounds[i] ?? new BBox( Vector3.Zero, Vector3.Zero ), parent < 0 ? null : names[parent] );
		}
	}

	// Markers as attachments on their node's bone (or placed by the node's default pose without a skeleton); repeated names get _1, _2...
	static void AddMarkers( ModelBuilder builder, RenderModelTag model, string[] bones, Transform[] pose, float scale )
	{
		var taken = new HashSet<string>( bones ?? [], StringComparer.OrdinalIgnoreCase );
		var counts = new Dictionary<string, int>( StringComparer.OrdinalIgnoreCase );
		foreach ( var marker in model.Markers )
		{
			if ( string.IsNullOrEmpty( marker.Name ) )
				continue;

			var n = counts.GetValueOrDefault( marker.Name );
			counts[marker.Name] = n + 1;
			var name = n == 0 ? marker.Name : $"{marker.Name}_{n}";
			if ( !taken.Add( name ) )
				continue;

			var node = marker.Node >= 0 && marker.Node < pose.Length ? marker.Node : 0;
			var local = new Transform( (Vector3)marker.Translation * scale, new Rotation( marker.Rotation.X, marker.Rotation.Y, marker.Rotation.Z, marker.Rotation.W ) );
			if ( bones is not null )
				builder.AddAttachment( name, local.Position, local.Rotation, bones[node] );
			else
			{
				var world = pose.Length > 0 ? pose[node].ToWorld( local ) : local;
				builder.AddAttachment( name, world.Position, world.Rotation );
			}
		}
	}
}

// Loads a model tag (hlmt) as mount://halo3/<hlmt path>.vmdl, from the first of its maps whose render model geometry isn't stripped. Builds on the main thread, like the other mounts' models.
sealed class RenderModelLoader( IReadOnlyList<(string MapPath, uint TagId)> sources ) : Halo3Loader
{
	protected override Task<object> LoadAsync()
	{
		// On the main thread, build right here: awaiting would make a blocking caller pump the main thread queue in the middle of whatever it's doing.
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
		foreach ( var (mapPath, tagId) in sources )
		{
			var tag = Host.GetCache( mapPath ).GetTag( tagId );
			if ( tag is null || new ModelTag( tag ).RenderModel is not { } renderModel )
				continue;

			try
			{
				if ( RenderModelBuilder.Build( Path, new RenderModelTag( renderModel ), new ModelTag( tag ), Halo3Mount.WorldScale ) is { } model )
					return model;
			}
			catch ( Exception e )
			{
				Host.Logger.Warning( e, $"Couldn't build {tag}: {e.Message}" );
			}
		}

		return null;
	}
}
