using System;
using Halo2.Data;

namespace Halo2.Combat;

// Halo 2's unit animation for a Spartan's third person body, run each tick on every machine from the replicated state.
// The base state (idle, move, turn, airborne, land) picks a run and a walk animation, the right and left hands' actions
// play over it, then the arm offsets, the aiming grid and the grips; changes cross-fade the whole pose.
// Rendering blends the last two ticks' poses.
public sealed class SpartanAnimation {
	// One tick's state: throttle x forward and y left, velocity relative to the ground in wu/s.
	// Crouching is the unit's crouch bit; Weapon is the right hand's and LeftWeapon the left's while dual wielding, else null.
	public readonly record struct Input( Vector2 Throttle, Angles Eyes, Vector3 Velocity, bool Airborne, bool Crouching,
		WeaponTag Weapon, bool Overheated, WeaponTag LeftWeapon, bool LeftOverheated );

	// A playing animation and its frame, advanced by its rate each tick; an action's chain head and its ticks before it shows.
	sealed class Channel {
		public UnitClip Clip;
		public FirstPersonGraph.Animation Head;
		public string Label;
		public float Frame;
		public float Rate = 1;
		public int Delay;
	}

	// Halo 2's fader: steps toward on or off once a tick, read through smoothstep.
	struct Fader {
		int _step, _count;
		bool _on, _set;

		public void Set( bool on, float seconds ) {
			if ( _set && _on == on ) return;
			var count = FirstPersonPose.BlendTicks( seconds );
			_step = !_set ? (on ? 1 : count - 1) : (int)(_step / (float)Math.Max( 1, _count ) * (count - 1));
			_count = count;
			_on = on;
			_set = true;
		}

		public void Tick() {
			if ( _set ) _step = _on ? Math.Min( _step + 1, _count ) : Math.Max( _step - 1, 0 );
		}

		public float Value => _set && _count > 0 ? Smoothstep( _step / (float)_count ) : 0;
	}

	// Halo 2's blend times in seconds: 0.267 and 0.1335 for state changes, 0.06675 for chambering and overheating.
	const float LongBlend = FirstPersonPose.LongBlend, ShortBlend = FirstPersonPose.ShortBlend, FlashBlend = 0.06675f;

	// The aiming grid fades in over 0.4 s after a spawn; no Spartan animation turns it off.
	const float AimFadeIn = 0.4f;

	// Ticks in the air before the airborne animation, and the most counted.
	const int AirborneTicks = 5, MostAirborneTicks = 127;

	// A move animation stays on for 0.1 s of idle throttle.
	const float IdleHold = 0.1f;

	// A throttle shorter than 0.1 counts as none.
	const float ThrottleDeadzone = 0.01f;

	// Standing, the body turns once the aim is the threshold's angle away; past cos 0.9 behind, a turn keeps its side.
	const float BehindCosine = -0.9f;

	// Moving, the body faces along the move while the aim's cosine to it passes 0.3, or 0.4 while the body is more
	// than 60 degrees off the move; an aim steeper than sine 0.9 always faces along.
	const float AlongCosine = 0.3f, AcrossAlongCosine = 0.4f, AcrossBodyCosine = 0.5f, SteepAimSine = 0.9f;

	// Actions show 2 ticks after they start, a repeated one too.
	const int ActionDelay = 2;

	// Halo 2's values when the biped tag isn't exported yet: degrees per second and degrees.
	const float DefaultMovingTurningSpeed = 486.2f, DefaultStationaryTurningThreshold = 80;
	const float DefaultMaximumSoftLandingTime = 0.6f, DefaultMinimumSoftLandingVelocity = 1.5f, DefaultMinimumHardLandingVelocity = 7;

	readonly UnitGraph _graph;
	Model _model;
	string[] _boneNames;
	int[] _parents;
	// The graph's arm masks: the right hand's arm offset and grip move only right arm nodes, the left hand's only left ones.
	bool[] _leftArm, _rightArm;
	Transform[] _bind;

	Transform[] _stage, _snapshot, _previous, _current, _drawn, _world;
	bool _hasPose;

	// The base state. The type is the right hand's weapon's; the left's is kept apart for the left hand's layers.
	string _mode, _class, _type, _label;

	// The hands' weapons the class and types come from, and the class from before the left hand took a weapon.
	WeaponTag _rightWeapon, _leftWeapon;
	string _leftType, _classBeforeDual;
	Channel _run, _walk;
	float _walkWeight;
	int _idleCounter;

	// Body facing, degrees left, this tick and last.
	float _bodyYaw, _previousBodyYaw;

	int _airborneTicks;
	bool _wasAirborne;
	Vector3 _fallingVelocity;
	int _landingTicks = -1, _landingCount;
	bool _landingHard;

	int _interpolationIndex, _interpolationCount;

	// The right and left hands' action channels over the base, and actions to start on the next tick.
	Channel _action, _leftAction;
	readonly List<(string Label, float Blend, Hand Channel)> _pending = new();
	bool _wasOverheated, _wasLeftOverheated;

	// While dual wielding each hand has its arm offsets and grip, limited to its arm.
	UnitClip _aimUp, _aimDown, _grip, _leftGrip, _armOffsets, _leftArmOffsets;
	Fader _aimFader, _gripFader;
	float _aimYaw, _aimPitch;

	// Each action frame shown, for its sounds and effects, with the hand whose weapon the action is.
	public Action<AnimationEntry, int, Hand> ActionFrame { get; set; }

	// A footstep: its material effects kind, its contact point (0 left foot, 1 right foot, OriginContact) and its scale.
	public Action<int, int, float> Footstep { get; set; }

	// Past the Spartan's two contact points, a footstep plays at the biped's origin.
	public const int OriginContact = 2;

	// The walk takes over the foot events past this walk weight and the run takes them back below the lower one.
	const float WalkStepsAbove = 0.55f, RunStepsBelow = 0.45f;

	// A foot event at or under 1.5 wu/s, or below half scale, is a walking step.
	const float WalkingStepSpeed = 1.5f, WalkingStepScale = 0.5f;

	// The feet shuffle on the second tick without throttle after moving.
	const int StopShuffleTick = 2;

	bool _walkSteps;
	// The last foot event's scale in 127ths, which a stop shuffle plays at and any shuffle clears.
	int _stepScale;
	int _stillTicks = -1;

	// The body facing to draw, between the last two ticks.
	public float DrawnBodyYaw => _previousBodyYaw + Angles.NormalizeAngle( _bodyYaw - _previousBodyYaw ) * HaloTick.Fraction;

	SpartanAnimation( UnitGraph graph, Model model ) {
		_graph = graph;
		_model = model;
	}

	// Headless: the pose is only computed, for a caller that reads it (DrawnWorld, Marker) and draws it on a model of its own.
	public static SpartanAnimation Create( Model model, string modelTag ) {
		if ( model is null || UnitGraph.For( modelTag ) is not { } graph ) return null;
		var animation = new SpartanAnimation( graph, model );
		return animation.TakeBones() ? animation : null;
	}

	// The body always faces the caller's way (yaw 0), as Sandbox turns the body itself: Eyes and Throttle come relative to it, and no turn plays.
	public bool FixedFacing { get; set; }

	bool TakeBones() {
		var bones = _model.Bones.AllBones.ToList();
		var count = bones.Count;
		if ( count == 0 ) return false;
		if ( _boneNames?.Length == count ) return true;

		_boneNames = bones.Select( x => x.Name ).ToArray();
		_parents = bones.Select( x => x.Parent is { } p ? bones.IndexOf( p ) : -1 ).ToArray();
		_leftArm = new bool[count];
		_rightArm = new bool[count];
		for ( var i = 0; i < count; i++ ) {
			_leftArm[i] = _graph.LeftArmNodes?.Contains( _boneNames[i] ) == true;
			_rightArm[i] = _graph.RightArmNodes?.Contains( _boneNames[i] ) == true;
		}

		// Bone.LocalTransform is in model space.
		_bind = new Transform[count];
		for ( var i = 0; i < count; i++ )
			_bind[i] = _parents[i] >= 0 ? bones[_parents[i]].LocalTransform.ToLocal( bones[i].LocalTransform ) : bones[i].LocalTransform;

		_stage = new Transform[count];
		_snapshot = new Transform[count];
		_previous = new Transform[count];
		_current = new Transform[count];
		_drawn = new Transform[count];
		_world = new Transform[count];
		return true;
	}


	// A new unit: nothing playing, facing its view, the aiming grid fading in.
	public void Reset() {
		_hasPose = false;
		_mode = _class = _type = _label = null;
		_rightWeapon = _leftWeapon = null;
		_leftType = _classBeforeDual = null;
		_run = _walk = _action = _leftAction = null;
		_walkWeight = 0;
		_walkSteps = false;
		_stepScale = 0;
		_stillTicks = -1;
		_idleCounter = 0;
		_airborneTicks = 0;
		_wasAirborne = false;
		_landingTicks = -1;
		_interpolationCount = 0;
		_pending.Clear();
		_wasOverheated = _wasLeftOverheated = false;
		_aimUp = _aimDown = _grip = _leftGrip = _armOffsets = _leftArmOffsets = null;
		_aimFader = default;
		_gripFader = default;
	}

	// Weapon actions from the hands' weapons' events, started on the next tick so a new weapon's class is in place.
	// The left hand's actions are its "_left" labels: its shots play on the right hand's channel, the rest on its own.
	public void Fired( Hand hand, int barrel ) => Queue( (barrel == 0 ? "fire_1" : "fire_2") + Suffix( hand ), 0, Hand.Right );
	public void Reloaded( Hand hand ) => Queue( "reload_1" + Suffix( hand ), LongBlend, hand );
	public void Chambered( Hand hand ) => Queue( "chamber_1" + Suffix( hand ), FlashBlend, hand );
	public void Readied( Hand hand ) => Queue( "ready" + Suffix( hand ), LongBlend, hand );
	public void PutAway( Hand hand ) => Queue( "put_away" + Suffix( hand ), LongBlend, hand );
	public void ThrewGrenade() => Queue( "throw_grenade", ShortBlend, Hand.Right );
	public void Overheating( Hand hand ) => Queue( "overheating" + Suffix( hand ), FlashBlend, hand );

	// A melee strike, or a sword lunge, airborne or not.
	public void Melee( string label ) => Queue( label, 0, Hand.Right );

	void Queue( string label, float blend, Hand channel ) => _pending.Add( (label, blend, channel) );

	static string Suffix( Hand hand ) => hand == Hand.Left ? "_left" : "";

	// The hand an action's weapon is in, by its label.
	static Hand Owner( string label ) => label.Contains( "_left" ) ? Hand.Left : Hand.Right;

	static WeaponTag HandWeapon( in Input input, Hand hand ) => hand == Hand.Left ? input.LeftWeapon : input.Weapon;
	static bool HandOverheated( in Input input, Hand hand ) => hand == Hand.Left ? input.LeftOverheated : input.Overheated;

	// One 30 Hz tick: facing, base state, actions, then the pose.
	public void Tick( in Input input ) {

		if ( !_hasPose || FixedFacing ) _bodyYaw = FixedFacing ? 0 : input.Eyes.yaw;
		var (facing, throttle) = FixedFacing ? (0, input.Throttle )
			: input.Throttle != Vector2.Zero && _airborneTicks < AirborneTicks
			? MoveFacing( input.Throttle, input.Eyes )
			: (input.Eyes.yaw, input.Throttle);
		if ( throttle.LengthSquared < ThrottleDeadzone ) throttle = Vector2.Zero;
		var mode = input.Crouching ? "crouch" : "combat";

		_previousBodyYaw = _bodyYaw;
		if ( _hasPose ) Array.Copy( _current, _previous, _current.Length );

		_airborneTicks = input.Airborne ? Math.Min( _airborneTicks + 1, MostAirborneTicks ) : 0;
		if ( _wasAirborne && !input.Airborne ) {
			Land();
			Footstep?.Invoke( MaterialEffectsTag.Land, OriginContact, 1 );
		}
		if ( input.Airborne ) _fallingVelocity = input.Velocity;
		_wasAirborne = input.Airborne;

		var label = FixedFacing ? "idle" : Facing( facing );
		_bodyYaw = Angles.NormalizeAngle( _bodyYaw );
		if ( label == "idle" && throttle != Vector2.Zero ) label = Direction( throttle );

		// A move held through a moment of no throttle.
		if ( label == "idle" && IsMove( _label ) ) {
			if ( _idleCounter * HaloUnits.TickInterval < IdleHold ) label = _label;
			_idleCounter++;
		}
		else _idleCounter = 0;

		if ( _airborneTicks >= AirborneTicks ) label = "airborne";
		else if ( _landingTicks >= 0 && LandingTick( label ) ) label = _landingHard ? "land_hard" : "land_soft";

		WalkWeight( input.Velocity );
		if ( _walk is null || _walkWeight < RunStepsBelow ) _walkSteps = false;
		else if ( _walkWeight > WalkStepsAbove ) _walkSteps = true;
		var ended = Advance( input );
		StopShuffle( throttle, input.Airborne );

		var weaponChanged = Arm( input );
		if ( _label is null ) ChangeBase( mode, label, 0, true );
		else if ( ended || mode != _mode || label != _label || weaponChanged ) {
			// A turn can only be cut short by moving.
			var turning = !ended && _label is "turn_left" or "turn_right";
			if ( !turning || label != "idle" || mode != _mode || weaponChanged )
				ChangeBase( mode, label, IsLanding( label ) && mode == _mode && !weaponChanged ? ShortBlend : LongBlend, ended );
		}

		ChannelTick( ref _action, Hand.Right, input, _wasOverheated );
		ChannelTick( ref _leftAction, Hand.Left, input, _wasLeftOverheated );
		StartPending( input );
		_wasOverheated = input.Overheated;
		_wasLeftOverheated = input.LeftOverheated;

		// The grip's left hand lets go over 0.06675 s during any right hand action but firing, chambering or melee.
		// It takes hold again over 0.267 s. Dual wielding, both hands always grip.
		var holding = _action is null || _action.Label.StartsWith( "fire" ) || _action.Label.StartsWith( "chamber" ) || _action.Label.StartsWith( "melee" );
		_gripFader.Set( holding, holding ? LongBlend : FlashBlend );
		_gripFader.Tick();
		_aimFader.Set( true, AimFadeIn );
		_aimFader.Tick();

		_aimYaw = MathX.DegreeToRadian( Angles.NormalizeAngle( input.Eyes.yaw - _bodyYaw ) );
		_aimPitch = MathX.DegreeToRadian( -input.Eyes.pitch );

		if ( _interpolationCount > 0 && ++_interpolationIndex > _interpolationCount ) _interpolationCount = 0;
		ComputePose();
	}

	// Halo 2 sets the class and type as a hand readies its weapon: the right hand's weapon gives both, or "dual" with the
	// left hand holding one. The left hand's makes the class "dual" and clears its channel; losing it brings back the class
	// from before. Returns whether the class or a hand's type changed.
	bool Arm( in Input input ) {
		var before = (_class, _type, _leftType);
		if ( input.LeftWeapon != _leftWeapon ) {
			if ( _leftWeapon is not null ) {
				SetClass( _classBeforeDual ?? UnitGraph.WeaponOf( input.Weapon ).Class, _type );
				_leftType = _classBeforeDual = null;
				_leftAction = null;
			}
			if ( input.LeftWeapon is not null ) {
				_classBeforeDual = _class;
				SetClass( UnitGraph.DualClass, _type );
				_leftType = UnitGraph.WeaponOf( input.LeftWeapon ).Type;
				// A left ready that arrived before the left weapon did is this readying's own.
				if ( _leftAction?.Label.StartsWith( "ready_left" ) != true ) _leftAction = null;
			}
			_leftWeapon = input.LeftWeapon;
			_wasLeftOverheated = false;
		}
		if ( input.Weapon != _rightWeapon || _class is null ) {
			var (weaponClass, weaponType) = UnitGraph.WeaponOf( input.Weapon );
			SetClass( _leftWeapon is not null ? UnitGraph.DualClass : weaponClass, weaponType );
			_rightWeapon = input.Weapon;
			_wasOverheated = false;
		}
		return before._class is not null && before != (_class, _type, _leftType);
	}

	// A class or type change looks the right hand's action up again.
	void SetClass( string weaponClass, string weaponType ) {
		if ( weaponClass == _class && weaponType == _type ) return;
		var first = _class is null;
		_class = weaponClass;
		_type = weaponType;
		if ( !first ) Relookup( ref _action, _class, _type );
	}

	// A playing action looked up again in a new class, type or mode: the same animation plays on, another starts over,
	// and none ends it.
	void Relookup( ref Channel channel, string weaponClass, string weaponType ) {
		if ( channel is null ) return;
		var head = _graph.Find( _mode, weaponClass, weaponType, channel.Label, true );
		if ( head == channel.Head ) return;
		channel = _graph.Clip( _graph.PickVariant( head ) ) is { } clip ? new Channel { Clip = clip, Head = head, Label = channel.Label, Delay = ActionDelay } : null;
	}

	// Halo 2's player control faces a moving biped along its move, forward or backward, while the aim is near the move's
	// line, else across it on the aim's side. Returns that facing's yaw and the throttle in its axes.
	(float Yaw, Vector2 Throttle) MoveFacing( Vector2 throttle, Angles eyes ) {
		var pitch = MathX.DegreeToRadian( eyes.pitch );
		var move = throttle.Normal;
		var body = MathX.DegreeToRadian( Angles.NormalizeAngle( _bodyYaw - eyes.yaw ) );
		var bodyAlong = MathF.Cos( body ) * move.x + MathF.Sin( body ) * move.y;
		var along = MathF.Cos( pitch ) * move.x;
		var threshold = MathF.Abs( bodyAlong ) < AcrossBodyCosine ? AcrossAlongCosine : AlongCosine;

		Vector2 facing;
		if ( MathF.Abs( along ) > threshold || MathF.Abs( MathF.Sin( pitch ) ) > SteepAimSine )
			facing = along >= 0 ? move : -move;
		else
			facing = move.y <= 0 ? new Vector2( -move.y, move.x ) : new Vector2( move.y, -move.x );

		var local = new Vector2( facing.x * throttle.x + facing.y * throttle.y, facing.x * throttle.y - facing.y * throttle.x );
		if ( local.LengthSquared > 1 ) local = local.Normal;
		return (eyes.yaw + MathX.RadianToDegree( MathF.Atan2( facing.y, facing.x ) ), local);
	}

	// The body's turn toward its facing: moving at the biped's turning speed, standing by turn animations past its
	// threshold. Anything else holds the facing. Returns the turn to play, or idle.
	string Facing( float facing ) {
		var biped = HaloDataStore.SpartanBiped;
		var delta = Angles.NormalizeAngle( facing - _bodyYaw );

		if ( _label is "turn_left" or "turn_right" )
			_bodyYaw += MathX.RadianToDegree( _run.Clip.Yaw( (int)_run.Frame ) * _run.Rate );

		switch ( BaseClass( _label ) ) {
			case 1:
				var step = Or( biped?.MovingTurningSpeed, DefaultMovingTurningSpeed ) * HaloUnits.TickInterval;
				_bodyYaw = MathF.Abs( delta ) <= step ? facing : _bodyYaw + MathF.Sign( delta ) * step;
				return "idle";

			case 0:
				var threshold = MathF.Cos( MathX.DegreeToRadian( Or( biped?.StationaryTurningThreshold, DefaultStationaryTurningThreshold ) ) );
				var cosine = MathF.Cos( MathX.DegreeToRadian( delta ) );
				if ( cosine > threshold + 0.0001f ) return "idle";
				if ( cosine < BehindCosine && _label is "turn_left" or "turn_right" ) return _label;
				return delta < 0 ? "turn_right" : "turn_left";

			default:
				return "idle";
		}
	}

	static float Or( float? value, float fallback ) => value is > 0 ? value.Value : fallback;

	// 0 standing, 1 moving or airborne, 2 anything else.
	static int BaseClass( string label ) => label switch {
		null or "idle" or "turn_left" or "turn_right" => 0,
		"move_front" or "move_back" or "move_left" or "move_right" or "airborne" => 1,
		_ => 2,
	};

	static bool IsMove( string label ) => label is "move_front" or "move_back" or "move_left" or "move_right";

	static bool IsLanding( string label ) => label is "land_soft" or "land_hard";

	// The throttle's larger axis picks the move.
	static string Direction( Vector2 throttle ) => MathF.Abs( throttle.y ) > MathF.Abs( throttle.x )
		? (throttle.y >= 0 ? "move_left" : "move_right")
		: (throttle.x >= 0 ? "move_front" : "move_back");

	static string Walk( string move ) => move switch {
		"move_front" => "walk_front",
		"move_back" => "walk_back",
		"move_left" => "walk_left",
		"move_right" => "walk_right",
		_ => null,
	};

	static string Opposite( string label ) => label switch {
		"move_front" => "move_back",
		"move_back" => "move_front",
		"move_left" => "move_right",
		"move_right" => "move_left",
		_ => null,
	};

	// Touching down at the biped's soft landing speed or more lands, for longer the harder, up to its landing time.
	// Hard landings scale by the whole speed over the hard range; the multiplayer Spartan's hard landing time is 0.
	void Land() {
		var biped = HaloDataStore.SpartanBiped;
		var speed = MathF.Max( 0, -_fallingVelocity.z );
		var soft = biped?.MinimumSoftLandingVelocity ?? DefaultMinimumSoftLandingVelocity;
		var hard = biped?.MinimumHardLandingVelocity ?? DefaultMinimumHardLandingVelocity;
		if ( speed < soft ) return;

		_landingHard = speed >= hard;
		var time = _landingHard
			? (biped?.MaximumHardLandingTime ?? 0) * Math.Clamp( speed / MathF.Max( 0.0001f, (biped?.MaximumHardLandingVelocity ?? 0) - hard ), 0, 1 )
			: Or( biped?.MaximumSoftLandingTime, DefaultMaximumSoftLandingTime ) * Math.Clamp( (speed - soft) / (hard - soft), 0, 1 );
		var ticks = (int)MathF.Round( time * HaloUnits.TicksPerSecond );
		if ( ticks <= 0 ) return;
		_landingTicks = Math.Min( ticks, MostAirborneTicks );
		_landingCount = 0;
	}

	// Whether the landing still shows: it ends with its time, or a soft one half way through once the unit moves.
	bool LandingTick( string label ) {
		_landingCount++;
		if ( _landingCount >= _landingTicks || (!_landingHard && IsMove( label ) && _landingCount >= _landingTicks >> 1) ) {
			_landingTicks = -1;
			return false;
		}
		return true;
	}

	// The walk's share against the run: how far the speed is from the run's root speed toward the walk's.
	// The run slows down to the walk's pace as it fades, and the weight eases a tenth of the way each tick.
	void WalkWeight( Vector3 velocity ) {
		if ( _run is null ) return;
		if ( _walk is null ) {
			_walkWeight = 0;
			return;
		}

		var run = _run.Clip.Speed;
		var walk = _walk.Clip.Speed;
		var target = 0f;
		_run.Rate = 1;
		if ( run - walk > 0 ) {
			var t = Math.Clamp( (velocity.Length - walk) / (run - walk), 0, 1 );
			var frames = _run.Clip.FrameCount / (float)_walk.Clip.FrameCount;
			var rate = Math.Clamp( frames + (1 - frames) * t, 0, 1 );
			_run.Rate = rate > 0.9f ? 1 : rate;
			target = 1 - t;
		}
		_walkWeight = 0.9f * _walkWeight + 0.1f * target;
	}

	// The base animation's frame. A turn or landing that ends hands over to what's wanted (returns true); the rest loop.
	// The foot events it passes play from the run or the walk, whichever has them.
	bool Advance( in Input input ) {
		if ( _run is null ) return false;
		var runFrom = _run.Frame;
		var walkFrom = _walk?.Frame ?? 0;
		_run.Frame += _run.Rate;
		var end = _run.Clip.FrameCount - 0.0001f;
		var ended = false;
		var loop = -1;
		if ( _run.Frame >= end ) {
			ended = _label is "turn_left" or "turn_right" || IsLanding( _label );
			if ( ended ) {
				if ( IsLanding( _label ) ) _landingTicks = -1;
				_run.Frame = end;
			}
			else {
				loop = Math.Clamp( _run.Clip.Animation.LoopFrame, 0, _run.Clip.FrameCount - 1 );
				_run.Frame = loop + (_run.Frame - end);
			}
		}
		if ( _walk is not null ) _walk.Frame = Math.Clamp( _run.Frame / _run.Clip.FrameCount, 0, 1 ) * _walk.Clip.FrameCount;

		if ( !input.Airborne ) {
			if ( _walkSteps ) FootEvents( _walk, walkFrom, loop < 0 ? -1 : 0, input );
			else FootEvents( _run, runFrom, loop, input );
		}
		return ended;
	}

	// The frame events passed from one frame to the channel's, through its loop frame when it wrapped (else -1).
	void FootEvents( Channel channel, float from, int loop, in Input input ) {
		var events = channel.Clip.Animation.FrameEvents;
		if ( events is null ) return;
		var to = channel.Frame;
		foreach ( var e in events ) {
			var passed = loop < 0 ? e.Frame >= from && e.Frame < to : e.Frame >= from || (e.Frame >= loop && e.Frame < to);
			if ( passed ) FootEvent( e.Type, input );
		}
	}

	// A foot event's step plays at the run's share of the run and walk blend, as a walking step under half of it.
	void FootEvent( string type, in Input input ) {
		int? kind;
		int contact;
		switch ( type ) {
			case "left foot": (kind, contact) = (null, 0); break;
			case "right foot": (kind, contact) = (null, 1); break;
			case "both-feet shuffle": (kind, contact) = (MaterialEffectsTag.Shuffle, OriginContact); break;
			case "body impact": (kind, contact) = (MaterialEffectsTag.BodyFall, OriginContact); break;
			default: return;
		}

		var walking = input.Velocity.Length <= WalkingStepSpeed;
		var scale = 1f;
		if ( _walk is not null ) {
			scale = 1 - Math.Clamp( MathF.Min( _walkWeight + 0.0001f, 0.9999f ) / 0.9998f, 0, 1 );
			walking |= scale < WalkingStepScale;
		}
		_stepScale = Math.Clamp( (int)(scale * 127), 0, 127 );
		Footstep?.Invoke( kind ?? (walking ? MaterialEffectsTag.Walk : MaterialEffectsTag.Run), contact, scale );
	}

	// Stopping shuffles the feet together at the last step's scale.
	void StopShuffle( Vector2 throttle, bool airborne ) {
		if ( throttle != Vector2.Zero ) {
			_stillTicks = 0;
			return;
		}
		if ( _stillTicks < 0 || ++_stillTicks < StopShuffleTick ) return;
		_stillTicks = -1;
		if ( !airborne ) Shuffle( _stepScale / 127f );
	}

	void Shuffle( float scale ) {
		Footstep?.Invoke( MaterialEffectsTag.Shuffle, OriginContact, scale );
		_stepScale = 0;
	}

	// A new base state cross-fades in; a move keeps its stride into the same move, mirrors it into the opposite one.
	void ChangeBase( string mode, string label, float blend, bool restart ) {
		if ( _graph.Clip( mode, _class, _type, label, false ) is not { } clip ) {
			// A graph without the label keeps what it plays, as Halo 2 does.
			if ( _run is not null ) return;
			clip = _graph.Clip( mode, _class, _type, "idle", false );
			label = "idle";
			if ( clip is null ) return;
		}

		var frame = 0f;
		if ( !restart && _run is not null && label == _label ) frame = Phase( _run ) * (clip.FrameCount - 0.0001f);
		else if ( !restart && _run is not null && label == Opposite( _label ) ) frame = (1 - Phase( _run )) * (clip.FrameCount - 0.0001f);

		var walk = Walk( label ) is { } walkLabel ? _graph.Clip( mode, _class, _type, walkLabel, false ) : null;
		if ( walk?.Animation != _walk?.Clip.Animation ) _walkWeight = 0;

		// A new mode looks both hands' actions up again.
		var modeChanged = _mode is not null && mode != _mode;
		_mode = mode;
		if ( modeChanged ) {
			Relookup( ref _action, _class, _type );
			Relookup( ref _leftAction, UnitGraph.DualClass, _leftType );
		}
		_label = label;
		_run = new Channel { Clip = clip, Label = label, Frame = frame };
		_walk = walk is null ? null : new Channel { Clip = walk, Label = Walk( label ), Frame = frame / clip.FrameCount * walk.FrameCount };
		StartInterpolation( blend );

		// A turn from its first frame shuffles the feet.
		if ( label is "turn_left" or "turn_right" && frame == 0 ) Shuffle( 1 );

		// The aiming grid for standing or moving, none for anything else; and the weapon's grip.
		// Dual wielding, each hand has its arm offsets and grip from "dual" with its weapon's type.
		var aim = label switch {
			"idle" or "turn_left" or "turn_right" or "airborne" or "land_soft" or "land_hard" => "aim_still",
			"move_front" or "move_back" or "move_left" or "move_right" => "aim_move",
			_ => null,
		};
		_aimUp = aim is null ? null : _graph.Clip( mode, _class, _type, $"{aim}_up", true );
		_aimDown = aim is null ? null : _graph.Clip( mode, _class, _type, $"{aim}_down", true );
		_grip = _graph.Clip( mode, _class, _type, "grip", true );
		var dual = _class == UnitGraph.DualClass;
		_leftGrip = dual ? _graph.Clip( mode, UnitGraph.DualClass, _leftType, "grip", true ) : null;
		_armOffsets = dual ? _graph.Clip( mode, UnitGraph.DualClass, _type, "arm_offsets", true ) : null;
		_leftArmOffsets = dual ? _graph.Clip( mode, UnitGraph.DualClass, _leftType, "arm_offsets", true ) : null;
	}

	static float Phase( Channel channel ) => channel.Frame / (channel.Clip.FrameCount - 0.0001f);

	// A cross-fade from the last pose, unless one with longer to go is under way.
	void StartInterpolation( float seconds ) {
		var ticks = FirstPersonPose.BlendTicks( seconds );
		if ( ticks <= 0 || !_hasPose ) return;
		if ( _interpolationCount > 0 && _interpolationCount - _interpolationIndex > ticks ) return;
		Array.Copy( _stage, _snapshot, _stage.Length );
		_interpolationIndex = 0;
		_interpolationCount = ticks;
	}

	// A hand's action frame and its end: overheating holds the overheated loop until the weapon cools, then its exit plays.
	// Anything else ends and cross-fades back to the base. A charged plasma pistol shot ends in its hand's overheated loop.
	void ChannelTick( ref Channel channel, Hand hand, in Input input, bool wasOverheated ) {
		var suffix = Suffix( hand );
		var cooled = wasOverheated && !HandOverheated( input, hand );
		if ( cooled && (channel is null || channel.Label == "overheating" + suffix || channel.Label == "overheated" + suffix) ) {
			Queue( "o_h_exit" + suffix, FlashBlend, hand );
			return;
		}
		if ( channel is null ) return;
		if ( channel.Delay > 0 ) {
			if ( --channel.Delay == 0 ) ActionFrame?.Invoke( channel.Clip.Animation, 0, Owner( channel.Label ) );
			return;
		}

		var before = (int)channel.Frame;
		channel.Frame += 1;
		var end = channel.Clip.FrameCount - 0.0001f;
		if ( channel.Frame < end ) {
			if ( (int)channel.Frame != before ) ActionFrame?.Invoke( channel.Clip.Animation, (int)channel.Frame, Owner( channel.Label ) );
			return;
		}

		var label = channel.Label;
		var owner = Owner( label );
		var ownerSuffix = Suffix( owner );
		var overheated = HandOverheated( input, owner );
		if ( label == "overheated" + ownerSuffix && overheated ) {
			channel.Frame = Math.Clamp( channel.Clip.Animation.LoopFrame, 0, channel.Clip.FrameCount - 1 );
			return;
		}
		channel = null;
		var plasmaPistolCharge = label == "fire_2" + ownerSuffix && HandWeapon( input, owner )?.WeaponType == "plasma pistol";
		if ( (label == "overheating" + ownerSuffix || plasmaPistolCharge) && overheated ) {
			Queue( "overheated" + ownerSuffix, FlashBlend, owner );
			return;
		}
		StartInterpolation( LongBlend );
	}

	// Each queued action replaces the one on its channel, the same label too, from its first frame after the start delay.
	// The right channel looks it up in the unit's class and type, the left in "dual" with the left weapon's type.
	// While the left hand holds a weapon the right one's ready plays nothing; an overheated weapon readies overheated if it can.
	// A melee strike the body's graph lacks plays the first, else "melee"; any other missing action plays nothing.
	void StartPending( in Input input ) {
		foreach ( var (wanted, blend, hand) in _pending ) {
			var left = hand == Hand.Left;
			var weaponClass = left ? UnitGraph.DualClass : _class;
			var weaponType = left ? _leftType : _type;
			var label = wanted;
			if ( label == "ready" && _leftWeapon is not null ) continue;

			FirstPersonGraph.Animation head = null;
			if ( label.StartsWith( "ready" ) && HandOverheated( input, hand ) ) {
				head = _graph.Find( _mode, weaponClass, weaponType, label + "_overheated", true );
				if ( head is not null ) label += "_overheated";
			}
			head ??= _graph.Find( _mode, weaponClass, weaponType, label, true );
			if ( head is null && label.StartsWith( "melee_strike_" ) ) {
				label = "melee_strike_1";
				head = _graph.Find( _mode, weaponClass, weaponType, label, true ) ?? _graph.Find( _mode, weaponClass, weaponType, "melee", true );
			}
			if ( _graph.Clip( _graph.PickVariant( head ) ) is not { } clip ) continue;
			var channel = new Channel { Clip = clip, Head = head, Label = label, Delay = ActionDelay };
			if ( left ) _leftAction = channel;
			else _action = channel;
			StartInterpolation( blend );
		}
		_pending.Clear();
	}

	// The tick's pose: base, the right then the left hand's action, the arm offsets and cross-fade, then the aiming grid
	// and the grips. Each hand's arm offset and grip moves only its arm while dual wielding.
	void ComputePose() {
		if ( _run is null ) return;
		var bones = _boneNames;
		var run = _run.Clip.Tracks( bones );
		var walk = _walk?.Clip.Tracks( bones );
		var weight = _walk is null ? 0 : Math.Clamp( _walkWeight, 0, 1 );
		var action = _action is { Delay: 0 } ? _action : null;
		var actionTracks = action?.Clip.Tracks( bones );
		var leftAction = _leftAction is { Delay: 0 } ? _leftAction : null;
		var leftActionTracks = leftAction?.Clip.Tracks( bones );
		var armOffsets = _armOffsets?.Tracks( bones );
		var leftArmOffsets = _leftArmOffsets?.Tracks( bones );
		var interpolate = _interpolationCount > 0 && _hasPose;
		var fade = interpolate ? Smoothstep( _interpolationIndex / (float)_interpolationCount ) : 1;

		var aimWeight = _aimFader.Value;
		var aimUp = _aimUp?.Tracks( bones );
		var aimDown = _aimDown?.Tracks( bones );
		var grip = _grip?.Tracks( bones );
		var leftGrip = _leftGrip?.Tracks( bones );
		// A single weapon's grip moves the whole body while the left hand holds on, else only the right arm.
		var wholeGrip = _class != UnitGraph.DualClass && _gripFader.Value >= 0.0001f;

		for ( var i = 0; i < bones.Length; i++ ) {
			var local = run[i] is { } r ? Sample( r, _run.Frame, _run.Clip.FrameCount ) : _bind[i];
			if ( walk?[i] is { } w && weight > 0 ) local = Blend( local, Sample( w, _walk.Frame, _walk.Clip.FrameCount ), weight );

			if ( actionTracks?[i] is { } a ) local = Act( local, action, a, bones[i] );
			if ( leftActionTracks?[i] is { } l ) local = Act( local, leftAction, l, bones[i] );
			if ( armOffsets?[i] is { } ro && _rightArm[i] ) local = Overlay( local, ro, 0, 0, 0, 1 );
			if ( leftArmOffsets?[i] is { } lo && _leftArm[i] ) local = Overlay( local, lo, 0, 0, 0, 1 );

			if ( interpolate ) local = Blend( _snapshot[i], local, fade );
			_stage[i] = local;

			// With both grids the raised one shows in full; the lowered one only before the grid has faded in at all.
			if ( aimUp is not null && aimDown is not null ) {
				if ( aimWeight < 0.0001f ) local = AimingGrid( local, _aimDown, aimDown[i], 1 );
				else local = AimingGrid( local, _aimUp, aimUp[i], 1 );
			}
			else if ( aimUp is not null ) local = AimingGrid( local, _aimUp, aimUp[i], aimWeight );
			else if ( aimDown is not null ) local = AimingGrid( local, _aimDown, aimDown[i], aimWeight );

			if ( grip?[i] is not null && (wholeGrip || _rightArm[i]) ) local = _grip.Frames.Replace( local, bones[i], 0, 0, 0 );
			if ( leftGrip?[i] is not null && _leftArm[i] ) local = _leftGrip.Frames.Replace( local, bones[i], 0, 0, 0 );

			_current[i] = local;
		}

		if ( !_hasPose ) Array.Copy( _current, _previous, _current.Length );
		_hasPose = true;
	}

	// An action's frame over a node: a replacement sets what it stores, an overlay adds to it.
	static Transform Act( Transform local, Channel channel, AnimationClip.Track track, string bone ) {
		var frame = (int)channel.Frame;
		return channel.Clip.Replacement
			? channel.Clip.Frames.Replace( local, bone, frame, frame + 1, channel.Frame - frame )
			: Overlay( local, track, frame, frame + 1, channel.Frame - frame, 1 );
	}

	// The aiming grid's cell for the view: columns step through yaw from full right, rows through pitch from full down.
	// Along a row the two frames are blended, then the two rows. A centred aim at full weight changes nothing.
	Transform AimingGrid( Transform local, UnitClip clip, AnimationClip.Track track, float weight ) {
		if ( track is null || clip.Screen is not { } s || weight < 0.0001f ) return local;
		if ( weight >= 0.9999f && MathF.Abs( _aimYaw ) < 0.0001f && MathF.Abs( _aimPitch ) < 0.0001f ) return local;

		var width = s.RightFrames + s.LeftFrames + 1;
		var rows = s.DownFrames + s.UpFrames;
		var column = Math.Clamp( Steps( _aimYaw, _aimYaw < 0 ? s.RightYawPerFrame : s.LeftYawPerFrame ), -s.RightFrames, s.LeftFrames ) + s.RightFrames;
		var row = Math.Clamp( Steps( _aimPitch, _aimPitch < 0 ? s.DownPitchPerFrame : s.UpPitchPerFrame ), -s.DownFrames, s.UpFrames ) + s.DownFrames;
		var r0 = Math.Clamp( (int)MathF.Floor( row ), 0, rows );
		var r1 = Math.Clamp( (int)MathF.Ceiling( row ), 0, rows );
		var t = row - r0;

		var frames = clip.FrameCount;
		var (rotation, position) = RowSample( track, width * r0 + column, frames );
		if ( t >= 0.0001f && r1 - row >= 0.0001f ) {
			var high = RowSample( track, width * r1 + column, frames );
			rotation = Nlerp( rotation, high.Rotation, t );
			position = position.LerpTo( high.Position, t );
		}
		else if ( t >= 0.0001f ) (rotation, position) = RowSample( track, width * r1 + column, frames );

		return ApplyOverlay( local, rotation, position, weight );
	}

	static float Steps( float angle, float perFrame ) => perFrame < 0.0001f ? 0 : angle / perFrame;

	static (Rotation Rotation, Vector3 Position) RowSample( AnimationClip.Track track, float frame, int frames ) {
		var f0 = Math.Clamp( (int)frame, 0, frames - 1 );
		var f1 = Math.Clamp( f0 + 1, 0, frames - 1 );
		var t = frame - f0;
		return (Nlerp( track.Rotation[f0], track.Rotation[f1], t ), track.Position[f0].LerpTo( track.Position[f1], t ));
	}

	// A base track's pose between two frames.
	static Transform Sample( AnimationClip.Track track, float frame, int frames ) {
		var f0 = Math.Clamp( (int)frame, 0, frames - 1 );
		var f1 = Math.Min( f0 + 1, frames - 1 );
		var t = frame - f0;
		return new Transform( track.Position[f0].LerpTo( track.Position[f1], t ), Nlerp( track.Rotation[f0], track.Rotation[f1], t ) );
	}

	static Transform Overlay( Transform local, AnimationClip.Track track, int f0, int f1, float t, float weight ) {
		var frames = track.Rotation.Length;
		f0 = Math.Clamp( f0, 0, frames - 1 );
		f1 = Math.Clamp( f1, 0, frames - 1 );
		return ApplyOverlay( local, Nlerp( track.Rotation[f0], track.Rotation[f1], t ), track.Position[f0].LerpTo( track.Position[f1], t ), weight );
	}

	// An overlay at a weight: its rotation eased toward its own sign's identity, post-multiplied, and its position added.
	static Transform ApplyOverlay( Transform local, Rotation rotation, Vector3 position, float weight ) {
		if ( weight < 0.9999f ) {
			var sign = rotation.w < 0 ? -1f : 1f;
			rotation = new Rotation( rotation.x * weight, rotation.y * weight, rotation.z * weight, sign + (rotation.w - sign) * weight );
			position *= weight;
		}
		return new Transform( local.Position + position, local.Rotation * rotation, local.Scale );
	}

	static float Smoothstep( float x ) {
		x = Math.Clamp( x, 0, 1 );
		return x * x * (3 - 2 * x);
	}

	static Transform Blend( Transform from, Transform to, float t ) =>
		new( from.Position.LerpTo( to.Position, t ), Nlerp( from.Rotation, to.Rotation, t ), from.Scale.LerpTo( to.Scale, t ) );

	// Halo 2's rotation blend: a lerp along the shorter arc, normalized.
	static Rotation Nlerp( Rotation a, Rotation b, float t ) {
		var u = 1 - t;
		if ( a.x * b.x + a.y * b.y + a.z * b.z + a.w * b.w < 0 ) t = -t;
		var x = a.x * u + b.x * t;
		var y = a.y * u + b.y * t;
		var z = a.z * u + b.z * t;
		var w = a.w * u + b.w * t;
		var length = MathF.Sqrt( x * x + y * y + z * z + w * w );
		return length > 0 ? new Rotation( x / length, y / length, z / length, w / length ) : Rotation.Identity;
	}

	// death

	bool _deathLoops;

	// Each frame a death animation shows, for its frame events.
	public Action<AnimationEntry, int> DeathFrame { get; set; }

	// A dead unit plays its death animation as the whole pose from frame 0: no actions, aiming or grips.
	// It cross-fades from the pose it had over blend seconds, and the facing turns to yaw at once.
	// Only the falling death loops; the rest hold their last frame.
	public void Die( FirstPersonGraph.Animation animation, float yaw, float blend, bool loops ) {
		_pending.Clear();
		_action = _leftAction = null;
		_walk = null;
		_aimUp = _aimDown = _grip = _leftGrip = _armOffsets = _leftArmOffsets = null;
		_bodyYaw = _previousBodyYaw = yaw;
		if ( _graph.Clip( animation ) is not { } clip ) return;
		_label = animation.Name;
		_run = new Channel { Clip = clip, Label = animation.Name };
		_deathLoops = loops;
		StartInterpolation( blend );
		if ( _hasPose ) Array.Copy( _current, _previous, _current.Length );
		ComputePose();
	}

	// One tick of a dead unit's animation.
	public void DeadTick() {
		_previousBodyYaw = _bodyYaw;
		if ( _run is null || !_hasPose ) return;
		Array.Copy( _current, _previous, _current.Length );

		var end = _run.Clip.FrameCount - 1;
		var frame = (int)_run.Frame + 1;
		if ( frame > end ) frame = _deathLoops ? Math.Clamp( _run.Clip.Animation.LoopFrame, 0, end ) : end;
		var moved = frame != (int)_run.Frame;
		_run.Frame = frame;
		if ( moved ) DeathFrame?.Invoke( _run.Clip.Animation, frame );

		if ( _interpolationCount > 0 && ++_interpolationIndex > _interpolationCount ) _interpolationCount = 0;
		ComputePose();
	}

	// This tick's pose from outside, as a ragdoll's: each bone relative to its parent, the root's relative to the renderer.
	// Nothing is sampled meanwhile, and a death animation started next blends from this pose.
	public void SetTickPose( ReadOnlySpan<Transform> pose ) {
		if ( !_hasPose || pose.Length != _current.Length ) return;
		Array.Copy( _current, _previous, _current.Length );
		pose.CopyTo( _current );
		pose.CopyTo( _stage );
		_interpolationCount = 0;
	}

	// The playing death animation's root motion this tick in s&box units, in the body's axes; zero once it holds its end.
	public Vector3 DeathMove {
		get {
			if ( _run is null ) return Vector3.Zero;
			var motion = _run.Clip.Frames.Motion;
			var frame = (int)_run.Frame;
			var held = !_deathLoops && frame >= _run.Clip.FrameCount - 1;
			return held || frame < 0 || frame >= motion.Length ? Vector3.Zero : motion[frame].Move;
		}
	}

	// Ticks the unit has been airborne, up to 127.
	public int TicksAirborne => _airborneTicks;

	// Each bone relative to the model's origin as Draw last posed it, in the model's bone order; empty before a pose.
	public ReadOnlySpan<Transform> DrawnWorld => _hasPose ? _world : default;

	// Poses the bones between the last two ticks; call every frame.
	public void Draw() {
		if ( !_hasPose ) return;
		var fraction = HaloTick.Fraction;
		for ( var i = 0; i < _drawn.Length; i++ ) {
			_drawn[i] = Blend( _previous[i], _current[i], fraction );
			_world[i] = _parents[i] >= 0 ? _world[_parents[i]].ToWorld( _drawn[i] ) : _drawn[i];
		}
	}

	// A model marker's transform in this frame's pose, relative to the renderer.
	public Transform? Marker( string name ) {
		if ( !_hasPose || _model?.Attachments?.Get( name ) is not { Bone: { } bone } attachment ) return null;
		var index = Array.IndexOf( _boneNames, bone.Name );
		return index < 0 ? null : _world[index].ToWorld( attachment.LocalTransform );
	}

	// The body facing this tick, degrees left.
	public float BodyYaw => _bodyYaw;

	// Node 0's position relative to the renderer, in this tick's pose or drawn between ticks; null before the first pose.
	public Vector3? Root( bool drawn ) =>
		!_hasPose ? null : drawn ? _previous[0].Position.LerpTo( _current[0].Position, HaloTick.Fraction ) : _current[0].Position;

	// A marker's offset from node 0 in this tick's pose, in the renderer's axes; null without a pose or the marker.
	public Vector3? MarkerFromRoot( string name ) =>
		_model?.Attachments?.Get( name ) is { Bone: { } bone } attachment ? FromRoot( bone.Name, attachment.LocalTransform.Position ) : null;

	Vector3? FromRoot( string bone, Vector3 local ) => TickNode( bone )?.PointToWorld( local ) - _current[0].Position;

	// A model marker's transform in this tick's pose, relative to the renderer; null without a pose or the marker.
	public Transform? TickMarker( string name ) =>
		_model?.Attachments?.Get( name ) is { Bone: { } bone } attachment ? TickNode( bone.Name )?.ToWorld( attachment.LocalTransform ) : null;

	// A bone relative to the renderer in this tick's pose.
	Transform? TickNode( string bone ) {
		var index = _hasPose ? Array.IndexOf( _boneNames, bone ) : -1;
		if ( index < 0 ) return null;
		var node = _current[index];
		for ( var parent = _parents[index]; parent >= 0; parent = _parents[parent] )
			node = _current[parent].ToWorld( node );
		return node;
	}

	// The bone names, in the model's order.
	public IReadOnlyList<string> BoneNames => _boneNames;

	// Each bone's pose relative to its parent as Draw last set it, the root's relative to the renderer; empty before a pose.
	public ReadOnlySpan<Transform> DrawnPose => _hasPose ? _drawn : default;

	// Each bone relative to the renderer in this tick's pose, in BoneNames' order; false before the first pose.
	public bool TickPose( Transform[] into ) {
		if ( !_hasPose || into.Length < _current.Length ) return false;
		for ( var i = 0; i < _current.Length; i++ )
			into[i] = _parents[i] >= 0 ? into[_parents[i]].ToWorld( _current[i] ) : _current[i];
		return true;
	}
}
