using Blam.Halo1;
using Blam.Halo1.Tags;

namespace Sandbox.Mounting.Blam.Halo1;

/// <summary>
/// Halo's planar fog: murky water, swamp mist, the haze filling a valley. A view is fogged by
/// the fog plane of the cluster the camera is in, if that cluster is under one, and by nothing
/// otherwise. Everything seen is fogged by how deep below that plane it is and how far it is in
/// front of the camera.
/// </summary>
/// <remarks>
/// Each fog plane gets a triangle covering the screen, drawn with halo1_fog_plane.shader. The
/// shader looks the camera's cluster up in the BSP's collision tree (a texture), draws only when
/// that cluster is under its plane, and works out the fog of whatever the depth buffer shows.
///
/// Water and glass draw what's behind them from s&amp;box's frame buffer copy and write over
/// it, and the copy doesn't have the fog in it. So they get their BSP's fog too
/// (<see cref="Fogs.ApplyTo"/>) and fog what they show themselves, and the fog leaves every pixel
/// seen through a fog plane's water surface alone (a water level and coverage mask per plane).
/// That way it doesn't matter which of the two s&amp;box happens to draw first.
/// </remarks>
static class PlanarFog
{
	public const string ShaderPath = "shaders/halo1_fog_plane.shader";

	/// <summary>
	/// On the fogs' own draws, which PlanarFogFrame sorts first and gives the frame behind them.
	/// </summary>
	public const string PlaneTag = "halo1_fog_plane";

	/// <summary>
	/// On the fogs' screen layer draws, which go after every other transparent thing (<see cref="PlanarFogFrame"/>).
	/// </summary>
	public const string ScreenTag = "halo1_fog_screen";

	/// <summary>
	/// Collision tree nodes per row of the tree texture, two texels each. The shaders use the same.
	/// </summary>
	const int TreeNodesPerRow = 1024;

	/// <summary>
	/// A fog's triangle is culled by the bounds of the clusters under its plane, grown by this
	/// much (in world units) so a camera on a cluster's edge still draws it.
	/// </summary>
	const float ClusterMargin = 1;

	/// <summary>
	/// Adds the fog of every fog plane in the scenario's BSPs that has a fog. Each fog's renderer
	/// carries its BSP's tag, so it's shown and hidden with its BSP.
	/// </summary>
	/// <param name="models">Models built here are added to this, to keep them alive.</param>
	/// <returns>The BSPs' fogs, to hand each of the level's BSPs to.</returns>
	public static Fogs Build( HaloCeMount host, CacheFile cache, ScenarioTag scenario, List<Model> models )
	{
		var levels = new Dictionary<int, Level>();
		var density = DensityTexture( cache );
		if ( density is null )
		{
			host.Logger.Warning( $"{cache.Name}: no planar fog density bitmap in the globals; no planar fog" );
			return new Fogs( levels );
		}

		var atmosphereDistance = AtmosphereDistance( cache, scenario );
		GameObject root = null;

		foreach ( var reference in scenario.StructureBsps )
		{
			var name = reference.Tag?.Path.Replace( '\\', '/' ) ?? $"{cache.Name}/bsp{reference.Index}";

			StructureBspFog fog;
			try
			{
				fog = StructureBspTag.ReadFog( cache, reference );
			}
			catch ( Exception e )
			{
				host.Logger.Warning( e, $"{name}: couldn't read fog planes: {e.Message}" );
				continue;
			}

			// Each plane's fog, and which of them each plane has (-1: none).
			var volumes = new List<Volume>();
			var planeVolumes = new int[fog.Planes.Count];
			List<(System.Numerics.Vector3, System.Numerics.Vector3, System.Numerics.Vector3)> water = null;
			for ( var i = 0; i < fog.Planes.Count; i++ )
			{
				planeVolumes[i] = -1;
				var fogTag = fog.GetFog( fog.Planes[i] );
				if ( fogTag is null )
					continue;

				var tag = new FogTag( cache, fogTag );
				var shape = FogVolume.Create( fog, i );
				if ( shape is null )
					continue;

				// The BSP's own water now (see FogVolume).
				water ??= new StructureBspTag( cache, reference ).ReadWaterTriangles().ToList();
				shape.AddWater( water );

				planeVolumes[i] = volumes.Count;
				volumes.Add( new Volume( shape, tag, volumes.Count ) );
			}

			if ( volumes.Count == 0 )
				continue;

			var level = new Level( volumes, TreeTexture( StructureBspTag.ReadClusterTree( cache, reference ), fog, planeVolumes ), density, atmosphereDistance );
			levels[reference.Index] = level;

			foreach ( var volume in volumes )
			{
				var model = BuildModel( cache, $"haloce/{name}/fog_plane_{volume.Index}.vmdl", volume, level, false );
				if ( model is null )
					continue;

				volume.UpdateWater();
				models.Add( model );
				root ??= new GameObject( true, "fog planes" );

				var go = new GameObject( root, true, $"{System.IO.Path.GetFileName( name )} fog plane {volume.Index}" );
				go.Tags.Add( ScenarioSceneLoader.BspTag( reference ) );
				go.Tags.Add( PlaneTag );
				var renderer = go.AddComponent<ModelRenderer>();
				renderer.Model = model;
				renderer.RenderType = ModelRenderer.ShadowRenderType.Off;

				// The screen layers are a draw of their own, last (see halo1_fog_plane.shader).
				if ( !volume.HasScreen || BuildModel( cache, $"haloce/{name}/fog_screen_{volume.Index}.vmdl", volume, level, true ) is not { } screenModel )
					continue;

				models.Add( screenModel );
				var screen = new GameObject( root, true, $"{System.IO.Path.GetFileName( name )} fog screen {volume.Index}" );
				screen.Tags.Add( ScenarioSceneLoader.BspTag( reference ) );
				screen.Tags.Add( ScreenTag );
				var screenRenderer = screen.AddComponent<ModelRenderer>();
				screenRenderer.Model = screenModel;
				screenRenderer.RenderType = ModelRenderer.ShadowRenderType.Off;
			}
		}

		if ( levels.Count > 0 )
			PlanarFogFrame.Watch();

		return new Fogs( levels );
	}

	/// <summary>
	/// One fog plane's fog, in s&amp;box units, and the material that draws it.
	/// </summary>
	public sealed class Volume( FogVolume shape, FogTag tag, int index )
	{
		public FogVolume Shape { get; } = shape;
		public FogTag Tag { get; } = tag;

		/// <summary>Which of its BSP's fogs this is: what the collision tree's leaves hold.</summary>
		public int Index { get; } = index;

		/// <summary>Normal and distance, the normal pointing out of the fog.</summary>
		public Vector4 Plane { get; } = new( (Vector3)shape.Normal, shape.Plane.Plane.W * HaloCeMount.WorldScale );
		public Vector3 Min => (Vector3)Shape.Min * HaloCeMount.WorldScale;
		public Vector3 Max => (Vector3)Shape.Max * HaloCeMount.WorldScale;

		/// <summary>Fog color, in gamma space like all Halo colors.</summary>
		public Vector3 Color { get; } = tag.Color;

		/// <summary>
		/// Halo takes a maximum density of 0 or less as 1. A fog that's only a screen effect has its
		/// screen layers but no fog.
		/// </summary>
		public float MaximumDensity { get; } = tag.Flags.HasFlag( FogFlags.FogScreenOnly ) ? 0 : tag.MaximumDensity <= 0 ? 1 : Math.Min( tag.MaximumDensity, 1 );

		/// <summary>Whether the atmospheric fog goes over this fog (c10) rather than under it.</summary>
		public bool AtmosphereDominant { get; } = tag.Flags.HasFlag( FogFlags.AtmosphereDominant );
		public float OpaqueDistance { get; } = MathF.Max( tag.OpaqueDistance, 0.001f ) * HaloCeMount.WorldScale;
		public float OpaqueDepth { get; } = MathF.Max( tag.OpaqueDepth, 0.001f ) * HaloCeMount.WorldScale;

		/// <summary>Bounds of the clusters under the plane: where the camera can be for this fog to draw.</summary>
		public BBox ClusterBounds { get; } = new(
			((Vector3)shape.Clusters.Select( c => c.Min ).Aggregate( System.Numerics.Vector3.Min ) - ClusterMargin) * HaloCeMount.WorldScale,
			((Vector3)shape.Clusters.Select( c => c.Max ).Aggregate( System.Numerics.Vector3.Max ) + ClusterMargin) * HaloCeMount.WorldScale );

		public Material Material { get; set; }

		/// <summary>Whether its screen layers add to the frame behind the fog (see SetScreenLayers).</summary>
		public bool Multipass { get; set; }

		/// <summary>Whether it has screen layers, drawn by a second object after the transparent things.</summary>
		public bool HasScreen { get; set; }

		/// <summary>
		/// Gives the fog's material the water under its plane: the level (along the plane's main
		/// axis, in s&amp;box units), which axes the mask spans, and the mask.
		/// </summary>
		public void UpdateWater()
		{
			if ( Material is null || Shape.WaterLevel is not { } level || Shape.WaterMask is null )
				return;

			var mask = new byte[Shape.WaterMask.Length * 4];
			for ( var i = 0; i < Shape.WaterMask.Length; i++ )
				mask[i * 4] = mask[i * 4 + 1] = mask[i * 4 + 2] = mask[i * 4 + 3] = Shape.WaterMask[i];

			var texture = Texture.Create( FogVolume.MaskSize, FogVolume.MaskSize )
				.WithData( mask )
				.WithStaticUsage()
				.Finish();

			Material.Set( "g_vPlanarFogBoundsMin", Min );
			Material.Set( "g_vPlanarFogBoundsMax", Max );
			Material.Set( "g_tPlanarFogWaterMask", texture );
			Material.Set( "g_vPlanarFogWater", new Vector4( Shape.Axis, level * HaloCeMount.WorldScale, 1, 0 ) );
		}
	}

	/// <summary>
	/// A BSP's fogs and what every shader that draws them needs: the collision tree to find the
	/// camera's fog with, Halo's planar fog density bitmap, and the atmospheric fog's opaque
	/// distance (s&amp;box units, 0 for none).
	/// </summary>
	public sealed class Level( List<Volume> volumes, Texture tree, Texture density, float atmosphereDistance )
	{
		readonly Lazy<Texture> _packed = new( () => Pack( volumes ) );

		public IReadOnlyList<Volume> Volumes => volumes;

		/// <summary>
		/// Sets the parameters every fog shader shares.
		/// </summary>
		public void SetShared( Material material )
		{
			material.Set( "g_tPlanarFogTree", tree );
			material.Set( "g_tPlanarFogDensity", density );
			material.Set( "g_flPlanarFogAtmosphereDistance", atmosphereDistance );
		}

		/// <summary>
		/// Gives a glass or water material all of the BSP's fogs.
		/// </summary>
		public void SetVolumes( Material material )
		{
			SetShared( material );
			material.Set( "g_tPlanarFogVolumes", _packed.Value );
			material.Set( "g_flPlanarFogVolumeCount", (float)volumes.Count );
		}

		/// <summary>
		/// The fogs packed for the glass and water shader: a row of three texels each,
		/// (plane), (opaque distance, opaque depth, maximum density, atmosphere dominant), (color).
		/// </summary>
		static Texture Pack( List<Volume> volumes )
		{
			const int width = 3;
			var data = new float[volumes.Count * width * 4];
			for ( var row = 0; row < volumes.Count; row++ )
			{
				var v = volumes[row];
				var texels = new[]
				{
					v.Plane,
					new Vector4( v.OpaqueDistance, v.OpaqueDepth, v.MaximumDensity, v.AtmosphereDominant ? 1 : 0 ),
					new Vector4( v.Color, 0 ),
				};

				for ( var x = 0; x < width; x++ )
				{
					var i = (row * width + x) * 4;
					data[i] = texels[x].x;
					data[i + 1] = texels[x].y;
					data[i + 2] = texels[x].z;
					data[i + 3] = texels[x].w;
				}
			}

			return Texture.Create( width, volumes.Count, ImageFormat.RGBA32323232F )
				.WithData( new ReadOnlySpan<float>( data ) )
				.WithStaticUsage()
				.Finish();
		}
	}

	/// <summary>
	/// The level's fogs, by structure BSP index.
	/// </summary>
	public sealed class Fogs( Dictionary<int, Level> levels )
	{
		/// <summary>
		/// Hands a BSP's model to the fog: the BSP's water surfaces (<paramref name="water"/>, null
		/// for none) go into the fogs' water masks, and every material of the model gets the BSP's
		/// fogs. Only the glass and water shader reads those, to fog what it shows through the
		/// surface; the rest ignore them.
		/// </summary>
		public void ApplyTo( Model model, StructureBspReference reference, StructureBspTag water )
		{
			if ( levels.Count == 0 )
				return;

			if ( water is not null )
			{
				var triangles = water.ReadWaterTriangles().ToList();
				foreach ( var volume in levels.Values.SelectMany( l => l.Volumes ) )
				{
					if ( triangles.Count > 0 && volume.Shape.AddWater( triangles ) )
						volume.UpdateWater();
				}
			}

			if ( model is null || !levels.TryGetValue( reference.Index, out var level ) )
				return;

			foreach ( var material in model.Materials )
			{
				if ( material is not null )
					level.SetVolumes( material );
			}
		}
	}

	/// <param name="screen">The screen layers' draw rather than the fog's.</param>
	static Model BuildModel( CacheFile cache, string name, Volume volume, Level level, bool screen )
	{
		var material = Material.Create( $"{name}.vmat", ShaderPath );
		if ( material is null )
			return null;

		level.SetShared( material );
		material.Set( "g_flPlanarFogIndex", (float)volume.Index );
		material.Set( "g_vPlanarFogPlane", volume.Plane );
		material.Set( "g_vPlanarFogColor", volume.Color );
		material.Set( "g_flPlanarFogMaximumDensity", volume.MaximumDensity );
		material.Set( "g_flPlanarFogAtmosphereDominant", volume.AtmosphereDominant ? 1f : 0f );
		material.Set( "g_flPlanarFogOpaqueDistance", volume.OpaqueDistance );
		material.Set( "g_flPlanarFogOpaqueDepth", volume.OpaqueDepth );
		SetScreenLayers( cache, material, volume );
		material.Set( "g_flFogPass", screen ? 2f : volume.HasScreen ? 1f : 0f );

		// No water until a BSP with some comes along (UpdateWater).
		material.Set( "g_tPlanarFogWaterMask", MaterialTextures.Transparent );
		material.Set( "g_vPlanarFogWater", new Vector4( 2, 0, 0, 0 ) );
		if ( !screen )
			volume.Material = material;

		// One triangle covering the screen: the vertex shader takes its positions as clip space.
		var mesh = new Mesh( material );
		var vertices = new List<HaloVertex>
		{
			new() { Position = new Vector3( -1, -1, 0 ), Normal = Vector3.Up, Tangent = new Vector4( 1, 0, 0, 1 ) },
			new() { Position = new Vector3( 3, -1, 0 ), Normal = Vector3.Up, Tangent = new Vector4( 1, 0, 0, 1 ) },
			new() { Position = new Vector3( -1, 3, 0 ), Normal = Vector3.Up, Tangent = new Vector4( 1, 0, 0, 1 ) },
		};
		mesh.CreateVertexBuffer( vertices.Count, vertices );
		mesh.CreateIndexBuffer( 3, new List<int> { 0, 1, 2 } );
		mesh.Bounds = volume.ClusterBounds;

		return Model.Builder.WithName( name ).AddMesh( mesh ).Create();
	}

	/// <summary>
	/// The fog's screen layers: noise drawn over the view while the camera is in the fog (see
	/// halo1_fog_plane.shader). No layers, no map or no far density turns them off, like Halo.
	/// </summary>
	static void SetScreenLayers( CacheFile cache, Material material, Volume volume )
	{
		var tag = volume.Tag;
		var atlas = tag.ScreenLayerCount > 0 && tag.ScreenFarDistance != 0 && tag.ScreenFarDensity != 0 && tag.ScreenLayerMap is not null
			? NoiseAtlas( cache, tag.ScreenLayerMap )
			: null;
		material.Set( "g_flFogScreenLayerCount", atlas is null ? 0f : Math.Min( tag.ScreenLayerCount, 4 ) );
		volume.HasScreen = atlas is not null;
		if ( atlas is null )
			return;

		var (texture, count) = atlas.Value;
		var scale = HaloCeMount.WorldScale;
		material.Set( "g_tFogScreenMap", texture );
		material.Set( "g_vFogScreenGradient", new Vector4( tag.ScreenNearDistance, tag.ScreenFarDistance, tag.ScreenNearDensity, tag.ScreenFarDensity ) );
		material.Set( "g_vFogScreenEye", new Vector4( tag.ScreenStartDistanceFromPlane, MathF.Max( tag.OpaqueDepth, 0.0001f ), tag.ScreenMapScale, count ) );
		material.Set( "g_vFogScreenMotion", new Vector4( tag.ScreenStrafingMultiplier, tag.ScreenZoomMultiplier, tag.ScreenAnimationPeriod,
			(tag.ScreenWindVelocity.X + tag.ScreenWindVelocity.Y) * 0.5f ) );
		material.Set( "g_vFogScreenColor", (Vector3)(tag.ScreenColor ?? tag.Color) );
		material.Set( "g_flFogScreenWorldScale", scale );

		// Halo's multipass mode: with different near and far densities the layers are added to the
		// scene, scaled by the globals' planar fog density curve over the scene's depth.
		volume.Multipass = tag.ScreenNearDensity != tag.ScreenFarDensity && (tag.ScreenFlags & 0x1) == 0;
		material.Set( "g_flFogScreenMultipass", volume.Multipass ? 1f : 0f );
	}

	/// <summary>
	/// A screen layer map's bitmaps side by side (each one's R, G and B are three frames of noise),
	/// with one more row under them holding each bitmap's mean color (see WorldNoise in the shader).
	/// </summary>
	static (Texture Texture, int Count)? NoiseAtlas( CacheFile cache, TagEntry map )
	{
		var bitmaps = new BitmapTag( cache, map ).Bitmaps
			.Where( b => b.Type == BitmapType.Texture2D && (BitmapDecoder.CanDecode( b.Format ) || b.IsBlockCompressed) )
			.ToList();
		if ( bitmaps.Count == 0 )
			return null;

		int width = bitmaps[0].Width, height = bitmaps[0].Height;
		bitmaps = bitmaps.Where( b => b.Width == width && b.Height == height ).ToList();
		var atlas = new byte[width * bitmaps.Count * (height + 1) * 4];
		for ( var i = 0; i < bitmaps.Count; i++ )
		{
			var b = bitmaps[i];
			var pixels = BitmapTag.ReadPixels( cache, b );
			var rgba = new byte[width * height * 4];
			if ( b.Format == BitmapFormat.Dxt3 )
				BitmapDecoder.Dxt3ToRgba8888( pixels, rgba, width, height );
			else if ( b.IsBlockCompressed )
				BitmapDecoder.DxtColorToRgba8888( b.Format, pixels, rgba, width, height );
			else
				BitmapDecoder.ToRgba8888( b.Format, pixels, rgba, width * height );

			for ( var y = 0; y < height; y++ )
				Array.Copy( rgba, y * width * 4, atlas, ((y * bitmaps.Count + i) * width) * 4, width * 4 );

			var mean = new long[4];
			for ( var p = 0; p < width * height * 4; p++ )
				mean[p % 4] += rgba[p];

			var row = (height * bitmaps.Count + i) * width * 4;
			for ( var x = 0; x < width * 4; x++ )
				atlas[row + x] = (byte)Math.Round( mean[x % 4] / (double)(width * height) );
		}

		var texture = Texture.Create( width * bitmaps.Count, height + 1 )
			.WithData( atlas )
			.WithStaticUsage()
			.Finish();
		return (texture, bitmaps.Count);
	}

	/// <summary>
	/// Halo's planar fog density bitmap, from the globals' rasterizer data (0x134, 0x1AC each:
	/// planar fog density 0x30), as stored: across, how far in front of the camera over the
	/// opaque distance; down, how deep below the plane over the opaque depth. Its luminance is the
	/// fog for a camera deep in the fog, its alpha for one above it.
	/// </summary>
	static Texture DensityTexture( CacheFile cache )
	{
		var globals = cache.Tags.FirstOrDefault( t => t.Class.ToString() == "matg" );
		if ( globals is null )
			return null;

		var entry = cache.TagData.Block( globals.Address + 0x134, 0x1AC ).FirstOrDefault();
		var tag = entry == 0 ? null : cache.GetTagReference( cache.TagData, entry + 0x30 );
		var bitmap = tag is null ? null : new BitmapTag( cache, tag ).Bitmaps.FirstOrDefault( b => b.Type == BitmapType.Texture2D && BitmapDecoder.CanDecode( b.Format ) );
		if ( bitmap is null )
			return null;

		var rgba = new byte[bitmap.Width * bitmap.Height * 4];
		BitmapDecoder.ToRgba8888( bitmap.Format, BitmapTag.ReadPixels( cache, bitmap ), rgba, bitmap.Width * bitmap.Height );
		return Texture.Create( bitmap.Width, bitmap.Height )
			.WithData( rgba )
			.WithStaticUsage()
			.Finish();
	}

	/// <summary>
	/// The sky's atmospheric fog opaque distance in s&amp;box units, or 0 if it has none. How high
	/// the camera is above a fog plane over this is how much the atmospheric fog goes over the
	/// planar fog's color.
	/// </summary>
	static float AtmosphereDistance( CacheFile cache, ScenarioTag scenario )
	{
		if ( scenario.Skies.Count == 0 || scenario.Skies[0] is null )
			return 0;

		var sky = new SkyTag( cache, scenario.Skies[0] );
		return MathF.Max( sky.OutdoorFogOpaqueDistance, 0 ) * HaloCeMount.WorldScale;
	}

	/// <summary>
	/// A BSP's collision tree for the shaders to find the camera's fog with: two texels per node,
	/// (plane normal, distance in s&amp;box units) and (back child, front child). A child is a node
	/// index, or for a leaf -(fog + 2), the fog of the plane its cluster is under, or -1 for no fog
	/// (and outside the level).
	/// </summary>
	static Texture TreeTexture( BspClusterTree tree, StructureBspFog fog, int[] planeVolumes )
	{
		float Child( int child )
		{
			if ( child == -1 || (child & 0x80000000) == 0 )
				return child >= tree.Nodes.Count ? -1 : child;

			var leaf = child & 0x7FFFFFFF;
			var cluster = leaf < tree.LeafClusters.Count ? tree.LeafClusters[leaf] : -1;
			var plane = cluster >= 0 && cluster < fog.Clusters.Count ? fog.Clusters[cluster].FogPlane : -1;
			var volume = plane >= 0 && plane < planeVolumes.Length ? planeVolumes[plane] : -1;
			return volume >= 0 ? -(volume + 2) : -1;
		}

		var count = Math.Max( tree.Nodes.Count, 1 );
		var rows = (count + TreeNodesPerRow - 1) / TreeNodesPerRow;
		var data = new float[TreeNodesPerRow * 2 * rows * 4];
		for ( var n = 0; n < count; n++ )
		{
			var i = n * 8;
			if ( n >= tree.Nodes.Count || tree.Nodes[n].Plane < 0 || tree.Nodes[n].Plane >= tree.Planes.Count )
			{
				// Nowhere: both children outside.
				data[i + 4] = data[i + 5] = -1;
				continue;
			}

			var node = tree.Nodes[n];
			var plane = tree.Planes[node.Plane];
			data[i] = plane.X;
			data[i + 1] = plane.Y;
			data[i + 2] = plane.Z;
			data[i + 3] = plane.W * HaloCeMount.WorldScale;
			data[i + 4] = Child( node.Back );
			data[i + 5] = Child( node.Front );
		}

		return Texture.Create( TreeNodesPerRow * 2, rows, ImageFormat.RGBA32323232F )
			.WithData( new ReadOnlySpan<float>( data ) )
			.WithStaticUsage()
			.Finish();
	}
}
