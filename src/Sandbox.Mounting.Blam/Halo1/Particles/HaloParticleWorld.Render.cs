using Blam.Halo1;
using Blam.Halo1.Tags;

namespace Sandbox.Mounting.Blam.Halo1;

// Drawing: every sprite built as Halo builds it, drawn with halo1_sprite.shader.
sealed partial class HaloParticleWorld
{
	// One sprite to draw, as of the last frame (view independent).
	struct DrawItem
	{
		public HaloLook Look;

		// s&box units.
		public Vector3 Position;

		// What the sprite turns by: the particle's direction (along or across it), or a rotational particle's axis.
		public Vector3 Direction;

		// Where Halo's transparent sort puts it: a particle tag's particles go as one (their centroid), each particle system particle on its own.
		public Vector3 SortPoint;

		public float Rotation;

		// s&box units per sprite unit (see HaloSprite).
		public float Scale;

		// For particle tags (0 otherwise): its radius (s&box units), for its size on screen, its minimum size and its LOD cutoff (pixels).
		public float Radius;
		public float MinimumPixels;
		public float LodCutoff;

		// Gamma space color and alpha, and the fade (or cross fade weight) the alpha goes by.
		public Vector4 Color;
		public float Fade;

		// How far a zsprite's depth reaches toward the viewer, s&box units.
		public float ZReach;

		public HaloSprite Sprite;
		public HaloSprite[] Face;
		public HaloSprite[] Edge;
		public int SpriteIndex;

		public byte Mode;
		public byte Flags;

		// The particle slot it draws, or -1 (particle system particles).
		public int Particle;
	}

	// Modes.
	const byte ScreenFacing = 0;
	const byte ParallelToDirection = 1;
	const byte PerpendicularToDirection = 2;
	const byte Rotational = 3;

	// Draw flags.
	const byte DrawMirrorU = 0x1;
	const byte DrawMirrorV = 0x2;
	const byte Sideways = 0x4;

	// Particles drawn together: effects with the same tags, in the world or with a view model.
	sealed class RenderGroup
	{
		public string Key;
		public string[] Tags;
		public bool Overlay;
		public SceneCustomObject Object;
		public DrawItem[] Items = new DrawItem[64];
		public int Count;

		// What the render thread draws, swapped in at the end of each frame's snapshot.
		public readonly object Lock = new();
		public DrawItem[] Drawing = new DrawItem[64];
		public int DrawingCount;
		public float Time;
	}

	readonly List<RenderGroup> _groups = new();
	readonly Dictionary<string, int> _groupIndex = new();

	// When a view last drew each particle slot (written while drawing).
	readonly float[] _drawn = new float[MaxParticles];

	// An effect's render groups: by its tags, and the view model's for its first person only particles.
	(int Group, int FirstPersonGroup) GroupsFor( GameObject root )
	{
		var tags = root.Tags.TryGetAll().Where( t => !OwnTag( t ) ).Distinct().OrderBy( t => t, StringComparer.Ordinal ).ToArray();
		var key = string.Join( ' ', tags );
		var group = GroupFor( key, tags, false );
		var viewModel = root.Tags.Has( HaloEffects.ViewModelTag ) || root.Tags.Has( "viewmodel" );
		return (group, viewModel ? GroupFor( key + "|overlay", tags, true ) : group);
	}

	static bool OwnTag( string tag ) =>
		tag is HaloEffects.RootTag or HaloEffects.FirstPersonTag or HaloEffects.LoopTag or HaloEffects.AttachedTag or "halo_effect_location" or "halo_attachments"
		|| HaloEffectLibrary.IsToken( tag );

	int GroupFor( string key, string[] tags, bool overlay )
	{
		if ( _groupIndex.TryGetValue( key, out var index ) )
			return index;

		var group = new RenderGroup { Key = key, Tags = tags, Overlay = overlay };
		group.Object = new SceneCustomObject( Scene.SceneWorld ) { RenderOverride = _ => Render( group ) };
		group.Object.Flags.IsOpaque = false;
		group.Object.Flags.IsTranslucent = true;
		group.Object.Flags.CastShadows = false;
		foreach ( var tag in tags )
			group.Object.Tags.Add( tag );
		if ( overlay )
		{
			group.Object.Flags.OverlayLayer = true;
			group.Object.Flags.ExcludeGameLayer = false;
		}

		_groups.Add( group );
		_groupIndex[key] = _groups.Count - 1;
		return _groups.Count - 1;
	}

	void ClearRender()
	{
		foreach ( var group in _groups )
			group.Object?.Delete();
		_groups.Clear();
		_groupIndex.Clear();
	}

	static void Add( RenderGroup group, in DrawItem item )
	{
		if ( group.Count == group.Items.Length )
			Array.Resize( ref group.Items, group.Count * 2 );
		group.Items[group.Count++] = item;
	}

	readonly Dictionary<(int Group, int Order), (Vector3 Sum, int Count)> _centroids = new();

	// What every group draws this frame, from the particles and systems as they are now.
	void Snapshot()
	{
		foreach ( var group in _groups )
			group.Count = 0;

		// Particles drawn by a view since the last snapshot.
		for ( var i = 0; i < _particleHigh; i++ )
		{
			ref var p = ref _particles[i];
			if ( p.Definition is not null && _drawn[i] > p.LastDrawn )
				p.LastDrawn = _drawn[i];
		}

		_centroids.Clear();
		for ( var i = 0; i < _particleHigh; i++ )
		{
			ref var p = ref _particles[i];
			if ( p.Definition is null || p.Group >= _groups.Count )
				continue;

			var look = p.Definition.Look;
			var tag = p.Definition.Tag;
			var sprite = look.Sheet.Get( p.Sequence, p.Frame );
			if ( sprite is null )
				continue;

			var position = p.Position;
			var direction = p.Direction;
			if ( p.Attached is { } attached )
			{
				if ( !attached.IsValid() )
					continue;

				position = attached.WorldTransform.PointToWorld( position * Units ) / Units;
				direction = attached.WorldRotation * direction;
			}

			var radius = RadiusOf( p );
			var fade = 1f;
			var remaining = p.Lifespan - p.Age;
			if ( tag.FadeInTime > 0 && p.Age < tag.FadeInTime )
				fade = p.Age / tag.FadeInTime;
			if ( tag.FadeOutTime > 0 && remaining < tag.FadeOutTime )
				fade *= remaining / tag.FadeOutTime;
			if ( fade <= 0 )
				continue;

			var oneOverWidth = tag.OneOverSpriteWidth > 0 ? tag.OneOverSpriteWidth : 1 / MathF.Max( sprite.Size.x, 1 );
			var mode = tag.Orientation switch
			{
				ParticleRenderMode.ParallelToDirection => ParallelToDirection,
				ParticleRenderMode.PerpendicularToDirection => PerpendicularToDirection,
				_ => ScreenFacing,
			};

			var key = (p.Group, look.Order);
			var centroid = _centroids.GetValueOrDefault( key );
			_centroids[key] = (centroid.Sum + position, centroid.Count + 1);

			Add( _groups[p.Group], new DrawItem
			{
				Look = look,
				Position = position * Units,
				Direction = direction,
				Rotation = p.Rotation,
				Scale = 2 * radius * oneOverWidth * Units,
				Radius = radius * Units,
				MinimumPixels = tag.MinimumSize,
				LodCutoff = tag.LodCutoff,
				Color = p.Color,
				Fade = fade,
				ZReach = radius * look.ZSpriteScale * Units,
				Sprite = sprite,
				Mode = mode,
				Flags = (byte)(((p.Flags & MirrorU) != 0 ? DrawMirrorU : 0) | ((p.Flags & MirrorV) != 0 ? DrawMirrorV : 0)),
				Particle = i,
			} );
		}

		// Each particle tag's particles sort together, by their centroid.
		foreach ( var group in _groups )
		{
			for ( var i = 0; i < group.Count; i++ )
			{
				ref var item = ref group.Items[i];
				if ( item.Particle < 0 )
					continue;

				var (sum, count) = _centroids[(_particles[item.Particle].Group, item.Look.Order)];
				item.SortPoint = sum / count * Units;
			}
		}

		foreach ( var system in _systems )
			SnapshotSystem( system );

		foreach ( var group in _groups )
		{
			lock ( group.Lock )
			{
				(group.Items, group.Drawing) = (group.Drawing, group.Items);
				group.DrawingCount = group.Count;
				group.Time = _now;
			}

			UpdateBounds( group );
		}
	}

	void SnapshotSystem( SystemInstance system )
	{
		if ( system.Group >= _groups.Count )
			return;

		var group = _groups[system.Group];
		var definition = system.Definition;
		for ( var t = 0; t < definition.Types.Length; t++ )
		{
			var typeDefinition = definition.Types[t];
			var type = system.Types[t];
			if ( type.State < 0 || typeDefinition.Disabled || !typeDefinition.Type.DrawnIn( system.FirstPerson ? EffectView.FirstPerson : EffectView.ThirdPerson ) )
				continue;

			var rotational = typeDefinition.Type.Rotational;
			var mode = rotational ? Rotational : (byte)Math.Clamp( typeDefinition.Type.SpriteRenderMode, 0, 2 );
			var variables = type.Variables;
			foreach ( var p in type.Particles )
			{
				if ( !p.Valid || p.State < 0 )
					continue;

				var state = typeDefinition.ParticleStates[p.State];
				var next = p.Transition >= 0 ? typeDefinition.ParticleStates[p.Transition] : null;
				if ( state.Look is null )
					continue;

				float stateWeight = 1, nextWeight = 0, scale;
				Vector4 color;
				if ( next is null )
				{
					scale = p.Current.Scale * variables[0];
					color = p.Current.Color;
				}
				else
				{
					stateWeight = Math.Clamp( p.Length != 0 ? p.TimeLeft / p.Length : 0, 0, 1 );
					nextWeight = 1 - stateWeight;
					scale = (p.Next.Scale * nextWeight + p.Current.Scale * stateWeight) * variables[0];
					color = p.Next.Color * nextWeight + p.Current.Color * stateWeight;

					// States drawn alike don't cross fade.
					var a = state.State;
					var b = next.State;
					if ( a.BlendFunction == b.BlendFunction && a.PrimaryMapFlags == b.PrimaryMapFlags && a.Sequence == b.Sequence )
						(stateWeight, nextWeight) = (1, 0);
				}

				color = new Vector4( color.x * variables[4], color.y * variables[5], color.z * variables[6], color.w * variables[3] );

				// Alpha blended states of a system an effect made are lit where it was made.
				if ( state.State.BlendFunction == FramebufferBlendFunction.AlphaBlend )
					color = new Vector4( color.x * system.Lighting.x, color.y * system.Lighting.y, color.z * system.Lighting.z, color.w );

				// The sprite: random at first, then moving on at the animation rate.
				var sequence = Math.Max( state.State.Sequence, 0 ) + (rotational ? 1 : 0);
				var count = state.Look.Sheet.SpriteCount( sequence );
				if ( count <= 0 )
					continue;
				if ( p.Sprite == -1 )
					p.Sprite = RangeInt( 0, count );
				var index = (int)p.Sprite % count;
				if ( index < 0 )
					index += count;

				var flags = (typeDefinition.Flags & 0x80) != 0 ? Sideways : (byte)0;
				if ( stateWeight > 0.01f )
					Add( group, SystemItem( state, p, mode, flags, index, scale, color, stateWeight ) );
				if ( nextWeight > 0.01f && next.Look is not null )
					Add( group, SystemItem( next, p, mode, flags, index, scale, color, nextWeight ) );
			}
		}
	}

	static DrawItem SystemItem( HaloSystemParticleState state, SystemParticle p, byte mode, byte flags, int index, float scale, Vector4 color, float weight )
	{
		var sheet = state.Look.Sheet;
		var sequence = Math.Max( state.State.Sequence, 0 );
		var position = p.Position * Units;
		return new DrawItem
		{
			Look = state.Look,
			Position = position,
			SortPoint = position,
			Direction = p.Axis,
			Rotation = p.Rotation,
			Scale = scale * Units,
			Color = color,
			Fade = weight,
			ZReach = state.State.RadiusMultiplier * state.Look.ZSpriteScale * Units,
			Sprite = mode == Rotational ? null : sheet.Get( sequence, index ),
			Face = mode == Rotational && sequence < sheet.SequenceCount ? sheet.Sequences[sequence] : null,
			Edge = mode == Rotational && sequence + 1 < sheet.SequenceCount ? sheet.Sequences[sequence + 1] : null,
			SpriteIndex = index,
			Mode = mode,
			Flags = flags,
			Particle = -1,
		};
	}

	static void UpdateBounds( RenderGroup group )
	{
		var so = group.Object;
		if ( so is null || !so.IsValid() )
			return;

		if ( group.DrawingCount == 0 )
		{
			so.RenderingEnabled = false;
			return;
		}

		var mins = new Vector3( float.MaxValue );
		var maxs = new Vector3( float.MinValue );
		var reach = 0f;
		for ( var i = 0; i < group.DrawingCount; i++ )
		{
			ref var item = ref group.Drawing[i];
			mins = mins.ComponentMin( item.Position );
			maxs = maxs.ComponentMax( item.Position );
			var size = item.Sprite?.Size ?? new Vector2( 64 );
			var extent = MathF.Max( size.x, size.y ) * item.Scale * 2;

			// Drawn at a minimum size on screen, far ones grow.
			if ( item.MinimumPixels > 0 )
				extent = MathF.Max( extent, 2048 );
			reach = MathF.Max( reach, extent );
		}

		so.Bounds = new BBox( mins - reach, maxs + reach );
		so.RenderingEnabled = true;
	}

	// The render thread's side.

	// One view's quads: four corners each, with a key that sorts them back to front and in the order made.
	sealed class Quads
	{
		public Vertex[] Corners = new Vertex[1024];
		public long[] Keys = new long[256];
		public int[] Indices = new int[256];
		public Material[] Materials = new Material[256];
		public FramebufferBlendFunction[] Blends = new FramebufferBlendFunction[256];
		public int Count;

		public int Add( float distance, int order, Material material, FramebufferBlendFunction blend )
		{
			if ( Count == Keys.Length )
			{
				Array.Resize( ref Keys, Count * 2 );
				Array.Resize( ref Indices, Count * 2 );
				Array.Resize( ref Materials, Count * 2 );
				Array.Resize( ref Blends, Count * 2 );
				Array.Resize( ref Corners, Count * 8 );
			}

			// Farther first, by depth slices about 1.5% of the distance deep; within a slice by material (one draw each), then in order.
			var slice = (int)Math.Clamp( MathF.Log2( MathF.Max( distance, 1 ) ) * SlicesPerDoubling, 0, 4095 );
			Keys[Count] = ((long)(4095 - slice) << 44) | ((long)(material.GetHashCode() & 0xFFFFF) << 24) | (uint)(order & 0xFFFFFF);
			Indices[Count] = Count;
			Materials[Count] = material;
			Blends[Count] = blend;
			return Count++ * 4;
		}
	}

	// Depth slices per doubling of the squared distance: about 1.5% of the distance each.
	const float SlicesPerDoubling = 24;

	[ThreadStatic] static Quads _quads;

	// The frame being drawn, for looking textures up once per frame.
	static int _drawFrame;

	// The passes clamping adds at white and subtracts at black (halo1_sprite.shader's last two blend modes).
	static Material _clampWhite;
	static Material _clampBlack;

	static void MakeClampMaterials()
	{
		if ( _clampWhite is not null && _clampWhite.IsValid() )
			return;

		Material Make( string name, int mode )
		{
			var material = Material.Create( name, ParticleSpriteMaterial.ShaderPath );
			material?.SetFeature( "F_BLEND_MODE", mode );
			material?.SetFeature( "F_ZSPRITE", 0 );
			return material;
		}

		_clampWhite = Make( "halo particles clamp white", 8 );
		_clampBlack = Make( "halo particles clamp black", 9 );
	}
	[ThreadStatic] static Vertex[] _vertices;
	[ThreadStatic] static ushort[] _indices;

	const int MaxQuadsPerDraw = 16384;

	// 1 for sprites drawn only in s&box's overlay layer (halo1_sprite.shader's g_flHaloOverlayOnly).
	const string OverlayOnlyAttribute = "HaloOverlayOnly";

	const string BehindAttribute = "HaloSpriteBehind";

	void Render( RenderGroup group )
	{
		try
		{
			lock ( group.Lock )
				Draw( group );
		}
		catch ( Exception e )
		{
			HaloCeMount.Current?.Logger.Warning( $"Couldn't draw Halo particles: {e.Message}" );
		}
	}

	void Draw( RenderGroup group )
	{
		var timer = System.Diagnostics.Stopwatch.StartNew();
		var quads = _quads ??= new Quads();
		quads.Count = 0;

		var camera = Graphics.CameraPosition;
		var rotation = Graphics.CameraRotation;
		var view = new View
		{
			Camera = camera,
			Forward = rotation.Forward,
			Right = rotation.Right,
			Up = rotation.Up,
			Focal = Graphics.Viewport.Width * 0.5f / MathF.Tan( MathF.Max( Graphics.FieldOfView, 1 ).DegreeToRadian() * 0.5f ),
		};

		_drawFrame = _frame;
		var items = group.Drawing;
		for ( var i = 0; i < group.DrawingCount; i++ )
			Build( ref items[i], i, view, quads, group.Time );

		if ( quads.Count == 0 )
			return;

		// Halo's transparent sort: back to front by where each sorts, in the order made within.
		Array.Sort( quads.Keys, quads.Indices, 0, quads.Count );

		var runs = 0;
		var vertices = _vertices ??= new Vertex[MaxQuadsPerDraw * 4];
		var indices = _indices ??= MakeIndices();
		for ( var start = 0; start < quads.Count; )
		{
			var material = quads.Materials[quads.Indices[start]];
			var blend = quads.Blends[quads.Indices[start]];
			var end = start;
			var mins = new Vector3( float.MaxValue );
			var maxs = new Vector3( float.MinValue );
			while ( end < quads.Count && end - start < MaxQuadsPerDraw && quads.Materials[quads.Indices[end]] == material )
			{
				var from = quads.Indices[end] * 4;
				var to = (end - start) * 4;
				for ( var c = 0; c < 4; c++ )
				{
					var position = quads.Corners[from + c].Position;
					vertices[to + c] = quads.Corners[from + c];
					mins = mins.ComponentMin( position );
					maxs = maxs.ComponentMax( position );
				}
				end++;
			}

			var bounds = new BBox( mins, maxs );

			Behind( ScreenRect( bounds ) );
			Graphics.Attributes.Set( OverlayOnlyAttribute, group.Overlay ? 1f : 0f );
			var count = (end - start) * 4;
			Graphics.Draw( vertices.AsSpan(), count, indices.AsSpan(), count / 4 * 6, material );

			// Overlapping adds of one draw can't see each other, so what they touched is clamped at white after them (subtracts at black), as Halo's 8-bit frame was.
			var clamp = blend is FramebufferBlendFunction.Add or FramebufferBlendFunction.AlphaMultiplyAdd ? _clampWhite
				: blend == FramebufferBlendFunction.Subtract ? _clampBlack : null;
			if ( clamp is not null )
				Graphics.Draw( vertices.AsSpan(), count, indices.AsSpan(), count / 4 * 6, clamp );
			start = end;
			runs++;
		}

		DrawMilliseconds = (float)timer.Elapsed.TotalMilliseconds;
		DrawRuns = runs;
		DrawQuads = quads.Count;
	}

	struct View
	{
		public Vector3 Camera;
		public Vector3 Forward;
		public Vector3 Right;
		public Vector3 Up;

		// Pixels per s&box unit at a distance of one along the view.
		public float Focal;
	}

	// An item's quad (two for a rotational particle seen part way edge on), as Halo builds it.
	void Build( ref DrawItem item, int order, in View view, Quads quads, float time )
	{
		var toItem = item.Position - view.Camera;
		var depth = toItem.Dot( view.Forward );
		if ( depth < 1 )
			return;

		var scale = item.Scale;
		if ( item.Radius > 0 )
		{
			// Too small on screen isn't drawn; small is drawn at the minimum size.
			var diameter = 2 * item.Radius * view.Focal / depth;
			if ( diameter <= item.LodCutoff )
				return;
			if ( item.MinimumPixels > 0 && diameter < item.MinimumPixels && diameter > 0 )
				scale *= item.MinimumPixels / diameter;
		}

		if ( item.Particle >= 0 )
			_drawn[item.Particle] = time;

		var distance = (item.SortPoint - view.Camera).LengthSquared;
		var look = item.Look;
		var fade = item.Fade;
		var mirror = item.Flags;

		switch ( item.Mode )
		{
			case ParallelToDirection:
			{
				var direction = item.Direction;
				if ( direction.LengthSquared < 1e-12f )
					goto default;

				var x = direction.Normal;
				var y = toItem.Cross( x );
				if ( y.LengthSquared < 1e-12f )
					return;
				y = y.Normal;
				fade *= ViewFade( look.FadeMode, x, y, toItem );
				AddQuad( quads, item.Sprite, item, x, y, item.Rotation, scale, mirror, fade, distance, order * 2 );
				break;
			}

			case PerpendicularToDirection:
			{
				var direction = item.Direction;
				if ( direction.LengthSquared < 1e-12f )
					goto default;

				var up = direction.Normal;
				var reference = up.z * up.z > 0.99f ? Vector3.Left : Vector3.Up;
				var x = direction.Cross( reference ).Normal;
				var y = x.Cross( up );
				fade *= ViewFade( look.FadeMode, x, y, toItem );
				AddQuad( quads, item.Sprite, item, x, y, item.Rotation, scale, mirror, fade, distance, order * 2 );
				break;
			}

			case Rotational:
				BuildRotational( ref item, order, view, toItem, scale, fade, distance, quads );
				break;

			default:
				AddQuad( quads, item.Sprite, item, view.Right, view.Up, item.Rotation, scale, mirror, fade, distance, order * 2 );
				break;
		}
	}

	// Halo's view angle fade for sprites that aren't screen facing.
	static float ViewFade( int mode, Vector3 x, Vector3 y, Vector3 toItem )
	{
		if ( mode == 0 )
			return 1;

		var normal = x.Cross( y );
		var length = toItem.Length * normal.Length;
		var fade = length > 1e-6f ? MathF.Abs( normal.Dot( toItem ) ) / length : 1;
		return mode == 2 ? 1 - fade : fade;
	}

	// A rotational particle: its face on sprite picked by its spin, blended into the edge on sequence by the view angle.
	void BuildRotational( ref DrawItem item, int order, in View view, Vector3 toItem, float scale, float fade, float distance, Quads quads )
	{
		var axis = item.Direction;
		float angle = 0;
		if ( axis.LengthSquared > 1e-12f )
			angle = MathF.Acos( Math.Clamp( toItem.Normal.Dot( axis.Normal ), -1, 1 ) ) - MathF.PI / 2;
		var fraction = Math.Clamp( angle * angle / (MathF.PI / 2 * (MathF.PI / 2)), 0, 1 );
		var turn = item.Rotation / (MathF.PI * 2);

		if ( fraction > 0.05f && item.Edge is { Length: > 0 } edge )
		{
			int index;
			float spriteRotation;
			byte flags = 0;
			if ( (item.Flags & Sideways) != 0 )
			{
				index = (int)(Wrap( edge.Length * turn + 0.5f, edge.Length ) + item.SpriteIndex);
				spriteRotation = 0;
				if ( angle < 0 )
					index = edge.Length - item.SpriteIndex;
			}
			else
			{
				index = item.SpriteIndex;
				spriteRotation = item.Rotation;
				if ( angle < 0 )
					flags = DrawMirrorU;
			}

			if ( index >= 0 && index < edge.Length )
				AddQuad( quads, edge[index], item, view.Right, view.Up, spriteRotation, scale, flags, fraction * fade, distance, order * 2 );
		}

		if ( 1 - fraction > 0.05f && item.Face is { Length: > 0 } face )
		{
			var index = (int)Wrap( face.Length * turn + 0.5f, face.Length );
			var roll = MathF.Atan2( axis.Dot( view.Up ), axis.Dot( view.Right ) );
			if ( index >= 0 && index < face.Length )
				AddQuad( quads, face[index], item, view.Right, view.Up, roll, scale, 0, (1 - fraction) * fade, distance, order * 2 + 1 );
		}
	}

	static float Wrap( float value, float length )
	{
		var r = value % length;
		return r < 0 ? r + length : r;
	}

	// A sprite's quad on axes x and y, around its registration point, turned, mirrored and colored as Halo does.
	static void AddQuad( Quads quads, HaloSprite sprite, in DrawItem item, Vector3 x, Vector3 y, float roll, float scale, byte mirror, float fade, float distance, int order )
	{
		if ( sprite?.Texture is not { } texture || fade <= 0 )
			return;

		// Its index can change as the texture loads, so it's looked up (and streaming told it's used) once a frame.
		if ( sprite.DrawFrame != _drawFrame )
		{
			sprite.DrawFrame = _drawFrame;
			sprite.Index = texture.IsValid() && !texture.IsError ? texture.Index : -1;
			if ( sprite.Index >= 0 )
				texture.MarkUsed( 65535 );
		}

		if ( sprite.Index < 0 )
			return;

		var look = item.Look;
		var alpha = Math.Clamp( (look.UsesColorAlpha ? item.Color.w : 1) * fade, 0, 1 );
		if ( alpha <= 0 && look.Blend != FramebufferBlendFunction.Multiply && look.Blend != FramebufferBlendFunction.DoubleMultiply )
			return;

		var sine = roll != 0 ? MathF.Sin( roll ) : 0;
		var cosine = roll != 0 ? MathF.Cos( roll ) : 1;
		var left = -sprite.Origin.x;
		var right = sprite.Size.x - sprite.Origin.x;
		var top = sprite.Origin.y;
		var bottom = sprite.Origin.y - sprite.Size.y;
		var color = new Vector4( Math.Clamp( item.Color.x, 0, 1 ), Math.Clamp( item.Color.y, 0, 1 ), Math.Clamp( item.Color.z, 0, 1 ), alpha );
		var at = quads.Add( distance, order, look.Material, look.Blend );
		var corners = quads.Corners;
		var index = sprite.Index;
		var flipU = (mirror & DrawMirrorU) != 0 ? -1 : 1;
		var flipV = (mirror & DrawMirrorV) != 0 ? -1 : 1;
		for ( var c = 0; c < 4; c++ )
		{
			var u = c is 1 or 2 ? 1 : 0;
			var v = c >= 2 ? 1 : 0;
			var cx = u == 0 ? left : right;
			var cy = v == 0 ? top : bottom;
			var rx = (cx * cosine - cy * sine) * flipU;
			var ry = (cy * cosine + cx * sine) * flipV;

			ref var corner = ref corners[at + c];
			corner.Position = item.Position + (x * rx + y * ry) * scale;
			// Alpha 255: the corner is placed here (see halo1_sprite.shader).
			corner.Color = new Color32( 0, 0, 0, 255 );
			corner.Normal = default;
			corner.TexCoord0 = new Vector4( u, v, index, 0 );
			corner.TexCoord1 = color;
			corner.Tangent = new Vector4( item.ZReach, 0, 0, 0 );
		}
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

	// The frame behind the sprites, for halo1_sprite.shader's gamma space blend.

	static readonly object GrabLock = new();
	static Texture _behind;
	static (Vector3 Position, Rotation Rotation, Rect Viewport, int Frame) _behindView;
	static readonly List<Rect?> _drawnSinceGrab = new();
	static int _grabsThisView;
	static bool _copyFailed;

	// Most frame grabs in one view: past these, runs over others reuse the last grab.
	const int MaxGrabsPerView = 6;

	void Behind( Rect? rect )
	{
		lock ( GrabLock )
		{
			var view = (Graphics.CameraPosition, Graphics.CameraRotation, Graphics.Viewport, _frame);
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
						_behind = Texture.CreateRenderTarget( "halo particles behind", frame.ImageFormat, frame.Size, _behind );
						Graphics.CopyTexture( frame, _behind );
					}
					catch ( Exception e )
					{
						_copyFailed = true;
						_behind = null;
						HaloCeMount.Current?.Logger.Warning( $"Couldn't keep the frame behind Halo particles: {e.Message}" );
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
