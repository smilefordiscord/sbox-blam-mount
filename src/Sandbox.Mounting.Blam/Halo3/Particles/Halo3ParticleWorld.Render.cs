using Blam.Halo3.Tags;

namespace Sandbox.Mounting.Blam.Halo3;

// Drawing: each particle system is one transparent object, its sprites built on the CPU (the prt3's runtime sprite rectangle times the particle's size, stretched by its aspect ratio, turned by its rotation, on its billboard's axes) and drawn by halo3_particle.shader.
sealed partial class Halo3ParticleWorld
{
	// A sprite to draw, as of the last frame (view independent).
	struct DrawItem
	{
		// s&box units.
		public Vector3 Position;

		// Which way it's going (world space) and the location's frame, for the billboards that use them.
		public Vector3 Direction;
		public Rotation Frame;

		// Radians.
		public float Rotation;

		// The particle's size (s&box units) and its aspect ratio (width over height).
		public float Size;
		public float Aspect;

		// Linear HDR colour and alpha.
		public Vector4 Color;
		public float BlackPoint;
		public float PaletteRow;

		public Vector4 Sprite;
		public Vector4 Next;
		public float Blend;
		public byte Mirror;
	}

	const byte MirrorU = 0x1;
	const byte MirrorV = 0x2;

	sealed class RenderSystem
	{
		public SceneCustomObject Object;
		public Halo3ParticleDefinition Particle;
		public bool LightVolume;
		public float NearFadeRange;
		public float NearFadeCutoff;

		// Its system's LOD distances, applied per view when drawing; null for light volumes.
		public global::Blam.Halo3.Tags.EffectParticleSystem System;

		public DrawItem[] Items = new DrawItem[16];
		public int Count;

		// Every place its particles have been (s&box units), its object's bounds: s&box sorts translucent objects by the camera's distance to their bounds, and bounds following the live particles swapped places with Valhalla's waterfall sheet frame to frame.
		public BBox? Reached;

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

		foreach ( var volume in _volumes )
		{
			volume.Render?.Delete();
			volume.Render = null;
		}

		_probe?.Delete();
		_probe = null;
	}

	// The views that drew the scene lately (world units): a system's LOD is the nearest view's.
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
		lock ( _viewLock )
		{
			// Views seen within ViewLife of the newest (an editor that only redraws now and then keeps its last view, rather than falling back to the scene's camera somewhere else).
			var newest = _views.Count > 0 ? _views.Max( v => v.Time ) : 0;
			var oldest = newest - (long)(ViewLife * System.Diagnostics.Stopwatch.Frequency);
			_views.RemoveAll( v => v.Time < oldest );
			foreach ( var view in _views )
				_cameras.Add( view.Position );
		}

		if ( _cameras.Count == 0 && Scene.Camera is { } camera )
			_cameras.Add( camera.WorldPosition / Units );
	}

	static int _drawFrame;

	// What every system draws this frame, between its particles' last two ticks.
	void Snapshot( float fraction )
	{
		_drawFrame++;
		var count = 0;
		foreach ( var system in _systems )
		{
			SnapshotSystem( system, fraction );
			count += system.Count;
		}

		SnapshotLightVolumes();
		ParticleCount = count;
	}

	void SnapshotSystem( SystemInstance system, float fraction )
	{
		var definition = system.Definition;
		var particle = definition.Particle;
		var render = system.Render ??= NewRender( particle, definition.System );
		render.Count = 0;

		var tag = particle.Tag;
		var frames = particle.Frames;

		// The farthest the sprite rectangle reaches from the particle, in sizes.
		var r = particle.SpriteRect;
		var extent = MathF.Max( MathF.Max( MathF.Abs( r.x ), MathF.Abs( r.x + r.z ) ), MathF.Max( MathF.Abs( r.y ), MathF.Abs( r.y + r.w ) ) ) * 1.5f;
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

			var emitter = definition.Emitters[p.Emitter].Emitter;
			// As of between the last two ticks, like its position.
			var age = Math.Clamp( p.Age - (1 - fraction) * Tick * p.InvLifespan, 0, 1 );
			var turns = p.PreviousRotation + (p.Rotation - p.PreviousRotation) * fraction;
			SystemInputs( system, location );
			ParticleInputs( p, age, turns );

			var size = MathF.Max( Evaluate( emitter.ParticleSize, 0.1f ), 0 ) * MathF.Max( Evaluate( emitter.Scale, 1 ), 0 );
			if ( size <= 0 )
				continue;

			var color = EvaluateColor( emitter.Tint ) * EvaluateColor( tag.Color ) * MathF.Max( Evaluate( tag.Intensity, 1 ), 0 );
			var alpha = Math.Clamp( Evaluate( emitter.Alpha, 1 ) * Evaluate( tag.Alpha, 1 ), 0, 1 );
			if ( alpha <= 0 )
				continue;

			// The emitter's rotation (turns) added to the particle's own spin; a quarter more for bitmaps authored upright.
			var rotation = (Evaluate( emitter.Rotation, 0 ) + turns) * MathF.PI * 2;
			if ( tag.AuthoredVertically )
				rotation += MathF.PI / 2;

			// The frame from the frame index (0-1 over the frames), blended into the next.
			var frameCount = frames.Length;
			var frame = Math.Clamp( Evaluate( tag.FrameIndex, 0 ), 0, 1 ) * (tag.OneShotAnimation ? frameCount - 1 : frameCount);
			var whole = Math.Clamp( (int)MathF.Floor( frame ), 0, frameCount - 1 );
			var blend = particle.FrameBlend ? frame - MathF.Floor( frame ) : 0;

			var position = p.Previous + (p.Position - p.Previous) * fraction;
			var direction = p.Velocity;
			if ( definition.Local )
			{
				position = location.Position + location.Rotation * position;
				direction = location.Rotation * direction;
			}

			if ( render.Count == render.Items.Length )
				Array.Resize( ref render.Items, render.Count * 2 );

			render.Items[render.Count++] = new DrawItem
			{
				Position = position * Units,
				Direction = direction,
				Frame = location.Rotation,
				Rotation = rotation,
				Size = size * Units,
				Aspect = MathF.Max( Evaluate( tag.AspectRatio, 1 ), 0.01f ),
				Color = new Vector4( color.x, color.y, color.z, alpha ),
				BlackPoint = Math.Clamp( Evaluate( emitter.AlphaBlackPoint, 0 ), 0, 1 ),
				PaletteRow = Math.Clamp( Evaluate( tag.PaletteAnimation, 0 ), 0, 1 ),
				Sprite = frames[whole],
				Next = frames[(whole + 1) % frameCount],
				Blend = blend,
				Mirror = (byte)((tag.RandomUMirror && ((int)(p.Random * 4) & 1) != 0 ? MirrorU : 0) | (tag.RandomVMirror && ((int)(p.Random * 4) & 2) != 0 ? MirrorV : 0)),
			};

			mins = mins.ComponentMin( position * Units );
			maxs = maxs.ComponentMax( position * Units );
			reach = MathF.Max( reach, size * Units * extent * MathF.Max( Evaluate( tag.AspectRatio, 1 ), 1 ) );
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
			{
				var now = new BBox( mins - reach, maxs + reach );
				render.Reached = render.Reached is { } reached ? reached.AddBBox( now ) : now;
				so.Bounds = render.Reached.Value;
			}
		}
	}

	RenderSystem NewRender( Halo3ParticleDefinition particle, EffectParticleSystem system )
	{
		var render = new RenderSystem { Particle = particle, System = system, NearFadeRange = (system?.NearFadeRange ?? 0) * Units, NearFadeCutoff = (system?.NearFadeCutoff ?? 0) * Units };
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

	static bool _warnedInvalidMaterial;

	void Render( RenderSystem render )
	{
		try
		{
			lock ( render.Lock )
				Draw( render );
		}
		catch ( Exception e )
		{
			Halo3Mount.Current?.Logger.Warning( $"Couldn't draw Halo 3 particles: {e.Message}" );
		}
	}

	void Draw( RenderSystem render )
	{
		if ( render.DrawingCount == 0 || render.Particle.Material is not { } material )
			return;

		// A material the engine let go of draws nothing: say so once.
		if ( !material.IsValid() )
		{
			if ( !_warnedInvalidMaterial )
			{
				_warnedInvalidMaterial = true;
				Halo3Mount.Current?.Logger.Warning( $"Halo 3 particles: material {material.Name} is no longer valid" );
			}
			return;
		}

		var camera = Graphics.CameraPosition;
		var rotation = Graphics.CameraRotation;
		var vertices = _vertices ??= new Vertex[MaxQuadsPerDraw * 4];
		var indices = _indices ??= MakeIndices();
		var billboard = render.Particle.Tag?.BillboardStyle ?? 0;
		var rect = render.Particle.SpriteRect;
		var quads = 0;

		for ( var i = 0; i < render.DrawingCount && quads < MaxQuadsPerDraw; i++ )
		{
			ref var item = ref render.Drawing[i];
			var toItem = item.Position - camera;
			var depth = toItem.Dot( rotation.Forward );
			if ( depth < 1 )
				continue;

			// Halo culls a system where its LOD (the view's distance between its in and out distances) is 0.
			if ( render.System is { } lodSystem && lodSystem.LodAt( toItem.Length / Units ) <= 0 )
				continue;

			// Near fade: nothing nearer than the cutoff, faded in over the range past it.
			var color = item.Color;
			if ( render.NearFadeRange > 0 || render.NearFadeCutoff > 0 )
			{
				color.w *= Math.Clamp( (depth - render.NearFadeCutoff) / MathF.Max( render.NearFadeRange, 1e-3f ), 0, 1 );
				if ( color.w <= 0 )
					continue;
			}

			if ( render.LightVolume )
			{
				AddProfile( vertices, quads * 4, item, color, toItem );
				quads++;
				continue;
			}

			var (x, y, roll) = Axes( billboard, item, toItem, rotation );
			if ( x == Vector3.Zero )
				continue;

			AddQuad( vertices, quads * 4, item, rect, color, x, y, roll );
			quads++;
		}

		if ( quads > 0 )
			Graphics.Draw( vertices.AsSpan(), quads * 4, indices.AsSpan(), quads * 6, material );
	}

	// A billboard's axes (right, up) and whether the particle's rotation turns it in that plane.
	static (Vector3 X, Vector3 Y, bool Roll) Axes( int billboard, in DrawItem item, Vector3 toItem, Rotation camera )
	{
		switch ( billboard )
		{
			case 1:
			{
				// Camera facing: toward the camera's position, upright by the camera's up.
				var forward = toItem.Normal;
				var right = forward.Cross( camera.Up );
				if ( right.LengthSquared < 1e-8f )
					return (camera.Right, camera.Up, true);
				right = right.Normal;
				return (right, right.Cross( forward ), true);
			}

			case 2:
			{
				// Parallel to its direction: u along it, turned about it to face the viewer.
				if ( item.Direction.LengthSquared < 1e-12f )
					return (camera.Right, camera.Up, true);
				var axis = item.Direction.Normal;
				var side = toItem.Cross( axis );
				return side.LengthSquared < 1e-12f ? (Vector3.Zero, Vector3.Zero, false) : (axis, side.Normal, false);
			}

			case 3:
			{
				// Perpendicular to its direction: flat across it.
				if ( item.Direction.LengthSquared < 1e-12f )
					return (camera.Right, camera.Up, true);
				var axis = item.Direction.Normal;
				var reference = axis.z * axis.z > 0.99f ? Vector3.Left : Vector3.Up;
				var x = axis.Cross( reference ).Normal;
				return (x, x.Cross( axis ), true);
			}

			case 4:
			case 6:
			{
				// Vertical: upright (the world's up, or the location's), turned about it to face the viewer.
				var up = billboard == 6 ? item.Frame.Up : Vector3.Up;
				var side = up.Cross( toItem );
				return side.LengthSquared < 1e-12f ? (camera.Right, camera.Up, true) : (-side.Normal, up, false);
			}

			case 5:
			case 8:
				// Horizontal: flat on the world's horizontal plane.
				return (Vector3.Forward, Vector3.Left, true);

			case 7:
				// Local horizontal: flat in the location's forward-left plane.
				return (item.Frame.Forward, item.Frame.Left, true);

			case 9:
			{
				// Velocity horizontal: flat, u along its horizontal direction.
				var flat = item.Direction.WithZ( 0 );
				if ( flat.LengthSquared < 1e-12f )
					return (Vector3.Forward, Vector3.Left, true);
				var x = flat.Normal;
				return (x, Vector3.Up.Cross( x ), false);
			}

			default:
				// Screen facing: the view's own axes.
				return (camera.Right, camera.Up, true);
		}
	}

	// A sprite's quad: the runtime sprite rectangle (x, y, width, height, y up) times its size, x stretched by the aspect ratio, rolled, on axes x and y.
	static void AddQuad( Vertex[] vertices, int at, in DrawItem item, Vector4 rect, Vector4 color, Vector3 x, Vector3 y, bool roll )
	{
		var (sine, cosine) = roll && item.Rotation != 0 ? MathF.SinCos( item.Rotation ) : (0f, 1f);
		var mirrorU = (item.Mirror & MirrorU) != 0;
		var mirrorV = (item.Mirror & MirrorV) != 0;

		for ( var c = 0; c < 4; c++ )
		{
			float u = c is 1 or 2 ? 1 : 0;
			float v = c >= 2 ? 1 : 0;
			var su = mirrorU ? 1 - u : u;
			var sv = mirrorV ? 1 - v : v;

			// Halo's sprite rectangle has y up, the texture v down.
			var cx = (rect.x + u * rect.z) * item.Aspect;
			var cy = rect.y + (1 - v) * rect.w;
			var rx = cx * cosine - cy * sine;
			var ry = cy * cosine + cx * sine;

			ref var corner = ref vertices[at + c];
			corner.Position = item.Position + (x * rx + y * ry) * item.Size;
			corner.Color = Color32.White;
			corner.TexCoord0 = new Vector4( item.Sprite.x + su * item.Sprite.z, item.Sprite.y + sv * item.Sprite.w, item.Next.x + su * item.Next.z, item.Next.y + sv * item.Next.w );
			corner.TexCoord1 = color;
			corner.Normal = new Vector3( su, sv, item.Blend );
			corner.Tangent = new Vector4( item.BlackPoint, item.PaletteRow, 0, 0 );
		}
	}

	// The distortion systems' quads for the scene camera's post-processing (Halo3Distortion), as Draw builds sprites.
	void WriteDistortion()
	{
		if ( Halo3Distortion.For( Scene ) is not { } target )
			return;

		var quads = 0;
		if ( Scene.Camera is { } view && view.IsValid() )
		{
			var camera = view.WorldPosition;
			var rotation = view.WorldRotation;
			var vertices = target.Begin();
			foreach ( var system in _systems )
			{
				if ( system.Render is not { Particle.Distortion: { } look } render )
					continue;

				var billboard = render.Particle.Tag?.BillboardStyle ?? 0;
				var rect = render.Particle.SpriteRect;
				lock ( render.Lock )
				for ( var i = 0; i < render.DrawingCount && quads < Halo3Distortion.MaximumQuads; i++ )
				{
					ref var item = ref render.Drawing[i];
					var toItem = item.Position - camera;
					var depth = toItem.Dot( rotation.Forward );
					if ( depth < 1 )
						continue;
					if ( render.System is { } lodSystem && lodSystem.LodAt( toItem.Length / Units ) <= 0 )
						continue;

					var alpha = item.Color.w;
					if ( render.NearFadeRange > 0 || render.NearFadeCutoff > 0 )
						alpha *= Math.Clamp( (depth - render.NearFadeCutoff) / MathF.Max( render.NearFadeRange, 1e-3f ), 0, 1 );
					if ( alpha <= 0 )
						continue;

					var (x, y, roll) = Axes( billboard, item, toItem, rotation );
					if ( x == Vector3.Zero )
						continue;

					AddDistortionQuad( vertices, quads * 6, item, rect, alpha, x, y, roll, look );
					quads++;
				}
			}
		}

		target.End( quads );
	}

	// A distortion sprite as two triangles: the quad AddQuad makes, with the texture's u and up axes (rolled and mirrored as the sprite is) for the offset.
	static void AddDistortionQuad( Vertex[] vertices, int at, in DrawItem item, Vector4 rect, float alpha, Vector3 x, Vector3 y, bool roll, Halo3DistortionLook look )
	{
		var (sine, cosine) = roll && item.Rotation != 0 ? MathF.SinCos( item.Rotation ) : (0f, 1f);
		var mirrorU = (item.Mirror & MirrorU) != 0;
		var mirrorV = (item.Mirror & MirrorV) != 0;
		var axisU = (x * cosine + y * sine) * (mirrorU ? -1 : 1);
		var axisUp = (y * cosine - x * sine) * (mirrorV ? -1 : 1);

		Span<Vertex> corners = stackalloc Vertex[4];
		for ( var c = 0; c < 4; c++ )
		{
			float u = c is 1 or 2 ? 1 : 0;
			float v = c >= 2 ? 1 : 0;
			var su = mirrorU ? 1 - u : u;
			var sv = mirrorV ? 1 - v : v;
			var cx = (rect.x + u * rect.z) * item.Aspect;
			var cy = rect.y + (1 - v) * rect.w;
			var rx = cx * cosine - cy * sine;
			var ry = cy * cosine + cx * sine;

			corners[c] = new Vertex
			{
				Position = item.Position + (x * rx + y * ry) * item.Size,
				Normal = axisU,
				Tangent = new Vector4( axisUp, alpha ),
				TexCoord0 = new Vector4( item.Sprite.x + su * item.Sprite.z, item.Sprite.y + sv * item.Sprite.w, look.Texture, look.Expensive ? 1 : 0 ),
				TexCoord1 = new Vector4( look.DepthFadeRange, look.Scale, Units, 0 ),
				Color = Color32.White,
			};
		}

		vertices[at] = corners[0];
		vertices[at + 1] = corners[1];
		vertices[at + 2] = corners[2];
		vertices[at + 3] = corners[0];
		vertices[at + 4] = corners[2];
		vertices[at + 5] = corners[3];
	}

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
}
