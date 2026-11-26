using System;

namespace Halo.Vehicles;

// Halo's observer, the camera the director's cameras steer with a target and a time for each of five parameters.
// A parameter with no time left snaps; otherwise a quintic refitted each frame eases it into a moving target.
// The camera sits the focus distance back from the offset focus, pulled in where the level is in the way.
// Units are Halo's: world units, seconds, radians, and a 4:3 horizontal field of view.
public sealed class HaloObserver {
	public const byte TimeValid = 1, TimeForce = 2;
	public const int Focus = 0, Offset = 1, Distance = 2, FieldOfView = 3, Orientation = 4;

	static readonly int[] RealCounts = { 3, 3, 1, 1, 6 };
	static readonly int[] DerivativeCounts = { 3, 3, 1, 1, 3 };
	static readonly float[] MaximumAccelerations = { 1500, 1500, 100000, 100000, 100000 };
	const float SineRegionAngle = 0.174f;
	const int Values = 11;

	public sealed class Command {
		public bool Valid;
		public bool ForceTime;
		public bool IgnoreObstructions;
		// Focus position (3), focus offset (3), focus distance, field of view, forward (3), up (3).
		public readonly float[] Parameters = new float[14];
		// The focus's velocity, WU per tick, which the focus eases into.
		public Vector3 FocusVelocity;
		public float Timer;
		public readonly byte[] Flags = new byte[5];
		public readonly float[] Timers = new float[5];

		public Vector3 FocusPosition { get => Get( 0 ); set => Set( 0, value ); }
		public Vector3 FocusOffset { get => Get( 3 ); set => Set( 3, value ); }
		public float FocusDistance { get => Parameters[6]; set => Parameters[6] = value; }
		public float Fov { get => Parameters[7]; set => Parameters[7] = value; }
		public Vector3 Forward { get => Get( 8 ); set => Set( 8, value ); }
		public Vector3 Up { get => Get( 11 ); set => Set( 11, value ); }

		Vector3 Get( int i ) => new( Parameters[i], Parameters[i + 1], Parameters[i + 2] );
		void Set( int i, Vector3 v ) { Parameters[i] = v.x; Parameters[i + 1] = v.y; Parameters[i + 2] = v.z; }

		public void Clear() {
			Valid = ForceTime = IgnoreObstructions = false;
			Array.Clear( Parameters );
			FocusVelocity = default;
			Timer = 0;
			Array.Clear( Flags );
			Array.Clear( Timers );
		}

		public void CopyTo( Command other ) {
			other.Valid = Valid;
			other.ForceTime = ForceTime;
			other.IgnoreObstructions = IgnoreObstructions;
			Array.Copy( Parameters, other.Parameters, Parameters.Length );
			other.FocusVelocity = FocusVelocity;
			other.Timer = Timer;
			Array.Copy( Flags, other.Flags, Flags.Length );
			Array.Copy( Timers, other.Timers, Timers.Length );
		}
	}

	// A ray against front faces of the structure and scenery only, giving the hit's fraction along it.
	public delegate bool CollisionTest( Vector3 from, Vector3 to, out float t );

	readonly float[] _p = new float[14];
	readonly Command _last = new();
	readonly float[] _velocities = new float[Values];
	readonly float[] _accelerations = new float[Values];
	readonly float[] _displacements = new float[Values];
	readonly float[] _a = new float[Values], _b = new float[Values], _c = new float[Values];
	readonly float[] _d = new float[Values], _e = new float[Values], _f = new float[Values];
	bool _firstCommand;

	public Vector3 Position { get; private set; }
	public Vector3 ResultForward { get; private set; }
	public Vector3 ResultUp { get; private set; }
	public float ResultFov { get; private set; }

	// Whether any parameter is still on its way to its target.
	public bool Moving => _last.Timers.Any( x => x > 0 );

	public HaloObserver() => Clear();

	// Forward along x, up z, a 50 degree field of view, and the next command snaps.
	public void Clear() {
		Array.Clear( _p );
		SetVector( 8, Vector3.Forward );
		SetVector( 11, Vector3.Up );
		_p[7] = 50f.DegreeToRadian();
		ResultForward = Vector3.Forward;
		ResultUp = Vector3.Up;
		ResultFov = _p[7];

		_last.Clear();
		Array.Copy( _p, _last.Parameters, _p.Length );
		Array.Clear( _velocities );
		Array.Clear( _accelerations );
		Array.Clear( _displacements );
		_firstCommand = false;
	}

	// Takes the pending command and advances one frame; the command's timers are rewritten as Halo does.
	public void Update( Command pending, float dt, CollisionTest collision ) {
		if ( !_firstCommand ) {
			_firstCommand = true;
			pending.Timer = 0;
			pending.ForceTime = true;
			Array.Clear( pending.Timers );
		}

		UpdateCommand( pending );
		if ( dt > 0 ) PassTime( dt );
		Postcheck( collision );
	}

	void UpdateCommand( Command pending ) {
		if ( !pending.Valid ) return;

		for ( var i = 0; i < 5; i++ ) {
			if ( (pending.Flags[i] & TimeValid) != 0 ) {
				if ( (pending.Flags[i] & TimeForce) == 0 && pending.Timers[i] < _last.Timers[i] )
					pending.Timers[i] = MathF.Min( _last.Timers[i], 2 );
			}
			else if ( pending.Timer < _last.Timers[i] && !pending.ForceTime )
				pending.Timers[i] = MathF.Min( _last.Timers[i], 2 );
			else
				pending.Timers[i] = pending.Timer;
		}

		pending.CopyTo( _last );
	}

	void PassTime( float dt ) {
		FindDisplacement();
		UpdatePolynomial( dt );
		UpdateAccelerations( dt );
		UpdateVelocities( dt );
		UpdatePositions( dt );

		for ( var i = 0; i < 5; i++ )
			_last.Timers[i] = MathF.Max( 0, _last.Timers[i] - dt );
	}

	// Target minus current, with orientation as the rotation from current to target.
	void FindDisplacement() {
		for ( var i = 0; i < 8; i++ )
			_displacements[i] = _last.Parameters[i] - _p[i];

		var rotation = RotationalDisplacement( GetVector( _p, 8 ), GetVector( _p, 11 ), GetVector( _last.Parameters, 8 ), GetVector( _last.Parameters, 11 ) );
		_displacements[8] = rotation.x;
		_displacements[9] = rotation.y;
		_displacements[10] = rotation.z;
	}

	void UpdatePolynomial( float dt ) {
		var v = 0;
		for ( var p = 0; p < 5; p++ ) {
			var count = DerivativeCounts[p];
			var timer = _last.Timers[p];
			if ( _last.Valid && timer > dt ) {
				var t1 = 1 / timer;
				var t2 = t1 * t1;
				var t3 = t2 * t1;
				var t4 = t3 * t1;
				var t5 = t4 * t1;
				for ( var i = v; i < v + count; i++ ) {
					var acc = _accelerations[i];
					var vel = _velocities[i];
					var dis = _displacements[i];
					_a[i] = acc * t3 * 0.5f - (vel * t4 * 3 + dis * t5 * 6);
					_b[i] = vel * t3 * 7 + dis * t4 * 15 - acc * t2;
					_c[i] = acc * t1 * 0.5f - (vel * t2 * 4 + dis * t3 * 10);
					_d[i] = 0;
					_e[i] = 0;
					_f[i] = dis;

					if ( p == Focus ) {
						var component = i - v;
						var final = (component == 0 ? _last.FocusVelocity.x : component == 1 ? _last.FocusVelocity.y : _last.FocusVelocity.z) * 30;
						_a[i] -= 3 * final * t4;
						_b[i] += 8 * final * t3;
						_c[i] -= 6 * final * t2;
						_e[i] += final;
					}
				}
			}
			v += count;
		}
	}

	void UpdateAccelerations( float dt ) {
		var v = 0;
		for ( var p = 0; p < 5; p++ ) {
			var count = DerivativeCounts[p];
			var r = _last.Timers[p] - dt;
			if ( r > 0 ) {
				var r2 = r * r;
				var r3 = r2 * r;
				for ( var i = v; i < v + count; i++ ) {
					_accelerations[i] = 20 * _a[i] * r3 + 12 * _b[i] * r2 + 6 * _c[i] * r + 2 * _d[i];
					if ( MathF.Abs( _accelerations[i] ) > MaximumAccelerations[p] ) {
						// Too sharp: this parameter, and any timed with it, snap instead.
						var timer = _last.Timers[p];
						for ( var other = 0; other < 5; other++ )
							if ( other != p && _last.Timers[other] == timer ) _last.Timers[other] = 0;
						_last.Timers[p] = 0;
					}
				}
			}
			else {
				for ( var i = v; i < v + count; i++ ) _accelerations[i] = 0;
			}
			v += count;
		}
	}

	void UpdateVelocities( float dt ) {
		var v = 0;
		for ( var p = 0; p < 5; p++ ) {
			var count = DerivativeCounts[p];
			var r = _last.Timers[p] - dt;
			if ( r > 0 ) {
				var r2 = r * r;
				var r3 = r2 * r;
				var r4 = r3 * r;
				for ( var i = v; i < v + count; i++ )
					_velocities[i] = 5 * _a[i] * r4 + 4 * _b[i] * r3 + 3 * _c[i] * r2 + 2 * _d[i] * r + _e[i];
			}
			else if ( _last.Valid && ((_last.Flags[p] & TimeForce) != 0 || _last.ForceTime) ) {
				for ( var i = v; i < v + count; i++ ) _velocities[i] = 0;
			}
			else if ( _last.Valid ) {
				for ( var i = v; i < v + count; i++ ) _velocities[i] = -_displacements[i] / dt;
			}
			v += count;
		}
	}

	void UpdatePositions( float dt ) {
		Span<float> delta = stackalloc float[Values];
		var v = 0;
		var position = 0;
		for ( var p = 0; p < 5; p++ ) {
			var count = DerivativeCounts[p];
			var reals = RealCounts[p];
			var r = _last.Timers[p] - dt;
			var snapped = false;

			if ( r > 0 ) {
				var r2 = r * r;
				var r3 = r2 * r;
				var r4 = r3 * r;
				var r5 = r4 * r;
				for ( var i = v; i < v + count; i++ )
					delta[i] = _a[i] * r5 + _b[i] * r4 + _c[i] * r3 + _d[i] * r2 + _e[i] * r + _f[i];
			}
			else if ( _last.Valid ) {
				for ( var i = 0; i < reals; i++ ) _p[position + i] = _last.Parameters[position + i];
				snapped = true;
			}
			else {
				for ( var i = v; i < v + count; i++ ) delta[i] = -_velocities[i] * dt;
			}

			if ( !snapped ) {
				if ( p == Orientation ) {
					var axis = new Vector3( delta[v], delta[v + 1], delta[v + 2] );
					var angle = axis.Length;
					if ( angle != 0 ) {
						var rotation = Rotation.FromAxis( axis / angle, angle.RadianToDegree() );
						SetVector( 8, rotation * GetVector( _p, 8 ) );
						SetVector( 11, rotation * GetVector( _p, 11 ) );
					}
				}
				else {
					for ( var i = 0; i < count; i++ ) _p[position + i] += delta[v + i];
				}
			}

			v += count;
			position += reals;
		}

		// Re-orthonormalize the axes when they drift.
		var forward = GetVector( _p, 8 );
		var up = GetVector( _p, 11 );
		if ( MathF.Abs( forward.Length - 1 ) > 0.0001f || MathF.Abs( up.Length - 1 ) > 0.0001f || MathF.Abs( Vector3.Dot( forward, up ) ) > 0.0001f ) {
			var left = Vector3.Cross( up, forward );
			up = Vector3.Cross( forward, left );
			SetVector( 8, forward.Normal );
			SetVector( 11, up.Normal );
		}
	}

	// Offsets the focus in the heading's frame (x forward, y right, z up), then backs the camera off along forward.
	void Postcheck( CollisionTest collision ) {
		var focus = GetVector( _p, 0 );
		var distance = MathF.Max( 0, _p[6] );
		var forward = GetVector( _p, 8 );
		var up = GetVector( _p, 11 );
		// Halo pins the field of view to 90 degrees, MCC to 120.
		_p[7] = Math.Clamp( _p[7], 0.001f, 120f.DegreeToRadian() );

		var heading = new Vector3( forward.x, forward.y, 0 );
		heading = heading.Length > 0 ? heading / heading.Length : Vector3.Zero;
		var offset = GetVector( _p, 3 );
		focus.x += offset.x * heading.x + offset.y * heading.y;
		focus.y += offset.x * heading.y - offset.y * heading.x;
		focus.z += offset.z;

		if ( !_last.IgnoreObstructions && distance != 0 && collision is not null )
			CheckPenetration( focus, forward, up, ref distance, 0.02f, collision );

		Position = focus - forward * distance;
		ResultForward = forward;
		ResultUp = up;
		ResultFov = _p[7];
	}

	// Casts from the focus to the camera, plus four rays about 10 degrees off to each side and up and down.
	// If a side ray hits sooner, ten bisections find where that nearer hit begins.
	// The distance then blends between the straight hit and the side hit.
	static void CheckPenetration( Vector3 focus, Vector3 forward, Vector3 up, ref float distance, float safe, CollisionTest test ) {
		var desired = focus - forward * (distance + safe);
		var firstT = 1f;
		if ( test( focus, desired, out var t ) ) firstT = t;

		var bestT = firstT;
		var bestOffset = -1;
		var bestSign = 0f;
		var step = distance * SineRegionAngle;
		var perturbations = new[] { up * step, Vector3.Cross( up, forward ) * step };

		for ( var probe = 0; probe < 4; probe++ ) {
			var sign = (probe & 1) != 0 ? 1f : -1f;
			var offset = perturbations[probe & 1];
			if ( test( focus, desired + offset * sign, out var collisionT ) && collisionT < bestT ) {
				bestT = collisionT;
				bestOffset = probe & 1;
				bestSign = sign;
			}
		}

		if ( bestOffset >= 0 ) {
			var lower = 0f;
			var upper = bestSign;
			var lastMissT = firstT;
			var lastHitT = bestT;
			for ( var iteration = 0; iteration < 10; iteration++ ) {
				var middle = (upper + lower) * 0.5f;
				var hit = test( focus, desired + perturbations[bestOffset] * middle, out var collisionT );
				if ( hit && MathF.Abs( collisionT - lastHitT ) < 0.1f ) {
					upper = middle;
					lastHitT = collisionT;
				}
				else {
					lower = middle;
					lastMissT = hit ? collisionT : 1;
				}
			}

			var scale = MathF.Abs( lastMissT < lastHitT ? lower : upper );
			distance *= (1 - scale) * bestT + scale * firstT;
		}
		else {
			distance *= firstT;
		}
	}

	// The rotation taking one orientation to another, as an axis scaled by its angle.
	static Vector3 RotationalDisplacement( Vector3 forward0, Vector3 up0, Vector3 forward1, Vector3 up1 ) {
		var r0 = Rotation.LookAt( forward0, up0 );
		var r1 = Rotation.LookAt( forward1, up1 );
		var q = r1 * r0.Inverse;
		if ( q.w < 0 ) q = new Rotation( -q.x, -q.y, -q.z, -q.w );

		var sin = MathF.Sqrt( q.x * q.x + q.y * q.y + q.z * q.z );
		if ( sin < 1e-7f ) return Vector3.Zero;
		var angle = 2 * MathF.Atan2( sin, q.w );
		return new Vector3( q.x, q.y, q.z ) / sin * angle;
	}

	static Vector3 GetVector( float[] values, int i ) => new( values[i], values[i + 1], values[i + 2] );

	void SetVector( int i, Vector3 v ) {
		_p[i] = v.x;
		_p[i + 1] = v.y;
		_p[i + 2] = v.z;
	}
}
