using System;
using System.Collections.Generic;
using System.Linq;
using Blam.Halo3.Tags;
using Blam.Visibility;

namespace Blam.Halo3;

// Inside the namespace so it wins over s&box's global Vector3 when this compiles into the mount.
using Vector3 = System.Numerics.Vector3;

// A Halo 3 scene's clusters as Source 2 visibility: boxes classified by each BSP's collision tree and its leaves' clusters (sbsp +0x34), and what each cluster sees from the scenario's zone set potentially visible sets, every zone set's taken together (see ZoneSetVisibility), plus its portal neighbours.
public static class WorldVisibility
{
	public const float GridSize = 128;

	public sealed record Bsp( int Index, StructureBspTag Tag, CollisionBsp Tree );

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
					var index = plane & 0x7FFF;
					if ( index >= tree.Planes.Count )
						continue;

					var (normal, distance) = tree.Planes[index];
					if ( (plane & 0x8000) != 0 )
					{
						normal = -normal;
						distance = -distance;
					}
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

	static bool Finite( Vector3 v ) => float.IsFinite( v.X ) && float.IsFinite( v.Y ) && float.IsFinite( v.Z );

	// unpairedSeeEachOther: BSPs no zone set loads together see each other whole (the scene shows them all at once, Halo never both).
	public static byte[] Build( IReadOnlyList<Bsp> bsps, IReadOnlyList<ZoneSetVisibility> sets, float scale, out string stats, bool unpairedSeeEachOther = false )
	{
		var first = new Dictionary<int, int>();
		var space = new List<(CollisionBsp, IReadOnlyList<int>, int)>();
		var count = 0;
		var min = new Vector3( float.MaxValue );
		var max = new Vector3( float.MinValue );
		foreach ( var bsp in bsps )
		{
			first[bsp.Index] = count;
			if ( bsp.Tree is not null )
				space.Add( (bsp.Tree, bsp.Tag.LeafClusters, count) );
			// The BSP's world bounds; a sky cluster's box can be unbounded.
			if ( Finite( bsp.Tag.WorldMin ) && Finite( bsp.Tag.WorldMax ) && bsp.Tag.WorldMin.X <= bsp.Tag.WorldMax.X )
			{
				min = Vector3.Min( min, bsp.Tag.WorldMin );
				max = Vector3.Max( max, bsp.Tag.WorldMax );
			}
			else
			{
				foreach ( var cluster in bsp.Tag.Clusters.Where( c => Finite( c.Min ) && Finite( c.Max ) && (c.Max - c.Min).Length() < 1e5f ) )
				{
					min = Vector3.Min( min, cluster.Min );
					max = Vector3.Max( max, cluster.Max );
				}
			}
			count += bsp.Tag.Clusters.Count;
		}

		if ( count == 0 || min.X > max.X )
		{
			stats = "no clusters";
			return null;
		}

		var rows = new HashSet<int>[count + 1];
		for ( var i = 1; i <= count; i++ )
			rows[i] = new HashSet<int>();

		// Each set's BSPs are its mask's, in order of index; each cluster's bits go over those BSPs' clusters.
		var used = 0;
		var covered = new HashSet<int>();
		foreach ( var set in sets )
		{
			var order = Enumerable.Range( 0, 32 ).Where( b => (set.BspMask & 1u << b) != 0 ).ToList();
			if ( set.Sees.Count == 0 )
				continue;
			used++;
			for ( var k = 0; k < set.Sees.Count && k < order.Count; k++ )
			{
				if ( !first.TryGetValue( order[k], out var from ) )
					continue;
				var clusters = set.Sees[k];
				if ( clusters.Count > 0 )
					covered.Add( order[k] );
				for ( var c = 0; c < clusters.Count; c++ )
				{
					var id = from + c + 1;
					if ( id > count )
						continue;
					for ( var j = 0; j < clusters[c].Count && j < order.Count; j++ )
					{
						if ( !first.TryGetValue( order[j], out var to ) )
							continue;
						var bits = clusters[c][j];
						for ( var w = 0; w < bits.Length; w++ )
						{
							var word = bits[w];
							while ( word != 0 )
							{
								var bit = System.Numerics.BitOperations.TrailingZeroCount( word );
								word &= word - 1;
								var other = to + w * 32 + bit + 1;
								if ( other <= count )
									rows[id].Add( other );
							}
						}
					}
				}
			}
		}

		// Portal neighbours always see each other.
		foreach ( var bsp in bsps )
		{
			var f = first[bsp.Index];
			var n = bsp.Tag.Clusters.Count;
			foreach ( var (back, front, _) in bsp.Tag.Portals )
			{
				if ( back < 0 || front < 0 || back >= n || front >= n )
					continue;
				rows[f + back + 1].Add( f + front + 1 );
				rows[f + front + 1].Add( f + back + 1 );
			}
		}

		if ( unpairedSeeEachOther )
		{
			foreach ( var a in bsps )
			{
				foreach ( var b in bsps )
				{
					if ( a == b || sets.Any( z => (z.BspMask & 1u << a.Index) != 0 && (z.BspMask & 1u << b.Index) != 0 ) )
						continue;
					for ( var c = 0; c < a.Tag.Clusters.Count; c++ )
					{
						for ( var o = 0; o < b.Tag.Clusters.Count; o++ )
							rows[first[a.Index] + c + 1].Add( first[b.Index] + o + 1 );
					}
				}
			}
		}

		// A BSP no set covers sees everything.
		foreach ( var bsp in bsps.Where( b => !covered.Contains( b.Index ) ) )
		{
			for ( var c = 0; c < bsp.Tag.Clusters.Count; c++ )
			{
				var id = first[bsp.Index] + c + 1;
				for ( var o = 1; o <= count; o++ )
					rows[id].Add( o );
			}
		}

		// Seeing is mutual.
		for ( var i = 1; i <= count; i++ )
		{
			foreach ( var o in rows[i].ToList() )
				rows[o].Add( i );
		}

		var result = VoxelVisibilityBuilder.Build( new Space( space, scale ), min * scale, max * scale, GridSize, count, id => rows[id], out var built );
		var pairs = rows.Skip( 1 ).Sum( r => r.Count );
		stats = $"{built}; {used} zone set visible sets, {pairs * 100 / Math.Max( 1, count * count )}% of cluster pairs visible";
		return result;
	}
}
