using System.Threading;
using Blam.Halo3;

namespace Sandbox.Mounting.Blam.Halo3;

// Halo 3, from halo3/maps in MCC.
public sealed class Halo3Mount : MccMount
{
	// s&box units per Halo world unit, the same as the other games so levels match a player of the same size.
	public const float WorldScale = Halo1.HaloCeMount.WorldScale;

	// The mounted instance; loaders reach the mount through this, since ResourceLoader.Host is the development stub during development.
	internal static Halo3Mount Current { get; private set; }

	public override string Ident => "halo3";
	public override string Title => "Halo 3";
	protected override string GameFolder => "halo3";

	readonly Dictionary<string, Lazy<CacheFile>> _caches = new( StringComparer.OrdinalIgnoreCase );
	int _readers;
	int _closed;

	internal string MapsDirectory => System.IO.Path.Combine( GameDirectory, "maps" );
	internal string FmodDirectory => System.IO.Path.Combine( GameDirectory, "fmod", "pc" );
	internal Sandbox.Diagnostics.Logger Logger => Log;

	protected override Task Mount( MountContext context )
	{
		Current = this;
		Volatile.Write( ref _closed, 0 );
		ResourceFiles.Open();
		MccBrightness.Start();

		if ( !System.IO.Directory.Exists( MapsDirectory ) )
		{
			Log.Warning( $"Couldn't find {MapsDirectory}" );
			return Task.CompletedTask;
		}

		// Every map holds all its own tags; the first map with a tag provides it.
		var registered = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
		var models = new Halo3Models();
		var animationData = new Halo3AnimationData();
		foreach ( var mapPath in System.IO.Directory.EnumerateFiles( MapsDirectory, "*.map" ).Order() )
		{
			try
			{
				var cache = GetCache( mapPath );
				if ( !cache.HasTags )
					continue;

				Halo3Textures.Register( context, cache, registered );
				models.Find( cache );
				animationData.Collect( cache );
				Halo3Sounds.Register( context, cache, registered );
				Halo3Surfaces.Register( context, cache, registered );
				Halo3Effects.Register( context, cache, registered ); // h3-effects
				context.Add( ResourceType.Scene, $"maps/{cache.Name}", new ScenarioSceneLoader( mapPath ) );
				context.Add( ResourceType.Scene, ScenarioObjectsBuilder.SkyScenePath( cache ), new ScenarioSceneLoader( mapPath, sky: true ) );
			}
			catch ( Exception e )
			{
				Log.Warning( e, $"Skipping {mapPath}: {e.Message}" );
			}
		}

		models.Register( context );
		animationData.Register( context );
		ShowInSpawnMenus( models.Paths );
		IsMounted = true;
		return Task.CompletedTask;
	}

	// The resource path of an object model (an hlmt's path).
	internal static string ModelPath( string tagPath ) => $"mount://halo3/{ResourcePath( tagPath )}.vmdl";

	internal static string ResourcePath( string tagPath ) => tagPath.Replace( '\\', '/' );

	// The resource name of one bitmap of a bitmap tag; the ones after the first get an index suffix (as in the other mounts).
	internal static string TextureName( string tagPath, int index ) =>
		index == 0 ? ResourcePath( tagPath ) : $"{ResourcePath( tagPath )}_{index}";

	internal static string TexturePath( string tagPath, int index = 0 ) => $"mount://halo3/{TextureName( tagPath, index )}.vtex";

	// An opened cache file, kept open (memory-mapped) for loaders; a failed open is forgotten so the next call tries again.
	internal CacheFile GetCache( string mapPath )
	{
		if ( Volatile.Read( ref _closed ) != 0 )
			throw new ObjectDisposedException( nameof( Halo3Mount ) );

		Lazy<CacheFile> cache;
		lock ( _caches )
		{
			if ( !_caches.TryGetValue( mapPath, out cache ) )
				_caches[mapPath] = cache = new Lazy<CacheFile>( () => CacheFile.Open( mapPath ) );
		}

		try
		{
			return cache.Value;
		}
		catch
		{
			lock ( _caches )
			{
				if ( _caches.TryGetValue( mapPath, out var current ) && current == cache )
					_caches.Remove( mapPath );
			}
			throw;
		}
	}

	// Runs a worker-thread read of the mapped caches; Shutdown waits for these before unmapping them.
	internal T Read<T>( Func<T> read )
	{
		Interlocked.Increment( ref _readers );
		try
		{
			if ( Volatile.Read( ref _closed ) != 0 )
				throw new ObjectDisposedException( nameof( Halo3Mount ) );
			return read();
		}
		finally
		{
			Interlocked.Decrement( ref _readers );
		}
	}

	internal CacheFile GetMap( string name )
	{
		var path = System.IO.Path.Combine( MapsDirectory, $"{name}.map" );
		return File.Exists( path ) ? GetCache( path ) : null;
	}

	protected override void Shutdown()
	{
		Interlocked.Exchange( ref _closed, 1 );
		MccBrightness.Stop();
		Halo3SceneUpkeep.StopWatching();
		Halo3Sounds.Shutdown();
		Halo3Materials.Clear();
		Halo3Effects.Shutdown(); // h3-effects
		ScenarioSceneLoader.Clear();

		// A load still reading the maps would crash on unmapped memory: leaking the mappings beats that.
		if ( !SpinWait.SpinUntil( () => Volatile.Read( ref _readers ) == 0, 10_000 ) )
		{
			Log.Warning( "Halo 3 loads were still reading the maps at shutdown; their files stay mapped" );
			return;
		}

		lock ( _caches )
		{
			foreach ( var cache in _caches.Values.Where( c => c.IsValueCreated ) )
				cache.Value.Dispose();
			_caches.Clear();
		}
		ResourceFiles.Close();
	}
}
