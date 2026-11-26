using Blam.Halo3;

namespace Sandbox.Mounting.Blam.Halo3;

// Halo 3's global materials (matg +0x464) as s&box physics surfaces, mount://halo3/surfaces/halo3_<material>.surface, named like Halo 2's (hard_metal_thick_hum_warthog...), so traces say what they hit.
static class Halo3Surfaces
{
	// Physical properties and kind by material name prefix, the first match; as Halo2Surfaces.
	static readonly (string Prefix, float Friction, float Elasticity, float Density, AudioSurface Audio, string Kind)[] Kinds =
	[
		("liquid", 0.1f, 0.00f, 1000, AudioSurface.Water, "water"),
		("soft_organic_flesh", 0.6f, 0.05f, 1000, AudioSurface.Leather, "flesh"),
		("tough_organic_flesh", 0.6f, 0.05f, 1000, AudioSurface.Leather, "flesh"),
		("tough_organic_bone", 0.6f, 0.10f, 1900, AudioSurface.Leather, "flesh"),
		("soft_floodflesh", 0.6f, 0.05f, 1000, AudioSurface.Leather, "flesh"),
		("tough_floodflesh", 0.6f, 0.05f, 1000, AudioSurface.Leather, "flesh"),
		("hard_floodflesh", 0.6f, 0.05f, 1000, AudioSurface.Leather, "flesh"),
		("tough_terrain_floodflesh", 0.6f, 0.05f, 1000, AudioSurface.Leather, "flesh"),
		("soft_organic_cloth", 0.7f, 0.05f, 300, AudioSurface.Generic, "cloth"),
		("soft_organic_plant", 0.7f, 0.05f, 300, AudioSurface.Generic, "foliage"),
		("soft_inorganic", 0.9f, 0.50f, 1100, AudioSurface.Generic, "rubber"),
		("soft_terrain_snow", 0.35f, 0.05f, 400, AudioSurface.Snow, "ground"),
		("soft_terrain_beach", 0.7f, 0.05f, 1600, AudioSurface.Sand, "ground"),
		("soft_terrain", 0.8f, 0.10f, 1600, AudioSurface.Soil, "ground"),
		("tough_organic_wood", 0.6f, 0.25f, 700, AudioSurface.Wood, "wood"),
		("tough_terrain_woodfloor", 0.6f, 0.25f, 700, AudioSurface.Wood, "wood"),
		("tough_inorganic_rubber", 0.9f, 0.70f, 1100, AudioSurface.Generic, "rubber"),
		("tough_inorganic_plastic", 0.5f, 0.30f, 950, AudioSurface.Generic, "plastic"),
		("tough_inorganic_armor", 0.5f, 0.20f, 2000, AudioSurface.Metal, "armor"),
		("tough_terrain_sand", 0.7f, 0.05f, 1600, AudioSurface.Sand, "ground"),
		("tough_terrain_gravel", 0.8f, 0.15f, 1800, AudioSurface.Rock, "ground"),
		("tough_terrain_carpet", 0.8f, 0.05f, 300, AudioSurface.Generic, "ground"),
		("tough_terrain", 0.8f, 0.10f, 1600, AudioSurface.Soil, "ground"),
		("hard_metal_thin", 0.5f, 0.30f, 2700, AudioSurface.Metal, "metal"),
		("hard_metal", 0.5f, 0.25f, 7800, AudioSurface.Steel, "metal"),
		("hard_terrain_ice", 0.8f * 0.12f, 0.10f, 917, AudioSurface.Glass, "ground"),
		("hard_terrain_glass", 0.3f, 0.20f, 2500, AudioSurface.Glass, "glass"),
		("hard_terrain", 0.8f, 0.20f, 2600, AudioSurface.Rock, "ground"),
		("brittle_glass", 0.3f, 0.20f, 2500, AudioSurface.Glass, "glass"),
		("brittle", 0.5f, 0.30f, 2700, AudioSurface.Metal, "metal"),
		("energy", 0.2f, 0.30f, 1000, AudioSurface.Generic, "shield"),
		("bounds", 0.6f, 0.20f, 1000, AudioSurface.Generic, "bounds"),
	];

	public static string ResourceName( string material ) => $"halo3_{material}";

	// The global materials' names by index.
	public static List<string> MaterialNames( CacheFile cache ) =>
		cache.Globals?.Data.Block( 0x464, 0x170 ).Select( m => m.StringId( 0 ) ).ToList() ?? new List<string>();

	public static void Register( MountContext context, CacheFile cache, HashSet<string> registered )
	{
		foreach ( var material in MaterialNames( cache ) )
		{
			var path = $"surfaces/{ResourceName( material )}.surface";
			if ( !string.IsNullOrEmpty( material ) && registered.Add( path ) )
				context.Add( ResourceType.GameResource, path, new SurfaceLoader( material ) );
		}
	}

	// The surface of a global material, or null (the default surface) for none. Main thread only.
	public static Surface Get( string material ) =>
		string.IsNullOrEmpty( material ) || Halo3Mount.Current is not { } host ? null : Surface.FindByName( $"mount://{host.Ident}/surfaces/{ResourceName( material )}.surface" );

	// Adds the surfaces these triangles are made of to a model and returns each triangle's index into them, for AddCollisionMesh. Main thread only.
	public static List<byte> AddSurfaces( ModelBuilder builder, IEnumerable<string> triangleMaterials )
	{
		var slots = new Dictionary<string, byte>();
		var triangles = new List<byte>();
		foreach ( var material in triangleMaterials )
		{
			var key = material ?? "";
			if ( !slots.TryGetValue( key, out var slot ) )
			{
				slot = (byte)Math.Min( slots.Count, 255 );
				slots[key] = slot;
				builder.AddSurface( Get( material ) );
			}

			triangles.Add( slot );
		}

		return triangles;
	}

	sealed class SurfaceLoader( string material ) : Halo3Loader
	{
		protected override object Load()
		{
			var kind = Kinds.FirstOrDefault( k => material.StartsWith( k.Prefix, StringComparison.Ordinal ) );
			var (friction, elasticity, density, audio, tag) = kind.Prefix is null
				? (0.6f, 0.2f, 1000f, AudioSurface.Generic, "generic")
				: (kind.Friction, kind.Elasticity, kind.Density, kind.Audio, kind.Kind);

			return new Surface
			{
				Description = $"Halo 3 material \"{material}\"",
				Friction = friction,
				Elasticity = elasticity,
				Density = density,
				AudioSurface = audio,
				Tags = $"halo3 {tag}",
			};
		}
	}
}
