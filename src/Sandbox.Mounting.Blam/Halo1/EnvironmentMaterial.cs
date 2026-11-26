using Blam.Halo1;
using Blam.Halo1.Tags;

namespace Sandbox.Mounting.Blam.Halo1;

/// <summary>
/// Builds s&amp;box materials for Halo CE shader_environment tags.
/// </summary>
static class EnvironmentMaterial
{
	public const string ShaderPath = "shaders/halo1_environment.shader";

	/// <param name="name">Unique material name. A shader can be lit by several lightmap pages, so the page is part of it.</param>
	/// <param name="lightmap">Lightmap page texture, or null for unlit geometry.</param>
	/// <param name="onObject">On an object's model: lit like the model shader, from the object's row in <see cref="ObjectLightingTable"/>.</param>
	public static Material Create( HaloCeMount host, string name, CacheFile cache, TagEntry shaderTag, Texture lightmap, bool onObject = false )
	{
		var shader = new ShaderEnvironmentTag( cache, shaderTag );
		var material = Material.Create( name, ShaderPath );
		if ( material is null )
			return null;

		// A detail map that isn't there must leave the color alone: grey for the double/biased
		// functions, which treat 0.5 as neutral, and white for multiply.
		var detailDefault = shader.DetailMapFunction == DetailMapFunction.Multiply ? MaterialTextures.White : MaterialTextures.Grey;
		var microDefault = shader.MicroDetailMapFunction == DetailMapFunction.Multiply ? MaterialTextures.White : MaterialTextures.Grey;

		SetTexture( host, material, "g_tBaseMap", shader.BaseMap, MaterialTextures.White );
		SetTexture( host, material, "g_tPrimaryDetailMap", shader.PrimaryDetailMap, detailDefault );
		SetTexture( host, material, "g_tSecondaryDetailMap", shader.SecondaryDetailMap, detailDefault );
		SetTexture( host, material, "g_tMicroDetailMap", shader.MicroDetailMap, microDefault );
		SetTexture( host, material, "g_tBumpMap", shader.BumpMap, MaterialTextures.FlatNormal );
		material.Set( "g_tLightmap", lightmap ?? MaterialTextures.White );

		material.Set( "g_flPrimaryDetailScale", shader.PrimaryDetailMapScale );
		material.Set( "g_flSecondaryDetailScale", shader.SecondaryDetailMapScale );
		material.Set( "g_flMicroDetailScale", shader.MicroDetailMapScale );
		material.Set( "g_flBumpScale", shader.BumpMapScale );
		material.Set( "g_flShaderType", (float)shader.Type );
		material.Set( "g_flDetailFunction", (float)shader.DetailMapFunction );
		material.Set( "g_flMicroDetailFunction", (float)shader.MicroDetailMapFunction );
		material.Set( "g_flAlphaTested", shader.Flags.HasFlag( EnvironmentShaderFlags.AlphaTested ) ? 1f : 0f );
		material.Set( "g_flHasLightmap", lightmap is null ? 0f : 1f );
		material.Set( "g_flObjectLit", onObject ? 1f : 0f );
		if ( onObject )
			ObjectLightingTable.Bind( material );
		material.Set( "g_vMaterialColor", (Vector3)shader.MaterialColor );

		material.Set( "g_vAnimU", Animation( shader.UAnimation.Function, shader.UAnimation.Period, 0, shader.UAnimation.Scale ) );
		material.Set( "g_vAnimV", Animation( shader.VAnimation.Function, shader.VAnimation.Period, 0, shader.VAnimation.Scale ) );

		SetSelfIllumination( host, material, shader );
		SetReflection( host, material, shader );

		return material;
	}

	/// <remarks>
	/// Level geometry is always "active", so each effect is its on color modulated by its
	/// animation; the off colors only show on shaders the game switches off, which BSPs can't.
	/// </remarks>
	static void SetSelfIllumination( HaloCeMount host, Material material, ShaderEnvironmentTag shader )
	{
		var map = shader.HasSelfIllumination ? MaterialTextures.Load( host, material, shader.SelfIlluminationMap, 0 ) : null;
		material.Set( "g_tSelfIllumMap", map ?? MaterialTextures.Transparent );
		material.Set( "g_flHasSelfIllum", map is null ? 0f : 1f );
		material.Set( "g_flSelfIllumMapScale", shader.SelfIlluminationMapScale );
		material.Set( "g_flSelfIllumUnfiltered", shader.SelfIlluminationUnfiltered ? 1f : 0f );

		var effects = new[]
		{
			("Primary", shader.PrimarySelfIllumination),
			("Secondary", shader.SecondarySelfIllumination),
			("Plasma", shader.PlasmaSelfIllumination),
		};

		foreach ( var (name, effect) in effects )
		{
			material.Set( $"g_v{name}OnColor", (Vector3)effect.OnColor );
			material.Set( $"g_v{name}Anim", Animation( effect.Function, effect.Period, effect.Phase, 1 ) );
		}
	}

	static void SetReflection( HaloCeMount host, Material material, ShaderEnvironmentTag shader )
	{
		var cubeMap = shader.HasReflection ? MaterialTextures.Load( host, material, shader.ReflectionCubeMap, 0 ) : null;
		material.Set( "g_tReflectionMap", cubeMap ?? MaterialTextures.Transparent );
		material.Set( "g_flHasReflection", cubeMap is null ? 0f : 1f );
		material.Set( "g_flReflectionBumped", shader.ReflectionType == EnvironmentReflectionType.BumpedCubeMap ? 1f : 0f );
		material.Set( "g_vPerpendicularColor", (Vector3)shader.PerpendicularColor );
		material.Set( "g_vParallelColor", (Vector3)shader.ParallelColor );
		material.Set( "g_flPerpendicularBrightness", shader.PerpendicularBrightness );
		material.Set( "g_flParallelBrightness", shader.ParallelBrightness );
		material.Set( "g_flBumpIsSpecularMask", shader.Flags.HasFlag( EnvironmentShaderFlags.BumpMapIsSpecularMask ) ? 1f : 0f );
		// Halo dims every environment reflection where the lightmap is dark (a lightmap mask pass
		// into the frame buffer's alpha before the reflection), down to the shader's "lightmap
		// brightness scale" where it's black. Not only for "lightmap is specular" shaders: that
		// flag belongs to the dynamic specular highlights, and designers set the scale on many
		// shaders without it.
		material.Set( "g_flLightmapReflectionFloor", Math.Clamp( shader.LightmapBrightnessScale, 0f, 1f ) );
	}

	/// <summary>
	/// Packs a periodic animation for the shader as (function, period, phase, scale).
	/// </summary>
	static Vector4 Animation( AnimationFunction function, float period, float phase, float scale ) =>
		new( (float)function, period, phase, scale );

	/// <summary>
	/// Points a texture input at the first bitmap of a bitmap tag, or at <paramref name="fallback"/>
	/// when the tag doesn't reference one.
	/// </summary>
	static void SetTexture( HaloCeMount host, Material material, string input, TagEntry bitmap, Texture fallback ) =>
		material.Set( input, MaterialTextures.Load( host, material, bitmap, 0 ) ?? fallback );
}
