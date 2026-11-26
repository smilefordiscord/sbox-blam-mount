using System;
using System.Buffers.Binary;

namespace Halo.Combat;


// The command bits for everything a Spartan does besides moving.
public static class CombatButtons {
	public const ushort Fire = 1, Zoom = 2, Melee = 4, Grenade = 8, Reload = 16, Switch = 32, SwitchGrenade = 64,
		Use = 128, Flashlight = 256, SecondaryFire = 512;

	public const ushort All = 1023;
}

// Little-endian writer into a byte array, for RPC payloads and headless tests.
public ref struct NetWriter {
	byte[] _buffer;
	int _length;

	public NetWriter( int capacity ) {
		_buffer = new byte[Math.Max( 16, capacity )];
		_length = 0;
	}

	public readonly int Length => _length;

	Span<byte> Take( int count ) {
		if ( _length + count > _buffer.Length )
			Array.Resize( ref _buffer, Math.Max( _buffer.Length * 2, _length + count ) );
		var span = _buffer.AsSpan( _length, count );
		_length += count;
		return span;
	}

	public void Byte( byte v ) => Take( 1 )[0] = v;
	public void Short( short v ) => BinaryPrimitives.WriteInt16LittleEndian( Take( 2 ), v );
	public void UShort( ushort v ) => BinaryPrimitives.WriteUInt16LittleEndian( Take( 2 ), v );
	public void Int( int v ) => BinaryPrimitives.WriteInt32LittleEndian( Take( 4 ), v );
	public void Float( float v ) => BinaryPrimitives.WriteInt32LittleEndian( Take( 4 ), BitConverter.SingleToInt32Bits( v ) );
	public void Vector( Vector3 v ) { Float( v.x ); Float( v.y ); Float( v.z ); }

	public readonly byte[] ToArray() => _buffer.AsSpan( 0, _length ).ToArray();
}

// Reads what NetWriter wrote; reading past the end sets Failed and returns zeros.
public ref struct NetReader {
	readonly ReadOnlySpan<byte> _data;
	int _position;

	public NetReader( ReadOnlySpan<byte> data ) {
		_data = data;
		_position = 0;
		Failed = false;
	}

	public bool Failed { get; private set; }
	public readonly int Remaining => _data.Length - _position;

	ReadOnlySpan<byte> Take( int count ) {
		if ( _position + count > _data.Length ) {
			Failed = true;
			_position = _data.Length;
			return new byte[count];
		}
		var span = _data.Slice( _position, count );
		_position += count;
		return span;
	}

	public byte Byte() => Take( 1 )[0];
	public short Short() => BinaryPrimitives.ReadInt16LittleEndian( Take( 2 ) );
	public ushort UShort() => BinaryPrimitives.ReadUInt16LittleEndian( Take( 2 ) );
	public int Int() => BinaryPrimitives.ReadInt32LittleEndian( Take( 4 ) );
	public float Float() => BitConverter.Int32BitsToSingle( BinaryPrimitives.ReadInt32LittleEndian( Take( 4 ) ) );
	public Vector3 Vector() => new( Float(), Float(), Float() );
}
