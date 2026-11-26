using System;
using Blam.Halo2.Tags;

namespace Blam.Halo2.TagData;

// How Halo 2's sphere lights fall off: the globals' distance attenuation volume, 1 at its centre and 0 at its faces.
// A sphere light at L lights P by the texel at 0.5 + 0.5 (P - L) / R, R the larger of its radius and specular radius.
// That radius is scaled by the light's size modifier, interpolated by its scale.
// Curve is the volume filtered as the GPU does along an axis from its centre, at even steps of distance / R up to 1.
public static class LightFalloff
{
	public const int SampleCount = 33;

	// The curve, or null if the bitmap isn't a volume of 32 bit texels.
	public static float[] Curve( BitmapData volume, byte[] pixels )
	{
		if ( volume.Type != BitmapType.Texture3D || volume.ElementSize != 4 || pixels.Length < volume.GetLevelSize( 0 ) )
			return null;

		var curve = new float[SampleCount];
		for ( var i = 0; i < SampleCount; i++ )
			curve[i] = MathF.Round( Sample( volume, pixels, 0.5f + 0.5f * i / (SampleCount - 1), 0.5f, 0.5f ), 5 );

		return curve;
	}

	// Trilinear filtering of the top level's red channel, texels outside the volume being 0.
	static float Sample( BitmapData volume, byte[] pixels, float u, float v, float w )
	{
		var (x, fx) = Texel( u, volume.Width );
		var (y, fy) = Texel( v, volume.Height );
		var (z, fz) = Texel( w, volume.Depth );
		var sum = 0f;
		for ( var corner = 0; corner < 8; corner++ )
		{
			var dx = corner & 1;
			var dy = (corner >> 1) & 1;
			var dz = corner >> 2;
			var weight = (dx == 0 ? 1 - fx : fx) * (dy == 0 ? 1 - fy : fy) * (dz == 0 ? 1 - fz : fz);
			sum += weight * Red( volume, pixels, x + dx, y + dy, z + dz );
		}

		return sum;
	}

	static (int Index, float Fraction) Texel( float coordinate, int size )
	{
		var position = coordinate * size - 0.5f;
		var index = (int)MathF.Floor( position );
		return (index, position - index);
	}

	// Texels are stored blue, green, red, alpha.
	static float Red( BitmapData volume, byte[] pixels, int x, int y, int z )
	{
		if ( x < 0 || y < 0 || z < 0 || x >= volume.Width || y >= volume.Height || z >= volume.Depth )
			return 0;

		return pixels[((z * volume.Height + y) * volume.Width + x) * 4 + 2] / 255f;
	}
}
