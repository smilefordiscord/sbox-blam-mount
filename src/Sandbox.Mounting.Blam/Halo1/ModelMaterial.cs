using System.Runtime.CompilerServices;
using Blam.Halo1;
using Blam.Halo1.Tags;

namespace Sandbox.Mounting.Blam.Halo1;

/// <summary>
/// Builds s&amp;box materials for Halo CE shader_model tags.
/// </summary>
static class ModelMaterial
{
	public const string ShaderPath = "shaders/halo1_model.shader";

	/// <summary>
	/// The camouflaged variant of the model shader, generated at build time (see tools/compile-shaders.ps1).
	/// </summary>
	public const string CamouflagedShaderPath = "shaders/halo1_model_camo.shader";

	/// <summary>
	/// The variant of the model shader for MCC's weapon skins (see <see cref="WeaponSkins"/>), generated
	/// at build time like the camouflaged one.
	/// </summary>
	public const string SkinShaderPath = "shaders/halo1_model_skin.shader";

	/// <summary>
	/// Change colors B-D for each model shader of a map, worked out once per map (see
	/// <see cref="ObjectChangeColors.TypicalColorsByShader"/>).
	/// </summary>
	static readonly ConditionalWeakTable<CacheFile, Dictionary<uint, System.Numerics.Vector3[]>> _changeColors = new();

	/// <param name="camouflaged">The variant for a camouflaged object (the <c>_camo</c> models): drawn
	/// after the frame buffer copy, so it can blend the bent level behind it over its own color by the
	/// renderer's <c>HaloCamo</c>, as Halo's active camouflage pass does (see halo1_model.shader).</param>
	/// <param name="skin">A weapon skin's maps for this shader's base map (see <see cref="WeaponSkins"/>):
	/// drawn with <see cref="SkinShaderPath"/>, its maps over the shader's own.</param>
	/// <param name="skinCubeMap">The cube map a skin reflects when this shader has none.</param>
	public static Material Create( HaloCeMount host, string name, CacheFile cache, TagEntry shaderTag, int permutation = 0, bool camouflaged = false, WeaponSkinMaps skin = null, TagEntry skinCubeMap = null )
	{
		var shader = new ShaderModelTag( cache, shaderTag );
		var material = Material.Create( name, skin is not null ? SkinShaderPath : camouflaged ? CamouflagedShaderPath : ShaderPath );
		if ( material is null )
			return null;

		if ( camouflaged )
			ActiveCamouflageMaterial.SetGlobals( host, material, cache );

		var detailDefault = shader.DetailFunction == DetailMapFunction.Multiply ? MaterialTextures.White : MaterialTextures.Grey;

		// Without a multipurpose map every mask is fully on: shaders without one still glow
		// (a10's siren and lock lights) and reflect (b40's metal scenery).
		material.Set( "g_tBaseMap", SkinTexture( host, skin?.Diffuse ) ?? MaterialTextures.LoadPermutation( host, material, cache, shader.BaseMap, permutation ) ?? MaterialTextures.White );
		material.Set( "g_tDetailMap", MaterialTextures.LoadPermutation( host, material, cache, shader.DetailMap, permutation ) ?? detailDefault );
		material.Set( "g_tMultipurposeMap", SkinTexture( host, skin?.Multipurpose ) ?? MaterialTextures.LoadPermutation( host, material, cache, shader.MultipurposeMap, permutation ) ?? MaterialTextures.White );

		material.Set( "g_vMapScale", (Vector2)shader.MapScale );
		material.Set( "g_vDetailScale", new Vector2( shader.DetailMapScale, shader.DetailMapScale * shader.DetailMapVScale ) );
		material.Set( "g_flDetailFunction", (float)shader.DetailFunction );
		material.Set( "g_flAlphaTested", shader.IsAlphaTested ? 1f : 0f );
		material.SetFeature( "F_TWO_SIDED", shader.Flags.HasFlag( ModelShaderFlags.TwoSided ) ? 1 : 0 );
		material.Set( "g_flTwoSidedNearDistance", shader.Flags.HasFlag( ModelShaderFlags.DisableTwoSidedCulling ) ? 8 * HaloCeMount.WorldScale : 0f );
		material.Set( "g_flTranslucency", shader.Translucency );

		var channels = shader.MultipurposeChannels;
		material.Set( "g_vReflectionMaskChannel", Channel( channels.Reflection ) );
		material.Set( "g_vSelfIlluminationMaskChannel", Channel( channels.SelfIllumination ) );
		material.Set( "g_vChangeColorMaskChannel", Channel( channels.ChangeColor ) );
		material.Set( "g_vAuxiliaryMaskChannel", Channel( channels.Auxiliary ) );

		material.Set( "g_vDetailMask", DetailMask( shader ) );

		SetChangeColors( material, cache, shaderTag, shader );
		SetSelfIllumination( material, shader );
		SetAnimation( material, shader );
		if ( skin is not null )
			SetSkin( host, material, cache, shader, skin, skinCubeMap );
		else
			SetReflection( host, material, cache, shader, permutation );

		// Placed objects' lighting and change colors, and the light for anything else, like a model
		// a game spawns (see ObjectLightingTable).
		ObjectLightingTable.Bind( material );

		return material;
	}

	static Texture SkinTexture( HaloCeMount host, string path ) => path is null ? null : host.WeaponSkins?.LoadTexture( path, host.Logger );

	/// <summary>
	/// A weapon skin's physically based maps (missing ones as plain metal 0, roughness 0.5, flat and
	/// unlit), and the cube map that shapes its reflections where no environment probe covers it:
	/// the shader's own, else the model's, sharp and blurred (see <see cref="SkinReflection"/>).
	/// </summary>
	static void SetSkin( HaloCeMount host, Material material, CacheFile cache, ShaderModelTag shader, WeaponSkinMaps skin, TagEntry fallbackCubeMap )
	{
		material.Set( "g_tSkinNormal", SkinTexture( host, skin.Normal ) ?? MaterialTextures.FlatNormal );
		material.Set( "g_tSkinMetallic", SkinTexture( host, skin.Metallic ) ?? MaterialTextures.Transparent );
		material.Set( "g_tSkinRoughness", SkinTexture( host, skin.Roughness ) ?? MaterialTextures.Grey );
		material.Set( "g_tSkinEmissive", SkinTexture( host, skin.Emissive ) ?? MaterialTextures.Transparent );

		var cubeMap = shader.ReflectionCubeMap ?? fallbackCubeMap;
		material.Set( "g_tReflectionMap", MaterialTextures.Load( host, material, cubeMap, 0 ) ?? MaterialTextures.Transparent );
		material.Set( "g_tSkinReflection", SkinReflection.Get( host, cache, cubeMap, out var cubeMean ) ?? MaterialTextures.Transparent );
		material.Set( "g_vSkinCubeMean", cubeMean );
		material.Set( "g_flSkinReflectionLevels", (float)SkinReflection.Levels );
		material.Set( "g_flHasReflection", 0f );
		material.Set( "g_flSkinAlbedoScale", host.WeaponSkins?.AlbedoScale( host, cache, shader.BaseMap, skin ) ?? 1f );
	}

	static Vector4 Channel( int index ) => new( index == 0 ? 1 : 0, index == 1 ? 1 : 0, index == 2 ? 1 : 0, index == 3 ? 1 : 0 );

	/// <summary>
	/// (mask, inverted, detail after reflection), the mask numbered as in the shader: 1 reflection,
	/// 2 self-illumination, 3 change color, 4 auxiliary. The tag enum lists each mask's inverse first.
	/// </summary>
	static Vector3 DetailMask( ShaderModelTag shader )
	{
		var value = (int)shader.DetailMask;
		var mask = value == 0 ? 0 : (value + 1) / 2;
		var inverted = value != 0 && value % 2 == 1;
		return new Vector3( mask, inverted ? 1 : 0, shader.Flags.HasFlag( ModelShaderFlags.DetailAfterReflection ) ? 1 : 0 );
	}

	/// <summary>
	/// Change color A comes from each placed object's tint, picked by the scene. B-D can't be
	/// per object (the tint is the only per-object value a renderer passes the shader), so they
	/// come from the first object in the map that uses the shader, which is plenty: they're only
	/// used for fixed colors like the Chief's armor and lights switched by object functions.
	/// </summary>
	static void SetChangeColors( Material material, CacheFile cache, TagEntry shaderTag, ShaderModelTag shader )
	{
		material.Set( "g_flChangeColorSource", (float)shader.ChangeColorSource );

		var colors = _changeColors.GetValue( cache, ObjectChangeColors.TypicalColorsByShader );
		colors.TryGetValue( shaderTag.Id, out var typical );

		material.Set( "g_vChangeColorB", typical is null ? Vector3.One : (Vector3)typical[1] );
		material.Set( "g_vChangeColorC", typical is null ? Vector3.One : (Vector3)typical[2] );
		material.Set( "g_vChangeColorD", typical is null ? Vector3.One : (Vector3)typical[3] );
	}

	static void SetSelfIllumination( Material material, ShaderModelTag shader )
	{
		material.Set( "g_vSelfIlluminationLower", (Vector3)shader.SelfIlluminationLower );
		material.Set( "g_vSelfIlluminationUpper", (Vector3)shader.SelfIlluminationUpper );
		material.Set( "g_vSelfIlluminationAnimation", new Vector4( (float)shader.SelfIlluminationFunction, shader.SelfIlluminationPeriod, shader.SelfIlluminationNoRandomPhase ? 0 : 1, 0 ) );
		material.Set( "g_flSelfIlluminationSource", (float)shader.SelfIlluminationSource );
	}

	/// <summary>
	/// Texture animations driven by time play; ones scaled by an object function (the Scorpion's
	/// tread position) stay at rest, since nothing runs those functions, unless a game sets the
	/// function on the renderer (HaloFunctionA-D, see the shader).
	/// </summary>
	static void SetAnimation( Material material, ShaderModelTag shader )
	{
		material.Set( "g_vUAnimation", Animation( shader.UAnimation ) );
		material.Set( "g_vVAnimation", Animation( shader.VAnimation ) );
		material.Set( "g_vRotationAnimation", Animation( shader.RotationAnimation ) );
		material.Set( "g_vRotationCenter", (Vector2)shader.RotationCenter );
		material.Set( "g_vAnimationSources", new Vector3( (float)shader.UAnimation.Source, (float)shader.VAnimation.Source, (float)shader.RotationAnimation.Source ) );
	}

	static Vector4 Animation( ModelMapAnimation animation ) =>
		new( (float)animation.Animation.Function, animation.Animation.Period, animation.Animation.Phase, animation.Animation.Scale );

	/// <summary>
	/// The tints and brightnesses go to the shader separately: Halo blends each by N.V on its own
	/// and multiplies them per pixel, which differs from blending their products when the
	/// brightnesses differ. The shader permutation picks the cube map too: the elites' one cube
	/// map tag holds a reflection per rank.
	/// </summary>
	static void SetReflection( HaloCeMount host, Material material, CacheFile cache, ShaderModelTag shader, int permutation )
	{
		var perpendicular = (Vector3)shader.PerpendicularTint * shader.PerpendicularBrightness;
		var parallel = (Vector3)shader.ParallelTint * shader.ParallelBrightness;
		var reflection = perpendicular.Length > 0 || parallel.Length > 0 ? MaterialTextures.LoadPermutation( host, material, cache, shader.ReflectionCubeMap, permutation ) : null;

		material.Set( "g_tReflectionMap", reflection ?? MaterialTextures.Transparent );
		material.Set( "g_flHasReflection", reflection is null ? 0f : 1f );
		material.Set( "g_vPerpendicularReflection", (Vector3)shader.PerpendicularTint );
		material.Set( "g_vParallelReflection", (Vector3)shader.ParallelTint );
		material.Set( "g_vReflectionBrightness", new Vector2( shader.PerpendicularBrightness, shader.ParallelBrightness ) );
		material.Set( "g_vReflectionFade", new Vector2( shader.ReflectionFalloffDistance, shader.ReflectionCutoffDistance ) * HaloCeMount.WorldScale );
	}
}
