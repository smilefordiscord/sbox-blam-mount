//
// Halo 3's patchy fog (Halo3/Halo3PatchyFog.cs): MCC's rasterizer\shaders\patchy_fog, one full-screen quad a world unit in front of the camera standing for 8 view-aligned noise sheets.
// Per sheet i at view depth d_i: noise.r^2 at its texture transform, times its fade into the geometry behind ( 1 - e^( -( z - d_i ) * depth fade ) ) and the sheet's fade factor, times its height falloff min( e^( ( full height - ( eye.z + dir.z * d_i ) ) * att.y ), 1 ); opacity 1 - e^( -Mie beta.x * sum ), colour the Mie phase toward the sun * ( 1 - g^2 ) Mie theta * sun colour / ( Rayleigh + Mie beta ), faded out for geometry within 1.3 units; premultiplied (one, one minus source alpha) and exposed like Halo 3's materials.
// World units throughout (Halo3PatchyFogScale converts s&box ones).
//

HEADER
{
	Description = "Halo 3 patchy fog";
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
	#define S_TRANSLUCENT 1
	#define BLEND_MODE_ALREADY_SET 1
	#include "common/shared.hlsl"

	#define CUSTOM_MATERIAL_INPUTS
}

struct VertexInput
{
	float3 vPosition : POSITION < Semantic( None ); >;
};

struct PixelInput
{
	float4 vPositionPs : SV_Position;
	float3 vPositionWs : TEXCOORD0;
};

VS
{
	PixelInput MainVs( VertexInput v )
	{
		PixelInput o = (PixelInput)0;
		o.vPositionPs = Position3WsToPs( v.vPosition );
		o.vPositionWs = v.vPosition;
		return o;
	}
}

PS
{
	#include "common/pixel.hlsl"
	#include "common/classes/Depth.hlsl"

	RenderState( CullMode, NONE );
	// Not depth tested: walls nearer than the quad would cut the dust off with a hard edge; each pixel fades its sheets by the scene depth behind them itself.
	RenderState( DepthEnable, false );
	RenderState( DepthWriteEnable, false );
	RenderState( BlendEnable, true );
	RenderState( SrcBlend, ONE );
	RenderState( DstBlend, INV_SRC_ALPHA );
	RenderState( SrcBlendAlpha, ZERO );
	RenderState( DstBlendAlpha, ONE );
	RenderState( BlendOp, ADD );

	SamplerState g_sWrap < Filter( Bilinear ); AddressU( WRAP ); AddressV( WRAP ); >;

	float g_flNoise < Attribute( "Halo3PatchyNoise" ); Default( -1.0 ); >;
	float4 g_vTransform0 < Attribute( "Halo3PatchyTransform0" ); >;
	float4 g_vTransform1 < Attribute( "Halo3PatchyTransform1" ); >;
	float4 g_vTransform2 < Attribute( "Halo3PatchyTransform2" ); >;
	float4 g_vTransform3 < Attribute( "Halo3PatchyTransform3" ); >;
	float4 g_vTransform4 < Attribute( "Halo3PatchyTransform4" ); >;
	float4 g_vTransform5 < Attribute( "Halo3PatchyTransform5" ); >;
	float4 g_vTransform6 < Attribute( "Halo3PatchyTransform6" ); >;
	float4 g_vTransform7 < Attribute( "Halo3PatchyTransform7" ); >;
	float4 g_vDepths0 < Attribute( "Halo3PatchyDepths0" ); >;
	float4 g_vDepths1 < Attribute( "Halo3PatchyDepths1" ); >;
	float4 g_vFades0 < Attribute( "Halo3PatchyFades0" ); >;
	float4 g_vFades1 < Attribute( "Halo3PatchyFades1" ); >;
	float4 g_vBasis < Attribute( "Halo3PatchyBasis" ); Default4( 1.0, 0.0, 0.0, 1.0 ); >;
	// Full intensity height + sea level, ln2 / ( half - full ), depth fade, sheet spacing.
	float4 g_vAttenuation < Attribute( "Halo3PatchyAttenuation" ); >;
	// Sun direction; sun colour / ( Rayleigh + Mie beta ); ( 1 - g^2 ) Mie theta; ( Mie beta.x, g ).
	float3 g_vSun < Attribute( "Halo3PatchySun" ); Default3( 0.0, 0.0, 1.0 ); >;
	float3 g_vSunColor < Attribute( "Halo3PatchySunColor" ); >;
	float3 g_vMieTheta < Attribute( "Halo3PatchyMieTheta" ); >;
	float2 g_vMie < Attribute( "Halo3PatchyMie" ); >;
	// x = world units per s&box unit.
	float g_flScale < Attribute( "Halo3PatchyFogScale" ); Default( 1.0 ); >;

	// Exposure as halo3_shader's: while Halo 3's post-processing runs, x = colour * 2^E / 1.4938 with E from its state.
	float g_flHdr < Attribute( "Halo3Hdr" ); Default( 0.0 ); >;
	float g_flExposureState < Attribute( "Halo3ExposureStateIndex" ); Default( -1.0 ); >;
	float g_flSceneExposureStops < Attribute( "Halo3SceneExposureStops" ); Default( 0.0 ); >;

	float Stops()
	{
		return g_flHdr > 0.5 && g_flExposureState >= 0 ? Bindless::GetTexture2D( (int)round( g_flExposureState ) ).Load( int3( 0, 0, 0 ) ).x : g_flSceneExposureStops;
	}

	float4 MainPs( PixelInput i ) : SV_Target0
	{
		if ( g_flNoise < 0 )
			discard;

		float2 pixel = i.vPositionPs.xy;
		float2 screen = ( pixel - g_vViewportOffset ) * g_vInvViewportSize;
		float2 ndc = float2( screen.x * 2 - 1, 1 - screen.y * 2 );
		// The scene's view depth from its world position (GetLinear read near surfaces as a few world units at most, so indoors and near the ground the sheets found nothing behind them); nothing drawn (the sky) is far.
		float z = Depth::GetNormalized( pixel ) <= 0 ? 1e6 : dot( Depth::GetWorldPosition( pixel ) - g_vCameraPositionWs, g_vCameraDirWs ) * g_flScale;
		float3 dir = normalize( i.vPositionWs - g_vCameraPositionWs );
		float eyeZ = g_vCameraPositionWs.z * g_flScale;

		Texture2D noise = Bindless::GetTexture2D( (int)round( g_flNoise ) );
		float depths[8] = { g_vDepths0.x, g_vDepths0.y, g_vDepths0.z, g_vDepths0.w, g_vDepths1.x, g_vDepths1.y, g_vDepths1.z, g_vDepths1.w };
		float fades[8] = { g_vFades0.x, g_vFades0.y, g_vFades0.z, g_vFades0.w, g_vFades1.x, g_vFades1.y, g_vFades1.z, g_vFades1.w };
		float4 transforms[8] = { g_vTransform0, g_vTransform1, g_vTransform2, g_vTransform3, g_vTransform4, g_vTransform5, g_vTransform6, g_vTransform7 };

		float sum = 0;
		[unroll] for ( int k = 0; k < 8; k++ )
		{
			float4 t = transforms[k];
			float2 uv = t.xy + ndc.x * t.z * g_vBasis.xy + ndc.y * t.w * g_vBasis.zw;
			float n = noise.Sample( g_sWrap, uv ).r;
			float into = max( 1 - exp( -( z - depths[k] ) * g_vAttenuation.z ), 0 ) * fades[k];
			float height = min( exp( ( g_vAttenuation.x - ( eyeZ + dir.z * depths[k] ) ) * g_vAttenuation.y ), 1 );
			sum += n * n * into * height;
		}

		float alpha = 1 - exp( -g_vMie.x * sum );
		float g = g_vMie.y;
		float3 color = pow( max( 1 + g - 2 * g * dot( dir, g_vSun ), 1e-4 ), -1.5 ) * g_vMieTheta * g_vSunColor;
		float edge = smoothstep( 0, 1, saturate( ( z - 1.03 ) / 0.27 ) );
		float3 x = edge * color * alpha * exp2( Stops() ) / 1.4938016;
		return float4( g_flHdr > 0.5 ? min( x, 128 ) : saturate( x ), alpha );
	}
}
