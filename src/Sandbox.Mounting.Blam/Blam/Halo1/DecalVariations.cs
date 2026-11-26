using System;
using System.Collections.Generic;
using System.Linq;
using Blam.Halo1.Tags;

namespace Blam.Halo1;

/// <summary>
/// One look a decal can have: a rectangle of one of its map's bitmaps, in pixels, either a sprite
/// of a sprite sheet or the whole bitmap, padded so that its registration point (where Halo puts
/// the decal's origin) is the middle of <see cref="PaddedWidth"/> by <see cref="PaddedHeight"/>,
/// the rectangle starting <see cref="PadLeft"/> and <see cref="PadTop"/> pixels in.
/// </summary>
public sealed record DecalVariation( BitmapData Bitmap, int X, int Y, int Width, int Height, bool Sprite, int PadLeft, int PadTop, int PaddedWidth, int PaddedHeight );

/// <summary>
/// The variations of a decal, as Halo picks them: decal maps are usually sprite sheets where each
/// sequence is a variation (a few different scorches or cracks), and Halo gives a new decal the
/// first sprite of a random sequence. A map without sprites is used whole.
/// </summary>
public static class DecalVariations
{
	public static List<DecalVariation> Read( CacheFile cache, DecalTag decal )
	{
		var variations = new List<DecalVariation>();
		if ( decal.Map is null )
			return variations;

		var bitmap = new BitmapTag( cache, decal.Map );
		foreach ( var sprite in bitmap.Sequences.Where( s => s.Sprites.Count > 0 ).Select( s => s.Sprites[0] ) )
		{
			if ( sprite.BitmapIndex < 0 || sprite.BitmapIndex >= bitmap.Bitmaps.Count )
				continue;

			var page = bitmap.Bitmaps[sprite.BitmapIndex];
			var (x, y, width, height) = sprite.GetPixelRect( page );

			// The registration point is in the page's texture coordinates, from the sprite's corner.
			var (padLeft, paddedWidth) = Centre( width, sprite.RegistrationX * page.Width );
			var (padTop, paddedHeight) = Centre( height, sprite.RegistrationY * page.Height );
			variations.Add( new DecalVariation( page, x, y, width, height, true, padLeft, padTop, paddedWidth, paddedHeight ) );
		}

		if ( variations.Count == 0 && bitmap.Bitmaps.Count > 0 && bitmap.Bitmaps[0].Type == BitmapType.Texture2D )
		{
			var whole = bitmap.Bitmaps[0];
			variations.Add( new DecalVariation( whole, 0, 0, whole.Width, whole.Height, false, 0, 0, whole.Width, whole.Height ) );
		}

		return variations;
	}

	/// <summary>
	/// The padding before a span of <paramref name="size"/> pixels and the padded size that put
	/// <paramref name="registration"/> (pixels from the span's start) in the middle.
	/// </summary>
	static (int Pad, int Size) Centre( int size, float registration )
	{
		var half = MathF.Max( registration, size - registration );
		var pad = Math.Max( 0, (int)MathF.Round( half - registration ) );
		return (pad, Math.Max( (int)MathF.Ceiling( 2 * half ), pad + size ));
	}

	/// <summary>
	/// The variation's pixels as RGBA8888, padded with <paramref name="neutral"/>, or null if its
	/// bitmap's format can't be decoded.
	/// </summary>
	public static byte[] Cut( CacheFile cache, DecalVariation variation, ReadOnlySpan<byte> neutral )
	{
		var bitmap = variation.Bitmap;
		if ( bitmap.Type != BitmapType.Texture2D || bitmap.Width <= 0 || bitmap.Height <= 0 )
			return null;

		var pixels = BitmapTag.ReadPixels( cache, bitmap );
		var page = BitmapDecoder.DecodeTopLevel( bitmap, pixels.AsSpan( 0, Math.Min( pixels.Length, bitmap.GetLevelSize( 0 ) ) ) );
		if ( page is null )
			return null;

		var rgba = new byte[variation.PaddedWidth * variation.PaddedHeight * 4];
		for ( var i = 0; i < rgba.Length; i += 4 )
			neutral.CopyTo( rgba.AsSpan( i, 4 ) );

		for ( var y = 0; y < variation.Height; y++ )
		{
			Array.Copy( page, ((variation.Y + y) * bitmap.Width + variation.X) * 4,
				rgba, ((variation.PadTop + y) * variation.PaddedWidth + variation.PadLeft) * 4, variation.Width * 4 );
		}

		return rgba;
	}
}
