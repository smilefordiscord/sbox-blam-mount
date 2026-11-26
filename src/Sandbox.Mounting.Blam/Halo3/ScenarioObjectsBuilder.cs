using Blam.Halo3;
using Blam.Halo3.Tags;

namespace Sandbox.Mounting.Blam.Halo3;

// The scenario's placed objects, multiplayer spawns and objects, and the sky; what goes in is ScenarioPlan's choice (Blam/Halo3/Tags/ScenarioTagPlan.cs).
static class ScenarioObjectsBuilder
{
	public static void Build( Halo3Mount mount, CacheFile cache, List<Model> models )
	{
		ScenarioPlan plan;
		try
		{
			var scenario = new ScenarioTag( cache.Scenario );
			plan = new ScenarioPlan( scenario, ShownBspMask( scenario ) );
		}
		catch ( Exception e )
		{
			mount.Logger.Warning( e, $"{cache.Name}: couldn't read the scenario: {e.Message}" );
			return;
		}

		var lighting = Halo3ObjectLighting.For( plan );
		var root = new GameObject( true, "objects" );
		GameObject multiplayerRoot = null;
		var placed = 0;
		var missing = 0;
		foreach ( var planned in plan.Objects )
		{
			var placement = planned.Placement;

			// Hung on the sky: drawn with it (BuildSkyScene).
			if ( placement.Sky >= 0 )
				continue;

			try
			{
				var parent = planned.Role is null ? root : multiplayerRoot ??= new GameObject( true, "multiplayer" );
				var go = planned.Marker ? Marker( placement, parent ) : Place( planned, parent );
				if ( go is null )
				{
					missing++;
					continue;
				}

				if ( !planned.Marker )
					lighting?.Light( go, placement );
				go.Tags.Add( Halo3Tag( placement.Kind.ToString() ) );
				if ( planned.Role is not null )
					ScenarioMultiplayer.TagObject( go, placement, planned.Role );

				placed++;
			}
			catch ( Exception e )
			{
				mount.Logger.Warning( e, $"{cache.Name}: couldn't place {placement}: {e.Message}" );
			}
		}

		if ( missing > 0 )
			mount.Logger.Info( $"{cache.Name}: placed {placed} objects, {missing} without a model" );

		ScenarioMultiplayer.BuildSpawns( plan );
		BuildSky( mount, cache, plan );
		Halo3SunLight.Build( lighting );
	}

	// The BSPs whose objects are placed, a bit each.
	// TODO(geometry): keep this in step with the BSPs StructureBspBuilder shows (all of them, or one zone set's mask on campaign maps); every BSP for now.
	internal static ushort ShownBspMask( ScenarioTag scenario ) => ScenarioPlan.AllBsps( scenario );

	static GameObject Place( PlannedObject planned, GameObject parent, bool collide = true )
	{
		var placement = planned.Placement;
		var variant = placement.Variant ?? planned.Object.DefaultVariant;
		var go = PlaceObject( planned.Model, variant, placement.Random, parent, Name( placement ), planned.Object, placement.DevicePosition );
		if ( go is null )
			return null;

		SetTransform( go, placement );

		var simulated = placement.Kind == PlacementKind.Crate && planned.Model?.PhysicsModel is { } phmo && PhysicsModelTag.For( phmo ) is { Simulated: true };
		if ( collide && ScenarioPlan.SolidKinds.Contains( placement.Kind ) )
			AddCollider( go, simulated );

		// Objects the variant attaches at markers (a Warthog's chaingun, a Scorpion's turret), children of the object so they share its probe.
		var frame = new ObjectFrame( placement.Position, placement.Orientation, placement.Scale );
		foreach ( var attached in AttachedObjects.For( placement.Object, frame, placement.Variant ?? planned.Object.DefaultVariant ) )
		{
			var childTag = new ObjectTag( attached.Object );
			var child = PlaceObject( childTag.Model is null ? null : new ObjectModel( childTag.Model ), attached.Variant ?? childTag.DefaultVariant, salt => 0, go,
				System.IO.Path.GetFileName( attached.Object.Path ), childTag );
			if ( child is null )
				continue;

			child.WorldPosition = (Vector3)attached.Frame.Position * Halo3Mount.WorldScale;
			child.WorldRotation = Rotation.LookAt( (Vector3)attached.Frame.Forward, (Vector3)attached.Frame.Up );
			if ( attached.Frame.Scale != 1 )
				child.WorldScale = attached.Frame.Scale;
			child.Tags.Add( "attached" );
			if ( collide && ScenarioPlan.SolidKinds.Contains( placement.Kind ) )
				AddCollider( child, simulated, attached: true );
		}

		return go;
	}

	// The model at the origin, showing a variant: each region's permutation through the model's body groups (Halo3Models: a body group per render model region, choice = permutation index). Null when there's no model.
	static GameObject PlaceObject( ObjectModel hlmt, string variant, Func<int, float> random, GameObject parent, string name, ObjectTag objectTag = null, float devicePosition = -1 )
	{
		if ( hlmt is null )
			return null;

		var model = Model.Load( Halo3Mount.ModelPath( hlmt.Tag.Path ) );
		if ( model is null || model.IsError )
			return null;

		var go = new GameObject( parent, true, name );

		// Animations (h3-animation): what the object plays when the level starts (Halo3ObjectAnimations; a plain ModelRenderer when nothing).
		var renderer = Halo3ObjectAnimations.AddRenderer( go, model, objectTag, hlmt, devicePosition );
		ApplyVariant( renderer, hlmt, variant, random );
		return go;
	}

	static void ApplyVariant( ModelRenderer renderer, ObjectModel hlmt, string variantName, Func<int, float> random )
	{
		var variant = hlmt.FindVariant( variantName );
		if ( variant is null )
			return;

		foreach ( var (region, _, permutation) in ObjectModel.Choose( variant, random ) )
		{
			if ( renderer.Model.Parts.Get( region ) is not { } part || part.Choices.Count == 0 )
				continue;

			// By name when the choices are named after the permutations (the model may come from another map's copy of the tag), else by index.
			var permutationName = PermutationName( variant, region, permutation );
			if ( permutationName is not null && part.Choices.Any( c => c.Name == permutationName ) )
				renderer.SetBodyGroup( region, permutationName );
			else if ( permutation >= 0 && permutation < part.Choices.Count )
				renderer.SetBodyGroup( region, permutation );
			else if ( permutation < 0 )
			{
				// A variant hiding a region: the model's empty last choice "none" (RenderModelBuilder.NoChoice).
				for ( var i = 0; i < part.Choices.Count; i++ )
				{
					if ( string.IsNullOrEmpty( part.Choices[i].Name ) || part.Choices[i].Name == "none" )
					{
						renderer.SetBodyGroup( region, i );
						break;
					}
				}
			}
		}
	}

	static string PermutationName( ObjectModel.Variant variant, string region, int permutation )
	{
		if ( permutation < 0 )
			return null;

		var listed = variant.Regions.FirstOrDefault( r => r.Name == region );
		return listed?.Permutations.FirstOrDefault( p => p.RenderModelPermutation == permutation )?.Name;
	}

	// The name of the variant FindVariant picks (the first when the name is empty or stale), which change colours are chosen by.
	static string VariantName( ObjectModel hlmt, string variant ) => hlmt?.FindVariant( variant )?.Name ?? variant;

	// Change colours: the placement's where it sets them, else the object's for its variant (a Spartan's olive, a crate's paint, the flag's white), on the object and the objects attached to it. Renderer attributes aren't saved with a mounted scene, so Halo3ObjectLighting.ApplyOnce calls this on the live scene.
	internal static void ApplyChangeColors( GameObject go, PlannedObject planned )
	{
		var placement = planned.Placement;
		var variant = placement.Variant ?? planned.Object.DefaultVariant;
		SetChangeColors( go, planned.Object.InitialChangeColors( VariantName( planned.Model, variant ), placement.Position, placement.ActiveChangeColors, placement.ChangeColors ) );

		var attachedChildren = go.Children.Where( c => c.Tags.Has( "attached" ) ).ToList();
		var next = 0;
		var frame = new ObjectFrame( placement.Position, placement.Orientation, placement.Scale );
		foreach ( var attached in AttachedObjects.For( placement.Object, frame, variant ) )
		{
			var name = System.IO.Path.GetFileName( attached.Object.Path );
			var index = attachedChildren.FindIndex( next, c => c.Name == name );
			if ( index < 0 )
				continue;
			next = index + 1;
			var childTag = new ObjectTag( attached.Object );
			SetChangeColors( attachedChildren[index], childTag.InitialChangeColors( VariantName( childTag.Model is null ? null : new ObjectModel( childTag.Model ), attached.Variant ?? childTag.DefaultVariant ), attached.Frame.Position ) );
		}
	}

	// Colours neither the placement nor the object gives stay white, the shader's neutral. Halo's colours are gamma; halo3_shader works in linear.
	static void SetChangeColors( GameObject go, System.Numerics.Vector3?[] colors )
	{
		if ( colors.All( c => c is null ) )
			return;
		foreach ( var renderer in go.GetComponents<ModelRenderer>() )
		{
			for ( var k = 0; k < 4; k++ )
			{
				var c = k < colors.Length && colors[k] is { } given ? given : System.Numerics.Vector3.One;
				renderer.Attributes.Set( $"Halo3ChangeColor{k}", new Vector4( SrgbToLinear( c.X ), SrgbToLinear( c.Y ), SrgbToLinear( c.Z ), 1 ) );
			}
			renderer.Attributes.Set( "Halo3HasChangeColors", 1f );
		}
	}

	static float SrgbToLinear( float c ) => c <= 0.04045f ? c / 12.92f : MathF.Pow( (c + 0.055f) / 1.055f, 2.4f );

	// A placement without a model to show: only its transform and name.
	static GameObject Marker( ScenarioPlacement placement, GameObject parent )
	{
		var go = new GameObject( parent, true, Name( placement ) );
		SetTransform( go, placement );
		return go;
	}

	// Halo's axes are s&box's (x forward, y left, z up); positions are world units.
	static void SetTransform( GameObject go, ScenarioPlacement placement )
	{
		go.WorldPosition = (Vector3)placement.Position * Halo3Mount.WorldScale;
		go.WorldRotation = Rotation.LookAt( (Vector3)placement.Forward, (Vector3)placement.Up );
		if ( placement.Scale != 1 )
			go.WorldScale = placement.Scale;
	}

	internal static string Name( ScenarioPlacement placement )
	{
		var file = System.IO.Path.GetFileName( placement.Object.Path );
		return string.IsNullOrEmpty( placement.Name ) ? file : $"{file} ({placement.Name})";
	}

	// SoundScenery → halo3_sound_scenery, Teleporter2Way → halo3_teleporter_2way.
	internal static string Halo3Tag( string name )
	{
		var s = new System.Text.StringBuilder( "halo3" );
		for ( var i = 0; i < name.Length; i++ )
		{
			var c = name[i];
			if ( i == 0 || (char.IsUpper( c ) || char.IsDigit( c )) && !char.IsDigit( name[i - 1] ) )
				s.Append( '_' );
			s.Append( char.ToLowerInvariant( c ) );
		}

		return s.ToString();
	}

	// Collision for an object whose model has physics: static, or for one playing an animation following its nodes, as Halo tests each collision BSP where its node is (Zanzibar's wheel blades, an open door).
	// A crate whose physics model Halo simulates (cones, bins, boxes) falls and can be pushed, with the phmo's mass and damping the model's bodies carry; an object attached to it joins its body.
	static void AddCollider( GameObject go, bool simulated = false, bool attached = false )
	{
		var renderer = go.GetComponent<ModelRenderer>();
		var model = renderer?.Model;
		if ( model?.Physics is not { Parts.Count: > 0 } )
			return;

		if ( simulated && renderer is not SkinnedModelRenderer { Sequence.Name.Length: > 0 } )
		{
			AddPhysics( go, renderer, model, attached );
			return;
		}

		if ( renderer is SkinnedModelRenderer { Sequence.Name.Length: > 0 } animated )
		{
			var physics = go.AddComponent<ModelPhysics>();
			physics.Model = model;
			physics.Renderer = animated;
			physics.MotionEnabled = false;
			return;
		}

		var collider = go.AddComponent<ModelCollider>();
		collider.Model = model;
		collider.Static = true;
	}

	// As s&box's Prop: one body is a collider with a rigidbody, several (jointed as in their default pose) a ModelPhysics.
	static void AddPhysics( GameObject go, ModelRenderer renderer, Model model, bool attached )
	{
		if ( model.Physics.Parts.Count > 1 && !attached && renderer is SkinnedModelRenderer skinned )
		{
			var physics = go.AddComponent<ModelPhysics>();
			physics.Model = model;
			physics.Renderer = skinned;
			return;
		}

		var collider = go.AddComponent<ModelCollider>();
		collider.Model = model;
		collider.Static = false;
		if ( attached )
			return;

		var part = model.Physics.Parts[0];
		var body = go.AddComponent<Rigidbody>();
		body.MassOverride = model.Physics.Parts.Sum( p => p.Mass );
		body.LinearDamping = part.LinearDamping;
		body.AngularDamping = part.AngularDamping;
	}

	// The sky: a scenery object (scen → hlmt → mode) whose model is the world around the level in world units, at the origin (Blackout's ice floes 50-900 world units out, domes ~9,000).
	// It's drawn as s&box's 3D skybox, like MCC's own sky pass: after the level's opaque geometry where nothing covers it, with its own depth (so no far plane cuts it and its layers sort by their true distance) and its true parallax.
	// The sky scene (skies/<map>.scene, BuildSkyScene) holds the model this many times smaller; the skybox draws it this much larger again.
	public const float SkyboxScale = 64;

	public static string SkyScenePath( CacheFile cache ) => $"skies/{cache.Name}";

	// The shown sky's model lighting, what MCC lights the sky and any object with no lightmap under it with (RenderModelTag.Lighting); null without a sky model.
	public static LightProbe SkyLighting( ScenarioTag scenario ) => SkyLighting( new ScenarioPlan( scenario, ShownBspMask( scenario ) ) );

	public static LightProbe SkyLighting( ScenarioPlan plan ) =>
		plan.Sky is { } sky && plan.Model( plan.Object( sky.Object )?.Model )?.RenderModel is { } mode ? new RenderModelTag( mode ).Lighting : null;

	static void BuildSky( Halo3Mount mount, CacheFile cache, ScenarioPlan plan )
	{
		if ( plan.Sky is null )
			return;

		var path = $"mount://{mount.Ident}/{SkyScenePath( cache )}.scene";
		if ( ResourceLibrary.Get<SceneFile>( path ) is not { } sceneFile )
		{
			mount.Logger.Warning( $"{cache.Name}: the sky scene {path} isn't there" );
			return;
		}

		var go = new GameObject( true, "sky" );
		go.Tags.Add( "sky" );
		go.WorldScale = SkyboxScale;
		go.AddComponent<Skybox3D>().SkyboxScene = sceneFile;
	}

	// The sky scene's contents: the sky model at 1 / SkyboxScale, lit by its vertex colours.
	public static void BuildSkyScene( Halo3Mount mount, CacheFile cache, List<Model> models )
	{
		ScenarioPlan plan;
		try
		{
			var scenario = new ScenarioTag( cache.Scenario );
			plan = new ScenarioPlan( scenario, ShownBspMask( scenario ) );
		}
		catch ( Exception e )
		{
			mount.Logger.Warning( e, $"{cache.Name}: couldn't read the scenario: {e.Message}" );
			return;
		}
		if ( plan.Sky is not { } sky )
			return;

		try
		{
			var objectTag = plan.Object( sky.Object );
			var hlmt = plan.Model( objectTag.Model );
			if ( hlmt?.RenderModel is not { } renderModel )
			{
				mount.Logger.Info( $"{cache.Name}: the sky {sky.Object} has no model" );
				return;
			}

			// The opaque sky in one model, each translucent layer in its own (RenderModelBuilder.SkyLayer: s&box sorts objects, not meshes).
			var layers = new List<Model>();
			var model = RenderModelBuilder.Build( $"{Halo3Mount.ResourcePath( hlmt.Tag.Path )}__skybox.vmdl", new RenderModelTag( renderModel ), new ModelTag( hlmt.Tag ), Halo3Mount.WorldScale / SkyboxScale, SkyboxScale, layers );
			var root = new GameObject( true, System.IO.Path.GetFileName( sky.Object.Path ) );
			root.Tags.Add( "sky" );
			foreach ( var part in layers.Prepend( model ).Where( m => m is not null ) )
			{
				models.Add( part );
				var go = new GameObject( root, true, part.Name );
				var renderer = go.AddComponent<ModelRenderer>();
				renderer.Model = part;
				renderer.RenderType = ModelRenderer.ShadowRenderType.Off;
			}
		}
		catch ( Exception e )
		{
			mount.Logger.Warning( e, $"{cache.Name}: couldn't build the sky {sky.Object}: {e.Message}" );
		}

		BuildSkyObjects( mount, cache, plan );
	}

	// Tags the objects BuildSkyObjects places (Halo3SceneUpkeep gives them the scene's fog and clock).
	public const string SkyObjectTag = "halo3_sky_object";

	// The objects hung on the sky's markers (Standoff's dishes, Midship's capital ships), hundreds of world units out: in the sky scene, so the skybox draws them with the sky (lit by its probe, fogged and animated by Halo3SceneUpkeep.ShareHdrWithSky).
	static void BuildSkyObjects( Halo3Mount mount, CacheFile cache, ScenarioPlan plan )
	{
		GameObject root = null;
		foreach ( var planned in plan.Objects )
		{
			if ( planned.Placement.Sky < 0 || planned.Marker )
				continue;

			try
			{
				root ??= new GameObject( true, "sky objects" );
				if ( Place( planned, root, collide: false ) is { } go )
				{
					go.Tags.Add( "sky" );
					go.Tags.Add( SkyObjectTag );
					foreach ( var renderer in go.GetComponentsInChildren<ModelRenderer>( true, true ) )
						renderer.RenderType = ModelRenderer.ShadowRenderType.Off;
				}
			}
			catch ( Exception e )
			{
				mount.Logger.Warning( e, $"{cache.Name}: couldn't place {planned.Placement} on the sky: {e.Message}" );
			}
		}

		// Placed in world units about the origin, then shrunk with everything else in the sky scene.
		if ( root is not null )
			root.WorldScale = 1 / SkyboxScale;
	}
}
