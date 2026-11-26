using System;
using Halo.Combat;
using Halo.Data;

namespace HaloWeapons;

// A player's Halo grenades, thrown from any Halo weapon. Added the first time they hold one, with Halo's multiplayer two frags.
public sealed class HaloGrenades : Component {
	public const int Maximum = 4;

	[Sync] public int Frags { get; set; } = 2;
	[Sync] public int Plasmas { get; set; }

	public static HaloGrenades For( PlayerController player ) {
		if ( player is null ) return null;
		var grenades = player.GetComponent<HaloGrenades>();
		if ( grenades is not null || player.IsProxy ) return grenades;
		return player.AddComponent<HaloGrenades>();
	}

	public void Set( int frags, int plasmas ) {
		Frags = Math.Clamp( frags, 0, Maximum );
		Plasmas = Math.Clamp( plasmas, 0, Maximum );
	}
}
