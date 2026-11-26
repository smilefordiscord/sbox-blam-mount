using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace Blam.Halo1.Tags;

// Inside the namespace so these win over s&box's global types when this compiles into the mount.
using Quaternion = System.Numerics.Quaternion;
using Vector3 = System.Numerics.Vector3;

/// <summary>
/// What <see cref="CompressedAnimation.Inspect"/> found in an animation's compressed data.
/// </summary>
/// <param name="Error">Why the data can't be decoded, or null if it can.</param>
/// <param name="UsedBytes">Bytes the header's arrays cover, from the start of the compressed data.</param>
/// <param name="Keyframes">Stored keyframes over all channels.</param>
/// <param name="MinQuaternionLength">Shortest stored rotation, 1 meaning a unit quaternion before renormalizing.</param>
/// <param name="MaxQuaternionLength">Longest stored rotation.</param>
/// <param name="Warnings">Things the game would assert on but that still decode (keyframe ranges, packing).</param>
public sealed record CompressedAnimationReport( string Error, int UsedBytes, int AvailableBytes, int Keyframes, float MinQuaternionLength, float MaxQuaternionLength, IReadOnlyList<string> Warnings );

/// <summary>
/// Decoder for Halo CE's compressed animation data (animation flag 0x1), which the cinematics use.
/// </summary>
/// <remarks>
/// The compressed data starts at the animation's "offset to compressed data" (0x88, zero in MCC's
/// maps, which keep nothing else in the frame data; their default data is empty). It begins with
/// eleven int32 offsets, relative to its start:
/// <code>
/// 0x00 rotation keyframe frame indices   0x10 translation keyframe frame indices   0x20 scale keyframe frame indices
/// 0x04 rotation defaults                 0x14 translation defaults                 0x24 scale defaults
/// 0x08 rotation keyframes                0x18 translation keyframes                0x28 scale keyframes
///                                        0x0C translation keyframe headers         0x1C scale keyframe headers
/// </code>
/// followed by the rotation keyframe headers at 0x2C. Every channel (rotation, translation, scale)
/// has one uint32 header per node it animates (the node masks' set bits, in node order): the low 12
/// bits are the node's keyframe count, the rest the index of its first keyframe in the channel's
/// frame index (uint16) and keyframe arrays. Keyframes sit at frame indices above 0, the last one
/// at the animation's last frame; frame 0 is the node's default value, and frames between
/// keyframes interpolate linearly (rotations: normalized lerp along the shorter arc). A node with
/// no keyframes holds its default.
///
/// Rotation and translation defaults are stored for every node (so they also give the pose of the
/// nodes the animation doesn't animate); scale defaults only for the nodes with animated scale, the
/// others have scale 1. Translations and scales are floats; rotations are 48 bits: three uint16,
/// read as one big endian 48-bit value holding i, j, k and w as 12-bit two's complement numbers,
/// most significant first, scaled like the uncompressed int16 rotations (so they also need the
/// conjugate, see <see cref="ModelAnimationsTag.FromHalo"/>).
/// </remarks>
public static class CompressedAnimation
{
	const int HeaderSize = 0x2C;

	/// <summary>
	/// The arrays one channel's header points at.
	/// </summary>
	readonly record struct Channel( int Headers, int FrameIndices, int Defaults, int Keyframes, int Count, int ValueSize );

	/// <summary>
	/// Checks the compressed data's layout and the stored rotations without decoding the frames.
	/// </summary>
	public static CompressedAnimationReport Inspect( ModelAnimation animation, byte[] data, int offset )
	{
		var warnings = new List<string>();
		var keyframes = 0;
		var minLength = float.MaxValue;
		var maxLength = 0f;
		var used = 0;

		CompressedAnimationReport Fail( string error ) => new( error, used, data?.Length - offset ?? 0, keyframes, minLength, maxLength, warnings );

		if ( data is null || offset < 0 || data.Length - offset < HeaderSize )
			return Fail( "no compressed header" );

		var span = data.AsSpan( offset );
		if ( !TryGetChannels( animation, span, out var channels, out var error ) )
			return Fail( error );

		var nodeCount = animation.NodeCount;
		for ( var c = 0; c < 3; c++ )
		{
			var channel = channels[c];
			var defaults = c == 2 ? channel.Count : nodeCount;
			used = Math.Max( used, channel.Headers + 4 * channel.Count );
			used = Math.Max( used, channel.Defaults + channel.ValueSize * defaults );

			var expectedFirst = 0;
			for ( var i = 0; i < channel.Count; i++ )
			{
				var header = BinaryPrimitives.ReadUInt32LittleEndian( span[(channel.Headers + 4 * i)..] );
				var count = (int)(header & 0xFFF);
				var first = (int)(header >> 12);
				keyframes += count;

				if ( first != expectedFirst )
					warnings.Add( $"channel {c} node {i}: first keyframe {first}, expected {expectedFirst}" );
				expectedFirst = first + count;

				if ( count == 0 )
					continue;

				used = Math.Max( used, channel.FrameIndices + 2 * (first + count) );
				used = Math.Max( used, channel.Keyframes + channel.ValueSize * (first + count) );
				if ( used > span.Length )
					return Fail( $"channel {c} node {i}: keyframes run past the data ({used} > {span.Length})" );

				var previous = 0;
				for ( var k = 0; k < count; k++ )
				{
					var frame = BinaryPrimitives.ReadUInt16LittleEndian( span[(channel.FrameIndices + 2 * (first + k))..] );
					if ( frame <= previous )
						warnings.Add( $"channel {c} node {i}: keyframe {k} at frame {frame} after {previous}" );
					previous = frame;
				}

				if ( previous != animation.FrameCount - 1 )
					warnings.Add( $"channel {c} node {i}: last keyframe at frame {previous} of {animation.FrameCount}" );

				if ( c == 0 )
				{
					for ( var k = 0; k < count; k++ )
						Measure( ReadRotation( span, channel.Keyframes + 6 * (first + k) ) );
				}
			}

			if ( used > span.Length )
				return Fail( $"channel {c}: arrays run past the data ({used} > {span.Length})" );
		}

		for ( var n = 0; n < nodeCount; n++ )
			Measure( ReadRotation( span, channels[0].Defaults + 6 * n ) );

		void Measure( Quaternion q )
		{
			var length = q.Length() / 2047;
			minLength = MathF.Min( minLength, length );
			maxLength = MathF.Max( maxLength, length );
		}

		return new CompressedAnimationReport( null, used, span.Length, keyframes, minLength, maxLength, warnings );
	}

	/// <summary>
	/// Decodes every frame into the same layout as <see cref="ModelAnimation.ReadFrames"/>:
	/// <c>NodeCount</c> parent-relative poses per frame, frame after frame.
	/// </summary>
	public static NodePose[] Decode( ModelAnimation animation, byte[] data, int offset )
	{
		var span = data.AsSpan( offset );
		if ( !TryGetChannels( animation, span, out var channels, out var error ) )
			throw new InvalidOperationException( $"Animation {animation.Name}: {error}" );

		var nodeCount = animation.NodeCount;
		var frameCount = animation.FrameCount;
		var raw = new Quaternion[frameCount * nodeCount];
		var translations = new Vector3[frameCount * nodeCount];
		var scales = new float[frameCount * nodeCount];
		Array.Fill( scales, 1 );

		int rotationIndex = 0, translationIndex = 0, scaleIndex = 0;
		for ( var n = 0; n < nodeCount; n++ )
		{
			// Rotations stay as stored (unnormalized, conjugated) until the end, like the game
			// interpolates them.
			var rotation = channels[0];
			var defaultRotation = ReadRotation( span, rotation.Defaults + 6 * n );
			if ( animation.AnimatesRotation( n ) )
			{
				Fill( span, rotation, rotationIndex++, defaultRotation, n, nodeCount, frameCount, raw,
					( s, at ) => ReadRotation( s, at ), Nlerp );
			}
			else
			{
				for ( var f = 0; f < frameCount; f++ )
					raw[f * nodeCount + n] = defaultRotation;
			}

			var translation = channels[1];
			var defaultTranslation = ReadVector( span, translation.Defaults + 12 * n );
			if ( animation.AnimatesTranslation( n ) )
			{
				Fill( span, translation, translationIndex++, defaultTranslation, n, nodeCount, frameCount, translations,
					( s, at ) => ReadVector( s, at ), Vector3.Lerp );
			}
			else
			{
				for ( var f = 0; f < frameCount; f++ )
					translations[f * nodeCount + n] = defaultTranslation;
			}

			if ( animation.AnimatesScale( n ) )
			{
				var scale = channels[2];
				var defaultScale = BinaryPrimitives.ReadSingleLittleEndian( span[(scale.Defaults + 4 * scaleIndex)..] );
				Fill( span, scale, scaleIndex++, defaultScale, n, nodeCount, frameCount, scales,
					( s, at ) => BinaryPrimitives.ReadSingleLittleEndian( s[at..] ), ( a, b, t ) => a + (b - a) * t );
			}
		}

		var poses = new NodePose[raw.Length];
		for ( var i = 0; i < poses.Length; i++ )
			poses[i] = new NodePose( ModelAnimationsTag.FromHalo( raw[i] ), translations[i], scales[i] );

		return poses;
	}

	delegate T ReadValue<T>( ReadOnlySpan<byte> span, int offset );

	/// <summary>
	/// Writes one node's channel for every frame: its default at frame 0, its keyframes, and
	/// interpolation in between. Frames after the last keyframe (which the game never has) hold it.
	/// </summary>
	static void Fill<T>( ReadOnlySpan<byte> span, Channel channel, int index, T defaultValue, int node, int nodeCount, int frameCount, T[] output, ReadValue<T> read, Func<T, T, float, T> interpolate )
	{
		var header = BinaryPrimitives.ReadUInt32LittleEndian( span[(channel.Headers + 4 * index)..] );
		var count = (int)(header & 0xFFF);
		var first = (int)(header >> 12);

		// The segment being filled runs from (fromFrame, from) to (toFrame, to).
		var fromFrame = 0;
		var from = defaultValue;
		var toFrame = 0;
		var to = defaultValue;
		var next = 0;

		for ( var f = 0; f < frameCount; f++ )
		{
			while ( f >= toFrame && next < count )
			{
				fromFrame = toFrame;
				from = to;
				toFrame = BinaryPrimitives.ReadUInt16LittleEndian( span[(channel.FrameIndices + 2 * (first + next))..] );
				to = read( span, channel.Keyframes + channel.ValueSize * (first + next) );
				next++;
			}

			T value;
			if ( f >= toFrame )
				value = to;
			else if ( f <= fromFrame )
				value = from;
			else
				value = interpolate( from, to, (f - fromFrame) / (float)(toFrame - fromFrame) );

			output[f * nodeCount + node] = value;
		}
	}

	/// <summary>
	/// Finds the three channels' arrays and checks they lie inside the data.
	/// </summary>
	static bool TryGetChannels( ModelAnimation animation, ReadOnlySpan<byte> span, out Channel[] channels, out string error )
	{
		channels = null;
		error = null;

		if ( span.Length < HeaderSize )
		{
			error = "no compressed header";
			return false;
		}

		if ( animation.NodeCount <= 0 || animation.NodeCount > 64 || animation.FrameCount <= 0 )
		{
			error = $"{animation.NodeCount} nodes, {animation.FrameCount} frames";
			return false;
		}

		var offsets = new int[11];
		for ( var i = 0; i < offsets.Length; i++ )
		{
			offsets[i] = BinaryPrimitives.ReadInt32LittleEndian( span[(4 * i)..] );
			if ( offsets[i] < HeaderSize || offsets[i] > span.Length )
			{
				error = $"header offset {i} is 0x{offsets[i]:X} (data is 0x{span.Length:X} bytes)";
				return false;
			}
		}

		int rotations = 0, translations = 0, scales = 0;
		for ( var n = 0; n < animation.NodeCount; n++ )
		{
			rotations += animation.AnimatesRotation( n ) ? 1 : 0;
			translations += animation.AnimatesTranslation( n ) ? 1 : 0;
			scales += animation.AnimatesScale( n ) ? 1 : 0;
		}

		channels =
		[
			new Channel( HeaderSize, offsets[0], offsets[1], offsets[2], rotations, 6 ),
			new Channel( offsets[3], offsets[4], offsets[5], offsets[6], translations, 12 ),
			new Channel( offsets[7], offsets[8], offsets[9], offsets[10], scales, 4 ),
		];

		for ( var c = 0; c < 3; c++ )
		{
			var channel = channels[c];
			var defaults = c == 2 ? channel.Count : animation.NodeCount;
			if ( channel.Headers + 4 * channel.Count > span.Length || channel.Defaults + channel.ValueSize * defaults > span.Length )
			{
				error = $"channel {c} runs past the data";
				return false;
			}

			// Every keyframe a header points at has to be there.
			for ( var i = 0; i < channel.Count; i++ )
			{
				var header = BinaryPrimitives.ReadUInt32LittleEndian( span[(channel.Headers + 4 * i)..] );
				var end = (int)(header >> 12) + (int)(header & 0xFFF);
				if ( channel.FrameIndices + 2 * end > span.Length || channel.Keyframes + channel.ValueSize * end > span.Length )
				{
					error = $"channel {c} node {i}: keyframes run past the data";
					return false;
				}
			}
		}

		return true;
	}

	/// <summary>
	/// A 48-bit rotation as stored: four 12-bit components, unnormalized.
	/// </summary>
	static Quaternion ReadRotation( ReadOnlySpan<byte> span, int offset )
	{
		ulong bits = (ulong)BinaryPrimitives.ReadUInt16LittleEndian( span[offset..] ) << 32
			| (ulong)BinaryPrimitives.ReadUInt16LittleEndian( span[(offset + 2)..] ) << 16
			| BinaryPrimitives.ReadUInt16LittleEndian( span[(offset + 4)..] );

		static float Component( ulong bits, int shift )
		{
			var value = (int)(bits >> shift & 0xFFF);
			return value >= 0x800 ? value - 0x1000 : value;
		}

		return new Quaternion( Component( bits, 36 ), Component( bits, 24 ), Component( bits, 12 ), Component( bits, 0 ) );
	}

	static Vector3 ReadVector( ReadOnlySpan<byte> span, int offset ) => new(
		BinaryPrimitives.ReadSingleLittleEndian( span[offset..] ),
		BinaryPrimitives.ReadSingleLittleEndian( span[(offset + 4)..] ),
		BinaryPrimitives.ReadSingleLittleEndian( span[(offset + 8)..] ) );

	/// <summary>
	/// Linear blend along the shorter arc; the result is normalized later.
	/// </summary>
	static Quaternion Nlerp( Quaternion a, Quaternion b, float t )
	{
		if ( Quaternion.Dot( a, b ) < 0 )
			b = Quaternion.Negate( b );

		return Quaternion.Multiply( a, 1 - t ) + Quaternion.Multiply( b, t );
	}
}
