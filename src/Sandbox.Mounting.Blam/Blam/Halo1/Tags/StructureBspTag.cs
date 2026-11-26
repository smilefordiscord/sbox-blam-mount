using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using Blam.IO;

namespace Blam.Halo1.Tags;

// Inside the namespace so these win over s&box's global Vector2/Vector3 when this compiles into the mount.
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;

/// <summary>
/// A world-geometry vertex: position, normal, binormal, tangent, texture coordinates (56 bytes).
/// </summary>
public readonly record struct RenderedVertex( Vector3 Position, Vector3 Normal, Vector3 Binormal, Vector3 Tangent, Vector2 TexCoord );

/// <summary>
/// The lightmap half of a world-geometry vertex: incoming light direction and lightmap
/// texture coordinates (20 bytes).
/// </summary>
public readonly record struct LightmapVertex( Vector3 Normal, Vector2 TexCoord );

/// <summary>
/// A collision BSP surface: a convex polygon on a plane.
/// </summary>
/// <param name="Normal">The plane's normal, facing the surface's front (open) side.</param>
/// <param name="Flags">Two-sided 1, invisible 2, climbable 4, breakable 8.</param>
/// <param name="Material">Index into <see cref="StructureBspTag.CollisionMaterials"/>, or -1.</param>
public sealed record CollisionSurface( Vector3 Normal, float Distance, byte Flags, Vector3[] Corners, int Material = -1 )
{
	public bool TwoSided => (Flags & 1) != 0;
	public bool Invisible => (Flags & 2) != 0;
	public bool Climbable => (Flags & 4) != 0;
}

/// <summary>
/// What collision surfaces are made of: a shader, and the material type it gives them.
/// </summary>
/// <param name="Type">The material type, or null if neither the shader nor the cached value has a valid one.</param>
public sealed record BspCollisionMaterial( TagEntry Shader, MaterialType? Type );

/// <summary>
/// A run of triangles sharing one shader and one lightmap page.
/// </summary>
/// <param name="FirstSurface">Index of the first triangle in <see cref="StructureBspTag.Surfaces"/>.</param>
/// <param name="RenderedVertexOffset">Byte offset of this material's vertices in the BSP's vertex data.</param>
/// <param name="LightmapVertexOffset">Byte offset of this material's lightmap vertices, or -1 if it isn't lightmapped.</param>
/// <param name="AmbientColor">The ambient light the lightmapper found here, which Halo lights objects with.</param>
/// <param name="BreakableSurface">The breakable surface these triangles draw, which stops drawing once broken, or -1.</param>
public sealed record BspMaterial(
	TagEntry Shader,
	int FirstSurface,
	int SurfaceCount,
	int VertexCount,
	int RenderedVertexOffset,
	int LightmapVertexOffset,
	Vector3 AmbientColor,
	int BreakableSurface );

/// <summary>
/// A lightmap page and the materials drawn with it.
/// </summary>
/// <param name="BitmapIndex">Index into the BSP's lightmap bitmap tag, or -1 for unlit geometry.</param>
public sealed record BspLightmap( int BitmapIndex, IReadOnlyList<BspMaterial> Materials );

/// <summary>
/// Reader for the render geometry of <c>sbsp</c> tags.
/// </summary>
/// <remarks>
/// A BSP's data is loaded at its own address, separate from the tag data. It begins with a
/// header: a pointer to the <c>sbsp</c> tag, then on MCC the size and file offset of the BSP's
/// vertex data, which MCC keeps near the start of the map file instead of inside the BSP, then
/// the <c>sbsp</c> signature.
///
/// Fields used, relative to the tag: lightmap bitmap reference at 0x0, world bounds at 0xC8,
/// surfaces (triangles as three 16-bit indices) at 0xF8, lightmaps at 0x104 (32 bytes each,
/// with a block of 256 byte materials at 0x14). Each material's vertex buffer descriptors at
/// 0xB0 and 0xC4 give vertex counts and byte offsets into the vertex data.
/// </remarks>
public sealed partial class StructureBspTag
{
	const uint SbspSignature = 0x73627370; // 'sbsp'
	const int RenderedVertexSize = 56;
	const int LightmapVertexSize = 20;

	readonly byte[] _vertexData;
	readonly MemoryRegion _region;
	readonly uint _tag;

	public StructureBspReference Reference { get; }

	/// <summary>
	/// The BSP's data region and the address of the <c>sbsp</c> tag in it, for readers of parts of
	/// the tag this class doesn't cover.
	/// </summary>
	public MemoryRegion Data => _region;
	public uint TagAddress => _tag;
	public TagEntry LightmapBitmaps { get; }
	public Vector3 WorldBoundsMin { get; }
	public Vector3 WorldBoundsMax { get; }
	public IReadOnlyList<BspLightmap> Lightmaps { get; }

	/// <summary>
	/// The collision materials that collision surfaces index (see <see cref="CollisionSurface.Material"/>).
	/// </summary>
	public IReadOnlyList<BspCollisionMaterial> CollisionMaterials { get; }

	/// <summary>
	/// Triangle vertex indices, three per surface, relative to the owning material's vertices.
	/// </summary>
	public ushort[] Surfaces { get; }

	public StructureBspTag( CacheFile cache, StructureBspReference reference )
	{
		Reference = reference;

		var region = new MemoryRegion( cache.Read( reference.FileOffset, reference.Size ), reference.Address );
		if ( region.U32( reference.Address + 0x14 ) != SbspSignature )
			throw new InvalidDataException( $"{reference.Tag} has no 'sbsp' signature in its header" );

		var tag = region.U32( reference.Address );
		_region = region;
		_tag = tag;
		var vertexDataSize = region.I32( reference.Address + 0x4 );
		var vertexDataOffset = region.U32( reference.Address + 0x8 );
		_vertexData = cache.Read( vertexDataOffset, vertexDataSize );

		LightmapBitmaps = cache.GetTagReference( region, tag );
		WorldBoundsMin = new Vector3( region.F32( tag + 0xC8 ), region.F32( tag + 0xD0 ), region.F32( tag + 0xD8 ) );
		WorldBoundsMax = new Vector3( region.F32( tag + 0xCC ), region.F32( tag + 0xD4 ), region.F32( tag + 0xDC ) );

		var surfaceCount = region.I32( tag + 0xF8 );
		var surfaces = region.Slice( region.U32( tag + 0xFC ), surfaceCount * 6 );
		Surfaces = new ushort[surfaceCount * 3];
		for ( var i = 0; i < Surfaces.Length; i++ )
			Surfaces[i] = BinaryPrimitives.ReadUInt16LittleEndian( surfaces[(i * 2)..] );

		var lightmaps = new List<BspLightmap>();
		foreach ( var lightmap in region.Block( tag + 0x104, 0x20 ) )
		{
			var materials = new List<BspMaterial>();
			foreach ( var material in region.Block( lightmap + 0x14, 0x100 ) )
			{
				var lightmapVertexCount = region.I32( material + 0xC8 );
				materials.Add( new BspMaterial(
					Shader: cache.GetTagReference( region, material ),
					FirstSurface: region.I32( material + 0x14 ),
					SurfaceCount: region.I32( material + 0x18 ),
					VertexCount: region.I32( material + 0xB4 ),
					RenderedVertexOffset: region.I32( material + 0xB8 ),
					LightmapVertexOffset: lightmapVertexCount > 0 ? region.I32( material + 0xCC ) : -1,
					AmbientColor: region.Vec3( material + 0x28 ),
					BreakableSurface: region.I16( material + 0xAC ) ) );
			}

			lightmaps.Add( new BspLightmap( region.I16( lightmap ), materials ) );
		}

		Lightmaps = lightmaps;

		// Collision materials (0xA4, 20 bytes): shader 0x0, then the shader's material type at 0x12,
		// which tool.exe copies in when it builds the map. The shader is the authority; the copy is
		// the fallback for a material whose shader is missing.
		var collisionMaterials = new List<BspCollisionMaterial>();
		foreach ( var material in region.Block( tag + 0xA4, 0x14 ) )
		{
			var shader = cache.GetTagReference( region, material );
			var cached = region.I16( material + 0x12 );
			collisionMaterials.Add( new BspCollisionMaterial( shader,
				MaterialTypes.OfShader( cache, shader ) ?? (MaterialTypes.IsValid( cached ) ? (MaterialType)cached : null) ) );
		}

		CollisionMaterials = collisionMaterials;
	}

	/// <summary>
	/// The material type of a collision surface's material index, or null if it has none.
	/// </summary>
	public MaterialType? GetCollisionMaterialType( int material ) =>
		material >= 0 && material < CollisionMaterials.Count ? CollisionMaterials[material].Type : null;

	/// <summary>
	/// Reads the collision geometry as triangles, in world units. This is what Halo uses for
	/// movement and bullets, including invisible walls and floors that aren't drawn.
	/// </summary>
	/// <remarks>
	/// The collision BSP's surfaces are polygons stored as a winged edge structure: each edge
	/// knows the surfaces on its left and right, and the next edge around each of them. Walking
	/// from a surface's first edge gives its corners in order, and each polygon becomes a fan of
	/// triangles. Offsets in the collision BSP block (at 0xB0, 96 bytes): surfaces 0x3C (12 bytes:
	/// plane, first edge, flags), edges 0x48 (24 bytes: start/end vertex, forward/reverse edge,
	/// left/right surface), vertices 0x54 (16 bytes: point, first edge).
	/// </remarks>
	public (List<Vector3> Vertices, List<int> Indices) ReadCollision() => ReadCollision( null );

	/// <summary>
	/// Reads the collision geometry as triangles, as <see cref="ReadCollision()"/> does, and
	/// each triangle's collision material index (-1 for none) into <paramref name="triangleMaterials"/>,
	/// its surface's flags (see <see cref="CollisionSurface.Flags"/>) into <paramref name="triangleFlags"/>
	/// and its breakable surface (-1 for a surface that isn't breakable) into <paramref name="triangleBreakables"/>.
	/// </summary>
	/// <remarks>
	/// A surface's material is the int16 at 0xA of its 12 bytes (after the flags byte at 0x8 and
	/// the breakable surface index at 0x9).
	/// </remarks>
	public (List<Vector3> Vertices, List<int> Indices) ReadCollision( List<int> triangleMaterials, List<byte> triangleFlags = null, List<int> triangleBreakables = null )
	{
		var vertices = new List<Vector3>();
		var indices = new List<int>();
		var data = _region;

		foreach ( var collision in data.Block( _tag + 0xB0, 0x60 ) )
		{
			var baseIndex = vertices.Count;
			foreach ( var vertex in data.Block( collision + 0x54, 0x10 ) )
				vertices.Add( data.Vec3( vertex ) );

			var edgeCount = data.I32( collision + 0x48 );
			var edges = data.U32( collision + 0x4C );
			var surface = 0;
			var polygon = new List<int>();

			foreach ( var entry in data.Block( collision + 0x3C, 0xC ) )
			{
				polygon.Clear();
				var first = data.I32( entry + 0x4 );
				var edge = first;

				// A polygon can't have more corners than there are edges; stop on bad data.
				for ( var guard = 0; guard < edgeCount && edge >= 0 && edge < edgeCount; guard++ )
				{
					var e = edges + (uint)(edge * 0x18);
					if ( data.I32( e + 0x10 ) == surface )
					{
						polygon.Add( data.I32( e ) );
						edge = data.I32( e + 0x8 );
					}
					else
					{
						polygon.Add( data.I32( e + 0x4 ) );
						edge = data.I32( e + 0xC );
					}

					if ( edge == first )
						break;
				}

				var material = data.I16( entry + 0xA );
				var flags = data.U8( entry + 0x8 );
				var breakable = (flags & 8) != 0 ? data.U8( entry + 0x9 ) : -1;
				for ( var i = 1; i + 1 < polygon.Count; i++ )
				{
					indices.Add( baseIndex + polygon[0] );
					indices.Add( baseIndex + polygon[i] );
					indices.Add( baseIndex + polygon[i + 1] );
					triangleMaterials?.Add( material );
					triangleFlags?.Add( flags );
					triangleBreakables?.Add( breakable );
				}

				surface++;
			}
		}

		return (vertices, indices);
	}

	/// <summary>
	/// Reads the collision BSP's surfaces with their planes and flags, as triangle fans.
	/// </summary>
	/// <remarks>
	/// Planes are at 0xC in the collision BSP block (16 bytes: normal, distance). A surface's plane
	/// index has its top bit set when the surface faces the other way from the plane. Surface flags
	/// (byte 0x8): two-sided 1, invisible 2, climbable 4, breakable 8.
	/// </remarks>
	public List<CollisionSurface> ReadCollisionSurfaces()
	{
		var surfaces = new List<CollisionSurface>();
		var data = _region;

		foreach ( var collision in data.Block( _tag + 0xB0, 0x60 ) )
		{
			var points = new List<Vector3>();
			foreach ( var vertex in data.Block( collision + 0x54, 0x10 ) )
				points.Add( data.Vec3( vertex ) );

			var planes = new List<(Vector3 Normal, float Distance)>();
			foreach ( var plane in data.Block( collision + 0x0C, 0x10 ) )
				planes.Add( (data.Vec3( plane ), data.F32( plane + 0xC )) );

			var edgeCount = data.I32( collision + 0x48 );
			var edges = data.U32( collision + 0x4C );
			var index = 0;

			foreach ( var entry in data.Block( collision + 0x3C, 0xC ) )
			{
				var planeIndex = data.U32( entry );
				var flipped = (planeIndex & 0x80000000) != 0;
				var p = (int)(planeIndex & 0x7FFFFFFF);
				var (normal, distance) = p < planes.Count ? planes[p] : (Vector3.UnitZ, 0f);
				if ( flipped ) { normal = -normal; distance = -distance; }

				var polygon = new List<Vector3>();
				var first = data.I32( entry + 0x4 );
				var edge = first;
				for ( var guard = 0; guard < edgeCount && edge >= 0 && edge < edgeCount; guard++ )
				{
					var e = edges + (uint)(edge * 0x18);
					int corner;
					if ( data.I32( e + 0x10 ) == index )
					{
						corner = data.I32( e );
						edge = data.I32( e + 0x8 );
					}
					else
					{
						corner = data.I32( e + 0x4 );
						edge = data.I32( e + 0xC );
					}

					if ( corner >= 0 && corner < points.Count )
						polygon.Add( points[corner] );
					if ( edge == first )
						break;
				}

				surfaces.Add( new CollisionSurface( normal, distance, data.U8( entry + 0x8 ), polygon.ToArray(), data.I16( entry + 0xA ) ) );
				index++;
			}
		}

		return surfaces;
	}

	public RenderedVertex[] ReadRenderedVertices( BspMaterial material )
	{
		var vertices = new RenderedVertex[material.VertexCount];
		var data = _vertexData.AsSpan( material.RenderedVertexOffset, material.VertexCount * RenderedVertexSize );

		for ( var i = 0; i < vertices.Length; i++ )
		{
			var v = data.Slice( i * RenderedVertexSize, RenderedVertexSize );
			vertices[i] = new RenderedVertex( ReadVector3( v ), ReadVector3( v[12..] ), ReadVector3( v[24..] ), ReadVector3( v[36..] ), ReadVector2( v[48..] ) );
		}

		return vertices;
	}

	/// <summary>
	/// Returns the material's lightmap vertices, or null if it isn't lightmapped.
	/// </summary>
	public LightmapVertex[] ReadLightmapVertices( BspMaterial material )
	{
		if ( material.LightmapVertexOffset < 0 )
			return null;

		var vertices = new LightmapVertex[material.VertexCount];
		var data = _vertexData.AsSpan( material.LightmapVertexOffset, material.VertexCount * LightmapVertexSize );

		for ( var i = 0; i < vertices.Length; i++ )
		{
			var v = data.Slice( i * LightmapVertexSize, LightmapVertexSize );
			vertices[i] = new LightmapVertex( ReadVector3( v ), ReadVector2( v[12..] ) );
		}

		return vertices;
	}

	static Vector2 ReadVector2( ReadOnlySpan<byte> data ) => new(
		BinaryPrimitives.ReadSingleLittleEndian( data ),
		BinaryPrimitives.ReadSingleLittleEndian( data[4..] ) );

	static Vector3 ReadVector3( ReadOnlySpan<byte> data ) => new(
		BinaryPrimitives.ReadSingleLittleEndian( data ),
		BinaryPrimitives.ReadSingleLittleEndian( data[4..] ),
		BinaryPrimitives.ReadSingleLittleEndian( data[8..] ) );
}
