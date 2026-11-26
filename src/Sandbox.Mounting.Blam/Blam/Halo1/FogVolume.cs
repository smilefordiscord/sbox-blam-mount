using System;
using System.Collections.Generic;
using System.Linq;
using Blam.Halo1.Tags;

namespace Blam.Halo1;

// Inside the namespace so these win over s&box's global Vector2/Vector3 when this compiles into the mount.
using Vector3 = System.Numerics.Vector3;

/// <summary>
/// One fog plane's extent, in world units: a box behind the plane, and the water surface lying
/// in it, if there is one. The fog itself covers everything behind the whole plane; the box says
/// what's under its water and where the view crosses that water.
/// </summary>
/// <remarks>
/// The box is the bounds of the clusters under the plane, but only across the plane's outline:
/// clusters are big (c10's swamp clusters reach into the interior BSPs next to them), while the
/// outline is the fog surface the level designer modeled. Along the plane's normal it runs from
/// the bottom of the clusters to the plane. Water level with the plane that carries on past the
/// outline widens the box (within the clusters): b30's sea reaches 200 world units past its fog
/// plane's outline.
///
/// Water and glass fog what they show themselves, so where the view crosses the water surface the
/// fog volume must leave the pixel alone, whatever order the two happen to draw in. The water is
/// recorded as one level (along the normal's main axis) and a coverage mask over the box.
/// </remarks>
public sealed class FogVolume
{
	/// <summary>
	/// Resolution of <see cref="WaterMask"/>, both ways.
	/// </summary>
	public const int MaskSize = 256;

	/// <summary>
	/// The fog's outline only marks out where it is; the fog can reach a little past it.
	/// </summary>
	const float OutlineMargin = 0.5f;

	/// <summary>
	/// How far a water triangle may be from level (in world units) to count as flat water.
	/// </summary>
	const float LevelTolerance = 0.05f;

	readonly List<(Vector3 A, Vector3 B, Vector3 C)> _water = new();

	public BspFogPlane Plane { get; }
	public Vector3 Normal => new( Plane.Plane.X, Plane.Plane.Y, Plane.Plane.Z );
	public Vector3 Min { get; private set; }
	public Vector3 Max { get; private set; }
	public IReadOnlyList<BspClusterFog> Clusters { get; }

	/// <summary>
	/// The normal's main axis (0 = x, 1 = y, 2 = z). The water mask spans the other two.
	/// </summary>
	public int Axis { get; }

	/// <summary>
	/// Height of the water surface along <see cref="Axis"/>, or null if there's no water.
	/// </summary>
	public float? WaterLevel { get; private set; }

	/// <summary>
	/// <see cref="MaskSize"/> x <see cref="MaskSize"/> bytes, row by row: 255 where there's water at <see cref="WaterLevel"/>.
	/// Columns run across the box along the axis after <see cref="Axis"/>, rows along the one after
	/// that (for a horizontal plane: columns along x, rows along y).
	/// </summary>
	public byte[] WaterMask { get; private set; }

	FogVolume( BspFogPlane plane, Vector3 min, Vector3 max, int axis, IReadOnlyList<BspClusterFog> clusters )
	{
		Plane = plane;
		Min = min;
		Max = max;
		Axis = axis;
		Clusters = clusters;
	}

	/// <summary>
	/// The fog volume of one of a BSP's fog planes, or null if it covers nothing.
	/// </summary>
	public static FogVolume Create( StructureBspFog fog, int planeIndex )
	{
		var plane = fog.Planes[planeIndex];
		var normal = new Vector3( plane.Plane.X, plane.Plane.Y, plane.Plane.Z );
		if ( normal.Length() < 0.5f || plane.Vertices.Count == 0 )
			return null;

		var clusters = fog.Clusters.Where( c => c.FogPlane == planeIndex && c.Min.X <= c.Max.X ).ToList();
		if ( clusters.Count == 0 )
			return null;

		var min = ToArray( clusters.Select( c => c.Min ).Aggregate( Vector3.Min ) );
		var max = ToArray( clusters.Select( c => c.Max ).Aggregate( Vector3.Max ) );
		var outlineMin = ToArray( plane.Vertices.Aggregate( Vector3.Min ) );
		var outlineMax = ToArray( plane.Vertices.Aggregate( Vector3.Max ) );
		var n = ToArray( normal );

		var axis = MainAxis( normal );
		var u = (axis + 1) % 3;
		var v = (axis + 2) % 3;

		// Across the plane, only as far as its outline.
		foreach ( var a in new[] { u, v } )
		{
			min[a] = MathF.Max( min[a], outlineMin[a] - OutlineMargin );
			max[a] = MathF.Min( max[a], outlineMax[a] + OutlineMargin );
			if ( min[a] >= max[a] )
				return null;
		}

		// Along the normal, up to the highest point the plane reaches over the box (planes are
		// nearly always axis aligned; a30 has one tilted by a degree). The shader clips exactly.
		var lowest = float.MaxValue;
		var highest = float.MinValue;
		for ( var corner = 0; corner < 4; corner++ )
		{
			var cu = (corner & 1) == 0 ? min[u] : max[u];
			var cv = (corner & 2) == 0 ? min[v] : max[v];
			var crossing = (plane.Plane.W - n[u] * cu - n[v] * cv) / n[axis];
			lowest = MathF.Min( lowest, crossing );
			highest = MathF.Max( highest, crossing );
		}

		// The normal points out of the fog, so the fog is on its negative side.
		if ( n[axis] > 0 )
			max[axis] = MathF.Min( max[axis], highest );
		else
			min[axis] = MathF.Max( min[axis], lowest );

		if ( min[axis] >= max[axis] )
			return null;

		return new FogVolume( plane, ToVector( min ), ToVector( max ), axis, clusters );
	}

	/// <summary>
	/// Adds a BSP's water triangles (world units) that lie in this volume, and works out the water
	/// level and mask again. Returns true if anything changed.
	/// </summary>
	public bool AddWater( IEnumerable<(Vector3 A, Vector3 B, Vector3 C)> triangles )
	{
		var added = false;
		var min = ToArray( Min );
		var max = ToArray( Max );
		var clusterMin = ToArray( Clusters.Select( c => c.Min ).Aggregate( Vector3.Min ) );
		var clusterMax = ToArray( Clusters.Select( c => c.Max ).Aggregate( Vector3.Max ) );
		var pending = triangles.ToList();

		// Water touching the box widens it to cover the water, and so on for water touching that.
		for ( var grew = true; grew; )
		{
			grew = false;
			foreach ( var triangle in pending.ToList() )
			{
				var a = ToArray( triangle.A );
				var b = ToArray( triangle.B );
				var c = ToArray( triangle.C );
				var level = (a[Axis] + b[Axis] + c[Axis]) / 3;
				if ( MathF.Max( a[Axis], MathF.Max( b[Axis], c[Axis] ) ) - MathF.Min( a[Axis], MathF.Min( b[Axis], c[Axis] ) ) > LevelTolerance ||
					level < min[Axis] - LevelTolerance || level > max[Axis] + LevelTolerance )
					continue;

				var touches = true;
				for ( var i = 0; i < 3 && touches; i++ )
				{
					if ( i == Axis )
						continue;
					touches = MathF.Max( a[i], MathF.Max( b[i], c[i] ) ) >= min[i] - OutlineMargin &&
						MathF.Min( a[i], MathF.Min( b[i], c[i] ) ) <= max[i] + OutlineMargin;
				}

				if ( !touches )
					continue;

				pending.Remove( triangle );
				for ( var i = 0; i < 3; i++ )
				{
					if ( i == Axis )
						continue;
					var lo = MathF.Max( MathF.Min( a[i], MathF.Min( b[i], c[i] ) ), clusterMin[i] );
					var hi = MathF.Min( MathF.Max( a[i], MathF.Max( b[i], c[i] ) ), clusterMax[i] );
					if ( lo < min[i] ) { min[i] = lo; grew = true; }
					if ( hi > max[i] ) { max[i] = hi; grew = true; }
				}
			}
		}

		Min = ToVector( min );
		Max = ToVector( max );

		foreach ( var triangle in triangles )
		{
			if ( _water.Contains( triangle ) )
				continue;

			// Only flat water inside the box, level with the fog plane's main axis.
			var a = ToArray( triangle.A );
			var b = ToArray( triangle.B );
			var c = ToArray( triangle.C );
			if ( MathF.Max( a[Axis], MathF.Max( b[Axis], c[Axis] ) ) - MathF.Min( a[Axis], MathF.Min( b[Axis], c[Axis] ) ) > LevelTolerance )
				continue;

			var inside = true;
			for ( var i = 0; i < 3 && inside; i++ )
			{
				var lo = MathF.Min( a[i], MathF.Min( b[i], c[i] ) );
				var hi = MathF.Max( a[i], MathF.Max( b[i], c[i] ) );
				var margin = i == Axis ? LevelTolerance : OutlineMargin;
				inside = hi >= min[i] - margin && lo <= max[i] + margin;
			}

			if ( !inside )
				continue;

			_water.Add( triangle );
			added = true;
		}

		if ( added )
			BuildMask();

		return added;
	}

	/// <summary>
	/// Picks the level most of the water area is at, and marks the texels that water covers. Each
	/// mark is grown by a texel so the edge of the water never shows fog drawn twice.
	/// </summary>
	void BuildMask()
	{
		// Grouped by rounded level, but the level used is the group's own (area weighted): rounded,
		// c10's swamp at 1.18 became 1.20 and the shore just under 1.20 counted as under water.
		var levels = new Dictionary<float, (float Area, float Sum)>();
		foreach ( var (a, b, c) in _water )
		{
			var exact = (ToArray( a )[Axis] + ToArray( b )[Axis] + ToArray( c )[Axis]) / 3;
			var key = MathF.Round( exact / LevelTolerance ) * LevelTolerance;
			var area = Vector3.Cross( b - a, c - a ).Length() / 2;
			var group = levels.GetValueOrDefault( key );
			levels[key] = (group.Area + area, group.Sum + exact * area);
		}

		var largest = levels.MaxBy( l => l.Value.Area );
		var chosen = largest.Key;
		WaterLevel = largest.Value.Area > 0 ? largest.Value.Sum / largest.Value.Area : chosen;

		var u = (Axis + 1) % 3;
		var v = (Axis + 2) % 3;
		var min = ToArray( Min );
		var max = ToArray( Max );
		var su = MaskSize / (max[u] - min[u]);
		var sv = MaskSize / (max[v] - min[v]);

		var covered = new bool[MaskSize * MaskSize];
		foreach ( var (a, b, c) in _water )
		{
			var pa = ToArray( a );
			var pb = ToArray( b );
			var pc = ToArray( c );
			if ( MathF.Abs( (pa[Axis] + pb[Axis] + pc[Axis]) / 3 - chosen ) > LevelTolerance * 2 )
				continue;

			// Corners in texel space.
			float X( float[] p ) => (p[u] - min[u]) * su;
			float Y( float[] p ) => (p[v] - min[v]) * sv;
			float ax = X( pa ), ay = Y( pa ), bx = X( pb ), by = Y( pb ), cx = X( pc ), cy = Y( pc );

			var area = (bx - ax) * (cy - ay) - (by - ay) * (cx - ax);
			if ( MathF.Abs( area ) < 1e-6f )
				continue;

			var x0 = Math.Clamp( (int)MathF.Floor( MathF.Min( ax, MathF.Min( bx, cx ) ) ), 0, MaskSize - 1 );
			var x1 = Math.Clamp( (int)MathF.Ceiling( MathF.Max( ax, MathF.Max( bx, cx ) ) ), 0, MaskSize - 1 );
			var y0 = Math.Clamp( (int)MathF.Floor( MathF.Min( ay, MathF.Min( by, cy ) ) ), 0, MaskSize - 1 );
			var y1 = Math.Clamp( (int)MathF.Ceiling( MathF.Max( ay, MathF.Max( by, cy ) ) ), 0, MaskSize - 1 );

			for ( var y = y0; y <= y1; y++ )
			{
				for ( var x = x0; x <= x1; x++ )
				{
					// Texel centre inside the triangle (either winding).
					float px = x + 0.5f, py = y + 0.5f;
					var w0 = (bx - ax) * (py - ay) - (by - ay) * (px - ax);
					var w1 = (cx - bx) * (py - by) - (cy - by) * (px - bx);
					var w2 = (ax - cx) * (py - cy) - (ay - cy) * (px - cx);
					if ( (w0 >= 0 && w1 >= 0 && w2 >= 0) || (w0 <= 0 && w1 <= 0 && w2 <= 0) )
						covered[y * MaskSize + x] = true;
				}
			}
		}

		// Grow by a texel: triangles thinner than a texel (shorelines, rivers) still get marked.
		var mask = new byte[MaskSize * MaskSize];
		for ( var y = 0; y < MaskSize; y++ )
		{
			for ( var x = 0; x < MaskSize; x++ )
			{
				var any = false;
				for ( var dy = -1; dy <= 1 && !any; dy++ )
				{
					for ( var dx = -1; dx <= 1 && !any; dx++ )
					{
						var nx = x + dx;
						var ny = y + dy;
						any = nx >= 0 && ny >= 0 && nx < MaskSize && ny < MaskSize && covered[ny * MaskSize + nx];
					}
				}

				mask[y * MaskSize + x] = any ? (byte)255 : (byte)0;
			}
		}

		WaterMask = mask;
	}

	/// <summary>
	/// Fraction of the mask that's water, for checking.
	/// </summary>
	public float WaterCoverage => WaterMask is null ? 0 : WaterMask.Count( b => b != 0 ) / (float)WaterMask.Length;

	static int MainAxis( Vector3 n ) => MathF.Abs( n.X ) > MathF.Abs( n.Y )
		? (MathF.Abs( n.X ) > MathF.Abs( n.Z ) ? 0 : 2)
		: (MathF.Abs( n.Y ) > MathF.Abs( n.Z ) ? 1 : 2);

	static float[] ToArray( Vector3 v ) => [v.X, v.Y, v.Z];
	static Vector3 ToVector( float[] a ) => new( a[0], a[1], a[2] );
}
