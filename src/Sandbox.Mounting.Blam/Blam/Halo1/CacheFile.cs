using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Blam.IO;

namespace Blam.Halo1;

public enum ScenarioType : ushort
{
	Singleplayer = 0,
	Multiplayer = 1,
	UserInterface = 2,
}

/// <summary>
/// A Halo CE cache file (<c>.map</c>) as shipped with Halo: MCC on PC.
/// </summary>
/// <remarks>
/// Layout, little-endian:
/// <list type="bullet">
/// <item>2048 byte header, <c>head</c> ... <c>foot</c>. Cache version at 0x4, tag data offset and size at 0x10/0x14, scenario name at 0x20, build at 0x40, scenario type at 0x60.</item>
/// <item>Tag data, loaded by the game at a fixed virtual address. It begins with a 40 byte header (tag array pointer, scenario tag id, checksum, tag count, model data info, <c>tags</c>) followed by the tag array.</item>
/// <item>Model vertex and index data, structure BSPs and bitmap pixels live elsewhere in the file and are referenced by file offset.</item>
/// </list>
/// </remarks>
public sealed class CacheFile : IDisposable
{
	const uint HeadMagic = 0x68656164; // 'head'
	const uint FootMagic = 0x666F6F74; // 'foot'
	const uint TagsMagic = 0x74616773; // 'tags'
	const int HeaderSize = 0x800;
	const int TagDataHeaderSize = 0x28;
	const int TagEntrySize = 0x20;

	/// <summary>
	/// The cache version used by Halo CE in MCC (Anniversary engine).
	/// </summary>
	public const int MccCacheVersion = 13;

	readonly RawFile _file;
	readonly TagEntry[] _tags;

	public string FilePath => _file.Path;
	public int CacheVersion { get; }
	public string Name { get; }
	public string Build { get; }
	public ScenarioType ScenarioType { get; }

	/// <summary>
	/// The tag data, addressable by the pointers stored inside tags.
	/// </summary>
	public MemoryRegion TagData { get; }

	public IReadOnlyList<TagEntry> Tags => _tags;
	public TagEntry Scenario { get; }

	/// <summary>
	/// File offset of the shared model vertex data. Model index data follows it at <see cref="IndexDataOffset"/>.
	/// </summary>
	public uint VertexDataOffset { get; }
	public uint IndexDataOffset { get; }
	public uint ModelDataSize { get; }

	/// <summary>
	/// The shared <c>bitmaps.map</c> this cache file pulls external bitmap pixels from.
	/// </summary>
	public ResourceMap Bitmaps { get; }

	CacheFile( RawFile file, ResourceMap bitmaps )
	{
		_file = file;
		Bitmaps = bitmaps;

		var header = file.Read( 0, HeaderSize );
		uint U32( int offset ) => BinaryPrimitives.ReadUInt32LittleEndian( header.AsSpan( offset ) );

		if ( U32( 0x0 ) != HeadMagic || U32( 0x7FC ) != FootMagic )
			throw new InvalidDataException( $"'{file.Path}' is not a Halo CE cache file" );

		CacheVersion = (int)U32( 0x4 );
		if ( CacheVersion != MccCacheVersion )
			throw new InvalidDataException( $"'{file.Path}' has cache version {CacheVersion}, only Halo CE from MCC ({MccCacheVersion}) is supported" );

		var fileSize = U32( 0x8 );
		if ( fileSize > file.Length )
			throw new InvalidDataException( $"'{file.Path}' appears to be compressed ({fileSize} bytes expected, {file.Length} on disk), which isn't supported" );

		var tagDataOffset = U32( 0x10 );
		var tagDataSize = (int)U32( 0x14 );
		Name = ReadString( header.AsSpan( 0x20, 32 ) );
		Build = ReadString( header.AsSpan( 0x40, 32 ) );
		ScenarioType = (ScenarioType)BinaryPrimitives.ReadUInt16LittleEndian( header.AsSpan( 0x60 ) );

		var tagData = file.Read( tagDataOffset, tagDataSize );
		uint T32( int offset ) => BinaryPrimitives.ReadUInt32LittleEndian( tagData.AsSpan( offset ) );

		if ( T32( 0x24 ) != TagsMagic )
			throw new InvalidDataException( $"'{file.Path}' has no 'tags' marker in its tag data header" );

		// The tag array directly follows the tag data header, so the address it's loaded at
		// tells us the base address of the whole region without hardcoding it per engine.
		var tagArrayAddress = T32( 0x0 );
		TagData = new MemoryRegion( tagData, tagArrayAddress - TagDataHeaderSize );

		var scenarioId = T32( 0x4 );
		var tagCount = (int)T32( 0xC );
		VertexDataOffset = T32( 0x14 );
		IndexDataOffset = VertexDataOffset + T32( 0x1C );
		ModelDataSize = T32( 0x20 );

		_tags = new TagEntry[tagCount];
		for ( var i = 0; i < tagCount; i++ )
		{
			var entry = tagArrayAddress + (uint)(i * TagEntrySize);
			_tags[i] = new TagEntry(
				Index: i,
				Id: TagData.U32( entry + 0xC ),
				Class: TagData.Class( entry + 0x0 ),
				Parent: TagData.Class( entry + 0x4 ),
				Grandparent: TagData.Class( entry + 0x8 ),
				Path: TagData.CString( TagData.U32( entry + 0x10 ) ),
				Address: TagData.U32( entry + 0x14 ),
				IsExternal: TagData.U32( entry + 0x18 ) != 0 );
		}

		Scenario = GetTag( scenarioId );
	}

	/// <summary>
	/// Opens a cache file. <paramref name="bitmaps"/> is the shared <c>bitmaps.map</c> next to it,
	/// which is owned by the caller.
	/// </summary>
	public static CacheFile Open( string path, ResourceMap bitmaps )
	{
		var file = new RawFile( path );
		try
		{
			return new CacheFile( file, bitmaps );
		}
		catch
		{
			file.Dispose();
			throw;
		}
	}

	/// <summary>
	/// Finds a tag by its 32 bit id. The low 16 bits are the index into the tag array.
	/// </summary>
	public TagEntry GetTag( uint id )
	{
		if ( id == 0xFFFFFFFF ) return null;

		var index = (int)(id & 0xFFFF);
		if ( index >= _tags.Length ) return null;

		var tag = _tags[index];
		return tag.Id == id ? tag : null;
	}

	/// <summary>
	/// Resolves a 16 byte tag reference (class, path pointer, path length, tag id) stored at
	/// <paramref name="address"/> in <paramref name="region"/>.
	/// </summary>
	public TagEntry GetTagReference( MemoryRegion region, uint address ) => GetTag( region.U32( address + 0xC ) );

	/// <summary>
	/// Reads raw bytes at an absolute offset in this cache file.
	/// </summary>
	public byte[] Read( long offset, int length ) => _file.Read( offset, length );

	static string ReadString( ReadOnlySpan<byte> span )
	{
		var end = span.IndexOf( (byte)0 );
		return Encoding.ASCII.GetString( end < 0 ? span : span[..end] );
	}

	public void Dispose() => _file.Dispose();
}
