using Blam.Halo2.Tags;

namespace Sandbox.Mounting.Blam.Halo2;

// Drawing: each particle system is one transparent object, as each is one sort item in Halo, its particles in their order, every sprite built on the CPU as Halo builds it and drawn by halo1_sprite.shader in Halo's gamma space blend.
sealed partial class Halo2ParticleWorld
{
	// A sprite to draw, as of the last frame (view independent).
	struct DrawItem
	{
		// s&box units.
		public Vector3 Position;

		// Which way it's going (world space), for the billboards along or across it.
		public Vector3 Direction;

		// Radians.
		public float Rotation;

		// Half its longer side, s&box units.
		public float Size;

		// Gamma space colour and alpha.
		public Vector4 Color;

		public Halo2Sprite Sprite;
		public Halo2Sprite Next;
		public float Blend;
		public byte Mirror;
	}

	const byte MirrorU = 0x1;
	const byte MirrorV = 0x2;

	// A system's draw object and what it draws.
	sealed class RenderSystem
	{
		public SceneCustomObject Object;
		public Halo2Look Look;
		public int Billboard;

		public DrawItem[] Items = new DrawItem[16];
		public int Count;

		// What the render thread draws, swapped in at the end of each frame's snapshot.
		public readonly object Lock = new();
		public DrawItem[] Drawing = new DrawItem[16];
		public int DrawingCount;

		public void Delete()
		{
			Object?.Delete();
			Object = null;
		}
	}

	void ClearRender()
	{
		foreach ( var system in _systems )
		{
			system.Render?.Delete();
			system.Render = null;
		}

		_probe?.Delete();
		_probe = null;
	}

	// The views that drew the scene lately (world units): Halo's system LOD is the nearest view's.
	readonly List<Vector3> _cameras = new();

	SceneCustomObject _probe;
	readonly object _viewLock = new();
	readonly List<(Vector3 Position, long Time)> _views = new();

	// Views seen within this long (seconds) count.
	const float ViewLife = 0.5f;

	// An object every view draws, so the runtime knows where the views are (the editor's included).
	void MakeCameraProbe()
	{
		_probe = new SceneCustomObject( Scene.SceneWorld ) { RenderOverride = _ => SeeView() };
		_probe.Flags.IsOpaque = false;
		_probe.Flags.IsTranslucent = true;
		_probe.Flags.CastShadows = false;
		_probe.Bounds = new BBox( new Vector3( -1e7f ), new Vector3( 1e7f ) );
	}

	void SeeView()
	{
		var position = Graphics.CameraPosition / Units;
		var now = System.Diagnostics.Stopwatch.GetTimestamp();
		lock ( _viewLock )
		{
			for ( var i = 0; i < _views.Count; i++ )
			{
				if ( _views[i].Position.DistanceSquared( position ) < 1e-6f )
				{
					_views[i] = (position, now);
					return;
				}
			}

			if ( _views.Count < 16 )
				_views.Add( (position, now) );
		}
	}

	void UpdateCameras()
	{
		_cameras.Clear();
		var oldest = System.Diagnostics.Stopwatch.GetTimestamp() - (long)(ViewLife * System.Diagnostics.Stopwatch.Frequency);
		lock ( _viewLock )
		{
			_views.RemoveAll( v => v.Time < oldest );
			foreach ( var view in _views )
				_cameras.Add( view.Position );
		}

		if ( _cameras.Count == 0 && Scene.Camera is { } camera )
			_cameras.Add( camera.WorldPosition / Units );
	}

	// Halo 2 draws a first person view's particles through that view's own projection, 1/64 to 1 world unit deep.
	// A game drawing the view model stretched across the camera puts the stretch in this attribute, and they stretch alike.
	const string FirstPersonSpreadAttribute = "HaloFirstPersonSpread";
	const float FirstPersonNear = 1f / 64;
	const float FirstPersonFar = 1;

	// This frame's camera (world units), its forward and the first person stretch across it.
	(Vector3 Camera, Vector3 Forward, float Spread)? _firstPersonView;

	static Vector3 Stretch( Vector3 v, Vector3 forward, float spread ) => v + (spread - 1) * (v - forward * Vector3.Dot( v, forward ));

	// What every system draws this frame, between its particles' last two ticks.
	void Snapshot( float fraction )
	{
		_drawFrame++;
		var spread = Scene.RenderAttributes.GetFloat( FirstPersonSpreadAttribute, 1 );
		_firstPersonView = Scene.Camera is { } camera ? (camera.WorldPosition / Units, camera.WorldRotation.Forward, spread > 0 ? spread : 1) : null;
		var count = 0;
		foreach ( var system in _systems )
		{
			SnapshotSystem( system, fraction );
			count += system.Count;
		}

		ParticleCount = count;
	}

	void SnapshotSystem( SystemInstance system, float fraction )
	{
		var definition = system.Definition;
		var particle = definition.Particle;
		var render = system.Render ??= NewRender( particle.Look, particle.Tag.BillboardStyle );
		render.Count = 0;

		var sheet = particle.Look.Sheet;
		var tag = particle.Tag;
		var mins = new Vector3( float.MaxValue );
		var maxs = new Vector3( float.MinValue );
		var reach = 0f;

		// A local system's particles go where their places are this frame, not as of the last tick.
		if ( definition.Local )
		{
			foreach ( var location in system.Locations )
			{
				if ( location.Valid && location.Place.Valid )
					(location.Position, location.Rotation) = location.Place.Frame;
			}
		}

		for ( var i = 0; i < system.Count; i++ )
		{
			ref var p = ref system.Particles[i];
			var location = system.Locations[p.Location];

			// Halo culls a system where its LOD fades to nothing.
			if ( location.Lod <= 0 )
				continue;

			var emitter = definition.Emitters[p.Emitter].Emitter;
			var age = MathF.Min( p.Age + fraction * Tick * p.InvLifespan, 1 );
			var turns = p.PreviousRotation + (p.Rotation - p.PreviousRotation) * fraction;
			SystemInputs( system, location );
			ParticleInputs( p, age, turns );

			// The emitter's size, tint and alpha times the particle tag's scale, colour and alpha.
			var size = MathF.Max( Evaluate( emitter.Size, 0.1f ), 0 ) * MathF.Max( Evaluate( tag.Scale, 1 ), 0 );
			if ( size <= 0 )
				continue;

			var color = EvaluateColor( emitter.Tint ) * EvaluateColor( tag.Color );
			var alpha = Math.Clamp( Evaluate( emitter.Alpha, 1 ) * Evaluate( tag.Alpha, 1 ), 0, 1 );
			if ( alpha <= 0 )
				continue;

			// Turns of the particle tag's rotation (its own spin's turns when the tag has none and it spins), a quarter more for bitmaps authored upright.
			var rotation = (tag.Rotation.IsEmpty ? (tag.Spins ? turns - MathF.Floor( turns ) : 0) : Evaluate( tag.Rotation, 0 )) * MathF.PI * 2;
			if ( particle.AuthoredVertically )
				rotation += MathF.PI / 2;

			// The sequence (a random one of the tag's for "select random sequence"), the frame from the frame index function, blended with the next.
			var sequence = Math.Max( tag.FirstSequence, 0 );
			if ( particle.RandomSequence )
				sequence += (int)(p.Random2 * 65536) % Math.Max( tag.SequenceCount, 1 );
			sequence = Math.Clamp( sequence, 0, sheet.SequenceCount - 1 );
			var sprites = sheet.Sequences[sequence];
			if ( sprites.Length == 0 )
				continue;

			var frames = particle.OneShot ? sprites.Length - 1 : sprites.Length;
			var frame = MathF.Max( Evaluate( tag.FrameIndex, 0 ), 0 ) * frames;
			var whole = (int)MathF.Floor( frame );
			var blend = particle.NoFrameBlending ? 0 : frame - whole;

			var position = p.Previous + (p.Position - p.Previous) * fraction;
			var direction = p.Velocity;
			if ( definition.Local )
			{
				position = location.Position + location.Rotation * position;
				direction = location.Rotation * direction;
			}

			// A first person particle stretches about its place, which rides the stretched view model already.
			if ( system.FirstPerson && _firstPersonView is { } view )
			{
				position = location.Position + Stretch( position - location.Position, view.Forward, view.Spread );
				direction = Stretch( direction, view.Forward, view.Spread );
				size *= view.Spread;
				var depth = Vector3.Dot( position - view.Camera, view.Forward );
				if ( depth < FirstPersonNear || depth > FirstPersonFar )
					continue;
			}

			if ( render.Count == render.Items.Length )
				Array.Resize( ref render.Items, render.Count * 2 );

			var sprite = sprites[whole % sprites.Length];
			render.Items[render.Count++] = new DrawItem
			{
				Position = position * Units,
				Direction = direction,
				Rotation = rotation,
				Size = size * Units,
				Color = new Vector4( Math.Clamp( color.x, 0, 1 ), Math.Clamp( color.y, 0, 1 ), Math.Clamp( color.z, 0, 1 ), alpha ),
				Sprite = sprite,
				Next = blend > 0 ? sprites[(whole + 1) % sprites.Length] : null,
				Blend = blend,
				Mirror = (byte)((tag.RandomUMirror && ((int)(p.Random1 * 65536) & 1) != 0 ? MirrorU : 0) | (tag.RandomVMirror && ((int)(p.Random2 * 65536) & 1) != 0 ? MirrorV : 0)),
			};

			mins = mins.ComponentMin( position * Units );
			maxs = maxs.ComponentMax( position * Units );
			reach = MathF.Max( reach, size * Units * 2 );
		}

		lock ( render.Lock )
		{
			(render.Items, render.Drawing) = (render.Drawing, render.Items);
			render.DrawingCount = render.Count;
		}

		if ( render.Object is { } so && so.IsValid() )
		{
			so.RenderingEnabled = render.DrawingCount > 0;
			if ( render.DrawingCount > 0 )
				so.Bounds = new BBox( mins - reach, maxs + reach );
		}
	}

	RenderSystem NewRender( Halo2Look look, int billboard )
	{
		var render = new RenderSystem { Look = look, Billboard = billboard };
		MakeClampWhite();
		render.Object = new SceneCustomObject( Scene.SceneWorld ) { RenderOverride = _ => Render( render ) };
		render.Object.Flags.IsOpaque = false;
		render.Object.Flags.IsTranslucent = true;
		render.Object.Flags.CastShadows = false;
		render.Object.RenderingEnabled = false;
		return render;
	}

	// The render thread's side.

	[ThreadStatic] static Vertex[] _vertices;
	[ThreadStatic] static ushort[] _indices;

	const int MaxQuadsPerDraw = 16384;
	const string BehindAttribute = "HaloSpriteBehind";

	void Render( RenderSystem render )
	{
		try
		{
			lock ( render.Lock )
				Draw( render );
		}
		catch ( Exception e )
		{
			Halo2Mount.Current?.Logger.Warning( $"Couldn't draw Halo 2 particles: {e.Message}" );
		}
	}

	void Draw( RenderSystem render )
	{
		if ( render.DrawingCount == 0 || render.Look.Material is not { } material )
			return;

		var camera = Graphics.CameraPosition;
		var rotation = Graphics.CameraRotation;
		var right = rotation.Right;
		var up = rotation.Up;
		var vertices = _vertices ??= new Vertex[MaxQuadsPerDraw * 4];
		var indices = _indices ??= MakeIndices();
		var quads = 0;
		var mins = new Vector3( float.MaxValue );
		var maxs = new Vector3( float.MinValue );

		for ( var i = 0; i < render.DrawingCount && quads < MaxQuadsPerDraw; i++ )
		{
			ref var item = ref render.Drawing[i];
			var toItem = item.Position - camera;
			if ( toItem.Dot( rotation.Forward ) < 1 )
				continue;

			Vector3 x = right, y = up;
			var roll = item.Rotation;
			switch ( render.Billboard )
			{
				case 1:
				{
					// Parallel to its direction: the sprite's u along it, turned about it to face the viewer.
					if ( item.Direction.LengthSquared < 1e-12f )
						break;

					x = item.Direction.Normal;
					y = toItem.Cross( x );
					if ( y.LengthSquared < 1e-12f )
						continue;
					y = y.Normal;
					roll = 0;
					break;
				}

				case 2:
				{
					// Perpendicular to its direction: flat across it.
					if ( item.Direction.LengthSquared < 1e-12f )
						break;

					var axis = item.Direction.Normal;
					var reference = axis.z * axis.z > 0.99f ? Vector3.Left : Vector3.Up;
					x = axis.Cross( reference ).Normal;
					y = x.Cross( axis );
					break;
				}

				case 3:
				{
					// Vertical: upright, turned about the world's up to face the viewer.
					var side = Vector3.Up.Cross( toItem );
					if ( side.LengthSquared < 1e-12f )
						break;
					x = -side.Normal;
					y = Vector3.Up;
					break;
				}

				case 4:
					// Horizontal: flat on the world's horizontal plane.
					x = Vector3.Forward;
					y = Vector3.Left;
					break;
			}

			if ( AddQuad( vertices, quads * 4, item, x, y, roll ) )
			{
				for ( var c = 0; c < 4; c++ )
				{
					mins = mins.ComponentMin( vertices[quads * 4 + c].Position );
					maxs = maxs.ComponentMax( vertices[quads * 4 + c].Position );
				}
				quads++;
			}
		}

		if ( quads == 0 )
			return;

		Behind( ScreenRect( new BBox( mins, maxs ) ) );
		var count = quads * 4;
		Graphics.Draw( vertices.AsSpan(), count, indices.AsSpan(), quads * 6, material );

		// Overlapping adds of one draw can't see each other, so what they touched is clamped at white after them, as Halo's 8-bit frame was.
		if ( render.Look.Adds && ClampWhite() is { } clamp )
			Graphics.Draw( vertices.AsSpan(), count, indices.AsSpan(), quads * 6, clamp );
	}

	// A sprite's quad on axes x and y around its registration point, rolled and mirrored: Halo's corners span twice its size along its longer side (Halo's sprite tangent rectangle).
	static bool AddQuad( Vertex[] vertices, int at, in DrawItem item, Vector3 x, Vector3 y, float roll )
	{
		var sprite = item.Sprite;
		if ( !Index( sprite ) )
			return false;

		var next = item.Next is { } n && Index( n ) ? n.Index + 1 : 0;
		var (sine, cosine) = roll != 0 ? MathF.SinCos( roll ) : (0, 1);
		var extent = sprite.Extent * 2;
		var left = -sprite.Origin.x * extent.x;
		var right = extent.x + left;
		var top = sprite.Origin.y * extent.y;
		var bottom = top - extent.y;
		var flipU = (item.Mirror & MirrorU) != 0 ? -1 : 1;
		var flipV = (item.Mirror & MirrorV) != 0 ? -1 : 1;

		for ( var c = 0; c < 4; c++ )
		{
			var u = c is 1 or 2 ? 1 : 0;
			var v = c >= 2 ? 1 : 0;
			var cx = u == 0 ? left : right;
			var cy = v == 0 ? top : bottom;
			var rx = (cx * cosine - cy * sine) * flipU;
			var ry = (cy * cosine + cx * sine) * flipV;

			ref var corner = ref vertices[at + c];
			corner.Position = item.Position + (x * rx + y * ry) * item.Size;
			// Alpha 255: the corner is placed here (see halo1_sprite.shader).
			corner.Color = new Color32( 0, 0, 0, 255 );
			corner.Normal = new Vector3( next, next > 0 ? item.Blend : 0, 0 );
			corner.TexCoord0 = new Vector4( u, v, sprite.Index, 0 );
			corner.TexCoord1 = item.Color;
			corner.Tangent = Vector4.Zero;
		}

		return true;
	}

	// The frame being drawn (set by each snapshot), so textures are looked up once per frame.
	static volatile int _drawFrame;

	// A sprite's bindless index, looked up (and streaming told it's used) once a frame since it can change as the texture loads.
	static bool Index( Halo2Sprite sprite )
	{
		if ( sprite?.Texture is not { } texture )
			return false;

		var frame = _drawFrame;
		if ( sprite.DrawFrame != frame )
		{
			sprite.DrawFrame = frame;
			sprite.Index = texture.IsValid() && !texture.IsError ? texture.Index : -1;
			if ( sprite.Index >= 0 )
				texture.MarkUsed( 65535 );
		}

		return sprite.Index >= 0;
	}

	static Material _clampWhite;

	// halo1_sprite.shader's pass clamping what adds left at white, made on the main thread (materials can't be made while rendering).
	static void MakeClampWhite()
	{
		if ( _clampWhite is not null && _clampWhite.IsValid() )
			return;

		_clampWhite = Material.Create( "halo 2 particles clamp white", Halo2ParticleMaterialLoader.ShaderPath );
		_clampWhite?.SetFeature( "F_BLEND_MODE", 8 );
		_clampWhite?.SetFeature( "F_ZSPRITE", 0 );
	}

	// The render thread's view of it: none until the main thread has made it.
	static Material ClampWhite() => _clampWhite is not null && _clampWhite.IsValid() ? _clampWhite : null;

	static ushort[] MakeIndices()
	{
		var indices = new ushort[MaxQuadsPerDraw * 6];
		for ( var i = 0; i < MaxQuadsPerDraw; i++ )
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

	// The frame behind the sprites, for halo1_sprite.shader's gamma space blend: grabbed again for a draw over what an earlier draw of the view changed, up to a few times a view (as Halo CE's runtime does).

	static readonly object GrabLock = new();
	static Texture _behind;

	// One copy per frame size, kept: views of different sizes (a game view and the editor's) grab in turn every frame, and a
	// copy replaced and collected while draws recorded with it wait on the GPU is read freed, which faults the GPU.
	static readonly Dictionary<(ImageFormat Format, Vector2 Size), Texture> _behindBySize = new();
	static (Vector3 Position, Rotation Rotation, Rect Viewport, float Time) _behindView;
	static readonly List<Rect?> _drawnSinceGrab = new();
	static int _grabsThisView;
	static bool _copyFailed;

	const int MaxGrabsPerView = 6;

	static void Behind( Rect? rect )
	{
		lock ( GrabLock )
		{
			var view = (Graphics.CameraPosition, Graphics.CameraRotation, Graphics.Viewport, Time.Now);
			var sameView = _behindView == view;
			var reuse = !_copyFailed && _behind is not null && sameView
				&& (_grabsThisView >= MaxGrabsPerView || !_drawnSinceGrab.Any( r => Overlap( r, rect ) ));
			if ( reuse )
			{
				Graphics.Attributes.Set( BehindAttribute, _behind );
			}
			else
			{
				var grabbed = Graphics.GrabFrameTexture( BehindAttribute );
				_drawnSinceGrab.Clear();
				_grabsThisView = sameView ? _grabsThisView + 1 : 1;
				_behindView = view;
				if ( !_copyFailed )
				{
					try
					{
						var frame = grabbed.ColorTarget;
						var key = (frame.ImageFormat, frame.Size);
						if ( !_behindBySize.TryGetValue( key, out _behind ) )
							_behindBySize[key] = _behind = Texture.CreateRenderTarget( "halo 2 particles behind", frame.ImageFormat, frame.Size );
						Graphics.CopyTexture( frame, _behind );
					}
					catch ( Exception e )
					{
						_copyFailed = true;
						_behind = null;
						Halo2Mount.Current?.Logger.Warning( $"Couldn't keep the frame behind Halo 2 particles: {e.Message}" );
					}
				}
			}

			_drawnSinceGrab.Add( rect );
		}
	}

	static Rect? ScreenRect( BBox box )
	{
		var position = Graphics.CameraPosition;
		var inverse = Graphics.CameraRotation.Inverse;
		float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
		for ( var i = 0; i < 8; i++ )
		{
			var corner = new Vector3( (i & 1) == 0 ? box.Mins.x : box.Maxs.x, (i & 2) == 0 ? box.Mins.y : box.Maxs.y, (i & 4) == 0 ? box.Mins.z : box.Maxs.z );
			var local = inverse * (corner - position);
			if ( local.x < 1 )
				return null;

			var x = -local.y / local.x;
			var y = local.z / local.x;
			minX = MathF.Min( minX, x );
			maxX = MathF.Max( maxX, x );
			minY = MathF.Min( minY, y );
			maxY = MathF.Max( maxY, y );
		}

		return new Rect( minX, minY, maxX - minX, maxY - minY );
	}

	static bool Overlap( Rect? a, Rect? b ) =>
		a is not { } x || b is not { } y || (x.Left < y.Right && y.Left < x.Right && x.Top < y.Bottom && y.Top < x.Bottom);
}
