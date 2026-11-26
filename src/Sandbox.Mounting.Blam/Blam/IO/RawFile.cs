using System;
using System.IO;
using Microsoft.Win32.SafeHandles;

namespace Blam.IO;

/// <summary>
/// A read-only file handle that can be read at any offset from multiple threads at once.
/// s&amp;box loads resources in parallel, so everything here goes through positional reads
/// instead of a shared stream position.
/// </summary>
public sealed class RawFile : IDisposable
{
	readonly SafeFileHandle _handle;

	public string Path { get; }
	public long Length { get; }

	public RawFile( string path )
	{
		Path = path;
		_handle = File.OpenHandle( path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.RandomAccess );
		Length = RandomAccess.GetLength( _handle );
	}

	public byte[] Read( long offset, int length )
	{
		if ( offset < 0 || length < 0 || offset + length > Length )
			throw new ArgumentOutOfRangeException( nameof( offset ), $"Read of {length} bytes at 0x{offset:X} is outside '{Path}' ({Length} bytes)" );

		var buffer = GC.AllocateUninitializedArray<byte>( length );
		var read = 0;
		while ( read < length )
		{
			var n = RandomAccess.Read( _handle, buffer.AsSpan( read ), offset + read );
			if ( n == 0 ) throw new EndOfStreamException( $"Unexpected end of '{Path}' at 0x{offset + read:X}" );
			read += n;
		}

		return buffer;
	}

	public void Dispose() => _handle.Dispose();
}
