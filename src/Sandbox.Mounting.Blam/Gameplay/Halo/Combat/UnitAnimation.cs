using System;

namespace Halo.Combat;

// Halo's unit animation, posed on the third person body: state animation, then actions and overlays, then aiming.
// Advances one frame per 30 Hz tick; rendering blends the last two ticks' poses.
// Null when the animation graph or the mount's animation export is missing.
public sealed class UnitAnimation {
	// Halo's unit states, whose values matter because Halo compares ranges of them.
	public enum UnitState {
		None = -1,
		Idle = 0,
		Gesture = 1,
		TurnLeft = 2,
		TurnRight = 3,
		MoveFront = 4,
		MoveBack = 5,
		MoveLeft = 6,
		MoveRight = 7,
		StunnedMoveFront = 8,
		StunnedMoveBack = 9,
		StunnedMoveLeft = 10,
		StunnedMoveRight = 11,
		Airborne = 20,
		LandSoft = 21,
		LandHard = 22,
		HardPing = 23,
		DyingAirborne = 24,
		Dying = 25,
		EnteringSeat = 26,
		ExitingSeat = 27,
		UserAnimation = 28,
		AiImpulse = 29,
		MeleeAttack = 30,
		MeleeAirborne = 31,
		MeleeContinuous = 32,
		ThrowGrenade = 33,
		ResurrectFront = 34,
		ResurrectBack = 35,
		LeapStart = 39,
		LeapMelee = 41,
	}

	// Weapon overlay actions; a higher one replaces a lower one.
	public const int PrimaryRecoil = 1, SecondaryRecoil = 2, PrimaryCharged = 3, SecondaryCharged = 4, PrimaryChamber = 5, SecondaryChamber = 6;

	// Weapon actions: reload-1, reload-2 and melee.
	public const int PrimaryReload = 5, SecondaryReload = 6, Melee = 7;

	// Results of advancing an animation one frame.
	const int Running = 0, WillRestart = 2, Restarted = 3, Looped = 4;

	// One tick's input, with Throttle x forward and y left.
	// AimYaw and AimPitch are the view relative to the body's facing, in radians, left and up positive.
	// Seat is the graph's seat label ("W-driver"), null on foot; SeatTransitionTime is seconds into getting in or out.
	public readonly record struct Input( Vector2 Throttle, bool Crouching, bool Airborne, float AimYaw, float AimPitch, string WeaponLabel,
		string Seat = null, UnitState SeatTransition = UnitState.None, float SeatTransitionTime = 0 );

	sealed class Playing {
		public string Name;
		public AnimationClip Clip;
		public int Frame;
		public int SoundFrame = -1;
		public string Sound;

		// Frames where each foot lands, 0 for none.
		public int LeftFootFrame;
		public int RightFootFrame;
	}

	// Effect indices in the biped's footsteps tag.
	public const int FootstepWalk = 0, FootstepRun = 1, FootstepSliding = 2, FootstepShuffle = 3, FootstepJump = 4, FootstepJumpLand = 5;

	readonly SkinnedModelRenderer _renderer;
	// A headless animation's model, posed at Root with no renderer (a seated Sandbox player's Halo pose, retargeted onto their own body).
	readonly Model _model;
	readonly string _graphPath;
	readonly UnitAnimationGraph _graph;
	readonly List<(BoneCollection.Bone Bone, GameObject Object, int Parent)> _bones = new();
	readonly Dictionary<int, int> _boneSlot = new();
	readonly Transform[] _bind;

	UnitAnimationGraph.Seat _seat;
	UnitAnimationGraph.WeaponClass _class;
	UnitAnimationGraph.WeaponType _type;
	string _weaponLabel;

	UnitState _state = UnitState.None;
	Playing _base;
	Playing _action;
	int _actionKind;
	Playing _overlay;
	int _overlayKind;

	// The move playing when the weapon last changed, so the new weapon's move keeps its walk phase.
	(UnitState State, Playing Base) _stride;

	AnimationClip _aim;
	AnimationClip _look;

	// Whether the last pose applied the aiming and looking screens, and the yaw and pitch range each covers in radians.
	// The next tick's aim and look are clipped to these.
	bool _aimingWithScreen;
	float _aimingRight;
	float _aimingLeft;
	float _aimingDown;
	float _aimingUp;
	bool _lookingWithScreen;
	float _lookingRight;
	float _lookingLeft;
	float _lookingDown;
	float _lookingUp;

	Transform[] _pose;
	Transform[] _previous;
	Transform[] _next;
	Transform[] _original;
	Transform[] _drawn;
	Transform[] _world;

	// The renderer's transform the pose was last drawn from.
	Transform _drawnRoot;

	// Whether the last poses already follow the renderer onto or off a seat, until the next tick.
	bool _rootRebased;
	bool _hasPose;
	bool _suspended;
	int _interpolationIndex;
	int _interpolationCount;

	int _landing = -1;
	int _landingCounter;
	int _landingTime;
	int _airborneTicks;

	// Plays an animation's sound tag on its frame.
	public Action<string> PlaySound { get; set; }

	// Plays a footstep effect (FootstepWalk...) at a foot: 0 left, 1 right.
	public Action<int, int> Footstep { get; set; }

	// Ticks since the biped stopped moving, for the shuffle.
	int _stopTicks;

	public UnitState State => _state;

	// Ticks the biped has been in the air, up to 127.
	public int AirborneTicks => _airborneTicks;

	// The pose drawn this frame, parent relative, in the model's bone order (null before the first).
	public Transform[] DrawnPose => _hasPose && !_suspended ? _drawn.ToArray() : null;

	// A headless animation's world transform for the model's origin.
	public Transform Root { get; set; } = global::Transform.Zero;

	// This frame's drawn bones in the world, in the model's bone order; not copied.
	public IReadOnlyList<Transform> WorldPose => _world;

	Model Model => _renderer.IsValid() ? _renderer.Model : _model;
	bool Live => _renderer.IsValid() || _model is not null;
	Transform RootTransform => _renderer.IsValid() ? _renderer.WorldTransform : Root;

	UnitAnimation( SkinnedModelRenderer renderer, Model model, string graphPath, UnitAnimationGraph graph, int boneCount ) {
		_renderer = renderer;
		_model = model;
		_graphPath = graphPath;
		_graph = graph;
		_pose = new Transform[boneCount];
		_previous = new Transform[boneCount];
		_next = new Transform[boneCount];
		_original = new Transform[boneCount];
		_drawn = new Transform[boneCount];
		_world = new Transform[boneCount];
		_bind = new Transform[boneCount];
	}

	public static UnitAnimation Create( SkinnedModelRenderer renderer, string graphPath ) {
		if ( !renderer.IsValid() || renderer.Model is null ) return null;
		var graph = UnitAnimationGraph.For( graphPath );
		if ( graph is null ) return null;

		// Needs the mount's per-animation export.
		if ( AnimationClip.Load( graphPath, graph.FindSeat( "stand" )?.Classes.FirstOrDefault()?.Animation( "idle" ) ) is null ) return null;

		var all = renderer.Model.Bones.AllBones.ToList();
		var animation = new UnitAnimation( renderer, null, graphPath, graph, all.Count );
		renderer.CreateBoneObjects = true;
		for ( var i = 0; i < all.Count; i++ )
			animation._boneSlot[all[i].Index] = i;

		foreach ( var bone in all ) {
			var go = renderer.GetBoneObject( bone.Index );
			if ( !go.IsValid() ) return null;
			var parent = bone.Parent is { } p && animation._boneSlot.TryGetValue( p.Index, out var slot ) ? slot : -1;
			animation._bones.Add( (bone, go, parent) );
		}

		// Parent-relative bind pose, used for nodes no animation drives.
		// Bone.LocalTransform is in model space.
		for ( var i = 0; i < animation._bones.Count; i++ ) {
			var (bone, _, parent) = animation._bones[i];
			animation._bind[i] = parent >= 0 ? animation._bones[parent].Bone.LocalTransform.ToLocal( bone.LocalTransform ) : bone.LocalTransform;
		}

		animation.Resume();
		return animation;
	}

	// Poses the model's bones without a renderer; read them from WorldPose after each Draw.
	public static UnitAnimation CreateHeadless( Model model, string graphPath ) {
		if ( model is null || model.IsError ) return null;
		var graph = UnitAnimationGraph.For( graphPath );
		if ( graph is null ) return null;

		var all = model.Bones.AllBones.ToList();
		var animation = new UnitAnimation( null, model, graphPath, graph, all.Count );
		for ( var i = 0; i < all.Count; i++ )
			animation._boneSlot[all[i].Index] = i;
		foreach ( var bone in all ) {
			var parent = bone.Parent is { } p && animation._boneSlot.TryGetValue( p.Index, out var slot ) ? slot : -1;
			animation._bones.Add( (bone, null, parent) );
		}
		for ( var i = 0; i < animation._bones.Count; i++ ) {
			var (bone, _, parent) = animation._bones[i];
			animation._bind[i] = parent >= 0 ? animation._bones[parent].Bone.LocalTransform.ToLocal( bone.LocalTransform ) : bone.LocalTransform;
		}
		animation.Reset();
		return animation;
	}

	public bool HasSeat( string label ) => _graph.FindSeat( label ) is not null;

	// Hands the bones back to the renderer's own animation, for seats the graph lacks.
	public void Suspend() {
		if ( _suspended ) return;
		_suspended = true;
		foreach ( var b in _bones )
			if ( b.Object.IsValid() ) b.Object.Flags &= ~GameObjectFlags.ProceduralBone;
	}

	// Takes the bones back and starts over.
	public void Resume() {
		if ( !_suspended && _bones.Count > 0 && _bones[0].Object.Flags.HasFlag( GameObjectFlags.ProceduralBone ) ) return;
		_suspended = false;
		foreach ( var b in _bones )
			if ( b.Object.IsValid() ) b.Object.Flags |= GameObjectFlags.ProceduralBone;
		Reset();
	}

	public void Destroy() => Suspend();

	// For a new unit, such as after a respawn: no state, nothing playing.
	public void Reset() {
		_state = UnitState.None;
		_base = null;
		_action = null;
		_actionKind = 0;
		_overlay = null;
		_overlayKind = 0;
		_stride = default;
		_aim = null;
		_look = null;
		_aimingWithScreen = false;
		_lookingWithScreen = false;
		_landing = -1;
		_airborneTicks = 0;
		_stopTicks = 0;
		_interpolationCount = 0;
		_hasPose = false;
		_seat = null;
		_class = null;
		_type = null;
		_weaponLabel = null;
	}

	// Call every fixed update.
	public void Tick( in Input input ) {
		if ( _suspended || !Live ) return;
		Step( input );
	}

	// Poses the bones between the last two ticks; call every frame.
	// A unit with no pose yet gets its first one at once.
	public void Draw( in Input input ) {
		if ( _suspended || !Live ) return;

		// The renderer just moved onto or off a seat, so move the last poses with it before drawing.
		// The next tick may still be frames away.
		var seat = input.Seat is null ? null : _graph.FindSeat( input.Seat );
		if ( !_rootRebased && seat is null && _seat?.Enter is not null ) {
			RebaseRoot( true );
			_rootRebased = true;
		}
		else if ( !_rootRebased && seat is not null && seat != _seat && seat.Enter is not null && input.SeatTransition == UnitState.EnteringSeat ) {
			RebaseRoot( false );
			_rootRebased = true;
		}

		if ( !_hasPose ) Step( input );
		Pose( HaloTick.Fraction );
	}

	// Events

	// Plays the trigger's fire overlay.
	public void Fired( int trigger ) => StartOverlayAction( trigger == 0 ? PrimaryRecoil : SecondaryRecoil );

	// Plays a weapon overlay unless a higher one is playing or the unit is busy.
	public void StartOverlayAction( int overlayAction ) {
		if ( overlayAction < _overlayKind ) return;
		if ( _state is UnitState.HardPing or UnitState.DyingAirborne or UnitState.Dying or UnitState.EnteringSeat or UnitState.ExitingSeat
			or UnitState.AiImpulse or UnitState.MeleeAttack or UnitState.MeleeAirborne or UnitState.MeleeContinuous or UnitState.ThrowGrenade
			or UnitState.ResurrectFront or UnitState.ResurrectBack or UnitState.LeapStart or UnitState.LeapMelee )
			return;

		var slot = overlayAction switch {
			PrimaryRecoil => "fire-1",
			SecondaryRecoil => "fire-2",
			PrimaryCharged => "charged-1",
			SecondaryCharged => "charged-2",
			PrimaryChamber => "chamber-1",
			SecondaryChamber => "chamber-2",
			_ => null,
		};
		var playing = Play( _type?.Animation( slot ) );
		if ( playing is null ) return;

		_overlay = playing;
		_overlayKind = overlayAction;
	}

	// Plays the weapon's reload or melee.
	// A melee snaps in; a reload blends in over 6 ticks.
	public void StartAction( int action ) {
		var slot = action switch {
			PrimaryReload => "reload-1",
			SecondaryReload => "reload-2",
			Melee => "melee",
			_ => null,
		};
		var playing = Play( _type?.Animation( slot ) );
		if ( playing is null ) return;

		var interpolation = action == Melee ? 0 : 6;
		if ( interpolation > 0 ) StartInterpolation( interpolation );
		_action = playing;
		_actionKind = action;
	}

	// Drops any action and plays the throw-grenade state, unless the unit is busy.
	public void ThrowGrenade() {
		if ( _state is UnitState.HardPing or UnitState.DyingAirborne or UnitState.Dying or UnitState.EnteringSeat or UnitState.ExitingSeat
			or UnitState.AiImpulse or UnitState.MeleeAttack or UnitState.MeleeAirborne or UnitState.MeleeContinuous or UnitState.ThrowGrenade
			or UnitState.ResurrectFront or UnitState.ResurrectBack or UnitState.LeapStart or UnitState.LeapMelee )
			return;

		_actionKind = 0;
		_action = null;
		SetState( UnitState.ThrowGrenade );
	}

	// Starts a soft or hard landing lasting recoveryTicks.
	public void Landed( bool hard, int recoveryTicks ) {
		_landing = hard ? 1 : 0;
		_landingCounter = 0;
		_landingTime = recoveryTicks;
	}

	// The tick

	void Step( in Input input ) {
		_rootRebased = false;

		// The pose blend steps before the unit updates, as in Halo.
		if ( _interpolationCount > 0 && ++_interpolationIndex >= _interpolationCount )
			_interpolationCount = 0;

		// A weapon switch below clears the state, so footsteps use the state from before it.
		var footstepState = _state;

		var label = string.IsNullOrEmpty( input.WeaponLabel ) ? "unarmed" : input.WeaponLabel;
		var vehicleSeat = input.Seat is null ? null : _graph.FindSeat( input.Seat );
		if ( vehicleSeat is not null && _seat != vehicleSeat ) {
			// Entering a seat: use the weapon's class in the seat, else the seat's default.
			_weaponLabel = label;
			if ( !SetSeatAndWeapon( vehicleSeat.Label, label ) )
				SetSeatAndWeapon( vehicleSeat.Label, null );
			StopActions();
		}
		else if ( vehicleSeat is null && _seat?.Enter is not null ) {
			// Out of the seat: standing again.
			_weaponLabel = label;
			if ( !SetSeatAndWeapon( "stand", label ) )
				SetSeatAndWeapon( "stand", "unarmed" );
			StopActions();
		}
		else if ( _class is null || label != _weaponLabel ) {
			// A new weapon restarts the state with a 6 tick blend.
			// Halo restarts the walk cycle at frame 0; here the new move keeps the old one's phase.
			_weaponLabel = label;
			var stride = IsMove( _state ) ? (_state, _base) : default;
			if ( !SetSeatAndWeapon( _seat?.Label ?? "stand", label ) && _class is null )
				SetSeatAndWeapon( "stand", "unarmed" );
			_stride = stride;
		}

		// A seated unit has no throttle and never leaves the ground.
		var desired = vehicleSeat is null ? DesiredState( input ) : UnitState.Idle;

		if ( vehicleSeat is null ) {
			// Footsteps come before the animation advances.
			TryToMakeFootsteps( footstepState, input.Throttle );

			// Crouching switches to the crouch seat when the state allows it.
			var seat = input.Crouching ? "crouch" : "stand";
			if ( !string.Equals( _seat?.Label, seat, StringComparison.OrdinalIgnoreCase ) && Interruptable( desired ) )
				SetSeatAndWeapon( seat, _weaponLabel );
		}

		// The enter or exit animation follows the host's time into the transition.
		var transition = vehicleSeat is not null && input.SeatTransition is UnitState.EnteringSeat or UnitState.ExitingSeat
			&& PlaySeatTransition( input.SeatTransition, input.SeatTransitionTime );

		// The exit holds its last frame until the unit is actually out of the seat.
		if ( !transition && vehicleSeat is not null && _state == UnitState.ExitingSeat && _base is not null ) {
			_base.Frame = _base.Clip.FrameCount - 1;
			transition = true;
		}

		var apply = false;
		if ( !transition && _base is not null && Advance( _base ) == WillRestart && !StateLoops() )
			apply = true;

		if ( _action is not null && Advance( _action ) == WillRestart ) {
			StartInterpolation( 6 );
			_action = null;
			_actionKind = 0;
		}

		if ( _overlay is not null ) {
			var result = Advance( _overlay );
			if ( (result == WillRestart || result == Looped) && !OverlayActionLoops() ) {
				_overlay = null;
				_overlayKind = 0;
			}
		}

		// Seated once the enter animation is over.
		var entered = _state == UnitState.EnteringSeat;
		if ( !transition && (apply || entered || desired != _state && Interruptable( desired )) )
			SetState( desired );
		_stride = default;

		ComputePose( input.AimYaw, input.AimPitch );
	}

	// Drops everything playing, for a new seat.
	void StopActions() {
		_base = null;
		_action = null;
		_actionKind = 0;
		_overlay = null;
		_overlayKind = 0;
	}

	// Plays the seat's enter or exit animation at the host's time into it, held on the last frame.
	// Entering blends in over 6 ticks from where the unit stood.
	// False, changing nothing, if the seat has no such animation.
	bool PlaySeatTransition( UnitState state, float time ) {
		if ( _state != state ) {
			var playing = Play( state == UnitState.EnteringSeat ? _seat?.Enter : _seat?.Exit );
			if ( playing is null ) return false;
			if ( state == UnitState.EnteringSeat ) StartInterpolation( 6 );
			_base = playing;
			_state = state;
			_look = AnimationClip.Load( _graphPath, _seat.Look );
		}

		_base.Frame = Math.Clamp( (int)(time * HaloUnits.TicksPerSecond), 0, _base.Clip.FrameCount - 1 );
		return true;
	}

	// Idle, or a move along the throttle's bigger axis (front/back on a tie, none while landing hard).
	// Airborne after more than 3 ticks in the air, or a landing while it lasts.
	UnitState DesiredState( in Input input ) {
		var throttle = input.Throttle.LengthSquared < 0.1f * 0.1f ? Vector2.Zero : input.Throttle;

		if ( input.Airborne ) { if ( _airborneTicks < 127 ) _airborneTicks++; }
		else _airborneTicks = 0;

		var desired = UnitState.Idle;
		if ( _landing != 1 && throttle != Vector2.Zero ) {
			desired = MathF.Abs( throttle.x ) >= MathF.Abs( throttle.y )
				? throttle.x >= 0 ? UnitState.MoveFront : UnitState.MoveBack
				: throttle.y >= 0 ? UnitState.MoveLeft : UnitState.MoveRight;
		}

		if ( input.Airborne ) {
			if ( _state == UnitState.Airborne || _airborneTicks > 3 )
				desired = UnitState.Airborne;
		}
		else if ( _landing != -1 ) {
			if ( ++_landingCounter >= _landingTime )
				_landing = -1;

			// Both feet play the jump land effect on the second tick, or at once for a shorter landing.
			if ( _landingCounter == 2 || (_landing == -1 && _landingTime < 2) ) {
				Footstep?.Invoke( FootstepJumpLand, 0 );
				Footstep?.Invoke( FootstepJumpLand, 1 );
			}

			// Halo tests the landing after clearing it: the last tick of a hard landing asks for soft.
			desired = _landing == 1 ? UnitState.LandHard : UnitState.LandSoft;
		}

		return desired;
	}

	// Uses the state and frame from before this tick's update.
	// A turn's first frame shuffles both feet, and so does the third idle tick after moving.
	// While moving with throttle past 0.5, each foot steps on its foot frame.
	void TryToMakeFootsteps( UnitState state, Vector2 throttle ) {
		// Halo zeroes a throttle under 0.1.
		if ( throttle.LengthSquared < 0.1f * 0.1f ) throttle = Vector2.Zero;

		var airContact = state is UnitState.TurnLeft or UnitState.TurnRight;
		var moving = IsMove( state ) && throttle.LengthSquared > 0.25f;

		if ( _base is { } animation ) {
			if ( airContact ) {
				if ( animation.Frame == 0 ) {
					Footstep?.Invoke( FootstepShuffle, 0 );
					Footstep?.Invoke( FootstepShuffle, 1 );
				}
			}
			else if ( moving && (animation.LeftFootFrame != 0 || animation.RightFootFrame != 0) ) {
				// Run standing, walk crouched.
				var effect = string.Equals( _seat?.Label, "stand", StringComparison.OrdinalIgnoreCase ) ? FootstepRun : FootstepWalk;
				if ( animation.Frame == animation.LeftFootFrame )
					Footstep?.Invoke( effect, 0 );
				else if ( animation.Frame == animation.RightFootFrame )
					Footstep?.Invoke( effect, 1 );
			}
		}

		// Counts idle ticks after moving, for the stop shuffle.
		if ( state is UnitState.Idle or UnitState.TurnLeft or UnitState.TurnRight ) {
			if ( _stopTicks > 0 && ++_stopTicks > 3 ) {
				Footstep?.Invoke( FootstepShuffle, 0 );
				Footstep?.Invoke( FootstepShuffle, 1 );
				_stopTicks = 0;
			}
		}
		else if ( IsMove( state ) ) {
			_stopTicks = 1;
		}
		else {
			_stopTicks = 0;
		}
	}

	// Sets the seat and the weapon's class and type in it, and clears the state.
	// False, changing nothing, if the seat has no type for the weapon.
	bool SetSeatAndWeapon( string seatLabel, string weaponLabel ) {
		var seat = _graph.FindSeat( seatLabel );
		if ( !UnitAnimationGraph.Find( seat, weaponLabel, out var weaponClass, out var weaponType ) ) return false;

		_seat = seat;
		_class = weaponClass;
		_type = weaponType;
		_state = UnitState.None;
		return true;
	}

	bool SetState( UnitState state ) {
		var wasNone = _state == UnitState.None;
		var changed = false;
		var interpolation = 0;

		if ( wasNone || state != _state ) {
			var name = _class?.Animation( StateSlot( state ) );
			var playing = Play( name );
			if ( playing is null && !CanEnterWithoutAnimation( state ) )
				return false;

			if ( wasNone && playing is not null && state == _stride.State && _stride.Base is { } old && old.Clip.FrameCount > 0 )
				playing.Frame = Math.Min( old.Frame * playing.Clip.FrameCount / old.Clip.FrameCount, playing.Clip.FrameCount - 1 );

			_base = playing;
			interpolation = InterpolationFrames( state, _state );
			changed = true;
		}

		var aim = AimSlot( state );
		if ( wasNone || aim != AimSlot( _state ) ) {
			_aim = aim is null ? null : AnimationClip.Load( _graphPath, _class?.Animation( aim ) );
			if ( wasNone ) _look = AnimationClip.Load( _graphPath, _seat?.Look );
			interpolation = 6;
			changed = true;
		}

		if ( changed ) StartInterpolation( interpolation );
		_state = state;
		return true;
	}

	Playing Play( string name ) {
		name = _graph.ChoosePermutation( name );
		var clip = AnimationClip.Load( _graphPath, name );
		if ( clip is null ) return null;

		var playing = new Playing { Name = name, Clip = clip };
		if ( AnimationGraphs.Find( _graphPath, name ) is { } timing ) {
			playing.LeftFootFrame = timing.LeftFootFrame;
			playing.RightFootFrame = timing.RightFootFrame;
		}
		if ( AnimationSounds.For( _graphPath, name ) is { } cue ) {
			playing.Sound = cue.Sound;
			playing.SoundFrame = (int)MathF.Round( cue.Time * HaloUnits.TicksPerSecond );
		}
		return playing;
	}

	// Plays the frame's sound, then steps one frame.
	// Past the end it goes to the loop frame, or restarts on a new random permutation if there is none.
	int Advance( Playing playing ) {
		if ( playing.Sound is not null && playing.SoundFrame == playing.Frame )
			PlaySound?.Invoke( playing.Sound );

		var count = playing.Clip.FrameCount;
		var loop = playing.Clip.LoopFrame;
		playing.Frame++;

		if ( playing.Frame >= count ) {
			if ( loop > 0 ) {
				playing.Frame = Math.Min( loop, count - 1 );
				return Looped;
			}

			var restarted = Play( PermutationParent( playing.Name ) );
			if ( restarted is not null ) {
				playing.Name = restarted.Name;
				playing.Clip = restarted.Clip;
				playing.Sound = restarted.Sound;
				playing.SoundFrame = restarted.SoundFrame;
				playing.LeftFootFrame = restarted.LeftFootFrame;
				playing.RightFootFrame = restarted.RightFootFrame;
			}
			playing.Frame = 0;
			return Restarted;
		}

		if ( playing.Frame + 1 == count && loop == 0 )
			return WillRestart;
		return Running;
	}

	// The first permutation ("%1") of a permuted animation, else the name itself.
	string PermutationParent( string name ) {
		foreach ( var (parent, chain) in _graph.Permutations )
			if ( chain.Any( c => c.Name == name ) ) return parent;
		return name;
	}

	// Blends from the current pose over this many ticks.
	// A shorter blend doesn't cut short one already under way.
	void StartInterpolation( int frames ) {
		if ( !_hasPose ) return;
		Array.Copy( _pose, _original, _pose.Length );
		if ( frames >= _interpolationCount - _interpolationIndex ) {
			_interpolationIndex = 0;
			_interpolationCount = frames;
		}
	}

	// Moves the stored poses' root nodes to the renderer's new transform after entering or leaving a seat.
	// Entering offsets the root but keeps its orientation.
	// Leaving keeps the root where it is in the world.
	void RebaseRoot( bool rotate ) {
		if ( !_hasPose ) return;
		var from = _drawnRoot;
		var to = RootTransform;
		var offset = to.PointToLocal( from.Position );

		Transform Move( Transform node ) => rotate ? to.ToLocal( from.ToWorld( node ) ) : node.WithPosition( node.Position + offset );

		for ( var i = 0; i < _bones.Count; i++ ) {
			if ( _bones[i].Parent >= 0 ) continue;
			_pose[i] = Move( _pose[i] );
			_previous[i] = Move( _previous[i] );
			if ( _interpolationCount > 0 ) _original[i] = Move( _original[i] );
		}
	}

	static string StateSlot( UnitState state ) => state switch {
		UnitState.Idle => "idle",
		UnitState.Gesture => "gesture",
		UnitState.TurnLeft => "turn-left",
		UnitState.TurnRight => "turn-right",
		UnitState.MoveFront => "move-front",
		UnitState.MoveBack => "move-back",
		UnitState.MoveLeft => "move-left",
		UnitState.MoveRight => "move-right",
		UnitState.Airborne => "airborne",
		UnitState.LandSoft => "land-soft",
		UnitState.LandHard => "land-hard",
		UnitState.ThrowGrenade => "throw-grenade",
		UnitState.MeleeAttack => "melee",
		_ => null,
	};

	static string AimSlot( UnitState state ) => state switch {
		UnitState.Idle or UnitState.TurnLeft or UnitState.TurnRight or UnitState.Airborne or UnitState.LandSoft or UnitState.LandHard => "aim-still",
		UnitState.MoveFront or UnitState.MoveBack or UnitState.MoveLeft or UnitState.MoveRight => "aim-move",
		_ => null,
	};

	// Blend ticks for a state change: 1 between still states, 2 into a landing, else 6.
	static int InterpolationFrames( UnitState state, UnitState old ) {
		static bool Still( UnitState s ) => s is UnitState.Idle or UnitState.TurnLeft or UnitState.TurnRight;
		var frames = Still( state ) && Still( old ) ? 1 : 6;
		if ( state is UnitState.LandHard or UnitState.LandSoft ) frames = 2;
		return frames;
	}

	static bool IsMove( UnitState state ) =>
		state is UnitState.MoveFront or UnitState.MoveBack or UnitState.MoveLeft or UnitState.MoveRight;

	static bool CanEnterWithoutAnimation( UnitState state ) =>
		state is not (UnitState.MeleeAttack or UnitState.MeleeAirborne or UnitState.MeleeContinuous or UnitState.ThrowGrenade or UnitState.LeapStart or UnitState.LeapMelee);

	// Covers the states a player's biped can get into.
	bool Interruptable( UnitState desired ) {
		switch ( _state ) {
			case UnitState.AiImpulse:
			case UnitState.MeleeAttack:
			case UnitState.MeleeAirborne:
			case UnitState.ThrowGrenade:
			case UnitState.ResurrectFront:
			case UnitState.ResurrectBack:
			case UnitState.LeapStart:
			case UnitState.LeapMelee:
				return desired == UnitState.HardPing;

			case UnitState.TurnLeft:
			case UnitState.TurnRight:
				return desired != UnitState.Idle;

			case UnitState.HardPing:
			case UnitState.EnteringSeat:
			case UnitState.ExitingSeat:
			case UnitState.UserAnimation:
				return false;

			default:
				return true;
		}
	}

	bool StateLoops() => _state is not (UnitState.Gesture or UnitState.TurnLeft or UnitState.TurnRight or UnitState.HardPing
		or UnitState.EnteringSeat or UnitState.ExitingSeat or UnitState.UserAnimation or UnitState.AiImpulse or UnitState.MeleeAttack
		or UnitState.MeleeAirborne or UnitState.ThrowGrenade or UnitState.ResurrectFront or UnitState.ResurrectBack
		or UnitState.LeapStart or UnitState.LeapMelee);

	// Halo keeps an overlay going past its end while the state is turn-right or move-front.
	bool OverlayActionLoops() => _state is >= UnitState.TurnRight and <= UnitState.MoveFront;

	// No aiming or looking while an action plays or the unit is pinged, dying or meleeing.
	bool AimingScreen() => _actionKind == 0 && !(_state >= UnitState.HardPing && (_state <= UnitState.ResurrectBack || _state == UnitState.LeapMelee));

	// The pose

	// State frame, then action replacement, overlay, aiming screen and looking screen, then the blend.
	void ComputePose( float aimYaw, float aimPitch ) {
		var aiming = AimingScreen();
		var fraction = _interpolationCount > 0 ? (_interpolationIndex + 1) / (float)_interpolationCount : 1;

		// A player aims and looks along the view, clipped to the screens the last pose used.
		// The looking screen allows 70 degrees of yaw and 45 of pitch.
		// MCC leaves a player's aim unclipped on foot, where the body faces the view anyway.
		var lookYaw = _lookingWithScreen ? Math.Clamp( aimYaw, _lookingRight, _lookingLeft ) : aimYaw;
		var lookPitch = _lookingWithScreen ? Math.Clamp( aimPitch, _lookingDown, _lookingUp ) : aimPitch;

		// In a vehicle seat the aim is clipped to the class's aiming screen.
		if ( _aimingWithScreen && _seat?.Enter is not null ) {
			aimYaw = Math.Clamp( aimYaw, _aimingRight, _aimingLeft );
			aimPitch = Math.Clamp( aimPitch, _aimingDown, _aimingUp );
		}
		var aimingScreen = aiming && _aim is not null && _class is not null;
		var lookingScreen = aiming && _look is not null && _seat is not null;

		// The looking screen takes the look relative to the aim.
		// Past 45 degrees the clipped look lags the aim, so the head turns back: the "Red vs. Blue" nod.
		// Seated, the aiming screen turns the torso toward the view and the looking screen turns the head from there.
		var lookingYaw = lookYaw - (aimingScreen ? aimYaw : 0);
		var lookingPitch = lookPitch - (aimingScreen ? aimPitch : 0);

		for ( var i = 0; i < _bones.Count; i++ ) {
			var name = _bones[i].Bone.Name;

			// The base animation's frame, else the bind pose.
			var local = _base is { } b && b.Clip.Tracks.TryGetValue( name, out var track ) && !b.Clip.IsOverlay && !b.Clip.IsReplacement
				? new Transform( track.Position[b.Frame], track.Rotation[b.Frame], track.Scale[b.Frame] )
				: _bind[i];

			if ( _action is { } a && a.Clip.IsReplacement )
				local = Replace( local, a.Clip, name, a.Frame );

			if ( _overlay is { } o && o.Clip.IsOverlay )
				local = Overlay( local, o.Clip, name, o.Frame );

			if ( aimingScreen )
				local = ApplyAimingScreen( local, _aim, name, _class.AimingScreen, aimYaw, aimPitch );
			if ( lookingScreen )
				local = ApplyAimingScreen( local, _look, name, _seat.LookingScreen, lookingYaw, lookingPitch );

			if ( _interpolationCount > 0 && _hasPose )
				local = Blend( _original[i], local, fraction );

			_next[i] = local;
		}

		// Remember which screens this pose used and their bounds, for next tick's clipping.
		_aimingWithScreen = aimingScreen;
		if ( aimingScreen ) {
			var bounds = _class.AimingScreen;
			_aimingRight = -(bounds.NegativeYawFrames * bounds.NegativeYawDelta);
			_aimingLeft = bounds.PositiveYawFrames * bounds.PositiveYawDelta;
			_aimingDown = -(bounds.NegativePitchFrames * bounds.NegativePitchDelta);
			_aimingUp = bounds.PositivePitchFrames * bounds.PositivePitchDelta;
		}

		_lookingWithScreen = lookingScreen;
		if ( lookingScreen ) {
			var bounds = _seat.LookingScreen;
			_lookingRight = -(bounds.NegativeYawFrames * bounds.NegativeYawDelta);
			_lookingLeft = bounds.PositiveYawFrames * bounds.PositiveYawDelta;
			_lookingDown = -(bounds.NegativePitchFrames * bounds.NegativePitchDelta);
			_lookingUp = bounds.PositivePitchFrames * bounds.PositivePitchDelta;
		}

		(_previous, _pose, _next) = (_pose, _next, _previous);
		if ( !_hasPose ) Array.Copy( _pose, _previous, _pose.Length );
		_hasPose = true;
	}

	// Replaces only the components the animation animates.
	internal static Transform Replace( Transform local, AnimationClip clip, string bone, int frame ) {
		if ( frame < 0 || frame >= clip.FrameCount || !clip.Tracks.TryGetValue( bone, out var track ) ) return local;
		return new Transform(
			clip.PositionAnimated.Contains( bone ) ? track.Position[frame] : local.Position,
			clip.RotationAnimated.Contains( bone ) ? track.Rotation[frame] : local.Rotation,
			clip.ScaleAnimated.Contains( bone ) ? Vector3.One * track.Scale[frame] : local.Scale );
	}

	// Rotation post-multiplied, position added, scale multiplied.
	// A scale below 1 applies that share of each.
	internal static Transform Overlay( Transform local, AnimationClip clip, string bone, int frame, float scale = 1 ) {
		if ( frame < 0 || frame >= clip.FrameCount || !clip.Tracks.TryGetValue( bone, out var track ) ) return local;
		var rotation = scale >= 1 ? track.Rotation[frame] : Rotation.Slerp( Rotation.Identity, track.Rotation[frame], scale );
		return new Transform(
			local.Position + track.Position[frame] * scale,
			local.Rotation * rotation,
			local.Scale * (track.Scale[frame] * scale + (1 - scale)) );
	}

	// The overlay's frames form a grid, yaw across and pitch down.
	// The four frames around the angle are blended bilinearly and applied as an overlay, rotation and position only.
	internal static Transform ApplyAimingScreen( Transform local, AnimationClip clip, string bone, UnitAnimationGraph.AimingScreen bounds, float yaw, float pitch ) {
		var width = bounds.NegativeYawFrames + bounds.PositiveYawFrames + 1;
		var height = bounds.NegativePitchFrames + bounds.PositivePitchFrames + 1;
		if ( !clip.IsOverlay || clip.FrameCount < width * height ) return local;
		if ( !clip.Tracks.TryGetValue( bone, out var track ) ) return local;

		var (yawIndex, yawFraction) = Cell( yaw, bounds.NegativeYawDelta, bounds.PositiveYawDelta, bounds.NegativeYawFrames, bounds.PositiveYawFrames );
		var (pitchIndex, pitchFraction) = Cell( pitch, bounds.NegativePitchDelta, bounds.PositivePitchDelta, bounds.NegativePitchFrames, bounds.PositivePitchFrames );
		if ( pitchIndex < 0 || pitchIndex >= height || yawIndex < 0 || yawIndex >= width ) return local;

		var nextYaw = yawIndex + 1 == width ? yawIndex : yawIndex + 1;
		var nextPitch = pitchIndex + 1 == height ? pitchIndex : pitchIndex + 1;
		var f00 = yawIndex + pitchIndex * width;
		var f10 = nextYaw + pitchIndex * width;
		var f01 = yawIndex + nextPitch * width;
		var f11 = nextYaw + nextPitch * width;

		var rotation = Nlerp( Nlerp( track.Rotation[f00], track.Rotation[f10], yawFraction ), Nlerp( track.Rotation[f01], track.Rotation[f11], yawFraction ), pitchFraction );
		var position = (track.Position[f01] * (1 - yawFraction) + track.Position[f11] * yawFraction) * pitchFraction
			+ (track.Position[f00] * (1 - yawFraction) + track.Position[f10] * yawFraction) * (1 - pitchFraction);

		return new Transform( local.Position + position, local.Rotation * rotation, local.Scale );
	}

	// The grid cell along one axis: frame index from the negative edge, and fraction toward the next.
	static (int Index, float Fraction) Cell( float angle, float negativeDelta, float positiveDelta, int negativeFrames, int positiveFrames ) {
		var delta = angle < 0 ? negativeDelta : positiveDelta;
		var frame = delta == 0 ? 0 : angle / delta;
		var index = (int)frame;
		var fraction = frame % 1f;
		if ( fraction < 0 ) {
			fraction += 1;
			index--;
		}
		if ( index >= positiveFrames ) {
			index = positiveFrames - 1;
			fraction = 1;
		}
		if ( index < -negativeFrames ) {
			index = -negativeFrames;
			fraction = 0;
		}
		return (index + negativeFrames, fraction);
	}

	internal static Transform Blend( Transform from, Transform to, float t ) => new(
		from.Position * (1 - t) + to.Position * t,
		Nlerp( from.Rotation, to.Rotation, t ),
		from.Scale * (1 - t) + to.Scale * t );

	// Normalized lerp along the shorter arc, as Halo blends rotations.
	internal static Rotation Nlerp( Rotation a, Rotation b, float t ) {
		var u = 1 - t;
		if ( a.x * b.x + a.y * b.y + a.z * b.z + a.w * b.w < 0 ) t = -t;
		var x = a.x * u + b.x * t;
		var y = a.y * u + b.y * t;
		var z = a.z * u + b.z * t;
		var w = a.w * u + b.w * t;
		var length = MathF.Sqrt( x * x + y * y + z * z + w * w );
		return length > 0 ? new Rotation( x / length, y / length, z / length, w / length ) : Rotation.Identity;
	}

	void Pose( float alpha ) {
		var root = RootTransform;
		_drawnRoot = root;

		for ( var i = 0; i < _bones.Count; i++ ) {
			var (_, go, parent) = _bones[i];
			_drawn[i] = Blend( _previous[i], _pose[i], alpha );
			_world[i] = (parent >= 0 ? _world[parent] : root).ToWorld( _drawn[i] );
			if ( go is not null ) go.WorldTransform = _world[i];
		}
	}

	// A model marker's world transform in this frame's drawn pose.
	public Transform? Marker( string name ) {
		if ( !_hasPose || _suspended ) return null;
		var attachment = Model?.Attachments?.Get( name );
		if ( attachment?.Bone is null || !_boneSlot.TryGetValue( attachment.Bone.Index, out var slot ) ) return null;
		return _world[slot].ToWorld( attachment.LocalTransform );
	}

	// A model marker's world transform in the last tick's pose, not the blended one drawn this frame.
	public Transform? TickMarker( string name ) {
		if ( !_hasPose || _suspended ) return null;
		var attachment = Model?.Attachments?.Get( name );
		if ( attachment?.Bone is null || !_boneSlot.TryGetValue( attachment.Bone.Index, out var slot ) ) return null;

		var node = _pose[slot];
		for ( var parent = _bones[slot].Parent; parent >= 0; parent = _bones[parent].Parent )
			node = _pose[parent].ToWorld( node );
		return RootTransform.ToWorld( node ).ToWorld( attachment.LocalTransform );
	}
}
