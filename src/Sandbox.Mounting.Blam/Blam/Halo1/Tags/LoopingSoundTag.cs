using System.Collections.Generic;

namespace Blam.Halo1.Tags;

// Inside the namespace so these win over s&box's global Vector2/Vector3 when this compiles into the mount.
using Vector3 = System.Numerics.Vector3;

/// <summary>
/// One track of a looping sound: an optional start sound, the loop, and an optional end sound,
/// with an alternate loop and end a script can switch to (music changing to its tense version).
/// </summary>
/// <param name="Flags">Bit 0: fade in at start, bit 1: fade out at stop, bit 2: fade in alternate.</param>
/// <param name="FadeInDuration">Seconds.</param>
/// <param name="FadeOutDuration">Seconds.</param>
public sealed record LoopingSoundTrack(
	uint Flags,
	float Gain,
	float FadeInDuration,
	float FadeOutDuration,
	TagEntry Start,
	TagEntry Loop,
	TagEntry End,
	TagEntry AlternateLoop,
	TagEntry AlternateEnd )
{
	public bool FadeInAtStart => (Flags & 1) != 0;

	/// <summary>
	/// The loop playing in the given state. Halo only switches to the alternate loop when a script
	/// asks for it (<c>sound_looping_set_alternate</c>); objects and cluster background sounds
	/// always play the regular one.
	/// </summary>
	public TagEntry GetLoop( bool alternate ) => alternate && AlternateLoop is not null ? AlternateLoop : Loop;

	/// <summary>
	/// The sound played when the loop stops in the given state.
	/// </summary>
	public TagEntry GetEnd( bool alternate ) => alternate && AlternateEnd is not null ? AlternateEnd : End;
}

/// <summary>
/// A one-shot sound a looping sound plays at random intervals (birds, creaks, distant gunfire),
/// at a random offset from the looping sound.
/// </summary>
/// <param name="PeriodMin">Seconds between plays (lower bound), before the looping sound's period scale.</param>
/// <param name="Flags">Bit 0: don't play with the alternate loop, bit 1: don't play without it.</param>
/// <param name="YawMin">Direction of the offset: yaw bounds in radians.</param>
/// <param name="PitchMin">Direction of the offset: pitch (elevation) bounds in radians.</param>
/// <param name="DistanceMin">Length of the offset in world units. 0 plays at the looping sound itself.</param>
public sealed record LoopingSoundDetail(
	TagEntry Sound,
	float PeriodMin,
	float PeriodMax,
	float Gain,
	uint Flags,
	float YawMin,
	float YawMax,
	float PitchMin,
	float PitchMax,
	float DistanceMin,
	float DistanceMax )
{
	/// <summary>
	/// Whether the detail plays while the looping sound is (or isn't) on its alternate loop.
	/// </summary>
	public bool PlaysWith( bool alternate ) => alternate ? (Flags & 1) == 0 : (Flags & 2) == 0;
}

/// <summary>
/// Reader for <c>lsnd</c> (looping sound) tags: ambient loops, machine hums, music.
/// </summary>
/// <remarks>
/// Offsets: flags 0x0 (bit 1: not a loop, plays once), detail period multiplier at scale 0 0x4
/// and at scale 1 0x10, maximum distance 0x20 (runtime), continuous damage effect 0x2C, tracks
/// block 0x3C (0xA0 bytes each: flags 0x0, gain 0x4, fade in/out duration 0x8/0xC, start 0x30,
/// loop 0x40, end 0x50, alternate loop 0x80, alternate end 0x90), detail sounds block 0x48 (0x68
/// bytes each: sound 0x0, random period bounds 0x10, gain 0x18, flags 0x1C, yaw bounds 0x50, pitch
/// bounds 0x58, distance bounds 0x60).
///
/// Halo schedules a detail sound's next play at a random period (scaled by the period multiplier
/// for the looping sound's scale) plus the detail sound's longest permutation, so a detail never
/// overlaps itself. Its position is the looping sound's plus an offset in a random direction
/// within the yaw/pitch bounds and at a random distance within the distance bounds. Details of a
/// sound without a position (a cluster's background sound) are offset from the listener.
/// </remarks>
public sealed class LoopingSoundTag
{
	public uint Flags { get; }
	public bool NotALoop => (Flags & 2) != 0;

	/// <summary>
	/// Multipliers of the detail sounds' periods at scale 0 and 1. Placed looping sounds have no
	/// scale, so they use the first.
	/// </summary>
	public float DetailPeriodScaleZero { get; }
	public float DetailPeriodScaleOne { get; }

	public float MaximumDistance { get; }
	public IReadOnlyList<LoopingSoundTrack> Tracks { get; }
	public IReadOnlyList<LoopingSoundDetail> DetailSounds { get; }

	public LoopingSoundTag( CacheFile cache, TagEntry tag )
	{
		var data = cache.TagData;
		var a = tag.Address;

		Flags = data.U32( a );
		DetailPeriodScaleZero = data.F32( a + 0x4 );
		DetailPeriodScaleOne = data.F32( a + 0x10 );
		MaximumDistance = data.F32( a + 0x20 );

		var tracks = new List<LoopingSoundTrack>();
		foreach ( var t in data.Block( a + 0x3C, 0xA0 ) )
		{
			tracks.Add( new LoopingSoundTrack(
				Flags: data.U32( t ),
				Gain: data.F32( t + 0x4 ),
				FadeInDuration: data.F32( t + 0x8 ),
				FadeOutDuration: data.F32( t + 0xC ),
				Start: cache.GetTagReference( data, t + 0x30 ),
				Loop: cache.GetTagReference( data, t + 0x40 ),
				End: cache.GetTagReference( data, t + 0x50 ),
				AlternateLoop: cache.GetTagReference( data, t + 0x80 ),
				AlternateEnd: cache.GetTagReference( data, t + 0x90 ) ) );
		}
		Tracks = tracks;

		var details = new List<LoopingSoundDetail>();
		foreach ( var d in data.Block( a + 0x48, 0x68 ) )
		{
			var sound = cache.GetTagReference( data, d );
			if ( sound is null )
				continue;

			details.Add( new LoopingSoundDetail(
				Sound: sound,
				PeriodMin: data.F32( d + 0x10 ),
				PeriodMax: data.F32( d + 0x14 ),
				Gain: data.F32( d + 0x18 ),
				Flags: data.U32( d + 0x1C ),
				YawMin: data.F32( d + 0x50 ),
				YawMax: data.F32( d + 0x54 ),
				PitchMin: data.F32( d + 0x58 ),
				PitchMax: data.F32( d + 0x5C ),
				DistanceMin: data.F32( d + 0x60 ),
				DistanceMax: data.F32( d + 0x64 ) ) );
		}
		DetailSounds = details;
	}

	/// <summary>
	/// Every <c>snd!</c> tag used as a loop by any looping sound in the map. These are the sounds
	/// that should repeat seamlessly.
	/// </summary>
	public static IEnumerable<TagEntry> FindLoopedSounds( CacheFile cache )
	{
		foreach ( var tag in cache.Tags )
		{
			if ( tag.Class != SoundTagClasses.LoopingSound )
				continue;

			var data = cache.TagData;
			foreach ( var t in data.Block( tag.Address + 0x3C, 0xA0 ) )
			{
				var loop = cache.GetTagReference( data, t + 0x40 );
				if ( loop is not null ) yield return loop;

				var alternate = cache.GetTagReference( data, t + 0x80 );
				if ( alternate is not null ) yield return alternate;
			}
		}
	}
}

/// <summary>
/// Reader for <c>snde</c> (sound environment) tags: the reverb of an area, as EAX 2 / I3DL2
/// listener properties.
/// </summary>
/// <remarks>
/// Offsets: priority 0x4 (the highest priority environment around a listener wins), room intensity
/// 0x8 and room intensity at high frequencies 0xC, room rolloff 0x10, decay time 0x14 (s), decay
/// high frequency ratio 0x18, reflections intensity 0x1C, reflections delay 0x20 (s), reverb
/// intensity 0x24, reverb delay 0x28 (s), diffusion 0x2C, density 0x30, high frequency reference
/// 0x34 (Hz). 0x48 bytes. Intensities are linear gains (0-1), not dB. MCC's maps only use the 12
/// tags in <c>sound\sound environments\</c>, named after and set like the EAX presets (alley, arena,
/// bathroom, cave, concert_hall, forest, hallway, hangar, mountains, quarry, stone_room,
/// underwater), all at priority 0.
/// </remarks>
public sealed class SoundEnvironmentTag
{
	public TagEntry Tag { get; }
	public int Priority { get; }
	public float RoomIntensity { get; }
	public float RoomIntensityHf { get; }
	public float RoomRolloff { get; }
	public float DecayTime { get; }
	public float DecayHfRatio { get; }
	public float ReflectionsIntensity { get; }
	public float ReflectionsDelay { get; }
	public float ReverbIntensity { get; }
	public float ReverbDelay { get; }
	public float Diffusion { get; }
	public float Density { get; }
	public float HfReference { get; }

	public SoundEnvironmentTag( CacheFile cache, TagEntry tag )
	{
		var data = cache.TagData;
		var a = tag.Address;

		Tag = tag;
		Priority = data.I16( a + 0x4 );
		RoomIntensity = data.F32( a + 0x8 );
		RoomIntensityHf = data.F32( a + 0xC );
		RoomRolloff = data.F32( a + 0x10 );
		DecayTime = data.F32( a + 0x14 );
		DecayHfRatio = data.F32( a + 0x18 );
		ReflectionsIntensity = data.F32( a + 0x1C );
		ReflectionsDelay = data.F32( a + 0x20 );
		ReverbIntensity = data.F32( a + 0x24 );
		ReverbDelay = data.F32( a + 0x28 );
		Diffusion = data.F32( a + 0x2C );
		Density = data.F32( a + 0x30 );
		HfReference = data.F32( a + 0x34 );
	}
}

/// <summary>
/// A sound scenery object placed in a scenario: an invisible object whose attached looping
/// sounds play at its position (waterfalls, machinery, fires).
/// </summary>
public sealed record SoundSceneryPlacement( TagEntry Object, Vector3 Position, IReadOnlyList<TagEntry> LoopingSounds );

/// <summary>
/// A structure BSP cluster's background sound: the stereo ambience heard while inside it.
/// </summary>
/// <param name="BoundsMin">Bounds of the cluster's subclusters, in world units.</param>
public sealed record ClusterBackgroundSound( int Cluster, TagEntry LoopingSound, Vector3 BoundsMin, Vector3 BoundsMax );

/// <summary>
/// A structure BSP cluster's sound environment: the reverb heard while inside it.
/// </summary>
/// <param name="BoundsMin">Bounds of the cluster's subclusters, in world units.</param>
public sealed record ClusterSoundEnvironment( int Cluster, TagEntry SoundEnvironment, Vector3 BoundsMin, Vector3 BoundsMax );

/// <summary>
/// Readers for the sound parts of scenarios, objects and structure BSPs.
/// </summary>
/// <remarks>
/// Scenario: sound scenery placements at 0x2DC (0x28 bytes, laid out like other object
/// placements: palette index, flags 0x4, position 0x8) with a palette of <c>ssce</c> tags at 0x2E8
/// (0x30 bytes). Objects: attachments block at 0x140 (0x48 bytes: type reference first, often a
/// looping sound). Structure BSP: clusters at 0x134 (0x68 bytes: background sound index 0x4,
/// sound environment index 0x6, subclusters 0x34 of 0x24 bytes starting with x/y/z bounds),
/// background sound palette at 0x1FC (0x74 bytes: name, looping sound 0x20), sound environment
/// palette at 0x208 (0x50 bytes: name, sound environment 0x20).
/// </remarks>
public static class SoundPlacements
{
	public static IReadOnlyList<SoundSceneryPlacement> ReadSoundScenery( CacheFile cache, TagEntry scenario )
	{
		var data = cache.TagData;
		var palette = new List<TagEntry>();
		foreach ( var entry in data.Block( scenario.Address + 0x2E8, 0x30 ) )
			palette.Add( cache.GetTagReference( data, entry ) );

		var result = new List<SoundSceneryPlacement>();
		foreach ( var entry in data.Block( scenario.Address + 0x2DC, 0x28 ) )
		{
			var index = data.I16( entry );
			if ( index < 0 || index >= palette.Count || palette[index] is null )
				continue;

			// Placed only when a script creates it.
			if ( (data.U16( entry + 0x4 ) & 0x1) != 0 )
				continue;

			result.Add( new SoundSceneryPlacement( palette[index], data.Vec3( entry + 0x8 ), GetAttachedLoopingSounds( cache, palette[index] ) ) );
		}

		return result;
	}

	/// <summary>
	/// The looping sounds attached to an object.
	/// </summary>
	public static IReadOnlyList<TagEntry> GetAttachedLoopingSounds( CacheFile cache, TagEntry objectTag )
	{
		var data = cache.TagData;
		var result = new List<TagEntry>();
		foreach ( var attachment in data.Block( objectTag.Address + 0x140, 0x48 ) )
		{
			var type = cache.GetTagReference( data, attachment );
			if ( type is not null && type.Class == SoundTagClasses.LoopingSound )
				result.Add( type );
		}

		return result;
	}

	public static IReadOnlyList<ClusterBackgroundSound> ReadClusterBackgroundSounds( CacheFile cache, StructureBspReference reference )
	{
		var result = new List<ClusterBackgroundSound>();
		foreach ( var (cluster, tag, min, max) in ReadClusterPalette( cache, reference, 0x4, 0x1FC, 0x74 ) )
			result.Add( new ClusterBackgroundSound( cluster, tag, min, max ) );

		return result;
	}

	public static IReadOnlyList<ClusterSoundEnvironment> ReadClusterSoundEnvironments( CacheFile cache, StructureBspReference reference )
	{
		var result = new List<ClusterSoundEnvironment>();
		foreach ( var (cluster, tag, min, max) in ReadClusterPalette( cache, reference, 0x6, 0x208, 0x50 ) )
			result.Add( new ClusterSoundEnvironment( cluster, tag, min, max ) );

		return result;
	}

	/// <summary>
	/// The clusters that pick an entry of one of the BSP's palettes (with the tag reference at
	/// 0x20 of each entry), with the bounds of their subclusters.
	/// </summary>
	/// <param name="clusterField">Offset of the palette index in a cluster.</param>
	static List<(int Cluster, TagEntry Tag, Vector3 Min, Vector3 Max)> ReadClusterPalette( CacheFile cache, StructureBspReference reference, uint clusterField, uint paletteBlock, int paletteEntrySize )
	{
		var region = new IO.MemoryRegion( cache.Read( reference.FileOffset, reference.Size ), reference.Address );
		var tag = region.U32( reference.Address );

		var palette = new List<TagEntry>();
		foreach ( var entry in region.Block( tag + paletteBlock, paletteEntrySize ) )
			palette.Add( cache.GetTagReference( region, entry + 0x20 ) );

		var result = new List<(int, TagEntry, Vector3, Vector3)>();
		var clusterIndex = 0;
		foreach ( var cluster in region.Block( tag + 0x134, 0x68 ) )
		{
			var index = region.I16( cluster + clusterField );
			var current = clusterIndex++;
			if ( index < 0 || index >= palette.Count || palette[index] is null )
				continue;

			var min = new Vector3( float.MaxValue );
			var max = new Vector3( float.MinValue );
			foreach ( var sub in region.Block( cluster + 0x34, 0x24 ) )
			{
				min = Vector3.Min( min, new Vector3( region.F32( sub ), region.F32( sub + 0x8 ), region.F32( sub + 0x10 ) ) );
				max = Vector3.Max( max, new Vector3( region.F32( sub + 0x4 ), region.F32( sub + 0xC ), region.F32( sub + 0x14 ) ) );
			}

			if ( min.X > max.X )
				continue;

			result.Add( (current, palette[index], min, max) );
		}

		return result;
	}
}
