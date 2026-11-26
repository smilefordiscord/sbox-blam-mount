//
// Halo CE shader_environment, the shader most level geometry uses.
//
// Halo combines a base map with two detail maps (blended by an alpha channel) and a micro detail
// map, then multiplies by the light: the lightmap tinted by the material color, plus animated
// self-illumination. A cube map reflection, bent by the bump map and masked by the diffuse maps'
// alpha, is added on top. Like the original game this all happens in gamma space, so textures
// are sampled raw and the result is converted to linear at the end.
//

HEADER
{
	Description = "Halo CE environment shader";
}

FEATURES
{
	#include "common/features.hlsl"
}

MODES
{
	Forward();
	Depth();
	ToolsShadingComplexity( "tools_shading_complexity.shader" );
}

COMMON
{
	#ifndef S_ALPHA_TEST
	#define S_ALPHA_TEST 1
	#endif
	#ifndef S_TRANSLUCENT
	#define S_TRANSLUCENT 0
	#endif

	#include "common/shared.hlsl"

	#define S_UV2 1
	#define CUSTOM_MATERIAL_INPUTS
}

struct VertexInput
{
	// The lightmap's incoming light direction (world space, length = how directional), level
	// geometry only. Zero on models, which turns bump shading off.
	float3 vIncidentLight : TEXCOORD4 < Semantic( None ); >;
	#include "common/vertexinput.hlsl"
};

struct PixelInput
{
	float3 vIncidentLightWs : TEXCOORD14;
	#include "common/pixelinput.hlsl"
};

VS
{
	#include "common/vertex.hlsl"

	PixelInput MainVs( VertexInput v )
	{
		PixelInput i = ProcessVertex( v );
		i.vIncidentLightWs = v.vIncidentLight;
		return FinalizeVertex( i );
	}
}

PS
{
	#include "common/pixel.hlsl"

	SamplerState g_sWrap < Filter( Anisotropic ); AddressU( WRAP ); AddressV( WRAP ); >;
	SamplerState g_sSelfIllumPoint < Filter( POINT ); AddressU( WRAP ); AddressV( WRAP ); >;
	SamplerState g_sLightmap < Filter( Bilinear ); AddressU( CLAMP ); AddressV( CLAMP ); >;
	SamplerState g_sClamp < Filter( Bilinear ); AddressU( CLAMP ); AddressV( CLAMP ); >;

	// Bump normals use plain trilinear filtering like the original: anisotropic filtering keeps
	// fine bumps at glancing angles, which sparkle in the reflection.
	SamplerState g_sBumpNormal < Filter( TRILINEAR ); AddressU( WRAP ); AddressV( WRAP ); >;

	CreateInputTexture2D( BaseMap, Linear, 8, "None", "_color", ",0/,0/0", Default4( 1.00, 1.00, 1.00, 1.00 ) );
	CreateInputTexture2D( PrimaryDetailMap, Linear, 8, "None", "_color", ",0/,0/0", Default4( 0.50, 0.50, 0.50, 1.00 ) );
	CreateInputTexture2D( SecondaryDetailMap, Linear, 8, "None", "_color", ",0/,0/0", Default4( 0.50, 0.50, 0.50, 1.00 ) );
	CreateInputTexture2D( MicroDetailMap, Linear, 8, "None", "_color", ",0/,0/0", Default4( 0.50, 0.50, 0.50, 1.00 ) );
	CreateInputTexture2D( BumpMap, Linear, 8, "None", "_color", ",0/,0/0", Default4( 0.50, 0.50, 1.00, 1.00 ) );
	CreateInputTexture2D( Lightmap, Linear, 8, "None", "_color", ",0/,0/0", Default4( 1.00, 1.00, 1.00, 1.00 ) );
	CreateInputTexture2D( SelfIllumMap, Linear, 8, "None", "_color", ",0/,0/0", Default4( 0.00, 0.00, 0.00, 0.00 ) );
	CreateInputTexture2D( ReflectionMap, Linear, 8, "None", "_color", ",0/,0/0", Default4( 0.00, 0.00, 0.00, 1.00 ) );
	CreateInputTexture2D( ObjectTable, Linear, 8, "None", "_color", ",0/,0/0", Default4( 0.00, 0.00, 0.00, 0.00 ) );

	Texture2D g_tBaseMap < Channel( RGBA, Box( BaseMap ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( false ); >;
	Texture2D g_tPrimaryDetailMap < Channel( RGBA, Box( PrimaryDetailMap ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( false ); >;
	Texture2D g_tSecondaryDetailMap < Channel( RGBA, Box( SecondaryDetailMap ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( false ); >;
	Texture2D g_tMicroDetailMap < Channel( RGBA, Box( MicroDetailMap ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( false ); >;
	Texture2D g_tBumpMap < Channel( RGBA, Box( BumpMap ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( false ); >;
	Texture2D g_tLightmap < Channel( RGBA, Box( Lightmap ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( false ); >;
	Texture2D g_tSelfIllumMap < Channel( RGBA, Box( SelfIllumMap ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( false ); >;
	Texture2D g_tReflectionMap < Channel( RGBA, Box( ReflectionMap ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( false ); >;
	Texture2D g_tObjectTable < Channel( RGBA, Box( ObjectTable ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( false ); >;

	float g_flPrimaryDetailScale < Default( 1.0 ); >;
	float g_flSecondaryDetailScale < Default( 1.0 ); >;
	float g_flMicroDetailScale < Default( 1.0 ); >;
	float g_flBumpScale < Default( 1.0 ); >;

	// shader_environment type: 0 normal, 1 blended, 2 blended base specular.
	float g_flShaderType < Default( 0.0 ); >;

	// Detail map functions: 0 double/biased multiply, 1 multiply, 2 double/biased add.
	float g_flDetailFunction < Default( 0.0 ); >;
	float g_flMicroDetailFunction < Default( 0.0 ); >;

	float g_flAlphaTested < Default( 0.0 ); >;
	float g_flHasLightmap < Default( 1.0 ); >;

	// On an object's model (doors, panels: Halo let models use environment shaders), there's no
	// lightmap; the object is lit like the model shader does it, from its row in the object table
	// (ObjectLightingTable), which the renderer tint indexes, else from the lighting on its renderer
	// (a model a game spawns, see MovingObjectLights). Same parameters as halo1_model.shader.
	float g_flObjectLit < Default( 0.0 ); >;
	float g_flObjectCount < Default( 0.0 ); >;
	float3 g_vFallbackAmbient < Default3( 0.5, 0.5, 0.5 ); >;
	float3 g_vFallbackLight0 < Default3( 0.0, 0.0, 0.0 ); >;
	float3 g_vFallbackToLight0 < Default3( 0.0, 0.0, 1.0 ); >;
	float3 g_vFallbackLight1 < Default3( 0.0, 0.0, 0.0 ); >;
	float3 g_vFallbackToLight1 < Default3( 0.0, 0.0, -1.0 ); >;
	float3 g_vDefaultReflectionTint < Default3( 0.5, 0.5, 0.5 ); >;
	float g_flDebugObjectLookup < Default( 0.0 ); >;
	float4 g_vHaloObjectAmbient < Attribute( "HaloObjectAmbient" ); Default4( 0.0, 0.0, 0.0, 0.0 ); >;

	// A game's scene attribute HaloLevelLightsOff (0 or 1): Halo's render_lights false, dynamic
	// lights stop lighting the level (objects drawn with this shader still take them).
	float g_flHaloLevelLightsOff < Attribute( "HaloLevelLightsOff" ); Default( 0.0 ); >;
	float3 g_vHaloObjectLight0 < Attribute( "HaloObjectLight0" ); Default3( 0.0, 0.0, 0.0 ); >;
	float3 g_vHaloObjectToLight0 < Attribute( "HaloObjectToLight0" ); Default3( 0.0, 0.0, 1.0 ); >;
	float3 g_vHaloObjectLight1 < Attribute( "HaloObjectLight1" ); Default3( 0.0, 0.0, 0.0 ); >;
	float3 g_vHaloObjectToLight1 < Attribute( "HaloObjectToLight1" ); Default3( 0.0, 0.0, -1.0 ); >;

	// Tints the lightmap only.
	float3 g_vMaterialColor < Default3( 1.0, 1.0, 1.0 ); >;

	// Texture scrolling for every map, as (function, period, phase, scale) for U and V.
	float4 g_vAnimU < Default4( 1.0, 1.0, 0.0, 0.0 ); >;
	float4 g_vAnimV < Default4( 1.0, 1.0, 0.0, 0.0 ); >;

	// Self-illumination: the map's red, green and blue mask the primary, secondary and plasma
	// effects. Each effect has an on color and an animation (function, period, phase).
	float g_flHasSelfIllum < Default( 0.0 ); >;
	float g_flSelfIllumMapScale < Default( 1.0 ); >;
	float g_flSelfIllumUnfiltered < Default( 0.0 ); >;
	float3 g_vPrimaryOnColor < Default3( 0.0, 0.0, 0.0 ); >;
	float3 g_vSecondaryOnColor < Default3( 0.0, 0.0, 0.0 ); >;
	float3 g_vPlasmaOnColor < Default3( 0.0, 0.0, 0.0 ); >;
	float4 g_vPrimaryAnim < Default4( 0.0, 1.0, 0.0, 0.0 ); >;
	float4 g_vSecondaryAnim < Default4( 0.0, 1.0, 0.0, 0.0 ); >;
	float4 g_vPlasmaAnim < Default4( 0.0, 1.0, 0.0, 0.0 ); >;

	// Cube map reflection: tint and brightness head on (perpendicular) and at glancing angles
	// (parallel). The tint picks between the plain reflection (1) and only its highlights (0).
	float g_flHasReflection < Default( 0.0 ); >;
	float g_flReflectionBumped < Default( 1.0 ); >;
	float3 g_vPerpendicularColor < Default3( 1.0, 1.0, 1.0 ); >;
	float3 g_vParallelColor < Default3( 1.0, 1.0, 1.0 ); >;
	float g_flPerpendicularBrightness < Default( 0.0 ); >;
	float g_flParallelBrightness < Default( 0.0 ); >;
	float g_flBumpIsSpecularMask < Default( 0.0 ); >;

	// The reflection fades with the lightmap's brightness, down to this fraction where the
	// lightmap is black (the shader's "lightmap brightness scale"). Negative turns it off.
	float g_flLightmapReflectionFloor < Default( 0.0 ); >;

	// Whether the bump map shades the lightmap (1) or not (0).
	float g_flBumpLighting < Default( 1.0 ); >;

	// Halo CE's atmospheric fog, from the scene's gradient fog (Halo1/ScenarioSceneLoader.cs BuildFog, which
	// stores the color linear and the maximum density raised to 2.2 for s&box's linear blending). Halo fogs
	// by view depth, linearly from nothing at the start distance to the maximum density at the opaque
	// distance (measured against MCC: amount = density x u), and blends in gamma space.
	float HaloFogAmount( float3 positionWs )
	{
		if ( !g_bFogEnabled || !g_bGradientFogEnabled )
			return 0;

		float depth = dot( positionWs - g_vCameraPositionWs, g_vCameraDirWs );
		float u = saturate( g_vGradientFogBiasAndScale.x + g_vGradientFogBiasAndScale.z * depth );
		return u * pow( saturate( g_vGradientFogColor_Opacity.w ), 1.0 / 2.2 );
	}

	// Objects' shadows on the level. Halo projected a blurred silhouette of each object along its own
	// shadow vector and multiplied the level under it by a shadow color worked out from the lightmap
	// there: clamp(1 - 1.3 lightmap + 0.5 (1 - directionality), 0.03, 1), faded in as that gets darker
	// than 0.81. Here s&box's sun shadow map gives the shape (only objects cast into it, the level's own
	// shadows are baked), and the darkness is Halo's, from the lightmap where the shadow falls: bright
	// sunlit ground darkens a lot, ground already in baked shade hardly at all. Where the baked light
	// doesn't come from the sun's side (indoors, lit by lamps) the sun's shadow doesn't apply.
	float g_flHaloObjectShadows < Attribute( "HaloObjectShadows" ); Default( 1.0 ); >;

	// The sun's shadow, as DirectionalLightShadow::GetVisibility gives it, with three changes:
	// - The cascade is the first whose shadow map square holds the point, not the first whose sphere does. s&box leaves
	//   out of each cascade the casters entirely inside a box at the previous one's center (they're in that one), but a
	//   point under such a caster can be outside the previous sphere: its shadow went missing whenever the camera moved
	//   that boundary across it. Under the box, the previous square always holds it. The point must be inside the
	//   cascade's depth range too, as the sphere made sure of: from high up, a near cascade's square falls on ground
	//   further along the sunlight than its far plane, where nothing on that ground is in its shadow map (a tank on
	//   the far side of another lost its shadow whenever the camera looked down on the two).
	// - s&box's filter is turned by a noise fixed to the screen (for temporal antialiasing), which left a hatching on the
	//   edges; here it gets a fixed screen position, so one fixed turn.
	// - Over the outer 8% of each square it blends into the next, coarser cascade (wider made shadows dip partway: two
	//   offset, differently blurred copies averaged), and the last fades out to no shadow,
	//   instead of stepping (Halo's object shadows faded out with distance too).
	static const float SunCascadeBlend = 0.08;

	float3 SunCascadePosition( uint c, float3 positionWs, out float edge )
	{
		float3 shadowPos = mul( g_DirectionalLightWorldToShadowViewMatrices[c], float4( positionWs, 1 ) ).xyz;
		edge = max( abs( shadowPos.x - 0.5 ), abs( shadowPos.y - 0.5 ) ) * 2;
		// Reversed depth: 1 at the near plane (toward the sun), 0 at the far plane. Outside that, outside the cascade.
		if ( shadowPos.z <= 0 || shadowPos.z >= 1 )
			edge = 2;
		return shadowPos;
	}

	float HaloSunVisibility( float3 positionWs, float3 normalWs, float4 positionSs, out int cascade )
	{
		cascade = -1;
		uint count = min( g_DirectionalLightCascadeCount, MAX_CASCADE_COUNT );

		[loop]
		for ( uint c = 0; c < count; c++ )
		{
			float edge;
			SunCascadePosition( c, positionWs, edge );
			if ( edge >= 0.97 )
				continue;

			float visibility = DirectionalLightShadow::SampleCascade( c, positionWs, normalWs, float2( 0, 0 ) );
			float blend = saturate( ( edge - ( 0.97 - SunCascadeBlend ) ) / SunCascadeBlend );
			if ( blend > 0 )
			{
				float next = 1;
				float nextEdge;
				if ( c + 1 < count )
				{
					SunCascadePosition( c + 1, positionWs, nextEdge );
					if ( nextEdge < 0.97 )
						next = DirectionalLightShadow::SampleCascade( c + 1, positionWs, normalWs, float2( 0, 0 ) );
				}
				visibility = lerp( visibility, next, blend );
			}
			cascade = c;
			return visibility;
		}

		return 1;
	}

	// Halo shadowed only from 0.5 bounding radii toward the sun to 4 past an object's center (render_object_shadow_end), fading through "rasterizer\linear corner fade", so shadows never fell through floors.
	StructuredBuffer<float4> g_HaloShadowCasters < Attribute( "HaloShadowCasters" ); >;
	int g_nHaloShadowCasterCount < Attribute( "HaloShadowCasterCount" ); Default( -1 ); >;

	// Full darkness before the fade, which is about 0.85 under a vehicle on open ground where MCC was measured (below).
	static const float HaloShadowStrength = 0.73;
	static const float HaloShadowTypicalReach = 0.85;

	// Where the sun is blocked from a point: the mean depth of the occluding shadow map texels over the filter's footprint, taken back toward the sun.
	bool HaloSunBlocker( uint c, float3 positionWs, out float3 blocker, out float texelWorldSize )
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

		// Reversed depth grows toward the sun, along the matrix's third row.
		float3 zGrad = worldToShadow[2].xyz;
		blocker = positionWs + zGrad * ( sum / found - shadowPos.z ) / dot( zGrad, zGrad );
		return true;
	}

	// Only a caster whose bounding sphere holds the blocker counts, so one object's shadow doesn't show through another's volume.
	float HaloShadowReach( float3 positionWs, int cascade )
	{
		if ( g_nHaloShadowCasterCount < 0 )
			return HaloShadowTypicalReach;

		float3 blocker;
		float texelWorldSize;
		if ( cascade < 0 || !HaloSunBlocker( cascade, positionWs, blocker, texelWorldSize ) )
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

			float along = dot( positionWs - caster.xyz, travel );
			if ( along < -0.5 * radius || along > 4 * radius )
				continue;

			// The fade bitmap is a 16x16 ramp of max(u, v), filtered between texel centers.
			float u = max( along / ( 4 * radius ), -along / ( 0.5 * radius ) );
			reach = max( reach, 1 - saturate( ( 16 * u - 0.5 ) / 15 ) );
		}

		return reach;
	}

	float3 HaloObjectShadow( float3 positionWs, float3 normalWs, float3 lightmap, float3 incident, float4 positionSs )
	{
		if ( g_flHaloObjectShadows < 0.5 || g_flObjectLit > 0.5 || !g_DirectionalLightEnabled )
			return 1;

		float directionality = saturate( length( incident ) );
		if ( directionality < 0.001 )
			return 1;

		float towardSun = dot( incident / length( incident ), -normalize( g_DirectionalLightDirection.xyz ) );
		float sunlit = smoothstep( 0.25, 0.75, towardSun );
		if ( sunlit <= 0 )
			return 1;

		int cascade;
		float visibility = HaloSunVisibility( positionWs, normalWs, positionSs, cascade );
		if ( visibility >= 1 )
			return 1;

		float reach = HaloShadowReach( positionWs, cascade );
		if ( reach <= 0 )
			return 1;

		// Halo's blurred silhouette (and its fade along the shadow vector) never covers fully: MCC's
		// shadows on Blood Gulch are 0.40-0.46 of the sunlit ground where this color is 0.03, so
		// only 0.62 of it applies there.
		float3 shadow = clamp( 1 - 1.3 * lightmap + 0.5 * ( 1 - directionality ), 0.03, 1 );
		float darkness = 1 - dot( shadow, float3( 0.299, 0.587, 0.114 ) );
		shadow = lerp( 1, shadow, HaloShadowStrength * reach * saturate( ( darkness - 0.19 ) * 9.0909 ) * sunlit );
		return lerp( shadow, 1, visibility );
	}

	// The fog color back in Halo's gamma space.
	float3 HaloFogColor()
	{
		float3 c = max( g_vGradientFogColor_Opacity.rgb, 0 );
		return lerp( 1.055 * pow( c, 1.0 / 2.4 ) - 0.055, c * 12.92, step( c, 0.0031308 ) );
	}

	// One texel of an object's row: 0 ambient, 1 main light, 2 direction toward it, 3 reflection
	// tint, 4 bounce light, 5 direction toward it (6-9 change colors; 9's alpha is the row's index).
	float4 ObjectTexel( int index, int texel )
	{
		// ObjectLightingTable.ObjectsPerRow and TexelsPerObject.
		return g_tObjectTable.Load( int3( ( index % 128 ) * 10 + texel, index / 128, 0 ) );
	}

	// The tint holds the row index, 8 bits per channel; each row stores its own index to check.
	bool FindObject( float3 tint, out int index )
	{
		float3 bytes = round( saturate( tint ) * 255.0 );
		index = (int)( bytes.r + bytes.g * 256.0 + bytes.b * 65536.0 + 0.5 );
		return index >= 0 && index < (int)g_flObjectCount && abs( ObjectTexel( index, 9 ).w - index ) < 0.5;
	}

	float3 ApplyDetail( float3 color, float3 detail, float function )
	{
		if ( function < 0.5 ) return saturate( color * detail * 2.0 );
		if ( function < 1.5 ) return color * detail;
		return saturate( color + 2.0 * detail - 1.0 );
	}

	float Hash( float n )
	{
		return frac( sin( n * 12.9898 ) * 43758.5453 );
	}

	float SmoothNoise( float t )
	{
		float i = floor( t );
		float f = frac( t );
		return lerp( Hash( i ), Hash( i + 1.0 ), f * f * ( 3.0 - 2.0 * f ) );
	}

	// Halo's periodic functions, 0..1 over one period. The random ones (noise, jitter, wander,
	// spark) are approximations: noise drifts smoothly, jitter flickers, wander drifts slowly,
	// spark is mostly off with short bursts.
	float Periodic( float function, float t )
	{
		if ( function < 0.5 ) return 1.0;									// one
		if ( function < 1.5 ) return 0.0;									// zero
		if ( function < 3.5 ) return 0.5 + 0.5 * cos( t * 6.2831853 );		// cosine
		if ( function < 5.5 ) return 1.0 - abs( 2.0 * frac( t ) - 1.0 );	// diagonal wave
		if ( function < 7.5 ) return frac( t );								// slide
		if ( function < 8.5 ) return SmoothNoise( t * 4.0 );				// noise
		if ( function < 9.5 ) return Hash( floor( t * 16.0 ) );				// jitter
		if ( function < 10.5 ) return SmoothNoise( t );						// wander
		return Hash( floor( t * 8.0 ) ) > 0.85 ? 1.0 : 0.0;					// spark
	}

	// anim = (function, period in seconds, phase, scale).
	float Animate( float4 anim )
	{
		return Periodic( anim.x, g_flTime / anim.y + anim.z ) * anim.w;
	}

	// The plasma effect glows where the map's alpha is close to the animation's current value,
	// so a slide animation sweeps a bright band through the alpha gradient.
	float Plasma( float reference, float value )
	{
		float closeness = saturate( 1.0 - 2.0 * abs( value - reference ) );
		float p = closeness * closeness;
		p *= p;
		float q = saturate( 2.0 * p - 1.0 );
		return q * q;
	}

	// Cube maps arrive as a 2D atlas of six faces side by side (+X, -X, +Y, -Y, +Z, -Z), and the
	// face and position within it are picked with Direct3D's cube map rules. Same lookup as
	// halo1_glass_water.shader.
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

	// Halo's dynamic lights (fires, flashing lights, glowing machinery) lit the level as well as
	// objects, on top of the lightmap. ObjectLights marks them by enabling their transmissive
	// contribution, which nothing else in a Halo level uses; the sun and the other lights are
	// already baked in. Returns Halo's own (gamma space) light value, to add to the lightmap's.
	float3 HaloDynamicLights( float3 positionWs, float3 normalWs, float4 positionSs )
	{
		float3 sum = 0;
		ClusterRange range = Cluster::Query( ClusterItemType_Light, positionSs );

		[loop]
		for ( uint n = 0; n < range.Count; n++ )
		{
			BinnedLight data = DynamicLightConstantByIndex( Cluster::LoadItem( range, n ) );
			if ( data.Type == LightType::LightTypeDirectional || !data.IsTransmissiveEnabled() )
				continue;

			Light light = {};
			light.LightData = data;
			float3 ray = data.GetPosition() - positionWs;
			float distance = length( ray );
			float3 toLight = ray / max( distance, 0.0001 );

			// Halo's dynamic lights on the level: color times gel times its distance attenuation
			// times N.L. A spot light with a gel (the flashlight) is cut off at its cone and
			// shaped by the gel alone, with no soft edge; one without keeps the cone's falloff.
			float3 color = data.Color;
			float cone = 1;
			if ( data.IsSpotLight() )
			{
				float toCone = dot( toLight, -data.GetDirection() ) - data.SpotLightInnerOuterConeCosines.y;
				if ( toCone <= 0 )
					continue;

				if ( data.HasLightCookie() )
					color *= light.GetLightCookie( positionWs );
				else
					cone = saturate( toCone * data.SpotLightInnerOuterConeCosines.z );
			}

			// Halo's distance attenuation map, (1 - x^1.65)^1.8 out to the light's radius.
			float x = saturate( distance / max( data.Radius, 0.0001 ) );
			float attenuation = pow( saturate( 1 - pow( x, 1.65 ) ), 1.8 );
			sum += color * cone * attenuation * saturate( dot( normalWs, toLight ) );
		}

		// ObjectLights scales Halo's colors up by pi for s&box's lighting.
		return sum / 3.14159265;
	}

	// Decals a game spawns from the mount's decal prefabs (bullet holes, burns, blood, see
	// DecalPrefabs). s&box draws decals inside each surface's shading, so this shader blends them
	// in itself, as Halo's rasterizer does on level geometry: light and alpha-tested decals go into
	// the light before the diffuse texture multiplies it, primary and secondary decals onto the lit
	// color, each by its framebuffer blend function. DecalPrefabs keeps a decal's layer and blend
	// function in its sort layer (layer * 8 + function), which s&box keeps in the top byte of each
	// decal's sort order. Like the rest of the shader this happens in gamma space: the decals'
	// maps and tints are Halo's own values.
	static const uint DecalLayerLight = 2;
	static const uint DecalLayerAlphaTested = 3;

	// Halo lays a decal on the surfaces within 40 degrees of the one it was made on (cos 40).
	static const float DecalSurfaceCosine = 0.76604444;

	// Halo's framebuffer blend functions.
	static const uint BlendMultiply = 1;
	static const uint BlendDoubleMultiply = 2;
	static const uint BlendAdd = 3;
	static const uint BlendSubtract = 4;
	static const uint BlendMin = 5;
	static const uint BlendMax = 6;
	static const uint BlendAlphaMultiplyAdd = 7;

	// A decal's map, color and intensity (its color mix: Halo's intensity and fade) where it covers
	// the pixel; false where it doesn't.
	bool SampleDecal( Decal decal, float3 positionWs, float3 ddxPositionWs, float3 ddyPositionWs, float3 normalWs,
		out float4 map, out float3 color, out float intensity )
	{
		map = 0;
		color = 0;
		intensity = 0;

		uint colorMap = decal.PackedTextureID.x & 0xFFFF;
		float3 p = TransformPoint( decal, positionWs );
		if ( colorMap == 0 || any( abs( p ) > 0.5 ) )
			return false;

		// The decal's X axis points into the surface it was made on.
		float3 into = RotateVector( float3( 1, 0, 0 ), float4( -decal.Quat.xyz, decal.Quat.w ) );
		if ( dot( normalWs, -into ) < DecalSurfaceCosine )
			return false;

		float2 uv = 0.5 - p.yz;
		float2 ddxUv = -( RotateVector( ddxPositionWs, decal.Quat ) * decal.Scale ).yz;
		float2 ddyUv = -( RotateVector( ddyPositionWs, decal.Quat ) * decal.Scale ).yz;

		intensity = 1;
		if ( decal.ExtraDataOffset >= 0 )
		{
			intensity = asfloat( DecalsExtraDataBuffer.Load( decal.ExtraDataOffset + 20 ) );
			SamplerState decalSampler = Bindless::GetSamplerNonUniform( DecalsExtraDataBuffer.Load( decal.ExtraDataOffset + 36 ) );
			map = Bindless::GetTexture2D( colorMap ).SampleGrad( decalSampler, uv, ddxUv, ddyUv );
		}
		else
		{
			map = Bindless::GetTexture2D( colorMap ).SampleGrad( g_sTrilinearClamp, uv, ddxUv, ddyUv );
		}

		// s&box packs the color with a shared exponent.
		float4 tint = UnpackColor( decal.ColorTint );
		uint exponent = ( decal.PackedTextureID.y >> 16 ) & 0xFF;
		color = exponent == 0 ? 0 : ( tint.rgb * 255.0 - 128.0 ) * exp2( float( exponent ) - 135.0 );
		intensity *= tint.a;
		return true;
	}

	// Halo's decal pixel shader and framebuffer blend: multiply and min take the map toward white
	// by one minus the color and the intensity, double multiply toward 50% gray, the others scale
	// the map by the color and the intensity.
	float3 BlendDecal( uint function, float3 surface, float4 map, float3 color, float intensity )
	{
		float3 scaled = map.rgb * color * intensity;
		switch ( function )
		{
			case BlendMultiply:
				return surface * lerp( 1, lerp( 1, map.rgb, color ), intensity );
			case BlendDoubleMultiply:
				return saturate( 2 * surface * lerp( 0.5, lerp( 0.5, map.rgb, color ), intensity ) );
			case BlendAdd:
				return saturate( surface + scaled );
			case BlendSubtract:
				return saturate( surface - scaled );
			case BlendMin:
				return min( surface, lerp( 1, lerp( 1, map.rgb, color ), intensity ) );
			case BlendMax:
				return max( surface, saturate( scaled ) );
			case BlendAlphaMultiplyAdd:
				return saturate( scaled + surface * ( 1 - map.a * intensity ) );
			default:
				return lerp( surface, saturate( map.rgb * color ), map.a * intensity );
		}
	}

	// The framebuffer holds 0 to 1, as Halo's does, before and after each decal.
	float3 ApplyDecals( ClusterRange range, bool lightPass, float3 surface, float3 positionWs, float3 ddxPositionWs, float3 ddyPositionWs, float3 normalWs )
	{
		surface = saturate( surface );

		[loop]
		for ( uint n = 0; n < range.Count; n++ )
		{
			Decal decal = DecalBuffer[ Cluster::LoadItem( range, n ) ];
			uint sortLayer = ( decal.SortOrder >> 24 ) & 0xFF;
			uint layer = sortLayer / 8;
			if ( ( layer == DecalLayerLight || layer == DecalLayerAlphaTested ) != lightPass )
				continue;

			float4 map;
			float3 color;
			float intensity;
			if ( SampleDecal( decal, positionWs, ddxPositionWs, ddyPositionWs, normalWs, map, color, intensity ) )
				surface = BlendDecal( sortLayer % 8, surface, map, color, intensity );
		}

		return surface;
	}

	float4 MainPs( PixelInput i ) : SV_Target0
	{
		// Scrolling moves every map together, in base map repeats.
		float2 uv = i.vTextureCoords.xy + float2( Animate( g_vAnimU ), Animate( g_vAnimV ) );

		float4 bump = Tex2DS( g_tBumpMap, g_sWrap, uv * g_flBumpScale );
		if ( g_flAlphaTested > 0.5 )
		{
			// Perforated environment shaders cut holes using the bump map's alpha.
			clip( bump.a - 0.5 );
		}

		float4 base = Tex2DS( g_tBaseMap, g_sWrap, uv );
		float4 primary = Tex2DS( g_tPrimaryDetailMap, g_sWrap, uv * g_flPrimaryDetailScale );
		float4 secondary = Tex2DS( g_tSecondaryDetailMap, g_sWrap, uv * g_flSecondaryDetailScale );
		float4 micro = Tex2DS( g_tMicroDetailMap, g_sWrap, uv * g_flMicroDetailScale );

		float blend = g_flShaderType < 0.5 ? secondary.a : base.a;
		float4 detail = lerp( secondary, primary, blend );

		float3 color = ApplyDetail( base.rgb, detail.rgb, g_flDetailFunction );
		color = ApplyDetail( color, micro.rgb, g_flMicroDetailFunction );

		// The specular mask, which the reflection is multiplied by: the blended detail maps'
		// alpha (and the base map's, when it isn't the detail blend mask), or for blended base
		// specular the base map's alpha. The micro detail map's alpha always applies.
		float specularMask;
		if ( g_flShaderType < 0.5 ) specularMask = detail.a * base.a;
		else if ( g_flShaderType < 1.5 ) specularMask = detail.a;
		else specularMask = base.a;
		specularMask *= micro.a;
		if ( g_flBumpIsSpecularMask > 0.5 )
			specularMask = bump.a;

		float3 positionWs = i.vPositionWithOffsetWs.xyz + g_vHighPrecisionLightingOffsetWs.xyz;
		float3 ddxPositionWs = ddx( positionWs );
		float3 ddyPositionWs = ddy( positionWs );
		ClusterRange decals = Cluster::Query( ClusterItemType_Decal, i.vPositionSs );

		// Halo puts decals on level geometry only, not on objects that use this shader.
		if ( g_flObjectLit > 0.5 )
			decals.Count = 0;

		float3 normalWs = normalize( i.vNormalWs );
		float3 bumpTs = normalize( Tex2DS( g_tBumpMap, g_sBumpNormal, uv * g_flBumpScale ).xyz * 2.0 - 1.0 );
		float3 bumpedWs = normalize( bumpTs.x * normalize( i.vTangentUWs ) + bumpTs.y * normalize( i.vTangentVWs ) + bumpTs.z * normalWs );

		float3 lightmap = g_flHasLightmap > 0.5 ? Tex2DS( g_tLightmap, g_sLightmap, i.vTextureCoords.zw ).rgb : float3( 1, 1, 1 );
		float3 light = lightmap * g_vMaterialColor;

		bool objectLight = g_vHaloObjectAmbient.w > 0.5;
		if ( g_flObjectLit > 0.5 && ( g_flObjectCount > 0.5 || objectLight ) )
		{
			// Lighting on the renderer wins over the tint (as in halo1_model.shader).
			int objectIndex = 0;
			bool found = FindObject( i.vVertexColor.rgb, objectIndex ) && !objectLight;
			if ( g_flDebugObjectLookup > 0.5 )
				return float4( found ? float3( 0, 1, 0 ) : objectLight ? float3( 0, 0, 1 ) : float3( 1, 0, 0 ), 1 );

			float3 ambient = found ? ObjectTexel( objectIndex, 0 ).rgb : objectLight ? g_vHaloObjectAmbient.rgb : g_vFallbackAmbient;
			float3 light0 = found ? ObjectTexel( objectIndex, 1 ).rgb : objectLight ? g_vHaloObjectLight0 : g_vFallbackLight0;
			float3 toLight0 = found ? ObjectTexel( objectIndex, 2 ).xyz : objectLight ? g_vHaloObjectToLight0 : g_vFallbackToLight0;
			float3 light1 = found ? ObjectTexel( objectIndex, 4 ).rgb : objectLight ? g_vHaloObjectLight1 : g_vFallbackLight1;
			float3 toLight1 = found ? ObjectTexel( objectIndex, 5 ).xyz : objectLight ? g_vHaloObjectToLight1 : g_vFallbackToLight1;

			// Halo's object lighting (as in halo1_model.shader), standing in for the lightmap.
			lightmap = saturate( ambient + light0 * saturate( dot( bumpedWs, toLight0 ) ) + light1 * saturate( dot( bumpedWs, toLight1 ) ) );
			light = lightmap;
		}

		// Bump mapping darkens the lightmap where the bumped normal turns away from the light's
		// baked incoming direction, as much as that light is directional. As MCC's lightmap pass
		// (env_lm_*): the directionality is the incident vector's squared length, and the facing
		// isn't clamped, so a normal turned away goes on darkening (the frame buffer clamps at 0).
		float directionality = saturate( dot( i.vIncidentLightWs, i.vIncidentLightWs ) );
		if ( directionality > 0.000001 && g_flBumpLighting > 0.5 )
		{
			float facingLight = dot( bumpedWs, normalize( i.vIncidentLightWs ) );
			light = max( light * lerp( 1.0, facingLight, directionality ), 0 );
		}

		if ( g_flHaloLevelLightsOff < 0.5 || g_flObjectLit > 0.5 )
			light += HaloDynamicLights( positionWs, bumpedWs, i.vPositionSs );

		if ( g_flHasSelfIllum > 0.5 )
		{
			float2 illumUv = uv * g_flSelfIllumMapScale;
			float4 mask = g_flSelfIllumUnfiltered > 0.5
				? Tex2DS( g_tSelfIllumMap, g_sSelfIllumPoint, illumUv )
				: Tex2DS( g_tSelfIllumMap, g_sWrap, illumUv );

			float3 glow = saturate( g_vPrimaryOnColor * Animate( g_vPrimaryAnim ) * mask.r
				+ g_vSecondaryOnColor * Animate( g_vSecondaryAnim ) * mask.g );
			glow += g_vPlasmaOnColor * Plasma( mask.a, Animate( g_vPlasmaAnim ) ) * mask.b;

			// Self-illumination lights the surface like the lightmap does.
			light += glow;
		}

		// The lit color's inputs, for the editor's view modes (albedo, lighting, normals...).
		float3 albedo = color;

		light = ApplyDecals( decals, true, saturate( light ), positionWs, ddxPositionWs, ddyPositionWs, normalWs );
		color *= light;
		color = ApplyDecals( decals, false, color, positionWs, ddxPositionWs, ddyPositionWs, normalWs );

		if ( g_flHasReflection > 0.5 )
		{
			float3 toCamera = normalize( -i.vPositionWithOffsetWs.xyz );

			// Flat cube maps reflect off the surface itself; the bump map still shapes the fresnel.
			float3 reflectNormal = g_flReflectionBumped > 0.5 ? bumpedWs : normalWs;
			float3 reflection = SampleCubeAtlas( reflect( -toCamera, reflectNormal ) );

			float facing = saturate( dot( bumpedWs, toCamera ) );
			facing *= facing;
			float3 tint = lerp( g_vParallelColor, g_vPerpendicularColor, facing );
			float brightness = lerp( g_flParallelBrightness, g_flPerpendicularBrightness, facing );

			// The tint blends from just the cube map's highlights to the full reflection.
			float3 highlights = reflection * reflection;
			highlights *= highlights;
			highlights *= highlights;
			float3 tinted = lerp( highlights, reflection, tint );

			// Dark areas reflect less: Halo masks the reflection by the lightmap's brightness
			// (weighted toward green, saturating at about two thirds of white).
			if ( g_flLightmapReflectionFloor >= 0 )
			{
				float lightmapBrightness = saturate( dot( lightmap, float3( 0.502, 0.690, 0.314 ) ) );
				brightness *= lerp( g_flLightmapReflectionFloor, 1.0, lightmapBrightness );
			}

			color = saturate( color + tinted * brightness * specularMask );
		}

		// All of Halo's level lighting is baked into the lightmap, so the result is output as is.
		// Running it through s&box's lighting would add sky reflections that wash out everything
		// seen at a grazing angle. Halo's atmospheric fog blends in gamma space, here, rather than
		// through s&box's linear one.
		color *= HaloObjectShadow( positionWs, normalWs, lightmap, i.vIncidentLightWs, i.vPositionSs );
		color = lerp( saturate( color ), HaloFogColor(), HaloFogAmount( positionWs ) );
		float4 result = float4( SrgbGammaToLinear( saturate( color ) ), 1 );

		// The editor's view modes: the lightmap stands in for the diffuse lighting.
		if ( ToolsVis::WantsToolsVis() )
		{
			Material m = Material::From( i );
			m.Albedo = SrgbGammaToLinear( saturate( albedo ) );
			m.Normal = bumpedWs;
			m.TangentNormal = bumpTs;
			m.Roughness = 1;
			m.Metalness = 0;
			m.AmbientOcclusion = 1;
			m.Opacity = 1;
			m.WorldPosition = positionWs;
			m.WorldPositionWithOffset = i.vPositionWithOffsetWs;
			m.ScreenPosition = i.vPositionSs;
			m.GeometricNormal = normalWs;
			m.WorldTangentU = i.vTangentUWs;
			m.WorldTangentV = i.vTangentVWs;

			LightingTerms_t lightingTerms = InitLightingTerms();
			lightingTerms.vDiffuse = SrgbGammaToLinear( saturate( light ) );
			return ShadingModelStandard::DoToolsVis( result, m, lightingTerms );
		}

		return result;
	}
}
