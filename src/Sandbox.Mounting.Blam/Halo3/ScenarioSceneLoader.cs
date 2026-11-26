using Blam.Halo3;
using Blam.Halo3.Tags;

namespace Sandbox.Mounting.Blam.Halo3;

// Builds the scene of a Halo 3 map: its structure BSPs (geometry, instances, collision), the scenario's objects and spawns, and the sky; or (sky) the map's sky scene, which its 3D skybox draws.
sealed class ScenarioSceneLoader( string mapPath, bool sky = false ) : SceneLoader<BaseGameMount>
{
	new Halo3Mount Host => Halo3Mount.Current;

	// Models built in code only exist while something holds on to them: the last two scenes built keep theirs, so a map's models are let go once two others were opened since.
	const int KeptScenes = 2;
	static readonly Queue<List<Model>> Kept = new();

	List<Model> _models;

	internal static void Clear()
	{
		lock ( Kept )
			Kept.Clear();
	}

	// A map's scene loads its sky scene first: the 3D skybox it builds (ScenarioObjectsBuilder.BuildSky) can only refer to a loaded scene file.
	protected override async Task<object> LoadAsync()
	{
		if ( !sky && Host.GetCache( mapPath ) is { } cache )
			await ResourceLibrary.LoadAsync<SceneFile>( $"mount://{Host.Ident}/{ScenarioObjectsBuilder.SkyScenePath( cache )}.scene" );
		return await base.LoadAsync();
	}

	protected override void BuildScene()
	{
		var cache = Host.GetCache( mapPath );
		_models = new List<Model>();
		if ( sky )
		{
			// The sky's models are kept with the last map scene built (the map whose 3D skybox loads this scene).
			ScenarioObjectsBuilder.BuildSkyScene( Host, cache, _models );
			lock ( Kept )
			{
				if ( Kept.Count > 0 )
					Kept.Last().AddRange( _models );
				else
					Kept.Enqueue( _models );
			}
			return;
		}

		lock ( Kept )
		{
			Kept.Enqueue( _models );
			while ( Kept.Count > KeptScenes )
				Kept.Dequeue();
		}

		// Level geometry and collision (StructureBspBuilder.cs).
		StructureBspBuilder.Build( Host, cache, _models );

		// The scenario's decals laid onto the level (Halo3DecalBuilder.cs).
		Halo3DecalBuilder.Build( Host, cache, _models );

		// Objects, multiplayer spawns and objects, the sky (ScenarioObjectsBuilder.cs).
		ScenarioObjectsBuilder.Build( Host, cache, _models );

		// Static lens flares and light volumes of the placed objects and effect scenery (Halo3FlareBuilder.cs).
		Halo3FlareBuilder.Build( Host, cache, _models );

		// Sound scenery and cluster background sounds (LevelSounds.cs).
		LevelSounds.Build( Host, cache );

		// Effect scenery and objects' effects at rest (Halo3Effects.cs). h3-effects
		LevelEffects.Build( Host, cache );

		// Screen effects + weather: bloom from the camera fx, weather by camera cluster (Halo3SceneUpkeep.cs).
		Halo3SceneUpkeep.Mark( cache );

		// Source 2 visibility for the level (see SourceVisibility), built the first time on this thread: it reads the BSPs' tag resources.
		try
		{
			SourceVisibility.AddMarker( BuildVisibility( cache ), [] );
		}
		catch ( Exception e )
		{
			Host.Logger.Warning( e, $"{cache.Name}: no Source visibility: {e.Message}" );
		}
	}

	string BuildVisibility( CacheFile cache )
	{
		var scenario = new ScenarioStructureBsps( cache );
		var references = scenario.SceneBsps.Where( b => b.Bsp is not null && b.Lightmap is not null ).ToList();
		var file = new FileInfo( cache.FilePath );
		var identity = $"{file.Length}|{file.LastWriteTimeUtc.Ticks}|{string.Join( ',', references.Select( r => r.Index ) )}";
		return SourceVisibility.Package( $"halo3_{cache.Name.ToLowerInvariant()}", identity, () =>
		{
			var bsps = references.Select( r =>
			{
				var tag = new StructureBspTag( r.Bsp );
				return new global::Blam.Halo3.WorldVisibility.Bsp( r.Index, tag, tag.LoadResources()?.Collision );
			} ).ToList();
			return (global::Blam.Halo3.WorldVisibility.Build( bsps, ZoneSetVisibility.Read( cache ), Halo3Mount.WorldScale, out var stats ), stats);
		}, Host.Logger );
	}
}
