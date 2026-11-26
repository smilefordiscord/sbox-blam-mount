using System;
using System.Collections.Generic;
using System.Linq;
using Blam.IO;

namespace Blam.Halo1.Tags;

// Inside the namespace so these win over s&box's global types when this compiles into the mount.
using Vector3 = System.Numerics.Vector3;

/// <summary>
/// A plane of a collision BSP: points p on it have dot(Normal, p) = Distance.
/// </summary>
public readonly record struct CollisionPlane( Vector3 Normal, float Distance )
{
	public float DistanceTo( Vector3 point ) => Vector3.Dot( Normal, point ) - Distance;
	public CollisionPlane Flipped => new( -Normal, -Distance );
}

/// <summary>
/// A node of a collision BSP's 3D tree, splitting space by a plane.
/// </summary>
/// <param name="Back">The child behind the plane: a node index, or a leaf (see <see cref="CollisionBsp.IsLeaf"/>).</param>
/// <param name="Front">The child in front of the plane (or on it).</param>
public readonly record struct CollisionBspNode( uint Plane, int Back, int Front );

/// <summary>
/// A collision BSP surface: a convex polygon on one of the BSP's planes.
/// </summary>
/// <param name="Plane">The plane index; <see cref="PlaneFlipped"/> when the surface faces the other way.</param>
/// <param name="Flags">Two-sided 1, invisible 2, climbable 4, breakable 8.</param>
/// <param name="Material">Index into the owning tag's materials (the collision model's, or the structure BSP's), -1 for none.</param>
/// <param name="Corners">Vertex indices in order around the polygon.</param>
public sealed record CollisionBspSurface( int Plane, bool PlaneFlipped, byte Flags, int BreakableSurface, int Material, int[] Corners )
{
	public bool TwoSided => (Flags & 1) != 0;
	public bool Invisible => (Flags & 2) != 0;
}

/// <summary>
/// A collision BSP, as structure BSPs and each node of a collision model have (96 bytes).
/// </summary>
/// <remarks>
/// Blocks: 3D nodes 0x0 (12 bytes: plane, back child, front child), planes 0xC (16 bytes: normal,
/// distance), leaves 0x18 (8 bytes), 2D references 0x24, 2D nodes 0x30, surfaces 0x3C (12 bytes:
/// plane 0x0, first edge 0x4, flags 0x8, breakable surface 0x9, material 0xA), edges 0x48 (24
/// bytes: start/end vertex, forward/reverse edge, left/right surface), vertices 0x54 (16 bytes:
/// point, first edge).
///
/// A 3D node child with its top bit set is a leaf: -1 is solid space (inside the geometry),
/// anything else is a leaf of open space. A point goes to the front child when it's on or in
/// front of the node's plane. Plane indices with the top bit set mean the plane faces the other
/// way.
///
/// Surfaces are stored as a winged edge structure: each edge knows the surfaces on its left and
/// right, and the next edge around each of them, so walking from a surface's first edge gives its
/// corners in order.
/// </remarks>
public sealed class CollisionBsp
{
	public const int Size = 0x60;

	public IReadOnlyList<CollisionBspNode> Nodes { get; }
	public IReadOnlyList<CollisionPlane> Planes { get; }
	public int LeafCount { get; }
	public IReadOnlyList<Vector3> Vertices { get; }
	public IReadOnlyList<CollisionBspSurface> Surfaces { get; }

	public CollisionBsp( MemoryRegion data, uint address )
	{
		Nodes = data.Block( address, 0xC ).Select( n => new CollisionBspNode( data.U32( n ), data.I32( n + 0x4 ), data.I32( n + 0x8 ) ) ).ToList();
		Planes = data.Block( address + 0xC, 0x10 ).Select( p => new CollisionPlane( data.Vec3( p ), data.F32( p + 0xC ) ) ).ToList();
		LeafCount = data.I32( address + 0x18 );
		Vertices = data.Block( address + 0x54, 0x10 ).Select( data.Vec3 ).ToList();

		var edgeCount = data.I32( address + 0x48 );
		var edges = data.U32( address + 0x4C );
		var surfaces = new List<CollisionBspSurface>();
		var index = 0;
		foreach ( var surface in data.Block( address + 0x3C, 0xC ) )
		{
			var corners = new List<int>();
			var first = data.I32( surface + 0x4 );
			var edge = first;

			// A polygon can't have more corners than there are edges; stop on bad data.
			for ( var guard = 0; guard < edgeCount && edge >= 0 && edge < edgeCount; guard++ )
			{
				var e = edges + (uint)(edge * 0x18);
				if ( data.I32( e + 0x10 ) == index )
				{
					corners.Add( data.I32( e ) );
					edge = data.I32( e + 0x8 );
				}
				else
				{
					corners.Add( data.I32( e + 0x4 ) );
					edge = data.I32( e + 0xC );
				}

				if ( edge == first )
					break;
			}

			var plane = data.U32( surface );
			surfaces.Add( new CollisionBspSurface( (int)(plane & 0x7FFFFFFF), (plane & 0x80000000) != 0, data.U8( surface + 0x8 ),
				data.U8( surface + 0x9 ), data.I16( surface + 0xA ), corners.Where( c => c >= 0 && c < Vertices.Count ).ToArray() ) );
			index++;
		}
		Surfaces = surfaces;
	}

	/// <summary>
	/// Whether a 3D node child is a leaf rather than another node.
	/// </summary>
	public static bool IsLeaf( int child ) => child < 0;

	/// <summary>
	/// Whether a 3D node child is solid space.
	/// </summary>
	public static bool IsSolid( int child ) => child == -1;

	/// <summary>
	/// The plane a node or surface plane index refers to, flipped if its top bit is set.
	/// </summary>
	public CollisionPlane Plane( uint designator )
	{
		var index = (int)(designator & 0x7FFFFFFF);
		var plane = index < Planes.Count ? Planes[index] : new CollisionPlane( Vector3.UnitZ, 0 );
		return (designator & 0x80000000) != 0 ? plane.Flipped : plane;
	}

	/// <summary>
	/// Whether a point is inside the geometry (in solid space).
	/// </summary>
	public bool Contains( Vector3 point )
	{
		if ( Nodes.Count == 0 )
			return false;

		var child = 0;
		for ( var guard = 0; guard <= Nodes.Count && !IsLeaf( child ) && child < Nodes.Count; guard++ )
		{
			var node = Nodes[child];
			child = Plane( node.Plane ).DistanceTo( point ) >= 0 ? node.Front : node.Back;
		}

		return IsSolid( child );
	}

	/// <summary>
	/// The surfaces as triangle fans, in the surfaces' own winding.
	/// </summary>
	/// <param name="surfaces">Receives the surface index of each triangle, if not null.</param>
	public List<int> Triangles( List<int> surfaces = null )
	{
		var indices = new List<int>();
		for ( var s = 0; s < Surfaces.Count; s++ )
		{
			var corners = Surfaces[s].Corners;
			for ( var i = 1; i + 1 < corners.Length; i++ )
			{
				indices.Add( corners[0] );
				indices.Add( corners[i] );
				indices.Add( corners[i + 1] );
				surfaces?.Add( s );
			}
		}

		return indices;
	}

	/// <summary>
	/// Whether the geometry is one convex solid: every vertex is on or behind every surface's
	/// plane (surfaces face out of the solid).
	/// </summary>
	public bool IsConvex( float tolerance = 1e-4f )
	{
		if ( Surfaces.Count < 4 || Surfaces.Any( s => s.TwoSided ) )
			return false;

		foreach ( var surface in Surfaces )
		{
			var plane = Plane( (uint)surface.Plane | (surface.PlaneFlipped ? 0x80000000 : 0) );
			if ( Vertices.Any( v => plane.DistanceTo( v ) > tolerance ) )
				return false;
		}

		return true;
	}

	/// <summary>
	/// Convex pieces that together fill the geometry, each as its corner points: all the vertices
	/// if the geometry is convex and its hull within <paramref name="maximumHalfEdges"/>, its
	/// <see cref="SolidCells"/> otherwise.
	/// </summary>
	/// <param name="maximumHalfEdges">The most half-edges a piece may have (see <see cref="SolidCells"/>).</param>
	public List<Vector3[]> ConvexPieces( int maximumHalfEdges = int.MaxValue ) =>
		IsConvex() && ConvexHalfEdges() <= maximumHalfEdges ? [Vertices.ToArray()] : SolidCells( maximumHalfEdges );

	/// <summary>
	/// The half-edges of a convex geometry's hull with its faces as triangles (by Euler's formula,
	/// three edges per vertex less six, two half-edges each).
	/// </summary>
	int ConvexHalfEdges() => 6 * Vertices.Count - 12;

	/// <summary>
	/// What a convex piece of the geometry is mostly made of: of the surfaces on its boundary (on a
	/// plane it has three or more corners on and is wholly behind, and overlapping its bounds), the
	/// group with the most area, or the nearest surface's group if none is.
	/// </summary>
	/// <param name="group">Maps a surface's material index to what's counted (its material type, say).</param>
	public int DominantMaterial( Vector3[] piece, Func<int, int> group, float tolerance = 1e-3f )
	{
		var min = piece.Aggregate( Vector3.Min ) - new Vector3( tolerance );
		var max = piece.Aggregate( Vector3.Max ) + new Vector3( tolerance );
		var center = piece.Aggregate( Vector3.Zero, ( s, p ) => s + p ) / piece.Length;
		var areas = new Dictionary<int, float>();
		var nearest = (Distance: float.MaxValue, Group: -1);

		foreach ( var surface in Surfaces )
		{
			if ( surface.Corners.Length < 3 )
				continue;

			var corners = surface.Corners.Select( c => Vertices[c] ).ToArray();
			var centroid = corners.Aggregate( Vector3.Zero, ( s, p ) => s + p ) / corners.Length;
			var key = group( surface.Material );
			var distance = Vector3.DistanceSquared( centroid, center );
			if ( distance < nearest.Distance )
				nearest = (distance, key);

			// On the piece's boundary: the piece lies behind the surface's plane, touching it.
			var plane = Plane( (uint)surface.Plane | (surface.PlaneFlipped ? 0x80000000 : 0) );
			var on = 0;
			var behind = true;
			foreach ( var p in piece )
			{
				var d = plane.DistanceTo( p );
				if ( d > tolerance )
				{
					behind = false;
					break;
				}

				if ( d >= -tolerance )
					on++;
			}

			var surfaceMin = corners.Aggregate( Vector3.Min );
			var surfaceMax = corners.Aggregate( Vector3.Max );
			if ( !behind || on < 3 )
				continue;
			if ( surfaceMin.X > max.X || surfaceMin.Y > max.Y || surfaceMin.Z > max.Z || surfaceMax.X < min.X || surfaceMax.Y < min.Y || surfaceMax.Z < min.Z )
				continue;

			var area = 0f;
			for ( var i = 1; i + 1 < corners.Length; i++ )
				area += Vector3.Cross( corners[i] - corners[0], corners[i + 1] - corners[0] ).Length() / 2;
			areas[key] = areas.GetValueOrDefault( key ) + area;
		}

		return areas.Count > 0 ? areas.MaxBy( a => a.Value ).Key : nearest.Group;
	}

	/// <summary>
	/// Splits the solid space into convex pieces: the cells of the 3D tree that are solid, each
	/// as its corner points. Together they fill the geometry exactly (the tree's planes are the
	/// surfaces' planes), so they work as convex collision hulls.
	/// </summary>
	/// <param name="maximumHalfEdges">The most half-edges a piece may have with its faces as
	/// triangles; bigger cells are cut in two, across their longest side, until no piece has more.</param>
	/// <param name="minimumThickness">Leaves out slivers whose <see cref="ConvexCell.Thickness"/> is
	/// under this (world units). Modelled geometry is thicker: c_field_generator's shield is a shell
	/// 0.0023 thick, its cells 0.00115 by that measure.</param>
	public List<Vector3[]> SolidCells( int maximumHalfEdges = int.MaxValue, float minimumThickness = 0.0001f ) =>
		ConvexCell.SolidCells( Nodes.Select( n => (Plane( n.Plane ), n.Back, n.Front) ).ToList(), Vertices, maximumHalfEdges, minimumThickness );
}

/// <summary>
/// A convex polyhedron as its faces, cut down plane by plane (see <see cref="CollisionBsp.SolidCells"/>).
/// </summary>
sealed class ConvexCell
{
	const float Epsilon = 1e-5f;

	readonly List<Vector3[]> _faces;

	ConvexCell( List<Vector3[]> faces ) => _faces = faces;

	// The solid cells of a collision BSP's 3D tree (see CollisionBsp.SolidCells): nodes are [plane, back, front], children below 0 leaves, -1 solid; on or in front of a plane goes front.
	// Every Halo game's collision BSPs are built this way.
	// ConvexPieces is CollisionBsp.ConvexPieces for any of them: surfacePlanes are each surface's outward plane, null for a two-sided one.
	public static List<Vector3[]> ConvexPieces( IReadOnlyList<(CollisionPlane Plane, int Back, int Front)> nodes, IReadOnlyList<Vector3> vertices,
		IReadOnlyList<CollisionPlane?> surfacePlanes, int maximumHalfEdges, float minimumThickness = 0.0001f )
	{
		const float tolerance = 1e-4f;
		var convex = surfacePlanes.Count >= 4 && surfacePlanes.All( p => p is { } plane && vertices.All( v => plane.DistanceTo( v ) <= tolerance ) );
		return convex && 6 * vertices.Count - 12 <= maximumHalfEdges ? [vertices.ToArray()] : SolidCells( nodes, vertices, maximumHalfEdges, minimumThickness );
	}

	public static List<Vector3[]> SolidCells( IReadOnlyList<(CollisionPlane Plane, int Back, int Front)> nodes, IReadOnlyList<Vector3> vertices, int maximumHalfEdges, float minimumThickness )
	{
		var cells = new List<Vector3[]>();
		if ( nodes.Count == 0 || vertices.Count == 0 )
			return cells;

		// Cells on the outside of the tree are unbounded; the geometry's bounds close them off.
		var min = vertices.Aggregate( Vector3.Min );
		var max = vertices.Aggregate( Vector3.Max );
		var box = Box( min - new Vector3( 0.001f ), max + new Vector3( 0.001f ) );

		var stack = new Stack<(int Child, ConvexCell Cell, int Depth)>();
		stack.Push( (0, box, 0) );
		while ( stack.Count > 0 )
		{
			var (child, cell, depth) = stack.Pop();
			if ( child < 0 )
			{
				if ( child == -1 && cell.Thickness() >= minimumThickness )
					cells.AddRange( cell.Split( maximumHalfEdges ).Select( c => c.Points() ) );
				continue;
			}

			// A tree deeper than it has nodes loops; stop on bad data.
			if ( child >= nodes.Count || depth > nodes.Count )
				continue;

			var (plane, backChild, frontChild) = nodes[child];
			if ( cell.Clip( plane ) is { } front )
				stack.Push( (frontChild, front, depth + 1) );
			if ( cell.Clip( plane.Flipped ) is { } back )
				stack.Push( (backChild, back, depth + 1) );
		}

		return cells;
	}

	public static ConvexCell Box( Vector3 min, Vector3 max )
	{
		Vector3 P( int i ) => new( (i & 1) != 0 ? max.X : min.X, (i & 2) != 0 ? max.Y : min.Y, (i & 4) != 0 ? max.Z : min.Z );
		int[][] faces = [[0, 2, 3, 1], [4, 5, 7, 6], [0, 1, 5, 4], [2, 6, 7, 3], [0, 4, 6, 2], [1, 3, 7, 5]];
		return new ConvexCell( faces.Select( f => f.Select( P ).ToArray() ).ToList() );
	}

	/// <summary>
	/// The part of the cell on or in front of a plane, or null if nothing of it with any volume is.
	/// </summary>
	public ConvexCell Clip( CollisionPlane plane )
	{
		var inside = false;
		var outside = false;
		foreach ( var face in _faces )
		{
			foreach ( var p in face )
			{
				var d = plane.DistanceTo( p );
				inside |= d > Epsilon;
				outside |= d < -Epsilon;
			}
		}

		if ( !inside )
			return null;
		if ( !outside )
			return this;

		var faces = new List<Vector3[]>( _faces.Count + 1 );
		var cap = new List<Vector3>();
		var polygon = new List<Vector3>();
		foreach ( var face in _faces )
		{
			polygon.Clear();
			for ( var i = 0; i < face.Length; i++ )
			{
				var a = face[i];
				var b = face[(i + 1) % face.Length];
				var da = plane.DistanceTo( a );
				var db = plane.DistanceTo( b );

				if ( da >= -Epsilon )
				{
					polygon.Add( a );
					if ( da <= Epsilon )
						cap.Add( a );
				}

				if ( (da > Epsilon && db < -Epsilon) || (da < -Epsilon && db > Epsilon) )
				{
					var p = a + (b - a) * (da / (da - db));
					polygon.Add( p );
					cap.Add( p );
				}
			}

			if ( polygon.Count >= 3 )
				faces.Add( polygon.ToArray() );
		}

		if ( OrderAround( cap, plane.Normal ) is { Length: >= 3 } capFace )
			faces.Add( capFace );

		return faces.Count >= 4 ? new ConvexCell( faces ) : null;
	}

	/// <summary>
	/// The cell's half-edges with its faces as triangles: three per triangle.
	/// </summary>
	public int HalfEdges => _faces.Sum( f => 3 * (f.Length - 2) );

	/// <summary>
	/// The cell cut in two across the middle of its longest side, and the halves again, until no
	/// piece has more than <paramref name="maximumHalfEdges"/>. The pieces fill the cell exactly.
	/// </summary>
	public List<ConvexCell> Split( int maximumHalfEdges )
	{
		var pieces = new List<ConvexCell>();
		var stack = new Stack<(ConvexCell Cell, int Depth)>();
		stack.Push( (this, 0) );
		while ( stack.Count > 0 )
		{
			var (cell, depth) = stack.Pop();

			// Halving a cell 16 times leaves pieces a 65536th of its size; stop on bad data.
			if ( cell.HalfEdges <= maximumHalfEdges || depth >= 16 )
			{
				pieces.Add( cell );
				continue;
			}

			var corners = cell.Points();
			var min = corners.Aggregate( Vector3.Min );
			var max = corners.Aggregate( Vector3.Max );
			var size = max - min;
			var axis = size.X >= size.Y && size.X >= size.Z ? Vector3.UnitX : size.Y >= size.Z ? Vector3.UnitY : Vector3.UnitZ;
			var cut = new CollisionPlane( axis, Vector3.Dot( axis, (min + max) / 2 ) );
			if ( cell.Clip( cut ) is { } front )
				stack.Push( (front, depth + 1) );
			if ( cell.Clip( cut.Flipped ) is { } back )
				stack.Push( (back, depth + 1) );
		}

		return pieces;
	}

	/// <summary>
	/// The cell's distinct corners.
	/// </summary>
	public Vector3[] Points()
	{
		var points = new List<Vector3>();
		foreach ( var face in _faces )
		{
			foreach ( var p in face )
			{
				if ( !points.Any( q => Vector3.DistanceSquared( p, q ) < Epsilon * Epsilon ) )
					points.Add( p );
			}
		}

		return points.ToArray();
	}

	/// <summary>
	/// About how thick the cell is: three times its volume over its surface area (the radius of a
	/// sphere, half the thickness of a slab).
	/// </summary>
	public float Thickness()
	{
		var center = Vector3.Zero;
		var count = 0;
		foreach ( var face in _faces )
		{
			foreach ( var p in face )
			{
				center += p;
				count++;
			}
		}
		center /= Math.Max( count, 1 );

		float volume = 0, area = 0;
		foreach ( var face in _faces )
		{
			for ( var i = 1; i + 1 < face.Length; i++ )
			{
				var cross = Vector3.Cross( face[i] - face[0], face[i + 1] - face[0] );
				area += cross.Length() / 2;
				volume += MathF.Abs( Vector3.Dot( cross, face[0] - center ) ) / 6;
			}
		}

		return area > 0 ? 3 * volume / area : 0;
	}

	/// <summary>
	/// Points on a plane in order around their center, duplicates removed.
	/// </summary>
	static Vector3[] OrderAround( List<Vector3> points, Vector3 normal )
	{
		var unique = new List<Vector3>();
		foreach ( var p in points )
		{
			if ( !unique.Any( q => Vector3.DistanceSquared( p, q ) < Epsilon * Epsilon ) )
				unique.Add( p );
		}

		if ( unique.Count < 3 )
			return null;

		var center = unique.Aggregate( Vector3.Zero, ( s, p ) => s + p ) / unique.Count;
		var u = Vector3.Normalize( Vector3.Cross( normal, MathF.Abs( normal.X ) < 0.9f ? Vector3.UnitX : Vector3.UnitY ) );
		var v = Vector3.Cross( normal, u );
		return unique.OrderBy( p => MathF.Atan2( Vector3.Dot( p - center, v ), Vector3.Dot( p - center, u ) ) ).ToArray();
	}
}
