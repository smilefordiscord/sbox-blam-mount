using System;
using Halo.Data;

namespace Halo.Vehicles;

// One vehicle tag's data, looked up once: the tag, physics, weapon, skeleton and markers.
// The mount's models have no attachments, so markers come from the tag dump.
// As in Halo, a marker's transform is relative to its node, times the node's pose.
public sealed class VehicleDefinition {
	static readonly Dictionary<string, VehicleDefinition> Cache = new();

	public string Path { get; }
	public VehicleTag Tag { get; }
	public PhysicsTag Physics { get; }
	public ModelInfoTag ModelInfo { get; }
	public CollisionModelTag Collision { get; }
	public WeaponTag Weapon { get; }
	public string WeaponPath => Tag.Weapon;
	public VehicleKind Kind { get; }

	// "warthog", "rocket warthog", "ghost" and so on, for the kill feed and HUD.
	public string DisplayName { get; }

	readonly Transform[] _nodeLocal;
	readonly int[] _nodeParent;
	readonly Dictionary<string, int> _nodeIndex = new( StringComparer.OrdinalIgnoreCase );

	public int NodeCount => _nodeLocal.Length;
	public string NodeName( int node ) => ModelInfo.Nodes[node].Name;

	// Nodes the gun aims with, or -1.
	public int GunYawNode { get; }
	public int GunPitchNode { get; }

	public static VehicleDefinition Get( string path ) {
		if ( string.IsNullOrEmpty( path ) ) return null;
		if ( Cache.TryGetValue( path, out var cached ) ) return cached;

		var game = HaloDataStore.Game;
		var tag = game?.Vehicle( path );
		var definition = tag is null ? null : new VehicleDefinition( path, tag, game );
		Cache[path] = definition;
		return definition;
	}

	VehicleDefinition( string path, VehicleTag tag, HaloGameData game ) {
		Path = path;
		Tag = tag;
		Physics = game.PhysicsModel( tag.Physics );
		ModelInfo = game.ModelInfo( tag.Model ) ?? new ModelInfoTag();
		Collision = game.CollisionModel( tag.CollisionModel );
		Weapon = game.Weapon( tag.Weapon );

		Kind = KindOf( tag );

		var file = System.IO.Path.GetFileNameWithoutExtension( path.Split( '/' ).Last() ).Replace( "_mp", "" ).Replace( "mp_", "" ).Trim();
		DisplayName = file switch {
			"rwarthog" => "rocket warthog",
			"c gun turret" => "gun turret",
			_ => file,
		};

		var nodes = ModelInfo.Nodes;
		_nodeLocal = new Transform[nodes.Count];
		_nodeParent = new int[nodes.Count];
		for ( var i = 0; i < nodes.Count; i++ ) {
			var n = nodes[i];
			_nodeLocal[i] = new Transform( Point( n.Translation ), FromHalo( n.Rotation ) );
			_nodeParent[i] = n.Parent is int p && p >= 0 && p < i ? p : -1;
			if ( n.Name is not null ) _nodeIndex.TryAdd( n.Name, i );
		}

		// The nodes the "stand fixed aim-still" overlay turns.
		(GunYawNode, GunPitchNode) = Kind switch {
			VehicleKind.Jeep => (FindNode( "frame gun mount base" ), FindNode( "frame gun" )),
			VehicleKind.Tank => (FindNode( "frame turret rotator" ), FindNode( "frame turret" )),
			VehicleKind.Turret => (FindNode( "frame turret" ), FindNode( "frame turret" )),
			_ => (-1, -1),
		};
	}

	public static VehicleKind KindOf( VehicleTag tag ) => tag.VehicleType switch {
		"human tank" => VehicleKind.Tank,
		"alien scout" => VehicleKind.Scout,
		"alien fighter" => VehicleKind.Fighter,
		"turret" => VehicleKind.Turret,
		"human plane" => VehicleKind.Plane,
		_ => VehicleKind.Jeep,
	};

	public int FindNode( string name ) => name is not null && _nodeIndex.TryGetValue( name, out var i ) ? i : -1;

	MassPointModel _massPoints;
	VehicleMotionDefinition _motion;

	// Null without a physics tag.
	public MassPointModel MassPoints => _massPoints ??= BuildMassPoints( Physics );

	// The vehicle tag fields the per-tick vehicle update reads.
	public VehicleMotionDefinition Motion => _motion ??= BuildMotion( Tag );

	public static MassPointModel BuildMassPoints( PhysicsTag physics ) {
		if ( physics is null || physics.Mass <= 0 || physics.MassPoints is not { Count: > 0 } ) return null;

		static Vector3 V( float[] v, Vector3 fallback ) => v is { Length: >= 3 } ? new Vector3( v[0], v[1], v[2] ) : fallback;

		var model = new MassPointModel {
			Radius = physics.Radius,
			MomentScale = physics.MomentScale,
			Mass = physics.Mass,
			CenterOfMass = V( physics.CenterOfMass, Vector3.Zero ),
			GravityScale = physics.GravityScale,
			GroundFriction = physics.GroundFriction,
			GroundDepth = physics.GroundDepth,
			GroundDampFraction = physics.GroundDampFraction,
			GroundNormalK1 = physics.GroundNormalK1,
			GroundNormalK0 = physics.GroundNormalK0,
			WaterFriction = physics.WaterFriction,
			WaterDepth = physics.WaterDepth,
			WaterDensity = physics.WaterDensity,
			AirFriction = physics.AirFriction,
			XxMoment = physics.XxMoment,
			YyMoment = physics.YyMoment,
			ZzMoment = physics.ZzMoment,
			Powered = physics.PoweredMassPoints.Select( p => new PoweredMassPointDefinition {
				Name = p.Name,
				GroundFriction = p.Flags?.Contains( "ground friction" ) ?? false,
				WaterFriction = p.Flags?.Contains( "water friction" ) ?? false,
				AirFriction = p.Flags?.Contains( "air friction" ) ?? false,
				WaterLift = p.Flags?.Contains( "water lift" ) ?? false,
				AirLift = p.Flags?.Contains( "air lift" ) ?? false,
				Thrust = p.Flags?.Contains( "thrust" ) ?? false,
				Antigrav = p.IsAntigrav,
				AntigravStrength = p.AntigravStrength,
				AntigravOffset = p.AntigravOffset,
				AntigravHeight = p.AntigravHeight,
				AntigravDampFraction = p.AntigravDampFraction,
				AntigravNormalK1 = p.AntigravNormalK1,
				AntigravNormalK0 = p.AntigravNormalK0,
			} ).ToArray(),
			Points = physics.MassPoints.Select( p => new MassPointDefinition {
				Name = p.Name,
				PoweredIndex = p.PoweredMassPoint is int i && i >= 0 && i < physics.PoweredMassPoints.Count ? i : -1,
				Metallic = p.Flags?.Contains( "metallic" ) ?? false,
				Mass = p.Mass,
				Density = p.Density,
				Position = V( p.Position, Vector3.Zero ),
				Forward = V( p.Forward, new Vector3( 1, 0, 0 ) ),
				Up = V( p.Up, new Vector3( 0, 0, 1 ) ),
				FrictionType = p.FrictionType switch {
					"forward" => MassPointFriction.Forward,
					"left" => MassPointFriction.Left,
					"up" => MassPointFriction.Up,
					_ => MassPointFriction.Point,
				},
				FrictionParallelScale = p.FrictionParallelScale,
				FrictionPerpendicularScale = p.FrictionPerpendicularScale,
				Radius = p.Radius,
			} ).ToArray(),
		};
		model.ComputeInertia();
		return model;
	}

	public static VehicleMotionDefinition BuildMotion( VehicleTag tag ) {
		return new VehicleMotionDefinition {
			Kind = KindOf( tag ),
			SpeedWakesPhysics = tag.HasFlag( "speed wakes physics" ),
			TurnWakesPhysics = tag.HasFlag( "turn wakes physics" ),
			DriverPowerWakesPhysics = tag.HasFlag( "driver power wakes physics" ),
			GunnerPowerWakesPhysics = tag.HasFlag( "gunner power wakes physics" ),
			ControlOppositeSpeedSetsBrake = tag.HasFlag( "control opposite speed sets brake" ),
			SlideWakesPhysics = tag.HasFlag( "slide wakes physics" ),
			KillsRidersAtTerminalVelocity = tag.HasFlag( "kills riders at terminal velocity" ),
			MaximumForwardSpeed = tag.MaximumForwardSpeed,
			MaximumReverseSpeed = tag.MaximumReverseSpeed,
			SpeedAcceleration = tag.SpeedAcceleration,
			SpeedDeceleration = tag.SpeedDeceleration,
			MaximumLeftTurn = tag.MaximumLeftTurn,
			MaximumRightTurn = tag.MaximumRightTurn,
			WheelCircumference = tag.WheelCircumference,
			TurnRate = tag.TurnRate,
			MaximumLeftSlide = tag.MaximumLeftSlide,
			MaximumRightSlide = tag.MaximumRightSlide,
			SlideAcceleration = tag.SlideAcceleration,
			SlideDeceleration = tag.SlideDeceleration,
			MinimumFlippingAngularVelocity = tag.MinimumFlippingAngularVelocity,
			MaximumFlippingAngularVelocity = tag.MaximumFlippingAngularVelocity,
			FixedGunPitch = tag.FixedGunPitch,
			PowerupTime = (tag.PoweredSeats ?? new()).Select( x => x.DriverPowerupTime ).ToArray(),
			PowerdownTime = (tag.PoweredSeats ?? new()).Select( x => x.DriverPowerdownTime ).ToArray(),
		};
	}

	// The model origin for a vehicle placed at a spot.
	// Halo puts the center of mass half the bounding radius above the spot and lets it drop onto its mass points.
	public Vector3 SpawnOrigin( Vector3 spot, Rotation rotation ) {
		var model = MassPoints;
		if ( model is null ) return spot;
		var center = spot + Vector3.Up * HaloUnits.ToInches( Tag.BoundingRadius * 0.5f );
		return center - rotation * (model.CenterOfMass * HaloUnits.WorldScale);
	}

	// Halo stores rotations conjugated.
	public static Rotation FromHalo( float[] q ) {
		if ( q is not { Length: >= 4 } ) return Rotation.Identity;
		var r = new Rotation( -q[0], -q[1], -q[2], q[3] );
		var length = MathF.Sqrt( r.x * r.x + r.y * r.y + r.z * r.z + r.w * r.w );
		return length > 1e-6f ? new Rotation( r.x / length, r.y / length, r.z / length, r.w / length ) : Rotation.Identity;
	}

	// A point in world units as inches.
	public static Vector3 Point( float[] p ) => p is { Length: >= 3 } ? new Vector3( p[0], p[1], p[2] ) * HaloUnits.WorldScale : Vector3.Zero;

	// The node's default transform local to its parent, in inches.
	public Transform DefaultLocal( int node ) => node >= 0 && node < _nodeLocal.Length ? _nodeLocal[node] : Transform.Zero;

	// A node's parent, -1 for the root.
	public int NodeParent( int node ) => node >= 0 && node < _nodeParent.Length ? _nodeParent[node] : -1;

	// Model space from local transforms; parents come before children.
	public Transform[] PoseFromLocals( Transform[] locals, Transform[] pose = null ) {
		if ( pose is null || pose.Length != _nodeLocal.Length ) pose = new Transform[_nodeLocal.Length];
		for ( var i = 0; i < pose.Length; i++ ) {
			var local = i < locals.Length ? locals[i] : _nodeLocal[i];
			pose[i] = _nodeParent[i] < 0 ? local : pose[_nodeParent[i]].ToWorld( local );
		}
		return pose;
	}

	// The default pose in model space, with optional extra local rotations per node.
	public Transform[] Pose( Rotation[] extra = null ) {
		var pose = new Transform[_nodeLocal.Length];
		for ( var i = 0; i < pose.Length; i++ ) {
			var local = _nodeLocal[i];
			if ( extra is not null && i < extra.Length )
				local = local.WithRotation( local.Rotation * extra[i] );
			pose[i] = _nodeParent[i] < 0 ? local : pose[_nodeParent[i]].ToWorld( local );
		}
		return pose;
	}

	public int MarkerCount( string name ) => Markers( name ).Count;

	public ModelMarkerTag Marker( string name, int instance = 0 ) {
		var found = Markers( name );
		if ( found.Count == 0 ) return null;
		return found[Math.Clamp( instance, 0, found.Count - 1 )];
	}

	readonly Dictionary<string, List<ModelMarkerTag>> _markers = new( StringComparer.OrdinalIgnoreCase );

	// Markers from each region's base permutation only.
	// The model tag repeats markers in every permutation, but Halo only finds those of the shown one.
	List<ModelMarkerTag> Markers( string name ) {
		if ( string.IsNullOrEmpty( name ) ) return new();
		if ( !_markers.TryGetValue( name, out var found ) )
			_markers[name] = found = ModelInfo.Markers.Where( x => (x.Permutation ?? 0) == 0 && string.Equals( x.Name, name, StringComparison.OrdinalIgnoreCase ) ).ToList();
		return found;
	}

	// A marker in model space, given the nodes' pose.
	public static Transform MarkerTransform( ModelMarkerTag marker, Transform[] pose ) {
		var local = new Transform( Point( marker.Translation ), FromHalo( marker.Rotation ) );
		var node = marker.Node ?? -1;
		return node >= 0 && node < pose.Length ? pose[node].ToWorld( local ) : local;
	}

	public VehicleSeatTag Seat( int index ) => index >= 0 && index < Tag.Seats.Count ? Tag.Seats[index] : null;
	public int SeatCount => Tag.Seats.Count;

	// The seat that fires the vehicle's weapon; on one-seat vehicles it's also the driver's.
	public int GunnerSeat {
		get {
			for ( var i = 0; i < Tag.Seats.Count; i++ )
				if ( Tag.Seats[i].IsGunner ) return i;
			return -1;
		}
	}

	public int DriverSeat {
		get {
			for ( var i = 0; i < Tag.Seats.Count; i++ )
				if ( Tag.Seats[i].IsDriver ) return i;
			return -1;
		}
	}

	// For Vehicle.Destructible. Halo CE's multiplayer vehicles have no vitality, so these are our own picks.
	// Roughly two rockets for a Warthog, just over one for a Ghost and five for a Scorpion.
	public float MaximumVitality {
		get {
			if ( Collision?.MaximumBodyVitality > 0 ) return Collision.MaximumBodyVitality * 4;
			return Kind switch {
				VehicleKind.Tank => 1500,
				VehicleKind.Scout => 350,
				VehicleKind.Fighter => 400,
				VehicleKind.Turret => 300,
				_ => 600,
			};
		}
	}

	// Seconds for the driver's seat to power up or down.
	public float PowerUpTime => Tag.PoweredSeats?.FirstOrDefault()?.DriverPowerupTime is float t && t > 0 ? t : 1;
	public float PowerDownTime => Tag.PoweredSeats?.FirstOrDefault()?.DriverPowerdownTime is float t && t > 0 ? t : 0.1f;

	// Bounding radius in inches.
	public float Radius => HaloUnits.ToInches( Tag.BoundingRadius > 0 ? Tag.BoundingRadius : 1 );
}
