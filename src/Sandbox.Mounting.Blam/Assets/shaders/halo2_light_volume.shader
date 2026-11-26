//
// Halo 2 light volumes (MGS2): a row of glowing sprites along the marker's forward (the object's +X): Dune's
// watchtower beam, the glows of plasma bolts. As Halo 2 draws them:
// - c = |view . axis| (view from the camera to the marker); the functions' range input R = 1 - facing( 1 - c ), the
//   facing function's raw curve.
// - sprite k sits at the marker + axis x offset(k), radius(k) its half width; its colour is colour(k) x brightness(k)
//   x the distance fade (falloff to cutoff distance from the camera).
// - aspect: T = ( 2 acos( c ) / pi - t0 ) / ( 1 - t0 ) clamped, t0 = 2 threshold / pi, raised to the exponent;
//   along = lerp( parallel scale, along(k), T ), away likewise. The quad is camera facing, turned so its u axis runs
//   along the projected axis (screen x when T = 0), half extents max( along, away ) x r along it and away x r across.
// - pixel = texture.rgb x colour (texture alpha unused), added to the frame (ONE / INVSRCALPHA with alpha 0), in
//   Halo's gamma space frame buffer.
//
// As halo1_light_volume.shader does for Halo CE, the volume is one quad on screen around every sprite and the pixel
// shader adds every sprite at the pixel itself; the sum is added to the frame in gamma space against a copy of it
// (halo2_transparent's gamma-space add). The per-sprite values come from a small float table built by
// LightVolumeModels:
//   row 0 / 2: offset, radius (s&box units), brightness, along-axis scale at range input 0 / 1
//   row 1 / 3: colour rgb (gamma space), away-from-axis scale at range input 0 / 1
//   row 4: the facing function's curve at u = j / (width - 1) (in .r)
//

HEADER
{
	Description = "Halo 2 light volume";
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

	// Sprite count, parallel scale, its threshold angle (radians) and exponent.
	float4 g_vLightVolume < Default4( 0.0, 1.0, 0.0, 1.0 ); >;

	// Falloff and cutoff distances from the camera (s&box units); a cutoff not past the falloff: no fade.
	float2 g_vLightVolumeDistance < Default2( 0.0, 0.0 ); >;

	// Float data set from code (as ObjectLightingTable's table).
	CreateInputTexture2D( LightVolumeTable, Linear, 8, "None", "_color", ",0/,0/0", Default4( 0.00, 0.00, 0.00, 0.00 ) );
	Texture2D g_tLightVolumeTable < Channel( RGBA, Box( LightVolumeTable ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( false ); >;

	float4 TableAt( int k, int row )
	{
		return g_tLightVolumeTable.Load( int3( k, row, 0 ) );
	}

	// |view . axis|: 1 seen end on, 0 side on.
	float ViewCosine( float3 origin, float3 axis )
	{
		return saturate( abs( dot( normalize( origin - g_vCameraPositionWs.xyz ), axis ) ) );
	}

	// The range input: 1 - the facing function's curve at 1 - c.
	float RangeInput( float c )
	{
		float width = 0;
		float height = 0;
		g_tLightVolumeTable.GetDimensions( width, height );
		float at = saturate( 1 - c ) * ( width - 1 );
		int j = (int)floor( at );
		return 1 - lerp( TableAt( j, 4 ).r, TableAt( min( j + 1, (int)width - 1 ), 4 ).r, at - j );
	}

	// How far the aspect scales are from the parallel scale (0 end on, 1 side on).
	float AspectWeight( float c )
	{
		float a = 2 * acos( c ) / 3.14159265;
		float t0 = clamp( 2 * g_vLightVolume.z / 3.14159265, 0, 0.9999 );
		float t = saturate( ( a - t0 ) / ( 1 - t0 ) );
		return t > 0 ? ( g_vLightVolume.w > 0 ? pow( t, g_vLightVolume.w ) : 1 ) : 0;
	}

	// Sprite k at range input r: offset, radius, brightness, along scale; colour, away scale; the aspect scales
	// blended toward the parallel scale by w.
	void Sprite( int k, float r, float w, out float4 shape, out float4 color )
	{
		shape = lerp( TableAt( k, 0 ), TableAt( k, 2 ), r );
		color = lerp( TableAt( k, 1 ), TableAt( k, 3 ), r );
		shape.w = lerp( g_vLightVolume.y, shape.w, w );
		color.w = lerp( g_vLightVolume.y, color.w, w );
	}

	// A point's normalized device coordinates and clip w; false behind the camera.
	bool Project( float3 position, out float2 ndc, out float4 clip )
	{
		clip = Position3WsToPs( position );
		ndc = clip.w > g_flNearPlane ? clip.xy / clip.w : 0;
		return clip.w > g_flNearPlane;
	}

	// A sprite's centre on screen and how many pixels a world radius r spans there.
	bool ProjectSprite( float3 center, float radius, out float2 ndc, out float pixels, out float4 clip )
	{
		pixels = 0;
		if ( !Project( center, ndc, clip ) )
			return false;

		float3 right = normalize( cross( g_vCameraDirWs.xyz, g_vCameraUpDirWs.xyz ) );
		float2 side;
		float4 sideClip;
		Project( center + right * radius, side, sideClip );
		pixels = abs( side.x - ndc.x ) * 0.5 / g_vInvViewportSize.x;
		return true;
	}

	float2 NdcToPixels( float2 ndc ) { return ndc * 0.5 / g_vInvViewportSize.xy; }
	float2 PixelsToNdc( float2 pixels ) { return pixels * 2 * g_vInvViewportSize.xy; }

	// The volume's axis on screen (pixels, unit length), or screen x when its aspect is the parallel one.
	float2 ScreenAxis( float3 origin, float3 axis, float w )
	{
		float2 a, b;
		float4 ca, cb;
		if ( w <= 0 || !Project( origin, a, ca ) || !Project( origin + axis * 10, b, cb ) )
			return float2( 1, 0 );

		float2 d = NdcToPixels( b - a );
		return length( d ) > 1e-6 ? normalize( d ) : float2( 1, 0 );
	}
}

struct VertexInput
{
	#include "common/vertexinput.hlsl"
};

struct PixelInput
{
	#include "common/pixelinput.hlsl"

	// The marker's position and the distance fade; its forward (unit) and the object's scale; this pixel's normalized
	// device coordinates.
	nointerpolation float4 vLightVolumeOrigin : TEXCOORD14;
	nointerpolation float4 vLightVolumeAxis : TEXCOORD15;
	float2 vLightVolumeNdc : TEXCOORD16;
};

VS
{
	#include "common/vertex.hlsl"

	PixelInput MainVs( VertexInput v )
	{
		float2 corner = v.vTexCoord.xy;

		PixelInput i = ProcessVertex( v );
		float3 origin = i.vPositionWs.xyz;

		float3x4 matObjectToWorld = GetTransformMatrix( v.nInstanceTransformID, v.nBoneIndex.x );
		float3 axis = mul( (float3x3)matObjectToWorld, float3( 1, 0, 0 ) );
		float scale = max( length( axis ), 0.0001 );
		axis /= scale;

		float fade = 1;
		if ( g_vLightVolumeDistance.y > g_vLightVolumeDistance.x )
			fade = saturate( ( g_vLightVolumeDistance.y - distance( origin, g_vCameraPositionWs.xyz ) ) / ( g_vLightVolumeDistance.y - g_vLightVolumeDistance.x ) );

		// The box on screen around every sprite in front of the camera, at the nearest one's depth.
		float c = ViewCosine( origin, axis );
		float r = RangeInput( c );
		float w = AspectWeight( c );
		float2 lo = 1e9;
		float2 hi = -1e9;
		float4 nearest = float4( 0, 0, 0, 1e30 );
		int count = (int)g_vLightVolume.x;
		[loop]
		for ( int k = 0; k < min( count, 32 ); k++ )
		{
			float4 shape, color;
			Sprite( k, r, w, shape, color );
			float2 ndc;
			float pixels;
			float4 clip;
			if ( shape.y <= 0 || !ProjectSprite( origin + axis * shape.x * scale, shape.y * scale, ndc, pixels, clip ) )
				continue;

			float2 reach = PixelsToNdc( pixels * max( max( shape.w, color.w ), 0 ) );
			lo = min( lo, ndc - reach );
			hi = max( hi, ndc + reach );
			if ( clip.w < nearest.w )
				nearest = clip;
		}

		i.vLightVolumeOrigin = float4( origin, fade );
		i.vLightVolumeAxis = float4( axis, scale );
		i.vNormalWs = -g_vCameraDirWs.xyz;
		i = FinalizeVertex( i );

		if ( fade <= 0 || hi.x < lo.x || nearest.w > 1e29 )
		{
			i.vPositionPs = float4( 2, 2, 2, 1 );
			i.vLightVolumeNdc = 0;
			return i;
		}

		float2 ndcCorner = clamp( lerp( lo, hi, corner * 0.5 + 0.5 ), -2.0, 2.0 );
		i.vPositionPs = float4( ndcCorner * nearest.w, nearest.z, nearest.w );
		i.vLightVolumeNdc = ndcCorner;
		return i;
	}
}

PS
{
	#include "common/pixel.hlsl"

	RenderState( CullMode, NONE );
	RenderState( DepthWriteEnable, false );
	RenderState( BlendEnable, true );
	RenderState( SrcBlend, ONE );
	RenderState( DstBlend, ONE );
	RenderState( BlendOp, ADD );
	RenderState( SrcBlendAlpha, ZERO );
	RenderState( DstBlendAlpha, ONE );
	BoolAttribute( bWantsFBCopyTexture, true );

	SamplerState g_sLightVolume < Filter( Trilinear ); AddressU( WRAP ); AddressV( WRAP ); >;
	SamplerState g_sBehind < Filter( Point ); AddressU( CLAMP ); AddressV( CLAMP ); >;

	CreateInputTexture2D( LightVolume, Linear, 8, "None", "_color", ",0/,0/0", Default4( 1.00, 1.00, 1.00, 1.00 ) );
	Texture2D g_tLightVolume < Channel( RGBA, Box( LightVolume ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( false ); >;
	Texture2D g_tFrameBufferCopyTexture < Attribute( "FrameBufferCopyTexture" ); SrgbRead( false ); >;

	// The sRGB curve both ways, so what's behind comes back unchanged where nothing is added.
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
		float3 origin = i.vLightVolumeOrigin.xyz;
		float fade = i.vLightVolumeOrigin.w;
		float3 axis = i.vLightVolumeAxis.xyz;
		float scale = i.vLightVolumeAxis.w;
		float2 pixel = i.vLightVolumeNdc;

		float c = ViewCosine( origin, axis );
		float r = RangeInput( c );
		float w = AspectWeight( c );
		float2 along = ScreenAxis( origin, axis, w );
		float2 across = float2( -along.y, along.x );

		float3 sum = 0;
		int count = (int)g_vLightVolume.x;
		[loop]
		for ( int k = 0; k < min( count, 32 ); k++ )
		{
			float4 shape, color;
			Sprite( k, r, w, shape, color );
			float2 ndc;
			float pixels;
			float4 clip;
			float brightness = saturate( shape.z * fade );
			if ( shape.y <= 0 || brightness <= 0 || !ProjectSprite( origin + axis * shape.x * scale, shape.y * scale, ndc, pixels, clip ) )
				continue;

			// The pixel in the sprite's quad: along the projected axis over max( along, away ) radii, across over away.
			float2 d = NdcToPixels( pixel - ndc ) / max( pixels, 1e-4 );
			float2 local = float2( dot( d, along ) / max( max( shape.w, color.w ), 1e-4 ), dot( d, across ) / max( color.w, 1e-4 ) );
			if ( any( abs( local ) > 1 ) )
				continue;

			float3 t = g_tLightVolume.SampleLevel( g_sLightVolume, float2( local.x, -local.y ) * 0.5 + 0.5, 0 ).rgb;
			sum += t * saturate( color.rgb ) * brightness;
		}

		// Added in gamma space (fogged like additive particles: light in front of fog fades with it).
		float atmosphere = DoAtmospherics( origin, i.vPositionSs.xy, float4( 0, 0, 0, 1 ), true ).a;
		float2 uv = i.vPositionSs.xy * g_vInvViewportSize * g_vFrameBufferCopyInvSizeAndUvScale.zw;
		float3 behind = saturate( g_tFrameBufferCopyTexture.SampleLevel( g_sBehind, uv, 0 ).rgb );
		float3 added = max( GammaToLinear( saturate( LinearToGamma( behind ) + saturate( sum ) * atmosphere ) ) - behind, 0 );
		return float4( added, 1 );
	}
}
