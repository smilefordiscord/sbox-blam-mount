using System;
using System.Collections.Generic;

namespace Blam.Halo1;

/// <summary>
/// One piece of decoded audio: interleaved 16 bit samples.
/// </summary>
public sealed record PcmClip( short[] Samples, int Channels, int SampleRate )
{
	public int Frames => Channels > 0 ? Samples.Length / Channels : 0;
	public double Seconds => SampleRate > 0 ? (double)Frames / SampleRate : 0;
}

/// <summary>
/// A looping sound track baked into one sound: the start sound, then a sequence of the loop's
/// variations that repeats from where the start sound ends.
/// </summary>
/// <remarks>
/// Halo doesn't repeat one sample: every time the loop sound finishes it picks another
/// permutation of it at random, never the same one twice until all have played, and passes over
/// a permutation with its skip fraction. Long ambient loops are several variations of a few
/// seconds each, so looping one of them repeats audibly. The sequence is a few such rounds in a
/// row, long enough that its own repetition isn't noticeable.
/// </remarks>
public static class LoopingSoundSequence
{
	/// <summary>
	/// Rounds of variations are added until the sequence is at least this long. One round holds
	/// every variation already, so more only help short loops (and cost memory: a level's
	/// ambience is a few dozen tracks).
	/// </summary>
	public const double TargetSeconds = 30;

	/// <summary>
	/// At most this many rounds, so many short variations don't make a huge sound.
	/// </summary>
	public const int MaximumRounds = 4;

	/// <summary>
	/// The order to play the loop's variations in: rounds that each play every variation once in a
	/// random order (unless skipped by its skip fraction), without the same variation twice in a
	/// row, including across the end of the sequence.
	/// </summary>
	/// <param name="seconds">Length of each variation.</param>
	/// <param name="skipFractions">Each variation's skip fraction, or null for none.</param>
	public static List<int> BuildOrder( IReadOnlyList<double> seconds, IReadOnlyList<float> skipFractions, Random random )
	{
		var order = new List<int>();
		var count = seconds.Count;
		if ( count == 0 )
			return order;

		if ( count == 1 )
		{
			order.Add( 0 );
			return order;
		}

		var total = 0.0;
		for ( var round = 0; round < MaximumRounds && (round == 0 || total < TargetSeconds); round++ )
		{
			var picks = new List<int>();
			for ( var i = 0; i < count; i++ )
			{
				var skip = skipFractions is not null && i < skipFractions.Count ? skipFractions[i] : 0;
				if ( random.NextDouble() >= skip )
					picks.Add( i );
			}

			// Halo always ends up playing something.
			if ( picks.Count == 0 )
				picks.Add( random.Next( count ) );

			Shuffle( picks, random );

			// Don't repeat the variation that just played.
			if ( order.Count > 0 && picks.Count > 1 && picks[0] == order[^1] )
				(picks[0], picks[^1]) = (picks[^1], picks[0]);

			foreach ( var pick in picks )
			{
				order.Add( pick );
				total += seconds[pick];
			}
		}

		// Nor when the sequence wraps around.
		if ( order.Count > 2 && order[^1] == order[0] )
		{
			var last = order.Count - 1;
			for ( var i = last - 2; i > 0; i-- )
			{
				// Swapping the last with an earlier one must not put either next to its own kind.
				if ( order[i] != order[0] && order[i] != order[last - 1] && order[i - 1] != order[last] && order[i + 1] != order[last] )
				{
					(order[i], order[last]) = (order[last], order[i]);
					break;
				}
			}

			// No swap works (few variations, some skipped): leave the last one out.
			if ( order[last] == order[0] )
				order.RemoveAt( last );
		}

		return order;
	}

	static void Shuffle( List<int> list, Random random )
	{
		for ( var i = list.Count - 1; i > 0; i-- )
		{
			var j = random.Next( i + 1 );
			(list[i], list[j]) = (list[j], list[i]);
		}
	}

	/// <summary>
	/// Joins the start sound (optional), the loop variations in the given order and the end sound
	/// (optional) into one clip, at the highest sample rate and channel count among them.
	/// </summary>
	/// <param name="startRate">Playback rate of the start sound relative to the loop's (their
	/// pitch ranges' playback rates can differ); the start is resampled to match.</param>
	/// <param name="loopStartFrame">The frame the loop starts at: where the start sound ends.</param>
	/// <param name="endRate">The end sound's playback rate relative to the loop's, as for the start.</param>
	public static PcmClip Join( PcmClip start, float startRate, IReadOnlyList<PcmClip> variations, IReadOnlyList<int> order, out int loopStartFrame, PcmClip end = null, float endRate = 1 )
	{
		var channels = Math.Max( start?.Channels ?? 1, end?.Channels ?? 1 );
		var sampleRate = Math.Max( start?.SampleRate ?? 0, end?.SampleRate ?? 0 );
		foreach ( var index in order )
		{
			channels = Math.Max( channels, variations[index].Channels );
			sampleRate = Math.Max( sampleRate, variations[index].SampleRate );
		}

		var parts = new List<short[]>();
		loopStartFrame = 0;
		if ( start is not null )
		{
			var converted = Convert( start, channels, sampleRate, startRate );
			parts.Add( converted );
			loopStartFrame = converted.Length / channels;
		}

		// Each variation is converted once, however often it plays.
		var cache = new Dictionary<int, short[]>();
		foreach ( var index in order )
		{
			if ( !cache.TryGetValue( index, out var converted ) )
				cache[index] = converted = Convert( variations[index], channels, sampleRate, 1 );

			parts.Add( converted );
		}

		if ( end is not null )
			parts.Add( Convert( end, channels, sampleRate, endRate ) );

		var length = 0;
		foreach ( var part in parts )
			length += part.Length;

		var samples = new short[length];
		var offset = 0;
		foreach ( var part in parts )
		{
			part.CopyTo( samples, offset );
			offset += part.Length;
		}

		return new PcmClip( samples, channels, sampleRate );
	}

	/// <summary>
	/// Converts a clip to a channel count and sample rate, sped up by <paramref name="speed"/>.
	/// Mono is copied to every channel; extra channels are dropped (banks give at most stereo).
	/// Resampling is linear, which is fine for the occasional mismatch.
	/// </summary>
	public static short[] Convert( PcmClip clip, int channels, int sampleRate, float speed )
	{
		var step = (double)clip.SampleRate / sampleRate * speed;
		var frames = clip.Frames;
		if ( frames == 0 )
			return [];

		var outFrames = Math.Abs( step - 1 ) < 1e-6 ? frames : (int)Math.Floor( (frames - 1) / step ) + 1;
		var result = new short[outFrames * channels];
		for ( var f = 0; f < outFrames; f++ )
		{
			var position = f * step;
			var i0 = Math.Min( (int)position, frames - 1 );
			var i1 = Math.Min( i0 + 1, frames - 1 );
			var t = position - i0;

			for ( var c = 0; c < channels; c++ )
			{
				var source = clip.Channels == 1 ? 0 : Math.Min( c, clip.Channels - 1 );
				var a = clip.Samples[i0 * clip.Channels + source];
				var b = clip.Samples[i1 * clip.Channels + source];
				result[f * channels + c] = (short)Math.Round( a + (b - a) * t );
			}
		}

		return result;
	}
}
