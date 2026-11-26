using Blam.Halo3.Tags;

namespace Sandbox.Mounting.Blam.Halo3;

// Particle systems and their particles: emission (rate in particles a second through the event's duration, the starting count at once, the maximum count), the emitters' shapes, and the particle tick as MCC's particle_update compute shader runs it (position by velocity, self acceleration, gravity, air and rotational friction), plus a level collider.
sealed partial class Halo3ParticleWorld
{
	// One of a system's places with its emitters' state; positions in world units.
	sealed class SystemLocation
	{
		public Place Place;
		public bool Valid = true;
		public float Random;
		public float Lod;
		public Vector3 Position;
		public Rotation Rotation;

		// Each emitter's carried fraction of a particle, where it was last tick, its live particles and whether it has made its starting count.
		public float[] Accumulators;
		public Vector3[] Previous;
		public bool HasPrevious;

		// The emission step that last matched it to one of its effect's places.
		public int Matched;
		public int[] Counts;
		public bool[] Started;
	}

	struct Particle
	{
		// World units, in the location's space for a local system.
		public Vector3 Position;
		public Vector3 Previous;
		public Vector3 Velocity;

		// Age as a fraction of the lifespan, and one over the lifespan (seconds).
		public float Age;
		public float InvLifespan;
		public float EmitTime;

		// The particle random and correlations 1-8.
		public float Random;
		public float C1, C2, C3, C4, C5, C6, C7, C8;

		// Turns, and turns a second.
		public float Rotation;
		public float PreviousRotation;
		public float AngularVelocity;

		public short Location;
		public byte Emitter;
		public byte Flags;

		// Ticks until the collider looks ahead again.
		public byte Countdown;
	}

	const byte Collided = 0x1;
	const byte AtRest = 0x2;

	sealed class SystemInstance
	{
		public Halo3SystemDefinition Definition;

		// The effect running it, or null once that's over: it then lives until its last particle dies.
		public EffectInstance Effect;
		public bool Alive = true;
		public bool KillAll;

		public float Age;
		public float Random, Correlation1, Correlation2;

		// The effect's scales A and B as of its last tick, for particles left once it's over.
		public float A = 1, B = 1;

		public readonly List<SystemLocation> Locations = new();
		public Particle[] Particles = new Particle[16];
		public int Count;

		public RenderSystem Render;
	}

	readonly List<SystemInstance> _systems = new();
	int _particleCount;

	const int MaxSystems = 256;
	const int MaxParticles = 4096;

	// Halo's gravity, world units per second squared (Halo 2's physics constant; Halo 3 keeps the default gravity).
	const float Gravity = 4.17126f;

	// On a floor (normal z above this) slower than RestSpeed (world units per second), a particle comes to rest.
	const float FloorNormal = 0.8f;
	const float RestSpeed = 0.25f;

	// Halo's inputs (ParticleInput), filled for each evaluation.
	readonly float[] _inputs = new float[25];

	SystemInstance NewSystem( Halo3SystemDefinition definition, EffectInstance effect )
	{
		var system = new SystemInstance
		{
			Definition = definition,
			Effect = effect,
			Random = Rand(),
			Correlation1 = Rand(),
			Correlation2 = Rand(),
		};

		_systems.Add( system );
		return system;
	}

	int _emitStep;

	// Keeps a system's locations in step with its effect's places of the location.
	void MatchLocations( SystemInstance system, List<Place> places )
	{
		var emitters = system.Definition.Emitters.Length;
		for ( var i = 0; i < places.Count; i++ )
		{
			var place = places[i];
			var found = false;
			foreach ( var location in system.Locations )
			{
				if ( location.Valid && (place.Object is null ? location.Place.Object is null : location.Place.Object == place.Object) )
				{
					location.Place = place;
					location.Matched = _emitStep;
					found = true;
					break;
				}
			}

			if ( !found && system.Locations.Count < short.MaxValue )
			{
				system.Locations.Add( new SystemLocation
				{
					Place = place,
					Random = Rand(),
					Accumulators = new float[emitters],
					Previous = new Vector3[emitters],
					Counts = new int[emitters],
					Started = new bool[emitters],
					Matched = _emitStep,
				} );
			}
		}
	}

	// Where a location is now and its system LOD (the nearest view's).
	void PlaceLocation( SystemInstance system, SystemLocation location )
	{
		if ( !location.Place.Valid )
		{
			location.Valid = false;
			location.Lod = 0;
			return;
		}

		(location.Position, location.Rotation) = location.Place.Frame;
		var lod = 0f;
		foreach ( var camera in _cameras )
			lod = MathF.Max( lod, system.Definition.System.LodAt( location.Position.Distance( camera ) ) );
		location.Lod = lod;
	}

	// The system's inputs at a location: its age and randoms, the LOD, game time, the effect's scales.
	void SystemInputs( SystemInstance system, SystemLocation location )
	{
		var v = _inputs;
		Array.Clear( v );
		v[(int)ParticleInput.SystemAge] = system.Age;
		v[(int)ParticleInput.SystemRandom] = system.Random;
		v[(int)ParticleInput.SystemCorrelation1] = system.Correlation1;
		v[(int)ParticleInput.SystemCorrelation2] = system.Correlation2;
		v[(int)ParticleInput.LocationLod] = location.Lod;
		v[(int)ParticleInput.GameTime] = _gameTime;
		if ( system.Effect is { } effect )
			(system.A, system.B) = (effect.A, effect.B);
		v[(int)ParticleInput.EffectAScale] = system.A;
		v[(int)ParticleInput.EffectBScale] = system.B;
		v[(int)ParticleInput.LocationRandom] = location.Random;
	}

	void ParticleInputs( in Particle p, float age, float rotation )
	{
		var v = _inputs;
		v[(int)ParticleInput.ParticleAge] = age;
		v[(int)ParticleInput.ParticleRandom] = p.Random;
		v[(int)ParticleInput.ParticleCorrelation1] = p.C1;
		v[(int)ParticleInput.ParticleCorrelation2] = p.C2;
		v[(int)ParticleInput.ParticleCorrelation3] = p.C3;
		v[(int)ParticleInput.ParticleCorrelation4] = p.C4;
		v[(int)ParticleInput.ParticleCorrelation5] = p.C5;
		v[(int)ParticleInput.ParticleCorrelation6] = p.C6;
		v[(int)ParticleInput.ParticleCorrelation7] = p.C7;
		v[(int)ParticleInput.ParticleCorrelation8] = p.C8;
		v[(int)ParticleInput.ParticleEmissionTime] = p.EmitTime;
		v[(int)ParticleInput.ParticleRotation] = rotation - MathF.Floor( rotation );
	}

	float Evaluate( ParticleProperty p, float empty ) => p is null ? empty : p.Evaluate( _inputs, empty );

	Vector3 EvaluateColor( ParticleProperty p )
	{
		if ( p is null )
			return Vector3.One;
		var c = p.EvaluateColor( _inputs );
		return new Vector3( c.X, c.Y, c.Z );
	}

	// The emitter's frame at a location: forward along the relative direction (between its interpolants), up the location's up made square to it (its forward when the direction is straight up), and the offset (between its interpolants) in that turned frame.
	// The direction is a unit vector's side components, the second toward the location's up: MCC's emitter frames for Valhalla's man cannons and waterfall base match exactly. Which way the first component leans is unverified (0 in every case checked).
	(Vector3 Origin, Rotation Rotation) EmitterFrame( EffectEmitter emitter, SystemLocation location, bool local )
	{
		var o = emitter.Offset.IsEmpty ? 0 : Math.Clamp( Evaluate( emitter.Offset, 0 ), 0, 1 );
		var offset = System.Numerics.Vector3.Lerp( emitter.OffsetFrom, emitter.OffsetTo, o );
		var d = emitter.Direction.IsEmpty ? 0 : Math.Clamp( Evaluate( emitter.Direction, 0 ), 0, 1 );
		var side = System.Numerics.Vector2.Lerp( emitter.DirectionFrom, emitter.DirectionTo, d );
		var direction = new Vector3( MathF.Sqrt( MathF.Max( 1 - side.X * side.X - side.Y * side.Y, 0 ) ), side.X, side.Y );
		var turn = Rotation.Identity;
		if ( direction.LengthSquared > 1e-12f )
		{
			direction = direction.Normal;
			turn = Rotation.LookAt( direction, MathF.Abs( direction.z ) > 0.999f ? Vector3.Forward : Vector3.Up );
		}

		var localOffset = turn * new Vector3( offset.X, offset.Y, offset.Z );
		if ( local )
			return (localOffset, turn);
		return (location.Position + location.Rotation * localOffset, location.Rotation * turn);
	}

	// A step of a system's emission at its places: the starting count once, then each emitter's rate (particles a second) accumulated, every whole particle made.
	void EmitSystem( SystemInstance system, List<Place> places, float step )
	{
		_emitStep++;
		if ( places is not null )
			MatchLocations( system, places );

		var definition = system.Definition;
		foreach ( var location in system.Locations )
		{
			// Places the effect no longer has (a marker instance gone) keep their particles but make no more.
			if ( places is not null && location.Matched != _emitStep )
				continue;

			PlaceLocation( system, location );
			if ( !location.Valid || location.Lod <= 0 )
				continue;

			for ( var e = 0; e < definition.Emitters.Length; e++ )
			{
				var emitter = definition.Emitters[e].Emitter;
				SystemInputs( system, location );
				var (origin, _) = EmitterFrame( emitter, location, false );
				var previous = location.HasPrevious ? location.Previous[e] : origin;
				location.Previous[e] = origin;

				var count = 0;
				if ( !location.Started[e] )
				{
					location.Started[e] = true;
					count += Math.Max( 0, (int)MathF.Round( Evaluate( emitter.StartingCount, 0 ) ) );
				}

				location.Accumulators[e] += MathF.Max( Evaluate( emitter.EmissionRate, 0 ), 0 ) * step;
				var whole = (int)MathF.Floor( location.Accumulators[e] );
				if ( whole > 0 )
				{
					location.Accumulators[e] -= whole;
					count += whole;
				}

				count = Math.Min( count, 256 );
				var maximum = (int)MathF.Round( Evaluate( emitter.MaximumCount, 0 ) );
				for ( var n = 0; n < count && _particleCount < MaxParticles; n++ )
				{
					if ( maximum > 0 && location.Counts[e] >= maximum )
						break;

					// The tick's particles spread from where the emitter was to where it is.
					var fraction = count > 1 ? (n + 1) / (float)count : 1;
					Spawn( system, location, e, Vector3.Lerp( previous, origin, fraction ) - origin );
				}
			}

			location.HasPrevious = true;
		}
	}

	// A new particle of an emitter: its shape's position and direction, its properties at birth.
	void Spawn( SystemInstance system, SystemLocation location, int emitterIndex, Vector3 shift )
	{
		var definition = system.Definition;
		var emitter = definition.Emitters[emitterIndex].Emitter;
		var particle = definition.Particle.Tag;

		var p = new Particle
		{
			Location = (short)system.Locations.IndexOf( location ),
			Emitter = (byte)emitterIndex,
			EmitTime = system.Age,
			Random = Rand(),
			C1 = Rand(), C2 = Rand(), C3 = Rand(), C4 = Rand(), C5 = Rand(), C6 = Rand(), C7 = Rand(), C8 = Rand(),
			Countdown = (byte)(Rand() * 5),
		};

		SystemInputs( system, location );
		ParticleInputs( p, 0, 0 );
		var radius = Evaluate( emitter.EmissionRadius, 0 );
		var angle = Evaluate( emitter.EmissionAngle, 0 ).DegreeToRadian();
		var speed = Evaluate( emitter.InitialVelocity, 0 );
		var lifespan = Evaluate( emitter.Lifespan, 1 );
		p.InvLifespan = 1 / MathF.Max( lifespan, 0.01f );
		p.AngularVelocity = Evaluate( emitter.RotationRate, 0 );
		if ( particle.RandomStartingRotation )
			p.Rotation = Rand();

		var (position, direction) = Shape( emitter.Shape, radius, angle );
		var local = definition.Local;
		var (origin, turn) = EmitterFrame( emitter, location, local );

		p.Position = origin + turn * position;
		p.Velocity = turn * (direction * speed);
		if ( !local )
		{
			p.Position += shift;
			if ( definition.System.InheritsVelocity && system.Effect is { } effect )
				p.Velocity += effect.TotalVelocity;

			// Debris lands where a ray down from just above it meets the level; it dies if there's nothing there.
			if ( emitter.Shape == EmissionShape.Debris )
			{
				if ( !Trace( p.Position + Vector3.Up, Vector3.Down * 2, out _, out var hit, out var normal ) )
					return;

				p.Position = hit + normal * 0.005f;
				p.Velocity = Vector3.Zero;
				p.Flags |= AtRest;
			}
		}

		p.Previous = p.Position;
		p.PreviousRotation = p.Rotation;

		if ( system.Count == system.Particles.Length )
			Array.Resize( ref system.Particles, system.Count * 2 );
		system.Particles[system.Count++] = p;
		location.Counts[emitterIndex]++;
		_particleCount++;
	}

	// An emission shape's position and direction in the emitter's frame (x its forward, y left, z up): Halo 2's shapes, which Halo 3's match; the axis angle only sets an oriented billboard's axis, never the direction.
	(Vector3 Position, Vector3 Direction) Shape( EmissionShape shape, float radius, float angle )
	{
		var theta = Rand() * MathF.PI * 2;
		var (sinTheta, cosTheta) = MathF.SinCos( theta );
		Vector3 position, direction;
		switch ( shape )
		{
			case EmissionShape.Disc:
				position = new Vector3( 0, radius * sinTheta, radius * cosTheta );
				direction = new Vector3( 0, sinTheta, cosTheta );
				break;

			case EmissionShape.Globe:
			case EmissionShape.Implode:
			{
				var n = RandomDirection();
				position = n * radius;
				direction = shape == EmissionShape.Implode ? -n : n;
				break;
			}

			case EmissionShape.Tube:
			{
				var (sinA, cosA) = MathF.SinCos( angle );
				position = new Vector3( 0, radius * sinTheta, radius * cosTheta );
				direction = new Vector3( cosA, sinA * sinTheta, sinA * cosTheta );
				break;
			}

			case EmissionShape.Halo:
				position = RandomDirection() * radius;
				direction = Cone( angle );
				break;

			case EmissionShape.ImpactArea:
			case EmissionShape.ImpactContour:
			{
				var (sinA, cosA) = MathF.SinCos( angle );
				position = Vector3.Zero;
				direction = new Vector3( cosA, sinA * sinTheta, sinA * cosTheta );
				break;
			}

			case EmissionShape.Debris:
			{
				var r = Rand() * radius;
				position = new Vector3( 0, r * sinTheta, r * cosTheta );
				direction = Vector3.Up;
				break;
			}

			case EmissionShape.Line:
				position = new Vector3( 0, Range( -radius, radius ), 0 );
				direction = Cone( angle );
				break;

			default:
				// The sprayer (and breakable surface) from the point.
				position = Vector3.Zero;
				direction = Cone( angle );
				break;
		}

		return (position, direction.LengthSquared > 1e-12f ? direction.Normal : Vector3.Forward);
	}

	// A direction off forward by a uniform part of the angle, around it at random.
	Vector3 Cone( float angle )
	{
		var phi = Rand() * angle;
		var theta = Rand() * MathF.PI * 2;
		var (sinPhi, cosPhi) = MathF.SinCos( phi );
		var (sinTheta, cosTheta) = MathF.SinCos( theta );
		return new Vector3( cosPhi, sinTheta * sinPhi, cosTheta * sinPhi );
	}

	// A tick of every system's particles; then the systems nothing keeps alive go.
	void UpdateSystems( float dt )
	{
		for ( var i = _systems.Count - 1; i >= 0; i-- )
		{
			var system = _systems[i];
			foreach ( var location in system.Locations )
			{
				if ( location.Valid )
					PlaceLocation( system, location );
			}

			if ( system.KillAll )
			{
				_particleCount -= system.Count;
				system.Count = 0;
			}

			UpdateParticles( system, dt );

			if ( system.Count == 0 && system.Effect is null )
			{
				system.Alive = false;
				system.Render?.Delete();
				system.Render = null;
				_systems.RemoveAt( i );
			}
		}
	}

	void UpdateParticles( SystemInstance system, float dt )
	{
		var definition = system.Definition;
		for ( var i = 0; i < system.Count; i++ )
		{
			ref var p = ref system.Particles[i];
			var location = p.Location >= 0 && p.Location < system.Locations.Count ? system.Locations[p.Location] : null;

			// A local system's particles go with their place.
			if ( location is null || (definition.Local && !location.Valid) )
			{
				Remove( system, i--, false );
				continue;
			}

			p.Previous = p.Position;
			p.PreviousRotation = p.Rotation;
			p.Age += dt * p.InvLifespan;
			if ( p.Age >= 1 )
			{
				Remove( system, i--, true );
				continue;
			}

			if ( (p.Flags & AtRest) != 0 )
				continue;

			if ( !Move( system, location, ref p, dt ) )
				Remove( system, i--, true );
		}
	}

	void Remove( SystemInstance system, int index, bool dies )
	{
		ref var p = ref system.Particles[index];
		if ( p.Location >= 0 && p.Location < system.Locations.Count )
		{
			var location = system.Locations[p.Location];
			location.Counts[p.Emitter]--;
			if ( dies && system.Definition.Particle.DeathEffect is { } death )
				Contact( death, system, location, p, p.Velocity );
		}

		system.Particles[index] = system.Particles[--system.Count];
		_particleCount--;
	}

	// A tick of a particle's motion, as particle_update: position by velocity, then self acceleration, gravity, air friction and rotational friction (its movement's physics), and the collider. False if it died.
	bool Move( SystemInstance system, SystemLocation location, ref Particle p, float dt )
	{
		var emitterDefinition = system.Definition.Emitters[p.Emitter];
		var emitter = emitterDefinition.Emitter;
		var movement = emitterDefinition.Movement;

		p.Position += p.Velocity * dt;
		p.Rotation += p.AngularVelocity * dt;

		SystemInputs( system, location );
		ParticleInputs( p, p.Age, p.Rotation );

		// Self acceleration between its interpolants, in the location's frame.
		if ( !emitter.SelfAcceleration.IsEmpty && (emitter.AccelerationFrom != System.Numerics.Vector3.Zero || emitter.AccelerationTo != System.Numerics.Vector3.Zero) )
		{
			var t = Evaluate( emitter.SelfAcceleration, 0 );
			var a = System.Numerics.Vector3.Lerp( emitter.AccelerationFrom, emitter.AccelerationTo, t );
			var accel = new Vector3( a.X, a.Y, a.Z );
			p.Velocity += (system.Definition.Local ? accel : location.Rotation * accel) * dt;
		}

		if ( movement.Physics )
		{
			// In a local system the location's up is its z: gravity pulls along the marker's down.
			p.Velocity.z -= Evaluate( movement.Gravity, 0 ) * Gravity * dt;
			p.Velocity *= 1 - Math.Clamp( Evaluate( movement.Drag, 0 ) * dt, 0, 1 );
			p.AngularVelocity *= 1 - Math.Clamp( Evaluate( movement.AngularDrag, 0 ) * dt, 0, 1 );
		}

		if ( !movement.Collides || system.Definition.Local )
			return true;

		// The collider looks 12 ticks ahead, every sixth tick while nothing is near; a hit within the first two ticks is now.
		if ( p.Countdown > 0 )
		{
			p.Countdown--;
			return true;
		}

		var from = p.Previous;
		var ahead = p.Velocity * (dt * 12);
		if ( !Trace( from, ahead, out var fraction, out var point, out var normal ) )
		{
			p.Countdown = 5;
			return true;
		}

		if ( fraction > 1 / 6f )
		{
			p.Countdown = (byte)Math.Clamp( (int)(fraction * 12) - 2, 0, 5 );
			return true;
		}

		var friction = Math.Clamp( Evaluate( movement.Friction, 0 ), 0, 1 );
		var elasticity = Math.Clamp( Evaluate( movement.Elasticity, 0 ), 0, 1 );
		var into = normal * p.Velocity.Dot( normal );
		p.Position = point + normal * 0.005f;
		p.Velocity = (p.Velocity - into) * (1 - friction) - into * elasticity;
		p.AngularVelocity *= 1 - friction;

		var particle = system.Definition.Particle;
		if ( (p.Flags & Collided) == 0 )
		{
			p.Flags |= Collided;
			if ( particle.CollisionEffect is { } collision )
				Contact( collision, system, location, p, normal );
		}

		if ( particle.Tag.DiesOnStructureCollision )
			return false;

		if ( normal.z > FloorNormal && p.Velocity.LengthSquared <= RestSpeed * RestSpeed )
		{
			if ( particle.Tag.DiesAtRest )
				return false;

			p.Flags |= AtRest;
			p.Velocity = Vector3.Zero;
		}

		return true;
	}

	// Particles collide with anything solid (the level's and objects' colliders; Halo 3's BSP objects carry no tag of their own).
	SceneTrace? _levelTrace;

	// The first level surface a moving point meets (world units).
	bool Trace( Vector3 from, Vector3 delta, out float fraction, out Vector3 point, out Vector3 normal )
	{
		fraction = 1;
		point = from + delta;
		normal = Vector3.Up;
		if ( delta.LengthSquared < 1e-12f )
			return false;

		_levelTrace ??= Scene.Trace.WithoutTags( "trigger", "player" );
		var trace = _levelTrace.Value.Ray( from * Units, (from + delta) * Units ).Run();
		if ( !trace.Hit || trace.StartedSolid )
			return false;

		fraction = trace.Fraction;
		point = trace.HitPosition / Units;
		normal = trace.Normal;
		return true;
	}

	// Most collision and death effects started in one tick.
	const int MaxContactsPerTick = 24;
	int _contactsTick = -1;
	int _contacts;

	// Starts a particle's collision or death effect where it is, facing along forward (world space).
	void Contact( global::Blam.Halo3.TagEntry tag, SystemInstance system, SystemLocation location, in Particle p, Vector3 forward )
	{
		var tick = (int)(_gameTime * 30 + 0.5f);
		if ( tick != _contactsTick )
		{
			_contactsTick = tick;
			_contacts = 0;
		}

		if ( _contacts >= MaxContactsPerTick || Halo3ParticleLibrary.Effect( tag ) is not { } effect )
			return;

		_contacts++;
		var position = p.Position;
		var velocity = p.Velocity;
		if ( system.Definition.Local )
		{
			position = location.Position + location.Rotation * position;
			velocity = location.Rotation * velocity;
			forward = location.Rotation * forward;
		}

		forward = forward.LengthSquared > 1e-8f ? forward.Normal : Vector3.Up;
		var up = MathF.Abs( forward.z ) > 0.99f ? Vector3.Forward : Vector3.Up;
		StartEffect( effect, null, new Place { Position = position, Rotation = Rotation.LookAt( forward, up ) }, velocity );
	}
}
