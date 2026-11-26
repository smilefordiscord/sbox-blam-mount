/* C# port of the decoder side of libcelt 0.11.0 (cwrs.c).

   Copyright (c) 2007-2008 CSIRO
   Copyright (c) 2007-2009 Xiph.Org Foundation
   Copyright (c) 2007-2009 Timothy B. Terriberry
   Written by Timothy B. Terriberry and Jean-Marc Valin

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
/// Decoding of pyramid vector quantizer codewords: an index into the codebook of all
/// N-dimensional integer vectors with K pulses (sum of absolute values) back into the vector.
/// Uses the generic O(NK) row recurrence of U(N,K) for every size.
/// </summary>
static class Cwrs
{
	/// <summary>
	/// Computes the next row of a recurrence u[i][j] = u[i-1][j] + u[i][j-1] + u[i-1][j-1].
	/// </summary>
	static void UNext( Span<uint> ui, int len, uint ui0 )
	{
		var j = 1;
		do
		{
			var ui1 = ui[j] + ui[j - 1] + ui0;
			ui[j - 1] = ui0;
			ui0 = ui1;
		}
		while ( ++j < len );

		ui[j - 1] = ui0;
	}

	/// <summary>
	/// Computes the previous row of the same recurrence.
	/// </summary>
	static void UPrev( Span<uint> ui, int n, uint ui0 )
	{
		var j = 1;
		do
		{
			var ui1 = ui[j] - ui[j - 1] - ui0;
			ui[j - 1] = ui0;
			ui0 = ui1;
		}
		while ( ++j < n );

		ui[j - 1] = ui0;
	}

	/// <summary>
	/// Computes V(n,k), and U(n,0..k+1) into <paramref name="u"/>.
	/// </summary>
	public static uint NcwrsURow( int n, int k, Span<uint> u )
	{
		var len = k + 2;
		u[0] = 0;
		u[1] = 1;
		for ( var i = 2; i < len; i++ )
			u[i] = (uint)((i << 1) - 1);

		for ( var i = 2; i < n; i++ )
			UNext( u[1..], k + 1, 1 );

		return u[k] + u[k + 1];
	}

	/// <summary>
	/// Returns the i'th combination of k pulses in n dimensions with signs.
	/// </summary>
	static void Cwrsi( int n, int k, uint i, Span<int> y, Span<uint> u )
	{
		var j = 0;
		do
		{
			var p = u[k + 1];
			var s = i >= p ? -1 : 0;
			i -= p & (uint)s;
			var yj = k;
			p = u[k];
			while ( p > i )
				p = u[--k];

			i -= p;
			yj -= k;
			y[j] = (yj + s) ^ s;
			UPrev( u, k + 2, 0 );
		}
		while ( ++j < n );
	}

	public static void DecodePulses( Span<int> y, int n, int k, RangeDecoder dec )
	{
		if ( k == 0 )
		{
			y[..n].Clear();
			return;
		}

		if ( n == 1 )
		{
			var s = -(int)dec.DecodeBits( 1 );
			y[0] = (k + s) ^ s;
			return;
		}

		Span<uint> u = k + 2 <= 256 ? stackalloc uint[k + 2] : new uint[k + 2];
		Cwrsi( n, k, dec.DecodeUint( NcwrsURow( n, k, u ) ), y, u );
	}
}
