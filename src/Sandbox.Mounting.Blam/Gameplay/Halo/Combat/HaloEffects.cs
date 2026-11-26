using System;
using Halo.Data;

namespace Halo.Combat;

// Plays Halo effect tags, through the mount's effect prefab when there is one.
// Without a prefab, sounds, decals and a short light flash are played here.
// Damage parts are applied by the host in HaloCombat, never here.
public static class HaloEffects {
	[ConVar( "halo_effect_prefabs", Help = "Use the mount's effect prefabs (particles, lights, sounds, decals).", Saved = true )]
	public static bool PrefabsEnabled { get; set; } = true;

	[ConVar( "halo_decals", Help = "Bullet holes and scorch marks from Halo's decal tags.", Saved = true )]
	public static bool DecalsEnabled { get; set; } = true;

	static readonly Dictionary<string, PrefabFile> Prefabs = new();

	// A mount prefab by tag path without extension, or null. Cached, including misses.
	// Joining a game resets the resource library and destroys cached prefabs, which then break on every clone.
	// So a cached prefab is reused only while still valid, and reloaded otherwise.
	public static PrefabFile Prefab( string tagPathWithoutExtension ) {
		if ( string.IsNullOrEmpty( tagPathWithoutExtension ) ) return null;
		if ( Prefabs.TryGetValue( tagPathWithoutExtension, out var cached ) && (cached is null || cached.IsValid) ) return cached;

		var path = $"mount://haloce/{tagPathWithoutExtension.Replace( '\\', '/' )}.prefab";
		PrefabFile prefab = null;
		try {
			var exists = Sandbox.Mounting.Directory.Get( "haloce" )?.GetByPath( path ) is not null;
			if ( exists ) prefab = PrefabFile.Load( path );
		}
		catch ( Exception e ) {
			Log.Warning( $"Couldn't load {path}: {e.Message}" );
		}

		Prefabs[tagPathWithoutExtension] = prefab;
		return prefab;
	}

	public static string WithoutExtension( string tagPath ) {
		if ( string.IsNullOrEmpty( tagPath ) ) return null;
		var path = tagPath.Replace( '\\', '/' );
		var dot = path.LastIndexOf( '.' );
		return dot > path.LastIndexOf( '/' ) ? path[..dot] : path;
	}

	// +X is Halo's forward: the surface normal for impacts, the firing direction for muzzle flashes. It cleans itself up.
	// Sounds play from source under Halo's copy limits; source is null for an effect not on an object.
	public static GameObject SpawnPrefab( PrefabFile prefab, Vector3 position, Vector3 forward, GameObject parent = null, object source = null ) =>
		Spawn( prefab, position, forward, parent, source, null, default );

	// With effectPath set, decals are placed as Halo does instead of where the prefab has them.
	// velocity is an impact's projectile velocity. own marks the local player's unit as owner.
	static GameObject Spawn( PrefabFile prefab, Vector3 position, Vector3 forward, GameObject parent, object source, string effectPath, Vector3 velocity, bool own = false ) {
		if ( prefab is null ) return null;
		using var perf = HaloPerf.Measure( "Effects.Spawn" );
		if ( forward.LengthSquared < 0.0001f ) forward = Vector3.Up;

		// Cloned disabled so no sound starts before Halo's limits are applied.
		var go = GameObject.Clone( prefab, new Transform( position, Rotation.LookAt( forward.Normal ) ), null, false );
		if ( !go.IsValid() ) return null;

		go.MakeLocal();
		HaloSounds.Dry( go );
		HaloSounds.LimitEffectSounds( go, source, own );
		if ( effectPath is not null ) RemoveDecals( go );
		go.Enabled = true;
		if ( parent.IsValid() ) {
			// Parented during a tick, it would otherwise ease in from its world position read as local.
			go.SetParent( parent, true );
			go.Transform.ClearInterpolation();
		}
		if ( effectPath is not null ) PlaceDecals( effectPath, position, forward.Normal, velocity );
		return go;
	}

	// The prefab's decals project from wherever the effect is, which isn't how Halo places them.
	// PlaceDecals puts them where Halo would instead.
	static void RemoveDecals( GameObject effect ) {
		var decals = effect.GetComponentsInChildren<Decal>( true )
			.Select( x => x.GameObject )
			.Where( x => x.Parent is null || x.Parent.Components.Get<Decal>() is null )
			.Distinct()
			.ToList();

		foreach ( var decal in decals ) {
			decal.Enabled = false;
			decal.Destroy();
		}
	}

	// The child of an effect root through which the mount's particle runtime reads Halo's scales A and B.
	const string ControlsName = "halo controls";

	// Sets a spawned effect's scales A and B, which the mount's runtime reads each tick.
	public static void SetScales( GameObject effect, float a, float b = 1 ) {
		if ( !effect.IsValid() ) return;
		foreach ( var child in effect.Children ) {
			if ( child.Name != ControlsName ) continue;
			child.LocalPosition = new Vector3( Math.Clamp( a, 0, 1 ), Math.Clamp( b, 0, 1 ), 0 );
			return;
		}
	}

	// Draws the effect's first person only particles with the view model, hidden only by it.
	public static void DrawWithViewModel( GameObject effect ) {
		if ( effect.IsValid() ) effect.Tags.Add( "halo_view_model" );
	}

	// Plays an effect on an object, like a vehicle gun firing: it rides the object, its locations at the object's markers.
	// Sounds play on the object. False when the mount has no prefab.
	public static bool PlayOnObject( GameObject owner, string effectPath, Vector3 position, Vector3 forward, Func<string, GameObject> marker, object source ) {
		if ( SpawnEffect( effectPath, position, forward, parent: owner, source: source ) is not { } fx ) return false;
		SoundsOn( fx, owner );
		PlaceLocations( fx, marker );
		return true;
	}

	// Plays the mount's prefab for an effect, or a variant like "__fp". False when there is none.
	// own marks the local player's unit as owner, so its sounds play as MCC plays the local player's own.
	public static bool PlayPrefab( string effectPath, Vector3 position, Vector3 forward, string variant = null, GameObject parent = null, object source = null, bool own = false ) =>
		SpawnEffect( effectPath, position, forward, variant, parent, source, own ) is not null;

	// Null when the mount has no prefab for the effect.
	public static GameObject SpawnEffect( string effectPath, Vector3 position, Vector3 forward, string variant = null, GameObject parent = null, object source = null, bool own = false ) {
		if ( !PrefabsEnabled ) return null;
		var name = WithoutExtension( effectPath );
		var prefab = variant is not null ? Prefab( $"{name}{variant}" ) ?? Prefab( name ) : Prefab( name );
		return Spawn( prefab, position, forward, parent, source, effectPath, default, own );
	}

	// Halo plays effect sounds on the object itself, never at first person markers.
	// So a first person weapon's firing sound comes from the held weapon, and zooming never cuts it off.
	// The sounds leave the effect's hierarchy so hiding or destroying the view model can't stop them.
	public static void SoundsOn( GameObject effect, GameObject target ) {
		if ( !effect.IsValid() ) return;
		var sounds = effect.GetComponentsInChildren<BaseSoundComponent>( true ).Where( x => x.GameObject != effect ).ToList();
		if ( sounds.Count == 0 ) return;

		var holder = effect.Scene.CreateLocalObject( "effect sounds" );
		holder.WorldPosition = target.IsValid() ? target.WorldPosition : effect.WorldPosition;
		if ( target.IsValid() ) holder.WorldRotation = target.WorldRotation;
		if ( target.IsValid() ) holder.AddComponent<SoundAnchor>().Target = target;
		foreach ( var sound in sounds )
			sound.GameObject.SetParent( holder, true );

		// Lives for the effect's lifetime, then as long as its sounds still play.
		var lifetime = holder.AddComponent<TemporaryEffect>( false );
		// A looping effect's marker never expires, so its sounds' holder goes once they've played.
		var seconds = effect.GetComponent<TemporaryEffect>()?.DestroyAfterSeconds ?? 0.1f;
		lifetime.DestroyAfterSeconds = seconds < 3600 ? seconds : 0.1f;
		lifetime.WaitForChildEffects = true;
		lifetime.Enabled = true;
	}

	// velocity is an impact's projectile velocity, which aims the shot-relative locations.
	// source is the object the effect's sounds play on. own is as in PlayPrefab.
	public static void Play( string effectPath, Vector3 position, Vector3 normal = default, float volume = 1, Vector3 velocity = default, object source = null, bool own = false ) =>
		PlayObject( effectPath, position, normal, volume, velocity, source, own );

	// Returns the spawned prefab so it can follow what it's attached to. Null when played without one.
	public static GameObject PlayObject( string effectPath, Vector3 position, Vector3 normal = default, float volume = 1, Vector3 velocity = default, object source = null, bool own = false ) {
		// The prefab brings particles, lights and sounds; decals are placed as Halo does.
		if ( PrefabsEnabled && Spawn( Prefab( WithoutExtension( effectPath ) ), position, normal, null, source, effectPath, velocity, own ) is { } spawned ) {
			AimLocations( spawned, normal, velocity );
			return spawned;
		}

		var game = HaloDataStore.Game;
		var effect = game?.Effect( effectPath );
		if ( effect is null ) return null;

		var flash = false;
		foreach ( var part in effect.Parts ) {
			if ( part.IsSound ) {
				var delay = part.Delay is { Length: > 0 } d ? d[0] : 0;
				PlaySound( part.Tag, position, volume, delay, source, own );
			}
			else if ( part.IsDamage ) {
				flash |= game.DamageEffect( part.Tag )?.CutoffRadius > 0.5f;
			}
			else if ( part.Tag?.EndsWith( ".ligh" ) ?? false ) {
				flash = true;
			}
		}

		PlaceDecals( effectPath, position, normal, velocity );

		if ( flash )
			Flash( position + normal * 8, effect.MaximumDamageRadius > 0 ? effect.MaximumDamageRadius : 2, new Color( 1f, 0.75f, 0.45f ) );
		return null;
	}

	// Aims the prefab's named location children, whose +X is the direction. The root stays along the surface normal.
	static void AimLocations( GameObject effect, Vector3 normal, Vector3 velocity ) {
		foreach ( var location in effect.Children ) {
			if ( !location.Tags.Has( "halo_effect_location" ) ) continue;

			if ( ShotDirection( location.Name, normal, velocity ) is { } d && d.LengthSquared > 0.01f ) {
				location.WorldRotation = Rotation.LookAt( d );
				// Impacts play during fixed update, so the first burst must leave the way it's aimed.
				location.Transform.ClearInterpolation();
			}
		}
	}

	// Moves each location onto the object's marker of the same name, since the prefab has them all at the root.
	// A marker repeated on the model, like the plasma rifle's "vent", "vent 1"..., gets a copy of the location at each.
	// Call after SoundsOn so sounds aren't copied with the location. Placed once; the locations ride with the root.
	public static void PlaceLocations( GameObject effect, Func<string, GameObject> marker ) {
		if ( !effect.IsValid() || marker is null ) return;

		foreach ( var location in effect.Children.ToList() ) {
			if ( !location.Tags.Has( "halo_effect_location" ) ) continue;
			if ( marker( location.Name ) is not { } m || !m.IsValid() ) continue;

			for ( var instance = 1; instance < 32 && marker( $"{location.Name} {instance}" ) is { } more && more.IsValid(); instance++ ) {
				var copy = location.Clone( new Transform(), location.Parent, false, location.Name );
				if ( !copy.IsValid() ) break;
				copy.WorldPosition = more.WorldPosition;
				copy.WorldRotation = more.WorldRotation;
				copy.Enabled = true;
			}

			location.WorldPosition = m.WorldPosition;
			location.WorldRotation = m.WorldRotation;
			// Shots fire during the fixed update, so the first burst must leave from the marker.
			location.Transform.ClearInterpolation();
		}
	}

	// "gravity" points down. With a shot, "incident" points back along it and "negative incident" along it.
	// "reflection" is the shot mirrored off the surface. Other names return null and sit at the "normal" marker.
	static Vector3? ShotDirection( string name, Vector3 normal, Vector3 velocity ) => name switch {
		"gravity" => Vector3.Down,
		"incident" when velocity.LengthSquared > 1 => -velocity.Normal,
		"negative incident" when velocity.LengthSquared > 1 => velocity.Normal,
		"reflection" when velocity.LengthSquared > 1 && normal.LengthSquared > 0.01f
			=> (velocity - 2 * Vector3.Dot( velocity, normal.Normal ) * normal.Normal).Normal,
		_ => null,
	};

	public static void PlaySound( string soundTag, Vector3 position, float volume = 1, float delay = 0, object source = null, bool own = false ) {
		if ( delay > 0.01f ) {
			HaloTickSystem.After( Game.ActiveScene, delay, () => HaloSounds.Play( soundTag, position, volume, source, own ) );
			return;
		}

		HaloSounds.Play( soundTag, position, volume, source, own );
	}

	// Unpositioned, for the local player only, like pickups and HUD beeps.
	public static void PlayLocal( string soundTag, float volume = 1 ) => HaloSounds.PlayLocal( soundTag, volume );

	static readonly Dictionary<string, PrefabFile> Decals = new();
	static readonly Dictionary<string, EffectDefinitionTag> Definitions = new();

	// One pass of a looping effect's events, each a random delay plus a random duration, at least a tick.
	// Weapon overheat effects are one 0.05 s event, so they emit 20 times a second until stopped.
	public static float LoopPeriod( string effectPath ) {
		var period = 0f;
		foreach ( var e in Definition( effectPath )?.Events ?? new() ) {
			if ( e is null || (e.SkipFraction > 0 && Game.Random.Float() < e.SkipFraction) ) continue;
			period += RandomInBounds( e.DelayBounds ) + RandomInBounds( e.DurationBounds );
		}
		return MathF.Max( period, HaloUnits.TickInterval );
	}

	static float RandomInBounds( float[] bounds ) => bounds switch {
		{ Length: >= 2 } => Game.Random.Float( bounds[0], MathF.Max( bounds[0], bounds[1] ) ),
		{ Length: 1 } => bounds[0],
		_ => 0,
	};

	// The effect tag's own export, which has the part details game.json leaves out. Null if the mount lacks it.
	static EffectDefinitionTag Definition( string effectPath ) {
		if ( string.IsNullOrEmpty( effectPath ) ) return null;
		var path = effectPath.Replace( '\\', '/' );
		if ( Definitions.TryGetValue( path, out var cached ) ) return cached;

		var definition = HaloDataStore.Tag<EffectDefinitionTag>( path );
		if ( definition is not null || HaloDataStore.IsAvailable ) Definitions[path] = definition;
		return definition;
	}

	// Halo's decal placement: each event's decal parts fire after its delay, along the part's location direction.
	// Each part gets a random velocity, and that segment in world units must hit the level for the decal to appear.
	// So a bullet hole at "negative incident" lands where the shot hit, and a splat at "incident" never shows.
	// forward is the effect's forward; velocity is an impact's projectile velocity.
	static void PlaceDecals( string effectPath, Vector3 origin, Vector3 forward, Vector3 velocity ) {
		if ( !DecalsEnabled ) return;
		var definition = Definition( effectPath );
		if ( definition?.Events is not { Count: > 0 } events ) return;

		var time = 0f;
		for ( var e = 0; e < events.Count; e++ ) {
			var ev = events[e];

			// Each event after the first is skipped by its skip fraction.
			if ( e > 0 && Game.Random.Float( 0, 1 ) < ev.SkipFraction ) continue;

			time += RandomRange( ev.DelayBounds );
			foreach ( var part in ev.Parts ) {
				if ( !part.IsDecal || !PartAllowed( part ) ) continue;
				if ( part.Location is not { } location || location < 0 || location >= definition.Locations.Count ) continue;

				var direction = part.HasFlag( "face down regardless of location decals" )
					? Vector3.Down
					: ShotDirection( definition.Locations[location].MarkerName, forward, velocity ) ?? forward;

				var decalVelocity = RandomTranslationalVelocity( direction, part );
				var radiusModifier = RandomRange( part.RadiusModifierBounds );

				if ( time > 0.001f ) HaloTickSystem.After( Game.ActiveScene, time, () => DecalNew( part.Type, origin, decalVelocity, radiusModifier ) );
				else DecalNew( part.Type, origin, decalVelocity, radiusModifier );
			}
			time += RandomRange( ev.DurationBounds );
		}
	}

	// Effects here are never nonviolent, which in Halo only corpses' are.
	// There's no water, so "water only" parts never play, and "space only" never does in Halo either.
	static bool PartAllowed( EffectPartDefinitionTag part ) =>
		part.ViolenceMode != "nonviolent mode only" && part.CreateIn is not ("water only" or "space only");

	// A speed within the part's bounds, along forward turned by a random fraction of the cone about a random axis.
	// In world units. Effect scales A and B are taken as 1, since no stock decal part uses them.
	static Vector3 RandomTranslationalVelocity( Vector3 forward, EffectPartDefinitionTag part ) {
		var bounds = part.VelocityBounds;
		var lower = bounds is { Length: > 0 } ? bounds[0] : 0;
		var upper = bounds is { Length: > 1 } ? bounds[1] : lower;
		var magnitude = Game.Random.Float( 0, upper - lower ) + lower;

		var angle = Game.Random.Float( 0, 1 ) * part.VelocityConeAngle.DegreeToRadian();
		var direction = forward;
		if ( angle != 0 )
			direction = RotateAboutAxis( direction, RandomDirection(), MathF.Sin( angle ), MathF.Cos( angle ) );

		return direction * magnitude;
	}

	// Written exactly as Halo's math does it.
	static Vector3 RotateAboutAxis( Vector3 v, Vector3 n, float sine, float cosine ) {
		var oneMinusCosineTimesVDotN = Vector3.Dot( n, v ) * (1 - cosine);
		var vCrossN = new Vector3( n.z * v.y - v.z * n.y, n.x * v.z - v.x * n.z, n.y * v.x - v.y * n.x );
		return cosine * v + oneMinusCosineTimesVDotN * n - sine * vCrossN;
	}

	// Halo picks a vertex of a 16 segment geosphere; a uniform direction is the same without the table.
	static Vector3 RandomDirection() {
		var z = Game.Random.Float( -1, 1 );
		var angle = Game.Random.Float( 0, MathF.PI * 2 );
		var r = MathF.Sqrt( 1 - z * z );
		return new Vector3( r * MathF.Cos( angle ), r * MathF.Sin( angle ), z );
	}

	// lower + random(0, upper - lower).
	static float RandomRange( float[] bounds ) {
		var lower = bounds is { Length: > 0 } ? bounds[0] : 0;
		var upper = bounds is { Length: > 1 } ? bounds[1] : lower;
		return upper == lower ? lower : Game.Random.Float( 0, upper - lower ) + lower;
	}

	// Casts velocity, in world units, from origin against the level's front faces and decals the hit flat on its plane.
	// A miss, including zero velocity, makes no decal. radiusModifier 0 means 1.
	static void DecalNew( string decalTag, Vector3 origin, Vector3 velocity, float radiusModifier ) {
		if ( !DecalsEnabled || string.IsNullOrEmpty( decalTag ) ) return;
		var prefab = DecalPrefab( decalTag );
		if ( prefab is null ) return;
		if ( !DecalSurface( origin, velocity * HaloUnits.WorldScale, out var point, out var normal ) ) return;

		// Set up before enabling, since the decal is built from its transform then.
		var go = GameObject.Clone( prefab, new Transform( point, Rotation.LookAt( -normal ) ), null, false );
		if ( !go.IsValid() ) return;

		go.MakeLocal();
		if ( radiusModifier != 0 && radiusModifier != 1 )
			go.LocalScale = radiusModifier;

		// A "no random rotation" decal runs u along the velocity across the surface when heading into it.
		// Otherwise it gets a random spin. The map runs u along -Y and v along -Z.
		if ( go.GetComponent<Decal>( true ) is { } decal && decal.Rotation.IsNearlyZero() ) {
			var along = velocity - normal * Vector3.Dot( velocity, normal );
			go.WorldRotation = Vector3.Dot( normal, velocity ) < -0.0001f && along.LengthSquared > 1e-8f
				? Rotation.LookAt( -normal, Vector3.Cross( normal, along.Normal ) )
				: Rotation.LookAt( -normal ) * Rotation.FromRoll( Game.Random.Float( 0, 360 ) );
		}

		go.Enabled = true;
	}

	// Halo decals only test the structure BSP, so every placed object kind is skipped.
	// s&box traces only honour the first 8 WithoutTags.
	static readonly string[] DecalIgnoreTags =
	[
		"scenery", "machine", "control", "light_fixture", "vehicle", "biped", "weapon", "equipment",
	];

	// Starts the ray a little behind the origin, which can sit just past the surface hit.
	const float DecalTraceBackoff = 0.25f;

	// The first front-facing level surface the segment hits.
	static bool DecalSurface( Vector3 origin, Vector3 segment, out Vector3 point, out Vector3 normal ) {
		point = default;
		normal = default;

		var scene = Game.ActiveScene;
		var length = segment.Length;
		if ( scene is null || length < 0.001f ) return false;

		var direction = segment / length;
		var tr = scene.Trace.Ray( origin - direction * DecalTraceBackoff, origin + segment )
			.WithTag( "halo_map" )
			.WithoutTags( DecalIgnoreTags )
			.Run();

		if ( !tr.Hit || tr.StartedSolid || Vector3.Dot( tr.Normal, direction ) >= 0 ) return false;

		point = tr.HitPosition;
		normal = tr.Normal;
		return true;
	}

	static PrefabFile DecalPrefab( string decalTag ) {
		// Reloaded once destroyed, like Prefab's cache.
		if ( !Decals.TryGetValue( decalTag, out var prefab ) || (prefab is not null && !prefab.IsValid) ) {
			prefab = Prefab( WithoutExtension( decalTag ) );
			Decals[decalTag] = prefab;
		}
		return prefab;
	}

	// A brief point light, for muzzle flashes and explosions.
	public static void Flash( Vector3 position, float radiusWu, Color color, float duration = 0.12f ) {
		var scene = Game.ActiveScene;
		if ( scene is null ) return;

		var go = scene.CreateLocalObject( "flash" );
		go.WorldPosition = position;
		var light = go.AddComponent<PointLight>();
		light.Radius = HaloUnits.ToInches( MathF.Max( 1, radiusWu * 2 ) );
		light.LightColor = color * 4;
		light.Shadows = false;
		go.AddComponent<TimedDestroy>().Seconds = duration;
	}

	// Picks the firing effect for a trigger, given how many shots it's fired in a row.
	public static string FiringEffect( TriggerTag trigger, int shotCount ) => FiringEffects( trigger, shotCount )?.FiringEffect;

	// The damage effect a shot applies to its shooter, i.e. the camera shake.
	public static DamageEffectTag FiringDamage( TriggerTag trigger, int shotCount ) =>
		HaloDataStore.Game?.DamageEffect( FiringEffects( trigger, shotCount )?.FiringDamage );

	static FiringEffectTag FiringEffects( TriggerTag trigger, int shotCount ) {
		if ( trigger?.FiringEffects is not { Count: > 0 } effects ) return null;

		foreach ( var e in effects ) {
			if ( shotCount >= e.ShotCountLowerBound && (e.ShotCountUpperBound <= 0 || shotCount <= e.ShotCountUpperBound) )
				return e;
		}
		return effects[^1];
	}
}

// Halo's firing effect rotation, one per trigger. Each entry plays for a random shot count, then the next takes over.
// "random firing effects" starts from a random entry, and every entry plays once before any repeats.
// The plasma rifle's two one-shot entries alternate its upper and lower barrels.
public sealed class FiringEffectCycle {
	TriggerTag _trigger;
	int _index;
	int _remaining;
	int _used;

	public string Next( TriggerTag trigger ) => NextEntry( trigger )?.FiringEffect;

	public FiringEffectTag NextEntry( TriggerTag trigger ) {
		if ( trigger?.FiringEffects is not { Count: > 0 } effects ) return null;
		var count = Math.Min( effects.Count, 30 );

		// Reset on a new trigger, or a weapon with fewer firing effects would never find an unused one.
		if ( trigger != _trigger ) {
			_trigger = trigger;
			_index = 0;
			_remaining = 0;
			_used = 0;
		}

		if ( _remaining <= 0 ) {
			var start = Math.Clamp( _index, 0, count - 1 );
			var index = trigger.HasFlag( "random firing effects" ) ? Game.Random.Int( 0, count - 1 ) : start;
			do {
				if ( _used == (1 << count) - 1 ) _used = 0;
				do {
					index = index + 1 >= count ? 0 : index + 1;
				}
				while ( (_used & (1 << index)) != 0 );

				_index = index;
				_used |= 1 << index;
				var e = effects[index];
				_remaining = Game.Random.Int( e.ShotCountLowerBound, Math.Max( e.ShotCountLowerBound, e.ShotCountUpperBound ) );
			}
			while ( _remaining <= 0 && index != start );
		}

		_remaining--;
		return effects[Math.Clamp( _index, 0, count - 1 )];
	}
}

// Destroys its GameObject after a delay.
public sealed class TimedDestroy : Component {
	[Property] public float Seconds { get; set; } = 1;

	TimeSince _alive;

	protected override void OnStart() => _alive = 0;

	protected override void OnUpdate() {
		if ( _alive >= Seconds )
			GameObject.Destroy();
	}
}

// Keeps effect sounds on their object until it's deleted or hidden, then lets them play out where it was, as Halo does.
// They turn with the object too, so a gun's next shot plays where the last one is and takes its channel.
// Halo keeps following a weapon dropped on death as it falls; here the sound stays where it was held.
public sealed class SoundAnchor : Component {
	public GameObject Target { get; set; }

	protected override void OnUpdate() {
		if ( Target is null ) return;
		if ( Target.IsValid() && Target.Active ) {
			WorldPosition = Target.WorldPosition;
			WorldRotation = Target.WorldRotation;
		}
		else Target = null;
	}
}
