using System;
using System.Collections.Generic;
using System.Linq;
using Blam.Halo3.Tags;

namespace Blam.Halo3;

using Vector2 = System.Numerics.Vector2;
using Vector4 = System.Numerics.Vector4;

// An RGBA8 image whose rgb is sRGB-encoded when Srgb (alpha is always linear).
public sealed record DecalAlbedoImage( int Width, int Height, byte[] Rgba, bool Srgb )
{
	// Every level down to 1x1 (2x2 box filter, odd sizes clamped), smallest first as s&box takes them, and how many there are.
	public (byte[] Data, int Levels) MipChain()
	{
		var levels = new List<byte[]> { Rgba };
		var (w, h) = (Width, Height);
		while ( w > 1 || h > 1 )
		{
			var (nw, nh) = (Math.Max( 1, w / 2 ), Math.Max( 1, h / 2 ));
			var source = levels[^1];
			var next = new byte[nw * nh * 4];
			for ( var y = 0; y < nh; y++ )
			for ( var x = 0; x < nw; x++ )
			for ( var c = 0; c < 4; c++ )
			{
				int x0 = Math.Min( x * 2, w - 1 ), x1 = Math.Min( x * 2 + 1, w - 1 ), y0 = Math.Min( y * 2, h - 1 ), y1 = Math.Min( y * 2 + 1, h - 1 );
				var sum = source[(y0 * w + x0) * 4 + c] + source[(y0 * w + x1) * 4 + c] + source[(y1 * w + x0) * 4 + c] + source[(y1 * w + x1) * 4 + c];
				next[(y * nw + x) * 4 + c] = (byte)((sum + 2) / 4);
			}
			levels.Add( next );
			(w, h) = (nw, nh);
		}

		var data = new byte[levels.Sum( l => l.Length )];
		var offset = 0;
		for ( var k = levels.Count - 1; k >= 0; k-- )
		{
			var l = levels[k];
			l.CopyTo( data, offset );
			offset += l.Length;
		}
		return (data, levels.Count);
	}
}

// The decal render method's albedo options that read more than the base map, baked into one RGBA image the level's shader can take as its base map:
// diffuse_plus_alpha (base rgb, alpha map's alpha), diffuse_plus_alpha_mask (base rgb tiled by u_tiles/v_tiles, alpha map's alpha at the decal's own uv), palettized (the palette's colour at the base map's index), palettized_plus_alpha (and its alpha map's alpha), vector_alpha (the base map's colour, opacity from the vector map's distance field sharpened by vector_sharpness).
// Our reading of MCC's decal shader from its parameters, not lifted code.
public static class DecalAlbedo
{
	const int MaximumSize = 1024;

	public static bool NeedsComposite( ShaderTag decal ) => decal?.Option( "albedo" ) is "diffuse_plus_alpha" or "diffuse_plus_alpha_mask" or "palettized" or "palettized_plus_alpha"
		or "palettized_plus_alpha_mask" or "vector_alpha" or "vector_alpha_drop_shadow";

	// Null when a bitmap it needs has no pixels in this map.
	public static DecalAlbedoImage Compose( ShaderTag decal )
	{
		var option = decal.Option( "albedo" );
		var baseMap = Image.Load( decal.Texture( "base_map" ) );
		if ( baseMap is null )
			return null;

		switch ( option )
		{
			case "diffuse_plus_alpha":
			case "diffuse_plus_alpha_mask":
			{
				var alpha = Image.Load( decal.Texture( "alpha_map" ) );
				if ( alpha is null )
					return null;
				var tiles = option == "diffuse_plus_alpha_mask" ? new Vector2( decal.Scalar( "u_tiles", 1 ), decal.Scalar( "v_tiles", 1 ) ) : Vector2.One;
				var (w, h) = Size( Math.Max( alpha.Width, (int)(baseMap.Width * MathF.Abs( tiles.X )) ), Math.Max( alpha.Height, (int)(baseMap.Height * MathF.Abs( tiles.Y )) ) );
				return Build( w, h, baseMap.Srgb, uv => new Vector4( Rgb( baseMap.Sample( uv * tiles ) ), alpha.Sample( uv ).W ) );
			}
			case "palettized":
			case "palettized_plus_alpha":
			case "palettized_plus_alpha_mask":
			{
				var palette = Image.Load( decal.Texture( "palette" ) );
				if ( palette is null )
					return null;
				var alpha = option == "palettized" ? null : Image.Load( decal.Texture( "alpha_map" ) );
				var v = decal.Scalar( "palette_v", 0.5f );
				var (w, h) = Size( baseMap.Width, baseMap.Height );
				return Build( w, h, palette.Srgb, uv =>
				{
					var index = Index( baseMap.Sample( uv ) );
					var c = palette.SampleClamped( new Vector2( index, v ) );
					return new Vector4( Rgb( c ), alpha is null ? c.W : alpha.Sample( uv ).W );
				} );
			}
			case "vector_alpha":
			case "vector_alpha_drop_shadow":
			{
				var vector = Image.Load( decal.Texture( "vector_map" ) );
				if ( vector is null )
					return null;
				var sharpness = MathF.Max( decal.Scalar( "vector_sharpness", 1000 ), 1 );
				var (w, h) = Size( Math.Max( baseMap.Width, vector.Width ), Math.Max( baseMap.Height, vector.Height ) );
				return Build( w, h, baseMap.Srgb, uv =>
				{
					var c = baseMap.Sample( uv );
					var field = vector.Sample( uv ).X;
					return new Vector4( Rgb( c ), c.W * Math.Clamp( (field - 0.5f) * sharpness + 0.5f, 0, 1 ) );
				} );
			}
			default:
				return null;
		}
	}

	// A texture parameter's image as stored (level 0), for dumps.
	public static DecalAlbedoImage Decode( ShaderTexture texture ) => Image.Load( texture ) is { } i ? new DecalAlbedoImage( i.Width, i.Height, i.Rgba, i.Srgb ) : null;

	static System.Numerics.Vector3 Rgb( Vector4 c ) => new( c.X, c.Y, c.Z );

	// A single-channel index map comes out of the decoder as grey (or white with alpha for A8).
	static float Index( Vector4 c ) => c.X >= 0.999f && c.Y >= 0.999f && c.Z >= 0.999f ? c.W : c.X;

	static (int, int) Size( int w, int h )
	{
		var scale = Math.Min( 1f, (float)MaximumSize / Math.Max( w, h ) );
		return (Math.Max( 1, (int)(w * scale) ), Math.Max( 1, (int)(h * scale) ));
	}

	static DecalAlbedoImage Build( int w, int h, bool srgb, Func<Vector2, Vector4> pixel )
	{
		var rgba = new byte[w * h * 4];
		for ( var y = 0; y < h; y++ )
		for ( var x = 0; x < w; x++ )
		{
			var c = pixel( new Vector2( (x + 0.5f) / w, (y + 0.5f) / h ) );
			var o = (y * w + x) * 4;
			rgba[o] = Byte( c.X );
			rgba[o + 1] = Byte( c.Y );
			rgba[o + 2] = Byte( c.Z );
			rgba[o + 3] = Byte( c.W );
		}
		return new DecalAlbedoImage( w, h, rgba, srgb );
	}

	static byte Byte( float v ) => (byte)Math.Clamp( (int)MathF.Round( v * 255 ), 0, 255 );

	// One decoded image (level 0, layer 0) with its texture parameter's transform.
	internal sealed class Image
	{
		public int Width, Height;
		public byte[] Rgba;
		public bool Srgb;
		public Vector4 Transform;

		public static Image Load( ShaderTexture texture )
		{
			if ( texture?.Bitmap is null || texture.Bitmap.Data.IsNull )
				return null;
			var images = BitmapTag.ImagesOf( texture.Bitmap );
			var index = Math.Clamp( texture.BitmapIndex, 0, Math.Max( 0, images.Count - 1 ) );
			if ( images.Count == 0 )
				return null;
			try
			{
				var pixels = BitmapPixels.Read( images[index] );
				var w = pixels.LevelWidth( 0 );
				var h = pixels.LevelHeight( 0 );
				return new Image
				{
					Width = w,
					Height = h,
					Rgba = BitmapDecoder.ToRgba8888( pixels.Format, pixels.Levels[0][0].Span, w, h ),
					Srgb = images[index].IsSrgb,
					Transform = texture.Transform,
				};
			}
			catch ( Exception e ) when ( e is BitmapUnavailableException or NotSupportedException or ArgumentException or IndexOutOfRangeException )
			{
				return null;
			}
		}

		public Vector4 Sample( Vector2 uv ) => Bilinear( uv * new Vector2( Transform.X, Transform.Y ) + new Vector2( Transform.Z, Transform.W ), true );

		public Vector4 SampleClamped( Vector2 uv ) => Bilinear( uv, false );

		Vector4 Bilinear( Vector2 uv, bool wrap )
		{
			var x = uv.X * Width - 0.5f;
			var y = uv.Y * Height - 0.5f;
			var x0 = (int)MathF.Floor( x );
			var y0 = (int)MathF.Floor( y );
			var fx = x - x0;
			var fy = y - y0;
			var a = Texel( x0, y0, wrap );
			var b = Texel( x0 + 1, y0, wrap );
			var c = Texel( x0, y0 + 1, wrap );
			var d = Texel( x0 + 1, y0 + 1, wrap );
			return Vector4.Lerp( Vector4.Lerp( a, b, fx ), Vector4.Lerp( c, d, fx ), fy );
		}

		Vector4 Texel( int x, int y, bool wrap )
		{
			x = wrap ? ((x % Width) + Width) % Width : Math.Clamp( x, 0, Width - 1 );
			y = wrap ? ((y % Height) + Height) % Height : Math.Clamp( y, 0, Height - 1 );
			var o = (y * Width + x) * 4;
			return new Vector4( Rgba[o], Rgba[o + 1], Rgba[o + 2], Rgba[o + 3] ) / 255f;
		}
	}
}
