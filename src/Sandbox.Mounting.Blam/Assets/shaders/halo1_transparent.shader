//
// Halo CE transparent effect shaders: shader_transparent_chicago, _chicago_extended and _generic.
// Lights, glows, energy fields, waterfalls and surf.
//
// Up to four maps are sampled. Generic shaders combine them with register combiner stages, like
// the original Xbox GPU: each stage reads registers, remaps them, multiplies in pairs and sums,
// then writes registers back. The stages arrive packed into a small float texture, one row each.
// Chicago shaders instead combine maps in order, each map's color/alpha function saying how the
// result so far ("current") combines with the next map. The result is blended with what's behind it by the
// GPU, using Halo's framebuffer blend function (picked per material with F_BLEND_MODE), so
// overlapping transparent surfaces like the layers of a sky stack up properly.
//
// With F_FLAG_CLOTH the vertex shader also poses the CTF flag's cloth in the air moving past it
// (see FlagCloth and the HaloFlagWind / HaloFlagGusts renderer attributes below).
//
// On objects, Halo drove some inputs with the object's functions (A-D out). A game sets those on
// the renderer as HaloFunctionA-D (float, 0-1); where one isn't set (or is below 0), the input
// keeps what the material baked in. They drive: a generic stage's constant color 0 between its
// bounds by A ("A out controls color 0": the powerup orbs, the needler's core), a map's U/V offset
// (its animation's value times the function: the needler's needles, the plasma cannon's and
// flamethrower's displays, the Warthog's speed readout), and the fade (the result times the
// function: the Warthog's chain gun blur; and 1 - HaloCamo, the object's active camouflage), and on numeric shaders the number shown (the assault
// rifle's ammo counter by A, its compass by D; see g_vNumeric).
//

HEADER
{
	Description = "Halo CE transparent effect shader";
}

FEATURES
{
	#include "common/features.hlsl"

	// Halo's framebuffer blend function: alpha blend, multiply, double multiply, add, subtract,
	// component min, component max, alpha-multiply add.
	Feature( F_BLEND_MODE, 0..7( 0="Alpha Blend", 1="Multiply", 2="Double Multiply", 3="Add", 4="Subtract", 5="Component Min", 6="Component Max", 7="Alpha-Multiply Add" ), "Blending" );

	// The CTF flag's cloth: the vertex shader waves it in the air moving past it (see FlagCloth).
	Feature( F_FLAG_CLOTH, 0..1, "Flag cloth" );
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

	StaticCombo( S_FLAG_CLOTH, F_FLAG_CLOTH, Sys( ALL ) );

	#if S_FLAG_CLOTH
		// Per-object attributes a game sets on the flag's renderer (renderer.Attributes):
		//   HaloFlagWind (Vector4): xyz = the air moving past the cloth, in the world, s&box units a
		//     second (the level's wind minus the flag's own velocity), w = 1 to use it. Without it
		//     the cloth streams in a light breeze along its rest direction.
		//   HaloFlagGusts (float): how strongly the air gusts, s&box units a second (Blood Gulch's
		//     wind is about 13). Below 0 (unset): the material's.
		float4 g_vHaloFlagWind < Attribute( "HaloFlagWind" ); Default4( 0.0, 0.0, 0.0, 0.0 ); >;
		float g_flHaloFlagGusts < Attribute( "HaloFlagGusts" ); Default( -1.0 ); >;

		// World units a second: how fast a cloth vertex falls through still air (from the flag's
		// point physics), the breeze without a game's air, and the gusts without a game's.
		float g_flFlagSag < Default( 0.3 ); >;
		float g_flFlagRestWind < Default( 0.8 ); >;
		float g_flFlagGusts < Default( 0.13 ); >;
		float g_flFlagWorldScale < Default( 103.2 ); >;

		// Poses a cloth vertex. Halo's cloth (simulated offline by BlamInspect flagsim) streams
		// straight along the air, below it by the sag speed (two thirds of it in light air, nearly
		// all of it at speed, as the cells pull against each other), stretches with the air's
		// speed (8% at 0.5 world units a second, 25% at 2, 50% at 10), and gusts swing the whole
		// sheet about once a second (rms angle about 0.6 x gusts / air speed). The ripple running
		// down it is extra, for a cloth that reads as cloth up close.
		PixelInput WaveFlag( PixelInput i, uint transformId, float3 restOs, float3 normalOs, float distance, float2 uv )
		{
			float3x4 objectToWorld = GetTransformMatrix( transformId );
			float3 origin = float3( objectToWorld[0][3], objectToWorld[1][3], objectToWorld[2][3] );
			float3 rest = mul( (float3x3)objectToWorld, restOs );
			float scale = max( length( rest ), 0.0001 );
			rest /= scale;
			float3 normal = normalize( mul( (float3x3)objectToWorld, normalOs ) );
			float3 down = normalize( cross( normal, rest ) );
			float3 edge = i.vPositionWs.xyz - rest * distance * scale;

			// The air (world units a second), and a random phase per flag from where it is.
			float3 air = g_vHaloFlagWind.w > 0.5 ? g_vHaloFlagWind.xyz / g_flFlagWorldScale : rest * g_flFlagRestWind;
			float gusts = g_flHaloFlagGusts >= 0.0 ? g_flHaloFlagGusts / g_flFlagWorldScale : g_flFlagGusts;
			float speed = length( air );
			float phase = frac( sin( dot( floor( origin ), float3( 12.9898, 78.233, 37.719 ) ) ) * 43758.5453 ) * 6.2831853;

			// Gusts: a smooth wandering push, mostly across the air.
			float t = g_flTime * 6.2831853 * ( 0.45 + 0.25 * min( speed, 2.0 ) );
			float3 gust = float3(
				0.6 * sin( t + phase ) + 0.8 * sin( 1.73 * t + 2.1 * phase ),
				0.6 * sin( 1.37 * t + 3.0 * phase ) + 0.8 * sin( 2.11 * t + 1.3 * phase ),
				0.3 * sin( 0.83 * t + 4.0 * phase ) ) * ( 0.85 * gusts );

			float sag = g_flFlagSag * lerp( 0.66, 0.95, 1.0 - exp( -speed / 5.0 ) );
			float3 along = air + gust - float3( 0, 0, sag ) + rest * 0.02;
			along = dot( along, along ) > 0.000001 ? normalize( along ) : rest;
			float stretch = 1.0 + 0.52 * ( 1.0 - exp( -speed / 3.0 ) );

			// The ripple: a wave running from the attached edge to the trailing edge, growing toward it.
			float3 side = cross( along, down );
			side = dot( side, side ) > 0.0001 ? normalize( side ) : normal;
			float distanceWu = distance / g_flFlagWorldScale;
			float wave = 6.2831853 * ( distanceWu / 0.3 - g_flTime * ( 1.0 + 0.6 * min( speed, 4.0 ) ) ) + 2.0 * uv.y + phase;
			float amplitude = uv.x * ( 0.01 + 0.03 * gusts );
			float3 position = edge + ( along * distance * stretch + side * sin( wave ) * amplitude * g_flFlagWorldScale ) * scale;

			i.vPositionWs.xyz = position;
			i.vPositionPs = Position3WsToPs( position );
			i.vNormalWs = normalize( side - along * cos( wave ) * amplitude * 6.2831853 / 0.3 );
			return i;
		}
	#endif

	PixelInput MainVs( VertexInput v )
	{
		#if S_FLAG_CLOTH
			// The cloth's rest direction and normal, and each vertex's distance from the attached
			// edge, read raw before the common code treats them as a tangent frame.
			float3 restOs = v.vTangentUOs_flTangentVSign.xyz;
			float3 normalOs = v.vNormalOs.xyz;
			float distance = v.vTexCoord2.x;
			float2 uv = v.vTexCoord.xy;
		#endif

		PixelInput i = ProcessVertex( v );

		#if S_FLAG_CLOTH
			i = WaveFlag( i, v.nInstanceTransformID, restOs, normalOs, distance, uv );
		#endif

		return FinalizeVertex( i );
	}
}

PS
{
	#include "common/pixel.hlsl"

	StaticCombo( S_BLEND_MODE, F_BLEND_MODE, Sys( ALL ) );

	RenderState( CullMode, NONE );
	RenderState( BlendEnable, true );
	RenderState( SrcBlendAlpha, ZERO );
	RenderState( DstBlendAlpha, ONE );

	#if S_BLEND_MODE == 0
		RenderState( SrcBlend, SRC_ALPHA );
		RenderState( DstBlend, INV_SRC_ALPHA );
		RenderState( BlendOp, ADD );
	#elif S_BLEND_MODE == 1
		RenderState( SrcBlend, DEST_COLOR );
		RenderState( DstBlend, ZERO );
		RenderState( BlendOp, ADD );
	#elif S_BLEND_MODE == 2
		RenderState( SrcBlend, DEST_COLOR );
		RenderState( DstBlend, SRC_COLOR );
		RenderState( BlendOp, ADD );
	#elif S_BLEND_MODE == 3
		RenderState( SrcBlend, ONE );
		RenderState( DstBlend, ONE );
		RenderState( BlendOp, ADD );
	#elif S_BLEND_MODE == 4
		RenderState( SrcBlend, ONE );
		RenderState( DstBlend, ONE );
		RenderState( BlendOp, REV_SUBTRACT );
	#elif S_BLEND_MODE == 5
		RenderState( SrcBlend, ONE );
		RenderState( DstBlend, ONE );
		RenderState( BlendOp, MIN );
	#elif S_BLEND_MODE == 6
		RenderState( SrcBlend, ONE );
		RenderState( DstBlend, ONE );
		RenderState( BlendOp, MAX );
	#else
		RenderState( SrcBlend, SRC_ALPHA );
		RenderState( DstBlend, ONE );
		RenderState( BlendOp, ADD );
	#endif

	SamplerState g_sWrap < Filter( Anisotropic ); AddressU( WRAP ); AddressV( WRAP ); >;
	SamplerState g_sClamp < Filter( Bilinear ); AddressU( CLAMP ); AddressV( CLAMP ); >;

	CreateInputTexture2D( Map0, Linear, 8, "None", "_color", ",0/,0/0", Default4( 1.00, 1.00, 1.00, 1.00 ) );
	CreateInputTexture2D( Map1, Linear, 8, "None", "_color", ",0/,0/0", Default4( 1.00, 1.00, 1.00, 1.00 ) );
	CreateInputTexture2D( Map2, Linear, 8, "None", "_color", ",0/,0/0", Default4( 1.00, 1.00, 1.00, 1.00 ) );
	CreateInputTexture2D( Map3, Linear, 8, "None", "_color", ",0/,0/0", Default4( 1.00, 1.00, 1.00, 1.00 ) );
	Texture2D g_tMap0 < Channel( RGBA, Box( Map0 ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( false ); >;
	Texture2D g_tMap1 < Channel( RGBA, Box( Map1 ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( false ); >;
	Texture2D g_tMap2 < Channel( RGBA, Box( Map2 ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( false ); >;
	Texture2D g_tMap3 < Channel( RGBA, Box( Map3 ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( false ); >;

	// Per map: (u scale, v scale, u offset, v offset), and U/V animation as (function, period, phase, scale).
	// The animation sources are the object function (0 none, 1-4 A-D out) that scales each map's
	// U and V animation on an object, x-w for maps 0-3.
	float4 g_vMap0Transform < Default4( 1.0, 1.0, 0.0, 0.0 ); >;
	float4 g_vMap1Transform < Default4( 1.0, 1.0, 0.0, 0.0 ); >;
	float4 g_vMap2Transform < Default4( 1.0, 1.0, 0.0, 0.0 ); >;
	float4 g_vMap3Transform < Default4( 1.0, 1.0, 0.0, 0.0 ); >;
	float4 g_vMap0AnimU < Default4( 0.0, 1.0, 0.0, 1.0 ); >;
	float4 g_vMap1AnimU < Default4( 0.0, 1.0, 0.0, 1.0 ); >;
	float4 g_vMap2AnimU < Default4( 0.0, 1.0, 0.0, 1.0 ); >;
	float4 g_vMap3AnimU < Default4( 0.0, 1.0, 0.0, 1.0 ); >;
	float4 g_vMap0AnimV < Default4( 0.0, 1.0, 0.0, 1.0 ); >;
	float4 g_vMap1AnimV < Default4( 0.0, 1.0, 0.0, 1.0 ); >;
	float4 g_vMap2AnimV < Default4( 0.0, 1.0, 0.0, 1.0 ); >;
	float4 g_vMap3AnimV < Default4( 0.0, 1.0, 0.0, 1.0 ); >;
	float4 g_vMapAnimationSourcesU < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float4 g_vMapAnimationSourcesV < Default4( 0.0, 0.0, 0.0, 0.0 ); >;

	// How many images each map's texture holds side by side (numeric shaders), or 0 for one image.
	float4 g_vMapFrames < Default4( 0.0, 0.0, 0.0, 0.0 ); >;

	// Numeric shaders: counter limit (0 if not numeric, -1 for the countdown timer), the first map's
	// image count, this part's digit place, and the object function shown (1 A, 4 D), which rests
	// at g_flNumericRest.
	float4 g_vNumeric < Default4( 0.0, 1.0, 0.0, 1.0 ); >;
	float g_flNumericRest < Default( 1.0 ); >;

	// The scripted countdown timer's milliseconds left, a scene attribute the game sets.
	float g_flHaloCountdownMilliseconds < Attribute( "HaloCountdownMilliseconds" ); Default( 0.0 ); >;

	// How maps 0-2 combine with the next map (Halo's map function enum), for color and alpha.
	float4 g_vColorFunctions < Default4( 2.0, 2.0, 2.0, 0.0 ); >;
	float4 g_vAlphaFunctions < Default4( 2.0, 2.0, 2.0, 0.0 ); >;

	float g_flMapCount < Default( 1.0 ); >;

	// Register combiner stages for generic shaders, one row per stage. 0 stages for chicago shaders.
	// Texels 9 and 10 are constant color 0's bounds for A out (both the constant itself when A out
	// doesn't control it); texel 11 is (constant color 0's change color source 0-4, 1 when A out
	// controls it, 0, 0).
	CreateInputTexture2D( Stages, Linear, 8, "None", "_color", ",0/,0/0", Default4( 0.00, 0.00, 0.00, 0.00 ) );
	Texture2D g_tStages < Channel( RGBA, Box( Stages ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( false ); >;
	float g_flStageCount < Default( 0.0 ); >;
	float g_flFadeMode < Default( 0.0 ); >;

	// The object function the result fades by on an object (0 none, 1-4 A-D out).
	float g_flFadeSource < Default( 0.0 ); >;
	float3 g_vTint < Default3( 1.0, 1.0, 1.0 ); >;

	// Object functions a game sets (see the top of the file); below 0 isn't set.
	float g_flHaloFunctionA < Attribute( "HaloFunctionA" ); Default( -1.0 ); >;
	float g_flHaloFunctionB < Attribute( "HaloFunctionB" ); Default( -1.0 ); >;
	float g_flHaloFunctionC < Attribute( "HaloFunctionC" ); Default( -1.0 ); >;
	float g_flHaloFunctionD < Attribute( "HaloFunctionD" ); Default( -1.0 ); >;

	// The object's change colors A-D: the material's (the object's steady colors, white where they
	// follow its functions), or the game's when it sets them (below 0 isn't set). Constant color 0
	// of a generic stage with a change color source is multiplied by one.
	float3 g_vChangeColorA < Default3( 1.0, 1.0, 1.0 ); >;
	float3 g_vChangeColorB < Default3( 1.0, 1.0, 1.0 ); >;
	float3 g_vChangeColorC < Default3( 1.0, 1.0, 1.0 ); >;
	float3 g_vChangeColorD < Default3( 1.0, 1.0, 1.0 ); >;
	float3 g_vHaloChangeColorA < Attribute( "HaloChangeColorA" ); Default3( -1.0, -1.0, -1.0 ); >;
	float3 g_vHaloChangeColorB < Attribute( "HaloChangeColorB" ); Default3( -1.0, -1.0, -1.0 ); >;
	float3 g_vHaloChangeColorC < Attribute( "HaloChangeColorC" ); Default3( -1.0, -1.0, -1.0 ); >;
	float3 g_vHaloChangeColorD < Attribute( "HaloChangeColorD" ); Default3( -1.0, -1.0, -1.0 ); >;

	float3 StageChangeColor( float source )
	{
		if ( source < 1.5 ) return g_vHaloChangeColorA.x >= 0 ? g_vHaloChangeColorA : g_vChangeColorA;
		if ( source < 2.5 ) return g_vHaloChangeColorB.x >= 0 ? g_vHaloChangeColorB : g_vChangeColorB;
		if ( source < 3.5 ) return g_vHaloChangeColorC.x >= 0 ? g_vHaloChangeColorC : g_vChangeColorC;
		return g_vHaloChangeColorD.x >= 0 ? g_vHaloChangeColorD : g_vChangeColorD;
	}

	// The object's active camouflage (0-1), as on its model (halo1_model.shader).
	float g_flHaloCamo < Attribute( "HaloCamo" ); Default( 0.0 ); >;

	// The value a game set for an object function (1-4 A-D out), or below 0 if it didn't.
	float HaloFunctionValue( float source )
	{
		if ( source < 0.5 ) return -1;
		if ( source < 1.5 ) return g_flHaloFunctionA;
		if ( source < 2.5 ) return g_flHaloFunctionB;
		if ( source < 3.5 ) return g_flHaloFunctionC;
		return g_flHaloFunctionD;
	}

	// Decals use alpha as coverage in every blend mode, not just alpha blending: outside the
	// decal's shape the color is faded to whatever leaves the background unchanged.
	float g_flAlphaCoverage < Default( 0.0 ); >;

	// Alpha blending in Halo's gamma space covered a dark background far less than the same alpha
	// does in linear space (alpha a over black is a^2.2 of the color). Set for surfaces that lie on
	// water (b30's surf), whose background is the dark sea; at 0.5 alpha the foam came out twice as
	// strong. Not for everything: over bright backgrounds Halo's blend covered closer to a.
	float g_flGammaAlpha < Default( 0.0 ); >;

	// sRGB conversions, written out to work on values above 1 from the HDR frame buffer.
	float3 ToLinear( float3 c )
	{
		c = max( c, 0 );
		return lerp( pow( ( c + 0.055 ) / 1.055, 2.4 ), c / 12.92, step( c, 0.04045 ) );
	}

	float3 ToGamma( float3 c )
	{
		c = max( c, 0 );
		return lerp( 1.055 * pow( c, 1.0 / 2.4 ) - 0.055, c * 12.92, step( c, 0.0031308 ) );
	}

	// Halo's atmospheric fog density and gamma color at a point, taken back out of s&box's fog, which holds the density raised to 2.2 (ScenarioSceneLoader.BuildFog).
	float HaloAtmosphere( float3 positionWs, float2 screen, out float3 colorGamma )
	{
		float3 fogged0 = DoAtmospherics( positionWs, screen, float4( 0, 0, 0, 1 ) ).rgb;
		float linearDensity = saturate( 1 - dot( saturate( DoAtmospherics( positionWs, screen, float4( 1, 1, 1, 1 ) ).rgb - fogged0 ), 1.0 / 3.0 ) );
		colorGamma = ToGamma( fogged0 / max( linearDensity, 1e-4 ) );
		return pow( linearDensity, 1.0 / 2.2 );
	}


	// Halo's texture coordinate animation functions: slide scrolls steadily, cosine sways.
	float Animate( float4 anim )
	{
		float t = g_flTime / anim.y + anim.z;
		if ( anim.x == 2 || anim.x == 3 ) return cos( t * 6.2831853 ) * anim.w;
		if ( anim.x == 6 || anim.x == 7 ) return frac( t ) * anim.w;
		return 0;
	}

	// An animation scaled by an object function, as Halo animated maps on objects: the function's
	// value times the wave (a constant "one" wave makes the offset follow the function).
	float AnimateBy( float4 anim, float source )
	{
		float value = HaloFunctionValue( source );
		if ( value < 0 ) return Animate( anim );
		if ( anim.x == 0 ) return anim.w * saturate( value );
		return Animate( anim ) * saturate( value );
	}

	// The digit a numeric shader shows on this part: the function times the limit, rounded and
	// clamped to the limit, then the digit at this part's place in base "image count".
	float NumericDigit()
	{
		float value = HaloFunctionValue( g_vNumeric.w );
		if ( value < 0 )
			value = g_flNumericRest;

		int limit = (int)g_vNumeric.x;
		int count = max( (int)g_vNumeric.y, 1 );
		int number = clamp( (int)floor( limit * value + 0.5 ), 0, limit );
		for ( int place = 0; place < (int)g_vNumeric.z; place++ )
			number /= count;

		return number % count;
	}

	// The countdown timer's digit at this part's place: thousandths, hundredths, tenths and
	// seconds, tens of seconds (0-5), minutes, tens of minutes (0-5), hours, tens of hours.
	float CountdownDigit()
	{
		int ms = (int)g_flHaloCountdownMilliseconds;
		switch ( (int)g_vNumeric.z )
		{
			case 0: return ms % 10;
			case 1: return ms / 10 % 10;
			case 2: return ms / 100 % 10;
			case 3: return ms / 1000 % 10;
			case 4: return ms / 10000 % 6;
			case 5: return ms / 60000 % 10;
			case 6: return ms / 600000 % 6;
			case 7: return ms / 3600000 % 10;
			case 8: return ms / 36000000 % 10;
		}
		return 0;
	}

	// frames: images side by side in the texture (0 for a plain texture); image: which one.
	float4 SampleMap( Texture2D map, float2 uv, float4 transform, float4 animU, float4 animV, float sourceU, float sourceV, float frames, float image )
	{
		float2 offset = transform.zw + float2( AnimateBy( animU, sourceU ), AnimateBy( animV, sourceV ) );
		float2 st = uv * transform.xy + offset;
		if ( frames < 1.5 )
			return Tex2DS( map, g_sWrap, st );

		// Wrapped within the image, half a texel in from its edges so filtering stays inside it.
		float2 size;
		map.GetDimensions( size.x, size.y );
		float2 inset = 0.5 / float2( size.x / frames, size.y );
		st = clamp( frac( st ), inset, 1 - inset );
		return map.SampleLevel( g_sClamp, float2( ( fmod( image, frames ) + st.x ) / frames, st.y ), 0 );
	}

	float4 Combine( float4 current, float4 next, float colorFunction, float alphaFunction )
	{
		float4 result;
		[unroll] for ( int c = 0; c < 2; c++ )
		{
			float f = c == 0 ? colorFunction : alphaFunction;
			float4 r;
			if ( f < 0.5 ) r = current;
			else if ( f < 1.5 ) r = next;
			else if ( f < 2.5 ) r = current * next;
			else if ( f < 3.5 ) r = 2.0 * current * next;
			else if ( f < 4.5 ) r = current + next;
			else if ( f < 6.5 ) r = current + next - 0.5;
			else if ( f < 7.5 ) r = current - next;
			else if ( f < 8.5 ) r = next - current;
			else if ( f < 9.5 ) r = lerp( next, current, current.a );
			else if ( f < 10.5 ) r = lerp( current, next, current.a );
			else if ( f < 11.5 ) r = lerp( current, next, next.a );
			else r = lerp( next, current, next.a );

			if ( c == 0 ) result.rgb = r.rgb;
			else result.a = r.a;
		}
		return saturate( result );
	}

	// Register file: 0-3 maps, 4-5 vertex colors (lighting and fades), 6-7 scratch, 8-9 constants.
	static float4 R[10];

	float3 ColorInput( int input )
	{
		if ( input == 0 ) return 0;
		if ( input == 1 ) return 1;
		if ( input == 2 ) return 0.5;
		if ( input == 3 ) return -1;
		if ( input == 4 ) return -0.5;
		if ( input <= 14 ) return R[input - 5].rgb;
		return R[input - 15].aaa;
	}

	float AlphaInput( int input )
	{
		if ( input == 0 ) return 0;
		if ( input == 1 ) return 1;
		if ( input == 2 ) return 0.5;
		if ( input == 3 ) return -1;
		if ( input == 4 ) return -0.5;
		if ( input <= 14 ) return R[input - 5].a;
		return R[input - 15].b;
	}

	// clamp, 1 - clamp, expand, expand negate, half bias, half bias negate, signed, signed negate
	float MapInput( float v, int m )
	{
		float u = saturate( v );
		if ( m == 0 ) return u;
		if ( m == 1 ) return 1 - u;
		if ( m == 2 ) return 2 * u - 1;
		if ( m == 3 ) return 1 - 2 * u;
		if ( m == 4 ) return u - 0.5;
		if ( m == 5 ) return 0.5 - u;
		if ( m == 6 ) return v;
		return -v;
	}

	float3 MapInput3( float3 v, int m ) { return float3( MapInput( v.x, m ), MapInput( v.y, m ), MapInput( v.z, m ) ); }

	// identity, scale by 1/2, scale by 2, scale by 4, bias by -1/2, expand
	float3 MapOutput( float3 x, int mapping )
	{
		if ( mapping == 1 ) x *= 0.5;
		else if ( mapping == 2 ) x *= 2;
		else if ( mapping == 3 ) x *= 4;
		else if ( mapping == 4 ) x -= 0.5;
		else if ( mapping == 5 ) x = ( x - 0.5 ) * 2;
		return clamp( x, -1, 1 );
	}

	// Output register: 0 discard, 1-2 scratch, 3-4 vertex colors, 5-8 maps.
	int OutputRegister( int output ) { return output == 1 ? 6 : output == 2 ? 7 : output == 3 ? 4 : output == 4 ? 5 : output - 5; }

	void RunStage( int stage )
	{
		float4 colorIn = g_tStages.Load( int3( 0, stage, 0 ) );
		float4 colorMap = g_tStages.Load( int3( 1, stage, 0 ) );
		float4 colorOut = g_tStages.Load( int3( 2, stage, 0 ) );
		float4 functions = g_tStages.Load( int3( 3, stage, 0 ) );
		float4 alphaIn = g_tStages.Load( int3( 4, stage, 0 ) );
		float4 alphaMap = g_tStages.Load( int3( 5, stage, 0 ) );
		float4 alphaOut = g_tStages.Load( int3( 6, stage, 0 ) );
		R[8] = g_tStages.Load( int3( 7, stage, 0 ) );
		R[9] = g_tStages.Load( int3( 8, stage, 0 ) );
		float4 constant0 = g_tStages.Load( int3( 11, stage, 0 ) );
		if ( constant0.y > 0.5 && g_flHaloFunctionA >= 0 )
			R[8] = lerp( g_tStages.Load( int3( 9, stage, 0 ) ), g_tStages.Load( int3( 10, stage, 0 ) ), saturate( g_flHaloFunctionA ) );
		if ( constant0.x > 0.5 )
			R[8].rgb *= StageChangeColor( constant0.x );

		float3 c[4];
		float a[4];
		[unroll] for ( int k = 0; k < 4; k++ )
		{
			c[k] = MapInput3( ColorInput( (int)colorIn[k] ), (int)colorMap[k] );
			a[k] = MapInput( AlphaInput( (int)alphaIn[k] ), (int)alphaMap[k] );
		}

		float3 ab = functions.x > 0.5 ? dot( c[0], c[1] ).xxx : c[0] * c[1];
		float3 cd = functions.y > 0.5 ? dot( c[2], c[3] ).xxx : c[2] * c[3];
		float3 abcd = functions.z > 0.5 ? ( R[6].a >= 0.5 ? cd : ab ) : ab + cd;
		float aab = a[0] * a[1];
		float acd = a[2] * a[3];
		float aabcd = functions.w > 0.5 ? ( R[6].a >= 0.5 ? acd : aab ) : aab + acd;

		int colorMapping = (int)colorOut.w;
		int alphaMapping = (int)alphaOut.w;
		ab = MapOutput( ab, colorMapping );
		cd = MapOutput( cd, colorMapping );
		abcd = MapOutput( abcd, colorMapping );
		float3 alphas = MapOutput( float3( aab, acd, aabcd ), alphaMapping );

		// Every output is computed from the inputs before any is written, like the hardware.
		if ( colorOut.x > 0.5 ) R[OutputRegister( (int)colorOut.x )].rgb = ab;
		if ( colorOut.y > 0.5 ) R[OutputRegister( (int)colorOut.y )].rgb = cd;
		if ( colorOut.z > 0.5 ) R[OutputRegister( (int)colorOut.z )].rgb = abcd;
		if ( alphaOut.x > 0.5 ) R[OutputRegister( (int)alphaOut.x )].a = alphas.x;
		if ( alphaOut.y > 0.5 ) R[OutputRegister( (int)alphaOut.y )].a = alphas.y;
		if ( alphaOut.z > 0.5 ) R[OutputRegister( (int)alphaOut.z )].a = alphas.z;
	}

	float4 MainPs( PixelInput i ) : SV_Target0
	{
		float2 uv = i.vTextureCoords.xy;
		float digit = g_vNumeric.x > 0 ? NumericDigit() : g_vNumeric.x < 0 ? CountdownDigit() : 0;

		float4 map0 = SampleMap( g_tMap0, uv, g_vMap0Transform, g_vMap0AnimU, g_vMap0AnimV, g_vMapAnimationSourcesU.x, g_vMapAnimationSourcesV.x, g_vMapFrames.x, digit );
		float4 map1 = SampleMap( g_tMap1, uv, g_vMap1Transform, g_vMap1AnimU, g_vMap1AnimV, g_vMapAnimationSourcesU.y, g_vMapAnimationSourcesV.y, g_vMapFrames.y, digit );
		float4 map2 = SampleMap( g_tMap2, uv, g_vMap2Transform, g_vMap2AnimU, g_vMap2AnimV, g_vMapAnimationSourcesU.z, g_vMapAnimationSourcesV.z, g_vMapFrames.z, digit );
		float4 map3 = SampleMap( g_tMap3, uv, g_vMap3Transform, g_vMap3AnimU, g_vMap3AnimV, g_vMapAnimationSourcesU.w, g_vMapAnimationSourcesV.w, g_vMapFrames.w, digit );

		// How directly the surface faces the camera: 1 head on, 0 edge on.
		float facing = saturate( abs( dot( normalize( i.vNormalWs ), normalize( -i.vPositionWithOffsetWs.xyz ) ) ) );

		float4 color;
		if ( g_flStageCount > 0.5 )
		{
			R[0] = map0; R[1] = map1; R[2] = map2; R[3] = map3;
			R[4] = float4( 1, 1, 1, 1 );                                  // diffuse light (unlit), fade(none)
			R[5] = float4( 1 - facing, 1 - facing, facing, 1 - facing );  // fade(perpendicular), blue: fade(parallel)
			R[6] = float4( 0, 0, 0, map0.a );                             // scratch 0 starts with map 0's alpha
			R[7] = 0;
			R[8] = 0;
			R[9] = 0;

			[loop] for ( int stage = 0; stage < (int)g_flStageCount; stage++ )
				RunStage( stage );

			color = saturate( R[6] );
		}
		else
		{
			color = map0;
			if ( g_flMapCount > 1.5 ) color = Combine( color, map1, g_vColorFunctions.x, g_vAlphaFunctions.x );
			if ( g_flMapCount > 2.5 ) color = Combine( color, map2, g_vColorFunctions.y, g_vAlphaFunctions.y );
			if ( g_flMapCount > 3.5 ) color = Combine( color, map3, g_vColorFunctions.z, g_vAlphaFunctions.z );
		}

		color.rgb *= g_vTint;

		if ( g_flAlphaCoverage > 0.5 )
		{
			#if S_BLEND_MODE == 1
				color.rgb = lerp( 1.0, color.rgb, color.a );
			#elif S_BLEND_MODE == 2
				color.rgb = lerp( 0.5, color.rgb, color.a );
			#elif S_BLEND_MODE == 3 || S_BLEND_MODE == 4 || S_BLEND_MODE == 6
				color.rgb *= color.a;
			#endif
		}

		// Fade toward having no effect when seen head on or edge on.
		float fade = g_flFadeMode < 0.5 ? 1.0 : ( g_flFadeMode < 1.5 ? 1.0 - facing : facing );
		float fadeFunction = HaloFunctionValue( g_flFadeSource );
		if ( fadeFunction >= 0 )
			fade *= saturate( fadeFunction );

		// A camouflaged object's transparent parts (the cyborg's armor lights, a weapon's display)
		// fade out with its camouflage, gone when fully cloaked.
		fade *= 1 - saturate( g_flHaloCamo );

		// Fog: surfaces that add light fade out with the fog in front of them, ones that cover
		// what's behind them are fogged like anything else, both in gamma space by Halo's density
		// (MCC's stage shaders fade toward black or the fog color): s&box's own transmittance, the
		// density raised to 2.2, let c10's light strips shine through its fog about twice as bright.
		float3 positionWs = i.vPositionWithOffsetWs.xyz + g_vHighPrecisionLightingOffsetWs.xyz;
		float3 fogGamma;
		float fogDensity = HaloAtmosphere( positionWs, i.vPositionSs.xy, fogGamma );
		#if S_BLEND_MODE == 3 || S_BLEND_MODE == 6 || S_BLEND_MODE == 7
			fade *= 1 - fogDensity;
		#endif

		// Halo blended in gamma space and the frame buffer is linear, so the color goes out linear,
		// and is shaped so each blend mode's result is close to what Halo produced.
		float3 rgb = ToLinear( color.rgb );
		#if S_BLEND_MODE == 0
			rgb = ToLinear( lerp( ToGamma( rgb ), fogGamma, fogDensity ) );
		#endif
		#if S_BLEND_MODE == 0
			float alpha = saturate( color.a );
			return float4( rgb, ( g_flGammaAlpha > 0.5 ? pow( alpha, 2.2 ) : alpha ) * fade );
		#elif S_BLEND_MODE == 7
			return float4( rgb, color.a * fade );
		#elif S_BLEND_MODE == 1
			return float4( lerp( 1.0, rgb, fade ), 1 );
		#elif S_BLEND_MODE == 2
			// The GPU computes 2 * src * dst, so 0.5 leaves the background alone.
			return float4( lerp( 0.5, 0.5 * pow( 2.0 * max( color.rgb, 0 ), 2.2 ), fade ), 1 );
		#elif S_BLEND_MODE == 5
			return float4( lerp( 65504.0, rgb, fade ), 1 );
		#else
			return float4( rgb * fade, 1 );
		#endif
	}
}
