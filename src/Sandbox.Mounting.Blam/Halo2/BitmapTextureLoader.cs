using Blam.Halo2.Tags;
using Halo1Format = Blam.Halo1.Tags.BitmapFormat;

namespace Sandbox.Mounting.Blam.Halo2;

/// <summary>
/// Turns one bitmap of a Halo 2 bitmap tag into a texture, from its pixels in textures.dat.
/// </summary>
/// <remarks>
/// Works like the Halo CE loader: DXT1/5 go to the GPU as they are, everything else (DXT3 included, which
/// s&amp;box shows as empty) becomes RGBA8888, mips go smallest first, and cube maps become a six face atlas.
/// </remarks>
sealed class BitmapTextureLoader( BitmapData bitmap ) : Halo2Loader
{
	public static bool IsSupported( BitmapData bitmap ) =>
		bitmap.Width > 0 && bitmap.Height > 0 && bitmap.ElementSize > 0 && CanConvert( bitmap.Format ) && bitmap.Type switch
		{
			BitmapType.Texture2D or BitmapType.CubeMap => true,
			BitmapType.Texture3D => bitmap.Depth > 0 && !bitmap.IsBlockCompressed,
			_ => false,
		};

	static bool CanConvert( BitmapFormat format ) => format is BitmapFormat.Dxt1 or BitmapFormat.Dxt3 or BitmapFormat.Dxt5
		or BitmapFormat.V8U8 or BitmapFormat.G8B8
		|| ((ushort)format <= (ushort)Halo1Format.A8R8G8B8 && global::Blam.Halo1.BitmapDecoder.CanDecode( (Halo1Format)format ));

	protected override object Load()
	{
		var pixels = Host.Textures.Read( bitmap.PixelDataOffset, bitmap.PixelDataSize );
		var format = bitmap.Format switch
		{
			BitmapFormat.Dxt1 => ImageFormat.DXT1,
			BitmapFormat.Dxt5 => ImageFormat.DXT5,
			_ => ImageFormat.RGBA8888,
		};

		return bitmap.Type switch
		{
			BitmapType.CubeMap => LoadCubeMap( pixels, format ),
			BitmapType.Texture3D => LoadVolume( pixels ),
			_ => Load2D( pixels, format ),
		};
	}

	Texture Load2D( byte[] pixels, ImageFormat format )
	{
		// Block compressed mips smaller than one 4x4 block come out black in s&box, so those chains stop at 4x4.
		var levelCount = bitmap.LevelCount;
		if ( format != ImageFormat.RGBA8888 )
		{
			while ( levelCount > 1 && Math.Min( bitmap.Width >> (levelCount - 1), bitmap.Height >> (levelCount - 1) ) < 4 )
				levelCount--;
		}

		return Texture.Create( bitmap.Width, bitmap.Height, format )
			.WithName( Path )
			.WithData( SmallestFirst( pixels, levelCount, format ) )
			.WithMips( levelCount )
			.WithStaticUsage()
			.Finish();
	}

	Texture LoadVolume( byte[] pixels )
	{
		return Texture.CreateVolume( bitmap.Width, bitmap.Height, bitmap.Depth, ImageFormat.RGBA8888 )
			.WithName( Path )
			.WithData( SmallestFirst( pixels, bitmap.LevelCount, ImageFormat.RGBA8888 ) )
			.WithMips( bitmap.LevelCount )
			.WithStaticUsage()
			.Finish();
	}

	/// <summary>
	/// A sequence of same sized 2D bitmaps (the water bump flipbooks: 17 frames played by time) as one RGBA8888
	/// texture with the frames side by side, every level too (frame n's level in the n-th slot of that level), or
	/// null if they can't be. Frames are power of two sized, so each level of the atlas is exactly its frames'.
	/// </summary>
	/// <param name="columns">Frames per row of the atlas, the rest in rows below (frame n at column n % columns,
	/// row n / columns); 0 for one row.</param>
	public static Texture LoadFlipbook( string name, IReadOnlyList<BitmapData> frames, int columns = 0 )
	{
		var first = frames.FirstOrDefault();
		if ( first is null || frames.Any( f => f.Type != BitmapType.Texture2D || f.Width != first.Width || f.Height != first.Height || !IsSupported( f ) )
			|| !System.Numerics.BitOperations.IsPow2( first.Width ) || !System.Numerics.BitOperations.IsPow2( first.Height ) )
			return null;

		var levelCount = frames.Min( f => f.LevelCount );
		columns = columns <= 0 ? frames.Count : Math.Min( columns, frames.Count );
		var rows = (frames.Count + columns - 1) / columns;
		var host = Halo2Mount.Current;
		var loaders = frames.Select( f => new BitmapTextureLoader( f ) ).ToList();
		var pixels = frames.Select( f => host.Textures.Read( f.PixelDataOffset, f.PixelDataSize ) ).ToList();
		var sources = new int[frames.Count];

		var levels = new byte[levelCount][];
		for ( var level = 0; level < levelCount; level++ )
		{
			var width = Math.Max( 1, first.Width >> level );
			var height = Math.Max( 1, first.Height >> level );
			var rowSize = width * 4;
			var atlas = new byte[rowSize * height * columns * rows];
			for ( var f = 0; f < frames.Count; f++ )
			{
				var size = frames[f].GetLevelSize( level );
				var rgba = loaders[f].Convert( pixels[f].AsSpan( sources[f], size ), level, ImageFormat.RGBA8888 );
				sources[f] += size;

				for ( var row = 0; row < height; row++ )
					Array.Copy( rgba, row * rowSize, atlas, (((f / columns) * height + row) * columns + f % columns) * rowSize, rowSize );
			}

			levels[level] = atlas;
		}

		var output = new byte[levels.Sum( l => l.Length )];
		var destination = 0;
		for ( var level = levels.Length - 1; level >= 0; level-- )
		{
			levels[level].CopyTo( output, destination );
			destination += levels[level].Length;
		}

		return Texture.Create( first.Width * columns, first.Height * rows, ImageFormat.RGBA8888 )
			.WithName( name )
			.WithData( output )
			.WithMips( levelCount )
			.WithStaticUsage()
			.Finish();
	}

	/// <summary>
	/// Halo stores the full size level first; s&amp;box wants the smallest first.
	/// </summary>
	byte[] SmallestFirst( byte[] pixels, int levelCount, ImageFormat format )
	{
		var levels = new byte[levelCount][];
		var source = 0;
		for ( var level = 0; level < levelCount; level++ )
		{
			var size = bitmap.GetLevelSize( level );
			levels[level] = Convert( pixels.AsSpan( source, size ), level, format );
			source += size;
		}

		var output = new byte[levels.Sum( l => l.Length )];
		var destination = 0;
		for ( var level = levels.Length - 1; level >= 0; level-- )
		{
			levels[level].CopyTo( output, destination );
			destination += levels[level].Length;
		}

		return output;
	}

	/// <summary>
	/// Cube maps become a 2D atlas with the six faces side by side (+X, -X, +Y, -Y, +Z, -Z), full size only,
	/// as in the Halo CE mount. Halo 2 stores every face of a level before the next level; the face order
	/// (+X, +Y, -X, -Y, +Z, -Z, as in Halo CE) is still to be checked in engine.
	/// </summary>
	Texture LoadCubeMap( byte[] pixels, ImageFormat format )
	{
		ReadOnlySpan<int> storedFace = [0, 2, 1, 3, 4, 5];

		var faceSize = bitmap.GetLevelSize( 0 );
		var faces = new byte[6][];
		for ( var face = 0; face < 6; face++ )
			faces[face] = Convert( pixels.AsSpan( storedFace[face] * faceSize, faceSize ), 0, format );

		// Interleave the faces row by row; for block compressed data a row is a row of 4x4 blocks.
		var rows = format != ImageFormat.RGBA8888 ? (bitmap.Height + 3) / 4 : bitmap.Height;
		var rowSize = faces[0].Length / rows;

		var atlas = new byte[faces[0].Length * 6];
		for ( var row = 0; row < rows; row++ )
		for ( var face = 0; face < 6; face++ )
			Array.Copy( faces[face], row * rowSize, atlas, (row * 6 + face) * rowSize, rowSize );

		return Texture.Create( bitmap.Width * 6, bitmap.Height, format )
			.WithName( Path )
			.WithData( atlas )
			.WithStaticUsage()
			.Finish();
	}

	// The full size image of a 2D bitmap as RGBA8888, from its pixels in textures.dat.
	public byte[] TopLevelRgba( byte[] pixels ) => Convert( pixels.AsSpan( 0, bitmap.GetLevelSize( 0 ) ), 0, ImageFormat.RGBA8888 );

	/// <summary>
	/// Converts one level (every slice of it, for 3D textures) to the format it's uploaded in.
	/// </summary>
	byte[] Convert( ReadOnlySpan<byte> data, int level, ImageFormat format )
	{
		if ( format != ImageFormat.RGBA8888 )
			return data.ToArray();

		var width = Math.Max( 1, bitmap.Width >> level );
		var height = Math.Max( 1, bitmap.Height >> level );
		var depth = bitmap.Type == BitmapType.Texture3D ? Math.Max( 1, bitmap.Depth >> level ) : 1;
		var count = width * height * depth;
		var rgba = new byte[count * 4];

		switch ( bitmap.Format )
		{
			case BitmapFormat.Dxt1:
				global::Blam.Halo1.BitmapDecoder.Dxt1ToRgba8888( data, rgba, width, height );
				break;

			case BitmapFormat.Dxt3:
				global::Blam.Halo1.BitmapDecoder.Dxt3ToRgba8888( data, rgba, width, height );
				break;

			case BitmapFormat.Dxt5:
				global::Blam.Halo1.BitmapDecoder.Dxt5ToRgba8888( data, rgba, width, height );
				break;

			// Two signed bytes (u, v): stored biased by 128 in red and green, like an unsigned normal map.
			case BitmapFormat.V8U8:
				for ( var i = 0; i < count; i++ )
				{
					rgba[i * 4] = (byte)((sbyte)data[i * 2] + 128);
					rgba[i * 4 + 1] = (byte)((sbyte)data[i * 2 + 1] + 128);
					rgba[i * 4 + 2] = 255;
					rgba[i * 4 + 3] = 255;
				}
				break;

			// Two unsigned bytes: green in the high byte, blue in the low one (the Xbox layout; not checked in engine).
			case BitmapFormat.G8B8:
				for ( var i = 0; i < count; i++ )
				{
					rgba[i * 4 + 1] = data[i * 2 + 1];
					rgba[i * 4 + 2] = data[i * 2];
					rgba[i * 4 + 3] = 255;
				}
				break;

			default:
				global::Blam.Halo1.BitmapDecoder.ToRgba8888( (Halo1Format)bitmap.Format, data, rgba, count );
				break;
		}

		return rgba;
	}
}
