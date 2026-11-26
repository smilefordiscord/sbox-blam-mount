using System;
using Halo2.Data;

namespace Halo2.Combat;

// Halo 2's global materials on level geometry: the mount names each collision surface's Surface "halo2_<material>".
// A material's index is its place in the globals' materials block, which material responses are indexed by.
public static class HaloSurfaces {
	const string Prefix = "halo2_";

	// Suffix of the surface on triangles flagged climbable in the collision BSP.
	public const string ClimbableSuffix = "_climbable";

	static GlobalsTag _indexedGlobals;
	static Dictionary<string, int> _index;

	// The global material index of a traced surface, or -1 for sky walls, non-Halo geometry, or without the globals.
	public static int MaterialIndex( Surface surface ) {
		var name = surface?.ResourceName;
		if ( string.IsNullOrEmpty( name ) || !name.StartsWith( Prefix ) ) return -1;
		if ( name.EndsWith( ClimbableSuffix ) ) name = name[..^ClimbableSuffix.Length];
		return MaterialIndex( name[Prefix.Length..] );
	}

	// The global material index of a material name, or -1.
	public static int MaterialIndex( string material ) => material is not null && Index().TryGetValue( material, out var i ) ? i : -1;

	// The material under a trace, or the globals' first when the trace found none.
	public static int MaterialOrDefault( Surface surface ) => Math.Max( 0, MaterialIndex( surface ) );

	// A climbable surface is ground a biped can stand on at any angle.
	public static bool IsClimbable( Surface surface ) =>
		surface?.ResourceName is { } name && name.StartsWith( Prefix ) && name.EndsWith( ClimbableSuffix );

	static Dictionary<string, int> Index() {
		var globals = HaloDataStore.Game?.Globals;
		if ( _index is not null && globals == _indexedGlobals ) return _index;

		_indexedGlobals = globals;
		_index = new( StringComparer.OrdinalIgnoreCase );
		var materials = globals?.Materials ?? new();
		for ( var i = 0; i < materials.Count; i++ ) {
			if ( !string.IsNullOrEmpty( materials[i].Name ) )
				_index.TryAdd( materials[i].Name, i );
		}
		return _index;
	}
}
