using System;
using Halo2.Data;

namespace Halo2.Combat;

// halo2sbox's own corpses, which Sandbox doesn't have: its ragdolls are props, hit as the level.
public sealed class SpartanCorpse : IValid {
	public bool IsValid => false;
	public Guid Owner => Guid.Empty;
	public int Death => 0;
	public SkinnedModelRenderer Renderer => null;
	public static SpartanCorpse Find( Scene scene, Guid owner, int death ) => null;
	public static bool Trace( Scene scene, Vector3 start, Vector3 direction, float distance, SpartanCorpse ignore, out SpartanCorpse corpse, out HitboxHit hit ) {
		corpse = null;
		hit = default;
		return false;
	}
	public static void HostAreaDamage( Scene scene, DamageEffectTag tag, Vector3 epicenter ) { }
	public void HostDamage( string damageEffect, Vector3 direction ) { }
}

// halo2sbox's loose weapons and equipment; dropped Sandbox weapons are props.
public sealed class WorldItem : Component {
	public const string ColliderTag = "halo2_world_item";
	public void HostImpactDamage( string damageEffect, float scale, Vector3 direction ) { }
	public static void HostAreaDamage( Scene scene, DamageEffectTag tag, Vector3 epicenter ) { }
}

// halo2sbox's impact debugging, off here.
public sealed class ImpactTrack {
	public void Step( Scene scene, Projectile p, Vector3 start, Vector3 direction, float length, UnitHit? hit ) { }
}

public sealed class ImpactDebug {
	public static ImpactTrack Track( Projectile p ) => null;
	public static ImpactDebug Get( Scene scene ) => null;
	public void Finished( Projectile p ) { }
}
