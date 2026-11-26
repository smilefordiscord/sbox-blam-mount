//
// Halo 3 particles (prt3 render methods, shaders\particle), drawn by Halo3ParticleWorld: every corner placed on the CPU, one draw per particle system.
// The pixel math is MCC's particle_templates pixel shaders: albedo diffuse (base map) or palettized (the palette at base map red and the particle's palette row), alpha from the albedo or the alpha map at the billboard's or the sprite's coordinates,
// frame blending into the next frame, depth fade against the scene, the black point curve, colour = albedo x particle colour + self illumination, premultiplied for pre_multiplied_alpha.
// HDR colours go through halo3_shader's exposure and tone curve (H3FinishColor) before blending. Not done: lighting (ravi orders), distortion, fog.
//

HEADER
{
	Description = "Halo 3 particle";
}

FEATURES
{
	#include "common/features.hlsl"

	// The render method's blend mode, in shaders\particle's option order.
	Feature( F_BLEND_MODE, 0..10( 0="Opaque", 1="Additive", 2="Multiply", 3="Alpha Blend", 4="Double Multiply", 5="Maximum", 6="Multiply Add", 7="Add Src Times Dst Alpha", 8="Add Src Times Src Alpha", 9="Inverse Alpha Blend", 10="Premultiplied Alpha" ), "Blending" );
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

// s&box's Vertex, filled by Halo3ParticleWorld.
struct VertexInput
{
	// The corner in the world.
	float3 vPosition : POSITION < Semantic( None ); >;
	// The frame's texture coordinates (xy) and the next frame's (zw).
	float4 vFrameUv : TEXCOORD0 < Semantic( None ); >;
	// The particle's colour (linear HDR, before exposure) and alpha.
	float4 vColor : TEXCOORD1 < Semantic( None ); >;
	// The billboard's own coordinates (0-1 over the quad), and how far to blend into the next frame.
	float3 vBillboard : NORMAL < Semantic( None ); >;
	// The black point, the palette row (v), the particle's view depth (s&box units, for depth fade).
	float4 vExtra : TANGENT < Semantic( None ); >;
};

struct PixelInput
{
	float4 vPositionPs : SV_Position;
	float4 vFrameUv : TEXCOORD0;
	float4 vColor : TEXCOORD1;
	float3 vBillboard : TEXCOORD2;
	float4 vExtra : TEXCOORD3;
	float3 vPositionWs : TEXCOORD4;
};

VS
{
	PixelInput MainVs( VertexInput v )
	{
		PixelInput o = (PixelInput)0;
		o.vPositionPs = Position3WsToPs( v.vPosition );
		o.vPositionWs = v.vPosition;
		o.vFrameUv = v.vFrameUv;
		o.vColor = v.vColor;
		o.vBillboard = v.vBillboard;
		o.vExtra = v.vExtra;
		return o;
	}
}

PS
{
	#include "common/pixel.hlsl"

	StaticCombo( S_BLEND_MODE, F_BLEND_MODE, Sys( ALL ) );

	RenderState( CullMode, NONE );
	RenderState( DepthWriteEnable, false );
	RenderState( BlendEnable, true );
	RenderState( SrcBlendAlpha, ZERO );
	RenderState( DstBlendAlpha, ONE );
	RenderState( BlendOp, ADD );

	#if S_BLEND_MODE == 1 || S_BLEND_MODE == 6 || S_BLEND_MODE == 7 || S_BLEND_MODE == 8
		// Additive kinds: the shader works out what's added.
		RenderState( SrcBlend, ONE );
		RenderState( DstBlend, ONE );
	#elif S_BLEND_MODE == 2
		RenderState( SrcBlend, DEST_COLOR );
		RenderState( DstBlend, ZERO );
	#elif S_BLEND_MODE == 4
		RenderState( SrcBlend, DEST_COLOR );
		RenderState( DstBlend, SRC_COLOR );
	#elif S_BLEND_MODE == 5
		RenderState( SrcBlend, ONE );
		RenderState( DstBlend, ONE );
		RenderState( BlendOp, MAX );
	#elif S_BLEND_MODE == 9
		RenderState( SrcBlend, INV_SRC_ALPHA );
		RenderState( DstBlend, SRC_ALPHA );
	#else
		// Opaque, alpha blend and premultiplied: premultiplied output.
		RenderState( SrcBlend, ONE );
		RenderState( DstBlend, INV_SRC_ALPHA );
	#endif

	SamplerState g_sWrap < Filter( Bilinear ); AddressU( WRAP ); AddressV( WRAP ); >;
	SamplerState g_sClamp < Filter( Bilinear ); AddressU( CLAMP ); AddressV( CLAMP ); >;

	CreateInputTexture2D( BaseMap, Linear, 8, "None", "_color", ",0/,0/0", Default4( 1.00, 1.00, 1.00, 1.00 ) );
	Texture2D g_tBaseMap < Channel( RGBA, Box( BaseMap ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( false ); >;
	CreateInputTexture2D( Palette, Linear, 8, "None", "_color", ",0/,0/1", Default4( 1.00, 1.00, 1.00, 1.00 ) );
	Texture2D g_tPalette < Channel( RGBA, Box( Palette ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( false ); >;
	CreateInputTexture2D( AlphaMap, Linear, 8, "None", "_color", ",0/,0/2", Default4( 1.00, 1.00, 1.00, 1.00 ) );
	Texture2D g_tAlphaMap < Channel( RGBA, Box( AlphaMap ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( false ); >;

	// 1 where MCC samples the texture through an sRGB view.
	float g_flBaseMapSrgb < Default( 0.0 ); >;
	float g_flPaletteSrgb < Default( 0.0 ); >;
	float g_flAlphaMapSrgb < Default( 0.0 ); >;

	// albedo: 0 diffuse_only, 1 diffuse_plus_billboard_alpha, 2 palettized, 3 palettized_plus_billboard_alpha, 4 diffuse_plus_sprite_alpha, 5 palettized_plus_sprite_alpha.
	float g_flAlbedoMode < Default( 0.0 ); >;
	float g_flFrameBlend < Default( 0.0 ); >;
	float g_flBlackPoint < Default( 0.0 ); >;

	// Depth fade range (s&box units; 0 off).
	float g_flDepthFadeRange < Default( 0.0 ); >;

	// Self illumination constant colour (linear HDR), added to the colour.
	float3 g_vSelfIllumColor < Default3( 0.0, 0.0, 0.0 ); >;

	// Exposure (stops) and tone curve, as halo3_shader's H3FinishColor; Halo3ExposureOverride overrides the stops for the scene.
	float g_flExposureStops < Default( 1.0 ); >;
	float2 g_vExposureOverride < Attribute( "Halo3ExposureOverride" ); Default2( 0.0, 0.0 ); >;
	float4 g_vToneCurve < Attribute( "Halo3ToneCurve" ); Default4( 1.0, 1.5, 0.0, -0.5 ); >;
	// As halo3_shader: 1 while Halo 3's post-processing runs, which takes x = exposed colour and does the tone curve itself, with the exposure from its auto exposure.
	float g_flHdr < Attribute( "Halo3Hdr" ); Default( 0.0 ); >;
	float g_flExposureState < Attribute( "Halo3ExposureStateIndex" ); Default( -1.0 ); >;

	float3 H3SrgbToLinear( float3 c )
	{
		c = saturate( c );
		return lerp( pow( ( c + 0.055 ) / 1.055, 2.4 ), c / 12.92, step( c, 0.04045 ) );
	}

	float4 Read( Texture2D t, float srgb, float2 uv )
	{
		float4 c = t.Sample( g_sWrap, uv );
		if ( srgb > 0.5 )
			c.rgb = H3SrgbToLinear( c.rgb );
		return c;
	}

	// Palettes and billboard alpha maps cover 0-1 once.
	float4 ReadClamped( Texture2D t, float srgb, float2 uv )
	{
		float4 c = t.Sample( g_sClamp, uv );
		if ( srgb > 0.5 )
			c.rgb = H3SrgbToLinear( c.rgb );
		return c;
	}

	// MCC's particles expose additive colours and self-illumination by the camera fx's self-illumination exposure (g_alt_exposure.y; Sandtrap's jump pad smoke 0.705 against the scene's 0.796); 1 for prt3 particles, 0 for light volumes.
	float g_flAltExposure < Default( 0.0 ); >;
	float2 g_vSelfIllumExposure < Attribute( "Halo3SelfIllumExposure" ); Default2( 0.0, 1.0 ); >;

	float H3Stops()
	{
		return g_vExposureOverride.y > 0.5 ? g_vExposureOverride.x : g_flHdr > 0.5 && g_flExposureState >= 0 ? Bindless::GetTexture2D( (int)round( g_flExposureState ) ).Load( int3( 0, 0, 0 ) ).x : g_flExposureStops;
	}

	// The self-illumination exposure relative to the scene's, as halo3_shader's H3SelfIllumExposure.
	float H3AltExposure()
	{
		if ( g_flAltExposure < 0.5 )
			return 1;
		float stops = H3Stops();
		return exp2( g_vSelfIllumExposure.x + g_vSelfIllumExposure.y * ( stops - g_vSelfIllumExposure.x ) - stops );
	}

	float3 H3FinishColor( float3 c )
	{
		float stops = H3Stops();
		// g_exposure.x = 2^stops / 1.4938, the tone curve's white point.
		float3 x = max( c * exp2( stops ) / 1.4938016, 0 );
		if ( g_flHdr > 0.5 )
			return min( x, 128 );
		x = min( saturate( x ), g_vToneCurve.x );
		return x * ( g_vToneCurve.y + x * ( g_vToneCurve.z + x * g_vToneCurve.w ) ) * ( 15.0 / 16.0 );
	}

	float4 ParticleAlbedo( float2 uv, float2 billboard, float paletteRow )
	{
		int mode = (int)round( g_flAlbedoMode );
		float4 base = Read( g_tBaseMap, g_flBaseMapSrgb, uv );
		float4 c = base;
		if ( mode == 2 || mode == 3 || mode == 5 )
			c = ReadClamped( g_tPalette, g_flPaletteSrgb, float2( base.r, paletteRow ) );
		if ( mode == 1 || mode == 3 )
			c.a = ReadClamped( g_tAlphaMap, g_flAlphaMapSrgb, billboard ).a;
		else if ( mode == 4 || mode == 5 )
			c.a = Read( g_tAlphaMap, g_flAlphaMapSrgb, uv ).a;
		return c;
	}

	float4 MainPs( PixelInput i ) : SV_Target0
	{
		float paletteRow = i.vExtra.y;
		float4 albedo = ParticleAlbedo( i.vFrameUv.xy, i.vBillboard.xy, paletteRow );
		if ( g_flFrameBlend > 0.5 && i.vBillboard.z > 0 )
			albedo = lerp( albedo, ParticleAlbedo( i.vFrameUv.zw, i.vBillboard.xy, paletteRow ), saturate( i.vBillboard.z ) );

		float alpha = albedo.a;

		// Depth fade: how far the scene is behind the particle, over the fade range. Over the sky (Blackout's ocean and ice are sky geometry at a pinned, near-zero depth) there is nothing to fade against: its reconstructed position is at infinity or not a number, which hid depth-faded particles everywhere but over the level's own geometry (a chimney-shaped white blob on Blackout).
		if ( g_flDepthFadeRange > 0 && Depth::GetNormalized( i.vPositionPs.xy ) > 1e-5 )
		{
			float3 scene = Depth::GetWorldPosition( i.vPositionPs.xy );
			float behind = dot( scene - i.vPositionWs, normalize( i.vPositionWs - g_vCameraPositionWs ) );
			if ( !isnan( behind ) && !isinf( behind ) )
				alpha *= saturate( behind / g_flDepthFadeRange );
		}

		// Nothing that isn't a number reaches the blend.
		if ( isnan( alpha ) )
			alpha = 0;

		// The black point curve: below the black point nothing, the rest stretched to reach 1 at the half way point above it, then the remainder added.
		if ( g_flBlackPoint > 0.5 )
		{
			float bp = saturate( i.vExtra.x );
			float half_ = ( 1 + bp ) * 0.5;
			alpha = half_ * saturate( ( alpha - bp ) / max( half_ - bp, 1e-4 ) ) + saturate( alpha - half_ );
		}

		alpha = saturate( alpha * i.vColor.a );
		float alt = H3AltExposure();
		#if S_BLEND_MODE == 1 || S_BLEND_MODE == 8
			float3 color = ( albedo.rgb * i.vColor.rgb + g_vSelfIllumColor ) * alt;
		#else
			float3 color = albedo.rgb * i.vColor.rgb + g_vSelfIllumColor * alt;
		#endif

		#if S_BLEND_MODE == 2
			return float4( lerp( 1.0, saturate( albedo.rgb * i.vColor.rgb ), alpha ), 1 );
		#elif S_BLEND_MODE == 4
			return float4( lerp( 0.5, saturate( albedo.rgb * i.vColor.rgb ), alpha ), 1 );
		#elif S_BLEND_MODE == 10
			// Pre-multiplied: the colour already carries its coverage.
			return float4( H3FinishColor( color * alpha ), alpha );
		#elif S_BLEND_MODE == 1
			// Additive: the particle's alpha (its fades) scales what's added.
			return float4( H3FinishColor( color * alpha ), 0 );
		#elif S_BLEND_MODE == 6 || S_BLEND_MODE == 7 || S_BLEND_MODE == 8
			return float4( H3FinishColor( color * alpha ), 0 );
		#elif S_BLEND_MODE == 5
			return float4( H3FinishColor( color * alpha ), 0 );
		#elif S_BLEND_MODE == 9
			return float4( H3FinishColor( color ), alpha );
		#elif S_BLEND_MODE == 0
			return float4( H3FinishColor( color ), 1 );
		#else
			return float4( H3FinishColor( color ) * alpha, alpha );
		#endif
	}
}
