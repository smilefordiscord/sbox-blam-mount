using System;
using System.Collections.Generic;
using System.IO;
using Blam.IO;

namespace Blam.Halo3;

public enum CacheType
{
	Campaign = 0,
	Multiplayer = 1,
	MainMenu = 2,
	Shared = 3,
	CampaignShared = 4,
}

// A Halo 3 cache file from MCC on PC (halo3/maps, header version 13).
// The file is memory-mapped: four sections (debug, resource, tag, locale) whose virtual addresses map to file offsets through per-section masks, and tag data pointers are packed (address = pointer << 2).
// shared.map and campaign.map hold only resource pages, which every other map's resource layout points into.
public sealed class CacheFile : IDisposable
{
	const uint HeadMagic = 0x68656164; // 'head'
	const uint TagsMagic = 0x74616773; // 'tags'
	public const int MccCacheVersion = 13;

	readonly TagEntry[] _slots;
	readonly List<TagEntry> _tags = new();
	readonly Dictionary<TagClass, TagEntry> _globals = new();
	readonly long _tagSectionOffset;
	readonly long _tagSectionSize;
	readonly ulong _virtualBase;
	readonly long _debugMask;
	string[] _strings;
	int[] _namespaceStarts;
	int _namespace0Size;
	int _namespace0Rest;
	ResourceGestalt _resources;

	public MappedFile File { get; }
	public string FilePath => File.Path;
	public string Name { get; }
	public string ScenarioPath { get; }
	public CacheType Type { get; }
	public bool HasTags => _tagSectionSize > 0;

	// Where the resource section starts in the file; page offsets are relative to it.
	public long ResourceSectionOffset { get; }

	public IReadOnlyList<TagEntry> Tags => _tags;
	public TagEntry Scenario => GetGlobal( "scnr" );
	public TagEntry Globals => GetGlobal( "matg" );

	// The files beside this one, for its pages in shared.map / campaign.map.
	public string Directory => System.IO.Path.GetDirectoryName( FilePath );

	CacheFile( MappedFile file )
	{
		File = file;
		if ( file.U32( 0 ) != HeadMagic )
			throw new InvalidDataException( $"'{file.Path}' is not a Halo 3 cache file" );

		var version = (int)file.U32( 4 );
		if ( version != MccCacheVersion )
			throw new InvalidDataException( $"'{file.Path}' has cache version {version}, only Halo 3 from MCC ({MccCacheVersion}) is supported" );

		Type = (CacheType)file.I16( 0x18 );
		Name = file.CString( 0xC0, 32 );
		ScenarioPath = file.CString( 0xE0, 256 );
		_virtualBase = file.U64( 0x2E0 );

		long SectionOffset( int index ) => (uint)(file.U32( 0x4DC + 8 * index ) + file.U32( 0x4CC + 4 * index ));
		_debugMask = file.U32( 0x4CC );
		ResourceSectionOffset = SectionOffset( 1 );
		_tagSectionOffset = SectionOffset( 2 );
		_tagSectionSize = file.U32( 0x4DC + 8 * 2 + 4 );

		if ( string.IsNullOrEmpty( Name ) )
			Name = System.IO.Path.GetFileNameWithoutExtension( file.Path );

		if ( !HasTags )
		{
			_slots = Array.Empty<TagEntry>();
			return;
		}

		// Tag index header: groups, tags, global tags (6), interops, ?, 'tags'.
		var index = Offset64( file.U64( 0x2E8 ) );
		if ( file.U32( index + 0x44 ) != TagsMagic )
			throw new InvalidDataException( $"'{file.Path}' has no 'tags' marker in its tag index" );

		var groupCount = (int)file.U32( index );
		var groups = Offset64( file.U64( index + 0x08 ) );
		var tagCount = (int)file.U32( index + 0x10 );
		var tagTable = Offset64( file.U64( index + 0x18 ) );
		var globalCount = (int)file.U32( index + 0x20 );
		var globalTable = Offset64( file.U64( index + 0x28 ) );

		var groupList = new (TagClass Class, TagClass Parent, TagClass Grandparent)[groupCount];
		for ( var i = 0; i < groupCount; i++ )
		{
			var entry = groups + 16 * i;
			groupList[i] = (new TagClass( file.U32( entry ) ), new TagClass( file.U32( entry + 4 ) ), new TagClass( file.U32( entry + 8 ) ));
		}

		// Tag names: i32 offsets (debug section) into a NUL-terminated blob.
		var nameCount = (int)file.U32( 0x20 );
		var nameData = DebugOffset( file.U32( 0x24 ) );
		var nameIndex = DebugOffset( file.U32( 0x2C ) );

		_slots = new TagEntry[tagCount];
		for ( var i = 0; i < tagCount; i++ )
		{
			var entry = tagTable + 8 * i;
			var group = file.I16( entry );
			if ( group < 0 || group >= groupCount )
				continue;

			var salt = file.U16( entry + 2 );
			var address = file.U32( entry + 4 );
			var nameOffset = i < nameCount ? file.I32( nameIndex + 4 * i ) : -1;
			var name = nameOffset >= 0 ? file.CString( nameData + nameOffset, 512 ) : "";
			var (cls, parent, grandparent) = groupList[group];
			var tag = new TagEntry( this, i, (uint)salt << 16 | (uint)i, cls, parent, grandparent, name, address );
			_slots[i] = tag;
			_tags.Add( tag );
		}

		for ( var i = 0; i < globalCount; i++ )
		{
			var entry = globalTable + 8 * i;
			if ( GetTag( file.U32( entry + 4 ) ) is { } tag )
				_globals[new TagClass( file.U32( entry ) )] = tag;
		}
	}

	public static CacheFile Open( string path )
	{
		var file = new MappedFile( path );
		try
		{
			return new CacheFile( file );
		}
		catch
		{
			file.Dispose();
			throw;
		}
	}

	public TagEntry GetGlobal( string group ) => _globals.GetValueOrDefault( new TagClass( group ) );

	// A tag by its datum (salt << 16 | index) or plain index; null for none or a stale datum.
	public TagEntry GetTag( uint id )
	{
		if ( id == 0xFFFFFFFF )
			return null;

		var index = (int)(id & 0xFFFF);
		if ( index >= _slots.Length || _slots[index] is not { } tag )
			return null;

		return id > 0xFFFF && tag.Id != id ? null : tag;
	}

	public IEnumerable<TagEntry> TagsOf( string group )
	{
		var cls = new TagClass( group );
		foreach ( var tag in _tags )
		{
			if ( tag.Class == cls )
				yield return tag;
		}
	}

	public TagEntry FindTag( string path, string group )
	{
		var cls = new TagClass( group );
		foreach ( var tag in _tags )
		{
			if ( tag.Class == cls && string.Equals( tag.Path, path, StringComparison.OrdinalIgnoreCase ) )
				return tag;
		}
		return null;
	}

	long DebugOffset( uint address ) => (uint)(address + _debugMask);

	long Offset64( ulong address )
	{
		var offset = _tagSectionOffset + (long)(address - _virtualBase);
		if ( offset < _tagSectionOffset || offset >= _tagSectionOffset + _tagSectionSize )
			throw new InvalidDataException( $"Address 0x{address:X} is outside the tag data of '{File.Path}'" );
		return offset;
	}

	// The file offset of a packed tag data pointer.
	public long PointerOffset( uint pointer ) => Offset64( (ulong)pointer << 2 );

	public TagView View( uint pointer ) => pointer == 0 ? default : new TagView( this, PointerOffset( pointer ) );

	// String ids: 5 bits length, 8 bits namespace, 19 bits index. Table order is namespace 0's shared part, namespaces 1..n, then the rest of namespace 0 (the map's own strings).
	public string GetString( uint id )
	{
		if ( id == 0 )
			return "";
		if ( id == 0xFFFFFFFF )
			return null;

		if ( _strings is null )
			LoadStrings();

		var index = (int)(id & 0x7FFFF);
		var space = (int)(id >> 19 & 0xFF);
		int slot;
		if ( space > 0 )
			slot = space < _namespaceStarts.Length ? _namespaceStarts[space] + index : -1;
		else
			slot = index < _namespace0Size ? index : index - _namespace0Size + _namespace0Rest;

		return slot >= 0 && slot < _strings.Length ? _strings[slot] : null;
	}

	void LoadStrings()
	{
		lock ( _tags )
		{
			if ( _strings is not null )
				return;

			var count = (int)File.U32( 0x30 );
			var data = DebugOffset( File.U32( 0x34 ) );
			var index = DebugOffset( File.U32( 0x3C ) );
			var namespaceCount = (int)File.U32( 0x40 );
			var namespaces = DebugOffset( File.U32( 0x44 ) );

			var starts = new int[Math.Max( 1, namespaceCount )];
			var size0 = namespaceCount > 0 ? (int)File.U32( namespaces ) : 0;
			var next = size0;
			for ( var i = 1; i < namespaceCount; i++ )
			{
				starts[i] = next;
				next += (int)File.U32( namespaces + 4 * i );
			}

			var strings = new string[count];
			for ( var i = 0; i < count; i++ )
			{
				var offset = File.I32( index + 4 * i );
				if ( offset >= 0 )
					strings[i] = File.CString( data + offset, 1024 );
			}

			_namespaceStarts = starts;
			_namespace0Size = size0;
			_namespace0Rest = next;
			_strings = strings;
		}
	}

	// The map's resources (zone + play tags): bitmaps, geometry, animations, constant buffers.
	public ResourceGestalt Resources
	{
		get
		{
			if ( _resources is null )
			{
				lock ( _tags )
					_resources ??= new ResourceGestalt( this );
			}
			return _resources;
		}
	}

	public override string ToString() => Name;

	public void Dispose() => File.Dispose();
}
