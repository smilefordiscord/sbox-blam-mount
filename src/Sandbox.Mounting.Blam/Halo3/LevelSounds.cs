using Blam.Halo3;
using LoopingSoundResources = Sandbox.Mounting.Blam.Halo1.LoopingSoundResources;
using SoundEventLoader = Sandbox.Mounting.Blam.Halo1.SoundEventLoader;

namespace Sandbox.Mounting.Blam.Halo3;

// A Halo 3 level's ambient sound, as Halo2/LevelSounds.cs builds Halo 2's out of engine components and the looping sound resources Halo3Sounds registers:
// sound scenery (a sound point per track of each looping sound the ssce attaches, a repeating one per detail, the placement's override distances above 0 replacing the sounds' own) and per BSP cluster the acoustics palette's background sound as a soundscape trigger over the cluster's box.
// Not done: objects' attached looping sounds (needs object placements and model markers, see the TODO in Build), sound environments (DSP), inside-cluster sounds, cylinder volumes, adjacent-cluster fades.
static class LevelSounds
{
	// bspIndices: the scenario's structure BSPs the scene has (a zone set's); null for all of them.
	public static void Build( Halo3Mount mount, CacheFile cache, IReadOnlyCollection<int> bspIndices = null )
	{
		if ( Halo3Sounds.Banks is null )
			return;

		var scenario = cache.Scenario;
		var root = new GameObject( true, "sounds" );
		var counts = (Scenery: 0, Clusters: 0, Points: 0);

		// Decode what the level plays in parallel up front; loading the events one by one below would decode them one after another.
		try
		{
			foreach ( var attachment in SoundPlacements.ReadSoundScenery( scenario ).Where( p => p.IsPlaced ).SelectMany( p => p.Sounds ) )
				Halo3Sounds.GetLoopingSound( attachment.LoopingSound.Path )?.Prefetch();

			var bsps = SoundPlacements.StructureBsps( scenario );
			var backgrounds = Enumerable.Range( 0, bsps.Count ).Where( b => bsps[b] is not null && (bspIndices is null || bspIndices.Contains( b )) )
				.SelectMany( b => SoundPlacements.ReadClusterSounds( scenario, bsps[b] ) ).Select( c => c.BackgroundSound?.Path ).Distinct();
			foreach ( var path in backgrounds )
				Halo3Sounds.GetLoopingSound( path )?.Prefetch( background: true );
		}
		catch ( Exception e )
		{
			mount.Logger.Warning( e, $"{cache.Name}: couldn't read the level's sounds: {e.Message}" );
		}

		try
		{
			foreach ( var placement in SoundPlacements.ReadSoundScenery( scenario ) )
			{
				if ( !placement.IsPlaced )
					continue;

				var distances = (placement.DistanceMin, placement.DistanceMax);
				foreach ( var attachment in placement.Sounds )
				{
					var resources = Halo3Sounds.GetLoopingSound( attachment.LoopingSound.Path );
					if ( resources is null )
						continue;

					var go = new GameObject( root, true, System.IO.Path.GetFileName( attachment.LoopingSound.Path ) );
					go.WorldPosition = (Vector3)placement.Position * Halo3Mount.WorldScale;
					go.Tags.Add( "sound_scenery" );
					counts.Points += AddLoopingSound( go, resources, distances, placement.Index );
					counts.Scenery++;
				}
			}
		}
		catch ( Exception e )
		{
			mount.Logger.Warning( e, $"{cache.Name}: couldn't place the sound scenery: {e.Message}" );
		}

		// TODO(scenario/lead): looping sounds of placed scenery/machines/controls (SoundPlacements.GetAttachedLoopingSounds on the object tag, empty PrimaryScale only) at the attachment's marker, once object placements and markers are readable; AddLoopingSound( go, resources, null, seed ) does the rest.

		try
		{
			var bsps = SoundPlacements.StructureBsps( scenario );
			for ( var b = 0; b < bsps.Count; b++ )
			{
				if ( bsps[b] is not null && (bspIndices is null || bspIndices.Contains( b )) )
					counts.Clusters += AddClusters( scenario, bsps[b], b, root );
			}
		}
		catch ( Exception e )
		{
			mount.Logger.Warning( e, $"{cache.Name}: couldn't read the clusters' sounds: {e.Message}" );
		}

		mount.Logger.Info( $"{cache.Name}: {counts.Scenery} sound scenery sounds ({counts.Points} sound points), {counts.Clusters} cluster background sounds" );
	}

	// A sound point per track and a repeating one per detail; returns how many. The seed picks the details' offsets, the same on every load.
	static int AddLoopingSound( GameObject go, LoopingSoundResources resources, (float Minimum, float Maximum)? distances, int seed )
	{
		var count = 0;
		foreach ( var track in resources.Tracks )
		{
			var soundEvent = GameResource.Load<SoundEvent>( track.Event.Path );
			if ( soundEvent is null )
				continue;

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

	// The placement's override distances above 0 replace the sound's own minimum and/or maximum.
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
		component.Distance = maximum * Halo3Mount.WorldScale;
		component.Falloff = SoundEventLoader.Falloff( Math.Clamp( minimum / maximum, 0, 1 ) );
	}

	// A direction within the detail's yaw and pitch bounds at a distance within its distance bounds; none when the distance bounds are 0.
	static Vector3 DetailOffset( global::Blam.Halo1.Tags.LoopingSoundDetail detail, Random random )
	{
		if ( detail.DistanceMax <= 0 )
			return Vector3.Zero;

		static float Between( Random random, float a, float b ) => a + (float)random.NextDouble() * (b - a);
		var distance = Between( random, detail.DistanceMin, MathF.Max( detail.DistanceMin, detail.DistanceMax ) );
		var yaw = Between( random, detail.YawMin, detail.YawMax );
		var pitch = Between( random, detail.PitchMin, detail.PitchMax );
		var direction = new Vector3( MathF.Cos( pitch ) * MathF.Cos( yaw ), MathF.Cos( pitch ) * MathF.Sin( yaw ), MathF.Sin( pitch ) );
		return direction * distance * Halo3Mount.WorldScale;
	}

	// Each cluster's background sound as a soundscape trigger over its box; smaller boxes are added later so they win where boxes overlap.
	static int AddClusters( TagEntry scenario, TagEntry bsp, int bspIndex, GameObject root )
	{
		var count = 0;
		foreach ( var cluster in SoundPlacements.ReadClusterSounds( scenario, bsp ).OrderByDescending( Volume ) )
		{
			var background = Halo3Sounds.GetLoopingSound( cluster.BackgroundSound?.Path );
			if ( background?.Soundscape is null )
				continue;

			var min = (Vector3)cluster.BoundsMin * Halo3Mount.WorldScale;
			var max = (Vector3)cluster.BoundsMax * Halo3Mount.WorldScale;
			if ( min.x > max.x || min.y > max.y || min.z > max.z )
				continue;

			// Flat clusters (a doorway) still need some height to stand in.
			max = Vector3.Max( max, min + 1 );

			var go = new GameObject( root, true, $"bsp {bspIndex} cluster {cluster.Cluster}" );
			go.WorldPosition = (min + max) * 0.5f;
			go.Tags.Add( "sound_cluster" );

			var trigger = go.AddComponent<SoundscapeTrigger>();
			trigger.Type = SoundscapeTrigger.TriggerType.Box;
			// Half extents, despite the name.
			trigger.BoxSize = (max - min) * 0.5f;
			trigger.Soundscape = GameResource.Load<Soundscape>( background.Soundscape.Path );
			count++;
		}

		return count;
	}

	static float Volume( ClusterSound c )
	{
		var size = c.BoundsMax - c.BoundsMin;
		return MathF.Max( size.X, 0 ) * MathF.Max( size.Y, 0 ) * MathF.Max( size.Z, 0 );
	}
}
