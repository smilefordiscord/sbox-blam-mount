using System;
using Halo.Data;

namespace Halo.Combat;

// Something Halo's shots, damage and aim treat as a unit: a Sandbox player or NPC (SandboxUnit), or a Halo vehicle.
public interface IHaloUnit : IValid {
	GameObject GameObject { get; }
	bool IsAlive { get; }

	// Origin and facing as hits are tested: feet and yaw.
	Transform Root { get; }

	// The centre of its body.
	Vector3 Center { get; }

	// Its eyes or head.
	Vector3 Head { get; }

	SkinnedModelRenderer Renderer { get; }

	// The body left from its last death, or null.
	SkinnedModelRenderer Corpse { get; }

	// The material type struck, which picks the projectile's response.
	int HitMaterial( in UnitHit hit );

	// Host only.
	DamageResult TakeDamage( DamageEvent e );
}

// Finds units by object id.
public static class HaloUnitDirectory {
	public static IHaloUnit Find( Guid id ) {
		if ( id == Guid.Empty ) return null;
		return Of( Game.ActiveScene?.Directory.FindByGuid( id ) );
	}

	// The unit an object is part of: a Halo vehicle, or a Sandbox player or NPC.
	public static IHaloUnit Of( GameObject go ) {
		if ( !go.IsValid() ) return null;
		if ( go.GetComponentInParent<IHaloUnit>() is { } unit ) return unit;
		return SandboxUnit.For( go );
	}
}
