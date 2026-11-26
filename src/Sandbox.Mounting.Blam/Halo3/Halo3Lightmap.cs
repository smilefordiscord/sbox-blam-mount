using Blam.Halo3;
using Blam.Halo3.Tags;

namespace Sandbox.Mounting.Blam.Halo3;

// One BSP's static lighting (its Lbsp): the SH lightmap texture arrays with their decode scales, and its probes.
// halo3_shader decodes the arrays per pixel; both must be registered as 2D array textures at Halo3Mount.TexturePath( path ) by the textures loader.
sealed class Halo3Lightmap
{
	public LightmapBspSh Data { get; }

	// The lightprobe array (8 DXT5 layers: SH c0..c3 as pairs) and the dominant light intensity array (2 layers).
	public string LightprobeArrayPath { get; }
	public string IntensityArrayPath { get; }

	// c0, c1, c2, c3, dominant intensity.
	public float[] Scales => Data.Scales;

	Halo3Lightmap( LightmapBspSh data )
	{
		Data = data;
		LightprobeArrayPath = data.LightprobeArray is { } lp ? Halo3Mount.TexturePath( lp.Path ) : null;
		IntensityArrayPath = data.IntensityArray is { } intensity ? Halo3Mount.TexturePath( intensity.Path ) : null;
	}

	static readonly System.Runtime.CompilerServices.ConditionalWeakTable<TagEntry, Halo3Lightmap> Cache = new();

	// The lighting of an Lbsp (scenario_lightmap_bsp_data, from sLdT +0x04 per BSP), or null without one.
	public static Halo3Lightmap ForBsp( CacheFile cache, TagEntry lightmapBsp )
	{
		if ( lightmapBsp is null || lightmapBsp.Data.IsNull )
			return null;
		return Cache.GetValue( lightmapBsp, t => new Halo3Lightmap( new LightmapBspSh( t ) ) );
	}

	// The arrays (loaded through the resource system each time, not held here, so they go with the scenes using them), or null where a texture is missing or not an array (so halo3_shader falls back to neutral light).
	public Texture LightprobeArray => LoadArray( LightprobeArrayPath );
	public Texture IntensityArray => LoadArray( IntensityArrayPath );

	static Texture LoadArray( string path )
	{
		if ( path is null )
			return null;
		var texture = Texture.Load( path );
		return texture is null || texture.IsError || texture.Depth < 2 ? null : texture;
	}

	// An instance's probe (Lbsp instances +0x118 → probes +0x124), for instances lit by a single probe; null for none.
	public ShProbe? InstanceProbe( int instance )
	{
		var index = Data.InstanceProbeIndex( instance );
		if ( index < 0 || index >= Data.ProbeCount )
			return null;
		var probe = Data.Probe( index );
		return probe.IsZero ? null : probe;
	}

	public ShProbe? Probe( int index ) => index >= 0 && index < Data.ProbeCount ? Data.Probe( index ) : null;

	// A placed scenery object's probe by its unique id (scnr scenery +0x38), if this BSP baked one.
	public ShProbe? SceneryProbe( uint objectId ) => Data.SceneryProbe( objectId ) is { IsZero: false } probe ? probe : null;

	// Gives a renderer's materials (halo3_shader with Halo3Lighting.Probe) an L2 SH probe: renderer.Attributes or sceneObject.Attributes.
	public static void SetProbe( RenderAttributes attributes, ShProbe probe )
	{
		SetProbe( attributes, probe.Coefficients );
		SetSun( attributes, probe.Direction, probe.Intensity );
	}

	// The same from a scenario probe or lightmap sample: its coefficients and its dominant light (the analytic light of the material models' specular, and what the sun's shadow takes away).
	public static void SetProbe( RenderAttributes attributes, LightProbe probe )
	{
		if ( probe is null )
			return;
		SetProbe( attributes, probe.Coefficients );
		SetSun( attributes, probe.DominantDirection, probe.DominantIntensity );
	}

	static void SetSun( RenderAttributes attributes, System.Numerics.Vector3 direction, System.Numerics.Vector3 intensity )
	{
		if ( attributes is null )
			return;
		var has = direction.LengthSquared() > 1e-6f && intensity.LengthSquared() > 0 ? 1 : 0;
		attributes.Set( "Halo3ProbeSun", new Vector4( direction.X, direction.Y, direction.Z, has ) );
		attributes.Set( "Halo3ProbeSunColor", new Vector4( intensity.X, intensity.Y, intensity.Z, 0 ) );
	}

	// The same from 9 RGB coefficients (D3DX order).
	public static void SetProbe( RenderAttributes attributes, IReadOnlyList<System.Numerics.Vector3> coefficients )
	{
		if ( attributes is null || coefficients is null || coefficients.Count < 9 )
			return;
		for ( var k = 0; k < 9; k++ )
		{
			var c = coefficients[k];
			attributes.Set( $"Halo3ProbeSh{k}", new Vector4( c.X, c.Y, c.Z, 0 ) );
		}
		attributes.Set( "Halo3HasProbe", 1f );
	}

	// Back to the neutral ambient.
	public static void ClearProbe( RenderAttributes attributes ) => attributes?.Set( "Halo3HasProbe", 0f );

	// Gives a renderer's dynamic environment_mapping materials a baked cube map (halo3_shader's Halo3EnvMap): what a placed object reflects.
	public static void SetCubemap( RenderAttributes attributes, Halo3Cubemap cubemap )
	{
		if ( attributes is null || cubemap is null )
			return;
		var loaded = Texture.Load( Halo3Mount.TexturePath( cubemap.Bitmap.Path, cubemap.Index ) );
		if ( loaded is null || loaded.IsError )
			return;
		attributes.Set( "Halo3EnvMap", loaded );
		attributes.Set( "Halo3EnvMapSrgb", ShaderBitmaps.IsSrgb( cubemap.Bitmap, cubemap.Index ) ? 1f : 0f );
		attributes.Set( "Halo3HasEnvMap", 1f );
	}

	// An object's change colours (primary..quaternary, linear RGB) for the change colour albedo and self-illumination options.
	public static void SetChangeColors( RenderAttributes attributes, params System.Numerics.Vector3[] colors )
	{
		if ( attributes is null )
			return;
		for ( var k = 0; k < 4; k++ )
		{
			var c = k < colors.Length ? colors[k] : System.Numerics.Vector3.One;
			attributes.Set( $"Halo3ChangeColor{k}", new Vector4( c.X, c.Y, c.Z, 1 ) );
		}
		attributes.Set( "Halo3HasChangeColors", 1f );
	}
}

// Lights a scenario's placed objects from its lightmap probes: scenery by its own baked probe, machines by their nearest baked one, everything else (vehicles, weapons, crates, equipment) from the lightmap under it as MCC does (LightmapSampler), else by the sky model's lighting as MCC does, else by the nearest probe there is (non-zero air probes and other placements' baked probes).
sealed class Halo3ObjectLighting
{
	readonly ScenarioLightmapProbes _probes;
	readonly LightmapSampler _sampler;
	readonly List<(System.Numerics.Vector3 Position, LightProbe Probe)> _nearby = new();

	// The BSPs' baked cube maps at the scenario's cube map points (StructureBspBuilder: the point's index names the image of the same number in the BSP's cubemaps bitmap), what objects reflect (MCC's dynamic_environment_map_0 is the nearest).
	readonly List<(System.Numerics.Vector3 Position, Halo3Cubemap Cubemap)> _cubemaps = new();

	// The sun: every probe's dominant light direction, weighted by its intensity's luminance, and the mean intensity.
	System.Numerics.Vector3 _sunDirection;
	System.Numerics.Vector3 _sunIntensity;
	float _sunWeight;

	// The sky model's lighting: MCC lights an object with no lightmap under it (out over Zanzibar's sea) by it.
	readonly LightProbe _sky;

	Halo3ObjectLighting( ScenarioLightmapProbes probes, ScenarioPlan plan, LightmapSampler sampler )
	{
		_probes = probes;
		_sampler = sampler;
		try
		{
			_sky = ScenarioObjectsBuilder.SkyLighting( plan );
		}
		catch ( Exception )
		{
			// Without a readable sky model such objects take the nearest probe.
		}
		foreach ( var air in probes.AirProbes )
		{
			if ( !air.Probe.IsZero )
				_nearby.Add( (air.Position, air.Probe) );
		}
		foreach ( var planned in plan.Objects )
		{
			if ( probes.ForScenery( planned.Placement ) is { IsZero: false } probe )
			{
				_nearby.Add( (planned.Placement.Position, probe) );
				AddSun( probe );
			}
		}

		try
		{
			var bsps = new ScenarioStructureBsps( plan.Scenario.Tag.Cache );
			foreach ( var reference in bsps.SceneBsps )
			{
				if ( reference.Cubemaps is null )
					continue;
				var images = BitmapTag.ImagesOf( reference.Cubemaps ).Count;
				for ( var k = 0; k < Math.Min( images, bsps.CubemapPoints.Count ); k++ )
					_cubemaps.Add( (bsps.CubemapPoints[k], new Halo3Cubemap( reference.Cubemaps, k )) );
			}
		}
		catch ( Exception )
		{
			// Without readable cube maps objects reflect the shader's grey.
		}
	}

	// The baked cube map nearest a position, or null.
	public Halo3Cubemap CubemapFor( System.Numerics.Vector3 position ) =>
		_cubemaps.Count == 0 ? null : _cubemaps.MinBy( c => System.Numerics.Vector3.DistanceSquared( c.Position, position ) ).Cubemap;

	// The scenario's object lighting, or null without a lightmap.
	public static Halo3ObjectLighting For( ScenarioPlan plan ) => ScenarioLightmapProbes.For( plan.Scenario ) is { } probes ? new Halo3ObjectLighting( probes, plan, LightmapSampler.For( plan.Scenario.Tag.Cache ) ) : null;

	public LightProbe ProbeFor( ScenarioPlacement placement )
	{
		if ( _probes.ForScenery( placement ) is { IsZero: false } own )
			return own;
		if ( _probes.ForMachine( placement ) is { Probes.Count: > 0 } machine )
			return machine.Probes.MinBy( p => System.Numerics.Vector3.DistanceSquared( p.Position, placement.Position ) ).Probe;
		if ( _sampler is not null && SampleObject( placement ) is { } sample )
		{
			var probe = sample.ToProbe();
			AddSun( probe );
			return probe;
		}
		if ( _sky is not null )
			return _sky;
		return _nearby.Count == 0 ? null : _nearby.MinBy( p => System.Numerics.Vector3.DistanceSquared( p.Position, placement.Position ) ).Probe;
	}

	// The lightmap under a placed object as Halo 3 samples it: down from the top of its bounding sphere (an origin a little under the ground, as the Behemoth's on Sandtrap, found nothing and took the sky's light).
	LightmapSample? SampleObject( ScenarioPlacement placement )
	{
		if ( !ObjectTag.IsObject( placement.Object ) )
			return _sampler.Sample( placement.Position );
		var tag = new ObjectTag( placement.Object );
		var scale = placement.Scale > 0 ? placement.Scale : 1;
		var forward = placement.Forward;
		var up = placement.Up;
		var left = System.Numerics.Vector3.Cross( up, forward );
		var o = tag.BoundingOffset;
		var center = placement.Position + (forward * o.X + left * o.Y + up * o.Z) * scale;
		return _sampler.SampleObject( center, tag.BoundingRadius * scale );
	}

	void AddSun( LightProbe probe )
	{
		var w = System.Numerics.Vector3.Dot( probe.DominantIntensity, new System.Numerics.Vector3( 0.212656f, 0.715158f, 0.072186f ) );
		if ( w <= 0 || probe.DominantDirection.LengthSquared() < 1e-6f )
			return;
		_sunDirection += probe.DominantDirection * w;
		_sunIntensity += probe.DominantIntensity * w;
		_sunWeight += w;
	}

	// The direction toward the sun (unit, s&box axes) and its intensity, from every probe seen so far; null where nothing is lit by a dominant light.
	public (Vector3 Direction, Vector3 Intensity)? Sun
	{
		get
		{
			if ( _sunWeight <= 0 || _sunDirection.LengthSquared() < 1e-6f )
				return null;
			var d = System.Numerics.Vector3.Normalize( _sunDirection );
			var i = _sunIntensity / _sunWeight;
			return (new Vector3( d.X, d.Y, d.Z ), new Vector3( i.X, i.Y, i.Z ));
		}
	}

	// Tags a placed object with its placement (renderer attributes aren't saved with a mounted scene: ApplyOnce sets them on the live scene) and lights it now too.
	public void Light( GameObject go, ScenarioPlacement placement )
	{
		go.Tags.Add( PlacementTag( placement ) );
		var cubemap = CubemapFor( placement.Position );
		var probe = ProbeFor( placement );
		foreach ( var renderer in go.GetComponentsInChildren<ModelRenderer>( true, true ) )
		{
			Halo3Lightmap.SetProbe( renderer.Attributes, probe );
			Halo3Lightmap.SetCubemap( renderer.Attributes, cubemap );
		}
	}

	const string PlacementTagPrefix = "h3p_";

	// A placement's tag: its kind and index in the scenario.
	public static string PlacementTag( ScenarioPlacement placement ) => $"{PlacementTagPrefix}{(int)placement.Kind}_{placement.Index}";

	static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Scene, object> Lit = new();

	// Lights a live scene's placed objects (once per scene): each renderer under an object tagged by Light gets its placement's probe.
	public static void ApplyOnce( Scene scene, CacheFile cache )
	{
		if ( scene is null || cache is null || Lit.TryGetValue( scene, out _ ) )
			return;
		Lit.Add( scene, null );

		// Every renderer takes the scene's exposure and fog (shared models' materials may carry another map's).
		foreach ( var renderer in scene.GetAllComponents<ModelRenderer>() )
			Halo3Materials.SetSceneLook( renderer.Attributes, cache );

		var scenario = new ScenarioTag( cache.Scenario );
		var plan = new ScenarioPlan( scenario, ScenarioObjectsBuilder.ShownBspMask( scenario ) );
		if ( For( plan ) is not { } lighting )
			return;

		var placements = new Dictionary<string, PlannedObject>();
		foreach ( var planned in plan.Objects )
			placements.TryAdd( PlacementTag( planned.Placement ), planned );

		foreach ( var renderer in scene.GetAllComponents<ModelRenderer>() )
		{
			var tag = TagOf( renderer.GameObject ) ?? TagOf( renderer.GameObject?.Parent );
			if ( tag is not null && placements.TryGetValue( tag, out var planned ) )
			{
				Halo3Lightmap.SetProbe( renderer.Attributes, lighting.ProbeFor( planned.Placement ) );
				Halo3Lightmap.SetCubemap( renderer.Attributes, lighting.CubemapFor( planned.Placement.Position ) );
			}
		}

		// Change colours, from the objects that carry a placement's tag (their attached objects with them).
		foreach ( var go in scene.GetAllObjects( true ) )
		{
			if ( TagOf( go ) is { } tag && placements.TryGetValue( tag, out var planned ) && planned.Object is not null )
			{
				try
				{
					ScenarioObjectsBuilder.ApplyChangeColors( go, planned );
				}
				catch ( Exception e )
				{
					Halo3Mount.Current?.Logger.Warning( e, $"Couldn't colour {go.Name}: {e.Message}" );
				}
			}
		}
	}

	static string TagOf( GameObject go ) => go?.Tags.TryGetAll().FirstOrDefault( t => t.StartsWith( PlacementTagPrefix, StringComparison.Ordinal ) );
}
