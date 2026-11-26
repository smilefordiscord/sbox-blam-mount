using Blam.Halo3;
using Blam.Halo3.Tags;

namespace Sandbox.Mounting.Blam.Halo3;

// The scenario's placed decals laid onto the level: per scene BSP, its runtime decals (the scenario decals in that BSP) projected onto its render triangles (DecalProjector), drawn with each decal's render method lit like the surface under it.
// Blended decals get a model each under "<bsp> decals", as s&box sorts translucent objects by the camera's distance to their bounds: one map-wide model had the camera inside its bounds and drew over every particle (Sandtrap's jump pad sand cut by the shaft's stone edge decal). Pre-lighting multiply decals, drawn as opaque copies of the surface, stay in one model per BSP. A decal listed by two BSPs goes in once, on the first that takes it.
static class Halo3DecalBuilder
{
	public static void Build( Halo3Mount mount, CacheFile cache, List<Model> models )
	{
		var scenario = new ScenarioTag( cache.Scenario );
		var bsps = new ScenarioStructureBsps( cache );
		var placed = new HashSet<(TagEntry, System.Numerics.Vector3)>();
		var materials = new Dictionary<(DecalSystemEntry, Halo3Lighting, Halo3Lightmap, TagEntry), Material>();
		var systems = new Dictionary<TagEntry, DecalSystemTag>();
		foreach ( var reference in bsps.SceneBsps )
		{
			if ( reference.Bsp is null || reference.Lightmap is null )
				continue;

			try
			{
				BuildBsp( cache, scenario, bsps, reference, placed, materials, systems, models );
			}
			catch ( Exception e )
			{
				mount.Logger.Warning( e, $"Couldn't lay {reference.Bsp}'s decals: {e.Message}" );
			}
		}
	}

	static void BuildBsp( CacheFile cache, ScenarioTag scenario, ScenarioStructureBsps bsps, ScenarioStructureBsp reference, HashSet<(TagEntry, System.Numerics.Vector3)> placed,
		Dictionary<(DecalSystemEntry, Halo3Lighting, Halo3Lightmap, TagEntry), Material> materials, Dictionary<TagEntry, DecalSystemTag> systems, List<Model> models )
	{
		var bsp = new StructureBspTag( reference.Bsp );
		var decals = BspDecals.Read( bsp, scenario );
		if ( decals.Count == 0 )
			return;

		DecalSystemTag SystemOf( TagEntry tag )
		{
			if ( !systems.TryGetValue( tag, out var system ) )
				systems[tag] = system = new DecalSystemTag( tag );
			return system;
		}

		decals.RemoveAll( d => d.Decal is null || d.Decal.Class != DecalSystemTag.Class || placed.Contains( (d.Decal, d.Position) ) );
		if ( decals.Count == 0 )
			return;

		// Only the level triangles near a decal are kept.
		var regions = decals.Select( d => DecalProjector.Region( SystemOf( d.Decal ), d.Position, d.Scale ) ).ToList();
		var lightmapBsp = new LightmapBspTag( reference.Lightmap );
		if ( DecalSurfaces.Build( bsp, lightmapBsp, lightmapBsp.Geometry(), bsp.LoadResources(), regions ) is not { } surfaces )
			return;

		var path = Halo3Mount.ResourcePath( reference.Bsp.Path );
		var name = System.IO.Path.GetFileName( path );
		var lightmap = Halo3Lightmap.ForBsp( cache, reference.Lightmap );
		var projector = new DecalProjector( surfaces );
		var meshes = new Dictionary<Material, List<Halo3Vertex>>();
		var blended = new List<(ScenarioDecal Decal, Dictionary<Material, List<Halo3Vertex>> Meshes)>();

		// Pre-lighting multiply decals: drawn as the surface under them, its own material (as StructureBspBuilder names and makes it: lighting path, cube map) with up to three decals in its albedo, per BSP material, lighting, cube map and decals.
		var cubemapOf = StructureBspBuilder.Cubemaps( bsps, reference );
		var surfaceMaterials = new Dictionary<(int, Halo3Lighting, Halo3Cubemap, DecalSystemEntry, DecalSystemEntry, DecalSystemEntry), Material>();
		Material SurfaceMaterial( int bspMaterial, DecalSurfaceGroup group, IReadOnlyList<DecalSystemEntry> layers )
		{
			if ( bspMaterial < 0 || bspMaterial >= bsp.Materials.Count || bsp.Materials[bspMaterial] is not { } shader )
				return null;
			var lighting = Lighting( group );
			var cubemap = cubemapOf( group.Instance >= 0
				? StructureBspBuilder.InstanceCubemap( bsp, bsp.Instances[group.Instance] )
				: bsp.Clusters.FirstOrDefault( c => c.Mesh == group.Mesh )?.Cubemaps.FirstOrDefault() );
			var key = (bspMaterial, lighting, cubemap, layers.ElementAtOrDefault( 0 ), layers.ElementAtOrDefault( 1 ), layers.ElementAtOrDefault( 2 ));
			if ( surfaceMaterials.TryGetValue( key, out var material ) )
				return material;
			// A copy: Halo3Materials.Create shares the level's own material.
			var name = StructureBspBuilder.MaterialName( $"halo3/{path}", shader, lighting, cubemap );
			material = Halo3Materials.Create( name, shader, lighting, lighting == Halo3Lighting.PerPixel ? lightmap : null, cubemap: cubemap )?.CreateCopy( name.Replace( ".vmat", $"_decal{surfaceMaterials.Count}.vmat" ) );
			if ( material is not null )
			{
				for ( var k = 0; k < layers.Count; k++ )
					Halo3Materials.AddDecalAlbedo( material, k, layers[k] );
			}
			return surfaceMaterials[key] = material;
		}
		var albedoPieces = new List<(int Decal, PlacedDecal Piece)>();

		for ( var decalIndex = 0; decalIndex < decals.Count; decalIndex++ )
		{
			var decal = decals[decalIndex];
			var system = SystemOf( decal.Decal );

			var pieces = projector.Place( system, decal.Rotation, decal.Position, decal.Scale );
			if ( pieces.Count == 0 )
				continue;
			placed.Add( (decal.Decal, decal.Position) );
			var own = new Dictionary<Material, List<Halo3Vertex>>();

			foreach ( var piece in pieces )
			{
				if ( Halo3Materials.IsAlbedoDecal( piece.Decal ) )
				{
					albedoPieces.Add( (decalIndex, piece) );
					continue;
				}

				// Lit with the terms of the BSP material it lies on (Halo3Materials.LightLike), one material per decal, lighting and surface shader.
				var lighting = Lighting( piece.Group );
				var surface = Halo3Materials.LitAsSurface( piece.Decal.RenderMethod ) && piece.Material >= 0 && piece.Material < bsp.Materials.Count ? bsp.Materials[piece.Material] : null;
				var key = (piece.Decal, lighting, lighting == Halo3Lighting.PerPixel ? lightmap : null, surface);
				if ( !materials.TryGetValue( key, out var material ) )
				{
					var index = system.Decals.ToList().IndexOf( piece.Decal );
					var on = surface is null ? "" : $"_on_{System.IO.Path.GetFileName( Halo3Mount.ResourcePath( surface.Path ) )}";
					materials[key] = material = Halo3Materials.CreateDecal( $"halo3/{path}/decals/{Halo3Mount.ResourcePath( system.Tag.Path )}_{index}_{lighting.ToString().ToLowerInvariant()}{on}.vmat", piece.Decal, lighting, lightmap, surface is null ? null : ShaderTag.Get( surface ) );
				}
				if ( material is null )
					continue;

				if ( !own.TryGetValue( material, out var vertices ) )
					own[material] = vertices = new List<Halo3Vertex>();
				foreach ( var v in piece.Triangles )
					vertices.Add( Vertex( v ) );
			}

			if ( own.Count > 0 )
				blended.Add( (decal, own) );
		}

		// Each level triangle under them split where they overlap, every region drawn once with all the decals over it in drawing order, as MCC multiplies each into the same albedo (one copy of the surface per decal z-fought where they overlapped).
		foreach ( var triangle in albedoPieces.SelectMany( a => a.Piece.Polygons.Select( p => (a.Decal, a.Piece, p.Triangle, p.Points) ) ).GroupBy( x => x.Triangle ) )
		{
			var (ia, ib, ic, groupIndex, bspMaterial) = surfaces.Triangles[triangle.Key];
			var (va, vb, vc) = (surfaces.Vertices[ia], surfaces.Vertices[ib], surfaces.Vertices[ic]);
			var pieces = new List<(int Decal, System.Numerics.Vector2[] Polygon, DecalUvMap Uv, DecalSystemEntry Entry)>();
			foreach ( var x in triangle )
			{
				var st = x.Points.Select( q => Barycentric( q.Position, va.Position, vb.Position, vc.Position ) ).ToArray();
				if ( DecalUvMap.Fit( st, x.Points.Select( q => q.TexCoord ).ToArray() ) is { } uv )
					pieces.Add( (x.Decal, st, uv, x.Piece.Decal) );
			}
			foreach ( var region in DecalOverlay.Overlay( pieces.Select( ( x, n ) => (x.Decal, n, x.Polygon, x.Uv) ) ) )
			{
				var layers = region.Layers.Take( 3 ).ToList();
				if ( SurfaceMaterial( bspMaterial, surfaces.Groups[groupIndex], layers.Select( l => pieces[l.Piece].Entry ).ToList() ) is not { } material )
					continue;
				if ( !meshes.TryGetValue( material, out var vertices ) )
					meshes[material] = vertices = new List<Halo3Vertex>();
				var points = region.Polygon.Select( st => SurfaceVertex( va, vb, vc, st, layers.Select( l => l.Uv.At( st ) ).ToList() ) ).ToList();
				for ( var k = 1; k + 1 < points.Count; k++ )
				{
					vertices.Add( points[0] );
					vertices.Add( points[k] );
					vertices.Add( points[k + 1] );
				}
			}
		}

		if ( meshes.Count == 0 && blended.Count == 0 )
			return;

		var root = new GameObject( true, $"{name} decals" );
		AddModel( root, $"halo3/{path}_decals.vmdl", meshes, models );
		for ( var k = 0; k < blended.Count; k++ )
		{
			var (decal, own) = blended[k];
			var go = new GameObject( root, true, $"{System.IO.Path.GetFileName( decal.Decal.Path )} {k}" );
			AddModel( go, $"halo3/{path}_decal{k}.vmdl", own, models );
		}
	}

	static void AddModel( GameObject go, string modelName, Dictionary<Material, List<Halo3Vertex>> meshes, List<Model> models )
	{
		var builder = Model.Builder.WithName( modelName );
		var count = 0;
		foreach ( var (material, vertices) in meshes )
		{
			if ( vertices.Count < 3 )
				continue;
			var mesh = new Mesh( material );
			mesh.CreateVertexBuffer( vertices.Count, vertices );
			mesh.CreateIndexBuffer( vertices.Count, Enumerable.Range( 0, vertices.Count ).ToList() );
			mesh.Bounds = BBox.FromPoints( vertices.Select( v => v.Position ) );
			builder.AddMesh( mesh );
			count++;
		}
		if ( count == 0 )
			return;

		var model = builder.Create();
		models.Add( model );
		var renderer = go.AddComponent<ModelRenderer>();
		renderer.Model = model;
		renderer.RenderType = ModelRenderer.ShadowRenderType.Off;
	}

	static Halo3Lighting Lighting( DecalSurfaceGroup group ) => group.Lighting switch
	{
		DecalSurfaceLighting.PerPixel => Halo3Lighting.PerPixel,
		DecalSurfaceLighting.PerVertex => Halo3Lighting.PerVertex,
		_ => Halo3Lighting.Unlit,
	};

	// A point's (s, t) in a triangle: p = a + s (b - a) + t (c - a).
	static System.Numerics.Vector2 Barycentric( System.Numerics.Vector3 p, System.Numerics.Vector3 a, System.Numerics.Vector3 b, System.Numerics.Vector3 c )
	{
		var (e1, e2, d) = (b - a, c - a, p - a);
		var (d00, d01, d11) = (System.Numerics.Vector3.Dot( e1, e1 ), System.Numerics.Vector3.Dot( e1, e2 ), System.Numerics.Vector3.Dot( e2, e2 ));
		var (d20, d21) = (System.Numerics.Vector3.Dot( d, e1 ), System.Numerics.Vector3.Dot( d, e2 ));
		var den = d00 * d11 - d01 * d01;
		return den == 0 ? default : new System.Numerics.Vector2( (d11 * d20 - d01 * d21) / den, (d00 * d21 - d01 * d20) / den );
	}

	// The level surface at (s, t) of its triangle, with the decals' u/v over it (up to three).
	static Halo3Vertex SurfaceVertex( DecalSurfaceVertex a, DecalSurfaceVertex b, DecalSurfaceVertex c, System.Numerics.Vector2 st, List<System.Numerics.Vector2> uvs )
	{
		System.Numerics.Vector3 L3( System.Numerics.Vector3 x, System.Numerics.Vector3 y, System.Numerics.Vector3 z ) => x + st.X * (y - x) + st.Y * (z - x);
		System.Numerics.Vector2 L2( System.Numerics.Vector2 x, System.Numerics.Vector2 y, System.Numerics.Vector2 z ) => x + st.X * (y - x) + st.Y * (z - x);
		var normal = System.Numerics.Vector3.Normalize( L3( a.Normal, b.Normal, c.Normal ) );
		var light = new VertexLighting( L3( a.Light.Intensity, b.Light.Intensity, c.Light.Intensity ), L3( a.Light.C0, b.Light.C0, c.Light.C0 ), L3( a.Light.C1, b.Light.C1, c.Light.C1 ),
			L3( a.Light.C2, b.Light.C2, c.Light.C2 ), L3( a.Light.C3, b.Light.C3, c.Light.C3 ) );
		System.Numerics.Vector2 Uv( int k ) => k < uvs.Count ? uvs[k] : new System.Numerics.Vector2( -1, -1 );
		return new Halo3Vertex
		{
			Position = (Vector3)L3( a.Position, b.Position, c.Position ) * Halo3Mount.WorldScale,
			Normal = normal,
			Tangent = Halo3Vertex.Tangent4( normal, L3( a.Tangent, b.Tangent, c.Tangent ), L3( a.Binormal, b.Binormal, c.Binormal ) ),
			TexCoord = L2( a.TexCoord, b.TexCoord, c.TexCoord ),
			LightmapTexCoord = L2( a.LightmapTexCoord, b.LightmapTexCoord, c.LightmapTexCoord ),
			Sh0 = new Vector4( light.C0, light.Intensity.X ),
			Sh1 = new Vector4( light.C1, light.Intensity.Y ),
			Sh2 = new Vector4( light.C2, light.Intensity.Z ),
			Sh3 = new Vector4( light.C3, 0 ),
			DecalTexCoords = new Vector4( Uv( 0 ).X, Uv( 0 ).Y, Uv( 1 ).X, Uv( 1 ).Y ),
			DecalTexCoord2 = Uv( 2 ),
		};
	}

	static Halo3Vertex Vertex( DecalVertex v )
	{
		var vertex = new Halo3Vertex
		{
			Position = (Vector3)v.Position * Halo3Mount.WorldScale,
			Normal = v.Normal,
			Tangent = Halo3Vertex.Tangent4( v.Normal, v.Tangent, v.Binormal ),
			TexCoord = v.TexCoord,
			LightmapTexCoord = v.LightmapTexCoord,
			Sh0 = new Vector4( v.Light.C0, v.Light.Intensity.X ),
			Sh1 = new Vector4( v.Light.C1, v.Light.Intensity.Y ),
			Sh2 = new Vector4( v.Light.C2, v.Light.Intensity.Z ),
			Sh3 = new Vector4( v.Light.C3, 0 ),
		};
		return vertex;
	}
}
