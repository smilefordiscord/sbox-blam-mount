using System.Reflection;
using Blam.Visibility;
using Sandbox.Internal;

namespace Sandbox.Mounting.Blam;

// Gives mounted levels Source 2's own visibility: a map holding only a world_visibility.vvis built from Halo's clusters is loaded into the scene's world, and the engine culls everything by it as it does in a compiled Hammer map.
// The maps are written once per level under mounts/blam/Assets/maps/halo/, which the engine searches like any assets folder.
static class SourceVisibility
{
	// Bumped whenever what the maps hold changes, so old ones are built again.
	const int Version = 3;

	// The object a scene's maps are listed under; its children are named after the maps, tagged with what each is for.
	public const string MarkerName = "halo world visibility";
	public const string MarkerTag = "halo_world_visibility";
	public const string AllTag = "halo_pvs_all";
	public static string BspTag( int bsp ) => $"halo_pvs_bsp_{bsp}";

	static string Folder
	{
		get
		{
			var root = System.IO.Directory.Exists( "mounts/blam" ) ? "mounts/blam" : "mount/blam";
			return Path.Combine( Environment.CurrentDirectory, root, "Assets", "maps", "halo" );
		}
	}

	// The map path (maps/halo/<name>.vpk) of a level's visibility, building it first if this version of it isn't on disk yet; null if it can't be built.
	public static string Package( string name, string identity, Func<(byte[] Visibility, string Stats)> build, Sandbox.Diagnostics.Logger logger )
	{
		var hash = Convert.ToHexString( System.Security.Cryptography.SHA1.HashData( System.Text.Encoding.UTF8.GetBytes( $"{Version}|{identity}" ) ) )[..8].ToLowerInvariant();
		var file = $"{name}_{hash}";
		var mapPath = $"maps/halo/{file}.vpk";
		var path = Path.Combine( Folder, $"{file}.vpk" );
		if ( File.Exists( path ) )
			return mapPath;

		try
		{
			var watch = System.Diagnostics.Stopwatch.StartNew();
			var (visibility, stats) = build();
			if ( visibility is null )
				return null;

			System.IO.Directory.CreateDirectory( Folder );
			var temporary = path + ".tmp";
			File.WriteAllBytes( temporary, WorldVisibilityPackage.Build( $"halo/{file}", visibility ) );
			File.Move( temporary, path, true );
			logger.Info( $"{name}: built Source visibility in {watch.ElapsedMilliseconds} ms ({stats})" );

			// Older versions of this level's map; one still open in a running scene stays until next time.
			foreach ( var old in System.IO.Directory.GetFiles( Folder, $"{name}_*.vpk" ) )
			{
				if ( !string.Equals( old, path, StringComparison.OrdinalIgnoreCase ) && Path.GetFileNameWithoutExtension( old ).Length == file.Length )
				{
					try { File.Delete( old ); } catch ( IOException ) { } catch ( UnauthorizedAccessException ) { }
				}
			}
			return mapPath;
		}
		catch ( Exception e )
		{
			logger.Warning( e, $"{name}: couldn't build Source visibility: {e.Message}" );
			return null;
		}
	}

	// Lists a scene's visibility maps for the component that loads them: one for the whole level, and one per BSP for when a game shows only that one.
	public static void AddMarker( string all, IReadOnlyList<(int Bsp, string MapPath)> bsps )
	{
		if ( all is null )
			return;

		var marker = new GameObject( true, MarkerName );
		marker.Tags.Add( MarkerTag );
		new GameObject( marker, true, all ).Tags.Add( AllTag );
		foreach ( var (bsp, mapPath) in bsps )
		{
			if ( mapPath is not null )
				new GameObject( marker, true, mapPath ).Tags.Add( BspTag( bsp ) );
		}
		Watch();
	}

	static readonly MethodInfo SetFlagMethod = typeof( SceneObject.SceneObjectFlagAccessor ).GetMethod( "SetFlag", BindingFlags.NonPublic | BindingFlags.Instance );
	static readonly object NoPvsCulling = SetFlagMethod?.GetParameters()[0].ParameterType is { } flagType ? Enum.ToObject( flagType, 0x0000080000000000UL ) : null;

	// Keeps a scene object drawn wherever the camera is: what's placed relative to the camera for sorting (fog, weather, frame grabs) would otherwise be culled with whatever cluster it ends up in.
	public static void KeepDrawn( SceneObject sceneObject )
	{
		if ( sceneObject is null || !sceneObject.IsValid() || SetFlagMethod is null || NoPvsCulling is null )
			return;
		if ( KeptObjects.TryGetValue( sceneObject, out _ ) )
			return;

		SetFlagMethod.Invoke( sceneObject.Flags, [NoPvsCulling, true] );
		KeptObjects.AddOrUpdate( sceneObject, KeptObjects );
	}

	// KeepDrawn, handing the object back (for bounds set relative to the camera).
	public static T Kept<T>( T sceneObject ) where T : SceneObject
	{
		KeepDrawn( sceneObject );
		return sceneObject;
	}

	static readonly System.Runtime.CompilerServices.ConditionalWeakTable<SceneObject, object> KeptObjects = new();

	// Watching for scenes with a marker and no loader yet (see ClusterVisibility).

	static readonly object WatchLock = new();
	static System.Threading.Timer _watchTimer;
	static int _scanQueued;
	static readonly PropertyInfo AllScenesProperty = typeof( Scene ).GetProperty( "_all", BindingFlags.NonPublic | BindingFlags.Static );
	const string LoaderName = "halo world visibility map";

	public static void Watch()
	{
		lock ( WatchLock )
			_watchTimer ??= new System.Threading.Timer( _ => QueueScan(), null, 0, 500 );
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
				if ( !scene.IsValid() || scene.Children.Any( g => g.Name == LoaderName ) || !scene.FindAllWithTag( MarkerTag ).Any() )
					continue;

				using ( scene.Push() )
				{
					var go = new GameObject( true, LoaderName );
					go.Flags |= GameObjectFlags.Hidden | GameObjectFlags.NotSaved | GameObjectFlags.NotNetworked;

					// As in ClusterVisibility: the game's type library doesn't know mount types.
					go.Flags |= GameObjectFlags.Deserializing;
					var loader = go.Components.Create<VisibilityMapLoader>( false );
					go.Flags &= ~GameObjectFlags.Deserializing;
					loader.Flags |= ComponentFlags.Hidden | ComponentFlags.NotSaved | ComponentFlags.NotNetworked;
					loader.Enabled = true;
				}
			}
		}
		catch ( Exception e )
		{
			Sandbox.Internal.GlobalGameNamespace.Log.Warning( e, $"Couldn't add Halo world visibility: {e.Message}" );
		}
	}
}

// Loads the visibility map a scene's marker lists into its world: the shown BSP's when a game shows just one, else the whole level's. Deleting it with the scene, while the world still stands.
sealed class VisibilityMapLoader : Component, Component.ExecuteInEditor, IPreRenderSubscriber
{
	SceneMap _map;
	string _loaded;
	RealTimeSince _checked;

	protected override void OnEnabled()
	{
		_checked = 1;
	}

	// IPreRenderSubscriber is what gets OnPreRender called for a mount component (see MirrorReflections).
	protected override void OnPreRender()
	{
		if ( _checked < 0.5f )
			return;
		_checked = 0;

		var wanted = Wanted();
		if ( wanted == _loaded )
			return;

		var previous = _map;
		_map = null;
		_loaded = wanted;
		if ( wanted is not null )
		{
			// Added before the old one goes, so the world never draws a frame without visibility.
			var map = new SceneMap( Scene.SceneWorld, wanted );
			if ( map.IsValid )
				_map = map;
			else
				Sandbox.Internal.GlobalGameNamespace.Log.Warning( $"Couldn't load the level's visibility {wanted}" );
		}
		previous?.Delete();
	}

	protected override void OnDisabled()
	{
		_map?.Delete();
		_map = null;
		_loaded = null;
	}

	string Wanted()
	{
		var marker = Scene.FindAllWithTag( SourceVisibility.MarkerTag ).FirstOrDefault( go => go.Name == SourceVisibility.MarkerName );
		if ( marker is null )
			return null;

		string all = null;
		string only = null;
		var shown = 0;
		foreach ( var child in marker.Children )
		{
			if ( child.Tags.Has( SourceVisibility.AllTag ) )
			{
				all = child.Name;
				continue;
			}

			var bspTag = child.Tags.TryGetAll().FirstOrDefault( t => t.StartsWith( "halo_pvs_bsp_" ) );
			if ( bspTag is null )
				continue;

			// A BSP is shown while any object tagged with it is.
			var levelTag = "bsp_" + bspTag["halo_pvs_bsp_".Length..];
			if ( Scene.FindAllWithTag( levelTag ).Any( go => go.Active ) )
			{
				shown++;
				only = child.Name;
			}
		}

		if ( shown == 1 )
			return only;

		// A mount drawing one of the shown BSPs itself (Halo1.OneBspShown) gets that BSP's map.
		var drawn = Halo1.OneBspShown.ShownIn( Scene );
		if ( drawn >= 0 )
		{
			var tag = SourceVisibility.BspTag( drawn );
			if ( marker.Children.FirstOrDefault( c => c.Tags.Has( tag ) ) is { } child )
				return child.Name;
		}
		return all;
	}
}
