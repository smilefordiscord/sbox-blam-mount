using System;
using System.Collections.Generic;
using System.Linq;
using Blam.Halo3.Tags;

namespace Blam.Halo3;

// Inside the namespace so these win over s&box's global vector types when this compiles into the mount.
using Quaternion = System.Numerics.Quaternion;
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;

// A decal vertex on the level: world units, the decal's bump frame and texture coordinate, the surface's lighting inputs, and the surface's own texture coordinate and frame (for drawing the surface again with the decal in its albedo).
public readonly record struct DecalVertex( Vector3 Position, Vector3 Normal, Vector3 Tangent, Vector3 Binormal, Vector2 TexCoord, Vector2 LightmapTexCoord, VertexLighting Light,
	Vector2 SurfaceTexCoord = default, Vector3 SurfaceTangent = default, Vector3 SurfaceBinormal = default );

// A placed decal's triangles (a list) on one group of level surfaces (one lighting path) of one BSP material.
// Polygons: the same pieces before triangulation, each convex and on one level triangle (DecalSurfaces.Triangles index), for overlaying decals that share a surface.
public sealed record PlacedDecal( DecalSystemTag System, DecalSystemEntry Decal, DecalSurfaceGroup Group, IReadOnlyList<DecalVertex> Triangles, int Material = -1, IReadOnlyList<(int Triangle, DecalVertex[] Points)> Polygons = null );

// Lays Halo 3 decals onto a BSP's render triangles as MCC lays them onto collision surfaces: spreading from the surface under the decal across edges inside its u/v rectangle (half extents from radius and bitmap aspect), texture coordinates from projecting along the decal's normal (bent toward steep faces past the clamp angle), stopping at faces past the cull angle.
// Halo 3 lays decals onto collision surfaces and draws pre-lighting ones into its albedo buffer; render triangles carry the lightmap inputs, so the decals light exactly like the surface under them.
public sealed class DecalProjector( DecalSurfaces surfaces )
{
	// Against z fighting decals stay on the surface and their shader pulls them this fraction of the way toward the camera, as MCC's depth bias does (a lift off the surface showed up close as a floating layer).
	public const float DepthPull = 1f / 1024;

	public DecalSurfaces Surfaces => surfaces;

	readonly List<(int Group, int Material, int Triangle, DecalVertex[] Points)> _polygons = new();

	// A world box holding everything a decal can touch (for DecalSurfaces.Build's regions).
	public static (Vector3 Min, Vector3 Max) Region( DecalSystemTag system, Vector3 position, float scale )
	{
		var reach = system.Decals.Select( d => MathF.Max( d.RadiusMax, 0.05f ) * MathF.Max( d.BitmapAspect, 1 ) ).DefaultIfEmpty( 0.05f ).Max() * (scale > 0 ? scale : 1) * 1.5f;
		var r = new Vector3( reach );
		return (position - r, position + r);
	}

	// A scenario decal: rotation's z is the surface normal (out), x the image's up, -y its right. Halo 3 lays every decal of the system (looping over the decs block).
	public List<PlacedDecal> Place( DecalSystemTag system, Quaternion rotation, Vector3 position, float scale )
	{
		var placed = new List<PlacedDecal>();
		foreach ( var decal in system.Decals )
			Place( system, decal, rotation, position, scale, placed );
		return placed;
	}

	// Most render triangles a decal may cover (MCC stops at 128 collision surfaces).
	const int MaximumTriangles = 4096;

	// One decal as Halo 3 lays it: no depth limit; from the surface its ray hits it spreads across shared edges that pass through its u/v rectangle onto faces within the cull angle, projected along the normal turned toward faces steeper than the clamp angle.
	// So a decal stops at any face steeper than its cull angle (Zanzibar's wall streak ends at the edge of the recess it starts in). Render triangles are welded by position to find their neighbours. MCC turns the projection on from face to face over its few large collision surfaces; over many small render triangles that would wrap a decal right around corners, so each face is measured against the decal's own normal.
	void Place( DecalSystemTag system, DecalSystemEntry decal, Quaternion rotation, Vector3 position, float scale, List<PlacedDecal> placed )
	{
		var radius = (decal.RadiusMin + decal.RadiusMax) / 2 * (scale > 0 ? scale : 1);
		if ( radius <= 0 )
			return;

		var aspect = decal.BitmapAspect > 0 ? decal.BitmapAspect : 1;
		var normal = Vector3.Normalize( Vector3.Transform( Vector3.UnitZ, rotation ) );
		// Signs and drips on walls have x pointing up (810 of 1014 wall decals over five maps; MCC's frame agrees): x is the image's up, -y its right.
		var a = -Vector3.Normalize( Vector3.Transform( Vector3.UnitY, rotation ) );
		var b = Vector3.Normalize( Vector3.Transform( Vector3.UnitX, rotation ) );
		var halfU = radius * aspect;
		var halfV = radius;
		var cosCull = MathF.Max( MathF.Cos( Math.Clamp( decal.CullAngle > 0 ? decal.CullAngle : 90, 0, 90 ) * MathF.PI / 180 ), 1e-3f );
		var clamp = Math.Clamp( decal.ClampAngle > 0 ? decal.ClampAngle : 90, 0, 89 ) * MathF.PI / 180;
		var cosClamp = MathF.Cos( clamp );

		// The triangles it could reach: the u/v rectangle, as deep as it is wide.
		var reach = MathF.Max( halfU, halfV ) * 2;
		var box = new Vector3( reach );
		var candidates = surfaces.Near( position - box, position + box ).ToList();
		if ( candidates.Count == 0 )
			return;

		bool Allowed( int t )
		{
			var g = surfaces.Groups[surfaces.Triangles[t].Group];
			return !(g.Instance < 0 ? system.AvoidStructure : system.AvoidInstances);
		}

		// Where it starts: what a ray from just off the decal hits, and (unless the system keeps to one surface) the nearest surface of each other piece of geometry (an instance, or the structure under one) within half the system's largest radius of it, as MCC's extra hits.
		var seeds = new List<int>();
		if ( surfaces.Raycast( position + normal * 0.1f, -normal, 1.1f ) is { } hit && Allowed( hit.Triangle ) )
			seeds.Add( hit.Triangle );
		if ( (system.Flags & 0x58) == 0 )
		{
			var half = MathF.Max( system.MaximumRadius, radius ) / 2;
			var nearest = new Dictionary<int, (int Triangle, float Distance)>();
			foreach ( var t in candidates )
			{
				if ( !Allowed( t ) || seeds.Contains( t ) )
					continue;
				var (ia, ib, ic, group, _) = surfaces.Triangles[t];
				if ( Intersect( position + normal * half, -normal, surfaces.Vertices[ia].Position, surfaces.Vertices[ib].Position, surfaces.Vertices[ic].Position ) is not { } d || d < 0 || d > 2 * half )
					continue;
				var instance = surfaces.Groups[group].Instance;
				var distance = MathF.Abs( d - half );
				if ( !nearest.TryGetValue( instance, out var best ) || distance < best.Distance )
					nearest[instance] = (t, distance);
			}
			var seedInstance = seeds.Count > 0 ? surfaces.Groups[surfaces.Triangles[seeds[0]].Group].Instance : int.MinValue;
			foreach ( var (instance, best) in nearest.OrderBy( n => n.Value.Distance ) )
			{
				if ( instance != seedInstance && seeds.Count < 16 )
					seeds.Add( best.Triangle );
			}
		}
		if ( seeds.Count == 0 )
			return;

		// Restrict to a single material: the one under the decal's centre.
		var material = (system.Flags & 0x20) != 0 ? surfaces.Triangles[seeds[0]].Material : -1;

		// Neighbours across shared edges, by welded position.
		var edges = new Dictionary<(Vector3, Vector3), List<int>>();
		(Vector3, Vector3) Edge( int i, int j )
		{
			var p = Weld( surfaces.Vertices[i].Position );
			var q = Weld( surfaces.Vertices[j].Position );
			return Less( p, q ) ? (p, q) : (q, p);
		}
		foreach ( var t in candidates )
		{
			var (ia, ib, ic, _, _) = surfaces.Triangles[t];
			foreach ( var e in new[] { Edge( ia, ib ), Edge( ib, ic ), Edge( ic, ia ) } )
			{
				if ( !edges.TryGetValue( e, out var list ) )
					edges[e] = list = new List<int>( 2 );
				list.Add( t );
			}
		}

		// The u/v of a point projected along a direction onto the decal's plane.
		(float U, float V) Uv( Vector3 point, Vector3 direction )
		{
			var along = Vector3.Dot( direction, normal );
			var h = Vector3.Dot( point - position, normal );
			var onPlane = point - direction * (h / along) - position;
			return (Vector3.Dot( onPlane, a ), Vector3.Dot( onPlane, b ));
		}

		// The projection onto a face (null: past the cull angle), turned toward the face to meet it at the clamp angle when steeper.
		Vector3? Projector( Vector3 face )
		{
			var from = normal;
			var facing = Vector3.Dot( face, from );
			if ( facing < cosCull )
				return null;
			if ( facing >= cosClamp )
				return from;
			var axis = Vector3.Cross( face, from );
			return axis.LengthSquared() > 1e-12f ? Vector3.Transform( face, Quaternion.CreateFromAxisAngle( Vector3.Normalize( axis ), clamp ) ) : from;
		}

		var byGroup = new Dictionary<(int Group, int Material), List<DecalVertex>>();
		_polygons.Clear();
		var done = new HashSet<int>();

		// The structure as MCC covers it: spread over its collision surfaces, then the render triangles lying on the surfaces reached (render triangles are finer and bevelled, so spreading over them runs past the steps MCC stops at).
		if ( surfaces.Collision is { } collision && !system.AvoidStructure && CollisionFill( collision, system, new DecalFrame( position, a, b, normal ), halfU, halfV, cosCull, cosClamp ) is { Count: > 0 } reached )
		{
			var carrying = reached.Select( r => r.Surface ).ToHashSet();
			foreach ( var t in candidates )
			{
				var (ia, ib, ic, group, _) = surfaces.Triangles[t];
				if ( surfaces.Groups[group].Instance >= 0 )
					continue;
				done.Add( t );
				// Each part of the triangle over a reached surface it lies on takes that surface's frame (the recess behind Zanzibar's wall is 0.1 wu off the wall's plane and turned differently).
				var face = surfaces.FaceNormal( t );
				Vector3 pa = surfaces.Vertices[ia].Position, pb = surfaces.Vertices[ib].Position, pc = surfaces.Vertices[ic].Position;
				var fits = new List<(int Surface, DecalFrame Frame, float Error)>();
				foreach ( var (s, frame) in reached )
				{
					var n = collision.Normals[s];
					if ( Vector3.Dot( face, n ) <= 0.7f )
						continue;
					var error = MathF.Max( MathF.Abs( Vector3.Dot( n, pa ) - collision.Distances[s] ), MathF.Max( MathF.Abs( Vector3.Dot( n, pb ) - collision.Distances[s] ), MathF.Abs( Vector3.Dot( n, pc ) - collision.Distances[s] ) ) );
					if ( error < 0.15f )
						fits.Add( (s, frame, error) );
				}
				var best = fits.Count > 0 ? fits.Min( f => f.Error ) : 0;
				foreach ( var (s, frame, error) in fits )
				{
					if ( error <= best + 0.05f )
						Emit( t, face, frame.U, frame.V, group, halfU, halfV, frame.Uv, byGroup, collision.Sides( s, carrying.Contains ).Append( Behind( collision, s ) ) );
				}
			}
			seeds.RemoveAll( t => surfaces.Groups[surfaces.Triangles[t].Group].Instance < 0 );
		}

		var queue = new Queue<(int Triangle, Vector3 Direction)>();
		foreach ( var seed in seeds )
		{
			if ( Projector( surfaces.FaceNormal( seed ) ) is { } direction && done.Add( seed ) )
				queue.Enqueue( (seed, direction) );
		}

		while ( queue.Count > 0 && done.Count <= MaximumTriangles )
		{
			var (t, direction) = queue.Dequeue();
			var (ia, ib, ic, group, _) = surfaces.Triangles[t];
			var face = surfaces.FaceNormal( t );
			Emit( t, face, a, b, group, halfU, halfV, p => Uv( p, direction ), byGroup );

			foreach ( var (i, j) in new[] { (ia, ib), (ib, ic), (ic, ia) } )
			{
				var (u0, v0) = Uv( surfaces.Vertices[i].Position, direction );
				var (u1, v1) = Uv( surfaces.Vertices[j].Position, direction );
				if ( !SegmentInRectangle( u0, v0, u1, v1, halfU, halfV ) || !edges.TryGetValue( Edge( i, j ), out var across ) )
					continue;
				foreach ( var n in across )
				{
					if ( done.Contains( n ) || !Allowed( n ) || material >= 0 && surfaces.Triangles[n].Material != material )
						continue;
					var nFace = surfaces.FaceNormal( n );
					if ( system.ForcePlanar && Vector3.DistanceSquared( nFace, face ) > 1e-4f )
						continue;
					if ( Projector( nFace ) is { } next )
					{
						done.Add( n );
						queue.Enqueue( (n, next) );
					}
				}
			}
		}

		foreach ( var ((group, bspMaterial), triangles) in byGroup )
			placed.Add( new PlacedDecal( system, decal, surfaces.Groups[group], triangles, bspMaterial, _polygons.Where( p => p.Group == group && p.Material == bspMaterial ).Select( p => (p.Triangle, p.Points) ).ToList() ) );
	}

	// How far the render surface may stand in front of a collision polygon and still show its decal (world units).
	const float ShowAhead = 0.005f;

	// MCC draws a decal on the collision polygon and depth tests it (no depth write, a small bias), so where the render surface stands in front of the polygon (Zanzibar's wall bulging past its collision beside the crack) the decal is hidden: the side keeping the part of a render triangle not in front of the polygon's plane.
	static (Vector3 Point, Vector3 Inward) Behind( DecalCollision collision, int s ) => (collision.Normals[s] * (collision.Distances[s] + ShowAhead), -collision.Normals[s]);

	// Halo 3's spread over collision surfaces: breadth first from the surface a ray from just off the decal hits, across every edge whose u/v (in the surface's own frame) passes through the decal's rectangle, onto surfaces within the cull angle of the frame carried over from the surface before; a surface steeper than the clamp angle turns its frame about the shared edge (the seed's about the hit point) until it meets it at the clamp angle, so u/v run on across the fold. At most 128 surfaces. Null when the ray hits none.
	public static List<(int Surface, DecalFrame Frame)> CollisionFill( DecalCollision collision, DecalSystemTag system, DecalFrame start, float halfU, float halfV, float cosCull, float cosClamp, List<int> parents = null )
	{
		if ( collision.Raycast( start.Origin + start.Normal * 0.1f, -start.Normal, 1.1f ) is not { } hit || collision.Excluded( hit.Surface ) )
			return null;

		var reached = new List<(int, DecalFrame)>();
		var hitPoint = start.Origin + start.Normal * (0.1f - hit.T);
		if ( start.Turned( collision.Normals[hit.Surface], hitPoint, null, cosCull, cosClamp ) is not { } first )
			return reached;
		var material = collision.Bsp.Surfaces[hit.Surface].Material;
		var done = new HashSet<int> { hit.Surface };
		var queue = new Queue<(int Surface, DecalFrame Frame)>();
		queue.Enqueue( (hit.Surface, first) );
		var from = new Dictionary<int, int> { [hit.Surface] = -1 };
		while ( queue.Count > 0 )
		{
			var (s, frame) = queue.Dequeue();
			reached.Add( (s, frame) );
			parents?.Add( from[s] );
			if ( (system.Flags & 0x8) != 0 )
				break;
			var corners = collision.Bsp.Surfaces[s].Corners;
			for ( var k = 0; k < corners.Length; k++ )
			{
				int i = corners[k], j = corners[(k + 1) % corners.Length];
				Vector3 p = collision.Bsp.Vertices[i], q = collision.Bsp.Vertices[j];
				var (u0, v0) = frame.Uv( p );
				var (u1, v1) = frame.Uv( q );
				if ( !SegmentInRectangle( u0, v0, u1, v1, halfU, halfV ) )
					continue;
				// The surface across the edge record, as Halo 3 follows it; vertex pairs miss neighbours across split edges.
				var across = collision.Bsp.Surfaces[s].Neighbours;
				foreach ( var n in across is not null ? (across[k] >= 0 ? new[] { across[k] } : Array.Empty<int>()) : collision.Across( i, j ) )
				{
					if ( n == s || done.Contains( n ) || collision.Excluded( n ) || collision.Bsp.Surfaces[n].Material < 0 || done.Count >= 128 )
						continue;
					if ( system.ForcePlanar && Vector3.DistanceSquared( collision.Normals[n], collision.Normals[s] ) > 3e-8f )
						continue;
					if ( (system.Flags & 0x20) != 0 && collision.Bsp.Surfaces[n].Material != material )
						continue;
					if ( frame.Turned( collision.Normals[n], (p + q) / 2, Vector3.Normalize( q - p ), cosCull, cosClamp ) is { } next )
					{
						done.Add( n );
						from[n] = s;
						queue.Enqueue( (n, next) );
					}
				}
			}
		}
		return reached;
	}

	// A triangle's part inside the decal's u/v rectangle (and inside the sides given), its texture coordinates from projecting along the direction.
	void Emit( int t, Vector3 face, Vector3 a, Vector3 b, int group, float halfU, float halfV, Func<Vector3, (float U, float V)> uv, Dictionary<(int, int), List<DecalVertex>> byGroup, IEnumerable<(Vector3 Point, Vector3 Inward)> sides = null )
	{
		var (ia, ib, ic, _, material) = surfaces.Triangles[t];
		var polygon = new List<(DecalVertex Vertex, float U, float V, float H)>( 8 );
		foreach ( var i in new[] { ia, ib, ic } )
		{
			var s = surfaces.Vertices[i];
			var (u, v) = uv( s.Position );
			polygon.Add( (new DecalVertex( s.Position, s.Normal, s.Tangent, s.Binormal, default, s.LightmapTexCoord, s.Light, s.TexCoord, s.Tangent, s.Binormal ), u, v, 0) );
		}

		foreach ( var (point, inward) in sides ?? Enumerable.Empty<(Vector3, Vector3)>() )
			polygon = Clip( polygon, p => Vector3.Dot( p.Vertex.Position - point, inward ) );
		polygon = Clip( polygon, p => p.U + halfU );
		polygon = Clip( polygon, p => halfU - p.U );
		polygon = Clip( polygon, p => p.V + halfV );
		polygon = Clip( polygon, p => halfV - p.V );
		if ( polygon.Count < 3 )
			return;

		if ( !byGroup.TryGetValue( (group, material), out var list ) )
			byGroup[(group, material)] = list = new List<DecalVertex>();

		// The decal's own bump frame: u along the image's right, v down its up, both on the face.
		var tangent = Normalize( a - face * Vector3.Dot( a, face ) );
		var binormal = Normalize( -b + face * Vector3.Dot( b, face ) );
		var points = polygon.Select( p => p.Vertex with
		{
			Position = p.Vertex.Position,
			Normal = Normalize( p.Vertex.Normal ),
			Tangent = tangent,
			Binormal = binormal,
			TexCoord = new Vector2( 0.5f + p.U / (2 * halfU), 0.5f - p.V / (2 * halfV) ),
		} ).ToList();
		_polygons.Add( (group, material, t, points.ToArray()) );
		for ( var i = 1; i + 1 < points.Count; i++ )
		{
			// Slivers left by clipping along a side the triangle lies on.
			if ( Vector3.Cross( points[i].Position - points[0].Position, points[i + 1].Position - points[0].Position ).LengthSquared() < 1e-12f )
				continue;
			list.Add( points[0] );
			list.Add( points[i] );
			list.Add( points[i + 1] );
		}
	}

	// Whether a u/v segment passes through the rectangle |u| <= halfU, |v| <= halfV (Liang-Barsky).
	static bool SegmentInRectangle( float u0, float v0, float u1, float v1, float halfU, float halfV )
	{
		float lo = 0, hi = 1;
		bool Side( float p, float q )
		{
			if ( MathF.Abs( p ) < 1e-12f )
				return q >= 0;
			var r = q / p;
			if ( p < 0 )
				lo = MathF.Max( lo, r );
			else
				hi = MathF.Min( hi, r );
			return lo <= hi;
		}
		var du = u1 - u0;
		var dv = v1 - v0;
		return Side( -du, u0 + halfU ) && Side( du, halfU - u0 ) && Side( -dv, v0 + halfV ) && Side( dv, halfV - v0 );
	}

	static Vector3 Weld( Vector3 p ) => new( MathF.Round( p.X * 512 ) / 512, MathF.Round( p.Y * 512 ) / 512, MathF.Round( p.Z * 512 ) / 512 );

	static bool Less( Vector3 p, Vector3 q ) => p.X != q.X ? p.X < q.X : p.Y != q.Y ? p.Y < q.Y : p.Z < q.Z;

	// Where a ray meets a triangle (either side), along the direction, or null.
	static float? Intersect( Vector3 origin, Vector3 direction, Vector3 a, Vector3 b, Vector3 c )
	{
		var e1 = b - a;
		var e2 = c - a;
		var p = Vector3.Cross( direction, e2 );
		var det = Vector3.Dot( e1, p );
		if ( MathF.Abs( det ) < 1e-12f )
			return null;
		var s = origin - a;
		var u = Vector3.Dot( s, p ) / det;
		if ( u < -1e-5f || u > 1 + 1e-5f )
			return null;
		var q = Vector3.Cross( s, e1 );
		var v = Vector3.Dot( direction, q ) / det;
		if ( v < -1e-5f || u + v > 1 + 1e-5f )
			return null;
		return Vector3.Dot( e2, q ) / det;
	}

	// Sutherland-Hodgman against one plane (inside where the distance is >= 0), interpolating every attribute.
	static List<(DecalVertex Vertex, float U, float V, float H)> Clip( List<(DecalVertex Vertex, float U, float V, float H)> polygon, Func<(DecalVertex Vertex, float U, float V, float H), float> distance )
	{
		if ( polygon.Count < 3 )
			return polygon;
		var output = new List<(DecalVertex, float, float, float)>( polygon.Count + 2 );
		for ( var i = 0; i < polygon.Count; i++ )
		{
			var current = polygon[i];
			var next = polygon[(i + 1) % polygon.Count];
			var dc = distance( current );
			var dn = distance( next );
			if ( dc >= 0 )
				output.Add( current );
			if ( (dc >= 0) != (dn >= 0) )
				// From the same end whichever way the edge is walked, so the triangles either side of it split it at the same point.
				output.Add( Less( current.Vertex.Position, next.Vertex.Position ) ? Lerp( current, next, dc / (dc - dn) ) : Lerp( next, current, dn / (dn - dc) ) );
		}
		return output;
	}

	static (DecalVertex, float, float, float) Lerp( (DecalVertex Vertex, float U, float V, float H) p, (DecalVertex Vertex, float U, float V, float H) q, float t )
	{
		var (a, b) = (p.Vertex, q.Vertex);
		var light = new VertexLighting( Vector3.Lerp( a.Light.Intensity, b.Light.Intensity, t ), Vector3.Lerp( a.Light.C0, b.Light.C0, t ), Vector3.Lerp( a.Light.C1, b.Light.C1, t ),
			Vector3.Lerp( a.Light.C2, b.Light.C2, t ), Vector3.Lerp( a.Light.C3, b.Light.C3, t ) );
		var vertex = new DecalVertex( Vector3.Lerp( a.Position, b.Position, t ), Vector3.Lerp( a.Normal, b.Normal, t ), Vector3.Lerp( a.Tangent, b.Tangent, t ), Vector3.Lerp( a.Binormal, b.Binormal, t ),
			default, Vector2.Lerp( a.LightmapTexCoord, b.LightmapTexCoord, t ), light,
			Vector2.Lerp( a.SurfaceTexCoord, b.SurfaceTexCoord, t ), Vector3.Lerp( a.SurfaceTangent, b.SurfaceTangent, t ), Vector3.Lerp( a.SurfaceBinormal, b.SurfaceBinormal, t ) );
		return (vertex, p.U + (q.U - p.U) * t, p.V + (q.V - p.V) * t, p.H + (q.H - p.H) * t);
	}

	static Vector3 Normalize( Vector3 v ) => v.LengthSquared() > 1e-20f ? Vector3.Normalize( v ) : Vector3.UnitZ;
}

// A decal's projector as MCC carries it from surface to surface: an origin, the image's right (U) and up (V) axes and the normal it projects along (out of the surface); u/v are a point's offsets along the axes from the origin.
public readonly record struct DecalFrame( Vector3 Origin, Vector3 U, Vector3 V, Vector3 Normal )
{
	public (float U, float V) Uv( Vector3 point ) => (Vector3.Dot( point - Origin, U ), Vector3.Dot( point - Origin, V ));

	// This frame on a face (null past the cull angle): as is within the clamp angle, else turned toward the face until it meets it at the clamp angle, about the edge's axis through the pivot (the shared edge stays put) or, without one, the shortest way.
	public DecalFrame? Turned( Vector3 face, Vector3 pivot, Vector3? edge, float cosCull, float cosClamp )
	{
		var facing = Vector3.Dot( face, Normal );
		if ( facing < cosCull )
			return null;
		if ( facing >= cosClamp )
			return this;

		Vector3 axis;
		float angle;
		if ( edge is { } k && k.LengthSquared() > 1e-12f )
		{
			// The turn about k that brings the normal's dot with the face to cos(clamp): A cos t + B sin t + C, with the smallest |t| (else as close as it gets).
			axis = k;
			var along = Vector3.Dot( k, Normal );
			var a = facing - along * Vector3.Dot( k, face );
			var b = Vector3.Dot( Vector3.Cross( k, Normal ), face );
			var c = along * Vector3.Dot( k, face );
			var r = MathF.Sqrt( a * a + b * b );
			var phase = MathF.Atan2( b, a );
			if ( r < 1e-6f )
				return this;
			var x = (cosClamp - c) / r;
			if ( x >= 1 )
				angle = phase;
			else
			{
				var spread = MathF.Acos( Math.Max( x, -1 ) );
				var t1 = Wrap( phase + spread );
				var t2 = Wrap( phase - spread );
				angle = MathF.Abs( t1 ) < MathF.Abs( t2 ) ? t1 : t2;
			}
		}
		else
		{
			axis = Vector3.Cross( Normal, face );
			if ( axis.LengthSquared() < 1e-12f )
				return this;
			axis = Vector3.Normalize( axis );
			angle = MathF.Acos( Math.Clamp( facing, -1, 1 ) ) - MathF.Acos( Math.Clamp( cosClamp, -1, 1 ) );
		}

		var turn = Quaternion.CreateFromAxisAngle( axis, angle );
		return new DecalFrame( pivot + Vector3.Transform( Origin - pivot, turn ), Vector3.Transform( U, turn ), Vector3.Transform( V, turn ), Vector3.Transform( Normal, turn ) );
	}

	static float Wrap( float t ) => t > MathF.PI ? t - 2 * MathF.PI : t < -MathF.PI ? t + 2 * MathF.PI : t;
}
