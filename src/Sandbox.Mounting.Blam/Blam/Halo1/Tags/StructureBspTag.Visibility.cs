using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace Blam.Halo1.Tags;

// Inside the namespace so these win over s&box's global types when this compiles into the mount.
using Vector3 = System.Numerics.Vector3;
using Vector4 = System.Numerics.Vector4;

/// <summary>
/// Part of a cluster: an axis-aligned box and the structure surfaces (triangles) inside it.
/// Halo draws a subcluster's surfaces when its box is in the view through the cluster's portals.
/// </summary>
public sealed record BspSubcluster( Vector3 Min, Vector3 Max, int[] Surfaces );

/// <summary>
/// A cluster of a structure BSP: a region of the level joined to its neighbours by portals.
/// </summary>
public sealed record BspCluster( short[] Portals, BspSubcluster[] Subclusters );

/// <summary>
/// An opening between two clusters: a convex polygon on a collision BSP plane.
/// </summary>
/// <param name="Plane">Normal and distance (dot( normal, p ) = distance on it); null if the plane index is bad.</param>
public sealed record BspPortal( int FrontCluster, int BackCluster, Vector4? Plane, Vector3 Centroid, float Radius, Vector3[] Vertices );

/// <summary>
/// What Halo works out a structure BSP's visibility from: the collision tree that finds the
/// camera's leaf, each leaf's cluster, the clusters' portals and subclusters, and each cluster's
/// potentially visible set. In world units (Halo's).
/// </summary>
public sealed class BspVisibility
{
	public BspClusterTree Tree { get; init; }
	public IReadOnlyList<BspCluster> Clusters { get; init; }
	public IReadOnlyList<BspPortal> Portals { get; init; }

	/// <summary>One bit per cluster for each cluster, rows of <see cref="PvsWords"/> words.</summary>
	public uint[] Pvs { get; init; }
	public int PvsWords { get; init; }

	/// <summary>Whether a cluster's potentially visible set holds another cluster.</summary>
	public bool Sees( int from, int to )
	{
		var word = from * PvsWords + (to >> 5);
		return word < Pvs.Length && (Pvs[word] & (1u << (to & 31))) != 0;
	}
}

partial class StructureBspTag
{
	/// <summary>
	/// Reads the BSP's visibility data.
	/// </summary>
	/// <remarks>
	/// Offsets relative to the tag: collision BSP 0xB0 (3D nodes 0x0, planes 0xC), leaves 0xE0
	/// (16 bytes, cluster 0x8), clusters 0x134 (104 bytes: subclusters 0x34, portal indices 0x5C),
	/// cluster data 0x140 (the visible sets), cluster portals 0x154 (64 bytes: front and back
	/// cluster 0x0, plane 0x4, centroid 0x8, bounding radius 0x14, vertices 0x34). Subclusters are
	/// 36 bytes: bounds x, y, z as minimum and maximum pairs, surface indices 0x18 (32-bit).
	/// </remarks>
	public BspVisibility ReadVisibility()
	{
		var data = _region;
		var tag = _tag;

		var nodes = new List<BspTreeNode>();
		var planes = new List<Vector4>();
		if ( data.I32( tag + 0xB0 ) > 0 )
		{
			var bsp = data.U32( tag + 0xB4 );
			foreach ( var node in data.Block( bsp, 0xC ) )
				nodes.Add( new BspTreeNode( data.I32( node ), data.I32( node + 0x4 ), data.I32( node + 0x8 ) ) );
			foreach ( var plane in data.Block( bsp + 0xC, 0x10 ) )
				planes.Add( new Vector4( data.Vec3( plane ), data.F32( plane + 0xC ) ) );
		}

		var leaves = new List<int>();
		foreach ( var leaf in data.Block( tag + 0xE0, 0x10 ) )
			leaves.Add( data.I16( leaf + 0x8 ) );

		var clusters = new List<BspCluster>();
		foreach ( var cluster in data.Block( tag + 0x134, 0x68 ) )
		{
			var subclusters = new List<BspSubcluster>();
			foreach ( var subcluster in data.Block( cluster + 0x34, 0x24 ) )
			{
				var min = new Vector3( data.F32( subcluster ), data.F32( subcluster + 0x8 ), data.F32( subcluster + 0x10 ) );
				var max = new Vector3( data.F32( subcluster + 0x4 ), data.F32( subcluster + 0xC ), data.F32( subcluster + 0x14 ) );
				var surfaces = new List<int>();
				foreach ( var surface in data.Block( subcluster + 0x18, 0x4 ) )
					surfaces.Add( data.I32( surface ) );
				subclusters.Add( new BspSubcluster( min, max, surfaces.ToArray() ) );
			}

			var portals = new List<short>();
			foreach ( var portal in data.Block( cluster + 0x5C, 0x2 ) )
				portals.Add( data.I16( portal ) );

			clusters.Add( new BspCluster( portals.ToArray(), subclusters.ToArray() ) );
		}

		var clusterPortals = new List<BspPortal>();
		foreach ( var portal in data.Block( tag + 0x154, 0x40 ) )
		{
			var planeIndex = data.I32( portal + 0x4 );
			var vertices = new List<Vector3>();
			foreach ( var vertex in data.Block( portal + 0x34, 0xC ) )
				vertices.Add( data.Vec3( vertex ) );

			clusterPortals.Add( new BspPortal(
				FrontCluster: data.I16( portal ),
				BackCluster: data.I16( portal + 0x2 ),
				Plane: planeIndex >= 0 && planeIndex < planes.Count ? planes[planeIndex] : null,
				Centroid: data.Vec3( portal + 0x8 ),
				Radius: data.F32( portal + 0x14 ),
				Vertices: vertices.ToArray() ) );
		}

		// A cluster whose row the data doesn't hold whole sees nothing, as in Halo.
		var words = (clusters.Count + 31) / 32;
		var pvsSize = data.I32( tag + 0x140 );
		var pvsAddress = data.U32( tag + 0x14C );
		var pvs = new uint[clusters.Count * words];
		if ( words > 0 && pvsSize > 0 && data.Contains( pvsAddress, pvsSize ) )
		{
			var bytes = data.Slice( pvsAddress, pvsSize );
			var rows = Math.Min( clusters.Count, pvsSize / (words * 4) );
			for ( var i = 0; i < rows * words; i++ )
				pvs[i] = BinaryPrimitives.ReadUInt32LittleEndian( bytes.Slice( i * 4, 4 ) );
		}

		return new BspVisibility
		{
			Tree = new BspClusterTree( nodes, planes, leaves ),
			Clusters = clusters,
			Portals = clusterPortals,
			Pvs = pvs,
			PvsWords = words,
		};
	}
}
