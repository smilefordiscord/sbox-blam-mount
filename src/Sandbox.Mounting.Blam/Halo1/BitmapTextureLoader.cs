using Blam.Halo1;
using Blam.Halo1.Tags;

namespace Sandbox.Mounting.Blam.Halo1;

/// <summary>
/// Turns one bitmap from a Halo CE bitmap tag into a texture.
/// </summary>
sealed class BitmapTextureLoader( string mapPath, BitmapData bitmap ) : HaloCeLoader
{
	string _name;

	public static bool IsSupported( BitmapData bitmap ) =>
		bitmap.Width > 0 && bitmap.Height > 0 && bitmap.Type switch
		{
			BitmapType.Texture2D or BitmapType.CubeMap => bitmap.IsBlockCompressed || BitmapDecoder.CanDecode( bitmap.Format ),

			// Every stock volume is uncompressed (A8Y8 plasma noise, AY8 light attenuation, X8R8G8B8 defaults).
			BitmapType.Texture3D => bitmap.Depth > 0 && BitmapDecoder.CanDecode( bitmap.Format ),
			_ => false,
		};

	protected override object Load() => Create( Host, mapPath, bitmap, Path );

	/// <summary>
	/// Makes the texture directly, outside the resource system (see <see cref="MaterialTextures.PrivateCopies"/>).
	/// </summary>
	public static Texture Create( HaloCeMount host, string mapPath, BitmapData bitmap, string name ) =>
		new BitmapTextureLoader( mapPath, bitmap ).Create( host, name );

	Texture Create( HaloCeMount host, string name )
	{
		_name = name;
		var pixels = ReadPixels( host, mapPath, bitmap );
		var format = UploadFormat( bitmap.Format, pixels );

		return bitmap.Type switch
		{
			BitmapType.CubeMap => LoadCubeMap( pixels, format ),
			BitmapType.Texture3D => LoadVolume( pixels ),
			_ => Load2D( pixels, format ),
		};
	}

	/// <summary>
	/// The format a bitmap goes to the GPU in. DXT3 textures come back empty in s&amp;box (Source 2
	/// seems not to support BC2), and s&amp;box's DXT1 has no alpha: a three-color block's
	/// transparent texels come back opaque black, where Halo's are transparent (b40's control room
	/// glass, whose diffuse map is all transparent, covered its panels in black). Those are decoded
	/// like the uncompressed formats; the other block formats go to the GPU untouched.
	/// </summary>
	static ImageFormat UploadFormat( BitmapFormat format, ReadOnlySpan<byte> pixels ) => format switch
	{
		BitmapFormat.Dxt1 when !BitmapDecoder.Dxt1HasTransparency( pixels ) => ImageFormat.DXT1,
		BitmapFormat.Dxt5 => ImageFormat.DXT5,
		BitmapFormat.Bc7 => ImageFormat.BC7,
		_ => ImageFormat.RGBA8888,
	};

	/// <summary>
	/// A bitmap's stored pixels (every face and level), from <c>bitmaps.map</c> or the map itself.
	/// </summary>
	internal static byte[] ReadPixels( HaloCeMount host, string mapPath, BitmapData bitmap ) => bitmap.IsExternal
		? host.Bitmaps.Read( bitmap.PixelDataOffset, bitmap.PixelDataSize )
		: host.ReadMap( mapPath, bitmap.PixelDataOffset, bitmap.PixelDataSize );

	Texture Load2D( byte[] pixels, ImageFormat format )
	{
		// Block compressed mips smaller than one 4x4 block come out black in s&box, so those
		// chains stop at 4x4. The GPU clamps to the smallest level, so distant surfaces look the same.
		var levelCount = bitmap.LevelCount;
		if ( format != ImageFormat.RGBA8888 )
		{
			while ( levelCount > 1 && Math.Min( bitmap.Width >> (levelCount - 1), bitmap.Height >> (levelCount - 1) ) < 4 )
				levelCount--;
		}

		// Halo stores the full size image first and the smallest mip last. s&box wants the
		// smallest first, so the levels are written into the output back to front.
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

		return Texture.Create( bitmap.Width, bitmap.Height, format )
			.WithName( _name )
			.WithData( output )
			.WithMips( levelCount )
			.WithStaticUsage()
			.Finish();
	}

	/// <summary>
	/// 3D textures become volume textures with their full mip chain. The only stock ones are the
	/// shield plasma noise (sampled by shader_transparent_plasma, which nothing draws at rest) and
	/// the rasterizer's light attenuation and default volumes.
	/// </summary>
	/// <remarks>
	/// Each level is stored linearly (x fastest, then y, then z slices), unswizzled, and depth
	/// halves with every level like width and height: each level is a 2x2x2 box filtered copy of
	/// the one before (<c>BlamInspect &lt;map&gt; volumecheck &lt;bitmap&gt;</c>). s&amp;box wants
	/// the smallest level first, as for 2D textures.
	/// </remarks>
	Texture LoadVolume( byte[] pixels )
	{
		var levels = new byte[bitmap.LevelCount][];
		var source = 0;
		for ( var level = 0; level < levels.Length; level++ )
		{
			var size = bitmap.GetLevelSize( level );
			levels[level] = Convert( pixels.AsSpan( source, size ), level, ImageFormat.RGBA8888 );
			source += size;
		}

		var output = new byte[levels.Sum( l => l.Length )];
		var destination = 0;
		for ( var level = levels.Length - 1; level >= 0; level-- )
		{
			levels[level].CopyTo( output, destination );
			destination += levels[level].Length;
		}

		return Texture.CreateVolume( bitmap.Width, bitmap.Height, bitmap.Depth, ImageFormat.RGBA8888 )
			.WithName( Path )
			.WithData( output )
			.WithMips( levels.Length )
			.WithStaticUsage()
			.Finish();
	}

	/// <summary>
	/// Cube maps become a 2D atlas with the six faces side by side (+X, -X, +Y, -Y, +Z, -Z), which
	/// the shaders sample with their own face lookup. s&amp;box doesn't bind cube textures set on a
	/// runtime material, and 2D textures work everywhere.
	/// </summary>
	/// <remarks>
	/// Halo stores cube maps level by level, with all six faces of a level together, so the full
	/// size faces are the first six images. Only those are used: cube maps are only used for
	/// reflections, where the missing mips aren't noticeable. The faces are stored in the order
	/// +X, +Y, -X, -Y, +Z, -Z, each laid out by Direct3D's rules for its direction.
	/// </remarks>
	Texture LoadCubeMap( byte[] pixels, ImageFormat format )
	{
		// Where each atlas face (+X, -X, +Y, -Y, +Z, -Z) is stored.
		ReadOnlySpan<int> storedFace = [0, 2, 1, 3, 4, 5];

		var faceSize = bitmap.GetLevelSize( 0 );
		var faces = new byte[6][];
		for ( var face = 0; face < 6; face++ )
			faces[face] = Convert( pixels.AsSpan( storedFace[face] * faceSize, faceSize ), 0, format );

		// Interleave the faces row by row. Block compressed data is laid out in rows of 4x4
		// blocks, so for those a "row" is one row of blocks.
		var compressed = format != ImageFormat.RGBA8888;
		var rows = compressed ? (bitmap.Height + 3) / 4 : bitmap.Height;
		var rowSize = faces[0].Length / rows;

		var atlas = new byte[faces[0].Length * 6];
		for ( var row = 0; row < rows; row++ )
		for ( var face = 0; face < 6; face++ )
			Array.Copy( faces[face], row * rowSize, atlas, (row * 6 + face) * rowSize, rowSize );

		return Texture.Create( bitmap.Width * 6, bitmap.Height, format )
			.WithName( _name )
			.WithData( atlas )
			.WithStaticUsage()
			.Finish();
	}

	/// <summary>
	/// A bitmap tag's images side by side in one texture, left to right, for a shader that picks
	/// one of them as it draws (numeric shaders' digits). Only the full size level, laid out like
	/// the cube map atlas. Null unless every image is a 2D texture of the same size and format.
	/// </summary>
	public static Texture CreateStrip( HaloCeMount host, string mapPath, IReadOnlyList<BitmapData> bitmaps, string name )
	{
		var first = bitmaps.FirstOrDefault();
		if ( first is null || bitmaps.Any( b => b.Type != BitmapType.Texture2D || !IsSupported( b ) || b.Width != first.Width || b.Height != first.Height || b.Format != first.Format ) )
			return null;

		var pixels = bitmaps.Select( b => ReadPixels( host, mapPath, b ).AsMemory( 0, b.GetLevelSize( 0 ) ) ).ToList();
		var formats = pixels.Select( p => UploadFormat( first.Format, p.Span ) ).Distinct().ToList();
		var format = formats.Count == 1 ? formats[0] : ImageFormat.RGBA8888;

		var images = bitmaps.Select( ( b, i ) => new BitmapTextureLoader( mapPath, b ).Convert( pixels[i].Span, 0, format ) ).ToList();

		// Block compressed data is laid out in rows of 4x4 blocks, so for those a "row" is one row of blocks.
		var rows = format != ImageFormat.RGBA8888 ? (first.Height + 3) / 4 : first.Height;
		var rowSize = images[0].Length / rows;
		var strip = new byte[images[0].Length * images.Count];
		for ( var row = 0; row < rows; row++ )
		for ( var image = 0; image < images.Count; image++ )
			Array.Copy( images[image], row * rowSize, strip, (row * images.Count + image) * rowSize, rowSize );

		return Texture.Create( first.Width * images.Count, first.Height, format )
			.WithName( name )
			.WithData( strip )
			.WithStaticUsage()
			.Finish();
	}

	/// <summary>
	/// Converts one image of one mip level (every slice of it, for 3D textures) to the format it's uploaded in.
	/// </summary>
	byte[] Convert( ReadOnlySpan<byte> data, int level, ImageFormat format )
	{
		if ( format != ImageFormat.RGBA8888 )
			return data.ToArray();

		var width = Math.Max( 1, bitmap.Width >> level );
		var height = Math.Max( 1, bitmap.Height >> level );
		var depth = bitmap.Type == BitmapType.Texture3D ? Math.Max( 1, bitmap.Depth >> level ) : 1;
		var rgba = new byte[width * height * depth * 4];

		if ( bitmap.Format == BitmapFormat.Dxt3 )
			BitmapDecoder.Dxt3ToRgba8888( data, rgba, width, height );
		else if ( bitmap.Format == BitmapFormat.Dxt1 )
			BitmapDecoder.Dxt1ToRgba8888( data, rgba, width, height );
		else
			BitmapDecoder.ToRgba8888( bitmap.Format, data, rgba, width * height * depth );

		return rgba;
	}
}
