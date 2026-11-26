using System;
using System.Collections.Generic;
using System.Linq;
using Blam.Halo1.Tags;

namespace Blam.Halo1;

// Inside the namespace so these win over s&box's global Vector3 when this compiles into the mount.
using Vector3 = System.Numerics.Vector3;

/// <summary>
/// A light attached to a placed object, where its marker puts it.
/// </summary>
/// <param name="Position">World position, in world units.</param>
/// <param name="Forward">The marker's direction.</param>
public sealed record LightSource( TagEntry Tag, LightTag Light, Vector3 Position, Vector3 Forward );

/// <summary>
/// A light to create: one or more <see cref="LightSource"/>s of the same light tag merged together.
/// </summary>
/// <param name="Color">Halo color (gamma space, 0-1 per source), summed over the merged sources.</param>
/// <param name="Radius">World units.</param>
/// <param name="Sources">How many sources it stands for.</param>
public sealed record PlannedLight( TagEntry Tag, LightTag Light, Vector3 Position, Vector3 Forward, Vector3 Color, float Radius, int Sources );

/// <summary>
/// Decides which object lights become real-time lights, keeping how many overlap any area low.
/// Only lights flagged dynamic light anything as Halo runs; the rest are in the lightmaps, and
/// objects take them from there.
/// </summary>
/// <remarks>
/// s&amp;box bins lights into a 24x16x48 grid of view-space clusters and keeps at most 64 lights
/// in each, filled in order of screen size (<c>clustered_light_culling_cs</c>,
/// <c>LightBinnerFeature</c>). Clusters are screen tiles, so when a crowded cluster overflows, the
/// lights it drops change as the camera turns and tiles sweep across the world: lights pop on
/// and off. Distant clusters are big (a tile is a twelfth of the distance across and slices are
/// ~15% of it deep with the default near/far planes), so a level with over a hundred lights, like
/// a50's corridors lined with door lights, overflows them. To keep under the limit:
/// <list type="bullet">
/// <item>Several bulbs of one fixture facing the same way (a ceiling light's three spots, the
/// lock lights along one face of a door) a fraction of a unit apart become one light with their
/// summed color.</item>
/// <item>Lights too dim to see are dropped.</item>
/// <item>Where lights still crowd together, the least important (smallest, dimmest)
/// are dropped so that no neighbourhood holds more than <see cref="MaxNearby"/>.</item>
/// </list>
/// </remarks>
public static class ObjectLightPlan
{
	/// <summary>
	/// Sources of the same light tag closer than this (world units) merge into one light.
	/// </summary>
	public const float MergeDistance = 0.75f;

	/// <summary>
	/// Lights whose brightest channel is below this (Halo's 0-1 color) aren't worth a light.
	/// </summary>
	public const float MinColor = 0.04f;

	/// <summary>
	/// Most lights kept within <see cref="NeighbourhoodRadius"/> of any kept light, well under the
	/// 64 a cluster holds, since a distant cluster spans several neighbourhoods.
	/// </summary>
	public const int MaxNearby = 16;

	/// <summary>
	/// Size of the neighbourhood <see cref="MaxNearby"/> applies to, in world units (about 1000
	/// s&amp;box units: a cluster's width at 12000 units away).
	/// </summary>
	public const float NeighbourhoodRadius = 10f;

	public static List<PlannedLight> Plan( IEnumerable<LightSource> sources )
	{
		// Merge nearby bulbs of the same light facing the same way. Lights on the two faces of a
		// door stay apart: merged into the middle of the door, they'd light neither face.
		var merged = new List<PlannedLight>();
		foreach ( var group in sources.Where( s => s.Light.IsDynamic && s.Light.Radius > 0 ).GroupBy( s => s.Tag.Id ) )
		{
			var pending = group.ToList();
			while ( pending.Count > 0 )
			{
				var seed = pending[0];
				var cluster = pending.Where( s =>
					Vector3.Distance( s.Position, seed.Position ) <= MergeDistance &&
					Vector3.Dot( s.Forward, seed.Forward ) > 0.9f ).ToList();

				foreach ( var s in cluster )
					pending.Remove( s );

				var position = cluster.Aggregate( Vector3.Zero, ( sum, s ) => sum + s.Position ) / cluster.Count;
				var forward = Vector3.Normalize( cluster.Aggregate( Vector3.Zero, ( sum, s ) => sum + s.Forward ) );
				if ( float.IsNaN( forward.X ) )
					forward = seed.Forward;

				merged.Add( new PlannedLight( seed.Tag, seed.Light, position, forward, seed.Light.Color * cluster.Count, seed.Light.Radius, cluster.Count ) );
			}
		}

		// Most important first, by reach and brightness.
		var ordered = merged
			.Where( l => MathF.Max( l.Color.X, MathF.Max( l.Color.Y, l.Color.Z ) ) >= MinColor )
			.OrderByDescending( l => Importance( l ) )
			.ToList();

		var kept = new List<PlannedLight>();
		foreach ( var light in ordered )
		{
			var nearby = 0;
			foreach ( var other in kept )
			{
				if ( Vector3.DistanceSquared( other.Position, light.Position ) <= NeighbourhoodRadius * NeighbourhoodRadius )
					nearby++;
			}

			if ( nearby < MaxNearby )
				kept.Add( light );
		}

		return kept;
	}

	static float Importance( PlannedLight light ) =>
		light.Radius * MathF.Max( light.Color.X, MathF.Max( light.Color.Y, light.Color.Z ) );

	/// <summary>
	/// The most lights whose spheres reach into any sphere of <paramref name="radius"/> (world
	/// units) centred on one of the lights, a stand-in for the busiest cluster at a distance
	/// where clusters are about that big.
	/// </summary>
	public static int MaxOverlap( IReadOnlyList<(Vector3 Position, float Radius)> lights, float radius )
	{
		var max = 0;
		foreach ( var (center, _) in lights )
		{
			var count = 0;
			foreach ( var (position, r) in lights )
			{
				var reach = radius + r;
				if ( Vector3.DistanceSquared( center, position ) <= reach * reach )
					count++;
			}

			max = Math.Max( max, count );
		}

		return max;
	}
}
