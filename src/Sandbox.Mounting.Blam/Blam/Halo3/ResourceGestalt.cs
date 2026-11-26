using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace Blam.Halo3;

// Where a resource's pointers in its control data point: into the control data itself, or its primary or secondary page data.
public enum ResourcePointerKind
{
	Control = 2,
	Primary = 4,
	Secondary = 6,
}

public readonly record struct ResourceFixup( int Field, ResourcePointerKind Kind, int Offset );

public sealed record ResourceEntry( int Index, ushort Salt, uint OwnerId, string Type, int ControlOffset, int ControlSize, int SegmentIndex, int RootOffset, ResourceFixup[] Fixups )
{
	public uint Handle => (uint)Salt << 16 | (uint)Index;
}

// One resource's bytes: its control data (definition struct, pointers zeroed) and its slices of page data.
public sealed class ResourceData
{
	readonly Dictionary<int, ResourceFixup> _fixups;

	public ResourceEntry Entry { get; }
	public byte[] Control { get; }
	public ReadOnlyMemory<byte> Primary { get; }
	public ReadOnlyMemory<byte> Secondary { get; }
	public bool HasPrimary { get; }
	public bool HasSecondary { get; }
	public int Root => Entry.RootOffset;

	internal ResourceData( ResourceEntry entry, byte[] control, ReadOnlyMemory<byte>? primary, ReadOnlyMemory<byte>? secondary )
	{
		Entry = entry;
		Control = control;
		HasPrimary = primary.HasValue;
		HasSecondary = secondary.HasValue;
		Primary = primary ?? ReadOnlyMemory<byte>.Empty;
		Secondary = secondary ?? ReadOnlyMemory<byte>.Empty;
		_fixups = entry.Fixups.GroupBy( f => f.Field ).ToDictionary( g => g.Key, g => g.First() );
	}

	// Where the pointer field at a control data offset points, if anywhere.
	public ResourceFixup? Pointer( int field ) => _fixups.TryGetValue( field, out var fixup ) ? fixup : null;

	// The bytes a pointer field points at (to the end of their data).
	public ReadOnlyMemory<byte> Target( int field )
	{
		if ( Pointer( field ) is not { } fixup )
			return ReadOnlyMemory<byte>.Empty;

		var data = fixup.Kind switch
		{
			ResourcePointerKind.Control => Control,
			ResourcePointerKind.Primary => Primary,
			ResourcePointerKind.Secondary => Secondary,
			_ => ReadOnlyMemory<byte>.Empty,
		};
		return fixup.Offset <= data.Length ? data[fixup.Offset..] : ReadOnlyMemory<byte>.Empty;
	}

	public int I32( int offset ) => BitConverter.ToInt32( Control, offset );
	public uint U32( int offset ) => BitConverter.ToUInt32( Control, offset );
	public short I16( int offset ) => BitConverter.ToInt16( Control, offset );
	public ushort U16( int offset ) => BitConverter.ToUInt16( Control, offset );
	public float F32( int offset ) => BitConverter.ToSingle( Control, offset );
	public byte U8( int offset ) => Control[offset];

	// A tag block in control data (i32 count, pointer, u32): the control offsets of its elements.
	public int[] Block( int field, int elementSize )
	{
		var count = I32( field );
		if ( count <= 0 || Pointer( field + 4 ) is not { Kind: ResourcePointerKind.Control } fixup )
			return Array.Empty<int>();

		var offsets = new int[count];
		for ( var i = 0; i < count; i++ )
			offsets[i] = fixup.Offset + i * elementSize;
		return offsets;
	}

	// A data reference in control data (i32 size, 0, 0, pointer, 0): the bytes it points at.
	public ReadOnlyMemory<byte> DataReference( int field )
	{
		var size = I32( field );
		if ( size <= 0 )
			return ReadOnlyMemory<byte>.Empty;
		var target = Target( field + 12 );
		return target.Length >= size ? target[..size] : target;
	}
}

// The resource tables of a map: the zone tag (resource types, entries, control data) and the play tag (pages and where each resource's data sits in them).
public sealed class ResourceGestalt
{
	readonly record struct Page( short SharedFile, int Offset, int CompressedSize, int Size );
	readonly record struct Segment( short PrimaryPage, short SecondaryPage, int PrimaryOffset, int SecondaryOffset );

	readonly CacheFile _cache;
	readonly string[] _types;
	readonly ResourceEntry[] _entries;
	readonly Page[] _pages;
	readonly Segment[] _segments;
	readonly string[] _sharedFiles;
	readonly byte[] _control;
	Dictionary<int, int[]> _sliceStarts;

	public IReadOnlyList<ResourceEntry> Entries => _entries;

	internal ResourceGestalt( CacheFile cache )
	{
		_cache = cache;
		var zone = cache.GetGlobal( "zone" ) ?? throw new InvalidDataException( $"'{cache.Name}' has no zone tag" );
		var play = cache.GetGlobal( "play" ) ?? throw new InvalidDataException( $"'{cache.Name}' has no play tag" );
		var z = zone.Data;
		var p = play.Data;

		_types = z.Block( 0x04, 0x1C ).Select( t => t.StringId( 0x18 ) ).ToArray();
		_control = z.Data( 0x148 ).ToArray();

		_sharedFiles = p.Block( 0x0C, 0x108 ).Select( f => Path.GetFileName( f.Cache.File.CString( f.Offset, 256 ).Replace( '\\', '/' ) ) ).ToArray();
		_pages = p.Block( 0x18, 0x58 ).Select( e => new Page( e.I16( 0x04 ), e.I32( 0x08 ), e.I32( 0x0C ), e.I32( 0x10 ) ) ).ToArray();
		_segments = p.Block( 0x3C, 0x10 ).Select( e => new Segment( e.I16( 0 ), e.I16( 2 ), e.I32( 4 ), e.I32( 8 ) ) ).ToArray();

		var resources = z.Block( 0x64, 0x40 );
		_entries = new ResourceEntry[resources.Count];
		for ( var i = 0; i < resources.Count; i++ )
		{
			var r = resources[i];
			var salt = r.U16( 0x10 );
			var typeIndex = r.U8( 0x12 );
			var fixups = r.Block( 0x28, 8 ).Select( f => new ResourceFixup( f.I32( 0 ), (ResourcePointerKind)(f.U32( 4 ) >> 28), (int)(f.U32( 4 ) & 0x0FFFFFFF) ) ).ToArray();
			_entries[i] = new ResourceEntry( i, salt, r.U32( 0x0C ), typeIndex < _types.Length ? _types[typeIndex] : null,
				r.I32( 0x14 ), r.I32( 0x18 ), r.I16( 0x22 ), (int)(r.U32( 0x24 ) & 0x0FFFFFFF), fixups );
		}
	}

	// A live resource by its handle (salt << 16 | index), or null.
	public ResourceEntry Get( uint handle )
	{
		var index = (int)(handle & 0xFFFF);
		if ( handle == 0xFFFFFFFF || index >= _entries.Length )
			return null;
		var entry = _entries[index];
		if ( entry.Salt == 0xFFFF || handle > 0xFFFF && entry.Salt != handle >> 16 )
			return null;
		return entry;
	}

	public byte[] ControlData( ResourceEntry entry ) =>
		entry.ControlSize > 0 && entry.ControlOffset >= 0 && entry.ControlOffset + entry.ControlSize <= _control.Length
			? _control.AsSpan( entry.ControlOffset, entry.ControlSize ).ToArray()
			: Array.Empty<byte>();

	// A resource's data, or null if it's dead. Page slices run to the next resource's data in the page unless exact sizes are given.
	public ResourceData Read( uint handle, int primarySize = -1, int secondarySize = -1 )
	{
		if ( Get( handle ) is not { } entry )
			return null;

		var control = ControlData( entry );
		if ( entry.SegmentIndex < 0 || entry.SegmentIndex >= _segments.Length )
			return new ResourceData( entry, control, null, null );

		var segment = _segments[entry.SegmentIndex];
		return new ResourceData( entry, control, Slice( segment.PrimaryPage, segment.PrimaryOffset, primarySize ), Slice( segment.SecondaryPage, segment.SecondaryOffset, secondarySize ) );
	}

	ReadOnlyMemory<byte>? Slice( int page, int offset, int size )
	{
		if ( page < 0 || page >= _pages.Length )
			return null;

		var data = ReadPage( page );
		if ( data is null || offset < 0 || offset > data.Length )
			return null;

		var end = size >= 0 ? Math.Min( data.Length, offset + size ) : SliceEnd( page, offset, data.Length );
		return new ReadOnlyMemory<byte>( data, offset, end - offset );
	}

	int SliceEnd( int page, int offset, int pageSize )
	{
		if ( _sliceStarts is null )
		{
			var starts = new Dictionary<int, SortedSet<int>>();
			foreach ( var entry in _entries )
			{
				if ( entry.Salt == 0xFFFF || entry.SegmentIndex < 0 || entry.SegmentIndex >= _segments.Length )
					continue;
				var s = _segments[entry.SegmentIndex];
				if ( s.PrimaryPage >= 0 ) (starts.TryGetValue( s.PrimaryPage, out var a ) ? a : starts[s.PrimaryPage] = new()).Add( s.PrimaryOffset );
				if ( s.SecondaryPage >= 0 ) (starts.TryGetValue( s.SecondaryPage, out var b ) ? b : starts[s.SecondaryPage] = new()).Add( s.SecondaryOffset );
			}
			_sliceStarts = starts.ToDictionary( kv => kv.Key, kv => kv.Value.ToArray() );
		}

		if ( !_sliceStarts.TryGetValue( page, out var list ) )
			return pageSize;
		var index = Array.BinarySearch( list, offset + 1 );
		if ( index < 0 ) index = ~index;
		return index < list.Length ? Math.Min( pageSize, list[index] ) : pageSize;
	}

	// The inflated bytes of a page (raw deflate), from this map or a shared resource file; null if it has none.
	public byte[] ReadPage( int index )
	{
		var page = _pages[index];
		if ( page.Offset < 0 || page.CompressedSize <= 0 )
			return null;

		CacheFile holder;
		if ( page.SharedFile < 0 )
			holder = _cache;
		else if ( page.SharedFile < _sharedFiles.Length )
			holder = ResourceFiles.Get( Path.Combine( _cache.Directory, _sharedFiles[page.SharedFile] ) );
		else
			return null;

		if ( holder is null )
			return null;

		return PageCache.Get( holder, holder.ResourceSectionOffset + page.Offset, page.CompressedSize, page.Size );
	}
}

// shared.map and campaign.map, opened once for every map's pages in them.
public static class ResourceFiles
{
	static readonly ConcurrentDictionary<string, Lazy<CacheFile>> Files = new( StringComparer.OrdinalIgnoreCase );
	static volatile bool _closed;

	public static CacheFile Get( string path )
	{
		if ( _closed )
			throw new ObjectDisposedException( nameof( ResourceFiles ) );
		if ( !File.Exists( path ) )
			return null;

		// A failed open (the file locked by a Steam verify, say) is forgotten so the next read tries again.
		var key = Path.GetFullPath( path );
		var lazy = Files.GetOrAdd( key, p => new Lazy<CacheFile>( () => CacheFile.Open( p ) ) );
		try
		{
			return lazy.Value;
		}
		catch
		{
			Files.TryRemove( new KeyValuePair<string, Lazy<CacheFile>>( key, lazy ) );
			throw;
		}
	}

	public static void Open() => _closed = false;

	public static void Close()
	{
		_closed = true;
		foreach ( var file in Files.Values.Where( f => f.IsValueCreated ) )
			file.Value.Dispose();
		Files.Clear();
		PageCache.Clear();
	}
}

// Inflated pages, shared by all maps (they all use the same shared.map pages), dropping the least recently used beyond a budget.
public static class PageCache
{
	public static long Budget { get; set; } = 512L << 20;

	sealed class Item
	{
		public Lazy<byte[]> Data;
		public long LastUse;
		public long Size;
	}

	static readonly Dictionary<(string, long), Item> Items = new();
	static long _clock;
	static long _total;

	public static byte[] Get( CacheFile holder, long offset, int compressedSize, int size )
	{
		Item item;
		lock ( Items )
		{
			var key = (holder.FilePath, offset);
			if ( !Items.TryGetValue( key, out item ) )
			{
				item = new Item { Size = size, Data = new Lazy<byte[]>( () => Inflate( holder, offset, compressedSize, size ) ) };
				Items[key] = item;
				_total += size;
				Trim( item );
			}
			item.LastUse = ++_clock;
		}
		return item.Data.Value;
	}

	static void Trim( Item keep )
	{
		while ( _total > Budget && Items.Count > 1 )
		{
			var oldest = Items.Where( kv => kv.Value != keep ).MinBy( kv => kv.Value.LastUse );
			Items.Remove( oldest.Key );
			_total -= oldest.Value.Size;
		}
	}

	static byte[] Inflate( CacheFile holder, long offset, int compressedSize, int size )
	{
		var data = GC.AllocateUninitializedArray<byte>( size );
		using var stream = new DeflateStream( holder.File.Stream( offset, compressedSize ), CompressionMode.Decompress );
		var read = 0;
		while ( read < size )
		{
			var n = stream.Read( data, read, size - read );
			if ( n == 0 )
				throw new InvalidDataException( $"Page at 0x{offset:X} in '{holder.FilePath}' inflates to {read} bytes, not {size}" );
			read += n;
		}
		return data;
	}

	public static void Clear()
	{
		lock ( Items )
		{
			Items.Clear();
			_total = 0;
		}
	}
}
