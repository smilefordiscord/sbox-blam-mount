using System;
using System.Collections.Generic;
using System.Linq;
using Blam.Halo3.Tags;

namespace Blam.Halo3;

// Inside the namespace so these win over s&box's global vector types when this compiles into the mount.
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;
using Vector4 = System.Numerics.Vector4;

// One point of a BSP's per-pixel lightmap: the L1 SH (c0..c3, RGB, Halo's order) and the dominant light intensity, decoded as halo3_shader decodes them (lighting.md §2).
// Normal and Albedo: the surface the sample was taken on, for the light it bounces back up (ToProbe).
public readonly record struct LightmapSample( Vector3 C0, Vector3 C1, Vector3 C2, Vector3 C3, Vector3 Intensity, Vector3 Position, int Bsp )
{
	public Vector3 Normal { get; init; } = Vector3.UnitZ;
	public Vector3 Albedo { get; init; } = new( 0.5f );

	static readonly Vector3 Luma = new( 0.212656f, 0.715158f, 0.072186f );

	// The dominant light's direction (toward the light): the luminance-weighted L1, as every probe stores it.
	public Vector3 DominantDirection
	{
		get
		{
			var d = new Vector3( -Vector3.Dot( C3, Luma ), -Vector3.Dot( C1, Luma ), Vector3.Dot( C2, Luma ) );
			var len = d.Length();
			return len > 1e-6f ? d / len : Vector3.UnitZ;
		}
	}

	// The light the surface under an object bounces back up at it, as Halo 3 adds it to a lightmap sample: the sample's irradiance at the surface normal over pi, times the surface's colour and 1.5, as a directional light from the surface (its SH projection scaled by 16 pi / 17).
	public const float GroundBounce = 1.5f;
	const float DirectionalScale = 2.956793f;

	// The L2 probe MCC lights a dynamic object with: this sample's L1, the dominant light's own L2 projection and the bounce off the surface.
	public LightProbe ToProbe()
	{
		var d = DominantDirection;
		var i = Intensity;
		var c = new Vector3[9];
		c[0] = C0;
		c[1] = C1;
		c[2] = C2;
		c[3] = C3;
		// Halo's convention flips the terms odd in x or y (lighting.md §4.1: c1 ~ -y, c3 ~ -x).
		c[4] = 1.092548f * d.X * d.Y * i;
		c[5] = -1.092548f * d.Y * d.Z * i;
		c[6] = 0.315392f * (3 * d.Z * d.Z - 1) * i;
		c[7] = -1.092548f * d.X * d.Z * i;
		c[8] = 0.546274f * (d.X * d.X - d.Y * d.Y) * i;

		var n = Normal;
		var bounce = Vector3.Max( Irradiance( c, n ), Vector3.Zero ) * Albedo * GroundBounce * DirectionalScale;
		var basis = Basis( -n );
		for ( var k = 0; k < 9; k++ )
			c[k] += bounce * basis[k];
		return new LightProbe( d, i, c );
	}

	// Irradiance over pi in direction n from L2 coefficients in Halo's convention (Halo 3's SH evaluation: bands weighted 1, 2/3, 1/4).
	static Vector3 Irradiance( Vector3[] c, Vector3 n )
	{
		float x = n.X, y = n.Y, z = n.Z;
		return c[0] * 0.282095f
			+ (c[2] * z - c[1] * y - c[3] * x) * 0.325735f
			+ (c[4] * (x * y) - c[5] * (y * z) - c[7] * (x * z)) * 0.273137f
			+ c[6] * ((3 * z * z - 1) * 0.078848f)
			+ c[8] * ((x * x - y * y) * 0.136568f);
	}

	// The L2 basis in direction d, Halo's convention.
	static float[] Basis( Vector3 d ) =>
	[
		0.282095f, -0.488603f * d.Y, 0.488603f * d.Z, -0.488603f * d.X,
		1.092548f * d.X * d.Y, -1.092548f * d.Y * d.Z, 0.315392f * (3 * d.Z * d.Z - 1), -1.092548f * d.X * d.Z, 0.546274f * (d.X * d.X - d.Y * d.Y),
	];
}

// Samples a scenario's per-pixel lightmaps on the CPU, for lighting objects that have no baked probe the way MCC lights them: from the lightmap under the object (lighting.md §4.3, "lightmap_pointsample").
// Every scene BSP's cluster meshes and per-pixel lit instances go in, binned by their XY footprint, so a ray straight down finds its surface quickly.
public sealed class LightmapSampler
{
	static readonly System.Runtime.CompilerServices.ConditionalWeakTable<CacheFile, LightmapSampler> Cache = new();

	// A map's sampler (built once, kept with the cache file); null for a map without lightmaps.
	public static LightmapSampler For( CacheFile cache )
	{
		if ( cache is null || cache.Scenario is null )
			return null;
		var sampler = Cache.GetValue( cache, c => new LightmapSampler( c ) );
		return sampler._bsps.Count == 0 ? null : sampler;
	}

	sealed class Atlas
	{
		public int Width, Height;
		public byte[][] Lightprobe = new byte[8][];
		public byte[][] Intensity = new byte[2][];
		public float[] Scales;
	}

	sealed class Bsp
	{
		public int Index;
		public Atlas Atlas;
		public List<Vector3> Vertices = new();
		public List<Vector2> TexCoords = new();
		public List<Vector2> SurfaceUv = new();
		public List<int> Indices = new();
		public List<int> Materials = new();
		public IReadOnlyList<TagEntry> Shaders;
		public Dictionary<(int, int), List<int>> Cells = new();
		public Vector3 Min = new( float.MaxValue ), Max = new( float.MinValue );
	}

	// Cell size of the XY bins, world units.
	const float CellSize = 2;

	readonly List<Bsp> _bsps = new();

	public int BspCount => _bsps.Count;
	public int TriangleCount => _bsps.Sum( b => b.Indices.Count / 3 );

	LightmapSampler( CacheFile cache )
	{
		foreach ( var reference in new ScenarioStructureBsps( cache ).SceneBsps )
		{
			if ( reference.Bsp is null || reference.Lightmap is null )
				continue;
			try
			{
				if ( Build( cache, reference ) is { } bsp )
					_bsps.Add( bsp );
			}
			catch ( Exception )
			{
				// A BSP whose geometry or lightmap can't be read isn't sampled; objects on it keep their fallback light.
			}
		}
	}

	static Bsp Build( CacheFile cache, ScenarioStructureBsp reference )
	{
		var lightmapBsp = new LightmapBspTag( reference.Lightmap );
		var sh = new LightmapBspSh( reference.Lightmap );
		if ( ReadAtlas( sh ) is not { } atlas )
			return null;

		var geometry = lightmapBsp.Geometry();
		if ( !geometry.Load() )
			return null;

		var structure = new StructureBspTag( reference.Bsp );
		var bsp = new Bsp { Index = reference.Index, Atlas = atlas, Shaders = structure.Materials };

		foreach ( var cluster in structure.Clusters )
		{
			if ( cluster.Mesh < 0 || cluster.Mesh >= geometry.Meshes.Count )
				continue;
			var mesh = geometry.Meshes[cluster.Mesh];
			if ( geometry.Buffer( mesh.VertexBuffers[GeometryMesh.SlotLightmapTexCoord] ) is not { Format: 4 } uv )
				continue;
			Add( bsp, geometry, mesh, null, null, RenderGeometry.DecodeLightmapTexCoords( uv ) );
		}

		if ( structure.LoadResources() is { } resources )
		{
			foreach ( var instance in structure.Instances )
			{
				if ( instance.NotForRender || instance.Definition < 0 || instance.Definition >= resources.Definitions.Count || instance.Index >= lightmapBsp.Instances.Count )
					continue;
				var lit = lightmapBsp.Instances[instance.Index];
				if ( lit.LightprobeTexture < 0 || instance.LightmapTexCoords < 0 || instance.LightmapTexCoords >= geometry.PerInstanceLightmapTexCoords.Count )
					continue;
				if ( geometry.Buffer( geometry.PerInstanceLightmapTexCoords[instance.LightmapTexCoords] ) is not { Format: 4 } uv )
					continue;
				var definition = resources.Definitions[instance.Definition];
				if ( definition.Mesh < 0 || definition.Mesh >= geometry.Meshes.Count )
					continue;
				var compression = definition.Compression >= 0 && definition.Compression < geometry.Compression.Count ? geometry.Compression[definition.Compression] : GeometryCompression.Identity;
				Add( bsp, geometry, geometry.Meshes[definition.Mesh], compression, instance, RenderGeometry.DecodeLightmapTexCoords( uv ) );
			}
		}

		return bsp.Indices.Count == 0 ? null : bsp;
	}

	static void Add( Bsp bsp, RenderGeometry geometry, GeometryMesh mesh, GeometryCompression? compression, StructureInstance instance, Vector2[] texCoords )
	{
		if ( geometry.Vertices( mesh, compression ) is not { Count: > 0 } source || texCoords.Length < source.Count )
			return;

		var first = bsp.Vertices.Count;
		for ( var i = 0; i < source.Count; i++ )
		{
			var p = instance is null ? source.Positions[i] : instance.Transform( source.Positions[i] );
			bsp.Vertices.Add( p );
			bsp.TexCoords.Add( texCoords[i] );
			bsp.SurfaceUv.Add( source.TexCoords is { } uvs && i < uvs.Length ? uvs[i] : Vector2.Zero );
			bsp.Min = Vector3.Min( bsp.Min, p );
			bsp.Max = Vector3.Max( bsp.Max, p );
		}

		foreach ( var part in mesh.Parts.Where( p => p.IsDrawn && !p.IsTransparent ) )
		{
			foreach ( var (a, b, c) in geometry.Triangles( mesh, part ) )
			{
				if ( a >= source.Count || b >= source.Count || c >= source.Count )
					continue;
				var t = bsp.Indices.Count / 3;
				bsp.Indices.Add( first + a );
				bsp.Indices.Add( first + b );
				bsp.Indices.Add( first + c );
				bsp.Materials.Add( part.Material );
				Bin( bsp, t, bsp.Vertices[first + a], bsp.Vertices[first + b], bsp.Vertices[first + c] );
			}
		}
	}

	static void Bin( Bsp bsp, int triangle, Vector3 a, Vector3 b, Vector3 c )
	{
		int x0 = Cell( Math.Min( a.X, Math.Min( b.X, c.X ) ) ), x1 = Cell( Math.Max( a.X, Math.Max( b.X, c.X ) ) );
		int y0 = Cell( Math.Min( a.Y, Math.Min( b.Y, c.Y ) ) ), y1 = Cell( Math.Max( a.Y, Math.Max( b.Y, c.Y ) ) );
		// A huge triangle (a sky plane) would land in thousands of cells; it's left out, as nothing stands on one.
		if ( (x1 - x0 + 1) * (y1 - y0 + 1) > 4096 )
			return;
		for ( var y = y0; y <= y1; y++ )
		for ( var x = x0; x <= x1; x++ )
		{
			if ( !bsp.Cells.TryGetValue( (x, y), out var list ) )
				bsp.Cells[(x, y)] = list = new List<int>();
			list.Add( triangle );
		}
	}

	static int Cell( float v ) => (int)MathF.Floor( v / CellSize );

	// What Halo 3 samples for an object without a baked probe: one ray straight down from the top of its bounding sphere (centre, radius at least 0.05), 10 x max(radius, 0.4) long.
	public LightmapSample? SampleObject( Vector3 center, float radius )
	{
		radius = MathF.Max( radius, 0.05f );
		return Sample( center + new Vector3( 0, 0, radius ), 10 * MathF.Max( radius, 0.4f ) );
	}

	// The lightmap of the nearest surface straight below a point (within maxDrop world units; a surface up to 0.05 above it counts, for an origin resting on the ground), or null.
	public LightmapSample? Sample( Vector3 position, float maxDrop = 20 )
	{
		LightmapSample? best = null;
		var bestDrop = float.MaxValue;
		Bsp bestBsp = null;
		var bestTriangle = -1;
		var bestUv = Vector3.Zero;
		var cell = (Cell( position.X ), Cell( position.Y ));
		foreach ( var bsp in _bsps )
		{
			if ( position.X < bsp.Min.X || position.X > bsp.Max.X || position.Y < bsp.Min.Y || position.Y > bsp.Max.Y )
				continue;
			if ( !bsp.Cells.TryGetValue( cell, out var triangles ) )
				continue;
			foreach ( var t in triangles )
			{
				int ia = bsp.Indices[t * 3], ib = bsp.Indices[t * 3 + 1], ic = bsp.Indices[t * 3 + 2];
				Vector3 a = bsp.Vertices[ia], b = bsp.Vertices[ib], c = bsp.Vertices[ic];
				if ( !Barycentric( position, a, b, c, out var u, out var v, out var w ) )
					continue;
				var z = u * a.Z + v * b.Z + w * c.Z;
				var drop = position.Z - z;
				if ( drop < -0.05f || drop > maxDrop || drop >= bestDrop )
					continue;
				var uv = u * bsp.TexCoords[ia] + v * bsp.TexCoords[ib] + w * bsp.TexCoords[ic];
				if ( Decode( bsp.Atlas, uv ) is not { } sample )
					continue;
				bestDrop = drop;
				best = sample with { Position = new Vector3( position.X, position.Y, z ), Bsp = bsp.Index };
				bestBsp = bsp;
				bestTriangle = t;
				bestUv = new Vector3( u, v, w );
			}
		}
		if ( best is not { } found || bestBsp is null )
			return best;

		// The surface's normal and colour, for the light it bounces up.
		int i0 = bestBsp.Indices[bestTriangle * 3], i1 = bestBsp.Indices[bestTriangle * 3 + 1], i2 = bestBsp.Indices[bestTriangle * 3 + 2];
		var normal = Vector3.Cross( bestBsp.Vertices[i1] - bestBsp.Vertices[i0], bestBsp.Vertices[i2] - bestBsp.Vertices[i0] );
		normal = normal.LengthSquared() > 1e-12f ? Vector3.Normalize( normal ) : Vector3.UnitZ;
		if ( normal.Z < 0 )
			normal = -normal;
		var surfaceUv = bestUv.X * bestBsp.SurfaceUv[i0] + bestUv.Y * bestBsp.SurfaceUv[i1] + bestUv.Z * bestBsp.SurfaceUv[i2];
		var material = bestBsp.Materials[bestTriangle];
		var shader = material >= 0 && material < bestBsp.Shaders.Count ? bestBsp.Shaders[material] : null;
		return found with { Normal = normal, Albedo = SurfaceColor( shader, surfaceUv ) ?? new Vector3( 0.5f ) };
	}

	readonly Dictionary<(TagEntry, string), DecalAlbedo.Image> _images = new();

	DecalAlbedo.Image ImageOf( ShaderTag tag, string name )
	{
		lock ( _images )
		{
			if ( !_images.TryGetValue( (tag.Tag, name), out var image ) )
				_images[(tag.Tag, name)] = image = DecalAlbedo.Image.Load( tag.Texture( name ) );
			return image;
		}
	}

	// The surface colour Halo 3 bounces light with: its base map's texel under the sample (a terrain's materials' base maps by its blend map's weights), raw.
	Vector3? SurfaceColor( TagEntry shader, Vector2 uv )
	{
		if ( shader is null || ShaderTag.Get( shader ) is not { Definition: not null } tag )
			return null;
		if ( tag.Kind == "terrain" )
		{
			if ( ImageOf( tag, "blend_map" ) is not { } blend )
				return null;
			var w = blend.Sample( uv );
			var sum = Vector3.Zero;
			var total = 0f;
			for ( var m = 0; m < 4; m++ )
			{
				var weight = m switch { 0 => w.X, 1 => w.Y, 2 => w.Z, _ => w.W };
				if ( weight <= 0 || tag.Option( $"material_{m}" ) is null or "off" || ImageOf( tag, $"base_map_m_{m}" ) is not { } map )
					continue;
				var t = map.Sample( uv );
				sum += new Vector3( t.X, t.Y, t.Z ) * weight;
				total += weight;
			}
			return total > 1e-4f ? sum / total : null;
		}
		if ( ImageOf( tag, "base_map" ) is not { } image )
			return null;
		var texel = image.Sample( uv );
		return new Vector3( texel.X, texel.Y, texel.Z );
	}

	// Barycentric weights of a point's XY in a triangle's XY, true when inside.
	static bool Barycentric( Vector3 p, Vector3 a, Vector3 b, Vector3 c, out float u, out float v, out float w )
	{
		var v0 = new Vector2( b.X - a.X, b.Y - a.Y );
		var v1 = new Vector2( c.X - a.X, c.Y - a.Y );
		var v2 = new Vector2( p.X - a.X, p.Y - a.Y );
		var denominator = v0.X * v1.Y - v1.X * v0.Y;
		u = v = w = 0;
		if ( MathF.Abs( denominator ) < 1e-12f )
			return false;
		v = (v2.X * v1.Y - v1.X * v2.Y) / denominator;
		w = (v0.X * v2.Y - v2.X * v0.Y) / denominator;
		u = 1 - v - w;
		const float e = -1e-4f;
		return u >= e && v >= e && w >= e;
	}

	// The atlas texel pairs at a lightmap texcoord, bilinear over the 4 nearest texels (texels with alpha 0 are unused and left out).
	static LightmapSample? Decode( Atlas atlas, Vector2 uv )
	{
		var x = uv.X * atlas.Width - 0.5f;
		var y = uv.Y * atlas.Height - 0.5f;
		var x0 = (int)MathF.Floor( x );
		var y0 = (int)MathF.Floor( y );
		var fx = x - x0;
		var fy = y - y0;
		Span<float> weights = [ (1 - fx) * (1 - fy), fx * (1 - fy), (1 - fx) * fy, fx * fy ];
		Span<Vector3> sum = stackalloc Vector3[5];
		Span<Vector3> texel = stackalloc Vector3[5];
		var total = 0f;
		for ( var k = 0; k < 4; k++ )
		{
			var tx = Math.Clamp( x0 + (k & 1), 0, atlas.Width - 1 );
			var ty = Math.Clamp( y0 + (k >> 1), 0, atlas.Height - 1 );
			if ( weights[k] <= 0 )
				continue;
			var ok = true;
			for ( var pair = 0; pair < 5 && ok; pair++ )
			{
				var layers = pair < 4 ? atlas.Lightprobe : atlas.Intensity;
				var l = pair < 4 ? pair * 2 : 0;
				var a = Dxt5Texel( layers[l], atlas.Width, tx, ty );
				var b = Dxt5Texel( layers[l + 1], atlas.Width, tx, ty );
				if ( a.W == 0 && b.W == 0 )
					ok = false;
				var rgb = new Vector3( a.X + b.X, a.Y + b.Y, a.Z + b.Z ) * 2 - new Vector3( 2 );
				texel[pair] = rgb * (a.W * b.W * atlas.Scales[pair]);
			}
			if ( !ok )
				continue;
			for ( var pair = 0; pair < 5; pair++ )
				sum[pair] += texel[pair] * weights[k];
			total += weights[k];
		}
		if ( total <= 0 )
			return null;
		return new LightmapSample( sum[0] / total, sum[1] / total, sum[2] / total, sum[3] / total, Vector3.Max( sum[4] / total, Vector3.Zero ), Vector3.Zero, -1 );
	}

	// One texel of a DXT5 (BC3) layer as unorm rgba.
	static Vector4 Dxt5Texel( byte[] layer, int width, int x, int y )
	{
		var blocksX = Math.Max( 1, width / 4 );
		var offset = ((y >> 2) * blocksX + (x >> 2)) * 16;
		if ( layer is null || offset + 16 > layer.Length )
			return Vector4.Zero;
		var block = layer.AsSpan( offset, 16 );
		var texel = (y & 3) * 4 + (x & 3);

		int a0 = block[0], a1 = block[1];
		var bits = (ulong)block[2] | (ulong)block[3] << 8 | (ulong)block[4] << 16 | (ulong)block[5] << 24 | (ulong)block[6] << 32 | (ulong)block[7] << 40;
		var ai = (int)(bits >> (3 * texel)) & 7;
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
		var ci = (block[12 + (texel >> 2)] >> (2 * (texel & 3))) & 3;
		var rgb = ci switch
		{
			0 => c0,
			1 => c1,
			2 => (2 * c0 + c1) / 3,
			_ => (c0 + 2 * c1) / 3,
		};
		return new Vector4( rgb, alpha / 255f );
	}

	static Vector3 Rgb565( int v ) => new( ((v >> 11) & 31) / 31f, ((v >> 5) & 63) / 63f, (v & 31) / 31f );

	// The two arrays' level 0 layers (8 lightprobe, 2 intensity) and the five decode scales; null when either is missing or isn't DXT5.
	static Atlas ReadAtlas( LightmapBspSh sh )
	{
		if ( sh.LightprobeArray is null || sh.IntensityArray is null )
			return null;
		var lp = BitmapPixels.Read( new BitmapTag( sh.LightprobeArray ).Images[0] );
		var intensity = BitmapPixels.Read( new BitmapTag( sh.IntensityArray ).Images[0] );
		if ( lp.Format is not (DxgiFormat.BC3Unorm or DxgiFormat.BC3UnormSrgb) || intensity.Format is not (DxgiFormat.BC3Unorm or DxgiFormat.BC3UnormSrgb) )
			return null;
		if ( lp.Layers < 8 || intensity.Layers < 2 || lp.Width != intensity.Width || lp.Height != intensity.Height || lp.Width < 4 )
			return null;
		var atlas = new Atlas { Width = lp.Width, Height = lp.Height, Scales = sh.Scales };
		for ( var k = 0; k < 8; k++ )
			atlas.Lightprobe[k] = lp.Levels[k][0].ToArray();
		for ( var k = 0; k < 2; k++ )
			atlas.Intensity[k] = intensity.Levels[k][0].ToArray();
		return atlas;
	}
}
