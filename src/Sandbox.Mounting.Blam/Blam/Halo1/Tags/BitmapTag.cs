using System;
using System.Collections.Generic;
using System.IO;

namespace Blam.Halo1.Tags;

public enum BitmapType : ushort
{
	Texture2D = 0,
	Texture3D = 1,
	CubeMap = 2,
	White = 3,
}

public enum BitmapFormat : ushort
{
	A8 = 0x0,
	Y8 = 0x1,
	AY8 = 0x2,
	A8Y8 = 0x3,
	R5G6B5 = 0x6,
	A1R5G5B5 = 0x8,
	A4R4G4B4 = 0x9,
	X8R8G8B8 = 0xA,
	A8R8G8B8 = 0xB,
	Dxt1 = 0xE,
	Dxt3 = 0xF,
	Dxt5 = 0x10,
	P8Bump = 0x11,
	Bc7 = 0x12,
}

[Flags]
public enum BitmapFlags : ushort
{
	PowerOfTwoDimensions = 0x1,
	Compressed = 0x2,
	Palettized = 0x4,
	Swizzled = 0x8,
	Linear = 0x10,
	V16U16 = 0x20,
	MakeItActuallyWork = 0x80,

	/// <summary>
	/// The pixel data offset points into <c>bitmaps.map</c> instead of the cache file.
	/// </summary>
	External = 0x100,
	Stubbed = 0x200,
}

/// <summary>
/// One bitmap's pixel data description, from a bitmap tag's "bitmap data" block (48 bytes each).
/// </summary>
public sealed record BitmapData(
	int Index,
	int Width,
	int Height,
	int Depth,
	BitmapType Type,
	BitmapFormat Format,
	BitmapFlags Flags,
	int MipmapCount,
	uint PixelDataOffset,
	int PixelDataSize )
{
	public bool IsExternal => Flags.HasFlag( BitmapFlags.External );

	/// <summary>
	/// Mip levels including the full size image. The tag's mipmap count excludes it.
	/// </summary>
	public int LevelCount => MipmapCount + 1;

	public int FaceCount => Type == BitmapType.CubeMap ? 6 : 1;

	public bool IsBlockCompressed => Format is BitmapFormat.Dxt1 or BitmapFormat.Dxt3 or BitmapFormat.Dxt5 or BitmapFormat.Bc7;

	/// <summary>
	/// Bytes per pixel for uncompressed formats, or bytes per 4x4 block for block compressed ones.
	/// </summary>
	public int ElementSize => Format switch
	{
		BitmapFormat.A8 or BitmapFormat.Y8 or BitmapFormat.AY8 or BitmapFormat.P8Bump => 1,
		BitmapFormat.A8Y8 or BitmapFormat.R5G6B5 or BitmapFormat.A1R5G5B5 or BitmapFormat.A4R4G4B4 => 2,
		BitmapFormat.X8R8G8B8 or BitmapFormat.A8R8G8B8 => 4,
		BitmapFormat.Dxt1 => 8,
		BitmapFormat.Dxt3 or BitmapFormat.Dxt5 or BitmapFormat.Bc7 => 16,
		_ => throw new NotSupportedException( $"Unknown bitmap format {Format}" ),
	};

	/// <summary>
	/// Size in bytes of a single mip level of a single face.
	/// </summary>
	public int GetLevelSize( int level )
	{
		var width = Math.Max( 1, Width >> level );
		var height = Math.Max( 1, Height >> level );
		var depth = Math.Max( 1, Depth >> level );

		if ( IsBlockCompressed )
			return ((width + 3) / 4) * ((height + 3) / 4) * depth * ElementSize;

		return width * height * depth * ElementSize;
	}
}

/// <summary>
/// What a bitmap tag was imported as (the tag's type at 0x0). Each of its bitmaps has its own
/// <see cref="BitmapType"/>: sprite pages and interface bitmaps are 2D textures.
/// </summary>
public enum BitmapTagType : ushort
{
	Textures2D = 0,
	Textures3D = 1,
	CubeMaps = 2,

	/// <summary>
	/// Sprites packed onto pages, with each sequence's sprites giving the rectangles.
	/// </summary>
	Sprites = 3,

	/// <summary>
	/// Like 2D textures, but any size and without mipmaps. Used by the HUD and menus.
	/// </summary>
	InterfaceBitmaps = 4,
}

/// <summary>
/// A sprite: a rectangle of one bitmap, in texture coordinates, and its registration point.
/// </summary>
public sealed record BitmapSprite( int BitmapIndex, float Left, float Right, float Top, float Bottom, float RegistrationX = 0, float RegistrationY = 0 )
{
	/// <summary>
	/// The sprite's rectangle in pixels of the full size <paramref name="bitmap"/>, at least one pixel.
	/// </summary>
	public (int X, int Y, int Width, int Height) GetPixelRect( BitmapData bitmap ) =>
		SpriteCrop.PixelRect( Left, Right, Top, Bottom, bitmap.Width, bitmap.Height );
}

/// <summary>
/// A named run of bitmaps or sprites, e.g. the frames of an animation or a set of decal variations.
/// </summary>
public sealed record BitmapSequence( string Name, int FirstBitmapIndex, int BitmapCount, IReadOnlyList<BitmapSprite> Sprites );

/// <summary>
/// Reader for <c>bitm</c> tags.
/// </summary>
/// <remarks>
/// The bitmap tag is 108 bytes. The fields needed here are the type at 0x0, the "sequences" block at
/// 0x54 (64 bytes each: name, first bitmap index 0x20, bitmap count 0x22, sprites block 0x34 of 32 byte
/// sprites: bitmap index, then left, right, top, bottom at 0x8 and the registration point at 0x18) and
/// the "bitmap data" block at 0x60. A block is a 12 byte count + pointer + definition pointer.
/// </remarks>
public sealed class BitmapTag
{
	public TagEntry Tag { get; }
	public BitmapTagType Kind { get; }
	public IReadOnlyList<BitmapData> Bitmaps { get; }
	public IReadOnlyList<BitmapSequence> Sequences { get; }

	public BitmapTag( CacheFile cache, TagEntry tag )
	{
		if ( tag.Class != TagClasses.Bitmap )
			throw new ArgumentException( $"{tag} is not a bitmap tag", nameof( tag ) );

		Tag = tag;

		var data = cache.TagData;
		Kind = (BitmapTagType)data.U16( tag.Address );
		var count = data.I32( tag.Address + 0x60 );
		var address = data.U32( tag.Address + 0x64 );

		var bitmaps = new BitmapData[count];
		for ( var i = 0; i < count; i++ )
		{
			var entry = address + (uint)(i * 0x30);
			bitmaps[i] = new BitmapData(
				Index: i,
				Width: data.U16( entry + 0x4 ),
				Height: data.U16( entry + 0x6 ),
				Depth: data.U16( entry + 0x8 ),
				Type: (BitmapType)data.U16( entry + 0xA ),
				Format: (BitmapFormat)data.U16( entry + 0xC ),
				Flags: (BitmapFlags)data.U16( entry + 0xE ),
				MipmapCount: data.U16( entry + 0x14 ),
				PixelDataOffset: data.U32( entry + 0x18 ),
				PixelDataSize: data.I32( entry + 0x1C ) );
		}

		Bitmaps = bitmaps;

		var sequences = new List<BitmapSequence>();
		foreach ( var sequence in data.Block( tag.Address + 0x54, 0x40 ) )
		{
			var sprites = new List<BitmapSprite>();
			foreach ( var sprite in data.Block( sequence + 0x34, 0x20 ) )
				sprites.Add( new BitmapSprite( data.I16( sprite ), data.F32( sprite + 0x8 ), data.F32( sprite + 0xC ), data.F32( sprite + 0x10 ), data.F32( sprite + 0x14 ), data.F32( sprite + 0x18 ), data.F32( sprite + 0x1C ) ) );

			sequences.Add( new BitmapSequence( data.CString( sequence, 32 ), data.I16( sequence + 0x20 ), data.I16( sequence + 0x22 ), sprites ) );
		}
		Sequences = sequences;
	}

	/// <summary>
	/// Reads a bitmap's raw pixel data, every face and mip level, as stored by the game.
	/// </summary>
	public static byte[] ReadPixels( CacheFile cache, BitmapData bitmap )
	{
		if ( !bitmap.IsExternal )
			return cache.Read( bitmap.PixelDataOffset, bitmap.PixelDataSize );

		if ( cache.Bitmaps is null )
			throw new InvalidOperationException( $"Bitmap data is in bitmaps.map, but it wasn't provided for '{cache.FilePath}'" );

		return cache.Bitmaps.Read( bitmap.PixelDataOffset, bitmap.PixelDataSize );
	}
}
