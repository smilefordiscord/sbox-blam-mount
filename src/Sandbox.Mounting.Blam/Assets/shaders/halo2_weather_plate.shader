//
// A Halo 2 weather background plate (Halo2/Weather.cs; scratch research notes, weather.md section 3): three scrolling
// texture layers whose alpha, combined, lays the plate's tint over everything from one view depth (none) to another
// (all), the sky included. MCC's pixel shader for it:
//   fade = sat( ( view depth - near ) / ( far - near ) )        (packed depth; the sky is at the far plane)
//   cover = 1 - (1 - A0 f0)(1 - A1 f1)(1 - A2 f2)               (A = layer alpha, f = layer opacity)
//   frame = lerp( frame, tint, cover * fade )                    (in its gamma space frame buffer)
// Each layer's uv is S (screen u, v) + offset, the same S across and down the screen (so the textures stretch with
// the aspect ratio, as in MCC), S the layer's texture scale times its plane's height on screen when the plate
// scales automatically. Drawn as a blit over the frame grabbed just before it, which is written back lerped.
//

HEADER
{
	Description = "Halo 2 weather plate";
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
	#include "common/classes/Depth.hlsl"

	RenderState( DepthEnable, false );
	RenderState( DepthWriteEnable, false );
	RenderState( BlendEnable, false );

	Texture2D g_tFrame < Attribute( "HaloWeatherFrame" ); SrgbRead( false ); >;
	Texture2D g_tLayer0 < Attribute( "HaloWeatherLayer0" ); SrgbRead( false ); >;
	Texture2D g_tLayer1 < Attribute( "HaloWeatherLayer1" ); SrgbRead( false ); >;
	Texture2D g_tLayer2 < Attribute( "HaloWeatherLayer2" ); SrgbRead( false ); >;
	SamplerState g_sWrap < Filter( Anisotropic ); AddressU( WRAP ); AddressV( WRAP ); >;
	SamplerState g_sPoint < Filter( Point ); AddressU( CLAMP ); AddressV( CLAMP ); >;

	// Per layer: texture scale, plane depth (world units; 0 = not scaled by the plane), offset u, v (scaled).
	float4 g_vUv0 < Attribute( "HaloWeatherUv0" ); >;
	float4 g_vUv1 < Attribute( "HaloWeatherUv1" ); >;
	float4 g_vUv2 < Attribute( "HaloWeatherUv2" ); >;
	float4 g_vOpacity < Attribute( "HaloWeatherOpacity" ); >;
	float4 g_vTint < Attribute( "HaloWeatherTint" ); >;

	// View depth where the plate starts, and over how far it comes in (s&box units).
	float4 g_vDepthBlend < Attribute( "HaloWeatherDepthBlend" ); >;

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

	float LayerAlpha( Texture2D layer, float4 uv, float2 screen, float planeHeight )
	{
		float scale = uv.x * ( uv.y > 0 ? planeHeight * uv.y : 1 );
		return saturate( layer.Sample( g_sWrap, screen * scale + uv.zw ).a );
	}

	float4 MainPs( PixelInput i ) : SV_Target0
	{
		float2 pixel = i.vPositionSs.xy;
		float2 screen = ( pixel - g_vViewportOffset ) * g_vInvViewportSize;
		float3 behind = g_tFrame.SampleLevel( g_sPoint, screen, 0 ).rgb;

		// Nothing drawn there (the sky's layers don't write depth): as far as it gets.
		float raw = Depth::Get( pixel );
		float depth = raw > 0 ? Depth::GetLinear( pixel ) : 1e9;
		float fade = saturate( ( depth - g_vDepthBlend.x ) / g_vDepthBlend.y );
		if ( fade <= 0 )
			return float4( behind, 1 );

		// The planes' height on screen per world unit of depth: 2 tan( vfov / 2 ).
		float planeHeight = 2.0 / g_matViewToProjection[1][1];
		float a0 = LayerAlpha( g_tLayer0, g_vUv0, screen, planeHeight ) * g_vOpacity.x;
		float a1 = LayerAlpha( g_tLayer1, g_vUv1, screen, planeHeight ) * g_vOpacity.y;
		float a2 = LayerAlpha( g_tLayer2, g_vUv2, screen, planeHeight ) * g_vOpacity.z;
		float cover = 1 - ( 1 - a0 ) * ( 1 - a1 ) * ( 1 - a2 );

		float3 frame = LinearToGamma( saturate( behind ) );
		float3 result = lerp( frame, g_vTint.rgb, cover * fade );
		return float4( GammaToLinear( result ) + max( behind - saturate( behind ), 0 ), 1 );
	}
}
