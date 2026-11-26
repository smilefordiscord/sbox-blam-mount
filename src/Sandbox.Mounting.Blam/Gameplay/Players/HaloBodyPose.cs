using System;
using Halo;
using Halo.Combat;
using Halo.Data;

namespace HaloWeapons;

// A Sandbox player posed by Halo's Spartan animation. The animation runs headless on the Spartan's skeleton and each frame its bone rotations
// carry onto the player's own model, matched by role and corrected for the difference between the two rest poses.
// A seated rider takes the whole body (getting in, sitting, aiming and looking at the seat), the hands and feet then reaching where Halo's are,
// so the wheel, handles and pedals line up whatever the model's proportions. A player holding a Halo weapon takes only the upper body (Halo's
// standing, aiming, firing, reloading, melee and throwing for the weapon's type) over Sandbox's legs, the weapon on Halo's right hand marker
// and the left hand reaching where Halo's grips it. Works on any model with the citizen skeleton (Sandbox's human and citizen).
public sealed class HaloBodyPose {
	// A player model bone, the Halo node it follows (and a second one to blend toward), and the child each points at in its rest pose.
	readonly record struct Link( string Target, string Source, string Aim = null, string SourceAim = null, string SourceB = null, float Blend = 0 );

	readonly Link[] _links;

	// Halo CE's Max biped and Halo 2's Spartan name their spines, arms and legs alike; their toes and fingers differ.
	static Link[] BuildLinks( bool halo2 ) {
		var links = new List<Link> {
			new( "pelvis", "pelvis", "spine_0", "spine" ),
			new( "spine_0", "spine", "neck_0", "neck" ),
			new( "spine_1", "spine", "neck_0", "neck", "spine1", 0.5f ),
			new( "spine_2", "spine1", "neck_0", "neck" ),
			new( "neck_0", "neck", "head", "head" ),
			new( "head", "head" ),
		};
		foreach ( var (side, halo) in new[] { ("L", "l"), ("R", "r") } ) {
			var toe = halo2 ? $"{halo} toe" : $"{halo} toe0";
			links.Add( new( $"clavicle_{side}", $"{halo} clavicle", $"arm_upper_{side}", $"{halo} upperarm" ) );
			links.Add( new( $"arm_upper_{side}", $"{halo} upperarm", $"arm_lower_{side}", $"{halo} forearm" ) );
			links.Add( new( $"arm_lower_{side}", $"{halo} forearm", $"hand_{side}", $"{halo} hand" ) );
			links.Add( halo2 ? new( $"hand_{side}", $"{halo} hand", $"finger_index_0_{side}", $"{halo} index1" ) : new( $"hand_{side}", $"{halo} hand", $"finger_middle_0_{side}", $"{halo} finger2" ) );
			links.Add( new( $"leg_upper_{side}", $"{halo} thigh", $"leg_lower_{side}", $"{halo} calf" ) );
			links.Add( new( $"leg_lower_{side}", $"{halo} calf", $"ankle_{side}", $"{halo} foot" ) );
			links.Add( new( $"ankle_{side}", $"{halo} foot", $"ball_{side}", toe ) );
			links.Add( new( $"ball_{side}", toe ) );

			if ( halo2 ) {
				// Halo 2's Spartan has a thumb, index and ring finger of two segments each; the middle finger follows the ring.
				foreach ( var (finger, source) in new[] { ("thumb", "thumb"), ("index", "index"), ("ring", "ring"), ("middle", "ring") } ) {
					links.Add( new( $"finger_{finger}_0_{side}", $"{halo} {source}1", $"finger_{finger}_1_{side}", $"{halo} {source}2" ) );
					links.Add( new( $"finger_{finger}_1_{side}", $"{halo} {source}2" ) );
				}
				continue;
			}

			// Max biped fingers: finger0 is the thumb, then index, middle, ring; finger<n>1 and <n>2 are its next two segments.
			foreach ( var (finger, index) in new[] { ("thumb", 0), ("index", 1), ("middle", 2), ("ring", 3) } ) {
				for ( var segment = 0; segment < 3; segment++ ) {
					var source = $"{halo} finger{index}{(segment == 0 ? "" : segment.ToString())}";
					var aim = segment < 2 ? $"finger_{finger}_{segment + 1}_{side}" : null;
					var sourceAim = segment < 2 ? $"{halo} finger{index}{segment + 1}" : null;
					links.Add( new( $"finger_{finger}_{segment}_{side}", source, aim, sourceAim ) );
				}
			}
		}
		return links.ToArray();
	}

	// The ends Halo's hands and feet reach for: the upper bone, the lower bone and the end bone.
	static readonly (string Upper, string Lower, string End)[] Limbs = {
		("arm_upper_L", "arm_lower_L", "hand_L"), ("arm_upper_R", "arm_lower_R", "hand_R"),
		("leg_upper_L", "leg_lower_L", "ankle_L"), ("leg_upper_R", "leg_lower_R", "ankle_R"),
	};

	// The rig for one player model: each bone's link to Halo, and the rotation from Halo's posed node to the bone.
	sealed class Rig {
		public Model Model;
		public BoneCollection.Bone[] Bones;
		public int[] Parent;
		public int[] Source;
		public int[] SourceB;
		public float[] Blend;
		public Rotation[] Offset;
		public Rotation[] OffsetB;
		public (int Upper, int Lower, int End, int Source)[] Limbs;
		public int Pelvis;
		public int HandR, ArmUpperL, ArmLowerL, HandL, ArmUpperR, ArmLowerR, Chest;
		public int ThighL, ThighR, SourceThighL, SourceThighR;
		// The model's arms over Halo's, scaling the right hand's reach from the chest.
		public float ArmScale = 1;
		public bool UpperBody;
		public int SourcePelvis;
		// The model's legs over Halo's, scaling the pelvis's height off the seat.
		public float Scale;
		public bool Valid;
	}

	readonly Model _halo;
	// The animation posing Halo's skeleton: Halo CE's unit animation, or Halo 2's.
	readonly UnitAnimation _animation;
	readonly Halo2.Combat.SpartanAnimation _halo2;
	readonly Dictionary<string, int> _haloNodes = new( StringComparer.OrdinalIgnoreCase );
	readonly Transform[] _haloRest;
	readonly Dictionary<(Model, bool), Rig> _rigs = new();
	Rig _posed;
	Transform[] _world = Array.Empty<Transform>();
	bool[] _set = Array.Empty<bool>();

	// Halo's skeleton as last posed, in the world, and where it stood.
	IReadOnlyList<Transform> _halosPose = Array.Empty<Transform>();
	Transform[] _halo2World = Array.Empty<Transform>();
	Transform _root;

	// How much an upper body's hips turn to Halo's: all of it standing still, none on the move, eased between.
	float _hipWeight;
	float _hipTurn;

	public string GraphPath { get; }

	public UnitAnimation Animation => _animation;

	public Halo2.Combat.SpartanAnimation Halo2Animation => _halo2;

	HaloBodyPose( Model halo, string graph, UnitAnimation animation, Halo2.Combat.SpartanAnimation halo2 ) {
		_halo = halo;
		GraphPath = graph;
		_animation = animation;
		_halo2 = halo2;
		var bones = halo.Bones.AllBones;
		_haloRest = new Transform[bones.Count];
		for ( var i = 0; i < bones.Count; i++ ) {
			_haloRest[i] = bones[i].LocalTransform;
			_haloNodes.TryAdd( Normalize( bones[i].Name ), i );
		}
		_links = BuildLinks( halo2 is not null );
	}

	// The multiplayer Spartan's model and graph; null without Halo CE mounted.
	public static HaloBodyPose Create() {
		var biped = HaloDataStore.Spartan.Biped;
		var graph = biped?.AnimationGraph ?? "characters/cyborg/cyborg.antr";
		var model = HaloResources.LoadModel( biped?.Model ?? "characters/cyborg/cyborg.gbxmodel" );
		var animation = model is null ? null : UnitAnimation.CreateHeadless( model, graph );
		return animation is null ? null : new HaloBodyPose( model, graph, animation, null );
	}

	// Halo 2's multiplayer Spartan and its graph, facing where the player's body does; null without Halo 2 mounted.
	public static HaloBodyPose CreateHalo2() {
		var tag = Halo2.Data.HaloDataStore.SpartanBiped?.Model ?? "objects/characters/masterchief/masterchief_mp.hlmt";
		var model = Halo2.Combat.HaloResources.LoadModel( tag );
		if ( model is null || Halo2.Combat.SpartanAnimation.Create( model, tag ) is not { } animation ) return null;
		animation.FixedFacing = true;
		return new HaloBodyPose( model, tag, null, animation );
	}

	// "bip01 l upperarm", "Bip01_L_UpperArm" and "l upperarm" are the same node.
	static string Normalize( string name ) {
		var n = name.Replace( '_', ' ' ).Trim().ToLowerInvariant();
		return n.StartsWith( "bip01 " ) ? n[6..] : n;
	}

	int HaloNode( string name ) => name is not null && _haloNodes.TryGetValue( Normalize( name ), out var i ) ? i : -1;

	public bool HasSeat( string label ) => _animation?.HasSeat( label ) ?? false;

	// Seconds of the seat's getting in animation, 0 without one.
	public float EnterSeconds( string label ) {
		var seat = UnitAnimationGraph.For( GraphPath )?.FindSeat( label );
		var clip = AnimationClip.Load( GraphPath, seat?.Enter );
		return clip is null ? 0 : clip.FrameCount / HaloUnits.TicksPerSecond;
	}

	// For a new rider.
	public void Reset() {
		_animation?.Reset();
		_halo2?.Reset();
	}

	// Steps Halo's animation by this frame's ticks, draws it at root (the seat, or the player's feet) and poses the player's model to match,
	// all of it or only above the pelvis. False if the model can't take it.
	public bool Apply( SkinnedModelRenderer renderer, Transform root, in UnitAnimation.Input input, bool upperBody = false ) {
		_posed = null;
		if ( _animation is null || RigFor( renderer, upperBody ) is not { } rig ) return false;

		_animation.Root = root.WithScale( 1 );
		for ( var i = 0; i < HaloTick.Advanced; i++ ) _animation.Tick( input );
		_animation.Draw( input );
		_root = _animation.Root;
		Settle( input.Throttle );
		return Retarget( renderer, rig, _animation.WorldPose );
	}

	// Halo 2's: steps the Spartan's animation by this frame's Halo 2 ticks and poses the player's upper body, root at the player's feet.
	// The input's eyes and throttle are relative to the player's body.
	public bool Apply( SkinnedModelRenderer renderer, Transform root, in Halo2.Combat.SpartanAnimation.Input input ) {
		_posed = null;
		if ( _halo2 is null || RigFor( renderer, true ) is not { } rig ) return false;

		for ( var i = 0; i < Halo2.Combat.HaloTick.Advanced; i++ ) _halo2.Tick( input );
		_halo2.Draw();
		var drawn = _halo2.DrawnWorld;
		if ( drawn.Length != _haloRest.Length ) return false;
		_root = root.WithScale( 1 );
		if ( _halo2World.Length != drawn.Length ) _halo2World = new Transform[drawn.Length];
		for ( var i = 0; i < drawn.Length; i++ ) _halo2World[i] = _root.ToWorld( drawn[i] );
		Settle( input.Throttle );
		return Retarget( renderer, rig, _halo2World );
	}

	// Sandbox's legs step for the way the body moves, so the hips only turn to Halo's while it stands still.
	void Settle( Vector2 throttle ) {
		var target = 1 - Math.Clamp( throttle.Length / 0.25f, 0, 1 );
		_hipWeight = _hipWeight.LerpTo( target, 1 - MathF.Exp( -8 * Time.Delta ) );
	}

	// Degrees Halo's hips face from the model's own, about the root's up, each from its thighs.
	float HipTurn( SkinnedModelRenderer renderer, Rig rig, IReadOnlyList<Transform> halo ) {
		if ( rig.ThighL < 0 || rig.ThighR < 0 || rig.SourceThighL < 0 || rig.SourceThighR < 0 ) return 0;
		if ( !renderer.TryGetBoneTransformAnimation( rig.Bones[rig.ThighL], out var left ) || !renderer.TryGetBoneTransformAnimation( rig.Bones[rig.ThighR], out var right ) ) return 0;
		static float Facing( Vector3 across ) => MathF.Atan2( -across.x, across.y ) * 180 / MathF.PI;
		var model = Facing( _root.Rotation.Inverse * (left.Position - right.Position) );
		var haloHips = Facing( _root.Rotation.Inverse * (halo[rig.SourceThighL].Position - halo[rig.SourceThighR].Position) );
		return Angles.NormalizeAngle( haloHips - model );
	}

	Rig RigFor( SkinnedModelRenderer renderer, bool upperBody ) {
		var model = renderer.Model;
		if ( model is null || !renderer.SceneModel.IsValid() ) return null;
		if ( !_rigs.TryGetValue( (model, upperBody), out var rig ) ) _rigs[(model, upperBody)] = rig = BuildRig( model, upperBody );
		return rig.Valid ? rig : null;
	}

	// Halo's marker in the world, as last posed.
	Transform? HaloMarker( string name ) =>
		_halo2 is not null ? _halo2.Marker( name ) is { } m ? _root.ToWorld( m ) : null : _animation.Marker( name );

	// Carries Halo's posed skeleton (world transforms by node) onto the player's model.
	bool Retarget( SkinnedModelRenderer renderer, Rig rig, IReadOnlyList<Transform> halo ) {
		if ( halo.Count != _haloRest.Length ) return false;
		_halosPose = halo;

		var bones = rig.Bones;
		if ( _world.Length < bones.Length ) {
			_world = new Transform[bones.Length];
			_set = new bool[bones.Length];
		}

		// The rotations Halo gives the linked bones, then everything placed down the hierarchy.
		var rotations = new Rotation?[bones.Length];
		for ( var i = 0; i < bones.Length; i++ ) {
			if ( rig.Source[i] < 0 ) continue;
			var rotation = halo[rig.Source[i]].Rotation * rig.Offset[i];
			if ( rig.SourceB[i] >= 0 ) rotation = Rotation.Slerp( rotation, halo[rig.SourceB[i]].Rotation * rig.OffsetB[i], rig.Blend[i] );
			rotations[i] = rotation;
		}

		// Halo's Spartan stands bladed, hips and all, so standing still the model's hips turn as Halo's instead of its waist twisting that much further.
		_hipTurn = rig.UpperBody && _hipWeight > 0.001f ? HipTurn( renderer, rig, halo ) * _hipWeight : 0;
		var pelvis = rig.UpperBody ? Vector3.Zero : PelvisPosition( rig, halo );
		Place( renderer, rig, rotations, pelvis );

		// Hands and feet reach Halo's, bending at the elbows and knees the way they already bend.
		// Holding a weapon, the right hand reaches where Halo's is from the chest, scaled by the arms' lengths, as the same rotations on the model's
		// own shoulders and arms put it elsewhere (Halo 2's shotgun reload swung out to the side instead of up by the head); the left hand then
		// reaches for where Halo's grips the weapon, relative to the right hand that holds it.
		if ( !rig.UpperBody ) {
			foreach ( var limb in rig.Limbs )
				Reach( rotations, limb.Upper, limb.Lower, limb.End, halo[limb.Source].Position );
			Place( renderer, rig, rotations, pelvis );
		}
		else if ( rig.HandR >= 0 && rig.HandL >= 0 && rig.ArmUpperL >= 0 && rig.ArmLowerL >= 0 && rig.Source[rig.HandR] >= 0 && rig.Source[rig.HandL] >= 0 ) {
			if ( rig.Chest >= 0 && rig.Source[rig.Chest] >= 0 && rig.ArmUpperR >= 0 && rig.ArmLowerR >= 0 ) {
				var chest = halo[rig.Source[rig.Chest]];
				var hand = HaloFrame( rig, rig.Chest ).PointToWorld( chest.PointToLocal( halo[rig.Source[rig.HandR]].Position ) * rig.ArmScale );
				Reach( rotations, rig.ArmUpperR, rig.ArmLowerR, rig.HandR, hand );
				Place( renderer, rig, rotations, pelvis );
			}
			var right = halo[rig.Source[rig.HandR]];
			var grip = HaloFrame( rig, rig.HandR ).PointToWorld( right.PointToLocal( halo[rig.Source[rig.HandL]].Position ) );
			Reach( rotations, rig.ArmUpperL, rig.ArmLowerL, rig.HandL, grip );
			Place( renderer, rig, rotations, pelvis );
		}

		var drawn = renderer.WorldTransform;
		for ( var i = 0; i < bones.Length; i++ )
			if ( _set[i] ) renderer.SetBoneTransform( bones[i], drawn.ToLocal( _world[i] ) );
		_posed = rig;
		return true;
	}

	// Where Halo's node for a linked bone would be: at the posed bone, turned back from the bone's rest offset.
	Transform HaloFrame( Rig rig, int bone ) => new( _world[bone].Position, _world[bone].Rotation * rig.Offset[bone].Inverse );

	// A marker on the Spartan's right hand ("right hand", where Halo puts a held weapon's origin) as posed on the player's hand by the last Apply.
	public Transform? HandMarker( string name ) {
		var rig = _posed;
		if ( rig is null || rig.HandR < 0 || rig.Source[rig.HandR] < 0 || HaloMarker( name ) is not { } marker ) return null;
		var hand = _halosPose[rig.Source[rig.HandR]];
		return HaloFrame( rig, rig.HandR ).ToWorld( hand.ToLocal( marker ) );
	}

	// Halo's pelvis off the seat, its height scaled by the model's legs over Halo's.
	Vector3 PelvisPosition( Rig rig, IReadOnlyList<Transform> halo ) {
		var local = _root.PointToLocal( halo[rig.SourcePelvis].Position );
		return _root.PointToWorld( local * rig.Scale );
	}

	// World transforms down the hierarchy: linked bones take their rotation, the rest keep their animated pose relative to their parent.
	// Every bone keeps its own length, so only the pelvis moves to Halo's position.
	void Place( SkinnedModelRenderer renderer, Rig rig, Rotation?[] rotations, Vector3 pelvis ) {
		var bones = rig.Bones;
		for ( var i = 0; i < bones.Length; i++ ) {
			var parent = rig.Parent[i];
			renderer.TryGetBoneTransformAnimation( bones[i], out var animated );
			Transform local;
			if ( parent >= 0 && renderer.TryGetBoneTransformAnimation( bones[parent], out var parentAnimated ) && animated != global::Transform.Zero && parentAnimated != global::Transform.Zero )
				local = parentAnimated.ToLocal( animated );
			else
				local = parent >= 0 ? bones[parent].LocalTransform.ToLocal( bones[i].LocalTransform ) : bones[i].LocalTransform;

			Transform world;
			if ( parent >= 0 && _set[parent] ) world = _world[parent].ToWorld( local );
			else if ( animated != global::Transform.Zero ) world = animated;
			else world = renderer.WorldTransform.ToWorld( bones[i].LocalTransform );

			if ( i == rig.Pelvis && !rig.UpperBody ) world = world.WithPosition( pelvis );
			var turned = i == rig.Pelvis && rig.UpperBody && _hipTurn != 0;
			if ( turned ) world = world.WithRotation( Rotation.FromAxis( _root.Rotation.Up, _hipTurn ) * world.Rotation );
			if ( rotations[i] is { } rotation ) world = world.WithRotation( rotation );
			_world[i] = world.WithScale( 1 );
			_set[i] = parent >= 0 ? _set[parent] || rotations[i] is not null : (i == rig.Pelvis && !rig.UpperBody) || turned || rotations[i] is not null;
		}
	}

	// Two bone reach: turns the upper bone so the lower's far end lands on the target, keeping the joint on the side it's bent toward.
	void Reach( Rotation?[] rotations, int upper, int lower, int end, Vector3 target ) {
		if ( rotations[upper] is not { } upperRotation || rotations[lower] is not { } lowerRotation ) return;
		var a = _world[upper].Position;
		var b = _world[lower].Position;
		var c = _world[end].Position;
		var upperLength = (b - a).Length;
		var lowerLength = (c - b).Length;
		var toTarget = target - a;
		var distance = toTarget.Length;
		if ( upperLength < 0.01f || lowerLength < 0.01f || distance < 0.01f ) return;

		distance = Math.Clamp( distance, MathF.Abs( upperLength - lowerLength ) + 0.01f, upperLength + lowerLength - 0.01f );
		var along = toTarget.Normal;
		var bend = (b - a) - along * Vector3.Dot( b - a, along );
		if ( bend.LengthSquared < 0.0001f ) return;
		bend = bend.Normal;

		var cosine = Math.Clamp( (upperLength * upperLength + distance * distance - lowerLength * lowerLength) / (2 * upperLength * distance), -1, 1 );
		var joint = a + along * (upperLength * cosine) + bend * (upperLength * MathF.Sqrt( 1 - cosine * cosine ));
		var reached = a + along * distance;

		var turnUpper = FromTo( b - a, joint - a );
		rotations[upper] = turnUpper * upperRotation;
		var turnLower = FromTo( turnUpper * (c - b), reached - joint );
		rotations[lower] = turnLower * turnUpper * lowerRotation;
	}

	static Rotation FromTo( Vector3 from, Vector3 to ) {
		if ( from.LengthSquared < 1e-8f || to.LengthSquared < 1e-8f ) return Rotation.Identity;
		from = from.Normal;
		to = to.Normal;
		var dot = Vector3.Dot( from, to );
		if ( dot > 0.99999f ) return Rotation.Identity;
		if ( dot < -0.99999f ) {
			var axis = Vector3.Cross( from, MathF.Abs( from.x ) < 0.9f ? Vector3.Forward : Vector3.Left ).Normal;
			return Rotation.FromAxis( axis, 180 );
		}
		var cross = Vector3.Cross( from, to );
		return new Rotation( cross.x, cross.y, cross.z, 1 + dot ).Normal;
	}

	// The bones Sandbox keeps under a Halo upper body: the pelvis and legs.
	static bool IsLowerBody( string bone ) => bone == "pelvis" || bone.StartsWith( "leg_" ) || bone.StartsWith( "ankle_" ) || bone.StartsWith( "ball_" );

	// Matches the model's bones to Halo's nodes and works out each one's rest offset.
	Rig BuildRig( Model model, bool upperBody ) {
		var bones = model.Bones.AllBones.ToArray();
		var rig = new Rig {
			Model = model,
			Bones = bones,
			Parent = new int[bones.Length],
			Source = new int[bones.Length],
			SourceB = new int[bones.Length],
			Blend = new float[bones.Length],
			Offset = new Rotation[bones.Length],
			OffsetB = new Rotation[bones.Length],
			UpperBody = upperBody,
		};
		var index = new Dictionary<string, int>( StringComparer.OrdinalIgnoreCase );
		for ( var i = 0; i < bones.Length; i++ ) index.TryAdd( bones[i].Name, i );
		for ( var i = 0; i < bones.Length; i++ ) {
			rig.Parent[i] = bones[i].Parent is { } p && index.TryGetValue( p.Name, out var pi ) ? pi : -1;
			rig.Source[i] = rig.SourceB[i] = -1;
		}

		var aligns = new Dictionary<int, Rotation>();
		var matched = 0;
		foreach ( var link in _links ) {
			if ( upperBody && IsLowerBody( link.Target ) ) continue;
			if ( !index.TryGetValue( link.Target, out var t ) ) continue;
			var s = HaloNode( link.Source );
			if ( s < 0 ) continue;

			// The rest poses differ (Sandbox's arms out, Halo's not quite), so each bone first turns from its rest direction to Halo's.
			// A bone with nothing to aim at turns as its parent did.
			var align = Rotation.Identity;
			var aim = link.Aim is null ? -1 : index.GetValueOrDefault( link.Aim, -1 );
			var sourceAim = HaloNode( link.SourceAim );
			if ( aim >= 0 && sourceAim >= 0 )
				align = FromTo( bones[aim].LocalTransform.Position - bones[t].LocalTransform.Position, _haloRest[sourceAim].Position - _haloRest[s].Position );
			else if ( rig.Parent[t] >= 0 && aligns.TryGetValue( rig.Parent[t], out var inherited ) )
				align = inherited;
			aligns[t] = align;

			var rest = align * bones[t].LocalTransform.Rotation;
			rig.Source[t] = s;
			rig.Offset[t] = _haloRest[s].Rotation.Inverse * rest;
			if ( HaloNode( link.SourceB ) is var b and >= 0 ) {
				rig.SourceB[t] = b;
				rig.Blend[t] = link.Blend;
				rig.OffsetB[t] = _haloRest[b].Rotation.Inverse * rest;
			}
			matched++;
		}

		rig.Pelvis = index.GetValueOrDefault( "pelvis", -1 );
		rig.HandR = index.GetValueOrDefault( "hand_R", -1 );
		rig.HandL = index.GetValueOrDefault( "hand_L", -1 );
		rig.ArmUpperL = index.GetValueOrDefault( "arm_upper_L", -1 );
		rig.ArmLowerL = index.GetValueOrDefault( "arm_lower_L", -1 );
		rig.ArmUpperR = index.GetValueOrDefault( "arm_upper_R", -1 );
		rig.ArmLowerR = index.GetValueOrDefault( "arm_lower_R", -1 );
		rig.Chest = index.GetValueOrDefault( "spine_2", -1 );
		var arms = LegLength( i => bones[i].LocalTransform.Position, rig.ArmUpperR, rig.ArmLowerR, rig.HandR );
		var haloArms = LegLength( i => _haloRest[i].Position, HaloNode( "r upperarm" ), HaloNode( "r forearm" ), HaloNode( "r hand" ) );
		rig.ArmScale = arms > 0 && haloArms > 0 ? Math.Clamp( arms / haloArms, 0.5f, 2 ) : 1;
		rig.SourcePelvis = HaloNode( "pelvis" );
		rig.ThighL = index.GetValueOrDefault( "leg_upper_L", -1 );
		rig.ThighR = index.GetValueOrDefault( "leg_upper_R", -1 );
		rig.SourceThighL = HaloNode( "l thigh" );
		rig.SourceThighR = HaloNode( "r thigh" );

		rig.Limbs = Limbs
			.Select( l => (Upper: index.GetValueOrDefault( l.Upper, -1 ), Lower: index.GetValueOrDefault( l.Lower, -1 ), End: index.GetValueOrDefault( l.End, -1 )) )
			.Select( l => (l.Upper, l.Lower, l.End, Source: l.End >= 0 ? rig.Source[l.End] : -1) )
			.Where( l => l.Upper >= 0 && l.Lower >= 0 && l.End >= 0 && l.Source >= 0 )
			.ToArray();

		var legs = LegLength( i => bones[i].LocalTransform.Position, index.GetValueOrDefault( "leg_upper_L", -1 ), index.GetValueOrDefault( "leg_lower_L", -1 ), index.GetValueOrDefault( "ankle_L", -1 ) );
		var haloLegs = LegLength( i => _haloRest[i].Position, HaloNode( "l thigh" ), HaloNode( "l calf" ), HaloNode( "l foot" ) );
		rig.Scale = legs > 0 && haloLegs > 0 ? Math.Clamp( legs / haloLegs, 0.5f, 2 ) : 1;

		// Pelvis, spine, head, arms and legs at the least; spine, head and arms for an upper body.
		rig.Valid = rig.Pelvis >= 0 && rig.SourcePelvis >= 0 && matched >= (upperBody ? 10 : 14);
		Log.Info( $"Halo {(upperBody ? "upper body" : "rider")} pose on {model.ResourcePath}: {matched} bones follow {_halo.ResourcePath}, legs {legs:0.#} vs {haloLegs:0.#}, arms x{rig.ArmScale:0.##}{(rig.Valid ? "" : " (too few, keeping Sandbox's own pose)")}" );
		return rig;
	}

	static float LegLength( Func<int, Vector3> position, int thigh, int calf, int foot ) =>
		thigh < 0 || calf < 0 || foot < 0 ? 0 : (position( calf ) - position( thigh )).Length + (position( foot ) - position( calf )).Length;
}
