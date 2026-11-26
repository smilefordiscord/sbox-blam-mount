//
// Halo 3's render methods (shaders\shader and its siblings: terrain, foliage, halogram, custom, cortana, decal), as one forward pass.
//
// MCC draws an opaque material in two passes: an albedo pass into a G-buffer, then a lighting entry point (static_per_pixel for lightmapped BSP surfaces, static_per_vertex, static_sh / static_prt_* for objects) reading it back. Here both happen in one pass: albedo, normal and lighting inline.
//
// Each render method category is a mode number the mount sets from the tag's chosen option (Halo3/Halo3Materials.cs), so one shader covers the option combinations: g_flAlbedoMode, g_flBumpMode, g_flSpecularMaskMode, g_flMaterialModel, g_flEnvMode, g_flSelfIllumMode, g_flParallaxMode. Only what changes pipeline state is a variant: halo3_shader_transparent.shader (HALO3_TRANSPARENT, blend mode as F_BLEND_MODE) and halo3_terrain.shader (HALO3_TERRAIN, up to four materials by blend map), both made by tools/compile-shaders.ps1.
//
// Static lighting (g_flLightingMode): 1 the BSP's L1 SH lightmap (two DXT5 texture arrays, bindless, with the Lbsp's five decode scales), 2 L1 SH per vertex (Halo3Vertex.Sh0..Sh3), 3 an L2 SH probe per object (render attributes Halo3ProbeSh0..8; a neutral ambient until set), 0 a neutral ambient. Lighting is linear HDR radiance; the result goes through exposure and a tone curve to land near MCC's frame (see FinishColor).
//
// Atmosphere fog: one skya setting's Rayleigh/Mie scattering per pixel (H3Atmosphere), set per map by Halo3Materials.
//
// Objects' shadows: the scene's sun (Halo3SunLight) casts only objects into its shadow map; H3SunVisibility reads it for the level (inside a caster's volume) and for objects (themselves and each other), and H3SunShadowFactor darkens the lit colour there by how much of its light the sun was.
// The editor's view modes and wireframe (H3ToolsVis): the surface as s&box's material, with Halo's static lighting standing in for s&box's.
// Not here: animated parameters (overlays), dynamic lights, the dynamic environment cube maps (approximated from the SH), PRT, bloom.
//

HEADER
{
	Description = "Halo 3 render method";
}

FEATURES
{
	#include "common/features.hlsl"

	// Decorators (and anything else seen from both sides): no back face culling.
	Feature( F_TWO_SIDED, 0..1, "Rendering" );

	// halo3_shader_transparent.shader only: the render method's blend mode.
	Feature( F_BLEND_MODE, 0..5( 0="Additive", 1="Alpha Blend", 2="Multiply", 3="Double Multiply", 4="Premultiplied Alpha", 5="Inverse Alpha Blend" ), "Blending" );
}

MODES
{
	Forward();
	Depth( S_MODE_DEPTH );
	ToolsShadingComplexity( "tools_shading_complexity.shader" );
}

COMMON
{
	// halo3_water.shader (HALO3_WATER): Halo 3's water (rmw), transparent, reading a copy of the frame for its refraction.
	#ifndef HALO3_WATER
	#define HALO3_WATER 0
	#endif
	#if HALO3_WATER
	#define HALO3_TRANSPARENT 1
	// Water draws both sides and keeps the one on the camera's side of the surface (MainPs).
	#define S_RENDER_BACKFACES 1
	#endif

	#ifndef HALO3_TRANSPARENT
	#define HALO3_TRANSPARENT 0
	#endif
	#ifndef HALO3_TERRAIN
	#define HALO3_TERRAIN 0
	#endif

	#if HALO3_TRANSPARENT
	#define S_TRANSLUCENT 1
	#define BLEND_MODE_ALREADY_SET 1
	#endif

	#ifndef S_ALPHA_TEST
	#define S_ALPHA_TEST 1
	#endif
	#ifndef S_TRANSLUCENT
	#define S_TRANSLUCENT 0
	#endif

	#include "common/shared.hlsl"
	// Decals: the fraction of the way to the camera their vertices move along the view (0 off), for their depth only.
	float g_flDecalPull < Default( 0.0 ); >;

	#define S_UV2 1
	#define CUSTOM_MATERIAL_INPUTS

	// Decorators (StructureBspBuilder.AddDecorators, set on the set's material): the distances the set fades over, as MCC's DecoratorsVS: fade = saturate( (end - d) / (end - start) ) by the instance's distance (its position in TEXCOORD1) from the camera, horizontally; its alpha is multiplied by the fade and the instance isn't drawn once the fade is at or below 0.3. 0: off.
	float2 g_vDecoratorFade < Default2( 0.0, 0.0 ); >;

	// Scroll: time-driven translation overlays (Halo3Materials), per second (xy) over the offset range they repeat in (zw; 0 = none).
	float2 H3Scrolled( float4 xform, float4 scroll )
	{
		float2 offset = xform.zw;
		if ( scroll.z != 0 ) offset.x += frac( g_flTime * scroll.x / scroll.z ) * scroll.z;
		if ( scroll.w != 0 ) offset.y += frac( g_flTime * scroll.y / scroll.w ) * scroll.w;
		return offset;
	}

	// Time-driven overlay waves (ShaderOverlay.TimeWave): a wave is (period, frequency, phase, kind = function type * 16 + shape, + 256 when the function clamps, which its users apply to the mapped value), evaluated at frac( time / period ) as Halo3Function does: periodic shapes 0 one, 1 zero, 2/3 cosine, 4/5 triangle, 6/7 slide, 8 noise, 9 jitter, 10 wander, 11 spark; transition shapes 0 linear, 1 square root, 2 fourth root, 3 square, 4 fourth power, 5 cosine, 6 one, 7 zero.
	float H3PeriodicShape( int shape, float t )
	{
		if ( shape == 0 ) return 1;
		if ( shape == 1 ) return 0;
		if ( shape <= 3 ) return ( cos( 2 * 3.14159265 * t ) + 1 ) * 0.5;
		if ( shape <= 5 ) return t < 0.5 ? 2 * t : 2 - 2 * t;
		if ( shape == 8 ) return frac( pow( 2, t ) * 10000 );
		if ( shape == 9 || shape == 10 )
		{
			float a = cos( t * 3.14159265 / 2 ) * sin( 3.14159265 * t );
			float b = cos( 8 * 3.14159265 * t ) * cos( 0.897598 * t );
			float c = sin( t * 3.14159265 / 2 ) * cos( 14 * 3.14159265 * t );
			if ( shape == 9 ) return ( a + b + c ) * 0.5 + 1;
			float d = sin( 3.14159265 * t ) * cos( 2 * 3.14159265 * t );
			return ( d + b * 0.5 + c * 0.5 ) * 0.5 + 1;
		}
		return t;
	}

	float H3TransitionShape( int shape, float x )
	{
		if ( shape == 1 ) return sqrt( x );
		if ( shape == 2 ) return sqrt( sqrt( x ) );
		if ( shape == 3 ) return x * x;
		if ( shape == 4 ) return x * x * x * x;
		if ( shape == 5 ) return cos( 2 * 3.14159265 * ( x + 1 ) );
		if ( shape == 6 ) return 1;
		if ( shape == 7 ) return 0;
		return x;
	}

	float H3Wave( float4 wave )
	{
		int kind = (int)round( wave.w ) & 255;
		int type = kind >> 4, shape = kind & 15;
		float x = frac( g_flTime / max( wave.x, 0.001 ) );
		float v = x;
		if ( type == 3 ) v = H3PeriodicShape( shape, frac( x * wave.y + wave.z ) );
		else if ( type == 2 ) v = H3TransitionShape( shape, x );
		else if ( type == 1 ) v = 0;
		return v;
	}

	float3 H3SrgbToLinear( float3 c )
	{
		c = saturate( c );
		return lerp( pow( ( c + 0.055 ) / 1.055, 2.4 ), c / 12.92, step( c, 0.04045 ) );
	}

	#if HALO3_WATER
	// Water (rmw, Halo3Materials.Water), as MCC's water shaders: the wave slope array (bindless index, -1 none) with its transform and scroll; slope ranges x/y, layer count, time warp period; detail slope scale x/y, strength (steepness), aux time warp period; colour (pure, or the texture in BaseMap) and murkiness; fresnel coefficient, reflection scale, bank alpha depth (water units, 0 none).
	float g_flWaveSlopeArray < Default( -1.0 ); >;
	float4 g_vWaveSlopeXform < Default4( 1.0, 1.0, 0.0, 0.0 ); >;
	float4 g_vWaveSlopeScroll < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float4 g_vWaterWave < Default4( 0.5, 0.5, 1.0, 1.0 ); >;
	float4 g_vWaterDetail < Default4( 4.0, 4.0, 0.5, 1.0 ); >;
	// wave_displacement_array's transform (MCC's main wave layer reads the slope array at it) and wave_height, wave_height_aux, detail_slope_scale_z (how much faster the detail layer runs), 1 if waveshape is default (else MCC's main and aux slopes weigh nothing).
	float4 g_vWaveDisplacementXform < Default4( 1.0, 1.0, 0.0, 0.0 ); >;
	float4 g_vWaterHeights < Default4( 0.5, 0.5, 1.0, 0.0 ); >;
	// The global shape: x 0 none (1), 1 painted in global_shape_texture (a bindless index, -1 none), 2 the water depth over y (globalshape_infuence_depth).
	float2 g_vGlobalShape < Default2( 0.0, 1.0 ); >;
	float g_flGlobalShapeTexture < Default( -1.0 ); >;
	float g_flGlobalShapeSrgb < Default( 0.0 ); >;
	// wave_visual_damping_distance (world units): past it MCC's water divides its wave slopes by distance / damping, so far water lies flat.
	float g_flWaterDamping < Default( 8.0 ); >;
	// refraction_depth_dominant_ratio (how much the depth seen through is the vertical depth rather than the distance) and refraction_extinct_distance (world units: the bed fades out by then).
	float2 g_vWaterRefraction < Default2( 0.95, 1000.0 ); >;
	// water_diffuse (rgb, added on top, lit and by the normal's facing up) and watercolor_coefficient (w).
	float4 g_vWaterDiffuse < Default4( 0.0, 0.0, 0.0, 1.0 ); >;
	float4 g_vWaterColor < Default4( 0.03, 0.06, 0.07, 3.0 ); >;
	float4 g_vWaterFresnel < Default4( 0.1, 1.0, 0.0, 1.0 ); >;
	// Water reflection (MCC's water PS): x sunspot_cut, y shadow_intensity_mark.
	float2 g_vWaterReflection < Default2( 1.0, 0.0 ); >;
	float g_flWaterColorTexture < Default( 0.0 ); >;
	// 1 for bankalpha paint: the banks are painted in watercolor_texture's alpha (in BaseMap).
	float g_flWaterBankPaint < Default( 0.0 ); >;
	float g_flWorldScale < Default( 103.2258 ); >;
	// MCC's water displacement (its vertex shader, waveshape default): the displacement array (bindless, -1 none), displacement_range x y z, choppiness forward, backward, side.
	float g_flWaveDisplacementArray < Default( -1.0 ); >;
	float4 g_vDisplacementRange < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float4 g_vChoppiness < Default4( 1.0, 1.0, 1.0, 0.0 ); >;
	// Foam (foam_texture in DetailMap, foam_texture_detail in DetailMap2): foam_height, foam_pow, and where it comes from: 1 the wave height (auto), 2 painted in the global shape texture's blue (paint), 3 both, 0 none.
	float4 g_vWaterFoam < Default4( 0.5, 1.0, 0.0, 0.0 ); >;
	// Refraction: refraction_texcoord_shift, minimal_wave_disturbance, 1 if dynamic (the waves bend the frame behind, read from a copy of the frame).
	float4 g_vWaterRefract < Default4( 0.0, 1.0, 0.0, 0.0 ); >;
	// The camera under water (Halo3SceneUpkeep.KeepUnderwater): x 1 when it's inside a water volume (the surfaces are seen from below), y 1 to fog them, z the murkiness; the fog colour (exposed, as the frame).
	float4 g_vCameraUnderwater < Attribute( "Halo3CameraUnderwater" ); Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float3 g_vUnderwaterFogColor < Attribute( "Halo3UnderwaterFog" ); Default3( 0.0, 0.0, 0.0 ); >;

	// The global shape at a water vertex's parameters (x depth, yz global texture coordinate) as MCC's water vertex shader reads it, at a mip level: x scales the waves, y their choppiness.
	float2 H3GlobalShape( float4 water, float lod )
	{
		if ( g_vGlobalShape.x > 1.5 )
			return float2( saturate( water.x / max( g_vGlobalShape.y, 1e-3 ) ), 1 );
		if ( g_vGlobalShape.x > 0.5 && g_flGlobalShapeTexture >= 0 )
		{
			float2 shape = Bindless::GetTexture2D( (int)round( g_flGlobalShapeTexture ) ).SampleLevel( g_sTrilinearClamp, water.yz, lod ).rg;
			return g_flGlobalShapeSrgb > 0.5 ? H3SrgbToLinear( float3( shape, 0 ) ).xy : shape;
		}
		return 1;
	}

	// One layer pair of the displacement array at a time warp (0..1 over its period), as MCC interpolates its frames.
	float3 H3DisplacementLayer( Texture2DArray t, float2 uv, float warp, float layers, float lod )
	{
		float f = frac( warp ) * layers;
		float l0 = floor( f );
		float l1 = l0 + 1 >= layers ? 0 : l0 + 1;
		return lerp( t.SampleLevel( g_sTrilinearWrap, float3( uv, l0 ), lod ).xyz, t.SampleLevel( g_sTrilinearWrap, float3( uv, l1 ), lod ).xyz, f - l0 );
	}

	// MCC's water vertex shader (waveshape default): the displacement array's main layer (its own transform, time_warp) and aux layer (the slope array's transform, time_warp_aux), each texel x displacement_range less half of it, times wave_height / wave_height_aux, over sqrt(transform x * y); sideways by the global shape's green times choppiness (forward when x < 0, else backward; side), all times the vertex's displacement scale (water.w), up by the shape's red; both textures at mip max(distance / damping, 1). Water units along tangent, -binormal and normal; foam gets MCC's foam heights (x 10): this vertex's rise and the highest a wave can rise.
	float3 H3WaterDisplacement( float2 uv, float4 water, float far, out float2 foam )
	{
		float lod = max( far / max( g_flWaterDamping, 1e-3 ), 1 );
		float2 shape = H3GlobalShape( water, lod );

		Texture2DArray t = Bindless::GetTexture2DArray( (int)round( g_flWaveDisplacementArray ) );
		float width, height, layers;
		t.GetDimensions( width, height, layers );
		float3 range = g_vDisplacementRange.xyz;
		float3 main = H3DisplacementLayer( t, uv * g_vWaveDisplacementXform.xy + g_vWaveDisplacementXform.zw, g_flTime / max( g_vWaterWave.w, 0.01 ), layers, lod ) * range - range * 0.5;
		float3 aux = H3DisplacementLayer( t, uv * g_vWaveSlopeXform.xy + H3Scrolled( g_vWaveSlopeXform, g_vWaveSlopeScroll ), g_flTime / max( g_vWaterDetail.w, 0.01 ), layers, lod ) * range - range * 0.5;
		float scale = sqrt( max( g_vWaveDisplacementXform.x * g_vWaveDisplacementXform.y, 1e-6 ) );
		float3 d = ( main * g_vWaterHeights.x + aux * g_vWaterHeights.y ) / scale;
		d.x *= shape.y * ( d.x < 0 ? g_vChoppiness.x : g_vChoppiness.y );
		d.y *= shape.y * g_vChoppiness.z;
		d *= water.w;
		d.z *= shape.x;
		foam = 10 * float2( d.z, water.w * range.z * 0.5 * ( g_vWaterHeights.x + g_vWaterHeights.y ) / scale );
		return d;
	}
	#endif
}

struct VertexInput
{
	// Per-vertex SH (Halo3Vertex.Sh0..Sh3): L1 coefficients c0..c3 in xyz (RGB), the dominant light intensity's r, g, b in Sh0.w, Sh1.w, Sh2.w.
	float4 vSh0 : TEXCOORD4 < Semantic( None ); >;
	float4 vSh1 : TEXCOORD5 < Semantic( None ); >;
	float4 vSh2 : TEXCOORD6 < Semantic( None ); >;
	float4 vSh3 : TEXCOORD7 < Semantic( None ); >;
	// Not in skinned draws: compute skinning adds three inputs of its own, and with these the draw passed s&box's input limit and wasn't drawn (Zanzibar's wheel, every vehicle); skinned models never carry decals.
	#if !( D_CS_VERTEX_ANIMATION )
	float4 vDecalTexCoords : TEXCOORD8 < Semantic( None ); >;
	float2 vDecalTexCoord2 : TEXCOORD9 < Semantic( None ); >;
	#endif
	#include "common/vertexinput.hlsl"
};

struct PixelInput
{
	float4 vSh0 : TEXCOORD8;
	float4 vSh1 : TEXCOORD9;
	float4 vSh2 : TEXCOORD10;
	float4 vSh3 : TEXCOORD12;
	float4 vDecalTexCoords : TEXCOORD13;
	float2 vDecalTexCoord2 : TEXCOORD14;
	#if HALO3_WATER
	// MCC's foam heights (H3WaterDisplacement), interpolated as its vertex shader's.
	float2 vWaterFoam : TEXCOORD5;
	#endif
	#include "common/pixelinput.hlsl"
};

VS
{
	#include "common/vertex.hlsl"

	StaticCombo( S_MODE_DEPTH, 0..1, Sys( ALL ) );

	PixelInput MainVs( VertexInput v )
	{
		PixelInput i = ProcessVertex( v );
		i.vSh0 = v.vSh0;
		i.vSh1 = v.vSh1;
		i.vSh2 = v.vSh2;
		i.vSh3 = v.vSh3;
		#if ( D_CS_VERTEX_ANIMATION )
		i.vDecalTexCoords = 0;
		i.vDecalTexCoord2 = 0;
		#else
		i.vDecalTexCoords = v.vDecalTexCoords;
		i.vDecalTexCoord2 = v.vDecalTexCoord2;
		#endif

		// Water with its per-vertex parameters (Sh3.w its displacement scale) moves as MCC's water vertex shader moves it; without them its foam heights are MCC's (0, 10).
		#if HALO3_WATER
		i.vWaterFoam = float2( 0, 10 );
		if ( g_flWaveDisplacementArray >= 0 && v.vSh3.w > 0 )
		{
			float far = length( g_vCameraPositionWs - i.vPositionWs ) / g_flWorldScale;
			float3 d = H3WaterDisplacement( i.vTextureCoords.xy, v.vSh3, far, i.vWaterFoam );
			i.vPositionWs += ( normalize( i.vTangentUWs ) * d.x - normalize( i.vTangentVWs ) * d.y + normalize( i.vNormalWs ) * d.z ) * g_flWorldScale;
			i.vPositionPs = Position3WsToPs( i.vPositionWs );
		}
		#endif

		// Decals lie on the surface under them; like MCC's depth bias they're pulled toward the camera along the view ray, which changes their depth and not where they draw (a lift along the normal showed as a layer floating over the surface).
		if ( g_flDecalPull > 0 )
		{
			i.vPositionWs += ( g_vCameraPositionWs - i.vPositionWs ) * g_flDecalPull;
			i.vPositionPs = Position3WsToPs( i.vPositionWs );
		}

		PixelInput o = FinalizeVertex( i );

		// Halo's decorator distance fade, carried to the pixel shader in Sh3.w (decorators have no L1 c3): every vertex of a faded-out instance goes to the same point, so its triangles are empty.
		if ( g_vDecoratorFade.y > g_vDecoratorFade.x )
		{
			float fade = saturate( ( g_vDecoratorFade.y - distance( v.vTexCoord2.xy, g_vCameraPositionWs.xy ) ) / ( g_vDecoratorFade.y - g_vDecoratorFade.x ) );
			o.vSh3.w = fade;
			if ( fade <= 0.3 )
				o.vPositionPs = float4( 0, 0, 0, 1 );
		}

		// Water draws no depth in depth passes (shadows, prepasses), as its pixel shader discards there.
		#if HALO3_WATER && S_MODE_DEPTH
			o.vPositionPs = float4( 0, 0, 0, 1 );
		#endif

		return o;
	}
}

PS
{
	#include "common/pixel.hlsl"

	StaticCombo( S_TWO_SIDED, F_TWO_SIDED, Sys( ALL ) );
	#if HALO3_WATER
		RenderState( CullMode, NONE );
	#else
		RenderState( CullMode, S_TWO_SIDED ? NONE : DEFAULT );
	#endif

	#if HALO3_TRANSPARENT
		StaticCombo( S_BLEND_MODE, F_BLEND_MODE, Sys( ALL ) );

		RenderState( DepthWriteEnable, false );
		RenderState( BlendEnable, true );
		RenderState( BlendOp, ADD );
		RenderState( SrcBlendAlpha, ZERO );
		RenderState( DstBlendAlpha, ONE );
		#if S_BLEND_MODE == 0
			RenderState( SrcBlend, ONE );
			RenderState( DstBlend, ONE );
		#elif S_BLEND_MODE == 1
			RenderState( SrcBlend, SRC_ALPHA );
			RenderState( DstBlend, INV_SRC_ALPHA );
		#elif S_BLEND_MODE == 2
			RenderState( SrcBlend, DEST_COLOR );
			RenderState( DstBlend, ZERO );
		#elif S_BLEND_MODE == 3
			RenderState( SrcBlend, DEST_COLOR );
			RenderState( DstBlend, SRC_COLOR );
		#elif S_BLEND_MODE == 4
			RenderState( SrcBlend, ONE );
			RenderState( DstBlend, INV_SRC_ALPHA );
		#else
			RenderState( SrcBlend, INV_SRC_ALPHA );
			RenderState( DstBlend, SRC_ALPHA );
		#endif
		#define HALO3_BLEND S_BLEND_MODE
	#else
		#define HALO3_BLEND -1
	#endif

	SamplerState g_sWrap < Filter( Anisotropic ); AddressU( WRAP ); AddressV( WRAP ); >;
	SamplerState g_sLightmap < Filter( Bilinear ); AddressU( CLAMP ); AddressV( CLAMP ); >;

	// The render method's textures (Halo3Materials.cs maps each Halo parameter to one), each with its scale/offset transform (the real named like it) and whether MCC reads it through an sRGB view.
	// Unset slots keep neutral defaults: base and detail maps MCC's 50% grey in linear (0.2176, which the detail multiplier takes to 1), flat bump maps, no self-illumination.
	#if HALO3_TERRAIN
		CreateInputTexture2D( BlendMap, Linear, 8, "None", "_color", ",0/,0/0", Default4( 1.0, 0.0, 0.0, 0.0 ) );
		Texture2D g_tBlendMap < Channel( RGBA, Box( BlendMap ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( false ); >;
		float4 g_vBlendMapXform < Default4( 1.0, 1.0, 0.0, 0.0 ); >;
		float4 g_vBlendMapScroll < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
		float g_flBlendMapSrgb < Default( 0.0 ); >;
		CreateInputTexture2D( BaseMap0, Linear, 8, "None", "_color", ",0/,0/0", Default4( 0.2176, 0.2176, 0.2176, 1.0 ) );
		Texture2D g_tBaseMap0 < Channel( RGBA, Box( BaseMap0 ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( false ); >;
		float4 g_vBaseMap0Xform < Default4( 1.0, 1.0, 0.0, 0.0 ); >;
		float4 g_vBaseMap0Scroll < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
		float g_flBaseMap0Srgb < Default( 0.0 ); >;
		CreateInputTexture2D( DetailMap0, Linear, 8, "None", "_color", ",0/,0/0", Default4( 0.2176, 0.2176, 0.2176, 1.0 ) );
		Texture2D g_tDetailMap0 < Channel( RGBA, Box( DetailMap0 ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( false ); >;
		float4 g_vDetailMap0Xform < Default4( 1.0, 1.0, 0.0, 0.0 ); >;
		float4 g_vDetailMap0Scroll < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
		float g_flDetailMap0Srgb < Default( 0.0 ); >;
		CreateInputTexture2D( BumpMap0, Linear, 8, "None", "_color", ",0/,0/0", Default4( 0.5, 0.5, 1.0, 1.0 ) );
		Texture2D g_tBumpMap0 < Channel( RGBA, Box( BumpMap0 ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( false ); >;
		float4 g_vBumpMap0Xform < Default4( 1.0, 1.0, 0.0, 0.0 ); >;
		float4 g_vBumpMap0Scroll < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
		float g_flBumpMap0Srgb < Default( 0.0 ); >;
		float g_flBumpMap0Unsigned < Default( 0.0 ); >;
		CreateInputTexture2D( DetailBump0, Linear, 8, "None", "_color", ",0/,0/0", Default4( 0.5, 0.5, 1.0, 1.0 ) );
		Texture2D g_tDetailBump0 < Channel( RGBA, Box( DetailBump0 ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( false ); >;
		float4 g_vDetailBump0Xform < Default4( 1.0, 1.0, 0.0, 0.0 ); >;
		float4 g_vDetailBump0Scroll < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
		float g_flDetailBump0Srgb < Default( 0.0 ); >;
		float g_flDetailBump0Unsigned < Default( 0.0 ); >;
		CreateInputTexture2D( BaseMap1, Linear, 8, "None", "_color", ",0/,0/0", Default4( 0.2176, 0.2176, 0.2176, 1.0 ) );
		Texture2D g_tBaseMap1 < Channel( RGBA, Box( BaseMap1 ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( false ); >;
		float4 g_vBaseMap1Xform < Default4( 1.0, 1.0, 0.0, 0.0 ); >;
		float4 g_vBaseMap1Scroll < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
		float g_flBaseMap1Srgb < Default( 0.0 ); >;
		CreateInputTexture2D( DetailMap1, Linear, 8, "None", "_color", ",0/,0/0", Default4( 0.2176, 0.2176, 0.2176, 1.0 ) );
		Texture2D g_tDetailMap1 < Channel( RGBA, Box( DetailMap1 ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( false ); >;
		float4 g_vDetailMap1Xform < Default4( 1.0, 1.0, 0.0, 0.0 ); >;
		float4 g_vDetailMap1Scroll < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
		float g_flDetailMap1Srgb < Default( 0.0 ); >;
		CreateInputTexture2D( BumpMap1, Linear, 8, "None", "_color", ",0/,0/0", Default4( 0.5, 0.5, 1.0, 1.0 ) );
		Texture2D g_tBumpMap1 < Channel( RGBA, Box( BumpMap1 ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( false ); >;
		float4 g_vBumpMap1Xform < Default4( 1.0, 1.0, 0.0, 0.0 ); >;
		float4 g_vBumpMap1Scroll < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
		float g_flBumpMap1Srgb < Default( 0.0 ); >;
		float g_flBumpMap1Unsigned < Default( 0.0 ); >;
		CreateInputTexture2D( DetailBump1, Linear, 8, "None", "_color", ",0/,0/0", Default4( 0.5, 0.5, 1.0, 1.0 ) );
		Texture2D g_tDetailBump1 < Channel( RGBA, Box( DetailBump1 ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( false ); >;
		float4 g_vDetailBump1Xform < Default4( 1.0, 1.0, 0.0, 0.0 ); >;
		float4 g_vDetailBump1Scroll < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
		float g_flDetailBump1Srgb < Default( 0.0 ); >;
		float g_flDetailBump1Unsigned < Default( 0.0 ); >;
		CreateInputTexture2D( BaseMap2, Linear, 8, "None", "_color", ",0/,0/0", Default4( 0.2176, 0.2176, 0.2176, 1.0 ) );
		Texture2D g_tBaseMap2 < Channel( RGBA, Box( BaseMap2 ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( false ); >;
		float4 g_vBaseMap2Xform < Default4( 1.0, 1.0, 0.0, 0.0 ); >;
		float4 g_vBaseMap2Scroll < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
		float g_flBaseMap2Srgb < Default( 0.0 ); >;
		CreateInputTexture2D( DetailMap2, Linear, 8, "None", "_color", ",0/,0/0", Default4( 0.2176, 0.2176, 0.2176, 1.0 ) );
		Texture2D g_tDetailMap2 < Channel( RGBA, Box( DetailMap2 ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( false ); >;
		float4 g_vDetailMap2Xform < Default4( 1.0, 1.0, 0.0, 0.0 ); >;
		float4 g_vDetailMap2Scroll < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
		float g_flDetailMap2Srgb < Default( 0.0 ); >;
		CreateInputTexture2D( BumpMap2, Linear, 8, "None", "_color", ",0/,0/0", Default4( 0.5, 0.5, 1.0, 1.0 ) );
		Texture2D g_tBumpMap2 < Channel( RGBA, Box( BumpMap2 ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( false ); >;
		float4 g_vBumpMap2Xform < Default4( 1.0, 1.0, 0.0, 0.0 ); >;
		float4 g_vBumpMap2Scroll < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
		float g_flBumpMap2Srgb < Default( 0.0 ); >;
		float g_flBumpMap2Unsigned < Default( 0.0 ); >;
		CreateInputTexture2D( DetailBump2, Linear, 8, "None", "_color", ",0/,0/0", Default4( 0.5, 0.5, 1.0, 1.0 ) );
		Texture2D g_tDetailBump2 < Channel( RGBA, Box( DetailBump2 ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( false ); >;
		float4 g_vDetailBump2Xform < Default4( 1.0, 1.0, 0.0, 0.0 ); >;
		float4 g_vDetailBump2Scroll < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
		float g_flDetailBump2Srgb < Default( 0.0 ); >;
		float g_flDetailBump2Unsigned < Default( 0.0 ); >;
		CreateInputTexture2D( BaseMap3, Linear, 8, "None", "_color", ",0/,0/0", Default4( 0.2176, 0.2176, 0.2176, 1.0 ) );
		Texture2D g_tBaseMap3 < Channel( RGBA, Box( BaseMap3 ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( false ); >;
		float4 g_vBaseMap3Xform < Default4( 1.0, 1.0, 0.0, 0.0 ); >;
		float4 g_vBaseMap3Scroll < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
		float g_flBaseMap3Srgb < Default( 0.0 ); >;
		CreateInputTexture2D( DetailMap3, Linear, 8, "None", "_color", ",0/,0/0", Default4( 0.2176, 0.2176, 0.2176, 1.0 ) );
		Texture2D g_tDetailMap3 < Channel( RGBA, Box( DetailMap3 ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( false ); >;
		float4 g_vDetailMap3Xform < Default4( 1.0, 1.0, 0.0, 0.0 ); >;
		float4 g_vDetailMap3Scroll < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
		float g_flDetailMap3Srgb < Default( 0.0 ); >;
		CreateInputTexture2D( BumpMap3, Linear, 8, "None", "_color", ",0/,0/0", Default4( 0.5, 0.5, 1.0, 1.0 ) );
		Texture2D g_tBumpMap3 < Channel( RGBA, Box( BumpMap3 ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( false ); >;
		float4 g_vBumpMap3Xform < Default4( 1.0, 1.0, 0.0, 0.0 ); >;
		float4 g_vBumpMap3Scroll < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
		float g_flBumpMap3Srgb < Default( 0.0 ); >;
		float g_flBumpMap3Unsigned < Default( 0.0 ); >;
		CreateInputTexture2D( DetailBump3, Linear, 8, "None", "_color", ",0/,0/0", Default4( 0.5, 0.5, 1.0, 1.0 ) );
		Texture2D g_tDetailBump3 < Channel( RGBA, Box( DetailBump3 ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( false ); >;
		float4 g_vDetailBump3Xform < Default4( 1.0, 1.0, 0.0, 0.0 ); >;
		float4 g_vDetailBump3Scroll < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
		float g_flDetailBump3Srgb < Default( 0.0 ); >;
		float g_flDetailBump3Unsigned < Default( 0.0 ); >;
		CreateInputTexture2D( SelfIllumMap, Linear, 8, "None", "_color", ",0/,0/0", Default4( 0.0, 0.0, 0.0, 0.0 ) );
		Texture2D g_tSelfIllumMap < Channel( RGBA, Box( SelfIllumMap ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( false ); >;
		float4 g_vSelfIllumMapXform < Default4( 1.0, 1.0, 0.0, 0.0 ); >;
		float4 g_vSelfIllumMapScroll < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
		float g_flSelfIllumMapSrgb < Default( 0.0 ); >;
		CreateInputTexture2D( SelfIllumDetailMap, Linear, 8, "None", "_color", ",0/,0/0", Default4( 0.2176, 0.2176, 0.2176, 1.0 ) );
		Texture2D g_tSelfIllumDetailMap < Channel( RGBA, Box( SelfIllumDetailMap ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( false ); >;
		float4 g_vSelfIllumDetailMapXform < Default4( 1.0, 1.0, 0.0, 0.0 ); >;
		float4 g_vSelfIllumDetailMapScroll < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
		float g_flSelfIllumDetailMapSrgb < Default( 0.0 ); >;

		// Per material: 0 off, 1 diffuse only, 2 diffuse plus specular, 3 diffuse plus specular plus self-illumination (material 1 only in MCC's maps).
		float4 g_vTerrainMaterials < Default4( 1.0, 0.0, 0.0, 0.0 ); >;

		// Per material (x..w): diffuse coefficient, specular coefficient, specular power, analytical specular contribution, area specular contribution, albedo specular tint blend, fresnel curve steepness.
		float4 g_vTerrainDiffuse < Default4( 1.0, 1.0, 1.0, 1.0 ); >;
		float4 g_vTerrainSpecular < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
		float4 g_vTerrainPower < Default4( 10.0, 10.0, 10.0, 10.0 ); >;
		float4 g_vTerrainAnalytical < Default4( 0.5, 0.5, 0.5, 0.5 ); >;
		float4 g_vTerrainArea < Default4( 0.5, 0.5, 0.5, 0.5 ); >;
		float4 g_vTerrainTintBlend < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
		float4 g_vTerrainFresnel < Default4( 5.0, 5.0, 5.0, 5.0 ); >;
		float4 g_vTerrainEnv < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
		float3 g_vTerrainTint0 < Default3( 1.0, 1.0, 1.0 ); >;
		float3 g_vTerrainTint1 < Default3( 1.0, 1.0, 1.0 ); >;
		float3 g_vTerrainTint2 < Default3( 1.0, 1.0, 1.0 ); >;
		float3 g_vTerrainTint3 < Default3( 1.0, 1.0, 1.0 ); >;
		float3 g_vGlobalAlbedoTint < Default3( 1.0, 1.0, 1.0 ); >;
	#else
		CreateInputTexture2D( BaseMap, Linear, 8, "None", "_color", ",0/,0/0", Default4( 0.2176, 0.2176, 0.2176, 1.0 ) );
		Texture2D g_tBaseMap < Channel( RGBA, Box( BaseMap ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( false ); >;
		float4 g_vBaseMapXform < Default4( 1.0, 1.0, 0.0, 0.0 ); >;
		float4 g_vBaseMapScroll < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
		float g_flBaseMapSrgb < Default( 0.0 ); >;
		CreateInputTexture2D( DetailMap, Linear, 8, "None", "_color", ",0/,0/0", Default4( 0.2176, 0.2176, 0.2176, 1.0 ) );
		Texture2D g_tDetailMap < Channel( RGBA, Box( DetailMap ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( false ); >;
		float4 g_vDetailMapXform < Default4( 1.0, 1.0, 0.0, 0.0 ); >;
		float4 g_vDetailMapScroll < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
		float g_flDetailMapSrgb < Default( 0.0 ); >;
		CreateInputTexture2D( DetailMap2, Linear, 8, "None", "_color", ",0/,0/0", Default4( 0.2176, 0.2176, 0.2176, 1.0 ) );
		Texture2D g_tDetailMap2 < Channel( RGBA, Box( DetailMap2 ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( false ); >;
		float4 g_vDetailMap2Xform < Default4( 1.0, 1.0, 0.0, 0.0 ); >;
		float4 g_vDetailMap2Scroll < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
		float g_flDetailMap2Srgb < Default( 0.0 ); >;
		CreateInputTexture2D( DetailMap3, Linear, 8, "None", "_color", ",0/,0/0", Default4( 0.2176, 0.2176, 0.2176, 1.0 ) );
		Texture2D g_tDetailMap3 < Channel( RGBA, Box( DetailMap3 ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( false ); >;
		float4 g_vDetailMap3Xform < Default4( 1.0, 1.0, 0.0, 0.0 ); >;
		float4 g_vDetailMap3Scroll < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
		float g_flDetailMap3Srgb < Default( 0.0 ); >;
		CreateInputTexture2D( DetailMapOverlay, Linear, 8, "None", "_color", ",0/,0/0", Default4( 0.2176, 0.2176, 0.2176, 1.0 ) );
		Texture2D g_tDetailMapOverlay < Channel( RGBA, Box( DetailMapOverlay ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( false ); >;
		float4 g_vDetailMapOverlayXform < Default4( 1.0, 1.0, 0.0, 0.0 ); >;
		float4 g_vDetailMapOverlayScroll < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
		float g_flDetailMapOverlaySrgb < Default( 0.0 ); >;
		CreateInputTexture2D( ChangeColorMap, Linear, 8, "None", "_color", ",0/,0/0", Default4( 0.0, 0.0, 0.0, 0.0 ) );
		Texture2D g_tChangeColorMap < Channel( RGBA, Box( ChangeColorMap ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( false ); >;
		float4 g_vChangeColorMapXform < Default4( 1.0, 1.0, 0.0, 0.0 ); >;
		float4 g_vChangeColorMapScroll < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
		float g_flChangeColorMapSrgb < Default( 0.0 ); >;
		CreateInputTexture2D( BumpMap, Linear, 8, "None", "_color", ",0/,0/0", Default4( 0.5, 0.5, 1.0, 1.0 ) );
		Texture2D g_tBumpMap < Channel( RGBA, Box( BumpMap ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( false ); >;
		float4 g_vBumpMapXform < Default4( 1.0, 1.0, 0.0, 0.0 ); >;
		float4 g_vBumpMapScroll < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
		float g_flBumpMapSrgb < Default( 0.0 ); >;
		float g_flBumpMapUnsigned < Default( 0.0 ); >;
		CreateInputTexture2D( BumpDetailMap, Linear, 8, "None", "_color", ",0/,0/0", Default4( 0.5, 0.5, 1.0, 1.0 ) );
		Texture2D g_tBumpDetailMap < Channel( RGBA, Box( BumpDetailMap ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( false ); >;
		float4 g_vBumpDetailMapXform < Default4( 1.0, 1.0, 0.0, 0.0 ); >;
		float4 g_vBumpDetailMapScroll < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
		float g_flBumpDetailMapSrgb < Default( 0.0 ); >;
		float g_flBumpDetailMapUnsigned < Default( 0.0 ); >;
		CreateInputTexture2D( BumpDetailMaskMap, Linear, 8, "None", "_color", ",0/,0/0", Default4( 1.0, 1.0, 1.0, 1.0 ) );
		Texture2D g_tBumpDetailMaskMap < Channel( RGBA, Box( BumpDetailMaskMap ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( false ); >;
		float4 g_vBumpDetailMaskMapXform < Default4( 1.0, 1.0, 0.0, 0.0 ); >;
		float4 g_vBumpDetailMaskMapScroll < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
		float g_flBumpDetailMaskMapSrgb < Default( 0.0 ); >;
		CreateInputTexture2D( AlphaTestMap, Linear, 8, "None", "_color", ",0/,0/0", Default4( 1.0, 1.0, 1.0, 1.0 ) );
		Texture2D g_tAlphaTestMap < Channel( RGBA, Box( AlphaTestMap ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( false ); >;
		float4 g_vAlphaTestMapXform < Default4( 1.0, 1.0, 0.0, 0.0 ); >;
		float4 g_vAlphaTestMapScroll < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
		float g_flAlphaTestMapSrgb < Default( 0.0 ); >;
		CreateInputTexture2D( SpecularMaskTexture, Linear, 8, "None", "_color", ",0/,0/0", Default4( 1.0, 1.0, 1.0, 1.0 ) );
		Texture2D g_tSpecularMaskTexture < Channel( RGBA, Box( SpecularMaskTexture ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( false ); >;
		float4 g_vSpecularMaskTextureXform < Default4( 1.0, 1.0, 0.0, 0.0 ); >;
		float4 g_vSpecularMaskTextureScroll < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
		float g_flSpecularMaskTextureSrgb < Default( 0.0 ); >;
		CreateInputTexture2D( MaterialTexture, Linear, 8, "None", "_color", ",0/,0/0", Default4( 1.0, 1.0, 1.0, 1.0 ) );
		Texture2D g_tMaterialTexture < Channel( RGBA, Box( MaterialTexture ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( false ); >;
		float4 g_vMaterialTextureXform < Default4( 1.0, 1.0, 0.0, 0.0 ); >;
		float4 g_vMaterialTextureScroll < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
		float g_flMaterialTextureSrgb < Default( 0.0 ); >;
		CreateInputTexture2D( SelfIllumMap, Linear, 8, "None", "_color", ",0/,0/0", Default4( 0.0, 0.0, 0.0, 0.0 ) );
		Texture2D g_tSelfIllumMap < Channel( RGBA, Box( SelfIllumMap ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( false ); >;
		float4 g_vSelfIllumMapXform < Default4( 1.0, 1.0, 0.0, 0.0 ); >;
		float4 g_vSelfIllumMapScroll < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
		float g_flSelfIllumMapSrgb < Default( 0.0 ); >;
		CreateInputTexture2D( SelfIllumDetailMap, Linear, 8, "None", "_color", ",0/,0/0", Default4( 0.2176, 0.2176, 0.2176, 1.0 ) );
		Texture2D g_tSelfIllumDetailMap < Channel( RGBA, Box( SelfIllumDetailMap ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( false ); >;
		float4 g_vSelfIllumDetailMapXform < Default4( 1.0, 1.0, 0.0, 0.0 ); >;
		float4 g_vSelfIllumDetailMapScroll < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
		float g_flSelfIllumDetailMapSrgb < Default( 0.0 ); >;
		CreateInputTexture2D( AlphaMaskMap, Linear, 8, "None", "_color", ",0/,0/0", Default4( 1.0, 1.0, 1.0, 1.0 ) );
		Texture2D g_tAlphaMaskMap < Channel( RGBA, Box( AlphaMaskMap ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( false ); >;
		float4 g_vAlphaMaskMapXform < Default4( 1.0, 1.0, 0.0, 0.0 ); >;
		float4 g_vAlphaMaskMapScroll < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
		float g_flAlphaMaskMapSrgb < Default( 0.0 ); >;
		CreateInputTexture2D( HeightMap, Linear, 8, "None", "_color", ",0/,0/0", Default4( 0.5, 0.5, 0.5, 0.5 ) );
		Texture2D g_tHeightMap < Channel( RGBA, Box( HeightMap ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( false ); >;
		float4 g_vHeightMapXform < Default4( 1.0, 1.0, 0.0, 0.0 ); >;
		float4 g_vHeightMapScroll < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
		float g_flHeightMapSrgb < Default( 0.0 ); >;
		CreateInputTexture2D( OverlayMap, Linear, 8, "None", "_color", ",0/,0/0", Default4( 0.0, 0.0, 0.0, 0.0 ) );
		Texture2D g_tOverlayMap < Channel( RGBA, Box( OverlayMap ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( false ); >;
		float4 g_vOverlayMapXform < Default4( 1.0, 1.0, 0.0, 0.0 ); >;
		float4 g_vOverlayMapScroll < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
		float g_flOverlayMapSrgb < Default( 0.0 ); >;
		CreateInputTexture2D( OverlayDetailMap, Linear, 8, "None", "_color", ",0/,0/0", Default4( 0.2176, 0.2176, 0.2176, 1.0 ) );
		Texture2D g_tOverlayDetailMap < Channel( RGBA, Box( OverlayDetailMap ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( false ); >;
		float4 g_vOverlayDetailMapXform < Default4( 1.0, 1.0, 0.0, 0.0 ); >;
		float4 g_vOverlayDetailMapScroll < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
		float g_flOverlayDetailMapSrgb < Default( 0.0 ); >;

		// Water's refraction reads the frame behind from a copy (as MCC's water reads scene_ldr_texture).
		#if HALO3_WATER
		BoolAttribute( bWantsFBCopyTexture, true );
		Texture2D g_tFrameBufferCopyTexture < Attribute( "FrameBufferCopyTexture" ); SrgbRead( false ); >;
		#endif

	#endif

	// A cube map as Halo3Textures registers it at its plain path: a 6:1 atlas of the faces (+X -X +Y -Y +Z -Z, D3D face rules on Halo's Z-up directions).
	CreateInputTexture2D( EnvMap, Linear, 8, "None", "_color", ",0/,0/0", Default4( 0.0, 0.0, 0.0, 1.0 ) );
	Texture2D g_tEnvMap < Channel( RGBA, Box( EnvMap ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( false ); >;
	float g_flEnvMapSrgb < Default( 0.0 ); >;
	float g_flHasEnvMap < Default( 0.0 ); >;

	// A placed object's cube map (Halo3ObjectLighting.SetCubemap: the BSP's baked cube at the scenario cube map point nearest the object, as MCC's dynamic_environment_map_0), read when the material has none of its own.
	Texture2D g_tObjectEnvMap < Attribute( "Halo3EnvMap" ); SrgbRead( false ); >;
	float g_flObjectEnvMapSrgb < Attribute( "Halo3EnvMapSrgb" ); Default( 0.0 ); >;
	float g_flHasObjectEnvMap < Attribute( "Halo3HasEnvMap" ); Default( 0.0 ); >;

	// Category modes, from the render method's options (Halo3Materials.cs has the numbering).
	float g_flAlbedoMode < Default( 0.0 ); >;
	float g_flBumpMode < Default( 0.0 ); >;
	float g_flAlphaTest < Default( 0.0 ); >;
	float g_flSpecularMaskMode < Default( 0.0 ); >;
	float g_flMaterialModel < Default( 0.0 ); >;
	float g_flEnvMode < Default( 0.0 ); >;
	float g_flSelfIllumMode < Default( 0.0 ); >;
	float g_flParallaxMode < Default( 0.0 ); >;
	float g_flOverlayMode < Default( 0.0 ); >;
	float g_flEdgeFade < Default( 0.0 ); >;

	// Albedo parameters.
	float4 g_vAlbedoColor < Default4( 1.0, 1.0, 1.0, 1.0 ); >;
	float4 g_vAlbedoColor2 < Default4( 1.0, 1.0, 1.0, 1.0 ); >;
	float4 g_vAlbedoColor3 < Default4( 1.0, 1.0, 1.0, 1.0 ); >;
	float4 g_vNeutralGray < Default4( 0.5, 0.5, 0.5, 1.0 ); >;
	float g_flBumpDetailCoefficient < Default( 1.0 ); >;
	float g_flHeightScale < Default( 0.0 ); >;

	// Material model parameters (cook_torrance and the phong lobes share these where the names match).
	float g_flDiffuseCoefficient < Default( 1.0 ); >;
	float g_flSpecularCoefficient < Default( 0.0 ); >;
	float g_flRoughness < Default( 0.4 ); >;
	float g_flAlbedoBlend < Default( 0.0 ); >;
	float3 g_vFresnelColor < Default3( 0.04, 0.04, 0.04 ); >;
	float3 g_vSpecularTint < Default3( 1.0, 1.0, 1.0 ); >;
	float g_flAnalyticalSpecular < Default( 0.5 ); >;
	float g_flAreaSpecular < Default( 0.5 ); >;
	float g_flAntiShadowControl < Default( 0.0 ); >;
	float g_flUseMaterialTexture < Default( 0.0 ); >;
	float g_flNormalSpecularPower < Default( 10.0 ); >;
	float3 g_vNormalSpecularTint < Default3( 1.0, 1.0, 1.0 ); >;
	float g_flGlancingSpecularPower < Default( 10.0 ); >;
	float3 g_vGlancingSpecularTint < Default3( 1.0, 1.0, 1.0 ); >;
	float g_flFresnelCurveSteepness < Default( 5.0 ); >;
	float g_flAlbedoSpecularTintBlend < Default( 0.0 ); >;
	float g_flRimFresnelCoefficient < Default( 0.0 ); >;
	float3 g_vRimFresnelColor < Default3( 1.0, 1.0, 1.0 ); >;
	float g_flRimFresnelPower < Default( 2.0 ); >;

	// Environment map: tint, contribution (environment_map_specular_contribution).
	float3 g_vEnvTint < Default3( 1.0, 1.0, 1.0 ); >;
	float g_flEnvContribution < Default( 0.0 ); >;
	// Dynamic environment maps pick their cube level by the roughness times this (env_roughness_scale).
	float g_flEnvRoughnessScale < Default( 0.0 ); >;

	// Glass (material_model glass): the fresnel curve weighting the environment and joining the opacity: fresnel_coefficient, fresnel_curve_steepness (shared above), fresnel_curve_bias.
	float g_flFresnelCoefficient < Default( 0.1 ); >;
	float g_flFresnelCurveBias < Default( 0.0 ); >;

	// Self-illumination: colour, intensity, the 3 channel colours (channel_a..c), meter colours and value, plasma colours (alpha scales each band).
	float3 g_vSelfIllumColor < Default3( 1.0, 1.0, 1.0 ); >;
	float g_flSelfIllumIntensity < Default( 1.0 ); >;
	// Time-driven pulses (Halo3Materials.SetSelfIllumWaves; H3Wave's waves, period 0 = none): intensity = pulse.x + pulse.y * wave held within pulse.zw; colour = the gradient of (curve.z) stops at curve.x + curve.y * wave.
	float4 g_vSelfIllumPulse < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float4 g_vSelfIllumPulseWave < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float4 g_vSelfIllumColorCurve < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float4 g_vSelfIllumColorWave < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float4 g_vSelfIllumColorStop0 < Default4( 1.0, 1.0, 1.0, 1.0 ); >;
	float4 g_vSelfIllumColorStop1 < Default4( 1.0, 1.0, 1.0, 1.0 ); >;
	float4 g_vSelfIllumColorStop2 < Default4( 1.0, 1.0, 1.0, 1.0 ); >;
	float4 g_vSelfIllumColorStop3 < Default4( 1.0, 1.0, 1.0, 1.0 ); >;
	float4 g_vChannelA < Default4( 1.0, 0.0, 0.0, 1.0 ); >;
	float4 g_vChannelB < Default4( 0.0, 1.0, 0.0, 1.0 ); >;
	float4 g_vChannelC < Default4( 0.0, 0.0, 1.0, 1.0 ); >;
	float3 g_vMeterColorOn < Default3( 1.0, 1.0, 1.0 ); >;
	float3 g_vMeterColorOff < Default3( 0.0, 0.0, 0.0 ); >;
	float g_flMeterValue < Default( 0.5 ); >;
	float4 g_vPlasmaColorMedium < Default4( 0.0, 0.0, 0.0, 1.0 ); >;
	float4 g_vPlasmaColorWide < Default4( 0.0, 0.0, 0.0, 1.0 ); >;
	float4 g_vPlasmaColorSharp < Default4( 0.0, 0.0, 0.0, 1.0 ); >;
	float3 g_vPlasmaThinness < Default3( 1.0, 1.0, 1.0 ); >;
	float g_flLayerDepth < Default( 0.0 ); >;
	float g_flLayerContrast < Default( 1.0 ); >;
	float g_flTexcoordAspectRatio < Default( 1.0 ); >;
	float g_flDepthDarken < Default( 1.0 ); >;
	float g_flLayersOf4 < Default( 1.0 ); >;

	// Halogram: overlay tint and intensity, edge fade tints and power.
	float3 g_vOverlayTint < Default3( 1.0, 1.0, 1.0 ); >;
	float g_flOverlayIntensity < Default( 1.0 ); >;
	float3 g_vEdgeFadeCenterTint < Default3( 1.0, 1.0, 1.0 ); >;
	float3 g_vEdgeFadeEdgeTint < Default3( 0.0, 0.0, 0.0 ); >;
	float g_flEdgeFadePower < Default( 1.0 ); >;

	// Decals' tint (tint_color, intensity) and the level's static lighting.
	float4 g_vTint < Default4( 1.0, 1.0, 1.0, 1.0 ); >;

	// Static lighting (Halo3Lighting): 0 neutral, 1 per pixel, 2 per vertex, 3 probe, 4 vertex colour (Sh0.rgb, diffuse only: the sky's static_per_vertex_color).
	float g_flLightingMode < Default( 0.0 ); >;

	// The BSP's lightmap arrays as bindless indices (Texture.Index; -1 for none) and their five decode scales (Lbsp +0x08, +0x20, +0x38, +0x50, +0x68).
	float g_flLightprobeArray < Default( -1.0 ); >;
	float g_flIntensityArray < Default( -1.0 ); >;
	float4 g_vLightmapScales < Default4( 1.0, 1.0, 1.0, 1.0 ); >;
	float g_flLightmapScale4 < Default( 1.0 ); >;

	// An object's L2 SH probe (Halo3Lightmap.SetProbe): RGB per coefficient, D3DX order; Halo3HasProbe 1 once set.
	float4 g_vProbeSh0 < Attribute( "Halo3ProbeSh0" ); Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float4 g_vProbeSh1 < Attribute( "Halo3ProbeSh1" ); Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float4 g_vProbeSh2 < Attribute( "Halo3ProbeSh2" ); Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float4 g_vProbeSh3 < Attribute( "Halo3ProbeSh3" ); Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float4 g_vProbeSh4 < Attribute( "Halo3ProbeSh4" ); Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float4 g_vProbeSh5 < Attribute( "Halo3ProbeSh5" ); Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float4 g_vProbeSh6 < Attribute( "Halo3ProbeSh6" ); Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float4 g_vProbeSh7 < Attribute( "Halo3ProbeSh7" ); Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float4 g_vProbeSh8 < Attribute( "Halo3ProbeSh8" ); Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float g_flHasProbe < Attribute( "Halo3HasProbe" ); Default( 0.0 ); >;

	// The object's dominant light (Halo3Lightmap.SetProbe): its probe's or lightmap sample's direction toward the light (xyz, w = 1 once set) and intensity. The analytic light of the material models' specular (MCC's k_ps_dominant_light_direction/intensity), and what the sun's shadow takes away.
	float4 g_vProbeSun < Attribute( "Halo3ProbeSun" ); Default4( 0.0, 0.0, 1.0, 0.0 ); >;
	float4 g_vProbeSunColor < Attribute( "Halo3ProbeSunColor" ); Default4( 0.0, 0.0, 0.0, 0.0 ); >;

	// Objects' shadows from the sun's shadow map (Halo3SunLight; only objects cast into it, the level's shadows are baked): 0 turns them off for a renderer.
	float g_flHaloSunShadows < Attribute( "HaloSunShadows" ); Default( 1.0 ); >;

	// The casters' bounding spheres (Halo3SceneUpkeep): a shadow on the level only counts near the object casting it, as MCC clips each object's shadow to its bounds.
	StructuredBuffer<float4> g_HaloShadowCasters < Attribute( "HaloShadowCasters" ); >;
	// Their bounds' half extents (xyz), same order: a flat object's sphere (Zanzibar's wheel, 12.7 units) holds things that aren't it.
	StructuredBuffer<float4> g_Halo3ShadowCasterExtents < Attribute( "Halo3ShadowCasterExtents" ); >;
	int g_nHaloShadowCasterCount < Attribute( "HaloShadowCasterCount" ); Default( -1 ); >;
	float g_flShadowDebug < Attribute( "Halo3ShadowDebug" ); Default( 0.0 ); >;

	// MCC's cook_torrance area specular tables (Halo3SceneUpkeep: the rasterizer cc0236 and dd0236 bitmaps, 32x32 RGBA16F), H3CookTorrance.
	Texture2D g_tCookTorranceCc < Attribute( "Halo3CookTorranceCc" ); >;
	Texture2D g_tCookTorranceDd < Attribute( "Halo3CookTorranceDd" ); >;
	float g_flHasCookTorranceTables < Attribute( "Halo3HasCookTorranceTables" ); Default( 0.0 ); >;

	// The ambient radiance standing in for missing lighting (unlit geometry, objects before their probe is set): sky-ish above, darker below.
	float3 g_vNeutralAbove < Default3( 0.22, 0.23, 0.25 ); >;
	float3 g_vNeutralBelow < Default3( 0.08, 0.08, 0.08 ); >;

	// An object's change colours (primary..quaternary, linear); white until a game sets them.
	float4 g_vChangeColor0 < Attribute( "Halo3ChangeColor0" ); Default4( 1.0, 1.0, 1.0, 1.0 ); >;
	float4 g_vChangeColor1 < Attribute( "Halo3ChangeColor1" ); Default4( 1.0, 1.0, 1.0, 1.0 ); >;
	float4 g_vChangeColor2 < Attribute( "Halo3ChangeColor2" ); Default4( 1.0, 1.0, 1.0, 1.0 ); >;
	float4 g_vChangeColor3 < Attribute( "Halo3ChangeColor3" ); Default4( 1.0, 1.0, 1.0, 1.0 ); >;

	// 1 once a game set the change colours (Halo3Lightmap.SetChangeColors): an unset attribute reads 0, not its default, so without this they're white.
	float g_flHasChangeColors < Attribute( "Halo3HasChangeColors" ); Default( 0.0 ); >;

	float3 H3ChangeColor( int k )
	{
		if ( g_flHasChangeColors < 0.5 )
			return 1;
		return k == 0 ? g_vChangeColor0.rgb : k == 1 ? g_vChangeColor1.rgb : k == 2 ? g_vChangeColor2.rgb : g_vChangeColor3.rgb;
	}

	// Exposure (stops) and tone curve (H3FinishColor). The material's value (from the map's camera fx) unless a scene sets Halo3ExposureOverride (x = stops, y = 1 to use it).
	float g_flExposureStops < Default( 1.0 ); >;
	float2 g_vExposureOverride < Attribute( "Halo3ExposureOverride" ); Default2( 0.0, 0.0 ); >;

	// Tone curve on x = sqrt( exposed colour ) (final_composite's tone_curve_constants, as MCC sets them): min( x, a ) * ( b + c x + d x^2 ).
	float4 g_vToneCurve < Attribute( "Halo3ToneCurve" ); Default4( 1.0, 1.5, 0.0, -0.5 ); >;
	// 1 while Halo 3's post-processing runs on the view (Halo3PostFx): H3FinishColor writes x and leaves the tone curve to the composite, and the exposure (stops, texel 0's x) is its auto exposure's.
	float g_flHdr < Attribute( "Halo3Hdr" ); Default( 0.0 ); >;
	// The exposure state as a bindless index (Texture.Index, -1 none). The 3D skybox's draws only see their renderers' attributes, which Halo3SceneUpkeep sets to the scene's.
	float g_flExposureState < Attribute( "Halo3ExposureStateIndex" ); Default( -1.0 ); >;
	static const float H3WhitePoint = 1.4938016;
	// Self-illumination exposure from the camera fx (self illum preferred exposure, exposure change): MCC exposes self-illumination by 2^(preferred + change * (stops - preferred)) instead of 2^stops (g_alt_exposure; Valhalla's capture: 0.28 of the scene's exposure). (0, 1) = the scene's exposure.
	float2 g_vSelfIllumExposure < Attribute( "Halo3SelfIllumExposure" ); Default2( 0.0, 1.0 ); >;

	// Atmosphere fog (skya setting, H3Atmosphere), in Halo world units: sun direction and distance bias, sun colour x intensity and max fog thickness, Mie beta and phase g, Rayleigh beta and sea level, Rayleigh theta prefix and height scale, Mie theta prefix and height scale. g_vFogScale: x = 1 / s&box units per world unit, y = 1 to fog.
	float4 g_vFogSun < Default4( 0.0, 0.0, 1.0, 0.0 ); >;
	float4 g_vFogSunColor < Default4( 1.0, 1.0, 1.0, 65536.0 ); >;
	float4 g_vFogMie < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float4 g_vFogRayleigh < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float4 g_vFogRayleighTheta < Default4( 0.0, 0.0, 0.0, 1.0 ); >;
	float4 g_vFogMieTheta < Default4( 0.0, 0.0, 0.0, 1.0 ); >;
	float2 g_vFogScale < Default2( 1.0, 0.0 ); >;

	// 1 on render methods that don't fog (render method flag 1).
	float g_flNoFog < Default( 0.0 ); >;

	// 1 on render methods fogged with the sky's atmosphere setting (render method flag 2, the sky's shaders): the material's own fog, never the scene's.
	float g_flSkyFog < Default( 0.0 ); >;

	// The scene's fog and exposure, set on its live renderers (Halo3Materials.SetSceneLook) when Halo3SceneLook is 1: shared models' materials were made from whichever map first had the tag, so their own fog and exposure can be another map's.
	float g_flSceneLook < Attribute( "Halo3SceneLook" ); Default( 0.0 ); >;
	float g_flSceneExposureStops < Attribute( "Halo3SceneExposureStops" ); Default( 0.0 ); >;
	float4 g_vSceneFogSun < Attribute( "Halo3FogSun" ); Default4( 0.0, 0.0, 1.0, 0.0 ); >;
	float4 g_vSceneFogSunColor < Attribute( "Halo3FogSunColor" ); Default4( 1.0, 1.0, 1.0, 65536.0 ); >;
	float4 g_vSceneFogMie < Attribute( "Halo3FogMie" ); Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float4 g_vSceneFogRayleigh < Attribute( "Halo3FogRayleigh" ); Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float4 g_vSceneFogRayleighTheta < Attribute( "Halo3FogRayleighTheta" ); Default4( 0.0, 0.0, 0.0, 1.0 ); >;
	float4 g_vSceneFogMieTheta < Attribute( "Halo3FogMieTheta" ); Default4( 0.0, 0.0, 0.0, 1.0 ); >;
	float2 g_vSceneFogScale < Attribute( "Halo3FogScale" ); Default2( 1.0, 0.0 ); >;

	static const float DetailMultiplier = 4.59479;
	static const float HaloPi = 3.14159265;
	#define LumaWeights float3( 0.212656, 0.715158, 0.072186 )

	// Pre-lighting multiply decals on this surface (Halo3DecalBuilder draws the surface again with up to three, where they overlap): each base map multiplies the albedo before lighting in turn, as MCC's albedo buffer pass (the raw texel, its trilinear clamped sampler), so the surface's specular stays. g_flDecalAlbedoN: 0 none, 1 multiply, 2 double multiply.
	SamplerState g_sDecalClamp < Filter( TRILINEAR ); AddressU( CLAMP ); AddressV( CLAMP ); >;
	// 1: the decal layers' uv is the second uv set (multiply decals on render models: their own uv there, the surface's in the first, as objects light by probe and have no lightmap uv).
	float g_flDecalAlbedoUv1 < Default( 0.0 ); >;
	// 1: a render model's decal, whose pixels outside its 0..1 uv are killed, as MCC's decal pixel shaders do with pixel_kill_enabled (Sandtrap's Behemoth stains reach past it, and a clamped sample smeared their dark edge over the hull).
	float g_flDecalKill < Default( 0.0 ); >;
	float g_flDecalAlbedo0 < Default( 0.0 ); >;
	float g_flDecalAlbedo1 < Default( 0.0 ); >;
	float g_flDecalAlbedo2 < Default( 0.0 ); >;
	CreateInputTexture2D( DecalAlbedoMap0, Linear, 8, "None", "_color", ",0/,0/0", Default4( 1.0, 1.0, 1.0, 1.0 ) );
	CreateInputTexture2D( DecalAlbedoMap1, Linear, 8, "None", "_color", ",0/,0/0", Default4( 1.0, 1.0, 1.0, 1.0 ) );
	CreateInputTexture2D( DecalAlbedoMap2, Linear, 8, "None", "_color", ",0/,0/0", Default4( 1.0, 1.0, 1.0, 1.0 ) );
	Texture2D g_tDecalAlbedoMap0 < Channel( RGBA, Box( DecalAlbedoMap0 ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( false ); >;
	Texture2D g_tDecalAlbedoMap1 < Channel( RGBA, Box( DecalAlbedoMap1 ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( false ); >;
	Texture2D g_tDecalAlbedoMap2 < Channel( RGBA, Box( DecalAlbedoMap2 ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( false ); >;
	float4 g_vDecalAlbedoMap0Xform < Default4( 1.0, 1.0, 0.0, 0.0 ); >;
	float4 g_vDecalAlbedoMap1Xform < Default4( 1.0, 1.0, 0.0, 0.0 ); >;
	float4 g_vDecalAlbedoMap2Xform < Default4( 1.0, 1.0, 0.0, 0.0 ); >;
	float g_flDecalAlbedoMap0Srgb < Default( 0.0 ); >;
	float g_flDecalAlbedoMap1Srgb < Default( 0.0 ); >;
	float g_flDecalAlbedoMap2Srgb < Default( 0.0 ); >;

	float3 H3DecalLayer( Texture2D map, float4 xform, float srgb, float scale, float2 uv )
	{
		if ( scale <= 0 || ( g_flDecalKill > 0.5 && ( any( uv < 0 ) || any( uv > 1 ) ) ) )
			return 1;
		float3 t = map.Sample( g_sDecalClamp, uv * xform.xy + xform.zw ).rgb;
		if ( srgb > 0.5 )
			t = H3SrgbToLinear( t );
		return t * scale;
	}

	float3 H3DecalAlbedo( float4 uv01, float2 uv2 )
	{
		return H3DecalLayer( g_tDecalAlbedoMap0, g_vDecalAlbedoMap0Xform, g_flDecalAlbedoMap0Srgb, g_flDecalAlbedo0, uv01.xy )
			* H3DecalLayer( g_tDecalAlbedoMap1, g_vDecalAlbedoMap1Xform, g_flDecalAlbedoMap1Srgb, g_flDecalAlbedo1, uv01.zw )
			* H3DecalLayer( g_tDecalAlbedoMap2, g_vDecalAlbedoMap2Xform, g_flDecalAlbedoMap2Srgb, g_flDecalAlbedo2, uv2 );
	}

	float4 H3Read( Texture2D t, float4 xform, float4 scroll, float srgb, float2 uv )
	{
		float4 c = t.Sample( g_sWrap, uv * xform.xy + H3Scrolled( xform, scroll ) );
		if ( srgb > 0.5 )
			c.rgb = H3SrgbToLinear( c.rgb );
		return c;
	}

	#define SAMPLE( name, uv ) H3Read( g_t##name, g_v##name##Xform, g_v##name##Scroll, g_fl##name##Srgb, uv )

	// A tangent space normal from a bump map's first two channels, z rebuilt. MCC samples every bump map as its texture format reads: signed formats (DXN, V8U8; re-biased by the loader, so x 2 - 1 here) give both signs, while an unsigned one (unsignedXy: DXT5 like the Wraith's, whose red and green are mostly 0) is used as it comes, so most of it is flat and the rest tilts toward +x +y (riverworld's Wraith, MCC albedo pass: BC3_UNORM bump, bump.xy straight into the normal).
	float3 H3BumpNormal( float4 c, float unsignedXy = 0 )
	{
		float2 xy = unsignedXy > 0.5 ? c.xy : c.xy * 2 - 1;
		return float3( xy, sqrt( saturate( 1 - dot( xy, xy ) ) ) );
	}

	// ---- Static lighting: what every lighting path hands the material model.
	struct Halo3Light
	{
		// The L1 SH with the dominant light's own projection removed: sh0 (RGB) and the per-channel L1 vectors (x, y, z) as dot(n, L1) wants them.
		float3 C0;
		float3 L1r;
		float3 L1g;
		float3 L1b;

		// The dominant light: direction and intensity (0 for probes, whose SH keeps everything).
		float3 Direction;
		float3 Intensity;

		// The dominant light as the material models' analytic light and the sun's shadow see it: the same for lightmaps, the probe's own for objects (whose diffuse keeps it inside the SH: SunInSh 1).
		float3 SunDirection;
		float3 SunIntensity;
		float SunInSh;

		// L2 terms for probes (zero otherwise): per-channel xy, yz, 3z^2-1, xz, x^2-y^2 coefficients.
		float3 C4;
		float3 C5;
		float3 C6;
		float3 C7;
		float3 C8;
	};

	// Splits an L1 SH (D3DX order, sh1 ~ -y, sh2 ~ z, sh3 ~ -x) and its dominant light intensity into the residual SH and the analytic light.
	Halo3Light H3FromL1( float3 sh0, float3 sh1, float3 sh2, float3 sh3, float3 intensity )
	{
		Halo3Light l = (Halo3Light)0;
		float3 v = float3( dot( LumaWeights, sh3 ), dot( LumaWeights, sh1 ), dot( LumaWeights, sh2 ) );
		float3 d = float3( -v.x, -v.y, v.z );
		float len = length( d );
		l.Direction = len > 1e-6 ? d / len : float3( 0, 0, 1 );
		l.Intensity = max( intensity, 0 );
		l.SunDirection = l.Direction;
		l.SunIntensity = l.Intensity;
		l.C0 = sh0 - 0.282095 * l.Intensity;
		l.L1r = float3( sh3.r, sh1.r, -sh2.r ) + 0.488602 * l.Direction * l.Intensity.r;
		l.L1g = float3( sh3.g, sh1.g, -sh2.g ) + 0.488602 * l.Direction * l.Intensity.g;
		l.L1b = float3( sh3.b, sh1.b, -sh2.b ) + 0.488602 * l.Direction * l.Intensity.b;
		return l;
	}

	// One DXT5 pair of the lightprobe arrays as a signed HDR RGB: ((a + b) * 2 - 2) * a.a * b.a * scale.
	float3 H3DecodePair( Texture2DArray t, float2 uv, float layer, float scale )
	{
		float4 a = t.SampleLevel( g_sLightmap, float3( uv, layer ), 0 );
		float4 b = t.SampleLevel( g_sLightmap, float3( uv, layer + 1 ), 0 );
		return ( ( a.rgb + b.rgb ) * 2 - 2 ) * ( a.a * b.a * scale );
	}

	// The lighting of a shading normal's surroundings when nothing better exists.
	Halo3Light H3NeutralLight()
	{
		// A constant SH with a vertical gradient: g_vNeutralAbove facing +z, g_vNeutralBelow facing -z (as irradiance / pi).
		float3 mean = ( g_vNeutralAbove + g_vNeutralBelow ) * 0.5;
		float3 diff = ( g_vNeutralAbove - g_vNeutralBelow ) * 0.5;
		Halo3Light l = (Halo3Light)0;
		l.C0 = mean * HaloPi / 0.886227;
		float3 z = -diff * HaloPi / 1.023328;
		l.L1r = float3( 0, 0, z.r );
		l.L1g = float3( 0, 0, z.g );
		l.L1b = float3( 0, 0, z.b );
		l.Direction = float3( 0, 0, 1 );
		l.SunDirection = l.Direction;
		return l;
	}

	Halo3Light H3StaticLight( PixelInput i )
	{
		int mode = (int)round( g_flLightingMode );
		if ( mode == 1 && g_flLightprobeArray >= 0 )
		{
			float2 uv = i.vTextureCoords.zw;
			Texture2DArray lp = Bindless::GetTexture2DArray( (int)round( g_flLightprobeArray ) );
			float3 sh0 = H3DecodePair( lp, uv, 0, g_vLightmapScales.x );
			float3 sh1 = H3DecodePair( lp, uv, 2, g_vLightmapScales.y );
			float3 sh2 = H3DecodePair( lp, uv, 4, g_vLightmapScales.z );
			float3 sh3 = H3DecodePair( lp, uv, 6, g_vLightmapScales.w );
			float3 intensity = 0;
			if ( g_flIntensityArray >= 0 )
				intensity = H3DecodePair( Bindless::GetTexture2DArray( (int)round( g_flIntensityArray ) ), uv, 0, g_flLightmapScale4 );
			return H3FromL1( sh0, sh1, sh2, sh3, intensity );
		}
		if ( mode == 2 )
			return H3FromL1( i.vSh0.xyz, i.vSh1.xyz, i.vSh2.xyz, i.vSh3.xyz, float3( i.vSh0.w, i.vSh1.w, i.vSh2.w ) );
		if ( mode == 3 && g_flHasProbe > 0.5 )
		{
			// An L2 probe: no separate dominant light; its direction (for highlights) from the luminance-weighted L1.
			Halo3Light l = (Halo3Light)0;
			float3 sh1 = g_vProbeSh1.rgb, sh2 = g_vProbeSh2.rgb, sh3 = g_vProbeSh3.rgb;
			l.C0 = g_vProbeSh0.rgb;
			l.L1r = float3( sh3.r, sh1.r, -sh2.r );
			l.L1g = float3( sh3.g, sh1.g, -sh2.g );
			l.L1b = float3( sh3.b, sh1.b, -sh2.b );
			float3 d = -float3( dot( LumaWeights, sh3 ), dot( LumaWeights, sh1 ), -dot( LumaWeights, sh2 ) );
			l.Direction = length( d ) > 1e-6 ? normalize( d ) : float3( 0, 0, 1 );
			if ( g_vProbeSun.w > 0.5 )
			{
				l.Direction = normalize( g_vProbeSun.xyz );
				l.SunIntensity = max( g_vProbeSunColor.rgb, 0 );
				l.SunInSh = 1;
			}
			l.SunDirection = l.Direction;
			l.C4 = g_vProbeSh4.rgb;
			l.C5 = g_vProbeSh5.rgb;
			l.C6 = g_vProbeSh6.rgb;
			l.C7 = g_vProbeSh7.rgb;
			l.C8 = g_vProbeSh8.rgb;
			return l;
		}
		return H3NeutralLight();
	}

	// Irradiance / pi of the residual SH toward n (L1 and, for probes, L2), D3DX basis with the cosine lobe folded in.
	float3 H3ShIrradiance( Halo3Light l, float3 n )
	{
		float3 e = 0.886227 * l.C0 - 1.023328 * float3( dot( n, l.L1r ), dot( n, l.L1g ), dot( n, l.L1b ) );
		e += 0.858086 * n.x * n.y * l.C4 - 0.858086 * n.y * n.z * l.C5 + 0.247708 * ( 3 * n.z * n.z - 1 ) * l.C6
			- 0.858086 * n.x * n.z * l.C7 + 0.429043 * ( n.x * n.x - n.y * n.y ) * l.C8;
		return max( e, 0 ) / HaloPi;
	}

	// Diffuse light: the residual SH plus the dominant light's lambert term (0.281 I N.L, as the static_per_pixel shader).
	float3 H3DiffuseLight( Halo3Light l, float3 n )
	{
		return H3ShIrradiance( l, n ) + 0.281 * l.Intensity * saturate( dot( l.Direction, n ) );
	}

	// Irradiance / pi toward n of a directional light's L2 SH projection (unclamped): what the sun is inside a probe-lit object's SH.
	float3 H3SunShIrradiance( float3 d, float3 i, float3 n )
	{
		float e = 0.886227 * 0.282095 + 1.023328 * 0.488603 * dot( n, d )
			+ 0.858086 * 1.092548 * ( n.x * n.y * d.x * d.y + n.y * n.z * d.y * d.z + n.x * n.z * d.x * d.z )
			+ 0.247708 * 0.315392 * ( 3 * n.z * n.z - 1 ) * ( 3 * d.z * d.z - 1 )
			+ 0.429043 * 0.546274 * ( n.x * n.x - n.y * n.y ) * ( d.x * d.x - d.y * d.y );
		return i * e / HaloPi;
	}

	// Diffuse light split into the sun's part (what its shadow takes away) and the rest: lightmapped surfaces have the sun as their analytic term, probe-lit objects have it inside their SH.
	void H3DiffuseSplit( Halo3Light l, float3 n, out float3 ambient, out float3 sun )
	{
		float3 total = H3DiffuseLight( l, n );
		if ( l.SunInSh > 0.5 )
		{
			sun = min( max( H3SunShIrradiance( l.SunDirection, l.SunIntensity, n ), 0 ), total );
			ambient = total - sun;
		}
		else
		{
			sun = 0.281 * l.Intensity * saturate( dot( l.Direction, n ) );
			ambient = total - sun;
		}
	}

	// ---- Objects' shadows: the sun's shadow map (Halo3SunLight), which only objects cast into; the level's own shadows are in its lightmaps.
	// Lifted from halo1_model.shader's HaloSunVisibility: the cascade is the first whose square holds the point (and whose depth range does), the filter's turn is fixed, and the outer 8% of each square blends into the next, the last fading out.
	static const float SunCascadeBlend = 0.08;

	float3 H3SunCascadePosition( uint c, float3 positionWs, out float edge )
	{
		float3 shadowPos = mul( g_DirectionalLightWorldToShadowViewMatrices[c], float4( positionWs, 1 ) ).xyz;
		edge = max( abs( shadowPos.x - 0.5 ), abs( shadowPos.y - 0.5 ) ) * 2;
		if ( shadowPos.z <= 0 || shadowPos.z >= 1 )
			edge = 2;
		return shadowPos;
	}

	float H3SunCascadeVisibility( float3 positionWs, float3 normalWs, out int cascade )
	{
		cascade = -1;
		uint count = min( g_DirectionalLightCascadeCount, MAX_CASCADE_COUNT );

		[loop]
		for ( uint c = 0; c < count; c++ )
		{
			float edge;
			H3SunCascadePosition( c, positionWs, edge );
			if ( edge >= 0.97 )
				continue;

			cascade = c;
			float visibility = DirectionalLightShadow::SampleCascade( c, positionWs, normalWs, float2( 0, 0 ) );
			float blend = saturate( ( edge - ( 0.97 - SunCascadeBlend ) ) / SunCascadeBlend );
			if ( blend > 0 )
			{
				float next = 1;
				float nextEdge;
				if ( c + 1 < count )
				{
					H3SunCascadePosition( c + 1, positionWs, nextEdge );
					if ( nextEdge < 0.97 )
						next = DirectionalLightShadow::SampleCascade( c + 1, positionWs, normalWs, float2( 0, 0 ) );
				}
				visibility = lerp( visibility, next, blend );
			}
			return visibility;
		}

		return 1;
	}

	// Where the sun is blocked from a point: the mean depth of the occluding shadow map texels over the filter's footprint, taken back toward the sun (halo1_environment.shader's HaloSunBlocker).
	bool H3SunBlocker( uint c, float3 positionWs, out float3 blocker, out float texelWorldSize )
	{
		float4x4 worldToShadow = g_DirectionalLightWorldToShadowViewMatrices[c];
		float3 shadowPos = mul( worldToShadow, float4( positionWs, 1 ) ).xyz;
		Texture2D map = Bindless::GetTexture2D( g_DirectionalLightShadowMapTextureIndex[c] );
		texelWorldSize = g_DirectionalLightInverseShadowMapSize / length( worldToShadow[0].xyz );

		float sum = 0;
		float found = 0;
		[unroll]
		for ( int y = -2; y <= 2; y++ )
		{
			[unroll]
			for ( int x = -2; x <= 2; x++ )
			{
				float depth = map.SampleLevel( g_sPointClamp, shadowPos.xy + float2( x, y ) * 2 * g_DirectionalLightInverseShadowMapSize, 0 ).r;
				if ( depth > shadowPos.z )
				{
					sum += depth;
					found++;
				}
			}
		}

		blocker = positionWs;
		if ( found == 0 )
			return false;

		float3 zGrad = worldToShadow[2].xyz;
		blocker = positionWs + zGrad * ( sum / found - shadowPos.z ) / dot( zGrad, zGrad );
		return true;
	}

	// How much of a shadow on the level counts here: only inside a caster's volume, its bounding sphere's depth along the sun (the caster's sphere holding the blocker), fading over the far half as MCC's shadow_apply fades its projection (1 - (2d - 1)^4, d across the object's bounds, where it draws its volume). The CE reach of four radii past it let Standoff's roof pallet shadow the room below.
	float H3ShadowReach( float3 positionWs, int cascade )
	{
		if ( g_nHaloShadowCasterCount < 0 )
			return 1;

		float3 blocker;
		float texelWorldSize;
		if ( cascade < 0 || !H3SunBlocker( cascade, positionWs, blocker, texelWorldSize ) )
			return 0;

		float3 travel = normalize( g_DirectionalLightDirection.xyz );
		float reach = 0;

		[loop]
		for ( int i = 0; i < g_nHaloShadowCasterCount; i++ )
		{
			float4 caster = g_HaloShadowCasters[i];
			float radius = caster.w;
			float3 toBlocker = blocker - caster.xyz;
			float tolerance = radius + 5 * texelWorldSize;
			if ( dot( toBlocker, toBlocker ) > tolerance * tolerance )
				continue;
			if ( any( abs( toBlocker ) > g_Halo3ShadowCasterExtents[i].xyz + 5 * texelWorldSize ) )
				continue;

			float along = dot( positionWs - caster.xyz, travel );
			if ( abs( along ) > radius )
				continue;

			float f = saturate( along / radius );
			reach = max( reach, 1 - f * f * f * f );
		}

		return reach;
	}

	// The sun's visibility at a point (1 lit, 0 in an object's shadow), for the light it gives: where the surface's dominant light isn't the sun (indoors, under lamps) nothing is shadowed; on the level, only inside a caster's volume (H3ShadowReach).
	float H3SunVisibility( float3 positionWs, float3 normalWs, Halo3Light l, bool level )
	{
		if ( g_flHaloSunShadows < 0.5 || !g_DirectionalLightEnabled )
			return 1;

		// MCC's shadow_apply darkens the opaque G-buffer before transparents draw, so glass and other blended surfaces are never in an object's shadow (only what's behind them).
		#if HALO3_TRANSPARENT
			return 1;
		#endif

		float sunward = smoothstep( 0.25, 0.75, dot( l.SunDirection, -normalize( g_DirectionalLightDirection.xyz ) ) );
		if ( sunward <= 0 )
			return 1;

		int cascade;
		float visibility = H3SunCascadeVisibility( positionWs, normalWs, cascade );
		if ( visibility >= 1 )
			return 1;

		float reach = level ? H3ShadowReach( positionWs, cascade ) : 1;
		return lerp( 1, visibility, sunward * reach );
	}

	// What's left of the lit colour (diffuse, specular and reflections alike, as MCC's shadow_apply darkens the frame) in an object's shadow: HaloShadowCore of it where the sun is HaloShadowSunShare or more of the diffuse light (MCC's shadow core on the Banshee pad measures 0.14 of the sunlit pad, compare/riverworld-valhalla/22_banshee_pad_mcc.png), less where the sun was less of it, nothing in baked shade: a shadow blends into the level's own instead of darkening it again as MCC's does.
	static const float HaloShadowCore = 0.14;
	static const float HaloShadowSunShare = 0.85;

	float H3SunShadowFactor( Halo3Light l, float3 n, float visibility )
	{
		if ( visibility >= 1 )
			return 1;
		float3 ambient, sun;
		H3DiffuseSplit( l, n, ambient, sun );
		float share = dot( LumaWeights, sun ) / max( dot( LumaWeights, ambient + sun ), 1e-5 );
		float dark = saturate( share / HaloShadowSunShare ) * ( 1 - HaloShadowCore );
		return 1 - ( 1 - visibility ) * dark;
	}

	// Trilinear, as MCC's cube samplers: water reads the cube at the level its waves pick, and Valhalla's sky alpha (1..3 / 255) only evens out between levels.
	SamplerState g_sEnv < Filter( Trilinear ); AddressU( CLAMP ); AddressV( CLAMP ); >;

	// A direction's cube face (+X -X +Y -Y +Z -Z) and its position on it, -1..1 (D3D face rules).
	float H3EnvFace( float3 dir, out float2 st )
	{
		float3 a = abs( dir );
		if ( a.x >= a.y && a.x >= a.z )
		{
			st = ( dir.x > 0 ? float2( -dir.z, -dir.y ) : float2( dir.z, -dir.y ) ) / max( a.x, 1e-6 );
			return dir.x > 0 ? 0 : 1;
		}
		if ( a.y >= a.z )
		{
			st = ( dir.y > 0 ? float2( dir.x, dir.z ) : float2( dir.x, -dir.z ) ) / max( a.y, 1e-6 );
			return dir.y > 0 ? 2 : 3;
		}
		st = ( dir.z > 0 ? float2( dir.x, -dir.y ) : float2( -dir.x, -dir.y ) ) / max( a.z, 1e-6 );
		return dir.z > 0 ? 4 : 5;
	}

	// The direction through a position on a cube face, the inverse of H3EnvFace.
	float3 H3EnvFaceDir( float face, float2 st )
	{
		if ( face < 0.5 ) return float3( 1, -st.y, -st.x );
		if ( face < 1.5 ) return float3( -1, -st.y, st.x );
		if ( face < 2.5 ) return float3( st.x, 1, st.y );
		if ( face < 3.5 ) return float3( st.x, -1, -st.y );
		if ( face < 4.5 ) return float3( st.x, -st.y, 1 );
		return float3( -st.x, -st.y, -1 );
	}

	bool H3EnvIsObject() { return g_flHasEnvMap < 0.5 && g_flHasObjectEnvMap > 0.5; }

	// One face of the bound cube atlas at a position on it, kept half a texel of the coarsest level read inside so filtering never reaches the next face in the atlas.
	float4 H3EnvAtlasFace( float face, float2 st, float lod, float edge )
	{
		float2 uv = clamp( st * 0.5 + 0.5, edge, 1 - edge );
		float2 atlas = float2( ( face + uv.x ) / 6.0, uv.y );
		return H3EnvIsObject() ? g_tObjectEnvMap.SampleLevel( g_sEnv, atlas, lod ) : g_tEnvMap.SampleLevel( g_sEnv, atlas, lod );
	}

	// A direction's texel of the cube atlas (as halo2_opaque's SampleCubeAtlas): linear colour and alpha (the intensity the other env paths scale by; water reads it as its sunspot mask).
	// Within half a texel of a face's edge it blends toward the neighbouring face's edge texels, as a cube sampler filters across faces, so the atlas shows no seams at the blurry levels.
	float4 H3SampleEnvRaw( float3 dir, float lod = 0 )
	{
		float width, height, levels;
		if ( H3EnvIsObject() )
			g_tObjectEnvMap.GetDimensions( 0, width, height, levels );
		else
			g_tEnvMap.GetDimensions( 0, width, height, levels );
		float texels = max( height * exp2( -min( ceil( lod ), levels - 1 ) ), 1 );
		float edge = 0.5 / texels;

		float2 st;
		float face = H3EnvFace( dir, st );
		float4 c = H3EnvAtlasFace( face, st, lod, edge );
		float2 w = saturate( 0.5 - ( 1 - abs( st ) ) * 0.5 * texels );
		if ( w.x > 0 )
		{
			float2 across;
			float other = H3EnvFace( H3EnvFaceDir( face, float2( sign( st.x ) * ( 1 + 1 / texels ), st.y ) ), across );
			c = lerp( c, H3EnvAtlasFace( other, across, lod, edge ), w.x );
		}
		if ( w.y > 0 )
		{
			float2 across;
			float other = H3EnvFace( H3EnvFaceDir( face, float2( st.x, sign( st.y ) * ( 1 + 1 / texels ) ) ), across );
			c = lerp( c, H3EnvAtlasFace( other, across, lod, edge ), w.y );
		}
		bool srgb = ( H3EnvIsObject() ? g_flObjectEnvMapSrgb : g_flEnvMapSrgb ) > 0.5;
		return float4( srgb ? H3SrgbToLinear( c.rgb ) : c.rgb, c.a );
	}

	// The level a cube sampler picks for a direction (trilinear, no anisotropy): from how fast it turns across the pixel, in texels of the face it lands on.
	float H3EnvImplicitLod( float3 dir )
	{
		float width, height, levels;
		if ( H3EnvIsObject() )
			g_tObjectEnvMap.GetDimensions( 0, width, height, levels );
		else
			g_tEnvMap.GetDimensions( 0, width, height, levels );
		float3 a = abs( dir );
		float footprint = max( length( ddx( dir ) ), length( ddy( dir ) ) ) / max( max( a.x, max( a.y, a.z ) ), 1e-6 );
		return max( log2( footprint * height * 0.5 ), 0 );
	}

	// The cube's colour times its intensity, linear.
	float3 H3SampleEnv( float3 dir, float lod )
	{
		float4 c = H3SampleEnvRaw( dir, lod );
		return c.rgb * c.a;
	}

	// The static light's radiance toward a direction as MCC's environment map term reads it (static_per_pixel, per_pixel env): the full SH (dominant light put back) at L0 plus the L1 band softened by the roughness.
	float3 H3ShRadiance( Halo3Light l, float3 r, float roughness )
	{
		float3 c0 = l.C0 + 0.282095 * l.Intensity;
		float3 shift = 0.488602 * l.Direction;
		float3 l1 = float3( dot( r, l.L1r - shift * l.Intensity.r ), dot( r, l.L1g - shift * l.Intensity.g ), dot( r, l.L1b - shift * l.Intensity.b ) );
		float k = -0.6 * ( 0.512895 - 0.140737 * roughness - 0.00266 * roughness * roughness );
		return max( 0.282095 * c0 + k * l1, 0.001 );
	}

	// ---- Material models (material_model category).
	struct H3Surface
	{
		float3 Albedo;
		float Alpha;
		float SpecularMask;
		float3 Normal;
		float3 VertexNormal;
		float3 View;
		float2 Uv;

		// The sun's visibility here (H3SunVisibility): 1 lit.
		float SunVisibility;

		// An object's ambient PRT (1 - vSh1.w, set by RenderModelBuilder.AmbientPrt): MCC scales its probe's diffuse, specular and environment light by it; 1 elsewhere.
		float Prt;
	};

	// Exact dielectric fresnel from f0, as the lifted cook_torrance.
	float3 H3Fresnel( float3 f0, float vh )
	{
		float3 e = sqrt( min( f0, 0.999 ) );
		float3 eta = ( 1 + e ) / ( 1 - e );
		float3 g = sqrt( max( eta * eta + vh * vh - 1, 0 ) );
		float3 a = g - vh, b = g + vh;
		float3 c = ( vh * b - 1 ) / ( vh * a + 1 );
		return 0.5 * a * a / ( b * b + 1e-5 ) * ( 1 + c * c );
	}

	float3 H3SchlickFresnel( float3 f0, float nv )
	{
		float k = pow( 1 - saturate( nv ), 5 );
		return f0 + ( 1 - f0 ) * k;
	}

	// MCC's analytical anti-shadow (every static_per_pixel / per_vertex / sh template alike): the dominant light's highlight times (1 - min(residual / full, 1))^(100 control), from the green channel's L0 and L1 energy of the static light without and with the dominant light's projection, so it fades where the baked light isn't the sun's (shade, and the edge of a baked shadow, where the dominant direction swings).
	float H3AntiShadow( Halo3Light l )
	{
		if ( g_flAntiShadowControl <= 0 )
			return 1;
		float residualC0, fullC0;
		float3 residualL1, fullL1;
		if ( l.SunInSh > 0.5 )
		{
			fullC0 = l.C0.g;
			fullL1 = l.L1g;
			residualC0 = fullC0 - 0.282095 * l.SunIntensity.g;
			residualL1 = fullL1 + 0.488602 * l.SunDirection * l.SunIntensity.g;
		}
		else
		{
			residualC0 = l.C0.g;
			residualL1 = l.L1g;
			fullC0 = residualC0 + 0.282095 * l.Intensity.g;
			fullL1 = residualL1 - 0.488602 * l.Direction * l.Intensity.g;
		}
		float residual = residualC0 * residualC0 + dot( residualL1, residualL1 );
		float full = fullC0 * fullC0 + dot( fullL1, fullL1 );
		float ratio = residual > 0 ? min( residual / max( full, 1e-10 ), 1 ) : 0;
		return pow( 1 - ratio, 100 * g_flAntiShadowControl );
	}

	// The lifted cook_torrance: Beckmann D, exact fresnel, Cook-Torrance G for the dominant light; area specular from the SH toward the reflection (the engine's two lookup tables approximated by Schlick fresnel times the SH there).
	float3 H3CookTorrance( H3Surface s, Halo3Light l, float3 f0, float roughness, out float3 area )
	{
		float3 n = s.Normal, v = s.View, ld = l.Direction;
		float nv = dot( v, n );
		float3 r = 2 * nv * n - v;
		float3 h = normalize( v + ld );
		float nh = dot( n, h ), vh = dot( v, h ), nl = dot( ld, n );
		float g = saturate( 2 * nh * min( nv, nl ) / ( saturate( vh ) + 1e-5 ) );
		float3 f = H3Fresnel( f0, saturate( vh ) );
		float m2 = max( roughness, 0.05 );
		m2 *= m2;
		float nh2 = nh * nh;
		float d = exp( ( nh2 - 1 ) / ( m2 * nh2 + 1e-6 ) ) / ( m2 * nh2 * nh2 + 1e-5 );
		float3 dominant = f * min( d * g / ( HaloPi * nv + 1e-5 ), 1 + dot( s.VertexNormal, ld ) );
		dominant = min( nv, nl ) > 0 ? l.SunIntensity * dominant : 0;

		dominant *= H3AntiShadow( l );

		if ( g_flHasCookTorranceTables > 0.5 )
		{
			// MCC's area specular (lifted static_sh / static_per_pixel): the two tables at u = sin of the view angle + 1/64, v = max(roughness, 0.05) weight the full SH's L0, -N.L1 and T'.L1 (T' the view's direction in the tangent plane) per channel, the L1 terms scaled by 0.35 + 0.65 max(Ld.R, 0); the fresnel colour blends the cc table's result against the dd table's at 1/100.
			float3 tangentView = v - n * nv;
			float tangentLength = length( tangentView );
			tangentView = tangentLength > 1e-5 ? tangentView / tangentLength : float3( 0, 0, 0 );
			float2 tableUv = float2( dot( v, tangentView ) + 0.015625, max( roughness, 0.05 ) );
			float3 tableA = g_tCookTorranceCc.SampleLevel( g_sLightmap, tableUv, 0 ).rgb;
			float3 tableB = g_tCookTorranceDd.SampleLevel( g_sLightmap, tableUv, 0 ).rgb;
			float towardSun = 0.35 + 0.65 * max( dot( l.SunDirection, r ), 0 );
			float3 shDc = l.C0 + 0.282095 * l.Intensity;
			float3 shRed = l.L1r - l.Direction * ( 0.488602 * l.Intensity.x );
			float3 shGreen = l.L1g - l.Direction * ( 0.488602 * l.Intensity.y );
			float3 shBlue = l.L1b - l.Direction * ( 0.488602 * l.Intensity.z );
			float3 basisRed = float3( shDc.x, -dot( n, shRed ) * towardSun, dot( tangentView, shRed ) * towardSun );
			float3 basisGreen = float3( shDc.y, -dot( n, shGreen ) * towardSun, dot( tangentView, shGreen ) * towardSun );
			float3 basisBlue = float3( shDc.z, -dot( n, shBlue ) * towardSun, dot( tangentView, shBlue ) * towardSun );
			float3 areaA = float3( dot( tableA, basisRed ), dot( tableA, basisGreen ), dot( tableA, basisBlue ) );
			float3 areaB = float3( dot( tableB, basisRed ), dot( tableB, basisGreen ), dot( tableB, basisBlue ) );
			area = max( lerp( areaB * 0.01, areaA, f0 ), 0 );
		}
		else
			area = H3SchlickFresnel( f0, nv ) * H3ShIrradiance( l, r ) * lerp( 1, 0.5, saturate( roughness ) );
		return dominant;
	}

	// The full SH's (dominant light inside) radiance toward r as MCC's two_lobe_phong reads it (static_sh of _0_2_0_1_2_2_1_0_0_0_0_0_0, lifted): 0.423142 L0 - 0.380524 dot(r, L1) per channel, where the compiled shader takes the red L0 for all three channels (r10 = p_lighting_constant_0.x * 0.423142 in every lobe), kept for its colour: Valhalla's warm sun makes the blue lobe stronger than its own L0 would.
	float3 H3PhongAreaRadiance( Halo3Light l, float3 r )
	{
		float3 c0 = ( l.C0 + 0.282095 * l.Intensity ).rrr;
		float3 l1 = float3(
			dot( r, l.L1r - l.Direction * ( 0.488602 * l.Intensity.r ) ),
			dot( r, l.L1g - l.Direction * ( 0.488602 * l.Intensity.g ) ),
			dot( r, l.L1b - l.Direction * ( 0.488602 * l.Intensity.b ) ) );
		return 0.423142 * c0 - 0.380524 * l1;
	}

	// A phong lobe for the dominant light: I (R.L)^power, plus the SH toward R as its area part.
	float3 H3PhongLobe( H3Surface s, Halo3Light l, float power, out float3 area )
	{
		float3 n = s.Normal, v = s.View;
		float3 r = 2 * dot( v, n ) * n - v;
		float rl = saturate( dot( r, l.Direction ) );
		area = H3ShIrradiance( l, r );
		return l.SunIntensity * pow( rl, max( power, 1 ) ) * saturate( 4 * dot( n, l.Direction ) );
	}

	// Glass's fresnel curve (static_per_pixel of a glass template): (1 - coefficient) (1 - N.V)^steepness + coefficient + bias.
	float H3GlassFresnel( float nv )
	{
		return ( 1 - g_flFresnelCoefficient ) * pow( 1 - saturate( nv ), g_flFresnelCurveSteepness ) + g_flFresnelCoefficient + g_flFresnelCurveBias;
	}

	// The last H3Shade's specular and environment terms and the roughness its environment map level comes from, for the debug views.
	static float3 g_vDebugSpecular = 0;
	static float3 g_vDebugEnv = 0;
	static float g_flDebugRoughness = 1;

	// Lit colour of an opaque surface: diffuse * albedo + specular (+ environment), all HDR.
	float3 H3Shade( H3Surface s, Halo3Light l )
	{
		int model = (int)round( g_flMaterialModel );
		float3 diffuse = H3DiffuseLight( l, s.Normal ) * s.Prt;
		float shadow = H3SunShadowFactor( l, s.Normal, s.SunVisibility );
		float nv = saturate( dot( s.View, s.Normal ) );

		// The roughness a dynamic environment map's level comes from: cook_torrance's and glass's roughness, the phong lobes' from their power.
		float envRoughness = g_flRoughness;

		// none: no lighting (only self-illumination shows).
		g_flDebugRoughness = envRoughness;
		if ( model == 5 )
			return 0;

		float3 specular = 0;
		float diffuseScale = g_flDiffuseCoefficient;

		// What the environment map is scaled by where a material model has its own (cook_torrance's and two_lobe_phong's tinted area terms); below 0 the SH radiance toward the reflection.
		float3 envArea = -1;

		// The specular coefficient the environment map is scaled by: per pixel where the material texture gives one (Standoff's sky river reflects in its channels only, not over its whole bed).
		float envSpecular = g_flSpecularCoefficient;

		if ( model == 1 )
		{
			// cook_torrance and its variants.
			float specularCoefficient = g_flSpecularCoefficient, albedoBlend = g_flAlbedoBlend, roughness = g_flRoughness;
			#if !HALO3_TERRAIN
			if ( g_flUseMaterialTexture > 0.5 )
			{
				float4 m = SAMPLE( MaterialTexture, s.Uv );
				specularCoefficient = m.x;
				envSpecular = specularCoefficient;
				albedoBlend = m.y;
				roughness = m.a;
				diffuseScale = 1 - specularCoefficient;
			}
			#endif
			envRoughness = roughness;
			float3 f0 = lerp( g_vFresnelColor, s.Albedo, albedoBlend );
			float3 area;
			float3 dominant = H3CookTorrance( s, l, f0, roughness, area );
			specular = ( area * g_flAreaSpecular + dominant * g_flAnalyticalSpecular ) * g_vSpecularTint * ( s.SpecularMask * specularCoefficient );
			// MCC's cook_torrance scales its environment map by the area specular times the specular tint, not the SH radiance toward the reflection: that made the far sea a warm grey.
			envArea = max( area * g_vSpecularTint, 0.001 );
			if ( g_flRimFresnelCoefficient > 0 )
				specular += g_vRimFresnelColor * g_flRimFresnelCoefficient * pow( 1 - nv, g_flRimFresnelPower ) * diffuse;
		}
		else if ( model == 2 )
		{
			// two_lobe_phong, as MCC's static_sh template: the lobe's power and tint go from the normal pair to the glancing pair by (1 - N.V)^steepness, the tint toward the albedo; the analytic lobe is (p + 1) / (2 pi) (R.L)^p times the dominant light, the area lobe the full SH's radiance toward R (H3PhongAreaRadiance), both tinted; the environment map is scaled by that tinted area term.
			float blend = pow( 1 - nv, g_flFresnelCurveSteepness );
			float power = lerp( g_flNormalSpecularPower, g_flGlancingSpecularPower, blend );
			envRoughness = max( 1.01 - 0.005 * power, 0.01 );
			float3 tint = lerp( g_vNormalSpecularTint, g_vGlancingSpecularTint, blend );
			tint = lerp( tint, s.Albedo, g_flAlbedoSpecularTintBlend );
			float3 r = 2 * dot( s.View, s.Normal ) * s.Normal - s.View;
			float rl = dot( r, l.SunDirection );
			float3 analytic = rl > 0 ? tint * pow( rl, max( power, 1e-3 ) ) * ( power + 1 ) * 0.159155 * l.SunIntensity * H3AntiShadow( l ) : 0;
			float3 area = tint * H3PhongAreaRadiance( l, r );
			specular = s.SpecularMask * g_flSpecularCoefficient * ( max( analytic, 0 ) * g_flAnalyticalSpecular + max( area * g_flAreaSpecular, 0 ) );
			envArea = max( area, 0.001 );
		}
		else if ( model == 3 )
		{
			// single_lobe_phong: the exponent from the roughness as MCC's simple lights (0.27291 r^-2.1973), normalised by it.
			float power = 0.27291 * pow( max( g_flRoughness, 0.02 ), -2.1973 );
			float3 area;
			float3 dominant = H3PhongLobe( s, l, power, area ) * power / ( 2 * HaloPi );
			specular = ( area * g_flAreaSpecular + dominant * g_flAnalyticalSpecular ) * g_vSpecularTint * ( s.SpecularMask * g_flSpecularCoefficient );
		}
		else if ( model == 4 )
		{
			// foliage: diffuse with light through the leaf (the back side's lambert, dimmer).
			diffuse += 0.281 * l.Intensity * saturate( -dot( l.Direction, s.Normal ) ) * 0.5;
		}
		else if ( model == 7 )
		{
			// organism: diffuse plus a rim.
			specular = g_vRimFresnelColor * g_flRimFresnelCoefficient * pow( 1 - nv, max( g_flRimFresnelPower, 1 ) ) * diffuse;
		}
		else if ( model == 6 )
		{
			// glass, as MCC's static_per_pixel glass template: the dominant light's lobe about the reflection, pi m^2 c^3 e^(-tan^2/m^2) for c = L.R and m the roughness, plus the area specular from the SH toward the reflection, times the specular coefficient and mask; the diffuse scaled by the albedo's alpha; the fresnel curve weights the environment and joins the opacity (MainPs).
			float3 r = 2 * dot( s.View, s.Normal ) * s.Normal - s.View;
			float c = saturate( dot( l.Direction, r ) );
			float m2 = g_flRoughness * g_flRoughness;
			float c2 = max( c * c, 1e-6 );
			float lobe = m2 > 0 ? HaloPi * m2 * c * c2 * exp( -( 1 - c2 ) / c2 / m2 ) : 0;
			float3 area = H3ShRadiance( l, r, g_flRoughness );
			specular = ( l.SunIntensity * lobe * g_flAnalyticalSpecular + area * g_flAreaSpecular ) * g_flSpecularCoefficient * s.SpecularMask;
			diffuseScale *= s.Alpha;
		}

		specular *= s.Prt;
		float3 color = diffuse * s.Albedo * diffuseScale + specular;
		g_vDebugSpecular = specular;
		g_flDebugRoughness = envRoughness;

		// Environment map, as MCC's per_pixel and dynamic options (lifted from static_per_pixel): the cube map's colour (x its alpha) times the static light's radiance toward the reflection, env tint, specular mask, specular coefficient and env contribution. The cube is a colour, the SH sets the brightness.
		// Dynamic: the BSP's baked cube map for the cluster (Halo3Materials.SetDynamicCubemap), rgb x alpha x 256, read at the level the roughness (x env_roughness_scale) or the reflection's screen footprint picks. Glass scales it by its fresnel, specular coefficient and mask against the SH's L0 instead. Missing cubes are a mid grey.
		if ( g_flEnvMode > 0.5 )
		{
			float3 r = 2 * dot( s.View, s.Normal ) * s.Normal - s.View;
			float3 cube = 0.5;
			float3 scale = ( envArea.x >= 0 ? envArea : H3ShRadiance( l, r, g_flRoughness ) ) * s.SpecularMask * envSpecular * g_flEnvContribution;
			if ( model == 6 )
				scale = max( l.C0 + 0.282095 * l.Intensity, 0.001 ) * H3GlassFresnel( nv ) * g_flSpecularCoefficient * s.SpecularMask;
			#if !HALO3_TERRAIN
				if ( g_flHasEnvMap > 0.5 || g_flHasObjectEnvMap > 0.5 )
				{
					if ( g_flEnvMode < 1.5 )
					{
						// MCC's per_pixel reads its cube with a plain sample: the hardware's level, so bumpy glass reflects blurred.
						float3 dir = float3( r.x, -r.y, r.z );
						cube = H3SampleEnv( dir, H3EnvImplicitLod( dir ) );
					}
					else
					{
						float footprint = max( length( ddx( r ) ), length( ddy( r ) ) );
						float lod = max( sqrt( footprint ) * 6 - 0.6, envRoughness * g_flEnvRoughnessScale * 4 );
						// MCC's shader templates read their cubes at (x, -y, z) (static_per_pixel, per_pixel and dynamic alike: sample r7.xwzx with w = -y; water doesn't); matched against the editor's own views from Valhalla's cube point 0 (0.75 correlation, the identity 0.16).
						float4 raw = H3SampleEnvRaw( float3( r.x, -r.y, r.z ), lod );
						cube = raw.rgb * raw.a * 256;
					}
				}
			#endif
			color += cube * scale * g_vEnvTint * s.Prt;
			g_vDebugEnv = cube * scale * g_vEnvTint * s.Prt;
		}
		return color * shadow;
	}

	// The self-illumination intensity and colour, pulsing when their overlays say so.
	float H3SelfIllumIntensity()
	{
		if ( g_vSelfIllumPulseWave.x <= 0 )
			return g_flSelfIllumIntensity;
		return clamp( g_vSelfIllumPulse.x + g_vSelfIllumPulse.y * H3Wave( g_vSelfIllumPulseWave ), g_vSelfIllumPulse.z, g_vSelfIllumPulse.w );
	}

	float3 H3SelfIllumColor()
	{
		int stops = (int)round( g_vSelfIllumColorCurve.z );
		if ( g_vSelfIllumColorWave.x <= 0 || stops <= 0 )
			return g_vSelfIllumColor;
		float f = saturate( g_vSelfIllumColorCurve.x + g_vSelfIllumColorCurve.y * H3Wave( g_vSelfIllumColorWave ) ) * ( stops - 1 );
		int segment = min( (int)f, max( stops - 2, 0 ) );
		float t = saturate( f - segment );
		float3 a = segment == 0 ? g_vSelfIllumColorStop0.rgb : segment == 1 ? g_vSelfIllumColorStop1.rgb : g_vSelfIllumColorStop2.rgb;
		float3 b = segment == 0 ? g_vSelfIllumColorStop1.rgb : segment == 1 ? g_vSelfIllumColorStop2.rgb : g_vSelfIllumColorStop3.rgb;
		return stops == 1 ? g_vSelfIllumColorStop0.rgb : lerp( a, b, t );
	}

	// ---- Self-illumination (self_illumination category), HDR before exposure.
	float3 H3SelfIllumination( float2 uv, float3 albedo, float2 viewTs = 0 )
	{
		int mode = (int)round( g_flSelfIllumMode );
		if ( mode == 0 )
			return 0;

		float intensity = H3SelfIllumIntensity() * H3SelfIllumExposure();
		float3 c = H3SelfIllumColor() * intensity;
		#if HALO3_TERRAIN
			float4 map = SAMPLE( SelfIllumMap, uv );
			float4 detail = SAMPLE( SelfIllumDetailMap, uv );
			return map.rgb * detail.rgb * DetailMultiplier * c;
		#else
			float4 map = SAMPLE( SelfIllumMap, uv );
			switch ( mode )
			{
				case 2: // 3_channel_self_illum
					return ( map.r * g_vChannelA.rgb + map.g * g_vChannelB.rgb + map.b * g_vChannelC.rgb ) * intensity;
				case 3: // plasma: the noise maps' red channels' difference makes three nested bands, each colour only where the thinner one isn't, as MCC's (Sandtrap's steam vents)
				{
					float a = SAMPLE( SelfIllumMap, uv ).r;
					float b = SAMPLE( SelfIllumDetailMap, uv ).r;
					float mask = SAMPLE( AlphaMaskMap, uv ).a;
					float x = saturate( 1 - abs( a - b ) );
					float medium = pow( x, g_vPlasmaThinness.x );
					float wide = pow( x, g_vPlasmaThinness.y );
					float sharp = pow( x, g_vPlasmaThinness.z );
					float3 p = g_vPlasmaColorMedium.rgb * g_vPlasmaColorMedium.a * ( medium - sharp )
						+ g_vPlasmaColorWide.rgb * g_vPlasmaColorWide.a * ( wide - medium )
						+ g_vPlasmaColorSharp.rgb * g_vPlasmaColorSharp.a * sharp;
					return p * mask * intensity;
				}
				case 4: // from_diffuse
					return albedo * c;
				case 5: // illum_detail
					return map.rgb * SAMPLE( SelfIllumDetailMap, uv ).rgb * DetailMultiplier * c;
				case 6: // meter: on below the meter value, off above
				{
					float4 m = map;
					return m.g > 0.5 ? ( m.r <= g_flMeterValue ? g_vMeterColorOn : g_vMeterColorOff ) : 0;
				}
				case 7: // self_illum_times_diffuse
					return map.rgb * albedo * c;
				case 8: // simple_with_alpha_mask
					return map.rgb * map.a * c;
				case 9: // illum_change_color: the primary change colour
					return map.rgb * H3ChangeColor( 0 ) * intensity;
				case 10: // multilayer_additive as MCC's halograms: 4 x layers_of_4 samples stepped away from the eye across the tangent plane, each depth_darken dimmer, averaged, then to the power layer_contrast (Orbital's ad screens)
				{
					float4 xf = g_vSelfIllumMapXform;
					float2 st = uv * xf.xy + H3Scrolled( xf, g_vSelfIllumMapScroll );
					float n = 4 * max( g_flLayersOf4, 0.25 );
					float2 delta = viewTs * xf.xy * float2( g_flTexcoordAspectRatio, 1 ) * g_flLayerDepth / n;
					float2 gx = ddx( st ), gy = ddy( st );
					float3 sum = 0;
					float w = 1;
					int count = min( (int)ceil( n ), 64 );
					for ( int k = 0; k < count; k++ )
					{
						float3 t = g_tSelfIllumMap.SampleGrad( g_sWrap, st, gx, gy ).rgb;
						sum += w * ( g_flSelfIllumMapSrgb > 0.5 ? H3SrgbToLinear( t ) : t );
						st -= delta;
						w *= g_flDepthDarken;
					}
					return pow( max( sum / n, 0 ), g_flLayerContrast ) * c;
				}
				default: // simple and the other halogram layers (scope_blur, holograms) for now
					return map.rgb * c;
			}
		#endif
	}

	// ---- Albedo (albedo category). Returns linear albedo, alpha = the specular mask from diffuse / opacity.
	#if !HALO3_TERRAIN
	float4 H3Albedo( float2 uv, float3 viewWs, float3 normalWs )
	{
		int mode = (int)round( g_flAlbedoMode );
		if ( mode == 2 )
			return g_vAlbedoColor;

		float4 base = SAMPLE( BaseMap, uv );
		float4 detail = SAMPLE( DetailMap, uv );
		float4 a;
		switch ( mode )
		{
			case 1: // detail_blend
			{
				float4 blended = lerp( detail, SAMPLE( DetailMap2, uv ), base.a );
				a = float4( base.rgb * blended.rgb * DetailMultiplier, blended.a );
				break;
			}
			case 3: // two_change_color
			case 4: // four_change_color
			{
				a = base * detail * float4( DetailMultiplier.xxx, 1 ) * g_vAlbedoColor;
				float4 m = SAMPLE( ChangeColorMap, uv );
				float3 cc = lerp( 1, H3ChangeColor( 0 ), m.x ) * lerp( 1, H3ChangeColor( 1 ), m.y );
				if ( mode == 4 )
					cc *= lerp( 1, H3ChangeColor( 2 ), m.z ) * lerp( 1, H3ChangeColor( 3 ), m.w );
				a.rgb *= cc;
				break;
			}
			case 5: // three_detail_blend
			{
				float b1 = saturate( 2 * base.a ), b2 = saturate( 2 * base.a - 1 );
				float4 first = lerp( detail, SAMPLE( DetailMap2, uv ), b1 );
				float4 blended = lerp( first, SAMPLE( DetailMap3, uv ), b2 );
				a = float4( base.rgb * blended.rgb * DetailMultiplier, blended.a );
				break;
			}
			case 6: // two_detail_overlay
			{
				float4 blended = lerp( detail, SAMPLE( DetailMap2, uv ), base.a );
				float4 overlay = SAMPLE( DetailMapOverlay, uv );
				a = float4( base.rgb * blended.rgb * overlay.rgb * DetailMultiplier * DetailMultiplier, blended.a * overlay.a );
				break;
			}
			case 7: // two_detail
			{
				float4 d2 = SAMPLE( DetailMap2, uv );
				a = float4( base.rgb * detail.rgb * d2.rgb * DetailMultiplier * DetailMultiplier, base.a * detail.a * d2.a );
				break;
			}
			case 8: // color_mask: up to three colours by the mask's channels, neutral grey elsewhere
			{
				a = base * detail * float4( DetailMultiplier.xxx, 1 );
				float4 m = SAMPLE( ChangeColorMap, uv );
				float rest = saturate( 1 - m.r - m.g - m.b );
				a.rgb *= ( g_vAlbedoColor.rgb * m.r + g_vAlbedoColor2.rgb * m.g + g_vAlbedoColor3.rgb * m.b + g_vNeutralGray.rgb * rest ) / max( g_vNeutralGray.rgb, 0.01 );
				break;
			}
			case 9: // two_detail_black_point: the base alpha is the black point for the details' alpha (MCC's PS: ramps to (base.a + 1) / 2 at the midpoint, then on to 1)
			{
				float4 d2 = SAMPLE( DetailMap2, uv );
				float d = detail.a * d2.a;
				float mid = ( base.a + 1 ) * 0.5;
				float alpha = mid * saturate( ( d - base.a ) / ( mid - base.a ) ) + saturate( d - mid );
				a = float4( base.rgb * detail.rgb * d2.rgb * DetailMultiplier * DetailMultiplier, alpha );
				break;
			}
			case 10: // two_color: albedo colour and second colour by the blend map
			{
				float t = SAMPLE( ChangeColorMap, uv ).r;
				a = base * lerp( g_vAlbedoColor, g_vAlbedoColor2, t );
				break;
			}
			case 11: // chameleon: colours by view angle (first two of the four)
			{
				float f = pow( saturate( dot( viewWs, normalWs ) ), max( g_vAlbedoColor3.w, 0.1 ) );
				a = base * detail * float4( DetailMultiplier.xxx, 1 ) * float4( lerp( g_vAlbedoColor2.rgb, g_vAlbedoColor.rgb, f ), 1 );
				break;
			}
			default: // default, custom_cube, texture_from_misc
				a = base * detail * float4( DetailMultiplier.xxx, 1 ) * g_vAlbedoColor;
				break;
		}
		return a;
	}
	#endif

	// The exposure in stops: the scene's override, the scene's own (Halo3SceneLook), else the material's map's.
	float H3Stops()
	{
		if ( g_vExposureOverride.y > 0.5 )
			return g_vExposureOverride.x;
		if ( g_flHdr > 0.5 && g_flExposureState >= 0 )
			return Bindless::GetTexture2D( (int)round( g_flExposureState ) ).Load( int3( 0, 0, 0 ) ).x;
		return g_flSceneLook > 0.5 ? g_flSceneExposureStops : g_flExposureStops;
	}

	// What self-illumination is scaled by relative to the scene's exposure (g_vSelfIllumExposure).
	float H3SelfIllumExposure()
	{
		float stops = H3Stops();
		return exp2( g_vSelfIllumExposure.x + g_vSelfIllumExposure.y * ( stops - g_vSelfIllumExposure.x ) - stops );
	}

	// ---- Exposure and tone curve: MCC writes x = colour * g_exposure.x, g_exposure.x = 2^stops / 1.4938 (the tone curve's white point), into its LDR target (as min( x, 1 )) and HDR target (as x / 128), both sRGB targets blending linear x; its final composite shows curve( min( x, 1 ) + bloom ) * 15/16 as linear light.
	// While Halo 3's post-processing runs (Halo3Hdr, Halo3PostFx) the frame gets x and the composite does the rest; otherwise the curve is applied here, without bloom.
	float3 H3FinishColor( float3 c )
	{
		float3 x = max( c * exp2( H3Stops() ) / H3WhitePoint, 0 );
		// Halo3ShadowDebug 13: what the exposure sees, red = Halo3Hdr, green = ( stops + 8 ) / 16.
		if ( g_flShadowDebug > 12.5 && g_flShadowDebug < 13.5 )
			return float3( saturate( g_flHdr ), saturate( ( H3Stops() + 8 ) / 16 ), 0 );
		if ( g_flHdr > 0.5 )
			return min( x, 128 );
		x = min( saturate( x ), g_vToneCurve.x );
		// MCC's identity colour grading LUT holds trunc( 255 i / 16 ) but is read as if it held i / 15: white lands at 15/16.
		return x * ( g_vToneCurve.y + x * ( g_vToneCurve.z + x * g_vToneCurve.w ) ) * ( 15.0 / 16.0 );
	}

	// The editor's view modes (ToolsVis) and wireframe: the surface as s&box's material, Halo's static lighting (exposed and tone mapped, as the lit colour is) standing in for s&box's direct diffuse and specular; the UV views over the base map.
	float4 H3ToolsVis( PixelInput i, H3Surface s, float3 tangentNormal, float3 diffuse, float3 specular, float roughness, float opacity, Texture2D uvMap )
	{
		if ( g_bWireframeMode )
			return g_vWireframeColor;

		Material m = Material::Init( i.vPositionWithOffsetWs.xyz, i.vPositionSs );
		m.Albedo = s.Albedo;
		m.Normal = s.Normal;
		m.TangentNormal = tangentNormal;
		m.GeometricNormal = s.VertexNormal;
		m.WorldTangentU = normalize( i.vTangentUWs );
		m.WorldTangentV = normalize( i.vTangentVWs );
		m.Roughness = saturate( roughness );
		m.Opacity = opacity;
		m.TextureCoords = s.Uv;

		// DoToolsVis shows half the diffuse.
		LightingTerms_t terms = InitLightingTerms();
		terms.vDiffuse = 2 * H3FinishColor( diffuse );
		terms.vSpecular = H3FinishColor( specular );
		float4 color = float4( 0, 0, 0, opacity );
		color = ShadingModelStandard::DoToolsVis( color, m, terms );

		// The UV and mip views read a texture DoToolsVis only knows as g_tColor.
		float4 scratch = 0;
		ToolsVis uvVis = ToolsVis::Init( scratch, (float3)0, (float3)0, (float3)0, (float3)0, (float3)0 );
		uvVis.ShowUVs( color, m.Albedo, uvMap, s.Uv );
		uvVis.ShowMipUtilization( color, m.Albedo, uvMap, s.Uv );
		color.rgb = saturate( color.rgb );
		return color;
	}

	// Optical depth along the view through an exponential atmosphere of height scale H above the sea level (heights clamped at 0), as AtmosphereVS.
	float H3OpticalDepth( float dist, float hc, float hp, float h )
	{
		h = max( h, 1e-3 );
		if ( abs( hp - hc ) < 1e-3 )
			return dist * exp( -hc / h );
		return dist * h * ( exp( -hc / h ) - exp( -hp / h ) ) / ( hp - hc );
	}

	// Rayleigh + Mie single scattering between the camera and a point (HDR, before exposure): returns the inscattered light, the extinction through out.
	float3 H3Atmosphere( float3 positionWs, out float3 extinction )
	{
		extinction = 1;
		bool scene = g_flSceneLook > 0.5 && g_flSkyFog < 0.5;
		float4 fogSun = scene ? g_vSceneFogSun : g_vFogSun;
		float4 fogSunColor = scene ? g_vSceneFogSunColor : g_vFogSunColor;
		float4 fogMie = scene ? g_vSceneFogMie : g_vFogMie;
		float4 fogRayleigh = scene ? g_vSceneFogRayleigh : g_vFogRayleigh;
		float4 fogRayleighTheta = scene ? g_vSceneFogRayleighTheta : g_vFogRayleighTheta;
		float4 fogMieTheta = scene ? g_vSceneFogMieTheta : g_vFogMieTheta;
		float2 fogScale = scene ? g_vSceneFogScale : g_vFogScale;
		if ( fogScale.y < 0.5 || fogSunColor.w <= 0 || g_flNoFog > 0.5 )
			return 0;

		float3 p = positionWs * fogScale.x;
		float3 camera = g_vCameraPositionWs * fogScale.x;
		float3 toPoint = p - camera;
		float len = length( toPoint );
		float dist = clamp( len + fogSun.w, 0, fogSunColor.w );
		float hc = max( camera.z - fogRayleigh.w, 0 ), hp = max( p.z - fogRayleigh.w, 0 );
		float depthR = H3OpticalDepth( dist, hc, hp, fogRayleighTheta.w );
		float depthM = H3OpticalDepth( dist, hc, hp, fogMieTheta.w );
		extinction = exp( -( fogRayleigh.rgb * depthR + fogMie.rgb * depthM ) );

		// Phases toward the sun as MCC's AtmosphereVS: Rayleigh (1 + cos^2), Mie (1 + g - 2 g cos)^-1.5 (its constants: 1 + g, not 1 + g^2; the (1 - g^2) is in MieTheta); the theta prefixes over the total beta give the scattered fraction.
		float cosTheta = len > 1e-4 ? dot( toPoint / len, fogSun.xyz ) : 0;
		float g = fogMie.w;
		float mie = pow( max( 1 + g - 2 * g * cosTheta, 1e-3 ), -1.5 );
		float3 beta = max( fogRayleigh.rgb + fogMie.rgb, 1e-6 );
		float3 scatter = ( fogRayleighTheta.rgb * ( 1 + cosTheta * cosTheta ) + fogMieTheta.rgb * mie ) / beta;
		return scatter * fogSunColor.rgb * ( 1 - extinction );
	}

	#if HALO3_TERRAIN
	// One terrain material's albedo (base x detail) and bump (plus its detail bump), weighted.
	void H3TerrainLayer( inout float4 albedo, inout float3 nt, float w, float4 base, float4 detail, float4 bump, float4 detailBump, float2 unsignedXy )
	{
		float3 n = H3BumpNormal( bump, unsignedXy.x );
		n.xy += H3BumpNormal( detailBump, unsignedXy.y ).xy;
		albedo += w * float4( base.rgb * detail.rgb * DetailMultiplier, base.a * detail.a );
		nt += w * normalize( n );
	}
	#endif

	#if HALO3_WATER
	// One slope (unsigned xy) of the wave slope array, blended between two of its layers by an animation phase 0-1.
	// MCC samples the wave slopes trilinear, not anisotropic: seen at a grazing angle the waves blur smooth.
	float2 H3WaveSlope( Texture2DArray waves, float2 uv, float phase )
	{
		float layers = max( g_vWaterWave.z, 1 );
		float f = frac( phase ) * layers;
		float l0 = floor( f );
		float l1 = l0 + 1 >= layers ? 0 : l0 + 1;
		float2 a = waves.Sample( g_sTrilinearWrap, float3( uv, l0 ) ).xy * 2 - 1;
		float2 b = waves.Sample( g_sTrilinearWrap, float3( uv, l1 ) ).xy * 2 - 1;
		return lerp( a, b, f - l0 );
	}

	// Water (rmw) as MCC's water pixel shader (water_ps.txt): wave slopes for the normal, the static cube reflection by fresnel, a body of water colour lit by the lightmap through which the scene behind shows (bent by the waves, dimmed by the depth seen through it), faded at the banks, foam on top. It blends over the frame (premultiplied); dynamic refraction adds the waves' bend of it from a copy of the frame.
	float4 H3Water( PixelInput i, float2 uv, float3 tangentWs, float3 binormalWs, float3 vertexNormal, float3 viewWs, float3 fogExtinction, float3 fogInscatter )
	{
		float3 positionWs = i.vPositionWithOffsetWs.xyz + g_vHighPrecisionLightingOffsetWs.xyz;
		float distance = length( g_vCameraPositionWs - positionWs ) / g_flWorldScale;
		float damping = max( distance / max( g_flWaterDamping, 1e-3 ), 1 );

		// The water's per-vertex parameters (Sh3: depth, global shape texture coordinate, displacement scale), unless Sh3 holds per-vertex light.
		bool hasParameters = i.vSh3.w > 0 && (int)round( g_flLightingMode ) != 2;
		float2 globalUv = i.vSh3.yz;

		// MCC's water PS: three reads of the slope array, each texel (-1..1) times slope_range less half of it: the main waves at the displacement array's transform (time_warp), the slope array's own (time_warp_aux), and detail at the displacement transform times detail_slope_scale (time_warp times detail_slope_scale_z); weighted by sqrt(global shape x wave_height), sqrt(global shape x wave_height_aux) (both 0 unless waveshape is default) and detail_slope_steepness. The normal's slope is that over max(distance / damping, 1); the refraction's isn't damped and weighs the main waves by at least minimal_wave_disturbance.
		float2 slope = 0;
		float2 disturbance = 0;
		if ( g_flWaveSlopeArray >= 0 )
		{
			Texture2DArray waves = Bindless::GetTexture2DArray( (int)round( g_flWaveSlopeArray ) );
			float warp = frac( g_flTime / max( g_vWaterWave.w, 0.01 ) );
			float shape = hasParameters ? H3GlobalShape( i.vSh3, damping ).x : 1;
			float mainWeight = g_vWaterHeights.w * sqrt( shape * g_vWaterHeights.x );
			float auxWeight = g_vWaterHeights.w * sqrt( shape * g_vWaterHeights.y );

			float2 mainUv = uv * g_vWaveDisplacementXform.xy + g_vWaveDisplacementXform.zw;
			float2 auxUv = uv * g_vWaveSlopeXform.xy + H3Scrolled( g_vWaveSlopeXform, g_vWaveSlopeScroll );
			float2 detailUv = uv * g_vWaveDisplacementXform.xy * g_vWaterDetail.xy + g_vWaveDisplacementXform.zw;
			float2 range = g_vWaterWave.xy;
			float2 main = H3WaveSlope( waves, mainUv, warp ) * range - range * 0.5;
			float2 aux = H3WaveSlope( waves, auxUv, g_flTime / max( g_vWaterDetail.w, 0.01 ) ) * range - range * 0.5;
			float2 detail = H3WaveSlope( waves, detailUv, warp * g_vWaterHeights.z ) * range - range * 0.5;
			float2 rest = aux * auxWeight + detail * g_vWaterDetail.z;
			slope = ( main * mainWeight + rest ) / damping;
			disturbance = main * max( g_vWaterRefract.y, mainWeight ) + rest;
		}
		else
		{
			float3 b = H3BumpNormal( SAMPLE( BumpMap, uv ) );
			slope = float2( b.x, -b.y ) / max( b.z, 0.1 );
			disturbance = slope;
		}

		// MCC's water: the normal is slope.x along the tangent, -slope.y along the binormal (its vertex shader negates the binormal), plus the vertex normal.
		float3 nt = normalize( float3( slope.x, -slope.y, 1 ) );
		float3 n = normalize( nt.x * tangentWs + nt.y * binormalWs + nt.z * vertexNormal );
		// Under water MCC's fresnel faces the normal down, toward the camera; the reflection is the same either way.
		bool underwater = g_vCameraUnderwater.x > 0.5;
		float nv = saturate( dot( underwater ? -n : n, viewWs ) );
		float3 r = 2 * dot( viewWs, n ) * n - viewWs;

		// MCC's water reflects into the upper half only (|z|): steep waves seen at a grazing angle would otherwise reflect the cube's dark lower half.
		r.z = abs( r.z );

		// The cube's level as its sampler picks it (MCC's cube is a real cube map with mips): from how fast the reflection turns across the pixel, a face spanning about 2 in direction.
		float envWidth, envHeight;
		g_tEnvMap.GetDimensions( envWidth, envHeight );
		float envLod = max( log2( max( length( ddx( r ) ), length( ddy( r ) ) ) * envHeight * 0.5 ), 0 );

		Halo3Light light = H3StaticLight( i );

		// MCC's water lights itself by the lightmap's first SH pair (its constant term) clamped to 0..1, white without a lightmap.
		float3 lightmap = 1;
		if ( (int)round( g_flLightingMode ) == 1 && g_flLightprobeArray >= 0 )
			lightmap = saturate( H3DecodePair( Bindless::GetTexture2DArray( (int)round( g_flLightprobeArray ) ), i.vTextureCoords.zw, 0, g_vLightmapScales.x ) );
		else if ( (int)round( g_flLightingMode ) == 2 )
			lightmap = saturate( i.vSh0.xyz );

		// MCC's water: fresnel f0 + (1 - f0) (1 - n.v)^2.5; the cube's colour times min(sunspot_cut, alpha) plus, where its alpha passes the cut (the sun), how far the light is past shadow_intensity_mark, all times reflection_coefficient.
		float fresnel = g_vWaterFresnel.x + ( 1 - g_vWaterFresnel.x ) * pow( 1 - nv, 2.5 );
		float3 reflection;
		if ( g_flHasEnvMap > 0.5 )
		{
			float4 env = H3SampleEnvRaw( r, envLod );
			float3 lit = saturate( lightmap - g_vWaterReflection.y );
			reflection = env.rgb * ( min( g_vWaterReflection.x, env.a ) + saturate( env.a - g_vWaterReflection.x ) * dot( lit, lit ) ) * g_vWaterFresnel.y;
		}
		else
			reflection = H3ShIrradiance( light, r ) * g_vWaterFresnel.y;

		// The water colour (MCC reads watercolor_texture at the global shape texture coordinate, clamped: Valhalla's lake runs past its edge) times watercolor_coefficient, lit; water_diffuse by how much the normal faces up, lit.
		float3 waterColor = g_vWaterColor.rgb;
		if ( g_flWaterColorTexture > 0.5 )
		{
			waterColor = g_tBaseMap.Sample( g_sTrilinearClamp, hasParameters ? globalUv : uv ).rgb;
			if ( g_flBaseMapSrgb > 0.5 )
				waterColor = H3SrgbToLinear( waterColor );
		}
		float3 body = waterColor * g_vWaterDiffuse.w * lightmap;
		float3 diffuse = g_vWaterDiffuse.rgb * saturate( n.z ) * lightmap;

		if ( g_bWireframeMode || ToolsVis::WantsToolsVis() )
		{
			H3Surface s;
			s.Albedo = waterColor;
			s.Alpha = 1;
			s.SpecularMask = 1;
			s.Normal = n;
			s.VertexNormal = vertexNormal;
			s.View = viewWs;
			s.Uv = uv;
			s.SunVisibility = 1;
			s.Prt = 1;
			return H3ToolsVis( i, s, nt, lightmap, reflection * fresnel, 0.05, 1, g_tBaseMap );
		}

		// What shows through (MCC's refraction; under water it takes the depth behind as infinitely far, so only the banks show the frame): the frame's pixel moved by the undamped waves (the view direction's y, x times their slope times refraction_texcoord_shift), less where the bed lies within a third of a unit of the surface or the eye is past 2 units, and only onto what lies behind the water.
		// The water blends over the frame and adds how the frame copy changes from this pixel to the moved one: s&box's copy swaps a blurred copy into whatever its quarter-res D16 mask puts in front of the water (far shallow beds), and there both reads are blurred alike, while far away MCC's move is nothing.
		float2 pixel = i.vPositionSs.xy;
		float2 screenUv = pixel * g_vInvViewportSize;
		float3 behind = Depth::GetWorldPosition( pixel );
		float depth = underwater ? 0 : Depth::GetNormalized( pixel );
		float3 bend = 0;
		if ( g_vWaterRefract.z > 0.5 )
		{
			float bed = depth <= 1e-5 ? 1e4 : length( behind - positionWs ) / g_flWorldScale;
			float2 shift = viewWs.yx * disturbance * g_vWaterRefract.x * saturate( 3 * bed ) * saturate( 2 / max( distance, 1e-3 ) );
			float2 bentUv = clamp( screenUv + shift, 0.001, 0.999 );
			float2 bentPixel = bentUv * g_vViewportSize;
			float bentDepth = underwater ? 0 : Depth::GetNormalized( bentPixel );
			float3 bentBehind = Depth::GetWorldPosition( bentPixel );
			if ( bentDepth <= 1e-5 || dot( bentBehind - g_vCameraPositionWs, g_vCameraDirWs ) > dot( positionWs - g_vCameraPositionWs, g_vCameraDirWs ) )
			{
				float2 copyScale = g_vFrameBufferCopyInvSizeAndUvScale.zw;
				bend = g_tFrameBufferCopyTexture.SampleLevel( g_sBilinearClamp, bentUv * copyScale, 0 ).rgb - g_tFrameBufferCopyTexture.SampleLevel( g_sBilinearClamp, screenUv * copyScale, 0 ).rgb;
				behind = bentBehind;
				depth = bentDepth;
			}
		}

		// MCC's water: what shows through is exp(-murkiness x the depth seen through), that depth mostly the vertical one (refraction_depth_dominant_ratio), all gone by refraction_extinct_distance from the eye.
		float through = depth <= 1e-5 ? 1e4 : length( behind - positionWs ) / g_flWorldScale;
		float vertical = depth <= 1e-5 ? 1e4 : abs( positionWs.z - behind.z ) / g_flWorldScale;
		float seen = saturate( exp( -g_vWaterColor.w * lerp( through, vertical, g_vWaterRefraction.x ) ) );
		seen *= saturate( 1 - distance / max( g_vWaterRefraction.y, 1e-3 ) );

		// The banks: painted in watercolor_texture's alpha (paint), by the water's own depth per vertex as MCC (bankalpha_infuence_depth), else by how far the bed behind lies below the surface.
		float bank = 1;
		if ( g_flWaterBankPaint > 0.5 )
			bank = saturate( g_tBaseMap.Sample( g_sTrilinearClamp, hasParameters ? globalUv : uv ).a );
		else if ( g_vWaterFresnel.z > 0 && hasParameters )
			bank = saturate( i.vSh3.x / g_vWaterFresnel.z );
		else if ( g_vWaterFresnel.z > 0 && depth > 1e-5 )
			bank = saturate( ( positionWs.z - behind.z ) / ( g_vWaterFresnel.z * g_flWorldScale ) );

		// Foam: how far the wave has risen past foam_height toward the highest it can, to the power foam_pow (auto), and painted in the global shape texture's blue (paint); foam_texture x foam_texture_detail, lit, their alphas the cover.
		float foam = 0;
		float3 foamColor = 0;
		int foamMode = (int)round( g_vWaterFoam.z );
		if ( foamMode & 1 )
			foam = pow( saturate( ( i.vWaterFoam.x - g_vWaterFoam.x ) / max( saturate( i.vWaterFoam.y - g_vWaterFoam.x ), 1e-6 ) ), g_vWaterFoam.y );
		if ( ( foamMode & 2 ) && hasParameters && g_flGlobalShapeTexture >= 0 )
		{
			float painted = Bindless::GetTexture2D( (int)round( g_flGlobalShapeTexture ) ).Sample( g_sTrilinearClamp, globalUv ).b;
			foam = max( foam, g_flGlobalShapeSrgb > 0.5 ? H3SrgbToLinear( painted.xxx ).x : painted );
		}
		if ( foam > 0.002 )
		{
			float4 a = g_tDetailMap.Sample( g_sTrilinearWrap, uv * g_vDetailMapXform.xy + H3Scrolled( g_vDetailMapXform, g_vDetailMapScroll ) );
			float4 b = g_tDetailMap2.Sample( g_sTrilinearWrap, uv * g_vDetailMap2Xform.xy + H3Scrolled( g_vDetailMap2Xform, g_vDetailMap2Scroll ) );
			if ( g_flDetailMapSrgb > 0.5 ) a.rgb = H3SrgbToLinear( a.rgb );
			if ( g_flDetailMap2Srgb > 0.5 ) b.rgb = H3SrgbToLinear( b.rgb );
			foamColor = a.rgb * b.rgb * lightmap;
			foam *= a.a * b.a;
		}

		// As MCC composes it (the scene left out, it's the frame behind): the body over what shows through, the reflection by fresnel, plus the diffuse, the scene again toward the banks, the foam on top; shown is how much of the scene is left in it. Fog falls on the rest. Premultiplied, plus the refraction's bend of what shows.
		float3 color = body * ( 1 - seen );
		float shown = seen;
		color = lerp( color, reflection, fresnel );
		shown *= 1 - fresnel;
		color += diffuse;
		color *= bank;
		shown = shown * bank + 1 - bank;
		color = lerp( color, foamColor, foam );
		shown *= 1 - foam;
		color = color * fogExtinction + fogInscatter * ( 1 - shown );

		float alpha = saturate( 1 - shown );
		float3 premultiplied = H3FinishColor( color / max( alpha, 1e-3 ) ) * alpha + shown * bend;

		// Under water MCC fogs the surface as its underwater pass fogged the frame: fog + 0.5 e^(-murkiness x distance) (colour - fog), here on the blend (the frame's part keeps its share).
		if ( underwater && g_vCameraUnderwater.y > 0.5 )
		{
			float k = 0.5 * saturate( exp( -g_vCameraUnderwater.z * distance ) );
			return float4( g_vUnderwaterFogColor * ( 1 - k ) + premultiplied * k, 1 - k * shown );
		}
		return float4( premultiplied, alpha );
	}
	#endif

	float4 MainPs( PixelInput i ) : SV_Target0
	{
		#if HALO3_WATER && S_MODE_DEPTH
			discard;
		#endif

		// MCC culls the water's back faces, and its front faces with the camera under water.
		#if HALO3_WATER
			if ( i.face == ( g_vCameraUnderwater.x > 0.5 ) )
				discard;
		#endif

		float3 vertexNormal = normalize( i.vNormalWs );
		float3 tangentWs = normalize( i.vTangentUWs );
		float3 binormalWs = normalize( i.vTangentVWs );
		float3 viewWs = normalize( g_vCameraPositionWs - ( i.vPositionWithOffsetWs.xyz + g_vHighPrecisionLightingOffsetWs.xyz ) );
		float2 uv = i.vTextureCoords.xy;

		H3Surface s;
		s.VertexNormal = vertexNormal;
		s.View = viewWs;
		s.SunVisibility = 1;
		s.Prt = 1;

		float3 fogExtinction;
		float3 fogInscatter = H3Atmosphere( i.vPositionWithOffsetWs.xyz + g_vHighPrecisionLightingOffsetWs.xyz, fogExtinction );

		#if HALO3_TERRAIN
			// Up to four materials by the blend map's channels (morph); off materials take no weight.
			float4 w = SAMPLE( BlendMap, uv ) * ( g_vTerrainMaterials > 0.5 );
			w /= max( dot( w, 1 ), 1e-4 );
			float4 albedo = 0;
			float3 nt = 0;
			H3TerrainLayer( albedo, nt, w.x, SAMPLE( BaseMap0, uv ), SAMPLE( DetailMap0, uv ), SAMPLE( BumpMap0, uv ), SAMPLE( DetailBump0, uv ), float2( g_flBumpMap0Unsigned, g_flDetailBump0Unsigned ) );
			H3TerrainLayer( albedo, nt, w.y, SAMPLE( BaseMap1, uv ), SAMPLE( DetailMap1, uv ), SAMPLE( BumpMap1, uv ), SAMPLE( DetailBump1, uv ), float2( g_flBumpMap1Unsigned, g_flDetailBump1Unsigned ) );
			H3TerrainLayer( albedo, nt, w.z, SAMPLE( BaseMap2, uv ), SAMPLE( DetailMap2, uv ), SAMPLE( BumpMap2, uv ), SAMPLE( DetailBump2, uv ), float2( g_flBumpMap2Unsigned, g_flDetailBump2Unsigned ) );
			H3TerrainLayer( albedo, nt, w.w, SAMPLE( BaseMap3, uv ), SAMPLE( DetailMap3, uv ), SAMPLE( BumpMap3, uv ), SAMPLE( DetailBump3, uv ), float2( g_flBumpMap3Unsigned, g_flDetailBump3Unsigned ) );
			albedo.rgb *= g_vGlobalAlbedoTint * H3DecalAlbedo( i.vDecalTexCoords, i.vDecalTexCoord2 );
			nt = normalize( nt );
			s.Albedo = albedo.rgb;
			s.Alpha = 1;
			s.SpecularMask = albedo.a;
			s.Normal = normalize( nt.x * tangentWs + nt.y * binormalWs + nt.z * vertexNormal );
			s.Uv = uv;

			Halo3Light light = H3StaticLight( i );
			s.SunVisibility = H3SunVisibility( i.vPositionWithOffsetWs.xyz + g_vHighPrecisionLightingOffsetWs.xyz, vertexNormal, light, true );
			float3 diffuse = H3DiffuseLight( light, s.Normal ) * dot( w, g_vTerrainDiffuse );
			float shadow = H3SunShadowFactor( light, s.Normal, s.SunVisibility );
			// Specular as MCC's terrain static_per_pixel: the specular materials' power, steepness and tint averaged by weight; a normalised phong lobe for the dominant light, the full SH toward the reflection as the area term and as the environment map's brightness, all tinted from the specular tint toward white by (1 - N.V)^steepness.
			float4 ws = w * ( g_vTerrainMaterials > 1.5 );
			float wsum = max( dot( ws, 1 ), 0 );
			float4 sw = ws * g_vTerrainSpecular;
			float power = ( dot( ws, g_vTerrainPower ) + 0.001 ) / max( wsum + 0.001, 0.001 );
			float steepness = ( dot( ws, g_vTerrainFresnel ) + 0.005 ) / max( wsum + 0.001, 0.001 );
			float3 tint = ws.x * g_vTerrainTint0 * ( 1 - g_vTerrainTintBlend.x ) + ws.y * g_vTerrainTint1 * ( 1 - g_vTerrainTintBlend.y ) + ws.z * g_vTerrainTint2 * ( 1 - g_vTerrainTintBlend.z ) + ws.w * g_vTerrainTint3 * ( 1 - g_vTerrainTintBlend.w );
			tint += dot( ws, g_vTerrainTintBlend ) * ( s.Albedo - tint );
			float nv = dot( s.View, s.Normal );
			tint = lerp( tint, 1, pow( 1 - saturate( nv ), steepness ) );

			float3 r = 2 * nv * s.Normal - s.View;
			float3 shC0 = light.C0 + 0.282095 * light.Intensity;
			float3 shift = 0.488602 * light.Direction;
			float3 shL1 = float3( dot( r, light.L1r - shift * light.Intensity.r ), dot( r, light.L1g - shift * light.Intensity.g ), dot( r, light.L1b - shift * light.Intensity.b ) );
			float3 area = max( 0.282095 * shC0 - 0.325735 * shL1, 0 );
			float nl = dot( s.Normal, light.SunDirection );
			float3 dominant = nv > 0 && nl > 0 ? light.SunIntensity * pow( saturate( dot( r, light.SunDirection ) ), power ) * ( power + 1 ) * 0.159155 : 0;

			// Environment map (dynamic: the cluster's baked cube as rgb x a x 256 at the level the roughness or the reflection's footprint picks; per_pixel: the shader's own cube) times the area radiance, env tint and the materials' environment contribution.
			float3 env = 0;
			if ( g_flEnvMode > 0.5 && ( g_flHasEnvMap > 0.5 || g_flHasObjectEnvMap > 0.5 ) )
			{
				float3 dir = float3( r.x, -r.y, r.z );
				float3 cube;
				if ( g_flEnvMode < 1.5 )
					cube = H3SampleEnv( dir, H3EnvImplicitLod( dir ) );
				else
				{
					float footprint = max( length( ddx( r ) ), length( ddy( r ) ) );
					float lod = max( sqrt( footprint ) * 6 - 0.6, max( 1.01 - 0.005 * power, 0.01 ) * g_flEnvRoughnessScale * 4 );
					float4 raw = H3SampleEnvRaw( dir, lod );
					cube = raw.rgb * raw.a * 256;
				}
				env = area * cube * g_vEnvTint * dot( sw, g_vTerrainEnv );
			}
			float3 specular = ( dominant * dot( sw, g_vTerrainAnalytical ) + area * dot( sw, g_vTerrainArea ) + env ) * tint * s.SpecularMask;
			g_vDebugSpecular = specular;
			g_vDebugEnv = env * tint * s.SpecularMask;
			float3 color = ( diffuse * s.Albedo + specular ) * shadow;
			if ( g_vTerrainMaterials.y > 2.5 )
				color += w.y * H3SelfIllumination( uv, s.Albedo );

			// The view modes: the roughness from the materials' phong powers as two_lobe_phong's.
			if ( g_bWireframeMode || ToolsVis::WantsToolsVis() )
				return H3ToolsVis( i, s, nt, diffuse * shadow, specular * shadow, dot( w, max( 1.01 - 0.005 * g_vTerrainPower, 0.01 ) ), 1, g_tBaseMap0 );
			return float4( H3FinishColor( color * fogExtinction + fogInscatter ), 1 );
		#else
			// Parallax (simple): the uv moved along the view by the height map.
			if ( g_flParallaxMode > 0.5 )
			{
				float3 viewTs = float3( dot( viewWs, tangentWs ), dot( viewWs, binormalWs ), dot( viewWs, vertexNormal ) );
				float h = ( SAMPLE( HeightMap, uv ).g - 0.5 ) * g_flHeightScale;
				uv += viewTs.xy * h;
			}

			#if HALO3_WATER
				return H3Water( i, uv, tangentWs, binormalWs, vertexNormal, viewWs, fogExtinction, fogInscatter );
			#endif

			// Decorators (g_flAlphaTest 2) test their alpha times the distance fade against 0.3, as MCC's decorator pixel shader, with or without a fade (Zanzibar's kelp has none); everything else against 0.5.
			if ( g_flAlphaTest > 0.5 )
			{
				if ( g_vDecoratorFade.y > g_vDecoratorFade.x )
					clip( SAMPLE( AlphaTestMap, uv ).a * i.vSh3.w - 0.3 );
				else
					clip( SAMPLE( AlphaTestMap, uv ).a - ( g_flAlphaTest > 1.5 ? 0.3 : 0.5 ) );
			}

			if ( g_flDecalKill > 0.5 && g_flDecalAlbedoUv1 < 0.5 && ( any( uv < 0 ) || any( uv > 1 ) ) )
				discard;

			float4 albedo = H3Albedo( uv, viewWs, vertexNormal );

			// Bump (bump_mapping): 0 off, 1 standard, 2 detail (detail_unorm, detail_masked alike, masked by the mask's red).
			float3 nt = float3( 0, 0, 1 );
			int bumpMode = (int)round( g_flBumpMode );
			if ( bumpMode >= 1 )
				nt = H3BumpNormal( SAMPLE( BumpMap, uv ), g_flBumpMapUnsigned );
			if ( bumpMode >= 2 )
			{
				float mask = bumpMode == 3 ? SAMPLE( BumpDetailMaskMap, uv ).r : 1;
				nt.xy += H3BumpNormal( SAMPLE( BumpDetailMap, uv ), g_flBumpDetailMapUnsigned ).xy * g_flBumpDetailCoefficient * mask;
			}
			nt = normalize( nt );
			s.Normal = normalize( nt.x * tangentWs + nt.y * binormalWs + nt.z * vertexNormal );
			s.Albedo = max( albedo.rgb, 0 ) * H3DecalAlbedo( g_flDecalAlbedoUv1 > 0.5 ? float4( i.vTextureCoords.zw, 0, 0 ) : i.vDecalTexCoords, i.vDecalTexCoord2 );
			s.Alpha = saturate( albedo.a );
			s.Uv = uv;

			// Specular mask: 0 none (1), 1 from diffuse (albedo alpha), 2 from texture (alpha), 3 from colour texture (rgb luminance).
			int maskMode = (int)round( g_flSpecularMaskMode );
			s.SpecularMask = maskMode == 1 ? s.Alpha : maskMode == 2 ? SAMPLE( SpecularMaskTexture, uv ).a : maskMode == 3 ? dot( SAMPLE( SpecularMaskTexture, uv ).rgb, LumaWeights ) : 1;

			#if HALO3_BLEND == 2 || HALO3_BLEND == 3
				// Multiply blends multiply the frame by the albedo itself; in the view modes and wireframe by one, so what's under them shows.
				if ( g_bWireframeMode || ToolsVis::WantsToolsVis() )
				{
					float one = HALO3_BLEND == 3 ? 0.5 : 1;
					return float4( one, one, one, 1 );
				}
				return float4( HALO3_BLEND == 3 ? s.Albedo * 0.5 : s.Albedo, 1 );
			#endif

			Halo3Light light = H3StaticLight( i );
			s.SunVisibility = H3SunVisibility( i.vPositionWithOffsetWs.xyz + g_vHighPrecisionLightingOffsetWs.xyz, vertexNormal, light, (int)round( g_flLightingMode ) != 3 );
			s.Prt = (int)round( g_flLightingMode ) == 3 ? 1 - i.vSh1.w : 1;
			if ( g_flShadowDebug > 0.5 )
			{
				float3 pWs = i.vPositionWithOffsetWs.xyz + g_vHighPrecisionLightingOffsetWs.xyz;
				int cascade;
				float raw = H3SunCascadeVisibility( pWs, vertexNormal, cascade );
				float3 ambientD, sunD;
				H3DiffuseSplit( light, s.Normal, ambientD, sunD );
				float share = dot( LumaWeights, sunD ) / max( dot( LumaWeights, ambientD + sunD ), 1e-5 );
				float v = g_flShadowDebug < 1.5 ? s.SunVisibility : g_flShadowDebug < 2.5 ? raw : g_flShadowDebug < 3.5 ? H3ShadowReach( pWs, cascade ) : share;
				// 5 the cube map sample (rgb x a x 256), 6 the specular mask, 7 the SH radiance toward the reflection (H3PhongAreaRadiance), 8 the albedo: material debugging, through the same attribute.
				if ( g_flShadowDebug > 4.5 )
				{
					float3 r = 2 * dot( s.View, s.Normal ) * s.Normal - s.View;
					#if !HALO3_TERRAIN
					float4 raw4 = H3SampleEnvRaw( float3( r.x, -r.y, r.z ), 0 );
					#else
					float4 raw4 = 0;
					#endif
					float3 f0d = lerp( g_vFresnelColor, s.Albedo, g_flAlbedoBlend );
					float3 areaD;
					H3CookTorrance( s, light, f0d, g_flRoughness, areaD );
					float3 shadeD = H3Shade( s, light );
					float3 c = g_flShadowDebug < 5.5 ? raw4.rgb * raw4.a * 256 : g_flShadowDebug < 6.5 ? s.SpecularMask : g_flShadowDebug < 7.5 ? H3PhongAreaRadiance( light, r ) : g_flShadowDebug < 8.5 ? s.Albedo : g_flShadowDebug < 9.5 ? areaD : g_flShadowDebug < 10.5 ? f0d : g_flShadowDebug < 11.5 ? g_tCookTorranceCc.SampleLevel( g_sLightmap, float2( 0.9, 0.1 ), 0 ).rgb : g_flShadowDebug < 12.5 ? g_vDebugEnv : g_vDebugSpecular;
					return float4( H3FinishColor( c ), 1 );
				}
				return float4( v, v, v, 1 );
			}
			float3 color = g_flLightingMode > 3.5 ? s.Albedo * max( i.vSh0.rgb, 0 ) : H3Shade( s, light );
			color += H3SelfIllumination( uv, s.Albedo, float2( dot( viewWs, tangentWs ), dot( viewWs, binormalWs ) ) );

			// Halogram overlay: 1 additive, 2 additive detail, 3 multiply, 4 multiply and additive detail.
			int overlay = (int)round( g_flOverlayMode );
			if ( overlay > 0 )
			{
				float3 o = SAMPLE( OverlayMap, uv ).rgb * g_vOverlayTint * g_flOverlayIntensity;
				if ( overlay == 2 || overlay == 4 )
					o *= SAMPLE( OverlayDetailMap, uv ).rgb * DetailMultiplier;
				color = overlay == 3 ? color * o : color + o;
			}

			// Halogram edge fade: tint between centre and edge by facing.
			float opacity = s.Alpha;

			// Glass: the fresnel curve joins the opacity.
			if ( (int)round( g_flMaterialModel ) == 6 )
				opacity = saturate( s.Alpha + H3GlassFresnel( dot( viewWs, s.Normal ) ) );
			if ( g_flEdgeFade > 0.5 )
			{
				// |N.V| as MCC's halogram templates: a sheet fades toward its edge tint seen edge on from either side.
				float f = pow( abs( dot( viewWs, s.Normal ) ), max( g_flEdgeFadePower, 0.01 ) );
				color *= lerp( g_vEdgeFadeEdgeTint, g_vEdgeFadeCenterTint, f );
			}

			color *= g_vTint.rgb * g_vTint.a;

			if ( g_bWireframeMode || ToolsVis::WantsToolsVis() )
			{
				float shadow = H3SunShadowFactor( light, s.Normal, s.SunVisibility );
				float3 diffuse = g_flLightingMode > 3.5 ? max( i.vSh0.rgb, 0 ) : H3DiffuseLight( light, s.Normal ) * g_flDiffuseCoefficient * shadow;
				return H3ToolsVis( i, s, nt, diffuse, ( g_vDebugSpecular + g_vDebugEnv ) * shadow, g_flDebugRoughness, opacity, g_tBaseMap );
			}

			// Fog: additive light only fades; everything else also gains the inscattered light. Premultiplied blends then scale the whole colour by the opacity, as MCC's templates do after fog and exposure (lockout's _0_0_0_0_0_0_4_5 and Valhalla's glass_wall alike).
			#if HALO3_BLEND == 0
				color *= fogExtinction;
			#elif HALO3_BLEND == 4
				color = ( color * fogExtinction + fogInscatter ) * opacity;
			#else
				color = color * fogExtinction + fogInscatter;
			#endif
			float3 finished = H3FinishColor( color );

			#if HALO3_BLEND == 0
				return float4( finished, 1 );
			#elif HALO3_BLEND == 4
				return float4( finished, opacity );
			#else
				return float4( finished, HALO3_BLEND < 0 ? 1 : opacity );
			#endif
		#endif
	}
}
