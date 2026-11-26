//
// Halo 2 decorators (Halo2/DecoratorModels.cs; scratch research notes, decorators.md): the grass, rocks and debris a
// structure BSP scatters over its surfaces, drawn with the decorator_tex template.
//
// Models (rocks, tufts) are placed in world space by the mount; grass cards are a quad per placement that turns about
// world up to face the camera, as MCC's decorator vertex shader turns them (side = up x -camera forward). Both shrink
// into the point they stand on as the camera moves away: full size until two thirds of their fade distance, gone at it
// (MCC's fade, 3 - k distance, k per world unit from the group's fade: 0.375, 0.1875, 0.125 for 8, 16, 24 units).
//
// Colour, in Halo's gamma space: min( 2 texture colour vertex colour, 1 ), the vertex colour being the placement's tint
// times its lightmap colour (times the light on a model's normal, worked out by the mount), drawn where the texture's
// alpha is over 0.502. Then Halo's planar and atmospheric fog, as halo2_opaque.shader.
//
// Vertex: position = full size, world; TexCoord4 = the point it stands on (world); TexCoord5 = colour (gamma);
// TexCoord6 = (fade k per s&box unit, kind: 0 model 1 card, card side offset, card up offset (s&box units)).
//

HEADER
{
	Description = "Halo 2 decorators";
}

FEATURES
{
	#include "common/features.hlsl"
}

MODES
{
	Forward();
	Depth();
}

COMMON
{
	#define S_ALPHA_TEST 1
	#define S_TRANSLUCENT 0
	#include "common/shared.hlsl"

	#define CUSTOM_MATERIAL_INPUTS
}

struct VertexInput
{
	float3 vAnchor : TEXCOORD4 < Semantic( None ); >;
	float3 vDecoratorColor : TEXCOORD5 < Semantic( None ); >;
	float4 vDecorator : TEXCOORD6 < Semantic( None ); >;
	#include "common/vertexinput.hlsl"
};

struct PixelInput
{
	float3 vDecoratorColor : TEXCOORD14;
	#include "common/pixelinput.hlsl"
};

VS
{
	#include "common/vertex.hlsl"

	PixelInput MainVs( VertexInput v )
	{
		float3 anchor = v.vAnchor;
		float fade = saturate( 3 - v.vDecorator.x * length( anchor - g_vCameraPositionWs ) );

		if ( v.vDecorator.y > 0.5 )
		{
			// A card: its side across the view, about world up.
			float3 up = float3( 0, 0, 1 );
			float3 side = cross( up, -g_vCameraDirWs );
			side = dot( side, side ) > 1e-8 ? normalize( side ) : float3( 0, 1, 0 );
			v.vPositionOs = anchor + side * ( fade * v.vDecorator.z ) + up * ( fade * v.vDecorator.w );
		}
		else
		{
			v.vPositionOs = anchor + fade * ( v.vPositionOs - anchor );
		}

		PixelInput i = ProcessVertex( v );
		i.vDecoratorColor = v.vDecoratorColor;
		return FinalizeVertex( i );
	}
}

PS
{
	#include "common/pixel.hlsl"

	RenderState( CullMode, NONE );

	SamplerState g_sWrap < Filter( Anisotropic ); AddressU( WRAP ); AddressV( WRAP ); >;

	CreateInputTexture2D( Map, Linear, 8, "None", "_color", ",0/,0/0", Default4( 1.00, 1.00, 1.00, 1.00 ) );
	Texture2D g_tMap < Channel( RGBA, Box( Map ), Linear ); OutputFormat( RGBA8888 ); SrgbRead( false ); >;

	// Halo 2's atmospheric fog, as in halo2_opaque.shader.
	float HaloFogAmount( float3 positionWs )
	{
		if ( !g_bFogEnabled || !g_bGradientFogEnabled )
			return 0;

		float depth = dot( positionWs - g_vCameraPositionWs, g_vCameraDirWs );
		float u = saturate( g_vGradientFogBiasAndScale.x + g_vGradientFogBiasAndScale.z * depth );
		return smoothstep( 0, 1, saturate( ( 16 * u - 0.5 ) / 15 ) ) * saturate( g_vGradientFogColor_Opacity.w );
	}

	float3 HaloFogColor()
	{
		float3 c = max( g_vGradientFogColor_Opacity.rgb, 0 );
		return lerp( 1.055 * pow( c, 1.0 / 2.4 ) - 0.055, c * 12.92, step( c, 0.0031308 ) );
	}

	// Halo 2's planar fog, as in halo2_opaque.shader (Halo2/PlanarFog.cs sets these on every model).
	float4 g_vHaloPlanarFogPlane < Attribute( "HaloPlanarFogPlane" ); Default4( 0.0, 0.0, 1.0, 0.0 ); >;
	float4 g_vHaloPlanarFogColor < Attribute( "HaloPlanarFogColor" ); Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float4 g_vHaloPlanarFogDepths < Attribute( "HaloPlanarFogDepths" ); Default4( 1.0, 1.0, 103.2258, 0.0 ); >;
	Texture2D g_tHaloPlanarFogGradient < Attribute( "HaloPlanarFogGradient" ); SrgbRead( false ); >;
	SamplerState g_sHaloPlanarFogGradient < Filter( Bilinear ); AddressU( CLAMP ); AddressV( CLAMP ); >;

	float HaloPlanarFogAmount( float3 positionWs )
	{
		float maxDensity = g_vHaloPlanarFogColor.w;
		if ( maxDensity <= 0 )
			return 0;

		float scale = g_vHaloPlanarFogDepths.z;
		float3 n = g_vHaloPlanarFogPlane.xyz;
		float d = g_vHaloPlanarFogPlane.w;
		float eye = saturate( ( d - dot( n, g_vCameraPositionWs / scale ) ) / g_vHaloPlanarFogDepths.y );
		if ( g_vHaloPlanarFogDepths.w > 0.5 && eye <= 0 )
			return 0;

		float below = d - dot( n, positionWs / scale );
		if ( below <= 0 && eye <= 0 )
			return 0;

		float u = saturate( dot( positionWs - g_vCameraPositionWs, g_vCameraDirWs ) / scale / g_vHaloPlanarFogDepths.x );
		float v = saturate( below / g_vHaloPlanarFogDepths.y );
		float4 h = g_tHaloPlanarFogGradient.SampleLevel( g_sHaloPlanarFogGradient, float2( u, v ), 0 );
		return saturate( h.a * ( 1 - eye ) * maxDensity + h.b * eye * maxDensity );
	}

	float4 MainPs( PixelInput i ) : SV_Target0
	{
		float4 t = saturate( g_tMap.Sample( g_sWrap, i.vTextureCoords.xy ) );
		clip( t.a > 0.50196 ? 1 : -1 );

		float3 color = min( 2 * t.rgb * saturate( i.vDecoratorColor ), 1 );

		float3 positionWs = i.vPositionWithOffsetWs.xyz + g_vHighPrecisionLightingOffsetWs.xyz;
		color = lerp( color, g_vHaloPlanarFogColor.rgb, HaloPlanarFogAmount( positionWs ) );
		float4 result = float4( SrgbGammaToLinear( lerp( color, HaloFogColor(), HaloFogAmount( positionWs ) ) ), 1 );

		if ( ToolsVis::WantsToolsVis() )
		{
			Material m = Material::From( i );
			m.Albedo = SrgbGammaToLinear( t.rgb );
			m.Normal = i.vNormalWs;
			m.TangentNormal = float3( 0, 0, 1 );
			m.Roughness = 1;
			m.Metalness = 0;
			m.AmbientOcclusion = 1;
			m.Opacity = 1;
			m.GeometricNormal = i.vNormalWs;

			LightingTerms_t lightingTerms = InitLightingTerms();
			lightingTerms.vDiffuse = SrgbGammaToLinear( saturate( 2.0 * i.vDecoratorColor ) );
			return ShadingModelStandard::DoToolsVis( result, m, lightingTerms );
		}

		return result;
	}
}
