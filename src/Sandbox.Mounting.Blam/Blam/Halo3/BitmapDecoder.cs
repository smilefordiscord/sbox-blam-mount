using System;
using System.Buffers.Binary;
using Blam.Halo3.Tags;

namespace Blam.Halo3;

// Decodes one level of any DXGI format Halo 3 uses into RGBA8888.
// Signed two-channel normals (BC5 and R8G8 SNORM) come out biased like an unsigned normal map: x, y, z = sqrt(1 - x² - y²), each * 0.5 + 0.5.
// A8 comes out white with its alpha, R8 grey, float formats clamped to 0..1, nothing is converted between gamma curves.
public static class BitmapDecoder
{
	public static void ToRgba8888( DxgiFormat format, ReadOnlySpan<byte> source, Span<byte> destination, int width, int height )
	{
		var count = width * height;
		switch ( format )
		{
			case DxgiFormat.BC1Unorm or DxgiFormat.BC1UnormSrgb:
				Blocks( source, destination, width, height, 8, ( block, texels ) => Bc1( block, texels, false ) );
				break;

			case DxgiFormat.BC2Unorm or DxgiFormat.BC2UnormSrgb:
				Blocks( source, destination, width, height, 16, Bc2 );
				break;

			case DxgiFormat.BC3Unorm or DxgiFormat.BC3UnormSrgb:
				Blocks( source, destination, width, height, 16, Bc3 );
				break;

			case DxgiFormat.BC4Unorm or DxgiFormat.BC4Snorm:
				var signed4 = format == DxgiFormat.BC4Snorm;
				Blocks( source, destination, width, height, 8, ( block, texels ) => Bc4Grey( block, texels, signed4 ) );
				break;

			case DxgiFormat.BC5Unorm or DxgiFormat.BC5Snorm:
				var signed5 = format == DxgiFormat.BC5Snorm;
				Blocks( source, destination, width, height, 16, ( block, texels ) => Bc5( block, texels, signed5 ) );
				break;

			// Black with alpha, as MCC's A8_UNORM resources sample under D3D11: Orbital's ad screens add only their cube's reflection through glass_light_sign_f_dif, not a white sheet.
			case DxgiFormat.A8Unorm:
				for ( var i = 0; i < count; i++ )
					Set( destination, i, 0, 0, 0, source[i] );
				break;

			case DxgiFormat.R8Unorm:
				for ( var i = 0; i < count; i++ )
					Set( destination, i, source[i], source[i], source[i], 255 );
				break;

			case DxgiFormat.R8G8Snorm:
				for ( var i = 0; i < count; i++ )
					Normal( destination, i, Snorm( source[i * 2] ), Snorm( source[i * 2 + 1] ) );
				break;

			case DxgiFormat.R8G8Unorm:
				for ( var i = 0; i < count; i++ )
					Normal( destination, i, source[i * 2] / 255.0 * 2 - 1, source[i * 2 + 1] / 255.0 * 2 - 1 );
				break;

			case DxgiFormat.R8G8B8A8Unorm:
				source[..(count * 4)].CopyTo( destination );
				break;

			case DxgiFormat.R8G8B8A8Snorm:
				for ( var i = 0; i < count * 4; i++ )
					destination[i] = To8( Snorm( source[i] ) * 0.5 + 0.5 );
				break;

			case DxgiFormat.B8G8R8A8Unorm or DxgiFormat.B8G8R8A8UnormSrgb or DxgiFormat.B8G8R8X8Unorm or DxgiFormat.B8G8R8X8UnormSrgb:
				var opaque = format is DxgiFormat.B8G8R8X8Unorm or DxgiFormat.B8G8R8X8UnormSrgb;
				for ( var i = 0; i < count; i++ )
					Set( destination, i, source[i * 4 + 2], source[i * 4 + 1], source[i * 4], opaque ? (byte)255 : source[i * 4 + 3] );
				break;

			case DxgiFormat.B4G4R4A4Unorm:
				for ( var i = 0; i < count; i++ )
				{
					var p = BinaryPrimitives.ReadUInt16LittleEndian( source[(i * 2)..] );
					Set( destination, i, (byte)((p >> 8 & 15) * 17), (byte)((p >> 4 & 15) * 17), (byte)((p & 15) * 17), (byte)((p >> 12 & 15) * 17) );
				}
				break;

			case DxgiFormat.R16G16B16A16Float:
				for ( var i = 0; i < count * 4; i++ )
					destination[i] = To8( (double)BinaryPrimitives.ReadHalfLittleEndian( source[(i * 2)..] ) );
				break;

			case DxgiFormat.R32G32B32A32Float:
				for ( var i = 0; i < count * 4; i++ )
					destination[i] = To8( BinaryPrimitives.ReadSingleLittleEndian( source[(i * 4)..] ) );
				break;

			default:
				throw new NotSupportedException( $"Can't decode DXGI format {(uint)format}" );
		}
	}

	public static byte[] ToRgba8888( DxgiFormat format, ReadOnlySpan<byte> source, int width, int height )
	{
		var rgba = new byte[width * height * 4];
		ToRgba8888( format, source, rgba, width, height );
		return rgba;
	}

	// Whether BC1 data has transparent texels (index 3 of a three colour block), which s&box's DXT1 drops.
	public static bool Bc1HasTransparency( ReadOnlySpan<byte> source )
	{
		for ( var block = 0; block + 8 <= source.Length; block += 8 )
		{
			if ( BinaryPrimitives.ReadUInt16LittleEndian( source[block..] ) > BinaryPrimitives.ReadUInt16LittleEndian( source[(block + 2)..] ) )
				continue;

			var indices = BinaryPrimitives.ReadUInt32LittleEndian( source[(block + 4)..] );
			for ( var i = 0; i < 16; i++ )
			{
				if ( (indices >> (i * 2) & 3) == 3 )
					return true;
			}
		}
		return false;
	}

	// BC4 block -> 16 values, 0..1 unsigned or -1..1 signed (endpoints clamped to -127), interpolated in floating point.
	public static void Bc4Values( ReadOnlySpan<byte> block, bool signed, Span<double> values )
	{
		double e0, e1, lo, hi;
		if ( signed )
		{
			e0 = Math.Max( (int)(sbyte)block[0], -127 );
			e1 = Math.Max( (int)(sbyte)block[1], -127 );
			lo = -127;
			hi = 127;
		}
		else
		{
			e0 = block[0];
			e1 = block[1];
			lo = 0;
			hi = 255;
		}

		Span<double> palette = stackalloc double[8];
		palette[0] = e0;
		palette[1] = e1;
		if ( e0 > e1 )
		{
			for ( var k = 1; k < 7; k++ )
				palette[k + 1] = ((7 - k) * e0 + k * e1) / 7.0;
		}
		else
		{
			for ( var k = 1; k < 5; k++ )
				palette[k + 1] = ((5 - k) * e0 + k * e1) / 5.0;
			palette[6] = lo;
			palette[7] = hi;
		}

		ulong bits = 0;
		for ( var j = 0; j < 6; j++ )
			bits |= (ulong)block[2 + j] << (8 * j);
		for ( var i = 0; i < 16; i++ )
			values[i] = palette[(int)(bits >> (3 * i) & 7)] / hi;
	}

	delegate void BlockDecoder( ReadOnlySpan<byte> block, Span<byte> texels );

	// Runs a decoder over every 4x4 block (texels: 16 RGBA texels in row order) and copies the visible part out.
	static void Blocks( ReadOnlySpan<byte> source, Span<byte> destination, int width, int height, int blockSize, BlockDecoder decode )
	{
		var blocksWide = Math.Max( 1, (width + 3) / 4 );
		var blocksHigh = Math.Max( 1, (height + 3) / 4 );
		Span<byte> texels = stackalloc byte[64];
		for ( var by = 0; by < blocksHigh; by++ )
		for ( var bx = 0; bx < blocksWide; bx++ )
		{
			decode( source.Slice( (by * blocksWide + bx) * blockSize, blockSize ), texels );
			for ( var i = 0; i < 16; i++ )
			{
				var x = bx * 4 + (i & 3);
				var y = by * 4 + (i >> 2);
				if ( x < width && y < height )
					texels.Slice( i * 4, 4 ).CopyTo( destination[((y * width + x) * 4)..] );
			}
		}
	}

	static void Bc1( ReadOnlySpan<byte> block, Span<byte> texels, bool fourColorsAlways )
	{
		var c0 = BinaryPrimitives.ReadUInt16LittleEndian( block );
		var c1 = BinaryPrimitives.ReadUInt16LittleEndian( block[2..] );
		var indices = BinaryPrimitives.ReadUInt32LittleEndian( block[4..] );
		var four = fourColorsAlways || c0 > c1;

		Span<byte> palette = stackalloc byte[16];
		Unpack565( c0, palette[0..] );
		Unpack565( c1, palette[4..] );
		for ( var c = 0; c < 3; c++ )
		{
			palette[8 + c] = (byte)(four ? (2 * palette[c] + palette[4 + c]) / 3 : (palette[c] + palette[4 + c]) / 2);
			palette[12 + c] = (byte)(four ? (palette[c] + 2 * palette[4 + c]) / 3 : 0);
		}
		palette[3] = palette[7] = palette[11] = 255;
		palette[15] = four ? (byte)255 : (byte)0;

		for ( var i = 0; i < 16; i++ )
			palette.Slice( (int)(indices >> (i * 2) & 3) * 4, 4 ).CopyTo( texels[(i * 4)..] );
	}

	static void Bc2( ReadOnlySpan<byte> block, Span<byte> texels )
	{
		Bc1( block[8..], texels, true );
		var alpha = BinaryPrimitives.ReadUInt64LittleEndian( block );
		for ( var i = 0; i < 16; i++ )
			texels[i * 4 + 3] = (byte)((alpha >> (i * 4) & 15) * 17);
	}

	static void Bc3( ReadOnlySpan<byte> block, Span<byte> texels )
	{
		Bc1( block[8..], texels, true );
		Span<double> alpha = stackalloc double[16];
		Bc4Values( block, false, alpha );
		for ( var i = 0; i < 16; i++ )
			texels[i * 4 + 3] = To8( alpha[i] );
	}

	static void Bc4Grey( ReadOnlySpan<byte> block, Span<byte> texels, bool signed )
	{
		Span<double> v = stackalloc double[16];
		Bc4Values( block, signed, v );
		for ( var i = 0; i < 16; i++ )
		{
			var g = To8( signed ? v[i] * 0.5 + 0.5 : v[i] );
			Set( texels, i, g, g, g, 255 );
		}
	}

	static void Bc5( ReadOnlySpan<byte> block, Span<byte> texels, bool signed )
	{
		Span<double> x = stackalloc double[16];
		Span<double> y = stackalloc double[16];
		Bc4Values( block, signed, x );
		Bc4Values( block[8..], signed, y );
		for ( var i = 0; i < 16; i++ )
			Normal( texels, i, signed ? x[i] : x[i] * 2 - 1, signed ? y[i] : y[i] * 2 - 1 );
	}

	static void Normal( Span<byte> destination, int i, double x, double y )
	{
		var z = Math.Sqrt( Math.Clamp( 1 - x * x - y * y, 0, 1 ) );
		Set( destination, i, To8( x * 0.5 + 0.5 ), To8( y * 0.5 + 0.5 ), To8( z * 0.5 + 0.5 ), 255 );
	}

	static double Snorm( byte value ) => Math.Max( (sbyte)value / 127.0, -1 );

	static byte To8( double v ) => (byte)Math.Clamp( Math.Round( v * 255 ), 0, 255 );

	static void Unpack565( ushort value, Span<byte> rgb )
	{
		rgb[0] = (byte)((value >> 11 & 31) * 255 / 31);
		rgb[1] = (byte)((value >> 5 & 63) * 255 / 63);
		rgb[2] = (byte)((value & 31) * 255 / 31);
	}

	static void Set( Span<byte> destination, int i, byte r, byte g, byte b, byte a )
	{
		destination[i * 4] = r;
		destination[i * 4 + 1] = g;
		destination[i * 4 + 2] = b;
		destination[i * 4 + 3] = a;
	}
}
