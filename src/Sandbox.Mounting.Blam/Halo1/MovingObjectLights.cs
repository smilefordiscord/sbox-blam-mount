using System.Reflection;
using System.Runtime.CompilerServices;
using Sandbox.Internal;
using Blam.Halo1;
using Blam.Halo1.Tags;

namespace Sandbox.Mounting.Blam.Halo1;

/// <summary>
/// Lights the Halo models a game puts in a scene (vehicles, bipeds, weapons it spawns) the way
/// Halo lights moving objects, and hands each renderer its lighting through its attributes
/// (HaloObjectAmbient and the rest, see halo1_model.shader), which the model shader prefers over
/// everything but a placed object's row.
/// </summary>
/// <remarks>
/// <para>Every frame, for each renderer of a Halo model whose tint isn't a row of the
/// <see cref="ObjectLightingTable"/> (so not the level's own placed objects): the object's bounding
/// sphere center is its root bone's transform applied to its object's bounding offset (or the
/// renderer's HaloLightProbe attribute, if a game sets one), and Halo's lighting there is the ray
/// straight down from it (<see cref="ObjectLightingSampler"/>, kept from the last built map). Then,
/// as Halo does (<see cref="MovingObjectLighting"/>), the renderer's lighting steps toward that by
/// at most 0.03 per color channel and direction component per 1/30 s, only while the object moves,
/// and jumps straight to it when the renderer is new, comes back into the camera's view, jumps
/// more than <see cref="TeleportDistance"/> in a frame, or the level changes. Objects that don't
/// relight as they move in Halo (scenery, projectiles: see <see cref="ObjectLightingShape.Follows"/>)
/// keep the lighting they got first, unless they jump.</para>
///
/// <para>A placed object that relights as it moves keeps its row until it leaves where it was placed, then is lit here starting from its row (c10's marines teleported from the swamp into the lab); the shader still takes its change colors from the row.</para>
///
/// <para>A level with more than one BSP lights its objects as Halo does, against the BSP loaded
/// only (the scene's enabled objects tagged <c>bsp_&lt;index&gt;</c>, as a game shows one at a
/// time): campaign BSPs overlap in space (a10's cryo bay lies inside three), and a ray in the wrong
/// one finds another room's floor or ceiling. Its sampler is built for that BSP when it's loaded,
/// in the background; until it's ready, and for one-BSP levels whose sampler would hold more than
/// <see cref="MaximumSamplerSize"/>, the light volume is read on the CPU at the same center the way
/// the shader reads it.</para>
///
/// <para>Done on the CPU rather than in the shader so the result can't change from frame to frame
/// unless the object moves: the shader's own fallback reads the volume at a position it takes from
/// the draw's instance transform, and its row lookup decodes the draw's tint, neither of which has
/// been checked for skinned draws (Warthogs, Ghosts and bipeds are skinned).</para>
///
/// <para>Like <see cref="MirrorReflections"/>, this can't be saved in a scene, so <see cref="Watch"/>
/// adds it to every live scene with such renderers, on a hidden object that isn't saved. A game
/// does nothing: it spawns <c>mount://haloce/&lt;model&gt;.vmdl</c> with the tint left white.</para>
/// </remarks>
sealed class MovingObjectLights : Component, Component.ExecuteInEditor, IPreRenderSubscriber
{
	/// <summary>
	/// A renderer that moves farther than this in one frame (world units) has jumped (respawned,
	/// teleported) and takes its new lighting at once.
	/// </summary>
	const float TeleportDistance = 5;

	/// <summary>
	/// Moves smaller than this (s&amp;box units) don't count as moving.
	/// </summary>
	const float Still = 0.01f;

	// How far (world units) a placed object can be from where its row was worked out before it counts as moved, as its animation sways its root bone.
	const float PlacedTolerance = 0.25f;

	/// <summary>
	/// The most sampler memory kept for lighting moving objects; bigger levels use their light
	/// volume instead. Every multiplayer level fits (Death Island's is about 9 MB).
	/// </summary>
	const long MaximumSamplerSize = 64L << 20;

	public const string AmbientAttribute = "HaloObjectAmbient";
	const string Light0Attribute = "HaloObjectLight0";
	const string ToLight0Attribute = "HaloObjectToLight0";
	const string Light1Attribute = "HaloObjectLight1";
	const string ToLight1Attribute = "HaloObjectToLight1";
	const string ReflectionAttribute = "HaloObjectReflection";
	const string ProbeAttribute = "HaloLightProbe";

	sealed class Tracked
	{
		// What the renderer was found to be: its model's shape, and its tint's row while the table is unchanged.
		public Model Model;
		public int Shapes = -1;
		public ObjectLightingShape Shape;
		public Color Tint;
		public int TableVersion = -1;
		public bool HasRow;
		public ObjectLight RowLight;
		public Vector3 RowCenter;

		public int Frame;

		// Whether it's lit here, and how.
		public bool Lit;
		public ObjectLight Light;
		public ObjectLight? Applied;
		public Vector3 Center;
		public bool Visible;
		public int Level;

		// A placed object still where it was placed, lit by its row.
		public bool OnRow;
	}

	readonly Dictionary<ModelRenderer, Tracked> _tracked = new();
	readonly List<ModelRenderer> _gone = new();
	long _lastTimestamp;
	int _frame;

	// The level the last frame lit from.
	LevelLighting _level;
	string _levelMap;
	int _levelsVersion = -1;

	protected override void OnEnabled()
	{
		RenderCulling.Cleared += OnCleared;
	}

	protected override void OnDisabled()
	{
		RenderCulling.Cleared -= OnCleared;
		foreach ( var (renderer, tracked) in _tracked )
		{
			if ( tracked.Lit )
				Release( renderer );
		}
		_tracked.Clear();
		_level = null;
		_levelsVersion = -1;
	}

	// IPreRenderSubscriber is what gets OnPreRender called (see MirrorReflections).
	protected override void OnPreRender()
	{
		var map = MapOf( Scene );
		var level = _level = LevelOf( map );

		// The scene being played lights its objects from its own map's light volume, and from the
		// BSP it shows.
		if ( map is not null && Game.ActiveScene == Scene )
		{
			ObjectLightingTable.Activate( map );
			level?.Load( ActiveBspOf( Scene ) );
		}
		var timestamp = System.Diagnostics.Stopwatch.GetTimestamp();
		var seconds = _lastTimestamp == 0 ? 0 : Math.Clamp( (float)System.Diagnostics.Stopwatch.GetElapsedTime( _lastTimestamp, timestamp ).TotalSeconds, 0, 0.1f );
		_lastTimestamp = timestamp;
		_frame++;

		var camera = Scene.Camera is { } main && main.IsValid() && main.Enabled
			? main
			: Scene.GetAllComponents<CameraComponent>().FirstOrDefault( c => c.IsValid() && c.Enabled );
		var frustum = camera?.GetFrustum();

		if ( level is not null )
		{
			var table = ObjectLightingTable.Version;
			foreach ( var renderer in Scene.GetAllComponents<ModelRenderer>() )
			{
				if ( !renderer.IsValid() || !renderer.Active || renderer.Model is not { } model )
					continue;

				if ( !_tracked.TryGetValue( renderer, out var tracked ) )
					_tracked[renderer] = tracked = new Tracked();
				tracked.Frame = _frame;
				if ( !Resolve( renderer, tracked, model, table ) )
					continue;

				// Halo works an object's lighting out only while it's drawn. One that relights as
				// it moves takes its lighting as is when it's drawn again, so nothing is kept up
				// while it isn't; the rest still watch for a jump.
				var visible = !RenderCulling.IsCulled( renderer ) && (frustum is not { } f || f.IsInside( renderer.Bounds, true ));
				if ( !visible && tracked.Lit && tracked.Shape.Follows )
				{
					tracked.Visible = false;
					continue;
				}

				Update( renderer, tracked, level, visible, seconds );
			}
		}

		foreach ( var (renderer, tracked) in _tracked )
		{
			if ( tracked.Frame != _frame )
				_gone.Add( renderer );
		}

		foreach ( var renderer in _gone )
		{
			if ( _tracked.Remove( renderer, out var tracked ) && tracked.Lit )
				Release( renderer );
		}
		_gone.Clear();
	}

	/// <summary>
	/// Lights a renderer culling shows again at once, whether or not this frame's lighting has run.
	/// </summary>
	void OnCleared( ModelRenderer renderer, RenderCulling.Reason cleared, RenderCulling.Reason culled )
	{
		if ( culled == 0 && _level is { } level && renderer.Scene == Scene && _tracked.TryGetValue( renderer, out var tracked ) && tracked.Lit && tracked.Frame == _frame )
			Update( renderer, tracked, level, true, 0 );
	}

	/// <summary>
	/// Finds the renderer's shape and row again when its model, its tint or the table changes; false
	/// if it isn't lit here: not a Halo model's, or a placed object that keeps its row as it moves.
	/// </summary>
	static bool Resolve( ModelRenderer renderer, Tracked tracked, Model model, int table )
	{
		var shapes = ShapesVersion;
		if ( tracked.Model != model || tracked.Shapes != shapes )
		{
			tracked.Model = model;
			tracked.Shapes = shapes;
			tracked.Shape = ShapeOf( model );
		}

		var tint = renderer.Tint;
		if ( tracked.TableVersion != table || tracked.Tint != tint )
		{
			tracked.Tint = tint;
			tracked.TableVersion = table;
			tracked.HasRow = ObjectLightingTable.TryGetRow( tint, out tracked.RowLight, out var center );
			tracked.RowCenter = (Vector3)center * HaloCeMount.WorldScale;
		}

		if ( tracked.Shape is null || (tracked.HasRow && !tracked.Shape.Follows) )
		{
			if ( tracked.Lit )
				Release( renderer );
			tracked.Lit = false;
			return false;
		}
		return true;
	}

	void Update( ModelRenderer renderer, Tracked tracked, LevelLighting level, bool visible, float seconds )
	{
		var shape = tracked.Shape;
		var center = Center( renderer, shape );

		if ( !tracked.Lit )
		{
			// A placed object starts as if it had stood where it was placed all along, lit by its row.
			tracked.Lit = true;
			tracked.Applied = null;
			tracked.Light = tracked.HasRow ? tracked.RowLight : default;
			tracked.Center = tracked.HasRow ? tracked.RowCenter : default;
			tracked.Visible = tracked.HasRow;
			tracked.Level = tracked.HasRow ? level.Version : -1;
			tracked.OnRow = tracked.HasRow;
		}

		var moved = center.Distance( tracked.Center );
		if ( tracked.OnRow )
		{
			// Until it leaves its placed spot it keeps its row, whatever lights the level.
			tracked.Level = level.Version;
			tracked.Visible = visible;
			if ( moved <= PlacedTolerance * HaloCeMount.WorldScale )
				return;
			tracked.OnRow = false;
		}

		var jumped = tracked.Level != level.Version || moved > TeleportDistance * HaloCeMount.WorldScale;
		if ( jumped )
			tracked.Light = level.Light( center, shape );
		else if ( shape.Follows && moved > Still )
		{
			var desired = level.Light( center, shape );

			// Halo works an object's lighting out again only while it's drawn, and takes it as is
			// when it's drawn again after a frame or more out of view.
			tracked.Light = tracked.Visible
				? MovingObjectLighting.Step( tracked.Light, desired, MovingObjectLighting.MaximumChangePerSecond * seconds )
				: desired;
		}

		tracked.Center = center;
		tracked.Visible = visible;
		tracked.Level = level.Version;

		if ( tracked.Applied is { } applied && MovingObjectLighting.Difference( applied, tracked.Light ) < 1e-5f )
			return;

		var light = tracked.Light;
		var attributes = renderer.Attributes;
		// w = 2 tells the shader the renderer's tint is still its row, for the change colors.
		attributes.Set( AmbientAttribute, new Vector4( (Vector3)light.Ambient, tracked.HasRow ? 2 : 1 ) );
		attributes.Set( Light0Attribute, (Vector3)light.Light0 );
		attributes.Set( ToLight0Attribute, (Vector3)light.ToLight0 );
		attributes.Set( Light1Attribute, (Vector3)light.Light1 );
		attributes.Set( ToLight1Attribute, (Vector3)light.ToLight1 );
		attributes.Set( ReflectionAttribute, (Vector3)light.Reflection );
		tracked.Applied = light;
	}

	/// <summary>
	/// Takes a renderer's lighting away, so the shader goes back to its own.
	/// </summary>
	static void Release( ModelRenderer renderer )
	{
		if ( renderer.IsValid() )
			renderer.Attributes.Set( AmbientAttribute, Vector4.Zero );
	}

	/// <summary>
	/// Where the object's lighting ray starts (s&amp;box units): the game's HaloLightProbe if it set
	/// one, else Halo's bounding sphere center, its root node's transform applied to the bounding
	/// offset.
	/// </summary>
	internal static Vector3 Center( ModelRenderer renderer, ObjectLightingShape shape )
	{
		var probe = renderer.Attributes.GetVector4( ProbeAttribute );
		if ( probe.w > 0.5f )
			return new Vector3( probe.x, probe.y, probe.z );

		if ( RootBone( renderer as SkinnedModelRenderer ) is { } root )
			return root.PointToWorld( (Vector3)shape.BoundingOffset * HaloCeMount.WorldScale );

		return renderer.WorldTransform.PointToWorld( (Vector3)shape.LocalCenter * HaloCeMount.WorldScale );
	}

	/// <summary>
	/// A skinned renderer's root bone this frame, read alone: the whole skeleton's copy costs more
	/// than everything else done with it here.
	/// </summary>
	internal static Transform? RootBone( SkinnedModelRenderer renderer ) =>
		renderer is { SceneModel: { } model } && model.IsValid() && renderer.Model is { BoneCount: > 0 }
			? model.GetBoneWorldTransform( 0 )
			: null;

	// The level: what moving objects are lit from.

	/// <summary>
	/// The last built map's lighting for moving objects: Halo's own (the sampler of the whole level,
	/// or of the BSP loaded), or where there's none, the light volume and the map's average light.
	/// </summary>
	internal sealed class LevelLighting
	{
		readonly ObjectLightingSampler _levelSampler;
		readonly ObjectLightVolume _volume;
		readonly ObjectLight _fallback;
		readonly CacheFile _cache;

		/// <summary>The map the lighting is of.</summary>
		public CacheFile Cache => _cache;
		readonly IReadOnlyList<StructureBspReference> _bsps;

		int _version;
		int _loadedBsp = -1;
		ObjectLightingSampler _bspSampler;

		/// <param name="bsps">The level's BSPs, each lit on its own while it's loaded; null to light against <paramref name="sampler"/>.</param>
		public LevelLighting( ObjectLightingSampler sampler, ObjectLightVolume volume, ObjectLight fallback, CacheFile cache, IReadOnlyList<StructureBspReference> bsps )
		{
			_levelSampler = sampler;
			_volume = volume;
			_fallback = fallback;
			_cache = cache;
			_bsps = bsps;
			_version = NextVersion();
		}

		/// <summary>Changes whenever what lights the objects does, so they take the new lighting at once. Read and changed on the main thread.</summary>
		public int Version => _version;

		ObjectLightingSampler Sampler => _bsps is null ? _levelSampler : _bspSampler;

		/// <summary>
		/// The BSP the scene shows (its index in the scenario), whose sampler is built in the
		/// background if it isn't the one loaded. Nothing for a level lit as a whole, or no BSP shown.
		/// </summary>
		public void Load( int bsp )
		{
			if ( _bsps is null || bsp < 0 || bsp == _loadedBsp || Find( bsp ) is not { } reference )
				return;

			_loadedBsp = bsp;
			_bspSampler = null;
			_version = NextVersion();

			var cache = _cache;
			System.Threading.Tasks.Task.Run( () =>
			{
				ObjectLightingSampler sampler;
				try
				{
					sampler = new ObjectLightingSampler( cache, [new StructureBspTag( cache, reference )] );
				}
				catch ( Exception e )
				{
					HaloCeMount.Current?.Logger.Warning( e, $"Couldn't light objects in {reference.Tag?.Path}: {e.Message}" );
					return;
				}

				MainThread.Queue( () =>
				{
					if ( _loadedBsp != bsp )
						return;
					_bspSampler = sampler;
					_version = NextVersion();
				} );
			} );
		}

		StructureBspReference Find( int bsp )
		{
			foreach ( var reference in _bsps )
			{
				if ( reference.Index == bsp )
					return reference;
			}
			return null;
		}

		/// <param name="center">s&amp;box units.</param>
		public ObjectLight Light( Vector3 center, ObjectLightingShape shape )
		{
			var point = (System.Numerics.Vector3)(center / HaloCeMount.WorldScale);
			if ( Sampler is { } sampler )
				return sampler.Light( shape.Request( point ) );

			return _volume is not null && _volume.TryLight( point, out var light ) ? light : _fallback;
		}

		/// <summary>
		/// The light and ground color a particle born at <paramref name="position"/> (s&amp;box units)
		/// gets (see <see cref="ParticleLighting"/>). Without the sampler, worked back from the light
		/// volume's lighting there.
		/// </summary>
		public (System.Numerics.Vector3 Light, System.Numerics.Vector3 Diffuse) ParticleGround( Vector3 position )
		{
			var point = (System.Numerics.Vector3)(position / HaloCeMount.WorldScale);
			if ( Sampler is { } sampler )
				return ParticleLighting.At( sampler, point );

			return _volume is not null && _volume.TryLight( point, out var light )
				? ParticleLighting.FromObjectLight( light )
				: (ParticleLighting.Grey, ParticleLighting.Grey);
		}
	}

	static int NextVersion() => System.Threading.Interlocked.Increment( ref _levelVersion );

	/// <summary>The lighting of a map (see <see cref="LevelForMap"/>), found again only when the map or the levels change.</summary>
	LevelLighting LevelOf( string map )
	{
		var levels = System.Threading.Volatile.Read( ref _levelsChanged );
		if ( levels == _levelsVersion && string.Equals( map, _levelMap, StringComparison.OrdinalIgnoreCase ) )
			return _level;

		_levelsVersion = levels;
		_levelMap = map;
		return LevelForMap( map );
	}

	/// <summary>The structure BSP a scene shows: the index in the first enabled object's <c>bsp_&lt;index&gt;</c> tag, or -1.</summary>
	static int ActiveBspOf( Scene scene )
	{
		var entry = SceneBsps.GetOrCreateValue( scene );
		var now = RealTime.Now;
		if ( now - entry.CheckedAt < BspCheckInterval )
			return entry.Bsp;
		entry.CheckedAt = now;

		// Which of the tagged objects is enabled is looked at often; they're found again when one
		// goes, or now and then while none is enabled.
		if ( !entry.Tagged.TrueForAll( tagged => tagged.Object.IsValid() ) || (FirstEnabled( entry.Tagged ) < 0 && now - entry.FoundAt >= BspFindInterval) )
		{
			entry.FoundAt = now;
			entry.Tagged.Clear();
			foreach ( var go in scene.GetAllObjects( false ) )
			{
				if ( BspOf( go ) is var bsp && bsp >= 0 )
					entry.Tagged.Add( (go, bsp) );
			}
		}

		return entry.Bsp = FirstEnabled( entry.Tagged );
	}

	static int FirstEnabled( List<(GameObject Object, int Bsp)> tagged )
	{
		foreach ( var (go, bsp) in tagged )
		{
			if ( go.Active )
				return bsp;
		}
		return -1;
	}

	static int BspOf( GameObject go )
	{
		foreach ( var tag in go.Tags.TryGetAll() )
		{
			if ( tag.StartsWith( BspTagPrefix, StringComparison.OrdinalIgnoreCase ) && int.TryParse( tag.AsSpan( BspTagPrefix.Length ), out var bsp ) )
				return bsp;
		}
		return -1;
	}

	/// <summary>What ScenarioSceneLoader tags each BSP's objects with, before the BSP's index.</summary>
	const string BspTagPrefix = "bsp_";

	/// <summary>How often (seconds) a scene is looked over for the BSP it shows, and for the objects tagged with one while none is enabled.</summary>
	const double BspCheckInterval = 0.25;
	const double BspFindInterval = 2;

	static readonly ConditionalWeakTable<Scene, BspOfScene> SceneBsps = new();

	sealed class BspOfScene
	{
		public int Bsp = -1;
		public double CheckedAt = double.MinValue;
		public double FoundAt = double.MinValue;
		public readonly List<(GameObject Object, int Bsp)> Tagged = new();
	}

	static readonly object LevelLock = new();
	static readonly Dictionary<string, LevelLighting> _levels = new( StringComparer.OrdinalIgnoreCase );
	static string _lastMap;
	static int _levelVersion;

	// Changes whenever a map's lighting is kept or dropped.
	static int _levelsChanged;

	/// <summary>
	/// The tag a map's scene carries, with <c>halo_map_&lt;map name&gt;</c> beside it (see
	/// ScenarioSceneLoader), so the lighting follows the scene's map.
	/// </summary>
	public const string MapTag = "halo_map";

	/// <summary>
	/// The lighting of the last map built (for scenes that aren't a map's, see <see cref="LevelFor"/>).
	/// </summary>
	internal static LevelLighting Level => LevelForMap( null );

	/// <summary>
	/// The lighting of a scene's map: the map its <see cref="MapTag"/> names, else the last map built.
	/// </summary>
	internal static LevelLighting LevelFor( Scene scene ) => LevelForMap( MapOf( scene ) );

	static LevelLighting LevelForMap( string map )
	{
		lock ( LevelLock )
		{
			if ( map is not null && _levels.TryGetValue( map, out var level ) )
				return level;

			return _lastMap is not null ? _levels.GetValueOrDefault( _lastMap ) : null;
		}
	}

	static readonly ConditionalWeakTable<Scene, MapOfScene> SceneMaps = new();

	sealed class MapOfScene
	{
		public string Map;
		public GameObject Marker;
		public double CheckedAt = double.MinValue;
	}

	/// <summary>
	/// The map a scene was built from (its <see cref="MapTag"/>), or null. While the object found
	/// carrying it stays, that's the map; otherwise it's looked for again every couple of seconds,
	/// for a game that loads a map into a scene of its own.
	/// </summary>
	internal static string MapOf( Scene scene )
	{
		if ( !scene.IsValid() )
			return null;

		var entry = SceneMaps.GetOrCreateValue( scene );
		if ( entry.Marker.IsValid() && entry.Marker.Active )
			return entry.Map;

		var now = RealTime.Now;
		if ( now - entry.CheckedAt < 2 )
			return entry.Map;

		entry.CheckedAt = now;
		entry.Map = null;
		entry.Marker = null;
		foreach ( var go in scene.GetAllObjects( true ) )
		{
			if ( !go.Tags.Has( MapTag, false ) )
				continue;

			var tag = go.Tags.TryGetAll().FirstOrDefault( t => t.StartsWith( MapTag + "_", StringComparison.OrdinalIgnoreCase ) );
			if ( tag is not null )
			{
				entry.Map = tag[(MapTag.Length + 1)..];
				entry.Marker = go;
				break;
			}
		}

		return entry.Map;
	}

	/// <summary>
	/// Keeps a built map's lighting for moving objects, makes it the last built, and starts adding
	/// the lighting to scenes. Nothing for a map whose objects couldn't be lit. A map of more than
	/// one BSP lights them against the BSP loaded, from its own sampler.
	/// </summary>
	public static void SetLevel( string map, ObjectLightingSampler sampler, ObjectLightVolume volume, ObjectLight fallback, CacheFile cache, IReadOnlyList<StructureBspReference> bsps )
	{
		var perBsp = bsps.Count > 1;
		var keep = !perBsp && sampler is not null && sampler.ApproximateSize <= MaximumSamplerSize;
		LevelLighting level = null;
		lock ( LevelLock )
		{
			if ( sampler is null && volume is null )
			{
				_levels.Remove( map );
			}
			else
			{
				level = new LevelLighting( keep ? sampler : null, keep ? null : volume, fallback, cache, perBsp ? bsps : null );
				_levels[map] = level;
			}

			_lastMap = map;
			_levelsChanged++;
		}

		if ( level is not null )
		{
			Watch();
			HaloParticleWorld.Watch();
		}
	}

	// The models: which object each Halo model belongs to.

	static readonly ConditionalWeakTable<Model, ObjectLightingShape> Shapes = new();
	static readonly Dictionary<string, ObjectLightingShape> ShapesByName = new( StringComparer.OrdinalIgnoreCase );

	// What other models were found to be by name, until another model is registered.
	sealed record NamedShape( ObjectLightingShape Shape, int Version );
	static readonly ConditionalWeakTable<Model, NamedShape> ShapesFoundByName = new();
	static int _shapesVersion;

	/// <summary>Changes whenever a model is registered, so what <see cref="ShapeOf"/> found can be kept until then.</summary>
	internal static int ShapesVersion => System.Threading.Volatile.Read( ref _shapesVersion );

	/// <summary>
	/// Remembers which object's shape lights a built Halo model (by the model, and by its name in
	/// case the model a renderer holds is another instance of it).
	/// </summary>
	public static void Register( Model model, string name, ObjectLightingShape shape )
	{
		if ( model is null || shape is null )
			return;

		Shapes.AddOrUpdate( model, shape );
		lock ( ShapesByName )
			ShapesByName[NormalizedName( name )] = shape;
		System.Threading.Interlocked.Increment( ref _shapesVersion );
	}

	internal static ObjectLightingShape ShapeOf( Model model )
	{
		if ( Shapes.TryGetValue( model, out var shape ) )
			return shape;

		if ( string.IsNullOrEmpty( model.Name ) )
			return null;

		var version = ShapesVersion;
		if ( ShapesFoundByName.TryGetValue( model, out var found ) && found.Version == version )
			return found.Shape;

		lock ( ShapesByName )
			shape = ShapesByName.GetValueOrDefault( NormalizedName( model.Name ) );
		ShapesFoundByName.AddOrUpdate( model, new NamedShape( shape, version ) );
		return shape;
	}

	internal static string NormalizedName( string name )
	{
		name = name.Replace( '\\', '/' );
		var scheme = name.IndexOf( "://", StringComparison.Ordinal );
		if ( scheme >= 0 )
		{
			// mount://haloce/<path>: drop the mount's ident too.
			name = name[(scheme + 3)..];
			var slash = name.IndexOf( '/' );
			name = slash >= 0 ? name[(slash + 1)..] : name;
		}

		name = name.TrimStart( '/' );
		return name.EndsWith( ".vmdl", StringComparison.OrdinalIgnoreCase ) ? name[..^5] : name;
	}

	static bool NeedsLighting( ModelRenderer renderer ) =>
		renderer.IsValid() && renderer.Model is { } model && ShapeOf( model ) is { } shape && (shape.Follows || !ObjectLightingTable.IsRow( renderer.Tint ));

	// Watching for scenes with Halo models a game put there (see MirrorReflections).

	static readonly object WatchLock = new();
	static System.Threading.Timer _watchTimer;
	static int _scanQueued;

	static readonly PropertyInfo AllScenesProperty = typeof( Scene ).GetProperty( "_all", BindingFlags.NonPublic | BindingFlags.Static );

	/// <summary>
	/// Starts looking for live scenes with Halo models to light, twice a second. Safe to call more
	/// than once.
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
		if ( Level is null )
			return;

		try
		{
			var scenes = AllScenesProperty?.GetValue( null ) is System.Collections.IEnumerable all ? all.OfType<Scene>().ToList() : Scene.All.ToList();
			foreach ( var scene in scenes )
			{
				if ( !scene.IsValid() || scene.GetAllComponents<MovingObjectLights>().Any() || !scene.GetAllComponents<ModelRenderer>().Any( NeedsLighting ) )
					continue;

				AddTo( scene );
			}
		}
		catch ( Exception e )
		{
			HaloCeMount.Current?.Logger.Warning( e, $"Couldn't add Halo object lighting: {e.Message}" );
		}
	}

	static void AddTo( Scene scene )
	{
		using ( scene.Push() )
		{
			var go = new GameObject( true, "halo moving object lights" );
			go.Flags |= GameObjectFlags.Hidden | GameObjectFlags.NotSaved | GameObjectFlags.NotNetworked;

			// As in MirrorReflections: the game's type library doesn't know mount types.
			go.Flags |= GameObjectFlags.Deserializing;
			var lights = go.Components.Create<MovingObjectLights>( false );
			go.Flags &= ~GameObjectFlags.Deserializing;
			lights.Flags |= ComponentFlags.Hidden | ComponentFlags.NotSaved | ComponentFlags.NotNetworked;
			lights.Enabled = true;
		}
	}
}
