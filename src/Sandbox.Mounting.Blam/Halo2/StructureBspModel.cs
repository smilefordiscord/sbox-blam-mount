using Blam.Halo2;
using Blam.Halo2.Tags;

namespace Sandbox.Mounting.Blam.Halo2;

/// <summary>
/// Turns a Halo 2 structure BSP into a model: its clusters, and every instanced geometry definition where its
/// instances place it, each with its lightmap page. It collides like Halo: with the BSP's collision BSP and each
/// instance's own, whose surfaces carry Halo 2's global materials (see <see cref="Halo2Surfaces"/>).
/// </summary>
/// <remarks>
/// Instanced geometry is lit by its own lightmap page or, for instances lit per vertex, by a colour per vertex
/// from the lightmap's geometry buckets, which also hold every instance's incident light directions. For now
/// only opaque templates are drawn.
/// </remarks>
static class StructureBspModel
{
	// The BSP as built: its collision, and the models drawing it, one per cluster (with the instances whose centre is in it) and one per instance in no cluster, so the engine's visibility culls them by cluster (see SourceVisibility).
	public sealed record Parts( Model Collision, List<Model> Pieces, BBox Bounds );

	/// <param name="globals">The map's globals, which name the collision surfaces' materials; null for none.</param>
	public static Parts Build( string name, StructureBspTag bsp, LightmapTag lightmap, GlobalsTag globals )
	{
		var builder = Model.Builder.WithName( name );
		var pieces = new SortedDictionary<int, List<Mesh>>();
		BBox? bounds = null;
		var collisionVertices = new List<Vector3>();
		var collisionIndices = new List<int>();
		var materials = new Dictionary<(int Material, int Page), Material>();
		var shaders = new Dictionary<int, ShaderTag>();
		var meshCount = 0;

		// page: the lightmap page, or PerVertexLit, or -1 for none. lodSphere: what MCC sizes the section by for shader
		// LODs (world units). inFog: the section is in the BSP's planar fog.
		void Add( int piece, GeometrySection section, InstancedGeometryInstance instance, int page, List<LevelVertex> vertices, (System.Numerics.Vector3 Center, float Radius) lodSphere, bool inFog )
		{
			foreach ( var group in section.Parts.Where( p => p.IsDrawn ).GroupBy( p => p.Material ) )
			{
				if ( group.Key < 0 || group.Key >= bsp.Materials.Count || bsp.Materials[group.Key] is null )
					continue;

				if ( !shaders.TryGetValue( group.Key, out var shader ) )
					shaders[group.Key] = shader = new ShaderTag( bsp.Materials[group.Key] );
				if ( !OpaqueMaterial.IsSupported( shader ) )
					continue;

				var indices = new List<int>();
				var lodVertices = new List<LevelVertex>( vertices );
				var lodCenter = (Vector3)lodSphere.Center * Halo2Mount.WorldScale;
				foreach ( var part in group )
				{
					// Each part's vertices carry its LOD magic number (vertices are shared by a section's parts).
					var lodSize = new Vector3( lodSphere.Radius * Halo2Mount.WorldScale, part.LodMagic > 0 ? part.LodMagic : 1, inFog ? 1 : 0 );
					foreach ( var (a, b, c) in section.Triangles( part ) )
					{
						foreach ( var index in new[] { a, b, c } )
						{
							var vertex = lodVertices[index];
							vertex.LodCenter = lodCenter;
							vertex.LodSize = lodSize;
							lodVertices[index] = vertex;
						}
					}

					// Halo 2 winds front faces counter-clockwise, as s&box does (Halo CE's BSP surfaces are the other way).
					foreach ( var (a, b, c) in section.Triangles( part ) )
					{
						indices.Add( a );
						indices.Add( b );
						indices.Add( c );
					}
				}

				if ( indices.Count == 0 )
					continue;

				if ( !materials.TryGetValue( (group.Key, page), out var material ) )
				{
					var lightmapTexture = page >= 0 && lightmap?.Bitmaps is not null
						? Texture.Load( $"mount://halo2/{Halo2Mount.TextureName( lightmap.Bitmaps.Path, page )}.vtex" )
						: null;
					var suffix = page == PerVertexLit ? "vertex" : page.ToString();
					var materialName = $"{System.IO.Path.ChangeExtension( name, null )}/{Halo2Mount.ResourcePath( shader.Tag.Path )}_{suffix}.vmat";
					materials[(group.Key, page)] = material = OpaqueMaterial.Create( materialName, shader, lightmapTexture, page == PerVertexLit, levelLod: true );
				}

				var mesh = new Mesh( material );
				mesh.CreateVertexBuffer( lodVertices.Count, lodVertices );
				mesh.CreateIndexBuffer( indices.Count, indices );
				var meshBounds = BBox.FromPoints( indices.Select( i => vertices[i].Position ) );
				mesh.Bounds = meshBounds;
				if ( !pieces.TryGetValue( piece, out var list ) )
					pieces[piece] = list = new();
				list.Add( mesh );
				bounds = bounds?.AddBBox( meshBounds ) ?? meshBounds;
				meshCount++;

				var collisionBase = collisionVertices.Count;
				collisionVertices.AddRange( vertices.Select( v => v.Position ) );
				collisionIndices.AddRange( indices.Select( i => collisionBase + i ) );
			}
		}

		for ( var i = 0; i < bsp.Clusters.Count; i++ )
		{
			var cluster = bsp.Clusters[i];
			if ( cluster.IsEmpty )
				continue;

			var page = Page( lightmap?.ClusterPages, i );
			Add( i, cluster, null, page, ReadVertices( cluster, null, page >= 0, default, default ), bsp.ClusterBounds[i], bsp.ClusterPlanarFog[i] >= 0 );
		}

		// An instance's cluster isn't stored with it: taken as in the fog when its centre is inside a fog cluster's bounds.
		bool InPlanarFog( InstancedGeometryInstance instance ) => Enumerable.Range( 0, bsp.Clusters.Count )
			.Any( c => bsp.ClusterPlanarFog[c] >= 0 && System.Numerics.Vector3.Distance( instance.BoundingCenter, bsp.ClusterBounds[c].Center ) <= bsp.ClusterBounds[c].Radius );

		foreach ( var instance in bsp.Instances )
		{
			if ( instance.Definition < 0 || instance.Definition >= bsp.Definitions.Count || bsp.Definitions[instance.Definition] is not { IsEmpty: false } definition )
				continue;

			// Instances get their incident directions from the lightmap's geometry buckets, and the ones lit per
			// vertex (no page) their light colour too.
			var page = Page( lightmap?.InstancePages, instance.Index );
			var (incident, colors) = lightmap?.InstanceLighting( instance.Index, definition.VertexCount ) ?? default;
			if ( page < 0 && colors.Array is not null )
				page = PerVertexLit;

			// MCC sizes small instances up (radius under 0.13 world units: 1.75 times the projected size).
			var radius = instance.BoundingRadius * (instance.BoundingRadius < 0.13f ? 1.75f : 1f);
			// An instance draws with the cluster its centre is in, or alone.
			var instanceCluster = global::Blam.Halo2.WorldVisibility.ClusterAt( bsp, instance.BoundingCenter );
			Add( instanceCluster >= 0 ? instanceCluster : -1 - instance.Index, definition, instance, page, ReadVertices( definition, instance, page >= 0 || page == PerVertexLit, incident, colors ), (instance.BoundingCenter, radius),
				InPlanarFog( instance ) );
		}

		if ( meshCount == 0 )
			return null;

		// Collide with Halo's own collision geometry, which includes invisible walls and leaves out decorative
		// detail, and say what each triangle is made of. Fall back to the drawn geometry if a BSP has none.
		var (haloVertices, haloIndices, triangleMaterials) = ReadCollision( bsp, globals );
		if ( haloIndices.Count > 0 )
		{
			collisionVertices = haloVertices;
			collisionIndices = haloIndices;
		}
		else
			triangleMaterials = Enumerable.Repeat<string>( null, collisionIndices.Count / 3 ).ToList();

		builder.AddCollisionMesh( collisionVertices, collisionIndices, Halo2Surfaces.AddSurfaces( builder, triangleMaterials ) );
		builder.AddTraceMesh( collisionVertices, collisionIndices );

		var baseName = System.IO.Path.ChangeExtension( name, null );
		var models = new List<Model>();
		foreach ( var (piece, meshes) in pieces )
		{
			var pieceBuilder = Model.Builder.WithName( piece >= 0 ? $"{baseName}_cluster_{piece}.vmdl" : $"{baseName}_instance_{-1 - piece}.vmdl" );
			foreach ( var mesh in meshes )
				pieceBuilder.AddMesh( mesh );
			models.Add( pieceBuilder.Create() );
		}
		return new Parts( builder.Create(), models, bounds.Value );
	}

	/// <summary>
	/// The BSP's transparent and plasma surfaces (glass, lights, grav lifts, gas, clouds...), one model per mesh
	/// (a material's triangles in one cluster or placed instance), each bounded by its own triangles.
	/// </summary>
	/// <remarks>
	/// s&amp;box draws translucent objects farthest first by the distance from the camera to their bounds, and
	/// objects whose bounds contain the camera last; it doesn't promise an order for the meshes of one model. In the
	/// BSP's model, whose bounds hold the camera, these would draw after every other translucent thing (the Halo CE
	/// mount's lesson with glass, see Halo1/StructureBspModel). As objects of their own they sort back to front with
	/// everything else, close to Halo 2's sorted transparent layer. No collision here: walkable glass gets it from
	/// the level's collision.
	/// </remarks>
	/// <param name="name">The BSP model's name (the parts are named after it).</param>
	public static List<Model> BuildTransparent( string name, StructureBspTag bsp )
	{
		var models = new List<Model>();
		var baseName = System.IO.Path.ChangeExtension( name, null );
		var materials = new Dictionary<int, Material>();
		var shaders = new Dictionary<int, ShaderTag>();

		void Add( GeometrySection section, InstancedGeometryInstance instance )
		{
			if ( section.IsEmpty )
				return;

			List<LevelVertex> vertices = null;
			foreach ( var group in section.Parts.Where( p => p.IsDrawn ).GroupBy( p => p.Material ) )
			{
				if ( group.Key < 0 || group.Key >= bsp.Materials.Count || bsp.Materials[group.Key] is null )
					continue;

				if ( !shaders.TryGetValue( group.Key, out var shader ) )
					shaders[group.Key] = shader = new ShaderTag( bsp.Materials[group.Key] );
				if ( Halo2Materials.KindOf( shader ) is not (Halo2MaterialKind.Transparent or Halo2MaterialKind.Plasma or Halo2MaterialKind.Water) )
					continue;

				var indices = new List<int>();
				foreach ( var part in group )
				{
					foreach ( var (a, b, c) in section.Triangles( part ) )
					{
						indices.Add( a );
						indices.Add( b );
						indices.Add( c );
					}
				}

				if ( indices.Count == 0 )
					continue;

				if ( !materials.TryGetValue( group.Key, out var material ) )
					materials[group.Key] = material = Halo2Materials.Create( $"{baseName}/{Halo2Mount.ResourcePath( shader.Tag.Path )}.vmat", shader,
						water: bsp.WaterDefinitions.FirstOrDefault() );
				if ( material is null )
					continue;

				vertices ??= ReadVertices( section, instance, false, default, default );
				var mesh = new Mesh( material );
				mesh.CreateVertexBuffer( vertices.Count, vertices );
				mesh.CreateIndexBuffer( indices.Count, indices );
				mesh.Bounds = BBox.FromPoints( indices.Select( i => vertices[i].Position ) );
				models.Add( Model.Builder.WithName( $"{baseName}_transparent_{models.Count}.vmdl" ).AddMesh( mesh ).Create() );
			}
		}

		foreach ( var cluster in bsp.Clusters )
			Add( cluster, null );

		foreach ( var instance in bsp.Instances )
		{
			if ( instance.Definition >= 0 && instance.Definition < bsp.Definitions.Count )
				Add( bsp.Definitions[instance.Definition], instance );
		}

		return models;
	}

	/// <summary>
	/// The open water of the BSP's water definition (oceans: Zanzibar's, Needle's, Dune's...) as a model, or null if
	/// it has none: a flat grid at the definition's height reaching <see cref="OpenWaterReach"/> (or its far distance,
	/// if more) from the origin in x and y, where Halo projected a grid around the camera out to its far distance.
	/// </summary>
	/// <remarks>
	/// Halo draws water after the level and the sky and before anything transparent. s&amp;box sorts translucent
	/// objects by the distance from the camera to their bounds (farthest first), and a plane under the camera would
	/// be the nearest of all: drawn last, over the waves breaking on it. So the mesh's bounds are a slab sunk
	/// <see cref="OpenWaterSortDepth"/> below the plane, as wide: farther than anything, so it draws first. Seen at a
	/// shallow angle the slab is out of view while the plane isn't, so in live scenes
	/// <see cref="SceneUpkeep"/> moves the bounds in front of the camera every frame, as far away, and puts the
	/// sky's layers farther still (Halo draws the sky first). The water writes depth (halo2_water.shader's
	/// F_DEPTH_WRITE), so what is transparent under it stays hidden.
	/// </remarks>
	/// <param name="name">The BSP model's name (the water is named after it).</param>
	/// <param name="refraction">What the water shows through it (see <see cref="WaterMaterial.Create"/>).</param>
	public static Model BuildOpenWater( string name, StructureBspTag bsp, Vector3? refraction )
	{
		var water = bsp.WaterDefinitions.FirstOrDefault();
		if ( water?.Shader is null )
			return null;

		var shader = new ShaderTag( water.Shader );
		if ( !WaterMaterial.IsOpenWater( shader ) )
			return null;

		var baseName = System.IO.Path.ChangeExtension( name, null );
		var material = WaterMaterial.Create( $"{baseName}/{Halo2Mount.ResourcePath( shader.Tag.Path )}_open.vmat", shader, water, depthWrite: true, refraction: refraction );
		if ( material is null )
			return null;

		const int cells = 32;
		var radius = MathF.Max( water.FarZ, OpenWaterReach ) * Halo2Mount.WorldScale;
		var height = water.Height * Halo2Mount.WorldScale;
		var vertices = new List<LevelVertex>();
		for ( var y = 0; y <= cells; y++ )
		for ( var x = 0; x <= cells; x++ )
		{
			var position = new Vector3( (x * 2f / cells - 1) * radius, (y * 2f / cells - 1) * radius, height );
			vertices.Add( new LevelVertex
			{
				Position = position,
				Normal = Vector3.Up,
				Tangent = new Vector4( 1, 0, 0, 1 ),
				TexCoord = new Vector2( position.x, position.y ) / Halo2Mount.WorldScale,
			} );
		}

		// Counter-clockwise from above.
		var indices = new List<int>();
		for ( var y = 0; y < cells; y++ )
		for ( var x = 0; x < cells; x++ )
		{
			var i = y * (cells + 1) + x;
			indices.AddRange( [i, i + 1, i + cells + 2, i, i + cells + 2, i + cells + 1] );
		}

		var sunk = height - OpenWaterSortDepth * Halo2Mount.WorldScale;
		var mesh = new Mesh( material );
		mesh.CreateVertexBuffer( vertices.Count, vertices );
		mesh.CreateIndexBuffer( indices.Count, indices );
		mesh.Bounds = new BBox( new Vector3( -radius, -radius, sunk - 1 ), new Vector3( radius, radius, sunk ) );
		return Model.Builder.WithName( $"{baseName}_open_water.vmdl" ).AddMesh( mesh ).Create();
	}

	/// <summary>
	/// How far the open water reaches from the origin at least (world units). Halo's grid went out to the definition's
	/// far distance around the camera, under a sky drawn around the camera that met it; ours is fixed, and the sky is
	/// a model out in the world, its rim a little below the horizon. Water stopping at the far distance (Dune's 512)
	/// left a band of nothing between them; this is past any sky's rim (Dune's is 3467 out).
	/// </summary>
	const float OpenWaterReach = 4096;

	/// <summary>
	/// How far below the open water its bounds are (world units), so it sorts before every other translucent
	/// object (see <see cref="BuildOpenWater"/>): more than any map is across.
	/// </summary>
	public const float OpenWaterSortDepth = 300;

	/// <summary>
	/// The BSP's collision BSP and every instance's own (placed by the instance) as triangles, with each triangle's
	/// global material name (null for none).
	/// </summary>
	static (List<Vector3> Vertices, List<int> Indices, List<string> Materials) ReadCollision( StructureBspTag bsp, GlobalsTag globals )
	{
		var vertices = new List<Vector3>();
		var indices = new List<int>();
		var materials = new List<string>();

		string MaterialName( int collisionMaterial )
		{
			if ( collisionMaterial < 0 || collisionMaterial >= bsp.CollisionMaterials.Count || globals is null )
				return null;

			var global = bsp.CollisionMaterials[collisionMaterial].GlobalMaterial;
			return global >= 0 && global < globals.Materials.Count ? globals.Materials[global].Name : null;
		}

		void Add( CollisionBsp collision, InstancedGeometryInstance instance )
		{
			var surfaces = new List<CollisionSurface>();
			var triangles = collision.Triangles( surfaces );
			var first = vertices.Count;
			foreach ( var v in collision.Vertices )
				vertices.Add( (Vector3)(instance is null ? v : instance.Transform( v )) * Halo2Mount.WorldScale );

			// A mirroring instance turns its triangles inside out; keep them facing out.
			var mirrored = instance is not null && System.Numerics.Vector3.Dot( System.Numerics.Vector3.Cross( instance.Forward, instance.Left ), instance.Up ) < 0;
			for ( var i = 0; i + 2 < triangles.Count; i += 3 )
			{
				indices.Add( first + triangles[i] );
				indices.Add( first + triangles[mirrored ? i + 2 : i + 1] );
				indices.Add( first + triangles[mirrored ? i + 1 : i + 2] );
				materials.Add( MaterialName( surfaces[i / 3].Material ) );
			}
		}

		if ( bsp.Collision is not null )
			Add( bsp.Collision, null );

		foreach ( var instance in bsp.Instances )
		{
			if ( instance.Definition >= 0 && instance.Definition < bsp.DefinitionCollision.Count && bsp.DefinitionCollision[instance.Definition] is { } collision )
				Add( collision, instance );
		}

		return (vertices, indices, materials);
	}

	/// <summary>
	/// The page number standing for geometry lit per vertex (from a lightmap bucket) rather than by a page.
	/// </summary>
	const int PerVertexLit = -2;

	static int Page( IReadOnlyList<int> pages, int index ) => pages is not null && index < pages.Count ? pages[index] : -1;

	/// <param name="lit">Whether the geometry has lightmap lighting (a page or per-vertex colours); without, it's lit neutrally.</param>
	/// <param name="incident">For an instance, its incident directions from the lightmap (world space).</param>
	/// <param name="colors">For an instance lit per vertex, its lightmap colours.</param>
	static List<LevelVertex> ReadVertices( GeometrySection section, InstancedGeometryInstance instance, bool lit, ArraySegment<System.Numerics.Vector3> incident, ArraySegment<System.Numerics.Vector3> colors )
	{
		var vertices = new List<LevelVertex>( section.VertexCount );
		for ( var i = 0; i < section.VertexCount; i++ )
		{
			var position = section.Positions[i];
			var normal = section.Normals?[i] ?? System.Numerics.Vector3.UnitZ;
			var tangent = section.Tangents?[i] ?? System.Numerics.Vector3.UnitX;
			var binormal = section.Binormals?[i] ?? System.Numerics.Vector3.UnitY;

			// Clusters carry their own incident directions; instances' come from the lightmap, already in world
			// space. Their length says how directional the light is.
			var direction = !lit ? default : incident.Array is not null ? incident[i] : section.IncidentDirections?[i] ?? default;

			if ( instance is not null )
			{
				position = instance.Transform( position );
				normal = instance.TransformDirection( normal );
				tangent = instance.TransformDirection( tangent );
				binormal = instance.TransformDirection( binormal );
			}

			vertices.Add( new LevelVertex
			{
				Position = (Vector3)position * Halo2Mount.WorldScale,
				Normal = normal,
				Tangent = LevelVertex.Tangent4( normal, tangent, binormal ),
				TexCoord = section.TexCoords?[i] ?? default,
				LightmapTexCoord = lit ? section.LightmapTexCoords?[i] ?? default : default,
				IncidentLight = direction,
				LightmapColor = colors.Array is not null ? colors[i] : default,
			} );
		}

		return vertices;
	}
}
