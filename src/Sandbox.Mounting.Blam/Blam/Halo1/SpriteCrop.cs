using System;
using System.Collections.Generic;
using Blam.Halo1.Tags;

namespace Blam.Halo1;

/// <summary>
/// Cuts single sprites out of sprite pages: HUD digits, crosshairs and icons, particle frames,
/// decal variations. The page's full size image is decoded and the sprite's rectangle copied out,
/// with a fresh mip chain if the page had mips.
/// </summary>
/// <remarks>
/// Halo's own mips can't be cropped: sprite rectangles aren't aligned to the mip block sizes, so
/// each smaller level is filtered from the crop instead. Colors are averaged weighted by alpha, like
/// the tool's "alpha blend" usage, so transparent texels don't darken the edges.
/// </remarks>
public static class SpriteCrop
{
	// A sprite's rectangle (texture coordinates, 0-1) in pixels of a page of the given size, at least one pixel.
	public static (int X, int Y, int Width, int Height) PixelRect( float left, float right, float top, float bottom, int width, int height )
	{
		var x0 = Math.Clamp( (int)MathF.Round( left * width ), 0, width - 1 );
		var y0 = Math.Clamp( (int)MathF.Round( top * height ), 0, height - 1 );
		var x1 = Math.Clamp( (int)MathF.Round( right * width ), x0 + 1, width );
		var y1 = Math.Clamp( (int)MathF.Round( bottom * height ), y0 + 1, height );
		return (x0, y0, x1 - x0, y1 - y0);
	}

	public static bool CanCrop( BitmapData page ) =>
		page.Type == BitmapType.Texture2D && page.Width > 0 && page.Height > 0 && BitmapDecoder.CanDecodeImage( page.Format );

	/// <summary>
	/// Decodes the page's full size image (the start of <paramref name="pixels"/>) and returns the
	/// rectangle as RGBA8888.
	/// </summary>
	public static byte[] Crop( BitmapData page, ReadOnlySpan<byte> pixels, (int X, int Y, int Width, int Height) rect )
	{
		var rgba = new byte[page.Width * page.Height * 4];
		BitmapDecoder.DecodeImage( page.Format, pixels[..page.GetLevelSize( 0 )], rgba, page.Width, page.Height );

		var crop = new byte[rect.Width * rect.Height * 4];
		for ( var y = 0; y < rect.Height; y++ )
			Array.Copy( rgba, ((rect.Y + y) * page.Width + rect.X) * 4, crop, y * rect.Width * 4, rect.Width * 4 );

		return crop;
	}

	/// <summary>
	/// The full mip chain of an RGBA8888 image down to 1x1, largest first, starting with the image itself.
	/// Odd sizes round down; the last row or column of an odd level is folded into its neighbor.
	/// </summary>
	public static List<byte[]> MipChain( byte[] rgba, int width, int height )
	{
		var levels = new List<byte[]> { rgba };
		while ( width > 1 || height > 1 )
		{
			var w = Math.Max( 1, width / 2 );
			var h = Math.Max( 1, height / 2 );
			var previous = levels[^1];
			var next = new byte[w * h * 4];

			for ( var y = 0; y < h; y++ )
			for ( var x = 0; x < w; x++ )
			{
				// The source texels this one covers: 2x2, or 3 wide/high at the end of an odd row/column.
				var x0 = Math.Min( x * 2, width - 1 );
				var y0 = Math.Min( y * 2, height - 1 );
				var x1 = x == w - 1 ? width - 1 : Math.Min( x0 + 1, width - 1 );
				var y1 = y == h - 1 ? height - 1 : Math.Min( y0 + 1, height - 1 );

				float r = 0, g = 0, b = 0, a = 0, plainR = 0, plainG = 0, plainB = 0;
				var count = 0;
				for ( var sy = y0; sy <= y1; sy++ )
				for ( var sx = x0; sx <= x1; sx++ )
				{
					var i = (sy * width + sx) * 4;
					var alpha = previous[i + 3];
					r += previous[i] * alpha;
					g += previous[i + 1] * alpha;
					b += previous[i + 2] * alpha;
					a += alpha;
					plainR += previous[i];
					plainG += previous[i + 1];
					plainB += previous[i + 2];
					count++;
				}

				var o = (y * w + x) * 4;
				if ( a > 0 )
				{
					next[o] = (byte)MathF.Round( r / a );
					next[o + 1] = (byte)MathF.Round( g / a );
					next[o + 2] = (byte)MathF.Round( b / a );
				}
				else
				{
					next[o] = (byte)MathF.Round( plainR / count );
					next[o + 1] = (byte)MathF.Round( plainG / count );
					next[o + 2] = (byte)MathF.Round( plainB / count );
				}
				next[o + 3] = (byte)MathF.Round( a / count );
			}

			levels.Add( next );
			width = w;
			height = h;
		}

		return levels;
	}
}
