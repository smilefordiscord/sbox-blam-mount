using System.Reflection;
using Blam.Halo2.Tags;
using Sandbox.Internal;
using Sandbox.Rendering;

namespace Sandbox.Mounting.Blam.Halo2;

/// <summary>
/// What a Halo 2 scene needs done every frame: the order its sky's layers and open water draw in, what the open water
/// reflects, its weather (<see cref="Weather"/>, drawn right after the water), the frame its camouflaged objects refract
/// (<see cref="CamoFrame"/>, grabbed after the weather), the lighting of the objects a game moves in it
/// (<see cref="MovingObjectLights"/>) and its bloom (<see cref="Bloom"/>, on the scene's camera).
/// </summary>
/// <remarks>
/// <para>Draw order (<see cref="KeepInOrder"/>): Halo draws the sky's layers one after another right after the opaque
/// geometry, then the water, then everything else transparent. s&amp;box sorts transparent objects back to front by
/// the distance to their bounds, and the camera is inside the sky's, so each sky layer is an object of its own
/// (<see cref="SkyLayerTag"/>, in order under one parent), and every frame their bounds and the water's go to boxes
/// straight ahead of the camera: the sky's layers farthest, first layer first, then the water, all farther than
/// anything else.</para>
///
/// <para>Open water reflects the scene seen from a point above the water, rendered once into the six faces of a cube
/// (six square textures) shortly after the scene appears and again a moment later, once its textures have streamed
/// in. halo2_water.shader looks the faces up by the reflection of the view off the bumped surface. Halo rendered the
/// scene mirrored in the water's plane every frame. In s&amp;box that is another view of the whole scene per frame,
/// which took Zanzibar from 120 fps to 30-60 in the editor, even at a low resolution (most of a view's cost doesn't
/// depend on its size). What the sea mostly reflects is the sky, which a cube catches from anywhere, so the cube
/// holds the sky's layers only: level geometry seen from one fixed point reflected in the wrong places, and the point
/// (the level's centre) can be inside it (Dune's tower: the sea went dark). s&amp;box's EnvmapProbe would do this, but
/// its dynamic rendering doesn't run in editor scenes. The faces go on the water objects' attributes (HaloReflection0-5,
/// HaloHasReflection).</para>
///
/// <para>Mount component types can't be part of the scene the loader saves, so, as Halo 1's MirrorReflections does,
/// <see cref="Watch"/> looks for live scenes with a map marker (<see cref="MapTag"/>), a sky layer or a probe marker (<see cref="ProbeTag"/>) and adds this
/// to each on a hidden object that isn't saved.</para>
/// </remarks>
sealed class SceneUpkeep : Component, Component.ExecuteInEditor, IPreRenderSubscriber
{
	/// <summary>
	/// The marker object the open water's reflection cube is rendered from.
	/// </summary>
	public const string ProbeTag = "halo2_open_water_probe";

	/// <summary>
	/// The open water objects, which get the faces (and are left out of them).
	/// </summary>
	public const string WaterTag = "halo2_open_water";

	/// <summary>
	/// The sky's layers, drawn in the order they are in under their parent.
	/// </summary>
	public const string SkyLayerTag = "halo2_sky_layer";

	/// <summary>
	/// The decals (placed decals and decorators' floating decals), drawn after the sky and before the water.
	/// </summary>
	public const string DecalTag = "halo2_decal";

	// The marker object a map's scene carries, named MapPrefix and the map's name, since a game can load the map into a scene of its own.
	public const string MapTag = "halo2_map";
	const string MapPrefix = "halo2 map: ";

	// How often (frames) a scene without its map's marker looks for one, as a game swaps maps in the same scene.
	const int MapScanInterval = 30;

	const int FaceSize = 512;

	/// <summary>
	/// Frames after the component starts at which the cube is rendered: soon, and again as the sky's textures stream in
	/// (six small views of the sky alone are cheap).
	/// </summary>
	static readonly int[] RenderFrames = [1, 5, 30, 120, 300];

	/// <summary>
	/// How often (frames) the faces are handed to the water objects again, in case their scene objects were rebuilt.
	/// </summary>
	const int ReapplyInterval = 60;

	/// <summary>
	/// Each face's forward and up, as halo2_water.shader's SampleReflection expects them (+X, -X, +Y, -Y, +Z, -Z).
	/// </summary>
	static readonly (Vector3 Forward, Vector3 Up)[] Faces =
	[
		(Vector3.Forward, Vector3.Up), (Vector3.Backward, Vector3.Up),
		(Vector3.Left, Vector3.Up), (Vector3.Right, Vector3.Up),
		(Vector3.Up, Vector3.Backward), (Vector3.Down, Vector3.Forward),
	];

	readonly Texture[] _faces = new Texture[6];
	bool _rendered;
	int _frame;

	// Frames since the scene's map was found, which the cube's renders and the planar fog's first passes count.
	int _mapFrame;

	CommandList _bloom;
	CommandList _sunGlare;
	CameraComponent _bloomCamera;

	Weather _weather;
	PlanarFog _planarFog;
	(Vector3 Direction, float SkyFogDensity)? _glareSun;
	bool _weatherLooked;

	GameObject _mapMarker;
	string _map;

	readonly FlareVisibility _flareVisibility = new();
	readonly CamoFrame _camoFrame = new();
	readonly List<ModelRenderer> _camoRenderers = new();
	readonly SkyGamma _skyGamma = new();
	readonly MovingObjectLights _objectLights = new();

	// Leaves the map's marker in the scene being built.
	public static void Mark( string mapPath )
	{
		var go = new GameObject( true, MapPrefix + System.IO.Path.GetFileNameWithoutExtension( mapPath ) );
		go.Tags.Add( MapTag );
	}

	protected override void OnDestroy()
	{
		foreach ( var face in _faces )
			face?.Dispose();

		DetachCamera();
		_flareVisibility.Delete();
		_weather?.Delete();
		_camoFrame.Delete();
		_skyGamma.Delete();
	}

	void DetachCamera()
	{
		if ( _bloomCamera.IsValid() )
		{
			_bloomCamera.RemoveCommandList( _bloom, Stage.BeforePostProcess );
			if ( _sunGlare is not null )
				_bloomCamera.RemoveCommandList( _sunGlare, Stage.BeforePostProcess );
			if ( _flareVisibility.Commands is { } flares )
				_bloomCamera.RemoveCommandList( flares, Stage.AfterOpaque );
		}

		_bloomCamera = null;
	}

	// Follows the map the scene shows: its marker's, else the scene's own (mount://halo2/maps/<map>.scene).
	// When it changes, the weather, planar fog and sun glare are looked up again and the water's reflection is rendered again.
	void FollowMap()
	{
		if ( _mapMarker.IsValid() || (_frame > 1 && _frame % MapScanInterval != 0) )
			return;

		_mapMarker = MapMarker( Scene );
		var map = _mapMarker is not null ? _mapMarker.Name[MapPrefix.Length..] : SourceMap( Scene );
		if ( string.Equals( map, _map, StringComparison.OrdinalIgnoreCase ) )
			return;

		_map = map;
		DetachCamera();
		_sunGlare = null;
		_weather?.Delete();
		_weather = null;
		_planarFog = null;
		_glareSun = null;
		_weatherLooked = false;
		_rendered = false;
		_mapFrame = 0;
	}

	static GameObject MapMarker( Scene scene ) =>
		scene.FindAllWithTag( MapTag ).FirstOrDefault( g => g.Name.StartsWith( MapPrefix, StringComparison.Ordinal ) );

	static string SourceMap( Scene scene )
	{
		var path = scene.Source?.ResourcePath;
		return path is not null && path.Contains( "maps/", StringComparison.OrdinalIgnoreCase ) ? System.IO.Path.GetFileNameWithoutExtension( path ) : null;
	}

	/// <summary>
	/// The map a scene shows: its marker's, else the scene's own; null for none.
	/// </summary>
	public static string MapOf( Scene scene ) => MapMarker( scene ) is { } marker ? marker.Name[MapPrefix.Length..] : SourceMap( scene );

	/// <summary>
	/// Hands the planar fog (<see cref="PlanarFog"/>) to every model in the scene.
	/// </summary>
	void ApplyPlanarFog()
	{
		SceneWeather();
		if ( _planarFog is null )
			return;

		foreach ( var renderer in Scene.GetAllComponents<ModelRenderer>() )
		{
			if ( renderer.SceneObject is { } sceneObject )
				_planarFog.Apply( sceneObject );
		}
	}

	/// <summary>
	/// The weather of the map this scene shows (<see cref="FollowMap"/>), or null. Its planar fog is looked up with it.
	/// </summary>
	Weather SceneWeather()
	{
		if ( _weatherLooked )
			return _weather;

		_weatherLooked = true;
		try
		{
			if ( _map is null || Halo2Mount.Current is not { } mount )
				return null;

			if ( mount.GetMap( _map ) is { } cache )
			{
				_weather = Weather.Build( cache );
				_planarFog = PlanarFog.Build( cache );
				_glareSun = LensFlareModels.GlareSun( new ScenarioTag( cache.Scenario ) );
			}
		}
		catch ( Exception e )
		{
			Halo2Mount.Current?.Logger.Warning( e, $"Couldn't read the Halo 2 weather: {e.Message}" );
		}

		return _weather;
	}

	// IPreRenderSubscriber is what gets OnPreRender called; mount components declare it themselves.
	protected override void OnPreRender()
	{
		_frame++;
		FollowMap();
		_mapFrame++;
		if ( RenderFrames.Contains( _mapFrame ) && !Render() )
			_mapFrame--;

		if ( _rendered && _frame % ReapplyInterval == 0 )
			Apply();

		if ( _mapFrame <= 5 || _frame % ReapplyInterval == 0 )
			ApplyPlanarFog();

		AnimatedPrt.Update( Scene );
		ApplyCamoFrame();
		_flareVisibility.Update( Scene );
		_objectLights.Update( Scene, _map, Camera() );

		if ( Camera() is { } camera )
		{
			KeepInOrder( camera.WorldPosition, camera.WorldRotation.Forward, camera.ZFar );
			_flareVisibility.KeepLast( Scene.SceneWorld, camera.WorldPosition, camera.WorldRotation.Forward, camera.ZNear, camera.GetFrustum() );
			KeepBloom( camera );
		}
	}

	/// <summary>
	/// Keeps Halo 2's bloom (<see cref="Bloom"/>) and then its sun glare (<see cref="SunGlare"/>, for the map's sun) on the
	/// scene's camera, which the editor's view runs too.
	/// </summary>
	void KeepBloom( CameraComponent camera )
	{
		if ( camera == _bloomCamera )
			return;

		DetachCamera();
		_bloom ??= Bloom.Build();
		if ( _bloom is not null )
			camera.AddCommandList( _bloom, Stage.BeforePostProcess );

		// The lens flares' visibility, measured once the depth buffer holds the opaque things, before any flare draws.
		if ( _flareVisibility.Commands is { } flares )
			camera.AddCommandList( flares, Stage.AfterOpaque );

		SceneWeather();
		if ( _sunGlare is null && _glareSun is { } sun )
			_sunGlare = SunGlare.Build( sun.Direction, sun.SkyFogDensity );
		if ( _sunGlare is not null )
			camera.AddCommandList( _sunGlare, Stage.BeforePostProcess, SunGlare.Order );
		_bloomCamera = camera;
	}

	/// <summary>
	/// Moves the sky layers' and the water's bounds to boxes straight ahead of a view, the sky's layers farthest (first
	/// layer first), then the water, so they sort before every other transparent object in Halo's order, and are never
	/// culled while in view.
	/// </summary>
	/// <remarks>
	/// The water's box is as far away as the slab its model's bounds are (<see cref="StructureBspModel.BuildOpenWater"/>).
	/// That slab is only as wide as the water, so looking a little above the horizon took it out of the view and the
	/// whole sea vanished (Dune's reaches 512 world units, 305 below the camera: anything less than about 30 degrees
	/// down missed it). No fixed box is both in every view of the plane and farther than everything else.
	/// </remarks>
	void KeepInOrder( Vector3 eye, Vector3 forward, float zFar )
	{
		BBox Ahead( float distance ) => BBox.FromPositionAndSize( eye + forward * distance, distance * BoundsSize );

		var water = MathF.Min( StructureBspModel.OpenWaterSortDepth * Halo2Mount.WorldScale, zFar * 0.9f );
		foreach ( var go in Scene.FindAllWithTag( WaterTag ) )
		{
			if ( go.GetComponent<ModelRenderer>()?.SceneObject is { } sceneObject )
				SourceVisibility.Kept( sceneObject ).Bounds = Ahead( water );
		}

		// The weather right after the water, before every other transparent thing.
		if ( SceneWeather() is { } weather )
			SourceVisibility.Kept( weather.ObjectIn( Scene.SceneWorld ) ).Bounds = Ahead( water * WeatherSortFraction );

		// Then the frame camouflaged objects refract, which so holds the sky, the water and the weather.
		SourceVisibility.Kept( _camoFrame.ObjectIn( Scene.SceneWorld ) ).Bounds = Ahead( water * CamoFrameSortFraction );

		var sky = MathF.Min( SkySortDepth * Halo2Mount.WorldScale, zFar * 0.97f );
		var lastSkyLayer = sky;
		var anySkyLayer = false;
		var skyGamma = _skyGamma.Works ? 1f : 0f;
		foreach ( var layers in Scene.FindAllWithTag( SkyLayerTag ).GroupBy( l => l.Parent ) )
		{
			var ordered = layers.OrderBy( l => l.Parent?.Children.IndexOf( l ) ?? 0 ).ToList();
			for ( var i = 0; i < ordered.Count; i++ )
			{
				var distance = sky - (sky - water) * i / ordered.Count;
				lastSkyLayer = MathF.Min( lastSkyLayer, distance );
				if ( ordered[i].GetComponent<ModelRenderer>()?.SceneObject is { } sceneObject )
				{
					SourceVisibility.Kept( sceneObject ).Bounds = Ahead( distance );
					sceneObject.Attributes.Set( SkyGamma.LayerAttribute, skyGamma );
					anySkyLayer = true;
				}
			}
		}

		// The sky's layers in Halo's gamma space: the frame goes to it just before the first and back just after the last.
		if ( anySkyLayer )
		{
			var (toGamma, toLinear) = _skyGamma.ObjectsIn( Scene.SceneWorld );
			SourceVisibility.Kept( toGamma ).Bounds = Ahead( sky * 1.01f );
			SourceVisibility.Kept( toLinear ).Bounds = Ahead( lastSkyLayer - (lastSkyLayer - water) * 0.1f );
		}

		// Decals between the sky's last layer and the water: Halo draws them over the opaque level, before the sky, the
		// water and everything transparent (the sky only draws where nothing else is, so the order of the two doesn't
		// matter).
		var decals = water + (lastSkyLayer - water) * 0.5f;
		foreach ( var go in Scene.FindAllWithTag( DecalTag ) )
		{
			if ( go.GetComponent<ModelRenderer>()?.SceneObject is { } sceneObject )
				SourceVisibility.Kept( sceneObject ).Bounds = Ahead( decals );
		}

		// The decals effects leave come after the placed ones, a layer at a time in Halo's order.
		foreach ( var particles in Scene.GetAllComponents<Halo2ParticleWorld>() )
		{
			for ( var layer = 0; layer < Halo2ParticleWorld.DecalLayers; layer++ )
			{
				if ( particles.DecalLayerObject( layer ) is { } sceneObject )
					SourceVisibility.Kept( sceneObject ).Bounds = Ahead( decals - (decals - water) * (layer + 1) / (Halo2ParticleWorld.DecalLayers + 1) );
			}
		}
	}

	/// <summary>
	/// How far ahead the sky's first layer sorts (world units, at most nearly the far plane): farther than the open
	/// water (<see cref="StructureBspModel.OpenWaterSortDepth"/>), which Halo draws after the sky.
	/// </summary>
	const float SkySortDepth = 400;

	/// <summary>
	/// How far ahead the weather sorts, as a fraction of the water's distance: just nearer, so it draws after it.
	/// </summary>
	const float WeatherSortFraction = 0.95f;

	/// <summary>
	/// How far ahead the camouflage's frame grab sorts, as a fraction of the water's distance: nearer than the weather.
	/// </summary>
	const float CamoFrameSortFraction = 0.9f;

	/// <summary>
	/// Hands the camouflage's frame (<see cref="CamoFrame"/>) to the camouflaged objects' renderers, found anew every
	/// <see cref="CamoScanInterval"/> frames (a game may add or swap them at any time).
	/// </summary>
	void ApplyCamoFrame()
	{
		if ( _frame % CamoScanInterval == 1 )
		{
			_camoRenderers.Clear();
			_camoRenderers.AddRange( Scene.GetAllComponents<ModelRenderer>().Where( r => CamoFrame.IsCamo( r ) || CamoFrame.IsShield( r ) ) );
		}

		foreach ( var renderer in _camoRenderers )
		{
			if ( renderer.IsValid() )
				_camoFrame.Apply( renderer );
		}
	}

	const int CamoScanInterval = 10;

	/// <summary>
	/// The size of the box <see cref="KeepInOrder"/> puts the bounds in, for its distance: big enough on screen not to be
	/// culled as too small (s&amp;box drops anything under about 0.2% of the view across), small enough to stay nearly
	/// as far as the slab.
	/// </summary>
	const float BoundsSize = 0.1f;

	CameraComponent Camera() => Scene.Camera is { } main && main.IsValid() && main.Enabled
		? main
		: Scene.GetAllComponents<CameraComponent>().FirstOrDefault( c => c.IsValid() && c.Enabled );

	bool Render()
	{
		var probe = Scene.FindAllWithTag( ProbeTag ).FirstOrDefault();
		var camera = Camera();
		if ( probe is null )
			return true;
		if ( camera is null )
			return false;

		// The camera's command lists (Halo 2's bloom and sun glare) run in RenderToTexture whatever its ViewSetup says, and
		// only the camera's own switch keeps them out: Halo's mirror holds the sky as drawn, and the bloom runs once, over
		// the finished frame. With them in, the sea under the horizon was bloomed twice (Zanzibar: +18/255 red, +10 green
		// over MCC's in the first 30 pixels, where MCC's mirror is its sky to 1/255).
		var post = camera.EnablePostProcessing;
		camera.EnablePostProcessing = false;
		try
		{
			RenderFaces( probe, camera );
		}
		finally
		{
			camera.EnablePostProcessing = post;
		}

		_rendered = true;
		Apply();
		return true;
	}

	void RenderFaces( GameObject probe, CameraComponent camera )
	{
		var projection = Matrix.CreateProjection( camera.ZNear, camera.ZFar, 90, 1 );
		for ( var i = 0; i < Faces.Length; i++ )
		{
			// The sky's layers in order for this face's view (it renders here and now).
			KeepInOrder( probe.WorldPosition, Faces[i].Forward, camera.ZFar );

			_faces[i] ??= Texture.CreateRenderTarget( $"halo2 water reflection {i}", ImageFormat.RGBA16161616F, new Vector2( FaceSize, FaceSize ) );
			camera.RenderToTexture( _faces[i], new ViewSetup
			{
				Transform = new Transform( probe.WorldPosition, Rotation.LookAt( Faces[i].Forward, Faces[i].Up ) ),
				ProjectionMatrix = projection,
				FieldOfView = 90,
				RenderTags = new TagSet { SkyLayerTag },
				ExcludeTags = new TagSet { WaterTag },
				EnablePostprocessing = false,
				EnableUI = false,
				EnableDebugOverlays = false,
			} );
		}
	}

	/// <summary>
	/// Hands the faces to the water objects.
	/// </summary>
	void Apply()
	{
		foreach ( var water in Scene.FindAllWithTag( WaterTag ) )
		{
			if ( water.GetComponent<ModelRenderer>()?.SceneObject is not { } sceneObject )
				continue;

			for ( var i = 0; i < Faces.Length; i++ )
				sceneObject.Attributes.Set( $"HaloReflection{i}", _faces[i] );
			sceneObject.Attributes.Set( "HaloHasReflection", 1f );
		}
	}

	// Watching for Halo 2 scenes.

	/// <summary>
	/// The hidden object holding a scene's upkeep. Looked for by name: a component search leaves hidden objects out.
	/// </summary>
	const string ObjectName = "halo2 scene upkeep";

	static readonly object WatchLock = new();
	static System.Threading.Timer _watchTimer;
	static int _scanQueued;

	/// <summary>
	/// The engine's set of every live scene, editor scenes included (<see cref="Scene.All"/> leaves those out).
	/// </summary>
	static readonly PropertyInfo AllScenesProperty = typeof( Scene ).GetProperty( "_all", BindingFlags.NonPublic | BindingFlags.Static );

	/// <summary>
	/// Starts looking for live scenes with a sky layer or open water, twice a second. Safe to call more than once.
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
				if ( !scene.IsValid() || scene.Children.Any( g => g.Name == ObjectName )
					|| (!scene.FindAllWithTag( MapTag ).Any() && !scene.FindAllWithTag( ProbeTag ).Any() && !scene.FindAllWithTag( SkyLayerTag ).Any()) )
					continue;

				using ( scene.Push() )
				{
					var go = new GameObject( true, ObjectName );
					go.Flags |= GameObjectFlags.Hidden | GameObjectFlags.NotSaved | GameObjectFlags.NotNetworked;

					// The type library is the game's, which doesn't know mount types: a new component looks its type
					// up there for required components and warns, unless it's being deserialized.
					go.Flags |= GameObjectFlags.Deserializing;
					var upkeep = go.Components.Create<SceneUpkeep>( false );
					go.Flags &= ~GameObjectFlags.Deserializing;
					upkeep.Flags |= ComponentFlags.Hidden | ComponentFlags.NotSaved | ComponentFlags.NotNetworked;
					upkeep.Enabled = true;
				}
			}
		}
		catch ( Exception e )
		{
			Halo2Mount.Current?.Logger.Warning( e, $"Couldn't add the Halo 2 scene upkeep: {e.Message}" );
		}
	}
}
