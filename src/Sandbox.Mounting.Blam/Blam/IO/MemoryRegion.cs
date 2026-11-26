using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace Blam.IO;

// Inside the namespace so these win over s&box's global Vector2/Vector3 when this compiles into the mount.
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;

/// <summary>
/// A block of cache file data loaded into memory, addressed the same way the game addresses it
/// at runtime. Blam stores pointers as virtual addresses relative to where a region gets loaded
/// (for example the tag data at 0x40440000), so tag structures are read by address, not offset.
/// </summary>
public sealed class MemoryRegion
{
	readonly byte[] _data;

	public uint BaseAddress { get; }
	public int Length => _data.Length;

	public MemoryRegion( byte[] data, uint baseAddress )
	{
		_data = data;
		BaseAddress = baseAddress;
	}

	public bool Contains( uint address, int length = 1 )
	{
		long offset = (long)address - BaseAddress;
		return offset >= 0 && offset + length <= _data.Length;
	}

	public ReadOnlySpan<byte> Slice( uint address, int length )
	{
		if ( length == 0 )
			return ReadOnlySpan<byte>.Empty;

		if ( !Contains( address, length ) )
			throw new ArgumentOutOfRangeException( nameof( address ), $"0x{address:X8} (+{length}) is outside region 0x{BaseAddress:X8}-0x{BaseAddress + (uint)_data.Length:X8}" );

		return _data.AsSpan( (int)(address - BaseAddress), length );
	}

	public byte U8( uint address ) => Slice( address, 1 )[0];
	public short I16( uint address ) => BinaryPrimitives.ReadInt16LittleEndian( Slice( address, 2 ) );
	public ushort U16( uint address ) => BinaryPrimitives.ReadUInt16LittleEndian( Slice( address, 2 ) );
	public int I32( uint address ) => BinaryPrimitives.ReadInt32LittleEndian( Slice( address, 4 ) );
	public uint U32( uint address ) => BinaryPrimitives.ReadUInt32LittleEndian( Slice( address, 4 ) );
	public float F32( uint address ) => BinaryPrimitives.ReadSingleLittleEndian( Slice( address, 4 ) );
	public TagClass Class( uint address ) => new( U32( address ) );
	public Vector2 Vec2( uint address ) => new( F32( address ), F32( address + 4 ) );
	public Vector3 Vec3( uint address ) => new( F32( address ), F32( address + 4 ), F32( address + 8 ) );

	/// <summary>
	/// Returns the address of each element of a tag block. A block is stored as a 12 byte
	/// count, pointer to the first element, and a definition pointer the game uses at runtime.
	/// </summary>
	public IEnumerable<uint> Block( uint address, int elementSize )
	{
		var count = I32( address );
		var first = U32( address + 4 );
		for ( var i = 0; i < count; i++ )
			yield return first + (uint)(i * elementSize);
	}

	/// <summary>
	/// Reads a null-terminated ASCII string.
	/// </summary>
	public string CString( uint address, int maxLength = 256 )
	{
		if ( !Contains( address ) ) return null;

		var available = (int)Math.Min( maxLength, _data.Length - (address - BaseAddress) );
		var span = Slice( address, available );
		var end = span.IndexOf( (byte)0 );
		return Encoding.ASCII.GetString( end < 0 ? span : span[..end] );
	}
}
