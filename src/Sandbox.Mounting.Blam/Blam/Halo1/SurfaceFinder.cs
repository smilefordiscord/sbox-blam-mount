using System;
using System.Collections.Generic;
using Blam.Halo1.Tags;

namespace Blam.Halo1;

// Inside the namespace so this wins over s&box's global Vector3 when this compiles into the mount.
using Vector3 = System.Numerics.Vector3;

/// <summary>
/// Finds the level surface nearest to a point, from the structure BSPs' render geometry, with the
/// direction it faces (its vertex normals, which face the open side).
/// </summary>
/// <remarks>
/// Triangles go into a grid of <see cref="CellSize"/> cells by their bounds. The cells are coarse
/// because outdoor levels have huge triangles: at 1 world unit, b30's terrain filled millions of
/// cells and finding its decals' surfaces took 5 seconds of the level's 8.6 second build.
/// </remarks>
public sealed class SurfaceFinder
{
	const float CellSize = 8f;

	readonly List<(Vector3 A, Vector3 B, Vector3 C, Vector3 Normal, Vector3 Min, Vector3 Max)> _triangles = new();
	readonly Dictionary<(int, int, int), List<int>> _cells = new();

	// The query each triangle was last tested by, so one in several cells is tested once.
	readonly List<int> _testedBy = new();
	int _query;

	public SurfaceFinder( IEnumerable<StructureBspTag> bsps )
	{
		foreach ( var bsp in bsps )
		foreach ( var lightmap in bsp.Lightmaps )
		foreach ( var material in lightmap.Materials )
		{
			var vertices = bsp.ReadRenderedVertices( material );
			for ( var t = 0; t < material.SurfaceCount; t++ )
			{
				var i = (material.FirstSurface + t) * 3;
				int a = bsp.Surfaces[i], b = bsp.Surfaces[i + 1], c = bsp.Surfaces[i + 2];
				if ( a >= vertices.Length || b >= vertices.Length || c >= vertices.Length )
					continue;

				var normal = vertices[a].Normal + vertices[b].Normal + vertices[c].Normal;
				if ( normal.LengthSquared() < 1e-6f )
					continue;

				Add( vertices[a].Position, vertices[b].Position, vertices[c].Position, Vector3.Normalize( normal ) );
			}
		}
	}

	void Add( Vector3 a, Vector3 b, Vector3 c, Vector3 normal )
	{
		var index = _triangles.Count;
		var low = Vector3.Min( a, Vector3.Min( b, c ) );
		var high = Vector3.Max( a, Vector3.Max( b, c ) );
		_triangles.Add( (a, b, c, normal, low, high) );
		_testedBy.Add( 0 );

		var min = Cell( low );
		var max = Cell( high );
		for ( var x = min.X; x <= max.X; x++ )
		for ( var y = min.Y; y <= max.Y; y++ )
		for ( var z = min.Z; z <= max.Z; z++ )
		{
			if ( !_cells.TryGetValue( (x, y, z), out var list ) )
				_cells[(x, y, z)] = list = new List<int>();
			list.Add( index );
		}
	}

	static (int X, int Y, int Z) Cell( Vector3 p ) =>
		((int)MathF.Floor( p.X / CellSize ), (int)MathF.Floor( p.Y / CellSize ), (int)MathF.Floor( p.Z / CellSize ));

	/// <summary>
	/// The nearest surface within <paramref name="maxDistance"/> of <paramref name="point"/>: its
	/// normal and distance, or null if there's none.
	/// </summary>
	public (Vector3 Normal, float Distance)? Nearest( Vector3 point, float maxDistance )
	{
		var reach = new Vector3( maxDistance );
		var min = Cell( point - reach );
		var max = Cell( point + reach );
		(Vector3 Normal, float Distance)? best = null;
		_query++;

		for ( var x = min.X; x <= max.X; x++ )
		for ( var y = min.Y; y <= max.Y; y++ )
		for ( var z = min.Z; z <= max.Z; z++ )
		{
			if ( !_cells.TryGetValue( (x, y, z), out var list ) )
				continue;

			foreach ( var index in list )
			{
				if ( _testedBy[index] == _query )
					continue;
				_testedBy[index] = _query;

				// Nothing on a triangle is nearer than its bounds.
				var t = _triangles[index];
				var outside = Vector3.Max( Vector3.Max( t.Min - point, point - t.Max ), Vector3.Zero );
				if ( outside.LengthSquared() > maxDistance * maxDistance )
					continue;

				var distance = Vector3.Distance( point, ClosestPoint( point, t.A, t.B, t.C ) );
				if ( distance <= maxDistance && (best is null || distance < best.Value.Distance) )
					best = (t.Normal, distance);
			}
		}

		return best;
	}

	/// <summary>
	/// Closest point on a triangle (Ericson, Real-Time Collision Detection, 5.1.5).
	/// </summary>
	static Vector3 ClosestPoint( Vector3 p, Vector3 a, Vector3 b, Vector3 c )
	{
		Vector3 ab = b - a, ac = c - a, ap = p - a;
		float d1 = Vector3.Dot( ab, ap ), d2 = Vector3.Dot( ac, ap );
		if ( d1 <= 0 && d2 <= 0 ) return a;

		Vector3 bp = p - b;
		float d3 = Vector3.Dot( ab, bp ), d4 = Vector3.Dot( ac, bp );
		if ( d3 >= 0 && d4 <= d3 ) return b;

		float vc = d1 * d4 - d3 * d2;
		if ( vc <= 0 && d1 >= 0 && d3 <= 0 ) return a + d1 / (d1 - d3) * ab;

		Vector3 cp = p - c;
		float d5 = Vector3.Dot( ab, cp ), d6 = Vector3.Dot( ac, cp );
		if ( d6 >= 0 && d5 <= d6 ) return c;

		float vb = d5 * d2 - d1 * d6;
		if ( vb <= 0 && d2 >= 0 && d6 <= 0 ) return a + d2 / (d2 - d6) * ac;

		float va = d3 * d6 - d5 * d4;
		if ( va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0 ) return b + (d4 - d3) / ((d4 - d3) + (d5 - d6)) * (c - b);

		var denom = 1 / (va + vb + vc);
		return a + ab * (vb * denom) + ac * (vc * denom);
	}
}
