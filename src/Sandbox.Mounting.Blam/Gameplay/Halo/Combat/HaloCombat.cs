using System;
using Halo.Data;

namespace Halo.Combat;

// Host side combat: explosions and melee. Shots are projectiles, see ProjectileSimulation.
public static class HaloCombat {
	// Host only. Plays an effect for everyone and applies its damage parts as area damage.
	public static void Detonate( Scene scene, string effectPath, Vector3 position, Vector3 normal, Guid attacker, string weapon ) {
		BroadcastEffect( effectPath, position, normal, attacker );

		var effect = HaloDataStore.Game?.Effect( effectPath );
		if ( effect is null ) return;
		foreach ( var part in effect.Parts.Where( x => x.IsDamage ) )
			AreaDamage( scene, part.Tag, position, attacker, weapon );
	}

	[Rpc.Broadcast( NetFlags.Unreliable | NetFlags.HostOnly )]
	public static void BroadcastEffect( string effect, Vector3 position, Vector3 normal, Guid owner ) {
		HaloEffects.Play( effect, position, normal );
		ProjectileSimulation.Get( Game.ActiveScene )?.ClearNear( position, 200, owner );
	}

	// Host only. Damages every unit (Sandbox players and NPCs, Halo vehicles) and loose object in reach, Halo's way:
	// full damage within the falloff radius, down to none at the cutoff by distance to the object's centre,
	// reaching it straight or round the blast's core radius, and blocked by the level but not by bodies.
	public static void AreaDamage( Scene scene, string damageEffect, Vector3 epicenter, Guid attacker, string weapon ) {
		var tag = HaloDataStore.Game?.DamageEffect( damageEffect );
		if ( tag is null || tag.CutoffRadius <= 0 ) return;

		var cutoff = HaloUnits.ToInches( tag.CutoffRadius );
		var core = HaloUnits.ToInches( tag.DamageAoeCoreRadius );
		var seen = new HashSet<GameObject>();
		foreach ( var go in scene.FindInPhysics( new Sphere( epicenter, cutoff ) ) ) {
			var target = Target( go );
			if ( target is null || !seen.Add( target ) ) continue;
			if ( tag.HasFlag( "does not hurt owner" ) && target.Id == attacker ) continue;

			var unit = HaloUnitDirectory.Of( target );
			if ( unit is { IsAlive: false } ) continue;

			var center = unit?.Center ?? target.GetBounds().Center;
			var distance = HaloUnits.ToWorldUnits( center.Distance( epicenter ) );
			var falloff = tag.FalloffRadius;
			var scale = tag.CutoffRadius - falloff > 0 ? Math.Clamp( 1 - (distance - falloff) / (tag.CutoffRadius - falloff), 0, 1 ) : 1;
			if ( scale <= 0 ) continue;

			var trace = scene.Trace.WithoutTags( "playercontroller", "player", "trigger", "projectile", "debris", "ragdoll", "viewmodel" ).IgnoreGameObjectHierarchy( target );
			if ( !AreaReaches( trace, epicenter, center, core ) ) continue;

			var e = new DamageEvent {
				DamageEffect = damageEffect,
				Scale = tag.HasFlag( "don't scale damage by distance" ) ? 1 : scale,
				Position = epicenter,
				Direction = (center - epicenter).Normal,
				AreaOfEffect = true,
				AttackerId = attacker,
				WeaponName = weapon,
				Seed = Game.Random.Int( 0, int.MaxValue - 1 ),
			};
			if ( unit is not null ) unit.TakeDamage( e );
			else SandboxUnit.Apply( target, e );
		}
	}

	// The object a blast hurts for a physics object it found: the unit or damageable it's part of, or its rigidbody's object.
	static GameObject Target( GameObject go ) {
		if ( !go.IsValid() || go.Tags.Has( "trigger" ) || go.Tags.Has( "projectile" ) || go.Tags.Has( "viewmodel" ) ) return null;
		if ( HaloUnitDirectory.Of( go ) is { } unit ) return unit.GameObject;
		if ( go.GetComponentInParent<Component.IDamageable>() is Component damageable ) return damageable.GameObject;
		if ( go.GetComponentInParent<Rigidbody>() is { MotionEnabled: true } body ) return body.GameObject;
		return null;
	}

	// Whether area damage reaches an object's centre, straight or from four offset points with a core radius in inches.
	// The offsets sit square to the line to the object and are stopped short by the level.
	// trace must ignore the object and whatever carries it.
	public static bool AreaReaches( SceneTrace trace, Vector3 epicenter, Vector3 center, float core ) {
		if ( core <= HaloUnits.ToInches( Negligible ) )
			return !AreaBlocked( trace, epicenter, center, out _ );

		var toUnit = center - epicenter;
		var perpendicular = Perpendicular( toUnit ).Normal;
		var cross = Vector3.Cross( toUnit, perpendicular ).Normal;
		foreach ( var offset in new[] { perpendicular * core, -perpendicular * core, cross * core, -cross * core } ) {
			AreaBlocked( trace, epicenter, epicenter + offset, out var point );
			if ( !AreaBlocked( trace, point, center, out _ ) ) return true;
		}
		return false;
	}

	// A vector square to a, built from its two largest components.
	static Vector3 Perpendicular( Vector3 a ) {
		float i = MathF.Abs( a.x ), j = MathF.Abs( a.y ), k = MathF.Abs( a.z );
		if ( i <= j && i <= k ) return new Vector3( 0, a.z, -a.y );
		return j <= k ? new Vector3( -a.z, 0, a.x ) : new Vector3( a.y, -a.x, 0 );
	}

	// Halo's real epsilon, in world units.
	const float Negligible = 0.0001f;

	// Whether a line meets anything but a unit. point is where it stops.
	static bool AreaBlocked( SceneTrace trace, Vector3 from, Vector3 to, out Vector3 point ) {
		point = to;
		var nearest = float.MaxValue;
		foreach ( var tr in trace.Ray( from, to ).RunAll() ) {
			if ( !tr.Hit || tr.Distance >= nearest || HaloUnitDirectory.Of( tr.GameObject ) is not null ) continue;
			nearest = tr.Distance;
			point = tr.HitPosition;
		}
		return nearest < float.MaxValue;
	}

	// What a melee's rays found.
	public struct MeleeHit {
		public IHaloUnit Target;
		public HitboxHit Hit;
		// A loose object hit when no unit was.
		public GameObject Object;
		public Vector3 ObjectPoint;
		// -1 for a miss, which plays no sound.
		public int Material;
	}

	// A 5x5 fan of rays from the eyes, aim * 0.8 plus 0.1 steps across it, each stopping at the first thing it meets.
	// A unit beats any other object and one hit sooner along its ray a later one; otherwise the first object hit stays.
	public static MeleeHit MeleeTrace( Scene scene, IHaloUnit attacker, Vector3 origin, Vector3 aim ) {
		var result = new MeleeHit { Material = -1 };

		aim = aim.Normal;
		var perpendicular = Perpendicular( aim ).Normal;
		var cross = Vector3.Cross( aim, perpendicular );

		var haveObject = false;
		var bestIsUnit = false;
		var bestFraction = float.MaxValue;

		for ( var outer = -2; outer <= 2; outer++ ) {
			for ( var inner = -2; inner <= 2; inner++ ) {
				var ray = aim * 0.8f + (cross * inner + perpendicular * outer) * 0.1f;
				var dir = ray.Normal;
				var reach = HaloUnits.ToInches( ray.Length );

				if ( UnitTrace.Ray( scene, origin, dir, reach, attacker, out var hit, out var wall ) ) {
					var fraction = hit.Hit.Distance / reach;
					if ( !haveObject || !bestIsUnit || fraction < bestFraction ) {
						haveObject = bestIsUnit = true;
						bestFraction = fraction;
						result.Target = hit.Unit;
						result.Hit = hit.Hit;
						result.Object = null;
						result.Material = hit.Unit.HitMaterial( hit );
					}
				}
				else if ( wall.Hit && !wall.StartedSolid && !haveObject ) {
					result.Material = HaloSurfaces.MaterialOf( wall.Surface );
					if ( Target( wall.GameObject ) is { } loose ) {
						haveObject = true;
						result.Object = loose;
						result.ObjectPoint = wall.HitPosition;
					}
				}
			}
		}

		return result;
	}

	// Host only. Damage scales with forward speed, from the minimum standing to 1.5x after a long fall.
	// A unit takes the damage; a loose object is pushed along the aim.
	public static MeleeHit Melee( Scene scene, IHaloUnit attacker, Vector3 eye, Vector3 velocity, bool longFall, Vector3 aim, string damageEffect ) {
		aim = aim.Normal;
		var result = MeleeTrace( scene, attacker, eye, aim );
		if ( HaloDataStore.Game?.DamageEffect( damageEffect ) is null ) return result;

		var run = HaloDataStore.Game?.Globals?.Player?.RunForward ?? 2.25f;
		var forwardSpeed = HaloUnits.ToWorldUnits( Vector3.Dot( velocity, aim.WithZ( 0 ).Normal ) );
		var scale = Math.Clamp( forwardSpeed / run, 0, 1 );
		if ( longFall ) scale = 1.5f;

		var e = new DamageEvent {
			DamageEffect = damageEffect,
			Scale = scale,
			Direction = aim,
			AreaOfEffect = true,
			AttackerId = attacker?.GameObject?.Id ?? Guid.Empty,
			WeaponName = "melee",
			Seed = Game.Random.Int( 0, int.MaxValue - 1 ),
		};

		if ( result.Target is { } target ) {
			// From behind it's lethal, as Halo's unsuspecting melee is.
			var root = target.Root;
			var toTarget = (root.Position - eye).WithZ( 0 ).Normal;
			e.TargetUnsuspecting = Vector3.Dot( toTarget, root.Rotation.Forward ) > 0;
			e.Position = result.Hit.Position;
			target.TakeDamage( e );
		}
		else if ( result.Object.IsValid() ) {
			e.Position = result.ObjectPoint;
			SandboxUnit.Apply( result.Object, e );
		}

		return result;
	}

	// Plays the material's melee hit sound and the damage effect's own sound at the attacker, following it. A miss plays neither.
	public static void MeleeSound( GameObject attacker, int material, string damageEffect, bool own ) {
		if ( material < 0 || !attacker.IsValid() ) return;
		var game = HaloDataStore.Game;
		PlayOn( attacker, game?.Globals?.Material( material )?.MeleeHitSound, own );
		PlayOn( attacker, game?.DamageEffect( damageEffect )?.Sound, own );
	}

	static void PlayOn( GameObject attacker, string sound, bool own ) {
		if ( string.IsNullOrEmpty( sound ) ) return;
		var handle = HaloSounds.Play( sound, attacker.WorldPosition, own: own );
		if ( handle is null ) return;
		handle.Parent = attacker;
		handle.FollowParent = true;
	}
}
