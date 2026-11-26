using System.Reflection;
using Sandbox.Internal;
using Sandbox.Rendering;

namespace Sandbox.Mounting.Blam.Halo1;

/// <summary>
/// The planes of a level's dynamic mirror glass (b30's map room floors), each one mirror: panels
/// on the same plane share its reflection. A plane's two faces are one mirror, seen from
/// whichever side the camera is on.
/// </summary>
sealed class MirrorPlanes
{
	/// <summary>
	/// Tags of the mirror glass objects (and <c>halo_mirror_&lt;index&gt;</c> for their plane), which
	/// are left out of the mirrored views, and of the object marking each plane.
	/// </summary>
	public const string GlassTag = "halo_mirror";
	public const string PlaneTag = "halo_mirror_plane";

	/// <summary>
	/// On a plane's marker: the mirror is rendered whenever the camera is above it, wherever its glass's bounds
	/// are (Halo 2's open water, a plane out to the horizon whose bounds are moved for sorting).
	/// </summary>
	public const string UnboundedTag = "halo_mirror_unbounded";

	public static string PlaneIndexTag( int index ) => $"halo_mirror_{index}";

	/// <param name="Normal">Unit normal, its largest component positive.</param>
	/// <param name="Distance">Normal . point on the plane.</param>
	public sealed record Plane( Vector3 Normal, float Distance )
	{
		/// <summary>Its triangles' bounds.</summary>
		public BBox? Bounds { get; set; }
	}

	public List<Plane> Planes { get; } = new();

	/// <summary>
	/// The triangles grouped by the plane they lie on (its index in <see cref="Planes"/>), which
	/// this adds to.
	/// </summary>
	public Dictionary<int, List<int>> Split( IReadOnlyList<HaloVertex> vertices, IReadOnlyList<int> indices )
	{
		var groups = new Dictionary<int, List<int>>();
		var degenerate = new List<int>();
		for ( var i = 0; i + 2 < indices.Count; i += 3 )
		{
			var a = vertices[indices[i]].Position;
			var b = vertices[indices[i + 1]].Position;
			var c = vertices[indices[i + 2]].Position;
			var cross = Vector3.Cross( b - a, c - a );
			if ( cross.Length < 1e-4f )
			{
				degenerate.AddRange( [indices[i], indices[i + 1], indices[i + 2]] );
				continue;
			}

			var plane = Find( cross.Normal, a );
			plane.Bounds = (plane.Bounds ?? new BBox( a, a )).AddPoint( b ).AddPoint( c );

			var index = Planes.IndexOf( plane );
			if ( !groups.TryGetValue( index, out var group ) )
				groups[index] = group = new List<int>();
			group.AddRange( [indices[i], indices[i + 1], indices[i + 2]] );
		}

		if ( degenerate.Count > 0 && groups.Count > 0 )
			groups.First().Value.AddRange( degenerate );

		return groups;
	}

	Plane Find( Vector3 normal, Vector3 point )
	{
		// One side of the plane stands for both.
		var largest = MathF.Abs( normal.x ) >= MathF.Abs( normal.y ) && MathF.Abs( normal.x ) >= MathF.Abs( normal.z ) ? normal.x
			: MathF.Abs( normal.y ) >= MathF.Abs( normal.z ) ? normal.y : normal.z;
		if ( largest < 0 )
			normal = -normal;

		var distance = Vector3.Dot( normal, point );
		foreach ( var plane in Planes )
		{
			if ( Vector3.Dot( plane.Normal, normal ) > 0.999f && MathF.Abs( plane.Distance - distance ) < 1f )
				return plane;
		}

		var added = new Plane( normal, distance );
		Planes.Add( added );
		return added;
	}
}

/// <summary>
/// Renders the scene mirrored in each dynamic mirror plane (see <see cref="MirrorPlanes"/>) that
/// the camera can see, like Halo did for its mirror glass, and hands it to the plane's glass.
/// </summary>
/// <remarks>
/// A mounted scene is saved as a scene file, which can only hold s&amp;box's own components, so
/// this can't be part of it. <see cref="Watch"/> looks for live scenes with mirror planes in them
/// (editor scenes included) and adds it to each, on a hidden object that isn't saved.
///
/// Each frame before rendering it picks the camera (the scene's main camera, else the first
/// enabled one: the editor's), and for every mirror in its view and within
/// <see cref="MaximumDistance"/> renders that camera's view mirrored in the plane into a screen
/// sized texture: <see cref="CameraComponent.RenderToTexture(Texture, in ViewSetup)"/> with the camera's transform
/// mirrored, flipped so it lines up with the screen, an oblique projection clipping it at the
/// plane, and the mirror glass left out (what CommandList.DrawReflection does inside a view, but
/// that hands its target only to view attributes). The texture goes on the glass objects' own
/// attributes (HaloMirror, HaloHasMirror), which halo1_glass_water.shader's reflection pass reads,
/// so a second camera looking at the same mirror in the same frame sees the first one's.
/// Halo rendered one mirror, found through the visible clusters.
/// </remarks>
sealed class MirrorReflections : Component, Component.ExecuteInEditor, IPreRenderSubscriber
{
	/// <summary>
	/// Mirrors farther than this from the camera (to their bounds) aren't rendered.
	/// </summary>
	const float MaximumDistance = 4096;

	/// <summary>
	/// Unbounded mirrors (open water, mostly sky and distant scenery seen through bumps, which hide the blur) render
	/// at this fraction of the screen's size each way.
	/// </summary>
	const float UnboundedScale = 0.25f;

	/// <summary>
	/// How far an unbounded mirror's plane reaches for the visibility test (units from its marker).
	/// </summary>
	const float UnboundedExtent = 1_000_000;

	/// <summary>
	/// The mirrored view is clipped this far in front of the plane, so the floor around the glass
	/// (on the plane) isn't caught half in it.
	/// </summary>
	const float ClipOffset = 1;

	sealed class Mirror
	{
		public Vector3 Normal;
		public Vector3 Origin;
		public float Distance;
		public BBox Bounds;
		public List<ModelRenderer> Glass = new();
		public Texture Texture;
		public bool Shown;
		public bool Unbounded;
	}

	List<Mirror> _mirrors = new();
	int _framesUntilRefresh;

	protected override void OnEnabled()
	{
		Refresh( log: true );
	}

	protected override void OnDisabled()
	{
		foreach ( var mirror in _mirrors )
			Show( mirror, null );
	}

	protected override void OnDestroy()
	{
		foreach ( var mirror in _mirrors )
			mirror.Texture?.Dispose();

		_mirrors.Clear();
	}

	/// <summary>
	/// Finds the mirror planes and their glass.
	/// </summary>
	void Refresh( bool log )
	{
		var old = _mirrors;
		var mirrors = new List<Mirror>();
		foreach ( var marker in Scene.FindAllWithTag( MirrorPlanes.PlaneTag ) )
		{
			var tag = marker.Tags.TryGetAll().FirstOrDefault( t => t.StartsWith( "halo_mirror_" ) && t != MirrorPlanes.PlaneTag && t != MirrorPlanes.UnboundedTag );
			if ( tag is null )
				continue;

			var mirror = new Mirror { Normal = marker.WorldRotation.Forward, Origin = marker.WorldPosition, Unbounded = marker.Tags.Has( MirrorPlanes.UnboundedTag ) };
			mirror.Distance = Vector3.Dot( mirror.Normal, marker.WorldPosition );
			foreach ( var glass in Scene.FindAllWithTag( tag ) )
			{
				if ( glass != marker && glass.GetComponent<ModelRenderer>() is { } renderer && renderer.IsValid() )
					mirror.Glass.Add( renderer );
			}

			if ( mirror.Glass.Count == 0 )
				continue;

			mirror.Bounds = mirror.Glass.Skip( 1 ).Aggregate( mirror.Glass[0].Bounds, ( box, r ) => box.AddBBox( r.Bounds ) );
			if ( old.FirstOrDefault( m => m.Normal == mirror.Normal && m.Distance == mirror.Distance ) is { } previous )
			{
				mirror.Texture = previous.Texture;
				mirror.Shown = previous.Shown;
			}

			mirrors.Add( mirror );

			if ( log )
				HaloCeMount.Current?.Logger.Info( $"Halo mirror in {Scene.Name}: {marker.Name}, plane normal {mirror.Normal:F2} distance {mirror.Distance:F1}, {mirror.Glass.Count} glass objects in {mirror.Bounds.Mins:F0} .. {mirror.Bounds.Maxs:F0}" );
		}

		foreach ( var gone in old.Where( m => m.Texture is not null && !mirrors.Any( n => n.Texture == m.Texture ) ) )
			gone.Texture.Dispose();

		_mirrors = mirrors;
		_framesUntilRefresh = 120;
	}

	// IPreRenderSubscriber is what gets OnPreRender called; s&box's code generator adds it to game
	// components, so mount components declare it themselves.
	protected override void OnPreRender()
	{
		if ( --_framesUntilRefresh <= 0 )
			Refresh( log: false );

		var camera = Scene.Camera is { } main && main.IsValid() && main.Enabled
			? main
			: Scene.GetAllComponents<CameraComponent>().FirstOrDefault( c => c.IsValid() && c.Enabled );

		foreach ( var mirror in _mirrors )
		{
			if ( camera is null || !Render( mirror, camera ) )
				Show( mirror, null );
		}
	}

	/// <summary>
	/// Renders the mirror for the camera if it can see it.
	/// </summary>
	bool Render( Mirror mirror, CameraComponent camera )
	{
		var position = camera.WorldPosition;

		// The face toward the camera; edge on, neither.
		var side = Vector3.Dot( mirror.Normal, position ) - mirror.Distance;
		if ( MathF.Abs( side ) < 0.01f )
			return false;

		if ( mirror.Unbounded )
		{
			// Its glass's bounds are moved for sorting; test the plane itself, out to the horizon.
			var n = mirror.Normal;
			var extent = new Vector3( 1 - MathF.Abs( n.x ), 1 - MathF.Abs( n.y ), 1 - MathF.Abs( n.z ) ) * UnboundedExtent + Vector3.One;
			if ( !camera.GetFrustum().IsInside( new BBox( mirror.Origin - extent, mirror.Origin + extent ), true ) )
				return false;
		}
		else
		{
			var closest = Vector3.Max( mirror.Bounds.Mins, Vector3.Min( mirror.Bounds.Maxs, position ) );
			if ( closest.Distance( position ) > MaximumDistance || !camera.GetFrustum().IsInside( mirror.Bounds, true ) )
				return false;
		}

		var size = camera.ScreenRect.Size;
		if ( mirror.Unbounded )
			size = new Vector2( MathF.Max( 1, MathF.Round( size.x * UnboundedScale ) ), MathF.Max( 1, MathF.Round( size.y * UnboundedScale ) ) );
		if ( size.x < 1 || size.y < 1 )
			return false;

		if ( mirror.Texture is null || mirror.Texture.Width != (int)size.x || mirror.Texture.Height != (int)size.y )
		{
			var old = mirror.Texture;
			mirror.Texture = Texture.CreateRenderTarget( "halo mirror", ImageFormat.RGBA16161616F, size );
			old?.Dispose();
		}

		var normal = side > 0 ? mirror.Normal : -mirror.Normal;
		var plane = new Plane( normal, side > 0 ? mirror.Distance : -mirror.Distance );
		var transform = camera.WorldTransform.Mirror( plane );
		var clip = new Plane( plane.Position + normal * ClipOffset, normal );

		var rendered = camera.RenderToTexture( mirror.Texture, new ViewSetup
		{
			Transform = transform,
			ProjectionMatrix = Matrix.CreateObliqueProjection( transform, clip, camera.ProjectionMatrix ),
			FlipX = true,
			ExcludeTags = new TagSet { MirrorPlanes.GlassTag },
			EnablePostprocessing = false,
			EnableUI = false,
			EnableDebugOverlays = false,
		} );

		Show( mirror, rendered ? mirror.Texture : null );
		return rendered;
	}

	/// <summary>
	/// Gives the mirror's glass its reflection, or takes it away (null).
	/// </summary>
	static void Show( Mirror mirror, Texture texture )
	{
		if ( texture is null && !mirror.Shown )
			return;

		foreach ( var glass in mirror.Glass )
		{
			if ( !glass.IsValid() || glass.SceneObject is not { } sceneObject )
				continue;

			if ( texture is not null )
				sceneObject.Attributes.Set( "HaloMirror", texture );
			sceneObject.Attributes.Set( "HaloHasMirror", texture is null ? 0f : 1f );

		}

		mirror.Shown = texture is not null;
	}

	// Watching for scenes with mirrors.

	static readonly object WatchLock = new();
	static System.Threading.Timer _watchTimer;
	static int _scanQueued;

	/// <summary>
	/// The engine's set of every live scene, editor scenes included (<see cref="Scene.All"/> leaves
	/// those out). Private; without it only game scenes get mirrors.
	/// </summary>
	static readonly PropertyInfo AllScenesProperty = typeof( Scene ).GetProperty( "_all", BindingFlags.NonPublic | BindingFlags.Static );

	/// <summary>
	/// Starts looking for live scenes with mirror planes, twice a second, adding the mirrors to
	/// each one found. Safe to call more than once.
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

	/// <summary>
	/// The hidden object holding a scene's mirrors. Looked for by name: the component is on a hidden object,
	/// which a component search leaves out, and Halo 2's development loader loads its own copy of this assembly,
	/// whose component is another type; one is enough.
	/// </summary>
	const string MirrorsObjectName = "halo mirrors";

	static void Scan()
	{
		_scanQueued = 0;
		try
		{
			foreach ( var scene in LiveScenes() )
			{
				if ( !scene.IsValid() || scene.Children.Any( g => g.Name == MirrorsObjectName )
					|| !scene.FindAllWithTag( MirrorPlanes.PlaneTag ).Any() )
					continue;

				AddTo( scene );
			}
		}
		catch ( Exception e )
		{
			HaloCeMount.Current?.Logger.Warning( e, $"Couldn't add Halo mirrors: {e.Message}" );
		}
	}

	static List<Scene> LiveScenes()
	{
		if ( AllScenesProperty?.GetValue( null ) is System.Collections.IEnumerable all )
			return all.OfType<Scene>().ToList();

		return Scene.All.ToList();
	}

	static void AddTo( Scene scene )
	{
		using ( scene.Push() )
		{
			var go = new GameObject( true, MirrorsObjectName );
			go.Flags |= GameObjectFlags.Hidden | GameObjectFlags.NotSaved | GameObjectFlags.NotNetworked;

			// The type library is the game's, which doesn't know mount types: a new component looks
			// its type up there for required components and warns, unless it's being deserialized.
			go.Flags |= GameObjectFlags.Deserializing;
			var mirrors = go.Components.Create<MirrorReflections>( false );
			go.Flags &= ~GameObjectFlags.Deserializing;
			mirrors.Flags |= ComponentFlags.Hidden | ComponentFlags.NotSaved | ComponentFlags.NotNetworked;
			mirrors.Enabled = true;
		}
	}
}
