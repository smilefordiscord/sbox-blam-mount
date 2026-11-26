using System;

namespace Halo.Vehicles;

// Campaign-only flight: human planes like the Pelican and dropship, and the alien fighter's older physics.
// A plane is held up by hover and speed and turns toward its pilot's facing.
// The older fighter physics is used by tags with a physics radius, such as lifepods and cutscene Banshees.
public sealed partial class VehicleSimulation {
	// A human plane holds still where it is.
	public bool Hovering;

	void UpdatePlane( IMassPointWorld world, in VehicleControls controls ) {
		var def = Definition;
		var body = Body;
		var model = body.Model;

		if ( Hovering ) {
			Array.Clear( body.Points );
			return;
		}

		var maximum = def.MaximumForwardSpeed;
		var throttle = Math.Clamp( Speed, 0, maximum ) / maximum;
		throttle *= throttle;

		// Hover settles at 3/4 of seat power, 1 braking, 1/4 crouching, less with throttle, moving at most 0.05 a tick.
		var factor = Crouching ? 0.25f : Braking ? 1 : 0.75f;
		factor *= 1 - throttle;
		Hover += Math.Clamp( factor * SeatPower0 - Hover, -0.05f, 0.05f );
		Thrust = throttle * SeatPower0;

		var desiredForward = controls.DesiredFacing.LengthSquared > 0 ? controls.DesiredFacing : body.Forward;
		var desiredUp = LevelUp( desiredForward );

		var along = Vector3.Dot( body.Velocity, body.Forward );
		var drive = (Speed - along) * Thrust * model.Mass * 0.05f;
		var lift = (MathF.Abs( along / maximum ) * 1.05f + Hover * 1.3f) * MassPointBody.Gravity * model.Mass;
		var force = body.Up * lift + body.Forward * drive;

		// Roll into the sideways drift.
		var yaw = (body.Velocity.y * desiredForward.x - body.Velocity.x * desiredForward.y) * (MathF.PI / 2) / MathF.Abs( maximum );
		desiredUp = desiredUp * MathF.Cos( yaw ) + Vector3.Cross( desiredForward, desiredUp ) * MathF.Sin( yaw );

		var torque = (TurnToward( desiredForward, desiredUp ) * (1f / 30) - body.AngularVelocity) * (model.Radius * model.Radius * model.Mass * 0.05f);

		body.Update( world, null, force * SeatPower0, torque * SeatPower0 );
	}

	void UpdateFighterOld( IMassPointWorld world, in VehicleControls controls ) {
		var def = Definition;
		var body = Body;
		var model = body.Model;

		if ( model.Powered.Length != 2 ) {
			body.Update( world, null, Vector3.Zero, Vector3.Zero );
			return;
		}

		var facing = controls.DesiredFacing.LengthSquared > 0 ? controls.DesiredFacing : body.Forward;
		var desiredUp = LevelUp( facing );

		var along = Vector3.Dot( body.Forward, body.Velocity );
		var thrust = (Speed - along) * model.Mass * 0.05f;
		var lift = MathF.Abs( along / def.MaximumForwardSpeed ) * model.Mass * MassPointBody.Gravity * 1.05f;
		var force = body.Up * lift + body.Forward * thrust;

		var yaw = (facing.x * body.Velocity.y - facing.y * body.Velocity.x) * (MathF.PI / 2) / MathF.Abs( def.MaximumForwardSpeed );
		desiredUp = desiredUp * MathF.Cos( yaw ) + Vector3.Cross( facing, desiredUp ) * MathF.Sin( yaw );

		var torque = (TurnToward( facing, desiredUp ) * (4f / 30) - body.AngularVelocity) * (model.Radius * model.Radius * model.Mass * 0.05f);

		_powered[0] = new PoweredMassPointState { AntigravFraction = SeatPower0 };
		_powered[1] = new PoweredMassPointState { AntigravFraction = SeatPower0 };
		body.Update( world, _powered, force * SeatPower0, torque * SeatPower0 );
	}

	// Up for a roll-free frame looking along forward; (1, 0, 0) looking straight up or down.
	static Vector3 LevelUp( Vector3 forward ) {
		var up = new Vector3( -forward.z * forward.x, -forward.z * forward.y, 1 - forward.z * forward.z );
		return up.LengthSquared > 0 ? up.Normal : new Vector3( 1, 0, 0 );
	}

	// The rotation from the body's orientation to the desired one, as an axis times its angle in radians.
	Vector3 TurnToward( Vector3 forward, Vector3 up ) {
		var delta = FromForwardUp( forward, up ) * FromForwardUp( Body.Forward, Body.Up ).Inverse;
		var w = Math.Clamp( delta.w, -1, 1 );
		var axis = new Vector3( delta.x, delta.y, delta.z );
		var sine = axis.Length;
		var angle = 2 * MathF.Atan2( sine, w );
		if ( angle > MathF.PI ) {
			angle = MathF.PI * 2 - angle;
			axis = -axis;
		}
		return sine > 1e-6f ? axis / sine * angle : Vector3.Zero;
	}
}
