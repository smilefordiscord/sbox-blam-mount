using Blam.Halo1;
using Blam.Halo1.Tags;
using Sandbox.Audio;
using Sandbox.Volumes;

namespace Sandbox.Mounting.Blam.Halo1;

/// <summary>
/// A level's ambient sound: the looping sounds attached to its sound scenery, played where the
/// objects stand, and per BSP cluster the background sound and the sound environment (reverb)
/// heard inside it.
/// </summary>
/// <remarks>
/// Everything is engine components and mounted resources (see <see cref="HaloSounds"/>), since
/// the scene is saved and our own component types wouldn't survive that:
/// <list type="bullet">
/// <item>A looping sound's track is one sound file (start sound, then its loop's variations in a
/// random sequence), played by a sound point. Its detail sounds are sound points repeating at
/// Halo's random periods.</item>
/// <item>A cluster's background sound is a soundscape trigger over the cluster's bounds. Only one
/// soundscape plays at a time (the nearest trigger around the listener), like Halo only plays
/// the background sound of the cluster the camera is in.</item>
/// <item>A cluster's sound environment is a DSP volume with the closest of s&amp;box's built-in DSP
/// presets (there's no API for custom reverb settings).</item>
/// </list>
/// Clusters are approximated by the boxes around their subclusters, which can overlap; smaller
/// boxes win.
/// </remarks>
static class AmbientSoundBuilder
{
	/// <summary>
	/// Adds the level's sounds a slice at a time, yielding how far it's got (0 to 1) when the slice is due.
	/// </summary>
	public static IEnumerable<float> Build( HaloCeMount host, CacheFile cache, ScenarioTag scenario, BuildSlice slice )
	{
		var sounds = host.Sounds;
		if ( sounds is null )
			yield break;

		var scenery = SoundPlacements.ReadSoundScenery( cache, cache.Scenario );
		var clusters = scenario.StructureBsps.Select( b => ReadClusters( cache, b ) ).ToList();

		// Decode everything the level plays in parallel up front; loading the sound events one by
		// one would decode them one after another.
		foreach ( var tag in scenery.SelectMany( s => s.LoopingSounds ).Distinct() )
			sounds.GetLoopingSound( tag?.Path )?.Prefetch();
		foreach ( var tag in clusters.SelectMany( b => b ).Select( c => c.BackgroundSound ).Distinct() )
			sounds.GetLoopingSound( tag?.Path )?.Prefetch( background: true );

		var root = new GameObject( true, "sounds" );
		var count = scenery.Count + clusters.Sum( b => b.Count );
		var done = 0;

		foreach ( var placement in scenery )
		{
			if ( slice.Due )
				yield return (float)done / count;
			done++;
			var position = (Vector3)placement.Position * HaloCeMount.WorldScale;
			foreach ( var tag in placement.LoopingSounds )
			{
				var resources = sounds.GetLoopingSound( tag.Path );
				if ( resources is null )
					continue;

				var go = new GameObject( root, true, System.IO.Path.GetFileName( tag.Path ) );
				go.WorldPosition = position;
				go.Tags.Add( "sound_scenery" );
				AddLoopingSound( go, resources );
			}
		}

		var environments = new Dictionary<TagEntry, SoundEnvironmentTag>();
		var priorities = EnvironmentPriorities( cache, clusters, environments );
		for ( var b = 0; b < clusters.Count; b++ )
		{
			foreach ( var cluster in clusters[b] )
			{
				if ( slice.Due )
					yield return (float)done / count;
				done++;
				var min = (Vector3)cluster.BoundsMin * HaloCeMount.WorldScale;
				var max = (Vector3)cluster.BoundsMax * HaloCeMount.WorldScale;
				var background = sounds.GetLoopingSound( cluster.BackgroundSound?.Path );
				var environment = cluster.Environment is null ? null : environments[cluster.Environment];
				if ( background?.Soundscape is null && environment is null )
					continue;

				var go = new GameObject( root, true, $"bsp {b} cluster {cluster.Index}" );
				go.WorldPosition = (min + max) * 0.5f;
				go.Tags.Add( "sound_cluster" );

				if ( background?.Soundscape is not null )
				{
					var trigger = go.AddComponent<SoundscapeTrigger>();
					trigger.Type = SoundscapeTrigger.TriggerType.Box;
					// Half extents, despite the name.
					trigger.BoxSize = (max - min) * 0.5f;
					trigger.Soundscape = GameResource.Load<Soundscape>( background.Soundscape.Path );
				}

				if ( environment is not null && DspPresetFor( environment ) is { } preset )
				{
					var dsp = go.AddComponent<DspVolume>();
					dsp.Dsp = preset;
					dsp.Priority = priorities[cluster];
					dsp.SceneVolume = new SceneVolume { Type = SceneVolume.VolumeTypes.Box, Box = BBox.FromPositionAndSize( Vector3.Zero, max - min ) };
				}
			}
		}
	}

	/// <summary>
	/// A BSP cluster's background sound and sound environment, with its bounds in world units.
	/// </summary>
	sealed record Cluster( int Index, TagEntry BackgroundSound, TagEntry Environment, System.Numerics.Vector3 BoundsMin, System.Numerics.Vector3 BoundsMax );

	static List<Cluster> ReadClusters( CacheFile cache, StructureBspReference bsp )
	{
		var backgrounds = SoundPlacements.ReadClusterBackgroundSounds( cache, bsp ).ToDictionary( c => c.Cluster );
		var environments = SoundPlacements.ReadClusterSoundEnvironments( cache, bsp ).ToDictionary( c => c.Cluster );

		var result = new List<Cluster>();
		foreach ( var index in backgrounds.Keys.Union( environments.Keys ).Order() )
		{
			backgrounds.TryGetValue( index, out var background );
			environments.TryGetValue( index, out var environment );
			result.Add( new Cluster( index, background?.LoopingSound, environment?.SoundEnvironment,
				background?.BoundsMin ?? environment.BoundsMin, background?.BoundsMax ?? environment.BoundsMax ) );
		}

		return result;
	}

	/// <summary>
	/// DSP volume priorities: the sound environment's priority first, as in Halo, then smaller
	/// clusters over bigger ones, so the cluster you're in wins where cluster boxes overlap.
	/// </summary>
	static Dictionary<Cluster, int> EnvironmentPriorities( CacheFile cache, List<List<Cluster>> clusters, Dictionary<TagEntry, SoundEnvironmentTag> environments )
	{
		var withEnvironment = clusters.SelectMany( b => b ).Where( c => c.Environment is not null ).ToList();
		foreach ( var cluster in withEnvironment )
		{
			if ( !environments.ContainsKey( cluster.Environment ) )
				environments[cluster.Environment] = new SoundEnvironmentTag( cache, cluster.Environment );
		}

		static float Volume( Cluster c )
		{
			var size = c.BoundsMax - c.BoundsMin;
			return size.X * size.Y * size.Z;
		}

		var result = new Dictionary<Cluster, int>();
		var rank = 0;
		foreach ( var cluster in withEnvironment.OrderByDescending( Volume ) )
			result[cluster] = environments[cluster.Environment].Priority * 100000 + rank++;

		return result;
	}

	/// <summary>
	/// s&amp;box only has its built-in DSP presets (Half-Life's). Halo's sound environments are EAX
	/// presets under their own names, so those are matched by name; anything else by its decay time.
	/// Outdoor environments get none (null): s&amp;box's "outside" presets put a loud late echo on
	/// every sound, where MCC's outdoor maps sound essentially dry.
	/// </summary>
	static string DspPresetFor( SoundEnvironmentTag environment )
	{
		var name = System.IO.Path.GetFileName( environment.Tag.Path ).ToLowerInvariant();
		switch ( name )
		{
			case "alley": return "concrete.small";
			case "arena": return "room.diffuse.huge";
			case "bathroom": return "room.empty.small";
			case "cave": return "cavern.medium";
			case "concert_hall": return "chamber.large";
			case "forest" or "mountains" or "quarry": return null;
			case "hallway": return "tunnel.small";
			case "hangar": return "metallic.large";
			case "stone_room": return "concrete.medium";
			case "underwater": return "water.small";
		}

		// Outdoors (late, faint reflections): dry, as above.
		if ( environment.ReflectionsDelay > 0.1f )
			return null;

		return environment.DecayTime switch
		{
			< 1.2f => "room.empty.small",
			< 2f => "concrete.small",
			< 3f => "concrete.medium",
			< 5f => "chamber.large",
			< 8f => "room.diffuse.huge",
			_ => "metallic.large",
		};
	}

	/// <summary>
	/// A sound point per track of a looping sound, and one per detail sound repeating at Halo's
	/// random periods.
	/// </summary>
	/// <remarks>
	/// Each fades with distance the way its sound event does: Halo fades a looping sound's tracks
	/// and details by their own sounds' minimum and maximum distances. The looping sound's own
	/// maximum distance only decides whether it plays at all.
	/// </remarks>
	public static void AddLoopingSound( GameObject go, LoopingSoundResources resources )
	{
		foreach ( var track in resources.Tracks )
		{
			var soundEvent = GameResource.Load<SoundEvent>( track.Event.Path );
			if ( soundEvent is null )
				continue;

			// The gain and random pitch are in the event; overriding the volume would also fix the pitch.
			var component = go.AddComponent<SoundPointComponent>();
			component.SoundEvent = soundEvent;
			component.PlayOnStart = true;
		}

		foreach ( var detail in resources.Details )
		{
			var soundEvent = GameResource.Load<SoundEvent>( detail.Event.Path );
			if ( soundEvent is null )
				continue;

			// Halo plays a detail at a random offset from the looping sound each time. MCC's maps
			// all have no offset (distance bounds 0); otherwise pick one offset per placement.
			var target = go;
			var offset = DetailOffset( detail.Detail );
			if ( offset != Vector3.Zero )
			{
				target = new GameObject( go, true, $"detail {detail.Index}" );
				target.LocalPosition = offset;
			}

			var component = target.AddComponent<SoundPointComponent>();
			component.SoundEvent = soundEvent;
			component.PlayOnStart = false;
			component.Repeat = true;
			component.MinRepeatTime = MathF.Max( 0.1f, detail.MinPeriod );
			component.MaxRepeatTime = MathF.Max( component.MinRepeatTime, detail.MaxPeriod );
		}
	}

	static Vector3 DetailOffset( LoopingSoundDetail detail )
	{
		if ( detail.DistanceMax <= 0 )
			return Vector3.Zero;

		var distance = Random.Shared.Float( detail.DistanceMin, MathF.Max( detail.DistanceMin, detail.DistanceMax ) );
		var yaw = Random.Shared.Float( detail.YawMin, detail.YawMax );
		var pitch = Random.Shared.Float( detail.PitchMin, detail.PitchMax );
		var direction = new Vector3( MathF.Cos( pitch ) * MathF.Cos( yaw ), MathF.Cos( pitch ) * MathF.Sin( yaw ), MathF.Sin( pitch ) );
		return direction * distance * HaloCeMount.WorldScale;
	}
}
