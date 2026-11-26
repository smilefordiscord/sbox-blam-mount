using System;
using System.Collections.Generic;
using System.IO;
using Blam.IO;

namespace Blam.Halo1.Tags;

// Inside the namespace so these win over s&box's global Vector3 when this compiles into the mount.
using Vector3 = System.Numerics.Vector3;
using Vector4 = System.Numerics.Vector4;

/// <summary>
/// A lens flare placed in the level geometry, usually on a light fixture that's part of the BSP.
/// </summary>
/// <param name="Direction">The way the light faces, or zero.</param>
public sealed record BspLensFlareMarker( Vector3 Position, Vector3 Direction, TagEntry LensFlare );

/// <summary>
/// A volume where weather doesn't fall, such as under an overhang: inside every plane.
/// </summary>
/// <param name="Planes">Plane normal and distance; the normals face inward, so points inside have dot( normal, p ) &gt;= distance for all of them.</param>
public sealed record WeatherPolyhedron( Vector3 Center, float Radius, IReadOnlyList<Vector4> Planes );

/// <summary>
/// One entry of a BSP's weather palette: a weather particle system and the wind.
/// </summary>
public sealed record BspWeather( string Name, TagEntry ParticleSystem, TagEntry Wind, Vector3 WindDirection, float WindMagnitude );

/// <summary>
/// Reader for the parts of <c>sbsp</c> tags that place effects: lens flare markers, the weather
/// palette, which clusters have weather, and the collision BSP's node tree for finding the
/// cluster a point is in.
/// </summary>
/// <remarks>
/// Offsets, relative to the tag in the BSP's own region (see <see cref="StructureBspTag"/>):
/// collision BSP 0xB0 (first element: 3D nodes 0x0 of 12 bytes: plane, back child, front child;
/// planes 0xC of 16 bytes), leaves 0xE0 (16 bytes, cluster at 0x8; same indices as the collision
/// BSP's leaves), lens flares 0x11C (tag references), lens flare markers 0x128 (16 bytes:
/// position, direction as three signed bytes , lens flare index), clusters 0x134
/// (104 bytes, weather index at 0x8), weather palette 0x1B4 (240 bytes: name, particle system
/// 0x20, wind 0x80, wind direction 0x90, magnitude 0x9C), weather polyhedra 0x1C0 (32 bytes:
/// bounding sphere center and radius, planes block 0x14).
///
/// A 3D node child with the top bit set is a leaf index; -1 is outside the level.
/// </remarks>
public sealed class StructureBspEffects
{
	const uint SbspSignature = 0x73627370; // 'sbsp'

	readonly MemoryRegion _region;
	readonly uint _nodes;
	readonly int _nodeCount;
	readonly uint _planes;
	readonly int _planeCount;
	readonly short[] _leafClusters;

	public IReadOnlyList<BspLensFlareMarker> LensFlareMarkers { get; }
	public IReadOnlyList<BspWeather> WeatherPalette { get; }

	/// <summary>
	/// Weather palette index of each cluster, or -1.
	/// </summary>
	public IReadOnlyList<int> ClusterWeather { get; }
	public IReadOnlyList<WeatherPolyhedron> WeatherPolyhedra { get; }
	public Vector3 WorldBoundsMin { get; }
	public Vector3 WorldBoundsMax { get; }

	public StructureBspEffects( CacheFile cache, StructureBspReference reference )
	{
		var region = new MemoryRegion( cache.Read( reference.FileOffset, reference.Size ), reference.Address );
		if ( region.U32( reference.Address + 0x14 ) != SbspSignature )
			throw new InvalidDataException( $"{reference.Tag} has no 'sbsp' signature in its header" );

		_region = region;
		var tag = region.U32( reference.Address );

		WorldBoundsMin = new Vector3( region.F32( tag + 0xC8 ), region.F32( tag + 0xD0 ), region.F32( tag + 0xD8 ) );
		WorldBoundsMax = new Vector3( region.F32( tag + 0xCC ), region.F32( tag + 0xD4 ), region.F32( tag + 0xDC ) );

		var palette = new List<TagEntry>();
		foreach ( var entry in region.Block( tag + 0x11C, 0x10 ) )
			palette.Add( cache.GetTagReference( region, entry ) );

		var markers = new List<BspLensFlareMarker>();
		foreach ( var entry in region.Block( tag + 0x128, 0x10 ) )
		{
			var index = (sbyte)region.U8( entry + 0xF );
			if ( index < 0 || index >= palette.Count || palette[index] is null )
				continue;

			var direction = new Vector3( (sbyte)region.U8( entry + 0xC ), (sbyte)region.U8( entry + 0xD ), (sbyte)region.U8( entry + 0xE ) );
			direction = direction == Vector3.Zero ? direction : Vector3.Normalize( direction );
			markers.Add( new BspLensFlareMarker( region.Vec3( entry ), direction, palette[index] ) );
		}
		LensFlareMarkers = markers;

		var weather = new List<BspWeather>();
		foreach ( var entry in region.Block( tag + 0x1B4, 0xF0 ) )
		{
			weather.Add( new BspWeather(
				Name: region.CString( entry, 32 ),
				ParticleSystem: cache.GetTagReference( region, entry + 0x20 ),
				Wind: cache.GetTagReference( region, entry + 0x80 ),
				WindDirection: region.Vec3( entry + 0x90 ),
				WindMagnitude: region.F32( entry + 0x9C ) ) );
		}
		WeatherPalette = weather;

		var clusters = new List<int>();
		foreach ( var entry in region.Block( tag + 0x134, 0x68 ) )
			clusters.Add( region.I16( entry + 0x8 ) );
		ClusterWeather = clusters;

		var polyhedra = new List<WeatherPolyhedron>();
		foreach ( var entry in region.Block( tag + 0x1C0, 0x20 ) )
		{
			var planes = new List<Vector4>();
			foreach ( var plane in region.Block( entry + 0x14, 0x10 ) )
				planes.Add( new Vector4( region.Vec3( plane ), region.F32( plane + 0xC ) ) );
			polyhedra.Add( new WeatherPolyhedron( region.Vec3( entry ), region.F32( entry + 0xC ), planes ) );
		}
		WeatherPolyhedra = polyhedra;

		var leaves = new List<short>();
		foreach ( var entry in region.Block( tag + 0xE0, 0x10 ) )
			leaves.Add( region.I16( entry + 0x8 ) );
		_leafClusters = leaves.ToArray();

		// The first (only) collision BSP.
		if ( region.I32( tag + 0xB0 ) > 0 )
		{
			var bsp = region.U32( tag + 0xB4 );
			_nodeCount = region.I32( bsp );
			_nodes = region.U32( bsp + 0x4 );
			_planeCount = region.I32( bsp + 0xC );
			_planes = region.U32( bsp + 0x10 );
		}
	}

	/// <summary>
	/// The cluster containing a point (world units), or -1 if it's outside the level or inside
	/// solid geometry.
	/// </summary>
	public int FindCluster( Vector3 point )
	{
		if ( _nodeCount == 0 )
			return -1;

		var node = 0;
		for ( var guard = 0; guard < 4096; guard++ )
		{
			var address = _nodes + (uint)(node * 0xC);
			var planeIndex = _region.I32( address );
			if ( planeIndex < 0 || planeIndex >= _planeCount )
				return -1;

			var plane = _planes + (uint)(planeIndex * 0x10);
			var side = Vector3.Dot( _region.Vec3( plane ), point ) - _region.F32( plane + 0xC );
			var child = _region.I32( address + (side >= 0 ? 0x8u : 0x4u) );

			if ( child == -1 )
				return -1;

			if ( (child & 0x80000000) != 0 )
			{
				var leaf = child & 0x7FFFFFFF;
				return leaf < _leafClusters.Length ? _leafClusters[leaf] : -1;
			}

			if ( child >= _nodeCount )
				return -1;

			node = child;
		}

		return -1;
	}

	/// <summary>
	/// The weather palette index at a point, or -1 for none: the point's cluster's weather,
	/// unless a weather polyhedron keeps it out.
	/// </summary>
	public int FindWeather( Vector3 point )
	{
		var cluster = FindCluster( point );
		if ( cluster < 0 || cluster >= ClusterWeather.Count )
			return -1;

		var weather = ClusterWeather[cluster];
		if ( weather < 0 )
			return -1;

		foreach ( var polyhedron in WeatherPolyhedra )
		{
			if ( Vector3.DistanceSquared( point, polyhedron.Center ) > polyhedron.Radius * polyhedron.Radius )
				continue;

			var inside = true;
			foreach ( var plane in polyhedron.Planes )
			{
				if ( Vector3.Dot( new Vector3( plane.X, plane.Y, plane.Z ), point ) < plane.W )
				{
					inside = false;
					break;
				}
			}

			if ( inside )
				return -1;
		}

		return weather;
	}
}
