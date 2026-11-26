using Blam.Halo3;
using Blam.Halo3.Tags;

namespace Sandbox.Mounting.Blam.Halo3;

// Registers Halo 3's effects as prefabs, as Halo 2's: mount://halo3/<effect tag>.prefab plays the effect once and removes itself (its loose particles finish on their own); <effect tag>__loop.prefab repeats it for ever (as objects carry them).
// Built from engine components and tags only (Halo3ParticleEffects marks the root, Halo3ParticleWorld plays it). The root's +X is the effect's forward; each location (marker) the effect names is a child of that name ("" is the root) for a game to move onto its model's marker.
static class Halo3Effects
{
	public static string PrefabName( string tagPath, bool looping ) => $"{Halo3Mount.ResourcePath( tagPath )}{(looping ? "__loop" : "")}.prefab";

	public static string PrefabPath( string tagPath, bool looping ) => $"mount://halo3/{PrefabName( tagPath, looping )}";

	// Every effect of the map, unless an earlier map registered it.
	public static void Register( MountContext context, CacheFile cache, HashSet<string> registered )
	{
		foreach ( var tag in cache.TagsOf( "effe" ) )
		{
			if ( string.IsNullOrEmpty( tag.Path ) || tag.Address == 0 )
				continue;

			foreach ( var looping in new[] { false, true } )
			{
				var name = PrefabName( tag.Path, looping );
				if ( registered.Add( name ) )
					context.Add( ResourceType.PrefabFile, name, new Halo3EffectPrefabLoader( cache.FilePath, tag.Id, looping ) );
			}

			Halo3ParticleLibrary.Register( cache, tag );
		}

		// Light volume systems objects carry by themselves play through the same runtime (Halo3ParticleLibrary.CompileLightVolumeEffect).
		foreach ( var tag in cache.TagsOf( "ltvl" ) )
		{
			if ( !string.IsNullOrEmpty( tag.Path ) && tag.Address != 0 )
				Halo3ParticleLibrary.Register( cache, tag );
		}

		// Saved scenes and prefabs with effect roots play without anything marking them first.
		Halo3ParticleWorld.Watch();
	}

	public static void Shutdown()
	{
		Halo3ParticleWorld.StopWatching();
		Halo3ParticleLibrary.Clear();
		LevelEffects.Clear();
	}

	// Locations that face the world's up or down rather than turn with what carries them.
	public const string WorldUpTag = "halo3_world_up";
	public const string WorldDownTag = "halo3_world_down";

	// Builds an effect under root: a child per named location, the root marked for Halo3ParticleWorld. Returns how long one run lasts with its particles' lives (seconds).
	public static float Build( EffectTag effect, GameObject root, bool looping, float a = 1, float b = 1, string mapName = null )
	{
		var made = new HashSet<string>();
		foreach ( var name in effect.Locations )
		{
			if ( string.IsNullOrEmpty( name ) || !made.Add( name ) || root.Children.Any( c => c.Name == name ) )
				continue;

			var go = new GameObject( root, true, name );
			go.Tags.Add( "halo_effect_location" );

			// Special locations of effects made where something hits (the root's +X the surface normal): "gravity" straight down, "up" up, "forward" into the surface.
			// "up" has the root's forward as its y and left as its z (matching MCC's frames for Valhalla's man cannons).
			// Halo3ParticleWorld turns "gravity" and "up" to the world's down and up as it plays (tagged here; a model marker of the name takes the tag off).
			if ( name == "gravity" )
			{
				go.LocalRotation = Rotation.LookAt( Vector3.Down, Vector3.Forward );
				go.Tags.Add( WorldDownTag );
			}
			else if ( name == "up" )
			{
				go.LocalRotation = Rotation.LookAt( Vector3.Up, Vector3.Left );
				go.Tags.Add( WorldUpTag );
			}
			else if ( name == "forward" )
				go.LocalRotation = Rotation.LookAt( Vector3.Backward, Vector3.Up );
		}

		Halo3ParticleEffects.Mark( root, effect.Tag.Path, looping, a, b, mapName );
		var definition = Halo3ParticleLibrary.Effect( effect.Tag );
		return (definition?.LongestRun ?? 0) + (definition?.LongestLife ?? 0);
	}
}

// Builds a prefab on the main thread (as the other mounts' prefab loaders): the root built disabled, saved enabled.
sealed class Halo3EffectPrefabLoader( string mapPath, uint effectTagId, bool looping ) : Halo3Loader
{
	PrefabFile _prefab;

	protected override Task<object> LoadAsync()
	{
		if ( ThreadSafe.IsMainThread )
			return Task.FromResult( Load() );

		return LoadOnMainThread();
	}

	async Task<object> LoadOnMainThread()
	{
		await GameTask.MainThread();
		return Load();
	}

	protected override object Load()
	{
		if ( _prefab is not null )
			return _prefab;

		var cache = Host.GetCache( mapPath );
		var tag = cache.GetTag( effectTagId );
		if ( !EffectTag.IsEffect( tag ) )
			return null;

		var builder = new PrefabBuilder().WithName( Path );
		using ( builder.Scope() )
		{
			var root = new GameObject( false, Name );
			var length = Halo3Effects.Build( new EffectTag( tag ), root, looping );

			// A one-shot effect's root goes once it's played out (its loose particles stay till they die).
			if ( !looping )
				root.Components.Get<TemporaryEffect>( FindMode.EverythingInSelf ).DestroyAfterSeconds = length + 0.1f;
		}

		_prefab = builder.Create();
		if ( _prefab?.RootObject is { } json )
			json["Enabled"] = true;

		return _prefab;
	}

	protected override void Shutdown()
	{
		PrefabBuilder.Destroy( _prefab );
		_prefab = null;
	}
}

// A level's effects at rest: effect scenery placements and the effects placed objects carry (object attachments with no scale function, which nothing would drive here), looping at each instance of their marker in the model's default pose.
static class LevelEffects
{
	public static void Build( Halo3Mount mount, CacheFile cache )
	{
		ScenarioPlan plan;
		try
		{
			var scenario = new ScenarioTag( cache.Scenario );
			plan = new ScenarioPlan( scenario, ScenarioObjectsBuilder.ShownBspMask( scenario ) );
		}
		catch ( Exception e )
		{
			mount.Logger.Warning( e, $"{cache.Name}: couldn't read the scenario for effects: {e.Message}" );
			return;
		}

		var root = new GameObject( true, "effects" );
		var count = 0;
		var placements = plan.Objects.Where( o => !o.Marker ).Select( o => o.Placement )
			.Concat( plan.Scenario.PlacementsOf( PlacementKind.EffectScenery ).Where( p => p.Object is not null && !p.NotAutomatically && !p.NeverPlaced && plan.Shown( p ) ) );

		foreach ( var placement in placements )
		{
			try
			{
				count += Place( placement, root );
			}
			catch ( Exception e )
			{
				mount.Logger.Warning( e, $"{cache.Name}: couldn't place the effects of {placement}: {e.Message}" );
			}
		}

		if ( count == 0 )
			root.Destroy();
		mount.Logger.Info( $"{cache.Name}: {count} level effects" );
	}

	static readonly Dictionary<TagEntry, RenderModelTag> Models = new();

	// Forgets the render models read for markers (mount shutdown).
	public static void Clear()
	{
		lock ( Models )
			Models.Clear();
	}

	// Level objects' functions run by themselves (Shrine's beacons, Sandbox's tower), so they keep their function-scaled attachments; items (weapons, equipment, vehicles) have theirs off at rest.
	static readonly HashSet<PlacementKind> LevelKinds = [PlacementKind.Scenery, PlacementKind.Machine, PlacementKind.Control, PlacementKind.Crate, PlacementKind.EffectScenery];

	static int Place( ScenarioPlacement placement, GameObject root )
	{
		var levelObject = LevelKinds.Contains( placement.Kind );
		var attachments = ObjectAttachment.Read( placement.Object )
			.Where( a => (EffectTag.IsEffect( a.Type ) || LightVolumeTag.IsLightVolume( a.Type )) && (levelObject || string.IsNullOrEmpty( a.PrimaryScale )) ).ToList();
		if ( attachments.Count == 0 )
			return 0;

		var go = new GameObject( root, true, ScenarioObjectsBuilder.Name( placement ) );
		go.WorldPosition = (Vector3)placement.Position * Halo3Mount.WorldScale;
		go.WorldRotation = Rotation.LookAt( (Vector3)placement.Forward, (Vector3)placement.Up );
		if ( placement.Scale != 1 )
			go.WorldScale = placement.Scale;
		go.Tags.Add( placement.Kind == PlacementKind.EffectScenery ? "halo3_effect_scenery" : "halo3_object_effects" );

		var model = RenderModel( placement.Object );
		var count = 0;
		foreach ( var attachment in attachments )
		{
			foreach ( var (rotation, translation) in Markers( model, attachment.Marker ) )
			{
				var site = new GameObject( go, true, System.IO.Path.GetFileName( attachment.Type.Path ) );
				site.LocalTransform = MarkerTransform( rotation, translation );
				if ( EffectTag.IsEffect( attachment.Type ) )
				{
					Halo3Effects.Build( new EffectTag( attachment.Type ), site, looping: true, mapName: attachment.Type.Cache.Name );

					// The effect's locations are markers of the object's model, wherever the attachment itself sits (Valhalla's waterfalls: attached at the origin, their location "marker" points up).
					foreach ( var location in site.Children.Where( c => c.Tags.Has( "halo_effect_location" ) ) )
					{
						if ( ModelMarkers( model, location.Name ) is [var marker, ..] )
						{
							location.WorldTransform = go.WorldTransform.ToWorld( MarkerTransform( marker.Rotation, marker.Translation ) );
							location.Tags.Remove( Halo3Effects.WorldUpTag );
							location.Tags.Remove( Halo3Effects.WorldDownTag );
						}
					}
				}
				else
					Halo3ParticleEffects.Mark( site, attachment.Type.Path, looping: true, mapName: attachment.Type.Cache.Name );
				count++;
			}
		}

		return count;
	}

	static RenderModelTag RenderModel( TagEntry objectTag )
	{
		lock ( Models )
		{
			if ( Models.TryGetValue( objectTag, out var cached ) )
				return cached;
		}

		RenderModelTag model = null;
		var obj = new ObjectTag( objectTag );
		if ( obj.Model is { Address: not 0 } hlmt && new ObjectModel( hlmt ).RenderModel is { Address: not 0 } mode && mode.Class.ToString() == "mode" )
			model = new RenderModelTag( mode );

		lock ( Models )
			Models[objectTag] = model;
		return model;
	}

	// A marker in the object's space (s&box units).
	static Transform MarkerTransform( System.Numerics.Quaternion rotation, System.Numerics.Vector3 translation ) =>
		new( (Vector3)translation * Halo3Mount.WorldScale, Rotation.LookAt( (Vector3)System.Numerics.Vector3.Transform( System.Numerics.Vector3.UnitX, rotation ), (Vector3)System.Numerics.Vector3.Transform( System.Numerics.Vector3.UnitZ, rotation ) ) );

	// Every instance of a marker in the model's default pose (model space, world units); the object's origin when the model has none of that name.
	static List<(System.Numerics.Quaternion Rotation, System.Numerics.Vector3 Translation)> Markers( RenderModelTag model, string name )
	{
		var found = ModelMarkers( model, name );
		if ( found.Count == 0 )
			found.Add( (System.Numerics.Quaternion.Identity, System.Numerics.Vector3.Zero) );
		return found;
	}

	// Every instance of a marker in the model's default pose (model space, world units), none when the model has none of that name.
	static List<(System.Numerics.Quaternion Rotation, System.Numerics.Vector3 Translation)> ModelMarkers( RenderModelTag model, string name )
	{
		var found = new List<(System.Numerics.Quaternion, System.Numerics.Vector3)>();
		if ( model is not null && !string.IsNullOrEmpty( name ) )
		{
			var pose = model.DefaultPose();
			var seen = new HashSet<(int, System.Numerics.Vector3)>();
			foreach ( var marker in model.Markers )
			{
				if ( !string.Equals( marker.Name, name, StringComparison.OrdinalIgnoreCase ) || !seen.Add( (marker.Node, marker.Translation) ) )
					continue;

				var rotation = System.Numerics.Quaternion.Normalize( marker.Rotation );
				if ( marker.Node >= 0 && marker.Node < pose.Length )
				{
					var (translation, nodeRotation) = pose[marker.Node];
					found.Add( (System.Numerics.Quaternion.Normalize( nodeRotation * rotation ), translation + System.Numerics.Vector3.Transform( marker.Translation, nodeRotation )) );
				}
				else
				{
					found.Add( (rotation, marker.Translation) );
				}
			}
		}

		return found;
	}
}
