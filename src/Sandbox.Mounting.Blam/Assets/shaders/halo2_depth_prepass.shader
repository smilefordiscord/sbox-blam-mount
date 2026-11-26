//
// Halo 2's z_prepass (transparent\cortana, two_add_detail_masked_prepass): the mesh's depth alone, drawn in the
// transparent layer before the template's colour passes, so they only draw on its nearest surface (Cortana's eyelids
// cover her eyes, her hair covers her head). The colour is blended away (ZERO / ONE) rather than masked off.
//

HEADER
{
	Description = "Halo 2 depth prepass";
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
		PixelInput i = ProcessVertex( v );
		return FinalizeVertex( i );
	}
}

PS
{
	#include "common/pixel.hlsl"

	RenderState( DepthWriteEnable, true );
	RenderState( BlendEnable, true );
	RenderState( BlendOp, ADD );
	RenderState( SrcBlend, ZERO );
	RenderState( DstBlend, ONE );
	RenderState( SrcBlendAlpha, ZERO );
	RenderState( DstBlendAlpha, ONE );

	float4 MainPs( PixelInput i ) : SV_Target0
	{
		return 0;
	}
}
