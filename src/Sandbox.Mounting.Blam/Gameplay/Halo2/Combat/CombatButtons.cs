namespace Halo2.Combat;

// Halo 2's combat buttons as CombatSim reads them, from halo2sbox's SpartanCommand.
public static class CombatButtons {
	public const ushort Fire = 1, Zoom = 2, Melee = 4, Grenade = 8, Reload = 16, Switch = 32, SwitchGrenade = 64,
		Use = 128, FireLeft = 256;

	public const ushort All = 511;
}
