//
// Halo 2's plasma shader templates: plasma_alpha (grav lifts and bridges, covenant lights, powerups),
// plasma_alpha_active_camo (the needler's and plasma pistol's cores) and plasma_mask_offset (jackal and
// enforcer shields, the energy blade, turret and door fields).
//
// The same combiner trick as Halo CE's plasma (halo1_plasma.shader): one channel of two scrolling noise maps
// compared, (0.502 - |a - b|) squared and boosted makes thin bright bands where the two fields cross, and
// repeated (2x - 1)^2 steps sharpen them into a core and a hot core (shader_templates_other.md sections 2.3 and
// 6). Everything is added to the frame (ONE / ONE) and faded by Halo's atmospheric fog (HaloFogAmount, as in
// halo2_opaque.shader). Halo adds in its gamma space frame buffer, which brightens a lit background far more than
// adding the same colour in linear space: the grav lift's streaks over its pale blue column all but vanished.
// So the sum is taken in gamma space against the frame buffer copy and the difference added (as
// halo1_contrail.shader does). The copy is taken before translucent objects draw, so over another translucent
// layer the increment falls between a linear add and Halo's.
//
// Parameters arrive like halo2_transparent.shader's (textures by stage, constants by register, texture
// coordinate set n for stage n, the fresnel opacities in g_vV10.w / g_vV11.w, time curves for the scrolling and
// colour pulses); see Halo2/TemplatePassParameters.cs. Not yet: the shields and overshield on units (3D noise,
// shell offset, object functions).
//

HEADER
{
	Description = "Halo 2 plasma shader";
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

	SamplerState g_sWrap < Filter( Anisotropic ); AddressU( WRAP ); AddressV( WRAP ); >;
	SamplerState g_sClamp < Filter( Bilinear ); AddressU( CLAMP ); AddressV( CLAMP ); >;

	CreateInputTexture2D( Map0, Linear, 8, "None", "_color", ",0/,0/0", Default4( 0.00, 0.00, 0.00, 0.00 ) );
	CreateInputTexture2D( Map1, Linear, 8, "None", "_color", ",0/,0/0", Default4( 1.00, 1.00, 1.00, 1.00 ) );
	CreateInputTexture2D( Map2, Linear, 8, "None", "_color", ",0/,0/0", Default4( 1.00, 1.00, 1.00, 1.00 ) );
	CreateInputTexture2D( Map3, Linear, 8, "None", "_color", ",0/,0/0", Default4( 1.00, 1.00, 1.00, 1.00 ) );
	Texture2D g_tMap0 < Channel( RGBA, Box( Map0 ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( false ); >;
	Texture2D g_tMap1 < Channel( RGBA, Box( Map1 ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( false ); >;
	Texture2D g_tMap2 < Channel( RGBA, Box( Map2 ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( false ); >;
	Texture2D g_tMap3 < Channel( RGBA, Box( Map3 ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( false ); >;
	Texture2D g_tFrameBufferCopyTexture < Attribute( "FrameBufferCopyTexture" ); SrgbRead( false ); >;
	SamplerState g_sBehind < Filter( Point ); AddressU( CLAMP ); AddressV( CLAMP ); >;

	// Per stage: 1 where the pass clamps the texture instead of wrapping it (the *_clamped templates).
	float4 g_vMapClamp < Default4( 0.0, 0.0, 0.0, 0.0 ); >;

	// Texture coordinate sets 0-3: u' = dot( (u, v, 1), U.xyz ), v' = dot( (u, v, 1), V.xyz ).
	float4 g_vUv0U < Default4( 1.0, 0.0, 0.0, 0.0 ); >;
	float4 g_vUv0V < Default4( 0.0, 1.0, 0.0, 0.0 ); >;
	float4 g_vUv1U < Default4( 1.0, 0.0, 0.0, 0.0 ); >;
	float4 g_vUv1V < Default4( 0.0, 1.0, 0.0, 0.0 ); >;
	float4 g_vUv2U < Default4( 1.0, 0.0, 0.0, 0.0 ); >;
	float4 g_vUv2V < Default4( 0.0, 1.0, 0.0, 0.0 ); >;
	float4 g_vUv3U < Default4( 1.0, 0.0, 0.0, 0.0 ); >;
	float4 g_vUv3V < Default4( 0.0, 1.0, 0.0, 0.0 ); >;

	// Time curves added to each set's u and v offsets: A = (type + 16 shape, period in seconds, output min,
	// output max), B = the curve's constants; a period of 0 is no curve. See AnimatedFraction.
	float4 g_vAnim0UA < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float4 g_vAnim0UB < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float4 g_vAnim0VA < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float4 g_vAnim0VB < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float4 g_vAnim1UA < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float4 g_vAnim1UB < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float4 g_vAnim1VA < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float4 g_vAnim1VB < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float4 g_vAnim2UA < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float4 g_vAnim2UB < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float4 g_vAnim2VA < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float4 g_vAnim2VB < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float4 g_vAnim3UA < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float4 g_vAnim3UB < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float4 g_vAnim3VA < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float4 g_vAnim3VB < Default4( 0.0, 0.0, 0.0, 0.0 ); >;

	// Halo's pixel shader constant registers c1-c7 (colours as rgb, scalars in whichever component the
	// template reads).
	float4 g_vC1 < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float4 g_vC2 < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float4 g_vC3 < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float4 g_vC4 < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float4 g_vC5 < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float4 g_vC6 < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float4 g_vC7 < Default4( 0.0, 0.0, 0.0, 0.0 ); >;

	// Three constants that follow a time curve: the curve (as above, output 0-1), the values at its ends, and
	// the target (register, component 0-3 or 4 for rgb, 1 if used).
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

	// The fresnel pair: rgb tint and opacity head on (v10) and at grazing angles (v11).
	float4 g_vV10 < Default4( 1.0, 1.0, 1.0, 1.0 ); >;
	float4 g_vV11 < Default4( 1.0, 1.0, 1.0, 1.0 ); >;

	// Which program (PlasmaMaterial.Program), and whether it fades with the camouflage.
	float g_flProgram < Default( 0.0 ); >;
	float g_flCamoFade < Default( 0.0 ); >;

	// The object's active camouflage (0-1), as on its model.
	float g_flHaloCamo < Attribute( "HaloCamo" ); Default( 0.0 ); >;

	// s&box draws a translucent part of an overlay layer renderer twice onto the same frame, in the world's pass and over it.
	// A game sets HaloOverlay on such renderers (first person weapons), and each draw then adds half.
	float g_flHaloOverlay < Attribute( "HaloOverlay" ); Default( 0.0 ); >;

	//
	// Parameters that follow the object's functions while a game plays (Halo2/DrivenParameters.cs).
	// The game sets the functions on the renderer as HaloFunction0-7, 0-1, by the model's slots in <model>__functions.json; below 0 or unset, a parameter keeps its value at rest.
	// g_vDriven0-3: x the function's slot (-1 none), y the kind (3 a register, 4 a texture coordinate set's offsets in xy; the target is the register or set), z the target.
	// Row 0-3 of g_tDrivenTable holds the parameter's values over the function, 16 samples from 0 to 1.
	//

	float g_flHaloFunction0 < Attribute( "HaloFunction0" ); Default( -1.0 ); >;
	float g_flHaloFunction1 < Attribute( "HaloFunction1" ); Default( -1.0 ); >;
	float g_flHaloFunction2 < Attribute( "HaloFunction2" ); Default( -1.0 ); >;
	float g_flHaloFunction3 < Attribute( "HaloFunction3" ); Default( -1.0 ); >;
	float g_flHaloFunction4 < Attribute( "HaloFunction4" ); Default( -1.0 ); >;
	float g_flHaloFunction5 < Attribute( "HaloFunction5" ); Default( -1.0 ); >;
	float g_flHaloFunction6 < Attribute( "HaloFunction6" ); Default( -1.0 ); >;
	float g_flHaloFunction7 < Attribute( "HaloFunction7" ); Default( -1.0 ); >;
	float4 g_vDriven0 < Default4( -1.0, 0.0, 0.0, 0.0 ); >;
	float4 g_vDriven1 < Default4( -1.0, 0.0, 0.0, 0.0 ); >;
	float4 g_vDriven2 < Default4( -1.0, 0.0, 0.0, 0.0 ); >;
	float4 g_vDriven3 < Default4( -1.0, 0.0, 0.0, 0.0 ); >;
	float4 g_vDrivenFrame0 < Default4( 1.0, 0.0, 0.0, 0.0 ); >;
	float4 g_vDrivenFrame1 < Default4( 1.0, 0.0, 0.0, 0.0 ); >;
	float4 g_vDrivenFrame2 < Default4( 1.0, 0.0, 0.0, 0.0 ); >;
	float4 g_vDrivenFrame3 < Default4( 1.0, 0.0, 0.0, 0.0 ); >;
	CreateInputTexture2D( DrivenTable, Linear, 8, "None", "_color", ",0/,0/0", Default4( 0.00, 0.00, 0.00, 0.00 ) );
	Texture2D g_tDrivenTable < Channel( RGBA, Box( DrivenTable ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( false ); >;

	float ObjectFunction( float slot )
	{
		if ( slot < 0.5 ) return g_flHaloFunction0;
		if ( slot < 1.5 ) return g_flHaloFunction1;
		if ( slot < 2.5 ) return g_flHaloFunction2;
		if ( slot < 3.5 ) return g_flHaloFunction3;
		if ( slot < 4.5 ) return g_flHaloFunction4;
		if ( slot < 5.5 ) return g_flHaloFunction5;
		if ( slot < 6.5 ) return g_flHaloFunction6;
		return g_flHaloFunction7;
	}

	float4 DrivenEntry( float4 entry, int row, float kind, float target, float4 value )
	{
		if ( entry.x < -0.5 || abs( entry.y - kind ) > 0.5 || abs( entry.z - target ) > 0.5 )
			return value;

		float f = ObjectFunction( entry.x );
		if ( f < 0 )
			return value;

		float x = saturate( f ) * 15;
		int i = min( (int)x, 14 );
		return lerp( g_tDrivenTable.Load( int3( i, row, 0 ) ), g_tDrivenTable.Load( int3( i + 1, row, 0 ) ), x - i );
	}

	// A texture coordinate set whose stage's bitmap frame follows a function (kind 5): the stage's texture is the bitmap's images stacked top to bottom, and the image is picked as Halo does, floor( value mod count ).
	// g_vDrivenFrame: x the image count, y the image at rest; the table holds the curve's value before the floor.
	float2 FrameEntry( float4 entry, float4 frame, int row, int set, float2 uv )
	{
		if ( entry.x < -0.5 || abs( entry.y - 5 ) > 0.5 || abs( entry.z - set ) > 0.5 )
			return uv;

		float image = frame.y;
		float f = ObjectFunction( entry.x );
		if ( f >= 0 )
		{
			float x = saturate( f ) * 15;
			int i = min( (int)x, 14 );
			float v = lerp( g_tDrivenTable.Load( int3( i, row, 0 ) ).x, g_tDrivenTable.Load( int3( i + 1, row, 0 ) ).x, x - i );
			image = v < 0 ? 0 : floor( fmod( v + 0.001, frame.x ) );
		}

		return float2( uv.x, ( uv.y + image ) / frame.x );
	}

	float2 DrivenFrame( int set, float2 uv )
	{
		uv = FrameEntry( g_vDriven0, g_vDrivenFrame0, 0, set, uv );
		uv = FrameEntry( g_vDriven1, g_vDrivenFrame1, 1, set, uv );
		uv = FrameEntry( g_vDriven2, g_vDrivenFrame2, 2, set, uv );
		uv = FrameEntry( g_vDriven3, g_vDrivenFrame3, 3, set, uv );
		return uv;
	}

	// A parameter of a kind and target with the object's functions applied, if any drives it.
	float4 Driven( float kind, float target, float4 value )
	{
		value = DrivenEntry( g_vDriven0, 0, kind, target, value );
		value = DrivenEntry( g_vDriven1, 1, kind, target, value );
		value = DrivenEntry( g_vDriven2, 2, kind, target, value );
		value = DrivenEntry( g_vDriven3, 3, kind, target, value );
		return value;
	}

	//
	// Halo 2's function evaluator (shader_engine.md section 5.1) for time curves.
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

	// The curve's value 0-1 at the current time: input = time / period, wrapped unless periodic.
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

	// A texture coordinate set's rows with the offsets the object's functions drive (kind 4).
	float4 DrivenU( int set, float4 u, float4 v ) { u.z = Driven( 4, set, float4( u.z, v.z, 0, 0 ) ).x; return u; }
	float4 DrivenV( int set, float4 u, float4 v ) { v.z = Driven( 4, set, float4( u.z, v.z, 0, 0 ) ).y; return v; }

	float2 UvSet( float2 uv, float4 u, float4 v, float4 animUA, float4 animUB, float4 animVA, float4 animVB )
	{
		return float2( dot( float3( uv, 1 ), u.xyz ) + AnimatedValue( animUA, animUB ), dot( float3( uv, 1 ), v.xyz ) + AnimatedValue( animVA, animVB ) );
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

	float4 SampleMap( Texture2D map, float clampAddress, float2 uv )
	{
		return saturate( clampAddress > 0.5 ? map.Sample( g_sClamp, uv ) : map.Sample( g_sWrap, uv ) );
	}

	// Halo's plasma kernel on two noise values: the band (x1 = sat( 4p ), p = (0.502 - |a - b|)^2, the select
	// done the combiner's way), and the sharpening steps y = (step >= 0.5) * (2x - 1)^2.
	float PlasmaBand( float a, float b, bool saturateDifferences, out float p )
	{
		float d1 = b - a + 0.50196;
		float d2 = a - b + 0.50196;
		if ( saturateDifferences )
		{
			float s1 = saturate( d1 );
			p = round( d1 ) * ( saturate( d2 ) * saturate( d2 ) - s1 * s1 ) + s1 * s1;
		}
		else
		{
			p = round( d1 ) * ( d2 * d2 - d1 * d1 ) + d1 * d1;
		}
		return saturate( 4 * p );
	}

	float Sharpen( float step, float x )
	{
		return round( step ) * ( 2 * x - 1 ) * ( 2 * x - 1 );
	}

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

	// What the colour adds to the frame where Halo adds it in gamma space (see the top).
	float3 AddedInGammaSpace( float2 pixel, float3 added )
	{
		float2 uv = pixel * g_vInvViewportSize * g_vFrameBufferCopyInvSizeAndUvScale.zw;
		float3 behind = saturate( g_tFrameBufferCopyTexture.SampleLevel( g_sBehind, uv, 0 ).rgb );
		return max( GammaToLinear( saturate( LinearToGamma( behind ) + added ) ) - behind, 0 );
	}

	// Halo 2's atmospheric fog, as in halo2_opaque.shader (amount by view depth; none on the sky).
	float g_flHaloSky < Default( 0.0 ); >;

	float HaloFogAmount( float3 positionWs )
	{
		if ( !g_bFogEnabled || !g_bGradientFogEnabled || g_flHaloSky > 0.5 )
			return 0;

		float depth = dot( positionWs - g_vCameraPositionWs, g_vCameraDirWs );
		float u = saturate( g_vGradientFogBiasAndScale.x + g_vGradientFogBiasAndScale.z * depth );
		return smoothstep( 0, 1, saturate( ( 16 * u - 0.5 ) / 15 ) ) * saturate( g_vGradientFogColor_Opacity.w );
	}

	float4 MainPs( PixelInput i ) : SV_Target0
	{
		float4 c[8];
		c[0] = 0;
		c[1] = g_vC1; c[2] = g_vC2; c[3] = g_vC3; c[4] = g_vC4; c[5] = g_vC5; c[6] = g_vC6; c[7] = g_vC7;
		ApplyAnimatedConstant( c, g_vAnimConst0A, g_vAnimConst0B, g_vAnimConst0Min, g_vAnimConst0Max, g_vAnimConst0Target );
		ApplyAnimatedConstant( c, g_vAnimConst1A, g_vAnimConst1B, g_vAnimConst1Min, g_vAnimConst1Max, g_vAnimConst1Target );
		ApplyAnimatedConstant( c, g_vAnimConst2A, g_vAnimConst2B, g_vAnimConst2Min, g_vAnimConst2Max, g_vAnimConst2Target );
		[unroll] for ( int driven = 1; driven < 8; driven++ )
			c[driven] = Driven( 3, driven, c[driven] );

		float2 uv = i.vTextureCoords.xy;
		float2 uv0 = DrivenFrame( 0, UvSet( uv, DrivenU( 0, g_vUv0U, g_vUv0V ), DrivenV( 0, g_vUv0U, g_vUv0V ), g_vAnim0UA, g_vAnim0UB, g_vAnim0VA, g_vAnim0VB ) );
		float2 uv1 = DrivenFrame( 1, UvSet( uv, DrivenU( 1, g_vUv1U, g_vUv1V ), DrivenV( 1, g_vUv1U, g_vUv1V ), g_vAnim1UA, g_vAnim1UB, g_vAnim1VA, g_vAnim1VB ) );
		float2 uv2 = DrivenFrame( 2, UvSet( uv, DrivenU( 2, g_vUv2U, g_vUv2V ), DrivenV( 2, g_vUv2U, g_vUv2V ), g_vAnim2UA, g_vAnim2UB, g_vAnim2VA, g_vAnim2VB ) );
		float2 uv3 = DrivenFrame( 3, UvSet( uv, DrivenU( 3, g_vUv3U, g_vUv3V ), DrivenV( 3, g_vUv3U, g_vUv3V ), g_vAnim3UA, g_vAnim3UB, g_vAnim3VA, g_vAnim3VB ) );

		float3 normal = normalize( i.vNormalWs );
		float3 toCamera = normalize( -i.vPositionWithOffsetWs.xyz );
		float facing = max( dot( toCamera, normal ), 0 );
		float4 fr = saturate( facing * g_vV10 + ( 1 - facing ) * g_vV11 );

		float3 o = 0;
		int program = (int)( g_flProgram + 0.5 );

		if ( program == 0 )
		{
			// plasma_alpha: the blue channels of two noise maps (t1, t2); colour from the outer (c3) to the core
			// colour (c2) across the band, plus the hot core colour (c5); the fresnel opacity, its falloff
			// reshaped by c4.w, times the shape mask's alpha (t3).
			float a = SampleMap( g_tMap1, g_vMapClamp.y, uv1 ).b;
			float b = SampleMap( g_tMap2, g_vMapClamp.z, uv2 ).b;
			float p;
			float x1 = PlasmaBand( a, b, false, p );
			float y1 = Sharpen( 4 * p, x1 );
			float x2 = saturate( y1 );
			float y2 = Sharpen( y1, x2 );
			float x3 = saturate( y2 );
			float y3 = saturate( Sharpen( y2, x3 ) );

			float3 col = saturate( saturate( lerp( c[3].rgb, c[2].rgb, x2 ) ) * x1 + y3 * c[5].rgb );
			float f = fr.a;
			float edge = 1 - pow( 1 - f, 4 );
			float g = saturate( f * ( 1 - c[4].w ) + edge * c[4].w );
			o = col * g * SampleMap( g_tMap3, g_vMapClamp.w, uv3 ).a;
		}
		else if ( program == 1 )
		{
			// plasma_mask_offset: noise A (t0) mixed with an offset map (t2) by c1.w, noise B (t1) around grey by
			// c1.z, both flattened to grey where the mask's alpha (t3) is; the core lights in the mask's colour
			// times c3, the mask's transparent parts take c4; brightness c5.w.
			float4 m = SampleMap( g_tMap3, g_vMapClamp.w, uv3 );
			float k = c[1].w;
			float k2 = c[1].z;
			float a = min( 1, lerp( saturate( SampleMap( g_tMap2, g_vMapClamp.z, uv2 ).a * ( 1 - k ) + SampleMap( g_tMap0, g_vMapClamp.x, uv0 ).a * k ), 0.5, m.a ) );
			float b = min( 1, lerp( saturate( ( 1 - k2 ) * 0.5 + SampleMap( g_tMap1, g_vMapClamp.y, uv1 ).a * k2 ), 0.5, m.a ) );
			float p;
			float x1 = PlasmaBand( a, b, true, p );
			float y = saturate( Sharpen( 4 * p, x1 ) );
			y = min( 1, y * ( 2 - y ) );
			o = min( 1, y * saturate( m.rgb * c[3].rgb ) + saturate( ( 1 - m.a ) * c[4].rgb ) ) * c[5].w;
		}

		// The needler's and plasma pistol's cores fade out with their holder's camouflage.
		if ( g_flCamoFade > 0.5 )
			o *= saturate( 1 - g_flHaloCamo );

		// Faded by fog, added to what's behind in Halo's gamma space.
		float3 positionWs = i.vPositionWithOffsetWs.xyz + g_vHighPrecisionLightingOffsetWs.xyz;
		float3 added = AddedInGammaSpace( i.vPositionSs.xy, saturate( o ) * ( 1 - HaloFogAmount( positionWs ) ) );
		return float4( g_flHaloOverlay > 0.5 ? 0.5 * added : added, 0 );
	}
}
