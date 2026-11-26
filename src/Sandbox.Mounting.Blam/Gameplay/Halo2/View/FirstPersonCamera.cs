using System;
using Halo2.Combat;
using Halo2.Data;

namespace Halo2.Combat;

// Halo 2's first person camera for a player's biped: the eye point moves toward the body's head as the view looks down.
// Each tick the head's offset from node 0 is stored along the level aim, following the pose by at most 0.01 wu an axis.
// The move eases in from the biped's interpolation start angle to its end, scaled per axis by its camera scales.
public sealed class FirstPersonCamera {
	// World units a tick each axis of the stored offset can follow the pose.
	const float FollowPerTick = 0.01f;

	// The clip test starts up to 0.25 wu behind the eye point, at 0.9 of the way to anything there.
	// It keeps the camera 0.05 wu short of what it hits.
	const float ClipBehind = 0.25f, ClipBehindFraction = 0.9f, ClipStandOff = 0.05f;

	// The stored offset as forward, left and up along the level aim, in inches: this tick's and the last.
	Vector3 _offset, _previous;
	bool _valid;

	// A new unit takes the pose's offset outright on its first tick.
	public void Reset() => _valid = false;

	// One tick: marker is the camera marker's offset from node 0 in world axes, aim the unit's aim.
	public void Tick( Vector3 marker, Vector3 aim ) {
		var (forward, left) = Axes( aim );
		var wanted = new Vector3( Vector3.Dot( marker, forward ), Vector3.Dot( marker, left ), marker.z );
		if ( !_valid ) {
			_offset = _previous = wanted;
			_valid = true;
			return;
		}

		_previous = _offset;
		var step = HaloUnits.ToInches( FollowPerTick );
		_offset += new Vector3( Math.Clamp( wanted.x - _offset.x, -step, step ), Math.Clamp( wanted.y - _offset.y, -step, step ),
			Math.Clamp( wanted.z - _offset.z, -step, step ) );
	}

	// The camera for the eye point, the unit's aim and node 0's world position.
	// drawn takes the stored offset between ticks, for the view; otherwise this tick's, for shots.
	public Vector3 Position( Scene scene, Vector3 eye, Vector3 aim, Vector3 root, bool drawn ) {
		var biped = HaloDataStore.SpartanBiped;
		if ( !_valid || biped is null || biped.CameraInterpolationEnd <= biped.CameraInterpolationStart ) return eye;

		// Halo's cosine transition over the angle below level.
		var down = aim.z >= 0 ? 0 : MathF.Asin( MathF.Min( -aim.z, 1 ) ).RadianToDegree();
		var t = Math.Clamp( (down - biped.CameraInterpolationStart) / (biped.CameraInterpolationEnd - biped.CameraInterpolationStart), 0, 1 );
		var f = (1 - MathF.Cos( MathF.PI * t )) / 2;
		if ( f <= 0 ) return eye;

		var stored = drawn ? _previous.LerpTo( _offset, HaloTick.Fraction ) : _offset;
		var (forward, left) = Axes( aim );
		var fromRoot = eye - root;
		var exclusion = HaloUnits.ToInches( biped.CameraExclusionDistance );
		var target = eye
			+ forward * (f * biped.CameraForwardMovementScale * (MathF.Max( stored.x, exclusion ) - Vector3.Dot( fromRoot, forward )))
			+ left * (f * biped.CameraSideMovementScale * (stored.y - Vector3.Dot( fromRoot, left )))
			+ Vector3.Up * (f * biped.CameraVerticalMovementScale * (stored.z - fromRoot.z));
		return Clip( scene, eye, target );
	}

	// From a little behind the eye point toward the target, stopping short of the world.
	static Vector3 Clip( Scene scene, Vector3 eye, Vector3 target ) {
		var move = target - eye;
		if ( move.LengthSquared <= 0 || ProjectileSimulation.Get( scene ) is not { } world ) return target;

		var direction = move.Normal;
		var behind = eye - direction * HaloUnits.ToInches( ClipBehind );
		var back = world.WorldTrace( eye, behind );
		var start = back.Hit ? eye + (behind - eye) * (back.Fraction * ClipBehindFraction) : behind;

		var standOff = HaloUnits.ToInches( ClipStandOff );
		var ahead = world.WorldTrace( start, start + direction * HaloUnits.ToInches( 1 ) );
		if ( ahead.Hit && Vector3.Dot( target - start, direction ) + standOff > Vector3.Dot( ahead.HitPosition - start, direction ) )
			return ahead.HitPosition - direction * standOff;
		return target;
	}

	// The level aim and its left.
	static (Vector3 Forward, Vector3 Left) Axes( Vector3 aim ) {
		var forward = aim.WithZ( 0 ).Normal;
		return (forward, new Vector3( -forward.y, forward.x, 0 ));
	}
}
