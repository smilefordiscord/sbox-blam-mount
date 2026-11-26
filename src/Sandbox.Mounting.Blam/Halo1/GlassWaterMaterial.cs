using Blam.Halo1;
using Blam.Halo1.Tags;

namespace Sandbox.Mounting.Blam.Halo1;

/// <summary>
/// Builds s&amp;box materials for Halo CE shader_transparent_glass and shader_transparent_water
/// tags, which share one shader.
/// </summary>
static class GlassWaterMaterial
{
	public const string ShaderPath = "shaders/halo1_glass_water.shader";

	public static Material CreateWater( HaloCeMount host, string name, CacheFile cache, TagEntry shaderTag )
	{
		var water = new ShaderWaterTag( cache, shaderTag );
		var material = Material.Create( name, ShaderPath );
		if ( material is null )
			return null;

		SetTexture( host, material, "g_tTintMap", water.BaseMap, 0, MaterialTextures.White );
		material.Set( "g_vBackgroundTint", Vector3.One );
		material.Set( "g_flTintMapScale", 1f );
		// The base map covers the water once (an island-shaped mask on b30), so it's clamped.
		material.Set( "g_flTintMapClamp", 1f );
		material.Set( "g_flTintMapColorTintsBackground", water.Flags.HasFlag( WaterShaderFlags.BaseMapColorModulatesBackground ) ? 1f : 0f );
		material.Set( "g_flTintMapAlphaScalesReflection", water.Flags.HasFlag( WaterShaderFlags.BaseMapAlphaModulatesReflection ) ? 1f : 0f );

		SetReflection( host, material, water.ReflectionMap, (Vector3)water.PerpendicularTint, (Vector3)water.ParallelTint, water.PerpendicularBrightness, water.ParallelBrightness );
		material.Set( "g_flWaterReflection", 1f );
		material.Set( "g_flWaterAtmosphericFog", water.Flags.HasFlag( WaterShaderFlags.AtmosphericFog ) ? 1f : 0f );
		material.Set( "g_flWaterDrawBeforeFog", water.Flags.HasFlag( WaterShaderFlags.DrawBeforeFog ) ? 1f : 0f );

		// Each ripple layer scrolls one of the ripple maps. Only the first ripple map is bound,
		// which is all the stock water shaders use. The layers are averaged, the result scrolls
		// as a whole, and it fades toward flat over the ripple mip levels.
		SetBumpMap( host, material, water.RippleMaps );
		material.Set( "g_flBumpScale", water.RippleScale );
		material.Set( "g_vRippleScroll", new Vector2( MathF.Cos( water.RippleAnimationAngle ), MathF.Sin( water.RippleAnimationAngle ) ) * water.RippleAnimationVelocity );
		material.Set( "g_vRippleFade", new Vector3( water.RippleMipmapLevels, Math.Clamp( water.RippleMipmapFadeFactor, 0f, 1f ), water.RippleMipmapDetailBias ) );

		var layers = new[] { "g_vBumpLayer0", "g_vBumpLayer1", "g_vBumpLayer2", "g_vBumpLayer3" };
		var offsets = new Vector2[4];
		for ( var i = 0; i < layers.Length; i++ )
		{
			if ( i >= water.Ripples.Count )
			{
				material.Set( layers[i], new Vector4( 0, 0, 1, 0 ) );
				continue;
			}

			var ripple = water.Ripples[i];
			var velocity = ripple.AnimationVelocity;
			material.Set( layers[i], new Vector4( MathF.Cos( ripple.AnimationAngle ) * velocity, MathF.Sin( ripple.AnimationAngle ) * velocity, ripple.MapRepeats, ripple.ContributionFactor ) );
			offsets[i] = ripple.MapOffset;
		}

		material.Set( "g_vBumpOffsets01", new Vector4( offsets[0].x, offsets[0].y, offsets[1].x, offsets[1].y ) );
		material.Set( "g_vBumpOffsets23", new Vector4( offsets[2].x, offsets[2].y, offsets[3].x, offsets[3].y ) );

		material.Set( "g_tDiffuseMap", MaterialTextures.Transparent );
		material.Set( "g_tLightmap", MaterialTextures.White );
		material.Set( "g_flHasDiffuse", 0f );
		material.Set( "g_flHasLightmap", 0f );

		return material;
	}

	/// <param name="pass">0: over the frame buffer copy; 1, 2: Halo's tint and reflection passes,
	/// drawn as separate objects (see StructureBspModel and ObjectGlass).</param>
	public static Material CreateGlass( HaloCeMount host, string name, CacheFile cache, TagEntry shaderTag, Texture lightmap, int pass = 0 )
	{
		var glass = new ShaderGlassTag( cache, shaderTag );
		var material = Material.Create( name, ShaderPath );
		if ( material is null )
			return null;

		material.SetFeature( "F_GLASS_PASS", pass );
		material.SetFeature( "F_TWO_SIDED", glass.Flags.HasFlag( GlassShaderFlags.TwoSided ) ? 1 : 0 );
		// Dynamic mirrors get their reflection from MirrorReflections.
		material.Set( "g_flDynamicMirror", glass.ReflectionType == GlassReflectionType.DynamicMirror ? 1f : 0f );

		SetTexture( host, material, "g_tTintMap", glass.TintMap, 0, MaterialTextures.White );
		material.Set( "g_vBackgroundTint", (Vector3)glass.BackgroundTint );
		material.Set( "g_flTintMapScale", glass.TintMapScale );
		material.Set( "g_flTintMapClamp", 0f );
		material.Set( "g_flTintMapColorTintsBackground", 1f );
		material.Set( "g_flTintMapAlphaScalesReflection", 0f );

		SetReflection( host, material, glass.ReflectionMap, (Vector3)glass.PerpendicularTint, (Vector3)glass.ParallelTint, glass.PerpendicularBrightness, glass.ParallelBrightness );
		material.Set( "g_flWaterReflection", 0f );

		// Only bumped cube map glass bends its reflection.
		var bumped = glass.ReflectionType == GlassReflectionType.BumpedCubeMap;
		SetBumpMap( host, material, bumped ? glass.BumpMap : null );
		material.Set( "g_flBumpScale", glass.BumpMapScale );
		material.Set( "g_vRippleScroll", Vector2.Zero );
		material.Set( "g_vRippleFade", new Vector3( 1, 0, 0 ) );
		material.Set( "g_vBumpLayer0", new Vector4( 0, 0, 1, bumped ? 1 : 0 ) );
		material.Set( "g_vBumpLayer1", new Vector4( 0, 0, 1, 0 ) );
		material.Set( "g_vBumpLayer2", new Vector4( 0, 0, 1, 0 ) );
		material.Set( "g_vBumpLayer3", new Vector4( 0, 0, 1, 0 ) );
		material.Set( "g_vBumpOffsets01", Vector4.Zero );
		material.Set( "g_vBumpOffsets23", Vector4.Zero );

		var hasDiffuse = SetTexture( host, material, "g_tDiffuseMap", glass.DiffuseMap, 0, MaterialTextures.Transparent );
		material.Set( "g_flDiffuseMapScale", glass.DiffuseMapScale );
		material.Set( "g_flHasDiffuse", hasDiffuse ? 1f : 0f );
		material.Set( "g_tLightmap", lightmap ?? MaterialTextures.White );
		material.Set( "g_flHasLightmap", lightmap is null ? 0f : 1f );

		return material;
	}

	static void SetReflection( HaloCeMount host, Material material, TagEntry cubeMap, Vector3 perpendicularTint, Vector3 parallelTint, float perpendicularBrightness, float parallelBrightness )
	{
		var reflection = MaterialTextures.Load( host, material, cubeMap, 0 );
		material.Set( "g_tReflectionMap", reflection ?? MaterialTextures.Transparent );
		material.Set( "g_flHasReflection", reflection is null ? 0f : 1f );
		material.Set( "g_vPerpendicularTint", perpendicularTint );
		material.Set( "g_vParallelTint", parallelTint );
		material.Set( "g_vReflectionBrightness", new Vector2( perpendicularBrightness, parallelBrightness ) );
	}

	/// <summary>
	/// Binds the bump (ripple) map and tells the shader its size, which it picks mip levels with.
	/// </summary>
	static void SetBumpMap( HaloCeMount host, Material material, TagEntry bitmap )
	{
		var texture = MaterialTextures.Load( host, material, bitmap, 0 );
		material.Set( "g_tBumpMap", texture ?? MaterialTextures.FlatNormal );
		material.Set( "g_flBumpMapSize", (float)(texture?.Width ?? 1) );
	}

	static bool SetTexture( HaloCeMount host, Material material, string input, TagEntry bitmap, int index, Texture fallback )
	{
		var texture = MaterialTextures.Load( host, material, bitmap, index );
		material.Set( input, texture ?? fallback );
		return texture is not null;
	}
}
