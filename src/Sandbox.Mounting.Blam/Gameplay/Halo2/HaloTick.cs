using System;

namespace Halo2.Combat;

// Halo 2's 30 Hz tick is the addon's frame clock tick (Halo.HaloTick), shared with Halo CE: never Sandbox's fixed step.
public static class HaloTick {
	public static long Count => Halo.HaloTick.Count;

	// Halo ticks this frame advanced, for systems that step once per tick in their update.
	public static int Advanced => Halo.HaloTick.Advanced;

	// How far the drawn frame is past the last tick, 0 to 1.
	public static float Fraction => Halo.HaloTick.Fraction;
}

// Runs actions in the tick they fall due, on the shared clock.
public static class HaloTickSystem {
	public static void After( Scene scene, float seconds, Action action ) => Halo.HaloTickSystem.After( scene, seconds, action );
}
