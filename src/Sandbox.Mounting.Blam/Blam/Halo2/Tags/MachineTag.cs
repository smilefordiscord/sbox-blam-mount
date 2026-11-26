namespace Blam.Halo2.Tags;

public enum MachineType : short
{
	Door = 0,
	Platform = 1,
	/// <summary>Runs through its positions continuously (Zanzibar's wheel and flywheels).</summary>
	Gear = 2,
}

/// <summary>
/// The parts of a Halo 2 machine (<c>mach</c>, a device) that drive its position animation.
/// </summary>
/// <remarks>
/// Device fields (after the object's): power transition 0xC0 and acceleration 0xC4, position transition 0xC8 and
/// acceleration 0xCC, depowered position transition 0xD0 / acceleration 0xD4; machine type 0x11C, flags 0x11E. The
/// transition fields hold rates in the maps, positions per second: Zanzibar's wheel has 1/30 and its flywheels 1/5,
/// and MCC turns them once every 30.0 s and 5.0 s (measured live from their node matrices).
/// </remarks>
public sealed record MachineTag( MachineType Type, int Flags, float PositionRate, float PowerRate )
{
	public static readonly TagClass Class = new( "mach" );

	/// <summary>
	/// A machine tag's fields, or null for another kind of tag.
	/// </summary>
	public static MachineTag Read( TagEntry tag )
	{
		if ( tag is null || tag.Class != Class || tag.Address == 0 )
			return null;

		var data = tag.Cache.TagData;
		var a = tag.Address;
		return new MachineTag( (MachineType)data.I16( a + 0x11C ), data.U16( a + 0x11E ), data.F32( a + 0xC8 ), data.F32( a + 0xC0 ) );
	}
}
