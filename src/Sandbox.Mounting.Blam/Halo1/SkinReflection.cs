using System.Runtime.CompilerServices;
using Blam.Halo1;
using Blam.Halo1.Tags;
using Vec3 = System.Numerics.Vector3;

namespace Sandbox.Mounting.Blam.Halo1;

/// <summary>
/// A Halo cube map blurred for rough surfaces, for the skin materials' reflections (see
/// <see cref="ModelMaterial.SkinShaderPath"/>): <see cref="Levels"/> rows of six faces (+X, -X, +Y,
/// -Y, +Z, -Z, as the cube map atlases), row k blurred for roughness (k + 1) / Levels by the GGX
/// distribution, as a physically based renderer prefilters its environment. Roughness 0 reads the
/// cube map itself. With it, the cube map's average color, which the skin shader divides by to
/// use the cube map only for its shape (where it's lighter or darker), under Halo's light.
/// </summary>
/// <remarks>
/// Faces are <see cref="FaceSize"/> square: Halo's cube maps are 16 to 128 texels a face, and
/// blurred they hold no detail a bigger face would keep. Colors are blurred in linear light, from
/// and back to Halo's display values with the sRGB curve, as halo1_model.shader converts them.
/// </remarks>
static class SkinReflection
{
	public const int Levels = 5;
	public const int FaceSize = 32;
	const int SampleCount = 128;

	static readonly ConditionalWeakTable<CacheFile, Dictionary<uint, (Texture Texture, Vector3 Mean)>> _cache = new();

	/// <summary>
	/// The blurred atlas of a cube map bitmap tag and the cube map's average color (display
	/// values), made once per map. Null if it isn't a cube map this can decode.
	/// </summary>
	public static Texture Get( HaloCeMount host, CacheFile cache, TagEntry cubeMap, out Vector3 mean )
	{
		mean = Vector3.One;
		if ( cubeMap is null )
			return null;

		var byTag = _cache.GetValue( cache, _ => new Dictionary<uint, (Texture, Vector3)>() );
		lock ( byTag )
		{
			if ( byTag.TryGetValue( cubeMap.Id, out var found ) )
			{
				mean = found.Mean;
				return found.Texture;
			}
		}

		var faces = ReadFaces( host, cache, cubeMap, out var size );
		var built = faces is null ? null : Build( faces, size, $"{HaloCeMount.ResourcePath( cubeMap.Path )}/skin_reflection" );
		if ( faces is not null )
		{
			var sum = faces.Aggregate( Vec3.Zero, ( total, face ) => face.Aggregate( total, ( t, c ) => t + c ) );
			var average = sum / (6 * size * size);
			mean = new Vector3( ToDisplay( average.X ), ToDisplay( average.Y ), ToDisplay( average.Z ) ) / 255f;
		}

		lock ( byTag )
			byTag[cubeMap.Id] = (built, mean);

		return built;
	}

	/// <summary>
	/// The cube map's six faces in linear RGB, atlas order, at their full size.
	/// </summary>
	static Vec3[][] ReadFaces( HaloCeMount host, CacheFile cache, TagEntry cubeMap, out int size )
	{
		size = 0;
		var bitmap = new BitmapTag( cache, cubeMap ).Bitmaps.FirstOrDefault();
		if ( bitmap is null || bitmap.Type != BitmapType.CubeMap || !BitmapDecoder.CanDecodeImage( bitmap.Format ) )
			return null;

		size = bitmap.Width;
		var pixels = BitmapTextureLoader.ReadPixels( host, cache.FilePath, bitmap );
		var faceBytes = bitmap.GetLevelSize( 0 );

		// Stored +X, +Y, -X, -Y, +Z, -Z (see BitmapTextureLoader.LoadCubeMap).
		ReadOnlySpan<int> storedFace = [0, 2, 1, 3, 4, 5];
		var faces = new Vec3[6][];
		var rgba = new byte[size * size * 4];
		for ( var face = 0; face < 6; face++ )
		{
			BitmapDecoder.DecodeImage( bitmap.Format, pixels.AsSpan( storedFace[face] * faceBytes, faceBytes ), rgba, size, size );
			faces[face] = new Vec3[size * size];
			for ( var i = 0; i < size * size; i++ )
				faces[face][i] = new Vec3( ToLinear( rgba[i * 4] ), ToLinear( rgba[i * 4 + 1] ), ToLinear( rgba[i * 4 + 2] ) );
		}

		return faces;
	}

	static Texture Build( Vec3[][] source, int sourceSize, string name )
	{
		// Box filtered down to the output size first, so the samples don't alias.
		var faces = source;
		var size = sourceSize;
		while ( size > FaceSize )
		{
			faces = faces.Select( f => Halve( f, size ) ).ToArray();
			size /= 2;
		}

		var outSize = FaceSize;
		var atlas = new byte[outSize * 6 * outSize * Levels * 4];
		for ( var level = 0; level < Levels; level++ )
		{
			var roughness = (level + 1) / (float)Levels;
			var lobe = Lobe( roughness * roughness );
			for ( var face = 0; face < 6; face++ )
			for ( var y = 0; y < outSize; y++ )
			for ( var x = 0; x < outSize; x++ )
			{
				var n = Direction( face, (x + 0.5f) / outSize, (y + 0.5f) / outSize );
				var color = Prefilter( faces, size, n, lobe );
				var o = (((level * outSize + y) * outSize * 6) + face * outSize + x) * 4;
				atlas[o] = ToDisplay( color.X );
				atlas[o + 1] = ToDisplay( color.Y );
				atlas[o + 2] = ToDisplay( color.Z );
				atlas[o + 3] = 255;
			}
		}

		return Texture.Create( outSize * 6, outSize * Levels, ImageFormat.RGBA8888 )
			.WithName( name )
			.WithData( atlas )
			.WithStaticUsage()
			.Finish();
	}

	/// <summary>
	/// Half vectors importance sampled from the GGX distribution of width alpha, around +Z:
	/// Hammersley points mapped to the lobe.
	/// </summary>
	static Vec3[] Lobe( float alpha )
	{
		var lobe = new Vec3[SampleCount];
		for ( var i = 0; i < SampleCount; i++ )
		{
			var u = (i + 0.5f) / SampleCount;
			var phi = 2 * MathF.PI * RadicalInverse( (uint)i );
			var cosTheta = MathF.Sqrt( (1 - u) / (1 + (alpha * alpha - 1) * u) );
			var sinTheta = MathF.Sqrt( 1 - cosTheta * cosTheta );
			lobe[i] = new Vec3( sinTheta * MathF.Cos( phi ), sinTheta * MathF.Sin( phi ), cosTheta );
		}

		return lobe;
	}

	/// <summary>
	/// The environment around n seen through a GGX lobe, with n as the view and reflection
	/// direction (the usual split sum approximation): each light direction the lobe's half vectors
	/// reflect n into, weighted by n.l.
	/// </summary>
	static Vec3 Prefilter( Vec3[][] faces, int size, Vec3 n, Vec3[] lobe )
	{
		var up = MathF.Abs( n.Z ) < 0.999f ? Vec3.UnitZ : Vec3.UnitX;
		var tangent = Vec3.Normalize( Vec3.Cross( up, n ) );
		var bitangent = Vec3.Cross( n, tangent );

		var sum = Vec3.Zero;
		var weight = 0f;
		foreach ( var sample in lobe )
		{
			var h = tangent * sample.X + bitangent * sample.Y + n * sample.Z;
			var l = 2 * Vec3.Dot( n, h ) * h - n;
			var nl = Vec3.Dot( n, l );
			if ( nl <= 0 )
				continue;

			sum += Sample( faces, size, l ) * nl;
			weight += nl;
		}

		return weight > 0 ? sum / weight : Sample( faces, size, n );
	}

	static float RadicalInverse( uint bits )
	{
		bits = (bits << 16) | (bits >> 16);
		bits = ((bits & 0x55555555u) << 1) | ((bits & 0xAAAAAAAAu) >> 1);
		bits = ((bits & 0x33333333u) << 2) | ((bits & 0xCCCCCCCCu) >> 2);
		bits = ((bits & 0x0F0F0F0Fu) << 4) | ((bits & 0xF0F0F0F0u) >> 4);
		bits = ((bits & 0x00FF00FFu) << 8) | ((bits & 0xFF00FF00u) >> 8);
		return bits * 2.3283064365386963e-10f;
	}

	/// <summary>
	/// The texel a direction lands on, by Direct3D's cube map rules, as SampleCubeAtlas in
	/// halo1_model.shader picks it.
	/// </summary>
	static Vec3 Sample( Vec3[][] faces, int size, Vec3 d )
	{
		var a = Vec3.Abs( d );
		int face;
		float s, t, ma;
		if ( a.X >= a.Y && a.X >= a.Z )
		{
			ma = a.X;
			face = d.X > 0 ? 0 : 1;
			(s, t) = d.X > 0 ? (-d.Z, -d.Y) : (d.Z, -d.Y);
		}
		else if ( a.Y >= a.Z )
		{
			ma = a.Y;
			face = d.Y > 0 ? 2 : 3;
			(s, t) = d.Y > 0 ? (d.X, d.Z) : (d.X, -d.Z);
		}
		else
		{
			ma = a.Z;
			face = d.Z > 0 ? 4 : 5;
			(s, t) = d.Z > 0 ? (d.X, -d.Y) : (-d.X, -d.Y);
		}

		var x = Math.Clamp( (int)((s / ma * 0.5f + 0.5f) * size), 0, size - 1 );
		var y = Math.Clamp( (int)((t / ma * 0.5f + 0.5f) * size), 0, size - 1 );
		return faces[face][y * size + x];
	}

	/// <summary>
	/// The direction through a point (u, v in 0-1) of a face: the inverse of <see cref="Sample"/>.
	/// </summary>
	static Vec3 Direction( int face, float u, float v )
	{
		var s = u * 2 - 1;
		var t = v * 2 - 1;
		var d = face switch
		{
			0 => new Vec3( 1, -t, -s ),
			1 => new Vec3( -1, -t, s ),
			2 => new Vec3( s, 1, t ),
			3 => new Vec3( s, -1, -t ),
			4 => new Vec3( s, -t, 1 ),
			_ => new Vec3( -s, -t, -1 ),
		};
		return Vec3.Normalize( d );
	}

	static Vec3[] Halve( Vec3[] face, int size )
	{
		var half = size / 2;
		var result = new Vec3[half * half];
		for ( var y = 0; y < half; y++ )
		for ( var x = 0; x < half; x++ )
		{
			var i = y * 2 * size + x * 2;
			result[y * half + x] = (face[i] + face[i + 1] + face[i + size] + face[i + size + 1]) * 0.25f;
		}

		return result;
	}

	static float ToLinear( byte value )
	{
		var c = value / 255f;
		return c <= 0.04045f ? c / 12.92f : MathF.Pow( (c + 0.055f) / 1.055f, 2.4f );
	}

	static byte ToDisplay( float c )
	{
		c = Math.Clamp( c, 0, 1 );
		var v = c <= 0.0031308f ? c * 12.92f : 1.055f * MathF.Pow( c, 1 / 2.4f ) - 0.055f;
		return (byte)Math.Clamp( (int)MathF.Round( v * 255 ), 0, 255 );
	}
}
