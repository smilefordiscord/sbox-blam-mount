using System;
using System.Collections.Generic;

namespace Blam.Halo2;

// Generated from the opaque template census of every MCC map (where each template's passes bind each shader
// parameter, research tool op_cs.py), not written by hand: regenerate rather than edit.

public static partial class OpaqueTemplates
{
	/// <summary>
	/// Every opaque template (by path below <c>shaders\shader_templates\</c>), most used first: its LOD0 passes
	/// that a single pass port needs (texture accumulation, lightmap, PRT, self-illumination, environment map), and
	/// which shader parameter slot fills each role.
	/// </summary>
	static readonly Dictionary<string, OpaqueTemplate> Table = new( StringComparer.OrdinalIgnoreCase )
	{
		// 631 shaders
		[@"opaque\tex_bump"] = new()
		{
			Passes = ["lightmap_tex_detail"],
			Base = 2, Detail = 3, Bump = 0,
			BaseUv = UvTransform.Xform( 1 ), DetailUv = UvTransform.Xform( 2 ), BumpUv = UvTransform.Xform( 0 ),
			SpecularColor = 0, SpecularColorGrazing = 1,
		},
		// 180 shaders
		[@"opaque\tex_bump_env_combined"] = new()
		{
			Passes = ["lightmap_tex_env_detail"],
			Base = 2, Detail = 3, Bump = 0, EnvMap = 4,
			BaseUv = UvTransform.Xform( 1 ), DetailUv = UvTransform.Xform( 2 ), BumpUv = UvTransform.Xform( 0 ),
			SpecularColor = 4, SpecularColorGrazing = 5, EnvTintPerpendicular = 0, EnvTintParallel = 1, EnvBrightnessPerpendicular = 2, EnvBrightnessParallel = 3,
		},
		// 139 shaders
		[@"opaque\tex_bump_env"] = new()
		{
			Passes = ["default_texture_zwrite", "lightmap_env"],
			Base = 2, Detail = 3, Bump = 0, EnvMap = 4,
			BaseUv = UvTransform.Xform( 1 ), DetailUv = UvTransform.Xform( 0 ), BumpUv = UvTransform.Xform( 2 ),
			AlbedoMultiply = 0, SpecularColor = 5, SpecularColorGrazing = 6, EnvTintPerpendicular = 1, EnvTintParallel = 2, EnvBrightnessPerpendicular = 3, EnvBrightnessParallel = 4,
		},
		// 104 shaders
		[@"opaque\tex_bump_illum"] = new()
		{
			Passes = ["lightmap_tex_detail", "default_self_illumination"],
			Base = 1, Detail = 2, Bump = 0, SelfIllum = 3,
			BaseUv = UvTransform.Xform( 1 ), DetailUv = UvTransform.Xform( 2 ), BumpUv = UvTransform.Xform( 0 ), SelfIllumUv = UvTransform.Xform( 7 ),
			SpecularColor = 0, SpecularColorGrazing = 1, SelfIllumColor = 2,
		},
		// 79 shaders
		[@"opaque\prt_simple"] = new()
		{
			Passes = ["default_texture_zwrite", "prt_simple"],
			Base = 1, Detail = 2, Bump = 0, EnvMap = 3,
			BaseUv = UvTransform.Xform( 1 ), DetailUv = UvTransform.Xform( 0 ), BumpUv = UvTransform.Xform( 2 ),
			AlbedoMultiply = 0, SpecularColor = 1,
			PrtScale = 3,
		},
		// 57 shaders
		[@"opaque\tex_bump_env_no_detail"] = new()
		{
			Passes = ["lightmap_tex_env"],
			Base = 2, Bump = 0, EnvMap = 3,
			BaseUv = UvTransform.Xform( 1 ), BumpUv = UvTransform.Xform( 0 ),
			SpecularColor = 4, SpecularColorGrazing = 5, EnvTintPerpendicular = 0, EnvTintParallel = 1, EnvBrightnessPerpendicular = 2, EnvBrightnessParallel = 3,
		},
		// 55 shaders
		[@"opaque\tex_bump_alpha_test_single_pass"] = new()
		{
			Passes = ["lightmap_tex_detail_alpha_test"],
			Base = 2, Detail = 3, Bump = 0,
			BaseUv = UvTransform.Xform( 1 ), DetailUv = UvTransform.Xform( 2 ), BumpUv = UvTransform.Xform( 0 ),
			SpecularColor = 0, SpecularColorGrazing = 1,
		},
		// 50 shaders
		[@"opaque\overlay"] = new()
		{
			Passes = [],
		},
		// 50 shaders
		[@"opaque\tex_bump_shiny"] = new()
		{
			Passes = ["lightmap_tex_detail_shiny"],
			Base = 2, Detail = 3, Bump = 0,
			BaseUv = UvTransform.Xform( 1 ), DetailUv = UvTransform.Xform( 2 ), BumpUv = UvTransform.Xform( 0 ),
			SpecularColor = 0, SpecularColorGrazing = 1,
		},
		// 44 shaders
		[@"opaque\tex_bump_env_illum_combined"] = new()
		{
			Passes = ["default_texture_zwrite", "lightmap_env", "default_self_illumination"],
			Base = 2, Detail = 3, Bump = 0, EnvMap = 4, SelfIllum = 5,
			BaseUv = UvTransform.Xform( 1 ), DetailUv = UvTransform.Xform( 0 ), BumpUv = UvTransform.Xform( 2 ), SelfIllumUv = UvTransform.Xform( 5 ),
			AlbedoMultiply = 0, SpecularColor = 5, SpecularColorGrazing = 6, EnvTintPerpendicular = 1, EnvTintParallel = 2, EnvBrightnessPerpendicular = 3, EnvBrightnessParallel = 4, SelfIllumColor = 7,
		},
		// 39 shaders
		[@"opaque\tex_bump_detail_blend"] = new()
		{
			Passes = ["detail_blend_texture_zwrite", "old_lightmap_tangentspace"],
			Base = 1, Detail = 2, DetailB = 3, Bump = 0,
			BaseUv = UvTransform.Xform( 2 ), DetailUv = UvTransform.Xform( 0 ), DetailBUv = UvTransform.Xform( 1 ), BumpUv = UvTransform.Xform( 3 ),
			AlbedoMultiply = 0, AlbedoAdd = 1, SpecularColor = 2, SpecularColorGrazing = 3,
		},
		// 36 shaders
		[@"opaque\tex_bump_env_illum"] = new()
		{
			Passes = ["default_texture_zwrite", "lightmap_env", "default_self_illumination"],
			Base = 2, Detail = 3, Bump = 0, EnvMap = 4, SelfIllum = 5,
			BaseUv = UvTransform.Xform( 1 ), DetailUv = UvTransform.Xform( 0 ), BumpUv = UvTransform.Xform( 2 ), SelfIllumUv = UvTransform.Xform( 5 ),
			AlbedoMultiply = 0, SpecularColor = 5, SpecularColorGrazing = 6, EnvTintPerpendicular = 1, EnvTintParallel = 2, EnvBrightnessPerpendicular = 3, EnvBrightnessParallel = 4, SelfIllumColor = 7,
		},
		// 31 shaders
		[@"opaque\tex_bump_detail_blend_detail"] = new()
		{
			Passes = ["lightmap_tex_detail_blend_detail"],
			Base = 1, Detail = 2, DetailB = 3, DetailC = 4, Bump = 0,
			BaseUv = UvTransform.Xform( 1 ), DetailUv = UvTransform.Xform( 2 ), DetailBUv = UvTransform.ScaleXY( 3 ), DetailCUv = UvTransform.ScaleZW( 4 ), BumpUv = UvTransform.Xform( 0 ),
			AlbedoMultiply = 0, AlbedoAdd = 1, SpecularColor = 2, SpecularColorGrazing = 3,
		},
		// 31 shaders
		[@"opaque\tex_bump_env_illum_3_channel"] = new()
		{
			Passes = ["lightmap_tex_detail", "multichannel_self_illumination", "default_environment_map_tangent_space"],
			Base = 1, Detail = 2, Bump = 0, EnvMap = 3, SelfIllum = 4,
			BaseUv = UvTransform.Xform( 1 ), DetailUv = UvTransform.Xform( 2 ), BumpUv = UvTransform.Xform( 0 ), SelfIllumUv = UvTransform.Xform( 11 ),
			SpecularColor = 0, SpecularColorGrazing = 1, SheenWidth = 2, SheenColor = 3, ChannelRIntensity = 4, ChannelGIntensity = 5, ChannelBIntensity = 9, ChannelRColor = 6, ChannelGColor = 7, ChannelBColor = 8,
			EnvLayerPerpendicularColor = 7, EnvLayerPerpendicularBrightness = 9, EnvLayerParallelColor = 8, EnvLayerParallelBrightness = 10,
		},
		// 30 shaders
		[@"opaque\illum"] = new()
		{
			Passes = ["default_self_illumination_single_pass"],
			SelfIllum = 0,
			BaseUv = UvTransform.Components( 1, 2, 3, 4 ), SelfIllumUv = UvTransform.Xform( 0 ),
			SelfIllumColor = 0,
		},
		// 25 shaders
		[@"opaque\tex_alpha_test"] = new()
		{
			Passes = ["lightmap_tex_detail_alpha_test_no_bump"],
			Base = 1, Detail = 2,
			BaseUv = UvTransform.Xform( 0 ), DetailUv = UvTransform.Xform( 1 ),
		},
		// 25 shaders
		[@"opaque\tex_bump_one_change_color"] = new()
		{
			Passes = ["lightmap_tex_one_change_color"],
			Base = 1, Bump = 0, ChangeColorMask = 2,
			BaseUv = UvTransform.Xform( 1 ), BumpUv = UvTransform.Xform( 0 ), ChangeColorMaskUv = UvTransform.Xform( 2 ),
			SpecularColor = 0,
		},
		// 23 shaders
		[@"opaque\tex_bump_env_illum_3_channel_combined"] = new()
		{
			Passes = ["lightmap_tex_env_detail", "multichannel_self_illumination"],
			Base = 1, Detail = 2, Bump = 0, EnvMap = 3, SelfIllum = 4,
			BaseUv = UvTransform.Xform( 1 ), DetailUv = UvTransform.Xform( 2 ), BumpUv = UvTransform.Xform( 0 ), SelfIllumUv = UvTransform.Xform( 5 ),
			SpecularColor = 4, SpecularColorGrazing = 5, EnvTintPerpendicular = 0, EnvTintParallel = 1, EnvBrightnessPerpendicular = 2, EnvBrightnessParallel = 3, ChannelRIntensity = 6, ChannelGIntensity = 7, ChannelBIntensity = 11, ChannelRColor = 8, ChannelGColor = 9, ChannelBColor = 10,
		},
		// 19 shaders
		[@"opaque\tex_bump_alpha_test"] = new()
		{
			Passes = ["default_texture_zwrite_alphatest", "old_lightmap_tangentspace"],
			Base = 3, Detail = 4, Bump = 0, AlphaMaskA = 0, AlphaMask = 1,
			BaseUv = UvTransform.Xform( 2 ), DetailUv = UvTransform.Xform( 0 ), BumpUv = UvTransform.Xform( 4 ), AlphaMaskAUv = UvTransform.ScaleXY( 1 ), AlphaMaskUv = UvTransform.ScaleZW( 3 ),
			SpecularColor = 0, SpecularColorGrazing = 1,
		},
		// 19 shaders
		[@"opaque\tex_bump_illum_3_channel"] = new()
		{
			Passes = ["lightmap_tex", "multichannel_self_illumination_occlusion"],
			Base = 1, Bump = 0, SelfIllum = 3, OcclusionMask = 4,
			BaseUv = UvTransform.Xform( 1 ), BumpUv = UvTransform.Xform( 0 ), SelfIllumUv = UvTransform.Xform( 4 ), OcclusionMaskUv = UvTransform.Xform( 5 ),
			SpecularColor = 0, SpecularColorGrazing = 1, ChannelRIntensity = 2, ChannelGIntensity = 3, ChannelBIntensity = 5, ChannelRColor = 4, ChannelGColor = 6, ChannelBColor = 7, OcclusionTintR = 8, OcclusionTintG = 9, OcclusionTintB = 10,
		},
		// 16 shaders
		[@"opaque\tex_bump_illum_bloom"] = new()
		{
			Passes = ["lightmap_tex_detail", "default_self_illumination_scroll"],
			Base = 1, Detail = 2, Bump = 0, SelfIllum = 3,
			BaseUv = UvTransform.Xform( 1 ), DetailUv = UvTransform.Xform( 2 ), BumpUv = UvTransform.Xform( 0 ), SelfIllumUv = UvTransform.Xform( 7 ),
			SpecularColor = 0, SpecularColorGrazing = 1, SelfIllumColor = 2,
		},
		// 15 shaders
		[@"opaque\tex_bump_detail_keep"] = new()
		{
			Passes = ["lightmap_tex_detail"],
			Base = 1, Detail = 2, Bump = 0,
			BaseUv = UvTransform.Xform( 1 ), DetailUv = UvTransform.Xform( 2 ), BumpUv = UvTransform.Xform( 0 ),
			SpecularColor = 0, SpecularColorGrazing = 1,
		},
		// 15 shaders
		[@"opaque\tex_bump_env_fast_masked"] = new()
		{
			Passes = ["lightmap_tex_detail", "default_environment_map_tangent_space_fast_masked"],
			Base = 1, Detail = 2, Bump = 0, EnvMap = 3, EnvMask = 4,
			BaseUv = UvTransform.Xform( 1 ), DetailUv = UvTransform.Xform( 2 ), BumpUv = UvTransform.Xform( 0 ), EnvMaskUv = UvTransform.Xform( 11 ),
			SpecularColor = 0, SpecularColorGrazing = 1,
			EnvLayerPerpendicularColor = 7, EnvLayerPerpendicularBrightness = 9, EnvLayerParallelColor = 8, EnvLayerParallelBrightness = 10,
		},
		// 15 shaders
		[@"opaque\tex_bump_three_detail_blend"] = new()
		{
			Passes = ["lightmap_tex_three_detail_blend"],
			Base = 1, Detail = 2, DetailB = 3, DetailC = 4, Bump = 0,
			BaseUv = UvTransform.Xform( 1 ), DetailUv = UvTransform.Xform( 2 ), DetailBUv = UvTransform.ScaleXY( 3 ), DetailCUv = UvTransform.ScaleZW( 4 ), BumpUv = UvTransform.Xform( 0 ),
			SpecularColor = 0,
		},
		// 14 shaders
		[@"opaque\tex_bump_env_alpha_test"] = new()
		{
			Passes = ["lightmap_tex_env_detail_alpha_test"],
			Base = 3, Detail = 4, Bump = 0, AlphaMask = 1, EnvMap = 5,
			BaseUv = UvTransform.Xform( 1 ), DetailUv = UvTransform.Xform( 2 ), BumpUv = UvTransform.Xform( 0 ), AlphaMaskUv = UvTransform.ScaleXY( 3 ),
			SpecularColor = 4, SpecularColorGrazing = 5, EnvTintPerpendicular = 0, EnvTintParallel = 1, EnvBrightnessPerpendicular = 2, EnvBrightnessParallel = 3,
		},
		// 14 shaders
		[@"opaque\tex_bump_plasma_one_channel_illum"] = new()
		{
			Passes = ["lightmap_tex_detail", "plasma_one_channel_self_illumination"],
			Base = 1, Detail = 2, Bump = 0, SelfIllum = 3,
			BaseUv = UvTransform.Xform( 1 ), DetailUv = UvTransform.Xform( 2 ), BumpUv = UvTransform.Xform( 0 ), SelfIllumUv = UvTransform.Xform( 7 ),
			SpecularColor = 0, SpecularColorGrazing = 1, ChannelGColor = 3, ChannelBColor = 4, PlasmaThreshold = 2, PlasmaBandColor = 5,
		},
		// 13 shaders
		[@"opaque\tex_env"] = new()
		{
			Passes = ["lightmap_tex_env_no_bump"],
			Base = 0, Detail = 1, EnvMap = 2,
			BaseUv = UvTransform.Xform( 1 ), DetailUv = UvTransform.Xform( 0 ),
			SpecularColor = 4, EnvTintPerpendicular = 0, EnvTintParallel = 1, EnvBrightnessPerpendicular = 2, EnvBrightnessParallel = 3,
		},
		// 12 shaders
		[@"opaque\prt_lightmap"] = new()
		{
			Passes = ["prt_lightmap"],
			Base = 1, Detail = 2, Bump = 0, EnvMap = 3,
			BaseUv = UvTransform.Xform( 0 ), DetailUv = UvTransform.Xform( 3 ), BumpUv = UvTransform.Xform( 1 ),
			SpecularColor = 0,
			PrtScale = 2,
		},
		// 12 shaders
		[@"opaque\tex_bump_env_alpha_test_combined"] = new()
		{
			Passes = ["lightmap_tex_env_detail_alpha_test"],
			Base = 2, Detail = 3, Bump = 0, AlphaMask = 1, EnvMap = 4,
			BaseUv = UvTransform.Xform( 1 ), DetailUv = UvTransform.Xform( 2 ), BumpUv = UvTransform.Xform( 0 ), AlphaMaskUv = UvTransform.ScaleXY( 3 ),
			SpecularColor = 4, SpecularColorGrazing = 5, EnvTintPerpendicular = 0, EnvTintParallel = 1, EnvBrightnessPerpendicular = 2, EnvBrightnessParallel = 3,
		},
		// 11 shaders
		[@"opaque\illum_bloom"] = new()
		{
			Passes = ["self_illumination_single_pass_detail"],
			SelfIllum = 0, SelfIllumB = 1, SelfIllumDetail = 2,
			BaseUv = UvTransform.Components( 1, 2, -1, -1 ), SelfIllumUv = UvTransform.Xform( 0 ), SelfIllumBUv = UvTransform.Xform( 3 ), SelfIllumDetailUv = UvTransform.Xform( 4 ),
			SelfIllumColor = 0,
		},
		// 11 shaders
		[@"opaque\tex_bump_dprs_env"] = new()
		{
			Passes = ["lightmap_tex_detail", "default_environment_map_tangent_space"],
			Base = 2, Detail = 3, Bump = 0, EnvMap = 4,
			BaseUv = UvTransform.Xform( 1 ), DetailUv = UvTransform.Xform( 2 ), BumpUv = UvTransform.Xform( 0 ),
			SpecularColor = 0, SpecularColorGrazing = 1, SheenWidth = 2, SheenColor = 3,
			EnvLayerPerpendicularColor = 7, EnvLayerPerpendicularBrightness = 9, EnvLayerParallelColor = 8, EnvLayerParallelBrightness = 10,
		},
		// 10 shaders
		[@"opaque\illum_bloom_opaque"] = new()
		{
			Passes = ["self_illumination_single_pass_detail_zwrite"],
			SelfIllum = 0, SelfIllumB = 1, SelfIllumDetail = 2,
			BaseUv = UvTransform.Components( 1, 2, -1, -1 ), SelfIllumUv = UvTransform.Xform( 0 ), SelfIllumBUv = UvTransform.Xform( 3 ), SelfIllumDetailUv = UvTransform.Xform( 4 ),
			SelfIllumColor = 0,
		},
		// 10 shaders
		[@"opaque\tex_bump_env_clamped"] = new()
		{
			Passes = ["default_texture_clamped_zwrite", "lightmap_env"],
			Base = 2, Detail = 3, Bump = 0, EnvMap = 4,
			BaseUv = UvTransform.Xform( 1 ), DetailUv = UvTransform.Xform( 0 ), BumpUv = UvTransform.Xform( 2 ),
			AlbedoMultiply = 0, SpecularColor = 5, SpecularColorGrazing = 6, EnvTintPerpendicular = 1, EnvTintParallel = 2, EnvBrightnessPerpendicular = 3, EnvBrightnessParallel = 4,
		},
		// 10 shaders
		[@"opaque\tex_bump_no_alpha"] = new()
		{
			Passes = ["lightmap_tex_detail_no_alpha"],
			Base = 2, Detail = 3, Bump = 0,
			BaseUv = UvTransform.Xform( 1 ), DetailUv = UvTransform.Xform( 2 ), BumpUv = UvTransform.Xform( 0 ),
			SpecularColor = 0, SpecularColorGrazing = 1,
		},
		// 9 shaders
		[@"opaque\tex_bump_active_camo"] = new()
		{
			Passes = ["lightmap_tex_detail"],
			Base = 2, Detail = 3, Bump = 0,
			BaseUv = UvTransform.Xform( 1 ), DetailUv = UvTransform.Xform( 2 ), BumpUv = UvTransform.Xform( 0 ),
			SpecularColor = 0, SpecularColorGrazing = 1,
		},
		// 9 shaders
		[@"opaque\tex_bump_alpha_test_clamped_single_pass"] = new()
		{
			Passes = ["lightmap_tex_detail_alpha_test_clamped"],
			Base = 2, Detail = 3, Bump = 0, AlphaMask = 4,
			BaseUv = UvTransform.Xform( 1 ), DetailUv = UvTransform.Xform( 2 ), BumpUv = UvTransform.Xform( 0 ), AlphaMaskUv = UvTransform.ScaleXY( 3 ),
			SpecularColor = 0, SpecularColorGrazing = 1,
		},
		// 9 shaders
		[@"opaque\tex_bump_env_two_detail_combined"] = new()
		{
			Passes = ["lightmap_tex_two_detail", "default_environment_map_tangent_space"],
			Base = 1, Detail = 2, DetailC = 3, Bump = 0, EnvMap = 4,
			BaseUv = UvTransform.Xform( 1 ), DetailUv = UvTransform.Xform( 2 ), DetailCUv = UvTransform.ScaleXY( 3 ), BumpUv = UvTransform.Xform( 0 ),
			AlbedoMultiply = 0, AlbedoAdd = 1, SpecularColor = 2, SpecularColorGrazing = 3, SheenWidth = 4, SheenColor = 5,
			EnvLayerPerpendicularColor = 6, EnvLayerPerpendicularBrightness = 8, EnvLayerParallelColor = 7, EnvLayerParallelBrightness = 9,
		},
		// 8 shaders
		[@"opaque\tex_bump_detail_overlay"] = new()
		{
			Passes = ["lightmap_tex_detail_overlay"],
			Base = 1, Detail = 2, DetailC = 3, Bump = 0,
			BaseUv = UvTransform.Xform( 1 ), DetailUv = UvTransform.Xform( 2 ), DetailCUv = UvTransform.ScaleXY( 3 ), BumpUv = UvTransform.Xform( 0 ),
			AlbedoMultiply = 0, AlbedoAdd = 1, SpecularColorGrazing = 3,
		},
		// 8 shaders
		[@"opaque\tex_bump_env_two_detail"] = new()
		{
			Passes = ["lightmap_tex_two_detail", "default_environment_map_tangent_space"],
			Base = 1, Detail = 2, DetailC = 3, Bump = 0, EnvMap = 4,
			BaseUv = UvTransform.Xform( 1 ), DetailUv = UvTransform.Xform( 2 ), DetailCUv = UvTransform.ScaleXY( 3 ), BumpUv = UvTransform.Xform( 0 ),
			AlbedoMultiply = 0, AlbedoAdd = 1, SpecularColor = 2, SpecularColorGrazing = 3, SheenWidth = 4, SheenColor = 5,
			EnvLayerPerpendicularColor = 6, EnvLayerPerpendicularBrightness = 8, EnvLayerParallelColor = 7, EnvLayerParallelBrightness = 9,
		},
		// 8 shaders
		[@"opaque\tex_bump_four_change_color"] = new()
		{
			Passes = ["lightmap_tex_four_change_color"],
			Base = 1, Detail = 2, Bump = 0, ChangeColorMask = 3,
			BaseUv = UvTransform.Xform( 1 ), DetailUv = UvTransform.Xform( 2 ), BumpUv = UvTransform.Xform( 0 ), ChangeColorMaskUv = UvTransform.ScaleXY( 3 ),
			SpecularColor = 0, SpecularColorGrazing = 1,
		},
		// 7 shaders
		[@"opaque\tex_bump_env_illum_detail_honor_guard"] = new()
		{
			Passes = ["lightmap_honor_guard", "default_self_illumination_detail", "default_environment_map_tangent_space"],
			Base = 1, Detail = 2, Bump = 0, EnvMap = 5, SelfIllum = 3, SelfIllumDetail = 4,
			BaseUv = UvTransform.Xform( 1 ), DetailUv = UvTransform.Xform( 2 ), BumpUv = UvTransform.Xform( 0 ), SelfIllumUv = UvTransform.Xform( 12 ), SelfIllumDetailUv = UvTransform.ScaleXY( 13 ),
			SpecularColor = 1, SpecularColorGrazing = 2, SheenWidth = 3, SheenColor = 4, SelfIllumColor = 5, SelfIllumDetailStrength = 6,
			EnvLayerPerpendicularColor = 6, EnvLayerPerpendicularBrightness = 8, EnvLayerParallelColor = 7, EnvLayerParallelBrightness = 9,
		},
		// 7 shaders
		[@"opaque\tex_bump_two_detail"] = new()
		{
			Passes = ["lightmap_tex_two_detail"],
			Base = 1, Detail = 2, DetailC = 3, Bump = 0,
			BaseUv = UvTransform.Xform( 1 ), DetailUv = UvTransform.Xform( 2 ), DetailCUv = UvTransform.ScaleXY( 3 ), BumpUv = UvTransform.Xform( 0 ),
			AlbedoMultiply = 0, AlbedoAdd = 1, SpecularColor = 2, SpecularColorGrazing = 3,
		},
		// 6 shaders
		[@"opaque\illum_bloom_masked"] = new()
		{
			Passes = ["self_illumination_single_pass_detail_masked"],
			SelfIllum = 0, SelfIllumDetail = 1, SelfIllumDetailB = 2,
			BaseUv = UvTransform.Components( 1, 2, -1, -1 ), SelfIllumUv = UvTransform.Xform( 0 ), SelfIllumDetailUv = UvTransform.Xform( 3 ), SelfIllumDetailBUv = UvTransform.Xform( 4 ),
			SelfIllumColor = 0,
		},
		// 6 shaders
		[@"opaque\prt_simple_lm_emissive"] = new()
		{
			Passes = ["prt_lightmap", "default_self_illumination"],
			Base = 1, Detail = 2, Bump = 0, EnvMap = 4, SelfIllum = 3,
			BaseUv = UvTransform.Xform( 0 ), DetailUv = UvTransform.Xform( 3 ), BumpUv = UvTransform.Xform( 1 ), SelfIllumUv = UvTransform.Xform( 5 ),
			SpecularColor = 0, SelfIllumColor = 1,
			PrtScale = 2,
		},
		// 6 shaders
		[@"opaque\tex_bump_illum_alpha_test"] = new()
		{
			Passes = ["lightmap_tex_illum_alpha_test"],
			Base = 3, Bump = 0, AlphaMask = 1, SelfIllum = 5,
			BaseUv = UvTransform.Xform( 1 ), BumpUv = UvTransform.Xform( 0 ), AlphaMaskUv = UvTransform.ScaleXY( 3 ), SelfIllumUv = UvTransform.Xform( 2 ),
			SpecularColor = 0, SpecularColorGrazing = 2, SelfIllumColor = 1,
		},
		// 5 shaders
		[@"opaque\prt_scarab"] = new()
		{
			Passes = ["prt_scarab"],
			Base = 1, Detail = 2, Bump = 0, EnvMap = 3,
			BaseUv = UvTransform.Xform( 0 ), DetailUv = UvTransform.Xform( 3 ), BumpUv = UvTransform.Xform( 1 ),
			SpecularColor = 0, SpecularColorGrazing = 1,
			PrtScale = 2,
		},
		// 5 shaders
		[@"opaque\tex_bump_env_detail_blend_specular"] = new()
		{
			Passes = ["lightmap_tex_detail_blend_specular", "default_environment_map_tangent_space_fast"],
			Base = 1, Detail = 2, DetailB = 3, Bump = 0, SpecularMask = 4, EnvMap = 5,
			BaseUv = UvTransform.Xform( 1 ), DetailUv = UvTransform.Xform( 2 ), DetailBUv = UvTransform.ScaleXY( 3 ), BumpUv = UvTransform.Xform( 0 ), SpecularMaskUv = UvTransform.ScaleZW( 4 ),
			AlbedoMultiply = 0, AlbedoAdd = 1, SpecularColor = 2, SpecularColorGrazing = 3,
			EnvLayerPerpendicularColor = 5, EnvLayerPerpendicularBrightness = 7, EnvLayerParallelColor = 6, EnvLayerParallelBrightness = 8,
		},
		// 5 shaders
		[@"opaque\tex_bump_env_two_change_color"] = new()
		{
			Passes = ["lightmap_tex_two_change_color", "default_environment_map_tangent_space"],
			Base = 2, Detail = 3, Bump = 0, EnvMap = 5, ChangeColorMask = 4,
			BaseUv = UvTransform.Xform( 1 ), DetailUv = UvTransform.Xform( 2 ), BumpUv = UvTransform.Xform( 0 ), ChangeColorMaskUv = UvTransform.ScaleXY( 3 ),
			SpecularColor = 0, SpecularColorGrazing = 1, SheenWidth = 2, SheenColor = 3,
			EnvLayerPerpendicularColor = 4, EnvLayerPerpendicularBrightness = 6, EnvLayerParallelColor = 5, EnvLayerParallelBrightness = 7,
		},
		// 4 shaders
		[@"opaque\active_camo_opaque"] = new()
		{
			Passes = [],
		},
		// 4 shaders
		[@"opaque\decal_simple"] = new()
		{
			Passes = [],
		},
		// 4 shaders
		[@"opaque\illum_3_channel"] = new()
		{
			Passes = ["multichannel_self_illumination_single_pass"],
			SelfIllum = 0,
			BaseUv = UvTransform.Xform( 1 ), SelfIllumUv = UvTransform.Xform( 0 ),
			ChannelRIntensity = 0, ChannelGIntensity = 1, ChannelBIntensity = 5, ChannelRColor = 2, ChannelGColor = 3, ChannelBColor = 4,
		},
		// 4 shaders
		[@"opaque\illum_3_channel_opaque"] = new()
		{
			Passes = ["multichannel_self_illumination_zwrite"],
			SelfIllum = 0,
			BaseUv = UvTransform.Xform( 1 ), SelfIllumUv = UvTransform.Xform( 0 ),
			ChannelRIntensity = 0, ChannelGIntensity = 1, ChannelBIntensity = 5, ChannelRColor = 2, ChannelGColor = 3, ChannelBColor = 4,
		},
		// 4 shaders
		[@"opaque\tex_bump_env_dbl_spec"] = new()
		{
			Passes = ["lightmap_tex_env_detail_dbl_spec"],
			Base = 2, Detail = 3, Bump = 0, EnvMap = 4,
			BaseUv = UvTransform.Xform( 1 ), DetailUv = UvTransform.Xform( 2 ), BumpUv = UvTransform.Xform( 0 ),
			SpecularColor = 4, SpecularColorGrazing = 5, EnvTintPerpendicular = 0, EnvTintParallel = 1, EnvBrightnessPerpendicular = 2, EnvBrightnessParallel = 3,
		},
		// 4 shaders
		[@"opaque\tex_bump_env_illum_two_change_color"] = new()
		{
			Passes = ["lightmap_tex_two_change_color", "default_self_illumination", "default_environment_map_tangent_space"],
			Base = 2, Detail = 3, Bump = 0, EnvMap = 5, SelfIllum = 6, ChangeColorMask = 4,
			BaseUv = UvTransform.Xform( 1 ), DetailUv = UvTransform.Xform( 2 ), BumpUv = UvTransform.Xform( 0 ), SelfIllumUv = UvTransform.Xform( 8 ), ChangeColorMaskUv = UvTransform.ScaleXY( 3 ),
			SpecularColor = 0, SpecularColorGrazing = 1, SheenWidth = 2, SheenColor = 3, SelfIllumColor = 4,
			EnvLayerPerpendicularColor = 4, EnvLayerPerpendicularBrightness = 6, EnvLayerParallelColor = 5, EnvLayerParallelBrightness = 7,
		},
		// 3 shaders
		[@"opaque\tex_bump_detail_blend_specular"] = new()
		{
			Passes = ["lightmap_tex_detail_blend_specular"],
			Base = 1, Detail = 2, DetailB = 3, Bump = 0, SpecularMask = 4,
			BaseUv = UvTransform.Xform( 1 ), DetailUv = UvTransform.Xform( 4 ), DetailBUv = UvTransform.ScaleXY( 5 ), BumpUv = UvTransform.Xform( 0 ), SpecularMaskUv = UvTransform.ScaleZW( 6 ),
			AlbedoMultiply = 2, AlbedoAdd = 3, SpecularColor = 0, SpecularColorGrazing = 1,
		},
		// 3 shaders
		[@"opaque\tex_bump_detail_keep_blend"] = new()
		{
			Passes = ["lightmap_tex_detail_blend"],
			Base = 1, Detail = 2, DetailB = 3, Bump = 0,
			BaseUv = UvTransform.Xform( 1 ), DetailUv = UvTransform.Xform( 2 ), DetailBUv = UvTransform.ScaleXY( 3 ), BumpUv = UvTransform.Xform( 0 ),
			AlbedoMultiply = 0, AlbedoAdd = 1, SpecularColor = 2, SpecularColorGrazing = 3,
		},
		// 3 shaders
		[@"opaque\tex_bump_env_clamped_combined"] = new()
		{
			Passes = ["default_texture_clamped_zwrite", "lightmap_env"],
			Base = 1, Detail = 2, Bump = 0, EnvMap = 3,
			BaseUv = UvTransform.Xform( 1 ), DetailUv = UvTransform.Xform( 0 ), BumpUv = UvTransform.Xform( 2 ),
			AlbedoMultiply = 0, SpecularColor = 5, SpecularColorGrazing = 6, EnvTintPerpendicular = 1, EnvTintParallel = 2, EnvBrightnessPerpendicular = 3, EnvBrightnessParallel = 4,
		},
		// 3 shaders
		[@"opaque\tex_bump_env_detail_overlay"] = new()
		{
			Passes = ["lightmap_tex_detail_overlay", "default_environment_map_tangent_space_fast"],
			Base = 1, Detail = 2, DetailC = 3, Bump = 0, EnvMap = 4,
			BaseUv = UvTransform.Xform( 1 ), DetailUv = UvTransform.Xform( 2 ), DetailCUv = UvTransform.ScaleXY( 3 ), BumpUv = UvTransform.Xform( 0 ),
			AlbedoMultiply = 0, AlbedoAdd = 1, SpecularColorGrazing = 3,
			EnvLayerPerpendicularColor = 6, EnvLayerPerpendicularBrightness = 8, EnvLayerParallelColor = 7, EnvLayerParallelBrightness = 9,
		},
		// 3 shaders
		[@"opaque\tex_bump_env_four_change_color"] = new()
		{
			Passes = ["lightmap_tex_four_change_color", "default_environment_map_tangent_space"],
			Base = 2, Detail = 3, Bump = 0, EnvMap = 5, ChangeColorMask = 4,
			BaseUv = UvTransform.Xform( 1 ), DetailUv = UvTransform.Xform( 2 ), BumpUv = UvTransform.Xform( 0 ), ChangeColorMaskUv = UvTransform.ScaleXY( 3 ),
			SpecularColor = 0, SpecularColorGrazing = 1, SheenWidth = 2, SheenColor = 3,
			EnvLayerPerpendicularColor = 4, EnvLayerPerpendicularBrightness = 6, EnvLayerParallelColor = 5, EnvLayerParallelBrightness = 7,
		},
		// 3 shaders
		[@"opaque\tex_bump_env_illum_3_channel_occlusion_combined"] = new()
		{
			Passes = ["lightmap_tex_detail", "multichannel_self_illumination_occlusion", "default_environment_map_tangent_space"],
			Base = 1, Detail = 2, Bump = 0, EnvMap = 3, SelfIllum = 4, OcclusionMask = 5,
			BaseUv = UvTransform.Xform( 1 ), DetailUv = UvTransform.Xform( 2 ), BumpUv = UvTransform.Xform( 0 ), SelfIllumUv = UvTransform.Xform( 11 ), OcclusionMaskUv = UvTransform.Xform( 12 ),
			SpecularColor = 0, SpecularColorGrazing = 1, SheenWidth = 2, SheenColor = 3, ChannelRIntensity = 4, ChannelGIntensity = 5, ChannelBIntensity = 7, ChannelRColor = 6, ChannelGColor = 8, ChannelBColor = 9, OcclusionTintR = 10, OcclusionTintG = 11, OcclusionTintB = 12,
			EnvLayerPerpendicularColor = 7, EnvLayerPerpendicularBrightness = 9, EnvLayerParallelColor = 8, EnvLayerParallelBrightness = 10,
		},
		// 3 shaders
		[@"opaque\tex_bump_env_illum_two_change_color_combined"] = new()
		{
			Passes = ["lightmap_tex_two_change_color", "default_self_illumination", "default_environment_map_tangent_space_fast"],
			Base = 1, Detail = 2, Bump = 0, EnvMap = 4, SelfIllum = 5, ChangeColorMask = 3,
			BaseUv = UvTransform.Xform( 1 ), DetailUv = UvTransform.Xform( 2 ), BumpUv = UvTransform.Xform( 0 ), SelfIllumUv = UvTransform.Xform( 4 ), ChangeColorMaskUv = UvTransform.ScaleXY( 3 ),
			SpecularColor = 0, SpecularColorGrazing = 1, SelfIllumColor = 2,
			EnvLayerPerpendicularColor = 5, EnvLayerPerpendicularBrightness = 7, EnvLayerParallelColor = 6, EnvLayerParallelBrightness = 8,
		},
		// 3 shaders
		[@"opaque\tex_bump_env_two_change_color_combined"] = new()
		{
			Passes = ["lightmap_tex_two_change_color", "default_environment_map_tangent_space_fast"],
			Base = 1, Detail = 2, Bump = 0, EnvMap = 4, ChangeColorMask = 3,
			BaseUv = UvTransform.Xform( 1 ), DetailUv = UvTransform.Xform( 2 ), BumpUv = UvTransform.Xform( 0 ), ChangeColorMaskUv = UvTransform.ScaleXY( 3 ),
			SpecularColor = 0, SpecularColorGrazing = 1,
			EnvLayerPerpendicularColor = 4, EnvLayerPerpendicularBrightness = 6, EnvLayerParallelColor = 5, EnvLayerParallelBrightness = 7,
		},
		// 3 shaders
		[@"opaque\tex_bump_illum_alpha_test_illum"] = new()
		{
			Passes = ["lightmap_tex_illum_alpha_test"],
			Base = 3, Bump = 0, AlphaMask = 1, SelfIllum = 5,
			BaseUv = UvTransform.Xform( 1 ), BumpUv = UvTransform.Xform( 0 ), AlphaMaskUv = UvTransform.ScaleXY( 3 ), SelfIllumUv = UvTransform.Xform( 2 ),
			SpecularColor = 0, SpecularColorGrazing = 2, SelfIllumColor = 1,
		},
		// 3 shaders
		[@"opaque\tex_bump_illum_no_specular"] = new()
		{
			Passes = ["lightmap_tex_detail_no_specular", "default_self_illumination"],
			Base = 1, Detail = 2, Bump = 0, SelfIllum = 3,
			BaseUv = UvTransform.Xform( 1 ), DetailUv = UvTransform.Xform( 2 ), BumpUv = UvTransform.Xform( 0 ), SelfIllumUv = UvTransform.Xform( 7 ),
			SelfIllumColor = 0,
		},
		// 3 shaders
		[@"opaque\tex_detail_blend"] = new()
		{
			Passes = ["lightmap_tex_detail_blend"],
			Base = 0, Detail = 1, DetailB = 2, Bump = 3,
			BaseUv = UvTransform.Xform( 1 ), DetailUv = UvTransform.Xform( 2 ), DetailBUv = UvTransform.ScaleXY( 3 ), BumpUv = UvTransform.Xform( 0 ),
			AlbedoMultiply = 0, AlbedoAdd = 1, SpecularColor = 2, SpecularColorGrazing = 3,
		},
		// 2 shaders
		[@"opaque\emblem_overlay_simple"] = new()
		{
			Passes = [],
		},
		// 2 shaders
		[@"opaque\illum_detail"] = new()
		{
			Passes = ["default_self_illumination_detail_single_pass"],
			SelfIllum = 0, SelfIllumDetail = 1,
			BaseUv = UvTransform.Components( 1, 2, 4, 4 ), SelfIllumUv = UvTransform.Xform( 0 ), SelfIllumDetailUv = UvTransform.Xform( 3 ),
			SelfIllumColor = 0,
		},
		// 2 shaders
		[@"opaque\illum_opaque"] = new()
		{
			Passes = ["default_self_illumination_single_pass_zwrite"],
			SelfIllum = 0,
			BaseUv = UvTransform.Components( 1, 2, 3, 4 ), SelfIllumUv = UvTransform.Xform( 0 ),
			SelfIllumColor = 0,
		},
		// 2 shaders
		[@"opaque\tex_bump_dprs_env_illum"] = new()
		{
			Passes = ["lightmap_tex_detail", "default_self_illumination", "default_environment_map_tangent_space"],
			Base = 1, Detail = 2, Bump = 0, EnvMap = 3, SelfIllum = 4,
			BaseUv = UvTransform.Xform( 1 ), DetailUv = UvTransform.Xform( 2 ), BumpUv = UvTransform.Xform( 0 ), SelfIllumUv = UvTransform.Xform( 7 ),
			SpecularColor = 0, SpecularColorGrazing = 1, SheenWidth = 3, SheenColor = 4, SelfIllumColor = 2,
			EnvLayerPerpendicularColor = 8, EnvLayerPerpendicularBrightness = 10, EnvLayerParallelColor = 9, EnvLayerParallelBrightness = 11,
		},
		// 2 shaders
		[@"opaque\tex_bump_env_illum_four_change_color_no_lod"] = new()
		{
			Passes = ["lightmap_tex_four_change_color", "default_self_illumination", "default_environment_map_tangent_space"],
			Base = 2, Detail = 3, Bump = 0, EnvMap = 6, SelfIllum = 5, ChangeColorMask = 4,
			BaseUv = UvTransform.Xform( 1 ), DetailUv = UvTransform.Xform( 2 ), BumpUv = UvTransform.Xform( 0 ), SelfIllumUv = UvTransform.Xform( 11 ), ChangeColorMaskUv = UvTransform.ScaleXY( 3 ),
			SpecularColor = 0, SpecularColorGrazing = 1, SheenWidth = 2, SheenColor = 3, SelfIllumColor = 4,
			EnvLayerPerpendicularColor = 4, EnvLayerPerpendicularBrightness = 6, EnvLayerParallelColor = 5, EnvLayerParallelBrightness = 7,
		},
		// 2 shaders
		[@"opaque\tex_bump_env_tiling_specular"] = new()
		{
			Passes = ["lightmap_tex_tiling", "default_environment_map_tangent_space"],
			Base = 1, Detail = 2, Bump = 0, SpecularMask = 4, EnvMap = 5,
			BaseUv = UvTransform.Xform( 1 ), DetailUv = UvTransform.Xform( 2 ), BumpUv = UvTransform.Xform( 0 ), SpecularMaskUv = UvTransform.ScaleXY( 3 ),
			AlbedoMultiply = 0, SpecularColor = 1, SpecularColorGrazing = 2, SheenWidth = 3, SheenColor = 4,
			EnvLayerPerpendicularColor = 6, EnvLayerPerpendicularBrightness = 8, EnvLayerParallelColor = 7, EnvLayerParallelBrightness = 9,
		},
		// 2 shaders
		[@"opaque\tex_bump_env_two_change_color_indexed"] = new()
		{
			Passes = ["lightmap_tex_two_change_color", "default_environment_map_tangent_space"],
			Base = 1, Detail = 2, Bump = 0, EnvMap = 4, ChangeColorMask = 3,
			BaseUv = UvTransform.Xform( 1 ), DetailUv = UvTransform.Xform( 2 ), BumpUv = UvTransform.Xform( 0 ), ChangeColorMaskUv = UvTransform.ScaleXY( 3 ),
			SpecularColor = 0, SpecularColorGrazing = 1, SheenWidth = 2, SheenColor = 3,
			EnvLayerPerpendicularColor = 4, EnvLayerPerpendicularBrightness = 6, EnvLayerParallelColor = 5, EnvLayerParallelBrightness = 7,
		},
		// 2 shaders
		[@"opaque\tex_bump_illum_detail_honor_guard"] = new()
		{
			Passes = ["lightmap_honor_guard", "default_self_illumination_detail"],
			Base = 1, Detail = 2, Bump = 0, SelfIllum = 3, SelfIllumDetail = 4,
			BaseUv = UvTransform.Xform( 1 ), DetailUv = UvTransform.Xform( 2 ), BumpUv = UvTransform.Xform( 0 ), SelfIllumUv = UvTransform.Xform( 6 ), SelfIllumDetailUv = UvTransform.ScaleXY( 7 ),
			SpecularColor = 1, SpecularColorGrazing = 2, SelfIllumColor = 3, SelfIllumDetailStrength = 4,
		},
		// 2 shaders
		[@"opaque\tex_bump_meter_illum"] = new()
		{
			Passes = ["lightmap_tex_detail", "meter_self_illumination"],
			Base = 1, Detail = 2, Bump = 0, SelfIllum = 3,
			BaseUv = UvTransform.Xform( 1 ), DetailUv = UvTransform.Xform( 2 ), BumpUv = UvTransform.Xform( 0 ), SelfIllumUv = UvTransform.Xform( 7 ),
			SpecularColor = 0, SpecularColorGrazing = 1, MeterValue = 2, MeterColorBelow = 3, MeterColorAbove = 4,
		},
		// 2 shaders
		[@"opaque\tex_bump_plasma"] = new()
		{
			Passes = ["lightmap_tex_detail", "plasma_alpha"],
			Base = 1, Detail = 2, Bump = 0,
			BaseUv = UvTransform.Xform( 1 ), DetailUv = UvTransform.Xform( 2 ), BumpUv = UvTransform.Xform( 0 ),
			SpecularColor = 0, SpecularColorGrazing = 1, PlasmaBandColor = 5,
			EnvLayerPerpendicularBrightness = 16, EnvLayerParallelBrightness = 15,
		},
		// 2 shaders
		[@"opaque\tex_bump_two_change_color"] = new()
		{
			Passes = ["lightmap_tex_two_change_color"],
			Base = 1, Detail = 2, Bump = 0, ChangeColorMask = 3,
			BaseUv = UvTransform.Xform( 1 ), DetailUv = UvTransform.Xform( 2 ), BumpUv = UvTransform.Xform( 0 ), ChangeColorMaskUv = UvTransform.ScaleXY( 3 ),
			SpecularColor = 0, SpecularColorGrazing = 1,
		},
		// 2 shaders
		[@"opaque\tex_env_3_channel_illum"] = new()
		{
			Passes = ["lightmap_tex_env_no_bump", "multichannel_self_illumination"],
			Base = 0, Detail = 1, EnvMap = 2, SelfIllum = 3,
			BaseUv = UvTransform.Xform( 1 ), DetailUv = UvTransform.Xform( 0 ), SelfIllumUv = UvTransform.Xform( 7 ),
			SpecularColor = 4, EnvTintPerpendicular = 0, EnvTintParallel = 1, EnvBrightnessPerpendicular = 2, EnvBrightnessParallel = 3, ChannelRIntensity = 6, ChannelGIntensity = 7, ChannelBIntensity = 11, ChannelRColor = 8, ChannelGColor = 9, ChannelBColor = 10,
		},
		// 2 shaders
		[@"opaque\tex_illum"] = new()
		{
			Passes = ["lightmap_tex_detail_illum_no_bump"],
			Base = 0, Detail = 1, SelfIllum = 2,
			BaseUv = UvTransform.Xform( 1 ), DetailUv = UvTransform.Xform( 2 ), SelfIllumUv = UvTransform.Xform( 0 ),
			SpecularColor = 1, SelfIllumColor = 0,
		},
		// 2 shaders
		[@"opaque\tex_illum_bloom"] = new()
		{
			Passes = ["lightmap_tex_detail_illum_no_bump"],
			Base = 0, Detail = 1, SelfIllum = 2,
			BaseUv = UvTransform.Xform( 1 ), DetailUv = UvTransform.Xform( 2 ), SelfIllumUv = UvTransform.Xform( 0 ),
			SpecularColor = 1, SelfIllumColor = 0,
		},
		// 1 shader
		[@"opaque\bloom"] = new()
		{
			Passes = [],
		},
		// 1 shader
		[@"opaque\emblem_flag"] = new()
		{
			Passes = ["lightmap_emblem_flag"],
			BaseUv = UvTransform.Xform( 0 ),
			SpecularColorGrazing = 0,
		},
		// 1 shader
		[@"opaque\illum_3_channel_plasma"] = new()
		{
			Passes = [],
		},
		// 1 shader
		[@"opaque\illum_bloom_3_channel_opaque"] = new()
		{
			Passes = ["multichannel_self_illumination_zwrite"],
			SelfIllum = 0,
			BaseUv = UvTransform.Xform( 1 ), SelfIllumUv = UvTransform.Xform( 0 ),
			ChannelRIntensity = 0, ChannelGIntensity = 1, ChannelBIntensity = 5, ChannelRColor = 2, ChannelGColor = 3, ChannelBColor = 4,
		},
		// 1 shader
		[@"opaque\illum_clamped"] = new()
		{
			Passes = ["default_self_illumination_single_pass_clamped"],
			SelfIllum = 0,
			BaseUv = UvTransform.Components( 1, 2, -1, -1 ), SelfIllumUv = UvTransform.Xform( 0 ),
			SelfIllumColor = 0,
		},
		// 1 shader
		[@"opaque\render_layer_disabled"] = new()
		{
			Passes = ["texaccum_disabled"],
		},
		// 1 shader
		[@"opaque\tex_bump_alpha_test_clamped"] = new()
		{
			Passes = ["lightmap_tex_detail_alpha_test_clamped"],
			Base = 2, Detail = 3, Bump = 0, AlphaMask = 4,
			BaseUv = UvTransform.Xform( 1 ), DetailUv = UvTransform.Xform( 2 ), BumpUv = UvTransform.Xform( 0 ), AlphaMaskUv = UvTransform.ScaleXY( 3 ),
			SpecularColor = 0, SpecularColorGrazing = 1,
		},
		// 1 shader
		[@"opaque\tex_bump_detail_blend_specular_dblmult"] = new()
		{
			Passes = ["lightmap_tex_detail_blend_specular_dblmult"],
			Base = 1, Detail = 2, DetailB = 3, Bump = 0, SpecularMask = 4,
			BaseUv = UvTransform.Xform( 1 ), DetailUv = UvTransform.Xform( 4 ), DetailBUv = UvTransform.ScaleXY( 5 ), BumpUv = UvTransform.Xform( 0 ), SpecularMaskUv = UvTransform.ScaleZW( 6 ),
			AlbedoMultiply = 2, AlbedoAdd = 3, SpecularColor = 0, SpecularColorGrazing = 1,
		},
		// 1 shader
		[@"opaque\tex_bump_env_alpha_test_indexed"] = new()
		{
			Passes = ["lightmap_tex_env_detail_alpha_test"],
			Base = 2, Detail = 3, Bump = 0, AlphaMask = 1, EnvMap = 4,
			BaseUv = UvTransform.Xform( 1 ), DetailUv = UvTransform.Xform( 2 ), BumpUv = UvTransform.Xform( 0 ), AlphaMaskUv = UvTransform.ScaleXY( 3 ),
			SpecularColor = 4, SpecularColorGrazing = 5, EnvTintPerpendicular = 0, EnvTintParallel = 1, EnvBrightnessPerpendicular = 2, EnvBrightnessParallel = 3,
		},
		// 1 shader
		[@"opaque\tex_bump_env_detail_overlay_combined"] = new()
		{
			Passes = ["lightmap_tex_detail_overlay", "default_environment_map_tangent_space_fast"],
			Base = 1, Detail = 2, DetailC = 3, Bump = 0, EnvMap = 4,
			BaseUv = UvTransform.Xform( 1 ), DetailUv = UvTransform.Xform( 2 ), DetailCUv = UvTransform.ScaleXY( 3 ), BumpUv = UvTransform.Xform( 0 ),
			AlbedoMultiply = 0, AlbedoAdd = 1, SpecularColorGrazing = 3,
			EnvLayerPerpendicularColor = 6, EnvLayerPerpendicularBrightness = 8, EnvLayerParallelColor = 7, EnvLayerParallelBrightness = 9,
		},
		// 1 shader
		[@"opaque\tex_bump_env_illum_3_channel_combined_unfucked"] = new()
		{
			Passes = ["lightmap_tex_env_detail", "multichannel_self_illumination"],
			Base = 1, Detail = 2, Bump = 0, EnvMap = 3, SelfIllum = 4,
			BaseUv = UvTransform.Xform( 1 ), DetailUv = UvTransform.Xform( 2 ), BumpUv = UvTransform.Xform( 0 ), SelfIllumUv = UvTransform.Xform( 5 ),
			SpecularColor = 4, SpecularColorGrazing = 5, EnvTintPerpendicular = 0, EnvTintParallel = 1, EnvBrightnessPerpendicular = 2, EnvBrightnessParallel = 3, ChannelRIntensity = 6, ChannelGIntensity = 7, ChannelBIntensity = 11, ChannelRColor = 8, ChannelGColor = 9, ChannelBColor = 10,
		},
		// 1 shader
		[@"opaque\tex_bump_env_illum_tiling_specular"] = new()
		{
			Passes = ["lightmap_tex_tiling", "default_self_illumination", "default_environment_map_tangent_space_fast"],
			Base = 1, Detail = 2, Bump = 0, SpecularMask = 4, EnvMap = 5, SelfIllum = 6,
			BaseUv = UvTransform.Xform( 1 ), DetailUv = UvTransform.Xform( 2 ), BumpUv = UvTransform.Xform( 0 ), SpecularMaskUv = UvTransform.ScaleXY( 3 ), SelfIllumUv = UvTransform.Xform( 6 ),
			AlbedoMultiply = 0, SpecularColor = 1, SpecularColorGrazing = 2, SelfIllumColor = 3,
			EnvLayerPerpendicularColor = 7, EnvLayerPerpendicularBrightness = 9, EnvLayerParallelColor = 8, EnvLayerParallelBrightness = 10,
		},
		// 1 shader
		[@"opaque\tex_bump_env_two_change_color_multiply_map_self_illum"] = new()
		{
			Passes = ["default_texture_two_change_color_multiply_map_zwrite", "old_lightmap_tangentspace", "default_self_illumination", "default_environment_map_tangent_space"],
			Base = 1, Detail = 2, Bump = 0, EnvMap = 5, SelfIllum = 6, ChangeColorMask = 3,
			BaseUv = UvTransform.Xform( 1 ), DetailUv = UvTransform.Xform( 0 ), BumpUv = UvTransform.Xform( 4 ), SelfIllumUv = UvTransform.Xform( 9 ), ChangeColorMaskUv = UvTransform.ScaleZW( 2 ),
			SpecularColor = 0, SpecularColorGrazing = 1, SheenWidth = 3, SheenColor = 4, SelfIllumColor = 2,
			EnvLayerPerpendicularColor = 10, EnvLayerPerpendicularBrightness = 12, EnvLayerParallelColor = 11, EnvLayerParallelBrightness = 13,
		},
		// 1 shader
		[@"opaque\tex_bump_illum_alpha_test_single_pass"] = new()
		{
			Passes = ["lightmap_tex_illum_alpha_test"],
			Base = 1, Bump = 0, AlphaMask = 3, SelfIllum = 2,
			BaseUv = UvTransform.Xform( 1 ), BumpUv = UvTransform.Xform( 0 ), AlphaMaskUv = UvTransform.ScaleXY( 3 ), SelfIllumUv = UvTransform.Xform( 2 ),
			SpecularColor = 0, SpecularColorGrazing = 2, SelfIllumColor = 1,
		},
		// 1 shader
		[@"opaque\tex_bump_illum_detail"] = new()
		{
			Passes = ["lightmap_tex_detail", "default_self_illumination_detail"],
			Base = 1, Detail = 2, Bump = 0, SelfIllum = 3, SelfIllumDetail = 4,
			BaseUv = UvTransform.Xform( 1 ), DetailUv = UvTransform.Xform( 2 ), BumpUv = UvTransform.Xform( 0 ), SelfIllumUv = UvTransform.Xform( 7 ), SelfIllumDetailUv = UvTransform.ScaleXY( 8 ),
			SpecularColor = 0, SpecularColorGrazing = 1, SelfIllumColor = 2, SelfIllumDetailStrength = 3,
		},
		// 1 shader
		[@"opaque\tex_bump_tiling_specular"] = new()
		{
			Passes = ["lightmap_tex_tiling"],
			Base = 1, Detail = 2, Bump = 0, SpecularMask = 3,
			BaseUv = UvTransform.Xform( 1 ), DetailUv = UvTransform.Xform( 2 ), BumpUv = UvTransform.Xform( 0 ), SpecularMaskUv = UvTransform.ScaleXY( 3 ),
			AlbedoMultiply = 0, SpecularColor = 1, SpecularColorGrazing = 2,
		},
	};
}
