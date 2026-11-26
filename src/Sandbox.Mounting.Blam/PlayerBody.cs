namespace Sandbox.Mounting.Blam;

// A Halo biped refitted onto s&box's human skeleton, so Sandbox's player animations, hitboxes and ragdoll drive it as a player model.
// Each Halo node's mesh is turned from Halo's rest direction to the human bone's, scaled to the human's height and moved onto that bone, then skinned to it.
sealed class PlayerBody
{
	public const string HumanModel = "models/citizen_human/citizen_human_male.vmdl";

	// A human bone, the Halo node that follows it, and the child each points at in its rest pose.
	readonly record struct Link( string Target, string Source, string Aim = null, string SourceAim = null );

	// Halo CE's Max biped and Halo 2/3's skeletons name their spines, arms and legs alike; toes and fingers differ, so both spellings are listed.
	static readonly Link[] Links = BuildLinks();

	static Link[] BuildLinks()
	{
		var links = new List<Link>
		{
			new( "pelvis", "pelvis", "spine_0", "spine" ),
			new( "spine_0", "spine", "spine_2", "spine1" ),
			new( "spine_2", "spine1", "neck_0", "neck" ),
			new( "neck_0", "neck", "head", "head" ),
			new( "head", "head" ),
		};

		foreach ( var (side, halo) in new[] { ("L", "l"), ("R", "r") } )
		{
			links.Add( new( $"clavicle_{side}", $"{halo} clavicle", $"arm_upper_{side}", $"{halo} upperarm" ) );
			links.Add( new( $"arm_upper_{side}", $"{halo} upperarm", $"arm_lower_{side}", $"{halo} forearm" ) );
			links.Add( new( $"arm_lower_{side}", $"{halo} forearm", $"hand_{side}", $"{halo} hand" ) );
			links.Add( new( $"hand_{side}", $"{halo} hand", $"finger_middle_0_{side}", $"{halo} finger2" ) );
			links.Add( new( $"hand_{side}", $"{halo} hand", $"finger_index_0_{side}", $"{halo} index1" ) );
			links.Add( new( $"leg_upper_{side}", $"{halo} thigh", $"leg_lower_{side}", $"{halo} calf" ) );
			links.Add( new( $"leg_lower_{side}", $"{halo} calf", $"ankle_{side}", $"{halo} foot" ) );
			links.Add( new( $"ankle_{side}", $"{halo} foot", $"ball_{side}", $"{halo} toe0" ) );
			links.Add( new( $"ankle_{side}", $"{halo} foot", $"ball_{side}", $"{halo} toe" ) );
			links.Add( new( $"ball_{side}", $"{halo} toe0" ) );
			links.Add( new( $"ball_{side}", $"{halo} toe" ) );

			// Max biped fingers: finger0 is the thumb, then index, middle and ring; finger<n>1 and <n>2 are its next two segments.
			foreach ( var (finger, index) in new[] { ("thumb", 0), ("index", 1), ("middle", 2), ("ring", 3) } )
			{
				for ( var segment = 0; segment < 3; segment++ )
				{
					var source = $"{halo} finger{index}{(segment == 0 ? "" : segment.ToString())}";
					var aim = segment < 2 ? $"finger_{finger}_{segment + 1}_{side}" : null;
					var sourceAim = segment < 2 ? $"{halo} finger{index}{segment + 1}" : null;
					links.Add( new( $"finger_{finger}_{segment}_{side}", source, aim, sourceAim ) );
				}
			}

			// Halo 2 and 3: a thumb, index and ring finger of two segments each, the middle finger following the ring.
			foreach ( var (finger, source) in new[] { ("thumb", "thumb"), ("index", "index"), ("ring", "ring"), ("middle", "ring") } )
			{
				links.Add( new( $"finger_{finger}_0_{side}", $"{halo} {source}1", $"finger_{finger}_1_{side}", $"{halo} {source}2" ) );
				links.Add( new( $"finger_{finger}_1_{side}", $"{halo} {source}2" ) );
			}
		}

		return links.ToArray();
	}

	// The nodes a body needs to be worth refitting: everything Sandbox's player animations move.
	static readonly string[] Required = ["pelvis", "spine", "head", "l upperarm", "l forearm", "l hand", "r upperarm", "r forearm", "r hand", "l thigh", "l calf", "l foot", "r thigh", "r calf", "r foot"];

	// "bip01 l upperarm", "Bip01_L_UpperArm" and "l upperarm" are the same node.
	public static string Normalize( string name )
	{
		var n = name.Replace( '_', ' ' ).Trim().ToLowerInvariant();
		return n.StartsWith( "bip01 " ) ? n[6..] : n;
	}

	// Whether a skeleton has the nodes to refit, by name alone.
	public static bool CanFit( IEnumerable<string> nodeNames )
	{
		var names = nodeNames.Select( Normalize ).ToHashSet();
		return Required.All( names.Contains );
	}

	public Model Human { get; private init; }
	public BoneCollection.Bone[] Bones { get; private init; }

	// Per Halo node: the human bone its vertices are skinned to, and where its rest pose moves to.
	int[] _bone;
	Vector3[] _origin;
	Vector3[] _target;
	Rotation[] _align;
	float _scale;

	public float Scale => _scale;

	// Fits a skeleton given as node names, parents and rest pose in model space (s&box units); null when it doesn't fit or the human model isn't there.
	public static PlayerBody Create( IReadOnlyList<string> names, IReadOnlyList<int> parents, IReadOnlyList<Transform> rest, Sandbox.Diagnostics.Logger log )
	{
		if ( !CanFit( names ) )
			return null;

		var human = Model.Load( HumanModel );
		if ( human is null || human.IsError )
		{
			log.Warning( $"Player models need {HumanModel}, which didn't load." );
			return null;
		}

		var bones = human.Bones.AllBones.ToArray();
		if ( bones.Length > 255 )
		{
			log.Warning( $"{HumanModel} has {bones.Length} bones, more than a vertex can index." );
			return null;
		}

		var boneIndex = new Dictionary<string, int>( StringComparer.OrdinalIgnoreCase );
		for ( var i = 0; i < bones.Length; i++ )
			boneIndex.TryAdd( bones[i].Name, i );

		var nodeIndex = new Dictionary<string, int>();
		for ( var i = 0; i < names.Count; i++ )
			nodeIndex.TryAdd( Normalize( names[i] ), i );

		int Node( string name ) => name is not null && nodeIndex.TryGetValue( name, out var i ) ? i : -1;
		int Bone( string name ) => name is not null && boneIndex.TryGetValue( name, out var i ) ? i : -1;
		Vector3 HumanAt( int bone ) => bones[bone].LocalTransform.Position;

		// The human is shorter than most Halo bipeds; the whole body scales to the human's head height.
		var scale = HumanAt( Bone( "head" ) ).z / MathF.Max( rest[Node( "head" )].Position.z, 1 );

		var body = new PlayerBody
		{
			Human = human,
			Bones = bones,
			_bone = Enumerable.Repeat( -1, names.Count ).ToArray(),
			_origin = new Vector3[names.Count],
			_target = new Vector3[names.Count],
			_align = new Rotation[names.Count],
			_scale = scale,
		};

		// Each linked node turns from its rest direction to its bone's, a node with nothing to aim at as its bone's parent did.
		var alignOfBone = new Dictionary<int, Rotation>();
		foreach ( var link in Links )
		{
			var t = Bone( link.Target );
			var s = Node( link.Source );
			if ( t < 0 || s < 0 || body._bone[s] >= 0 )
				continue;

			var aim = Bone( link.Aim );
			var sourceAim = Node( link.SourceAim );
			Rotation align;
			if ( aim >= 0 && sourceAim >= 0 )
				align = Rotation.FromToRotation( (rest[sourceAim].Position - rest[s].Position).Normal, (HumanAt( aim ) - HumanAt( t )).Normal );
			else if ( link.Aim is not null && sourceAim < 0 )
				continue;
			else
				align = bones[t].Parent is { } p && alignOfBone.TryGetValue( p.Index, out var inherited ) ? inherited : Rotation.Identity;

			alignOfBone.TryAdd( t, align );
			body._bone[s] = t;
			body._origin[s] = rest[s].Position;
			body._target[s] = HumanAt( t );
			body._align[s] = align;
		}

		// Nodes no bone follows (Halo's root, extra armour nodes) move with their nearest followed ancestor, else with the pelvis.
		var pelvis = Node( "pelvis" );
		for ( var i = 0; i < names.Count; i++ )
		{
			if ( body._bone[i] >= 0 )
				continue;

			var from = parents[i];
			while ( from >= 0 && body._bone[from] < 0 )
				from = parents[from];
			if ( from < 0 )
				from = pelvis;

			body._bone[i] = body._bone[from];
			body._origin[i] = body._origin[from];
			body._target[i] = body._target[from];
			body._align[i] = body._align[from];
		}

		return body;
	}

	// Where a point of a node's mesh lands on the human.
	public Vector3 Point( int node, Vector3 position ) => _target[node] + _align[node] * ((position - _origin[node]) * _scale);

	// A vertex weighted to Halo nodes, refitted and weighted to the human's bones instead.
	public (Vector3 Position, Vector3 Normal, Vector4 Tangent, Color32 Indices, Color32 Weights) Refit( Vector3 position, Vector3 normal, Vector4 tangent, Color32 nodes, Color32 weights )
	{
		var w0 = weights.r / 255f;
		var w1 = weights.g / 255f;
		var n0 = Math.Min( nodes.r, _bone.Length - 1 );
		var n1 = Math.Min( nodes.g, _bone.Length - 1 );

		var p = Point( n0, position ) * w0 + Point( n1, position ) * w1;
		var n = (_align[n0] * normal * w0 + _align[n1] * normal * w1).Normal;
		var t = (_align[n0] * (Vector3)tangent * w0 + _align[n1] * (Vector3)tangent * w1).Normal;

		var b0 = _bone[n0];
		var b1 = _bone[n1];
		if ( b0 == b1 || weights.g == 0 )
			return (p, n, new Vector4( t, tangent.w ), new Color32( (byte)b0, (byte)b0, 0, 0 ), new Color32( 255, 0, 0, 0 ));

		return (p, n, new Vector4( t, tangent.w ), new Color32( (byte)b0, (byte)b1, 0, 0 ), weights);
	}

	public string[] BoneNames => Bones.Select( b => b.Name ).ToArray();
	public int[] BoneParents => Bones.Select( b => b.Parent?.Index ?? -1 ).ToArray();
	public Transform[] BonePose => Bones.Select( b => b.LocalTransform ).ToArray();

	// The human's attachments (hold_R, hold_L, eyes...), which Sandbox places weapons and cameras on.
	public void AddAttachments( ModelBuilder builder )
	{
		foreach ( var a in Human.Attachments.All )
			builder.AddAttachment( a.Name, a.LocalTransform.Position, a.LocalTransform.Rotation, a.Bone?.Name );
	}

	// The human's hitboxes, on the same bones.
	public void AddHitboxes( ModelBuilder builder )
	{
		if ( Human.HitboxSet is not { } set || set.All.Count == 0 )
			return;

		var output = builder.AddHitboxSet();
		foreach ( var box in set.All )
		{
			if ( box.Bone is null )
				continue;

			var hitbox = box.Shape switch
			{
				Sphere sphere => output.AddSphere( box.Name, box.Bone.Name, sphere ),
				Capsule capsule => output.AddCapsule( box.Name, box.Bone.Name, capsule ),
				BBox bounds => output.AddBox( box.Name, box.Bone.Name, bounds ),
				_ => null,
			};

			if ( hitbox is null )
				continue;
			if ( !string.IsNullOrEmpty( box.SurfaceName ) )
				hitbox.WithSurface( box.SurfaceName );
			foreach ( var tag in box.Tags.TryGetAll() )
				hitbox.AddTag( tag );
		}
	}

	// The human's ragdoll: its bodies and joints as they are.
	public void AddPhysics( ModelBuilder builder )
	{
		if ( Human.Physics is not { } physics )
			return;

		foreach ( var part in physics.Parts )
		{
			var body = builder.AddBody( part.Mass, part.Parts.FirstOrDefault()?.Surface, part.BoneName );
			body.SetBindPose( part.Transform );
			body.SetDamping( part.LinearDamping, part.AngularDamping );
			foreach ( var sphere in part.Spheres )
				body.AddSphere( sphere.Sphere );
			foreach ( var capsule in part.Capsules )
				body.AddCapsule( capsule.Capsule );
			foreach ( var hull in part.Hulls )
				body.AddHull( hull.GetLines().SelectMany( l => new[] { l.Start, l.End } ).Distinct().ToArray() );
		}

		foreach ( var joint in physics.Joints )
		{
			var frame1 = new Transform( joint.Frame1.Position, joint.Frame1.Rotation.Normal );
			var frame2 = new Transform( joint.Frame2.Position, joint.Frame2.Rotation.Normal );
			switch ( joint.Type )
			{
				case PhysicsGroupDescription.JointType.Ball:
					var ball = builder.AddBallJoint( joint.Body1, joint.Body2, frame1, frame2, joint.EnableCollision );
					if ( joint.EnableSwingLimit ) ball.WithSwingLimit( joint.SwingMax );
					if ( joint.EnableTwistLimit ) ball.WithTwistLimit( joint.TwistMin, joint.TwistMax );
					break;
				case PhysicsGroupDescription.JointType.Hinge:
					var hinge = builder.AddHingeJoint( joint.Body1, joint.Body2, frame1, frame2, joint.EnableCollision );
					if ( joint.EnableTwistLimit ) hinge.WithTwistLimit( joint.TwistMin, joint.TwistMax );
					break;
				case PhysicsGroupDescription.JointType.Fixed:
					builder.AddFixedJoint( joint.Body1, joint.Body2, frame1, frame2, joint.EnableCollision );
					break;
			}
		}
	}
}
