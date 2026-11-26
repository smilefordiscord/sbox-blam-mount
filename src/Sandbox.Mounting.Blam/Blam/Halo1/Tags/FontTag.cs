using System;
using System.Collections.Generic;

namespace Blam.Halo1.Tags;

/// <summary>
/// One character of a font: its metrics, its pixels in the font's pixel data, and where the mount
/// puts it on the font's glyph page.
/// </summary>
/// <param name="CodePoint">The UTF-16 code unit this glyph draws.</param>
/// <param name="Advance">How far the pen moves after drawing it, in pixels.</param>
/// <param name="Width">Glyph bitmap width in pixels (0 for blank characters such as space).</param>
/// <param name="Height">Glyph bitmap height in pixels.</param>
/// <param name="OriginX">The pen position inside the glyph bitmap, from its left edge.</param>
/// <param name="OriginY">The baseline inside the glyph bitmap, from its top edge.</param>
/// <param name="PixelsOffset">Where its pixels start in the font's pixel data.</param>
/// <param name="PageX">Left edge of the glyph on <see cref="FontTag.Page"/>.</param>
/// <param name="PageY">Top edge of the glyph on <see cref="FontTag.Page"/>.</param>
public sealed record FontGlyph( char CodePoint, int Advance, int Width, int Height, int OriginX, int OriginY, int PixelsOffset, int PageX, int PageY );

/// <summary>
/// Reader for <c>font</c> tags: the HUD's and menus' text. A font isn't a bitmap: each character
/// carries its own small 8-bit coverage image in the tag's pixel data. The mount packs them onto one
/// glyph page (<see cref="Page"/>, <see cref="ReadPage"/>) so a game can draw text from one texture.
/// </summary>
/// <remarks>
/// Layout (156 bytes): flags 0x0, ascending height 0x4, descending height 0x6, leading height 0x8,
/// leading width 0xA (all int16), character tables block 0x30 (12 bytes each, a block of int16
/// character indices for 256 code points: table = code point / 256), bold/italic/condense/underline
/// font references 0x3C/0x4C/0x5C/0x6C, characters block 0x7C (20 bytes each: code point 0x0,
/// advance 0x2, bitmap width 0x4 and height 0x6, origin x 0x8 and y 0xA, hardware index 0xC,
/// pixel offset 0x10) and the pixel data 0x88 (size 0x0, pointer 0xC), one byte per pixel, rows
/// top to bottom.
/// <para>
/// Halo draws a glyph with its top left corner at (pen x - origin x, baseline - origin y) and then
/// moves the pen right by the advance. A line starts leading width in from the left of its box (or
/// ends leading width in from the right, right aligned), the first baseline is ascending height
/// below the top, and lines are ascending + descending + leading height apart. Fonts have no kerning.
/// The bold, italic, condense and underline references are other fonts used for those styles.
/// </para>
/// </remarks>
public sealed class FontTag
{
	public static readonly TagClass Class = new( "font" );

	public TagEntry Tag { get; }
	public uint Flags { get; }

	/// <summary>
	/// Pixels from the baseline up to the top of the tallest characters.
	/// </summary>
	public int AscendingHeight { get; }

	/// <summary>
	/// Pixels from the baseline down to the bottom of the lowest characters.
	/// </summary>
	public int DescendingHeight { get; }

	/// <summary>
	/// Extra space between lines, added to ascending + descending height.
	/// </summary>
	public int LeadingHeight { get; }

	/// <summary>
	/// Horizontal inset of each line from the edge of its box.
	/// </summary>
	public int LeadingWidth { get; }

	/// <summary>
	/// Distance between baselines of consecutive lines.
	/// </summary>
	public int LineHeight => AscendingHeight + DescendingHeight + LeadingHeight;

	public TagEntry Bold { get; }
	public TagEntry Italic { get; }
	public TagEntry Condense { get; }
	public TagEntry Underline { get; }

	public IReadOnlyList<FontGlyph> Characters { get; }

	/// <summary>
	/// Character indices by code point, 256 code points per table. Missing entries are -1.
	/// </summary>
	public IReadOnlyList<short[]> CharacterTables { get; }

	/// <summary>
	/// Size of the glyph page all characters are packed onto.
	/// </summary>
	public (int Width, int Height) Page { get; }

	readonly uint _pixelsAddress;
	readonly int _pixelsSize;

	public FontTag( CacheFile cache, TagEntry tag )
	{
		if ( tag.Class != Class )
			throw new ArgumentException( $"{tag} is not a font tag", nameof( tag ) );

		Tag = tag;
		var data = cache.TagData;
		Flags = data.U32( tag.Address );
		AscendingHeight = data.I16( tag.Address + 0x4 );
		DescendingHeight = data.I16( tag.Address + 0x6 );
		LeadingHeight = data.I16( tag.Address + 0x8 );
		LeadingWidth = data.I16( tag.Address + 0xA );
		Bold = cache.GetTagReference( data, tag.Address + 0x3C );
		Italic = cache.GetTagReference( data, tag.Address + 0x4C );
		Condense = cache.GetTagReference( data, tag.Address + 0x5C );
		Underline = cache.GetTagReference( data, tag.Address + 0x6C );

		var tables = new List<short[]>();
		foreach ( var table in data.Block( tag.Address + 0x30, 0xC ) )
		{
			var indices = new short[256];
			indices.AsSpan().Fill( -1 );
			var i = 0;
			foreach ( var entry in data.Block( table, 2 ) )
			{
				if ( i < indices.Length )
					indices[i] = data.I16( entry );
				i++;
			}
			tables.Add( indices );
		}
		CharacterTables = tables;

		_pixelsSize = data.I32( tag.Address + 0x88 );
		_pixelsAddress = data.U32( tag.Address + 0x88 + 0xC );

		var characters = new List<FontGlyph>();
		foreach ( var c in data.Block( tag.Address + 0x7C, 0x14 ) )
		{
			characters.Add( new FontGlyph(
				CodePoint: (char)data.U16( c ),
				Advance: data.I16( c + 0x2 ),
				Width: Math.Max( (int)data.I16( c + 0x4 ), 0 ),
				Height: Math.Max( (int)data.I16( c + 0x6 ), 0 ),
				OriginX: data.I16( c + 0x8 ),
				OriginY: data.I16( c + 0xA ),
				PixelsOffset: data.I32( c + 0x10 ),
				PageX: 0,
				PageY: 0 ) );
		}

		(Characters, Page) = Pack( characters );
	}

	/// <summary>
	/// Finds the glyph for a code point, or null if the font doesn't have it.
	/// </summary>
	public FontGlyph Find( char codePoint )
	{
		var table = codePoint >> 8;
		if ( table < CharacterTables.Count )
		{
			var index = CharacterTables[table][codePoint & 0xFF];
			return index >= 0 && index < Characters.Count ? Characters[index] : null;
		}

		foreach ( var glyph in Characters )
		{
			if ( glyph.CodePoint == codePoint )
				return glyph;
		}
		return null;
	}

	// Packs the glyphs onto the page in the tag's character order (GlyphPage.Pack).
	static (IReadOnlyList<FontGlyph>, (int, int)) Pack( List<FontGlyph> characters )
	{
		var (positions, width, height) = GlyphPage.Pack( characters.ConvertAll( c => (c.Width, c.Height) ) );
		for ( var i = 0; i < characters.Count; i++ )
			characters[i] = characters[i] with { PageX = positions[i].X, PageY = positions[i].Y };
		return (characters, (width, height));
	}

	/// <summary>
	/// Draws every glyph onto the page as RGBA8888: white, with the glyph's coverage as alpha, so a
	/// game tints text by multiplying with its color. Empty space is transparent.
	/// </summary>
	public byte[] ReadPage( CacheFile cache )
	{
		var pixels = cache.TagData.Slice( _pixelsAddress, _pixelsSize );
		var (width, height) = Page;
		var rgba = new byte[width * height * 4];

		for ( var i = 0; i < rgba.Length; i += 4 )
		{
			rgba[i] = 255;
			rgba[i + 1] = 255;
			rgba[i + 2] = 255;
		}

		foreach ( var c in Characters )
		{
			if ( c.Width == 0 || c.Height == 0 || c.PixelsOffset < 0 || c.PixelsOffset + c.Width * c.Height > pixels.Length )
				continue;

			for ( var row = 0; row < c.Height; row++ )
			for ( var column = 0; column < c.Width; column++ )
				rgba[((c.PageY + row) * width + c.PageX + column) * 4 + 3] = pixels[c.PixelsOffset + row * c.Width + column];
		}

		return rgba;
	}
}
