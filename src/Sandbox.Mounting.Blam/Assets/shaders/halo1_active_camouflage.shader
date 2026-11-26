//
// Halo CE active camouflage: a cloaked unit bends the scene behind it.
//
// Halo copied the frame (at 320x240) before drawing camouflaged units, then drew each one as a
// lookup into that copy: a cube map of perturbation vectors ("active camouflage distortion" from the
// globals), looked up by the surface's normal as the camera sees it, moves the lookup off the
// pixel's own screen position by the refraction amount (in 320x240 pixels) times the camouflage,
// less with distance (the distance falloff). What it reads is tinted by the camouflage tint. While
// the camouflage fades in or out (below 1) Halo drew the unit normally underneath and blended this
// over it by the camouflage; fully cloaked, only the bent background shows.
//
// Here the background comes from s&box's frame buffer copy, which is taken after opaque things
// draw. A game draws the model a second time with this material (a second renderer, bone merged
// onto the first, with this as its material override) and sets the HaloCamo attribute to the
// camouflage (0-1) on both renderers. As in Halo, below 1 the model draws normally and this is
// blended over it by the camouflage, tested against the model's own depth so only its front
// surface takes it; the bent copy then holds the unit too, as Halo's did. At 1 the model isn't
// drawn (halo1_model.shader), so the copy holds the level behind it and this covers the unit
// completely, writing its depth so its nearer parts hide the farther ones. The bone merged second
// renderer's depth doesn't come out exactly equal to the model's, so this is drawn a hair nearer.
//

HEADER
{
	Description = "Halo CE active camouflage";
}

FEATURES
{
	#include "common/features.hlsl"
}

MODES
{
	Forward();
	ToolsShadingComplexity( "tools_shading_complexity.shader" );
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
		PixelInput i = ProcessVertex( v );
		i = FinalizeVertex( i );

		// 0.4% of the distance nearer (reverse Z: depth is proportional to 1 / distance), so it
		// doesn't z-fight with the model it's drawn over.
		i.vPositionPs.z *= 1.004;
		return i;
	}
}

PS
{
	#include "common/pixel.hlsl"

	RenderState( CullMode, DEFAULT );
	// Blended by the camouflage, on the model's own depth (reverse Z: nearer is greater).
	RenderState( BlendEnable, true );
	RenderState( SrcBlend, SRC_ALPHA );
	RenderState( DstBlend, INV_SRC_ALPHA );
	RenderState( BlendOp, ADD );
	RenderState( DepthWriteEnable, true );
	RenderState( DepthFunc, GREATER_EQUAL );
	BoolAttribute( bWantsFBCopyTexture, true );

	SamplerState g_sClamp < Filter( Bilinear ); AddressU( CLAMP ); AddressV( CLAMP ); >;

	Texture2D g_tFrameBufferCopyTexture < Attribute( "FrameBufferCopyTexture" ); SrgbRead( false ); >;

	// The distortion cube map, as a 2D atlas of its six faces (+X, -X, +Y, -Y, +Z, -Z).
	CreateInputTexture2D( Distortion, Linear, 8, "None", "_color", ",0/,0/0", Default4( 0.50, 0.50, 1.00, 0.00 ) );
	Texture2D g_tDistortion < Channel( RGBA, Box( Distortion ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( false ); >;

	// Refraction amount (320x240 pixels), distance falloff (s&box units) and tint, normal and hyper
	// stealth, from the globals.
	float2 g_vCamoRefraction < Default2( 11.0, 9.0 ); >;
	float2 g_vCamoDistanceFalloff < Default2( 2270.0, 929.0 ); >;
	float3 g_vCamoTint < Default3( 0.84, 0.84, 0.91 ); >;
	float3 g_vCamoHyperStealthTint < Default3( 1.0, 1.0, 1.0 ); >;

	// Per object: the camouflage (0-1), and Halo's "parameter" blending toward hyper stealth (0 in
	// normal play).
	float g_flHaloCamo < Attribute( "HaloCamo" ); Default( 1.0 ); >;
	float g_flHaloCamoHyperStealth < Attribute( "HaloCamoHyperStealth" ); Default( 0.0 ); >;

	// Debugging aid, set on the renderer: 1 draws flat magenta (where this draws at all), 2 reads the
	// frame copy without bending it, 3 shows the bend (red/green, 0 = mid gray) instead of the copy,
	// 4/5 read the copy a fixed 8 pixels to the right/left, 6 shows the copy coordinates (bands every
	// eighth of the screen), 7 draws green wherever the bent read comes back black.
	float g_flHaloCamoDebug < Attribute( "HaloCamoDebug" ); Default( 0.0 ); >;

	// A cube map atlas texel, the face picked by Direct3D's cube map rules. Same as in
	// halo1_model.shader.
	float4 SampleCubeAtlas( Texture2D atlas, float3 dir )
	{
		float3 a = abs( dir );
		float face;
		float2 st;
		float ma;

		if ( a.x >= a.y && a.x >= a.z )
		{
			ma = a.x;
			face = dir.x > 0 ? 0 : 1;
			st = dir.x > 0 ? float2( -dir.z, -dir.y ) : float2( dir.z, -dir.y );
		}
		else if ( a.y >= a.z )
		{
			ma = a.y;
			face = dir.y > 0 ? 2 : 3;
			st = dir.y > 0 ? float2( dir.x, dir.z ) : float2( dir.x, -dir.z );
		}
		else
		{
			ma = a.z;
			face = dir.z > 0 ? 4 : 5;
			st = dir.z > 0 ? float2( dir.x, -dir.y ) : float2( -dir.x, -dir.y );
		}

		float2 uv = clamp( st / ma * 0.5 + 0.5, 0.01, 0.99 );
		return atlas.SampleLevel( g_sClamp, float2( ( face + uv.x ) / 6.0, uv.y ), 0 );
	}

	float3 ToLinear( float3 c )
	{
		c = max( c, 0 );
		return lerp( pow( ( c + 0.055 ) / 1.055, 2.4 ), c / 12.92, step( c, 0.04045 ) );
	}

	float4 MainPs( PixelInput i ) : SV_Target0
	{
		float camo = saturate( g_flHaloCamo );
		clip( camo - 1.0 / 256 );

		float3 positionWs = i.vPositionWithOffsetWs.xyz + g_vHighPrecisionLightingOffsetWs.xyz;
		float3 normal = normalize( i.vNormalWs );

		// The normal as the camera sees it: x right, y up, z toward the camera.
		float3 forward = g_vCameraDirWs.xyz;
		float3 right = normalize( cross( forward, g_vCameraUpDirWs.xyz ) );
		float3 up = cross( right, forward );
		float3 viewNormal = float3( dot( normal, right ), dot( normal, up ), -dot( normal, forward ) );

		float hyper = saturate( g_flHaloCamoHyperStealth );
		float refraction = lerp( g_vCamoRefraction.x, g_vCamoRefraction.y, hyper );
		float falloff = lerp( g_vCamoDistanceFalloff.x, g_vCamoDistanceFalloff.y, hyper );
		float3 tint = lerp( g_vCamoTint, g_vCamoHyperStealthTint, hyper );

		// The perturbation, in 320x240 pixels of Halo's frame copy, as a fraction of the screen.
		float2 perturbation = SampleCubeAtlas( g_tDistortion, viewNormal ).rg * 2 - 1;
		float depth = max( dot( positionWs - g_vCameraPositionWs, forward ), 1 );
		float2 offset = perturbation * refraction * camo * saturate( falloff / depth ) / float2( 320, -240 );

		int debug = (int)round( g_flHaloCamoDebug );
		if ( debug == 1 )
			return float4( 1, 0, 1, camo );
		if ( debug == 3 )
			return float4( ToLinear( float3( perturbation * 0.5 + 0.5, 0 ) ), camo );
		if ( debug == 2 )
			offset = 0;
		if ( debug == 4 || debug == 5 )
			offset = float2( debug == 4 ? 8 : -8, 0 ) * g_vInvViewportSize;

		float2 screenUv = i.vPositionSs.xy * g_vInvViewportSize + offset;
		float2 copyUv = screenUv * g_vFrameBufferCopyInvSizeAndUvScale.zw;
		float3 behind = g_tFrameBufferCopyTexture.SampleLevel( g_sClamp, copyUv, 0 ).rgb;

		// s&box's frame copy holds a thin dark rim along a camouflaged unit's outline (cause not
		// found yet), and near the silhouette the bend changes fast enough to stretch it into dark
		// slivers. A bent read far darker than the unbent one takes the unbent one instead.
		float3 straight = g_tFrameBufferCopyTexture.SampleLevel( g_sClamp, i.vPositionSs.xy * g_vInvViewportSize * g_vFrameBufferCopyInvSizeAndUvScale.zw, 0 ).rgb;
		if ( debug != 7 && max( behind.r, max( behind.g, behind.b ) ) < 0.1 * max( straight.r, max( straight.g, straight.b ) ) )
			behind = straight;

		if ( debug == 6 )
			return float4( frac( copyUv * 8 ), 0, camo );
		if ( debug == 7 && max( behind.r, max( behind.g, behind.b ) ) < 0.01 )
			return float4( 0, 1, 0, camo );

		// Halo tinted the copy in its gamma space frame buffer.
		return float4( behind * ToLinear( tint ), camo );
	}
}
