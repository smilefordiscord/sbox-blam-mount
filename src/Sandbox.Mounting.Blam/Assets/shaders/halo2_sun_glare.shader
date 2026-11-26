//
// Halo 2's sun glare (MCC classic), the crisp disc over a level's sun: the passes Halo2/SunGlare.cs runs after the bloom
// (scratch research notes, overnight/effects/sun_glare_re.md). The sun's square is a view-facing square 64 world units
// across half at the far plane (1024): half a sixteenth of the view's tangent, wherever the sun is on screen.
//
// HaloSunPass picks the pass:
// 0 mask, square target: where the square shows sky (nothing drawn in front of the far plane), rasterizer\glow's cone
//   a gives rgb sat(4a) and a mask sat(1.50588a) in alpha (sunTexture); elsewhere nothing.
// 1 blur: the 3x3 tent of the four texel corners on the diagonals (black past the edge), times HaloSunBlurWeight / 2
//   (1 on the first pass, 127/255 after: sunshine), saturated, times the mask, which stays in alpha.
// 2 composite: 16 copies of the result over the square, 0.875 to 3.22 times its size ((28 + 5i) / 32), each weighted
//   I / (i + 1), dim parts tinted warm, added to the frame in Halo's gamma space and clipped at white. I fades the glare
//   as the sun leaves the view's centre (normalised to a 4:3 reference view) and with the sky fog.
//

HEADER
{
	Description = "Halo 2 sun glare";
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

	Texture2D g_tSource < Attribute( "HaloSunSource" ); SrgbRead( false ); >;
	Texture2D g_tFrame < Attribute( "HaloSunFrame" ); SrgbRead( false ); >;
	Texture2D g_tGlow < Attribute( "HaloSunGlow" ); SrgbRead( false ); >;
	SamplerState g_sLinear < Filter( Bilinear ); AddressU( CLAMP ); AddressV( CLAMP ); >;
	SamplerState g_sPoint < Filter( Point ); AddressU( CLAMP ); AddressV( CLAMP ); >;

	int g_nPass < Attribute( "HaloSunPass" ); Default( 0 ); >;
	float g_flBlurWeight < Attribute( "HaloSunBlurWeight" ); Default( 1.0 ); >;
	// The direction toward the sun (world), and 1 - the sky fog's density.
	float3 g_vSunDirection < Attribute( "HaloSunDirection" ); Default3( 0, 0, 1 ); >;
	float g_flSkyFogClear < Attribute( "HaloSunSkyFogClear" ); Default( 1.0 ); >;

	float3 GammaToLinear( float3 c )
	{
		c = max( c, 0 );
		return lerp( pow( ( c + 0.055 ) / 1.055, 2.4 ), c / 12.92, step( c, 0.04045 ) );
	}

	float3 LinearToGamma( float3 c )
	{
		c = saturate( c );
		return lerp( 1.055 * pow( c, 1.0 / 2.4 ) - 0.055, c * 12.92, step( c, 0.0031308 ) );
	}

	// The sun's square in screen uv (y down): its centre and half size; false when the sun is behind the view.
	bool SunSquare( out float2 centre, out float2 extent, out float c )
	{
		float3 forward = normalize( g_vCameraDirWs.xyz );
		float3 right = normalize( cross( forward, g_vCameraUpDirWs.xyz ) );
		float3 up = cross( right, forward );
		float3 d = normalize( g_vSunDirection );
		float2 p = float2( g_matViewToProjection[0][0], g_matViewToProjection[1][1] );

		c = dot( d, forward );
		float2 ndc = float2( dot( d, right ), dot( d, up ) ) / max( c, 1e-6 ) * p;
		centre = float2( 0.5 + 0.5 * ndc.x, 0.5 - 0.5 * ndc.y );
		extent = 0.5 * 0.0625 * p;
		return c > 0;
	}

	// I: 1 looking straight at the sun, down to 0 at 45 degrees off as a 4:3 view of Halo's default field of view sees
	// it (wider views fade it less: k), times the sky fog's clearness.
	float Intensity( float c, float2 p )
	{
		float tanH = 1 / p.x;
		float tanV = 1 / p.y;
		float sinH = tanH / sqrt( 1 + tanH * tanH );
		float sinV = tanV / sqrt( 1 + tanV * tanV );
		float aspect = tanH / tanV;
		float k = min( 1, 0.67966 / sqrt( sinV * sinV + sinH * sinH ) * ( 4.0 / 3.0 ) / aspect );
		float s = k * sqrt( saturate( 1 - c * c ) );
		float cAdjusted = sqrt( saturate( 1 - s * s ) );
		return saturate( ( cAdjusted - 0.70711 ) / 0.29289 ) * saturate( g_flSkyFogClear );
	}

	// The 3x3 tent around a texel: its four corners, each the mean of the four texels around it, black past the edge.
	float3 Corner( int2 at, int2 size )
	{
		float3 sum = 0;
		[unroll] for ( int y = 0; y < 2; y++ )
			[unroll] for ( int x = 0; x < 2; x++ )
			{
				int2 t = at + int2( x, y );
				if ( all( t >= 0 ) && all( t < size ) )
					sum += g_tSource.Load( int3( t, 0 ) ).rgb;
			}
		return sum * 0.25;
	}

	float4 MainPs( PixelInput i ) : SV_Target0
	{
		float2 uv = i.vTexCoord;

		if ( g_nPass == 0 )
		{
			float2 centre, extent;
			float c;
			if ( !SunSquare( centre, extent, c ) )
				return 0;

			// The screen pixel this texel of the square stands for; sky where nothing was drawn in front of the far
			// plane (reversed depth: 0 there).
			float2 screen = centre - extent + uv * 2 * extent;
			if ( any( screen < 0 ) || any( screen > 1 ) )
				return 0;
			float2 size;
			g_tDepthChain.GetDimensions( size.x, size.y );
			if ( Depth::Get( screen * size ) > 1e-7 )
				return 0;

			float a = g_tGlow.SampleLevel( g_sLinear, uv, 0 ).a;
			return float4( saturate( 4 * a ).xxx, saturate( 1.505883 * a ) );
		}

		if ( g_nPass == 1 )
		{
			int2 size;
			g_tSource.GetDimensions( size.x, size.y );
			int2 at = int2( i.vPositionSs.xy );
			float3 sum = Corner( at - 1, size ) + Corner( at, size ) + Corner( at + int2( -1, 0 ), size ) + Corner( at + int2( 0, -1 ), size );
			float mask = g_tSource.Load( int3( at, 0 ) ).a;
			return float4( saturate( saturate( g_flBlurWeight * 0.5 * sum ) * mask ), mask );
		}

		// Composite.
		float4 frame = g_tFrame.SampleLevel( g_sPoint, uv, 0 );
		float2 centre, extent;
		float c;
		if ( !SunSquare( centre, extent, c ) )
			return frame;

		float intensity = Intensity( c, float2( g_matViewToProjection[0][0], g_matViewToProjection[1][1] ) );
		if ( intensity <= 0 )
			return frame;

		float3 glare = 0;
		[loop] for ( int k = 0; k < 16; k++ )
		{
			float2 h = extent * ( 28 + 5 * k ) / 32.0;
			float2 at = ( uv - ( centre - h ) ) / ( 2 * h );
			if ( any( at < 0 ) || any( at > 1 ) )
				continue;

			float3 t = g_tSource.SampleLevel( g_sLinear, at, 0 ).rgb;
			float b = saturate( 2 * t.b - 1 );
			glare += t * lerp( float3( 0.690196, 0.690196, 0.501961 ), 1, b ) * intensity / ( k + 1 );
		}

		float3 g = LinearToGamma( frame.rgb );
		float3 added = saturate( g + glare );
		return float4( frame.rgb + GammaToLinear( added ) - GammaToLinear( g ), frame.a );
	}
}
