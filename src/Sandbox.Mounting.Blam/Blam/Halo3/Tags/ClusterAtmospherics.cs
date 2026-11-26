using System;
using System.Collections.Generic;
using System.Linq;

namespace Blam.Halo3.Tags;

// Inside the namespace so these win over s&box's global vector types when this compiles into the mount.
using Vector3 = System.Numerics.Vector3;
using Vector4 = System.Numerics.Vector4;

// A structure BSP cluster's bounds and its scenario atmosphere and camera fx palette indices (-1 none).
public sealed record AtmosphereCluster( int Bsp, int Cluster, Vector3 Min, Vector3 Max, int Atmosphere, int CameraFx )
{
	public bool Contains( Vector3 p ) => p.X >= Min.X && p.Y >= Min.Y && p.Z >= Min.Z && p.X <= Max.X && p.Y <= Max.Y && p.Z <= Max.Z;

	public float Volume => MathF.Max( Max.X - Min.X, 0 ) * MathF.Max( Max.Y - Min.Y, 0 ) * MathF.Max( Max.Z - Min.Z, 0 );
}

// A skya atmosphere setting's name and weather effect ("particle systems from this effect follow the camera and wrap").
public sealed record AtmosphereWeather( int Index, string Name, int Flags, TagEntry Effect );

// A convex volume kept free of weather (sbsp +0xAC, 0x1C): bounding sphere and planes (normal, distance), inside = behind every plane.
public sealed record WeatherPolyhedron( Vector3 Center, float Radius, IReadOnlyList<Vector4> Planes )
{
	public bool Contains( Vector3 p )
	{
		if ( Vector3.DistanceSquared( p, Center ) >= Radius * Radius )
			return false;
		foreach ( var plane in Planes )
		{
			if ( plane.X * p.X + plane.Y * p.Y + plane.Z * p.Z - plane.W > 1e-4f )
				return false;
		}
		return true;
	}
}

// Which atmosphere (and so weather) and camera fx each cluster of a map's scene BSPs uses: scnr +0x508 cluster data (one per BSP, by index; +0x50 atmosphere, +0x5C camera fx, i16 per cluster in 4-byte elements) → scnr +0x4F0 atmosphere palette (sid, i16 skya setting) → skya +0x34 settings (+0x8C weather effect); scnr +0x4FC camera fx palette (lighting.md §1).
public sealed class ClusterAtmospherics
{
	public IReadOnlyList<AtmosphereCluster> Clusters { get; }
	public IReadOnlyList<(string Name, int Setting)> AtmospherePalette { get; }
	public IReadOnlyList<AtmosphereWeather> Settings { get; }
	public IReadOnlyList<CameraFxPaletteEntry> CameraFxPalette { get; }
	public IReadOnlyList<WeatherPolyhedron> Polyhedra { get; }

	// Scene BSPs whose cluster data doesn't have one entry per cluster.
	public int Mismatched { get; }

	public ClusterAtmospherics( CacheFile cache )
	{
		var scenario = cache.Scenario.Data;
		AtmospherePalette = scenario.Block( 0x4F0, 8 ).Select( e => (e.StringId( 0 ), (int)e.I16( 4 )) ).ToArray();
		CameraFxPalette = CameraFxPaletteEntry.Read( cache );

		var skya = scenario.Reference( 0x680 );
		Settings = skya is null || skya.Data.IsNull
			? []
			: skya.Data.Block( 0x34, 0xA4 ).Select( ( s, i ) => new AtmosphereWeather( i, s.StringId( 4 ), s.U16( 0 ), s.Reference( 0x8C ) ) ).ToArray();

		var clusterData = scenario.Block( 0x508, 0x68 );
		var clusters = new List<AtmosphereCluster>();
		var polyhedra = new List<WeatherPolyhedron>();
		foreach ( var bsp in new ScenarioStructureBsps( cache ).SceneBsps )
		{
			if ( bsp.Bsp is null || bsp.Bsp.Data.IsNull )
				continue;

			// The cluster data's BSP reference is null in MCC's maps: its elements go by BSP index.
			var data = bsp.Index < clusterData.Count ? clusterData[bsp.Index] : default;
			var atmosphere = data.IsNull ? [] : data.Block( 0x50, 4 );
			var cameraFx = data.IsNull ? [] : data.Block( 0x5C, 4 );
			var sbsp = new StructureBspTag( bsp.Bsp );
			foreach ( var c in sbsp.Clusters )
			{
				var a = c.Index < atmosphere.Count ? atmosphere[c.Index].I16( 0 ) : -1;
				var f = c.Index < cameraFx.Count ? cameraFx[c.Index].I16( 0 ) : -1;
				clusters.Add( new AtmosphereCluster( bsp.Index, c.Index, c.Min, c.Max, a, f ) );
			}
			if ( atmosphere.Count != sbsp.Clusters.Count )
				Mismatched++;

			foreach ( var p in bsp.Bsp.Data.Block( 0xAC, 0x1C ) )
				polyhedra.Add( new WeatherPolyhedron( p.Vec3( 0 ), p.F32( 0xC ), p.Block( 0x10, 0x10 ).Select( q => q.Vec4( 0 ) ).ToArray() ) );
		}
		Clusters = clusters;
		Polyhedra = polyhedra;
	}

	// The smallest cluster box holding a point (Halo world units), or null outside them all.
	public AtmosphereCluster Locate( Vector3 p )
	{
		AtmosphereCluster best = null;
		foreach ( var c in Clusters )
		{
			if ( c.Contains( p ) && (best is null || c.Volume < best.Volume) )
				best = c;
		}
		return best;
	}

	public AtmosphereWeather SettingOf( AtmosphereCluster cluster )
	{
		if ( cluster is null || cluster.Atmosphere < 0 || cluster.Atmosphere >= AtmospherePalette.Count )
			return null;
		var setting = AtmospherePalette[cluster.Atmosphere].Setting;
		return setting >= 0 && setting < Settings.Count ? Settings[setting] : null;
	}

	public TagEntry WeatherOf( AtmosphereCluster cluster ) => SettingOf( cluster )?.Effect is { } e && !e.Data.IsNull ? e : null;

	public CameraFxPaletteEntry CameraFxOf( AtmosphereCluster cluster ) =>
		cluster is null || cluster.CameraFx < 0 || cluster.CameraFx >= CameraFxPalette.Count ? null : CameraFxPalette[cluster.CameraFx];

	// Every weather effect some cluster uses.
	public IEnumerable<TagEntry> WeatherEffects => Clusters.Select( WeatherOf ).Where( e => e is not null ).DistinctBy( e => e.Id );
}
