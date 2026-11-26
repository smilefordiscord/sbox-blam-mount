using Blam.Halo2.Tags;

namespace Sandbox.Mounting.Blam.Halo2;

// Gives a model its object's Halo physics in place of its drawn geometry: a crate's, or a biped's ragdoll.
// A node's rigid bodies (PhysicsModelPlan) are one body on its bone, with the node's default pose as its bind pose.
// The collision model is the trace mesh, since Halo's physics pushes the phmo's shapes while shots hit the coll's.
// Joints between bodies are added as Halo has them; the rest (motion type, inertia, joints to the world, flags) is in the JSON.
// Halo's convex radius isn't added: the multiplayer maps' crates have none.
static class PhysicsModelBodies
{
	public static void Add( ModelBuilder builder, PhysicsModelPlan plan, ModelTag hlmt, RenderModelTag render, string[] bones, Transform[] pose, float scale )
	{
		var bindPoses = new Transform[plan.Bodies.Count];
		var boned = new bool[plan.Bodies.Count];
		for ( var i = 0; i < plan.Bodies.Count; i++ )
		{
			var body = plan.Bodies[i];
			var bindPose = new Transform( (Vector3)body.BindPosition * scale, ToRotation( body.BindRotation ) );
			boned[i] = body.OnBone && bones is not null && body.RenderNode < bones.Length;
			bindPoses[i] = bindPose;

			var surface = ThreadSafe.IsMainThread ? Halo2Surfaces.Get( body.Material ) : null;
			var built = builder.AddBody( body.Mass, surface, boned[i] ? bones[body.RenderNode] : null );
			built.SetDamping( body.LinearDamping, body.AngularDamping );
			if ( boned[i] )
				built.SetBindPose( bindPose );

			// Shapes in the body's space, or the model's for a body without a bone.
			var toBody = boned[i] ? Transform.Zero : bindPose;
			foreach ( var shape in body.Shapes )
				AddShape( built, shape, toBody, scale );
		}

		Transform Frame( int body, System.Numerics.Vector3 position, System.Numerics.Quaternion rotation )
		{
			var frame = new Transform( (Vector3)position * scale, ToRotation( rotation ) );
			return boned[body] ? frame : bindPoses[body].ToWorld( frame );
		}

		foreach ( var joint in plan.Joints )
		{
			if ( joint.BodyB < 0 )
				continue;

			var a = Frame( joint.BodyA, joint.PositionA, joint.RotationA );
			var b = Frame( joint.BodyB, joint.PositionB, joint.RotationB );
			switch ( joint.Kind )
			{
				case PhysicsModelConstraintKind.Hinge or PhysicsModelConstraintKind.LimitedHinge or PhysicsModelConstraintKind.Ragdoll when joint.Rigid:
					builder.AddFixedJoint( joint.BodyA, joint.BodyB, a, b );
					break;

				case PhysicsModelConstraintKind.Hinge:
					builder.AddHingeJoint( joint.BodyA, joint.BodyB, a, b );
					break;

				case PhysicsModelConstraintKind.LimitedHinge:
					builder.AddHingeJoint( joint.BodyA, joint.BodyB, a, b ).WithTwistLimit( joint.Limit.X, joint.Limit.Y );
					break;

				case PhysicsModelConstraintKind.Ragdoll:
					// Havok's cone is round, and Halo makes it the larger of the tag's bounds; the plane limit has no s&box equivalent.
					builder.AddBallJoint( joint.BodyA, joint.BodyB, a, b ).WithSwingLimit( MathF.Max( joint.Cone.X, joint.Cone.Y ) ).WithTwistLimit( joint.Limit.X, joint.Limit.Y );
					break;

				case PhysicsModelConstraintKind.BallAndSocket:
					builder.AddBallJoint( joint.BodyA, joint.BodyB, a, b );
					break;
			}
		}

		if ( CollisionModelTag.IsCollisionModel( hlmt.CollisionModel ) )
			AddTraceMesh( builder, new CollisionModelTag( hlmt.CollisionModel ), render, pose, scale );
	}

	static void AddShape( PhysicsBodyBuilder body, PhysicsModelShape shape, Transform toBody, float scale )
	{
		switch ( shape.Kind )
		{
			case PhysicsModelShapeKind.Sphere:
				body.AddSphere( new Sphere( toBody.PointToWorld( (Vector3)shape.Center * scale ), shape.Radius * scale ) );
				break;

			case PhysicsModelShapeKind.Pill:
				body.AddCapsule( new Capsule( toBody.PointToWorld( (Vector3)shape.Bottom * scale ), toBody.PointToWorld( (Vector3)shape.Top * scale ), shape.Radius * scale ) );
				break;

			case PhysicsModelShapeKind.Box:
				body.AddBox( (Vector3)shape.HalfExtents * scale, toBody.ToWorld( new Transform( (Vector3)shape.Center * scale, ToRotation( shape.Rotation ) ) ) );
				break;

			case PhysicsModelShapeKind.Hull:
				body.AddHull( shape.Points.Select( p => toBody.PointToWorld( (Vector3)p * scale ) ).ToArray() );
				break;
		}
	}

	// Every region's first permutation, each node's BSP where the render model's node of the same name is in its default pose.
	static void AddTraceMesh( ModelBuilder builder, CollisionModelTag collision, RenderModelTag render, Transform[] pose, float scale )
	{
		var vertices = new List<Vector3>();
		var indices = new List<int>();
		foreach ( var region in collision.Regions )
		{
			if ( region.Permutations.FirstOrDefault() is not { } permutation )
				continue;

			foreach ( var (node, bsp) in permutation.Bsps )
			{
				var name = node >= 0 && node < collision.Nodes.Count ? collision.Nodes[node] : null;
				var renderNode = render?.Nodes.FirstOrDefault( n => string.Equals( n.Name, name, StringComparison.OrdinalIgnoreCase ) )?.Index ?? -1;
				var nodePose = renderNode >= 0 && renderNode < pose.Length ? pose[renderNode] : Transform.Zero;
				var first = vertices.Count;
				vertices.AddRange( bsp.Vertices.Select( v => nodePose.PointToWorld( (Vector3)v * scale ) ) );
				indices.AddRange( bsp.Triangles().Select( i => first + i ) );
			}
		}

		if ( indices.Count > 0 )
			builder.AddTraceMesh( vertices, indices );
	}

	static Rotation ToRotation( System.Numerics.Quaternion q ) => new( q.X, q.Y, q.Z, q.W );
}
