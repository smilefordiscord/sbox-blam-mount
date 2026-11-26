using System;
using System.Collections.Generic;
using System.Linq;

namespace Blam.Halo3;

// Inside the namespace so these win over s&box's global vector types when this compiles into the mount.
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;

// A decal's u/v over one level triangle: affine in the triangle's barycentric (s, t), as projection along a fixed direction is.
public readonly record struct DecalUvMap( Vector3 U, Vector3 V )
{
	public Vector2 At( Vector2 st ) => new( U.X * st.X + U.Y * st.Y + U.Z, V.X * st.X + V.Y * st.Y + V.Z );

	// From three or more points of a piece (barycentric, u/v): the three spanning the largest area.
	public static DecalUvMap? Fit( IReadOnlyList<Vector2> st, IReadOnlyList<Vector2> uv )
	{
		var best = (0f, 0, 0, 0);
		for ( var i = 0; i < st.Count; i++ )
		for ( var j = i + 1; j < st.Count; j++ )
		for ( var k = j + 1; k < st.Count; k++ )
		{
			var area = MathF.Abs( Cross( st[j] - st[i], st[k] - st[i] ) );
			if ( area > best.Item1 )
				best = (area, i, j, k);
		}
		if ( best.Item1 < 1e-10f )
			return null;

		var (_, a, b, c) = best;
		// Solve [s t 1] x = value for the three points, per u and v.
		var det = st[a].X * (st[b].Y - st[c].Y) - st[a].Y * (st[b].X - st[c].X) + (st[b].X * st[c].Y - st[c].X * st[b].Y);
		Vector3 Solve( float va, float vb, float vc )
		{
			var x = (va * (st[b].Y - st[c].Y) - st[a].Y * (vb - vc) + (vb * st[c].Y - vc * st[b].Y)) / det;
			var y = (st[a].X * (vb - vc) - va * (st[b].X - st[c].X) + (st[b].X * vc - st[c].X * vb)) / det;
			var z = (st[a].X * (st[b].Y * vc - st[c].Y * vb) - st[a].Y * (st[b].X * vc - st[c].X * vb) + va * (st[b].X * st[c].Y - st[c].X * st[b].Y)) / det;
			return new Vector3( x, y, z );
		}
		return new DecalUvMap( Solve( uv[a].X, uv[b].X, uv[c].X ), Solve( uv[a].Y, uv[b].Y, uv[c].Y ) );
	}

	internal static float Cross( Vector2 a, Vector2 b ) => a.X * b.Y - a.Y * b.X;
}

// Overlapping decals on one level triangle as MCC applies them, one after another into the same albedo: the triangle's covered area split into convex regions, each with the decals over it in order (a decal's own overlapping pieces count once).
public static class DecalOverlay
{
	const float MinArea = 1e-7f;

	// Many decals stacked on one triangle split it into ever more slivers (Guardian ran out of memory); past this many regions, later decals on the triangle are left out.
	const int MaxRegions = 256;

	public sealed record Region( Vector2[] Polygon, List<(int Decal, int Piece, DecalUvMap Uv)> Layers );

	// pieces: in drawing order, each a convex polygon in the triangle's barycentric coordinates with the decal it belongs to (an index) and its u/v.
	public static List<Region> Overlay( IEnumerable<(int Decal, int Piece, Vector2[] Polygon, DecalUvMap Uv)> pieces )
	{
		var regions = new List<Region>();
		foreach ( var (decal, piece, polygon, uv) in pieces )
		{
			// A piece is cut along every region it overlaps, so cap what's kept, not only what's started.
			if ( regions.Count >= MaxRegions )
				break;
			var p = Ccw( polygon );
			if ( Area( p ) < MinArea )
				continue;
			var next = new List<Region>();
			var rest = new List<Vector2[]> { p };
			foreach ( var r in regions )
			{
				var inside = Intersect( r.Polygon, p );
				if ( inside is null )
				{
					next.Add( r );
					continue;
				}
				rest = rest.SelectMany( x => Intersect( x, r.Polygon ) is null ? [x] : Difference( x, r.Polygon ) ).ToList();
				if ( rest.Count > MaxRegions * 4 )
					return regions;
				if ( r.Layers.Any( l => l.Decal == decal ) )
				{
					next.Add( r );
					continue;
				}
				next.Add( new Region( inside, [.. r.Layers, (decal, piece, uv)] ) );
				next.AddRange( Difference( r.Polygon, p ).Select( x => new Region( x, r.Layers ) ) );
			}
			next.AddRange( rest.Select( x => new Region( x, [(decal, piece, uv)] ) ) );
			if ( next.Count > MaxRegions * 4 )
				break;
			regions = next;
		}
		return regions;
	}

	static float Area( Vector2[] p )
	{
		var a = 0f;
		for ( var i = 0; i < p.Length; i++ )
			a += DecalUvMap.Cross( p[i], p[(i + 1) % p.Length] );
		return a / 2;
	}

	static Vector2[] Ccw( Vector2[] p ) => Area( p ) < 0 ? p.Reverse().ToArray() : p;

	// The part of a convex polygon left of the line a -> b (inside, for a counter-clockwise edge), or right of it.
	static Vector2[] Clip( Vector2[] p, Vector2 a, Vector2 b, bool left )
	{
		var output = new List<Vector2>( p.Length + 2 );
		float Side( Vector2 x ) => (left ? 1 : -1) * DecalUvMap.Cross( b - a, x - a );
		for ( var i = 0; i < p.Length; i++ )
		{
			var c = p[i];
			var n = p[(i + 1) % p.Length];
			var dc = Side( c );
			var dn = Side( n );
			if ( dc >= 0 )
				output.Add( c );
			if ( (dc >= 0) != (dn >= 0) )
				output.Add( c + (n - c) * (dc / (dc - dn)) );
		}
		return output.Count >= 3 ? output.ToArray() : null;
	}

	static Vector2[] Intersect( Vector2[] p, Vector2[] q )
	{
		var result = p;
		for ( var i = 0; i < q.Length && result is not null; i++ )
			result = Clip( result, q[i], q[(i + 1) % q.Length], true );
		return result is not null && Area( result ) >= MinArea ? result : null;
	}

	// p minus q as convex pieces: past each edge of q in turn, keeping what's inside the edges before.
	static IEnumerable<Vector2[]> Difference( Vector2[] p, Vector2[] q )
	{
		var remaining = p;
		for ( var i = 0; i < q.Length && remaining is not null; i++ )
		{
			var outside = Clip( remaining, q[i], q[(i + 1) % q.Length], false );
			if ( outside is not null && Area( outside ) >= MinArea )
				yield return outside;
			remaining = Clip( remaining, q[i], q[(i + 1) % q.Length], true );
		}
	}
}
