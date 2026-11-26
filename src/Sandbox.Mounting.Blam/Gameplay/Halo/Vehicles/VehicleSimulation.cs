using System;

namespace Halo.Vehicles;

// The vehi tag's vehicle type, which picks the physics update.
public enum VehicleKind {
	// Four wheel steering: the Warthog.
	Jeep,
	// Two treads: the Scorpion.
	Tank,
	// Antigravity hover: the Ghost.
	Scout,
	// Flight: the Banshee.
	Fighter,
	// Mass points only: the Covenant gun turret.
	Turret,
	// Hover and speed lift: the Pelican and the Covenant dropship.
	Plane,
}

// The vehi tag fields the per-tick vehicle update reads.
public sealed class VehicleMotionDefinition {
	public VehicleKind Kind;

	public bool SpeedWakesPhysics, TurnWakesPhysics, DriverPowerWakesPhysics, GunnerPowerWakesPhysics;
	public bool ControlOppositeSpeedSetsBrake, SlideWakesPhysics, KillsRidersAtTerminalVelocity;

	public float MaximumForwardSpeed, MaximumReverseSpeed, SpeedAcceleration, SpeedDeceleration;
	// Degrees for the turn variable; the Banshee reads them raw for its sideslip bank.
	public float MaximumLeftTurn, MaximumRightTurn;
	public float WheelCircumference;
	// Degrees per second for the turn variable; the Banshee reads it raw.
	public float TurnRate;
	public float MaximumLeftSlide, MaximumRightSlide, SlideAcceleration, SlideDeceleration;
	public float MinimumFlippingAngularVelocity, MaximumFlippingAngularVelocity;
	// Radians the Banshee flies pitched above where its pilot looks.
	public float FixedGunPitch;

	// Per powered seat, in seconds.
	public float[] PowerupTime = Array.Empty<float>();
	public float[] PowerdownTime = Array.Empty<float>();
}

// One tick's controls.
public struct VehicleControls {
	// x forward, y left, each -1 to 1.
	public Vector2 Throttle;
	// The brake.
	public bool Jump;
	public bool Crouch;
	// Where the driver looks, a unit vector.
	public Vector3 DesiredFacing;
	// Powers up seat 0 (driver) and seat 1 (a separate gunner).
	public bool HasDriver, HasGunner;
	// An actor drives, so a fighter flies along its facing without the fixed gun pitch.
	public bool DrivenByAi;
}

// Halo's per-tick vehicle update: controls become the speed, slide and turn variables, then the type's physics runs.
// Units are Halo's, as in MassPointBody.
public sealed partial class VehicleSimulation {
	public readonly VehicleMotionDefinition Definition;
	public readonly MassPointBody Body;
	readonly PoweredMassPointState[] _powered;

	public float Speed, Slide, Turn, Wheel, LeftTread, RightTread, Hover, Thrust;
	public int StopTime, AirborneTicks, OnGroundTicks;
	public int UpendingType, UpendingTicks;
	public bool Crouching;
	public bool Braking;
	// Someone is flipping it back over.
	public bool Upending;

	// Driver and gunner seat power.
	public float SeatPower0, SeatPower1;

	// Last tick's angle to the desired facing, in radians, left positive.
	public float SteeringAngle { get; private set; }

	// The change of velocity over the last tick physics ran, for crash effects.
	public float LastImpact { get; private set; }

	// Everything carried between ticks: what the host sends and a predicting client rewinds to.
	// Mass point state is excluded; each tick recomputes it from the pose, as Halo does.
	public struct State {
		public Vector3 Position, Forward, Up, Velocity, AngularVelocity;
		public bool AtRest;
		public uint StuckMassPointFlags;
		public Vector3 CollisionForce, CollisionTorque;
		public float Speed, Slide, Turn, Wheel, LeftTread, RightTread, Hover, Thrust;
		public int StopTime, AirborneTicks, OnGroundTicks, UpendingType, UpendingTicks;
		public bool Crouching, Braking, Upending;
		public float SeatPower0, SeatPower1;
	}

	public State Capture() => new() {
		Position = Body.Position, Forward = Body.Forward, Up = Body.Up,
		Velocity = Body.Velocity, AngularVelocity = Body.AngularVelocity,
		AtRest = Body.AtRest, StuckMassPointFlags = Body.StuckMassPointFlags,
		CollisionForce = Body.CollisionForce, CollisionTorque = Body.CollisionTorque,
		Speed = Speed, Slide = Slide, Turn = Turn, Wheel = Wheel, LeftTread = LeftTread, RightTread = RightTread,
		Hover = Hover, Thrust = Thrust,
		StopTime = StopTime, AirborneTicks = AirborneTicks, OnGroundTicks = OnGroundTicks,
		UpendingType = UpendingType, UpendingTicks = UpendingTicks,
		Crouching = Crouching, Braking = Braking, Upending = Upending,
		SeatPower0 = SeatPower0, SeatPower1 = SeatPower1,
	};

	public void Restore( in State s ) {
		Body.Position = s.Position; Body.Forward = s.Forward; Body.Up = s.Up;
		Body.Velocity = s.Velocity; Body.AngularVelocity = s.AngularVelocity;
		Body.AtRest = s.AtRest; Body.StuckMassPointFlags = s.StuckMassPointFlags;
		Body.CollisionForce = s.CollisionForce; Body.CollisionTorque = s.CollisionTorque;
		Speed = s.Speed; Slide = s.Slide; Turn = s.Turn; Wheel = s.Wheel; LeftTread = s.LeftTread; RightTread = s.RightTread;
		Hover = s.Hover; Thrust = s.Thrust;
		StopTime = s.StopTime; AirborneTicks = s.AirborneTicks; OnGroundTicks = s.OnGroundTicks;
		UpendingType = s.UpendingType; UpendingTicks = s.UpendingTicks;
		Crouching = s.Crouching; Braking = s.Braking; Upending = s.Upending;
		SeatPower0 = s.SeatPower0; SeatPower1 = s.SeatPower1;
	}

	// One 1/30 s tick as a pure step; Halo's constants are per tick.
	// Deterministic for the same inputs, so clients can predict and replay it.
	public void Step( ref State state, in VehicleControls controls, IMassPointWorld world ) {
		Restore( state );
		Tick( world, controls );
		state = Capture();
	}

	public VehicleSimulation( VehicleMotionDefinition definition, MassPointModel model ) {
		Definition = definition;
		Body = new MassPointBody( model );
		_powered = new PoweredMassPointState[Math.Max( 1, model.Powered.Length )];
	}

	public void Reset() {
		Speed = Slide = Turn = Wheel = LeftTread = RightTread = Hover = Thrust = 0;
		StopTime = AirborneTicks = OnGroundTicks = UpendingType = UpendingTicks = 0;
		Crouching = Braking = Upending = Hovering = false;
		Body.CollisionForce = Body.CollisionTorque = Vector3.Zero;
		Body.StuckMassPointFlags = 0;
	}

	// Center of mass half the bounding radius above the spot and awake, so it drops onto its mass points.
	public void Place( Vector3 position, Vector3 forward, Vector3 up, float boundingRadius ) {
		Reset();
		Body.Forward = forward.Normal;
		Body.Up = up.Normal;
		Body.Position = position + new Vector3( 0, 0, boundingRadius * 0.5f );
		Body.Velocity = Vector3.Zero;
		Body.AngularVelocity = Vector3.Zero;
		Body.AtRest = false;
	}

	// A player flipping it back over.
	public void StartUpending( int approachType ) {
		Upending = true;
		UpendingType = approachType;
		UpendingTicks = 0;
	}

	// Seat power, then the vehicle update.
	public void Tick( IMassPointWorld world, in VehicleControls controls ) {
		UpdateSeatPower( controls );

		var def = Definition;
		var body = Body;

		Crouching = controls.Crouch;
		Braking = controls.Jump || (def.ControlOppositeSpeedSetsBrake &&
			((controls.Throttle.x > 0 && Speed < 0) || (controls.Throttle.x < 0 && Speed > 0)));

		var facing = controls.DesiredFacing.LengthSquared > 0 ? controls.DesiredFacing : body.Forward;
		SteeringAngle = MathF.Atan2( Vector3.Dot( body.Left, facing ), Vector3.Dot( facing, body.Forward ) );

		UpdateUpending();
		UpdateVariables( controls );

		if ( (def.SpeedWakesPhysics && Speed != 0) ||
			(def.TurnWakesPhysics && Turn != 0) ||
			(def.DriverPowerWakesPhysics && SeatPower0 != 0) ||
			(def.GunnerPowerWakesPhysics && SeatPower1 != 0) ||
			(def.SlideWakesPhysics && Slide != 0) ) {
			body.AtRest = false;
		}

		if ( !body.AtRest ) {
			var previousVelocity = body.Velocity;

			switch ( def.Kind ) {
				case VehicleKind.Tank: UpdateTank( world ); break;
				case VehicleKind.Jeep: UpdateJeep( world ); break;
				case VehicleKind.Scout: UpdateScout( world, controls ); break;
				case VehicleKind.Fighter when Body.Model.Radius > 0: UpdateFighterOld( world, controls ); break;
				case VehicleKind.Fighter: UpdateFighter( world, controls ); break;
				case VehicleKind.Plane: UpdatePlane( world, controls ); break;
				default: body.Update( world, null, Vector3.Zero, Vector3.Zero ); break;
			}

			LastImpact = (body.Velocity - previousVelocity).Length;
			ComputeAirborneTicks();

			if ( body.AtRest )
				StopTime = 15;

			// Flyers are pushed back between the BSP's vehicle floor and ceiling (0 is none), scaled by seat power.
			if ( def.Kind is VehicleKind.Fighter or VehicleKind.Plane ) {
				var z = body.Position.z;
				var v = body.Velocity;
				var floor = world.VehicleFloor;
				var ceiling = world.VehicleCeiling;
				if ( floor != 0 && z < floor )
					v.z += ((floor - z) * 0.015625f - v.z * 0.0625f) * SeatPower0;
				if ( ceiling != 0 && z > ceiling )
					v.z -= ((z - ceiling) * 0.015625f + v.z * 0.0625f) * SeatPower0;
				body.Velocity = v;
			}
		}
		else {
			LastImpact = 0;
			if ( StopTime > 0 )
				SlowlyStop();
		}
	}

	void UpdateSeatPower( in VehicleControls controls ) {
		var def = Definition;
		for ( var seat = 0; seat < def.PowerupTime.Length && seat < 2; seat++ ) {
			var on = seat == 0 ? controls.HasDriver : controls.HasGunner;
			ref var power = ref (seat == 0 ? ref SeatPower0 : ref SeatPower1);
			if ( !on ) {
				if ( power != 0 )
					power = MathF.Max( 0, power - 1 / (MathF.Max( def.PowerdownTime[seat], 0.0001f ) * 30) );
			}
			else if ( power != 1 ) {
				power = MathF.Min( 1, power + 1 / (MathF.Max( def.PowerupTime[seat], 0.0001f ) * 30) );
			}
		}
	}

	// Up to 30 ticks of spin back toward upright.
	void UpdateUpending() {
		var body = Body;
		if ( Upending && UpendingType != 0 && UpendingTicks < 30 && body.Up.z <= 0.9f ) {
			var torque = UpendingType is 2 or 4 ? 0.3f : -0.3f;
			var axis = UpendingType is 4 or 3 ? Vector3.Cross( body.Forward, body.Up ) : body.Forward;

			var roll = body.Up.z * -2;
			if ( roll < Definition.MinimumFlippingAngularVelocity ) roll = Definition.MinimumFlippingAngularVelocity;
			else if ( roll > Definition.MaximumFlippingAngularVelocity ) roll = Definition.MaximumFlippingAngularVelocity;

			torque *= roll;
			body.AtRest = false;

			if ( UpendingType is 2 or 1 )
				axis += Vector3.Cross( body.Forward, body.Up ) * -body.Forward.z;

			body.AngularVelocity = axis * torque;

			switch ( Definition.Kind ) {
				case VehicleKind.Fighter:
					body.Velocity = body.Velocity.WithZ( MathF.Min( -0.01f, body.Velocity.z ) );
					break;
				case VehicleKind.Tank:
					body.Velocity = body.Forward * Vector3.Dot( body.Forward, body.Velocity );
					break;
			}

			UpendingTicks++;
		}
		else {
			UpendingTicks = 0;
			UpendingType = 0;
			Upending = false;
		}
	}

	void UpdateVariables( in VehicleControls controls ) {
		var def = Definition;
		var speed = new SpeedParameters( def.MaximumForwardSpeed, def.MaximumReverseSpeed, def.SpeedAcceleration, def.SpeedDeceleration );

		if ( Braking ) {
			SpeedSeek( ref Speed, speed, 0, 1 );
		}
		else {
			SpeedSeek( ref Speed, speed, controls.Throttle.x, 1 );
			SpeedSeek( ref Slide, new SpeedParameters( def.MaximumLeftSlide, def.MaximumRightSlide, def.SlideAcceleration, def.SlideDeceleration ), controls.Throttle.y, 1 );
		}

		if ( def.Kind != VehicleKind.Tank ) {
			// As in Halo, the seek limits are the raw tag degrees while the target is clamped to them in radians.
			var desired = Speed < 0 ? -SteeringAngle : SteeringAngle;
			var minimum = def.MaximumRightTurn * (MathF.PI / 180);
			var maximum = def.MaximumLeftTurn * (MathF.PI / 180);
			if ( desired < minimum ) desired = minimum;
			else if ( desired > maximum ) desired = maximum;

			PositionSeek( ref Turn, def.MaximumLeftTurn, def.MaximumRightTurn, desired, def.TurnRate * (MathF.PI / 180) / 30 );
		}
		else if ( Speed == 0 ) {
			SpeedSeek( ref Turn, speed, 0, 1 );
		}
		else {
			SpeedSeek( ref Turn, speed, Math.Clamp( SteeringAngle * 0.63661975f, -1, 1 ) * def.MaximumForwardSpeed, 2 );
		}
	}

	// The vehicle types

	void UpdateJeep( IMassPointWorld world ) {
		var def = Definition;
		Wheel = Wrap( Wheel + Speed, def.WheelCircumference );

		if ( Body.Model.Powered.Length == 2 ) {
			// The front pair turns by the turn, the back pair the other way.
			_powered[0] = new PoweredMassPointState { GroundFrictionVelocity = Speed, Yaw = Turn };
			_powered[1] = new PoweredMassPointState { GroundFrictionVelocity = Speed, Yaw = -Turn };
			Body.Update( world, _powered, Vector3.Zero, Vector3.Zero );
		}
		else {
			Body.Update( world, null, Vector3.Zero, Vector3.Zero );
		}
	}

	void UpdateTank( IMassPointWorld world ) {
		var def = Definition;
		var left = Speed - Turn;
		var right = Turn + Speed;
		LeftTread = Wrap( LeftTread + left, def.WheelCircumference );
		RightTread = Wrap( RightTread + right, def.WheelCircumference );

		if ( Body.Model.Powered.Length == 2 ) {
			_powered[0] = new PoweredMassPointState { GroundFrictionVelocity = left };
			_powered[1] = new PoweredMassPointState { GroundFrictionVelocity = right };
			Body.Update( world, _powered, Vector3.Zero, Vector3.Zero );
		}
		else {
			Body.Update( world, null, Vector3.Zero, Vector3.Zero );
		}
	}

	// The Ghost.
	void UpdateScout( IMassPointWorld world, in VehicleControls controls ) {
		var def = Definition;
		var body = Body;
		var model = body.Model;
		var steering = SteeringAngle;
		var magicForce = Vector3.Zero;
		var magicTorque = Vector3.Zero;
		var antigrav = SeatPower0;

		for ( var i = 0; i < model.Powered.Length; i++ )
			_powered[i] = new PoweredMassPointState { AntigravFraction = antigrav };

		var waterDepth = world.WaterDepth( body.Position );
		if ( waterDepth < 0.5f && body.Up.z > -0.2f ) {
			var forward = body.Forward;
			var up = body.Up;
			var left = body.Left;
			var angularVelocity = body.AngularVelocity;
			var localVelocity = new Vector3( Vector3.Dot( body.Velocity, forward ), Vector3.Dot( body.Velocity, left ), Vector3.Dot( body.Velocity, up ) );

			if ( Hover > 0 ) {
				var maximumSpeed = def.MaximumForwardSpeed;
				if ( Braking ) maximumSpeed *= 0.8f;
				var maximumAcceleration = def.SpeedAcceleration;

				var acceleration = new Vector3( maximumSpeed * controls.Throttle.x - localVelocity.x, maximumSpeed * controls.Throttle.y - localVelocity.y, 0 );
				if ( OnGroundTicks > 0 && MathF.Abs( steering ) > 0.785398185f )
					maximumAcceleration *= 1 - MathF.Min( OnGroundTicks * 0.05f, 0.98f );

				acceleration = Limit( acceleration, maximumAcceleration );
				acceleration = forward * acceleration.x + left * acceleration.y + up * acceleration.z;
				magicForce += acceleration * (model.Mass * Hover);
			}

			if ( Hover > 0 ) {
				var current = Vector3.Dot( up, angularVelocity );
				var sign = MathF.Sign( steering );
				var desired = MathF.Sqrt( MathF.Abs( steering ) * 0.0069813174f ) * sign;
				if ( MathF.Abs( desired ) > 0.0001f && steering / desired < 2 )
					desired = steering * 0.5f;

				var error = Math.Clamp( desired - current, -0.0034906587f, 0.0034906587f );
				magicTorque += up * (error * model.ZzMoment * Hover);
			}

			if ( Hover < 1 ) {
				// Off the ground the stick pitches and rolls it, and it rights itself.
				var forward2 = new Vector2( forward.x, forward.y ).Normal;
				var left2 = new Vector2( left.x, left.y ).Normal;
				var control = Vector2.Zero;
				float torqueA, torqueB;

				if ( up.z > 0 ) {
					var up2 = new Vector2( up.x, up.y );
					var spin2 = new Vector2( angularVelocity.x, angularVelocity.y );
					var alignment = new Vector2( Vector2.Dot( up2, forward2 ), Vector2.Dot( up2, left2 ) );
					var rate = new Vector2( Vector2.Dot( spin2, left2 ), -Vector2.Dot( spin2, forward2 ) );
					var level = new Vector2( -alignment.x - 15 * rate.x, -alignment.y - 15 * rate.y );

					var weightA = MathF.Abs( level.x ) * MathF.Sign( controls.Throttle.x * level.x );
					var weightB = MathF.Abs( level.y ) * MathF.Sign( controls.Throttle.y * level.y );
					control.x += controls.Throttle.x * Math.Clamp( weightA + 1, 0.3f, 2.5f ) * 0.0015514038f;
					control.y += controls.Throttle.y * Math.Clamp( weightB + 1, 0.3f, 2.5f ) * 0.0015514038f;

					var levelScale = (1 - up.z) * 0.0038785094f;
					torqueA = levelScale * level.x + control.x;
					torqueB = levelScale * level.y;
				}
				else {
					torqueA = controls.Throttle.x * 0.0015514038f + control.x;
					torqueB = controls.Throttle.y * 0.0015514038f;
				}

				torqueB += control.y;

				var torque = left * (model.YyMoment * torqueA) + forward * -(model.XxMoment * torqueB);
				magicTorque += torque * (1 - Hover);
			}

			if ( Braking ) {
				var speed = Math.Clamp( Vector3.Dot( forward, body.Velocity ) / def.MaximumForwardSpeed, 0, 1 );
				if ( speed > 0 ) {
					magicTorque += left * (model.YyMoment * speed * Hover * -0.005817764f);
					magicForce += Vector3.Up * (model.Mass * speed * Hover * 0.004f);
				}

				if ( AirborneTicks > 0 ) {
					var axis = Vector3.Cross( left, Vector3.Up );
					if ( axis.Length > 0 ) {
						axis = axis.Normal;
						var fade = Math.Clamp( 1 - AirborneTicks * (1f / 30), 0, 1 );
						magicForce += axis * ((1 - Hover) * model.Mass * fade * 0.002f);
						magicForce += Vector3.Up * ((1 - Hover) * model.Mass * fade * 0.001f);
					}
				}
			}

			magicForce *= antigrav;
			magicTorque *= antigrav;
		}

		body.Update( world, _powered, magicForce, magicTorque );

		// Hover is the share of powered points antigraving, less when tipped, changing at most 0.1 a tick.
		var maximum = MathF.Max( body.Up.z, 0.4f );
		var powered = 0;
		var grounded = 0;
		for ( var i = 0; i < model.Points.Length; i++ ) {
			if ( model.Points[i].PoweredIndex < 0 ) continue;
			powered++;
			if ( body.Points[i].Antigraving ) grounded++;
		}

		var ratio = powered > 0 ? grounded / (float)powered : 0;
		var target = Math.Clamp( ratio * maximum, 0, 1 );
		Hover = Math.Clamp( target, Hover - 0.1f, Hover + 0.1f );
	}

	// The Banshee.
	void UpdateFighter( IMassPointWorld world, in VehicleControls controls ) {
		var def = Definition;
		var body = Body;
		var model = body.Model;

		if ( model.Powered.Length != 2 ) {
			body.Update( world, null, Vector3.Zero, Vector3.Zero );
			return;
		}

		// Velocity seeks forward × speed, countering gravity, limited by throttle; with no throttle it falls.
		var desiredVelocity = body.Forward * Speed;
		var throttle = Speed > 0 ? Speed / def.MaximumForwardSpeed : -(Speed / def.MaximumReverseSpeed);
		var acceleration = ComputeAcceleration( desiredVelocity, body.Velocity, throttle * def.SpeedAcceleration, throttle * def.SpeedDeceleration );
		var magicForce = acceleration * model.Mass * SeatPower0;

		// Orientation seeks the facing, pitched up by the fixed gun pitch for a player and rolled into the sideslip.
		var facing = controls.DesiredFacing.LengthSquared > 0 ? controls.DesiredFacing.Normal : body.Forward;
		var desiredForward = facing;
		var desiredUp = Vector3.Up - desiredForward * desiredForward.z;
		desiredUp = desiredUp.LengthSquared > 0 ? desiredUp.Normal : new Vector3( 1, 0, 0 );

		if ( !controls.DrivenByAi ) {
			var pitchSine = MathF.Sin( def.FixedGunPitch );
			var pitchCosine = MathF.Cos( def.FixedGunPitch );
			var pitchedForward = desiredForward * pitchCosine + desiredUp * pitchSine;
			desiredUp = desiredUp * pitchCosine - desiredForward * pitchSine;
			desiredForward = pitchedForward;
		}

		var yaw = (desiredForward.x * body.Velocity.y - desiredForward.y * body.Velocity.x) / def.MaximumForwardSpeed * def.MaximumLeftTurn;
		// Turn up about forward.
		desiredUp = desiredUp * MathF.Cos( yaw ) + Vector3.Cross( desiredForward, desiredUp ) * MathF.Sin( yaw );

		var desired = FromForwardUp( desiredForward, desiredUp );
		var current = FromForwardUp( body.Forward, body.Up );
		var delta = desired * current.Inverse;
		var w = Math.Clamp( delta.w, -1, 1 );
		var axis = new Vector3( delta.x, delta.y, delta.z );
		var sine = axis.Length;
		var angle = 2 * MathF.Atan2( sine, w );
		if ( angle > MathF.PI ) {
			angle = MathF.PI * 2 - angle;
			axis = -axis;
		}
		axis = sine > 1e-6f ? axis / sine : Vector3.Zero;

		var desiredAngularVelocity = axis * (angle * def.TurnRate * (1 / MathF.PI));
		var magicTorque = (desiredAngularVelocity - body.AngularVelocity) * ((model.ZzMoment + model.YyMoment + model.XxMoment) * (1f / 3)) * SeatPower0;

		var spin = body.AngularVelocity.Length / def.TurnRate;
		float thrustDelta;
		if ( spin > Thrust ) {
			thrustDelta = Math.Clamp( (1 - Thrust) * (1 - Thrust) * 0.2f, 0.01f, 0.05f );
			thrustDelta = MathF.Min( spin - Thrust, thrustDelta );
		}
		else {
			thrustDelta = -MathF.Max( Thrust * Thrust * 0.05f, 0.005f );
			thrustDelta = MathF.Max( spin - Thrust, thrustDelta );
		}
		Thrust += thrustDelta;

		_powered[0] = new PoweredMassPointState { AntigravFraction = SeatPower0 };
		_powered[1] = new PoweredMassPointState { AntigravFraction = SeatPower0 };
		body.Update( world, _powered, magicForce, magicTorque );
	}

	// WU per tick, gravity compensated; the limit blends from minimum to maximum as the change aligns with desired.
	public static Vector3 ComputeAcceleration( Vector3 desired, Vector3 current, float maximum, float minimum ) {
		var result = desired - current;
		result.z += MassPointBody.Gravity;

		var dot = Vector3.Dot( desired, result );
		if ( dot > 0.0001f && result.LengthSquared > 0 && desired.LengthSquared > 0 )
			maximum = (maximum - minimum) * (dot * dot / result.LengthSquared / desired.LengthSquared) + minimum;
		else
			maximum = minimum;

		return Limit( result, maximum );
	}

	// A grounded or antigraving point resets the airborne count.
	void ComputeAirborneTicks() {
		if ( AirborneTicks < 255 ) AirborneTicks++;

		foreach ( var point in Body.Points ) {
			if ( point.OnGround ) {
				AirborneTicks = 0;
				if ( OnGroundTicks < 255 ) OnGroundTicks++;
				return;
			}

			if ( point.Antigraving )
				AirborneTicks = 0;
		}

		OnGroundTicks = 0;
	}

	// For 15 ticks after coming to rest, the velocities die away.
	void SlowlyStop() {
		var body = Body;
		StopTime--;
		body.Velocity *= 0.835f;
		body.AngularVelocity *= 0.835f;

		var position = body.Position + body.Velocity;
		var magnitude = body.AngularVelocity.Length;
		var forward = body.Forward;
		var up = body.Up;
		if ( magnitude != 0 ) {
			var axis = body.AngularVelocity / magnitude;
			forward = MassPointBody.RotateAboutAxis( forward, axis, MathF.Sin( magnitude ), MathF.Cos( magnitude ) );
			up = MassPointBody.RotateAboutAxis( up, axis, MathF.Sin( magnitude ), MathF.Cos( magnitude ) );
		}

		if ( StopTime == 0 ) {
			body.Velocity = Vector3.Zero;
			body.AngularVelocity = Vector3.Zero;
		}

		body.Position = position;
		body.Forward = forward.Normal;
		body.Up = (up - body.Forward * Vector3.Dot( up, body.Forward )).Normal;
	}

	// Physics variables

	public readonly record struct SpeedParameters( float PositiveScale, float NegativeScale, float Acceleration, float Deceleration );

	// Accelerates toward delta's sign, decelerating first when moving the other way.
	public static void SpeedUpdate( ref float speed, SpeedParameters p, float delta ) {
		var magnitude = MathF.Abs( delta );
		var acceleration = magnitude * p.Acceleration;
		var deceleration = magnitude * p.Deceleration;

		if ( delta > 0 ) {
			if ( speed <= -deceleration ) speed += deceleration;
			else if ( speed >= 0 ) speed += acceleration;
			else speed = (speed / deceleration + 1) * acceleration;
			speed = MathF.Min( speed, magnitude * p.PositiveScale );
		}
		else if ( delta < 0 ) {
			if ( speed >= deceleration ) speed -= deceleration;
			else if ( speed <= 0 ) speed -= acceleration;
			else speed = (speed / deceleration - 1) * acceleration;
			speed = MathF.Max( -magnitude * p.NegativeScale, speed );
		}
	}

	public static void SpeedSeek( ref float speed, SpeedParameters p, float target, float delta ) {
		if ( speed > target ) {
			SpeedUpdate( ref speed, p, -delta );
			if ( speed <= target ) speed = target;
		}
		else if ( speed < target ) {
			SpeedUpdate( ref speed, p, delta );
			if ( speed >= target ) speed = target;
		}
	}

	// Non-wrapping, clamped to [lower, upper].
	public static void PositionSeek( ref float position, float upper, float lower, float target, float delta ) {
		var direction = MathF.Sign( target - position );
		if ( direction != 0 ) {
			position += direction * delta;
			if ( position < lower ) position = lower;
			else if ( position > upper ) position = upper;

			if ( MathF.Sign( target - position ) == direction )
				return;
		}

		position = target;
	}

	static float Wrap( float value, float circumference ) {
		if ( circumference <= 0 ) return 0;
		value %= circumference;
		if ( value < 0 ) value += circumference;
		return value;
	}

	static Vector3 Limit( Vector3 v, float length ) {
		var squared = v.LengthSquared;
		if ( squared > length * length )
			return squared > 0 ? v * (length / MathF.Sqrt( squared )) : v;
		return v;
	}

	// x forward, z up.
	static Rotation FromForwardUp( Vector3 forward, Vector3 up ) => Rotation.LookAt( forward, up );
}
