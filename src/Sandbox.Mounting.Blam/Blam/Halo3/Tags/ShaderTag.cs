using System;
using System.Collections.Generic;
using System.Linq;

namespace Blam.Halo3.Tags;

// Inside the namespace so these win over s&box's global vector types when this compiles into the mount.
using Vector4 = System.Numerics.Vector4;

// One option category of a render method definition (albedo, bump_mapping...) and its option names.
public sealed record ShaderCategory( string Name, IReadOnlyList<string> Options );

// A render method definition (rmdf, e.g. shaders\shader): its categories in the order the render methods' option indices follow.
public sealed class RenderMethodDefinition
{
	public TagEntry Tag { get; }
	public IReadOnlyList<ShaderCategory> Categories { get; }

	// The definition's name without its folder: shader, terrain, foliage, halogram, decal, custom, water, cortana, particle...
	public string Kind { get; }

	RenderMethodDefinition( TagEntry tag )
	{
		Tag = tag;
		Kind = tag.Path[(tag.Path.LastIndexOf( '\\' ) + 1)..];
		var data = tag.Data;
		Categories = data.Block( 0x10, 0x18 )
			.Select( c => new ShaderCategory( c.StringId( 0 ), c.Block( 4, 0x1C ).Select( o => o.StringId( 0 ) ).ToArray() ) )
			.ToArray();
	}

	static readonly System.Runtime.CompilerServices.ConditionalWeakTable<TagEntry, RenderMethodDefinition> Cache = new();

	public static RenderMethodDefinition Get( TagEntry tag ) => tag is null ? null : Cache.GetValue( tag, t => new RenderMethodDefinition( t ) );

	public int CategoryIndex( string name )
	{
		for ( var i = 0; i < Categories.Count; i++ )
		{
			if ( Categories[i].Name == name )
				return i;
		}
		return -1;
	}
}

// A texture parameter: its bitmap, which image, sampler modes and its scale/offset transform (the real named <name>_xform, or identity).
public sealed record ShaderTexture( string Name, TagEntry Bitmap, int BitmapIndex, int AddressMode, int FilterMode, int ExternMode, Vector4 Transform );

// An animated parameter (postprocess overlay): type 0 value, 1 colour, 2 scale uniform, 3/4 scale x/y, 5/6 translation x/y, 7 frame index, 8 alpha; driven by an object input (empty = time) over a period; Function is the usual Halo function blob.
// Target is the real it overwrites (through the postprocess routing to a register and the template's routing back to a parameter), or null.
public sealed record ShaderOverlay( int Type, string Input, string Range, float Period, byte[] Function, string Target )
{
	// The overlay as a wave over time, when it is time-driven and its function is one halo3_shader evaluates (identity, constant, transition or periodic, without a range curve); null otherwise.
	public ShaderWave? TimeWave
	{
		get
		{
			if ( !string.IsNullOrEmpty( Input ) || Period <= 0 )
				return null;
			var f = new Halo3Function( Function );
			if ( f.IsEmpty || f.IsRange || f.Type is not (Halo3Function.Identity or Halo3Function.Constant or Halo3Function.Transition or Halo3Function.Periodic) )
				return null;
			// A colour function's curve is the gradient position itself; a scalar one maps it into the output range.
			var (offset, scale) = f.IsColor ? (f.CurveMinimum, f.CurveMaximum - f.CurveMinimum)
				: (f.Minimum + (f.Maximum - f.Minimum) * f.CurveMinimum, (f.Maximum - f.Minimum) * (f.CurveMaximum - f.CurveMinimum));
			return new ShaderWave( offset, scale, f.Type * 16 + (f.Shape & 15) + (f.Clamps ? 256 : 0), f.Frequency, f.Phase, Period );
		}
	}
}

// A time-driven overlay's value: Offset + Scale * curve( frac( time / Period ) ), the curve as halo3_shader's H3Wave reads it: Kind = function type * 16 + shape (+256 when clamped to 0-1), with the periodic wave's frequency and phase.
public readonly record struct ShaderWave( float Offset, float Scale, int Kind, float Frequency, float Phase, float Period )
{
	// A wave that runs from From to To once per period, as a texture scroll needs: an identity function, or a periodic slide (6, 7) or spark (11) wave with its frequency folded into the period.
	public (float From, float To, float Period)? Ramp => (Kind & 255) switch
	{
		0 => (Offset, Offset + Scale, Period),
		0x36 or 0x37 or 0x3B when Frequency > 0 => (Offset, Offset + Scale, Period / Frequency),
		_ => null,
	};
}

// A render method tag (rmsh, rmtr, rmfl, rmhg, rmd, rmcs, rmct, rmw, prt3...) with the options it picked and the parameter values its template binds.
// Values come from the postprocess block (the artist parameter block is stripped in cache maps), named by the template's (rmt2) parameter name lists.
public sealed class ShaderTag
{
	public static readonly string[] Groups = ["rmsh", "rmtr", "rmfl", "rmhg", "rmd ", "rmcs", "rmct", "rmw ", "rmlv", "rmsk", "rmb ", "prt3"];

	const int PostprocessSize = 0x8C;

	public TagEntry Tag { get; }
	public string Group { get; }
	public RenderMethodDefinition Definition { get; }

	// One option index per definition category.
	public IReadOnlyList<int> Options { get; }

	// The template (rmt2) whose name ends in the option indices joined by underscores.
	public TagEntry Template { get; }

	public IReadOnlyList<ShaderTexture> Textures { get; } = [];
	public IReadOnlyList<(string Name, Vector4 Value)> Reals { get; } = [];
	public IReadOnlyList<(string Name, int Value)> Ints { get; } = [];
	public IReadOnlyList<(string Name, bool Value)> Bools { get; } = [];

	// Animated parameters (only time-driven texture scrolls are drawn so far).
	public IReadOnlyList<ShaderOverlay> Overlays { get; } = [];
	public int OverlayCount => Overlays.Count;

	// Postprocess +0x68.
	public int BlendModeValue { get; }

	// Render method +0x34: 1 don't fog, 2 custom fog, 4 calc z camera.
	public int Flags { get; }
	public int SortLayer { get; }

	// rmsh +0x40: the global material (e.g. hard_terrain_ice), or null.
	public string GlobalMaterial { get; }

	public string Kind => Definition?.Kind ?? "";

	ShaderTag( TagEntry tag, TagView data )
	{
		Tag = tag;
		Group = tag.Class.ToString().PadRight( 4 );
		Definition = RenderMethodDefinition.Get( data.Reference( 0 ) );
		Options = data.Block( 0x10, 2 ).Select( o => (int)o.I16( 0 ) ).ToArray();
		Flags = data.U16( 0x34 );
		SortLayer = data.U8( 0x36 );
		if ( tag.Class == new TagClass( "rmsh" ) )
			GlobalMaterial = data.StringId( 0x40 );

		var postprocess = data.Block( 0x28, PostprocessSize );
		if ( postprocess.Count == 0 )
			return;

		var pp = postprocess[0];
		Template = pp.Reference( 0 );
		BlendModeValue = pp.I32( 0x68 );

		var realNames = Names( Template, 0x48 );
		var intNames = Names( Template, 0x54 );
		var boolNames = Names( Template, 0x60 );
		var textureNames = Names( Template, 0x6C );

		var reals = pp.Block( 0x1C, 0x10 );
		Reals = reals.Select( ( r, i ) => (Name( realNames, i, "real" ), new Vector4( r.F32( 0 ), r.F32( 4 ), r.F32( 8 ), r.F32( 12 ) )) ).ToArray();
		Ints = pp.Block( 0x28, 4 ).Select( ( v, i ) => (Name( intNames, i, "int" ), v.I32( 0 )) ).ToArray();
		var bits = pp.U32( 0x34 );
		Bools = boolNames.Select( ( n, i ) => (n, i < 32 && (bits >> i & 1) != 0) ).ToArray();

		Overlays = ReadOverlays( pp, Template, realNames );

		Textures = pp.Block( 0x10, 0x1C ).Select( ( t, i ) =>
		{
			var xform = t.I8( 0x16 );
			var transform = xform >= 0 && xform < Reals.Count ? Reals[xform].Value : new Vector4( 1, 1, 0, 0 );
			return new ShaderTexture( Name( textureNames, i, "texture" ), t.Reference( 0 ), t.I16( 0x10 ), t.I8( 0x12 ), t.I8( 0x13 ), t.I8( 0x15 ), transform );
		} ).ToArray();
	}

	// Overlays with the real each one lands on: postprocess entry point → pass (u16 bitmaps, vertex reals, pixel reals as count << 10 | start) → routing (u16 register, u8 overlay, u8), matched by register against the template's own routes of the same entry point (rmt2 passes: 12 ranges, vs real 2nd, ps real 5th).
	static ShaderOverlay[] ReadOverlays( TagView pp, TagEntry template, string[] realNames )
	{
		var overlays = pp.Block( 0x5C, 0x24 );
		if ( overlays.Count == 0 )
			return [];

		var targets = new string[overlays.Count];
		if ( template is not null && !template.Data.IsNull )
		{
			var t = template.Data;
			var ppEntries = pp.Block( 0x38, 2 );
			var ppPasses = pp.Block( 0x44, 6 );
			var ppRoutes = pp.Block( 0x50, 4 );
			var tEntries = t.Block( 0x24, 2 );
			var tPasses = t.Block( 0x30, 0x20 );
			var tRoutes = t.Block( 0x3C, 4 );
			for ( var e = 0; e < ppEntries.Count && e < tEntries.Count; e++ )
			{
				var (ppStart, ppCount) = Range( ppEntries[e].U16( 0 ) );
				var (tStart, tCount) = Range( tEntries[e].U16( 0 ) );
				if ( ppCount == 0 || tCount == 0 || ppStart >= ppPasses.Count || tStart >= tPasses.Count )
					continue;
				foreach ( var (ppField, tField) in new[] { (2, 2), (4, 8) } )
				{
					var (rs, rc) = Range( ppPasses[ppStart].U16( ppField ) );
					var (ts, tc) = Range( tPasses[tStart].U16( tField ) );
					for ( var r = rs; r < rs + rc && r < ppRoutes.Count; r++ )
					{
						int overlay = ppRoutes[r].U8( 2 ), register = ppRoutes[r].U16( 0 );
						if ( overlay >= targets.Length || targets[overlay] is not null )
							continue;
						for ( var k = ts; k < ts + tc && k < tRoutes.Count; k++ )
						{
							if ( tRoutes[k].U16( 0 ) == register && tRoutes[k].U8( 2 ) < realNames.Length )
								targets[overlay] = realNames[tRoutes[k].U8( 2 )];
						}
					}
				}
			}
		}

		return overlays.Select( ( o, i ) => new ShaderOverlay( (int)o.U32( 0 ), o.StringId( 4 ), o.StringId( 8 ), o.F32( 12 ), o.Data( 0x10 ).ToArray(), targets[i] ) ).ToArray();
	}

	static (int Start, int Count) Range( ushort packed ) => (packed & 0x3FF, packed >> 10);

	// The render method of a tag of one of the Groups (prt3's starts at +0x3C), or null for anything else. Read each time (cheap; not cached so nothing outlives a map's use).
	public static ShaderTag Get( TagEntry tag )
	{
		if ( tag is null || tag.Data.IsNull )
			return null;
		var group = tag.Class.ToString().PadRight( 4 );
		if ( !Groups.Contains( group ) )
			return null;
		return new ShaderTag( tag, tag.Data.At( group == "prt3" ? 0x3C : 0 ) );
	}

	// A render method embedded in another tag (e.g. each decal of a decal system, decs element +0x8).
	public static ShaderTag Get( TagEntry tag, TagView renderMethod ) => tag is null || renderMethod.IsNull ? null : new ShaderTag( tag, renderMethod );

	static string[] Names( TagEntry template, int field ) =>
		template is null || template.Data.IsNull ? [] : template.Data.Block( field, 4 ).Select( s => s.StringId( 0 ) ).ToArray();

	static string Name( string[] names, int index, string kind ) => index < names.Length && !string.IsNullOrEmpty( names[index] ) ? names[index] : $"{kind}{index}";

	// The chosen option of a category, e.g. Option( "albedo" ) = "two_detail"; null if the definition has no such category.
	public string Option( string category )
	{
		var c = Definition?.CategoryIndex( category ) ?? -1;
		if ( c < 0 || c >= Options.Count )
			return null;
		var options = Definition.Categories[c].Options;
		var o = Options[c];
		return o >= 0 && o < options.Count ? options[o] : null;
	}

	public ShaderTexture Texture( string name ) => Textures.FirstOrDefault( t => t.Name == name );

	public Vector4? Real( string name )
	{
		foreach ( var (n, v) in Reals )
		{
			if ( n == name )
				return v;
		}
		return null;
	}

	public float Scalar( string name, float fallback ) => Real( name ) is { } v ? v.X : fallback;

	public Vector4 Vector( string name, Vector4 fallback ) => Real( name ) ?? fallback;

	public bool? Bool( string name )
	{
		foreach ( var (n, v) in Bools )
		{
			if ( n == name )
				return v;
		}
		return null;
	}

	// Every category with its chosen option, "albedo=default bump_mapping=standard ...".
	public string Describe() => Definition is null ? "" : string.Join( " ", Definition.Categories.Select( ( c, i ) => $"{c.Name}={Option( c.Name )}" ) );

	public override string ToString() => $"{Tag.Path}.{Tag.Class} ({Definition?.Tag.Path})";
}

// Bitmap facts the materials need before a texture loads: whether MCC samples an image through an sRGB view.
public static class ShaderBitmaps
{
	// MCC's rule (bitmaps.md §3): formats with an sRGB DXGI variant (X8R8G8B8, A8R8G8B8, DXT1/3/5) are sRGB unless their curve is linear (3) or gamma 2.0 (2).
	public static bool IsSrgb( TagEntry bitmap, int index )
	{
		if ( bitmap is null || bitmap.Data.IsNull )
			return false;
		var image = bitmap.Data.Element( 0x60, 0x38, Math.Max( 0, index ) );
		if ( image.IsNull )
			return false;
		var format = image.I16( 0x0C );
		var curve = image.U8( 0x15 );
		return format is 10 or 11 or 14 or 15 or 16 && curve is not (2 or 3);
	}

	// The image's type: 0 2D, 2 cube, 3 array; -1 without an image.
	public static int Type( TagEntry bitmap, int index )
	{
		if ( bitmap is null || bitmap.Data.IsNull )
			return -1;
		var image = bitmap.Data.Element( 0x60, 0x38, Math.Max( 0, index ) );
		return image.IsNull ? -1 : image.I16( 0x0A );
	}

	// The image's tag format (0 A8, 14 DXT1, 16 DXT5, 22 V8U8, 25 ABGRFP16, 33 DXN...); -1 without an image.
	// Whether a bump map's pixels are a signed format (V8U8, Q8W8V8U8, V16U16, DXN and its mono alpha variant: MCC binds them as SNORM textures), so a shader reads them as x 2 - 1 after the loader's re-bias; an unsigned one (DXT5 and the rest) MCC uses as it comes.
	public static bool IsSignedBump( TagEntry bitmap, int index ) => Format( bitmap, index ) is 22 or 26 or 29 or 33 or 39;

	public static int Format( TagEntry bitmap, int index )
	{
		if ( bitmap is null || bitmap.Data.IsNull )
			return -1;
		var image = bitmap.Data.Element( 0x60, 0x38, Math.Max( 0, index ) );
		return image.IsNull ? -1 : image.I16( 0x0C );
	}
}
