//
// Halo 2's water shader templates: pools on level geometry (water_pool, water_pool_cube, water_pool_static,
// water_pool_static_cube) and the open water a BSP's water definition draws as a plane (water_static,
// ocean_static, ocean_static_opaque, ocean_static_opaque_blend).
//
// Each program is a transliteration of the template's pixel shader in MCC, in Halo's gamma space, converted to linear
// at the end. Halo hands the
// stages these coordinates: t0 the bump map at world xy (world units) through a 2D transform (c20), t1 and t2
// the screen position (0-1, top left origin), t3 the eye vector's world xy * 0.5 + 0.5 (the fresnel bitmap is
// looked up by view direction), each nudged by the signed bump offset through a 2x2 matrix per stage: the water
// definition's reflection, refraction and fresnel bump scales times a rotation by the camera's heading plus the
// definition's rotate offset. The static pools read t2 at the mesh UV (c18) instead, the oceans at the
// definition's planar coordinates.
//
// What Halo rendered for the water, here:
//  - water_refraction (the frame before the water): the frame buffer copy, every program draws over it, so
//    pools come out opaque like Halo's (no blending) and everything is one alpha blend;
//  - water_reflection (the scene mirrored in the water's plane, every frame): a cube rendered once from just
//    above the water (Halo2/SceneUpkeep.cs), by the reflection of the view off the bumped surface;
//  - the edge blend (water_edge_blend strips on the shore writing frame buffer alpha, which water_static and
//    ocean_static_opaque_blend blend by): faded by the depth of water under the surface instead.
// The bump flipbook (17 frames played by time) arrives as an atlas of its frames side by side.
//

HEADER
{
	Description = "Halo 2 water shader";
}

FEATURES
{
	#include "common/features.hlsl"

	Feature( F_TWO_SIDED, 0..1, "Rendering" );
	// The open water plane writes depth: it draws before the sky's translucent layers (see
	// StructureBspModel.BuildOpenWater), which have to stay behind it.
	Feature( F_DEPTH_WRITE, 0..1, "Rendering" );
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

	StaticCombo( S_TWO_SIDED, F_TWO_SIDED, Sys( ALL ) );
	StaticCombo( S_DEPTH_WRITE, F_DEPTH_WRITE, Sys( ALL ) );

	RenderState( CullMode, S_TWO_SIDED ? NONE : DEFAULT );
	RenderState( DepthWriteEnable, S_DEPTH_WRITE ? true : false );
	RenderState( BlendEnable, true );
	RenderState( BlendOp, ADD );
	RenderState( SrcBlend, SRC_ALPHA );
	RenderState( DstBlend, INV_SRC_ALPHA );
	RenderState( SrcBlendAlpha, ZERO );
	RenderState( DstBlendAlpha, ONE );
	BoolAttribute( bWantsFBCopyTexture, true );

	SamplerState g_sWrap < Filter( Anisotropic ); AddressU( WRAP ); AddressV( WRAP ); >;
	SamplerState g_sClamp < Filter( Bilinear ); AddressU( CLAMP ); AddressV( CLAMP ); >;
	SamplerState g_sAtlas < Filter( Trilinear ); AddressU( CLAMP ); AddressV( WRAP ); >;

	Texture2D g_tFrameBufferCopyTexture < Attribute( "FrameBufferCopyTexture" ); SrgbRead( false ); >;

	// t0: the bump flipbook, frames side by side; t1: the reflection bitmap (2D, or a cube map as a six face
	// atlas); t2: the static texture (static pools, the oceans' plane texture); t3: the fresnel bitmap.
	CreateInputTexture2D( Bump, Linear, 8, "None", "_color", ",0/,0/0", Default4( 0.50, 0.50, 0.50, 1.00 ) );
	CreateInputTexture2D( Reflection, Linear, 8, "None", "_color", ",0/,0/0", Default4( 0.00, 0.00, 0.00, 1.00 ) );
	CreateInputTexture2D( Static, Linear, 8, "None", "_color", ",0/,0/0", Default4( 1.00, 1.00, 1.00, 1.00 ) );
	CreateInputTexture2D( Fresnel, Linear, 8, "None", "_color", ",0/,0/0", Default4( 0.50, 0.50, 1.00, 0.50 ) );
	Texture2D g_tBump < Channel( RGBA, Box( Bump ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( false ); >;
	Texture2D g_tReflection < Channel( RGBA, Box( Reflection ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( false ); >;
	Texture2D g_tStatic < Channel( RGBA, Box( Static ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( false ); >;
	Texture2D g_tFresnel < Channel( RGBA, Box( Fresnel ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( false ); >;

	// The open water's reflection: the six faces of a cube (+X, -X, +Y, -Y, +Z, -Z, see SceneUpkeep.Faces),
	// while HaloHasReflection is 1.
	float g_flHaloHasReflection < Attribute( "HaloHasReflection" ); Default( 0.0 ); >;
	Texture2D g_tHaloReflection0 < Attribute( "HaloReflection0" ); SrgbRead( false ); >;
	Texture2D g_tHaloReflection1 < Attribute( "HaloReflection1" ); SrgbRead( false ); >;
	Texture2D g_tHaloReflection2 < Attribute( "HaloReflection2" ); SrgbRead( false ); >;
	Texture2D g_tHaloReflection3 < Attribute( "HaloReflection3" ); SrgbRead( false ); >;
	Texture2D g_tHaloReflection4 < Attribute( "HaloReflection4" ); SrgbRead( false ); >;
	Texture2D g_tHaloReflection5 < Attribute( "HaloReflection5" ); SrgbRead( false ); >;

	// Which program (WaterMaterial.Program).
	float g_flProgram < Default( 0.0 ); >;

	// s&box units per Halo world unit (Halo's coordinates are world units).
	float g_flWorldScale < Default( 103.2258 ); >;

	// The bump map: c20 (u = x * xy.x + zw.x, v = y * xy.y + zw.y, from world units), time curves on its
	// offsets (see AnimatedFraction), the flipbook (frame count, period in seconds, first and last frame value of
	// its curve), and the stored format: 1 for signed bytes (V8U8, G8B8), with the offsets in (red, green) or
	// (green, blue) (z = 1).
	float4 g_vBumpXform < Default4( 1.0, 1.0, 0.0, 0.0 ); >;
	float4 g_vBumpAnimUA < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float4 g_vBumpAnimUB < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float4 g_vBumpAnimVA < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float4 g_vBumpAnimVB < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float4 g_vFlipbook < Default4( 1.0, 0.0, 0.0, 0.0 ); >;
	float3 g_vBumpFormat < Default3( 1.0, 0.0, 0.0 ); >;

	// The static pools' texture transform (c18, of the mesh UV) and its time curves.
	float4 g_vStaticXform < Default4( 1.0, 1.0, 0.0, 0.0 ); >;
	float4 g_vStaticAnimUA < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float4 g_vStaticAnimUB < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float4 g_vStaticAnimVA < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float4 g_vStaticAnimVB < Default4( 0.0, 0.0, 0.0, 0.0 ); >;

	// From the BSP's water definition: bump scales (reflection, refraction, fresnel) and the rotate offset
	// (radians), the sun (direction toward it, spot colour), the horizon colour (Halo's c3) and linear fog
	// (c24: 1 / (far - near), -near / (far - near), world units), the ocean plane texture's centre and inverse
	// half size (Halo's c22: x, -y, 1 / x, 1 / y), and how deep the water has to be (world units) before it's
	// fully drawn, standing in for the shore's edge blend.
	float4 g_vBumpScales < Default4( 0.5, 0.05, 0.15, 0.0 ); >;
	float3 g_vSunDirection < Default3( 0.0, 0.0, 1.0 ); >;
	float3 g_vSunColor < Default3( 0.0, 0.0, 0.0 ); >;
	float3 g_vHorizonColor < Default3( 0.0, 0.0, 0.0 ); >;
	float2 g_vFog < Default2( 0.0, 0.0 ); >;
	float4 g_vOceanPlane < Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float g_flShoreDepth < Default( 0.0 ); >;

	// water_static: what shows through the water instead of the frame, while w is 1 (MCC's refraction target for
	// open water holds only the sky's clear colour; see WaterMaterial.Create).
	float4 g_vRefractionColor < Default4( 0.0, 0.0, 0.0, 0.0 ); >;

	//
	// Halo 2's function evaluator for time curves (as in halo2_transparent.shader).
	//

	float TransitionShape( float shape, float x )
	{
		if ( shape < 0.5 ) return x;
		if ( shape < 1.5 ) return 1 - ( 1 - x ) * ( 1 - x );
		if ( shape < 2.5 ) return 1 - pow( 1 - x, 4 );
		if ( shape < 3.5 ) return x * x;
		if ( shape < 4.5 ) return pow( x, 4 );
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

	// A = (type + 16 shape, period in seconds, output min, output max), B = the curve's constants.
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

	// A 2D simple transform (uv * xy + zw) with time curves added to its offsets.
	float2 SimpleXform( float2 uv, float4 xform, float4 ua, float4 ub, float4 va, float4 vb )
	{
		return uv * xform.xy + xform.zw + float2( AnimatedValue( ua, ub ), AnimatedValue( va, vb ) );
	}

	float3 LinearToGamma( float3 c )
	{
		c = saturate( c );
		return lerp( 1.055 * pow( c, 1.0 / 2.4 ) - 0.055, c * 12.92, step( c, 0.0031308 ) );
	}

	// Cube maps arrive as a 2D atlas of six faces side by side (+X, -X, +Y, -Y, +Z, -Z), as in
	// halo2_transparent.shader.
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
		float2 uv = clamp( st / max( ma, 0.0001 ) * 0.5 + 0.5, 0.01, 0.99 );
		return atlas.SampleLevel( g_sClamp, float2( ( face + uv.x ) / 6.0, uv.y ), 0 ).rgb;
	}

	// The signed bump offset (du, dv) at bump coordinates: the flipbook frame for the current time, wrapped
	// inside its slot of the atlas (gradients from the unwrapped coordinates, so the wrap has no mip seam).
	float2 SampleBump( float2 uv )
	{
		float frames = max( g_vFlipbook.x, 1 );
		float frame = 0;
		if ( g_vFlipbook.y > 0 )
			frame = clamp( floor( lerp( g_vFlipbook.z, g_vFlipbook.w, frac( g_flTime / g_vFlipbook.y ) ) ), 0, frames - 1 );

		float2 scale = float2( 1.0 / frames, 1 );
		float4 t = g_tBump.SampleGrad( g_sAtlas, float2( ( frame + frac( uv.x ) ) / frames, uv.y ), ddx( uv ) * scale, ddy( uv ) * scale );
		float2 d = g_vBumpFormat.z > 0.5 ? t.gb : t.rg;

		// Signed bytes arrive biased by 128 (see BitmapTextureLoader); D3D reads -128..127 as -1..1.
		return g_vBumpFormat.x > 0.5 ? ( d * 255 - 128 ) / 127 : d;
	}

	// One stage's coordinates nudged by the bump: uv + ( M.x du + M.y dv, M.z du + M.w dv ).
	float2 Bumped( float2 uv, float4 m, float2 d )
	{
		return uv + float2( dot( m.xy, d ), dot( m.zw, d ) );
	}

	// The reflection in a direction, gamma space: the face of the cube it points into, at the point where it meets
	// that face's view (each face rendered with a 90 degree view: forward and up as below, right = forward x up).
	float3 SampleReflection( float3 dir )
	{
		if ( g_flHaloHasReflection < 0.5 )
			return 0;

		float3 a = abs( dir );
		int face;
		float3 forward, up;
		if ( a.x >= a.y && a.x >= a.z )
		{
			face = dir.x > 0 ? 0 : 1;
			forward = float3( sign( dir.x ), 0, 0 );
			up = float3( 0, 0, 1 );
		}
		else if ( a.y >= a.z )
		{
			face = dir.y > 0 ? 2 : 3;
			forward = float3( 0, sign( dir.y ), 0 );
			up = float3( 0, 0, 1 );
		}
		else
		{
			face = dir.z > 0 ? 4 : 5;
			forward = float3( 0, 0, sign( dir.z ) );
			up = float3( -sign( dir.z ), 0, 0 );
		}

		float3 right = cross( forward, up );
		float depth = dot( dir, forward );
		float2 uv = saturate( float2( 0.5 + 0.5 * dot( dir, right ) / depth, 0.5 - 0.5 * dot( dir, up ) / depth ) );

		float3 c;
		if ( face == 0 ) c = g_tHaloReflection0.SampleLevel( g_sClamp, uv, 0 ).rgb;
		else if ( face == 1 ) c = g_tHaloReflection1.SampleLevel( g_sClamp, uv, 0 ).rgb;
		else if ( face == 2 ) c = g_tHaloReflection2.SampleLevel( g_sClamp, uv, 0 ).rgb;
		else if ( face == 3 ) c = g_tHaloReflection3.SampleLevel( g_sClamp, uv, 0 ).rgb;
		else if ( face == 4 ) c = g_tHaloReflection4.SampleLevel( g_sClamp, uv, 0 ).rgb;
		else c = g_tHaloReflection5.SampleLevel( g_sClamp, uv, 0 ).rgb;
		return LinearToGamma( c );
	}

	// What's behind the water at a screen position (0-1), gamma space.
	float3 SampleRefraction( float2 screenUv )
	{
		return LinearToGamma( g_tFrameBufferCopyTexture.SampleLevel( g_sClamp, saturate( screenUv ) * g_vFrameBufferCopyInvSizeAndUvScale.zw, 0 ).rgb );
	}

	float4 MainPs( PixelInput i ) : SV_Target0
	{
		int program = (int)( g_flProgram + 0.5 );

		float3 positionWs = i.vPositionWithOffsetWs.xyz + g_vHighPrecisionLightingOffsetWs.xyz;
		float3 world = positionWs / g_flWorldScale;
		float3 toCamera = normalize( -i.vPositionWithOffsetWs.xyz );
		float2 screenUv = i.vPositionSs.xy * g_vInvViewportSize;

		// The bump matrices: the scales times a rotation by the camera's heading plus the rotate offset.
		float angle = atan2( -g_vCameraDirWs.x, -g_vCameraDirWs.y ) + g_vBumpScales.w;
		float4 rotation = float4( cos( angle ), -sin( angle ), sin( angle ), cos( angle ) );
		float4 m1 = -g_vBumpScales.x * rotation;
		float4 m2 = g_vBumpScales.y * rotation;
		float4 m3 = -g_vBumpScales.z * rotation;

		float2 uv0 = SimpleXform( world.xy, g_vBumpXform, g_vBumpAnimUA, g_vBumpAnimUB, g_vBumpAnimVA, g_vBumpAnimVB );
		float2 d = SampleBump( uv0 );
		float2 uv3 = toCamera.xy * 0.5 + 0.5;

		float3 rgb;
		float alpha = 1;

		if ( program <= 3 )
		{
			// Pools: lerp( refraction, reflection, fresnel ). The reflection is a bitmap looked up at the screen
			// position (2D), or at the "direction" (screen position, 0) or (screen position, depth) for the cube
			// variants, as Halo did; the static ones take what's behind from their own texture at the mesh UV
			// and blend by its alpha.
			float2 reflectionUv = Bumped( screenUv, m1, d );
			float3 reflection;
			if ( program == 1 )
				reflection = SampleCubeAtlas( g_tReflection, float3( reflectionUv, 0 ) );
			else if ( program == 3 )
				reflection = SampleCubeAtlas( g_tReflection, float3( reflectionUv, i.vPositionSs.z ) );
			else
				reflection = g_tReflection.Sample( g_sClamp, reflectionUv ).rgb;

			float4 refraction;
			if ( program >= 2 )
			{
				float2 uv2 = SimpleXform( i.vTextureCoords.xy, g_vStaticXform, g_vStaticAnimUA, g_vStaticAnimUB, g_vStaticAnimVA, g_vStaticAnimVB );
				refraction = g_tStatic.Sample( g_sWrap, Bumped( uv2, m2, d ) );
			}
			else
			{
				refraction = float4( SampleRefraction( Bumped( screenUv, m2, d ) ), 1 );
			}

			float fresnel = g_tFresnel.Sample( g_sWrap, Bumped( uv3, m3, d ) ).a;
			rgb = lerp( refraction.rgb, reflection, fresnel );
			alpha = refraction.a;
		}
		else
		{
			// Open water: the reflection weighted by the fresnel bitmap's alpha, plus a sun glint off the fresnel
			// bitmap's normal (rgb), over what's behind (water_static) or the ocean's plane texture.
			float4 f = g_tFresnel.Sample( g_sWrap, Bumped( uv3, m3, d ) );
			float s = saturate( dot( g_vSunDirection, f.rgb * 2 - 1 ) );
			s *= s;
			s *= s;
			float3 sun = saturate( ( s * s - 0.5 ) * g_vSunColor );
			// Halo renders the sky mirrored in the water's plane into a screen-sized target (the view's z flipped
			// about the water height) and reads it at the screen position nudged by the bump
			// (bumpenv1, like the pools): what that pixel of the mirror shows is the sky along the camera's ray through
			// the nudged position with its z turned around. The cube is looked up along that ray.
			float2 mirrorUv = Bumped( screenUv, m1, d );
			float4 nudged = mul( g_matProjectionToWorld, float4( mirrorUv.x * 2 - 1, 1 - mirrorUv.y * 2, 1, 1 ) );
			float3 ray = normalize( nudged.xyz / nudged.w );

			// Turned around, but kept above the horizon: where the nudge reaches above the horizon on screen, the
			// mirror looks below its own, which the cube (sky layers only) doesn't hold.
			float3 reflected = float3( ray.xy, abs( ray.z ) );
			float3 c = SampleReflection( reflected ) * f.a + sun;

			if ( program == 4 )
			{
				float3 refraction = g_vRefractionColor.w > 0.5 ? g_vRefractionColor.rgb : SampleRefraction( Bumped( screenUv, m2, d ) );
				rgb = saturate( c ) + refraction * ( 1 - f.a );
			}
			else
			{
				float2 planar = float2( world.x - g_vOceanPlane.x, -world.y - g_vOceanPlane.y ) * g_vOceanPlane.zw * 0.5 + 0.5;
				float4 o = g_tStatic.Sample( g_sClamp, Bumped( planar, m2, d ) );
				rgb = saturate( c * o.a ) + o.rgb * ( 1 - f.a );

				// Alpha tested on the plane texture's alpha (the shoreline); ocean_static(_opaque) blends by it,
				// the opaque blend one by the frame buffer's alpha (the edge blend, below).
				clip( o.a - 0.002 );
				alpha = program == 5 ? o.a : 1;
			}

			// Linear fog toward the horizon colour by distance (none when the definition has no fog).
			float fog = saturate( length( i.vPositionWithOffsetWs.xyz ) / g_flWorldScale * g_vFog.x + g_vFog.y );
			rgb = lerp( rgb, g_vHorizonColor, fog );

			// The shore: Halo's edge strips fade water_static and the opaque blend ocean out where the water gets
			// shallow; here the depth of water under this pixel does.
			if ( program != 5 && g_flShoreDepth > 0 )
			{
				float3 behind = Depth::GetWorldPosition( i.vPositionSs.xy );
				alpha *= saturate( ( positionWs.z - behind.z ) / ( g_flShoreDepth * g_flWorldScale ) );
			}
		}

		return float4( SrgbGammaToLinear( saturate( rgb ) ), saturate( alpha ) );
	}
}
