//
// Halo CE's weapon HUD screen effect (the "screen effect" block of a weapon HUD interface): the
// pistol, rocket launcher and sniper scopes.
//
// The mask bitmap is mapped onto the screen centred, one mask pixel per H/960 screen pixels (MCC's
// HUD scale), its edge texels clamped outwards. Its two channels mean different things:
//   - alpha is how much of the blurred frame to use (0 = sharp);
//   - colour (Y) darkens: frame * (1 - Y). The pistol circle is Y = 0 inside, a thin Y = 255 ring
//     makes its black outline, and Y = 132 outside leaves the world at 48%.
// The blur is Halo's "warp" convolution: a radial zoom blur that averages three copies of the frame
// scaled about the screen centre by 1, 1 - r/size and 1 + r/size (size = the viewport in 480-line
// pixels, per axis), done twice, each pass mixed in by the mask alpha. r grows as the camera zooms
// (the tag's convolution field of view bounds), worked out on the CPU.
//
// The sniper also has light enhancement (night vision, with the flashlight while zoomed):
// c' = lerp( c, 1 - (1 - c)^4, k ), and additive desaturation: c' += k * tint * average( c' ),
// weighted by (1 - Y) when the tag says "masked".
//
// Halo did all of this in gamma space on the 8-bit frame. The frame copy here is read as linear
// (sRGB decoded), so it is encoded to gamma for Halo's maths and decoded again on output.
//

HEADER
{
	Description = "Halo CE weapon HUD screen effect (scope mask, warp blur, night vision)";
	DevShader = true;
}

MODES
{
	Default();
	Forward();
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
	float2 uv : TEXCOORD0;

	// VS only
	#if ( PROGRAM == VFX_PROGRAM_VS )
		float4 vPositionPs		: SV_Position;
	#endif

	// PS only
	#if ( ( PROGRAM == VFX_PROGRAM_PS ) )
		float4 vPositionSs		: SV_Position;
	#endif
};

VS
{
	PixelInput MainVs( VertexInput i )
	{
		PixelInput o;
		o.vPositionPs = float4( i.vPositionOs.xy, 0.0f, 1.0f );
		o.uv = i.vTexCoord;
		return o;
	}
}

PS
{
	#include "postprocess/common.hlsl"

	RenderState( BlendEnable, false );

	SamplerState g_sHaloClamp < Filter( Bilinear ); AddressU( CLAMP ); AddressV( CLAMP ); >;

	Texture2D g_tColorBuffer < Attribute( "ColorBuffer" ); SrgbRead( true ); >;
	Texture2D g_tScopeMask < Attribute( "HaloScopeMask" ); SrgbRead( false ); >;

	// Screen uv offset from the centre -> mask uv offset: (aspect * 960 / mask width, 960 / mask height).
	float2 g_vMaskScale < Attribute( "HaloMaskScale" ); Default2( 1.0, 1.0 ); >;

	// 1 when there is a mask, 0 to skip it (no darkening, the whole screen takes the blur).
	float g_flHasMask < Attribute( "HaloHasMask" ); Default( 1.0 ); >;

	// The warp radius over the viewport size in 480-line pixels, per axis: (r / (480 * aspect), r / 480).
	float2 g_vWarp < Attribute( "HaloWarp" ); Default2( 0.0, 0.0 ); >;

	// Light enhancement (night vision) intensity, and whether it uses the mask.
	float g_flLightEnhancement < Attribute( "HaloLightEnhancement" ); Default( 0.0 ); >;
	float g_flLightEnhancementMasked < Attribute( "HaloLightEnhancementMasked" ); Default( 0.0 ); >;

	// Desaturation intensity, tint, additive or not, masked or not.
	float g_flDesaturation < Attribute( "HaloDesaturation" ); Default( 0.0 ); >;
	float3 g_vDesaturationTint < Attribute( "HaloDesaturationTint" ); Default3( 1.0, 1.0, 1.0 ); >;
	float g_flDesaturationAdditive < Attribute( "HaloDesaturationAdditive" ); Default( 0.0 ); >;
	float g_flDesaturationMasked < Attribute( "HaloDesaturationMasked" ); Default( 0.0 ); >;

	float3 HaloToGamma( float3 c )
	{
		c = saturate( c );
		return lerp( c * 12.92, 1.055 * pow( c, 1.0 / 2.4 ) - 0.055, step( 0.0031308, c ) );
	}

	float3 HaloToLinear( float3 c )
	{
		c = saturate( c );
		return lerp( c / 12.92, pow( ( c + 0.055 ) / 1.055, 2.4 ), step( 0.04045, c ) );
	}

	// The frame in gamma space.
	float3 Frame( float2 uv )
	{
		return HaloToGamma( g_tColorBuffer.SampleLevel( g_sHaloClamp, uv, 0 ).rgb );
	}

	// The mask at a screen uv: colour (Y) in rgb, blur amount in a. Without a mask: no darkening, full blur.
	float4 Mask( float2 uv )
	{
		if ( g_flHasMask < 0.5 )
			return float4( 0, 0, 0, 1 );

		return g_tScopeMask.SampleLevel( g_sHaloClamp, 0.5 + ( uv - 0.5 ) * g_vMaskScale, 0 );
	}

	// One warp pass's three samples: the frame scaled about the centre by 1, 1 - r/size and 1 + r/size.
	float2 Shrink( float2 uv ) { return 0.5 + ( uv - 0.5 ) * ( 1.0 - g_vWarp ); }
	float2 Grow( float2 uv ) { return 0.5 + ( uv - 0.5 ) * ( 1.0 + g_vWarp ); }

	// The first pass at a point: the frame there, mixed with its warp average by the mask alpha there.
	float3 FirstPass( float2 uv )
	{
		float3 c = Frame( uv );
		float3 warped = ( c + Frame( Shrink( uv ) ) + Frame( Grow( uv ) ) ) / 3.0;
		return lerp( c, warped, Mask( uv ).a );
	}

	float4 MainPs( PixelInput i ) : SV_Target0
	{
		float2 uv = CalculateViewportUv( i.vPositionSs.xy );
		float4 mask = Mask( uv );

		float3 c;
		if ( g_vWarp.x > 0.0 || g_vWarp.y > 0.0 )
		{
			// The second pass reads the first pass's result at the same three points.
			float3 first = FirstPass( uv );
			float3 warped = ( first + FirstPass( Shrink( uv ) ) + FirstPass( Grow( uv ) ) ) / 3.0;
			c = lerp( first, warped, mask.a );
		}
		else
		{
			c = Frame( uv );
		}

		// The mask colour darkens.
		c *= 1.0 - mask.rgb;

		// Light enhancement (night vision).
		if ( g_flLightEnhancement > 0.0 )
		{
			float k = g_flLightEnhancement * ( g_flLightEnhancementMasked > 0.5 ? 1.0 - mask.r : 1.0 );
			float3 inverse = 1.0 - c;
			inverse *= inverse;
			c = lerp( c, 1.0 - inverse * inverse, k );
		}

		// Desaturation: additive adds the tinted average, otherwise blends towards it.
		if ( g_flDesaturation > 0.0 )
		{
			float k = g_flDesaturation * ( g_flDesaturationMasked > 0.5 ? 1.0 - mask.r : 1.0 );
			float average = ( c.r + c.g + c.b ) / 3.0;
			if ( g_flDesaturationAdditive > 0.5 )
				c += k * g_vDesaturationTint * average;
			else
				c = lerp( c, g_vDesaturationTint * average, k );
		}

		return float4( HaloToLinear( c ), 1.0 );
	}
}
