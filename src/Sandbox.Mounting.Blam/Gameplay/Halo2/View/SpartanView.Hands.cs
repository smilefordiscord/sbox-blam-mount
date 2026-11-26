using System;
using Halo2.Data;

namespace Halo2.Combat;

public sealed partial class SpartanView {
	// One hand's first person state, as Halo 2 keeps one per hand: that hand's weapon's own first person model, graph,
	// state animation and pose. The hands model is drawn once from both: everything from the right hand's state but
	// the left arm, which comes from the left's. Here the right view model draws the hands and the left draws only its gun.
	// The left hand's gun is the left weapon's own model, moved onto the left hand.
	sealed class HandView {
		readonly SpartanView _owner;
		readonly Hand _hand;

		public HandView( SpartanView owner, Hand hand ) {
			_owner = owner;
			_hand = hand;
		}

		IHalo2Hands Spartan => _owner.Spartan;

		GameObject _viewModel;
		SkinnedModelRenderer _renderer;
#pragma warning disable CS0414
		Model _normalModel;
#pragma warning restore CS0414
		ObjectAttachments _attachments;
		FirstPersonPose _pose;
		SkinnedModelRenderer _shieldShell;
		SkinnedModelRenderer _overshieldShell;

		// The weapon this view was built for.
		string _weapon;

		public bool Valid => _viewModel.IsValid();
		public bool Shown => _viewModel.IsValid() && _viewModel.Enabled;
		public bool Animating => _animation is not null;
		public bool DisablesOverlays => _animation?.DisablesOverlays ?? false;
		public FirstPersonPose Pose => _pose;

		public void Destroy() {
			_attachments?.Destroy();
			_attachments = null;
			_pose?.Destroy();
			_pose = null;
			if ( _viewModel.IsValid() ) _viewModel.Destroy();
			_viewModel = null;
			_renderer = null;
		}

		// The view model is set up for the hand's weapon when it changes, and afresh for a ready even of the same weapon.
		// A rebuild sets the state up again at idle without blending, in the class the hands now use.
		public void Sync( IHalo2Hands weapons, bool rebuild, bool fresh ) {
			var path = weapons.HandPath( _hand );
			if ( fresh || path != _weapon ) Build( path, weapons.HandTag( _hand ), weapons.DualWielding );
			else if ( rebuild && _renderer.IsValid() ) Restart( weapons.DualWielding );
		}

		// visible is false in third person, where the hidden weapon sits at the camera.
		public void Update( Transform camera, bool visible ) {
			var weapons = Spartan;
			if ( weapons.HandPath( _hand ) != _weapon ) Sync( weapons, false, false );
			if ( !_viewModel.IsValid() ) {
				Local( _hand ) = default;
				return;
			}

			// Halo 2 hides the first person weapons while zoomed, by a scope or the binoculars.
			var tag = weapons.HandTag( _hand );
			Show( visible && Spartan.ZoomLevel < 0 );

			Place( camera, tag );
			UpdateMuzzle( visible );
			var functions = WeaponFunctions.For( Spartan, _hand );
			_attachments?.Update( functions );
			ShaderFunctions.Apply( _viewModel, ViewModelPath( _weapon ), functions );
			_owner.SetShields( _shieldShell, _overshieldShell );
			DrawAnimation();
		}

		// Halo 2 puts the model's origin at the camera, moved by the weapon's first person offset in camera axes.
		// MCC's own offset moves the left hand's the other way across.
		// The offset is part of the drawn weapon, so it spreads with it.
		void Place( Transform camera, WeaponTag tag ) {
			var k = ViewModelSpread;
			_offset = WeaponOffset( tag ) + (_hand == Hand.Left ? ViewModelOffset.WithY( -ViewModelOffset.y ) : ViewModelOffset);
			var rotation = camera.Rotation;
			_viewModel.WorldPosition = camera.Position + rotation * new Vector3( _offset.x, _offset.y * k, _offset.z * k );
			_viewModel.WorldRotation = rotation;
		}

		// The weapon tag's first person offset in inches: forward, left and up.
		static Vector3 WeaponOffset( WeaponTag tag ) =>
			tag?.FirstPersonWeaponOffset is { Length: 3 } o ? HaloUnits.ToInches( 1 ) * new Vector3( o[0], o[1], o[2] ) : Vector3.Zero;

		// The view model's origin from the camera before the spread, in camera axes.
		Vector3 _offset;

		// After the camera's final move and zoom: the spread, the pose's layers and the reveal.
		// The weapon uses the camera's rotation, so its axes are camera space.
		// The right hand's hands take their left arm from the left hand's view, posed before it.
		public void PreRender( float k, HandView leftArm ) {
			if ( !Shown ) return;
			_viewModel.WorldScale = new Vector3( 1, k, k );
			var arm = leftArm is { Shown: true } ? leftArm : null;
			_pose?.Apply( _owner.Layers( _hand, _shownRounds ), arm?._pose, arm is null ? default : arm._offset - _offset );
			SpreadMarkers();
			LocalViewEffects( _hand ).Follow();
			UpdateReveal();
		}

		// visible is false in third person.
		void UpdateMuzzle( bool visible ) {
			var muzzle = visible && _renderer.IsValid() ? _renderer.GetAttachmentObject( "primary_trigger" ) : null;
			var shown = muzzle.IsValid() && _viewModel.Enabled;
			Local( _hand ) = new LocalHand(
				shown ? muzzle.WorldTransform : null,
				!muzzle.IsValid() ? null : shown ? ViewModelToWorld( muzzle.WorldTransform ) : muzzle.WorldTransform,
				shown ? muzzle : null,
				visible && _renderer.IsValid() ? _renderer : null,
				_viewModel.Enabled );
		}

		// Sets the sequence time from the tick's frame, interpolated between ticks; the renderer doesn't play it itself.
		// Halo 2's own blend replaces the engine's crossfade.
		void DrawAnimation() {
			if ( _animation is null || !_renderer.IsValid() || !_renderer.Active || !_renderer.SceneModel.IsValid() ) return;
			var sequence = _renderer.Sequence;
			if ( sequence.Name != _animation.Name ) {
				sequence.Blending = false;
				sequence.Looping = false;
				sequence.Name = _animation.Name;
			}

			var frame = _drawFrom < 0 ? _frame : _drawFrom + (_frame - _drawFrom) * HaloTick.Fraction;
			sequence.Time = MathF.Min( frame * HaloUnits.TickInterval, sequence.Duration );
		}

		// Building

		void Build( string path, WeaponTag tag, bool dual ) {
			Destroy();
			_weapon = path;
			_normalModel = null;
			_shieldShell = _overshieldShell = null;
			_graph = null;
			_animation = null;
			_state = null;
			_idle.Reset();
			if ( tag is null ) return;

			// A weapon with no first person model or graph draws nothing in first person, as in Halo.
			var viewModelPath = ViewModelPath( path );
			var graph = FirstPersonGraph.For( path );
			var fpModel = graph is null ? null : Model.Load( viewModelPath );
			if ( fpModel is null || fpModel.IsError || fpModel.AnimationCount == 0 ) return;

			_viewModel = Spartan.Scene.CreateObject( true );
			_viewModel.Name = _hand == Hand.Left ? "halo 2 left view model" : "halo 2 view model";
			_viewModel.Flags |= GameObjectFlags.NotSaved | GameObjectFlags.NotNetworked;
			_viewModel.Tags.Add( "viewmodel", "firstperson" );
			// At the camera from the start, so effects spawned on it now (its attachments, a ready's) don't start at the origin.
			if ( Spartan.Scene.Camera is { } camera ) Place( camera.WorldTransform, tag );
			_renderer = _viewModel.AddComponent<SkinnedModelRenderer>();
			_renderer.UseAnimGraph = false;
			_renderer.Model = fpModel;
			// The tick steps its animations.
			_renderer.PlaybackRate = 0;
			// Glass parts, such as the needler's needles, if the mount split them out.
			HaloResources.SetMountedModelWithGlass( _renderer, viewModelPath );
			_renderer.RenderType = ModelRenderer.ShadowRenderType.Off;
			_renderer.CreateAttachments = true;
			_graph = graph;
			_shieldShell = CreateShell( _renderer, fpModel, graph.ShieldMaterial );
			_overshieldShell = CreateShell( _renderer, fpModel, graph.OvershieldMaterial );
			// The weapon's effects, lens flares and looping sounds.
			_attachments = ObjectAttachments.Create( path, _renderer, firstPerson: true );

			_shownRounds = Spartan.HandState( _hand ).Loaded;
			_roundsRamp = 0;
			Restart( dual );

			_unposedFrames = 0;
			_posedLastFrame = false;
			SetRendering( false );
			DrawOverWorld();

			// The ready that rebuilt it spawns its effect on it straight away.
			UpdateMuzzle( !ThirdPerson );
		}

		// The state starts over at idle in the hands' class, with a new pose and no blend.
		void Restart( bool dual ) {
			_pose?.Destroy();
			_pose = FirstPersonPose.Create( _renderer, _graph, dual, _hand == Hand.Left );
			_dual = dual;
			ShowArms();
			_overcharged = false;
			_exitSent = false;
			_fireHold = 0;
			_state = null;
			SetState( "idle" );
		}

		// The hands' "left" and "right" regions are body groups: the left hand's view model draws only its gun.
		void ShowArms() {
			var arms = _hand == Hand.Left ? 1 : 0;
			foreach ( var renderer in new[] { _renderer, _shieldShell, _overshieldShell } ) {
				if ( !renderer.IsValid() ) continue;
				HaloResources.SetBodyGroup( renderer, "left", arms );
				HaloResources.SetBodyGroup( renderer, "right", arms );
			}

			// The shield effect is the unit's, on its first person arms; Halo 2 never draws it on the weapon.
			foreach ( var shell in new[] { _shieldShell, _overshieldShell } )
				if ( shell.IsValid() ) HaloResources.HideBodyGroupsExcept( shell, "left", "right" );
		}

		// The weapon draws in the overlay layer so it never clips into walls, as Halo's own depth range does.
		// Its lens flares stay in the world's layers under it: Halo 2 draws them just before the weapon, which covers them.
		// s&box draws an overlay renderer's translucent parts twice, so the mount's shaders blend each draw by half (HaloOverlay).
		void DrawOverWorld() {
			foreach ( var renderer in _viewModel.GetComponentsInChildren<ModelRenderer>( true ) ) {
				if ( renderer.Model?.ResourcePath?.EndsWith( "lens_flare.vmdl" ) ?? false ) continue;
				renderer.RenderOptions.Game = false;
				renderer.RenderOptions.Overlay = true;
				renderer.Attributes.Set( "HaloOverlay", 1f );
			}
		}

		// Showing

		// The GPU stretches the posed model across the camera whole, but marker objects take the spread bone by bone.
		// So each marker goes where its point of the posed model is drawn, even on bones turned from the camera's axes.
		void SpreadMarkers() {
			if ( !_renderer.IsValid() || _renderer.Model is null || _pose is null ) return;

			var drawn = _viewModel.WorldTransform;
			foreach ( var attachment in _renderer.Model.Attachments.All ) {
				if ( attachment.Bone is null || _pose.ModelBone( attachment.Bone.Name ) is not { } bone ) continue;
				var marker = _renderer.GetAttachmentObject( attachment );
				if ( marker.IsValid() ) marker.WorldPosition = drawn.PointToWorld( bone.ToWorld( attachment.LocalTransform ).Position );
			}
		}

		int _unposedFrames = -1;
		bool _posedLastFrame;

		// A new view model would show its bind pose for a frame, since the first frame's pose draws on the second.
		// It stays invisible until posed.
		void UpdateReveal() {
			if ( _unposedFrames < 0 || !_renderer.IsValid() ) return;

			// Never leave it hidden if the animation can't be read back.
			if ( !_posedLastFrame && ++_unposedFrames < 30 ) {
				_posedLastFrame = _renderer.Model.Bones.AllBones.FirstOrDefault() is not { } root || _renderer.TryGetBoneTransformAnimation( root, out _ );
				SetRendering( false );
				return;
			}

			_unposedFrames = -1;
			SetRendering( true );
		}

		void SetRendering( bool render ) {
			foreach ( var renderer in _viewModel.GetComponentsInChildren<ModelRenderer>( true ) )
				if ( renderer.SceneObject.IsValid() ) renderer.SceneObject.RenderingEnabled = render;
		}

		// Hidden weapons keep animating as in Halo, so unscoping mid-draw shows the draw where it has got to.
		public void Show( bool show ) {
			if ( !_viewModel.IsValid() ) return;

			// The attachments ride the view model's markers, which stay enabled with it.
			_attachments?.SetVisible( show );
			if ( _viewModel.Enabled == show ) return;

			if ( !show ) LocalViewEffects( _hand ).Drop();
			_viewModel.Enabled = show;
			if ( !show ) _pose?.Hidden();

			// Shown again, it stays invisible until posed, like a new view model.
			if ( show && _renderer.IsValid() ) {
				_unposedFrames = 0;
				_posedLastFrame = false;
				SetRendering( false );
			}
		}

		// States

		// Halo 2's first person weapon state is the label it plays; the weapon's and unit's messages set it.
		FirstPersonGraph _graph;
		string _state;
		FirstPersonGraph.Animation _animation;

		// The hands use weapon class "dual" while dual wielding.
		bool _dual;

		// The state animation's frame, stepped once a tick, and the previous one for drawing between ticks (-1 on a start).
		int _frame;
		int _drawFrom = -1;
		long _stateTick;

		// The last frame whose events played, so a held frame plays them once.
		int _passedFrame = -1;

		// A shotgun shell's animation, or the put away, waits on its last frame.
		bool _hold;

		// A weapon flagged to loop its firing animation keeps looping it while a shot came in the last 2 ticks.
		// Halo 2 sets 4 of MCC's 60 Hz ticks on each fire_1, counts it down every tick, and reads it when fire ends.
		const int FireHoldTicks = 2;
		int _fireHold;

		bool _overcharged;
		bool _exitSent;

		// Rounds the needler's "ammunition" overlay shows, and the ticks into easing up to a reload's new count.
		float _shownRounds;
		int _roundsRamp;
		const int RoundsRampTicks = 5;

		readonly FirstPersonIdle _idle = new();

		public void Message( string label ) {
			if ( label == "fire_1" ) _fireHold = FireHoldTicks;
			SetState( label );
		}

		bool LoopsFiring => Spartan?.HandTag( _hand )?.HasFlag( "loop fp firing animation" ) ?? false;

		// Seconds Halo 2 blends into a state from the pose the last one left.
		static float BlendSeconds( string state ) => state switch {
			"fire_1" or "fire_2" or "misfire_1" or "misfire_2" or "ready" or "ready_overheated" or "overheated" or "reload_exit" => 0,
			"reload_empty" or "reload_full" or "reload_enter" => FirstPersonPose.ShortBlend,
			_ when state.StartsWith( "melee_strike_" ) => FirstPersonPose.ShortBlend,
			_ => FirstPersonPose.LongBlend,
		};

		// Any state interrupts any other, except that a ready doesn't restart itself and a looping fire carries on.
		// A label no weapon class has plays idle.
		void SetState( string label ) {
			if ( _graph?.Play( label, _dual ) is not { } head ) return;
			if ( label == _state && label is "ready" or "ready_overheated" ) return;
			if ( label == "fire_1" && _state == "fire_1" && LoopsFiring ) return;
			if ( _graph.Head( label, _dual ) is null ) label = "idle";

			// Melee strikes and readies also drop a blend under way.
			if ( label is "ready" or "ready_overheated" || label.StartsWith( "melee_strike_" ) ) _pose?.CancelInterpolation();
			var blend = FirstPersonPose.BlendTicks( BlendSeconds( label ) );
			_pose?.StartInterpolation( blend );
			if ( blend == 0 ) _pose?.Jumped();

			_state = label;
			_animation = _graph.PickVariant( head );
			_frame = 0;
			_drawFrom = -1;
			_passedFrame = -1;
			_stateTick = HaloTick.Count;
			_hold = false;
			DrawAnimation();
		}

		// The weapon's own messages, the state animation, the fire hold and the pose's blend and moving overlay.
		public void Tick( IHalo2Hands weapons ) {
			if ( _animation is null ) return;
			TickWeaponMessages( weapons );

			// A state set earlier this tick shows its first frame.
			if ( _stateTick != HaloTick.Count ) Advance( weapons );
			if ( _fireHold > 0 ) _fireHold--;

			var throttle = Spartan.Throttle;
			_pose?.Tick( throttle.Length, !Spartan.IsAirborne, _state == "idle" );
			TickRounds( weapons );
		}

		// The idle timer, after the sway springs step.
		public void TickIdle( IHalo2Hands weapons ) {
			if ( _animation is null ) return;
			var change = _idle.Tick( Spartan, _owner._haloSway, _state == "idle", _state == "posing" );
			if ( change == FirstPersonIdle.Change.Pose ) SetState( "posing" );
			else if ( change == FirstPersonIdle.Change.Idle ) SetState( "idle" );
		}

		// The weapon's own messages to its first person state: fully charged, and the overheat exit.
		// A put away abandoned by switching back goes to idle.
		// The exit starts when its animation would end as the heat falls to the recovery threshold.
		void TickWeaponMessages( IHalo2Hands weapons ) {
			if ( _state == "put_away" && !weapons.HandPuttingAway( _hand ) ) SetState( "idle" );

			var charged = weapons.HandChargeLevel( _hand ) >= 1;
			if ( charged && !_overcharged ) SetState( "overcharged" );
			_overcharged = charged;

			if ( !weapons.HandOverheated( _hand ) ) _exitSent = false;
			else if ( !_exitSent && _graph.Head( "o_h_exit", _dual ) is { } exit && exit.FrameCount >= weapons.HandOverheatTicksLeft( _hand ) ) {
				_exitSent = true;
				SetState( "o_h_exit" );
			}
		}

		// Steps the state animation; when the next step would pass its last frame the state moves on.
		// The frame it leaves plays its sound events, those for one arm only on a dual wielded hand.
		void Advance( IHalo2Hands weapons ) {
			if ( _passedFrame != _frame ) {
				_passedFrame = _frame;
				var at = _renderer.IsValid() ? _renderer.WorldPosition : Spartan.EyePosition;
				AnimationEvents.FirstPerson( _animation, _frame, at, !ThirdPerson, weapons.HandSoundSource( _hand ), _dual ? _hand : null );
			}

			if ( _hold ) {
				_drawFrom = -1;
				if ( _state != "put_away" && !weapons.HandReloading( _hand ) ) SetState( "reload_exit" );
				return;
			}

			if ( _frame + 1 < _animation.FrameCount ) {
				_drawFrom = _frame;
				_frame++;
				return;
			}

			switch ( _state ) {
				case "fire_1" or "fire_2" when LoopsFiring && _fireHold > 0:
				case "overheated" when weapons.HandOverheated( _hand ):
				case "overcharged":
					Loop();
					return;
				case "put_away":
					_hold = true;
					_drawFrom = -1;
					return;
				case "reload_enter" or "reload_continue_full" or "reload_continue_empty" when weapons.HandReloading( _hand ):
					// The next shell's message comes as its timer runs out with this animation.
					_hold = true;
					_drawFrom = -1;
					return;
			}

			SetState( _state switch {
				"fire_2" when weapons.HandTag( _hand )?.WeaponType == "plasma pistol" => "overheated",
				"overheating" or "ready_overheated" or "throw_overheated" => "overheated",
				"overheated" => "o_h_exit",
				"melee_dash" => "melee_lunge",
				"melee_dash_airborne" => "melee_lunge_airborne",
				"reload_enter" or "reload_continue_full" or "reload_continue_empty" => "reload_exit",
				_ => "idle",
			} );
		}

		// Back to the loop frame; looping from the start rolls the variant again.
		void Loop() {
			_pose?.Jumped();
			if ( _animation.LoopFrame == 0 ) _animation = _graph.PickVariant( _graph.Play( _state, _dual ) );
			_frame = Math.Clamp( _animation.LoopFrame, 0, _animation.FrameCount - 1 );
			_drawFrom = -1;
			_passedFrame = -1;
		}

		// The needler's overlay eases up to a reload's new count over 5 ticks.
		void TickRounds( IHalo2Hands weapons ) {
			var loaded = weapons.HandState( _hand ).Loaded;
			if ( loaded <= _shownRounds ) {
				_shownRounds = loaded;
				_roundsRamp = 0;
				return;
			}

			_roundsRamp++;
			_shownRounds += (loaded - _shownRounds) / (RoundsRampTicks - _roundsRamp + 1);
			if ( _roundsRamp >= RoundsRampTicks ) _roundsRamp = 0;
		}
	}
}
