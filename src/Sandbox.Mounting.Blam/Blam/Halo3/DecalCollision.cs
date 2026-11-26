using System;
using System.Collections.Generic;
using System.Linq;
using Blam.Halo3.Tags;

namespace Blam.Halo3;

// Inside the namespace so these win over s&box's global vector types when this compiles into the mount.
using Vector3 = System.Numerics.Vector3;

// A structure collision BSP's surfaces as MCC's decals spread over them: each surface's outward normal and bounds, and its neighbours across shared edges.
public sealed class DecalCollision
{
	const float CellSize = 4;

	public CollisionBsp Bsp { get; }
	public Vector3[] Normals { get; }
	public float[] Distances { get; }
	readonly (Vector3 Min, Vector3 Max)[] _bounds;
	readonly Dictionary<(int, int), List<int>> _edges = new();
	readonly Dictionary<(int, int, int), List<int>> _cells = new();

	public DecalCollision( CollisionBsp bsp )
	{
		Bsp = bsp;
		var count = bsp.Surfaces.Count;
		Normals = new Vector3[count];
		Distances = new float[count];
		_bounds = new (Vector3, Vector3)[count];
		for ( var s = 0; s < count; s++ )
		{
			var surface = bsp.Surfaces[s];
			var (normal, distance) = surface.Plane < bsp.Planes.Count ? bsp.Planes[surface.Plane] : (Vector3.UnitZ, 0f);
			Normals[s] = surface.PlaneFlipped ? -normal : normal;
			Distances[s] = surface.PlaneFlipped ? -distance : distance;

			var corners = surface.Corners;
			if ( corners.Length < 3 )
			{
				_bounds[s] = (new Vector3( float.MaxValue ), new Vector3( float.MinValue ));
				continue;
			}
			var min = new Vector3( float.MaxValue );
			var max = new Vector3( float.MinValue );
			for ( var i = 0; i < corners.Length; i++ )
			{
				var p = bsp.Vertices[corners[i]];
				min = Vector3.Min( min, p );
				max = Vector3.Max( max, p );
				var j = corners[(i + 1) % corners.Length];
				var key = corners[i] < j ? (corners[i], j) : (j, corners[i]);
				if ( !_edges.TryGetValue( key, out var list ) )
					_edges[key] = list = new List<int>( 2 );
				list.Add( s );
			}
			_bounds[s] = (min, max);

			var a = Cell( min );
			var b = Cell( max );
			if ( (long)(b.X - a.X + 1) * (b.Y - a.Y + 1) * (b.Z - a.Z + 1) > 4096 )
				continue;
			for ( var x = a.X; x <= b.X; x++ )
			for ( var y = a.Y; y <= b.Y; y++ )
			for ( var z = a.Z; z <= b.Z; z++ )
			{
				if ( !_cells.TryGetValue( (x, y, z), out var list ) )
					_cells[(x, y, z)] = list = new List<int>();
				list.Add( s );
			}
		}
	}

	static (int X, int Y, int Z) Cell( Vector3 p ) => ((int)MathF.Floor( p.X / CellSize ), (int)MathF.Floor( p.Y / CellSize ), (int)MathF.Floor( p.Z / CellSize ));

	// Surfaces whose bounds overlap a box.
	public IEnumerable<int> Near( Vector3 min, Vector3 max )
	{
		var seen = new HashSet<int>();
		var a = Cell( min );
		var b = Cell( max );
		for ( var x = a.X; x <= b.X; x++ )
		for ( var y = a.Y; y <= b.Y; y++ )
		for ( var z = a.Z; z <= b.Z; z++ )
		{
			if ( !_cells.TryGetValue( (x, y, z), out var list ) )
				continue;
			foreach ( var s in list )
			{
				var (lo, hi) = _bounds[s];
				if ( seen.Add( s ) && lo.X <= max.X && hi.X >= min.X && lo.Y <= max.Y && hi.Y >= min.Y && lo.Z <= max.Z && hi.Z >= min.Z )
					yield return s;
			}
		}
	}

	// The surfaces across a surface's edge from corner i to corner j.
	public IReadOnlyList<int> Across( int i, int j ) => _edges.TryGetValue( i < j ? (i, j) : (j, i), out var list ) ? list : Array.Empty<int>();

	// The nearest surface a ray meets from its front, and how far along.
	public (int Surface, float T)? Raycast( Vector3 origin, Vector3 direction, float length )
	{
		var end = origin + direction * length;
		(int, float)? best = null;
		foreach ( var s in Near( Vector3.Min( origin, end ), Vector3.Max( origin, end ) ) )
		{
			var facing = Vector3.Dot( Normals[s], direction );
			if ( facing >= 0 )
				continue;
			var t = (Distances[s] - Vector3.Dot( Normals[s], origin )) / facing;
			if ( t < 0 || t > length || best is { } b && t >= b.Item2 )
				continue;
			if ( Inside( s, origin + direction * t, 1e-3f ) )
				best = (s, t);
		}
		return best;
	}

	// Whether a point (near the surface's plane) lies inside its polygon, give or take a margin.
	public bool Inside( int s, Vector3 point, float margin )
	{
		var corners = Bsp.Surfaces[s].Corners;
		if ( corners.Length < 3 )
			return false;
		var n = Normals[s];
		int positive = 0, negative = 0;
		for ( var i = 0; i < corners.Length; i++ )
		{
			var p = Bsp.Vertices[corners[i]];
			var q = Bsp.Vertices[corners[(i + 1) % corners.Length]];
			var edge = q - p;
			var length = edge.Length();
			if ( length < 1e-6f )
				continue;
			var side = Vector3.Dot( Vector3.Cross( edge, point - p ), n ) / length;
			if ( side > margin )
				positive++;
			else if ( side < -margin )
				negative++;
		}
		return positive == 0 || negative == 0;
	}

	// A surface's sides as planes through each edge, pointing inward: toward a neighbour that also carries the decal, along the mean of the two normals, so the two split render triangles off their planes without gaps.
	public IEnumerable<(Vector3 Point, Vector3 Inward)> Sides( int s, Func<int, bool> shared )
	{
		var surface = Bsp.Surfaces[s];
		var corners = surface.Corners;
		for ( var i = 0; i < corners.Length; i++ )
		{
			var p = Bsp.Vertices[corners[i]];
			var q = Bsp.Vertices[corners[(i + 1) % corners.Length]];
			var n = Normals[s];
			if ( surface.Neighbours is { } across && across[i] >= 0 && shared( across[i] ) && (n + Normals[across[i]]).LengthSquared() > 1e-6f )
				n = Vector3.Normalize( n + Normals[across[i]] );
			yield return (p, Vector3.Cross( n, q - p ));
		}
	}

	// Surfaces a decal never spreads onto (flags & 0x3B: two-sided, invisible, breakable, invalid, conveyor).
	public bool Excluded( int s ) => (Bsp.Surfaces[s].Flags & 0x3B) != 0;
}
