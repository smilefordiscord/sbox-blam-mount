using Blam.Halo3;
using Blam.Halo3.Tags;

namespace Sandbox.Mounting.Blam.Halo3;

// A Halo 3 map's weather: the particle systems of the weather effect (skya atmosphere setting +0x8C) of the cluster the camera is in, kept in a box around the camera and wrapped as MCC's weather systems are ("follow the camera and wrap seamlessly").
// Simulated on the main thread (Update, from Halo3SceneUpkeep) and drawn by one translucent scene object around the camera (Render), so it sorts after every other transparent thing.
// Plausible rather than exact: each emitter's ranged constants (count, lifespan, velocity, size, acceleration) are sampled per particle, its curves (alpha, tint, scale over age) become a fade in and out at their peak; velocity starts straight down within the emission angle, the self acceleration is taken as a world space push (the tags' vectors read as wind drift), physics movement parameter 0 as gravity and 1 as drag, both per 30 Hz tick.
// Systems flagged for GPU occlusion (bit 13: covered places stay dry in MCC through a height map seen from above) hide particles under the level: one downward trace per 1 world unit column, cached.
sealed class Halo3Weather
{
	public const string ShaderPath = "shaders/halo3_weather.shader";

	// Most particles of one system (snow_heavy has 2000).
	const int MaxParticles = 4000;

	// The wrap box's half size bounds (world units): rain emitters reach 120 out, which would spread their drops to nothing.
	const float MinimumBox = 2, MaximumBox = 15;

	// Seconds to fade a weather in or out when the camera changes cluster.
	const float FadeTime = 1.5f;

	// Emission shapes: 2 globe, 4 tube (Assembly effe enum).
	const int GlobeShape = 2;

	// Halo's tick rate, for the per-tick physics parameters.
	const float TickRate = 30;

	// A particle this close to the camera (world units) fades out.
	const float NearFade = 0.1f;

	[ConVar( "halo3_weather_occlusion", Help = "Hide Halo 3 weather under cover (a trace down per column), as MCC's GPU occlusion does." )]
	public static bool OcclusionEnabled { get; set; } = true;

	readonly CacheFile _cache;
	readonly Dictionary<uint, Effect> _effects = new();
	readonly Occlusion _occlusion = new();
	uint _activeId;

	// The camera the weather was last drawn for (s&box units), which follows the editor's view too.
	public Vector3? LastCamera { get; private set; }

	public Halo3Weather( CacheFile cache )
	{
		_cache = cache;
	}

	// Which weather effect runs (null: none); the others fade out.
	public void SetActive( TagEntry effect )
	{
		_activeId = effect?.Id ?? 0;
		if ( effect is null )
			return;
		lock ( _effects )
		{
			if ( !_effects.ContainsKey( effect.Id ) )
				_effects[effect.Id] = Effect.Build( effect );
		}
	}

	Effect[] Effects()
	{
		lock ( _effects )
			return _effects.Values.Where( e => e is not null ).ToArray();
	}

	// Steps every effect with some weight and rebuilds what Render draws (main thread).
	public void Update( Scene scene, float dt )
	{
		if ( LastCamera is not { } cameraWorld )
			return;

		var camera = cameraWorld / Halo3Mount.WorldScale;
		foreach ( var effect in Effects() )
		{
			effect.Weight = Math.Clamp( effect.Weight + (effect.Id == _activeId ? dt : -dt) / FadeTime, 0, 1 );
			if ( effect.Weight > 0 )
				effect.Update( camera, dt, scene, _occlusion );
			else
				effect.Reset();
		}
	}

	// Per scene: the drawn object.

	SceneCustomObject _object;
	static Material _material;

	public SceneCustomObject ObjectIn( SceneWorld world )
	{
		if ( _object is { } existing && existing.IsValid() && existing.World == world )
			return existing;

		// On the main thread (rendering isn't).
		_material ??= Material.Create( "halo3/weather.vmat", ShaderPath );

		_object?.Delete();
		_object = new SceneCustomObject( world ) { RenderOverride = _ => Render() };
		_object.Flags.IsOpaque = false;
		_object.Flags.IsTranslucent = true;
		_object.Flags.CastShadows = false;
		return _object;
	}

	// Keeps the object's bounds around the camera, so it is never culled and sorts nearest (drawn last); everywhere until a view has drawn it.
	public void KeepAround( SceneWorld world, Vector3? camera )
	{
		var o = SourceVisibility.Kept( ObjectIn( world ) );
		o.Bounds = (LastCamera ?? camera) is { } eye
			? BBox.FromPositionAndSize( eye, 4 * MaximumBox * Halo3Mount.WorldScale )
			: BBox.FromPositionAndSize( Vector3.Zero, 1e7f );
	}

	public void Delete()
	{
		_object?.Delete();
		_object = null;
	}

	void Render()
	{
		try
		{
			LastCamera = Graphics.CameraPosition;
			if ( _material is null )
				return;
			foreach ( var effect in Effects() )
			{
				if ( effect.Weight > 0 )
					effect.Draw( _material );
			}
		}
		catch ( Exception e )
		{
			Halo3Mount.Current?.Logger.Warning( $"Couldn't draw the Halo 3 weather: {e.Message}" );
		}
	}

	// One weather effect's particle systems.
	sealed class Effect
	{
		public uint Id { get; init; }
		public string Name { get; init; }
		public float Weight;
		public List<Particles> Systems { get; init; }

		public static Effect Build( TagEntry tag )
		{
			try
			{
				var effect = new WeatherEffectTag( tag );
				var systems = new List<Particles>();
				foreach ( var system in effect.Systems )
				{
					foreach ( var emitter in system.Emitters )
					{
						if ( Particles.Build( system, emitter ) is { } particles )
							systems.Add( particles );
					}
				}
				return new Effect { Id = tag.Id, Name = tag.Path, Systems = systems };
			}
			catch ( Exception e )
			{
				Halo3Mount.Current?.Logger.Warning( e, $"Couldn't read the Halo 3 weather {tag.Path}: {e.Message}" );
				return null;
			}
		}

		public void Update( Vector3 camera, float dt, Scene scene, Occlusion occlusion )
		{
			foreach ( var s in Systems )
				s.Update( camera, dt, Weight, scene, occlusion );
		}

		public void Reset()
		{
			foreach ( var s in Systems )
				s.Started = false;
		}

		public void Draw( Material material )
		{
			foreach ( var s in Systems )
				s.Draw( material );
		}
	}

	// One emitter's particles, in Halo world units around the camera.
	sealed class Particles
	{
		readonly WeatherParticleSystem _system;
		readonly WeatherEmitter _emitter;
		readonly Texture _sprite;
		readonly float _srgb;
		readonly bool _additive;
		readonly bool _occluded;
		readonly float _box;
		readonly Random _random = new();
		readonly Vector3[] _position, _velocity, _acceleration;
		readonly float[] _age, _life, _width, _height, _alpha;
		readonly bool[] _hidden;
		readonly int _count;
		readonly float _gravity, _drag;
		readonly Vertex[] _vertices;
		readonly ushort[] _indices;
		readonly object _lock = new();
		int _drawn;
		public bool Started;

		Particles( WeatherParticleSystem system, WeatherEmitter emitter, Texture sprite, float srgb, bool additive, int count )
		{
			_system = system;
			_emitter = emitter;
			_sprite = sprite;
			_srgb = srgb;
			_additive = additive;
			_occluded = (system.Flags & 0x2000) != 0;
			_count = count;
			// A globe emitter's box holds as much as its sphere (same particle density).
			_box = Math.Clamp( emitter.EmissionRadius.Largest * (emitter.Shape == GlobeShape ? 0.806f : 1), MinimumBox, MaximumBox );
			_gravity = emitter.Gravity * TickRate;
			_drag = emitter.Drag is > 0 and < 1 ? -MathF.Log( 1 - emitter.Drag ) * TickRate : 0;
			_position = new Vector3[count];
			_velocity = new Vector3[count];
			_acceleration = new Vector3[count];
			_age = new float[count];
			_life = new float[count];
			_width = new float[count];
			_height = new float[count];
			_alpha = new float[count];
			_hidden = new bool[count];
			_vertices = new Vertex[count * 4];
			_indices = Indices( count );
		}

		public static Particles Build( WeatherParticleSystem system, WeatherEmitter emitter )
		{
			var shader = ShaderTag.Get( system.Particle );
			if ( shader?.Texture( "base_map" ) is not { Bitmap: { } bitmap } map )
				return null;

			var count = (int)emitter.MaximumCount.Largest;
			if ( count <= 0 )
				count = (int)(emitter.Rate.Largest * emitter.Lifespan.Largest);
			count = Math.Clamp( count, 0, MaxParticles );
			if ( count == 0 )
				return null;

			var sprite = Texture.Load( Halo3Mount.TexturePath( bitmap.Path, map.BitmapIndex ) );
			var srgb = ShaderBitmaps.IsSrgb( bitmap, map.BitmapIndex ) ? 1f : 0f;
			var blend = shader.Option( "blend_mode" ) ?? "";
			var additive = blend.StartsWith( "add", StringComparison.Ordinal );
			return new Particles( system, emitter, sprite, srgb, additive, count );
		}

		float Rand() => _random.NextSingle();

		float Signed() => 2 * Rand() - 1;

		void Spawn( int i, Vector3 camera, bool anywhereInLife )
		{
			var e = _emitter;
			_position[i] = camera + new Vector3( Signed(), Signed(), Signed() ) * _box;

			// Straight down, tilted up to the emission angle.
			var angle = e.EmissionAngle.Sample( Rand() ) * MathF.PI / 180 * MathF.Sqrt( Rand() );
			var heading = Rand() * 2 * MathF.PI;
			var direction = new Vector3( MathF.Sin( angle ) * MathF.Cos( heading ), MathF.Sin( angle ) * MathF.Sin( heading ), -MathF.Cos( angle ) );
			_velocity[i] = direction * e.InitialVelocity.Sample( Rand() );

			var push = Vector3.Lerp( (Vector3)e.AccelerationFrom, (Vector3)e.AccelerationTo, Rand() );
			_acceleration[i] = push * e.SelfAcceleration.Sample( Rand() );

			_life[i] = MathF.Max( e.Lifespan.Sample( Rand() ), 0.1f );
			_age[i] = anywhereInLife ? Rand() * _life[i] : 0;
			var size = e.Size.Sample( Rand() );
			var scale = e.Scale.IsConstant ? e.Scale.Sample( Rand() ) : 1;
			_height[i] = size;
			_width[i] = size * (scale > 0 ? scale : 1);
			_alpha[i] = Math.Clamp( e.Alpha.Largest, 0, 1 );
			_hidden[i] = false;
		}

		public void Update( Vector3 camera, float dt, float weight, Scene scene, Occlusion occlusion )
		{
			if ( !Started )
			{
				for ( var i = 0; i < _count; i++ )
					Spawn( i, camera, true );
				Started = true;
			}

			var damping = MathF.Exp( -_drag * dt );
			var box = _box;
			var drawn = 0;
			lock ( _lock )
			{
				for ( var i = 0; i < _count; i++ )
				{
					_age[i] += dt;
					if ( _age[i] >= _life[i] )
						Spawn( i, camera, false );

					var v = _velocity[i] + (_acceleration[i] + new Vector3( 0, 0, -_gravity )) * dt;
					v *= damping;
					_velocity[i] = v;
					var p = _position[i] + v * dt;

					// Wrapped into the box around the camera; a wrapped particle is checked for cover again.
					var r = p - camera;
					var wrapped = false;
					r.x = Wrap( r.x, box, ref wrapped );
					r.y = Wrap( r.y, box, ref wrapped );
					r.z = Wrap( r.z, box, ref wrapped );
					p = camera + r;
					_position[i] = p;
					if ( wrapped || _age[i] <= dt )
						_hidden[i] = _occluded && OcclusionEnabled && occlusion.Covered( scene, p );
					if ( _hidden[i] )
						continue;

					var fade = Envelope( _age[i], _life[i] );
					var distance = r.Length;
					if ( distance < NearFade )
						fade *= distance / NearFade;
					var alpha = _alpha[i] * fade * weight;
					if ( alpha <= 1e-3f )
						continue;

					var vertex = new Vertex
					{
						Position = p * Halo3Mount.WorldScale,
						Normal = v * Halo3Mount.WorldScale,
						Color = Color.White,
						TexCoord1 = new Vector4( _width[i] * Halo3Mount.WorldScale, _height[i] * Halo3Mount.WorldScale, alpha, _additive ? 1 : 0 ),
					};
					foreach ( var corner in Corners )
					{
						vertex.TexCoord0 = new Vector4( corner.x, corner.y, 0, 0 );
						_vertices[drawn++] = vertex;
					}
				}
				_drawn = drawn;
			}
		}

		static float Wrap( float x, float half, ref bool wrapped )
		{
			if ( x < -half )
			{
				wrapped = true;
				return x + 2 * half * MathF.Ceiling( (-half - x) / (2 * half) );
			}
			if ( x > half )
			{
				wrapped = true;
				return x - 2 * half * MathF.Ceiling( (x - half) / (2 * half) );
			}
			return x;
		}

		// Fades in over the first fifth of a life (at most half a second) and out over the last fifth.
		static float Envelope( float age, float life )
		{
			var fadeIn = MathF.Min( 0.2f * life, 0.5f );
			var a = fadeIn > 0 ? Math.Clamp( age / fadeIn, 0, 1 ) : 1;
			var b = Math.Clamp( (life - age) / (0.2f * life), 0, 1 );
			return MathF.Min( a, b );
		}

		public void Draw( Material material )
		{
			if ( _sprite is null )
				return;
			lock ( _lock )
			{
				if ( _drawn == 0 )
					return;
				_sprite.MarkUsed( 65535 );
				Graphics.Attributes.Set( "Halo3WeatherSprite", _sprite );
				Graphics.Attributes.Set( "Halo3WeatherSrgb", _srgb );
				Graphics.Attributes.Set( "Halo3WeatherStreak", _system.IsStreak ? 1f : 0f );
				Graphics.Draw( _vertices.AsSpan(), _drawn, _indices.AsSpan(), _drawn / 4 * 6, material );
			}
		}
	}

	// Which places are under cover: per 1 world unit column, the height of the first surface a ray down from above the level hits.
	sealed class Occlusion
	{
		readonly Dictionary<(int, int), float> _tops = new();

		// Above every Halo 3 level (world units).
		const float Ceiling = 2000;

		public bool Covered( Scene scene, Vector3 p )
		{
			var key = ((int)MathF.Floor( p.x ), (int)MathF.Floor( p.y ));
			if ( !_tops.TryGetValue( key, out var top ) )
			{
				if ( _tops.Count > 200_000 )
					_tops.Clear();
				top = float.NegativeInfinity;
				try
				{
					var from = new Vector3( key.Item1 + 0.5f, key.Item2 + 0.5f, Ceiling ) * Halo3Mount.WorldScale;
					var to = new Vector3( key.Item1 + 0.5f, key.Item2 + 0.5f, -Ceiling ) * Halo3Mount.WorldScale;
					var trace = scene.Trace.Ray( from, to ).Run();
					if ( trace.Hit )
						top = trace.HitPosition.z / Halo3Mount.WorldScale;
				}
				catch ( Exception )
				{
				}
				_tops[key] = top;
			}
			return p.z < top - 0.05f;
		}
	}

	static readonly Vector2[] Corners = [new( 0, 0 ), new( 1, 0 ), new( 1, 1 ), new( 0, 1 )];

	static ushort[] Indices( int count )
	{
		var indices = new ushort[count * 6];
		for ( var i = 0; i < count; i++ )
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
