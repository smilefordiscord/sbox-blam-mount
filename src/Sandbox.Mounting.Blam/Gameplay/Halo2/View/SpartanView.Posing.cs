using System;
using Halo2.Data;

namespace Halo2.Combat;

// Halo 2's first person weapon sway: springs driven by movement, turning and firing.
// Their values weight the frames of the weapon's "overlays" animation and pick the "pitch_and_turn" column.
// Movement and turning are the player's; each hand's state has its own push-back.
public sealed class FirstPersonSway {
	// Halo 2's limits when the weapon tag leaves its own at 0: acceleration and top speed, per second.
	const float DefaultMovementAcceleration = 72, DefaultMovementVelocity = 15;
	const float DefaultTurningAcceleration = 27, DefaultTurningVelocity = 6;
	const float PushBackAcceleration = 9, PushBackVelocity = 6;

	// Every primary shot adds this push-back velocity.
	// It's under the spring's snap step at 30 Hz, so the push-back never shows, as in Halo 2.
	const float FiringPushBackVelocity = 0.05f;

	// Drawn between the last two ticks.
	static float Blend => HaloTick.Fraction;

	// x = forward/back input, y = left/right (left +), -1..1.
	public Vector2 Position => new( _piPrev.LerpTo( _pi, Blend ), _pjPrev.LerpTo( _pj, Blend ) );
	float _pi, _pj, _piVelocity, _pjVelocity, _piPrev, _pjPrev;

	// x = yaw rate (left +), y = pitch rate (down +), in radians per second up to 1.
	public Vector2 Turning => new( _tiPrev.LerpTo( _ti, Blend ), _tjPrev.LerpTo( _tj, Blend ) );
	float _ti, _tj, _tiVelocity, _tjVelocity, _tiPrev, _tjPrev;

	// 0..1.
	public float PushBack( Hand hand ) {
		var push = Push( hand );
		return push.Previous.LerpTo( push.Value, Blend );
	}

	struct Spring {
		public float Value, Velocity, Previous;
	}

	Spring _pushRight, _pushLeft;

	ref Spring Push( Hand hand ) => ref hand == Hand.Left ? ref _pushLeft : ref _pushRight;

	Angles _lastFacing;
	bool _hasFacing;

	public void Reset() {
		_pi = _pj = _piVelocity = _pjVelocity = _piPrev = _pjPrev = 0;
		_ti = _tj = _tiVelocity = _tjVelocity = _tiPrev = _tjPrev = 0;
		_pushRight = _pushLeft = default;
		_hasFacing = false;
	}

	public void Fired( Hand hand ) => Push( hand ).Velocity += FiringPushBackVelocity;

	// All springs exactly at rest, as Halo tests before running the idle timer.
	public bool Settled => _pi == 0 && _pj == 0 && _ti == 0 && _tj == 0 && _pushRight.Value == 0 && _pushLeft.Value == 0;

	// throttle is x forward, y left. facing is the view in s&box angles: pitch down +, yaw left +.
	public void Tick( Vector2 throttle, Angles facing, WeaponTag weapon ) {
		if ( !_hasFacing ) {
			_lastFacing = facing;
			_hasFacing = true;
		}

		// The facing change over the tick in radians; Halo's pitch is up +, ours is down +.
		const float dt = HaloUnits.TickInterval;
		var yaw = MathX.DegreeToRadian( Angles.NormalizeAngle( facing.yaw - _lastFacing.yaw ) );
		var pitchUp = MathX.DegreeToRadian( -Angles.NormalizeAngle( facing.pitch - _lastFacing.pitch ) );
		_lastFacing = facing;

		_piPrev = _pi; _pjPrev = _pj; _tiPrev = _ti; _tjPrev = _tj;

		var moveAcceleration = Or( weapon?.MaximumMovementAcceleration, DefaultMovementAcceleration );
		var moveVelocity = Or( weapon?.MaximumMovementVelocity, DefaultMovementVelocity );
		var turnAcceleration = Or( weapon?.MaximumTurningAcceleration, DefaultTurningAcceleration );
		var turnVelocity = Or( weapon?.MaximumTurningVelocity, DefaultTurningVelocity );

		Approach( ref _pi, ref _piVelocity, throttle.x.Clamp( -1, 1 ), moveAcceleration, moveVelocity, -1, 1 );
		Approach( ref _pj, ref _pjVelocity, throttle.y.Clamp( -1, 1 ), moveAcceleration, moveVelocity, -1, 1 );
		Approach( ref _ti, ref _tiVelocity, (yaw / dt).Clamp( -1, 1 ), turnAcceleration, turnVelocity, -1, 1 );
		Approach( ref _tj, ref _tjVelocity, (-pitchUp / dt).Clamp( -1, 1 ), turnAcceleration, turnVelocity, -1, 1 );
		TickPush( ref _pushRight );
		TickPush( ref _pushLeft );
	}

	static void TickPush( ref Spring push ) {
		push.Previous = push.Value;
		Approach( ref push.Value, ref push.Velocity, 0, PushBackAcceleration, PushBackVelocity, 0, 1 );
	}

	static float Or( float? value, float fallback ) => value is > 0 ? value.Value : fallback;

	// Halo 2's spring: toward the target as fast as stopping in time allows, within its acceleration and top speed.
	// Within a step of the target it snaps there.
	static void Approach( ref float position, ref float velocity, float target, float acceleration, float maximumVelocity, float min, float max ) {
		const float dt = HaloUnits.TickInterval;
		var error = target - position;
		if ( MathF.Abs( error - velocity * dt ) <= MathF.Min( maximumVelocity * dt, acceleration * dt * dt ) ) {
			position = target;
			velocity = 0;
			return;
		}

		var desired = MathF.Sign( error ) * MathF.Min( MathF.Sqrt( 2 * acceleration * MathF.Abs( error ) ), maximumVelocity );
		var change = Math.Clamp( (desired - velocity) / dt, -acceleration, acceleration );
		velocity += change * dt;
		position = Math.Clamp( position + (velocity + 0.5f * change * dt) * dt, min, max );
	}
}

// The first person weapon's idle timer: after a random 10-15 s idle, the weapon plays its "posing" animation.
// A skip roll can pass a pose over.
// Pulling a trigger, zooming, moving or turning resets it and cuts a pose short.
public sealed class FirstPersonIdle {
	// Halo keeps both as shorts, in ticks.
	short _ticksIdle;
	short _ticksUntilPose;

	// MCC's globals values, for an export without them.
	const float DefaultIdleTimeLower = 10;
	const float DefaultIdleTimeUpper = 15;
	const float DefaultSkipFraction = 0.25f;

	public enum Change { None, Pose, Idle }

	// A new weapon restarts the idle count but keeps the pose time.
	public void Reset() => _ticksIdle = 0;

	// One tick of the idle check, after the sway springs step.
	// Returns Pose to start posing, Idle to cut a pose short.
	public Change Tick( IHalo2Hands spartan, FirstPersonSway sway, bool idle, bool posing ) {
		var weapons = spartan;
		if ( weapons.WantsFire || weapons.WantsGrenade || spartan.ZoomLevel >= 0 || !sway.Settled ) {
			_ticksIdle = 0;
			return posing ? Change.Idle : Change.None;
		}

		if ( !idle ) {
			_ticksIdle = 0;
			return Change.None;
		}

		var player = HaloDataStore.Game?.Globals?.Player;
		var bounds = player?.FirstPersonIdleTime;
		var lower = bounds is { Length: > 1 } ? bounds[0] : DefaultIdleTimeLower;
		var upper = bounds is { Length: > 1 } ? bounds[1] : DefaultIdleTimeUpper;
		var skip = bounds is { Length: > 1 } ? player.FirstPersonSkipFraction : DefaultSkipFraction;

		if ( _ticksUntilPose == 0 )
			_ticksUntilPose = (short)MathF.Round( Game.Random.Float( lower, upper ) * HaloUnits.TicksPerSecond );

		// A skipped pose doesn't reset the idle count, so a new timeout below it fires on the next tick.
		_ticksIdle++;
		if ( _ticksIdle > _ticksUntilPose ) {
			_ticksUntilPose = 0;
			if ( Game.Random.Float() >= skip ) return Change.Pose;
		}
		return Change.None;
	}
}

// The weight of the "moving", "pitch_and_turn" and "overlays" layers, one per player as in Halo 2.
// They fade in over 0.4 s, and out over 0.2 s while a hand's state animation is flagged to disable weapon aim.
public sealed class FirstPersonOverlayFade {
	const float InSeconds = 0.4f, OutSeconds = 0.2f;

	// Ticks into the fade and its length; a length of 0 is fully off.
	int _index, _count;
	bool _on;
	float _value, _previous;

	public float Value => _previous.LerpTo( _value, HaloTick.Fraction );

	// Steps the fade, then aims it for the states this tick left.
	public void Tick( bool on ) {
		_previous = _value;
		if ( _count > 0 ) _index = Math.Clamp( _index + (_on ? 1 : -1), 0, _count );
		Set( on );
		_value = _count > 0 ? Smoothstep( (float)_index / _count ) : 0;
	}

	// A new aim or length keeps the fade's fraction, rounded down.
	void Set( bool on ) {
		var ticks = FirstPersonPose.BlendTicks( on ? InSeconds : OutSeconds );
		if ( _count > 0 ) {
			if ( on == _on && ticks == _count ) return;
			_index = (int)((float)_index / _count * (ticks - 1));
		}
		else {
			_index = on ? 1 : ticks - 1;
		}
		_count = ticks;
		_on = on;
	}

	static float Smoothstep( float t ) => Math.Clamp( t * t * (3 - 2 * t), 0, 1 );
}

// What the overlays read each frame, drawn between ticks.
public readonly record struct FirstPersonLayers( Vector2 Move, Vector2 Turn, float PushBack, float Pitch, float Rounds, float Fade );

// Poses the first person view model as Halo 2 does: the state animation, then the "moving", "pitch_and_turn" and
// "overlays" overlays at the overlay fade's weight, the "ammunition" one, then the blend from the pose a new state started over.
// The left hand's weapon then moves from the right hand to the left, its local transform reflected left to right.
// Dual wielding, Halo 2 draws the hands once: the right hand's pose with the left arm's nodes from the left hand's.
// Runs as procedural bones on top of the renderer's own animation.
public sealed class FirstPersonPose {
	readonly SkinnedModelRenderer _renderer;
	readonly List<(BoneCollection.Bone Bone, GameObject Object, int Parent)> _bones = new();
	readonly Dictionary<string, int> _boneIndex = new();
	bool[] _leftArm;
	readonly AnimationClip _moving;
	readonly AnimationClip _pitchAndTurn;
	readonly FirstPersonGraph.BlendScreen _screen;
	readonly AnimationClip _overlays;
	readonly AnimationClip _ammunition;

	Transform[] _last;
	Transform[] _snapshot;
	Transform[] _animatedWorld;
	Transform[] _model;
	int _interpolationIndex;
	int _interpolationCount;
	int _cameraControl = -1;

	// The left hand's gun node, the hand node it's animated under and the one it's drawn under, or -1.
	int _gun = -1, _gunHand = -1, _otherHand = -1;

	// The moving overlay's frame, and its last tick's step for drawing between ticks.
	bool _movingActive;
	float _movingFrame;
	float _movingStep;

	bool _primed;
	int _misses;

	// dual picks the overlays of the "dual" weapon class.
	FirstPersonPose( SkinnedModelRenderer renderer, FirstPersonGraph graph, bool dual ) {
		_renderer = renderer;
		_moving = graph.Clip( "moving", dual );
		_pitchAndTurn = graph.Clip( "pitch_and_turn", dual );
		_screen = graph.Screen( graph.Head( "pitch_and_turn", dual ) );
		_overlays = graph.Clip( "overlays", dual ) is { FrameCount: >= 9 } overlays ? overlays : null;
		_ammunition = graph.Clip( "ammunition", dual );
	}

	// leftHand moves the gun to the other hand, for the left hand's weapon.
	public static FirstPersonPose Create( SkinnedModelRenderer renderer, FirstPersonGraph graph, bool dual, bool leftHand ) {
		var pose = new FirstPersonPose( renderer, graph, dual );
		renderer.CreateBoneObjects = true;
		var all = renderer.Model.Bones.AllBones.ToList();
		var gun = leftHand ? graph.OtherHandGun : null;
		pose._leftArm = new bool[all.Count];
		foreach ( var bone in all ) {
			var go = renderer.GetBoneObject( bone.Index );
			if ( !go.IsValid() ) return null;
			pose._bones.Add( (bone, go, bone.Parent?.Index ?? -1) );
			var index = pose._bones.Count - 1;
			pose._boneIndex[bone.Name] = index;
			pose._leftArm[index] = graph.LeftArmNodes.Contains( bone.Name );
			if ( bone.Name == "camera_control" ) pose._cameraControl = index;
			if ( bone.Name == gun?.Gun ) pose._gun = index;
			if ( bone.Name == gun?.Hand ) pose._otherHand = index;
		}
		if ( pose._gun < 0 || pose._otherHand < 0 || pose._otherHand > pose._gun ) pose._gun = -1;
		else pose._gunHand = pose._bones[pose._gun].Parent;

		pose._last = new Transform[all.Count];
		pose._snapshot = new Transform[all.Count];
		pose._animatedWorld = new Transform[all.Count];
		pose._model = new Transform[all.Count];
		foreach ( var b in pose._bones )
			b.Object.Flags |= GameObjectFlags.ProceduralBone;
		return pose;
	}

	// Halo 2's blend times into a new state, in seconds.
	public const float ShortBlend = 0.1335f, LongBlend = 0.267f;

	// The ticks a blend of this many seconds takes: Halo 2 rounds them to ticks, less one.
	public static int BlendTicks( float seconds ) => seconds <= 0 ? 0 : Math.Clamp( (int)MathF.Round( seconds * HaloUnits.TicksPerSecond ), 1, 127 ) - 1;

	// Blends from the drawn pose over this many ticks; a blend shorter than the one under way is ignored.
	public void StartInterpolation( int ticks ) {
		if ( ticks <= 0 || !_primed || ticks < _interpolationCount - _interpolationIndex ) return;
		Array.Copy( _last, _snapshot, _last.Length );
		_interpolationIndex = 0;
		_interpolationCount = ticks;
	}

	public void CancelInterpolation() => _interpolationCount = 0;

	// The pose drawn before the state animation jumped, and the tick it jumped on.
	Transform[] _jumpedFrom;
	long _jumpTick = -1;

	// A loop's wrap or a state started without a blend jumps the animation, which the sequence time can't draw between.
	// That tick draws from the last drawn pose instead, as every other tick draws from the frame before.
	public void Jumped() {
		if ( !_primed ) return;
		// Made on first use for hot reload.
		_jumpedFrom ??= new Transform[_last.Length];
		Array.Copy( _last, _jumpedFrom, _last.Length );
		_jumpTick = HaloTick.Count;
	}

	// The last drawn pose goes stale while hidden, so it reappears without blending.
	public void Hidden() {
		_primed = false;
		_interpolationCount = 0;
	}

	// One tick of the state blend and the "moving" overlay.
	// That plays while the player moves on the ground, at the throttle's rate from half to full speed.
	// Starting blends over 3 ticks; stopping drops it at once, blending over 7 if the weapon is idle.
	public void Tick( float throttle, bool grounded, bool idle ) {
		if ( _interpolationCount > 0 && ++_interpolationIndex >= _interpolationCount ) _interpolationCount = 0;
		if ( _moving is null ) return;

		var moving = throttle > 0.1f && grounded;
		if ( moving && !_movingActive ) {
			StartInterpolation( BlendTicks( ShortBlend ) );
			_movingActive = true;
			_movingFrame = 0;
			_movingStep = 0;
			return;
		}

		if ( !moving && _movingActive ) {
			if ( idle ) StartInterpolation( BlendTicks( LongBlend ) );
			_movingActive = false;
			return;
		}

		if ( !_movingActive ) return;
		_movingStep = Math.Clamp( throttle, 0.5f, 1 );
		_movingFrame = (_movingFrame + _movingStep) % _moving.FrameCount;
	}

	// The camera_control node's pose from the model's origin, which moves the camera in first person.
	public Transform? CameraControl => _primed && _cameraControl >= 0 ? _model[_cameraControl] : null;

	// A left arm member node's last drawn pose from the model's origin, or null.
	Transform? LeftArmNode( string bone ) => _primed && _boneIndex.TryGetValue( bone, out var i ) && _leftArm[i] ? _model[i] : null;

	// A bone's last drawn pose from the model's origin, or null.
	public Transform? ModelBone( string bone ) => _primed && _boneIndex.TryGetValue( bone, out var i ) ? _model[i] : null;

	// The renderer never gave its animated pose, so the layer is off.
	public bool Failed { get; private set; }

	public void Destroy() {
		foreach ( var b in _bones )
			if ( b.Object.IsValid() ) b.Object.Flags &= ~GameObjectFlags.ProceduralBone;
	}

	// Call after the renderer's animation updates.
	// leftArm is the left hand's pose while dual wielding, posed first, and leftArmOffset its origin from this one's.
	public void Apply( in FirstPersonLayers layers, FirstPersonPose leftArm, Vector3 leftArmOffset ) {
		if ( Failed || !_renderer.IsValid() ) return;

		// Local transforms come from the animated world pose, which carries the renderer's largest scale on all axes.
		// The first person spread scales the renderer unevenly, so the root uses that same even scale.
		var rendererTransform = _renderer.WorldTransform;
		var scale = rendererTransform.Scale;
		var root = rendererTransform.WithScale( MathF.Max( scale.x, MathF.Max( scale.y, scale.z ) ) );
		for ( var i = 0; i < _bones.Count; i++ ) {
			if ( !_renderer.TryGetBoneTransformAnimation( _bones[i].Bone, out var world ) ) {
				// If it never gets posed, hand the bones back rather than freezing the view model.
				if ( ++_misses > 30 ) {
					Failed = true;
					Destroy();
					Log.Warning( "First person pose: the renderer gave no animated bones, so the view model shows its plain animation." );
				}
				return;
			}
			_animatedWorld[i] = world;
		}
		_misses = 0;

		var fraction = HaloTick.Fraction;
		var fade = layers.Fade;
		var faded = fade > 0.0001f;
		var movingFrame = faded && _movingActive ? (_movingFrame - _movingStep * (1 - fraction) + _moving.FrameCount) % _moving.FrameCount : -1;
		var interpolate = _interpolationCount > 0 ? MathF.Min( 1, (_interpolationIndex + fraction) / _interpolationCount ) : 1;
		var jumped = _primed && _jumpTick == HaloTick.Count;
		var (column, row) = PitchAndTurnCell( layers );

		for ( var i = 0; i < _bones.Count; i++ ) {
			var (bone, go, parent) = _bones[i];
			var local = (parent >= 0 ? _animatedWorld[parent] : root).ToLocal( _animatedWorld[i] );
			var name = bone.Name;

			if ( movingFrame >= 0 ) local = Overlay( local, _moving.Delta( name, (int)movingFrame, ((int)movingFrame + 1) % _moving.FrameCount, movingFrame % 1 ), fade );
			if ( faded && _pitchAndTurn is not null && _screen is not null ) local = Overlay( local, PitchAndTurn( name, column, row ), fade );

			if ( faded && _overlays is not null ) {
				local = Sway( local, name, layers.Move.x * fade, 0, 1 );
				local = Sway( local, name, layers.Move.y * fade, 3, 2 );
				// The turning frames stand in for an aiming grid the weapon lacks.
				if ( _pitchAndTurn is null ) {
					local = Sway( local, name, layers.Turn.x * fade, 4, 5 );
					local = Sway( local, name, layers.Turn.y * fade, 7, 6 );
				}
				local = Sway( local, name, layers.PushBack * fade, 8, 8 );
			}

			if ( _ammunition is not null ) {
				var frame = Math.Clamp( layers.Rounds, 0, _ammunition.FrameCount - 1 );
				// The needler's is a replacement: it sets each needle's position and scale outright.
				local = _ammunition.Replacement
					? _ammunition.Replace( local, name, (int)frame, (int)frame + 1, frame % 1 )
					: Overlay( local, _ammunition.Delta( name, (int)frame, (int)frame + 1, frame % 1 ), 1 );
			}

			if ( interpolate < 1 && _primed ) local = Blend( _snapshot[i], local, interpolate );
			if ( jumped ) local = Blend( _jumpedFrom[i], local, fraction );

			_last[i] = local;
			if ( i == _gun && _gunHand >= 0 ) local = _model[_gunHand].ToLocal( _model[_otherHand].ToWorld( Reflect( local ) ) );
			if ( leftArm?.LeftArmNode( name ) is { } arm ) {
				arm.Position += leftArmOffset;
				local = parent >= 0 ? _model[parent].ToLocal( arm ) : arm;
			}
			_model[i] = parent >= 0 ? _model[parent].ToWorld( local ) : local;
			go.LocalTransform = local;
		}
		_primed = true;
	}

	// A local transform reflected across the x-z plane, as Halo 2 rebuilds the left hand's gun: y negated, no mirroring scale.
	static Transform Reflect( Transform local ) {
		var q = local.Rotation;
		return new Transform( local.Position.WithY( -local.Position.y ), new Rotation( -q.x, q.y, -q.z, q.w ), 1 );
	}

	// A linear blend between two poses of a node; the scale stays the new pose's.
	static Transform Blend( Transform from, Transform to, float t ) =>
		new( from.Position.LerpTo( to.Position, t ), Rotation.Lerp( from.Rotation, to.Rotation, t ), to.Scale );

	// One sway input on the "overlays" frames: the first frame for a positive value, the second for a negative one.
	Transform Sway( Transform local, string bone, float value, int positive, int negative ) {
		if ( value == 0 ) return local;
		var frame = value > 0 ? positive : negative;
		return Overlay( local, _overlays.Delta( bone, frame, frame, 0 ), MathF.Abs( value ) );
	}

	// The grid cell for the turning spring's yaw and the camera's pitch, each over its angle per frame.
	(float Column, float Row) PitchAndTurnCell( in FirstPersonLayers layers ) {
		if ( _screen is not { } s ) return default;
		var yaw = layers.Turn.x;
		var pitch = layers.Pitch;
		var column = Math.Clamp( Steps( yaw, yaw < 0 ? s.RightYawPerFrame : s.LeftYawPerFrame ), -s.RightFrames, s.LeftFrames ) + s.RightFrames;
		var row = Math.Clamp( Steps( pitch, pitch < 0 ? s.DownPitchPerFrame : s.UpPitchPerFrame ), -s.DownFrames, s.UpFrames ) + s.DownFrames;
		return (column, row);
	}

	static float Steps( float angle, float perFrame ) => perFrame < 0.0001f ? 0 : angle / perFrame;

	// Blends along the cell's row between its columns, then between the two rows.
	(Rotation, Vector3) PitchAndTurn( string bone, float column, float row ) {
		var width = _screen.RightFrames + _screen.LeftFrames + 1;
		var height = _screen.DownFrames + _screen.UpFrames + 1;
		var c0 = Math.Min( (int)column, width - 1 );
		var c1 = Math.Min( c0 + 1, width - 1 );
		var r0 = Math.Min( (int)row, height - 1 );
		var r1 = Math.Min( r0 + 1, height - 1 );
		var low = _pitchAndTurn.Delta( bone, r0 * width + c0, r0 * width + c1, column - c0 );
		var high = _pitchAndTurn.Delta( bone, r1 * width + c0, r1 * width + c1, column - c0 );
		var t = row - r0;
		return (Rotation.Slerp( low.Rotation, high.Rotation, t ), low.Position.LerpTo( high.Position, t ));
	}

	// An overlay's delta at a weight: rotation post-multiplied and position added in the parent's space.
	static Transform Overlay( Transform local, (Rotation Rotation, Vector3 Position) delta, float weight ) =>
		new( local.Position + delta.Position * weight, local.Rotation * Rotation.Slerp( Rotation.Identity, delta.Rotation, weight ), local.Scale );
}
