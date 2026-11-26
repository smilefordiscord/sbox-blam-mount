//
// Halo 2's bloom (MCC classic), the passes Halo2/Bloom.cs runs on the finished frame (scratch research notes,
// bloom.md). Halo works on its 8-bit gamma space frame: the frame is read through the sRGB curve and the result
// goes back the same way.
//
// HaloBloomPass picks the pass:
// 0 bright pass, frame -> quarter-ish target: four bilinear taps on the diagonals, averaged, sat( 4 (avg - threshold) )
//   per channel (Halo's mask path, the frame's alpha from first person bloom layers, isn't drawn: no first person
//   view here); the average is scaled by HaloBloomInputScale first, as MCC's bloom sees its frame brighter than its
//   screen shows it (Halo2/Bloom.cs).
// 1 copy: one bilinear tap, each target half the last.
// 2 blur: four bilinear taps on the diagonals HaloBloomScale half texels out, averaged.
// 3 composite: twice the blur times the brightness, laid over the frame with Halo's default "silver bullet" blend,
//   a screen (frame + bloom (1 - frame)).
// Halo's targets are 480, 240 and 120 pixels tall whatever the screen; s&box's are fractions of the view, so tap
// offsets are taken from Halo's sizes (HaloTargetSize) to blur as far across the screen as Halo does.
//

HEADER
{
	Description = "Halo 2 bloom";
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

	RenderState( DepthEnable, false );
	RenderState( DepthWriteEnable, false );
	RenderState( BlendEnable, false );

	Texture2D g_tSource < Attribute( "HaloBloomSource" ); SrgbRead( false ); >;
	Texture2D g_tFrame < Attribute( "HaloBloomFrame" ); SrgbRead( false ); >;
	SamplerState g_sLinear < Filter( Bilinear ); AddressU( CLAMP ); AddressV( CLAMP ); >;
	SamplerState g_sPoint < Filter( Point ); AddressU( CLAMP ); AddressV( CLAMP ); >;

	int g_nPass < Attribute( "HaloBloomPass" ); Default( 0 ); >;
	float g_flScale < Attribute( "HaloBloomScale" ); Default( 1.0 ); >;
	float g_flThreshold < Attribute( "HaloBloomThreshold" ); Default( 0.9 ); >;
	float g_flInputScale < Attribute( "HaloBloomInputScale" ); Default( 1.0 ); >;
	float g_flBrightness < Attribute( "HaloBloomBrightness" ); Default( 0.375 ); >;

	// The sRGB curve both ways, so the frame comes back unchanged where nothing is added.
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

	// Halo's smallest bloom target for a screen of this shape: 120 tall, as wide as the shape makes it.
	float2 HaloTargetSize( float aspect )
	{
		return float2( floor( 120 * aspect ), 120 );
	}

	float4 Diagonals( Texture2D t, float2 uv, float2 d, bool gamma )
	{
		float4 a = t.SampleLevel( g_sLinear, uv + float2( d.x, d.y ), 0 );
		float4 b = t.SampleLevel( g_sLinear, uv + float2( -d.x, d.y ), 0 );
		float4 c = t.SampleLevel( g_sLinear, uv + float2( d.x, -d.y ), 0 );
		float4 e = t.SampleLevel( g_sLinear, uv + float2( -d.x, -d.y ), 0 );
		if ( gamma )
		{
			a.rgb = LinearToGamma( a.rgb );
			b.rgb = LinearToGamma( b.rgb );
			c.rgb = LinearToGamma( c.rgb );
			e.rgb = LinearToGamma( e.rgb );
		}
		return ( a + b + c + e ) * 0.25;
	}

	float4 MainPs( PixelInput i ) : SV_Target0
	{
		float2 uv = i.vTexCoord;
		float2 size;
		g_tSource.GetDimensions( size.x, size.y );
		float2 halo = HaloTargetSize( size.x / size.y );

		if ( g_nPass == 0 )
		{
			// Halo's tap offset: a quarter of the whole screen pixels per smallest-target texel (about one texel
			// of its first target).
			float2 d = floor( size / halo ) * 0.25 / size;
			float4 avg = Diagonals( g_tSource, uv, d, true );
			return float4( saturate( 4 * ( avg.rgb * g_flInputScale - g_flThreshold ) ), 0 );
		}

		if ( g_nPass == 1 )
			return g_tSource.SampleLevel( g_sLinear, uv, 0 );

		if ( g_nPass == 2 )
		{
			float4 avg = Diagonals( g_tSource, uv, 0.5 * g_flScale / halo, false );
			return float4( avg.rgb, saturate( 1.4 * avg.a ) );
		}

		// Composite.
		float4 frame = g_tFrame.SampleLevel( g_sPoint, uv, 0 );
		float3 bloom = saturate( 2 * g_tSource.SampleLevel( g_sLinear, uv, 0 ).rgb * g_flBrightness );
		float3 g = LinearToGamma( frame.rgb );
		float3 screened = g + bloom * ( 1 - g );
		return float4( frame.rgb + GammaToLinear( screened ) - GammaToLinear( g ), frame.a );
	}
}
