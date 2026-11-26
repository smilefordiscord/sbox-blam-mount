using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.MemoryMappedFiles;

namespace Blam.IO;

// A read-only memory-mapped file, readable at any offset from any thread; for cache files too big to load whole.
public sealed unsafe class MappedFile : IDisposable
{
	readonly MemoryMappedFile _map;
	readonly MemoryMappedViewAccessor _view;
	readonly byte* _pointer;
	volatile bool _disposed;

	public string Path { get; }
	public long Length { get; }
	public bool IsDisposed => _disposed;

	public MappedFile( string path )
	{
		Path = path;
		Length = new FileInfo( path ).Length;
		using var stream = new FileStream( path, FileMode.Open, FileAccess.Read, FileShare.Read );
		_map = MemoryMappedFile.CreateFromFile( stream, null, 0, MemoryMappedFileAccess.Read, HandleInheritability.None, leaveOpen: false );
		_view = _map.CreateViewAccessor( 0, 0, MemoryMappedFileAccess.Read );
		byte* pointer = null;
		_view.SafeMemoryMappedViewHandle.AcquirePointer( ref pointer );
		_pointer = pointer + _view.PointerOffset;
	}

	public ReadOnlySpan<byte> Span( long offset, int length )
	{
		if ( _disposed )
			throw new ObjectDisposedException( Path );
		if ( offset < 0 || length < 0 || offset + length > Length )
			throw new ArgumentOutOfRangeException( nameof( offset ), $"Read of {length} bytes at 0x{offset:X} is outside '{Path}' ({Length} bytes)" );

		return new ReadOnlySpan<byte>( _pointer + offset, length );
	}

	public byte[] Read( long offset, int length ) => Span( offset, length ).ToArray();

	// A stream over part of the file, e.g. for inflating a compressed page without copying it first.
	public Stream Stream( long offset, int length )
	{
		Span( offset, length );
		return new UnmanagedMemoryStream( _pointer + offset, length );
	}

	public byte U8( long offset ) => Span( offset, 1 )[0];
	public short I16( long offset ) => BinaryPrimitives.ReadInt16LittleEndian( Span( offset, 2 ) );
	public ushort U16( long offset ) => BinaryPrimitives.ReadUInt16LittleEndian( Span( offset, 2 ) );
	public int I32( long offset ) => BinaryPrimitives.ReadInt32LittleEndian( Span( offset, 4 ) );
	public uint U32( long offset ) => BinaryPrimitives.ReadUInt32LittleEndian( Span( offset, 4 ) );
	public ulong U64( long offset ) => BinaryPrimitives.ReadUInt64LittleEndian( Span( offset, 8 ) );
	public float F32( long offset ) => BinaryPrimitives.ReadSingleLittleEndian( Span( offset, 4 ) );

	// A NUL-terminated Latin-1 string.
	public string CString( long offset, int maxLength = 256 )
	{
		var span = Span( offset, (int)Math.Min( maxLength, Length - offset ) );
		var end = span.IndexOf( (byte)0 );
		return System.Text.Encoding.Latin1.GetString( end < 0 ? span : span[..end] );
	}

	public void Dispose()
	{
		if ( _disposed )
			return;
		_disposed = true;
		_view.SafeMemoryMappedViewHandle.ReleasePointer();
		_view.Dispose();
		_map.Dispose();
	}
}
