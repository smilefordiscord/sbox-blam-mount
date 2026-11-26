using Blam.Halo1;

namespace Sandbox.Mounting.Blam.Halo1;

// Particles (part): Halo's particles_update, every frame.
sealed partial class HaloParticleWorld
{
	// A particle, in world units: in the world, or in its location's space while attached.
	struct Particle
	{
		// Null for a free slot.
		public HaloParticleDefinition Definition;

		// The location it stays attached to, or null.
		public GameObject Attached;

		public Vector3 Position;
		public Vector3 Direction;
		public Vector3 Velocity;
		public float Rotation;
		public float AngularVelocity;
		public float Radius;
		public float Age;
		public float Lifespan;
		public float FrameSpan;
		public float FrameTime;

		// Halo's color in gamma space (rgb, alpha), lit when it was born.
		public Vector4 Color;

		public short Sequence;
		public short Frame;
		public byte State;
		public byte Flags;
		public int Group;

		// When a view last drew it; Halo removes particles no view has drawn for a while.
		public float LastDrawn;
	}

	// Particle flags.
	const byte Backwards = 0x1;
	const byte MirrorU = 0x2;
	const byte MirrorV = 0x4;
	const byte AtRest = 0x8;

	// A particle's place in its sequences: picking an initial sequence, then looping ones, then a final one.
	const byte InitialState = 0;
	const byte LoopingState = 1;
	const byte StillLoopingState = 2;
	const byte FinalState = 3;

	// Halo's particle limit: past it, no more are made until some die.
	const int MaxParticles = 1024;

	// Halo removes a particle no view has drawn in 15 of its frames (at 30 a second).
	const float UndrawnLife = 0.5f;

	// Slower than this (world units per second) a particle that has settled is at rest.
	const float RestSpeed = 0.25f;

	readonly Particle[] _particles = new Particle[MaxParticles];
	readonly Stack<int> _freeParticles = new();
	int _particleHigh;

	void ClearParticles()
	{
		Array.Clear( _particles );
		_freeParticles.Clear();
		_particleHigh = 0;
		_systemParticleCount = 0;
	}

	void NewParticle( HaloParticleDefinition definition, GameObject attached, Vector3 position, Vector3 direction, Vector3 velocity, float radius, float spin, float rotation, Vector4 color, int group )
	{
		int index;
		if ( _freeParticles.Count > 0 )
			index = _freeParticles.Pop();
		else if ( _particleHigh < MaxParticles )
			index = _particleHigh++;
		else
			return;

		var tag = definition.Tag;
		ref var p = ref _particles[index];
		p = default;
		p.Definition = definition;
		p.Attached = attached;
		p.Position = position;
		p.Direction = direction;
		p.Velocity = velocity;
		p.Rotation = rotation;
		p.AngularVelocity = spin;
		p.Radius = radius;
		p.Color = color;
		p.Group = group;
		p.LastDrawn = _now;
		p.State = InitialState;

		if ( tag.CanAnimateBackwards && Rand() < 0.5f ) p.Flags |= Backwards;
		if ( tag.RandomHorizontalMirroring && Rand() < 0.5f ) p.Flags |= MirrorU;
		if ( tag.RandomVerticalMirroring && Rand() < 0.5f ) p.Flags |= MirrorV;

		p.Lifespan = Range( tag.Lifespan.X, tag.Lifespan.Y );
		p.FrameSpan = tag.AnimationRate.Y != 0 ? 1 / Range( tag.AnimationRate.X, tag.AnimationRate.Y ) : float.MaxValue;
		p.FrameTime = -1;

		// Lit by the lightmap under it and tinted by the ground's color, once, as it's born.
		if ( !tag.SelfIlluminated || tag.TintFromDiffuseTexture )
		{
			var world = attached is null ? position : attached.WorldTransform.PointToWorld( position * Units ) / Units;
			var ground = Level?.ParticleGround( world * Units ) ?? (ParticleLighting.Grey, ParticleLighting.Grey);
			var m = ParticleLighting.Multiplier( ground, !tag.SelfIlluminated, tag.TintFromDiffuseTexture );
			p.Color = new Vector4( p.Color.x * m.X, p.Color.y * m.Y, p.Color.z * m.Z, p.Color.w );
		}

		if ( !NextSequence( index ) )
			return;

		var sprites = definition.Look.Sheet.SpriteCount( p.Sequence );
		if ( tag.RandomStartFrame )
			p.Frame = (short)(((p.Flags & Backwards) != 0 ? 1 : -1) + RangeInt( 0, sprites ));
		else
			p.Frame = (short)((p.Flags & Backwards) != 0 ? sprites : -1);
	}

	void FreeParticle( int index )
	{
		_particles[index] = default;
		_freeParticles.Push( index );
	}

	// Halo's particle death: its death effect where it is (at scale 0), and it's gone.
	void Die( int index )
	{
		ref var p = ref _particles[index];
		var definition = p.Definition;
		if ( definition.DeathEffect is not null || definition.DeathSound is not null )
			PlayContact( definition.DeathEffect, definition.DeathSound, ref p, 0 );

		FreeParticle( index );
	}

	// On to the next sequence: initial, then looping while it has life left, then final; false if it died.
	bool NextSequence( int index )
	{
		ref var p = ref _particles[index];
		var tag = p.Definition.Tag;
		var sequence = -1;

		if ( p.State == InitialState )
		{
			if ( tag.InitialSequenceCount > 0 )
				sequence = tag.FirstSequence + RangeInt( 0, tag.InitialSequenceCount );
			p.State++;
		}

		if ( (sequence < 0 && p.State == LoopingState) || p.State == StillLoopingState )
		{
			if ( p.State == LoopingState )
				p.State = StillLoopingState;

			if ( p.Age < p.Lifespan && tag.LoopingSequenceCount > 0 )
				sequence = tag.InitialSequenceCount + tag.FirstSequence + RangeInt( 0, tag.LoopingSequenceCount );
			else
				p.State++;
		}

		if ( sequence < 0 && p.State == FinalState )
		{
			if ( tag.FinalSequenceCount > 0 )
				sequence = tag.LoopingSequenceCount + tag.InitialSequenceCount + tag.FirstSequence + RangeInt( 0, tag.FinalSequenceCount );
			p.State++;
		}

		var sequences = p.Definition.Look.Sheet.SequenceCount;
		if ( sequence < 0 || sequences == 0 )
		{
			Die( index );
			return false;
		}

		// A sequence past the bitmap's last is its last (the plasma impacts' flare).
		p.Sequence = (short)Math.Clamp( sequence, 0, sequences - 1 );
		return true;
	}

	bool NextFrame( int index )
	{
		ref var p = ref _particles[index];
		p.FrameTime = 0;

		if ( (p.Flags & Backwards) != 0 )
		{
			if ( p.Frame > 0 )
			{
				p.Frame--;
				return true;
			}

			if ( !NextSequence( index ) )
				return false;

			ref var q = ref _particles[index];
			q.Frame = (short)(q.Definition.Look.Sheet.SpriteCount( q.Sequence ) - 1);
			return true;
		}

		if ( p.Frame + 1 < p.Definition.Look.Sheet.SpriteCount( p.Sequence ) )
		{
			p.Frame++;
			return true;
		}

		if ( !NextSequence( index ) )
			return false;

		_particles[index].Frame = 0;
		return true;
	}

	bool UpdateFrame( int index, float dt )
	{
		ref var p = ref _particles[index];
		var tag = p.Definition.Tag;
		if ( tag.AnimationStopsAtRest && (p.Flags & AtRest) != 0 )
			return true;

		// "Animate once per frame": a sprite every frame, whatever the rate.
		if ( tag.AnimateOncePerFrame )
			return dt == 0 || NextFrame( index );

		var alive = true;
		if ( p.FrameTime == -1 )
		{
			alive = NextFrame( index );
			if ( alive )
				_particles[index].FrameTime = 0;
		}

		for ( var guard = 0; dt > 0 && alive && guard < 256; guard++ )
		{
			ref var q = ref _particles[index];
			var remaining = q.FrameSpan - q.FrameTime;
			if ( remaining <= dt )
			{
				alive = NextFrame( index );
				dt -= remaining;
			}
			else
			{
				q.FrameTime += dt;
				break;
			}
		}

		return alive;
	}

	// A particle's radius now: its radius times its radius animation over its lifespan.
	static float RadiusOf( in Particle p )
	{
		var animation = p.Definition.Tag.RadiusAnimation;
		var t = p.Lifespan > 0 ? p.Age / p.Lifespan : 1;
		return ((animation.Y - animation.X) * t + animation.X) * p.Radius;
	}

	void UpdateParticles( float dt )
	{
		_contactsThisFrame = 0;
		for ( var i = 0; i < _particleHigh; i++ )
		{
			ref var p = ref _particles[i];
			if ( p.Definition is null )
				continue;

			if ( _now - p.LastDrawn > UndrawnLife )
			{
				FreeParticle( i );
				continue;
			}

			var wasNew = p.Age == 0;
			p.Age += dt;
			if ( p.Age >= p.Lifespan && !wasNew && p.Definition.Tag.FinalSequenceCount == 0 )
			{
				Die( i );
				continue;
			}

			if ( UpdateFrame( i, dt ) )
				UpdatePhysics( i, dt );
		}

		while ( _particleHigh > 0 && _particles[_particleHigh - 1].Definition is null )
			_particleHigh--;
		if ( _freeParticles.Count > 0 && _freeParticles.Any( f => f >= _particleHigh ) )
		{
			var keep = _freeParticles.Where( f => f < _particleHigh ).ToArray();
			_freeParticles.Clear();
			foreach ( var f in keep.Reverse() )
				_freeParticles.Push( f );
		}
	}

	// Halo's particle physics: point physics through the level when free, only air friction when attached.
	void UpdatePhysics( int index, float dt )
	{
		ref var p = ref _particles[index];
		if ( (p.Flags & AtRest) != 0 )
		{
			if ( p.Attached is not null && !p.Attached.IsValid() )
				FreeParticle( index );
			return;
		}

		var definition = p.Definition;
		var tag = definition.Tag;
		var physics = definition.Physics;
		var settled = false;

		if ( p.Attached is null )
		{
			var result = PointPhysics( physics, ref p.Position, ref p.Velocity, RadiusOf( p ), dt, out var normal );

			if ( (result & CollidedWithStructure) != 0 )
			{
				var collides = definition.CollisionEffect is not null || definition.CollisionSound is not null;
				if ( collides )
				{
					var scale = Math.Clamp( (p.Velocity.Length - 0.5f) / (1.5f - 0.5f), 0, 1 );
					PlayContact( definition.CollisionEffect, definition.CollisionSound, ref p, scale );
				}

				if ( tag.DiesOnContact )
				{
					if ( !collides )
						Die( index );
					else
						FreeParticle( index );
					return;
				}
			}

			if ( ((result & InAir) != 0 && tag.DiesInAir) || ((result & InWater) != 0 && tag.DiesInWater) )
			{
				Die( index );
				return;
			}

			if ( (result & (CollidedWithStructure | CollidedWithWater)) != 0 )
			{
				if ( normal.z > ParticleTagRestSlope )
					settled = true;
				p.FrameSpan += tag.ContactDeterioration;
			}
		}
		else
		{
			if ( !p.Attached.IsValid() )
			{
				FreeParticle( index );
				return;
			}

			// Attached: air friction only, in the location's space.
			var radius = RadiusOf( p );
			var friction = physics.AirFriction * radius * radius;
			var mass = physics.MassOverRadiusCubed * radius * radius * radius;
			var scale = mass == 0 ? (friction == 0 ? 1 : 0) : Math.Clamp( 1 - friction / mass * dt, 0, 1 );
			p.Velocity *= scale;
			p.Position += p.Velocity * dt;
			settled = true;
		}

		if ( p.Velocity.LengthSquared < RestSpeed * RestSpeed )
		{
			if ( settled )
			{
				if ( tag.DiesAtRest )
				{
					Die( index );
					return;
				}

				p.Flags |= AtRest;
			}
		}
		else
		{
			p.Direction = p.Velocity;
		}

		p.Rotation += dt * p.AngularVelocity;
	}

	// Ground flatter than this (its normal's z) is level enough to come to rest on.
	const float ParticleTagRestSlope = 0.8f;

	// Most collision and death effects started in one frame: a burst of debris landing at once shouldn't spawn hundreds of prefabs.
	const int MaxContactsPerFrame = 24;

	int _contactsThisFrame;

	// Plays a collision or death effect at the particle, at scale A and B 0, or its sound.
	void PlayContact( string path, HaloContactSound sound, ref Particle p, float scale )
	{
		if ( _contactsThisFrame >= MaxContactsPerFrame )
			return;

		_contactsThisFrame++;
		var position = p.Position;
		var direction = p.Direction;
		var velocity = p.Velocity;
		if ( p.Attached is { } attached )
		{
			if ( !attached.IsValid() )
				return;

			var rotation = attached.WorldRotation;
			position = attached.WorldTransform.PointToWorld( position * Units ) / Units;
			direction = rotation * direction;
			velocity = rotation * velocity;
		}

		try
		{
			if ( sound is not null )
			{
				PlayImpulseSound( sound, position, scale );
				return;
			}

			var forward = direction.LengthSquared > 1e-8f ? direction.Normal : Vector3.Up;
			var up = MathF.Abs( forward.z ) > 0.99f ? Vector3.Forward : Vector3.Up;
			GameObject go;
			using ( Scene.Push() )
				go = GameObject.Clone( path, new Transform( position * Units, Rotation.LookAt( forward, up ) ) );
			if ( go is null )
				return;


			go.Flags |= GameObjectFlags.NotSaved | GameObjectFlags.NotNetworked;
			foreach ( var child in go.Children )
			{
				if ( child.Name == "gravity" )
					child.WorldRotation = Rotation.LookAt( Vector3.Down, MathF.Abs( forward.z ) > 0.99f ? Vector3.Forward : forward );
				else if ( child.Name == HaloEffects.ControlsName )
					child.LocalPosition = new Vector3( scale, 0, 0 );
			}

			// Started now, moving as the particle did.
			if ( go.Tags.Has( HaloEffects.RootTag, false ) && !_effectsByRoot.ContainsKey( go ) && !Start( go, velocity ) )
				_notEffects.Add( go );
		}
		catch ( Exception e )
		{
			HaloCeMount.Current?.Logger.Warning( $"Couldn't play {path ?? sound?.Event}: {e.Message}" );
		}
	}

	readonly Dictionary<string, SoundEvent> _sounds = new();

	// Copies of each contact sound playing, oldest first.
	readonly Dictionary<HaloContactSound, List<(SoundHandle Handle, float Start)>> _soundCopies = new();

	// Each promoting sound's counter, in seconds of its longest permutation, and when it was last played.
	readonly Dictionary<HaloContactSound, (float Counter, float Time)> _promotions = new();

	static float Scaled( Vector2 modifiers, float scale ) => (modifiers.y - modifiers.x) * scale + modifiers.x;

	// Halo's impulse sound at a point in world units. It doesn't play at scale 0 with no gain there,
	// when skipped, beyond its maximum distance, or with its copies all too new or nearer the listener.
	void PlayImpulseSound( HaloContactSound sound, Vector3 position, float scale )
	{
		if ( scale == 0 && sound.GainModifiers.x == 0 )
			return;
		if ( Rand() <= sound.SkipFraction * Scaled( sound.SkipModifiers, scale ) )
			return;

		var listener = Sandbox.Sound.Listener.Position / Units;
		var distanceSquared = (position - listener).LengthSquared;
		if ( distanceSquared > sound.MaximumDistance * sound.MaximumDistance )
			return;

		// Each play adds its longest permutation to a counter that runs down in real time.
		if ( sound.PromotionCount > 0 )
		{
			_promotions.TryGetValue( sound, out var promotion );
			var counter = MathF.Max( 0, promotion.Counter - (_now - promotion.Time) ) + sound.LongestSeconds;
			if ( counter > sound.PromotionCount * sound.LongestSeconds )
			{
				_promotions[sound] = (sound.Promotion is null ? counter - sound.LongestSeconds : 0, _now);
				if ( sound.Promotion is not null )
					PlayImpulseSound( sound.Promotion, position, scale );
				return;
			}
			_promotions[sound] = (counter, _now);
		}

		if ( !_soundCopies.TryGetValue( sound, out var copies ) )
			_soundCopies[sound] = copies = new();
		copies.RemoveAll( c => !c.Handle.IsValid || c.Handle.IsStopped || c.Handle.Finished );

		if ( sound.Copies > 0 && copies.Count >= sound.Copies )
		{
			var taken = copies.FindIndex( c => _now - c.Start >= sound.PreemptionSeconds
				&& distanceSquared - (c.Handle.Position / Units - listener).LengthSquared < 1 );
			if ( taken < 0 )
				return;
			copies[taken].Handle.Stop( 0 );
			copies.RemoveAt( taken );
		}

		if ( !_sounds.TryGetValue( sound.Event, out var soundEvent ) )
			_sounds[sound.Event] = soundEvent = GameResource.Load<SoundEvent>( sound.Event );
		if ( soundEvent is null )
			return;

		var handle = Sandbox.Sound.Play( soundEvent, position * Units );
		if ( handle is null || !handle.IsValid )
			return;
		handle.Volume *= Scaled( sound.GainModifiers, scale );
		var pitch = Scaled( sound.PitchModifiers, scale );
		if ( pitch > 0 )
			handle.Pitch *= pitch;
		copies.Add( (handle, _now) );
	}
}
