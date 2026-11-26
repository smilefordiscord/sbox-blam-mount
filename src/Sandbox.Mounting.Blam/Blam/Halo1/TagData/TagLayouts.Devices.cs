namespace Blam.Halo1.TagData;

// Devices: machines (doors, platforms, gears), controls (switches) and light fixtures. Each starts
// with the object definition, then the device definition at 0x17C, then its own fields at 0x290.
// They are built on first use because the object definition lives in another file.
public static partial class TagLayouts
{
	static readonly string[] DeviceFunctionIn = ["none", "power", "change in power", "position", "change in position", "locked", "delay"];

	static TagStruct _machine, _control, _lightFixture;

	/// <summary>
	/// The device definition (0x290 bytes with the object's). The per tick rates are the cache's
	/// runtime values: one over the matching time in ticks (0 when the time is 0), what the game
	/// moves a device's power and position by; the delay is in ticks.
	/// </summary>
	static TagStruct DeviceFields( TagStruct device ) => device
		.Flags32( 0x17C, "device flags", ["position loops", "position not interpolated"] )
		.Float( 0x180, "power transition time" )
		.Float( 0x184, "power acceleration time" )
		.Float( 0x188, "position transition time" )
		.Float( 0x18C, "position acceleration time" )
		.Float( 0x190, "depowered position transition time" )
		.Float( 0x194, "depowered position acceleration time" )
		.Enum( 0x198, "device a in", DeviceFunctionIn )
		.Enum( 0x19A, "device b in", DeviceFunctionIn )
		.Enum( 0x19C, "device c in", DeviceFunctionIn )
		.Enum( 0x19E, "device d in", DeviceFunctionIn )
		.Reference( 0x1A0, "open" )
		.Reference( 0x1B0, "close" )
		.Reference( 0x1C0, "opened" )
		.Reference( 0x1D0, "closed" )
		.Reference( 0x1E0, "depowered" )
		.Reference( 0x1F0, "repowered" )
		.Float( 0x200, "delay time" )
		.Reference( 0x20C, "delay effect" )
		.Float( 0x21C, "automatic activation radius" )
		.Float( 0x274, "power acceleration per tick" )
		.Float( 0x278, "power velocity per tick" )
		.Float( 0x27C, "depowered position acceleration per tick" )
		.Float( 0x280, "depowered position velocity per tick" )
		.Float( 0x284, "position acceleration per tick" )
		.Float( 0x288, "position velocity per tick" )
		.Float( 0x28C, "delay ticks" );

	/// <summary>
	/// <c>mach</c>, 0x324 bytes. A door opens by itself for units near it and closes once it has
	/// been open for its door open time; a gear runs through its positions while powered.
	/// </summary>
	public static TagStruct Machine => _machine ??= DeviceFields( new TagStruct( "device machine", 0x324, ObjectDefinition ) )
		.Enum( 0x290, "machine type", ["door", "platform", "gear"] )
		.Flags16( 0x292, "machine flags", ["pathfinding obstacle", "but not when open", "elevator"] )
		.Float( 0x294, "door open time" )
		.Enum( 0x2E8, "collision response", ["pause until crushed", "reverse directions"] )
		.Index( 0x2EA, "elevator node" )
		.Int32( 0x320, "door open time ticks" );

	/// <summary>
	/// <c>ctrl</c>, 0x318 bytes: a switch that sets its position group when a player uses it (or
	/// when it's destroyed), with the effect for on, off, or a change refused.
	/// </summary>
	public static TagStruct Control => _control ??= DeviceFields( new TagStruct( "device control", 0x318, ObjectDefinition ) )
		.Enum( 0x290, "type", ["toggle switch", "on button", "off button", "call button"] )
		.Enum( 0x292, "triggers when", ["touched by player", "destroyed"] )
		.Float( 0x294, "call value" )
		.Reference( 0x2E8, "on" )
		.Reference( 0x2F8, "off" )
		.Reference( 0x308, "deny" );

	/// <summary>
	/// <c>lifi</c>, 0x2D0 bytes: a device with nothing of its own; its placements give the light.
	/// </summary>
	public static TagStruct LightFixture => _lightFixture ??= DeviceFields( new TagStruct( "device light fixture", 0x2D0, ObjectDefinition ) );
}
