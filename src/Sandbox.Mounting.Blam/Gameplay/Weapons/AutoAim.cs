using System;
using Halo;
using Halo.Combat;
using Halo.Data;

namespace HaloWeapons;

// Halo's autoaim, from halo1sbox's AutoAim, with every Sandbox player and NPC as a possible target (Sandbox has no teams).
// One search finds the best unit near the crosshair: within the weapon's autoaim angle and range of a pill from the unit's
// pelvis to its head (widened by the biped's autoaim width), in sight. It bends shots toward it up to the deviation angle,
// homing projectiles (needles, the charged plasma bolt) steer at it, and at level 1 Halo's reticle turns red.
public static class AutoAim {
	// Aim assist angles in degrees, ranges in inches.
	public readonly record struct Parameters( float AutoaimAngle, float AutoaimRange, float DeviationAngle );

	// The autoaim point, its distance and angle off the aim, and its level.
	public readonly record struct Target( IHaloUnit Unit, Vector3 Point, float Distance, float Angle, float Level );

	// Angles divide by the zoom magnification and ranges multiply by it; deviation is at least the autoaim angle.
	// False unzoomed for an "aim assists only when zoomed" weapon.
	public static bool GetParameters( WeaponTag weapon, bool zoomed, float magnification, out Parameters parameters ) {
		parameters = default;
		if ( weapon is null || (!zoomed && weapon.HasFlag( "aim assists only when zoomed" )) ) return false;
		magnification = MathF.Max( 1, magnification );
		parameters = new Parameters(
			weapon.AutoaimAngle / magnification,
			HaloUnits.ToInches( weapon.AutoaimRange * magnification ),
			MathF.Max( weapon.DeviationAngle, weapon.AutoaimAngle ) / magnification );
		return true;
	}

	// The autoaim target, with the shot bent toward it by its level up to the deviation angle; null and the aim unchanged without one.
	public static IHaloUnit FindTarget( Scene scene, IHaloUnit shooter, WeaponTag weapon, bool zoomed, float magnification, Vector3 origin, Vector3 aim, out Vector3 deflected, out float level ) {
		deflected = aim;
		level = 0;
		if ( !GetParameters( weapon, zoomed, magnification, out var parameters ) ) return null;
		if ( !Search( scene, shooter, parameters, origin, aim, out var target ) || target.Level <= 0 ) return null;

		level = target.Level;
		var toTarget = (target.Point - origin).Normal;
		var dir = Vector3.Lerp( aim, toTarget, target.Level ).Normal;
		var angle = MathF.Acos( Math.Clamp( Vector3.Dot( dir, aim ), -1, 1 ) ).RadianToDegree();
		if ( angle > parameters.DeviationAngle && angle > 0 )
			dir = Vector3.Lerp( aim, dir, parameters.DeviationAngle / angle ).Normal;
		deflected = dir;
		return target.Unit;
	}

	static readonly List<Target> _targets = new();

	// The first visible unit, ranked by autoaim level, then distance and angle.
	public static bool Search( Scene scene, IHaloUnit shooter, in Parameters parameters, Vector3 position, Vector3 direction, out Target target ) {
		target = default;
		_targets.Clear();
		var width = HaloUnits.ToInches( HaloDataStore.Spartan.Biped?.AutoaimWidth ?? 0 );

		foreach ( var player in scene.GetAllComponents<PlayerController>() )
			Consider( SandboxUnit.For( player.GameObject ), shooter, width, parameters, position, direction );
		foreach ( var npc in scene.GetAllComponents<Sandbox.Npcs.BaseNpc>() )
			Consider( SandboxUnit.For( npc.GameObject ), shooter, width, parameters, position, direction );

		_targets.Sort( static ( a, b ) =>
			a.Level != b.Level ? b.Level.CompareTo( a.Level ) :
			a.Distance != b.Distance ? a.Distance.CompareTo( b.Distance ) :
			a.Angle.CompareTo( b.Angle ) );

		foreach ( var candidate in _targets ) {
			var trace = scene.Trace.Ray( position, candidate.Point ).WithoutTags( "playercontroller", "trigger", "projectile", "debris", "ragdoll", "viewmodel" );
			if ( shooter?.GameObject is { } self && self.IsValid() ) trace = trace.IgnoreGameObjectHierarchy( self );
			var tr = trace.Run();
			if ( tr.Hit && tr.Fraction < 0.98f && HaloUnitDirectory.Of( tr.GameObject )?.GameObject != candidate.Unit.GameObject ) continue;
			target = candidate;
			_targets.Clear();
			return true;
		}

		_targets.Clear();
		return false;
	}

	// The pill axis point closest to the aim, pulled toward the aim by at most the width, scored by distance and angle.
	static void Consider( IHaloUnit unit, IHaloUnit shooter, float width, in Parameters parameters, Vector3 position, Vector3 direction ) {
		if ( unit is null || !unit.IsValid() || !unit.IsAlive || unit.GameObject == shooter?.GameObject ) return;
		var (a, b) = Pill( unit );

		var point = ClosestOnSegment( a, b, position, direction );
		var offset = point - position;
		var perpendicular = offset - direction * Vector3.Dot( offset, direction );
		if ( perpendicular.Length > width ) perpendicular = perpendicular.Normal * width;
		point -= perpendicular;

		var to = point - position;
		var distance = to.Length;
		if ( distance <= 0 ) return;
		var angle = MathF.Acos( Math.Clamp( Vector3.Dot( to / distance, direction ), -1, 1 ) ).RadianToDegree();

		var level = Atten( distance, parameters.AutoaimRange ) * Atten( angle, parameters.AutoaimAngle );
		if ( level > 0 ) _targets.Add( new Target( unit, point, distance, angle, level ) );
	}

	// Pelvis to head as posed, or the root's height to its head without those bones.
	static (Vector3 Base, Vector3 Top) Pill( IHaloUnit unit ) {
		var renderer = unit.Renderer;
		if ( renderer.IsValid() && renderer.TryGetBoneTransform( "pelvis", out var pelvis ) && renderer.TryGetBoneTransform( "head", out var head ) )
			return (pelvis.Position, head.Position);
		var root = unit.Root.Position;
		return (root + Vector3.Up * HaloUnits.ToInches( 0.2f ), unit.Head);
	}

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
