using Blam.Halo3;
using Blam.Halo3.Tags;

namespace Sandbox.Mounting.Blam.Halo3;

// The level geometry of a scenario's structure BSPs: cluster meshes, instanced geometry placed by its instances, and Halo's collision with surfaces per global material.
// Every BSP some zone set loads goes in the scene (all BSPs share one world frame; multiplayer maps have one). Vertices come from each BSP's Lbsp, whose lighting picks each mesh's Halo3Lighting (lighting.md §1.3).
static class StructureBspBuilder
{
	// Decorators (grass, ferns, rocks; Halo3Materials draws their dctr sets); they're heavy (Valhalla 1.5 M triangles, 010_jungle more), so this switches them off.
	public static bool BuildDecorators = true;

	public static void Build( Halo3Mount mount, CacheFile cache, List<Model> models )
	{
		var scenario = new ScenarioStructureBsps( cache );
		var materialNames = Halo3Surfaces.MaterialNames( cache );
		var sun = BuildDecorators ? DecoratorSunOf( mount, cache ) : null;
		// Every BSP's geometry first, to find the triangles several draw (see SharedTriangles).
		var loaded = new List<Loaded>();
		foreach ( var reference in scenario.SceneBsps )
		{
			if ( reference.Bsp is null || reference.Lightmap is null )
				continue;
			try
			{
				var bsp = new StructureBspTag( reference.Bsp );
				var lightmapBsp = new LightmapBspTag( reference.Lightmap );
				var geometry = lightmapBsp.Geometry();
				if ( !geometry.Load() )
				{
					mount.Logger.Warning( $"{reference.Bsp}: its lightmap BSP's geometry is stripped" );
					continue;
				}
				loaded.Add( new Loaded( reference, bsp, lightmapBsp, geometry, bsp.LoadResources() ) );
			}
			catch ( Exception e )
			{
				mount.Logger.Warning( e, $"Couldn't load {reference.Bsp}: {e.Message}" );
			}
		}

		var dropped = new Dictionary<int, HashSet<SharedTriangles.Key>>();
		try
		{
			dropped = SharedTriangles.Find( loaded.Select( l => new SharedTriangles.Source( l.Reference.Index, l.Bsp, l.Geometry, l.Resources?.Collision, l.Resources?.Definitions ) ).ToList(), out var shared );
			if ( shared > 0 )
				mount.Logger.Info( $"{cache.Name}: {shared} triangles drawn by more than one BSP, each now by the one it faces into" );
		}
		catch ( Exception e )
		{
			mount.Logger.Warning( e, $"{cache.Name}: couldn't look for triangles BSPs share: {e.Message}" );
		}

		for ( var i = 0; i < loaded.Count; i++ )
		{
			var reference = loaded[i].Reference;
			try
			{
				BuildBsp( mount, cache, scenario, loaded[i], dropped.GetValueOrDefault( reference.Index ), materialNames, models, sun );
				loaded[i] = null;
			}
			catch ( Exception e )
			{
				mount.Logger.Warning( e, $"Couldn't build {reference.Bsp}: {e.Message}" );
			}
		}
	}

	// The baked cube map dynamic environment maps reflect: a cluster's own (its first), an instance's own or else the nearest of the cluster it sits in.
	// The index names a scenario cube map point and, as far as the maps show, the image of the same number in the BSP's cubemaps bitmap; a few maps (Construct, Zanzibar) have fewer images than points, where an index past the images takes the nearest point's image that is within them (unverified against MCC).
	internal static Func<StructureCubemap, Halo3Cubemap> Cubemaps( ScenarioStructureBsps scenario, ScenarioStructureBsp reference )
	{
		var images = reference.Cubemaps is null ? 0 : BitmapTag.ImagesOf( reference.Cubemaps ).Count;
		var points = scenario.CubemapPoints;
		return cubemap =>
		{
			if ( cubemap is null || images == 0 )
				return null;
			var index = cubemap.Index;
			if ( index < 0 || index >= images )
			{
				index = -1;
				var best = float.MaxValue;
				for ( var k = 0; k < Math.Min( images, points.Count ); k++ )
				{
					var d = System.Numerics.Vector3.DistanceSquared( points[k], cubemap.Position );
					if ( d < best )
						(best, index) = (d, k);
				}
				if ( index < 0 )
					return null;
			}
			return new Halo3Cubemap( reference.Cubemaps, index );
		};
	}

	internal static StructureCubemap InstanceCubemap( StructureBspTag bsp, StructureInstance instance ) =>
		instance.Cubemaps.FirstOrDefault() ?? bsp.ClusterAt( instance.BoundingCenter )?.Cubemaps.MinBy( c => System.Numerics.Vector3.DistanceSquared( c.Position, instance.BoundingCenter ) );

	// The name StructureBspBuilder gives a level material (shared with the decals drawn on it, Halo3DecalBuilder).
	internal static string MaterialName( string modelName, TagEntry shader, Halo3Lighting lighting, Halo3Cubemap cubemap ) =>
		$"{modelName}/{Halo3Mount.ResourcePath( shader.Path )}_{lighting.ToString().ToLowerInvariant()}{(cubemap is null ? "" : $"_cube{cubemap.Index}")}.vmat";

	// The sun the decorators are lit by, from the shown sky's model (MCC takes the camera cluster's sky; multiplayer maps have one).
	static DecoratorSun? DecoratorSunOf( Halo3Mount mount, CacheFile cache )
	{
		try
		{
			var scenario = new ScenarioTag( cache.Scenario );
			var plan = new ScenarioPlan( scenario, ScenarioObjectsBuilder.ShownBspMask( scenario ) );
			return plan.Sky is { } sky && plan.Model( plan.Object( sky.Object )?.Model )?.RenderModel is { } mode ? DecoratorSun.FromSkyModel( mode ) : null;
		}
		catch ( Exception e )
		{
			mount.Logger.Warning( e, $"Couldn't read {cache.Name}'s decorator sun: {e.Message}" );
			return null;
		}
	}

	sealed record Loaded( ScenarioStructureBsp Reference, StructureBspTag Bsp, LightmapBspTag LightmapBsp, RenderGeometry Geometry, StructureBspResources Resources );

	static void BuildBsp( Halo3Mount mount, CacheFile cache, ScenarioStructureBsps scenario, Loaded loaded, HashSet<SharedTriangles.Key> dropped, List<string> materialNames, List<Model> models, DecoratorSun? sun )
	{
		var (reference, bsp, lightmapBsp, geometry, resources) = loaded;
		var path = Halo3Mount.ResourcePath( reference.Bsp.Path );
		var name = System.IO.Path.GetFileName( path );
		var lightmap = Halo3Lightmap.ForBsp( cache, reference.Lightmap );
		var builder = new LevelBuilder( $"halo3/{path}", bsp, geometry, lightmap ) { Dropped = dropped };

		var Cubemap = Cubemaps( scenario, reference );

		foreach ( var cluster in bsp.Clusters )
		{
			if ( cluster.Mesh < 0 || cluster.Mesh >= geometry.Meshes.Count )
				continue;

			var mesh = geometry.Meshes[cluster.Mesh];
			var lightmapTexCoords = geometry.Buffer( mesh.VertexBuffers[GeometryMesh.SlotLightmapTexCoord] ) is { Format: 4 } uv ? RenderGeometry.DecodeLightmapTexCoords( uv ) : null;
			builder.Add( mesh, null, null, lightmapTexCoords is null ? Halo3Lighting.Unlit : Halo3Lighting.PerPixel, lightmapTexCoords, null, Cubemap( cluster.Cubemaps.FirstOrDefault() ) );
		}

		if ( resources is not null )
		{
			foreach ( var instance in bsp.Instances )
			{
				if ( instance.NotForRender || instance.Definition < 0 || instance.Definition >= resources.Definitions.Count )
					continue;

				var definition = resources.Definitions[instance.Definition];
				if ( definition.Mesh < 0 || definition.Mesh >= geometry.Meshes.Count )
					continue;

				var mesh = geometry.Meshes[definition.Mesh];
				var compression = definition.Compression >= 0 && definition.Compression < geometry.Compression.Count ? geometry.Compression[definition.Compression] : GeometryCompression.Identity;
				var (lighting, lightmapTexCoords, vertexLighting) = InstanceLighting( geometry, lightmapBsp, instance );
				builder.Add( mesh, compression, instance, lighting, lightmapTexCoords, vertexLighting, Cubemap( InstanceCubemap( bsp, instance ) ) );
			}
		}

		var (collision, aggregates) = builder.Create( Collision( bsp, resources, materialNames ) );
		if ( collision is not null || aggregates.Count > 0 )
		{
			var go = new GameObject( true, name );
			if ( collision is not null )
			{
				models.Add( collision );
				var collider = go.AddComponent<ModelCollider>();
				collider.Model = collision;
				collider.Static = true;
			}

			// The opaque level as s&box draws compiled scenes: an aggregate per material, each cluster's and instance's triangles a fragment that the GPU culls on its own (by the view and by what's in front of it) and draws in one indirect draw.
			foreach ( var (model, fragments) in aggregates )
			{
				models.Add( model );
				var aggregate = new GameObject( go, true, System.IO.Path.GetFileNameWithoutExtension( model.Name ) ).AddComponent<AggregateRenderer>();
				aggregate.Model = model;
				aggregate.Fragments = fragments;
				// The level's shadows are baked into its lightmaps; only objects cast into the sun's shadow map (Halo3SunLight).
				aggregate.CastShadows = false;
			}
		}

		if ( BuildDecorators )
			AddDecorators( $"halo3/{path}", name, bsp, lightmap, models, sun );

		// Transparent parts are models of their own so they sort back to front with everything else (as Halo2/StructureBspModel.BuildTransparent).
		foreach ( var transparent in builder.Transparent )
		{
			models.Add( transparent );
			var transparentRenderer = new GameObject( true, $"{name} transparent" ).AddComponent<ModelRenderer>();
			transparentRenderer.Model = transparent;
			transparentRenderer.RenderType = ModelRenderer.ShadowRenderType.Off;
		}
	}

	// The BSP's decorators, batched: aggregates per decorator set (one material), each of its groups (a cluster's instances) a fragment the GPU culls on its own, each instance a copy of its type's subpart of the set's model, lit by its own light as constant SH.
	// The material's g_vDecoratorFade is the set's fade distances as MCC draws them (DecoratorSetTag.MccFade): halo3_shader fades an instance's alpha from the first to the second (horizontally from the camera, by the instance's position in LightmapTexCoord) and drops its vertices once the fade is below the cutoff.
	static void AddDecorators( string modelName, string name, StructureBspTag bsp, Halo3Lightmap lightmap, List<Model> models, DecoratorSun? sun )
	{
		if ( StructureBspDecorators.Load( bsp ) is not { Groups.Count: > 0 } decorators )
			return;

		var parent = new GameObject( true, $"{name} decorators" );
		foreach ( var bySet in decorators.Groups.GroupBy( g => g.Set ) )
		{
			var setTag = decorators.Sets.ElementAtOrDefault( bySet.Key );
			if ( DecoratorSet( setTag, modelName ) is not { } s )
				continue;
			var renderShader = new DecoratorSetTag( setTag ).RenderShader;

			var fragments = new List<AggregateFragment<DecoratorVertex>>();
			var vertices = new List<DecoratorVertex>();
			var indices = new List<int>();
			var bounds = new BBox( Vector3.Zero, Vector3.Zero );
			var used = new Dictionary<int, int>();

			DecoratorGroup current = null;
			void EndFragment()
			{
				if ( indices.Count > 0 )
					fragments.Add( new AggregateFragment<DecoratorVertex>( vertices.ToArray(), indices.ToArray(), bounds ) );
				vertices.Clear();
				indices.Clear();
			}

			foreach ( var (group, instance) in bySet.SelectMany( g => g.Instances.Select( i => (g, i) ) ) )
			{
				if ( group != current )
				{
					EndFragment();
					current = group;
				}

				if ( instance.Type >= s.Mesh.Subparts.Length )
					continue;

				var subpart = s.Mesh.Subparts[instance.Type];
				used.Clear();
				var orientation = instance.Orientation;
				var center = (Vector3)instance.Position * Halo3Mount.WorldScale;

				// Light = E / pi with only the L0 term: c0 = light / 0.282095, per vertex where the set's render shader adds the sun (DecoratorSun.Light).
				var sh0 = new Vector4( (Vector3)instance.Light / 0.282095f, 0 );
				foreach ( var (a, b, c) in RenderGeometry.Triangles( s.Indices, subpart.IndexStart, subpart.IndexCount, s.Mesh.IndexBufferType ) )
				{
					if ( a >= s.Vertices.Count || b >= s.Vertices.Count || c >= s.Vertices.Count )
						continue;
					indices.Add( Vertex( a ) );
					indices.Add( Vertex( b ) );
					indices.Add( Vertex( c ) );
				}

				int Vertex( int i )
				{
					if ( used.TryGetValue( i, out var k ) )
						return k;
					var world = instance.Transform( s.Vertices.Positions[i] );
					var position = (Vector3)world * Halo3Mount.WorldScale;
					var normal = System.Numerics.Vector3.Transform( s.Vertices.Normals[i], orientation );
					bounds = vertices.Count == 0 ? new BBox( position, position ) : bounds.AddPoint( position );
					vertices.Add( new DecoratorVertex
					{
						Position = position,
						Normal = normal,
						Tangent = new Color32( 255, 0, 0, 255 ),
						TexCoord = s.Vertices.TexCoords[i],
						Center = new Vector2( center.x, center.y ),
						Sh0 = sun is { } light && renderShader is DecoratorSetTag.SunShader or DecoratorSetTag.ShadedShader ? new Vector4( (Vector3)light.Light( renderShader, instance, normal, world - instance.Position ) / 0.282095f, 0 ) : sh0,
					} );
					return used[i] = vertices.Count - 1;
				}
			}

			EndFragment();
			if ( fragments.Count == 0 )
				continue;

			var (fadeStart, fadeEnd) = new DecoratorSetTag( setTag ).MccFade();
			if ( fadeEnd > fadeStart && fadeStart > 0 )
				s.Material.Set( "g_vDecoratorFade", new Vector2( fadeStart, fadeEnd ) * Halo3Mount.WorldScale );

			var part = 0;
			foreach ( var (model, infos) in Aggregates( $"{modelName}_decorators_{bySet.Key}", s.Material, fragments, DecoratorMeshVertices ) )
			{
				models.Add( model );
				var aggregate = new GameObject( parent, true, $"{System.IO.Path.GetFileName( setTag.Path )} {part++}" ).AddComponent<AggregateRenderer>();
				aggregate.Model = model;
				aggregate.Fragments = infos;
				aggregate.CastShadows = false;
			}
		}

		(Material Material, RenderGeometry Geometry, GeometryMesh Mesh, GeometryVertices Vertices, ushort[] Indices)? DecoratorSet( TagEntry tag, string modelName )
		{
			if ( tag is null || new DecoratorSetTag( tag ) is not { RenderModel: { } renderModel } set )
				return null;

			var model = new RenderModelTag( renderModel );
			var geometry = model.Geometry();
			var instanceMesh = renderModel.Data.I32( 0x1C );
			if ( !geometry.Load() || instanceMesh < 0 || instanceMesh >= geometry.Meshes.Count )
				return null;

			var mesh = geometry.Meshes[instanceMesh];
			if ( geometry.Vertices( mesh, DecoratorSetTag.ModelCompression( geometry ) ) is not { Count: > 0 } vertices )
				return null;

			var material = Halo3Materials.Create( $"{modelName}/{Halo3Mount.ResourcePath( tag.Path )}.vmat", tag, Halo3Lighting.PerVertex, lightmap );
			return material is null ? null : (material, geometry, mesh, vertices, geometry.Indices( mesh ));
		}
	}

	// Vertices per decorator mesh; a set with more gets several aggregates.
	public const int DecoratorMeshVertices = 1 << 20;

	// The vertex of decorator meshes: Halo3Vertex's layout with the parts decorators don't use packed small (80 bytes, not 144); LightmapTexCoord carries the instance's position (x, y) for the distance cull.
	[System.Runtime.InteropServices.StructLayout( System.Runtime.InteropServices.LayoutKind.Sequential )]
	struct DecoratorVertex
	{
		[VertexLayout.Position] public Vector3 Position;
		[VertexLayout.Normal] public Vector3 Normal;
		[VertexLayout.Tangent] public Color32 Tangent;
		[VertexLayout.TexCoord( 0 )] public Vector2 TexCoord;
		[VertexLayout.TexCoord( 1 )] public Vector2 Center;
		[VertexLayout.TexCoord( 4 )] public Vector4 Sh0;
		[VertexLayout.TexCoord( 5 )] public Color32 Sh1;
		[VertexLayout.TexCoord( 6 )] public Color32 Sh2;
		[VertexLayout.TexCoord( 7 )] public Color32 Sh3;
		// Halo3Vertex's decal u/v, always zero: halo3_shader reads them in every unskinned draw.
		[VertexLayout.TexCoord( 8 )] public Color32 DecalTexCoords;
		[VertexLayout.TexCoord( 9 )] public Color32 DecalTexCoord2;
	}

	// How an instance is lit: its lightmap texcoords when it's in the lightmap atlas, else its per-vertex SH, else its probe as constant per-vertex SH, else unlit.
	static (Halo3Lighting, System.Numerics.Vector2[], VertexLighting[]) InstanceLighting( RenderGeometry geometry, LightmapBspTag lightmapBsp, StructureInstance instance )
	{
		if ( instance.Index >= lightmapBsp.Instances.Count )
			return (Halo3Lighting.Unlit, null, null);

		var lit = lightmapBsp.Instances[instance.Index];
		if ( lit.LightprobeTexture >= 0 && instance.LightmapTexCoords >= 0 && instance.LightmapTexCoords < geometry.PerInstanceLightmapTexCoords.Count
			&& geometry.Buffer( geometry.PerInstanceLightmapTexCoords[instance.LightmapTexCoords] ) is { Format: 4 } uv )
			return (Halo3Lighting.PerPixel, RenderGeometry.DecodeLightmapTexCoords( uv ), null);

		if ( lit.PerVertex >= 0 && lit.PerVertex < lightmapBsp.PerVertexBuffers.Count && geometry.Buffer( lightmapBsp.PerVertexBuffers[lit.PerVertex] ) is { Format: 6 } sh )
			return (Halo3Lighting.PerVertex, null, RenderGeometry.DecodeVertexLighting( sh ));

		if ( lit.Probe >= 0 && lightmapBsp.Probe( lit.Probe ) is { } probe )
			return (Halo3Lighting.PerVertex, null, [probe]);

		return (Halo3Lighting.Unlit, null, null);
	}

	// The structure collision BSP and every collidable instance's own (placed by the instance), with each triangle's global material name.
	static (List<Vector3> Vertices, List<int> Indices, List<string> Materials) Collision( StructureBspTag bsp, StructureBspResources resources, List<string> materialNames )
	{
		var vertices = new List<Vector3>();
		var indices = new List<int>();
		var materials = new List<string>();
		if ( resources is null )
			return (vertices, indices, materials);

		string MaterialName( int collisionMaterial )
		{
			if ( collisionMaterial < 0 || collisionMaterial >= bsp.CollisionMaterials.Count )
				return null;
			var global = bsp.CollisionMaterials[collisionMaterial].GlobalMaterial;
			return global >= 0 && global < materialNames.Count ? materialNames[global] : null;
		}

		void Add( CollisionBsp collision, StructureInstance instance )
		{
			var surfaces = new List<CollisionSurface>();
			var triangles = collision.Triangles( surfaces );
			var first = vertices.Count;
			foreach ( var v in collision.Vertices )
				vertices.Add( (Vector3)(instance is null ? v : instance.Transform( v )) * Halo3Mount.WorldScale );

			// A mirroring instance turns its triangles inside out; keep them facing out.
			var mirrored = instance is { IsMirrored: true };
			for ( var i = 0; i + 2 < triangles.Count; i += 3 )
			{
				indices.Add( first + triangles[i] );
				indices.Add( first + triangles[mirrored ? i + 2 : i + 1] );
				indices.Add( first + triangles[mirrored ? i + 1 : i + 2] );
				materials.Add( MaterialName( surfaces[i / 3].Material ) );
			}
		}

		if ( resources.Collision is not null )
			Add( resources.Collision, null );

		foreach ( var instance in bsp.Instances )
		{
			if ( !instance.RenderOnly && instance.Definition >= 0 && instance.Definition < resources.Definitions.Count )
				Add( resources.Definitions[instance.Definition].Collision, instance );
		}

		return (vertices, indices, materials);
	}

	// Collects a BSP's meshes: opaque parts as fragments of an aggregate per material, transparent parts into models of their own, one material per render method and lighting.
	sealed class LevelBuilder( string modelName, StructureBspTag bsp, RenderGeometry geometry, Halo3Lightmap lightmap )
	{
		readonly Dictionary<(int, Halo3Lighting, Halo3Cubemap), Material> _materials = new();
		// Each material's triangles of a cluster or placed instance, with only the vertices they use.
		readonly Dictionary<Material, List<AggregateFragment<Halo3Vertex>>> _fragments = new();
		readonly List<Vector3> _renderVertices = new();
		readonly List<int> _renderIndices = new();

		public List<Model> Transparent { get; } = new();

		Material MaterialFor( int index, Halo3Lighting lighting, Halo3Cubemap cubemap )
		{
			if ( _materials.TryGetValue( (index, lighting, cubemap), out var material ) )
				return material;

			var shader = bsp.Materials[index];
			material = shader is null ? null : Halo3Materials.Create( MaterialName( modelName, shader, lighting, cubemap ), shader, lighting, lightmap, cubemap: cubemap );
			return _materials[(index, lighting, cubemap)] = material;
		}

		// Whether a BSP material's shader blends, looked up once per material.
		readonly Dictionary<int, bool> _blended = new();
		bool BlendedMaterial( int index )
		{
			if ( index < 0 || index >= bsp.Materials.Count )
				return false;
			if ( !_blended.TryGetValue( index, out var blended ) )
				_blended[index] = blended = Halo3Materials.IsBlended( bsp.Materials[index] );
			return blended;
		}

		// A cluster (instance null, world vertices) or a placed instance's mesh; vertexLighting has one entry per vertex, or a single one for the whole mesh; cubemap is the baked cube map its dynamic environment maps reflect.
		// Triangles another BSP draws instead (see SharedTriangles).
		public HashSet<SharedTriangles.Key> Dropped { get; init; }

		public void Add( GeometryMesh mesh, GeometryCompression? compression, StructureInstance instance, Halo3Lighting lighting, System.Numerics.Vector2[] lightmapTexCoords, VertexLighting[] vertexLighting, Halo3Cubemap cubemap )
		{
			System.Numerics.Vector3 Place( System.Numerics.Vector3 p ) => instance?.Transform( p ) ?? p;
			if ( geometry.Vertices( mesh, compression ) is not { Count: > 0 } source )
				return;

			List<Halo3Vertex> vertices = null;
			var mirrored = instance is { IsMirrored: true };
			// Water parts (flag 8) draw as transparent whatever their type (Valhalla's are type 3, opaque non-shadowing): blended, with their per-vertex water parameters. So do parts whose shader blends.
			foreach ( var group in mesh.Parts.Where( p => p.IsDrawn ).GroupBy( p => (p.Material, p.IsTransparent || p.IsWater || BlendedMaterial( p.Material )) ) )
			{
				var (materialIndex, transparent) = group.Key;
				if ( materialIndex < 0 || materialIndex >= bsp.Materials.Count )
					continue;

				// Halo 3 winds front faces counter-clockwise, as s&box does.
				var indices = new List<int>();
				foreach ( var part in group )
				{
					foreach ( var (a, b, c) in geometry.Triangles( mesh, part ) )
					{
						if ( a >= source.Count || b >= source.Count || c >= source.Count )
							continue;
						if ( Dropped is { Count: > 0 } && Dropped.Contains( SharedTriangles.KeyOf( Place( source.Positions[a] ), Place( source.Positions[b] ), Place( source.Positions[c] ) ) ) )
							continue;
						indices.Add( a );
						indices.Add( mirrored ? c : b );
						indices.Add( mirrored ? b : c );
					}
				}

				if ( indices.Count == 0 || MaterialFor( materialIndex, lighting, cubemap ) is not { } material )
					continue;

				vertices ??= Vertices( source, instance, lightmapTexCoords, vertexLighting );

				// Water carries its depth, global shape texture coordinate and displacement scale per vertex in Sh3 (unused with per-pixel lightmaps), for halo3_shader's water, and is tessellated for its displacement.
				var drawnVertices = vertices;
				if ( transparent && lighting != Halo3Lighting.PerVertex && geometry.WaterParameters( mesh, vertices.Count ) is { } water )
				{
					drawnVertices = new List<Halo3Vertex>( vertices );
					for ( var i = 0; i < drawnVertices.Count; i++ )
					{
						var vertex = drawnVertices[i];
						vertex.Sh3 = new Vector4( water[i].X, water[i].Y, water[i].Z, water[i].W );
						drawnVertices[i] = vertex;
					}
					(drawnVertices, indices) = TessellateWater( drawnVertices, indices );
				}

				var bounds = BBox.FromPoints( indices.Select( i => drawnVertices[i].Position ) );

				if ( transparent )
				{
					var m = new Mesh( material );
					m.CreateVertexBuffer( drawnVertices.Count, drawnVertices );
					m.CreateIndexBuffer( indices.Count, indices );

					// Displaced water reaches past its flat mesh; s&box copies the frame for its refraction only within its bounds.
					if ( drawnVertices != vertices && Halo3Materials.WaterDisplacementReach( bsp.Materials[materialIndex] ) is > 0 and var reach )
						bounds = bounds.Grow( reach * Halo3Mount.WorldScale );
					m.Bounds = bounds;

					Transparent.Add( Model.Builder.WithName( $"{modelName}_transparent_{Transparent.Count}.vmdl" ).AddMesh( m ).Create() );
					continue;
				}

				var used = new Dictionary<int, int>();
				var fragmentVertices = new List<Halo3Vertex>();
				var fragmentIndices = new int[indices.Count];
				for ( var i = 0; i < indices.Count; i++ )
				{
					if ( !used.TryGetValue( indices[i], out var index ) )
					{
						used[indices[i]] = index = fragmentVertices.Count;
						fragmentVertices.Add( vertices[indices[i]] );
					}
					fragmentIndices[i] = index;
				}
				if ( !_fragments.TryGetValue( material, out var fragments ) )
					_fragments[material] = fragments = new();
				fragments.Add( new AggregateFragment<Halo3Vertex>( fragmentVertices.ToArray(), fragmentIndices, bounds ) );

				var first = _renderVertices.Count;
				_renderVertices.AddRange( vertices.Select( v => v.Position ) );
				_renderIndices.AddRange( indices.Select( i => first + i ) );
			}
		}

		// Water triangles each split into WaterTessellation^2, as MCC's water draws them, so its vertex shader can displace the surface. A new vertex is shared by what it lies on in the source mesh: a corner by its vertex, an edge point by the edge's two vertices (computed from them in one order), an inner point by its triangle; shared edges can't crack, and vertices the source keeps apart at one position (lightmap and texture seams) stay apart.
		const int WaterTessellation = 15;

		static (List<Halo3Vertex>, List<int>) TessellateWater( List<Halo3Vertex> vertices, List<int> indices )
		{
			var output = new List<Halo3Vertex>();
			var outputIndices = new List<int>();
			var made = new Dictionary<(int, int, int, int), int>();
			const int n = WaterTessellation;

			int Make( (int, int, int, int) key, (int Index, float Weight)[] corners )
			{
				if ( made.TryGetValue( key, out var index ) )
					return index;

				var r = new Halo3Vertex();
				var tangent = Vector4.Zero;
				var normal = Vector3.Zero;
				foreach ( var (i, weight) in corners )
				{
					var v = vertices[i];
					r.Position += v.Position * weight;
					normal += v.Normal * weight;
					tangent += v.Tangent * weight;
					r.TexCoord += v.TexCoord * weight;
					r.LightmapTexCoord += v.LightmapTexCoord * weight;
					r.Sh0 += v.Sh0 * weight;
					r.Sh1 += v.Sh1 * weight;
					r.Sh2 += v.Sh2 * weight;
					r.Sh3 += v.Sh3 * weight;
				}
				r.Normal = normal.Normal;
				r.Tangent = new Vector4( new Vector3( tangent.x, tangent.y, tangent.z ).Normal, vertices[corners[0].Index].Tangent.w );
				output.Add( r );
				return made[key] = output.Count - 1;
			}

			// The point (n - i - j, i, j) / n of triangle t (corners a, b, c).
			int At( int t, int a, int b, int c, int i, int j )
			{
				var weights = (A: n - i - j, B: i, C: j);
				if ( weights.B == 0 && weights.C == 0 ) return Make( (0, a, 0, 0), [(a, 1)] );
				if ( weights.A == 0 && weights.C == 0 ) return Make( (0, b, 0, 0), [(b, 1)] );
				if ( weights.A == 0 && weights.B == 0 ) return Make( (0, c, 0, 0), [(c, 1)] );
				if ( weights.A == 0 || weights.B == 0 || weights.C == 0 )
				{
					// On an edge: by its two vertices, lower index first, and the higher one's weight.
					var (p, wp, q, wq) = weights.A == 0 ? (b, weights.B, c, weights.C) : weights.B == 0 ? (a, weights.A, c, weights.C) : (a, weights.A, b, weights.B);
					if ( p > q )
						(p, wp, q, wq) = (q, wq, p, wp);
					return Make( (1, p, q, wq), [(p, (float)wp / n), (q, (float)wq / n)] );
				}
				return Make( (2, t, i, j), [(a, (float)weights.A / n), (b, (float)i / n), (c, (float)j / n)] );
			}

			for ( var t = 0; t + 2 < indices.Count; t += 3 )
			{
				int a = indices[t], b = indices[t + 1], c = indices[t + 2];
				for ( var i = 0; i < n; i++ )
				{
					for ( var j = 0; j < n - i; j++ )
					{
						outputIndices.Add( At( t, a, b, c, i, j ) );
						outputIndices.Add( At( t, a, b, c, i + 1, j ) );
						outputIndices.Add( At( t, a, b, c, i, j + 1 ) );
						if ( j + 1 < n - i )
						{
							outputIndices.Add( At( t, a, b, c, i + 1, j ) );
							outputIndices.Add( At( t, a, b, c, i + 1, j + 1 ) );
							outputIndices.Add( At( t, a, b, c, i, j + 1 ) );
						}
					}
				}
			}

			return (output, outputIndices);
		}

		static List<Halo3Vertex> Vertices( GeometryVertices source, StructureInstance instance, System.Numerics.Vector2[] lightmapTexCoords, VertexLighting[] vertexLighting )
		{
			var vertices = new List<Halo3Vertex>( source.Count );
			for ( var i = 0; i < source.Count; i++ )
			{
				var position = source.Positions[i];
				var normal = source.Normals[i];
				var tangent = source.Tangents?[i] ?? System.Numerics.Vector3.UnitX;
				var binormal = source.Binormals?[i] ?? System.Numerics.Vector3.UnitY;
				if ( instance is not null )
				{
					position = instance.Transform( position );
					normal = System.Numerics.Vector3.Normalize( instance.TransformDirection( normal ) );
					tangent = System.Numerics.Vector3.Normalize( instance.TransformDirection( tangent ) );
					binormal = System.Numerics.Vector3.Normalize( instance.TransformDirection( binormal ) );
				}

				var vertex = new Halo3Vertex
				{
					Position = (Vector3)position * Halo3Mount.WorldScale,
					Normal = normal,
					Tangent = Halo3Vertex.Tangent4( normal, tangent, binormal ),
					TexCoord = source.TexCoords[i],
					LightmapTexCoord = lightmapTexCoords is not null && i < lightmapTexCoords.Length ? lightmapTexCoords[i] : default,
				};

				if ( vertexLighting is { Length: > 0 } )
				{
					var sh = vertexLighting[vertexLighting.Length == 1 ? 0 : Math.Min( i, vertexLighting.Length - 1 )];
					vertex.Sh0 = new Vector4( sh.C0, sh.Intensity.X );
					vertex.Sh1 = new Vector4( sh.C1, sh.Intensity.Y );
					vertex.Sh2 = new Vector4( sh.C2, sh.Intensity.Z );
					vertex.Sh3 = new Vector4( sh.C3, 0 );
				}

				vertices.Add( vertex );
			}

			return vertices;
		}

		// The level's collision model, Halo's collision or the drawn geometry when the BSP has none (null if neither has any), and the opaque aggregates.
		public (Model Collision, List<(Model Model, List<AggregateFragmentInfo> Fragments)> Aggregates) Create( (List<Vector3> Vertices, List<int> Indices, List<string> Materials) collision )
		{
			var aggregates = new List<(Model, List<AggregateFragmentInfo>)>();
			foreach ( var (material, fragments) in _fragments )
				aggregates.AddRange( Aggregates( $"{modelName}_aggregate_{aggregates.Count}", material, fragments, int.MaxValue ) );

			var (vertices, indices, materials) = collision;
			if ( indices.Count == 0 )
			{
				vertices = _renderVertices;
				indices = _renderIndices;
				materials = Enumerable.Repeat<string>( null, indices.Count / 3 ).ToList();
			}
			if ( indices.Count == 0 )
				return (null, aggregates);

			var builder = Model.Builder.WithName( $"{modelName}.vmdl" );
			builder.AddCollisionMesh( vertices, indices, Halo3Surfaces.AddSurfaces( builder, materials ) );
			builder.AddTraceMesh( vertices, indices );
			return (builder.Create(), aggregates);
		}
	}

	// Triangles one fragment of an aggregate draws, with only the vertices they use.
	readonly record struct AggregateFragment<T>( T[] Vertices, int[] Indices, BBox Bounds ) where T : unmanaged;

	// A material's fragments as aggregate models (name_0.vmdl, name_1.vmdl...), each holding as many as AggregateRenderer.MaxFragments and maxVertices allow.
	static List<(Model, List<AggregateFragmentInfo>)> Aggregates<T>( string name, Material material, List<AggregateFragment<T>> fragments, int maxVertices ) where T : unmanaged
	{
		var aggregates = new List<(Model, List<AggregateFragmentInfo>)>();
		var start = 0;
		while ( start < fragments.Count )
		{
			var count = 0;
			var vertexCount = 0;
			while ( start + count < fragments.Count && count < AggregateRenderer.MaxFragments && (count == 0 || vertexCount + fragments[start + count].Vertices.Length <= maxVertices) )
				vertexCount += fragments[start + count++].Vertices.Length;
			aggregates.Add( Aggregate( $"{name}_{aggregates.Count}.vmdl", material, fragments.GetRange( start, count ) ) );
			start += count;
		}
		return aggregates;
	}

	// One mesh of a material's fragments, each its own draw call (the first the mesh's range, the rest sub meshes), as s&box's scene compiler builds an aggregate.
	static (Model, List<AggregateFragmentInfo>) Aggregate<T>( string name, Material material, List<AggregateFragment<T>> fragments ) where T : unmanaged
	{
		var vertices = new T[fragments.Sum( f => f.Vertices.Length )];
		var indices = new int[fragments.Sum( f => f.Indices.Length )];
		var infos = new List<AggregateFragmentInfo>( fragments.Count );
		var bounds = fragments[0].Bounds;
		int vertex = 0, index = 0;
		foreach ( var fragment in fragments )
		{
			fragment.Vertices.CopyTo( vertices, vertex );
			fragment.Indices.CopyTo( indices, index );
			vertex += fragment.Vertices.Length;
			index += fragment.Indices.Length;
			infos.Add( new AggregateFragmentInfo( fragment.Bounds ) );
			bounds = bounds.AddBBox( fragment.Bounds );
		}

		var mesh = new Mesh( material );
		mesh.CreateVertexBuffer( vertices.Length, vertices );
		mesh.CreateIndexBuffer( indices.Length, indices );
		mesh.Bounds = bounds;
		mesh.SetVertexRange( 0, fragments[0].Vertices.Length );
		mesh.SetIndexRange( 0, fragments[0].Indices.Length );
		vertex = fragments[0].Vertices.Length;
		index = fragments[0].Indices.Length;
		for ( var i = 1; i < fragments.Count; i++ )
		{
			mesh.AddSubMesh( material, index, fragments[i].Indices.Length, vertex, fragments[i].Vertices.Length );
			vertex += fragments[i].Vertices.Length;
			index += fragments[i].Indices.Length;
		}
		return (Model.Builder.WithName( name ).AddMesh( mesh ).Create(), infos);
	}
}
