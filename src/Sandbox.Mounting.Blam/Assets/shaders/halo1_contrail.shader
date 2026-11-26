//
// Halo CE contrails (cont): the ribbons behind tracers, plasma bolts, rockets and needles, drawn
// by s&box's TrailRenderer with this as its material.
//
// The trail object builds the ribbon (in the world, facing the camera) and hands each vertex its
// color from the trail's gradient, which here is Halo's point state color in gamma space, and a
// texture coordinate along the trail (u) and across it (v). Halo's contrail is its bitmap times
// that color (or with a nonlinear tint, (1 - color) x bitmap^4 + color x bitmap per channel: white
// where the bitmap is bright), blended by the contrail's framebuffer blend function in Halo's gamma
// space frame buffer. Alpha blended and additive contrails are blended against the frame buffer
// copy in gamma space and output so the hardware blend lands on that, as halo1_sprite.shader does;
// the other blend functions are done in linear space.
//
// The vertex input matches s&box's line.shader, whose vertex buffer the trail object fills.
//

HEADER
{
	Description = "Halo CE contrail";
}

FEATURES
{
	#include "common/features.hlsl"

	// Halo's framebuffer blend function: alpha blend, multiply, double multiply, add, subtract,
	// component min, component max, alpha-multiply add.
	Feature( F_BLEND_MODE, 0..7( 0="Alpha Blend", 1="Multiply", 2="Double Multiply", 3="Add", 4="Subtract", 5="Component Min", 6="Component Max", 7="Alpha-Multiply Add" ), "Blending" );
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
	float3 WorldPosition : POSITION < Semantic( None ); >;
	float3 Normal : NORMAL < Semantic( None ); >;
	float3 Tangent : Tangent < Semantic( None ); >;
	float4 Color : COLOR0 < Semantic( None ); >;
	float2 TextureCoords : TEXCOORD0 < Semantic( None ); >;
};

struct PixelInput
{
	float4 vPositionPs : SV_Position;
	float2 vUv : TEXCOORD0;
	float4 vColor : TEXCOORD1;
	float3 vPositionWs : TEXCOORD2;
};

VS
{
	// Texture repeats across the trail (Halo's v repeats; u comes from the trail's texturing).
	float g_flContrailRepeatsV < Default( 1.0 ); >;

	PixelInput MainVs( const VertexInput v )
	{
		PixelInput o;
		o.vPositionPs = Position3WsToPs( v.WorldPosition );
		o.vPositionWs = v.WorldPosition;
		o.vColor = v.Color;
		o.vUv = float2( v.TextureCoords.x, v.TextureCoords.y * g_flContrailRepeatsV );
		return o;
	}
}

PS
{
	#include "common/pixel.hlsl"

	StaticCombo( S_BLEND_MODE, F_BLEND_MODE, Sys( ALL ) );

	RenderState( CullMode, NONE );
	RenderState( DepthWriteEnable, false );
	RenderState( BlendEnable, true );
	RenderState( SrcBlendAlpha, ZERO );
	RenderState( DstBlendAlpha, ONE );

	#if S_BLEND_MODE == 0
		// Premultiplied (see halo1_sprite.shader).
		RenderState( SrcBlend, ONE );
		RenderState( DstBlend, INV_SRC_ALPHA );
		RenderState( BlendOp, ADD );
	#elif S_BLEND_MODE == 1
		RenderState( SrcBlend, DEST_COLOR );
		RenderState( DstBlend, ZERO );
		RenderState( BlendOp, ADD );
	#elif S_BLEND_MODE == 2
		RenderState( SrcBlend, DEST_COLOR );
		RenderState( DstBlend, SRC_COLOR );
		RenderState( BlendOp, ADD );
	#elif S_BLEND_MODE == 3
		RenderState( SrcBlend, ONE );
		RenderState( DstBlend, ONE );
		RenderState( BlendOp, ADD );
	#elif S_BLEND_MODE == 4
		RenderState( SrcBlend, ONE );
		RenderState( DstBlend, ONE );
		RenderState( BlendOp, REV_SUBTRACT );
	#elif S_BLEND_MODE == 5
		RenderState( SrcBlend, ONE );
		RenderState( DstBlend, ONE );
		RenderState( BlendOp, MIN );
	#elif S_BLEND_MODE == 6
		RenderState( SrcBlend, ONE );
		RenderState( DstBlend, ONE );
		RenderState( BlendOp, MAX );
	#else
		// Add and alpha-multiply add: the increase over what's behind.
		RenderState( SrcBlend, ONE );
		RenderState( DstBlend, ONE );
		RenderState( BlendOp, ADD );
	#endif

	#if S_BLEND_MODE == 0 || S_BLEND_MODE == 3 || S_BLEND_MODE == 7
		BoolAttribute( bWantsFBCopyTexture, true );
	#endif

	// Along the trail the texture repeats; across it, it doesn't.
	SamplerState g_sContrail < Filter( Bilinear ); AddressU( WRAP ); AddressV( CLAMP ); >;
	SamplerState g_sBehind < Filter( Point ); AddressU( CLAMP ); AddressV( CLAMP ); >;

	CreateInputTexture2D( Contrail, Linear, 8, "None", "_color", ",0/,0/0", Default4( 1.00, 1.00, 1.00, 1.00 ) );
	Texture2D g_tContrail < Channel( RGBA, Box( Contrail ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( false ); >;
	Texture2D g_tFrameBufferCopyTexture < Attribute( "FrameBufferCopyTexture" ); SrgbRead( false ); >;

	// A full resolution frame its drawer grabbed (HaloContrailGrabbed 1), used over the engine's frame buffer copy, which doesn't line up with the frame pixel for pixel.
	Texture2D g_tHaloContrailBehind < Attribute( "HaloContrailBehind" ); SrgbRead( false ); >;
	float g_flHaloContrailGrabbed < Attribute( "HaloContrailGrabbed" ); Default( 0.0 ); >;

	// 1: Halo's nonlinear tint (see the top).
	float g_flNonlinearTint < Default( 0.0 ); >;

	// The sRGB curve both ways, so what's behind comes back unchanged where nothing is drawn.
	float3 GammaToLinear( float3 c )
	{
		c = max( c, 0 );
		return lerp( pow( ( c + 0.055 ) / 1.055, 2.4 ), c / 12.92, step( c, 0.04045 ) );
	}

	float3 LinearToGamma( float3 c )
	{
		c = max( c, 0 );
		return lerp( 1.055 * pow( c, 1.0 / 2.4 ) - 0.055, c * 12.92, step( c, 0.0031308 ) );
	}

	float3 Behind( float2 pixel )
	{
		if ( g_flHaloContrailGrabbed > 0.5 )
			return saturate( g_tHaloContrailBehind.SampleLevel( g_sBehind, ( pixel - g_vViewportOffset ) * g_vInvViewportSize, 0 ).rgb );

		float2 uv = pixel * g_vInvViewportSize * g_vFrameBufferCopyInvSizeAndUvScale.zw;
		return saturate( g_tFrameBufferCopyTexture.SampleLevel( g_sBehind, uv, 0 ).rgb );
	}

	float4 MainPs( PixelInput i ) : SV_Target0
	{
		float4 map = Tex2DS( g_tContrail, g_sContrail, i.vUv );
		float3 tint = saturate( i.vColor.rgb );
		float4 color;
		if ( g_flNonlinearTint > 0.5 )
		{
			float3 map2 = map.rgb * map.rgb;
			color = float4( ( 1 - tint ) * map2 * map2 + tint * map.rgb, map.a * i.vColor.a );
		}
		else
		{
			color = map * i.vColor;
		}

		// Contrails that add light fade out with the fog in front of them.
		float fade = 1;
		#if S_BLEND_MODE == 3 || S_BLEND_MODE == 6 || S_BLEND_MODE == 7
			// Halo faded added light by 1 - its fog density in gamma space; s&box's transmittance is 1 - density^2.2 (ScenarioSceneLoader.BuildFog), far too little.
			fade *= ( 1 - pow( saturate( 1 - DoAtmospherics( i.vPositionWs, i.vPositionPs.xy, float4( 0, 0, 0, 1 ), true ).a ), 1.0 / 2.2 ) );
		#endif

		float3 rgb = GammaToLinear( saturate( color.rgb ) );
		float coverage = saturate( color.a );
		#if S_BLEND_MODE == 0
			// Fogged, then laid over what's behind in gamma space (see halo1_sprite.shader).
			float3 fogged = LinearToGamma( DoAtmospherics( i.vPositionWs, i.vPositionPs.xy, float4( rgb, 1 ) ).rgb );
			float3 behind = Behind( i.vPositionPs.xy );
			float3 result = GammaToLinear( lerp( LinearToGamma( behind ), fogged, coverage ) );
			float3 ratio = result / max( behind, 0.0001 );
			float a = saturate( max( coverage, 1 - min( ratio.r, min( ratio.g, ratio.b ) ) ) );
			return float4( max( result - behind * ( 1 - a ), 0 ), a );
		#elif S_BLEND_MODE == 3 || S_BLEND_MODE == 7
			// The point color's alpha fades additive contrails too (their ends).
			float3 behind = Behind( i.vPositionPs.xy );
			float3 result = GammaToLinear( saturate( LinearToGamma( behind ) + saturate( color.rgb ) * coverage * fade ) );
			return float4( max( result - behind, 0 ), 0 );
		#elif S_BLEND_MODE == 1
			return float4( lerp( 1.0, rgb, color.a ), 1 );
		#elif S_BLEND_MODE == 2
			return float4( lerp( 0.5, 0.5 * pow( 2.0 * max( color.rgb, 0 ), 2.2 ), color.a ), 1 );
		#elif S_BLEND_MODE == 5
			return float4( lerp( 65504.0, rgb, color.a ), 1 );
		#else
			return float4( rgb * coverage * fade, 1 );
		#endif
	}
}
