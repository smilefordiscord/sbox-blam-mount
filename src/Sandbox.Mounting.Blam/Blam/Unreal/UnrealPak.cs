using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Blam.IO;

namespace Blam.Unreal;

/// <summary>
/// One file in an <see cref="UnrealPak"/>.
/// </summary>
/// <param name="Path">The file's path inside the pak, mount point included (<c>../../../MCC/Content/...</c>).</param>
/// <param name="DataOffset">Where the file's bytes start in the pak, after the entry header that repeats before them.</param>
public sealed record UnrealPakEntry( string Path, long DataOffset, long Size, bool Compressed, bool Encrypted );

/// <summary>
/// An Unreal Engine 4 .pak file, as MCC's menus ship theirs (<c>mcc/Content/Paks/MCC-WindowsNoEditor.pak</c>):
/// pak version 7 with an unencrypted index, and every file stored as is. Only reads stored files.
/// </summary>
/// <remarks>
/// Layout: the footer's last 44 bytes are (magic, version, index offset, index size, index hash),
/// with the compression method names in front of them from version 8 on. The index is the mount
/// point, the entry count, then per entry its path and the same record that precedes the file's
/// bytes in the pak: offset, size, uncompressed size, compression method, SHA-1, compression blocks
/// (compressed files only), an encrypted flag and the compression block size.
/// </remarks>
public sealed class UnrealPak : IDisposable
{
	const uint Magic = 0x5A6F12E1;
	const int FooterSize = 44;

	readonly RawFile _file;
	readonly Dictionary<string, UnrealPakEntry> _entries = new( StringComparer.OrdinalIgnoreCase );

	public string FilePath => _file.Path;
	public int Version { get; }
	public string MountPoint { get; }
	public IReadOnlyCollection<UnrealPakEntry> Entries => _entries.Values;

	public UnrealPak( string path )
	{
		_file = new RawFile( path );
		try
		{
			// Version 8+ adds 32-byte compression method names before the footer; the 44-byte tail
			// is the same in every version this reads.
			var footer = _file.Read( _file.Length - FooterSize, FooterSize );
			if ( BitConverter.ToUInt32( footer, 0 ) != Magic )
				throw new InvalidDataException( $"{path} isn't an Unreal pak (no footer magic)" );

			Version = BitConverter.ToInt32( footer, 4 );
			if ( Version < 3 || Version > 8 )
				throw new NotSupportedException( $"{path}: pak version {Version} isn't supported" );

			var indexOffset = BitConverter.ToInt64( footer, 8 );
			var indexSize = BitConverter.ToInt64( footer, 16 );
			var index = new UnrealReader( _file.Read( indexOffset, checked((int)indexSize) ) );

			MountPoint = index.ReadFString();
			var count = index.ReadInt32();
			for ( var i = 0; i < count; i++ )
			{
				var name = index.ReadFString();
				var start = index.Position;
				var offset = index.ReadInt64();
				var size = index.ReadInt64();
				index.ReadInt64(); // uncompressed size
				var compression = index.ReadInt32();
				index.Skip( 20 ); // SHA-1
				if ( compression != 0 )
					index.Skip( index.ReadInt32() * 16 );
				var encrypted = index.ReadByte() != 0;
				index.ReadInt32(); // compression block size

				// The same record precedes the file's bytes, with the offset field there unused.
				var headerSize = index.Position - start;
				var entry = new UnrealPakEntry( MountPoint + name, offset + headerSize, size, compression != 0, encrypted );
				_entries[entry.Path] = entry;
			}
		}
		catch
		{
			_file.Dispose();
			throw;
		}
	}

	/// <summary>
	/// The entry at a path inside the pak (mount point included), or null. Not case sensitive.
	/// </summary>
	public UnrealPakEntry Find( string path ) => _entries.GetValueOrDefault( path );

	/// <summary>
	/// A stored file's bytes, or part of them.
	/// </summary>
	public byte[] Read( UnrealPakEntry entry, long offset = 0, int length = -1 )
	{
		if ( entry.Compressed || entry.Encrypted )
			throw new NotSupportedException( $"{entry.Path} is compressed or encrypted" );

		if ( length < 0 )
			length = checked((int)(entry.Size - offset));
		if ( offset < 0 || offset + length > entry.Size )
			throw new ArgumentOutOfRangeException( nameof( offset ), $"Read of {length} bytes at {offset} is outside {entry.Path} ({entry.Size} bytes)" );

		return _file.Read( entry.DataOffset + offset, length );
	}

	public void Dispose() => _file.Dispose();
}

/// <summary>
/// Little-endian reads of Unreal's serialized types from a byte array.
/// </summary>
public sealed class UnrealReader( byte[] data, int position = 0 )
{
	public byte[] Data { get; } = data;
	public int Position { get; set; } = position;

	public byte ReadByte() => Data[Position++];
	public int ReadInt32() { var v = BitConverter.ToInt32( Data, Position ); Position += 4; return v; }
	public uint ReadUInt32() { var v = BitConverter.ToUInt32( Data, Position ); Position += 4; return v; }
	public long ReadInt64() { var v = BitConverter.ToInt64( Data, Position ); Position += 8; return v; }
	public float ReadSingle() { var v = BitConverter.ToSingle( Data, Position ); Position += 4; return v; }
	public void Skip( int count ) => Position += count;

	public byte[] ReadBytes( int count )
	{
		var bytes = Data.AsSpan( Position, count ).ToArray();
		Position += count;
		return bytes;
	}

	/// <summary>
	/// A length-prefixed string: a positive length is that many Latin-1 bytes, a negative one that
	/// many UTF-16 characters, both with a terminating zero.
	/// </summary>
	public string ReadFString()
	{
		var length = ReadInt32();
		if ( length == 0 )
			return "";

		string value;
		if ( length < 0 )
		{
			value = Encoding.Unicode.GetString( Data, Position, -length * 2 );
			Position += -length * 2;
		}
		else
		{
			value = Encoding.Latin1.GetString( Data, Position, length );
			Position += length;
		}

		return value.TrimEnd( '\0' );
	}
}
