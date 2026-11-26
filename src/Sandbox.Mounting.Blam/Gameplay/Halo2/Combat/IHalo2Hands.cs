using System;
using Halo2.Data;

namespace Halo2.Combat;

// What Halo 2's first person view and weapon functions read of the unit holding the weapons, in place of halo2sbox's Spartan and SpartanWeapons.
// Halo2Weapon implements it for a Sandbox player; only the right hand ever holds a weapon here.
public interface IHalo2Hands {
	Scene Scene { get; }
	GameObject GameObject { get; }
	bool IsLocalPlayer { get; }
	bool IsAlive { get; }
	bool IsAirborne { get; }
	Vector3 EyePosition { get; }
	Angles EyeAngles { get; }
	// Movement input, x forward and y left, -1 to 1.
	Vector2 Throttle { get; }

	// The host runs the combat step for everyone and an owner its own, so only they know every number.
	bool Simulated { get; }
	int ZoomLevel { get; }
	bool WantsFire { get; }
	bool WantsGrenade { get; }
	WeaponTag CurrentTag { get; }
	bool DualWielding { get; }

	string HandPath( Hand hand );
	WeaponTag HandTag( Hand hand );
	WeaponSlotState HandState( Hand hand );
	float HandChargeLevel( Hand hand );
	float HandHeatLevel( Hand hand );
	float HandAge( Hand hand );
	bool HandOverheated( Hand hand );
	int HandOverheatTicksLeft( Hand hand );
	bool HandPuttingAway( Hand hand );
	bool HandReloading( Hand hand );
	float HandSinceReadied( Hand hand );
	float HandSinceFired( Hand hand );
	int HandLastFiredBarrel( Hand hand );
	float HandPrimaryFiring( Hand hand );
	object HandSoundSource( Hand hand );
}
