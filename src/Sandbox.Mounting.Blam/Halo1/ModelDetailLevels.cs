using System.Reflection;
using System.Runtime.CompilerServices;
using Sandbox.Internal;
using Blam.Halo1;
using Blam.Halo1.Tags;

namespace Sandbox.Mounting.Blam.Halo1;

/// <summary>
/// Draws every Halo model in a game scene at the level of detail Halo picks for it each frame, hides
/// it while it's too small on screen for its lowest level, and lets it cast its shadow only while
/// it's big enough to in Halo. The mount builds a model's levels as its LODs (see <see cref="Lod"/>).
/// </summary>
/// <remarks>
/// <para>An object's size on screen, in pixels, is its bounding sphere's diameter there: the
/// camera's focal length in pixels over the depth of the sphere's center along the view (no nearer
/// than 0.1 world units), times the diameter. The level drawn is the highest whose cutoff that
/// reaches, and below the super low level's cutoff the object isn't drawn at all.</para>
///
/// <para>Halo draws an object's shadow, with whatever it carries, only while the object is over
/// <see cref="MinimumShadowPixels"/>; its sun shadow here does the same.</para>
///
/// <para>Like <see cref="MovingObjectLights"/>, this can't be saved in a scene, so <see cref="Watch"/>
/// adds it to every live game scene with Halo models, on a hidden object that isn't saved. An
/// editor's scene is left alone: it's seen through the editor's camera.</para>
/// </remarks>
sealed class ModelDetailLevels : Component, Component.ExecuteInEditor, IPreRenderSubscriber
{
	/// <summary>The nearest a sphere's center counts as being to the camera, in world units.</summary>
	const float NearestDepth = 0.1f;

	/// <summary>The size on screen, in pixels, an object needs to cast its shadow.</summary>
	const float MinimumShadowPixels = 30;

	/// <summary>How many frames the renderers found are used for before looking again.</summary>
	const int RefreshFrames = 15;

	/// <summary>
	/// The model LOD a level of detail (0 super low to 4 super high) is built at: the level drawn at
	/// normal sizes is LOD 0, which a renderer nothing picks for draws, the lower levels follow it,
	/// and any higher ones come after those.
	/// </summary>
	public static int Lod( int level, int drawnLevel ) => level <= drawnLevel ? drawnLevel - level : level;

	/// <summary>The level of detail a model LOD draws (see <see cref="Lod"/>).</summary>
	public static int Level( int lod, int drawnLevel ) => lod <= drawnLevel ? drawnLevel - lod : lod;

	/// <param name="Cutoffs">The pixels each level needs, super low to super high.</param>
	/// <param name="Lods">The model LOD of each level.</param>
	/// <param name="Shape">The object's bounding sphere.</param>
	sealed record Levels( float[] Cutoffs, int[] Lods, ObjectLightingShape Shape );

	sealed class Tracked
	{
		public ModelRenderer Renderer;
		public Levels Levels;
		public int Seen;

		// The outermost Halo object carrying this one, whose size decides its shadow.
		public Tracked Carrier;

		// Its size on screen this frame, below 0 when it isn't drawn.
		public float Pixels;

		// Hidden for being too small.
		public bool Culled;

		// Its shadow turned off here for being too small.
		public bool ShadowOff;
	}

	readonly Dictionary<ModelRenderer, Tracked> _tracked = new();
	readonly List<Tracked> _renderers = new();
	readonly List<ModelRenderer> _gone = new();
	int _framesUntilRefresh;
	int _refresh;

	// This frame's view: the focal length in pixels, where the camera is and where it looks.
	float _focal;
	Vector3 _eye;
	Vector3 _forward;
	bool _viewed;

	protected override void OnEnabled()
	{
		RenderCulling.Cleared += OnCleared;
	}

	protected override void OnDisabled()
	{
		RenderCulling.Cleared -= OnCleared;
		_viewed = false;
		foreach ( var tracked in _tracked.Values )
			Release( tracked );
		_tracked.Clear();
		_renderers.Clear();
		_framesUntilRefresh = 0;
	}

	// IPreRenderSubscriber is what gets OnPreRender called (see MirrorReflections).
	protected override void OnPreRender()
	{
		if ( --_framesUntilRefresh <= 0 )
		{
			Refresh();
			_framesUntilRefresh = RefreshFrames;
		}

		var camera = Scene.Camera is { } main && main.IsValid() && main.Enabled
			? main
			: Scene.GetAllComponents<CameraComponent>().FirstOrDefault( c => c.IsValid() && c.Enabled );
		if ( camera is null )
			return;

		var size = camera.ScreenRect.Size;
		var halfSize = camera.FovAxis == CameraComponent.Axis.Vertical ? size.y * 0.5f : size.x * 0.5f;
		_focal = halfSize / MathF.Tan( camera.FieldOfView.DegreeToRadian() * 0.5f );
		_eye = camera.WorldPosition;
		_forward = camera.WorldRotation.Forward;
		_viewed = true;

		// What visibility hides, shadow and all, needs no level until it's shown again (see OnCleared).
		foreach ( var tracked in _renderers )
		{
			var renderer = tracked.Renderer;
			if ( !renderer.IsValid() || !renderer.Active || RenderCulling.IsCulledBy( renderer, RenderCulling.Reason.Visibility ) )
				tracked.Pixels = -1;
			else
				Pick( tracked );
		}

		foreach ( var tracked in _renderers )
		{
			if ( tracked.Pixels >= 0 )
				UpdateShadow( tracked );
		}
	}

	/// <summary>
	/// Picks a renderer's level, hides it while it's too small and sets its shadow at once when
	/// visibility shows it again, whether or not this frame's levels were picked yet.
	/// </summary>
	void OnCleared( ModelRenderer renderer, RenderCulling.Reason cleared, RenderCulling.Reason culled )
	{
		if ( (cleared & RenderCulling.Reason.Visibility) == 0 || !_viewed || renderer.Scene != Scene || !_tracked.TryGetValue( renderer, out var tracked ) || tracked.Levels is null )
			return;

		Pick( tracked );
		UpdateShadow( tracked );
	}

	// Picks the level a renderer draws at for its size on screen, and hides it below the lowest.
	void Pick( Tracked tracked )
	{
		var renderer = tracked.Renderer;
		var levels = tracked.Levels;
		var depth = MathF.Max( MathF.Abs( Vector3.Dot( Center( renderer, levels.Shape ) - _eye, _forward ) ) / HaloCeMount.WorldScale, NearestDepth );
		var pixels = _focal / depth * levels.Shape.Radius * renderer.WorldScale.x * 2;
		tracked.Pixels = pixels;

		var level = levels.Cutoffs.Length - 1;
		while ( level > 0 && pixels < levels.Cutoffs[level] )
			level--;
		if ( renderer.LodOverride != levels.Lods[level] )
			renderer.LodOverride = levels.Lods[level];

		// A renderer can make its scene object again, drawn, so a culled one is hidden again.
		var culled = pixels < levels.Cutoffs[0];
		if ( culled != tracked.Culled || (culled && renderer.SceneObject is { } so && so.IsValid() && so.RenderingEnabled) )
		{
			tracked.Culled = culled;
			RenderCulling.Set( renderer, RenderCulling.Reason.Size, culled );
		}
	}

	// Only a renderer casting its shadow as usual is changed; one a game set to cast none, or only a shadow, is left alone.
	static void UpdateShadow( Tracked tracked )
	{
		var renderer = tracked.Renderer;
		if ( renderer.RenderType != ModelRenderer.ShadowRenderType.On || renderer.SceneObject is not { } so || !so.IsValid() )
		{
			tracked.ShadowOff = false;
			return;
		}

		var pixels = tracked.Carrier is { Pixels: >= 0 } carrier ? carrier.Pixels : tracked.Pixels;
		if ( pixels <= MinimumShadowPixels )
		{
			// The renderer turns it back on whenever its body groups change.
			if ( so.Flags.CastShadows )
			{
				so.Flags.CastShadows = false;
				tracked.ShadowOff = true;
			}
		}
		else if ( tracked.ShadowOff )
		{
			so.Flags.CastShadows = true;
			tracked.ShadowOff = false;
		}
	}

	void Refresh()
	{
		_refresh++;
		_renderers.Clear();
		foreach ( var renderer in Scene.GetAllComponents<ModelRenderer>() )
		{
			if ( renderer.Model is not { } model || LevelsOf( model ) is not { } levels )
				continue;

			if ( !_tracked.TryGetValue( renderer, out var tracked ) )
				_tracked[renderer] = tracked = new Tracked { Renderer = renderer };
			tracked.Levels = levels;
			tracked.Seen = _refresh;
			_renderers.Add( tracked );
		}

		foreach ( var (renderer, tracked) in _tracked )
		{
			if ( tracked.Seen != _refresh )
				_gone.Add( renderer );
		}
		foreach ( var renderer in _gone )
		{
			Release( _tracked[renderer] );
			_tracked.Remove( renderer );
		}
		_gone.Clear();

		foreach ( var tracked in _renderers )
		{
			tracked.Carrier = null;
			for ( var parent = tracked.Renderer.GameObject.Parent; parent.IsValid() && parent is not Sandbox.Scene; parent = parent.Parent )
			{
				if ( parent.Components.Get<ModelRenderer>() is { } carrier && _tracked.TryGetValue( carrier, out var carrying ) )
					tracked.Carrier = carrying;
			}
		}
	}

	// Gives a renderer no longer looked after back what was changed on it.
	static void Release( Tracked tracked )
	{
		var renderer = tracked.Renderer;
		if ( !renderer.IsValid() )
			return;

		renderer.LodOverride = null;
		if ( tracked.Culled )
			RenderCulling.Set( renderer, RenderCulling.Reason.Size, false );
		if ( tracked.ShadowOff && renderer.RenderType == ModelRenderer.ShadowRenderType.On && renderer.SceneObject is { } so && so.IsValid() )
			so.Flags.CastShadows = true;
	}

	/// <summary>Halo's bounding sphere center: the root node's transform applied to the bounding offset.</summary>
	static Vector3 Center( ModelRenderer renderer, ObjectLightingShape shape )
	{
		var skinned = renderer as SkinnedModelRenderer;
		if ( skinned?.BoneMergeTarget is { } target && target.IsValid() )
			skinned = target;
		if ( MovingObjectLights.RootBone( skinned ) is { } root )
			return root.PointToWorld( (Vector3)shape.BoundingOffset * HaloCeMount.WorldScale );

		return renderer.WorldTransform.PointToWorld( (Vector3)shape.LocalCenter * HaloCeMount.WorldScale );
	}

	// The models: their levels of detail and bounding spheres.

	static readonly ConditionalWeakTable<Model, Levels> ByModel = new();
	static readonly Dictionary<string, Levels> ByName = new( StringComparer.OrdinalIgnoreCase );

	// What a model the mount didn't build for an object resolves to.
	static readonly Levels None = new( [], [], null );

	/// <summary>
	/// Remembers a built model's levels of detail and its object's bounding sphere.
	/// </summary>
	public static void Register( Model model, string name, GbxModelTag tag, ObjectLightingShape shape )
	{
		if ( model is null || shape is null )
			return;

		var cutoffs = tag.DetailCutoffs.ToArray();
		var lods = Enumerable.Range( 0, cutoffs.Length ).Select( level => Lod( level, tag.DrawnLevel ) ).ToArray();
		var levels = new Levels( cutoffs, lods, shape );
		ByModel.AddOrUpdate( model, levels );
		lock ( ByName )
			ByName[MovingObjectLights.NormalizedName( name )] = levels;
		Watch();
	}

	static Levels LevelsOf( Model model )
	{
		if ( !ByModel.TryGetValue( model, out var levels ) )
		{
			if ( !string.IsNullOrEmpty( model.Name ) )
			{
				lock ( ByName )
					levels = ByName.GetValueOrDefault( MovingObjectLights.NormalizedName( model.Name ) );
			}
			ByModel.AddOrUpdate( model, levels ?? None );
		}

		return levels == None ? null : levels;
	}

	// Watching for scenes with such models (see MirrorReflections).

	static readonly object WatchLock = new();
	static System.Threading.Timer _watchTimer;
	static int _scanQueued;

	static readonly PropertyInfo AllScenesProperty = typeof( Scene ).GetProperty( "_all", BindingFlags.NonPublic | BindingFlags.Static );

	/// <summary>Starts looking for live scenes with such models, twice a second. Safe to call more than once.</summary>
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
				if ( !scene.IsValid() || scene.IsEditor || scene.Components.GetAll<ModelDetailLevels>( FindMode.EverythingInSelfAndDescendants ).Any() || !scene.GetAllComponents<ModelRenderer>().Any( r => r.IsValid() && r.Model is { } model && LevelsOf( model ) is not null ) )
					continue;

				AddTo( scene );
			}
		}
		catch ( Exception e )
		{
			HaloCeMount.Current?.Logger.Warning( e, $"Couldn't add Halo model detail levels: {e.Message}" );
		}
	}

	static void AddTo( Scene scene )
	{
		using ( scene.Push() )
		{
			var go = new GameObject( true, "halo model detail levels" );
			go.Flags |= GameObjectFlags.Hidden | GameObjectFlags.NotSaved | GameObjectFlags.NotNetworked;

			// As in MirrorReflections: the game's type library doesn't know mount types.
			go.Flags |= GameObjectFlags.Deserializing;
			var levels = go.Components.Create<ModelDetailLevels>( false );
			go.Flags &= ~GameObjectFlags.Deserializing;
			levels.Flags |= ComponentFlags.Hidden | ComponentFlags.NotSaved | ComponentFlags.NotNetworked;
			levels.Enabled = true;
		}
	}
}
