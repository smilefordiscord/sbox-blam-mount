using System.Reflection;
using Sandbox.Internal;

namespace Sandbox.Mounting.Blam.Halo1;

/// <summary>
/// Puts the planar fog and its screen layers where Halo draws them among the transparent things, and
/// grabs the frames they blend over in gamma space (halo1_fog_plane.shader).
/// </summary>
/// <remarks>
/// While the camera is in the bounds of a fog, its fog draw is given bounds far straight ahead of the
/// camera, so it sorts before every other transparent thing (Halo draws the planar fog right after the
/// opaque level), and its screen layer draw bounds just past the near plane, so it sorts after them all,
/// the water too (Halo draws them last). The BSPs' detail objects (grass, reeds) sort just behind the
/// screen layers' grab: Halo blends them after the water and before the screen layers.
///
/// s&amp;box's frame copy for translucent readers is masked by the reader's own depth, and the fog
/// writes none, so it reads black. So objects of this one's grab the frame just before the fog draws
/// (HaloFogPlaneBehind) and just before the screen layers draw (HaloFogBehind), and they read the copies.
///
/// Weather (rain, snow) sorts just behind the detail objects, after the fog and the water as Halo drew it, with a grab of its own right before it (HaloWeatherBehind) that it blends over in gamma space (halo1_particles.shader).
///
/// Added to live scenes with fogs, detail objects or weather the way <see cref="MirrorReflections"/> is.
/// </remarks>
sealed class PlanarFogFrame : Component, Component.ExecuteInEditor, IPreRenderSubscriber
{
	public const string FrameAttribute = "HaloFogBehind";
	public const string HasFrameAttribute = "HaloHasFogBehind";
	public const string PlaneFrameAttribute = "HaloFogPlaneBehind";
	public const string HasPlaneFrameAttribute = "HaloHasFogPlaneBehind";

	/// <summary>
	/// On the BSPs' detail objects, which sort after the water (see the remarks).
	/// </summary>
	public const string DetailObjectsTag = "halo1_detail_objects";

	/// <summary>
	/// On the BSPs' water, which reads the frame grabbed before the fog as what's behind it: s&amp;box's copy is taken after the fog has drawn, and the fog's coarse water mask leaves a band along every shore fogged twice.
	/// </summary>
	public const string WaterTag = "halo1_water";

	// On the level's weather, which sorts behind the detail objects with a grab of its own (see the remarks).
	public const string WeatherTag = "halo1_weather";
	public const string WeatherFrameAttribute = "HaloWeatherBehind";
	public const string HasWeatherFrameAttribute = "HaloHasWeatherBehind";

	/// <summary>
	/// How far ahead the fogs and the grab before them sort, as fractions of the camera's far plane.
	/// </summary>
	const float PlaneGrabSortFraction = 0.95f;
	const float FogSortFraction = 0.9f;

	/// <summary>
	/// How far ahead the grab and the screen layers sort, in multiples of the camera's near plane.
	/// </summary>
	const float DetailSortNear = 4f;
	const float GrabSortNear = 3f;
	const float ScreenSortNear = 2f;
	const float WeatherGrabSortNear = 6f;
	const float WeatherSortNear = 5f;

	/// <summary>
	/// The size of the sorting boxes for their distance: big enough on screen not to be culled as too
	/// small, small enough to stay nearly as far.
	/// </summary>
	const float BoundsSize = 0.1f;

	readonly List<ModelRenderer> _fogs = new();
	readonly List<ModelRenderer> _screens = new();
	readonly List<ModelRenderer> _details = new();
	readonly List<ModelRenderer> _water = new();
	readonly List<ModelRenderer> _weather = new();
	readonly Grabber _planeGrab = new( "halo fog plane behind", PlaneFrameAttribute );
	readonly Grabber _screenGrab = new( "halo fog behind", FrameAttribute );
	readonly Grabber _weatherGrab = new( "halo weather behind", WeatherFrameAttribute );
	int _framesUntilRefresh;

	protected override void OnEnabled()
	{
		Refresh();
	}

	protected override void OnDisabled()
	{
		foreach ( var fog in _fogs.Concat( _screens ).Concat( _details ).Concat( _weather ).Concat( _water ) )
			Restore( fog );

		_planeGrab.Disable();
		_screenGrab.Disable();
		_weatherGrab.Disable();
	}

	protected override void OnDestroy()
	{
		_planeGrab.Dispose();
		_screenGrab.Dispose();
		_weatherGrab.Dispose();
	}

	void Refresh()
	{
		Find( PlanarFog.PlaneTag, _fogs );
		Find( PlanarFog.ScreenTag, _screens );
		Find( DetailObjectsTag, _details );
		Find( WaterTag, _water );
		Find( WeatherTag, _weather );

		_framesUntilRefresh = 120;
	}

	void Find( string tag, List<ModelRenderer> renderers )
	{
		renderers.Clear();
		foreach ( var go in Scene.FindAllWithTag( tag ) )
		{
			if ( go.GetComponent<ModelRenderer>() is { } renderer && renderer.IsValid() )
				renderers.Add( renderer );
		}
	}

	// IPreRenderSubscriber is what gets OnPreRender called (see MirrorReflections).
	protected override void OnPreRender()
	{
		if ( --_framesUntilRefresh <= 0 )
			Refresh();

		var camera = Scene.Camera is { } main && main.IsValid() && main.Enabled
			? main
			: Scene.GetAllComponents<CameraComponent>().FirstOrDefault( c => c.IsValid() && c.Enabled );

		var eye = camera?.WorldPosition ?? default;
		var forward = camera?.WorldRotation.Forward ?? default;
		BBox Ahead( float distance ) => BBox.FromPositionAndSize( eye + forward * distance, distance * BoundsSize );

		bool InFog( ModelRenderer fog ) => camera is not null && fog.IsValid() && fog.Active && fog.Model.Bounds.Contains( eye );

		var fogged = false;
		foreach ( var fog in _fogs )
		{
			if ( !InFog( fog ) || fog.SceneObject is not { } sceneObject )
			{
				Restore( fog );
				continue;
			}

			fogged = true;
			sceneObject.Bounds = Ahead( camera.ZFar * FogSortFraction );
			SourceVisibility.KeepDrawn( sceneObject );
			_planeGrab.Apply( sceneObject, HasPlaneFrameAttribute );
		}

		_planeGrab.Place( Scene.SceneWorld, fogged ? Ahead( camera.ZFar * PlaneGrabSortFraction ) : null );
		foreach ( var water in _water )
		{
			if ( water.IsValid() && water.SceneObject is { } sceneObject )
			{
				if ( fogged )
					_planeGrab.Apply( sceneObject, HasPlaneFrameAttribute );
				else
					sceneObject.Attributes.Set( HasPlaneFrameAttribute, false );
			}
		}

		foreach ( var details in _details )
		{
			if ( camera is null || !details.IsValid() || details.SceneObject is not { } sceneObject )
				Restore( details );
			else
			{
				sceneObject.Bounds = Ahead( camera.ZNear * DetailSortNear );
				SourceVisibility.KeepDrawn( sceneObject );
			}
		}

		var screened = false;
		foreach ( var screen in _screens )
		{
			if ( !InFog( screen ) || screen.SceneObject is not { } sceneObject )
			{
				Restore( screen );
				continue;
			}

			screened = true;
			sceneObject.Bounds = Ahead( camera.ZNear * ScreenSortNear );
			SourceVisibility.KeepDrawn( sceneObject );
			_screenGrab.Apply( sceneObject, HasFrameAttribute );
		}

		_screenGrab.Place( Scene.SceneWorld, screened ? Ahead( camera.ZNear * GrabSortNear ) : null );

		var raining = false;
		foreach ( var weather in _weather )
		{
			if ( camera is null || !weather.IsValid() || !weather.Active || !weather.Model.Bounds.Contains( eye ) || weather.SceneObject is not { } sceneObject )
			{
				Restore( weather );
				continue;
			}

			raining = true;
			sceneObject.Bounds = Ahead( camera.ZNear * WeatherSortNear );
			SourceVisibility.KeepDrawn( sceneObject );
			_weatherGrab.Apply( sceneObject, HasWeatherFrameAttribute );
		}

		_weatherGrab.Place( Scene.SceneWorld, raining ? Ahead( camera.ZNear * WeatherGrabSortNear ) : null );
	}

	static void Restore( ModelRenderer fog )
	{
		if ( fog.IsValid() && fog.SceneObject is { } sceneObject )
		{
			sceneObject.Bounds = fog.Model.Bounds;
			sceneObject.Attributes.Set( HasFrameAttribute, false );
			sceneObject.Attributes.Set( HasPlaneFrameAttribute, false );
			sceneObject.Attributes.Set( HasWeatherFrameAttribute, false );
		}
	}

	// A copy of the frame as far as it's drawn, made by an object of its own sorted among the transparent things.
	sealed class Grabber( string name, string attribute )
	{
		SceneCustomObject _object;
		Texture _frame;
		bool _copyFailed;

		Texture Frame => _copyFailed ? null : _frame;

		public void Apply( SceneObject sceneObject, string hasAttribute )
		{
			if ( Frame is { } frame )
				sceneObject.Attributes.Set( attribute, frame );
			sceneObject.Attributes.Set( hasAttribute, Frame is not null );
		}

		// Sorts the grab at these bounds in this world, or stops it with none.
		public void Place( SceneWorld world, BBox? bounds )
		{
			if ( bounds is not { } box )
			{
				if ( _object is not null )
					_object.RenderingEnabled = false;
				return;
			}

			if ( _object is null || !_object.IsValid() || _object.World != world )
			{
				_object?.Delete();
				_object = new SceneCustomObject( world ) { RenderOverride = _ => Grab() };
				_object.Flags.IsOpaque = false;
				_object.Flags.IsTranslucent = true;
				_object.Flags.CastShadows = false;
				SourceVisibility.KeepDrawn( _object );
			}

			_object.RenderingEnabled = true;
			_object.Bounds = box;
		}

		public void Disable()
		{
			_object?.Delete();
			_object = null;
		}

		public void Dispose()
		{
			_frame?.Dispose();
			_frame = null;
		}

		void Grab()
		{
			if ( _copyFailed )
				return;

			try
			{
				var frame = Graphics.GrabFrameTexture( attribute ).ColorTarget;
				_frame = Texture.CreateRenderTarget( name, frame.ImageFormat, frame.Size, _frame );
				Graphics.CopyTexture( frame, _frame );
			}
			catch ( Exception e )
			{
				_copyFailed = true;
				HaloCeMount.Current?.Logger.Warning( $"Couldn't copy the frame ({name}): {e.Message}" );
			}
		}
	}

	// Watching for scenes with such fogs (see MirrorReflections).

	static readonly object WatchLock = new();
	static System.Threading.Timer _watchTimer;
	static int _scanQueued;

	static readonly PropertyInfo AllScenesProperty = typeof( Scene ).GetProperty( "_all", BindingFlags.NonPublic | BindingFlags.Static );

	/// <summary>
	/// The hidden object holding a scene's fog frame, looked for by name (see MirrorReflections).
	/// </summary>
	const string ObjectName = "halo planar fog frame";

	/// <summary>
	/// Starts looking for live scenes with fogs, detail objects or weather, twice a second. Safe to call
	/// more than once.
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
				if ( !scene.IsValid() || scene.Children.Any( g => g.Name == ObjectName ) || !scene.FindAllWithTag( PlanarFog.PlaneTag ).Concat( scene.FindAllWithTag( DetailObjectsTag ) ).Concat( scene.FindAllWithTag( WeatherTag ) ).Any() )
					continue;

				AddTo( scene );
			}
		}
		catch ( Exception e )
		{
			HaloCeMount.Current?.Logger.Warning( e, $"Couldn't add the planar fog's frame: {e.Message}" );
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
			var frame = go.Components.Create<PlanarFogFrame>( false );
			go.Flags &= ~GameObjectFlags.Deserializing;
			frame.Flags |= ComponentFlags.Hidden | ComponentFlags.NotSaved | ComponentFlags.NotNetworked;
			frame.Enabled = true;
		}
	}
}
