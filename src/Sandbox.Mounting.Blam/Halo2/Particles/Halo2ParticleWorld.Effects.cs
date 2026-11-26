namespace Sandbox.Mounting.Blam.Halo2;

// Effects: Halo 2's effect update, a tick at a time.
sealed partial class Halo2ParticleWorld
{
	// Where an effect makes things: an object (a marker's child of the root, or the root), or a fixed place for effects the runtime starts itself.
	struct Place
	{
		public GameObject Object;
		public Vector3 Position;
		public Rotation Rotation;

		public bool Valid => Object is null || Object.IsValid();

		// Where it is (world units) and which way it faces.
		public (Vector3 Position, Rotation Rotation) Frame => Object is null ? (Position, Rotation) : (Object.WorldPosition / Units, Object.WorldRotation);
	}

	sealed class EffectInstance
	{
		public Halo2EffectDefinition Definition;

		// Null for an effect the runtime started at a fixed place (a particle's collision or death effect).
		public GameObject Root;
		public GameObject Controls;
		public Place Fixed;

		// Each location's places: every child of the root named after its marker (Halo makes things at each instance), else the root.
		public List<Place>[] Locations;
		public int LocationsFrame = -1;

		public bool Looping;
		public bool FirstPerson;
		public bool Dormant;
		public bool Done;

		public int Event;
		public bool InDuration;
		public float Time;
		public float End;

		public float A = 1, B = 1;
		public Carrier Carrier;

		// A velocity it was given (world units per second), added to its carrier's.
		public Vector3 Velocity;

		public Vector3 TotalVelocity => (Carrier?.Velocity ?? Vector3.Zero) + Velocity;

		// Its particle systems by event and system index, kept across loops as Halo keeps them.
		public readonly Dictionary<(int Event, int System), SystemInstance> Systems = new();

		// The sounds its parts are playing, by event, sound and place, so a looping effect doesn't stack copies.
		public readonly Dictionary<(int Event, int Sound, int Place), SoundHandle> Sounds = new();

		// The same for a game that plays the sounds: the clones of its sound child (Halo2ParticleEffects).
		public readonly Dictionary<(int Event, int Sound, int Place), GameObject> GameSounds = new();
	}

	readonly List<EffectInstance> _effects = new();
	readonly Dictionary<GameObject, EffectInstance> _effectsByRoot = new();

	// Halo 2's limits: past them nothing more is made until something ends.
	const int MaxEffects = 512;

	// Most event phases an effect goes through in one tick.
	const int MaxPhaseChanges = 8;

	EffectInstance StartEffect( Halo2EffectDefinition definition, GameObject root, Place? place, Vector3 velocity = default )
	{
		if ( _effects.Count >= MaxEffects )
			return null;

		var effect = new EffectInstance
		{
			Definition = definition,
			Root = root,
			Controls = root is null ? null : FindChild( root, Halo2ParticleEffects.ControlsName ),
			Fixed = place ?? default,
			Looping = root is not null && root.Tags.Has( Halo2ParticleEffects.LoopTag, false ),
			FirstPerson = root is not null && root.Tags.Has( Halo2ParticleEffects.FirstPersonTag, false ),
			Carrier = root is null ? null : new Carrier { Object = root },
			Velocity = velocity,
		};

		effect.Locations = new List<Place>[definition.Tag.Locations.Count];
		for ( var i = 0; i < effect.Locations.Length; i++ )
			effect.Locations[i] = new List<Place>();
		FindLocations( effect );

		(effect.A, effect.B) = ReadControls( effect.Controls );
		effect.Carrier?.Measure( _now );

		_effects.Add( effect );
		if ( root is not null )
			_effectsByRoot[root] = effect;

		if ( definition.Events.Length == 0 )
		{
			effect.Done = true;
			return effect;
		}

		// Event 0's delay starts now, and a zero delay begins its duration at once. An "instant" effect (tag flag 2: every event one tick) plays out here unless it loops.
		StartDelay( effect, 0 );
		UpdateEffect( effect, (definition.Tag.Flags & 0x2) != 0 && !effect.Looping ? 1 : 0 );
		return effect;
	}

	void UpdateEffects( float dt )
	{
		for ( var i = _effects.Count - 1; i >= 0; i-- )
		{
			var effect = _effects[i];
			var root = effect.Root;
			if ( root is not null )
			{
				if ( !root.IsValid() )
				{
					effect.Done = true;
				}
				else if ( !root.Active )
				{
					// A looping effect goes dormant (or stops, if the tag says it's deleted when its attachment deactivates); a one-shot one ends.
					if ( !effect.Looping || (effect.Definition.Tag.Flags & 0x1) != 0 )
						effect.Done = true;
					else
						effect.Dormant = true;
				}
				else if ( effect.Dormant )
				{
					// Back on: from event 0 again.
					effect.Dormant = false;
					StartDelay( effect, 0 );
				}
			}

			if ( !effect.Done && !effect.Dormant )
			{
				(effect.A, effect.B) = ReadControls( effect.Controls );
				effect.Carrier?.Measure( _now );
				UpdateEffect( effect, dt );
			}

			if ( effect.Done )
				EndEffect( i );
		}
	}

	void EndEffect( int index )
	{
		var effect = _effects[index];
		_effects.RemoveAt( index );
		foreach ( var system in effect.Systems.Values )
			system.Effect = null;

		if ( effect.Root is { } root )
		{
			if ( _effectsByRoot.TryGetValue( root, out var known ) && known == effect )
				_effectsByRoot.Remove( root );
			if ( root.IsValid() && root.Active )
				_finished.Add( root );
		}
	}

	void StartDelay( EffectInstance effect, int index )
	{
		var ev = effect.Definition.Events[index].Event;
		effect.Event = index;
		effect.InDuration = false;
		effect.Time = 0;
		effect.End = Range( ev.Delay.X, ev.Delay.Y );
	}

	void StartDuration( EffectInstance effect )
	{
		var ev = effect.Definition.Events[effect.Event];
		effect.InDuration = true;
		effect.Time = 0;
		effect.End = Range( ev.Event.Duration.X, ev.Event.Duration.Y );
		PlaySounds( effect, ev );
		PlaceDecals( effect, ev );
	}

	// The next event, skipping those whose skip fraction the dice beat; a looping effect goes from its last back to its loop start event. False when a one-shot effect is over.
	bool NextEvent( EffectInstance effect )
	{
		var events = effect.Definition.Events;
		var next = effect.Event + 1;
		for ( var guard = 0; guard < events.Length * 2; guard++ )
		{
			if ( next >= events.Length )
			{
				if ( !effect.Looping )
					return false;

				next = Math.Max( effect.Definition.Tag.LoopStartEvent, 0 );
				if ( next >= events.Length )
					return false;
			}

			if ( Rand() >= events[next].Event.SkipFraction )
			{
				StartDelay( effect, next );
				return true;
			}

			next++;
		}

		return false;
	}

	// Each event's delay, then its duration, through which its particle systems emit.
	void UpdateEffect( EffectInstance effect, float dt )
	{
		for ( var change = 0; change < MaxPhaseChanges && !effect.Done; change++ )
		{
			var remaining = effect.End - effect.Time;
			var completed = remaining <= dt + 1e-6f;
			var step = completed ? MathF.Max( remaining, 0 ) : dt;

			if ( effect.InDuration )
				Emit( effect, effect.Time, step );

			effect.Time += step;
			dt = MathF.Max( dt - step, 0 );
			if ( !completed )
				break;

			if ( !effect.InDuration )
				StartDuration( effect );
			else if ( !NextEvent( effect ) )
				effect.Done = true;
		}
	}

	// A step of an event's duration: each of its particle systems emits at each of its location's places.
	void Emit( EffectInstance effect, float time, float step )
	{
		if ( effect.Root is not null && _frame != effect.LocationsFrame )
		{
			effect.LocationsFrame = _frame;
			FindLocations( effect );
		}

		var ev = effect.Definition.Events[effect.Event];
		for ( var s = 0; s < ev.Systems.Length; s++ )
		{
			var definition = ev.Systems[s];

			// Camera mode 1 is made only for an effect seen in its local player's first person view, 2 only for any other.
			if ( definition.System.CameraMode == (effect.FirstPerson ? 2 : 1) )
				continue;

			if ( !effect.Systems.TryGetValue( (effect.Event, s), out var system ) || !system.Alive )
			{
				if ( _systems.Count >= MaxSystems )
					continue;

				system = NewSystem( definition, effect );
				effect.Systems[(effect.Event, s)] = system;
			}

			// The emitter's age runs 0-1 over the event's duration.
			system.EmitterAge = effect.End > 0 ? Math.Clamp( (time + step) / effect.End, 0, 1 ) : 1;
			var location = definition.System.Location;
			EmitSystem( system, location >= 0 && location < effect.Locations.Length ? effect.Locations[location] : null, step );
		}
	}

	// Looks each location's places up again: a game may add a marker's other instances after the effect starts.
	static void FindLocations( EffectInstance effect )
	{
		var names = effect.Definition.Tag.Locations;
		for ( var i = 0; i < names.Count; i++ )
		{
			var list = effect.Locations[i];
			list.Clear();
			if ( effect.Root is null )
			{
				list.Add( FixedPlace( effect.Fixed, names[i] ) );
				continue;
			}

			if ( !string.IsNullOrEmpty( names[i] ) )
			{
				foreach ( var child in effect.Root.Children )
				{
					if ( child.Name == names[i] && list.Count < MaxLocationPlaces )
						list.Add( new Place { Object = child } );
				}
			}

			if ( list.Count == 0 )
				list.Add( new Place { Object = effect.Root } );
		}
	}

	// Halo's most instances of one location.
	const int MaxLocationPlaces = 16;

	// A location of an effect made at a fixed place (forward the surface normal or the way a particle went): "gravity" points down and "up" up.
	static Place FixedPlace( Place place, string name ) => name switch
	{
		"gravity" => place with { Rotation = Rotation.LookAt( Vector3.Down, MathF.Abs( place.Rotation.Forward.z ) > 0.99f ? Vector3.Forward : place.Rotation.Forward ) },
		"up" => place with { Rotation = Rotation.LookAt( Vector3.Up, MathF.Abs( place.Rotation.Forward.z ) > 0.99f ? Vector3.Forward : place.Rotation.Forward ) },
		_ => place,
	};

	readonly Dictionary<string, SoundEvent> _soundEvents = new();

	// An event's sound parts, at each place of their locations (a looping effect's copy still playing isn't started again).
	// A root with a sound child has its game play them instead (Halo2ParticleEffects).
	void PlaySounds( EffectInstance effect, Halo2EventDefinition ev )
	{
		var gameSound = effect.Root is null ? null : FindChild( effect.Root, Halo2ParticleEffects.SoundName );
		for ( var s = 0; s < ev.Sounds.Length; s++ )
		{
			var sound = ev.Sounds[s];
			if ( sound.Location < 0 || sound.Location >= effect.Locations.Length )
				continue;

			SoundEvent soundEvent = null;
			if ( gameSound is null )
			{
				if ( !_soundEvents.TryGetValue( sound.Event, out soundEvent ) )
					_soundEvents[sound.Event] = soundEvent = GameResource.Load<SoundEvent>( sound.Event );
				if ( soundEvent is null )
					continue;
			}

			var places = effect.Locations[sound.Location];
			for ( var p = 0; p < places.Count; p++ )
			{
				if ( !places[p].Valid )
					continue;

				var key = (effect.Event, s, p);
				if ( gameSound is not null )
				{
					if ( effect.GameSounds.TryGetValue( key, out var clone ) && clone.IsValid() )
						continue;

					clone = gameSound.Clone( global::Transform.Zero, places[p].Object, false, sound.Tag );
					clone.LocalTransform = global::Transform.Zero;
					clone.Enabled = true;
					effect.GameSounds[key] = clone;
					continue;
				}

				if ( effect.Sounds.TryGetValue( key, out var playing ) && playing.IsValid && !playing.IsStopped && !playing.Finished )
					continue;

				var handle = Sandbox.Sound.Play( soundEvent, places[p].Frame.Position * Units );
				if ( handle is not null && handle.IsValid )
				{
					if ( places[p].Object is { } follow )
					{
						handle.Parent = follow;
						handle.FollowParent = true;
					}
					effect.Sounds[key] = handle;
				}
			}
		}
	}
}
