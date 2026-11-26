namespace Blam.Halo3.Tags;

public enum MachineType : short
{
	Door = 0,
	Platform = 1,
	Gear = 2,
}

// The device and machine fields of a Halo 3 mach tag that animate it: device flags 0xF8 (1 position loops), position transition 0x104 (a rate in positions per second, as Halo 2's: 040_voi's fan_fast 1, fan_slow 0.1, warehouse fan_reallyslow 0.0588), machine type 0x190, machine flags 0x192.
public sealed record MachineTag( MachineType Type, uint DeviceFlags, float PositionRate, ushort Flags )
{
	public static readonly TagClass Class = new( "mach" );

	public bool PositionLoops => (DeviceFlags & 1) != 0;

	public static MachineTag Read( TagEntry tag )
	{
		if ( tag is not { Address: not 0 } || tag.Class != Class )
			return null;

		var d = tag.Data;
		return new MachineTag( (MachineType)d.I16( 0x190 ), d.U32( 0xF8 ), d.F32( 0x104 ), d.U16( 0x192 ) );
	}
}
