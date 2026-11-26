using Blam.Halo2;
using Blam.Halo2.Tags;

namespace Sandbox.Mounting.Blam.Halo2;

/// <summary>
/// Halo 2's global materials as s&amp;box physics surfaces, <c>mount://halo2/surfaces/halo2_&lt;material&gt;.surface</c>
/// (<c>halo2_hard_metal_thick_for</c>, <c>halo2_tough_terrain_sand</c>...), so a trace against a level says what it
/// hit in <see cref="SceneTraceResult.Surface"/> (footsteps, impacts), like the Halo CE mount's <c>halo_&lt;type&gt;</c>.
/// </summary>
/// <remarks>
/// Halo 2 names its materials by kind, then what they are, then whose: <c>hard_metal_thin_hum_warthog</c>, each
/// name's parent being the name without its last part. Every MCC map has the same 183 (shared.map's globals hold
/// them for multiplayer), so the surfaces are registered from shared.map once. A surface's tags are <c>halo2</c> and
/// a broad kind (<c>metal</c>, <c>ground</c>, <c>glass</c>, <c>water</c>...): its name says the rest.
///
/// Friction, bounce and density come from the name's kind (the nearest ancestor below), as sensible values for the
/// material: the globals' material physics are Havok's and have no per-material friction for players either.
/// Collision surfaces without a material (the sky's walls) get the default surface.
/// </remarks>
static class Halo2Surfaces
{
	/// <summary>
	/// Physical properties and kind by material name prefix, the first match on the name or one of its ancestors.
	/// </summary>
	static readonly (string Prefix, float Friction, float Elasticity, float Density, AudioSurface Audio, string Kind)[] Kinds =
	[
		("liquid", 0.1f, 0.00f, 1000, AudioSurface.Water, "water"),
		("soft_organic_flesh", 0.6f, 0.05f, 1000, AudioSurface.Leather, "flesh"),
		("tough_organic_flesh", 0.6f, 0.05f, 1000, AudioSurface.Leather, "flesh"),
		("tough_organic_bone", 0.6f, 0.10f, 1900, AudioSurface.Leather, "flesh"),
		("tough_floodflesh", 0.6f, 0.05f, 1000, AudioSurface.Leather, "flesh"),
		("hard_floodflesh", 0.6f, 0.05f, 1000, AudioSurface.Leather, "flesh"),
		("soft_organic_cloth", 0.7f, 0.05f, 300, AudioSurface.Generic, "cloth"),
		("soft_organic_plant", 0.7f, 0.05f, 300, AudioSurface.Generic, "foliage"),
		("soft_inorganic", 0.9f, 0.50f, 1100, AudioSurface.Generic, "rubber"),
		("soft_terrain_snow", 0.35f, 0.05f, 400, AudioSurface.Snow, "ground"),
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
		// Ice is as slippery as Halo makes it for vehicles in the Halo CE mount.
		("hard_terrain_ice", 0.8f * 0.12f, 0.10f, 917, AudioSurface.Glass, "ground"),
		("hard_terrain", 0.8f, 0.20f, 2600, AudioSurface.Rock, "ground"),
		("brittle_glass", 0.3f, 0.20f, 2500, AudioSurface.Glass, "glass"),
		("brittle", 0.5f, 0.30f, 2700, AudioSurface.Metal, "metal"),
		("energy", 0.2f, 0.30f, 1000, AudioSurface.Generic, "shield"),
		("bounds", 0.6f, 0.20f, 1000, AudioSurface.Generic, "bounds"),
	];

	/// <summary>
	/// The surface's resource name: <c>halo2_</c> and the global material's name.
	/// </summary>
	public static string ResourceName( string material ) => $"halo2_{material}";

	public static void Register( MountContext context, CacheFile shared )
	{
		if ( shared.Globals is null )
			return;

		foreach ( var material in new GlobalsTag( shared.Globals ).Materials )
		{
			if ( !string.IsNullOrEmpty( material.Name ) )
				context.Add( ResourceType.GameResource, $"surfaces/{ResourceName( material.Name )}.surface", new SurfaceLoader( material.Name ) );
		}
	}

	/// <summary>
	/// The surface of a global material, or null (the default surface) for none. Main thread only: the first use
	/// loads it.
	/// </summary>
	public static Surface Get( string material ) =>
		string.IsNullOrEmpty( material ) || Halo2Mount.Current is not { } host ? null : Surface.FindByName( $"mount://{host.Ident}/surfaces/{ResourceName( material )}.surface" );

	/// <summary>
	/// Adds the surfaces these triangles are made of to a model, and returns each triangle's index into them, for
	/// <see cref="ModelBuilder.AddCollisionMesh(List{Vector3}, List{int}, List{byte})"/>. Main thread only.
	/// </summary>
	/// <param name="triangleMaterials">Each triangle's global material name, null for the default surface.</param>
	public static List<byte> AddSurfaces( ModelBuilder builder, IEnumerable<string> triangleMaterials )
	{
		var slots = new Dictionary<string, byte>();
		var triangles = new List<byte>();
		foreach ( var material in triangleMaterials )
		{
			var key = material ?? "";
			if ( !slots.TryGetValue( key, out var slot ) )
			{
				slot = (byte)slots.Count;
				slots[key] = slot;
				builder.AddSurface( Get( material ) );
			}

			triangles.Add( slot );
		}

		return triangles;
	}

	sealed class SurfaceLoader( string material ) : Halo2Loader
	{
		protected override object Load()
		{
			var kind = Kinds.FirstOrDefault( k => material.StartsWith( k.Prefix, StringComparison.Ordinal ) );
			var (friction, elasticity, density, audio, tag) = kind.Prefix is null
				? (0.6f, 0.2f, 1000f, AudioSurface.Generic, "generic")
				: (kind.Friction, kind.Elasticity, kind.Density, kind.Audio, kind.Kind);

			return new Surface
			{
				Description = $"Halo 2 material \"{material}\"",
				Friction = friction,
				Elasticity = elasticity,
				Density = density,
				AudioSurface = audio,
				Tags = $"halo2 {tag}",
			};
		}
	}
}
