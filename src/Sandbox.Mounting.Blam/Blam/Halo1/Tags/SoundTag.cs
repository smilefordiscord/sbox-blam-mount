using System.Collections.Generic;

namespace Blam.Halo1.Tags;

/// <summary>
/// Tag group codes of the sound tags. Kept here rather than in <see cref="TagClasses"/> so the
/// sound support stays self-contained.
/// </summary>
public static class SoundTagClasses
{
	public static readonly TagClass Sound = new( "snd!" );
	public static readonly TagClass LoopingSound = new( "lsnd" );
	public static readonly TagClass SoundScenery = new( "ssce" );
}

public enum SoundCompression : ushort
{
	None = 0,
	XboxAdpcm = 1,
	ImaAdpcm = 2,
	Ogg = 3,
}

/// <summary>
/// One piece of sample data in a sound tag.
/// </summary>
/// <param name="SkipFraction">Chance of passing this permutation over when it's picked at random.</param>
/// <param name="NextPermutation">The piece that follows this one when a long sound was split up, or -1.</param>
public sealed record SoundPermutation( int Index, string Name, float SkipFraction, float Gain, SoundCompression Compression, int NextPermutation, int SampleSize );

/// <summary>
/// A set of samples for one pitch range of a sound.
/// </summary>
/// <param name="NaturalPitch">The pitch the samples play at unchanged.</param>
/// <param name="BendMin">Lowest pitch this range is picked for.</param>
/// <param name="BendMax">Highest pitch this range is picked for.</param>
/// <param name="ActualPermutationCount">How many of the permutations are variations to pick from.
/// The rest are continuations reached through <see cref="SoundPermutation.NextPermutation"/>.</param>
/// <param name="PlaybackRate">How fast the samples play at pitch 1; filled in when the map was
/// built (1 / natural pitch).</param>
public sealed record SoundPitchRange( string Name, float NaturalPitch, float BendMin, float BendMax, int ActualPermutationCount, float PlaybackRate, IReadOnlyList<SoundPermutation> Permutations );

/// <summary>
/// Reader for <c>snd!</c> tags.
/// </summary>
/// <remarks>
/// Offsets: flags 0x0 (bit 1: long sound split into permutations), class 0x4, sample rate 0x6
/// (0: 22 kHz, 1: 44 kHz), minimum/maximum distance 0x8/0xC (world units), skip fraction 0x10,
/// random pitch bounds 0x14 (lower, upper), gain modifier 0x28, pitch modifier at scale 0/1
/// 0x44/0x5C, encoding 0x6C (0: mono, 1: stereo), compression 0x6E, longest permutation length
/// 0x84 (milliseconds, filled in at map build), pitch ranges block 0x98 (0x48 bytes each: name,
/// natural pitch 0x20, bend bounds 0x24, actual permutation count 0x2C, playback rate 0x30 (map
/// build), permutations block 0x3C). Permutations are 0x7C bytes: name, skip fraction 0x20,
/// gain 0x24, compression 0x28, next permutation index 0x2A, samples data reference 0x40.
///
/// In MCC the samples data reference still points into the PC <c>sounds.map</c>, which MCC doesn't
/// ship. The audio lives in FMOD banks instead (see <see cref="SoundBankIndex"/>).
/// </remarks>
public sealed class SoundTag
{
	public uint Flags { get; }
	public int SoundClass { get; }
	public int SampleRate { get; }
	public bool Stereo { get; }
	public SoundCompression Compression { get; }
	public float MinimumDistance { get; }
	public float MaximumDistance { get; }
	public float SkipFraction { get; }
	public float RandomPitchMin { get; }
	public float RandomPitchMax { get; }
	public float GainModifier { get; }

	/// <summary>
	/// Pitch multipliers at scale 0 and 1. Sounds played without a scale (ambience) use the first.
	/// </summary>
	public float ZeroPitchModifier { get; }
	public float OnePitchModifier { get; }

	/// <summary>
	/// Gain multiplier at scale 1 (0 when unset, which plays unchanged).
	/// </summary>
	public float OneGainModifier { get; }

	// Raw multipliers at scale 0 and 1, which the game scales an impulse sound's gain and skip fraction by.
	public float ZeroGainModifier { get; }
	public float ZeroSkipFractionModifier { get; }
	public float OneSkipFractionModifier { get; }

	// Played instead once more than PromotionCount copies have played back to back, or null.
	public TagEntry PromotionSound { get; }
	public int PromotionCount { get; }

	/// <summary>
	/// Length of the longest permutation in milliseconds.
	/// </summary>
	public int LongestPermutationLength { get; }
	public IReadOnlyList<SoundPitchRange> PitchRanges { get; }

	public SoundTag( CacheFile cache, TagEntry tag )
	{
		var data = cache.TagData;
		var a = tag.Address;

		Flags = data.U32( a );
		SoundClass = data.U16( a + 0x4 );
		SampleRate = data.U16( a + 0x6 ) == 1 ? 44100 : 22050;
		MinimumDistance = data.F32( a + 0x8 );
		MaximumDistance = data.F32( a + 0xC );
		SkipFraction = data.F32( a + 0x10 );
		RandomPitchMin = data.F32( a + 0x14 );
		RandomPitchMax = data.F32( a + 0x18 );
		GainModifier = data.F32( a + 0x28 );
		ZeroSkipFractionModifier = data.F32( a + 0x3C );
		ZeroGainModifier = data.F32( a + 0x40 );
		ZeroPitchModifier = data.F32( a + 0x44 );
		OneSkipFractionModifier = data.F32( a + 0x54 );
		OneGainModifier = data.F32( a + 0x58 );
		OnePitchModifier = data.F32( a + 0x5C );
		PromotionSound = cache.GetTagReference( data, a + 0x70 );
		PromotionCount = data.I16( a + 0x80 );
		LongestPermutationLength = data.I32( a + 0x84 );
		Stereo = data.U16( a + 0x6C ) == 1;
		Compression = (SoundCompression)data.U16( a + 0x6E );

		var pitchRanges = new List<SoundPitchRange>();
		foreach ( var pr in data.Block( a + 0x98, 0x48 ) )
		{
			var permutations = new List<SoundPermutation>();
			foreach ( var p in data.Block( pr + 0x3C, 0x7C ) )
			{
				permutations.Add( new SoundPermutation(
					permutations.Count,
					data.CString( p, 32 ),
					data.F32( p + 0x20 ),
					data.F32( p + 0x24 ),
					(SoundCompression)data.U16( p + 0x28 ),
					data.I16( p + 0x2A ),
					data.I32( p + 0x40 ) ) );
			}

			pitchRanges.Add( new SoundPitchRange(
				data.CString( pr, 32 ),
				data.F32( pr + 0x20 ),
				data.F32( pr + 0x24 ),
				data.F32( pr + 0x28 ),
				data.I16( pr + 0x2C ),
				data.F32( pr + 0x30 ),
				permutations ) );
		}

		PitchRanges = pitchRanges;
	}

	/// <summary>
	/// How Halo plays the sound without a scale (ambience, detail sounds, anything not driven by an
	/// object function): at a random pitch between the bounds times the scale 0 pitch modifier,
	/// from the pitch range picked for that pitch, at the range's playback rate.
	/// </summary>
	/// <param name="withModifier">Whether the rates include the scale 0 pitch modifier. A game that
	/// applies the scale's pitch modifier itself leaves it out (the range is still picked with it).</param>
	/// <returns>The playback rate bounds (pitch times the range's playback rate) and the pitch
	/// range, picked for the middle of the bounds; -1 if the sound has no samples.</returns>
	public (float MinRate, float MaxRate, int PitchRange) GetUnscaledPlayback( bool withModifier = true )
	{
		// Unset values (never seen in MCC's maps, but cheap to guard against) play unchanged.
		var modifier = ZeroPitchModifier > 0 ? ZeroPitchModifier : 1;
		var min = RandomPitchMin > 0 ? RandomPitchMin : 1;
		var max = RandomPitchMax > 0 ? RandomPitchMax : min;

		var pitchRange = FindPitchRange( (min + max) * 0.5f * modifier );
		if ( pitchRange < 0 )
			return (1, 1, -1);

		var range = PitchRanges[pitchRange];
		var rate = range.PlaybackRate > 0 ? range.PlaybackRate : range.NaturalPitch > 0 ? 1 / range.NaturalPitch : 1;
		if ( withModifier )
			rate *= modifier;

		return (min * rate, max * rate, pitchRange);
	}

	/// <summary>
	/// The playback rate bounds of one pitch range: the random pitch bounds times the range's
	/// playback rate (without the scale's pitch modifier).
	/// </summary>
	public (float MinRate, float MaxRate) GetRangeRates( int pitchRange )
	{
		var range = PitchRanges[pitchRange];
		var rate = range.PlaybackRate > 0 ? range.PlaybackRate : range.NaturalPitch > 0 ? 1 / range.NaturalPitch : 1;
		var min = RandomPitchMin > 0 ? RandomPitchMin : 1;
		var max = RandomPitchMax > 0 ? RandomPitchMax : min;
		return (min * rate, max * rate);
	}

	/// <summary>
	/// The pitch range Halo plays at a pitch: the first whose bend bounds contain it, else the one
	/// whose bounds are closest to it (as a ratio). Returns -1 if no range has samples.
	/// </summary>
	public int FindPitchRange( float pitch )
	{
		var result = -1;
		var closest = float.MaxValue;
		for ( var i = 0; i < PitchRanges.Count; i++ )
		{
			var range = PitchRanges[i];
			if ( range.Permutations.Count == 0 )
				continue;

			if ( range.BendMin <= pitch && pitch <= range.BendMax )
				return i;

			var ratio = range.BendMax < pitch ? pitch / range.BendMax : range.BendMin / pitch;
			if ( ratio < closest )
			{
				closest = ratio;
				result = i;
			}
		}

		return result;
	}

	/// <summary>
	/// Which of the tag's samples in an FMOD bank are the variations of one pitch range, or null if
	/// the bank's samples can't be matched up with the tag's permutations.
	/// </summary>
	/// <remarks>
	/// A bank holds a tag's permutations in order, pitch range by pitch range: either every
	/// permutation or only the variations. With every permutation, the variations of a long sound
	/// split into chained permutations are only their first pieces (see <see cref="GetBankChain"/>).
	/// </remarks>
	public (int First, int Count)? GetBankSamples( int pitchRange, int bankSampleCount )
	{
		if ( pitchRange < 0 || pitchRange >= PitchRanges.Count )
			return null;

		var variations = 0;
		foreach ( var range in PitchRanges )
			variations += range.ActualPermutationCount;

		int first = 0;
		if ( bankSampleCount == PermutationCount )
		{
			for ( var i = 0; i < pitchRange; i++ )
				first += PitchRanges[i].Permutations.Count;
		}
		else if ( bankSampleCount == variations )
		{
			for ( var i = 0; i < pitchRange; i++ )
				first += PitchRanges[i].ActualPermutationCount;
		}
		else
		{
			return null;
		}

		var count = PitchRanges[pitchRange].ActualPermutationCount;
		return count > 0 ? (first, count) : null;
	}

	/// <summary>
	/// The bank samples that make up the permutation in bank sample <paramref name="bankIndex"/>,
	/// in playing order: just that sample, unless the tag splits long sounds into permutations
	/// and the bank holds every permutation. Halo plays such a variation as its first permutation
	/// followed by each next permutation in turn, back to back.
	/// </summary>
	public IReadOnlyList<int> GetBankChain( int bankIndex, int bankSampleCount )
	{
		if ( bankSampleCount != PermutationCount )
			return [bankIndex];

		// The pitch range the sample belongs to, and where its permutations start in the bank.
		var first = 0;
		foreach ( var range in PitchRanges )
		{
			var count = range.Permutations.Count;
			if ( bankIndex < first + count )
			{
				var chain = new List<int>();
				var visited = new HashSet<int>();
				for ( var p = bankIndex - first; p >= 0 && p < count && visited.Add( p ); p = range.Permutations[p].NextPermutation )
					chain.Add( first + p );

				return chain;
			}

			first += count;
		}

		return [bankIndex];
	}

	/// <summary>
	/// The gain of the permutation in bank sample <paramref name="bankIndex"/> (see
	/// <see cref="GetBankSamples"/> for how the bank lines up with the tag), or 1 if it doesn't.
	/// </summary>
	public float GetBankGain( int bankIndex, int bankSampleCount )
	{
		var allPermutations = bankSampleCount == PermutationCount;
		var first = 0;
		foreach ( var range in PitchRanges )
		{
			var count = allPermutations ? range.Permutations.Count : range.ActualPermutationCount;
			if ( bankIndex < first + count )
				return bankIndex - first < range.Permutations.Count ? range.Permutations[bankIndex - first].Gain : 1;

			first += count;
		}

		return 1;
	}

	/// <summary>
	/// Halo's minimum and maximum distance (world units) for each sound class, used by sounds that
	/// leave theirs at 0. Classes Halo doesn't use are 0.
	/// </summary>
	static readonly (float Minimum, float Maximum)[] ClassDistances =
	[
		(1.4f, 8), (8, 120), (0, 0), (0, 0), (4, 70), (1, 9), (1, 9), (1, 9), (1, 9), (1, 9), (1, 9), (0, 0), (0, 0),
		(0.5f, 3), (0.5f, 3), (0.5f, 3), (0, 0), (0, 0), (0.9f, 10), (3, 20), (0, 0), (0, 0), (1.4f, 8), (1.4f, 8),
		(0, 0), (0, 0), (0.9f, 5), (0.9f, 5), (0.9f, 5), (0.9f, 5), (0.5f, 3), (0, 0), (0.9f, 5), (0.9f, 5),
		(0.9f, 5), (0.5f, 3), (0, 0), (0, 0), (0, 0), (0.5f, 3), (0, 0), (0, 0), (0, 0), (0, 0), (3, 20), (2, 5),
		(3, 20), (3, 20), (0, 0), (0, 0), (3, 20),
	];

	/// <summary>
	/// The distances (world units) Halo fades the sound over: full volume within the minimum, then
	/// down to nothing at the maximum. A sound that leaves either at 0 uses its class's.
	/// </summary>
	public (float Minimum, float Maximum) Distances
	{
		get
		{
			var defaults = SoundClass >= 0 && SoundClass < ClassDistances.Length ? ClassDistances[SoundClass] : (0f, 0f);
			return (MinimumDistance > 0 ? MinimumDistance : defaults.Item1, MaximumDistance > 0 ? MaximumDistance : defaults.Item2);
		}
	}

	// MCC's limit on copies of one sound playing at once, by class, and how many milliseconds a copy plays
	// before a new one may take its place. Classes Halo doesn't use have no limit (0 copies).
	static readonly (int Copies, int PreemptionMilliseconds)[] ClassCopyLimits =
	[
		(3, 100), (4, 200), (0, 0), (0, 0), (4, 0), (4, 500), (4, 500), (4, 60), (4, 500), (4, 500), (4, 500), (0, 0), (0, 0),
		(2, 100), (2, 100), (2, 1000), (0, 0), (0, 0), (4, 200), (4, 100), (0, 0), (0, 0), (4, 400), (4, 100),
		(0, 0), (0, 0), (4, 100), (4, 100), (4, 100), (4, 100), (4, 100), (0, 0), (4, 100), (4, 100),
		(4, 100), (4, 100), (0, 0), (0, 0), (0, 0), (4, 100), (0, 0), (0, 0), (0, 0), (0, 0), (4, 100), (4, 100),
		(4, 100), (4, 100), (0, 0), (0, 0), (4, 100),
	];

	public (int Copies, int PreemptionMilliseconds) CopyLimits =>
		SoundClass >= 0 && SoundClass < ClassCopyLimits.Length ? ClassCopyLimits[SoundClass] : (0, 0);

	/// <summary>
	/// The total number of permutations in all pitch ranges.
	/// </summary>
	public int PermutationCount
	{
		get
		{
			var count = 0;
			foreach ( var pitchRange in PitchRanges )
				count += pitchRange.Permutations.Count;

			return count;
		}
	}
}
