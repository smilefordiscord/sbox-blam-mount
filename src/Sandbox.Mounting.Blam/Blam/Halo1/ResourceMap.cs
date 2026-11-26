using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Blam.IO;

namespace Blam.Halo1;

public enum ResourceMapType : uint
{
	Bitmaps = 1,
	Sounds = 2,
	Localization = 3,
}

/// <summary>
/// A shared resource map such as <c>bitmaps.map</c>. Cache files point into these instead of
/// storing data that many maps have in common.
/// </summary>
/// <remarks>
/// Header: type, offset to the path strings, offset to the resource table, resource count.
/// Each resource table entry is 12 bytes: path offset (relative to the path strings), size, data offset.
/// </remarks>
public sealed class ResourceMap : IDisposable
{
	public readonly record struct Resource( int Index, string Path, uint Offset, uint Size );

	readonly RawFile _file;
	readonly Resource[] _resources;

	public ResourceMapType Type { get; }
	public IReadOnlyList<Resource> Resources => _resources;
	public string FilePath => _file.Path;

	public ResourceMap( string path )
	{
		_file = new RawFile( path );

		try
		{
			var header = _file.Read( 0, 16 );
			Type = (ResourceMapType)BinaryPrimitives.ReadUInt32LittleEndian( header );
			var pathsOffset = BinaryPrimitives.ReadUInt32LittleEndian( header.AsSpan( 4 ) );
			var tableOffset = BinaryPrimitives.ReadUInt32LittleEndian( header.AsSpan( 8 ) );
			var count = BinaryPrimitives.ReadInt32LittleEndian( header.AsSpan( 12 ) );

			if ( !Enum.IsDefined( Type ) || count < 0 || tableOffset + (long)count * 12 > _file.Length )
				throw new InvalidDataException( $"'{path}' is not a Halo CE resource map" );

			var table = _file.Read( tableOffset, count * 12 );
			var paths = _file.Read( pathsOffset, (int)(tableOffset - pathsOffset) );

			_resources = new Resource[count];
			for ( var i = 0; i < count; i++ )
			{
				var entry = table.AsSpan( i * 12 );
				var pathOffset = BinaryPrimitives.ReadInt32LittleEndian( entry );
				var size = BinaryPrimitives.ReadUInt32LittleEndian( entry[4..] );
				var offset = BinaryPrimitives.ReadUInt32LittleEndian( entry[8..] );

				_resources[i] = new Resource( i, ReadString( paths, pathOffset ), offset, size );
			}
		}
		catch
		{
			_file.Dispose();
			throw;
		}
	}

	static string ReadString( byte[] data, int offset )
	{
		if ( offset < 0 || offset >= data.Length ) return null;

		var span = data.AsSpan( offset );
		var end = span.IndexOf( (byte)0 );
		return Encoding.ASCII.GetString( end < 0 ? span : span[..end] );
	}

	/// <summary>
	/// Reads raw bytes at an absolute offset in the resource map.
	/// </summary>
	public byte[] Read( long offset, int length ) => _file.Read( offset, length );

	public byte[] Read( in Resource resource ) => _file.Read( resource.Offset, (int)resource.Size );

	public void Dispose() => _file.Dispose();
}
