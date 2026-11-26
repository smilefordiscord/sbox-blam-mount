using System;
using System.Collections.Generic;
using System.Linq;
using Blam.IO;

namespace Blam.Halo2.Tags;

// Inside the namespace so these win over s&box's global vector types when this compiles into the mount.
using Quaternion = System.Numerics.Quaternion;
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;

public enum PhysicsModelMotion
{
	Sphere,
	StabilizedSphere,
	Box,
	StabilizedBox,
	Keyframed,
	Fixed,
}

public enum PhysicsModelShapeKind
{
	Sphere,
	Pill,
	Box,
	Hull,
	Triangle,
}

// One convex piece of a rigid body or phantom, in its node's space (world units).
// A sphere has Center and Radius, a pill Bottom, Top and Radius, a box Center, Rotation and HalfExtents, a hull or triangle Points.
// ConvexRadius is the shell Havok keeps around a box, hull or triangle.
// Material is the global material name; PhantomType indexes the phantom types, -1 for a solid shape.
public sealed record PhysicsModelShape( PhysicsModelShapeKind Kind, string Name, string Material, float Friction, float Restitution, int PhantomType,
	Vector3 Center, Quaternion Rotation, Vector3 HalfExtents, float Radius, Vector3 Bottom, Vector3 Top, IReadOnlyList<Vector3> Points, float ConvexRadius );

// A rigid body on a node: its solid shapes and the phantoms in it, mass, centre of mass and inertia tensor rows in the node's space.
// The tensor already has the body's inertia tensor scale in it (hanging_beam's beam: half a solid box's).
// Default: the body is in its region's first permutation (or in no region), the one a new object has.
// Flags are the tag's rigid body flags (TagLayouts.RigidBodyFlags).
public sealed record PhysicsModelBody( int Index, int Node, PhysicsModelMotion Motion, float Mass, Vector3 CenterOfMass, Vector3 InertiaX,
	Vector3 InertiaY, Vector3 InertiaZ, float LinearDamping, float AngularDamping, IReadOnlyList<PhysicsModelShape> Shapes,
	IReadOnlyList<int> Phantoms, bool Default, int Flags );

public enum PhysicsModelConstraintKind
{
	Hinge,
	LimitedHinge,
	Ragdoll,
	StiffSpring,
	BallAndSocket,
	Prismatic,
}

// A constraint's frame in its node's space: Havok's axis is Forward.
public sealed record PhysicsModelConstraintFrame( Vector3 Forward, Vector3 Left, Vector3 Up, Vector3 Position );

// A constraint between two nodes; NodeB -1 holds node A to the object itself.
// Limit is a limited hinge's angle, a ragdoll's twist or a prismatic's travel; Cone and Plane are a ragdoll's.
// Angles are in degrees, as the maps store them. Rigid: its node edge marks it rigid.
// Friction is a limited hinge's or ragdoll's node edge friction where that isn't 0, else its own; a prismatic's is its own.
public sealed record PhysicsModelConstraint( PhysicsModelConstraintKind Kind, string Name, int NodeA, int NodeB, PhysicsModelConstraintFrame A, PhysicsModelConstraintFrame B,
	bool Rigid, Vector2 Limit, Vector2 Cone, Vector2 Plane, float Friction, float SpringLength );

// A phantom: a shape that doesn't collide but acts on what is inside it (gravity lifts), and the rigid body it's in (-1 for none).
public sealed record PhysicsModelPhantom( int Index, int Body, PhysicsModelShape Shape );

// A Halo 2 physics model (phmo): the rigid bodies Halo gives an object, their shapes, phantoms and constraints.
// Polyhedra keep up to 3 four vectors (4 vertices each, as x, y and z rows) in place and more in the four vectors block, one after another.
// Lists keep up to 4 children in place and more in the list shapes block the same way.
public sealed class PhysicsModelTag
{
	public static readonly TagClass Class = new( "phmo" );

	const int RigidBodySize = 0xA0;
	const int PolyhedronInlineFourVectors = 3;
	const int ListInlineChildren = 4;

	enum ShapeType
	{
		Sphere = 0,
		Pill = 1,
		Box = 2,
		Triangle = 3,
		Polyhedron = 4,
		MultiSphere = 5,
		Phantom = 6,
		List = 14,
		Mopp = 15,
	}

	readonly MemoryRegion _data;
	readonly CacheFile _cache;
	readonly uint _address;
	readonly List<(string Name, int PhantomType)> _materials;
	readonly uint[] _fourVectors;
	readonly uint[] _listChildren;
	readonly Dictionary<int, int> _phantomBodies = new();

	public TagEntry Tag { get; }
	public IReadOnlyList<string> Nodes { get; }
	public IReadOnlyList<PhysicsModelBody> RigidBodies { get; }
	public IReadOnlyList<PhysicsModelConstraint> Constraints { get; }
	public IReadOnlyList<PhysicsModelPhantom> Phantoms { get; }

	// Shapes Halo has but the reader doesn't (none in the multiplayer maps' crates).
	public int Unsupported { get; private set; }

	public PhysicsModelTag( TagEntry tag )
	{
		Tag = tag;
		_cache = tag.Cache;
		_data = tag.Cache.TagData;
		_address = tag.Address;
		var data = _data;
		var a = _address;

		Nodes = data.Elements( a + 0xC8, 0xC ).Select( n => _cache.GetString( data.U32( n ) ) ?? "" ).ToList();
		_materials = data.Elements( a + 0x40, 0xC )
			.Select( m => (Name: MaterialName( m ), PhantomType: (int)data.I16( m + 0x8 )) )
			.ToList();

		_fourVectors = Spill( a + 0x70, 0x120, 0x68, PolyhedronInlineFourVectors, a + 0x78, 0x30 );
		_listChildren = Spill( a + 0x90, 0x68, 0x20, ListInlineChildren, a + 0x98, 0x10 );

		var defaults = DefaultBodies();
		var bodies = new List<PhysicsModelBody>();
		var index = 0;
		foreach ( var b in data.Elements( a + 0x38, RigidBodySize ) )
		{
			var shapes = new List<PhysicsModelShape>();
			var phantoms = new List<int>();
			AddShapes( (ShapeType)data.I16( b + 0x38 ), data.I16( b + 0x3A ), shapes, phantoms, 0 );
			foreach ( var p in phantoms )
				_phantomBodies.TryAdd( p, index );

			bodies.Add( new PhysicsModelBody( index, data.I16( b ), (PhysicsModelMotion)data.I16( b + 0x1A ), data.F32( b + 0x40 ), data.Vec3( b + 0x50 ),
				data.Vec3( b + 0x60 ), data.Vec3( b + 0x70 ), data.Vec3( b + 0x80 ), data.F32( b + 0x24 ), data.F32( b + 0x28 ), shapes, phantoms,
				defaults is null || defaults.Contains( index ), data.U16( b + 0x18 ) ) );
			index++;
		}
		RigidBodies = bodies;

		Phantoms = data.Elements( a + 0x108, 0x40 ).Select( ( p, i ) => new PhysicsModelPhantom( i, _phantomBodies.GetValueOrDefault( i, -1 ),
			Shape( (ShapeType)data.I16( p + 0x18 ), data.I16( p + 0x1A ) ) ) ).ToList();

		Constraints = ReadConstraints();
	}

	public static bool IsPhysicsModel( TagEntry tag ) => tag is { Address: not 0 } && tag.Class == Class;

	// The global material a physics material is made of: its global material name, or its own name where that's empty.
	string MaterialName( uint material )
	{
		var global = _cache.GetString( _data.U32( material + 0x4 ) );
		return string.IsNullOrEmpty( global ) ? _cache.GetString( _data.U32( material ) ) ?? "" : global;
	}

	// Where each element's overflow starts in a shared block: elements with more than inline entries take theirs in turn.
	uint[] Spill( uint block, int size, uint countOffset, int inline, uint spillBlock, int spillSize )
	{
		var elements = _data.Elements( block, size ).ToList();
		var starts = new uint[elements.Count];
		var next = _data.U32( spillBlock + 4 );
		for ( var i = 0; i < elements.Count; i++ )
		{
			var count = _data.I32( elements[i] + countOffset );
			starts[i] = count > inline ? next : 0;
			if ( count > inline )
				next += (uint)(count * spillSize);
		}

		return starts;
	}

	// The rigid bodies of each region's first permutation, or null when the model has no regions (all bodies).
	HashSet<int> DefaultBodies()
	{
		var regions = _data.Elements( _address + 0xC0, 0xC ).ToList();
		if ( regions.Count == 0 )
			return null;

		var bodies = new HashSet<int>();
		var listed = new HashSet<int>();
		foreach ( var region in regions )
		{
			var first = true;
			foreach ( var permutation in _data.Elements( region + 0x4, 0xC ) )
			{
				foreach ( var body in _data.Elements( permutation + 0x4, 0x2 ).Select( e => (int)_data.I16( e ) ) )
				{
					listed.Add( body );
					if ( first )
						bodies.Add( body );
				}
				first = false;
			}
		}

		// Bodies no permutation lists are always there.
		var count = _data.Count( _address + 0x38 );
		for ( var i = 0; i < count; i++ )
		{
			if ( !listed.Contains( i ) )
				bodies.Add( i );
		}

		return bodies;
	}

	// A body's shape and its children, solid shapes into shapes and phantoms by index.
	void AddShapes( ShapeType type, int index, List<PhysicsModelShape> shapes, List<int> phantoms, int depth )
	{
		if ( index < 0 || depth > 8 )
			return;

		switch ( type )
		{
			case ShapeType.List:
				var list = Element( 0x90, 0x68, index );
				if ( list == 0 )
					return;

				var count = _data.I32( list + 0x20 );
				var children = count > ListInlineChildren ? _listChildren[index] : list + 0x28;
				for ( var i = 0; i < count; i++ )
				{
					var child = children + (uint)(i * 0x10);
					AddShapes( (ShapeType)_data.I16( child ), _data.I16( child + 2 ), shapes, phantoms, depth + 1 );
				}
				return;

			case ShapeType.Mopp:
				var mopp = Element( 0xA0, 0x28, index );
				if ( mopp != 0 )
					AddShapes( (ShapeType)_data.I16( mopp + 0x18 ), _data.I16( mopp + 0x1A ), shapes, phantoms, depth + 1 );
				return;

			case ShapeType.Phantom:
				phantoms.Add( index );
				return;

			case ShapeType.MultiSphere:
				var multi = Element( 0x50, 0xC0, index );
				if ( multi == 0 )
					return;

				var common = Common( multi );
				var spheres = Math.Min( _data.I32( multi + 0x38 ), 8 );
				for ( var i = 0; i < spheres; i++ )
				{
					var sphere = multi + 0x40 + (uint)(i * 0x10);
					shapes.Add( common with { Kind = PhysicsModelShapeKind.Sphere, Center = _data.Vec3( sphere ), Radius = _data.F32( sphere + 0xC ) } );
				}
				return;

			default:
				if ( Shape( type, index ) is { } shape )
					shapes.Add( shape );
				return;
		}
	}

	// A convex shape by type and index, or null.
	PhysicsModelShape Shape( ShapeType type, int index )
	{
		switch ( type )
		{
			case ShapeType.Sphere when Element( 0x48, 0xA0, index ) is not 0 and var s:
				return Common( s ) with { Kind = PhysicsModelShapeKind.Sphere, Radius = _data.F32( s + 0x38 ), Center = _data.Vec3( s + 0x90 ) };

			case ShapeType.Pill when Element( 0x58, 0x60, index ) is not 0 and var p:
				return Common( p ) with { Kind = PhysicsModelShapeKind.Pill, Radius = _data.F32( p + 0x38 ), Bottom = _data.Vec3( p + 0x40 ), Top = _data.Vec3( p + 0x50 ) };

			case ShapeType.Box when Element( 0x60, 0xB0, index ) is not 0 and var b:
				// The rotation's rows are the box's axes, as Havok's transform keeps its columns.
				var i = _data.Vec3( b + 0x70 );
				var j = _data.Vec3( b + 0x80 );
				var k = _data.Vec3( b + 0x90 );
				var basis = new System.Numerics.Matrix4x4( i.X, i.Y, i.Z, 0, j.X, j.Y, j.Z, 0, k.X, k.Y, k.Z, 0, 0, 0, 0, 1 );
				return Common( b ) with
				{
					Kind = PhysicsModelShapeKind.Box,
					ConvexRadius = _data.F32( b + 0x38 ),
					HalfExtents = _data.Vec3( b + 0x40 ),
					Rotation = Quaternion.Normalize( Quaternion.CreateFromRotationMatrix( basis ) ),
					Center = _data.Vec3( b + 0xA0 ),
				};

			case ShapeType.Triangle when Element( 0x68, 0x70, index ) is not 0 and var t:
				return Common( t ) with
				{
					Kind = PhysicsModelShapeKind.Triangle,
					ConvexRadius = _data.F32( t + 0x38 ),
					Points = [_data.Vec3( t + 0x40 ), _data.Vec3( t + 0x50 ), _data.Vec3( t + 0x60 )],
				};

			case ShapeType.Polyhedron when Element( 0x70, 0x120, index ) is not 0 and var h:
				var groups = _data.I32( h + 0x68 );
				var vectors = groups > PolyhedronInlineFourVectors ? _fourVectors[index] : h + 0x70;
				var points = new List<Vector3>();
				var vertices = Math.Min( _data.I32( h + 0x100 ), groups * 4 );
				for ( var v = 0; v < vertices; v++ )
				{
					var group = vectors + (uint)(v / 4 * 0x30 + v % 4 * 4);
					points.Add( new Vector3( _data.F32( group ), _data.F32( group + 0x10 ), _data.F32( group + 0x20 ) ) );
				}
				return Common( h ) with { Kind = PhysicsModelShapeKind.Hull, ConvexRadius = _data.F32( h + 0x38 ), Points = points };
		}

		Unsupported++;
		return null;
	}

	// The fields every shape starts with.
	PhysicsModelShape Common( uint shape )
	{
		var material = _data.I16( shape + 0x4 );
		var (name, materialPhantom) = material >= 0 && material < _materials.Count ? _materials[material] : ("", -1);
		var phantom = (sbyte)_data.U8( shape + 0x1E );
		return new PhysicsModelShape( PhysicsModelShapeKind.Sphere, _cache.GetString( _data.U32( shape ) ) ?? "", name, _data.F32( shape + 0xC ), _data.F32( shape + 0x10 ),
			phantom >= 0 ? phantom : materialPhantom, default, Quaternion.Identity, default, 0, default, default, [], 0 );
	}

	// The address of a block's element, 0 if out of range.
	uint Element( uint block, int size, int index ) =>
		index >= 0 && index < _data.Count( _address + block ) ? _data.U32( _address + block + 4 ) + (uint)(index * size) : 0;

	List<PhysicsModelConstraint> ReadConstraints()
	{
		// Constraints a node edge marks rigid, by kind and index, and the friction each edge gives its constraints.
		var rigid = new HashSet<(int, int)>();
		var edgeFriction = new Dictionary<(int, int, int), float>();
		var edgeIndex = 0;
		foreach ( var edge in _data.Elements( _address + 0x30, 0x18 ) )
		{
			foreach ( var c in _data.Elements( edge + 0x8, 0xC ) )
			{
				var (kind, index) = ((int)_data.I16( c ), (int)_data.I16( c + 0x2 ));
				if ( (_data.U32( c + 0x4 ) & 1) != 0 )
					rigid.Add( (kind, index) );
				edgeFriction.TryAdd( (edgeIndex, kind, index), _data.F32( c + 0x8 ) );
			}
			edgeIndex++;
		}

		var constraints = new List<PhysicsModelConstraint>();
		void Add( PhysicsModelConstraintKind kind, uint block, int size, Func<uint, float, PhysicsModelConstraint, PhysicsModelConstraint> extra = null )
		{
			var i = 0;
			foreach ( var c in _data.Elements( _address + block, size ) )
			{
				var constraint = new PhysicsModelConstraint( kind, _cache.GetString( _data.U32( c ) ) ?? "", _data.I16( c + 0x4 ), _data.I16( c + 0x6 ),
					Frame( c + 0x8 ), Frame( c + 0x3C ), rigid.Contains( ((int)kind, i) ), default, default, default, 0, 0 );
				var friction = edgeFriction.GetValueOrDefault( (_data.I16( c + 0x70 ), (int)kind, i) );
				constraints.Add( extra is null ? constraint : extra( c, friction, constraint ) );
				i++;
			}
		}

		Add( PhysicsModelConstraintKind.Hinge, 0xB0, 0x78 );
		Add( PhysicsModelConstraintKind.LimitedHinge, 0xE8, 0x84,
			( c, edge, x ) => x with { Friction = edge != 0 ? edge : _data.F32( c + 0x78 ), Limit = _data.Vec2( c + 0x7C ) } );
		Add( PhysicsModelConstraintKind.Ragdoll, 0xB8, 0x94, ( c, edge, x ) => x with
		{
			Limit = _data.Vec2( c + 0x78 ),
			Cone = _data.Vec2( c + 0x80 ),
			Plane = _data.Vec2( c + 0x88 ),
			Friction = edge != 0 ? edge : _data.F32( c + 0x90 ),
		} );
		Add( PhysicsModelConstraintKind.StiffSpring, 0xF8, 0x7C, ( c, _, x ) => x with { SpringLength = _data.F32( c + 0x78 ) } );
		Add( PhysicsModelConstraintKind.BallAndSocket, 0xF0, 0x78 );
		Add( PhysicsModelConstraintKind.Prismatic, 0x100, 0x84, ( c, _, x ) => x with { Limit = _data.Vec2( c + 0x78 ), Friction = _data.F32( c + 0x80 ) } );
		return constraints;
	}

	// A scale, then forward, left, up and position.
	PhysicsModelConstraintFrame Frame( uint at ) => new( _data.Vec3( at + 0x4 ), _data.Vec3( at + 0x10 ), _data.Vec3( at + 0x1C ), _data.Vec3( at + 0x28 ) );

	// An inertia tensor's rows as principal moments and the rotation from their axes to the node's (Jacobi's method).
	public static (Vector3 Moments, Quaternion Rotation) PrincipalInertia( Vector3 x, Vector3 y, Vector3 z )
	{
		var m = new double[3, 3];
		Vector3[] rows = [x, y, z];
		for ( var r = 0; r < 3; r++ )
		{
			m[r, 0] = rows[r].X;
			m[r, 1] = rows[r].Y;
			m[r, 2] = rows[r].Z;
		}

		var v = new double[3, 3] { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } };
		for ( var sweep = 0; sweep < 32; sweep++ )
		{
			var off = Math.Abs( m[0, 1] ) + Math.Abs( m[0, 2] ) + Math.Abs( m[1, 2] );
			if ( off < 1e-12 * (Math.Abs( m[0, 0] ) + Math.Abs( m[1, 1] ) + Math.Abs( m[2, 2] ) + 1e-30) )
				break;

			foreach ( var (p, q) in new[] { (0, 1), (0, 2), (1, 2) } )
			{
				if ( m[p, q] == 0 )
					continue;

				var theta = (m[q, q] - m[p, p]) / (2 * m[p, q]);
				var t = Math.Sign( theta == 0 ? 1 : theta ) / (Math.Abs( theta ) + Math.Sqrt( theta * theta + 1 ));
				var c = 1 / Math.Sqrt( t * t + 1 );
				var s = t * c;
				for ( var k = 0; k < 3; k++ )
				{
					var mkp = m[k, p];
					var mkq = m[k, q];
					m[k, p] = c * mkp - s * mkq;
					m[k, q] = s * mkp + c * mkq;
				}
				for ( var k = 0; k < 3; k++ )
				{
					var mpk = m[p, k];
					var mqk = m[q, k];
					m[p, k] = c * mpk - s * mqk;
					m[q, k] = s * mpk + c * mqk;
				}
				for ( var k = 0; k < 3; k++ )
				{
					var vkp = v[k, p];
					var vkq = v[k, q];
					v[k, p] = c * vkp - s * vkq;
					v[k, q] = s * vkp + c * vkq;
				}
			}
		}

		// The eigenvectors are v's columns; keep them right handed.
		var ex = new Vector3( (float)v[0, 0], (float)v[1, 0], (float)v[2, 0] );
		var ey = new Vector3( (float)v[0, 1], (float)v[1, 1], (float)v[2, 1] );
		var ez = Vector3.Cross( ex, ey );
		var basis = new System.Numerics.Matrix4x4( ex.X, ex.Y, ex.Z, 0, ey.X, ey.Y, ey.Z, 0, ez.X, ez.Y, ez.Z, 0, 0, 0, 0, 1 );
		return (new Vector3( (float)m[0, 0], (float)m[1, 1], (float)m[2, 2] ), Quaternion.Normalize( Quaternion.CreateFromRotationMatrix( basis ) ));
	}
}
