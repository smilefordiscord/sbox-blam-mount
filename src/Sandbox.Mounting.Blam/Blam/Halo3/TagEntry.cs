using System;
using System.Collections.Generic;

namespace Blam.Halo3;

// Inside the namespace so these win over s&box's global Vector types when this compiles into the mount.
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;
using Vector4 = System.Numerics.Vector4;

// One tag of a Halo 3 cache file. Every map holds all of its own tags (nothing is shared between maps).
public sealed record TagEntry( CacheFile Cache, int Index, uint Id, TagClass Class, TagClass Parent, TagClass Grandparent, string Path, uint Address )
{
	public bool Is( TagClass tagClass ) => Class == tagClass || Parent == tagClass || Grandparent == tagClass;

	// The tag's main struct; null for a tag without data.
	public TagView Data => Address == 0 ? default : new TagView( Cache, Cache.PointerOffset( Address ) );

	public override string ToString() => $"{Path}.{Class}";
}

// A struct in a cache file's tag data, at a file offset. Field offsets are relative to it.
public readonly struct TagView( CacheFile cache, long offset )
{
	public CacheFile Cache { get; } = cache;
	public long Offset { get; } = offset;
	public bool IsNull => Cache is null;

	public TagView At( int field ) => new( Cache, Offset + field );

	public byte U8( int field ) => Cache.File.U8( Offset + field );
	public sbyte I8( int field ) => (sbyte)Cache.File.U8( Offset + field );
	public short I16( int field ) => Cache.File.I16( Offset + field );
	public ushort U16( int field ) => Cache.File.U16( Offset + field );
	public int I32( int field ) => Cache.File.I32( Offset + field );
	public uint U32( int field ) => Cache.File.U32( Offset + field );
	public float F32( int field ) => Cache.File.F32( Offset + field );
	public Vector2 Vec2( int field ) => new( F32( field ), F32( field + 4 ) );
	public Vector3 Vec3( int field ) => new( F32( field ), F32( field + 4 ), F32( field + 8 ) );
	public Vector4 Vec4( int field ) => new( F32( field ), F32( field + 4 ), F32( field + 8 ), F32( field + 12 ) );
	public ReadOnlySpan<byte> Bytes( int field, int length ) => Cache.File.Span( Offset + field, length );
	public TagClass Class( int field ) => new( U32( field ) );

	// A tag block: i32 count, u32 packed pointer, u32 0.
	public int Count( int field ) => Math.Max( 0, I32( field ) );

	public IReadOnlyList<TagView> Block( int field, int elementSize )
	{
		var count = I32( field );
		var pointer = U32( field + 4 );
		if ( count <= 0 || pointer == 0 )
			return Array.Empty<TagView>();

		var first = Cache.PointerOffset( pointer );
		var elements = new TagView[count];
		for ( var i = 0; i < count; i++ )
			elements[i] = new TagView( Cache, first + (long)i * elementSize );
		return elements;
	}

	public TagView Element( int field, int elementSize, int index )
	{
		var count = I32( field );
		if ( index < 0 || index >= count )
			return default;
		return new TagView( Cache, Cache.PointerOffset( U32( field + 4 ) ) + (long)index * elementSize );
	}

	// A tag reference: u32 group, 0, 0, u32 datum.
	public TagEntry Reference( int field ) => Cache.GetTag( U32( field + 12 ) );

	// A data reference: i32 size, 0, 0, u32 packed pointer, 0.
	public ReadOnlySpan<byte> Data( int field )
	{
		var size = I32( field );
		var pointer = U32( field + 12 );
		if ( size <= 0 || pointer == 0 )
			return ReadOnlySpan<byte>.Empty;
		return Cache.File.Span( Cache.PointerOffset( pointer ), size );
	}

	public string StringId( int field ) => Cache.GetString( U32( field ) );
}
