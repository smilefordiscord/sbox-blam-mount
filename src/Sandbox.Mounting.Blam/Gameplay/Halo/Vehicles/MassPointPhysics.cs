using System;

namespace Halo.Vehicles;

// Halo's vehicle physics: spheres ("mass points") fixed to the vehicle, pushed by ground, water and air.
// Their forces sum into one force and torque that move the vehicle a tick at a time.
// Units are world units and 30 Hz ticks, with x forward, y left, z up.
// The world is reached only through IMassPointWorld, so tools/vehicletests runs the same code.

public enum MassPointFriction {
	Point = 0,
	Forward = 1,
	Left = 2,
	Up = 3,
}

// A mass point of a physics tag, in the object's space (world units).
public sealed class MassPointDefinition {
	public string Name;
	// Index into MassPointModel.Powered, or -1.
	public int PoweredIndex = -1;
	// The "metallic" flag (bit 0), which picks the slipping effect.
	public bool Metallic;
	public float Mass;
	public float Density;
	public Vector3 Position;
	public Vector3 Forward = new( 1, 0, 0 );
	public Vector3 Up = new( 0, 0, 1 );
	public MassPointFriction FrictionType;
	public float FrictionParallelScale = 1;
	public float FrictionPerpendicularScale = 1;
	public float Radius;
}

// The engine one or more mass points share.
public sealed class PoweredMassPointDefinition {
	public string Name;
	public bool GroundFriction, WaterFriction, AirFriction, WaterLift, AirLift, Thrust, Antigrav;
	public float AntigravStrength;
	public float AntigravOffset;
	public float AntigravHeight;
	public float AntigravDampFraction;
	public float AntigravNormalK1;
	public float AntigravNormalK0;
}

// A physics tag (phys), with its inverse inertia tensor.
public sealed class MassPointModel {
	// Above zero selects Halo's older physics path, which isn't ported; every multiplayer vehicle has -1.
	public float Radius = -1;
	public float MomentScale = 1;
	public float Mass;
	public Vector3 CenterOfMass;
	public float GravityScale = 1;
	public float GroundFriction;
	public float GroundDepth;
	public float GroundDampFraction;
	public float GroundNormalK1;
	public float GroundNormalK0;
	public float WaterFriction;
	public float WaterDepth;
	public float WaterDensity;
	public float AirFriction;
	public float XxMoment, YyMoment, ZzMoment;
	public PoweredMassPointDefinition[] Powered = Array.Empty<PoweredMassPointDefinition>();
	public MassPointDefinition[] Points = Array.Empty<MassPointDefinition>();

	// The inverse inertia tensor in the object's space, rows forward, left, up.
	public Vector3 InverseInertiaX, InverseInertiaY, InverseInertiaZ;

	// Inertia from the mass points as solid spheres, as the map compiler builds it, with r from the center of mass.
	// Diagonal: moment scale × Σ m (|r|² + 0.4 R²); off diagonal: −m r r.
	// The diagonal matches every multiplayer vehicle's tag xx/yy/zz moments exactly.
	public void ComputeInertia() {
		double xx = 0, yy = 0, zz = 0, xy = 0, xz = 0, yz = 0;
		foreach ( var p in Points ) {
			var r = p.Position - CenterOfMass;
			var sphere = 0.4 * p.Radius * p.Radius;
			double m = p.Mass * MomentScale;
			xx += m * (r.y * r.y + r.z * r.z + sphere);
			yy += m * (r.x * r.x + r.z * r.z + sphere);
			zz += m * (r.x * r.x + r.y * r.y + sphere);
			xy -= m * r.x * r.y;
			xz -= m * r.x * r.z;
			yz -= m * r.y * r.z;
		}

		// The inverse of the symmetric 3x3.
		var c00 = yy * zz - yz * yz;
		var c01 = xz * yz - xy * zz;
		var c02 = xy * yz - xz * yy;
		var det = xx * c00 + xy * c01 + xz * c02;
		if ( Math.Abs( det ) < 1e-12 ) {
			InverseInertiaX = InverseInertiaY = InverseInertiaZ = Vector3.Zero;
			return;
		}

		var c11 = xx * zz - xz * xz;
		var c12 = xy * xz - xx * yz;
		var c22 = xx * yy - xy * xy;
		InverseInertiaX = new Vector3( (float)(c00 / det), (float)(c01 / det), (float)(c02 / det) );
		InverseInertiaY = new Vector3( (float)(c01 / det), (float)(c11 / det), (float)(c12 / det) );
		InverseInertiaZ = new Vector3( (float)(c02 / det), (float)(c12 / det), (float)(c22 / det) );
	}
}

// A globals material's physics scales.
public struct MaterialPhysics {
	public float GroundFrictionScale;
	public float GroundFrictionNormalK1Scale;
	public float GroundFrictionNormalK0Scale;
	public float GroundDepthScale;
	public float GroundDampFractionScale;
}

// What a mass point sinks into.
public struct GroundContact {
	// The plane's normal: away from the feature the point is deepest in.
	public Vector3 Normal;
	// How far the point's sphere reaches into it, world units.
	public float Depth;
	// The Halo material type, or -1.
	public int Material;
	// A breakable surface or a moving object: the vehicle can't rest on it.
	public bool Volatile;
}

// The world as Halo's vehicle physics sees it: structure, scenery and machines, never bipeds, items or other vehicles.
public interface IMassPointWorld {
	// The face, edge or vertex a sphere reaches deepest into; depth is the radius less the distance to it.
	bool GroundPlane( Vector3 center, float radius, out GroundContact contact );

	// The first front facing surface along a segment, as a fraction of the way along it.
	bool TestVector( Vector3 start, Vector3 delta, out float t, out Vector3 normal );

	// How far below the water's surface a point is, or 0.
	float WaterDepth( Vector3 point );

	// The globals' physics scales for a material type (zeros leave the tag's values).
	MaterialPhysics Material( int material );

	// The structure BSP's vehicle floor and ceiling heights; 0 is none.
	float VehicleFloor { get; }
	float VehicleCeiling { get; }
}

// Per tick state of a powered mass point.
public struct PoweredMassPointState {
	public float GroundFrictionVelocity;
	public float WaterFrictionVelocity;
	public float AirFrictionVelocity;
	public float WaterLiftRatio;
	public float AirLiftRatio;
	public float ThrustFraction;
	public float AntigravFraction;
	// Turns the powered points about up, as the Warthog steers.
	public float Yaw;
}

// Per tick state of a mass point.
public struct MassPointState {
	public bool AtRest, OnGround, OnVolatileSurface, InWater, Antigraving;
	public Vector3 Position;
	public Vector3 Forward;
	public Vector3 Up;
	// From the object's center of mass to the point.
	public Vector3 Arm;
	public Vector3 Velocity;
	public Vector3 VelocityRelativeToGround;
	public Vector3 GroundNormal;
	public float GroundDepth;
	public int GroundMaterial;
	public float WaterDepth;
	public float NormalForceMagnitude;
	public Vector3 Force;
}

// One object moved by mass point physics, on the path every multiplayer vehicle takes (physics radius ≤ 0).
public sealed class MassPointBody {
	public const float Gravity = 0.0035651792f;
	public const float CollisionDepth = 0.2f;
	public const float DepthsOfHell = -256f;

	public readonly MassPointModel Model;
	public readonly MassPointState[] Points;

	// The center of mass (Halo's object position for an object with physics).
	public Vector3 Position;
	public Vector3 Forward = new( 1, 0, 0 );
	public Vector3 Up = new( 0, 0, 1 );
	// World units per tick.
	public Vector3 Velocity;
	// An axis times radians per tick.
	public Vector3 AngularVelocity;

	// Physics is skipped until something wakes it.
	public bool AtRest;
	public bool OnGround;
	public bool InWater;
	public bool WhollyUnderWater;
	public uint StuckMassPointFlags;

	// Other vehicles' pushes, added to the next update's force.
	public Vector3 CollisionForce;
	public Vector3 CollisionTorque;

	// The last update's acceleration, for the rest test.
	public Vector3 LinearAcceleration, AngularAcceleration;

	public MassPointBody( MassPointModel model ) {
		Model = model;
		Points = new MassPointState[model.Points.Length];
	}

	public Vector3 Left => Vector3.Cross( Up, Forward );

	// A point in the model's space, in the world.
	public Vector3 PointToWorld( Vector3 local ) => PointToWorld( Position, Forward, Up, local );

	public Vector3 PointToWorld( Vector3 position, Vector3 forward, Vector3 up, Vector3 local ) {
		var p = local - Model.CenterOfMass;
		return position + forward * p.x + Vector3.Cross( up, forward ) * p.y + up * p.z;
	}

	public Vector3 VectorToWorld( Vector3 local ) => Forward * local.x + Left * local.y + Up * local.z;

	// The model's origin in the world: the center of mass less its offset.
	public Vector3 Origin => PointToWorld( Vector3.Zero );

	// Puts the model's origin, not the center of mass, at a point.
	public void SetOrigin( Vector3 origin, Vector3 forward, Vector3 up ) {
		Forward = forward.Normal;
		Up = up.Normal;
		var c = Model.CenterOfMass;
		Position = origin + Forward * c.x + Left * c.y + Up * c.z;
	}

	// One tick under the mass point forces, other vehicles' pushes and the vehicle's "magic" force and torque.
	public void Update( IMassPointWorld world, PoweredMassPointState[] powered, Vector3 magicForce, Vector3 magicTorque ) {
		Compute( world, powered, out var force, out var torque );

		force += CollisionForce + magicForce;
		torque += CollisionTorque + magicTorque;
		CollisionForce = Vector3.Zero;
		CollisionTorque = Vector3.Zero;

		Integrate( world, force, torque );
	}

	void Compute( IMassPointWorld world, PoweredMassPointState[] powered, out Vector3 totalForce, out Vector3 totalTorque ) {
		var model = Model;
		var gravity = model.GravityScale * Gravity;
		totalForce = new Vector3( 0, 0, -model.Mass * gravity );
		totalTorque = Vector3.Zero;

		var left = Left;
		for ( var i = 0; i < model.Points.Length; i++ ) {
			var definition = model.Points[i];
			ref var point = ref Points[i];
			point = default;

			PoweredMassPointDefinition poweredDefinition = null;
			var poweredState = default( PoweredMassPointState );
			if ( definition.PoweredIndex >= 0 && powered is not null && definition.PoweredIndex < model.Powered.Length && definition.PoweredIndex < powered.Length ) {
				poweredDefinition = model.Powered[definition.PoweredIndex];
				poweredState = powered[definition.PoweredIndex];
			}

			point.Position = PointToWorld( definition.Position );
			var localForward = definition.Forward;
			var localUp = definition.Up;
			if ( poweredDefinition is not null && poweredState.Yaw != 0 ) {
				localForward = YawLocal( localForward, poweredState.Yaw );
				localUp = YawLocal( localUp, poweredState.Yaw );
			}
			point.Forward = Forward * localForward.x + left * localForward.y + Up * localForward.z;
			point.Up = Forward * localUp.x + left * localUp.y + Up * localUp.z;

			point.Arm = point.Position - Position;
			point.Velocity = Velocity + Vector3.Cross( AngularVelocity, point.Arm );

			// The floor of the world at z = -256, unless something closer is found.
			point.GroundNormal = Vector3.Up;
			point.GroundDepth = definition.Radius - (point.Position.z - DepthsOfHell);
			point.GroundMaterial = -1;
			if ( world.GroundPlane( point.Position, definition.Radius, out var contact ) ) {
				point.GroundNormal = contact.Normal;
				point.GroundDepth = contact.Depth;
				point.GroundMaterial = contact.Material;
				point.OnVolatileSurface = contact.Volatile;
			}
			point.WaterDepth = world.WaterDepth( point.Position );

			var normalForce = Vector3.Zero;
			var groundFriction = Vector3.Zero;
			if ( point.GroundDepth > 0 ) {
				var material = world.Material( point.GroundMaterial );
				var friction = model.GroundFriction;
				var k1 = model.GroundNormalK1;
				var k0 = model.GroundNormalK0;
				var depth = model.GroundDepth;
				var damp = model.GroundDampFraction;
				if ( material.GroundFrictionScale > 0 && model.Mass <= 7500 ) friction *= material.GroundFrictionScale;
				if ( material.GroundFrictionNormalK1Scale > 0 ) k1 *= material.GroundFrictionNormalK1Scale;
				if ( material.GroundFrictionNormalK0Scale > 0 ) k0 *= material.GroundFrictionNormalK0Scale;
				if ( material.GroundDepthScale > 0 ) depth *= material.GroundDepthScale;
				if ( material.GroundDampFractionScale > 0 ) damp *= material.GroundDampFractionScale;

				var n = point.GroundNormal;
				var normalVelocity = Vector3.Dot( point.Velocity, n );
				point.NormalForceMagnitude = model.Mass * (Gravity / depth * point.GroundDepth - normalVelocity * damp);
				normalForce = n * point.NormalForceMagnitude;

				point.VelocityRelativeToGround = point.Velocity - n * normalVelocity;
				var groundScale = -definition.Mass * friction;
				groundFriction = point.VelocityRelativeToGround * groundScale;

				if ( poweredDefinition is not null && poweredDefinition.GroundFriction && poweredState.GroundFrictionVelocity != 0 ) {
					var fraction = PinFraction( n.z, k0, k1 );
					var alignment = Math.Clamp( Vector3.Dot( point.Up, n ), 0, 1 );
					var weight = alignment * alignment * fraction * fraction * groundScale;
					var poweredVelocity = point.Forward * -poweredState.GroundFrictionVelocity;
					var projected = poweredVelocity - n * Vector3.Dot( poweredVelocity, n );
					point.VelocityRelativeToGround += projected;
					groundFriction += projected * weight;
				}

				groundFriction = FrictionEvaluate( definition, groundFriction, point.Forward, point.Up );
			}

			var waterPressure = Vector3.Zero;
			var waterFriction = Vector3.Zero;
			var poweredForce = Vector3.Zero;
			if ( point.WaterDepth > 0 ) {
				var depthFraction = point.WaterDepth < model.WaterDepth ? point.WaterDepth / model.WaterDepth : 1;
				var waterScale = -definition.Mass * model.WaterFriction;

				if ( definition.Density > 0 && model.WaterDepth > 0 )
					waterPressure = new Vector3( 0, 0, definition.Mass / definition.Density * model.WaterDensity * depthFraction * gravity );

				if ( poweredDefinition is not null && poweredDefinition.WaterFriction && poweredState.WaterFrictionVelocity != 0 )
					waterFriction = (point.Velocity - point.Forward * poweredState.WaterFrictionVelocity) * waterScale;
				else
					waterFriction = point.Velocity * waterScale;

				waterFriction = FrictionEvaluate( definition, waterFriction, point.Forward, point.Up );

				if ( poweredDefinition is not null && poweredDefinition.WaterLift && poweredState.WaterLiftRatio != 0 ) {
					var lift = MathF.Abs( Vector3.Dot( point.Forward, point.Velocity ) ) * poweredState.WaterLiftRatio * model.Mass * depthFraction;
					poweredForce += point.Up * lift;
				}
			}

			Vector3 airFriction;
			{
				var airScale = -definition.Mass * model.AirFriction;
				if ( poweredDefinition is not null && poweredDefinition.AirFriction && poweredState.AirFrictionVelocity != 0 )
					airFriction = (point.Velocity - point.Forward * poweredState.AirFrictionVelocity) * airScale;
				else
					airFriction = point.Velocity * airScale;

				airFriction = FrictionEvaluate( definition, airFriction, point.Forward, point.Up );

				if ( poweredDefinition is not null && poweredDefinition.AirLift && poweredState.AirLiftRatio != 0 ) {
					var lift = MathF.Abs( Vector3.Dot( point.Forward, point.Velocity ) ) * model.Mass * poweredState.AirLiftRatio;
					poweredForce += point.Up * lift;
				}
			}

			point.AtRest = point.Velocity.LengthSquared < 0.0011111111f;
			point.OnGround = point.GroundDepth > 0;
			point.InWater = point.WaterDepth > 0;

			if ( poweredDefinition is not null ) {
				if ( poweredDefinition.Thrust )
					poweredForce += point.Forward * (poweredState.ThrustFraction * model.Mass);

				if ( poweredDefinition.Antigrav ) {
					var probeLength = definition.Radius + poweredDefinition.AntigravHeight;
					if ( world.TestVector( point.Position, Vector3.Down * probeLength, out var t, out var normal ) ) {
						var height = probeLength * t - definition.Radius;
						var alignment = PinFraction( point.Up.z, poweredDefinition.AntigravNormalK0, poweredDefinition.AntigravNormalK1 );
						var groundEffect = height > 0 ? 1 - height / poweredDefinition.AntigravHeight : 1;
						var magnitude = (groundEffect * groundEffect * Gravity - Vector3.Dot( normal, point.Velocity ) * poweredDefinition.AntigravDampFraction)
							* poweredState.AntigravFraction * poweredDefinition.AntigravStrength * model.Mass * alignment;
						poweredForce += normal * magnitude;
						point.Antigraving = true;
					}
				}
			}

			point.Force = normalForce + groundFriction + waterPressure + waterFriction + airFriction + poweredForce;
			totalForce += point.Force;
			totalTorque += Vector3.Cross( point.Arm, point.Force );
		}
	}

	// Applies the force and torque, then moves without letting any mass point pass through a front facing surface.
	// Each of up to four tries stops short of the first hit and removes the velocity into it.
	// If all four hit, the vehicle stays put.
	void Integrate( IMassPointWorld world, Vector3 totalForce, Vector3 totalTorque ) {
		var model = Model;
		var linearAcceleration = totalForce * (1 / model.Mass);
		var linearVelocity = Velocity + linearAcceleration;
		var position = Position + linearVelocity;

		var angularAcceleration = WorldInverseInertia( totalTorque );
		var angularVelocity = AngularVelocity + angularAcceleration;
		RotateByAngularVelocity( Forward, Up, angularVelocity, out var forward, out var up );

		Velocity = linearVelocity;
		AngularVelocity = angularVelocity;

		uint stuck = 0;
		for ( var pass = 0; pass < 4; pass++ ) {
			stuck = 0;
			var found = false;
			var worstT = 0f;
			var worstDelta = Vector3.Zero;
			var worstNormal = Vector3.Zero;

			for ( var i = 0; i < model.Points.Length; i++ ) {
				var swept = PointToWorld( position, forward, up, model.Points[i].Position );
				var delta = swept - Points[i].Position;
				if ( delta.LengthSquared <= 0 ) continue;

				if ( world.TestVector( Points[i].Position, delta, out var t, out var normal ) ) {
					stuck |= 1u << i;
					if ( !found || worstT > t ) {
						found = true;
						worstT = t;
						worstDelta = delta;
						worstNormal = normal;
					}
				}
			}

			if ( !found ) {
				Position = position;
				Forward = forward;
				Up = up;
				break;
			}

			var normalDotDelta = Vector3.Dot( worstDelta, worstNormal );
			var epsilon = normalDotDelta != 0 ? 0.0078125f / MathF.Abs( normalDotDelta ) : 0.03125f;
			var fraction = MathF.Max( worstT - epsilon, 0 );
			var normalDotVelocity = Vector3.Dot( worstNormal, linearVelocity );
			if ( normalDotVelocity < 0 ) {
				linearVelocity += worstNormal * ((fraction - 1) * normalDotVelocity);
				Velocity = linearVelocity;
				position = Position + linearVelocity;
			}

			angularVelocity *= fraction;
			AngularVelocity = angularVelocity;
			RotateByAngularVelocity( Forward, Up, angularVelocity, out forward, out up );
		}

		StuckMassPointFlags = stuck;

		var atRest = 0;
		var onGround = 0;
		var onVolatile = 0;
		var inWater = 0;
		foreach ( var point in Points ) {
			if ( point.AtRest ) atRest++;
			if ( point.OnGround ) onGround++;
			if ( point.OnVolatileSurface ) onVolatile++;
			if ( point.InWater ) inWater++;
		}

		LinearAcceleration = linearAcceleration;
		AngularAcceleration = angularAcceleration;
		AtRest = atRest == Points.Length && onGround >= 3 && onVolatile == 0 &&
			linearVelocity.LengthSquared <= 0.0011111111f &&
			angularVelocity.LengthSquared <= 0.0027415568f &&
			linearAcceleration.LengthSquared <= 0.00000030864197f &&
			angularAcceleration.LengthSquared <= 0.0000030461742f;
		OnGround = onGround > 0;
		InWater = inWater > 0;
		WhollyUnderWater = inWater == Points.Length;
	}

	// The world's inverse inertia times a torque: frame × I⁻¹ × frameᵀ.
	public Vector3 WorldInverseInertia( Vector3 torque ) {
		var left = Left;
		var local = new Vector3( Vector3.Dot( torque, Forward ), Vector3.Dot( torque, left ), Vector3.Dot( torque, Up ) );
		var m = Model;
		var a = new Vector3( Vector3.Dot( m.InverseInertiaX, local ), Vector3.Dot( m.InverseInertiaY, local ), Vector3.Dot( m.InverseInertiaZ, local ) );
		return Forward * a.x + left * a.y + Up * a.z;
	}

	// This vehicle's half of a vehicle collision: overlapping mass point spheres push apart.
	// The push is 2 √(m₀m₁) g / 0.2 per world unit of half the overlap.
	// Returns whether any touched.
	public bool AddVehicleCollision( MassPointBody other ) {
		var massScale = MathF.Sqrt( Model.Mass * other.Model.Mass );
		var force = Vector3.Zero;
		var torque = Vector3.Zero;
		var collision = false;

		foreach ( var p0 in Model.Points ) {
			var point0 = PointToWorld( p0.Position );
			foreach ( var p1 in other.Model.Points ) {
				var radius = p0.Radius + p1.Radius;
				var point1 = other.PointToWorld( p1.Position );
				var direction = point1 - point0;
				var distance = direction.Length;
				if ( distance >= radius || distance <= 0 ) continue;

				direction /= distance;
				var penetration = (radius - distance) * 0.5f;
				var magnitude = 2 * massScale * Gravity / CollisionDepth * penetration;
				var collisionForce = direction * -magnitude;
				var collisionPoint = point0 + direction * (p0.Radius - penetration);
				force += collisionForce;
				torque += Vector3.Cross( collisionPoint - Position, collisionForce );
				collision = true;
			}
		}

		if ( collision ) {
			CollisionForce += force;
			CollisionTorque += torque;
		}
		return collision;
	}

	// A change of velocity, which also spins the vehicle about the horizontal.
	public void Accelerate( Vector3 acceleration ) {
		Velocity += acceleration;
		var torque = Vector3.Cross( Vector3.Up, acceleration );
		if ( torque.LengthSquared > 0 )
			AngularVelocity += torque * MathF.PI;
		AtRest = false;
	}

	// Scales a point's friction separately along and across its friction axis.
	static Vector3 FrictionEvaluate( MassPointDefinition definition, Vector3 friction, Vector3 forward, Vector3 up ) {
		Vector3 axis;
		switch ( definition.FrictionType ) {
			case MassPointFriction.Forward: axis = forward; break;
			case MassPointFriction.Left: axis = Vector3.Cross( up, forward ); break;
			case MassPointFriction.Up: axis = up; break;
			default: return friction;
		}

		var parallel = axis * Vector3.Dot( friction, axis );
		var perpendicular = friction - parallel;
		return parallel * definition.FrictionParallelScale + perpendicular * definition.FrictionPerpendicularScale;
	}

	// Where value sits between begin and end, clamped to 0..1; begin may be above end.
	public static float PinFraction( float value, float begin, float end ) {
		if ( begin < end ) {
			if ( value <= begin ) return 0;
			if ( value >= end ) return 1;
			return (value - begin) / (end - begin);
		}

		if ( value <= end ) return 1;
		if ( value >= begin ) return 0;
		return (begin - value) / (begin - end);
	}

	// A local vector turned about local z, for a powered point's yaw.
	static Vector3 YawLocal( Vector3 v, float angle ) {
		var c = MathF.Cos( angle );
		var s = MathF.Sin( angle );
		return new Vector3( v.x * c - v.y * s, v.x * s + v.y * c, v.z );
	}

	// Turns forward and up by one tick of angular velocity.
	public static void RotateByAngularVelocity( Vector3 forward, Vector3 up, Vector3 angularVelocity, out Vector3 rotatedForward, out Vector3 rotatedUp ) {
		var magnitude = angularVelocity.Length;
		if ( magnitude == 0 ) {
			rotatedForward = forward;
			rotatedUp = up;
			return;
		}

		var axis = angularVelocity / magnitude;
		rotatedForward = RotateAboutAxis( forward, axis, MathF.Sin( magnitude ), MathF.Cos( magnitude ) ).Normal;
		rotatedUp = RotateAboutAxis( up, axis, MathF.Sin( magnitude ), MathF.Cos( magnitude ) );
		rotatedUp = (rotatedUp - rotatedForward * Vector3.Dot( rotatedUp, rotatedForward )).Normal;
	}

	// Right handed.
	public static Vector3 RotateAboutAxis( Vector3 v, Vector3 n, float sine, float cosine ) =>
		v * cosine + n * (Vector3.Dot( n, v ) * (1 - cosine)) + Vector3.Cross( n, v ) * sine;
}
