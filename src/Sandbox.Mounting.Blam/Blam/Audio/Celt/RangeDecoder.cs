/* C# port of the decoder side of libcelt 0.11.0 (entcode.c, entdec.c, laplace.c).

   Copyright (c) 2001-2011 Timothy B. Terriberry
   Copyright (c) 2007-2008 CSIRO
   Copyright (c) 2007-2009 Xiph.Org Foundation
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
using System.Numerics;

namespace Blam.Audio.Celt;

/// <summary>
/// CELT's range decoder. Range coded symbols are read from the start of the frame and raw bits
/// from the end, and both count towards <see cref="Tell"/>, which the bit allocation depends on.
/// </summary>
sealed class RangeDecoder
{
	const int SymBits = 8;
	const int CodeBits = 32;
	const uint SymMax = (1u << SymBits) - 1;
	const uint CodeTop = 1u << (CodeBits - 1);
	const uint CodeBot = CodeTop >> SymBits;
	const int CodeExtra = (CodeBits - 2) % SymBits + 1;
	const int WindowSize = 32;
	const int UintBits = 8;

	/// <summary>
	/// Fractional bits used by <see cref="TellFrac"/> (1/8 bit).
	/// </summary>
	public const int BitRes = 3;

	byte[] _buf;
	int _start;
	uint _storage;
	uint _endOffs;
	uint _endWindow;
	int _nendBits;
	int _offs;
	uint _val;
	uint _ext;
	int _rem;

	/// <summary>
	/// Total whole bits read so far, not counting the partial bits still in the range.
	/// </summary>
	public int TotalBits;
	public uint Rng;
	public bool Error;

	public uint Storage => _storage;

	public void Init( byte[] buffer, int offset, int length )
	{
		_buf = buffer;
		_start = offset;
		_storage = (uint)length;
		_endOffs = 0;
		_endWindow = 0;
		_nendBits = 0;
		_offs = 0;
		Rng = 1u << CodeExtra;
		_rem = ReadByte();
		_val = Rng - 1 - (uint)(_rem >> (SymBits - CodeExtra));
		Error = false;
		Normalize();
		TotalBits = CodeBits + 1;
	}

	int ReadByte() => _offs < _storage ? _buf[_start + _offs++] : 0;

	int ReadByteFromEnd() => _endOffs < _storage ? _buf[_start + (int)(_storage - ++_endOffs)] : 0;

	/// <summary>
	/// Number of significant bits in <paramref name="v"/>, 0 for 0.
	/// </summary>
	public static int ILog( uint v ) => 32 - BitOperations.LeadingZeroCount( v );

	void Normalize()
	{
		while ( Rng <= CodeBot )
		{
			TotalBits += SymBits;
			Rng <<= SymBits;
			var sym = _rem;
			_rem = ReadByte();
			sym = ((sym << SymBits) | _rem) >> (SymBits - CodeExtra);
			_val = ((_val << SymBits) + (SymMax & ~(uint)sym)) & (CodeTop - 1);
		}
	}

	/// <summary>
	/// Bits used so far, rounded up.
	/// </summary>
	public int Tell() => TotalBits - ILog( Rng );

	/// <summary>
	/// Bits used so far in 1/8 bit units, rounded up.
	/// </summary>
	public uint TellFrac()
	{
		var nbits = (uint)(TotalBits << BitRes);
		var l = ILog( Rng );
		var r = Rng >> (l - 16);
		for ( var i = BitRes; i-- > 0; )
		{
			r = (r * r) >> 15;
			var b = (int)(r >> 16);
			l = (l << 1) | b;
			r >>= b;
		}

		return nbits - (uint)l;
	}

	public uint Decode( uint ft )
	{
		_ext = Rng / ft;
		var s = _val / _ext;
		return ft - Math.Min( s + 1, ft );
	}

	public uint DecodeBin( int bits )
	{
		_ext = Rng >> bits;
		var s = _val / _ext;
		return (1u << bits) - Math.Min( s + 1, 1u << bits );
	}

	public void Update( uint fl, uint fh, uint ft )
	{
		var s = _ext * (ft - fh);
		_val -= s;
		Rng = fl > 0 ? _ext * (fh - fl) : Rng - s;
		Normalize();
	}

	/// <summary>
	/// Decodes a bit whose probability of being one is 1/(1 &lt;&lt; <paramref name="logp"/>).
	/// </summary>
	public int DecodeBitLogp( int logp )
	{
		var r = Rng;
		var d = _val;
		var s = r >> logp;
		var ret = d < s;
		if ( !ret ) _val = d - s;
		Rng = ret ? s : r - s;
		Normalize();
		return ret ? 1 : 0;
	}

	public int DecodeIcdf( ReadOnlySpan<byte> icdf, int ftb )
	{
		var s = Rng;
		var d = _val;
		var r = s >> ftb;
		var ret = -1;
		uint t;
		do
		{
			t = s;
			s = r * icdf[++ret];
		}
		while ( d < s );

		_val = d - s;
		Rng = t - s;
		Normalize();
		return ret;
	}

	public uint DecodeUint( uint ft )
	{
		ft--;
		var ftb = ILog( ft );
		if ( ftb > UintBits )
		{
			ftb -= UintBits;
			var f = (ft >> ftb) + 1;
			var s = Decode( f );
			Update( s, s + 1, f );
			var t = (s << ftb) | DecodeBits( ftb );
			if ( t <= ft ) return t;
			Error = true;
			return ft;
		}
		else
		{
			ft++;
			var s = Decode( ft );
			Update( s, s + 1, ft );
			return s;
		}
	}

	/// <summary>
	/// Reads raw bits from the end of the frame.
	/// </summary>
	public uint DecodeBits( int bits )
	{
		var window = _endWindow;
		var available = _nendBits;
		if ( available < bits )
		{
			do
			{
				window |= (uint)ReadByteFromEnd() << available;
				available += SymBits;
			}
			while ( available <= WindowSize - SymBits );
		}

		var ret = window & ((1u << bits) - 1);
		window = bits == 32 ? 0 : window >> bits;
		available -= bits;
		_endWindow = window;
		_nendBits = available;
		TotalBits += bits;
		return ret;
	}

	const int LaplaceMinP = 1;
	const int LaplaceLogMinP = 0;
	const int LaplaceNMin = 16;

	/// <summary>
	/// Decodes a value from the Laplace-like distribution used for coarse band energies.
	/// </summary>
	/// <param name="fs">Probability of 0, in 1/32768.</param>
	/// <param name="decay">Decay of the probabilities of larger values, Q14.</param>
	public int DecodeLaplace( int fs, int decay )
	{
		var val = 0;
		var fm = (int)DecodeBin( 15 );
		var fl = 0;
		if ( fm >= fs )
		{
			val++;
			fl = fs;
			fs = (((32768 - LaplaceMinP * (2 * LaplaceNMin) - fs) * (16384 - decay)) >> 15) + LaplaceMinP;

			while ( fs > LaplaceMinP && fm >= fl + 2 * fs )
			{
				fs *= 2;
				fl += fs;
				fs = ((fs - 2 * LaplaceMinP) * decay) >> 15;
				fs += LaplaceMinP;
				val++;
			}

			if ( fs <= LaplaceMinP )
			{
				var di = (fm - fl) >> (LaplaceLogMinP + 1);
				val += di;
				fl += 2 * di * LaplaceMinP;
			}

			if ( fm < fl + fs )
				val = -val;
			else
				fl += fs;
		}

		Update( (uint)fl, (uint)Math.Min( fl + fs, 32768 ), 32768 );
		return val;
	}
}
