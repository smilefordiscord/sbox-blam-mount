//
// Halo 3's post-processing (Halo3/Halo3PostFx.cs): MCC's passes and constants.
// While it runs, Halo 3 materials write x = colour * 2^E / 1.4938 (E = this pass's auto exposure, 1.4938 the tone curve's white point) unclamped: MCC's LDR target holds min( x, 1 ) and its HDR target x / 128, both blending linear x, so one float frame holds both.
//
// Halo3PostPass picks the pass:
// 0 bright pass, frame -> quarter: a 4x4 block average of the HDR value (up to 128) and of the LDR value (up to 1), each kept by max( m - T, m * inherent bloom ) / m (m = luminance, kept in alpha), the larger of the two; T = bloom point * max( 1, 2^( E * anti-bloom ) ) / 1.4938.
// 1 downsample, source -> quarter of it: a 9 tap tent at +-2 source texels (MCC's downsample_4x4_block).
// 2 exposure measure, 20x11 -> 1x1: over 4x8 bilinear taps weighted by the auto_exposure_weight bitmap, s * log2( mean luminance ) + ( 1 - s ) * mean of log2 luminance.
// 3 exposure state, 64x1: texel 0 = ( E + the screen effects' exposure boost (what the materials and the bright pass use), E, history index, 1 ), texels 1-60 the last 60 targets, 61 = ( measure, target ), 62 = ( the E before this frame's ): updated once per frame.
// 4 copy.
// 5 horizontal blur: 5 bilinear taps of a 10th row binomial (10 120 252 120 10 / 512) half a texel down.
// 6 vertical blur: the same along y half a texel right, times Halo3PostScale.
// 7 add: original * scale + the smaller level's blur.
// 8 add (alpha): original * scale * add.a + add.
// 10 motion blur, frame -> the LDR value (min( x, 1 )): MCC's displacement_motion_blur, moved first by the distortion particles' offsets (halo3_distortion.shader, half res), then 6 taps along the camera motion's screen velocity * 0.03 s / frame time (0.05 x, 0.15 y in NDC per unit, clamped to 0.03, 0.05 of the screen), fading out toward the crosshair; the HDR value isn't blurred.
// 11 underwater, frame -> frame, after the opaque world and the sky (Halo3PostFx.BuildUnderwater): MCC's underwater fog, before the water surfaces (which fog themselves) and the transparent things.
// 9 composite: the LDR value + bloom, hue/saturation matrix, contrast, tone curve, MCC's identity colour grading LUT (* 15/16); written as linear light (MCC's composite goes to an sRGB target shown as is).
//

HEADER
{
	Description = "Halo 3 post-processing";
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
	#include "common/classes/Motion.hlsl"

	RenderState( DepthEnable, false );
	RenderState( DepthWriteEnable, false );
	RenderState( BlendEnable, false );

	Texture2D g_tSource < Attribute( "Halo3PostSource" ); SrgbRead( false ); >;
	Texture2D g_tAdd < Attribute( "Halo3PostAdd" ); SrgbRead( false ); >;
	Texture2D g_tBloom < Attribute( "Halo3PostBloom" ); SrgbRead( false ); >;
	Texture2D g_tState < Attribute( "Halo3PostState" ); SrgbRead( false ); >;
	// The distortion particles' uv offsets (Halo3Distortion), when any are drawn.
	Texture2D g_tDistortion < Attribute( "Halo3PostDistortion" ); SrgbRead( false ); >;
	float g_flHasDistortion < Attribute( "Halo3HasDistortion" ); Default( 0.0 ); >;
	// The LDR value after motion blur (pass 10), for the bright pass and the composite.
	Texture2D g_tLdr < Attribute( "Halo3PostLdr" ); SrgbRead( false ); >;
	Texture2D g_tWeights < Attribute( "Halo3PostWeights" ); SrgbRead( false ); >;
	// What changes from frame to frame (Halo3PostFx.Update): texels 0-2 the hue/saturation matrix rows, 3 = ( contrast, exposure boost, 0, 0 ), 4 = ( motion blur's expected / last frame time, 1 to blur, crosshair NDC x, y ), 5 = ( max blur x, y, blur scale x, y ), 6 = ( taps, centre falloff, 0, 0 ), 7 = ( 1 underwater, murkiness, world units per s&box unit, 0 ), 8 = ( underwater fog colour, 0 ).
	Texture2D g_tParams < Attribute( "Halo3PostParams" ); SrgbRead( false ); >;
	SamplerState g_sLinear < Filter( Bilinear ); AddressU( CLAMP ); AddressV( CLAMP ); >;
	SamplerState g_sPoint < Filter( Point ); AddressU( CLAMP ); AddressV( CLAMP ); >;

	int g_nPass < Attribute( "Halo3PostPass" ); Default( 0 ); >;
	float4 g_vScale < Attribute( "Halo3PostScale" ); Default4( 1.0, 1.0, 1.0, 1.0 ); >;

	// Bloom point, inherent bloom, anti-bloom (camera fx).
	float3 g_vBloomThreshold < Attribute( "Halo3BloomThreshold" ); Default3( 1.2, 0.2, 0.0 ); >;
	// The HDR target's range: MCC writes x / 128 into a unorm target (2^render_HDR_target_stops, g_exposure.y).
	float g_flHdrRange < Attribute( "Halo3HdrRange" ); Default( 128.0 ); >;
	// Exposure measure: the share of log2( mean luminance ) (cfxs +0x24); 1 when Halo3PostWeights holds the weight bitmap.
	float g_flMeasureMix < Attribute( "Halo3ExposureMeasureMix" ); Default( 0.0 ); >;
	float g_flHasWeights < Attribute( "Halo3HasExposureWeights" ); Default( 0.0 ); >;
	// Exposure: x = the stops to start from (and the fixed exposure), y = 1 for auto exposure, z = targets that must agree (1-60), w = rate per frame; range min, max, auto exposure target brightness.
	float4 g_vExposure < Attribute( "Halo3ExposureControl" ); Default4( 0.0, 1.0, 1.0, 0.01 ); >;
	float3 g_vExposureRange < Attribute( "Halo3ExposureRange" ); Default3( -6.0, 6.0, -4.8365 ); >;
	// Composite: tone curve min( c, x ) * ( y + z c + w c^2 ).
	float4 g_vToneCurve < Attribute( "Halo3ToneCurve" ); Default4( 1.0, 1.5, 0.0, -0.5 ); >;
	// Debug views (halo3_postfx_debug): 1 the bloom alone, 2 the frame without bloom, 4 the exposure as grey (-6..6 stops), 5 a linear ramp (32 steps), 6 the frame's luminance / 16, 7 the frame as it is.
	int g_nDebug < Attribute( "Halo3PostDebug" ); Default( 0 ); >;

	#define Luma float3( 0.299, 0.587, 0.114 )
	static const float WhitePoint = 1.4938016;
	static const int History = 60;

	float2 TexelOf( Texture2D t )
	{
		float2 size;
		t.GetDimensions( size.x, size.y );
		return 1.0 / size;
	}

	float4 Tap( float2 uv )
	{
		return g_tSource.SampleLevel( g_sLinear, uv, 0 );
	}

	float Stops()
	{
		return g_tState.Load( int3( 0, 0, 0 ) ).x;
	}

	// downsample_4x4_block_bloom / downsize_2x_to_bloom: c *= max( m - T, m * inherent ) / m, luminance in alpha.
	float4 KeepBright( float3 c, float threshold )
	{
		float m = dot( c, Luma );
		float keep = max( m - threshold, m * g_vBloomThreshold.y ) / max( m, 1e-6 );
		return float4( c * keep, m );
	}

	float4 BrightPass( float2 uv, float2 texel )
	{
		float3 hdr = 0, ldr = 0;
		[unroll] for ( int k = 0; k < 4; k++ )
		{
			// MCC's bilinear taps between 2x2 texels average the targets' clamped values.
			float2 at = uv + float2( ( k & 1 ) ? 1 : -1, ( k & 2 ) ? 1 : -1 ) * texel;
			float2 base = ( floor( at / texel - 0.5 ) + 0.5 ) * texel;
			float2 f = frac( at / texel - 0.5 );
			float3 x00 = g_tSource.SampleLevel( g_sPoint, base, 0 ).rgb;
			float3 x10 = g_tSource.SampleLevel( g_sPoint, base + float2( texel.x, 0 ), 0 ).rgb;
			float3 x01 = g_tSource.SampleLevel( g_sPoint, base + float2( 0, texel.y ), 0 ).rgb;
			float3 x11 = g_tSource.SampleLevel( g_sPoint, base + texel, 0 ).rgb;
			float4 w = float4( ( 1 - f.x ) * ( 1 - f.y ), f.x * ( 1 - f.y ), ( 1 - f.x ) * f.y, f.x * f.y );
			hdr += clamp( x00, 0, g_flHdrRange ) * w.x + clamp( x10, 0, g_flHdrRange ) * w.y + clamp( x01, 0, g_flHdrRange ) * w.z + clamp( x11, 0, g_flHdrRange ) * w.w;
			ldr += g_tLdr.SampleLevel( g_sLinear, at, 0 ).rgb;
		}
		float threshold = g_vBloomThreshold.x * max( 1, exp2( Stops() * g_vBloomThreshold.z ) ) / WhitePoint;
		return max( KeepBright( hdr * 0.25, threshold ), KeepBright( ldr * 0.25, threshold ) );
	}

	float4 Downsample( float2 uv, float2 texel )
	{
		float4 sum = Tap( uv ) * 0.25;
		sum += ( Tap( uv + texel * float2( -2, 0 ) ) + Tap( uv + texel * float2( 2, 0 ) ) + Tap( uv + texel * float2( 0, -2 ) ) + Tap( uv + texel * float2( 0, 2 ) ) ) * 0.125;
		sum += ( Tap( uv + texel * float2( -2, -2 ) ) + Tap( uv + texel * float2( 2, -2 ) ) + Tap( uv + texel * float2( -2, 2 ) ) + Tap( uv + texel * float2( 2, 2 ) ) ) * 0.0625;
		return sum;
	}

	// exposure_downsample as compiled: 4 columns (-3..3) by 8 rows (-7..7) of source texels about the middle, so only the screen's centre counts.
	float4 MeasureExposure( float2 uv, float2 texel )
	{
		float3 sum = 0;
		[unroll] for ( int x = -3; x <= 3; x += 2 )
		{
			[unroll] for ( int y = -7; y <= 7; y += 2 )
			{
				float2 at = uv + float2( x, y ) * texel;
				float w = g_flHasWeights > 0.5 ? g_tWeights.SampleLevel( g_sLinear, at, 0 ).g : 1.0;
				float l = g_tSource.SampleLevel( g_sLinear, at, 0 ).a;
				sum += w * float3( log2( l + 1e-5 ), l, 1 );
			}
		}
		float2 mean = sum.xy / max( sum.z, 1e-6 );
		return float4( g_flMeasureMix * log2( mean.y + 1e-5 ) + ( 1 - g_flMeasureMix ) * mean.x, mean.y, 0, 1 );
	}

	// Exposure update: target = clamp( auto ? brightness + E_before - measure : fixed, min, max ); E moves by min( 0.05 |E - target|, rate ) of the way, only when the last n targets are all on the same side of it.
	float4 ExposureState( int texel )
	{
		float4 last = g_tState.Load( int3( 0, 0, 0 ) );
		float earlier = g_tState.Load( int3( History + 2, 0, 0 ) ).x;
		bool fresh = !( last.w > 0.5 && last.w < 1.5 ) || !( abs( last.y ) < 64 ) || !( abs( earlier ) < 64 );
		float e = fresh ? g_vExposure.x : last.y;
		float before = fresh ? e : earlier;
		int at = fresh ? 0 : (int)last.z;

		float measure = g_tAdd.Load( int3( 0, 0, 0 ) ).x;
		float target = g_vExposure.y > 0.5 ? g_vExposureRange.z + before - measure : g_vExposure.x;
		target = clamp( target, g_vExposureRange.x, g_vExposureRange.y );
		int slot = at % History;

		if ( texel >= 1 && texel <= History )
		{
			if ( texel - 1 == slot )
				return float4( target, 0, 0, 1 );
			return fresh ? float4( e, 0, 0, 1 ) : g_tState.Load( int3( texel, 0, 0 ) );
		}
		if ( texel == History + 1 )
			return float4( measure, target, 0, 1 );
		if ( texel == History + 2 )
			return float4( e, 0, 0, 1 );
		if ( texel > History )
			return 0;

		float lowest = target, highest = target;
		int agree = (int)clamp( g_vExposure.z, 1, History );
		for ( int k = 1; k < agree; k++ )
		{
			float t = fresh ? e : g_tState.Load( int3( 1 + ( slot - k + History ) % History, 0, 0 ) ).x;
			lowest = min( lowest, t );
			highest = max( highest, t );
		}
		float next = e;
		if ( lowest > e || highest < e )
		{
			float a = min( 0.05 * abs( e - target ), saturate( g_vExposure.w ) );
			next = e + a * ( target - e );
		}
		float boost = g_tParams.Load( int3( 3, 0, 0 ) ).y;
		return float4( next + boost, next, ( slot + 1 ) % History, 1 );
	}

	float4 BlurHorizontal( float2 uv, float2 texel )
	{
		float4 sum = Tap( uv + texel * float2( -4.1, 0.5 ) ) * 10;
		sum += Tap( uv + texel * float2( -2.3, 0.5 ) ) * 120;
		sum += Tap( uv + texel * float2( -0.5, 0.5 ) ) * 252;
		sum += Tap( uv + texel * float2( 1.3, 0.5 ) ) * 120;
		sum += Tap( uv + texel * float2( 3.1, 0.5 ) ) * 10;
		return sum / 512;
	}

	float4 BlurVertical( float2 uv, float2 texel )
	{
		float4 sum = Tap( uv + texel * float2( 0.5, -4.1 ) ) * 0.0195312;
		sum += Tap( uv + texel * float2( 0.5, -2.3 ) ) * 0.234375;
		sum += Tap( uv + texel * float2( 0.5, -0.5 ) ) * 0.492188;
		sum += Tap( uv + texel * float2( 0.5, 1.3 ) ) * 0.234375;
		sum += Tap( uv + texel * float2( 0.5, 3.1 ) ) * 0.0195312;
		return sum * g_vScale;
	}

	float3 ToneCurve( float3 c )
	{
		c = min( c, g_vToneCurve.x );
		return c * ( g_vToneCurve.y + c * ( g_vToneCurve.z + c * g_vToneCurve.w ) );
	}

	float4 Composite( float2 uv )
	{
		float4 frame = g_tSource.SampleLevel( g_sPoint, uv, 0 );
		float3 bloom = g_tBloom.SampleLevel( g_sLinear, uv, 0 ).rgb;
		if ( g_nDebug == 1 )
			return float4( bloom, frame.a );
		if ( g_nDebug == 2 )
			bloom = 0;
		if ( g_nDebug == 4 )
			return float4( saturate( ( Stops() + 6 ) / 12 ).xxx, frame.a );
		if ( g_nDebug == 5 )
			return float4( ( floor( uv.x * 32 ) / 31 ).xxx, frame.a );
		if ( g_nDebug == 6 )
			return float4( saturate( dot( frame.rgb, Luma ) / 16 ).xxx, frame.a );
		if ( g_nDebug == 7 )
			return float4( saturate( frame.rgb ), frame.a );

		// The LDR target holds x up to 1, motion blurred.
		float4 c = float4( g_tLdr.SampleLevel( g_sPoint, uv, 0 ).rgb + bloom, 1 );
		float3 graded = float3( dot( c, g_tParams.Load( int3( 0, 0, 0 ) ) ), dot( c, g_tParams.Load( int3( 1, 0, 0 ) ) ), dot( c, g_tParams.Load( int3( 2, 0, 0 ) ) ) );
		float contrast = g_tParams.Load( int3( 3, 0, 0 ) ).x;
		float average = dot( graded, 0.333 );
		if ( average > 0 )
			graded *= exp2( log2( average ) * contrast ) / average;

		// MCC's identity colour grading LUT holds trunc( 255 i / 16 ) at texel i but is sampled at c * 15/16 + 1/32 as if it held i / 15: c * 15/16, white at 239/255.
		return float4( ToneCurve( graded ) * ( 15.0 / 16.0 ), frame.a );
	}

	// MCC's underwater pass (water_ripple shadow_apply) on the LDR value: fog + 0.5 * e^( -murkiness * distance ) * ( scene - fog ), written to its HDR target too, so the value is clipped.
	float3 Underwater( float3 c, float2 uv )
	{
		float4 water = g_tParams.Load( int3( 7, 0, 0 ) );
		if ( water.x < 0.5 )
			return c;
		float3 fog = g_tParams.Load( int3( 8, 0, 0 ) ).rgb;
		float d = length( Depth::GetWorldPosition( uv * g_vViewportSize ) - g_vCameraPositionWs ) * water.z;
		return max( fog + 0.5 * min( exp( -water.y * d ), 1 ) * ( c - fog ), 0 );
	}

	float4 MotionBlur( float2 uv, float2 pixel )
	{
		if ( g_flHasDistortion > 0.5 )
			uv += g_tDistortion.SampleLevel( g_sLinear, uv, 0 ).xy;
		float3 c = saturate( g_tSource.SampleLevel( g_sLinear, uv, 0 ).rgb );
		float4 motion = g_tParams.Load( int3( 4, 0, 0 ) );
		if ( motion.y < 0.5 )
			return float4( c, 1 );

		float4 blur = g_tParams.Load( int3( 5, 0, 0 ) );
		float2 taps = g_tParams.Load( int3( 6, 0, 0 ) ).xy;
		float2 ndc = float2( uv.x * 2 - 1, 1 - uv.y * 2 );
		float2 fromCrosshair = ndc - motion.zw;
		float fall = min( dot( fromCrosshair, fromCrosshair ) * taps.y, 1 );
		if ( fall <= 0.01 )
			return float4( c, 1 );

		// Where this pixel was last frame (s&box's reprojection of the camera's motion), as an NDC move; MCC's y scale is negative, NDC y up to uv y down.
		float2 previous = Motion::Get( pixel ).xy;
		float2 moved = ( pixel - previous ) * g_vInvViewportSize * float2( 2, -2 );
		float2 velocity = clamp( moved * blur.zw * float2( motion.x, -motion.x ), -blur.xy, blur.xy );

		int count = (int)taps.x;
		float3 sum = 0;
		for ( int k = 1; k <= count; k++ )
			sum += saturate( g_tSource.SampleLevel( g_sLinear, uv + velocity * ( (float)k / count ), 0 ).rgb );
		return float4( c + ( sum / count - c ) * fall, 1 );
	}

	float4 MainPs( PixelInput i ) : SV_Target0
	{
		float2 uv = i.vTexCoord;
		float2 texel = TexelOf( g_tSource );

		switch ( g_nPass )
		{
			case 0: return BrightPass( uv, texel );
			case 1: return Downsample( uv, texel );
			case 2: return MeasureExposure( uv, texel );
			case 3: return ExposureState( (int)i.vPositionSs.x );
			case 4: return g_tSource.Load( int3( (int)i.vPositionSs.x, (int)i.vPositionSs.y, 0 ) );
			case 5: return BlurHorizontal( uv, texel );
			case 6: return BlurVertical( uv, texel );
			case 7: return float4( g_tSource.SampleLevel( g_sLinear, uv, 0 ).rgb * g_vScale.rgb + g_tAdd.SampleLevel( g_sLinear, uv, 0 ).rgb, 1 );
			case 10: return MotionBlur( uv, i.vPositionSs.xy );
			case 11:
			{
				float4 frame = g_tSource.Load( int3( (int)i.vPositionSs.x, (int)i.vPositionSs.y, 0 ) );
				return float4( Underwater( saturate( frame.rgb ), uv ), frame.a );
			}
			case 8:
			{
				float4 add = g_tAdd.SampleLevel( g_sLinear, uv, 0 );
				return float4( g_tSource.SampleLevel( g_sLinear, uv, 0 ).rgb * g_vScale.rgb * add.a + add.rgb, add.a );
			}
		}
		return Composite( uv );
	}
}
