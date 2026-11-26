using System;
using System.Collections.Generic;

namespace Blam;

// Packs a font's glyphs onto one page texture, so a game can draw text from one texture.
public static class GlyphPage
{
	// Space between glyphs on the page, so bilinear filtering doesn't pick up the neighbors.
	const int Padding = 1;

	// Places the glyphs in rows, in order, on a page whose width is the smallest power of two that keeps it about square.
	// It's deterministic, so the rectangles a game reads and the page the mount draws always agree.
	// Empty glyphs stay at 0, 0.
	public static ((int X, int Y)[] Positions, int Width, int Height) Pack( IReadOnlyList<(int Width, int Height)> glyphs )
	{
		var area = 0;
		var widest = 1;
		foreach ( var (w, h) in glyphs )
		{
			area += (w + Padding) * (h + Padding);
			widest = Math.Max( widest, w + Padding );
		}

		var width = 16;
		while ( width < widest || width * width < area * 5 / 4 )
			width *= 2;

		var positions = new (int, int)[glyphs.Count];
		int x = 0, y = 0, row = 0;
		for ( var i = 0; i < glyphs.Count; i++ )
		{
			var (w, h) = glyphs[i];
			if ( w == 0 || h == 0 )
				continue;

			if ( x + w > width )
			{
				x = 0;
				y += row + Padding;
				row = 0;
			}

			positions[i] = (x, y);
			x += w + Padding;
			row = Math.Max( row, h );
		}

		// Block compressed or not, textures are friendlier in multiples of 4.
		var height = Math.Max( 4, (y + row + 3) / 4 * 4 );
		return (positions, width, height);
	}
}
