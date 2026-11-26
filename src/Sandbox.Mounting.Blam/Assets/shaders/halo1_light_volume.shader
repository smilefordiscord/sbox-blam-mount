//
// Halo CE light volumes (mgs2): the glowing bolts of plasma weapons and the glows on rockets,
// needles and fuel rods. A light volume is a row of camera facing square sprites along its
// marker's forward (the object's +X), each with its own size and color, added to the frame.
//
// Halo's widget sprite combiners: rgb = color x T + (1 - color) x T^6 for the map's value T (so a
// bright sprite's middle goes white), times alpha = color alpha x brightness, added with SRCALPHA /
// ONE to Halo's 8 bit gamma space frame buffer. A plasma bolt is up to twenty such sprites piled on
// each other, and the pile only looks right added up in gamma space: seen end on from behind (the
// shooter's view) the overcharged bolt's sprites sum far past white in the middle, while its edge
// is the biggest sprite's green fringe on its own. Added one by one in linear space the same pile
// is a small grey-white dot.
//
// So the whole light volume is one quad on screen, around every sprite's square, and the pixel
// shader adds up every sprite at that pixel itself (a loop over at most 64), as Halo's frame
// buffer did, clamps the sum to white and adds that to the frame in linear space. Each sprite is
// placed and sized exactly as Halo projected it: centered on its point, half as wide as its radius
// at its depth. The quad is depth tested at the nearest sprite's depth.
//
// Halo also scales the brightness by how the bolt is seen: between the perpendicular and parallel
// brightness scales by |camera forward . bolt forward|, faded out between the near and far fade
// distances along the camera's forward.
//

HEADER
{
	Description = "Halo CE light volume";
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

	// Halo's first frame, lengths in s&box units: offset from the marker, offset exponent, length,
	// and the sprite count; radius at the first (hither) and last (yon) sprite and its exponent;
	// colors (gamma space, alpha the brightness) at the first and last sprite; color and brightness
	// exponents.
	float4 g_vLightVolumeOffset < Default4( 0.0, 1.0, 0.0, 0.0 ); >;
	float3 g_vLightVolumeRadius < Default3( 0.0, 0.0, 1.0 ); >;
	float4 g_vLightVolumeColorHither < Default4( 1.0, 1.0, 1.0, 1.0 ); >;
	float4 g_vLightVolumeColorYon < Default4( 1.0, 1.0, 1.0, 1.0 ); >;
	float2 g_vLightVolumeExponents < Default2( 1.0, 1.0 ); >;

	float Pow1( float value, float exponent )
	{
		return exponent != 1.0 ? pow( max( value, 0.0 ), exponent ) : value;
	}

	// Sprite k's distance along the forward and radius (s&box units, unscaled), as Halo works them out.
	float2 LightVolumeSprite( int k, out float fraction )
	{
		float count = g_vLightVolumeOffset.w;
		float t = count > 1.0 ? k / ( count - 1.0 ) : 0.0;
		fraction = Pow1( t, g_vLightVolumeOffset.y );
		float radius = lerp( g_vLightVolumeRadius.x, g_vLightVolumeRadius.y, Pow1( fraction, g_vLightVolumeRadius.z ) );
		return float2( fraction * g_vLightVolumeOffset.z + g_vLightVolumeOffset.x, radius );
	}

	// A sprite's center and half size on screen (normalized device coordinates), from its world
	// center and radius. False when it's behind the camera.
	bool ProjectSprite( float3 center, float radius, out float2 ndc, out float2 halfSize, out float4 clip )
	{
		clip = Position3WsToPs( center );
		ndc = 0;
		halfSize = 0;
		if ( clip.w <= g_flNearPlane )
			return false;

		float3 right = normalize( cross( g_vCameraDirWs.xyz, g_vCameraUpDirWs.xyz ) );
		float3 up = normalize( cross( right, g_vCameraDirWs.xyz ) );
		float4 clipRight = Position3WsToPs( center + right * radius );
		float4 clipUp = Position3WsToPs( center + up * radius );
		ndc = clip.xy / clip.w;
		halfSize = float2( abs( clipRight.x / clipRight.w - ndc.x ), abs( clipUp.y / clipUp.w - ndc.y ) );
		return true;
	}
}

struct VertexInput
{
	#include "common/vertexinput.hlsl"
};

struct PixelInput
{
	#include "common/pixelinput.hlsl"

	// The marker's position (world, no lighting offset) and the brightness scale; its forward
	// (unit) and the object's scale; this pixel's normalized device coordinates.
	nointerpolation float4 vLightVolumeOrigin : TEXCOORD14;
	nointerpolation float4 vLightVolumeAxis : TEXCOORD15;
	float2 vLightVolumeNdc : TEXCOORD16;
};

VS
{
	#include "common/vertex.hlsl"

	// Perpendicular and parallel brightness scales, and the brightness scale function's value
	// (1 when the light volume has none).
	float3 g_vLightVolumeBrightness < Default3( 1.0, 1.0, 1.0 ); >;
	// Near and far fade distances along the camera's forward (s&box units); far 0 doesn't fade.
	float2 g_vLightVolumeFade < Default2( 0.0, 0.0 ); >;

	PixelInput MainVs( VertexInput v )
	{
		// The quad's corner (-1..1) is its texture coordinate; every vertex sits on the marker.
		float2 corner = v.vTexCoord.xy;

		PixelInput i = ProcessVertex( v );
		float3 origin = i.vPositionWs.xyz;

		// The marker's forward is the object's +X; its length is the object's scale.
		float3x4 matObjectToWorld = GetTransformMatrix( v.nInstanceTransformID, v.nBoneIndex.x );
		float3 axis = mul( (float3x3)matObjectToWorld, float3( 1, 0, 0 ) );
		float scale = max( length( axis ), 0.0001 );
		axis /= scale;

		float parallel = abs( dot( g_vCameraDirWs.xyz, axis ) );
		float brightness = saturate( lerp( g_vLightVolumeBrightness.x, g_vLightVolumeBrightness.y, parallel ) ) * g_vLightVolumeBrightness.z;
		if ( g_vLightVolumeFade.y > 0 )
			brightness *= saturate( ( dot( g_vCameraDirWs.xyz, origin - g_vCameraPositionWs.xyz ) - g_vLightVolumeFade.y ) / ( g_vLightVolumeFade.x - g_vLightVolumeFade.y ) );

		// The box on screen around every sprite in front of the camera, at the nearest one's depth.
		float2 lo = 1e9;
		float2 hi = -1e9;
		float4 nearest = float4( 0, 0, 0, 1e30 );
		int count = (int)g_vLightVolumeOffset.w;
		[loop]
		for ( int k = 0; k < min( count, 64 ); k++ )
		{
			float fraction;
			float2 sprite = LightVolumeSprite( k, fraction );
			float2 ndc, halfSize;
			float4 clip;
			if ( sprite.y <= 0 || !ProjectSprite( origin + axis * sprite.x * scale, sprite.y * scale, ndc, halfSize, clip ) )
				continue;

			lo = min( lo, ndc - halfSize );
			hi = max( hi, ndc + halfSize );
			if ( clip.w < nearest.w )
				nearest = clip;
		}

		i.vLightVolumeOrigin = float4( origin, brightness );
		i.vLightVolumeAxis = float4( axis, scale );
		i.vNormalWs = -g_vCameraDirWs.xyz;
		i = FinalizeVertex( i );

		if ( brightness <= 0 || hi.x < lo.x || nearest.w > 1e29 )
		{
			i.vPositionPs = float4( 2, 2, 2, 1 );
			i.vLightVolumeNdc = 0;
			return i;
		}

		// Every corner has the nearest sprite's w, so the device coordinates interpolate linearly.
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

	SamplerState g_sLightVolume < Filter( Bilinear ); AddressU( CLAMP ); AddressV( CLAMP ); >;

	CreateInputTexture2D( LightVolume, Linear, 8, "None", "_color", ",0/,0/0", Default4( 1.00, 1.00, 1.00, 1.00 ) );
	Texture2D g_tLightVolume < Channel( RGBA, Box( LightVolume ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( false ); >;

	float4 MainPs( PixelInput i ) : SV_Target0
	{
		float3 origin = i.vLightVolumeOrigin.xyz;
		float brightness = i.vLightVolumeOrigin.w;
		float3 axis = i.vLightVolumeAxis.xyz;
		float scale = i.vLightVolumeAxis.w;
		float2 pixel = i.vLightVolumeNdc;

		// Halo's frame buffer: every sprite added in gamma space.
		float3 sum = 0;
		int count = (int)g_vLightVolumeOffset.w;
		[loop]
		for ( int k = 0; k < min( count, 64 ); k++ )
		{
			float fraction;
			float2 sprite = LightVolumeSprite( k, fraction );
			float2 ndc, halfSize;
			float4 clip;
			if ( sprite.y <= 0 || !ProjectSprite( origin + axis * sprite.x * scale, sprite.y * scale, ndc, halfSize, clip ) )
				continue;

			float2 uv = ( pixel - ndc ) / ( 2 * max( halfSize, 1e-6 ) ) + 0.5;
			if ( any( uv < 0 ) || any( uv > 1 ) )
				continue;

			float3 t = g_tLightVolume.SampleLevel( g_sLightVolume, float2( uv.x, 1 - uv.y ), 0 ).rgb;
			float3 color = saturate( lerp( g_vLightVolumeColorHither.rgb, g_vLightVolumeColorYon.rgb, Pow1( fraction, g_vLightVolumeExponents.x ) ) );
			float alpha = saturate( lerp( g_vLightVolumeColorHither.a, g_vLightVolumeColorYon.a, Pow1( fraction, g_vLightVolumeExponents.y ) ) * brightness );
			float3 t2 = t * t;
			sum += ( color * t + ( 1 - color ) * t2 * t2 * t2 ) * alpha;
		}

		// Light added in front of fog fades with it, like additive particles.
		// Halo faded added light by 1 - its fog density in gamma space; s&box's transmittance is 1 - density^2.2 (ScenarioSceneLoader.BuildFog), far too little.
		float fade = ( 1 - pow( saturate( 1 - DoAtmospherics( origin, i.vPositionSs.xy, float4( 0, 0, 0, 1 ), true ).a ), 1.0 / 2.2 ) );
		return float4( SrgbGammaToLinear( saturate( sum ) ) * fade, 1 );
	}
}
