using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using Blam.IO;

namespace Blam.Halo2;

/// <summary>
/// What a Halo 2 cache file holds, from its header (0x18).
/// </summary>
public enum CacheType
{
	Campaign = 0,
	Multiplayer = 1,
	MainMenu = 2,
	Shared = 3,
	SinglePlayerShared = 4,
}

/// <summary>
/// A Halo 2 cache file (<c>.map</c>) as shipped with Halo: MCC on PC (classic engine, <c>halo2/h2_maps_win64_dx11</c>).
/// </summary>
/// <remarks>
/// Layout, little-endian:
/// <list type="bullet">
/// <item>A 0x380 byte header, <c>daeh</c> ... <c>toof</c>, stored uncompressed. Everything after it is zlib
/// compressed in chunks that each inflate to 0x40000 bytes; the chunk table (size, offset per chunk, a negative
/// size meaning stored raw) is at the header's 0x310. All other header offsets are offsets into the inflated image.</item>
/// <item>Raw data (geometry, sound gestalt tables...) from 0x380, then tag names and string ids, then the meta
/// area: the tag index (<c>tags</c>) and the tag data. Tags address each other with virtual addresses that map to
/// <c>meta offset + (address - meta base)</c>, the meta base being 0x03200000 on multiplayer maps and 0 elsewhere.</item>
/// <item>Multiplayer maps share their tag slots with <c>shared.map</c>: a slot with a tag id but no data is that
/// slot's tag in shared.map. Campaign and main menu maps are self-contained.</item>
/// <item>Bitmap pixels are in <c>textures.dat</c> (<see cref="TextureFile"/>), sound samples in <c>sounds_*.dat</c>.</item>
/// </list>
/// </remarks>
public sealed class CacheFile : IDisposable
{
	const uint HeadMagic = 0x68656164; // 'head'
	const uint FootMagic = 0x666F6F74; // 'foot'
	const uint TagsMagic = 0x74616773; // 'tags'
	const int HeaderSize = 0x380;
	const int TagEntrySize = 0x10;
	const int GroupEntrySize = 0xC;

	/// <summary>
	/// The cache version of Halo 2 in MCC since its 2023 update.
	/// </summary>
	public const int MccCacheVersion = 13;

	readonly RawFile _file;
	readonly CompressedImage _image;
	readonly TagEntry[] _slots;
	readonly List<TagEntry> _tags = new();
	readonly int _stringCount;
	readonly uint _stringIndexOffset;
	readonly uint _stringDataOffset;
	readonly int _stringDataSize;
	readonly uint _localeTablesOffset;
	string[] _strings;
	readonly Dictionary<int, IReadOnlyList<(uint StringId, string Text)>> _localizedStrings = new();

	public string FilePath => _file.Path;
	public string Name { get; }
	public string ScenarioPath { get; }
	public CacheType Type { get; }

	/// <summary>
	/// The tag index and tag data, addressed the way tags point at each other.
	/// </summary>
	public MemoryRegion TagData { get; }

	/// <summary>
	/// Every tag in this map, including the ones it takes from <see cref="Shared"/> (those belong to shared.map).
	/// </summary>
	public IReadOnlyList<TagEntry> Tags => _tags;

	public TagEntry Scenario { get; }
	public TagEntry Globals { get; }

	/// <summary>
	/// The sound gestalt (<c>ugh!</c>) that holds the playback data and sample locations of this map's own sound
	/// tags. Sound tags a multiplayer map takes from shared.map use shared.map's.
	/// </summary>
	public TagEntry SoundGestalt { get; }

	/// <summary>
	/// The shared map this one takes tags from, or null (owned by the caller).
	/// </summary>
	public CacheFile Shared { get; }

	CacheFile( RawFile file, CacheFile shared )
	{
		_file = file;

		var header = file.Read( 0, HeaderSize );
		uint U32( int offset ) => BinaryPrimitives.ReadUInt32LittleEndian( header.AsSpan( offset ) );

		if ( U32( 0x0 ) != HeadMagic || U32( 0x37C ) != FootMagic )
			throw new InvalidDataException( $"'{file.Path}' is not a Halo 2 cache file" );

		var version = (int)U32( 0x4 );
		if ( version != MccCacheVersion )
			throw new InvalidDataException( $"'{file.Path}' has cache version {version}, only Halo 2 from MCC ({MccCacheVersion}) is supported" );

		Type = (CacheType)U32( 0x18 );
		Name = ReadString( header.AsSpan( 0xB0, 32 ) );
		ScenarioPath = ReadString( header.AsSpan( 0xD0, 256 ) );
		Shared = Type is CacheType.Multiplayer or CacheType.MainMenu ? shared : null;

		var compressed = (BinaryPrimitives.ReadUInt16LittleEndian( header.AsSpan( 0x1C ) ) & 1) != 0;
		_image = new CompressedImage( file, header, compressed, U32( 0x8 ), (int)U32( 0x308 ), U32( 0x310 ), (int)U32( 0x314 ) );

		var metaOffset = U32( 0x10 );
		var metaSize = (int)U32( 0x14 );
		var metaBase = U32( 0x2D0 );
		TagData = new MemoryRegion( _image.Read( metaOffset, metaSize ), metaBase );

		// Tag index header: group table offset and count, tag table offset (both relative to the index),
		// scenario and globals tag ids, ?, tag count, 'tags'.
		if ( TagData.U32( metaBase + 0x1C ) != TagsMagic )
			throw new InvalidDataException( $"'{file.Path}' has no 'tags' marker in its tag index" );

		var groupOffset = TagData.U32( metaBase + 0x0 );
		var groupCount = (int)TagData.U32( metaBase + 0x4 );
		var tagOffset = TagData.U32( metaBase + 0x8 );
		var scenarioId = TagData.U32( metaBase + 0xC );
		var globalsId = TagData.U32( metaBase + 0x10 );
		var tagCount = (int)TagData.U32( metaBase + 0x18 );

		var parents = new Dictionary<TagClass, (TagClass, TagClass)>();
		for ( var i = 0; i < groupCount; i++ )
		{
			var entry = metaBase + groupOffset + (uint)(i * GroupEntrySize);
			parents[TagData.Class( entry )] = (TagData.Class( entry + 4 ), TagData.Class( entry + 8 ));
		}

		var nameCount = (int)U32( 0x20 );
		var names = _image.Read( U32( 0x24 ), (int)U32( 0x28 ) );
		var nameIndex = _image.Read( U32( 0x2C ), nameCount * 4 );

		_stringCount = (int)U32( 0x30 );
		_stringDataOffset = U32( 0x34 );
		_stringDataSize = (int)U32( 0x38 );
		_stringIndexOffset = U32( 0x3C );
		_localeTablesOffset = U32( 0x2E4 );

		_slots = new TagEntry[tagCount];
		for ( var i = 0; i < tagCount; i++ )
		{
			var entry = metaBase + tagOffset + (uint)(i * TagEntrySize);
			var id = TagData.U32( entry + 4 );
			if ( id == 0xFFFFFFFF )
				continue;

			var address = TagData.U32( entry + 8 );
			if ( address == 0 && Shared is not null )
			{
				// Data-less slot: the tag is shared.map's tag in the same slot.
				var sharedTag = Shared.GetTag( id );
				if ( sharedTag is not null )
				{
					_slots[i] = sharedTag;
					_tags.Add( sharedTag );
				}

				continue;
			}

			var tagClass = TagData.Class( entry );
			parents.TryGetValue( tagClass, out var family );
			var nameOffset = i < nameCount ? BinaryPrimitives.ReadInt32LittleEndian( nameIndex.AsSpan( i * 4 ) ) : -1;

			var tag = new TagEntry( this, i, id, tagClass, family.Item1, family.Item2,
				nameOffset >= 0 ? ReadString( names.AsSpan( nameOffset ) ) : "",
				address, TagData.U32( entry + 12 ) );

			_slots[i] = tag;
			_tags.Add( tag );
		}

		Scenario = GetTag( scenarioId );
		Globals = GetTag( globalsId );

		// Multiplayer maps name the small gestalt of their own sounds in the header; other maps have one gestalt.
		var soundGestaltId = U32( 0x2EC );
		SoundGestalt = soundGestaltId != 0xFFFFFFFF
			? GetTag( soundGestaltId )
			: _tags.Find( t => t.Cache == this && t.Class == global::Blam.Halo2.Tags.SoundGestaltTag.Class );
	}

	/// <summary>
	/// Opens a cache file. Multiplayer and main menu maps need <paramref name="shared"/>, the opened
	/// <c>shared.map</c> next to them, which is owned by the caller.
	/// </summary>
	public static CacheFile Open( string path, CacheFile shared = null )
	{
		var file = new RawFile( path );
		try
		{
			return new CacheFile( file, shared );
		}
		catch
		{
			file.Dispose();
			throw;
		}
	}

	/// <summary>
	/// Finds a tag by its 32 bit id; the low 16 bits are its slot. Tags this map takes from shared.map
	/// come back as shared.map's entries.
	/// </summary>
	public TagEntry GetTag( uint id )
	{
		if ( id == 0xFFFFFFFF ) return null;

		var index = (int)(id & 0xFFFF);
		if ( index >= _slots.Length ) return null;

		var tag = _slots[index];
		return tag?.Id == id ? tag : null;
	}

	/// <summary>
	/// Resolves an 8 byte tag reference (group, tag id) stored at <paramref name="address"/> in this map's tag data.
	/// </summary>
	public TagEntry GetTagReference( uint address ) => GetTag( TagData.U32( address + 4 ) );

	/// <summary>
	/// A string id's text. The low 24 bits index the string table; the top 8 bits are the string's length.
	/// </summary>
	public string GetString( uint stringId )
	{
		if ( _strings is null )
		{
			var index = _image.Read( _stringIndexOffset, _stringCount * 4 );
			var data = _image.Read( _stringDataOffset, _stringDataSize );
			var strings = new string[_stringCount];
			for ( var i = 0; i < _stringCount; i++ )
			{
				var offset = BinaryPrimitives.ReadInt32LittleEndian( index.AsSpan( i * 4 ) );
				strings[i] = offset >= 0 ? ReadString( data.AsSpan( offset ) ) : null;
			}

			_strings = strings;
		}

		var slot = (int)(stringId & 0xFFFFFF);
		return slot < _strings.Length ? _strings[slot] : null;
	}

	// The languages of the localized string tables, in Halo's order.
	public static readonly string[] Languages = ["english", "japanese", "german", "french", "spanish", "italian", "korean", "chinese", "portuguese"];

	// This map's localized strings in one language (an index of Languages): each string's id and its UTF-8 text.
	// String lists (unic) index them.
	// A multiplayer map's tables hold only its own strings and are named in its header (0x2E4).
	// Other maps have them in their globals (0x188, a table per language).
	public IReadOnlyList<(uint StringId, string Text)> LocalizedStrings( int language )
	{
		lock ( _localizedStrings )
		{
			if ( !_localizedStrings.TryGetValue( language, out var strings ) )
				_localizedStrings[language] = strings = ReadLocalizedStrings( language );
			return strings;
		}
	}

	// A table: string count at 0x8, text size at 0xC, then raw pointers to the index (string id, text offset) and the text.
	IReadOnlyList<(uint, string)> ReadLocalizedStrings( int language )
	{
		const int TableSize = 0x1C;
		byte[] table;
		if ( _localeTablesOffset != 0xFFFFFFFF )
			table = _image.Read( _localeTablesOffset + (uint)(language * TableSize), TableSize );
		else if ( Globals is { } globals && globals.Cache == this )
			table = TagData.Slice( globals.Address + 0x188 + (uint)(language * TableSize), TableSize ).ToArray();
		else
			return [];

		var count = BinaryPrimitives.ReadInt32LittleEndian( table.AsSpan( 0x8 ) );
		var textSize = BinaryPrimitives.ReadInt32LittleEndian( table.AsSpan( 0xC ) );
		if ( count <= 0 || textSize <= 0 )
			return [];

		var index = ReadRaw( BinaryPrimitives.ReadUInt32LittleEndian( table.AsSpan( 0x10 ) ), count * 8 );
		var text = ReadRaw( BinaryPrimitives.ReadUInt32LittleEndian( table.AsSpan( 0x14 ) ), textSize );
		var strings = new (uint, string)[count];
		for ( var i = 0; i < count; i++ )
		{
			var offset = BinaryPrimitives.ReadInt32LittleEndian( index.AsSpan( i * 8 + 4 ) );
			var span = offset >= 0 && offset < text.Length ? text.AsSpan( offset ) : default;
			var end = span.IndexOf( (byte)0 );
			strings[i] = (BinaryPrimitives.ReadUInt32LittleEndian( index.AsSpan( i * 8 ) ), Encoding.UTF8.GetString( end < 0 ? span : span[..end] ));
		}

		return strings;
	}

	/// <summary>
	/// Reads raw data (geometry blocks, sound tables) through a raw data pointer from one of this map's tags.
	/// The top two bits pick the file: 0 this map, 2 shared.map. Main menu (1) and single_player_shared (3)
	/// are never used by MCC's maps.
	/// </summary>
	public byte[] ReadRaw( uint pointer, int length )
	{
		var offset = pointer & 0x3FFFFFFF;
		return (pointer >> 30) switch
		{
			0 => _image.Read( offset, length ),
			2 when Type == CacheType.Shared => _image.Read( offset, length ),
			2 when Shared is not null => Shared.ReadRaw( offset, length ),
			var location => throw new InvalidDataException( $"Raw data pointer 0x{pointer:X8} of '{Name}' points into cache file {location}, which isn't open" ),
		};
	}

	static string ReadString( ReadOnlySpan<byte> span )
	{
		var end = span.IndexOf( (byte)0 );
		return Encoding.ASCII.GetString( end < 0 ? span : span[..end] );
	}

	public void Dispose() => _file.Dispose();

	/// <summary>
	/// Random access to the inflated image of a compressed cache file, a chunk at a time.
	/// </summary>
	sealed class CompressedImage
	{
		readonly RawFile _file;
		readonly byte[] _header;
		readonly bool _compressed;
		readonly long _length;
		readonly int _chunkSize;
		readonly (int Size, uint Offset)[] _chunks;

		// The last few chunks inflated, since tag reads tend to touch the same ones.
		readonly (int Index, byte[] Data)[] _recent = new (int, byte[])[8];
		int _nextRecent;

		public CompressedImage( RawFile file, byte[] header, bool compressed, long length, int chunkSize, uint tableOffset, int chunkCount )
		{
			_file = file;
			_header = header;
			_compressed = compressed;
			_length = length;
			_chunkSize = chunkSize;

			if ( !compressed )
				return;

			var table = file.Read( tableOffset, chunkCount * 8 );
			_chunks = new (int, uint)[chunkCount];
			for ( var i = 0; i < chunkCount; i++ )
				_chunks[i] = (BinaryPrimitives.ReadInt32LittleEndian( table.AsSpan( i * 8 ) ), BinaryPrimitives.ReadUInt32LittleEndian( table.AsSpan( i * 8 + 4 ) ));

			for ( var i = 0; i < _recent.Length; i++ )
				_recent[i].Index = -1;
		}

		public byte[] Read( long offset, int length )
		{
			if ( offset < 0 || length < 0 || offset + length > _length )
				throw new ArgumentOutOfRangeException( nameof( offset ), $"Read of {length} bytes at 0x{offset:X} is outside '{_file.Path}' ({_length} bytes inflated)" );

			if ( !_compressed )
				return _file.Read( offset, length );

			var result = GC.AllocateUninitializedArray<byte>( length );
			var done = 0;

			// The header isn't part of any chunk.
			if ( offset < HeaderSize )
			{
				var n = (int)Math.Min( length, HeaderSize - offset );
				_header.AsSpan( (int)offset, n ).CopyTo( result );
				done = n;
			}

			while ( done < length )
			{
				var position = offset + done - HeaderSize;
				var chunk = (int)(position / _chunkSize);
				var within = (int)(position % _chunkSize);
				var data = GetChunk( chunk );
				var n = Math.Min( length - done, data.Length - within );
				data.AsSpan( within, n ).CopyTo( result.AsSpan( done ) );
				done += n;
			}

			return result;
		}

		byte[] GetChunk( int index )
		{
			lock ( _recent )
			{
				foreach ( var (i, data) in _recent )
					if ( i == index ) return data;
			}

			var (size, offset) = _chunks[index];
			byte[] chunk;
			if ( size < 0 )
			{
				chunk = _file.Read( offset, -size );
			}
			else
			{
				using var input = new MemoryStream( _file.Read( offset, size ) );
				using var zlib = new ZLibStream( input, CompressionMode.Decompress );
				var buffer = GC.AllocateUninitializedArray<byte>( _chunkSize );
				var read = 0;
				int n;
				while ( read < buffer.Length && (n = zlib.Read( buffer, read, buffer.Length - read )) > 0 )
					read += n;

				chunk = read == buffer.Length ? buffer : buffer.AsSpan( 0, read ).ToArray();
			}

			lock ( _recent )
			{
				_recent[_nextRecent] = (index, chunk);
				_nextRecent = (_nextRecent + 1) % _recent.Length;
			}

			return chunk;
		}
	}
}
