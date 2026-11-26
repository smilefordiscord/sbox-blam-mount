//
// Halo CE's HUD bitmaps, drawn the way Halo blends them: in gamma space.
//
// Halo drew its HUD into an 8-bit gamma space frame buffer with "alpha-multiply add": the frame is
// darkened by the bitmap's alpha times the color's alpha, then the bitmap's color times the color is
// added, out = tex.rgb * color.rgb + frame * (1 - tex.a * color.a), all on gamma values. s&box's
// frame is linear, and blending there makes faint HUD colors (the motion sensor's (6, 20, 40), the
// team box's dark blue) several times fainter and the darkening stronger.
//
// So the HUD isn't blended into the frame directly. HaloHudRenderer draws every HUD quad here, into
// a layer cleared to nothing, as premultiplied gamma space color and coverage (blend ONE,
// INV_SRC_ALPHA): one Halo blend after another, out = c + frame * (1 - a), composes into one such
// pair, (C, A). halo_hud_composite.shader then lays the layer over the frame in gamma space:
// frame' = C + frame * (1 - A).
//
// Each quad is one of the HUD painter's draws:
//   - normal blend:  c = tex.rgb * color.rgb * coverage, a = coverage (coverage = tex.a * color.a);
//   - additive:      c = tex.rgb * color.rgb * coverage, a = 0.
// Halo's own blend is a normal draw of black at its alpha followed by an additive draw of the opaque
// bitmap, which compose into exactly out = tex.rgb * color.rgb + frame * (1 - tex.a * color.a).
// Bitmaps and colors are Halo's gamma space values, read as they are.
//
// What a quad samples (tex above) is its bitmap or something worked out here per texel, so nothing
// the HUD draws is generated on the CPU per frame (the draw's HaloHudMode):
//   0 the bitmap. Per vertex, "opaque" reads its alpha as 1 (the additive half of Halo's blend
//     adds the colour alone), and "region" samples a sub-rectangle (a glyph on its font page) at
//     its top mip with the uv held half a texel inside it, as the glyph's own texture clamped;
//   1 a meter: each texel of the raw meter bitmap recoloured by its gray against
//     the lit and flash thresholds, as HudMeterTexture does on the CPU;
//   2 a multitexture overlay (the sniper's pitch ladders): each texel of the overlay the primary
//     and secondary maps make, point sampled at the texel's centre, faded, combined and rounded to
//     a byte, as the CPU composite does;
//   3 the motion sensor sweep: the 64 x 64 composite of the sweep bitmap scaled about the centre,
//     times its colour and the mask, truncated to bytes as on the CPU.
// Modes 1-3 filter their texels bilinearly by hand, clamped at the edges: what sampling the CPU's
// texture (no mips) did.
//

HEADER
{
	Description = "Halo CE HUD bitmaps, accumulated in gamma space";
	DevShader = true;
}

MODES
{
	Default();
	Forward();
}

COMMON
{
	#include "system.fxc"
	#include "common.fxc"
}

// s&box's Vertex, filled by HaloHudRenderer.
struct VertexInput
{
	// Clip space (the layer covers the screen).
	float3 vPosition : POSITION < Semantic( None ); >;
	float4 vUnused0 : COLOR0 < Semantic( None ); >;
	// xy: a region's largest uv.
	float3 vRegionMax : NORMAL < Semantic( None ); >;
	// xy: the bitmap's uv, zw: a region's smallest uv.
	float4 vUv : TEXCOORD0 < Semantic( None ); >;
	// The draw's color, gamma space, straight alpha.
	float4 vColor : TEXCOORD1 < Semantic( None ); >;
	// x: 1 for an additive draw, y: 1 to read the bitmap's alpha as 1, z: 1 for a region.
	float4 vKind : TANGENT < Semantic( None ); >;
};

struct PixelInput
{
	float4 vPositionPs : SV_Position;
	float2 vUv : TEXCOORD0;
	float4 vColor : TEXCOORD1;
	float vAdditive : TEXCOORD2;
	float vOpaque : TEXCOORD3;
	float vRegion : TEXCOORD4;
	float4 vRegionBounds : TEXCOORD5;
};

VS
{
	PixelInput MainVs( VertexInput i )
	{
		PixelInput o;
		o.vPositionPs = float4( i.vPosition.xy, 0.5, 1.0 );
		o.vUv = i.vUv.xy;
		o.vColor = i.vColor;
		o.vAdditive = i.vKind.x;
		o.vOpaque = i.vKind.y;
		o.vRegion = i.vKind.z;
		o.vRegionBounds = float4( i.vUv.zw, i.vRegionMax.xy );
		return o;
	}
}

PS
{
	RenderState( DepthEnable, false );
	RenderState( DepthWriteEnable, false );
	RenderState( CullMode, NONE );
	RenderState( BlendEnable, true );
	RenderState( SrcBlend, ONE );
	RenderState( DstBlend, INV_SRC_ALPHA );
	RenderState( BlendOp, ADD );
	RenderState( SrcBlendAlpha, ONE );
	RenderState( DstBlendAlpha, INV_SRC_ALPHA );
	RenderState( BlendOpAlpha, ADD );

	SamplerState g_sHud < Filter( Trilinear ); AddressU( CLAMP ); AddressV( CLAMP ); >;

	// The bitmap: Halo's bytes, not decoded. For modes 1-3, the raw RGBA8 copy of the meter, the
	// primary map or the sweep mask, and of the secondary map or the sweep bitmap in the second.
	Texture2D g_tHud < Attribute( "HaloHudTexture" ); SrgbRead( false ); >;
	Texture2D g_tHud2 < Attribute( "HaloHudTexture2" ); SrgbRead( false ); >;

	float g_flMode < Attribute( "HaloHudMode" ); Default( 0.0 ); >;
	// The mode's parameters (HaloHudRenderer.QuadEffect).
	float4 g_vA < Attribute( "HaloHudA" ); Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float4 g_vB < Attribute( "HaloHudB" ); Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float4 g_vC < Attribute( "HaloHudC" ); Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float4 g_vD < Attribute( "HaloHudD" ); Default4( 0.0, 0.0, 0.0, 0.0 ); >;
	float4 g_vE < Attribute( "HaloHudE" ); Default4( 0.0, 0.0, 0.0, 0.0 ); >;

	int2 Size( Texture2D t )
	{
		uint w, h;
		t.GetDimensions( w, h );
		return int2( w, h );
	}

	// A raw texel's bytes (exact multiples of 1/255).
	int4 Bytes( float4 v ) { return (int4)round( v * 255.0 ); }

	// HudMeterTexture.Update's texel: clear where the mask is, else the gradient from the minimum
	// to the maximum colour below the lit threshold (a + (b - a) * gray / 255 in integers), the flash
	// colour below the flash threshold, the empty colour above, with the bitmap's alpha.
	// A: the lit and flash thresholds; B-E: the min, max, flash and empty colours as bytes.
	float4 MeterTexel( int2 p )
	{
		int4 raw = Bytes( g_tHud.Load( int3( p, 0 ) ) );
		if ( raw.a == 0 ) return 0;

		int gray = raw.r;
		int3 c;
		if ( gray < (int)g_vA.x )
		{
			int3 a = (int3)g_vB.rgb;
			int3 b = (int3)g_vC.rgb;
			c = a + ( b - a ) * gray / 255;
		}
		else if ( gray < (int)g_vA.y ) c = (int3)g_vD.rgb;
		else c = (int3)g_vE.rgb;
		return float4( c, raw.a ) / 255.0;
	}

	// The map's texel at uv, wrapped or clamped, as 0..1.
	float4 MapTexel( Texture2D t, float u, float v, bool wrap )
	{
		if ( wrap )
		{
			u -= floor( u );
			v -= floor( v );
		}
		int2 size = Size( t );
		int x = clamp( (int)( u * size.x ), 0, size.x - 1 );
		int y = clamp( (int)( v * size.y ), 0, size.y - 1 );
		return (float4)Bytes( t.Load( int3( x, y, 0 ) ) ) / 255.0;
	}

	// DrawMultitexture's texel: the maps at the texel centre's uv / scale + offset, faded, combined
	// by the zero-to-one function and rounded to a byte (ToByte).
	// A: the primary's scale (xy) and offset (zw); B: the secondary's; C: fade 0, fade 1, the
	// function (0 multiply, 1 add, 2 subtract, 3 multiply 2x), flags (1 the primary wraps, 2 the
	// secondary wraps, 4 there's a secondary); D: the overlay's size in texels.
	float4 MultitextureTexel( int2 p )
	{
		float u = ( p.x + 0.5 ) / g_vD.x;
		float v = ( p.y + 0.5 ) / g_vD.y;
		int flags = (int)g_vC.w;
		float4 a = MapTexel( g_tHud, u * g_vA.x + g_vA.z, v * g_vA.y + g_vA.w, ( flags & 1 ) != 0 ) * g_vC.x;
		float4 b = 1.0;
		if ( ( flags & 4 ) != 0 ) b = MapTexel( g_tHud2, u * g_vB.x + g_vB.z, v * g_vB.y + g_vB.w, ( flags & 2 ) != 0 ) * g_vC.y;
		int function = (int)g_vC.z;
		float4 c = a * b;
		if ( function == 1 ) c = a + b;
		else if ( function == 2 ) c = a - b;
		else if ( function == 3 ) c = ( a * b ) * 2.0;
		return (float4)clamp( (int4)( c * 255.0 + 0.5 ), 0, 255 ) / 255.0;
	}

	// HudPixels.Sample: the nearest texel, nothing outside 0..1.
	int4 BorderTexel( Texture2D t, float u, float v )
	{
		if ( u < 0 || v < 0 || u >= 1 || v >= 1 ) return 0;
		int2 size = Size( t );
		int x = clamp( (int)( u * size.x ), 0, size.x - 1 );
		int y = clamp( (int)( v * size.y ), 0, size.y - 1 );
		return Bytes( t.Load( int3( x, y, 0 ) ) );
	}

	// DrawSweep's texel: the sweep bitmap scaled by theta about the centre, times the sweep colour
	// and the mask's alpha, truncated to bytes, opaque.
	// A: x theta; B: the sweep colour; D: the composite's size in texels.
	float4 SweepTexel( int2 p )
	{
		float u = ( p.x + 0.5 ) / g_vD.x;
		float v = ( p.y + 0.5 ) / g_vD.y;
		float mask = BorderTexel( g_tHud, u, v ).a / 255.0;
		int4 s = BorderTexel( g_tHud2, 0.5 + ( u - 0.5 ) * g_vA.x, 0.5 + ( v - 0.5 ) * g_vA.x );
		float3 c = floor( ( (float3)s.rgb * g_vB.rgb ) * mask );
		return float4( c / 255.0, 1.0 );
	}

	float4 Texel( int mode, int2 p )
	{
		if ( mode == 1 ) return MeterTexel( p );
		if ( mode == 2 ) return MultitextureTexel( p );
		return SweepTexel( p );
	}

	// Bilinear filtering of a generated texture of this size, clamped at its edges.
	float4 Filtered( int mode, float2 uv, int2 size )
	{
		float2 t = uv * (float2)size - 0.5;
		float2 f = t - floor( t );
		int2 i0 = (int2)floor( t );
		int2 i1 = clamp( i0 + 1, 0, size - 1 );
		i0 = clamp( i0, 0, size - 1 );
		float4 a = Texel( mode, int2( i0.x, i0.y ) );
		float4 b = Texel( mode, int2( i1.x, i0.y ) );
		float4 c = Texel( mode, int2( i0.x, i1.y ) );
		float4 d = Texel( mode, int2( i1.x, i1.y ) );
		return lerp( lerp( a, b, f.x ), lerp( c, d, f.x ), f.y );
	}

	float4 MainPs( PixelInput i ) : SV_Target0
	{
		int mode = (int)g_flMode;
		float4 map;
		if ( mode == 1 ) map = Filtered( 1, i.vUv, Size( g_tHud ) );
		else if ( mode == 2 || mode == 3 ) map = Filtered( mode, i.vUv, (int2)g_vD.xy );
		else if ( i.vRegion > 0.5 ) map = g_tHud.SampleLevel( g_sHud, clamp( i.vUv, i.vRegionBounds.xy, i.vRegionBounds.zw ), 0 );
		else map = g_tHud.Sample( g_sHud, i.vUv );
		if ( i.vOpaque > 0.5 ) map.a = 1.0;

		float4 color = saturate( i.vColor );
		float coverage = map.a * color.a;
		float3 c = map.rgb * color.rgb * coverage;
		return float4( c, i.vAdditive > 0.5 ? 0.0 : coverage );
	}
}
