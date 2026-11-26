using System;

namespace Blam.Halo3;

// Inside the namespace so these win over s&box's global vector types when this compiles into the mount.
using Vector3 = System.Numerics.Vector3;

// An L2 SH light probe as Halo 3 stores it: 36 half floats, dominant light direction (xyz, 0), its intensity (rgb, 0), then 9 coefficients each for red, green and blue (D3DX order), 0 (lighting.md §4.1).
public readonly record struct ShProbe( Vector3 Direction, Vector3 Intensity, Vector3[] Coefficients )
{
	public const int Size = 72;

	public bool IsZero => Coefficients is null || Array.TrueForAll( Coefficients, c => c == Vector3.Zero );

	public static ShProbe Read( TagView view, int field )
	{
		var b = view.Bytes( field, Size );
		var coefficients = new Vector3[9];
		for ( var k = 0; k < 9; k++ )
			coefficients[k] = new Vector3( Half( b, 8 + k ), Half( b, 17 + k ), Half( b, 26 + k ) );
		return new ShProbe( new Vector3( Half( b, 0 ), Half( b, 1 ), Half( b, 2 ) ), new Vector3( Half( b, 4 ), Half( b, 5 ), Half( b, 6 ) ), coefficients );
	}

	static float Half( ReadOnlySpan<byte> b, int i ) => (float)BitConverter.ToHalf( b.Slice( i * 2, 2 ) );
}

// One Lbsp's lightmap decode data and probes (scenario_lightmap_bsp_data, lighting.md §1.2).
public sealed class LightmapBspSh
{
	public TagEntry Tag { get; }

	// The five decode scales of the SH pairs: c0, c1, c2, c3, dominant intensity (Lbsp +0x08, +0x20, +0x38, +0x50, +0x68).
	public float[] Scales { get; }

	// The lightprobe texture array (8 DXT5 layers) and the dominant light intensity array (2 layers).
	public TagEntry LightprobeArray { get; }
	public TagEntry IntensityArray { get; }

	public LightmapBspSh( TagEntry lbsp )
	{
		Tag = lbsp;
		var data = lbsp.Data;
		Scales = [data.F32( 0x08 ), data.F32( 0x20 ), data.F32( 0x38 ), data.F32( 0x50 ), data.F32( 0x68 )];
		LightprobeArray = data.Reference( 0xE0 );
		IntensityArray = data.Reference( 0xF0 );
	}

	// The per-instance probes (Lbsp +0x124, 0x48 each), indexed by an Lbsp instance's probe index (instances +0x118, i16 at +4).
	public int ProbeCount => Tag.Data.Count( 0x124 );

	public ShProbe Probe( int index )
	{
		var element = Tag.Data.Element( 0x124, 0x48, index );
		return element.IsNull ? default : ShProbe.Read( element, 0 );
	}

	// An instance's probe index (Lbsp +0x118 instances, 8 bytes: lightprobe texture index, per-vertex block index, probe index); -1 for none.
	public int InstanceProbeIndex( int instance )
	{
		var element = Tag.Data.Element( 0x118, 8, instance );
		return element.IsNull ? -1 : element.I16( 4 );
	}

	// A scenery placement's probe from this BSP's scenery probes (Lbsp +0x1C0, 0x50 each: u32 object unique id, ..., probe at +0x08), or null.
	public ShProbe? SceneryProbe( uint objectId )
	{
		foreach ( var element in Tag.Data.Block( 0x1C0, 0x50 ) )
		{
			if ( element.U32( 0 ) == objectId )
				return ShProbe.Read( element, 0x08 );
		}
		return null;
	}
}

// Per-vertex lightmap decode (lighting.md §3): the format 6 stream is 5 RGBA8 shared-exponent vectors per vertex, I then c0..c3.
public static class LightmapVertexSh
{
	public const int Stride = 20;

	// One vertex: dominant light intensity and the L1 coefficients c0..c3 (RGB each).
	public static (Vector3 Intensity, Vector3 C0, Vector3 C1, Vector3 C2, Vector3 C3) Decode( ReadOnlySpan<byte> vertex ) =>
		(Value( vertex, 0 ), Value( vertex, 1 ), Value( vertex, 2 ), Value( vertex, 3 ), Value( vertex, 4 ));

	// (rgb * 2 - 1) * 2^(31.75 * (a * 2 - 1)).
	static Vector3 Value( ReadOnlySpan<byte> vertex, int k )
	{
		var o = k * 4;
		var scale = MathF.Pow( 2, 31.75f * (vertex[o + 3] / 255f * 2 - 1) );
		return new Vector3( vertex[o] / 255f * 2 - 1, vertex[o + 1] / 255f * 2 - 1, vertex[o + 2] / 255f * 2 - 1 ) * scale;
	}
}

// How bright a map's static lighting is, for picking an exposure: the geometric mean of its lightmaps' DC irradiance / pi (luminance), sampled at one texel per 4x4 block on a sparse grid.
public static class LightmapBrightness
{
	static readonly System.Runtime.CompilerServices.ConditionalWeakTable<CacheFile, object> Cache = new();

	// log2 of the mean, or null for a map without readable lightmaps.
	public static float? MeanLog2Irradiance( CacheFile cache )
	{
		if ( cache is null )
			return null;
		var boxed = Cache.GetValue( cache, c => Measure( c ) is { } v ? v : "none" );
		return boxed is float f ? f : null;
	}

	// A typical Halo 3 surface's albedo (linear), between the lightmap's irradiance and the frame's luminance.
	public const float TypicalAlbedo = 0.2f;

	// Exposure in stops when nothing better is known.
	public const float DefaultExposureStops = 1;

	// A fixed exposure for a map: where its camera fx's auto exposure (scnr +0x660 cfxs: target +0x18 and range +0x10, in stops) would settle for a frame of typical surfaces under its average lightmap light.
	// MCC's settles where target = measured log2 luminance of the exposed frame, whose exposure is 2^stops / 1.4938 (the tone curve's white point).
	public static (float Stops, float Target, float Min, float Max, float? Log2Irradiance) Exposure( CacheFile cache )
	{
		var cfxs = cache?.Scenario?.Data.Reference( 0x660 );
		var log2 = MeanLog2Irradiance( cache );
		if ( cfxs is null || cfxs.Data.IsNull )
			return (DefaultExposureStops, float.NaN, float.NaN, float.NaN, log2);

		var data = cfxs.Data;
		float target = data.F32( 0x18 ), min = data.F32( 0x10 ), max = data.F32( 0x14 );
		var frame = (log2 ?? -2.8f) + MathF.Log2( TypicalAlbedo );
		var stops = float.IsFinite( target ) ? target - frame + MathF.Log2( 1.4938016f ) : DefaultExposureStops;
		if ( float.IsFinite( min ) && float.IsFinite( max ) && min <= max )
			stops = Math.Clamp( stops, min, max );
		return (stops, target, min, max, log2);
	}

	static float? Measure( CacheFile cache )
	{
		var sldt = cache.Scenario?.Data.Reference( 0x6AC );
		if ( sldt is null || sldt.Data.IsNull )
			return null;

		double sum = 0;
		long count = 0;
		foreach ( var element in sldt.Data.Block( 0x04, 0x10 ) )
		{
			var lbsp = element.Reference( 0 );
			if ( lbsp is null || lbsp.Data.IsNull )
				continue;
			try
			{
				Sample( cache, new LightmapBspSh( lbsp ), ref sum, ref count );
			}
			catch ( Exception )
			{
				// A lightmap whose pixels are stripped or unreadable doesn't count.
			}
		}
		return count == 0 ? null : (float)(sum / count);
	}

	static void Sample( CacheFile cache, LightmapBspSh lightmap, ref double sum, ref long count )
	{
		var bitmap = lightmap.LightprobeArray;
		if ( bitmap is null )
			return;
		var image = bitmap.Data.Element( 0x60, 0x38, 0 );
		var handles = bitmap.Data.Block( 0x8C, 8 );
		if ( image.IsNull || handles.Count == 0 || image.I16( 0x0C ) != 16 )
			return;
		int width = image.I16( 4 ), height = image.I16( 6 );
		var data = cache.Resources.Read( handles[0].U32( 0 ) );
		if ( data is null )
			return;
		var pixels = data.Secondary.Length > data.Primary.Length ? data.Secondary.Span : data.Primary.Span;
		var layer = width * height;
		if ( width < 4 || height < 4 || pixels.Length < layer * 2 )
			return;

		var scale = lightmap.Scales[0];
		var blocksX = width / 4;
		var blocksY = height / 4;
		var step = Math.Max( 1, Math.Min( blocksX, blocksY ) / 64 );
		for ( var by = 0; by < blocksY; by += step )
		{
			for ( var bx = 0; bx < blocksX; bx += step )
			{
				var offset = (by * blocksX + bx) * 16;
				var a = FirstTexel( pixels.Slice( offset, 16 ) );
				var b = FirstTexel( pixels.Slice( layer + offset, 16 ) );
				if ( a.W == 0 && b.W == 0 )
					continue;
				var c0 = ((a + b) * 2 - new System.Numerics.Vector4( 2 )) * (a.W * b.W * scale);
				var luminance = 0.212656f * c0.X + 0.715158f * c0.Y + 0.072186f * c0.Z;
				var irradiance = 0.886227f * luminance / MathF.PI;
				sum += Math.Log2( Math.Max( irradiance, 1e-4 ) );
				count++;
			}
		}
	}

	// Texel (0, 0) of a DXT5 block as unorm rgba.
	static System.Numerics.Vector4 FirstTexel( ReadOnlySpan<byte> block )
	{
		int a0 = block[0], a1 = block[1];
		var ai = block[2] & 7;
		float alpha = ai switch
		{
			0 => a0,
			1 => a1,
			_ when a0 > a1 => ((8 - ai) * a0 + (ai - 1) * a1) / 7f,
			6 => 0,
			7 => 255,
			_ => ((6 - ai) * a0 + (ai - 1) * a1) / 5f,
		};
		var c0 = Rgb565( block[8] | block[9] << 8 );
		var c1 = Rgb565( block[10] | block[11] << 8 );
		var ci = block[12] & 3;
		var rgb = ci switch
		{
			0 => c0,
			1 => c1,
			2 => (2 * c0 + c1) / 3,
			_ => (c0 + 2 * c1) / 3,
		};
		return new System.Numerics.Vector4( rgb, alpha / 255f );
	}

	static Vector3 Rgb565( int v ) => new( (v >> 11 & 31) / 31f, (v >> 5 & 63) / 63f, (v & 31) / 31f );
}
