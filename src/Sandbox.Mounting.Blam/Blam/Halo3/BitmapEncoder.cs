using System;
using System.Buffers.Binary;

namespace Blam.Halo3;

// Re-encodes the formats s&box can't take as they are into block formats it can, instead of 4-byte RGBA:
// BC1 with transparent texels and BC2 into BC3, R8G8 bump maps into BC5 (biased like BitmapDecoder's output, read as tex.rg * 2 - 1).
public static class BitmapEncoder
{
	// BC1 to BC3: colour blocks kept (three colour blocks re-indexed into four colour mode, the midpoint to its nearest third), 1-bit alpha exact.
	public static byte[] Bc1ToBc3( ReadOnlySpan<byte> source, int width, int height )
	{
		var blocks = Math.Max( 1, (width + 3) / 4 ) * Math.Max( 1, (height + 3) / 4 );
		var output = new byte[blocks * 16];
		Span<int> stored = stackalloc int[12];
		Span<int> four = stackalloc int[12];
		for ( var b = 0; b < blocks; b++ )
		{
			var block = source.Slice( b * 8, 8 );
			var target = output.AsSpan( b * 16, 16 );
			var c0 = BinaryPrimitives.ReadUInt16LittleEndian( block );
			var c1 = BinaryPrimitives.ReadUInt16LittleEndian( block[2..] );
			var indices = BinaryPrimitives.ReadUInt32LittleEndian( block[4..] );
			block[..4].CopyTo( target[8..] );

			if ( c0 > c1 )
			{
				// Four colour block: the same in BC3, fully opaque.
				target[0] = 255;
				target[1] = 255;
				BinaryPrimitives.WriteUInt32LittleEndian( target[12..], indices );
				continue;
			}

			Palette( c0, c1, false, stored );
			Palette( c0, c1, true, four );
			uint newIndices = 0;
			ulong alphaBits = 0;
			for ( var i = 0; i < 16; i++ )
			{
				var index = (int)(indices >> (i * 2) & 3);
				var transparent = index == 3;
				var r = transparent ? 0 : stored[index * 3];
				var g = transparent ? 0 : stored[index * 3 + 1];
				var bl = transparent ? 0 : stored[index * 3 + 2];
				newIndices |= (uint)Nearest( four, r, g, bl ) << (i * 2);
				if ( transparent )
					alphaBits |= 1UL << (i * 3);
			}

			// Alpha endpoints 255 and 0 in eight value mode: index 0 opaque, 1 transparent.
			target[0] = 255;
			target[1] = 0;
			for ( var j = 0; j < 6; j++ )
				target[2 + j] = (byte)(alphaBits >> (8 * j));
			BinaryPrimitives.WriteUInt32LittleEndian( target[12..], newIndices );
		}
		return output;
	}

	// BC2 to BC3: the colour block is the same (both always four colour), the explicit 4-bit alpha becomes an interpolated alpha block.
	public static byte[] Bc2ToBc3( ReadOnlySpan<byte> source, int width, int height )
	{
		var blocks = Math.Max( 1, (width + 3) / 4 ) * Math.Max( 1, (height + 3) / 4 );
		var output = new byte[blocks * 16];
		Span<byte> alpha = stackalloc byte[16];
		for ( var b = 0; b < blocks; b++ )
		{
			var block = source.Slice( b * 16, 16 );
			var bits = BinaryPrimitives.ReadUInt64LittleEndian( block );
			for ( var i = 0; i < 16; i++ )
				alpha[i] = (byte)((bits >> (i * 4) & 15) * 17);
			Bc4( alpha, output.AsSpan( b * 16, 8 ) );
			block[8..].CopyTo( output.AsSpan( b * 16 + 8, 8 ) );
		}
		return output;
	}

	// Two-channel R8G8 (signed or unsigned) to BC5, each channel stored as BitmapDecoder writes it (x * 0.5 + 0.5).
	public static byte[] R8G8ToBc5( ReadOnlySpan<byte> source, int width, int height, bool signed )
	{
		var blocksWide = Math.Max( 1, (width + 3) / 4 );
		var blocksHigh = Math.Max( 1, (height + 3) / 4 );
		var output = new byte[blocksWide * blocksHigh * 16];
		Span<byte> x = stackalloc byte[16];
		Span<byte> y = stackalloc byte[16];
		for ( var by = 0; by < blocksHigh; by++ )
		for ( var bx = 0; bx < blocksWide; bx++ )
		{
			for ( var i = 0; i < 16; i++ )
			{
				// Texels past the edge repeat the last row or column.
				var px = Math.Min( bx * 4 + (i & 3), width - 1 );
				var py = Math.Min( by * 4 + (i >> 2), height - 1 );
				var p = (py * width + px) * 2;
				x[i] = Unsigned( source[p], signed );
				y[i] = Unsigned( source[p + 1], signed );
			}
			var target = output.AsSpan( (by * blocksWide + bx) * 16, 16 );
			Bc4( x, target[..8] );
			Bc4( y, target[8..] );
		}
		return output;
	}

	static byte Unsigned( byte value, bool signed ) => signed
		? (byte)Math.Clamp( Math.Round( (Math.Max( (sbyte)value / 127.0, -1 ) * 0.5 + 0.5) * 255 ), 0, 255 )
		: value;

	// One unsigned BC4 block: the better of eight values between the extremes, or six between the inner extremes plus 0 and 255.
	public static void Bc4( ReadOnlySpan<byte> values, Span<byte> block )
	{
		int min = 255, max = 0, innerMin = 255, innerMax = 0;
		foreach ( var v in values )
		{
			min = Math.Min( min, v );
			max = Math.Max( max, v );
			if ( v is not (0 or 255) )
			{
				innerMin = Math.Min( innerMin, v );
				innerMax = Math.Max( innerMax, v );
			}
		}

		if ( min == max )
		{
			block.Clear();
			block[0] = (byte)min;
			block[1] = (byte)min;
			return;
		}

		Span<int> palette8 = stackalloc int[8];
		Span<int> palette6 = stackalloc int[8];
		Bc4Palette( max, min, palette8 );
		var error8 = Fit( values, palette8, out var bits8 );

		var error6 = long.MaxValue;
		ulong bits6 = 0;
		if ( innerMin <= innerMax && (min == 0 || max == 255) )
		{
			Bc4Palette( innerMin, innerMax, palette6 );
			error6 = Fit( values, palette6, out bits6 );
		}

		var six = error6 < error8;
		block[0] = (byte)(six ? innerMin : max);
		block[1] = (byte)(six ? innerMax : min);
		var bits = six ? bits6 : bits8;
		for ( var j = 0; j < 6; j++ )
			block[2 + j] = (byte)(bits >> (8 * j));
	}

	// The palette a decoder builds from two endpoints (eight values if e0 > e1), rounded as BitmapDecoder rounds.
	static void Bc4Palette( int e0, int e1, Span<int> palette )
	{
		palette[0] = e0;
		palette[1] = e1;
		if ( e0 > e1 )
		{
			for ( var k = 1; k < 7; k++ )
				palette[k + 1] = (int)Math.Round( ((7 - k) * e0 + k * e1) / 7.0 );
		}
		else
		{
			for ( var k = 1; k < 5; k++ )
				palette[k + 1] = (int)Math.Round( ((5 - k) * e0 + k * e1) / 5.0 );
			palette[6] = 0;
			palette[7] = 255;
		}
	}

	static long Fit( ReadOnlySpan<byte> values, ReadOnlySpan<int> palette, out ulong bits )
	{
		bits = 0;
		long error = 0;
		for ( var i = 0; i < 16; i++ )
		{
			int best = 0, bestError = int.MaxValue;
			for ( var k = 0; k < 8; k++ )
			{
				var d = Math.Abs( values[i] - palette[k] );
				if ( d < bestError )
				{
					bestError = d;
					best = k;
				}
			}
			bits |= (ulong)best << (3 * i);
			error += bestError * bestError;
		}
		return error;
	}

	static void Palette( ushort c0, ushort c1, bool fourColors, Span<int> rgb )
	{
		Unpack565( c0, rgb[0..] );
		Unpack565( c1, rgb[3..] );
		for ( var c = 0; c < 3; c++ )
		{
			rgb[6 + c] = fourColors ? (2 * rgb[c] + rgb[3 + c]) / 3 : (rgb[c] + rgb[3 + c]) / 2;
			rgb[9 + c] = fourColors ? (rgb[c] + 2 * rgb[3 + c]) / 3 : 0;
		}
	}

	static int Nearest( ReadOnlySpan<int> rgb, int r, int g, int b )
	{
		int best = 0, bestError = int.MaxValue;
		for ( var k = 0; k < 4; k++ )
		{
			var dr = rgb[k * 3] - r;
			var dg = rgb[k * 3 + 1] - g;
			var db = rgb[k * 3 + 2] - b;
			var e = dr * dr + dg * dg + db * db;
			if ( e < bestError )
			{
				bestError = e;
				best = k;
			}
		}
		return best;
	}

	static void Unpack565( ushort value, Span<int> rgb )
	{
		rgb[0] = (value >> 11 & 31) * 255 / 31;
		rgb[1] = (value >> 5 & 63) * 255 / 63;
		rgb[2] = (value & 31) * 255 / 31;
	}
}
