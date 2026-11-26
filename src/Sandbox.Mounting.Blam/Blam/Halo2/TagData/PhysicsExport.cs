using System.Collections.Generic;
using System.Text.Json;
using Blam.Halo2.Tags;

namespace Blam.Halo2.TagData;

// Inside the namespace so these win over s&box's global vector types when this compiles into the mount.
using Quaternion = System.Numerics.Quaternion;
using Vector3 = System.Numerics.Vector3;

// A crate's or biped's physics as a game rebuilds it (Physics in its JSON): its _physics.vmdl's bodies, joints, phantoms, nodes.
// Lengths are WorldUnits.SboxScale per world unit, inertia mass times length squared, angles degrees, rotations x, y, z, w.
// Body i is the model's physics body i, on the model's bone Bone (null: none, the body is in the model's space).
// Shapes, centre of mass and joint frames are in the body's space, which is Position and Rotation in the model's.
// Halo creates every body with friction 1 and restitution 0 (Friction, Restitution); the shapes carry their own as well.
// Joint frames and limits are as PhysicsPlanJoint has them, nodes as PhysicsPlanNode.
public static class PhysicsExport
{
	const float Scale = WorldUnits.SboxScale;
	const float BodyFriction = 1;
	const float BodyRestitution = 0;

	static readonly string[] MotionNames = ["sphere", "stabilized sphere", "box", "stabilized box", "keyframed", "fixed"];
	static readonly string[] JointNames = ["hinge", "limited hinge", "ragdoll", "stiff spring", "ball and socket", "prismatic"];
	static readonly string[] PoseNames = ["body", "default", "animation"];

	// The object's model's physics after the object tag's own fields; null when its model has no physics model.
	public static void WriteObject( Utf8JsonWriter json, LayoutJsonWriter writer, uint address )
	{
		var model = ((TagJsonWriter)writer).Reference( address + (uint)TagLayouts.ObjectDefinition.Field( "model" ).Offset );
		var plan = model is { Address: not 0 } && model.Class == ModelTag.Class ? PhysicsModelPlan.For( new ModelTag( model ) ) : null;
		json.WritePropertyName( "Physics" );
		if ( plan is null )
		{
			json.WriteNullValue();
			return;
		}

		json.WriteStartObject();
		json.WriteString( "Model", $"mount://halo2/{model.Path.Replace( '\\', '/' )}_physics.vmdl" );
		json.WriteString( "PhysicsModel", TagJsonWriter.TagName( plan.PhysicsModel.Tag ) );

		json.WriteStartArray( "Bodies" );
		foreach ( var body in plan.Bodies )
		{
			json.WriteStartObject();
			json.WriteString( "Node", body.Node );
			if ( body.OnBone )
				json.WriteString( "Bone", body.Node );
			else
				json.WriteNull( "Bone" );
			Numbers( json, "RigidBodies", body.RigidBodies );
			json.WriteString( "MotionType", MotionNames[(int)body.Motion] );
			Point( json, "Position", body.BindPosition );
			Rotation( json, "Rotation", body.BindRotation );
			Number( json, "Mass", body.Mass );
			Point( json, "CenterOfMass", body.CenterOfMass );
			Vector( json, "Inertia", body.Inertia * Scale * Scale );
			Rotation( json, "InertiaRotation", body.InertiaRotation );
			Number( json, "LinearDamping", body.LinearDamping );
			Number( json, "AngularDamping", body.AngularDamping );
			Number( json, "Friction", BodyFriction );
			Number( json, "Restitution", BodyRestitution );
			json.WriteStartArray( "Flags" );
			for ( var bit = 0; bit < TagLayouts.RigidBodyFlags.Length; bit++ )
			{
				if ( (body.Flags & (1 << bit)) != 0 )
					json.WriteStringValue( TagLayouts.RigidBodyFlags[bit] );
			}
			json.WriteEndArray();
			json.WriteString( "Surface", body.Material );
			json.WriteStartArray( "Shapes" );
			foreach ( var shape in body.Shapes )
				Shape( json, shape );
			json.WriteEndArray();
			json.WriteEndObject();
		}
		json.WriteEndArray();

		json.WriteStartArray( "Joints" );
		foreach ( var joint in plan.Joints )
		{
			json.WriteStartObject();
			json.WriteString( "Type", JointNames[(int)joint.Kind] );
			json.WriteString( "Name", joint.Name );
			json.WriteNumber( "BodyA", joint.BodyA );
			if ( joint.BodyB >= 0 )
				json.WriteNumber( "BodyB", joint.BodyB );
			else
				json.WriteNull( "BodyB" );
			Point( json, "PositionA", joint.PositionA );
			Rotation( json, "RotationA", joint.RotationA );
			Point( json, "PositionB", joint.PositionB );
			Rotation( json, "RotationB", joint.RotationB );
			json.WriteBoolean( "Rigid", joint.Rigid );
			Vector( json, "Limit", new Vector3( joint.Limit, 0 ), 2 );
			Vector( json, "Cone", new Vector3( joint.Cone, 0 ), 2 );
			Vector( json, "Plane", new Vector3( joint.Plane, 0 ), 2 );
			Number( json, "Friction", joint.Friction );
			Number( json, "SpringLength", joint.SpringLength * Scale );
			json.WriteEndObject();
		}
		json.WriteEndArray();

		json.WriteStartArray( "Phantoms" );
		foreach ( var phantom in plan.Phantoms )
		{
			json.WriteStartObject();
			json.WriteNumber( "Index", phantom.Index );
			json.WriteString( "Node", phantom.Node );
			json.WriteNumber( "PhantomType", phantom.PhantomType );
			json.WritePropertyName( "Shape" );
			Shape( json, phantom.Shape );
			json.WriteEndObject();
		}
		json.WriteEndArray();

		json.WriteStartArray( "Nodes" );
		foreach ( var node in plan.Nodes )
		{
			json.WriteStartObject();
			json.WriteString( "Node", node.Node );
			json.WriteString( "Parent", node.Parent );
			json.WriteString( "Pose", PoseNames[(int)node.Pose] );
			if ( node.Body >= 0 )
				json.WriteNumber( "Body", node.Body );
			else
				json.WriteNull( "Body" );
			if ( node.Pose == PhysicsNodePose.Default )
			{
				Point( json, "Position", node.Position );
				Rotation( json, "Rotation", node.Rotation );
			}
			json.WriteEndObject();
		}
		json.WriteEndArray();
		json.WriteEndObject();
	}

	static void Shape( Utf8JsonWriter json, PhysicsModelShape shape )
	{
		json.WriteStartObject();
		switch ( shape.Kind )
		{
			case PhysicsModelShapeKind.Sphere:
				json.WriteString( "Type", "sphere" );
				Point( json, "Center", shape.Center );
				Number( json, "Radius", shape.Radius * Scale );
				break;

			case PhysicsModelShapeKind.Pill:
				json.WriteString( "Type", "capsule" );
				Point( json, "A", shape.Bottom );
				Point( json, "B", shape.Top );
				Number( json, "Radius", shape.Radius * Scale );
				break;

			case PhysicsModelShapeKind.Box:
				json.WriteString( "Type", "box" );
				Point( json, "Center", shape.Center );
				Rotation( json, "Rotation", shape.Rotation );
				Point( json, "HalfExtents", shape.HalfExtents );
				Number( json, "ConvexRadius", shape.ConvexRadius * Scale );
				break;

			default:
				json.WriteString( "Type", shape.Kind == PhysicsModelShapeKind.Hull ? "hull" : "triangle" );
				json.WriteStartArray( "Points" );
				foreach ( var p in shape.Points )
					Vector( json, null, p * Scale );
				json.WriteEndArray();
				Number( json, "ConvexRadius", shape.ConvexRadius * Scale );
				break;
		}

		json.WriteString( "Material", shape.Material );
		Number( json, "Friction", shape.Friction );
		Number( json, "Restitution", shape.Restitution );
		json.WriteEndObject();
	}

	static void Number( Utf8JsonWriter json, string name, float value )
	{
		json.WritePropertyName( name );
		LayoutJsonWriter.WriteFloat( json, value );
	}

	static void Numbers( Utf8JsonWriter json, string name, IEnumerable<int> values )
	{
		json.WriteStartArray( name );
		foreach ( var v in values )
			json.WriteNumberValue( v );
		json.WriteEndArray();
	}

	static void Point( Utf8JsonWriter json, string name, Vector3 value ) => Vector( json, name, value * Scale );

	static void Vector( Utf8JsonWriter json, string name, Vector3 value, int count = 3 )
	{
		if ( name is null )
			json.WriteStartArray();
		else
			json.WriteStartArray( name );
		LayoutJsonWriter.WriteFloat( json, value.X );
		LayoutJsonWriter.WriteFloat( json, value.Y );
		if ( count > 2 )
			LayoutJsonWriter.WriteFloat( json, value.Z );
		json.WriteEndArray();
	}

	static void Rotation( Utf8JsonWriter json, string name, Quaternion q )
	{
		json.WriteStartArray( name );
		LayoutJsonWriter.WriteFloat( json, q.X );
		LayoutJsonWriter.WriteFloat( json, q.Y );
		LayoutJsonWriter.WriteFloat( json, q.Z );
		LayoutJsonWriter.WriteFloat( json, q.W );
		json.WriteEndArray();
	}
}
