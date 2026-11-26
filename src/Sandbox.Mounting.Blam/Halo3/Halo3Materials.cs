using Blam;
using Blam.Halo3;
using Blam.Halo3.Tags;
using MaterialTextures = Sandbox.Mounting.Blam.Halo1.MaterialTextures;

namespace Sandbox.Mounting.Blam.Halo3;

// How a piece of geometry gets its static lighting, which picks the material's lighting path.
enum Halo3Lighting
{
	// The BSP's SH lightmap arrays, at the vertex's lightmap texcoord.
	PerPixel,

	// SH per vertex (Halo3Vertex.Sh0..Sh3).
	PerVertex,

	// One L2 SH probe for the whole mesh (instances lit by a single probe, objects): Halo3Lightmap.SetProbe on the renderer; a neutral ambient until then.
	Probe,

	Unlit,

	// A colour per vertex (Halo3Vertex.Sh0.rgb) as the whole light, diffuse only: MCC's static_per_vertex_color, which lights sky models from their baked vertex colours.
	VertexColor,
}

// Materials for Halo 3 render method tags (rmsh, rmtr, rmfl, rmhg, rmw, rmd, rmcs, rmct): halo3_shader.shader and its variants.
// Each category's chosen option becomes a mode number of the shader, each texture parameter one of its slots (with its transform and sRGB-ness), each real a parameter by name.
// One image of a BSP's baked cube maps bitmap (ScenarioStructureBsp.Cubemaps), the environment a dynamic environment_mapping surface reflects; equal by tag and image so materials share.
sealed record Halo3Cubemap( TagEntry Bitmap, int Index )
{
	public bool Equals( Halo3Cubemap other ) => other is not null && other.Bitmap?.Id == Bitmap?.Id && other.Index == Index;
	public override int GetHashCode() => HashCode.Combine( Bitmap?.Id, Index );
}

static partial class Halo3Materials
{
	public const string ShaderPath = "shaders/halo3_shader.shader";
	public const string TransparentShaderPath = "shaders/halo3_shader_transparent.shader";
	public const string TerrainShaderPath = "shaders/halo3_terrain.shader";
	public const string WaterShaderPath = "shaders/halo3_water.shader";

	// Materials made, per map (cache) for the two maps used last: older maps' materials are dropped so they and their textures can go once their scenes do.
	static readonly List<(CacheFile Cache, Dictionary<(TagEntry Shader, Halo3Lighting Lighting, Halo3Lightmap Lightmap, float SkyScale, Halo3Cubemap Cubemap, TagEntry Surface, TagEntry AlbedoDecal), Material> Materials)> Created = new();
	const int MapsKept = 2;

	static Dictionary<(TagEntry, Halo3Lighting, Halo3Lightmap, float, Halo3Cubemap, TagEntry, TagEntry), Material> CreatedFor( CacheFile cache )
	{
		var index = Created.FindIndex( c => c.Cache == cache );
		var entry = index >= 0 ? Created[index] : (cache, new Dictionary<(TagEntry, Halo3Lighting, Halo3Lightmap, float, Halo3Cubemap, TagEntry, TagEntry), Material>());
		if ( index >= 0 )
			Created.RemoveAt( index );
		Created.Insert( 0, entry );
		if ( Created.Count > MapsKept )
			Created.RemoveRange( MapsKept, Created.Count - MapsKept );
		return entry.Item2;
	}

	// Materials whose texture transforms follow time-driven overlays halo3_shader can't (any wave but a ramp: cosine wobbles, curves, scales), re-evaluated on the CPU each frame by Animate. Each entry is the transform parameter, its tag value, and the overlays on it: 2 uniform scale, 3/4 scale x/y, 5/6 translation x/y.
	sealed record AnimatedTransform( Material Material, CacheFile Cache, string Parameter, Vector4 Base, List<(int Type, float Period, Halo3Function Function)> Overlays );

	static readonly List<AnimatedTransform> Animated = new();
	static float _animatedAt = -1;

	// Moves every animated transform to time t (Halo3SceneUpkeep, once per frame): Halo's function of frac( t / period ) per overlay, over the tag's transform; drops entries of maps no longer cached.
	public static void Animate( float t )
	{
		lock ( Created )
		{
			if ( t == _animatedAt )
				return;
			_animatedAt = t;
			for ( var i = Animated.Count - 1; i >= 0; i-- )
			{
				var a = Animated[i];
				if ( Created.All( c => c.Cache != a.Cache ) )
				{
					Animated.RemoveAt( i );
					continue;
				}

				var xform = a.Base;
				foreach ( var (type, period, function) in a.Overlays )
				{
					var x = t / period;
					var v = function.Evaluate( x - MathF.Floor( x ) );
					switch ( type )
					{
						case 2: xform.x = xform.y = v; break;
						case 3: xform.x = v; break;
						case 4: xform.y = v; break;
						case 5: xform.z = v; break;
						default: xform.w = v; break;
					}
				}
				a.Material.Set( a.Parameter, xform );
			}
		}
	}

	// Forgets every material made (mount shutdown).
	public static void Clear()
	{
		lock ( Created )
		{
			Created.Clear();
			Animated.Clear();
		}
		lock ( Exposures )
			Exposures.Clear();
		lock ( Atmospheres )
			Atmospheres.Clear();
	}

	// A material for a render method tag, or null for one that isn't drawn this way (particles, beams, light volumes...).
	// Materials are shared per (shader, lighting, lightmap, sky scale, cube map): the first caller's name sticks.
	// skyScale > 0: for a sky drawn as a 3D skybox that many times larger (its fog measures distances that much larger).
	// cubemap: the BSP's baked cube map the surface reflects when its environment_mapping option is dynamic (MCC binds the cluster's; a cubemap per material since BSP meshes can't carry attributes).
	// surface: for a pre-lighting decal, the shader of the surface under it, whose lighting terms it takes (LightLike).
	// albedoDecal: a render model's multiply decal drawn in this surface's albedo (AddModelDecalAlbedo), its own material rather than a copy, which a model resolved by name to the surface's.
	public static Material Create( string name, TagEntry shader, Halo3Lighting lighting, Halo3Lightmap lightmap = null, float skyScale = 0, Halo3Cubemap cubemap = null, TagEntry surface = null, TagEntry albedoDecal = null )
	{
		if ( shader is null )
			return null;
		var key = (shader, lighting, lighting == Halo3Lighting.PerPixel ? lightmap : null, skyScale, cubemap, surface, albedoDecal);
		lock ( Created )
		{
			if ( CreatedFor( shader.Cache ).TryGetValue( key, out var existing ) )
				return existing;
		}

		var decorator = shader.Class == DecoratorSetClass;
		var tag = decorator ? null : ShaderTag.Get( shader );
		if ( !decorator && tag?.Definition is null )
			return null;

		Material material;
		try
		{
			material = decorator ? Decorator( name, shader ) : tag.Kind switch
			{
				"terrain" => Terrain( name, tag ),
				"shader" or "foliage" or "halogram" or "custom" or "cortana" or "decal" => Standard( name, tag ),
				"water" => Water( name, tag ),
				_ => null,
			};
		}
		catch ( Exception e )
		{
			Halo3Mount.Current?.Logger.Warning( e, $"Material for {shader}: {e.Message}" );
			material = null;
		}

		if ( material is null )
			return null;

		if ( surface is not null )
			LightLike( material, tag, ShaderTag.Get( surface ) );
		if ( tag?.Kind == "decal" )
			material.Set( "g_flDecalKill", 1f );
		if ( albedoDecal is not null && ShaderTag.Get( albedoDecal ) is { } decalTag )
			AddModelDecalAlbedo( material, decalTag );
		SetLighting( material, lighting, lightmap );
		material.Set( "g_flExposureStops", CameraExposure( shader.Cache ) );
		if ( cubemap is not null && (tag?.Option( "environment_mapping" ) ?? tag?.Option( "environment_map" )) == "dynamic" )
			SetDynamicCubemap( material, cubemap );

		// Render method flags: 1 don't fog, 2 the sky's atmosphere (MCC fogs sky shaders with the skya setting flagged for the sky).
		var flags = tag?.Flags ?? 0;
		if ( (flags & 1) != 0 )
			material.Set( "g_flNoFog", 1f );
		else if ( (flags & 2) != 0 && (Halo3Atmosphere.ForSky( shader.Cache ) ?? Atmosphere( shader.Cache )) is { } skyFog )
		{
			skyFog.Apply( material, skyScale );
			material.Set( "g_flSkyFog", 1f );
		}
		else if ( Atmosphere( shader.Cache ) is { } fog )
			fog.Apply( material, skyScale );

		lock ( Created )
			CreatedFor( shader.Cache )[key] = material;
		return material;
	}

	// The baked cube map a dynamic environment_mapping surface reflects, in the EnvMap slot (a 6:1 face atlas, as the per_pixel option's cube): MCC reads it as rgb * a * 256 at a level set by the roughness.
	static void SetDynamicCubemap( Material material, Halo3Cubemap cubemap )
	{
		var loaded = Texture.Load( Halo3Mount.TexturePath( cubemap.Bitmap.Path, cubemap.Index ) );
		if ( loaded is null || loaded.IsError )
			return;
		material.Set( "g_tEnvMap", loaded );
		material.Set( "g_flEnvMapSrgb", ShaderBitmaps.IsSrgb( cubemap.Bitmap, cubemap.Index ) ? 1f : 0f );
		material.Set( "g_flHasEnvMap", 1f );
	}

	// Lighting path and, per pixel, the BSP's lightmap arrays as bindless indices with their scales.
	static void SetLighting( Material material, Halo3Lighting lighting, Halo3Lightmap lightmap )
	{
		var lightprobe = lighting == Halo3Lighting.PerPixel ? lightmap?.LightprobeArray : null;
		var mode = lighting switch
		{
			Halo3Lighting.PerPixel when lightprobe is not null => 1,
			Halo3Lighting.PerVertex => 2,
			Halo3Lighting.Probe => 3,
			Halo3Lighting.VertexColor => 4,
			_ => 0,
		};
		material.Set( "g_flLightingMode", (float)mode );
		if ( mode != 1 )
			return;

		var intensity = lightmap.IntensityArray;
		var s = lightmap.Scales;
		material.Set( "g_flLightprobeArray", (float)lightprobe.Index );
		material.Set( "g_flIntensityArray", intensity is null ? -1f : intensity.Index );
		material.Set( "g_vLightmapScales", new Vector4( s[0], s[1], s[2], s[3] ) );
		material.Set( "g_flLightmapScale4", s[4] );
	}

	// halo3_shader_transparent's F_BLEND_MODE for a blend_mode option, -1 for opaque.
	static int BlendMode( string option ) => option switch
	{
		null or "opaque" => -1,
		"additive" or "add_src_times_srcalpha" or "add_src_times_dstalpha" or "multiply_add" or "maximum" => 0,
		"alpha_blend" => 1,
		"multiply" => 2,
		"double_multiply" => 3,
		"pre_multiplied_alpha" => 4,
		"inv_alpha_blend" => 5,
		_ => 1,
	};

	// shaders\shader and the definitions sharing its categories (foliage, halogram, custom, cortana, decal).
	static Material Standard( string name, ShaderTag tag )
	{
		var blend = BlendMode( tag.Option( "blend_mode" ) );
		var material = Material.Create( name, blend < 0 ? ShaderPath : TransparentShaderPath, anonymous: true );
		if ( material is null )
			return null;
		if ( blend >= 0 )
			material.SetFeature( "F_BLEND_MODE", blend );

		SetDefaults( material, StandardSlots );
		foreach ( var texture in tag.Textures )
		{
			if ( TextureSlot( texture.Name ) is { } slot )
				SetTexture( material, slot, texture, tag );
		}

		material.Set( "g_flAlbedoMode", (float)AlbedoMode( tag.Option( "albedo" ) ) );
		material.Set( "g_flBumpMode", (float)BumpMode( tag.Option( "bump_mapping" ) ) );
		material.Set( "g_flAlphaTest", tag.Option( "alpha_test" ) is null or "none" ? 0f : 1f );
		material.Set( "g_flSpecularMaskMode", (float)SpecularMaskMode( tag.Option( "specular_mask" ) ) );
		material.Set( "g_flMaterialModel", (float)MaterialModel( tag ) );
		material.Set( "g_flEnvMode", (float)EnvMode( tag.Option( "environment_mapping" ) ) );
		material.Set( "g_flSelfIllumMode", (float)SelfIllumMode( tag.Option( "self_illumination" ) ) );
		SetSelfIllumWaves( material, tag );
		material.Set( "g_flParallaxMode", tag.Option( "parallax" ) is null or "off" ? 0f : 1f );
		material.Set( "g_flOverlayMode", (float)OverlayMode( tag.Option( "overlay" ) ) );
		material.Set( "g_flEdgeFade", tag.Option( "edge_fade" ) is "simple" ? 1f : 0f );
		if ( tag.Bool( "use_material_texture" ) == true )
			material.Set( "g_flUseMaterialTexture", 1f );

		foreach ( var (real, parameter, components) in Reals )
		{
			if ( tag.Real( real ) is { } v )
				SetReal( material, parameter, components, v );
		}

		// Decals: the base map is whatever the albedo option samples, tinted when the tinting option says so.
		if ( tag.Kind == "decal" )
		{
			if ( tag.Option( "albedo" ) == "change_color" && tag.Texture( "change_color_map" ) is { } cc )
				SetTexture( material, "BaseMap", cc, tag );
			if ( tag.Option( "tinting" ) is { } tinting && tinting != "none" )
			{
				var t = tag.Vector( "tint_color", System.Numerics.Vector4.One );
				material.Set( "g_vTint", new Vector4( t.X, t.Y, t.Z, tag.Scalar( "intensity", 1 ) ) );
			}
		}

		// Chameleon: the facing and grazing colours and the fresnel power where halo3_shader reads them.
		if ( tag.Option( "albedo" )?.StartsWith( "chameleon" ) == true )
		{
			SetReal( material, "g_vAlbedoColor", 4, tag.Vector( "chameleon_color0", System.Numerics.Vector4.One ) );
			SetReal( material, "g_vAlbedoColor2", 4, tag.Vector( "chameleon_color1", System.Numerics.Vector4.One ) );
			material.Set( "g_vAlbedoColor3", new Vector4( 1, 1, 1, tag.Scalar( "chameleon_fresnel_power", 1 ) ) );
		}

		// Plasma: the three band thinnesses in one vector.
		if ( tag.Real( "thinness_medium" ) is not null )
			material.Set( "g_vPlasmaThinness", new Vector3( tag.Scalar( "thinness_medium", 1 ), tag.Scalar( "thinness_wide", 1 ), tag.Scalar( "thinness_sharp", 1 ) ) );

		return material;
	}

	// Terrain: up to four materials (base, detail, bump, detail bump each) blended by the blend map's channels.
	static Material Terrain( string name, ShaderTag tag )
	{
		var material = Material.Create( name, TerrainShaderPath, anonymous: true );
		if ( material is null )
			return null;

		SetDefaults( material, TerrainSlots );
		foreach ( var texture in tag.Textures )
		{
			if ( TerrainSlot( texture.Name ) is { } slot )
				SetTexture( material, slot, texture, tag );
		}

		var modes = new float[4];
		for ( var m = 0; m < 4; m++ )
		{
			var option = tag.Option( $"material_{m}" );
			modes[m] = option switch
			{
				null or "off" => 0,
				_ when option.Contains( "self_illum" ) => 3,
				_ when !option.Contains( "specular" ) => 1,
				_ => 2,
			};
		}
		material.Set( "g_vTerrainMaterials", new Vector4( modes[0], modes[1], modes[2], modes[3] ) );

		Vector4 PerMaterial( string prefix, float fallback ) => new(
			tag.Scalar( $"{prefix}_m_0", fallback ), tag.Scalar( $"{prefix}_m_1", fallback ), tag.Scalar( $"{prefix}_m_2", fallback ), tag.Scalar( $"{prefix}_m_3", fallback ) );

		material.Set( "g_vTerrainDiffuse", PerMaterial( "diffuse_coefficient", 1 ) );
		material.Set( "g_vTerrainSpecular", PerMaterial( "specular_coefficient", 0 ) );
		material.Set( "g_vTerrainPower", PerMaterial( "specular_power", 10 ) );
		material.Set( "g_vTerrainAnalytical", PerMaterial( "analytical_specular_contribution", 0.5f ) );
		material.Set( "g_vTerrainArea", PerMaterial( "area_specular_contribution", 0.5f ) );
		material.Set( "g_vTerrainTintBlend", PerMaterial( "albedo_specular_tint_blend", 0 ) );
		material.Set( "g_vTerrainFresnel", PerMaterial( "fresnel_curve_steepness", 5 ) );
		material.Set( "g_vTerrainEnv", PerMaterial( "environment_specular_contribution", 0 ) );
		material.Set( "g_flEnvMode", (float)EnvMode( tag.Option( "environment_map" ) ) );
		var envTint = tag.Vector( "env_tint_color", System.Numerics.Vector4.One );
		material.Set( "g_vEnvTint", new Vector3( envTint.X, envTint.Y, envTint.Z ) );
		material.Set( "g_flEnvRoughnessScale", tag.Scalar( "env_roughness_scale", 1 ) );
		for ( var m = 0; m < 4; m++ )
		{
			var t = tag.Vector( $"specular_tint_m_{m}", System.Numerics.Vector4.One );
			material.Set( $"g_vTerrainTint{m}", new Vector3( t.X, t.Y, t.Z ) );
		}
		var tint = tag.Vector( "global_albedo_tint", System.Numerics.Vector4.One );
		material.Set( "g_vGlobalAlbedoTint", new Vector3( tint.X, tint.Y, tint.Z ) );

		if ( modes[1] > 2.5f )
		{
			material.Set( "g_flSelfIllumMode", 1f );
			if ( tag.Real( "self_illum_color_m_1" ) is { } c )
				material.Set( "g_vSelfIllumColor", new Vector3( c.X, c.Y, c.Z ) );
			material.Set( "g_flSelfIllumIntensity", tag.Scalar( "self_illum_intensity_m_1", 1 ) );
		}
		return material;
	}

	static readonly TagClass DecoratorSetClass = new( "dctr" );

	// A decorator set (dctr: grass, plants, rocks; no render method): its one bitmap (dctr +0x20) as an alpha-tested, two-sided foliage material; dctr +0x31 render shader 2 "static" and 5 "shaded" draw as plain diffuse.
	static Material Decorator( string name, TagEntry dctr )
	{
		var material = Material.Create( name, ShaderPath, anonymous: true );
		if ( material is null )
			return null;
		material.SetFeature( "F_TWO_SIDED", 1 );
		SetDefaults( material, StandardSlots );

		var bitmap = dctr.Data.Reference( 0x20 );
		if ( bitmap is not null )
		{
			var texture = new ShaderTexture( "base_map", bitmap, 0, 0, 0, 0, System.Numerics.Vector4.UnitX + System.Numerics.Vector4.UnitY );
			SetTexture( material, "BaseMap", texture, null );
			SetTexture( material, "AlphaTestMap", texture, null );
			// 2: a decorator's alpha test, against 0.3 as MCC's (halo3_shader).
			material.Set( "g_flAlphaTest", 2f );
		}
		var shading = dctr.Data.U8( 0x31 );
		material.Set( "g_flMaterialModel", shading is 2 or 5 ? 0f : 4f );
		return material;
	}

	// Water (rmw): halo3_water (see H3Water), MCC's water shaders: wave slope array normals and displacement, the static cube reflection by fresnel, a body of water colour through which the scene behind shows (bent by the waves), foam.
	static Material Water( string name, ShaderTag tag )
	{
		var material = Material.Create( name, WaterShaderPath, anonymous: true );
		if ( material is null )
			return null;
		material.SetFeature( "F_BLEND_MODE", 4 );
		SetDefaults( material, StandardSlots );
		material.Set( "g_flWorldScale", Halo3Mount.WorldScale );

		if ( tag.Texture( "environment_map" ) is { } env )
			SetTexture( material, "EnvMap", env, tag );
		if ( tag.Option( "watercolor" ) == "texture" && tag.Texture( "watercolor_texture" ) is { } watercolor )
		{
			SetTexture( material, "BaseMap", watercolor, tag );
			material.Set( "g_flWaterColorTexture", 1f );
		}
		// bankalpha paint: MCC's water PS fades the banks by watercolor_texture's alpha, whatever the water colour (Zanzibar's pure water over the beach).
		if ( tag.Option( "bankalpha" ) == "paint" && tag.Texture( "watercolor_texture" ) is { } bankPaint )
		{
			if ( tag.Option( "watercolor" ) != "texture" )
				SetTexture( material, "BaseMap", bankPaint, tag );
			material.Set( "g_flWaterBankPaint", 1f );
		}
		if ( tag.Texture( "bump_map" ) is { } bump )
			SetTexture( material, "BumpMap", bump, tag );

		// The wave slope array: its layers are the animation's frames, the time warp overlays' periods its speeds.
		if ( tag.Texture( "wave_slope_array" ) is { Bitmap: not null } slopes )
		{
			var waves = Texture.Load( Halo3Mount.TexturePath( slopes.Bitmap.Path, Math.Max( 0, slopes.BitmapIndex ) ) );
			if ( waves is not null && !waves.IsError && waves.Depth > 1 )
			{
				material.Set( "g_flWaveSlopeArray", (float)waves.Index );
				SetTransform( material, "g_vWaveSlope", slopes, tag );
				material.Set( "g_vWaterWave", new Vector4( tag.Scalar( "slope_range_x", 0.5f ), tag.Scalar( "slope_range_y", 0.5f ), waves.Depth, WarpPeriod( tag, "time_warp", 1 ) ) );
				material.Set( "g_vWaterDetail", new Vector4( tag.Scalar( "detail_slope_scale_x", 4 ), tag.Scalar( "detail_slope_scale_y", 4 ), tag.Scalar( "detail_slope_steepness", 0.5f ), WarpPeriod( tag, "time_warp_aux", 1.3f ) ) );
				material.Set( "g_flWaterDamping", tag.Scalar( "wave_visual_damping_distance", 8 ) );
				var displacementTexture = tag.Texture( "wave_displacement_array" );
				var displacement = displacementTexture?.Transform ?? new System.Numerics.Vector4( 1, 1, 0, 0 );

				// MCC's water vertex shader moves the (tessellated) surface by the displacement array (waveshape default).
				var displacementArray = tag.Option( "waveshape" ) == "default" && displacementTexture is { Bitmap: not null } ? Texture.Load( Halo3Mount.TexturePath( displacementTexture.Bitmap.Path, Math.Max( 0, displacementTexture.BitmapIndex ) ) ) : null;
				if ( displacementArray is { IsError: false } && displacementArray.Depth > 1 )
				{
					material.Set( "g_flWaveDisplacementArray", (float)displacementArray.Index );
					material.Set( "g_vDisplacementRange", new Vector4( tag.Scalar( "displacement_range_x", 0 ), tag.Scalar( "displacement_range_y", 0 ), tag.Scalar( "displacement_range_z", 0 ), 0 ) );
					material.Set( "g_vChoppiness", new Vector4( tag.Scalar( "choppiness_forward", 1 ), tag.Scalar( "choppiness_backward", 1 ), tag.Scalar( "choppiness_side", 1 ), 0 ) );
				}
				material.Set( "g_vWaveDisplacementXform", new Vector4( displacement.X, displacement.Y, displacement.Z, displacement.W ) );
				material.Set( "g_vWaterHeights", new Vector4( tag.Scalar( "wave_height", 0.5f ), tag.Scalar( "wave_height_aux", 0.5f ), tag.Scalar( "detail_slope_scale_z", 1 ), tag.Option( "waveshape" ) == "default" ? 1 : 0 ) );
			}
		}

		var color = tag.Vector( "water_color_pure", new System.Numerics.Vector4( 0.03f, 0.06f, 0.07f, 1 ) );
		material.Set( "g_vWaterColor", new Vector4( color.X, color.Y, color.Z, Math.Max( tag.Scalar( "water_murkiness", 3 ), 0.05f ) ) );
		var waterDiffuse = tag.Vector( "water_diffuse", System.Numerics.Vector4.Zero );
		material.Set( "g_vWaterDiffuse", new Vector4( waterDiffuse.X, waterDiffuse.Y, waterDiffuse.Z, tag.Scalar( "watercolor_coefficient", 1 ) ) );
		material.Set( "g_vWaterRefraction", new Vector2( tag.Scalar( "refraction_depth_dominant_ratio", 0.95f ), tag.Scalar( "refraction_extinct_distance", 1000 ) ) );
		var bank = tag.Option( "bankalpha" ) == "depth" ? tag.Scalar( "bankalpha_infuence_depth", 0.2f ) : 0;

		// The global shape (how strong the waves are where): painted in global_shape_texture's red at the water's per-vertex global texture coordinate, or the depth over globalshape_infuence_depth.
		var globalShape = tag.Option( "global_shape" ) switch { "paint" => 1f, "depth" => 2f, _ => 0f };
		material.Set( "g_vGlobalShape", new Vector2( globalShape, tag.Scalar( "globalshape_infuence_depth", 1 ) ) );
		// Foam (foam_texture in DetailMap, foam_texture_detail in DetailMap2): from the wave height (auto), painted in the global shape texture's blue (paint), or both.
		var foam = tag.Option( "foam" ) switch { "auto" => 1f, "paint" => 2f, "both" => 3f, _ => 0f };
		if ( foam > 0 && tag.Texture( "foam_texture" ) is { } foamTexture )
			SetTexture( material, "DetailMap", foamTexture, tag );
		if ( foam > 0 && tag.Texture( "foam_texture_detail" ) is { } foamDetail )
			SetTexture( material, "DetailMap2", foamDetail, tag );
		material.Set( "g_vWaterFoam", new Vector4( tag.Scalar( "foam_height", 0.5f ), tag.Scalar( "foam_pow", 1 ), foam, 0 ) );

		// Dynamic refraction reads the scene behind from a copy of the frame, bent by the waves.
		material.Set( "g_vWaterRefract", new Vector4( tag.Scalar( "refraction_texcoord_shift", 0 ), tag.Scalar( "minimal_wave_disturbance", 1 ), tag.Option( "refraction" ) == "dynamic" ? 1 : 0, 0 ) );

		var shapeTexture = (globalShape == 1 || foam >= 2) && tag.Texture( "global_shape_texture" ) is { Bitmap: not null } shape ? Texture.Load( Halo3Mount.TexturePath( shape.Bitmap.Path, Math.Max( 0, shape.BitmapIndex ) ) ) : null;
		material.Set( "g_flGlobalShapeTexture", shapeTexture is { IsError: false } ? shapeTexture.Index : -1f );
		material.Set( "g_flGlobalShapeSrgb", tag.Texture( "global_shape_texture" ) is { Bitmap: not null } s && ShaderBitmaps.IsSrgb( s.Bitmap, s.BitmapIndex ) ? 1f : 0f );
		// The reflection as MCC's water PS scales it (reflection_coefficient, Valhalla's 600, against a sunspot_cut of 0.01); the sun's glint comes from the cube's alpha, not an analytic term.
		material.Set( "g_vWaterFresnel", new Vector4( Math.Clamp( tag.Scalar( "fresnel_coefficient", 0.1f ), 0, 1 ), tag.Scalar( "reflection_coefficient", 1 ), bank, 0 ) );
		material.Set( "g_vWaterReflection", new Vector2( tag.Scalar( "sunspot_cut", 1 ), tag.Scalar( "shadow_intensity_mark", 0 ) ) );
		return material;
	}

	// How far (world units) MCC's water vertex shader can move a water vertex of this shader at most, along any axis: H3WaterDisplacement's largest texel (displacement_range less half of it) per layer, for a displacement scale and global shape of 1, times the largest choppiness. 0 unless waveshape is default.
	// Whether a shader blends (its blend_mode isn't opaque): MCC draws it in the transparent pass whatever the mesh part's type says (Sandtrap's additive god ray and steam sheets are opaque type 2 parts).
	public static bool IsBlended( TagEntry shader ) => shader is not null && ShaderTag.Get( shader ) is { } tag && BlendMode( tag.Option( "blend_mode" ) ) >= 0;

	public static float WaterDisplacementReach( TagEntry shader )
	{
		if ( ShaderTag.Get( shader ) is not { } tag || tag.Option( "waveshape" ) != "default" || tag.Texture( "wave_displacement_array" ) is not { } displacement )
			return 0;
		var range = Math.Max( Math.Abs( tag.Scalar( "displacement_range_x", 0 ) ), Math.Max( Math.Abs( tag.Scalar( "displacement_range_y", 0 ) ), Math.Abs( tag.Scalar( "displacement_range_z", 0 ) ) ) );
		var heights = Math.Abs( tag.Scalar( "wave_height", 0.5f ) ) + Math.Abs( tag.Scalar( "wave_height_aux", 0.5f ) );
		var chop = Math.Max( 1, Math.Max( Math.Abs( tag.Scalar( "choppiness_forward", 1 ) ), Math.Max( Math.Abs( tag.Scalar( "choppiness_backward", 1 ) ), Math.Abs( tag.Scalar( "choppiness_side", 1 ) ) ) ) );
		var area = MathF.Sqrt( Math.Max( Math.Abs( displacement.Transform.X * displacement.Transform.Y ), 1e-6f ) );
		return range * 0.5f * heights * chop / area;
	}

	// The period of the overlay driving a time warp (its animation runs once per period), or a fallback.
	static float WarpPeriod( ShaderTag tag, string real, float fallback ) =>
		tag.Overlays.FirstOrDefault( o => o.Target == real && o.Period > 0 )?.Period ?? fallback;

	// Every slot's neutral texture (as the shader's defaults, set explicitly): grey albedo maps (MCC's 50% grey in linear, x the detail multiplier = 1), flat bumps, white masks, black self-illumination.
	static readonly Lazy<Texture> NeutralGrey = new( () => Texture.Create( 1, 1 ).WithData( new byte[] { 56, 56, 56, 255 } ).WithStaticUsage().Finish() );

	static readonly (string Slot, int Kind)[] StandardSlots =
	[
		("BaseMap", 0), ("DetailMap", 0), ("DetailMap2", 0), ("DetailMap3", 0), ("DetailMapOverlay", 0), ("ChangeColorMap", 3), ("BumpMap", 1), ("BumpDetailMap", 1),
		("BumpDetailMaskMap", 2), ("AlphaTestMap", 2), ("SpecularMaskTexture", 2), ("MaterialTexture", 2), ("SelfIllumMap", 3), ("SelfIllumDetailMap", 0), ("AlphaMaskMap", 2),
		("HeightMap", 4), ("OverlayMap", 3), ("OverlayDetailMap", 0), ("EnvMap", 3),
	];

	static readonly (string Slot, int Kind)[] TerrainSlots =
	[
		("BlendMap", 5), ("BaseMap0", 0), ("DetailMap0", 0), ("BumpMap0", 1), ("DetailBump0", 1), ("BaseMap1", 0), ("DetailMap1", 0), ("BumpMap1", 1), ("DetailBump1", 1),
		("BaseMap2", 0), ("DetailMap2", 0), ("BumpMap2", 1), ("DetailBump2", 1), ("BaseMap3", 0), ("DetailMap3", 0), ("BumpMap3", 1), ("DetailBump3", 1),
		("SelfIllumMap", 3), ("SelfIllumDetailMap", 0), ("EnvMap", 3),
	];

	static readonly Lazy<Texture> RedOnly = new( () => Texture.Create( 1, 1 ).WithData( new byte[] { 255, 0, 0, 0 } ).WithStaticUsage().Finish() );

	static void SetDefaults( Material material, (string Slot, int Kind)[] slots )
	{
		foreach ( var (slot, kind) in slots )
		{
			material.Set( $"g_t{slot}", kind switch
			{
				0 => NeutralGrey.Value,
				1 => MaterialTextures.FlatNormal,
				2 => MaterialTextures.White,
				3 => MaterialTextures.Transparent,
				4 => MaterialTextures.Grey,
				_ => RedOnly.Value,
			} );
			material.Set( $"g_fl{slot}Srgb", 0f );
		}
	}

	// The halo3_shader slot a texture parameter goes to, or null for one it doesn't sample.
	static string TextureSlot( string name ) => name switch
	{
		"base_map" or "tex0_sampler" => "BaseMap",
		"detail_map" => "DetailMap",
		"detail_map2" => "DetailMap2",
		"detail_map3" => "DetailMap3",
		"detail_map_overlay" => "DetailMapOverlay",
		"change_color_map" or "color_mask_map" or "blend_map" => "ChangeColorMap",
		"bump_map" => "BumpMap",
		"bump_detail_map" => "BumpDetailMap",
		"bump_detail_mask_map" => "BumpDetailMaskMap",
		"alpha_test_map" => "AlphaTestMap",
		"specular_mask_texture" => "SpecularMaskTexture",
		"material_texture" => "MaterialTexture",
		"self_illum_map" or "meter_map" or "noise_map_a" => "SelfIllumMap",
		"self_illum_detail_map" or "noise_map_b" => "SelfIllumDetailMap",
		"alpha_mask_map" => "AlphaMaskMap",
		"height_map" => "HeightMap",
		"overlay_map" => "OverlayMap",
		"overlay_detail_map" => "OverlayDetailMap",
		"environment_map" or "custom_cube" or "flat_environment_map" => "EnvMap",
		_ => null,
	};

	// halo3_terrain's slot for a terrain texture parameter (<name>_m_<material>).
	static string TerrainSlot( string name )
	{
		switch ( name )
		{
			case "blend_map": return "BlendMap";
			case "self_illum_map_m_1": return "SelfIllumMap";
			case "self_illum_detail_map_m_1": return "SelfIllumDetailMap";
			case "environment_map": return "EnvMap";
		}
		if ( name.Length < 5 || name[^4..^1] != "_m_" || name[^1] is < '0' or > '3' )
			return null;
		var m = name[^1];
		return name[..^4] switch
		{
			"base_map" => $"BaseMap{m}",
			"detail_map" => $"DetailMap{m}",
			"bump_map" => $"BumpMap{m}",
			"detail_bump" => $"DetailBump{m}",
			_ => null,
		};
	}

	// Reals by Halo name → halo3_shader parameter, with how many components it takes (1 scalar, 3 colour, 4 vector).
	static readonly (string Real, string Parameter, int Components)[] Reals =
	[
		("albedo_color", "g_vAlbedoColor", 4),
		("albedo_color2", "g_vAlbedoColor2", 4),
		("albedo_second_color", "g_vAlbedoColor2", 4),
		("albedo_color3", "g_vAlbedoColor3", 4),
		("neutral_gray", "g_vNeutralGray", 4),
		("bump_detail_coefficient", "g_flBumpDetailCoefficient", 1),
		("height_scale", "g_flHeightScale", 1),
		("diffuse_coefficient", "g_flDiffuseCoefficient", 1),
		("specular_coefficient", "g_flSpecularCoefficient", 1),
		("roughness", "g_flRoughness", 1),
		("albedo_blend", "g_flAlbedoBlend", 1),
		("fresnel_color", "g_vFresnelColor", 3),
		("specular_tint", "g_vSpecularTint", 3),
		("analytical_specular_contribution", "g_flAnalyticalSpecular", 1),
		("area_specular_contribution", "g_flAreaSpecular", 1),
		("analytical_anti_shadow_control", "g_flAntiShadowControl", 1),
		("normal_specular_power", "g_flNormalSpecularPower", 1),
		("normal_specular_tint", "g_vNormalSpecularTint", 3),
		("glancing_specular_power", "g_flGlancingSpecularPower", 1),
		("glancing_specular_tint", "g_vGlancingSpecularTint", 3),
		("fresnel_curve_steepness", "g_flFresnelCurveSteepness", 1),
		("fresnel_coefficient", "g_flFresnelCoefficient", 1),
		("fresnel_curve_bias", "g_flFresnelCurveBias", 1),
		("albedo_specular_tint_blend", "g_flAlbedoSpecularTintBlend", 1),
		("rim_fresnel_coefficient", "g_flRimFresnelCoefficient", 1),
		("rim_fresnel_color", "g_vRimFresnelColor", 3),
		("rim_fresnel_power", "g_flRimFresnelPower", 1),
		("rim_coefficient", "g_flRimFresnelCoefficient", 1),
		("rim_tint", "g_vRimFresnelColor", 3),
		("rim_power", "g_flRimFresnelPower", 1),
		("env_tint_color", "g_vEnvTint", 3),
		("environment_map_specular_contribution", "g_flEnvContribution", 1),
		("env_roughness_scale", "g_flEnvRoughnessScale", 1),
		("self_illum_color", "g_vSelfIllumColor", 3),
		("self_illum_intensity", "g_flSelfIllumIntensity", 1),
		("channel_a", "g_vChannelA", 4),
		("channel_b", "g_vChannelB", 4),
		("channel_c", "g_vChannelC", 4),
		("meter_color_on", "g_vMeterColorOn", 3),
		("meter_color_off", "g_vMeterColorOff", 3),
		("meter_value", "g_flMeterValue", 1),
		("color_medium", "g_vPlasmaColorMedium", 4),
		("color_wide", "g_vPlasmaColorWide", 4),
		("color_sharp", "g_vPlasmaColorSharp", 4),
		("layer_depth", "g_flLayerDepth", 1),
		("layer_contrast", "g_flLayerContrast", 1),
		("texcoord_aspect_ratio", "g_flTexcoordAspectRatio", 1),
		("depth_darken", "g_flDepthDarken", 1),
		("layers_of_4", "g_flLayersOf4", 1),
		("overlay_tint", "g_vOverlayTint", 3),
		("overlay_intensity", "g_flOverlayIntensity", 1),
		("edge_fade_center_tint", "g_vEdgeFadeCenterTint", 3),
		("edge_fade_edge_tint", "g_vEdgeFadeEdgeTint", 3),
		("edge_fade_power", "g_flEdgeFadePower", 1),
	];

	static void SetReal( Material material, string parameter, int components, System.Numerics.Vector4 v )
	{
		switch ( components )
		{
			case 1: material.Set( parameter, v.X ); break;
			case 3: material.Set( parameter, new Vector3( v.X, v.Y, v.Z ) ); break;
			default: material.Set( parameter, new Vector4( v.X, v.Y, v.Z, v.W ) ); break;
		}
	}

	// A texture into a slot with its transform (and time-driven scroll) and sRGB flag; a missing bitmap leaves the slot's neutral default.
	static void SetTexture( Material material, string slot, ShaderTexture texture, ShaderTag tag )
	{
		SetTransform( material, $"g_v{slot}", texture, tag );
		if ( texture.Bitmap is null )
			return;

		var loaded = Texture.Load( Halo3Mount.TexturePath( texture.Bitmap.Path, Math.Max( 0, texture.BitmapIndex ) ) );
		if ( loaded is null || loaded.IsError )
			return;
		material.Set( $"g_t{slot}", loaded );
		material.Set( $"g_fl{slot}Srgb", ShaderBitmaps.IsSrgb( texture.Bitmap, texture.BitmapIndex ) ? 1f : 0f );
		// A bump map in an unsigned format (the Wraith's DXT5 one) is read as it comes, as MCC's albedo pass samples it (halo3_shader's H3BumpNormal).
		if ( slot.Contains( "Bump", StringComparison.Ordinal ) && !slot.EndsWith( "MaskMap", StringComparison.Ordinal ) )
			material.Set( $"g_fl{slot}Unsigned", ShaderBitmaps.IsSignedBump( texture.Bitmap, texture.BitmapIndex ) ? 0f : 1f );
		if ( slot == "EnvMap" )
			material.Set( "g_flHasEnvMap", 1f );
	}

	// The self-illumination's time-driven pulses, which halo3_shader evaluates with g_flTime: a value overlay on self_illum_intensity, and a colour overlay on self_illum_color whose gradient goes as up to four stops (ShaderOverlay.TimeWave).
	static void SetSelfIllumWaves( Material material, ShaderTag tag )
	{
		foreach ( var overlay in tag?.Overlays ?? [] )
		{
			if ( overlay.TimeWave is not { } wave )
				continue;
			if ( overlay.Type == 0 && overlay.Target == "self_illum_intensity" )
			{
				// A clamping function holds its curve to 0-1, so the value stays within its output range (Zanzibar's gate blink curve runs -2 to 10).
				var f = new Halo3Function( overlay.Function );
				var (low, high) = f.Clamps ? (MathF.Min( f.Minimum, f.Maximum ), MathF.Max( f.Minimum, f.Maximum )) : (float.MinValue, float.MaxValue);
				material.Set( "g_vSelfIllumPulse", new Vector4( wave.Offset, wave.Scale, low, high ) );
				material.Set( "g_vSelfIllumPulseWave", new Vector4( wave.Period, wave.Frequency, wave.Phase, wave.Kind ) );
			}
			else if ( overlay.Type == 1 && overlay.Target == "self_illum_color" )
			{
				var stops = new Halo3Function( overlay.Function ).GradientStops;
				if ( stops.Length == 0 )
					continue;
				for ( var i = 0; i < 4; i++ )
				{
					var c = Halo3Function.ToVector( stops[Math.Min( i, stops.Length - 1 )] );
					material.Set( $"g_vSelfIllumColorStop{i}", new Vector4( c.X, c.Y, c.Z, 1 ) );
				}
				material.Set( "g_vSelfIllumColorCurve", new Vector4( wave.Offset, wave.Scale, stops.Length, 0 ) );
				material.Set( "g_vSelfIllumColorWave", new Vector4( wave.Period, wave.Frequency, wave.Phase, wave.Kind ) );
			}
		}
	}

	// <prefix>Xform (scale u, scale v, offset u, offset v) and <prefix>Scroll from the time-driven translation overlays on it (types 5 x, 6 y): the offset runs from the function's start to its end each period (an identity function or a periodic slide), which halo3_shader turns into a speed (xy) over a repeat range (zw).
	static void SetTransform( Material material, string prefix, ShaderTexture texture, ShaderTag tag )
	{
		var t = texture.Transform;
		var scroll = Vector4.Zero;
		foreach ( var overlay in tag?.Overlays ?? [] )
		{
			if ( overlay.Target != texture.Name || overlay.Type is not (5 or 6) || overlay.TimeWave?.Ramp is not { } line || line.To == line.From )
				continue;
			var range = line.To - line.From;
			if ( overlay.Type == 5 )
			{
				t.Z = line.From;
				scroll.x = range / line.Period;
				scroll.z = range;
			}
			else
			{
				t.W = line.From;
				scroll.y = range / line.Period;
				scroll.w = range;
			}
		}
		material.Set( $"{prefix}Xform", new Vector4( t.X, t.Y, t.Z, t.W ) );
		material.Set( $"{prefix}Scroll", scroll );

		// The other time-driven transform overlays (waves that aren't ramps, curves, scales) run on the CPU (Animate).
		var animated = new List<(int, float, Halo3Function)>();
		foreach ( var overlay in tag?.Overlays ?? [] )
		{
			if ( overlay.Target != texture.Name || overlay.Type is < 2 or > 6 || !string.IsNullOrEmpty( overlay.Input ) || overlay.Period <= 0 )
				continue;
			if ( overlay.Type is 5 or 6 && overlay.TimeWave?.Ramp is { } ramp && ramp.To != ramp.From )
				continue;
			var function = new Halo3Function( overlay.Function );
			if ( !function.IsEmpty && !function.IsColor )
				animated.Add( (overlay.Type, overlay.Period, function) );
		}
		if ( animated.Count > 0 )
			lock ( Created )
				Animated.Add( new AnimatedTransform( material, tag.Tag.Cache, $"{prefix}Xform", new Vector4( t.X, t.Y, t.Z, t.W ), animated ) );
	}

	static int AlbedoMode( string option ) => option switch
	{
		"detail_blend" => 1,
		"constant_color" => 2,
		"two_change_color" or "two_change_color_anim_overlay" or "two_change_color_chameleon" or "two_change_color_tex_overlay" => 3,
		"four_change_color" => 4,
		"three_detail_blend" => 5,
		"two_detail_overlay" => 6,
		"two_detail" => 7,
		"color_mask" or "color_mask_hard_light" => 8,
		"two_detail_black_point" => 9,
		"two_color" => 10,
		"chameleon" or "chameleon_masked" or "chameleon_albedo_masked" => 11,
		_ => 0,
	};

	static int BumpMode( string option ) => option switch
	{
		"standard" or "standard_mask" => 1,
		"detail" or "detail_unorm" => 2,
		"detail_masked" or "detail_plus_detail_masked" => 3,
		_ => 0,
	};

	static int SpecularMaskMode( string option ) => option switch
	{
		"specular_mask_from_diffuse" => 1,
		"specular_mask_from_texture" => 2,
		"specular_mask_from_color_texture" => 3,
		_ => 0,
	};

	// halo3_shader's g_flMaterialModel: 0 diffuse only, 1 cook_torrance family, 2 two lobe phong, 3 single lobe phong, 4 foliage, 5 none (unlit), 6 glass, 7 organism.
	static int MaterialModel( ShaderTag tag )
	{
		if ( tag.Kind == "foliage" )
			return 4;
		// Halograms have no material model: MCC's halogram templates draw their self-illumination, overlay and edge fade only, no albedo lit by the level (Sandtrap's god ray sheets).
		if ( tag.Kind == "halogram" )
			return 5;
		var option = tag.Option( "material_model" );
		return option switch
		{
			null or "diffuse_only" => 0,
			"two_lobe_phong" or "two_lobe_phong_tint_map" => 2,
			"single_lobe_phong" => 3,
			"foliage" => 4,
			"none" => 5,
			"glass" => 6,
			"organism" => 7,
			_ when option.StartsWith( "cook_torrance" ) || option == "car_paint" => 1,
			_ => 0,
		};
	}

	static int EnvMode( string option ) => option switch
	{
		null or "none" => 0,
		"per_pixel" or "custom_map" => 1,
		"dynamic" => 2,
		_ => 3,
	};

	static int SelfIllumMode( string option ) => option switch
	{
		null or "off" or "none" => 0,
		"3_channel_self_illum" => 2,
		"plasma" or "plasma_wide_and_sharp_five_change_color" => 3,
		"from_diffuse" => 4,
		"illum_detail" or "illum_detail_world_space_four_cc" => 5,
		"meter" => 6,
		"self_illum_times_diffuse" => 7,
		"simple_with_alpha_mask" => 8,
		"illum_change_color" or "simple_four_change_color" => 9,
		"multilayer_additive" => 10,
		_ => 1,
	};

	static int OverlayMode( string option ) => option switch
	{
		"additive" => 1,
		"additive_detail" => 2,
		"multiply" => 3,
		"multiply_and_additive_detail" => 4,
		_ => 0,
	};

	static readonly Dictionary<CacheFile, float> Exposures = new();

	// The map's fixed exposure in stops (LightmapBrightness.Exposure: its camera fx's auto exposure target for its average lightmap light).
	public static float CameraExposure( CacheFile cache )
	{
		if ( cache is null )
			return LightmapBrightness.DefaultExposureStops;
		lock ( Exposures )
		{
			if ( !Exposures.TryGetValue( cache, out var stops ) )
				Exposures[cache] = stops = LightmapBrightness.Exposure( cache ).Stops;
			return stops;
		}
	}

	// Gives a live renderer its scene's map's exposure and fog (halo3_shader's Halo3SceneLook): a model shared between maps has materials made from the first map with its tags, whose exposure and fog may be another map's (Last Resort's vehicles came out at 005_intro's 3.3 stops).
	// positionScale > 0: the renderer is drawn that many times smaller than it is (an object in the 3D skybox's scene).
	public static void SetSceneLook( RenderAttributes attributes, CacheFile cache, float positionScale = 0 )
	{
		if ( attributes is null || cache is null )
			return;
		attributes.Set( "Halo3SceneLook", 1f );
		attributes.Set( "Halo3SceneExposureStops", CameraExposure( cache ) );
		if ( Atmosphere( cache ) is { } fog )
			fog.Apply( attributes, positionScale );
		else
			attributes.Set( "Halo3FogScale", new Vector2( 1, 0 ) );
	}

	static readonly Dictionary<CacheFile, Halo3Atmosphere> Atmospheres = new();

	// The map's atmosphere fog (its most used skya setting), or null for none.
	public static Halo3Atmosphere Atmosphere( CacheFile cache )
	{
		if ( cache is null )
			return null;
		lock ( Atmospheres )
		{
			if ( !Atmospheres.TryGetValue( cache, out var fog ) )
				Atmospheres[cache] = fog = Halo3Atmosphere.ForScenario( cache );
			return fog;
		}
	}
}

// Halo 3's atmosphere fog for halo3_shader: one skya atmosphere setting's Rayleigh/Mie scattering, applied per pixel.
// MCC picks the setting per cluster; this takes the one most clusters use for the whole map, setting 0 when no cluster names one (Valhalla's live AtmosphereVS constants are its setting 0).
sealed class Halo3Atmosphere
{
	public string Name { get; init; }
	public Vector4 Sun { get; init; }
	public Vector4 SunColor { get; init; }
	public Vector4 Mie { get; init; }
	public Vector4 Rayleigh { get; init; }
	public Vector4 RayleighTheta { get; init; }
	public Vector4 MieTheta { get; init; }

	// Flags (bit 0 enable, 1 override the real sun, 2 patchy fog) and the patchy fog's sheet density, the heights its intensity is full and half at (world units) and its wind.
	public uint Flags { get; init; }
	public float SeaLevel { get; init; }
	public float SheetDensity { get; init; }
	public float FullIntensityHeight { get; init; }
	public float HalfIntensityHeight { get; init; }
	public Vector3 Wind { get; init; }
	public bool PatchyFog => (Flags & 1) != 0 && (Flags & 4) != 0 && SheetDensity > 0.001f;

	// A setting of the scenario's sky atmosphere (scnr +0x680), or null.
	public static Halo3Atmosphere ForSetting( CacheFile cache, int index )
	{
		var skya = cache?.Scenario?.Data.Reference( 0x680 );
		return skya is null || skya.Data.IsNull || index < 0 ? null : Setting( skya, index );
	}

	// positionScale > 0: positions are drawn that many times smaller than they are (a 3D skybox's).
	public void Apply( Material material, float positionScale = 0 )
	{
		material.Set( "g_vFogSun", Sun );
		material.Set( "g_vFogSunColor", SunColor );
		material.Set( "g_vFogMie", Mie );
		material.Set( "g_vFogRayleigh", Rayleigh );
		material.Set( "g_vFogRayleighTheta", RayleighTheta );
		material.Set( "g_vFogMieTheta", MieTheta );
		material.Set( "g_vFogScale", new Vector2( (positionScale > 0 ? positionScale : 1) / Halo3Mount.WorldScale, 1 ) );
	}

	// The same as scene attributes (Halo3Materials.SetSceneLook).
	public void Apply( RenderAttributes attributes, float positionScale = 0 )
	{
		attributes.Set( "Halo3FogSun", Sun );
		attributes.Set( "Halo3FogSunColor", SunColor );
		attributes.Set( "Halo3FogMie", Mie );
		attributes.Set( "Halo3FogRayleigh", Rayleigh );
		attributes.Set( "Halo3FogRayleighTheta", RayleighTheta );
		attributes.Set( "Halo3FogMieTheta", MieTheta );
		attributes.Set( "Halo3FogScale", new Vector2( (positionScale > 0 ? positionScale : 1) / Halo3Mount.WorldScale, 1 ) );
	}

	// scnr +0x680 skya (settings +0x34, 0xA4 each); scnr +0x4F0 atmosphere palette (sid name, i16 setting); scnr +0x508 cluster data (+0x50: a 4-byte element per cluster, i16 palette index first).
	public static Halo3Atmosphere ForScenario( CacheFile cache )
	{
		var scenario = cache.Scenario?.Data ?? default;
		if ( scenario.IsNull )
			return null;
		var skya = scenario.Reference( 0x680 );
		var palette = scenario.Block( 0x4F0, 8 );
		if ( skya is null || skya.Data.IsNull )
			return null;
		if ( palette.Count == 0 )
			return Setting( skya, 0 );

		var uses = new Dictionary<int, int>();
		foreach ( var bsp in scenario.Block( 0x508, 0x68 ) )
		{
			foreach ( var cluster in bsp.Block( 0x50, 4 ) )
			{
				var index = cluster.I16( 0 );
				if ( index >= 0 && index < palette.Count )
					uses[index] = uses.GetValueOrDefault( index ) + 1;
			}
		}
		if ( uses.Count == 0 )
			return Setting( skya, 0 );

		var chosen = uses.OrderByDescending( u => u.Value ).ThenBy( u => u.Key ).First().Key;
		var setting = skya.Data.Element( 0x34, 0xA4, palette[chosen].I16( 4 ) );
		return setting.IsNull ? null : FromSetting( setting );
	}

	// The skya setting MCC fogs sky shaders with (render method flag 2): the first one flagged 4 (Valhalla's haze_skydome), or null.
	public static Halo3Atmosphere ForSky( CacheFile cache )
	{
		var scenario = cache?.Scenario?.Data ?? default;
		if ( scenario.IsNull || scenario.Reference( 0x680 ) is not { } skya || skya.Data.IsNull )
			return null;
		var settings = skya.Data.Block( 0x34, 0xA4 );
		for ( var i = 0; i < settings.Count; i++ )
		{
			if ( (settings[i].U16( 0 ) & 4) != 0 )
				return FromSetting( settings[i] );
		}
		return null;
	}

	static Halo3Atmosphere Setting( global::Blam.Halo3.TagEntry skya, int index )
	{
		var setting = skya.Data.Element( 0x34, 0xA4, index );
		return setting.IsNull ? null : FromSetting( setting );
	}

	// One atmosphere setting (skya +0x34 element), as Halo 3 builds its constants: the sun's colour with its luminance replaced by the intensity (colour * intensity / Y709( colour ); Valhalla's 1.659), its direction from the pitch as an angle from straight up.
	public static Halo3Atmosphere FromSetting( global::Blam.Halo3.TagView s )
	{
		var pitch = s.F32( 0x08 ) * MathF.PI / 180;
		var heading = s.F32( 0x0C ) * MathF.PI / 180;
		var sun = new Vector3( MathF.Cos( heading ) * MathF.Sin( pitch ), MathF.Sin( heading ) * MathF.Sin( pitch ), MathF.Cos( pitch ) ).Normal;
		var tint = s.Vec3( 0x10 );
		var luminance = 0.212656f * tint.X + 0.715158f * tint.Y + 0.0721856f * tint.Z;
		var color = tint * (luminance > 1e-6f ? s.F32( 0x1C ) / luminance : 0);
		var g = s.F32( 0x34 );
		Vector4 V( System.Numerics.Vector3 v, float w ) => new( v.X, v.Y, v.Z, w );
		return new Halo3Atmosphere
		{
			Name = s.StringId( 0x04 ),
			Sun = new Vector4( sun.x, sun.y, sun.z, s.F32( 0x3C ) ),
			SunColor = V( color, s.F32( 0x40 ) ),
			Mie = V( s.Vec3( 0x50 ), s.F32( 0x34 ) ),
			Rayleigh = V( s.Vec3( 0x44 ), s.F32( 0x20 ) ),
			RayleighTheta = V( s.Vec3( 0x5C ), s.F32( 0x24 ) ),
			MieTheta = V( s.Vec3( 0x68 ) * (1 - g * g), s.F32( 0x28 ) ),
			Flags = s.U32( 0x00 ),
			SeaLevel = s.F32( 0x20 ),
			SheetDensity = s.F32( 0x74 ),
			FullIntensityHeight = s.F32( 0x78 ),
			HalfIntensityHeight = s.F32( 0x7C ),
			Wind = new Vector3( s.Vec3( 0x80 ).X, s.Vec3( 0x80 ).Y, s.Vec3( 0x80 ).Z ),
		};
	}
}
