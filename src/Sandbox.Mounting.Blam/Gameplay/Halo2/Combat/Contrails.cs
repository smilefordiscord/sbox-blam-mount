using System;
using System.Buffers;
using Halo2.Data;

namespace Halo2.Combat;

// A contrail tag (cont) from the mount's tag document.
public sealed class ContrailTag {
	public List<string> Flags { get; set; } = new();
	public List<string> ScaleFlags { get; set; } = new();
	// Points per second.
	public float PointGenerationRate { get; set; }
	// World units per second, bounds.
	public float[] PointVelocity { get; set; }
	// Degrees.
	public float PointVelocityConeAngle { get; set; }
	public float InheritedVelocityFraction { get; set; }
	public string RenderType { get; set; }
	public float TextureRepeatsU { get; set; }
	public float TextureRepeatsV { get; set; }
	public float TextureAnimationU { get; set; }
	public float TextureAnimationV { get; set; }
	public string FramebufferFadeMode { get; set; }
	public List<ContrailPointStateTag> PointStates { get; set; } = new();

	public bool HasFlag( string flag ) => Flags?.Contains( flag ) ?? false;
	public bool Scales( string flag ) => ScaleFlags?.Contains( flag ) ?? false;
}

// A stage of a point's life. Colors are A, R, G, B.
public sealed class ContrailPointStateTag {
	public float[] Duration { get; set; }
	public float[] TransitionDuration { get; set; }
	public string Physics { get; set; }
	// World units.
	public float Width { get; set; }
	public float[] ColorLowerBound { get; set; }
	public float[] ColorUpperBound { get; set; }
	public List<string> ScaleFlags { get; set; } = new();

	public bool Scales( string flag ) => ScaleFlags?.Contains( flag ) ?? false;
}

// Halo's contrails: ribbons through points a marker drops at the point generation rate.
// New points are spread evenly from the newest point to the marker, so fast rounds still leave even trails.
// Each point steps through the point states on its own clock, blending width and colour, and expires after the last.
// An orphaned contrail lives on until its last point expires.
public sealed class ContrailSystem : GameObjectSystem<ContrailSystem> {
	// Halo shares 1024 points across all contrails.
	public const int MaximumPoints = 1024;

	readonly List<Contrail> _contrails = new();
	internal int PointCount;

	public ContrailSystem( Scene scene ) : base( scene ) {
		// Runs after Halo.ProjectileVisuals (order 10) has laid this frame's points.
		Listen( Stage.FinishUpdate, 20, Update, "Halo2.Contrails" );
	}

	// A new contrail with its first point at the marker.
	public Contrail Create( ContrailDefinition definition, Vector3 marker, Rotation orientation, Vector3 objectVelocity ) {
		if ( definition is null || _contrails.Count >= 256 ) return null;

		var contrail = new Contrail( this, definition );
		_contrails.Add( contrail );
		contrail.AddPoints( 1, true, marker, orientation, objectVelocity );
		return contrail;
	}

	void Update() {
		using var perf = Halo2.Combat.HaloPerf.Measure( "Contrails.Update" );
		var dt = Time.Delta;
		for ( var i = _contrails.Count - 1; i >= 0; i-- ) {
			var contrail = _contrails[i];
			contrail.Update( dt );
			if ( contrail.Finished ) {
				contrail.Delete();
				_contrails.RemoveAt( i );
			}
		}
	}

	public override void Dispose() {
		foreach ( var contrail in _contrails )
			contrail.Delete();
		_contrails.Clear();
		base.Dispose();
	}
}

// A contrail's tag and the mount's material for it.
public sealed class ContrailDefinition {
	public ContrailTag Tag;
	public Material Material;

	static readonly Dictionary<string, ContrailDefinition> Cache = new();

	// Null when the mount has no tag document or material for it.
	public static ContrailDefinition Get( string contrailPath ) {
		if ( string.IsNullOrEmpty( contrailPath ) ) return null;
		contrailPath = contrailPath.Replace( '\\', '/' );
		if ( Cache.TryGetValue( contrailPath, out var cached ) && (cached is null || cached.Material.IsValid) ) return cached;

		var tag = HaloDataStore.Tag<ContrailTag>( contrailPath );
		var material = LoadMaterial( $"mount://{HaloMountCheck.Ident}/{HaloEffects.WithoutExtension( contrailPath )}__contrail.vmat" );
		var definition = tag is { PointStates.Count: > 0, PointGenerationRate: > 0 } && material is not null ? new ContrailDefinition { Tag = tag, Material = material } : null;
		if ( definition is not null || HaloDataStore.IsAvailable ) Cache[contrailPath] = definition;
		return definition;
	}

	static Material LoadMaterial( string path ) {
		try {
			return HaloMountCheck.Mount?.GetByPath( path ) is not null ? Material.Load( path ) : null;
		}
		catch ( Exception e ) {
			Log.Warning( $"Couldn't load {path}: {e.Message}" );
			return null;
		}
	}
}

// One contrail's points, newest first as Halo keeps them, and the scene object that draws them.
public sealed class Contrail {
	const int PointNew = 1, PointTransitioning = 2, PointExpired = 4;

	sealed class Point {
		public Vector3 Position;
		public Vector3 Velocity;
		public float Time;
		public float Delta;
		public int State;
		public int Flags;
		public float Density;
	}

	// A point as the render thread draws it.
	readonly struct RenderPoint( Vector3 position, float halfWidth, Color color, float u ) {
		public readonly Vector3 Position = position;
		public readonly float HalfWidth = halfWidth;
		public readonly Color Color = color;
		public readonly float U = u;
	}

	sealed class Frame {
		public RenderPoint[] Points;
		public float VNear;
		public float VFar;
	}

	readonly ContrailSystem _system;
	readonly ContrailDefinition _definition;
	readonly List<Point> _points = new();
	readonly SceneCustomObject _so;
	Frame _frame;

	float _timeUntilPoint;
	float _expiredDt;
	float _textureOffsetU;
	float _textureOffsetV;
	const float Density = 1;

	// Lays points while attached. Orphaned when the object is deleted.
	public bool Attached { get; private set; } = true;

	public bool Finished => !Attached && _points.Count == 0;

	internal Contrail( ContrailSystem system, ContrailDefinition definition ) {
		_system = system;
		_definition = definition;
		_so = new SceneCustomObject( system.Scene.SceneWorld ) { RenderOverride = Render };
		_so.Flags.IsOpaque = false;
		_so.Flags.IsTranslucent = true;
		_so.Flags.CastShadows = false;
	}

	ContrailTag Tag => _definition.Tag;

	// Lays the points due over dt, from the newest point to the marker.
	public void Advance( float dt, Vector3 marker, Rotation orientation, Vector3 objectVelocity ) {
		if ( !Attached ) return;
		AddPoints( ComputeNewPointCount( dt ), true, marker, orientation, objectVelocity );
	}

	// The object hit something dt into its move. Lays at least one point up to the collision.
	public void OwnerCollision( float dt, Vector3 marker, Rotation orientation, Vector3 objectVelocity ) {
		if ( !Attached ) return;
		AddPoints( Math.Max( 1, ComputeNewPointCount( dt ) ), false, marker, orientation, objectVelocity );
		_expiredDt += dt;
	}

	// The object is going away. Halo 2 lays no points for that.
	public void Orphan() => Attached = false;

	int ComputeNewPointCount( float dt ) {
		var timeBetweenPoints = 1 / Tag.PointGenerationRate;
		var count = 0;
		while ( dt != 0 ) {
			if ( dt >= _timeUntilPoint ) {
				dt -= _timeUntilPoint;
				_timeUntilPoint = timeBetweenPoints;
				count++;
			}
			else {
				_timeUntilPoint -= dt;
				break;
			}
		}
		return count;
	}

	// Spreads count points from the newest point to the marker, the last one on it.
	// Skipped when the marker hasn't moved, unless forced.
	internal void AddPoints( int count, bool force, Vector3 marker, Rotation orientation, Vector3 objectVelocity ) {
		if ( count == 0 ) return;

		var previous = _points.Count > 0 ? _points[0] : null;
		if ( previous is null ) count = 1;
		if ( previous is not null && previous.Position == marker && !force ) return;

		var speed = 0f;
		if ( Tag.PointVelocity is { Length: > 1 } v )
			speed = HaloUnits.PerSecondToInchesPerSecond( v[0] + Game.Random.Float( 0, v[1] - v[0] ) );
		var cone = Tag.PointVelocityConeAngle;
		var inherited = Tag.InheritedVelocityFraction;

		for ( var i = 1; i <= count; i++ ) {
			if ( _system.PointCount >= ContrailSystem.MaximumPoints ) break;

			var direction = cone > 0 ? RandomInCone( orientation.Forward, cone ) : orientation.Forward;
			var point = new Point {
				Flags = PointNew | PointTransitioning,
				State = -1,
				Density = Density,
				Position = marker,
				Velocity = objectVelocity * inherited + direction * speed,
			};

			if ( i < count ) {
				var t = (float)i / count;
				point.Density = previous.Density.LerpTo( point.Density, t );
				point.Position = Vector3.Lerp( previous.Position, point.Position, t );
				point.Velocity = Vector3.Lerp( previous.Velocity, point.Velocity, t );
			}

			_points.Insert( 0, point );
			_system.PointCount++;
		}
	}

	static Vector3 RandomInCone( Vector3 forward, float degrees ) {
		var angle = Game.Random.Float( 0, degrees ).DegreeToRadian();
		var around = Game.Random.Float( 0, MathF.PI * 2 );
		var side = Vector3.Cross( forward, MathF.Abs( forward.z ) < 0.99f ? Vector3.Up : Vector3.Forward ).Normal;
		var up = Vector3.Cross( side, forward );
		return (forward * MathF.Cos( angle ) + (side * MathF.Cos( around ) + up * MathF.Sin( around )) * MathF.Sin( angle )).Normal;
	}

	// Scrolls the texture, steps point states, and drops expired points off the old end.
	internal void Update( float dt ) {
		var frameDt = dt - _expiredDt;
		_expiredDt = 0;

		_textureOffsetU -= Tag.TextureAnimationU * (Tag.Scales( "texture animation u" ) ? Density : 1) * frameDt;
		_textureOffsetV += Tag.TextureAnimationV * (Tag.Scales( "texture animation v" ) ? Density : 1) * frameDt;

		foreach ( var point in _points ) {
			UpdatePoint( point, dt );
			point.Flags &= ~PointNew;
		}

		// The last point goes only once it has expired too.
		while ( _points.Count > 1 && (_points[^1].Flags & PointExpired) != 0 && (_points[^2].Flags & PointExpired) != 0 )
			RemoveOldest();
		if ( _points.Count == 1 && (_points[0].Flags & PointExpired) != 0 )
			RemoveOldest();

		BuildFrame();
	}

	void RemoveOldest() {
		_points.RemoveAt( _points.Count - 1 );
		_system.PointCount--;
	}

	void UpdatePoint( Point point, float dt ) {
		if ( (point.Flags & PointExpired) != 0 ) return;

		var states = Tag.PointStates;
		point.Time += dt * point.Delta;

		while ( point.Delta == 0 || point.Time > 1 ) {
			if ( (point.Flags & PointTransitioning) != 0 ) {
				point.State++;
				var state = states[point.State];
				point.Time = 0;
				point.Delta = RandomRange( point.Density, state.Duration, state.Scales( "duration" ), state.Scales( "duration delta" ) );
				if ( point.Delta != 0 ) point.Delta = 1 / point.Delta;
				point.Flags &= ~PointTransitioning;
			}
			else if ( point.State + 1 < states.Count ) {
				var state = states[point.State];
				point.Time = 0;
				point.Delta = RandomRange( point.Density, state.TransitionDuration, state.Scales( "transition duration" ), state.Scales( "transition duration delta" ) );
				if ( point.Delta != 0 ) point.Delta = 1 / point.Delta;
				point.Flags |= PointTransitioning;
			}
			else {
				point.Flags |= PointExpired;
				break;
			}
		}
	}

	// Lower bound plus a random part of the range, each optionally scaled by density.
	static float RandomRange( float density, float[] bounds, bool scaleMinimum, bool scaleRange ) {
		if ( bounds is not { Length: > 1 } ) return 0;
		var minimum = scaleMinimum ? bounds[0] * density : bounds[0];
		var range = scaleRange ? (bounds[1] - bounds[0]) * density : bounds[1] - bounds[0];
		return Game.Random.Float( 0, range ) + minimum;
	}

	// A point's colour and full width in inches, blended toward the next state while transitioning.
	(Color Color, float Width) Look( Point point ) {
		var states = Tag.PointStates;
		var state = states[Math.Clamp( point.State, 0, states.Count - 1 )];
		var (color, width) = StateLook( state, point.Density );

		if ( (point.Flags & PointTransitioning) != 0 && point.State + 1 < states.Count ) {
			var (nextColor, nextWidth) = StateLook( states[point.State + 1], point.Density );
			var t = point.Time;
			width = (nextWidth - width) * t + width;
			color = new Color(
				(nextColor.r - color.r) * t + color.r,
				(nextColor.g - color.g) * t + color.g,
				(nextColor.b - color.b) * t + color.b,
				(nextColor.a - color.a) * t + color.a );
		}

		return (color, HaloUnits.ToInches( width ));
	}

	static (Color Color, float Width) StateLook( ContrailPointStateTag state, float density ) {
		var scale = state.Scales( "color" ) ? density : 1;
		var lower = state.ColorLowerBound is { Length: > 3 } l ? l : new float[] { 1, 1, 1, 1 };
		var upper = state.ColorUpperBound is { Length: > 3 } u ? u : lower;
		// Halo's colours are A, R, G, B.
		var color = new Color(
			(upper[1] - lower[1]) * scale + lower[1],
			(upper[2] - lower[2]) * scale + lower[2],
			(upper[3] - lower[3]) * scale + lower[3],
			(upper[0] - lower[0]) * scale + lower[0] );
		var width = state.Width * (state.Scales( "width" ) ? density : 1);
		return (color, width);
	}

	// u steps back by the u repeats at each point, so the texture repeats per point, not per distance.
	// v runs across the ribbon from the offset to the v repeats.
	void BuildFrame() {
		if ( _points.Count < 2 ) {
			_frame = null;
			_so.Bounds = default;
			return;
		}

		var uStep = -Tag.TextureRepeatsU * (Tag.Scales( "texture scale u" ) ? Density : 1);
		var vNear = _textureOffsetV;
		var vFar = Tag.TextureRepeatsV * (Tag.Scales( "texture scale v" ) ? Density : 1) + _textureOffsetV;

		var points = new RenderPoint[_points.Count];
		var u = _textureOffsetU;
		var bounds = BBox.FromPositionAndSize( _points[0].Position, 1 );
		var maxWidth = 0f;
		for ( var i = 0; i < _points.Count; i++ ) {
			var point = _points[i];
			var (color, width) = Look( point );
			color = color.WithAlpha( Math.Clamp( color.a, 0, 1 ) );
			points[i] = new RenderPoint( point.Position, width * 0.5f, color, u );
			u += uStep;
			bounds = bounds.AddPoint( point.Position );
			maxWidth = MathF.Max( maxWidth, width );
		}

		// The end points are transparent unless the tag says otherwise.
		if ( !Tag.HasFlag( "first point unfaded" ) ) points[0] = Faded( points[0] );
		if ( !Tag.HasFlag( "last point unfaded" ) ) points[^1] = Faded( points[^1] );

		_so.Bounds = bounds.Grow( maxWidth + 1 );
		_frame = new Frame { Points = points, VNear = vNear, VFar = vFar };
	}

	static RenderPoint Faded( RenderPoint p ) => new( p.Position, p.HalfWidth, p.Color.WithAlpha( 0 ), p.U );

	// Two vertices per point, spread across the ribbon according to its render type.
	void Render( SceneObject so ) {
		var frame = _frame;
		var material = _definition.Material;
		if ( frame?.Points is not { Length: > 1 } points || material is null ) return;

		var renderType = Tag.RenderType ?? "viewer facing";
		var camera = Graphics.CameraPosition;
		var vertexCount = points.Length * 2;
		var indexCount = (points.Length - 1) * 6;
		// Pooled, since a long contrail's vertices would land on the large object heap every frame; Draw copies them.
		var vertices = ArrayPool<Vertex>.Shared.Rent( vertexCount );
		var indices = ArrayPool<ushort>.Shared.Rent( indexCount );
		try {

			// The mount's shader multiplies v by the tag's v repeats, so divide it out here.
			var repeatsV = Tag.TextureRepeatsV != 0 ? Tag.TextureRepeatsV : 1;
			var vFar = frame.VFar / repeatsV;
			var vNear = frame.VNear / repeatsV;

			// A framebuffer fade mode scales alpha by how squarely the camera sees the ribbon, so end-on bolts look faint.
			// "edge effect fades slowly" raises it to the 1/4 power, and "fade when parallel" inverts it.
			var fadeMode = Tag.FramebufferFadeMode;
			var fadeWhenParallel = fadeMode == "fade when parallel";
			var fades = fadeWhenParallel || fadeMode == "fade when perpendicular";
			var fadesSlowly = Tag.HasFlag( "edge effect fades slowly" );

			for ( var i = 0; i < points.Length; i++ ) {
				var point = points[i];
				var previous = i > 0 ? points[i - 1].Position : (Vector3?)null;
				var next = i + 1 < points.Length ? points[i + 1].Position : point.Position;
				Vector3 offset;
				var orientation = Vector3.Zero;

				switch ( renderType ) {
					case "vertical orientation": {
						offset = Vector3.Up;
						var across = previous is { } p
							? new Vector3( p.y - point.Position.y, point.Position.x - p.x, 0 )
							: new Vector3( point.Position.y - next.y, next.x - point.Position.x, 0 );
						orientation = across.Length > 0.0001f ? across.Normal : Vector3.Zero;
						break;
					}

					case "horizontal orientation":
					case "media mapped": {
						var perpendicular = previous is { } p
							? new Vector2( p.y - point.Position.y, point.Position.x - p.x )
							: new Vector2( point.Position.y - next.y, next.x - point.Position.x );
						perpendicular = perpendicular.Length > 0.0001f ? perpendicular.Normal : Vector2.Zero;
						offset = new Vector3( perpendicular.x, perpendicular.y, 0 );
						orientation = Vector3.Up;
						break;
					}

					case "viewer facing": {
						Vector3 eye, tangent;
						if ( previous is { } p ) {
							eye = camera - p;
							tangent = point.Position - p;
						}
						else {
							eye = camera - point.Position;
							tangent = next - point.Position;
						}
						var facing = Vector3.Cross( eye, tangent );
						offset = facing.Length > 0.0001f ? facing.Normal : Vector3.Zero;
						var normal = Vector3.Cross( offset, tangent );
						orientation = normal.Length > 0.0001f ? normal.Normal : Vector3.Zero;
						break;
					}

					default:
						// Halo doesn't draw ground mapped or double-marker linked contrails.
						return;
				}

				var tint = point.Color;
				if ( fades ) {
					var toCamera = camera - point.Position;
					var fade = toCamera.Length > 0.0001f ? MathF.Abs( Vector3.Dot( orientation, toCamera ) ) / toCamera.Length : 1;
					if ( fadesSlowly ) fade = MathF.Pow( Math.Clamp( fade, 0, 1 ), 0.25f );
					if ( fadeWhenParallel ) fade = 1 - fade;
					tint = tint.WithAlpha( Math.Clamp( tint.a * fade, 0, 1 ) );
				}

				var color = (Color32)tint;
				vertices[i * 2] = new Vertex {
					Position = point.Position - offset * point.HalfWidth,
					TexCoord0 = new Vector4( point.U, vFar, 0, 0 ),
					Color = color,
					Normal = Vector3.Up,
					Tangent = new Vector4( 1, 0, 0, 1 ),
				};
				vertices[i * 2 + 1] = new Vertex {
					Position = point.Position + offset * point.HalfWidth,
					TexCoord0 = new Vector4( point.U, vNear, 0, 0 ),
					Color = color,
					Normal = Vector3.Up,
					Tangent = new Vector4( 1, 0, 0, 1 ),
				};
			}

			for ( var s = 0; s < points.Length - 1; s++ ) {
				var k = s * 6;
				indices[k] = (ushort)(2 * s);
				indices[k + 1] = (ushort)(2 * s + 1);
				indices[k + 2] = (ushort)(2 * s + 2);
				indices[k + 3] = (ushort)(2 * s + 2);
				indices[k + 4] = (ushort)(2 * s + 1);
				indices[k + 5] = (ushort)(2 * s + 3);
			}

			BindBehind();
			Graphics.Draw( vertices.AsSpan(), vertexCount, indices.AsSpan(), indexCount, material );
		}
		finally {
			ArrayPool<Vertex>.Shared.Return( vertices );
			ArrayPool<ushort>.Shared.Return( indices );
		}
	}

	// The mount's contrail shader blends against a full-resolution grab of the frame behind it.
	// Each view regrabs for its first few contrails so overlapping contrails see each other, then reuses the last grab.
	const int MaxGrabsPerView = 4;
	static readonly object GrabLock = new();
	static (float Time, Vector3 Position, Rotation Rotation, Rect Viewport) _behindView;
	static int _grabsThisView;
	static Texture _behind;

	static void BindBehind() {
		lock ( GrabLock ) {
			var view = (Time.Now, Graphics.CameraPosition, Graphics.CameraRotation, Graphics.Viewport);
			var sameView = _behindView == view;
			if ( _behind is null || !sameView || _grabsThisView < MaxGrabsPerView ) {
				var frame = Graphics.GrabFrameTexture( "HaloContrailBehind" ).ColorTarget;
				_behind = Texture.CreateRenderTarget( "halo contrail behind", frame.ImageFormat, frame.Size, _behind );
				Graphics.CopyTexture( frame, _behind );
				_grabsThisView = sameView ? _grabsThisView + 1 : 1;
				_behindView = view;
			}

			Graphics.Attributes.Set( "HaloContrailBehind", _behind );
			Graphics.Attributes.Set( "HaloContrailGrabbed", 1f );
		}
	}

	internal void Delete() {
		_system.PointCount -= _points.Count;
		_points.Clear();
		_frame = null;
		_so?.Delete();
	}
}

// A contrail an object carries: its tag and its marker at the default pose, in inches from the object's origin.
// The tracer is the projectile's first contrail attachment, which Halo leaves off shots that aren't tracers.
public sealed record ContrailAttachment( string Contrail, Vector3 Position, bool Tracer );

// Halo's contrails are drawn by ContrailSystem from their tags, not by the trail renderers the mount's prefabs carry.
// The prefabs still say where each one is: a child tagged halo_contrail per marker instance.
public static class ContrailAttachments {
	const string ContrailTag = "halo_contrail";

	sealed class TagFile {
		public List<AttachmentData> Attachments { get; set; } = new();
	}

	sealed class AttachmentData {
		public string Type { get; set; }
	}

	static readonly Dictionary<string, List<ContrailAttachment>> Cache = new();

	// The contrails of an object's prefab, in prefab order.
	public static List<ContrailAttachment> For( string objectPath, PrefabFile prefab ) {
		if ( string.IsNullOrEmpty( objectPath ) || prefab is null ) return new();
		if ( Cache.TryGetValue( objectPath, out var cached ) ) return cached;

		var doc = HaloDataStore.Tag<TagFile>( objectPath );
		var tracer = doc?.Attachments.FirstOrDefault( x => x.Type?.EndsWith( ".cont" ) ?? false )?.Type;

		var list = new List<ContrailAttachment>();
		var root = SceneUtility.GetPrefabScene( prefab );
		foreach ( var child in root.GetAllObjects( false ).Where( x => x.Tags.Has( ContrailTag ) ) ) {
			// The mount's contrail material is "<contrail tag>__contrail.vmat".
			var material = child.GetComponent<TrailRenderer>( true )?.Texturing.Material?.ResourcePath;
			const string suffix = "__contrail.vmat";
			var prefix = $"mount://{HaloMountCheck.Ident}/";
			if ( material is null || !material.StartsWith( prefix ) || !material.EndsWith( suffix ) ) continue;

			var contrail = $"{material[prefix.Length..^suffix.Length]}.cont";
			var position = root.WorldTransform.PointToLocal( child.WorldPosition );
			list.Add( new ContrailAttachment( contrail, position, contrail == tracer ) );
		}

		// Cached only once the document has loaded.
		if ( doc is not null ) Cache[objectPath] = list;
		return list;
	}

	// Removes the prefab's own trail renderers from a spawned copy.
	public static void Strip( GameObject go ) {
		foreach ( var contrail in go.GetAllObjects( false ).Where( x => x.Tags.Has( ContrailTag ) ).ToList() )
			contrail.Destroy();
	}
}
