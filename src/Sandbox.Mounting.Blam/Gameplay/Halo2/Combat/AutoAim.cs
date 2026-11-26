using System;
using Halo2.Combat;
using Halo2.Data;

namespace Halo2.Combat;

// Halo's aim assist, built on one search for the best enemy near the crosshair.
// Autoaim bends shots toward it, and at level 1 turns the reticle red and gives the sniper its range readout.
// Magnetism slows and drags a controller's look over it; MCC drops only magnetism for keyboard and mouse.
// The host bends the shots that count, against targets as the shooter saw them.
public static class AutoAim {
	// A melee only weapon dashes at a target in full autoaim farther than this, world units, origin to origin.
	const float DashMinimumDistance = 0.9f;

	// Aim assist angles in degrees, ranges in inches.
	public readonly record struct Parameters( float AutoaimAngle, float AutoaimRange, float MagnetismAngle, float MagnetismRange, float DeviationAngle );

	// The autoaim point, its distance and angle off the aim, and both levels.
	public readonly record struct Target( Halo2Unit Unit, Vector3 Point, float Distance, float Angle, float AutoaimLevel, float MagnetismLevel );

	// Angles divide by the zoom magnification and ranges multiply by it; deviation is at least the autoaim angle.
	// False unzoomed for an "aim assists only when zoomed" weapon.
	public static bool GetParameters( WeaponTag weapon, int zoomLevel, out Parameters parameters ) {
		parameters = default;
		if ( weapon is null || (zoomLevel < 0 && weapon.HasFlag( "aim assists only when zoomed" )) ) return false;

		var magnification = Zoom.Magnification( weapon, zoomLevel );
		parameters = new Parameters(
			weapon.AutoaimAngle / magnification,
			HaloUnits.ToInches( weapon.AutoaimRange * magnification ),
			weapon.MagnetismAngle / magnification,
			HaloUnits.ToInches( weapon.MagnetismRange * magnification ),
			MathF.Max( weapon.DeviationAngle, weapon.AutoaimAngle ) / magnification );
		return true;
	}

	static readonly List<Target> _targets = new();

	public static bool Search( Halo2Unit shooter, in Parameters parameters, Vector3 position, Vector3 direction, out Target target ) =>
		Search( shooter, parameters, position, direction, null, false, out target );

	// The HUD's target also takes teammates, for the friendly reticle and the name over them.
	public static bool SearchWithFriends( Halo2Unit shooter, in Parameters parameters, Vector3 position, Vector3 direction, out Target target ) =>
		Search( shooter, parameters, position, direction, null, true, out target );

	// The first visible, uncamouflaged enemy (or teammate, with friends), ranked by autoaim level, then magnetism level, distance and angle.
	// Targets are placed as drawn here, or as the shooter saw them with rewind.
	static bool Search( Halo2Unit shooter, in Parameters parameters, Vector3 position, Vector3 direction, RewindContext rewind, bool friends, out Target target ) {
		using var perf = Halo2.Combat.HaloPerf.Measure( "AutoAim.Search" );
		target = default;
		var scene = shooter?.Scene ?? Game.ActiveScene;
		_targets.Clear();
		var width = HaloUnits.ToInches( HaloDataStore.SpartanBiped?.AutoaimWidth ?? 0 );

		foreach ( var unit in Halo2Unit.All( scene ) ) {
			if ( unit == shooter ) continue;
			Consider( unit, unit.WorldPosition + Vector3.Up * HaloUnits.ToInches( 0.2f ), unit.Head, width, parameters, position, direction );
		}

		_targets.Sort( static ( a, b ) =>
			a.AutoaimLevel != b.AutoaimLevel ? b.AutoaimLevel.CompareTo( a.AutoaimLevel ) :
			a.MagnetismLevel != b.MagnetismLevel ? b.MagnetismLevel.CompareTo( a.MagnetismLevel ) :
			a.Distance != b.Distance ? a.Distance.CompareTo( b.Distance ) :
			a.Angle.CompareTo( b.Angle ) );

		foreach ( var candidate in _targets ) {
			var tr = scene.Trace.Ray( position, candidate.Point ).WithoutTags( "player", "trigger", "projectile", "debris" ).Run();
			if ( tr.Hit && tr.Fraction < 0.98f ) continue;
			target = candidate;
			_targets.Clear();
			return true;
		}

		_targets.Clear();
		return false;
	}

	// The pill axis point closest to the aim, pulled toward the aim by at most the width, scored for both levels.
	static void Consider( Halo2Unit unit, Vector3 a, Vector3 b, float width, in Parameters parameters, Vector3 position, Vector3 direction ) {
		var point = ClosestOnSegment( a, b, position, direction );
		var offset = point - position;
		var perpendicular = offset - direction * Vector3.Dot( offset, direction );
		if ( perpendicular.Length > width ) perpendicular = perpendicular.Normal * width;
		point -= perpendicular;

		var to = point - position;
		var distance = to.Length;
		if ( distance <= 0 ) return;
		var angle = MathF.Acos( Math.Clamp( Vector3.Dot( to / distance, direction ), -1, 1 ) ).RadianToDegree();

		var autoaim = Atten( distance, parameters.AutoaimRange ) * Atten( angle, parameters.AutoaimAngle );
		var magnetism = Atten( distance, parameters.MagnetismRange ) * Atten( angle, parameters.MagnetismAngle );
		if ( autoaim > 0 || magnetism > 0 )
			_targets.Add( new Target( unit, point, distance, angle, autoaim, magnetism ) );
	}

	// Bends the shot toward an autoaim target by its level, up to the deviation angle, and returns it for homing.
	// A magnetism-only target gets neither, whatever the input device.
	public static Halo2Unit FindTarget( Halo2Unit shooter, WeaponTag weapon, int zoomLevel, Vector3 origin, Vector3 aim, RewindContext rewind, out Vector3 deflected ) {
		deflected = aim;
		if ( !GetParameters( weapon, zoomLevel, out var parameters ) ) return null;
		if ( !Search( shooter, parameters, origin, aim, rewind, false, out var target ) || target.AutoaimLevel <= 0 ) return null;

		var toTarget = (target.Point - origin).Normal;
		var dir = Vector3.Lerp( aim, toTarget, target.AutoaimLevel ).Normal;
		var angle = MathF.Acos( Math.Clamp( Vector3.Dot( dir, aim ), -1, 1 ) ).RadianToDegree();
		if ( angle > parameters.DeviationAngle && angle > 0 )
			dir = Vector3.Lerp( aim, dir, parameters.DeviationAngle / angle ).Normal;
		deflected = dir;
		return target.Unit;
	}

	// The target a melee only weapon's trigger dashes at: the autoaim target at full level more than 0.9 wu from the feet.
	public static Halo2Unit DashTarget( Halo2Unit shooter, WeaponTag weapon, int zoomLevel, Vector3 origin, Vector3 feet, Vector3 aim, RewindContext rewind ) {
		if ( !GetParameters( weapon, zoomLevel, out var parameters ) ) return null;
		if ( !Search( shooter, parameters, origin, aim, rewind, false, out var target ) || target.AutoaimLevel < 1 ) return null;
		var position = target.Unit.WorldPosition;
		return HaloUnits.ToWorldUnits( position.Distance( feet ) ) > DashMinimumDistance ? target.Unit : null;
	}

	// 0 without a target; applies to keyboard and mouse too.
	public static float AutoaimLevel( Halo2Unit shooter, WeaponTag weapon, int zoomLevel, Vector3 origin, Vector3 aim ) =>
		GetParameters( weapon, zoomLevel, out var parameters ) && Search( shooter, parameters, origin, aim, out var target ) ? target.AutoaimLevel : 0;

	// 1 up to half the maximum, then linear down to 0 at the maximum.
	static float Atten( float value, float max ) {
		if ( max <= 0 ) return 0;
		if ( value <= max * 0.5f ) return 1;
		return Math.Clamp( 1 - (value - max * 0.5f) / (max * 0.5f), 0, 1 );
	}

	// The point on segment ab closest to the ray.
	static Vector3 ClosestOnSegment( Vector3 a, Vector3 b, Vector3 origin, Vector3 dir ) {
		var u = b - a;
		var w = a - origin;
		var aa = Vector3.Dot( u, u );
		var bb = Vector3.Dot( u, dir );
		var cc = Vector3.Dot( dir, dir );
		var dd = Vector3.Dot( u, w );
		var ee = Vector3.Dot( dir, w );
		var denom = aa * cc - bb * bb;
		var s = denom > 1e-6f ? Math.Clamp( (bb * ee - cc * dd) / denom, 0, 1 ) : 0;
		return a + u * s;
	}
}
