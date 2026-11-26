//
// Halo CE detail objects: the grass and plant sprites painted onto a level's ground.
//
// Every object is a quad whose four corners all sit at the object's anchor (the bottom middle of
// the sprite). The vertex shader spreads the corners out to face the camera, so a whole level's
// worth of sprites is a handful of static meshes. Each vertex carries its corner offset in the
// normal and the object's baked color in the tangent: lighting was baked into that color when
// the level was lit, so the sprite is drawn unlit, like the level geometry.
//
// Halo only drew the objects close to the camera, fading them out between the type's near and
// far fade distances. Past the far distance the quad collapses to a point.
//
// Halo alpha blended the sprites (sprite alpha x fade) without writing depth, after the water and
// the planar fog and before the fog screens; PlanarFogFrame sorts them there. A screen-door dither
// in their place broke c10's 1-2 texel reed stalks into dotted lines.
//

HEADER
{
	Description = "Halo CE detail object shader";
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

	// Near and far fade distances, in s&box units.
	float g_flNearFade < Default( 300.0 ); >;
	float g_flFarFade < Default( 800.0 ); >;

	// Collection type: 0 screen facing (always faces the camera), 1 viewer facing (upright,
	// turning around the vertical axis).
	float g_flFacing < Default( 1.0 ); >;
}

struct VertexInput
{
	#include "common/vertexinput.hlsl"
};

struct PixelInput
{
	#include "common/pixelinput.hlsl"
};

VS
{
	#include "common/vertex.hlsl"

	PixelInput MainVs( VertexInput v )
	{
		// The corner offset (right, up) and color are read raw, before the common code treats
		// them as a normal and tangent.
		float2 corner = v.vNormalOs.xy;
		float4 color = v.vTangentUOs_flTangentVSign;

		PixelInput i = ProcessVertex( v );
		float3 anchor = i.vPositionWs.xyz;

		float3 toCamera = g_vCameraPositionWs.xyz - anchor;
		float flDistance = length( toCamera );

		float3 up;
		float3 right;
		if ( g_flFacing < 0.5 )
		{
			// Screen facing: the camera's own axes.
			right = normalize( cross( g_vCameraDirWs.xyz, g_vCameraUpDirWs.xyz ) );
			up = normalize( cross( right, g_vCameraDirWs.xyz ) );
		}
		else
		{
			// Viewer facing: stand upright and turn toward the camera. Looking straight down
			// there's no horizontal direction to the camera, so fall back to the camera's right.
			up = float3( 0, 0, 1 );
			float3 side = cross( up, toCamera );
			float3 cameraRight = cross( g_vCameraDirWs.xyz, up );
			right = normalize( dot( side.xy, side.xy ) > 1.0 ? side : cameraRight + float3( 0.0001, 0, 0 ) );
		}

		float fade = saturate( ( g_flFarFade - flDistance ) / max( g_flFarFade - g_flNearFade, 0.001 ) );
		float3 offset = fade > 0.0 ? right * corner.x + up * corner.y : float3( 0, 0, 0 );

		i.vPositionWs.xyz = anchor + offset;
		i.vPositionPs = Position3WsToPs( i.vPositionWs.xyz );
		i.vNormalWs = normalize( toCamera + float3( 0, 0, 0.0001 ) );
		i.vVertexColor = float4( color.rgb, fade );

		return FinalizeVertex( i );
	}
}

PS
{
	#include "common/pixel.hlsl"

	RenderState( CullMode, NONE );
	RenderState( DepthWriteEnable, false );
	RenderState( BlendEnable, true );
	RenderState( SrcBlend, SRC_ALPHA );
	RenderState( DstBlend, INV_SRC_ALPHA );
	RenderState( BlendOp, ADD );
	RenderState( SrcBlendAlpha, ZERO );
	RenderState( DstBlendAlpha, ONE );

	SamplerState g_sSprite < Filter( Bilinear ); AddressU( CLAMP ); AddressV( CLAMP ); >;

	CreateInputTexture2D( SpritePlate, Linear, 8, "None", "_color", ",0/,0/0", Default4( 1.00, 1.00, 1.00, 1.00 ) );
	Texture2D g_tSpritePlate < Channel( RGBA, Box( SpritePlate ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( false ); >;

	// Halo CE's atmospheric fog amount: linear in view depth from the start distance to the maximum
	// density at the opaque distance (halo1_environment.shader has the details).
	float HaloFogAmount( float3 positionWs )
	{
		if ( !g_bFogEnabled || !g_bGradientFogEnabled )
			return 0;

		float depth = dot( positionWs - g_vCameraPositionWs, g_vCameraDirWs );
		float u = saturate( g_vGradientFogBiasAndScale.x + g_vGradientFogBiasAndScale.z * depth );
		return u * pow( saturate( g_vGradientFogColor_Opacity.w ), 1.0 / 2.2 );
	}

	// The fog color back in Halo's gamma space.
	float3 HaloFogColor()
	{
		float3 c = max( g_vGradientFogColor_Opacity.rgb, 0 );
		return lerp( 1.055 * pow( c, 1.0 / 2.4 ) - 0.055, c * 12.92, step( c, 0.0031308 ) );
	}

	float4 MainPs( PixelInput i ) : SV_Target0
	{
		float4 sprite = Tex2DS( g_tSpritePlate, g_sSprite, i.vTextureCoords.xy );

		float alpha = sprite.a * i.vVertexColor.a;
		clip( alpha - 1.0 / 255.0 );

		// Like the level geometry, Halo combined these in gamma space.
		float3 color = saturate( sprite.rgb * i.vVertexColor.rgb );

		// Halo's atmospheric fog, in gamma space (as in halo1_environment.shader).
		float3 positionWs = i.vPositionWithOffsetWs.xyz + g_vHighPrecisionLightingOffsetWs.xyz;
		color = lerp( color, HaloFogColor(), HaloFogAmount( positionWs ) );
		return float4( SrgbGammaToLinear( color ), alpha );
	}
}
