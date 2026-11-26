using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Blam.Halo2;

// One character of a Halo 2 font: its metrics and where the mount puts it on the font's glyph page.
// Halo draws it with its top left at (pen + kerning + origin x, baseline - origin y).
// Then the pen moves right by kerning + origin x + advance.
public sealed record FontCharacter( int CodePoint, int Advance, int Width, int Height, int OriginX, int OriginY, int PageX, int PageY );

// A Halo 2 font from MCC's h2_fonts folder: one file per font and size (conduit-12, handel_gothic-24...).
// The maps have no font tags; font_table.txt names the file for each of the engine's font slots.
// Glyphs are 16-bit ARGB (4 bits a channel) and can carry colour: the button, weapon and medal icons the strings use.
public sealed class FontFile
{
	// 0x200 bytes of padding come before the header.
	const int Header = 0x200;
	const uint Version = 0xF0000001;
	const int GlyphIndex = 0x400;
	const int GlyphRecordSize = 0x10;
	const int MaximumKerningPairs = 96;

	public string Name { get; }
	public int AscendingHeight { get; }
	public int DescendingHeight { get; }
	public int LeadingHeight { get; }
	public int LeadingWidth { get; }

	// Every character the font has, by code point; code points it lacks draw MissingCharacter.
	public IReadOnlyList<FontCharacter> Characters { get; }
	public FontCharacter MissingCharacter { get; }

	// Pairs of characters (both below 256) whose spacing changes: the adjustment in pixels is added to the pen between them.
	public IReadOnlyList<(int First, int Second, int Adjustment)> KerningPairs { get; }

	public (int Width, int Height) Page { get; }

	readonly byte[] _data;
	readonly (int Offset, int Size)[] _pixels;
	readonly int[] _glyphOf;
	readonly (int X, int Y)[] _pagePositions;
	readonly (int Width, int Height)[] _sizes;

	// Header: version, then ascending, descending and leading height and leading width (int16s), and the glyph count at 0xC.
	// The kerning pair count is at 0x20, the pairs at 0x24: first and second character (bytes), then the adjustment (int16).
	// A glyph index (int32) for each of the 65536 UTF-16 code units starts at 0x400, and the glyph records follow it.
	// A record is the advance, compressed size, width, height, origin x and origin y (int16s), then its pixels' file offset.
	public FontFile( string path )
	{
		Name = Path.GetFileName( path );
		_data = File.ReadAllBytes( path );
		if ( _data.Length < GlyphIndex + 0x40000 || U32( Header ) != Version )
			throw new InvalidDataException( $"'{path}' is not a Halo 2 font" );

		AscendingHeight = I16( Header + 0x4 );
		DescendingHeight = I16( Header + 0x6 );
		LeadingHeight = I16( Header + 0x8 );
		LeadingWidth = I16( Header + 0xA );

		var glyphCount = (int)U32( Header + 0xC );
		var records = GlyphIndex + 0x10000 * 4;
		if ( glyphCount <= 0 || records + glyphCount * GlyphRecordSize > _data.Length )
			throw new InvalidDataException( $"'{path}' has {glyphCount} glyphs, more than it holds" );

		var pairCount = Math.Min( (int)U32( Header + 0x20 ), MaximumKerningPairs );
		KerningPairs = Enumerable.Range( 0, pairCount )
			.Select( i => Header + 0x24 + i * 4 )
			.Select( p => ((int)_data[p], (int)_data[p + 1], (int)I16( p + 2 )) )
			.ToList();

		_sizes = new (int, int)[glyphCount];
		_pixels = new (int, int)[glyphCount];
		for ( var g = 0; g < glyphCount; g++ )
		{
			var r = records + g * GlyphRecordSize;
			_sizes[g] = (Math.Max( 0, (int)I16( r + 4 ) ), Math.Max( 0, (int)I16( r + 6 ) ));
			_pixels[g] = ((int)U32( r + 0xC ), BinaryPrimitives.ReadUInt16LittleEndian( _data.AsSpan( r + 2 ) ));
		}

		(_pagePositions, var width, var height) = GlyphPage.Pack( _sizes );
		Page = (width, height);

		FontCharacter Character( int codePoint, int g )
		{
			var r = records + g * GlyphRecordSize;
			return new FontCharacter( codePoint, I16( r ), _sizes[g].Width, _sizes[g].Height, I16( r + 8 ), I16( r + 0xA ),
				_pagePositions[g].X, _pagePositions[g].Y );
		}

		// The glyph most code points share is the one drawn for characters the font lacks.
		_glyphOf = new int[0x10000];
		var uses = new int[glyphCount];
		for ( var c = 0; c < _glyphOf.Length; c++ )
		{
			var g = (int)U32( GlyphIndex + c * 4 );
			_glyphOf[c] = g < glyphCount ? g : -1;
			if ( g < glyphCount )
				uses[g]++;
		}

		var missing = Array.IndexOf( uses, uses.Max() );
		MissingCharacter = Character( -1, missing );
		Characters = Enumerable.Range( 0, _glyphOf.Length )
			.Where( c => _glyphOf[c] >= 0 && _glyphOf[c] != missing )
			.Select( c => Character( c, _glyphOf[c] ) )
			.ToList();
	}

	// Draws every glyph onto the page as RGBA8888 (straight alpha); empty space is transparent white.
	public byte[] ReadPage()
	{
		var (width, height) = Page;
		var rgba = new byte[width * height * 4];
		for ( var i = 0; i < rgba.Length; i += 4 )
		{
			rgba[i] = 255;
			rgba[i + 1] = 255;
			rgba[i + 2] = 255;
		}

		var pixels = new List<ushort>();
		for ( var g = 0; g < _sizes.Length; g++ )
		{
			var (w, h) = _sizes[g];
			var (offset, size) = _pixels[g];
			if ( w == 0 || h == 0 || offset < 0 || offset + size > _data.Length )
				continue;

			pixels.Clear();
			Decompress( _data.AsSpan( offset, size ), pixels );
			var (x0, y0) = _pagePositions[g];
			for ( var i = 0; i < Math.Min( pixels.Count, w * h ); i++ )
			{
				var p = pixels[i];
				var o = ((y0 + i / w) * width + x0 + i % w) * 4;
				rgba[o] = (byte)(((p >> 8) & 0xF) * 17);
				rgba[o + 1] = (byte)(((p >> 4) & 0xF) * 17);
				rgba[o + 2] = (byte)((p & 0xF) * 17);
				rgba[o + 3] = (byte)((p >> 12) * 17);
			}
		}

		return rgba;
	}

	// 3-bit alphas as Halo widens them to 4 bits.
	static readonly ushort[] Alpha = [0x0, 0x3, 0x4, 0x7, 0x8, 0xB, 0xC, 0xF];

	// Runs after a 10aaabbb byte's pixel, by bbb: none, then 4, 3 or 2 opaque, then 5, 4, 3 or 2 transparent pixels.
	static readonly (int Count, ushort Alpha)[] Runs = [(0, 0), (4, 0xF), (3, 0xF), (2, 0xF), (5, 0), (4, 0), (3, 0), (2, 0)];

	// Glyph pixels, rows top to bottom, as 16-bit ARGB in the current colour (white until a pixel gives one).
	// Byte 00nnnnnn is n transparent pixels; 0 is a pixel given by the next two bytes (big endian), its rgb the new colour.
	// 01nnnnnn is n opaque pixels, 10aaabbb a pixel of alpha aaa then Runs[bbb], 11aaabbb two pixels of alpha aaa and bbb.
	public static void Decompress( ReadOnlySpan<byte> data, List<ushort> pixels )
	{
		ushort color = 0xFFF;
		for ( var i = 0; i < data.Length; i++ )
		{
			var b = data[i];
			switch ( b >> 6 )
			{
				case 0 when b == 0:
					if ( i + 2 >= data.Length )
						return;
					var given = (ushort)(data[i + 1] << 8 | data[i + 2]);
					color = (ushort)(given & 0xFFF);
					pixels.Add( given );
					i += 2;
					break;

				case 0:
				case 1:
					var alpha = b >= 0x40 ? 0xF000 : 0;
					for ( var n = 0; n < (b & 0x3F); n++ )
						pixels.Add( (ushort)(alpha | color) );
					break;

				case 2:
					pixels.Add( (ushort)(Alpha[(b >> 3) & 7] << 12 | color) );
					var (count, runAlpha) = Runs[b & 7];
					for ( var n = 0; n < count; n++ )
						pixels.Add( (ushort)(runAlpha << 12 | color) );
					break;

				default:
					pixels.Add( (ushort)(Alpha[(b >> 3) & 7] << 12 | color) );
					pixels.Add( (ushort)(Alpha[b & 7] << 12 | color) );
					break;
			}
		}
	}

	// Glyphs whose compressed pixels don't fill their width x height (or overflow it), for checks.
	public IEnumerable<int> MismatchedGlyphs()
	{
		var pixels = new List<ushort>();
		for ( var g = 0; g < _sizes.Length; g++ )
		{
			pixels.Clear();
			var (offset, size) = _pixels[g];
			if ( offset >= 0 && offset + size <= _data.Length )
				Decompress( _data.AsSpan( offset, size ), pixels );
			if ( pixels.Count != _sizes[g].Width * _sizes[g].Height )
				yield return g;
		}
	}

	// The engine's font slots (0-11) to font file names, from a font_table*.txt beside the fonts.
	public static IReadOnlyList<string> ReadTable( string path ) =>
		File.ReadAllLines( path ).Select( l => l.Trim() ).Where( l => l.Length > 0 ).ToList();

	short I16( int offset ) => BinaryPrimitives.ReadInt16LittleEndian( _data.AsSpan( offset ) );
	uint U32( int offset ) => BinaryPrimitives.ReadUInt32LittleEndian( _data.AsSpan( offset ) );
}
