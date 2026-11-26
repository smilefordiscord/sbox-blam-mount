using System;
using Halo2.Data;

namespace Halo2.Combat;

// The local player's camera shake and temporary impulse from damage effects, including each trigger's firing damage.
// A new shake replaces the current one if it lasts longer than what's left or is stronger.
// Halo counts both down at the end of each tick and draws them every frame, easing their fade between ticks.
// Each frame the shake is the falloff of the time left times the wobble, along a fresh random axis and direction.
// The impulse from damage taken snaps the view back for a moment, like a melee to the face.
public static class CameraShake {
	[ConVar( "halo2_camera_shake", Help = "Camera shake and impulses from damage effects: firing (each trigger's firing damage) and being hit.", Saved = true )]
	public static bool Enabled { get; set; } = true;

	static DamageEffectTag _effect;
	static float _zeroScaleFactor;
	static float _durationTicks;
	static float _periodTicks;
	static int _timeLeft;
	static bool _justStarted;
	static long _countedTick = -1;

	public static void Apply( DamageEffectTag effect, float scale = 1 ) {
		if ( effect is null ) return;
		CountDown();

		// The tag's zero scale factor is always 0, so the intensity is just the scale.
		var intensity = scale;
		var duration = HaloUnits.TicksPerSecond * effect.CameraShakingDuration;

		if ( _timeLeft < duration || intensity > _zeroScaleFactor || (intensity >= _zeroScaleFactor && _timeLeft < duration) ) {
			_effect = effect;
			_zeroScaleFactor = intensity;
			_durationTicks = duration;
			_timeLeft = (short)duration;
			// The wobble period is in seconds.
			_periodTicks = HaloUnits.TicksPerSecond * effect.CameraShakingWobbleFunctionPeriod;
			_justStarted = true;
		}
	}

	// The camera tips away from the blow and is pushed along it, fading over the impulse's duration.
	// direction is the way the damage travelled, taken flat relative to facingYaw.
	// It replaces the current impulse by the same rule as the shake; PlayerEffects handles the permanent aim impulse.
	public static void Impulse( DamageEffectTag effect, Vector3 direction, float facingYaw, float scale = 1 ) {
		if ( effect is null ) return;
		CountDown();

		// This zero scale factor is always 0 in the tag too.
		var intensity = scale;
		var duration = HaloUnits.TicksPerSecond * effect.TemporaryCameraImpulseDuration;

		if ( !(_impulseDurationTicks > _impulseTimeLeft || intensity > _impulseZeroScaleFactor ||
			(intensity >= _impulseZeroScaleFactor && _impulseTimeLeft < duration)) )
			return;

		var flat = direction.WithZ( 0 );
		var facing = Rotation.FromYaw( facingYaw ).Forward.WithZ( 0 );
		if ( flat.LengthSquared < 1e-6f || facing.LengthSquared < 1e-6f ) return;
		flat = flat.Normal;
		facing = facing.Normal;

		// The blow in the player's frame of x forward, y left, which matches camera space.
		var angle = MathF.Acos( Math.Clamp( Vector3.Dot( facing, flat ), -1, 1 ) );
		if ( facing.x * flat.y - facing.y * flat.x < 0 ) angle = -angle;

		_impulse = effect;
		_impulseZeroScaleFactor = intensity;
		_impulseDurationTicks = duration;
		_impulseTimeLeft = (short)duration;
		_impulseDirection = new Vector3( MathF.Cos( angle ), MathF.Sin( angle ), 0 );

		// Jitter: a random length within the tag's bounds, perpendicular to the blow at a random angle around it.
		var jitter = effect.TemporaryCameraImpulseJitter is { Length: > 1 } j ? j : new float[] { 0, 0 };
		var magnitude = Game.Random.Float( jitter[0], jitter[1] );
		var around = Game.Random.Float( 0, 360 );
		var side = Vector3.Cross( _impulseDirection, Vector3.Up ).Normal;
		_impulseJitter = Rotation.FromAxis( _impulseDirection, around ) * side * magnitude;
		_impulseJustStarted = true;
	}

	public static void Reset() {
		_effect = null;
		_zeroScaleFactor = 0;
		_timeLeft = 0;
		_justStarted = false;
		_impulse = null;
		_impulseZeroScaleFactor = 0;
		_impulseDurationTicks = 0;
		_impulseTimeLeft = 0;
		_impulseJustStarted = false;
	}

	static DamageEffectTag _impulse;
	static float _impulseZeroScaleFactor;
	static float _impulseDurationTicks;
	static int _impulseTimeLeft;
	static bool _impulseJustStarted;
	// The blow, flat in camera space.
	static Vector3 _impulseDirection;
	// World units, in camera space.
	static Vector3 _impulseJitter;

	// Halo's end of tick count down, for each tick finished since the last call.
	// The time left drops a tick and a new effect stops being drawn at full strength.
	static void CountDown() {
		var finished = HaloTick.Count - (Game.ActiveScene is { IsFixedUpdate: true } ? 1 : 0);
		var ticks = _countedTick < 0 ? 0 : (int)Math.Clamp( finished - _countedTick, 0, short.MaxValue );
		_countedTick = finished;
		if ( ticks == 0 ) return;

		_timeLeft = Math.Max( _timeLeft - ticks, 0 );
		_impulseTimeLeft = Math.Max( _impulseTimeLeft - ticks, 0 );
		_justStarted = false;
		_impulseJustStarted = false;
	}

	// This frame's camera effect in camera space: a rotation after the camera's own, and an offset in inches.
	// The shake is applied inside the impulse's frame.
	public static (Rotation Rotation, Vector3 Offset) Sample() {
		CountDown();
		if ( !Enabled ) return (Rotation.Identity, Vector3.Zero);

		// The fades run on the time left less the frame's way into the next tick.
		var fraction = HaloTick.Fraction;
		var impulse = SampleImpulse( fraction );
		var shake = SampleShake( fraction );
		return (impulse.Rotation * shake.Rotation, impulse.Offset + impulse.Rotation * shake.Offset);
	}

	// Full until the tick it started in ends, then the fade function of the time left times the intensity.
	// That fraction of the rotation tips the camera away from the blow, and of the pushback and jitter moves it.
	static (Rotation Rotation, Vector3 Offset) SampleImpulse( float fraction ) {
		if ( _impulse is null || !(_impulseTimeLeft > 0 || _impulseJustStarted) ) return (Rotation.Identity, Vector3.Zero);

		float scale;
		if ( _impulseJustStarted ) scale = 1;
		else scale = HaloFunctions.Fade( _impulse.TemporaryCameraImpulseFadeFunction, 1 - (_impulseDurationTicks - (_impulseTimeLeft - fraction)) / _impulseDurationTicks ) * _impulseZeroScaleFactor;

		var axis = Vector3.Cross( Vector3.Up, _impulseDirection );
		var degrees = scale * _impulse.TemporaryCameraImpulseRotation;
		var rotation = degrees != 0 && axis.LengthSquared > 1e-6f ? Rotation.FromAxis( axis.Normal, degrees ) : Rotation.Identity;
		var translation = scale * _impulse.TemporaryCameraImpulsePushback;
		var offset = HaloUnits.ToInches( 1 ) * (_impulseDirection * translation + _impulseJitter * scale);
		return (rotation, offset);
	}

	// The wobble's phase steps with whole ticks.
	static (Rotation Rotation, Vector3 Offset) SampleShake( float fraction ) {
		if ( _effect is null || !(_timeLeft > 0 || _justStarted) ) return (Rotation.Identity, Vector3.Zero);

		float scale;
		if ( _justStarted ) scale = 1;
		else scale = HaloFunctions.Fade( _effect.CameraShakingFalloffFunction, 1 - (_durationTicks - (_timeLeft - fraction)) / _durationTicks ) * _zeroScaleFactor;

		var weight = _effect.CameraShakingWobbleWeight;
		var magnitude = (HaloFunctions.Periodic( _effect.CameraShakingWobbleFunction, (_durationTicks - _timeLeft) / _periodTicks ) * weight + (1 - weight)) * scale;
		var translation = MathF.Max( magnitude * _effect.CameraShakingRandomTranslation, 0 );
		var rotation = MathF.Max( magnitude * _effect.CameraShakingRandomRotation, 0 );

		// The whole angle about a random axis, and the whole translation in a random direction.
		var shake = rotation != 0 ? Rotation.FromAxis( RandomDirection(), rotation ) : Rotation.Identity;
		var offset = translation != 0 ? RandomDirection() * HaloUnits.ToInches( translation ) : Vector3.Zero;
		return (shake, offset);
	}

	// Halo picks a vertex of a 16 segment geosphere; a uniform direction is the same without the table.
	static Vector3 RandomDirection() {
		var z = Game.Random.Float( -1, 1 );
		var angle = Game.Random.Float( 0, MathF.PI * 2 );
		var r = MathF.Sqrt( 1 - z * z );
		return new Vector3( r * MathF.Cos( angle ), r * MathF.Sin( angle ), z );
	}
}
