using System;
using System.Collections.Generic;
using System.Linq;

namespace Blam.Halo3;

// Inside the namespace so these win over s&box's global vector types when this compiles into the mount.
using Vector3 = System.Numerics.Vector3;

// One track of a looping sound: optional start ("in"), the loop, optional end ("out"), and an alternate loop/end scripts switch to. Flags: bit 0 fade in at start, 1 fade out at stop, 3 master surround track.
public sealed record LoopingSoundTrack( string Name, uint Flags, float GainDecibels, float FadeInDuration, float FadeOutDuration, TagEntry Start, TagEntry Loop, TagEntry End, TagEntry AlternateLoop, TagEntry AlternateEnd )
{
	public TagEntry GetLoop( bool alternate ) => alternate && AlternateLoop is not null ? AlternateLoop : Loop;
}

// A one-shot a looping sound plays at random intervals. Flags: bit 0 don't play with the alternate loop, 1 don't play without it, 2 start immediately; angles in radians, 0 and 0 = any.
public sealed record LoopingSoundDetail( string Name, TagEntry Sound, float PeriodMin, float PeriodMax, float GainDecibels, uint Flags,
	float YawMin, float YawMax, float PitchMin, float PitchMax, float DistanceMin, float DistanceMax )
{
	public bool PlaysWith( bool alternate ) => alternate ? (Flags & 1) == 0 : (Flags & 2) == 0;
}

// A looping sound (lsnd, 0x40; Assembly's Halo3 plugin, checked on MCC maps): flags 0x0 (bit 1 not a loop), distance bounds 0xC, sound class 0x24, tracks 0x28 (0x90), detail sounds 0x34 (0x3C).
public sealed class LoopingSoundTag
{
	public static readonly TagClass Class = new( "lsnd" );

	const uint NotALoopFlag = 1 << 1;

	public TagEntry Tag { get; }
	public uint Flags { get; }
	public float DistanceMin { get; }
	public float DistanceMax { get; }
	public int SoundClass { get; }
	public IReadOnlyList<LoopingSoundTrack> Tracks { get; }
	public IReadOnlyList<LoopingSoundDetail> DetailSounds { get; }

	public bool NotALoop => (Flags & NotALoopFlag) != 0;

	public LoopingSoundTag( TagEntry tag )
	{
		Tag = tag;
		var data = tag.Data;
		Flags = data.U32( 0x0 );
		DistanceMin = data.F32( 0xC );
		DistanceMax = data.F32( 0x10 );
		SoundClass = data.I16( 0x24 );

		Tracks = data.Block( 0x28, 0x90 ).Select( t => new LoopingSoundTrack(
			Name: t.StringId( 0x0 ) ?? "",
			Flags: t.U32( 0x4 ),
			GainDecibels: t.F32( 0x8 ),
			FadeInDuration: t.F32( 0xC ),
			FadeOutDuration: t.F32( 0x10 ),
			Start: Sound( t.Reference( 0x14 ) ),
			Loop: Sound( t.Reference( 0x24 ) ),
			End: Sound( t.Reference( 0x34 ) ),
			AlternateLoop: Sound( t.Reference( 0x44 ) ),
			AlternateEnd: Sound( t.Reference( 0x54 ) ) ) ).ToList();

		DetailSounds = data.Block( 0x34, 0x3C )
			.Select( d => (d, sound: Sound( d.Reference( 0x4 ) )) )
			.Where( x => x.sound is not null )
			.Select( x => new LoopingSoundDetail(
				Name: x.d.StringId( 0x0 ) ?? "",
				Sound: x.sound,
				PeriodMin: x.d.F32( 0x14 ),
				PeriodMax: x.d.F32( 0x18 ),
				GainDecibels: x.d.F32( 0x1C ),
				Flags: x.d.U32( 0x20 ),
				YawMin: x.d.F32( 0x24 ),
				YawMax: x.d.F32( 0x28 ),
				PitchMin: x.d.F32( 0x2C ),
				PitchMax: x.d.F32( 0x30 ),
				DistanceMin: x.d.F32( 0x34 ),
				DistanceMax: x.d.F32( 0x38 ) ) ).ToList();
	}

	static TagEntry Sound( TagEntry tag ) => tag is not null && tag.Class == SoundTag.Class && tag.Address != 0 ? tag : null;
}

// A looping sound an object (sound scenery, a machine) attaches: object attachments block 0xBC (0x20: type ref 0x0, marker 0x10, primary scale 0x18).
public sealed record ObjectSoundAttachment( TagEntry LoopingSound, string Marker, string PrimaryScale );

// A placed sound scenery (scnr +0x180, 0x70): palette 0x0, placement flags 0x4, position 0x8, override distance bounds 0x5C.
public sealed record SoundSceneryPlacement( int Index, TagEntry Object, uint Flags, Vector3 Position, float DistanceMin, float DistanceMax, IReadOnlyList<ObjectSoundAttachment> Sounds )
{
	// Placement flags bit 0 not automatically, 3 never placed.
	public bool IsPlaced => (Flags & 0x9) == 0;
}

// A structure BSP cluster's acoustics (sbsp clusters +0xC4, 0x118: bounds 0x0, acoustics index 0x1C into scnr's acoustics palette +0x4C0, 0x54: sound environment 0x4, background sound 0x1C, inside cluster sound 0x2C).
public sealed record ClusterSound( int Cluster, Vector3 BoundsMin, Vector3 BoundsMax, TagEntry BackgroundSound, TagEntry InsideSound, TagEntry SoundEnvironment );

public static class SoundPlacements
{
	public static IReadOnlyList<ObjectSoundAttachment> GetAttachedLoopingSounds( TagEntry objectTag )
	{
		if ( objectTag is null || objectTag.Address == 0 )
			return [];

		return objectTag.Data.Block( 0xBC, 0x20 )
			.Select( a => (Type: a.Reference( 0x0 ), a) )
			.Where( x => x.Type is not null && x.Type.Class == LoopingSoundTag.Class && x.Type.Address != 0 )
			.Select( x => new ObjectSoundAttachment( x.Type, x.a.StringId( 0x10 ) ?? "", x.a.StringId( 0x18 ) ?? "" ) )
			.ToList();
	}

	public static IReadOnlyList<SoundSceneryPlacement> ReadSoundScenery( TagEntry scenario )
	{
		if ( scenario is null || scenario.Address == 0 )
			return [];

		var data = scenario.Data;
		var palette = data.Block( 0x18C, 0x10 ).Select( p => p.Reference( 0x0 ) ).ToList();
		var attachments = new Dictionary<TagEntry, IReadOnlyList<ObjectSoundAttachment>>();
		var placements = new List<SoundSceneryPlacement>();
		var block = data.Block( 0x180, 0x70 );
		for ( var i = 0; i < block.Count; i++ )
		{
			var p = block[i];
			var index = p.I16( 0x0 );
			var tag = index >= 0 && index < palette.Count ? palette[index] : null;
			if ( tag is null || tag.Address == 0 )
				continue;

			if ( !attachments.TryGetValue( tag, out var sounds ) )
				attachments[tag] = sounds = GetAttachedLoopingSounds( tag );

			placements.Add( new SoundSceneryPlacement( i, tag, p.U32( 0x4 ), p.Vec3( 0x8 ), p.F32( 0x5C ), p.F32( 0x60 ), sounds ) );
		}

		return placements;
	}

	// The scenario's structure BSPs (scnr +0x18, 0x6C, sbsp reference at 0x0), null where missing.
	public static IReadOnlyList<TagEntry> StructureBsps( TagEntry scenario )
	{
		if ( scenario is null || scenario.Address == 0 )
			return [];

		return scenario.Data.Block( 0x18, 0x6C ).Select( b => b.Reference( 0x0 ) ).Select( t => t is not null && t.Address != 0 ? t : null ).ToList();
	}

	public static IReadOnlyList<ClusterSound> ReadClusterSounds( TagEntry scenario, TagEntry bsp )
	{
		if ( scenario is null || bsp is null || bsp.Address == 0 )
			return [];

		var acoustics = scenario.Data.Block( 0x4C0, 0x54 );
		var clusters = bsp.Data.Block( 0xC4, 0x118 );
		var sounds = new List<ClusterSound>();
		for ( var c = 0; c < clusters.Count; c++ )
		{
			var cluster = clusters[c];
			var index = cluster.I16( 0x1C );
			if ( index < 0 || index >= acoustics.Count )
				continue;

			var entry = acoustics[index];
			var min = new Vector3( cluster.F32( 0x0 ), cluster.F32( 0x8 ), cluster.F32( 0x10 ) );
			var max = new Vector3( cluster.F32( 0x4 ), cluster.F32( 0xC ), cluster.F32( 0x14 ) );
			sounds.Add( new ClusterSound( c, min, max, Live( entry.Reference( 0x1C ), LoopingSoundTag.Class ), Live( entry.Reference( 0x2C ), LoopingSoundTag.Class ),
				Live( entry.Reference( 0x4 ), new TagClass( "snde" ) ) ) );
		}

		return sounds;
	}

	static TagEntry Live( TagEntry tag, TagClass tagClass ) => tag is not null && tag.Class == tagClass && tag.Address != 0 ? tag : null;
}
