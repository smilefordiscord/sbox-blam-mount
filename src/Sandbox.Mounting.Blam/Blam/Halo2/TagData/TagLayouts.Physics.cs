namespace Blam.Halo2.TagData;

// The physics model (phmo): rigid bodies, the shapes they're built of, and the constraints between nodes.
// What the physics engine fills in at runtime is left out.
public static partial class TagLayouts
{
	static readonly string[] ShapeType =
	[
		"sphere", "pill", "box", "triangle", "polyhedron", "multi sphere", "phantom", "unused 1", "unused 2", "unused 3",
		"unused 4", "unused 5", "unused 6", "unused 7", "list", "mopp",
	];

	static readonly string[] RigidBodySize = ["default", "tiny", "small", "medium", "large", "huge", "extra huge"];

	internal static readonly string[] RigidBodyFlags =
		["no collisions with self", "only collide with environment", "disable effects", "does not interact with environment", "best early mover body", "has no phantom power version"];

	static readonly TagStruct PhysicsPhantomType = new TagStruct( "physics phantom type", 0x68 )
		.Flags32( 0x0, "flags", ["generates effects", "use acceleration as force", "negates gravity", "ignores players", "ignores non-players", "ignores bipeds", "ignores vehicles", "ignores weapons", "ignores equipment", "ignores garbage", "ignores projectiles", "ignores scenery", "ignores machines", "ignores controls", "ignores light fixtures", "ignores sound scenery", "ignores crates", "ignores creatures", "bit 18", "bit 19", "bit 20", "bit 21", "bit 22", "bit 23", "localizes physics", "disable linear damping", "disable angular damping", "ignores dead bipeds"] )
		.Enum8( 0x4, "minimum size", RigidBodySize )
		.Enum8( 0x5, "maximum size", RigidBodySize )
		.StringId( 0x8, "marker name" )
		.StringId( 0xC, "alignment marker name" )
		.Float( 0x18, "hooke's law e" )
		.Float( 0x1C, "linear dead radius" )
		.Float( 0x20, "center acceleration" )
		.Float( 0x24, "center max velocity" )
		.Float( 0x28, "axis acceleration" )
		.Float( 0x2C, "axis max velocity" )
		.Float( 0x30, "direction acceleration" )
		.Float( 0x34, "direction max velocity" )
		.Float( 0x38, "orbit acceleration" )
		.Float( 0x3C, "orbit max velocity" )
		.Float( 0x54, "alignment hooke's law e" )
		.Float( 0x58, "alignment acceleration" )
		.Float( 0x5C, "alignment max velocity" );

	static readonly TagStruct PhysicsNodeEdgeConstraint = new TagStruct( "physics node edge constraint", 0xC )
		.Enum( 0x0, "type", ["hinge", "limited hinge", "ragdoll", "stiff spring", "ball and socket", "prismatic"] )
		.Index( 0x2, "index" )
		.Flags32( 0x4, "flags", ["is rigid", "disable effects"] )
		.Float( 0x8, "friction" );

	static readonly TagStruct PhysicsNodeEdge = new TagStruct( "physics node edge", 0x18 )
		.Index( 0x4, "node a" )
		.Index( 0x6, "node b" )
		.Block( 0x8, "constraints", PhysicsNodeEdgeConstraint )
		.StringId( 0x10, "node a material" )
		.StringId( 0x14, "node b material" );

	// A rigid body: the node it moves, its shape (a shape block and an index in it), mass and inertia.
	static readonly TagStruct PhysicsRigidBody = new TagStruct( "physics rigid body", 0xA0 )
		.Index( 0x0, "node" )
		.Index( 0x2, "region" )
		.Index( 0x4, "permutation" )
		.Point3( 0x8, "bounding sphere offset" )
		.Float( 0x14, "bounding sphere radius" )
		.Flags16( 0x18, "flags", RigidBodyFlags )
		.Enum( 0x1A, "motion type", ["sphere", "stabilized sphere", "box", "stabilized box", "keyframed", "fixed"] )
		.Index( 0x1C, "no phantom power alt" )
		.Enum( 0x1E, "size", RigidBodySize )
		.Float( 0x20, "inertia tensor scale" )
		.Float( 0x24, "linear damping" )
		.Float( 0x28, "angular damping" )
		.Point3( 0x2C, "center of mass offset" )
		.Enum( 0x38, "shape type", ShapeType )
		.Index( 0x3A, "shape" )
		.Float( 0x40, "mass" )
		.Point3( 0x50, "center of mass" )
		.Point3( 0x60, "inertia tensor x" )
		.Point3( 0x70, "inertia tensor y" )
		.Point3( 0x80, "inertia tensor z" );

	static readonly TagStruct PhysicsMaterial = new TagStruct( "physics material", 0xC )
		.StringId( 0x0, "name" )
		.StringId( 0x4, "global material name" )
		.Index( 0x8, "phantom type" )
		.Flags16( 0xA, "flags", ["does not collide with fixed bodies"] );

	// What every shape starts with. A convex shape's radius is the skin around it.
	static TagStruct PhysicsShape( string name, int size ) => new TagStruct( name, size )
		.StringId( 0x0, "name" )
		.Index( 0x4, "material" )
		.Float( 0x8, "relative mass scale" )
		.Float( 0xC, "friction" )
		.Float( 0x10, "restitution" )
		.Float( 0x14, "volume" )
		.Float( 0x18, "mass" )
		.Index( 0x1C, "mass distribution" )
		.ByteIndex( 0x1E, "phantom" )
		.Int8( 0x1F, "collision group" );

	static readonly TagStruct PhysicsSphere = PhysicsShape( "physics sphere", 0xA0 )
		.Float( 0x38, "radius" )
		.Point3( 0x90, "translation" );

	static readonly TagStruct PhysicsMultiSphere = PhysicsShape( "physics multi sphere", 0xC0 )
		.Int32( 0x38, "number of spheres" )
		.Vector4( 0x40, "sphere 0" )
		.Vector4( 0x50, "sphere 1" )
		.Vector4( 0x60, "sphere 2" )
		.Vector4( 0x70, "sphere 3" )
		.Vector4( 0x80, "sphere 4" )
		.Vector4( 0x90, "sphere 5" )
		.Vector4( 0xA0, "sphere 6" )
		.Vector4( 0xB0, "sphere 7" );

	static readonly TagStruct PhysicsPill = PhysicsShape( "physics pill", 0x60 )
		.Float( 0x38, "radius" )
		.Point3( 0x40, "bottom" )
		.Point3( 0x50, "top" );

	static readonly TagStruct PhysicsBox = PhysicsShape( "physics box", 0xB0 )
		.Float( 0x38, "radius" )
		.Point3( 0x40, "half extents" )
		.Point3( 0x70, "rotation i" )
		.Point3( 0x80, "rotation j" )
		.Point3( 0x90, "rotation k" )
		.Point3( 0xA0, "translation" );

	static readonly TagStruct PhysicsTriangle = PhysicsShape( "physics triangle", 0x70 )
		.Float( 0x38, "radius" )
		.Point3( 0x40, "point a" )
		.Point3( 0x50, "point b" )
		.Point3( 0x60, "point c" );

	// A convex polyhedron keeps its vertices as four vectors, four per element as x, y and z rows.
	// Up to 3 are in the polyhedron itself; more are in the four vectors block, the polyhedra's one after another.
	// Its faces are in the plane equations block the same way.
	static readonly TagStruct PhysicsPolyhedron = PhysicsShape( "physics polyhedron", 0x120 )
		.Float( 0x38, "radius" )
		.Point3( 0x40, "aabb half extents" )
		.Point3( 0x50, "aabb center" )
		.Int32( 0x68, "four vectors size" )
		.Vector4( 0x70, "four vectors 0 x" )
		.Vector4( 0x80, "four vectors 0 y" )
		.Vector4( 0x90, "four vectors 0 z" )
		.Vector4( 0xA0, "four vectors 1 x" )
		.Vector4( 0xB0, "four vectors 1 y" )
		.Vector4( 0xC0, "four vectors 1 z" )
		.Vector4( 0xD0, "four vectors 2 x" )
		.Vector4( 0xE0, "four vectors 2 y" )
		.Vector4( 0xF0, "four vectors 2 z" )
		.Int32( 0x100, "number of vertices" )
		.Int32( 0x110, "plane equations size" );

	static readonly TagStruct PhysicsFourVectors = new TagStruct( "physics four vectors", 0x30 )
		.Vector4( 0x0, "four vectors x" )
		.Vector4( 0x10, "four vectors y" )
		.Vector4( 0x20, "four vectors z" );

	static readonly TagStruct PhysicsPlaneEquation = new TagStruct( "physics plane equation", 0x10 )
		.Vector4( 0x0, "plane equation" );

	static readonly TagStruct PhysicsMassDistribution = new TagStruct( "physics mass distribution", 0x40 )
		.Point3( 0x0, "center of mass" )
		.Point3( 0x10, "inertia tensor i" )
		.Point3( 0x20, "inertia tensor j" )
		.Point3( 0x30, "inertia tensor k" );

	static readonly TagStruct PhysicsListShape = new TagStruct( "physics list shape", 0x10 )
		.Enum( 0x0, "shape type", ShapeType )
		.Index( 0x2, "shape" )
		.Int32( 0x8, "collision filter" );

	// A list of shapes: up to four children are kept in the list; a list with more has them all in the list shapes block, the lists' one after another.
	static readonly TagStruct PhysicsList = new TagStruct( "physics list", 0x68 )
		.Int32( 0x20, "child shapes size" )
		.Struct( 0x28, "child shape 0", PhysicsListShape )
		.Struct( 0x38, "child shape 1", PhysicsListShape )
		.Struct( 0x48, "child shape 2", PhysicsListShape )
		.Struct( 0x58, "child shape 3", PhysicsListShape );

	static readonly TagStruct PhysicsMopp = new TagStruct( "physics mopp", 0x28 )
		.Enum( 0x18, "shape type", ShapeType )
		.Index( 0x1A, "shape" );

	// A constraint between two nodes: each node's frame (forward, left, up, position) and scale.
	static TagStruct PhysicsConstraint( string name, int size ) => new TagStruct( name, size )
		.StringId( 0x0, "name" )
		.Index( 0x4, "node a" )
		.Index( 0x6, "node b" )
		.Float( 0x8, "a scale" )
		.Point3( 0xC, "a forward" )
		.Point3( 0x18, "a left" )
		.Point3( 0x24, "a up" )
		.Point3( 0x30, "a position" )
		.Float( 0x3C, "b scale" )
		.Point3( 0x40, "b forward" )
		.Point3( 0x4C, "b left" )
		.Point3( 0x58, "b up" )
		.Point3( 0x64, "b position" )
		.Index( 0x70, "edge index" );

	static readonly TagStruct PhysicsHingeConstraint = PhysicsConstraint( "physics hinge constraint", 0x78 );

	static readonly TagStruct PhysicsRagdollConstraint = PhysicsConstraint( "physics ragdoll constraint", 0x94 )
		.FloatBounds( 0x78, "twist" )
		.FloatBounds( 0x80, "cone" )
		.FloatBounds( 0x88, "plane" )
		.Float( 0x90, "max friction torque" );

	// Degrees, as the maps store them (a door that swings from 10 to 170).
	static readonly TagStruct PhysicsLimitedHingeConstraint = PhysicsConstraint( "physics limited hinge constraint", 0x84 )
		.Float( 0x78, "limit friction" )
		.FloatBounds( 0x7C, "limit angle" );

	static readonly TagStruct PhysicsBallAndSocketConstraint = PhysicsConstraint( "physics ball and socket constraint", 0x78 );

	static readonly TagStruct PhysicsStiffSpringConstraint = PhysicsConstraint( "physics stiff spring constraint", 0x7C )
		.Float( 0x78, "spring length" );

	static readonly TagStruct PhysicsPrismaticConstraint = PhysicsConstraint( "physics prismatic constraint", 0x84 )
		.FloatBounds( 0x78, "limit angle" )
		.Float( 0x80, "max friction force" );

	static readonly TagStruct PhysicsRigidBodyIndex = new TagStruct( "physics rigid body index", 0x2 )
		.Index( 0x0, "rigid body" );

	static readonly TagStruct PhysicsPermutation = new TagStruct( "physics permutation", 0xC )
		.StringId( 0x0, "name" )
		.Block( 0x4, "rigid bodies", PhysicsRigidBodyIndex );

	static readonly TagStruct PhysicsRegion = new TagStruct( "physics region", 0xC )
		.StringId( 0x0, "name" )
		.Block( 0x4, "permutations", PhysicsPermutation );

	static readonly TagStruct PhysicsNode = new TagStruct( "physics node", 0xC )
		.StringId( 0x0, "name" )
		.Flags16( 0x4, "flags", ["does not animate"] )
		.Index( 0x6, "parent" )
		.Index( 0x8, "sibling" )
		.Index( 0xA, "child" );

	static readonly TagStruct PhysicsPhantom = new TagStruct( "physics phantom", 0x40 )
		.Enum( 0x18, "shape type", ShapeType )
		.Index( 0x1A, "shape" )
		.Int32( 0x20, "collision filter" );

	public static readonly TagStruct PhysicsModel = new TagStruct( "physics model", 0x110 )
		.Float( 0x4, "mass" )
		.Float( 0x8, "low freq. deactivation scale" )
		.Float( 0xC, "high freq. deactivation scale" )
		.Block( 0x28, "phantom types", PhysicsPhantomType )
		.Block( 0x30, "node edges", PhysicsNodeEdge )
		.Block( 0x38, "rigid bodies", PhysicsRigidBody )
		.Block( 0x40, "materials", PhysicsMaterial )
		.Block( 0x48, "spheres", PhysicsSphere )
		.Block( 0x50, "multi spheres", PhysicsMultiSphere )
		.Block( 0x58, "pills", PhysicsPill )
		.Block( 0x60, "boxes", PhysicsBox )
		.Block( 0x68, "triangles", PhysicsTriangle )
		.Block( 0x70, "polyhedra", PhysicsPolyhedron )
		.Block( 0x78, "polyhedron four vectors", PhysicsFourVectors )
		.Block( 0x80, "polyhedron plane equations", PhysicsPlaneEquation )
		.Block( 0x88, "mass distributions", PhysicsMassDistribution )
		.Block( 0x90, "lists", PhysicsList )
		.Block( 0x98, "list shapes", PhysicsListShape )
		.Block( 0xA0, "mopps", PhysicsMopp )
		.Block( 0xB0, "hinge constraints", PhysicsHingeConstraint )
		.Block( 0xB8, "ragdoll constraints", PhysicsRagdollConstraint )
		.Block( 0xC0, "regions", PhysicsRegion )
		.Block( 0xC8, "nodes", PhysicsNode )
		.Block( 0xE8, "limited hinge constraints", PhysicsLimitedHingeConstraint )
		.Block( 0xF0, "ball and socket constraints", PhysicsBallAndSocketConstraint )
		.Block( 0xF8, "stiff spring constraints", PhysicsStiffSpringConstraint )
		.Block( 0x100, "prismatic constraints", PhysicsPrismaticConstraint )
		.Block( 0x108, "phantoms", PhysicsPhantom );
}
