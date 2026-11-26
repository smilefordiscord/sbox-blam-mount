using Blam.Halo1;
using Blam.Halo1.Tags;

namespace Sandbox.Mounting.Blam.Halo1;

// Particle systems (pctl): Halo's particle_systems_update, every frame.
sealed partial class HaloParticleWorld
{
	sealed class SystemInstance
	{
		public HaloSystemDefinition Definition;

		// The object carrying it (an attachment's), or null for one an effect made.
		public GameObject Object;
		public GameObject Controls;
		public Carrier Carrier;
		public bool FirstPerson;

		public bool Active;
		public bool Initializing;
		public bool Gone;
		public float Scale = 1;

		// World units, and per second.
		public Vector3 Position;
		public Vector3 Velocity;

		// The tint (alpha, red, green, blue as x, y, z, w = r, g, b, a) and the light it was made in.
		public Vector4 Color = Vector4.One;
		public Vector3 Lighting = Vector3.One;

		public TypeInstance[] Types;
		public int Group;
	}

	sealed class TypeInstance
	{
		public int State;
		public int Transition = -1;
		public float TimeLeft;
		public float Length;
		public bool Forward = true;
		public float Fraction;

		// The type state's variables now (see StateVariables).
		public readonly float[] Variables = new float[10];
		public readonly List<SystemParticle> Particles = new();
	}

	// A particle's state variables, picked between its particle state's bounds.
	struct StateValues
	{
		public float Scale;
		public float AnimationRate;
		public float RotationRate;

		// RGBA, gamma space.
		public Vector4 Color;
	}

	sealed class SystemParticle
	{
		public bool Valid = true;
		public bool Forward = true;
		public int State = -1;
		public int Transition = -1;
		public float TimeLeft;
		public float Length;
		public Vector3 Position;
		public Vector3 Velocity;
		public Vector3 Axis;
		public float Rotation;
		public float Sprite = -1;
		public StateValues Current;
		public StateValues Next;
	}

	// Halo's limits.
	const int MaxSystems = 64;
	const int MaxSystemParticles = 512;
	const int MaxCreatedPerUpdate = 128;

	readonly List<SystemInstance> _systems = new();
	readonly Dictionary<GameObject, SystemInstance> _systemsByRoot = new();
	int _systemParticleCount;
	readonly HaloPhysics _blendedPhysics = new();

	// A particle system an effect made, at a point, or with carrier one an object carries.
	SystemInstance StartSystem( HaloSystemDefinition definition, GameObject carrier, Vector3 position, Vector3 velocity, Vector4 color, float scale, int group, bool lit )
	{
		if ( _systems.Count >= MaxSystems || definition.Types.Length == 0 || definition.Types.Any( t => t.Type.States.Count == 0 ) )
			return null;

		var system = new SystemInstance
		{
			Definition = definition,
			Object = carrier,
			Position = position,
			Velocity = velocity,
			Color = color,
			Scale = scale,
			Active = true,
			Group = group,
			Types = new TypeInstance[definition.Types.Length],
		};

		if ( lit && Level is { } level )
			system.Lighting = ToVector( level.ParticleGround( position * Units ).Light );

		for ( var t = 0; t < definition.Types.Length; t++ )
		{
			var type = system.Types[t] = new TypeInstance();
			var state = definition.Types[t].Type.States[0];
			type.TimeLeft = type.Length = Range( state.Duration.X, state.Duration.Y );
		}

		_systems.Add( system );
		system.Initializing = true;
		UpdateSystem( system, 0.001f );
		return system;
	}

	static Vector3 ToVector( System.Numerics.Vector3 v ) => new( v.X, v.Y, v.Z );

	void StartAttachedSystem( HaloSystemDefinition definition, GameObject root )
	{
		var controls = FindControls( root );
		var (a, _, color) = ReadControls( controls );
		var carrier = Carrier.For( root );
		carrier.Measure( _now );
		var (group, _) = GroupsFor( root );

		var system = StartSystem( definition, root, root.WorldPosition / Units, carrier.Velocity, new Vector4( color, 1 ), a, group, lit: false );
		if ( system is null )
		{
			_notEffects.Add( root );
			return;
		}

		system.Controls = controls;
		system.Carrier = carrier;
		system.FirstPerson = root.Tags.Has( HaloEffects.FirstPersonTag, false );
		_systemsByRoot[root] = system;
	}

	void UpdateSystems( float dt )
	{
		for ( var i = _systems.Count - 1; i >= 0; i-- )
		{
			var system = _systems[i];
			UpdateSystem( system, dt );
			if ( system.Gone )
			{
				foreach ( var type in system.Types )
					_systemParticleCount -= type.Particles.Count;
				_systems.RemoveAt( i );
			}
		}

		// Carried systems whose object is gone have been orphaned; forget their roots.
		if ( _frame % 60 == 0 )
		{
			foreach ( var root in _systemsByRoot.Keys.Where( r => !r.IsValid() ).ToList() )
				_systemsByRoot.Remove( root );
		}
	}

	void UpdateSystem( SystemInstance system, float dt )
	{
		var definition = system.Definition;
		if ( system.Object is { } carrier )
		{
			if ( !carrier.IsValid() )
			{
				// Halo orphans a system whose object goes: it makes no more, and its particles live out.
				system.Object = null;
				system.Active = false;
			}
			else
			{
				var (a, _, color) = ReadControls( system.Controls );
				system.Active = carrier.Active;
				system.Scale = a;
				system.Color = new Vector4( color, system.Color.w );
				system.Position = carrier.WorldPosition / Units;
				system.Carrier?.Measure( _now );
				system.Velocity = system.Carrier?.Velocity ?? Vector3.Zero;
			}
		}
		else if ( definition.SystemPhysics is { } physics )
		{
			PointPhysics( physics, ref system.Position, ref system.Velocity, 1, dt, out _ );
		}

		var live = 0;
		for ( var t = 0; t < definition.Types.Length; t++ )
		{
			var typeDefinition = definition.Types[t];
			if ( typeDefinition.Disabled )
				continue;

			var type = system.Types[t];
			type.TimeLeft -= dt;
			while ( type.State >= 0 )
			{
				var state = typeDefinition.Type.States[type.State];
				if ( type.TimeLeft < 0 )
				{
					float duration;
					if ( type.Transition < 0 )
					{
						NextTypeState( system, type, typeDefinition );
						duration = Range( state.Transition.X, state.Transition.Y );
					}
					else
					{
						type.State = type.Transition;
						type.Transition = -1;
						var next = typeDefinition.Type.States[type.State];
						duration = Range( next.Duration.X, next.Duration.Y );
					}

					type.Length = duration;
					type.TimeLeft += duration;
				}
				else
				{
					var variables = typeDefinition.StateVariables[type.State];
					if ( type.Transition < 0 )
					{
						variables.CopyTo( type.Variables, 0 );
					}
					else
					{
						var next = typeDefinition.StateVariables[type.Transition];
						var s = Math.Clamp( type.Length != 0 ? type.TimeLeft / type.Length : 0, 0, 1 );
						for ( var v = 0; v < 10; v++ )
							type.Variables[v] = variables[v] * s + next[v] * (1 - s);
					}

					var flags = typeDefinition.Flags;
					if ( (flags & 0x200) != 0 )
					{
						// Tinted by the effect's color.
						type.Variables[3] *= system.Color.w;
						type.Variables[4] *= system.Color.x;
						type.Variables[5] *= system.Color.y;
						type.Variables[6] *= system.Color.z;
					}
					if ( (flags & 0x800) != 0 ) type.Variables[8] *= system.Scale;
					if ( (flags & 0x1000) != 0 ) type.Variables[9] *= system.Scale;
					if ( (flags & 0x2000) != 0 ) type.Variables[0] *= system.Scale;
					if ( (flags & 0x4000) != 0 ) type.Variables[1] *= system.Scale;
					if ( (flags & 0x8000) != 0 ) type.Variables[2] *= system.Scale;
					break;
				}
			}

			if ( type.State < 0 )
				continue;

			if ( system.Active )
				NewSystemParticles( system, t, dt );

			var particles = type.Particles;
			for ( var i = 0; i < particles.Count; i++ )
			{
				var p = particles[i];
				p.TimeLeft -= dt;

				var states = typeDefinition.ParticleStates;
				if ( p.State < 0 && states.Length > 0 )
				{
					p.State = 0;
					var first = states[0].State;
					p.TimeLeft = p.Length = Range( first.Duration.X, first.Duration.Y );
					p.Current = Randomize( states[0].State );
				}

				if ( !p.Valid )
					p.State = -1;

				while ( p.State >= 0 && p.TimeLeft < 0 )
				{
					var state = states[p.State].State;
					float duration;
					if ( p.Transition < 0 )
					{
						NextParticleState( p, typeDefinition );
						duration = Range( state.Transition.X, state.Transition.Y );
					}
					else
					{
						p.State = p.Transition;
						p.Transition = -1;
						var next = states[p.State].State;
						duration = Range( next.Duration.X, next.Duration.Y );
					}

					p.Length = duration;
					p.TimeLeft += duration;
					if ( p.Transition >= 0 )
						p.Next = Randomize( states[p.Transition].State );
					else
						p.Current = p.Next;
				}

				if ( p.State < 0 )
				{
					particles.RemoveAt( i-- );
					_systemParticleCount--;
					continue;
				}

				var multipliers = type.Variables;
				if ( p.Transition < 0 )
				{
					p.Rotation += p.Current.RotationRate * multipliers[2] * dt;
					p.Sprite += p.Current.AnimationRate * multipliers[1] * dt;
				}
				else
				{
					var s = Math.Clamp( p.Length != 0 ? p.TimeLeft / p.Length : 0, 0, 1 );
					p.Rotation += (p.Next.RotationRate * (1 - s) + p.Current.RotationRate * s) * multipliers[2] * dt;
					p.Sprite += (p.Next.AnimationRate * (1 - s) + p.Current.AnimationRate * s) * multipliers[1] * dt;
				}

				MoveSystemParticle( system, typeDefinition, type, p, dt );
			}

			live++;
		}

		system.Initializing = false;
		if ( live == 0 && system.Object is null )
			system.Gone = true;
	}

	// On to a type's next state: forward, or back and forth, looping only while an object carries the system; a type out of states is over.
	static void NextTypeState( SystemInstance system, TypeInstance type, HaloSystemType definition )
	{
		var step = type.Forward ? 1 : -1;
		var next = type.State + step;
		type.Transition = next;
		var count = definition.Type.States.Count;
		if ( next >= 0 && next < count )
			return;

		var flags = definition.Flags;
		if ( (flags & 0x1) == 0 || system.Object is null || count <= 0 )
		{
			type.State = -1;
			type.Transition = -1;
		}
		else if ( (flags & 0x2) != 0 )
		{
			type.Transition = Math.Clamp( type.State - step, 0, count - 1 );
			type.Forward = !type.Forward;
		}
		else
		{
			type.Transition = 0;
		}
	}

	static void NextParticleState( SystemParticle p, HaloSystemType definition )
	{
		var step = p.Forward ? 1 : -1;
		var next = p.State + step;
		p.Transition = next;
		var count = definition.ParticleStates.Length;
		if ( next >= 0 && next < count )
			return;

		var flags = definition.Flags;
		if ( (flags & 0x4) != 0 && count > 0 )
		{
			if ( (flags & 0x8) != 0 )
			{
				p.Transition = Math.Clamp( p.State - step, 0, count - 1 );
				p.Forward = !p.Forward;
			}
			else
			{
				p.Transition = 0;
			}
		}
		else
		{
			p.State = -1;
			p.Transition = -1;
		}
	}

	StateValues Randomize( ParticleState state )
	{
		var t = Rand();
		return new StateValues
		{
			AnimationRate = Range( state.AnimationRate.X, state.AnimationRate.Y ),
			RotationRate = Range( state.RotationRate.X, state.RotationRate.Y ),
			Scale = Range( state.Scale.X, state.Scale.Y ),
			Color = new Vector4(
				(state.Color2.X - state.Color1.X) * t + state.Color1.X,
				(state.Color2.Y - state.Color1.Y) * t + state.Color1.Y,
				(state.Color2.Z - state.Color1.Z) * t + state.Color1.Z,
				Range( state.Color1.W, state.Color2.W ) ),
		};
	}

	// A type's new particles: its initial count at first, then its creation rate.
	void NewSystemParticles( SystemInstance system, int typeIndex, float dt )
	{
		var definition = system.Definition.Types[typeIndex];
		var type = system.Types[typeIndex];
		var particles = type.Particles;

		int target;
		if ( system.Initializing )
		{
			var initial = definition.Type.InitialCount;
			target = (definition.Flags & 0x400) != 0 ? (int)(initial * system.Scale + 0.5f) : initial;
		}
		else
		{
			var count = dt * type.Variables[9];
			var whole = (int)count;
			target = particles.Count + whole;
			type.Fraction += count - whole;
			if ( type.Fraction > 1 )
			{
				target++;
				type.Fraction -= 1;
			}
		}

		if ( particles.Count < target )
		{
			var position = system.Position;
			var forward = system.Object is { } carrier && carrier.IsValid() ? carrier.WorldRotation.Forward : Vector3.Zero;
			var creation = system.Initializing ? definition.Type.CreationPhysics : definition.Type.States[type.State].CreationPhysics;

			for ( var created = 0; particles.Count < target && created < MaxCreatedPerUpdate && _systemParticleCount < MaxSystemParticles; created++ )
			{
				var p = new SystemParticle { Rotation = Rand() * MathF.PI * 2 };
				switch ( creation )
				{
					case 1:
						CreateExplosionParticle( system, definition, p, position );
						break;
					case 2:
						CreateJetParticle( system, definition, p, position, forward );
						break;
					default:
						p.Position = position;
						p.Velocity = system.Velocity;
						break;
				}

				particles.Add( p );
				_systemParticleCount++;
			}
		}

		if ( particles.Count < type.Variables[8] )
			type.TimeLeft *= 0.3f;
	}

	static float Constant( HaloSystemType type, int index ) => index < type.Constants.Length ? type.Constants[index] : 0;

	// An explosion's particle: offset and velocity along a random direction scaled by the spread.
	void CreateExplosionParticle( SystemInstance system, HaloSystemType type, SystemParticle p, Vector3 center )
	{
		var xy = Constant( type, 0 );
		var z = Constant( type, 1 );
		var intensity = Constant( type, 2 );

		var offset = RandomDirection();
		offset = new Vector3( offset.x * xy, offset.y * xy, offset.z * z );
		p.Position = center + offset;
		p.Axis = new Vector3( -offset.y, offset.x, 0 );
		p.Velocity = offset * intensity + system.Velocity;
	}

	// A jet's particle: k / 30 world units per second, part along the forward and part random.
	void CreateJetParticle( SystemInstance system, HaloSystemType type, SystemParticle p, Vector3 position, Vector3 forward )
	{
		var speed = Constant( type, 0 ) * Tick;
		var spread = Constant( type, 1 );
		var rotatesUp = Constant( type, 2 ) != 0;

		p.Velocity = forward * ((1 - spread) * speed) + RandomDirection() * (speed * spread) + system.Velocity;
		p.Position = position;
		p.Axis = rotatesUp ? p.Velocity.Cross( Vector3.Up ) : forward.Cross( p.Velocity );
	}

	// Moves a system particle by its state's point physics, blended over a transition as Halo blends it.
	void MoveSystemParticle( SystemInstance system, HaloSystemType definition, TypeInstance type, SystemParticle p, float dt )
	{
		var state = definition.ParticleStates[p.State];
		var radiusScale = definition.Type.Radius * type.Variables[7];
		float radius;
		HaloPhysics physics;
		if ( p.Transition < 0 )
		{
			radius = radiusScale * state.State.RadiusMultiplier;
			physics = state.Physics;
		}
		else
		{
			var next = definition.ParticleStates[p.Transition];
			var s = Math.Clamp( p.Length != 0 ? p.TimeLeft / p.Length : 0, 0, 1 );
			radius = radiusScale * (state.State.RadiusMultiplier * s + next.State.RadiusMultiplier * (1 - s));

			// As Halo: (1 - s) of this state's and s of the next's.
			physics = HaloPhysics.Lerp( state.Physics, next.Physics, s, _blendedPhysics );
		}

		var result = PointPhysics( physics, ref p.Position, ref p.Velocity, MathF.Max( radius, 0 ), dt, out _ );
		var flags = definition.Flags;
		if ( ((result & InAir) != 0 && (flags & 0x20) != 0) || ((result & InWater) != 0 && (flags & 0x10) != 0) || ((result & CollidedWithStructure) != 0 && (flags & 0x40) != 0) )
			p.Valid = false;
	}
}
