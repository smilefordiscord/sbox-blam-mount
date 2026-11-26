using System;
using System.Collections.Generic;
using System.Linq;
using Blam.Halo1.Tags;

namespace Blam.Halo1;

// Inside the namespace so these win over s&box's global types when this compiles into the mount.
using Vector3 = System.Numerics.Vector3;
using Quaternion = System.Numerics.Quaternion;
using Matrix4x4 = System.Numerics.Matrix4x4;

/// <summary>
/// What a node of a character's skeleton is, by its name (3ds Max biped names, "bip01 l forearm",
/// and the "frame" names of the flood carrier, "frame knee left").
/// </summary>
public enum RagdollRole
{
	/// <summary>Anything else: follows its parent rigidly (fingers, eyelids, antennae, a jackal's shield).</summary>
	Other,
	Root,
	Spine,
	Neck,
	Head,
	Clavicle,
	UpperArm,
	Forearm,
	Hand,
	Thigh,
	Calf,
	/// <summary>The extra segment of an elite's or jackal's reversed leg, between calf and foot.</summary>
	Horselink,
	Foot,
}

public enum RagdollJointType
{
	Ball,
	Hinge,
}

/// <summary>
/// How a ragdoll part hangs on its parent part, at the part's bone (in the default pose, model
/// space, world units). Ball joints twist around their frames' z axis and swing within a cone
/// around it; hinges turn around the z axis, positive angles bending the joint further.
/// </summary>
/// <param name="ParentFrame">The joint's rotation as the parent part sees it at rest.</param>
/// <param name="ChildFrame">The joint's rotation as the child part sees it in the default pose
/// (the same as <paramref name="ParentFrame"/> unless the rest is tilted, as at the hips).</param>
/// <param name="Swing">The cone's half angle, degrees (ball joints).</param>
/// <param name="TwistMin">Least twist (ball) or hinge angle, degrees.</param>
/// <param name="TwistMax">Most twist (ball) or hinge angle, degrees.</param>
public sealed record RagdollJoint( RagdollJointType Type, Vector3 Anchor, Quaternion ParentFrame, Quaternion ChildFrame, float Swing, float TwistMin, float TwistMax );

/// <summary>
/// A rigid part of a ragdoll: one bone and the bones that follow it rigidly.
/// </summary>
/// <param name="Node">The bone the part's body is on.</param>
/// <param name="Nodes">Every node that moves with it, <paramref name="Node"/> first.</param>
/// <param name="CapsuleA">One end of the capsule fitted to its vertices, in the bone's space (world units).</param>
/// <param name="CapsuleB">The other end (the same point for a sphere).</param>
/// <param name="Mass">Kilograms.</param>
/// <param name="MaterialType">The material type most of its collision is made of (or the whole
/// collision model's, if it has none), -1 for none.</param>
/// <param name="HasCollision">Whether its bone has collision geometry. Its body holds the collision
/// of all its nodes (on its bone, where it is in the default pose), plus, if its own bone has
/// none, the capsule.</param>
/// <param name="Parent">The part it hangs on, -1 for the root.</param>
/// <param name="Joint">How it hangs there, null for the root.</param>
public sealed record RagdollPart( int Node, RagdollRole Role, IReadOnlyList<int> Nodes, Vector3 CapsuleA, Vector3 CapsuleB, float Radius, float Mass, int MaterialType, bool HasCollision, int Parent, RagdollJoint Joint )
{
	public float Volume => RagdollPlan.CapsuleVolume( Vector3.Distance( CapsuleA, CapsuleB ), Radius );
}

/// <summary>
/// Plans a character's ragdoll: which bones get a body, the shape and mass of each, and the
/// joints between them, from the skeleton's names and the vertices skinned to each bone.
/// </summary>
/// <remarks>
/// <para>Every bone that moves a meaningful part of the mesh gets a body: the pelvis (the root),
/// the upper spine, the head, the arms, hands, legs and feet, and the neck where it's long (elites).
/// Everything else follows its nearest bodied ancestor rigidly: the lower spine (with the pelvis),
/// clavicles and short necks (with the chest), fingers, toes, faces, mandibles, antennae, spikes
/// and shields. A part with fewer than <see cref="MinimumVertices"/> vertices joins its parent too,
/// unless it's a leg or arm segment carrying the rest of the limb (an elite's calf).</para>
///
/// <para>Each part gets a capsule around the vertices whose heaviest weight is one of its bones,
/// in its bone's space: along the limb toward its next part where it has one, along the
/// vertices' longest spread otherwise, as long as they reach (2nd to 98th percentile) and as thick
/// as 80% of them. Vertices of a biped's "frame" bones (attachments like shields and antennae) are
/// left out. The capsule is the part's shape only where its bone has no collision geometry;
/// either way it gives the part its mass (see <see cref="ReferenceMass"/>).</para>
///
/// <para>Joints sit at each part's bone, its rest pose being the default pose: ball joints at the
/// spine, neck, head, shoulders, wrists, hips and ankles (cone and twist limits), hinges at the
/// elbows, knees and horselinks. A hinge bends the way the default pose already bends it, or,
/// straight, forward at the elbow and backward at the knee. The hips rest tilted forward, so
/// legs swing further forward than back.</para>
///
/// <para>Only skeletons with two thighs get a ragdoll (humans, elites, grunts, jackals, hunters,
/// flood combat forms and carriers); sentinels, monitors and infection forms don't.</para>
/// </remarks>
public sealed class RagdollPlan
{
	/// <summary>The fewest vertices a part of its own needs.</summary>
	public const int MinimumVertices = 8;

	/// <summary>
	/// Kilograms per cubic metre of capsule, before the total is drawn toward <see cref="ReferenceMass"/>.
	/// </summary>
	public const float Density = 1000;

	/// <summary>
	/// A person's mass, kilograms. A ragdoll whose capsules weigh m at <see cref="Density"/> weighs
	/// sqrt(m × this) in all (the crewman ~90 kg, the cyborg ~130, a grunt ~190).
	/// </summary>
	public const float ReferenceMass = 90;

	/// <summary>The fitted capsules' radius, as a fraction of the distance to the axis their vertices are within.</summary>
	const float RadiusPercentile = 0.8f;

	public IReadOnlyList<RagdollPart> Parts { get; }

	/// <summary>The part each of the model's nodes moves with.</summary>
	public IReadOnlyList<int> PartOfNode { get; }

	/// <summary>Each node's default pose in model space, world units.</summary>
	public IReadOnlyList<(Vector3 Position, Quaternion Rotation)> Pose { get; }

	/// <summary>
	/// Pairs of nodes whose bodies overlap in the default pose without being jointed to each other
	/// (collision geometry only: capsules are thinned to stay clear), which push apart when the
	/// ragdoll starts.
	/// </summary>
	public IReadOnlyList<(int NodeA, int NodeB)> Overlaps { get; }

	public float TotalMass => Parts.Sum( p => p.Mass );

	RagdollPlan( List<RagdollPart> parts, int[] partOfNode, (Vector3, Quaternion)[] pose, List<(int, int)> overlaps )
	{
		Parts = parts;
		PartOfNode = partOfNode;
		Pose = pose;
		Overlaps = overlaps;
	}

	/// <summary>
	/// Whether any biped (<c>bipd</c>) uses a model.
	/// </summary>
	public static bool IsBipedModel( CacheFile cache, TagEntry modelTag ) =>
		cache.Tags.Any( t => t.Class == BipedClass && ObjectTags.GetObjectModel( cache, t )?.Id == modelTag.Id );

	static readonly TagClass BipedClass = new( "bipd" );

	/// <summary>
	/// Plans a model's ragdoll, or returns null if its skeleton isn't a character's.
	/// </summary>
	/// <param name="collision">The model's collision model, for the parts' material types and
	/// which bones already have collision geometry, or null.</param>
	/// <param name="metresPerUnit">How long a world unit is where the ragdoll is used, for the masses.</param>
	public static RagdollPlan Build( CacheFile cache, GbxModelTag model, CollisionModelTag collision, float metresPerUnit )
	{
		var nodes = model.Nodes;
		var count = nodes.Count;
		if ( count < 3 || nodes.Select( ( n, i ) => n.ParentIndex < i ).Any( ok => !ok ) )
			return null;

		var pose = DefaultPose( model );
		var roles = nodes.Select( ( n, i ) => RoleOf( n.Name, n.ParentIndex ) ).ToArray();
		var biped = nodes.Any( n => n.Name.StartsWith( "bip01", StringComparison.OrdinalIgnoreCase ) );

		// The vertices each node carries most of, in model space.
		var vertices = new List<Vector3>[count];
		for ( var i = 0; i < count; i++ )
			vertices[i] = new List<Vector3>();
		foreach ( var region in model.Regions )
		{
			var geometry = region.Permutations.Count > 0 ? region.Permutations[0].DrawnGeometry : -1;
			if ( geometry < 0 || geometry >= model.Geometries.Count )
				continue;

			foreach ( var part in model.Geometries[geometry] )
			{
				foreach ( var v in model.ReadVertices( cache, part ) )
				{
					var node = v.Node1 >= 0 && v.Weight1 > v.Weight0 ? v.Node1 : Math.Max( v.Node0, 0 );
					if ( node < count )
						vertices[node].Add( v.Position );
				}
			}
		}

		// A biped's "frame" nodes are attachments (shields, antennae, spikes, mandibles), whose
		// vertices don't belong in the body's shapes.
		bool Fits( int node ) => !biped || !nodes[node].Name.StartsWith( "frame", StringComparison.OrdinalIgnoreCase );

		// Which part each node moves with: its own (owner = itself) or its parent's.
		var owner = new int[count];
		for ( var i = 0; i < count; i++ )
		{
			var parent = nodes[i].ParentIndex;
			owner[i] = parent < 0 ? (i == 0 ? 0 : owner[0]) : OwnsPart( i, parent ) ? i : owner[parent];
		}

		bool OwnsPart( int i, int parent )
		{
			var parentRole = roles[owner[parent]];
			if ( parentRole is RagdollRole.Head or RagdollRole.Hand or RagdollRole.Foot )
				return false;

			return roles[i] switch
			{
				RagdollRole.UpperArm or RagdollRole.Forearm or RagdollRole.Thigh or RagdollRole.Calf or RagdollRole.Horselink or RagdollRole.Head or RagdollRole.Hand or RagdollRole.Foot => true,
				// The lowest spine bone is part of the pelvis.
				RagdollRole.Spine => roles[parent] != RagdollRole.Root,
				// A neck of its own only where it's long compared to the spine below it.
				RagdollRole.Neck => roles[parent] != RagdollRole.Neck && NeckLength( i ) >= 0.5f * nodes[i].Translation.Length(),
				_ => false,
			};
		}

		float NeckLength( int neck )
		{
			for ( var i = neck + 1; i < count; i++ )
			{
				if ( roles[i] == RagdollRole.Head && IsDescendant( i, neck ) )
					return Vector3.Distance( pose[i].Position, pose[neck].Position );
			}
			return 0;
		}

		bool IsDescendant( int node, int ancestor )
		{
			for ( var n = nodes[node].ParentIndex; n >= 0; n = nodes[n].ParentIndex )
			{
				if ( n == ancestor )
					return true;
			}
			return false;
		}

		int FittingVertices( int part ) => Enumerable.Range( 0, count ).Where( n => owner[n] == part && (n == part || Fits( n )) ).Sum( n => vertices[n].Count );
		bool IsLimb( int part ) => roles[part] is RagdollRole.UpperArm or RagdollRole.Forearm or RagdollRole.Thigh or RagdollRole.Calf or RagdollRole.Horselink;
		bool HasChildPart( int part ) => Enumerable.Range( part + 1, count - part - 1 ).Any( n => owner[n] == n && owner[nodes[n].ParentIndex] == part );

		// Parts with too little to carry join their parent, children first.
		for ( var i = count - 1; i > 0; i-- )
		{
			if ( owner[i] != i || FittingVertices( i ) >= MinimumVertices || (IsLimb( i ) && HasChildPart( i ) && vertices[i].Count > 0) )
				continue;

			var to = owner[nodes[i].ParentIndex];
			for ( var n = 0; n < count; n++ )
			{
				if ( owner[n] == i )
					owner[n] = to;
			}
		}

		var partNodes = Enumerable.Range( 0, count ).Where( n => owner[n] == n ).ToList();
		if ( partNodes.Count( n => roles[n] == RagdollRole.Thigh ) < 2 )
			return null;

		var partIndex = new int[count];
		for ( var n = 0; n < count; n++ )
			partIndex[n] = partNodes.IndexOf( owner[n] );

		// Each part's single child part, which its limb points at.
		int ChildPart( int part )
		{
			var children = partNodes.Where( n => nodes[n].ParentIndex >= 0 && owner[nodes[n].ParentIndex] == part ).ToList();
			return children.Count == 1 ? children[0] : -1;
		}

		var mainMaterial = collision is null ? -1 : MainMaterial( collision, Enumerable.Range( 0, Math.Min( count, collision.Nodes.Count ) ) );

		// Shapes, in each part's bone space.
		var shapes = new List<(Vector3 A, Vector3 B, float Radius)>();
		foreach ( var node in partNodes )
		{
			var toLocal = Quaternion.Conjugate( pose[node].Rotation );
			Vector3 Local( Vector3 p ) => Vector3.Transform( p - pose[node].Position, toLocal );

			var points = Enumerable.Range( 0, count ).Where( n => owner[n] == node && (n == node || Fits( n )) ).SelectMany( n => vertices[n] ).Select( Local ).ToList();
			var child = IsLimb( node ) ? ChildPart( node ) : -1;
			Vector3? axis = child >= 0 ? Vector3.Normalize( Local( pose[child].Position ) ) : null;

			// Too few vertices to show the limb's length: it reaches from its joint to the next.
			if ( child >= 0 && points.Count < 16 )
			{
				var end = Local( pose[child].Position );
				for ( var k = 0; k <= 4; k++ )
					points.Add( end * (k / 4f) );
			}

			shapes.Add( FitCapsule( points, axis ) );
		}

		// The parts, with their materials, masses and joints.
		var parts = new List<RagdollPart>();
		for ( var p = 0; p < partNodes.Count; p++ )
		{
			var node = partNodes[p];
			var role = roles[node];
			var (a, b, radius) = shapes[p];
			var members = Enumerable.Range( 0, count ).Where( n => owner[n] == node ).ToList();
			var material = collision is null ? -1 : MainMaterial( collision, members.Where( n => n < collision.Nodes.Count ) );
			var mass = Density * CapsuleVolume( Vector3.Distance( a, b ), radius ) * metresPerUnit * metresPerUnit * metresPerUnit;
			var hasCollision = collision is not null && node < collision.Nodes.Count && HasCollision( collision, node );

			RagdollJoint joint = null;
			var parent = -1;
			if ( p > 0 )
			{
				parent = partIndex[nodes[node].ParentIndex];
				joint = PlanJoint( node, partNodes[parent], role );
			}

			parts.Add( new RagdollPart( node, role, members, a, b, radius, mass, material >= 0 ? material : mainMaterial, hasCollision, parent, joint ) );
		}

		RagdollJoint PlanJoint( int node, int parentNode, RagdollRole role )
		{
			// Which way the part points from its joint, the axis it twists around: toward its one
			// child part; for a head, hand or foot, on along the segment before it (up the neck,
			// along the forearm, down the shin); otherwise toward its shape.
			var anchor = pose[node].Position;
			var child = ChildPart( node );
			var shape = shapes[partNodes.IndexOf( node )];
			var direction = child >= 0 ? pose[child].Position - anchor
				: role is RagdollRole.Head or RagdollRole.Hand or RagdollRole.Foot ? anchor - pose[parentNode].Position
				: Vector3.Transform( (shape.A + shape.B) / 2, pose[node].Rotation );
			direction = direction.LengthSquared() > 1e-8f ? Vector3.Normalize( direction ) : Vector3.Transform( Vector3.UnitX, pose[node].Rotation );

			switch ( role )
			{
				case RagdollRole.Forearm or RagdollRole.Calf or RagdollRole.Horselink:
				{
					// Bend further the way the default pose bends, or the way the joint bends
					// when it's straight: elbows and horselinks forward, knees back (Halo's x is forward).
					var along = anchor - pose[parentNode].Position;
					along = along.LengthSquared() > 1e-8f ? Vector3.Normalize( along ) : direction;
					var bent = MathF.Acos( Math.Clamp( Vector3.Dot( along, direction ), -1, 1 ) ) * 180 / MathF.PI;
					var flex = role == RagdollRole.Horselink ? 120f : 140f;
					Vector3 hinge;
					float min, max;
					if ( bent > 15 )
					{
						hinge = Vector3.Normalize( Vector3.Cross( along, direction ) );
						(min, max) = (-bent, flex - bent);
					}
					else
					{
						var bend = role == RagdollRole.Calf ? -Vector3.UnitX : Vector3.UnitX;
						hinge = Vector3.Cross( direction, bend );
						hinge = hinge.LengthSquared() > 1e-6f ? Vector3.Normalize( hinge ) : Vector3.Transform( Vector3.UnitZ, pose[node].Rotation );
						(min, max) = (-3f, flex);
					}

					var frame = Frame( hinge, direction );
					return new RagdollJoint( RagdollJointType.Hinge, anchor, frame, frame, 0, min, max );
				}

				default:
				{
					// The middle of the cone is tilted from the default pose at the hips (forward:
					// legs swing further forward than back) and shoulders (down: the arms are out
					// to the sides, and reach down to them).
					var (swing, twist, tilt, toward) = role switch
					{
						RagdollRole.Spine => (35f, 25f, 0f, Vector3.Zero),
						RagdollRole.Neck => (30f, 30f, 0f, Vector3.Zero),
						RagdollRole.Head => (45f, 50f, 0f, Vector3.Zero),
						RagdollRole.UpperArm => (80f, 60f, 25f, -Vector3.UnitZ),
						RagdollRole.Hand => (50f, 30f, 0f, Vector3.Zero),
						RagdollRole.Thigh => (55f, 20f, 30f, Vector3.UnitX),
						RagdollRole.Foot => (30f, 15f, 0f, Vector3.Zero),
						_ => (20f, 15f, 0f, Vector3.Zero),
					};

					var frame = Frame( direction, Perpendicular( direction ) );
					var rest = frame;
					if ( tilt != 0 && Vector3.Cross( direction, toward ) is { } axis && axis.LengthSquared() > 1e-6f )
					{
						// Never past the tilt direction (an arm already hanging down isn't tilted further).
						var angle = MathF.Min( tilt, MathF.Acos( Math.Clamp( Vector3.Dot( direction, toward ), -1, 1 ) ) * 180 / MathF.PI );
						rest = Quaternion.CreateFromAxisAngle( Vector3.Normalize( axis ), angle * MathF.PI / 180 ) * frame;
					}

					return new RagdollJoint( RagdollJointType.Ball, anchor, rest, frame, swing, -twist, twist );
				}
			}
		}

		// The capsules overstate stocky and armoured characters (a grunt's methane tank, the
		// cyborg's armour), so totals are drawn toward a person's.
		var raw = parts.Sum( p => p.Mass );
		var scale = raw > 0 ? ReferenceMass * MathF.Sqrt( raw / ReferenceMass ) / raw : 1;
		for ( var p = 0; p < parts.Count; p++ )
			parts[p] = parts[p] with { Mass = MathF.Max( parts[p].Mass * scale, 0.5f ) };

		var overlaps = SeparateCapsules( parts, collision, pose );
		return new RagdollPlan( parts, partIndex, pose, overlaps );
	}

	/// <summary>
	/// Bodies only stay clear of the bodies they're jointed to, so a capsule overlapping any other
	/// part in the default pose (the left foot the right) would push it away as soon as the
	/// ragdoll starts. Capsules are thinned, to half their radius at most, until they're clear.
	/// Collision geometry stays as it is.
	/// </summary>
	/// <returns>The pairs of nodes whose shapes still overlap.</returns>
	static List<(int NodeA, int NodeB)> SeparateCapsules( List<RagdollPart> parts, CollisionModelTag collision, (Vector3 Position, Quaternion Rotation)[] pose )
	{
		Vector3 World( int node, Vector3 p ) => pose[node].Position + Vector3.Transform( p, pose[node].Rotation );

		// Each part's collision: its nodes', all on its body.
		var hulls = parts.Select( part => part.Nodes
			.Where( n => collision is not null && n < collision.Nodes.Count && HasCollision( collision, n ) )
			.SelectMany( n => collision.TypedPieces( collision.NodeBsp( n ) ).Select( h => (Node: n, Support: Hull( h.Points.Select( v => World( n, v ) ).ToArray() )) ) )
			.ToList() ).ToList();

		bool Jointed( int a, int b ) => parts[a].Parent == b || parts[b].Parent == a;

		List<(int Node, Func<Vector3, Vector3> Support)> Shapes( int p, float radius )
		{
			var shapes = new List<(int, Func<Vector3, Vector3>)>( hulls[p] );
			if ( !parts[p].HasCollision )
			{
				var a = World( parts[p].Node, parts[p].CapsuleA );
				var b = World( parts[p].Node, parts[p].CapsuleB );
				shapes.Add( (parts[p].Node, d => (Vector3.Dot( a, d ) >= Vector3.Dot( b, d ) ? a : b) + Vector3.Normalize( d ) * radius) );
			}
			return shapes;
		}

		IEnumerable<(int, int)> Overlapping( List<(int Node, Func<Vector3, Vector3> Support)> a, List<(int Node, Func<Vector3, Vector3> Support)> b ) =>
			a.SelectMany( x => b.Where( y => Intersects( x.Support, y.Support ) ).Select( y => (x.Node, y.Node) ) );

		for ( var p = 0; p < parts.Count; p++ )
		{
			if ( parts[p].HasCollision )
				continue;

			var radius = parts[p].Radius;
			for ( var q = 0; q < parts.Count; q++ )
			{
				if ( q == p || Jointed( p, q ) )
					continue;

				while ( radius > parts[p].Radius * 0.5f && Overlapping( Shapes( p, radius ).Skip( hulls[p].Count ).ToList(), Shapes( q, parts[q].Radius ) ).Any() )
					radius *= 0.9f;
			}

			parts[p] = parts[p] with { Radius = MathF.Max( radius, parts[p].Radius * 0.5f ) };
		}

		var overlaps = new List<(int, int)>();
		for ( var p = 0; p < parts.Count; p++ )
		{
			for ( var q = p + 1; q < parts.Count; q++ )
			{
				if ( !Jointed( p, q ) )
					overlaps.AddRange( Overlapping( Shapes( p, parts[p].Radius ), Shapes( q, parts[q].Radius ) ).Distinct() );
			}
		}

		return overlaps;
	}


	static Func<Vector3, Vector3> Hull( Vector3[] points ) => d => points.MaxBy( p => Vector3.Dot( p, d ) );

	/// <summary>
	/// Whether two convex shapes, given by their support functions (the furthest point in a
	/// direction), overlap: the Gilbert-Johnson-Keerthi test.
	/// </summary>
	static bool Intersects( Func<Vector3, Vector3> a, Func<Vector3, Vector3> b )
	{
		Vector3 Support( Vector3 d ) => a( d ) - b( -d );

		var simplex = new List<Vector3> { Support( Vector3.UnitX ) };
		var direction = -simplex[0];
		for ( var i = 0; i < 64; i++ )
		{
			if ( direction.LengthSquared() < 1e-14f )
				return true;

			var point = Support( direction );
			if ( Vector3.Dot( point, direction ) < 0 )
				return false;

			simplex.Add( point );
			if ( ContainsOrigin( simplex, ref direction ) )
				return true;
		}

		return true;
	}

	/// <summary>
	/// One GJK step: reduces the simplex (newest point last) to the feature nearest the origin and
	/// points <paramref name="direction"/> at the origin from it, or says it holds the origin.
	/// </summary>
	static bool ContainsOrigin( List<Vector3> simplex, ref Vector3 direction )
	{
		var a = simplex[^1];
		var ao = -a;
		switch ( simplex.Count )
		{
			case 2:
				return Line( simplex, simplex[0], a, ref direction );

			case 3:
				return Triangle( simplex, simplex[0], simplex[1], a, ref direction );

			default:
			{
				var (d, c, b) = (simplex[0], simplex[1], simplex[2]);
				if ( Vector3.Dot( Vector3.Cross( b - a, c - a ), ao ) > 0 )
					return Triangle( simplex, c, b, a, ref direction );
				if ( Vector3.Dot( Vector3.Cross( c - a, d - a ), ao ) > 0 )
					return Triangle( simplex, d, c, a, ref direction );
				if ( Vector3.Dot( Vector3.Cross( d - a, b - a ), ao ) > 0 )
					return Triangle( simplex, b, d, a, ref direction );
				return true;
			}
		}
	}

	static bool Line( List<Vector3> simplex, Vector3 b, Vector3 a, ref Vector3 direction )
	{
		var (ab, ao) = (b - a, -a);
		simplex.Clear();
		if ( Vector3.Dot( ab, ao ) > 0 )
		{
			simplex.AddRange( [b, a] );
			direction = Vector3.Cross( Vector3.Cross( ab, ao ), ab );
			return direction.LengthSquared() < 1e-14f;
		}

		simplex.Add( a );
		direction = ao;
		return false;
	}

	static bool Triangle( List<Vector3> simplex, Vector3 c, Vector3 b, Vector3 a, ref Vector3 direction )
	{
		var (ab, ac, ao) = (b - a, c - a, -a);
		var normal = Vector3.Cross( ab, ac );
		if ( Vector3.Dot( Vector3.Cross( normal, ac ), ao ) > 0 )
		{
			if ( Vector3.Dot( ac, ao ) > 0 )
			{
				simplex.Clear();
				simplex.AddRange( [c, a] );
				direction = Vector3.Cross( Vector3.Cross( ac, ao ), ac );
				return direction.LengthSquared() < 1e-14f;
			}

			return Line( simplex, b, a, ref direction );
		}

		if ( Vector3.Dot( Vector3.Cross( ab, normal ), ao ) > 0 )
			return Line( simplex, b, a, ref direction );

		simplex.Clear();
		var side = Vector3.Dot( normal, ao );
		if ( MathF.Abs( side ) < 1e-14f )
			return true;

		if ( side > 0 )
		{
			simplex.AddRange( [c, b, a] );
			direction = normal;
		}
		else
		{
			simplex.AddRange( [b, c, a] );
			direction = -normal;
		}

		return false;
	}

	/// <summary>
	/// The role of a node, by its name.
	/// </summary>
	public static RagdollRole RoleOf( string name, int parent )
	{
		if ( parent < 0 )
			return RagdollRole.Root;

		var n = name.ToLowerInvariant();
		if ( n.Contains( "pelvis" ) ) return RagdollRole.Root;
		if ( n.Contains( "clavicle" ) || n.Contains( "shoulder" ) ) return RagdollRole.Clavicle;
		if ( n.Contains( "upperarm" ) || n.Contains( "upper arm" ) ) return RagdollRole.UpperArm;
		if ( n.Contains( "forearm" ) || n.Contains( "lower arm" ) ) return RagdollRole.Forearm;
		if ( n.Contains( "finger" ) || n.Contains( "toe" ) ) return RagdollRole.Other;
		if ( n.Contains( "hand" ) ) return RagdollRole.Hand;
		if ( n.Contains( "thigh" ) ) return RagdollRole.Thigh;
		if ( n.Contains( "calf" ) || n.Contains( "knee" ) ) return RagdollRole.Calf;
		if ( n.Contains( "horselink" ) ) return RagdollRole.Horselink;
		if ( n.Contains( "foot" ) ) return RagdollRole.Foot;
		if ( n.Contains( "spine" ) ) return RagdollRole.Spine;
		if ( n.Contains( "neck" ) ) return RagdollRole.Neck;
		if ( n.Contains( "head" ) ) return RagdollRole.Head;
		return RagdollRole.Other;
	}

	/// <summary>
	/// Whether a collision node has geometry the model gets a physics body for.
	/// </summary>
	public static bool HasCollision( CollisionModelTag collision, int node ) =>
		collision.NodeBsp( node ) is { Vertices.Count: > 0, Surfaces.Count: > 0 };

	/// <summary>
	/// The material type most of some collision nodes' surfaces are made of, -1 for none.
	/// </summary>
	static int MainMaterial( CollisionModelTag collision, IEnumerable<int> nodes )
	{
		var counts = new Dictionary<int, int>();
		foreach ( var node in nodes )
		{
			if ( collision.NodeBsp( node ) is not { } bsp )
				continue;

			foreach ( var surface in bsp.Surfaces )
			{
				var type = collision.MaterialTypeOf( surface.Material );
				if ( type >= 0 )
					counts[type] = counts.GetValueOrDefault( type ) + 1;
			}
		}

		return counts.Count == 0 ? -1 : counts.MaxBy( c => c.Value ).Key;
	}

	/// <summary>
	/// For a model without a ragdoll: the node each node with a physics body is held to, rigidly
	/// (its nearest ancestor with one, or the first node with one, for those without), -1 for the
	/// first.
	/// </summary>
	/// <param name="hasBody">Whether a node has a physics body.</param>
	public static int[] RigidParents( GbxModelTag model, Func<int, bool> hasBody )
	{
		var count = model.Nodes.Count;
		var parents = Enumerable.Repeat( -1, count ).ToArray();
		var first = -1;
		for ( var i = 0; i < count; i++ )
		{
			if ( !hasBody( i ) )
				continue;

			if ( first < 0 )
			{
				first = i;
				continue;
			}

			var parent = model.Nodes[i].ParentIndex;
			while ( parent >= 0 && parent < i && !hasBody( parent ) )
				parent = model.Nodes[parent].ParentIndex;
			parents[i] = parent >= 0 && parent < i ? parent : first;
		}

		return parents;
	}

	/// <summary>
	/// A model's nodes in their default pose, in model space (world units).
	/// </summary>
	public static (Vector3 Position, Quaternion Rotation)[] DefaultPose( GbxModelTag model )
	{
		var pose = new (Vector3, Quaternion)[model.Nodes.Count];
		for ( var i = 0; i < pose.Length; i++ )
		{
			var node = model.Nodes[i];
			var parent = node.ParentIndex;
			pose[i] = parent < 0 || parent >= i
				? (node.Translation, node.Rotation)
				: (pose[parent].Item1 + Vector3.Transform( node.Translation, pose[parent].Item2 ), Quaternion.Normalize( pose[parent].Item2 * node.Rotation ));
		}

		return pose;
	}

	public static float CapsuleVolume( float length, float radius ) =>
		MathF.PI * radius * radius * length + 4f / 3 * MathF.PI * radius * radius * radius;

	/// <summary>
	/// A capsule around points: along <paramref name="axis"/> (or their longest spread), through
	/// their middle, as long as 96% of them reach and as thick as most of them are.
	/// </summary>
	static (Vector3 A, Vector3 B, float Radius) FitCapsule( List<Vector3> points, Vector3? axis )
	{
		if ( points.Count == 0 )
			return (Vector3.Zero, Vector3.Zero, 0.01f);

		var center = points.Aggregate( Vector3.Zero, ( s, p ) => s + p ) / points.Count;
		var direction = axis ?? PrincipalAxis( points, center );

		var along = points.Select( p => Vector3.Dot( p - center, direction ) ).OrderBy( t => t ).ToList();
		var across = points.Select( p => (p - center - direction * Vector3.Dot( p - center, direction )).Length() ).OrderBy( d => d ).ToList();
		var radius = MathF.Max( Percentile( across, RadiusPercentile ), 0.005f );
		var start = Percentile( along, 0.02f );
		var end = Percentile( along, 0.98f );

		if ( end - start <= 2 * radius )
		{
			var middle = center + direction * ((start + end) / 2);
			return (middle, middle, MathF.Max( radius, (end - start) / 2 ));
		}

		return (center + direction * (start + radius), center + direction * (end - radius), radius);
	}

	static float Percentile( List<float> sorted, float fraction ) =>
		sorted[Math.Clamp( (int)MathF.Round( fraction * (sorted.Count - 1) ), 0, sorted.Count - 1 )];

	/// <summary>
	/// The direction points spread furthest along (the covariance's main eigenvector).
	/// </summary>
	static Vector3 PrincipalAxis( List<Vector3> points, Vector3 center )
	{
		float xx = 0, xy = 0, xz = 0, yy = 0, yz = 0, zz = 0;
		foreach ( var point in points )
		{
			var d = point - center;
			xx += d.X * d.X; xy += d.X * d.Y; xz += d.X * d.Z;
			yy += d.Y * d.Y; yz += d.Y * d.Z; zz += d.Z * d.Z;
		}

		// Power iteration, from the axis with the most spread.
		var v = xx >= yy && xx >= zz ? Vector3.UnitX : yy >= zz ? Vector3.UnitY : Vector3.UnitZ;
		for ( var i = 0; i < 32; i++ )
		{
			var next = new Vector3( xx * v.X + xy * v.Y + xz * v.Z, xy * v.X + yy * v.Y + yz * v.Z, xz * v.X + yz * v.Y + zz * v.Z );
			if ( next.LengthSquared() < 1e-20f )
				break;
			v = Vector3.Normalize( next );
		}

		return v;
	}

	/// <summary>
	/// A rotation whose z axis is <paramref name="z"/> and whose x axis is as near <paramref name="x"/> as that allows.
	/// </summary>
	static Quaternion Frame( Vector3 z, Vector3 x )
	{
		z = Vector3.Normalize( z );
		x -= z * Vector3.Dot( x, z );
		x = x.LengthSquared() > 1e-8f ? Vector3.Normalize( x ) : Perpendicular( z );
		var y = Vector3.Cross( z, x );
		var basis = new Matrix4x4(
			x.X, x.Y, x.Z, 0,
			y.X, y.Y, y.Z, 0,
			z.X, z.Y, z.Z, 0,
			0, 0, 0, 1 );
		return Quaternion.Normalize( Quaternion.CreateFromRotationMatrix( basis ) );
	}

	/// <summary>
	/// A direction at right angles to <paramref name="v"/>: forward (x) where it can be, else up.
	/// </summary>
	static Vector3 Perpendicular( Vector3 v )
	{
		var x = Vector3.UnitX - v * Vector3.Dot( Vector3.UnitX, v );
		if ( x.LengthSquared() < 0.01f )
			x = Vector3.UnitZ - v * Vector3.Dot( Vector3.UnitZ, v );
		return Vector3.Normalize( x );
	}
}
