using Blam.Halo2;
using Blam.Halo2.Tags;

namespace Sandbox.Mounting.Blam.Halo2;

/// <summary>
/// Builds a scene for a Halo 2 map: every structure BSP as a static model with Halo's collision (surfaces named
/// after Halo 2's materials) and its open water, and a spawn point at each player starting location.
/// </summary>
sealed class ScenarioSceneLoader( string mapPath ) : SceneLoader<BaseGameMount>
{
	// See Halo2Loader.
	new Halo2Mount Host => Halo2Mount.Current;

	// The level's structure, which particles trace against.
	public const string LevelTag = "halo2_level";

	// Models built in code only exist while something holds on to them.
	readonly List<Model> _models = new();


	/// <summary>
	/// The open water (oceans), reflecting a cube rendered once from just above the water at the level's centre
	/// (<see cref="SceneUpkeep"/>), where Halo mirrored the scene in the plane every frame.
	/// </summary>
	void AddOpenWater( string name, Model openWater, WaterDefinition water, BBox levelBounds )
	{
		_models.Add( openWater );
		var go = new GameObject( true, $"{name} open water" );
		go.AddComponent<ModelRenderer>().Model = openWater;
		go.Tags.Add( SceneUpkeep.WaterTag );

		var probe = new GameObject( true, $"{name} open water reflection point" );
		probe.WorldPosition = new Vector3( levelBounds.Center.x, levelBounds.Center.y, (water.Height + ReflectionHeight) * Halo2Mount.WorldScale );
		probe.Tags.Add( SceneUpkeep.ProbeTag );
	}

	/// <summary>
	/// The BSP's placed decals and its decorators (grass, rocks, debris, stains): see <see cref="DecalModels"/>.
	/// </summary>
	void AddDecalsAndDecorators( string name, string modelName, StructureBspTag bsp, ScenarioTag scenario, IReadOnlyList<DecoratorSetTag> decoratorSets )
	{
		var decorators = new StructureDecorators( bsp, decoratorSets );

		if ( DecalModels.BuildDecals( modelName, bsp, scenario.DecalPalette, decorators ) is { } decals )
		{
			_models.Add( decals );
			var go = new GameObject( true, $"{name} decals" );
			go.AddComponent<ModelRenderer>().Model = decals;
			go.Tags.Add( SceneUpkeep.DecalTag );
		}

		var models = DecalModels.BuildDecorators( modelName, decorators );
		if ( models.Count == 0 )
			return;

		var parent = new GameObject( true, $"{name} decorators" );
		foreach ( var model in models )
		{
			_models.Add( model );
			var go = new GameObject( parent, true, System.IO.Path.GetFileNameWithoutExtension( model.Name ) );
			go.AddComponent<ModelRenderer>().Model = model;
		}
	}

	/// <summary>
	/// The colour Halo clears the frame to: the sky's clear colour when it uses one, else black.
	/// </summary>
	static Vector3 SkyClearColor( ScenarioTag scenario )
	{
		var skyTag = scenario.Skies.FirstOrDefault( s => s.Class == SkyTag.Class && s.Address != 0 );
		var sky = skyTag is null ? null : new SkyTag( skyTag );
		return sky is { UseClearColor: true } ? (Vector3)sky.ClearColor : Vector3.Zero;
	}

	/// <summary>
	/// The sky's atmospheric fog as s&amp;box's gradient fog, which the Halo 2 shaders read and apply Halo's way (see
	/// HaloFog in halo2_opaque.shader): MCC fogs by view depth, from nothing at the start distance to the maximum
	/// density at the opaque distance along a smoothstep (its gradient bitmap, rasterizer\gradients), blending in
	/// gamma space, and leaves the sky alone. s&amp;box's own shaders get its usual fog from the same values.
	/// </summary>
	/// <remarks>
	/// The fog of the first sky, the one the sky model comes from; campaign levels pick a sky per cluster. Not drawn
	/// yet: the secondary fog (only Gemini's second sky has one in multiplayer) and the sky fog over the sky
	/// (Backwash, High Plains).
	/// </remarks>
	static void BuildFog( ScenarioTag scenario )
	{
		var skyTag = scenario.Skies.FirstOrDefault( s => s.Class == SkyTag.Class && s.Address != 0 );
		if ( skyTag is null || new SkyTag( skyTag ).AtmosphericFog is not { MaximumDensity: > 0 } fog )
			return;

		// Halo's colours are gamma space and GradientFog takes linear; the shaders turn it back.
		// GradientFog still sets the gradient fog constants the shaders read; the new Fog component doesn't.
#pragma warning disable CS0618
		var gradient = new GameObject( true, "fog" ).AddComponent<GradientFog>();
#pragma warning restore CS0618
		gradient.Color = new Color( SrgbToLinear( fog.Color.X ), SrgbToLinear( fog.Color.Y ), SrgbToLinear( fog.Color.Z ), Math.Clamp( fog.MaximumDensity, 0, 1 ) );
		gradient.StartDistance = fog.StartDistance * Halo2Mount.WorldScale;
		gradient.EndDistance = MathF.Max( fog.OpaqueDistance, fog.StartDistance + 0.01f ) * Halo2Mount.WorldScale;
		gradient.FalloffExponent = 2;
		gradient.VerticalFalloffExponent = 0;
		gradient.Height = 1000000;
		gradient.WorldPosition = Vector3.Down * 500000;
	}

	static float SrgbToLinear( float c ) =>
		c <= 0.04045f ? c / 12.92f : MathF.Pow( (c + 0.055f) / 1.055f, 2.4f );

	/// <summary>
	/// How high above the open water its reflection is rendered from (world units): about eye height over the sea.
	/// </summary>
	const float ReflectionHeight = 3;

	// The level's visibility map, built once per version of the map file (see SourceVisibility).
	string BuildVisibility( CacheFile cache, List<(int Index, StructureBspTag Bsp)> bsps )
	{
		var file = new FileInfo( cache.FilePath );
		var identity = $"{file.Length}|{file.LastWriteTimeUtc.Ticks}|{string.Join( ',', bsps.Select( b => b.Index ) )}";
		return SourceVisibility.Package( $"halo2_{cache.Name.ToLowerInvariant()}", identity,
			() => (global::Blam.Halo2.WorldVisibility.Build( bsps.Select( b => b.Bsp ).ToList(), Halo2Mount.WorldScale, out var stats ), stats), Host.Logger );
	}

	protected override void BuildScene()
	{
		var cache = Host.GetCache( mapPath );
		var scenario = new ScenarioTag( cache.Scenario );
		var globals = cache.Globals is null ? null : new GlobalsTag( cache.Globals );
		var decoratorSets = scenario.DecoratorPalette.Select( t => t is null || t.Class != DecoratorSetTag.Class ? null : new DecoratorSetTag( t ) ).ToList();

		// Source 2 visibility for the level, built off the main thread the first time while the rest builds.
		var visibilityBsps = scenario.StructureBsps.Where( r => r.Bsp is not null ).Select( r => (r.Index, Bsp: new StructureBspTag( r.Bsp )) ).ToList();
		var visibility = Task.Run( () => BuildVisibility( cache, visibilityBsps ) );

		foreach ( var reference in scenario.StructureBsps )
		{
			if ( reference.Bsp is null )
				continue;

			var name = Halo2Mount.ResourcePath( reference.Bsp.Path );
			var bsp = new StructureBspTag( reference.Bsp );
			var lightmap = reference.Lightmap is null ? null : new LightmapTag( reference.Lightmap );

			// Transparent surfaces are objects of their own, so they sort with everything else (see BuildTransparent).
			foreach ( var transparent in StructureBspModel.BuildTransparent( $"halo2/{name}.vmdl", bsp ) )
			{
				_models.Add( transparent );
				new GameObject( true, $"{System.IO.Path.GetFileName( name )} transparent" ).AddComponent<ModelRenderer>().Model = transparent;
			}

			var parts = StructureBspModel.Build( $"halo2/{name}.vmdl", bsp, lightmap, globals );
			if ( parts is null )
				continue;

			if ( StructureBspModel.BuildOpenWater( $"halo2/{name}.vmdl", bsp, SkyClearColor( scenario ) ) is { } openWater )
				AddOpenWater( System.IO.Path.GetFileName( name ), openWater, bsp.WaterDefinitions[0], parts.Bounds );

			_models.Add( parts.Collision );

			var go = new GameObject( true, System.IO.Path.GetFileName( name ) );
			go.Tags.Add( LevelTag );
			foreach ( var piece in parts.Pieces )
			{
				_models.Add( piece );
				new GameObject( go, true, System.IO.Path.GetFileNameWithoutExtension( piece.Name ) ).AddComponent<ModelRenderer>().Model = piece;
			}

			var collider = go.AddComponent<ModelCollider>();
			collider.Model = parts.Collision;
			collider.Static = true;

			AddDecalsAndDecorators( System.IO.Path.GetFileName( name ), $"halo2/{name}.vmdl", bsp, scenario, decoratorSets );
		}

		try
		{
			SourceVisibility.AddMarker( visibility.GetAwaiter().GetResult(), [] );
		}
		catch ( Exception e )
		{
			Host.Logger.Warning( e, $"{cache.Name}: no Source visibility: {e.Message}" );
		}

		// Objects, multiplayer spawns and flags, and the sky (Halo2/ScenarioObjectsBuilder.cs).
		ScenarioObjectsBuilder.Build( Host, cache, scenario, _models );

		// The sky's suns' lens flares (Halo2/LensFlareModels.cs).
		if ( LensFlareModels.AddSuns( $"halo2/{cache.Name}/sun_flares.vmdl", scenario ) is { } suns )
			_models.Add( suns );

		// Sound scenery, objects' looping sounds, cluster background sounds and reverb (Halo2/LevelSounds.cs).
		LevelSounds.Build( Host, cache, scenario );

		// The sky's and the open water's draw order, the water's reflection, the weather.
		SceneUpkeep.Mark( mapPath );
		SceneUpkeep.Watch();
		BuildFog( scenario );

		foreach ( var start in scenario.PlayerStartingLocations )
		{
			var go = new GameObject( true, "player start" );
			go.WorldPosition = (Vector3)start.Position * Halo2Mount.WorldScale;
			go.WorldRotation = Rotation.FromYaw( start.Facing.RadianToDegree() );
			go.AddComponent<SpawnPoint>();
		}

		// Map thumbnails look out from the first player start at eye height (as in the Halo CE mount).
		if ( scenario.PlayerStartingLocations.Count > 0 )
		{
			var start = scenario.PlayerStartingLocations[0];
			var go = new GameObject( true, "map preview" );
			go.WorldPosition = (Vector3)start.Position * Halo2Mount.WorldScale + Vector3.Up * 64;
			go.WorldRotation = Rotation.From( 10, start.Facing.RadianToDegree(), 0 );
			go.Tags.Add( "map_preview" );
		}
	}
}
