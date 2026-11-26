using Blam.Halo3.Tags;

namespace Sandbox.Mounting.Blam.Halo3;

// Halo 3's area screen effects for a view, as Halo 3 combines them: the scenario's global effect (its first element, weight 1) and up to 64 live instances from effects, each element weighted by its distance, time and angle falloffs, combined per field by the strongest (the colour filter by the darkest), into the final composite's exposure boost, colour matrix and contrast.
sealed class Halo3ScreenEffects
{
	const int MaximumInstances = 64;

	// A live screen effect: where it is (world units, or following an object), how old it is.
	sealed class Instance
	{
		public ScreenEffectTag Tag;
		public Vector3 Position;
		public GameObject Attached;
		public Vector3 Offset;
		public float Age;
	}

	readonly ScreenEffectTag _global;
	readonly List<Instance> _instances = new();

	public Halo3ScreenEffects( ScreenEffectTag global ) => _global = global;

	// Each Halo 3 scene's (Halo3SceneUpkeep registers it), for the particle runtime's effect events.
	static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Scene, Halo3ScreenEffects> Scenes = new();

	public static Halo3ScreenEffects For( Scene scene ) => scene is not null && Scenes.TryGetValue( scene, out var effects ) ? effects : null;

	public static void Register( Scene scene, Halo3ScreenEffects effects ) => Scenes.AddOrUpdate( scene, effects );

	// Starts an effect part's screen effect at a world position (world units), or attached to an object at an offset in its space.
	public void Add( ScreenEffectTag tag, Vector3 position, GameObject attached = null, Vector3 offset = default )
	{
		if ( tag is null || tag.Effects.Count == 0 )
			return;
		if ( _instances.Count >= MaximumInstances )
			_instances.RemoveAt( 0 );
		_instances.Add( new Instance { Tag = tag, Position = position, Attached = attached, Offset = offset } );
	}

	// Ages the instances; one goes when none of its elements lives on (a lifetime-0 element lives while its object does) or its object is gone.
	public void Step( float seconds )
	{
		for ( var i = _instances.Count - 1; i >= 0; i-- )
		{
			var instance = _instances[i];
			instance.Age += seconds;
			if ( instance.Attached is not null )
			{
				if ( !instance.Attached.IsValid() )
				{
					_instances.RemoveAt( i );
					continue;
				}
				instance.Position = instance.Attached.WorldTransform.PointToWorld( instance.Offset * Halo3Mount.WorldScale ) / Halo3Mount.WorldScale;
			}
			if ( instance.Tag.Effects.All( e => e.Lifetime > 0 && e.Lifetime < instance.Age ) )
				_instances.RemoveAt( i );
		}
	}

	// The accumulator: exposure boost, hue left, hue right, saturation, desaturation, contrast enhance, gamma enhance, gamma reduce (max of w v from 0), colour filter (min of w v + 1 - w from 1), colour floor (max from 0).
	struct Accumulator
	{
		public float Exposure, HueLeft, HueRight, Saturation, Desaturation, Contrast, GammaEnhance, GammaReduce;
		public Vector3 Filter, Floor;

		public void Add( ScreenEffect e, float w )
		{
			Exposure = MathF.Max( Exposure, w * e.ExposureBoost );
			HueLeft = MathF.Max( HueLeft, w * e.HueLeft );
			HueRight = MathF.Max( HueRight, w * e.HueRight );
			Saturation = MathF.Max( Saturation, w * e.Saturation );
			Desaturation = MathF.Max( Desaturation, w * e.Desaturation );
			Contrast = MathF.Max( Contrast, w * e.ContrastEnhance );
			GammaEnhance = MathF.Max( GammaEnhance, w * e.GammaEnhance );
			GammaReduce = MathF.Max( GammaReduce, w * e.GammaReduce );
			var filter = new Vector3( e.ColorFilter.X, e.ColorFilter.Y, e.ColorFilter.Z );
			Filter = Filter.ComponentMin( filter * w + Vector3.One * (1 - w) );
			Floor = Floor.ComponentMax( new Vector3( e.ColorFloor.X, e.ColorFloor.Y, e.ColorFloor.Z ) * w );
		}
	}

	// This frame's composite values for a camera at eye looking along forward (world units); firstPerson: whether the view is a first-person one (for elements flagged first or third person only, which need an attached instance).
	public PostFrame Evaluate( Vector3 eye, Vector3 forward, bool firstPerson = false )
	{
		var a = new Accumulator { Filter = Vector3.One };
		if ( _global is { Effects.Count: > 0 } )
			a.Add( _global.Effects[0], 1 );

		foreach ( var instance in _instances )
		{
			var to = instance.Position - eye;
			var distance = to.Length;
			var angle = distance > 1e-6f ? MathF.Acos( Math.Clamp( Vector3.Dot( to / distance, forward.Normal ), -1, 1 ) ) : 0;
			foreach ( var e in instance.Tag.Effects )
			{
				if ( e.Lifetime > 0 && instance.Age > e.Lifetime )
					continue;
				if ( e.FirstPersonOnly || e.ThirdPersonOnly )
				{
					if ( instance.Attached is null || (e.FirstPersonOnly && !firstPerson) || (e.ThirdPersonOnly && firstPerson) )
						continue;
				}
				if ( !e.AllowOutsideRadius && distance > e.MaximumDistance )
					continue;

				var fd = e.DistanceFalloff.Evaluate( e.MaximumDistance > 0 ? Math.Clamp( distance / e.MaximumDistance, 0, 1 ) : 0, 0.5f );
				var ft = e.TimeFalloff.Evaluate( e.Lifetime > 0.0001f ? Math.Clamp( instance.Age / e.Lifetime, 0, 1 ) : instance.Age, 0.5f );
				var fa = e.AngleFalloff.Evaluate( Math.Clamp( angle / MathF.PI, 0, 1 ), 0.5f );
				var w = Math.Clamp( fd * ft * fa, 0, 1 );
				if ( w > 0 )
					a.Add( e, w );
			}
		}

		return Compose( a );
	}

	// c' = Hue( Filter( Saturation( c ) ) ) as row vectors, its transpose's first three rows for the shader's dot( row, ( c, 1 ) ).
	static PostFrame Compose( in Accumulator a )
	{
		var s = Math.Clamp( 1 + a.Saturation - a.Desaturation, -10, 10 );
		var hue = (a.HueRight - a.HueLeft) % 360;
		if ( hue < 0 )
			hue += 360;
		var k = 1 + 10 * a.Contrast * a.Contrast;
		var offset = a.Contrast * a.Contrast;

		var m = Saturation( s );
		var filter = Matrix4.Identity;
		var gain = new Vector3( Math.Clamp( a.Filter.x, 0, 1 ), Math.Clamp( a.Filter.y, 0, 1 ), Math.Clamp( a.Filter.z, 0, 1 ) ) * k;
		filter.M[0, 0] = gain.x;
		filter.M[1, 1] = gain.y;
		filter.M[2, 2] = gain.z;
		filter.M[3, 0] = -a.Floor.x - offset;
		filter.M[3, 1] = -a.Floor.y - offset;
		filter.M[3, 2] = -a.Floor.z - offset;
		m = Matrix4.Multiply( m, filter );
		m = Matrix4.Multiply( m, HueRotation( hue * MathF.PI / 180 ) );

		Vector4 Row( int j ) => new( m.M[0, j], m.M[1, j], m.M[2, j], m.M[3, j] );
		return new PostFrame( Row( 0 ), Row( 1 ), Row( 2 ), Math.Clamp( 1 + a.GammaEnhance - a.GammaReduce, 0.01f, 10 ), a.Exposure );
	}

	// s c + ( 1 - s ) Y( c ), Rec. 709 luminance.
	static Matrix4 Saturation( float s )
	{
		var y = new[] { 0.2126709f, 0.7151600f, 0.0721750f };
		var m = Matrix4.Identity;
		for ( var i = 0; i < 3; i++ )
		{
			for ( var j = 0; j < 3; j++ )
				m.M[i, j] = y[i] * (1 - s) + (i == j ? s : 0);
		}
		return m;
	}

	// Paul Haeberli's luminance-preserving hue rotation ("Matrix Operations for Image Processing"): grey turned onto z, sheared so luminance stays put, rotated about z, undone.
	static Matrix4 HueRotation( float radians )
	{
		var m = Matrix4.Identity;
		var xs = 1 / MathF.Sqrt( 2 );
		var xc = 1 / MathF.Sqrt( 2 );
		m = Matrix4.Multiply( m, Matrix4.RotateX( xs, xc ) );
		var ys = -1 / MathF.Sqrt( 3 );
		var yc = MathF.Sqrt( 2 ) / MathF.Sqrt( 3 );
		m = Matrix4.Multiply( m, Matrix4.RotateY( ys, yc ) );

		var l = m.Transform( new Vector3( 0.212656f, 0.715158f, 0.0721856f ) );
		var zx = l.x / l.z;
		var zy = l.y / l.z;
		m = Matrix4.Multiply( m, Matrix4.ShearZ( zx, zy ) );
		m = Matrix4.Multiply( m, Matrix4.RotateZ( MathF.Sin( radians ), MathF.Cos( radians ) ) );
		m = Matrix4.Multiply( m, Matrix4.ShearZ( -zx, -zy ) );
		m = Matrix4.Multiply( m, Matrix4.RotateY( -ys, yc ) );
		m = Matrix4.Multiply( m, Matrix4.RotateX( -xs, xc ) );
		return m;
	}

	// A 4x4 for row vectors ( [ x y z 1 ] M ), Multiply( a, b ) applying a first.
	struct Matrix4
	{
		public float[,] M;

		public static Matrix4 Identity
		{
			get
			{
				var m = new Matrix4 { M = new float[4, 4] };
				for ( var i = 0; i < 4; i++ )
					m.M[i, i] = 1;
				return m;
			}
		}

		public static Matrix4 Multiply( Matrix4 a, Matrix4 b )
		{
			var c = new Matrix4 { M = new float[4, 4] };
			for ( var i = 0; i < 4; i++ )
			{
				for ( var j = 0; j < 4; j++ )
				{
					var sum = 0f;
					for ( var k = 0; k < 4; k++ )
						sum += a.M[i, k] * b.M[k, j];
					c.M[i, j] = sum;
				}
			}
			return c;
		}

		public Vector3 Transform( Vector3 p ) => new(
			p.x * M[0, 0] + p.y * M[1, 0] + p.z * M[2, 0] + M[3, 0],
			p.x * M[0, 1] + p.y * M[1, 1] + p.z * M[2, 1] + M[3, 1],
			p.x * M[0, 2] + p.y * M[1, 2] + p.z * M[2, 2] + M[3, 2] );

		public static Matrix4 RotateX( float s, float c )
		{
			var m = Identity;
			m.M[1, 1] = c;
			m.M[1, 2] = s;
			m.M[2, 1] = -s;
			m.M[2, 2] = c;
			return m;
		}

		public static Matrix4 RotateY( float s, float c )
		{
			var m = Identity;
			m.M[0, 0] = c;
			m.M[0, 2] = -s;
			m.M[2, 0] = s;
			m.M[2, 2] = c;
			return m;
		}

		public static Matrix4 RotateZ( float s, float c )
		{
			var m = Identity;
			m.M[0, 0] = c;
			m.M[0, 1] = s;
			m.M[1, 0] = -s;
			m.M[1, 1] = c;
			return m;
		}

		public static Matrix4 ShearZ( float dx, float dy )
		{
			var m = Identity;
			m.M[0, 2] = dx;
			m.M[1, 2] = dy;
			return m;
		}
	}
}
