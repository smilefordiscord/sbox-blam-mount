using Blam.Halo1;
using Blam.Halo1.Tags;

namespace Sandbox.Mounting.Blam.Halo1;

/// <summary>
/// Halo's material types as s&amp;box physics surfaces, <c>mount://haloce/surfaces/halo_&lt;type&gt;.surface</c>
/// (<c>halo_dirt</c>, <c>halo_metal_thin</c>, ...), so a trace against the level says what it hit
/// in <see cref="SceneTraceResult.Surface"/>: footsteps, bullet impacts, projectile responses.
/// </summary>
/// <remarks>
/// A surface's <see cref="Resource.ResourceName"/> is <c>halo_&lt;type&gt;</c>, and its tags are
/// <c>halo</c>, its own name and a broad kind (<c>flesh</c>, <c>metal</c>, <c>shield</c>...).
///
/// Ladders are level collision surfaces Halo flags climbable. Each material type has a climbable
/// twin, <c>halo_&lt;type&gt;_climbable</c> (and <c>halo_none_climbable</c> for climbable surfaces
/// without a material), the same as the plain one but for the name and an extra tag,
/// <c>climbable</c>, so a game finds ladders by what a trace hits.
/// The physics engine knows a mounted surface by its full mount path, and the engine loads it by
/// that name whenever a model's collision refers to it, so surfaces load wherever they're needed.
///
/// Halo has no friction per material for players (the globals' material block only scales vehicle
/// ground friction, and only ice sets it, to 0.12), so friction, bounce and density are sensible
/// values for the material, ice slippery as Halo makes it for vehicles.
/// </remarks>
static class HaloSurfaces
{
	/// <summary>
	/// Physical properties and tags of each material type, in <see cref="MaterialType"/> order.
	/// </summary>
	static readonly (float Friction, float Elasticity, float Density, AudioSurface Audio, string Kind)[] Properties =
	[
		(0.8f, 0.10f, 1600, AudioSurface.Soil, "ground"),          // dirt
		(0.7f, 0.05f, 1600, AudioSurface.Sand, "ground"),          // sand
		(0.8f, 0.20f, 2600, AudioSurface.Rock, "ground"),          // stone
		(0.35f, 0.05f, 400, AudioSurface.Snow, "ground"),          // snow
		(0.6f, 0.25f, 700, AudioSurface.Wood, "wood"),             // wood
		(0.5f, 0.30f, 2700, AudioSurface.Metal, "metal"),          // metal hollow
		(0.5f, 0.30f, 2700, AudioSurface.Metal, "metal"),          // metal thin
		(0.5f, 0.25f, 7800, AudioSurface.Steel, "metal"),          // metal thick
		(0.9f, 0.70f, 1100, AudioSurface.Generic, "rubber"),       // rubber
		(0.3f, 0.20f, 2500, AudioSurface.Glass, "glass"),          // glass
		(0.2f, 0.30f, 1000, AudioSurface.Generic, "shield"),       // force field
		(0.6f, 0.05f, 1000, AudioSurface.Leather, "flesh"),        // grunt
		(0.5f, 0.25f, 7800, AudioSurface.Metal, "armor"),          // hunter armor
		(0.6f, 0.05f, 1000, AudioSurface.Leather, "flesh"),        // hunter skin
		(0.6f, 0.05f, 1000, AudioSurface.Leather, "flesh"),        // elite
		(0.6f, 0.05f, 1000, AudioSurface.Leather, "flesh"),        // jackal
		(0.2f, 0.30f, 1000, AudioSurface.Generic, "shield"),       // jackal energy shield
		(0.6f, 0.05f, 1000, AudioSurface.Leather, "flesh"),        // engineer skin
		(0.2f, 0.30f, 1000, AudioSurface.Generic, "shield"),       // engineer force field
		(0.6f, 0.05f, 1000, AudioSurface.Leather, "flesh"),        // flood combat form
		(0.6f, 0.05f, 1000, AudioSurface.Leather, "flesh"),        // flood carrier form
		(0.5f, 0.20f, 2000, AudioSurface.Metal, "armor"),          // cyborg armor
		(0.2f, 0.30f, 1000, AudioSurface.Generic, "shield"),       // cyborg energy shield
		(0.5f, 0.20f, 2000, AudioSurface.Metal, "armor"),          // human armor
		(0.6f, 0.05f, 1000, AudioSurface.Leather, "flesh"),        // human skin
		(0.5f, 0.25f, 2700, AudioSurface.Metal, "metal"),          // sentinel
		(0.5f, 0.25f, 2700, AudioSurface.Metal, "metal"),          // monitor
		(0.5f, 0.30f, 950, AudioSurface.Generic, "plastic"),       // plastic
		(0.1f, 0.00f, 1000, AudioSurface.Water, "water"),          // water
		(0.7f, 0.05f, 300, AudioSurface.Generic, "foliage"),       // leaves
		(0.2f, 0.30f, 1000, AudioSurface.Generic, "shield"),       // elite energy shield
		(0.8f * 0.12f, 0.10f, 917, AudioSurface.Glass, "ground"),  // ice
		(0.5f, 0.25f, 7800, AudioSurface.Metal, "armor"),          // hunter shield
	];

	/// <summary>
	/// The surface's resource name: <c>halo_</c> and Halo's name for the material type.
	/// </summary>
	public static string ResourceName( MaterialType type ) => $"halo_{MaterialTypes.Name( type )}";

	/// <summary>
	/// The resource name of a material type's surface, or its climbable twin (see the remarks);
	/// <c>halo_none_climbable</c> for a climbable surface without one.
	/// </summary>
	public static string ResourceName( MaterialType? type, bool climbable ) =>
		type is { } t ? (climbable ? $"{ResourceName( t )}_climbable" : ResourceName( t )) : climbable ? "halo_none_climbable" : null;

	/// <summary>
	/// Full path to load a material type's surface with (also its name in the physics engine).
	/// </summary>
	public static string SurfacePath( string ident, MaterialType type ) => $"mount://{ident}/surfaces/{ResourceName( type )}.surface";

	public static void Register( MountContext context )
	{
		for ( var i = 0; i < MaterialTypes.Count; i++ )
		{
			context.Add( ResourceType.GameResource, $"surfaces/{ResourceName( (MaterialType)i )}.surface", new SurfaceLoader( (MaterialType)i, false ) );
			context.Add( ResourceType.GameResource, $"surfaces/{ResourceName( (MaterialType)i, true )}.surface", new SurfaceLoader( (MaterialType)i, true ) );
		}

		context.Add( ResourceType.GameResource, $"surfaces/{ResourceName( null, true )}.surface", new SurfaceLoader( null, true ) );
	}

	/// <summary>
	/// The surface of a material type, or null (the default surface) for none. Main thread only:
	/// the first use loads it.
	/// </summary>
	public static Surface Get( MaterialType? type ) => Get( type, false );

	/// <summary>
	/// The surface of a material type or its climbable twin, or null (the default surface) for a
	/// plain surface without a material. Main thread only: the first use loads it.
	/// </summary>
	public static Surface Get( MaterialType? type, bool climbable ) =>
		ResourceName( type, climbable ) is { } name && HaloCeMount.Current is { } host ? Surface.FindByName( $"mount://{host.Ident}/surfaces/{name}.surface" ) : null;

	/// <summary>
	/// Adds the surfaces these triangles are made of to a model, and returns each triangle's index
	/// into them, for <see cref="ModelBuilder.AddCollisionMesh(List{Vector3}, List{int}, List{byte})"/>.
	/// Triangles without a material type get the default surface. Main thread only.
	/// </summary>
	/// <param name="climbable">Whether each triangle is on a climbable surface (a ladder), or null for none.</param>
	public static List<byte> AddSurfaces( ModelBuilder builder, IEnumerable<MaterialType?> triangleTypes, IReadOnlyList<bool> climbable = null )
	{
		var slots = new Dictionary<(int, bool), byte>();
		var triangles = new List<byte>();
		var index = 0;
		foreach ( var type in triangleTypes )
		{
			var climb = climbable is not null && index < climbable.Count && climbable[index];
			var key = (type is { } t ? (int)t : -1, climb);
			if ( !slots.TryGetValue( key, out var slot ) )
			{
				slot = (byte)slots.Count;
				slots[key] = slot;
				builder.AddSurface( Get( type, climb ) );
			}

			triangles.Add( slot );
			index++;
		}

		return triangles;
	}

	/// <param name="type">The material type, or null for the climbable surface without one.</param>
	sealed class SurfaceLoader( MaterialType? type, bool climbable ) : HaloCeLoader
	{
		protected override object Load()
		{
			var (friction, elasticity, density, audio, kind) = type is { } t ? Properties[(int)t] : (0.6f, 0.2f, 1000f, AudioSurface.Generic, "generic");
			var plain = type is { } m ? ResourceName( m ) : "halo_none";
			var description = type is { } d ? $"Halo material type \"{MaterialTypes.Name( d ).Replace( '_', ' ' )}\"" : "Halo surface without a material type";
			return new Surface
			{
				Description = climbable ? $"{description}, climbable" : description,
				Friction = friction,
				Elasticity = elasticity,
				Density = density,
				AudioSurface = audio,
				Tags = climbable ? $"halo {plain} {kind} climbable" : $"halo {plain} {kind}",
			};
		}
	}
}

/// <summary>
/// Halo's water as trigger volumes: a box under every fog plane whose material type is water, up
/// to the water's surface, with the <c>halo_water</c> surface and the tags <c>water</c> and
/// <c>halo_water</c>. A game finds the water with a trace that hits triggers
/// (<c>Scene.Trace.Ray( a, b ).HitTriggers()</c>, the hit's surface is halo_water) or by touching
/// the trigger (wading, swimming, splashes).
/// </summary>
/// <remarks>
/// The box top is Halo's water plane, which its vehicles measure their depth in the water from.
/// The level's own collision has water material only under the water: the sea and river beds,
/// shaded "underwater" (b30, deathisland, c10). The surface itself isn't solid in Halo, it's a
/// fog plane of material water (b30's sea, c10's swamp, d20's coolant, deathisland). The fog
/// plane can sit above the water (c10's swamp mist is 0.45 world units over it), so the box
/// stops at the water found under the plane when there is some.
/// </remarks>
static class WaterVolumes
{
	public const string Tag = "water";

	public static void Build( HaloCeMount host, CacheFile cache, ScenarioTag scenario )
	{
		GameObject root = null;
		var surface = HaloSurfaces.Get( MaterialType.Water );

		foreach ( var reference in scenario.StructureBsps )
		{
			var name = reference.Tag?.Path.Replace( '\\', '/' ) ?? $"{cache.Name}/bsp{reference.Index}";

			StructureBspFog fog;
			try
			{
				fog = StructureBspTag.ReadFog( cache, reference );
			}
			catch ( Exception e )
			{
				host.Logger.Warning( e, $"{name}: couldn't read fog planes: {e.Message}" );
				continue;
			}

			List<(System.Numerics.Vector3, System.Numerics.Vector3, System.Numerics.Vector3)> water = null;
			for ( var i = 0; i < fog.Planes.Count; i++ )
			{
				if ( fog.Planes[i].Material != MaterialType.Water || FogVolume.Create( fog, i ) is not { } shape )
					continue;

				water ??= new StructureBspTag( cache, reference ).ReadWaterTriangles().ToList();
				shape.AddWater( water );

				// Up to the water. Halo's water plane is the fog plane less the fog's "distance to water
				// plane" (c10's swamp mist sits 0.4 over its water); without a fog tag, the water found
				// under the plane, else the plane.
				var min = (Vector3)shape.Min * HaloCeMount.WorldScale;
				var max = (Vector3)shape.Max * HaloCeMount.WorldScale;
				if ( shape.Axis == 2 && shape.Normal.Z > 0 )
				{
					var plane = fog.Planes[i].Plane;
					float? level = fog.GetFog( fog.Planes[i] ) is { } fogTag
						? (plane.W - new FogTag( cache, fogTag ).DistanceToWaterPlane) / plane.Z
						: shape.WaterLevel;
					if ( level is { } z && z > shape.Min.Z )
						max = max.WithZ( z * HaloCeMount.WorldScale );
				}

				root ??= new GameObject( true, "water" );
				var go = new GameObject( root, true, $"{System.IO.Path.GetFileName( name )} water {i}" );
				go.Tags.Add( Tag );
				go.Tags.Add( HaloSurfaces.ResourceName( MaterialType.Water ) );
				go.WorldPosition = (min + max) * 0.5f;

				var box = go.AddComponent<BoxCollider>();
				box.Scale = max - min;
				box.IsTrigger = true;
				box.Static = true;
				box.Surface = surface;
			}
		}
	}
}
