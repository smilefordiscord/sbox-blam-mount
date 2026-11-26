using Blam.Halo1;
using Blam.Halo1.Tags;

namespace Sandbox.Mounting.Blam.Halo1;

// Halo's point physics: gravity, air and water friction toward the wind, collisions with the level and water.
sealed partial class HaloParticleWorld
{
	// What a point physics update found.
	const int InAir = 0x1;
	const int InWater = 0x2;
	const int CollidedWithStructure = 0x4;
	const int CollidedWithWater = 0x8;

	// Halo's gravity: 0.0035651792 world units per tick per tick.
	const float Gravity = 0.0035651792f * 30 * 30;

	// The mass over radius cubed of air and of water (their densities x 118613.34).
	const float AirMassOverRadiusCubed = 0.0011f * 118613.34f;
	const float WaterMassOverRadiusCubed = 1.0f * 118613.34f;

	// Most times a point bounces in one update.
	const int MaxCollisions = 3;

	// The level's lighting this frame (see LevelFor), for lighting particles as they're born; null without a Halo level.
	MovingObjectLights.LevelLighting Level => _level;
	MovingObjectLights.LevelLighting _level;
	int _levelFrame = -1;

	void UpdateLevel()
	{
		if ( _levelFrame == _frame )
			return;

		_levelFrame = _frame;
		_level = MovingObjectLights.LevelFor( Scene );
	}

	// Halo's point physics: gravity, friction toward the wind or current, then the move, bouncing up to three times.
	int PointPhysics( HaloPhysics physics, ref Vector3 position, ref Vector3 velocity, float radius, float dt, out Vector3 normal )
	{
		normal = Vector3.Up;
		if ( dt == 0 )
			return 0;

		var result = 0;
		var r2 = radius * radius;
		var r3 = r2 * radius;
		var mass = physics.MassOverRadiusCubed;
		float buoyancy, friction;
		var wind = Vector3.Zero;

		if ( Underwater( position ) )
		{
			mass += WaterMassOverRadiusCubed;
			buoyancy = physics.WaterBuoyancy;
			friction = physics.WaterFriction * r2;
			result |= InWater;
		}
		else
		{
			mass += AirMassOverRadiusCubed;
			buoyancy = physics.AirBuoyancy;
			friction = physics.AirFriction * r2;
			result |= InAir;
			wind = Wind( position, physics );
		}

		mass *= r3;
		if ( physics.NoGravity )
			buoyancy = 0;

		velocity.z += Gravity * buoyancy * dt;

		float t;
		if ( mass == 0 )
			t = friction == 0 ? 0 : 1;
		else
			t = Math.Clamp( dt / mass * friction, 0, 1 );
		velocity += (wind - velocity) * t;

		var structures = physics.CollidesWithStructures;
		var water = physics.CollidesWithWater;
		if ( !structures && !water )
		{
			position += velocity * dt;
			return result;
		}

		for ( var i = 0; dt != 0 && i < MaxCollisions; i++ )
		{
			var delta = velocity * dt;
			if ( !Collide( position, delta, structures, water, out var fraction, out var point, out var hitNormal, out var hitWater ) )
			{
				position += delta;
				break;
			}

			result |= hitWater ? CollidedWithWater : CollidedWithStructure;
			normal = hitNormal;

			// The velocity's part along the surface loses the surface friction, the part into it bounces back by the elasticity.
			var along = hitNormal * velocity.Dot( hitNormal );
			var across = velocity - along;
			velocity = across * (1 - physics.ContactFriction) - along * physics.Elasticity;

			position = point + hitNormal * MathF.Min( radius, 0.005f );
			dt -= fraction * dt;
		}

		return result;
	}

	static readonly string[] LevelTags = Enumerable.Range( 0, 32 ).Select( i => $"bsp_{i}" ).ToArray();

	// A trace against the level's collision only, built once.
	SceneTrace? _levelTrace;

	// Traces this frame (for checking the cost).
	internal int Traces;

	// The first level surface or water surface a moving point hits (world units).
	bool Collide( Vector3 from, Vector3 delta, bool structures, bool water, out float fraction, out Vector3 point, out Vector3 normal, out bool hitWater )
	{
		fraction = 1;
		point = from + delta;
		normal = Vector3.Up;
		hitWater = false;
		var hit = false;

		if ( structures && delta.LengthSquared > 1e-12f )
		{
			_levelTrace ??= Scene.Trace.WithAnyTags( LevelTags );
			Traces++;
			var trace = _levelTrace.Value.Ray( from * Units, (from + delta) * Units ).Run();
			if ( trace.Hit && !trace.StartedSolid )
			{
				hit = true;
				fraction = trace.Fraction;
				point = trace.HitPosition / Units;
				normal = trace.Normal;
			}
		}

		if ( water && delta.z < 0 )
		{
			foreach ( var box in _water )
			{
				var top = box.Maxs.z;
				var end = from.z + delta.z;
				if ( from.z < top || end >= top )
					continue;

				var t = (from.z - top) / -delta.z;
				if ( t >= fraction )
					continue;

				var at = from + delta * t;
				if ( at.x < box.Mins.x || at.x > box.Maxs.x || at.y < box.Mins.y || at.y > box.Maxs.y )
					continue;

				hit = true;
				hitWater = true;
				fraction = t;
				point = at;
				normal = Vector3.Up;
			}
		}

		return hit;
	}

	// Water: the level's water volumes (see WaterVolumes), in world units, looked up again now and then.

	readonly List<BBox> _water = new();
	float _waterTime = float.MinValue;

	void UpdateWater()
	{
		UpdateLevel();
		UpdateWind();
		if ( _now - _waterTime < 2 )
			return;

		_waterTime = _now;
		_water.Clear();
		foreach ( var go in Scene.FindAllWithTag( WaterVolumes.Tag ) )
		{
			if ( go.Components.Get<BoxCollider>() is not { } box )
				continue;

			var half = box.Scale * 0.5f;
			var center = go.WorldTransform.PointToWorld( box.Center );
			_water.Add( new BBox( (center - half) / Units, (center + half) / Units ) );
		}
	}

	// Whether a point (world units) is in the level's water.
	bool Underwater( Vector3 point )
	{
		foreach ( var box in _water )
		{
			if ( box.Contains( point ) )
				return true;
		}

		return false;
	}

	// Wind: Halo's weather palette winds, by the cluster a point is in.

	sealed class WindState
	{
		public float VelocityLower, VelocityUpper;
		public float PitchArea, YawArea;
		public float LocalWeight, LocalRate, Damping;
		public Vector3 Direction;
		public float Magnitude;

		public float VelocityVariance, PitchVariance, YawVariance;
		public Vector3 Velocity;
	}

	sealed class LevelWind
	{
		public readonly List<(StructureBspEffects Bsp, WindState[] States)> Bsps = new();
	}

	static readonly System.Runtime.CompilerServices.ConditionalWeakTable<CacheFile, LevelWind> Winds = new();

	LevelWind _wind;
	CacheFile _windCache;
	int _windTime;

	// Halo's smooth random wind variation: per axis, a looping curve through random directions, sampled where a point is and as time passes.
	static readonly Vector3[][] Variance = MakeVariance();

	static Vector3[][] MakeVariance()
	{
		var random = new Random( 0x57696E64 );
		Vector3 Direction()
		{
			var z = random.NextSingle() * 2 - 1;
			var a = random.NextSingle() * MathF.PI * 2;
			var r = MathF.Sqrt( MathF.Max( 0, 1 - z * z ) );
			return new Vector3( r * MathF.Cos( a ), r * MathF.Sin( a ), z );
		}

		var table = new Vector3[3][];
		for ( var axis = 0; axis < 3; axis++ )
		{
			var points = Enumerable.Range( 0, 8 ).Select( _ => Direction() ).ToArray();
			table[axis] = new Vector3[64];
			for ( var i = 0; i < 64; i++ )
			{
				// A uniform cubic B-spline through the looping control points.
				var segment = i / 8;
				var t = (i % 8) / 8f;
				var p0 = points[(segment + 7) % 8];
				var p1 = points[segment];
				var p2 = points[(segment + 1) % 8];
				var p3 = points[(segment + 2) % 8];
				var t2 = t * t;
				var t3 = t2 * t;
				table[axis][i] = (p0 * (1 - 3 * t + 3 * t2 - t3) + p1 * (4 - 6 * t2 + 3 * t3) + p2 * (1 + 3 * t + 3 * t2 - 3 * t3) + p3 * t3) / 6;
			}
		}

		return table;
	}

	// Halo's wind update, a tick at a time: each weather palette wind's speed and direction wander within its bounds and variation area.
	void UpdateWind()
	{
		var cache = Level?.Cache;
		if ( cache != _windCache )
		{
			_windCache = cache;
			_wind = cache is null ? null : Winds.GetValue( cache, ReadWind );
		}

		if ( _wind is null )
			return;

		var ticks = (int)(_now * 30);
		if ( ticks == _windTime )
			return;

		_windTime = ticks;
		foreach ( var (_, states) in _wind.Bsps )
		{
			foreach ( var state in states )
			{
				if ( state is null )
					continue;

				state.VelocityVariance = Math.Clamp( state.VelocityVariance + (Rand() < 0.5f ? 0.01f : -0.01f), 0, 1 );
				state.PitchVariance = Math.Clamp( state.PitchVariance + (Rand() < 0.5f ? 0.01f : -0.01f), -1, 1 );
				state.YawVariance = Math.Clamp( state.YawVariance + (Rand() < 0.5f ? 0.01f : -0.01f), -1, 1 );

				var speed = (state.VelocityUpper - state.VelocityLower) * state.VelocityVariance + state.VelocityLower;
				var d = state.Direction;
				var yaw = MathF.Atan2( d.y, d.x ) + state.YawArea * state.YawVariance * 0.5f;
				var pitch = MathF.Asin( Math.Clamp( d.z, -1, 1 ) ) + state.PitchArea * state.PitchVariance * 0.5f;
				var direction = new Vector3( MathF.Cos( pitch ) * MathF.Cos( yaw ), MathF.Cos( pitch ) * MathF.Sin( yaw ), MathF.Sin( pitch ) );
				state.Velocity = direction * (state.Magnitude * speed);
			}
		}
	}

	static LevelWind ReadWind( CacheFile cache )
	{
		var wind = new LevelWind();
		try
		{
			var scenario = cache.Tags.FirstOrDefault( t => t.Class == TagClasses.Scenario );
			if ( scenario is null )
				return wind;

			foreach ( var reference in new ScenarioTag( cache, scenario ).StructureBsps )
			{
				var bsp = new StructureBspEffects( cache, reference );
				var states = new WindState[bsp.WeatherPalette.Count];
				for ( var i = 0; i < states.Length; i++ )
				{
					var entry = bsp.WeatherPalette[i];
					if ( entry.Wind is null )
						continue;

					var a = entry.Wind.Address;
					var data = cache.TagData;
					var direction = new Vector3( entry.WindDirection.X, entry.WindDirection.Y, entry.WindDirection.Z );
					states[i] = new WindState
					{
						VelocityLower = data.F32( a ),
						VelocityUpper = data.F32( a + 0x4 ),
						YawArea = data.F32( a + 0x8 ),
						PitchArea = data.F32( a + 0xC ),
						LocalWeight = data.F32( a + 0x10 ),
						LocalRate = data.F32( a + 0x14 ),
						Damping = data.F32( a + 0x18 ),
						Direction = direction.LengthSquared > 1e-8f ? direction.Normal : Vector3.Forward,
						Magnitude = entry.WindMagnitude,
					};
				}

				wind.Bsps.Add( (bsp, states) );
			}
		}
		catch ( Exception e )
		{
			HaloCeMount.Current?.Logger.Warning( $"{cache.Name}: couldn't read the wind: {e.Message}" );
		}

		return wind;
	}

	// Halo's wind at a point: its cluster's weather palette wind with its local variation.
	Vector3 Wind( Vector3 point, HaloPhysics physics )
	{
		if ( _wind is null )
			return Vector3.Zero;

		foreach ( var (bsp, states) in _wind.Bsps )
		{
			var cluster = bsp.FindCluster( new System.Numerics.Vector3( point.x, point.y, point.z ) );
			if ( cluster < 0 )
				continue;

			var index = cluster < bsp.ClusterWeather.Count ? bsp.ClusterWeather[cluster] : -1;
			if ( index < 0 || index >= states.Length || states[index] is not { } state )
				return Vector3.Zero;

			var weight = physics.SimpleWind ? 0 : state.LocalWeight;
			var current = state.Velocity * (1 - weight) + LocalVariation( point, state.LocalRate, state.LocalWeight * ((state.VelocityUpper - state.VelocityLower) * state.VelocityVariance + state.VelocityLower) );
			if ( physics.DampedWind )
				current *= 1 - state.Damping;
			return current;
		}

		return Vector3.Zero;
	}

	Vector3 LocalVariation( Vector3 point, float rate, float magnitude )
	{
		ReadOnlySpan<float> scales = [0.1f, 0.2f, 0.07f];
		var sum = Vector3.Zero;
		for ( var axis = 0; axis < 3; axis++ )
		{
			var key = MathF.Abs( (scales[axis] * _windTime * rate + point[axis]) * 8 );
			var sample = (int)MathF.Round( key ) & 0x3F;
			sum += Variance[axis][sample];
		}

		return sum * (magnitude / 3);
	}
}
