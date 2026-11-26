/* C# port of the decoder side of libcelt 0.11.0 (celt.c), floating point. Packet loss
   concealment isn't ported: every frame is expected to be present.

   Copyright (c) 2007-2008 CSIRO
   Copyright (c) 2007-2010 Xiph.Org Foundation
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

namespace Blam.Audio.Celt;

/// <summary>
/// A CELT 0.11 decoder for one mono or stereo stream. Frames must be decoded in order, since
/// energies, the overlap and the post-filter carry over from one frame to the next.
/// </summary>
public sealed class CeltDecoder
{
	const int BitRes = RangeDecoder.BitRes;
	const int DecodeBufferSize = 2048;
	const int MaxPeriod = 1024;
	const int CombFilterMinPeriod = 15;

	static readonly byte[] TrimIcdf = [126, 124, 119, 109, 87, 41, 19, 9, 4, 2, 0];
	// Probs: NONE: 21.875%, LIGHT: 6.25%, NORMAL: 65.625%, AGGRESSIVE: 6.25%
	static readonly byte[] SpreadIcdf = [25, 23, 2, 0];
	static readonly byte[] TapsetIcdf = [2, 1, 0];

	static readonly sbyte[][] TfSelectTable =
	[
		[0, -1, 0, -1, 0, -1, 0, -1],
		[0, -1, 0, -2, 1, 0, 1, -1],
		[0, -2, 0, -3, 2, 0, 1, -1],
		[0, -2, 0, -3, 3, 0, 1, -1],
	];

	static readonly float[][] CombGains =
	[
		[0.3066406250f, 0.2170410156f, 0.1296386719f],
		[0.4638671875f, 0.2680664062f, 0.0f],
		[0.7998046875f, 0.1000976562f, 0.0f],
	];

	readonly CeltMode _mode;
	readonly int _channels;
	readonly int _overlap;
	readonly RangeDecoder _dec = new();

	uint _rng;
	int _postfilterPeriod;
	int _postfilterPeriodOld;
	float _postfilterGain;
	float _postfilterGainOld;
	int _postfilterTapset;
	int _postfilterTapsetOld;
	readonly float[] _preemphMem = new float[2];

	/// <summary>
	/// Per channel: DECODE_BUFFER_SIZE samples of output history followed by the MDCT overlap.
	/// </summary>
	readonly float[][] _decodeMem;
	readonly float[] _oldBandE;
	readonly float[] _oldLogE;
	readonly float[] _oldLogE2;
	readonly float[] _backgroundLogE;

	public int Channels => _channels;

	/// <summary>
	/// Samples per channel in a full frame.
	/// </summary>
	public int FrameSize => _mode.ShortMdctSize << _mode.MaxLM;

	public CeltDecoder( int sampleRate, int frameSize, int channels )
		: this( GetMode( sampleRate, frameSize ), channels )
	{
	}

	static readonly System.Collections.Concurrent.ConcurrentDictionary<(int, int), CeltMode> Modes = new();

	/// <summary>
	/// Modes are immutable once built, so decoders share them.
	/// </summary>
	static CeltMode GetMode( int sampleRate, int frameSize ) =>
		Modes.GetOrAdd( (sampleRate, frameSize), key => new CeltMode( key.Item1, key.Item2 ) );

	CeltDecoder( CeltMode mode, int channels )
	{
		if ( channels is < 1 or > 2 )
			throw new ArgumentOutOfRangeException( nameof( channels ) );

		_mode = mode;
		_channels = channels;
		_overlap = mode.Overlap;
		_decodeMem = new float[channels][];
		for ( var c = 0; c < channels; c++ )
			_decodeMem[c] = new float[DecodeBufferSize + _overlap];

		_oldBandE = new float[channels * mode.NbEBands];
		_oldLogE = new float[channels * mode.NbEBands];
		_oldLogE2 = new float[channels * mode.NbEBands];
		_backgroundLogE = new float[channels * mode.NbEBands];
	}

	/// <summary>
	/// Decodes one frame into <paramref name="pcm"/> as interleaved samples in [-1, 1].
	/// </summary>
	/// <returns>False if the frame was corrupt (the output is still written).</returns>
	public bool Decode( byte[] data, int offset, int length, Span<float> pcm, int frameSize )
	{
		var mode = _mode;
		var nbEBands = mode.NbEBands;
		var cc = _channels;
		var c = _channels;
		const int start = 0;
		var end = mode.EffEBands;

		if ( length < 2 || length > 1275 )
			throw new ArgumentOutOfRangeException( nameof( length ), "CELT frames must be 2 to 1275 bytes (loss concealment isn't supported)" );

		int lm;
		for ( lm = 0; lm <= mode.MaxLM; lm++ )
		{
			if ( mode.ShortMdctSize << lm == frameSize )
				break;
		}

		if ( lm > mode.MaxLM )
			throw new ArgumentOutOfRangeException( nameof( frameSize ) );

		var bigM = 1 << lm;
		var n = bigM * mode.ShortMdctSize;

		var effEnd = Math.Min( end, mode.EffEBands );

		var freq = new float[cc * n];
		var x = new float[cc * n];
		var bandE = new float[nbEBands * cc];

		var dec = _dec;
		dec.Init( data, offset, length );

		var totalBits = length * 8;
		var tell = dec.Tell();

		var silence = tell == 1 && dec.DecodeBitLogp( 15 ) != 0;
		if ( silence )
		{
			// Pretend we've read all the remaining bits.
			tell = length * 8;
			dec.TotalBits += tell - dec.Tell();
		}

		float postfilterGain = 0;
		var postfilterPitch = 0;
		var postfilterTapset = 0;
		if ( start == 0 && tell + 16 <= totalBits )
		{
			if ( dec.DecodeBitLogp( 1 ) != 0 )
			{
				var octave = (int)dec.DecodeUint( 6 );
				postfilterPitch = (16 << octave) + (int)dec.DecodeBits( 4 + octave ) - 1;
				var qg = (int)dec.DecodeBits( 3 );
				if ( dec.Tell() + 2 <= totalBits )
					postfilterTapset = dec.DecodeIcdf( TapsetIcdf, 2 );

				postfilterGain = 0.09375f * (qg + 1);
			}

			tell = dec.Tell();
		}

		var isTransient = false;
		if ( lm > 0 && tell + 3 <= totalBits )
		{
			isTransient = dec.DecodeBitLogp( 3 ) != 0;
			tell = dec.Tell();
		}

		var shortBlocks = isTransient ? bigM : 0;

		// Decode the global flags (first symbols in the stream).
		var intraEner = tell + 3 <= totalBits && dec.DecodeBitLogp( 3 ) != 0;

		// Get band energies.
		Allocation.UnquantCoarseEnergy( mode, start, end, _oldBandE, intraEner, dec, c, lm );

		var tfRes = new int[nbEBands];
		TfDecode( start, end, isTransient, tfRes, lm, dec );

		tell = dec.Tell();
		var spreadDecision = Bands.SpreadNormal;
		if ( tell + 4 <= totalBits )
			spreadDecision = dec.DecodeIcdf( SpreadIcdf, 5 );

		var pulses = new int[nbEBands];
		var cap = new int[nbEBands];
		var offsets = new int[nbEBands];
		var finePriority = new int[nbEBands];

		for ( var i = 0; i < nbEBands; i++ )
		{
			var width = (mode.EBands[i + 1] - mode.EBands[i]) << lm;
			cap[i] = (mode.CacheCaps[nbEBands * (2 * lm + c - 1) + i] + 64) * c * width >> 2;
		}

		var dynallocLogp = 6;
		totalBits <<= BitRes;
		tell = (int)dec.TellFrac();
		for ( var i = start; i < end; i++ )
		{
			var width = c * (mode.EBands[i + 1] - mode.EBands[i]) << lm;
			// quanta is 6 bits, but no more than 1 bit/sample and no less than 1/8 bit/sample.
			var quanta = Math.Min( width << BitRes, Math.Max( 6 << BitRes, width ) );
			var dynallocLoopLogp = dynallocLogp;
			var boost = 0;
			while ( tell + (dynallocLoopLogp << BitRes) < totalBits && boost < cap[i] )
			{
				var flag = dec.DecodeBitLogp( dynallocLoopLogp );
				tell = (int)dec.TellFrac();
				if ( flag == 0 )
					break;

				boost += quanta;
				totalBits -= quanta;
				dynallocLoopLogp = 1;
			}

			offsets[i] = boost;
			// Making dynalloc more likely.
			if ( boost > 0 )
				dynallocLogp = Math.Max( 2, dynallocLogp - 1 );
		}

		var fineQuant = new int[nbEBands];
		var allocTrim = tell + (6 << BitRes) <= totalBits ? dec.DecodeIcdf( TrimIcdf, 7 ) : 5;

		var bits = (length * 8 << BitRes) - (int)dec.TellFrac() - 1;
		var antiCollapseRsv = isTransient && lm >= 2 && bits >= (lm + 2 << BitRes) ? 1 << BitRes : 0;
		bits -= antiCollapseRsv;

		var intensity = 0;
		var dualStereo = false;
		var codedBands = Allocation.ComputeAllocation( mode, start, end, offsets, cap, allocTrim, ref intensity, ref dualStereo,
			bits, out var balance, pulses, fineQuant, finePriority, c, lm, dec );

		Allocation.UnquantFineEnergy( mode, start, end, _oldBandE, fineQuant, dec, c );

		// Decode the fixed codebook.
		var collapseMasks = new byte[c * nbEBands];
		Bands.QuantAllBands( mode, start, end, x.AsSpan( 0, n ), c == 2 ? x.AsSpan( n, n ) : Span<float>.Empty, collapseMasks,
			pulses, shortBlocks, spreadDecision, dualStereo, intensity, tfRes,
			length * (8 << BitRes) - antiCollapseRsv, balance, dec, lm, codedBands, ref _rng );

		var antiCollapseOn = antiCollapseRsv > 0 && dec.DecodeBits( 1 ) != 0;

		Allocation.UnquantEnergyFinalise( mode, start, end, _oldBandE, fineQuant, finePriority, length * 8 - dec.Tell(), dec, c );

		if ( antiCollapseOn )
			Bands.AntiCollapse( mode, x, collapseMasks, lm, c, cc, n, start, end, _oldBandE, _oldLogE, _oldLogE2, pulses, _rng );

		Allocation.Log2Amp( mode, start, end, bandE, _oldBandE, c );

		if ( silence )
		{
			for ( var i = 0; i < c * nbEBands; i++ )
			{
				bandE[i] = 0;
				_oldBandE[i] = -28f;
			}
		}

		// Synthesis.
		Bands.DenormaliseBands( mode, x, freq, bandE, effEnd, c, bigM );

		for ( var ch = 0; ch < cc; ch++ )
			Array.Copy( _decodeMem[ch], n, _decodeMem[ch], 0, DecodeBufferSize - n );

		for ( var ch = 0; ch < c; ch++ )
		{
			Array.Clear( freq, ch * n, bigM * mode.EBands[start] );
			var bound = bigM * mode.EBands[effEnd];
			Array.Clear( freq, ch * n + bound, n - bound );
		}

		// out_syn = out_mem + MAX_PERIOD - N, out_mem = decode_mem + DECODE_BUFFER_SIZE - MAX_PERIOD.
		var outSyn = DecodeBufferSize - n;

		ComputeInverseMdcts( shortBlocks, freq, outSyn, cc, lm );

		for ( var ch = 0; ch < cc; ch++ )
		{
			_postfilterPeriod = Math.Max( _postfilterPeriod, CombFilterMinPeriod );
			_postfilterPeriodOld = Math.Max( _postfilterPeriodOld, CombFilterMinPeriod );
			var mem = _decodeMem[ch];
			if ( lm != 0 )
			{
				CombFilter( mem, outSyn, _postfilterPeriod, _postfilterPeriod, _overlap,
					_postfilterGain, _postfilterGain, _postfilterTapset, _postfilterTapset, null, 0 );
				CombFilter( mem, outSyn + _overlap, _postfilterPeriod, postfilterPitch, n - _overlap,
					_postfilterGain, postfilterGain, _postfilterTapset, postfilterTapset, mode.Window, mode.Overlap );
			}
			else
			{
				CombFilter( mem, outSyn, _postfilterPeriodOld, _postfilterPeriod, n - _overlap,
					_postfilterGainOld, _postfilterGain, _postfilterTapsetOld, _postfilterTapset, mode.Window, mode.Overlap );
			}
		}

		_postfilterPeriodOld = _postfilterPeriod;
		_postfilterGainOld = _postfilterGain;
		_postfilterTapsetOld = _postfilterTapset;
		_postfilterPeriod = postfilterPitch;
		_postfilterGain = postfilterGain;
		_postfilterTapset = postfilterTapset;

		// In case start or end were to change.
		for ( var ch = 0; ch < cc; ch++ )
		{
			for ( var i = 0; i < start; i++ )
				_oldBandE[ch * nbEBands + i] = 0;
			for ( var i = end; i < nbEBands; i++ )
				_oldBandE[ch * nbEBands + i] = 0;
		}

		if ( !isTransient )
		{
			Array.Copy( _oldLogE, _oldLogE2, cc * nbEBands );
			Array.Copy( _oldBandE, _oldLogE, cc * nbEBands );
			for ( var i = 0; i < cc * nbEBands; i++ )
				_backgroundLogE[i] = MathF.Min( _backgroundLogE[i] + bigM * 0.001f, _oldBandE[i] );
		}
		else
		{
			for ( var i = 0; i < cc * nbEBands; i++ )
				_oldLogE[i] = MathF.Min( _oldLogE[i], _oldBandE[i] );
		}

		_rng = dec.Rng;

		Deemphasis( outSyn, pcm, n, cc );

		return !(dec.Tell() > 8 * length || dec.Error);
	}

	static void TfDecode( int start, int end, bool isTransient, int[] tfRes, int lm, RangeDecoder dec )
	{
		var budget = dec.Storage * 8;
		var tell = (uint)dec.Tell();
		var logp = isTransient ? 2 : 4;
		var tfSelectRsv = lm > 0 && tell + logp + 1 <= budget ? 1 : 0;
		budget -= (uint)tfSelectRsv;
		var tfChanged = 0;
		var curr = 0;
		for ( var i = start; i < end; i++ )
		{
			if ( tell + logp <= budget )
			{
				curr ^= dec.DecodeBitLogp( logp );
				tell = (uint)dec.Tell();
				tfChanged |= curr;
			}

			tfRes[i] = curr;
			logp = isTransient ? 4 : 5;
		}

		var tfSelect = 0;
		var t = isTransient ? 1 : 0;
		if ( tfSelectRsv != 0 && TfSelectTable[lm][4 * t + 0 + tfChanged] != TfSelectTable[lm][4 * t + 2 + tfChanged] )
			tfSelect = dec.DecodeBitLogp( 1 );

		for ( var i = start; i < end; i++ )
			tfRes[i] = TfSelectTable[lm][4 * t + 2 * tfSelect + tfRes[i]];
	}

	/// <summary>
	/// Inverse MDCTs of all sub-frames and channels, windowed and overlap-added into the decode
	/// buffers at <paramref name="outSyn"/>.
	/// </summary>
	void ComputeInverseMdcts( int shortBlocks, float[] freq, int outSyn, int cc, int lm )
	{
		var mode = _mode;
		var n = mode.ShortMdctSize << lm;
		var overlap = _overlap;
		var buffer = new float[n + overlap];
		var tmp = new float[n];

		for ( var c = 0; c < cc; c++ )
		{
			var n2 = n;
			var b = 1;
			if ( shortBlocks != 0 )
			{
				n2 = mode.ShortMdctSize;
				b = shortBlocks;
			}

			// Prevents problems from the imdct doing the overlap-add.
			Array.Clear( buffer, 0, overlap );

			for ( var block = 0; block < b; block++ )
			{
				// De-interleaving the sub-frames.
				for ( var j = 0; j < n2; j++ )
					tmp[j] = freq[(j * b + block) + c * n2 * b];

				mode.Mdct.Backward( tmp.AsSpan( 0, n2 ), buffer, n2 * block, mode.Window, overlap, shortBlocks != 0 ? mode.MaxLM : mode.MaxLM - lm );
			}

			var mem = _decodeMem[c];
			var overlapMem = DecodeBufferSize;
			int k;
			for ( k = 0; k < overlap; k++ )
				mem[outSyn + k] = buffer[k] + mem[overlapMem + k];
			for ( ; k < n; k++ )
				mem[outSyn + k] = buffer[k];
			for ( k = 0; k < overlap; k++ )
				mem[overlapMem + k] = buffer[n + k];
		}
	}

	/// <summary>
	/// The pitch post-filter, cross-faded from the previous frame's parameters over the window.
	/// </summary>
	static void CombFilter( float[] buf, int y, int t0, int t1, int n, float g0, float g1, int tapset0, int tapset1, float[] window, int overlap )
	{
		// In place (y == x), like the decoder always uses it.
		var x = y;
		var g00 = g0 * CombGains[tapset0][0];
		var g01 = g0 * CombGains[tapset0][1];
		var g02 = g0 * CombGains[tapset0][2];
		var g10 = g1 * CombGains[tapset1][0];
		var g11 = g1 * CombGains[tapset1][1];
		var g12 = g1 * CombGains[tapset1][2];

		int i;
		for ( i = 0; i < overlap; i++ )
		{
			var f = window[i] * window[i];
			buf[y + i] = buf[x + i]
				+ (1 - f) * g00 * buf[x + i - t0]
				+ (1 - f) * g01 * buf[x + i - t0 - 1]
				+ (1 - f) * g01 * buf[x + i - t0 + 1]
				+ (1 - f) * g02 * buf[x + i - t0 - 2]
				+ (1 - f) * g02 * buf[x + i - t0 + 2]
				+ f * g10 * buf[x + i - t1]
				+ f * g11 * buf[x + i - t1 - 1]
				+ f * g11 * buf[x + i - t1 + 1]
				+ f * g12 * buf[x + i - t1 - 2]
				+ f * g12 * buf[x + i - t1 + 2];
		}

		for ( i = overlap; i < n; i++ )
		{
			buf[y + i] = buf[x + i]
				+ g10 * buf[x + i - t1]
				+ g11 * buf[x + i - t1 - 1]
				+ g11 * buf[x + i - t1 + 1]
				+ g12 * buf[x + i - t1 - 2]
				+ g12 * buf[x + i - t1 + 2];
		}
	}

	void Deemphasis( int outSyn, Span<float> pcm, int n, int cc )
	{
		var coef = _mode.Preemph;
		for ( var c = 0; c < cc; c++ )
		{
			var input = _decodeMem[c];
			var m = _preemphMem[c];
			for ( var j = 0; j < n; j++ )
			{
				var xv = input[outSyn + j];
				var tmp = xv + m;
				m = coef[0] * tmp - coef[1] * xv;
				tmp = coef[3] * tmp;
				pcm[j * cc + c] = tmp * (1f / 32768f);
			}

			_preemphMem[c] = m;
		}
	}
}
