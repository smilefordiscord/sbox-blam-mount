//
// Halo CE shader_transparent_glass and shader_transparent_water.
//
// Both draw what's behind them tinted by a map and a color, then add a cube map reflection that's
// brightened and tinted differently when seen head on (perpendicular) or at a glancing angle
// (parallel). Bump maps bend the reflection and the background: water averages up to four
// scrolling ripple layers, glass has one static bump map. Glass can also blend a lightmapped
// diffuse map on top.
//
// Halo rendered the water's ripple layers into one bump texture with its own mip chain, fading
// each mip toward flat ("ripple mipmap levels / fade factor"), so distant water is a smooth
// mirror. That's reproduced by picking the ripple mip level here and fading by it.
//
// The background comes from s&box's frame buffer copy, so the blend happens here and the result
// is written out opaque.
//

HEADER
{
	Description = "Halo CE glass and water shader";
}

FEATURES
{
	#include "common/features.hlsl"
	Feature( F_GLASS_PASS, 0..2( 0="Over the frame buffer copy", 1="Glass tint", 2="Glass reflection and diffuse" ), "Glass" );
	Feature( F_TWO_SIDED, 0..1, "Rendering" );
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

	#define S_UV2 1
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
		PixelInput i = ProcessVertex( v );
		return FinalizeVertex( i );
	}
}

PS
{
	#include "common/pixel.hlsl"
	#include "common/classes/DynamicReflections.hlsl"

	// Pass 0 (water, and the glass in object models' own resources) draws what's behind from the
	// frame buffer copy and writes it out opaque. Glass in levels and on placed objects draws like
	// Halo's glass instead, in two objects (see StructureBspModel and ObjectGlass): pass 1
	// multiplies what's behind by the tint, pass 2 adds the reflection and blends the diffuse map
	// over it. Without the copy, translucent things behind the glass (b30's map room hologram, a
	// powerup's orb) stay visible.
	StaticCombo( S_GLASS_PASS, F_GLASS_PASS, Sys( ALL ) );
	// Halo culled the back faces of glass that isn't two-sided: b30's mirror floors are a face
	// each way, and with both drawn the tint and the reflection applied twice. Water and the frame
	// buffer copy pass stay two-sided.
	StaticCombo( S_TWO_SIDED, F_TWO_SIDED, Sys( ALL ) );
	#if S_GLASS_PASS == 0
		RenderState( CullMode, NONE );
	#else
		RenderState( CullMode, S_TWO_SIDED ? NONE : DEFAULT );
	#endif
	#if S_GLASS_PASS == 1
		RenderState( BlendEnable, true );
		RenderState( SrcBlend, ZERO );
		RenderState( DstBlend, SRC_COLOR );
		RenderState( BlendOp, ADD );
		RenderState( SrcBlendAlpha, ZERO );
		RenderState( DstBlendAlpha, ONE );
	#elif S_GLASS_PASS == 2
		RenderState( BlendEnable, true );
		RenderState( SrcBlend, ONE );
		RenderState( DstBlend, INV_SRC_ALPHA );
		RenderState( BlendOp, ADD );
		RenderState( SrcBlendAlpha, ZERO );
		RenderState( DstBlendAlpha, ONE );
	#else
		RenderState( BlendEnable, false );
		BoolAttribute( bWantsFBCopyTexture, true );
	#endif

	SamplerState g_sWrap < Filter( Anisotropic ); AddressU( WRAP ); AddressV( WRAP ); >;
	SamplerState g_sClamp < Filter( Bilinear ); AddressU( CLAMP ); AddressV( CLAMP ); >;

	// Water base maps are drawn once over the whole water (b30's "water mask" is the island's
	// shape, cutting the reflection near the shore), and the open sea past them takes the edge
	// texel. Wrapping repeated the island all over b30's sea, whose UVs run from -4 to 5.
	SamplerState g_sTintClamp < Filter( Anisotropic ); AddressU( CLAMP ); AddressV( CLAMP ); >;

	// Ripples and glass bumps are sampled at an explicit, isotropic mip level: anisotropic
	// filtering keeps fine detail at glancing angles, which turns into glitter in the reflection.
	SamplerState g_sBump < Filter( TRILINEAR ); AddressU( WRAP ); AddressV( WRAP ); >;

	Texture2D g_tFrameBufferCopyTexture < Attribute( "FrameBufferCopyTexture" ); SrgbRead( false ); >;

	CreateInputTexture2D( TintMap, Linear, 8, "None", "_color", ",0/,0/0", Default4( 1.00, 1.00, 1.00, 1.00 ) );
	CreateInputTexture2D( BumpMap, Linear, 8, "None", "_color", ",0/,0/0", Default4( 0.50, 0.50, 1.00, 1.00 ) );
	CreateInputTexture2D( DiffuseMap, Linear, 8, "None", "_color", ",0/,0/0", Default4( 0.00, 0.00, 0.00, 0.00 ) );
	CreateInputTexture2D( Lightmap, Linear, 8, "None", "_color", ",0/,0/0", Default4( 1.00, 1.00, 1.00, 1.00 ) );

	Texture2D g_tTintMap < Channel( RGBA, Box( TintMap ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( false ); >;
	Texture2D g_tBumpMap < Channel( RGBA, Box( BumpMap ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( false ); >;
	Texture2D g_tDiffuseMap < Channel( RGBA, Box( DiffuseMap ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( false ); >;
	Texture2D g_tLightmap < Channel( RGBA, Box( Lightmap ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( false ); >;
	CreateInputTexture2D( ReflectionMap, Linear, 8, "None", "_color", ",0/,0/0", Default4( 0.00, 0.00, 0.00, 1.00 ) );
	Texture2D g_tReflectionMap < Channel( RGBA, Box( ReflectionMap ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( false ); >;

	// Background tint: color, and whether the tint map's color and alpha are used.
	float3 g_vBackgroundTint < Default3( 1.0, 1.0, 1.0 ); >;
	float g_flTintMapScale < Default( 1.0 ); >;
	float g_flTintMapColorTintsBackground < Default( 1.0 ); >;
	float g_flTintMapAlphaScalesReflection < Default( 0.0 ); >;
	float g_flTintMapClamp < Default( 0.0 ); >;

	// Reflection brightness (perpendicular, parallel) and tints, head on and at glancing angles.
	// Glass does it like Halo's glass reflection pass: tint and brightness blended by facing^2,
	// facing being the bumped normal against the camera's forward direction, and the color
	// lerp( cube^8, cube, tint ) times the brightness, so a dark tint keeps the cube map's
	// highlights rather than dimming the whole reflection.
	// Water does it like Halo's water passes:
	//  - strength: the brightnesses (perpendicular, parallel) blended per pixel by 0.5 + 0.5 N.V
	//    (Halo looked the eye vector up in a normalization cube map, which stores vectors biased
	//    to 0..1), so even at the horizon only about halfway to the parallel brightness;
	//  - color: lerp( cube^8, cube, tint ), the tint blended once for the whole surface by how far
	//    the camera looks down at it (straight down: perpendicular);
	//  - added on top of what's behind, unfogged unless the shader asks for atmospheric fog, which
	//    then only fades the reflection (what's behind was fogged when it was drawn).
	float g_flWaterReflection < Default( 0.0 ); >;
	// Glass whose reflection is a dynamic mirror (Halo's mirror glass never uses its cube map).
	// Halo rendered the scene mirrored in the glass's plane and the reflection pass read it. Here
	// MirrorReflections renders it for the camera and puts it on the glass object's attributes:
	// HaloMirror, laid out like the screen, while HaloHasMirror is 1. Without it this frame (too
	// far, out of view) it falls back to s&box's screen space reflections where the camera has
	// them, otherwise none.
	float g_flDynamicMirror < Default( 0.0 ); >;
	float g_flHaloHasMirror < Attribute( "HaloHasMirror" ); Default( 0.0 ); >;
	Texture2D g_tHaloMirror < Attribute( "HaloMirror" ); SrgbRead( false ); >;
	float g_flWaterAtmosphericFog < Default( 0.0 ); >;
	// Water drawn before the level's fog pass (c10's swamp): Halo drew it without depth, then the fog of the ground under it covered the whole pixel, reflection too.
	float g_flWaterDrawBeforeFog < Default( 0.0 ); >;
	float2 g_vReflectionBrightness < Default2( 0.1, 1.0 ); >;
	float3 g_vPerpendicularTint < Default3( 1.0, 1.0, 1.0 ); >;
	float3 g_vParallelTint < Default3( 1.0, 1.0, 1.0 ); >;
	float g_flHasReflection < Default( 0.0 ); >;

	// Bump layers: xy scroll per second, z repeats, w contribution (layers are averaged by it).
	// Offsets packed two per float4.
	float g_flBumpScale < Default( 1.0 ); >;
	float g_flBumpMapSize < Default( 256.0 ); >;
	float4 g_vBumpLayer0 < Default4( 0.0, 0.0, 1.0, 1.0 ); >;
	float4 g_vBumpLayer1 < Default4( 0.0, 0.0, 1.0, 0.0 ); >;
	float4 g_vBumpLayer2 < Default4( 0.0, 0.0, 1.0, 0.0 ); >;
	float4 g_vBumpLayer3 < Default4( 0.0, 0.0, 1.0, 0.0 ); >;
	float4 g_vBumpOffsets01 < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float4 g_vBumpOffsets23 < Default4( 0.0, 0.0, 0.0, 0.0 ); >;

	// Scroll of the combined ripples (bump texture repeats per second).
	float2 g_vRippleScroll < Default2( 0.0, 0.0 ); >;

	// Ripple mip fade: x = mip levels it spans, y = flatness reached at the last of them (0..1),
	// z = mip level bias.
	float3 g_vRippleFade < Default3( 1.0, 0.0, 0.0 ); >;

	float g_flDiffuseMapScale < Default( 1.0 ); >;
	float g_flHasDiffuse < Default( 0.0 ); >;
	float g_flHasLightmap < Default( 0.0 ); >;

	// Halo's planar fog (fog planes, see halo1_fog_plane.shader): a view is fogged by the fog plane
	// the camera's cluster is under. The fogs draw before water and glass but after the frame
	// buffer copy is taken, so what's seen through the surface is fogged here instead. A row of
	// three texels per fog of the BSP: plane (normal out of the fog, distance), (opaque distance,
	// opaque depth, maximum density, atmosphere dominant), (gamma color); s&box units. The BSP's
	// collision tree finds the camera's fog. See PlanarFog.cs.
	CreateInputTexture2D( PlanarFogVolumes, Linear, 8, "None", "_color", ",0/,0/0", Default4( 0.00, 0.00, 0.00, 0.00 ) );
	Texture2D g_tPlanarFogVolumes < Channel( RGBA, Box( PlanarFogVolumes ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( false ); >;
	float g_flPlanarFogVolumeCount < Default( 0.0 ); >;
	CreateInputTexture2D( PlanarFogTree, Linear, 8, "None", "_color", ",0/,0/0", Default4( 0.00, 0.00, 0.00, 0.00 ) );
	Texture2D g_tPlanarFogTree < Channel( RGBA, Box( PlanarFogTree ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( false ); >;
	SamplerState g_sPlanarFogDensity < Filter( Bilinear ); AddressU( CLAMP ); AddressV( CLAMP ); >;
	CreateInputTexture2D( PlanarFogDensity, Linear, 8, "None", "_color", ",0/,0/0", Default4( 1.00, 1.00, 1.00, 1.00 ) );
	Texture2D g_tPlanarFogDensity < Channel( RGBA, Box( PlanarFogDensity ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( false ); >;
	float g_flPlanarFogAtmosphereDistance < Default( 0.0 ); >;

	// The frame grabbed right before the planar fog draws (PlanarFogFrame), what's behind the water without the fog; s&box's copy can hold the fog already.
	Texture2D g_tHaloFogPlaneBehind < Attribute( "HaloFogPlaneBehind" ); SrgbRead( false ); >;
	bool g_bHaloHasFogPlaneBehind < Attribute( "HaloHasFogPlaneBehind" ); Default( 0 ); >;

	// The sRGB curve both ways (exact inverses, so nothing changes where nothing is added).
	float3 GammaToLinear( float3 c )
	{
		c = saturate( c );
		return lerp( pow( ( c + 0.055 ) / 1.055, 2.4 ), c / 12.92, step( c, 0.04045 ) );
	}

	float3 LinearToGamma( float3 c )
	{
		c = saturate( c );
		return lerp( 1.055 * pow( c, 1.0 / 2.4 ) - 0.055, c * 12.92, step( c, 0.0031308 ) );
	}

	// The same as halo1_fog_plane.shader's.
	static const int TreeNodesPerRow = 1024;
	static const int MaximumTreeDepth = 128;

	// The fog of the cluster a point is in, as halo1_fog_plane.shader finds it, or -1.
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

	// Fogs a color seen at a point by the camera's fog, as halo1_fog_plane.shader fogs the level.
	// coverage is how much the fog hides it. The density is raised to 2.2 like the fog does, to
	// match Halo's gamma space blending.
	float3 ApplyPlanarFog( float3 color, float3 camera, float3 position, float2 screen, out float coverage )
	{
		coverage = 0;
		if ( g_flPlanarFogVolumeCount < 0.5 )
			return color;

		int fog = CameraFog( camera );
		if ( fog < 0 )
			return color;

		float4 plane = g_tPlanarFogVolumes.Load( int3( 0, fog, 0 ) );
		float4 fogParameters = g_tPlanarFogVolumes.Load( int3( 1, fog, 0 ) );
		float3 fogColor = SrgbGammaToLinear( g_tPlanarFogVolumes.Load( int3( 2, fog, 0 ) ).rgb );

		float2 uv = float2( dot( position - camera, g_vCameraDirWs ) / fogParameters.x, ( plane.w - dot( plane.xyz, position ) ) / fogParameters.y );
		float eye = saturate( ( plane.w - dot( plane.xyz, camera ) ) / fogParameters.y );
		float4 density = g_tPlanarFogDensity.SampleLevel( g_sPlanarFogDensity, uv, 0 );
		float gammaDensity = saturate( fogParameters.z * lerp( density.a, density.b, eye ) );
		coverage = pow( gammaDensity, 2.2 );

		float above = fogParameters.w > 0.5 ? 1
			: g_flPlanarFogAtmosphereDistance > 0 ? saturate( ( dot( plane.xyz, camera ) - plane.w ) / g_flPlanarFogAtmosphereDistance )
			: 0;

		// Exactly as halo1_fog_plane.shader fogs the level around it: toward (1 - A above) planar color + A atmospheric color in gamma space, with Halo's atmospheric density and color taken back out of s&box's fog (raised to 2.2, ScenarioSceneLoader.BuildFog).
		float3 fogged0 = DoAtmospherics( position, screen, float4( 0, 0, 0, 1 ) ).rgb;
		float atmosphericLinear = saturate( 1 - dot( saturate( DoAtmospherics( position, screen, float4( 1, 1, 1, 1 ) ).rgb - fogged0 ), 1.0 / 3.0 ) );
		float atmosphere = pow( atmosphericLinear, 1.0 / 2.2 );
		float3 atmosphericGamma = LinearToGamma( fogged0 / max( atmosphericLinear, 1e-4 ) );
		float3 fogGamma = saturate( ( 1 - atmosphere * above ) * LinearToGamma( fogColor ) + atmosphere * atmosphericGamma );
		return GammaToLinear( lerp( LinearToGamma( color ), fogGamma, gammaDensity ) );
	}

	// Fogs a color seen at a point exactly as Halo's fog pass did (MCC's fx.bin shader 122), in its 8-bit gamma frame, for pixels this shader writes whole: the camera's planar fog (density P, gamma color Cp) and the atmospheric fog (density A, gamma color Ca), the atmospheric fog going over the planar fog's color as far as the camera is above the plane (all of it for atmosphere dominant fog):
	// color (1 - P)(1 - A) + (1 - A above) P Cp + A Ca.
	float3 ApplyHaloFog( float3 color, float3 camera, float3 position, float atmosphericDensity, float3 atmosphericGamma )
	{
		float planarDensity = 0;
		float3 planarGamma = 0;
		float above = 0;
		int fog = g_flPlanarFogVolumeCount > 0.5 ? CameraFog( camera ) : -1;
		if ( fog >= 0 )
		{
			float4 plane = g_tPlanarFogVolumes.Load( int3( 0, fog, 0 ) );
			float4 fogParameters = g_tPlanarFogVolumes.Load( int3( 1, fog, 0 ) );
			planarGamma = g_tPlanarFogVolumes.Load( int3( 2, fog, 0 ) ).rgb;

			float2 uv = float2( dot( position - camera, g_vCameraDirWs ) / fogParameters.x, ( plane.w - dot( plane.xyz, position ) ) / fogParameters.y );
			float eye = saturate( ( plane.w - dot( plane.xyz, camera ) ) / fogParameters.y );
			float4 density = g_tPlanarFogDensity.SampleLevel( g_sPlanarFogDensity, uv, 0 );
			planarDensity = saturate( fogParameters.z * lerp( density.a, density.b, eye ) );

			above = fogParameters.w > 0.5 ? 1
				: g_flPlanarFogAtmosphereDistance > 0 ? saturate( ( dot( plane.xyz, camera ) - plane.w ) / g_flPlanarFogAtmosphereDistance )
				: 0;
		}

		float3 gamma = LinearToGamma( color ) * ( 1 - planarDensity ) * ( 1 - atmosphericDensity )
			+ saturate( planarDensity * planarGamma ) * ( 1 - atmosphericDensity * above )
			+ saturate( atmosphericDensity * atmosphericGamma );
		return GammaToLinear( gamma );
	}

	// Cube maps arrive as a 2D atlas of six faces side by side (+X, -X, +Y, -Y, +Z, -Z), and the
	// face and position within it are picked with Direct3D's cube map rules.
	float3 SampleCubeAtlas( float3 dir )
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
		float2 uv = clamp( st / ma * 0.5 + 0.5, 0.01, 0.99 );
		return g_tReflectionMap.SampleLevel( g_sClamp, float2( ( face + uv.x ) / 6.0, uv.y ), 0 ).rgb;
	}

	// The mirror glass's reflection at a screen position (pixels), gamma space: its mirror, else
	// s&box's dynamic reflections, else black.
	float3 SampleMirror( float2 screenPosition )
	{
		if ( g_flHaloHasMirror > 0.5 )
			return LinearToGamma( g_tHaloMirror.SampleLevel( g_sClamp, screenPosition * g_vInvViewportSize, 0 ).rgb );

		return DynamicReflections::IsEnabled() ? LinearToGamma( DynamicReflections::Sample( screenPosition ).rgb ) : 0;
	}

	// Glass's cube map reflection (gamma space), see g_vReflectionBrightness.
	float3 GlassReflection( float3 cube, float3 normal )
	{
		float facing = saturate( dot( normal, -g_vCameraDirWs ) );
		facing *= facing;
		float3 tint = lerp( g_vParallelTint, g_vPerpendicularTint, facing );
		float brightness = lerp( g_vReflectionBrightness.y, g_vReflectionBrightness.x, facing );
		float3 highlights = cube * cube;
		highlights *= highlights;
		highlights *= highlights;
		return lerp( highlights, cube, tint ) * brightness;
	}

	// One bump layer's tangent space normal (unnormalized, -1..1) times its contribution. A layer
	// repeating more often is that many times finer, so it's a mip level further down.
	float3 SampleBumpLayer( float2 rippleUv, float lod, float4 layer, float2 offset )
	{
		float2 bumpUv = rippleUv * layer.z + offset + layer.xy * g_flTime;
		float3 bump = g_tBumpMap.SampleLevel( g_sBump, bumpUv, lod + log2( max( layer.z, 1.0 ) ) ).xyz * 2.0 - 1.0;
		return bump * layer.w;
	}

	float4 MainPs( PixelInput i ) : SV_Target0
	{
		float2 uv = i.vTextureCoords.xy;

		// Mip level of the combined bump texture, picked like the GPU would (but isotropic), with
		// the shader's detail bias (Halo's LOD bias is minus it: sharper). Glancing views get a high
		// level, so distant water flattens out.
		float2 texels = uv * g_flBumpScale * g_flBumpMapSize;
		float footprint = max( length( ddx( texels ) ), length( ddy( texels ) ) );
		float lod = log2( max( footprint, 1e-6 ) ) - g_vRippleFade.z;

		// Halo's combined ripple texture has only as many mip levels as the water asks for (at most 4); c10's swamp asks for one, so its ripples stay sharp at any distance.
		float rippleLevels = clamp( g_vRippleFade.x, 1, 4 );
		if ( g_flWaterReflection > 0.5 )
			lod = min( lod, rippleLevels - 1 );

		float2 rippleUv = uv * g_flBumpScale + g_vRippleScroll * g_flTime;
		float3 rippleSum = SampleBumpLayer( rippleUv, lod, g_vBumpLayer0, g_vBumpOffsets01.xy )
			+ SampleBumpLayer( rippleUv, lod, g_vBumpLayer1, g_vBumpOffsets01.zw )
			+ SampleBumpLayer( rippleUv, lod, g_vBumpLayer2, g_vBumpOffsets23.xy )
			+ SampleBumpLayer( rippleUv, lod, g_vBumpLayer3, g_vBumpOffsets23.zw );
		float totalContribution = g_vBumpLayer0.w + g_vBumpLayer1.w + g_vBumpLayer2.w + g_vBumpLayer3.w;
		float3 bumpTs = totalContribution > 0 ? rippleSum / max( totalContribution, 1e-4 ) : float3( 0, 0, 1 );

		// Fade toward flat over the ripple mip levels: level i by i / (levels - 1) times the fade factor, none with one level.
		float flatness = g_vRippleFade.x > 1.5 ? g_vRippleFade.y * saturate( lod / ( g_vRippleFade.x - 1.0 ) ) : 0;
		bumpTs = normalize( lerp( bumpTs, float3( 0, 0, 1 ), flatness ) );
		float2 bump = bumpTs.xy;

		// Halo's bump maps: x along the tangent (+U), y along the binormal (+V), no flip.
		float3 surfaceNormal = normalize( i.vNormalWs );
		float3 normal = normalize( bumpTs.x * normalize( i.vTangentUWs ) + bumpTs.y * normalize( i.vTangentVWs ) + bumpTs.z * surfaceNormal );
		float3 toCamera = normalize( -i.vPositionWithOffsetWs.xyz );

		float4 tint = g_flTintMapClamp > 0.5
			? Tex2DS( g_tTintMap, g_sTintClamp, uv * g_flTintMapScale )
			: Tex2DS( g_tTintMap, g_sWrap, uv * g_flTintMapScale );

		#if S_GLASS_PASS == 1
		{
			// What's behind, times the tint (and the tint map where it tints the background).
			float3 multiply = g_vBackgroundTint * ( g_flTintMapColorTintsBackground > 0.5 ? tint.rgb : float3( 1, 1, 1 ) );

			// Halo's tint pass fades toward white by the fog in front of the glass (final combiner v0.a x tint + (1 - v0.a) x 1, the fog's transmittance in v0.a): blue floor glass far off in c10's fog stayed fully blue.
			float3 positionWs = i.vPositionWithOffsetWs.xyz + g_vHighPrecisionLightingOffsetWs.xyz;
			float3 fogged0 = DoAtmospherics( positionWs, i.vPositionSs.xy, float4( 0, 0, 0, 1 ) ).rgb;
			float atmosphericLinear = saturate( 1 - dot( saturate( DoAtmospherics( positionWs, i.vPositionSs.xy, float4( 1, 1, 1, 1 ) ).rgb - fogged0 ), 1.0 / 3.0 ) );
			float transmittance = 1 - pow( atmosphericLinear, 1.0 / 2.2 );
			if ( g_flPlanarFogVolumeCount > 0.5 )
			{
				float coverage;
				ApplyPlanarFog( float3( 0, 0, 0 ), g_vCameraPositionWs.xyz, positionWs, i.vPositionSs.xy, coverage );
				transmittance *= 1 - pow( coverage, 1.0 / 2.2 );
			}
			multiply = lerp( 1, multiply, transmittance );

			return float4( SrgbGammaToLinear( saturate( multiply ) ), 1 );
		}
		#elif S_GLASS_PASS == 2
		{
			float3 reflection = 0;
			if ( g_flHasReflection > 0.5 || g_flDynamicMirror > 0.5 )
			{
				// Mirror glass reflects only the mirror (Halo never uses its cube map).
				float3 cube = g_flDynamicMirror > 0.5
					? SampleMirror( i.vPositionSs.xy + bump * 20 )
					: SampleCubeAtlas( reflect( -toCamera, normal ) );
				reflection = SrgbGammaToLinear( saturate( GlassReflection( cube, normal ) ) );
			}

			float3 diffuse = 0;
			float diffuseAlpha = 0;
			if ( g_flHasDiffuse > 0.5 )
			{
				// Halo lights it by lightmap + vertex light, clamped to 1 by the register combiners,
				// which leaves it unlit in practice: b30's map room mirror floors have a black lightmap
				// under the glass, yet MCC shows their edge detail plainly.
				float4 map = Tex2DS( g_tDiffuseMap, g_sWrap, uv * g_flDiffuseMapScale );
				diffuse = SrgbGammaToLinear( saturate( map.rgb ) );

				// Halo alpha blended it in its gamma space frame buffer: over the dark things seen
				// through glass, alpha a there covers about a^2.2 here (like the surf on b30's water).
				diffuseAlpha = pow( saturate( map.a ), 2.2 );
			}

			// What's behind was fogged when it was drawn; fog what's added here like it.
			float3 positionWs = i.vPositionWithOffsetWs.xyz + g_vHighPrecisionLightingOffsetWs.xyz;
			float3 fogged0 = DoAtmospherics( positionWs, i.vPositionSs.xy, float4( 0, 0, 0, 1 ) ).rgb;
			float3 fogged1 = DoAtmospherics( positionWs, i.vPositionSs.xy, float4( 1, 1, 1, 1 ) ).rgb;
			float3 transmittance = saturate( fogged1 - fogged0 );
			diffuse = DoAtmospherics( positionWs, i.vPositionSs.xy, float4( diffuse, 1 ) ).rgb;

			// The planar fog draws before the glass (as Halo's fog pass did), so the glass fogs what it adds itself, as Halo's transparent shaders did.
			if ( g_flPlanarFogVolumeCount > 0.5 )
			{
				float coverage;
				diffuse = ApplyPlanarFog( diffuse, g_vCameraPositionWs.xyz, positionWs, i.vPositionSs.xy, coverage );
				transmittance *= 1 - pow( coverage, 1.0 / 2.2 );
			}

			return float4( reflection * transmittance * ( 1 - diffuseAlpha ) + diffuse * diffuseAlpha, diffuseAlpha );
		}
		#endif

		// What's behind the surface, tinted (in gamma space, like Halo). Glass is nudged by its bumps; Halo's water blends straight over the frame, and a nudge pulled the bank into the water along every shore as a bright strip.
		float2 screenUv = i.vPositionSs.xy * g_vInvViewportSize + ( g_flWaterReflection > 0.5 ? 0 : bump * 0.02 );
		float3 background = g_tFrameBufferCopyTexture.SampleLevel( g_sClamp, screenUv * g_vFrameBufferCopyInvSizeAndUvScale.zw, 0 ).rgb;
		if ( g_bHaloHasFogPlaneBehind && g_flWaterReflection > 0.5 )
			background = g_tHaloFogPlaneBehind.SampleLevel( g_sClamp, ( i.vPositionSs.xy - g_vViewportOffset ) * g_vInvViewportSize, 0 ).rgb;

		// Water drawn before the fog (c10's swamp) is tinted and reflects before the level's fog covers the ground under it, so the ground's atmospheric fog comes off what's behind here and both fogs go over the whole pixel at the end.
		bool sceneIsLevel = Depth::GetNormalized( i.vPositionSs.xy ) > 0;
		bool beforeFog = g_flWaterReflection > 0.5 && g_flWaterDrawBeforeFog > 0.5 && sceneIsLevel;
		float3 scene = Depth::GetWorldPosition( i.vPositionSs.xy );
		float3 sceneFogged0 = 0;
		float3 sceneTransmittance = 1;
		if ( beforeFog )
		{
			sceneFogged0 = DoAtmospherics( scene, i.vPositionSs.xy, float4( 0, 0, 0, 1 ) ).rgb;
			sceneTransmittance = saturate( DoAtmospherics( scene, i.vPositionSs.xy, float4( 1, 1, 1, 1 ) ).rgb - sceneFogged0 );
			background = saturate( ( background - sceneFogged0 ) / max( sceneTransmittance, 0.02 ) );
		}

		// Planar fog between the camera and what's behind the surface, and between the camera and
		// the surface itself (when the surface is in the fog, its reflection is fogged too).
		float reflectionVisibility = 1;
		float3 camera = g_vCameraPositionWs.xyz;
		float3 toSurface = i.vPositionWithOffsetWs.xyz + g_vHighPrecisionLightingOffsetWs.xyz - camera;
		float surfaceDistance = length( toSurface );
		float3 viewDir = toSurface / max( surfaceDistance, 0.001 );
		float3 behind = camera + viewDir * max( length( scene - camera ), surfaceDistance );
		if ( g_flPlanarFogVolumeCount > 0.5 )
		{
			// What's behind, unless it's the sky (Halo's planar fog is on the level and objects).
			float coverage;
			if ( sceneIsLevel && !beforeFog )
				background = ApplyPlanarFog( background, camera, behind, i.vPositionSs.xy, coverage );
			// Halo's water reflection pass has no planar fog in it, so the reflection shows even from
			// inside the fog (MCC under b30's sea: the underside of the surface is all sky). Glass,
			// drawn like any other surface, is fogged.
			ApplyPlanarFog( float3( 0, 0, 0 ), camera, camera + toSurface, i.vPositionSs.xy, coverage );
			reflectionVisibility = g_flWaterReflection > 0.5 ? 1 : 1 - coverage;
		}

		float3 backgroundTint = g_vBackgroundTint * ( g_flTintMapColorTintsBackground > 0.5 ? tint.rgb : float3( 1, 1, 1 ) );

		// Water with atmospheric fog blends its base map toward white by the fog at its surface before multiplying (Halo's combiner fog x 1 + (1 - fog) x map, MCC fx.bin shader 110): in c10's mist the pools hardly darken what's under them.
		if ( g_flWaterReflection > 0.5 && g_flWaterAtmosphericFog > 0.5 && g_flTintMapColorTintsBackground > 0.5 )
		{
			float3 surface = i.vPositionWithOffsetWs.xyz + g_vHighPrecisionLightingOffsetWs.xyz;
			float3 surfaceFogged0 = DoAtmospherics( surface, i.vPositionSs.xy, float4( 0, 0, 0, 1 ) ).rgb;
			float surfaceFog = saturate( 1 - dot( saturate( DoAtmospherics( surface, i.vPositionSs.xy, float4( 1, 1, 1, 1 ) ).rgb - surfaceFogged0 ), 1.0 / 3.0 ) );
			backgroundTint = lerp( backgroundTint, 1, pow( surfaceFog, 1.0 / 2.2 ) );
		}

		float3 color = background * SrgbGammaToLinear( backgroundTint );

		if ( g_flHasReflection > 0.5 )
		{
			float3 cube = SampleCubeAtlas( reflect( -toCamera, normal ) );
			float3 reflection;
			if ( g_flWaterReflection > 0.5 )
			{
				float facing = saturate( dot( surfaceNormal, toCamera ) );
				float strength = lerp( g_vReflectionBrightness.y, g_vReflectionBrightness.x, 0.5 + 0.5 * facing );
				float lookingDown = saturate( -dot( g_vCameraDirWs, surfaceNormal ) );
				float3 tintColor = lerp( g_vParallelTint, g_vPerpendicularTint, lookingDown );
				float3 cube2 = cube * cube;
				float3 cube8 = cube2 * cube2;
				cube8 *= cube8;
				reflection = lerp( cube8, cube, tintColor ) * strength;
			}
			else
			{
				reflection = GlassReflection( cube, normal );
			}
			if ( g_flTintMapAlphaScalesReflection > 0.5 )
				reflection *= tint.a;

			if ( g_flWaterReflection > 0.5 && g_flWaterAtmosphericFog > 0.5 )
			{
				// Halo's reflection pass (MCC fx.bin shader 111) scales by 1 - its atmospheric density, which s&box's fog holds raised to 2.2 (ScenarioSceneLoader.BuildFog); its own transmittance faded far too little and lit the shoreline (the base map alpha's mask) up.
				float3 positionWs = i.vPositionWithOffsetWs.xyz + g_vHighPrecisionLightingOffsetWs.xyz;
				float3 fogged0 = DoAtmospherics( positionWs, i.vPositionSs.xy, float4( 0, 0, 0, 1 ) ).rgb;
				float3 fogged1 = DoAtmospherics( positionWs, i.vPositionSs.xy, float4( 1, 1, 1, 1 ) ).rgb;
				float atmosphericLinear = saturate( 1 - dot( saturate( fogged1 - fogged0 ), 1.0 / 3.0 ) );
				reflection *= 1 - pow( atmosphericLinear, 1.0 / 2.2 );
			}

			// Added in gamma space, as Halo's frame buffer did: added in linear space, the reflection
			// drowned b30's dark green sea in blue-gray.
			color = GammaToLinear( saturate( LinearToGamma( color ) + saturate( reflection ) * reflectionVisibility ) );
		}

		if ( g_flHasDiffuse > 0.5 )
		{
			float4 diffuse = Tex2DS( g_tDiffuseMap, g_sWrap, uv * g_flDiffuseMapScale );
			float3 light = g_flHasLightmap > 0.5 ? Tex2DS( g_tLightmap, g_sClamp, i.vTextureCoords.zw ).rgb : float3( 1, 1, 1 );
			color = lerp( color, SrgbGammaToLinear( saturate( diffuse.rgb * light ) ), diffuse.a );
		}

		// The ground's fogs over draw before fog water, as Halo's fog pass drew them over the ground after it.
		if ( beforeFog )
		{
			// s&box's fog is Halo's atmospheric fog with its density raised to 2.2 (ScenarioSceneLoader.BuildFog): take Halo's density and color back out of it.
			float atmosphericLinear = saturate( 1 - dot( sceneTransmittance, 1.0 / 3.0 ) );
			float3 atmosphericGamma = LinearToGamma( sceneFogged0 / max( atmosphericLinear, 1e-4 ) );
			color = ApplyHaloFog( color, camera, behind, pow( atmosphericLinear, 1.0 / 2.2 ), atmosphericGamma );
		}

		// Water fogs its reflection itself (above); what's behind it is fogged already.
		if ( g_flWaterReflection > 0.5 )
			return float4( color, 1 );

		float3 positionWs = i.vPositionWithOffsetWs.xyz + g_vHighPrecisionLightingOffsetWs.xyz;
		return DoAtmospherics( positionWs, i.vPositionSs.xy, float4( color, 1 ) );
	}
}
