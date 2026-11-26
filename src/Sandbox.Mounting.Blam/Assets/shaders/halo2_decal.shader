//
// Halo 2 decals (Halo2/DecalModels.cs; scratch research notes, decals.md and decorators.md): scenario decals (signs,
// stripes, stains) laid onto the level's collision surfaces, and decorators' floating decals. Unlit, as MCC draws them:
// the bitmap times the decal's colour, blended over the lit level in one of Halo's decal layers.
//
// Halo blends in its gamma space frame buffer; here each mode's result is taken to linear first (exact for max, close
// for the rest where a decal is opaque, which signs mostly are):
//   F_DECAL_MODE 0 alpha blend: c = min( k t.rgb colour, 1 ), a = t.a alpha (k = g_flColorScale: 1 for decals, 2 for
//                  decorators' decal_paint, whose colour is tint times lightmap)
//                1 double multiply: frame times 2 lerp( 0.5, t.rgb, t.a alpha )
//                2 multiply: frame times lerp( 1, t.rgb, alpha ) (times t.a too unless g_flTextureAlpha is 0: decorators'
//                  decal_paint_multiply ignores it)
//                3 max: max( frame, t.rgb colour )
//                4 add: frame plus t.rgb colour t.a alpha
// MCC draws decals before its fog passes, so each is fogged as the surface under it would be.
// Decorators' decals fade out with distance (g_flFadeK, per s&box unit: 3 - k distance; 0 for none).
//
// Vertex: position (world, just off the surface); TexCoord0 = uv; TexCoord4 = colour (rgb gamma, alpha).
//

HEADER
{
	Description = "Halo 2 decal";
	DevShader = true;
}

FEATURES
{
	#include "common/features.hlsl"
	Feature( F_DECAL_MODE, 0..4( 0="Alpha Blend", 1="Double Multiply", 2="Multiply", 3="Max", 4="Add" ), "Blending" );
}

MODES
{
	Forward();
}

COMMON
{
	#define S_TRANSLUCENT 1
	#define BLEND_MODE_ALREADY_SET 1
	#include "common/shared.hlsl"

	#define CUSTOM_MATERIAL_INPUTS
}

struct VertexInput
{
	float4 vDecalColor : TEXCOORD4 < Semantic( None ); >;
	#include "common/vertexinput.hlsl"
};

struct PixelInput
{
	float4 vDecalColor : TEXCOORD14;
	#include "common/pixelinput.hlsl"
};

VS
{
	#include "common/vertex.hlsl"

	float g_flFadeK < Default( 0.0 ); >;

	PixelInput MainVs( VertexInput v )
	{
		// Halo's depth bias: a little toward the camera, more with distance.
		float3 toCamera = g_vCameraPositionWs - v.vPositionOs;
		float distance = length( toCamera );
		v.vPositionOs += toCamera / max( distance, 1e-3 ) * ( 0.25 + 0.0008 * distance );

		PixelInput i = ProcessVertex( v );
		i.vDecalColor = v.vDecalColor;
		i.vDecalColor.a *= g_flFadeK > 0 ? saturate( 3 - g_flFadeK * distance ) : 1;
		return FinalizeVertex( i );
	}
}

PS
{
	#include "common/pixel.hlsl"

	StaticCombo( S_DECAL_MODE, F_DECAL_MODE, Sys( ALL ) );

	RenderState( CullMode, NONE );
	RenderState( DepthWriteEnable, false );
	RenderState( BlendEnable, true );
	RenderState( SrcBlendAlpha, ZERO );
	RenderState( DstBlendAlpha, ONE );

	#if S_DECAL_MODE == 0
		RenderState( BlendOp, ADD );
		RenderState( SrcBlend, ONE );
		RenderState( DstBlend, INV_SRC_ALPHA );
	#elif S_DECAL_MODE == 1 || S_DECAL_MODE == 2
		RenderState( BlendOp, ADD );
		RenderState( SrcBlend, DEST_COLOR );
		RenderState( DstBlend, ZERO );
	#elif S_DECAL_MODE == 3
		RenderState( BlendOp, MAX );
		RenderState( SrcBlend, ONE );
		RenderState( DstBlend, ONE );
	#else
		RenderState( BlendOp, ADD );
		RenderState( SrcBlend, ONE );
		RenderState( DstBlend, ONE );
	#endif

	SamplerState g_sClamp < Filter( TRILINEAR ); AddressU( CLAMP ); AddressV( CLAMP ); >;

	CreateInputTexture2D( Map, Linear, 8, "None", "_color", ",0/,0/0", Default4( 1.00, 1.00, 1.00, 1.00 ) );
	Texture2D g_tMap < Channel( RGBA, Box( Map ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( false ); >;

	float g_flColorScale < Default( 1.0 ); >;
	float g_flTextureAlpha < Default( 1.0 ); >;

	// Halo 2's atmospheric fog, as in halo2_opaque.shader.
	float HaloFogAmount( float3 positionWs )
	{
		if ( !g_bFogEnabled || !g_bGradientFogEnabled )
			return 0;

		float depth = dot( positionWs - g_vCameraPositionWs, g_vCameraDirWs );
		float u = saturate( g_vGradientFogBiasAndScale.x + g_vGradientFogBiasAndScale.z * depth );
		return smoothstep( 0, 1, saturate( ( 16 * u - 0.5 ) / 15 ) ) * saturate( g_vGradientFogColor_Opacity.w );
	}

	float3 HaloFogColor()
	{
		float3 c = max( g_vGradientFogColor_Opacity.rgb, 0 );
		return lerp( 1.055 * pow( c, 1.0 / 2.4 ) - 0.055, c * 12.92, step( c, 0.0031308 ) );
	}

	float4 MainPs( PixelInput i ) : SV_Target0
	{
		float4 t = saturate( g_tMap.Sample( g_sClamp, i.vTextureCoords.xy ) );
		float3 colour = saturate( i.vDecalColor.rgb );
		float alpha = saturate( i.vDecalColor.a );

		float3 positionWs = i.vPositionWithOffsetWs.xyz + g_vHighPrecisionLightingOffsetWs.xyz;
		float fog = HaloFogAmount( positionWs );

		#if S_DECAL_MODE == 0
			float a = t.a * alpha;
			float3 c = lerp( min( g_flColorScale * t.rgb * colour, 1 ), HaloFogColor(), fog );
			return float4( SrgbGammaToLinear( c ) * a, a );
		#elif S_DECAL_MODE == 1
			// 2 lerp( 0.5, t, a ) in gamma space is ( 2 lerp( 0.5, t, a ) )^2.2 in linear; fog pulls it to 1.
			float3 m = 2 * lerp( 0.5, t.rgb, t.a * alpha );
			return float4( pow( max( lerp( m, 1, fog ), 0 ), 2.2 ), 0 );
		#elif S_DECAL_MODE == 2
			float a = alpha * ( g_flTextureAlpha > 0.5 ? t.a : 1 );
			float3 m = lerp( 1, t.rgb, a );
			return float4( pow( max( lerp( m, 1, fog ), 0 ), 2.2 ), 0 );
		#elif S_DECAL_MODE == 3
			return float4( SrgbGammaToLinear( t.rgb * colour * ( 1 - fog ) ), 0 );
		#else
			return float4( SrgbGammaToLinear( t.rgb * colour * t.a * alpha ) * ( 1 - fog ), 0 );
		#endif
	}
}
