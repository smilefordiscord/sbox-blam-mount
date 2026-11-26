using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using Blam.Audio;
using Blam.Halo2.Tags;
using Blam.IO;

namespace Blam.Halo2;

/// <summary>
/// Halo 2's sound samples, in the <c>sounds_*.dat</c> files beside the maps: no header or index of their own, only
/// chunks (on 0x80 boundaries) that the gestalts locate.
/// </summary>
/// <remarks>
/// Which file a sound's chunks are in isn't stored anywhere; it follows from the sound (checked on every chunk of every
/// map): dialog (a multilingual sound class) is in <c>sounds_&lt;language&gt;.dat</c>, sounds only played with the
/// remastered audio in <c>sounds_remastered.dat</c>, and every other sound in <c>sounds_neutral.dat</c>.
/// </remarks>
public sealed class SoundSamples : IDisposable
{
	/// <summary>
	/// Samples per channel of an Opus packet at most (120 ms at 48 kHz). MCC's are all 5 ms.
	/// </summary>
	const int MaxOpusFrame = 5760;

	readonly string _directory;
	readonly Dictionary<string, RawFile> _files = new( StringComparer.OrdinalIgnoreCase );

	public SoundSamples( string mapsDirectory ) => _directory = mapsDirectory;

	/// <summary>
	/// The name of the sample file a sound's chunks are in (<c>neutral</c>, <c>en</c>...), or null for a language MCC
	/// has no samples for.
	/// </summary>
	public static string FileName( SoundTag sound, bool multilingual, SoundLanguage language )
	{
		if ( multilingual )
		{
			return language switch
			{
				SoundLanguage.English => "en",
				SoundLanguage.German => "de",
				SoundLanguage.French => "fr",
				SoundLanguage.Spanish => "sp",
				SoundLanguage.Italian => "it",
				_ => null,
			};
		}

		return sound.IsRemasteredOnly ? "remastered" : "neutral";
	}

	/// <summary>
	/// Whether a sample file is installed.
	/// </summary>
	public bool Exists( string fileName ) => File.Exists( Path.Combine( _directory, $"sounds_{fileName}.dat" ) );

	/// <summary>
	/// Whether samples stored this way can be decoded.
	/// </summary>
	public static bool CanDecode( SoundCompression compression, int channels ) => compression switch
	{
		SoundCompression.Opus => channels is 1 or 2,
		SoundCompression.XboxAdpcm => channels is 1 or 2,
		SoundCompression.PcmLittleEndian or SoundCompression.PcmBigEndian => true,
		_ => false,
	};

	/// <summary>
	/// Decodes a permutation's chunks to interleaved 16 bit PCM.
	/// </summary>
	public short[] Decode( string fileName, SoundCompression compression, int channels, IReadOnlyList<SoundChunk> chunks )
	{
		var file = GetFile( fileName );
		var total = 0;
		foreach ( var chunk in chunks )
			total += chunk.PcmBytes / 2;

		var output = new short[total];
		var written = 0;

		// The chunks are one stream: Opus carries its decoder state from one chunk into the next.
		var opus = compression == SoundCompression.Opus ? new Concentus.Structs.OpusDecoder( 48000, channels ) : null;
		var frame = opus is null ? null : new short[MaxOpusFrame * channels];

		foreach ( var chunk in chunks )
		{
			var data = file.Read( chunk.Offset, chunk.Size );
			var room = output.AsSpan( written, Math.Min( chunk.PcmBytes / 2, total - written ) );
			var count = compression switch
			{
				SoundCompression.Opus => DecodeOpus( opus, data, channels, frame, room ),
				SoundCompression.XboxAdpcm => Copy( XboxAdpcm.Decode( data, channels, room.Length / channels ), room ),
				SoundCompression.PcmLittleEndian => DecodePcm( data, room, bigEndian: false ),
				SoundCompression.PcmBigEndian => DecodePcm( data, room, bigEndian: true ),
				_ => throw new NotSupportedException( $"Can't decode {compression} sounds" ),
			};

			written += count;
		}

		return written == total ? output : output[..written];
	}

	/// <summary>
	/// Decodes one chunk of Opus packets, each a 16 bit length and the packet, the last one's length negated.
	/// </summary>
	static int DecodeOpus( Concentus.Structs.OpusDecoder decoder, ReadOnlySpan<byte> data, int channels, short[] frame, Span<short> output )
	{
		var written = 0;
		for ( var p = 0; p + 2 <= data.Length; )
		{
			int length = BinaryPrimitives.ReadInt16LittleEndian( data[p..] );
			var size = Math.Abs( length );
			if ( size == 0 || p + 2 + size > data.Length )
				break;

			var samples = decoder.Decode( data.Slice( p + 2, size ), frame, MaxOpusFrame ) * channels;
			var copied = Math.Min( samples, output.Length - written );
			frame.AsSpan( 0, copied ).CopyTo( output[written..] );
			written += copied;

			p += 2 + size;
			if ( length < 0 )
				break;
		}

		return written;
	}

	static int DecodePcm( ReadOnlySpan<byte> data, Span<short> output, bool bigEndian )
	{
		var count = Math.Min( output.Length, data.Length / 2 );
		for ( var i = 0; i < count; i++ )
		{
			output[i] = bigEndian
				? BinaryPrimitives.ReadInt16BigEndian( data[(i * 2)..] )
				: BinaryPrimitives.ReadInt16LittleEndian( data[(i * 2)..] );
		}

		return count;
	}

	static int Copy( short[] samples, Span<short> output )
	{
		var count = Math.Min( samples.Length, output.Length );
		samples.AsSpan( 0, count ).CopyTo( output );
		return count;
	}

	RawFile GetFile( string fileName )
	{
		lock ( _files )
		{
			if ( !_files.TryGetValue( fileName, out var file ) )
				_files[fileName] = file = new RawFile( Path.Combine( _directory, $"sounds_{fileName}.dat" ) );

			return file;
		}
	}

	public void Dispose()
	{
		lock ( _files )
		{
			foreach ( var file in _files.Values )
				file.Dispose();
			_files.Clear();
		}
	}
}
