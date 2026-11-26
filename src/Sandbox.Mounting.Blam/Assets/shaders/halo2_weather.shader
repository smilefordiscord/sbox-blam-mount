//
// Halo 2 weather particles (snowflakes; Halo2/Weather.cs, scratch research notes, weather.md section 2.4): a camera
// facing quad per particle, sized in pixels by its view depth as MCC sizes them, 1000 · size · H / (480 · depth in
// world units) pixels across for a viewport H pixels tall, clamped to H/480..64 H/480. The sprite times the
// particle's alpha is alpha blended over the frame in Halo's gamma space (MCC's frame buffer): the frame grabbed
// before the flakes is lerped toward the sprite there and the difference added.
//
// Vertex: position = particle centre (world), TexCoord0.xy = corner (0-1, also the uv), TexCoord1 = (size, alpha).
//

HEADER
{
	Description = "Halo 2 weather particles";
	DevShader = true;
}

FEATURES
{
	#include "common/features.hlsl"
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
	float3 vPosition : POSITION < Semantic( None ); >;
	float4 vColor : COLOR0 < Semantic( None ); >;
	float4 vCorner : TEXCOORD0 < Semantic( None ); >;
	float4 vParticle : TEXCOORD1 < Semantic( None ); >;
};

struct PixelInput
{
	float4 vPositionPs : SV_Position;
	float2 vUv : TEXCOORD0;
	float vAlpha : TEXCOORD1;
};

VS
{
	float g_flWorldScale < Attribute( "HaloWorldScale" ); Default( 103.2258 ); >;

	PixelInput MainVs( VertexInput v )
	{
		PixelInput o = (PixelInput)0;
		float4 clip = Position3WsToPs( v.vPosition );
		float height = 1.0 / g_vInvViewportSize.y;
		float depth = max( clip.w / g_flWorldScale, 0.1 );
		float pixels = clamp( 1000.0 * v.vParticle.x * height / ( 480.0 * depth ), height / 480.0, 64.0 * height / 480.0 );

		// pixels across: half of it each side, 2/W of NDC per pixel.
		float2 corner = v.vCorner.xy;
		clip.xy += float2( 2 * corner.x - 1, 1 - 2 * corner.y ) * pixels * g_vInvViewportSize * clip.w;

		o.vPositionPs = clip;
		o.vUv = corner;
		o.vAlpha = v.vParticle.y * v.vColor.a;
		return o;
	}
}

PS
{
	RenderState( CullMode, NONE );
	RenderState( DepthEnable, true );
	RenderState( DepthWriteEnable, false );
	RenderState( BlendEnable, true );
	RenderState( BlendOp, ADD );
	RenderState( SrcBlend, ONE );
	RenderState( DstBlend, ONE );
	RenderState( SrcBlendAlpha, ZERO );
	RenderState( DstBlendAlpha, ONE );

	Texture2D g_tSprite < Attribute( "HaloWeatherSprite" ); SrgbRead( false ); >;
	Texture2D g_tFrame < Attribute( "HaloWeatherFrame" ); SrgbRead( false ); >;
	SamplerState g_sSprite < Filter( Bilinear ); AddressU( CLAMP ); AddressV( CLAMP ); >;
	SamplerState g_sPoint < Filter( Point ); AddressU( CLAMP ); AddressV( CLAMP ); >;

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
		float4 sprite = saturate( g_tSprite.Sample( g_sSprite, i.vUv ) );
		float a = sprite.a * saturate( i.vAlpha );
		clip( a > 0 ? 1 : -1 );

		float2 uv = ( i.vPositionPs.xy - g_vViewportOffset ) * g_vInvViewportSize;
		float3 behind = saturate( g_tFrame.SampleLevel( g_sPoint, uv, 0 ).rgb );
		float3 result = GammaToLinear( lerp( LinearToGamma( behind ), sprite.rgb, a ) );
		return float4( max( result - behind, 0 ), 0 );
	}
}
