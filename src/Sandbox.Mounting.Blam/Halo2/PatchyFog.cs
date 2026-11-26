using Blam.Halo2;
using Blam.Halo2.Tags;

namespace Sandbox.Mounting.Blam.Halo2;

/// <summary>
/// Halo 2's patchy fog: four layers of scrolling noise at depths through a view depth range, laying a colour over
/// what's behind them by the noise's density (Colossus' sky; under Dune's and Needle's water and in Lockout's abyss).
/// Drawn by <see cref="Weather"/>'s object, before or after the weather as its tag sorts it, as a blit
/// (halo2_patchy_fog.shader).
/// </summary>
/// <remarks>
/// <para>As MCC does it (scratch research notes, patchy_fog.md): the sky's patchy fog block, blended toward the
/// planar fog's by how deep the camera is in that fog (from its minimum depth fraction on), gives the colour, the
/// density range and the view depth range; the patchy fog tag (fpch) the noise and how it moves. Each frame the four
/// layers sit at even steps through the range, pushed through it as the camera moves forward (a layer leaving one
/// end comes back at the other with a new random offset), their noise turned with the camera's roll and slid by
/// where last frame's layer centre went on screen (camera moves and turns, and the fog's wind drift), so the fog
/// seems to hang in the world. The noise bitmap's images step through its animation period, each layer at its own
/// phase, cross-fading through the image's red, green and blue channels.</para>
/// <para>MCC renders the combined noise at half resolution first and composites it in a second pass; here both are
/// one full-resolution pass. MCC draws it after the transparent objects unless the fpch sorts it behind them; here
/// the weather's object draws it before its plates and flakes or after them (Lockout's: after), other transparent
/// objects sorting as they do.</para>
/// </remarks>
sealed class PatchyFog
{
	/// <summary>
	/// MCC's near plane distance (world units), which the noise's parallax is scaled by.
	/// </summary>
	const float NearPlane = 0.06f;

	/// <summary>
	/// The per-layer phases of the noise animation.
	/// </summary>
	static readonly float[] AnimationPhases = [0, 0.7135f, 0.3422f, 0.5798f];

	readonly PatchyFogBlock _sky;
	readonly PatchyFogBlock _planar;
	readonly Vector3 _planeNormal;
	readonly float _planeDistance;
	readonly float _planarOpaqueDepth;
	readonly Dictionary<uint, Look> _looks;
	readonly Random _random = new();

	/// <summary>
	/// A patchy fog tag and its noise images.
	/// </summary>
	sealed record Look( PatchyFogTag Tag, Texture[] Noise );

	PatchyFog( PatchyFogBlock sky, PatchyFogBlock planar, Vector3 planeNormal, float planeDistance, float planarOpaqueDepth, Dictionary<uint, Look> looks )
	{
		_sky = sky;
		_planar = planar;
		_planeNormal = planeNormal;
		_planeDistance = planeDistance;
		_planarOpaqueDepth = planarOpaqueDepth;
		_looks = looks;
	}

	/// <summary>
	/// The patchy fog of a map (its first sky's and its planar fog's), or null if it has none.
	/// </summary>
	public static PatchyFog Build( CacheFile cache )
	{
		var scenario = new ScenarioTag( cache.Scenario );
		var skyTag = scenario.Skies.FirstOrDefault( s => s.Is( SkyTag.Class ) && s.Address != 0 );
		var sky = skyTag is null ? null : new SkyTag( skyTag ).PatchyFog;
		var planarFog = PlanarFog.Find( cache );
		var planar = planarFog?.Fog.PatchyFog;

		var looks = new Dictionary<uint, Look>();
		foreach ( var block in new[] { sky, planar } )
		{
			if ( block?.PatchyFog is not { } tag || !tag.Is( PatchyFogTag.Class ) || tag.Address == 0 || looks.ContainsKey( tag.Id ) )
				continue;

			var fpch = new PatchyFogTag( tag );
			var images = fpch.NoiseMap is { Address: not 0 } noise ? new BitmapTag( noise ).Bitmaps.Count : 0;
			if ( images == 0 )
				continue;

			looks[tag.Id] = new Look( fpch, Enumerable.Range( 0, images )
				.Select( i => Texture.Load( $"mount://halo2/{Halo2Mount.TextureName( fpch.NoiseMap.Path, i )}.vtex" ) )
				.ToArray() );
		}

		if ( looks.Count == 0 )
			return null;

		return new PatchyFog(
			sky is not null && sky.PatchyFog is { } s && looks.ContainsKey( s.Id ) ? sky : null,
			planar is not null && planar.PatchyFog is { } p && looks.ContainsKey( p.Id ) ? planar : null,
			planarFog?.Normal ?? Vector3.Up, planarFog?.Distance ?? 0, MathF.Max( planarFog?.Fog.OpaqueDepth ?? 1, 1e-4f ),
			looks );
	}

	// Per view state (MCC keeps one per player view; here one for whichever view draws).
	float _angle, _phase;
	int _first;
	readonly Vector2[] _offsets = new Vector2[4];
	Weather.View? _old;
	float _lastTime = -1;

	// Wind: direction and speed easing toward targets picked every period.
	Vector2 _windDirection = new( 1, 0 ), _targetDirection = new( 1, 0 );
	float _windSpeed, _targetSpeed, _lastChange, _windPeriod;

	/// <summary>
	/// The patchy fog for a camera: colour, density and distance ranges and look, or null where none draws.
	/// </summary>
	(Vector3 Color, float MinDensity, float MaxDensity, float Near, float Far, Look Look)? Current( Weather.View camera )
	{
		Vector3 color = default;
		float dmin = 0, dmax = 0, zmin = 0, zmax = 0;
		Look look = null;
		if ( _sky is { } sky && sky.Density.Y > 0 )
		{
			(color, dmin, dmax, zmin, zmax) = ((Vector3)sky.Color, sky.Density.X, sky.Density.Y, sky.Distance.X, sky.Distance.Y);
			look = _looks[sky.PatchyFog.Id];
		}

		// Deeper in the planar fog than its minimum depth fraction: toward its patchy fog.
		if ( _planar is { } planar )
		{
			var eye = Math.Clamp( -(Vector3.Dot( _planeNormal, camera.Position ) - _planeDistance) / _planarOpaqueDepth, 0, 1 );
			var mdf = Math.Clamp( planar.MinimumDepthFraction, 0, 0.9999f );
			var t = Math.Clamp( (eye - mdf) / (1 - mdf), 0, 1 );
			if ( t > 0 )
			{
				color = Vector3.Lerp( color, (Vector3)planar.Color, t );
				dmin = MathX.Lerp( dmin, planar.Density.X, t );
				dmax = MathX.Lerp( dmax, planar.Density.Y, t );
				zmin = MathX.Lerp( zmin, planar.Distance.X, t );
				zmax = MathX.Lerp( zmax, planar.Distance.Y, t );
				look ??= _looks[planar.PatchyFog.Id];
			}
		}

		if ( look is null || dmax <= 1e-4f || zmax <= 1e-4f )
			return null;

		dmin = Math.Clamp( dmin, 0, 1 );
		dmax = dmin > dmax ? dmin : MathF.Min( dmax, 1 );
		if ( dmax <= 0 )
			return null;

		color = new Vector3( Math.Clamp( color.x, 0, 1 ), Math.Clamp( color.y, 0, 1 ), Math.Clamp( color.z, 0, 1 ) );
		return (color, dmin, dmax, zmin, MathF.Max( zmin + 1e-4f, zmax ), look);
	}

	/// <summary>
	/// The wind, as MCC has it: eases toward its target direction and speed every frame, and every period (random in
	/// its range) turns its target by up to a right angle and picks a new speed. Returns the wind (world units/s).
	/// </summary>
	Vector3 UpdateWind( PatchyFogTag fp, float time )
	{
		if ( fp.WindVelocity.Y > 0 )
		{
			var a = fp.WindAccelerationWeight;
			var direction = _windDirection * (1 - a) + _targetDirection * a;
			_windDirection = direction.Length > 1e-6f ? direction.Normal : new Vector2( 1, 0 );
			_windSpeed = (1 - a) * _windSpeed + a * _targetSpeed;

			if ( time - _lastChange >= _windPeriod )
			{
				var x = MathF.Pow( _random.NextSingle(), 1 - fp.WindPerpendicularWeight );
				var sign = _random.NextSingle() < 0.5f ? -1 : 1;
				var perpendicular = new Vector2( -_windDirection.y, _windDirection.x );
				var target = _windDirection * (1 - x) + perpendicular * (sign * x);
				_targetDirection = target.Length > 1e-6f ? target.Normal : _windDirection;
				_lastChange = time;
				_targetSpeed = MathX.Lerp( fp.WindVelocity.X, fp.WindVelocity.Y, _random.NextSingle() );
				_windPeriod = MathX.Lerp( fp.WindPeriod.X, fp.WindPeriod.Y, _random.NextSingle() );
			}
		}

		return new Vector3( _windSpeed * _windDirection.x, _windSpeed * _windDirection.y, 0 );
	}

	Vector2 RandomOffset() => new( _random.Next( 65536 ) / 65536f, _random.Next( 65536 ) / 65536f );

	/// <summary>
	/// Updates the layers for this frame (as MCC does) and draws the fog over the frame, if the fog's tag sorts it
	/// on this side of the weather: behind the transparents (before the weather), or else after them (MCC's default,
	/// just before bloom, so the weather's plates and flakes are fogged too).
	/// </summary>
	public void Draw( Weather.View camera, float time, Material material, bool behindTransparents )
	{
		if ( Current( camera ) is not { } fog )
		{
			_old = null;
			return;
		}

		if ( ((fog.Look.Tag.Flags & 2) != 0) != behindTransparents )
			return;

		var fp = fog.Look.Tag;
		var dt = _lastTime < 0 ? 0 : MathF.Max( time - _lastTime, 0 );
		_lastTime = time;

		var wind = UpdateWind( fp, time );
		var drift = (wind + (Vector3)fp.WindConstantVelocity * 30) * dt;

		var range = fog.Far - fog.Near;
		var step = range / 4;
		var viewport = Graphics.Viewport;
		var aspect = viewport.Width > 0 ? viewport.Height / viewport.Width : 9f / 16f;
		var tanHalf = MathF.Tan( Graphics.FieldOfView.DegreeToRadian() / 2 );
		var k = NearPlane * fp.NoiseMapScale * 0.5f * 35 / (MathF.Max( fog.Far, 1e-4f ) * MathF.Max( tanHalf, 1e-4f ));

		if ( _old is { } old )
		{
			// Roll, and forward motion through the depth slices.
			var r = camera.ToViewDirection( old.ToWorldDirection( new Vector3( 1, 0, 0 ) ) );
			_angle += MathF.Atan2( r.y, r.x ) * fp.RotationMultiplier;
			var moved = camera.ToView( old.Position + drift );
			_phase -= moved.z * fp.ZoomMultiplier / MathF.Max( step, 1e-4f );
			var f = (int)MathF.Floor( _phase );
			_phase -= f;
			_first = ((_first - f) % 4 + 4) % 4;
			if ( f < 0 )
				_offsets[(_first + 3) & 3] = RandomOffset();
			if ( f > 0 )
				_offsets[_first] = RandomOffset();
		}
		else
		{
			for ( var i = 0; i < 4; i++ )
				_offsets[i] = RandomOffset();
		}

		// Looking up or down: the up scales instead of the forward ones.
		var c = 0.5f + 0.5f * MathF.Cos( MathF.PI * camera.Forward.z );
		var verticalScale = c * fp.NoiseVerticalScaleForward + (1 - c) * fp.NoiseVerticalScaleUp;
		var opacity = (1 - c) * fp.NoiseOpacityScaleUp + c;
		var cos = MathF.Cos( _angle );
		var sin = MathF.Sin( _angle );
		var images = fog.Look.Noise.Length;

		var a = Graphics.Attributes;
		for ( var i = 0; i < 4; i++ )
		{
			var slot = (_first + i) & 3;
			var z = (i + _phase) * step + fog.Near;
			var u = (z - fog.Near) / MathF.Max( range, 1e-4f );
			var edge = MathF.Pow( MathF.Abs( 2 * u - 1 ), 3 );
			var fade = (1 - edge) * (1 - edge) * opacity;
			var scale = z * fp.NoiseMapScale / MathF.Max( fog.Far, 1e-4f );

			if ( _old is { } last )
			{
				var p = camera.ToView( last.ToWorld( new Vector3( 0, 0, -z ) ) + drift );
				var o = _offsets[slot];
				_offsets[slot] = new Vector2(
					o.x - (p.x * cos + p.y * sin * verticalScale) * 0.5f * k * fp.StrafingMultiplier,
					o.y - (p.y * cos * verticalScale - p.x * sin) * 0.5f * k * fp.StrafingMultiplier );
			}

			int frame;
			Vector4 weights;
			if ( fp.AnimationPeriod > 0 )
			{
				var x = time / fp.AnimationPeriod + images * AnimationPhases[slot];
				frame = (int)MathF.Floor( x ) % images;
				var b = Math.Clamp( x - MathF.Floor( x ), 0, 1 );
				weights = new Vector4( Saturate( (1 - b) * (1 - b) * fade ), Saturate( 2 * b * (1 - b) * fade ), Saturate( b * b * fade ), Saturate( u ) );
			}
			else
			{
				frame = slot % images;
				weights = new Vector4( 0, Saturate( fade ), 0, Saturate( u ) );
			}

			var noise = fog.Look.Noise[Math.Clamp( frame, 0, images - 1 )];
			noise?.MarkUsed( 65535 );
			a.Set( $"HaloPatchyNoise{slot}", noise ?? Texture.White );
			a.Set( $"HaloPatchyLayer{slot}", weights );

			// uv = 0.5 scale R(angle) (x, aspect · vertical scale · y) + offset, x and y the screen in -1..1 (y up).
			var half = 0.5f * scale;
			a.Set( $"HaloPatchyU{slot}", new Vector4( half * cos, half * sin * aspect * verticalScale, _offsets[slot].x, 0 ) );
			a.Set( $"HaloPatchyV{slot}", new Vector4( -half * sin, half * cos * aspect * verticalScale, _offsets[slot].y, 0 ) );
		}

		_old = camera;

		a.Set( "HaloPatchyColor", new Vector4( fog.Color.x, fog.Color.y, fog.Color.z, (fp.Flags & 1) != 0 ? 1 : 0 ) );
		a.Set( "HaloPatchyDensity", new Vector4( fog.MinDensity, fog.MaxDensity, fog.Near * Halo2Mount.WorldScale, MathF.Max( range, 1e-4f ) * Halo2Mount.WorldScale ) );
		Graphics.GrabFrameTexture( "HaloWeatherFrame" );
		Graphics.Blit( material, a );
	}

	static float Saturate( float x ) => Math.Clamp( x, 0, 1 );
}
