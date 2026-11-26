using System;
using System.Buffers.Binary;

namespace Blam.Audio;

/// <summary>
/// Decoder for Xbox ADPCM, the IMA ADPCM variant original Halo used and MCC still uses for its
/// IMA ADPCM FSB bank.
/// </summary>
/// <remarks>
/// Data is in blocks of 36 bytes per channel. Each block starts with a 4 byte header per channel
/// (16 bit predictor, step index, unused byte), followed by the nibbles interleaved per channel
/// in 4 byte (8 sample) groups, low nibble first. A block holds 64 samples per channel; the
/// header predictor is only the starting state.
/// </remarks>
public static class XboxAdpcm
{
	public const int BlockSizePerChannel = 36;
	public const int SamplesPerBlock = 64;

	static readonly int[] IndexTable = [-1, -1, -1, -1, 2, 4, 6, 8, -1, -1, -1, -1, 2, 4, 6, 8];

	static readonly int[] StepTable =
	[
		7, 8, 9, 10, 11, 12, 13, 14, 16, 17, 19, 21, 23, 25, 28, 31, 34, 37, 41, 45,
		50, 55, 60, 66, 73, 80, 88, 97, 107, 118, 130, 143, 157, 173, 190, 209, 230, 253, 279, 307,
		337, 371, 408, 449, 494, 544, 598, 658, 724, 796, 876, 963, 1060, 1166, 1282, 1411, 1552, 1707, 1878, 2066,
		2272, 2499, 2749, 3024, 3327, 3660, 4026, 4428, 4871, 5358, 5894, 6484, 7132, 7845, 8630, 9493, 10442, 11487, 12635, 13899,
		15289, 16818, 18500, 20350, 22385, 24623, 27086, 29794, 32767
	];

	/// <summary>
	/// Number of samples per channel in <paramref name="byteCount"/> bytes of whole blocks.
	/// </summary>
	public static int SampleCount( int byteCount, int channels ) => byteCount / (BlockSizePerChannel * channels) * SamplesPerBlock;

	/// <summary>
	/// Decodes to interleaved 16 bit PCM. Decoding stops at <paramref name="sampleCount"/> samples
	/// per channel, or at the last whole block.
	/// </summary>
	public static short[] Decode( ReadOnlySpan<byte> data, int channels, int sampleCount )
	{
		var blockSize = BlockSizePerChannel * channels;
		sampleCount = Math.Min( sampleCount, SampleCount( data.Length, channels ) );
		var output = new short[sampleCount * channels];

		Span<int> predictor = stackalloc int[channels];
		Span<int> index = stackalloc int[channels];

		for ( var block = 0; block * SamplesPerBlock < sampleCount; block++ )
		{
			var b = data.Slice( block * blockSize, blockSize );
			for ( var c = 0; c < channels; c++ )
			{
				predictor[c] = BinaryPrimitives.ReadInt16LittleEndian( b[(c * 4)..] );
				index[c] = Math.Clamp( (int)b[c * 4 + 2], 0, 88 );
			}

			var first = block * SamplesPerBlock;
			var count = Math.Min( SamplesPerBlock, sampleCount - first );

			// 8 groups of 4 bytes per channel, each group 8 samples.
			for ( var group = 0; group < 8; group++ )
			{
				for ( var c = 0; c < channels; c++ )
				{
					var groupData = b.Slice( 4 * channels + (group * channels + c) * 4, 4 );
					for ( var i = 0; i < 8; i++ )
					{
						var nibble = (groupData[i >> 1] >> ((i & 1) * 4)) & 0xF;
						var sample = group * 8 + i;

						var step = StepTable[index[c]];
						var diff = step >> 3;
						if ( (nibble & 1) != 0 ) diff += step >> 2;
						if ( (nibble & 2) != 0 ) diff += step >> 1;
						if ( (nibble & 4) != 0 ) diff += step;
						if ( (nibble & 8) != 0 ) diff = -diff;

						predictor[c] = Math.Clamp( predictor[c] + diff, -32768, 32767 );
						index[c] = Math.Clamp( index[c] + IndexTable[nibble], 0, 88 );

						if ( sample < count )
							output[(first + sample) * channels + c] = (short)predictor[c];
					}
				}
			}
		}

		return output;
	}
}
