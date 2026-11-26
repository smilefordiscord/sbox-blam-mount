using System;
using System.Buffers.Binary;
using Blam.Halo1.Tags;

namespace Blam.Halo1;

/// <summary>
/// A decoded image: 8-bit RGBA rows, top to bottom.
/// </summary>
public sealed record RgbaImage( int Width, int Height, byte[] Pixels );

/// <summary>
/// Cuts the sprites of sprite sheets (particles, decals) out into images of their own, for
/// engine components that draw a whole texture rather than a rectangle of one.
/// </summary>
public static class SpriteImages
{
	/// <summary>
	/// The full size image of a 2D bitmap as RGBA, or null for formats it can't decode.
	/// </summary>
	public static RgbaImage Decode( CacheFile cache, BitmapData bitmap )
	{
		if ( bitmap.Type != BitmapType.Texture2D || bitmap.Width <= 0 || bitmap.Height <= 0 )
			return null;

		var pixels = BitmapTag.ReadPixels( cache, bitmap );
		var width = bitmap.Width;
		var height = bitmap.Height;
		var rgba = new byte[width * height * 4];

		switch ( bitmap.Format )
		{
			case BitmapFormat.Dxt1:
				BitmapDecoder.DxtColorToRgba8888( bitmap.Format, pixels, rgba, width, height );
				Dxt1Alpha( pixels, rgba, width, height );
				break;
			case BitmapFormat.Dxt3:
				BitmapDecoder.Dxt3ToRgba8888( pixels, rgba, width, height );
				break;
			case BitmapFormat.Dxt5:
				BitmapDecoder.DxtColorToRgba8888( bitmap.Format, pixels, rgba, width, height );
				Dxt5Alpha( pixels, rgba, width, height );
				break;
			default:
				if ( !BitmapDecoder.CanDecode( bitmap.Format ) )
					return null;
				BitmapDecoder.ToRgba8888( bitmap.Format, pixels, rgba, width * height );
				break;
		}

		return new RgbaImage( width, height, rgba );
	}

	/// <summary>
	/// The rectangle of <paramref name="page"/> a sprite covers (texture coordinates, rounded out to
	/// whole pixels).
	/// </summary>
	public static RgbaImage Crop( RgbaImage page, BitmapSprite sprite )
	{
		var x0 = Math.Clamp( (int)MathF.Floor( sprite.Left * page.Width + 0.001f ), 0, page.Width - 1 );
		var y0 = Math.Clamp( (int)MathF.Floor( sprite.Top * page.Height + 0.001f ), 0, page.Height - 1 );
		var x1 = Math.Clamp( (int)MathF.Ceiling( sprite.Right * page.Width - 0.001f ), x0 + 1, page.Width );
		var y1 = Math.Clamp( (int)MathF.Ceiling( sprite.Bottom * page.Height - 0.001f ), y0 + 1, page.Height );

		var width = x1 - x0;
		var height = y1 - y0;
		var pixels = new byte[width * height * 4];
		for ( var y = 0; y < height; y++ )
			Array.Copy( page.Pixels, ((y0 + y) * page.Width + x0) * 4, pixels, y * width * 4, width * 4 );

		return new RgbaImage( width, height, pixels );
	}

	/// <summary>
	/// DXT1 blocks in three-color mode (first color not greater than the second) use the fourth
	/// index for transparent black.
	/// </summary>
	static void Dxt1Alpha( ReadOnlySpan<byte> source, Span<byte> rgba, int width, int height )
	{
		var blocksWide = (width + 3) / 4;
		var blocksHigh = (height + 3) / 4;
		for ( var by = 0; by < blocksHigh; by++ )
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
					rgba[(y * width + x) * 4 + 3] = 0;
			}
		}
	}

	/// <summary>
	/// DXT5 alpha: two endpoints and 3-bit indices into eight levels between them (six
	/// interpolated, or four plus 0 and 255 when the first endpoint isn't the larger).
	/// </summary>
	static void Dxt5Alpha( ReadOnlySpan<byte> source, Span<byte> rgba, int width, int height )
	{
		var blocksWide = (width + 3) / 4;
		var blocksHigh = (height + 3) / 4;
		Span<byte> levels = stackalloc byte[8];
		for ( var by = 0; by < blocksHigh; by++ )
		for ( var bx = 0; bx < blocksWide; bx++ )
		{
			var block = source.Slice( (by * blocksWide + bx) * 16, 8 );
			int a0 = block[0], a1 = block[1];
			levels[0] = (byte)a0;
			levels[1] = (byte)a1;
			if ( a0 > a1 )
			{
				for ( var k = 1; k < 7; k++ )
					levels[k + 1] = (byte)(((7 - k) * a0 + k * a1) / 7);
			}
			else
			{
				for ( var k = 1; k < 5; k++ )
					levels[k + 1] = (byte)(((5 - k) * a0 + k * a1) / 5);
				levels[6] = 0;
				levels[7] = 255;
			}

			ulong bits = 0;
			for ( var b = 0; b < 6; b++ )
				bits |= (ulong)block[2 + b] << (8 * b);

			for ( var i = 0; i < 16; i++ )
			{
				var x = bx * 4 + (i & 3);
				var y = by * 4 + (i >> 2);
				if ( x < width && y < height )
					rgba[(y * width + x) * 4 + 3] = levels[(int)(bits >> (3 * i) & 7)];
			}
		}
	}
}
