using Blam.Halo1;
using Blam.Halo1.Tags;

namespace Sandbox.Mounting.Blam.Halo1;

/// <summary>
/// Turns a structure BSP's render geometry into a model per material, and its collision into a
/// model of its own. The collision carries Halo's material types as surfaces, per triangle (see
/// <see cref="HaloSurfaces"/>).
/// </summary>
/// <remarks>
/// Water is drawn separately (<see cref="BuildSeparate"/>), because it has to draw before every
/// translucent surface on or above it: it writes what's behind it out opaque, fogged by the scene
/// depth (see halo1_glass_water.shader), so anything drawn before it is overwritten, or fogged away
/// as if it lay on the sea floor. That includes the surfaces flagged "draw before water" (b30's
/// surf, a30's river foam), which Halo drew first and blended the water over.
///
/// s&amp;box draws translucent objects farthest first by the distance from the camera to their
/// bounds, and objects whose bounds contain the camera last. It doesn't promise to keep the order of
/// a model's meshes, and it breaks ties between objects by their slots in the render world, which
/// move whenever an object is removed.
/// So the only dependable order is by distance: each water mesh's bounds are a thin slab at its
/// surface, never containing a camera above (or well below) it, while the BSP's translucent
/// materials are bounded by the whole BSP, containing the camera, so the BSP's translucent surfaces
/// and the fog boxes always come after the water.
///
/// The "draw before water" surfaces are separate too, each bounded by its own surface, which lies
/// on the water a little above it: they come after the water, and before everything whose bounds
/// contain the camera (the level's particles, effects), as Halo drew them before every other
/// transparent. In the BSP they tied with the level's particles and could draw over them: a30's
/// river foam cut the waterfall's spray off in a straight line along the pool.
///
/// Glass draws what's behind it the same way, so it's separate too, each mesh with its own bounds:
/// in the BSP it drew after everything (b30's map room hologram vanished over the glass floor), as
/// its own object it sorts back to front with everything else, like Halo's transparent geometry.
///
/// Breakable surfaces (glass floors and panes, c20's grates) are separate as well, drawn and
/// collided with apart from the rest (<see cref="BuildBreakableCollision"/>), so a game can take
/// one away when it breaks, as Halo stops drawing and colliding with it.
/// </remarks>
static class StructureBspModel
{
	/// <summary>
	/// The BSP as built: its collision, a model per material drawing the material's visible
	/// surfaces (see <see cref="ClusterVisibility"/>), and its visibility.
	/// </summary>
	public sealed record Parts( Model Collision, List<Model> Materials, BspDrawing Drawing );

	/// <summary>
	/// Builds the BSP a few surfaces a slice: yields null each time the slice is due, then the
	/// parts, if the BSP has anything to draw.
	/// </summary>
	/// <remarks>
	/// A translucent material's model (one that isn't solid) is bounded by the whole BSP: its bounds
	/// contain the camera, so it draws after the water (see above).
	/// </remarks>
	public static IEnumerable<Parts> Build( HaloCeMount host, string name, CacheFile cache, StructureBspTag bsp, BuildSlice slice )
	{
		var drawing = new BspDrawing( bsp.ReadVisibility(), bsp.Surfaces.Length / 3 );
		var meshes = new List<(Mesh Mesh, BspMaterial Material, int[] Indices)>();
		var clusterMeshes = new SortedDictionary<int, List<Mesh>>();
		var surfaceClusters = SurfaceClusters( drawing.View.Data, bsp.Surfaces.Length / 3 );
		var collisionVertices = new List<Vector3>();
		var collisionIndices = new List<int>();
		var collisionTypes = new List<MaterialType?>();
		BBox? bounds = null;

		foreach ( var (lightmap, material, lightmapTexture) in Surfaces( host, bsp ) )
		{
			if ( DrawsSeparately( cache, material ) )
				continue;
			if ( slice.Due )
				yield return null;

			var (vertices, indices) = ReadGeometry( bsp, material );
			var meshBounds = BBox.FromPoints( vertices.Select( v => v.Position ) );
			bounds = bounds?.AddBBox( meshBounds ) ?? meshBounds;

			if ( !HaloMaterials.IsSolid( material.Shader.Class ) )
			{
				meshes.Add( (CreateMesh( host, name, cache, lightmap, material, lightmapTexture, vertices, indices ), material, indices.ToArray()) );
				continue;
			}

			var collisionBase = collisionVertices.Count;
			foreach ( var vertex in vertices )
				collisionVertices.Add( vertex.Position );
			foreach ( var index in indices )
				collisionIndices.Add( collisionBase + index );

			var type = MaterialTypes.OfShader( cache, material.Shader );
			collisionTypes.AddRange( Enumerable.Repeat( type, indices.Count / 3 ) );

			// Opaque surfaces go in their cluster's model, which the engine's visibility culls with the cluster.
			var renderMaterial = CreateMaterial( host, name, cache, lightmap, material, lightmapTexture );
			foreach ( var group in Enumerable.Range( 0, indices.Count / 3 ).GroupBy( t => surfaceClusters[material.FirstSurface + t] ) )
			{
				if ( !clusterMeshes.TryGetValue( group.Key, out var list ) )
					clusterMeshes[group.Key] = list = new();
				list.Add( CreateMesh( renderMaterial, vertices, group.SelectMany( t => new[] { indices[t * 3], indices[t * 3 + 1], indices[t * 3 + 2] } ).ToList() ) );
			}
		}

		if ( meshes.Count == 0 && clusterMeshes.Count == 0 )
			yield break;
		if ( slice.Due )
			yield return null;

		// Collide with Halo's own collision geometry, which includes invisible walls and leaves out
		// decorative detail. Fall back to the visible geometry if a BSP has none.
		var triangleMaterials = new List<int>();
		var triangleFlags = new List<byte>();
		var triangleBreakables = new List<int>();
		List<bool> climbable = null;
		var (haloVertices, haloIndices) = bsp.ReadCollision( triangleMaterials, triangleFlags, triangleBreakables );
		if ( haloIndices.Count > 0 )
		{
			// The breakable surfaces collide apart (see BuildBreakableCollision).
			var kept = Enumerable.Range( 0, haloIndices.Count / 3 ).Where( t => triangleBreakables[t] < 0 ).ToList();
			collisionVertices = haloVertices.Select( v => (Vector3)v * HaloCeMount.WorldScale ).ToList();
			collisionIndices = kept.SelectMany( t => new[] { haloIndices[t * 3], haloIndices[t * 3 + 1], haloIndices[t * 3 + 2] } ).ToList();
			collisionTypes = kept.Select( t => bsp.GetCollisionMaterialType( triangleMaterials[t] ) ).ToList();
			climbable = kept.Select( t => (triangleFlags[t] & 4) != 0 ).ToList();
		}

		// Each triangle gets the surface of its material type (see HaloSurfaces), so traces against
		// the level report what they hit, and ladders (surfaces flagged climbable) the climbable
		// variant. Surfaces without a material (the sky's walls and ceiling) get the default surface.
		var builder = Model.Builder.WithName( name );
		builder.AddCollisionMesh( collisionVertices, collisionIndices, HaloSurfaces.AddSurfaces( builder, collisionTypes, climbable ) );
		builder.AddTraceMesh( collisionVertices, collisionIndices );
		var collision = builder.Create();

		var baseName = System.IO.Path.ChangeExtension( name, null );
		var models = new List<Model>();
		foreach ( var (cluster, clusterMeshList) in clusterMeshes )
		{
			var clusterBuilder = Model.Builder.WithName( cluster >= 0 ? $"{baseName}_cluster_{cluster}.vmdl" : $"{baseName}_unclustered.vmdl" );
			foreach ( var mesh in clusterMeshList )
				clusterBuilder.AddMesh( mesh );
			var model = clusterBuilder.Create();
			ClusterVisibility.Register( model, drawing );
			models.Add( model );
		}

		// Translucent materials stay whole, bounded by the whole BSP so they sort after the water (see above); their visible surfaces are picked here.
		foreach ( var (mesh, material, indices) in meshes )
		{
			mesh.Bounds = bounds.Value;
			var model = Model.Builder.WithName( $"{baseName}_surfaces_{models.Count}.vmdl" ).AddMesh( mesh ).Create();
			ClusterVisibility.Register( model, new BspMaterialSurfaces( drawing, mesh, material.FirstSurface, indices ) );
			models.Add( model );
		}

		yield return new Parts( collision, models, drawing );
	}

	// The cluster each surface is drawn with: the first whose subclusters list it, or -1.
	static int[] SurfaceClusters( BspVisibility visibility, int surfaceCount )
	{
		var clusters = new int[surfaceCount];
		Array.Fill( clusters, -1 );
		for ( var c = 0; c < visibility.Clusters.Count; c++ )
		{
			foreach ( var subcluster in visibility.Clusters[c].Subclusters )
			{
				foreach ( var surface in subcluster.Surfaces )
				{
					if ( (uint)surface < (uint)surfaceCount && clusters[surface] < 0 )
						clusters[surface] = c;
				}
			}
		}
		return clusters;
	}

	/// <summary>
	/// Water and glass, which draw what's behind them, the surfaces drawn before water, and breakable
	/// surfaces go in objects of their own (see above).
	/// </summary>
	static bool DrawsSeparately( CacheFile cache, BspMaterial material ) =>
		material.Shader.Class == TagClasses.ShaderWater || material.Shader.Class == TagClasses.ShaderGlass || material.BreakableSurface >= 0
		|| DrawsBeforeWater( cache, material );

	static bool DrawsBeforeWater( CacheFile cache, BspMaterial material ) =>
		ShaderTransparentTag.IsTransparent( material.Shader.Class )
		&& new ShaderTransparentTag( cache, material.Shader ).Flags.HasFlag( TransparentShaderFlags.DrawBeforeWater );

	/// <summary>
	/// Each breakable surface's collision, one model per surface: the collision BSP's triangles on
	/// it, with their material types, which the BSP's own collision leaves out.
	/// </summary>
	/// <returns>The models and the breakable surface of each.</returns>
	public static List<(Model Model, int Breakable)> BuildBreakableCollision( string name, StructureBspTag bsp )
	{
		var triangleMaterials = new List<int>();
		var triangleBreakables = new List<int>();
		var (haloVertices, haloIndices) = bsp.ReadCollision( triangleMaterials, null, triangleBreakables );
		var vertices = haloVertices.Select( v => (Vector3)v * HaloCeMount.WorldScale ).ToList();
		var baseName = System.IO.Path.ChangeExtension( name, null );

		var models = new List<(Model Model, int Breakable)>();
		foreach ( var group in Enumerable.Range( 0, haloIndices.Count / 3 ).Where( t => triangleBreakables[t] >= 0 ).GroupBy( t => triangleBreakables[t] ) )
		{
			var triangles = group.ToList();
			var indices = triangles.SelectMany( t => new[] { haloIndices[t * 3], haloIndices[t * 3 + 1], haloIndices[t * 3 + 2] } ).ToList();
			var types = triangles.Select( t => bsp.GetCollisionMaterialType( triangleMaterials[t] ) ).ToList();
			var builder = Model.Builder.WithName( $"{baseName}_breakable_collision_{group.Key}.vmdl" );
			builder.AddCollisionMesh( vertices, indices, HaloSurfaces.AddSurfaces( builder, types, null ) );
			builder.AddTraceMesh( vertices, indices );
			models.Add( (builder.Create(), group.Key) );
		}

		return models;
	}

	/// <summary>
	/// The BSP's water, glass and surfaces drawn before water, one model per mesh: water bounded by
	/// a thin slab at its surface, the others by their own bounds (see above). Dynamic mirror glass is split by plane, each part showing
	/// its plane's mirror (see <see cref="MirrorPlanes"/>).
	/// </summary>
	/// <param name="name">The BSP model's name (its materials keep their names).</param>
	/// <param name="mirrors">The level's mirror planes, which this adds to.</param>
	/// <returns>The models, the index of each one's mirror plane (-1 for none) and the breakable surface it draws (-1 for none).</returns>
	/// <summary>
	/// Builds the BSP's separately drawn surfaces a slice at a time: yields a piece with no model
	/// each time the slice is due, and each model as it's built.
	/// </summary>
	public static IEnumerable<(Model Model, int MirrorPlane, int Breakable)> BuildSeparate( HaloCeMount host, string name, CacheFile cache, StructureBspTag bsp, BspDrawing drawing, MirrorPlanes mirrors, BuildSlice slice )
	{
		var models = new List<(Model Model, int MirrorPlane, int Breakable)>();
		var baseName = System.IO.Path.ChangeExtension( name, null );
		foreach ( var (lightmap, material, lightmapTexture) in Surfaces( host, bsp ) )
		{
			if ( !DrawsSeparately( cache, material ) )
				continue;
			if ( slice.Due )
				yield return default;
			foreach ( var piece in models )
				yield return piece;
			models.Clear();

			var breakable = material.BreakableSurface;
			var (vertices, indices) = ReadGeometry( bsp, material );

			// Each is shown while any of its material's surfaces is visible.
			Model Separate( Model model, int mirrorPlane )
			{
				ClusterVisibility.Register( model, new BspSeparateSurfaces( drawing, material.FirstSurface, material.SurfaceCount, mirrorPlane ) );
				return model;
			}

			if ( material.Shader.Class == TagClasses.ShaderWater )
			{
				var surface = BBox.FromPoints( vertices.Select( v => v.Position ) );
				var mesh = CreateMesh( host, name, cache, lightmap, material, lightmapTexture, vertices, indices );
				mesh.Bounds = new BBox( surface.Mins.WithZ( surface.Mins.z - 1 ), surface.Maxs );
				models.Add( (Separate( Model.Builder.WithName( $"{baseName}_water_{models.Count}.vmdl" ).AddMesh( mesh ).Create(), -1 ), -1, breakable) );
				continue;
			}

			if ( breakable < 0 && DrawsBeforeWater( cache, material ) )
			{
				var mesh = CreateMesh( host, name, cache, lightmap, material, lightmapTexture, vertices, indices );
				mesh.Bounds = BBox.FromPoints( vertices.Select( v => v.Position ) );
				models.Add( (Separate( Model.Builder.WithName( $"{baseName}_before_water_{models.Count}.vmdl" ).AddMesh( mesh ).Create(), -1 ), -1, breakable) );
				continue;
			}

			if ( material.Shader.Class != TagClasses.ShaderGlass )
			{
				var mesh = CreateMesh( host, name, cache, lightmap, material, lightmapTexture, vertices, indices );
				models.Add( (Separate( Model.Builder.WithName( $"{baseName}_breakable_{models.Count}.vmdl" ).AddMesh( mesh ).Create(), -1 ), -1, breakable) );
				continue;
			}

			var parts = new ShaderGlassTag( cache, material.Shader ).ReflectionType == GlassReflectionType.DynamicMirror
				? mirrors.Split( vertices, indices )
				: new Dictionary<int, List<int>> { [-1] = indices };

			foreach ( var (planeIndex, partIndices) in parts )
			{
				// Glass draws like Halo's: what's behind tinted, then the reflection and diffuse over
				// that. Two objects, the second with bounds a little bigger, so always a little
				// nearer: it draws after the tint by distance.
				var surface = BBox.FromPoints( partIndices.Select( i => vertices[i].Position ) );
				var tint = CreateMesh( host, name, cache, lightmap, material, lightmapTexture, vertices, partIndices, glassPass: 1 );
				tint.Bounds = surface;
				models.Add( (Separate( Model.Builder.WithName( $"{baseName}_glass_{models.Count}.vmdl" ).AddMesh( tint ).Create(), planeIndex ), planeIndex, breakable) );

				var over = CreateMesh( host, name, cache, lightmap, material, lightmapTexture, vertices, partIndices, glassPass: 2 );
				over.Bounds = new BBox( surface.Mins - 2, surface.Maxs + 2 );
				models.Add( (Separate( Model.Builder.WithName( $"{baseName}_glass_{models.Count}.vmdl" ).AddMesh( over ).Create(), planeIndex ), planeIndex, breakable) );
			}
		}

		foreach ( var piece in models )
			yield return piece;
	}

	/// <summary>
	/// The BSP's drawable surfaces, with their lightmap page and its texture.
	/// </summary>
	static IEnumerable<(BspLightmap Lightmap, BspMaterial Material, Texture LightmapTexture)> Surfaces( HaloCeMount host, StructureBspTag bsp )
	{
		foreach ( var lightmap in bsp.Lightmaps )
		{
			var lightmapTexture = lightmap.BitmapIndex >= 0 && bsp.LightmapBitmaps is not null
				? Texture.Load( HaloCeMount.TexturePath( host.Ident, bsp.LightmapBitmaps.Path, lightmap.BitmapIndex ) )
				: null;

			foreach ( var material in lightmap.Materials )
			{
				if ( material.Shader is null || material.SurfaceCount == 0 || !HaloMaterials.IsSupported( material.Shader.Class ) )
					continue;

				yield return (lightmap, material, lightmapTexture);
			}
		}
	}

	static (List<HaloVertex> Vertices, List<int> Indices) ReadGeometry( StructureBspTag bsp, BspMaterial material )
	{
		var rendered = bsp.ReadRenderedVertices( material );
		var lightmapVertices = bsp.ReadLightmapVertices( material );

		var vertices = new List<HaloVertex>( rendered.Length );
		for ( var i = 0; i < rendered.Length; i++ )
		{
			var v = rendered[i];
			var normal = (Vector3)v.Normal;

			vertices.Add( new HaloVertex
			{
				Position = (Vector3)v.Position * HaloCeMount.WorldScale,
				Normal = normal,
				Tangent = HaloVertex.Tangent4( normal, v.Tangent, v.Binormal ),
				TexCoord = v.TexCoord,
				LightmapTexCoord = lightmapVertices is null ? default : (Vector2)lightmapVertices[i].TexCoord,
				IncidentLight = lightmapVertices is null ? default : (Vector3)lightmapVertices[i].Normal,
			} );
		}

		var indices = new List<int>( material.SurfaceCount * 3 );
		for ( var s = material.FirstSurface; s < material.FirstSurface + material.SurfaceCount; s++ )
		{
			// Halo winds triangles the opposite way to s&box.
			indices.Add( bsp.Surfaces[s * 3 + 2] );
			indices.Add( bsp.Surfaces[s * 3 + 1] );
			indices.Add( bsp.Surfaces[s * 3 + 0] );
		}

		return (vertices, indices);
	}

	static Material CreateMaterial( HaloCeMount host, string name, CacheFile cache, BspLightmap lightmap, BspMaterial material, Texture lightmapTexture, int glassPass = 0 )
	{
		var materialName = $"{name}/{material.Shader.Path.Replace( '\\', '/' )}_{lightmap.BitmapIndex}{(glassPass > 0 ? $"_pass{glassPass}" : "")}.vmat";
		return glassPass > 0
			? GlassWaterMaterial.CreateGlass( host, materialName, cache, material.Shader, lightmapTexture, glassPass )
			: HaloMaterials.Create( host, materialName, cache, material.Shader, lightmapTexture );
	}

	static Mesh CreateMesh( HaloCeMount host, string name, CacheFile cache, BspLightmap lightmap, BspMaterial material, Texture lightmapTexture, List<HaloVertex> vertices, List<int> indices, int glassPass = 0 )
	{
		var mesh = new Mesh( CreateMaterial( host, name, cache, lightmap, material, lightmapTexture, glassPass ) );
		mesh.CreateVertexBuffer( vertices.Count, vertices );
		mesh.CreateIndexBuffer( indices.Count, indices );
		mesh.Bounds = BBox.FromPoints( vertices.Select( x => x.Position ) );
		return mesh;
	}

	// A mesh of some of a material's triangles, holding only the vertices they use.
	static Mesh CreateMesh( Material material, List<HaloVertex> vertices, List<int> indices )
	{
		var remap = new Dictionary<int, int>();
		var used = new List<HaloVertex>();
		var local = new List<int>( indices.Count );
		foreach ( var index in indices )
		{
			if ( !remap.TryGetValue( index, out var at ) )
			{
				remap[index] = at = used.Count;
				used.Add( vertices[index] );
			}
			local.Add( at );
		}

		var mesh = new Mesh( material );
		mesh.CreateVertexBuffer( used.Count, used );
		mesh.CreateIndexBuffer( local.Count, local );
		mesh.Bounds = BBox.FromPoints( used.Select( x => x.Position ) );
		return mesh;
	}
}
