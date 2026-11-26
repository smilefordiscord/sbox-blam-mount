using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Blam.Halo2.Tags;

namespace Blam.Halo2;

// Inside the namespace so these win over s&box's global vector types when this compiles into the mount.
using Vector3 = System.Numerics.Vector3;

/// <summary>
/// One level of detail of a render model's PRT data: where its clusters start in the cluster basis (in floats),
/// and for each section it covers, the section's first vertex in the PRT vertex buffer.
/// </summary>
public sealed record PrtLod( int ClusterOffset, IReadOnlyList<(int Section, int FirstVertex)> Sections );

/// <summary>
/// A render model's precomputed radiance transfer (<c>mode</c> +0x74, "PRT info"), what Halo 2's <c>prt_*</c>
/// shader templates light with instead of the object lighting: per vertex, how much of the light arriving from
/// each direction it reflects (shadowed by the model itself), as spherical harmonic coefficients, compressed with
/// clustered PCA.
/// </summary>
/// <remarks>
/// <para>Each vertex has a cluster and four PCA weights in a vertex buffer of the PRT info's own raw block (type
/// 0x38, 12 bytes: f32 cluster index, four s16 weights out of 32767). A cluster has a mean transfer vector and four
/// basis vectors, each order² coefficients per colour channel; the vertex's transfer vector is the mean plus the
/// weighted basis vectors. MCC does it per draw on the GPU, with the light already dotted into each cluster's
/// vectors on the CPU; dotting the light into the reconstructed vector is the same sum.</para>
///
/// <para>Every shipped model uses order 3 (9 coefficients), 10 clusters, 4 PCA vectors, and the same transfer for
/// red, green and blue; <see cref="Transfer"/> averages the three channels (identical in every MCC map) so one
/// vector of 9 serves all three.</para>
/// </remarks>
public sealed class RenderModelPrt
{
	public const int CoefficientCount = 9;

	const int VertexBufferType = 0x38;
	const int VertexStride = 12;
	const int ResourceSize = 0x10;
	const int LodSize = 0xC;
	const int LodSectionSize = 8;

	// The LOD info's section offsets count five values (cluster and four weights) per vertex.
	const int ValuesPerVertex = 5;

	readonly float[] _basis;
	readonly byte[] _vertices;

	public int ShOrder { get; }
	public int ClusterCount { get; }
	public int PcaVectorCount { get; }
	public IReadOnlyList<PrtLod> Lods { get; }

	/// <summary>
	/// The number of vertices in the PRT vertex buffer, all sections and LODs together.
	/// </summary>
	public int VertexCount => _vertices.Length / VertexStride;

	RenderModelPrt( int order, int clusters, int pca, IReadOnlyList<PrtLod> lods, float[] basis, byte[] vertices )
	{
		ShOrder = order;
		ClusterCount = clusters;
		PcaVectorCount = pca;
		Lods = lods;
		_basis = basis;
		_vertices = vertices;
	}

	/// <summary>
	/// Reads the PRT info element at <paramref name="address"/>, or returns null if it has no vertex data or isn't
	/// the order 3 data every MCC map has.
	/// </summary>
	/// <remarks>
	/// The element (0x58): u16 SH order, u16 cluster count, u16 PCA vectors per cluster, ray and bounce counts, f32
	/// length scale, u16 LOD count, LOD info block (0xC: u32 cluster offset, sections block of (s32 section, u32
	/// offset)), cluster basis block (f32), then a raw block like a geometry section's: pointer +0x34, size +0x38,
	/// struct size +0x3C, resource data size +0x40, resources +0x44. Its struct holds one vertex buffer descriptor.
	/// </remarks>
	public static RenderModelPrt Read( CacheFile cache, uint address )
	{
		var data = cache.TagData;
		var order = data.U16( address );
		var clusters = data.U16( address + 2 );
		var pca = data.U16( address + 4 );
		if ( order != 3 || clusters == 0 )
			return null;

		var lods = data.Elements( address + 0x14, LodSize ).Select( l => new PrtLod(
			(int)data.U32( l ),
			data.Elements( l + 4, LodSectionSize ).Select( s => (data.I32( s ), (int)(data.U32( s + 4 ) / ValuesPerVertex)) ).ToList() ) ).ToList();

		var basis = data.Elements( address + 0x1C, 4 ).Select( data.F32 ).ToArray();

		var rawPointer = data.U32( address + 0x34 );
		var rawSize = (int)data.U32( address + 0x38 );
		var structSize = (int)data.U32( address + 0x3C );
		if ( rawPointer == 0xFFFFFFFF || rawSize == 0 )
			return null;

		var block = cache.ReadRaw( rawPointer, rawSize );
		var resourceBase = 8 + structSize;
		if ( BinaryPrimitives.ReadUInt32LittleEndian( block ) != 0x626C6B68 || BinaryPrimitives.ReadUInt32LittleEndian( block.AsSpan( resourceBase ) ) != 0x72737263 )
			throw new InvalidDataException( $"PRT block at 0x{rawPointer:X8} in '{cache.Name}' isn't a blkh/rsrc block" );

		// One vertex buffer, described at the start of the struct (type, stride).
		if ( structSize < 2 || block[8] != VertexBufferType || block[9] != VertexStride )
			throw new InvalidDataException( $"PRT block at 0x{rawPointer:X8} in '{cache.Name}' has vertex buffer type 0x{block[8]:X2} stride {block[9]}" );

		byte[] vertices = null;
		foreach ( var e in data.Elements( address + 0x44, ResourceSize ) )
		{
			if ( data.U8( e ) != 2 )
				continue;

			var size = (int)data.U32( e + 8 );
			var offset = resourceBase + (int)data.U32( e + 12 );
			vertices = block.AsSpan( offset, size - size % VertexStride ).ToArray();
		}

		if ( vertices is null )
			return null;

		var floatsPerCluster = (pca + 1) * 3 * CoefficientCount;
		foreach ( var lod in lods )
		{
			if ( lod.ClusterOffset < 0 || lod.ClusterOffset + clusters * floatsPerCluster > basis.Length )
				throw new InvalidDataException( $"PRT LOD of '{cache.Name}' needs {clusters} clusters at {lod.ClusterOffset}, the basis has {basis.Length} floats" );
		}

		return new RenderModelPrt( order, clusters, pca, lods, basis, vertices );
	}

	/// <summary>
	/// The PRT LOD MCC uses for a section drawn at level of detail <paramref name="level"/> (0 super low ... 5
	/// hollywood): <c>min( 5 - level, LOD count - 1 )</c>.
	/// </summary>
	public PrtLod LodFor( int level ) => Lods.Count == 0 ? null : Lods[Math.Clamp( 5 - level, 0, Lods.Count - 1 )];

	/// <summary>
	/// The transfer vector of every vertex of <paramref name="section"/> (<see cref="CoefficientCount"/> each, one
	/// after the other) when drawn at level of detail <paramref name="level"/>, or null if the PRT data doesn't
	/// cover it.
	/// </summary>
	public float[] Transfer( int section, int level, int vertexCount )
	{
		var lod = LodFor( level );
		var entry = lod?.Sections.FirstOrDefault( s => s.Section == section ) ?? (-1, 0);
		if ( entry.Section != section || entry.FirstVertex < 0 || entry.FirstVertex + vertexCount > VertexCount )
			return null;

		const int n = CoefficientCount;
		var floatsPerCluster = (PcaVectorCount + 1) * 3 * n;
		var weights = new float[PcaVectorCount];
		var result = new float[vertexCount * n];
		for ( var v = 0; v < vertexCount; v++ )
		{
			var record = _vertices.AsSpan( (entry.FirstVertex + v) * VertexStride, VertexStride );
			var cluster = Math.Clamp( (int)BinaryPrimitives.ReadSingleLittleEndian( record ), 0, ClusterCount - 1 );
			for ( var j = 0; j < Math.Min( PcaVectorCount, 4 ); j++ )
				weights[j] = Math.Max( BinaryPrimitives.ReadInt16LittleEndian( record[(4 + j * 2)..] ) / 32767f, -1 );

			// Mean, then basis vector j, each red, green, blue blocks of n.
			var start = lod.ClusterOffset + cluster * floatsPerCluster;
			for ( var c = 0; c < n; c++ )
			{
				var sum = 0f;
				for ( var channel = 0; channel < 3; channel++ )
				{
					var t = _basis[start + channel * n + c];
					for ( var j = 0; j < PcaVectorCount; j++ )
						t += weights[j] * _basis[start + (j + 1) * 3 * n + channel * n + c];
					sum += t;
				}

				result[v * n + c] = sum / 3;
			}
		}

		return result;
	}
}

/// <summary>
/// Real spherical harmonics up to order 3 (9 coefficients) in Direct3D's (D3DX) convention, which Halo 2's PRT
/// data and lightmap lighting environments use: index <c>l² + l + m</c>, <c>Y1,-1 = -c y</c>, <c>Y1,0 = c z</c>,
/// <c>Y1,1 = -c x</c> and so on (checked against the models' normals).
/// </summary>
public static class SphericalHarmonics
{
	const float Y0 = 0.28209479177387814f;
	const float K1 = 0.48860251190291992f;
	const float K2 = 1.09254843059207907f;
	const float K3 = 0.31539156525252001f;
	const float K4 = 0.54627421529603953f;

	/// <summary>
	/// The 9 basis functions at direction <paramref name="d"/> (a unit vector).
	/// </summary>
	public static float[] Basis( Vector3 d ) =>
	[
		Y0,
		-K1 * d.Y, K1 * d.Z, -K1 * d.X,
		K2 * d.X * d.Y, -K2 * d.Y * d.Z, K3 * (3 * d.Z * d.Z - 1), -K2 * d.X * d.Z, K4 * (d.X * d.X - d.Y * d.Y),
	];

	/// <summary>
	/// The transfer vector of an unshadowed surface facing <paramref name="normal"/> (clamped cosine, divided by
	/// π like the PRT data: a white light of 1 everywhere gives 1), for vertices the PRT data doesn't cover.
	/// </summary>
	public static float[] CosineTransfer( Vector3 normal )
	{
		var y = Basis( normal.LengthSquared() > 1e-12f ? Vector3.Normalize( normal ) : Vector3.UnitZ );
		for ( var i = 1; i < 4; i++ )
			y[i] *= 2f / 3;
		for ( var i = 4; i < 9; i++ )
			y[i] *= 0.25f;
		return y;
	}

	/// <summary>
	/// A directional light from <paramref name="towards"/> of intensity <paramref name="intensity"/>, normalized
	/// like D3DX's <c>D3DXSHEvalDirectionalLight</c>: a white surface facing it
	/// reflects the intensity.
	/// </summary>
	public static float[] DirectionalLight( Vector3 towards, float intensity )
	{
		var y = Basis( Vector3.Normalize( towards ) );
		var scale = MathF.PI / 1.0625f * intensity;
		for ( var i = 0; i < y.Length; i++ )
			y[i] *= scale;
		return y;
	}

	/// <summary>
	/// Turns a function into an object's own frame: <c>local(w) = world(R w)</c>, R the rotation whose columns are
	/// the object's <paramref name="forward"/>, <paramref name="left"/> and <paramref name="up"/> in the world
	/// (unit vectors). Bands 1 and 2 are a vector and a symmetric matrix on the sphere, so they turn exactly.
	/// </summary>
	public static float[] ToLocal( IReadOnlyList<float> c, Vector3 forward, Vector3 left, Vector3 up )
	{
		Vector3[] axes = [forward, left, up];

		// Band 1: v.w.
		var v = K1 * new Vector3( -c[3], -c[1], c[2] );

		// Band 2: w^T Q w, with 3z² - 1 = 2z² - x² - y² on the sphere (Q traceless).
		var q = new float[3, 3];
		q[0, 0] = c[8] * K4 - c[6] * K3;
		q[1, 1] = -c[8] * K4 - c[6] * K3;
		q[2, 2] = 2 * c[6] * K3;
		q[0, 1] = q[1, 0] = c[4] * K2 / 2;
		q[1, 2] = q[2, 1] = -c[5] * K2 / 2;
		q[0, 2] = q[2, 0] = -c[7] * K2 / 2;

		// v' = R^T v, Q' = R^T Q R.
		var vl = new Vector3( Vector3.Dot( axes[0], v ), Vector3.Dot( axes[1], v ), Vector3.Dot( axes[2], v ) );
		var ql = new float[3, 3];
		for ( var i = 0; i < 3; i++ )
		{
			var qa = new Vector3(
				q[0, 0] * axes[i].X + q[0, 1] * axes[i].Y + q[0, 2] * axes[i].Z,
				q[1, 0] * axes[i].X + q[1, 1] * axes[i].Y + q[1, 2] * axes[i].Z,
				q[2, 0] * axes[i].X + q[2, 1] * axes[i].Y + q[2, 2] * axes[i].Z );
			for ( var j = 0; j < 3; j++ )
				ql[j, i] = Vector3.Dot( axes[j], qa );
		}

		return
		[
			c[0],
			-vl.Y / K1, vl.Z / K1, -vl.X / K1,
			2 * ql[0, 1] / K2, -2 * ql[1, 2] / K2, ql[2, 2] / (2 * K3), -2 * ql[0, 2] / K2, (ql[0, 0] - ql[1, 1]) / (2 * K4),
		];
	}
}

/// <summary>
/// The lighting environments of a scenario's BSPs, by position. MCC lights PRT objects from the loaded BSP's
/// lightmap; here an object takes the BSP whose render geometry's bounds contain it (the smallest, where they
/// overlap), or the first BSP.
/// </summary>
public sealed class PrtEnvironments
{
	readonly List<(Vector3 Min, Vector3 Max, IReadOnlyList<LightingEnvironment> Environments)> _bsps = new();

	public PrtEnvironments( IEnumerable<(StructureBspTag Bsp, LightmapTag Lightmap)> bsps )
	{
		foreach ( var (bsp, lightmap) in bsps )
		{
			var min = new Vector3( float.MaxValue );
			var max = new Vector3( float.MinValue );
			foreach ( var cluster in bsp.Clusters )
			{
				foreach ( var p in cluster.Positions )
				{
					min = Vector3.Min( min, p );
					max = Vector3.Max( max, p );
				}
			}

			_bsps.Add( (min, max, lightmap?.Environments ?? []) );
		}
	}

	public IReadOnlyList<LightingEnvironment> At( Vector3 position )
	{
		IReadOnlyList<LightingEnvironment> result = null;
		var smallest = float.MaxValue;
		foreach ( var (min, max, environments) in _bsps )
		{
			if ( position.X < min.X || position.Y < min.Y || position.Z < min.Z || position.X > max.X || position.Y > max.Y || position.Z > max.Z )
				continue;

			var size = max - min;
			var volume = size.X * size.Y * size.Z;
			if ( volume < smallest )
			{
				smallest = volume;
				result = environments;
			}
		}

		return result ?? (_bsps.Count > 0 ? _bsps[0].Environments : []);
	}
}

/// <summary>
/// The light a PRT object gets: spherical harmonic radiance per colour channel (9 coefficients each, D3DX
/// convention) and the dominant light direction (towards the light, world space; <c>c68</c> in MCC's PRT
/// shaders, for the bump map and the specular highlight).
/// </summary>
public sealed record PrtLight( float[] Red, float[] Green, float[] Blue, Vector3 Direction )
{
	/// <summary>
	/// The light MCC gives a PRT object whose node is at <paramref name="position"/> (world units), from the
	/// lighting environments of the BSP's lightmap: with one environment, it; with more, the nearest one, blended towards the nearest
	/// one on the other side of the position (along the line between the two, clamped); with none, MCC's fallback,
	/// a green light straight from below and no dominant direction.
	/// </summary>
	public static PrtLight Sample( IReadOnlyList<LightingEnvironment> environments, Vector3 position )
	{
		if ( environments is null || environments.Count == 0 )
			return new PrtLight( new float[9], SphericalHarmonics.DirectionalLight( -Vector3.UnitZ, 1 ), new float[9], Vector3.Zero );

		if ( environments.Count == 1 )
			return From( environments[0] );

		// The nearest environment (within 1024 world units, else the first).
		var nearest = 0;
		var best = 1048576f;
		for ( var i = 0; i < environments.Count; i++ )
		{
			var d = Vector3.DistanceSquared( position, environments[i].Point );
			if ( d < best )
			{
				best = d;
				nearest = i;
			}
		}

		// The nearest of those the position lies between it and the nearest.
		var en = environments[nearest].Point;
		var other = -1;
		best = 1048576f;
		for ( var i = 0; i < environments.Count; i++ )
		{
			if ( i == nearest )
				continue;

			var ej = environments[i].Point;
			var d = Vector3.DistanceSquared( position, ej );
			if ( Vector3.Dot( position - ej, position - en ) <= 0 && d < best )
			{
				best = d;
				other = i;
			}
		}

		if ( other < 0 )
			return From( environments[nearest] );

		var along = en - environments[other].Point;
		var length = along.Length();
		var t = length > 1e-4f ? Vector3.Dot( along, en - position ) / (length * length) : 0;
		if ( t <= 0 )
			return From( environments[nearest] );
		if ( t >= 1 )
			return From( environments[other] );

		var a = environments[nearest];
		var b = environments[other];
		float[] Lerp( float[] x, float[] y ) => x.Zip( y, ( p, q ) => p + (q - p) * t ).ToArray();
		return new PrtLight( Lerp( a.Red, b.Red ), Lerp( a.Green, b.Green ), Lerp( a.Blue, b.Blue ), Vector3.Lerp( a.Direction, b.Direction, t ) );
	}

	static PrtLight From( LightingEnvironment e ) => new( e.Red, e.Green, e.Blue, e.Direction );

	/// <summary>
	/// The light of a placed object with PRT data, turned into the object's frame. MCC samples it per drawn
	/// section at the section's node and keeps it per frame (a section of two nodes gets two); objects here are
	/// static in their default pose, so it's sampled once, at the root node.
	/// </summary>
	public static PrtLight ForPlacement( PrtEnvironments environments, RenderModelTag model, ObjectPlacement placement )
	{
		var node = model.Nodes.Count > 0 ? model.DefaultPose()[0].Translation : Vector3.Zero;
		var position = placement.Transform( node );
		return Sample( environments.At( position ), position ).ToLocal( placement.Forward, placement.Left, placement.Up );
	}

	/// <summary>
	/// The light in an object's own frame (see <see cref="SphericalHarmonics.ToLocal"/>), where its transfer
	/// vectors are; the direction stays in world space.
	/// </summary>
	public PrtLight ToLocal( Vector3 forward, Vector3 left, Vector3 up ) => new(
		SphericalHarmonics.ToLocal( Red, forward, left, up ),
		SphericalHarmonics.ToLocal( Green, forward, left, up ),
		SphericalHarmonics.ToLocal( Blue, forward, left, up ),
		Direction );

	/// <summary>
	/// The light a transfer vector (from <see cref="RenderModelPrt.Transfer"/>, in the frame of this light)
	/// reflects, per channel: MCC's PRT irradiance before the shader's scale.
	/// </summary>
	public Vector3 Irradiance( ReadOnlySpan<float> transfer )
	{
		var r = 0f;
		var g = 0f;
		var b = 0f;
		for ( var i = 0; i < RenderModelPrt.CoefficientCount; i++ )
		{
			r += transfer[i] * Red[i];
			g += transfer[i] * Green[i];
			b += transfer[i] * Blue[i];
		}

		return new Vector3( r, g, b );
	}
}
