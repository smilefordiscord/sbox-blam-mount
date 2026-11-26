/* C# port of the decoder side of libcelt 0.11.0 (bands.c, vq.c), floating point.

   Copyright (c) 2007-2008 CSIRO
   Copyright (c) 2007-2009 Xiph.Org Foundation
   Copyright (c) 2008-2009 Gregory Maxwell
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

namespace Blam.Audio.Celt;

/// <summary>
/// Decoding of the normalized band shapes (PVQ with recursive splitting, folding, stereo) and
/// the energy denormalization. Pointers in the C code become spans; an empty span stands for
/// NULL, which is never a real band since every band has at least one coefficient.
/// </summary>
static class Bands
{
	const int BitRes = RangeDecoder.BitRes;

	public const int SpreadNone = 0;
	public const int SpreadLight = 1;
	public const int SpreadNormal = 2;
	public const int SpreadAggressive = 3;

	public static uint LcgRand( uint seed ) => 1664525 * seed + 1013904223;

	/// <summary>
	/// Multiplies two Q15 values the way the C code does, with 16 bit truncation of both inputs.
	/// </summary>
	static int FracMul16( int a, int b ) => (16384 + (short)a * (short)b) >> 15;

	/// <summary>
	/// A cos() approximation that is bit-exact on every platform, since it affects bit allocation.
	/// </summary>
	static int BitexactCos( int x )
	{
		var tmp = (4096 + x * x) >> 13;
		if ( tmp > 32767 )
			tmp = 32767;

		var x2 = (short)tmp;
		x2 = (short)((32767 - x2) + FracMul16( x2, -7651 + FracMul16( x2, 8277 + FracMul16( -626, x2 ) ) ));
		if ( x2 > 32766 )
			x2 = 32766;

		return 1 + x2;
	}

	static int BitexactLog2Tan( int isin, int icos )
	{
		var lc = RangeDecoder.ILog( (uint)icos );
		var ls = RangeDecoder.ILog( (uint)isin );
		icos <<= 15 - lc;
		isin <<= 15 - ls;
		return ((ls - lc) << 11)
			+ FracMul16( isin, FracMul16( isin, -2597 ) + 7932 )
			- FracMul16( icos, FracMul16( icos, -2597 ) + 7932 );
	}

	static uint Isqrt32( uint val )
	{
		uint g = 0;
		var bshift = (RangeDecoder.ILog( val ) - 1) >> 1;
		var b = 1u << bshift;
		do
		{
			var t = ((g << 1) + b) << bshift;
			if ( t <= val )
			{
				g += b;
				val -= t;
			}

			b >>= 1;
			bshift--;
		}
		while ( bshift >= 0 );

		return g;
	}

	/// <summary>
	/// Scales the unit-norm bands back up to their decoded energies.
	/// </summary>
	public static void DenormaliseBands( CeltMode m, ReadOnlySpan<float> x, Span<float> freq, ReadOnlySpan<float> bandE, int end, int c, int bigM )
	{
		var eBands = m.EBands;
		var n = bigM * m.ShortMdctSize;
		for ( var ch = 0; ch < c; ch++ )
		{
			var f = freq.Slice( ch * n, n );
			var xs = x.Slice( ch * n, n );
			var j = 0;
			for ( var i = 0; i < end; i++ )
			{
				var g = bandE[i + ch * m.NbEBands];
				var bandEnd = bigM * eBands[i + 1];
				for ( j = bigM * eBands[i]; j < bandEnd; j++ )
					f[j] = xs[j] * g;
			}

			f[(bigM * eBands[end])..].Clear();
		}
	}

	/// <summary>
	/// Prevents energy collapse in transients with several short MDCTs by filling blocks that got
	/// no pulses with noise.
	/// </summary>
	public static void AntiCollapse( CeltMode m, Span<float> x, ReadOnlySpan<byte> collapseMasks, int lm, int c, int cc, int size,
		int start, int end, ReadOnlySpan<float> logE, ReadOnlySpan<float> prev1LogE, ReadOnlySpan<float> prev2LogE, ReadOnlySpan<int> pulses, uint seed )
	{
		for ( var i = start; i < end; i++ )
		{
			var n0 = m.EBands[i + 1] - m.EBands[i];
			// Depth in 1/8 bits.
			var depth = (1 + pulses[i]) / ((m.EBands[i + 1] - m.EBands[i]) << lm);
			var thresh = 0.5f * MathF.Exp( 0.6931471805599453094f * (-0.125f * depth) );
			var sqrt1 = 1f / MathF.Sqrt( n0 << lm );

			for ( var ch = 0; ch < c; ch++ )
			{
				var prev1 = prev1LogE[ch * m.NbEBands + i];
				var prev2 = prev2LogE[ch * m.NbEBands + i];
				if ( c < cc )
				{
					prev1 = MathF.Max( prev1, prev1LogE[m.NbEBands + i] );
					prev2 = MathF.Max( prev2, prev2LogE[m.NbEBands + i] );
				}

				var ediff = logE[ch * m.NbEBands + i] - MathF.Min( prev1, prev2 );
				ediff = MathF.Max( 0, ediff );

				// r needs to be multiplied by 2 or 2*sqrt(2) depending on LM because short blocks
				// don't have the same energy as long.
				var r = 2f * MathF.Exp( 0.6931471805599453094f * -ediff );
				if ( lm == 3 )
					r *= 1.41421356f;
				r = MathF.Min( thresh, r );
				r *= sqrt1;

				var xs = x[(ch * size + (m.EBands[i] << lm))..];
				var renormalize = false;
				for ( var k = 0; k < 1 << lm; k++ )
				{
					// Detect collapse.
					if ( (collapseMasks[i * c + ch] & 1 << k) == 0 )
					{
						// Fill with noise.
						for ( var j = 0; j < n0; j++ )
						{
							seed = LcgRand( seed );
							xs[(j << lm) + k] = (seed & 0x8000) != 0 ? r : -r;
						}

						renormalize = true;
					}
				}

				// We just added some energy, so we need to renormalise.
				if ( renormalize )
					RenormaliseVector( xs, n0 << lm, 1f );
			}
		}
	}

	static void StereoMerge( Span<float> x, Span<float> y, float mid, int n )
	{
		float xp = 0, side = 0;
		for ( var j = 0; j < n; j++ )
		{
			xp += x[j] * y[j];
			side += y[j] * y[j];
		}

		// Compensating for the mid normalization.
		xp *= mid;
		var mid2 = mid;
		var el = mid2 * mid2 + side - 2 * xp;
		var er = mid2 * mid2 + side + 2 * xp;
		if ( er < 6e-4f || el < 6e-4f )
		{
			x[..n].CopyTo( y );
			return;
		}

		var lgain = 1f / MathF.Sqrt( el );
		var rgain = 1f / MathF.Sqrt( er );
		for ( var j = 0; j < n; j++ )
		{
			// Apply mid scaling (side is already scaled).
			var l = mid * x[j];
			var r = y[j];
			x[j] = lgain * (l - r);
			y[j] = rgain * (l + r);
		}
	}

	/// <summary>
	/// Converts from natural Hadamard to ordery Hadamard order: a bit-reversed Gray code, reversed
	/// so DC comes last. Rows for N=2, 4, 8, 16.
	/// </summary>
	static readonly int[] OrderyTable =
	[
		1, 0,
		3, 0, 2, 1,
		7, 0, 4, 3, 6, 1, 5, 2,
		15, 0, 8, 7, 12, 3, 11, 4, 14, 1, 9, 6, 13, 2, 10, 5,
	];

	static void DeinterleaveHadamard( Span<float> x, int n0, int stride, bool hadamard )
	{
		var n = n0 * stride;
		Span<float> tmp = n <= 1024 ? stackalloc float[n] : new float[n];
		if ( hadamard )
		{
			var ordery = stride - 2;
			for ( var i = 0; i < stride; i++ )
				for ( var j = 0; j < n0; j++ )
					tmp[OrderyTable[ordery + i] * n0 + j] = x[j * stride + i];
		}
		else
		{
			for ( var i = 0; i < stride; i++ )
				for ( var j = 0; j < n0; j++ )
					tmp[i * n0 + j] = x[j * stride + i];
		}

		tmp.CopyTo( x );
	}

	static void InterleaveHadamard( Span<float> x, int n0, int stride, bool hadamard )
	{
		var n = n0 * stride;
		Span<float> tmp = n <= 1024 ? stackalloc float[n] : new float[n];
		if ( hadamard )
		{
			var ordery = stride - 2;
			for ( var i = 0; i < stride; i++ )
				for ( var j = 0; j < n0; j++ )
					tmp[j * stride + i] = x[OrderyTable[ordery + i] * n0 + j];
		}
		else
		{
			for ( var i = 0; i < stride; i++ )
				for ( var j = 0; j < n0; j++ )
					tmp[j * stride + i] = x[i * n0 + j];
		}

		tmp.CopyTo( x );
	}

	static void Haar1( Span<float> x, int n0, int stride )
	{
		n0 >>= 1;
		for ( var i = 0; i < stride; i++ )
		{
			for ( var j = 0; j < n0; j++ )
			{
				var tmp1 = 0.70710678f * x[stride * 2 * j + i];
				var tmp2 = 0.70710678f * x[stride * (2 * j + 1) + i];
				x[stride * 2 * j + i] = tmp1 + tmp2;
				x[stride * (2 * j + 1) + i] = tmp1 - tmp2;
			}
		}
	}

	static int ComputeQn( int n, int b, int offset, int pulseCap, bool stereo )
	{
		ReadOnlySpan<short> exp2Table8 = [16384, 17866, 19483, 21247, 23170, 25267, 27554, 30048];
		var n2 = 2 * n - 1;
		if ( stereo && n == 2 )
			n2--;

		// The upper limit ensures that in a stereo split with itheta==16384, we'll always have
		// enough bits left over to code at least one pulse in the side.
		var qb = Math.Min( b - pulseCap - (4 << BitRes), (b + n2 * offset) / n2 );
		qb = Math.Min( 8 << BitRes, qb );

		if ( qb < (1 << BitRes >> 1) )
			return 1;

		var qn = exp2Table8[qb & 0x7] >> (14 - (qb >> BitRes));
		return (qn + 1) >> 1 << 1;
	}

	/// <summary>
	/// Shared state of one frame's band decoding.
	/// </summary>
	sealed class BandContext
	{
		public CeltMode Mode;
		public RangeDecoder Dec;
		public int Spread;
		public int Intensity;
		public int RemainingBits;
		public uint Seed;
		public float[] LowbandScratch;
	}

	/// <summary>
	/// Decodes one band, mono or stereo. It can split the band in two and code the energy
	/// difference between the halves, recursively, so a band can end up in 8 parts.
	/// </summary>
	static uint QuantBand( BandContext ctx, int i, Span<float> x, Span<float> y, int n, int b, int bigB, int tfChange,
		Span<float> lowband, int lm, Span<float> lowbandOut, int level, float gain, Span<float> lowbandScratch, int fill )
	{
		var m = ctx.Mode;
		var dec = ctx.Dec;
		var n0 = n;
		var nB = n;
		var b0 = bigB;
		var timeDivide = 0;
		var recombine = 0;
		var inv = false;
		float mid = 0, side = 0;
		uint cm = 0;

		var longBlocks = b0 == 1;

		nB /= bigB;
		var nB0 = nB;

		var stereo = !y.IsEmpty;
		var split = stereo;

		// Special case for one sample.
		if ( n == 1 )
		{
			var xs = x;
			var ch = 0;
			do
			{
				var sign = 0;
				if ( ctx.RemainingBits >= 1 << BitRes )
				{
					sign = (int)dec.DecodeBits( 1 );
					ctx.RemainingBits -= 1 << BitRes;
					b -= 1 << BitRes;
				}

				xs[0] = sign != 0 ? -1f : 1f;
				xs = y;
			}
			while ( ++ch < 1 + (stereo ? 1 : 0) );

			if ( !lowbandOut.IsEmpty )
				lowbandOut[0] = x[0];

			return 1;
		}

		if ( !stereo && level == 0 )
		{
			if ( tfChange > 0 )
				recombine = tfChange;

			// Band recombining to increase frequency resolution.
			if ( !lowband.IsEmpty && (recombine != 0 || ((nB & 1) == 0 && tfChange < 0) || b0 > 1) )
			{
				lowband[..n].CopyTo( lowbandScratch );
				lowband = lowbandScratch[..n];
			}

			ReadOnlySpan<byte> bitInterleaveTable = [0, 1, 1, 1, 2, 3, 3, 3, 2, 3, 3, 3, 2, 3, 3, 3];
			for ( var k = 0; k < recombine; k++ )
			{
				if ( !lowband.IsEmpty )
					Haar1( lowband, n >> k, 1 << k );

				fill = bitInterleaveTable[fill & 0xF] | bitInterleaveTable[fill >> 4] << 2;
			}

			bigB >>= recombine;
			nB <<= recombine;

			// Increasing the time resolution.
			while ( (nB & 1) == 0 && tfChange < 0 )
			{
				if ( !lowband.IsEmpty )
					Haar1( lowband, nB, bigB );

				fill |= fill << bigB;
				bigB <<= 1;
				nB >>= 1;
				timeDivide++;
				tfChange++;
			}

			b0 = bigB;
			nB0 = nB;

			// Reorganize the samples in time order instead of frequency order.
			if ( b0 > 1 && !lowband.IsEmpty )
				DeinterleaveHadamard( lowband, nB >> recombine, b0 << recombine, longBlocks );
		}

		// If we need 1.5 more bit than we can produce, split the band in two.
		var cache = m.CacheIndex[(lm + 1) * m.NbEBands + i];
		if ( !stereo && lm != -1 && b > m.CacheBits[cache + m.CacheBits[cache]] + 12 && n > 2 )
		{
			if ( lm > 0 || (n & 1) == 0 )
			{
				n >>= 1;
				y = x.Slice( n );
				split = true;
				lm -= 1;
				if ( bigB == 1 )
					fill = (fill & 1) | (fill << 1);

				bigB = (bigB + 1) >> 1;
			}
		}

		if ( split )
		{
			var itheta = 0;
			int mbits, sbits, delta;

			// Decide on the resolution to give to the split parameter theta.
			var pulseCap = m.LogN[i] + (lm << BitRes);
			var offset = (pulseCap >> 1) - (stereo && n == 2 ? CeltMode.QThetaOffsetTwoPhase : CeltMode.QThetaOffset);
			var qn = ComputeQn( n, b, offset, pulseCap, stereo );
			if ( stereo && i >= ctx.Intensity )
				qn = 1;

			var tell = (int)dec.TellFrac();
			if ( qn != 1 )
			{
				// Entropy coding of the angle: a uniform pdf for the time split, a step for
				// stereo, and a triangular one for the rest.
				if ( stereo && n > 2 )
				{
					const int p0 = 3;
					var x0 = qn / 2;
					var ft = p0 * (x0 + 1) + x0;
					var fs = (int)dec.Decode( (uint)ft );
					var xv = fs < (x0 + 1) * p0 ? fs / p0 : x0 + 1 + (fs - (x0 + 1) * p0);
					dec.Update(
						(uint)(xv <= x0 ? p0 * xv : (xv - 1 - x0) + (x0 + 1) * p0),
						(uint)(xv <= x0 ? p0 * (xv + 1) : (xv - x0) + (x0 + 1) * p0),
						(uint)ft );
					itheta = xv;
				}
				else if ( b0 > 1 || stereo )
				{
					itheta = (int)dec.DecodeUint( (uint)(qn + 1) );
				}
				else
				{
					int fs, fl;
					var ft = ((qn >> 1) + 1) * ((qn >> 1) + 1);
					var fm = (int)dec.Decode( (uint)ft );
					if ( fm < ((qn >> 1) * ((qn >> 1) + 1) >> 1) )
					{
						itheta = (int)(Isqrt32( 8 * (uint)fm + 1 ) - 1) >> 1;
						fs = itheta + 1;
						fl = itheta * (itheta + 1) >> 1;
					}
					else
					{
						itheta = (int)(2 * (qn + 1) - Isqrt32( 8 * (uint)(ft - fm - 1) + 1 )) >> 1;
						fs = qn + 1 - itheta;
						fl = ft - ((qn + 1 - itheta) * (qn + 2 - itheta) >> 1);
					}

					dec.Update( (uint)fl, (uint)(fl + fs), (uint)ft );
				}

				itheta = itheta * 16384 / qn;
			}
			else if ( stereo )
			{
				if ( b > 2 << BitRes && ctx.RemainingBits > 2 << BitRes )
					inv = dec.DecodeBitLogp( 2 ) != 0;
				else
					inv = false;

				itheta = 0;
			}

			var qalloc = (int)dec.TellFrac() - tell;
			b -= qalloc;

			var origFill = fill;
			int imid, iside;
			if ( itheta == 0 )
			{
				imid = 32767;
				iside = 0;
				fill &= (1 << bigB) - 1;
				delta = -16384;
			}
			else if ( itheta == 16384 )
			{
				imid = 0;
				iside = 32767;
				fill &= ((1 << bigB) - 1) << bigB;
				delta = 16384;
			}
			else
			{
				imid = BitexactCos( itheta );
				iside = BitexactCos( 16384 - itheta );
				// The mid vs side allocation that minimizes squared error in that band.
				delta = FracMul16( (n - 1) << 7, BitexactLog2Tan( iside, imid ) );
			}

			mid = (1f / 32768) * imid;
			side = (1f / 32768) * iside;

			// Special case for N=2 that only works for stereo and takes advantage of the fact that
			// mid and side are orthogonal to code the side with just one bit.
			if ( n == 2 && stereo )
			{
				var sign = 0;
				mbits = b;
				sbits = 0;
				// Only need one bit for the side.
				if ( itheta != 0 && itheta != 16384 )
					sbits = 1 << BitRes;

				mbits -= sbits;
				var c = itheta > 8192;
				ctx.RemainingBits -= qalloc + sbits;

				var x2 = c ? y : x;
				var y2 = c ? x : y;
				if ( sbits != 0 )
					sign = (int)dec.DecodeBits( 1 );

				sign = 1 - 2 * sign;
				// orig_fill is used here because we want to fold the side, but if itheta==16384,
				// the low bits of fill have been cleared.
				cm = QuantBand( ctx, i, x2, Span<float>.Empty, n, mbits, bigB, tfChange, lowband, lm, lowbandOut, level, gain, lowbandScratch, origFill );
				// N=2 bands aren't split, so cm is either 1 or 0 (for a fold-collapse), and
				// there's no need to worry about mixing with the other channel.
				y2[0] = -sign * x2[1];
				y2[1] = sign * x2[0];

				x[0] = mid * x[0];
				x[1] = mid * x[1];
				y[0] = side * y[0];
				y[1] = side * y[1];
				var tmp = x[0];
				x[0] = tmp - y[0];
				y[0] = tmp + y[0];
				tmp = x[1];
				x[1] = tmp - y[1];
				y[1] = tmp + y[1];
			}
			else
			{
				// "Normal" split code.
				var nextLowband2 = Span<float>.Empty;
				var nextLowbandOut1 = Span<float>.Empty;
				var nextLevel = 0;

				// Give more bits to low-energy MDCTs than they would otherwise deserve.
				if ( b0 > 1 && !stereo && (itheta & 0x3fff) != 0 )
				{
					if ( itheta > 8192 )
						// Rough approximation for pre-echo masking.
						delta -= delta >> (4 - lm);
					else
						// Corresponds to a forward-masking slope of 1.5 dB per 10 ms.
						delta = Math.Min( 0, delta + (n << BitRes >> (5 - lm)) );
				}

				mbits = Math.Max( 0, Math.Min( b, (b - delta) / 2 ) );
				sbits = b - mbits;
				ctx.RemainingBits -= qalloc;

				if ( !lowband.IsEmpty && !stereo )
					nextLowband2 = lowband.Slice( n );

				// Only stereo needs to pass on lowband_out. Otherwise, it's handled at the end.
				if ( stereo )
					nextLowbandOut1 = lowbandOut;
				else
					nextLevel = level + 1;

				var sideShift = stereo ? 0 : b0 >> 1;
				var rebalance = ctx.RemainingBits;
				if ( mbits >= sbits )
				{
					// In stereo mode, the mid isn't scaled because the normalized mid is needed for
					// folding later.
					cm = QuantBand( ctx, i, x, Span<float>.Empty, n, mbits, bigB, tfChange, lowband, lm, nextLowbandOut1,
						nextLevel, stereo ? 1f : gain * mid, lowbandScratch, fill );
					rebalance = mbits - (rebalance - ctx.RemainingBits);
					if ( rebalance > 3 << BitRes && itheta != 0 )
						sbits += rebalance - (3 << BitRes);

					// For a stereo split, the high bits of fill are always zero, so no folding
					// will be done to the side.
					cm |= QuantBand( ctx, i, y, Span<float>.Empty, n, sbits, bigB, tfChange, nextLowband2, lm, Span<float>.Empty,
						nextLevel, gain * side, Span<float>.Empty, fill >> bigB ) << sideShift;
				}
				else
				{
					cm = QuantBand( ctx, i, y, Span<float>.Empty, n, sbits, bigB, tfChange, nextLowband2, lm, Span<float>.Empty,
						nextLevel, gain * side, Span<float>.Empty, fill >> bigB ) << sideShift;
					rebalance = sbits - (rebalance - ctx.RemainingBits);
					if ( rebalance > 3 << BitRes && itheta != 16384 )
						mbits += rebalance - (3 << BitRes);

					cm |= QuantBand( ctx, i, x, Span<float>.Empty, n, mbits, bigB, tfChange, lowband, lm, nextLowbandOut1,
						nextLevel, stereo ? 1f : gain * mid, lowbandScratch, fill );
				}
			}
		}
		else
		{
			// The basic no-split case.
			var q = m.BitsToPulses( i, lm, b );
			var currBits = m.PulsesToBits( i, lm, q );
			ctx.RemainingBits -= currBits;

			// Ensures we can never bust the budget.
			while ( ctx.RemainingBits < 0 && q > 0 )
			{
				ctx.RemainingBits += currBits;
				q--;
				currBits = m.PulsesToBits( i, lm, q );
				ctx.RemainingBits -= currBits;
			}

			if ( q != 0 )
			{
				var k = CeltMode.GetPulses( q );
				cm = AlgUnquant( x, n, k, ctx.Spread, bigB, ctx.Dec, gain );
			}
			else
			{
				// If there's no pulse, fill the band anyway.
				var cmMask = (uint)((1UL << bigB) - 1);
				fill &= (int)cmMask;
				if ( fill == 0 )
				{
					x[..n].Clear();
				}
				else
				{
					if ( lowband.IsEmpty )
					{
						// Noise.
						for ( var j = 0; j < n; j++ )
						{
							ctx.Seed = LcgRand( ctx.Seed );
							x[j] = (int)ctx.Seed >> 20;
						}

						cm = cmMask;
					}
					else
					{
						// Folded spectrum.
						lowband[..n].CopyTo( x );
						cm = (uint)fill;
					}

					RenormaliseVector( x, n, gain );
				}
			}
		}

		if ( stereo )
		{
			if ( n != 2 )
				StereoMerge( x, y, mid, n );

			if ( inv )
			{
				for ( var j = 0; j < n; j++ )
					y[j] = -y[j];
			}
		}
		else if ( level == 0 )
		{
			// Undo the sample reorganization going from time order to frequency order.
			if ( b0 > 1 )
				InterleaveHadamard( x, nB >> recombine, b0 << recombine, longBlocks );

			// Undo time-freq changes that we did earlier.
			nB = nB0;
			bigB = b0;
			for ( var k = 0; k < timeDivide; k++ )
			{
				bigB >>= 1;
				nB <<= 1;
				cm |= cm >> bigB;
				Haar1( x, nB, bigB );
			}

			ReadOnlySpan<byte> bitDeinterleaveTable = [0x00, 0x03, 0x0C, 0x0F, 0x30, 0x33, 0x3C, 0x3F, 0xC0, 0xC3, 0xCC, 0xCF, 0xF0, 0xF3, 0xFC, 0xFF];
			for ( var k = 0; k < recombine; k++ )
			{
				cm = bitDeinterleaveTable[(int)cm];
				Haar1( x, n0 >> k, 1 << k );
			}

			bigB <<= recombine;

			// Scale output for later folding.
			if ( !lowbandOut.IsEmpty )
			{
				var scale = MathF.Sqrt( n0 );
				for ( var j = 0; j < n0; j++ )
					lowbandOut[j] = scale * x[j];
			}

			cm &= (uint)((1 << bigB) - 1);
		}

		return cm;
	}

	/// <summary>
	/// Decodes the normalized shape of every band.
	/// </summary>
	public static void QuantAllBands( CeltMode m, int start, int end, Span<float> xAll, Span<float> yAll, Span<byte> collapseMasks,
		ReadOnlySpan<int> pulses, int shortBlocks, int spread, bool dualStereo, int intensity, ReadOnlySpan<int> tfRes,
		int totalBits, int balance, RangeDecoder dec, int lm, int codedBands, ref uint seed )
	{
		var eBands = m.EBands;
		var updateLowband = true;
		var c = yAll.IsEmpty ? 1 : 2;

		var bigM = 1 << lm;
		var bigB = shortBlocks != 0 ? bigM : 1;
		var normAll = new float[c * bigM * eBands[m.NbEBands]];
		var norm = normAll.AsSpan( 0, bigM * eBands[m.NbEBands] );
		var norm2 = normAll.AsSpan( bigM * eBands[m.NbEBands] );
		var ctx = new BandContext
		{
			Mode = m,
			Dec = dec,
			Spread = spread,
			Intensity = intensity,
			Seed = seed,
			LowbandScratch = new float[bigM * (eBands[m.NbEBands] - eBands[m.NbEBands - 1])],
		};

		var lowbandOffset = 0;
		for ( var i = start; i < end; i++ )
		{
			var effectiveLowband = -1;
			uint xCm, yCm;

			var x = xAll.Slice( bigM * eBands[i] );
			var y = yAll.IsEmpty ? Span<float>.Empty : yAll.Slice( bigM * eBands[i] );
			var n = bigM * eBands[i + 1] - bigM * eBands[i];
			var tell = (int)dec.TellFrac();

			// Compute how many bits we want to allocate to this band.
			if ( i != start )
				balance -= tell;

			ctx.RemainingBits = totalBits - tell - 1;
			int b;
			if ( i <= codedBands - 1 )
			{
				var currBalance = balance / Math.Min( 3, codedBands - i );
				b = Math.Max( 0, Math.Min( 16383, Math.Min( ctx.RemainingBits + 1, pulses[i] + currBalance ) ) );
			}
			else
			{
				b = 0;
			}

			if ( bigM * eBands[i] - n >= bigM * eBands[start] && (updateLowband || lowbandOffset == 0) )
				lowbandOffset = i;

			var tfChange = tfRes[i];
			if ( i >= m.EffEBands )
			{
				x = norm;
				if ( !yAll.IsEmpty )
					y = norm;
			}

			// Get a conservative estimate of the collapse masks of the bands we're going to be
			// folding from.
			if ( lowbandOffset != 0 && (spread != SpreadAggressive || bigB > 1 || tfChange < 0) )
			{
				// This ensures we never repeat spectral content within one band.
				effectiveLowband = Math.Max( bigM * eBands[start], bigM * eBands[lowbandOffset] - n );
				var foldStart = lowbandOffset;
				while ( bigM * eBands[--foldStart] > effectiveLowband ) { }

				var foldEnd = lowbandOffset - 1;
				while ( bigM * eBands[++foldEnd] < effectiveLowband + n ) { }

				xCm = yCm = 0;
				var foldI = foldStart;
				do
				{
					xCm |= collapseMasks[foldI * c + 0];
					yCm |= collapseMasks[foldI * c + c - 1];
				}
				while ( ++foldI < foldEnd );
			}
			else
			{
				// Otherwise the LCG is used to fold, so all blocks will (almost always) be non-zero.
				xCm = yCm = (uint)((1 << bigB) - 1);
			}

			if ( dualStereo && i == intensity )
			{
				// Switch off dual stereo to do intensity.
				dualStereo = false;
				for ( var j = bigM * eBands[start]; j < bigM * eBands[i]; j++ )
					norm[j] = 0.5f * (norm[j] + norm2[j]);
			}

			var lowbandX = effectiveLowband != -1 ? norm.Slice( effectiveLowband ) : Span<float>.Empty;
			if ( dualStereo )
			{
				var lowbandY = effectiveLowband != -1 ? norm2.Slice( effectiveLowband ) : Span<float>.Empty;
				xCm = QuantBand( ctx, i, x, Span<float>.Empty, n, b / 2, bigB, tfChange, lowbandX, lm,
					norm.Slice( bigM * eBands[i] ), 0, 1f, ctx.LowbandScratch, (int)xCm );
				yCm = QuantBand( ctx, i, y, Span<float>.Empty, n, b / 2, bigB, tfChange, lowbandY, lm,
					norm2.Slice( bigM * eBands[i] ), 0, 1f, ctx.LowbandScratch, (int)yCm );
			}
			else
			{
				xCm = QuantBand( ctx, i, x, y, n, b, bigB, tfChange, lowbandX, lm,
					norm.Slice( bigM * eBands[i] ), 0, 1f, ctx.LowbandScratch, (int)(xCm | yCm) );
				yCm = xCm;
			}

			collapseMasks[i * c + 0] = (byte)xCm;
			collapseMasks[i * c + c - 1] = (byte)yCm;
			balance += pulses[i] + tell;

			// Update the folding position only as long as we have 1 bit/sample depth.
			updateLowband = b > n << BitRes;
		}

		seed = ctx.Seed;
	}

	static void ExpRotation1( Span<float> x, int len, int stride, float c, float s )
	{
		var p = 0;
		for ( var i = 0; i < len - stride; i++ )
		{
			var x1 = x[p];
			var x2 = x[p + stride];
			x[p + stride] = c * x2 + s * x1;
			x[p++] = c * x1 - s * x2;
		}

		p = len - 2 * stride - 1;
		for ( var i = len - 2 * stride - 1; i >= 0; i-- )
		{
			var x1 = x[p];
			var x2 = x[p + stride];
			x[p + stride] = c * x2 + s * x1;
			x[p--] = c * x1 - s * x2;
		}
	}

	static void ExpRotation( Span<float> x, int len, int dir, int stride, int k, int spread )
	{
		ReadOnlySpan<int> spreadFactor = [5, 10, 15];

		if ( 2 * k >= len || spread == SpreadNone )
			return;

		var factor = spreadFactor[spread - 1];
		var gain = (float)len / (len + factor * k);
		var theta = 0.5f * (gain * gain);
		var c = MathF.Cos( 0.5f * MathF.PI * theta );
		var s = MathF.Cos( 0.5f * MathF.PI * (1f - theta) ); // sin(theta)

		var stride2 = 0;
		if ( len >= 8 * stride )
		{
			stride2 = 1;
			// sqrt(len/stride) with rounding.
			while ( (stride2 * stride2 + stride2) * stride + (stride >> 2) < len )
				stride2++;
		}

		len /= stride;
		for ( var i = 0; i < stride; i++ )
		{
			var xs = x.Slice( i * len );
			if ( dir < 0 )
			{
				if ( stride2 != 0 )
					ExpRotation1( xs, len, stride2, s, c );

				ExpRotation1( xs, len, 1, c, s );
			}
			else
			{
				ExpRotation1( xs, len, 1, c, -s );
				if ( stride2 != 0 )
					ExpRotation1( xs, len, stride2, s, -c );
			}
		}
	}

	static uint ExtractCollapseMask( ReadOnlySpan<int> iy, int n, int b )
	{
		if ( b <= 1 )
			return 1;

		var n0 = n / b;
		uint collapseMask = 0;
		var i = 0;
		do
		{
			var j = 0;
			do
			{
				collapseMask |= (iy[i * n0 + j] != 0 ? 1u : 0u) << i;
			}
			while ( ++j < n0 );
		}
		while ( ++i < b );

		return collapseMask;
	}

	/// <summary>
	/// Decodes a pulse vector and scales it to the band's gain.
	/// </summary>
	static uint AlgUnquant( Span<float> x, int n, int k, int spread, int b, RangeDecoder dec, float gain )
	{
		Span<int> iy = n <= 512 ? stackalloc int[n] : new int[n];
		Cwrs.DecodePulses( iy, n, k, dec );

		float ryy = 0;
		for ( var i = 0; i < n; i++ )
			ryy += iy[i] * (float)iy[i];

		// Normalise the residual.
		var g = gain / MathF.Sqrt( ryy );
		for ( var i = 0; i < n; i++ )
			x[i] = g * iy[i];

		ExpRotation( x, n, -1, b, k, spread );
		return ExtractCollapseMask( iy, n, b );
	}

	public static void RenormaliseVector( Span<float> x, int n, float gain )
	{
		var e = 1e-15f;
		for ( var i = 0; i < n; i++ )
			e += x[i] * x[i];

		var g = gain / MathF.Sqrt( e );
		for ( var i = 0; i < n; i++ )
			x[i] *= g;
	}
}
