//
// Halo 2's opaque shader templates (opaque\tex_bump and its 94 relatives), as one pass.
//
// Halo 2 lights level geometry in one "lightmap" pass: the albedo (base map times detail maps, doubled and
// clamped) times twice the lightmap, which the bump map shades against the lightmap's per-vertex incident light
// direction, plus the lightmap's specular highlight and, on environment mapped templates, a cube map reflection.
// The templates that build the albedo in a pass of its own first (texture accumulation), add the reflection in a
// layer of its own, or glow (the self-illumination layer) fold into the same pass here: each of those layers
// only feeds or adds onto the 8-bit frame, so the result is the same but for rounding. Everything happens in
// gamma space with Halo's 8-bit clamps, so textures are sampled raw and the result is converted to linear at the
// end.
//
// Which template does what, and which bitmap or constant of the Halo shader is the base map, detail map and so
// on, the mount works out (Blam/Halo2/OpaqueTemplates.cs, Halo2/OpaqueMaterial.cs); the modes below are the
// variations of the math between templates.
//
// Objects (render models) are lit by Halo 2's object lighting instead of a lightmap, or, on the PRT templates
// (prt_simple and its relatives, mostly campaign machinery), by the object's spherical harmonic light through
// each vertex's precomputed radiance transfer (see PrtShading).
//
// The specular highlight and the fresnel tint see the surface from the camera's direction, not its position:
// MCC's level shaders use a constant "eye vector", -camera forward, so they change as the camera turns, not as
// it moves. Reflections use the real view direction.
//
// Shader animation: the mount evaluates what follows object functions for the object at rest (or level geometry)
// and what is frozen in time; the texture scrolling and self-illumination pulses that follow time curves are
// evaluated here every frame (AnimatedTransform, AnimatedConstant).
//
// Dynamic lights (s&box's point and spot lights) are added Halo's way when a game turns them on (HaloDynamicLights).
//
// Not here: change colours (objects), bloom.
//

HEADER
{
	Description = "Halo 2 opaque shader";
}

FEATURES
{
	#include "common/features.hlsl"
}

MODES
{
	Forward();
	Depth();
	ToolsShadingComplexity( "tools_shading_complexity.shader" );
}

COMMON
{
	// halo2_opaque_camo.shader (tools/compile-shaders.ps1) defines this: a camouflaged object's model, drawn with the
	// translucent things after the frame buffer copy (see HaloCamouflage).
	#ifndef HALO2_OPAQUE_CAMO
	#define HALO2_OPAQUE_CAMO 0
	#endif
	#if HALO2_OPAQUE_CAMO
	#define S_TRANSLUCENT 1
	#define BLEND_MODE_ALREADY_SET 1
	#define DEPTH_STATE_ALREADY_SET 1
	#endif

	#ifndef S_ALPHA_TEST
	#define S_ALPHA_TEST 1
	#endif
	#ifndef S_TRANSLUCENT
	#define S_TRANSLUCENT 0
	#endif

	#include "common/shared.hlsl"

	#define S_UV2 1
	#define CUSTOM_MATERIAL_INPUTS

	// Level geometry's shader LOD (see LevelLod): the template's LOD0 threshold, the shader's LOD bias scale, 1 if
	// the material switches LOD at all.
	float4 g_vLod < Default4( 0.0, 1.0, 0.0, 0.0 ); >;

	// 1 on level geometry, whose vertices carry the LOD sphere and planar fog membership (Halo2/LevelVertex.cs).
	float g_flHaloLevel < Default( 0.0 ); >;
}

struct VertexInput
{
	// The lightmap's incoming light direction (world space, length = how directional the light is).
	float3 vIncidentLight : TEXCOORD4 < Semantic( None ); >;

	// Instanced geometry lit per vertex: the lightmap colour, standing in for a lightmap page.
	float3 vLightmapColor : TEXCOORD5 < Semantic( None ); >;

	// Render models: the vertex's radiance transfer, 9 spherical harmonic coefficients (Halo2/RenderModelBuilder.cs).
	// Level geometry instead: its cluster's or instance's bounding sphere centre (A) and radius, and its part's LOD
	// magic number (B.x, B.y), and whether it's in the BSP's planar fog (B.z; Halo2/LevelVertex.cs).
	float3 vPrtTransferA : TEXCOORD6 < Semantic( None ); >;
	float3 vPrtTransferB : TEXCOORD7 < Semantic( None ); >;
	float3 vPrtTransferC : TEXCOORD8 < Semantic( None ); >;
	#include "common/vertexinput.hlsl"
};

struct PixelInput
{
	float3 vIncidentLightWs : TEXCOORD14;
	float3 vLightmapColor : TEXCOORD15;
	float3 vPrtTransferA : TEXCOORD8;
	float3 vPrtTransferB : TEXCOORD9;
	float3 vPrtTransferC : TEXCOORD10;

	// How far level geometry has faded from its shader's LOD0 to its LOD1, 0 to 1 (see LevelLod).
	nointerpolation float vHaloLod : TEXCOORD12;

	// 1 where level geometry is in its BSP's planar fog (MCC draws the planar fog only there; objects get none here).
	nointerpolation float vHaloInPlanarFog : TEXCOORD13;

	// Objects: the direction to the camera at each vertex in the vertex's tangent frame (tangent, binormal, normal),
	// normalised there and interpolated without renormalising, as MCC's object vertex shaders pack it into COLOR0
	// (shorter between vertices that see the camera from different sides, which dims the highlight). Normalising in
	// the tangent frame matters: Halo 2's frames aren't orthonormal, so the world direction's projections on them
	// make a vector longer than 1, which faced every pixel more to the eye (highlight ~1.1x, more of the facing
	// specular colour against MCC on Zanzibar's drums).
	float3 vObjectEyeTs : TEXCOORD5;
	#include "common/pixelinput.hlsl"
};

VS
{
	#include "common/vertex.hlsl"

	// MCC's shader LOD for level geometry (scratch research notes, bsp_lod.md): the cluster's or instance's bounding
	// sphere sized as 480 r / (depth tan(vfov / 2)) (its depth along the view, at least 0.1 world units), in steps of
	// 4 up to 1020, times the part's magic number, 0.85 and the shader's LOD bias scale, over 480; LOD1 below the
	// template's LOD0 threshold. A 480-line screen at the view's own vertical field of view, so the resolution doesn't
	// matter but the field of view does (1203.2 r / depth at Halo's default 70 degree field, 868 at 90: MCC rebuilds
	// its reference field from the player's setting). LOD2, lightmap only, draws as LOD1 here.
	// Rather than MCC's switch, which pops the detail maps in and out, the part fades from LOD0 at MCC's switch to
	// LOD1 at twice its depth, sized without the steps of 4. Returns the fade: 0 for LOD0, 1 for LOD1. A part whose
	// largest size stays under the threshold, which MCC never draws at LOD0 (big outer shells), stays 1.
	float LevelLod( VertexInput v )
	{
		if ( g_vLod.z < 0.5 || v.vPrtTransferB.x <= 0 )
			return 0;

		float scale = v.vPrtTransferB.y * 0.85 * g_vLod.y / 480;
		if ( 1020 * scale < g_vLod.x )
			return 1;

		// The level's model sits untransformed at the origin: the centre is a world position.
		float depth = max( abs( dot( v.vPrtTransferA - g_vCameraPositionWs, g_vCameraDirWs ) ), 0.1 * 64 / 0.62 );
		float size = min( 480 * abs( g_matViewToProjection[1][1] ) * v.vPrtTransferB.x / depth, 1020 );
		return saturate( 2 - 2 * size * scale / g_vLod.x );
	}

	PixelInput MainVs( VertexInput v )
	{
		PixelInput i = ProcessVertex( v );
		i.vIncidentLightWs = v.vIncidentLight;
		i.vLightmapColor = v.vLightmapColor;
		i.vPrtTransferA = v.vPrtTransferA;
		i.vPrtTransferB = v.vPrtTransferB;
		i.vPrtTransferC = v.vPrtTransferC;
		i.vHaloLod = LevelLod( v );
		i.vHaloInPlanarFog = g_flHaloLevel > 0.5 ? v.vPrtTransferB.z : 0;
		float3 toEye = g_vCameraPositionWs - i.vPositionWs.xyz;
		i.vObjectEyeTs = normalize( float3( dot( normalize( i.vTangentUWs ), toEye ), dot( normalize( i.vTangentVWs ), toEye ), dot( normalize( i.vNormalWs ), toEye ) ) );
		return FinalizeVertex( i );
	}
}

PS
{
	#include "common/pixel.hlsl"

	SamplerState g_sWrap < Filter( Anisotropic ); AddressU( WRAP ); AddressV( WRAP ); >;

	// The "clamped" templates clamp U (only) on the base and bump maps.
	SamplerState g_sClampU < Filter( Anisotropic ); AddressU( CLAMP ); AddressV( WRAP ); >;
	SamplerState g_sLightmap < Filter( Bilinear ); AddressU( CLAMP ); AddressV( CLAMP ); >;
	SamplerState g_sClamp < Filter( Bilinear ); AddressU( CLAMP ); AddressV( CLAMP ); >;

	#if HALO2_OPAQUE_CAMO
		// MCC draws a camouflaged unit in a render job of its own among the transparent things: it copies the frame
		// (which then holds everything behind the unit, not the unit), draws the unit's parts with their own shaders,
		// then blends its camouflage pass over them at equal depth by the camouflage (layer 18,
		// rasterizer\render_layer_disabled, implementation 0); fully camouflaged (from 255/256) it draws only the
		// camouflage pass, opaque (implementation 1). s&box copies the frame after the opaque things, so this variant
		// is translucent and writes its colour as is, both steps in one (scratch research notes,
		// overnight/shields_camo/re_notes.md).
		BoolAttribute( bWantsFBCopyTexture, true );
		RenderState( BlendEnable, false );
		RenderState( DepthEnable, true );
		RenderState( DepthFunc, GREATER_EQUAL );
		RenderState( DepthWriteEnable, true );

		Texture2D g_tFrameBufferCopyTexture < Attribute( "FrameBufferCopyTexture" ); SrgbRead( false ); >;
		SamplerState g_sCamoBehind < Filter( Bilinear ); AddressU( CLAMP ); AddressV( CLAMP ); >;

		// The frame grabbed after the sky, the water and the weather (Halo2/CamoFrame.cs, set by the scene upkeep):
		// s&box's copy above is taken before them, so cloaked units in front of the sea refracted the sky's clear colour.
		Texture2D g_tHaloCamoFrame < Attribute( "HaloCamoFrame" ); SrgbRead( false ); >;
		bool g_bHaloHasCamoFrame < Attribute( "HaloHasCamoFrame" ); Default( 0 ); >;

		// The bump map the camouflage refracts through: the part's own shader's camo bump slot (extern 26), flat without.
		CreateInputTexture2D( CamoBump, Linear, 8, "None", "_color", ",0/,0/0", Default4( 0.50, 0.50, 1.00, 1.00 ) );
		Texture2D g_tCamoBump < Channel( RGBA, Box( CamoBump ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( false ); >;

		// render_layer_disabled's constants: bump and normal offsets (c26.xy, 1/320 of the screen), facing exponents (c27.xy).
		float4 g_vCamoOffsets < Default4( 64.0, 32.0, 1.0, 1.0 ); >;

		// The object's active camouflage (0-1), set by a game on the renderer.
		float g_flHaloCamo < Attribute( "HaloCamo" ); Default( 0.0 ); >;
		float g_flHaloCamoDebug < Attribute( "HaloCamoDebug" ); Default( 0.0 ); >;

		// MCC's camouflage pass (transparent_active_camo_viewer_normal VS, active_camo_opaque PS): the frame copy read at
		// the pixel's screen position bent by the bump map through the tangent frame as the camera sees it (s = facing *
		// 64) and by the normal (facing * 32 * (N.right, N.up)), both times camo / 320 of the screen; the bump map's
		// blue scales the screen position, as the shader's dot product does. c squared brightens the edges (1 - facing)^2
		// where the bump map has relief. Partial camouflage blends it over the shaded model by the camouflage.
		float3 HaloCamouflage( PixelInput i, float3 model, float3 normalWs, float3 tangentWs, float3 binormalWs )
		{
			float camo = saturate( g_flHaloCamo );
			if ( camo <= 0 )
				return model;

			float3 forward = g_vCameraDirWs.xyz;
			float3 right = normalize( cross( forward, g_vCameraUpDirWs.xyz ) );
			float3 up = cross( right, forward );

			float facing = max( dot( normalWs, -forward ), 0 );
			float s = pow( facing, g_vCamoOffsets.z ) * g_vCamoOffsets.x;
			float2 o = pow( facing, g_vCamoOffsets.w ) * g_vCamoOffsets.y * float2( dot( normalWs, right ), dot( normalWs, up ) );
			float k = camo / 320;

			float3 b = saturate( g_tCamoBump.Sample( g_sWrap, i.vTextureCoords.xy ).rgb );
			float2 bent = b.xy * 2 - 1;
			float2 screen = i.vPositionSs.xy * g_vInvViewportSize;
			float2 uv = float2(
				b.z * ( screen.x + o.x * k ) + ( bent.x * dot( tangentWs, right ) + bent.y * dot( binormalWs, right ) ) * s * k,
				b.z * ( screen.y + o.y * k ) + ( bent.x * dot( tangentWs, up ) + bent.y * dot( binormalWs, up ) ) * s * k );

			// Debug (HaloCamoDebug): 1 the copy a fixed 0.05 screens to the right, 2 the bent coordinates as colour.
			int debug = (int)round( g_flHaloCamoDebug );
			if ( debug == 1 )
				uv = screen + float2( 0.05, 0 );
			else if ( debug == 2 )
				return float3( frac( uv * 4 ), 0 );

			// s&box's frame copy for translucent readers is masked by their own depth (its refraction stencil, meant to
			// keep what's in front of glass out of its refraction): outside the unit's silhouette it reads black. MCC
			// bends up to a fifth of the screen, mostly past the silhouette near the unit's edges, so a black read is
			// tried again bent half as far, a quarter, then not at all.
			float3 behind = 0;
			if ( g_bHaloHasCamoFrame )
				behind = max( g_tHaloCamoFrame.SampleLevel( g_sCamoBehind, uv, 0 ).rgb, 0 );
			else for ( int step = 0; step < 4; step++ )
			{
				float2 at = lerp( screen, uv, step < 3 ? exp2( -step ) : 0 );
				behind = max( g_tFrameBufferCopyTexture.SampleLevel( g_sCamoBehind, at * g_vFrameBufferCopyInvSizeAndUvScale.zw, 0 ).rgb, 0 );
				if ( max( behind.r, max( behind.g, behind.b ) ) > 0.0005 )
					break;
			}
			float3 c = saturate( lerp( 1.055 * pow( behind, 1.0 / 2.4 ) - 0.055, behind * 12.92, step( behind, 0.0031308 ) ) );
			float e = ( 1 - facing ) * ( 1 - facing );

			if ( camo >= 255.0 / 256.0 )
				return saturate( c * c * e * ( 1 - b.z ) + c );

			float3 camouflage = saturate( c * c * saturate( e * camo ) * ( 1 - b.z ) + saturate( c * camo ) );
			return lerp( model, camouflage, camo );
		}
	#endif

	CreateInputTexture2D( BaseMap, Linear, 8, "None", "_color", ",0/,0/0", Default4( 1.00, 1.00, 1.00, 1.00 ) );
	CreateInputTexture2D( DetailMap, Linear, 8, "None", "_color", ",0/,0/0", Default4( 0.50, 0.50, 0.50, 1.00 ) );
	CreateInputTexture2D( DetailMapB, Linear, 8, "None", "_color", ",0/,0/0", Default4( 0.50, 0.50, 0.50, 1.00 ) );
	CreateInputTexture2D( DetailMapC, Linear, 8, "None", "_color", ",0/,0/0", Default4( 0.50, 0.50, 0.50, 1.00 ) );
	CreateInputTexture2D( BumpMap, Linear, 8, "None", "_color", ",0/,0/0", Default4( 0.50, 0.50, 1.00, 1.00 ) );
	CreateInputTexture2D( AlphaMapA, Linear, 8, "None", "_color", ",0/,0/0", Default4( 1.00, 1.00, 1.00, 1.00 ) );
	CreateInputTexture2D( AlphaMap, Linear, 8, "None", "_color", ",0/,0/0", Default4( 1.00, 1.00, 1.00, 1.00 ) );
	CreateInputTexture2D( SpecularMask, Linear, 8, "None", "_color", ",0/,0/0", Default4( 1.00, 1.00, 1.00, 1.00 ) );
	CreateInputTexture2D( ChangeColorMask, Linear, 8, "None", "_color", ",0/,0/0", Default4( 0.00, 0.00, 0.00, 0.00 ) );
	CreateInputTexture2D( EnvMap, Linear, 8, "None", "_color", ",0/,0/0", Default4( 0.00, 0.00, 0.00, 1.00 ) );
	CreateInputTexture2D( EnvMask, Linear, 8, "None", "_color", ",0/,0/0", Default4( 1.00, 1.00, 1.00, 1.00 ) );
	CreateInputTexture2D( SelfIllumMap, Linear, 8, "None", "_color", ",0/,0/0", Default4( 0.00, 0.00, 0.00, 0.00 ) );
	CreateInputTexture2D( SelfIllumMapB, Linear, 8, "None", "_color", ",0/,0/0", Default4( 0.00, 0.00, 0.00, 0.00 ) );
	CreateInputTexture2D( SelfIllumDetail, Linear, 8, "None", "_color", ",0/,0/0", Default4( 0.50, 0.50, 0.50, 1.00 ) );
	CreateInputTexture2D( SelfIllumDetailB, Linear, 8, "None", "_color", ",0/,0/0", Default4( 0.50, 0.50, 0.50, 1.00 ) );
	CreateInputTexture2D( Lightmap, Linear, 8, "None", "_color", ",0/,0/0", Default4( 0.50, 0.50, 0.50, 1.00 ) );

	Texture2D g_tBaseMap < Channel( RGBA, Box( BaseMap ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( false ); >;
	Texture2D g_tDetailMap < Channel( RGBA, Box( DetailMap ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( false ); >;
	Texture2D g_tDetailMapB < Channel( RGBA, Box( DetailMapB ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( false ); >;
	Texture2D g_tDetailMapC < Channel( RGBA, Box( DetailMapC ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( false ); >;
	Texture2D g_tBumpMap < Channel( RGBA, Box( BumpMap ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( false ); >;
	Texture2D g_tAlphaMapA < Channel( RGBA, Box( AlphaMapA ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( false ); >;
	Texture2D g_tAlphaMap < Channel( RGBA, Box( AlphaMap ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( false ); >;
	Texture2D g_tSpecularMask < Channel( RGBA, Box( SpecularMask ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( false ); >;
	Texture2D g_tChangeColorMask < Channel( RGBA, Box( ChangeColorMask ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( false ); >;
	Texture2D g_tEnvMap < Channel( RGBA, Box( EnvMap ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( false ); >;
	Texture2D g_tEnvMask < Channel( RGBA, Box( EnvMask ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( false ); >;
	Texture2D g_tSelfIllumMap < Channel( RGBA, Box( SelfIllumMap ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( false ); >;
	Texture2D g_tSelfIllumMapB < Channel( RGBA, Box( SelfIllumMapB ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( false ); >;
	Texture2D g_tSelfIllumDetail < Channel( RGBA, Box( SelfIllumDetail ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( false ); >;
	Texture2D g_tSelfIllumDetailB < Channel( RGBA, Box( SelfIllumDetailB ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( false ); >;
	Texture2D g_tLightmap < Channel( RGBA, Box( Lightmap ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( false ); >;

	// Halo's 2D texture transforms, uv * xy + zw. All but the base apply to the base-transformed uv.
	float4 g_vBaseTransform < Default4( 1.0, 1.0, 0.0, 0.0 ); >;
	float4 g_vDetailTransform < Default4( 1.0, 1.0, 0.0, 0.0 ); >;
	float4 g_vDetailBTransform < Default4( 1.0, 1.0, 0.0, 0.0 ); >;
	float4 g_vDetailCTransform < Default4( 1.0, 1.0, 0.0, 0.0 ); >;
	float4 g_vBumpTransform < Default4( 1.0, 1.0, 0.0, 0.0 ); >;
	float4 g_vAlphaATransform < Default4( 1.0, 1.0, 0.0, 0.0 ); >;
	float4 g_vAlphaTransform < Default4( 1.0, 1.0, 0.0, 0.0 ); >;
	float4 g_vSpecularMaskTransform < Default4( 1.0, 1.0, 0.0, 0.0 ); >;
	float4 g_vChangeColorMaskTransform < Default4( 1.0, 1.0, 0.0, 0.0 ); >;
	float4 g_vEnvMaskTransform < Default4( 1.0, 1.0, 0.0, 0.0 ); >;
	float4 g_vSelfIllumTransform < Default4( 1.0, 1.0, 0.0, 0.0 ); >;
	float4 g_vSelfIllumBTransform < Default4( 1.0, 1.0, 0.0, 0.0 ); >;
	float4 g_vSelfIllumDetailTransform < Default4( 1.0, 1.0, 0.0, 0.0 ); >;
	float4 g_vSelfIllumDetailBTransform < Default4( 1.0, 1.0, 0.0, 0.0 ); >;

	// Shader animation (Halo2/OpaqueMaterial.cs SetAnimation): time curves added to a texture transform's offsets
	// (scrolling), for the transform of role Role (1 base, 2 detail, 3 detail B, 4 detail C, 5 bump, 6 alpha test
	// A, 7 alpha test, 8 specular mask, 9 environment mask, 10 self-illumination, 11 self-illumination B, 12 and 13
	// its detail maps; 0 unused). Curves: A = (type + 16 shape, period in seconds, output min, output max), B = the
	// curve's constants; a period of 0 is no curve. See AnimatedFraction.
	float4 g_vUvAnim0UA < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float4 g_vUvAnim0UB < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float4 g_vUvAnim0VA < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float4 g_vUvAnim0VB < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float g_flUvAnim0Role < Default( 0.0 ); >;
	float4 g_vUvAnim1UA < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float4 g_vUvAnim1UB < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float4 g_vUvAnim1VA < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float4 g_vUvAnim1VB < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float g_flUvAnim1Role < Default( 0.0 ); >;
	float4 g_vUvAnim2UA < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float4 g_vUvAnim2UB < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float4 g_vUvAnim2VA < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float4 g_vUvAnim2VB < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float g_flUvAnim2Role < Default( 0.0 ); >;
	float4 g_vUvAnim3UA < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float4 g_vUvAnim3UB < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float4 g_vUvAnim3VA < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float4 g_vUvAnim3VB < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float g_flUvAnim3Role < Default( 0.0 ); >;

	// Six self-illumination parameters that follow a time curve (colour pulses, flicker): the curve (as above,
	// output 0-1), the values at its ends, and the target (role, component 0-3 or 4 for rgb, 1 if used). Roles:
	// 1 g_vSelfIllumColor, 2 g_flSelfIllumDetailStrength, 3-5 g_vChannelR/G/B, 6-8 g_vOcclusionTintR/G/B,
	// 9 g_flMeterValue, 10 g_vMeterColorBelow, 11 g_vMeterColorAbove, 12 g_flPlasmaThreshold, 13 g_vPlasmaBandColor
	// (scalars in x).
	float4 g_vAnimConst0A < Default4( 0.0, 0.0, 0.0, 1.0 ); >;
	float4 g_vAnimConst0B < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float4 g_vAnimConst0Min < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float4 g_vAnimConst0Max < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float3 g_vAnimConst0Target < Default3( 0.0, 0.0, 0.0 ); >;
	float4 g_vAnimConst1A < Default4( 0.0, 0.0, 0.0, 1.0 ); >;
	float4 g_vAnimConst1B < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float4 g_vAnimConst1Min < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float4 g_vAnimConst1Max < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float3 g_vAnimConst1Target < Default3( 0.0, 0.0, 0.0 ); >;
	float4 g_vAnimConst2A < Default4( 0.0, 0.0, 0.0, 1.0 ); >;
	float4 g_vAnimConst2B < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float4 g_vAnimConst2Min < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float4 g_vAnimConst2Max < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float3 g_vAnimConst2Target < Default3( 0.0, 0.0, 0.0 ); >;
	float4 g_vAnimConst3A < Default4( 0.0, 0.0, 0.0, 1.0 ); >;
	float4 g_vAnimConst3B < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float4 g_vAnimConst3Min < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float4 g_vAnimConst3Max < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float3 g_vAnimConst3Target < Default3( 0.0, 0.0, 0.0 ); >;
	float4 g_vAnimConst4A < Default4( 0.0, 0.0, 0.0, 1.0 ); >;
	float4 g_vAnimConst4B < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float4 g_vAnimConst4Min < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float4 g_vAnimConst4Max < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float3 g_vAnimConst4Target < Default3( 0.0, 0.0, 0.0 ); >;
	float4 g_vAnimConst5A < Default4( 0.0, 0.0, 0.0, 1.0 ); >;
	float4 g_vAnimConst5B < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float4 g_vAnimConst5Min < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float4 g_vAnimConst5Max < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float3 g_vAnimConst5Target < Default3( 0.0, 0.0, 0.0 ); >;

	float g_flClampU < Default( 0.0 ); >;
	static float s_flClampU;

	// Albedo (detail maps A, B, C; a = the base map's alpha): 0 base, 1 min( 2 base A, 1 ), 2 detail blend: the
	// base's alpha blends A to B, 3 detail blend then times C, 4 three detail blend: the base's alpha picks A, B,
	// C, 5 two detail: A then C, 6 detail overlay: A times C first, 7 black (self-illumination only). Then
	// saturate( albedo * multiply + add ).
	float g_flAlbedoMode < Default( 1.0 ); >;
	static float s_flAlbedoMode;
	float3 g_vAlbedoMultiply < Default3( 1.0, 1.0, 1.0 ); >;
	float3 g_vAlbedoAdd < Default3( 0.0, 0.0, 0.0 ); >;

	// Gloss, the mask the specular and the reflections are scaled by (see Gloss()). The specular uses its own
	// source if g_flSpecularGlossMode isn't negative. The specular mask is saturate( gloss * brightness ), times
	// max( 2 d - 1, 0 ) when gated by the light's directionality d (no lightmap specular where the light is
	// diffuse), times the gloss again when squared.
	float g_flGlossMode < Default( 1.0 ); >;
	static float s_flGlossMode;
	float g_flSpecularGlossMode < Default( -1.0 ); >;
	static float s_flSpecularGlossMode;
	float g_flGlossSquared < Default( 1.0 ); >;
	static float s_flGlossSquared;
	float g_flGlossGated < Default( 1.0 ); >;
	static float s_flGlossGated;

	// Bump: 0 bumped, 1 flat (light = lightmap, no specular), 2 flat with Halo's doubled N.L (lightmap * (1 + d L.z)).
	float g_flBumpMode < Default( 0.0 ); >;
	static float s_flBumpMode;

	// The lightmap: 0 none (lit neutrally), 1 the lightmap page, 2 the vertex colour (instanced geometry lit per vertex).
	float g_flLightmapMode < Default( 1.0 ); >;

	// Lightmap specular: 0 none, 1 default (s^8), 2 dull (s^2), 3 shiny (2 s^8 - 1), s = saturate( R.E ).
	float g_flLightmapType < Default( 0.0 ); >;
	float g_flLightmapSpecularBrightness < Default( 1.0 ); >;
	float3 g_vSpecularColor < Default3( 1.0, 1.0, 1.0 ); >;

	// Environment map: 0 none, 1 combined into the lightmap pass, 2 bumped layer, 3 fast layer, 4 fast masked
	// layer. Tint (rgb) and brightness (a) where the surface faces the eye (perpendicular) and at grazing angles
	// (parallel).
	float g_flEnvMode < Default( 0.0 ); >;
	static float s_flEnvMode;
	float4 g_vEnvPerpendicular < Default4( 1.0, 1.0, 1.0, 1.0 ); >;
	float4 g_vEnvParallel < Default4( 1.0, 1.0, 1.0, 1.0 ); >;
	float3 g_vSheenColor < Default3( 0.0, 0.0, 0.0 ); >;
	float g_flSheenWidth < Default( 0.0 ); >;

	// Alpha test: 0 none, 1 the base map's alpha, 2 the alpha test maps' alphas (A, B, and the bump map's if
	// g_flAlphaTestBump). MCC tests in the pixel shader: discard where round( mask ) == 0.
	float g_flAlphaTest < Default( 0.0 ); >;
	static float s_flAlphaTest;
	float g_flAlphaTestBump < Default( 0.0 ); >;
	static float s_flAlphaTestBump;

	// The modes above at LOD1 (Halo2/OpaqueMaterial.cs), used where the vertex shader fades to it (vHaloLod): A =
	// albedo, gloss, specular gloss, bump; B = environment map, self-illumination, alpha test, alpha test bump; C =
	// gloss squared, gloss gated, clamp U. Copied into the s_fl statics by UseLod before each shading.
	float4 g_vLod1ModesA < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float4 g_vLod1ModesB < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float4 g_vLod1ModesC < Default4( 0.0, 0.0, 0.0, 0.0 ); >;

	// Self-illumination, added after everything else (see SelfIllumination()).
	float g_flIllumMode < Default( 0.0 ); >;
	static float s_flIllumMode;
	float3 g_vSelfIllumColor < Default3( 1.0, 1.0, 1.0 ); >;
	float g_flSelfIllumDetailStrength < Default( 1.0 ); >;
	float4 g_vChannelR < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float4 g_vChannelG < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float4 g_vChannelB < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float3 g_vOcclusionTintR < Default3( 1.0, 1.0, 1.0 ); >;
	float3 g_vOcclusionTintG < Default3( 1.0, 1.0, 1.0 ); >;
	float3 g_vOcclusionTintB < Default3( 1.0, 1.0, 1.0 ); >;
	float g_flMeterValue < Default( 0.0 ); >;
	float3 g_vMeterColorBelow < Default3( 0.0, 0.0, 0.0 ); >;
	float3 g_vMeterColorAbove < Default3( 0.0, 0.0, 0.0 ); >;
	float g_flPlasmaThreshold < Default( 0.0 ); >;
	float3 g_vPlasmaBandColor < Default3( 0.0, 0.0, 0.0 ); >;

	// Objects (render models placed in a level, Halo2/RenderModelBuilder.cs) are lit by Halo 2's object
	// lighting instead of a lightmap: a direct light, an indirect light from the other side and an
	// ambient, worked out from the lightmap texel under the object (Blam/Halo2/ObjectLighting.cs). The
	// renderer tint holds the object's row in g_tObjectTable (Halo2/ObjectLightingTable.cs: 0 ambient,
	// 1 direct, 2 towards it, 3 indirect, 4 towards it, its alpha the row's own index; 5-12 the PRT
	// light); objects without a row get the fallback.
	float g_flObjectLit < Default( 0.0 ); >;
	float g_flObjectCount < Default( 0.0 ); >;

	// Objects' specular (MCC's object pixel shaders by the shader's specular type: 0 none, 1 default s^8, 2 dull s^2,
	// 3 shiny max( 4 ( s^8 - 0.753 ), 0 ), s = saturate( R.E ) with R the direct light reflected about the bump
	// normal): in the specular colour facing the eye (g_vSpecularColor) to the grazing one (g_vSpecularColorGrazing),
	// times the direct light, half the shader's lightmap specular brightness, the gloss; faded where N.L < 1/8.
	float g_flObjectSpecularType < Default( 0.0 ); >;

	// The shader's specular type for dynamic lights' highlights (0 none), level and objects alike (see DynamicLights).
	float g_flDynamicSpecularType < Default( 0.0 ); >;

	// A game turns on Halo 2's dynamic lights, s&box's point and spot lights, with this (a scene or renderer attribute).
	float g_flHaloDynamicLights < Attribute( "HaloDynamicLights" ); Default( 0.0 ); >;
	float3 g_vFallbackAmbient < Default3( 0.1, 0.1, 0.1 ); >;
	float3 g_vFallbackDirect < Default3( 0.3, 0.3, 0.3 ); >;
	float3 g_vFallbackToDirect < Default3( 0.0, 0.0, 1.0 ); >;
	float3 g_vFallbackIndirect < Default3( 0.2, 0.2, 0.2 ); >;
	float3 g_vFallbackToIndirect < Default3( 0.0, 0.0, -1.0 ); >;

	// The lighting of an object without a row, worked out as it moves (Halo2/MovingObjectLights.cs): ambient, alpha 1 when set.
	float4 g_vHaloObjectAmbient < Attribute( "HaloObjectAmbient" ); Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float3 g_vHaloObjectDirect < Attribute( "HaloObjectDirect" ); Default3( 0.0, 0.0, 0.0 ); >;
	float3 g_vHaloObjectToDirect < Attribute( "HaloObjectToDirect" ); Default3( 0.0, 0.0, 1.0 ); >;
	float3 g_vHaloObjectIndirect < Attribute( "HaloObjectIndirect" ); Default3( 0.0, 0.0, 0.0 ); >;
	float3 g_vHaloObjectToIndirect < Attribute( "HaloObjectToIndirect" ); Default3( 0.0, 0.0, -1.0 ); >;
	CreateInputTexture2D( ObjectTable, Linear, 8, "None", "_color", ",0/,0/0", Default4( 0.00, 0.00, 0.00, 0.00 ) );
	Texture2D g_tObjectTable < Channel( RGBA, Box( ObjectTable ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( false ); >;

	float4 ObjectTexel( int index, int texel )
	{
		// ObjectLightingTable.ObjectsPerRow and TexelsPerObject.
		return g_tObjectTable.Load( int3( ( index % 128 ) * 17 + texel, index / 128, 0 ) );
	}

	// The object's row from its renderer tint (one byte of the index per channel), or -1 without one.
	int ObjectIndex( float3 tint )
	{
		float3 bytes = round( saturate( tint ) * 255.0 );
		int index = (int)( bytes.r + bytes.g * 256.0 + bytes.b * 65536.0 + 0.5 );
		if ( index >= 1 && index < (int)g_flObjectCount && abs( ObjectTexel( index, 4 ).w - index ) < 0.5 )
			return index;
		return -1;
	}

	// An object's lighting: its row of g_tObjectTable, else what it was lit with as it moved, else the fallback.
	struct HaloObjectLight
	{
		float3 Ambient;
		float3 Direct;
		float3 ToDirect;
		float3 Indirect;
		float3 ToIndirect;
	};

	HaloObjectLight ObjectLighting( float3 tint )
	{
		HaloObjectLight o;
		o.Ambient = g_vFallbackAmbient;
		o.Direct = g_vFallbackDirect;
		o.ToDirect = g_vFallbackToDirect;
		o.Indirect = g_vFallbackIndirect;
		o.ToIndirect = g_vFallbackToIndirect;

		int index = ObjectIndex( tint );
		if ( index >= 0 )
		{
			o.Ambient = ObjectTexel( index, 0 ).rgb;
			o.Direct = ObjectTexel( index, 1 ).rgb;
			o.ToDirect = ObjectTexel( index, 2 ).xyz;
			o.Indirect = ObjectTexel( index, 3 ).rgb;
			o.ToIndirect = ObjectTexel( index, 4 ).xyz;
		}
		else if ( g_vHaloObjectAmbient.a > 0.5 )
		{
			o.Ambient = g_vHaloObjectAmbient.rgb;
			o.Direct = g_vHaloObjectDirect;
			o.ToDirect = g_vHaloObjectToDirect;
			o.Indirect = g_vHaloObjectIndirect;
			o.ToIndirect = g_vHaloObjectToIndirect;
		}

		return o;
	}

	// Objects' change colours (primary to quaternary, gamma space): a game's renderer attributes HaloChangeColor0-3
	// (alpha 1 to use the rgb: team and player colours), else the model variant's the material is for (alpha 2: a
	// material group the scene or game picked), else the object's row of the table (the colours picked from its
	// definition), else the model's default variant's (alpha 1: models a game spawns), else white. Colours an
	// object doesn't have are 0, as in MCC.
	float4 g_vHaloChangeColor0 < Attribute( "HaloChangeColor0" ); Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float4 g_vHaloChangeColor1 < Attribute( "HaloChangeColor1" ); Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float4 g_vHaloChangeColor2 < Attribute( "HaloChangeColor2" ); Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float4 g_vHaloChangeColor3 < Attribute( "HaloChangeColor3" ); Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float4 g_vVariantChangeColor0 < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float4 g_vVariantChangeColor1 < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float4 g_vVariantChangeColor2 < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float4 g_vVariantChangeColor3 < Default4( 0.0, 0.0, 0.0, 0.0 ); >;

	float3 ChangeColor( int k, int index )
	{
		float4 given = k == 0 ? g_vHaloChangeColor0 : k == 1 ? g_vHaloChangeColor1 : k == 2 ? g_vHaloChangeColor2 : g_vHaloChangeColor3;
		if ( given.a > 0.5 )
			return given.rgb;
		float4 variant = k == 0 ? g_vVariantChangeColor0 : k == 1 ? g_vVariantChangeColor1 : k == 2 ? g_vVariantChangeColor2 : g_vVariantChangeColor3;
		if ( variant.a > 1.5 )
			return variant.rgb;
		if ( index < 0 )
			return variant.a > 0.5 ? variant.rgb : 1;
		float4 stored = ObjectTexel( index, 13 + k );
		return stored.a > 0.5 ? stored.rgb : 0;
	}

	// lerp( 1, colour, mask ): how much a change colour tints where its mask is.
	float3 ChangeColorTint( float3 m, float3 color ) { return saturate( m * color + 1 - m ); }

	// The light an object's surface gets, in place of the bumped lightmap, as MCC's object pixel shaders
	// (implementation 2) work it out: sat( sat( N.Ld ) direct + sat( N.Li ) indirect ) + sat( max( bump.z, 0 )
	// ambient ), the ambient shaded by the bump map's own z rather than by which way the surface faces.
	float3 ObjectLight( HaloObjectLight o, float3 normalWs, float bumpZ )
	{
		return saturate( saturate( dot( normalWs, o.ToDirect ) ) * o.Direct + saturate( dot( normalWs, o.ToIndirect ) ) * o.Indirect )
			+ saturate( max( bumpZ, 0.0 ) * o.Ambient );
	}

	//
	// Halo 2's function evaluator (shader_engine.md section 5.1) for time curves, as halo2_transparent.shader's.
	//

	float TransitionShape( float shape, float x )
	{
		if ( shape < 0.5 ) return x;
		if ( shape < 1.5 ) return sqrt( x );
		if ( shape < 2.5 ) return sqrt( sqrt( x ) );
		if ( shape < 3.5 ) return x * x;
		if ( shape < 4.5 ) return x * x * x * x;
		if ( shape < 5.5 ) return 0.5 - 0.5 * cos( 3.14159265 * x );
		return shape < 6.5 ? 1 : 0;
	}

	float PeriodicShape( float shape, float x )
	{
		float t = frac( x );
		if ( shape < 0.5 ) return 1;
		if ( shape < 1.5 ) return 0;
		if ( shape < 3.5 ) return 0.5 + 0.5 * cos( 6.2831853 * t );
		if ( shape < 5.5 ) return 1 - abs( 2 * t - 1 );
		if ( shape < 7.5 ) return t;
		return 0.5 + 0.25 * sin( 6.2831853 * t ) + 0.25 * sin( 6.2831853 * 2.7 * t );
	}

	// The curve's value 0-1 at the current time: input = time / period, wrapped unless periodic.
	float AnimatedFraction( float4 a, float4 b )
	{
		float type = fmod( a.x, 16 );
		float shape = floor( a.x / 16 );
		float x = g_flTime / a.y;
		float f;
		if ( type < 0.5 ) f = frac( x );
		else if ( type < 2.5 ) f = lerp( b.x, b.y, TransitionShape( shape, frac( x ) ) );
		else if ( type < 3.5 ) f = lerp( b.z, b.w, PeriodicShape( shape, x * b.x + b.y ) );
		else if ( type < 4.5 ) f = frac( x ) * b.x + b.y;
		else f = lerp( b.x, b.y, pow( frac( x ), b.z ) );
		return saturate( f );
	}

	float AnimatedValue( float4 a, float4 b )
	{
		return a.y > 0 ? lerp( a.z, a.w, AnimatedFraction( a, b ) ) : 0;
	}

	//
	// Parameters that follow the object's functions while a game plays (Halo2/DrivenParameters.cs).
	// The game sets the functions on the renderer as HaloFunction0-7, 0-1, by the model's slots in <model>__functions.json; below 0 or unset, a parameter keeps its value at rest.
	// g_vDriven0-3: x the function's slot (-1 none), y the kind (1 a texture transform, 2 a self-illumination constant; the target is the role, as AnimatedTransform's and AnimatedConstant's), z the target.
	// Row 0-3 of g_tDrivenTable holds the parameter's values over the function, 16 samples from 0 to 1.
	//

	float g_flHaloFunction0 < Attribute( "HaloFunction0" ); Default( -1.0 ); >;
	float g_flHaloFunction1 < Attribute( "HaloFunction1" ); Default( -1.0 ); >;
	float g_flHaloFunction2 < Attribute( "HaloFunction2" ); Default( -1.0 ); >;
	float g_flHaloFunction3 < Attribute( "HaloFunction3" ); Default( -1.0 ); >;
	float g_flHaloFunction4 < Attribute( "HaloFunction4" ); Default( -1.0 ); >;
	float g_flHaloFunction5 < Attribute( "HaloFunction5" ); Default( -1.0 ); >;
	float g_flHaloFunction6 < Attribute( "HaloFunction6" ); Default( -1.0 ); >;
	float g_flHaloFunction7 < Attribute( "HaloFunction7" ); Default( -1.0 ); >;
	float4 g_vDriven0 < Default4( -1.0, 0.0, 0.0, 0.0 ); >;
	float4 g_vDriven1 < Default4( -1.0, 0.0, 0.0, 0.0 ); >;
	float4 g_vDriven2 < Default4( -1.0, 0.0, 0.0, 0.0 ); >;
	float4 g_vDriven3 < Default4( -1.0, 0.0, 0.0, 0.0 ); >;
	float4 g_vDrivenFrame0 < Default4( 1.0, 0.0, 0.0, 0.0 ); >;
	float4 g_vDrivenFrame1 < Default4( 1.0, 0.0, 0.0, 0.0 ); >;
	float4 g_vDrivenFrame2 < Default4( 1.0, 0.0, 0.0, 0.0 ); >;
	float4 g_vDrivenFrame3 < Default4( 1.0, 0.0, 0.0, 0.0 ); >;
	CreateInputTexture2D( DrivenTable, Linear, 8, "None", "_color", ",0/,0/0", Default4( 0.00, 0.00, 0.00, 0.00 ) );
	Texture2D g_tDrivenTable < Channel( RGBA, Box( DrivenTable ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( false ); >;

	float ObjectFunction( float slot )
	{
		if ( slot < 0.5 ) return g_flHaloFunction0;
		if ( slot < 1.5 ) return g_flHaloFunction1;
		if ( slot < 2.5 ) return g_flHaloFunction2;
		if ( slot < 3.5 ) return g_flHaloFunction3;
		if ( slot < 4.5 ) return g_flHaloFunction4;
		if ( slot < 5.5 ) return g_flHaloFunction5;
		if ( slot < 6.5 ) return g_flHaloFunction6;
		return g_flHaloFunction7;
	}

	float4 DrivenEntry( float4 entry, int row, float kind, float target, float4 value )
	{
		if ( entry.x < -0.5 || abs( entry.y - kind ) > 0.5 || abs( entry.z - target ) > 0.5 )
			return value;

		float f = ObjectFunction( entry.x );
		if ( f < 0 )
			return value;

		float x = saturate( f ) * 15;
		int i = min( (int)x, 14 );
		return lerp( g_tDrivenTable.Load( int3( i, row, 0 ) ), g_tDrivenTable.Load( int3( i + 1, row, 0 ) ), x - i );
	}

	// A map whose frame follows a function (kind 5): its texture is the bitmap's images stacked top to bottom, and the transform picks the image as Halo does, floor( value mod count ).
	// g_vDrivenFrame: x the image count, y the image at rest; the table holds the curve's value before the floor.
	float4 FrameEntry( float4 entry, float4 frame, int row, float role, float4 t )
	{
		if ( entry.x < -0.5 || abs( entry.y - 5 ) > 0.5 || abs( entry.z - role ) > 0.5 )
			return t;

		float image = frame.y;
		float f = ObjectFunction( entry.x );
		if ( f >= 0 )
		{
			float x = saturate( f ) * 15;
			int i = min( (int)x, 14 );
			float v = lerp( g_tDrivenTable.Load( int3( i, row, 0 ) ).x, g_tDrivenTable.Load( int3( i + 1, row, 0 ) ).x, x - i );
			image = v < 0 ? 0 : floor( fmod( v + 0.001, frame.x ) );
		}

		return float4( t.x, t.y / frame.x, t.z, ( t.w + image ) / frame.x );
	}

	float4 DrivenFrame( float role, float4 t )
	{
		t = FrameEntry( g_vDriven0, g_vDrivenFrame0, 0, role, t );
		t = FrameEntry( g_vDriven1, g_vDrivenFrame1, 1, role, t );
		t = FrameEntry( g_vDriven2, g_vDrivenFrame2, 2, role, t );
		t = FrameEntry( g_vDriven3, g_vDrivenFrame3, 3, role, t );
		return t;
	}

	// A parameter of a kind and target with the object's functions applied, if any drives it.
	float4 Driven( float kind, float target, float4 value )
	{
		value = DrivenEntry( g_vDriven0, 0, kind, target, value );
		value = DrivenEntry( g_vDriven1, 1, kind, target, value );
		value = DrivenEntry( g_vDriven2, 2, kind, target, value );
		value = DrivenEntry( g_vDriven3, 3, kind, target, value );
		return value;
	}

	// The scrolling one animation slot adds to a texture transform's offsets, if it targets that transform.
	float2 UvAnimation( float role, float4 ua, float4 ub, float4 va, float4 vb, float target )
	{
		if ( abs( target - role ) > 0.5 )
			return float2( 0, 0 );
		return float2( AnimatedValue( ua, ub ), AnimatedValue( va, vb ) );
	}

	// A texture transform (role as for g_flUvAnim0Role) with its scrolling.
	float4 AnimatedTransform( float role, float4 t )
	{
		t = DrivenFrame( role, Driven( 1, role, t ) );
		t.zw += UvAnimation( role, g_vUvAnim0UA, g_vUvAnim0UB, g_vUvAnim0VA, g_vUvAnim0VB, g_flUvAnim0Role );
		t.zw += UvAnimation( role, g_vUvAnim1UA, g_vUvAnim1UB, g_vUvAnim1VA, g_vUvAnim1VB, g_flUvAnim1Role );
		t.zw += UvAnimation( role, g_vUvAnim2UA, g_vUvAnim2UB, g_vUvAnim2VA, g_vUvAnim2VB, g_flUvAnim2Role );
		t.zw += UvAnimation( role, g_vUvAnim3UA, g_vUvAnim3UB, g_vUvAnim3VA, g_vUvAnim3VB, g_flUvAnim3Role );
		return t;
	}

	// One animated constant applied to a parameter, if it targets it.
	float4 ApplyAnimatedConstant( float role, float4 value, float4 a, float4 b, float4 lo, float4 hi, float3 target )
	{
		if ( target.z < 0.5 || a.y <= 0 || abs( target.x - role ) > 0.5 )
			return value;

		float4 v = lerp( lo, hi, AnimatedFraction( a, b ) );
		int component = (int)target.y;
		if ( component >= 4 ) value.xyz = v.xyz;
		else if ( component == 0 ) value.x = v.x;
		else if ( component == 1 ) value.y = v.x;
		else if ( component == 2 ) value.z = v.x;
		else value.w = v.x;
		return value;
	}

	// A self-illumination parameter (role as for g_vAnimConst0Target) with its time curves.
	float4 AnimatedConstant( float role, float4 value )
	{
		value = Driven( 2, role, value );
		value = ApplyAnimatedConstant( role, value, g_vAnimConst0A, g_vAnimConst0B, g_vAnimConst0Min, g_vAnimConst0Max, g_vAnimConst0Target );
		value = ApplyAnimatedConstant( role, value, g_vAnimConst1A, g_vAnimConst1B, g_vAnimConst1Min, g_vAnimConst1Max, g_vAnimConst1Target );
		value = ApplyAnimatedConstant( role, value, g_vAnimConst2A, g_vAnimConst2B, g_vAnimConst2Min, g_vAnimConst2Max, g_vAnimConst2Target );
		value = ApplyAnimatedConstant( role, value, g_vAnimConst3A, g_vAnimConst3B, g_vAnimConst3Min, g_vAnimConst3Max, g_vAnimConst3Target );
		value = ApplyAnimatedConstant( role, value, g_vAnimConst4A, g_vAnimConst4B, g_vAnimConst4Min, g_vAnimConst4Max, g_vAnimConst4Target );
		value = ApplyAnimatedConstant( role, value, g_vAnimConst5A, g_vAnimConst5B, g_vAnimConst5Min, g_vAnimConst5Max, g_vAnimConst5Target );
		return value;
	}

	// PRT (precomputed radiance transfer): 0 none, 1 prt_simple, 2 prt_lightmap (and prt_simple_lm_emissive), 3
	// prt_scarab. The irradiance is scaled by c21.x; g_vSpecularColor is the highlight's colour (prt_scarab's
	// facing the camera, g_vSpecularColorGrazing at grazing angles); g_tEnvMap is the specular cube.
	float g_flPrtMode < Default( 0.0 ); >;
	float g_flPrtScale < Default( 1.0 ); >;
	float3 g_vSpecularColorGrazing < Default3( 0.0, 0.0, 0.0 ); >;

	// The light a vertex's radiance transfer (9 spherical harmonic coefficients a, b, c, in the model's frame)
	// reflects from the object's PRT light (its row's texels 5-12, already turned into the object's frame), before
	// the template's scale, and the direction towards the dominant light (world space). Without a PRT light: the
	// transfer's own visibility, at half brightness.
	float3 PrtIrradiance( float3 tint, float3 a, float3 b, float3 c, out float3 toLight )
	{
		int index = ObjectIndex( tint );
		if ( index >= 0 && ObjectTexel( index, 12 ).w > 0.5 )
		{
			float4 t0 = float4( a, b.x );
			float4 t1 = float4( b.yz, c.xy );
			float4 last = ObjectTexel( index, 11 );
			toLight = ObjectTexel( index, 12 ).xyz;
			return float3(
				dot( t0, ObjectTexel( index, 5 ) ) + dot( t1, ObjectTexel( index, 6 ) ) + c.z * last.r,
				dot( t0, ObjectTexel( index, 7 ) ) + dot( t1, ObjectTexel( index, 8 ) ) + c.z * last.g,
				dot( t0, ObjectTexel( index, 9 ) ) + dot( t1, ObjectTexel( index, 10 ) ) + c.z * last.b );
		}

		toLight = float3( 0, 0, 1 );
		return float3( 1, 1, 1 ) * ( a.x * 1.772454 );
	}

	float2 Transform( float2 uv, float4 t ) { return uv * t.xy + t.zw; }

	float4 SampleMap( Texture2D t, float2 uv, float4 transform )
	{
		return saturate( Tex2DS( t, g_sWrap, Transform( uv, transform ) ) );
	}

	// The base and bump maps, U clamped on the clamped templates.
	float4 SampleClampable( Texture2D t, float2 uv )
	{
		return saturate( s_flClampU > 0.5 ? Tex2DS( t, g_sClampU, uv ) : Tex2DS( t, g_sWrap, uv ) );
	}

	// Cube maps arrive as a 2D atlas of six faces side by side (+X, -X, +Y, -Y, +Z, -Z), and the face and position
	// within it are picked with Direct3D's cube map rules: the same lookup as halo1_environment.shader's.
	float3 SampleCubeAtlas( float3 dir )
	{
		float3 a = abs( dir );
		float face;
		float2 st;
		float ma;

		if ( a.x >= a.y && a.x >= a.z )
		{
			ma = a.x;
			face = dir.x > 0 ? 0 : 1;
			st = dir.x > 0 ? float2( -dir.z, -dir.y ) : float2( dir.z, -dir.y );
		}
		else if ( a.y >= a.z )
		{
			ma = a.y;
			face = dir.y > 0 ? 2 : 3;
			st = dir.y > 0 ? float2( dir.x, dir.z ) : float2( dir.x, -dir.z );
		}
		else
		{
			ma = a.z;
			face = dir.z > 0 ? 4 : 5;
			st = dir.z > 0 ? float2( dir.x, -dir.y ) : float2( -dir.x, -dir.y );
		}

		// Keep away from the face edges so filtering doesn't pick up the neighbouring face.
		float2 uv = clamp( st / max( ma, 1e-6 ) * 0.5 + 0.5, 0.01, 0.99 );
		return saturate( g_tEnvMap.SampleLevel( g_sClamp, float2( ( face + uv.x ) / 6.0, uv.y ), 0 ).rgb );
	}

	// A gloss source: 0 none, 1 base alpha, 2 base alpha times detail alpha, 3 that doubled (clamped), 4 the bump
	// map's alpha, 5 the bump map's times the alpha test map's, 6 the detail blend's alpha, 7 the three detail
	// blend's, 8 base times detail A times detail C alphas, 9 the specular mask's alpha, 10 its blue, 11 the
	// specular mask's alpha doubled with detail A's, then doubled with detail B's.
	float Gloss( float mode, float4 base, float4 bump, float4 detailA, float4 detailB, float4 detailC, float4 mask, float4 specularMask )
	{
		if ( mode < 0.5 ) return 0;
		if ( mode < 1.5 ) return base.a;
		if ( mode < 2.5 ) return base.a * detailA.a;
		if ( mode < 3.5 ) return min( 2 * base.a * detailA.a, 1 );
		if ( mode < 4.5 ) return bump.a;
		if ( mode < 5.5 ) return bump.a * mask.a;
		if ( mode < 6.5 ) return min( lerp( detailA.a, detailB.a, base.a ), 1 );
		if ( mode < 7.5 )
		{
			float w1 = min( 2 * base.a, 1 );
			float w2 = max( 2 * base.a - 1, 0 );
			return min( w2 * detailC.a + min( w1 * detailB.a + ( 1 - w1 ) * detailA.a, 1 ) * ( 1 - w2 ), 1 );
		}
		if ( mode < 8.5 ) return base.a * detailA.a * detailC.a;
		if ( mode < 9.5 ) return specularMask.a;
		if ( mode < 10.5 ) return specularMask.b;
		return min( 2 * min( 2 * specularMask.a * detailA.a, 1 ) * detailB.a, 1 );
	}

	bool IsGloss( float mode, float glossMode, float specularGlossMode )
	{
		return abs( glossMode - mode ) < 0.5 || abs( specularGlossMode - mode ) < 0.5;
	}

	// Halo's plasma band: bright where the map's alpha is close to the threshold, sharpened by repeated squaring.
	float PlasmaBand( float a, float threshold )
	{
		float above = a - threshold + 0.501961;
		float below = -a + threshold + 0.501961;
		float x = ( round( below ) * ( above * above - below * below ) + below * below ) * 4;
		float sx = saturate( x ) * 2 - 1;
		float y = round( x ) * sx * sx;
		float sy = saturate( y ) * 2 - 1;
		return saturate( round( y ) * sy * sy );
	}

	// Self-illumination: 0 none, 1 map times colour, 2 plus the map's brightness times a detail map, 3 doubled
	// with a detail map, 4 two maps added, times a detail map, 5 detail maps masked by the map's alpha, 6 three
	// channels (red and green: intensity and colour, blue added), 7 three channels with an occlusion tint mask,
	// 8 meter (the map's alpha below or above a value picks the colour), 9 plasma band.
	float3 SelfIllumination( float2 uv )
	{
		float4 map = SampleMap( g_tSelfIllumMap, uv, AnimatedTransform( 10, g_vSelfIllumTransform ) );
		float4 detailTransform = AnimatedTransform( 12, g_vSelfIllumDetailTransform );
		float4 detailBTransform = AnimatedTransform( 13, g_vSelfIllumDetailBTransform );

		// The parameters with their time curves (colour pulses, flicker).
		float3 c = AnimatedConstant( 1, float4( g_vSelfIllumColor, 0 ) ).rgb;
		float detailStrength = AnimatedConstant( 2, float4( g_flSelfIllumDetailStrength, 0, 0, 0 ) ).x;
		float4 channelR = AnimatedConstant( 3, g_vChannelR );
		float4 channelG = AnimatedConstant( 4, g_vChannelG );
		float4 channelB = AnimatedConstant( 5, g_vChannelB );

		if ( s_flIllumMode < 1.5 ) return saturate( map.rgb * c );
		if ( s_flIllumMode < 2.5 )
		{
			float3 detail = SampleMap( g_tSelfIllumDetail, uv, detailTransform ).rgb;
			return min( saturate( dot( map.rgb, c ) ) * saturate( detail * detailStrength ) + saturate( map.rgb * c ), 1 );
		}
		if ( s_flIllumMode < 3.5 )
		{
			float3 detail = SampleMap( g_tSelfIllumDetail, uv, detailTransform ).rgb;
			return saturate( min( 2 * map.rgb * detail, 1 ) * c );
		}
		if ( s_flIllumMode < 4.5 )
		{
			float3 b = SampleMap( g_tSelfIllumMapB, uv, AnimatedTransform( 11, g_vSelfIllumBTransform ) ).rgb;
			float3 detail = SampleMap( g_tSelfIllumDetail, uv, detailTransform ).rgb;
			return saturate( min( map.rgb + b, 1 ) * detail * c );
		}
		if ( s_flIllumMode < 5.5 )
		{
			float3 inside = SampleMap( g_tSelfIllumDetail, uv, detailTransform ).rgb;
			float3 multiplied = SampleMap( g_tSelfIllumDetailB, uv, detailBTransform ).rgb;
			return saturate( min( multiplied * map.a + ( 1 - map.a ), 1 ) * min( inside * map.a + map.rgb, 1 ) * c );
		}
		if ( s_flIllumMode < 6.5 )
			return saturate( saturate( map.r * channelR.w ) * channelR.rgb + saturate( map.g * channelG.w ) * channelG.rgb ) + map.b * channelB.w * channelB.rgb;
		if ( s_flIllumMode < 7.5 )
		{
			// The occlusion mask is in the second detail slot.
			float3 o = SampleMap( g_tSelfIllumDetailB, uv, detailBTransform ).rgb;
			float3 tintR = AnimatedConstant( 6, float4( g_vOcclusionTintR, 0 ) ).rgb;
			float3 tintG = AnimatedConstant( 7, float4( g_vOcclusionTintG, 0 ) ).rgb;
			float3 tintB = AnimatedConstant( 8, float4( g_vOcclusionTintB, 0 ) ).rgb;
			float3 glow = saturate( saturate( map.b * channelB.w ) * channelB.rgb + saturate( saturate( map.r * channelR.w ) * channelR.rgb + saturate( map.g * channelG.w ) * channelG.rgb ) );
			return glow * saturate( o.r * tintR + 1 - o.r ) * saturate( o.g * tintG + 1 - o.g ) * saturate( o.b * tintB + 1 - o.b );
		}
		if ( s_flIllumMode < 8.5 )
		{
			float meter = AnimatedConstant( 9, float4( g_flMeterValue, 0, 0, 0 ) ).x;
			float3 below = AnimatedConstant( 10, float4( g_vMeterColorBelow, 0 ) ).rgb;
			float3 above = AnimatedConstant( 11, float4( g_vMeterColorAbove, 0 ) ).rgb;
			return saturate( lerp( map.rgb * below, map.rgb * above, round( map.a - meter + 0.501961 ) ) );
		}

		float threshold = AnimatedConstant( 12, float4( g_flPlasmaThreshold, 0, 0, 0 ) ).x;
		float3 band = AnimatedConstant( 13, float4( g_vPlasmaBandColor, 0 ) ).rgb;
		return min( saturate( PlasmaBand( map.a, threshold ) * band ) * map.r + saturate( map.b * channelB.rgb + saturate( map.g * channelG.rgb ) ), 1 );
	}

	// MCC's PRT pixel shaders (layer 5), in tangent space like them. The irradiance (their vertex shader: the
	// transfer dotted into the object's light, plus the vertex lightmap colour for prt_lightmap and prt_scarab, times
	// c21.x) is bumped against the dominant light like a lightmap; its squared length masks a highlight off the
	// specular cube (reflected off the unbumped surface), sharpened by the bump map against the half vector
	// (prt_simple, prt_lightmap). prt_scarab adds the cube to the light instead, its highlight the bump map's N.H to
	// the fourth in a colour between facing and grazing. prt_simple's albedo is its texture accumulation pass's (base
	// times detail, doubled, tinted; the mask their alphas); the others quadruple base times detail.
	float3 PrtShading( PixelInput i, float4 base, float4 detail, float3 albedo, float3 n, float3 tangentWs, float3 binormalWs, float3 normalWs )
	{
		float3 toLightWs;
		float3 irradiance = PrtIrradiance( i.vVertexColor.rgb, i.vPrtTransferA, i.vPrtTransferB, i.vPrtTransferC, toLightWs );
		bool lightmapVariant = g_flPrtMode > 1.5;
		if ( lightmapVariant )
			irradiance += i.vLightmapColor;
		float3 scaled = irradiance * g_flPrtScale;

		// The vertex colour's alpha: the squared length, prt_simple's before the scale.
		float strength = saturate( lightmapVariant ? dot( scaled, scaled ) : dot( irradiance, irradiance ) );
		float3 irr = saturate( scaled );

		float3 toCamera = normalize( -i.vPositionWithOffsetWs.xyz );
		float3 l = float3( 0, 0, 1 );
		if ( dot( toLightWs, toLightWs ) > 1e-8 )
			l = normalize( float3( dot( tangentWs, toLightWs ), dot( binormalWs, toLightWs ), dot( normalWs, toLightWs ) ) );
		float3 halfWs = normalize( toCamera + toLightWs );
		float3 h = float3( dot( tangentWs, halfWs ), dot( binormalWs, halfWs ), dot( normalWs, halfWs ) );
		float3 cube = SampleCubeAtlas( reflect( -toCamera, normalWs ) );

		if ( g_flPrtMode < 1.5 )
		{
			float nlSimple = clamp( clamp( dot( n, l ), -1, 1 ) - l.z, -1, 1 );
			float nhSimple = clamp( clamp( dot( n, h ), -1, 1 ) - h.z, -1, 1 );
			float mask = strength * base.a * detail.a;
			return min( 2 * saturate( irr * nlSimple + irr ) * albedo, 1 ) + min( 2 * mask * saturate( cube * nhSimple + cube ), 1 ) * g_vSpecularColor;
		}

		float nl = dot( n, l ) - l.z;
		if ( g_flPrtMode < 2.5 )
		{
			float nh = dot( n, h ) - h.z;
			return detail.a * strength * base.a * saturate( cube * nh + cube ) * g_vSpecularColor + min( saturate( irr * nl + irr ) * base.rgb * detail.rgb * 4, 1 );
		}

		float3 light = min( cube * base.a * strength + irr, 1 );
		float facing = dot( normalWs, toCamera );
		float nh2 = saturate( dot( n, h ) );
		nh2 *= nh2;
		return base.a * saturate( lerp( g_vSpecularColorGrazing, g_vSpecularColor, saturate( facing * facing ) ) ) * nh2 * nh2
			+ min( base.rgb * saturate( light * nl + light ) * detail.rgb * 4, 1 );
	}

	// Halo 2's atmospheric fog, from the scene's gradient fog (Halo2/ScenarioSceneLoader.cs BuildFog: start and end
	// distances, colour, the maximum density as its opacity). MCC fogs by view depth, from nothing at the start
	// distance to the maximum density at the opaque distance along a smoothstep (the 16 samples of its gradient
	// bitmap, read bilinearly), and blends in its gamma space frame buffer. It leaves the sky alone (g_flHaloSky).
	float g_flHaloSky < Default( 0.0 ); >;

	float HaloFogAmount( float3 positionWs )
	{
		if ( !g_bFogEnabled || !g_bGradientFogEnabled || g_flHaloSky > 0.5 )
			return 0;

		float depth = dot( positionWs - g_vCameraPositionWs, g_vCameraDirWs );
		float u = saturate( g_vGradientFogBiasAndScale.x + g_vGradientFogBiasAndScale.z * depth );
		return smoothstep( 0, 1, saturate( ( 16 * u - 0.5 ) / 15 ) ) * saturate( g_vGradientFogColor_Opacity.w );
	}

	// The fog colour back in Halo's gamma space (GradientFog takes it linear).
	float3 HaloFogColor()
	{
		float3 c = max( g_vGradientFogColor_Opacity.rgb, 0 );
		return lerp( 1.055 * pow( c, 1.0 / 2.4 ) - 0.055, c * 12.92, step( c, 0.0031308 ) );
	}

	// Halo 2's planar fog (Halo2/PlanarFog.cs; scratch research notes, planar_fog.md), drawn by MCC before its
	// atmospheric fog: u = view depth / opaque distance, v = depth below the plane / opaque depth, both world units;
	// the amount is the gradient bitmap's alpha at (u, v) (none above the plane) while the camera is above the plane,
	// blending toward its rgb (the smoothstep along u) as the camera sinks in, times the maximum density.
	// Plane (n, d), n·p = d on it, n up out of the fog; colour (gamma space) and maximum density (0: no planar fog);
	// opaque distance, opaque depth, world scale, 1 if it only draws with the camera below the plane.
	float4 g_vHaloPlanarFogPlane < Attribute( "HaloPlanarFogPlane" ); Default4( 0.0, 0.0, 1.0, 0.0 ); >;
	float4 g_vHaloPlanarFogColor < Attribute( "HaloPlanarFogColor" ); Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float4 g_vHaloPlanarFogDepths < Attribute( "HaloPlanarFogDepths" ); Default4( 1.0, 1.0, 103.2258, 0.0 ); >;
	Texture2D g_tHaloPlanarFogGradient < Attribute( "HaloPlanarFogGradient" ); SrgbRead( false ); >;
	SamplerState g_sHaloPlanarFogGradient < Filter( Bilinear ); AddressU( CLAMP ); AddressV( CLAMP ); >;

	float HaloPlanarFogAmount( float3 positionWs, float inFog )
	{
		float maxDensity = g_vHaloPlanarFogColor.w;
		if ( maxDensity <= 0 || g_flHaloSky > 0.5 || inFog < 0.5 )
			return 0;

		float scale = g_vHaloPlanarFogDepths.z;
		float3 n = g_vHaloPlanarFogPlane.xyz;
		float d = g_vHaloPlanarFogPlane.w;
		float eye = saturate( ( d - dot( n, g_vCameraPositionWs / scale ) ) / g_vHaloPlanarFogDepths.y );
		if ( g_vHaloPlanarFogDepths.w > 0.5 && eye <= 0 )
			return 0;

		float below = d - dot( n, positionWs / scale );
		if ( below <= 0 && eye <= 0 )
			return 0;

		float u = saturate( dot( positionWs - g_vCameraPositionWs, g_vCameraDirWs ) / scale / g_vHaloPlanarFogDepths.x );
		float v = saturate( below / g_vHaloPlanarFogDepths.y );
		float4 h = g_tHaloPlanarFogGradient.SampleLevel( g_sHaloPlanarFogGradient, float2( u, v ), 0 );
		return saturate( h.a * ( 1 - eye ) * maxDensity + h.b * eye * maxDensity );
	}

	// Halo 2's dynamic lights, added as its light passes add them after the self-illumination.
	// Per light: N.L times the albedo by the light's colour, plus the highlight of the shader's specular type (as the object pass has it, from the true eye vector) in its specular colours by the light's colour, masked by the gloss and min( 2 N.L, 1 ), clamped and attenuated.
	// s&box's lights stand in for Halo's: their colour is Halo's times pi (as the Halo CE mount has it), and a cookie is the light's gel.
	// A point light falls off as Halo's sphere lights do (HaloLightFalloff); a spot light by 1 - d^2/r^2 and its cone.
	// Diffuse contrast and the shadows Halo casts from dynamic lights are left out.
	// Halo 2's sphere light falloff, rasterizer\distance_attenuation along an axis: its texels at d / R = (i + 0.5) / 16, filtered linearly, 0 at the edge.
	static const float LightFalloff[16] = { 1.0, 0.9765, 0.9333, 0.8706, 0.7922, 0.7059, 0.6157, 0.5255, 0.4353, 0.3529, 0.2824, 0.2157, 0.1608, 0.1098, 0.0588, 0.0 };

	float HaloLightFalloff( float d )
	{
		float x = saturate( d ) * 16 - 0.5;
		if ( x <= 0 )
			return 1;

		int i = min( (int)x, 14 );
		return lerp( LightFalloff[i], LightFalloff[i + 1], saturate( x - i ) );
	}

	float3 DynamicLights( float3 positionWs, float4 positionSs, float3 n, float3 tangentWs, float3 binormalWs, float3 normalWs, float3 albedo, float gloss )
	{
		float3 toCameraWs = normalize( g_vCameraPositionWs - positionWs );
		float3 e = normalize( float3( dot( tangentWs, toCameraWs ), dot( binormalWs, toCameraWs ), dot( normalWs, toCameraWs ) ) );
		float ne = dot( n, e );
		float facing = saturate( ne );
		float3 specularColor = saturate( facing * g_vSpecularColor + ( 1 - facing ) * g_vSpecularColorGrazing );

		float3 sum = 0;
		ClusterRange range = Cluster::Query( ClusterItemType_Light, positionSs );

		[loop]
		for ( uint index = 0; index < range.Count; index++ )
		{
			BinnedLight data = DynamicLightConstantByIndex( Cluster::LoadItem( range, index ) );
			if ( data.Type == LightType::LightTypeDirectional || data.Radius <= 0 )
				continue;

			float3 ray = data.GetPosition() - positionWs;
			float distanceSquared = dot( ray, ray );
			float attenuation = data.IsSpotLight() ? saturate( 1 - distanceSquared / ( data.Radius * data.Radius ) ) : HaloLightFalloff( sqrt( distanceSquared ) / data.Radius );
			if ( attenuation <= 0 )
				continue;

			float3 toLightWs = ray * rsqrt( max( distanceSquared, 1e-8 ) );
			if ( data.IsSpotLight() )
				attenuation *= saturate( ( dot( toLightWs, -data.GetDirection() ) - data.SpotLightInnerOuterConeCosines.y ) * data.SpotLightInnerOuterConeCosines.z );

			float3 color = data.Color / 3.14159265;
			if ( data.HasLightCookie() )
				color *= data.SampleLightCookie( data.GetCookieUV( positionWs ) ).rgb;

			float3 l = normalize( float3( dot( tangentWs, toLightWs ), dot( binormalWs, toLightWs ), dot( normalWs, toLightWs ) ) );
			float nl = dot( n, l );
			float3 lit = data.IsDiffuseEnabled() ? saturate( nl ) * saturate( albedo * color ) : 0;
			if ( g_flDynamicSpecularType > 0.5 && data.IsSpecularEnabled() )
			{
				float s = saturate( 2 * ( nl * ne - 0.5 * dot( l, e ) ) );
				float s8 = pow( s, 8 );
				float k = g_flDynamicSpecularType < 1.5 ? s8 : g_flDynamicSpecularType < 2.5 ? s * s : max( 4 * ( s8 - 0.752941 ), 0 );
				lit += k * saturate( specularColor * color ) * min( 2 * saturate( nl ), 1 ) * gloss;
			}

			sum += attenuation * min( lit, 1 );
		}

		return sum;
	}

	// Shades with LOD0's or LOD1's modes (see g_vLod1ModesA).
	void UseLod( bool lod1 )
	{
		s_flAlbedoMode = lod1 ? g_vLod1ModesA.x : g_flAlbedoMode;
		s_flGlossMode = lod1 ? g_vLod1ModesA.y : g_flGlossMode;
		s_flSpecularGlossMode = lod1 ? g_vLod1ModesA.z : g_flSpecularGlossMode;
		s_flBumpMode = lod1 ? g_vLod1ModesA.w : g_flBumpMode;
		s_flEnvMode = lod1 ? g_vLod1ModesB.x : g_flEnvMode;
		s_flIllumMode = lod1 ? g_vLod1ModesB.y : g_flIllumMode;
		s_flAlphaTest = lod1 ? g_vLod1ModesB.z : g_flAlphaTest;
		s_flAlphaTestBump = lod1 ? g_vLod1ModesB.w : g_flAlphaTestBump;
		s_flGlossSquared = lod1 ? g_vLod1ModesC.x : g_flGlossSquared;
		s_flGlossGated = lod1 ? g_vLod1ModesC.y : g_flGlossGated;
		s_flClampU = lod1 ? g_vLod1ModesC.z : g_flClampU;
	}

	// What the editor's view modes show of the last shading: the albedo, the tangent space normal, the diffuse light.
	static float3 s_vShadedAlbedo;
	static float3 s_vShadedNormalTs;
	static float3 s_vShadedLight;

	// The surface's colour by the modes UseLod set, before the fog.
	float3 ShadeColor( PixelInput i )
	{
		float2 uv = Transform( i.vTextureCoords.xy, AnimatedTransform( 1, g_vBaseTransform ) );
		float4 base = SampleClampable( g_tBaseMap, uv );
		float4 bump = SampleClampable( g_tBumpMap, Transform( uv, AnimatedTransform( 5, g_vBumpTransform ) ) );

		// Only the maps this template uses (the rest are their neutral defaults).
		float albedoMode = s_flAlbedoMode;
		float glossMode = s_flGlossMode;
		float specularGlossMode = s_flSpecularGlossMode;
		bool usesGloss = glossMode > 0.5 || specularGlossMode > 0.5;
		bool usesDetailB = ( albedoMode > 1.5 && albedoMode < 4.5 ) || ( usesGloss && ( IsGloss( 6, glossMode, specularGlossMode ) || IsGloss( 7, glossMode, specularGlossMode ) || IsGloss( 11, glossMode, specularGlossMode ) ) );
		bool usesDetailC = ( albedoMode > 2.5 && albedoMode < 6.5 ) || ( usesGloss && ( IsGloss( 7, glossMode, specularGlossMode ) || IsGloss( 8, glossMode, specularGlossMode ) ) );
		bool usesSpecularMask = usesGloss && ( IsGloss( 9, glossMode, specularGlossMode ) || IsGloss( 10, glossMode, specularGlossMode ) || IsGloss( 11, glossMode, specularGlossMode ) );
		bool usesMask = s_flAlphaTest > 1.5 || IsGloss( 5, glossMode, specularGlossMode );

		float4 mask = usesMask ? SampleMap( g_tAlphaMap, uv, AnimatedTransform( 7, g_vAlphaTransform ) ) : float4( 1, 1, 1, 1 );

		if ( s_flAlphaTest > 0.5 )
		{
			float alpha = base.a;
			if ( s_flAlphaTest > 1.5 )
				alpha = ( s_flAlphaTestBump > 0.5 ? bump.a : 1 ) * SampleMap( g_tAlphaMapA, uv, AnimatedTransform( 6, g_vAlphaATransform ) ).a * mask.a;
			clip( round( alpha ) - 0.5 );
		}

		float4 detailA = albedoMode > 0.5 || usesGloss ? SampleMap( g_tDetailMap, uv, AnimatedTransform( 2, g_vDetailTransform ) ) : float4( 0.5, 0.5, 0.5, 1 );
		float4 detailB = usesDetailB ? SampleMap( g_tDetailMapB, uv, AnimatedTransform( 3, g_vDetailBTransform ) ) : float4( 0.5, 0.5, 0.5, 1 );
		float4 detailC = usesDetailC ? SampleMap( g_tDetailMapC, uv, AnimatedTransform( 4, g_vDetailCTransform ) ) : float4( 0.5, 0.5, 0.5, 1 );
		float4 specularMask = usesSpecularMask ? SampleMap( g_tSpecularMask, uv, AnimatedTransform( 8, g_vSpecularMaskTransform ) ) : float4( 1, 1, 1, 1 );

		float3 albedo = base.rgb;
		if ( s_flAlbedoMode > 7.5 )
		{
			// Objects' change colours (see ChangeColor), masked by the change colour map's channels.
			float4 m = SampleMap( g_tChangeColorMask, uv, AnimatedTransform( 14, g_vChangeColorMaskTransform ) );
			int objectIndex = ObjectIndex( i.vVertexColor.rgb );
			float3 cc1 = ChangeColor( 0, objectIndex );
			if ( s_flAlbedoMode < 8.5 )
				albedo = base.rgb * ChangeColorTint( m.rgb, cc1 );
			else if ( s_flAlbedoMode < 9.5 )
				albedo = min( 2 * ChangeColorTint( m.g, ChangeColor( 1, objectIndex ) ) * ChangeColorTint( m.r, cc1 ) * base.rgb * detailA.rgb, 1 );
			else
			{
				float3 cc34 = saturate( ChangeColorTint( m.b, ChangeColor( 2, objectIndex ) ) * ( m.a * ChangeColor( 3, objectIndex ) + 1 - m.a ) );
				albedo = min( 2 * saturate( cc34 * ChangeColorTint( m.r, cc1 ) * ChangeColorTint( m.g, ChangeColor( 1, objectIndex ) ) * base.rgb ) * detailA.rgb, 1 );
			}
		}
		else if ( s_flAlbedoMode > 6.5 )
			albedo = 0;
		else if ( s_flAlbedoMode > 5.5 )
			albedo = min( 2 * base.rgb * min( 2 * detailA.rgb * detailC.rgb, 1 ), 1 );
		else if ( s_flAlbedoMode > 4.5 )
			albedo = min( 2 * detailC.rgb * min( 2 * base.rgb * detailA.rgb, 1 ), 1 );
		else if ( s_flAlbedoMode > 3.5 )
		{
			float w1 = min( 2 * base.a, 1 );
			float w2 = max( 2 * base.a - 1, 0 );
			float3 detail = min( w2 * detailC.rgb + min( w1 * detailB.rgb + ( 1 - w1 ) * detailA.rgb, 1 ) * ( 1 - w2 ), 1 );
			albedo = min( 2 * base.rgb * detail, 1 );
		}
		else if ( s_flAlbedoMode > 1.5 )
		{
			float3 detail = min( lerp( detailA.rgb, detailB.rgb, base.a ), 1 );
			albedo = min( 2 * base.rgb * detail, 1 );
			if ( s_flAlbedoMode > 2.5 )
				albedo = min( 2 * albedo * detailC.rgb, 1 );
		}
		else if ( s_flAlbedoMode > 0.5 )
			albedo = min( 2 * base.rgb * detailA.rgb, 1 );
		albedo = saturate( albedo * g_vAlbedoMultiply + g_vAlbedoAdd );

		float gloss = Gloss( s_flGlossMode, base, bump, detailA, detailB, detailC, mask, specularMask );
		float specularGloss = s_flSpecularGlossMode < -0.5 ? gloss : Gloss( s_flSpecularGlossMode, base, bump, detailA, detailB, detailC, mask, specularMask );

		// Tangent space, where Halo does the lighting: the bump map's normal is used as stored (2x - 1, not
		// renormalised); the light direction and the eye vector are turned into it.
		float3 normalWs = normalize( i.vNormalWs );
		float3 tangentWs = normalize( i.vTangentUWs );
		float3 binormalWs = normalize( i.vTangentVWs );
		bool flat = s_flBumpMode > 0.5;
		bool flatPlain = flat && s_flBumpMode < 1.5;
		float3 n = flat ? float3( 0, 0, 1 ) : bump.xyz * 2 - 1;

		float3 incident = i.vIncidentLightWs;
		float d = saturate( length( incident ) );
		float3 l = d > 0.0001 ? normalize( float3( dot( tangentWs, incident ), dot( binormalWs, incident ), dot( normalWs, incident ) ) ) : float3( 0, 0, 1 );

		// The constant eye vector of MCC's level shaders: the camera's backward direction.
		float3 eyeWs = -g_vCameraDirWs.xyz;
		float3 e = float3( dot( tangentWs, eyeWs ), dot( binormalWs, eyeWs ), dot( normalWs, eyeWs ) );

		float3 lightmap = float3( 0.5, 0.5, 0.5 );
		if ( g_flLightmapMode > 1.5 )
			lightmap = saturate( i.vLightmapColor );
		else if ( g_flLightmapMode > 0.5 )
			lightmap = saturate( Tex2DS( g_tLightmap, g_sLightmap, i.vTextureCoords.zw ).rgb );

		// Bumped lightmap: lm + lm d (N.L - L.z), so a flat bump map leaves the lightmap as it is.
		float nl = s_flBumpMode > 1.5 ? clamp( 2 * l.z, -1, 1 ) : dot( n, l );
		float3 light = flatPlain ? lightmap : saturate( lightmap + lightmap * ( d * nl - d * l.z ) );

		// Objects: Halo 2's object lighting in place of the lightmap (see ObjectLight).
		HaloObjectLight objectLight = ObjectLighting( i.vVertexColor.rgb );
		if ( g_flObjectLit > 0.5 )
		{
			float3 objectNormalWs = normalize( n.x * tangentWs + n.y * binormalWs + n.z * normalWs );
			light = ObjectLight( objectLight, objectNormalWs, n.z );
		}

		float3 color = min( 2 * albedo * light, 1 );

		// Objects' specular from their direct light (see g_flObjectSpecularType), against the eye vector. Their
		// gloss: where the lightmap pass takes the base map's alpha, the object pass multiplies in the detail map's.
		float3 toCameraWs = normalize( -i.vPositionWithOffsetWs.xyz );
		float objectGloss = abs( s_flGlossMode - 1 ) < 0.5 ? base.a * detailA.a : specularGloss;
		if ( g_flObjectLit > 0.5 && g_flObjectSpecularType > 0.5 && s_flGlossMode > 0.5 )
		{
			float3 ld = normalize( float3( dot( tangentWs, objectLight.ToDirect ), dot( binormalWs, objectLight.ToDirect ), dot( normalWs, objectLight.ToDirect ) ) );
			float3 eo = i.vObjectEyeTs;
			float nd = dot( n, ld );
			float ne = dot( n, eo );
			float s = saturate( 2 * ( nd * ne - 0.5 * dot( ld, eo ) ) );
			float s8 = pow( s, 8 );
			float k = g_flObjectSpecularType < 1.5 ? s8 : g_flObjectSpecularType < 2.5 ? s * s : max( 4 * ( s8 - 0.753 ), 0 );
			float spec = min( 2 * k * objectGloss, 1 );
			float3 halfSpecular = saturate( objectLight.Direct * 0.5 * g_flLightmapSpecularBrightness );
			float facing = saturate( ne );
			float3 tint = saturate( 2 * saturate( facing * g_vSpecularColor + ( 1 - facing ) * g_vSpecularColorGrazing ) * halfSpecular );
			color += min( 2 * spec * tint, 1 ) * saturate( 8 * saturate( nd ) );
		}

		// Lightmap specular: R.E, R the light reflected about the bump normal, masked by the gloss.
		if ( g_flLightmapType > 0.5 && s_flGlossMode > 0.5 && !flatPlain )
		{
			float s = saturate( 2 * ( nl * dot( n, e ) - 0.5 * dot( l, e ) ) );
			float s8 = pow( s, 8 );
			float k = g_flLightmapType < 1.5 ? s8 : g_flLightmapType < 2.5 ? s * s : max( 2 * s8 - 1, 0 );

			float strength = specularGloss;
			if ( s_flGlossGated > 0.5 ) strength = max( ( 2 * d - 1 ) * strength, 0 );
			if ( s_flGlossSquared > 0.5 ) strength *= specularGloss;
			strength = saturate( strength * g_flLightmapSpecularBrightness );

			color += min( 2 * k * min( 2 * strength * saturate( 2 * lightmap * g_vSpecularColor ), 1 ), 1 );
		}

		// PRT objects: their spherical harmonic light instead (see PrtShading).
		if ( g_flPrtMode > 0.5 )
			color = PrtShading( i, base, detailA, albedo, n, tangentWs, binormalWs, normalWs );

		// The gloss mask the lightmap pass leaves in the frame's alpha, which reflections are scaled by: objects' by
		// their environment brightness, the luminance of their ambient and direct light.
		float glossMask = min( 2 * gloss * min( dot( light, 0.752941 ), 1 ), 1 );
		if ( g_flObjectLit > 0.5 )
			glossMask = saturate( 2 * saturate( dot( objectLight.Ambient + objectLight.Direct, float3( 0.299, 0.587, 0.114 ) ) ) * objectGloss );

		float3 toCamera = toCameraWs;
		if ( s_flEnvMode > 0.5 && s_flEnvMode < 1.5 )
		{
			// In the lightmap pass: the reflection off the unbumped surface, tinted and brightened between facing
			// the eye and grazing (Halo's fresnel, the other way round from a physical one: artists mostly make the
			// facing tint the brighter), masked by the gloss.
			float f = saturate( dot( n, e ) );
			float3 tint = saturate( lerp( g_vEnvParallel.rgb, g_vEnvPerpendicular.rgb, f ) );
			float brightness = saturate( lerp( g_vEnvParallel.a, g_vEnvPerpendicular.a, f ) );
			color += SampleCubeAtlas( reflect( -toCamera, normalWs ) ) * tint * brightness * glossMask;
		}

		color = saturate( color );

		if ( s_flEnvMode > 1.5 )
		{
			// A layer of its own, added onto the frame scaled by the gloss mask in its alpha.
			float3 env;
			if ( s_flEnvMode < 2.5 )
			{
				// Reflected off the bump map (its z not expanded); fresnel from the eye vector, with a sheen colour
				// at mid angles.
				float3 bumpedWs = ( bump.x * 2 - 1 ) * tangentWs + ( bump.y * 2 - 1 ) * binormalWs + bump.z * normalWs;
				float3 r = 2 * dot( bumpedWs, toCamera ) / max( dot( bumpedWs, bumpedWs ), 1e-6 ) * bumpedWs - toCamera;
				float f = saturate( dot( normalize( bumpedWs ), eyeWs ) );
				float x = saturate( 2 * g_flSheenWidth * min( f, 1 - f ) );
				float4 t = min( f * saturate( g_vEnvPerpendicular ) + ( 1 - f ) * saturate( g_vEnvParallel ), 1 );
				env = SampleCubeAtlas( r ) * t.a * saturate( ( 1 - x ) * t.rgb + x * g_vSheenColor );
			}
			else
			{
				// Off the unbumped surface; fresnel from the real view direction against the bump map.
				float3 v = float3( dot( tangentWs, toCamera ), dot( binormalWs, toCamera ), dot( normalWs, toCamera ) );
				float f = saturate( dot( v, bump.xyz * 2 - 1 ) );
				float4 t = lerp( saturate( g_vEnvParallel ), saturate( g_vEnvPerpendicular ), f );
				float3 reflection = SampleCubeAtlas( reflect( -toCamera, normalWs ) );
				if ( s_flEnvMode < 3.5 )
					env = reflection * min( t.a, 1 ) * min( t.rgb, 1 );
				else
				{
					// Outside the mask's colour, the reflection to the fourth: sharper and darker.
					float4 envMask = SampleMap( g_tEnvMask, uv, AnimatedTransform( 9, g_vEnvMaskTransform ) );
					float3 r2 = reflection * reflection;
					env = envMask.a * min( t.a, 1 ) * min( reflection * envMask.rgb + r2 * r2 * ( 1 - envMask.rgb ), 1 ) * min( t.rgb, 1 );
				}
			}

			color = saturate( color + env * glossMask );
		}

		if ( s_flIllumMode > 0.5 )
			color = saturate( color + SelfIllumination( uv ) );

		float3 positionWs = i.vPositionWithOffsetWs.xyz + g_vHighPrecisionLightingOffsetWs.xyz;
		if ( g_flHaloDynamicLights > 0.5 )
		{
			float lightGloss = s_flGlossMode < 0.5 ? 0 : g_flObjectLit > 0.5 ? objectGloss : specularGloss;
			color = saturate( color + DynamicLights( positionWs, i.vPositionSs, n, tangentWs, binormalWs, normalWs, albedo, lightGloss ) );
		}

		s_vShadedAlbedo = albedo;
		s_vShadedNormalTs = n;
		s_vShadedLight = light;
		return color;
	}

	float4 MainPs( PixelInput i ) : SV_Target0
	{
		// A camouflaged model stays out of the depth prepass: its depth there kept the level behind it out of the
		// frame copy it refracts, which then read black inside its own silhouette.
		#if HALO2_OPAQUE_CAMO && S_MODE_DEPTH
			discard;
		#endif

		// Level geometry fading between its LODs shades with both (the fade is the same over a whole part).
		float fade = i.vHaloLod;
		float3 color = 0;
		[branch] if ( fade > 0 )
		{
			UseLod( true );
			color = ShadeColor( i );
		}
		[branch] if ( fade < 1 )
		{
			UseLod( false );
			color = lerp( ShadeColor( i ), color, fade );
		}

		float3 normalWs = normalize( i.vNormalWs );
		float3 tangentWs = normalize( i.vTangentUWs );
		float3 binormalWs = normalize( i.vTangentVWs );
		float3 positionWs = i.vPositionWithOffsetWs.xyz + g_vHighPrecisionLightingOffsetWs.xyz;
		color = lerp( color, g_vHaloPlanarFogColor.rgb, HaloPlanarFogAmount( positionWs, i.vHaloInPlanarFog ) );
		color = lerp( color, HaloFogColor(), HaloFogAmount( positionWs ) );
		#if HALO2_OPAQUE_CAMO
			color = HaloCamouflage( i, color, normalWs, tangentWs, binormalWs );
		#endif
		float4 result = float4( SrgbGammaToLinear( color ), 1 );

		// The editor's view modes: the lightmap stands in for the diffuse lighting.
		if ( ToolsVis::WantsToolsVis() )
		{
			Material m = Material::From( i );
			float3 n = s_vShadedNormalTs;
			m.Albedo = SrgbGammaToLinear( s_vShadedAlbedo );
			m.Normal = normalize( n.x * tangentWs + n.y * binormalWs + n.z * normalWs );
			m.TangentNormal = normalize( n );
			m.Roughness = 1;
			m.Metalness = 0;
			m.AmbientOcclusion = 1;
			m.Opacity = 1;
			m.GeometricNormal = normalWs;

			LightingTerms_t lightingTerms = InitLightingTerms();
			lightingTerms.vDiffuse = SrgbGammaToLinear( saturate( 2.0 * s_vShadedLight ) );
			return ShadingModelStandard::DoToolsVis( result, m, lightingTerms );
		}

		return result;
	}
}
