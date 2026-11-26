using System;
using Halo.Data;

namespace Halo.Combat;

// The projectiles of a weapon trigger's shot, the same from the same seed on every machine.
public static class ShotLaunches {
	// Every projectile one trigger pull makes, deterministic from the seed so every machine fires the same pellets.
	// A horizontal fan offsets them 0, ±1, ±2 for odd counts or ±0.5, ±1.5 for even, then each lands in the error cone.
	// error is 0 to 1 between the trigger's error angle bounds, or below 0 for none.
	// errorAngle is an AI's aim cone in radians, replacing the trigger's when nonzero.
	public static List<ProjectileLaunch> Build( WeaponTag weapon, TriggerTag trigger, Vector3 origin, Vector3 aim, int seed, float error, Vector3 ownerVelocity, Guid target, Guid owner, string weaponPath, ProjectileKey key, float? errorAngle = null ) {
		var list = new List<ProjectileLaunch>();
		var projectile = HaloDataStore.Game?.Projectile( trigger.Projectile );
		if ( projectile is null ) return list;

		var count = Math.Max( 1, trigger.ProjectilesPerShot );
		var cone = error < 0 ? 0 : errorAngle is { } angle && angle != 0 ? angle : (trigger.ErrorAngle is { Length: > 1 } e ? e[0].LerpTo( e[1], error ) : 0);
		var speed = HaloUnits.PerTickToInchesPerSecond( projectile.InitialVelocity );

		var right = Vector3.Cross( aim, Vector3.Up ).Normal;
		if ( right.LengthSquared < 0.01f ) right = Vector3.Right;
		var up = Vector3.Cross( right, aim ).Normal;

		Vector3 first = default;
		for ( var i = 0; i < count; i++ ) {
			var random = new HaloRandom( seed + i * 7919 );

			var dir = aim;
			if ( trigger.DistributionFunction == "horizontal fan" && count > 1 ) {
				var k = (i + 1) / 2;
				var offset = count % 2 == 1 ? (i == 0 ? 0 : (i % 2 == 1 ? k : -k)) : (i % 2 == 0 ? (i / 2 + 0.5f) : -(i / 2 + 0.5f));
				dir = Rotation.FromAxis( up, offset * trigger.DistributionAngle ) * aim;
			}

			if ( i > 0 && trigger.HasFlag( "projectiles have identical error" ) )
				dir = first;
			else
				dir = random.InCone( dir, trigger.MinimumError, cone );

			if ( i == 0 ) first = dir;

			var inherited = dir * MathF.Max( 0, Vector3.Dot( ownerVelocity, dir ) );
			list.Add( new ProjectileLaunch {
				Projectile = trigger.Projectile,
				Origin = origin,
				Velocity = dir * speed + inherited,
				Owner = owner,
				Target = target,
				Weapon = weaponPath,
				Tracer = (seed & 1) == 1,
				Seed = seed + i * 7919 + 1,
				Key = key.Nth( i ),
			} );
		}

		return list;
	}
}
