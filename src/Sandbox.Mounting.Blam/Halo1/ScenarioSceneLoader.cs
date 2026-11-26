using Blam;
using Blam.Halo1;
using Blam.Halo1.Tags;

namespace Sandbox.Mounting.Blam.Halo1;

/// <summary>
/// Builds a scene for a Halo CE map: every structure BSP as a static model with collision, the
/// sky, the objects placed in the level, and a spawn point at each player starting location.
/// </summary>
sealed class ScenarioSceneLoader( string mapPath ) : SceneLoader<BaseGameMount>
{
	// See HaloCeLoader.
	new HaloCeMount Host => HaloCeMount.Current;

	/// <summary>
	/// Object kinds that are solid. Weapons and equipment lying around shouldn't trip you up.
	/// </summary>
	static readonly HashSet<string> SolidObjects = ["scenery", "vehicle", "machine", "control", "light fixture"];

	/// <summary>
	/// Object kinds Halo makes shadowless as it creates them: devices, equipment and sound scenery.
	/// Scenery, bipeds, vehicles and weapons cast unless their object tag says not to.
	/// </summary>
	static readonly HashSet<string> ShadowlessObjects = ["machine", "control", "light fixture", "equipment", "sound scenery"];

	// Models built in code only exist while something holds on to them.
	readonly List<Model> _models = new();

	/// <summary>
	/// The attachments (by index) of placed objects that ride on the object (see <see cref="MovingAttachments"/>),
	/// which the level's own lights, flares and particles leave out.
	/// </summary>
	readonly HashSet<(ObjectPlacement Placement, int Attachment)> _movingAttachments = new();
	readonly HashSet<(ObjectPlacement Placement, int Attachment)> _carriedFlares = new();
	readonly Dictionary<(uint Light, System.Numerics.Vector3 Tint), Model> _tintedFlares = new();

	// Time spent loading placed objects' models (BuildScene's timing log).
	long _objectModelMilliseconds;

	// Placed objects' lighting and change colors, for their renderers' tints.
	ObjectAppearance _appearance;

	// Objects with glass get their own models (see ObjectGlass).
	ObjectGlass _objectGlass;

	// The region permutations a campaign mission's objects start with (see InitialPermutations), by kind and placement index.
	Dictionary<(string Kind, int Index), int[]> _permutations;

	// Collision models of objects showing other region permutations than the first, by model, collision model and the BSP each node shows (see CollisionFor).
	readonly Dictionary<(uint Model, uint Collision, string Bsps), Model> _permutationCollision = new();
	readonly Dictionary<uint, CollisionModelTag> _collisionTags = new();

	/// <summary>
	/// Builds the scene a slice at a time: each <c>MoveNext</c> does about <see cref="BuildSlice.Milliseconds"/>
	/// of work and yields how far the build has got (0 to 1). With a target scene the level goes
	/// straight into it, for a game to run a slice a frame under its loading screen (see
	/// <see cref="SceneBuilding"/>); without one it goes into the scene the engine pushed for
	/// <see cref="BuildScene"/>. Main thread.
	/// </summary>
	/// <remarks>
	/// Not awaiting frames itself: <c>GameTask.Yield</c> goes back to the main thread's queue, which
	/// keeps draining inside the same engine frame, so the whole build would run in one frame.
	/// </remarks>
	public IEnumerator<float> Build( Scene target )
	{
		var slice = new BuildSlice( stepped: target is not null );
		var laps = new Dictionary<string, double>();
		var slices = 0;
		var total = 0.0;

		using var steps = Steps( slice );
		while ( true )
		{
			slice.Restart();
			var start = System.Diagnostics.Stopwatch.GetTimestamp();
			bool more;
			using ( target?.Push() )
			using ( target?.BatchGroup() )
			{
				more = steps.MoveNext();
			}

			var milliseconds = System.Diagnostics.Stopwatch.GetElapsedTime( start ).TotalMilliseconds;
			laps[slice.Part] = laps.GetValueOrDefault( slice.Part ) + milliseconds;
			total += milliseconds;
			slices++;
			if ( !more )
				break;
			yield return steps.Current;
		}

		Host.Logger.Info( $"{System.IO.Path.GetFileNameWithoutExtension( mapPath )} scene built in {total:0} ms over {slices} slices: {string.Join( ", ", laps.Select( l => $"{l.Key} {l.Value:0}" ) )} (object models {_objectModelMilliseconds})" );
	}

	protected override void BuildScene()
	{
		using var build = Build( null );
		while ( build.MoveNext() )
		{
		}
	}

	// Shares of the progress, roughly as the parts take on b30 and c10.
	const float BspModelShare = 0.3f, ObjectModelShare = 0.15f, LightingShare = 0.05f, BspShare = 0.15f, SkyShare = 0.05f, ObjectShare = 0.15f, EffectShare = 0.1f;

	IEnumerator<float> Steps( BuildSlice slice )
	{
		slice.Part = "tags";
		var cache = Host.GetCache( mapPath );
		var scenario = new ScenarioTag( cache, cache.Scenario );
		var solo = cache.ScenarioType == ScenarioType.Singleplayer;
		var bsps = LevelBsps( cache, scenario );
		var netgameItems = NetgameItems( cache, scenario );
		var netgamePlacements = netgameItems.Select( x => x.Placement ).ToList();
		var modelPaths = scenario.Objects.Where( p => solo || !p.NotAutomatically ).Concat( netgamePlacements )
			.Select( p => ObjectTags.GetObjectModel( cache, p.Object ) )
			.Where( m => m is not null && m.Class == TagClasses.GbxModel )
			.Select( m => HaloCeMount.ModelPath( Host.Ident, m.Path ) )
			.Distinct().ToList();
		yield return 0.01f;

		// The BSPs' models, the biggest pieces, each built a few surfaces at a time.
		slice.Part = "bsp models";
		var bspModels = new Dictionary<string, StructureBspModel.Parts>();
		var separateModels = new Dictionary<string, List<(Model Model, int MirrorPlane, int Breakable)>>();
		var drawings = new List<BspDrawing>();
		var mirrors = new MirrorPlanes();
		for ( var i = 0; i < bsps.Count; i++ )
		{
			var (name, _, bsp) = bsps[i];
			var at = BspModelShare * i / bsps.Count;
			foreach ( var parts in StructureBspModel.Build( Host, $"haloce/{name}.vmdl", cache, bsp, slice ) )
			{
				if ( parts is null )
				{
					yield return at;
					continue;
				}
				bspModels[name] = parts;
			}

			var separate = separateModels[name] = new();
			var drawing = bspModels.GetValueOrDefault( name )?.Drawing ?? new BspDrawing( bsp.ReadVisibility(), bsp.Surfaces.Length / 3 );
			drawings.Add( drawing );
			foreach ( var piece in StructureBspModel.BuildSeparate( Host, $"haloce/{name}.vmdl", cache, bsp, drawing, mirrors, slice ) )
			{
				if ( piece.Model is null )
				{
					yield return at + BspModelShare / bsps.Count * 0.7f;
					continue;
				}
				separate.Add( piece );
			}
			yield return BspModelShare * (i + 1) / bsps.Count;
		}

		// Source 2 visibility for the level and each BSP, built off the main thread the first time.
		slice.Part = "visibility";
		var visibility = Task.Run( () => BuildVisibility( cache, bsps, drawings ) );
		while ( slice.Stepped && !visibility.IsCompleted )
			yield return BspModelShare;
		try
		{
			var (all, perBsp) = visibility.GetAwaiter().GetResult();
			SourceVisibility.AddMarker( all, perBsp );
		}
		catch ( Exception e )
		{
			Host.Logger.Warning( e, $"{cache.Name}: no Source visibility: {e.Message}" );
		}

		// The placed objects' models, so placing them finds each loaded.
		slice.Part = "object models";
		var objectModels = new List<Model>();
		for ( var i = 0; i < modelPaths.Count; i++ )
		{
			if ( Model.Load( modelPaths[i] ) is { IsError: false } model )
				objectModels.Add( model );
			if ( slice.Due )
				yield return BspModelShare + ObjectModelShare * (i + 1) / modelPaths.Count;
		}

		// Plain computation over the map's data (positional file reads, tag data in memory), so a
		// stepped build runs it off the main thread while the game draws its frames.
		slice.Part = "object lighting";
		var bspTags = bsps.Select( b => b.Bsp ).ToList();
		ObjectAppearance.Computed lighting = null;
		if ( slice.Stepped )
		{
			var computing = Task.Run( () => ObjectAppearance.Compute( cache, scenario, bspTags, netgamePlacements ) );
			while ( !computing.IsCompleted )
				yield return BspModelShare + ObjectModelShare;
			if ( computing.IsCompletedSuccessfully )
				lighting = computing.Result;
			else
				Host.Logger.Warning( computing.Exception, $"{cache.Name}: couldn't light the objects: {computing.Exception?.InnerException?.Message}" );
		}
		else
		{
			try
			{
				lighting = ObjectAppearance.Compute( cache, scenario, bspTags, netgamePlacements );
			}
			catch ( Exception e )
			{
				Host.Logger.Warning( e, $"{cache.Name}: couldn't light the objects: {e.Message}" );
			}
		}
		yield return BspModelShare + ObjectModelShare + LightingShare;

		// Planar fog volumes; each BSP hands them its water and gets them for its glass and water.
		slice.Part = "fog, water";
		var fogVolumes = PlanarFog.Build( Host, cache, scenario, _models );
		WaterVolumes.Build( Host, cache, scenario );
		var detailObjectCollections = DetailObjectCollectionTag.ReadPalette( cache, cache.Scenario );
		var built = BspModelShare + ObjectModelShare + LightingShare;
		yield return built;

		// The ambient light baked into the level's surfaces, weighted by how much of the level
		// each covers. Halo lit objects with the ambient of the surface they stood on.
		var ambientSum = Vector3.Zero;
		var ambientWeight = 0f;

		slice.Part = "bsp";
		for ( var i = 0; i < bsps.Count; i++ )
		{
			var (name, reference, bsp) = bsps[i];
			var at = built + BspShare * i / bsps.Count;
			var step = BspShare / bsps.Count / 3;

			foreach ( var material in bsp.Lightmaps.Where( l => l.BitmapIndex >= 0 ).SelectMany( l => l.Materials ) )
			{
				ambientSum += (Vector3)material.AmbientColor * material.SurfaceCount;
				ambientWeight += material.SurfaceCount;
			}

			if ( bspModels.GetValueOrDefault( name ) is not { } parts )
				continue;

			_models.Add( parts.Collision );
			fogVolumes.ApplyTo( null, reference, bsp );

			var go = new GameObject( true, System.IO.Path.GetFileName( name ) );
			go.Tags.Add( BspTag( reference ) );
			var collider = go.AddComponent<ModelCollider>();
			collider.Model = parts.Collision;
			collider.Static = true;

			// The level's own shadows are baked into its lightmaps; only objects cast into the sun's
			// shadow map, for halo1_environment.shader's object shadows.
			for ( var m = 0; m < parts.Materials.Count; m++ )
			{
				var model = parts.Materials[m];
				_models.Add( model );
				fogVolumes.ApplyTo( model, reference, null );
				var bspRenderer = new GameObject( go, true, $"{go.Name} surfaces {m}" ).AddComponent<ModelRenderer>();
				bspRenderer.Model = model;
				bspRenderer.RenderType = ModelRenderer.ShadowRenderType.Off;
			}
			yield return at + step;

			foreach ( var (separate, mirrorPlane, breakable) in separateModels[name] )
			{
				_models.Add( separate );
				fogVolumes.ApplyTo( separate, reference, null );
				var renderer = new GameObject( true, $"{System.IO.Path.GetFileName( name )} {System.IO.Path.GetFileNameWithoutExtension( separate.Name )}" ).AddComponent<ModelRenderer>();
				renderer.Model = separate;
				renderer.RenderType = ModelRenderer.ShadowRenderType.Off;
				renderer.GameObject.Tags.Add( BspTag( reference ) );
				if ( breakable >= 0 )
					renderer.GameObject.Tags.Add( BreakableTag( breakable ) );
				if ( separate.Name.Contains( "_water_" ) && !separate.Name.Contains( "_before_water_" ) )
					renderer.GameObject.Tags.Add( PlanarFogFrame.WaterTag );
				if ( mirrorPlane >= 0 )
				{
					renderer.GameObject.Tags.Add( MirrorPlanes.GlassTag );
					renderer.GameObject.Tags.Add( MirrorPlanes.PlaneIndexTag( mirrorPlane ) );
				}
			}

			foreach ( var (breakableCollision, breakable) in StructureBspModel.BuildBreakableCollision( $"haloce/{name}.vmdl", bsp ) )
			{
				_models.Add( breakableCollision );
				var collisionObject = new GameObject( true, $"{System.IO.Path.GetFileName( name )} breakable {breakable}" );
				var breakableCollider = collisionObject.AddComponent<ModelCollider>();
				breakableCollider.Model = breakableCollision;
				breakableCollider.Static = true;
				collisionObject.Tags.Add( BspTag( reference ) );
				collisionObject.Tags.Add( BreakableTag( breakable ) );
			}
			yield return at + step * 2;

			BuildDetailObjects( cache, bsp, detailObjectCollections, name, reference );
			yield return at + step * 3;
		}
		built += BspShare;

		// Where the level switches BSPs (see OneBspShown), for scenes that show them all.
		if ( bsps.Count > 1 )
		{
			var switches = new GameObject( true, "bsp switches" );
			foreach ( var bspSwitch in BspSwitches.Read( cache ) )
			{
				var v = bspSwitch.Volume;
				var go = new GameObject( switches, true, $"bsp switch {bspSwitch.Source} {bspSwitch.Destination}" );
				go.WorldPosition = (Vector3)v.Position * HaloCeMount.WorldScale;
				go.WorldRotation = Rotation.LookAt( (Vector3)v.Forward, (Vector3)v.Up );
				go.WorldScale = (Vector3)v.Extents * HaloCeMount.WorldScale;
				go.Tags.Add( OneBspShown.SwitchTag );
			}
		}

		slice.Part = "mirrors, sky";
		BuildMirrors( cache, mirrors );
		if ( scenario.Skies.Count > 0 )
		{
			var sky = new SkyTag( cache, scenario.Skies[0] );
			BuildSky( scenario.Skies[0] );
			BuildLighting( sky, ambientWeight > 0 ? ambientSum / ambientWeight : null );
			BuildFog( sky );
		}
		yield return built + SkyShare / 2;

		slice.Part = "decals";
		if ( scenario.Decals.Count > 0 )
		{
			var decals = DecalModel.Build( Host, $"haloce/{cache.Name}/decals.vmdl", cache, scenario.Decals, new SurfaceFinder( bspTags ), drawings );
			for ( var d = 0; d < decals.Count; d++ )
			{
				_models.Add( decals[d] );
				var decalRenderer = new GameObject( true, $"decals {d}" ).AddComponent<ModelRenderer>();
				decalRenderer.Model = decals[d];
				decalRenderer.RenderType = ModelRenderer.ShadowRenderType.Off;
			}
		}
		built += SkyShare;
		yield return built;

		// Objects are lit from the lightmap around them (see ObjectAppearance); the sun and ambient
		// light above still light anything else in the scene, like the game's own player.
		slice.Part = "object lighting";
		_appearance = lighting is null ? null : ObjectAppearance.Install( cache, lighting );

		// Which map this scene is, for lighting what a game spawns in it from this map's lighting
		// (see MovingObjectLights.MapOf), also after another map was built.
		var mapMarker = new GameObject( true, "halo map" );
		mapMarker.Tags.Add( MovingObjectLights.MapTag );
		mapMarker.Tags.Add( $"{MovingObjectLights.MapTag}_{cache.Name.ToLowerInvariant()}" );

		slice.Part = "permutations";
		_objectGlass = new ObjectGlass( Host, _models );
		_permutations = null;
		if ( solo && scenario.StructureBsps.Count > 0 )
		{
			try
			{
				var start = StructureBspTag.ReadClusterTree( cache, scenario.StructureBsps[0] );
				var prediction = InitialPermutations.Predict( cache, cache.Scenario, p => start.FindCluster( p ) != -1 );
				_permutations = prediction.Permutations;
				foreach ( var warning in prediction.Warnings )
					Host.Logger.Info( $"{cache.Name}: {warning}" );
			}
			catch ( Exception e )
			{
				Host.Logger.Warning( e, $"{cache.Name}: couldn't work out the objects' starting permutations: {e.Message}" );
			}
		}
		yield return built;

		slice.Part = "objects";
		var objects = new GameObject( true, "objects" );
		for ( var i = 0; i < scenario.Objects.Count; i++ )
		{
			PlaceObject( cache, scenario.Objects[i], objects );
			if ( slice.Due )
				yield return built + ObjectShare * (i + 1) / scenario.Objects.Count;
		}
		built += ObjectShare;
		yield return built;

		// Lights, lens flares, weather and ambient particle effects.
		slice.Part = "level effects";
		_models.AddRange( LevelEffects.Build( Host, cache, scenario, _movingAttachments, _carriedFlares ) );
		yield return built + EffectShare / 3;

		// Multiplayer markers. Making teleporters teleport, flags capturable and so on is up to the
		// game: each marker is tagged with its kind and "channel_<usage id>" for it to find.
		slice.Part = "netgame, starts";
		var netgame = new GameObject( true, "netgame flags" );
		foreach ( var flag in scenario.NetgameFlags )
		{
			var kind = NetgameFlagTag( flag.Type );
			var go = new GameObject( netgame, true, $"{kind} {flag.UsageId}" );
			go.WorldPosition = (Vector3)flag.Position * HaloCeMount.WorldScale;
			go.WorldRotation = Rotation.FromYaw( flag.Facing.RadianToDegree() );
			go.Tags.Add( "netgame_flag" );
			go.Tags.Add( kind );
			go.Tags.Add( $"channel_{flag.UsageId}" );
		}

		// Multiplayer weapons and powerups. Picking them up and respawning them is up to the game:
		// each is tagged "netgame_item", "netgame_item_<index>" for its entry in the scenario's netgame
		// equipment, "game_<type>" for every game type it spawns in, and "respawn_<seconds>" (plus
		// "at_rest" if Halo leaves it floating where it's placed).
		if ( netgameItems.Count > 0 )
		{
			var items = new GameObject( true, "netgame items" );
			foreach ( var (spawn, placement, respawnTime) in netgameItems )
			{
				var go = PlaceObject( cache, placement, items );
				if ( go is null )
					continue;

				go.Tags.Add( "netgame_item" );
				go.Tags.Add( $"netgame_item_{spawn.Index}" );
				foreach ( var (game, name) in NetgameGames )
				{
					if ( spawn.SpawnsIn( game ) )
						go.Tags.Add( $"game_{name}" );
				}
				go.Tags.Add( $"respawn_{respawnTime}" );

				// Halo's multiplayer engine draws items that no unit holds at the item's own scale
				// (pistol 2.25, rifles 1.8): weapons lying around are bigger than held ones.
				var scale = ItemScale( cache, placement.Object );
				go.WorldScale = scale;

				if ( spawn.AtRest )
					go.Tags.Add( "at_rest" );
				else
					LayDown( cache, placement, go, scale );

				if ( slice.Due )
					yield return built + EffectShare / 3;
			}
		}

		foreach ( var start in scenario.PlayerStartingLocations )
		{
			var go = new GameObject( true, "player start" );
			go.WorldPosition = (Vector3)start.Position * HaloCeMount.WorldScale;
			go.WorldRotation = Rotation.FromYaw( start.Facing.RadianToDegree() );
			go.AddComponent<SpawnPoint>();
		}

		// s&box renders map thumbnails (mount_generatethumbs) from the object tagged map_preview,
		// else from a spawn point, which is at floor level. Look out from the first player start
		// at the Master Chief's eye height instead (see HaloCeMount.WorldScale).
		if ( scenario.PlayerStartingLocations.Count > 0 )
		{
			var start = scenario.PlayerStartingLocations[0];
			var go = new GameObject( true, "map preview" );
			go.WorldPosition = (Vector3)start.Position * HaloCeMount.WorldScale + Vector3.Up * 64;
			go.WorldRotation = Rotation.From( 10, start.Facing.RadianToDegree(), 0 );
			go.Tags.Add( "map_preview" );
		}
		yield return built + EffectShare * 2 / 3;

		// Ambient sound: sound scenery loops and the BSP clusters' background sounds.
		slice.Part = "ambient sound";
		foreach ( var share in Tolerant( AmbientSoundBuilder.Build( Host, cache, scenario, slice ), $"Couldn't add the ambient sounds of {cache.Name}" ) )
			yield return built + EffectShare * (2 + share) / 3;
	}

	/// <summary>
	/// Runs steps whose failure shouldn't fail the build, logging it instead.
	/// </summary>
	IEnumerable<float> Tolerant( IEnumerable<float> steps, string failure )
	{
		using var step = steps.GetEnumerator();
		while ( true )
		{
			bool more;
			try
			{
				more = step.MoveNext();
			}
			catch ( Exception e )
			{
				Host.Logger.Warning( e, $"{failure}: {e.Message}" );
				yield break;
			}

			if ( !more )
				yield break;
			yield return step.Current;
		}
	}

	// The level's visibility map and each BSP's (only when there are several), built once per version of the map file.
	(string All, List<(int Bsp, string MapPath)> PerBsp) BuildVisibility( CacheFile cache, List<(string Name, StructureBspReference Reference, StructureBspTag Bsp)> bsps, List<BspDrawing> drawings )
	{
		var file = new FileInfo( cache.FilePath );
		var identity = $"{file.Length}|{file.LastWriteTimeUtc.Ticks}|{string.Join( ',', bsps.Select( b => b.Reference.Index ) )}";
		var name = $"{Host.Ident}_{cache.Name.ToLowerInvariant()}";
		var data = drawings.Select( d => d.View.Data ).ToList();

		var all = SourceVisibility.Package( name, identity, () => (global::Blam.Halo1.WorldVisibility.Build( data, HaloCeMount.WorldScale, out var stats ), stats), Host.Logger );
		var perBsp = new List<(int, string)>();
		if ( bsps.Count > 1 )
		{
			for ( var i = 0; i < bsps.Count; i++ )
			{
				var one = data[i];
				perBsp.Add( (bsps[i].Reference.Index, SourceVisibility.Package( $"{name}_bsp{bsps[i].Reference.Index}", identity, () => (global::Blam.Halo1.WorldVisibility.Build( [one], HaloCeMount.WorldScale, out var stats ), stats), Host.Logger )) );
			}
		}
		return (all, perBsp);
	}

	static bool IsLit( StructureBspTag bsp ) => bsp.Lightmaps.Any( l => l.BitmapIndex >= 0 );

	/// <summary>
	/// The structure BSPs the scene builds, with their resource names. Campaign levels that share a
	/// layout (b40 and c40) carry every room's BSP, but only the rooms a mission visits got lightmaps;
	/// the rest are sealed off and would draw fullbright, so BSPs without any lightmap are left out
	/// when others have them.
	/// </summary>
	static List<(string Name, StructureBspReference Reference, StructureBspTag Bsp)> LevelBsps( CacheFile cache, ScenarioTag scenario )
	{
		var all = scenario.StructureBsps.Select( r => (Name: r.Tag?.Path.Replace( '\\', '/' ) ?? $"{cache.Name}/bsp{r.Index}", Reference: r, Bsp: new StructureBspTag( cache, r )) ).ToList();
		var anyLit = all.Any( x => IsLit( x.Bsp ) );
		foreach ( var x in all.Where( x => anyLit && !IsLit( x.Bsp ) ) )
			HaloCeMount.Current.Logger.Info( $"{cache.Name}: leaving out {x.Name}, which has no lightmaps (not visited in this mission)" );
		return all.Where( x => !anyLit || IsLit( x.Bsp ) ).ToList();
	}

	/// <summary>
	/// An object marking each dynamic mirror plane (on the plane, facing along its normal), which
	/// <see cref="MirrorReflections"/> finds in the live scene and renders the mirror for.
	/// </summary>
	void BuildMirrors( CacheFile cache, MirrorPlanes mirrors )
	{
		for ( var index = 0; index < mirrors.Planes.Count; index++ )
		{
			var plane = mirrors.Planes[index];
			if ( plane.Bounds is not { } bounds )
				continue;

			var go = new GameObject( true, $"mirror {index}" );
			go.WorldPosition = bounds.Center - plane.Normal * (Vector3.Dot( plane.Normal, bounds.Center ) - plane.Distance);
			go.WorldRotation = Rotation.LookAt( plane.Normal, MathF.Abs( plane.Normal.z ) > 0.9f ? Vector3.Forward : Vector3.Up );
			go.Tags.Add( MirrorPlanes.PlaneTag );
			go.Tags.Add( MirrorPlanes.PlaneIndexTag( index ) );
			Host.Logger.Info( $"{cache.Name}: dynamic mirror {index}, plane normal {plane.Normal:F2} distance {plane.Distance:F1}, glass {bounds.Mins:F0} .. {bounds.Maxs:F0}" );
		}

		if ( mirrors.Planes.Count > 0 )
			MirrorReflections.Watch();
	}

	/// <summary>
	/// The grass and plants painted onto a BSP's ground. They're unlit sprites with their lighting
	/// baked in, so they don't cast shadows.
	/// </summary>
	void BuildDetailObjects( CacheFile cache, StructureBspTag bsp, IReadOnlyList<DetailObjectCollectionTag> collections, string bspName, StructureBspReference reference )
	{
		var models = DetailObjectModel.Build( Host, $"haloce/{bspName}_detail_objects.vmdl", cache, bsp, collections );
		for ( var m = 0; m < models.Count; m++ )
		{
			var model = models[m];
			_models.Add( model );

			var renderer = new GameObject( true, $"{System.IO.Path.GetFileName( bspName )} detail objects {m}" ).AddComponent<ModelRenderer>();
			renderer.Model = model;
			renderer.RenderType = ModelRenderer.ShadowRenderType.Off;
			renderer.GameObject.Tags.Add( BspTag( reference ) );

			// Blended after the water, as Halo drew them (PlanarFogFrame sorts them).
			renderer.GameObject.Tags.Add( PlanarFogFrame.DetailObjectsTag );
		}

		if ( models.Count > 0 )
			PlanarFogFrame.Watch();
	}

	/// <summary>
	/// Tags what belongs to one structure BSP, for a game to show only the BSP a campaign
	/// mission is in, as Halo loads one at a time.
	/// </summary>
	internal static string BspTag( StructureBspReference reference ) => $"bsp_{reference.Index}";

	/// <summary>
	/// Tags what draws or collides as one of a BSP's breakable surfaces, for a game to take away
	/// when it breaks (with the BSP's own tag beside it).
	/// </summary>
	static string BreakableTag( int breakable ) => $"breakable_{breakable}";

	/// <summary>
	/// The map's thumbnail, rendered on the user's machine by s&amp;box's <c>mount_generatethumbs haloce</c>.
	/// That writes to <c>mount/haloce/assets/thumbs/haloce/</c>, a folder named after the mount's
	/// ident, but s&amp;box looks for thumbnails in the folder of the mount's DLL, which is
	/// <c>mount/blam/</c>. Read them from where they're written; when there's none there, s&amp;box
	/// still looks in <c>mount/blam/Assets/thumbs/haloce/</c>.
	/// </summary>
	protected override Texture GetThumbnail()
	{
		// s&box's working directory is its install folder, where mount_generatethumbs writes.
		var ident = base.Host.Ident;
		var path = System.IO.Path.Combine( Environment.CurrentDirectory, "mount", ident, "assets", "thumbs", ident, System.IO.Path.ChangeExtension( RelativePath, ".png" ) );
		if ( !File.Exists( path ) )
			return null;

		try
		{
			using var bitmap = Bitmap.CreateFromBytes( File.ReadAllBytes( path ) );
			return bitmap?.ToTexture();
		}
		catch ( Exception e )
		{
			Log.Warning( e, $"Couldn't load the thumbnail {path}: {e.Message}" );
			return null;
		}
	}

	static string NetgameFlagTag( NetgameFlagType type ) => type switch
	{
		NetgameFlagType.CtfFlag => "ctf_flag",
		NetgameFlagType.CtfVehicle => "ctf_vehicle",
		NetgameFlagType.OddballSpawn => "oddball_spawn",
		NetgameFlagType.RaceTrack => "race_track",
		NetgameFlagType.RaceVehicle => "race_vehicle",
		NetgameFlagType.TeleportFrom => "teleport_from",
		NetgameFlagType.TeleportTo => "teleport_to",
		NetgameFlagType.HillFlag => "hill_flag",
		_ => "netgame_other",
	};

	static readonly TagClass WeaponClass = new( "weap" );

	/// <summary>
	/// The multiplayer game types, with the names their tags use.
	/// </summary>
	static readonly (NetgameGameType Game, string Name)[] NetgameGames =
	[
		(NetgameGameType.Ctf, "ctf"),
		(NetgameGameType.Slayer, "slayer"),
		(NetgameGameType.Oddball, "oddball"),
		(NetgameGameType.King, "king"),
		(NetgameGameType.Race, "race"),
	];

	/// <summary>
	/// The item each multiplayer item spawn holds, as an object placement, and how often it respawns.
	/// Halo picks an item from the spawn's collection at random by weight every time it spawns one;
	/// here the random number comes from the spawn's position, so every load places the same item.
	/// </summary>
	static List<(NetgameEquipment Spawn, ObjectPlacement Placement, int RespawnTime)> NetgameItems( CacheFile cache, ScenarioTag scenario )
	{
		var items = new List<(NetgameEquipment, ObjectPlacement, int)>();
		foreach ( var spawn in scenario.NetgameEquipment )
		{
			var collection = new ItemCollectionTag( cache, spawn.ItemCollection );
			var item = collection.Pick( StableRandom.At( spawn.Position ) );
			if ( item is null )
				continue;

			// Halo spawns items with no particular facing and lets them drop; the facing the
			// level gives them looks tidier when they stay put.
			var placement = new ObjectPlacement(
				Kind: item.Is( WeaponClass ) ? "weapon" : "equipment",
				Object: item,
				Position: spawn.Position,
				Rotation: new System.Numerics.Vector3( spawn.Facing, 0, 0 ),
				DesiredPermutation: -1,
				NotAutomatically: false );

			// No spawn time on the spawn or its collection means every 30 seconds.
			var respawnTime = spawn.SpawnTime != 0 ? spawn.SpawnTime : collection.SpawnTime != 0 ? collection.SpawnTime : 30;
			items.Add( (spawn, placement, respawnTime) );
		}

		return items;
	}

	/// <summary>
	/// Lays a dropped multiplayer item down the way it comes to rest in Halo: turned so its "ground
	/// point" marker faces up (weapons lie on their side), with the marker on the spawn point (the
	/// spawns sit on the floor). Halo itself ignores the spawn's facing; it's kept here as the yaw.
	/// </summary>
	/// <summary>
	/// The scale an item is drawn at in multiplayer while no unit holds it (item definition
	/// 0x184; 0 means 1).
	/// </summary>
	static float ItemScale( CacheFile cache, TagEntry item )
	{
		var scale = item is null || item.Address == 0 ? 0 : cache.TagData.F32( item.Address + 0x184 );
		return scale > 0 ? scale : 1;
	}

	static void LayDown( CacheFile cache, ObjectPlacement placement, GameObject go, float scale = 1 )
	{
		var modelTag = ObjectTags.GetObjectModel( cache, placement.Object );
		if ( modelTag is null || modelTag.Class != TagClasses.GbxModel || ItemRestPose.For( new GbxModelTag( cache, modelTag ) ) is not { } rest )
			return;

		var rotation = Rotation.FromYaw( placement.Rotation.X.RadianToDegree() ) * GbxSkeleton.ToRotation( rest.Rotation );
		go.WorldRotation = rotation;
		go.WorldPosition = ((Vector3)placement.Position - rotation * ((Vector3)rest.GroundPoint * scale)) * HaloCeMount.WorldScale;
	}

	/// <summary>
	/// The sky is drawn as the skybox, from a material the mount renders from the sky tag.
	/// </summary>
	void BuildSky( TagEntry skyTag )
	{
		var material = Material.Load( HaloCeMount.SkyMaterialPath( Host.Ident, skyTag.Path ) );
		if ( material is null )
			return;

		var skybox = new GameObject( true, "sky" ).AddComponent<SkyBox2D>();
		skybox.SkyMaterial = material;
		skybox.SkyIndirectLighting = false;
	}

	/// <summary>
	/// Level geometry has its lighting baked in, but objects are lit live. Light them with the
	/// sky's lights, the same light the level was baked with, and the level's baked ambient.
	/// </summary>
	/// <param name="levelAmbient">Average ambient baked into the level, or null to use the sky's.</param>
	static void BuildLighting( SkyTag sky, Vector3? levelAmbient )
	{
		var ambient = levelAmbient ?? (Vector3)sky.OutdoorAmbientColor * sky.OutdoorAmbientPower;
		if ( ambient.Length > 0 )
		{
			var go = new GameObject( true, "ambient light" );
			go.AddComponent<AmbientLight>().Color = new Color( ambient.x, ambient.y, ambient.z );
		}

		// One directional light: s&box's shaders only get one (the last enabled), and the object shadows
		// (halo1_environment/halo1_model) read its direction and shadow map. With a second sky light (Death
		// Island's light from straight up) that one could win, leaving no shadows until the sun was turned
		// off and on. It's the sky's strongest light; objects are lit by Halo's object lighting anyway.
		var sun = sky.Lights
			.OrderByDescending( l => ((Vector3)l.Color * l.Power).Dot( new Vector3( 0.299f, 0.587f, 0.114f ) ) )
			.FirstOrDefault();
		if ( sun is not null )
		{
			var yaw = sun.Direction.X;
			var pitch = sun.Direction.Y;
			var toLight = new Vector3( MathF.Cos( pitch ) * MathF.Cos( yaw ), MathF.Cos( pitch ) * MathF.Sin( yaw ), MathF.Sin( pitch ) );
			// s&box's lighting divides diffuse light by pi and Halo's didn't, so the light is scaled
			// up to light objects as brightly as the lightmapped ground around them.
			var color = (Vector3)sun.Color * sun.Power * MathF.PI;

			// Halo casts object shadows along the light's horizontal direction, at least 45 degrees below the horizontal
			// (horizontal part h: z = -sqrt(1 - h^2) below 0.707, else scaled to 0.707 at z = -0.707). a10's sky light is
			// 30 degrees below the horizon: followed as is, the sun shone upward and units' shadows landed on the ceilings.
			var across = new Vector2( -toLight.x, -toLight.y );
			var length = across.Length;
			var shadow = length < 0.707f
				? new Vector3( across.x, across.y, -MathF.Sqrt( 1 - length * length ) )
				: new Vector3( across.x * 0.707f / length, across.y * 0.707f / length, -0.707f );

			var go = new GameObject( true, "sun" );
			go.WorldRotation = Rotation.LookAt( shadow );

			var directional = go.AddComponent<DirectionalLight>();
			directional.LightColor = new Color( color.x, color.y, color.z );
			directional.Shadows = true;
			// Objects' shadows span the whole level: s&box's default split (0.91, nearly logarithmic)
			// gives its last cascade everything from about 20 world units out, where a vehicle's shadow
			// lost half its darkness. 0.85 (picked in the editor) holds them further out, near ones sharp.
			directional.ShadowCascadeSplitRatio = 0.85f;
			// Screen-space contact shadows trace the depth buffer, level included, whose shadows are baked;
			// the object shadows don't read them (they shimmered as the camera turned).
			directional.ContactShadows = false;
			ObjectShadowVolumes.Watch();
		}
	}

	/// <summary>
	/// Halo's outdoor atmospheric fog: none up to the start distance, then thickening to the
	/// maximum density at the opaque distance, the same at every height. Halo fogs a view by the
	/// sky's indoor fog instead while the camera's cluster has no sky; that's for whoever moves the
	/// camera to switch (the map data has both fogs and each cluster's sky), so the fog is built
	/// when either fog exists, and starts off without an outdoor fog.
	/// </summary>
	/// <remarks>
	/// Halo blended fog in gamma space and s&amp;box blends it in linear space, which washes dark
	/// colors out much faster. For a dark surface, blending by f in gamma space is blending by
	/// f^2.2 in linear space, so the curve and maximum density are raised to 2.2 to match.
	/// </remarks>
	const float GammaFogExponent = 2.2f;

	static void BuildFog( SkyTag sky )
	{
		if ( sky.OutdoorFogOpaqueDistance <= 0 && sky.IndoorFogOpaqueDistance <= 0 )
			return;

		// Halo colors are gamma space and GradientFog takes linear, as the sky (cleared to this
		// color) and the planar fog shaders do with SrgbGammaToLinear.
		var color = (Vector3)sky.OutdoorFogColor;
		// GradientFog still sets the gradient fog constants the shaders read; the new Fog component doesn't.
#pragma warning disable CS0618
		var fog = new GameObject( true, "fog" ).AddComponent<GradientFog>();
#pragma warning restore CS0618
		fog.Enabled = sky.OutdoorFogOpaqueDistance > 0;
		fog.Color = new Color( SrgbToLinear( color.x ), SrgbToLinear( color.y ), SrgbToLinear( color.z ), MathF.Pow( Math.Clamp( sky.OutdoorFogMaximumDensity, 0, 1 ), GammaFogExponent ) );
		fog.StartDistance = sky.OutdoorFogStartDistance * HaloCeMount.WorldScale;
		fog.EndDistance = MathF.Max( sky.OutdoorFogOpaqueDistance, sky.OutdoorFogStartDistance + 1 ) * HaloCeMount.WorldScale;
		fog.FalloffExponent = GammaFogExponent;
		fog.VerticalFalloffExponent = 0;
		fog.Height = 1000000;
		fog.WorldPosition = Vector3.Down * 500000;
	}

	static float SrgbToLinear( float c ) =>
		c <= 0.04045f ? c / 12.92f : MathF.Pow( (c + 0.055f) / 1.055f, 2.4f );

	/// <summary>
	/// A light's lens flare model: the mount's own, or one tinted by an object's change color built
	/// for this level (one per light and color).
	/// </summary>
	Model LensFlare( CacheFile cache, TagEntry light, System.Numerics.Vector3 tint )
	{
		if ( tint == System.Numerics.Vector3.One )
			return EffectBuilder.LoadLensFlare( cache, light );

		if ( _tintedFlares.TryGetValue( (light.Id, tint), out var model ) )
			return model;

		var color = $"{(int)(tint.X * 255):X2}{(int)(tint.Y * 255):X2}{(int)(tint.Z * 255):X2}";
		model = LensFlares.ForLight( Host, $"haloce/{cache.Name}/lens_flares/{HaloCeMount.ResourcePath( light.Path )}_{color}.vmdl", cache, light, tint );
		_tintedFlares[(light.Id, tint)] = model;
		if ( model is not null )
			_models.Add( model );
		return model;
	}

	GameObject PlaceObject( CacheFile cache, ObjectPlacement placement, GameObject parent )
	{
		// Objects that only appear when a script creates them are placed disabled in campaign
		// missions, for the game's scripts to create, and not at all elsewhere.
		var solo = cache.ScenarioType == ScenarioType.Singleplayer;
		if ( placement.NotAutomatically && !solo )
			return null;

		var modelTag = ObjectTags.GetObjectModel( cache, placement.Object );
		if ( modelTag is null || modelTag.Class != TagClasses.GbxModel )
			return null;

		var loading = System.Diagnostics.Stopwatch.StartNew();
		var glass = _objectGlass.For( cache, modelTag );
		var model = glass?.Body ?? Model.Load( HaloCeMount.ModelPath( Host.Ident, modelTag.Path ) );
		_objectModelMilliseconds += loading.ElapsedMilliseconds;
		if ( model is null || model.IsError )
			return null;

		var go = new GameObject( parent, true, System.IO.Path.GetFileName( placement.Object.Path ) );
		go.WorldPosition = (Vector3)placement.Position * HaloCeMount.WorldScale;
		go.WorldRotation = Rotation.LookAt( (Vector3)placement.Forward, (Vector3)placement.Up );
		go.Tags.Add( placement.Kind.Replace( ' ', '_' ) );
		if ( placement.Index >= 0 )
			go.Tags.Add( $"placement_{placement.Kind.Replace( ' ', '_' )}_{placement.Index}" );

		// Attachments the game makes and drives by the object's functions, which the level leaves out.
		var attachments = ObjectAttachments.Read( cache, placement.Object );
		for ( var i = 0; i < attachments.Count; i++ )
		{
			if ( !ObjectAttachmentSites.GameDriven( cache, placement, attachments[i].Type, attachments[i].PrimaryScale, attachments[i].ChangeColor ) )
				continue;
			go.Tags.Add( ObjectAttachmentSites.GameAttachmentsTag );
			go.Tags.Add( ObjectAttachmentSites.GameAttachmentTag( i ) );
		}
		// A campaign mission's machines and controls move: the game poses them by their position.
		var device = solo && placement.Kind is "machine" or "control";
		ObjectAnimationPicker.AddRenderer( go, model, cache, placement, device, out var animation );

		// The model shader finds the object's lighting and change colors through the tint.
		var renderer = go.GetComponent<ModelRenderer>();
		renderer.Tint = _appearance?.TintFor( cache, placement ) ?? ObjectAppearance.ChangeColorTint( cache, placement );
		if ( ShadowlessObjects.Contains( placement.Kind ) || ObjectTags.DoesNotCastShadow( cache, placement.Object ) )
			renderer.RenderType = ModelRenderer.ShadowRenderType.Off;
		// Without a prediction (a multiplayer map, or an object only scripts create), Halo's random pick at a seed from the position.
		var permutations = _permutations?.GetValueOrDefault( (placement.Kind, placement.Index) )
			?? InitialPermutations.ChooseRegionPermutations( cache, placement.Object, placement.DesiredPermutation, new HaloRandom( (uint)(StableRandom.At( placement.Position ) * uint.MaxValue) ) );
		SetPermutations( renderer, cache, modelTag, permutations );
		if ( glass is not null )
			ObjectGlass.AddRenderers( glass, renderer );
		if ( ObjectTags.TransparencySelfOccludes( cache, placement.Object ) && Model.Load( $"mount://{Host.Ident}/{ObjectSelfOcclusion.PrepassResourceName( modelTag.Path )}.vmdl" ) is { IsError: false } prepass )
			ObjectSelfOcclusion.AddRenderer( prepass, renderer );

		// Lights, flares and effects on parts the animation moves go with them, and the flares of
		// lights on parts the game poses a campaign device by (door lock lights).
		if ( renderer is SkinnedModelRenderer animated )
		{
			if ( animation is { } playing )
			{
				foreach ( var attachment in MovingAttachments.Build( cache, placement, animated, playing, ( light, tint ) => LensFlare( cache, light, tint ) ) )
					_movingAttachments.Add( (placement, attachment) );
			}

			if ( device && animation is not { Looping: true } && ObjectAnimationPicker.DevicePosition( cache, placement ) is { } position )
			{
				foreach ( var attachment in MovingAttachments.Build( cache, placement, animated, position, ( light, tint ) => LensFlare( cache, light, tint ), posed: true ) )
					_carriedFlares.Add( (placement, attachment) );
			}
		}

		// The model's physics is the object's collision model; objects without one (flag bases,
		// shrubs) don't collide in Halo either. A posed device's collision follows its nodes, as
		// Halo tests each collision node where its node is.
		var solid = SolidObjects.Contains( placement.Kind ) ? CollisionFor( cache, placement, modelTag, model, permutations ) : null;
		if ( solid?.Physics is { Parts.Count: > 0 } && device && renderer is SkinnedModelRenderer posed )
		{
			var physics = go.AddComponent<ModelPhysics>();
			physics.Model = solid;
			physics.Renderer = posed;
			physics.MotionEnabled = false;
		}
		else if ( solid?.Physics is { Parts.Count: > 0 } )
		{
			var collider = go.AddComponent<ModelCollider>();
			collider.Model = solid;
			collider.Static = true;
		}

		if ( placement.NotAutomatically )
			go.Enabled = false;

		return go;
	}

	// The model an object collides with: its own, or for one showing other region permutations whose collision differs, a collision-only model of theirs (null if they have none).
	Model CollisionFor( CacheFile cache, ObjectPlacement placement, TagEntry modelTag, Model model, int[] permutations )
	{
		if ( permutations is null || permutations.All( p => p == 0 ) )
			return model;
		if ( ObjectTags.GetObjectCollisionModel( cache, placement.Object ) is not { } collisionTag || collisionTag.Class != CollisionModelTag.Class )
			return model;

		try
		{
			if ( !_collisionTags.TryGetValue( collisionTag.Id, out var collision ) )
				_collisionTags[collisionTag.Id] = collision = new CollisionModelTag( cache, collisionTag );

			// Halo picks each node's BSP by its region's permutation, clamped to the BSPs it has; ones that land on the first BSPs keep the model's own collision.
			var nodes = Enumerable.Range( 0, collision.Nodes.Count );
			var bsps = nodes.Select( n => collision.NodeBspIndex( n, permutations ) ).ToArray();
			if ( bsps.SequenceEqual( nodes.Select( n => collision.NodeBspIndex( n, null ) ) ) )
				return model;

			var key = (modelTag.Id, collisionTag.Id, string.Join( "_", bsps.Select( b => b < 0 ? "x" : b.ToString() ) ));
			if ( _permutationCollision.TryGetValue( key, out var built ) )
				return built;

			built = GbxModelBuilder.BuildCollision( $"haloce/{cache.Name}/collision/{HaloCeMount.ResourcePath( modelTag.Path )}_{key.Item3}.vmdl", cache, new GbxModelTag( cache, modelTag ), collision, permutations );
			_permutationCollision[key] = built;
			if ( built is not null )
				_models.Add( built );
			return built;
		}
		catch ( Exception e )
		{
			Host.Logger.Warning( $"{cache.Name}: couldn't build the collision of {placement.Object.Path}'s permutations: {e.Message}" );
			return model;
		}
	}

	// Shows each region's permutation: GbxModelBuilder makes a region with several a body group named after it, a choice per permutation in tag order.
	// A runtime-built model's mask starts with every choice of every group on, and ModelRenderer.SetBodyGroup leaves it so.
	// The mask has one bit per choice, the parts in order, so each region's group is set to exactly its permutation.
	static void SetPermutations( ModelRenderer renderer, CacheFile cache, TagEntry modelTag, int[] permutations )
	{
		if ( renderer.Model is not { } model )
			return;
		var data = cache.TagData;
		var regions = data.Block( modelTag.Address + 0xC4, 0x4C ).Select( entry => data.CString( entry, 32 ) ).ToList();
		var mask = renderer.BodyGroups;
		var bit = 0;
		foreach ( var part in model.Parts.All )
		{
			var count = part.Choices.Count;
			if ( bit + count > 64 )
				break;
			var region = regions.IndexOf( part.Name );
			if ( region >= 0 )
			{
				var choice = region < permutations.Length && permutations[region] < count ? permutations[region] : 0;
				var partMask = (count == 64 ? ulong.MaxValue : (1UL << count) - 1) << bit;
				mask = (mask & ~partMask) | (1UL << (bit + choice));
			}
			bit += count;
		}
		renderer.BodyGroups = mask;
	}
}
