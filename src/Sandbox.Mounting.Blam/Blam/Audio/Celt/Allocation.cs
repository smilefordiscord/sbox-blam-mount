/* C# port of the decoder side of libcelt 0.11.0 (rate.c, quant_bands.c), floating point.

   Copyright (c) 2007-2008 CSIRO
   Copyright (c) 2007-2009 Xiph.Org Foundation
   Written by Jean-Marc Valin

   Redistribution and use in source and binary forms, with or without
   modification, are permitted provided that the following conditions
   are met:

   - Redistributions of source code must retain the above copyright
   notice, this list of conditions and the following disclaimer.

   - Redistributions in binary form must reproduce the above copyright
   notice, this list of conditions and the following disclaimer in the
   documentation and/or other materials provided with the distribution.

   - Neither the name of the Xiph.org Foundation nor the names of its
   contributors may be used to endorse or promote products derived from
   this software without specific prior written permission.

   THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
   ``AS IS'' AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
   LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR
   A PARTICULAR PURPOSE ARE DISCLAIMED.  IN NO EVENT SHALL THE FOUNDATION OR
   CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL,
   EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO,
   PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR
   PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF
   LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING
   NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
   SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
*/

using System;

namespace Blam.Audio.Celt;

/// <summary>
/// Band energy decoding and the bit allocation that splits each frame's bits between fine
/// energy and the band shapes.
/// </summary>
static class Allocation
{
	const int BitRes = RangeDecoder.BitRes;
	const int AllocSteps = 6;

	static readonly byte[] Log2FracTable =
	[
		0,
		8, 13,
		16, 19, 21, 23,
		24, 26, 27, 28, 29, 30, 31, 32,
		32, 33, 34, 34, 35, 36, 36, 37, 37
	];

	/// <summary>
	/// Mean energy in each band, Q6 converted back to float.
	/// </summary>
	public static readonly float[] EMeans =
	[
		6.437500f, 6.250000f, 5.750000f, 5.312500f, 5.062500f,
		4.812500f, 4.500000f, 4.375000f, 4.875000f, 4.687500f,
		4.562500f, 4.437500f, 4.875000f, 4.625000f, 4.312500f,
		4.500000f, 4.375000f, 4.625000f, 4.750000f, 4.437500f,
		3.750000f, 3.750000f, 3.750000f, 3.750000f, 3.750000f
	];

	// Prediction coefficients: 0.9, 0.8, 0.65, 0.5
	static readonly float[] PredCoef = [29440 / 32768f, 26112 / 32768f, 21248 / 32768f, 16384 / 32768f];
	static readonly float[] BetaCoef = [30147 / 32768f, 22282 / 32768f, 12124 / 32768f, 6554 / 32768f];
	const float BetaIntra = 4915 / 32768f;

	/// <summary>
	/// Parameters of the Laplace-like models for the coarse energy: per frame size, inter/intra
	/// and band, the probability of 0 and the decay rate, both Q8.
	/// </summary>
	static readonly byte[][][] EProbModel =
	[
		// 120 sample frames.
		[
			[72, 127, 65, 129, 66, 128, 65, 128, 64, 128, 62, 128, 64, 128, 64, 128, 92, 78, 92, 79, 92, 78, 90, 79, 116, 41, 115, 40, 114, 40, 132, 26, 132, 26, 145, 17, 161, 12, 176, 10, 177, 11],
			[24, 179, 48, 138, 54, 135, 54, 132, 53, 134, 56, 133, 55, 132, 55, 132, 61, 114, 70, 96, 74, 88, 75, 88, 87, 74, 89, 66, 91, 67, 100, 59, 108, 50, 120, 40, 122, 37, 97, 43, 78, 50],
		],
		// 240 sample frames.
		[
			[83, 78, 84, 81, 88, 75, 86, 74, 87, 71, 90, 73, 93, 74, 93, 74, 109, 40, 114, 36, 117, 34, 117, 34, 143, 17, 145, 18, 146, 19, 162, 12, 165, 10, 178, 7, 189, 6, 190, 8, 177, 9],
			[23, 178, 54, 115, 63, 102, 66, 98, 69, 99, 74, 89, 71, 91, 73, 91, 78, 89, 86, 80, 92, 66, 93, 64, 102, 59, 103, 60, 104, 60, 117, 52, 123, 44, 138, 35, 133, 31, 97, 38, 77, 45],
		],
		// 480 sample frames.
		[
			[61, 90, 93, 60, 105, 42, 107, 41, 110, 45, 116, 38, 113, 38, 112, 38, 124, 26, 132, 27, 136, 19, 140, 20, 155, 14, 159, 16, 158, 18, 170, 13, 177, 10, 187, 8, 192, 6, 175, 9, 159, 10],
			[21, 178, 59, 110, 71, 86, 75, 85, 84, 83, 91, 66, 88, 73, 87, 72, 92, 75, 98, 72, 105, 58, 107, 54, 115, 52, 114, 55, 112, 56, 129, 51, 132, 40, 150, 33, 140, 29, 98, 35, 77, 42],
		],
		// 960 sample frames.
		[
			[42, 121, 96, 66, 108, 43, 111, 40, 117, 44, 123, 32, 120, 36, 119, 33, 127, 33, 134, 34, 139, 21, 147, 23, 152, 20, 158, 25, 154, 26, 166, 21, 173, 16, 184, 13, 184, 10, 150, 13, 139, 15],
			[22, 178, 63, 114, 74, 82, 84, 83, 92, 82, 103, 62, 96, 72, 96, 67, 101, 73, 107, 72, 113, 55, 118, 52, 125, 52, 118, 52, 117, 55, 135, 49, 137, 39, 157, 32, 145, 29, 97, 33, 77, 40],
		],
	];

	static readonly byte[] SmallEnergyIcdf = [2, 1, 0];

	public static void UnquantCoarseEnergy( CeltMode m, int start, int end, Span<float> oldEBands, bool intra, RangeDecoder dec, int c, int lm )
	{
		var probModel = EProbModel[lm][intra ? 1 : 0];
		Span<float> prev = stackalloc float[2];
		float coef, beta;
		if ( intra )
		{
			coef = 0;
			beta = BetaIntra;
		}
		else
		{
			beta = BetaCoef[lm];
			coef = PredCoef[lm];
		}

		var budget = (int)dec.Storage * 8;

		// Decode at a fixed coarse resolution.
		for ( var i = start; i < end; i++ )
		{
			for ( var ch = 0; ch < c; ch++ )
			{
				int qi;
				var tell = dec.Tell();
				if ( budget - tell >= 15 )
				{
					var pi = 2 * Math.Min( i, 20 );
					qi = dec.DecodeLaplace( probModel[pi] << 7, probModel[pi + 1] << 6 );
				}
				else if ( budget - tell >= 2 )
				{
					qi = dec.DecodeIcdf( SmallEnergyIcdf, 2 );
					qi = (qi >> 1) ^ -(qi & 1);
				}
				else if ( budget - tell >= 1 )
				{
					qi = -dec.DecodeBitLogp( 1 );
				}
				else
				{
					qi = -1;
				}

				float q = qi;
				var index = i + ch * m.NbEBands;
				oldEBands[index] = MathF.Max( -9f, oldEBands[index] );
				var tmp = coef * oldEBands[index] + prev[ch] + q;
				oldEBands[index] = tmp;
				prev[ch] = prev[ch] + q - beta * q;
			}
		}
	}

	public static void UnquantFineEnergy( CeltMode m, int start, int end, Span<float> oldEBands, ReadOnlySpan<int> fineQuant, RangeDecoder dec, int c )
	{
		for ( var i = start; i < end; i++ )
		{
			if ( fineQuant[i] <= 0 )
				continue;

			for ( var ch = 0; ch < c; ch++ )
			{
				var q2 = (int)dec.DecodeBits( fineQuant[i] );
				var offset = (q2 + 0.5f) * (1 << (14 - fineQuant[i])) * (1f / 16384) - 0.5f;
				oldEBands[i + ch * m.NbEBands] += offset;
			}
		}
	}

	public static void UnquantEnergyFinalise( CeltMode m, int start, int end, Span<float> oldEBands, ReadOnlySpan<int> fineQuant, ReadOnlySpan<int> finePriority, int bitsLeft, RangeDecoder dec, int c )
	{
		// Use up the remaining bits.
		for ( var prio = 0; prio < 2; prio++ )
		{
			for ( var i = start; i < end && bitsLeft >= c; i++ )
			{
				if ( fineQuant[i] >= CeltMode.MaxFineBits || finePriority[i] != prio )
					continue;

				for ( var ch = 0; ch < c; ch++ )
				{
					var q2 = (int)dec.DecodeBits( 1 );
					var offset = (q2 - 0.5f) * (1 << (14 - fineQuant[i] - 1)) * (1f / 16384);
					oldEBands[i + ch * m.NbEBands] += offset;
					bitsLeft--;
				}
			}
		}
	}

	/// <summary>
	/// Converts log2 band energies back to amplitudes.
	/// </summary>
	public static void Log2Amp( CeltMode m, int start, int end, Span<float> eBands, ReadOnlySpan<float> oldEBands, int c )
	{
		for ( var ch = 0; ch < c; ch++ )
		{
			int i;
			for ( i = 0; i < start; i++ )
				eBands[i + ch * m.NbEBands] = 0;

			for ( ; i < end; i++ )
			{
				var lg = oldEBands[i + ch * m.NbEBands] + EMeans[i];
				eBands[i + ch * m.NbEBands] = (float)Math.Exp( 0.6931471805599453094 * lg );
			}

			for ( ; i < m.NbEBands; i++ )
				eBands[i + ch * m.NbEBands] = 0;
		}
	}

	/// <summary>
	/// Computes how many bits go to each band's shape (<paramref name="pulses"/>) and fine
	/// energy (<paramref name="ebits"/>), reading the skip, intensity and dual stereo decisions.
	/// </summary>
	/// <returns>The number of coded bands.</returns>
	public static int ComputeAllocation( CeltMode m, int start, int end, ReadOnlySpan<int> offsets, ReadOnlySpan<int> cap, int allocTrim,
		ref int intensity, ref bool dualStereo, int total, out int balance, Span<int> pulses, Span<int> ebits, Span<int> finePriority,
		int c, int lm, RangeDecoder dec )
	{
		total = Math.Max( total, 0 );
		var len = m.NbEBands;
		var skipStart = start;

		// Reserve a bit to signal the end of manually skipped bands.
		var skipRsv = total >= 1 << BitRes ? 1 << BitRes : 0;
		total -= skipRsv;

		// Reserve bits for the intensity and dual stereo parameters.
		int intensityRsv = 0, dualStereoRsv = 0;
		if ( c == 2 )
		{
			intensityRsv = Log2FracTable[end - start];
			if ( intensityRsv > total )
			{
				intensityRsv = 0;
			}
			else
			{
				total -= intensityRsv;
				dualStereoRsv = total >= 1 << BitRes ? 1 << BitRes : 0;
				total -= dualStereoRsv;
			}
		}

		Span<int> bits1 = stackalloc int[len];
		Span<int> bits2 = stackalloc int[len];
		Span<int> thresh = stackalloc int[len];
		Span<int> trimOffset = stackalloc int[len];

		for ( var j = start; j < end; j++ )
		{
			// Below this threshold, we're sure not to allocate any PVQ bits.
			thresh[j] = Math.Max( c << BitRes, (3 * (m.EBands[j + 1] - m.EBands[j]) << lm << BitRes) >> 4 );
			// Tilt of the allocation curve.
			trimOffset[j] = c * (m.EBands[j + 1] - m.EBands[j]) * (allocTrim - 5 - lm) * (m.NbEBands - j - 1) << (lm + BitRes) >> 6;
			// Giving less resolution to single-coefficient bands because they get more benefit
			// from having one coarse value per coefficient.
			if ( (m.EBands[j + 1] - m.EBands[j]) << lm == 1 )
				trimOffset[j] -= c << BitRes;
		}

		var lo = 1;
		var hi = m.NbAllocVectors - 1;
		do
		{
			var done = false;
			var psum = 0;
			var mid = (lo + hi) >> 1;
			for ( var j = end; j-- > start; )
			{
				var n = m.EBands[j + 1] - m.EBands[j];
				bits1[j] = c * n * m.AllocVectors[mid * len + j] << lm >> 2;
				if ( bits1[j] > 0 )
					bits1[j] = Math.Max( 0, bits1[j] + trimOffset[j] );

				bits1[j] += offsets[j];
				if ( bits1[j] >= thresh[j] || done )
				{
					done = true;
					// Don't allocate more than we can actually use.
					psum += Math.Min( bits1[j], cap[j] );
				}
				else if ( bits1[j] >= c << BitRes )
				{
					psum += c << BitRes;
				}
			}

			if ( psum > total )
				hi = mid - 1;
			else
				lo = mid + 1;
		}
		while ( lo <= hi );

		hi = lo--;
		for ( var j = start; j < end; j++ )
		{
			var n = m.EBands[j + 1] - m.EBands[j];
			bits1[j] = c * n * m.AllocVectors[lo * len + j] << lm >> 2;
			bits2[j] = hi >= m.NbAllocVectors ? cap[j] : c * n * m.AllocVectors[hi * len + j] << lm >> 2;
			if ( bits1[j] > 0 )
				bits1[j] = Math.Max( 0, bits1[j] + trimOffset[j] );
			if ( bits2[j] > 0 )
				bits2[j] = Math.Max( 0, bits2[j] + trimOffset[j] );
			if ( lo > 0 )
				bits1[j] += offsets[j];

			bits2[j] += offsets[j];
			if ( offsets[j] > 0 )
				skipStart = j;

			bits2[j] = Math.Max( 0, bits2[j] - bits1[j] );
		}

		return InterpBitsToPulses( m, start, end, skipStart, bits1, bits2, thresh, cap, total, out balance, skipRsv,
			ref intensity, intensityRsv, ref dualStereo, dualStereoRsv, pulses, ebits, finePriority, c, lm, dec );
	}

	static int InterpBitsToPulses( CeltMode m, int start, int end, int skipStart, ReadOnlySpan<int> bits1, ReadOnlySpan<int> bits2,
		ReadOnlySpan<int> thresh, ReadOnlySpan<int> cap, int total, out int outBalance, int skipRsv, ref int intensity, int intensityRsv,
		ref bool dualStereo, int dualStereoRsv, Span<int> bits, Span<int> ebits, Span<int> finePriority, int c, int lm, RangeDecoder dec )
	{
		var allocFloor = c << BitRes;
		var stereo = c > 1 ? 1 : 0;
		var logM = lm << BitRes;
		var lo = 0;
		var hi = 1 << AllocSteps;
		int psum;
		bool done;

		for ( var i = 0; i < AllocSteps; i++ )
		{
			var mid = (lo + hi) >> 1;
			psum = 0;
			done = false;
			for ( var j = end; j-- > start; )
			{
				var tmp = bits1[j] + (mid * bits2[j] >> AllocSteps);
				if ( tmp >= thresh[j] || done )
				{
					done = true;
					// Don't allocate more than we can actually use.
					psum += Math.Min( tmp, cap[j] );
				}
				else if ( tmp >= allocFloor )
				{
					psum += allocFloor;
				}
			}

			if ( psum > total )
				hi = mid;
			else
				lo = mid;
		}

		psum = 0;
		done = false;
		for ( var j = end; j-- > start; )
		{
			var tmp = bits1[j] + (lo * bits2[j] >> AllocSteps);
			if ( tmp < thresh[j] && !done )
			{
				tmp = tmp >= allocFloor ? allocFloor : 0;
			}
			else
			{
				done = true;
			}

			// Don't allocate more than we can actually use.
			tmp = Math.Min( tmp, cap[j] );
			bits[j] = tmp;
			psum += tmp;
		}

		// Decide which bands to skip, working backwards from the end.
		int codedBands, left, percoeff;
		for ( codedBands = end; ; codedBands-- )
		{
			var j = codedBands - 1;
			// Never skip the first band, nor a band that has been boosted by dynalloc.
			if ( j <= skipStart )
			{
				// Give the bit we reserved to end skipping back.
				total += skipRsv;
				break;
			}

			// How many left-over bits we would be adding to this band, including bits stolen
			// back from higher, skipped bands.
			left = total - psum;
			percoeff = left / (m.EBands[codedBands] - m.EBands[start]);
			left -= (m.EBands[codedBands] - m.EBands[start]) * percoeff;
			var rem = Math.Max( left - (m.EBands[j] - m.EBands[start]), 0 );
			var bandWidth = m.EBands[codedBands] - m.EBands[j];
			var bandBits = bits[j] + percoeff * bandWidth + rem;

			// Only code a skip decision if we're above the threshold for this band. Otherwise
			// it is force-skipped.
			if ( bandBits >= Math.Max( thresh[j], allocFloor + (1 << BitRes) ) )
			{
				if ( dec.DecodeBitLogp( 1 ) != 0 )
					break;

				// We used a bit to skip this band.
				psum += 1 << BitRes;
				bandBits -= 1 << BitRes;
			}

			// Reclaim the bits originally allocated to this band.
			psum -= bits[j] + intensityRsv;
			if ( intensityRsv > 0 )
				intensityRsv = Log2FracTable[j - start];

			psum += intensityRsv;
			if ( bandBits >= allocFloor )
			{
				// If we have enough for a fine energy bit per channel, use it.
				psum += allocFloor;
				bits[j] = allocFloor;
			}
			else
			{
				// Otherwise this band gets nothing at all.
				bits[j] = 0;
			}
		}

		// The intensity and dual stereo parameters.
		if ( intensityRsv > 0 )
			intensity = start + (int)dec.DecodeUint( (uint)(codedBands + 1 - start) );
		else
			intensity = 0;

		if ( intensity <= start )
		{
			total += dualStereoRsv;
			dualStereoRsv = 0;
		}

		dualStereo = dualStereoRsv > 0 && dec.DecodeBitLogp( 1 ) != 0;

		// Allocate the remaining bits.
		left = total - psum;
		percoeff = left / (m.EBands[codedBands] - m.EBands[start]);
		left -= (m.EBands[codedBands] - m.EBands[start]) * percoeff;
		for ( var j = start; j < codedBands; j++ )
			bits[j] += percoeff * (m.EBands[j + 1] - m.EBands[j]);

		for ( var j = start; j < codedBands; j++ )
		{
			var tmp = Math.Min( left, m.EBands[j + 1] - m.EBands[j] );
			bits[j] += tmp;
			left -= tmp;
		}

		var balance = 0;
		int jj;
		for ( jj = start; jj < codedBands; jj++ )
		{
			int excess;
			var n0 = m.EBands[jj + 1] - m.EBands[jj];
			var n = n0 << lm;
			bits[jj] += balance;
			if ( n > 1 )
			{
				excess = Math.Max( bits[jj] - cap[jj], 0 );
				bits[jj] -= excess;

				// Compensate for the extra DoF in stereo.
				var den = c * n + ((c == 2 && n > 2 && !dualStereo && jj < intensity) ? 1 : 0);
				var nclogn = den * (m.LogN[jj] + logM);

				// Offset for the number of fine bits by log2(N)/2 + FINE_OFFSET compared to
				// their "fair share" of total/N.
				var offset = (nclogn >> 1) - den * CeltMode.FineOffset;

				// N=2 is the only point that doesn't match the curve.
				if ( n == 2 )
					offset += den << BitRes >> 2;

				// Changing the offset for allocating the second and third fine energy bit.
				if ( bits[jj] + offset < den * 2 << BitRes )
					offset += nclogn >> 2;
				else if ( bits[jj] + offset < den * 3 << BitRes )
					offset += nclogn >> 3;

				// Divide with rounding.
				ebits[jj] = Math.Max( 0, (bits[jj] + offset + (den << (BitRes - 1))) / (den << BitRes) );

				// Make sure not to bust.
				if ( c * ebits[jj] > bits[jj] >> BitRes )
					ebits[jj] = bits[jj] >> stereo >> BitRes;

				// More than that is useless because that's about as far as PVQ can go.
				ebits[jj] = Math.Min( ebits[jj], CeltMode.MaxFineBits );

				// If we rounded down or capped this band, make it a candidate for the final fine
				// energy pass.
				finePriority[jj] = ebits[jj] * (den << BitRes) >= bits[jj] + offset ? 1 : 0;

				// Remove the allocated fine bits; the rest are assigned to PVQ.
				bits[jj] -= c * ebits[jj] << BitRes;
			}
			else
			{
				// For N=1, all bits go to fine energy except for a single sign bit.
				excess = Math.Max( 0, bits[jj] - (c << BitRes) );
				bits[jj] -= excess;
				ebits[jj] = 0;
				finePriority[jj] = 1;
			}

			// Fine energy can't take advantage of the re-balancing in quant_all_bands(), so the
			// re-balancing is done here instead.
			if ( excess > 0 )
			{
				var extraFine = Math.Min( excess >> (stereo + BitRes), CeltMode.MaxFineBits - ebits[jj] );
				ebits[jj] += extraFine;
				var extraBits = extraFine * c << BitRes;
				finePriority[jj] = extraBits >= excess - balance ? 1 : 0;
				excess -= extraBits;
			}

			balance = excess;
		}

		// Save any remaining bits over the cap for the rebalancing in quant_all_bands().
		outBalance = balance;

		// The skipped bands use all their bits for fine energy.
		for ( ; jj < end; jj++ )
		{
			ebits[jj] = bits[jj] >> stereo >> BitRes;
			bits[jj] = 0;
			finePriority[jj] = ebits[jj] < 1 ? 1 : 0;
		}

		return codedBands;
	}
}
