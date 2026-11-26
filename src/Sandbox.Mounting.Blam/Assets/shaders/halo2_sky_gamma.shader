//
// Halo 2's sky in Halo's gamma space (Halo2/SkyGamma.cs): Halo draws the sky's layers one over another into its gamma
// space frame buffer, blending and adding there. Just before the first layer this takes the frame to gamma space, the
// layers then write gamma space colours and blend as Halo's do, and just after the last it takes the frame back.
// Drawn as a blit over the frame grabbed just before it; s&box's sRGB curve both ways, extended past 1 so the round
// trip leaves bright pixels as they were.
//

HEADER
{
	Description = "Halo 2 sky gamma space";
	DevShader = true;
}

MODES
{
	Default();
	Forward();
}

FEATURES
{
}

COMMON
{
	#include "postprocess/shared.hlsl"
}

struct VertexInput
{
	float3 vPositionOs : POSITION < Semantic( PosXyz ); >;
	float2 vTexCoord : TEXCOORD0 < Semantic( LowPrecisionUv ); >;
};

struct PixelInput
{
	float2 vTexCoord : TEXCOORD0;

	#if ( PROGRAM == VFX_PROGRAM_VS )
		float4 vPositionPs : SV_Position;
	#endif

	#if ( PROGRAM == VFX_PROGRAM_PS )
		float4 vPositionSs : SV_Position;
	#endif
};

VS
{
	PixelInput MainVs( VertexInput i )
	{
		PixelInput o;
		o.vPositionPs = float4( i.vPositionOs.xy, 0.0f, 1.0f );
		o.vTexCoord = i.vTexCoord;
		return o;
	}
}

PS
{
	#include "postprocess/common.hlsl"

	RenderState( DepthEnable, false );
	RenderState( DepthWriteEnable, false );
	RenderState( BlendEnable, false );

	Texture2D g_tFrame < Attribute( "HaloSkyGammaFrame" ); SrgbRead( false ); >;
	SamplerState g_sPoint < Filter( Point ); AddressU( CLAMP ); AddressV( CLAMP ); >;

	// 1 to take the frame back to linear, 0 to take it to gamma space.
	float g_flToLinear < Attribute( "HaloSkyGammaToLinear" ); Default( 0.0 ); >;

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

	float4 MainPs( PixelInput i ) : SV_Target0
	{
		float2 screen = ( i.vPositionSs.xy - g_vViewportOffset ) * g_vInvViewportSize;
		float4 frame = g_tFrame.SampleLevel( g_sPoint, screen, 0 );
		return float4( g_flToLinear > 0.5 ? GammaToLinear( frame.rgb ) : LinearToGamma( frame.rgb ), frame.a );
	}
}
