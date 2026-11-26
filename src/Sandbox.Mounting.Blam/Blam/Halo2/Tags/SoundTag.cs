using System;
using System.Collections.Generic;
using System.Linq;

namespace Blam.Halo2.Tags;

/// <summary>
/// How a Halo 2 sound's samples are stored (the gestalt's codec, +0x2).
/// </summary>
public enum SoundCompression : byte
{
	PcmBigEndian = 0,
	XboxAdpcm = 1,
	ImaAdpcm = 2,
	PcmLittleEndian = 3,
	Wma = 4,

	/// <summary>
	/// Raw Opus packets (MCC's re-encode of most classic sounds and all dialog).
	/// </summary>
	Opus = 5,
}

/// <summary>
/// The languages a dialog sound has samples for, in the order of the gestalt's per-language blocks.
/// Japanese, Korean and Chinese have chunk lists but MCC ships no sample files for them.
/// </summary>
public enum SoundLanguage
{
	English = 0,
	Japanese = 1,
	German = 2,
	French = 3,
	Spanish = 4,
	Italian = 5,
	Korean = 6,
	Chinese = 7,
}

/// <summary>
/// One run of a permutation's samples in a <c>sounds_*.dat</c> file. A permutation's chunks follow each other and
/// decode as one stream.
/// </summary>
/// <param name="Offset">Offset in the sample file.</param>
/// <param name="Size">Stored bytes.</param>
/// <param name="PcmBytes">Bytes of 16 bit PCM it decodes to (all channels).</param>
public readonly record struct SoundChunk( long Offset, int Size, int PcmBytes );

/// <summary>
/// One variation of a sound in one language.
/// </summary>
/// <param name="Name">Import name (the source file's).</param>
/// <param name="GainDecibels">Gain of this variation, in dB.</param>
/// <param name="Chunks">Where its samples are, for the language the tag was read with.</param>
/// <param name="SkipFraction">Chance it's passed over when picked (encoded u16 / 65535).</param>
public sealed record SoundPermutation( string Name, float GainDecibels, IReadOnlyList<SoundChunk> Chunks, float SkipFraction = 0 )
{
	public int PcmBytes => Chunks.Sum( c => c.PcmBytes );
}

/// <summary>
/// A sound's variations for a range of pitches, recorded at <see cref="NaturalPitch"/>. Pitches are in cents.
/// </summary>
/// <param name="BendMin">Lowest pitch this range plays (bend bounds), or <see cref="short.MinValue"/> without bounds.</param>
/// <param name="BendMax">Highest pitch this range plays, or <see cref="short.MaxValue"/> without bounds.</param>
/// <param name="MaxGainMin">Lowest pitch at which this range plays at full gain (maximum gain pitch bounds), which Halo
/// picks a range by first.</param>
public sealed record SoundPitchRange( string Name, short NaturalPitch, short BendMin, short BendMax, IReadOnlyList<SoundPermutation> Permutations,
	short MaxGainMin = short.MinValue, short MaxGainMax = short.MaxValue )
{
	/// <summary>
	/// Whether <paramref name="pitch"/> is within the maximum gain pitch bounds.
	/// </summary>
	public bool HasFullGainAt( float pitch ) => pitch >= MaxGainMin && pitch <= MaxGainMax;

	/// <summary>
	/// Playback rate that sounds this range's samples at <paramref name="pitch"/> (cents).
	/// </summary>
	public float RateAt( float pitch ) => MathF.Pow( 2, (pitch - NaturalPitch) / 1200f );

	/// <summary>
	/// How far <paramref name="pitch"/> lies outside the bend bounds (0 inside).
	/// </summary>
	public float DistanceTo( float pitch ) => pitch < BendMin ? BendMin - pitch : pitch > BendMax ? pitch - BendMax : 0;
}

/// <summary>
/// The Halo 2 sound gestalt (<c>ugh!</c>): the playback data of every sound tag in a map, which the sound tags
/// only index. Multiplayer maps have a small one of their own beside shared.map's (see <see cref="CacheFile.SoundGestalt"/>).
/// </summary>
/// <remarks>
/// Layout (Assembly's Halo2MCC plugin, checked on shared.map, Lockout and Old Mombasa): codecs 0x0 (3 bytes: sample
/// rate 0 22050 / 1 44100 / 2 32000 / 3 48000, encoding 0 mono / 1 stereo / 3 quad, compression), playback
/// parameters 0x8 (0x38: distances, skip fraction 0x8, gain base and variance 0x10, random pitch 0x18, cone angles 0x1C,
/// outer cone gain 0x24), scales 0x10 (0x14), import names 0x18 (string ids), pitch range parameters 0x20 (0xA:
/// natural pitch, bend bounds, max gain pitch bounds), pitch ranges 0x28 (0xC: name, parameters, 2 encoded indices,
/// first permutation, count), permutations 0x30 (0x10: name, encoded skip fraction, gain dB (s8), info index,
/// language neutral time, per-language block 0x8 of {PCM bytes (English's in every language), first chunk, count}),
/// per-language chunk lists 0x60 (each a block of 0x10: offset, size | flags &lt;&lt; 24, PCM bytes, runtime).
/// </remarks>
public sealed class SoundGestaltTag
{
	public static readonly TagClass Class = new( "ugh!" );

	public TagEntry Tag { get; }

	public SoundGestaltTag( TagEntry tag ) => Tag = tag;

	public (int SampleRate, int Channels, SoundCompression Compression) GetCodec( int index )
	{
		var data = Tag.Cache.TagData;
		var address = Element( 0x0, 3, index );
		var rate = data.U8( address ) switch { 0 => 22050, 1 => 44100, 2 => 32000, _ => 48000 };
		var channels = data.U8( address + 1 ) switch { 0 => 1, 3 => 4, _ => 2 };
		return (rate, channels, (SoundCompression)data.U8( address + 2 ));
	}

	public SoundPlayback GetPlayback( int index )
	{
		if ( index < 0 || index >= Tag.Cache.TagData.Count( Tag.Address + 0x8 ) )
			return new SoundPlayback( 0, 0, 0, 0, 0, 0 );

		var data = Tag.Cache.TagData;
		var a = Element( 0x8, 0x38, index );
		return new SoundPlayback( data.F32( a ), data.F32( a + 4 ), data.F32( a + 0x10 ), data.F32( a + 0x14 ), data.I16( a + 0x18 ), data.I16( a + 0x1A ),
			data.F32( a + 0x8 ), data.F32( a + 0x1C ), data.F32( a + 0x20 ), data.F32( a + 0x24 ) );
	}

	/// <summary>
	/// A sound's scale modifiers (scales block +0x10, 0x14 each), or none.
	/// </summary>
	public SoundScale GetScale( int index )
	{
		if ( index < 0 || index >= Tag.Cache.TagData.Count( Tag.Address + 0x10 ) )
			return SoundScale.None;

		var data = Tag.Cache.TagData;
		var a = Element( 0x10, 0x14, index );
		return new SoundScale( data.F32( a ), data.F32( a + 4 ), data.I16( a + 8 ), data.I16( a + 0xA ), data.F32( a + 0xC ), data.F32( a + 0x10 ) );
	}

	public string GetImportName( int index )
	{
		if ( index < 0 || index >= Tag.Cache.TagData.Count( Tag.Address + 0x18 ) )
			return "";

		return Tag.Cache.GetString( Tag.Cache.TagData.U32( Element( 0x18, 4, index ) ) ) ?? "";
	}

	public IReadOnlyList<SoundPitchRange> GetPitchRanges( int first, int count, SoundLanguage language )
	{
		var data = Tag.Cache.TagData;
		var ranges = new List<SoundPitchRange>();
		for ( var r = first; r < first + count; r++ )
		{
			var a = Element( 0x28, 0xC, r );
			var parameters = data.I16( a + 2 );
			short natural = 0, bendMin = short.MinValue, bendMax = short.MaxValue, maxGainMin = short.MinValue, maxGainMax = short.MaxValue;
			if ( parameters >= 0 && parameters < data.Count( Tag.Address + 0x20 ) )
			{
				var p = Element( 0x20, 0xA, parameters );
				(natural, bendMin, bendMax, maxGainMin, maxGainMax) = (data.I16( p ), data.I16( p + 2 ), data.I16( p + 4 ), data.I16( p + 6 ), data.I16( p + 8 ));
			}

			var permutations = new List<SoundPermutation>();
			var firstPermutation = data.I16( a + 8 );
			var permutationCount = data.I16( a + 0xA );
			for ( var p = firstPermutation; p < firstPermutation + permutationCount; p++ )
				permutations.Add( GetPermutation( p, language ) );

			ranges.Add( new SoundPitchRange( GetImportName( data.I16( a ) ), natural, bendMin, bendMax, permutations, maxGainMin, maxGainMax ) );
		}

		return ranges;
	}

	SoundPermutation GetPermutation( int index, SoundLanguage language )
	{
		var data = Tag.Cache.TagData;
		var a = Element( 0x30, 0x10, index );
		var name = GetImportName( data.I16( a ) );
		var gain = (sbyte)data.U8( a + 4 );

		// The per-language block's PCM bytes are English's in every language; the chunks have their own.
		var chunks = new List<SoundChunk>();
		var languages = data.Elements( a + 8, 8 ).ToList();
		if ( (int)language < languages.Count )
		{
			var info = languages[(int)language];
			var first = data.I16( info + 4 );
			var count = data.I16( info + 6 );
			var listAddress = Element( 0x60, 8, (int)language );
			for ( var c = first; c >= 0 && c < first + count && c < data.Count( listAddress ); c++ )
			{
				var chunk = data.U32( listAddress + 4 ) + (uint)(c * 0x10);
				chunks.Add( new SoundChunk( data.U32( chunk ), (int)(data.U32( chunk + 4 ) & 0xFFFFFF), data.I32( chunk + 8 ) ) );
			}
		}

		return new SoundPermutation( name, gain, chunks, data.U16( a + 2 ) / 65535f );
	}

	uint Element( uint block, int size, int index ) => Tag.Cache.TagData.U32( Tag.Address + block + 4 ) + (uint)(index * size);
}

/// <summary>
/// A sound tag's playback parameters (the gestalt's, +0x8).
/// </summary>
/// <param name="MinimumDistance">Distance (world units) within which it no longer gets louder; 0 means the sound class's.</param>
/// <param name="MaximumDistance">Distance beyond which it can't be heard; 0 means the sound class's.</param>
/// <param name="GainBase">Gain in dB.</param>
/// <param name="GainVariance">Random gain added to <see cref="GainBase"/> (dB): Halo plays base + r * variance, r in [0, 1).</param>
/// <param name="RandomPitchMin">Random pitch bounds in cents (looping sound tracks play at the lower bound).</param>
/// <param name="SkipFraction">Chance the whole sound isn't played (times the scale's skip modifier).</param>
/// <param name="InnerConeAngle">A directional sound plays at full gain within this angle of its direction (radians).</param>
/// <param name="OuterConeAngle">Outside this angle it plays at <paramref name="OuterConeGain"/>; 0 means it isn't directional.</param>
public sealed record SoundPlayback( float MinimumDistance, float MaximumDistance, float GainBase, float GainVariance, short RandomPitchMin, short RandomPitchMax,
	float SkipFraction = 0, float InnerConeAngle = 0, float OuterConeAngle = 0, float OuterConeGain = 0 );

/// <summary>
/// How a sound changes with the scale it's played at (0..1, from an object function, a looping sound's scale...):
/// modifiers at scale 0 and at scale 1 (gestalt scales, +0x10).
/// </summary>
/// <param name="GainMin">dB added at scale 0 (to the sound class's gain at scale 0).</param>
/// <param name="GainMax">dB added at scale 1.</param>
/// <param name="PitchMin">Cents added at scale 0.</param>
/// <param name="SkipMin">Multiplier of the skip fraction at scale 0.</param>
public sealed record SoundScale( float GainMin, float GainMax, short PitchMin, short PitchMax, float SkipMin, float SkipMax )
{
	public static readonly SoundScale None = new( 0, 0, 0, 0, 1, 1 );

	/// <summary>
	/// The pitch modifier at a scale, in cents, as Halo 2 computes it.
	/// </summary>
	public float PitchAt( float scale ) => PitchMin + (PitchMax - PitchMin) * scale;
}

/// <summary>
/// A Halo 2 sound tag (<c>snd!</c>). Its data is mostly indices into the map's <see cref="SoundGestaltTag"/>,
/// resolved here for one language.
/// </summary>
/// <remarks>
/// Layout (0x1C, Assembly's Halo2MCC plugin; Reclaimer's offsets are the Xbox layout): flags 0x0 (bit 10 play only
/// in legacy mode, bit 11 only in remastered mode), sound class 0x2, codec 0x3, remastered twin 0x4, playback
/// parameters 0x6, first pitch range 0x8, pitch range count 0xA, scale 0xB, promotion 0xC, custom playback 0xD,
/// extra info 0xE, maximum play time 0x10, inner silence distance 0x14, reflections 0x18, low pass 0x1B.
/// </remarks>
public sealed class SoundTag
{
	public static readonly TagClass Class = new( "snd!" );

	const ushort LegacyOnlyFlag = 1 << 10;
	const ushort RemasteredOnlyFlag = 1 << 11;

	public TagEntry Tag { get; }
	public ushort Flags { get; }
	public int SoundClass { get; }
	public int SampleRate { get; }
	public int Channels { get; }
	public SoundCompression Compression { get; }

	// The rate the samples play at: Opus is always 48 kHz, whatever the codec entry says.
	public int PlaybackSampleRate => Compression == SoundCompression.Opus ? 48000 : SampleRate;

	public SoundPlayback Playback { get; }
	public SoundScale Scale { get; }
	public IReadOnlyList<SoundPitchRange> PitchRanges { get; }

	/// <summary>
	/// Flags 0x1000 / 0x2000: the minimum / maximum distance is the sound class's (the playback parameters then hold 0).
	/// </summary>
	public bool UsesClassMinimumDistance => (Flags & 0x1000) != 0;
	public bool UsesClassMaximumDistance => (Flags & 0x2000) != 0;

	/// <summary>
	/// Minimum and maximum distance (world units) as Halo 2 picks them: the playback
	/// parameters', or the sound class's where the flags say so; the minimum is at least 0.001.
	/// </summary>
	public (float Minimum, float Maximum) GetDistances( SoundClass soundClass )
	{
		var minimum = UsesClassMinimumDistance ? soundClass?.DistanceMin ?? 0 : Playback.MinimumDistance;
		var maximum = UsesClassMaximumDistance ? soundClass?.DistanceMax ?? 0 : Playback.MaximumDistance;
		return (MathF.Max( minimum, 0.001f ), maximum);
	}

	/// <summary>
	/// The gain (dB) the sound class and the sound's scale add at <paramref name="scale"/>, as Halo 2 computes it: the
	/// class's gain bounds are its gain at scale 0 and at scale 1, each plus the scale's gain modifier, interpolated in
	/// linear amplitude.
	/// </summary>
	public float ClassGainAt( SoundClass soundClass, float scale )
	{
		var at0 = (soundClass?.GainMin ?? 0) + Scale.GainMin;
		var at1 = (soundClass?.GainMax ?? 0) + Scale.GainMax;
		var amplitude = Amplitude( at0 ) + (Amplitude( at1 ) - Amplitude( at0 )) * Math.Clamp( scale, 0, 1 );
		return amplitude > 0 ? Math.Clamp( 20 * MathF.Log10( amplitude ), -64, 0 ) : -64;
	}

	/// <summary>
	/// The pitch range Halo 2 plays at a pitch: the first whose maximum gain pitch bounds hold it,
	/// else the one whose bend bounds come nearest. Only ranges <paramref name="usable"/> allows count; -1 if none.
	/// </summary>
	public int ChoosePitchRange( float pitch, Func<int, bool> usable = null )
	{
		for ( var r = 0; r < PitchRanges.Count; r++ )
		{
			if ( (usable is null || usable( r )) && PitchRanges[r].HasFullGainAt( pitch ) )
				return r;
		}

		var best = -1;
		for ( var r = 0; r < PitchRanges.Count; r++ )
		{
			if ( (usable is null || usable( r )) && (best < 0 || PitchRanges[r].DistanceTo( pitch ) < PitchRanges[best].DistanceTo( pitch )) )
				best = r;
		}

		return best;
	}

	static float Amplitude( float decibels ) => MathF.Pow( 10, Math.Clamp( decibels, -64, 0 ) / 20 );

	/// <summary>
	/// Only played with MCC's remastered audio; the classic sounds are the rest.
	/// </summary>
	public bool IsRemasteredOnly => (Flags & RemasteredOnlyFlag) != 0;

	public SoundTag( TagEntry tag, SoundLanguage language = SoundLanguage.English )
	{
		Tag = tag;
		var data = tag.Cache.TagData;
		var a = tag.Address;
		Flags = data.U16( a );
		SoundClass = data.U8( a + 2 );

		var gestalt = new SoundGestaltTag( tag.Cache.SoundGestalt ?? throw new InvalidOperationException( $"{tag.Cache.Name} has no sound gestalt" ) );
		(SampleRate, Channels, Compression) = gestalt.GetCodec( data.U8( a + 3 ) );
		Playback = gestalt.GetPlayback( data.I16( a + 6 ) );
		Scale = gestalt.GetScale( data.U8( a + 0xB ) );
		PitchRanges = gestalt.GetPitchRanges( data.I16( a + 8 ), data.U8( a + 0xA ), language );
	}
}

/// <summary>
/// One of Halo 2's sound classes (weapon_fire, unit_dialog, ambient_nature...), which a sound tag names by index.
/// </summary>
/// <param name="DistanceMin">Default minimum distance (world units) of the class's sounds.</param>
/// <param name="DistanceMax">Default maximum distance.</param>
/// <param name="Multilingual">Its sounds are dialog, with samples per language in <c>sounds_&lt;language&gt;.dat</c>.</param>
public sealed record SoundClass( float DistanceMin, float DistanceMax, float GainMin, float GainMax, bool Multilingual );

/// <summary>
/// The sound classes (<c>sncl</c>) of classic Halo 2, which the globals reference at +0x2DC (+0x2D4 is the remastered
/// audio's). Each class is 0x78: internal flags 0x8 (bit 7 multilingual), flags 0xA, distance bounds 0x18, gain bounds 0x20.
/// </summary>
public sealed class SoundClassesTag
{
	public static readonly TagClass Class = new( "sncl" );

	const uint ClassicReference = 0x2DC;
	const ushort MultilingualFlag = 1 << 7;

	public IReadOnlyList<SoundClass> Classes { get; }

	SoundClassesTag( TagEntry tag )
	{
		var data = tag.Cache.TagData;
		Classes = data.Elements( tag.Address, 0x78 )
			.Select( e => new SoundClass( data.F32( e + 0x18 ), data.F32( e + 0x1C ), data.F32( e + 0x20 ), data.F32( e + 0x24 ),
				(data.U16( e + 0x8 ) & MultilingualFlag) != 0 ) )
			.ToList();
	}

	/// <summary>
	/// The classic sound classes a map's globals reference, or null.
	/// </summary>
	public static SoundClassesTag ForCache( CacheFile cache )
	{
		var globals = cache.Globals;
		var tag = globals is null ? null : globals.Cache.GetTagReference( globals.Address + ClassicReference );
		return tag is not null && tag.Class == Class ? new SoundClassesTag( tag ) : null;
	}

	public SoundClass Get( int index ) => index >= 0 && index < Classes.Count ? Classes[index] : null;
}
