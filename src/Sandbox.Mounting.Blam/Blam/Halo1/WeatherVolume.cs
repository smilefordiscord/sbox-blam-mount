using System;
using System.Collections.Generic;
using Blam.Halo1.Tags;

namespace Blam.Halo1;

// Inside the namespace so these win over s&box's global Vector3 when this compiles into the mount.
using Vector3 = System.Numerics.Vector3;

/// <summary>
/// A weather particle system that falls somewhere in a level, and the wind blowing it.
/// </summary>
/// <param name="Wind">Wind direction times magnitude, from the BSP's weather palette.</param>
/// <param name="WindTag">The wind tag, whose speed range scales <paramref name="Wind"/>.</param>
public sealed record WeatherSystem( TagEntry ParticleSystem, Vector3 Wind, TagEntry WindTag );

/// <summary>
/// Where a level's weather falls, as a grid of cells over the part of one BSP that has weather.
/// Each cell holds 0 for no weather, or 1 + the index of the weather system falling there.
/// </summary>
/// <remarks>
/// Halo decides per cluster: each cluster names an entry of the BSP's weather palette, and
/// weather polyhedra cut out spaces under overhangs where it shouldn't fall. A particle shader
/// can't walk the BSP, so the answer is sampled into a grid instead.
/// </remarks>
public sealed class WeatherVolume
{
	/// <summary>
	/// Keeps the grid (and its texture) to a few megabytes.
	/// </summary>
	const int MaxCells = 2_000_000;

	/// <summary>
	/// Smallest cell size, in world units. Weather particles are fractions of a world unit, and
	/// the outdoors they fill are tens of units across.
	/// </summary>
	const float MinCellSize = 1.0f;

	/// <summary>
	/// Cell size of the first pass that finds where weather is at all.
	/// </summary>
	const float CoarseCellSize = 4.0f;

	/// <summary>
	/// Minimum corner of the grid, in world units.
	/// </summary>
	public Vector3 Origin { get; }
	public float CellSize { get; }
	public int SizeX { get; }
	public int SizeY { get; }
	public int SizeZ { get; }
	public byte[] Cells { get; }

	/// <summary>
	/// Weather systems by cell value - 1. Shared by all of a level's volumes.
	/// </summary>
	public IReadOnlyList<WeatherSystem> Systems { get; }

	WeatherVolume( Vector3 origin, float cellSize, int x, int y, int z, byte[] cells, IReadOnlyList<WeatherSystem> systems )
	{
		Origin = origin;
		CellSize = cellSize;
		SizeX = x;
		SizeY = y;
		SizeZ = z;
		Cells = cells;
		Systems = systems;
	}

	/// <summary>
	/// Builds a volume for each BSP that has weather with particles. Where BSPs overlap, the
	/// earlier one decides, so the same space doesn't get weather twice.
	/// </summary>
	public static List<WeatherVolume> Build( IReadOnlyList<StructureBspEffects> bsps )
	{
		var systems = new List<WeatherSystem>();
		var volumes = new List<WeatherVolume>();

		for ( var b = 0; b < bsps.Count; b++ )
		{
			var bsp = bsps[b];

			// Palette entry -> system value (1 + index), 0 for entries without particles.
			var values = new byte[bsp.WeatherPalette.Count];
			for ( var i = 0; i < values.Length; i++ )
			{
				var entry = bsp.WeatherPalette[i];
				if ( entry.ParticleSystem is null )
					continue;

				var index = systems.FindIndex( s => s.ParticleSystem.Id == entry.ParticleSystem.Id );
				if ( index < 0 )
				{
					index = systems.Count;
					systems.Add( new WeatherSystem( entry.ParticleSystem, entry.WindDirection * entry.WindMagnitude, entry.Wind ) );
				}

				values[i] = (byte)Math.Min( index + 1, 255 );
			}

			// Palette entries no cluster uses (b30's underwater debris) don't count.
			var used = false;
			foreach ( var weather in bsp.ClusterWeather )
				used |= weather >= 0 && weather < values.Length && values[weather] != 0;

			if ( !used )
				continue;

			byte Sample( Vector3 point )
			{
				var weather = bsp.FindWeather( point );
				if ( weather < 0 || weather >= values.Length || values[weather] == 0 )
					return 0;

				for ( var earlier = 0; earlier < b; earlier++ )
				{
					if ( bsps[earlier].FindCluster( point ) >= 0 )
						return 0;
				}

				return values[weather];
			}

			// Find the part of the BSP with weather, on a coarse grid.
			var min = new Vector3( float.MaxValue );
			var max = new Vector3( float.MinValue );
			var size = bsp.WorldBoundsMax - bsp.WorldBoundsMin;
			var coarse = new Vector3( MathF.Ceiling( size.X / CoarseCellSize ), MathF.Ceiling( size.Y / CoarseCellSize ), MathF.Ceiling( size.Z / CoarseCellSize ) );
			for ( var z = 0; z < coarse.Z; z++ )
				for ( var y = 0; y < coarse.Y; y++ )
					for ( var x = 0; x < coarse.X; x++ )
					{
						var point = bsp.WorldBoundsMin + (new Vector3( x, y, z ) + new Vector3( 0.5f )) * CoarseCellSize;
						if ( Sample( point ) == 0 )
							continue;

						min = Vector3.Min( min, point );
						max = Vector3.Max( max, point );
					}

			if ( min.X > max.X )
				continue;

			min -= new Vector3( CoarseCellSize );
			max += new Vector3( CoarseCellSize );
			var extent = max - min;
			var cellSize = MathF.Max( MinCellSize, MathF.Cbrt( extent.X * extent.Y * extent.Z / MaxCells ) );
			var sx = Math.Max( 1, (int)MathF.Ceiling( extent.X / cellSize ) );
			var sy = Math.Max( 1, (int)MathF.Ceiling( extent.Y / cellSize ) );
			var sz = Math.Max( 1, (int)MathF.Ceiling( extent.Z / cellSize ) );

			var cells = new byte[sx * sy * sz];
			System.Threading.Tasks.Parallel.For( 0, sz, z =>
			{
				for ( var y = 0; y < sy; y++ )
					for ( var x = 0; x < sx; x++ )
						cells[x + sx * (y + sy * z)] = Sample( min + (new Vector3( x, y, z ) + new Vector3( 0.5f )) * cellSize );
			} );

			volumes.Add( new WeatherVolume( min, cellSize, sx, sy, sz, cells, systems ) );
		}

		return volumes;
	}
}

/// <summary>
/// How weather particles move and how many there are.
/// </summary>
public static class WeatherMotion
{
	/// <summary>
	/// Fastest fall, world units per second. Rain's physics would otherwise come out at 20.
	/// </summary>
	const float MaxFallSpeed = 8;

	/// <summary>
	/// The gravity the friction guess below was tuned with, lower than Halo's
	/// (0.0035651792 world units per tick², 3.21 per second²); kept so weather falls as it did.
	/// </summary>
	const float TunedGravity = 0.00214f * 30 * 30;

	/// <summary>
	/// A weather particle type's steady velocity in world units per second: falling at the speed
	/// where gravity (against the air's buoyancy) balances air friction, blown by the wind.
	/// </summary>
	/// <remarks>
	/// Wind tags give a speed range (0x0), scaled by the weather palette's wind magnitude and
	/// pointing along its direction. Particles with damped wind (point physics flag 0x10) follow
	/// it at half speed.
	/// </remarks>
	public static Vector3 Velocity( CacheFile cache, WeatherParticleType type, WeatherSystem system )
	{
		var velocity = Vector3.Zero;
		var physics = type.Physics is null ? null : new PointPhysicsTag( cache, type.Physics );
		if ( physics is not null && !physics.NoGravity && MathF.Abs( physics.AirGravityScale ) > 0.1f )
		{
			var drag = Math.Clamp( 0.01f * physics.AirFriction / MathF.Max( physics.Density * 1000, 1 ), 0.05f, 10 );
			var fall = Math.Clamp( physics.AirGravityScale * TunedGravity / drag, -MaxFallSpeed, MaxFallSpeed );
			velocity.Z = fall;
		}

		var wind = system.WindTag;
		if ( wind is not null && system.Wind != Vector3.Zero )
		{
			var data = cache.TagData;
			var speed = (data.F32( wind.Address ) + data.F32( wind.Address + 4 )) * 0.5f;
			var damped = physics is not null && (physics.Flags & 0x10) != 0;
			velocity += system.Wind * speed * (damped ? 0.5f : 1);
		}

		return velocity;
	}

	/// <summary>
	/// Halo simulates at most this many weather particles, shared by all of a system's particle
	/// types (the first types get them first).
	/// </summary>
	public const int ParticleBudget = 512;

	/// <summary>
	/// The size of the cube of particles drawn around the camera, in world units: twice the
	/// distance they've faded out by, so it reaches that far in every direction.
	/// </summary>
	public static float CubeSize( WeatherParticleType type ) => SimulationCubeSize( type ) * 2;

	/// <summary>
	/// The size of the cube Halo simulates a particle type in, which repeats through space: its
	/// fade out end distance.
	/// </summary>
	public static float SimulationCubeSize( WeatherParticleType type ) => MathF.Max( type.FadeDistance.W, 0.5f );

	/// <summary>
	/// How many particles Halo simulates for each of a system's particle types. The tag's particle
	/// count is a density per cubic world unit (c40's snow is 8, the swamp beetles 0.05) in the
	/// simulation cube, limited by the shared <see cref="ParticleBudget"/>: c40's snow asks for
	/// 729 in its 4.5 unit cube and gets 512.
	/// </summary>
	public static int[] SimulatedCounts( IReadOnlyList<WeatherParticleType> types )
	{
		var counts = new int[types.Count];
		var remaining = ParticleBudget;
		for ( var i = 0; i < types.Count; i++ )
		{
			var wanted = (int)MathF.Round( (types[i].ParticleCount.X + types[i].ParticleCount.Y) * 0.5f * MathF.Pow( SimulationCubeSize( types[i] ), 3 ) );
			counts[i] = Math.Clamp( wanted, 0, remaining );
			remaining -= counts[i];
		}

		return counts;
	}

	/// <summary>
	/// How many particles to draw in the <see cref="CubeSize"/> cube for the same density as
	/// Halo's simulation cube repeated through space: eight times as many.
	/// </summary>
	public static int DrawnCount( WeatherParticleType type, int simulated, int max ) =>
		Math.Clamp( (int)MathF.Round( simulated * MathF.Pow( CubeSize( type ) / SimulationCubeSize( type ), 3 ) ), 0, max );
}
