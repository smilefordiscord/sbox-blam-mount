using System;
using System.Collections.Generic;
using System.Linq;
using Blam.Halo2.Tags;
using Blam.Visibility;

namespace Blam.Halo2;

// Inside the namespace so it wins over s&box's global Vector3 when this compiles into the mount.
using Vector3 = System.Numerics.Vector3;

// A Halo 2 map's clusters as Source 2 visibility: boxes classified by the collision BSP and its leaves' clusters, and what each cluster sees worked out from the cluster portals (Halo 2 keeps no visible set of its own).
public static class WorldVisibility
{
	public const float GridSize = 128;

	sealed class Space( IReadOnlyList<(CollisionBsp Tree, IReadOnlyList<int> Leaves, int First)> bsps, float scale ) : IClusterSpace
	{
		readonly Stack<int> _nodes = new();

		public bool Reach( Vector3 min, Vector3 max, ClusterSet clusters )
		{
			var center = (min + max) * 0.5f / scale;
			var extent = (max - min) * 0.5f / scale;
			var mixed = false;
			foreach ( var (tree, leaves, first) in bsps )
			{
				var nodes = tree.Nodes;
				if ( nodes.Count == 0 )
					continue;

				var before = clusters.Count;
				_nodes.Clear();
				_nodes.Push( 0 );
				while ( _nodes.Count > 0 )
				{
					var (plane, back, front) = nodes[_nodes.Pop()];
					if ( plane < 0 || plane >= tree.Planes.Count )
						continue;

					var (normal, distance) = tree.Planes[plane];
					var d = Vector3.Dot( normal, center ) - distance;
					var r = MathF.Abs( normal.X ) * extent.X + MathF.Abs( normal.Y ) * extent.Y + MathF.Abs( normal.Z ) * extent.Z;
					if ( d + r >= 0 )
						Visit( front, nodes.Count, leaves, first, clusters );
					if ( d - r < 0 )
						Visit( back, nodes.Count, leaves, first, clusters );
				}
				mixed |= clusters.Count - before > 1;
			}
			return mixed;
		}

		void Visit( int child, int nodeCount, IReadOnlyList<int> leaves, int first, ClusterSet clusters )
		{
			if ( child == -1 )
				return;
			if ( child < 0 )
			{
				var leaf = child & 0x7FFFFF;
				if ( leaf < leaves.Count && leaves[leaf] >= 0 )
					clusters.Add( first + leaves[leaf] + 1 );
				return;
			}
			if ( child < nodeCount )
				_nodes.Push( child );
		}
	}

	// The cluster a point is in (world units), or -1: for checking the portals' sides.
	public static int ClusterAt( StructureBspTag bsp, Vector3 point )
	{
		var tree = bsp.Collision;
		if ( tree is null || tree.Nodes.Count == 0 )
			return -1;
		var child = 0;
		for ( var guard = 0; guard <= tree.Nodes.Count && child >= 0 && child < tree.Nodes.Count; guard++ )
		{
			var (plane, back, front) = tree.Nodes[child];
			var (normal, distance) = plane >= 0 && plane < tree.Planes.Count ? tree.Planes[plane] : (Vector3.UnitZ, 0f);
			child = Vector3.Dot( normal, point ) - distance >= 0 ? front : back;
		}
		if ( child == -1 || child >= 0 )
			return -1;
		var leaf = child & 0x7FFFFF;
		return leaf < bsp.LeafClusters.Count ? bsp.LeafClusters[leaf] : -1;
	}

	// A BSP's portals in s&box units, each plane turned to face its front cluster.
	public static List<ClusterPortal> Portals( StructureBspTag bsp, float scale )
	{
		var portals = new List<ClusterPortal>();
		foreach ( var (back, front, _, points) in bsp.Portals )
		{
			if ( points.Length < 3 )
				continue;

			// The polygon's own plane (Newell), turned so the front cluster's side is in front.
			var normal = Vector3.Zero;
			for ( var i = 0; i < points.Length; i++ )
			{
				var a = points[i];
				var b = points[(i + 1) % points.Length];
				normal += new Vector3( (a.Y - b.Y) * (a.Z + b.Z), (a.Z - b.Z) * (a.X + b.X), (a.X - b.X) * (a.Y + b.Y) );
			}
			if ( normal.LengthSquared() < 1e-12f )
				continue;
			normal = Vector3.Normalize( normal );
			var centroid = points.Aggregate( Vector3.Zero, ( s, p ) => s + p ) / points.Length;
			if ( FacesBack( bsp, back, front, centroid, normal, points ) )
				normal = -normal;

			var scaled = points.Select( p => p * scale ).ToArray();
			portals.Add( new ClusterPortal( back, front, normal, Vector3.Dot( normal, centroid * scale ), scaled ) );
		}
		return portals;
	}

	// Whether the front cluster lies behind the polygon's normal: asked of the collision tree a little either side of points across the portal, the centroid first.
	static bool FacesBack( StructureBspTag bsp, int back, int front, Vector3 centroid, Vector3 normal, Vector3[] points )
	{
		var samples = new List<Vector3> { centroid };
		foreach ( var p in points )
		{
			samples.Add( Vector3.Lerp( centroid, p, 0.5f ) );
			samples.Add( Vector3.Lerp( centroid, p, 0.9f ) );
		}
		var votes = 0;
		foreach ( var sample in samples )
		{
			foreach ( var step in new[] { 0.01f, 0.05f, 0.2f, 0.5f } )
			{
				var ahead = ClusterAt( bsp, sample + normal * step );
				var behind = ClusterAt( bsp, sample - normal * step );
				if ( ahead == front || behind == back )
					votes++;
				else if ( ahead == back || behind == front )
					votes--;
			}
		}
		return votes < 0;
	}

	// Whether a portal's side could be told from the tree.
	public static bool Sided( StructureBspTag bsp, int back, int front, Vector3 centroid, Vector3 normal )
	{
		foreach ( var step in new[] { 0.01f, 0.05f, 0.2f, 0.5f } )
		{
			var ahead = ClusterAt( bsp, centroid + normal * step );
			var behind = ClusterAt( bsp, centroid - normal * step );
			if ( ahead == front || behind == back || ahead == back || behind == front )
				return true;
		}
		return false;
	}

	public static byte[] Build( IReadOnlyList<StructureBspTag> bsps, float scale, out string stats )
	{
		var space = new List<(CollisionBsp, IReadOnlyList<int>, int)>();
		var rows = new List<bool[]>();
		var firsts = new List<int>();
		var min = new Vector3( float.MaxValue );
		var max = new Vector3( float.MinValue );
		var solved = new List<string>();
		foreach ( var bsp in bsps )
		{
			var first = rows.Count;
			firsts.Add( first );
			var count = bsp.ClusterBoxes.Count;
			if ( bsp.Collision is not null )
				space.Add( (bsp.Collision, bsp.LeafClusters, first) );
			foreach ( var (lo, hi) in bsp.ClusterBoxes )
			{
				min = Vector3.Min( min, lo );
				max = Vector3.Max( max, hi );
			}

			// Bungie's visible set, and what the portals show on top of it, so neither culls what the other sees.
			var bspRows = PortalFlow.Solve( count, Portals( bsp, scale ), out var flowStats );
			var stored = bsp.StoredVisibility();
			if ( stored is not null )
			{
				for ( var c = 0; c < count; c++ )
				{
					for ( var o = 0; o < count; o++ )
						bspRows[c][o] |= stored[c][o];
				}
			}
			solved.Add( $"{flowStats}{(stored is null ? ", no stored set" : "")}" );
			rows.AddRange( bspRows );
		}

		if ( rows.Count == 0 || min.X > max.X )
		{
			stats = "no clusters";
			return null;
		}

		var owner = new List<(int Bsp, int Cluster)>();
		for ( var b = 0; b < bsps.Count; b++ )
		{
			for ( var c = 0; c < bsps[b].ClusterBoxes.Count; c++ )
				owner.Add( (b, c) );
		}

		IEnumerable<int> Sees( int id )
		{
			var (b, c) = owner[id - 1];
			var row = rows[id - 1];
			for ( var other = 0; other < row.Length; other++ )
			{
				if ( row[other] )
					yield return firsts[b] + other + 1;
			}
		}

		var result = VoxelVisibilityBuilder.Build( new Space( space, scale ), min * scale, max * scale, GridSize, rows.Count, Sees, out var built );
		stats = $"{built}; {string.Join( "; ", solved )}";
		return result;
	}
}
