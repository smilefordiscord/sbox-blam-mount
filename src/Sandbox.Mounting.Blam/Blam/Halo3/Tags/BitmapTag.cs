using System;
using System.Collections.Generic;

namespace Blam.Halo3.Tags;

public enum BitmapType : short
{
	Texture2D = 0,
	Texture3D = 1,
	CubeMap = 2,
	Array = 3,
}

// The tag's own format enum; the pixels' real format is the DXGI format in the interop resource (DxgiFormat).
public enum BitmapFormat : short
{
	A8 = 0,
	Y8 = 1,
	AY8 = 2,
	A8Y8 = 3,
	R5G6B5 = 6,
	A1R5G5B5 = 8,
	A4R4G4B4 = 9,
	X8R8G8B8 = 10,
	A8R8G8B8 = 11,
	Dxt1 = 14,
	Dxt3 = 15,
	Dxt5 = 16,
	A4R4G4B4Font = 17,
	RgbFp32 = 20,
	V8U8 = 22,
	G8B8 = 23,
	AbgrFp32 = 24,
	AbgrFp16 = 25,
	Q8W8V8U8 = 26,
	A2R10G10B10 = 27,
	A16B16G16R16 = 28,
	V16U16 = 29,
	Dxt3A = 30,
	Dxt5A = 31,
	Dxt3A1111 = 32,
	Dxn = 33,
	Ctx1 = 34,
	Dxt3AAlpha = 35,
	Dxt3AMono = 36,
	Dxt5AAlpha = 37,
	Dxt5AMono = 38,
	DxnMonoAlpha = 39,
}

public enum BitmapCurve : byte
{
	Unknown = 0,
	XRgb = 1,
	Gamma2 = 2,
	Linear = 3,
	OffsetLog = 4,
	Srgb = 5,
}

// The DXGI formats MCC creates Halo 3 textures with (interop control +0x30).
public enum DxgiFormat : uint
{
	Unknown = 0,
	R32G32B32A32Float = 2,
	R16G16B16A16Float = 10,
	R8G8B8A8Unorm = 28,
	R8G8B8A8Snorm = 31,
	R8G8Unorm = 49,
	R8G8Snorm = 51,
	R8Unorm = 61,
	A8Unorm = 65,
	BC1Unorm = 71,
	BC1UnormSrgb = 72,
	BC2Unorm = 74,
	BC2UnormSrgb = 75,
	BC3Unorm = 77,
	BC3UnormSrgb = 78,
	BC4Unorm = 80,
	BC4Snorm = 81,
	BC5Unorm = 83,
	BC5Snorm = 84,
	B8G8R8A8Unorm = 87,
	B8G8R8X8Unorm = 88,
	B8G8R8A8UnormSrgb = 91,
	B8G8R8X8UnormSrgb = 93,
	B4G4R4A4Unorm = 115,
}

// One image of a bitmap tag (the 0x38 byte entries at +0x60) and its hardware texture handle (+0x8C).
public sealed record BitmapImage(
	TagEntry Tag,
	int Index,
	int Width,
	int Height,
	int Depth,
	byte MoreFlags,
	BitmapType Type,
	BitmapFormat Format,
	ushort Flags,
	int MipmapCount,
	BitmapCurve Curve,
	uint Handle )
{
	// Mip levels including the full size one; the tag's count leaves it out.
	public int LevelCount => MipmapCount + 1;

	// Faces of a cube map, layers of an array, 1 otherwise (a 3D texture's slices are part of each level).
	public int Layers => Type switch
	{
		BitmapType.CubeMap => 6,
		BitmapType.Array => Math.Max( 1, Depth ),
		_ => 1,
	};

	// Level 0 is in the resource's secondary data and the smaller levels in its primary data.
	public bool HasHighResData => (MoreFlags & 0x20) != 0;

	// MCC samples it through an sRGB view: colour formats with curve unknown, xRGB or sRGB (bitmaps.md section 3).
	public bool IsSrgb => Format is BitmapFormat.A8R8G8B8 or BitmapFormat.X8R8G8B8 or BitmapFormat.Dxt1 or BitmapFormat.Dxt3 or BitmapFormat.Dxt5
		&& Curve is BitmapCurve.Unknown or BitmapCurve.XRgb or BitmapCurve.Srgb;
}

// A bitmap tag (bitm): its images and their hardware texture handles.
public sealed class BitmapTag
{
	public static readonly TagClass Class = new( "bitm" );

	public TagEntry Tag { get; }
	public int Usage { get; }
	public IReadOnlyList<BitmapImage> Images { get; }

	public BitmapTag( TagEntry tag )
	{
		Tag = tag;
		var data = tag.Data;
		if ( data.IsNull )
		{
			Images = Array.Empty<BitmapImage>();
			return;
		}

		Usage = data.I32( 0x00 );
		var images = data.Block( 0x60, 0x38 );
		var handles = data.Block( 0x8C, 8 );
		var list = new BitmapImage[images.Count];
		for ( var i = 0; i < images.Count; i++ )
		{
			var e = images[i];
			var handle = i < handles.Count ? handles[i].U32( 0 ) : 0xFFFFFFFF;
			list[i] = new BitmapImage( tag, i, e.I16( 0x04 ), e.I16( 0x06 ), e.U8( 0x08 ), e.U8( 0x09 ), (BitmapType)e.I16( 0x0A ),
				(BitmapFormat)e.I16( 0x0C ), e.U16( 0x0E ), e.U8( 0x14 ), (BitmapCurve)e.U8( 0x15 ), handle );
		}
		Images = list;
	}

	public static IReadOnlyList<BitmapImage> ImagesOf( TagEntry tag ) => new BitmapTag( tag ).Images;
}
