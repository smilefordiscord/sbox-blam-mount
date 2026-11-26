using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Blam.Visibility;

// A KV3 object whose members keep the order they were added in.
public sealed class Kv3Object : List<(string Name, object Value)>
{
	public void Add( string name, object value ) => Add( (name, value) );
}

// Writes binary KV3 version 2, uncompressed, the version s&box's resource compiler writes (layout from ValveResourceFormat's reader, MIT).
// Values: Kv3Object, object lists, bool, int, uint, long, ulong, float/double (stored as double), string.
public static class Kv3Writer
{
	const uint Magic = 0x4B563302;
	const uint Trailer = 0xFFEEDD00;

	// The generic KV3 format, version{7412167c-06e9-4698-aff2-e63eb59037e7}.
	static readonly byte[] GenericFormat = Convert.FromHexString( "7c161274e9069846aff2e63eb59037e7" );

	enum NodeType : byte
	{
		Int64 = 3,
		UInt64 = 4,
		Double = 5,
		String = 6,
		Array = 8,
		Object = 9,
		Int32 = 11,
		UInt32 = 12,
		True = 13,
		False = 14,
	}

	sealed class Lanes
	{
		public readonly MemoryStream Bytes4 = new();
		public readonly MemoryStream Bytes8 = new();
		public readonly MemoryStream Types = new();
		public readonly Dictionary<string, int> StringIds = new();
		public readonly List<string> Strings = new();
		public int Objects;
		public int Arrays;
		public int Bytes4Count;

		public void Int( int value )
		{
			Span<byte> b = stackalloc byte[4];
			BitConverter.TryWriteBytes( b, value );
			Bytes4.Write( b );
			Bytes4Count++;
		}

		public void Long( long value )
		{
			Span<byte> b = stackalloc byte[8];
			BitConverter.TryWriteBytes( b, value );
			Bytes8.Write( b );
		}

		public int StringId( string value )
		{
			if ( string.IsNullOrEmpty( value ) )
				return -1;
			if ( !StringIds.TryGetValue( value, out var id ) )
			{
				id = Strings.Count;
				StringIds[value] = id;
				Strings.Add( value );
			}
			return id;
		}
	}

	public static byte[] Write( Kv3Object root )
	{
		var lanes = new Lanes();
		Value( lanes, root );

		var buffer = new MemoryStream();
		var writer = new BinaryWriter( buffer );

		// No 1-byte values: the 4-byte lane starts the buffer, its first value the string count.
		writer.Write( lanes.Strings.Count );
		writer.Write( lanes.Bytes4.ToArray() );
		while ( buffer.Length % 8 != 0 )
			writer.Write( (byte)0 );
		writer.Write( lanes.Bytes8.ToArray() );

		var stringsStart = buffer.Length;
		foreach ( var s in lanes.Strings )
		{
			writer.Write( Encoding.UTF8.GetBytes( s ) );
			writer.Write( (byte)0 );
		}
		writer.Write( lanes.Types.ToArray() );
		var stringsAndTypes = (int)(buffer.Length - stringsStart);
		writer.Write( Trailer );

		var body = buffer.ToArray();
		var output = new MemoryStream();
		var header = new BinaryWriter( output );
		header.Write( Magic );
		header.Write( GenericFormat );
		header.Write( 0u ); // uncompressed
		header.Write( (ushort)0 ); // compression dictionary
		header.Write( (ushort)0 ); // compression frame size
		header.Write( 0 ); // 1-byte values
		header.Write( lanes.Bytes4Count + 1 );
		header.Write( (int)(lanes.Bytes8.Length / 8) );
		header.Write( stringsAndTypes );
		header.Write( (ushort)Math.Min( lanes.Objects, ushort.MaxValue ) );
		header.Write( (ushort)Math.Min( lanes.Arrays, ushort.MaxValue ) );
		header.Write( body.Length );
		header.Write( body.Length );
		header.Write( 0 ); // blocks
		header.Write( 0 ); // binary blob bytes
		header.Write( body );
		return output.ToArray();
	}

	static void Value( Lanes lanes, object value )
	{
		switch ( value )
		{
			case Kv3Object obj:
				Type( lanes, NodeType.Object );
				lanes.Objects++;
				lanes.Int( obj.Count );
				foreach ( var (name, member) in obj )
				{
					// Lanes are read separately, so the name can go in before the value's type is.
					lanes.Int( lanes.StringId( name ) );
					Value( lanes, member );
				}
				break;
			case System.Collections.IList list when value is not string:
				Type( lanes, NodeType.Array );
				lanes.Arrays++;
				lanes.Int( list.Count );
				foreach ( var item in list )
					Value( lanes, item );
				break;
			case bool b:
				Type( lanes, b ? NodeType.True : NodeType.False );
				break;
			case int i:
				Type( lanes, NodeType.Int32 );
				lanes.Int( i );
				break;
			case uint u:
				Type( lanes, NodeType.UInt32 );
				lanes.Int( unchecked( (int)u ) );
				break;
			case long l:
				Type( lanes, NodeType.Int64 );
				lanes.Long( l );
				break;
			case ulong ul:
				Type( lanes, NodeType.UInt64 );
				lanes.Long( unchecked( (long)ul ) );
				break;
			case float f:
				Type( lanes, NodeType.Double );
				lanes.Long( BitConverter.DoubleToInt64Bits( f ) );
				break;
			case double d:
				Type( lanes, NodeType.Double );
				lanes.Long( BitConverter.DoubleToInt64Bits( d ) );
				break;
			case string s:
				Type( lanes, NodeType.String );
				lanes.Int( lanes.StringId( s ) );
				break;
			default:
				throw new ArgumentException( $"KV3 can't hold a {value?.GetType().Name ?? "null"}" );
		}
	}

	static void Type( Lanes lanes, NodeType type ) => lanes.Types.WriteByte( (byte)type );
}
