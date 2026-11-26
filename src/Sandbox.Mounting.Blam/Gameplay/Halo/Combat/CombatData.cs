using System;
using Halo.Data;

namespace Halo.Combat;

// The mount's weapon tags and animation timings, as the combat step reads them.
public sealed class GameCombatData : ICombatData, IWeaponIndex {
	public static GameCombatData Instance { get; } = new();

	public WeaponTag Weapon( string path ) => HaloDataStore.Game?.Weapon( path );

	public bool HasFirstPersonGraph( WeaponTag weapon ) => AnimationGraphs.FirstPersonSlot( weapon?.FirstPersonAnimations, "idle" ) is not null;

	public CombatTiming? FirstPerson( WeaponTag weapon, string slot ) => Of( AnimationGraphs.FirstPersonSlot( weapon?.FirstPersonAnimations, slot ) );

	public int? WeaponAnimationFrames( WeaponTag weapon, string animation ) => AnimationGraphs.Find( weapon?.AnimationGraph, animation )?.FrameCount;

	public CombatTiming? BodyMelee( WeaponTag weapon ) => Of( AnimationGraphs.Find( Graph, $"stand {weapon?.Label} melee" ) ?? AnimationGraphs.Find( Graph, "stand rifle melee" ) );

	static string Graph => HaloDataStore.Spartan.Biped?.AnimationGraph ?? "characters/cyborg/cyborg.antr";

	// "stand rifle throw-grenade" (38 frames, key 8), except for the plasma cannon and flamethrower.
	public CombatTiming? Throw( WeaponTag weapon ) {
		var graph = HaloDataStore.Spartan.Biped?.AnimationGraph ?? "characters/cyborg/cyborg.antr";
		var name = weapon?.Label switch {
			"pc" => "zstand cannon throw-grenade",
			"ft" => "zstand flame throw-grenade",
			_ => "stand rifle throw-grenade",
		};
		return Of( AnimationGraphs.Find( graph, name ) ?? AnimationGraphs.Find( graph, "stand rifle throw-grenade" ) );
	}

	static CombatTiming? Of( AnimationGraphs.Timing? timing ) => timing is { } t ? new CombatTiming( t.FrameCount, t.KeyFrame ) : null;

	// wire index

	HaloGameData _indexed;
	string[] _paths = Array.Empty<string>();
	readonly Dictionary<string, ushort> _indices = new();

	// Every weapon tag path in ordinal order, so the index matches on every machine with the same mount.
	// Campaign-only weapons like the hunter's fuel rod are exported on first use, so they come from the mount's tag list.
	string[] Paths {
		get {
			var game = HaloDataStore.Game;
			if ( game == _indexed ) return _paths;
			_indexed = game;
			_paths = game?.Weapons.Keys.Union( HaloDataStore.TagPaths( "weap" ) ).Order( StringComparer.Ordinal ).ToArray() ?? Array.Empty<string>();
			_indices.Clear();
			for ( var i = 0; i < _paths.Length; i++ ) _indices[_paths[i]] = (ushort)i;
			return _paths;
		}
	}

	public ushort IndexOf( string path ) {
		_ = Paths;
		return path is not null && _indices.TryGetValue( path, out var index ) ? index : ushort.MaxValue;
	}

	public string PathOf( ushort index ) {
		var paths = Paths;
		return index < paths.Length ? paths[index] : null;
	}
}
