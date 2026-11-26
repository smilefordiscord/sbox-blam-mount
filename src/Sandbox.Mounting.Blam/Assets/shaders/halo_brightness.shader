//
// MCC's gamma slider (MccBrightness.cs): an offset added to the finished frame in its gamma space, clamped.
//

HEADER
{
	Description = "MCC gamma slider";
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

	Texture2D g_tFrame < Attribute( "HaloBrightnessFrame" ); SrgbRead( false ); >;
	SamplerState g_sPoint < Filter( Point ); AddressU( CLAMP ); AddressV( CLAMP ); >;
	float g_flOffset < Attribute( "HaloBrightnessOffset" ); Default( 0.0 ); >;

	float3 GammaToLinear( float3 c )
	{
		c = max( c, 0 );
		return lerp( pow( ( c + 0.055 ) / 1.055, 2.4 ), c / 12.92, step( c, 0.04045 ) );
	}

	float3 LinearToGamma( float3 c )
	{
		c = saturate( c );
		return lerp( 1.055 * pow( c, 1.0 / 2.4 ) - 0.055, c * 12.92, step( c, 0.0031308 ) );
	}

	float4 MainPs( PixelInput i ) : SV_Target0
	{
		float4 frame = g_tFrame.SampleLevel( g_sPoint, i.vTexCoord, 0 );
		return float4( GammaToLinear( saturate( LinearToGamma( frame.rgb ) + g_flOffset ) ), frame.a );
	}
}
