using Blam.Halo2.Tags;

namespace Sandbox.Mounting.Blam.Halo2;

/// <summary>
/// Builds s&amp;box materials for Halo 2's transparent shader templates (halo2_transparent.shader): glass, lights,
/// holograms, gas, clouds, waves, meters and sky layers; and the opaque overlay templates (overlay, decal_simple:
/// markings, labels and grime drawn over the surface under them in Halo's overlay layer).
/// </summary>
/// <remarks>
/// A template draws its transparent part in one or more passes of the transparent layer; the program is picked
/// by the first pass's name (the pass, not the template, decides the pixel shader: one_alpha_env,
/// one_alpha_env_illum, one_add_env_illum and roadsign_glass all draw default_transparent_one_sided with their
/// own slot orders), and the slots are routed by the template (<see cref="TemplatePassParameters"/>).
///
/// A material draws one pass, the template's first unless told otherwise: models draw the later passes (Cortana's
/// plasma traces and bumped reflection, some glass's masked bump reflection) and the z prepass with copies of the
/// mesh (<see cref="Halo2Materials.ExtraPasses"/>).
/// </remarks>
static class TransparentMaterial
{
	public const string ShaderPath = "shaders/halo2_transparent.shader";

	/// <summary>
	/// The fraction of its distance an overlay is drawn nearer the camera (g_flHaloDepthBias), standing in for
	/// MCC's depth bias of -1e-5: enough over the surface it shares at any distance, too little to show through
	/// anything in front.
	/// </summary>
	const float OverlayDepthBias = 2e-4f;

	/// <summary>
	/// The programs of halo2_transparent.shader (g_flProgram).
	/// </summary>
	internal enum Program
	{
		Generic, NoFog, Detail, Multichannel, OneAddIllum, OneAddIllumDetail, SelfIllumDetail, TwoPlusTwo,
		DetailMaskedAdd, DetailMaskedAlpha, CloudsAlpha, CloudsAdd, SkyCloudsAdd, Waves, Meter, SkyEnvironment, SkyIllumDetail,
		Overlay, DecalSimple, BumpedEnvironment, LitTwoChangeColor, Emblem, SelfIllum,
	}

	[Flags]
	enum Variant { None = 0, FixedAlpha = 1, ThreeChannelIllum = 2, CamoFade = 4 }

	/// <summary>
	/// Pass (below shaders\shader_passes\) → program. Every pass with a reflection reads its cube map from stage 0.
	/// </summary>
	static readonly Dictionary<string, (Program Program, Variant Variant)> Passes = new( StringComparer.OrdinalIgnoreCase )
	{
		[@"transparent\default_transparent_one_sided"] = (Program.Generic, Variant.None),
		[@"transparent\default_transparent_one_sided_clamped"] = (Program.Generic, Variant.None),
		[@"transparent\default_transparent_one_sided_fixed"] = (Program.Generic, Variant.FixedAlpha),
		[@"transparent\default_transparent_one_sided_active_camo"] = (Program.Generic, Variant.CamoFade),
		[@"transparent\default_transparent"] = (Program.Generic, Variant.None),
		[@"transparent\default_transparent_add"] = (Program.Generic, Variant.None),
		[@"transparent\default_transparent_add_active_camo"] = (Program.Generic, Variant.CamoFade),
		[@"transparent\roadsign_glass_newmombasa"] = (Program.Generic, Variant.None),
		[@"transparent\cortana_default_transparent_one_sided"] = (Program.Generic, Variant.None),
		[@"transparent\transparent_add_illum_3_channel"] = (Program.Generic, Variant.ThreeChannelIllum),
		[@"transparent\default_transparent_one_sided_no_fog"] = (Program.NoFog, Variant.None),
		[@"transparent\default_transparent_detail"] = (Program.Detail, Variant.None),
		[@"transparent\default_transparent_multichannel"] = (Program.Multichannel, Variant.None),
		[@"transparent\one_add_illum"] = (Program.OneAddIllum, Variant.None),
		[@"transparent\one_add_illum_detail"] = (Program.OneAddIllumDetail, Variant.None),
		[@"self_illumination\default_self_illumination_detail_single_pass"] = (Program.SelfIllumDetail, Variant.None),

		// Glow-only opaque templates' self-illumination passes (illum, illum_clamped), found when a template has no lighting pass.
		[@"self_illumination\default_self_illumination"] = (Program.SelfIllum, Variant.None),
		[@"self_illumination\default_self_illumination_scroll"] = (Program.SelfIllum, Variant.None),
		[@"self_illumination\default_self_illumination_single_pass"] = (Program.SelfIllum, Variant.None),
		[@"self_illumination\default_self_illumination_single_pass_clamped"] = (Program.SelfIllum, Variant.None),
		[@"transparent\one_add_two_plus_two"] = (Program.TwoPlusTwo, Variant.None),
		[@"transparent\sky_one_add_two_plus_two"] = (Program.TwoPlusTwo, Variant.None),
		[@"transparent\add_detail_masked"] = (Program.DetailMaskedAdd, Variant.None),
		[@"transparent\sky_add_detail_masked"] = (Program.DetailMaskedAdd, Variant.None),
		[@"transparent\alpha_detail_masked"] = (Program.DetailMaskedAlpha, Variant.None),
		[@"transparent\clouds_simple_transparent"] = (Program.CloudsAlpha, Variant.None),
		[@"transparent\sky_clouds_simple_transparent"] = (Program.CloudsAlpha, Variant.None),
		[@"transparent\add_clouds_simple_transparent"] = (Program.CloudsAdd, Variant.None),
		[@"transparent\sky_add_clouds_simple_transparent"] = (Program.SkyCloudsAdd, Variant.None),
		[@"transparent\sky_add_clouds_simple_transparent_clamped"] = (Program.SkyCloudsAdd, Variant.None),
		[@"transparent\waves"] = (Program.Waves, Variant.None),
		[@"transparent\meter"] = (Program.Meter, Variant.None),
		[@"transparent\meter_active_camo"] = (Program.Meter, Variant.CamoFade),
		[@"transparent\sky_default_transparent_one_sided"] = (Program.SkyEnvironment, Variant.None),
		[@"transparent\sky_default_transparent_one_sided_clamped"] = (Program.SkyEnvironment, Variant.None),
		[@"transparent\sky_one_add_illum_detail"] = (Program.SkyIllumDetail, Variant.None),
		[@"transparent\lit\transparent_lit_alpha_blend_two_change_color"] = (Program.LitTwoChangeColor, Variant.None),

		// The overlay layer's passes (opaque\overlay, opaque\decal_simple), found when a template has no transparent pass.
		[@"overlay\overlay"] = (Program.Overlay, Variant.None),
		[@"overlay\decal_simple_double_multiply"] = (Program.DecalSimple, Variant.None),
		[@"texture_mapping\emblem_overlay_simple"] = (Program.Emblem, Variant.None),

		// Passes following a first one.
		[@"transparent\bumped_environment_additive"] = (Program.BumpedEnvironment, Variant.None),
		[@"transparent\bumped_environment_masked"] = (Program.BumpedEnvironment, Variant.None),
	};

	// The stage holding the program's cube map, -1 for none.
	static int CubeStage( Program program ) => program switch
	{
		Program.Generic or Program.Detail or Program.Multichannel or Program.SkyEnvironment => 0,
		Program.BumpedEnvironment => 3,
		_ => -1,
	};

	/// <summary>
	/// The transparent pass this material would draw, or null if the shader has none this supports.
	/// </summary>
	public static ShaderTemplatePass FindPass( ShaderTag shader )
	{
		if ( shader.Template is null )
			return null;

		var template = new ShaderTemplateTag( shader.Template );
		var pass = template.Passes( ShaderTemplateTag.TransparentLayer ).FirstOrDefault()
			?? template.Passes( ShaderTemplateTag.OverlayLayer ).FirstOrDefault()
			?? GlowPass( template );
		return pass is not null && Passes.ContainsKey( pass.Name ) && TemplatePassParameters.BlendMode( pass ) is not null ? pass : null;
	}

	// A glow-only template's pass (illum, illum_clamped, illum_detail: the battle rifle's ammunition digits), which Halo adds over the surface without writing depth.
	// Only for templates that don't light a surface of their own.
	static ShaderTemplatePass GlowPass( ShaderTemplateTag template ) =>
		template.Passes( ShaderTemplateTag.LightmapLayer ).Count == 0 ? template.Passes( ShaderTemplateTag.SelfIllumLayer ).FirstOrDefault() : null;

	// Whether the shader draws a glow-only template's pass (see GlowPass).
	public static bool IsGlow( ShaderTag shader ) =>
		shader.Template is { } stem && new ShaderTemplateTag( stem ).Passes( ShaderTemplateTag.TransparentLayer ).Count == 0
		&& new ShaderTemplateTag( stem ).Passes( ShaderTemplateTag.OverlayLayer ).Count == 0 && FindPass( shader ) is not null;

	public static bool IsSupported( ShaderTag shader ) => FindPass( shader ) is not null;

	/// <summary>Whether this draws a pass (by its name, below shaders\shader_passes\).</summary>
	public static bool Draws( ShaderTemplatePass pass ) => Passes.ContainsKey( pass.Name ) && TemplatePassParameters.BlendMode( pass ) is not null;

	/// <summary>Whether the shader's material draws an overlay on the surface (signs, labels, emblems), its opaque
	/// template's overlay pass.</summary>
	public static bool IsOverlay( ShaderTag shader ) =>
		FindPass( shader ) is { } pass && Passes[pass.Name].Program is Program.Overlay or Program.DecalSimple or Program.Emblem;

	/// <summary>Whether the shader's material is tinted by its object's change colours (brute hair).</summary>
	public static bool UsesChangeColors( ShaderTag shader ) =>
		FindPass( shader ) is { } pass && Passes[pass.Name].Program == Program.LitTwoChangeColor;

	/// <param name="onObject">On an object rather than level geometry or the sky (see <see cref="TemplatePassParameters.Apply"/>).</param>
	/// <param name="randomConstant">On an object: its <c>random_constant</c>, if known.</param>
	/// <param name="named">Findable by name (not anonymous): see <see cref="ModelMaterials.Create"/>.</param>
	/// <param name="pass">The template's pass to draw (one of its later passes, see <see cref="Halo2Materials.ExtraPasses"/>);
	/// null for its first.</param>
	public static Material Create( string name, ShaderTag shader, bool onObject, float? randomConstant = null, bool named = false, ShaderTemplatePass pass = null,
		IReadOnlyList<string> functions = null )
	{
		pass ??= FindPass( shader );
		if ( pass is null || !Draws( pass ) )
			return null;

		var material = Material.Create( name, ShaderPath, anonymous: !named );
		if ( material is null )
			return null;

		var (program, variant) = Passes[pass.Name];
		TemplatePassParameters.Apply( material, shader, pass, onObject, CubeStage( program ), randomConstant: randomConstant, functions: functions );

		// Additive passes add in Halo's gamma space (the sky's layers turn it off, see ModelMaterials).
		material.SetFeature( "F_GAMMA_ADD", TemplatePassParameters.BlendMode( pass ) is 1 or 3 ? 1 : 0 );

		material.Set( "g_flProgram", (float)program );
		material.Set( "g_flFixedAlpha", variant.HasFlag( Variant.FixedAlpha ) ? 1f : 0f );
		material.Set( "g_flThreeChannelIllum", variant.HasFlag( Variant.ThreeChannelIllum ) ? 1f : 0f );
		material.Set( "g_flCamoFade", variant.HasFlag( Variant.CamoFade ) ? 1f : 0f );

		// The lit template reads its object's light and change colours from the object table.
		if ( program == Program.LitTwoChangeColor )
			ObjectLightingTable.Bind( material );

		// Halo's depth bias for the overlays, which lie on the surface they draw over, and the glows, which may.
		if ( program is Program.Overlay or Program.DecalSimple or Program.Emblem || IsGlow( shader ) )
			material.Set( "g_flHaloDepthBias", OverlayDepthBias );

		// Emblems pick their picture and colours per object (Emblems.cs).
		if ( program == Program.Emblem )
		{
			Emblems.Apply( material, shader );
			ObjectLightingTable.Bind( material );
		}

		return material;
	}
}
