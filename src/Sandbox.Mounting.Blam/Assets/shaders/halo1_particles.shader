//
// Halo CE particles: weather (rain, snow, swamp bugs) and the looping effects on scenery (steam,
// smoke, sparks, drips).
//
// Particles are simulated entirely on the GPU from data fixed when the map loads, so a mounted
// scene needs no code running. Every particle is a quad whose four corners carry the same
// particle; the vertex shader works out where the particle is at the current time and builds the
// sprite around it.
//
// Weather (F_WEATHER 1): each particle has a fixed place in a cube that repeats through space,
// drifting with the wind and falling. The cube is wrapped around the camera, so particles stay
// put in the world while there are always some around the viewer, the way Halo did it. Where
// weather falls comes from a grid of cells sampled from the BSP's clusters (see WeatherVolume),
// stored as 2D slices side by side in a texture.
//
// Effects (F_WEATHER 0): each particle is born at an emitter at its own point in a looping cycle,
// flies off with its initial velocity under gravity and air friction, grows or shrinks, and
// fades in and out over its life.
//
// Zsprites (F_ZSPRITE: a30's waterfall spray): as in halo1_sprite.shader, each pixel is tested
// against the depth buffer at the sprite's depth pulled toward the camera by its depth map (the
// secondary bitmap at the sprite's coordinates) x the particle's radius x the zsprite radius
// scale, so a cloud meets the water and ground with a soft, noisy edge instead of a straight cut.
//
// Lightning (F_WEATHER 0, g_flLightning 1): bolts through an object's markers. Each vertex is one
// side of a point along a segment between two markers; the vertex shader redraws the bolt 30
// times a second by midpoint displacement across the segment as seen from the camera, the way
// Halo regenerated it every frame, and the markers wander inside their random bounds.
//

HEADER
{
	Description = "Halo CE particles";
}

FEATURES
{
	#include "common/features.hlsl"

	Feature( F_WEATHER, 0..1, "Particles" );

	// Halo's framebuffer blend function: alpha blend, multiply, double multiply, add, subtract,
	// component min, component max, alpha-multiply add.
	Feature( F_BLEND_MODE, 0..7( 0="Alpha Blend", 1="Multiply", 2="Double Multiply", 3="Add", 4="Subtract", 5="Component Min", 6="Component Max", 7="Alpha-Multiply Add" ), "Blending" );
	Feature( F_ZSPRITE, 0..1, "Blending" );
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
}

struct VertexInput
{
	// Weather: the particle's place in the repeating cube (0..1). Effects: the emitter's position.
	// Lightning: the segment's start marker.
	float3 vPositionOs : POSITION < Semantic( PosXyz ); >;
	// Corner (-1..1), and two random numbers (0..1). Lightning: the end marker, jitter at the start.
	float4 vCorner : TEXCOORD0 < Semantic( None ); >;
	// Color in gamma space, and alpha.
	float4 vColor : TEXCOORD1 < Semantic( None ); >;
	// Radius (s&box units). Effects: lifetime and the point in the cycle it's born (seconds), cycle length.
	// Lightning: point index in the segment, side * ( octaves + 1 ), half width, u along the bolt.
	float4 vParticle : TEXCOORD2 < Semantic( None ); >;
	// Effects: initial velocity (s&box units per second), and rotation (radians).
	// Lightning: jitter at the end marker, bolt * 64 + segment, both markers' packed random bounds.
	float4 vMotion : TEXCOORD3 < Semantic( None ); >;
};

struct PixelInput
{
	float4 vPositionPs : SV_Position;
	float2 vUv : TEXCOORD0;
	float4 vColor : TEXCOORD1;
	float3 vPositionWs : TEXCOORD2;
	// Sprite frame, and 1 for lightning.
	float2 vFrame : TEXCOORD3;
	// Zsprites: the pixel's place on the sprite in the world, and how far (s&box units) the depth
	// map's white reaches toward the viewer.
	float4 vZSprite : TEXCOORD4;
};

VS
{
	StaticCombo( S_WEATHER, F_WEATHER, Sys( ALL ) );

	// How sprites face: 0 the screen, 1 along their direction of travel, 2 across it.
	float g_flOrientation < Default( 0.0 ); >;
	// Halo's framebuffer fade mode for sprites along a direction: 1 fades them as they're seen end on, 2 as they're seen side on.
	float g_flFadeMode < Default( 0.0 ); >;
	float g_flAnimationRate < Default( 0.0 ); >;
	float g_flFrameCount < Default( 1.0 ); >;

	// Weather: drift velocity (s&box units per second), the repeating cube's size, how far
	// particles wander, and the fade in start/end and fade out start/end distances.
	float3 g_vWeatherVelocity < Default3( 0.0, 0.0, 0.0 ); >;
	float g_flWeatherCube < Default( 1000.0 ); >;
	float g_flWander < Default( 0.0 ); >;
	float4 g_vWeatherFade < Default4( 0.0, 0.0, 400.0, 500.0 ); >;

	// Effects: acceleration (gravity and buoyancy), air friction (per second), fade in and out
	// times (seconds), and radius at birth and death as multiples of the particle's radius.
	float3 g_vGravity < Default3( 0.0, 0.0, 0.0 ); >;
	float g_flDrag < Default( 0.0 ); >;
	float2 g_vFadeTimes < Default2( 0.0, 0.0 ); >;
	float2 g_vRadiusAnimation < Default2( 1.0, 1.0 ); >;

	// A zsprite's reach in particle radii (the zsprite radius scale).
	float g_flZSpriteScale < Default( 0.0 ); >;

	// Lightning: 1 to draw bolts, and the object functions scaling their jitter, thickness and
	// brightness and moving their tint between two colors, each as (wave, period, scale, offset)
	// and output range: clamp( wave * scale + offset, range ).
	float g_flLightning < Default( 0.0 ); >;
	float4 g_vLightningJitter < Default4( 0.0, 1.0, 0.0, 1.0 ); >;
	float2 g_vLightningJitterRange < Default2( 0.0, 1.0 ); >;
	float4 g_vLightningThickness < Default4( 0.0, 1.0, 0.0, 1.0 ); >;
	float2 g_vLightningThicknessRange < Default2( 0.0, 1.0 ); >;
	float4 g_vLightningBrightness < Default4( 0.0, 1.0, 0.0, 1.0 ); >;
	float2 g_vLightningBrightnessRange < Default2( 0.0, 1.0 ); >;
	float4 g_vLightningTint < Default4( 0.0, 1.0, 0.0, 1.0 ); >;
	float2 g_vLightningTintRange < Default2( 0.0, 1.0 ); >;
	float3 g_vLightningTintLower < Default3( 1.0, 1.0, 1.0 ); >;
	float3 g_vLightningTintUpper < Default3( 1.0, 1.0, 1.0 ); >;

	float3 Hash3( float2 r )
	{
		return frac( sin( float3( r.x * 127.1 + r.y * 311.7, r.x * 269.5 + r.y * 183.3, r.x * 419.2 + r.y * 371.9 ) ) * 43758.5453 );
	}

	float Hash( float n )
	{
		return frac( sin( n * 12.9898 ) * 43758.5453 );
	}

	float SmoothNoise( float t )
	{
		float i = floor( t );
		float f = frac( t );
		return lerp( Hash( i ), Hash( i + 1.0 ), f * f * ( 3.0 - 2.0 * f ) );
	}

	// Halo's periodic functions, 0..1 over one period (as in halo1_environment.shader).
	float Periodic( float function, float t )
	{
		if ( function < 0.5 ) return 1.0;									// one
		if ( function < 1.5 ) return 0.0;									// zero
		if ( function < 3.5 ) return 0.5 + 0.5 * cos( t * 6.2831853 );		// cosine
		if ( function < 5.5 ) return 1.0 - abs( 2.0 * frac( t ) - 1.0 );	// diagonal wave
		if ( function < 7.5 ) return frac( t );								// slide
		if ( function < 8.5 ) return SmoothNoise( t * 4.0 );				// noise
		if ( function < 9.5 ) return Hash( floor( t * 16.0 ) );				// jitter
		if ( function < 10.5 ) return SmoothNoise( t );						// wander
		return Hash( floor( t * 8.0 ) ) > 0.85 ? 1.0 : 0.0;					// spark
	}

	float ObjectFunction( float4 wave, float2 range )
	{
		return clamp( Periodic( wave.x, g_flTime / max( wave.y, 0.001 ) ) * wave.z + wave.w, range.x, range.y );
	}

	// A random number (0..1) that comes out the same wherever it's computed from the same inputs.
	float LightningRandom( uint a, uint b, uint c )
	{
		uint h = a * 0x9E3779B1u ^ b * 0x85EBCA77u ^ c * 0xC2B2AE3Du;
		h ^= h >> 16;
		h *= 0x7FEB352Du;
		h ^= h >> 15;
		h *= 0x846CA68Bu;
		h ^= h >> 16;
		return ( h & 0xFFFFFF ) / 16777216.0;
	}

	// How far point k of a segment split into 2^octaves pieces is moved off the straight line.
	// Each octave moves its new midpoints by up to the jitter (interpolated along the segment,
	// halving every octave) and drags the finer points between them along, so the total is the
	// sum over octaves of that octave's offsets interpolated between its points.
	float LightningOffset( float k, int octaves, float2 jitter, uint segment, uint frame )
	{
		float n = exp2( octaves );
		float offset = 0;
		float scale = 1;

		[loop]
		for ( int octave = 1; octave <= octaves; octave++ )
		{
			float spacing = exp2( octaves - octave );
			float j = floor( k / spacing );
			float t = k / spacing - j;
			float p0 = j * spacing;
			float p1 = p0 + spacing;

			// This octave's new points are the odd multiples of the spacing.
			float v0 = fmod( j, 2.0 ) > 0.5 ? ( LightningRandom( segment, (uint)p0, frame ) * 2 - 1 ) * lerp( jitter.x, jitter.y, p0 / n ) : 0;
			float v1 = fmod( j + 1, 2.0 ) > 0.5 ? ( LightningRandom( segment, (uint)p1, frame ) * 2 - 1 ) * lerp( jitter.x, jitter.y, p1 / n ) : 0;
			offset += lerp( v0, v1, t ) * scale;
			scale *= 0.5;
		}

		return offset;
	}

	// Where a marker has wandered to this frame inside its random bounds (s&box units along the
	// world axes, packed 8 bits each in steps of 0.5).
	float3 MarkerWander( float packed, uint bolt, uint marker, uint frame )
	{
		uint bits = (uint)packed;
		float3 bounds = float3( bits & 255, ( bits >> 8 ) & 255, ( bits >> 16 ) & 255 ) * 0.5;
		uint id = 0x10000u + marker * 4u;
		return ( float3( LightningRandom( bolt, id, frame ), LightningRandom( bolt, id + 1, frame ), LightningRandom( bolt, id + 2, frame ) ) * 2 - 1 ) * bounds;
	}

	PixelInput LightningVs( VertexInput v, PixelInput o )
	{
		uint frame = (uint)floor( g_flTime * 30.0 );
		uint seed = (uint)v.vMotion.y;
		uint bolt = seed / 64;
		uint segment = seed % 64;
		int octaves = (int)abs( v.vParticle.y ) - 1;
		float side = v.vParticle.y < 0 ? -1 : 1;
		float n = exp2( octaves );
		float k = v.vParticle.x;

		// Both segments meeting at a marker move it the same way.
		float3 a = v.vPositionOs + MarkerWander( v.vMotion.z, bolt, segment, frame );
		float3 b = v.vCorner.xyz + MarkerWander( v.vMotion.w, bolt, segment + 1, frame );

		// Points move across the segment as seen from the camera.
		float3 across = cross( b - a, g_vCameraDirWs );
		across = length( across ) > 0.0001 ? normalize( across ) : float3( 0, 0, 1 );

		float2 jitter = float2( v.vCorner.w, v.vMotion.x ) * ObjectFunction( g_vLightningJitter, g_vLightningJitterRange );
		uint segmentSeed = seed * 31 + 7;
		float kPrevious = max( k - 1, 0 );
		float kNext = min( k + 1, n );
		float3 position = lerp( a, b, k / n ) + across * LightningOffset( k, octaves, jitter, segmentSeed, frame );
		float3 previous = lerp( a, b, kPrevious / n ) + across * LightningOffset( kPrevious, octaves, jitter, segmentSeed, frame );
		float3 next = lerp( a, b, kNext / n ) + across * LightningOffset( kNext, octaves, jitter, segmentSeed, frame );

		// The ribbon faces the camera, its width across the bolt's direction at this point.
		float3 sideways = cross( next - previous, g_vCameraDirWs );
		sideways = length( sideways ) > 0.0001 ? normalize( sideways ) : across;
		float width = v.vParticle.z * ObjectFunction( g_vLightningThickness, g_vLightningThicknessRange );

		o.vPositionPs = Position3WsToPs( position + sideways * width * side );
		o.vPositionWs = position;

		float3 tint = lerp( g_vLightningTintLower, g_vLightningTintUpper, ObjectFunction( g_vLightningTint, g_vLightningTintRange ) );
		o.vColor = float4( v.vColor.rgb * tint, v.vColor.a * ObjectFunction( g_vLightningBrightness, g_vLightningBrightnessRange ) );

		// The texture runs along the whole bolt from a new random offset every frame.
		o.vUv = float2( v.vParticle.w + LightningRandom( bolt, 0xFFFFFu, frame ), side > 0 ? 0 : 1 );
		o.vFrame = float2( 0, 1 );
		return o;
	}

	PixelInput MainVs( VertexInput v )
	{
		PixelInput o;
		o.vUv = v.vCorner.xy * float2( 0.5, -0.5 ) + 0.5;
		o.vPositionPs = float4( 2, 2, 2, 1 );
		o.vColor = 0;
		o.vPositionWs = 0;
		o.vFrame = 0;
		o.vZSprite = 0;

		#if !S_WEATHER
			if ( g_flLightning > 0.5 )
				return LightningVs( v, o );
		#endif

		float3 position;
		float3 direction;
		float radius = v.vParticle.x;
		float alpha = 1;
		float age;
		float rotation = 0;

		#if S_WEATHER
			float t = g_flTime;
			float3 seed = Hash3( v.vCorner.zw );

			// Place the particle in the copy of the cube nearest the camera.
			float3 drift = g_vWeatherVelocity * t;
			float3 wander = g_flWander * sin( t * ( 0.7 + seed * 0.8 ) + seed * 6.2831853 );
			position = g_vCameraPositionWs + ( frac( v.vPositionOs + ( drift - g_vCameraPositionWs ) / g_flWeatherCube ) - 0.5 ) * g_flWeatherCube + wander;
			direction = g_vWeatherVelocity + g_flWander * ( 0.7 + seed * 0.8 ) * cos( t * ( 0.7 + seed * 0.8 ) + seed * 6.2831853 );

			// Faded by depth along the view, as Halo did, not by distance.
			float depth = dot( position - g_vCameraPositionWs, g_vCameraDirWs );
			if ( g_vWeatherFade.y > g_vWeatherFade.x )
				alpha *= saturate( ( depth - g_vWeatherFade.x ) / ( g_vWeatherFade.y - g_vWeatherFade.x ) );
			alpha *= saturate( ( g_vWeatherFade.w - depth ) / max( g_vWeatherFade.w - g_vWeatherFade.z, 1 ) );

			age = t + seed.x * 100;
			rotation = v.vMotion.w;
		#else
			float lifetime = v.vParticle.y;
			float cycle = max( v.vParticle.w, 0.001 );
			age = fmod( g_flTime + v.vParticle.z, cycle );
			if ( age > lifetime )
				return o;

			float3 velocity = v.vMotion.xyz;
			if ( g_flDrag > 0.0001 )
			{
				// Velocity decays toward the terminal velocity gravity gives against the friction.
				float decay = ( 1 - exp( -g_flDrag * age ) ) / g_flDrag;
				float3 terminal = g_vGravity / g_flDrag;
				position = v.vPositionOs + terminal * age + ( velocity - terminal ) * decay;
				direction = terminal + ( velocity - terminal ) * exp( -g_flDrag * age );
			}
			else
			{
				position = v.vPositionOs + velocity * age + 0.5 * g_vGravity * age * age;
				direction = velocity + g_vGravity * age;
			}

			float life = age / max( lifetime, 0.001 );
			radius *= lerp( g_vRadiusAnimation.x, g_vRadiusAnimation.y, life );
			if ( g_vFadeTimes.x > 0 ) alpha *= saturate( age / g_vFadeTimes.x );
			if ( g_vFadeTimes.y > 0 ) alpha *= saturate( ( lifetime - age ) / g_vFadeTimes.y );
			rotation = v.vMotion.w;
		#endif

		if ( alpha <= 0 || radius <= 0 )
			return o;

		float3 toCamera = g_vCameraPositionWs - position;
		float3 right, up;
		float speed = length( direction );

		// Halo's framebuffer fade: the sine of the angle between the view ray and the sprite's direction (build_sprite_compute_vertex_fade).
		if ( g_flFadeMode > 0.5 && g_flOrientation > 0.5 && speed > 0.0001 && length( toCamera ) > 0.0001 )
		{
			float sine = length( cross( direction / speed, normalize( toCamera ) ) );
			alpha *= g_flFadeMode < 1.5 ? sine : 1 - sine;
		}

		if ( g_flOrientation > 0.5 && speed > 0.0001 )
		{
			float3 axis = direction / speed;
			float3 side = cross( axis, toCamera );
			side = length( side ) > 0.0001 ? normalize( side ) : normalize( cross( axis, float3( 0.3, 0.2, 0.9 ) ) );
			if ( g_flOrientation < 1.5 )
			{
				// Halo's streak sprites (rain, sparks) run along the bitmap's u axis.
				right = axis;
				up = side;
			}
			else
			{
				right = side;
				up = normalize( cross( side, axis ) );
			}
		}
		else
		{
			right = normalize( cross( g_vCameraDirWs, g_vCameraUpDirWs ) );
			up = normalize( cross( right, g_vCameraDirWs ) );
		}

		float s = sin( rotation );
		float c = cos( rotation );
		float2 corner = float2( v.vCorner.x * c - v.vCorner.y * s, v.vCorner.x * s + v.vCorner.y * c );
		if ( g_flOrientation > 0.5 )
			corner = v.vCorner.xy;

		float3 cornerWs = position + ( right * corner.x + up * corner.y ) * radius;
		o.vPositionPs = Position3WsToPs( cornerWs );
		o.vPositionWs = position;
		o.vZSprite = float4( cornerWs, radius * g_flZSpriteScale );
		o.vColor = float4( v.vColor.rgb, v.vColor.a * alpha );

		float count = max( g_flFrameCount, 1 );
		o.vFrame = float2( fmod( floor( v.vCorner.z * count + age * g_flAnimationRate ), count ), 0 );
		if ( o.vFrame.x < 0 ) o.vFrame.x += count;
		return o;
	}
}

PS
{
	#include "common/pixel.hlsl"

	StaticCombo( S_WEATHER, F_WEATHER, Sys( ALL ) );
	StaticCombo( S_BLEND_MODE, F_BLEND_MODE, Sys( ALL ) );
	StaticCombo( S_ZSPRITE, F_ZSPRITE, Sys( ALL ) );

	RenderState( CullMode, NONE );
	RenderState( DepthWriteEnable, false );
	RenderState( BlendEnable, true );
	RenderState( SrcBlendAlpha, ZERO );
	RenderState( DstBlendAlpha, ONE );

	#if S_ZSPRITE
		// Tested against the depth buffer in the shader, at the pulled depth.
		RenderState( DepthEnable, false );
	#endif

	#if S_BLEND_MODE == 0 && S_WEATHER
		// Premultiplied: weather lays itself over what's behind in gamma space (see MainPs).
		RenderState( SrcBlend, ONE );
		RenderState( DstBlend, INV_SRC_ALPHA );
		RenderState( BlendOp, ADD );
	#elif S_BLEND_MODE == 0
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

	SamplerState g_sClamp < Filter( Bilinear ); AddressU( CLAMP ); AddressV( CLAMP ); >;
	SamplerState g_sBehind < Filter( Point ); AddressU( CLAMP ); AddressV( CLAMP ); >;

	#if S_BLEND_MODE == 0 && S_WEATHER
		BoolAttribute( bWantsFBCopyTexture, true );
	#endif
	Texture2D g_tFrameBufferCopyTexture < Attribute( "FrameBufferCopyTexture" ); SrgbRead( false ); >;

	// The frame as drawn just before the weather (PlanarFogFrame grabs it), planar fog and water included: s&box's copy is from before every translucent thing, and rain blended against a frame without c10's mist came out far too faint over it.
	Texture2D g_tHaloWeatherBehind < Attribute( "HaloWeatherBehind" ); SrgbRead( false ); >;
	bool g_bHaloHasWeatherBehind < Attribute( "HaloHasWeatherBehind" ); Default( 0 ); >;

	// The sRGB curve both ways, so what's behind comes back unchanged where nothing is drawn.
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

	float3 Behind( float2 pixel )
	{
		if ( g_bHaloHasWeatherBehind )
			return saturate( g_tHaloWeatherBehind.SampleLevel( g_sBehind, ( pixel - g_vViewportOffset ) * g_vInvViewportSize, 0 ).rgb );

		float2 uv = pixel * g_vInvViewportSize * g_vFrameBufferCopyInvSizeAndUvScale.zw;
		return saturate( g_tFrameBufferCopyTexture.SampleLevel( g_sBehind, uv, 0 ).rgb );
	}

	// Lightning textures repeat along the bolt and use the whole bitmap.
	SamplerState g_sLightningWrap < Filter( Bilinear ); AddressU( WRAP ); AddressV( CLAMP ); >;

	CreateInputTexture2D( Sprite, Linear, 8, "None", "_color", ",0/,0/0", Default4( 1.00, 1.00, 1.00, 1.00 ) );
	Texture2D g_tSprite < Channel( RGBA, Box( Sprite ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( false ); >;

	// The sprite's animation frames as texture rectangles (left, top, right, bottom), one texel each.
	CreateInputTexture2D( Frames, Linear, 8, "None", "_color", ",0/,0/0", Default4( 0.00, 0.00, 1.00, 1.00 ) );
	Texture2D g_tFrames < Channel( RGBA, Box( Frames ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( false ); >;

	// Where weather falls: cells of 0 (none) or 1 + the weather system (a float texture), slices
	// of the grid side by side. Origin and cell size in s&box units, grid size in cells, slices per row.
	CreateInputTexture2D( WeatherMask, Linear, 8, "None", "_color", ",0/,0/0", Default4( 1.00, 1.00, 1.00, 1.00 ) );
	Texture2D g_tWeatherMask < Channel( RGBA, Box( WeatherMask ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( false ); >;
	float3 g_vMaskOrigin < Default3( 0.0, 0.0, 0.0 ); >;
	float g_flMaskCell < Default( 100.0 ); >;
	float3 g_vMaskSize < Default3( 1.0, 1.0, 1.0 ); >;
	float g_flMaskColumns < Default( 1.0 ); >;
	// The cell value this material's weather system has; 0 doesn't check the mask.
	float g_flMaskValue < Default( 0.0 ); >;

	// A zsprite's depth map, in its red channel.
	SamplerState g_sZMap < Filter( Bilinear ); AddressU( WRAP ); AddressV( WRAP ); >;
	CreateInputTexture2D( ZMap, Linear, 8, "None", "_color", ",0/,0/0", Default4( 0.00, 0.00, 0.00, 1.00 ) );
	Texture2D g_tZMap < Channel( RGBA, Box( ZMap ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( false ); >;

	// Nearer than this (s&box units, a world unit) a pixel is taken to be a first person view
	// model, which is in front of everything in Halo, so the sprite keeps its own depth there.
	static const float FirstPersonDepth = 103.2;

	bool WeatherFalls( float3 positionWs )
	{
		if ( g_flMaskValue < 0.5 )
			return true;

		int3 cell = (int3)floor( ( positionWs - g_vMaskOrigin ) / g_flMaskCell );
		if ( any( cell < 0 ) || any( cell >= (int3)g_vMaskSize ) )
			return false;

		int columns = max( (int)g_flMaskColumns, 1 );
		int2 texel = int2( cell.z % columns, cell.z / columns ) * (int2)g_vMaskSize.xy + cell.xy;
		float value = g_tWeatherMask.Load( int3( texel, 0 ) ).r;
		return abs( value - g_flMaskValue ) < 0.5;
	}

	float4 MainPs( PixelInput i ) : SV_Target0
	{
		#if S_WEATHER
			if ( !WeatherFalls( i.vPositionWs ) )
				discard;
		#endif

		#if S_ZSPRITE
		{
			float3 eye = g_vCameraPositionWs;
			float spriteDistance = distance( i.vZSprite.xyz, eye );
			float sceneDistance = distance( Depth::GetWorldPosition( i.vPositionPs.xy ), eye );
			float pulled = spriteDistance - Tex2DS( g_tZMap, g_sZMap, i.vUv ).r * i.vZSprite.w;
			if ( ( sceneDistance < FirstPersonDepth ? spriteDistance : pulled ) > sceneDistance )
				discard;
		}
		#endif

		float4 frame = g_tFrames.Load( int3( (int)i.vFrame.x, 0, 0 ) );
		float2 uv = lerp( frame.xy, frame.zw, i.vUv );
		float4 color = ( i.vFrame.y > 0.5 ? Tex2DS( g_tSprite, g_sLightningWrap, i.vUv ) : Tex2DS( g_tSprite, g_sClamp, uv ) ) * i.vColor;
		float fade = 1;

		// Particles that add light fade out with the fog in front of them, ones that cover what's
		// behind them are fogged like anything else.
		#if S_BLEND_MODE == 3 || S_BLEND_MODE == 6 || S_BLEND_MODE == 7
			// Halo faded added light by 1 - its fog density in gamma space; s&box's transmittance is 1 - density^2.2 (ScenarioSceneLoader.BuildFog), far too little.
			fade *= ( 1 - pow( saturate( 1 - DoAtmospherics( i.vPositionWs, i.vPositionPs.xy, float4( 0, 0, 0, 1 ), true ).a ), 1.0 / 2.2 ) );
		#endif

		// Halo blended in gamma space and the frame buffer is linear. Covering a dark background
		// with alpha a in gamma space is covering it with a^2.2 in linear space; blending with a
		// itself made snow and smoke far more solid than in Halo.
		float3 rgb = SrgbGammaToLinear( saturate( color.rgb ) );
		float coverage = pow( saturate( color.a ), 2.2 );
		#if S_BLEND_MODE == 0 && S_WEATHER
			// Weather is laid over what's behind in gamma space, exactly like Halo: a^2.2 only matches that over black, and it wiped out c10's rain streaks (alpha 0.07 at most) over the fog-lit swamp. As halo1_contrail.shader does it.
			float3 fogged = LinearToGamma( DoAtmospherics( i.vPositionWs, i.vPositionPs.xy, float4( rgb, 1 ) ).rgb );
			float3 behind = Behind( i.vPositionPs.xy );
			float alpha = saturate( color.a );
			float3 result = GammaToLinear( lerp( LinearToGamma( behind ), fogged, alpha ) );
			float3 ratio = result / max( behind, 0.0001 );
			float a = saturate( max( alpha, 1 - min( ratio.r, min( ratio.g, ratio.b ) ) ) );
			return float4( max( result - behind * ( 1 - a ), 0 ), a );
		#elif S_BLEND_MODE == 0
			rgb = DoAtmospherics( i.vPositionWs, i.vPositionPs.xy, float4( rgb, 1 ) ).rgb;
			return float4( rgb, coverage );
		#elif S_BLEND_MODE == 7
			return float4( rgb * fade, coverage );
		#elif S_BLEND_MODE == 1
			return float4( lerp( 1.0, rgb, color.a ), 1 );
		#elif S_BLEND_MODE == 2
			return float4( lerp( 0.5, 0.5 * pow( 2.0 * max( color.rgb, 0 ), 2.2 ), color.a ), 1 );
		#elif S_BLEND_MODE == 5
			return float4( lerp( 65504.0, rgb, color.a ), 1 );
		#else
			return float4( rgb * coverage * fade, 1 );
		#endif
	}
}
