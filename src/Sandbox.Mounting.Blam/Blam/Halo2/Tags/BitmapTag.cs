using System;
using System.Collections.Generic;

namespace Blam.Halo2.Tags;

public enum BitmapType : ushort
{
	Texture2D = 0,
	Texture3D = 1,
	CubeMap = 2,
}

/// <summary>
/// Halo 2's bitmap formats. The same ids as Halo CE up to P8 bump; 0x12 is P8 here (BC7 in MCC's Halo CE).
/// </summary>
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
	P8 = 0x12,
	ArgbFp32 = 0x13,
	RgbFp32 = 0x14,
	RgbFp16 = 0x15,
	V8U8 = 0x16,
	G8B8 = 0x17,
}

/// <summary>
/// One bitmap of a bitmap tag (the 0xA8 byte "bitmaps" elements).
/// </summary>
public sealed record BitmapData(
	int Index,
	int Width,
	int Height,
	int Depth,
	BitmapType Type,
	BitmapFormat Format,
	int MipmapCount,
	uint PixelDataOffset,
	int StoredSize )
{
	/// <summary>
	/// Mip levels including the full size image. The tag's mipmap count excludes it.
	/// </summary>
	public int LevelCount => MipmapCount + 1;

	public int FaceCount => Type == BitmapType.CubeMap ? 6 : 1;

	public bool IsBlockCompressed => Format is BitmapFormat.Dxt1 or BitmapFormat.Dxt3 or BitmapFormat.Dxt5;

	/// <summary>
	/// Bytes per pixel for uncompressed formats, or bytes per 4x4 block for block compressed ones; 0 if unknown.
	/// </summary>
	public int ElementSize => Format switch
	{
		BitmapFormat.A8 or BitmapFormat.Y8 or BitmapFormat.AY8 or BitmapFormat.P8Bump or BitmapFormat.P8 => 1,
		BitmapFormat.A8Y8 or BitmapFormat.R5G6B5 or BitmapFormat.A1R5G5B5 or BitmapFormat.A4R4G4B4 or BitmapFormat.V8U8 or BitmapFormat.G8B8 => 2,
		BitmapFormat.X8R8G8B8 or BitmapFormat.A8R8G8B8 => 4,
		BitmapFormat.RgbFp16 => 6,
		BitmapFormat.RgbFp32 => 12,
		BitmapFormat.ArgbFp32 => 16,
		BitmapFormat.Dxt1 => 8,
		BitmapFormat.Dxt3 or BitmapFormat.Dxt5 => 16,
		_ => 0,
	};

	/// <summary>
	/// Size in bytes of one mip level of one face (every slice of it, for 3D textures).
	/// </summary>
	public int GetLevelSize( int level )
	{
		var width = Math.Max( 1, Width >> level );
		var height = Math.Max( 1, Height >> level );
		var depth = Type == BitmapType.Texture3D ? Math.Max( 1, Depth >> level ) : 1;

		if ( IsBlockCompressed )
			return ((width + 3) / 4) * ((height + 3) / 4) * depth * ElementSize;

		return width * height * depth * ElementSize;
	}

	/// <summary>
	/// The size of the whole pixel data: every level of every face.
	/// </summary>
	public int PixelDataSize
	{
		get
		{
			var size = 0;
			for ( var level = 0; level < LevelCount; level++ )
				size += GetLevelSize( level );
			return size * FaceCount;
		}
	}
}

/// <summary>
/// A rectangle of one of a sprite bitmap's pages (texture coordinates 0-1), and the point (in the same units) it's
/// placed by.
/// </summary>
public sealed record BitmapSprite( int BitmapIndex, float Left, float Right, float Top, float Bottom, float RegistrationX, float RegistrationY )
{
	// The sprite's rectangle in pixels of its full size page, at least one pixel.
	public (int X, int Y, int Width, int Height) GetPixelRect( BitmapData page ) =>
		Halo1.SpriteCrop.PixelRect( Left, Right, Top, Bottom, page.Width, page.Height );
}

/// <summary>
/// A bitmap tag's sequence: a run of its bitmaps, or of sprites on its pages.
/// </summary>
public sealed record BitmapSequence( string Name, int FirstBitmapIndex, int BitmapCount, IReadOnlyList<BitmapSprite> Sprites );

/// <summary>
/// A Halo 2 bitmap tag (<c>bitm</c>): a set of textures, with their pixels in <c>textures.dat</c>.
/// </summary>
public sealed class BitmapTag
{
	public static readonly TagClass Class = new( "bitm" );

	const int SequencesBlock = 0x3C;
	const int BitmapsBlock = 0x44;
	const int SequenceSize = 0x3C;
	const int SpriteSize = 0x20;
	const int BitmapSize = 0xA8;

	public TagEntry Tag { get; }
	public IReadOnlyList<BitmapData> Bitmaps { get; }

	/// <summary>
	/// The sequences, with their sprites for a sprite bitmap (decal and particle sheets).
	/// </summary>
	public IReadOnlyList<BitmapSequence> Sequences { get; }

	public BitmapTag( TagEntry tag )
	{
		Tag = tag;
		var data = tag.Cache.TagData;
		var bitmaps = new List<BitmapData>();

		var index = 0;
		foreach ( var e in data.Elements( tag.Address + BitmapsBlock, BitmapSize ) )
		{
			bitmaps.Add( new BitmapData(
				Index: index++,
				Width: data.I16( e + 0x4 ),
				Height: data.I16( e + 0x6 ),
				Depth: data.U8( e + 0x8 ),
				Type: (BitmapType)data.U16( e + 0xA ),
				Format: (BitmapFormat)data.U16( e + 0xC ),
				MipmapCount: data.I16( e + 0x14 ),
				PixelDataOffset: data.U32( e + 0x38 ),
				StoredSize: data.I32( e + 0x50 ) ) );
		}

		Bitmaps = bitmaps;

		// Sequence: name 0x0 (32 chars), first bitmap 0x20, bitmap count 0x22, sprites 0x34. Sprite: bitmap 0x0,
		// left 0x8, right 0xC, top 0x10, bottom 0x14, registration point 0x18.
		var sequences = new List<BitmapSequence>();
		foreach ( var e in data.Elements( tag.Address + SequencesBlock, SequenceSize ) )
		{
			var sprites = new List<BitmapSprite>();
			foreach ( var s in data.Elements( e + 0x34, SpriteSize ) )
				sprites.Add( new BitmapSprite( data.I16( s ), data.F32( s + 0x8 ), data.F32( s + 0xC ), data.F32( s + 0x10 ), data.F32( s + 0x14 ), data.F32( s + 0x18 ), data.F32( s + 0x1C ) ) );
			sequences.Add( new BitmapSequence( data.CString( e, 32 ), data.I16( e + 0x20 ), data.I16( e + 0x22 ), sprites ) );
		}

		Sequences = sequences;
	}
}
