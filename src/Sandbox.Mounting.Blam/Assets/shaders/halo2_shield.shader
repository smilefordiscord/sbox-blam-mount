//
// Halo 2's shield shells: the shader a unit's model draws over itself while its shields take damage (the model
// tag's damage info: "shield damaged shader", transparent\plasma_shield or plasma_shield_change_color), and its
// overshield (transparent\overshield). shader_templates_other.md section 7.
//
// MCC's vertex shader (transparent_generic_offset) pushes the surface out along its normal by c26.w world units and
// reads two 3D noise maps at the bind pose position (world units) through two 3D affine transforms (scale, then a
// translation in noise lengths). Its pixel shader runs the plasma kernel on the alpha of both maps (the band x1 and
// one sharpening step y), colours them by c2 (band), c3 (core) and c4 (hot, y squared) and multiplies by the
// fresnel colour (v10 head on, v11 at grazing angles, by how much the unshifted surface faces the camera), adding
// the result to the frame (ONE / ONE, back faces culled). plasma_shield_change_color takes the object's primary
// change colour for c2-c4 (untinted hot term); overshield fades out as the object's camouflage passes c6.w.
//
// Almost every value follows the object's functions: elite shields' colours, offset and noise translation are
// transitions of shield_glow_source, the Master Chief's fresnel colours and offset of shield_hit (his colours
// pulse with time), the overshield's fresnel colour a spline of object_overshield_amount. The material holds each
// value as a curve over its function (g_tShieldCurves, one row per value, evaluated from the shader's animation
// at 0-1 by ShieldMaterial.cs), and the game sets the functions on the renderer: HaloShieldHit,
// HaloShieldGlowSource and HaloOvershieldAmount (0-1). On MCC's bipeds shield_hit and shield_glow_source are both
// the unit's current shield damage (recently taken, decaying). At 0 the shields draw nothing (black colours).
// HaloCamo (0-1) fades the overshield, HaloChangeColor0 (rgb, alpha 1 when set, gamma space) is the primary
// change colour plasma_shield_change_color tints with (white when unset).
//
// The noise volumes arrive as 2D atlases of their slices (3D textures may not bind to materials made at runtime,
// as in halo1_plasma.shader), sampled with wrapping and trilinear filtering by hand. Added in Halo's gamma space
// against the frame buffer copy, as halo2_plasma.shader does.
//

HEADER
{
	Description = "Halo 2 shield shader";
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

	// The object's functions the shield shaders read (0-1).
	float g_flHaloShieldHit < Attribute( "HaloShieldHit" ); Default( 0.0 ); >;
	float g_flHaloShieldGlowSource < Attribute( "HaloShieldGlowSource" ); Default( 0.0 ); >;
	float g_flHaloOvershieldAmount < Attribute( "HaloOvershieldAmount" ); Default( 0.0 ); >;

	// Each value's curve over its function: row r, column = function value * (width - 1) (ShieldMaterial.Row).
	CreateInputTexture2D( ShieldCurves, Linear, 8, "None", "_color", ",0/,0/0", Default4( 0.00, 0.00, 0.00, 0.00 ) );
	Texture2D g_tShieldCurves < Channel( RGBA, Box( ShieldCurves ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( false ); >;
	float g_flShieldCurveWidth < Default( 33.0 ); >;

	// Which function each row follows: 0 shield_hit, 1 shield_glow_source, 2 object_overshield_amount, -1 none.
	float4 g_vShieldDrivers0 < Default4( -1.0, -1.0, -1.0, -1.0 ); >;
	float4 g_vShieldDrivers1 < Default4( -1.0, -1.0, -1.0, -1.0 ); >;
	float4 g_vShieldDrivers2 < Default4( -1.0, -1.0, -1.0, -1.0 ); >;

	// s&box units per world unit.
	float g_flShieldWorldScale < Default( 103.2258 ); >;

	float ShieldDriver( int row )
	{
		float4 d = row < 4 ? g_vShieldDrivers0 : row < 8 ? g_vShieldDrivers1 : g_vShieldDrivers2;
		int k = row % 4;
		return k == 0 ? d.x : k == 1 ? d.y : k == 2 ? d.z : d.w;
	}

	// A row's value for the object's current function value (linear between columns).
	float4 ShieldCurve( int row )
	{
		float driver = ShieldDriver( row );
		float x = driver < -0.5 ? 0 : driver < 0.5 ? g_flHaloShieldHit : driver < 1.5 ? g_flHaloShieldGlowSource : g_flHaloOvershieldAmount;
		float u = saturate( x ) * ( g_flShieldCurveWidth - 1 );
		int a = (int)floor( u );
		int b = min( a + 1, (int)g_flShieldCurveWidth - 1 );
		return lerp( g_tShieldCurves.Load( int3( a, row, 0 ) ), g_tShieldCurves.Load( int3( b, row, 0 ) ), u - a );
	}

	// Rows (ShieldMaterial.Row).
	#define ROW_C2 0
	#define ROW_C3 1
	#define ROW_C4 2
	#define ROW_C6 3
	#define ROW_V10 4
	#define ROW_V11 5
	#define ROW_OFFSET 6
	#define ROW_NOISE0_SCALE 7
	#define ROW_NOISE0_TRANSLATE 8
	#define ROW_NOISE1_SCALE 9
	#define ROW_NOISE1_TRANSLATE 10
}

struct VertexInput
{
	#include "common/vertexinput.hlsl"
};

struct PixelInput
{
	#include "common/pixelinput.hlsl"

	// The bind pose position in world units, and w = how much the unshifted surface faces the camera (0-1).
	float4 vShieldPositionOs : TEXCOORD14;
};

VS
{
	#include "common/vertex.hlsl"

	PixelInput MainVs( VertexInput v )
	{
		PixelInput i = ProcessVertex( v );

		// Facing from where the surface is before it's pushed out (MCC: max( N.E, 0 ), E to the eye unnormalised
		// then normalised).
		float3 normalWs = normalize( i.vNormalWs );
		float facing = max( dot( normalize( g_vCameraPositionWs.xyz - i.vPositionWs.xyz ), normalWs ), 0 );
		i.vShieldPositionOs = float4( v.vPositionOs.xyz / g_flShieldWorldScale, facing );

		float offset = ShieldCurve( ROW_OFFSET ).x;
		if ( abs( offset ) > 0 )
		{
			i.vPositionWs.xyz += normalWs * offset * g_flShieldWorldScale;
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
	RenderState( DepthWriteEnable, false );
	RenderState( BlendEnable, true );
	RenderState( SrcBlend, ONE );
	RenderState( DstBlend, ONE );
	RenderState( BlendOp, ADD );
	RenderState( SrcBlendAlpha, ZERO );
	RenderState( DstBlendAlpha, ONE );
	BoolAttribute( bWantsFBCopyTexture, true );

	CreateInputTexture2D( Noise0, Linear, 8, "None", "_color", ",0/,0/0", Default4( 0.50, 0.50, 0.50, 0.50 ) );
	CreateInputTexture2D( Noise1, Linear, 8, "None", "_color", ",0/,0/0", Default4( 0.50, 0.50, 0.50, 0.50 ) );
	Texture2D g_tNoise0 < Channel( RGBA, Box( Noise0 ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( false ); >;
	Texture2D g_tNoise1 < Channel( RGBA, Box( Noise1 ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( false ); >;
	Texture2D g_tFrameBufferCopyTexture < Attribute( "FrameBufferCopyTexture" ); SrgbRead( false ); >;
	SamplerState g_sBehind < Filter( Point ); AddressU( CLAMP ); AddressV( CLAMP ); >;

	// Each noise volume's size (width, height, depth, slices per atlas row).
	float4 g_vNoise0Size < Default4( 1.0, 1.0, 1.0, 1.0 ); >;
	float4 g_vNoise1Size < Default4( 1.0, 1.0, 1.0, 1.0 ); >;

	// Time curves added to each noise translation axis (A = (type + 16 shape, period, output min, max), B = the
	// curve's constants, as halo2_plasma.shader's; a period of 0 is no curve).
	float4 g_vNoise0XA < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float4 g_vNoise0XB < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float4 g_vNoise0YA < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float4 g_vNoise0YB < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float4 g_vNoise0ZA < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float4 g_vNoise0ZB < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float4 g_vNoise1XA < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float4 g_vNoise1XB < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float4 g_vNoise1YA < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float4 g_vNoise1YB < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float4 g_vNoise1ZA < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float4 g_vNoise1ZB < Default4( 0.0, 0.0, 0.0, 0.0 ); >;

	// Up to three pixel constants following a time curve (the Master Chief's pulsing colours, the overshield's):
	// the curve (output 0-1), the values at its ends, the target (register, component 0-3 or 4 for rgb, 1 if used).
	float4 g_vAnimConst0A < Default4( 0.0, 0.0, 0.0, 1.0 ); >;
	float4 g_vAnimConst0B < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float4 g_vAnimConst0Min < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float4 g_vAnimConst0Max < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float3 g_vAnimConst0Target < Default3( 0.0, 0.0, 0.0 ); >;
	float4 g_vAnimConst1A < Default4( 0.0, 0.0, 0.0, 1.0 ); >;
	float4 g_vAnimConst1B < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float4 g_vAnimConst1Min < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float4 g_vAnimConst1Max < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float3 g_vAnimConst1Target < Default3( 0.0, 0.0, 0.0 ); >;
	float4 g_vAnimConst2A < Default4( 0.0, 0.0, 0.0, 1.0 ); >;
	float4 g_vAnimConst2B < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float4 g_vAnimConst2Min < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float4 g_vAnimConst2Max < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float3 g_vAnimConst2Target < Default3( 0.0, 0.0, 0.0 ); >;

	// 0 plasma_shield, 1 plasma_shield_change_color, 2 overshield (ShieldMaterial.Program).
	float g_flProgram < Default( 0.0 ); >;

	float g_flHaloCamo < Attribute( "HaloCamo" ); Default( 0.0 ); >;

	// s&box draws a translucent part of an overlay layer renderer twice onto the same frame, in the world's pass and over it.
	// A game sets HaloOverlay on such renderers (first person shield shells), and each draw then adds half.
	float g_flHaloOverlay < Attribute( "HaloOverlay" ); Default( 0.0 ); >;

	float4 g_vHaloChangeColor0 < Attribute( "HaloChangeColor0" ); Default4( 0.0, 0.0, 0.0, 0.0 ); >;

	//
	// Halo 2's function evaluator for time curves (as in halo2_plasma.shader).
	//

	float TransitionShape( float shape, float x )
	{
		if ( shape < 0.5 ) return x;
		if ( shape < 1.5 ) return sqrt( x );
		if ( shape < 2.5 ) return sqrt( sqrt( x ) );
		if ( shape < 3.5 ) return x * x;
		if ( shape < 4.5 ) return x * x * x * x;
		if ( shape < 5.5 ) return 0.5 - 0.5 * cos( 3.14159265 * x );
		return shape < 6.5 ? 1 : 0;
	}

	float PeriodicShape( float shape, float x )
	{
		float t = frac( x );
		if ( shape < 0.5 ) return 1;
		if ( shape < 1.5 ) return 0;
		if ( shape < 3.5 ) return 0.5 + 0.5 * cos( 6.2831853 * t );
		if ( shape < 5.5 ) return 1 - abs( 2 * t - 1 );
		if ( shape < 7.5 ) return t;
		return 0.5 + 0.25 * sin( 6.2831853 * t ) + 0.25 * sin( 6.2831853 * 2.7 * t );
	}

	float AnimatedFraction( float4 a, float4 b )
	{
		float type = fmod( a.x, 16 );
		float shape = floor( a.x / 16 );
		float x = g_flTime / a.y;
		float f;
		if ( type < 0.5 ) f = frac( x );
		else if ( type < 2.5 ) f = lerp( b.x, b.y, TransitionShape( shape, frac( x ) ) );
		else if ( type < 3.5 ) f = lerp( b.z, b.w, PeriodicShape( shape, x * b.x + b.y ) );
		else if ( type < 4.5 ) f = frac( x ) * b.x + b.y;
		else f = lerp( b.x, b.y, pow( frac( x ), b.z ) );
		return saturate( f );
	}

	float AnimatedValue( float4 a, float4 b )
	{
		return a.y > 0 ? lerp( a.z, a.w, AnimatedFraction( a, b ) ) : 0;
	}

	void ApplyAnimatedConstant( inout float4 c[8], float4 a, float4 b, float4 lo, float4 hi, float3 target )
	{
		if ( target.z < 0.5 || a.y <= 0 )
			return;

		float4 value = lerp( lo, hi, AnimatedFraction( a, b ) );
		int r = clamp( (int)target.x, 1, 7 );
		int component = (int)target.y;
		if ( component >= 4 ) c[r].xyz = value.xyz;
		else if ( component == 0 ) c[r].x = value.x;
		else if ( component == 1 ) c[r].y = value.x;
		else if ( component == 2 ) c[r].z = value.x;
		else c[r].w = value.x;
	}

	// One texel of a noise volume from its slice atlas, wrapping in all three directions.
	float4 NoiseTexel( Texture2D atlas, float4 size, int3 p )
	{
		int3 s = int3( size.xyz );
		p = ( p % s + s ) % s;
		int tilesX = (int)size.w;
		int2 texel = int2( ( p.z % tilesX ) * s.x + p.x, ( p.z / tilesX ) * s.y + p.y );
		return atlas.Load( int3( texel, 0 ) );
	}

	// Trilinear, wrapping, at uvw in volume lengths.
	float4 SampleNoise( Texture2D atlas, float4 size, float3 uvw )
	{
		float3 p = uvw * size.xyz - 0.5;
		float3 base = floor( p );
		float3 f = p - base;
		int3 b = int3( base );

		float4 c00 = lerp( NoiseTexel( atlas, size, b ), NoiseTexel( atlas, size, b + int3( 1, 0, 0 ) ), f.x );
		float4 c10 = lerp( NoiseTexel( atlas, size, b + int3( 0, 1, 0 ) ), NoiseTexel( atlas, size, b + int3( 1, 1, 0 ) ), f.x );
		float4 c01 = lerp( NoiseTexel( atlas, size, b + int3( 0, 0, 1 ) ), NoiseTexel( atlas, size, b + int3( 1, 0, 1 ) ), f.x );
		float4 c11 = lerp( NoiseTexel( atlas, size, b + int3( 0, 1, 1 ) ), NoiseTexel( atlas, size, b + int3( 1, 1, 1 ) ), f.x );
		return lerp( lerp( c00, c10, f.y ), lerp( c01, c11, f.y ), f.z );
	}

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

	// The frame behind the shell (Halo2/CamoFrame.cs grabs it after the sky, the water and the weather), where it's set:
	// s&box's copy for translucent readers is masked by the reader's depth, and the shell writes none, so it reads
	// black, and each vein added to black came out thin and faint at its edges (MCC's lit about 1.5x the area).
	Texture2D g_tHaloCamoFrame < Attribute( "HaloCamoFrame" ); SrgbRead( false ); >;
	bool g_bHaloHasCamoFrame < Attribute( "HaloHasCamoFrame" ); Default( 0 ); >;

	// What the colour adds to the frame where Halo adds it in gamma space.
	float3 AddedInGammaSpace( float2 pixel, float3 added )
	{
		float2 screen = pixel * g_vInvViewportSize;
		float3 behind = g_bHaloHasCamoFrame
			? saturate( g_tHaloCamoFrame.SampleLevel( g_sBehind, screen, 0 ).rgb )
			: saturate( g_tFrameBufferCopyTexture.SampleLevel( g_sBehind, screen * g_vFrameBufferCopyInvSizeAndUvScale.zw, 0 ).rgb );
		return max( GammaToLinear( saturate( LinearToGamma( behind ) + added ) ) - behind, 0 );
	}

	float4 MainPs( PixelInput i ) : SV_Target0
	{
		float4 c[8];
		c[0] = 0; c[1] = 0; c[5] = 0; c[7] = 0;
		c[2] = ShieldCurve( ROW_C2 );
		c[3] = ShieldCurve( ROW_C3 );
		c[4] = ShieldCurve( ROW_C4 );
		c[6] = ShieldCurve( ROW_C6 );
		ApplyAnimatedConstant( c, g_vAnimConst0A, g_vAnimConst0B, g_vAnimConst0Min, g_vAnimConst0Max, g_vAnimConst0Target );
		ApplyAnimatedConstant( c, g_vAnimConst1A, g_vAnimConst1B, g_vAnimConst1Min, g_vAnimConst1Max, g_vAnimConst1Target );
		ApplyAnimatedConstant( c, g_vAnimConst2A, g_vAnimConst2B, g_vAnimConst2Min, g_vAnimConst2Max, g_vAnimConst2Target );

		// The fresnel colour: v10 head on to v11 at grazing angles.
		float facing = i.vShieldPositionOs.w;
		float3 fr = saturate( facing * ShieldCurve( ROW_V10 ).rgb + ( 1 - facing ) * ShieldCurve( ROW_V11 ).rgb );
		if ( !any( fr > 0 ) )
			discard;

		// Both noise maps' alpha at the bind pose position through their transforms.
		float3 p = i.vShieldPositionOs.xyz;
		float3 t0 = ShieldCurve( ROW_NOISE0_TRANSLATE ).xyz + float3( AnimatedValue( g_vNoise0XA, g_vNoise0XB ), AnimatedValue( g_vNoise0YA, g_vNoise0YB ), AnimatedValue( g_vNoise0ZA, g_vNoise0ZB ) );
		float3 t1 = ShieldCurve( ROW_NOISE1_TRANSLATE ).xyz + float3( AnimatedValue( g_vNoise1XA, g_vNoise1XB ), AnimatedValue( g_vNoise1YA, g_vNoise1YB ), AnimatedValue( g_vNoise1ZA, g_vNoise1ZB ) );
		float a = saturate( SampleNoise( g_tNoise0, g_vNoise0Size, p * ShieldCurve( ROW_NOISE0_SCALE ).xyz + t0 ).a );
		float b = saturate( SampleNoise( g_tNoise1, g_vNoise1Size, p * ShieldCurve( ROW_NOISE1_SCALE ).xyz + t1 ).a );

		// The plasma kernel with saturated differences: the band x1 = sat( 4p ) and one sharpening step.
		float d1 = a - b + 0.50196;
		float s2 = saturate( b - a + 0.50196 );
		float s1 = saturate( d1 );
		float q = round( d1 ) * ( s2 * s2 - s1 * s1 ) + s1 * s1;
		float x1 = saturate( 4 * q );
		float y = saturate( round( 4 * q ) * ( 2 * x1 - 1 ) * ( 2 * x1 - 1 ) );

		int program = (int)( g_flProgram + 0.5 );
		float3 o;
		if ( program == 1 )
		{
			// The object's primary change colour for every colour; the hot term untinted.
			float3 cc = g_vHaloChangeColor0.a > 0.5 ? g_vHaloChangeColor0.rgb : float3( 1, 1, 1 );
			o = min( 1, saturate( x1 * cc ) * fr + saturate( y * cc ) * fr ) + y * y * fr;
		}
		else
		{
			o = min( 1, saturate( x1 * c[2].rgb ) * fr + saturate( y * c[3].rgb ) * fr ) + saturate( y * y * c[4].rgb ) * fr;
			if ( program == 2 )
				o = min( 1, o ) * saturate( 1 + c[6].w - g_flHaloCamo );
		}

		float3 added = AddedInGammaSpace( i.vPositionSs.xy, saturate( o ) );
		return float4( g_flHaloOverlay > 0.5 ? 0.5 * added : added, 0 );
	}
}
