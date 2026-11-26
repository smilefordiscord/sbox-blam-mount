using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using Blam.Halo3.Tags;

namespace Blam.Halo3;

// The formats a Halo 3 bitmap goes to s&box in (mapped to ImageFormat by the mount).
public enum UploadFormat
{
	Dxt1,
	Dxt5,
	Ati1n,
	Ati2n,
	Rgba16F,
	Rgba8888,
}

public enum UploadShape
{
	Texture2D,
	Array,
	Cube,
	CubeAtlas,
	Volume,
}

// Texture data ready for s&box: levels smallest first, each level holding every layer (array slice, cube face, volume slice) in order.
public sealed record BitmapUploadData( UploadShape Shape, UploadFormat Format, int Width, int Height, int Layers, int LevelCount, byte[] Data );

// Builds what the mount uploads, without any engine types, so BlamInspect can run the same code over every bitmap.
// BC1 (opaque), BC3, BC4 and BC5 go as they are; BC2 (empty in s&box) and BC1 with transparent texels (s&box drops its alpha) become BC3, R8G8 bump maps BC5 (BitmapEncoder), the other plain formats RGBA8888.
// Signed BC4/BC5 are re-biased to unsigned (endpoints + 128), so a shader reads them as tex * 2 - 1 like the RGBA8888 normals.
public static class BitmapUpload
{
	public static bool IsSupported( BitmapType type, DxgiFormat format ) => BitmapLayout.IsKnown( format ) && type switch
	{
		BitmapType.Texture2D or BitmapType.CubeMap or BitmapType.Array => true,
		BitmapType.Texture3D => !BitmapLayout.IsBlockCompressed( format ),
		_ => false,
	};

	// Stored cube faces are +X +Y -X -Y +Z -Z (as in Halo CE); this is the stored face of each D3D face.
	public static ReadOnlySpan<int> StoredCubeFace => [0, 2, 1, 3, 4, 5];

	public static UploadFormat FormatOf( BitmapPixels pixels )
	{
		var format = pixels.Format;
		// Block compressed images smaller than one block come out black in s&box.
		if ( (BitmapLayout.IsBlockCompressed( format ) || IsTwoChannel( pixels )) && Math.Min( pixels.Width, pixels.Height ) < 4 )
			return UploadFormat.Rgba8888;
		if ( IsTwoChannel( pixels ) )
			return UploadFormat.Ati2n;

		return format switch
		{
			DxgiFormat.BC1Unorm or DxgiFormat.BC1UnormSrgb => AnyBc1Transparency( pixels ) ? UploadFormat.Dxt5 : UploadFormat.Dxt1,
			DxgiFormat.BC2Unorm or DxgiFormat.BC2UnormSrgb => UploadFormat.Dxt5,
			DxgiFormat.BC3Unorm or DxgiFormat.BC3UnormSrgb => UploadFormat.Dxt5,
			DxgiFormat.BC4Unorm or DxgiFormat.BC4Snorm => UploadFormat.Ati1n,
			DxgiFormat.BC5Unorm or DxgiFormat.BC5Snorm => UploadFormat.Ati2n,
			DxgiFormat.R16G16B16A16Float => UploadFormat.Rgba16F,
			_ => UploadFormat.Rgba8888,
		};
	}

	// Bump maps (V8U8), only ever read as tex.rg * 2 - 1 with z rebuilt. Water wave slope arrays stay uncompressed (RGBA8888, the same encoding), as MCC samples them (R8G8_SNORM).
	static bool IsTwoChannel( BitmapPixels pixels ) => pixels.Format is DxgiFormat.R8G8Snorm or DxgiFormat.R8G8Unorm && pixels.Image.Type is not (BitmapType.Texture3D or BitmapType.Array);

	static bool AnyBc1Transparency( BitmapPixels pixels )
	{
		foreach ( var layer in pixels.Levels )
		foreach ( var level in layer )
		{
			if ( BitmapDecoder.Bc1HasTransparency( level.Span ) )
				return true;
		}
		return false;
	}

	// Levels to upload: block compressed chains stop at 4x4 (smaller levels come out black in s&box).
	public static int LevelsOf( BitmapPixels pixels, UploadFormat format )
	{
		var levels = pixels.LevelCount;
		if ( format is not (UploadFormat.Rgba8888 or UploadFormat.Rgba16F) )
		{
			while ( levels > 1 && Math.Min( pixels.LevelWidth( levels - 1 ), pixels.LevelHeight( levels - 1 ) ) < 4 )
				levels--;
		}
		return levels;
	}

	public static BitmapUploadData Texture2D( BitmapPixels pixels, int layer = 0 )
	{
		var format = FormatOf( pixels );
		var levels = LevelsOf( pixels, format );
		return new BitmapUploadData( UploadShape.Texture2D, format, pixels.Width, pixels.Height, 1, levels, SmallestFirst( pixels, format, levels, [layer] ) );
	}

	public static BitmapUploadData Array( BitmapPixels pixels )
	{
		var format = FormatOf( pixels );
		var levels = LevelsOf( pixels, format );
		var layers = new int[pixels.Layers];
		for ( var i = 0; i < layers.Length; i++ )
			layers[i] = i;
		return new BitmapUploadData( UploadShape.Array, format, pixels.Width, pixels.Height, pixels.Layers, levels, SmallestFirst( pixels, format, levels, layers ) );
	}

	// A real cube texture, faces in D3D order with their whole mip chains.
	public static BitmapUploadData Cube( BitmapPixels pixels )
	{
		var format = FormatOf( pixels );
		var levels = LevelsOf( pixels, format );
		return new BitmapUploadData( UploadShape.Cube, format, pixels.Width, pixels.Height, 6, levels, SmallestFirst( pixels, format, levels, StoredCubeFace ) );
	}

	// The six faces side by side in D3D order (+X -X +Y -Y +Z -Z), full size only, like the Halo CE and Halo 2 loaders (cube textures can't be bound on runtime materials).
	// The six faces side by side, each level of the cube's chain a level of the atlas (smallest first): shaders pick a level as a cube map's sampler would (MCC's water reflection blurs with its waves).
	public static BitmapUploadData CubeAtlas( BitmapPixels pixels )
	{
		var format = FormatOf( pixels );
		var levels = LevelsOf( pixels, format );
		var parts = new List<byte[]>();
		for ( var level = levels - 1; level >= 0; level-- )
			parts.Add( CubeAtlasLevel( pixels, format, level ) );

		var atlas = new byte[parts.Sum( p => p.Length )];
		var offset = 0;
		foreach ( var part in parts )
		{
			part.CopyTo( atlas, offset );
			offset += part.Length;
		}

		return new BitmapUploadData( UploadShape.CubeAtlas, format, pixels.Width * 6, pixels.Height, 1, levels, atlas );
	}

	static byte[] CubeAtlasLevel( BitmapPixels pixels, UploadFormat format, int level )
	{
		var faces = new byte[6][];
		for ( var face = 0; face < 6; face++ )
			faces[face] = Convert( pixels, StoredCubeFace[face], level, format );

		// Interleave the faces row by row; for block compressed data a row is a row of 4x4 blocks.
		var height = pixels.LevelHeight( level );
		var rows = format is UploadFormat.Rgba8888 or UploadFormat.Rgba16F ? height : (height + 3) / 4;
		var rowSize = faces[0].Length / rows;
		var atlas = new byte[faces[0].Length * 6];
		for ( var row = 0; row < rows; row++ )
		for ( var face = 0; face < 6; face++ )
			System.Array.Copy( faces[face], row * rowSize, atlas, (row * 6 + face) * rowSize, rowSize );
		return atlas;
	}

	public static BitmapUploadData Volume( BitmapPixels pixels )
	{
		var format = FormatOf( pixels );
		return new BitmapUploadData( UploadShape.Volume, format, pixels.Width, pixels.Height, pixels.Depth, pixels.LevelCount, SmallestFirst( pixels, format, pixels.LevelCount, [0] ) );
	}

	static byte[] SmallestFirst( BitmapPixels pixels, UploadFormat format, int levels, ReadOnlySpan<int> layers )
	{
		var parts = new List<byte[]>();
		var total = 0;
		for ( var level = levels - 1; level >= 0; level-- )
		{
			foreach ( var layer in layers )
			{
				var part = Convert( pixels, layer, level, format );
				parts.Add( part );
				total += part.Length;
			}
		}

		var output = new byte[total];
		var offset = 0;
		foreach ( var part in parts )
		{
			part.CopyTo( output, offset );
			offset += part.Length;
		}
		return output;
	}

	// One level of one layer in the upload format.
	public static byte[] Convert( BitmapPixels pixels, int layer, int level, UploadFormat format )
	{
		var data = pixels.Levels[layer][level].Span;
		switch ( format )
		{
			case UploadFormat.Ati1n or UploadFormat.Ati2n when pixels.Format is DxgiFormat.BC4Snorm or DxgiFormat.BC5Snorm:
				var biased = data.ToArray();
				for ( var block = 0; block + 8 <= biased.Length; block += 8 )
				{
					biased[block] ^= 0x80;
					biased[block + 1] ^= 0x80;
				}
				return biased;

			case UploadFormat.Dxt5 when pixels.Format is DxgiFormat.BC1Unorm or DxgiFormat.BC1UnormSrgb:
				return BitmapEncoder.Bc1ToBc3( data, pixels.LevelWidth( level ), pixels.LevelHeight( level ) );

			case UploadFormat.Dxt5 when pixels.Format is DxgiFormat.BC2Unorm or DxgiFormat.BC2UnormSrgb:
				return BitmapEncoder.Bc2ToBc3( data, pixels.LevelWidth( level ), pixels.LevelHeight( level ) );

			case UploadFormat.Ati2n when pixels.Format is DxgiFormat.R8G8Snorm or DxgiFormat.R8G8Unorm:
				return BitmapEncoder.R8G8ToBc5( data, pixels.LevelWidth( level ), pixels.LevelHeight( level ), pixels.Format == DxgiFormat.R8G8Snorm );

			case UploadFormat.Rgba8888:
				var width = pixels.LevelWidth( level );
				var height = pixels.LevelHeight( level ) * pixels.LevelDepth( level );
				return BitmapDecoder.ToRgba8888( pixels.Format, data, width, height );

			default:
				return data.ToArray();
		}
	}

	// Bytes s&box wants for one level of one layer in a format.
	public static int UploadLevelSize( UploadFormat format, int width, int height ) => format switch
	{
		UploadFormat.Dxt1 or UploadFormat.Ati1n => Math.Max( 1, (width + 3) / 4 ) * Math.Max( 1, (height + 3) / 4 ) * 8,
		UploadFormat.Dxt5 or UploadFormat.Ati2n => Math.Max( 1, (width + 3) / 4 ) * Math.Max( 1, (height + 3) / 4 ) * 16,
		UploadFormat.Rgba16F => width * height * 8,
		_ => width * height * 4,
	};

	// The size the data must have: every level of every layer (volume slices halve per level).
	public static long ExpectedSize( BitmapUploadData upload )
	{
		long total = 0;
		for ( var level = 0; level < upload.LevelCount; level++ )
		{
			var w = Math.Max( 1, upload.Width >> level );
			var h = Math.Max( 1, upload.Height >> level );
			var layers = upload.Shape == UploadShape.Volume ? Math.Max( 1, upload.Layers >> level ) : upload.Layers;
			total += (long)UploadLevelSize( upload.Format, w, h ) * layers;
		}
		return total;
	}

	public static void CheckSize( BitmapUploadData upload )
	{
		var expected = ExpectedSize( upload );
		if ( upload.Data.Length != expected )
			throw new InvalidOperationException( $"{upload.Shape} {upload.Format} {upload.Width}x{upload.Height}x{upload.Layers} {upload.LevelCount} levels: {upload.Data.Length} bytes, expected {expected}" );
	}
}
