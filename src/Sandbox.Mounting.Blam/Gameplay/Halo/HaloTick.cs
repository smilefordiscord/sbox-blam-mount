using System;

namespace Halo;

// Halo's 30 Hz tick, on the frame clock: each frame advances it by the ticks whose time has come, so a tick happens exactly when
// frame time reaches it and the drawn fraction between ticks never stalls. Sandbox's 50 Hz fixed steps can't carry it:
// a due tick would wait up to 20 ms for the next step and the drawn pose would hold.
public static class HaloTick {
	// Halo ticks of the active scene so far.
	public static long Count { get; internal set; }

	// Halo ticks this frame advanced, for systems and components that step once per tick in their update.
	public static int Advanced { get; internal set; }

	// Seconds past the last tick this frame.
	internal static double Remainder;

	// How far this frame is past the last tick, 0 to 1, for drawing between the last two ticks.
	public static float Fraction => (float)Math.Clamp( Remainder / HaloUnits.TickInterval, 0, 1 );
}

// Counts Halo ticks first in each frame, and runs actions deferred with After at the end of the frame they fall due.
public sealed class HaloTickSystem : GameObjectSystem<HaloTickSystem> {
	readonly PriorityQueue<Action, long> _due = new();

	public HaloTickSystem( Scene scene ) : base( scene ) {
		Listen( Stage.StartUpdate, -10000, Count, "Halo.Tick" );
		Listen( Stage.FinishUpdate, 10000, RunDue, "Halo.Tick.Due" );
	}

	double _accumulated;

	void Count() {
		if ( Scene != Game.ActiveScene ) return;
		_accumulated += Time.Delta;
		HaloTick.Advanced = 0;
		while ( _accumulated >= HaloUnits.TickInterval ) {
			_accumulated -= HaloUnits.TickInterval;
			HaloTick.Count++;
			HaloTick.Advanced++;
		}
		HaloTick.Remainder = _accumulated;
	}

	// Runs action in the first tick by which the seconds have passed, counting the next tick as the first.
	// This matches how Halo releases an effect's delayed events.
	public static void After( Scene scene, float seconds, Action action ) {
		if ( scene is null || Get( scene ) is not { } system ) return;
		var ticks = Math.Max( 1, (long)Math.Ceiling( seconds * HaloUnits.TicksPerSecond - 0.0001 ) );
		system._due.Enqueue( action, HaloTick.Count + ticks );
	}

	void RunDue() {
		while ( _due.TryPeek( out _, out var tick ) && tick <= HaloTick.Count )
			_due.Dequeue()();
	}

	public override void Dispose() {
		_due.Clear();
		base.Dispose();
	}
}
