using Blam.Halo2;
using Blam.Halo2.Tags;
using Sandbox.Audio;
using Sandbox.Volumes;
using LoopingSoundResources = Sandbox.Mounting.Blam.Halo1.LoopingSoundResources;
using SoundEventLoader = Sandbox.Mounting.Blam.Halo1.SoundEventLoader;

namespace Sandbox.Mounting.Blam.Halo2;

/// <summary>
/// A Halo 2 level's ambient sound, built like the Halo CE mount's (Halo1/AmbientSoundBuilder.cs) out of engine
/// components and the looping sound resources <see cref="Halo2Sounds"/> registers, so the scene survives being saved:
/// <list type="bullet">
/// <item>Sound scenery (waves, wind, gulls, distant battle): a sound point per track of each looping sound the
/// <c>ssce</c> attaches, and a repeating one per detail sound, at the placement; the placement's override distances
/// (above 0) replace every one of those sounds' own minimum/maximum distance, as Halo 2 does.</item>
/// <item>Placed objects' looping sounds (Zanzibar's wheel blades and flywheel pegs): the same at the attachment's marker
/// in the model's default pose, at the sounds' own distances. Sounds an object function scales (vehicle engines, turret
/// motors) depend on the game and are left out.</item>
/// <item>Per structure BSP cluster: the background sound as a soundscape trigger over the cluster's box (its details
/// play at the listener, as in Halo), and the sound environment as a DSP volume with the closest built-in preset.</item>
/// </list>
/// Details play at a fixed offset from their looping sound (Halo picks a new one each time; with distance bounds 0, most
/// of them, Halo plays them at the looping sound itself, as here).
/// </summary>
/// <remarks>
/// Not done: vertical cylinder sound scenery (heard only above its origin up to its
/// height, by horizontal distance; spheres here), the background sounds' adjacent-cluster fades (Halo fades a neighbour
/// cluster's sound in over its cutoff distance from the portal; here the box the listener is in wins) and their
/// interior/portal/exterior scales, the inside-cluster sounds (Derelict, High Plains; one soundscape plays at a time),
/// and the looping sounds' existence radius (lsnd maximum distance).
/// </remarks>
static class LevelSounds
{
	/// <summary>
	/// Kinds of placements the scene places (<see cref="ScenarioObjectsBuilder"/>), whose looping sounds play.
	/// </summary>
	static readonly HashSet<string> PlacedKinds = ["scenery", "machine", "control", "light fixture", "crate"];

	public static void Build( Halo2Mount host, CacheFile cache, ScenarioTag scenario )
	{
		var sounds = host.Sounds;
		if ( sounds is null )
			return;

		var root = new GameObject( true, "sounds" );
		var counts = (Scenery: 0, Objects: 0, Clusters: 0, Points: 0);

		// Decode what the level plays in parallel up front, as the Halo CE mount does: loading the sound events one by
		// one below would decode them one after another.
		try
		{
			foreach ( var attachment in SoundPlacements.ReadSoundScenery( cache.Scenario ).SelectMany( p => p.Sounds ) )
				sounds.GetLoopingSound( attachment.LoopingSound.Path )?.Prefetch();

			var backgrounds = scenario.StructureBsps.Where( b => b.Bsp is not null )
				.SelectMany( b => SoundPlacements.ReadClusterSounds( b.Bsp ) ).Select( c => c.BackgroundSound?.Path ).Distinct();
			foreach ( var path in backgrounds )
				sounds.GetLoopingSound( path )?.Prefetch( background: true );
		}
		catch ( Exception e )
		{
			host.Logger.Warning( e, $"{cache.Name}: couldn't read the level's sounds: {e.Message}" );
		}

		try
		{
			foreach ( var placement in SoundPlacements.ReadSoundScenery( cache.Scenario ) )
			{
				// Placed only by scripts, or never.
				if ( (placement.Flags & 0x41) != 0 )
					continue;

				// Halo 2 hands each override above 0 to every track and detail the sound scenery's looping
				// sounds start, replacing their own minimum or maximum distance; the ssce's own bounds (+0xBC) aren't used.
				var distances = (placement.DistanceMin, placement.DistanceMax);

				foreach ( var attachment in placement.Sounds )
				{
					var resources = sounds.GetLoopingSound( attachment.LoopingSound.Path );
					if ( resources is null )
						continue;

					var go = new GameObject( root, true, System.IO.Path.GetFileName( attachment.LoopingSound.Path ) );
					go.WorldPosition = (Vector3)placement.Position * Halo2Mount.WorldScale;
					go.Tags.Add( "sound_scenery" );
					counts.Points += AddLoopingSound( go, resources, distances, placement.Index );
					counts.Scenery++;
				}
			}
		}
		catch ( Exception e )
		{
			host.Logger.Warning( e, $"{cache.Name}: couldn't place the sound scenery: {e.Message}" );
		}

		try
		{
			counts.Objects += AddObjectSounds( sounds, cache, root, ref counts.Points );
		}
		catch ( Exception e )
		{
			host.Logger.Warning( e, $"{cache.Name}: couldn't place the objects' looping sounds: {e.Message}" );
		}

		try
		{
			for ( var b = 0; b < scenario.StructureBsps.Count; b++ )
			{
				if ( scenario.StructureBsps[b].Bsp is { } bsp )
					counts.Clusters += AddClusters( sounds, bsp, b, root );
			}
		}
		catch ( Exception e )
		{
			host.Logger.Warning( e, $"{cache.Name}: couldn't read the clusters' sounds: {e.Message}" );
		}

		host.Logger.Info( $"{cache.Name}: {counts.Scenery} sound scenery sounds, {counts.Objects} object sounds ({counts.Points} sound points), {counts.Clusters} cluster sound volumes" );
	}

	/// <summary>
	/// Looping sounds that placed objects attach without a scale function.
	/// </summary>
	static int AddObjectSounds( Halo2Sounds sounds, CacheFile cache, GameObject root, ref int points )
	{
		var count = 0;
		var attachments = new Dictionary<TagEntry, IReadOnlyList<ObjectSoundAttachment>>();
		foreach ( var placement in new ScenarioObjects( cache.Scenario ).Objects )
		{
			if ( !PlacedKinds.Contains( placement.Kind ) || placement.NotAutomatically || !ObjectTag.IsObject( placement.Object ) )
				continue;

			if ( !attachments.TryGetValue( placement.Object, out var list ) )
				attachments[placement.Object] = list = SoundPlacements.GetAttachedLoopingSounds( placement.Object ).Where( a => string.IsNullOrEmpty( a.PrimaryScale ) ).ToList();

			if ( list.Count == 0 )
				continue;

			var model = RenderModel( placement.Object );
			foreach ( var attachment in list )
			{
				var resources = sounds.GetLoopingSound( attachment.LoopingSound.Path );
				if ( resources is null )
					continue;

				var marker = string.IsNullOrEmpty( attachment.Marker ) ? null : model?.FindMarker( attachment.Marker );
				var position = placement.Transform( marker?.Translation ?? System.Numerics.Vector3.Zero );
				var go = new GameObject( root, true, $"{System.IO.Path.GetFileName( placement.Object.Path )} {System.IO.Path.GetFileName( attachment.LoopingSound.Path )}" );
				go.WorldPosition = (Vector3)position * Halo2Mount.WorldScale;
				go.Tags.Add( "object_sound" );
				points += AddLoopingSound( go, resources, null, count );
				count++;
			}
		}

		return count;
	}

	static RenderModelTag RenderModel( TagEntry objectTag )
	{
		var obj = new ObjectTag( objectTag );
		if ( obj.Model is not { Address: not 0 } hlmtTag || hlmtTag.Class != ModelTag.Class )
			return null;

		var hlmt = new ModelTag( hlmtTag );
		return hlmt.RenderModel is { Address: not 0 } tag && tag.Class == RenderModelTag.Class ? new RenderModelTag( tag ) : null;
	}

	/// <summary>
	/// A sound point per track and a repeating one per detail sound; returns how many.
	/// </summary>
	/// <param name="distances">Minimum and maximum distance (world units) to fade over instead of the sounds' own.</param>
	/// <param name="seed">Picks the details' offsets, the same on every load.</param>
	public static int AddLoopingSound( GameObject go, LoopingSoundResources resources, (float Minimum, float Maximum)? distances, int seed )
	{
		var count = 0;
		foreach ( var track in resources.Tracks )
		{
			var soundEvent = GameResource.Load<SoundEvent>( track.Event.Path );
			if ( soundEvent is null )
				continue;

			// The gain and random pitch are in the event; a volume override would also fix the pitch.
			var component = go.AddComponent<SoundPointComponent>();
			component.SoundEvent = soundEvent;
			component.PlayOnStart = true;
			OverrideDistances( component, distances, track.Event );
			count++;
		}

		var random = new Random( seed * 7919 + 17 );
		foreach ( var detail in resources.Details )
		{
			var soundEvent = GameResource.Load<SoundEvent>( detail.Event.Path );
			if ( soundEvent is null )
				continue;

			// Halo plays a detail at a random offset from the looping sound each time; one offset per placement here.
			var target = go;
			var offset = DetailOffset( detail.Detail, random );
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
			OverrideDistances( component, distances, detail.Event );
			count++;
		}

		return count;
	}

	/// <summary>
	/// Replaces the sound's minimum and/or maximum distance with the placement's overrides above 0.
	/// </summary>
	static void OverrideDistances( SoundPointComponent component, (float Minimum, float Maximum)? distances, SoundEventLoader sound )
	{
		if ( distances is not { } d || (d.Minimum <= 0 && d.Maximum <= 0) )
			return;

		var minimum = d.Minimum > 0 ? d.Minimum : sound.MinimumDistance;
		var maximum = d.Maximum > 0 ? d.Maximum : sound.MaximumDistance;
		if ( maximum <= 0 )
			return;

		component.DistanceAttenuationOverride = true;
		component.DistanceAttenuation = true;
		component.Distance = maximum * Halo2Mount.WorldScale;
		component.Falloff = SoundEventLoader.Falloff( Math.Clamp( minimum / maximum, 0, 1 ) );
	}

	/// <summary>
	/// A detail's offset from its looping sound: a direction within its yaw and pitch bounds at a distance within its
	/// distance bounds; none when the distance bounds are 0 (any distance).
	/// </summary>
	static Vector3 DetailOffset( global::Blam.Halo1.Tags.LoopingSoundDetail detail, Random random )
	{
		if ( detail.DistanceMax <= 0 )
			return Vector3.Zero;

		static float Between( Random random, float a, float b ) => a + (float)random.NextDouble() * (b - a);
		var distance = Between( random, detail.DistanceMin, MathF.Max( detail.DistanceMin, detail.DistanceMax ) );
		var yaw = Between( random, detail.YawMin, detail.YawMax );
		var pitch = Between( random, detail.PitchMin, detail.PitchMax );
		var direction = new Vector3( MathF.Cos( pitch ) * MathF.Cos( yaw ), MathF.Cos( pitch ) * MathF.Sin( yaw ), MathF.Sin( pitch ) );
		return direction * distance * Halo2Mount.WorldScale;
	}

	/// <summary>
	/// A cluster's background sound as a soundscape trigger over its box and its sound environment as a DSP volume.
	/// </summary>
	static int AddClusters( Halo2Sounds sounds, TagEntry bsp, int bspIndex, GameObject root )
	{
		var clusters = SoundPlacements.ReadClusterSounds( bsp );

		// Smaller clusters win where boxes overlap: the sound environment's priority first, as in Halo, then size.
		static float Volume( ClusterSound c )
		{
			var size = c.BoundsMax - c.BoundsMin;
			return MathF.Max( size.X, 0 ) * MathF.Max( size.Y, 0 ) * MathF.Max( size.Z, 0 );
		}

		var ranks = clusters.OrderByDescending( Volume ).Select( ( c, i ) => (c, i) ).ToDictionary( x => x.c, x => x.i );
		var count = 0;
		foreach ( var cluster in clusters )
		{
			var min = (Vector3)cluster.BoundsMin * Halo2Mount.WorldScale;
			var max = (Vector3)cluster.BoundsMax * Halo2Mount.WorldScale;
			if ( min.x > max.x || min.y > max.y || min.z > max.z )
				continue;

			// Flat clusters (a doorway) still need some height to stand in.
			max = Vector3.Max( max, min + 1 );

			var background = sounds.GetLoopingSound( cluster.BackgroundSound?.Path );
			var environment = cluster.SoundEnvironment is null ? null : new SoundEnvironment( cluster.SoundEnvironment );
			var preset = environment?.DspPreset;
			if ( background?.Soundscape is null && preset is null )
				continue;

			var go = new GameObject( root, true, $"bsp {bspIndex} cluster {cluster.Cluster}" );
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

			if ( preset is not null )
			{
				var dsp = go.AddComponent<DspVolume>();
				dsp.Dsp = preset;
				dsp.Priority = environment.Priority * 100000 + ranks[cluster];
				dsp.SceneVolume = new SceneVolume { Type = SceneVolume.VolumeTypes.Box, Box = BBox.FromPositionAndSize( Vector3.Zero, max - min ) };
			}

			count++;
		}

		return count;
	}

	/// <summary>
	/// A Halo 2 sound environment (<c>snde</c>, laid out like Halo CE's: priority 0x4, room intensity 0x8, decay time
	/// 0x14, reflections intensity 0x1C, reflections delay 0x20, reverb intensity 0x24), its intensities in dB.
	/// </summary>
	sealed class SoundEnvironment
	{
		public int Priority { get; }
		public string DspPreset { get; }

		public SoundEnvironment( TagEntry tag )
		{
			var data = tag.Cache.TagData;
			Priority = data.I16( tag.Address + 0x4 );
			var decay = data.F32( tag.Address + 0x14 );
			var reflectionsDelay = data.F32( tag.Address + 0x20 );
			var reverb = data.F32( tag.Address + 0x24 );
			DspPreset = PresetFor( System.IO.Path.GetFileName( tag.Path ).ToLowerInvariant(), decay, reflectionsDelay, reverb );
		}

		/// <summary>
		/// The closest of s&amp;box's built-in DSP presets, as the Halo CE mount picks them, or null for none: outdoor
		/// environments (late reflections, or a reverb 15 dB or more down, like Zanzibar's "city") play dry.
		/// </summary>
		static string PresetFor( string name, float decay, float reflectionsDelay, float reverb )
		{
			if ( reflectionsDelay > 0.1f || reverb < -15 || name is "forest" or "mountains" )
				return null;

			if ( name.Contains( "sewer" ) || name.Contains( "pipe" ) )
				return "tunnel.small";
			if ( name.Contains( "hallway" ) )
				return "tunnel.small";
			if ( name.Contains( "cave" ) )
				return "cavern.medium";
			if ( name.Contains( "hangar" ) )
				return "metallic.large";
			if ( name.Contains( "bathroom" ) || name.Contains( "small" ) )
				return "room.empty.small";

			return decay switch
			{
				< 1.2f => "room.empty.small",
				< 2f => "concrete.small",
				< 3f => "concrete.medium",
				< 5f => "chamber.large",
				< 8f => "room.diffuse.huge",
				_ => "metallic.large",
			};
		}
	}
}
