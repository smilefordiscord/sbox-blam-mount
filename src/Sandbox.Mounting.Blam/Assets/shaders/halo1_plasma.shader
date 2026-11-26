//
// Halo CE shader_transparent_plasma: the energy shell over an elite's, the cyborg's or the
// monitor's shields while they take damage, and the sentinels' shield.
//
// Two 3D noise maps are read at the model space position times their scale, each scrolled through
// its volume over time (game time / period along its animation direction). Halo's combiners then
// compare the two: the difference of their gray levels and of their alphas, squared and boosted,
// makes thin bright bands where the two fields cross, plus a white hot core where the bands are
// strongest. The color and brightness come from how much each vertex faces the camera (signed, so
// back faces get nothing): the perpendicular minus the parallel values plus the parallel ones times
// that facing, so a shell stays one color and fades out toward its silhouette rather than blending
// into the parallel color there. That's added to the frame with the intensity (its source object
// function to a power, 1 without a source). The shell is pushed out along the normals by the offset
// amount times its source function to a power (nothing without a source), which also nudges the
// primary noise.
//
// The functions are the object's A-D out (0-1), which a game sets on the renderer as HaloFunctionA-D;
// one that isn't set reads 0, and at 0 intensity the shell draws nothing. On the elite, the cyborg
// and the monitor the shell isn't geometry but the biped's modifier shader, drawn over the whole
// model: a game draws it as a second renderer of the model (bone merged onto the first) with this as
// its material override and the object's functions in its attributes. The sentinel's shield is
// geometry in its model, driven by the functions on the model's own renderer. HaloPlasmaTint
// (Vector3) replaces the tint color source (a change color) when set.
//
// The noise volumes arrive as 2D atlases of their slices (3D textures may not bind to materials
// made at runtime), sampled with wrapping and trilinear filtering by hand.
//

HEADER
{
	Description = "Halo CE plasma shader";
}

FEATURES
{
	#include "common/features.hlsl"
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

	// The object's functions, A-D out (0-1; below 0 when the game hasn't set them).
	float g_flHaloFunctionA < Attribute( "HaloFunctionA" ); Default( -1.0 ); >;
	float g_flHaloFunctionB < Attribute( "HaloFunctionB" ); Default( -1.0 ); >;
	float g_flHaloFunctionC < Attribute( "HaloFunctionC" ); Default( -1.0 ); >;
	float g_flHaloFunctionD < Attribute( "HaloFunctionD" ); Default( -1.0 ); >;

	// The functions driving the intensity and the offset: 0 none, 1-4 A-D out.
	float2 g_vPlasmaSources < Default2( 0.0, 0.0 ); >;

	// The offset: amount in s&box units, exponent.
	float2 g_vPlasmaOffset < Default2( 0.0, 1.0 ); >;

	// s&box units per world unit (HaloCeMount.WorldScale).
	float g_flPlasmaWorldScale < Default( 103.2258 ); >;

	float PlasmaFunction( float source )
	{
		float value = source < 1.5 ? g_flHaloFunctionA : source < 2.5 ? g_flHaloFunctionB : source < 3.5 ? g_flHaloFunctionC : g_flHaloFunctionD;
		return saturate( value );
	}

	// How far the shell is pushed out, in s&box units.
	float PlasmaOffset()
	{
		return g_vPlasmaSources.y > 0.5 ? pow( PlasmaFunction( g_vPlasmaSources.y ), g_vPlasmaOffset.y ) * g_vPlasmaOffset.x : 0;
	}
}

struct VertexInput
{
	#include "common/vertexinput.hlsl"
};

struct PixelInput
{
	#include "common/pixelinput.hlsl"

	// Model space position, in world units, and w = how much the vertex faces the camera (signed).
	float4 vPlasmaPositionOs : TEXCOORD14;
};

VS
{
	#include "common/vertex.hlsl"

	PixelInput MainVs( VertexInput v )
	{
		PixelInput i = ProcessVertex( v );

		// Facing from where the surface is before it's pushed out, against the skinned normal.
		float3 normalWs = normalize( i.vNormalWs );
		float facing = dot( normalize( g_vCameraPositionWs.xyz - i.vPositionWs.xyz ), normalWs );
		i.vPlasmaPositionOs = float4( v.vPositionOs.xyz / g_flPlasmaWorldScale, facing );

		float offset = PlasmaOffset();
		if ( offset > 0 )
		{
			i.vPositionWs.xyz += normalWs * offset;
			i.vPositionPs = Position3WsToPs( i.vPositionWs.xyz );
		}

		return FinalizeVertex( i );
	}
}

PS
{
	#include "common/pixel.hlsl"

	RenderState( CullMode, NONE );
	RenderState( DepthWriteEnable, false );
	RenderState( BlendEnable, true );
	RenderState( SrcBlend, ONE );
	RenderState( DstBlend, ONE );
	RenderState( BlendOp, ADD );
	RenderState( SrcBlendAlpha, ZERO );
	RenderState( DstBlendAlpha, ONE );

	CreateInputTexture2D( PrimaryNoise, Linear, 8, "None", "_color", ",0/,0/0", Default4( 0.50, 0.50, 0.50, 0.50 ) );
	CreateInputTexture2D( SecondaryNoise, Linear, 8, "None", "_color", ",0/,0/0", Default4( 0.50, 0.50, 0.50, 0.50 ) );
	Texture2D g_tPrimaryNoise < Channel( RGBA, Box( PrimaryNoise ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( false ); >;
	Texture2D g_tSecondaryNoise < Channel( RGBA, Box( SecondaryNoise ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( false ); >;

	// Each noise volume's size (width, height, depth, slices per atlas row).
	float4 g_vPrimaryNoiseSize < Default4( 1.0, 1.0, 1.0, 1.0 ); >;
	float4 g_vSecondaryNoiseSize < Default4( 1.0, 1.0, 1.0, 1.0 ); >;

	// Each noise map's animation: direction xyz (volumes per period), w = period in seconds; and scale.
	float4 g_vPrimaryAnimation < Default4( 0.0, 0.0, 0.0, 1.0 ); >;
	float4 g_vSecondaryAnimation < Default4( 0.0, 0.0, 0.0, 1.0 ); >;
	float2 g_vNoiseScale < Default2( 1.0, 1.0 ); >;

	// Color and brightness ("alpha") head on and at glancing angles, the tint (a change color of the
	// object, white without one), and the intensity's exponent.
	float4 g_vPerpendicularPlasma < Default4( 1.0, 1.0, 1.0, 1.0 ); >;
	float4 g_vParallelPlasma < Default4( 1.0, 1.0, 1.0, 1.0 ); >;
	float3 g_vPlasmaTint < Default3( 1.0, 1.0, 1.0 ); >;
	float3 g_vHaloPlasmaTint < Attribute( "HaloPlasmaTint" ); Default3( -1.0, -1.0, -1.0 ); >;
	float g_flPlasmaIntensityExponent < Default( 1.0 ); >;

	// One texel of a noise volume from its slice atlas, wrapping in all three directions.
	float4 NoiseTexel( Texture2D atlas, float4 size, int3 p )
	{
		int3 s = int3( size.xyz );
		p = ( p % s + s ) % s;
		int tilesX = (int)size.w;
		int2 texel = int2( ( p.z % tilesX ) * s.x + p.x, ( p.z / tilesX ) * s.y + p.y );
		return atlas.Load( int3( texel, 0 ) );
	}

	// Trilinear, wrapping, at uvw in volume lengths.
	float4 SampleNoise( Texture2D atlas, float4 size, float3 uvw )
	{
		float3 p = uvw * size.xyz - 0.5;
		float3 base = floor( p );
		float3 f = p - base;
		int3 b = int3( base );

		float4 c00 = lerp( NoiseTexel( atlas, size, b ), NoiseTexel( atlas, size, b + int3( 1, 0, 0 ) ), f.x );
		float4 c10 = lerp( NoiseTexel( atlas, size, b + int3( 0, 1, 0 ) ), NoiseTexel( atlas, size, b + int3( 1, 1, 0 ) ), f.x );
		float4 c01 = lerp( NoiseTexel( atlas, size, b + int3( 0, 0, 1 ) ), NoiseTexel( atlas, size, b + int3( 1, 0, 1 ) ), f.x );
		float4 c11 = lerp( NoiseTexel( atlas, size, b + int3( 0, 1, 1 ) ), NoiseTexel( atlas, size, b + int3( 1, 1, 1 ) ), f.x );
		return lerp( lerp( c00, c10, f.y ), lerp( c01, c11, f.y ), f.z );
	}

	float3 ToLinear( float3 c )
	{
		c = max( c, 0 );
		return lerp( pow( ( c + 0.055 ) / 1.055, 2.4 ), c / 12.92, step( c, 0.04045 ) );
	}

	float4 MainPs( PixelInput i ) : SV_Target0
	{
		float intensity = 1;
		if ( g_vPlasmaSources.x > 0.5 )
		{
			float value = PlasmaFunction( g_vPlasmaSources.x );
			clip( value - 1.0 / 1024.0 );
			intensity = pow( value, g_flPlasmaIntensityExponent );
		}

		// Halo also shifts the primary noise across by the offset (in world units) times the height.
		float3 positionOs = i.vPlasmaPositionOs.xyz;
		float3 primaryUvw = positionOs * g_vNoiseScale.x + g_flTime / g_vPrimaryAnimation.w * g_vPrimaryAnimation.xyz;
		primaryUvw.x += PlasmaOffset() / g_flPlasmaWorldScale * positionOs.z;
		float3 secondaryUvw = positionOs * g_vNoiseScale.y + g_flTime / g_vSecondaryAnimation.w * g_vSecondaryAnimation.xyz;
		float4 primary = SampleNoise( g_tPrimaryNoise, g_vPrimaryNoiseSize, primaryUvw );
		float4 secondary = SampleNoise( g_tSecondaryNoise, g_vSecondaryNoiseSize, secondaryUvw );

		// Halo's combiners: the two fields' gray and alpha differences (biased to 0.5), the square of
		// one or the other (picked by the gray difference's top bit) boosted 4x, and a hot core
		// where that passes one half.
		float grayDifference = saturate( primary.b - secondary.b + 0.5 );
		float alphaDifference = saturate( secondary.a - primary.a + 0.5 );
		float band = grayDifference >= 0.5 ? alphaDifference : grayDifference;
		band = saturate( 4 * band * band );
		float core = band >= 0.5 ? 0.5 * ( 2 * band - 1 ) * ( 2 * band - 1 ) : 0;

		// Perpendicular minus parallel, plus parallel times the facing: at the silhouette (facing 0)
		// only the difference is left, and behind it (negative) less still.
		float facing = i.vPlasmaPositionOs.w;
		float3 tint = g_vHaloPlasmaTint.x >= 0 ? g_vHaloPlasmaTint : g_vPlasmaTint;
		float3 color = saturate( ( g_vPerpendicularPlasma.rgb - g_vParallelPlasma.rgb ) * tint + facing * g_vParallelPlasma.rgb * tint );
		float alpha = saturate( intensity * ( g_vPerpendicularPlasma.a - g_vParallelPlasma.a + facing * g_vParallelPlasma.a ) );

		float3 added = saturate( band * color + core ) * band * band * alpha;

		// Added in Halo's gamma space frame buffer; out linear, faded by fog.
		float3 positionWs = i.vPositionWithOffsetWs.xyz + g_vHighPrecisionLightingOffsetWs.xyz;
		// Halo faded added light by 1 - its fog density in gamma space; s&box's transmittance is 1 - density^2.2 (ScenarioSceneLoader.BuildFog), far too little.
		float fade = ( 1 - pow( saturate( 1 - DoAtmospherics( positionWs, i.vPositionSs.xy, float4( 0, 0, 0, 1 ), true ).a ), 1.0 / 2.2 ) );
		return float4( ToLinear( added ) * fade, 0 );
	}
}
