using System.Collections.Generic;
using System.IO;
using Blam.IO;

namespace Blam.Halo1.Tags;

// Inside the namespace so these win over s&box's global Vector3/Vector4 when this compiles into the mount.
using Vector3 = System.Numerics.Vector3;
using Vector4 = System.Numerics.Vector4;

/// <summary>
/// A node of a BSP's collision tree: a point on the plane's front side (dot( normal, p ) &gt;= w)
/// goes to <paramref name="Front"/>, else to <paramref name="Back"/>. A child with its top bit set
/// is a leaf index; -1 is outside the level.
/// </summary>
public readonly record struct BspTreeNode( int Plane, int Back, int Front );

/// <summary>
/// A structure BSP's collision tree and the cluster each of its leaves belongs to: what Halo
/// looks a point's cluster up with (the camera's, to pick the fog plane that fogs the view).
/// </summary>
public sealed record BspClusterTree( IReadOnlyList<BspTreeNode> Nodes, IReadOnlyList<Vector4> Planes, IReadOnlyList<int> LeafClusters )
{
	/// <summary>
	/// The cluster containing a point (world units), or -1 if it's outside the level.
	/// </summary>
	public int FindCluster( Vector3 point )
	{
		if ( Nodes.Count == 0 )
			return -1;

		var node = 0;
		for ( var guard = 0; guard < Nodes.Count; guard++ )
		{
			var n = Nodes[node];
			if ( n.Plane < 0 || n.Plane >= Planes.Count )
				return -1;

			var plane = Planes[n.Plane];
			var child = Vector3.Dot( new Vector3( plane.X, plane.Y, plane.Z ), point ) >= plane.W ? n.Front : n.Back;
			if ( child == -1 )
				return -1;

			if ( (child & 0x80000000) != 0 )
			{
				var leaf = child & 0x7FFFFFFF;
				return leaf < LeafClusters.Count ? LeafClusters[leaf] : -1;
			}

			if ( child >= Nodes.Count )
				return -1;

			node = child;
		}

		return -1;
	}
}

partial class StructureBspTag
{
	/// <summary>
	/// Reads a BSP's collision tree and its leaves' clusters, from the BSP's own data.
	/// </summary>
	/// <remarks>
	/// Offsets relative to the tag: collision BSP 0xB0 (the first: 3D nodes 0x0 of 12 bytes:
	/// plane, back child, front child; planes 0xC of 16 bytes), leaves 0xE0 (16 bytes, cluster
	/// at 0x8; the collision BSP's leaf indices).
	/// </remarks>
	public static BspClusterTree ReadClusterTree( CacheFile cache, StructureBspReference reference )
	{
		var data = new MemoryRegion( cache.Read( reference.FileOffset, reference.Size ), reference.Address );
		if ( data.U32( reference.Address + 0x14 ) != SbspSignature )
			throw new InvalidDataException( $"{reference.Tag} has no 'sbsp' signature in its header" );

		var tag = data.U32( reference.Address );
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

		return new BspClusterTree( nodes, planes, leaves );
	}
}
