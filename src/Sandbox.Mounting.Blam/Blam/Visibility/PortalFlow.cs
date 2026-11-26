using System;
using System.Collections.Generic;
using System.Linq;

namespace Blam.Visibility;

// Inside the namespace so it wins over s&box's global Vector3 when this compiles into the mount.
using Vector3 = System.Numerics.Vector3;

// An opening between two clusters: a convex polygon whose plane faces the front cluster (dot( Normal, p ) > Distance in front).
public sealed record ClusterPortal( int Back, int Front, Vector3 Normal, float Distance, Vector3[] Points );

// Works out which clusters can see which through their portals, as Quake's vis does: a flood of the portals each portal faces (the conservative "might see"), then a flow through the portals clipped by the planes separating the source portal from the portals it passes through.
// Halo 2 and 3 store no potentially visible set (Halo works it out per frame from the portals), so this makes one for Source 2's visibility.
public static class PortalFlow
{
	// Units are s&box's; Quake's epsilon in its own inches.
	const float Epsilon = 0.1f;

	sealed class Directed
	{
		public int Index;
		public int From;
		public int To;
		public Vector3 Normal;
		public float Distance;
		public Vector3[] Points;
		public ulong[] Flood;
		public ulong[] Visible;
		public bool[] Clusters;
		public bool Done;
	}

	sealed class Stack
	{
		public Stack Previous;
		public Directed Portal;
		public Vector3 Normal;
		public float Distance;
		public Vector3[] Source;
		public Vector3[] Pass;
		public ulong[] MightSee;
	}

	// Each cluster's row: the clusters it may see (itself always). budget is the most portal steps one portal's flow may take before it settles for its flood.
	public static bool[][] Solve( int clusterCount, IReadOnlyList<ClusterPortal> portals, out string stats, int budget = 200_000 )
	{
		var directed = new List<Directed>();
		foreach ( var p in portals )
		{
			if ( p.Points.Length < 3 || p.Back < 0 || p.Front < 0 || p.Back >= clusterCount || p.Front >= clusterCount || p.Back == p.Front )
				continue;

			// Leaving the back cluster the portal faces the front one; leaving the front it faces back.
			directed.Add( new Directed { From = p.Back, To = p.Front, Normal = p.Normal, Distance = p.Distance, Points = p.Points } );
			directed.Add( new Directed { From = p.Front, To = p.Back, Normal = -p.Normal, Distance = -p.Distance, Points = p.Points.Reverse().ToArray() } );
		}
		for ( var i = 0; i < directed.Count; i++ )
			directed[i].Index = i;

		var words = (directed.Count + 63) / 64;
		var leaving = new List<Directed>[clusterCount];
		for ( var c = 0; c < clusterCount; c++ )
			leaving[c] = new();
		foreach ( var d in directed )
			leaving[d.From].Add( d );

		// The portals each portal faces and is faced by, flooded through the clusters.
		foreach ( var p in directed )
		{
			var front = new bool[directed.Count];
			foreach ( var q in directed )
			{
				if ( q == p )
					continue;
				if ( q.Points.All( v => Vector3.Dot( p.Normal, v ) - p.Distance < Epsilon ) )
					continue;
				if ( p.Points.All( v => Vector3.Dot( q.Normal, v ) - q.Distance > -Epsilon ) )
					continue;
				front[q.Index] = true;
			}

			p.Flood = new ulong[words];
			var seen = new bool[clusterCount];
			var queue = new Queue<int>();
			queue.Enqueue( p.To );
			seen[p.To] = true;
			while ( queue.Count > 0 )
			{
				foreach ( var q in leaving[queue.Dequeue()] )
				{
					if ( !front[q.Index] || Has( p.Flood, q.Index ) )
						continue;
					Set( p.Flood, q.Index );
					if ( !seen[q.To] )
					{
						seen[q.To] = true;
						queue.Enqueue( q.To );
					}
				}
			}
		}

		// The flow, smallest floods first so later portals can stop at what finished ones see.
		var exhausted = 0;
		foreach ( var p in directed.OrderBy( d => Count( d.Flood ) ) )
		{
			p.Visible = new ulong[words];
			p.Clusters = new bool[clusterCount];
			var steps = 0;
			var head = new Stack { Portal = p, Normal = p.Normal, Distance = p.Distance, Source = p.Points, MightSee = p.Flood };
			if ( !Flow( p, p.To, head, leaving, words, ref steps, budget ) )
			{
				// Too much to follow: everything the flood reaches.
				exhausted++;
				Array.Copy( p.Flood, p.Visible, words );
				Array.Clear( p.Clusters );
				p.Clusters[p.To] = true;
				foreach ( var q in directed )
				{
					if ( Has( p.Flood, q.Index ) )
						p.Clusters[q.To] = true;
				}
			}
			p.Done = true;
		}

		var rows = new bool[clusterCount][];
		for ( var c = 0; c < clusterCount; c++ )
		{
			rows[c] = new bool[clusterCount];
			rows[c][c] = true;
			foreach ( var p in leaving[c] )
			{
				for ( var o = 0; o < clusterCount; o++ )
					rows[c][o] |= p.Clusters[o];
			}
		}

		// Seeing is mutual.
		for ( var a = 0; a < clusterCount; a++ )
		{
			for ( var b = a + 1; b < clusterCount; b++ )
			{
				if ( rows[a][b] || rows[b][a] )
					rows[a][b] = rows[b][a] = true;
			}
		}

		var pairs = rows.Sum( r => r.Count( x => x ) );
		stats = $"{portals.Count} portals, {pairs * 100 / Math.Max( 1, clusterCount * clusterCount )}% of cluster pairs visible, {exhausted} portals over budget";
		return rows;
	}

	// Marks what the base portal sees from a cluster on, through each portal still possibly seen; false once over budget.
	static bool Flow( Directed baseline, int cluster, Stack previous, List<Directed>[] leaving, int words, ref int steps, int budget )
	{
		baseline.Clusters[cluster] = true;
		if ( ++steps > budget )
			return false;

		foreach ( var p in leaving[cluster] )
		{
			if ( !Has( previous.MightSee, p.Index ) )
				continue;

			// What's left to see through it: what this path might see and that portal might (or does, once done).
			var test = p.Done ? p.Visible : p.Flood;
			var might = new ulong[words];
			var more = false;
			for ( var w = 0; w < words; w++ )
			{
				might[w] = previous.MightSee[w] & test[w];
				more |= (might[w] & ~baseline.Visible[w]) != 0;
			}
			if ( !more && Has( baseline.Visible, p.Index ) )
				continue;

			var head = HeadOf( previous );
			var pass = Clip( p.Points, head.Normal, head.Distance );
			if ( pass is null )
				continue;
			var source = Clip( previous.Source, -p.Normal, -p.Distance );
			if ( source is null )
				continue;

			var stack = new Stack { Previous = previous, Portal = p, Normal = p.Normal, Distance = p.Distance, Source = source, Pass = pass, MightSee = might };
			Set( baseline.Visible, p.Index );

			if ( previous.Pass is not null )
			{
				pass = Clip( pass, previous.Normal, previous.Distance );
				if ( pass is null )
					continue;
				pass = ClipToSeparators( source, previous.Pass, pass, false );
				if ( pass is null )
					continue;
				pass = ClipToSeparators( previous.Pass, source, pass, true );
				if ( pass is null )
					continue;
				stack.Pass = pass;
			}

			if ( !Flow( baseline, p.To, stack, leaving, words, ref steps, budget ) )
				return false;
		}
		return true;
	}

	static Stack HeadOf( Stack stack )
	{
		while ( stack.Previous is not null )
			stack = stack.Previous;
		return stack;
	}

	// Clips the target to the side of each plane through an edge of the source and a corner of the pass that has the pass on one side and the source on the other.
	static Vector3[] ClipToSeparators( Vector3[] source, Vector3[] pass, Vector3[] target, bool flipClip )
	{
		for ( var i = 0; i < source.Length; i++ )
		{
			var l = (i + 1) % source.Length;
			var v1 = source[l] - source[i];
			for ( var j = 0; j < pass.Length; j++ )
			{
				var v2 = pass[j] - source[i];
				var normal = Vector3.Cross( v1, v2 );
				var length = normal.Length();
				if ( length < Epsilon )
					continue;
				normal /= length;
				var distance = Vector3.Dot( pass[j], normal );

				// Which side the source is on.
				var flip = false;
				var k = 0;
				for ( ; k < source.Length; k++ )
				{
					if ( k == i || k == l )
						continue;
					var d = Vector3.Dot( source[k], normal ) - distance;
					if ( d < -Epsilon )
					{
						flip = false;
						break;
					}
					if ( d > Epsilon )
					{
						flip = true;
						break;
					}
				}
				if ( k == source.Length )
					continue;
				if ( flip )
				{
					normal = -normal;
					distance = -distance;
				}

				// A separating plane has the whole pass on its front.
				var front = 0;
				k = 0;
				for ( ; k < pass.Length; k++ )
				{
					if ( k == j )
						continue;
					var d = Vector3.Dot( pass[k], normal ) - distance;
					if ( d < -Epsilon )
						break;
					if ( d > Epsilon )
						front++;
				}
				if ( k != pass.Length || front == 0 )
					continue;

				if ( flipClip )
				{
					normal = -normal;
					distance = -distance;
				}

				target = Clip( target, normal, distance );
				if ( target is null )
					return null;
			}
		}
		return target;
	}

	// The part of a convex polygon in front of a plane (on it counts as in front when the whole polygon is), or null for none.
	static Vector3[] Clip( Vector3[] points, Vector3 normal, float distance )
	{
		var n = points.Length;
		Span<float> d = n <= 64 ? stackalloc float[n] : new float[n];
		int front = 0, back = 0;
		for ( var i = 0; i < n; i++ )
		{
			d[i] = Vector3.Dot( points[i], normal ) - distance;
			if ( d[i] > Epsilon )
				front++;
			else if ( d[i] < -Epsilon )
				back++;
		}
		if ( back == 0 )
			return points;
		if ( front == 0 )
			return null;

		var result = new List<Vector3>( n + 2 );
		for ( var i = 0; i < n; i++ )
		{
			var a = points[i];
			var b = points[(i + 1) % n];
			var da = d[i];
			var db = d[(i + 1) % n];
			if ( da >= -Epsilon )
				result.Add( a );
			if ( (da > Epsilon && db < -Epsilon) || (da < -Epsilon && db > Epsilon) )
				result.Add( a + (b - a) * (da / (da - db)) );
		}
		return result.Count >= 3 ? result.ToArray() : null;
	}

	static bool Has( ulong[] bits, int i ) => (bits[i >> 6] & (1UL << (i & 63))) != 0;
	static void Set( ulong[] bits, int i ) => bits[i >> 6] |= 1UL << (i & 63);
	static int Count( ulong[] bits ) => bits.Sum( b => System.Numerics.BitOperations.PopCount( b ) );
}
