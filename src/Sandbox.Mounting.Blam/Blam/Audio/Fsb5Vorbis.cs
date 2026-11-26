using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using NVorbis;
using NVorbis.Contracts;

namespace Blam.Audio;

// FMOD's Vorbis samples: no Ogg pages or headers, just u16-length-prefixed audio packets; the setup header is named by its CRC32 (chunk 11) and lives with the game.
public static class Fsb5Vorbis
{
	// FMOD always encodes with 256/2048 sample blocks.
	const int ShortBlockExponent = 8;
	const int LongBlockExponent = 11;

	// Decodes one sample to interleaved 16 bit PCM in Vorbis channel order (6ch: FL C FR RL RR LFE), exactly sampleCount frames.
	public static short[] Decode( byte[] data, int channels, int sampleRate, int sampleCount, byte[] setup )
	{
		var packets = new PacketList( channels, sampleRate, sampleCount, setup, data );
		using var decoder = new StreamDecoder( packets ) { ClipSamples = true };

		var output = new short[sampleCount * channels];
		var buffer = new float[4096 * channels];
		var written = 0;
		while ( written < output.Length )
		{
			var count = decoder.Read( buffer, 0, Math.Min( buffer.Length, output.Length - written ) );
			if ( count <= 0 )
				break;

			for ( var i = 0; i < count; i++ )
				output[written + i] = (short)Math.Clamp( MathF.Round( buffer[i] * 32767f ), -32768f, 32767f );

			written += count;
		}

		return output;
	}

	// Reorders 6ch Vorbis (FL C FR RL RR LFE) to FMOD's 5.1 order (FL FR C LFE RL RR) in place; other counts are the same in both.
	public static void ToFmodOrder( short[] pcm, int channels )
	{
		if ( channels != 6 )
			return;

		for ( var i = 0; i + 6 <= pcm.Length; i += 6 )
		{
			var (fl, c, fr, rl, rr, lfe) = (pcm[i], pcm[i + 1], pcm[i + 2], pcm[i + 3], pcm[i + 4], pcm[i + 5]);
			pcm[i] = fl;
			pcm[i + 1] = fr;
			pcm[i + 2] = c;
			pcm[i + 3] = lfe;
			pcm[i + 4] = rl;
			pcm[i + 5] = rr;
		}
	}

	// zlib's CRC32, which FMOD names setup headers by.
	public static uint Crc32( ReadOnlySpan<byte> data )
	{
		var table = CrcTable.Value;
		var crc = 0xFFFFFFFFu;
		foreach ( var b in data )
			crc = table[(crc ^ b) & 0xFF] ^ (crc >> 8);

		return ~crc;
	}

	static readonly Lazy<uint[]> CrcTable = new( () =>
	{
		var table = new uint[256];
		for ( var i = 0u; i < 256; i++ )
		{
			var c = i;
			for ( var k = 0; k < 8; k++ )
				c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
			table[i] = c;
		}
		return table;
	} );

	static byte[] IdentificationHeader( int channels, int sampleRate )
	{
		var header = new byte[30];
		"\u0001vorbis"u8.CopyTo( header );
		header[11] = (byte)channels;
		BinaryPrimitives.WriteInt32LittleEndian( header.AsSpan( 12 ), sampleRate );
		header[28] = (byte)((LongBlockExponent << 4) | ShortBlockExponent);
		header[29] = 1;
		return header;
	}

	static readonly byte[] CommentHeader = [3, (byte)'v', (byte)'o', (byte)'r', (byte)'b', (byte)'i', (byte)'s', 0, 0, 0, 0, 0, 0, 0, 0, 1];

	// The three headers, then the sample's packets; the last one carries the sample count as its granule position so the decoder trims the tail.
	sealed class PacketList : NVorbis.Contracts.IPacketProvider
	{
		readonly List<Packet> _packets = new();
		readonly long _sampleCount;
		int _next;

		public PacketList( int channels, int sampleRate, int sampleCount, byte[] setup, byte[] bytes )
		{
			_sampleCount = sampleCount;
			_packets.Add( new Packet( IdentificationHeader( channels, sampleRate ), 0, 30 ) );
			_packets.Add( new Packet( CommentHeader, 0, CommentHeader.Length ) );
			_packets.Add( new Packet( setup, 0, setup.Length ) );

			var p = 0;
			while ( p + 2 <= bytes.Length )
			{
				var size = BinaryPrimitives.ReadUInt16LittleEndian( bytes.AsSpan( p ) );
				if ( size == 0 || size == 0xFFFF || p + 2 + size > bytes.Length )
					break;

				_packets.Add( new Packet( bytes, p + 2, size ) );
				p += 2 + size;
			}

			var last = _packets[^1];
			last.IsEndOfStream = true;
			last.GranulePosition = sampleCount;
		}

		public bool CanSeek => false;
		public int StreamSerial => 0;
		public IPacket GetNextPacket() => _next < _packets.Count ? _packets[_next++] : null;
		public IPacket PeekNextPacket() => _next < _packets.Count ? _packets[_next] : null;
		public long SeekTo( long granulePos, int preRoll, GetPacketGranuleCount getPacketGranuleCount ) => throw new NotSupportedException();
		public long GetGranuleCount() => _sampleCount;
	}

	sealed class Packet( byte[] data, int offset, int length ) : DataPacket
	{
		int _position;

		protected override int TotalBits => length * 8;

		protected override int ReadNextByte() => _position < length ? data[offset + _position++] : -1;

		public override void Reset()
		{
			_position = 0;
			base.Reset();
		}
	}
}

// Vorbis setup headers by id (CRC32), as FSB5 chunk 11 names them.
public sealed class FsbVorbisSetups
{
	readonly Dictionary<uint, byte[]> _setups = new();

	public int Count => _setups.Count;
	public IEnumerable<uint> Ids => _setups.Keys;

	public byte[] Get( uint id ) => _setups.GetValueOrDefault( id );

	public bool Add( byte[] packet )
	{
		var id = Fsb5Vorbis.Crc32( packet );
		return _setups.TryAdd( id, packet );
	}

	// Finds a setup header in an executable image: a "\x05vorbis" packet of the given length that, with the given bytes patched, has the given CRC.
	public bool AddFromImage( ReadOnlySpan<byte> image, uint id, int length, ReadOnlySpan<(int Offset, byte Value)> patches )
	{
		ReadOnlySpan<byte> magic = "\u0005vorbis"u8;
		var start = 0;
		while ( true )
		{
			var found = image[start..].IndexOf( magic );
			if ( found < 0 )
				return false;

			var at = start + found;
			start = at + 1;
			if ( at + length > image.Length )
				return false;

			var packet = image.Slice( at, length ).ToArray();
			foreach ( var (offset, value) in patches )
				packet[offset] = value;

			if ( Fsb5Vorbis.Crc32( packet ) == id )
			{
				_setups[id] = packet;
				return true;
			}
		}
	}
}
