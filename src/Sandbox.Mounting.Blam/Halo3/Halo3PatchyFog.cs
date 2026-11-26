using Blam.Halo3;

namespace Sandbox.Mounting.Blam.Halo3;

// Halo 3's patchy fog: 8 view-aligned noise sheets spaced by the sky atmosphere's distance between sheets, sliding toward the camera as it moves (a sheet passing the near plane comes back at the far end with a new identity, faded across so nothing pops), each sheet's noise shifted by the camera's sideways motion and turns times its depth so it stays put in the world, and moved by the wind.
// Drawn as one full-screen quad (halo3_patchy_fog.shader) sorted among the transparent things the sort distance ahead of the camera, as MCC adds it to its transparent list.
// The setting is the camera cluster's (MCC blends the settings of the clusters near the camera by portal distance; its multiplayer maps with one palette entry use the camera cluster's alone).
sealed class Halo3PatchyFog
{
	public const string ShaderPath = "shaders/halo3_patchy_fog.shader";
	const int Identities = 100;

	readonly SceneCustomObject _object;
	readonly Material _material;
	readonly RenderAttributes _attributes = new();
	readonly object _lock = new();
	readonly Vertex[] _quad = new Vertex[6];

	// skya header: noise bitmap (bindless index), texture repeat, distance between sheets, depth fade factor, transparent sort distance (world units).
	readonly int _noise;
	readonly float _repeat;
	readonly float _spacing;
	readonly float _depthFade;
	readonly float _sortDistance;

	// The sheets' state (MCC's per view, here the scene camera's).
	Vector3 _lastPosition;
	Rotation _lastRotation;
	bool _started;
	float _offset;
	int _index;
	float _roll;
	readonly float[] _u = new float[Identities];
	readonly float[] _v = new float[Identities];
	readonly Vector4[] _transforms = new Vector4[8];
	readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
	double _lastTime;
	Halo3Atmosphere _setting;
	float _near = 0.1f;

	Halo3PatchyFog( SceneWorld world, int noise, float repeat, float spacing, float depthFade, float sortDistance )
	{
		_noise = noise;
		_repeat = repeat;
		_spacing = spacing;
		_depthFade = depthFade;
		_sortDistance = sortDistance;
		_material = Material.Create( "halo3/patchy_fog.vmat", ShaderPath );
		_object = new SceneCustomObject( world ) { RenderOverride = _ => Render() };
		_object.Flags.IsOpaque = false;
		_object.Flags.IsTranslucent = true;
		_object.Flags.CastShadows = false;
		_object.RenderingEnabled = false;
	}

	// The scenario sky atmosphere's patchy fog (scnr +0x680 skya: +0x4 fog bitmap, +0x14 repeat, +0x18 sheet spacing, +0x1C depth fade, +0x2C sort distance), or null when it has no bitmap.
	public static Halo3PatchyFog For( CacheFile cache, SceneWorld world )
	{
		var skya = cache?.Scenario?.Data.Reference( 0x680 );
		if ( skya is null || skya.Data.IsNull )
			return null;
		var d = skya.Data;
		var bitmap = d.Reference( 0x04 );
		if ( bitmap is null || bitmap.Data.IsNull || d.F32( 0x18 ) <= 0 )
			return null;
		var texture = Texture.Load( Halo3Mount.TexturePath( bitmap.Path ) );
		if ( texture is null || texture.IsError )
			return null;
		return new Halo3PatchyFog( world, texture.Index, d.F32( 0x14 ), d.F32( 0x18 ), d.F32( 0x1C ), d.F32( 0x2C ) > 0 ? d.F32( 0x2C ) : 100 );
	}

	// The camera cluster's setting (null or not patchy: none) and the scene camera, a fallback for where the fog sorts until a view has drawn it.
	public void Update( CameraComponent camera, Halo3Atmosphere setting, float seconds )
	{
		var on = setting is { PatchyFog: true } && camera.IsValid() && _material is not null;
		lock ( _lock )
		{
			_setting = on ? setting : null;
			if ( on )
				_near = camera.ZNear / Halo3Mount.WorldScale;
		}
		if ( !on )
		{
			_object.RenderingEnabled = false;
			return;
		}

		// Sorted the sort distance ahead of the view that last drew it, in a box reaching back over the eye so a view turned away still draws it (and so moves it along).
		var scale = Halo3Mount.WorldScale;
		Vector3 eye;
		Vector3 forward;
		lock ( _lock )
		{
			eye = _started ? _lastPosition * scale : camera.WorldPosition;
			forward = _started ? _lastRotation.Forward : camera.WorldRotation.Forward;
		}
		var reach = _sortDistance * scale;
		SourceVisibility.Kept( _object ).Bounds = BBox.FromPositionAndSize( eye + forward * reach, reach * 2 + scale );
		_object.RenderingEnabled = true;
	}

	// The sheets for the view being drawn, as MCC steps them once per view and frame: the editor's own view turns them too, not only the scene's camera.
	void Step( Halo3Atmosphere setting )
	{
		var scale = Halo3Mount.WorldScale;
		var position = Graphics.CameraPosition / scale;
		var rotation = Graphics.CameraRotation;
		var near = _near;
		var s = _spacing;
		var now = _clock.Elapsed.TotalSeconds;
		var seconds = _started ? (float)Math.Clamp( now - _lastTime, 0, 0.1 ) : 0;
		_lastTime = now;
		if ( !_started )
		{
			_started = true;
			_lastPosition = position;
			_lastRotation = rotation;
			_offset = near;
		}

		// The camera's move (less the wind's) along its axes, and its turns (yaw left, pitch up, roll) in last frame's frame.
		var move = position - _lastPosition - setting.Wind * seconds;
		var forward = rotation.Forward;
		var up = rotation.Up;
		var right = forward.Cross( up );
		float along = move.Dot( forward ), across = move.Dot( right ), lift = move.Dot( up );
		var lastForward = _lastRotation.Forward;
		var lastLeft = _lastRotation.Left;
		var lastUp = _lastRotation.Up;
		var yaw = MathF.Atan2( forward.Dot( lastLeft ), forward.Dot( lastForward ) );
		var pitch = MathF.Asin( Math.Clamp( forward.Dot( lastUp ), -1, 1 ) );
		var roll = MathF.Atan2( up.Dot( lastLeft ), up.Dot( lastUp ) );
		_lastPosition = position;
		_lastRotation = rotation;

		// Teleports and cuts (another view drawing in between) restart the sheets.
		if ( move.Length > 50 * s || forward.Dot( lastForward ) < 0.5f )
		{
			along = across = lift = 0;
			yaw = pitch = roll = 0;
		}

		_offset -= along;
		if ( _offset < near )
		{
			var k = (int)MathF.Ceiling( (near - _offset) / s - 1e-6f );
			_offset += k * s;
			_index += k;
		}
		else if ( _offset > near + s )
		{
			var k = (int)MathF.Floor( (_offset - near) / s + 1e-6f );
			_offset -= k * s;
			_index -= k;
		}
		_index = ((_index % Identities) + Identities) % Identities;
		// The texture turns against the camera's roll (its basis rotates the screen's axes into the texture's), so the dust holds still as the view rolls, as when turning while looking up.
		_roll += roll;
		var t = Math.Clamp( (_offset - near) / s, 0, 1 );

		var (sin, cos) = MathF.SinCos( _roll );
		var repeat = _repeat / (MathF.PI * 2);
		var tanX = MathF.Tan( Graphics.FieldOfView.DegreeToRadian() / 2 );
		var viewport = Graphics.Viewport;
		var aspect = viewport.Height > 0 ? viewport.Width / viewport.Height : 16f / 9;
		var tanY = tanX / aspect;
		for ( var i = 0; i < 8; i++ )
		{
			var depth = _offset + i * s;
			var id = (_index + i) % Identities;
			var a = across - yaw * depth;
			// Turning left brings what was left of centre (less u) to the centre, looking up what was above it (more v).
			var b = lift + pitch * depth;
			_u[id] += (cos * a - sin * b) / s;
			_v[id] += (sin * a + cos * b) / s;
			var sx = repeat * MathF.Atan( tanX ) * depth / s;
			var sy = repeat * MathF.Atan( tanY ) * depth / s;
			var c = (77 * id) % 100;
			_transforms[i] = new Vector4( repeat * _u[id] + 0.1f * (c % 10), repeat * _v[id] + 0.1f * (c / 10), sx, sy );
		}

		var density = setting.SheetDensity;
		var full = setting.FullIntensityHeight;
		var half = setting.HalfIntensityHeight;
		var beta = new Vector3( setting.Rayleigh.x + setting.Mie.x, setting.Rayleigh.y + setting.Mie.y, setting.Rayleigh.z + setting.Mie.z );
		var sunColor = new Vector3( beta.x > 1e-4f ? setting.SunColor.x / beta.x : 0, beta.y > 1e-4f ? setting.SunColor.y / beta.y : 0, beta.z > 1e-4f ? setting.SunColor.z / beta.z : 0 );

		_attributes.Set( "Halo3PatchyNoise", (float)_noise );
		for ( var i = 0; i < 8; i++ )
			_attributes.Set( $"Halo3PatchyTransform{i}", _transforms[i] );
		_attributes.Set( "Halo3PatchyDepths0", new Vector4( _offset, _offset + s, _offset + 2 * s, _offset + 3 * s ) );
		_attributes.Set( "Halo3PatchyDepths1", new Vector4( _offset + 4 * s, _offset + 5 * s, _offset + 6 * s, _offset + 7 * s ) );
		_attributes.Set( "Halo3PatchyFades0", new Vector4( t, 1, 1, 1 ) * density );
		_attributes.Set( "Halo3PatchyFades1", new Vector4( 1, 1, 1, 1 - t ) * density );
		_attributes.Set( "Halo3PatchyBasis", new Vector4( cos, sin, -sin, cos ) );
		_attributes.Set( "Halo3PatchyAttenuation", new Vector4( full + setting.SeaLevel, MathF.Abs( half - full ) < 1e-6f ? 10000 : MathF.Log( 2 ) / (half - full), _depthFade, s ) );
		_attributes.Set( "Halo3PatchySun", new Vector3( setting.Sun.x, setting.Sun.y, setting.Sun.z ) );
		_attributes.Set( "Halo3PatchySunColor", sunColor );
		_attributes.Set( "Halo3PatchyMieTheta", new Vector3( setting.MieTheta.x, setting.MieTheta.y, setting.MieTheta.z ) );
		_attributes.Set( "Halo3PatchyMie", new Vector2( setting.Mie.x, setting.Mie.w ) );
		_attributes.Set( "Halo3PatchyFogScale", 1 / scale );

		// A quad a world unit ahead covering the view (with margin: the shader works from the pixel).
		var eye = Graphics.CameraPosition;
		var centre = eye + forward * scale;
		var half2 = scale * 1.5f;
		var x = rotation.Right * (tanX * half2);
		var y = up * (tanY * half2);
		var corners = new[] { centre - x + y, centre - x - y, centre + x - y, centre + x + y };
		int[] order = [0, 1, 2, 0, 2, 3];
		for ( var k = 0; k < 6; k++ )
			_quad[k] = new Vertex { Position = corners[order[k]], Color = Color32.White };
	}

	void Render()
	{
		if ( _material is null )
			return;
		lock ( _lock )
		{
			if ( _setting is not { } setting )
				return;
			Step( setting );
			Graphics.Draw( new Span<Vertex>( _quad ), 6, _material, _attributes );
		}
	}

	public void Delete() => _object?.Delete();
}
