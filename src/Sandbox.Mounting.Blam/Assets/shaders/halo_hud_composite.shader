//
// Lays the Halo HUD layer (halo_hud.shader: premultiplied gamma space color C and coverage A) over
// the frame the way Halo's HUD blend works, in gamma space: frame' = C + frame * (1 - A).
//
// The frame copy is read as linear (sRGB decoded), encoded to gamma for the blend and decoded
// again on output, with the same sRGB curve both ways so pixels the HUD doesn't touch come back
// unchanged.
//

HEADER
{
	Description = "Halo CE HUD layer composite, gamma space";
	DevShader = true;
}

MODES
{
	Default();
	Forward();
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
	float2 uv : TEXCOORD0;

	// VS only
	#if ( PROGRAM == VFX_PROGRAM_VS )
		float4 vPositionPs		: SV_Position;
	#endif

	// PS only
	#if ( ( PROGRAM == VFX_PROGRAM_PS ) )
		float4 vPositionSs		: SV_Position;
	#endif
};

VS
{
	PixelInput MainVs( VertexInput i )
	{
		PixelInput o;
		o.vPositionPs = float4( i.vPositionOs.xy, 0.0f, 1.0f );
		o.uv = i.vTexCoord;
		return o;
	}
}

PS
{
	#include "postprocess/common.hlsl"

	RenderState( BlendEnable, false );
	RenderState( DepthEnable, false );
	RenderState( DepthWriteEnable, false );

	SamplerState g_sHaloPoint < Filter( Point ); AddressU( CLAMP ); AddressV( CLAMP ); >;

	Texture2D g_tColorBuffer < Attribute( "ColorBuffer" ); SrgbRead( true ); >;
	Texture2D g_tHudLayer < Attribute( "HaloHudLayer" ); SrgbRead( false ); >;

	float3 HaloToGamma( float3 c )
	{
		c = max( c, 0 );
		return lerp( c * 12.92, 1.055 * pow( c, 1.0 / 2.4 ) - 0.055, step( 0.0031308, c ) );
	}

	float3 HaloToLinear( float3 c )
	{
		c = max( c, 0 );
		return lerp( c / 12.92, pow( ( c + 0.055 ) / 1.055, 2.4 ), step( 0.04045, c ) );
	}

	float4 MainPs( PixelInput i ) : SV_Target0
	{
		float4 frame = g_tColorBuffer.SampleLevel( g_sHaloPoint, i.uv, 0 );
		float4 hud = g_tHudLayer.SampleLevel( g_sHaloPoint, i.uv, 0 );

		// Nothing drawn here: the frame exactly as it was (HDR values above 1 included).
		if ( hud.a <= 0.0 && all( hud.rgb <= 0.0 ) )
			return frame;

		float3 gamma = saturate( hud.rgb + HaloToGamma( saturate( frame.rgb ) ) * ( 1.0 - saturate( hud.a ) ) );
		return float4( HaloToLinear( gamma ), frame.a );
	}
}
