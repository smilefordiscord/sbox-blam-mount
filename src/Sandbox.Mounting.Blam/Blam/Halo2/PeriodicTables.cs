using System;
using System.Buffers.Binary;
using System.IO;

namespace Blam.Halo2;

/// <summary>
/// Halo 2's periodic function shapes as the engine has them: one table of 1024 bytes per shape (0-11) in halo2.dll,
/// read from the user's install rather than shipped. Without them <see cref="Tags.MappingFunction.PeriodicShape"/>
/// falls back to closed forms, which are exact for the plain waves but only stand-ins for the random ones (noise,
/// jitter, wander, spark) and the variable-period ones.
/// </summary>
/// <remarks>
/// The engine samples a table at x · 1024/28 (it holds 28 periods; one period per unit of x), wrapped to 1024 entries,
/// blending the two nearest entries by the fraction of the index; the slide shapes (6, 7) blend across their wrap
/// and stay in [0, 1). The tables live at a fixed address of MCC's halo2.dll, which is no longer updated; they are
/// checked against shapes known exactly (one, zero, cosine) before use.
/// </remarks>
public static class PeriodicTables
{
	const int ShapeCount = 12;
	const int TableSize = 1024;

	/// <summary>The tables' address in halo2.dll (relative virtual address, MCC's last update).</summary>
	const uint TablesRva = 0xDFF500;

	/// <summary>Table entries per unit of x: 1024 / 28.</summary>
	const float EntriesPerUnit = 36.571430f;

	static byte[] _tables;

	public static bool IsLoaded => _tables is not null;

	/// <summary>
	/// Reads the tables from halo2.dll, which sits in the game folder above the maps folder. Returns whether they
	/// were found and look right; keeps the ones already loaded otherwise.
	/// </summary>
	public static bool TryLoad( string halo2Dll )
	{
		try
		{
			if ( _tables is not null )
				return true;
			if ( !File.Exists( halo2Dll ) )
				return false;

			var file = File.ReadAllBytes( halo2Dll );
			var offset = FileOffset( file, TablesRva );
			if ( offset < 0 || offset + ShapeCount * TableSize > file.Length )
				return false;

			var tables = file.AsSpan( offset, ShapeCount * TableSize ).ToArray();
			if ( !LooksRight( tables ) )
				return false;

			_tables = tables;
			return true;
		}
		catch ( IOException )
		{
			return false;
		}
		catch ( UnauthorizedAccessException )
		{
			return false;
		}
	}

	/// <summary>
	/// The shape at <paramref name="x"/> the engine's way, or null without the tables or for a shape outside them.
	/// </summary>
	public static float? Sample( int shape, float x )
	{
		if ( _tables is null || shape < 0 || shape >= ShapeCount )
			return null;
		if ( shape == 0 )
			return 1;

		// The index: x scaled and wrapped (keeping its sign), its whole part rounded away from zero and wrapped to
		// the table; the fraction (signed too) blends towards the next entry.
		var index = (x * EntriesPerUnit) % TableSize;
		var fraction = index % 1f;
		var whole = index - fraction;
		whole += whole >= 0 ? 0.5f : -0.5f;
		var i = (int)whole & (TableSize - 1);

		var table = shape * TableSize;
		var a = _tables[table + i] / 255f;
		var b = _tables[table + ((i + 1) & (TableSize - 1))] / 255f;

		if ( shape is 6 or 7 )
		{
			// The slides blend across their wrap from 1 back to 0.
			if ( a > 0.75f && b < 0.25f )
				b += 1;

			var value = (1 - fraction) * a + fraction * b;
			return value > 1 ? value - 1 : value;
		}

		return (1 - fraction) * a + fraction * b;
	}

	static bool LooksRight( byte[] tables )
	{
		for ( var i = 0; i < TableSize; i++ )
		{
			if ( tables[i] != 255 || tables[TableSize + i] != 0 )
				return false;
		}

		// Cosine: 1 at 0, 0 half a period on.
		var cosine = 2 * TableSize;
		return tables[cosine] == 255 && tables[cosine + 18] < 3 && tables[cosine + 36] > 250;
	}

	/// <summary>
	/// The file offset of a relative virtual address in a PE image, or -1.
	/// </summary>
	static int FileOffset( byte[] file, uint rva )
	{
		if ( file.Length < 0x40 || file[0] != 'M' || file[1] != 'Z' )
			return -1;

		var pe = BinaryPrimitives.ReadInt32LittleEndian( file.AsSpan( 0x3C ) );
		if ( pe < 0 || pe + 24 > file.Length || BinaryPrimitives.ReadUInt32LittleEndian( file.AsSpan( pe ) ) != 0x00004550 )
			return -1;

		var sections = BinaryPrimitives.ReadUInt16LittleEndian( file.AsSpan( pe + 6 ) );
		var optionalSize = BinaryPrimitives.ReadUInt16LittleEndian( file.AsSpan( pe + 20 ) );
		var table = pe + 24 + optionalSize;
		for ( var s = 0; s < sections && table + (s + 1) * 40 <= file.Length; s++ )
		{
			var section = file.AsSpan( table + s * 40 );
			var virtualSize = BinaryPrimitives.ReadUInt32LittleEndian( section[8..] );
			var virtualAddress = BinaryPrimitives.ReadUInt32LittleEndian( section[12..] );
			var rawSize = BinaryPrimitives.ReadUInt32LittleEndian( section[16..] );
			var rawPointer = BinaryPrimitives.ReadUInt32LittleEndian( section[20..] );
			if ( rva >= virtualAddress && rva < virtualAddress + Math.Max( virtualSize, rawSize ) && rva - virtualAddress < rawSize )
				return (int)(rawPointer + (rva - virtualAddress));
		}

		return -1;
	}
}
