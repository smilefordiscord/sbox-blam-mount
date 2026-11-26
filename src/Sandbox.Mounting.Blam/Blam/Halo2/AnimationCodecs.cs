using System;
using System.Buffers.Binary;
using System.IO;

namespace Blam.Halo2;

// Inside the namespace so these win over s&box's global vector types when this compiles into the mount.
using Quaternion = System.Numerics.Quaternion;
using Vector3 = System.Numerics.Vector3;

/// <summary>
/// The tracks one codec block of a Halo 2 animation holds: for each node it rotates, translates or scales (in node
/// order, by the animation's node flags), one value per frame (one for a static block).
/// </summary>
public sealed class CodecTracks
{
	public int Codec { get; init; }
	public Quaternion[][] Rotations { get; init; }
	public Vector3[][] Translations { get; init; }
	public float[][] Scales { get; init; }
}

/// <summary>
/// Decoder for the codec blocks of Halo 2 (MCC classic) animation data. Every animation's data holds a static block
/// (the values of components that don't change) and an animated block, each starting with its codec byte.
/// </summary>
/// <remarks>
/// <para>Worked out from the maps (every one of MCC's 9013 blocks decodes to unit quaternions that move smoothly
/// from frame to frame). Every block starts <c>u8 codec, u8 rotated nodes, u8 translated nodes, u8 scaled nodes,
/// f32 error, f32 compression rate</c>.</para>
///
/// <para><b>Full frame codecs</b> (1 uncompressed static, 2 uncompressed animated, 3 quantized rotations, 8 blend
/// screen): <c>+0xC u32 translation data offset, +0x10 u32 scale data offset, +0x14..0x1C bytes per node track</c>;
/// rotations start at 0x20. Node major: one node's every frame, then the next node's. Rotations are four int16 / 32767
/// (codec 8: four floats), translations three floats (world units), scales a float.</para>
///
/// <para><b>Keyframe codecs</b> (4 byte keys, 5 word keys, 6 and 7 the same with the node blocks stored back to
/// front, which changes nothing since the headers hold absolute indices): <c>+0xC eight u32 offsets</c>: translation
/// key headers, scale key headers, rotation keys, translation keys, scale keys, rotation values, translation values,
/// scale values; rotation key headers start at 0x30. A key header is <c>count (low 12 bits) | first key index &lt;&lt;
/// 12</c>; keys are frame numbers (u8 or u16) and each key has a value. Frames between keys are interpolated
/// (rotations normalised), frames outside them hold the nearest key.</para>
/// </remarks>
public static class AnimationCodecs
{
	public const int UncompressedStatic = 1;
	public const int UncompressedAnimated = 2;
	public const int QuantizedRotationOnly = 3;
	public const int ByteKeyframe = 4;
	public const int WordKeyframe = 5;
	public const int ReverseByteKeyframe = 6;
	public const int ReverseWordKeyframe = 7;
	public const int BlendScreen = 8;

	public static string Name( int codec ) => codec switch
	{
		UncompressedStatic => "uncompressed static",
		UncompressedAnimated => "uncompressed animated",
		QuantizedRotationOnly => "quantized rotation only",
		ByteKeyframe => "byte keyframe",
		WordKeyframe => "word keyframe",
		ReverseByteKeyframe => "reverse byte keyframe",
		ReverseWordKeyframe => "reverse word keyframe",
		BlendScreen => "blend screen",
		_ => $"codec {codec}",
	};

	/// <summary>
	/// Decodes a codec block.
	/// </summary>
	/// <param name="frames">Frames per track (1 for the static block).</param>
	/// <exception cref="InvalidDataException">The block isn't a codec this reads, or doesn't fit.</exception>
	public static CodecTracks Decode( ReadOnlySpan<byte> block, int frames )
	{
		if ( block.Length < 0x20 )
			throw new InvalidDataException( $"codec block of {block.Length} bytes" );

		int codec = block[0], rotated = block[1], translated = block[2], scaled = block[3];
		return codec switch
		{
			UncompressedStatic => FullFrames( block, codec, rotated, translated, scaled, 1 ),
			UncompressedAnimated or QuantizedRotationOnly or BlendScreen => FullFrames( block, codec, rotated, translated, scaled, frames ),
			ByteKeyframe or ReverseByteKeyframe => Keyframes( block, codec, rotated, translated, scaled, frames, 1 ),
			WordKeyframe or ReverseWordKeyframe => Keyframes( block, codec, rotated, translated, scaled, frames, 2 ),
			_ => throw new InvalidDataException( $"unknown codec {codec}" ),
		};
	}

	static CodecTracks FullFrames( ReadOnlySpan<byte> b, int codec, int rotated, int translated, int scaled, int frames )
	{
		var translations = (int)U32( b, 0xC );
		var scales = (int)U32( b, 0x10 );
		var floatRotations = codec == BlendScreen;
		var rotationSize = floatRotations ? 16 : 8;

		Check( b, 0x20, rotated * frames * rotationSize );
		Check( b, translations, translated * frames * 12 );
		Check( b, scales, scaled * frames * 4 );

		var r = new Quaternion[rotated][];
		for ( var n = 0; n < rotated; n++ )
		{
			r[n] = new Quaternion[frames];
			for ( var f = 0; f < frames; f++ )
			{
				var at = 0x20 + (n * frames + f) * rotationSize;
				r[n][f] = floatRotations
					? new Quaternion( F32( b, at ), F32( b, at + 4 ), F32( b, at + 8 ), F32( b, at + 12 ) )
					: Quantized( b, at );
			}
		}

		var t = new Vector3[translated][];
		for ( var n = 0; n < translated; n++ )
		{
			t[n] = new Vector3[frames];
			for ( var f = 0; f < frames; f++ )
				t[n][f] = Vec3( b, translations + (n * frames + f) * 12 );
		}

		var s = new float[scaled][];
		for ( var n = 0; n < scaled; n++ )
		{
			s[n] = new float[frames];
			for ( var f = 0; f < frames; f++ )
				s[n][f] = F32( b, scales + (n * frames + f) * 4 );
		}

		return new CodecTracks { Codec = codec, Rotations = r, Translations = t, Scales = s };
	}

	static CodecTracks Keyframes( ReadOnlySpan<byte> b, int codec, int rotated, int translated, int scaled, int frames, int keySize )
	{
		Check( b, 0xC, 0x24 + rotated * 4 );
		var headers = new[] { 0x30, (int)U32( b, 0xC ), (int)U32( b, 0x10 ) };
		var keys = new[] { (int)U32( b, 0x14 ), (int)U32( b, 0x18 ), (int)U32( b, 0x1C ) };
		var values = new[] { (int)U32( b, 0x20 ), (int)U32( b, 0x24 ), (int)U32( b, 0x28 ) };

		var r = new Quaternion[rotated][];
		var t = new Vector3[translated][];
		var s = new float[scaled][];
		var counts = new[] { rotated, translated, scaled };
		var valueSizes = new[] { 8, 12, 4 };
		Span<int> keyFrames = stackalloc int[4096];

		for ( var component = 0; component < 3; component++ )
		{
			Check( b, headers[component], counts[component] * 4 );
			for ( var n = 0; n < counts[component]; n++ )
			{
				var header = U32( b, headers[component] + 4 * n );
				var count = (int)(header & 0xFFF);
				var first = (int)(header >> 12);
				if ( count == 0 )
					throw new InvalidDataException( $"{Name( codec )}: track with no keys" );

				Check( b, keys[component] + first * keySize, count * keySize );
				Check( b, values[component] + first * valueSizes[component], count * valueSizes[component] );
				for ( var k = 0; k < count; k++ )
				{
					var at = keys[component] + (first + k) * keySize;
					keyFrames[k] = keySize == 1 ? b[at] : BinaryPrimitives.ReadUInt16LittleEndian( b[at..] );
				}

				var valueBase = values[component] + first * valueSizes[component];
				switch ( component )
				{
					case 0:
						r[n] = new Quaternion[frames];
						for ( var f = 0; f < frames; f++ )
						{
							var (k, x) = Locate( keyFrames[..count], f );
							var a = Quantized( b, valueBase + k * 8 );
							r[n][f] = x == 0 ? a : Nlerp( a, Quantized( b, valueBase + (k + 1) * 8 ), x );
						}
						break;
					case 1:
						t[n] = new Vector3[frames];
						for ( var f = 0; f < frames; f++ )
						{
							var (k, x) = Locate( keyFrames[..count], f );
							var a = Vec3( b, valueBase + k * 12 );
							t[n][f] = x == 0 ? a : Vector3.Lerp( a, Vec3( b, valueBase + (k + 1) * 12 ), x );
						}
						break;
					default:
						s[n] = new float[frames];
						for ( var f = 0; f < frames; f++ )
						{
							var (k, x) = Locate( keyFrames[..count], f );
							var a = F32( b, valueBase + k * 4 );
							s[n][f] = x == 0 ? a : a + (F32( b, valueBase + (k + 1) * 4 ) - a) * x;
						}
						break;
				}
			}
		}

		return new CodecTracks { Codec = codec, Rotations = r, Translations = t, Scales = s };
	}

	/// <summary>
	/// The key at or before <paramref name="frame"/> and how far the frame is towards the next one (0 at a key, and
	/// before the first or after the last key, which hold their value).
	/// </summary>
	static (int Key, float Fraction) Locate( ReadOnlySpan<int> keys, int frame )
	{
		var k = 0;
		while ( k + 1 < keys.Length && keys[k + 1] <= frame )
			k++;

		if ( k + 1 >= keys.Length || keys[k] > frame || keys[k + 1] == keys[k] )
			return (k, 0);

		return (k, (float)(frame - keys[k]) / (keys[k + 1] - keys[k]));
	}

	/// <summary>
	/// Linear interpolation between two rotations the short way round, normalised.
	/// </summary>
	public static Quaternion Nlerp( Quaternion a, Quaternion b, float t )
	{
		if ( Quaternion.Dot( a, b ) < 0 )
			b = Quaternion.Negate( b );

		var q = Quaternion.Add( Quaternion.Multiply( a, 1 - t ), Quaternion.Multiply( b, t ) );
		var length = q.Length();
		return length > 1e-8f ? Quaternion.Multiply( q, 1 / length ) : a;
	}

	static void Check( ReadOnlySpan<byte> b, int offset, int length )
	{
		if ( offset < 0 || length < 0 || (long)offset + length > b.Length )
			throw new InvalidDataException( $"data at 0x{offset:X} (+{length}) is outside the {b.Length} byte block" );
	}

	static Quaternion Quantized( ReadOnlySpan<byte> b, int at ) => new(
		BinaryPrimitives.ReadInt16LittleEndian( b[at..] ) / 32767f,
		BinaryPrimitives.ReadInt16LittleEndian( b[(at + 2)..] ) / 32767f,
		BinaryPrimitives.ReadInt16LittleEndian( b[(at + 4)..] ) / 32767f,
		BinaryPrimitives.ReadInt16LittleEndian( b[(at + 6)..] ) / 32767f );

	static uint U32( ReadOnlySpan<byte> b, int at ) => BinaryPrimitives.ReadUInt32LittleEndian( b[at..] );
	static float F32( ReadOnlySpan<byte> b, int at ) => BinaryPrimitives.ReadSingleLittleEndian( b[at..] );
	static Vector3 Vec3( ReadOnlySpan<byte> b, int at ) => new( F32( b, at ), F32( b, at + 4 ), F32( b, at + 8 ) );
}
