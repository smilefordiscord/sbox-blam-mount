using System.Collections.Generic;
using System.Linq;

namespace Blam.Halo2.Tags;

// Inside the namespace so these win over s&box's global vector types when this compiles into the mount.
using Vector4 = System.Numerics.Vector4;

/// <summary>
/// A bitmap slot of a shader: a bitmap tag and which of its images.
/// </summary>
public sealed record ShaderBitmap( TagEntry Bitmap, int Image );

/// <summary>
/// A Halo 2 shader (<c>shad</c>): its template and its precompiled ("postprocess") parameters. Parameter
/// names are stripped in the cache; what each slot means depends on the template.
/// </summary>
public sealed class ShaderTag
{
	public static readonly TagClass Class = new( "shad" );

	const uint PostprocessBlock = 0x20;
	const int PostprocessSize = 0x7C;
	const int BitmapSize = 0xC;
	const int VertexConstantSize = 0x10;

	public TagEntry Tag { get; }

	/// <summary>
	/// The shader template (<c>stem</c>).
	/// </summary>
	public TagEntry Template { get; }

	/// <summary>
	/// The template's path below <c>shaders\shader_templates\</c>, e.g. <c>opaque\tex_bump</c>.
	/// </summary>
	public string TemplateName { get; }

	public IReadOnlyList<ShaderBitmap> Bitmaps { get; } = [];

	/// <summary>
	/// Pixel constants as stored: ARGB, one byte each.
	/// </summary>
	public IReadOnlyList<uint> PixelConstants { get; } = [];

	public IReadOnlyList<Vector4> VertexConstants { get; } = [];

	/// <summary>
	/// 0 none, 1 default, 2 dull, 3 shiny: the specular for objects lit by the object lighting.
	/// </summary>
	public int SpecularType { get; }

	/// <summary>
	/// 0 diffuse, 1 default specular, 2 dull specular, 3 shiny specular: the lightmap pass's specular.
	/// </summary>
	/// <summary>
	/// The shader's LOD bias (shad +0x3C): scales the projected height its template's LOD thresholds see (see
	/// <c>Halo2.OpaqueMaterial</c>); 5 is "never" (always LOD0).
	/// </summary>
	public int LodBias { get; }

	public int LightmapType { get; }

	public float LightmapSpecularBrightness { get; }

	public ShaderTag( TagEntry tag )
	{
		Tag = tag;
		var cache = tag.Cache;
		var data = cache.TagData;

		Template = cache.GetTagReference( tag.Address );
		TemplateName = TemplateNameOf( Template?.Path );
		SpecularType = data.U16( tag.Address + 0x3E );
		LodBias = data.U16( tag.Address + 0x3C );
		LightmapType = data.U16( tag.Address + 0x40 );
		LightmapSpecularBrightness = data.F32( tag.Address + 0x44 );

		var postprocess = data.Elements( tag.Address + PostprocessBlock, PostprocessSize ).FirstOrDefault();
		if ( postprocess == 0 )
			return;

		// Bitmaps: tag id (no group), image index, log dimension.
		Bitmaps = data.Elements( postprocess + 0x4, BitmapSize )
			.Select( e => new ShaderBitmap( cache.GetTag( data.U32( e ) ), data.I32( e + 4 ) ) )
			.ToList();
		PixelConstants = data.Elements( postprocess + 0xC, 4 ).Select( data.U32 ).ToList();
		VertexConstants = data.Elements( postprocess + 0x14, VertexConstantSize )
			.Select( e => new Vector4( data.F32( e ), data.F32( e + 4 ), data.F32( e + 8 ), data.F32( e + 12 ) ) )
			.ToList();

		// The active camouflage pass binds the bitmap this names as its bump map (extern 26).
		var camo = data.Elements( postprocess + 0x5C, 0x14 ).FirstOrDefault();
		CamoBumpBitmap = camo == 0 ? -1 : data.I16( camo + 0x10 );
	}

	/// <summary>
	/// The bitmap slot (into <see cref="Bitmaps"/>) the active camouflage pass refracts through as its bump map
	/// (postprocess +0x5C element 0, +0x10: the opaque templates' "camo bump" slot, usually 1), -1 for none (flat).
	/// </summary>
	public int CamoBumpBitmap { get; } = -1;

	ShaderTag( ShaderTag source, IReadOnlyList<ShaderBitmap> bitmaps, IReadOnlyList<uint> pixelConstants, IReadOnlyList<Vector4> vertexConstants )
	{
		Tag = source.Tag;
		Template = source.Template;
		TemplateName = source.TemplateName;
		SpecularType = source.SpecularType;
		LightmapType = source.LightmapType;
		LightmapSpecularBrightness = source.LightmapSpecularBrightness;
		CamoBumpBitmap = source.CamoBumpBitmap;
		Bitmaps = bitmaps;
		PixelConstants = pixelConstants;
		VertexConstants = vertexConstants;
	}

	/// <summary>
	/// The same shader with other parameter values (its animation evaluated, see <see cref="AnimatedShader"/>).
	/// </summary>
	public ShaderTag WithValues( IReadOnlyList<ShaderBitmap> bitmaps, IReadOnlyList<uint> pixelConstants, IReadOnlyList<Vector4> vertexConstants ) =>
		new( this, bitmaps, pixelConstants, vertexConstants );

	static string TemplateNameOf( string path )
	{
		const string prefix = @"shaders\shader_templates\";
		if ( path is null ) return "";
		return path.StartsWith( prefix, System.StringComparison.OrdinalIgnoreCase ) ? path[prefix.Length..] : path;
	}

	public ShaderBitmap GetBitmap( int slot ) => slot >= 0 && slot < Bitmaps.Count ? Bitmaps[slot] : null;
	public Vector4? GetVertexConstant( int slot ) => slot >= 0 && slot < VertexConstants.Count ? VertexConstants[slot] : null;
}
