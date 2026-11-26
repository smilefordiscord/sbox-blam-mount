using System;
using System.Buffers.Binary;
using Blam.Halo1.Tags;

namespace Blam.Halo1;

/// <summary>
/// Converts Halo CE's uncompressed bitmap formats to 8-bit RGBA. Block compressed formats
/// (DXT1/3/5, BC7) normally go to the GPU as they are; they're only decoded here when they have
/// to be (DXT3, which s&amp;box doesn't load, DXT1 with transparent texels, whose alpha s&amp;box
/// drops, and sprites cut out of their pages).
/// </summary>
public static class BitmapDecoder
{
	public static bool CanDecode( BitmapFormat format ) => format is
		BitmapFormat.A8 or BitmapFormat.Y8 or BitmapFormat.AY8 or BitmapFormat.A8Y8 or
		BitmapFormat.R5G6B5 or BitmapFormat.A1R5G5B5 or BitmapFormat.A4R4G4B4 or
		BitmapFormat.X8R8G8B8 or BitmapFormat.A8R8G8B8 or BitmapFormat.P8Bump;

	/// <summary>
	/// Decodes <paramref name="pixelCount"/> pixels from <paramref name="source"/> into RGBA8888.
	/// </summary>
	public static void ToRgba8888( BitmapFormat format, ReadOnlySpan<byte> source, Span<byte> destination, int pixelCount )
	{
		for ( var i = 0; i < pixelCount; i++ )
		{
			var output = destination.Slice( i * 4, 4 );

			switch ( format )
			{
				case BitmapFormat.A8:
					Set( output, 255, 255, 255, source[i] );
					break;

				case BitmapFormat.Y8:
					Set( output, source[i], source[i], source[i], 255 );
					break;

				case BitmapFormat.AY8:
					Set( output, source[i], source[i], source[i], source[i] );
					break;

				case BitmapFormat.A8Y8:
					{
						var y = source[i * 2];
						Set( output, y, y, y, source[i * 2 + 1] );
						break;
					}

				case BitmapFormat.R5G6B5:
					{
						var p = BinaryPrimitives.ReadUInt16LittleEndian( source[(i * 2)..] );
						Set( output, Expand( p >> 11, 5 ), Expand( p >> 5, 6 ), Expand( p, 5 ), 255 );
						break;
					}

				case BitmapFormat.A1R5G5B5:
					{
						var p = BinaryPrimitives.ReadUInt16LittleEndian( source[(i * 2)..] );
						Set( output, Expand( p >> 10, 5 ), Expand( p >> 5, 5 ), Expand( p, 5 ), (p & 0x8000) != 0 ? (byte)255 : (byte)0 );
						break;
					}

				case BitmapFormat.A4R4G4B4:
					{
						var p = BinaryPrimitives.ReadUInt16LittleEndian( source[(i * 2)..] );
						Set( output, Expand( p >> 8, 4 ), Expand( p >> 4, 4 ), Expand( p, 4 ), Expand( p >> 12, 4 ) );
						break;
					}

				case BitmapFormat.X8R8G8B8:
					Set( output, source[i * 4 + 2], source[i * 4 + 1], source[i * 4], 255 );
					break;

				case BitmapFormat.A8R8G8B8:
					Set( output, source[i * 4 + 2], source[i * 4 + 1], source[i * 4], source[i * 4 + 3] );
					break;

				case BitmapFormat.P8Bump:
					{
						// Palettized bump maps index a fixed normal palette built into the engine
						// (see BumpPalette); without it the normal is flat. Index 255 is the
						// transparent entry: alpha-tested shaders cut holes (foliage, grates) with it.
						var palette = BumpPalette.Rgba;
						var alpha = source[i] == 255 ? (byte)0 : (byte)255;
						if ( palette is null )
							Set( output, 128, 128, 255, alpha );
						else
							Set( output, palette[source[i] * 4], palette[source[i] * 4 + 1], palette[source[i] * 4 + 2], alpha );
						break;
					}

				default:
					throw new NotSupportedException( $"Can't decode bitmap format {format}" );
			}
		}
	}

	/// <summary>
	/// True if <see cref="DecodeImage"/> can decode the format: the uncompressed ones and DXT1/3/5.
	/// </summary>
	public static bool CanDecodeImage( BitmapFormat format ) =>
		CanDecode( format ) || format is BitmapFormat.Dxt1 or BitmapFormat.Dxt3 or BitmapFormat.Dxt5;

	/// <summary>
	/// Decodes one <paramref name="width"/> x <paramref name="height"/> image (one mip level of one
	/// face) of any format <see cref="CanDecodeImage"/> accepts into RGBA8888.
	/// </summary>
	public static void DecodeImage( BitmapFormat format, ReadOnlySpan<byte> source, Span<byte> destination, int width, int height )
	{
		switch ( format )
		{
			case BitmapFormat.Dxt1:
				Dxt1ToRgba8888( source, destination, width, height );
				break;

			case BitmapFormat.Dxt3:
				Dxt3ToRgba8888( source, destination, width, height );
				break;

			case BitmapFormat.Dxt5:
				Dxt5ToRgba8888( source, destination, width, height );
				break;

			default:
				ToRgba8888( format, source, destination, width * height );
				break;
		}
	}

	/// <summary>
	/// Whether DXT1 data has transparent texels: index 3 of a three-color block (first color not
	/// greater than the second).
	/// </summary>
	public static bool Dxt1HasTransparency( ReadOnlySpan<byte> source )
	{
		for ( var block = 0; block + 8 <= source.Length; block += 8 )
		{
			if ( BinaryPrimitives.ReadUInt16LittleEndian( source[block..] ) > BinaryPrimitives.ReadUInt16LittleEndian( source[(block + 2)..] ) )
				continue;

			var indices = BinaryPrimitives.ReadUInt32LittleEndian( source[(block + 4)..] );
			for ( var i = 0; i < 16; i++ )
			{
				if ( (indices >> (i * 2) & 3) == 3 )
					return true;
			}
		}

		return false;
	}

	/// <summary>
	/// Decodes DXT1 (BC1) data into RGBA8888, including its one-bit alpha: in three-color blocks
	/// (first color not greater than the second) the fourth index is transparent black.
	/// </summary>
	public static void Dxt1ToRgba8888( ReadOnlySpan<byte> source, Span<byte> destination, int width, int height )
	{
		DxtColorToRgba8888( BitmapFormat.Dxt1, source, destination, width, height );

		var blocksWide = (width + 3) / 4;
		var blocksHigh = (height + 3) / 4;
		for ( var by = 0; by < blocksHigh; by++ )
		{
			for ( var bx = 0; bx < blocksWide; bx++ )
			{
				var block = source.Slice( (by * blocksWide + bx) * 8, 8 );
				if ( BinaryPrimitives.ReadUInt16LittleEndian( block ) > BinaryPrimitives.ReadUInt16LittleEndian( block[2..] ) )
					continue;

				var indices = BinaryPrimitives.ReadUInt32LittleEndian( block[4..] );
				for ( var i = 0; i < 16; i++ )
				{
					var x = bx * 4 + (i & 3);
					var y = by * 4 + (i >> 2);
					if ( x < width && y < height && (indices >> (i * 2) & 3) == 3 )
						destination[(y * width + x) * 4 + 3] = 0;
				}
			}
		}
	}

	/// <summary>
	/// Decodes DXT5 (BC3) data into RGBA8888. Each 16 byte block starts with two 8-bit alpha
	/// endpoints and 48 bits of 3-bit indices: eight interpolated alphas if the first endpoint is
	/// greater, otherwise six plus 0 and 255. Then a four-color DXT1 style color block.
	/// </summary>
	public static void Dxt5ToRgba8888( ReadOnlySpan<byte> source, Span<byte> destination, int width, int height )
	{
		DxtColorToRgba8888( BitmapFormat.Dxt5, source, destination, width, height );

		var blocksWide = (width + 3) / 4;
		var blocksHigh = (height + 3) / 4;
		Span<byte> alphas = stackalloc byte[8];

		for ( var by = 0; by < blocksHigh; by++ )
		{
			for ( var bx = 0; bx < blocksWide; bx++ )
			{
				var block = source.Slice( (by * blocksWide + bx) * 16, 8 );
				int a0 = block[0], a1 = block[1];
				alphas[0] = (byte)a0;
				alphas[1] = (byte)a1;
				if ( a0 > a1 )
				{
					for ( var i = 1; i < 7; i++ )
						alphas[i + 1] = (byte)(((7 - i) * a0 + i * a1) / 7);
				}
				else
				{
					for ( var i = 1; i < 5; i++ )
						alphas[i + 1] = (byte)(((5 - i) * a0 + i * a1) / 5);
					alphas[6] = 0;
					alphas[7] = 255;
				}

				ulong bits = 0;
				for ( var i = 0; i < 6; i++ )
					bits |= (ulong)block[2 + i] << (8 * i);

				for ( var i = 0; i < 16; i++ )
				{
					var x = bx * 4 + (i & 3);
					var y = by * 4 + (i >> 2);
					if ( x < width && y < height )
						destination[(y * width + x) * 4 + 3] = alphas[(int)(bits >> (i * 3) & 7)];
				}
			}
		}
	}

	/// <summary>
	/// Decodes DXT3 (BC2) data for a <paramref name="width"/> x <paramref name="height"/> image into RGBA8888.
	/// Each 16 byte block covers 4x4 pixels: 64 bits of explicit 4-bit alpha, then a DXT1 style
	/// color block that always uses four-color mode.
	/// </summary>
	public static void Dxt3ToRgba8888( ReadOnlySpan<byte> source, Span<byte> destination, int width, int height )
	{
		var blocksWide = (width + 3) / 4;
		var blocksHigh = (height + 3) / 4;
		Span<byte> palette = stackalloc byte[16];

		for ( var by = 0; by < blocksHigh; by++ )
		{
			for ( var bx = 0; bx < blocksWide; bx++ )
			{
				var block = source.Slice( (by * blocksWide + bx) * 16, 16 );
				var alpha = BinaryPrimitives.ReadUInt64LittleEndian( block );
				var c0 = BinaryPrimitives.ReadUInt16LittleEndian( block[8..] );
				var c1 = BinaryPrimitives.ReadUInt16LittleEndian( block[10..] );
				var indices = BinaryPrimitives.ReadUInt32LittleEndian( block[12..] );

				Unpack565( c0, palette[0..] );
				Unpack565( c1, palette[4..] );
				for ( var c = 0; c < 3; c++ )
				{
					palette[8 + c] = (byte)((2 * palette[c] + palette[4 + c]) / 3);
					palette[12 + c] = (byte)((palette[c] + 2 * palette[4 + c]) / 3);
				}

				for ( var i = 0; i < 16; i++ )
				{
					var x = bx * 4 + (i & 3);
					var y = by * 4 + (i >> 2);
					if ( x >= width || y >= height ) continue;

					var color = (int)(indices >> (i * 2) & 3) * 4;
					var a = (byte)((alpha >> (i * 4) & 0xF) * 17);
					Set( destination.Slice( (y * width + x) * 4, 4 ), palette[color], palette[color + 1], palette[color + 2], a );
				}
			}
		}
	}

	/// <summary>
	/// Decodes the color of DXT1, DXT3 or DXT5 data into RGBA8888 with alpha 255. DXT1 blocks whose
	/// first color isn't greater than the second use three-color mode (the fourth index is black);
	/// DXT3 and DXT5 color blocks always use four colors.
	/// </summary>
	public static void DxtColorToRgba8888( BitmapFormat format, ReadOnlySpan<byte> source, Span<byte> destination, int width, int height )
	{
		var blockSize = format == BitmapFormat.Dxt1 ? 8 : 16;
		var colorOffset = format == BitmapFormat.Dxt1 ? 0 : 8;
		var blocksWide = (width + 3) / 4;
		var blocksHigh = (height + 3) / 4;
		Span<byte> palette = stackalloc byte[16];

		for ( var by = 0; by < blocksHigh; by++ )
		{
			for ( var bx = 0; bx < blocksWide; bx++ )
			{
				var block = source.Slice( (by * blocksWide + bx) * blockSize + colorOffset, 8 );
				var c0 = BinaryPrimitives.ReadUInt16LittleEndian( block );
				var c1 = BinaryPrimitives.ReadUInt16LittleEndian( block[2..] );
				var indices = BinaryPrimitives.ReadUInt32LittleEndian( block[4..] );

				Unpack565( c0, palette[0..] );
				Unpack565( c1, palette[4..] );
				var fourColors = format != BitmapFormat.Dxt1 || c0 > c1;
				for ( var c = 0; c < 3; c++ )
				{
					if ( fourColors )
					{
						palette[8 + c] = (byte)((2 * palette[c] + palette[4 + c]) / 3);
						palette[12 + c] = (byte)((palette[c] + 2 * palette[4 + c]) / 3);
					}
					else
					{
						palette[8 + c] = (byte)((palette[c] + palette[4 + c]) / 2);
						palette[12 + c] = 0;
					}
				}

				for ( var i = 0; i < 16; i++ )
				{
					var x = bx * 4 + (i & 3);
					var y = by * 4 + (i >> 2);
					if ( x >= width || y >= height ) continue;

					var color = (int)(indices >> (i * 2) & 3) * 4;
					Set( destination.Slice( (y * width + x) * 4, 4 ), palette[color], palette[color + 1], palette[color + 2], 255 );
				}
			}
		}
	}

	/// <summary>
	/// Decodes the top mip level of a 2D bitmap, whatever its format (except BC7), into RGBA8888,
	/// alpha included: DXT1's transparent texels (three-color mode, index 3) get alpha 0, DXT5's
	/// interpolated alpha is decoded. For CPU work on a bitmap, such as cutting sprites out of a sheet.
	/// </summary>
	/// <returns>Width x height x 4 bytes, or null if the format can't be decoded.</returns>
	public static byte[] DecodeTopLevel( BitmapData bitmap, ReadOnlySpan<byte> pixels )
	{
		int width = bitmap.Width, height = bitmap.Height;
		var rgba = new byte[width * height * 4];

		switch ( bitmap.Format )
		{
			case BitmapFormat.Dxt3:
				Dxt3ToRgba8888( pixels, rgba, width, height );
				return rgba;

			case BitmapFormat.Dxt1:
			case BitmapFormat.Dxt5:
				DxtColorToRgba8888( bitmap.Format, pixels, rgba, width, height );
				DxtAlpha( bitmap.Format, pixels, rgba, width, height );
				return rgba;

			default:
				if ( !CanDecode( bitmap.Format ) )
					return null;

				ToRgba8888( bitmap.Format, pixels, rgba, width * height );
				return rgba;
		}
	}

	/// <summary>
	/// Fills in the alpha of DXT1 (1-bit: index 3 of a three-color block) or DXT5 (two 8-bit
	/// endpoints and 3-bit indices: eight interpolated values when the first endpoint is larger,
	/// otherwise six plus 0 and 255) over colors already decoded.
	/// </summary>
	static void DxtAlpha( BitmapFormat format, ReadOnlySpan<byte> source, Span<byte> destination, int width, int height )
	{
		var blockSize = format == BitmapFormat.Dxt1 ? 8 : 16;
		var blocksWide = (width + 3) / 4;
		var blocksHigh = (height + 3) / 4;
		Span<byte> alphas = stackalloc byte[8];

		for ( var by = 0; by < blocksHigh; by++ )
		{
			for ( var bx = 0; bx < blocksWide; bx++ )
			{
				var block = source.Slice( (by * blocksWide + bx) * blockSize, blockSize );
				ulong indices;

				if ( format == BitmapFormat.Dxt1 )
				{
					// Only three-color blocks have a transparent index.
					if ( BinaryPrimitives.ReadUInt16LittleEndian( block ) > BinaryPrimitives.ReadUInt16LittleEndian( block[2..] ) )
						continue;

					indices = BinaryPrimitives.ReadUInt32LittleEndian( block[4..] );
				}
				else
				{
					int a0 = block[0], a1 = block[1];
					alphas[0] = (byte)a0;
					alphas[1] = (byte)a1;
					if ( a0 > a1 )
					{
						for ( var i = 1; i < 7; i++ )
							alphas[i + 1] = (byte)(((7 - i) * a0 + i * a1) / 7);
					}
					else
					{
						for ( var i = 1; i < 5; i++ )
							alphas[i + 1] = (byte)(((5 - i) * a0 + i * a1) / 5);
						alphas[6] = 0;
						alphas[7] = 255;
					}

					indices = BinaryPrimitives.ReadUInt64LittleEndian( block ) >> 16;
				}

				for ( var i = 0; i < 16; i++ )
				{
					var x = bx * 4 + (i & 3);
					var y = by * 4 + (i >> 2);
					if ( x >= width || y >= height ) continue;

					var pixel = (y * width + x) * 4;
					if ( format == BitmapFormat.Dxt1 )
					{
						if ( (indices >> (i * 2) & 3) == 3 )
							destination[pixel + 3] = 0;
					}
					else
					{
						destination[pixel + 3] = alphas[(int)(indices >> (i * 3) & 7)];
					}
				}
			}
		}
	}

	static void Unpack565( ushort value, Span<byte> rgb )
	{
		rgb[0] = Expand( value >> 11, 5 );
		rgb[1] = Expand( value >> 5, 6 );
		rgb[2] = Expand( value, 5 );
	}

	static void Set( Span<byte> output, byte r, byte g, byte b, byte a )
	{
		output[0] = r;
		output[1] = g;
		output[2] = b;
		output[3] = a;
	}

	/// <summary>
	/// Scales the low <paramref name="bits"/> bits of <paramref name="value"/> up to 0-255.
	/// </summary>
	static byte Expand( int value, int bits )
	{
		var max = (1 << bits) - 1;
		return (byte)((value & max) * 255 / max);
	}
}
