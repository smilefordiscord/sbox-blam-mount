//
// Halo CE sky, used as a SkyBox2D material.
//
// Halo draws its sky model centered on the camera with depth testing off, so the sky is always
// infinitely far away. Here the sky model is rendered once into six faces when the map loads,
// packed side by side in a 2D atlas, and this shader looks the view direction up in them.
//
// Faces, in order, as (forward, up), with right = cross( forward, up ):
// (+X, +Z) (-X, +Z) (+Y, +Z) (-Y, +Z) (+Z, -X) (-Z, +X). The renderer uses the same table.
//

HEADER
{
	Description = "Halo CE sky";
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
	#include "common/shared.hlsl"

	#define CUSTOM_MATERIAL_INPUTS
}

struct VertexInput
{
	#include "common/vertexinput.hlsl"
};

struct PixelInput
{
	#include "common/pixelinput.hlsl"
	float3 vSkyDirection : TEXCOORD14;
};

VS
{
	#include "common/vertex.hlsl"

	// The skybox mesh is drawn around the camera, scaled out to the far plane. Its object space
	// position doubles as the view direction for the pixel shader.
	PixelInput MainVs( VertexInput v )
	{
		PixelInput i = ProcessVertex( v );
		float3 positionWs = g_vCameraPositionWs.xyz + v.vPositionOs.xyz * ( g_flNearPlane + g_flFarPlane );
		i.vPositionPs = Position4WsToPs( float4( positionWs, 1 ) );
		i.vSkyDirection = v.vPositionOs.xyz;
		return i;
	}
}

PS
{
	#include "common/pixel.hlsl"

	// Drawn behind everything else, like s&box's own sky shaders.
	RenderState( CullMode, NONE );
	RenderState( DepthWriteEnable, false );
	RenderState( DepthEnable, true );
	RenderState( DepthFunc, GREATER_EQUAL );
	BoolAttribute( sky, true );

	SamplerState g_sClamp < Filter( Bilinear ); AddressU( CLAMP ); AddressV( CLAMP ); >;

	CreateInputTexture2D( SkyAtlas, Linear, 8, "None", "_color", ",0/,0/0", Default4( 0.00, 0.00, 0.00, 1.00 ) );
	Texture2D g_tSkyAtlas < Channel( RGBA, Box( SkyAtlas ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( false ); >;

	float4 MainPs( PixelInput i ) : SV_Target0
	{
		float3 dir = normalize( i.vSkyDirection );
		float3 a = abs( dir );

		float face;
		float3 forward, up;
		if ( a.x >= a.y && a.x >= a.z )
		{
			face = dir.x > 0 ? 0 : 1;
			forward = float3( sign( dir.x ), 0, 0 );
			up = float3( 0, 0, 1 );
		}
		else if ( a.y >= a.z )
		{
			face = dir.y > 0 ? 2 : 3;
			forward = float3( 0, sign( dir.y ), 0 );
			up = float3( 0, 0, 1 );
		}
		else
		{
			face = dir.z > 0 ? 4 : 5;
			forward = float3( 0, 0, sign( dir.z ) );
			up = float3( -sign( dir.z ), 0, 0 );
		}

		float3 right = cross( forward, up );
		float3 d = dir / dot( dir, forward );
		float2 uv = float2( dot( d, right ), -dot( d, up ) ) * 0.5 + 0.5;

		// Stay inside the face so filtering doesn't pick up its neighbour.
		uv = clamp( uv, 0.002, 0.998 );

		float3 color = g_tSkyAtlas.SampleLevel( g_sClamp, float2( ( face + uv.x ) / 6.0, uv.y ), 0 ).rgb;
		return float4( SrgbGammaToLinear( color ), 1 );
	}
}
