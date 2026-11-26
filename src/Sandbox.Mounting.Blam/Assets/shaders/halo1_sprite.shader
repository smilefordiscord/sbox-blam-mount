//
// Halo effect sprites: the particles of effects and particle systems (muzzle flashes, impacts,
// explosions, smoke): Halo CE's drawn by HaloParticleWorld and Halo 2's by Halo2ParticleWorld, with their
// corners placed as Halo places them (Halo 2's blending each into the next frame); the path for corners
// the vertex shader places from an engine particle effect is described below.
//
// Every particle is a quad whose four corners carry the same particle; the vertex shader builds
// the sprite around it, facing the camera (turned by the particle's roll), for streaks with the
// sprite's u axis along the particle's velocity, or for "perpendicular to direction" lying flat
// across the particle's direction (turned by its roll within that plane). The sprite is anchored at its registration
// point. Each particle samples its own animation frame's texture (a bindless index), so the
// particles of one effect needn't show the same frame. Particles may be mirrored at random about
// the registration point, are drawn at least their minimum size on screen (glass shards, plasma
// bolts), and fade out below their smallest drawn size (mostly 5 to 4 pixels high).
//
// Color, Halo's particle combiners: texture x tint, all in gamma space. With a nonlinear tint each
// channel is (1 - tint) x texture^4 + tint x texture instead: a channel the tint leaves out still
// shows where the texture is bright, so the core goes white and only the fringe takes the tint.
//
// Halo blended its particles into a gamma space frame buffer. Blended in s&box's linear one, an
// additive flash over daylight came out several times dimmer and dark smoke several times
// fainter (covering by alpha a in gamma space only matches covering by a^2.2 over black). So each
// draw grabs the frame behind it, works out what Halo's blend makes of that pixel in gamma space,
// and outputs it so the hardware blend lands on exactly that over what was grabbed:
//   alpha blend:     result = lerp( behind, color, alpha ), drawn as a plain blend toward the color
//                    by the one opacity that gives the grabbed pixel that brightness (so a particle
//                    over an earlier one of the same draw is only a little off in opacity)
//   add:             result = saturate( behind + color x fade ), output the increase
//   alpha-mult add:  result = saturate( behind + color x alpha ), output the increase
//   subtract:        result = saturate( behind - color x fade ), output the decrease
// The particles of one draw land on the same grabbed pixel, so where they overlap they add up past what Halo's 8-bit frame could hold. Each draw of adds is followed by the same quads clamping what they touched at white (or what was behind, if brighter), and each of subtracts by one clamping at black.
// Component max, max( behind, color x fade ), comes out the same in either space. Multiply, double multiply and component min are done in linear space, as halo1_particles.shader does.
//
// Zsprites (F_ZSPRITE: dirt bursts, shield flashes, plasma impacts): Halo replaced each pixel's
// depth with the sprite's pulled toward the camera by its depth map (the secondary bitmap at the
// sprite's coordinates) x the particle's radius x the zsprite radius scale, so a burst bulges up
// over the ground in front of it with a noisy edge instead of a straight cut. Here each pixel is
// tested against the depth buffer at that depth. A first person weapon's view model is in front
// of everything in Halo, so pixels over it keep the sprite's own depth.
//

HEADER
{
	Description = "Halo CE effect sprite";
}

FEATURES
{
	#include "common/features.hlsl"

	// Halo's framebuffer blend function: alpha blend, multiply, double multiply, add, subtract,
	// component min, component max, alpha-multiply add. 8 and 9 are the passes that clamp what adds
	// and subtracts left at white and black, drawn over them (see the top).
	Feature( F_BLEND_MODE, 0..9( 0="Alpha Blend", 1="Multiply", 2="Double Multiply", 3="Add", 4="Subtract", 5="Component Min", 6="Component Max", 7="Alpha-Multiply Add", 8="Clamp At White", 9="Clamp At Black" ), "Blending" );
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

// s&box's Vertex, filled by ParticleSprites.
struct VertexInput
{
	// The particle's position in the world.
	float3 vPosition : POSITION < Semantic( None ); >;
	// Red: 1 for a streak (u along the velocity). Green: 1 for a sprite lying across its direction.
	// Blue: the particle's random bits (/ 255), for mirroring. Alpha: 1 for a corner already placed.
	float4 vFlags : COLOR0 < Semantic( None ); >;
	// The particle's velocity (s&box units per second) for streaks, its direction for sprites
	// lying across it.
	float3 vVelocity : NORMAL < Semantic( None ); >;
	// The corner (0..1, v from the top), the frame's texture (bindless index), roll (radians).
	float4 vCorner : TEXCOORD0 < Semantic( None ); >;
	// The particle's color in linear space (s&box's), and its opacity.
	float4 vColor : TEXCOORD1 < Semantic( None ); >;
	// The sprite's width and height (s&box units), and its registration point (fractions of its
	// size from the top left).
	float4 vSize : TANGENT < Semantic( None ); >;
};

struct PixelInput
{
	float4 vPositionPs : SV_Position;
	float2 vUv : TEXCOORD0;
	// Halo's tint in gamma space, and the opacity.
	float4 vColor : TEXCOORD1;
	float3 vPositionWs : TEXCOORD2;
	// The frame's texture, and how far (s&box units) the depth map's white reaches toward the viewer.
	float2 vSprite : TEXCOORD3;
	// Placed corners: the next frame's texture + 1 (0 for none) and how far toward it to blend (Halo 2's frame blending).
	float2 vNext : TEXCOORD4;
};

VS
{
	// A zsprite's reach as a fraction of the sprite's height: half (the radius) x the zsprite radius scale.
	float g_flZSpriteScale < Default( 0.0 ); >;

	// The particle's minimum size on screen (pixels of sprite height; 0 for none).
	float g_flHaloSpriteMinimumSize < Default( 0.0 ); >;

	// Its smallest drawn size: drawn whole from the first (pixels) and fading out to nothing at
	// the second; 0, 0 for always drawn.
	float2 g_vHaloSpriteFadeSize < Default2( 0.0, 0.0 ); >;

	// 1 where each particle is mirrored at random horizontally (x) or vertically (y).
	float2 g_vHaloSpriteMirror < Default2( 0.0, 0.0 ); >;

	// 1: drawn only in s&box's overlay layer (whose viewport depth is 0.99-1), which also draws its
	// objects in the world's layers; ParticleSprites sets it for each group.
	float g_flHaloOverlayOnly < Attribute( "HaloOverlayOnly" ); Default( 0.0 ); >;

	// Halo 2 particle systems' LOD distances from the camera (s&box units): drawn between in and out, fading over
	// the feathers (in, feather in, out, feather out; a feather of 0 cuts). Out 0: always drawn (Halo CE).
	float4 g_vHaloSpriteLod < Default4( 0.0, 0.0, 0.0, 0.0 ); >;

	float LodFade( float3 position )
	{
		if ( g_vHaloSpriteLod.z <= 0 )
			return 1;

		float d = distance( position, g_vCameraPositionWs );
		if ( d <= g_vHaloSpriteLod.x || d >= g_vHaloSpriteLod.z )
			return 0;

		float fade = 1;
		if ( g_vHaloSpriteLod.y > 0 )
			fade = min( fade, saturate( ( d - g_vHaloSpriteLod.x ) / g_vHaloSpriteLod.y ) );
		if ( g_vHaloSpriteLod.w > 0 )
			fade = min( fade, saturate( ( g_vHaloSpriteLod.z - d ) / g_vHaloSpriteLod.w ) );
		return fade;
	}

	PixelInput MainVs( VertexInput v )
	{
		PixelInput o = (PixelInput)0;
		if ( g_flHaloOverlayOnly > 0.5 && g_flViewportMinZ <= 0.5 )
		{
			o.vPositionPs = float4( 2, 2, 2, 1 );
			return o;
		}

		// Halo CE's particles (HaloParticleWorld) come with their corners placed, Halo's color in
		// gamma space and a zsprite's reach (s&box units) in the size's x.
		if ( v.vFlags.a > 0.5 )
		{
			o.vPositionPs = Position3WsToPs( v.vPosition );
			o.vPositionWs = v.vPosition;
			o.vUv = v.vCorner.xy;
			o.vColor = v.vColor;
			o.vSprite = float2( v.vCorner.z, v.vSize.x );
			o.vNext = v.vVelocity.xy;
			return o;
		}

		float2 corner = v.vCorner.xy;
		float2 size = v.vSize.xy;
		float2 origin = v.vSize.zw;
		float2 uv = corner;

		// Mirrored about the registration point, by the particle's random bits.
		uint bits = (uint)round( v.vFlags.b * 255.0 );
		if ( g_vHaloSpriteMirror.x > 0.5 && ( bits & 1 ) != 0 )
		{
			uv.x = 1 - uv.x;
			origin.x = 1 - origin.x;
		}
		if ( g_vHaloSpriteMirror.y > 0.5 && ( bits & 2 ) != 0 )
		{
			uv.y = 1 - uv.y;
			origin.y = 1 - origin.y;
		}

		// Its height in pixels: raised to the minimum size, then faded out below the smallest
		// drawn size.
		float sizeFade = 1;
		float4 center = Position3WsToPs( v.vPosition );
		if ( center.w > 0.0001 )
		{
			float4 top = Position3WsToPs( v.vPosition + g_vCameraUpDirWs * size.y );
			float pixels = top.w > 0.0001 ? length( ( top.xy / top.w - center.xy / center.w ) * 0.5 / g_vInvViewportSize.xy ) : 0;
			if ( pixels > 0 && pixels < g_flHaloSpriteMinimumSize )
			{
				size *= g_flHaloSpriteMinimumSize / pixels;
				pixels = g_flHaloSpriteMinimumSize;
			}

			float fadeStart = g_vHaloSpriteFadeSize.x;
			float fadeEnd = g_vHaloSpriteFadeSize.y;
			if ( fadeStart > fadeEnd )
				sizeFade = saturate( ( pixels - fadeEnd ) / ( fadeStart - fadeEnd ) );
			else if ( fadeEnd > 0 )
				sizeFade = pixels >= fadeEnd ? 1 : 0;
		}

		// Right and up from the registration point.
		float2 offset = float2( corner.x - origin.x, origin.y - corner.y ) * size;

		float3 right = normalize( cross( g_vCameraDirWs, g_vCameraUpDirWs ) );
		float3 up = normalize( cross( right, g_vCameraDirWs ) );
		float speed = length( v.vVelocity );
		if ( v.vFlags.r > 0.5 && speed > 0.001 )
		{
			// Halo's streak sprites run along the bitmap's u axis, turned about it to face the camera.
			float3 axis = v.vVelocity / speed;
			float3 side = cross( axis, g_vCameraPositionWs - v.vPosition );
			side = length( side ) > 0.0001 ? normalize( side ) : normalize( cross( axis, float3( 0.3, 0.2, 0.9 ) ) );
			right = axis;
			up = side;
		}
		else
		{
			if ( v.vFlags.g > 0.5 && speed > 0.000001 )
			{
				// Flat in the plane across the direction, upright where it can be.
				float3 axis = v.vVelocity / speed;
				float3 reference = abs( axis.z ) > 0.99 ? float3( 1, 0, 0 ) : float3( 0, 0, 1 );
				right = normalize( cross( axis, reference ) );
				up = cross( right, axis );
			}

			float s = sin( v.vCorner.w );
			float c = cos( v.vCorner.w );
			offset = float2( offset.x * c - offset.y * s, offset.x * s + offset.y * c );
		}

		float3 positionWs = v.vPosition + right * offset.x + up * offset.y;
		o.vPositionPs = Position3WsToPs( positionWs );
		o.vPositionWs = positionWs;
		o.vUv = uv;

		// The effect builder's tints are Halo's raised to 2.2; back to Halo's gamma space.
		o.vColor = float4( pow( max( v.vColor.rgb, 0 ), 1.0 / 2.2 ), v.vColor.a * sizeFade * LodFade( v.vPosition ) );
		o.vSprite = float2( v.vCorner.z, size.y * g_flZSpriteScale );
		return o;
	}
}

PS
{
	#include "common/pixel.hlsl"

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

	#if S_BLEND_MODE == 0
		// Premultiplied (see the top).
		RenderState( SrcBlend, ONE );
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
	#elif S_BLEND_MODE == 4
		RenderState( SrcBlend, ONE );
		RenderState( DstBlend, ONE );
		RenderState( BlendOp, REV_SUBTRACT );
	#elif S_BLEND_MODE == 5
		RenderState( SrcBlend, ONE );
		RenderState( DstBlend, ONE );
		RenderState( BlendOp, MIN );
	#elif S_BLEND_MODE == 6 || S_BLEND_MODE == 9
		RenderState( SrcBlend, ONE );
		RenderState( DstBlend, ONE );
		RenderState( BlendOp, MAX );
	#elif S_BLEND_MODE == 8
		RenderState( SrcBlend, ONE );
		RenderState( DstBlend, ONE );
		RenderState( BlendOp, MIN );
	#else
		// Add and alpha-multiply add: the increase over what's behind (see the top).
		RenderState( SrcBlend, ONE );
		RenderState( DstBlend, ONE );
		RenderState( BlendOp, ADD );
	#endif

	SamplerState g_sSprite < Filter( Bilinear ); AddressU( CLAMP ); AddressV( CLAMP ); >;
	SamplerState g_sBehind < Filter( Point ); AddressU( CLAMP ); AddressV( CLAMP ); >;
	SamplerState g_sZMap < Filter( Bilinear ); AddressU( WRAP ); AddressV( WRAP ); >;

	// 1: Halo's nonlinear tint (see the top); Halo 2's additive tint particles take a fraction of it.
	float g_flNonlinearTint < Default( 0.0 ); >;

	// The frame behind the sprites, grabbed by ParticleSprites before drawing them.
	Texture2D g_tHaloSpriteBehind < Attribute( "HaloSpriteBehind" ); SrgbRead( false ); >;

	// A zsprite's depth map, in its red channel.
	CreateInputTexture2D( ZMap, Linear, 8, "None", "_color", ",0/,0/0", Default4( 0.00, 0.00, 0.00, 1.00 ) );
	Texture2D g_tZMap < Channel( RGBA, Box( ZMap ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( false ); >;

	// Nearer than this (s&box units, a world unit) a pixel is taken to be a first person view model.
	static const float FirstPersonDepth = 103.2;

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

	// What adding (direction 1) or subtracting (-1) a gamma space color over what's behind changes in linear space, as Halo's gamma space blend would.
	// It's the color itself, scaled so its brightness changes as Halo's would: the per channel difference against the grabbed frame would swap hues wherever the grab isn't the frame (an earlier smoke particle darkening it).
	float3 GammaChange( float3 color, float3 behind, float direction )
	{
		float3 luma = float3( 0.2126, 0.7152, 0.0722 );
		float3 linearColor = GammaToLinear( color );
		float amount = dot( luma, linearColor );
		if ( amount <= 1e-6 )
			return 0;

		float before = dot( luma, behind );
		float after = GammaToLinear( saturate( LinearToGamma( before ) + direction * dot( luma, color ) ) ).x;
		return linearColor * ( abs( after - before ) / amount );
	}

	float3 Behind( float2 pixel )
	{
		float2 uv = ( pixel - g_vViewportOffset ) * g_vInvViewportSize;
		return saturate( g_tHaloSpriteBehind.SampleLevel( g_sBehind, uv, 0 ).rgb );
	}

	// s&box keeps reverse-Z depth 0-0.02 for the 3D skybox; the world is drawn over 0.02-1.
	static const float WorldMinZ = 0.02;

	// Where the depth buffer's surface at a pixel is in the world. A game can draw first person
	// particles in s&box's overlay layer, whose viewport depth is 0.99-1 while the depth buffer
	// still holds the world (and the view model, prepassed at its real depth) over 0.02-1.
	float3 SceneWorldPosition( float2 pixel )
	{
		if ( g_flViewportMinZ <= 0.5 )
			return Depth::GetWorldPosition( pixel );

		float3 ray = float3( pixel * g_vInvViewportSize, RemapValClamped( Depth::Get( pixel ), WorldMinZ, 1.0, 0.0, 1.0 ) );
		ray.y = 1.0 - ray.y;
		ray.xy = 2.0 * ray.xy - 1.0;
		float4 world = mul( g_matProjectionToWorld, float4( ray, 1.0 ) );
		return world.xyz / max( world.w, 1e-10 ) + g_vCameraPositionWs;
	}

	float4 MainPs( PixelInput i ) : SV_Target0
	{
		float2 uv = i.vUv;

		#if S_ZSPRITE
		{
			float3 eye = g_vCameraPositionWs;
			float spriteDistance = distance( i.vPositionWs, eye );
			float sceneDistance = distance( SceneWorldPosition( i.vPositionPs.xy ), eye );
			float pulled = spriteDistance - Tex2DS( g_tZMap, g_sZMap, uv ).r * i.vSprite.y;
			if ( ( sceneDistance < FirstPersonDepth ? spriteDistance : pulled ) > sceneDistance )
				discard;
		}
		#endif

		// Halo's bitmaps are in gamma space: the texture's own values, not decoded.
		float4 map = Bindless::GetTexture2D( (int)round( i.vSprite.x ) ).Sample( g_sSprite, uv );
		if ( i.vNext.x > 0.5 )
			map = lerp( map, Bindless::GetTexture2D( (int)round( i.vNext.x ) - 1 ).Sample( g_sSprite, uv ), saturate( i.vNext.y ) );
		float3 tint = saturate( i.vColor.rgb );
		// Halo 2's additive tint template takes part of the nonlinear tint (1 - its modulation factor).
		float3 map2 = map.rgb * map.rgb;
		float3 nonlinear = saturate( ( 1 - tint ) * map2 * map2 + tint * map.rgb );
		float3 color = saturate( lerp( map.rgb * tint, nonlinear, saturate( g_flNonlinearTint ) ) );

		// The effect builder leaves the opacity of particles Halo doesn't take the tint's alpha for
		// at their fade alone; additive ones don't use the texture's alpha.
		float opacity = saturate( i.vColor.a );
		float coverage = opacity * map.a;

		#if S_BLEND_MODE == 0
			// Fogged like anything else, then laid over what's behind in gamma space.
			float3 linearColor = DoAtmospherics( i.vPositionWs, i.vPositionPs.xy, float4( GammaToLinear( color ), 1 ) ).rgb;
			float3 behind = Behind( i.vPositionPs.xy );
			float3 result = GammaToLinear( lerp( LinearToGamma( behind ), LinearToGamma( linearColor ), coverage ) );

			// A plain blend toward the color, by the one opacity A that best fits the gamma blend:
			// lerp( behind, color, A ) ~ result, least squares over the channels weighted by luma.
			// That's an average of each channel's own opacity, so it stays between them when the
			// color and what's behind differ in hue but not brightness (brown smoke over a dark
			// blue sky), where a ratio of brightnesses jumps between 0 and 1. The grab only
			// decides A, so where the frame under a particle isn't the grab (an earlier particle
			// of the same draw over it) the particle is a little more or less opaque than Halo's,
			// never a color the two don't make.
			static const float3 Luma = float3( 0.2126, 0.7152, 0.0722 );
			float3 toColor = linearColor - behind;
			float spread = dot( Luma * toColor, toColor );
			float a = spread > 1e-6 ? saturate( dot( Luma * toColor, result - behind ) / spread ) : coverage;
			return float4( linearColor * a, a );
		#else
			// Particles that add light fade out with the fog in front of them.
			// Halo faded added light by 1 - its fog density in gamma space; s&box's transmittance is 1 - density^2.2 (ScenarioSceneLoader.BuildFog), far too little.
			float fade = ( 1 - pow( saturate( 1 - DoAtmospherics( i.vPositionWs, i.vPositionPs.xy, float4( 0, 0, 0, 1 ), true ).a ), 1.0 / 2.2 ) );

			#if S_BLEND_MODE == 3 || S_BLEND_MODE == 7
				float3 added = color * ( S_BLEND_MODE == 7 ? coverage : opacity ) * fade;
				return float4( GammaChange( added, Behind( i.vPositionPs.xy ), 1 ), 0 );
			#elif S_BLEND_MODE == 4
				return float4( GammaChange( color * opacity * fade, Behind( i.vPositionPs.xy ), -1 ), 0 );
			#elif S_BLEND_MODE == 8 || S_BLEND_MODE == 9
				// Only where the sprite added or took something away.
				if ( max( max( color.r, color.g ), color.b ) * opacity * fade < 1.0 / 1024.0 )
					discard;

				#if S_BLEND_MODE == 8
					// At most white, or as bright as the brightest channel behind where that's brighter (an HDR sky), the same for every channel so the hue can't shift.
					float2 behindUv = ( i.vPositionPs.xy - g_vViewportOffset ) * g_vInvViewportSize;
					float3 raw = g_tHaloSpriteBehind.SampleLevel( g_sBehind, behindUv, 0 ).rgb;
					return float4( (float3)max( 1.0, max( raw.r, max( raw.g, raw.b ) ) ), 1 );
				#else
					return float4( 0, 0, 0, 1 );
				#endif
			#elif S_BLEND_MODE == 1
				return float4( lerp( 1.0, GammaToLinear( color ), coverage ), 1 );
			#elif S_BLEND_MODE == 2
				return float4( lerp( 0.5, 0.5 * pow( 2.0 * color, 2.2 ), coverage ), 1 );
			#elif S_BLEND_MODE == 5
				return float4( lerp( 65504.0, GammaToLinear( color ), coverage ), 1 );
			#elif S_BLEND_MODE == 6
				// The brighter of the two in gamma space is the brighter in linear space too.
				return float4( GammaToLinear( color * opacity * fade ), 1 );
			#else
				return float4( GammaToLinear( color ) * opacity * fade, 1 );
			#endif
		#endif
	}
}
