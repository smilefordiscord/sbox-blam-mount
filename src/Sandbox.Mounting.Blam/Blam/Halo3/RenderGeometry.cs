using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

namespace Blam.Halo3;

// Inside the namespace so these win over s&box's global Vector types when this compiles into the mount.
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;

// A part of a mesh: a range of its index buffer drawn with one material.
public readonly record struct GeometryPart( int Material, int IndexStart, int IndexCount, int Type, int Flags )
{
	// Types: 0 not drawn, 1 opaque shadow-only, 2 opaque shadow-casting, 3 opaque non-shadowing, 4 transparent, 5 lightmap-only.
	public bool IsDrawn => Type is 2 or 3 or 4;
	public bool IsTransparent => Type == 4;
	public bool IsWater => (Flags & 8) != 0;
}

// A visibility subset of a part: decorator models draw one per decorator type.
public readonly record struct GeometrySubpart( int IndexStart, int IndexCount, int Part );

// Vertex types of a mesh (+0x2E).
public enum GeometryVertexType
{
	World = 0,
	Rigid = 1,
	Skinned = 2,
	ParticleModel = 3,
	Decorator = 15,
}

// A mesh of a render geometry (0x4C): its parts and which vertex/index buffers of the geometry's resource it draws from.
public sealed class GeometryMesh
{
	public const int SlotMain = 0, SlotLightmapTexCoord = 1, SlotVertexColor = 2, SlotPrt = 3, SlotWaterParameters = 7;

	public int Index { get; init; }
	public GeometryPart[] Parts { get; init; }
	public GeometrySubpart[] Subparts { get; init; }

	// Vertex buffer index per slot (-1 none): 0 main, 1 lightmap uv, 2 vertex colour, 3 PRT, 6/7 water.
	public short[] VertexBuffers { get; init; }
	public int IndexBuffer { get; init; }
	public int Flags { get; init; }
	public int RigidNode { get; init; }
	public GeometryVertexType VertexType { get; init; }
	public int PrtType { get; init; }

	// 3 triangle list, 5 triangle strip.
	public int IndexBufferType { get; init; }

	public bool HasPerInstanceLighting => (Flags & 8) != 0;
	public bool IsUnindexed => (Flags & 0x10) != 0;
	public int MainBuffer => VertexBuffers[SlotMain];
}

// Position and texcoord bounds that u16 vertex values are normalised into (0x2C).
public readonly record struct GeometryCompression( Vector3 Min, Vector3 Max, Vector2 UvMin, Vector2 UvMax )
{
	public static readonly GeometryCompression Identity = new( Vector3.Zero, Vector3.One, Vector2.Zero, Vector2.One );

	public Vector3 Position( Vector3 unit ) => Min + unit * (Max - Min);
	public Vector2 TexCoord( Vector2 unit ) => UvMin + unit * (UvMax - UvMin);
}

public sealed class VertexBuffer( int count, int format, int stride, ReadOnlyMemory<byte> data )
{
	public int Count { get; } = count;
	public int Format { get; } = format;
	public int Stride { get; } = stride;
	public ReadOnlyMemory<byte> Data { get; } = data;
}

public sealed class IndexBuffer( int type, ushort[] indices )
{
	public int Type { get; } = type;
	public ushort[] Indices { get; } = indices;
}

// A main vertex buffer decoded to model/world-format floats; arrays the format lacks are null.
public sealed class GeometryVertices
{
	public int Count { get; init; }
	public Vector3[] Positions { get; init; }
	public Vector2[] TexCoords { get; init; }
	public Vector3[] Normals { get; init; }
	public Vector3[] Tangents { get; init; }
	public Vector3[] Binormals { get; init; }

	// Skinned only: 4 node indices and 4 weights (out of 255) per vertex.
	public byte[] NodeIndices { get; init; }
	public byte[] NodeWeights { get; init; }
}

// Static lighting of one vertex lit per vertex: the dominant light intensity and the L1 SH coefficients (RGB each).
public readonly record struct VertexLighting( Vector3 Intensity, Vector3 C0, Vector3 C1, Vector3 C2, Vector3 C3 );

// The render geometry struct (0x84) of a mode / sbsp / Lbsp / pmdf tag and its render_geometry_api_resource_definition (geometry.md §1).
public sealed class RenderGeometry
{
	// Where the struct starts in each owner tag.
	public const int RenderModelOffset = 0x64, StructureBspOffset = 0x2F0, StructureBspDecoratorOffset = 0x1D8, LightmapBspOffset = 0x130, ParticleModelOffset = 0;

	public CacheFile Cache { get; }
	public IReadOnlyList<GeometryMesh> Meshes { get; }
	public IReadOnlyList<GeometryCompression> Compression { get; }

	// Per mesh: vertex node index -> model node, or empty for meshes whose indices are model nodes.
	public IReadOnlyList<byte[]> NodeMaps { get; }

	// Per lightmapped instance (Lbsp): the vertex buffer holding its lightmap texcoords.
	public IReadOnlyList<int> PerInstanceLightmapTexCoords { get; }
	public uint ResourceHandle { get; }

	public IReadOnlyList<VertexBuffer> VertexBuffers { get; private set; }
	public IReadOnlyList<IndexBuffer> IndexBuffers { get; private set; }
	public bool IsLoaded => VertexBuffers is not null;

	public RenderGeometry( TagView owner, int offset )
	{
		Cache = owner.Cache;
		var g = owner.At( offset );

		var meshes = new List<GeometryMesh>();
		foreach ( var m in g.Block( 0x04, 0x4C ) )
		{
			var parts = new List<GeometryPart>();
			foreach ( var p in m.Block( 0x00, 0x10 ) )
				parts.Add( new GeometryPart( p.I16( 0 ), p.U16( 4 ), p.U16( 6 ), p.I8( 0xC ), p.U8( 0xD ) ) );

			var subparts = new List<GeometrySubpart>();
			foreach ( var p in m.Block( 0x0C, 8 ) )
				subparts.Add( new GeometrySubpart( p.U16( 0 ), p.U16( 2 ), p.I16( 4 ) ) );

			var slots = new short[8];
			for ( var i = 0; i < 8; i++ )
				slots[i] = m.I16( 0x18 + 2 * i );

			meshes.Add( new GeometryMesh
			{
				Index = meshes.Count,
				Parts = parts.ToArray(),
				Subparts = subparts.ToArray(),
				VertexBuffers = slots,
				IndexBuffer = m.I16( 0x28 ),
				Flags = m.U8( 0x2C ),
				RigidNode = m.I8( 0x2D ),
				VertexType = (GeometryVertexType)m.U8( 0x2E ),
				PrtType = m.U8( 0x2F ),
				IndexBufferType = m.U8( 0x30 ),
			} );
		}
		Meshes = meshes;

		var compression = new List<GeometryCompression>();
		foreach ( var c in g.Block( 0x10, 0x2C ) )
		{
			compression.Add( new GeometryCompression(
				new Vector3( c.F32( 0x04 ), c.F32( 0x0C ), c.F32( 0x14 ) ),
				new Vector3( c.F32( 0x08 ), c.F32( 0x10 ), c.F32( 0x18 ) ),
				new Vector2( c.F32( 0x1C ), c.F32( 0x24 ) ),
				new Vector2( c.F32( 0x20 ), c.F32( 0x28 ) ) ) );
		}
		Compression = compression;

		var nodeMaps = new List<byte[]>();
		foreach ( var n in g.Block( 0x4C, 0xC ) )
		{
			var count = n.Count( 0 );
			nodeMaps.Add( count == 0 || n.U32( 4 ) == 0 ? Array.Empty<byte>() : Cache.File.Read( Cache.PointerOffset( n.U32( 4 ) ), count ) );
		}
		NodeMaps = nodeMaps;

		var lightmapTexCoords = new List<int>();
		foreach ( var t in g.Block( 0x70, 0x10 ) )
			lightmapTexCoords.Add( t.I16( 0xC ) );
		PerInstanceLightmapTexCoords = lightmapTexCoords;

		ResourceHandle = g.U32( 0x7C );
	}

	// Reads the vertex and index buffers from the resource; false if it's dead or stripped.
	public bool Load()
	{
		if ( IsLoaded )
			return true;

		var data = Cache.Resources.Read( ResourceHandle );
		if ( data is null || !data.HasPrimary )
			return false;

		// Root: xenon vertex buffers, xenon index buffers, PC vertex buffers, PC index buffers; each element a d3d pointer to a definition in the control data.
		var root = data.Root;
		var vertexBuffers = new List<VertexBuffer>();
		foreach ( var element in data.Block( root + 0x18, 12 ) )
		{
			if ( data.Pointer( element ) is not { Kind: ResourcePointerKind.Control } definition )
				throw new InvalidDataException( $"Vertex buffer {vertexBuffers.Count} of resource 0x{ResourceHandle:X8} has no definition" );

			var d = definition.Offset;
			var count = data.I32( d );
			var format = data.U16( d + 4 );
			var stride = data.U16( d + 6 );
			var size = data.I32( d + 8 );
			if ( size != count * stride )
				throw new InvalidDataException( $"Vertex buffer {vertexBuffers.Count}: {count} x {stride} != {size} bytes" );

			vertexBuffers.Add( new VertexBuffer( count, format, stride, size == 0 ? ReadOnlyMemory<byte>.Empty : Slice( data, d + 0x14, size ) ) );
		}

		var indexBuffers = new List<IndexBuffer>();
		foreach ( var element in data.Block( root + 0x24, 12 ) )
		{
			if ( data.Pointer( element ) is not { Kind: ResourcePointerKind.Control } definition )
				throw new InvalidDataException( $"Index buffer {indexBuffers.Count} of resource 0x{ResourceHandle:X8} has no definition" );

			var d = definition.Offset;
			var type = data.I32( d );
			var size = data.I32( d + 4 );
			var indices = new ushort[size / 2];
			if ( size > 0 )
			{
				var bytes = Slice( data, d + 0x10, size ).Span;
				for ( var i = 0; i < indices.Length; i++ )
					indices[i] = BinaryPrimitives.ReadUInt16LittleEndian( bytes[(2 * i)..] );
			}
			indexBuffers.Add( new IndexBuffer( type, indices ) );
		}

		VertexBuffers = vertexBuffers;
		IndexBuffers = indexBuffers;
		return true;
	}

	static ReadOnlyMemory<byte> Slice( ResourceData data, int field, int size )
	{
		if ( data.Pointer( field ) is not { } fixup || fixup.Kind == ResourcePointerKind.Control )
			throw new InvalidDataException( $"Buffer data at control offset 0x{field:X} has no page pointer" );

		var target = data.Target( field );
		if ( target.Length < size )
			throw new InvalidDataException( $"Buffer of {size} bytes at 0x{fixup.Offset:X} runs past its {fixup.Kind} data" );
		return target[..size];
	}

	public VertexBuffer Buffer( int index ) => IsLoaded && index >= 0 && index < VertexBuffers.Count ? VertexBuffers[index] : null;

	// A mesh's main vertices; compression is the model's (element 0) or the instanced geometry definition's, null for world (float) vertices.
	public GeometryVertices Vertices( GeometryMesh mesh, GeometryCompression? compression ) =>
		Buffer( mesh.MainBuffer ) is { } buffer ? DecodeVertices( buffer, compression ?? GeometryCompression.Identity ) : null;

	public static GeometryVertices DecodeVertices( VertexBuffer buffer, GeometryCompression compression )
	{
		var n = buffer.Count;
		var s = buffer.Data.Span;
		var stride = buffer.Stride;
		var positions = new Vector3[n];
		var texCoords = new Vector2[n];
		var normals = new Vector3[n];
		Vector3[] tangents = null, binormals = null;
		byte[] nodeIndices = null, nodeWeights = null;

		switch ( buffer.Format )
		{
			// World: f32 x3 position, f32 x2 uv, i16n x4 normal, tangent, binormal (44).
			case 1:
				Expect( buffer, 44 );
				tangents = new Vector3[n];
				binormals = new Vector3[n];
				for ( var i = 0; i < n; i++ )
				{
					var v = s[(i * stride)..];
					positions[i] = new Vector3( F32( v, 0 ), F32( v, 4 ), F32( v, 8 ) );
					texCoords[i] = new Vector2( F32( v, 12 ), F32( v, 16 ) );
					normals[i] = Short3N( v, 20 );
					tangents[i] = Short3N( v, 28 );
					binormals[i] = Short3N( v, 36 );
				}
				break;

			// Rigid (36) and skinned (44, + u8 x4 nodes, u8 x4 weights): u16n x4 position, u16n x2 uv, i16n x4 normal, tangent, binormal.
			case 2:
			case 3:
				Expect( buffer, buffer.Format == 2 ? 36 : 44 );
				tangents = new Vector3[n];
				binormals = new Vector3[n];
				if ( buffer.Format == 3 )
				{
					nodeIndices = new byte[n * 4];
					nodeWeights = new byte[n * 4];
				}
				for ( var i = 0; i < n; i++ )
				{
					var v = s[(i * stride)..];
					positions[i] = compression.Position( Unorm3( v, 0 ) );
					texCoords[i] = compression.TexCoord( Unorm2( v, 8 ) );
					normals[i] = Short3N( v, 12 );
					tangents[i] = Short3N( v, 20 );
					binormals[i] = Short3N( v, 28 );
					if ( nodeIndices is not null )
					{
						v.Slice( 36, 4 ).CopyTo( nodeIndices.AsSpan( i * 4 ) );
						v.Slice( 40, 4 ).CopyTo( nodeWeights.AsSpan( i * 4 ) );
					}
				}
				break;

			// Decorator model: u16n x4 position, u16n x2 uv, i16n x4 normal (20).
			case 24:
				Expect( buffer, 20 );
				for ( var i = 0; i < n; i++ )
				{
					var v = s[(i * stride)..];
					positions[i] = compression.Position( Unorm3( v, 0 ) );
					texCoords[i] = compression.TexCoord( Unorm2( v, 8 ) );
					normals[i] = Short3N( v, 12 );
				}
				break;

			// Particle model: f32 x3 position and f32 x2 uv in 0..1 of the compression bounds, f32 x3 normal (32).
			case 25:
				Expect( buffer, 32 );
				for ( var i = 0; i < n; i++ )
				{
					var v = s[(i * stride)..];
					positions[i] = compression.Position( new Vector3( F32( v, 0 ), F32( v, 4 ), F32( v, 8 ) ) );
					texCoords[i] = compression.TexCoord( new Vector2( F32( v, 12 ), F32( v, 16 ) ) );
					normals[i] = new Vector3( F32( v, 20 ), F32( v, 24 ), F32( v, 28 ) );
				}
				break;

			default:
				throw new NotSupportedException( $"Vertex format {buffer.Format} (stride {stride}) isn't a main vertex format" );
		}

		return new GeometryVertices
		{
			Count = n,
			Positions = positions,
			TexCoords = texCoords,
			Normals = normals,
			Tangents = tangents,
			Binormals = binormals,
			NodeIndices = nodeIndices,
			NodeWeights = nodeWeights,
		};
	}

	// A lightmap texcoord buffer (format 4: u16n x2, 0..1 of the BSP's lightmap atlas).
	public static Vector2[] DecodeLightmapTexCoords( VertexBuffer buffer )
	{
		Expect( buffer, 4, 4 );
		var s = buffer.Data.Span;
		var result = new Vector2[buffer.Count];
		for ( var i = 0; i < result.Length; i++ )
			result[i] = Unorm2( s[(i * 4)..], 0 );
		return result;
	}

	// A per-vertex lighting buffer (format 6): 5 RGBA8 shared-exponent values, (rgb * 2 - 1) * 2^(31.75 * (a * 2 - 1)): intensity, c0, c1, c2, c3 (lighting.md §3).
	public static VertexLighting[] DecodeVertexLighting( VertexBuffer buffer )
	{
		Expect( buffer, 6, 20 );
		var s = buffer.Data.Span;
		var result = new VertexLighting[buffer.Count];
		for ( var i = 0; i < result.Length; i++ )
		{
			var v = s[(i * 20)..];
			result[i] = new VertexLighting( SharedExponent( v, 0 ), SharedExponent( v, 4 ), SharedExponent( v, 8 ), SharedExponent( v, 12 ), SharedExponent( v, 16 ) );
		}
		return result;
	}

	static Vector3 SharedExponent( ReadOnlySpan<byte> v, int offset )
	{
		var scale = MathF.Pow( 2, 31.75f * (v[offset + 3] / 255f * 2 - 1) );
		return new Vector3( v[offset] / 255f * 2 - 1, v[offset + 1] / 255f * 2 - 1, v[offset + 2] / 255f * 2 - 1 ) * scale;
	}

	// The mesh's index buffer, or implied indices 0..n-1 for unindexed meshes (decorators, particle models).
	// A water mesh's parameters per vertex (x depth below the surface, y z its global shape texture coordinate, w its displacement scale, 0 where it has none), else null: slot 7 (format 27) holds a 24-byte record per index (six floats: displacement scale, depth, 0, global u, global v, ?), zero outside water parts; Halo 3's water vertex shader reads them.
	public System.Numerics.Vector4[] WaterParameters( GeometryMesh mesh, int vertexCount )
	{
		if ( Buffer( mesh.VertexBuffers.Length > GeometryMesh.SlotWaterParameters ? mesh.VertexBuffers[GeometryMesh.SlotWaterParameters] : -1 ) is not { Format: 27 } buffer || mesh.IndexBufferType != 3 )
			return null;

		var indices = Indices( mesh );
		var floats = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>( buffer.Data.Span );
		var result = new System.Numerics.Vector4[vertexCount];
		var any = false;
		for ( var k = 0; k < indices.Length && (k + 1) * 6 <= floats.Length; k++ )
		{
			var scale = floats[k * 6];
			var depth = floats[k * 6 + 1];
			var u = floats[k * 6 + 3];
			var v = floats[k * 6 + 4];
			if ( indices[k] >= vertexCount || scale <= 0 )
				continue;
			result[indices[k]] = new System.Numerics.Vector4( depth, u, v, scale );
			any = true;
		}

		return any ? result : null;
	}

	public ushort[] Indices( GeometryMesh mesh )
	{
		if ( IsLoaded && mesh.IndexBuffer >= 0 && mesh.IndexBuffer < IndexBuffers.Count )
			return IndexBuffers[mesh.IndexBuffer].Indices;

		if ( Buffer( mesh.MainBuffer ) is not { } buffer )
			return Array.Empty<ushort>();

		var implied = new ushort[buffer.Count];
		for ( var i = 0; i < implied.Length; i++ )
			implied[i] = (ushort)i;
		return implied;
	}

	// A part's triangles in their stored (counter-clockwise front) winding; strips skip degenerates and swap odd triangles counting from the part's start.
	public static IEnumerable<(int A, int B, int C)> Triangles( ushort[] indices, GeometryPart part, int indexBufferType ) =>
		Triangles( indices, part.IndexStart, part.IndexCount, indexBufferType );

	public static IEnumerable<(int A, int B, int C)> Triangles( ushort[] indices, int start, int count, int indexBufferType )
	{
		var end = Math.Min( indices.Length, start + count );
		if ( indexBufferType == 3 )
		{
			for ( var i = start; i + 2 < end; i += 3 )
				yield return (indices[i], indices[i + 1], indices[i + 2]);
			yield break;
		}

		if ( indexBufferType != 5 )
			throw new NotSupportedException( $"Index buffer type {indexBufferType}" );

		for ( var i = start; i + 2 < end; i++ )
		{
			int a = indices[i], b = indices[i + 1], c = indices[i + 2];
			if ( a == b || b == c || a == c )
				continue;
			yield return ((i - start) & 1) == 0 ? (a, b, c) : (a, c, b);
		}
	}

	public IEnumerable<(int A, int B, int C)> Triangles( GeometryMesh mesh, GeometryPart part ) => Triangles( Indices( mesh ), part, mesh.IndexBufferType );

	static void Expect( VertexBuffer buffer, int stride )
	{
		if ( buffer.Stride != stride )
			throw new InvalidDataException( $"Vertex format {buffer.Format} has stride {buffer.Stride}, not {stride}" );
		if ( buffer.Data.Length < buffer.Count * stride )
			throw new InvalidDataException( $"Vertex buffer of {buffer.Count} x {stride} has only {buffer.Data.Length} bytes" );
	}

	static void Expect( VertexBuffer buffer, int format, int stride )
	{
		if ( buffer.Format != format )
			throw new InvalidDataException( $"Vertex buffer has format {buffer.Format}, not {format}" );
		Expect( buffer, stride );
	}

	static float F32( ReadOnlySpan<byte> v, int offset ) => BinaryPrimitives.ReadSingleLittleEndian( v[offset..] );

	static float Unorm( ReadOnlySpan<byte> v, int offset ) => BinaryPrimitives.ReadUInt16LittleEndian( v[offset..] ) / 65535f;

	static float Snorm( ReadOnlySpan<byte> v, int offset ) => MathF.Max( BinaryPrimitives.ReadInt16LittleEndian( v[offset..] ) / 32767f, -1f );

	static Vector3 Unorm3( ReadOnlySpan<byte> v, int offset ) => new( Unorm( v, offset ), Unorm( v, offset + 2 ), Unorm( v, offset + 4 ) );

	static Vector2 Unorm2( ReadOnlySpan<byte> v, int offset ) => new( Unorm( v, offset ), Unorm( v, offset + 2 ) );

	static Vector3 Short3N( ReadOnlySpan<byte> v, int offset ) => new( Snorm( v, offset ), Snorm( v, offset + 2 ), Snorm( v, offset + 4 ) );
}
