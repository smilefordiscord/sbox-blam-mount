using Blam.Halo1;
using Blam.Halo1.Tags;

namespace Sandbox.Mounting.Blam.Halo1;

// Effects: Halo's events, run a tick at a time.
sealed partial class HaloParticleWorld
{
	// A playing effect.
	sealed class EffectInstance
	{
		public HaloEffectDefinition Definition;
		public GameObject Root;
		public GameObject Controls;

		// Each location's instances: every child of the root named after its marker (Halo makes things at each), else the root.
		public List<GameObject>[] Locations;
		public int LocationsFrame = -1;

		// A located effect's location objects, which ride other parts of its object (HaloEffects.LocatedTag); null when its locations are the root's children.
		public List<GameObject> Located;

		public bool FirstPerson;
		public bool Looping;
		public Carrier Carrier;

		public int Event;
		public float EventTime;
		public float EventDuration;
		public float LastFraction;
		public bool InDuration;
		public bool Stopping;
		public bool Stopped;
		public bool Gone;

		// How many particles each of the event's particle blocks makes, picked as its duration starts.
		public int[] Counts = [];

		public float A = 1, B = 1;
		public Vector3 Color = Vector3.One;

		// A velocity it was given when it was made (world units per second): a particle's, for the effect it plays where it hits something.
		public Vector3 Velocity;

		// Halo's effect velocity: its object's, plus what it was given.
		public Vector3 TotalVelocity => Carrier.Velocity + Velocity;

		// The render groups of its particles: the world's, and its first person only particles' when it's drawn with a view model.
		public int Group;
		public int FirstPersonGroup;
	}

	readonly List<EffectInstance> _effects = new();
	readonly Dictionary<GameObject, EffectInstance> _effectsByRoot = new();

	// Most events an effect runs through in one update (zero length events chain).
	const int MaxEventsPerUpdate = 8;

	void StartEffect( HaloEffectDefinition definition, GameObject root, Vector3 velocity )
	{
		var effect = new EffectInstance
		{
			Velocity = velocity,
			Definition = definition,
			Root = root,
			Controls = FindControls( root ),
			FirstPerson = root.Tags.Has( HaloEffects.FirstPersonTag, false ),
			Looping = root.Tags.Has( HaloEffects.LoopTag, false ),
			Carrier = Carrier.For( root ),
			Located = FindLocated( root ),
		};

		effect.Locations = new List<GameObject>[definition.Tag.Locations.Count];
		for ( var i = 0; i < effect.Locations.Length; i++ )
			effect.Locations[i] = new List<GameObject>();
		FindLocations( effect );

		(effect.Group, effect.FirstPersonGroup) = GroupsFor( root );
		(effect.A, effect.B, effect.Color) = ReadControls( effect.Controls );
		effect.Carrier.Measure( _now );

		_effects.Add( effect );
		_effectsByRoot[root] = effect;

		// As Halo: the first event's delay starts now, and an effect without one starts at once.
		if ( definition.Events.Length == 0 )
		{
			effect.Gone = true;
			return;
		}

		SetEvent( effect, 0 );
		UpdateEffect( effect, 0 );
	}

	void UpdateEffects( float dt )
	{
		for ( var i = _effects.Count - 1; i >= 0; i-- )
		{
			var effect = _effects[i];
			var root = effect.Root;
			if ( !effect.Gone && root.IsValid() )
			{
				if ( !root.Active )
				{
					// A carried effect plays its stop events while it's off; any other effect ends, and plays again if it's turned back on.
					if ( !effect.Looping || (effect.Definition.Tag.Flags & 0x1) != 0 )
						effect.Gone = true;
					else if ( !effect.Stopping && !effect.Stopped )
						Stop( effect );
				}
				else if ( effect.Stopped )
				{
					effect.Stopped = false;
					effect.Stopping = false;
					SetEvent( effect, 0 );
				}

				if ( !effect.Gone )
				{
					(effect.A, effect.B, effect.Color) = ReadControls( effect.Controls );
					effect.Carrier.Measure( _now );
					UpdateEffect( effect, dt );
				}
			}

			if ( effect.Gone || !root.IsValid() )
			{
				_effects.RemoveAt( i );
				if ( root is not null && _effectsByRoot.TryGetValue( root, out var known ) && known == effect )
					_effectsByRoot.Remove( root );
				if ( root.IsValid() && root.Active )
					_finished.Add( root );
			}
		}
	}

	// Starts an event's delay.
	void SetEvent( EffectInstance effect, int index )
	{
		var events = effect.Definition.Events;
		if ( index < 0 || index >= events.Length )
			return;

		var ev = events[index].Event;
		effect.InDuration = false;
		effect.Event = index;
		effect.EventTime = 0;
		effect.EventDuration = Range( ev.Delay.X, ev.Delay.Y );
	}

	// Halo's stop for a looping effect: on to the events after the loop if there are any, else stopped at once.
	void Stop( EffectInstance effect )
	{
		var stop = effect.Definition.Tag.LoopStop;
		if ( stop < 0 || stop + 1 >= effect.Definition.Events.Length )
		{
			effect.Stopped = true;
		}
		else
		{
			SetEvent( effect, stop + 1 );
			effect.Stopping = true;
		}
	}

	// Halo's effect update: each event's delay, then its parts, then its particles spread over its duration.
	void UpdateEffect( EffectInstance effect, float dt )
	{
		var events = effect.Definition.Events;
		var tag = effect.Definition.Tag;
		for ( var iteration = 0; dt >= 0; iteration++ )
		{
			if ( effect.Stopped || effect.Gone || iteration >= MaxEventsPerUpdate )
				break;

			bool completed;
			if ( effect.EventDuration - effect.EventTime <= dt )
			{
				dt -= effect.EventDuration - effect.EventTime;
				completed = true;
				effect.EventTime = effect.EventDuration;
			}
			else
			{
				effect.EventTime += dt;
				completed = false;
				dt = -1;
			}

			if ( effect.InDuration )
			{
				GenerateParticles( effect );
				if ( !completed )
					continue;

				var next = effect.Looping && effect.Event == tag.LoopStop && tag.LoopStart >= 0 ? tag.LoopStart : effect.Event + 1;
				while ( next < events.Length && Rand() < events[next].Event.SkipFraction )
					next++;

				if ( next >= events.Length )
				{
					if ( effect.Looping )
						effect.Stopped = true;
					else
						effect.Gone = true;
					return;
				}

				SetEvent( effect, next );
			}
			else if ( completed )
			{
				var ev = events[effect.Event];
				effect.InDuration = true;
				effect.EventTime = 0;
				effect.LastFraction = -1;
				effect.EventDuration = Range( ev.Event.Duration.X, ev.Event.Duration.Y );

				if ( effect.Counts.Length < ev.Particles.Length )
					effect.Counts = new int[ev.Particles.Length];
				for ( var i = 0; i < ev.Particles.Length; i++ )
				{
					var p = ev.Particles[i].Block;
					effect.Counts[i] = Math.Clamp( (int)ScaledRange( effect, p.Count.X, p.Count.Y, p.ScaleA, p.ScaleB, CountBit ), 0, 255 );
				}

				GenerateParts( effect );
			}
		}
	}

	// The values effect scales A and B can scale (a bit for the value, the next for its range).
	const int VelocityBit = 0;
	const int ConeBit = 2;
	const int AngularVelocityBit = 3;
	const int PartScaleBit = 5;
	const int CountBit = 5;
	const int DistributionRadiusBit = 7;
	const int RadiusBit = 9;
	const int TintBit = 11;

	static float Scaled( EffectInstance effect, float value, uint a, uint b, int bit )
	{
		if ( (a & (1u << bit)) != 0 )
			value *= effect.A;
		if ( (b & (1u << bit)) != 0 )
			value *= effect.B;
		return value;
	}

	// Halo's random value between bounds an effect's scales can scale: the lower bound by the value's bit, the range above it by the next bit.
	float ScaledRange( EffectInstance effect, float lower, float upper, uint a, uint b, int bit ) =>
		Scaled( effect, lower, a, b, bit ) + Rand() * Scaled( effect, upper - lower, a, b, bit + 1 );

	// Halo's random velocity: a speed between the bounds, along forward turned by a random part of the cone angle about a random axis.
	(Vector3 Direction, Vector3 Velocity) RandomVelocity( EffectInstance effect, Vector3 forward, System.Numerics.Vector2 speed, float cone, uint a, uint b )
	{
		var magnitude = ScaledRange( effect, speed.X, speed.Y, a, b, VelocityBit );
		var angle = Rand() * Scaled( effect, cone, a, b, ConeBit );
		var direction = forward;
		if ( angle != 0 )
			direction = RotateAbout( direction, RandomDirection(), MathF.Sin( angle ), MathF.Cos( angle ) );

		return (direction, direction * magnitude);
	}

	// Turns a vector about an axis (Rodrigues' rotation, by the angle's sine and cosine).
	static Vector3 RotateAbout( Vector3 v, Vector3 axis, float sine, float cosine ) =>
		v * cosine + axis.Cross( v ) * sine + axis * (axis.Dot( v ) * (1 - cosine));

	// The share of a block's particles made by a fraction of the event's duration (-1: none yet).
	static float DistributionIntegral( int function, float fraction )
	{
		if ( fraction == -1 )
			return 0;

		return function switch
		{
			0 => 1,
			1 => fraction < 1 ? 0 : 1,
			3 => fraction * fraction,
			4 => (2 - fraction) * fraction,
			5 => (3 - 2 * fraction) * fraction * fraction,
			_ => fraction,
		};
	}

	// First person effects leave out third person only blocks, and the others first person only ones.
	static bool ForCamera( EffectInstance effect, int cameraMode ) =>
		effect.FirstPerson ? cameraMode != 2 : cameraMode != 1;

	// Whether something made "in air" (1), "in water" (2) or "in space" (3) can be made at a point (world units); anywhere (0) always.
	bool AllowedAt( int environment, Vector3 point ) => environment switch
	{
		0 => true,
		1 => !Underwater( point ),
		2 => Underwater( point ),
		_ => false,
	};

	// Halo's most instances of one location.
	const int MaxLocationInstances = 16;

	// Looks up each location's instances again: a game may add a marker's other instances after the effect spawns. A location marked nowhere (the object has no marker for it) makes nothing; one with no object at all is the root.
	static void FindLocations( EffectInstance effect )
	{
		var names = effect.Definition.Tag.Locations;
		for ( var i = 0; i < names.Count; i++ )
		{
			var list = effect.Locations[i];
			list.Clear();
			var found = false;
			foreach ( var child in effect.Located ?? (IEnumerable<GameObject>)effect.Root.Children )
			{
				if ( !IsLocation( child, names[i] ) )
					continue;

				found = true;
				if ( !child.Tags.Has( HaloEffects.NowhereTag, false ) && list.Count < MaxLocationInstances )
					list.Add( child );
			}

			if ( list.Count == 0 && !found )
				list.Add( effect.Root );
		}
	}

	// An object named after a location, or tagged as the effect's origin for an unnamed one.
	static bool IsLocation( GameObject go, string name ) =>
		string.IsNullOrEmpty( name ) ? go.Tags.Has( HaloEffects.OriginTag, false ) : go.Name == name;

	// A located effect's location objects: those with its group tag under its scope (the nearest ancestor tagged so, else its parent). Null for any other effect.
	static List<GameObject> FindLocated( GameObject root )
	{
		if ( !root.Tags.Has( HaloEffects.LocatedTag, false ) )
			return null;

		string group = null;
		foreach ( var tag in root.Tags.TryGetAll() )
		{
			if ( HaloEffects.IsLocationGroup( tag ) && root.Tags.Has( tag, false ) )
			{
				group = tag;
				break;
			}
		}

		if ( group is null )
			return null;

		var scope = root.Parent ?? root;
		for ( var go = root.Parent; go is not null && go is not Sandbox.Scene; go = go.Parent )
		{
			if ( go.Tags.Has( HaloEffects.LocationScopeTag, false ) )
			{
				scope = go;
				break;
			}
		}

		var found = new List<GameObject>();
		CollectTagged( scope, group, root, found );
		return found;
	}

	static void CollectTagged( GameObject under, string tag, GameObject except, List<GameObject> found )
	{
		foreach ( var child in under.Children )
		{
			if ( child == except )
				continue;
			if ( child.Tags.Has( tag, false ) )
				found.Add( child );
			CollectTagged( child, tag, except, found );
		}
	}

	// Where a location is (world units) and which way it faces.
	static (Vector3 Position, Rotation Rotation) Place( GameObject location ) =>
		(location.WorldPosition / Units, location.WorldRotation);

	void GenerateParts( EffectInstance effect )
	{
		FindLocations( effect );
		var ev = effect.Definition.Events[effect.Event];
		foreach ( var part in ev.Parts )
		{
			var p = part.Part;
			if ( p.Location < 0 || p.Location >= effect.Locations.Length || p.ViolenceMode == 2 )
				continue;

			foreach ( var location in effect.Locations[p.Location] )
			{
				if ( !location.IsValid() )
					continue;

				var (position, rotation) = Place( location );
				var forward = rotation.Forward;

				// "Face down": straight down whatever the location.
				if ( (p.Flags & 0x1) != 0 )
					forward = Vector3.Down;

				if ( !AllowedAt( p.CreateIn, position ) )
					continue;

				var scale = Scaled( effect, 1, p.ScaleA, p.ScaleB, PartScaleBit );
				if ( part.System is { } system )
				{
					// Halo adds the effect's velocity per tick here, not per second.
					var (_, velocity) = RandomVelocity( effect, forward, p.Velocity, p.ConeAngle, p.ScaleA, p.ScaleB );
					if ( StartSystem( system, null, position, velocity + effect.TotalVelocity * Tick, new Vector4( effect.Color, 1 ), scale, effect.Group, lit: true ) is { } made )
						made.FirstPerson = effect.FirstPerson;
				}
				else if ( part.Light is { } light )
				{
					StartLight( light, location, scale );
				}
			}
		}
	}

	void GenerateParticles( EffectInstance effect )
	{
		if ( _frame != effect.LocationsFrame )
		{
			effect.LocationsFrame = _frame;
			FindLocations( effect );
		}

		var ev = effect.Definition.Events[effect.Event];
		var fraction = effect.EventDuration > 0 ? effect.EventTime / effect.EventDuration : 1;
		var last = effect.LastFraction;

		for ( var i = 0; i < ev.Particles.Length; i++ )
		{
			var block = ev.Particles[i];
			var p = block.Block;
			if ( p.Location < 0 || p.Location >= effect.Locations.Length || p.ViolenceMode == 2 || !ForCamera( effect, p.CameraMode ) )
				continue;

			var made = (int)(DistributionIntegral( p.Distribution, last ) * effect.Counts[i]);
			var delta = (int)(DistributionIntegral( p.Distribution, fraction ) * effect.Counts[i]) - made;
			if ( delta <= 0 )
				continue;

			// Each instance of the location makes the block's particles.
			var group = p.CameraMode == 1 ? effect.FirstPersonGroup : effect.Group;
			foreach ( var location in effect.Locations[p.Location] )
			{
				if ( !location.IsValid() )
					continue;

				var place = Place( location );
				for ( var n = 0; n < delta; n++ )
					CreateParticle( effect, block, location, place, group );
			}
		}

		effect.LastFraction = fraction;
	}

	// One particle of a block, placed and picked between the block's bounds as Halo does.
	void CreateParticle( EffectInstance effect, HaloEffectParticles block, GameObject location, (Vector3 Position, Rotation Rotation) place, int group )
	{
		var p = block.Block;
		var (origin, rotation) = place;

		var distance = ScaledRange( effect, p.DistributionRadius.X, p.DistributionRadius.Y, p.ScaleA, p.ScaleB, DistributionRadiusBit );
		var offset = rotation * new Vector3( p.Offset.X, p.Offset.Y, p.Offset.Z );
		var point = origin + RandomDirection() * distance + offset;

		var (direction, velocity) = RandomVelocity( effect, block.Direction, p.Velocity, p.ConeAngle, p.ScaleA, p.ScaleB );
		direction = rotation * direction;
		velocity = rotation * velocity;

		if ( !AllowedAt( p.CreateIn, point ) )
			return;

		var radius = ScaledRange( effect, p.Radius.X, p.Radius.Y, p.ScaleA, p.ScaleB, RadiusBit );
		var spin = ScaledRange( effect, p.AngularVelocity.X, p.AngularVelocity.Y, p.ScaleA, p.ScaleB, AngularVelocityBit );
		var angle = (p.Flags & 0x2) != 0 ? Rand() * MathF.PI * 2 : 0;

		var t = ((p.ScaleA | p.ScaleB) & (1u << TintBit)) != 0 ? Scaled( effect, 1, p.ScaleA, p.ScaleB, TintBit ) : Rand();
		var rgb = Interpolate( new Vector3( p.TintLower.X, p.TintLower.Y, p.TintLower.Z ), new Vector3( p.TintUpper.X, p.TintUpper.Y, p.TintUpper.Z ), t, (int)((p.Flags >> 3) & 3) );
		var alpha = (1 - t) * p.TintLower.W + p.TintUpper.W * t;

		// A block without a tint (all zero) is white.
		if ( p.TintLower == System.Numerics.Vector4.Zero && p.TintUpper == System.Numerics.Vector4.Zero )
			(rgb, alpha) = (Vector3.One, 1);

		if ( (p.Flags & 0x4) != 0 )
			rgb *= effect.Color;

		var attached = (p.Flags & 0x1) != 0;
		if ( attached )
		{
			// Kept in the location's space, so it moves with it.
			var inverse = rotation.Inverse;
			point = inverse * (point - origin);
			direction = inverse * direction;
			velocity = inverse * velocity;
		}
		else
		{
			velocity += effect.TotalVelocity;
		}

		NewParticle( block.Particle, attached ? location : null, point, direction, velocity, radius, spin, angle, new Vector4( rgb, alpha ), group );
	}

	// Halo's color interpolation between two colors by t: in RGB, or in HSV the short way round the hue circle (1) or the long way (3).
	static Vector3 Interpolate( Vector3 a, Vector3 b, float t, int mode )
	{
		if ( (mode & 1) == 0 )
			return a + (b - a) * t;

		var (h1, s1, v1) = ToHsv( a );
		var (h2, s2, v2) = ToHsv( b );
		// The long way goes the whole circle between equal hues (a grey and a red both have hue 0).
		var dh = h2 - h1;
		var longWay = (mode & 2) != 0;
		if ( (MathF.Abs( dh ) > 0.5f) != longWay )
			dh += h1 < h2 ? -1 : 1;

		var h = h1 + dh * t;
		h -= MathF.Floor( h );
		return FromHsv( h, s1 + (s2 - s1) * t, v1 + (v2 - v1) * t );
	}

	static (float H, float S, float V) ToHsv( Vector3 c )
	{
		var max = MathF.Max( c.x, MathF.Max( c.y, c.z ) );
		var min = MathF.Min( c.x, MathF.Min( c.y, c.z ) );
		var d = max - min;
		var h = 0f;
		if ( d > 1e-6f )
		{
			if ( max == c.x ) h = (c.y - c.z) / d / 6;
			else if ( max == c.y ) h = ((c.z - c.x) / d + 2) / 6;
			else h = ((c.x - c.y) / d + 4) / 6;
			h -= MathF.Floor( h );
		}

		return (h, max > 1e-6f ? d / max : 0, max);
	}

	static Vector3 FromHsv( float h, float s, float v )
	{
		var i = (int)MathF.Floor( h * 6 );
		var f = h * 6 - i;
		var p = v * (1 - s);
		var q = v * (1 - f * s);
		var t = v * (1 - (1 - f) * s);
		return (((i % 6) + 6) % 6) switch
		{
			0 => new Vector3( v, t, p ),
			1 => new Vector3( q, v, p ),
			2 => new Vector3( p, v, t ),
			3 => new Vector3( p, q, v ),
			4 => new Vector3( t, p, v ),
			_ => new Vector3( v, p, q ),
		};
	}
}
