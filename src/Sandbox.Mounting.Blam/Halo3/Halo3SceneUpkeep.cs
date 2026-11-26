using System.Reflection;
using Blam.Halo3;
using Blam.Halo3.Tags;
using Sandbox.Internal;
using Sandbox.Rendering;

namespace Sandbox.Mounting.Blam.Halo3;

// What a Halo 3 scene needs every frame on top of its saved objects: its post-processing (Halo3PostFx, from the camera fx) and its weather (Halo3Weather), both picked by the cluster the camera is in.
// Mount component types can't be saved in a mounted scene, so the loader leaves a marker object (Mark) and Watch adds this to each live scene that has one, on a hidden object that isn't saved, as Halo2/SceneUpkeep does.
sealed class Halo3SceneUpkeep : Component, Component.ExecuteInEditor, IPreRenderSubscriber
{
	// The marker the scene loader leaves, named after the map.
	public const string MarkerTag = "halo3_screen_effects";
	const string MarkerPrefix = "halo3 screen effects: ";

	[ConVar( "halo3_postfx", Help = "Halo 3's post-processing (HDR, bloom, exposure, tone curve) on Halo 3 scenes; off, each material tone maps itself." )]
	public static bool PostFxEnabled { get; set; } = true;

	[ConVar( "halo3_underwater", Help = "Halo 3's underwater fog when the camera is inside a water volume." )]
	public static bool UnderwaterEnabled { get; set; } = true;

	[ConVar( "halo3_patchy_fog", Help = "Halo 3's patchy fog (noise sheets around the camera) on the maps whose sky atmosphere has it." )]
	public static bool PatchyFogEnabled { get; set; } = true;

	[ConVar( "halo3_motion_blur", Help = "Halo 3's camera motion blur (MCC's, always on there) on Halo 3 scenes." )]
	public static bool MotionBlurEnabled { get; set; } = true;

	[ConVar( "halo3_shadow_volumes", Help = "Objects' shadows on a Halo 3 level only count near their caster (as MCC clips them to the object's bounds); off, they fall on everything below." )]
	public static bool ShadowVolumes { get; set; } = true;

	[ConVar( "halo3_shadow_debug", Help = "Draws halo3_shader's sun shadow terms instead of the scene: 1 the visibility used, 2 the raw shadow map visibility, 3 the caster volume reach, 4 the sun's share of the diffuse light." )]
	public static int ShadowDebug { get; set; } = 0;

	// How often (seconds) the camera's cluster is looked up again.
	const double LocateInterval = 0.25;

	CacheFile _cache;
	ClusterAtmospherics _atmospherics;

	// MCC's cook_torrance area specular tables (rasterizer\cc0236, rasterizer\dd0236: 32x32 RGBA16F), for halo3_shader's H3CookTorrance.
	Texture _cookTorranceCc;
	Texture _cookTorranceDd;
	// rasg's default bitmap 7, the auto exposure measure's weights.
	Texture _exposureWeights;
	Halo3ScreenEffects _screenEffects;
	Halo3Distortion _distortion;
	Halo3PatchyFog _patchyFog;
	WaterVolumes _water;
	readonly Dictionary<int, Halo3Atmosphere> _settings = new();

	// rasg's motion blur (+0x88 taps, +0x8C max blur, +0x94 blur scale, +0x9C centre falloff, +0xA0 expected frame time) and the camera it last blurred from.
	MotionBlurFrame _blur = new( false, 1, 6, new Vector2( 0.03f, 0.05f ), new Vector2( 0.05f, 0.15f ), 1.4f, Vector2.Zero );
	float _expectedFrameTime = 0.03f;
	Transform? _lastView;
	CameraFxTag _cameraFx;
	Halo3Weather _weather;
	bool _looked;

	AtmosphereCluster _cluster;
	BloomSettings _bloomSettings = BloomSettings.Fallback;
	double _nextLocate;
	readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
	double _lastTime = -1;

	readonly Halo3PostFx _postFx = new();
	CommandList _post;
	CameraComponent _postCamera;
	CommandList _underwater;
	CameraComponent _underwaterCamera;
	(BloomSettings Bloom, float Stops, int Debug)? _postBuiltFor;

	// The sun's shadow casters for halo3_shader (as Halo1/ObjectShadowVolumes): the nearest 256 objects' bounding spheres, so a shadow stops near its caster instead of falling through floors onto whatever is below.
	const string CastersAttribute = "HaloShadowCasters";
	const string CasterCountAttribute = "HaloShadowCasterCount";
	const string CasterExtentsAttribute = "Halo3ShadowCasterExtents";
	const int MaximumCasters = 256;
	const int CasterRefreshFrames = 30;
	readonly List<ModelRenderer> _casterRenderers = new();
	readonly List<(float Distance, Vector4 Caster, Vector4 Extents)> _casters = new();
	readonly List<Vector4> _casterUpload = new();
	readonly List<Vector4> _extentUpload = new();
	GpuBuffer<Vector4> _casterBuffer;
	GpuBuffer<Vector4> _extentBuffer;
	int _framesUntilCasterRefresh;

	// Leaves the marker in the scene being built and starts watching for it.
	public static void Mark( CacheFile cache )
	{
		var go = new GameObject( true, MarkerPrefix + System.IO.Path.GetFileNameWithoutExtension( cache.FilePath ) );
		go.Tags.Add( MarkerTag );
		Watch();
	}

	protected override void OnDestroy()
	{
		RemovePostFx();
		_postFx.Delete();
		_distortion?.Delete();
		_patchyFog?.Delete();
		_weather?.Delete();
		Scene.RenderAttributes.Set( CasterCountAttribute, -1 );
		Scene.RenderAttributes.Set( CastersAttribute, (GpuBuffer)null );
		Scene.RenderAttributes.Set( CasterExtentsAttribute, (GpuBuffer)null );
		_casterBuffer?.Dispose();
		_casterBuffer = null;
		_extentBuffer?.Dispose();
		_extentBuffer = null;
	}

	// Every renderer that casts into the sun's shadow map (the level's are off), nearest the eye first.
	void UploadShadowCasters( Vector3 eye )
	{
		if ( --_framesUntilCasterRefresh <= 0 )
		{
			_casterRenderers.Clear();
			foreach ( var renderer in Scene.GetAllComponents<ModelRenderer>() )
			{
				if ( renderer.RenderType != ModelRenderer.ShadowRenderType.Off )
					_casterRenderers.Add( renderer );
			}
			_framesUntilCasterRefresh = CasterRefreshFrames;
		}

		_casters.Clear();
		foreach ( var renderer in _casterRenderers )
		{
			if ( !renderer.IsValid() || !renderer.Active || renderer.RenderType == ModelRenderer.ShadowRenderType.Off || renderer.Model is null )
				continue;
			var bounds = renderer.Bounds;
			var radius = bounds.Size.Length * 0.5f;
			if ( radius > 0 )
				_casters.Add( (bounds.Center.DistanceSquared( eye ), new Vector4( bounds.Center, radius ), new Vector4( bounds.Size * 0.5f, 0 )) );
		}

		if ( _casters.Count > MaximumCasters )
			_casters.Sort( ( a, b ) => a.Distance.CompareTo( b.Distance ) );

		_casterUpload.Clear();
		_extentUpload.Clear();
		for ( var i = 0; i < _casters.Count && i < MaximumCasters; i++ )
		{
			_casterUpload.Add( _casters[i].Caster );
			_extentUpload.Add( _casters[i].Extents );
		}

		if ( _casterBuffer is null || _casterBuffer.ElementCount < Math.Max( _casterUpload.Count, 1 ) )
		{
			_casterBuffer?.Dispose();
			_extentBuffer?.Dispose();
			_casterBuffer = new GpuBuffer<Vector4>( Math.Max( _casterUpload.Count, 64 ), GpuBuffer.UsageFlags.Structured, "halo3 shadow casters" );
			_extentBuffer = new GpuBuffer<Vector4>( Math.Max( _casterUpload.Count, 64 ), GpuBuffer.UsageFlags.Structured, "halo3 shadow caster extents" );
		}
		if ( _casterUpload.Count > 0 )
		{
			_casterBuffer.SetData( _casterUpload );
			_extentBuffer.SetData( _extentUpload );
		}

		Scene.RenderAttributes.Set( CastersAttribute, _casterBuffer );
		Scene.RenderAttributes.Set( CasterExtentsAttribute, _extentBuffer );
		Scene.RenderAttributes.Set( CasterCountAttribute, ShadowVolumes ? _casterUpload.Count : -1 );
		// Only set from the convar when it's on, so a script can set the attribute itself (the mount's convars aren't known to the editor's console).
		if ( ShadowDebug > 0 )
			Scene.RenderAttributes.Set( "Halo3ShadowDebug", (float)ShadowDebug );
	}

	// The map's camera fx, clusters and weather, looked up once from the marker's name.
	bool Look()
	{
		if ( _looked )
			return _atmospherics is not null;

		_looked = true;
		try
		{
			var marker = Scene.FindAllWithTag( MarkerTag ).FirstOrDefault( g => g.Name.StartsWith( MarkerPrefix, StringComparison.Ordinal ) );
			if ( marker is null || Halo3Mount.Current is not { } mount || mount.GetMap( marker.Name[MarkerPrefix.Length..] ) is not { } cache )
				return false;

			_cache = cache;
			_cookTorranceCc = Table( cache, "rasterizer\\cc0236" );
			_cookTorranceDd = Table( cache, "rasterizer\\dd0236" );
			_exposureWeights = Table( cache, "shaders\\default_bitmaps\\bitmaps\\auto_exposure_weight" );
			_cameraFx = CameraFxTag.ForScenario( cache );
			_atmospherics = new ClusterAtmospherics( cache );
			_weather = new Halo3Weather( cache );
			_bloomSettings = BloomSettings.From( _cameraFx );
			_screenEffects = new Halo3ScreenEffects( ScreenEffectTag.ForScenario( cache ) );
			Halo3ScreenEffects.Register( Scene, _screenEffects );
			_distortion = new Halo3Distortion();
			Halo3Distortion.Register( Scene, _distortion );
			_patchyFog = Halo3PatchyFog.For( cache, Scene.SceneWorld );
			_water = new WaterVolumes( cache );
			ReadMotionBlur( cache );
		}
		catch ( Exception e )
		{
			Halo3Mount.Current?.Logger.Warning( e, $"Couldn't read the Halo 3 screen effects: {e.Message}" );
			_atmospherics = null;
		}
		return _atmospherics is not null;
	}

	static Texture Table( CacheFile cache, string path )
	{
		var tag = cache.FindTag( path, "bitm" );
		if ( tag is null )
			return null;
		var texture = Texture.Load( Halo3Mount.TexturePath( tag.Path ) );
		return texture is null || texture.IsError ? null : texture;
	}

	// IPreRenderSubscriber is what gets OnPreRender called; mount components declare it themselves.
	protected override void OnPreRender()
	{
		if ( !Look() || _cache.File.IsDisposed )
			return;

		if ( _cookTorranceCc is not null && _cookTorranceDd is not null )
		{
			Scene.RenderAttributes.Set( "Halo3CookTorranceCc", _cookTorranceCc );
			Scene.RenderAttributes.Set( "Halo3CookTorranceDd", _cookTorranceDd );
			Scene.RenderAttributes.Set( "Halo3HasCookTorranceTables", 1f );
		}

		var now = _clock.Elapsed.TotalSeconds;
		var frameTime = _lastTime < 0 ? 0 : (float)(now - _lastTime);
		var dt = Math.Clamp( frameTime, 0, 0.1f );
		_lastTime = now;
		Halo3Materials.Animate( Time.Now );

		// The view the weather last drew for (the editor's too), else the scene's camera; with neither, the weather object still goes in the world to learn the view.
		var camera = Camera();
		if ( (_weather.LastCamera ?? camera?.WorldPosition) is not { } eye )
		{
			_weather.KeepAround( Scene.SceneWorld, null );
			return;
		}

		Halo3ObjectLighting.ApplyOnce( Scene, _cache );
		UploadShadowCasters( eye );

		if ( now >= _nextLocate )
		{
			_nextLocate = now + LocateInterval;
			var p = eye / Halo3Mount.WorldScale;
			_cluster = _atmospherics.Locate( new System.Numerics.Vector3( p.x, p.y, p.z ) ) ?? _cluster;
			_weather.SetActive( _atmospherics.WeatherOf( _cluster ) );
			_bloomSettings = BloomSettings.From( _cameraFx, _atmospherics.CameraFxOf( _cluster ) );
		}

		try
		{
			_weather.KeepAround( Scene.SceneWorld, eye );
			_weather.Update( Scene, dt );
		}
		catch ( Exception e )
		{
			Halo3Mount.Current?.Logger.Warning( $"Couldn't update the Halo 3 weather: {e.Message}" );
		}

		if ( camera is not null )
		{
			KeepPostFx( camera );
			_screenEffects.Step( dt );
			var frame = Frame( camera, frameTime );
			_postFx.Update( frame );
			KeepUnderwater( camera, frame );
			_patchyFog?.Update( camera, PatchyFogEnabled ? SettingOf( _cluster ) : null, dt );
		}
		ShareHdrWithSky();
		// The map's fixed exposure for what draws without the post-processing (halo3_weather's fallback; halo3_shader takes it per renderer).
		Scene.RenderAttributes.Set( "Halo3SceneExposureStops", Halo3Materials.CameraExposure( _cache ) );
		// The camera fx's self-illumination exposure (halo3_shader's H3SelfIllumExposure).
		if ( _cameraFx is not null )
			Scene.RenderAttributes.Set( "Halo3SelfIllumExposure", new Vector2( _cameraFx.SelfIllumPreferredExposure, _cameraFx.SelfIllumExposureChange ) );
	}

	// The camera cluster's sky atmosphere setting, read once per setting.
	Halo3Atmosphere SettingOf( AtmosphereCluster cluster )
	{
		if ( cluster is null || _atmospherics?.SettingOf( cluster ) is not { } setting )
			return null;
		if ( !_settings.TryGetValue( setting.Index, out var atmosphere ) )
			_settings[setting.Index] = atmosphere = Halo3Atmosphere.ForSetting( _cache, setting.Index );
		return atmosphere;
	}

	// This frame's screen effects and motion blur for the camera.
	PostFrame Frame( CameraComponent camera, float frameTime )
	{
		var view = camera.WorldTransform;
		var eye = view.Position / Halo3Mount.WorldScale;
		var frame = _screenEffects.Evaluate( eye, view.Rotation.Forward );

		// MCC blurs unless the camera turned more than 60 degrees or moved more than 5 world units since the last frame, over the expected frame time / the last one's.
		var on = MotionBlurEnabled && _lastView is { } last
			&& last.Rotation.Forward.Dot( view.Rotation.Forward ) >= 0.5f && last.Rotation.Up.Dot( view.Rotation.Up ) >= 0.5f
			&& last.Position.Distance( view.Position ) <= 5 * Halo3Mount.WorldScale;
		_lastView = view;
		var scale = frameTime > 1e-4f ? _expectedFrameTime / frameTime : 1;

		// Under water (inside one of the structure design's water volumes), MCC fogs the frame and its LDR alpha of 1 stops the motion blur.
		var water = UnderwaterEnabled ? _water?.At( new System.Numerics.Vector3( eye.x, eye.y, eye.z ) ) : null;
		if ( water is not null )
			on = false;
		return frame with
		{
			Blur = _blur with { On = on, TimeScale = scale },
			Underwater = water is not null,
			Murkiness = water?.Murkiness ?? 0,
			FogColor = water is null ? default : new Vector3( water.Color.X, water.Color.Y, water.Color.Z ),
		};
	}

	void ReadMotionBlur( CacheFile cache )
	{
		var rasg = cache.FindTag( "globals\\rasterizer_globals", "rasg" );
		if ( rasg is null || rasg.Data.IsNull )
			return;
		var d = rasg.Data;
		_blur = new MotionBlurFrame( false, 1, Math.Clamp( d.I32( 0x88 ), 2, 20 ), new Vector2( d.F32( 0x8C ), d.F32( 0x90 ) ), new Vector2( d.F32( 0x94 ), d.F32( 0x98 ) ), d.F32( 0x9C ), Vector2.Zero );
		_expectedFrameTime = d.F32( 0xA0 ) > 0 ? d.F32( 0xA0 ) : 0.03f;
	}

	// Keeps the post-processing for the current settings on the scene's camera (which the editor's view runs too), rebuilt when the camera fx changes.
	void KeepPostFx( CameraComponent camera )
	{
		if ( !PostFxEnabled )
		{
			RemovePostFx();
			return;
		}

		var key = (_bloomSettings, Halo3Materials.CameraExposure( _cache ), Halo3PostFx.Debug);
		if ( camera == _postCamera && _postBuiltFor == key )
			return;

		RemovePostFx();
		_post = _postFx.Build( key.Item1, key.Item2, _exposureWeights, _distortion );
		_postBuiltFor = key;
		_postCamera = camera;
		if ( _post is null )
			return;

		camera.AddCommandList( _post, Stage.BeforePostProcess );
		SetHdr( true );
	}

	// What halo3_shader's water reads of the camera under water: x 1 under water (the surfaces are seen from below), y 1 to fog them as MCC (only with the post-processing on, whose units the fog colour is in), z murkiness; and the fog colour.
	public const string CameraUnderwaterAttribute = "Halo3CameraUnderwater";
	public const string UnderwaterFogAttribute = "Halo3UnderwaterFog";

	// MCC's underwater pass on the camera while it's inside a water volume (and the post-processing runs), and the water surfaces' view of it.
	void KeepUnderwater( CameraComponent camera, in PostFrame frame )
	{
		var fog = frame.Underwater && _post is not null;
		if ( !fog || camera != _underwaterCamera )
			RemoveUnderwater();
		if ( fog && _underwaterCamera is null && (_underwater ??= _postFx.BuildUnderwater()) is not null )
		{
			camera.AddCommandList( _underwater, Stage.AfterSkybox );
			_underwaterCamera = camera;
		}
		Scene.RenderAttributes.Set( CameraUnderwaterAttribute, new Vector4( frame.Underwater ? 1 : 0, fog ? 1 : 0, frame.Murkiness, 0 ) );
		Scene.RenderAttributes.Set( UnderwaterFogAttribute, frame.FogColor );
	}

	void RemoveUnderwater()
	{
		if ( _underwaterCamera.IsValid() )
			_underwaterCamera.RemoveCommandList( _underwater, Stage.AfterSkybox );
		_underwaterCamera = null;
	}

	// The materials' HDR output and exposure state (ShareHdrWithSky passes them on to the 3D skybox).
	void SetHdr( bool on )
	{
		Scene.RenderAttributes.Set( Halo3PostFx.HdrAttribute, on ? 1f : 0f );
		Scene.RenderAttributes.Set( Halo3PostFx.StateAttribute, on && _postFx.State is { } state ? (float)state.Index : -1f );
	}

	// The scene the engine's Skybox3D builds from its scene file, which has no public accessor.
	static readonly FieldInfo SkyboxSceneField = typeof( Skybox3D ).GetField( "skyboxScene", BindingFlags.Instance | BindingFlags.NonPublic );

	// The 3D skybox's draws see neither this scene's attributes nor their own scene's, only their renderers': without these the sky tone maps itself and the post-processing tone maps it again (Valhalla's sky washed out to white, its ring and far mountains gone).
	void ShareHdrWithSky()
	{
		var hdr = Scene.RenderAttributes.GetFloat( Halo3PostFx.HdrAttribute );
		var state = Scene.RenderAttributes.GetFloat( Halo3PostFx.StateAttribute, -1f );
		var probe = SkyProbe();
		foreach ( var skybox in Scene.GetAllComponents<Skybox3D>() )
		{
			if ( SkyboxSceneField?.GetValue( skybox ) is not Scene sky || !sky.IsValid() )
				continue;
			foreach ( var renderer in sky.GetAllComponents<ModelRenderer>() )
			{
				renderer.Attributes.Set( Halo3PostFx.HdrAttribute, hdr );
				renderer.Attributes.Set( Halo3PostFx.StateAttribute, state );
				// cook_torrance's area specular tables: without them the sky water's environment map took the Schlick stand-in, far darker at a grazing view than MCC's.
				if ( _cookTorranceCc is not null && _cookTorranceDd is not null )
				{
					renderer.Attributes.Set( "Halo3CookTorranceCc", _cookTorranceCc );
					renderer.Attributes.Set( "Halo3CookTorranceDd", _cookTorranceDd );
					renderer.Attributes.Set( "Halo3HasCookTorranceTables", 1f );
				}
				if ( probe is not null && renderer.Attributes.GetFloat( "Halo3HasProbe" ) < 0.5f )
					Halo3Lightmap.SetProbe( renderer.Attributes, probe );
				if ( IsSkyObject( renderer.GameObject ) )
				{
					// Objects hung on the sky (Standoff's dishes) are fogged as the level's objects are, at their true distance.
					Halo3Materials.SetSceneLook( renderer.Attributes, _cache, ScenarioObjectsBuilder.SkyboxScale );
					KeepSkyAnimation( renderer );
				}
			}
		}
	}

	static bool IsSkyObject( GameObject go )
	{
		for ( var g = go; g.IsValid(); g = g.Parent )
		{
			if ( g.Tags.Has( ScenarioObjectsBuilder.SkyObjectTag ) )
				return true;
		}
		return false;
	}

	readonly Dictionary<SkinnedModelRenderer, float> _skyAnimationRates = new();

	// The 3D skybox steps its scene more than once a frame (Standoff's dishes turned 2.4 times too fast), so a looping animation there is held still and set to the time this scene's clock says.
	void KeepSkyAnimation( ModelRenderer renderer )
	{
		if ( renderer is not SkinnedModelRenderer skinned || string.IsNullOrEmpty( skinned.Sequence.Name ) || !skinned.Sequence.Looping )
			return;
		if ( !_skyAnimationRates.TryGetValue( skinned, out var rate ) )
		{
			_skyAnimationRates[skinned] = rate = skinned.PlaybackRate;
			skinned.PlaybackRate = 0;
		}
		var duration = skinned.Sequence.Duration;
		if ( duration > 0 )
			skinned.Sequence.Time = (float)(_clock.Elapsed.TotalSeconds * rate % duration);
	}

	LightProbe _skyProbe;
	bool _skyProbeRead;

	// The sky object's lighting, for its meshes without baked vertex colours (Zanzibar's islands were dark silhouettes on the neutral ambient): MCC finds no lightmap under the sky's origin and lights it with the sky model's own SH (RenderModelTag.Lighting). The lightmap under the origin took Zanzibar's courtyard sand with a ground bounce, which greyed its far sea.
	LightProbe SkyProbe()
	{
		if ( _skyProbeRead )
			return _skyProbe;
		_skyProbeRead = true;
		try
		{
			_skyProbe = ScenarioObjectsBuilder.SkyLighting( new ScenarioTag( _cache.Scenario ) );
		}
		catch ( Exception e )
		{
			Halo3Mount.Current?.Logger.Warning( e, $"Couldn't light the Halo 3 sky: {e.Message}" );
		}
		return _skyProbe;
	}

	void RemovePostFx()
	{
		RemoveUnderwater();
		if ( _post is not null && _postCamera.IsValid() )
			_postCamera.RemoveCommandList( _post, Stage.BeforePostProcess );
		_post = null;
		_postCamera = null;
		_postBuiltFor = null;
		if ( Scene.IsValid() )
			SetHdr( false );
	}

	CameraComponent Camera() => Scene.Camera is { } main && main.IsValid() && main.Enabled
		? main
		: Scene.GetAllComponents<CameraComponent>().FirstOrDefault( c => c.IsValid() && c.Enabled );

	// Watching for Halo 3 scenes.

	// The hidden object holding a scene's upkeep, looked for by name: a component search leaves hidden objects out.
	const string ObjectName = "halo3 scene upkeep";

	static readonly object WatchLock = new();
	static System.Threading.Timer _watchTimer;
	static int _scanQueued;

	// The engine's set of every live scene, editor scenes included (Scene.All leaves those out).
	static readonly PropertyInfo AllScenesProperty = typeof( Scene ).GetProperty( "_all", BindingFlags.NonPublic | BindingFlags.Static );

	// Looks for live scenes with the marker twice a second. Safe to call more than once.
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
				if ( !scene.IsValid() || scene.Children.Any( g => g.Name == ObjectName ) || !scene.FindAllWithTag( MarkerTag ).Any() )
					continue;

				using ( scene.Push() )
				{
					var go = new GameObject( true, ObjectName );
					go.Flags |= GameObjectFlags.Hidden | GameObjectFlags.NotSaved | GameObjectFlags.NotNetworked;

					// The type library is the game's, which doesn't know mount types: a new component looks its type up there for required components and warns, unless it's being deserialized.
					go.Flags |= GameObjectFlags.Deserializing;
					var upkeep = go.Components.Create<Halo3SceneUpkeep>( false );
					go.Flags &= ~GameObjectFlags.Deserializing;
					upkeep.Flags |= ComponentFlags.Hidden | ComponentFlags.NotSaved | ComponentFlags.NotNetworked;
					upkeep.Enabled = true;
				}
			}
		}
		catch ( Exception e )
		{
			Halo3Mount.Current?.Logger.Warning( e, $"Couldn't add the Halo 3 scene upkeep: {e.Message}" );
		}
	}
}
