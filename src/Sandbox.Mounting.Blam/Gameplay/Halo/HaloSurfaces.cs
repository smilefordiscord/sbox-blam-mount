using System;

namespace Halo;

// Halo's material types on level geometry: the mount names each collision triangle's Surface "halo_<material>".
// The index order is Halo's, which footsteps and projectile material responses are indexed by.
public static class HaloSurfaces {
	public static readonly string[] Names = {
		"dirt", "sand", "stone", "snow", "wood", "metal_hollow", "metal_thin", "metal_thick", "rubber", "glass",
		"force_field", "grunt", "hunter_armor", "hunter_skin", "elite", "jackal", "jackal_energy_shield",
		"engineer_skin", "engineer_force_field", "flood_combat_form", "flood_carrier_form", "cyborg_armor",
		"cyborg_energy_shield", "human_armor", "human_skin", "sentinel", "monitor", "plastic", "water", "leaves",
		"elite_energy_shield", "ice", "hunter_shield",
	};

	static readonly Dictionary<string, int> Index = Names.Select( ( n, i ) => (n, i) ).ToDictionary( x => "halo_" + x.n, x => x.i );

	// Suffix of the surface on triangles flagged climbable in the collision BSP, Halo's ladders.
	public const string ClimbableSuffix = "_climbable";

	// The Halo material index of a traced surface, or -1 for sky walls and non-Halo geometry.
	public static int MaterialIndex( Surface surface ) {
		var name = surface?.ResourceName;
		if ( string.IsNullOrEmpty( name ) ) return -1;
		if ( name.EndsWith( ClimbableSuffix ) ) name = name[..^ClimbableSuffix.Length];
		return Index.TryGetValue( name, out var i ) ? i : -1;
	}

	// The Halo material of any traced surface: a Halo map's own, else the nearest Halo material to an s&box surface by its name
	// (and its base surfaces'), so shots spark off Sandbox's metal and kick up its dirt. Stone for anything else.
	public static int MaterialOf( Surface surface ) {
		var index = MaterialIndex( surface );
		if ( index >= 0 ) return index;

		for ( var s = surface; s is not null; s = s.GetBaseSurface() ) {
			var name = s.ResourceName?.ToLowerInvariant() ?? "";
			foreach ( var (key, material) in SandboxMaterials )
				if ( name.Contains( key ) ) return material;
		}
		return Data.HaloMaterials.Stone;
	}

	static readonly (string Key, int Material)[] SandboxMaterials = {
		("flesh", 24), ("water", Data.HaloMaterials.Water), ("glass", Data.HaloMaterials.Glass), ("wood", Data.HaloMaterials.Wood),
		("metal", Data.HaloMaterials.MetalThick), ("plastic", 27), ("rubber", 8), ("tire", 8), ("dirt", Data.HaloMaterials.Dirt),
		("grass", Data.HaloMaterials.Dirt), ("mud", Data.HaloMaterials.Dirt), ("gravel", Data.HaloMaterials.Dirt), ("sand", Data.HaloMaterials.Sand),
		("snow", Data.HaloMaterials.Snow), ("ice", 31), ("foliage", Data.HaloMaterials.Leaves), ("leaves", Data.HaloMaterials.Leaves),
		("concrete", Data.HaloMaterials.Stone), ("rock", Data.HaloMaterials.Stone), ("stone", Data.HaloMaterials.Stone), ("brick", Data.HaloMaterials.Stone),
	};

	// A climbable surface is ground a biped can stand on at any angle.
	public static bool IsClimbable( Surface surface ) =>
		surface?.ResourceName is { } name && name.StartsWith( "halo_" ) && name.EndsWith( ClimbableSuffix );

	// Without climbable surfaces no ladder works; the mount exports all or none, so probing one is enough.
	public static bool MountHasClimbableSurfaces =>
		HaloResources.MountHas( $"mount://haloce/surfaces/halo_{Names[6]}{ClimbableSuffix}.surface" );

	// MCC multiplayer maps with climbable surfaces, for the warning when the mount doesn't export them.
	public static readonly HashSet<string> MapsWithLadders = new() {
		"beavercreek", "boardingaction", "damnation", "deathisland", "icefields", "infinity", "longest",
		"prisoner", "putput", "sidewinder", "timberland", "wizard",
	};

	// The material under a trace, falling back to the map's main ground material.
	public static int MaterialOrDefault( Surface surface, string mapName ) {
		var index = MaterialIndex( surface );
		return index >= 0 ? index : Data.HaloMaterials.GroundFor( mapName ?? "" );
	}
}
