/* C# port of the decoder side of libcelt 0.11.0 (modes.c, rate.c: compute_pulse_cache).

   Copyright (c) 2007-2008 CSIRO
   Copyright (c) 2007-2009 Xiph.Org Foundation
   Copyright (c) 2008 Gregory Maxwell
   Written by Jean-Marc Valin and Gregory Maxwell

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
using System.Collections.Generic;

namespace Blam.Audio.Celt;

/// <summary>
/// A CELT "custom mode": the band layout, bit allocation tables, window and pulse cache for one
/// sample rate and frame size. Everything here is derived from those two numbers, so encoder and
/// decoder agree without transmitting any of it.
/// </summary>
sealed class CeltMode
{
	public const int MaxPseudo = 40;
	public const int LogMaxPseudo = 6;
	public const int MaxPulses = 128;
	public const int MaxFineBits = 8;
	public const int FineOffset = 21;
	public const int QThetaOffset = 4;
	public const int QThetaOffsetTwoPhase = 16;

	const int BitRes = RangeDecoder.BitRes;

	static readonly short[] EBand5Ms =
	[
		0, 1, 2, 3, 4, 5, 6, 7, 8, 10, 12, 14, 16, 20, 24, 28, 34, 40, 48, 60, 78, 100
	];

	const int BitAllocSize = 11;

	/// <summary>
	/// Bit allocation table in units of 1/32 bit/sample, one row per quality level.
	/// </summary>
	static readonly byte[] BandAllocation =
	[
		0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
		90, 80, 75, 69, 63, 56, 49, 40, 34, 29, 20, 18, 10, 0, 0, 0, 0, 0, 0, 0, 0,
		110, 100, 90, 84, 78, 71, 65, 58, 51, 45, 39, 32, 26, 20, 12, 0, 0, 0, 0, 0, 0,
		118, 110, 103, 93, 86, 80, 75, 70, 65, 59, 53, 47, 40, 31, 23, 15, 4, 0, 0, 0, 0,
		126, 119, 112, 104, 95, 89, 83, 78, 72, 66, 60, 54, 47, 39, 32, 25, 17, 12, 1, 0, 0,
		134, 127, 120, 114, 103, 97, 91, 85, 78, 72, 66, 60, 54, 47, 41, 35, 29, 23, 16, 10, 1,
		144, 137, 130, 124, 113, 107, 101, 95, 88, 82, 76, 70, 64, 57, 51, 45, 39, 33, 26, 15, 1,
		152, 145, 138, 132, 123, 117, 111, 105, 98, 92, 86, 80, 74, 67, 61, 55, 49, 43, 36, 20, 1,
		162, 155, 148, 142, 133, 127, 121, 115, 108, 102, 96, 90, 84, 77, 71, 65, 59, 53, 46, 30, 1,
		172, 165, 158, 152, 143, 137, 131, 125, 118, 112, 106, 100, 94, 87, 81, 75, 69, 63, 56, 45, 20,
		200, 200, 200, 200, 200, 200, 200, 200, 198, 193, 188, 183, 178, 173, 168, 163, 158, 153, 148, 129, 104,
	];

	/// <summary>
	/// Bark scale critical band edges in Hz, for laying out bands at other rates.
	/// </summary>
	static readonly short[] BarkFreq =
	[
		0, 100, 200, 300, 400, 510, 630, 770, 920, 1080, 1270, 1480, 1720, 2000, 2320,
		2700, 3150, 3700, 4400, 5300, 6400, 7700, 9500, 12000, 15500, 20000
	];

	public int SampleRate { get; }
	public int Overlap { get; }
	public int NbEBands { get; }
	public int EffEBands { get; }
	public float[] Preemph { get; } = new float[4];
	public short[] EBands { get; }
	public int NbAllocVectors { get; }
	public byte[] AllocVectors { get; }
	public float[] Window { get; }
	public int MaxLM { get; }
	public int NbShortMdcts { get; }
	public int ShortMdctSize { get; }
	public short[] LogN { get; }
	public Mdct Mdct { get; }

	public short[] CacheIndex { get; private set; }
	public byte[] CacheBits { get; private set; }
	public byte[] CacheCaps { get; private set; }

	public CeltMode( int sampleRate, int frameSize )
	{
		if ( sampleRate < 8000 || sampleRate > 96000 || frameSize < 40 || frameSize > 1024 || frameSize % 2 != 0 || frameSize * 1000 < sampleRate )
			throw new ArgumentException( $"Unsupported CELT mode {sampleRate} Hz / {frameSize}" );

		int lm;
		if ( frameSize * 75 >= sampleRate && frameSize % 16 == 0 ) lm = 3;
		else if ( frameSize * 150 >= sampleRate && frameSize % 8 == 0 ) lm = 2;
		else if ( frameSize * 300 >= sampleRate && frameSize % 4 == 0 ) lm = 1;
		else lm = 0;

		if ( (frameSize >> lm) * 300 > sampleRate )
			throw new ArgumentException( $"Unsupported CELT mode {sampleRate} Hz / {frameSize}" );

		SampleRate = sampleRate;

		// Pre/de-emphasis approximating 1 - 0.85 z^-1 at 48 kHz.
		if ( sampleRate < 12000 ) Preemph = [0.3500061035f, -0.1799926758f, 0.2719968125f, 3.6765136719f];
		else if ( sampleRate < 24000 ) Preemph = [0.6000061035f, -0.1799926758f, 0.4424998650f, 2.2598876953f];
		else if ( sampleRate < 40000 ) Preemph = [0.7799987793f, -0.1000061035f, 0.7499771125f, 1.3333740234f];
		else Preemph = [0.8500061035f, 0.0f, 1.0f, 1.0f];

		MaxLM = lm;
		NbShortMdcts = 1 << lm;
		ShortMdctSize = frameSize / NbShortMdcts;
		var res = (sampleRate + ShortMdctSize) / (2 * ShortMdctSize);

		EBands = ComputeEBands( sampleRate, ShortMdctSize, res, out var nbEBands );
		NbEBands = nbEBands;
		EffEBands = NbEBands;
		while ( EBands[EffEBands] > ShortMdctSize )
			EffEBands--;

		Overlap = (ShortMdctSize >> 2) << 2;

		NbAllocVectors = BitAllocSize;
		AllocVectors = ComputeAllocationTable();

		Window = new float[Overlap];
		for ( var i = 0; i < Overlap; i++ )
		{
			var s = Math.Sin( 0.5 * Math.PI * (i + 0.5) / Overlap );
			Window[i] = (float)Math.Sin( 0.5 * Math.PI * s * s );
		}

		LogN = new short[NbEBands];
		for ( var i = 0; i < NbEBands; i++ )
			LogN[i] = (short)Log2Frac( (uint)(EBands[i + 1] - EBands[i]), BitRes );

		ComputePulseCache( MaxLM );
		Mdct = new Mdct( 2 * ShortMdctSize * NbShortMdcts, MaxLM );
	}

	static short[] ComputeEBands( int fs, int frameSize, int res, out int nbEBands )
	{
		// All modes with 2.5 ms short blocks use the standard layout.
		if ( fs == 400 * frameSize )
		{
			nbEBands = EBand5Ms.Length - 1;
			return (short[])EBand5Ms.Clone();
		}

		int nBark;
		for ( nBark = 1; nBark < BarkFreq.Length - 1; nBark++ )
		{
			if ( BarkFreq[nBark + 1] * 2 >= fs )
				break;
		}

		// Where the linear part ends, i.e. where the spacing is more than the minimum width.
		int lin;
		for ( lin = 0; lin < nBark; lin++ )
		{
			if ( BarkFreq[lin + 1] - BarkFreq[lin] >= res )
				break;
		}

		var low = (BarkFreq[lin] + res / 2) / res;
		var high = nBark - lin;
		nbEBands = low + high;
		var eBands = new short[nbEBands + 2];

		for ( var i = 0; i < low; i++ )
			eBands[i] = (short)i;

		var offset = 0;
		if ( low > 0 )
			offset = eBands[low - 1] * res - BarkFreq[lin - 1];

		for ( var i = 0; i < high; i++ )
		{
			int target = BarkFreq[lin + i];
			eBands[i + low] = (short)((target + offset / 2 + res) / (2 * res) * 2);
			offset = eBands[i + low] * res - target;
		}

		for ( var i = 0; i < nbEBands; i++ )
		{
			if ( eBands[i] < i )
				eBands[i] = (short)i;
		}

		eBands[nbEBands] = (short)((BarkFreq[nBark] + res) / (2 * res) * 2);
		if ( eBands[nbEBands] > frameSize )
			eBands[nbEBands] = (short)frameSize;

		for ( var i = 1; i < nbEBands - 1; i++ )
		{
			if ( eBands[i + 1] - eBands[i] < eBands[i] - eBands[i - 1] )
				eBands[i] -= (short)((2 * eBands[i] - eBands[i - 1] - eBands[i + 1]) / 2);
		}

		// Remove empty bands.
		var j = 0;
		for ( var i = 0; i < nbEBands; i++ )
		{
			if ( eBands[i + 1] > eBands[j] )
				eBands[++j] = eBands[i + 1];
		}

		nbEBands = j;
		return eBands;
	}

	byte[] ComputeAllocationTable()
	{
		var maxBands = EBand5Ms.Length - 1;
		var allocVectors = new byte[BitAllocSize * NbEBands];

		if ( SampleRate == 400 * ShortMdctSize )
		{
			Array.Copy( BandAllocation, allocVectors, allocVectors.Length );
			return allocVectors;
		}

		// Interpolate the per-critical-band table onto this mode's bands.
		for ( var i = 0; i < BitAllocSize; i++ )
		{
			for ( var j = 0; j < NbEBands; j++ )
			{
				int k;
				for ( k = 0; k < maxBands; k++ )
				{
					if ( 400 * EBand5Ms[k] > EBands[j] * SampleRate / ShortMdctSize )
						break;
				}

				if ( k > NbEBands - 1 )
				{
					allocVectors[i * NbEBands + j] = BandAllocation[i * maxBands + maxBands - 1];
				}
				else
				{
					var a1 = EBands[j] * SampleRate / ShortMdctSize - 400 * EBand5Ms[k - 1];
					var a0 = 400 * EBand5Ms[k] - EBands[j] * SampleRate / ShortMdctSize;
					allocVectors[i * NbEBands + j] = (byte)((a0 * BandAllocation[i * maxBands + k - 1] + a1 * BandAllocation[i * maxBands + k]) / (a0 + a1));
				}
			}
		}

		return allocVectors;
	}

	/// <summary>
	/// Determines if V(N,K) fits in a 32-bit unsigned integer.
	/// </summary>
	static bool FitsIn32( int n, int k )
	{
		ReadOnlySpan<short> maxN = [32767, 32767, 32767, 1476, 283, 109, 60, 40, 29, 24, 20, 18, 16, 14, 13];
		ReadOnlySpan<short> maxK = [32767, 32767, 32767, 32767, 1172, 238, 95, 53, 36, 27, 22, 18, 16, 15, 13];
		if ( n >= 14 )
			return k < 14 && n <= maxN[k];

		return k <= maxK[n];
	}

	public static int GetPulses( int i ) => i < 8 ? i : (8 + (i & 7)) << ((i >> 3) - 1);

	/// <summary>
	/// Caches the pulse count to bit cost mapping of every band size, and the maximum useful rate
	/// of each band.
	/// </summary>
	void ComputePulseCache( int lm )
	{
		var curr = 0;
		var entryN = new List<int>();
		var entryK = new List<int>();
		var entryI = new List<int>();

		var cindex = new short[NbEBands * (lm + 2)];
		CacheIndex = cindex;

		// Scan for all unique band sizes.
		for ( var i = 0; i <= lm + 1; i++ )
		{
			for ( var j = 0; j < NbEBands; j++ )
			{
				var n = (EBands[j + 1] - EBands[j]) << i >> 1;
				cindex[i * NbEBands + j] = -1;

				// Find other bands that have the same size.
				for ( var k = 0; k <= i; k++ )
				{
					for ( var m = 0; m < NbEBands && (k != i || m < j); m++ )
					{
						if ( n == (EBands[m + 1] - EBands[m]) << k >> 1 )
						{
							cindex[i * NbEBands + j] = cindex[k * NbEBands + m];
							break;
						}
					}
				}

				if ( cindex[i * NbEBands + j] == -1 && n != 0 )
				{
					var kk = 0;
					while ( FitsIn32( n, GetPulses( kk + 1 ) ) && kk < MaxPseudo )
						kk++;

					entryN.Add( n );
					entryK.Add( kk );
					entryI.Add( curr );
					cindex[i * NbEBands + j] = (short)curr;
					curr += kk + 1;
				}
			}
		}

		var bits = new byte[curr];
		CacheBits = bits;

		// Compute the cache for all unique sizes.
		var tmp = new short[MaxPulses + 1];
		for ( var i = 0; i < entryN.Count; i++ )
		{
			var ptr = entryI[i];
			GetRequiredBits( tmp, entryN[i], GetPulses( entryK[i] ), BitRes );
			for ( var j = 1; j <= entryK[i]; j++ )
				bits[ptr + j] = (byte)(tmp[GetPulses( j )] - 1);

			bits[ptr] = (byte)entryK[i];
		}

		// The maximum rate for each band at which we'll reliably use as many bits as we ask for.
		var caps = new byte[(lm + 1) * 2 * NbEBands];
		CacheCaps = caps;
		var capIndex = 0;
		for ( var i = 0; i <= lm; i++ )
		{
			for ( var c = 1; c <= 2; c++ )
			{
				for ( var j = 0; j < NbEBands; j++ )
				{
					var n0 = EBands[j + 1] - EBands[j];
					int maxBits;

					// N=1 bands only have a sign bit and fine bits.
					if ( n0 << i == 1 )
					{
						maxBits = c * (1 + MaxFineBits) << BitRes;
					}
					else
					{
						var lm0 = 0;

						// Even-sized bands bigger than N=2 can be split one more time.
						if ( n0 > 2 && (n0 & 1) == 0 )
						{
							n0 >>= 1;
							lm0--;
						}
						// N0=1 bands can't be split down to N<2.
						else if ( n0 <= 1 )
						{
							lm0 = Math.Min( i, 1 );
							n0 <<= lm0;
						}

						// The cost of the lowest-level PVQ of a fully split band.
						var pcache = cindex[(lm0 + 1) * NbEBands + j];
						maxBits = bits[pcache + bits[pcache]] + 1;

						// Add in the cost of coding regular splits.
						var n = n0;
						int offset, num, den, qb;
						for ( var k = 0; k < i - lm0; k++ )
						{
							maxBits <<= 1;
							offset = ((LogN[j] + ((lm0 + k) << BitRes)) >> 1) - QThetaOffset;
							num = 459 * ((2 * n - 1) * offset + maxBits);
							den = ((2 * n - 1) << 9) - 459;
							qb = Math.Min( (num + (den >> 1)) / den, 57 );
							maxBits += qb;
							n <<= 1;
						}

						// Add in the cost of a stereo split, if necessary.
						if ( c == 2 )
						{
							maxBits <<= 1;
							offset = ((LogN[j] + (i << BitRes)) >> 1) - (n == 2 ? QThetaOffsetTwoPhase : QThetaOffset);
							var ndof2 = 2 * n - 1 - (n == 2 ? 1 : 0);
							num = (n == 2 ? 512 : 487) * (maxBits + ndof2 * offset);
							den = (ndof2 << 9) - (n == 2 ? 512 : 487);
							qb = Math.Min( (num + (den >> 1)) / den, n == 2 ? 64 : 61 );
							maxBits += qb;
						}

						// Add the fine bits we'll use, compensating for the extra DoF in stereo.
						var ndof = c * n + ((c == 2 && n > 2) ? 1 : 0);
						offset = ((LogN[j] + (i << BitRes)) >> 1) - FineOffset;
						if ( n == 2 )
							offset += (1 << BitRes) >> 2;

						num = maxBits + ndof * offset;
						den = (ndof - 1) << BitRes;
						qb = Math.Min( (num + (den >> 1)) / den, MaxFineBits );
						maxBits += c * qb << BitRes;
					}

					maxBits = (4 * maxBits / (c * ((EBands[j + 1] - EBands[j]) << i))) - 64;
					caps[capIndex++] = (byte)maxBits;
				}
			}
		}
	}

	/// <summary>
	/// A conservatively large estimate of log2( <paramref name="val"/> ) with <paramref name="frac"/>
	/// fractional bits.
	/// </summary>
	public static int Log2Frac( uint val, int frac )
	{
		var l = RangeDecoder.ILog( val );
		if ( (val & (val - 1)) != 0 )
		{
			// This is (val >> l-16), but guaranteed to round up.
			if ( l > 16 ) val = (val >> (l - 16)) + (((val & ((1u << (l - 16)) - 1)) + (1u << (l - 16)) - 1) >> (l - 16));
			else val <<= 16 - l;

			l = (l - 1) << frac;
			do
			{
				var b = (int)(val >> 16);
				l += b << frac;
				val = (val + (uint)b) >> b;
				val = (val * val + 0x7FFF) >> 15;
			}
			while ( frac-- > 0 );

			return l + (val > 0x8000 ? 1 : 0);
		}

		// Exact powers of two require no rounding.
		return (l - 1) << frac;
	}

	static void GetRequiredBits( short[] bits, int n, int maxK, int frac )
	{
		bits[0] = 0;
		if ( n == 1 )
		{
			for ( var k = 1; k <= maxK; k++ )
				bits[k] = (short)(1 << frac);
		}
		else
		{
			var u = new uint[maxK + 2];
			Cwrs.NcwrsURow( n, maxK, u );
			for ( var k = 1; k <= maxK; k++ )
				bits[k] = (short)Log2Frac( u[k] + u[k + 1], frac );
		}
	}

	/// <summary>
	/// The largest pulse count index whose cost is closest to <paramref name="bits"/>.
	/// </summary>
	public int BitsToPulses( int band, int lm, int bits )
	{
		lm++;
		var cache = CacheIndex[lm * NbEBands + band];
		var lo = 0;
		int hi = CacheBits[cache];
		bits--;
		for ( var i = 0; i < LogMaxPseudo; i++ )
		{
			var mid = (lo + hi + 1) >> 1;
			if ( CacheBits[cache + mid] >= bits )
				hi = mid;
			else
				lo = mid;
		}

		return bits - (lo == 0 ? -1 : CacheBits[cache + lo]) <= CacheBits[cache + hi] - bits ? lo : hi;
	}

	public int PulsesToBits( int band, int lm, int pulses )
	{
		lm++;
		var cache = CacheIndex[lm * NbEBands + band];
		return pulses == 0 ? 0 : CacheBits[cache + pulses] + 1;
	}
}
