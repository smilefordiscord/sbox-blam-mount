//
// Halo 3's distortion particles (Halo3/Halo3Distortion.cs) into Halo3PostFx's offset target: MCC's particle template with specialized_rendering distortion.
// Per pixel: the base map's signed xy (BC5, uploaded re-biased: tex * 2 - 1) as ( x, -y ) along the sprite's u and up axes, times the particle's alpha and its depth fade into the scene behind (which also hides it behind geometry), projected to the screen at the particle's depth, times distortion_scale / 32: a uv offset, added up (blend one, one).
// distortion_expensive also drops pixels whose moved-to point lies in front of the particle.
//
// Vertex: POSITION corner, NORMAL the sprite's u axis, TANGENT ( its up axis, alpha ), TEXCOORD0 ( base map uv, bindless index, 1 expensive ), TEXCOORD1 ( depth fade range in s&box units, distortion scale, world scale ).
//

HEADER
{
	Description = "Halo 3 distortion";
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
	#include "common/shared.hlsl"
}

struct VertexInput
{
	float3 vPosition : POSITION < Semantic( None ); >;
	float4 vUv : TEXCOORD0 < Semantic( None ); >;
	float4 vLook : TEXCOORD1 < Semantic( None ); >;
	float3 vAxisU : NORMAL < Semantic( None ); >;
	float4 vAxisUp : TANGENT < Semantic( None ); >;
};

struct PixelInput
{
	float4 vPositionPs : SV_Position;
	float4 vUv : TEXCOORD0;
	float4 vLook : TEXCOORD1;
	// The sprite's u and up axes (a world unit each) as NDC moves at its depth, and its alpha.
	float4 vAxes : TEXCOORD2;
	float vAlpha : TEXCOORD3;
};

VS
{
	PixelInput MainVs( VertexInput v )
	{
		PixelInput o = (PixelInput)0;
		o.vPositionPs = Position3WsToPs( v.vPosition );
		float w = max( o.vPositionPs.w, 1e-3 );
		float scale = v.vLook.z;
		o.vAxes = float4( mul( g_matWorldToProjection, float4( v.vAxisU * scale, 0 ) ).xy, mul( g_matWorldToProjection, float4( v.vAxisUp.xyz * scale, 0 ) ).xy ) / w;
		o.vUv = v.vUv;
		o.vLook = v.vLook;
		o.vAlpha = v.vAxisUp.w;
		return o;
	}
}

PS
{
	#include "common/classes/Depth.hlsl"

	RenderState( CullMode, NONE );
	RenderState( DepthEnable, false );
	RenderState( DepthWriteEnable, false );
	RenderState( BlendEnable, true );
	RenderState( SrcBlend, ONE );
	RenderState( DstBlend, ONE );
	RenderState( SrcBlendAlpha, ONE );
	RenderState( DstBlendAlpha, ONE );
	RenderState( BlendOp, ADD );

	SamplerState g_sWrap < Filter( Bilinear ); AddressU( WRAP ); AddressV( WRAP ); >;

	// The offset target's size against the screen's (Halo3PostFx: half).
	float g_flTargetScale < Attribute( "Halo3DistortionTargetScale" ); Default( 2.0 ); >;

	float4 MainPs( PixelInput i ) : SV_Target0
	{
		float2 pixel = i.vPositionPs.xy * g_flTargetScale;
		float depth = i.vPositionPs.w;
		float scene = Depth::GetLinear( pixel );

		// MCC's depth fade into what's behind: saturate( ( scene - particle ) / range ), no range a plain depth test.
		float range = i.vLook.x;
		float fade = range > 0 ? saturate( ( scene - depth ) / range ) : ( scene > depth ? 1 : 0 );

		float2 t = Bindless::GetTexture2D( (int)round( i.vUv.z ) ).Sample( g_sWrap, i.vUv.xy ).xy * 2 - 1;
		float2 move = float2( t.x, -t.y ) * i.vAlpha * fade;
		float2 ndc = move.x * i.vAxes.xy + move.y * i.vAxes.zw;
		float2 offset = ndc * float2( 0.5, -0.5 ) * i.vLook.y / 32;
		if ( dot( offset, offset ) == 0 )
			discard;

		if ( i.vUv.w > 0.5 && Depth::GetLinear( clamp( pixel + offset * g_vViewportSize, 0, g_vViewportSize - 1 ) ) < depth )
			discard;

		return float4( offset, 0, 1 );
	}
}
