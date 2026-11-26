namespace Blam.Halo1.Tags;

public enum MachineType : ushort
{
	Door = 0,
	Platform = 1,
	/// <summary>Runs through its positions over and over while powered.</summary>
	Gear = 2,
}

/// <summary>
/// Reader for the parts of <c>mach</c> (machine) tags the mount uses.
/// </summary>
/// <remarks>
/// Machines are devices, which are objects: the machine fields start at 0x290 with the machine
/// type, after the object (0x0) and device (0x17C, flags first) definitions. The device's position transition
/// time (seconds to go from position 0 to 1) is at 0x188; the cache also stores its inverse in
/// ticks at 0x288.
/// </remarks>
public static class MachineTag
{
	public static readonly TagClass Class = new( "mach" );

	/// <summary>
	/// The machine's type, or null if the tag isn't a machine.
	/// </summary>
	public static MachineType? GetType( CacheFile cache, TagEntry tag ) =>
		tag?.Class == Class ? (MachineType)cache.TagData.U16( tag.Address + 0x290 ) : null;

	/// <summary>
	/// Whether the machine is an elevator (bit 2 of the machine flags at 0x292).
	/// </summary>
	public static bool IsElevator( CacheFile cache, TagEntry tag ) =>
		tag?.Class == Class && (cache.TagData.U16( tag.Address + 0x292 ) & 0x4) != 0;

	/// <summary>
	/// Whether the device's position loops (bit 0 of the device flags at 0x17C): its position
	/// animation is read at position x frame count, the last frame blending back into the first,
	/// rather than position x (frame count - 1).
	/// </summary>
	public static bool PositionLoops( CacheFile cache, TagEntry device ) =>
		(cache.TagData.U32( device.Address + 0x17C ) & 0x1) != 0;

	/// <summary>
	/// Seconds a device (machine or control) takes to move from position 0 to 1.
	/// </summary>
	public static float GetPositionTransitionTime( CacheFile cache, TagEntry device ) =>
		cache.TagData.F32( device.Address + 0x188 );
}
