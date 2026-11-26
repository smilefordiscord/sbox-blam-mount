//
// Halo 2's transparent shader templates: glass, visors and windows (the "transparent generic" family:
// one_alpha_env, one_alpha_env_illum, one_add_env_illum, two_alpha_env_illum, two_add_env_illum and their
// variants), lights and light beams (one_add_illum, one_add_illum_detail, add_illum_detail), holograms
// (two_add_detail_masked), gas and clouds (one_add_two_plus_two, two_alpha_clouds, two_add_clouds), waves, meters,
// the sky's layers, the bump-mapped reflection passes that follow a first pass (Cortana's sheen, some glass), and the
// one lit template (brute hair: the object's direct light and change colours).
//
// Each program is a transliteration of the template pass's pixel shader in MCC (scratch research notes,
// shader_templates_other.md sections 3-5), in Halo's gamma space with its 8-bit clamps, converted to linear at
// the end. The parameters arrive the way Halo 2 hands them to its pixel shaders: textures by texture stage
// (g_tMap0-3 = t0-t3; on the "reflection" programs t0 is the environment cube map), constants by register
// (g_vC1-7 = c1-c7, each shader slot routed to its register and component by the template, see
// Halo2/TemplatePassParameters.cs), and stage n is always read at texture coordinate set n, Halo's 2D
// transforms of the mesh UV (g_vUvNU/V rows).
//
// The vertex side is done per pixel: the fresnel pair (head-on "perpendicular" colour and opacity in g_vV10, the
// grazing "parallel" ones in g_vV11) is blended by N.E with the real view vector, and the reflection vector is
// 2(E.N)N - E in the world, which is also the cube map's frame. Halo doesn't flip the normal of two-sided
// surfaces seen from behind, so their back faces get the grazing values, as here.
//
// Texture scrolling: up to two time curves per texture coordinate set (u and v offsets) are evaluated here,
// Halo's function evaluator for identity, transition, periodic, linear and exponent curves (the shapes are
// approximations). Three pixel constants can follow a time curve too (colour pulses). Curves driven by object
// functions keep the shader's stored value.
//
// Halo fades transparent surfaces by its fog's transmittance without adding fog colour, and its full-screen fog
// pass then fogs them by the depth of what's behind; here they are fogged by their own depth with Halo's
// atmospheric fog (HaloFogAmount, as in halo2_opaque.shader): additive light is faded, what covers the
// background is fogged like the background.
// Halo adds light in its gamma space frame buffer, much brighter over a lit background than the same colour added
// in linear space. Additive passes off the sky (F_GAMMA_ADD) take the sum in gamma space against the frame buffer
// copy and add the difference (see halo2_plasma.shader); the sky's layers stack on each other, which the copy
// (taken before translucent objects draw) can't see, and render into the water's reflection cube.
// Templates for camouflaged weapons (*_active_camo) fade out with the object's HaloCamo attribute (0-1).
// The overlay templates (overlay, decal_simple) are opaque templates drawn in Halo's overlay layer: markings, labels
// and grime over the surface they lie on, blended DESTCOLOR / SRCCOLOR (the frame times twice the colour, grey
// leaves it as is) in gamma space, which is the linear frame times ( 2 colour )^2.2 (F_BLEND_MODE 4), and pulled a
// little toward the camera (g_flHaloDepthBias) for Halo's depth bias over the surface they share.
//

HEADER
{
	Description = "Halo 2 transparent shader";
}

FEATURES
{
	#include "common/features.hlsl"

	// From the pass's D3D9 blend factors: premultiplied ONE / INVSRCALPHA (most glass), ONE / ONE (lights),
	// SRCALPHA / INVSRCALPHA (clouds, gas, waves, meters), SRCALPHA / ONE, DESTCOLOR / SRCCOLOR (overlays).
	Feature( F_BLEND_MODE, 0..4( 0="Premultiplied", 1="Additive", 2="Alpha Blend", 3="Alpha Additive", 4="Double Multiply" ), "Blending" );
	Feature( F_TWO_SIDED, 0..1, "Rendering" );
	Feature( F_GAMMA_ADD, 0..1, "Blending" );
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

	// 1 on the sky's layers: drawn at the far plane, as MCC's sky vertex shaders do, and never fogged.
	float g_flHaloSky < Default( 0.0 ); >;
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

	// Overlays: the fraction of its distance a vertex is pulled toward the camera (s&box's depth is reversed, so
	// scaling z by 1 + bias brings the depth that much nearer), to draw over the surface it lies on.
	float g_flHaloDepthBias < Default( 0.0 ); >;

	PixelInput MainVs( VertexInput v )
	{
		PixelInput i = ProcessVertex( v );
		i = FinalizeVertex( i );
		i.vPositionPs.z *= 1 + g_flHaloDepthBias;

		// The sky at the far plane (depth 0, s&box's depth is reversed): behind everything, however far the
		// camera's far plane is (the sky's models reach hundreds of thousands of units out).
		if ( g_flHaloSky > 0.5 )
			i.vPositionPs.z = 0;

		return i;
	}
}

PS
{
	#include "common/pixel.hlsl"

	StaticCombo( S_BLEND_MODE, F_BLEND_MODE, Sys( ALL ) );
	StaticCombo( S_TWO_SIDED, F_TWO_SIDED, Sys( ALL ) );
	StaticCombo( S_GAMMA_ADD, F_GAMMA_ADD, Sys( ALL ) );

	// Only the additive modes add in gamma space.
	#define GAMMA_ADD ( S_GAMMA_ADD && ( S_BLEND_MODE == 1 || S_BLEND_MODE == 3 ) )

	RenderState( CullMode, S_TWO_SIDED ? NONE : DEFAULT );
	RenderState( DepthWriteEnable, false );
	RenderState( BlendEnable, true );
	RenderState( BlendOp, ADD );
	RenderState( SrcBlendAlpha, ZERO );
	RenderState( DstBlendAlpha, ONE );

	#if S_BLEND_MODE == 0
		RenderState( SrcBlend, ONE );
		RenderState( DstBlend, INV_SRC_ALPHA );
	#elif S_BLEND_MODE == 1
		RenderState( SrcBlend, ONE );
		RenderState( DstBlend, ONE );
	#elif S_BLEND_MODE == 2
		RenderState( SrcBlend, SRC_ALPHA );
		RenderState( DstBlend, INV_SRC_ALPHA );
	#elif S_BLEND_MODE == 4
		RenderState( SrcBlend, DEST_COLOR );
		RenderState( DstBlend, ZERO );
	#else
		RenderState( SrcBlend, SRC_ALPHA );
		RenderState( DstBlend, ONE );
	#endif

	#if GAMMA_ADD
		BoolAttribute( bWantsFBCopyTexture, true );
	#endif

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

	// 1 on a sky layer drawn while the frame is in Halo's gamma space (Halo2/SkyGamma.cs): it writes gamma space
	// colours, which its blending then mixes as Halo's does.
	float g_flHaloSkyGamma < Attribute( "HaloSkyGamma" ); Default( 0.0 ); >;

	#if GAMMA_ADD
		Texture2D g_tFrameBufferCopyTexture < Attribute( "FrameBufferCopyTexture" ); SrgbRead( false ); >;
		SamplerState g_sBehind < Filter( Point ); AddressU( CLAMP ); AddressV( CLAMP ); >;

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
	#endif

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

	// Which program (TransparentMaterial.Program), and its variants.
	float g_flProgram < Default( 0.0 ); >;
	float g_flFixedAlpha < Default( 0.0 ); >;
	float g_flThreeChannelIllum < Default( 0.0 ); >;
	float g_flCamoFade < Default( 0.0 ); >; // 1 camouflaged weapons' templates, 2 overlays on a camouflaged model

	// The object's active camouflage (0-1), as on its model.
	float g_flHaloCamo < Attribute( "HaloCamo" ); Default( 0.0 ); >;

	// s&box draws a translucent part of an overlay layer renderer twice onto the same frame, in the world's pass and over it.
	// A game sets HaloOverlay on such renderers (first person weapons), and each draw then does what makes the pair blend once:
	// half an add, the square root of a multiply, an alpha blend's cover left at the square root of what it leaves.
	float g_flHaloOverlay < Attribute( "HaloOverlay" ); Default( 0.0 ); >;

	float4 HalfOfTwoDraws( float4 c )
	{
		#if S_BLEND_MODE == 4
			return float4( sqrt( c.rgb ), c.a );
		#elif S_BLEND_MODE == 0
			float keep = sqrt( saturate( 1 - c.a ) );
			return float4( c.rgb / ( 1 + keep ), 1 - keep );
		#elif S_BLEND_MODE == 2
			return float4( c.rgb, 1 - sqrt( saturate( 1 - c.a ) ) );
		#else
			return float4( 0.5 * c.rgb, c.a );
		#endif
	}

	// The lit template (transparent_lit_alpha_blend_two_change_color, brute hair) reads its object's direct light and
	// change colours: from the object's row of the object table (the renderer tint holds it), as halo2_opaque.shader
	// does (Halo2/ObjectLightingTable.cs), else the fallback light and the default variant's colours.
	float g_flObjectCount < Default( 0.0 ); >;
	float3 g_vFallbackDirect < Default3( 0.3, 0.3, 0.3 ); >;
	CreateInputTexture2D( ObjectTable, Linear, 8, "None", "_color", ",0/,0/0", Default4( 0.00, 0.00, 0.00, 0.00 ) );
	Texture2D g_tObjectTable < Channel( RGBA, Box( ObjectTable ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( false ); >;

	float4 ObjectTexel( int index, int texel )
	{
		// ObjectLightingTable.ObjectsPerRow and TexelsPerObject.
		return g_tObjectTable.Load( int3( ( index % 128 ) * 17 + texel, index / 128, 0 ) );
	}

	// The object's row from its renderer tint (one byte of the index per channel), or -1 without one.
	int ObjectIndex( float3 tint )
	{
		float3 bytes = round( saturate( tint ) * 255.0 );
		int index = (int)( bytes.r + bytes.g * 256.0 + bytes.b * 65536.0 + 0.5 );
		if ( index >= 1 && index < (int)g_flObjectCount && abs( ObjectTexel( index, 4 ).w - index ) < 0.5 )
			return index;
		return -1;
	}

	// The primary and secondary change colours exactly as halo2_opaque.shader's ChangeColor (see there).
	float4 g_vHaloChangeColor0 < Attribute( "HaloChangeColor0" ); Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float4 g_vHaloChangeColor1 < Attribute( "HaloChangeColor1" ); Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float4 g_vVariantChangeColor0 < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float4 g_vVariantChangeColor1 < Default4( 0.0, 0.0, 0.0, 0.0 ); >;

	float3 ChangeColor( int k, int index )
	{
		float4 given = k == 0 ? g_vHaloChangeColor0 : g_vHaloChangeColor1;
		if ( given.a > 0.5 )
			return given.rgb;
		float4 variant = k == 0 ? g_vVariantChangeColor0 : g_vVariantChangeColor1;
		if ( variant.a > 1.5 )
			return variant.rgb;
		if ( index < 0 )
			return variant.a > 0.5 ? variant.rgb : 1;
		float4 stored = ObjectTexel( index, 13 + k );
		return stored.a > 0.5 ? stored.rgb : 0;
	}

	// Emblems (emblem_overlay_simple, Halo2/Emblems.cs): the picture (an image of the emblem bitmap, laid out in a
	// grid: columns, rows, image count), whether its second region shows, whether it's drawn at all, and its two
	// colours (the object's tertiary and quaternary change colours) as a game sets them on the renderer, else the
	// object's stored ones, else the defaults.
	float4 g_vEmblemGrid < Default4( 1.0, 1.0, 1.0, 0.0 ); >;
	float g_flHaloEmblemForeground < Attribute( "HaloEmblemForeground" ); Default( 0.0 ); >;
	float g_flHaloEmblemToggle < Attribute( "HaloEmblemToggle" ); Default( 1.0 ); >;
	float g_flHaloEmblemActive < Attribute( "HaloEmblemActive" ); Default( 1.0 ); >;
	float4 g_vHaloChangeColor2 < Attribute( "HaloChangeColor2" ); Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float4 g_vHaloChangeColor3 < Attribute( "HaloChangeColor3" ); Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float3 g_vEmblemDefaultColor0 < Default3( 1.0, 1.0, 1.0 ); >;
	float3 g_vEmblemDefaultColor1 < Default3( 0.32, 0.32, 0.32 ); >;

	float3 EmblemColor( int k, int index )
	{
		float4 given = k == 0 ? g_vHaloChangeColor2 : g_vHaloChangeColor3;
		if ( given.a > 0.5 )
			return given.rgb;
		float4 stored = index >= 0 ? ObjectTexel( index, 15 + k ) : 0;
		return stored.a > 0.5 ? stored.rgb : ( k == 0 ? g_vEmblemDefaultColor0 : g_vEmblemDefaultColor1 );
	}

	// MCC draws the emblem at 2/3 of the image's size, about the image's centre: with the foreground bitmap in the
	// shader MCC's picture is 1.5x smaller than the bitmap's image would be at the armour's UVs (same centre, on the
	// back plate and the shoulder alike), while any other bitmap poked into the slot fills the UVs as they are. So
	// MCC supplies its own emblem picture, inset; the bitmap's image stands in for it here, inset the same.
	static const float EmblemInset = 1.5;

	// The emblem image's texel at uv (0-1; outside it, Halo's border colour: nothing).
	float4 SampleEmblem( float2 uv )
	{
		uv = ( uv - 0.5 ) * EmblemInset + 0.5;
		if ( any( uv < 0 ) || any( uv > 1 ) )
			return 0;

		float count = max( g_vEmblemGrid.z, 1 );
		float image = clamp( floor( g_flHaloEmblemForeground + 0.5 ), 0, count - 1 );
		float2 cell = float2( fmod( image, g_vEmblemGrid.x ), floor( image / g_vEmblemGrid.x ) );

		// Kept half a texel inside its cell so filtering doesn't reach the neighbours.
		float2 size;
		g_tMap0.GetDimensions( size.x, size.y );
		float2 inset = 0.5 * g_vEmblemGrid.xy / size;
		float2 atlas = ( cell + clamp( uv, inset, 1 - inset ) ) / g_vEmblemGrid.xy;
		return saturate( g_tMap0.SampleGrad( g_sClamp, atlas, ddx( uv ) / g_vEmblemGrid.xy, ddy( uv ) / g_vEmblemGrid.xy ) );
	}

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

	// Cube maps arrive as a 2D atlas of six faces side by side (+X, -X, +Y, -Y, +Z, -Z), and the face and
	// position within it are picked with Direct3D's cube map rules. Same lookup as the Halo CE shaders.
	float3 SampleCubeAtlas( Texture2D atlas, float3 dir )
	{
		float3 a = abs( dir );
		float face;
		float2 st;
		float ma;

		if ( a.x >= a.y && a.x >= a.z )
		{
			ma = a.x;
			face = dir.x > 0 ? 0 : 1;
			st = dir.x > 0 ? float2( -dir.z, -dir.y ) : float2( dir.z, -dir.y );
		}
		else if ( a.y >= a.z )
		{
			ma = a.y;
			face = dir.y > 0 ? 2 : 3;
			st = dir.y > 0 ? float2( dir.x, dir.z ) : float2( dir.x, -dir.z );
		}
		else
		{
			ma = a.z;
			face = dir.z > 0 ? 4 : 5;
			st = dir.z > 0 ? float2( dir.x, -dir.y ) : float2( -dir.x, -dir.y );
		}

		// Keep away from the face edges so filtering doesn't pick up the neighbouring face.
		float2 uv = clamp( st / max( ma, 1e-6 ) * 0.5 + 0.5, 0.01, 0.99 );
		return saturate( atlas.SampleLevel( g_sClamp, float2( ( face + uv.x ) / 6.0, uv.y ), 0 ).rgb );
	}

	// Halo 2's atmospheric fog, as in halo2_opaque.shader (amount by view depth; none on the sky).
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

		// The eye vector and the fresnel pair: lerp( grazing, head on, max( N.E, 0 ) ), per pixel.
		float3 normal = normalize( i.vNormalWs );
		float3 toCamera = normalize( -i.vPositionWithOffsetWs.xyz );
		float facing = max( dot( toCamera, normal ), 0 );
		float4 fr = saturate( facing * g_vV10 + ( 1 - facing ) * g_vV11 );
		float3 reflection = 2 * dot( toCamera, normal ) * normal - toCamera;

		// Halo's fog transmittance; s&box's atmospherics are applied below instead.
		float fog = 1;

		float4 o = 0;
		int program = (int)( g_flProgram + 0.5 );

		// A premultiplied colour taken to linear by its parts (colour, then times its coverage), where o.rgb's
		// premultiplied gamma value as one would be darkened by coverage^2.2; negative where unused.
		float3 linearPremultiplied = -1;

		if ( program == 0 )
		{
			// Generic: reflection masked by t1 plus self-illumination, added on top of a premultiplied
			// diffuse layer (t3, alpha = opacity); the added part isn't weighted by the opacity.
			float3 illum;
			if ( g_flThreeChannelIllum > 0.5 )
			{
				// Viewscreens: a channel-packed illumination map, each channel its own colour.
				float3 m = SampleMap( g_tMap2, g_vMapClamp.z, uv2 ).rgb;
				float3 rg = saturate( saturate( m.r * c[3].x ) * c[5].rgb + saturate( m.g * c[4].y ) * c[6].rgb );
				illum = min( 1, rg + m.b * saturate( c[7].w * c[7].rgb ) );
			}
			else
			{
				illum = SampleMap( g_tMap2, g_vMapClamp.z, uv2 ).rgb * c[3].rgb;
			}

			float3 env = saturate( SampleCubeAtlas( g_tMap0, reflection ) * c[1].rgb );
			float3 added = saturate( env * SampleMap( g_tMap1, g_vMapClamp.y, uv1 ).rgb + illum );
			float4 base = saturate( SampleMap( g_tMap3, g_vMapClamp.w, uv3 ) * c[2] );
			float a = base.a * fog;
			float3 rgb = min( 1, added * fog + base.rgb * a );
			o = float4( rgb * fr.rgb * fr.a, g_flFixedAlpha > 0.5 ? a : a * fr.a );
		}
		else if ( program == 1 )
		{
			// No fog (one_add_illum_no_fog lights): no reflection.
			float3 t = saturate( SampleMap( g_tMap2, g_vMapClamp.z, uv2 ).rgb * c[1].rgb );
			float4 b = saturate( SampleMap( g_tMap3, g_vMapClamp.w, uv3 ) * c[2] );
			o = float4( min( 1, b.rgb * b.a + t ) * fr.rgb * fr.a, b.a * fr.a );
		}
		else if ( program == 2 )
		{
			// Detail (two_alpha_env_detail): the reflection weighted by the opacity this time.
			float3 env = min( 1, 2 * saturate( SampleCubeAtlas( g_tMap0, reflection ) * c[1].rgb ) * SampleMap( g_tMap1, g_vMapClamp.y, uv1 ).rgb ) * fog;
			float4 b = saturate( SampleMap( g_tMap3, g_vMapClamp.w, uv3 ) * c[2] );
			float a = fog * b.a;
			float3 diffuse = min( 1, 2 * SampleMap( g_tMap2, g_vMapClamp.z, uv2 ).rgb * b.rgb ) * fog * a;
			float3 rgb = min( 1, env * a + diffuse );
			o = float4( rgb * fr.rgb * fr.a, a * fr.a );
		}
		else if ( program == 3 )
		{
			// Multichannel (vehicle glass): t1 packs diffuse (r), detail mask (g), opacity (b) and reflection
			// mask (a); t2 is a detail map whose blue also scales the result (as in MCC's port).
			float4 m = SampleMap( g_tMap1, g_vMapClamp.y, uv1 );
			float4 d = SampleMap( g_tMap2, g_vMapClamp.z, uv2 );
			float envMask = min( 1, d.a + 1 - m.g ) * m.a;
			float3 detail = min( 1, lerp( 0.5, d.rgb, m.g ) );
			float3 diffuse = min( 1, 2 * detail * saturate( 2 * m.r * c[5].rgb ) ) * fog;
			float a = fog * saturate( m.b * c[4].w );
			float3 env = min( 1, 2 * envMask * saturate( SampleCubeAtlas( g_tMap0, reflection ) * c[3].rgb ) ) * fog;
			float3 rgb = min( 1, env * a + diffuse * a ) * fr.rgb;
			o = float4( rgb * detail.b * fr.a, a * detail.b * fr.a );
		}
		else if ( program == 4 )
		{
			// one_add_illum: a light's map times its colour.
			float4 t = SampleMap( g_tMap1, g_vMapClamp.y, uv1 );
			o = float4( saturate( t.rgb * c[1].rgb ) * fr.rgb * fr.a, t.a * fr.a );
		}
		else if ( program == 5 )
		{
			// one_add_illum_detail: light volumes and beams, a scrolling detail (dust, clouds) over them.
			float4 t = SampleMap( g_tMap1, g_vMapClamp.y, uv1 );
			float a = t.a * fr.a;
			clip( a > 0 ? 1 : -1 );
			float3 col = saturate( t.rgb * c[1].rgb ) * a;
			float3 detail = SampleMap( g_tMap2, g_vMapClamp.z, uv2 ).rgb;
			o = float4( min( 1, 2 * col * detail ) * fr.rgb, a );
		}
		else if ( program == 22 )
		{
			// default_self_illumination (illum, illum_clamped): the glow map times its colour, added.
			float3 col = saturate( SampleMap( g_tMap0, g_vMapClamp.x, uv0 ).rgb * c[1].rgb );
			float a = min( 1, dot( col, col ) );
			clip( a > 0 ? 1 : -1 );
			o = float4( col, a );
		}
		else if ( program == 6 )
		{
			// add_illum_detail (self-illumination with detail, single pass): no fresnel.
			float3 col = saturate( min( 1, 2 * SampleMap( g_tMap0, g_vMapClamp.x, uv0 ).rgb * SampleMap( g_tMap1, g_vMapClamp.y, uv1 ).rgb ) * c[1].rgb );
			float a = min( 1, dot( col, col ) );
			clip( a > 0 ? 1 : -1 );
			o = float4( col, a );
		}
		else if ( program == 7 )
		{
			// two_plus_two (containment gas): three maps, each scaled and biased, multiplied.
			float4 m0 = SampleMap( g_tMap0, g_vMapClamp.x, uv0 );
			float4 m1 = SampleMap( g_tMap1, g_vMapClamp.y, uv1 );
			float4 m2 = SampleMap( g_tMap2, g_vMapClamp.z, uv2 );
			float3 rgb = saturate( m0.rgb * c[1].w + c[2].w ) * saturate( m1.rgb * c[3].w + c[4].w ) * saturate( m2.rgb * c[5].w + c[6].w );
			o = float4( rgb, m0.a * m1.a * m2.a * saturate( c[7].w ) );
		}
		else if ( program == 8 || program == 9 )
		{
			// detail_masked (holograms): base times a scrolling static, plus an overlay where the base's alpha
			// is, masked by a fourth map.
			float4 b = SampleMap( g_tMap0, g_vMapClamp.x, uv0 );
			float3 detail = min( 1, 2 * SampleMap( g_tMap1, g_vMapClamp.y, uv1 ).rgb * b.rgb );
			float3 overlay = SampleMap( g_tMap2, g_vMapClamp.z, uv2 ).rgb * b.a;
			float3 m = min( 1, lerp( 1, SampleMap( g_tMap3, g_vMapClamp.w, uv3 ).rgb, b.a ) );
			if ( program == 8 )
			{
				float3 col = min( 1, detail + saturate( overlay * c[2].rgb ) );
				o = float4( col * m * saturate( fr.a * c[1].w ) * fr.rgb, 1 );
			}
			else
			{
				// The alpha version blends with a constant blend factor, taken as 1 (premultiplied).
				float3 col = min( 1, overlay + detail );
				o = float4( col * m * saturate( fr.a * c[1].w ) * b.a * fr.rgb, b.a );
			}
		}
		else if ( program == 10 )
		{
			// clouds_simple: two maps multiplied, alpha blended; no fresnel, no fog.
			o = SampleMap( g_tMap1, g_vMapClamp.y, uv1 ) * SampleMap( g_tMap2, g_vMapClamp.z, uv2 );
		}
		else if ( program == 11 || program == 12 )
		{
			// add_clouds: two maps multiplied and doubled, added (the sky's without the fresnel).
			float3 rgb = min( 1, 2 * SampleMap( g_tMap2, g_vMapClamp.z, uv2 ).rgb * SampleMap( g_tMap1, g_vMapClamp.y, uv1 ).rgb );
			o = program == 11 ? float4( rgb * saturate( fr.a * c[1].w ) * fr.rgb, 0 ) : float4( rgb * saturate( c[1].w ), 0 );
		}
		else if ( program == 13 )
		{
			// waves: two scrolling layers masked by t0, coloured by t3.
			float4 m = SampleMap( g_tMap0, g_vMapClamp.x, uv0 );
			float4 w = min( 1, m * ( SampleMap( g_tMap1, g_vMapClamp.y, uv1 ) + SampleMap( g_tMap2, g_vMapClamp.z, uv2 ) ) );
			o = float4( w.rgb * SampleMap( g_tMap3, g_vMapClamp.w, uv3 ).rgb * fr.rgb, saturate( fr.a * c[1].w ) * w.a );
		}
		else if ( program == 14 )
		{
			// meter: lit up to the meter value (c1.w) along the map's alpha.
			float4 m = SampleMap( g_tMap1, g_vMapClamp.y, uv1 );
			bool on = m.a - c[1].w + 0.50196 >= 0.5;
			float4 color = on ? c[4] : c[3];
			o = float4( m.rgb * color.rgb, SampleMap( g_tMap2, g_vMapClamp.z, uv2 ).a * color.a );
		}
		else if ( program == 15 )
		{
			// The sky's layers (sky_default_transparent_one_sided): premultiplied, no fresnel. Halo blends them in its
			// gamma space frame, as SkyGamma does. Where the frame is linear instead (a sky model on its own), the
			// layer's colour goes to linear before its coverage multiplies it: converting the premultiplied value (as
			// below for the rest) darkened soft edges by coverage^2.2 while the frame behind still dimmed by
			// 1 - coverage, a dark seam where a layer fades out over a like-coloured one.
			float3 env = saturate( SampleCubeAtlas( g_tMap0, reflection ) * c[1].rgb );
			float3 added = saturate( env * SampleMap( g_tMap1, g_vMapClamp.y, uv1 ).rgb + SampleMap( g_tMap2, g_vMapClamp.z, uv2 ).rgb * c[3].rgb ) * fog;
			float4 b = saturate( SampleMap( g_tMap3, g_vMapClamp.w, uv3 ) * c[2] );
			o = float4( b.rgb * b.a + added, b.a );
			linearPremultiplied = SrgbGammaToLinear( b.rgb ) * b.a + SrgbGammaToLinear( added );
		}
		else if ( program == 16 )
		{
			// sky_one_add_illum_detail.
			float4 t = SampleMap( g_tMap1, g_vMapClamp.y, uv1 );
			float3 detail = SampleMap( g_tMap2, g_vMapClamp.z, uv2 ).rgb;
			o = float4( 2 * saturate( fr.rgb * fr.a ) * c[1].rgb * t.rgb * t.a * detail, t.a * fr.a );
		}
		else if ( program == 17 )
		{
			// overlay: the overlay map times a detail map, doubled, tinted by c1; where the change colour mask (t2)
			// is, by the object's primary change colour too (COLOR0, not given here: white, so the mask does nothing).
			float3 mask = SampleMap( g_tMap2, g_vMapClamp.z, uv2 ).rgb;
			float3 primary = 1;
			float3 changeColour = min( 1, mask * primary - mask + 1 );
			float3 overlay = min( 1, 2 * SampleMap( g_tMap0, g_vMapClamp.x, uv0 ).rgb * SampleMap( g_tMap1, g_vMapClamp.y, uv1 ).rgb );
			o = float4( changeColour * overlay * c[1].rgb, 0 );
		}
		else if ( program == 18 )
		{
			// decal_simple: the map tinted by c1 where its alpha is, neutral grey elsewhere.
			float4 t = SampleMap( g_tMap0, g_vMapClamp.x, uv0 );
			o = float4( min( 1, saturate( t.rgb * c[1].rgb ) * t.a + 0.501961 * ( 1 - t.a ) ), 0 );
		}
		else if ( program == 19 )
		{
			// bumped_environment_additive / _masked (shader_templates_other.md section 12): the cube map (t3) reflected
			// off the bump map's normal (t0, 2x - 1 as stored), tinted by the fresnel pair weighted by how much that
			// normal faces the camera, through a band colour (c3) around half facing (c2.w); the masked version's
			// opacity is the bump map's alpha.
			float4 nb = SampleMap( g_tMap0, g_vMapClamp.x, uv0 );
			float3 n = float3( 2 * nb.xy - 1, nb.z );
			float3 bumped = normalize( i.vTangentUWs ) * n.x + normalize( i.vTangentVWs ) * n.y + normal * n.z;
			float3 bumpedReflection = 2 * dot( bumped, toCamera ) / max( dot( bumped, bumped ), 1e-6 ) * bumped - toCamera;
			float f = saturate( dot( -normalize( bumped ), g_vCameraDirWs.xyz ) );
			float band = saturate( 2 * c[2].w * min( f, 1 - f ) );
			float4 tint = min( 1, f * saturate( g_vV10 ) + ( 1 - f ) * saturate( g_vV11 ) );
			o = float4( SampleCubeAtlas( g_tMap3, bumpedReflection ) * tint.a * saturate( ( 1 - band ) * tint.rgb + band * c[3].rgb ), saturate( nb.a ) );
		}
		else if ( program == 20 )
		{
			// transparent_lit_alpha_blend_two_change_color (brute hair, section 4): the map (t0) tinted by the primary
			// and secondary change colours where the mask (t1) has red and green, doubled and lit by the object's direct
			// light; premultiplied by the map's alpha. No fresnel.
			int objectIndex = ObjectIndex( i.vVertexColor.rgb );
			float3 direct = objectIndex >= 0 ? ObjectTexel( objectIndex, 1 ).rgb : g_vFallbackDirect;
			float2 cc = SampleMap( g_tMap1, g_vMapClamp.y, uv1 ).rg;
			float3 k = lerp( 1, ChangeColor( 0, objectIndex ), cc.r ) * lerp( 1, ChangeColor( 1, objectIndex ), cc.g );
			float4 b = SampleMap( g_tMap0, g_vMapClamp.x, uv0 );
			o = float4( saturate( 2 * b.rgb * k * direct ) * b.a, b.a );
		}
		else if ( program == 21 )
		{
			// emblem_overlay_simple (MCC's DXBC): the image's blue region in the second colour (c4), its red/green
			// region, when toggled on (c5.w), in the first colour (c3) and covering the rest; drawn when active (c6.w).
			int objectIndex = ObjectIndex( i.vVertexColor.rgb );
			float4 e = SampleEmblem( uv0 );
			float toggle = saturate( g_flHaloEmblemToggle );
			float t = saturate( e.g * toggle );
			float3 rgb = min( 1, ( 1 - t ) * saturate( e.b * EmblemColor( 1, objectIndex ) ) + saturate( saturate( e.r * EmblemColor( 0, objectIndex ) ) * toggle ) );
			o = float4( rgb, saturate( min( ( 1 - t ) * e.a + t, 1 ) * saturate( g_flHaloEmblemActive ) ) );
		}

		// Camouflaged weapons' transparent parts fade out with the camouflage (1). Overlays and emblems on a camouflaged
		// object's model (2): MCC draws its camouflage pass over them by the camouflage, so they fade to what they lie
		// on (grey leaves a double multiply's frame as is; the alpha blended fade their coverage).
		if ( g_flCamoFade > 1.5 )
		{
			float camo = saturate( g_flHaloCamo );
			#if S_BLEND_MODE == 4
				o.rgb = lerp( o.rgb, 0.5, camo );
			#elif S_BLEND_MODE == 2
				o.a *= 1 - camo;
			#else
				o *= 1 - camo;
			#endif
		}
		else if ( g_flCamoFade > 0.5 )
			o *= saturate( 1 - g_flHaloCamo );

		o = saturate( o );
		bool gammaSky = g_flHaloSky > 0.5 && g_flHaloSkyGamma > 0.5;
		float3 rgb = gammaSky ? o.rgb : linearPremultiplied.r >= 0 ? linearPremultiplied : SrgbGammaToLinear( o.rgb );

		// Fog: what's added fades with the transmittance; what covers the background is fogged like it.
		float3 positionWs = i.vPositionWithOffsetWs.xyz + g_vHighPrecisionLightingOffsetWs.xyz;
		float fogAmount = HaloFogAmount( positionWs );
		float3 fogged0 = g_vGradientFogColor_Opacity.rgb * fogAmount;
		float3 transmittance = 1 - fogAmount;

		float4 result;
		#if S_BLEND_MODE == 4
			// The frame times twice the colour in gamma space; MCC fogs the result afterwards, so fog pulls the
			// factor toward 1 (the frame here is fogged already).
			result = float4( gammaSky ? 2 * o.rgb : pow( lerp( 2 * o.rgb, 1, fogAmount ), 2.2 ), 0 );
		#elif GAMMA_ADD && S_BLEND_MODE == 1
			result = float4( AddedInGammaSpace( i.vPositionSs.xy, o.rgb * transmittance ), o.a );
		#elif GAMMA_ADD
			result = float4( AddedInGammaSpace( i.vPositionSs.xy, o.rgb * o.a * transmittance ), 1 );
		#elif S_BLEND_MODE == 0
			result = float4( rgb * transmittance + fogged0 * o.a, o.a );
		#elif S_BLEND_MODE == 2
			result = float4( rgb * transmittance + fogged0, o.a );
		#else
			result = float4( rgb * transmittance, o.a );
		#endif
		return g_flHaloOverlay > 0.5 ? HalfOfTwoDraws( result ) : result;
	}
}
