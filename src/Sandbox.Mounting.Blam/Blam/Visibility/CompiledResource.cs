using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Blam.Visibility;

// Source 2 compiled resource files (_c): a header, a block table and the blocks, as s&box loads them.
public static class CompiledResource
{
	const ushort HeaderVersion = 12;

	public static byte[] Write( ushort version, params (string Name, byte[] Data)[] blocks )
	{
		var tableEnd = 16 + 12 * blocks.Length;
		var offsets = new int[blocks.Length];
		var position = tableEnd;
		for ( var i = 0; i < blocks.Length; i++ )
		{
			position = Align( position, 16 );
			offsets[i] = position;
			position += blocks[i].Data.Length;
		}

		var file = new byte[position];
		var w = new BinaryWriter( new MemoryStream( file ) );
		w.Write( position );
		w.Write( HeaderVersion );
		w.Write( version );
		w.Write( 8 ); // the block table, relative to this field
		w.Write( blocks.Length );
		for ( var i = 0; i < blocks.Length; i++ )
		{
			var field = 16 + 12 * i + 4;
			w.Write( Encoding.ASCII.GetBytes( blocks[i].Name ) );
			w.Write( offsets[i] - field );
			w.Write( blocks[i].Data.Length );
			blocks[i].Data.CopyTo( file, offsets[i] );
		}
		return file;
	}

	// The id a resource is referenced by: MurmurHash64B of its lowercase path, seed 0xEDABCDEF.
	public static ulong ResourceId( string path ) => MurmurHash64B( Encoding.UTF8.GetBytes( path.ToLowerInvariant() ), 0xEDABCDEF );

	// External references (RERL): each resource's id and path.
	public static byte[] References( IReadOnlyList<string> paths )
	{
		var stream = new MemoryStream();
		var w = new BinaryWriter( stream );
		w.Write( 8 );
		w.Write( paths.Count );
		var stringsAt = 8 + 16 * paths.Count;
		var strings = new MemoryStream();
		for ( var i = 0; i < paths.Count; i++ )
		{
			var field = 8 + 16 * i + 8;
			w.Write( ResourceId( paths[i] ) );
			w.Write( (int)(stringsAt + strings.Length - field) );
			w.Write( 0 );
			strings.Write( Encoding.UTF8.GetBytes( paths[i] ) );
			strings.WriteByte( 0 );
		}
		w.Write( strings.ToArray() );
		return stream.ToArray();
	}

	// A resource manifest (vrman): what a world loads, as one array of paths.
	public static byte[] Manifest( IReadOnlyList<string> paths )
	{
		var stream = new MemoryStream();
		var w = new BinaryWriter( stream );
		w.Write( 8 );
		w.Write( 1 );
		w.Write( 8 );
		w.Write( paths.Count );
		var first = (int)stream.Length;
		var stringsAt = first + 4 * paths.Count;
		var strings = new MemoryStream();
		for ( var i = 0; i < paths.Count; i++ )
		{
			w.Write( (int)(stringsAt + strings.Length - (first + 4 * i)) );
			strings.Write( Encoding.UTF8.GetBytes( paths[i] ) );
			strings.WriteByte( 0 );
		}
		w.Write( strings.ToArray() );
		return Write( 1, ("RERL", References( paths )), ("DATA", stream.ToArray()) );
	}

	static int Align( int value, int alignment ) => (value + alignment - 1) / alignment * alignment;

	static ulong MurmurHash64B( byte[] data, ulong seed )
	{
		const uint m = 0x5bd1e995;
		const int r = 24;
		var h1 = (uint)seed ^ (uint)data.Length;
		var h2 = (uint)(seed >> 32);
		var i = 0;
		var n = data.Length;
		while ( n >= 8 )
		{
			var k1 = BitConverter.ToUInt32( data, i );
			k1 *= m; k1 ^= k1 >> r; k1 *= m; h1 *= m; h1 ^= k1;
			var k2 = BitConverter.ToUInt32( data, i + 4 );
			k2 *= m; k2 ^= k2 >> r; k2 *= m; h2 *= m; h2 ^= k2;
			i += 8;
			n -= 8;
		}
		if ( n >= 4 )
		{
			var k1 = BitConverter.ToUInt32( data, i );
			k1 *= m; k1 ^= k1 >> r; k1 *= m; h1 *= m; h1 ^= k1;
			i += 4;
			n -= 4;
		}
		if ( n == 3 ) h2 ^= (uint)data[i + 2] << 16;
		if ( n >= 2 ) h2 ^= (uint)data[i + 1] << 8;
		if ( n >= 1 ) { h2 ^= data[i]; h2 *= m; }
		h1 ^= h2 >> 18; h1 *= m;
		h2 ^= h1 >> 22; h2 *= m;
		h1 ^= h2 >> 17; h1 *= m;
		h2 ^= h1 >> 19; h2 *= m;
		return ((ulong)h1 << 32) | h2;
	}
}

// A VPK (version 2) holding its files in the directory file itself.
public static class VpkWriter
{
	public static byte[] Write( IReadOnlyDictionary<string, byte[]> files )
	{
		var tree = new MemoryStream();
		var data = new MemoryStream();
		var t = new BinaryWriter( tree );

		foreach ( var byExtension in files.GroupBy( f => Path.GetExtension( f.Key ).TrimStart( '.' ) ) )
		{
			CString( t, byExtension.Key );
			foreach ( var byFolder in byExtension.GroupBy( f => Path.GetDirectoryName( f.Key )?.Replace( '\\', '/' ) is { Length: > 0 } d ? d : " " ) )
			{
				CString( t, byFolder.Key );
				foreach ( var (path, bytes) in byFolder )
				{
					CString( t, Path.GetFileNameWithoutExtension( path ) );
					t.Write( Crc32( bytes ) );
					t.Write( (ushort)0 ); // preload bytes
					t.Write( (ushort)0x7FFF ); // in this file
					t.Write( (uint)data.Length );
					t.Write( (uint)bytes.Length );
					t.Write( (ushort)0xFFFF );
					data.Write( bytes );
				}
				t.Write( (byte)0 );
			}
			t.Write( (byte)0 );
		}
		t.Write( (byte)0 );

		var output = new MemoryStream();
		var w = new BinaryWriter( output );
		w.Write( 0x55AA1234u );
		w.Write( 2u );
		w.Write( (uint)tree.Length );
		w.Write( (uint)data.Length );
		w.Write( 0u ); // archive MD5 section
		w.Write( 0u ); // other MD5 section
		w.Write( 0u ); // signature section
		w.Write( tree.ToArray() );
		w.Write( data.ToArray() );
		return output.ToArray();
	}

	static uint Crc32( byte[] bytes )
	{
		var crc = 0xFFFFFFFFu;
		foreach ( var b in bytes )
		{
			crc ^= b;
			for ( var k = 0; k < 8; k++ )
				crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
		}
		return ~crc;
	}

	static void CString( BinaryWriter w, string s )
	{
		w.Write( Encoding.UTF8.GetBytes( s ) );
		w.Write( (byte)0 );
	}
}
