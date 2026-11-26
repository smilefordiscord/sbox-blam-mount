/* C# port of the decoder side of libcelt 0.11.0 (mdct.c: clt_mdct_backward). The FFT is a
   plain radix-2 one written for this port instead of kiss_fft.

   Copyright (c) 2007-2008 CSIRO
   Copyright (c) 2007-2008 Xiph.Org Foundation
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
/// CELT's inverse MDCT: pre-rotation, an N/4 point complex inverse FFT, post-rotation, then
/// windowed mirroring into the output with overlap-add for TDAC.
/// </summary>
sealed class Mdct
{
	readonly int _n;
	readonly float[] _trig;

	/// <summary>
	/// FFT twiddles exp(2 pi i k / (N/4)) for the largest FFT; smaller ones step through it.
	/// </summary>
	readonly float[] _twiddleCos;
	readonly float[] _twiddleSin;

	public Mdct( int n, int maxShift )
	{
		_n = n;
		if ( (n >> 2 >> maxShift) < 1 || ((n >> 2) & ((n >> 2) - 1)) != 0 )
			throw new ArgumentException( $"MDCT size {n} isn't supported (the FFT is radix-2 only)" );

		var n4 = n >> 2;
		_trig = new float[n4 + 1];
		for ( var i = 0; i <= n4; i++ )
			_trig[i] = (float)Math.Cos( 2 * Math.PI * i / n );

		_twiddleCos = new float[n4];
		_twiddleSin = new float[n4];
		for ( var i = 0; i < n4; i++ )
		{
			_twiddleCos[i] = (float)Math.Cos( 2 * Math.PI * i / n4 );
			_twiddleSin[i] = (float)Math.Sin( 2 * Math.PI * i / n4 );
		}
	}

	/// <summary>
	/// Inverse transforms <c>N/2 &gt;&gt; shift</c> coefficients from <paramref name="input"/> and
	/// overlap-adds the windowed result into <paramref name="output"/> starting at
	/// <paramref name="outOffset"/>. The first <paramref name="overlap"/> samples are added to what's
	/// there, the rest overwritten.
	/// </summary>
	public void Backward( ReadOnlySpan<float> input, float[] output, int outOffset, float[] window, int overlap, int shift )
	{
		var n = _n >> shift;
		var n2 = n >> 1;
		var n4 = n >> 2;
		var f = new float[n2];
		var f2 = new float[n2];

		// sin(x) ~= x here
		var sine = (float)(2 * Math.PI * 0.125 / n);

		// Pre-rotate.
		{
			var xp1 = 0;
			var xp2 = n2 - 1;
			var yp = 0;
			for ( var i = 0; i < n4; i++ )
			{
				var yr = -input[xp2] * _trig[i << shift] + input[xp1] * _trig[(n4 - i) << shift];
				var yi = -input[xp2] * _trig[(n4 - i) << shift] - input[xp1] * _trig[i << shift];
				// Works because the cos is nearly one.
				f2[yp++] = yr - yi * sine;
				f2[yp++] = yi + yr * sine;
				xp1 += 2;
				xp2 -= 2;
			}
		}

		// Inverse N/4 complex FFT, not scaled.
		InverseFft( f2, f, n4 );

		// Post-rotate.
		{
			var fp = 0;
			for ( var i = 0; i < n4; i++ )
			{
				var re = f[fp];
				var im = f[fp + 1];
				var yr = re * _trig[i << shift] - im * _trig[(n4 - i) << shift];
				var yi = im * _trig[i << shift] + re * _trig[(n4 - i) << shift];
				f[fp++] = yr - yi * sine;
				f[fp++] = yi + yr * sine;
			}
		}

		// De-shuffle the components for the middle of the window only.
		{
			var fp1 = 0;
			var fp2 = n2 - 1;
			var yp = 0;
			for ( var i = 0; i < n4; i++ )
			{
				f2[yp++] = -f[fp1];
				f2[yp++] = f[fp2];
				fp1 += 2;
				fp2 -= 2;
			}
		}

		var outBase = outOffset - ((n2 - overlap) >> 1);

		// Mirror on both sides for TDAC.
		{
			var fp1 = n4 - 1;
			var xp1 = outBase + n2 - 1;
			var yp1 = outBase + n4 - overlap / 2;
			var wp1 = 0;
			var wp2 = overlap - 1;
			int i;
			for ( i = 0; i < n4 - overlap / 2; i++ )
				output[xp1--] = f2[fp1--];

			for ( ; i < n4; i++ )
			{
				var x1 = f2[fp1--];
				output[yp1++] += -window[wp1] * x1;
				output[xp1--] += window[wp2] * x1;
				wp1++;
				wp2--;
			}
		}
		{
			var fp2 = n4;
			var xp2 = outBase + n2;
			var yp2 = outBase + n - 1 - (n4 - overlap / 2);
			var wp1 = 0;
			var wp2 = overlap - 1;
			int i;
			for ( i = 0; i < n4 - overlap / 2; i++ )
				output[xp2++] = f2[fp2++];

			for ( ; i < n4; i++ )
			{
				var x2 = f2[fp2++];
				output[yp2--] = window[wp1] * x2;
				output[xp2++] = window[wp2] * x2;
				wp1++;
				wp2--;
			}
		}
	}

	/// <summary>
	/// Unscaled inverse DFT (positive exponent) of <paramref name="count"/> interleaved complex values.
	/// </summary>
	void InverseFft( float[] input, float[] output, int count )
	{
		// Bit-reversed copy.
		var bits = RangeDecoder.ILog( (uint)count ) - 1;
		for ( var i = 0; i < count; i++ )
		{
			var r = bits == 0 ? 0 : (int)(ReverseBits( (uint)i ) >> (32 - bits));
			output[2 * r] = input[2 * i];
			output[2 * r + 1] = input[2 * i + 1];
		}

		for ( var size = 2; size <= count; size <<= 1 )
		{
			var half = size >> 1;
			var stride = _twiddleCos.Length / size;
			for ( var k = 0; k < half; k++ )
			{
				var wr = _twiddleCos[k * stride];
				var wi = _twiddleSin[k * stride];
				for ( var start = 0; start < count; start += size )
				{
					var a = 2 * (start + k);
					var b = 2 * (start + k + half);
					var tr = output[b] * wr - output[b + 1] * wi;
					var ti = output[b] * wi + output[b + 1] * wr;
					output[b] = output[a] - tr;
					output[b + 1] = output[a + 1] - ti;
					output[a] += tr;
					output[a + 1] += ti;
				}
			}
		}
	}

	static uint ReverseBits( uint v )
	{
		v = ((v >> 1) & 0x55555555) | ((v & 0x55555555) << 1);
		v = ((v >> 2) & 0x33333333) | ((v & 0x33333333) << 2);
		v = ((v >> 4) & 0x0F0F0F0F) | ((v & 0x0F0F0F0F) << 4);
		v = ((v >> 8) & 0x00FF00FF) | ((v & 0x00FF00FF) << 8);
		return (v >> 16) | (v << 16);
	}
}
