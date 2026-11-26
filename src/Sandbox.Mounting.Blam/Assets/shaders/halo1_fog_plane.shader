//
// Halo CE planar fog (fog tags on a structure BSP's fog planes).
//
// Halo fogs a view by one fog plane: the one the cluster the camera is in is under, if any. Then
// everything drawn is fogged against that plane, however far it is from the plane's outline, by
// the globals' planar fog density bitmap: across, how far in front of the camera the point is over
// the fog's opaque distance; down, how deep below the plane it is over the opaque depth. Its alpha
// is the fog seen from above the plane, its luminance (distance only) from deep in the fog, and
// the camera's own depth below the plane over the opaque depth blends between the two. That times
// the fog's maximum density covers the point with the fog color. The sky isn't fogged.
//
// The mesh is one triangle covering the screen, drawn without depth testing over the level. It
// finds the camera's cluster in the BSP's collision tree and draws only if that's under this
// fog's plane.
//
// Water fogs what's seen through it itself (halo1_glass_water.shader) and writes over whatever
// is behind it, and s&box may draw it before or after this fog. So pixels where the view crosses
// the plane's water surface are left alone here, whichever order they draw in.
//

HEADER
{
	Description = "Halo CE planar fog";
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
		PixelInput i = FinalizeVertex( ProcessVertex( v ) );

		// The mesh's positions are the corners of a triangle covering the screen (see the top).
		i.vPositionPs = float4( v.vPositionOs.xy, 0.5, 1 );
		return i;
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
	RenderState( DstBlend, INV_SRC_ALPHA );
	RenderState( BlendOp, ADD );
	RenderState( SrcBlendAlpha, ZERO );
	RenderState( DstBlendAlpha, ONE );

	// Which of its BSP's fogs this is, as the collision tree's leaves hold it.
	float g_flPlanarFogIndex < Default( 0.0 ); >;

	// What this draw does: 0 both, 1 the fog only, 2 the screen layers only. Halo draws the screen layers last, over the water and every other transparent thing, so they're a draw of their own (see PlanarFogFrame).
	float g_flFogPass < Default( 0.0 ); >;

	// Plane as (normal, distance), the normal pointing out of the fog. s&box units.
	float4 g_vPlanarFogPlane < Default4( 0.0, 0.0, 1.0, 0.0 ); >;
	float3 g_vPlanarFogColor < Default3( 1.0, 1.0, 1.0 ); >;
	float g_flPlanarFogMaximumDensity < Default( 1.0 ); >;
	float g_flPlanarFogAtmosphereDominant < Default( 0.0 ); >;
	float g_flPlanarFogOpaqueDistance < Default( 1.0 ); >;
	float g_flPlanarFogOpaqueDepth < Default( 1.0 ); >;

	// The sky's atmospheric fog opaque distance (s&box units, 0: none).
	float g_flPlanarFogAtmosphereDistance < Default( 0.0 ); >;

	// The BSP's collision tree, two texels per node (see CameraFog).
	CreateInputTexture2D( PlanarFogTree, Linear, 8, "None", "_color", ",0/,0/0", Default4( 0.00, 0.00, 0.00, 0.00 ) );
	Texture2D g_tPlanarFogTree < Channel( RGBA, Box( PlanarFogTree ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( false ); >;

	// Halo's planar fog density bitmap (see the top).
	SamplerState g_sPlanarFogDensity < Filter( Bilinear ); AddressU( CLAMP ); AddressV( CLAMP ); >;
	CreateInputTexture2D( PlanarFogDensity, Linear, 8, "None", "_color", ",0/,0/0", Default4( 1.00, 1.00, 1.00, 1.00 ) );
	Texture2D g_tPlanarFogDensity < Channel( RGBA, Box( PlanarFogDensity ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( false ); >;

	// Bounds of the water mask (see g_vPlanarFogWater), s&box units.
	float3 g_vPlanarFogBoundsMin < Default3( 0.0, 0.0, 0.0 ); >;
	float3 g_vPlanarFogBoundsMax < Default3( 0.0, 0.0, 0.0 ); >;

	// Fog screen (see FogScreen): layer count (0: off), (near, far distance, near, far density),
	// (start distance from the plane, maximum depth, map scale, bitmap count), (strafing, zoom,
	// animation period, wind speed); distances in world units. The map's bitmaps side by side.
	float g_flFogScreenLayerCount < Default( 0.0 ); >;
	float4 g_vFogScreenGradient < Default4( 1.0, 4.0, 0.5, 0.9 ); >;
	float4 g_vFogScreenEye < Default4( 0.0, 1.0, 1.0, 1.0 ); >;
	float4 g_vFogScreenMotion < Default4( 1.0, 1.0, 0.0, 0.0 ); >;
	float3 g_vFogScreenColor < Default3( 0.0, 0.0, 0.0 ); >;
	float g_flFogScreenWorldScale < Default( 103.2 ); >;
	float g_flFogScreenMultipass < Default( 0.0 ); >;
	SamplerState g_sFogScreen < Filter( Bilinear ); AddressU( CLAMP ); AddressV( CLAMP ); >;
	CreateInputTexture2D( FogScreenMap, Linear, 8, "None", "_color", ",0/,0/0", Default4( 0.00, 0.00, 0.00, 0.00 ) );
	Texture2D g_tFogScreenMap < Channel( RGBA, Box( FogScreenMap ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( false ); >;

	// The frame behind the fog, which the multipass fog screen adds to (see FogScreen), grabbed
	// for it by PlanarFogFrame.cs.
	Texture2D g_tHaloFogBehind < Attribute( "HaloFogBehind" ); SrgbRead( false ); >;
	bool g_bHaloHasFogBehind < Attribute( "HaloHasFogBehind" ); Default( 0 ); >;

	// The frame behind the fog itself (the level, fogged by its atmospheric fog), grabbed by PlanarFogFrame, which the fog is blended over in gamma space exactly.
	Texture2D g_tHaloFogPlaneBehind < Attribute( "HaloFogPlaneBehind" ); SrgbRead( false ); >;
	bool g_bHaloHasFogPlaneBehind < Attribute( "HaloHasFogPlaneBehind" ); Default( 0 ); >;
	SamplerState g_sBehind < Filter( Point ); AddressU( CLAMP ); AddressV( CLAMP ); >;

	// The water under the plane: x = the axis it's level on (0..2), y = its level along that axis,
	// z = 1 if there's water. The mask covers the bounds above across the other two axes (the one
	// after x as columns, the one after that as rows) and is set where there's water.
	float4 g_vPlanarFogWater < Default4( 2.0, 0.0, 0.0, 0.0 ); >;
	CreateInputTexture2D( PlanarFogWaterMask, Linear, 8, "None", "_color", ",0/,0/0", Default4( 0.00, 0.00, 0.00, 0.00 ) );
	Texture2D g_tPlanarFogWaterMask < Channel( RGBA, Box( PlanarFogWaterMask ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( false ); >;

	// Halo blended fog in gamma space and s&box blends in linear space, which washes dark colors
	// out much faster. Raising the density to 2.2 matches it over dark surfaces, like the
	// atmospheric fog (see ScenarioSceneLoader.BuildFog).
	static const float GammaFogExponent = 2.2;

	// The collision tree's nodes per row of its texture; PlanarFog.cs uses the same.
	static const int TreeNodesPerRow = 1024;

	// Deeper than any BSP's collision tree (icefields' is 81 nodes deep).
	static const int MaximumTreeDepth = 128;

	// The fog of the cluster a point is in: which of the BSP's fogs its plane has, or -1 for none
	// (or outside the level). A node is (plane normal, distance), (back child, front child); a
	// child is a node index, or for a leaf -(fog + 2), or -1.
	int CameraFog( float3 position )
	{
		int node = 0;
		[loop] for ( int depth = 0; depth < MaximumTreeDepth; depth++ )
		{
			int2 texel = int2( ( node % TreeNodesPerRow ) * 2, node / TreeNodesPerRow );
			float4 plane = g_tPlanarFogTree.Load( int3( texel, 0 ) );
			float2 children = g_tPlanarFogTree.Load( int3( texel + int2( 1, 0 ), 0 ) ).xy;
			float child = dot( plane.xyz, position ) >= plane.w ? children.y : children.x;
			if ( child < 0 )
				return (int)( -child ) - 2;
			node = (int)child;
		}

		return -1;
	}

	float AxisComponent( float3 v, int axis )
	{
		return axis == 0 ? v.x : ( axis == 1 ? v.y : v.z );
	}

	// Whether the view ray crosses the plane's water surface before it gets to distance tEnd.
	bool SeenThroughWater( float3 camera, float3 dir, float tEnd )
	{
		if ( g_vPlanarFogWater.z < 0.5 )
			return false;

		int axis = (int)g_vPlanarFogWater.x;
		float along = AxisComponent( dir, axis );
		if ( abs( along ) < 1e-6 )
			return false;

		// The scene has to be clearly past the water, not lying on it (c10's lily pads).
		float t = ( g_vPlanarFogWater.y - AxisComponent( camera, axis ) ) / along;
		if ( t <= 0 || ( tEnd - t ) * abs( along ) < 1.0 )
			return false;

		float3 crossing = camera + dir * t;
		int u = ( axis + 1 ) % 3;
		int v = ( axis + 2 ) % 3;
		float2 uv = float2(
			( AxisComponent( crossing, u ) - AxisComponent( g_vPlanarFogBoundsMin, u ) ) / ( AxisComponent( g_vPlanarFogBoundsMax, u ) - AxisComponent( g_vPlanarFogBoundsMin, u ) ),
			( AxisComponent( crossing, v ) - AxisComponent( g_vPlanarFogBoundsMin, v ) ) / ( AxisComponent( g_vPlanarFogBoundsMax, v ) - AxisComponent( g_vPlanarFogBoundsMin, v ) ) );
		if ( any( uv < 0 ) || any( uv >= 1 ) )
			return false;

		uint width, height;
		g_tPlanarFogWaterMask.GetDimensions( width, height );
		int2 texel = int2( uv * float2( width, height ) );
		return g_tPlanarFogWaterMask.Load( int3( texel, 0 ) ).r > 0.5;
	}

	// Halo's planar fog at a point (0..1, before the gamma correction; see the top).
	float PlanarFogDensity( float3 position, float3 camera )
	{
		float2 uv = float2(
			dot( position - camera, g_vCameraDirWs ) / g_flPlanarFogOpaqueDistance,
			( g_vPlanarFogPlane.w - dot( g_vPlanarFogPlane.xyz, position ) ) / g_flPlanarFogOpaqueDepth );
		float eye = saturate( ( g_vPlanarFogPlane.w - dot( g_vPlanarFogPlane.xyz, camera ) ) / g_flPlanarFogOpaqueDepth );
		float4 density = g_tPlanarFogDensity.SampleLevel( g_sPlanarFogDensity, uv, 0 );
		return g_flPlanarFogMaximumDensity * lerp( density.a, density.b, eye );
	}

	// Halo CE's atmospheric fog amount and gamma color, from the scene's gradient fog (as halo1_environment.shader works them out).
	float HaloFogAmount( float3 positionWs )
	{
		if ( !g_bFogEnabled || !g_bGradientFogEnabled )
			return 0;

		float depth = dot( positionWs - g_vCameraPositionWs, g_vCameraDirWs );
		float u = saturate( g_vGradientFogBiasAndScale.x + g_vGradientFogBiasAndScale.z * depth );
		return u * pow( saturate( g_vGradientFogColor_Opacity.w ), 1.0 / 2.2 );
	}

	float3 HaloFogColor()
	{
		return SrgbLinearToGamma( saturate( g_vGradientFogColor_Opacity.rgb ) );
	}

	// Halo's fog pass (MCC's fx.bin shader 122) over a level already fogged by the atmospheric fog in gamma space comes down to blending it toward (1 - A above) planar color + A atmospheric color by the planar density, in gamma space; done exactly over the grabbed frame, premultiplied for the blend.
	float4 ExactPlanarFog( float3 scene, float3 camera, float2 screen, float density )
	{
		float above = g_flPlanarFogAtmosphereDominant > 0.5 ? 1
			: g_flPlanarFogAtmosphereDistance > 0 ? saturate( ( dot( g_vPlanarFogPlane.xyz, camera ) - g_vPlanarFogPlane.w ) / g_flPlanarFogAtmosphereDistance )
			: 0;
		float atmosphere = HaloFogAmount( scene );
		float3 fogGamma = saturate( ( 1 - atmosphere * above ) * g_vPlanarFogColor + atmosphere * HaloFogColor() );

		float3 behind = saturate( g_tHaloFogPlaneBehind.SampleLevel( g_sBehind, ( screen - g_vViewportOffset ) * g_vInvViewportSize, 0 ).rgb );
		float3 result = SrgbGammaToLinear( lerp( SrgbLinearToGamma( behind ), fogGamma, saturate( density ) ) );

		// The least coverage that keeps the premultiplied color from going negative (as halo1_contrail.shader).
		float3 ratio = result / max( behind, 0.0001 );
		float a = saturate( max( saturate( density ), 1 - min( ratio.r, min( ratio.g, ratio.b ) ) ) );
		return float4( max( result - behind * ( 1 - a ), 0 ), a );
	}

	// The fog's color at a point: Halo puts the atmospheric fog in front of it as far as the camera
	// is above the plane over the atmospheric fog's opaque distance, or all of it for "atmosphere
	// dominant" fog (c10's swamp).
	float3 PlanarFogColor( float3 position, float3 camera, float2 screen )
	{
		float3 color = SrgbGammaToLinear( g_vPlanarFogColor );
		float above = g_flPlanarFogAtmosphereDominant > 0.5 ? 1
			: g_flPlanarFogAtmosphereDistance > 0 ? saturate( ( dot( g_vPlanarFogPlane.xyz, camera ) - g_vPlanarFogPlane.w ) / g_flPlanarFogAtmosphereDistance )
			: 0;
		return above > 0 ? lerp( color, DoAtmospherics( position, screen, float4( color, 1 ) ).rgb, above ) : color;
	}

	// One of the screen layer map's bitmaps (side by side in the texture, with a row of their mean
	// colors under them), wrapping at its own edges: the sampler would blend the neighboring
	// bitmap in at the seams.
	float3 SampleNoise( float frame, float2 uv, float bitmapCount )
	{
		uint width, height;
		g_tFogScreenMap.GetDimensions( width, height );
		int2 size = int2( width / (uint)bitmapCount, height - 1 );
		float2 p = frac( uv ) * size - 0.5;
		int2 i0 = (int2)floor( p );
		float2 f = p - i0;
		int x0 = ( i0.x % size.x + size.x ) % size.x, x1 = ( x0 + 1 ) % size.x;
		int y0 = ( i0.y % size.y + size.y ) % size.y, y1 = ( y0 + 1 ) % size.y;
		int base = (int)frame * size.x;
		float3 a = g_tFogScreenMap.Load( int3( base + x0, y0, 0 ) ).rgb;
		float3 b = g_tFogScreenMap.Load( int3( base + x1, y0, 0 ) ).rgb;
		float3 c = g_tFogScreenMap.Load( int3( base + x0, y1, 0 ) ).rgb;
		float3 d = g_tFogScreenMap.Load( int3( base + x1, y1, 0 ) ).rgb;
		return lerp( lerp( a, b, f.x ), lerp( c, d, f.x ), f.y );
	}

	// A bitmap's mean color, from the row under the bitmaps.
	float3 NoiseMean( float frame, float bitmapCount )
	{
		uint width, height;
		g_tFogScreenMap.GetDimensions( width, height );
		return g_tFogScreenMap.Load( int3( (int)frame * (int)( width / (uint)bitmapCount ), height - 1, 0 ) ).rgb;
	}

	float Hash( float n )
	{
		return frac( sin( n * 12.9898 + 4.1414 ) * 43758.5453 );
	}

	// The screen layer noise anchored in the world at point q (world units), seen along view
	// direction dir: projected on the three world planes and blended by how squarely the view
	// meets each. Each projection is sheared along its axis, so moving through the noise slides it
	// instead of leaving it still (cross-fading slices of it instead showed their seams).
	//
	// Halo samples each layer once. A blend of independent samples varies less than one does
	// (two equal weights: 1/sqrt(2) as much), which made the noise weaker than MCC's wherever the
	// view wasn't square to a world axis. So the blend's difference from the bitmap's mean is
	// scaled back up by 1/sqrt(sum of squared weights), which keeps both the mean and the spread
	// of a single sample.
	float3 WorldNoise( float3 q, float3 dir, float scale, float seed, float frame, float bitmapCount )
	{
		float3 weights = pow( abs( dir ), 4 );
		weights /= dot( weights, 1 );

		float3 result = 0;
		[unroll] for ( int axis = 0; axis < 3; axis++ )
		{
			float along = AxisComponent( q, axis );
			float2 across = float2( AxisComponent( q, ( axis + 1 ) % 3 ), AxisComponent( q, ( axis + 2 ) % 3 ) );
			float key = seed * 31.0 + axis * 7.0;
			float2 offset = float2( Hash( key ), Hash( key + 101.0 ) );
			result += SampleNoise( frame, ( across + along * float2( 0.37, 0.61 ) ) * scale + offset, bitmapCount ) * weights[axis];
		}

		float3 mean = NoiseMean( frame, bitmapCount );
		return saturate( mean + ( result - mean ) * rsqrt( dot( weights, weights ) ) );
	}

	// Halo's fog screen (layers of noise over the view while the camera is in the fog), after its
	// Xbox renderer. Halo keeps the layers' offsets from frame to frame (turning slides a layer by
	// its depth times the angle, strafing by the distance, wind drifts it, moving forward brings
	// the layers past the camera). Here each layer is a shell at a fixed view depth whose noise is
	// anchored in the world (WorldNoise), which moves much the same way without keeping anything:
	// a mounted scene can't hold the mount's own components, so nothing could keep it per frame.
	// Turning and strafing come out the same as Halo's with its multipliers at 1 (every map's);
	// moving forward slides the noise instead of bringing the next layer in.
	// Composes onto the premultiplied color and alpha this fog writes.
	void FogScreen( float3 camera, float3 scene, float2 pixel, float3 viewDir, inout float3 premultiplied, inout float alpha )
	{
		int count = (int)g_flFogScreenLayerCount;
		if ( count <= 0 )
			return;

		// World units from here on.
		float unit = g_flFogScreenWorldScale;
		float3 cameraWu = camera / unit;

		// Eye density: 0 at the start distance from the plane, 1 at the fog's maximum depth below it.
		float planeDistance = ( dot( g_vPlanarFogPlane.xyz, camera ) - g_vPlanarFogPlane.w ) / unit;
		float start = g_vFogScreenEye.x;
		float maximumDepth = g_vFogScreenEye.y;
		if ( abs( start + maximumDepth ) < 0.0001 )
			start = 0.0001 - maximumDepth;
		float eye = saturate( ( planeDistance - start ) / ( -maximumDepth - start ) );
		if ( eye <= 0 )
			return;

		float nearDistance = g_vFogScreenGradient.x;
		float farDistance = g_vFogScreenGradient.y;
		float layerSpacing = max( ( farDistance - nearDistance ) / count, 0.0001 );
		float mapScale = g_vFogScreenEye.z;
		float bitmapCount = max( g_vFogScreenEye.w, 1 );
		float period = g_vFogScreenMotion.z;

		// Halo maps a layer at depth z across the screen at map_scale * z / far / 2 per half screen
		// width, i.e. the same texture size in the world at every depth: this many repeats per
		// world unit (scaled by the strafing multiplier, which is how fast it slides).
		float aspect = g_vViewportSize.y / g_vViewportSize.x;
		float tanHalfWidth = 1 / g_matViewToProjection[0][0];
		float scale = mapScale / ( 2 * farDistance * tanHalfWidth ) * max( g_vFogScreenMotion.x, 0.0001 );

		float3 forward = g_vCameraDirWs;
		float along = max( dot( viewDir, forward ), 0.01 );
		float3 wind = float3( g_vFogScreenMotion.w * g_flTime, 0, 0 );

		float transmittance = 1;
		[loop] for ( int k = 0; k < count; k++ )
		{
			// Each layer at the middle of its stretch of the depth range.
			float z = ( k + 0.5 ) * layerSpacing + nearDistance;
			float t = ( z - nearDistance ) / max( farDistance - nearDistance, 0.0001 );
			float bell = 1 - pow( abs( 2 * t - 1 ), 3 );
			float density = bell * bell * eye * g_vFogScreenGradient.w;

			// Each bitmap's R, G and B are three frames, blended in turn as time goes by.
			float phase = k == 0 ? 0 : k == 1 ? 0.7135 : k == 2 ? 0.3422 : 0.5798;
			float frame = k - bitmapCount * floor( k / bitmapCount );
			float tau = 0;
			if ( period != 0 )
			{
				float f = bitmapCount * phase + g_flTime / period;
				frame = floor( f ) - bitmapCount * floor( floor( f ) / bitmapCount );
				tau = frac( f );
			}

			float3 q = cameraWu + viewDir * ( z / along ) - wind;
			float3 noise = WorldNoise( q, viewDir, scale, k, frame, bitmapCount );
			float3 weights = float3( ( 1 - tau ) * ( 1 - tau ), 2 * tau * ( 1 - tau ), tau * tau );
			transmittance *= 1 - saturate( dot( noise, weights * density ) );
		}

		float3 color = g_vFogScreenColor;
		if ( g_flFogScreenMultipass > 0.5 )
		{
			// Added to the scene, more of it the farther the scene is (Halo's planar fog density
			// curve over the layers' depth range), in gamma space to what's there: the scene behind
			// with this plane's fog over it.
			if ( !g_bHaloHasFogBehind )
				return;

			float viewDepth = dot( scene - camera, forward ) / unit;
			float range = saturate( ( viewDepth - nearDistance ) / max( farDistance - nearDistance, 0.0001 ) );
			float ramp = g_tPlanarFogDensity.SampleLevel( g_sFogScreen, float2( range, 0.5 ), 0 ).r;
			float strength = lerp( g_vFogScreenGradient.z / max( g_vFogScreenGradient.w, 0.0001 ), 1, ramp );
			float3 behind = g_tHaloFogBehind.SampleLevel( g_sBehind, ( pixel - g_vViewportOffset ) * g_vInvViewportSize, 0 ).rgb * ( 1 - alpha ) + premultiplied;
			float3 behindGamma = SrgbLinearToGamma( saturate( behind ) );
			premultiplied += SrgbGammaToLinear( saturate( behindGamma + color * ( 1 - transmittance ) * strength ) ) - SrgbGammaToLinear( behindGamma );
		}
		else
		{
			// Over the scene: scene * T + color * (1 - T), with the coverage gamma corrected like the fog.
			float coverage = pow( 1 - transmittance, GammaFogExponent );
			premultiplied = premultiplied * ( 1 - coverage ) + SrgbGammaToLinear( color ) * coverage;
			alpha = 1 - ( 1 - alpha ) * ( 1 - coverage );
		}
	}

	float4 MainPs( PixelInput i ) : SV_Target0
	{
		float3 camera = g_vCameraPositionWs.xyz;
		if ( CameraFog( camera ) != (int)g_flPlanarFogIndex )
			discard;

		float3 scene = Depth::GetWorldPosition( i.vPositionSs.xy );
		float3 ray = scene - camera;
		float sceneDistance = length( ray );
		float3 dir = ray / max( sceneDistance, 0.001 );

		float alpha = 0;
		float3 premultiplied = 0;
		bool sky = Depth::GetNormalized( i.vPositionSs.xy ) <= 0;
		if ( g_flFogPass < 1.5 && !sky && !SeenThroughWater( camera, dir, sceneDistance ) )
		{
			if ( g_bHaloHasFogPlaneBehind )
			{
				float4 fog = ExactPlanarFog( scene, camera, i.vPositionSs.xy, PlanarFogDensity( scene, camera ) );
				premultiplied = fog.rgb;
				alpha = fog.a;
			}
			else
			{
				alpha = pow( PlanarFogDensity( scene, camera ), GammaFogExponent );
				premultiplied = PlanarFogColor( scene, camera, i.vPositionSs.xy ) * alpha;
			}
		}

		if ( g_flFogPass < 0.5 || g_flFogPass > 1.5 )
			FogScreen( camera, scene, i.vPositionSs.xy, dir, premultiplied, alpha );

		if ( alpha <= 0 && all( premultiplied <= 0 ) )
			discard;
		return float4( premultiplied, alpha );
	}
}
