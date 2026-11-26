using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;

namespace Blam.Halo3.Tags;

// Inside the namespace so these win over s&box's global Vector types when this compiles into the mount.
using Vector3 = System.Numerics.Vector3;

// A surface of a collision BSP: a convex polygon, its corners counter-clockwise around the way it faces.
// Neighbours[k] is the surface across the edge from corner k to the next (from the edge record, -1 for none).
public sealed record CollisionSurface( int Plane, bool PlaneFlipped, int Flags, int Material, int[] Corners, int[] Neighbours = null )
{
	// Flags: two-sided 1, invisible 2, climbable 4, breakable 8, invalid 0x10, conveyor 0x20, slip 0x40.
	public bool TwoSided => (Flags & 1) != 0;
	public bool Invisible => (Flags & 2) != 0;
	public bool Breakable => (Flags & 8) != 0;
}

// A Halo 3 collision BSP (0x60 = 8 blocks): in the structure BSP's tag resources (arrays in page data), in each instanced geometry definition, and per permutation in coll tags (geometry.md §2.4).
// Blocks: 3D nodes 8 (i16 plane, back and front child as signed 24-bit), planes 16, leaves 8, 2D refs 4, 2D nodes 16, surfaces 12, edges 12, vertices 16.
public sealed class CollisionBsp
{
	public const int Size = 0x60;
	static readonly int[] ElementSizes = [8, 16, 8, 4, 16, 12, 12, 16];

	readonly (int Plane, int Back, int Front)[] _nodes;

	// The 3D nodes: plane (0x8000 flips it), back and front child (-1 solid, other negatives leaf index | 0x800000).
	public IReadOnlyList<(int Plane, int Back, int Front)> Nodes => _nodes;

	// How many leaves the tree's leaf block holds.
	public int LeafCount { get; }

	public IReadOnlyList<(Vector3 Normal, float Distance)> Planes { get; }
	public IReadOnlyList<Vector3> Vertices { get; }
	public IReadOnlyList<CollisionSurface> Surfaces { get; }

	CollisionBsp( Func<int, int, ReadOnlyMemory<byte>> block )
	{
		var nodes = block( 0, 8 ).Span;
		_nodes = new (int, int, int)[nodes.Length / 8];
		for ( var i = 0; i < _nodes.Length; i++ )
		{
			var n = nodes[(i * 8)..];
			_nodes[i] = (BinaryPrimitives.ReadInt16LittleEndian( n ), Child24( n[2..] ), Child24( n[5..] ));
		}

		LeafCount = block( 2, 8 ).Length / 8;

		var planeData = block( 1, 16 ).Span;
		var planes = new (Vector3, float)[planeData.Length / 16];
		for ( var i = 0; i < planes.Length; i++ )
			planes[i] = (new Vector3( F32( planeData, i * 16 ), F32( planeData, i * 16 + 4 ), F32( planeData, i * 16 + 8 ) ), F32( planeData, i * 16 + 12 ));
		Planes = planes;

		var vertexData = block( 7, 16 ).Span;
		var vertices = new Vector3[vertexData.Length / 16];
		for ( var i = 0; i < vertices.Length; i++ )
			vertices[i] = new Vector3( F32( vertexData, i * 16 ), F32( vertexData, i * 16 + 4 ), F32( vertexData, i * 16 + 8 ) );
		Vertices = vertices;

		var edges = block( 6, 12 ).Span;
		var edgeCount = edges.Length / 12;
		var surfaceData = block( 5, 12 ).Span;
		var surfaces = new CollisionSurface[surfaceData.Length / 12];
		var surfaceCount = surfaceData.Length / 12;
		var corners = new List<int>();
		var neighbours = new List<int>();
		for ( var s = 0; s < surfaces.Length; s++ )
		{
			var surface = surfaceData[(s * 12)..];

			// Winged-edge walk: an edge with the surface on its left goes start to end and continues forward, else end to start and continues reverse.
			corners.Clear();
			neighbours.Clear();
			int first = U16( surface, 2 ), edge = first;
			for ( var guard = 0; guard < edgeCount && edge < edgeCount; guard++ )
			{
				var e = edges[(edge * 12)..];
				if ( U16( e, 8 ) == s )
				{
					corners.Add( U16( e, 0 ) );
					neighbours.Add( U16( e, 10 ) );
					edge = U16( e, 4 );
				}
				else
				{
					corners.Add( U16( e, 2 ) );
					neighbours.Add( U16( e, 8 ) );
					edge = U16( e, 6 );
				}

				if ( edge == first )
					break;
			}

			for ( var k = corners.Count - 1; k >= 0; k-- )
			{
				if ( corners[k] >= vertices.Length )
				{
					corners.RemoveAt( k );
					neighbours.RemoveAt( k );
				}
			}
			var plane = U16( surface, 0 );
			surfaces[s] = new CollisionSurface( plane & 0x7FFF, (plane & 0x8000) != 0, surface[10], BinaryPrimitives.ReadInt16LittleEndian( surface[4..] ), corners.ToArray(),
				neighbours.Select( n => n == 0xFFFF || n >= surfaceCount ? -1 : n ).ToArray() );
		}
		Surfaces = surfaces;
	}

	// A collision BSP in resource control data, its arrays in page data.
	public static CollisionBsp FromResource( ResourceData data, int offset ) => new( ( k, size ) =>
	{
		var count = data.I32( offset + 12 * k );
		if ( count <= 0 )
			return ReadOnlyMemory<byte>.Empty;
		var target = data.Target( offset + 12 * k + 4 );
		return target[..Math.Min( target.Length, count * size )];
	} );

	// A collision BSP in tag data (coll tags), as ordinary tag blocks.
	public static CollisionBsp FromTag( TagView view ) => new( ( k, size ) =>
	{
		var count = view.I32( 12 * k );
		var pointer = view.U32( 12 * k + 4 );
		if ( count <= 0 || pointer == 0 )
			return ReadOnlyMemory<byte>.Empty;
		return view.Cache.File.Read( view.Cache.PointerOffset( pointer ), count * size );
	} );

	public static int ElementSize( int block ) => ElementSizes[block];

	public int NodeCount => _nodes.Length;

	// Whether a point is in solid space: children are [plane, back, front], -1 solid, other negatives leaves; on or in front of a plane goes front.
	public bool Contains( Vector3 point )
	{
		if ( _nodes.Length == 0 )
			return false;

		var child = 0;
		for ( var guard = 0; guard <= _nodes.Length && child >= 0 && child < _nodes.Length; guard++ )
		{
			var (plane, back, front) = _nodes[child];
			var index = plane & 0x7FFF;
			var (normal, distance) = index < Planes.Count ? Planes[index] : (Vector3.UnitZ, 0f);
			var d = Vector3.Dot( normal, point ) - distance;
			if ( (plane & 0x8000) != 0 )
				d = -d;
			child = d >= 0 ? front : back;
		}

		return child == -1;
	}

	// Convex pieces that fill the geometry, each as its corner points, as Halo CE's (Blam.Halo1.Tags.CollisionBsp.ConvexPieces).
	public List<Vector3[]> ConvexPieces( int maximumHalfEdges = int.MaxValue )
	{
		Halo1.Tags.CollisionPlane PlaneOf( int index, bool flipped )
		{
			var (normal, distance) = index < Planes.Count ? Planes[index] : (Vector3.UnitZ, 0f);
			return flipped ? new( -normal, -distance ) : new( normal, distance );
		}

		var nodes = _nodes.Select( n => (PlaneOf( n.Plane & 0x7FFF, (n.Plane & 0x8000) != 0 ), n.Back, n.Front) ).ToList();
		var surfaces = Surfaces.Select( s => s.TwoSided ? null : (Halo1.Tags.CollisionPlane?)PlaneOf( s.Plane, s.PlaneFlipped ) ).ToList();
		return Halo1.Tags.ConvexCell.ConvexPieces( nodes, Vertices, surfaces, maximumHalfEdges );
	}

	// The surfaces as triangle fans in their own winding, as vertex index triples; surfaces receives each triangle's surface.
	public List<int> Triangles( List<CollisionSurface> surfaces = null )
	{
		var indices = new List<int>();
		foreach ( var surface in Surfaces )
		{
			var corners = surface.Corners;
			for ( var i = 1; i + 1 < corners.Length; i++ )
			{
				indices.Add( corners[0] );
				indices.Add( corners[i] );
				indices.Add( corners[i + 1] );
				surfaces?.Add( surface );
			}
		}

		return indices;
	}

	static int Child24( ReadOnlySpan<byte> b )
	{
		var value = b[0] | b[1] << 8 | b[2] << 16;
		return (value & 0x800000) != 0 ? value - 0x1000000 : value;
	}

	static int U16( ReadOnlySpan<byte> b, int offset ) => BinaryPrimitives.ReadUInt16LittleEndian( b[offset..] );

	static float F32( ReadOnlySpan<byte> b, int offset ) => BinaryPrimitives.ReadSingleLittleEndian( b[offset..] );
}
