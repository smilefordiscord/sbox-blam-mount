using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

namespace Blam.Halo2;

// Inside the namespace so these win over s&box's global vector types when this compiles into the mount.
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;

/// <summary>
/// One part of a geometry section: a run of indices drawn with one material.
/// </summary>
/// <param name="LodMagic">The "LOD mipmap magic number" (part +0x2C): scales the projected size of its cluster or instance
/// before the shader's LOD thresholds see it; 0 or less counts as 1 (scratch research notes, bsp_lod.md).</param>
public sealed record GeometryPart( int Type, int Flags, int Material, int IndexStart, int IndexLength, float LodMagic = 1 )
{
	/// <summary>
	/// Parts with the "override triangle list" flag are triangle lists (every BSP part); the rest are strips.
	/// </summary>
	public bool IsList => (Flags & 8) != 0;

	/// <summary>
	/// 0 isn't drawn, 1 only casts shadows and 5 is only for the lightmapper; 2 and 3 are opaque, 4 transparent.
	/// </summary>
	public bool IsDrawn => Type is 2 or 3 or 4;
}

/// <summary>
/// The box a render model's compressed vertices are scaled into (the render model's compression info,
/// <c>mode</c> +0x14, element 0). Compressed positions and texture coordinates are floats in [-1, 1];
/// -1 maps to the minimum and 1 to the maximum, per axis.
/// </summary>
public sealed record GeometryCompression( Vector3 PositionMin, Vector3 PositionMax, Vector2 TexCoordMin, Vector2 TexCoordMax )
{
	public Vector3 Position( Vector3 c ) => PositionMin + (c + Vector3.One) * 0.5f * (PositionMax - PositionMin);
	public Vector2 TexCoord( Vector2 c ) => TexCoordMin + (c + Vector2.One) * 0.5f * (TexCoordMax - TexCoordMin);
}

/// <summary>
/// A Halo 2 geometry section: the drawable geometry of a render model section, BSP cluster or instanced
/// geometry definition. Its header is in the tag; the geometry is a raw block in the cache file.
/// </summary>
/// <remarks>
/// Raw block: <c>blkh</c>, a size, the section data struct (parts, subparts, indices, vertex buffer
/// descriptors...), <c>rsrc</c>, the resources, <c>blkf</c>. The section's resource table says which struct
/// field each resource fills (by the field's offset in the struct) and where it is (relative to <c>rsrc</c>).
/// Vertex buffers are separate float streams, one per attribute, identified by a type id.
/// </remarks>
public sealed class GeometrySection
{
	const int ResourceSize = 0x10;
	const int PartSize = 0x48;
	const int VertexBufferDescriptorSize = 0x20;

	// Fields of the section data struct, which the resources' primary locators name.
	const int PartsField = 0x00;
	const int IndicesField = 0x20;
	const int VertexBuffersField = 0x38;
	const int RenderModelNodeMapField = 0x68;

	// Vertex buffer types.
	const int PositionStream = 0x01;
	const int PositionNodeStream = 0x02;
	const int SkinnedPositionStreamFirst = 0x03;
	const int SkinnedPositionStreamLast = 0x08;
	const int TexCoordStream = 0x17;
	const int TangentFrameStream = 0x19;
	const int LightmapTexCoordStream = 0x1D;
	const int IncidentDirectionStream = 0x2F;
	const int ColorStream = 0x2E;

	public int VertexCount { get; private init; }
	public int TriangleCount { get; private init; }

	/// <summary>
	/// 0 worldspace, 1 rigid, 2 rigid boned, 3 skinned.
	/// </summary>
	public int Classification { get; private init; }

	/// <summary>
	/// 1 lightmap texture coordinates, 2 incident radiance, 4 lightmap colours, 8 PRT.
	/// </summary>
	public int LightingFlags { get; private init; }

	/// <summary>
	/// 1 positions, 2 texture coordinates, 4 secondary texture coordinates are compressed. Only render model
	/// sections really are: BSP geometry is plain floats whatever its flags say.
	/// </summary>
	public int CompressionFlags { get; private init; }

	/// <summary>
	/// Render model sections: the node rigid geometry belongs to, or -1.
	/// </summary>
	public int RigidNode { get; private init; } = -1;

	public IReadOnlyList<GeometryPart> Parts { get; private init; } = [];
	public ushort[] Indices { get; private init; } = [];
	public Vector3[] Positions { get; private init; } = [];
	public Vector2[] TexCoords { get; private init; }
	public Vector3[] Normals { get; private init; }
	public Vector3[] Binormals { get; private init; }
	public Vector3[] Tangents { get; private init; }
	public Vector2[] LightmapTexCoords { get; private init; }

	/// <summary>
	/// The lightmap's incoming light direction per vertex; its length is how directional the light is.
	/// </summary>
	public Vector3[] IncidentDirections { get; private init; }

	/// <summary>
	/// The lightmap colour per vertex (0-1, red, green, blue bytes), on the few render model sections the lightmapper
	/// lit per vertex (scenery whose PRT shaders add it to the transfer, see <see cref="RenderModelPrt"/>).
	/// </summary>
	public Vector3[] VertexColors { get; private init; }

	/// <summary>
	/// Per vertex, four node indices (into <see cref="NodeMap"/> if there is one) and four weights out of 255.
	/// </summary>
	public byte[] NodeIndices { get; private init; }
	public byte[] NodeWeights { get; private init; }
	public byte[] NodeMap { get; private init; }

	public bool IsEmpty => VertexCount == 0 || Positions.Length == 0;

	/// <summary>
	/// Reads the section whose header is at <paramref name="address"/> in <paramref name="cache"/>'s tag data.
	/// Render model sections (<paramref name="renderModel"/>) lay their header out differently, and their
	/// positions and texture coordinates are decompressed with <paramref name="compression"/> where the
	/// section's compression flags say so (checked per section: most have both, some only one or neither).
	/// </summary>
	public static GeometrySection Read( CacheFile cache, uint address, bool renderModel = false, GeometryCompression compression = null )
	{
		var data = cache.TagData;
		var header = address + (renderModel ? 4u : 0u);
		var raw = address + (renderModel ? 0x10u : 0u);

		var vertexCount = data.U16( header );
		var rawPointer = data.U32( raw + 0x28 );
		var rawSize = (int)data.U32( raw + 0x2C );
		var structSize = (int)data.U32( raw + 0x30 );
		var resourceDataSize = data.U32( raw + 0x34 );

		var empty = new GeometrySection
		{
			VertexCount = 0,
			TriangleCount = data.U16( header + 2 ),
			Classification = data.U16( header + 0x14 ),
			LightingFlags = data.U16( header + 0x26 ),
			CompressionFlags = data.U16( header + 0x16 ),
			RigidNode = renderModel ? data.I16( address + 0x2C ) : -1,
		};

		// Sections without geometry have just the block header and struct, and lightmap sections none at all.
		if ( vertexCount == 0 || rawPointer == 0xFFFFFFFF || rawSize == 0 || resourceDataSize == 0 )
			return empty;

		var block = cache.ReadRaw( rawPointer, rawSize );
		if ( BinaryPrimitives.ReadUInt32LittleEndian( block ) != 0x626C6B68 ) // 'blkh'
			throw new InvalidDataException( $"Geometry block at 0x{rawPointer:X8} in '{cache.Name}' doesn't start with blkh" );

		var resourceBase = 8 + structSize;
		if ( BinaryPrimitives.ReadUInt32LittleEndian( block.AsSpan( resourceBase ) ) != 0x72737263 ) // 'rsrc'
			throw new InvalidDataException( $"Geometry block at 0x{rawPointer:X8} in '{cache.Name}' has no rsrc marker" );

		// Resource table: u8 type (0 block, 1 data, 2 vertex buffer), u8, u16, s16 field, s16 element size or
		// vertex buffer index, u32 size, u32 offset from the rsrc marker.
		var fields = new Dictionary<int, (int ElementSize, int Offset, int Size)>();
		var vertexBuffers = new Dictionary<int, (int Offset, int Size)>();
		foreach ( var e in data.Elements( raw + 0x38, ResourceSize ) )
		{
			var type = data.U8( e );
			var field = data.I16( e + 4 );
			var secondary = data.I16( e + 6 );
			var size = (int)data.U32( e + 8 );
			var offset = resourceBase + (int)data.U32( e + 12 );
			if ( type == 2 )
				vertexBuffers[secondary] = (offset, size);
			else
				fields[field] = (secondary, offset, size);
		}

		var parts = new List<GeometryPart>();
		if ( fields.TryGetValue( PartsField, out var partsResource ) )
		{
			for ( var p = 0; p < partsResource.Size / PartSize; p++ )
			{
				var span = block.AsSpan( partsResource.Offset + p * PartSize );
				parts.Add( new GeometryPart(
					Type: BinaryPrimitives.ReadUInt16LittleEndian( span ),
					Flags: BinaryPrimitives.ReadUInt16LittleEndian( span[2..] ),
					Material: BinaryPrimitives.ReadInt16LittleEndian( span[4..] ),
					IndexStart: BinaryPrimitives.ReadUInt16LittleEndian( span[6..] ),
					IndexLength: BinaryPrimitives.ReadUInt16LittleEndian( span[8..] ),
					LodMagic: BinaryPrimitives.ReadSingleLittleEndian( span[0x2C..] ) ) );
			}
		}

		var indices = Array.Empty<ushort>();
		if ( fields.TryGetValue( IndicesField, out var indexResource ) )
		{
			indices = new ushort[indexResource.Size / 2];
			for ( var i = 0; i < indices.Length; i++ )
				indices[i] = BinaryPrimitives.ReadUInt16LittleEndian( block.AsSpan( indexResource.Offset + i * 2 ) );
		}

		byte[] nodeMap = null;
		if ( renderModel && fields.TryGetValue( RenderModelNodeMapField, out var nodeMapResource ) )
			nodeMap = block.AsSpan( nodeMapResource.Offset, nodeMapResource.Size ).ToArray();

		Vector3[] positions = [];
		Vector2[] texCoords = null, lightmapTexCoords = null;
		Vector3[] normals = null, binormals = null, tangents = null, incident = null, colors = null;
		byte[] nodeIndices = null, nodeWeights = null;

		if ( fields.TryGetValue( VertexBuffersField, out var descriptors ) )
		{
			for ( var b = 0; b < descriptors.Size / VertexBufferDescriptorSize; b++ )
			{
				var type = block[descriptors.Offset + b * VertexBufferDescriptorSize];
				var stride = block[descriptors.Offset + b * VertexBufferDescriptorSize + 1];
				if ( !vertexBuffers.TryGetValue( b, out var buffer ) || buffer.Size < stride * vertexCount )
					throw new InvalidDataException( $"Vertex buffer {b} (type 0x{type:X2}) of a section in '{cache.Name}' is missing or short" );

				var stream = block.AsSpan( buffer.Offset, buffer.Size );
				switch ( type )
				{
					case PositionStream:
					case PositionNodeStream:
					case >= SkinnedPositionStreamFirst and <= SkinnedPositionStreamLast:
						positions = ReadVector3s( stream, stride, 0, vertexCount );
						if ( type != PositionStream )
							(nodeIndices, nodeWeights) = ReadNodes( stream, stride, vertexCount, type == PositionNodeStream );
						break;

					case TexCoordStream:
						texCoords = ReadVector2s( stream, stride, 0, vertexCount );
						break;

					case TangentFrameStream:
						normals = ReadVector3s( stream, stride, 0, vertexCount );
						binormals = ReadVector3s( stream, stride, 12, vertexCount );
						tangents = ReadVector3s( stream, stride, 24, vertexCount );
						break;

					case LightmapTexCoordStream:
						lightmapTexCoords = ReadVector2s( stream, stride, 0, vertexCount );
						break;

					case IncidentDirectionStream:
						incident = ReadVector3s( stream, stride, 0, vertexCount );
						break;

					case ColorStream:
						colors = new Vector3[vertexCount];
						for ( var i = 0; i < vertexCount; i++ )
							colors[i] = new Vector3( stream[i * stride], stream[i * stride + 1], stream[i * stride + 2] ) / 255f;
						break;
				}
			}
		}

		if ( renderModel && compression is not null )
		{
			if ( (empty.CompressionFlags & 1) != 0 )
			{
				for ( var i = 0; i < positions.Length; i++ )
					positions[i] = compression.Position( positions[i] );
			}

			if ( (empty.CompressionFlags & 2) != 0 && texCoords is not null )
			{
				for ( var i = 0; i < texCoords.Length; i++ )
					texCoords[i] = compression.TexCoord( texCoords[i] );
			}
		}

		return new GeometrySection
		{
			VertexCount = vertexCount,
			TriangleCount = empty.TriangleCount,
			Classification = empty.Classification,
			LightingFlags = empty.LightingFlags,
			CompressionFlags = empty.CompressionFlags,
			RigidNode = empty.RigidNode,
			Parts = parts,
			Indices = indices,
			Positions = positions,
			TexCoords = texCoords,
			Normals = normals,
			Binormals = binormals,
			Tangents = tangents,
			LightmapTexCoords = lightmapTexCoords,
			IncidentDirections = incident,
			VertexColors = colors,
			NodeIndices = nodeIndices,
			NodeWeights = nodeWeights,
			NodeMap = nodeMap,
		};
	}

	/// <summary>
	/// A part's triangles as vertex indices, front faces counter-clockwise (Halo's winding). Strips restart
	/// with degenerate triangles, which are left out; every other strip triangle is flipped.
	/// </summary>
	public IEnumerable<(int A, int B, int C)> Triangles( GeometryPart part )
	{
		var end = Math.Min( Indices.Length, part.IndexStart + part.IndexLength );
		if ( part.IsList )
		{
			for ( var i = part.IndexStart; i + 2 < end; i += 3 )
				yield return (Indices[i], Indices[i + 1], Indices[i + 2]);
			yield break;
		}

		for ( var i = part.IndexStart; i + 2 < end; i++ )
		{
			int a = Indices[i], b = Indices[i + 1], c = Indices[i + 2];
			if ( a == b || b == c || a == c )
				continue;

			yield return (i - part.IndexStart) % 2 == 0 ? (a, b, c) : (a, c, b);
		}
	}

	static Vector3[] ReadVector3s( ReadOnlySpan<byte> stream, int stride, int offset, int count )
	{
		var result = new Vector3[count];
		for ( var i = 0; i < count; i++ )
		{
			var s = stream[(i * stride + offset)..];
			result[i] = new Vector3( BinaryPrimitives.ReadSingleLittleEndian( s ), BinaryPrimitives.ReadSingleLittleEndian( s[4..] ), BinaryPrimitives.ReadSingleLittleEndian( s[8..] ) );
		}

		return result;
	}

	static Vector2[] ReadVector2s( ReadOnlySpan<byte> stream, int stride, int offset, int count )
	{
		var result = new Vector2[count];
		for ( var i = 0; i < count; i++ )
		{
			var s = stream[(i * stride + offset)..];
			result[i] = new Vector2( BinaryPrimitives.ReadSingleLittleEndian( s ), BinaryPrimitives.ReadSingleLittleEndian( s[4..] ) );
		}

		return result;
	}

	/// <summary>
	/// Node indices and weights after the position: one node (rigid boned, full weight) or four indices and
	/// four weights out of 255.
	/// </summary>
	static (byte[] Indices, byte[] Weights) ReadNodes( ReadOnlySpan<byte> stream, int stride, int count, bool singleNode )
	{
		var indices = new byte[count * 4];
		var weights = new byte[count * 4];
		for ( var i = 0; i < count; i++ )
		{
			var s = stream[(i * stride + 12)..];
			if ( singleNode )
			{
				indices[i * 4] = s[0];
				weights[i * 4] = 255;
				continue;
			}

			s[..4].CopyTo( indices.AsSpan( i * 4 ) );
			s[4..8].CopyTo( weights.AsSpan( i * 4 ) );
		}

		return (indices, weights);
	}
}
