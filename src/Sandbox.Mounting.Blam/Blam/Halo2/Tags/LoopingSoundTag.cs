using System;
using System.Collections.Generic;
using System.Linq;

namespace Blam.Halo2.Tags;

// Inside the namespace so these win over s&box's global vector types when this compiles into the mount.
using Vector3 = System.Numerics.Vector3;

/// <summary>
/// One track of a Halo 2 looping sound: an optional start ("in") sound, the loop, an optional end ("out") sound,
/// and an alternate loop and end a script can switch to.
/// </summary>
/// <param name="Flags">Bit 0 fade in at start, 1 fade out at stop, 2 cross fade the alternate loop.</param>
/// <param name="GainDecibels">The track's gain in dB (0 = as the sounds are).</param>
public sealed record LoopingSoundTrack(
	string Name,
	uint Flags,
	float GainDecibels,
	float FadeInDuration,
	float FadeOutDuration,
	TagEntry Start,
	TagEntry Loop,
	TagEntry End,
	TagEntry AlternateLoop,
	TagEntry AlternateEnd )
{
	/// <summary>
	/// The loop playing in the given state; scripts switch to the alternate (<c>sound_looping_set_alternate</c>),
	/// objects and cluster background sounds always play the regular one.
	/// </summary>
	public TagEntry GetLoop( bool alternate ) => alternate && AlternateLoop is not null ? AlternateLoop : Loop;
}

/// <summary>
/// A one-shot sound a looping sound plays at random intervals (birds, gulls, creaks, distant machinery).
/// </summary>
/// <param name="PeriodMin">Seconds between plays, lower bound.</param>
/// <param name="GainDecibels">The detail's gain in dB.</param>
/// <param name="Flags">Bit 0 don't play with the alternate loop, 1 don't play without it, 2 start immediately with the loop.</param>
/// <param name="YawMin">Direction of its offset from the looping sound (radians); 0 and 0 = any.</param>
/// <param name="PitchMin">Elevation bounds of the offset (radians); 0 and 0 = any.</param>
/// <param name="DistanceMin">Length of the offset (world units); 0 and 0 = any (within the sound's distances).</param>
public sealed record LoopingSoundDetail(
	string Name,
	TagEntry Sound,
	float PeriodMin,
	float PeriodMax,
	float GainDecibels,
	uint Flags,
	float YawMin,
	float YawMax,
	float PitchMin,
	float PitchMax,
	float DistanceMin,
	float DistanceMax )
{
	public bool PlaysWith( bool alternate ) => alternate ? (Flags & 1) == 0 : (Flags & 2) == 0;
}

/// <summary>
/// A Halo 2 looping sound (<c>lsnd</c>): ambient loops, machine hums, music.
/// </summary>
/// <remarks>
/// Layout (0x34, Assembly's Halo2MCC plugin, checked against every MCC map with <c>BlamInspect h2 &lt;maps&gt; lsnd</c>):
/// remastered twin 0x0, flags 0x8 (bit 1 not a loop, 2 stops music, 3 always spatialize, 8 legacy only, 9 remastered
/// only), music time bounds 0xC, distance bounds 0x14, continuous damage effect 0x1C, tracks 0x24 (0x58: name sid 0x0,
/// flags 0x4, gain dB 0x8, fade in/out 0xC/0x10, in 0x14, loop 0x1C, out 0x24, alternate loop 0x2C, alternate out
/// 0x34, output effect 0x3C, alternate transitions 0x40/0x48, cross fade 0x50, alternate fade out 0x54), detail
/// sounds 0x2C (0x34: name sid 0x0, sound 0x4, period bounds 0xC, gain dB 0x14, flags 0x18, yaw bounds 0x1C, pitch
/// bounds 0x24, distance bounds 0x2C).
/// </remarks>
public sealed class LoopingSoundTag
{
	public static readonly TagClass Class = new( "lsnd" );

	const uint NotALoopFlag = 1 << 1;
	const uint RemasteredOnlyFlag = 1 << 9;

	public TagEntry Tag { get; }
	public uint Flags { get; }
	public TagEntry Remastered { get; }
	public float DistanceMin { get; }
	public float DistanceMax { get; }
	public IReadOnlyList<LoopingSoundTrack> Tracks { get; }
	public IReadOnlyList<LoopingSoundDetail> DetailSounds { get; }

	public bool NotALoop => (Flags & NotALoopFlag) != 0;
	public bool IsRemasteredOnly => (Flags & RemasteredOnlyFlag) != 0;

	public LoopingSoundTag( TagEntry tag )
	{
		Tag = tag;
		var cache = tag.Cache;
		var data = cache.TagData;
		var a = tag.Address;

		Remastered = cache.GetTagReference( a );
		Flags = data.U32( a + 0x8 );
		DistanceMin = data.F32( a + 0x14 );
		DistanceMax = data.F32( a + 0x18 );

		Tracks = data.Elements( a + 0x24, 0x58 ).Select( t => new LoopingSoundTrack(
			Name: cache.GetString( data.U32( t ) ) ?? "",
			Flags: data.U32( t + 0x4 ),
			GainDecibels: data.F32( t + 0x8 ),
			FadeInDuration: data.F32( t + 0xC ),
			FadeOutDuration: data.F32( t + 0x10 ),
			Start: Sound( cache.GetTagReference( t + 0x14 ) ),
			Loop: Sound( cache.GetTagReference( t + 0x1C ) ),
			End: Sound( cache.GetTagReference( t + 0x24 ) ),
			AlternateLoop: Sound( cache.GetTagReference( t + 0x2C ) ),
			AlternateEnd: Sound( cache.GetTagReference( t + 0x34 ) ) ) ).ToList();

		DetailSounds = data.Elements( a + 0x2C, 0x34 )
			.Select( d => (d, sound: Sound( cache.GetTagReference( d + 0x4 ) )) )
			.Where( x => x.sound is not null )
			.Select( x => new LoopingSoundDetail(
				Name: cache.GetString( data.U32( x.d ) ) ?? "",
				Sound: x.sound,
				PeriodMin: data.F32( x.d + 0xC ),
				PeriodMax: data.F32( x.d + 0x10 ),
				GainDecibels: data.F32( x.d + 0x14 ),
				Flags: data.U32( x.d + 0x18 ),
				YawMin: data.F32( x.d + 0x1C ),
				YawMax: data.F32( x.d + 0x20 ),
				PitchMin: data.F32( x.d + 0x24 ),
				PitchMax: data.F32( x.d + 0x28 ),
				DistanceMin: data.F32( x.d + 0x2C ),
				DistanceMax: data.F32( x.d + 0x30 ) ) ).ToList();
	}

	static TagEntry Sound( TagEntry tag ) => tag is not null && tag.Class == SoundTag.Class && tag.Address != 0 ? tag : null;

	/// <summary>
	/// Every sound tag some looping sound of the map plays as a loop (they repeat seamlessly).
	/// </summary>
	public static IEnumerable<TagEntry> FindLoopedSounds( CacheFile cache )
	{
		foreach ( var tag in cache.Tags )
		{
			if ( tag.Class != Class || tag.Address == 0 )
				continue;

			foreach ( var track in new LoopingSoundTag( tag ).Tracks )
			{
				if ( track.Loop is not null ) yield return track.Loop;
				if ( track.AlternateLoop is not null ) yield return track.AlternateLoop;
			}
		}
	}
}

/// <summary>
/// A sound scenery object placed in a scenario (<c>ssce</c>): invisible, its attached looping sounds play where it stands.
/// </summary>
/// <param name="VolumeType">0 sphere, 1 vertical cylinder (<paramref name="Height"/> tall).</param>
/// <param name="DistanceMin">Override of the sounds' minimum distance (world units), 0 = none.</param>
/// <param name="DistanceMax">Override of the sounds' maximum distance, 0 = none.</param>
/// <param name="ConeMin">Override cone angle bounds (radians), 0 = none.</param>
/// <param name="OuterConeGain">Override gain outside the cone (dB).</param>
public sealed record SoundSceneryPlacement( int Index, TagEntry Object, Vector3 Position, Vector3 Orientation, uint Flags, int NameIndex,
	int VolumeType, float Height, float DistanceMin, float DistanceMax, float ConeMin, float ConeMax, float OuterConeGain,
	IReadOnlyList<ObjectSoundAttachment> Sounds );

/// <summary>
/// A looping sound an object attaches (object <c>+0x94</c> attachments whose type is an <c>lsnd</c>).
/// </summary>
/// <param name="Marker">Marker of the object's model it plays at, or null/empty for the object's origin.</param>
/// <param name="PrimaryScale">Object function (name) driving the sound's scale, or null.</param>
public sealed record ObjectSoundAttachment( TagEntry LoopingSound, string Marker, string PrimaryScale, string SecondaryScale );

/// <summary>
/// A structure BSP cluster's background sound and sound environment.
/// </summary>
/// <param name="BoundsMin">The cluster's bounding box (world units).</param>
public sealed record ClusterSound( int Cluster, TagEntry BackgroundSound, TagEntry InsideClusterSound, float BackgroundCutoff,
	uint BackgroundScaleFlags, float InteriorScale, float PortalScale, float ExteriorScale,
	TagEntry SoundEnvironment, Vector3 BoundsMin, Vector3 BoundsMax );

/// <summary>
/// Readers for the sound parts of Halo 2 scenarios, objects and structure BSPs.
/// </summary>
/// <remarks>
/// Scenario: sound scenery placements 0xD8 (0x50: the usual placement header, then volume type 0x34, height 0x38,
/// override distance bounds 0x3C, cone angle bounds 0x44, outer cone gain 0x4C), palette 0xE0 (0x28, <c>ssce</c> first).
/// Objects: attachments 0x94 (0x18: type 0x0, marker sid 0x8, change colour 0xC, primary/secondary scale sids 0x10/0x14).
/// Structure BSP: clusters 0x9C (0xB0: bounds 0x54, background sound 0x72, sound environment 0x74), background sound
/// palette 0xD4 (0x64: name, looping sound 0x20, inside cluster sound 0x28, cutoff distance 0x44, scale flags 0x48,
/// interior/portal/exterior scale 0x4C/0x50/0x54, interpolation speed 0x58), sound environment palette 0xDC (0x48:
/// name, snde 0x20, cutoff distance 0x28, interpolation speed 0x2C).
/// </remarks>
public static class SoundPlacements
{
	public static readonly TagClass SoundSceneryClass = new( "ssce" );
	public static readonly TagClass SoundEnvironmentClass = new( "snde" );

	public static IReadOnlyList<SoundSceneryPlacement> ReadSoundScenery( TagEntry scenario )
	{
		var cache = scenario.Cache;
		var data = cache.TagData;
		var palette = data.Elements( scenario.Address + 0xE0, 0x28 ).Select( cache.GetTagReference ).ToList();

		var result = new List<SoundSceneryPlacement>();
		var index = 0;
		foreach ( var e in data.Elements( scenario.Address + 0xD8, 0x50 ) )
		{
			var n = index++;
			var p = data.I16( e );
			if ( p < 0 || p >= palette.Count || palette[p] is null || palette[p].Address == 0 )
				continue;

			result.Add( new SoundSceneryPlacement( n, palette[p], data.Vec3( e + 0x8 ), data.Vec3( e + 0x14 ), data.U32( e + 0x4 ), data.I16( e + 0x2 ),
				data.I32( e + 0x34 ), data.F32( e + 0x38 ), data.F32( e + 0x3C ), data.F32( e + 0x40 ), data.F32( e + 0x44 ), data.F32( e + 0x48 ),
				data.F32( e + 0x4C ), GetAttachedLoopingSounds( palette[p] ) ) );
		}

		return result;
	}

	/// <summary>
	/// The looping sounds an object tag attaches.
	/// </summary>
	public static IReadOnlyList<ObjectSoundAttachment> GetAttachedLoopingSounds( TagEntry objectTag )
	{
		var cache = objectTag.Cache;
		var data = cache.TagData;
		var result = new List<ObjectSoundAttachment>();
		foreach ( var a in data.Elements( objectTag.Address + 0x94, 0x18 ) )
		{
			var type = cache.GetTagReference( a );
			if ( type is null || type.Class != LoopingSoundTag.Class || type.Address == 0 )
				continue;

			result.Add( new ObjectSoundAttachment( type, cache.GetString( data.U32( a + 0x8 ) ), cache.GetString( data.U32( a + 0x10 ) ),
				cache.GetString( data.U32( a + 0x14 ) ) ) );
		}

		return result;
	}

	/// <summary>
	/// Every cluster of a structure BSP with a background sound or sound environment.
	/// </summary>
	public static IReadOnlyList<ClusterSound> ReadClusterSounds( TagEntry bsp )
	{
		var cache = bsp.Cache;
		var data = cache.TagData;
		var backgrounds = data.Elements( bsp.Address + 0xD4, 0x64 ).Select( e => (
			Sound: Looping( cache.GetTagReference( e + 0x20 ) ),
			Inside: Looping( cache.GetTagReference( e + 0x28 ) ),
			Cutoff: data.F32( e + 0x44 ),
			ScaleFlags: data.U32( e + 0x48 ),
			Interior: data.F32( e + 0x4C ),
			Portal: data.F32( e + 0x50 ),
			Exterior: data.F32( e + 0x54 )) ).ToList();
		var environments = data.Elements( bsp.Address + 0xDC, 0x48 ).Select( e => cache.GetTagReference( e + 0x20 ) )
			.Select( t => t is not null && t.Class == SoundEnvironmentClass && t.Address != 0 ? t : null ).ToList();

		var result = new List<ClusterSound>();
		var index = 0;
		foreach ( var c in data.Elements( bsp.Address + 0x9C, 0xB0 ) )
		{
			var n = index++;
			var b = data.I16( c + 0x72 );
			var s = data.I16( c + 0x74 );
			var background = b >= 0 && b < backgrounds.Count ? backgrounds[b] : default;
			var environment = s >= 0 && s < environments.Count ? environments[s] : null;
			if ( background.Sound is null && background.Inside is null && environment is null )
				continue;

			var min = new Vector3( data.F32( c + 0x54 ), data.F32( c + 0x5C ), data.F32( c + 0x64 ) );
			var max = new Vector3( data.F32( c + 0x58 ), data.F32( c + 0x60 ), data.F32( c + 0x68 ) );
			result.Add( new ClusterSound( n, background.Sound, background.Inside, background.Cutoff, background.ScaleFlags,
				background.Interior, background.Portal, background.Exterior, environment, min, max ) );
		}

		return result;
	}

	static TagEntry Looping( TagEntry tag ) => tag is not null && tag.Class == LoopingSoundTag.Class && tag.Address != 0 ? tag : null;
}
