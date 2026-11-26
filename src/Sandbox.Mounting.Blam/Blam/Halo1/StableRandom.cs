using System;

namespace Blam.Halo1;

// Inside the namespace so this wins over s&box's global Vector3 when this compiles into the mount.
using Vector3 = System.Numerics.Vector3;

/// <summary>
/// Stands in for the game's random choices (a decal's spin and sprite, which item a spawn holds)
/// with numbers that are the same every time the map loads.
/// </summary>
public static class StableRandom
{
	/// <summary>
	/// A pseudo-random number in [0, 1) from a position, and a second number to get several
	/// different values at one spot.
	/// </summary>
	public static float At( Vector3 p, int salt = 0 )
	{
		var h = BitConverter.SingleToUInt32Bits( p.X ) * 73856093u ^ BitConverter.SingleToUInt32Bits( p.Y ) * 19349663u ^ BitConverter.SingleToUInt32Bits( p.Z ) * 83492791u ^ (uint)salt * 0x9E3779B1u;
		h ^= h >> 13;
		h *= 0x5bd1e995u;
		h ^= h >> 15;
		return (h & 0xFFFFFF) / (float)0x1000000;
	}
}
