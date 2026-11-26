using System.Collections.Generic;
using System.Linq;

namespace Blam.Halo2.Tags;

/// <summary>
/// Where a shader template pass takes one of its textures from: a bitmap slot of the shader, or an engine
/// texture (<see cref="Extern"/>, e.g. 11 the lightmap, 5 the frame buffer).
/// </summary>
/// <param name="Stage">The pixel shader's texture register (t0-t7).</param>
/// <param name="BitmapSlot">The shader's bitmap slot, or -1 for an engine texture.</param>
/// <param name="AddressU">D3D9 texture address mode: 1 wrap, 2 mirror, 3 clamp, 4 border.</param>
public sealed record ShaderPassTexture( int Stage, int BitmapSlot, int Extern, int AddressU, int AddressV );

/// <summary>
/// One shader parameter routed to a shader constant register.
/// </summary>
/// <param name="Source">The slot in the shader's pixel or vertex constants.</param>
/// <param name="Register">Pixel constants: the pixel shader register cN. Vertex constants: n of c[18 + n]
/// (<see cref="Bank"/> true) or of the "v-register" n fed from c[192 + n] (v10 and v11 are the fresnel pair).</param>
/// <param name="Mask">Pixel constants: the component, 0-3 x-w, 4 xyz (rgb). Vertex constants: what is written,
/// 0-3 x-w, 4 xyz, 13 a 2D "simple" transform (scale xy, offset zw), 14 and 15 the rows of a 2D affine transform
/// (u' = dot( uv, row.xy ) + row.w), see prometheus.md section 1.1.</param>
public sealed record ShaderRemap( int Source, int Register, int Mask, bool Bank );

/// <summary>
/// One pass (<c>spas</c>) of a shader template, at one of its implementations.
/// </summary>
public sealed class ShaderTemplatePass
{
	/// <summary>
	/// The pass's path below <c>shaders\shader_passes\</c>, e.g. <c>transparent\default_transparent_one_sided</c>.
	/// </summary>
	public string Name { get; init; }

	/// <summary>
	/// The vertex shader (<c>vrtx</c>) name, e.g. <c>transparent_generic_reflection</c>.
	/// </summary>
	public string VertexShader { get; init; }

	/// <summary>
	/// The template-wide implementation index this pass implementation has (what the shader's animation is keyed by).
	/// </summary>
	public int TemplateImplementation { get; init; }

	/// <summary>
	/// D3D9 render states (state, value): the implementation's defaults overridden by its own.
	/// </summary>
	public IReadOnlyDictionary<int, int> RenderStates { get; init; }

	public IReadOnlyList<ShaderPassTexture> Textures { get; init; }

	/// <summary>
	/// Per bitmap parameter of the implementation (what animated bitmap transforms target), the shader's bitmap slot.
	/// </summary>
	public IReadOnlyList<int> Bitmaps { get; init; }

	public IReadOnlyList<ShaderRemap> PixelConstants { get; init; }
	public IReadOnlyList<ShaderRemap> VertexConstants { get; init; }

	public int RenderState( int state, int fallback ) => RenderStates.TryGetValue( state, out var value ) ? value : fallback;

	// D3D9 render state numbers and values.
	public const int SourceBlendState = 19, DestinationBlendState = 20, CullModeState = 22, AlphaBlendEnableState = 27;
	public const int BlendZero = 1, BlendOne = 2, BlendSourceColor = 3, BlendSourceAlpha = 5, BlendInverseSourceAlpha = 6,
		BlendDestinationColor = 9, BlendFactor = 14;
	public const int CullNone = 1;

	public bool IsTwoSided => RenderState( CullModeState, 2 ) == CullNone;
	public int SourceBlend => RenderState( AlphaBlendEnableState, 0 ) != 0 ? RenderState( SourceBlendState, BlendOne ) : BlendOne;
	public int DestinationBlend => RenderState( AlphaBlendEnableState, 0 ) != 0 ? RenderState( DestinationBlendState, BlendZero ) : BlendZero;

	public ShaderPassTexture TextureAtStage( int stage ) => Textures.FirstOrDefault( t => t.Stage == stage );

	public override string ToString() => Name;
}

/// <summary>
/// A Halo 2 shader template (<c>stem</c>): per layer of the frame (prometheus.md section 1: 3 lightmap, 7 self
/// illumination, 15 transparent...) the passes that draw it, and for each pass how the shader's nameless
/// parameters reach its textures and constant registers. Shaders keep only their parameter values in slots; this
/// says what each slot is.
/// </summary>
public sealed class ShaderTemplateTag
{
	public static readonly TagClass Class = new( "stem" );

	public const int TransparentLayer = 15;

	/// <summary>The depth-only pass some transparent templates draw first (z_prepass: Cortana, two_add_detail_masked_prepass).</summary>
	public const int DepthPrepassLayer = 7;

	/// <summary>The overlay layer: decal-like passes blended onto the opaque surface under them (overlay, decal_simple, emblems).</summary>
	public const int OverlayLayer = 2;

	// The lightmap layer, where an opaque template lights its surface.
	public const int LightmapLayer = 3;

	// The self-illumination layer (the same as the depth prepass's): a glow-only template's one pass, added over what's drawn.
	public const int SelfIllumLayer = 7;

	public TagEntry Tag { get; }

	readonly uint _postprocess;

	public ShaderTemplateTag( TagEntry tag )
	{
		Tag = tag;
		_postprocess = tag.Cache.TagData.Elements( tag.Address + 0x58, 1 ).FirstOrDefault();
	}

	/// <summary>
	/// Each LOD's projected height threshold (fraction of the screen height): LOD i draws while the projected height
	/// is at least threshold i, else the last LOD (shader_templates_opaque.md section 3.2, bsp_lod.md).
	/// </summary>
	public IReadOnlyList<float> LodThresholds
	{
		get
		{
			if ( _postprocess == 0 )
				return [];

			var data = Tag.Cache.TagData;
			var lods = Block( data, _postprocess + 0x0 );
			return Enumerable.Range( 0, lods.Count ).Select( i => data.F32( lods.Address + (uint)i * LodSize + 6 ) ).ToList();
		}
	}

	// LOD: u16 range of layers, u32 available layer mask, f32 projected height threshold.
	const int LodSize = 0xA;

	/// <summary>
	/// The passes drawing one layer at LOD <paramref name="lod"/>, each at its implementation <paramref name="implementation"/>
	/// (0 for level geometry and most transparent passes, prometheus.md section 1 step 3).
	/// </summary>
	public IReadOnlyList<ShaderTemplatePass> Passes( int layer, int implementation = 0, int lod = 0 )
	{
		var result = new List<ShaderTemplatePass>();
		if ( _postprocess == 0 )
			return result;

		var data = Tag.Cache.TagData;
		var lods = Block( data, _postprocess + 0x0 );
		var layers = Block( data, _postprocess + 0x8 );
		var passes = Block( data, _postprocess + 0x10 );
		var implementations = Block( data, _postprocess + 0x18 );
		var remaps = Block( data, _postprocess + 0x20 );
		if ( lod >= lods.Count )
			return result;

		var (layerStart, layerCount) = Range( data.U16( lods.Address + (uint)lod * LodSize ) );
		if ( layer >= layerCount )
			return result;

		var (passStart, passCount) = Range( data.U16( layers.Address + (uint)(layerStart + layer) * 2 ) );
		for ( var p = passStart; p < passStart + passCount && p < passes.Count; p++ )
		{
			var element = passes.Address + (uint)p * 0xA;
			var spas = Tag.Cache.GetTagReference( element );
			var (implStart, implCount) = Range( data.U16( element + 8 ) );
			if ( spas is null || implementation >= implCount )
				continue;

			var templateImplementation = implStart + implementation;
			var implElement = implementations.Address + (uint)templateImplementation * 6;
			var bitmapRange = Range( data.U16( implElement ) );
			var pixelRange = Range( data.U16( implElement + 2 ) );
			var vertexRange = Range( data.U16( implElement + 4 ) );

			var pass = ReadPass( spas, implementation, templateImplementation, data, remaps.Address, bitmapRange, pixelRange, vertexRange );
			if ( pass is not null )
				result.Add( pass );
		}

		return result;
	}

	static ShaderTemplatePass ReadPass( TagEntry spas, int implementation, int templateImplementation, global::Blam.IO.MemoryRegion stemData, uint remaps,
		(int Start, int Count) bitmapRange, (int Start, int Count) pixelRange, (int Start, int Count) vertexRange )
	{
		var data = spas.Cache.TagData;
		var postprocess = data.Elements( spas.Address + 0x1C, 1 ).FirstOrDefault();
		if ( postprocess == 0 )
			return null;

		var implementations = Block( data, postprocess + 0x0 );
		var textures = Block( data, postprocess + 0x8 );
		var renderStates = Block( data, postprocess + 0x10 );
		var textureStates = Block( data, postprocess + 0x18 );
		if ( implementation >= implementations.Count )
			return null;

		var impl = implementations.Address + (uint)implementation * 0x14A;

		var states = new Dictionary<int, int>();
		void ReadStates( (int Start, int Count) range )
		{
			for ( var q = range.Start; q < range.Start + range.Count; q++ )
			{
				var e = renderStates.Address + (uint)q * 5;
				states[data.U8( e )] = data.I32( e + 1 );
			}
		}
		ReadStates( Range( data.U16( impl + 0x11C ) ) );
		ReadStates( Range( data.U16( impl + 2 ) ) );

		var passTextures = new List<ShaderPassTexture>();
		var (textureStart, textureCount) = Range( data.U16( impl ) );
		var (stateStart, stateCount) = Range( data.U16( impl + 4 ) );
		for ( var q = 0; q < textureCount; q++ )
		{
			var e = textures.Address + (uint)(textureStart + q) * 4;
			int parameter = data.U8( e ), external = data.U8( e + 1 ), stage = data.U8( e + 2 );
			var slot = -1;
			if ( parameter != 0xFF && parameter < bitmapRange.Count )
				slot = stemData.U8( remaps + (uint)(bitmapRange.Start + parameter) * 4 + 3 );

			int addressU = 1, addressV = 1;
			if ( q < stateCount )
			{
				var s = textureStates.Address + (uint)(stateStart + q) * 0x18;
				addressU = data.U8( s + 3 );
				addressV = data.U8( s + 4 );
			}

			passTextures.Add( new ShaderPassTexture( stage, slot, parameter == 0xFF ? external : -1, addressU, addressV ) );
		}

		List<ShaderRemap> ReadRemaps( (int Start, int Count) range, bool vertex )
		{
			var list = new List<ShaderRemap>();
			for ( var q = 0; q < range.Count; q++ )
			{
				var e = remaps + (uint)(range.Start + q) * 4;
				var flags = stemData.U16( e );
				var source = stemData.U8( e + 3 );
				list.Add( vertex
					? new ShaderRemap( source, flags & 0xF, (flags >> 5) & 0x1F, ((flags >> 4) & 1) != 0 )
					: new ShaderRemap( source, flags & 0x1F, (flags >> 5) & 7, false ) );
			}
			return list;
		}

		var vertexShader = data.U32( impl + 0xFC + 4 ) != 0xFFFFFFFF ? spas.Cache.GetTagReference( impl + 0xFC ) : null;

		return new ShaderTemplatePass
		{
			Name = StripPrefix( spas.Path, @"shaders\shader_passes\" ),
			VertexShader = vertexShader is null ? "" : vertexShader.Path[(vertexShader.Path.LastIndexOf( '\\' ) + 1)..],
			TemplateImplementation = templateImplementation,
			RenderStates = states,
			Textures = passTextures,
			Bitmaps = Enumerable.Range( 0, bitmapRange.Count ).Select( q => (int)stemData.U8( remaps + (uint)(bitmapRange.Start + q) * 4 + 3 ) ).ToList(),
			PixelConstants = ReadRemaps( pixelRange, false ),
			VertexConstants = ReadRemaps( vertexRange, true ),
		};
	}

	static (int Count, uint Address) Block( global::Blam.IO.MemoryRegion data, uint address ) => (data.I32( address ), data.U32( address + 4 ));

	/// <summary>
	/// A packed u16 range: low 9 bits the first index, high 7 the count.
	/// </summary>
	static (int Start, int Count) Range( ushort value ) => (value & 0x1FF, value >> 9);

	static string StripPrefix( string path, string prefix ) =>
		path.StartsWith( prefix, System.StringComparison.OrdinalIgnoreCase ) ? path[prefix.Length..] : path;
}
