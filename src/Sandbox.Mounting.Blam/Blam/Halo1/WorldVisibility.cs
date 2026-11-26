using System;
using System.Collections.Generic;
using System.Linq;
using Blam.Halo1.Tags;
using Blam.Visibility;

namespace Blam.Halo1;

// Inside the namespace so it wins over s&box's global Vector3 when this compiles into the mount.
using Vector3 = System.Numerics.Vector3;

// A Halo CE map's clusters as Source 2 visibility: each BSP's clusters numbered on from the last BSP's, a box's clusters found by walking the collision BSP, and each cluster seeing what Halo's potentially visible set says.
public static class WorldVisibility
{
	// Source 2's default cell is 32 inches; Halo's clusters are rooms and stretches of ground, so cells a few times bigger lose nothing.
	public const float GridSize = 128;

	sealed class Space( IReadOnlyList<(BspClusterTree Tree, int First)> bsps, float scale ) : IClusterSpace
	{
		readonly Stack<int> _nodes = new();

		public bool Reach( Vector3 min, Vector3 max, ClusterSet clusters )
		{
			var center = (min + max) * 0.5f / scale;
			var extent = (max - min) * 0.5f / scale;
			var mixed = false;
			foreach ( var (tree, first) in bsps )
			{
				if ( tree.Nodes.Count == 0 )
					continue;

				var before = clusters.Count;
				_nodes.Clear();
				_nodes.Push( 0 );
				while ( _nodes.Count > 0 )
				{
					var n = tree.Nodes[_nodes.Pop()];
					if ( n.Plane < 0 || n.Plane >= tree.Planes.Count )
						continue;

					var plane = tree.Planes[n.Plane];
					var d = plane.X * center.X + plane.Y * center.Y + plane.Z * center.Z - plane.W;
					var r = MathF.Abs( plane.X ) * extent.X + MathF.Abs( plane.Y ) * extent.Y + MathF.Abs( plane.Z ) * extent.Z;
					if ( d + r >= 0 )
						Visit( tree, n.Front, first, clusters );
					if ( d - r < 0 )
						Visit( tree, n.Back, first, clusters );
				}

				// Only one BSP's own clusters can be told apart by splitting the box.
				mixed |= clusters.Count - before > 1;
			}
			return mixed;
		}

		void Visit( BspClusterTree tree, int child, int first, ClusterSet clusters )
		{
			if ( child == -1 )
				return;

			if ( (child & 0x80000000) != 0 )
			{
				var leaf = child & 0x7FFFFFFF;
				if ( leaf < tree.LeafClusters.Count && tree.LeafClusters[leaf] is var cluster && cluster >= 0 )
					clusters.Add( first + cluster + 1 );
				return;
			}

			if ( child < tree.Nodes.Count )
				_nodes.Push( child );
		}
	}

	// The visibility resource for the map's BSPs (scale: s&box units per world unit).
	public static byte[] Build( IReadOnlyList<BspVisibility> bsps, float scale, out string stats )
	{
		var space = new List<(BspClusterTree, int)>();
		var owner = new List<(BspVisibility Bsp, int Cluster, int First)>();
		var min = new Vector3( float.MaxValue );
		var max = new Vector3( float.MinValue );
		foreach ( var bsp in bsps )
		{
			var firstOfBsp = owner.Count;
			space.Add( (bsp.Tree, firstOfBsp) );
			for ( var c = 0; c < bsp.Clusters.Count; c++ )
			{
				owner.Add( (bsp, c, firstOfBsp) );
				foreach ( var sub in bsp.Clusters[c].Subclusters )
				{
					min = Vector3.Min( min, sub.Min );
					max = Vector3.Max( max, sub.Max );
				}
			}
		}

		if ( owner.Count == 0 || min.X > max.X )
		{
			stats = "no clusters";
			return null;
		}

		IEnumerable<int> Sees( int id )
		{
			var (bsp, cluster, first) = owner[id - 1];
			for ( var other = 0; other < bsp.Clusters.Count; other++ )
			{
				if ( bsp.Sees( cluster, other ) )
					yield return first + other + 1;
			}
		}

		return VoxelVisibilityBuilder.Build( new Space( space, scale ), min * scale, max * scale, GridSize, owner.Count, Sees, out stats );
	}
}
