using System;
using Halo2.Data;

namespace Halo2.Combat;

// The projectiles of a barrel's shot, the same from the same seed on every machine.
public static class ShotLaunches {
	// Every projectile one shot makes, each turned off the aim around a random axis.
	// The angle is picked evenly between the barrel's minimum error and its error angle at the shot's error.
	// error is 0 to 1 between the error angle's bounds, or below 0 for none.
	// dual blends the error angle, the minimum error and the damage scale toward the barrel's dual wield values.
	public static List<ProjectileLaunch> Build( BarrelTag barrel, Vector3 origin, Vector3 aim, int seed, float error, float dual, Vector3 ownerVelocity, Guid target, Guid owner, string weaponPath, ProjectileKey key ) {
		var list = new List<ProjectileLaunch>();
		var projectile = string.IsNullOrEmpty( barrel.Projectile ) ? null : HaloDataStore.Game?.Projectile( barrel.Projectile );
		if ( projectile is null ) return list;
		var speed = HaloUnits.PerSecondToInchesPerSecond( projectile.InitialVelocity * ProjectileSimulation.SpeedScale( projectile ) );

		var count = Math.Max( 1, barrel.ProjectilesPerShot );
		var maximum = 0f;
		if ( error >= 0 && barrel.ErrorAngle is { Length: > 1 } ) {
			var (low, high) = barrel.ErrorAngleBounds( dual );
			maximum = (high - low) * error + low;
		}
		var minimum = error < 0 ? 0 : (1 - dual) * barrel.MinimumError + dual * barrel.DualWieldMinimumError;
		// The damage scale moves toward the damage effect's lower bound.
		var damageScale = (1 - dual) + dual * barrel.DualWieldDamageScale;

		for ( var i = 0; i < count; i++ ) {
			var random = new HaloRandom( seed + i * 7919 );
			var dir = random.InCone( aim, minimum, maximum );
			var inherited = dir * MathF.Max( 0, Vector3.Dot( ownerVelocity, dir ) );
			list.Add( new ProjectileLaunch {
				Projectile = barrel.Projectile,
				Origin = origin,
				Velocity = dir * speed + inherited,
				Owner = owner,
				Target = target,
				Weapon = weaponPath,
				Tracer = (seed & 1) == 1,
				Seed = seed + i * 7919 + 1,
				Key = key.Nth( i ),
				DamageScale = damageScale,
			} );
		}

		return list;
	}
}
