using System;
using System.Collections.Generic;
using System.Linq;

namespace Blam.Halo3;

// One variation of a sound: its FMOD subsound hash names the bank sample (see SoundBanks).
public sealed record SoundPermutation( string Name, uint Hash, float GainDecibels, float SkipFraction );

// A sound's variations for a range of pitches (cents), recorded at NaturalPitch.
public sealed record SoundPitchRange( string Name, short NaturalPitch, short BendMin, short BendMax, short MaxGainMin, short MaxGainMax, IReadOnlyList<SoundPermutation> Permutations )
{
	public bool HasFullGainAt( float pitch ) => pitch >= MaxGainMin && pitch <= MaxGainMax;

	public float RateAt( float pitch ) => MathF.Pow( 2, (pitch - NaturalPitch) / 1200f );

	public float DistanceTo( float pitch ) => pitch < BendMin ? BendMin - pitch : pitch > BendMax ? pitch - BendMax : 0;
}

// Playback parameters (ugh! playbacks, 0x44): distances in world units, gains in dB, random pitch in cents.
public sealed record SoundPlayback( float DontPlayDistance, float AttackDistance, float MinimumDistance, float MaximumDistance, float SkipFraction,
	float GainBase, float GainVariance, short RandomPitchMin, short RandomPitchMax )
{
	public static readonly SoundPlayback None = new( 0, 0, 0, 0, 0, 0, 0, 0, 0 );
}

// Scale modifiers at scale 0 and 1 (ugh! scales, 0x14), as in Halo 2.
public sealed record SoundScale( float GainMin, float GainMax, short PitchMin, short PitchMax, float SkipMin, float SkipMax )
{
	public static readonly SoundScale None = new( 0, 0, 0, 0, 1, 1 );

	public float PitchAt( float scale ) => PitchMin + (PitchMax - PitchMin) * scale;
}

// A sound class (sncl entry, 0x98): default distances and gain bounds; multilingual classes are dialog.
public sealed record SoundClass( float DistanceMin, float DistanceMax, float GainMin, float GainMax, bool Multilingual );

// The sound classes the globals reference (matg sound globals +0x3D4, first entry's +0x0).
public sealed class SoundClasses
{
	const ushort MultilingualFlag = 1 << 7;

	public IReadOnlyList<SoundClass> Classes { get; }

	SoundClasses( TagEntry tag )
	{
		Classes = tag.Data.Block( 0x0, 0x98 )
			.Select( e => new SoundClass( e.F32( 0x3C ), e.F32( 0x40 ), e.F32( 0x44 ), e.F32( 0x48 ), (e.U16( 0x8 ) & MultilingualFlag) != 0 ) )
			.ToList();
	}

	public static SoundClasses ForCache( CacheFile cache )
	{
		var globals = cache.Globals?.Data ?? default;
		if ( globals.IsNull )
			return null;

		var soundGlobals = globals.Element( 0x3D4, 0x50, 0 );
		var tag = soundGlobals.IsNull ? null : soundGlobals.Reference( 0x0 );
		return tag is not null && tag.Class == new TagClass( "sncl" ) && tag.Address != 0 ? new SoundClasses( tag ) : null;
	}

	public SoundClass Get( int index ) => index >= 0 && index < Classes.Count ? Classes[index] : null;
}

// The map's sound gestalt (ugh!): every snd! only indexes into it. Each tag-bearing map has its own complete one.
public sealed class SoundGestalt
{
	readonly TagView _data;
	readonly string[] _importNames;

	public TagEntry Tag { get; }

	public SoundGestalt( TagEntry tag )
	{
		Tag = tag;
		_data = tag.Data;
		_importNames = _data.Block( 0x28, 4 ).Select( e => e.StringId( 0 ) ?? "" ).ToArray();
	}

	public static SoundGestalt ForCache( CacheFile cache )
	{
		var tag = cache.TagsOf( "ugh!" ).FirstOrDefault( t => t.Address != 0 );
		return tag is null ? null : new SoundGestalt( tag );
	}

	public string ImportName( int index ) => index >= 0 && index < _importNames.Length ? _importNames[index] : "";

	// Codec: rate index (3 = 48000), encoding (0 mono, 1 stereo, 2 quad, 3 5.1), compression (7 = FMOD).
	public (int SampleRate, int Channels, int Compression) GetCodec( int index )
	{
		var codec = _data.Element( 0x4, 3, index );
		if ( codec.IsNull )
			return (48000, 1, 0);

		var rate = codec.U8( 0 ) switch { 0 => 22050, 1 => 44100, 2 => 32000, _ => 48000 };
		var channels = codec.U8( 1 ) switch { 0 => 1, 1 => 2, 2 => 4, 3 => 6, _ => 2 };
		return (rate, channels, codec.U8( 2 ));
	}

	public SoundPlayback GetPlayback( int index )
	{
		var p = _data.Element( 0x10, 0x44, index );
		if ( p.IsNull )
			return SoundPlayback.None;

		return new SoundPlayback( p.F32( 0x4 ), p.F32( 0x8 ), p.F32( 0xC ), p.F32( 0x10 ), p.F32( 0x14 ), p.F32( 0x1C ), p.F32( 0x20 ), p.I16( 0x24 ), p.I16( 0x26 ) );
	}

	public SoundScale GetScale( int index )
	{
		var s = _data.Element( 0x1C, 0x14, index );
		if ( s.IsNull )
			return SoundScale.None;

		return new SoundScale( s.F32( 0 ), s.F32( 4 ), s.I16( 8 ), s.I16( 0xA ), s.F32( 0xC ), s.F32( 0x10 ) );
	}

	public IReadOnlyList<SoundPitchRange> GetPitchRanges( int first, int count )
	{
		var ranges = new List<SoundPitchRange>();
		for ( var r = first; first >= 0 && r < first + count; r++ )
		{
			var range = _data.Element( 0x40, 0xC, r );
			if ( range.IsNull )
				break;

			short natural = 0, bendMin = short.MinValue, bendMax = short.MaxValue, maxGainMin = short.MinValue, maxGainMax = short.MaxValue;
			var parameters = _data.Element( 0x34, 0xE, range.I16( 2 ) );
			if ( !parameters.IsNull )
				(natural, bendMin, bendMax, maxGainMin, maxGainMax) = (parameters.I16( 0 ), parameters.I16( 2 ), parameters.I16( 4 ), parameters.I16( 6 ), parameters.I16( 8 ));

			// Low 16 bits: first permutation; bits 20-25: permutation count.
			var encoded = range.U32( 8 );
			var firstPermutation = (int)(encoded & 0xFFFF);
			var permutationCount = (int)(encoded >> 20) & 63;

			var permutations = new List<SoundPermutation>( permutationCount );
			for ( var p = firstPermutation; p < firstPermutation + permutationCount; p++ )
			{
				var permutation = _data.Element( 0x4C, 0x14, p );
				if ( permutation.IsNull )
					break;

				permutations.Add( new SoundPermutation( ImportName( permutation.I16( 0 ) ), permutation.U32( 0x10 ), permutation.I8( 0xE ), permutation.U16( 2 ) / 65535f ) );
			}

			ranges.Add( new SoundPitchRange( ImportName( range.I16( 0 ) ), natural, bendMin, bendMax, maxGainMin, maxGainMax, permutations ) );
		}

		return ranges;
	}
}

// A Halo 3 sound tag (snd!, 0x24): flags, class, then indices into the map's gestalt.
public sealed class SoundTag
{
	public static readonly TagClass Class = new( "snd!" );

	public TagEntry Tag { get; }
	public ushort Flags { get; }
	public int SoundClass { get; }
	public int SampleRate { get; }
	public int Channels { get; }
	public int Compression { get; }
	public SoundPlayback Playback { get; }
	public SoundScale Scale { get; }
	public IReadOnlyList<SoundPitchRange> PitchRanges { get; }

	public SoundTag( TagEntry tag, SoundGestalt gestalt )
	{
		Tag = tag;
		var data = tag.Data;
		Flags = data.U16( 0x0 );
		SoundClass = data.U8( 0x2 );
		(SampleRate, Channels, Compression) = gestalt.GetCodec( data.I16( 0x4 ) );
		Playback = gestalt.GetPlayback( data.I16( 0xC ) );
		Scale = gestalt.GetScale( data.I16( 0xE ) );
		PitchRanges = gestalt.GetPitchRanges( data.I16( 0x6 ), data.U8( 0x3 ) );
	}

	public IEnumerable<SoundPermutation> Permutations => PitchRanges.SelectMany( r => r.Permutations );

	// The playback's distances where set, else the sound class's; the minimum is at least 0.001.
	public (float Minimum, float Maximum) GetDistances( SoundClass soundClass )
	{
		var minimum = Playback.MaximumDistance > 0 ? Playback.MinimumDistance : soundClass?.DistanceMin ?? 0;
		var maximum = Playback.MaximumDistance > 0 ? Playback.MaximumDistance : soundClass?.DistanceMax ?? 0;
		return (MathF.Max( minimum, 0.001f ), maximum);
	}

	// The gain (dB) the sound class and the sound's scale add at a scale, as Halo 2 does it (amplitude interpolation between the bounds).
	public float ClassGainAt( SoundClass soundClass, float scale )
	{
		var at0 = (soundClass?.GainMin ?? 0) + Scale.GainMin;
		var at1 = (soundClass?.GainMax ?? 0) + Scale.GainMax;
		var amplitude = Amplitude( at0 ) + (Amplitude( at1 ) - Amplitude( at0 )) * Math.Clamp( scale, 0, 1 );
		return amplitude > 0 ? Math.Clamp( 20 * MathF.Log10( amplitude ), -64, 0 ) : -64;
	}

	// The first pitch range whose maximum gain bounds hold the pitch, else the one whose bend bounds come nearest; -1 if none is usable.
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
}
