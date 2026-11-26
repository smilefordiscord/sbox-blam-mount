//
// Halo CE shader_transparent_meter: gauges on vehicles and weapons (the Warthog's speedometer,
// the plasma weapons' heat displays).
//
// The map's gray level is a position along the meter and its alpha a mask (zero alpha is cut
// out). Texels before the meter value are lit with a gradient from the min to the max color,
// which it reaches at the gradient value, plus a flash color over the last eighth before the
// flash extension; texels past the value get the background color. The result, scaled by the
// map's alpha, is added to what's behind, which is multiplied by the tint color ("tint mode 2":
// the result is tinted instead, and what's behind scaled by the meter or background
// transparency). The values come from object functions, fixed per material at their rest values.
// A game drives them by setting the object's functions on the renderer as HaloFunctionA-D (float,
// 0-1): each of the meter's value, gradient, flash extension, brightness and flash brightness
// that follows a function (A-D out) takes the game's value where it's set (0 or more), and the
// material's otherwise. The plasma rifle's gauge follows B out (its heat, at least 0.1), the plasma
// pistol's D out (its charge), the Warthog's speedometer and engine meters C out (its speed).
//

HEADER
{
	Description = "Halo CE meter shader";
}

FEATURES
{
	#include "common/features.hlsl"
	Feature( F_TWO_SIDED, 0..1, "Rendering" );
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

	// Decal meters lie on the surface they're drawn over: pull them toward the camera a little.
	float g_flMeterDecal < Default( 0.0 ); >;
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
		if ( g_flMeterDecal > 0.5 )
		{
			float3 toCamera = g_vCameraPositionWs.xyz - i.vPositionWs.xyz;
			float cameraDistance = length( toCamera );
			i.vPositionWs.xyz += toCamera / max( cameraDistance, 0.001 ) * min( 0.05 + cameraDistance * 0.001, cameraDistance * 0.5 );
			i.vPositionPs = Position3WsToPs( i.vPositionWs.xyz );
		}
		return FinalizeVertex( i );
	}
}

PS
{
	#include "common/pixel.hlsl"

	StaticCombo( S_TWO_SIDED, F_TWO_SIDED, Sys( ALL ) );
	RenderState( CullMode, S_TWO_SIDED ? NONE : DEFAULT );

	// out = color + background * alpha. Halo's blend factors were constants (the tint color, or
	// the brightness), folded into the color and alpha here.
	RenderState( BlendEnable, true );
	RenderState( SrcBlend, ONE );
	RenderState( DstBlend, SRC_ALPHA );
	RenderState( BlendOp, ADD );
	RenderState( SrcBlendAlpha, ZERO );
	RenderState( DstBlendAlpha, ONE );

	SamplerState g_sMeterLinear < Filter( Bilinear ); AddressU( WRAP ); AddressV( WRAP ); >;
	SamplerState g_sMeterPoint < Filter( POINT ); AddressU( WRAP ); AddressV( WRAP ); >;

	CreateInputTexture2D( MeterMap, Linear, 8, "None", "_color", ",0/,0/0", Default4( 1.00, 1.00, 1.00, 1.00 ) );
	Texture2D g_tMeterMap < Channel( RGBA, Box( MeterMap ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( false ); >;
	float g_flMeterUnfiltered < Default( 0.0 ); >;

	float3 g_vMeterGradientMin < Default3( 0.0, 0.0, 0.0 ); >;
	float3 g_vMeterGradientMax < Default3( 1.0, 1.0, 1.0 ); >;
	float3 g_vMeterBackground < Default3( 0.0, 0.0, 0.0 ); >;

	// Flash color, negative to subtract.
	float3 g_vMeterFlash < Default3( 0.0, 0.0, 0.0 ); >;

	// Meter value, gradient value, flash extension.
	float3 g_vMeterValues < Default3( 1.0, 1.0, 1.0 ); >;

	// Brightness and flash brightness.
	float2 g_vMeterBrightness < Default2( 1.0, 1.0 ); >;

	// The object function (0 none, 1-4 A-D out) each value follows: value, gradient, flash
	// extension, brightness; and flash brightness.
	float4 g_vMeterSources < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float g_flMeterFlashSource < Default( 0.0 ); >;

	// What the lit part and the background are scaled by (before the brightness), and what's
	// behind them (linear).
	float3 g_vMeterSourceScale < Default3( 1.0, 1.0, 1.0 ); >;
	float2 g_vMeterDestScale < Default2( 1.0, 1.0 ); >;

	// Object functions a game sets (see the top of the file); below 0 isn't set.
	float g_flHaloFunctionA < Attribute( "HaloFunctionA" ); Default( -1.0 ); >;
	float g_flHaloFunctionB < Attribute( "HaloFunctionB" ); Default( -1.0 ); >;
	float g_flHaloFunctionC < Attribute( "HaloFunctionC" ); Default( -1.0 ); >;
	float g_flHaloFunctionD < Attribute( "HaloFunctionD" ); Default( -1.0 ); >;

	// The object's active camouflage (0-1): a camouflaged weapon's meter fades out with it.
	float g_flHaloCamo < Attribute( "HaloCamo" ); Default( 0.0 ); >;

	// The value a game set for an object function (1-4 A-D out), else the material's.
	float HaloFunctionValue( float source, float rest )
	{
		float value = -1;
		if ( source > 3.5 ) value = g_flHaloFunctionD;
		else if ( source > 2.5 ) value = g_flHaloFunctionC;
		else if ( source > 1.5 ) value = g_flHaloFunctionB;
		else if ( source > 0.5 ) value = g_flHaloFunctionA;
		return value >= 0 ? saturate( value ) : rest;
	}

	// sRGB conversions, written out to work on values above 1 from the HDR frame buffer.
	float3 ToLinear( float3 c )
	{
		c = max( c, 0 );
		return lerp( pow( ( c + 0.055 ) / 1.055, 2.4 ), c / 12.92, step( c, 0.04045 ) );
	}

	float4 MainPs( PixelInput i ) : SV_Target0
	{
		float2 uv = i.vTextureCoords.xy;
		float4 map = g_flMeterUnfiltered > 0.5 ? Tex2DS( g_tMeterMap, g_sMeterPoint, uv ) : Tex2DS( g_tMeterMap, g_sMeterLinear, uv );
		clip( map.a - 0.5 / 255.0 );

		float position = map.b;
		float value = HaloFunctionValue( g_vMeterSources.x, g_vMeterValues.x );
		float gradient = HaloFunctionValue( g_vMeterSources.y, g_vMeterValues.y );
		float flashExtension = HaloFunctionValue( g_vMeterSources.z, g_vMeterValues.z );
		float brightness = HaloFunctionValue( g_vMeterSources.w, g_vMeterBrightness.x );

		float flashBrightness = HaloFunctionValue( g_flMeterFlashSource, g_vMeterBrightness.y );
		float3 flashColor = sign( g_vMeterFlash ) * min( abs( g_vMeterFlash ) * flashBrightness, 1.0 );

		// Gradient from min to max, reaching max at the gradient value (at least 1/8).
		float t = saturate( position * 8.0 / max( gradient * 8.0, 1.0 ) );
		float3 lit = lerp( g_vMeterGradientMin, g_vMeterGradientMax, t );

		// Flash ramping up over the eighth before the flash extension, full past it (and cut off at the value below).
		float flash = saturate( 1.0 - 2.0 * saturate( 4.0 * ( flashExtension - position ) ) );
		lit = saturate( lit + flash * flashColor );

		bool background = position >= value;
		float3 color = ( background ? g_vMeterBackground : lit ) * map.a;
		float destScale = background ? g_vMeterDestScale.y : g_vMeterDestScale.x;

		// Halo blended in gamma space: the color goes out linear, and what's behind is scaled by
		// the factor's linear equivalent (passed in). Fog fades the effect out.
		float3 positionWs = i.vPositionWithOffsetWs.xyz + g_vHighPrecisionLightingOffsetWs.xyz;
		// Halo faded added light by 1 - its fog density in gamma space; s&box's transmittance is 1 - density^2.2 (ScenarioSceneLoader.BuildFog), far too little.
		float fade = ( 1 - pow( saturate( 1 - DoAtmospherics( positionWs, i.vPositionSs.xy, float4( 0, 0, 0, 1 ), true ).a ), 1.0 / 2.2 ) );
		fade *= 1 - saturate( g_flHaloCamo );

		float3 rgb = ToLinear( color * g_vMeterSourceScale * brightness ) * fade;
		return float4( rgb, lerp( 1.0, destScale, fade ) );
	}
}
