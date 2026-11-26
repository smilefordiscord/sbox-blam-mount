using Blam.Halo2;
using Blam.Halo2.Tags;

namespace Sandbox.Mounting.Blam.Halo2;

/// <summary>
/// Adds what a Halo 2 scenario places to its scene: objects from its palettes, the items and vehicles of its
/// multiplayer spawns, its multiplayer flags as markers, and its sky.
/// </summary>
/// <remarks>
/// <para>Objects are static, in their default pose (animations, devices opening, physics come later). Each
/// is lit the way Halo 2 lights an object (<see cref="ObjectLight"/>): the lightmap under its bounding
/// sphere's centre, turned into direct, indirect and ambient light by the map's global lighting; the values
/// go into the <see cref="ObjectLightingTable"/> and the object's renderer tint holds its row. Objects whose
/// render model has PRT data also get its light (<see cref="PrtLight"/>: the lightmap's lighting environments
/// near the object, turned into its frame), which their <c>prt_*</c> shaders use instead. Scenery the level's
/// lightmap was baked with (<see cref="SceneryObjectLighting"/>: Turf's medical tent, Zanzibar's pillars, most
/// campaign props) is drawn the way Halo draws it, lit by the lightmap colours in its vertices, with a model of its
/// own.</para>
///
/// <para>Bipeds and creatures aren't placed (characters stand in their bind pose until animations exist);
/// neither are objects only scripts create. Objects with a collision model collide (with their render
/// geometry for now, see <see cref="RenderModelBuilder"/>), crates with Halo's physics (<see cref="AddCrateCollider"/>).</para>
/// </remarks>
static class ScenarioObjectsBuilder
{
	/// <summary>
	/// Placement kinds that are placed. Bipeds and creatures wait for animations; sound scenery has no model.
	/// </summary>
	static readonly HashSet<string> PlacedKinds = ["scenery", "vehicle", "equipment", "weapon", "machine", "control", "light fixture", "crate"];

	/// <summary>
	/// Kinds that are solid where their model has collision. Weapons and equipment lying around
	/// shouldn't trip you up.
	/// </summary>
	static readonly HashSet<string> SolidKinds = ["scenery", "vehicle", "machine", "control", "light fixture", "crate"];

	public static void Build( Halo2Mount host, CacheFile cache, ScenarioTag scenario, List<Model> models )
	{
		ScenarioObjects objects;
		try
		{
			objects = new ScenarioObjects( cache.Scenario );
		}
		catch ( Exception e )
		{
			host.Logger.Warning( e, $"{cache.Name}: couldn't read the placed objects: {e.Message}" );
			return;
		}

		// The handles MCC gives the objects (Slayer, the first BSP), for the random_constant their shaders read.
		var spawns = objects.NetgameEquipment.Select( s => (Spawn: s, Placement: s.Place()) ).ToList();
		var (createdPlacements, createdSpawns) = ObjectCreationOrder.Compute( objects.Objects, spawns, cache.Type == CacheType.Multiplayer );
		var created = new Dictionary<ObjectPlacement, ObjectCreationOrder.Created>( ReferenceEqualityComparer.Instance );
		for ( var i = 0; i < objects.Objects.Count; i++ )
		{
			if ( createdPlacements[i] is { } c )
				created[objects.Objects[i]] = c;
		}

		var placements = objects.Objects.Where( p => PlacedKinds.Contains( p.Kind ) && !p.NotAutomatically ).Select( p => (Placement: p, Spawn: (NetgameEquipment)null) ).ToList();
		for ( var s = 0; s < spawns.Count; s++ )
		{
			if ( spawns[s].Placement is { } placement )
			{
				placements.Add( (placement, spawns[s].Spawn) );
				if ( createdSpawns[s] is { } c )
					created[placement] = c;
			}
		}

		// The BSPs are read again here rather than shared with the level's build; it costs a fraction of the
		// build and keeps the two apart.
		List<(StructureBspTag Bsp, LightmapTag Lightmap)> bsps;
		try
		{
			bsps = scenario.StructureBsps.Where( b => b.Bsp is not null )
				.Select( b => (Bsp: new StructureBspTag( b.Bsp ), Lightmap: b.Lightmap is null ? null : new LightmapTag( b.Lightmap )) )
				.ToList();
		}
		catch ( Exception e )
		{
			host.Logger.Warning( e, $"{cache.Name}: couldn't read the BSPs to light the objects: {e.Message}" );
			bsps = [];
		}

		var tints = Light( host, cache, bsps, objects, placements.Select( p => p.Placement ).ToList() );
		var objectModels = new ObjectModels( bsps, models );

		var root = new GameObject( true, "objects" );
		var items = placements.Any( p => p.Spawn is not null ) ? new GameObject( true, "netgame items" ) : null;
		for ( var i = 0; i < placements.Count; i++ )
		{
			var (placement, spawn) = placements[i];
			try
			{
				var go = Place( placement, spawn is null ? root : items, tints?[i], objectModels, created.GetValueOrDefault( placement ) );
				if ( go is not null && spawn is not null )
					TagSpawn( go, placement, spawn );
			}
			catch ( Exception e )
			{
				host.Logger.Warning( e, $"{cache.Name}: couldn't place {placement.Object}: {e.Message}" );
			}
		}

		BuildNetgameFlags( objects );
		BuildSky( host, scenario, models );
	}

	/// <summary>
	/// Every placement's lighting into the <see cref="ObjectLightingTable"/>; returns each one's tint, or
	/// null if the level couldn't be sampled (the objects get the shader's fallback light).
	/// </summary>
	static Color[] Light( Halo2Mount host, CacheFile cache, List<(StructureBspTag Bsp, LightmapTag Lightmap)> bsps, ScenarioObjects objects, List<ObjectPlacement> placements )
	{
		try
		{
			var sampler = new ObjectLightingSampler( bsps, ObjectLightingSampler.TexturePages( host.Textures ) );
			var lighting = GlobalLightingTag.For( cache, objects.GlobalLighting );
			var environments = new PrtEnvironments( bsps );

			// Lighting the BSPs store for particular placements, which MCC uses instead of a sample.
			var stored = bsps.SelectMany( b => b.Bsp.ObjectLighting ).GroupBy( l => l.ObjectId ).ToDictionary( g => g.Key, g => g.First().Light );

			var lights = new List<ObjectLight>( placements.Count );
			var prt = new List<PrtLight>( placements.Count );
			var changeColors = new List<System.Numerics.Vector3?[]>( placements.Count );
			var unlit = 0;
			foreach ( var placement in placements )
			{
				var light = ObjectLight.Default;
				PrtLight prtLight = null;
				System.Numerics.Vector3?[] colors = null;
				if ( ObjectTag.IsObject( placement.Object ) )
				{
					var objectTag = new ObjectTag( placement.Object );
					if ( placement.ObjectId != 0 && stored.TryGetValue( placement.ObjectId, out var storedLight ) )
						light = storedLight;
					else if ( sampler.Sample( placement.Transform( objectTag.BoundingOffset ), objectTag.SearchesCardinalDirections ) is { } sample )
						light = ObjectLight.Evaluate( lighting?.For( objectTag.Type ), sample );
					else
					{
						// MCC keeps a new render state's initial light until a sample succeeds.
						light = ObjectLight.Unsampled;
						unlit++;
					}

					prtLight = PrtLightFor( host, objectTag, placement, environments );

					// As MCC picks them, from where the object is created, for its model variant.
					var variant = objectTag.Model is { Address: not 0 } hlmtTag && hlmtTag.Class == ModelTag.Class
						? new ModelTag( hlmtTag ).FindVariant( placement.Variant ?? objectTag.DefaultVariant )?.Name
						: null;
					colors = objectTag.InitialChangeColors( variant, placement.Position, placement.Data?.ActiveChangeColors ?? 0, placement.Data?.ChangeColors );
				}

				lights.Add( light );
				prt.Add( prtLight );
				changeColors.Add( colors );
			}

			if ( unlit > 0 )
				host.Logger.Info( $"{cache.Name}: {unlit} of {placements.Count} objects have no lightmap below them and keep MCC's unsampled light" );

			var first = ObjectLightingTable.Add( cache.FilePath, lights, prt, changeColors );
			return first == 0 ? null : Enumerable.Range( first, placements.Count ).Select( ObjectLightingTable.Tint ).ToArray();
		}
		catch ( Exception e )
		{
			host.Logger.Warning( e, $"{cache.Name}: couldn't light the objects: {e.Message}" );
			return null;
		}
	}

	/// <summary>
	/// The PRT light of an object whose render model has PRT data, or null.
	/// </summary>
	static PrtLight PrtLightFor( Halo2Mount host, ObjectTag objectTag, ObjectPlacement placement, PrtEnvironments environments )
	{
		try
		{
			if ( objectTag.Model is not { Address: not 0 } hlmtTag || hlmtTag.Class != ModelTag.Class )
				return null;

			var hlmt = new ModelTag( hlmtTag );
			if ( hlmt.RenderModel is not { Address: not 0 } renderModelTag || renderModelTag.Class != RenderModelTag.Class )
				return null;

			var model = new RenderModelTag( renderModelTag );
			return model.HasPrt ? PrtLight.ForPlacement( environments, model, placement ) : null;
		}
		catch ( Exception e )
		{
			host.Logger.Warning( e, $"Couldn't light {placement.Object} by PRT: {e.Message}" );
			return null;
		}
	}

	/// <summary>
	/// Models for objects that can't share their model tag's: scenery the scenario's lightmaps were baked with, lit that
	/// way, and objects whose shaders read <c>random_constant</c> (crate labels, drum stencils, container numbers, Scorpion
	/// markings), with materials of their own for MCC's value from their handle. Models built in code, which a scene
	/// can name (not material overrides: a scene saves those as paths, and nothing loads one built in code).
	/// </summary>
	sealed class ObjectModels( List<(StructureBspTag Bsp, LightmapTag Lightmap)> bsps, List<Model> models )
	{
		readonly Dictionary<ulong, (LightmapTag Lightmap, SceneryObjectLighting Scenery)> _objects = bsps
			.Where( b => b.Lightmap is not null )
			.SelectMany( b => b.Lightmap.SceneryObjects.Select( s => (b.Lightmap, Scenery: s) ) )
			.GroupBy( s => s.Scenery.ObjectId )
			.ToDictionary( g => g.Key, g => g.First() );

		// Materials per model tag and lighting, shared by its objects' models.
		readonly Dictionary<(string, ModelLighting), Dictionary<int, Material>> _materials = new();

		// Shaders whose animation reads random_constant, by tag path.
		readonly Dictionary<string, bool> _readsRandomConstant = new( StringComparer.OrdinalIgnoreCase );

		/// <summary>
		/// The object's own model, or null if it can share its model tag's.
		/// </summary>
		/// <param name="placement">The scenario's placement, whose baked lighting (if any) it gets; null for attached objects.</param>
		/// <param name="handle">The handle MCC gives the object (<see cref="ObjectCreationOrder"/>), if known.</param>
		/// <param name="physics">A crate's physics (<see cref="PhysicsModelBodies"/>), or null.</param>
		public Model ModelFor( TagEntry hlmtTag, ObjectPlacement placement, uint? handle, PhysicsModelPlan physics = null )
		{
			var hlmt = new ModelTag( hlmtTag );
			if ( hlmt.RenderModel is not { Address: not 0 } renderModelTag || renderModelTag.Class != RenderModelTag.Class )
				return null;

			var renderModel = new RenderModelTag( renderModelTag );
			var bakedLighting = Baked( placement, renderModel );
			var labels = handle is null ? [] : RandomConstantIndices( renderModel );
			if ( bakedLighting is null && labels.Count == 0 )
				return null;

			var path = Halo2Mount.ResourcePath( hlmtTag.Path );
			var lighting = bakedLighting is null ? ModelLighting.Object : ModelLighting.Baked;
			var materialBase = $"halo2/{path}_{(bakedLighting is null ? "objects" : "baked")}";
			if ( !_materials.TryGetValue( (hlmtTag.Path, lighting), out var shared ) )
				_materials[(hlmtTag.Path, lighting)] = shared = new();

			// The shared materials, but the object's own for the shaders reading random_constant.
			var materials = new Dictionary<int, Material>( shared );
			foreach ( var index in labels )
			{
				var shader = renderModel.Materials[index];
				materials[index] = ModelMaterials.Create( $"{materialBase}/{Halo2Mount.ResourcePath( shader.Path )}_rc{handle:x8}.vmat", new ShaderTag( shader ), lighting,
					ObjectCreationOrder.RandomConstant( handle.Value ) );
			}

			var name = bakedLighting is null ? $"halo2/{path}{(physics is null ? "" : "_physics")}_rc{handle:x8}.vmdl" : $"halo2/{path}_baked_{placement.ObjectId:x16}.vmdl";
			var model = RenderModelBuilder.Build( name, renderModel, hlmt, Halo2Mount.WorldScale, lighting, baked: bakedLighting, materials: materials, materialBase: materialBase,
				physics: physics );
			foreach ( var (index, material) in materials )
			{
				if ( !labels.Contains( index ) )
					shared.TryAdd( index, material );
			}

			if ( model is not null )
				models.Add( model );

			return model;
		}

		/// <summary>
		/// The placement's baked lighting, or null if it wasn't baked (or not every section it can show was).
		/// </summary>
		RenderModelBuilder.SectionLighting Baked( ObjectPlacement placement, RenderModelTag renderModel )
		{
			if ( placement is null || placement.ObjectId == 0 || !_objects.TryGetValue( placement.ObjectId, out var baked ) )
				return null;

			var (lightmap, scenery) = baked;
			foreach ( var index in renderModel.Regions.SelectMany( r => r.Permutations ).Select( p => p.HighestSection ).Distinct() )
			{
				if ( renderModel.GetSection( index ) is { IsEmpty: false } section && lightmap.SceneryLighting( scenery, index, section.VertexCount ).IncidentDirections.Array is null )
					return null;
			}

			return ( section, vertexCount ) => lightmap.SceneryLighting( scenery, section, vertexCount );
		}

		/// <summary>
		/// The render model's material indices whose shaders read <c>random_constant</c>.
		/// </summary>
		List<int> RandomConstantIndices( RenderModelTag renderModel )
		{
			var indices = new List<int>();
			for ( var i = 0; i < renderModel.Materials.Count; i++ )
			{
				var shader = renderModel.Materials[i];
				if ( shader is null || shader.Class != ShaderTag.Class || shader.Address == 0 )
					continue;

				if ( !_readsRandomConstant.TryGetValue( shader.Path, out var reads ) )
					_readsRandomConstant[shader.Path] = reads = ShaderAnimation.ReadsInput( shader, "random_constant" );
				if ( reads )
					indices.Add( i );
			}

			return indices;
		}
	}

	static GameObject Place( ObjectPlacement placement, GameObject parent, Color? tint, ObjectModels objectModels, ObjectCreationOrder.Created created )
	{
		var physics = placement.Kind == "crate" && ObjectTag.IsObject( placement.Object ) && new ObjectTag( placement.Object ).Model is { Address: not 0 } crateModel
			&& crateModel.Class == ModelTag.Class ? PhysicsModelPlan.For( new ModelTag( crateModel ) ) : null;
		var go = PlaceObject( placement.Object, placement.Frame, placement.Variant, placement.Random, parent, tint,
			hlmt => objectModels.ModelFor( hlmt, placement, created?.Handle, physics ), physics is not null );

		// The effects it carries (Halo2/ObjectEffects.cs); effect scenery is only that.
		go = ObjectEffects.Attach( placement.Object, placement.Frame, parent, go );
		if ( go is null )
			return null;

		go.Tags.Add( placement.Kind.Replace( ' ', '_' ) );
		if ( physics is not null )
			AddCrateCollider( go, placement, physics );
		else if ( SolidKinds.Contains( placement.Kind ) )
			AddCollider( go );

		// Objects the variant attaches (the Warthog's turret), lit like the object they're on.
		var attachedObjects = AttachedObjects.For( placement.Object, placement.Frame, placement.Variant );
		for ( var k = 0; k < attachedObjects.Count; k++ )
		{
			var attached = attachedObjects[k];
			var handle = created is not null && k < created.Attached.Count ? created.Attached[k] : null;
			var child = PlaceObject( attached.Object, attached.Frame, null, salt => 0, go, tint, hlmt => objectModels.ModelFor( hlmt, null, handle ) );
			if ( child is null )
				continue;

			child.Tags.Add( "attached" );
			if ( SolidKinds.Contains( placement.Kind ) )
				AddCollider( child );
		}

		return go;
	}

	/// <summary>
	/// An object's model where <paramref name="frame"/> puts it, showing its variant (the object's default if
	/// null): each region's permutation picked through the model's body groups.
	/// </summary>
	/// <param name="ownModel">The object's own model for its model tag (<see cref="ObjectModels"/>), or null to share the tag's.</param>
	/// <param name="physics">Share the model with Halo's physics (<see cref="PhysicsModelBodies"/>), a crate's.</param>
	static GameObject PlaceObject( TagEntry objectEntry, ObjectFrame frame, string variant, Func<int, float> random, GameObject parent, Color? tint,
		Func<TagEntry, Model> ownModel = null, bool physics = false )
	{
		if ( !ObjectTag.IsObject( objectEntry ) )
			return null;

		var objectTag = new ObjectTag( objectEntry );
		if ( objectTag.Model is not { Address: not 0 } hlmtTag || hlmtTag.Class != ModelTag.Class )
			return null;

		var model = ownModel?.Invoke( hlmtTag ) ?? Model.Load( physics ? Halo2Mount.PhysicsModelPath( hlmtTag.Path ) : Halo2Mount.ModelPath( hlmtTag.Path ) );
		if ( model is null || model.IsError )
			return null;

		var go = new GameObject( parent, true, System.IO.Path.GetFileName( objectEntry.Path ) );
		go.WorldPosition = (Vector3)frame.Position * Halo2Mount.WorldScale;
		go.WorldRotation = Rotation.LookAt( (Vector3)frame.Forward, (Vector3)frame.Up );
		if ( frame.Scale != 1 )
			go.WorldScale = frame.Scale;

		var renderer = Halo2ObjectAnimations.AddRenderer( go, model, objectEntry, objectTag, new ModelTag( hlmtTag ) );
		if ( tint is { } t )
			renderer.Tint = t;

		var hlmt = new ModelTag( hlmtTag );
		if ( hlmt.RenderModel is { Address: not 0 } renderModelTag && renderModelTag.Class == RenderModelTag.Class )
		{
			// By choice name, every region: the model may have been built from another map's copy of the tag, whose
			// variants (and so body groups and defaults) can differ from this one's.
			var renderModel = new RenderModelTag( renderModelTag );
			var choice = ModelPermutations.Choose( renderModel, hlmt.FindVariant( variant ?? objectTag.DefaultVariant ), random );
			foreach ( var region in renderModel.Regions )
				RenderModelBuilder.SetChoice( renderer, region, choice[region.Index] );
		}

		return go;
	}

	/// <summary>
	/// Static collision for an object whose model has physics (a collision model, see <see cref="RenderModelBuilder"/>).
	/// </summary>
	static void AddCollider( GameObject go )
	{
		var model = go.GetComponent<ModelRenderer>()?.Model;
		if ( model?.Physics is not { Parts.Count: > 0 } )
			return;

		var collider = go.AddComponent<ModelCollider>();
		collider.Model = model;
		collider.Static = true;
	}

	// A crate's collision is its model's Halo physics, which stays still: the game simulates crates itself and moves the object.
	// A crate with a body that moves is keyframed, one whose bodies are all fixed (a gravity lift) static.
	// It's tagged halo_crate and halo_object_<n>, n its index in the scenario's crates (maps/<map>.json's Crates).
	static void AddCrateCollider( GameObject go, ObjectPlacement placement, PhysicsModelPlan physics )
	{
		go.Tags.Add( "halo_crate" );
		go.Tags.Add( $"halo_object_{placement.Data.Index}" );

		var model = go.GetComponent<ModelRenderer>()?.Model;
		if ( model?.Physics is not { Parts.Count: > 0 } )
			return;

		var collider = go.AddComponent<ModelCollider>();
		collider.Model = model;
		collider.Static = physics.Bodies.All( b => b.Motion == PhysicsModelMotion.Fixed );
	}

	/// <summary>
	/// The multiplayer game types, by Halo 2's ids, with the names their tags use.
	/// </summary>
	static readonly (int Id, string Name)[] GameTypes =
	[
		(1, "ctf"), (2, "slayer"), (3, "oddball"), (4, "king"), (5, "race"), (6, "headhunter"), (7, "juggernaut"),
		(8, "territories"), (15, "medic"), (16, "vip"), (17, "infection"),
	];

	/// <summary>
	/// Multiplayer items and vehicles. Picking them up and respawning them is up to the game: each is tagged
	/// "netgame_item" (or "netgame_vehicle"), "game_&lt;type&gt;" for every game type it spawns in,
	/// "respawn_&lt;seconds&gt;" when the spawn or its collection sets a time, and "at_rest" if Halo leaves it
	/// floating where it's placed.
	/// </summary>
	static void TagSpawn( GameObject go, ObjectPlacement placement, NetgameEquipment spawn )
	{
		go.Tags.Add( placement.Kind == "vehicle" ? "netgame_vehicle" : "netgame_item" );

		foreach ( var type in spawn.GameTypes )
		{
			foreach ( var (id, name) in GameTypes )
			{
				// 12 all, 13 all but CTF, 14 all but CTF and race.
				var included = type == id
					|| (type == 12 && id <= 8)
					|| (type == 13 && id is >= 2 and <= 8)
					|| (type == 14 && id is >= 2 and <= 8 and not 5);
				if ( included )
					go.Tags.Add( $"game_{name}" );
			}
		}

		var respawn = spawn.SpawnTime != 0 ? spawn.SpawnTime : new ItemCollectionTag( spawn.Collection ).SpawnTime;
		if ( respawn > 0 )
			go.Tags.Add( $"respawn_{respawn}" );

		if ( spawn.Levitates )
			go.Tags.Add( "at_rest" );
	}

	static string NetgameFlagTag( int type ) => type switch
	{
		0 => "ctf_flag_spawn",
		1 => "ctf_flag_return",
		2 => "assault_bomb_spawn",
		3 => "assault_bomb_return",
		4 => "oddball_spawn",
		6 => "race_checkpoint",
		7 => "teleporter_source",
		8 => "teleporter_destination",
		9 => "headhunter_bin",
		10 => "territories_flag",
		>= 11 and <= 18 => $"king_hill_{type - 11}",
		_ => "netgame_other",
	};

	/// <summary>
	/// Multiplayer markers. Making teleporters teleport, flags capturable and so on is up to the game: each
	/// marker is tagged "netgame_flag", its kind, "team_&lt;n&gt;" (8 = neutral) and "id_&lt;identifier&gt;".
	/// </summary>
	static void BuildNetgameFlags( ScenarioObjects objects )
	{
		if ( objects.NetgameFlags.Count == 0 )
			return;

		var root = new GameObject( true, "netgame flags" );
		foreach ( var flag in objects.NetgameFlags )
		{
			var kind = NetgameFlagTag( flag.Type );
			var go = new GameObject( root, true, $"{kind} {flag.Identifier}" );
			go.WorldPosition = (Vector3)flag.Position * Halo2Mount.WorldScale;
			go.WorldRotation = Rotation.FromYaw( flag.Facing.RadianToDegree() );
			go.Tags.Add( "netgame_flag" );
			go.Tags.Add( kind );
			go.Tags.Add( $"team_{flag.Team}" );
			go.Tags.Add( $"id_{flag.Identifier}" );
		}
	}

	/// <summary>
	/// What Halo clears the frame to before the sky (its clear colour, or black for a sky without one) as the sky's
	/// first layer: a box around the world in that colour, which halo2_transparent.shader draws at the far plane like
	/// the sky's own layers, so the layers go over it and it shows wherever they don't reach.
	/// </summary>
	/// <remarks>
	/// It was a SkyBox2D with a material made here, which a scene opened from the mount's saved copy couldn't find
	/// again by name: s&amp;box's default sky showed instead. A model's own materials go with it.
	/// </remarks>
	static Model BuildClearColor( TagEntry skyTag, System.Numerics.Vector3 color )
	{
		var name = $"halo2/{Halo2Mount.ResourcePath( skyTag.Path )}_clear";
		var material = Material.Create( $"{name}.vmat", TransparentMaterial.ShaderPath );
		if ( material is null )
			return null;

		// The "no fog" program with the colour as its base: opaque, premultiplied.
		material.SetFeature( "F_BLEND_MODE", 0 );
		material.SetFeature( "F_TWO_SIDED", 1 );
		material.Set( "g_flProgram", (float)TransparentMaterial.Program.NoFog );
		material.Set( "g_tMap2", Halo1.MaterialTextures.Transparent );
		material.Set( "g_tMap3", Halo1.MaterialTextures.White );
		material.Set( "g_vC1", Vector4.Zero );
		material.Set( "g_vC2", new Vector4( color.X, color.Y, color.Z, 1 ) );
		material.Set( "g_flHaloSky", 1f );

		// Any size does: the shader puts it at the far plane.
		var r = SkyClearSize * Halo2Mount.WorldScale;
		var corners = new List<LevelVertex>();
		for ( var i = 0; i < 8; i++ )
		{
			var position = new Vector3( (i & 1) != 0 ? r : -r, (i & 2) != 0 ? r : -r, (i & 4) != 0 ? r : -r );
			corners.Add( new LevelVertex { Position = position, Normal = -position.Normal, Tangent = new Vector4( 1, 0, 0, 1 ) } );
		}

		int[] indices =
		[
			0, 2, 3, 0, 3, 1, 4, 5, 7, 4, 7, 6, // -z, +z
			0, 1, 5, 0, 5, 4, 2, 6, 7, 2, 7, 3, // -y, +y
			0, 4, 6, 0, 6, 2, 1, 3, 7, 1, 7, 5, // -x, +x
		];

		var mesh = new Mesh( material );
		mesh.CreateVertexBuffer( corners.Count, corners );
		mesh.CreateIndexBuffer( indices.Length, indices );
		mesh.Bounds = BBox.FromPositionAndSize( Vector3.Zero, 2 * r );
		return Model.Builder.WithName( $"{name}.vmdl" ).AddMesh( mesh ).Create();
	}

	/// <summary>
	/// Half the clear colour box's size (world units): past any sky, for views of it without <see cref="SceneUpkeep"/>.
	/// </summary>
	const float SkyClearSize = 4096;

	/// <summary>
	/// The sky's render model, every region, at the world origin, scaled by the sky's model scale: one object per
	/// layer (each region's parts by material), in the order Halo draws them, which <see cref="SceneUpkeep"/> keeps.
	/// </summary>
	/// <remarks>
	/// <para>Skies "fixed in world space" (Lockout, Zanzibar, Needle, Turf...) are drawn exactly like this by Halo:
	/// their models are the scenery around the level in world units, at scale 1. The others Halo keeps
	/// centred on the camera (no parallax); at their scales (Coagulation 120, Ascension 50...) a model at the
	/// origin is far enough away to look nearly the same, but it can be beyond the camera's far plane; the
	/// Halo CE mount's way (render the sky into a skybox, Halo1/SkyMaterialLoader.cs) is the better fit for
	/// those.</para>
	///
	/// <para>The layers overlap: Dune's planet, island and ring (region "20") are inside its cloud dome (region "10"),
	/// and with the whole sky one object s&amp;box drew the dome last, over them. Region names are draw-order numbers
	/// ("00.moon", "01.sky", "10.lattice"...) and regions are stored in name order.</para>
	/// </remarks>
	static void BuildSky( Halo2Mount host, ScenarioTag scenario, List<Model> models )
	{
		var skyTag = scenario.Skies.FirstOrDefault( s => s.Class == SkyTag.Class && s.Address != 0 );
		if ( skyTag is null )
			return;

		try
		{
			var sky = new SkyTag( skyTag );
			if ( sky.RenderModel is not { Address: not 0 } renderModel || renderModel.Class != RenderModelTag.Class )
				return;

			// 0 means the default, 0.03 by the tag's own description.
			var scale = sky.RenderModelScale > 0 ? sky.RenderModelScale : 0.03f;
			var mode = new RenderModelTag( renderModel );
			var materials = new Dictionary<int, Material>();
			var root = new GameObject( true, "sky" );
			var count = 0;

			// Halo clears to its clear colour (or black) first.
			if ( BuildClearColor( skyTag, sky.UseClearColor ? sky.ClearColor : default ) is { } clear )
			{
				models.Add( clear );
				var go = new GameObject( root, true, "sky clear colour" );
				go.Tags.Add( SceneUpkeep.SkyLayerTag );
				var renderer = go.AddComponent<ModelRenderer>();
				renderer.Model = clear;
				renderer.RenderType = ModelRenderer.ShadowRenderType.Off;
			}

			foreach ( var layer in RenderModelBuilder.Layers( mode ) )
			{
				var model = RenderModelBuilder.Build( $"halo2/{Halo2Mount.ResourcePath( skyTag.Path )}_sky_{count}.vmdl", mode, null,
					Halo2Mount.WorldScale * scale, ModelLighting.Unlit, collision: false, materials: materials,
					materialBase: $"halo2/{Halo2Mount.ResourcePath( skyTag.Path )}_sky", layer: layer );
				if ( model is null )
					continue;

				models.Add( model );
				var go = new GameObject( root, true, $"sky layer {count++} ({mode.Regions[layer.Region].Name})" );
				go.Tags.Add( SceneUpkeep.SkyLayerTag );
				var renderer = go.AddComponent<ModelRenderer>();
				renderer.Model = model;
				renderer.RenderType = ModelRenderer.ShadowRenderType.Off;
			}

		}
		catch ( Exception e )
		{
			host.Logger.Warning( e, $"Couldn't build the sky {skyTag}: {e.Message}" );
		}
	}
}
