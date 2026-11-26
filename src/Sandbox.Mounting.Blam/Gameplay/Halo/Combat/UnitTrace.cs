using System;

namespace Halo.Combat;

// Where a ray met a unit's body.
public struct HitboxHit {
	public float Distance;
	public Vector3 Position;
	public Vector3 Normal;

	// "head" for a head hit box, else null.
	public string Material;
}

// What a ray met of a unit.
public struct UnitHit {
	public IHaloUnit Unit;
	public HitboxHit Hit;

	// The bone of the hit box struck, null for a body without hit boxes.
	public string Node;

	// The unit's root as the ray was tested.
	public Transform TestedRoot;

	// The object the ray hit, for Sandbox's damage and push.
	public GameObject Object;
}

// Rays against units: Sandbox players' and NPCs' hit boxes, and Halo vehicles' hulls.
public static class UnitTrace {
	// Shots skip player controllers' movement colliders and hit their hit boxes instead, as Sandbox's guns do.
	public static SceneTrace Trace( Scene scene, Vector3 start, Vector3 end ) => scene.Trace.Ray( start, end )
		.UseHitboxes()
		.WithoutTags( "playercontroller", "trigger", "projectile", "debris", "ragdoll", "viewmodel" );

	// The nearest thing along a ray, ignoring one unit: true with result set for a unit's body, else world holds what was hit.
	public static bool Ray( Scene scene, Vector3 start, Vector3 direction, float distance, IHaloUnit ignore, out UnitHit result, out SceneTraceResult world ) {
		result = default;
		var trace = Trace( scene, start, start + direction * distance );
		if ( ignore?.GameObject is { } ignored && ignored.IsValid() ) trace = trace.IgnoreGameObjectHierarchy( ignored );
		world = trace.Run();
		if ( !world.Hit || world.StartedSolid ) return false;

		var unit = HaloUnitDirectory.Of( world.GameObject );
		if ( unit is null || !unit.IsValid() ) return false;

		var head = world.Hitbox?.Tags?.Has( "head" ) == true;
		result = new UnitHit {
			Unit = unit,
			Hit = new HitboxHit { Distance = world.Distance, Position = world.HitPosition, Normal = world.Normal, Material = head ? "head" : null },
			Node = world.Hitbox?.Bone?.Name,
			TestedRoot = unit.Root,
			Object = world.GameObject,
		};
		return true;
	}

	// Where a ray meets one unit's body.
	public static bool RayAgainst( Scene scene, Vector3 start, Vector3 direction, float distance, IHaloUnit unit, out UnitHit result ) {
		result = default;
		if ( !Ray( scene, start, direction, distance, null, out var hit, out _ ) || hit.Unit != unit ) return false;
		result = hit;
		return true;
	}
}
