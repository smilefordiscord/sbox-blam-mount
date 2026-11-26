using System;
using Halo;
using Halo.Combat;
using Halo.Data;

namespace HaloWeapons;

// The local player's Halo first person weapon: the mount's "<weapon tag>_fp.vmdl" (hands and weapon on one skeleton) posed at the camera,
// its first person animations stepped a frame a Halo tick by Halo's state rules, with Halo's sway and pose interpolation (ViewPose).
// From halo1sbox's SpartanView; HaloWeapon tells it what the weapon does.
public sealed class HaloViewModel {
	readonly HaloWeapon _weapon;
	GameObject _viewModel;
	SkinnedModelRenderer _renderer;
	ViewPose _pose;
	ObjectAttachments _attachments;
	readonly FirstPersonSway _sway = new();
	readonly AnimationSoundPlayer _sound = new();

	string _slot;
	string _animation;
	int _frame;
	int _frames = 1;
	int _drawFrom = -1;
	long _startTick;
	bool _looping;
	bool _blend;
	bool _hold;
	string _rest;
	bool _shotgunEmpty;
	string _queuedExit;
	int _unposedFrames = -1;
	bool _posedLastFrame;
	int _idleTicks;
	int _ticksUntilPose;

	public HaloViewModel( HaloWeapon weapon ) {
		_weapon = weapon;
		Build();
	}

	public bool IsValid => _viewModel.IsValid();

	// The mount's "<weapon tag>_fp.vmdl".
	public static string ModelPath( string weaponPath ) {
		if ( string.IsNullOrEmpty( weaponPath ) ) return null;
		var path = weaponPath.Replace( '\\', '/' );
		var dot = path.LastIndexOf( '.' );
		if ( dot > path.LastIndexOf( '/' ) ) path = path[..dot];
		return $"mount://haloce/{path}_fp.vmdl";
	}

	void Build() {
		var tag = _weapon.Tag;
		var path = ModelPath( _weapon.WeaponTag );
		var model = path is null ? null : Model.Load( path );
		if ( tag is null || model is null || model.IsError || model.AnimationCount == 0 ) return;

		_viewModel = _weapon.Scene.CreateObject( false );
		_viewModel.Name = "halo view model";
		_viewModel.Flags |= GameObjectFlags.NotSaved | GameObjectFlags.NotNetworked;
		_viewModel.Tags.Add( "viewmodel", "firstperson" );
		_renderer = _viewModel.AddComponent<SkinnedModelRenderer>();
		_renderer.UseAnimGraph = false;
		_renderer.Model = model;
		_renderer.PlaybackRate = 0;
		_renderer.RenderType = ModelRenderer.ShadowRenderType.Off;
		_renderer.CreateAttachments = true;
		HaloResources.SetMountedModelWithGlass( _renderer, path );
		_viewModel.Enabled = true;

		_pose = ViewPose.Create( _renderer, tag.FirstPersonAnimations );
		_attachments = ObjectAttachments.Create( _weapon.WeaponTag, _renderer, firstPerson: true, ViewModelToWorld, _weapon.Owner?.GameObject );
		_sway.WeaponReadied();
		if ( !SetState( true, "ready" ) ) Play( true, "idle" );
		_pose?.CancelInterpolation();
		_unposedFrames = 0;
		_posedLastFrame = false;
		SetRendering( false );
		DrawOverWorld();
	}

	public void Destroy() {
		_sound.PutAway();
		_attachments?.Destroy();
		_attachments = null;
		if ( _viewModel.IsValid() ) _viewModel.Destroy();
		_viewModel = null;
		HaloView.LocalMuzzle = null;
		HaloView.LocalViewModelShown = false;
	}

	// The weapon draws in the overlay layer so it never clips into walls, as Halo's own depth range does.
	void DrawOverWorld() {
		foreach ( var renderer in _viewModel.GetComponentsInChildren<ModelRenderer>( true ) ) {
			renderer.RenderOptions.Game = false;
			renderer.RenderOptions.Overlay = true;
			renderer.Attributes.Set( "HaloSunShadows", 0f );
			if ( renderer != _renderer ) renderer.Attributes.Set( "HaloOverlayOnly", 1f );
		}
	}

	void SetRendering( bool render ) {
		foreach ( var renderer in _viewModel.GetComponentsInChildren<ModelRenderer>( true ) )
			if ( renderer.SceneObject.IsValid() ) renderer.SceneObject.RenderingEnabled = render;
	}

	// Events from the weapon, in Halo's state rules.

	public void Fired( int trigger ) {
		if ( FireCanInterrupt( _slot ) )
			Play( true, trigger == 0 ? "fire-1" : "fire-2", "firing", "fire-1" );
		if ( trigger == 0 ) _sway.Fired();
		_idleTicks = 0;
	}

	public void ReloadStarted( bool empty ) {
		if ( _weapon.ReloadsByShell ) {
			_shotgunEmpty = empty;
			Play( true, "enter", "reload-empty", "reload-full" );
		}
		else Play( true, empty ? "reload-empty" : "reload-full", "reload-full", "reload-empty" );
	}

	public void ReloadShell() => Play( true, "reload-empty", "reload-full" );

	public void ReloadFinished() {
		if ( !_weapon.ReloadsByShell ) return;
		var exit = _shotgunEmpty ? "exit-empty" : "exit-full";
		if ( _animation is not null && !_looping && !AtEnd ) _queuedExit = exit;
		else Play( false, exit, "exit-full", "idle" );
	}

	public void Meleed() => Play( true, "melee" );

	public void Threw() => SetState( true, "throw-grenade" );

	public void Overheated( bool superRecoil ) => SetState( true, superRecoil ? "o-h-s-enter" : "overheating" );

	// Overheated weapons use their overheated ready and throw; fire only cuts in where allowed.
	bool SetState( bool resetSounds, string slot ) {
		var overheated = _weapon.Overheated;
		if ( overheated ) {
			slot = slot switch {
				"ready" => "overheating-again",
				"throw-grenade" => "throw-overheated",
				_ => slot,
			};
		}

		if ( slot is "fire-1" or "fire-2" or "misfire-1" or "misfire-2" && !FireCanInterrupt( _slot ) ) return false;
		if ( slot == "overheated" && !overheated && _weapon.Tag?.WeaponType == "plasma pistol" ) slot = "idle";
		return Play( resetSounds, slot );
	}

	static bool FireCanInterrupt( string slot ) => slot is null or "idle" or "moving" or "posing" or "ready" or "melee" or "fire-1" or "firing"
		or "overcharged" or "overcharged-jitter" or "enter" or "o-h-exit" or "exit-empty" or "exit-full" or "reload-empty" or "reload-full";

	bool AtEnd => _hold || _frame + 1 >= _frames;

	// Halo's blend ticks into a state.
	static int InterpolationTicks( string from, string to ) {
		if ( from == "exit-empty" ) return 0;
		if ( from == "moving" ) return 6;
		return to switch {
			"melee" or "ready" or "overheated" or "moving" => 0,
			"fire-1" or "fire-2" or "misfire-1" or "misfire-2" => 3,
			_ => 6,
		};
	}

	string RestingSlot => _weapon.Overcharged ? "overcharged-jitter"
		: _pose is not { HasMoving: true } && _weapon.OwnerMoving ? "moving"
		: "idle";

	void PlayResting( bool resetSounds = false ) {
		var rest = RestingSlot;
		Play( resetSounds, rest, "idle" );
		_rest = rest;
	}

	// Plays the first "first-person <slot>" animation the model has. Loops repeat; one shots move on to the next state.
	bool Play( bool resetSounds, params string[] slots ) {
		if ( !_renderer.IsValid() ) return false;

		var available = _renderer.Model.AnimationNames;
		foreach ( var slot in slots ) {
			var name = $"first-person {slot}";
			var match = available.FirstOrDefault( x => x == name ) ?? available.FirstOrDefault( x => x.StartsWith( name + "%" ) );
			if ( match is null ) continue;

			var looping = slot is "moving" or "overcharged-jitter" or "overheated";
			if ( !slot.StartsWith( "exit" ) && !looping ) _queuedExit = null;

			var ticks = InterpolationTicks( _slot, slot );
			_blend = _pose is null && ticks > 0;
			_pose?.StartInterpolation( ticks );
			if ( slot == "ready" ) _pose?.CancelInterpolation();
			_slot = slot;
			_rest = slot is "idle" or "moving" or "overcharged-jitter" ? slot : null;
			_hold = false;

			_animation = match;
			_frame = 0;
			_frames = FrameCount( match );
			_drawFrom = -1;
			_startTick = HaloTick.Count;
			_looping = looping;

			if ( resetSounds ) _sound.Stop();
			_sound.Started( _weapon.Tag?.FirstPersonAnimations, match, local: true, source: _weapon );
			Draw();
			return true;
		}
		return false;
	}

	int FrameCount( string animation ) {
		if ( AnimationGraphs.Find( _weapon.Tag?.FirstPersonAnimations, animation ) is { FrameCount: > 0 } timing )
			return timing.FrameCount;
		if ( !_renderer.IsValid() || !_renderer.SceneModel.IsValid() ) return 1;
		_renderer.Sequence.Name = animation;
		return Math.Max( 1, (int)MathF.Round( _renderer.Sequence.Duration * HaloUnits.TicksPerSecond ) + 1 );
	}

	void NextState() {
		_hold = false;
		if ( _slot is "overheating" or "o-h-s-enter" or "overheating-again" or "throw-overheated" ) {
			SetState( false, "overheated" );
			return;
		}

		// Between shells Halo goes back to idle and the next shell's reload blends out of that, so a shell left in the gun
		// never slides straight back to the hand through it; only the last shell goes on to the exit.
		if ( _slot is "enter" or "reload-empty" or "reload-full" && _weapon.ReloadsByShell && _queuedExit is { } exit ) {
			_queuedExit = null;
			Play( false, exit, "exit-full", "idle" );
			return;
		}

		PlayResting();
	}

	// One Halo tick: overheat state, the state animation, moving overlay, sway, pose interpolation, and the idle pose timer.
	public void Tick() {
		if ( !_renderer.IsValid() || _animation is null ) return;

		if ( _slot is "overheated" or "overheating" ) {
			if ( _weapon.OverheatedExit ) SetState( true, "o-h-exit" );
			if ( !_weapon.Overheated ) SetState( true, "idle" );
		}

		if ( _startTick != HaloTick.Count ) {
			if ( _hold ) NextState();
			else {
				_sound.Update( _frame * HaloUnits.TickInterval, _weapon.EyePosition, audible: !HaloView.ThirdPerson );
				_drawFrom = _frame;
				if ( ++_frame >= _frames ) {
					_frame = 0;
					_drawFrom = -1;
				}
				else if ( _frame + 1 == _frames && !_looping ) NextState();
			}
		}

		var throttle = Input.AnalogMove;
		_pose?.TickMoving( new Vector2( throttle.x, throttle.y ).Length > 0.1f && _weapon.OwnerGrounded, _slot == "idle" );
		if ( _rest is not null && _rest != RestingSlot ) PlayResting();

		_sway.Tick( new Vector2( throttle.x, throttle.y ), _weapon.EyeAngles );
		_pose?.TickInterpolation();
		TickIdle();
	}

	// After a random 10-15 s idle the weapon plays its "posing" animation; zooming, moving or turning resets the count.
	void TickIdle() {
		var idle = _slot is "idle" or "moving";
		if ( _weapon.ZoomLevel >= 0 || !_sway.Settled || !idle ) {
			_idleTicks = 0;
			if ( _slot == "posing" && (_weapon.ZoomLevel >= 0 || !_sway.Settled) ) {
				_sound.Stop();
				PlayResting();
			}
			return;
		}

		var player = HaloDataStore.Game?.Globals?.Player;
		var bounds = player?.FirstPersonIdleTime;
		var lower = bounds is { Length: > 1 } ? bounds[0] : 10;
		var upper = bounds is { Length: > 1 } ? bounds[1] : 15;
		var skip = bounds is { Length: > 1 } ? player.FirstPersonSkipFraction : 0.25f;
		if ( _ticksUntilPose == 0 ) _ticksUntilPose = (int)(Game.Random.Float( lower, upper ) * HaloUnits.TicksPerSecond);

		if ( ++_idleTicks <= _ticksUntilPose ) return;
		_ticksUntilPose = 0;
		if ( Game.Random.Float() < skip ) return;
		if ( !_renderer.Model.AnimationNames.Any( x => x == "first-person posing" || x.StartsWith( "first-person posing%" ) ) ) return;
		_sound.Stop();
		Play( false, "posing" );
	}

	// Each frame, after the camera: posed with the camera as origin as in Halo, hidden while a scope zooms.
	public void Place( in CameraView view, bool visible ) {
		if ( !_viewModel.IsValid() ) return;

		// A scope hides it without disabling it: it keeps animating and posing unseen, as Halo's does,
		// so unscoping shows it where it has got to. Re-enabling the object would build a new scene model that drew a frame unposed.
		var show = visible && (_weapon.ZoomLevel < 0 || (_weapon.Tag?.ZoomLevels ?? 0) == 0);
		_attachments?.SetVisible( show );
		_shown = show;

		_viewModel.WorldPosition = view.Position;
		_viewModel.WorldRotation = view.Rotation;
		_viewModel.WorldScale = new Vector3( 1, Spread, Spread );
		Draw();

		var muzzle = _renderer.GetAttachmentObject( "primary trigger" );
		HaloView.LocalMuzzle = show && muzzle.IsValid() ? muzzle.WorldTransform : null;
		HaloView.LocalViewModelShown = show;
		var outputs = _weapon.FunctionOutputs;
		WeaponFunctions.Apply( _renderer, outputs );
		_attachments?.Update( outputs );

		if ( _pose is not null ) {
			_pose.Apply( _sway, AmmunitionFrame );
			if ( _pose.Failed ) _pose = null;
		}
		Reveal();
	}

	bool _shown = true;

	// Halo reloads the needler's needles into view partway through its reload: 44 ticks in, the "ammunition" overlay
	// counts up from the rounds loaded to those the reload brings (loaded plus reserve, up to a full magazine) over 5 ticks.
	// Every other weapon's overlay frame is just the rounds loaded.
	const int ReloadSettleTicks = 44;

	int AmmunitionFrame {
		get {
			var loaded = _weapon.Loaded;
			if ( _weapon.Tag?.WeaponType != "needler" || _slot is not ("reload-empty" or "reload-full") || _frame < ReloadSettleTicks ) return loaded;
			var full = Math.Min( loaded + Math.Max( 0, _weapon.Reserve ), _weapon.Tag.Magazine( 0 )?.RoundsLoadedMaximum ?? loaded );
			var settle = MathF.Min( 1, (_frame - ReloadSettleTicks) * 0.2f );
			return loaded + (int)((full - loaded) * settle);
		}
	}

	// A new view model would show its bind pose for a frame; it stays invisible until posed, and while a scope hides it.
	void Reveal() {
		if ( _unposedFrames >= 0 ) {
			if ( !_posedLastFrame && ++_unposedFrames < 30 ) {
				_posedLastFrame = _pose is not null
					? _pose.Primed
					: _renderer.Model.Bones.AllBones.FirstOrDefault() is not { } root || _renderer.TryGetBoneTransformAnimation( root, out _ );
				SetRendering( false );
				return;
			}
			_unposedFrames = -1;
		}
		SetRendering( _shown );
	}

	// The sequence time from the tick's frame, interpolated between ticks; the renderer doesn't play it itself.
	void Draw() {
		if ( _animation is null || !_renderer.IsValid() || !_renderer.Active || !_renderer.SceneModel.IsValid() ) return;
		var sequence = _renderer.Sequence;
		if ( sequence.Name != _animation ) {
			sequence.Blending = _blend;
			sequence.Looping = false;
			sequence.Name = _animation;
		}

		var frame = _drawFrom < 0 ? _frame : _drawFrom + (_frame - _drawFrom) * HaloTick.Fraction;
		sequence.Time = MathF.Min( frame * HaloUnits.TickInterval, sequence.Duration );
	}

	// MCC's first person field of view (its slider's 86, horizontal at 16:9), which the weapon keeps whatever the camera's.
	public const float FieldOfView = 86;

	// The camera-space spread that makes the weapon look as at FieldOfView: the ratio of the two vertical FOV tangents.
	static float Spread {
		get {
			var camera = Game.ActiveScene?.Camera;
			if ( camera is null ) return 1;
			var vertical = camera.FovAxis == CameraComponent.Axis.Vertical ? camera.FieldOfView
				: 2 * MathF.Atan( MathF.Tan( camera.FieldOfView.DegreeToRadian() / 2 ) * Screen.Height / MathF.Max( 1, Screen.Width ) ).RadianToDegree();
			var mcc = 2 * MathF.Atan( MathF.Tan( FieldOfView.DegreeToRadian() / 2 ) * (9f / 16f) );
			var k = MathF.Tan( vertical.DegreeToRadian() / 2 ) / MathF.Tan( mcc / 2 );
			return float.IsFinite( k ) && k > 0 ? k : 1;
		}
	}

	// A view model marker as the scene shows it; the spread moves positions but not directions, so spread those here.
	static Transform ViewModelToWorld( Transform t ) {
		var camera = Game.ActiveScene?.Camera;
		if ( camera is null ) return t;
		var k = Spread;
		var view = camera.WorldRotation;
		Vector3 Spreads( Vector3 v ) => view * new Vector3( v.x, v.y * k, v.z * k );
		var forward = Spreads( view.Inverse * t.Rotation.Forward );
		var up = Spreads( view.Inverse * t.Rotation.Up );
		return new Transform( t.Position, Rotation.LookAt( forward.Normal, up.Normal ), t.Scale.x );
	}
}
