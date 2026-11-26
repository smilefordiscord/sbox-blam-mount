using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using Blam.IO;

namespace Blam.Halo2;

/// <summary>
/// <c>textures.dat</c>, which holds the pixels of every bitmap in every Halo 2 map.
/// </summary>
/// <remarks>
/// A bitmap's pixel offset points at an entry: <c>i32 n</c>, <c>i32 size[n]</c>, then the n chunks back to back.
/// A positive size is a zlib stream, a negative one that many bytes stored raw. Every chunk but the last inflates
/// to 0x40000 bytes, and together they are the bitmap's whole mip chain (every face of a level before the next level).
/// </remarks>
public sealed class TextureFile : IDisposable
{
	readonly RawFile _file;

	public string FilePath => _file.Path;

	public TextureFile( string path )
	{
		_file = new RawFile( path );
	}

	/// <summary>
	/// Inflates the entry at <paramref name="offset"/>. <paramref name="expectedLength"/> is the pixel data size
	/// the bitmap's description works out to.
	/// </summary>
	public byte[] Read( uint offset, int expectedLength )
	{
		var count = BinaryPrimitives.ReadInt32LittleEndian( _file.Read( offset, 4 ) );
		if ( count <= 0 || count > 4096 )
			throw new InvalidDataException( $"Bad chunk count {count} at 0x{offset:X} in '{_file.Path}'" );

		var sizes = _file.Read( offset + 4, count * 4 );
		var result = new byte[expectedLength];
		var position = offset + 4 + (long)count * 4;
		var written = 0;

		for ( var i = 0; i < count; i++ )
		{
			var size = BinaryPrimitives.ReadInt32LittleEndian( sizes.AsSpan( i * 4 ) );
			var stored = _file.Read( position, Math.Abs( size ) );
			position += Math.Abs( size );

			if ( size < 0 )
			{
				var n = Math.Min( stored.Length, result.Length - written );
				stored.AsSpan( 0, n ).CopyTo( result.AsSpan( written ) );
				written += n;
				continue;
			}

			using var input = new MemoryStream( stored );
			using var zlib = new ZLibStream( input, CompressionMode.Decompress );
			int read;
			while ( written < result.Length && (read = zlib.Read( result, written, result.Length - written )) > 0 )
				written += read;
		}

		if ( written != expectedLength )
			throw new InvalidDataException( $"Texture entry at 0x{offset:X} inflated to {written} bytes, expected {expectedLength}" );

		return result;
	}

	public void Dispose() => _file.Dispose();
}
