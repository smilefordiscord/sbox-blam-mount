//
// Halo 2's patchy fog (Halo2/PatchyFog.cs; scratch research notes, patchy_fog.md section 2.3-2.4): MCC's two passes in
// one. Its four noise layers, each n_k = dot( noise_k( uv_k ).rgb, weights_k ) with uv_k = U_k/V_k rows applied to
// the screen in -1..1 (y up), combined as N = 1 - (1 - n0)(1 - n1)(1 - n2)(1 - n3), or with separate layer depths
// N = sum of n_k max( 0, F - sat( u_k - 0.5 ) ) (u_k the layer's place in the depth range); then
//   F = sat( ( view depth - near ) / range )      (the sky at the far plane)
//   frame = lerp( frame, colour, F lerp( min density, max density, N ) )   in Halo's gamma space.
// Drawn as a blit over the frame grabbed just before it, which is written back lerped.
//

HEADER
{
	Description = "Halo 2 patchy fog";
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
	Texture2D g_tNoise0 < Attribute( "HaloPatchyNoise0" ); SrgbRead( false ); >;
	Texture2D g_tNoise1 < Attribute( "HaloPatchyNoise1" ); SrgbRead( false ); >;
	Texture2D g_tNoise2 < Attribute( "HaloPatchyNoise2" ); SrgbRead( false ); >;
	Texture2D g_tNoise3 < Attribute( "HaloPatchyNoise3" ); SrgbRead( false ); >;
	SamplerState g_sWrap < Filter( Bilinear ); AddressU( WRAP ); AddressV( WRAP ); >;
	SamplerState g_sPoint < Filter( Point ); AddressU( CLAMP ); AddressV( CLAMP ); >;

	// Per layer: the uv rows (x, y, offset), the channel weights (rgb) and the layer's place in the depth range (w).
	float4 g_vU0 < Attribute( "HaloPatchyU0" ); >;
	float4 g_vV0 < Attribute( "HaloPatchyV0" ); >;
	float4 g_vU1 < Attribute( "HaloPatchyU1" ); >;
	float4 g_vV1 < Attribute( "HaloPatchyV1" ); >;
	float4 g_vU2 < Attribute( "HaloPatchyU2" ); >;
	float4 g_vV2 < Attribute( "HaloPatchyV2" ); >;
	float4 g_vU3 < Attribute( "HaloPatchyU3" ); >;
	float4 g_vV3 < Attribute( "HaloPatchyV3" ); >;
	float4 g_vLayer0 < Attribute( "HaloPatchyLayer0" ); >;
	float4 g_vLayer1 < Attribute( "HaloPatchyLayer1" ); >;
	float4 g_vLayer2 < Attribute( "HaloPatchyLayer2" ); >;
	float4 g_vLayer3 < Attribute( "HaloPatchyLayer3" ); >;

	// Colour (gamma space), 1 for separate layer depths; min and max density, near and range (s&box units).
	float4 g_vColor < Attribute( "HaloPatchyColor" ); >;
	float4 g_vDensity < Attribute( "HaloPatchyDensity" ); >;

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

	float Layer( Texture2D noise, float4 u, float4 v, float4 weights, float2 s )
	{
		float2 uv = float2( dot( float3( s, 1 ), u.xyz ), dot( float3( s, 1 ), v.xyz ) );
		return saturate( dot( noise.Sample( g_sWrap, uv ).rgb, weights.xyz ) );
	}

	float4 MainPs( PixelInput i ) : SV_Target0
	{
		float2 pixel = i.vPositionSs.xy;
		float2 screen = ( pixel - g_vViewportOffset ) * g_vInvViewportSize;
		float3 behind = g_tFrame.SampleLevel( g_sPoint, screen, 0 ).rgb;

		// Nothing drawn there (the sky's layers don't write depth): as far as it gets.
		float raw = Depth::Get( pixel );
		float depth = raw > 0 ? Depth::GetLinear( pixel ) : 1e9;
		float f = saturate( ( depth - g_vDensity.z ) / g_vDensity.w );
		if ( f <= 0 )
			return float4( behind, 1 );

		float2 s = float2( 2 * screen.x - 1, 1 - 2 * screen.y );
		float n0 = Layer( g_tNoise0, g_vU0, g_vV0, g_vLayer0, s );
		float n1 = Layer( g_tNoise1, g_vU1, g_vV1, g_vLayer1, s );
		float n2 = Layer( g_tNoise2, g_vU2, g_vV2, g_vLayer2, s );
		float n3 = Layer( g_tNoise3, g_vU3, g_vV3, g_vLayer3, s );

		float n;
		if ( g_vColor.w > 0.5 )
		{
			float4 depths = saturate( float4( g_vLayer0.w, g_vLayer1.w, g_vLayer2.w, g_vLayer3.w ) );
			n = dot( float4( n0, n1, n2, n3 ), max( 0, f - saturate( depths - 0.5 ) ) );
		}
		else
		{
			n = 1 - ( 1 - n0 ) * ( 1 - n1 ) * ( 1 - n2 ) * ( 1 - n3 );
		}

		float amount = saturate( f * lerp( g_vDensity.x, g_vDensity.y, saturate( n ) ) );
		float3 result = lerp( LinearToGamma( saturate( behind ) ), g_vColor.rgb, amount );
		return float4( GammaToLinear( result ) + max( behind - saturate( behind ), 0 ), 1 );
	}
}
