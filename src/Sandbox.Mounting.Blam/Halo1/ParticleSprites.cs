using System.Reflection;
using Sandbox.Internal;

namespace Sandbox.Mounting.Blam.Halo1;

/// <summary>
/// Draws the sprites of Halo particles a game spawns (effect prefabs, attachments prefabs) the way
/// Halo blended them, with halo1_sprite.shader: in gamma space against the frame behind them, with
/// nonlinear tints and zsprite depth (see the shader). s&amp;box's sprite renderer draws every sprite
/// with its own material, so it can do none of that.
/// </summary>
/// <remarks>
/// <para>The effect builder keeps a <see cref="ParticleSpriteRenderer"/> on each particle object (so
/// the prefab still draws, the engine's way, wherever this isn't running), tags the object
/// <see cref="Tag"/> and gives it the particle's <see cref="ParticleSpriteMaterial"/> as the material
/// override of a disabled <see cref="ParticleModelRenderer"/>, which only carries it. Each frame this
/// turns those sprite renderers off and draws their particles itself. Halo CE's are drawn in Halo's
/// order, one scene object for all of them (split by tags and render options): every particle
/// tag's particles, a tag at a time, then every particle system's, each back to front, whatever
/// their distance. Other sprites (Halo 2's) are one scene object per root object (an effect, or
/// whatever the game parents it to), so the translucent sort places a whole effect, and its
/// particles are drawn back to front within it, all its particle objects together.</para>
/// <para>Each particle is drawn as the sprite renderer would: its sprite's animation frame, size
/// (its height the particle's size), registration point, roll, and for "face velocity" a streak
/// along its velocity. A renderer aligned to its object is Halo's "perpendicular to direction":
/// each sprite lies flat across its particle's velocity, or across the object's forward while the
/// particle is still. Its color is the particle's (so tints set on the effect apply). The frame is the particle's own, from its
/// age at the renderer's playback speed, rather than one frame for every particle of the renderer;
/// particles of objects tagged <see cref="RandomFrameTag"/> start on a random frame of a looping
/// animation, as Halo's rotational sprites and randomly started particles do, and those of objects
/// tagged <see cref="RandomSequenceTag"/> each on a random one of the sprite's animations, as Halo
/// starts a particle on a random initial sequence.</para>
/// <para>Like <see cref="MovingObjectLights"/>, this can't be saved in a scene, so <see cref="Watch"/>
/// adds it to every live scene with tagged particles, on a hidden object that isn't saved. Turned
/// off, it gives the sprite renderers back.</para>
/// </remarks>
sealed class ParticleSprites : Component, Component.ExecuteInEditor, IPreRenderSubscriber
{
	/// <summary>
	/// Particle objects drawn here.
	/// </summary>
	public const string Tag = "halo_sprite";

	/// <summary>
	/// Particles that start on a random frame of their looping animation.
	/// </summary>
	public const string RandomFrameTag = "halo_sprite_random_frame";

	/// <summary>
	/// Particles that start on a random one of their sprite's animations: Halo's initial sequences
	/// (see <see cref="Sandbox.Mounting.Blam.Halo2.Halo2ParticleSpriteLoader"/>).
	/// </summary>
	public const string RandomSequenceTag = "halo_sprite_random_sequence";

	/// <summary>
	/// The grabbed frame behind the sprites (halo1_sprite.shader's g_tHaloSpriteBehind).
	/// </summary>
	const string BehindAttribute = "HaloSpriteBehind";

	/// <summary>
	/// 1 for sprites drawn only in s&amp;box's overlay layer (halo1_sprite.shader's g_flHaloOverlayOnly),
	/// which the engine also draws in the world's layers.
	/// </summary>
	const string OverlayOnlyAttribute = "HaloOverlayOnly";

	/// <summary>
	/// Sets a particle object up to be drawn here: <paramref name="materialPath"/> is its
	/// <see cref="ParticleSpriteMaterial"/>. Nothing changes if the material can't be loaded.
	/// </summary>
	public static void Attach( GameObject go, string materialPath, bool randomFrame, bool randomSequence = false )
	{
		// The mount the material is registered with (the Halo 2 mount's particles use this too).
		var ident = materialPath.StartsWith( "mount://" ) && materialPath.IndexOf( '/', 8 ) is var slash and > 8 ? materialPath[8..slash] : EffectPrefabs.Ident;
		if ( Sandbox.Mounting.Directory.Get( ident )?.GetByPath( materialPath ) is null )
			return;

		var material = Material.Load( materialPath );
		if ( material is null || material.IsError )
			return;

		go.Tags.Add( Tag );
		if ( randomFrame )
			go.Tags.Add( RandomFrameTag );
		if ( randomSequence )
			go.Tags.Add( RandomSequenceTag );

		var carrier = go.AddComponent<ParticleModelRenderer>( false );
		carrier.Choices = [];
		carrier.MaterialOverride = material;
		carrier.CastShadows = false;
		Watch();
	}

	sealed class Entry
	{
		public ParticleEffect Effect;
		public ParticleSpriteRenderer Renderer;
		public Material Material;
		public bool RandomFrame;
		public bool RandomSequence;
		public bool TurnedOff;
		public Group Group;
		public int Frame;

		// Where Halo CE draws it (see Stage), and where its particles are this frame (see Update).
		public int Stage;
		public BBox Bounds;

		// The tags that pick its group (see Signature), and when they were last read.
		public int Signature;
		public int SignatureFrame = int.MinValue;

		// The animations last looked up (the renderer's, or all of a random sequence sprite's), and
		// their frames' bindless indices and sizes. The indices are looked up again every frame (see Indices).
		public Sprite Sprite;
		public string AnimationName;
		public Clip[] Clips;
		public int IndicesFrame = int.MinValue;

		// Streaks and sprites lying across their direction: each particle's remembered direction
		// (see Directions), and the effect's callbacks that keep it.
		public bool Directed;
		public bool Attached;
		public bool Stepping;
		public List<Particle> Born;
		public Action<Particle> OnCreated;
		public Action<float> OnPreStep;
		public Action<float> OnPostStep;
	}

	/// <summary>
	/// One animation of a particle object's sprite, with its frames' textures.
	/// </summary>
	sealed class Clip
	{
		public Sprite.Animation Animation;
		public int[] Textures;
		public Texture[] TextureObjects;
		public Vector2[] Sizes;
	}

	/// <summary>
	/// Particle objects under one root with the same tags (other than the effect builder's own) and
	/// render options are drawn together: cameras pick what they draw by tags, and a game may tag a
	/// first person part of an object differently. Halo CE's have no root: all those with the same
	/// tags and render options are drawn together, in Halo's order.
	/// </summary>
	readonly record struct GroupKey( GameObject Root, int Signature );

	/// <summary>
	/// Halo draws every particle, grouped by particle tag, then every particle system, whatever
	/// their distance: an <see cref="Entry.Stage"/> of 0 and 1. Sprites that aren't Halo CE's are -1,
	/// drawn by their root's distance.
	/// </summary>
	static int Stage( Material material )
	{
		var path = material.ResourcePath ?? "";
		if ( !path.StartsWith( $"mount://{EffectPrefabs.Ident}/", StringComparison.OrdinalIgnoreCase ) )
			return -1;

		return path.EndsWith( "__sprite.vmat", StringComparison.OrdinalIgnoreCase ) ? 0 : 1;
	}

	sealed class Group
	{
		public GroupKey Key;
		public SceneCustomObject Object;
		public readonly List<Entry> Entries = new();

		/// <summary>
		/// Its render options are the overlay's alone (see <see cref="OverlayOnlyAttribute"/>).
		/// </summary>
		public bool OverlayOnly;
	}

	readonly Dictionary<ParticleEffect, Entry> _entries = new();
	readonly Dictionary<GroupKey, Group> _groups = new();
	readonly HashSet<ParticleEffect> _ignored = new();
	readonly List<ParticleEffect> _gone = new();
	readonly List<GroupKey> _emptyGroups = new();
	int _frame;

	protected override void OnDisabled()
	{
		foreach ( var entry in _entries.Values )
			Release( entry );
		foreach ( var group in _groups.Values )
			group.Object?.Delete();

		_entries.Clear();
		_groups.Clear();
		_ignored.Clear();
	}

	// IPreRenderSubscriber is what gets OnPreRender called (see MirrorReflections).
	protected override void OnPreRender()
	{
		_frame++;
		System.Threading.Interlocked.Increment( ref _renderFrame );

		foreach ( var effect in Scene.GetAllComponents<ParticleEffect>() )
		{
			if ( !effect.IsValid() || !effect.Active || _ignored.Contains( effect ) )
				continue;

			if ( !_entries.TryGetValue( effect, out var entry ) )
			{
				if ( !effect.GameObject.Tags.Has( Tag ) || Create( effect ) is not { } created )
				{
					_ignored.Add( effect );
					continue;
				}

				_entries[effect] = entry = created;
			}

			entry.Frame = _frame;
			if ( entry.Renderer.Enabled )
			{
				entry.Renderer.Enabled = false;
				entry.TurnedOff = true;
			}

			// An effect moved to another root (a game reparenting it) or tagged differently changes group.
			if ( _frame - entry.SignatureFrame >= SignatureInterval )
			{
				entry.Signature = Signature( entry );
				entry.SignatureFrame = _frame;
			}

			var key = new GroupKey( entry.Stage >= 0 ? null : effect.GameObject.Root ?? effect.GameObject, entry.Signature );
			if ( entry.Group?.Key != key )
			{
				entry.Group?.Entries.Remove( entry );
				entry.Group = GroupFor( key );
				entry.Group.Entries.Add( entry );
			}
		}

		foreach ( var (effect, entry) in _entries )
		{
			if ( entry.Frame != _frame )
				_gone.Add( effect );
		}

		foreach ( var effect in _gone )
		{
			var entry = _entries[effect];
			Release( entry );
			entry.Group?.Entries.Remove( entry );
			_entries.Remove( effect );
		}
		_gone.Clear();
		_ignored.RemoveWhere( e => !e.IsValid() );

		foreach ( var (key, group) in _groups )
		{
			if ( group.Entries.Count == 0 || key.Root is not null && !key.Root.IsValid() )
			{
				group.Object?.Delete();
				_emptyGroups.Add( key );
				continue;
			}

			Update( group );
		}

		foreach ( var key in _emptyGroups )
			_groups.Remove( key );
		_emptyGroups.Clear();
	}

	/// <summary>
	/// Frames between looking at a particle object's tags again.
	/// </summary>
	const int SignatureInterval = 15;

	/// <summary>
	/// Tags the effect builder and the sprite renderer give particle objects, which don't split groups.
	/// </summary>
	static readonly HashSet<string> OwnTags =
	[
		Tag, RandomFrameTag, RandomSequenceTag, "particles", "halo_effect_location",
	];

	/// <summary>
	/// The particle object's other tags (its own and its parents') and its sprite renderer's render
	/// options, as one number.
	/// </summary>
	static int Signature( Entry entry )
	{
		var signature = 0;
		foreach ( var tag in entry.Effect.GameObject.Tags.TryGetAll() )
		{
			if ( !OwnTags.Contains( tag ) )
				signature += StringComparer.Ordinal.GetHashCode( tag ) * 31 + 7;
		}

		var options = entry.Renderer.RenderOptions;
		return HashCode.Combine( signature, options.Game, options.Overlay, options.Bloom, options.AfterUI );
	}

	static Entry Create( ParticleEffect effect )
	{
		// Enabled or not: the sprite renderer is on until it's turned off here, the carrier always off.
		var renderer = effect.GameObject.Components.Get<ParticleSpriteRenderer>( FindMode.EverythingInSelf );
		var carrier = effect.GameObject.Components.Get<ParticleModelRenderer>( FindMode.EverythingInSelf );
		if ( renderer is null || carrier?.MaterialOverride is not { IsError: false } material )
			return null;

		var entry = new Entry
		{
			Effect = effect,
			Renderer = renderer,
			Material = material,
			RandomFrame = effect.GameObject.Tags.Has( RandomFrameTag ),
			RandomSequence = effect.GameObject.Tags.Has( RandomSequenceTag ),
			Stage = Stage( material ),
		};

		if ( renderer.FaceVelocity || renderer.Alignment == ParticleSpriteRenderer.BillboardAlignment.Object )
			Directions( entry );

		return entry;
	}

	/// <summary>
	/// Halo turns a streak along its particle's direction, and lays a "perpendicular to direction"
	/// sprite across it: the direction it was emitted in, then its velocity's while it moves at
	/// least <see cref="TurningSpeed"/>, and the last of those once it's slower. Muzzle flash spikes
	/// are emitted barely moving and stop at once, and still point out from the barrel. Kept per
	/// particle, from its velocity before the effect's first step moves it (the emitter sets it
	/// after the particle is made), and in the effect's space for particles that stay attached to
	/// it, so they turn with the weapon.
	/// </summary>
	static void Directions( Entry entry )
	{
		var effect = entry.Effect;
		entry.Directed = true;
		entry.Attached = effect.LocalSpace.Evaluate( 1, 0 ) >= 0.999f;
		entry.Born = new List<Particle>();

		// Made outside a step: the emitter sets its velocity afterwards, so it's read when the next
		// step starts. Made in a step (delayed particles), it already has it.
		entry.OnCreated = p =>
		{
			if ( entry.Stepping )
				Remember( entry, p, p.Velocity );
			else
				entry.Born.Add( p );
		};
		entry.OnPreStep = _ =>
		{
			entry.Stepping = true;
			foreach ( var p in entry.Born )
				Remember( entry, p, p.Velocity );
			entry.Born.Clear();
		};
		entry.OnPostStep = _ =>
		{
			entry.Stepping = false;
			var particles = entry.Effect.Particles;
			for ( var i = 0; i < particles.Count; i++ )
			{
				var p = particles[i];
				if ( p is not null && p.Velocity.LengthSquared >= TurningSpeed * TurningSpeed )
					Remember( entry, p, p.Velocity );
			}
		};

		effect.OnParticleCreated += entry.OnCreated;
		effect.OnPreStep += entry.OnPreStep;
		effect.OnPostStep += entry.OnPostStep;
	}

	/// <summary>
	/// Slower than this (s&amp;box units per second, a quarter of a world unit) a particle keeps the
	/// direction it had.
	/// </summary>
	const float TurningSpeed = 0.25f * HaloCeMount.WorldScale;

	const string DirectionKey = "halo_direction";

	/// <summary>
	/// A particle's remembered direction, changed in place so it isn't boxed again every step.
	/// </summary>
	sealed class Direction
	{
		public Vector3 Value;
	}

	static void Remember( Entry entry, Particle p, Vector3 velocity )
	{
		if ( velocity.LengthSquared < 1e-12f )
			return;

		var direction = velocity.Normal;
		if ( entry.Attached )
			direction = entry.Effect.WorldRotation.Inverse * direction;

		if ( p.Get<Direction>( DirectionKey ) is { } kept )
			kept.Value = direction;
		else
			p.Set( DirectionKey, new Direction { Value = direction } );
	}

	/// <summary>
	/// The direction a particle is drawn along (see <see cref="Directions"/>): its velocity's while
	/// fast enough, else the one it remembers, else its emitter's forward (exact for blocks without
	/// a cone, which a particle made before this saw its effect falls back to).
	/// </summary>
	static Vector3 DirectionOf( Entry entry, Particle p, Vector3 forward )
	{
		if ( p.Velocity.LengthSquared >= TurningSpeed * TurningSpeed )
			return p.Velocity;

		if ( p.Get<Direction>( DirectionKey ) is { } kept )
			return entry.Attached ? entry.Effect.WorldRotation * kept.Value : kept.Value;

		return forward;
	}

	Group GroupFor( GroupKey key )
	{
		if ( _groups.TryGetValue( key, out var group ) )
			return group;

		group = new Group { Key = key };
		group.Object = new SceneCustomObject( Scene.SceneWorld ) { RenderOverride = _ => Render( group ) };
		group.Object.Flags.IsOpaque = false;
		group.Object.Flags.IsTranslucent = true;
		group.Object.Flags.CastShadows = false;
		_groups[key] = group;
		return group;
	}

	/// <summary>
	/// The group's bounds (where its effects' particles can be, as the engine bounds them), tags and
	/// render options (its first particle object's, as its sprite renderer would have them).
	/// </summary>
	static void Update( Group group )
	{
		var first = group.Entries[0];
		var so = group.Object;
		so.Tags.SetFrom( first.Effect.GameObject.Tags );
		var options = first.Renderer.RenderOptions;
		options.Apply( so );

		// s&box draws an overlay object in the world's layers too (so a view model's depth is
		// prepassed); the shader leaves sprites only for the overlay out of the world's.
		group.OverlayOnly = options.Overlay && !options.Game;

		BBox? bounds = null;
		var any = false;
		foreach ( var entry in group.Entries )
		{
			var effect = entry.Effect;
			if ( !effect.IsValid() || effect.Particles.Count == 0 )
				continue;

			var box = effect.ParticleBounds.Grow( effect.MaxParticleSize * MathF.Max( entry.Renderer.Scale, 1 ) * MathF.Abs( entry.Renderer.WorldScale.x ) );
			entry.Bounds = box;
			bounds = bounds is { } b ? b.AddBBox( box ) : box;
			any = true;
		}

		so.RenderingEnabled = any;
		if ( bounds is { } world )
			so.Bounds = world;
	}

	/// <summary>
	/// Gives an effect's sprite renderer back, if it was turned off here.
	/// </summary>
	static void Release( Entry entry )
	{
		if ( entry.TurnedOff && entry.Renderer.IsValid() )
			entry.Renderer.Enabled = true;
		entry.TurnedOff = false;

		if ( entry.Directed && entry.Effect.IsValid() )
		{
			entry.Effect.OnParticleCreated -= entry.OnCreated;
			entry.Effect.OnPreStep -= entry.OnPreStep;
			entry.Effect.OnPostStep -= entry.OnPostStep;
		}
		entry.Directed = false;
	}

	/// <summary>
	/// A particle ready to draw.
	/// </summary>
	struct Item
	{
		public int Stage;
		public float Distance;
		public Material Material;
		public Entry Entry;
		public Vertex Vertex;
	}

	/// <summary>
	/// Halo's draw order (see <see cref="Stage"/>): particles before particle systems, particles a
	/// tag at a time (by material, one per particle tag), each run back to front.
	/// </summary>
	static int DrawOrder( Item a, Item b )
	{
		if ( a.Stage != b.Stage )
			return a.Stage.CompareTo( b.Stage );

		if ( a.Stage == 0 && a.Material != b.Material )
		{
			var byTag = string.CompareOrdinal( a.Material.ResourcePath, b.Material.ResourcePath );
			if ( byTag != 0 )
				return byTag;
		}

		return b.Distance.CompareTo( a.Distance );
	}

	[ThreadStatic] static List<Item> _items;
	[ThreadStatic] static Vertex[] _vertices;
	[ThreadStatic] static ushort[] _indices;

	/// <summary>
	/// Most particles in one draw (four vertices each, indexed by 16 bits).
	/// </summary>
	const int MaxParticlesPerDraw = 16384;

	static readonly Vector2[] Corners = [new( 0, 0 ), new( 1, 0 ), new( 1, 1 ), new( 0, 1 )];

	/// <summary>
	/// Draws a group: its particles in order (see <see cref="DrawOrder"/>), a draw for each run
	/// sharing a material, over the frame grabbed behind them (for Halo CE's, behind each run).
	/// </summary>
	static void Render( Group group )
	{
		var items = _items ??= new List<Item>();
		items.Clear();

		var camera = Graphics.CameraPosition;
		foreach ( var entry in group.Entries )
		{
			try
			{
				Collect( entry, camera, items );
			}
			catch ( Exception e )
			{
				HaloCeMount.Current?.Logger.Warning( $"Couldn't draw {entry.Effect?.GameObject?.Name}: {e.Message}" );
			}
		}

		if ( items.Count == 0 )
			return;

		var ordered = group.Key.Root is null;
		items.Sort( DrawOrder );
		if ( !ordered )
			Behind( ScreenRect( group.Object.Bounds ) );

		var vertices = _vertices ??= new Vertex[MaxParticlesPerDraw * 4];
		var indices = _indices ??= MakeIndices();
		for ( var start = 0; start < items.Count; )
		{
			var material = items[start].Material;
			var end = start;
			while ( end < items.Count && end - start < MaxParticlesPerDraw && items[end].Material == material )
				end++;

			if ( ordered )
				Behind( RunRect( items, start, end ) );

			var count = 0;
			for ( var i = start; i < end; i++ )
			{
				var vertex = items[i].Vertex;
				foreach ( var corner in Corners )
				{
					vertex.TexCoord0 = new Vector4( corner.x, corner.y, items[i].Vertex.TexCoord0.z, items[i].Vertex.TexCoord0.w );
					vertices[count++] = vertex;
				}
			}

			Graphics.Attributes.Set( OverlayOnlyAttribute, group.OverlayOnly ? 1f : 0f );
			Graphics.Draw( vertices.AsSpan(), count, indices.AsSpan(), count / 4 * 6, material );
			start = end;
		}
	}

	// The frame behind the sprites, grabbed once per view and kept for the groups after (each
	// group draws in a render block of its own, which lets go of what it grabbed).

	static readonly object GrabLock = new();
	static int _renderFrame;
	static Texture _behind;
	static (int Frame, Vector3 Position, Rotation Rotation, Rect Viewport) _behindView;
	static readonly List<Rect?> _drawnSinceGrab = new();
	static int _grabsThisView;
	static bool _copyFailed;

	/// <summary>
	/// Most frame grabs in one view: past these, groups over others use the last grab (a little
	/// off in how much they brighten or cover, see halo1_sprite.shader, but not one full screen
	/// copy per puff of a vehicle's dust trail).
	/// </summary>
	const int MaxGrabsPerView = 4;

	/// <summary>
	/// Binds the frame behind a group (or a run of one) about to draw over <paramref name="rect"/> (see
	/// <see cref="ScreenRect"/>). The frame is grabbed for a view's first group, and again for a
	/// group over a place a group drew since the last grab (up to <see cref="MaxGrabsPerView"/>),
	/// so effects over effects blend against them; elsewhere the last grab is used again.
	/// </summary>
	static void Behind( Rect? rect )
	{
		lock ( GrabLock )
		{
			var view = (_renderFrame, Graphics.CameraPosition, Graphics.CameraRotation, Graphics.Viewport);
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
						_behind = Texture.CreateRenderTarget( "halo sprite behind", frame.ImageFormat, frame.Size, _behind );
						Graphics.CopyTexture( frame, _behind );
					}
					catch ( Exception e )
					{
						// Then every group grabs its own, as it would without this.
						_copyFailed = true;
						_behind = null;
						HaloCeMount.Current?.Logger.Warning( $"Couldn't keep the frame behind Halo sprites: {e.Message}" );
					}
				}
			}

			_drawnSinceGrab.Add( rect );
		}
	}

	/// <summary>
	/// Where a box is on screen, as the tangents of its angles right and up from the view direction
	/// (enough to tell whether two overlap), or null if it reaches behind the camera.
	/// </summary>
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

	/// <summary>
	/// Where a run of items is on screen (see <see cref="ScreenRect"/>): its particle objects' bounds.
	/// </summary>
	static Rect? RunRect( List<Item> items, int start, int end )
	{
		var bounds = items[start].Entry.Bounds;
		var last = items[start].Entry;
		for ( var i = start + 1; i < end; i++ )
		{
			if ( items[i].Entry == last )
				continue;

			last = items[i].Entry;
			bounds = bounds.AddBBox( last.Bounds );
		}

		return ScreenRect( bounds );
	}

	static bool Overlap( Rect? a, Rect? b ) =>
		a is not { } x || b is not { } y || (x.Left < y.Right && y.Left < x.Right && x.Top < y.Bottom && y.Top < x.Bottom);

	static ushort[] MakeIndices()
	{
		var indices = new ushort[MaxParticlesPerDraw * 6];
		for ( var i = 0; i < MaxParticlesPerDraw; i++ )
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

	/// <summary>
	/// An effect's particles as halo1_sprite.shader's vertices (one per particle; the corners are
	/// filled in when drawn).
	/// </summary>
	static void Collect( Entry entry, Vector3 camera, List<Item> items )
	{
		var effect = entry.Effect;
		var renderer = entry.Renderer;
		if ( !effect.IsValid() || !renderer.IsValid() || !effect.Active || renderer.Sprite is not { } sprite )
			return;

		var clips = Clips( entry, sprite, renderer.StartingAnimationName );
		if ( clips.Length == 0 )
			return;

		Indices( entry );

		var particles = effect.Particles;
		var scale = renderer.Scale * MathF.Abs( renderer.WorldScale.x );
		var streak = renderer.FaceVelocity;
		var perpendicular = !streak && renderer.Alignment == ParticleSpriteRenderer.BillboardAlignment.Object;
		var forward = effect.WorldRotation.Forward;

		for ( var i = 0; i < particles.Count; i++ )
		{
			var p = particles[i];
			if ( p is null )
				continue;

			var clip = clips[clips.Length == 1 ? 0 : Math.Min( (int)(p.Rand( 5381 ) * clips.Length), clips.Length - 1 )];
			var animation = clip.Animation;
			var frames = clip.Textures.Length;
			if ( frames == 0 )
				continue;

			// A random start only for animations that loop all their frames (not an initial sequence
			// followed by a looping one), or still ones, whose particles each keep a random frame.
			var speed = renderer.PlaybackSpeed * animation.FrameRate;
			var randomStart = entry.RandomFrame && (speed <= 0 || (animation.LoopMode == Sprite.LoopMode.Loop && animation.EffectiveLoopStart == 0));
			var frame = FrameAt( animation, frames, MathF.Max( p.Age * speed, 0 ) + (randomStart ? p.Rand( 7253 ) * frames : 0) );
			if ( clip.Textures[frame] < 0 )
				continue;

			// Sampled bindless, so texture streaming has to be told.
			clip.TextureObjects[frame].MarkUsed( 65535 );

			var origin = animation.Origin;
			var size = clip.Sizes[frame];
			var height = p.Size.y * scale;
			var width = p.Size.x * scale * (size.y > 0 ? size.x / size.y : 1);
			var alpha = p.Color.a * p.Alpha;
			if ( alpha <= 0 || height <= 0 )
				continue;

			// Streaks and perpendicular sprites go by the particle's direction (see Directions).
			var direction = streak || perpendicular ? DirectionOf( entry, p, forward ) : p.Velocity;

			items.Add( new Item
			{
				Stage = entry.Stage,
				Distance = p.Position.DistanceSquared( camera ),
				Material = entry.Material,
				Entry = entry,
				Vertex = new Vertex
				{
					Position = p.Position,
					Color = new Color32( streak ? (byte)255 : (byte)0, perpendicular ? (byte)255 : (byte)0, (byte)(p.Rand( 3907 ) * 255), 0 ),
					Normal = direction,
					TexCoord0 = new Vector4( 0, 0, clip.Textures[frame], p.Angles.roll.DegreeToRadian() ),
					TexCoord1 = new Vector4( p.Color.r, p.Color.g, p.Color.b, alpha ),
					Tangent = new Vector4( width, height, origin.x, origin.y ),
				},
			} );
		}
	}

	/// <summary>
	/// The frame <paramref name="time"/> frames into an animation, as the sprite renderer plays it:
	/// holding the last frame, or looping back to its loop start.
	/// </summary>
	static int FrameAt( Sprite.Animation animation, int frames, float time )
	{
		if ( frames <= 1 || !(time > 0) )
			return 0;

		var n = (int)MathF.Min( time, 1e6f );
		if ( n < frames )
			return n;
		if ( animation.LoopMode == Sprite.LoopMode.None )
			return frames - 1;

		var loopStart = Math.Clamp( animation.EffectiveLoopStart, 0, frames - 1 );
		var loopEnd = Math.Clamp( animation.EffectiveLoopEnd, loopStart, frames - 1 );
		return loopStart + (n - loopStart) % (loopEnd - loopStart + 1);
	}

	/// <summary>
	/// The frames' bindless indices, once a frame. A texture's index isn't fixed: a texture made
	/// while its pixels load is replaced by the loaded one, and the old index then names whatever
	/// texture takes its place (plasma impacts came out as rainbow blobs).
	/// </summary>
	static void Indices( Entry entry )
	{
		var frame = _renderFrame;
		if ( entry.IndicesFrame == frame )
			return;

		entry.IndicesFrame = frame;
		foreach ( var clip in entry.Clips )
		{
			for ( var i = 0; i < clip.Textures.Length; i++ )
			{
				var texture = clip.TextureObjects[i];
				var valid = texture is not null && texture.IsValid() && !texture.IsError;
				clip.Textures[i] = valid ? texture.Index : -1;
				if ( valid )
					clip.Sizes[i] = new Vector2( texture.Width, texture.Height );
			}
		}
	}

	/// <summary>
	/// The animations a particle object's particles are drawn with, their frames' textures looked
	/// up once: the renderer's, or every one of a random sequence sprite's.
	/// </summary>
	static Clip[] Clips( Entry entry, Sprite sprite, string name )
	{
		if ( entry.Sprite == sprite && entry.AnimationName == name && entry.Clips is not null )
			return entry.Clips;

		var all = sprite.Animations ?? [];
		IEnumerable<Sprite.Animation> animations = all.Count == 0 ? []
			: entry.RandomSequence ? all
			: [all[Math.Clamp( string.IsNullOrEmpty( name ) ? 0 : sprite.GetAnimationIndex( name ), 0, all.Count - 1 )]];

		entry.Sprite = sprite;
		entry.AnimationName = name;
		entry.IndicesFrame = int.MinValue;
		entry.Clips = animations.Select( ClipOf ).ToArray();
		return entry.Clips;
	}

	static Clip ClipOf( Sprite.Animation animation )
	{
		var frames = animation.Frames ?? [];
		var clip = new Clip
		{
			Animation = animation,
			Textures = new int[frames.Count],
			TextureObjects = new Texture[frames.Count],
			Sizes = new Vector2[frames.Count],
		};

		for ( var i = 0; i < frames.Count; i++ )
		{
			var texture = frames[i]?.Texture;
			var valid = texture is not null && texture.IsValid() && !texture.IsError;
			clip.Textures[i] = valid ? texture.Index : -1;
			clip.TextureObjects[i] = texture;
			clip.Sizes[i] = valid ? new Vector2( texture.Width, texture.Height ) : Vector2.One;
		}

		return clip;
	}

	// Watching for scenes with Halo sprites (see MirrorReflections).

	static readonly object WatchLock = new();
	static System.Threading.Timer _watchTimer;
	static int _scanQueued;

	const string ObjectName = "halo sprites";

	static readonly PropertyInfo AllScenesProperty = typeof( Scene ).GetProperty( "_all", BindingFlags.NonPublic | BindingFlags.Static );

	/// <summary>
	/// Starts looking for live scenes with Halo sprites, twice a second. Safe to call more than once.
	/// </summary>
	public static void Watch()
	{
		lock ( WatchLock )
		{
			_watchTimer ??= new System.Threading.Timer( _ => QueueScan(), null, 0, 500 );
		}
	}

	public static void StopWatching()
	{
		lock ( WatchLock )
		{
			_watchTimer?.Dispose();
			_watchTimer = null;
		}
	}

	static void QueueScan()
	{
		if ( System.Threading.Interlocked.Exchange( ref _scanQueued, 1 ) == 1 )
			return;

		MainThread.Queue( Scan );
	}

	static void Scan()
	{
		_scanQueued = 0;
		try
		{
			var scenes = AllScenesProperty?.GetValue( null ) is System.Collections.IEnumerable all ? all.OfType<Scene>().ToList() : Scene.All.ToList();
			foreach ( var scene in scenes )
			{
				// By name too: the Halo CE and Halo 2 mounts can run from two copies of this assembly, each with its own
				// ParticleSprites type, and one drawer per scene draws every Halo sprite.
				if ( !scene.IsValid() || scene is PrefabScene || scene.GetAllComponents<ParticleSprites>().Any()
					|| scene.Children.Any( g => g.Name == ObjectName ) || !scene.FindAllWithTag( Tag ).Any() )
					continue;

				AddTo( scene );
			}
		}
		catch ( Exception e )
		{
			HaloCeMount.Current?.Logger.Warning( e, $"Couldn't add Halo sprites: {e.Message}" );
		}
	}

	static void AddTo( Scene scene )
	{
		using ( scene.Push() )
		{
			var go = new GameObject( true, ObjectName );
			go.Flags |= GameObjectFlags.Hidden | GameObjectFlags.NotSaved | GameObjectFlags.NotNetworked;

			// As in MirrorReflections: the game's type library doesn't know mount types.
			go.Flags |= GameObjectFlags.Deserializing;
			var sprites = go.Components.Create<ParticleSprites>( false );
			go.Flags &= ~GameObjectFlags.Deserializing;
			sprites.Flags |= ComponentFlags.Hidden | ComponentFlags.NotSaved | ComponentFlags.NotNetworked;
			sprites.Enabled = true;
		}
	}
}
