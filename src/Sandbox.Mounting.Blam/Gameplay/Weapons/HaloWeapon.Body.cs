using System;
using Halo;
using Halo.Combat;

namespace HaloWeapons;

// The holder in third person, as everyone sees them: Halo's Spartan animation for the weapon's type on their upper body (HaloBodyPose), with
// the weapon on Halo's "right hand" marker. Every machine runs it; the owner's reloads, melees and throws are broadcast, and shots play their
// fire overlay where every machine fires them.
public sealed partial class HaloWeapon {
	// Halo CE's Spartan runs at about 2.25 world units a second; the throttle is the holder's speed over that.
	const float RunSpeed = 2.25f * HaloUnits.WorldScale;

	// A body action for BroadcastBodyAction besides UnitAnimation's weapon actions.
	const int ThrowAction = -1;

	HaloBodyPose _body;
	bool _bodyTried;
	SkinnedModelRenderer _bodyPosed;

	void PoseBody() {
		var owner = Owner;
		var controller = owner?.Controller;
		var renderer = controller?.Renderer;
		if ( renderer is null || IsDropped || owner.IsSeated || !Active ) {
			ReleaseBody();
			return;
		}

		if ( !_bodyTried ) {
			_bodyTried = true;
			_body = HaloBodyPose.Create();
		}
		if ( _body is null ) return;
		if ( renderer != _bodyPosed ) ReleaseBody();

		// Halo aims relative to the body, which Sandbox turns after the view.
		var root = renderer.WorldTransform;
		var eyes = controller.EyeAngles;
		var yaw = ((eyes.yaw - root.Rotation.Yaw()) % 360 + 540) % 360 - 180;
		var local = root.Rotation.Inverse * controller.Velocity;
		var throttle = new Vector2( local.x, local.y ) / RunSpeed;
		if ( throttle.Length > 1 ) throttle = throttle.Normal;
		var input = new UnitAnimation.Input( throttle, controller.IsDucking, !controller.IsOnGround, yaw.DegreeToRadian(), (-eyes.pitch).DegreeToRadian(), Tag?.Label );

		if ( !_body.Apply( renderer, root, input, upperBody: true ) ) {
			PlaceOnHoldBone( renderer );
			return;
		}
		_bodyPosed = renderer;
		if ( WorldModel.IsValid() && _body.HandMarker( "right hand" ) is { } hand ) WorldModel.WorldTransform = hand.WithScale( 1 );
	}

	// A model the pose can't take holds the weapon on Sandbox's hold bone.
	void PlaceOnHoldBone( SkinnedModelRenderer renderer ) {
		if ( WorldModel.IsValid() && renderer.TryGetBoneTransform( HoldBone, out var hold ) )
			WorldModel.WorldTransform = hold.ToWorld( new Transform( HoldOffset, HoldAngles.ToRotation() ) ).WithScale( 1 );
	}

	// Hands the body back to Sandbox's own animation.
	void ReleaseBody() {
		if ( _bodyPosed.IsValid() ) _bodyPosed.ClearPhysicsBones();
		_bodyPosed = null;
		_body?.Animation.Reset();
	}

	// The owner's reload, melee or grenade throw, on every machine's copy of the body.
	void BodyAction( int action ) {
		PlayBodyAction( action );
		BroadcastBodyAction( action );
	}

	[Rpc.Broadcast( NetFlags.Unreliable )]
	void BroadcastBodyAction( int action ) {
		if ( Rpc.Caller == Connection.Local ) return;
		PlayBodyAction( action );
	}

	void PlayBodyAction( int action ) {
		var animation = _body?.Animation;
		if ( animation is null ) return;
		if ( action == ThrowAction ) animation.ThrowGrenade();
		else animation.StartAction( action );
	}
}
