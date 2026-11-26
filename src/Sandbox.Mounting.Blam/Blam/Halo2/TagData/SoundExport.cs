using System;
using System.IO;
using System.Text.Json;
using Blam.Halo2.Tags;

namespace Blam.Halo2.TagData;

// Halo 2 sound (snd!) and looping sound (lsnd) tags as JSON, written from the readers the mount plays them with.
// A sound tag mostly indexes its map's sound gestalt, so its document holds what SoundTag resolves, not the tag's own fields.
// Units are as stored: gains in dB, pitches in cents, distances in world units; angles are in degrees, as in the other tag documents.
public static class SoundExport
{
	// The sound classes by index, as a sound tag names them.
	public static readonly string[] SoundClasses =
	[
		"projectile impact", "projectile detonation", "projectile flyby", "unused", "weapon fire", "weapon ready", "weapon reload",
		"weapon empty", "weapon charge", "weapon overheat", "weapon idle", "weapon melee", "weapon animation", "object impacts",
		"particle impacts", "weapon fire lod", "weapon fire lod far", "lfe", "unit footsteps", "unit dialog", "unit animation", "unused",
		"vehicle collision", "vehicle engine", "vehicle animation", "unused", "device door", "unused", "device machinery",
		"device stationary", "unused", "unused", "music", "ambient nature", "ambient machinery", "unused", "huge ass", "object looping",
		"cinematic music", "reflection", "reflection lod", "reflection lod far", "unused", "unused", "unused", "cortana mission",
		"cortana cinematic", "mission dialog", "cinematic dialog", "scripted cinematic foley", "game event", "ui", "test",
		"multilingual test",
	];

	// Bits 12 and 13 make the minimum and maximum distance the sound class's.
	static readonly string[] SoundFlags =
	[
		"fit to adpcm blocksize", "split long sound into permutations", "always spatialize", "never obstruct", "internal don't touch",
		"use huge sound transmission", "link count to owner unit", "pitch range is language", "don't use sound class speaker flag",
		"don't use lipsync data", "play only in legacy mode", "play only in remastered mode", "class minimum distance",
		"class maximum distance",
	];

	static readonly string[] Compressions = ["none (big endian)", "xbox adpcm", "ima adpcm", "none (little endian)", "wma", "opus"];

	static readonly string[] LoopingSoundFlags =
	[
		"deafening to ais", "not a loop", "stops music", "always spatialize", "synchronize playback", "synchronize tracks",
		"fake spatialization with distance", "combine all 3d playback", "legacy only", "remastered only",
	];

	static readonly string[] TrackFlags = ["fade in at start", "fade out at stop", "crossfade alternate loop", "master surround sound track", "fade out at alternate stop"];

	static readonly string[] DetailFlags = ["don't play with alternate", "don't play without alternate", "start immediately with loop"];

	public static bool IsSound( TagEntry tag ) => tag.Class == SoundTag.Class || tag.Class == LoopingSoundTag.Class;

	public static byte[] Tag( TagEntry tag ) => tag.Class == SoundTag.Class ? Sound( tag ) : LoopingSound( tag );

	// { Class, Flags, SampleRate, Channels, Compression, MinimumDistance, MaximumDistance (the playback parameters' or the class's,
	//   as Halo picks them), SkipFraction, GainBase, GainVariance, RandomPitchBounds, InnerConeAngle, OuterConeAngle, OuterConeGain,
	//   GainModifier, PitchModifier, SkipFractionModifier (each [at scale 0, at scale 1]), ClassGainBounds, ClassDistanceBounds,
	//   Multilingual, PitchRanges: [{ Name, NaturalPitch, BendBounds, MaximumGainPitchBounds, PermutationCount,
	//   Permutations: [{ Name, Gain, SkipFraction }] }] }.
	static byte[] Sound( TagEntry tag ) => Write( json =>
	{
		var sound = new SoundTag( tag );
		var soundClass = SoundClassesTag.ForCache( tag.Cache )?.Get( sound.SoundClass );
		var playback = sound.Playback;
		var scale = sound.Scale;
		var (minimumDistance, maximumDistance) = sound.GetDistances( soundClass );

		json.WriteStartObject();
		json.WriteString( "Class", Name( SoundClasses, sound.SoundClass ) );
		WriteFlags( json, "Flags", sound.Flags, SoundFlags );
		json.WriteNumber( "SampleRate", sound.PlaybackSampleRate );
		json.WriteNumber( "Channels", sound.Channels );
		json.WriteString( "Compression", Name( Compressions, (int)sound.Compression ) );
		Number( json, "MinimumDistance", minimumDistance );
		Number( json, "MaximumDistance", maximumDistance );
		Number( json, "SkipFraction", playback.SkipFraction );
		Number( json, "GainBase", playback.GainBase );
		Number( json, "GainVariance", playback.GainVariance );
		WritePair( json, "RandomPitchBounds", playback.RandomPitchMin, playback.RandomPitchMax );
		Number( json, "InnerConeAngle", LayoutJsonWriter.Degrees( playback.InnerConeAngle ) );
		Number( json, "OuterConeAngle", LayoutJsonWriter.Degrees( playback.OuterConeAngle ) );
		Number( json, "OuterConeGain", playback.OuterConeGain );
		WritePair( json, "GainModifier", scale.GainMin, scale.GainMax );
		WritePair( json, "PitchModifier", scale.PitchMin, scale.PitchMax );
		WritePair( json, "SkipFractionModifier", scale.SkipMin, scale.SkipMax );
		WritePair( json, "ClassGainBounds", soundClass?.GainMin ?? 0, soundClass?.GainMax ?? 0 );
		WritePair( json, "ClassDistanceBounds", soundClass?.DistanceMin ?? 0, soundClass?.DistanceMax ?? 0 );
		json.WriteBoolean( "Multilingual", soundClass?.Multilingual ?? false );

		json.WriteStartArray( "PitchRanges" );
		foreach ( var range in sound.PitchRanges )
		{
			json.WriteStartObject();
			json.WriteString( "Name", range.Name );
			json.WriteNumber( "NaturalPitch", range.NaturalPitch );
			WritePair( json, "BendBounds", range.BendMin, range.BendMax );
			WritePair( json, "MaximumGainPitchBounds", range.MaxGainMin, range.MaxGainMax );
			json.WriteNumber( "PermutationCount", range.Permutations.Count );
			json.WriteStartArray( "Permutations" );
			foreach ( var permutation in range.Permutations )
			{
				json.WriteStartObject();
				json.WriteString( "Name", permutation.Name );
				Number( json, "Gain", permutation.GainDecibels );
				Number( json, "SkipFraction", permutation.SkipFraction );
				json.WriteEndObject();
			}
			json.WriteEndArray();
			json.WriteEndObject();
		}
		json.WriteEndArray();
		json.WriteEndObject();
	} );

	// { Flags, Remastered, DistanceBounds, Tracks: [{ Name, Flags, Gain, FadeInDuration, FadeOutDuration, Start, Loop, End, AlternateLoop,
	//   AlternateEnd }], DetailSounds: [{ Name, Sound, RandomPeriodBounds, Gain, Flags, YawBounds, PitchBounds, DistanceBounds }] }.
	// Tracks and detail sounds are numbered as the mount's <lsnd>__track<n>.sound and __detail<n>.sound; details without a sound are left out.
	static byte[] LoopingSound( TagEntry tag ) => Write( json =>
	{
		var lsnd = new LoopingSoundTag( tag );
		json.WriteStartObject();
		WriteFlags( json, "Flags", lsnd.Flags, LoopingSoundFlags );
		json.WriteString( "Remastered", TagJsonWriter.TagName( lsnd.Remastered ) );
		WritePair( json, "DistanceBounds", lsnd.DistanceMin, lsnd.DistanceMax );

		json.WriteStartArray( "Tracks" );
		foreach ( var track in lsnd.Tracks )
		{
			json.WriteStartObject();
			json.WriteString( "Name", track.Name );
			WriteFlags( json, "Flags", track.Flags, TrackFlags );
			Number( json, "Gain", track.GainDecibels );
			Number( json, "FadeInDuration", track.FadeInDuration );
			Number( json, "FadeOutDuration", track.FadeOutDuration );
			json.WriteString( "Start", TagJsonWriter.TagName( track.Start ) );
			json.WriteString( "Loop", TagJsonWriter.TagName( track.Loop ) );
			json.WriteString( "End", TagJsonWriter.TagName( track.End ) );
			json.WriteString( "AlternateLoop", TagJsonWriter.TagName( track.AlternateLoop ) );
			json.WriteString( "AlternateEnd", TagJsonWriter.TagName( track.AlternateEnd ) );
			json.WriteEndObject();
		}
		json.WriteEndArray();

		json.WriteStartArray( "DetailSounds" );
		foreach ( var detail in lsnd.DetailSounds )
		{
			json.WriteStartObject();
			json.WriteString( "Name", detail.Name );
			json.WriteString( "Sound", TagJsonWriter.TagName( detail.Sound ) );
			WritePair( json, "RandomPeriodBounds", detail.PeriodMin, detail.PeriodMax );
			Number( json, "Gain", detail.GainDecibels );
			WriteFlags( json, "Flags", detail.Flags, DetailFlags );
			WritePair( json, "YawBounds", LayoutJsonWriter.Degrees( detail.YawMin ), LayoutJsonWriter.Degrees( detail.YawMax ) );
			WritePair( json, "PitchBounds", LayoutJsonWriter.Degrees( detail.PitchMin ), LayoutJsonWriter.Degrees( detail.PitchMax ) );
			WritePair( json, "DistanceBounds", detail.DistanceMin, detail.DistanceMax );
			json.WriteEndObject();
		}
		json.WriteEndArray();
		json.WriteEndObject();
	} );

	static string Name( string[] names, int index ) => index >= 0 && index < names.Length ? names[index] : $"option {index}";

	static void WriteFlags( Utf8JsonWriter json, string property, uint bits, string[] names )
	{
		json.WriteStartArray( property );
		for ( var bit = 0; bit < 32; bit++ )
		{
			if ( (bits & (1u << bit)) != 0 )
				json.WriteStringValue( bit < names.Length ? names[bit] : $"bit {bit}" );
		}
		json.WriteEndArray();
	}

	static void Number( Utf8JsonWriter json, string property, float value )
	{
		json.WritePropertyName( property );
		LayoutJsonWriter.WriteFloat( json, value );
	}

	static void WritePair( Utf8JsonWriter json, string property, float a, float b )
	{
		json.WriteStartArray( property );
		LayoutJsonWriter.WriteFloat( json, a );
		LayoutJsonWriter.WriteFloat( json, b );
		json.WriteEndArray();
	}

	static byte[] Write( Action<Utf8JsonWriter> write )
	{
		using var stream = new MemoryStream();
		using ( var json = new Utf8JsonWriter( stream, LayoutJsonWriter.Indented ) )
			write( json );

		return stream.ToArray();
	}
}
