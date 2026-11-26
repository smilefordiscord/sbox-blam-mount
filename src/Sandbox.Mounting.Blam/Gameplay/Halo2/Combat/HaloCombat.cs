using System;
using Halo2.Data;

namespace Halo2.Combat;

// Host side combat: explosions and melee. Shots are projectiles, see ProjectileSimulation.
public static class HaloCombat {
	// Host only. Plays a detonation's effect and sound for everyone and deals its area damage.
	public static void Detonate( Scene scene, string effectPath, string sound, string damageEffect, Vector3 position, Vector3 normal, Guid attacker, string weapon, RewindContext rewind, ProjectileKey key ) {
		if ( !string.IsNullOrEmpty( effectPath ) || !string.IsNullOrEmpty( sound ) )
			Halo2Net.Effect( effectPath, sound, position, normal, key.Shooter, key.Tick, key.Index );
		if ( !string.IsNullOrEmpty( damageEffect ) )
			AreaDamage( scene, damageEffect, position, attacker, weapon, rewind );
	}

	// Host only. Damages Spartans, items and crates in reach, scaled by their distance from the nearest point.
	// source is the crate exploding, which its own explosion leaves out.
	// Returns the global material of the last unit damaged, as Halo overwrites it per object, or null.
	public static string AreaDamage( Scene scene, string damageEffect, Vector3 position, Guid attacker, string weapon, RewindContext rewind, GameObject source = null ) {
		var tag = HaloDataStore.Game?.DamageEffect( damageEffect );
		if ( tag is null ) return null;

		var cutoff = tag.CutoffRadius;
		if ( cutoff <= 0 ) return null;

		string material = null;

		foreach ( var target in Halo2Unit.All( scene ).ToArray() ) {
			var center = target.Center;
			var head = target.Head;

			// Distance to the nearest part of the body's spine.
			var closest = ClosestOnSegment( target.WorldPosition + Vector3.Up * 8, head, position );
			var distanceWu = HaloUnits.ToWorldUnits( closest.Distance( position ) );
			if ( distanceWu > cutoff ) continue;

			// Needs line of sight to the center or head.
			if ( !Visible( scene, position, center ) && !Visible( scene, position, head ) )
				continue;

			var scale = HaloDamage.AreaScale( tag, distanceWu );
			var result = target.TakeDamage( new DamageEvent {
				DamageEffect = damageEffect,
				Scale = scale,
				Position = closest,
				Direction = (center - position).Normal,
				AreaOfEffect = true,
				AttackerId = attacker,
				WeaponName = weapon,
				Seed = Game.Random.Int( 0, int.MaxValue - 1 ),
			} );
			material = result.GlobalMaterial ?? material;
		}

		// Props and anything else that takes damage.
		HaloPhysics.HostAreaDamage( scene, tag, position, new DamageEvent {
			DamageEffect = damageEffect,
			AttackerId = attacker,
			WeaponName = weapon,
			Seed = Game.Random.Int( 0, int.MaxValue - 1 ),
		}, source );
		return material;
	}

	static bool Visible( Scene scene, Vector3 from, Vector3 to ) {
		var tr = scene.Trace.Ray( from, to ).WithoutTags( "player", "trigger", "projectile", "debris", "ragdoll" ).Run();
		return !tr.Hit || tr.Fraction > 0.98f;
	}

	static Vector3 ClosestOnSegment( Vector3 a, Vector3 b, Vector3 p ) {
		var ab = b - a;
		var len = ab.LengthSquared;
		if ( len < 1e-6f ) return a;
		return a + ab * Math.Clamp( Vector3.Dot( p - a, ab ) / len, 0, 1 );
	}

	// What a player melee's rays found.
	public struct MeleeHit {
		public Halo2Unit Target;
		public HitboxHit Hit;
		// -1 for a miss, which plays no sound.
		public int Material;
		// The nearest thing other than a living unit the rays hit: a prop, or the level.
		public GameObject Object;
	}

	// Halo 2's damage pyramid: a 5x5 grid of rays from the eyes, each the aim times the weapon's depth plus side steps
	// of half its two angles (in radians), unscaled by the depth.
	// A biped beats anything else and a nearer biped a farther one; the material is the last hit's.
	public static MeleeHit MeleeTrace( Halo2Unit attacker, WeaponTag weapon, Vector3 origin, Vector3 aim, RewindContext rewind ) {
		var scene = attacker.Scene;
		var result = new MeleeHit { Material = -1 };
		if ( weapon?.DamagePyramidAngles is not { Length: > 1 } angles || !(weapon.DamagePyramidDepth > 0) ) return result;

		var depth = weapon.DamagePyramidDepth;
		var forward = aim.Normal;
		var up = Perpendicular( forward ).Normal * (angles[0].DegreeToRadian() * 0.5f);
		var right = Vector3.Cross( forward, Perpendicular( forward ).Normal ) * (angles[1].DegreeToRadian() * 0.5f);

		var bestDistance = float.MaxValue;
		var objectDistance = float.MaxValue;
		for ( var j = -2; j <= 2; j++ ) {
			for ( var i = -2; i <= 2; i++ ) {
				var ray = HaloUnits.ToInches( 1 ) * (forward * depth + up * j + right * i);
				var reach = ray.Length;
				var dir = ray / reach;
				var wall = scene.Trace.Ray( origin, origin + ray )
					.WithoutTags( "player", "trigger", "projectile", "debris", "ragdoll" )
					.Run();
				var limit = wall.Hit ? wall.Distance : reach;

				if ( UnitTrace.Ray( scene, origin, dir, limit, rewind, attacker, out var hit ) ) {
					result.Material = hit.Unit.HitMaterial;
					if ( hit.Hit.Distance < bestDistance ) {
						bestDistance = hit.Hit.Distance;
						result.Target = hit.Unit;
						result.Hit = hit.Hit;
					}
				}
				else if ( wall.Hit ) {
					result.Material = HaloSurfaces.MaterialOrDefault( wall.Surface );
					if ( wall.Distance < objectDistance ) {
						objectDistance = wall.Distance;
						result.Object = wall.GameObject;
					}
				}
			}
		}

		return result;
	}

	// The axis Halo 2 builds a melee's grid from, perpendicular to the aim.
	static Vector3 Perpendicular( Vector3 f ) {
		var x = MathF.Abs( f.x );
		var y = MathF.Abs( f.y );
		var z = MathF.Abs( f.z );
		if ( y >= x && z >= x ) return new Vector3( 0, f.z, -f.y );
		if ( z >= y ) return new Vector3( -f.z, 0, f.x );
		return new Vector3( f.y, -f.x, 0 );
	}

	// Host only. Traces from the attacker's position at that tick and damages the Spartan hit.
	// scale is the melee's place between its damage bounds, from the attacker's speed when it started.
	public static MeleeHit Melee( Halo2Unit attacker, WeaponTag weapon, ShotFrame frame, Vector3 aim, RewindContext rewind, string damageEffect, float scale ) {
		aim = aim.Normal;
		var result = MeleeTrace( attacker, weapon, frame.Eye, aim, rewind );
		if ( result.Target is null ) {
			// A melee that hit no living unit damages and pushes what it hit, from the attacker's middle.
			var center = frame.Feet + Vector3.Up * ((frame.Eye.z - frame.Feet.z) * 0.5f);
			HaloPhysics.HostMeleeHit( attacker.Scene, result.Object, center, aim, new DamageEvent {
				Position = center,
				DamageEffect = damageEffect,
				Scale = scale,
				Direction = aim,
				AttackerId = attacker.GameObject.Id,
				WeaponName = "melee",
				Seed = Game.Random.Int( 0, int.MaxValue - 1 ),
			}, result.Material );
			return result;
		}
		if ( HaloDataStore.Game?.DamageEffect( damageEffect ) is null ) return result;

		result.Target.TakeDamage( new DamageEvent {
			DamageEffect = damageEffect,
			Scale = scale,
			Position = result.Hit.Position,
			Direction = aim,
			MaterialName = result.Hit.Material,
			Node = result.Hit.Node,
			AttackerId = attacker.GameObject.Id,
			WeaponName = "melee",
			Seed = Game.Random.Int( 0, int.MaxValue - 1 ),
		} );

		return result;
	}

	// Halo 2's strike target, a fixed cone no tag field changes: the enemy nearest the aim point of its pill at eye height.
	// Its surface must be within 2 wu of the attacker's for a melee only weapon, else 1.2, inside a yaw that narrows from 50
	// degrees at contact to 15 at 1.2 wu (the sword's reach ends near 1.71), and 30 degrees of pitch.
	// Within 0.6 wu the one nearest the aim wins, else the nearest. Its pill must be in sight from the eyes.
	public static Halo2Unit StrikeTarget( Halo2Unit attacker, WeaponTag weapon, Vector3 eye, Rotation aim, RewindContext rewind ) {
		var biped = HaloDataStore.SpartanBiped;
		var radius = biped?.Radius ?? 0.175f;
		var range = weapon.MeleeOnly ? MeleeOnlyStrikeRange : StrikeRange;

		Halo2Unit best = null;
		float bestDistance = 0, bestYaw = 0;
		var bestPose = Vector3.Zero;
		foreach ( var unit in Halo2Unit.All( attacker.Scene ) ) {
			if ( unit == attacker ) continue;
			var pose = unit.WorldPosition;
			var (bottom, top) = StrikeSpine( pose, biped, radius );
			var to = pose.WithZ( Math.Clamp( eye.z, bottom, top ) ) - eye;
			var distance = MathF.Max( 0, HaloUnits.ToWorldUnits( to.Length ) - 2 * radius );
			if ( distance >= range ) continue;

			var local = aim.Inverse * to;
			var yaw = MathF.Abs( MathF.Atan2( local.y, local.x ).RadianToDegree() );
			var pitch = MathF.Atan2( local.z, MathF.Sqrt( local.x * local.x + local.y * local.y ) ).RadianToDegree();
			var near = distance / StrikeRange;
			if ( yaw > (1 - near) * StrikeContactYaw + near * StrikeReachYaw || MathF.Abs( pitch ) > StrikePitch ) continue;

			var better = best is null || (distance < StrikeCloseRange && bestDistance < StrikeCloseRange ? yaw < bestYaw : distance < bestDistance);
			if ( !better ) continue;
			best = unit;
			bestDistance = distance;
			bestYaw = yaw;
			bestPose = pose;
		}
		if ( best is null ) return null;

		// Rays to the bottom, middle and top of its spine; any clear one sees it.
		var (low, high) = StrikeSpine( bestPose, biped, radius );
		for ( var i = 0; i < 3; i++ ) {
			var point = bestPose.WithZ( low + (high - low) * i * 0.5f );
			if ( !attacker.Scene.Trace.Ray( eye, point ).WithoutTags( "player", "trigger", "projectile", "debris", "ragdoll" ).Run().Hit ) return best;
		}
		return null;
	}

	// The strike cone's reaches, wu: a melee only weapon's, everyone else's, and within which the aim decides.
	const float MeleeOnlyStrikeRange = 2;
	const float StrikeRange = 1.2f;
	const float StrikeCloseRange = 0.6f;

	// Degrees.
	const float StrikeContactYaw = 50;
	const float StrikeReachYaw = 15;
	const float StrikePitch = 30;

	// The heights of a pill's sphere centres, in inches.
	static (float Bottom, float Top) StrikeSpine( Vector3 feet, BipedTag biped, float radius ) {
		var height = biped?.HeightStanding ?? 0.725f;
		return (feet.z + HaloUnits.ToInches( radius ), feet.z + HaloUnits.ToInches( MathF.Max( radius, height - radius ) ));
	}

	// Plays the material's melee hit sound and the damage effect's own sound on the attacker, following it.
	// A miss plays neither.
	public static void MeleeSound( Halo2Unit attacker, int material, string damageEffect ) {
		if ( material < 0 || !attacker.IsValid() ) return;
		var game = HaloDataStore.Game;
		PlayOn( attacker, game?.Globals?.Material( material )?.SoundSweetenerMelee );
		PlayOn( attacker, game?.DamageEffect( damageEffect )?.Sound );
	}

	static void PlayOn( Halo2Unit unit, string sound ) {
		if ( string.IsNullOrEmpty( sound ) ) return;
		var handle = HaloSounds.Play( sound, unit.WorldPosition, source: unit, own: unit.IsLocalPlayer );
		// An unspatialized sound stays at its listener.
		if ( handle is null || handle.ListenLocal ) return;
		handle.Parent = unit.GameObject;
		handle.FollowParent = true;
	}
}
