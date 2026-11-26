using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;
using Blam.Audio.Celt;
using Blam.IO;

namespace Blam.Audio;

/// <summary>
/// The codec of every sample in an FSB5 bank (FMOD_SOUND_FORMAT).
/// </summary>
public enum FsbCodec
{
	None = 0,
	Pcm8 = 1,
	Pcm16 = 2,
	Pcm24 = 3,
	Pcm32 = 4,
	PcmFloat = 5,
	GcAdpcm = 6,
	ImaAdpcm = 7,
	Vag = 8,
	HeVag = 9,
	Xma = 10,
	Mpeg = 11,
	Celt = 12,
	At9 = 13,
	Xwma = 14,
	Vorbis = 15,
}

/// <summary>
/// One sample in an FSB5 bank.
/// </summary>
/// <param name="DataOffset">Absolute offset of the encoded data in the bank file.</param>
/// <param name="SampleCount">Length in samples per channel.</param>
/// <param name="VorbisSetupId">Vorbis samples: the CRC32 of their setup header (chunk 11).</param>
public sealed record FsbSample( int Index, string Name, int SampleRate, int Channels, long DataOffset, int DataSize, int SampleCount, int LoopStart, int LoopEnd, uint VorbisSetupId = 0 );

/// <summary>
/// An FMOD Ex FSB5 sound bank, as MCC ships Halo CE's sounds in (<c>halo1/sound/pc/*.fsb</c>).
/// </summary>
/// <remarks>
/// Layout, little-endian: a 60 byte header (<c>FSB5</c>, version 1, sample count, sample header
/// size, name table size, data size, codec, flags and hash), then one sample header per sample,
/// then the name table (an offset per sample, then null-terminated names), then the sample data.
///
/// Each sample header is a 64 bit word: bit 0 "chunks follow", bits 1-4 sample rate index, bits
/// 5-6 channel count index, bits 7-33 data offset / 32, bits 34-63 sample count. Chunks are a 32
/// bit word each (bit 0 "another chunk follows", bits 1-24 size, bits 25-31 type) and their data;
/// type 1 overrides the channel count, 2 the sample rate, 3 gives the loop points, 11 the Vorbis
/// setup header's CRC32 (and a seek table), 14 a count of "intra layers" that multiplies the channels.
///
/// Halo CE's banks use IMA ADPCM (<c>sounds_adpcm.fsb</c>) or CELT (all the others). CELT samples
/// are a series of frames, each an 8 byte header (sync word 0x17C30DF3 big-endian, frame size)
/// followed by one CELT 0.11 frame of 512 samples, encoded at 44.1 kHz. Halo 3's banks are Vorbis
/// (see <see cref="Fsb5Vorbis"/>), decodable once <see cref="VorbisSetups"/> is set.
/// </remarks>
public sealed class Fsb5Bank : IDisposable
{
	const int HeaderSize = 0x3C;
	const uint CeltSync = 0xF30DC317; // 17 C3 0D F3 read as little-endian
	const int CeltFrameSamples = 512;
	const int CeltSampleRate = 44100;

	static readonly int[] SampleRates = [4000, 8000, 11000, 11025, 16000, 22050, 24000, 32000, 44100, 48000, 96000];

	readonly RawFile _file;
	readonly FsbSample[] _samples;

	public string FilePath => _file.Path;
	public FsbCodec Codec { get; }
	public System.Collections.Generic.IReadOnlyList<FsbSample> Samples => _samples;

	// Setup headers for Vorbis banks, which FMOD keeps out of the bank.
	public FsbVorbisSetups VorbisSetups { get; set; }

	public Fsb5Bank( string path )
	{
		_file = new RawFile( path );
		try
		{
			var header = _file.Read( 0, HeaderSize );
			if ( Encoding.ASCII.GetString( header, 0, 4 ) != "FSB5" )
				throw new InvalidDataException( $"'{path}' is not an FSB5 bank" );

			uint U32( int offset ) => BinaryPrimitives.ReadUInt32LittleEndian( header.AsSpan( offset ) );
			var version = U32( 0x4 );
			if ( version != 1 )
				throw new InvalidDataException( $"'{path}' is FSB5 version {version}, only version 1 is supported" );

			var count = (int)U32( 0x8 );
			var sampleHeaderSize = (int)U32( 0xC );
			var nameTableSize = (int)U32( 0x10 );
			var dataSize = U32( 0x14 );
			Codec = (FsbCodec)U32( 0x18 );

			var headers = _file.Read( HeaderSize, sampleHeaderSize );
			var names = _file.Read( HeaderSize + sampleHeaderSize, nameTableSize );
			var dataStart = (long)HeaderSize + sampleHeaderSize + nameTableSize;

			var offsets = new long[count];
			var rates = new int[count];
			var channels = new int[count];
			var lengths = new int[count];
			var loops = new (int Start, int End)[count];
			var setupIds = new uint[count];

			var p = 0;
			for ( var i = 0; i < count; i++ )
			{
				var mode = BinaryPrimitives.ReadUInt64LittleEndian( headers.AsSpan( p ) );
				p += 8;

				var more = (mode & 1) != 0;
				var rateIndex = (int)(mode >> 1) & 0xF;
				rates[i] = rateIndex < SampleRates.Length ? SampleRates[rateIndex] : 44100;
				channels[i] = ((int)(mode >> 5) & 3) switch { 0 => 1, 1 => 2, 2 => 6, _ => 8 };
				offsets[i] = (long)((mode >> 7) & 0x7FFFFFF) << 5;
				lengths[i] = (int)((mode >> 34) & 0x3FFFFFFF);

				while ( more )
				{
					var chunk = BinaryPrimitives.ReadUInt32LittleEndian( headers.AsSpan( p ) );
					p += 4;
					more = (chunk & 1) != 0;
					var size = (int)(chunk >> 1) & 0xFFFFFF;
					var type = (int)(chunk >> 25);

					var body = headers.AsSpan( p, size );
					switch ( type )
					{
						case 1: channels[i] = body[0]; break;
						case 2: rates[i] = BinaryPrimitives.ReadInt32LittleEndian( body ); break;
						case 3: loops[i] = (BinaryPrimitives.ReadInt32LittleEndian( body ), BinaryPrimitives.ReadInt32LittleEndian( body[4..] )); break;
						case 11: setupIds[i] = BinaryPrimitives.ReadUInt32LittleEndian( body ); break;
						case 14: channels[i] *= BinaryPrimitives.ReadInt32LittleEndian( body ); break;
					}

					p += size;
				}
			}

			_samples = new FsbSample[count];
			for ( var i = 0; i < count; i++ )
			{
				var end = i + 1 < count ? offsets[i + 1] : dataSize;
				var nameOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian( names.AsSpan( i * 4 ) );
				var nameSpan = names.AsSpan( nameOffset );
				var nameEnd = nameSpan.IndexOf( (byte)0 );
				var name = Encoding.ASCII.GetString( nameEnd < 0 ? nameSpan : nameSpan[..nameEnd] );

				_samples[i] = new FsbSample( i, name, rates[i], channels[i], dataStart + offsets[i], (int)(end - offsets[i]), lengths[i], loops[i].Start, loops[i].End, setupIds[i] );
			}
		}
		catch
		{
			_file.Dispose();
			throw;
		}
	}

	/// <summary>
	/// Whether <see cref="Decode"/> can decode this bank's samples.
	/// </summary>
	public bool CanDecode => Codec is FsbCodec.ImaAdpcm or FsbCodec.Celt or FsbCodec.Pcm16 || Codec == FsbCodec.Vorbis && VorbisSetups is not null;

	/// <summary>
	/// Decodes a sample to interleaved 16 bit PCM, mono or stereo. Samples with more channels
	/// (some music is quad or 5.1) are mixed down to stereo.
	/// </summary>
	public short[] Decode( FsbSample sample, out int channels )
	{
		var data = _file.Read( sample.DataOffset, sample.DataSize );
		var pcm = Codec switch
		{
			FsbCodec.ImaAdpcm => XboxAdpcm.Decode( data, sample.Channels, sample.SampleCount ),
			FsbCodec.Celt => DecodeCelt( data, sample.Channels, sample.SampleCount ),
			FsbCodec.Pcm16 => DecodePcm16( data, sample.Channels, sample.SampleCount ),
			FsbCodec.Vorbis => DecodeVorbis( data, sample ),
			_ => throw new NotSupportedException( $"FSB codec {Codec} isn't supported" ),
		};

		channels = Math.Min( sample.Channels, 2 );
		return sample.Channels > 2 ? DownmixToStereo( pcm, sample.Channels ) : pcm;
	}

	short[] DecodeVorbis( byte[] data, FsbSample sample )
	{
		var setup = VorbisSetups?.Get( sample.VorbisSetupId ) ?? throw new InvalidDataException( $"No Vorbis setup header {sample.VorbisSetupId:X8} for '{sample.Name}'" );
		var pcm = Fsb5Vorbis.Decode( data, sample.Channels, sample.SampleRate, sample.SampleCount, setup );
		Fsb5Vorbis.ToFmodOrder( pcm, sample.Channels );
		return pcm;
	}

	static short[] DecodePcm16( byte[] data, int channels, int sampleCount )
	{
		var output = new short[Math.Min( sampleCount * channels, data.Length / 2 )];
		for ( var i = 0; i < output.Length; i++ )
			output[i] = BinaryPrimitives.ReadInt16LittleEndian( data.AsSpan( i * 2 ) );

		return output;
	}

	/// <summary>
	/// CELT only does mono and stereo, so FMOD codes more channels as several streams, one per
	/// channel pair, with their frames interleaved: a frame of the first pair, then of the second...
	/// </summary>
	static short[] DecodeCelt( byte[] data, int channels, int sampleCount )
	{
		var streamCount = (channels + 1) / 2;
		var decoders = new CeltDecoder[streamCount];
		for ( var i = 0; i < streamCount; i++ )
			decoders[i] = new CeltDecoder( CeltSampleRate, CeltFrameSamples, Math.Min( 2, channels - i * 2 ) );

		var output = new short[sampleCount * channels];
		var frame = new float[CeltFrameSamples * 2];

		var offset = 0;
		var written = 0;
		while ( written < sampleCount )
		{
			var count = Math.Min( CeltFrameSamples, sampleCount - written );
			for ( var stream = 0; stream < streamCount; stream++ )
			{
				if ( offset + 8 > data.Length )
					return output;

				var sync = BinaryPrimitives.ReadUInt32LittleEndian( data.AsSpan( offset ) );
				var size = BinaryPrimitives.ReadInt32LittleEndian( data.AsSpan( offset + 4 ) );

				// The data is padded to 32 bytes after the last frame.
				if ( sync != CeltSync || size <= 0 || offset + 8 + size > data.Length )
					return output;

				var decoder = decoders[stream];
				if ( !decoder.Decode( data, offset + 8, size, frame, CeltFrameSamples ) )
					System.Threading.Interlocked.Increment( ref _corruptFrames );

				offset += 8 + size;

				var streamChannels = decoder.Channels;
				for ( var i = 0; i < count; i++ )
				{
					for ( var c = 0; c < streamChannels; c++ )
					{
						var value = MathF.Round( frame[i * streamChannels + c] * 32768f );
						output[(written + i) * channels + stream * 2 + c] = (short)Math.Clamp( value, -32768f, 32767f );
					}
				}
			}

			written += count;
		}

		return output;
	}

	/// <summary>
	/// Mixes quad (FL FR RL RR) or 5.1 (FL FR C LFE RL RR) down to stereo, keeping the level of
	/// the loudest channel combination below full scale.
	/// </summary>
	static short[] DownmixToStereo( short[] pcm, int channels )
	{
		var frames = pcm.Length / channels;
		var mixed = new float[frames * 2];
		var peak = 0f;
		for ( var i = 0; i < frames; i++ )
		{
			var s = pcm.AsSpan( i * channels, channels );
			float left = s[0], right = s[1];
			if ( channels == 6 )
			{
				var center = 0.7071f * s[2];
				left += center + 0.7071f * s[4];
				right += center + 0.7071f * s[5];
			}
			else
			{
				for ( var c = 2; c < channels; c++ )
				{
					if ( (c & 1) == 0 ) left += 0.7071f * s[c];
					else right += 0.7071f * s[c];
				}
			}

			mixed[i * 2] = left;
			mixed[i * 2 + 1] = right;
			peak = MathF.Max( peak, MathF.Max( MathF.Abs( left ), MathF.Abs( right ) ) );
		}

		var scale = peak > 32767f ? 32767f / peak : 1f;
		var output = new short[mixed.Length];
		for ( var i = 0; i < mixed.Length; i++ )
			output[i] = (short)MathF.Round( mixed[i] * scale );

		return output;
	}

	static int _corruptFrames;

	/// <summary>
	/// CELT frames that decoded with errors since startup, across all banks. Should stay 0.
	/// </summary>
	public static int CorruptFrames => _corruptFrames;

	public void Dispose() => _file.Dispose();
}
