using System;
using Halo2.Data;

namespace Halo2.Combat;

// Halo 2's zoom magnifications, from halo2sbox's SpartanWeapons.
public static class Zoom {
	// The camera's magnification at a zoom level: the weapon's own or the binoculars'.
	public static float Of( WeaponTag tag, int level ) => CombatSim.Binoculars( tag, level ) ? Binoculars( level ) : Magnification( tag, level );

	// A weapon's zoom: min x (max/min)^(level/(levels-1)).
	public static float Magnification( WeaponTag tag, int level ) {
		if ( tag is null || level < 0 || tag.MagnificationLevels <= 0 ) return 1;
		var range = tag.MagnificationRange is { Length: > 1 } r ? r : new[] { 2f, 2f };
		if ( tag.MagnificationLevels == 1 || range[0] <= 0 ) return MathF.Max( 1, range[0] );
		return range[0] * MathF.Pow( range[1] / range[0], level / (float)(tag.MagnificationLevels - 1) );
	}

	// The binoculars run linearly from min to max; a single level is max.
	static float Binoculars( int level ) {
		var player = GameCombatData.Instance.Player;
		if ( level < 0 || player?.BinocularsZoomRange is not { Length: > 1 } range ) return 1;
		var count = player.BinocularsZoomCount;
		var t = count == 1 ? 1 : level / (float)(count - 1);
		return range[0] + (range[1] - range[0]) * t;
	}
}
