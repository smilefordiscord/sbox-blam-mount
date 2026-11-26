using System.Collections.Generic;
using System.Linq;
using Blam.IO;

namespace Blam.Halo2.Tags;

// Inside the namespace so these win over s&box's global vector types when this compiles into the mount.
using Vector3 = System.Numerics.Vector3;

/// <summary>
/// A surface of a Halo 2 collision BSP: a convex polygon, its corners in order (counter-clockwise around the
/// way it faces).
/// </summary>
/// <param name="Flags">Two-sided 1, invisible 2, climbable 4, breakable 8, invalid 0x10, conveyor 0x20.</param>
/// <param name="Material">Index into the owner's collision materials (the structure BSP's), -1 for none.</param>
public sealed record CollisionSurface( int Plane, bool PlaneFlipped, byte Flags, int Material, int[] Corners )
{
	public bool TwoSided => (Flags & 1) != 0;
	public bool Invisible => (Flags & 2) != 0;
	public bool Breakable => (Flags & 8) != 0;
}

/// <summary>
/// A Halo 2 collision BSP (0x40 bytes): the structure BSP has one, each instanced geometry definition another
/// (in its own space), and collision models one per permutation.
/// </summary>
/// <remarks>
/// Same winged edge structure as Halo CE's (Blam.Halo1.Tags.CollisionBsp), with narrower fields. Blocks:
/// 3D nodes 0x00 (8 bytes: int16 plane, then the back and front child as signed 24-bit numbers), planes 0x08
/// (16: normal, distance), leaves 0x10 (4), 2D references 0x18 (4), 2D nodes 0x20 (16), surfaces 0x28 (8:
/// int16 plane whose top bit flips it, int16 first edge, flags, breakable surface, int16 material), edges 0x30
/// (12: int16 start and end vertex, forward and reverse edge, left and right surface), vertices 0x38 (16: point,
/// int16 first edge).
///
/// A child of -1 is solid space and other negative children are leaves of open space; a point goes to the front
/// child when it's on or in front of the plane. The order is [plane, back, front] as in Halo CE: read the other
/// way round, every player spawn point of Lockout lands in solid space.
/// </remarks>
public sealed class CollisionBsp
{
	public const int Size = 0x40;

	readonly (int Plane, int Back, int Front)[] _nodes;

	// The 3D nodes: plane, back and front child (-1 solid, other negatives leaf index | 0x800000).
	public IReadOnlyList<(int Plane, int Back, int Front)> Nodes => _nodes;

	public IReadOnlyList<(Vector3 Normal, float Distance)> Planes { get; }
	public IReadOnlyList<Vector3> Vertices { get; }
	public IReadOnlyList<CollisionSurface> Surfaces { get; }

	public CollisionBsp( MemoryRegion data, uint address )
	{
		_nodes = data.Elements( address, 8 ).Select( n => ((int)data.I16( n ), Child24( data, n + 2 ), Child24( data, n + 5 )) ).ToArray();
		Planes = data.Elements( address + 0x08, 0x10 ).Select( p => (data.Vec3( p ), data.F32( p + 0xC )) ).ToList();
		Vertices = data.Elements( address + 0x38, 0x10 ).Select( data.Vec3 ).ToList();

		var edgeCount = data.Count( address + 0x30 );
		var edges = data.U32( address + 0x34 );
		var surfaces = new List<CollisionSurface>();
		var index = 0;
		foreach ( var surface in data.Elements( address + 0x28, 8 ) )
		{
			// Walk the edges around the surface: an edge with the surface on its left goes start to end and
			// continues forward, one with it on its right the other way round.
			var corners = new List<int>();
			var first = data.I16( surface + 2 );
			var edge = (int)first;
			for ( var guard = 0; guard < edgeCount && edge >= 0 && edge < edgeCount; guard++ )
			{
				var e = edges + (uint)(edge * 12);
				if ( data.I16( e + 8 ) == index )
				{
					corners.Add( data.I16( e ) );
					edge = data.I16( e + 4 );
				}
				else
				{
					corners.Add( data.I16( e + 2 ) );
					edge = data.I16( e + 6 );
				}

				if ( edge == first )
					break;
			}

			var plane = data.U16( surface );
			surfaces.Add( new CollisionSurface( plane & 0x7FFF, (plane & 0x8000) != 0, data.U8( surface + 4 ), data.I16( surface + 6 ),
				corners.Where( c => c >= 0 && c < Vertices.Count ).ToArray() ) );
			index++;
		}
		Surfaces = surfaces;
	}

	static int Child24( MemoryRegion data, uint address )
	{
		var value = data.U8( address ) | data.U8( address + 1 ) << 8 | data.U8( address + 2 ) << 16;
		return (value & 0x800000) != 0 ? value - 0x1000000 : value;
	}

	/// <summary>
	/// Whether a point is inside the geometry (in solid space).
	/// </summary>
	public bool Contains( Vector3 point )
	{
		if ( _nodes.Length == 0 )
			return false;

		var child = 0;
		for ( var guard = 0; guard <= _nodes.Length && child >= 0 && child < _nodes.Length; guard++ )
		{
			var (plane, back, front) = _nodes[child];
			var (normal, distance) = plane >= 0 && plane < Planes.Count ? Planes[plane] : (Vector3.UnitZ, 0f);
			child = Vector3.Dot( normal, point ) - distance >= 0 ? front : back;
		}

		return child == -1;
	}

	// Convex pieces that fill the geometry, each as its corner points, as Halo CE's (Blam.Halo1.Tags.CollisionBsp.ConvexPieces).
	public List<Vector3[]> ConvexPieces( int maximumHalfEdges = int.MaxValue )
	{
		Halo1.Tags.CollisionPlane PlaneOf( int index, bool flipped )
		{
			var (normal, distance) = index >= 0 && index < Planes.Count ? Planes[index] : (Vector3.UnitZ, 0f);
			return flipped ? new( -normal, -distance ) : new( normal, distance );
		}

		var nodes = _nodes.Select( n => (PlaneOf( n.Plane, false ), n.Back, n.Front) ).ToList();
		var surfaces = Surfaces.Select( s => s.TwoSided ? null : (Halo1.Tags.CollisionPlane?)PlaneOf( s.Plane, s.PlaneFlipped ) ).ToList();
		return Halo1.Tags.ConvexCell.ConvexPieces( nodes, Vertices, surfaces, maximumHalfEdges );
	}

	/// <summary>
	/// The surfaces as triangle fans, in the surfaces' own winding, as vertex index triples.
	/// </summary>
	/// <param name="surfaces">Receives the surface of each triangle, if not null.</param>
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
}
