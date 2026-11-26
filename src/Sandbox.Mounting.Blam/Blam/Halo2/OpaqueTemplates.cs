using System;
using System.Collections.Generic;

namespace Blam.Halo2;

// Inside the namespace so these win over s&box's global vector types when this compiles into the mount.
using Vector4 = System.Numerics.Vector4;

/// <summary>
/// How a template applies a texture transform register to the base map's UV: <c>uv * scale + offset</c>.
/// </summary>
public enum UvKind
{
	/// <summary>Untransformed.</summary>
	None,

	/// <summary>A whole vertex constant: scale xy, offset zw (Halo's "2d simple xform").</summary>
	ScaleOffset,

	/// <summary>Scale only, from the constant's xy.</summary>
	ScaleXY,

	/// <summary>Scale only, from the constant's zw.</summary>
	ScaleZW,

	/// <summary>A scale-offset register assembled one component each from up to four constants (-1: identity).</summary>
	Components,
}

/// <summary>
/// Where a template's texture transform comes from: which of the shader's vertex constants, and how.
/// </summary>
public readonly record struct UvTransform( UvKind Kind, int Slot = -1, int SlotY = -1, int SlotZ = -1, int SlotW = -1 )
{
	public static UvTransform Xform( int slot ) => new( UvKind.ScaleOffset, slot );
	public static UvTransform ScaleXY( int slot ) => new( UvKind.ScaleXY, slot );
	public static UvTransform ScaleZW( int slot ) => new( UvKind.ScaleZW, slot );
	public static UvTransform Components( int x, int y, int z, int w ) => new( UvKind.Components, x, y, z, w );

	/// <summary>
	/// The transform as (scale u, scale v, offset u, offset v), identity where a constant is missing.
	/// </summary>
	public Vector4 Evaluate( IReadOnlyList<Vector4> constants )
	{
		Vector4? Constant( int slot ) => slot >= 0 && slot < constants.Count ? constants[slot] : null;

		switch ( Kind )
		{
			case UvKind.ScaleOffset:
				return Constant( Slot ) ?? new Vector4( 1, 1, 0, 0 );
			case UvKind.ScaleXY:
				return Constant( Slot ) is { } xy ? new Vector4( xy.X, xy.Y, 0, 0 ) : new Vector4( 1, 1, 0, 0 );
			case UvKind.ScaleZW:
				return Constant( Slot ) is { } zw ? new Vector4( zw.Z, zw.W, 0, 0 ) : new Vector4( 1, 1, 0, 0 );
			case UvKind.Components:
				return new Vector4( Constant( Slot )?.X ?? 1, Constant( SlotY )?.Y ?? 1, Constant( SlotZ )?.Z ?? 0, Constant( SlotW )?.W ?? 0 );
			default:
				return new Vector4( 1, 1, 0, 0 );
		}
	}
}

/// <summary>
/// One opaque shader template as far as drawing it in one pass goes: its passes (whose math <see cref="OpaqueSetup"/>
/// describes), and which slot of a shader's bitmaps, pixel constants and vertex constants fills each role (-1:
/// none). Pixel constants are ARGB colours; scalar roles read one of their bytes (see <see cref="OpaqueSetup"/>).
/// </summary>
public sealed class OpaqueTemplate
{
	/// <summary>
	/// The template's LOD0 passes that matter here (texture accumulation, lightmap, PRT, self-illumination,
	/// environment map), by name below <c>shaders\shader_passes\&lt;layer&gt;\</c>.
	/// </summary>
	public string[] Passes { get; init; } = [];

	// Bitmaps.
	public int Base { get; init; } = -1;
	public int Detail { get; init; } = -1;
	public int DetailB { get; init; } = -1;
	public int DetailC { get; init; } = -1;
	public int Bump { get; init; } = -1;
	public int AlphaMaskA { get; init; } = -1;
	public int AlphaMask { get; init; } = -1;
	public int SpecularMask { get; init; } = -1;
	public int EnvMap { get; init; } = -1;
	public int EnvMask { get; init; } = -1;
	public int SelfIllum { get; init; } = -1;
	public int SelfIllumB { get; init; } = -1;
	public int SelfIllumDetail { get; init; } = -1;
	public int SelfIllumDetailB { get; init; } = -1;
	public int OcclusionMask { get; init; } = -1;
	public int ChangeColorMask { get; init; } = -1;

	// Texture transforms (vertex constants). Every map's but the base's applies to the base-transformed UV.
	public UvTransform BaseUv { get; init; }
	public UvTransform DetailUv { get; init; }
	public UvTransform DetailBUv { get; init; }
	public UvTransform DetailCUv { get; init; }
	public UvTransform BumpUv { get; init; }
	public UvTransform AlphaMaskAUv { get; init; }
	public UvTransform AlphaMaskUv { get; init; }
	public UvTransform SpecularMaskUv { get; init; }
	public UvTransform EnvMapUv { get; init; }
	public UvTransform EnvMaskUv { get; init; }
	public UvTransform SelfIllumUv { get; init; }
	public UvTransform SelfIllumBUv { get; init; }
	public UvTransform SelfIllumDetailUv { get; init; }
	public UvTransform SelfIllumDetailBUv { get; init; }
	public UvTransform OcclusionMaskUv { get; init; }
	public UvTransform ChangeColorMaskUv { get; init; }

	// Pixel constants.
	public int AlbedoMultiply { get; init; } = -1;
	public int AlbedoAdd { get; init; } = -1;
	public int SpecularColor { get; init; } = -1;

	/// <summary>The specular colour at grazing angles (<see cref="SpecularColor"/> facing the eye): objects' (the object
	/// implementation's extra constant) and prt_scarab's.</summary>
	public int SpecularColorGrazing { get; init; } = -1;
	public int EnvTintPerpendicular { get; init; } = -1;
	public int EnvTintParallel { get; init; } = -1;
	public int EnvBrightnessPerpendicular { get; init; } = -1;
	public int EnvBrightnessParallel { get; init; } = -1;
	public int SheenWidth { get; init; } = -1;
	public int SheenColor { get; init; } = -1;
	public int SelfIllumColor { get; init; } = -1;
	public int SelfIllumDetailStrength { get; init; } = -1;
	public int ChannelRIntensity { get; init; } = -1;
	public int ChannelGIntensity { get; init; } = -1;
	public int ChannelBIntensity { get; init; } = -1;
	public int ChannelRColor { get; init; } = -1;
	public int ChannelGColor { get; init; } = -1;
	public int ChannelBColor { get; init; } = -1;
	public int OcclusionTintR { get; init; } = -1;
	public int OcclusionTintG { get; init; } = -1;
	public int OcclusionTintB { get; init; } = -1;
	public int MeterValue { get; init; } = -1;
	public int MeterColorBelow { get; init; } = -1;
	public int MeterColorAbove { get; init; } = -1;
	public int PlasmaThreshold { get; init; } = -1;
	public int PlasmaBandColor { get; init; } = -1;

	// Vertex constants: the separate environment map layer's colours (rgb) and brightnesses (w).
	public int EnvLayerPerpendicularColor { get; init; } = -1;
	public int EnvLayerPerpendicularBrightness { get; init; } = -1;
	public int EnvLayerParallelColor { get; init; } = -1;
	public int EnvLayerParallelBrightness { get; init; } = -1;

	/// <summary>Vertex constant: the PRT passes' irradiance scale (its x, <c>c21.x</c>).</summary>
	public int PrtScale { get; init; } = -1;

	OpaqueSetup _setup;
	bool _described;

	/// <summary>
	/// What the template's passes compute, or null for templates a single opaque pass can't draw (decals and
	/// overlays, additive glows, active camouflage, emblems).
	/// </summary>
	public OpaqueSetup Setup
	{
		get
		{
			if ( !_described )
			{
				_setup = OpaqueTemplates.Describe( Passes );
				_described = true;
			}

			return _setup;
		}
	}
}

/// <summary>
/// Albedo: what multiplies twice the light (detail maps A, B, C; a = the base map's alpha).
/// </summary>
public enum OpaqueAlbedo
{
	/// <summary>The base map.</summary>
	Base,

	/// <summary>min( 2 base A, 1 ).</summary>
	BaseDetail,

	/// <summary>The base's alpha blends detail A to B, then min( 2 base detail, 1 ).</summary>
	DetailBlend,

	/// <summary>Detail blend, then min( 2 that C, 1 ).</summary>
	DetailBlendDetail,

	/// <summary>The base's alpha picks detail A, B or C (blending between them).</summary>
	ThreeDetailBlend,

	/// <summary>min( 2 C min( 2 base A, 1 ), 1 ).</summary>
	TwoDetail,

	/// <summary>min( 2 base min( 2 A C, 1 ), 1 ).</summary>
	DetailOverlay,

	/// <summary>Nothing: self-illumination only.</summary>
	Black,

	/// <summary>Objects' change colour: base times cc1 per channel of the mask's rgb (cc( m ) = sat( m CC + 1 - m )).</summary>
	OneChangeColor,

	/// <summary>min( 2 cc2( m.g ) cc1( m.r ) base detail, 1 ).</summary>
	TwoChangeColor,

	/// <summary>min( 2 sat( sat( cc3( m.b ) cc4( m.a ) ) cc1( m.r ) cc2( m.g ) base ) detail, 1 ), cc4 unsaturated.</summary>
	FourChangeColor,
}

/// <summary>
/// Where the gloss comes from, which masks the specular, the reflections and what later layers add.
/// </summary>
public enum OpaqueGloss
{
	None,
	BaseAlpha,
	BaseDetailAlpha,
	DoubleBaseDetailAlpha,
	BumpAlpha,
	BumpMaskAlpha,
	DetailBlendAlpha,
	ThreeDetailBlendAlpha,
	TwoDetailAlpha,
	SpecularMaskAlpha,
	SpecularMaskBlue,
	DoubleMultiplyAlpha,
}

public enum OpaqueBump
{
	/// <summary>The bump map shades the lightmap: lm + lm d (N.L - L.z).</summary>
	Bumped,

	/// <summary>The lightmap as it is, no specular.</summary>
	Flat,

	/// <summary>A flat normal with Halo's doubled N.L: lm (1 + d L.z).</summary>
	FlatDoubled,
}

public enum OpaqueEnv
{
	None,

	/// <summary>Added in the lightmap pass, masked by the gloss.</summary>
	Combined,

	/// <summary>A layer of its own, reflected off the bump map, with a mid-angle sheen.</summary>
	Bumped,

	/// <summary>A layer of its own, reflected off the surface.</summary>
	Fast,

	/// <summary>The fast layer through a mask (outside it the reflection to the fourth).</summary>
	FastMasked,
}

public enum OpaqueAlphaTest
{
	None,
	BaseAlpha,

	/// <summary>The alpha test maps' alphas (and the bump map's, see <see cref="OpaqueSetup.AlphaTestBump"/>).</summary>
	Maps,
}

public enum OpaqueIllum
{
	None,
	Simple,
	Detail,
	DetailSinglePass,
	TwoMapsDetail,
	Masked,
	ThreeChannel,
	ThreeChannelOcclusion,
	Meter,
	PlasmaOneChannel,
}

/// <summary>
/// PRT lighting (layer 5 passes, campaign objects and a few multiplayer ones): the object's spherical harmonic
/// light through each vertex's precomputed radiance transfer instead of a lightmap or the object lighting.
/// </summary>
public enum OpaquePrt
{
	None,

	/// <summary>prt_simple: the texture accumulation pass's albedo, doubled; specular cube highlight.</summary>
	Simple,

	/// <summary>prt_lightmap: base times detail, quadrupled, plus the vertex lightmap colour; specular cube highlight.</summary>
	Lightmap,

	/// <summary>prt_scarab: like prt_lightmap with the cube map added to the light, and a sharp highlight.</summary>
	Scarab,
}

/// <summary>
/// What an opaque template's passes compute, as halo2_opaque.shader's modes, matching MCC's pixel shaders.
/// </summary>
/// <param name="GlossSquared">The lightmap specular mask is the gloss squared (lightmap_tex_detail: base alpha twice).</param>
/// <param name="GlossGated">The lightmap specular mask is scaled by max( 2 d - 1, 0 ), d the light's directionality.</param>
/// <param name="SpecularGloss">The lightmap specular's own gloss source, if it differs (the flat env template).</param>
/// <param name="AlphaTestBump">The alpha test includes the bump map's alpha (MCC's alpha tested bumped templates cut on it).</param>
/// <param name="ClampU">The base and bump maps clamp U.</param>
/// <param name="Prt">Lit by PRT rather than the lightmap or object lighting.</param>
public sealed record OpaqueSetup(
	OpaqueAlbedo Albedo,
	OpaqueGloss Gloss,
	bool GlossSquared,
	bool GlossGated,
	OpaqueGloss? SpecularGloss,
	OpaqueBump Bump,
	OpaqueEnv Env,
	OpaqueAlphaTest AlphaTest,
	bool AlphaTestBump,
	OpaqueIllum Illum,
	bool ClampU,
	OpaquePrt Prt = OpaquePrt.None );

/// <summary>
/// The opaque templates (see the generated table) and the math of their passes.
/// </summary>
public static partial class OpaqueTemplates
{
	/// <summary>
	/// A template by its name below <c>shaders\shader_templates\</c> (<c>opaque\tex_bump</c>), or null.
	/// </summary>
	public static OpaqueTemplate Get( string name ) => Table.GetValueOrDefault( name ?? "" );

	public static IReadOnlyDictionary<string, OpaqueTemplate> All => Table;

	/// <summary>
	/// A lightmap (layer 3) or PRT (layer 5) pass: what it lights with, and, unless a texture accumulation pass
	/// builds the albedo first, its albedo and gloss.
	/// </summary>
	record LightingPass( OpaqueAlbedo Albedo, OpaqueGloss Gloss, bool Squared, bool Gated, OpaqueBump Bump = OpaqueBump.Bumped,
		OpaqueEnv Env = OpaqueEnv.None, OpaqueAlphaTest AlphaTest = OpaqueAlphaTest.None, bool AlphaTestBump = false,
		OpaqueIllum Illum = OpaqueIllum.None, OpaqueGloss? SpecularGloss = null, bool ClampU = false, OpaquePrt Prt = OpaquePrt.None );

	static LightingPass Lighting( string pass ) => pass switch
	{
		// tex_bump and most bumped templates: base times detail, gloss = base alpha (squared for the specular).
		"lightmap_tex_detail" or "lightmap_tex_detail_no_alpha" or "lightmap_tex_detail_shiny" => new( OpaqueAlbedo.BaseDetail, OpaqueGloss.BaseAlpha, true, true ),
		"lightmap_tex_detail_no_specular" => new( OpaqueAlbedo.BaseDetail, OpaqueGloss.None, false, false ),
		"lightmap_tex" => new( OpaqueAlbedo.Base, OpaqueGloss.BaseAlpha, false, true ),
		"lightmap_tex_two_detail" => new( OpaqueAlbedo.TwoDetail, OpaqueGloss.TwoDetailAlpha, false, true ),
		"lightmap_tex_detail_overlay" => new( OpaqueAlbedo.DetailOverlay, OpaqueGloss.None, false, false ),
		"lightmap_tex_tiling" => new( OpaqueAlbedo.BaseDetail, OpaqueGloss.SpecularMaskBlue, false, true ),

		// Environment mapped in the lightmap pass.
		"lightmap_tex_env_detail" => new( OpaqueAlbedo.BaseDetail, OpaqueGloss.BaseDetailAlpha, false, true, Env: OpaqueEnv.Combined ),
		"lightmap_tex_env" or "lightmap_tex_change_color_env" => new( OpaqueAlbedo.Base, OpaqueGloss.BaseAlpha, false, true, Env: OpaqueEnv.Combined ),
		"lightmap_tex_env_clamped" => new( OpaqueAlbedo.Base, OpaqueGloss.BaseAlpha, false, true, Env: OpaqueEnv.Combined, ClampU: true ),
		"lightmap_tex_env_detail_dbl_spec" => new( OpaqueAlbedo.BaseDetail, OpaqueGloss.DoubleBaseDetailAlpha, false, true, Env: OpaqueEnv.Combined ),
		"lightmap_tex_env_no_bump" => new( OpaqueAlbedo.BaseDetail, OpaqueGloss.BaseDetailAlpha, false, true, OpaqueBump.FlatDoubled, OpaqueEnv.Combined, SpecularGloss: OpaqueGloss.BaseAlpha ),
		"lightmap_tex_env_detail_alpha_test" => new( OpaqueAlbedo.BaseDetail, OpaqueGloss.BaseDetailAlpha, false, true, Env: OpaqueEnv.Combined, AlphaTest: OpaqueAlphaTest.Maps, AlphaTestBump: true ),

		// Alpha tested: the bump map's alpha is the cut-out (and the gloss, ungated), except without a bump map.
		"lightmap_tex_detail_alpha_test" => new( OpaqueAlbedo.BaseDetail, OpaqueGloss.BumpAlpha, false, false, AlphaTest: OpaqueAlphaTest.Maps, AlphaTestBump: true ),
		"lightmap_tex_detail_alpha_test_clamped" => new( OpaqueAlbedo.BaseDetail, OpaqueGloss.BumpMaskAlpha, false, false, AlphaTest: OpaqueAlphaTest.Maps, AlphaTestBump: true, ClampU: true ),
		"lightmap_tex_detail_alpha_test_no_bump" => new( OpaqueAlbedo.BaseDetail, OpaqueGloss.None, false, false, OpaqueBump.Flat, AlphaTest: OpaqueAlphaTest.BaseAlpha ),
		"lightmap_tex_illum_alpha_test" => new( OpaqueAlbedo.Base, OpaqueGloss.BumpMaskAlpha, false, false, AlphaTest: OpaqueAlphaTest.Maps, AlphaTestBump: true, Illum: OpaqueIllum.Simple ),

		// Detail blends: the base map's alpha blends detail maps.
		"lightmap_tex_detail_blend" => new( OpaqueAlbedo.DetailBlend, OpaqueGloss.DetailBlendAlpha, false, false ),
		"lightmap_tex_detail_blend_detail" => new( OpaqueAlbedo.DetailBlendDetail, OpaqueGloss.DetailBlendAlpha, false, true ),
		"lightmap_tex_detail_blend_specular" => new( OpaqueAlbedo.DetailBlend, OpaqueGloss.SpecularMaskAlpha, false, false ),
		"lightmap_tex_detail_blend_specular_dblmult" => new( OpaqueAlbedo.DetailBlend, OpaqueGloss.DoubleMultiplyAlpha, false, true ),
		"lightmap_tex_three_detail_blend" => new( OpaqueAlbedo.ThreeDetailBlend, OpaqueGloss.ThreeDetailBlendAlpha, false, true ),

		// Self-illumination added in the lightmap pass, no bump map.
		"lightmap_tex_detail_illum_no_bump" => new( OpaqueAlbedo.BaseDetail, OpaqueGloss.BaseAlpha, true, true, OpaqueBump.FlatDoubled, Illum: OpaqueIllum.Simple ),

		// Objects' change colours (not drawn here: the colours count as white), honor guard pattern (left out).
		"lightmap_tex_one_change_color" => new( OpaqueAlbedo.OneChangeColor, OpaqueGloss.BaseAlpha, true, true ),
		"lightmap_tex_two_change_color" => new( OpaqueAlbedo.TwoChangeColor, OpaqueGloss.BaseAlpha, true, true ),
		"lightmap_tex_four_change_color" => new( OpaqueAlbedo.FourChangeColor, OpaqueGloss.BaseAlpha, true, true ),
		"lightmap_tex_change_color" => new( OpaqueAlbedo.Base, OpaqueGloss.None, false, false, OpaqueBump.Flat ),
		"lightmap_honor_guard" => new( OpaqueAlbedo.BaseDetail, OpaqueGloss.BaseDetailAlpha, false, true ),

		// Lighting for an albedo from the texture accumulation pass, which then decides albedo and gloss.
		"old_lightmap_tangentspace" => new( OpaqueAlbedo.Base, OpaqueGloss.BaseAlpha, false, true ),
		"lightmap_env" => new( OpaqueAlbedo.Base, OpaqueGloss.BaseAlpha, false, true, Env: OpaqueEnv.Combined ),

		// PRT (objects lit by spherical harmonics through their precomputed radiance transfer): base and detail
		// (prt_simple's from its texture accumulation pass), gloss = base alpha (and detail alpha, see OpaquePrt).
		"prt_simple" => new( OpaqueAlbedo.BaseDetail, OpaqueGloss.BaseDetailAlpha, false, false, Prt: OpaquePrt.Simple ),
		"prt_lightmap" => new( OpaqueAlbedo.BaseDetail, OpaqueGloss.BaseDetailAlpha, false, false, Prt: OpaquePrt.Lightmap ),
		"prt_scarab" => new( OpaqueAlbedo.BaseDetail, OpaqueGloss.BaseAlpha, false, false, Prt: OpaquePrt.Scarab ),

		_ => null,
	};

	/// <summary>
	/// A texture accumulation (layer 1) pass: the albedo (and its alpha, the gloss) a later lightmap pass lights.
	/// </summary>
	static (OpaqueAlbedo Albedo, OpaqueGloss Gloss, OpaqueAlphaTest AlphaTest, bool ClampU)? Accumulation( string pass ) => pass switch
	{
		"default_texture_zwrite" or "default_texture_two_change_color_multiply_map_zwrite" => (OpaqueAlbedo.BaseDetail, OpaqueGloss.BaseDetailAlpha, OpaqueAlphaTest.None, false),
		"default_texture_clamped_zwrite" => (OpaqueAlbedo.BaseDetail, OpaqueGloss.BaseDetailAlpha, OpaqueAlphaTest.None, true),
		"detail_blend_texture_zwrite" => (OpaqueAlbedo.DetailBlend, OpaqueGloss.DetailBlendAlpha, OpaqueAlphaTest.None, false),
		"default_texture_zwrite_alphatest" => (OpaqueAlbedo.BaseDetail, OpaqueGloss.BaseDetailAlpha, OpaqueAlphaTest.Maps, false),
		_ => null,
	};

	static OpaqueIllum SelfIllumination( string pass ) => pass switch
	{
		"default_self_illumination" or "default_self_illumination_scroll" or "default_self_illumination_single_pass"
			or "default_self_illumination_single_pass_clamped" or "default_self_illumination_single_pass_zwrite" => OpaqueIllum.Simple,
		"default_self_illumination_detail" => OpaqueIllum.Detail,
		"default_self_illumination_detail_single_pass" => OpaqueIllum.DetailSinglePass,
		"self_illumination_single_pass_detail" or "self_illumination_single_pass_detail_zwrite" => OpaqueIllum.TwoMapsDetail,
		"self_illumination_single_pass_detail_masked" => OpaqueIllum.Masked,
		"multichannel_self_illumination" or "multichannel_self_illumination_single_pass" or "multichannel_self_illumination_zwrite" => OpaqueIllum.ThreeChannel,
		"multichannel_self_illumination_occlusion" => OpaqueIllum.ThreeChannelOcclusion,
		"meter_self_illumination" => OpaqueIllum.Meter,
		"plasma_one_channel_self_illumination" => OpaqueIllum.PlasmaOneChannel,
		_ => OpaqueIllum.None,
	};

	static OpaqueEnv EnvironmentMap( string pass ) => pass switch
	{
		"default_environment_map_tangent_space" => OpaqueEnv.Bumped,
		"default_environment_map_tangent_space_fast" => OpaqueEnv.Fast,
		"default_environment_map_tangent_space_fast_masked" => OpaqueEnv.FastMasked,
		_ => OpaqueEnv.None,
	};

	/// <summary>
	/// Folds a template's passes into one setup: the lightmap pass lights the albedo (from the texture
	/// accumulation pass if there is one), the environment map and self-illumination layers add onto it. Templates
	/// without a lightmap pass are drawn only if their self-illumination pass is opaque (writes depth): black plus
	/// the glow. Everything else (overlays and decals blended onto other geometry, additive glows, active camouflage)
	/// isn't opaque geometry: null.
	/// </summary>
	public static OpaqueSetup Describe( IReadOnlyList<string> passes )
	{
		LightingPass lighting = null;
		(OpaqueAlbedo Albedo, OpaqueGloss Gloss, OpaqueAlphaTest AlphaTest, bool ClampU)? accumulation = null;
		var illum = OpaqueIllum.None;
		var opaqueIllum = false;
		var env = OpaqueEnv.None;
		var unknownLighting = false;

		foreach ( var pass in passes )
		{
			if ( Lighting( pass ) is { } l )
				lighting = l;
			else if ( Accumulation( pass ) is { } a )
				accumulation = a;
			else if ( SelfIllumination( pass ) is not OpaqueIllum.None and var i )
			{
				illum = i;
				opaqueIllum = pass.EndsWith( "_zwrite", StringComparison.Ordinal );
			}
			else if ( EnvironmentMap( pass ) is not OpaqueEnv.None and var e )
				env = e;
			else if ( pass.StartsWith( "lightmap", StringComparison.Ordinal ) || pass == "texaccum_disabled" )
				unknownLighting = true;
		}

		if ( unknownLighting )
			return null;

		if ( lighting is null )
		{
			return opaqueIllum
				? new OpaqueSetup( OpaqueAlbedo.Black, OpaqueGloss.None, false, false, null, OpaqueBump.Flat, OpaqueEnv.None, OpaqueAlphaTest.None, false, illum, false )
				: null;
		}

		return new OpaqueSetup(
			Albedo: accumulation?.Albedo ?? lighting.Albedo,
			Gloss: accumulation?.Gloss ?? lighting.Gloss,
			GlossSquared: lighting.Squared,
			GlossGated: lighting.Gated,
			SpecularGloss: lighting.SpecularGloss,
			Bump: lighting.Bump,
			Env: lighting.Env != OpaqueEnv.None ? lighting.Env : env,
			AlphaTest: accumulation is { AlphaTest: not OpaqueAlphaTest.None } ? accumulation.Value.AlphaTest : lighting.AlphaTest,
			AlphaTestBump: lighting.AlphaTestBump,
			Illum: lighting.Illum != OpaqueIllum.None ? lighting.Illum : illum,
			ClampU: lighting.ClampU || accumulation is { ClampU: true },
			Prt: lighting.Prt );
	}
}
