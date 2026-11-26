using System;
using Halo2.Data;

namespace Halo2.Combat;

// Plays Halo 2 effects through the mount's prefabs: particles, light volumes, lens flares and sounds (Halo2EffectSounds).
// Objects' effects go by role, "<object tag>__<role>.prefab", and the rest by effect tag, "<effect tag>.prefab".
// Damage parts are applied by the host in HaloCombat, never here.
public static class HaloEffects {
	static readonly Dictionary<string, PrefabFile> Prefabs = new();

	// A mount prefab by path without extension, or null. Cached, including misses once the mount is up.
	// Joining a game resets the resource library and destroys cached prefabs, which then break on every clone.
	// So a cached prefab is reused only while still valid, and reloaded otherwise.
	public static PrefabFile Prefab( string pathWithoutExtension ) {
		if ( string.IsNullOrEmpty( pathWithoutExtension ) ) return null;
		if ( Prefabs.TryGetValue( pathWithoutExtension, out var cached ) && (cached is null || cached.IsValid) ) return cached;

		var path = $"mount://{HaloMountCheck.Ident}/{pathWithoutExtension.Replace( '\\', '/' )}.prefab";
		PrefabFile prefab = null;
		try {
			var exists = HaloMountCheck.Mount?.GetByPath( path ) is not null;
			if ( exists ) prefab = PrefabFile.Load( path );
		}
		catch ( Exception e ) {
			Log.Warning( $"Couldn't load {path}: {e.Message}" );
			return null;
		}

		if ( prefab is not null || HaloMountCheck.MountState == 2 ) Prefabs[pathWithoutExtension] = prefab;
		return prefab;
	}

	public static string WithoutExtension( string tagPath ) {
		if ( string.IsNullOrEmpty( tagPath ) ) return null;
		var path = tagPath.Replace( '\\', '/' );
		var dot = path.LastIndexOf( '.' );
		return dot > path.LastIndexOf( '/' ) ? path[..dot] : path;
	}

	// The mount's alias for the effect an object plays in a role, such as "barrel0_fire" or "detonation_ground".
	public static string Role( string objectTag, string role ) =>
		string.IsNullOrEmpty( objectTag ) ? null : $"{WithoutExtension( objectTag )}__{role}";

	// Whether the mount has the effect: a role the object doesn't play has no prefab.
	public static bool Exists( string effect ) => Prefab( WithoutExtension( effect ) ) is not null;

	// +X is Halo's forward: the surface normal for impacts, the firing direction for muzzle flashes. It cleans itself up.
	// Sounds play from source under Halo's copy limits; source is null for an effect not on an object.
	// own marks the local player's unit as owner, so its sounds play as MCC plays the local player's own.
	public static GameObject SpawnPrefab( PrefabFile prefab, Vector3 position, Vector3 forward, GameObject parent = null, object source = null, bool own = false ) {
		if ( prefab is null ) return null;
		using var perf = HaloPerf.Measure( "Effects.Spawn" );
		if ( forward.LengthSquared < 0.0001f ) forward = Vector3.Up;

		// Cloned disabled so no sound starts before Halo's limits are applied.
		var go = GameObject.Clone( prefab, new Transform( position, Rotation.LookAt( forward.Normal ) ), null, false );
		if ( !go.IsValid() ) return null;

		go.NetworkMode = NetworkMode.Never;
		go.Tags.Add( LocalObjects.Tag );
		HaloSounds.Dry( go );
		HaloSounds.LimitLoopingSounds( go, source, own );
		Halo2EffectSounds.Take( go, source, own );
		go.Enabled = true;
		if ( parent.IsValid() ) {
			// Parented during a tick, it would otherwise ease in from its world position read as local.
			go.SetParent( parent, true );
			go.Transform.ClearInterpolation();
		}
		return go;
	}

	// An effect or role's prefab, or its first person view: variant "_fp" for a role, "__fp" for an effect tag.
	// The first person view is the effect as the local player sees their own; Halo plays it only for them.
	// Its light parts become lights at their locations. Null when the mount has no prefab for it.
	public static GameObject SpawnEffect( string effect, Vector3 position, Vector3 forward, string variant = null, GameObject parent = null, object source = null, bool own = false ) {
		var name = WithoutExtension( effect );
		var firstPerson = variant is not null && Prefab( $"{name}{variant}" ) is not null;
		var go = SpawnPrefab( firstPerson ? Prefab( $"{name}{variant}" ) : Prefab( name ), position, forward, parent, source, own );
		if ( go is not null ) AddLights( go, EffectTagOf( name ), firstPerson );
		return go;
	}

	// Only an effect or role's sounds, for an effect on something hidden, like the first person weapon while zoomed.
	public static void PlaySounds( string effect, Vector3 position, object source = null, bool own = false ) {
		if ( HaloDataStore.Game?.Effect( EffectTagOf( WithoutExtension( effect ) ) ) is not { } tag ) return;
		foreach ( var part in tag.Parts )
			if ( part.Tag?.EndsWith( ".snd!" ) ?? false ) HaloSounds.Play( part.Tag, position, source: source, own: own );
	}

	// The effect tag's light parts, each at its location or the effect's origin.
	static void AddLights( GameObject effect, string effectTag, bool firstPerson ) {
		if ( HaloDataStore.Game?.Effect( effectTag ) is not { } tag ) return;
		foreach ( var part in tag.Parts.Where( x => x.Tag?.EndsWith( ".ligh" ) ?? false ) ) {
			var location = tag.Locations.ElementAtOrDefault( part.Location );
			var at = effect.Children.FirstOrDefault( x => x.Name == location && x.Tags.Has( "halo_effect_location" ) ) ?? effect;
			Halo2Light.Effect( at, part.Tag, firstPerson );
		}
	}

	// The responses' names in the mount's impact roles, longest first so "bounce_dud" isn't read as "bounce".
	static readonly string[] ImpactResponses = ["impact", "fizzle", "overpenetrate", "attach", "bounce_dud", "bounce"];
	static readonly int[] ImpactEntries = [0, 1, 2, 3, 5, 4];

	// The effect tag behind a mount role: a projectile's detonation or material impact, or a weapon barrel's firing.
	// Anything else is an effect tag already, or a role without lights.
	static string EffectTagOf( string effect ) {
		var mark = effect?.LastIndexOf( "__" ) ?? -1;
		if ( mark < 0 ) return $"{effect}.effe";
		var stem = effect[..mark];
		var role = effect[(mark + 2)..];
		var game = HaloDataStore.Game;

		if ( game?.Projectile( $"{stem}.proj" ) is { } projectile ) {
			switch ( role ) {
				case "detonation_airborne": return projectile.DetonationEffectAirborne;
				case "detonation_ground": return projectile.DetonationEffectGround;
				case "super_detonation": return projectile.SuperDetonation;
			}
			var secondary = role.EndsWith( "_secondary" );
			if ( secondary ) role = role[..^"_secondary".Length];
			for ( var i = 0; i < ImpactResponses.Length; i++ ) {
				if ( !role.StartsWith( $"{ImpactResponses[i]}_" ) ) continue;
				var material = role[(ImpactResponses[i].Length + 1)..];
				var part = game.MaterialEffectsEntry( projectile.MaterialEffects, MaterialEffectsTag.ProjectileImpact + ImpactEntries[i] )?.Effects?.FirstOrDefault( x => x.MaterialName == material );
				return secondary ? part?.SecondaryTag : part?.Tag;
			}
			return null;
		}

		// "barrel<n>_fire" plays the barrel's first firing effect entry, "barrel<n>_fire_<i>" a later one.
		if ( game?.Weapon( $"{stem}.weap" ) is { } weapon && role.StartsWith( "barrel" ) ) {
			var parts = role["barrel".Length..].Split( '_' );
			if ( parts.Length < 2 || parts[1] != "fire" || !int.TryParse( parts[0], out var barrel ) ) return null;
			var entry = parts.Length > 2 && int.TryParse( parts[2], out var e ) ? e : 0;
			return weapon.Barrel( barrel )?.FiringEffects?.ElementAtOrDefault( entry )?.FiringEffect;
		}
		return null;
	}

	// Halo plays effect sounds on the object itself, never at first person markers.
	// So a first person weapon's firing sound comes from the held weapon, and zooming never cuts it off.
	public static void SoundsOn( GameObject effect, GameObject target ) => effect.GetComponent<Halo2EffectSounds>().Anchor = target;

	// An effect in the world, its locations aimed by the surface normal and an impact's projectile velocity.
	// source is the object the effect's sounds play on. own is as in SpawnPrefab.
	// Returns the spawned prefab so it can follow what it's attached to, or null if the mount has none.
	public static GameObject Play( string effect, Vector3 position, Vector3 normal = default, Vector3 velocity = default, string variant = null, object source = null, bool own = false ) {
		var spawned = SpawnEffect( effect, position, normal, variant, null, source, own );
		if ( spawned is not null ) AimLocations( spawned, normal, velocity );
		return spawned;
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
	// A marker repeated on the model, like the plasma rifle's "vent", "vent_1"..., gets a copy of the location at each.
	// Placed once; the locations ride with the root.
	public static void PlaceLocations( GameObject effect, Func<string, GameObject> marker ) {
		if ( !effect.IsValid() || marker is null ) return;

		foreach ( var location in effect.Children.ToList() ) {
			if ( !location.Tags.Has( "halo_effect_location" ) ) continue;
			if ( marker( location.Name ) is not { } m || !m.IsValid() ) continue;

			for ( var instance = 1; instance < 32 && marker( $"{location.Name}_{instance}" ) is { } more && more.IsValid(); instance++ ) {
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

	// Moves an effect's locations onto their markers again, each instance onto its own, as PlaceLocations laid them out.
	public static void FollowLocations( GameObject effect, Func<string, GameObject> marker ) {
		if ( !effect.IsValid() || marker is null ) return;
		foreach ( var group in effect.Children.Where( x => x.Tags.Has( "halo_effect_location" ) ).GroupBy( x => x.Name ) ) {
			var instance = 0;
			foreach ( var location in group ) {
				var m = marker( instance == 0 ? group.Key : $"{group.Key}_{instance}" );
				instance++;
				if ( m is null || !m.IsValid() ) continue;
				location.WorldPosition = m.WorldPosition;
				location.WorldRotation = m.WorldRotation;
			}
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

	// The effect tag's own export, with its events' timing. Null if the mount lacks it.
	static EffectDefinitionTag Definition( string effectPath ) {
		if ( string.IsNullOrEmpty( effectPath ) ) return null;
		var path = effectPath.Replace( '\\', '/' );
		if ( Definitions.TryGetValue( path, out var cached ) ) return cached;

		var definition = HaloDataStore.Tag<EffectDefinitionTag>( path );
		if ( definition is not null || HaloDataStore.IsAvailable ) Definitions[path] = definition;
		return definition;
	}

	// The damage effect a shot applies to its shooter, i.e. the camera shake.
	public static DamageEffectTag FiringDamage( BarrelTag barrel, int shotCount ) =>
		HaloDataStore.Game?.DamageEffect( FiringEffects( barrel, shotCount )?.FiringDamage );

	static FiringEffectTag FiringEffects( BarrelTag barrel, int shotCount ) {
		if ( barrel?.FiringEffects is not { Count: > 0 } effects ) return null;

		foreach ( var e in effects ) {
			if ( shotCount >= e.ShotCountLowerBound && (e.ShotCountUpperBound <= 0 || shotCount <= e.ShotCountUpperBound) )
				return e;
		}
		return effects[^1];
	}
}

// The firing effect rotation, one per barrel. Each entry plays for a random shot count, then the next takes over,
// and every entry plays once before any repeats.
// One-shot effects riding an object that gets hidden, such as the first person weapon while zoomed.
// The mount plays an effect again when its root is turned off and on, so they're left in place before the object hides.
// Their locations ride the object's markers as it animates, as Halo moves an effect's locations with its object's markers.
public sealed class CarriedEffects {
	readonly List<(GameObject Effect, Func<string, GameObject> Marker)> _effects = new();

	// marker finds the object's markers for Follow, null for effects that stay as placed.
	public void Add( GameObject effect, Func<string, GameObject> marker = null ) {
		_effects.RemoveAll( x => !x.Effect.IsValid() );
		if ( effect.IsValid() ) _effects.Add( (effect, marker) );
	}

	// Puts each effect's locations on their markers where the object is now drawn.
	public void Follow() {
		foreach ( var (effect, marker) in _effects )
			HaloEffects.FollowLocations( effect, marker );
	}

	// Each plays out where it is, no longer following the object.
	public void Drop() {
		foreach ( var (effect, _) in _effects )
			if ( effect.IsValid() ) effect.SetParent( null, true );
		_effects.Clear();
	}
}

public sealed class FiringEffectCycle {
	BarrelTag _barrel;
	int _index;
	int _remaining;
	int _used;

	// The barrel's firing effect entry for the next shot, -1 if it has none.
	public int Next( BarrelTag barrel ) {
		if ( barrel?.FiringEffects is not { Count: > 0 } effects ) return -1;
		var count = Math.Min( effects.Count, 30 );

		// Reset on a new barrel, or a weapon with fewer firing effects would never find an unused one.
		if ( barrel != _barrel ) {
			_barrel = barrel;
			_index = 0;
			_remaining = 0;
			_used = 0;
		}

		if ( _remaining <= 0 ) {
			var start = Math.Clamp( _index, 0, count - 1 );
			var index = start;
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
		return Math.Clamp( _index, 0, count - 1 );
	}
}

// Plays the sounds of an effect the game spawned by Halo's sound rules (HaloSounds), not the mount's.
// The mount's effect runtime still times them: as an event plays a sound, it clones the effect root's "halo sound" child.
public sealed class Halo2EffectSounds : Component {
	const string EffectRootTag = "halo2_effect";
	const string SoundName = "halo sound";

	// What the sounds play from, for the per-object copy limits, and whether they're the local player's own.
	public object Source { get; private set; }
	public bool Own { get; private set; }

	// The object the sounds play on instead of the effect's locations, if set.
	public GameObject Anchor { get; set; }

	// Call before enabling a spawned prefab: every effect in it plays its sounds here.
	public static void Take( GameObject spawned, object source, bool own ) {
		var sounds = spawned.AddComponent<Halo2EffectSounds>();
		sounds.Source = source;
		sounds.Own = own;
		foreach ( var root in spawned.GetAllObjects( false ).Where( x => x.Tags.Has( EffectRootTag, false ) ).ToList() ) {
			var template = new GameObject( root, false, SoundName );
			template.NetworkMode = NetworkMode.Never;
			template.Tags.Add( LocalObjects.Tag );
			template.AddComponent<Halo2EffectSound>();
		}
	}
}

// One sound an effect's event plays, named after its sound tag and made at the event's location.
// It keeps to its object until that's deleted or hidden, then plays out where it was, as Halo does.
// Halo keeps following a weapon dropped on death as it falls; here the sound stays where it was held.
// It leaves the effect, so the effect ending or the view model hiding can't stop it.
public sealed class Halo2EffectSound : Component {
	SoundHandle _handle;
	GameObject _target;

	protected override void OnEnabled() {
		var sounds = GetComponentInParent<Halo2EffectSounds>();
		_target = sounds.Anchor.IsValid() ? sounds.Anchor : GameObject.Parent;
		GameObject.SetParent( null, true );
		Follow();

		_handle = HaloSounds.Play( GameObject.Name, WorldPosition, source: sounds.Source, own: sounds.Own );
		if ( _handle is null ) {
			GameObject.Destroy();
			return;
		}
		// An unspatialized sound stays at its listener.
		if ( _handle.ListenLocal ) return;
		_handle.Parent = GameObject;
		_handle.FollowParent = true;
	}

	// Gone once the sound ends, so the mount may play that part there again.
	protected override void OnUpdate() {
		if ( _handle is null || !_handle.IsValid || _handle.IsStopped || _handle.Finished ) {
			GameObject.Destroy();
			return;
		}
		Follow();
	}

	void Follow() {
		if ( _target is null ) return;
		if ( _target.IsValid() && _target.Active ) {
			WorldPosition = _target.WorldPosition;
			WorldRotation = _target.WorldRotation;
		}
		else _target = null;
	}
}
