using System;
using Halo2.Data;

namespace Halo2.Combat;

// The local player's Halo 2 first person weapons, from halo2sbox's SpartanView: the mount's first person models of the weapons in hand at Sandbox's camera,
// animated by Halo 2's first person weapon states. Sandbox keeps the camera, look and death camera.
public sealed partial class SpartanView {
	// Halo 2 draws the first person weapon with its own fixed vertical field of view, 0.865588 radians.
	const float ViewModelVerticalFov = 49.5945f;

	// Camera space: +X forward, +Y left, +Z up.
	static Vector3 ViewModelOffset => Vector3.Zero;

	// The local player's camera is behind them, so the first person weapons are hidden.
	public static bool ThirdPerson { get; set; }

	// Halo 2's two first person states, one per hand; the left's has a weapon only while dual wielding.
	HandView _rightView;
	HandView _leftView;

	HandView RightView => _rightView ??= new HandView( this, Hand.Right );
	HandView LeftView => _leftView ??= new HandView( this, Hand.Left );
	HandView View( Hand hand ) => hand == Hand.Left ? LeftView : RightView;

	readonly IHalo2Hands _holder;

	// The weapon's holder, as halo2sbox's views read their Spartan and its weapons.
	IHalo2Hands Spartan => _holder;

	public SpartanView( IHalo2Hands holder ) => _holder = holder;

	public void Destroy() {
		_rightView?.Destroy();
		_leftView?.Destroy();
		_rightView = _leftView = null;
		_localRight = _localLeft = default;
		LocalViewModelShown = false;
	}

	// The camera-space spread that makes the weapon look as at ViewModelFieldOfView: the ratio of the two FOV tangents.
	static float ViewModelSpread {
		get {
			var camera = Game.ActiveScene?.Camera;
			if ( camera is null ) return 1;
			var vertical = camera.FovAxis == CameraComponent.Axis.Vertical ? camera.FieldOfView
				: 2 * MathF.Atan( MathF.Tan( camera.FieldOfView.DegreeToRadian() / 2 ) * Screen.Height / MathF.Max( 1, Screen.Width ) ).RadianToDegree();
			var k = MathF.Tan( vertical.DegreeToRadian() / 2 ) / MathF.Tan( ViewModelVerticalFov.DegreeToRadian() / 2 );
			return float.IsFinite( k ) && k > 0 ? k : 1;
		}
	}

	// A view model marker as the scene shows it; the spread moves positions but not directions, so spread those here.
	// Keeps world particles from weapon markers coming out of the drawn weapon.
	public static Transform ViewModelToWorld( Transform t ) {
		var camera = Game.ActiveScene?.Camera;
		if ( camera is null ) return t;

		// Camera space is x forward, y left, z up.
		var k = ViewModelSpread;
		var view = camera.WorldRotation;
		Vector3 Spread( Vector3 v ) => view * new Vector3( v.x, v.y * k, v.z * k );
		var forward = Spread( view.Inverse * t.Rotation.Forward );
		var up = Spread( view.Inverse * t.Rotation.Up );
		return new Transform( t.Position, Rotation.LookAt( forward.Normal, up.Normal ), t.Scale.x );
	}

	// The right hand's camera_control node this frame, which Halo 2's first person camera follows; null without one.
	public Transform? CameraControl => _rightView is { Shown: true } right ? right.Pose?.CameraControl : null;

	// What a hand's overlays read this frame.
	FirstPersonLayers Layers( Hand hand, float rounds ) => new(
		_haloSway.Position, _haloSway.Turning, _haloSway.PushBack( hand ),
		-Spartan.EyeAngles.pitch.DegreeToRadian(), rounds, _overlayFade.Value );

	// Each frame at the camera as Sandbox placed it (visible is false in third person): the view models, then their spread and overlays.
	public void Place( Transform camera, bool visible ) {
		using var perf = Halo2.Combat.HaloPerf.Measure( "View.Update" );
		_weaponTicking = false;
		_localRight = _localLeft = default;
		if ( !Spartan.IsAlive ) {
			_rightView?.Show( false );
			_leftView?.Show( false );
			LocalViewModelShown = false;
			_haloSway.Reset();
			return;
		}

		// Halo keeps the weapons animating unseen in third person.
		RightView.Update( camera, visible );
		LeftView.Update( camera, visible );
		_weaponTicking = RightView.Valid;
		LocalViewModelShown = RightView.Shown || LeftView.Shown;

		var k = ViewModelSpread;
		Spartan.Scene.RenderAttributes.Set( "HaloFirstPersonSpread", k );
		_leftView?.PreRender( k, null );
		_rightView?.PreRender( k, _leftView );
	}

	// View models

	// What the local player's hand views show this frame, for first person effects and shots.
	// Muzzle is the "primary_trigger" marker while shown; Barrel is where shots leave it, even while zoom hides it.
	readonly record struct LocalHand( Transform? Muzzle, Transform? Barrel, GameObject MuzzleObject, SkinnedModelRenderer Renderer, bool Shown );

	static LocalHand _localRight, _localLeft;

	static ref LocalHand Local( Hand hand ) => ref hand == Hand.Left ? ref _localLeft : ref _localRight;

	public static Transform? LocalMuzzle( Hand hand ) => Local( hand ).Muzzle;

	// Null without a first person weapon in that hand.
	public static Transform? LocalBarrel( Hand hand ) => Local( hand ).Barrel;

	// The muzzle object, for attaching first person firing effects.
	public static GameObject LocalMuzzleObject( Hand hand ) => Local( hand ).MuzzleObject;

	// The hand's view model, for first person effects on a weapon without a muzzle, like the sword.
	public static GameObject LocalViewObject( Hand hand ) => Local( hand ).Renderer is { } r && r.IsValid() ? r.GameObject : null;
	public static bool LocalViewShown( Hand hand ) => Local( hand ).Shown;

	// Either hand's view model is shown.
	public static bool LocalViewModelShown { get; private set; }

	// The one-shot effects playing on each hand's view model.
	static readonly Combat.CarriedEffects RightEffects = new(), LeftEffects = new();

	public static Combat.CarriedEffects LocalViewEffects( Hand hand ) => hand == Hand.Left ? LeftEffects : RightEffects;

	// A hand's view model marker for first person effects, posed even while zoom hides it; null if missing.
	// Halo 2 moves a weapon's markers onto the first person model of the state holding that weapon.
	public static GameObject LocalViewMarker( Hand hand, string name ) {
		var renderer = Local( hand ).Renderer;
		if ( string.IsNullOrEmpty( name ) || !renderer.IsValid() ) return null;
		var marker = renderer.GetAttachmentObject( name );
		return marker.IsValid() ? marker : null;
	}

	// The mount's "<weapon tag>_fp.vmdl": hands and weapon on one skeleton with the first person animations.
	public static string ViewModelPath( string weaponPath ) {
		if ( string.IsNullOrEmpty( weaponPath ) ) return null;
		return $"mount://{HaloMountCheck.Ident}/{Combat.HaloEffects.WithoutExtension( weaponPath )}_fp.vmdl";
	}

	// Sandbox's units have no shield shell to draw.
	public static SkinnedModelRenderer CreateShell( SkinnedModelRenderer target, Model model, string material ) => null;

	void SetShields( SkinnedModelRenderer shieldShell, SkinnedModelRenderer overshieldShell ) { }

	// States

	readonly FirstPersonSway _haloSway = new();
	readonly FirstPersonOverlayFade _overlayFade = new();

	// The right hand's view model was posed last frame, so the sway springs tick.
	bool _weaponTicking;

	// Halo 2's message to a hand's first person state.
	// The weapon's own go to its hand; of the unit's only the left put away, ready and removal go to the left hand.
	// A ready or removal first sets both hands up afresh at idle, so taking or losing the left weapon changes the right's class.
	public void Message( Hand hand, string label ) {
		var weapons = Spartan;
		var unit = label is "ready" or "ready_overheated" or "put_away" or "none";
		var target = unit && !(label is "ready" or "put_away" or "none") ? Hand.Right : hand;

		var rebuild = label is "ready" or "none";
		RightView.Sync( weapons, rebuild, rebuild && target == Hand.Right && label == "ready" );
		LeftView.Sync( weapons, rebuild, rebuild && target == Hand.Left && label == "ready" );

		if ( label == "fire_1" ) _haloSway.Fired( target );
		View( target ).Message( label );
	}

	// One first person weapon tick: the weapons' own messages, the state animations, the overlays' springs, the idle timers.
	public void Tick() {
		if ( !Spartan.IsLocalPlayer || !Spartan.IsAlive || !RightView.Animating ) return;
		var weapons = Spartan;
		RightView.Tick( weapons );
		LeftView.Tick( weapons );
		_overlayFade.Tick( !RightView.DisablesOverlays && !LeftView.DisablesOverlays );

		if ( _weaponTicking ) _haloSway.Tick( Spartan.Throttle, Spartan.EyeAngles, weapons.CurrentTag );

		RightView.TickIdle( weapons );
		LeftView.TickIdle( weapons );
	}
}
