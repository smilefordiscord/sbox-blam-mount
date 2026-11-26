using System;
using Halo2.Data;

namespace Halo2.Combat;

// The mount's weapon tags and animation timings, as the combat step reads them.
public sealed class GameCombatData : ICombatData, IWeaponIndex {
	public static GameCombatData Instance { get; } = new();

	public WeaponTag Weapon( string path ) => HaloDataStore.Game?.Weapon( path );

	public AnimationTiming? FirstPerson( string weapon, string label, bool dual ) => FirstPersonGraph.For( weapon )?.Head( label, dual )?.Timing;

	public AnimationTiming? ThirdPerson( WeaponTag weapon, string label ) => UnitGraph.FindCombat( HaloDataStore.SpartanBiped?.Model, weapon, label )?.Timing;

	public AnimationTiming? WeaponModel( WeaponTag weapon, string label, bool dual ) => UnitGraph.FindWeaponModel( weapon?.Model, label, dual )?.Timing;

	public PlayerInformationTag Player => HaloDataStore.Game?.Globals?.Player;

	// wire index

	HaloGameData _indexed;
	string[] _paths = Array.Empty<string>();
	readonly Dictionary<string, ushort> _indices = new();

	// Every weapon tag path in ordinal order, so the index matches on every machine with the same mount.
	string[] Paths {
		get {
			var game = HaloDataStore.Game;
			if ( game == _indexed ) return _paths;
			_indexed = game;
			_paths = game?.Weapons.Keys.Order( StringComparer.Ordinal ).ToArray() ?? Array.Empty<string>();
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
