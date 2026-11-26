//
// Halo CE lens flares: the glows on lights and the sun, and the reflections strung out from them
// across the screen.
//
// Every sprite of every flare is a quad whose four corners all carry the flare's position; the
// vertex shader projects that point and builds the sprite around it on screen. Reflections sit
// on the line from the light through the screen center.
//
// Like Halo's, sprites aren't depth tested: a whole flare fades by how much of its occlusion test
// is visible. Halo tests a square the size of the flare's occlusion radius on screen, at the
// light moved by that radius toward the viewer (or along the marker's forward, or not at all),
// against the depth buffer, and multiplies every sprite's brightness by the visible fraction;
// sprites flagged "radius scaled by occlusion" also shrink to (1 + fraction) / 2 of their size.
// Here each pixel samples that square 4 x 4 (Halo also eased the fraction over frames, a quarter
// of the way up or half the way down per frame, which needs state a mounted resource doesn't
// have). The sun's test is the same, with the sun 1023.875 world units away as Halo puts it; it's
// drawn far off in its direction here, so anything in front of the sky hides it.
//
// Halo added flares in its gamma space frame buffer, where a flare over a bright sky clips to
// white (b30's sun is a white blot). Added in linear space the same flare only brightened the sky
// a little. So each sprite reads what's behind it from the frame buffer copy, adds itself in gamma
// space, and outputs the increase, still blended additively so sprites don't overwrite each other.
//
// Suns (flares with occlusion radius 50, or the sun flag) also get Halo's sun glow, one more sprite:
// a cone 64 pixels across (at 480 lines) stacked 16 times, each copy 5 pixels bigger and 1/(n+1)
// as bright. It adds up past white near the middle, which is the sun's crisp white disc. The Xbox
// game also faded it out 45 degrees off the middle of the view; MCC doesn't (the sun is just as
// big in a corner of the screen), and its disc is about 35 pixels across at 720 lines, which a
// constant brightness of 0.45 matches.
//
// Positions are in the model's space, so a flare can ride on an object (a weapon's charge glow):
// a level's flare model sits at the origin. Halo interpolates every sprite's radius and brightness,
// and the light's color, by the light's scale (its object function value), so each sprite carries
// both ends and the scale picks between them: the sprite's own scale times the renderer's tint
// (red), or the renderer attribute HaloFunctionScale (0-1) where a game sets it. A flare whose
// light is black at that scale (a charge glow at no charge) isn't drawn.
//
// A first person weapon's flares: Halo draws the view model in a depth range of its own in front
// of everything, writing the stencil. So the view model hides a flare whose test point is anywhere
// behind it on screen (the plasma pistol's muzzle glows and the plasma rifle's heat flares at
// idle, BlamInspect fpmarkers), and a reflection flagged "occluded by solid objects" (the plasma
// pistol's charge glow) is never drawn over it. Here the view model is whatever is nearer than a
// little behind the light (FirstPersonViewModelDepth): it counts as hidden in the occlusion test,
// and flagged sprites skip those pixels. Unflagged sprites of a visible flare draw across the gun,
// as in Halo. A light counts as on a first person weapon when the renderer attribute
// HaloFirstPerson is 1, or, while that's unset, when it's within one world unit of the camera
// (Halo's view models are within about 0.3).
//
// Sprite color, Halo's widget combiners: color x map + tint factor x (1 - color) x map^6, times
// the brightness. The 6th power keeps the white core small: a soft flare stays its color but for
// the middle.
//

HEADER
{
	Description = "Halo CE lens flare";
}

FEATURES
{
	#include "common/features.hlsl"
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
	// The light's position in the world, or for the sun its direction.
	float3 vPositionOs : POSITION < Semantic( PosXyz ); >;
	// Corner (-1..1), position along the reflection line (0 on the light, 1 opposite), rotation (radians).
	float4 vCorner : TEXCOORD0 < Semantic( None ); >;
	// Radius and brightness at scale 0, flags, occlusion radius. Flags: 1 sun, 2 radius not scaled
	// by distance, 4 x this sprite's brightness scale function, 16 x the first sprite's (it scales
	// every later one), 64 for the sun glow, 128 the light is black at scale 0, 256 black at scale 1,
	// 512 occluded by solid objects (not drawn over a first person view model), 1024 radius scaled
	// by occlusion, 4096 x the occlusion offset direction (0 toward the viewer, 1 marker forward, 2 none),
	// 16384 the sprite takes the light's color (an untinted reflection).
	float4 vSprite : TEXCOORD1 < Semantic( None ); >;
	// The direction the light faces (zero: all directions), and corona stretch on x (y is 1/x).
	float4 vAxis : TEXCOORD2 < Semantic( None ); >;
	// Cosines of the falloff and cutoff angles, near and far fade distances (0 far: no fading).
	float4 vFade : TEXCOORD3 < Semantic( None ); >;
	// Color at scale 1, and how much it tints rather than modulates (-1: the sun glow).
	float4 vColor : TEXCOORD4 < Semantic( None ); >;
	// Radius and brightness at scale 1, the first sprite's brightness at scale 0 and 1 (1 for the first).
	float4 vBounds : TEXCOORD5 < Semantic( None ); >;
	// Color at scale 0, and the sprite's scale.
	float4 vLight : TEXCOORD6 < Semantic( None ); >;

	uint nInstanceTransformID : TEXCOORD13 < Semantic( InstanceTransformUv ); >;
};

struct PixelInput
{
	float4 vPositionPs : SV_Position;
	float2 vUv : TEXCOORD0;
	float4 vColor : TEXCOORD1;
	// This corner's and the occlusion test point's position on screen (normalized device coordinates).
	float4 vNdc : TEXCOORD2;
	// The occlusion test point's projected depth, the test square's half size in pixels, and the
	// projected depth nearer than which a first person weapon's view model is (-1 for other lights).
	float3 vOcclusion : TEXCOORD3;
	// The sprite's brightness (applied after tinting, so a tinted sprite's white core dims too).
	float flBrightness : TEXCOORD4;
	// 1 if the radius shrinks with occlusion; 1 if the sprite isn't drawn over the view model.
	float2 vFlags : TEXCOORD5;
};

VS
{
	// GetTransformMatrix and the per-instance tint.
	#include "instancing.fxc"

	// A lens flare brightness scale function: 0 none, 1 light direction (the camera looking
	// against the light), 2 light to camera (the light facing the camera), 3 camera direction (the
	// light near the middle of the view). cosines is (falloff, cutoff): 1 at the falloff angle, 0 at
	// the cutoff. Lights that face no particular way aren't faded by their direction.
	float ScaleFunction( float function, float3 direction, float3 eyeToLight, float2 cosines )
	{
		if ( function < 0.5 || cosines.x - cosines.y < 0.0001 )
			return 1;

		bool directional = dot( direction, direction ) > 0.5;
		float x;
		if ( function < 1.5 )
		{
			if ( !directional )
				return 1;
			x = -dot( g_vCameraDirWs, direction );
		}
		else if ( function < 2.5 )
		{
			if ( !directional )
				return 1;
			x = -dot( direction, eyeToLight );
		}
		else
			x = dot( g_vCameraDirWs, eyeToLight );

		return saturate( ( x - cosines.y ) / ( cosines.x - cosines.y ) );
	}

	// Where Halo's occlusion test point is from the light: the occlusion radius toward the viewer
	// (along the camera's forward), 1.41 x it along the marker's forward, or on the light.
	float3 OcclusionOffset( float offsetDirection, float3 forward, float radius )
	{
		if ( offsetDirection < 0.5 )
			return -g_vCameraDirWs * radius;
		if ( offsetDirection < 1.5 )
			return forward * radius * 1.41421356;
		return float3( 0, 0, 0 );
	}

	// s&box units in a world unit.
	static const float WorldUnit = 103.2;

	// How far from the camera Halo puts the sun (world units).
	static const float SunDistance = 1023.875;

	// The sun glow's biggest copy reaches this far from the sun, in pixels at 480 lines.
	static const float SunGlowExtent = 32 + 15 * 5 - 4;
	static const float SunGlowBrightness = 0.45;

	// The light's scale where a game drives it (see the top); below 0 (unset) it's the sprite's
	// scale times the renderer's tint.
	float g_flHaloFunctionScale < Attribute( "HaloFunctionScale" ); Default( -1.0 ); >;

	// The light's color (Halo's 0-1) where a game works it out itself, as with an object's change
	// color multiplied in: it replaces the color the mesh carries on the sprites that take the
	// light's color, and black draws no flare. Below 0 (unset) the mesh's color stands.
	float3 g_vHaloLightColor < Attribute( "HaloLightColor" ); Default3( -1.0, -1.0, -1.0 ); >;

	// 1: the flare is on a first person weapon, 0: it isn't; below 0 (unset): lights within
	// FirstPersonDistance (one world unit, in s&box units) of the camera are.
	float g_flHaloFirstPerson < Attribute( "HaloFirstPerson" ); Default( -1.0 ); >;
	static const float FirstPersonDistance = WorldUnit;

	// How far behind a first person weapon's light (s&box units, 0.1 world units) the view model is
	// taken to reach: Halo's view model is in front of everything, so it occludes a flare even
	// where it's behind the light (the plasma rifle's heat flares sit just in front of its body).
	static const float FirstPersonViewModelDepth = 10.32;

	// How far in front of the occlusion test point (s&box units) the depth buffer is ignored. 0 is
	// Halo's test. For a game whose camera looks at a weapon from behind (a third person view of
	// the player's own gun), where the gun stands in front of its own flares' test points.
	float g_flHaloOccluderMargin < Attribute( "HaloOccluderMargin" ); Default( 0.0 ); >;

	// 1: drawn only in s&box's overlay layer (whose viewport depth is 0.99-1), which also draws its
	// objects in the world's layers. For a game that draws a first person weapon there.
	float g_flHaloOverlayOnly < Attribute( "HaloOverlayOnly" ); Default( 0.0 ); >;

	PixelInput MainVs( VertexInput v )
	{
		PixelInput o;
		o.vUv = v.vCorner.xy * float2( 0.5, -0.5 ) + 0.5;
		o.vColor = 0;
		o.vNdc = 0;
		o.vOcclusion = 0;
		o.vFlags = 0;
		o.flBrightness = 0;
		o.vPositionPs = float4( 2, 2, 2, 1 );
		if ( g_flHaloOverlayOnly > 0.5 && g_flViewportMinZ <= 0.5 )
			return o;

		float flags = v.vSprite.z;
		bool sun = fmod( flags, 2 ) >= 1;
		bool fixedSize = fmod( floor( flags / 2 ), 2 ) >= 1;
		bool glow = fmod( floor( flags / 64 ), 2 ) >= 1;

		float scale = g_flHaloFunctionScale >= 0
			? saturate( g_flHaloFunctionScale )
			: saturate( v.vLight.w * GetExtraPerInstanceShaderData( v.nInstanceTransformID ).vTint.r );
		if ( ( fmod( floor( flags / 128 ), 2 ) >= 1 && scale <= 0 ) || ( fmod( floor( flags / 256 ), 2 ) >= 1 && scale >= 1 ) )
			return o;

		float3 lightColor = lerp( v.vLight.rgb, v.vColor.rgb, scale );
		if ( g_vHaloLightColor.x >= 0 )
		{
			if ( dot( g_vHaloLightColor, 1 ) <= 0 )
				return o;
			if ( fmod( floor( flags / 16384 ), 2 ) >= 1 )
				lightColor = saturate( g_vHaloLightColor );
		}

		float spriteRadius = lerp( v.vSprite.x, v.vBounds.x, scale );
		float spriteBrightness = lerp( v.vSprite.y, v.vBounds.y, scale ) * lerp( v.vBounds.z, v.vBounds.w, scale );
		if ( !glow && ( spriteRadius <= 0 || spriteBrightness <= 0 ) )
			return o;

		// The sun is drawn far away in its direction, inside the far plane so it isn't clipped.
		float3x4 matObjectToWorld = GetTransformMatrix( v.nInstanceTransformID );
		float3 lightWs = sun ? g_vCameraPositionWs + normalize( v.vPositionOs ) * g_flFarPlane * 0.5 : mul( matObjectToWorld, float4( v.vPositionOs, 1 ) ).xyz;
		float3 axisWs = mul( (float3x3)matObjectToWorld, v.vAxis.xyz );
		axisWs = dot( axisWs, axisWs ) > 1e-8 ? normalize( axisWs ) : float3( 0, 0, 0 );
		float3 toCamera = g_vCameraPositionWs - lightWs;
		float lightDistance = length( toCamera );
		float depth = dot( -toCamera, g_vCameraDirWs );
		if ( depth <= g_flNearPlane )
			return o;

		// Halo scales each sprite's brightness by one of a few angle functions, fading between the
		// falloff and cutoff angles, and every sprite after the first by the first one's too
		// (its brightness bounds are interpolated above).
		float3 eyeToLight = -toCamera / max( lightDistance, 0.001 );
		float3 direction = sun ? -normalize( v.vPositionOs ) : axisWs;
		float fade = ScaleFunction( fmod( floor( flags / 4 ), 4 ), direction, eyeToLight, v.vFade.xy ) *
			ScaleFunction( fmod( floor( flags / 16 ), 4 ), direction, eyeToLight, v.vFade.xy );

		if ( !sun && v.vFade.w > v.vFade.z )
			fade *= saturate( ( v.vFade.w - depth ) / ( v.vFade.w - v.vFade.z ) );

		if ( fade <= 0 )
			return o;

		float4 lightPs = Position3WsToPs( lightWs );
		float2 lightNdc = lightPs.xy / lightPs.w;

		// Reflections along the line from the light through the screen center.
		float2 centerNdc = lightNdc * ( 1 - 2 * v.vCorner.z );

		// Radius in world units at the light, or as if the light were one world unit away. The mesh
		// carries it in s&box units.
		float focal = g_matViewToProjection[1][1];
		float radiusNdc = glow ? SunGlowExtent / 240 : spriteRadius * focal / ( fixedSize ? WorldUnit : depth );

		float stretch = max( v.vAxis.w, 0.0001 );
		float2 corner = v.vCorner.xy * float2( stretch, 1 / stretch );
		float s = sin( v.vCorner.w );
		float c = cos( v.vCorner.w );
		corner = float2( corner.x * c - corner.y * s, corner.x * s + corner.y * c );

		float aspect = g_vViewportSize.y / g_vViewportSize.x;
		float2 ndc = centerNdc + corner * radiusNdc * float2( aspect, 1 );

		o.vColor = float4( lightColor, v.vColor.a );
		o.flBrightness = spriteBrightness * fade;

		if ( glow )
		{
			// Offset from the sun in pixels at 480 lines.
			o.vUv = v.vCorner.xy * SunGlowExtent;
			o.flBrightness = SunGlowBrightness;
		}

		// Not depth tested (see the top): the vertex depth only has to be on screen.
		o.vPositionPs = float4( ndc, saturate( lightPs.z / lightPs.w ), 1 );
		o.vFlags = float2( fmod( floor( flags / 1024 ), 2 ), 0 );

		// Halo's occlusion test point (see OcclusionOffset), tested over a square of the occlusion
		// radius on screen (at least a pixel either way).
		float offsetDirection = fmod( floor( flags / 4096 ), 4 );

		if ( sun )
		{
			// Worked out with the sun SunDistance world units away, its marker facing the camera, and
			// drawn far off in that direction, so anything in front of the sky hides it.
			float sunOcclusionRadius = v.vSprite.w / WorldUnit;
			float3 sunTest = -direction * SunDistance + OcclusionOffset( offsetDirection, direction, sunOcclusionRadius );
			float sunTestDistance = dot( sunTest, g_vCameraDirWs );
			if ( sunTestDistance <= 0 )
				return o;

			float4 sunTestPs = Position3WsToPs( g_vCameraPositionWs + normalize( sunTest ) * g_flFarPlane * 0.5 );
			o.vNdc = float4( ndc, sunTestPs.xy / sunTestPs.w );
			o.vOcclusion = float3( sunTestPs.z / sunTestPs.w, max( 1, sunOcclusionRadius * focal / sunTestDistance * 0.5 * g_vViewportSize.y ), -1 );
			return o;
		}

		float3 testWs = lightWs + OcclusionOffset( offsetDirection, axisWs, v.vSprite.w );
		float testDistance = dot( testWs - g_vCameraPositionWs, g_vCameraDirWs );
		if ( testDistance <= g_flNearPlane )
			return o;

		float4 testPs = Position3WsToPs( testWs );
		float testPixels = max( 1, v.vSprite.w * focal / testDistance * 0.5 * g_vViewportSize.y );

		// What's within the occluder margin in front of the test point doesn't hide it: the depth
		// buffer is compared with the point pulled that far toward the camera.
		float margin = max( 0, min( g_flHaloOccluderMargin, testDistance - g_flNearPlane - 1 ) );
		float4 occluderPs = Position3WsToPs( testWs - g_vCameraDirWs * margin );

		// On a first person weapon, the view model stands in for Halo's stencil: what's nearer than
		// a little behind the light (FirstPersonViewModelDepth) is taken as the gun.
		bool firstPerson = g_flHaloFirstPerson >= 0 ? g_flHaloFirstPerson > 0.5 : depth < FirstPersonDistance;
		float4 viewModelPs = Position3WsToPs( lightWs + g_vCameraDirWs * FirstPersonViewModelDepth );
		bool solid = fmod( floor( flags / 512 ), 2 ) >= 1;

		o.vNdc = float4( ndc, testPs.xy / testPs.w );
		o.vOcclusion = float3( occluderPs.z / occluderPs.w, testPixels, firstPerson ? viewModelPs.z / viewModelPs.w : -1 );
		o.vFlags.y = firstPerson && solid ? 1 : 0;
		return o;
	}
}

PS
{
	#include "common/pixel.hlsl"

	RenderState( CullMode, NONE );
	RenderState( DepthEnable, false );
	RenderState( DepthWriteEnable, false );
	RenderState( BlendEnable, true );
	RenderState( SrcBlend, ONE );
	RenderState( DstBlend, ONE );
	RenderState( BlendOp, ADD );
	RenderState( SrcBlendAlpha, ZERO );
	RenderState( DstBlendAlpha, ONE );
	BoolAttribute( bWantsFBCopyTexture, true );

	SamplerState g_sClamp < Filter( Bilinear ); AddressU( CLAMP ); AddressV( CLAMP ); >;
	Texture2D g_tFrameBufferCopyTexture < Attribute( "FrameBufferCopyTexture" ); SrgbRead( false ); >;

	CreateInputTexture2D( Flare, Linear, 8, "None", "_color", ",0/,0/0", Default4( 1.00, 1.00, 1.00, 1.00 ) );
	Texture2D g_tFlare < Channel( RGBA, Box( Flare ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( false ); >;

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

	// The sun glow at this many pixels (at 480 lines) from the sun: the glow cone (32 pixels in
	// radius) stacked 16 times, copy n 5n - 4 pixels bigger and 1/(n+1) as bright. Its color runs
	// from Halo's pale yellow at the edge to white where it piles up.
	float3 SunGlow( float distance )
	{
		float sum = 0;
		[unroll] for ( int n = 0; n < 16; n++ )
			sum += saturate( 1 - distance / ( 28 + 5 * n ) ) / ( n + 1 );
		return lerp( float3( 0.69, 0.69, 0.5 ), 1, saturate( sum ) ) * sum;
	}

	// s&box keeps reverse-Z depth 0-0.02 for the 3D skybox; the world is drawn over 0.02-1.
	static const float WorldMinZ = 0.02;

	// The depth buffer at a pixel, normalized (0 far, 1 near) to compare with projected z / w. A
	// game can draw a first person weapon and its flares in s&box's overlay layer, whose viewport
	// depth is 0.99-1 while the depth buffer still holds the world (and the weapon, prepassed at
	// its real depth) over 0.02-1: normalizing to the viewport there would put everything at 0.
	float SceneDepth( float2 pixel )
	{
		if ( g_flViewportMinZ > 0.5 )
			return RemapValClamped( Depth::Get( pixel ), WorldMinZ, 1.0, 0.0, 1.0 );
		return Depth::GetNormalized( pixel );
	}

	// Whether a pixel shows a first person weapon's view model (Halo's stencil): nearer than the
	// depth the VS worked out (reversed depth: larger is nearer). Never for other lights.
	bool ViewModel( PixelInput i, float2 pixel )
	{
		return i.vOcclusion.z >= 0 && SceneDepth( pixel ) > i.vOcclusion.z;
	}

	// Halo's occlusion test: the fraction of a square around the test point (half size in pixels)
	// where nothing is nearer than the point (reversed depth: larger is nearer), sampled 4 x 4. Off
	// screen counts as hidden, as the part of Halo's test square off screen draws no pixels. On a
	// first person weapon the view model hides it wherever it is, being in front of everything in
	// Halo.
	float Occlusion( PixelInput i, float2 testPixels )
	{
		float visible = 0;
		[unroll] for ( int y = 0; y < 4; y++ )
		{
			[unroll] for ( int x = 0; x < 4; x++ )
			{
				float2 pixel = testPixels + ( float2( x, y ) * 0.5 - 0.75 ) * i.vOcclusion.y;
				bool onScreen = all( pixel >= g_vViewportOffset ) && all( pixel < g_vViewportOffset + g_vViewportSize );
				if ( onScreen && SceneDepth( pixel ) <= i.vOcclusion.x && !ViewModel( i, pixel ) )
					visible += 1;
			}
		}

		return visible / 16;
	}

	float4 MainPs( PixelInput i ) : SV_Target0
	{
		// Where the occlusion test point is in pixels, from how this pixel's screen position
		// relates to its device coordinates. Doesn't depend on which way up the projection is.
		float2 pixelsPerNdc = float2( 1 / ddx( i.vNdc.x ), 1 / ddy( i.vNdc.y ) );
		float2 testPixels = i.vPositionPs.xy + ( i.vNdc.zw - i.vNdc.xy ) * pixelsPerNdc;

		float visibility = Occlusion( i, testPixels );
		if ( visibility <= 0 )
			discard;

		// Not drawn over a first person weapon's view model, for sprites "occluded by solid objects".
		if ( i.vFlags.y > 0.5 && ViewModel( i, i.vPositionPs.xy ) )
			discard;

		float3 added;
		if ( i.vColor.a < -0.5 )
		{
			added = SunGlow( length( i.vUv ) ) * i.flBrightness;
		}
		else
		{
			// "Radius scaled by occlusion": Halo draws the sprite (1 + visibility) / 2 as big.
			float2 uv = i.vUv;
			if ( i.vFlags.x > 0.5 )
			{
				uv = 0.5 + ( uv - 0.5 ) * 2 / ( 1 + visibility );
				if ( any( uv < 0 ) || any( uv > 1 ) )
					discard;
			}

			// Flare bitmaps are in gamma space like everything in Halo.
			float3 map = Tex2DS( g_tFlare, g_sClamp, uv ).rgb;

			// Halo's sprite combiners (see the top): the color, plus white toward the core by the
			// tint factor (1 for the light's color, the tint's alpha otherwise).
			float3 map2 = map * map;
			added = ( i.vColor.rgb * map + i.vColor.a * ( 1 - i.vColor.rgb ) * map2 * map2 * map2 ) * i.flBrightness;
		}
		added = saturate( added ) * visibility;

		// Added to what's behind in gamma space (see the top). Overlapping sprites each add to the
		// same background, which is close enough until they clip to white anyway.
		// In s&box's overlay layer the engine's copy predates the world's transparents and the first
		// person weapon, and covers only part of the screen. There a flare adds itself as over
		// black: the same where what's behind is dark, a little less over bright sky.
		float2 screenUv = i.vPositionPs.xy * g_vInvViewportSize;
		float3 behind = g_flViewportMinZ > 0.5
			? 0
			: saturate( g_tFrameBufferCopyTexture.SampleLevel( g_sClamp, screenUv * g_vFrameBufferCopyInvSizeAndUvScale.zw, 0 ).rgb );
		float3 sum = GammaToLinear( saturate( LinearToGamma( behind ) + added ) );
		return float4( max( sum - behind, 0 ), 0 );
	}
}
