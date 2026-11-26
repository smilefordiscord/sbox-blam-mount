using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using Blam.Halo3.Tags;

namespace Blam.Halo3;

// The 68 byte control data of a bitmap_texture_interop_resource (bitmaps.md section 2).
public readonly record struct BitmapInterop( int PrimarySize, int SecondarySize, int Width, int Height, int Depth, int LevelCount, BitmapType Type, bool HighRes, DxgiFormat Format, BitmapCurve Curve )
{
	public const int Size = 68;

	public static BitmapInterop? Parse( ReadOnlySpan<byte> control )
	{
		if ( control.Length < Size )
			return null;
		return new BitmapInterop( BitConverter.ToInt32( control ), BitConverter.ToInt32( control[0x14..] ), BitConverter.ToUInt16( control[0x28..] ), BitConverter.ToUInt16( control[0x2A..] ),
			control[0x2C], control[0x2D], (BitmapType)control[0x2E], control[0x2F] != 0, (DxgiFormat)BitConverter.ToUInt32( control[0x30..] ), (BitmapCurve)control[0x35] );
	}
}

public enum BitmapState
{
	Available,

	// The full size level's page is missing; the smaller levels can still be used.
	MissingHighRes,

	// The handle points at a dead zone entry: the map kept the tag but not its pixels.
	Stripped,

	// A live resource whose data has no page (container.md section 10.7).
	Pageless,
	NoResource,
}

// Whether a bitmap's pixels can be read, from tag data only (zone and play tags), without inflating any page.
public sealed class BitmapAvailability
{
	readonly record struct Page( short SharedFile, int Offset, int CompressedSize );
	readonly record struct Segment( short PrimaryPage, short SecondaryPage );

	static readonly ConditionalWeakTable<CacheFile, BitmapAvailability> Tables = new();

	readonly CacheFile _cache;
	readonly Page[] _pages;
	readonly Segment[] _segments;
	readonly bool[] _sharedFileExists;

	BitmapAvailability( CacheFile cache )
	{
		_cache = cache;
		var play = cache.GetGlobal( "play" ) ?? throw new InvalidDataException( $"'{cache.Name}' has no play tag" );
		var p = play.Data;

		var files = p.Block( 0x0C, 0x108 );
		_sharedFileExists = new bool[files.Count];
		for ( var i = 0; i < files.Count; i++ )
		{
			var name = Path.GetFileName( files[i].Cache.File.CString( files[i].Offset, 256 ).Replace( '\\', '/' ) );
			_sharedFileExists[i] = File.Exists( Path.Combine( cache.Directory, name ) );
		}

		var pages = p.Block( 0x18, 0x58 );
		_pages = new Page[pages.Count];
		for ( var i = 0; i < pages.Count; i++ )
			_pages[i] = new Page( pages[i].I16( 0x04 ), pages[i].I32( 0x08 ), pages[i].I32( 0x0C ) );

		var segments = p.Block( 0x3C, 0x10 );
		_segments = new Segment[segments.Count];
		for ( var i = 0; i < segments.Count; i++ )
			_segments[i] = new Segment( segments[i].I16( 0 ), segments[i].I16( 2 ) );
	}

	public static BitmapAvailability For( CacheFile cache ) => Tables.GetValue( cache, c => new BitmapAvailability( c ) );

	public BitmapState Check( BitmapImage image, out BitmapInterop interop )
	{
		interop = default;
		if ( image.Handle == 0xFFFFFFFF )
			return BitmapState.NoResource;

		var resources = _cache.Resources;
		var index = (int)(image.Handle & 0xFFFF);
		if ( index >= resources.Entries.Count )
			return BitmapState.NoResource;
		if ( resources.Get( image.Handle ) is not { } entry )
			return BitmapState.Stripped;
		if ( BitmapInterop.Parse( resources.ControlData( entry ) ) is not { } parsed )
			return BitmapState.NoResource;

		interop = parsed;
		if ( entry.SegmentIndex < 0 || entry.SegmentIndex >= _segments.Length )
			return BitmapState.Pageless;

		var segment = _segments[entry.SegmentIndex];
		if ( interop.PrimarySize > 0 && !HasPage( segment.PrimaryPage ) )
			return BitmapState.Pageless;
		if ( interop.SecondarySize > 0 && !HasPage( segment.SecondaryPage ) )
			return interop.HighRes && interop.LevelCount > 1 && interop.Type == BitmapType.Texture2D ? BitmapState.MissingHighRes : BitmapState.Pageless;
		return BitmapState.Available;
	}

	// The image's primary and secondary page indices (-1 for none), to read images in page order.
	public (int Primary, int Secondary) Pages( BitmapImage image )
	{
		if ( _cache.Resources.Get( image.Handle ) is not { } entry || entry.SegmentIndex < 0 || entry.SegmentIndex >= _segments.Length )
			return (-1, -1);
		var segment = _segments[entry.SegmentIndex];
		return (segment.PrimaryPage, segment.SecondaryPage);
	}

	bool HasPage( int index )
	{
		if ( index < 0 || index >= _pages.Length )
			return false;
		var page = _pages[index];
		if ( page.Offset < 0 || page.CompressedSize <= 0 )
			return false;
		return page.SharedFile < 0 || page.SharedFile < _sharedFileExists.Length && _sharedFileExists[page.SharedFile];
	}
}

// One image's pixels split into levels: Levels[layer][level], level 0 the largest, in the stored DXGI format.
public sealed class BitmapPixels
{
	public BitmapImage Image { get; }
	public DxgiFormat Format { get; }

	// Size of the first level kept; when the high-res page is missing that's the stored level 1.
	public int Width { get; }
	public int Height { get; }
	public int Depth { get; }
	public int LevelCount { get; }
	public int Layers { get; }
	public bool LostHighRes { get; }
	public ReadOnlyMemory<byte>[][] Levels { get; }

	BitmapPixels( BitmapImage image, DxgiFormat format, int width, int height, int depth, int layers, bool lostHighRes, ReadOnlyMemory<byte>[][] levels )
	{
		Image = image;
		Format = format;
		Width = width;
		Height = height;
		Depth = depth;
		Layers = layers;
		LostHighRes = lostHighRes;
		Levels = levels;
		LevelCount = levels.Length > 0 ? levels[0].Length : 0;
	}

	public int LevelWidth( int level ) => Math.Max( 1, Width >> level );
	public int LevelHeight( int level ) => Math.Max( 1, Height >> level );
	public int LevelDepth( int level ) => Image.Type == BitmapType.Texture3D ? Math.Max( 1, Depth >> level ) : 1;

	// Reads and splits an image's pixels; throws BitmapUnavailableException when the map doesn't have them.
	public static BitmapPixels Read( BitmapImage image )
	{
		var cache = image.Tag.Cache;
		var state = BitmapAvailability.For( cache ).Check( image, out var interop );
		if ( state is not (BitmapState.Available or BitmapState.MissingHighRes) )
			throw new BitmapUnavailableException( image, state );
		if ( !BitmapLayout.IsKnown( interop.Format ) )
			throw new NotSupportedException( $"{image.Tag.Path} image {image.Index}: DXGI format {(uint)interop.Format}" );

		var lostHighRes = state == BitmapState.MissingHighRes;
		var data = cache.Resources.Read( image.Handle, interop.PrimarySize, lostHighRes ? 0 : interop.SecondarySize )
			?? throw new BitmapUnavailableException( image, BitmapState.Stripped );
		if ( data.Primary.Length != interop.PrimarySize || !lostHighRes && data.Secondary.Length != interop.SecondarySize )
			throw new BitmapUnavailableException( image, BitmapState.Pageless );

		return Split( image, interop, data.Primary, lostHighRes ? ReadOnlyMemory<byte>.Empty : data.Secondary, lostHighRes );
	}

	// Splits per bitmaps.md section 4: high-res level 0 in the secondary data; cube maps mip-major, arrays layer-major.
	public static BitmapPixels Split( BitmapImage image, BitmapInterop interop, ReadOnlyMemory<byte> primary, ReadOnlyMemory<byte> secondary, bool lostHighRes )
	{
		var format = interop.Format;
		var type = interop.Type;
		var layers = type switch
		{
			BitmapType.CubeMap => 6,
			BitmapType.Array => Math.Max( 1, interop.Depth ),
			_ => 1,
		};
		var depth = type == BitmapType.Texture3D ? Math.Max( 1, interop.Depth ) : 1;
		var levelCount = Math.Max( 1, interop.LevelCount );
		var first = interop.HighRes ? 1 : 0;

		int Size( int k ) => BitmapLayout.LevelSize( format, Math.Max( 1, interop.Width >> k ), Math.Max( 1, interop.Height >> k ) ) * (type == BitmapType.Texture3D ? Math.Max( 1, depth >> k ) : 1);

		var levels = new ReadOnlyMemory<byte>[layers][];
		for ( var a = 0; a < layers; a++ )
			levels[a] = new ReadOnlyMemory<byte>[levelCount];

		if ( interop.HighRes && !lostHighRes )
		{
			for ( var a = 0; a < layers; a++ )
				levels[a][0] = Take( secondary, a * Size( 0 ), Size( 0 ), image );
		}

		var offset = 0;
		if ( type == BitmapType.CubeMap )
		{
			for ( var k = first; k < levelCount; k++ )
			for ( var a = 0; a < layers; a++ )
			{
				levels[a][k] = Take( primary, offset, Size( k ), image );
				offset += Size( k );
			}
		}
		else
		{
			for ( var a = 0; a < layers; a++ )
			for ( var k = first; k < levelCount; k++ )
			{
				levels[a][k] = Take( primary, offset, Size( k ), image );
				offset += Size( k );
			}
		}

		if ( lostHighRes )
		{
			for ( var a = 0; a < layers; a++ )
				levels[a] = levels[a][1..];
			return new BitmapPixels( image, format, Math.Max( 1, interop.Width >> 1 ), Math.Max( 1, interop.Height >> 1 ), depth, layers, true, levels );
		}

		return new BitmapPixels( image, format, interop.Width, interop.Height, depth, layers, false, levels );
	}

	static ReadOnlyMemory<byte> Take( ReadOnlyMemory<byte> data, int offset, int size, BitmapImage image )
	{
		if ( offset + size > data.Length )
			throw new InvalidDataException( $"{image.Tag.Path} image {image.Index}: level data ends at {offset + size}, past {data.Length}" );
		return data.Slice( offset, size );
	}
}

public sealed class BitmapUnavailableException( BitmapImage image, BitmapState state )
	: Exception( $"{image.Tag.Path} image {image.Index} has no pixels in {image.Tag.Cache.Name} ({state})" )
{
	public BitmapState State { get; } = state;
}

// Level sizes of the DXGI formats Halo 3 uses: plain D3D layout, block compressed levels rounded up to whole 4x4 blocks.
public static class BitmapLayout
{
	public static bool IsKnown( DxgiFormat format ) => BytesPerElement( format ) > 0;

	public static bool IsBlockCompressed( DxgiFormat format ) => format is >= DxgiFormat.BC1Unorm and <= DxgiFormat.BC5Snorm;

	// Bytes per 4x4 block for block compressed formats, else per pixel; 0 for formats Halo 3 doesn't use.
	public static int BytesPerElement( DxgiFormat format ) => format switch
	{
		DxgiFormat.BC1Unorm or DxgiFormat.BC1UnormSrgb or DxgiFormat.BC4Unorm or DxgiFormat.BC4Snorm => 8,
		DxgiFormat.BC2Unorm or DxgiFormat.BC2UnormSrgb or DxgiFormat.BC3Unorm or DxgiFormat.BC3UnormSrgb or DxgiFormat.BC5Unorm or DxgiFormat.BC5Snorm => 16,
		DxgiFormat.R32G32B32A32Float => 16,
		DxgiFormat.R16G16B16A16Float => 8,
		DxgiFormat.R8G8B8A8Unorm or DxgiFormat.R8G8B8A8Snorm or DxgiFormat.B8G8R8A8Unorm or DxgiFormat.B8G8R8X8Unorm or DxgiFormat.B8G8R8A8UnormSrgb or DxgiFormat.B8G8R8X8UnormSrgb => 4,
		DxgiFormat.R8G8Unorm or DxgiFormat.R8G8Snorm or DxgiFormat.B4G4R4A4Unorm => 2,
		DxgiFormat.R8Unorm or DxgiFormat.A8Unorm => 1,
		_ => 0,
	};

	public static int LevelSize( DxgiFormat format, int width, int height ) => IsBlockCompressed( format )
		? Math.Max( 1, (width + 3) / 4 ) * Math.Max( 1, (height + 3) / 4 ) * BytesPerElement( format )
		: width * height * BytesPerElement( format );

	public static bool IsSrgb( DxgiFormat format ) => format is DxgiFormat.BC1UnormSrgb or DxgiFormat.BC2UnormSrgb or DxgiFormat.BC3UnormSrgb
		or DxgiFormat.B8G8R8A8UnormSrgb or DxgiFormat.B8G8R8X8UnormSrgb;
}
