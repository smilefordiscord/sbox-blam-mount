using System;
using Halo2.Data;

namespace Halo2.Combat;

// The object functions a mount model's shaders follow: gauges, displays, plasma glows, the needler's needles, the sword's blade.
// The mount lists them in <model>__functions.json, the index being the slot, and the shaders read slot i from HaloFunction<i>.
// A function the object can't evaluate is set below 0, so what it drives keeps its look at rest.
public static class ShaderFunctions {
	sealed class SlotsFile {
		public List<string> Functions { get; set; }
	}

	const float AtRest = -1;

	// The mount gives a model at most 8 slots.
	static readonly string[] Attributes = Enumerable.Range( 0, 8 ).Select( x => $"HaloFunction{x}" ).ToArray();
	static readonly float[] Values = new float[8];

	static readonly Dictionary<string, string[]> Cache = new();
	static (int Generation, int MountState) _cacheState;

	// The functions in slot order for a mount model such as "mount://halo2/objects/.../needler.vmdl"; empty for none.
	public static string[] Slots( string modelPath ) {
		if ( modelPath is null || !modelPath.EndsWith( ".vmdl" ) ) return Array.Empty<string>();
		var state = (HaloDataStore.Generation, HaloMountCheck.MountState);
		if ( _cacheState != state ) {
			_cacheState = state;
			Cache.Clear();
		}
		if ( Cache.TryGetValue( modelPath, out var cached ) ) return cached;

		var prefix = $"mount://{HaloMountCheck.Ident}/";
		var stem = modelPath.StartsWith( prefix ) ? modelPath[prefix.Length..^".vmdl".Length] : null;
		var json = stem is null ? null : HaloDataStore.ReadMount( $"{stem}__functions.json" );
		var slots = string.IsNullOrEmpty( json ) ? null : Json.Deserialize<SlotsFile>( json )?.Functions;
		return Cache[modelPath] = slots?.Take( Attributes.Length ).ToArray() ?? Array.Empty<string>();
	}

	// Sets the model's functions on every renderer of the object under root: the model, its glass parts and shells.
	// The camouflage variant is the same renderer with its model swapped, so it keeps them.
	public static void Apply( GameObject root, string modelPath, ObjectFunctions functions ) {
		if ( functions is null || !root.IsValid() ) return;
		var slots = Slots( modelPath );
		if ( slots.Length == 0 ) return;

		for ( var i = 0; i < slots.Length; i++ )
			Values[i] = functions.Provides( slots[i] ) ? functions.Value( slots[i] ) : AtRest;

		foreach ( var renderer in root.GetComponentsInChildren<ModelRenderer>( true ) ) {
			for ( var i = 0; i < slots.Length; i++ )
				renderer.Attributes.Set( Attributes[i], Values[i] );
		}
	}
}
