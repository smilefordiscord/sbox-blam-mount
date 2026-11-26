using Blam.Halo2;
using Blam.Halo2.Tags;

namespace Sandbox.Mounting.Blam.Halo2;

/// <summary>
/// A Halo 2 level's weather (Lockout's snow): its weather systems' particles and background plates, simulated and
/// drawn the way MCC does it (scratch research notes, weather.md), in one translucent object that
/// <see cref="SceneUpkeep"/> keeps sorted right after the sky's layers and the water, as Halo draws weather.
/// </summary>
/// <remarks>
/// <para>Particles (snowflakes) live in a box in front of the camera, 5 world units each way on Lockout: they fall
/// and drift with the wind at up to their maximum speed, and wrap around to the other side of the box when they
/// leave it, so a still camera sees them fall through and a moving one passes them. Those inside the level's
/// weather polyhedra (the covered places) die and come back elsewhere. Each is a camera-facing sprite sized in
/// pixels by its distance, never smaller than a pixel (at 480 lines) nor bigger than 64.</para>
/// <para>A background plate is three texture layers on planes 10-80 world units in front of the camera, pinned to
/// the world as the camera turns and falling at 6-10 world units a second; their coverage combined lays a colour
/// (white) over everything from 10 world units (none) to 30 (all), the distant sky included. On Lockout that is the
/// bright haze over the mountains.</para>
/// <para>Both blend in Halo's gamma space frame buffer: the frame is grabbed before each and the result written
/// back, gamma-space lerped (see halo2_weather.shader and halo2_weather_plate.shader).</para>
/// <para>MCC runs a system while any cluster the camera can see uses it; here every system a cluster uses runs
/// everywhere, the polyhedra keeping it out of covered places (Lockout's snow is effectively always on).</para>
/// </remarks>
sealed class Weather
{
	public const string FlakeShaderPath = "shaders/halo2_weather.shader";
	public const string PlateShaderPath = "shaders/halo2_weather_plate.shader";

	public const string PatchyFogShaderPath = "shaders/halo2_patchy_fog.shader";

	readonly List<WeatherSystem> _systems;
	readonly IReadOnlyList<WeatherPolyhedron> _polyhedra;
	readonly PatchyFog _patchyFog;
	readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
	double _lastTime = -1;

	Weather( List<WeatherSystem> systems, IReadOnlyList<WeatherPolyhedron> polyhedra, PatchyFog patchyFog )
	{
		_systems = systems;
		_polyhedra = polyhedra;
		_patchyFog = patchyFog;
	}

	/// <summary>
	/// The weather of a map's structure BSPs (the systems any of their clusters use) and its patchy fog
	/// (<see cref="PatchyFog"/>, drawn before the weather or after it, as its tag sorts it), or null if it has neither.
	/// </summary>
	public static Weather Build( CacheFile cache )
	{
		var weather = BuildSystems( cache, out var polyhedra );
		var patchyFog = PatchyFog.Build( cache );
		return weather.Count == 0 && patchyFog is null ? null : new Weather( weather, polyhedra, patchyFog );
	}

	static List<WeatherSystem> BuildSystems( CacheFile cache, out List<WeatherPolyhedron> polyhedra )
	{
		var systems = new List<WeatherSystem>();
		polyhedra = new List<WeatherPolyhedron>();
		var scenario = new ScenarioTag( cache.Scenario );
		var seen = new HashSet<uint>();
		foreach ( var reference in scenario.StructureBsps )
		{
			if ( reference.Bsp is null )
				continue;

			var bsp = new StructureBspTag( reference.Bsp );
			polyhedra.AddRange( bsp.WeatherPolyhedra );
			foreach ( var index in bsp.ClusterWeather.Where( w => w >= 0 && w < bsp.WeatherPalette.Count ).Distinct() )
			{
				var tag = bsp.WeatherPalette[index];
				if ( tag is null || !tag.Is( WeatherTag.Class ) || tag.Address == 0 || !seen.Add( tag.Id ) )
					continue;

				systems.Add( new WeatherSystem( new WeatherTag( tag ) ) );
			}
		}

		return systems;
	}

	// Per scene: the drawn object.

	SceneCustomObject _object;

	/// <summary>
	/// The object drawing the weather in a scene's world (made on first use).
	/// </summary>
	public SceneCustomObject ObjectIn( SceneWorld world )
	{
		if ( _object is { } existing && existing.IsValid() && existing.World == world )
			return existing;

		// On the main thread (rendering isn't).
		_plateMaterial ??= Material.Create( "halo2/weather_plate.vmat", PlateShaderPath );
		_flakeMaterial ??= Material.Create( "halo2/weather.vmat", FlakeShaderPath );
		_patchyFogMaterial ??= Material.Create( "halo2/patchy_fog.vmat", PatchyFogShaderPath );

		_object?.Delete();
		_object = new SceneCustomObject( world ) { RenderOverride = _ => Render() };
		_object.Flags.IsOpaque = false;
		_object.Flags.IsTranslucent = true;
		_object.Flags.CastShadows = false;
		return _object;
	}

	public void Delete()
	{
		_object?.Delete();
		_object = null;
	}

	static Material _flakeMaterial;
	static Material _plateMaterial;
	static Material _patchyFogMaterial;

	void Render()
	{
		try
		{
			// One step per drawn view (the editor draws one a frame); the time since the last, at most a tenth of a second.
			var now = _clock.Elapsed.TotalSeconds;
			var dt = _lastTime < 0 ? 0 : (float)Math.Clamp( now - _lastTime, 0, 0.1 );
			_lastTime = now;

			var camera = new View( Graphics.CameraPosition / Halo2Mount.WorldScale, Graphics.CameraRotation );
			if ( _patchyFog is not null && _patchyFogMaterial is not null )
				_patchyFog.Draw( camera, (float)now, _patchyFogMaterial, behindTransparents: true );

			foreach ( var system in _systems )
				system.Update( camera, dt, _polyhedra );

			foreach ( var system in _systems )
			{
				if ( _plateMaterial is not null )
					system.DrawPlates( _plateMaterial );
				if ( _flakeMaterial is not null )
					system.DrawParticles( _flakeMaterial, camera );
			}

			if ( _patchyFog is not null && _patchyFogMaterial is not null )
				_patchyFog.Draw( camera, (float)now, _patchyFogMaterial, behindTransparents: false );
		}
		catch ( Exception e )
		{
			Halo2Mount.Current?.Logger.Warning( $"Couldn't draw the Halo 2 weather: {e.Message}" );
		}
	}

	/// <summary>
	/// The camera in Halo's world units, with Halo's view space (x right, y up, z back).
	/// </summary>
	internal readonly record struct View( Vector3 Position, Rotation Rotation )
	{
		public Vector3 Forward => Rotation.Forward;
		public Vector3 Right => Rotation.Right;
		public Vector3 Up => Rotation.Up;

		public Vector3 ToView( Vector3 world ) => ToViewDirection( world - Position );

		public Vector3 ToWorld( Vector3 view ) => Position + ToWorldDirection( view );

		public Vector3 ToViewDirection( Vector3 d ) => new( Vector3.Dot( d, Right ), Vector3.Dot( d, Up ), -Vector3.Dot( d, Forward ) );

		public Vector3 ToWorldDirection( Vector3 view ) => Right * view.x + Up * view.y - Forward * view.z;
	}

	/// <summary>
	/// One weather system's state: its wind, its particle systems' particles and its plates' layers.
	/// </summary>
	sealed class WeatherSystem
	{
		readonly WeatherTag _tag;
		readonly Random _random = new();
		readonly List<Particles> _particles;
		readonly List<PlateState> _plates;

		// The wind: the primary strength's random walk (0-1) and the gusts' (direction a, b in -1..1, strength c 0-1).
		float _strength;
		float _gustA, _gustB, _gustC;
		Vector3 _wind;
		float _turbulenceStep;

		public WeatherSystem( WeatherTag tag )
		{
			_tag = tag;
			_strength = (float)_random.NextDouble();
			_particles = tag.ParticleSystems.Select( p => new Particles( p ) ).ToList();
			_plates = tag.Plates.Select( p => new PlateState( p, _random ) ).ToList();
		}

		float Rand() => (float)_random.NextDouble();

		float Signed() => 2 * Rand() - 1;

		float Walk( float x, float step, float lo, float hi ) => Math.Clamp( x + Signed() * step, lo, hi );

		Vector3 SignedVector() => new( Signed(), Signed(), Signed() );

		public void Update( View camera, float dt, IReadOnlyList<WeatherPolyhedron> polyhedra )
		{
			UpdateWind( dt );
			foreach ( var plate in _plates )
				plate.Update( camera, dt, this );
			foreach ( var particles in _particles )
				particles.Update( camera, dt, this, polyhedra );
		}

		/// <summary>
		/// The wind (§2.3 of the notes): the primary strength walks between its minimum and maximum along its
		/// direction, gusts wander in a cone around theirs.
		/// </summary>
		void UpdateWind( float dt )
		{
			_strength = Walk( _strength, dt * _tag.WindRateOfChange, 0, 1 );
			_wind = (Vector3)_tag.WindDirection * MathX.Lerp( _tag.WindMinimumStrength, _tag.WindMaximumStrength, _strength );

			if ( _tag.GustMaximumStrength > 0 )
			{
				_gustA = Walk( _gustA, dt * _tag.GustDirectionalRateOfChange, -1, 1 );
				_gustB = Walk( _gustB, dt * _tag.GustDirectionalRateOfChange, -1, 1 );
				_gustC = Walk( _gustC, dt * _tag.GustStrengthRateOfChange, 0, 1 );

				var direction = ((Vector3)_tag.GustDirection).Normal;
				var u = Vector3.Cross( direction, MathF.Abs( direction.z ) < 0.9f ? Vector3.Up : Vector3.Forward ).Normal;
				var v = Vector3.Cross( direction, u );
				var spread = MathF.Tan( _tag.GustConeAngle );
				_wind += (direction + u * (_gustA * spread) + v * (_gustB * spread)).Normal * (_tag.GustMaximumStrength * _gustC);
			}

			_turbulenceStep = dt * _tag.TurbulenceRateOfChange;
		}

		/// <summary>
		/// The push on something with turbulence state <paramref name="j"/>: turbulence, wind, gravity.
		/// </summary>
		Vector3 Force( Vector3 j ) => new Vector3( _tag.TurbulenceScale.X * j.x, _tag.TurbulenceScale.Y * j.y, _tag.TurbulenceScale.Z * j.z ) + _wind;

		Vector3 WalkTurbulence( Vector3 j ) =>
			_turbulenceStep > 0 ? new Vector3( Walk( j.x, _turbulenceStep, -1, 1 ), Walk( j.y, _turbulenceStep, -1, 1 ), Walk( j.z, _turbulenceStep, -1, 1 ) ) : j;

		/// <summary>
		/// One background plate's layers (§3.2 of the notes): each a plane in front of the camera carried by its own
		/// velocity, whose texture offset follows where the last frame's plane centre ends up on screen.
		/// </summary>
		sealed class PlateState
		{
			public readonly WeatherTag.Plate Plate;
			public readonly Texture[] Textures = new Texture[3];
			public readonly float[] Depths = new float[3];
			public readonly Vector2[] Offsets = new Vector2[3];
			readonly Vector3[] _velocity = new Vector3[3];
			readonly float[] _maximumSpeed = new float[3];
			readonly Vector3[] _turbulence = new Vector3[3];
			readonly Vector3[] _centre = new Vector3[3];
			bool _started;

			public PlateState( WeatherTag.Plate plate, Random random )
			{
				Plate = plate;
				for ( var i = 0; i < 3; i++ )
				{
					_velocity[i] = plate.MoveSpeeds[i];
					_maximumSpeed[i] = _velocity[i].Length;
					_turbulence[i] = new Vector3( 2 * random.NextSingle() - 1, 2 * random.NextSingle() - 1, 2 * random.NextSingle() - 1 );
					if ( plate.Textures[i] is { } bitmap )
						Textures[i] = Texture.Load( $"mount://halo2/{Halo2Mount.TextureName( bitmap.Path, 0 )}.vtex" );
				}
			}

			public void Update( View camera, float dt, WeatherSystem system )
			{
				for ( var i = 0; i < 3; i++ )
				{
					var position = (Plate.Flags & 2) != 0 ? 0.25f * (i + 1) : Plate.Positions[i];
					Depths[i] = Plate.NearZ + position * (Plate.FarZ - Plate.NearZ);

					_turbulence[i] = system.WalkTurbulence( _turbulence[i] );
					var mass = Plate.Masses[i];
					if ( mass > 1e-4f )
						_velocity[i] += 30 * dt * (system.Force( _turbulence[i] ) - new Vector3( 0, 0, system._tag.Gravity )) / mass;
					var speed = _velocity[i].Length;
					if ( speed > _maximumSpeed[i] && speed > 0 )
						_velocity[i] *= _maximumSpeed[i] / speed;

					if ( _started )
					{
						var moved = camera.ToView( _centre[i] + _velocity[i] * dt );
						Offsets[i] = new Vector2( Offsets[i].x - moved.x, Offsets[i].y + moved.y );
					}

					_centre[i] = camera.Position + camera.Forward * Depths[i];
				}

				_started = true;
			}
		}

		/// <summary>
		/// A particle system's particles (§2 of the notes), in Halo's world units.
		/// </summary>
		sealed class Particles
		{
			readonly WeatherTag.ParticleSystem _system;
			readonly Texture _sprite;
			readonly Vector3[] _position, _velocity, _turbulence;
			readonly float[] _mass, _size, _age, _alpha;
			readonly int _count;
			bool _started;

			public Particles( WeatherTag.ParticleSystem system )
			{
				_system = system;
				_count = Math.Clamp( system.Count, 0, MaxParticles );
				_position = new Vector3[_count];
				_velocity = new Vector3[_count];
				_turbulence = new Vector3[_count];
				_mass = new float[_count];
				_size = new float[_count];
				_age = new float[_count];
				_alpha = new float[_count];
				if ( system.Sprite is { } bitmap )
					_sprite = Texture.Load( $"mount://halo2/{Halo2Mount.TextureName( bitmap.Path, 0 )}.vtex" );
			}

			Vector3 Box => new( Positive( _system.BoxSize.X ), Positive( _system.BoxSize.Y ), Positive( _system.BoxSize.Z ) );

			static float Positive( float v ) => v < 1e-4f ? 1 : v;

			float Mass( WeatherSystem s )
			{
				var (lo, hi) = (_system.MinimumMass, _system.MaximumMass);
				if ( (lo < 1e-4f && hi < 1e-4f) || lo > hi )
					(lo, hi) = (0.1f, 0.8f);
				return MathX.Lerp( lo, hi, s.Rand() );
			}

			void Spawn( int i, View camera, WeatherSystem s )
			{
				var box = Box;
				_mass[i] = Mass( s );
				var initial = (Vector3)_system.InitialVelocity;
				_velocity[i] = new Vector3( initial.x * s.Rand(), initial.y * s.Rand(), initial.z * s.Rand() );
				_size[i] = MathX.Lerp( _system.MinimumSize, _system.MaximumSize, s.Rand() );
				_age[i] = 0;
				_alpha[i] = 1;
				_position[i] = camera.Position + new Vector3( s.Signed() * box.z, s.Signed() * box.x, s.Signed() * box.y );
				_turbulence[i] = s.SignedVector();
			}

			public void Update( View camera, float dt, WeatherSystem s, IReadOnlyList<WeatherPolyhedron> polyhedra )
			{
				var box = Box;
				if ( !_started )
				{
					// MCC spawns them all at map load; here at the first view, around it.
					for ( var i = 0; i < _count; i++ )
						Spawn( i, camera, s );
					_started = true;
				}

				// Polyhedra near enough to matter this frame.
				var reach = MathF.Max( box.x, MathF.Max( box.y, box.z ) );
				var near = polyhedra.Where( p => ((Vector3)p.Center - camera.Position).Length < p.Radius + reach ).ToList();

				var live = 0;
				var gravity = new Vector3( 0, 0, s._tag.Gravity );
				for ( var i = 0; i < _count; i++ )
				{
					if ( _age[i] < 0 )
					{
						if ( live < _count )
							Spawn( i, camera, s );
						continue;
					}

					_age[i] += dt;
					_turbulence[i] = s.WalkTurbulence( _turbulence[i] );
					if ( _mass[i] <= 1e-4f )
						_mass[i] = 0.1f + 0.7f * s.Rand();

					var v = _velocity[i] + (s.Force( _turbulence[i] ) / _mass[i] - gravity) * dt;
					var speed = v.Length;
					if ( speed > _system.MaximumVelocity && speed > 1e-4f )
						v *= _system.MaximumVelocity / speed;
					_velocity[i] = v;
					_position[i] += v * dt;

					// Kept in the box in front of the camera: x and y centred, z from the camera ahead.
					var p = camera.ToView( _position[i] );
					var w = Wrap( p.x / box.x, out var wrappedX ) * box.x;
					var h = Wrap( p.y / box.y, out var wrappedY ) * box.y;
					var fz = p.z / box.z - MathF.Truncate( p.z / box.z );
					var wrappedZ = fz > 0 || p.z < -box.z;
					if ( fz > 0 )
						fz -= 1;
					var d = fz * box.z;
					if ( wrappedX || wrappedY || wrappedZ )
						_position[i] = camera.ToWorld( new Vector3( w, h, d ) );

					// Fading out within 0.2 world units of the camera (MCC latches it until the flake respawns).
					var r2 = w * w + h * h + d * d;
					_alpha[i] = r2 < 0.04f ? r2 / 0.04f : 1;

					if ( Killed( _position[i], camera.Position, near ) )
						_age[i] = -1;
					else
						live++;
				}
			}

			static float Wrap( float x, out bool wrapped )
			{
				var f = x - MathF.Truncate( x );
				wrapped = false;
				if ( f < -0.5f ) { f += 1; wrapped = true; }
				else if ( f > 0.5f ) { f -= 1; wrapped = true; }
				return f;
			}

			bool Killed( Vector3 position, Vector3 camera, List<WeatherPolyhedron> near )
			{
				if ( _system.ExclusionRadius > 0 && (position - camera).Length < _system.ExclusionRadius )
					return true;

				foreach ( var polyhedron in near )
				{
					if ( (position - (Vector3)polyhedron.Center).LengthSquared >= polyhedron.Radius * polyhedron.Radius )
						continue;

					var inside = true;
					foreach ( var (normal, distance) in polyhedron.Planes )
					{
						if ( Vector3.Dot( (Vector3)normal, position ) - distance > 1e-4f )
						{
							inside = false;
							break;
						}
					}

					if ( inside )
						return true;
				}

				return false;
			}

			public void Draw( Material material )
			{
				if ( _sprite is null || _system.Type == 2 )
					return; // Rain draws streaks (not done yet).

				var vertices = _vertices ??= new Vertex[MaxParticles * 4];
				var indices = _indices ??= MakeIndices();
				var count = 0;
				for ( var i = 0; i < _count; i++ )
				{
					if ( _age[i] <= 0 )
						continue;

					var vertex = new Vertex
					{
						Position = _position[i] * Halo2Mount.WorldScale,
						Color = Color.White,
						TexCoord1 = new Vector4( _size[i], _alpha[i], 0, 0 ),
					};
					foreach ( var corner in Corners )
					{
						vertex.TexCoord0 = new Vector4( corner.x, corner.y, 0, 0 );
						vertices[count++] = vertex;
					}
				}

				if ( count == 0 )
					return;

				_sprite.MarkUsed( 65535 );
				Graphics.Attributes.Set( "HaloWeatherSprite", _sprite );
				Graphics.GrabFrameTexture( "HaloWeatherFrame" );
				Graphics.Draw( vertices.AsSpan(), count, indices.AsSpan(), count / 4 * 6, material );
			}
		}

		public void DrawPlates( Material material )
		{
			foreach ( var plate in _plates )
			{
				var p = plate.Plate;
				var a = Graphics.Attributes;
				a.Set( "HaloWeatherLayer0", plate.Textures[0] ?? Texture.Transparent );
				a.Set( "HaloWeatherLayer1", plate.Textures[1] ?? Texture.Transparent );
				a.Set( "HaloWeatherLayer2", plate.Textures[2] ?? Texture.Transparent );
				for ( var i = 0; i < 3; i++ )
				{
					plate.Textures[i]?.MarkUsed( 65535 );

					// uv = S·(screen u, v) + O·texture scale, S the texture scale times the plane's height on screen
					// when the plate scales automatically (the shader multiplies by 2 tan(vfov/2)).
					var scale = p.TextureScales[i];
					var auto = (p.Flags & 4) != 0;
					a.Set( $"HaloWeatherUv{i}", new Vector4( scale, auto ? plate.Depths[i] : 0, plate.Offsets[i].x * scale, plate.Offsets[i].y * scale ) );
				}

				var tint = (Vector3)p.Tints[0];
				a.Set( "HaloWeatherOpacity", new Vector4( Math.Clamp( p.Opacities[0], 0, 1 ), Math.Clamp( p.Opacities[1], 0, 1 ), Math.Clamp( p.Opacities[2], 0, 1 ), 0 ) );
				a.Set( "HaloWeatherTint", new Vector4( Math.Clamp( tint.x, 0, 1 ), Math.Clamp( tint.y, 0, 1 ), Math.Clamp( tint.z, 0, 1 ), 1 ) );
				a.Set( "HaloWeatherDepthBlend", new Vector4( p.DepthBlendNear * Halo2Mount.WorldScale, MathF.Max( p.DepthBlendFar - p.DepthBlendNear, 1e-3f ) * Halo2Mount.WorldScale, 0, 0 ) );
				Graphics.GrabFrameTexture( "HaloWeatherFrame" );
				Graphics.Blit( material, a );
			}
		}

		public void DrawParticles( Material material, View camera )
		{
			Graphics.Attributes.Set( "HaloWorldScale", Halo2Mount.WorldScale );
			foreach ( var particles in _particles )
				particles.Draw( material );
		}
	}

	/// <summary>
	/// Most particles of one system (Lockout's has 1024).
	/// </summary>
	const int MaxParticles = 16384;

	[ThreadStatic] static Vertex[] _vertices;
	[ThreadStatic] static ushort[] _indices;

	static readonly Vector2[] Corners = [new( 0, 0 ), new( 1, 0 ), new( 1, 1 ), new( 0, 1 )];

	static ushort[] MakeIndices()
	{
		var indices = new ushort[MaxParticles * 6];
		for ( var i = 0; i < MaxParticles; i++ )
		{
			var v = (ushort)(i * 4);
			indices[i * 6] = v;
			indices[i * 6 + 1] = (ushort)(v + 1);
			indices[i * 6 + 2] = (ushort)(v + 2);
			indices[i * 6 + 3] = v;
			indices[i * 6 + 4] = (ushort)(v + 2);
			indices[i * 6 + 5] = (ushort)(v + 3);
		}

		return indices;
	}
}
