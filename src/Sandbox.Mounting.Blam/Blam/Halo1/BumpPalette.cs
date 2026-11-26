using System;
using System.IO;

namespace Blam.Halo1;

/// <summary>
/// The normal palette that P8 bump maps index into. It isn't stored in the maps: the game has a
/// fixed table of 256 normals built in, so it's read from the installed game's <c>halo1.dll</c>.
/// </summary>
/// <remarks>
/// The table is 256 entries of 4 bytes, blue/green/red/alpha, each a tangent space normal
/// (z = blue, mostly pointing out of the surface) mapped from -1..1 to 0..255, alpha 255.
/// Index 255 is the flat, transparent entry (FF 80 80 00) that alpha-tested shaders use to cut
/// holes, and a few entries before it are unused (all zero). It's found by looking for that last
/// entry and checking the table before it is made of unit vectors, so it doesn't depend on where
/// a particular build puts it.
/// </remarks>
public static class BumpPalette
{
	const int Entries = 256;
	const int TableSize = Entries * 4;

	/// <summary>
	/// The palette as RGBA (x, y, z, alpha) per index, or null to decode P8 bump maps as flat.
	/// </summary>
	public static byte[] Rgba { get; set; }

	/// <summary>
	/// Finds the palette in a <c>halo1.dll</c> and returns it as RGBA, or null if it isn't there.
	/// </summary>
	public static byte[] Find( string dllPath )
	{
		if ( !File.Exists( dllPath ) )
			return null;

		var data = File.ReadAllBytes( dllPath );
		ReadOnlySpan<byte> lastEntry = [0xFF, 0x80, 0x80, 0x00];

		var searchFrom = TableSize - 4;
		while ( searchFrom < data.Length )
		{
			var found = data.AsSpan( searchFrom ).IndexOf( lastEntry );
			if ( found < 0 )
				return null;

			var tableStart = searchFrom + found - (TableSize - 4);
			if ( IsNormalTable( data.AsSpan( tableStart, TableSize ) ) )
				return ToRgba( data.AsSpan( tableStart, TableSize ) );

			searchFrom += found + 1;
		}

		return null;
	}

	/// <summary>
	/// Whether every entry but the last is either unused (zero) or an opaque unit normal facing out
	/// of the surface, with most of them in use.
	/// </summary>
	static bool IsNormalTable( ReadOnlySpan<byte> table )
	{
		var normals = 0;
		for ( var i = 0; i < Entries - 1; i++ )
		{
			var e = table.Slice( i * 4, 4 );
			if ( e[0] == 0 && e[1] == 0 && e[2] == 0 && e[3] == 0 )
				continue;

			float z = e[0] / 127.5f - 1, y = e[1] / 127.5f - 1, x = e[2] / 127.5f - 1;
			var length = x * x + y * y + z * z;
			if ( e[3] != 255 || z < 0 || length < 0.9f || length > 1.1f )
				return false;

			normals++;
		}

		return normals >= 200;
	}

	static byte[] ToRgba( ReadOnlySpan<byte> table )
	{
		var rgba = new byte[TableSize];
		for ( var i = 0; i < Entries; i++ )
		{
			var e = table.Slice( i * 4, 4 );

			// Unused entries shouldn't be referenced, but give them a flat normal just in case.
			var unused = e[0] == 0 && e[1] == 0 && e[2] == 0;
			rgba[i * 4 + 0] = unused ? (byte)128 : e[2];
			rgba[i * 4 + 1] = unused ? (byte)128 : e[1];
			rgba[i * 4 + 2] = unused ? (byte)255 : e[0];
			rgba[i * 4 + 3] = e[3];
		}

		return rgba;
	}
}
