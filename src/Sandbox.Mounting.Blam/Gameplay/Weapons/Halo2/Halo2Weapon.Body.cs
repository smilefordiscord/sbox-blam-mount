using System;
using Halo2.Combat;
using Halo2.Data;

namespace HaloWeapons;

// The holder in third person, as everyone sees them: Halo 2's Spartan animation for the weapon's class and type on their upper body
// (HaloBodyPose over halo2sbox's SpartanAnimation), the weapon on Halo 2's "right_hand" marker. Every machine runs it; the owner's reloads,
// melees, throws, readies and overheats are broadcast, and shots play their fire overlay where every machine fires them.
public sealed partial class Halo2Weapon {
	// Halo 2's Spartan runs at about 2.25 world units a second; the throttle is the holder's speed over that.
	const float RunSpeed = 2.25f * HaloUnits.WorldScale;

	HaloBodyPose _body;
	bool _bodyTried;
	SkinnedModelRenderer _bodyPosed;

	SpartanAnimation BodyAnimation => _body?.Halo2Animation;

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
			_body = HaloBodyPose.CreateHalo2();
		}
		if ( _body is null ) {
			PlaceOnHoldBone( renderer );
			return;
		}
		if ( renderer != _bodyPosed ) ReleaseBody();

		// Halo 2 aims and moves relative to the body, which Sandbox turns after the view.
		var root = renderer.WorldTransform;
		var eyes = controller.EyeAngles;
		var yaw = ((eyes.yaw - root.Rotation.Yaw()) % 360 + 540) % 360 - 180;
		var local = root.Rotation.Inverse * controller.Velocity;
		var throttle = new Vector2( local.x, local.y ) / RunSpeed;
		if ( throttle.Length > 1 ) throttle = throttle.Normal;
		var input = new SpartanAnimation.Input( throttle, new Angles( eyes.pitch, yaw, 0 ), local / HaloUnits.WorldScale, !controller.IsOnGround,
			controller.IsDucking, Tag, HandOverheated( Hand.Right ), null, false );

		if ( !_body.Apply( renderer, root, input ) ) {
			PlaceOnHoldBone( renderer );
			return;
		}
		_bodyPosed = renderer;
		if ( HeldPlacement() is { } placed ) WorldModel.WorldTransform = placed;
		else PlaceOnHoldBone( renderer );
	}

	// Halo 2 hangs the weapon at the biped's right hand marker by the biped's preferred gun marker if the weapon has it, else by its handle,
	// which unnamed is its origin.
	Transform? HeldPlacement() {
		if ( !WorldModel.IsValid() || WorldModel.GetComponent<ModelRenderer>()?.Model is not { } weapon ) return null;
		var biped = HaloDataStore.SpartanBiped;
		var hand = string.IsNullOrEmpty( biped?.RightHandNode ) ? "right_hand" : biped.RightHandNode;
		if ( _body.HandMarker( hand ) is not { } posed ) return null;
		var preferred = biped?.PreferredGunNode;
		var by = string.IsNullOrEmpty( preferred ) ? null : weapon.Attachments?.Get( preferred );
		if ( by is null && !string.IsNullOrEmpty( Tag?.HandleNode ) ) by = weapon.Attachments?.Get( Tag.HandleNode );
		return by is null ? posed.WithScale( 1 ) : posed.WithScale( 1 ).ToWorld( by.WorldTransform.ToLocal( global::Transform.Zero ) );
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
		BodyAnimation?.Reset();
	}

	// The body's weapon actions, as halo2sbox's SpartanBody plays them.
	enum BodyAct { Reload, Chamber, Ready, PutAway, Overheat, Throw, Melee }

	// The owner's action, on every machine's copy of the body.
	void BodyAction( BodyAct act, Hand hand = Hand.Right, string label = null ) {
		PlayBodyAction( act, hand, label );
		BroadcastBodyAction( (int)act, (int)hand, label );
	}

	[Rpc.Broadcast( NetFlags.Unreliable )]
	void BroadcastBodyAction( int act, int hand, string label ) {
		if ( Rpc.Caller == Connection.Local ) return;
		PlayBodyAction( (BodyAct)act, (Hand)hand, label );
	}

	void PlayBodyAction( BodyAct act, Hand hand, string label ) {
		var animation = BodyAnimation;
		if ( animation is null ) return;
		switch ( act ) {
			case BodyAct.Reload: animation.Reloaded( hand ); break;
			case BodyAct.Chamber: animation.Chambered( hand ); break;
			case BodyAct.Ready: animation.Readied( hand ); break;
			case BodyAct.PutAway: animation.PutAway( hand ); break;
			case BodyAct.Overheat: animation.Overheating( hand ); break;
			case BodyAct.Throw: animation.ThrewGrenade(); break;
			case BodyAct.Melee: if ( !string.IsNullOrEmpty( label ) ) animation.Melee( label ); break;
		}
	}
}
