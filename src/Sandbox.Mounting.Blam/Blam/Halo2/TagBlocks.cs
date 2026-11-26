using System.Collections.Generic;
using Blam.IO;

namespace Blam.Halo2;

/// <summary>
/// Halo 2 tag data helpers. Its tag blocks are 8 bytes (count, address of the first element) and its tag
/// references 8 bytes (group, tag id), unlike Halo CE's 12 and 16.
/// </summary>
public static class TagBlocks
{
	/// <summary>
	/// The address of each element of the tag block at <paramref name="address"/>.
	/// </summary>
	public static IEnumerable<uint> Elements( this MemoryRegion data, uint address, int elementSize )
	{
		var count = data.I32( address );
		var first = data.U32( address + 4 );
		for ( var i = 0; i < count; i++ )
			yield return first + (uint)(i * elementSize);
	}

	/// <summary>
	/// The number of elements in the tag block at <paramref name="address"/>.
	/// </summary>
	public static int Count( this MemoryRegion data, uint address ) => data.I32( address );
}
