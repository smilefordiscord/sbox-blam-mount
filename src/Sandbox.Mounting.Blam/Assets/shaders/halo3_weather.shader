//
// Halo 3 weather particles (Halo3/Halo3Weather.cs): one quad per particle around the camera, facing it (snow, ash, dust, pollen) or stretched along the particle's velocity (rain streaks, billboard "parallel to direction").
// Quads are at least HaloMinimumPixels across, as tiny flakes would vanish between pixels. The sprite (sRGB decoded when its bitmap is) times the particle's alpha goes over the frame premultiplied: alpha blended kinds keep their alpha, additive kinds (additive, add_src_times_srcalpha) write alpha 0, so one blend state does both.
//
// Vertex: position = particle centre (world), normal = velocity (world, for streaks), TexCoord0.xy = corner (0-1, also the uv), TexCoord1 = (width, height in world units, alpha, additive).
//

HEADER
{
	Description = "Halo 3 weather particles";
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
	float3 vVelocity : NORMAL < Semantic( None ); >;
	float4 vCorner : TEXCOORD0 < Semantic( None ); >;
	float4 vParticle : TEXCOORD1 < Semantic( None ); >;
};

struct PixelInput
{
	float4 vPositionPs : SV_Position;
	float2 vUv : TEXCOORD0;
	float2 vAlpha : TEXCOORD1;
};

VS
{
	float g_flMinimumPixels < Attribute( "HaloMinimumPixels" ); Default( 1.5 ); >;
	float g_flStreak < Attribute( "Halo3WeatherStreak" ); Default( 0.0 ); >;

	PixelInput MainVs( VertexInput v )
	{
		PixelInput o = (PixelInput)0;
		float3 toCamera = g_vCameraPositionWs - v.vPosition;
		float distance = max( length( toCamera ), 1e-3 );
		float3 view = toCamera / distance;

		// World units per pixel at this distance.
		float perPixel = 2.0 * distance * g_vInvViewportSize.y / max( g_matViewToProjection[1][1], 1e-3 );
		float width = max( v.vParticle.x, g_flMinimumPixels * perPixel );
		float height = max( v.vParticle.y, g_flMinimumPixels * perPixel );

		float3 up = g_vCameraUpDirWs;
		float3 right = normalize( cross( up, view ) );
		if ( g_flStreak > 0.5 && dot( v.vVelocity, v.vVelocity ) > 1e-6 )
		{
			// Long axis along the velocity as seen from the camera, short axis across it.
			float3 along = normalize( v.vVelocity );
			float3 across = cross( along, view );
			if ( dot( across, across ) > 1e-6 )
			{
				up = along;
				right = normalize( across );
			}
		}
		else
			up = normalize( cross( view, right ) );

		float2 corner = v.vCorner.xy * 2 - 1;
		float3 position = v.vPosition + right * ( corner.x * width * 0.5 ) - up * ( corner.y * height * 0.5 );
		o.vPositionPs = Position3WsToPs( position );
		o.vUv = v.vCorner.xy;
		o.vAlpha = v.vParticle.zw;
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
	RenderState( DstBlend, INV_SRC_ALPHA );
	RenderState( SrcBlendAlpha, ZERO );
	RenderState( DstBlendAlpha, ONE );

	Texture2D g_tSprite < Attribute( "Halo3WeatherSprite" ); SrgbRead( false ); >;
	SamplerState g_sSprite < Filter( Bilinear ); AddressU( CLAMP ); AddressV( CLAMP ); >;
	float g_flSrgb < Attribute( "Halo3WeatherSrgb" ); Default( 1.0 ); >;
	float3 g_vTint < Attribute( "Halo3WeatherTint" ); Default3( 1.0, 1.0, 1.0 ); >;

	// Exposure as the other Halo 3 shaders: while Halo 3's post-processing runs (Halo3Hdr) the sprite goes out as x = colour * 2^E / 1.4938 with E from its state (bindless index), else through the tone curve here at the map's exposure.
	float g_flHdr < Attribute( "Halo3Hdr" ); Default( 0.0 ); >;
	float g_flExposureState < Attribute( "Halo3ExposureStateIndex" ); Default( -1.0 ); >;
	float g_flSceneExposureStops < Attribute( "Halo3SceneExposureStops" ); Default( 0.0 ); >;

	float3 Finish( float3 c )
	{
		bool live = g_flHdr > 0.5 && g_flExposureState >= 0;
		float stops = live ? Bindless::GetTexture2D( (int)round( g_flExposureState ) ).Load( int3( 0, 0, 0 ) ).x : g_flSceneExposureStops;
		float3 x = max( c * exp2( stops ) / 1.4938016, 0 );
		if ( live )
			return min( x, 128 );
		x = saturate( x );
		return x * ( 1.5 - 0.5 * x * x ) * ( 15.0 / 16.0 );
	}

	float4 MainPs( PixelInput i ) : SV_Target0
	{
		float4 sprite = saturate( g_tSprite.Sample( g_sSprite, i.vUv ) );
		if ( g_flSrgb > 0.5 )
			sprite.rgb = lerp( pow( ( sprite.rgb + 0.055 ) / 1.055, 2.4 ), sprite.rgb / 12.92, step( sprite.rgb, 0.04045 ) );
		float a = sprite.a * saturate( i.vAlpha.x );
		clip( a > 1.0 / 512 ? 1 : -1 );
		return float4( Finish( sprite.rgb * g_vTint ) * a, i.vAlpha.y > 0.5 ? 0 : a );
	}
}
