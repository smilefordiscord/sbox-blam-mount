//
// Halo 2 lens flare visibility, kept from frame to frame as Halo 2 keeps it: Halo2/FlareVisibility.cs runs it after
// the opaque things, one texel per flare (a slot), and halo2_lens_flare.shader reads it.
//
// HaloFlarePass picks the pass:
// 0 copy: the last frame's visibility, kept for pass 1 to read.
// 1 update: the slot's test square (as halo2_lens_flare.shader's: the occlusion radius at the flare moved toward the
//   viewer, along the marker's forward x 1.41, or not at all; a sun's at Halo's far plane) sampled 8 x 8 against the
//   depth buffer gives the visible fraction T, and the visibility moves toward it as Halo's byte does each frame it
//   draws: in by 1/8 of the way (1/2 when the lens fades in quickly), out by 1/2 (15/16 quickly), any visible pixel
//   keeping it above 0. Halo ran this per frame; here it runs per second as MCC at 240 frames a second.
//
// Slots (HaloFlareParams, three rows): row 0 the flare's position (world; a sun's direction) and its kind (0 none,
// 1 flare, 2 sun, 3 no occlusion test), row 1 the marker's forward (world) and the occlusion radius (s&box units),
// row 2 the occlusion offset direction (0 toward the viewer, 1 marker forward, 2 none), fades in quickly, fades out
// quickly, 1 on a first person weapon. The result (HaloFlareSource's other copy): visibility, the time it was taken,
// 1 once set.
//
// Every view that renders the scene (the editor's viewport, a camera preview, a screenshot) runs this, so each keeps
// its own row of results (Rows of them): the three columns past the slots tag a row with the view that wrote it
// (camera position and the time, camera forward, view size). A view takes the row of the same size nearest its camera,
// or else the oldest, whose history it starts afresh. Shared, a view drew another's visibility: a later view in the
// same frame (no time passed) kept the first one's value.
//

HEADER
{
	Description = "Halo 2 lens flare visibility";
	DevShader = true;
}

MODES
{
	Default();
	Forward();
}

FEATURES
{
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
	float2 vTexCoord : TEXCOORD0;

	#if ( PROGRAM == VFX_PROGRAM_VS )
		float4 vPositionPs : SV_Position;
	#endif

	#if ( PROGRAM == VFX_PROGRAM_PS )
		float4 vPositionSs : SV_Position;
	#endif
};

VS
{
	PixelInput MainVs( VertexInput i )
	{
		PixelInput o;
		o.vPositionPs = float4( i.vPositionOs.xy, 0.0f, 1.0f );
		o.vTexCoord = i.vTexCoord;
		return o;
	}
}

PS
{
	#include "postprocess/common.hlsl"
	#include "common/classes/Depth.hlsl"

	RenderState( DepthEnable, false );
	RenderState( DepthWriteEnable, false );
	RenderState( BlendEnable, false );

	Texture2D g_tSource < Attribute( "HaloFlareSource" ); SrgbRead( false ); >;
	Texture2D g_tParams < Attribute( "HaloFlareParams" ); SrgbRead( false ); >;
	int g_nPass < Attribute( "HaloFlarePass" ); Default( 0 ); >;

	static const float WorldUnit = 103.2;
	static const int Slots = 1024;
	static const int Rows = 4;

	// The view's size in pixels: the depth buffer can be bigger than the view (it's kept at the largest view's size),
	// and this pass draws into its own target, so the view's own size isn't at hand.
	float2 ViewSize()
	{
		float2 size;
		g_tDepthChain.GetDimensions( size.x, size.y );
		if ( all( g_vViewportToGBufferRatio > 0 ) && all( g_vViewportToGBufferRatio <= 1 ) )
			size *= g_vViewportToGBufferRatio;
		return size;
	}

	// This view's row, and whether it held this view's results last frame (else it starts afresh).
	int ViewRow( float2 size, out bool kept )
	{
		int row = 0;
		float best = 1e30;
		int oldest = 0;
		float oldestTime = 1e30;
		[unroll] for ( int r = 0; r < Rows; r++ )
		{
			float4 tag = g_tSource.Load( int3( Slots, r, 0 ) );
			float4 tagForward = g_tSource.Load( int3( Slots + 1, r, 0 ) );
			float4 tagSize = g_tSource.Load( int3( Slots + 2, r, 0 ) );
			float age = g_flTime - tag.w;
			bool live = tagSize.z > 0.5 && age >= 0 && age < 0.5;
			float when = live ? tag.w : -1e30;
			if ( when < oldestTime )
			{
				oldestTime = when;
				oldest = r;
			}

			if ( !live || any( abs( tagSize.xy - size ) > 0.5 ) )
				continue;

			float score = length( tag.xyz - g_vCameraPositionWs ) + 1000 * ( 1 - dot( tagForward.xyz, g_vCameraDirWs ) );
			if ( score < best )
			{
				best = score;
				row = r;
			}
		}

		kept = best < 1e30;
		return kept ? row : oldest;
	}

	// The visible fraction of a slot's test square: 8 x 8 samples of the depth buffer, nothing nearer than the test
	// point (reversed depth: larger is nearer); off screen counts as hidden.
	float Visible( float4 p0, float4 p1, float4 p2, float2 size )
	{
		bool sun = p0.w > 1.5;
		float radius = p1.w;
		float3 testWs;
		if ( sun )
		{
			testWs = g_vCameraPositionWs + normalize( p0.xyz ) * g_flFarPlane * 0.5;
		}
		else
		{
			float3 forward = dot( p1.xyz, p1.xyz ) > 1e-8 ? normalize( p1.xyz ) : float3( 0, 0, 0 );
			float3 offset = p2.x < 0.5 ? -g_vCameraDirWs * radius : p2.x < 1.5 ? forward * radius * 1.41421356 : float3( 0, 0, 0 );
			testWs = p0.xyz + offset;
		}

		float testDepth = dot( testWs - g_vCameraPositionWs, g_vCameraDirWs );
		if ( testDepth <= g_flNearPlane )
			return 0;

		float4 testPs = Position3WsToPs( testWs );
		float2 ndc = testPs.xy / testPs.w;
		float z = testPs.z / testPs.w;
		float2 centre = float2( ndc.x * 0.5 + 0.5, 0.5 - ndc.y * 0.5 ) * size;
		float focal = g_matViewToProjection[1][1];
		float halfPixels = max( 1, radius * focal / ( sun ? 1024 * WorldUnit : testDepth ) * 0.5 * size.y );

		// The part of the square on screen, exactly (Halo counted its pixels, so the visibility grows smoothly as an edge
		// crosses it; 8 x 8 samples over the whole square went up in eighths, 1/8 at once as the first row came on).
		float2 lo = max( centre - halfPixels, 0 );
		float2 hi = min( centre + halfPixels, size );
		if ( any( hi <= lo ) )
			return 0;
		float onScreen = ( hi.x - lo.x ) * ( hi.y - lo.y ) / ( 4 * halfPixels * halfPixels );

		// Halo 2 tests a first person weapon's own flares before it draws that weapon, so the weapon never hides them.
		// The depth buffer already holds it here and can't tell it from the world, so only the part on screen counts.
		if ( p2.w > 0.5 )
			return onScreen;

		float visible = 0;
		[loop] for ( int y = 0; y < 8; y++ )
		{
			[unroll] for ( int x = 0; x < 8; x++ )
			{
				float2 pixel = lo + ( float2( x, y ) + 0.5 ) / 8 * ( hi - lo );
				if ( Depth::Get( pixel ) <= z )
					visible += 1;
			}
		}

		return onScreen * visible / 64;
	}

	float4 MainPs( PixelInput i ) : SV_Target0
	{
		int slot = (int)i.vPositionSs.x;
		int row = (int)i.vPositionSs.y;
		float4 last = g_tSource.Load( int3( slot, row, 0 ) );
		if ( g_nPass == 0 )
			return last;

		// Other views' rows stay as they are.
		float2 size = ViewSize();
		bool kept;
		if ( ViewRow( size, kept ) != row )
			return last;

		if ( slot == Slots )
			return float4( g_vCameraPositionWs, g_flTime );
		if ( slot == Slots + 1 )
			return float4( g_vCameraDirWs, 0 );
		if ( slot == Slots + 2 )
			return float4( size, 1, 0 );

		float4 p0 = g_tParams.Load( int3( slot, 0, 0 ) );
		if ( p0.w < 0.5 )
			return 0;
		if ( p0.w > 2.5 )
			return float4( 1, g_flTime, 1, 0 );

		float4 p1 = g_tParams.Load( int3( slot, 1, 0 ) );
		float4 p2 = g_tParams.Load( int3( slot, 2, 0 ) );
		float target = Visible( p0, p1, p2, size );

		float dt = g_flTime - last.y;
		float v = target;
		if ( kept && last.z > 0.5 && dt >= 0 && dt < 0.5 )
		{
			float frames = dt * 240;
			float keep = target > last.x ? pow( p2.y > 0.5 ? 0.5 : 0.875, frames ) : pow( p2.z > 0.5 ? 1.0 / 16 : 0.5, frames );
			v = target + ( last.x - target ) * keep;
		}

		if ( target > 0 )
			v = max( v, 1.0 / 255 );
		return float4( saturate( v ), g_flTime, 1, 0 );
	}
}
