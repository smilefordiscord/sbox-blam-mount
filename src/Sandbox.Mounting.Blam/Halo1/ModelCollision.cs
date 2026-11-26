using System.Text;
using Blam.Halo1;
using Blam.Halo1.Tags;

namespace Sandbox.Mounting.Blam.Halo1;

/// <summary>
/// Gives models the collision of their objects' collision model (<c>coll</c>): physics shapes from
/// each node's collision BSP, and hitboxes per node and damage material, so a game can tell a head
/// from a body.
/// </summary>
/// <remarks>
/// Halo tests each collision node's BSP in the space of the object's node with the same index, so
/// with a skeleton every node with collision becomes a physics body on its bone (bind pose = the
/// node's default pose, shapes in the bone's space), which ModelPhysics can move with the bone and
/// ModelCollider puts at the bind pose. Without a skeleton it's one body in model space. The BSPs
/// used are the base permutation's (the first), like the meshes, unless an object's region
/// permutations are given (see <see cref="GbxModelBuilder.BuildCollision"/>).
///
/// Shapes are convex hulls: the whole BSP when it's convex (the cyborg's limbs, most vehicle
/// parts), otherwise its solid cells (see <see cref="CollisionBsp.SolidCells"/>), which fill it
/// exactly, cut smaller where a cell is too complex for one s&amp;box hull (the middle of a big
/// rock). A BSP that splits into more than <see cref="MaximumHulls"/> cells (big trees and rocks,
/// always static scenery) becomes a triangle mesh instead, which only collides as part of a static
/// or keyframed body.
///
/// Shapes carry Halo's material types as surfaces (<see cref="HaloSurfaces"/>, <c>halo_&lt;type&gt;</c>),
/// so a trace says what it hit. Surfaces are per body, so a node gets a body per material type its
/// pieces are made of (see <see cref="CollisionModelTag.TypedPieces"/>), all on its bone and held
/// together by fixed joints; mesh shapes carry a surface per triangle instead. The surfaces only load
/// on the main thread, which <see cref="GbxModelLoader"/> builds on; elsewhere shapes get the default
/// surface.
///
/// Bodies on different bones are joined so ModelPhysics holds the model together: fixed joints as
/// in the default pose for most models (see <see cref="JoinBones"/>), a ragdoll's joints for
/// characters (see <see cref="RagdollPlan"/>). A character's collision is regrouped by ragdoll part,
/// each node's shapes on its part's bone where they are in the default pose (the pelvis carries the
/// lower spine's, the forearm a jackal's shield), so its bodies only touch the bodies they're
/// jointed to; parts whose bone has no collision get a capsule fitted to their vertices, with the
/// material type most of the character is made of. ModelCollider, which puts every body at its
/// bind pose, gets the same shapes in the same places as without a ragdoll.
///
/// Hitboxes are boxes in the bone's space around each node's surfaces of one material, named for
/// the material ("head", "body", "legs"; repeats numbered from 1, "body 1"), and tagged with the
/// material type ("halo_cyborg_armor", see <see cref="MaterialTypes"/>), "material_&lt;name&gt;",
/// "region_&lt;name&gt;", and "head" for materials Halo counts as headshots. Only models with a
/// skeleton get hitboxes (s&amp;box hitboxes hang on bones).
/// </remarks>
static class ModelCollision
{
	/// <summary>
	/// The most hulls one node's BSP becomes; more and it's a triangle mesh.
	/// </summary>
	public const int MaximumHulls = 64;

	/// <summary>
	/// The most half-edges an s&amp;box hull takes (it numbers them in bytes) and drops a hull with
	/// more. Pieces are split to stay within it even with all their faces as triangles (see
	/// <see cref="CollisionBsp.SolidCells"/>), so a hull has at most 44 corners.
	/// </summary>
	public const int MaximumHullHalfEdges = 256;

	/// <param name="collision">The objects' collision model, or null for a character without one
	/// (its ragdoll is all capsules).</param>
	/// <param name="bones">The bone each of the model's nodes is on, or null for a model without a skeleton.</param>
	/// <param name="pose">Each node's default pose in model space (s&amp;box units).</param>
	/// <param name="ragdoll">A character's ragdoll (see <see cref="RagdollPlan"/>), for a model with a skeleton.</param>
	/// <param name="regionPermutations">The permutation each region shows, by region index, or null for the base permutations.</param>
	/// <returns>The number of physics shapes added.</returns>
	public static int Add( ModelBuilder builder, GbxModelTag model, CollisionModelTag collision, IReadOnlyList<string> bones, Transform[] pose, float scale, RagdollPlan ragdoll = null, IReadOnlyList<int> regionPermutations = null )
	{
		var bodies = new Bodies( builder );
		HitboxSetBuilder hitboxes = null;
		var hitboxNames = new Dictionary<string, int>( StringComparer.OrdinalIgnoreCase );
		var shapes = 0;
		ragdoll = bones is not null ? ragdoll : null;

		for ( var n = 0; n < (collision?.Nodes.Count ?? 0) && n < pose.Length; n++ )
		{
			var bsp = collision.NodeBsp( n, regionPermutations );
			if ( bsp is null || bsp.Vertices.Count == 0 )
				continue;

			// Shapes in the bone's space with a skeleton, in model space without one. A ragdoll
			// puts a node's shapes on its part's bone, where they are in the default pose.
			var bone = bones is not null && n < bones.Count ? bones[n] : null;
			var onNode = ragdoll is not null ? ragdoll.Parts[ragdoll.PartOfNode[n]].Node : n;
			var toBody = bone is null ? pose[n] : onNode == n ? Transform.Zero : pose[onNode].ToLocal( pose[n] );
			Vector3 Point( System.Numerics.Vector3 p ) => toBody.PointToWorld( (Vector3)p * scale );
			shapes += AddShapes( bodies, collision, bsp, bone is not null ? bones[onNode] : null, pose[onNode], Point );

			if ( bone is null )
				continue;

			hitboxes ??= builder.AddHitboxSet();
			foreach ( var group in bsp.Surfaces.Where( s => s.Corners.Length > 0 ).GroupBy( s => s.Material ) )
			{
				var bounds = BBox.FromPoints( group.SelectMany( s => s.Corners ).Select( c => (Vector3)bsp.Vertices[c] * scale ) );
				var material = collision.Material( group.First() );
				var name = !string.IsNullOrWhiteSpace( material?.Name ) ? material.Name : !string.IsNullOrWhiteSpace( collision.Nodes[n].Name ) ? collision.Nodes[n].Name : $"node {n}";
				var number = hitboxNames.GetValueOrDefault( name );
				hitboxNames[name] = number + 1;

				var hitbox = hitboxes.AddBox( number > 0 ? $"{name} {number}" : name, bone, bounds );
				if ( material is not null )
				{
					if ( MaterialTypes.Tag( material.MaterialType ) is { } type )
						hitbox.AddTag( type );
					hitbox.AddTag( $"material_{TagName( material.Name )}" );
					if ( material.IsHead )
						hitbox.AddTag( "head" );
				}

				var region = collision.Nodes[n].Region;
				if ( region >= 0 && region < collision.Regions.Count )
					hitbox.AddTag( $"region_{TagName( collision.Regions[region].Name )}" );
			}
		}

		if ( ragdoll is not null )
			shapes += AddRagdoll( builder, bodies, ragdoll, bones, pose, scale );
		else if ( bones is not null )
			JoinBones( builder, bodies, model, bones, pose );

		return shapes;
	}

	/// <summary>
	/// Holds the bodies of a model without a ragdoll together, so ModelPhysics moves it as one
	/// rigid object (a Banshee's wings stay on): each bone's first body is fixed to its nearest
	/// ancestor's with one (see <see cref="RagdollPlan.RigidParents"/>), as it is in the default pose.
	/// </summary>
	static void JoinBones( ModelBuilder builder, Bodies bodies, GbxModelTag model, IReadOnlyList<string> bones, Transform[] pose )
	{
		bool HasBody( int n ) => n < bones.Count && bodies.FirstOn( bones[n] ) is not null;
		var parents = RagdollPlan.RigidParents( model, HasBody );
		for ( var n = 0; n < parents.Length; n++ )
		{
			if ( parents[n] >= 0 )
				builder.AddFixedJoint( bodies.FirstOn( bones[parents[n]] ).Value, bodies.FirstOn( bones[n] ).Value, Normalized( pose[parents[n]].ToLocal( pose[n] ) ), Transform.Zero );
		}
	}

	/// <summary>
	/// Makes a character's bodies a ragdoll: a body on each part's bone (its collision, or the
	/// part's capsule where its bone has none) with the part's mass, jointed to its parent part's.
	/// </summary>
	/// <returns>The number of capsules added.</returns>
	static int AddRagdoll( ModelBuilder builder, Bodies bodies, RagdollPlan ragdoll, IReadOnlyList<string> bones, Transform[] pose, float scale )
	{
		var capsules = 0;
		var main = new int[ragdoll.Parts.Count];
		for ( var p = 0; p < ragdoll.Parts.Count; p++ )
		{
			var part = ragdoll.Parts[p];
			var bone = bones[part.Node];
			if ( !part.HasCollision || bodies.FirstOn( bone ) is null )
			{
				var body = bodies.For( bone, pose[part.Node], part.MaterialType );
				var (a, b) = ((Vector3)part.CapsuleA * scale, (Vector3)part.CapsuleB * scale);
				if ( a.Distance( b ) < 0.01f )
					body.AddSphere( new Sphere( (a + b) / 2, part.Radius * scale ) );
				else
					body.AddCapsule( new Capsule( a, b, part.Radius * scale ) );
				capsules++;
			}

			main[p] = bodies.FirstOn( bone ).Value;
			bodies[main[p]].Mass = part.Mass;
		}

		for ( var p = 1; p < ragdoll.Parts.Count; p++ )
		{
			var part = ragdoll.Parts[p];
			var joint = part.Joint;
			var parentNode = ragdoll.Parts[part.Parent].Node;
			var anchor = (Vector3)joint.Anchor * scale;
			var frame1 = Normalized( pose[parentNode].ToLocal( new Transform( anchor, GbxSkeleton.ToRotation( joint.ParentFrame ) ) ) );
			var frame2 = Normalized( pose[part.Node].ToLocal( new Transform( anchor, GbxSkeleton.ToRotation( joint.ChildFrame ) ) ) );

			if ( joint.Type == RagdollJointType.Hinge )
				builder.AddHingeJoint( main[part.Parent], main[p], frame1, frame2 ).WithTwistLimit( joint.TwistMin, joint.TwistMax );
			else
				builder.AddBallJoint( main[part.Parent], main[p], frame1, frame2 ).WithSwingLimit( joint.Swing ).WithTwistLimit( joint.TwistMin, joint.TwistMax );
		}

		return capsules;
	}

	/// <summary>
	/// A joint frame: unit scale and an exactly unit rotation, which joints insist on.
	/// </summary>
	static Transform Normalized( Transform frame ) => new( frame.Position, frame.Rotation.Normal );

	/// <summary>
	/// Adds a node BSP's convex pieces as hulls, on the body of each one's material type, or its
	/// surfaces as a mesh with a surface per triangle if there are too many pieces.
	/// </summary>
	/// <param name="bone">The node's bone, or null to put the shapes on the model's bodies.</param>
	static int AddShapes( Bodies bodies, CollisionModelTag collision, CollisionBsp bsp, string bone, Transform bindPose, Func<System.Numerics.Vector3, Vector3> point )
	{
		var pieces = collision.TypedPieces( bsp, MaximumHullHalfEdges );
		if ( pieces.Count > 0 && pieces.Count <= MaximumHulls )
		{
			foreach ( var (type, piece) in pieces )
				bodies.For( bone, bindPose, type ).AddHull( piece.Select( point ).ToArray() );
			return pieces.Count;
		}

		var triangleSurfaces = new List<int>();
		var indices = bsp.Triangles( triangleSurfaces );
		if ( indices.Count < 3 )
			return 0;

		var types = triangleSurfaces.Select( s => collision.MaterialTypeOf( bsp.Surfaces[s].Material ) ).ToList();
		var main = types.GroupBy( t => t ).MaxBy( g => g.Count() ).Key;
		bodies.For( bone, bindPose, main ).AddMesh( bsp.Vertices.Select( point ).ToArray(), indices.Select( i => (uint)i ).ToArray(), types.Select( bodies.SurfaceSlot ).ToArray() );
		return 1;
	}

	/// <summary>
	/// The surface of a material type, or null (the default surface) for none, or off the main thread.
	/// </summary>
	static Surface SurfaceOf( int type ) =>
		MaterialTypes.IsValid( type ) && ThreadSafe.IsMainThread ? HaloSurfaces.Get( (MaterialType)type ) : null;

	/// <summary>
	/// A model's physics bodies: one per material type on each bone (or in model space without a
	/// skeleton, where the others are held to the first by fixed joints so they move as one).
	/// The first on each bone is the one joined to other bones' (see <see cref="JoinBones"/>, <see cref="AddRagdoll"/>).
	/// </summary>
	sealed class Bodies( ModelBuilder builder )
	{
		readonly Dictionary<(string Bone, int Type), PhysicsBodyBuilder> _bodies = new();
		readonly List<PhysicsBodyBuilder> _list = new();
		readonly Dictionary<string, int> _firstOnBone = new();
		readonly Dictionary<int, byte> _surfaceSlots = new();

		/// <summary>A body by its index, in the order they were added.</summary>
		public PhysicsBodyBuilder this[int index] => _list[index];

		/// <summary>The index of the first body on a bone, or null if it has none.</summary>
		public int? FirstOn( string bone ) => _firstOnBone.TryGetValue( bone ?? "", out var first ) ? first : null;

		public PhysicsBodyBuilder For( string bone, Transform bindPose, int type )
		{
			var key = (bone ?? "", type);
			if ( _bodies.TryGetValue( key, out var body ) )
				return body;

			body = builder.AddBody( surface: SurfaceOf( type ), boneName: bone );
			if ( bone is not null )
				body.SetBindPose( bindPose );
			_bodies[key] = body;

			// Bodies on one bone already move as one with it, and a joint between them would join
			// the bone's body to itself. Without a skeleton they're separate bodies, held by a joint.
			if ( !_firstOnBone.TryGetValue( key.Item1, out var first ) )
				_firstOnBone[key.Item1] = _list.Count;
			else if ( bone is null )
				builder.AddFixedJoint( first, _list.Count );

			_list.Add( body );
			return body;
		}

		/// <summary>
		/// The index of a material type's surface in the model's surfaces, for mesh triangles.
		/// </summary>
		public byte SurfaceSlot( int type )
		{
			if ( !_surfaceSlots.TryGetValue( type, out var slot ) )
			{
				slot = (byte)_surfaceSlots.Count;
				_surfaceSlots[type] = slot;
				builder.AddSurface( SurfaceOf( type ) );
			}

			return slot;
		}
	}

	/// <summary>
	/// A name as a tag: lowercase, with anything but letters and digits as underscores.
	/// </summary>
	static string TagName( string name )
	{
		var tag = new StringBuilder( name.Length );
		foreach ( var c in name.Trim().ToLowerInvariant() )
			tag.Append( char.IsAsciiLetterOrDigit( c ) ? c : '_' );
		return tag.ToString();
	}
}
