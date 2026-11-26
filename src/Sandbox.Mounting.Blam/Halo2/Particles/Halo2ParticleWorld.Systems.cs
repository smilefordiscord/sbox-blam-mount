using Blam.Halo2.Tags;

namespace Sandbox.Mounting.Blam.Halo2;

// Particle systems and their particles: emission, shapes, the particle tick and particle movement, as Halo 2 has them.
sealed partial class Halo2ParticleWorld
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

		// Each emitter's carried fraction of a particle and where it was last tick (world units).
		public float[] Accumulators;
		public Vector3[] Previous;
		public bool HasPrevious;
		public int[] Counts;
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
		public float Random1, Random2, Random3, Random4;

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

	// Particle flags.
	const byte Collided = 0x1;
	const byte AtRest = 0x2;

	sealed class SystemInstance
	{
		public Halo2SystemDefinition Definition;

		// The effect running it, or null once that's over: it then lives until its last particle dies.
		public EffectInstance Effect;
		public bool Alive = true;

		// Its effect is the local player's first person view's, drawn through that view's projection.
		public bool FirstPerson;

		public float EmitterAge;
		public float EmitterRandom1, EmitterRandom2;
		public bool Emitting;
		public bool WasEmitting;

		public readonly List<SystemLocation> Locations = new();
		public Particle[] Particles = new Particle[16];
		public int Count;

		public RenderSystem Render;
	}

	readonly List<SystemInstance> _systems = new();
	int _particleCount;

	// Halo 2's limits (its data arrays): past them nothing more is made.
	const int MaxSystems = 128;
	const int MaxParticles = 1024;

	// Halo 2's gravity, world units per second squared (its physics constants).
	const float Gravity = 4.17126f;

	// On a floor (normal z above this) slower than RestSpeed (world units per second), a particle comes to rest.
	const float FloorNormal = 0.8f;
	const float RestSpeed = 0.25f;

	// Halo's particle inputs (ParticleInput), filled for each evaluation.
	readonly float[] _inputs = new float[17];

	SystemInstance NewSystem( Halo2SystemDefinition definition, EffectInstance effect )
	{
		var system = new SystemInstance
		{
			Definition = definition,
			Effect = effect,
			FirstPerson = effect?.FirstPerson ?? false,
			EmitterRandom1 = Rand(),
			EmitterRandom2 = Rand(),
		};

		_systems.Add( system );
		return system;
	}

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
					found = true;
					break;
				}
			}

			if ( !found && system.Locations.Count < short.MaxValue )
			{
				system.Locations.Add( new SystemLocation
				{
					Place = place,
					Random = (int)(Rand() * 256) / 255f,
					Accumulators = new float[emitters],
					Previous = new Vector3[emitters],
					Counts = new int[emitters],
				} );
			}
		}
	}

	// Where a location is now and how far it fades by distance (its system LOD: the nearest view's).
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
			lod = MathF.Max( lod, LodAt( system.Definition.System, location.Position.Distance( camera ) ) );
		location.Lod = lod;
	}

	// Halo's system LOD at a distance (world units): 0 outside its in and out distances, rising and falling over the feathers.
	static float LodAt( EffectParticleSystem system, float distance )
	{
		if ( system.LodOut <= 0 )
			return 1;
		if ( distance <= system.LodIn || distance >= system.LodOut )
			return 0;
		if ( system.LodFeatherIn > 0 && distance < system.LodIn + system.LodFeatherIn )
			return (distance - system.LodIn) / system.LodFeatherIn;
		if ( system.LodFeatherOut > 0 && distance > system.LodOut - system.LodFeatherOut )
			return (system.LodOut - distance) / system.LodFeatherOut;
		return 1;
	}

	// The inputs a system's emitter has at a location: its age and randoms, the LOD, game time, the effect's scales.
	void SystemInputs( SystemInstance system, SystemLocation location )
	{
		var v = _inputs;
		Array.Clear( v );
		v[(int)ParticleInput.EmitterAge] = system.EmitterAge;
		v[(int)ParticleInput.EmitterRandom1] = system.EmitterRandom1;
		v[(int)ParticleInput.EmitterRandom2] = system.EmitterRandom2;
		v[(int)ParticleInput.SystemLod] = location.Lod;
		v[(int)ParticleInput.GameTime] = _gameTime;
		v[(int)ParticleInput.EffectAScale] = system.Effect?.A ?? 0;
		v[(int)ParticleInput.EffectBScale] = system.Effect?.B ?? 0;
		v[(int)ParticleInput.LocationRandom] = location.Random;
	}

	// Adds a particle's own inputs to the system's.
	void ParticleInputs( in Particle p, float age, float rotation )
	{
		var v = _inputs;
		v[(int)ParticleInput.ParticleAge] = age;
		v[(int)ParticleInput.ParticleEmitTime] = p.EmitTime;
		v[(int)ParticleInput.ParticleRandom1] = p.Random1;
		v[(int)ParticleInput.ParticleRandom2] = p.Random2;
		v[(int)ParticleInput.ParticleRandom3] = p.Random3;
		v[(int)ParticleInput.ParticleRandom4] = p.Random4;
		v[(int)ParticleInput.ParticleRotation] = rotation - MathF.Floor( rotation );
	}

	// A property's value for the current inputs, or the default when the tag leaves it empty.
	float Evaluate( ParticleProperty p, float empty )
	{
		if ( p is null || p.IsEmpty )
			return empty;

		var v = _inputs;
		var value = p.Function.Evaluate( Input( p.Input ), Input( p.Range ) );
		return p.Modifier switch
		{
			1 => value + Input( p.ModifierInput ),
			2 => value * Input( p.ModifierInput ),
			_ => value,
		};

		float Input( ParticleInput i ) => (int)i >= 0 && (int)i < v.Length ? v[(int)i] : 0;
	}

	// A colour property's colour (gamma space) for the current inputs; white when empty.
	Vector3 EvaluateColor( ParticleProperty p )
	{
		if ( p is null || p.IsEmpty )
			return Vector3.One;

		var v = _inputs;
		var x = (int)p.Input >= 0 && (int)p.Input < v.Length ? v[(int)p.Input] : 0;
		var y = (int)p.Range >= 0 && (int)p.Range < v.Length ? v[(int)p.Range] : 0;
		var c = global::Blam.Halo2.Tags.MappingFunction.ToVector( p.Function.ColorAt( p.Function.Curve( x, y ) ) );
		return new Vector3( c.X, c.Y, c.Z );
	}

	// A step of a system's emission at its places: each emitter's rate (particles a tick) accumulates, and every whole particle is made.
	void EmitSystem( SystemInstance system, List<Place> places, float step )
	{
		if ( places is not null )
			MatchLocations( system, places );

		var definition = system.Definition;
		var spread = definition.SpreadBetweenTicks && system.WasEmitting;
		system.Emitting = true;

		foreach ( var location in system.Locations )
		{
			PlaceLocation( system, location );
			if ( !location.Valid )
				continue;

			for ( var e = 0; e < definition.Emitters.Length; e++ )
			{
				var emitter = definition.Emitters[e];
				var origin = location.Position + location.Rotation * emitter.Offset;
				var previous = location.HasPrevious ? location.Previous[e] : origin;
				location.Previous[e] = origin;

				// A looping particle system makes no more while its emitter has particles.
				if ( definition.LoopingParticle && location.Counts[e] > 0 )
					continue;

				SystemInputs( system, location );
				var rate = MathF.Max( Evaluate( emitter.Emitter.EmissionRate, 0 ), 0 );
				location.Accumulators[e] += rate * 30 * step;

				var count = 0;
				while ( location.Accumulators[e] + 0.0001f >= 1 && count < 256 )
				{
					location.Accumulators[e] -= 1;
					count++;
				}

				for ( var n = 0; n < count && _particleCount < MaxParticles; n++ )
				{
					// Spread between ticks: the tick's particles from where the emitter was to where it is, aged accordingly.
					var fraction = spread ? (n + 1) / (float)count : 1;
					Spawn( system, location, e, Vector3.Lerp( previous, origin, fraction ), spread ? (1 - fraction) * Tick : 0 );
				}
			}

			location.HasPrevious = true;
		}
	}

	// A new particle of an emitter, with its shape's position and direction and its properties at birth.
	void Spawn( SystemInstance system, SystemLocation location, int emitterIndex, Vector3 origin, float preAge )
	{
		var definition = system.Definition;
		var emitterDefinition = definition.Emitters[emitterIndex];
		var emitter = emitterDefinition.Emitter;

		var p = new Particle
		{
			Location = (short)system.Locations.IndexOf( location ),
			Emitter = (byte)emitterIndex,
			EmitTime = system.EmitterAge,
			Random1 = (int)(Rand() * 65536) / 65536f,
			Random2 = (int)(Rand() * 65536) / 65536f,
			Random3 = (int)(Rand() * 65536) / 65536f,
			Random4 = (int)(Rand() * 65536) / 65536f,
			Countdown = (byte)(Rand() * 5),
		};

		SystemInputs( system, location );
		ParticleInputs( p, 0, 0 );
		var radius = Evaluate( emitter.EmissionRadius, 0 );
		var angle = Evaluate( emitter.EmissionAngle, 0 ).DegreeToRadian();
		var speed = Evaluate( emitter.Velocity, 0 );
		var lifespan = Evaluate( emitter.Lifespan, 1 );
		p.InvLifespan = 1 / MathF.Max( lifespan, 0.01f );
		p.AngularVelocity = Evaluate( emitter.AngularVelocity, 0 ) / 360;

		var (position, direction) = Shape( emitter.Shape, radius, angle );
		var turn = location.Rotation * emitterDefinition.Direction;

		if ( definition.Local )
		{
			// In the location's space, so the particles follow it.
			p.Position = emitterDefinition.Offset + emitterDefinition.Direction * position;
			p.Velocity = emitterDefinition.Direction * (direction * speed);
			p.Position += p.Velocity * preAge;
		}
		else
		{
			p.Position = origin + turn * position;
			p.Velocity = turn * (direction * speed);
			if ( definition.InheritsVelocity && system.Effect is { } effect )
				p.Velocity += effect.TotalVelocity;

			// Debris lands where a ray down from just above it meets the level, at rest along the surface's normal; it dies if there's nothing there.
			if ( emitter.Shape == EmissionShape.Debris )
			{
				if ( !Trace( p.Position + Vector3.Up, Vector3.Down, out _, out var hit, out var normal ) )
					return;

				p.Position = hit + normal * 0.005f;
				p.Velocity = normal;
				p.Flags |= AtRest;
			}
		}

		p.Previous = p.Position;
		p.Age = preAge * p.InvLifespan;

		if ( system.Count == system.Particles.Length )
			Array.Resize( ref system.Particles, system.Count * 2 );
		system.Particles[system.Count++] = p;
		location.Counts[emitterIndex]++;
		_particleCount++;
	}

	// An emission shape's position and direction in the emitter's frame (x its forward, y left, z up).
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
			{
				// The impact's extents aren't known here: from the impact's point.
				var (sinA, cosA) = MathF.SinCos( angle );
				position = Vector3.Zero;
				direction = new Vector3( cosA, sinA * sinTheta, sinA * cosTheta );
				break;
			}

			case EmissionShape.ImpactContour:
			{
				var (sinA, cosA) = MathF.SinCos( angle );
				var side = Rand() < 0.5f ? -1 : 1;
				position = Vector3.Zero;
				direction = Rand() < 0.5f ? new Vector3( cosA, side * sinA, 0 ) : new Vector3( cosA, 0, side * sinA );
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
				// The sprayer ignores its radius.
				position = Vector3.Zero;
				direction = Cone( angle );
				break;
		}

		return (position, direction.LengthSquared > 1e-12f ? direction.Normal : Vector3.Forward);
	}

	// A sprayer's direction: off forward by a uniform part of the angle, around it at random.
	Vector3 Cone( float angle )
	{
		var phi = Rand() * angle;
		var theta = Rand() * MathF.PI * 2;
		var (sinPhi, cosPhi) = MathF.SinCos( phi );
		var (sinTheta, cosTheta) = MathF.SinCos( theta );
		return new Vector3( cosPhi, sinTheta * sinPhi, cosTheta * sinPhi );
	}

	// A tick of every system's particles: age, motion, movement; then the systems nothing keeps alive go.
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

			UpdateParticles( system, dt );
			system.WasEmitting = system.Emitting;
			system.Emitting = false;

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
				// A looping particle wraps its age while its system emits; a persistent one while it's in range.
				if ( (definition.LoopingParticle && system.Emitting) || (definition.Persistent && location.Lod > 0) )
				{
					p.Age -= MathF.Floor( p.Age );
				}
				else
				{
					Remove( system, i--, true );
					continue;
				}
			}

			if ( (p.Flags & AtRest) != 0 )
				continue;

			p.Position += p.Velocity * dt;
			p.Rotation += p.AngularVelocity * dt;
			if ( !Move( system, location, ref p, dt ) )
				Remove( system, i--, true );
		}
	}

	// Removes a particle, playing its death effect when it dies (not when its place went away).
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

	// The particle's movement (pmov): physics (gravity, drag, angular drag) and the collider. False if it died.
	bool Move( SystemInstance system, SystemLocation location, ref Particle p, float dt )
	{
		var movement = system.Definition.Emitters[p.Emitter].Movement;
		if ( !movement.Physics && !movement.Collides )
			return true;

		SystemInputs( system, location );
		ParticleInputs( p, p.Age, p.Rotation );

		if ( movement.Physics )
		{
			// In a local system the location's up is its z: gravity pulls along the marker's down.
			p.Velocity.z -= Evaluate( movement.Gravity, 0 ) * Gravity * dt;
			p.Velocity *= 1 - Math.Clamp( Evaluate( movement.Drag, 0 ) * dt, 0, 1 );
			p.AngularVelocity *= 1 - Math.Clamp( Evaluate( movement.AngularDrag, 0 ) * dt, 0, 1 );
		}

		if ( !movement.Collides || system.Definition.Local )
			return true;

		// The collider looks 12 ticks ahead from where it was, every sixth tick while nothing is near; a hit within the first two ticks is now.
		if ( p.Countdown > 0 )
		{
			p.Countdown--;
			return true;
		}

		var from = p.Position - p.Velocity * dt;
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

		var friction = Evaluate( movement.Friction, 0 );
		var elasticity = Evaluate( movement.Elasticity, 0 );
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

		if ( particle.DiesOnStructureCollision )
			return false;

		if ( normal.z > FloorNormal && p.Velocity.LengthSquared <= RestSpeed * RestSpeed )
		{
			if ( particle.DiesAtRest )
				return false;

			p.Flags |= AtRest;
			p.Velocity = Vector3.Zero;
		}

		return true;
	}

	static readonly string[] LevelTags = [ScenarioSceneLoader.LevelTag];
	SceneTrace? _levelTrace;

	// The first level surface a moving point meets (world units).
	bool Trace( Vector3 from, Vector3 delta, out float fraction, out Vector3 point, out Vector3 normal )
	{
		fraction = 1;
		point = from + delta;
		normal = Vector3.Up;
		if ( delta.LengthSquared < 1e-12f )
			return false;

		_levelTrace ??= Scene.Trace.WithAnyTags( LevelTags );
		var trace = _levelTrace.Value.Ray( from * Units, (from + delta) * Units ).Run();
		if ( !trace.Hit || trace.StartedSolid )
			return false;

		fraction = trace.Fraction;
		point = trace.HitPosition / Units;
		normal = trace.Normal;
		return true;
	}

	// Most collision and death effects started in one tick: a burst landing at once shouldn't start hundreds.
	const int MaxContactsPerTick = 24;
	int _contactsTick = -1;
	int _contacts;

	// Starts a particle's collision or death effect where it is, facing along forward (world space).
	void Contact( global::Blam.Halo2.TagEntry tag, SystemInstance system, SystemLocation location, in Particle p, Vector3 forward )
	{
		var tick = (int)(_gameTime * 30 + 0.5f);
		if ( tick != _contactsTick )
		{
			_contactsTick = tick;
			_contacts = 0;
		}

		if ( _contacts >= MaxContactsPerTick || Halo2ParticleLibrary.Effect( tag ) is not { } effect )
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
