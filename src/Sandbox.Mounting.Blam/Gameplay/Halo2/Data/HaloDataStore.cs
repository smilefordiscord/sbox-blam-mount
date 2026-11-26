using Halo2.Combat;
using System;

namespace Halo2.Data;

// Loads the gameplay data the Blam mount exports from the MCC install, once.
// The resources are mount://halo2/game.json, maps/<map>.json and tags/<tag>.json.
// A failed read isn't retried until the mount's state changes.
public static class HaloDataStore {
	static HaloGameData _game;
	static int _gameFailedState = -1;
	static readonly Dictionary<string, (HaloMapData Data, int State)> _maps = new();

	// A hot reload keeps parsed data whose classes may have gained fields that would read empty, so it's parsed again.
	sealed class HotloadWatch : IHotloadManaged {
		public void Created( IReadOnlyDictionary<string, object> state ) => Reset();
		public void Persisted() => Reset();
		public void Destroyed( Dictionary<string, object> state ) { }
		public void Failed() { }
	}

	static readonly HotloadWatch Watch = new();

	// Goes up on every hot reload; other caches of parsed mount data compare it to know when to drop theirs.
	public static int Generation { get; private set; }

	static void Reset() {
		_game = null;
		_gameFailedState = -1;
		_maps.Clear();
		Generation++;
	}

	public static bool IsAvailable => Game is not null;

	public static HaloGameData Game {
		get {
			if ( _game is not null ) return _game;

			var state = HaloMountCheck.MountState;
			if ( state == _gameFailedState ) return null;

			_game = Parse<HaloGameData>( ReadMount( "game.json" ) );
			if ( _game is null ) {
				_gameFailedState = state;
				Log.Info( $"No Halo 2 gameplay data (mount://{HaloMountCheck.Ident}/game.json): the game runs on its fallbacks." );
				return null;
			}

			Log.Info( $"Halo data: {_game.Weapons.Count} weapons, {_game.Projectiles.Count} projectiles, {_game.DamageEffects.Count} damage effects" );
			return _game;
		}
	}

	public static HaloMapData Map( string mapName ) {
		var state = HaloMountCheck.MountState;
		if ( _maps.TryGetValue( mapName, out var cached ) && (cached.Data is not null || cached.State == state) )
			return cached.Data;

		var data = Parse<HaloMapData>( ReadMount( $"maps/{mapName}.json" ) );
		_maps[mapName] = (data, state);
		return data;
	}

	// One tag from the mount's export, e.g. Tag<EffectDefinitionTag>( "effects/impact/.../airborne_detonation.effe" ).
	public static T Tag<T>( string tagPath ) where T : class => Parse<T>( ReadMount( $"tags/{tagPath}.json" ) );

	// A text resource from the mount's data export.
	// Its loader is synchronous, so the task has already finished.
	public static string ReadMount( string path ) {
		try {
			var loader = HaloMountCheck.Mount?.GetByPath( $"mount://{HaloMountCheck.Ident}/{path}" );
			return loader?.GetOrCreate().Result as string;
		}
		catch ( Exception e ) {
			Log.Warning( $"Couldn't read mount://{HaloMountCheck.Ident}/{path}: {e.Message}" );
			return null;
		}
	}

	static T Parse<T>( string json ) where T : class {
		if ( string.IsNullOrEmpty( json ) ) return null;
		try {
			return Json.Deserialize<T>( json );
		}
		catch ( Exception e ) {
			Log.Warning( $"Couldn't read Halo data ({typeof( T ).Name}): {e.Message}" );
			return null;
		}
	}

	// The multiplayer Spartan's biped and model tags.
	public static BipedTag SpartanBiped => Game?.SpartanBiped;
	public static DamageInfoTag SpartanDamage => Game?.SpartanModel?.DamageInfo;
}
