//
// Halo 2 lens flares (lens): the glows on lights, vehicles and weapons, and the sun, with their reflections strung
// out across the screen. As Halo 2 draws them:
// - reflection i sits on the line from the flare through the screen centre: screen = centre + (1 - 2 position) x
//   (flare - centre), at the flare's depth;
// - its radius is world units at that depth, or a constant screen size ("radius not scaled by distance": radius x
//   the projection's scale); reflection 0, the corona, is stretched by the corona scale and turned by the rotation;
//   "align rotation with screen centre" adds the flare's angle around the screen centre;
// - brightness = the reflection's x the distance fade (linear in view depth from near to far) x the angle fade
//   (dot( marker forward, camera - flare ) in world units, un-normalised as Halo has it, between the cosines of the
//   cutoff and falloff angles) x the light's value x the occlusion test's visible fraction;
// - colour: texture x colour + (1 - colour) x tint factor x texture^8 (tint factor = 1 - modulation factor),
//   times brightness, added in Halo's gamma space frame buffer (SRCALPHA / ONE). Texture alpha is unused.
// The occlusion test is a square of the occlusion radius (world units) at the flare moved toward the viewer, along
// the marker's forward x 1.41, or not at all, sampled 8 x 8 against the depth buffer per pixel (Halo used an occlusion
// query smoothed over frames). Suns are drawn far off in their direction, tested at the far plane.
//
// Every sprite is a quad whose four corners carry the flare; the vertex shader builds it on screen. Positions are in
// the model's space, so a flare can ride on an object; the renderer attribute HaloFunctionScale (0-1, or the tint's
// red where it's unset) is the light's value (an attachment's object function).
//

HEADER
{
	Description = "Halo 2 lens flare";
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

	// The lens's animation over one period (see the VS), float data set from code.
	CreateInputTexture2D( FlareAnimation, Linear, 8, "None", "_color", ",0/,0/0", Default4( 1.00, 1.00, 1.00, 1.00 ) );
	Texture2D g_tFlareAnimation < Channel( RGBA, Box( FlareAnimation ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( false ); >;
	SamplerState g_sFlareAnimation < Filter( Bilinear ); AddressU( WRAP ); AddressV( CLAMP ); >;
}

struct VertexInput
{
	// The flare's position (model space), or for the sun its direction.
	float3 vPositionOs : POSITION < Semantic( PosXyz ); >;
	// Corner (-1..1), position along the reflection line (0 on the flare, 1 opposite), rotation offset (radians).
	float4 vCorner : TEXCOORD0 < Semantic( None ); >;
	// Radius (s&box units, or world units for a constant screen size), brightness, flags, occlusion radius (s&box
	// units). Flags: 1 sun, 2 radius not scaled by distance, 4 radius scaled by occlusion, 16 align rotation with the
	// screen centre, 32 no occlusion test, 64 x the occlusion offset direction (0 toward the viewer, 1 marker forward,
	// 2 none).
	float4 vSprite : TEXCOORD1 < Semantic( None ); >;
	// The marker's forward (model space; zero: none), corona width scale.
	float4 vAxis : TEXCOORD2 < Semantic( None ); >;
	// Cosines of the falloff and cutoff angles, near and far fade depths (s&box units; equal: no fade).
	float4 vFade : TEXCOORD3 < Semantic( None ); >;
	// Colour (gamma space), tint factor.
	float4 vColor : TEXCOORD4 < Semantic( None ); >;
	// Corona height scale, the flare's index in the model (its visibility slot past the renderer's first).
	float4 vExtra : TEXCOORD5 < Semantic( None ); >;

	uint nInstanceTransformID : TEXCOORD13 < Semantic( InstanceTransformUv ); >;
};

struct PixelInput
{
	float4 vPositionPs : SV_Position;
	float2 vUv : TEXCOORD0;
	float4 vColor : TEXCOORD1;
	// This corner's and the occlusion test point's position on screen (normalized device coordinates).
	float4 vNdc : TEXCOORD2;
	// The test point's projected depth (-1: no test), the test square's half size in pixels.
	float2 vOcclusion : TEXCOORD3;
	float flBrightness : TEXCOORD4;
	// 1 if the radius shrinks with occlusion.
	float flShrink : TEXCOORD5;
};

VS
{
	#include "instancing.fxc"

	// s&box units in a world unit.
	static const float WorldUnit = 103.2;

	// The light's value where a game drives it; below 0 (unset) the renderer's tint (red).
	float g_flHaloFunctionScale < Attribute( "HaloFunctionScale" ); Default( -1.0 ); >;

	// The flares' visibility kept from frame to frame (Halo2/FlareVisibility.cs): this renderer's first slot (below 0:
	// none, the pixel shader tests each frame afresh), the flare's index in the model added. One row per view; the
	// columns past the slots tag each with the view that wrote it (halo2_flare_visibility.shader).
	float g_flHaloFlareSlot < Attribute( "HaloFlareSlot" ); Default( -1.0 ); >;
	Texture2D g_tHaloFlareVisibility < Attribute( "HaloFlareVisibility" ); SrgbRead( false ); >;

	// The row this view wrote this frame: its camera and time.
	int FlareVisibilityRow()
	{
		int row = 0;
		float best = 1e30;
		[unroll] for ( int r = 0; r < 4; r++ )
		{
			float4 tag = g_tHaloFlareVisibility.Load( int3( 1024, r, 0 ) );
			float4 tagForward = g_tHaloFlareVisibility.Load( int3( 1025, r, 0 ) );
			float score = length( tag.xyz - g_vCameraPositionWs ) + 1000 * ( 1 - dot( tagForward.xyz, g_vCameraDirWs ) ) + abs( tag.w - g_flTime ) * 1e6;
			if ( score < best )
			{
				best = score;
				row = r;
			}
		}

		return row;
	}

	// The lens's brightness and colour animation (its first functions of game time): sampled over one period into
	// g_tFlareAnimation (row 0 brightness in r, row 1 colour), with the period (seconds; 0: none) and 1 when
	// synchronized (else each flare runs at its own phase, from its position).
	float2 g_vFlareAnimation < Default2( 0.0, 0.0 ); >;

	PixelInput MainVs( VertexInput v )
	{
		PixelInput o;
		o.vUv = v.vCorner.xy * float2( 0.5, -0.5 ) + 0.5;
		o.vColor = 0;
		o.vNdc = 0;
		o.vOcclusion = float2( -1, 1 );
		o.flShrink = 0;
		o.flBrightness = 0;
		o.vPositionPs = float4( 2, 2, 2, 1 );

		float flags = v.vSprite.z;
		bool sun = fmod( flags, 2 ) >= 1;
		bool fixedSize = fmod( floor( flags / 2 ), 2 ) >= 1;
		bool shrink = fmod( floor( flags / 4 ), 2 ) >= 1;
		bool alignToCentre = fmod( floor( flags / 16 ), 2 ) >= 1;
		bool noTest = fmod( floor( flags / 32 ), 2 ) >= 1;
		float offsetDirection = fmod( floor( flags / 64 ), 4 );

		float value = g_flHaloFunctionScale >= 0 ? saturate( g_flHaloFunctionScale ) : saturate( GetExtraPerInstanceShaderData( v.nInstanceTransformID ).vTint.r );
		float brightness = saturate( v.vSprite.y ) * value;
		if ( brightness <= 0 || v.vSprite.x <= 0 )
			return o;

		float3x4 matObjectToWorld = GetTransformMatrix( v.nInstanceTransformID );
		float3 lightWs = sun ? g_vCameraPositionWs + normalize( v.vPositionOs ) * g_flFarPlane * 0.5 : mul( matObjectToWorld, float4( v.vPositionOs, 1 ) ).xyz;
		float3 forward = sun ? -normalize( v.vPositionOs ) : mul( (float3x3)matObjectToWorld, v.vAxis.xyz );
		forward = dot( forward, forward ) > 1e-8 ? normalize( forward ) : float3( 0, 0, 0 );

		float3 d = lightWs - g_vCameraPositionWs;
		float depth = dot( d, g_vCameraDirWs );
		if ( depth <= g_flNearPlane )
			return o;

		// Distance fade, linear in view depth (Halo also culls the flare past the far depth).
		if ( !sun && v.vFade.z != v.vFade.w )
		{
			if ( depth >= v.vFade.w )
				return o;
			brightness *= saturate( ( depth - v.vFade.w ) / ( v.vFade.z - v.vFade.w ) );
		}

		// Angle fade: the marker's forward against the (un-normalised, world unit) vector to the camera.
		if ( !sun && v.vFade.x != v.vFade.y && dot( forward, forward ) > 0.5 )
			brightness *= saturate( ( dot( forward, -d / WorldUnit ) - v.vFade.y ) / ( v.vFade.x - v.vFade.y ) );

		float3 animatedColor = 1;
		if ( g_vFlareAnimation.x > 0 )
		{
			float phase = g_vFlareAnimation.y > 0.5 ? 0 : frac( sin( dot( lightWs, float3( 12.9898, 78.233, 37.719 ) ) ) * 43758.5453 );
			float u = frac( g_flTime / g_vFlareAnimation.x + phase );
			brightness *= g_tFlareAnimation.SampleLevel( g_sFlareAnimation, float2( u, 0.25 ), 0 ).r;
			animatedColor = g_tFlareAnimation.SampleLevel( g_sFlareAnimation, float2( u, 0.75 ), 0 ).rgb;
		}

		if ( brightness <= 0 )
			return o;

		float4 lightPs = Position3WsToPs( lightWs );
		float2 lightNdc = lightPs.xy / lightPs.w;
		float2 centreNdc = lightNdc * ( 1 - 2 * v.vCorner.z );

		// Half size on screen: world units at the flare's depth, or a constant screen size.
		float focal = g_matViewToProjection[1][1];
		float radiusNdc = fixedSize ? v.vSprite.x * focal : v.vSprite.x * focal / depth;

		float angle = v.vCorner.w;
		float aspect = g_vViewportSize.y / g_vViewportSize.x;
		if ( alignToCentre )
			angle += atan2( lightNdc.y, lightNdc.x / aspect );

		float2 corner = v.vCorner.xy * float2( v.vAxis.w, v.vExtra.x );
		float s = sin( angle );
		float c = cos( angle );
		corner = float2( corner.x * c - corner.y * s, corner.x * s + corner.y * c );
		float2 ndc = centreNdc + corner * radiusNdc * float2( aspect, 1 );

		o.vColor = float4( v.vColor.rgb * animatedColor, v.vColor.a );
		o.flBrightness = brightness;
		o.flShrink = shrink ? 1 : 0;
		o.vPositionPs = float4( ndc, saturate( lightPs.z / lightPs.w ), 1 );
		o.vNdc = float4( ndc, lightNdc );
		if ( noTest )
			return o;

		if ( g_flHaloFlareSlot >= 0 )
		{
			float visibility = g_tHaloFlareVisibility.Load( int3( (int)( g_flHaloFlareSlot + v.vExtra.y + 0.5 ), FlareVisibilityRow(), 0 ) ).x;
			if ( visibility <= 0 )
			{
				o.flBrightness = 0;
				o.vPositionPs = float4( 2, 2, 2, 1 );
				return o;
			}

			o.vOcclusion = float2( -2, visibility );
			return o;
		}

		// The occlusion test point and its square's half size in pixels.
		float radius = v.vSprite.w;
		float3 testWs;
		if ( sun )
		{
			testWs = g_vCameraPositionWs + normalize( v.vPositionOs ) * g_flFarPlane * 0.5;
		}
		else
		{
			float3 offset = offsetDirection < 0.5 ? -g_vCameraDirWs * radius : offsetDirection < 1.5 ? forward * radius * 1.41421356 : float3( 0, 0, 0 );
			testWs = lightWs + offset;
		}

		float testDepth = dot( testWs - g_vCameraPositionWs, g_vCameraDirWs );
		if ( testDepth <= g_flNearPlane )
			return o;

		// A sun's square is its occlusion radius at Halo's far plane (1024 world units: 50 there is about 31 pixels
		// across half at 720 lines), so the sun fades out over that much as an edge crosses it, as in MCC.
		float4 testPs = Position3WsToPs( testWs );
		float testPixels = max( 1, radius * focal / ( sun ? 1024 * WorldUnit : testDepth ) * 0.5 * g_vViewportSize.y );
		o.vNdc.zw = testPs.xy / testPs.w;
		o.vOcclusion = float2( testPs.z / testPs.w, testPixels );
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

	// The frame as drawn before the flares (Halo2/FlareVisibility.cs grabs it), where it's set: s&box's copy for
	// translucent readers is masked by the reader's depth, and a flare writes none, so it reads black there.
	Texture2D g_tHaloFlareFrame < Attribute( "HaloFlareFrame" ); SrgbRead( false ); >;
	bool g_bHaloHasFlareFrame < Attribute( "HaloHasFlareFrame" ); Default( 0 ); >;

	CreateInputTexture2D( Flare, Linear, 8, "None", "_color", ",0/,0/0", Default4( 1.00, 1.00, 1.00, 1.00 ) );
	Texture2D g_tFlare < Channel( RGBA, Box( Flare ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( false ); >;

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

	// s&box keeps reverse-Z depth 0-0.02 for the 3D skybox; the world is drawn over 0.02-1.
	static const float WorldMinZ = 0.02;

	float SceneDepth( float2 pixel )
	{
		if ( g_flViewportMinZ > 0.5 )
			return RemapValClamped( Depth::Get( pixel ), WorldMinZ, 1.0, 0.0, 1.0 );
		return Depth::GetNormalized( pixel );
	}

	// The fraction of the test square where nothing is nearer than the test point (reversed depth: larger is nearer):
	// the part on screen, exactly, times the visible share of 8 x 8 samples over that part (off screen counts as
	// hidden). Halo counted the square's visible pixels (an occlusion query).
	float Occlusion( PixelInput i, float2 testPixels )
	{
		// Kept from frame to frame (the vertex shader read it), or no test.
		if ( i.vOcclusion.x < -1.5 )
			return i.vOcclusion.y;
		if ( i.vOcclusion.x < 0 )
			return 1;

		float halfPixels = i.vOcclusion.y;
		float2 lo = max( testPixels - halfPixels, g_vViewportOffset );
		float2 hi = min( testPixels + halfPixels, g_vViewportOffset + g_vViewportSize );
		if ( any( hi <= lo ) )
			return 0;
		float onScreen = ( hi.x - lo.x ) * ( hi.y - lo.y ) / ( 4 * halfPixels * halfPixels );

		float visible = 0;
		[loop] for ( int y = 0; y < 8; y++ )
		{
			[unroll] for ( int x = 0; x < 8; x++ )
			{
				float2 pixel = lo + ( float2( x, y ) + 0.5 ) / 8 * ( hi - lo );
				if ( SceneDepth( pixel ) <= i.vOcclusion.x )
					visible += 1;
			}
		}

		return onScreen * visible / 64;
	}

	float4 MainPs( PixelInput i ) : SV_Target0
	{
		float2 pixelsPerNdc = float2( 1 / ddx( i.vNdc.x ), 1 / ddy( i.vNdc.y ) );
		float2 testPixels = i.vPositionPs.xy + ( i.vNdc.zw - i.vNdc.xy ) * pixelsPerNdc;

		float visibility = Occlusion( i, testPixels );
		if ( visibility <= 0 )
			discard;

		// "Radius scaled by occlusion": (1 + visibility) / 2 as big.
		float2 uv = i.vUv;
		if ( i.flShrink > 0.5 )
		{
			uv = 0.5 + ( uv - 0.5 ) * 2 / ( 1 + visibility );
			if ( any( uv < 0 ) || any( uv > 1 ) )
				discard;
		}

		float3 map = Tex2DS( g_tFlare, g_sClamp, uv ).rgb;
		float3 map2 = map * map;
		float3 map8 = map2 * map2;
		map8 *= map8;
		float3 color = saturate( i.vColor.rgb );
		float3 added = saturate( map * color + ( 1 - color ) * i.vColor.a * map8 ) * i.flBrightness * visibility;

		// Added to what's behind in gamma space.
		float2 screenUv = i.vPositionPs.xy * g_vInvViewportSize;
		float3 behind = g_flViewportMinZ > 0.5 ? 0
			: g_bHaloHasFlareFrame ? saturate( g_tHaloFlareFrame.SampleLevel( g_sClamp, screenUv, 0 ).rgb )
			: saturate( g_tFrameBufferCopyTexture.SampleLevel( g_sClamp, screenUv * g_vFrameBufferCopyInvSizeAndUvScale.zw, 0 ).rgb );
		float3 sum = GammaToLinear( saturate( LinearToGamma( behind ) + saturate( added ) ) );
		return float4( max( sum - behind, 0 ), 0 );
	}
}
