using System;
using System.Collections.Generic;
using System.Linq;

namespace Blam.Halo2.Tags;

// Inside the namespace so these win over s&box's global vector types when this compiles into the mount.
using Matrix4x4 = System.Numerics.Matrix4x4;
using Quaternion = System.Numerics.Quaternion;
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;

// A body to build: a node's rigid bodies as one, in the node's space, which is BindPosition and BindRotation in the model.
// OnBone: the render model has the node as a bone; a model of one node has none, and its bodies are in the model's space instead.
// Inertia is the principal moments about the centre of mass along InertiaRotation's axes.
// Damping and Flags are the first rigid body's, as Halo builds a node's bodies as one from it.
public sealed record PhysicsPlanBody( string Node, int RenderNode, bool OnBone, Vector3 BindPosition, Quaternion BindRotation, PhysicsModelMotion Motion, float Mass,
	Vector3 CenterOfMass, Vector3 Inertia, Quaternion InertiaRotation, float LinearDamping, float AngularDamping, IReadOnlyList<PhysicsModelShape> Shapes,
	IReadOnlyList<int> RigidBodies, int Flags )
{
	public string Material => Shapes.Count > 0 ? Shapes[0].Material : "";
}

// A joint between two bodies, its frames in each body's space; BodyB -1 holds body A to the object, frame B then in the object's space.
// A frame's z is the hinge axis or a ragdoll's twist axis, and a ragdoll's x its plane axis. Angles are degrees.
// Limit is body B's angle about z from body A's: a hinge's, or a ragdoll's twist about the line halfway between their z axes.
// A ragdoll's z axes stay within the larger Cone bound of each other, and sin(Plane.X) <= B's x . A's z <= sin(Plane.Y).
public sealed record PhysicsPlanJoint( PhysicsModelConstraintKind Kind, string Name, int BodyA, int BodyB, Vector3 PositionA, Quaternion RotationA,
	Vector3 PositionB, Quaternion RotationB, bool Rigid, Vector2 Limit, Vector2 Cone, Vector2 Plane, float Friction, float SpringLength );

// A phantom's shape in the model's space, and the phantom type it applies.
public sealed record PhysicsPlanPhantom( int Index, string Node, PhysicsModelShape Shape, int PhantomType );

public enum PhysicsNodePose
{
	// The node's body moves it.
	Body,

	// Its parent's pose times its default pose, so it stays at Position and Rotation in Body's space.
	Default,

	// Its parent's pose times its pose in the animation, which Halo stops sampling while the object's physics moves it.
	Animation,
}

// How Halo poses a render model node while the physics model moves the object (a ragdoll); Body moves it, -1 for none.
// A node with a body follows it, and one with a body below it (the chief's spine, neck and clavicles) keeps its default pose.
// Any other node (hands, feet) keeps its animated pose under its parent.
public sealed record PhysicsPlanNode( string Node, string Parent, PhysicsNodePose Pose, int Body, Vector3 Position, Quaternion Rotation );

// How a model's physics model becomes bodies and joints, in world units: what a game rebuilds from the model's physics.
// Bodies are the default permutations' rigid bodies with solid shapes, one per node (rigid bodies on the same node move with it as one).
// They sit where the render model's node of the same name is in its default pose.
public sealed class PhysicsModelPlan
{
	public PhysicsModelTag PhysicsModel { get; }
	public IReadOnlyList<PhysicsPlanBody> Bodies { get; }
	public IReadOnlyList<PhysicsPlanJoint> Joints { get; }
	public IReadOnlyList<PhysicsPlanPhantom> Phantoms { get; }

	// Every render model node of a model with bones; none for a model of one node.
	public IReadOnlyList<PhysicsPlanNode> Nodes { get; }

	PhysicsModelPlan( PhysicsModelTag physics, RenderModelTag render )
	{
		PhysicsModel = physics;
		var pose = render?.DefaultPose() ?? [];
		int RenderNode( int node )
		{
			var name = node >= 0 && node < physics.Nodes.Count ? physics.Nodes[node] : null;
			if ( render is null || name is null )
				return -1;

			for ( var i = 0; i < render.Nodes.Count; i++ )
			{
				if ( string.Equals( render.Nodes[i].Name, name, StringComparison.OrdinalIgnoreCase ) )
					return i;
			}
			return -1;
		}

		Matrix4x4 NodePose( int node )
		{
			var r = RenderNode( node );
			return r >= 0 && r < pose.Length ? Matrix4x4.CreateFromQuaternion( pose[r].Rotation ) * Matrix4x4.CreateTranslation( pose[r].Translation ) : Matrix4x4.Identity;
		}

		// A body without a node is on the first.
		static int NodeOf( PhysicsModelBody body ) => Math.Max( body.Node, 0 );

		bool OnBone( int node ) => render is { Nodes.Count: > 1 } && RenderNode( node ) >= 0;

		// From a node's space to its body's.
		Matrix4x4 ToBody( int node ) => OnBone( node ) ? Matrix4x4.Identity : NodePose( node );
		Quaternion ToBodyRotation( int node ) => Quaternion.CreateFromRotationMatrix( ToBody( node ) );

		var bodies = new List<PhysicsPlanBody>();
		var bodyOfNode = new Dictionary<int, int>();
		foreach ( var group in physics.RigidBodies.Where( b => b.Default && b.Shapes.Count > 0 ).GroupBy( NodeOf ) )
		{
			var node = group.Key;
			Matrix4x4.Decompose( OnBone( node ) ? NodePose( node ) : Matrix4x4.Identity, out _, out var rotation, out var translation );
			var (mass, center, moments, axes) = Combine( group.ToList() );
			var toBody = ToBody( node );
			var motion = group.Any( b => b.Motion == PhysicsModelMotion.Fixed ) ? PhysicsModelMotion.Fixed
				: group.Any( b => b.Motion == PhysicsModelMotion.Keyframed ) ? PhysicsModelMotion.Keyframed : group.First().Motion;
			bodyOfNode[node] = bodies.Count;
			bodies.Add( new PhysicsPlanBody( node < physics.Nodes.Count ? physics.Nodes[node] : "", RenderNode( node ), OnBone( node ), translation, rotation, motion, mass,
				Vector3.Transform( center, toBody ), moments, Quaternion.Normalize( Quaternion.Concatenate( axes, ToBodyRotation( node ) ) ), group.First().LinearDamping,
				group.First().AngularDamping, group.SelectMany( b => b.Shapes ).Select( shape => Transform( shape, toBody ) ).ToList(), group.Select( b => b.Index ).ToList(),
				group.First().Flags ) );
		}
		Bodies = bodies;

		var joints = new List<PhysicsPlanJoint>();
		foreach ( var c in physics.Constraints )
		{
			if ( !bodyOfNode.TryGetValue( c.NodeA, out var a ) )
				continue;

			var b = bodyOfNode.GetValueOrDefault( c.NodeB, -1 );
			var ragdoll = c.Kind == PhysicsModelConstraintKind.Ragdoll;
			var rotationA = FrameRotation( c.A, ragdoll );
			var rotationB = FrameRotation( c.B, ragdoll );

			// Where B isn't a body, its frame goes to the object's space: a node's at its default pose, the object's own where it has none.
			var poseA = NodePose( c.NodeA );
			var poseB = c.NodeB >= 0 ? NodePose( c.NodeB ) : Matrix4x4.Identity;
			var positionB = c.B.Position;
			if ( b < 0 )
			{
				rotationB = Quaternion.Normalize( Quaternion.Concatenate( rotationB, Quaternion.CreateFromRotationMatrix( poseB ) ) );

				// Anchored where A's frame is in the default pose: the tags' own anchors are off on a few crates (hang_planter, hanging_beam).
				positionB = Vector3.Transform( c.A.Position, poseA );
			}

			// A rigid joint holds its bodies as the default pose has them: hang_planter's pots are 0.74 apart where its frames meet.
			var worldA = Quaternion.Concatenate( rotationA, Quaternion.CreateFromRotationMatrix( poseA ) );
			if ( c.Rigid )
			{
				Matrix4x4.Invert( poseB, out var fromB );
				positionB = b >= 0 ? Vector3.Transform( Vector3.Transform( c.A.Position, poseA ), fromB ) : positionB;
				rotationB = b >= 0 ? Quaternion.Concatenate( worldA, Quaternion.Conjugate( Quaternion.CreateFromRotationMatrix( poseB ) ) ) : worldA;
			}

			// Havok's hinge holds the axes on one line either way round; one that starts reversed turns B's frame over (Zanzibar's awning).
			// A ragdoll's cone holds its twist axes together, so its frames stay as the tag has them.
			var worldB = b >= 0 ? Quaternion.Concatenate( rotationB, Quaternion.CreateFromRotationMatrix( poseB ) ) : rotationB;
			if ( !ragdoll && Vector3.Dot( Vector3.Transform( Vector3.UnitZ, worldA ), Vector3.Transform( Vector3.UnitZ, worldB ) ) < 0 )
				rotationB = Quaternion.Normalize( Quaternion.Concatenate( Quaternion.CreateFromAxisAngle( Vector3.UnitX, MathF.PI ), rotationB ) );

			if ( b >= 0 )
			{
				positionB = Vector3.Transform( positionB, ToBody( c.NodeB ) );
				rotationB = Quaternion.Normalize( Quaternion.Concatenate( rotationB, ToBodyRotation( c.NodeB ) ) );
			}

			// Halo orders a limited hinge's and a ragdoll's bounds.
			// A ragdoll's twist is A's plane axis from B's, so B's angle from A's is its negation.
			var limit = c.Kind is PhysicsModelConstraintKind.LimitedHinge || ragdoll ? new Vector2( MathF.Min( c.Limit.X, c.Limit.Y ), MathF.Max( c.Limit.X, c.Limit.Y ) ) : c.Limit;
			if ( ragdoll )
				limit = new Vector2( -limit.Y, -limit.X );
			var plane = new Vector2( MathF.Min( c.Plane.X, c.Plane.Y ), MathF.Max( c.Plane.X, c.Plane.Y ) );

			joints.Add( new PhysicsPlanJoint( c.Kind, c.Name, a, b, Vector3.Transform( c.A.Position, ToBody( c.NodeA ) ),
				Quaternion.Normalize( Quaternion.Concatenate( rotationA, ToBodyRotation( c.NodeA ) ) ), positionB, rotationB, c.Rigid, limit, c.Cone, plane, c.Friction,
				c.SpringLength ) );
		}
		Joints = joints;

		Phantoms = physics.Phantoms.Where( p => p.Shape is not null ).Select( p =>
		{
			var node = p.Body >= 0 ? NodeOf( physics.RigidBodies[p.Body] ) : 0;
			return new PhysicsPlanPhantom( p.Index, node < physics.Nodes.Count ? physics.Nodes[node] : "", Transform( p.Shape, NodePose( node ) ), p.Shape.PhantomType );
		} ).ToList();

		Nodes = render is { Nodes.Count: > 1 } ? PlanNodes( render, pose, bodies ) : [];
	}

	// A bodiless node with a body below it is held to the nearest body above it as the default pose has them.
	static List<PhysicsPlanNode> PlanNodes( RenderModelTag render, (Quaternion Rotation, Vector3 Translation)[] pose, List<PhysicsPlanBody> bodies )
	{
		var nodes = render.Nodes;
		var bodyOn = new int[nodes.Count];
		Array.Fill( bodyOn, -1 );
		for ( var i = 0; i < bodies.Count; i++ )
		{
			if ( bodies[i].OnBone )
				bodyOn[bodies[i].RenderNode] = i;
		}

		var aboveBody = new bool[nodes.Count];
		for ( var n = 0; n < nodes.Count; n++ )
		{
			if ( bodyOn[n] < 0 )
				continue;

			for ( var p = nodes[n].Parent; p >= 0 && p < nodes.Count && !aboveBody[p]; p = nodes[p].Parent )
				aboveBody[p] = true;
		}

		var planned = new List<PhysicsPlanNode>();
		for ( var n = 0; n < nodes.Count; n++ )
		{
			var parent = nodes[n].Parent >= 0 && nodes[n].Parent < nodes.Count ? nodes[nodes[n].Parent].Name : null;
			if ( bodyOn[n] >= 0 )
			{
				planned.Add( new PhysicsPlanNode( nodes[n].Name, parent, PhysicsNodePose.Body, bodyOn[n], Vector3.Zero, Quaternion.Identity ) );
				continue;
			}

			var followed = nodes[n].Parent;
			while ( followed >= 0 && followed < nodes.Count && bodyOn[followed] < 0 )
				followed = nodes[followed].Parent;
			if ( !aboveBody[n] || parent is null || followed < 0 )
			{
				planned.Add( new PhysicsPlanNode( nodes[n].Name, parent, PhysicsNodePose.Animation, -1, Vector3.Zero, Quaternion.Identity ) );
				continue;
			}

			var inverse = Quaternion.Conjugate( pose[followed].Rotation );
			planned.Add( new PhysicsPlanNode( nodes[n].Name, parent, PhysicsNodePose.Default, bodyOn[followed],
				Vector3.Transform( pose[n].Translation - pose[followed].Translation, inverse ), Quaternion.Normalize( inverse * pose[n].Rotation ) ) );
		}

		return planned;
	}

	// The plan for a model tag's physics model, or null if it has none.
	public static PhysicsModelPlan For( ModelTag model )
	{
		if ( !PhysicsModelTag.IsPhysicsModel( model.PhysicsModel ) )
			return null;

		var render = model.RenderModel is { Address: not 0 } r && r.Class == RenderModelTag.Class ? new RenderModelTag( r ) : null;
		return new PhysicsModelPlan( new PhysicsModelTag( model.PhysicsModel ), render );
	}

	// A frame's rotation with its forward, left and up as x, y and z, a hinge turning about up.
	// A ragdoll twists about its left and limits its plane against its up, so those go to z and x.
	static Quaternion FrameRotation( PhysicsModelConstraintFrame frame, bool ragdoll )
	{
		var (x, y, z) = ragdoll ? (frame.Up, frame.Forward, frame.Left) : (frame.Forward, frame.Left, frame.Up);
		var basis = new Matrix4x4( x.X, x.Y, x.Z, 0, y.X, y.Y, y.Z, 0, z.X, z.Y, z.Z, 0, 0, 0, 0, 1 );
		return Quaternion.Normalize( Quaternion.CreateFromRotationMatrix( basis ) );
	}

	// Rigid bodies on one node as one: total mass, the centre of mass, and the inertia about it (parallel axes), as principal moments.
	static (float Mass, Vector3 Center, Vector3 Moments, Quaternion Axes) Combine( List<PhysicsModelBody> bodies )
	{
		var mass = bodies.Sum( b => b.Mass );
		var center = mass > 0 ? bodies.Aggregate( Vector3.Zero, ( s, b ) => s + b.CenterOfMass * b.Mass ) / mass : bodies[0].CenterOfMass;
		var x = Vector3.Zero;
		var y = Vector3.Zero;
		var z = Vector3.Zero;
		foreach ( var b in bodies )
		{
			var d = b.CenterOfMass - center;
			var dd = d.LengthSquared();
			x += b.InertiaX + b.Mass * new Vector3( dd - d.X * d.X, -d.X * d.Y, -d.X * d.Z );
			y += b.InertiaY + b.Mass * new Vector3( -d.Y * d.X, dd - d.Y * d.Y, -d.Y * d.Z );
			z += b.InertiaZ + b.Mass * new Vector3( -d.Z * d.X, -d.Z * d.Y, dd - d.Z * d.Z );
		}

		var (moments, axes) = PhysicsModelTag.PrincipalInertia( x, y, z );
		return (mass, center, moments, axes);
	}

	// A shape moved from its node's space by the node's pose.
	public static PhysicsModelShape Transform( PhysicsModelShape shape, Matrix4x4 pose )
	{
		Matrix4x4.Decompose( pose, out _, out var rotation, out _ );
		return shape with
		{
			Center = Vector3.Transform( shape.Center, pose ),
			Bottom = Vector3.Transform( shape.Bottom, pose ),
			Top = Vector3.Transform( shape.Top, pose ),
			Rotation = Quaternion.Normalize( Quaternion.Concatenate( shape.Rotation, rotation ) ),
			Points = shape.Points.Select( p => Vector3.Transform( p, pose ) ).ToList(),
		};
	}
}
