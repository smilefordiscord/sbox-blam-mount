namespace Sandbox.Mounting.Blam.Halo3;

// Effects: each event's delay, then its duration, through which its particle systems emit; one event after another (or all at once for "run events in parallel"), looping from the loop start event for effects objects carry.
sealed partial class Halo3ParticleWorld
{
	// Where an effect makes things: an object (a marker's child of the root, or the root), or a fixed place for effects the runtime starts itself.
	struct Place
	{
		public GameObject Object;
		public Vector3 Position;
		public Rotation Rotation;

		// 1 the world's up, 2 its down: an object's "up" and "gravity" locations (Halo3Effects.WorldUpTag), turned with neither the object nor its marker.
		public int Toward;

		public bool Valid => Object is null || Object.IsValid();

		// Where it is (world units) and which way it faces.
		public (Vector3 Position, Rotation Rotation) Frame => Object is null ? (Position, Rotation)
			: Toward == 0 ? (Object.WorldPosition / Units, Object.WorldRotation)
			: (Object.WorldPosition / Units, Upright( Toward == 1, Object.Parent?.WorldRotation ?? Object.WorldRotation ));

		// Up: forward the world's up, up the carrier's left (the carrier's forward its y: MCC's frames for Valhalla's man cannons); down: forward the world's down, up the carrier's forward. Its forward or left when the other lies along the vertical.
		static Rotation Upright( bool up, Rotation carrier )
		{
			var side = up ? carrier.Left : carrier.Forward;
			if ( MathF.Abs( side.z ) > 0.99f )
				side = up ? carrier.Forward : carrier.Up;
			return Rotation.LookAt( up ? Vector3.Up : Vector3.Down, side );
		}
	}

	// One event's progress: its delay, then its duration.
	sealed class EventRun
	{
		public int Event;
		public bool InDuration;
		public bool Done;
		public float Time;
		public float End;
	}

	sealed class EffectInstance
	{
		public Halo3EffectDefinition Definition;

		// Null for an effect the runtime started at a fixed place (a particle's collision or death effect).
		public GameObject Root;
		public GameObject Controls;
		public Place Fixed;

		// Each location's places: every child of the root named after its marker (Halo makes things at each instance), else the root.
		public List<Place>[] Locations;
		public int LocationsFrame = -1;

		public bool Looping;
		public bool Dormant;
		public bool Done;

		public readonly List<EventRun> Runs = new();

		public float A = 1, B = 1;
		public Carrier Carrier;

		// A velocity it was given (world units per second), added to its carrier's.
		public Vector3 Velocity;

		public Vector3 TotalVelocity => (Carrier?.Velocity ?? Vector3.Zero) + Velocity;

		// Its particle systems by event and system index, kept across loops.
		public readonly Dictionary<(int Event, int System), SystemInstance> Systems = new();

		// The sounds its parts are playing, by event, sound and place, so a looping effect doesn't stack copies.
		public readonly Dictionary<(int Event, int Sound, int Place), SoundHandle> Sounds = new();
	}

	readonly List<EffectInstance> _effects = new();
	readonly Dictionary<GameObject, EffectInstance> _effectsByRoot = new();

	// Past this nothing more is made until something ends.
	const int MaxEffects = 512;

	// Most event phases one run goes through in a tick.
	const int MaxPhaseChanges = 8;

	EffectInstance StartEffect( Halo3EffectDefinition definition, GameObject root, Place? place, Vector3 velocity = default )
	{
		if ( _effects.Count >= MaxEffects )
			return null;

		var effect = new EffectInstance
		{
			Definition = definition,
			Root = root,
			Controls = root is null ? null : FindControls( root ),
			Fixed = place ?? default,
			Looping = root is not null && root.Tags.Has( Halo3ParticleEffects.LoopTag, false ),
			Carrier = root is null ? null : new Carrier { Object = root },
			Velocity = velocity,
		};

		effect.Locations = new List<Place>[Math.Max( definition.Locations.Count, 1 )];
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

		Restart( effect, 0 );
		UpdateEffect( effect, 0 );
		return effect;
	}

	// From an event: in parallel every event from it at once, else that one.
	void Restart( EffectInstance effect, int first )
	{
		effect.Runs.Clear();
		var events = effect.Definition.Events;
		if ( effect.Definition.Parallel )
		{
			for ( var i = first; i < events.Length; i++ )
			{
				if ( Rand() >= events[i].SkipFraction )
					effect.Runs.Add( NewRun( effect, i ) );
			}
		}
		else
		{
			effect.Runs.Add( NewRun( effect, first ) );
		}
	}

	EventRun NewRun( EffectInstance effect, int index )
	{
		var ev = effect.Definition.Events[index];
		return new EventRun { Event = index, End = Range( ev.Delay.X, ev.Delay.Y ) };
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
					if ( !effect.Looping || effect.Definition.DeletedWhenDeactivated )
					{
						effect.Done = true;
					}
					else if ( !effect.Dormant )
					{
						effect.Dormant = true;
						StopSounds( effect );
					}
				}
				else if ( effect.Dormant )
				{
					effect.Dormant = false;
					Restart( effect, 0 );
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
		foreach ( var (key, system) in effect.Systems )
		{
			system.Effect = null;

			// Events and systems flagged so: their particles die with the effect.
			if ( (effect.Definition.Events[key.Event].Flags & 0x2) != 0 || system.Definition.System.DieWithEffect )
				system.KillAll = true;
		}

		// A looping effect's sounds go with it; a one-shot effect's play out.
		if ( effect.Looping )
			StopSounds( effect );

		if ( effect.Root is { } root )
		{
			if ( _effectsByRoot.TryGetValue( root, out var known ) && known == effect )
				_effectsByRoot.Remove( root );
			if ( root.IsValid() && root.Active )
				_finished.Add( root );
		}
	}

	void UpdateEffect( EffectInstance effect, float dt )
	{
		var events = effect.Definition.Events;
		var parallel = effect.Definition.Parallel;
		for ( var r = 0; r < effect.Runs.Count; r++ )
		{
			var run = effect.Runs[r];
			var left = dt;
			for ( var change = 0; change < MaxPhaseChanges && !run.Done; change++ )
			{
				var remaining = run.End - run.Time;
				var completed = remaining <= left + 1e-6f;
				var step = completed ? MathF.Max( remaining, 0 ) : left;

				if ( run.InDuration )
					Emit( effect, run, run.Time, step );

				run.Time += step;
				left = MathF.Max( left - step, 0 );
				if ( !completed )
					break;

				if ( !run.InDuration )
				{
					StartDuration( effect, run );
					continue;
				}

				// The duration is over: in parallel this event is done; else the next one (past the skip fraction's dice), looping from the loop start event.
				if ( parallel || !NextEvent( effect, run ) )
					run.Done = true;
			}
		}

		if ( effect.Runs.All( r => r.Done ) )
		{
			var loopStart = Math.Clamp( effect.Definition.LoopStartEvent, 0, events.Length - 1 );
			if ( effect.Looping )
				Restart( effect, loopStart );
			else
				effect.Done = true;
		}
	}

	void StartDuration( EffectInstance effect, EventRun run )
	{
		var ev = effect.Definition.Events[run.Event];
		run.InDuration = true;
		run.Time = 0;

		// A duration of 0 is one tick.
		run.End = MathF.Max( Range( ev.Duration.X, ev.Duration.Y ), Tick );
		PlaySounds( effect, run.Event, ev );
		StartScreenEffects( effect, ev );

		// Each run of the event makes its systems' starting counts again, with new system randoms (a spark arc's burst every few seconds).
		for ( var s = 0; s < ev.Systems.Length; s++ )
		{
			if ( effect.Systems.TryGetValue( (run.Event, s), out var system ) )
			{
				foreach ( var location in system.Locations )
					Array.Clear( location.Started );
				(system.Random, system.Correlation1, system.Correlation2) = (Rand(), Rand(), Rand());
			}
		}
	}

	bool NextEvent( EffectInstance effect, EventRun run )
	{
		var events = effect.Definition.Events;
		for ( var next = run.Event + 1; next < events.Length; next++ )
		{
			if ( Rand() >= events[next].SkipFraction )
			{
				var ev = events[next];
				run.Event = next;
				run.InDuration = false;
				run.Time = 0;
				run.End = Range( ev.Delay.X, ev.Delay.Y );
				return true;
			}
		}

		return false;
	}

	// A step of an event's duration: each of its particle systems emits at each of its location's places.
	void Emit( EffectInstance effect, EventRun run, float time, float step )
	{
		if ( effect.Root is not null && _frame != effect.LocationsFrame )
		{
			effect.LocationsFrame = _frame;
			FindLocations( effect );
		}

		var ev = effect.Definition.Events[run.Event];
		if ( ev.LightVolumes.Length > 0 )
			ShowLightVolumes( effect, run, run.End > 0 ? Math.Clamp( (time + step) / run.End, 0, 1 ) : 1 );

		for ( var s = 0; s < ev.Systems.Length; s++ )
		{
			var definition = ev.Systems[s];
			if ( !effect.Systems.TryGetValue( (run.Event, s), out var system ) || !system.Alive )
			{
				if ( _systems.Count >= MaxSystems )
					continue;

				system = NewSystem( definition, effect );
				effect.Systems[(run.Event, s)] = system;
			}

			// The system's age runs 0-1 over the event's duration.
			system.Age = run.End > 0 ? Math.Clamp( (time + step) / run.End, 0, 1 ) : 1;
			var location = definition.System.Location;
			EmitSystem( system, location >= 0 && location < effect.Locations.Length ? effect.Locations[location] : effect.Locations[0], step );
		}
	}

	// Looks each location's places up again: a game may add a marker's other instances after the effect starts.
	static void FindLocations( EffectInstance effect )
	{
		var names = effect.Definition.Locations;
		for ( var i = 0; i < effect.Locations.Length; i++ )
		{
			var name = i < names.Count ? names[i] : "";
			var list = effect.Locations[i];
			list.Clear();
			if ( effect.Root is null )
			{
				list.Add( FixedPlace( effect.Fixed, name ) );
				continue;
			}

			if ( !string.IsNullOrEmpty( name ) )
			{
				foreach ( var child in effect.Root.Children )
				{
					if ( child.Name == name && list.Count < MaxLocationPlaces )
						list.Add( new Place { Object = child, Toward = child.Tags.Has( Halo3Effects.WorldUpTag ) ? 1 : child.Tags.Has( Halo3Effects.WorldDownTag ) ? 2 : 0 } );
				}
			}

			if ( list.Count == 0 )
				list.Add( new Place { Object = effect.Root } );
		}
	}

	// Most instances of one location.
	const int MaxLocationPlaces = 16;

	// A location of an effect made at a fixed place (forward the surface normal or the way a particle went): "gravity" points down and "up" up.
	static Place FixedPlace( Place place, string name ) => name switch
	{
		"gravity" => place with { Rotation = Rotation.LookAt( Vector3.Down, MathF.Abs( place.Rotation.Forward.z ) > 0.99f ? Vector3.Forward : place.Rotation.Forward ) },
		"up" => place with { Rotation = Rotation.LookAt( Vector3.Up, MathF.Abs( place.Rotation.Forward.z ) > 0.99f ? Vector3.Forward : place.Rotation.Forward ) },
		_ => place,
	};

	// Stops the sounds an effect's parts are playing (it went dormant or ended).
	static void StopSounds( EffectInstance effect )
	{
		foreach ( var handle in effect.Sounds.Values )
		{
			if ( handle is not null && handle.IsValid && !handle.IsStopped )
				handle.Stop();
		}

		effect.Sounds.Clear();
	}

	// An event's area screen effects, one instance at each place of their locations (following the place's object).
	void StartScreenEffects( EffectInstance effect, Halo3EventDefinition ev )
	{
		if ( ev.ScreenEffects is not { Length: > 0 } || Halo3ScreenEffects.For( Scene ) is not { } screen )
			return;

		foreach ( var part in ev.ScreenEffects )
		{
			var location = part.Location >= 0 && part.Location < effect.Locations.Length ? part.Location : 0;
			foreach ( var place in effect.Locations[location] )
			{
				if ( !place.Valid )
					continue;
				var position = place.Frame.Position;
				var offset = place.Object is { } follow ? follow.WorldTransform.PointToLocal( position * Units ) / Units : default;
				screen.Add( part.Tag, position, place.Object, offset );
			}
		}
	}

	readonly Dictionary<string, SoundEvent> _soundEvents = new();

	// An event's sound parts, at each place of their locations (a looping effect's copy still playing isn't started again).
	void PlaySounds( EffectInstance effect, int eventIndex, Halo3EventDefinition ev )
	{
		for ( var s = 0; s < ev.Sounds.Length; s++ )
		{
			var sound = ev.Sounds[s];
			var location = sound.Location >= 0 && sound.Location < effect.Locations.Length ? sound.Location : 0;

			if ( !_soundEvents.TryGetValue( sound.Event, out var soundEvent ) )
				_soundEvents[sound.Event] = soundEvent = GameResource.Load<SoundEvent>( sound.Event );
			if ( soundEvent is null )
				continue;

			var places = effect.Locations[location];
			for ( var p = 0; p < places.Count; p++ )
			{
				if ( !places[p].Valid )
					continue;

				var key = (eventIndex, s, p);
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
