using System.Reflection;
using System.Runtime.CompilerServices;
using Blam.Halo1;
using Blam.Halo1.Tags;
using Sandbox.Internal;

namespace Sandbox.Mounting.Blam.Halo1;

/// <summary>
/// One structure BSP's surfaces as visibility draws them: its visibility, and for each material
/// the mesh that draws the material's visible surfaces.
/// </summary>
sealed class BspDrawing
{
	public BspDrawing( BspVisibility data, int surfaceCount )
	{
		View = new PortalVisibility( data );
		SurfaceVisible = new bool[surfaceCount];
		Subclusters = new bool[View.SubclusterCount];
		MirrorSubclusters = new bool[View.SubclusterCount];
		Shown = new bool[View.SubclusterCount];
		ClustersDrawn = new bool[data.Clusters.Count];
		_marking = new bool[data.Clusters.Count];
	}

	public PortalVisibility View { get; }

	PortalVisibility _mirrorView;

	/// <summary>The view of a mirror's reflection, from the mirror's cluster.</summary>
	public PortalVisibility MirrorView => _mirrorView ??= new PortalVisibility( View.Data );

	/// <summary>The cluster the camera was last in, kept while it's in solid geometry, as Halo does.</summary>
	public int CameraCluster = -1;

	public readonly bool[] SurfaceVisible;
	public readonly bool[] Subclusters;
	public readonly bool[] MirrorSubclusters;

	// The subclusters the meshes draw, and whether they draw everything.
	public readonly bool[] Shown;
	public bool ShowingAll = true;

	/// <summary>The clusters the view draws (the mirror's view too), and a count of the times they changed.</summary>
	public readonly bool[] ClustersDrawn;
	public int ClustersVersion;
	readonly bool[] _marking;

	/// <summary>Marks the clusters the last view draws, and the mirror's view's too if it has one.</summary>
	public void MarkClusters( bool mirrored )
	{
		Array.Clear( _marking );
		Mark( View.RenderedClusters );
		if ( mirrored )
			Mark( MirrorView.RenderedClusters );

		if ( _marking.AsSpan().SequenceEqual( ClustersDrawn ) )
			return;

		Array.Copy( _marking, ClustersDrawn, ClustersDrawn.Length );
		ClustersVersion++;
	}

	void Mark( IReadOnlyList<int> clusters )
	{
		for ( var i = 0; i < clusters.Count; i++ )
			_marking[clusters[i]] = true;
	}

	/// <summary>
	/// Marks the surfaces of the marked subclusters.
	/// </summary>
	public void MarkSurfaces()
	{
		Array.Clear( SurfaceVisible );
		var clusters = View.Data.Clusters;
		for ( var c = 0; c < clusters.Count; c++ )
		{
			var start = View.SubclusterStart[c];
			var subclusters = clusters[c].Subclusters;
			for ( var i = 0; i < subclusters.Length; i++ )
			{
				if ( !Subclusters[start + i] )
					continue;

				foreach ( var surface in subclusters[i].Surfaces )
				{
					if ( (uint)surface < (uint)SurfaceVisible.Length )
						SurfaceVisible[surface] = true;
				}
			}
		}
	}

	/// <summary>
	/// The subclusters holding any of a run of surfaces (numbered as <see cref="PortalVisibility.SubclusterStart"/>).
	/// </summary>
	public int[] SubclustersOf( int first, int count )
	{
		var found = new List<int>();
		var clusters = View.Data.Clusters;
		for ( var c = 0; c < clusters.Count; c++ )
		{
			var subclusters = clusters[c].Subclusters;
			for ( var i = 0; i < subclusters.Length; i++ )
			{
				foreach ( var surface in subclusters[i].Surfaces )
				{
					if ( surface >= first && surface < first + count )
					{
						found.Add( View.SubclusterStart[c] + i );
						break;
					}
				}
			}
		}
		return found.ToArray();
	}

	/// <summary>Whether any of these subclusters is marked.</summary>
	public bool AnyMarked( int[] subclusters )
	{
		foreach ( var subcluster in subclusters )
		{
			if ( Subclusters[subcluster] )
				return true;
		}
		return false;
	}
}

/// <summary>
/// A material's surfaces in a structure BSP and the mesh drawing them, whose index buffer holds the
/// visible ones.
/// </summary>
sealed class BspMaterialSurfaces( BspDrawing bsp, Mesh mesh, int firstSurface, int[] indices )
{
	readonly VisibleIndices _indices = new( mesh, indices );

	public BspDrawing Bsp { get; } = bsp;
	public int FirstSurface { get; } = firstSurface;
	public int SurfaceCount => indices.Length / 3;

	/// <summary>Index count drawn now; 0 hides the renderer.</summary>
	public int Count => _indices.Count;

	/// <summary>Draws the surfaces the BSP marks visible.</summary>
	public void Update()
	{
		var visible = Bsp.SurfaceVisible;
		var building = _indices.Building;
		var count = 0;
		for ( var t = 0; t < SurfaceCount; t++ )
		{
			var surface = FirstSurface + t;
			if ( (uint)surface >= (uint)visible.Length || !visible[surface] )
				continue;

			building[count++] = indices[t * 3];
			building[count++] = indices[t * 3 + 1];
			building[count++] = indices[t * 3 + 2];
		}

		_indices.Draw( count );
	}

	/// <summary>Draws every surface.</summary>
	public void DrawAll() => _indices.DrawAll();
}

/// <summary>
/// A structure BSP's surfaces drawn by an object of their own (water, glass, surfaces drawn before
/// water, breakable surfaces): one material's, shown while any of them is visible.
/// </summary>
sealed class BspSeparateSurfaces( BspDrawing bsp, int firstSurface, int surfaceCount, int mirrorPlane )
{
	int[] _subclusters;

	public BspDrawing Bsp { get; } = bsp;
	public int MirrorPlane { get; } = mirrorPlane;

	/// <summary>Whether a subcluster holding any of the surfaces is marked.</summary>
	public bool Visible => Bsp.AnyMarked( _subclusters ??= Bsp.SubclustersOf( firstSurface, surfaceCount ) );
}

/// <summary>
/// Draws what Halo's visibility draws of what the engine's own visibility can't cull (see
/// <see cref="SourceVisibility"/>, which culls the BSP's opaque cluster models and every object):
/// the translucent surfaces of the clusters the camera's cluster can see (see <see cref="PortalVisibility"/>).
/// Which way the camera faces is left to s&amp;box's frustum culling.
/// </summary>
/// <remarks>
/// <para>A translucent material is one model bounded by the whole BSP (see <see cref="StructureBspModel"/>),
/// whose index buffer is rewritten with the visible surfaces when they change, as Halo builds each material's
/// surface list per frame; a material with none visible is hidden.</para>
///
/// <para>A dynamic mirror whose glass is visible adds what its cluster can see, for the reflection.
/// Only the camera of a game being played is followed; other scenes (the editor's) draw
/// everything.</para>
///
/// <para>A BSP's detail objects are drawn for the cells around the camera's (see
/// <see cref="DetailObjectCells"/>), and the level's decals for the clusters the view draws (see
/// <see cref="DecalClusters"/>), their meshes' index buffers rewritten when those change.</para>
///
/// <para>Renderers are hidden through <see cref="RenderCulling"/>. Added to live scenes the way
/// <see cref="MirrorReflections"/> is.</para>
/// </remarks>
sealed class ClusterVisibility : Component, Component.ExecuteInEditor, IPreRenderSubscriber
{
	/// <summary>How many frames the renderers found are used for before looking again.</summary>
	const int RefreshFrames = 30;

	/// <summary>How many refreshes the mirror planes found are used for before looking again.</summary>
	const int MirrorRefreshes = 10;

	readonly Dictionary<BspDrawing, (List<(ModelRenderer Renderer, BspMaterialSurfaces Surfaces)> Materials, List<(ModelRenderer Renderer, BspSeparateSurfaces Surfaces)> Separate, List<ModelRenderer> Clusters)> _bsps = new();
	readonly List<(int Plane, Vector3 Position, Vector3 Normal)> _mirrors = new();
	readonly List<GameObject> _mirrorMarkers = new();
	int _refreshesUntilMirrors;
	readonly List<(ModelRenderer Renderer, DetailObjectCells Cells)> _detailObjects = new();
	readonly List<(ModelRenderer Renderer, DecalClusters Decals)> _decals = new();

	// Refresh's working set, kept between refreshes.
	readonly List<BspDrawing> _goneBsps = new();
	int _framesUntilRefresh;
	bool _culling;

	// Whether the renderers were just found again, so each one's culling is set whether or not it changed.
	bool _refreshed;

	protected override void OnDisabled()
	{
		ShowAll();
		_bsps.Clear();
		_detailObjects.Clear();
		_decals.Clear();
		_mirrors.Clear();
		_mirrorMarkers.Clear();
		_refreshesUntilMirrors = 0;
	}

	// IPreRenderSubscriber is what gets OnPreRender called (see MirrorReflections).
	protected override void OnPreRender()
	{
		var camera = Scene.Camera;
		if ( !Game.IsPlaying || Game.ActiveScene != Scene || camera is null || !camera.IsValid() || !camera.Enabled )
		{
			ShowAll();
			return;
		}

		if ( --_framesUntilRefresh <= 0 )
		{
			Refresh( camera );
			_framesUntilRefresh = RefreshFrames;
			_refreshed = true;
		}

		_culling = true;
		var position = Halo( camera.WorldPosition );
		BspDrawing primary = null;
		var mirrored = false;
		foreach ( var (bsp, renderers) in _bsps )
		{
			if ( !AnyActive( renderers.Materials ) && !renderers.Clusters.Any( r => r.IsValid() && r.Active ) )
				continue;

			var cluster = bsp.View.FindCluster( position );
			if ( cluster >= 0 )
				bsp.CameraCluster = cluster;
			bsp.View.Compute( bsp.CameraCluster );
			bsp.View.MarkSubclusters( bsp.Subclusters );

			var mirror = ComputeMirror( bsp, renderers.Separate, position );
			if ( mirror )
			{
				for ( var i = 0; i < bsp.Subclusters.Length; i++ )
					bsp.Subclusters[i] |= bsp.MirrorSubclusters[i];
			}

			ShowSurfaces( bsp, renderers.Materials, renderers.Separate );
			bsp.MarkClusters( mirror );

			if ( primary is null || (primary.CameraCluster < 0 && bsp.CameraCluster >= 0) )
			{
				primary = bsp;
				mirrored = mirror;
			}
		}

		_ = mirrored;
		ShowDetailObjects( position );
		ShowDecals( primary );
		_refreshed = false;
	}

	/// <summary>Shows the detail objects of the cells around the camera's.</summary>
	void ShowDetailObjects( System.Numerics.Vector3 camera )
	{
		var cell = DetailObjectCells.CellOf( camera );
		foreach ( var (renderer, cells) in _detailObjects )
		{
			if ( !renderer.IsValid() || !renderer.Active )
				continue;

			var was = cells.Count;
			cells.ShowAround( cell );
			if ( _refreshed || (was == 0) != (cells.Count == 0) )
				RenderCulling.Set( renderer, RenderCulling.Reason.Visibility, cells.Count == 0 );
		}
	}

	/// <summary>Shows the decals in the clusters the BSP's view draws, or all of them without an active BSP or with the camera outside it.</summary>
	void ShowDecals( BspDrawing bsp )
	{
		foreach ( var (renderer, decals) in _decals )
		{
			if ( !renderer.IsValid() )
				continue;

			var was = decals.Count;
			if ( bsp is null || bsp.View.CameraCluster < 0 )
				decals.DrawAll();
			else
				decals.Show( bsp );
			if ( _refreshed || (was == 0) != (decals.Count == 0) )
				RenderCulling.Set( renderer, RenderCulling.Reason.Visibility, decals.Count == 0 );
		}
	}

	static bool AnyActive( List<(ModelRenderer Renderer, BspMaterialSurfaces Surfaces)> materials )
	{
		foreach ( var (renderer, _) in materials )
		{
			if ( renderer.IsValid() && renderer.Active )
				return true;
		}
		return false;
	}

	static System.Numerics.Vector3 Halo( Vector3 v ) => new( v.x / HaloCeMount.WorldScale, v.y / HaloCeMount.WorldScale, v.z / HaloCeMount.WorldScale );
	static System.Numerics.Vector3 Numerics( Vector3 v ) => new( v.x, v.y, v.z );

	/// <summary>
	/// Works out what the cluster of the first dynamic mirror whose glass is visible sees, into
	/// <see cref="BspDrawing.MirrorSubclusters"/>; false if there's none.
	/// </summary>
	bool ComputeMirror( BspDrawing bsp, List<(ModelRenderer Renderer, BspSeparateSurfaces Surfaces)> separate, System.Numerics.Vector3 camera )
	{
		foreach ( var (plane, position, normal) in _mirrors )
		{
			var seen = false;
			foreach ( var (renderer, surfaces) in separate )
			{
				if ( surfaces.MirrorPlane == plane && renderer.IsValid() && renderer.Active && surfaces.Visible )
				{
					seen = true;
					break;
				}
			}
			if ( !seen )
				continue;

			var n = Numerics( normal );
			var point = Halo( position );
			var side = System.Numerics.Vector3.Dot( n, camera - point );
			if ( MathF.Abs( side ) < 0.0001f )
				continue;

			// The mirror's cluster: the one on the camera's side of its plane.
			var cluster = bsp.View.FindCluster( point + n * MathF.CopySign( 0.01f, side ) );
			if ( cluster < 0 )
				continue;

			bsp.MirrorView.Compute( cluster );
			bsp.MirrorView.MarkSubclusters( bsp.MirrorSubclusters );
			return true;
		}

		return false;
	}

	void ShowSurfaces( BspDrawing bsp, List<(ModelRenderer Renderer, BspMaterialSurfaces Surfaces)> materials, List<(ModelRenderer Renderer, BspSeparateSurfaces Surfaces)> separate )
	{
		var changed = bsp.ShowingAll || !bsp.Subclusters.AsSpan().SequenceEqual( bsp.Shown );
		if ( changed )
		{
			Array.Copy( bsp.Subclusters, bsp.Shown, bsp.Shown.Length );
			bsp.ShowingAll = false;
			bsp.MarkSurfaces();
			foreach ( var (_, surfaces) in materials )
				surfaces.Update();
		}

		if ( !changed && !_refreshed )
			return;

		foreach ( var (renderer, surfaces) in materials )
			RenderCulling.Set( renderer, RenderCulling.Reason.Visibility, surfaces.Count == 0 );

		foreach ( var (renderer, surfaces) in separate )
			RenderCulling.Set( renderer, RenderCulling.Reason.Visibility, !surfaces.Visible );
	}

	/// <summary>
	/// Finds the BSPs' renderers and the mirror planes.
	/// </summary>
	void Refresh( CameraComponent camera )
	{
		_ = camera;
		foreach ( var (materials, separate, clusters) in _bsps.Values )
		{
			materials.Clear();
			separate.Clear();
			clusters.Clear();
		}
		_detailObjects.Clear();
		_decals.Clear();

		foreach ( var renderer in Scene.GetAllComponents<ModelRenderer>() )
		{
			if ( !renderer.IsValid() || renderer.Model is not { } model )
				continue;

			if ( MaterialSurfaces.TryGetValue( model, out var surfaces ) )
				Renderers( surfaces.Bsp ).Materials.Add( (renderer, surfaces) );
			else if ( SeparateSurfaces.TryGetValue( model, out var separate ) )
				Renderers( separate.Bsp ).Separate.Add( (renderer, separate) );
			else if ( ClusterModels.TryGetValue( model, out var drawing ) )
				Renderers( drawing ).Clusters.Add( renderer );
			else if ( DetailObjectMeshes.TryGetValue( model, out var cells ) )
				_detailObjects.Add( (renderer, cells) );
			else if ( DecalMeshes.TryGetValue( model, out var decals ) )
				_decals.Add( (renderer, decals) );
		}

		foreach ( var (bsp, renderers) in _bsps )
		{
			if ( renderers.Materials.Count == 0 && renderers.Separate.Count == 0 && renderers.Clusters.Count == 0 )
				_goneBsps.Add( bsp );
		}
		foreach ( var bsp in _goneBsps )
			_bsps.Remove( bsp );
		_goneBsps.Clear();

		// The mirror planes are placed with the level and stay put; looking for them costs a pass over
		// every object, so it's done again only now and then, or when one goes.
		if ( --_refreshesUntilMirrors > 0 && _mirrorMarkers.TrueForAll( marker => marker.IsValid() ) )
			return;

		_refreshesUntilMirrors = MirrorRefreshes;
		_mirrors.Clear();
		_mirrorMarkers.Clear();
		foreach ( var marker in Scene.FindAllWithTag( MirrorPlanes.PlaneTag ) )
		{
			var tag = marker.Tags.TryGetAll().FirstOrDefault( t => t.StartsWith( "halo_mirror_" ) && t != MirrorPlanes.PlaneTag && t != MirrorPlanes.UnboundedTag );
			if ( tag is not null && int.TryParse( tag.AsSpan( "halo_mirror_".Length ), out var plane ) )
			{
				_mirrors.Add( (plane, marker.WorldPosition, marker.WorldRotation.Forward) );
				_mirrorMarkers.Add( marker );
			}
		}
	}

	(List<(ModelRenderer, BspMaterialSurfaces)> Materials, List<(ModelRenderer, BspSeparateSurfaces)> Separate, List<ModelRenderer> Clusters) Renderers( BspDrawing bsp )
	{
		if ( !_bsps.TryGetValue( bsp, out var renderers ) )
			_bsps[bsp] = renderers = (new(), new(), new());
		return renderers;
	}

	void ShowAll()
	{
		if ( !_culling )
			return;

		_culling = false;
		foreach ( var (bsp, renderers) in _bsps )
		{
			foreach ( var (renderer, surfaces) in renderers.Materials )
			{
				surfaces.DrawAll();
				if ( renderer.IsValid() )
					RenderCulling.Set( renderer, RenderCulling.Reason.Visibility, false );
			}
			foreach ( var (renderer, _) in renderers.Separate )
			{
				if ( renderer.IsValid() )
					RenderCulling.Set( renderer, RenderCulling.Reason.Visibility, false );
			}
			bsp.ShowingAll = true;
		}

		foreach ( var (renderer, cells) in _detailObjects )
		{
			cells.DrawAll();
			if ( renderer.IsValid() )
				RenderCulling.Set( renderer, RenderCulling.Reason.Visibility, false );
		}

		foreach ( var (renderer, decals) in _decals )
		{
			decals.DrawAll();
			if ( renderer.IsValid() )
				RenderCulling.Set( renderer, RenderCulling.Reason.Visibility, false );
		}

		_framesUntilRefresh = 0;
	}

	// The BSPs' models.

	static readonly ConditionalWeakTable<Model, BspMaterialSurfaces> MaterialSurfaces = new();
	static readonly ConditionalWeakTable<Model, BspSeparateSurfaces> SeparateSurfaces = new();
	static readonly ConditionalWeakTable<Model, BspDrawing> ClusterModels = new();
	static readonly ConditionalWeakTable<Model, DetailObjectCells> DetailObjectMeshes = new();
	static readonly ConditionalWeakTable<Model, DecalClusters> DecalMeshes = new();

	/// <summary>Remembers the model drawing a material's surfaces.</summary>
	public static void Register( Model model, BspMaterialSurfaces surfaces )
	{
		MaterialSurfaces.AddOrUpdate( model, surfaces );
		Watch();
	}

	/// <summary>Remembers a model drawing a material's surfaces by itself.</summary>
	public static void Register( Model model, BspSeparateSurfaces surfaces )
	{
		SeparateSurfaces.AddOrUpdate( model, surfaces );
		Watch();
	}

	// Remembers one of a BSP's opaque cluster models, which the engine's visibility culls; the BSP's view is still worked out for its translucent surfaces, decals and mirrors.
	public static void Register( Model model, BspDrawing drawing )
	{
		ClusterModels.AddOrUpdate( model, drawing );
		Watch();
	}

	// The BSP a cluster model draws, or null.
	public static BspDrawing DrawingOf( Model model ) => ClusterModels.TryGetValue( model, out var drawing ) ? drawing : null;

	/// <summary>Remembers a model drawing one mesh of a BSP's detail objects.</summary>
	public static void Register( Model model, DetailObjectCells cells )
	{
		DetailObjectMeshes.AddOrUpdate( model, cells );
		Watch();
	}

	/// <summary>Remembers a model drawing one mesh of a level's decals.</summary>
	public static void Register( Model model, DecalClusters decals )
	{
		DecalMeshes.AddOrUpdate( model, decals );
		Watch();
	}

	// Watching for scenes with such models (see MirrorReflections).

	static readonly object WatchLock = new();
	static System.Threading.Timer _watchTimer;
	static int _scanQueued;

	static readonly PropertyInfo AllScenesProperty = typeof( Scene ).GetProperty( "_all", BindingFlags.NonPublic | BindingFlags.Static );

	// The hidden object holding a scene's visibility, looked for by name (see MirrorReflections).
	const string ObjectName = "halo cluster visibility";

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
					|| !scene.GetAllComponents<ModelRenderer>().Any( r => r.IsValid() && r.Model is { } model && (MaterialSurfaces.TryGetValue( model, out _ ) || ClusterModels.TryGetValue( model, out _ )) ) )
					continue;

				AddTo( scene );
			}
		}
		catch ( Exception e )
		{
			HaloCeMount.Current?.Logger.Warning( e, $"Couldn't add Halo cluster visibility: {e.Message}" );
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
			var visibility = go.Components.Create<ClusterVisibility>( false );
			go.Flags &= ~GameObjectFlags.Deserializing;
			visibility.Flags |= ComponentFlags.Hidden | ComponentFlags.NotSaved | ComponentFlags.NotNetworked;
			visibility.Enabled = true;

			go.Flags |= GameObjectFlags.Deserializing;
			var oneBsp = go.Components.Create<OneBspShown>( false );
			go.Flags &= ~GameObjectFlags.Deserializing;
			oneBsp.Flags |= ComponentFlags.Hidden | ComponentFlags.NotSaved | ComponentFlags.NotNetworked;
			oneBsp.Enabled = true;
		}
	}
}
