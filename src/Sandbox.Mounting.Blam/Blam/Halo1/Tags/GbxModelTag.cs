using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace Blam.Halo1.Tags;

// Inside the namespace so these win over s&box's global Vector2/Vector3 when this compiles into the mount.
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;
using Quaternion = System.Numerics.Quaternion;

/// <summary>
/// A model vertex: position, normal, binormal, tangent, texture coordinates and up to two
/// weighted nodes (68 bytes).
/// </summary>
public readonly record struct ModelVertex( Vector3 Position, Vector3 Normal, Vector3 Binormal, Vector3 Tangent, Vector2 TexCoord, int Node0, int Node1, float Weight0, float Weight1 );

/// <summary>
/// A piece of a model drawn with one shader.
/// </summary>
/// <param name="LocalNodes">Maps the vertices' node indices to the model's nodes, or null if they index the model's nodes directly.</param>
public sealed record ModelPart( int ShaderIndex, int VertexCount, int VertexOffset, int TriangleCount, int TriangleOffset, byte[] LocalNodes );

/// <summary>
/// One variant of a region, with a geometry index per level of detail.
/// </summary>
/// <param name="Lods">Geometry indices from super low to super high detail.</param>
/// <param name="DrawnLevel">The level of detail drawn at normal sizes (see <see cref="GbxModelTag.DrawnLevel"/>).</param>
public sealed record ModelPermutation( string Name, int[] Lods, int DrawnLevel = 4 )
{
	public int HighestDetailGeometry => Lods[^1];

	/// <summary>The geometry drawn at normal sizes: the one at <see cref="DrawnLevel"/>.</summary>
	public int DrawnGeometry => Lods[Math.Clamp( DrawnLevel, 0, Lods.Length - 1 )];
}

/// <summary>
/// A part of a model that can be swapped between variants, e.g. a damaged and undamaged hull.
/// </summary>
public sealed record ModelRegion( string Name, IReadOnlyList<ModelPermutation> Permutations );

public sealed record ModelShader( TagEntry Shader, int Permutation );

/// <summary>
/// A node (bone) of the model's skeleton, in its default pose.
/// </summary>
/// <param name="Translation">Position relative to the parent node, in world units.</param>
/// <param name="Rotation">Rotation relative to the parent node, in the usual quaternion convention.</param>
public sealed record ModelNode( string Name, int ParentIndex, Vector3 Translation = default, Quaternion Rotation = default );

/// <summary>
/// One place a marker is: on a node, for one permutation of one region.
/// </summary>
/// <param name="Translation">Relative to the node, in world units.</param>
/// <param name="Rotation">Relative to the node, in the usual quaternion convention.</param>
public readonly record struct GbxMarkerInstance( int Region, int Permutation, int Node, Vector3 Translation, Quaternion Rotation );

/// <summary>
/// A named point on a model ("primary trigger", "right hand", "driver"), where Halo attaches
/// effects, projectiles, held items and riders. A marker can be in more than one place: Halo uses
/// every instance whose permutation its region currently shows.
/// </summary>
public sealed record GbxMarker( string Name, IReadOnlyList<GbxMarkerInstance> Instances );

/// <summary>
/// Reader for <c>mod2</c> (gbxmodel) tags.
/// </summary>
/// <remarks>
/// Offsets: flags 0x0, detail cutoffs 0x8 (5 floats, super low to super high, in pixels), base map U/V scale 0x30/0x34, markers 0xAC (64 bytes: name, instances
/// 0x34 of 32 bytes: region, permutation and node bytes, translation 0x4, rotation 0x10), nodes 0xB8 (156 bytes: name, sibling,
/// child, parent, default translation 0x28, default rotation 0x34), regions 0xC4 (76 bytes: name, permutations at 0x40), geometries 0xD0
/// (48 bytes: parts at 0x24), shaders 0xDC (32 bytes: reference, permutation).
///
/// Permutations are 88 bytes with geometry indices for each level of detail at 0x40, and a
/// markers block at 0x4C that the tools empty into the model's markers when they build a map. Parts are
/// 132 bytes: shader index 0x4, triangle count/offset 0x48/0x4C, vertex count 0x58, vertex
/// offset 0x64, local node count 0x6B and up to 24 local node indices at 0x6C.
///
/// Vertex positions are in model space, in the default pose, whatever nodes they're weighted to
/// (also for parts with local nodes, whose node indices go through the part's node table).
///
/// The vertices and triangles live in the cache file's shared model data, at the part's byte
/// offsets from <see cref="CacheFile.VertexDataOffset"/> and <see cref="CacheFile.IndexDataOffset"/>.
/// Triangles are stored as one triangle strip per part, with degenerate triangles joining
/// the pieces.
/// </remarks>
public sealed class GbxModelTag
{
	const int VertexSize = 68;

	public TagEntry Tag { get; }

	/// <summary>
	/// The screen size in pixels (the object's bounding sphere's diameter) each level of detail
	/// needs, super low to super high. Halo starts at super high and steps down while the object is
	/// smaller than the level's cutoff.
	/// </summary>
	public IReadOnlyList<float> DetailCutoffs { get; }

	/// <summary>
	/// The level of detail a model draws at normal sizes: the highest whose cutoff is under
	/// <see cref="UnreachableCutoff"/>. Nearly every model's super high level is drawn from a few
	/// hundred pixels (a character within a few world units), so that's this level; the one model
	/// with a super high level Halo draws only from inside it is a10's cryotube (3000 pixels), whose
	/// super high geometry alone has a frozen Spartan in the tube. The mount takes a model's
	/// collision, bounds and skeleton from this level, and draws it where nothing picks a level.
	/// </summary>
	public int DrawnLevel { get; }

	/// <summary>
	/// A cutoff no object reaches at normal viewing distances, only with the camera inside it.
	/// </summary>
	public const float UnreachableCutoff = 1000;

	public bool PartsHaveLocalNodes { get; }
	public Vector2 BaseMapScale { get; }
	public IReadOnlyList<ModelNode> Nodes { get; }
	public IReadOnlyList<ModelRegion> Regions { get; }
	public IReadOnlyList<IReadOnlyList<ModelPart>> Geometries { get; }
	public IReadOnlyList<ModelShader> Shaders { get; }
	public IReadOnlyList<GbxMarker> Markers { get; }

	public GbxModelTag( CacheFile cache, TagEntry tag )
	{
		if ( tag.Class != TagClasses.GbxModel )
			throw new ArgumentException( $"{tag} is not a gbxmodel tag", nameof( tag ) );

		Tag = tag;
		var data = cache.TagData;
		var a = tag.Address;

		PartsHaveLocalNodes = (data.U32( a ) & 0x2) != 0;
		var cutoffs = new float[5];
		for ( var i = 0; i < cutoffs.Length; i++ )
			cutoffs[i] = data.F32( a + 0x8 + (uint)(i * 4) );
		DetailCutoffs = cutoffs;
		var drawn = DetailCutoffs.Count - 1;
		while ( drawn > 0 && DetailCutoffs[drawn] >= UnreachableCutoff )
			drawn--;
		DrawnLevel = drawn;
		BaseMapScale = new Vector2( DefaultOne( data.F32( a + 0x30 ) ), DefaultOne( data.F32( a + 0x34 ) ) );

		var markers = new List<GbxMarker>();
		foreach ( var marker in data.Block( a + 0xAC, 0x40 ) )
		{
			var instances = new List<GbxMarkerInstance>();
			foreach ( var instance in data.Block( marker + 0x34, 0x20 ) )
			{
				var rotation = new Quaternion( data.F32( instance + 0x10 ), data.F32( instance + 0x14 ), data.F32( instance + 0x18 ), data.F32( instance + 0x1C ) );
				instances.Add( new GbxMarkerInstance( (sbyte)data.U8( instance ), (sbyte)data.U8( instance + 1 ), (sbyte)data.U8( instance + 2 ), data.Vec3( instance + 0x4 ), ModelAnimationsTag.FromHalo( rotation ) ) );
			}
			markers.Add( new GbxMarker( data.CString( marker, 32 ), instances ) );
		}
		Markers = markers;

		var nodes = new List<ModelNode>();
		foreach ( var node in data.Block( a + 0xB8, 0x9C ) )
		{
			var rotation = new Quaternion( data.F32( node + 0x34 ), data.F32( node + 0x38 ), data.F32( node + 0x3C ), data.F32( node + 0x40 ) );
			nodes.Add( new ModelNode( data.CString( node, 32 ), data.I16( node + 0x24 ), data.Vec3( node + 0x28 ), ModelAnimationsTag.FromHalo( rotation ) ) );
		}
		Nodes = nodes;

		var regions = new List<ModelRegion>();
		foreach ( var region in data.Block( a + 0xC4, 0x4C ) )
		{
			var permutations = new List<ModelPermutation>();
			foreach ( var permutation in data.Block( region + 0x40, 0x58 ) )
			{
				var lods = new int[5];
				for ( var i = 0; i < lods.Length; i++ )
					lods[i] = data.I16( permutation + 0x40 + (uint)(i * 2) );
				permutations.Add( new ModelPermutation( data.CString( permutation, 32 ), lods, DrawnLevel ) );
			}

			regions.Add( new ModelRegion( data.CString( region, 32 ), permutations ) );
		}
		Regions = regions;

		var geometries = new List<IReadOnlyList<ModelPart>>();
		foreach ( var geometry in data.Block( a + 0xD0, 0x30 ) )
		{
			var parts = new List<ModelPart>();
			foreach ( var part in data.Block( geometry + 0x24, 0x84 ) )
			{
				byte[] localNodes = null;
				if ( PartsHaveLocalNodes )
				{
					var count = Math.Min( (int)data.U8( part + 0x6B ), 24 );
					localNodes = data.Slice( part + 0x6C, count ).ToArray();
				}

				parts.Add( new ModelPart(
					ShaderIndex: data.I16( part + 0x4 ),
					VertexCount: data.I32( part + 0x58 ),
					VertexOffset: data.I32( part + 0x64 ),
					TriangleCount: data.I32( part + 0x48 ),
					TriangleOffset: data.I32( part + 0x4C ),
					LocalNodes: localNodes ) );
			}
			geometries.Add( parts );
		}
		Geometries = geometries;

		var shaders = new List<ModelShader>();
		foreach ( var shader in data.Block( a + 0xDC, 0x20 ) )
			shaders.Add( new ModelShader( cache.GetTagReference( data, shader ), data.I16( shader + 0x10 ) ) );
		Shaders = shaders;
	}

	/// <summary>
	/// Reads a part's vertices, with texture coordinates scaled by the model's base map scale and
	/// node indices resolved through the part's local node table.
	/// </summary>
	public ModelVertex[] ReadVertices( CacheFile cache, ModelPart part )
	{
		var data = cache.Read( cache.VertexDataOffset + part.VertexOffset, part.VertexCount * VertexSize );
		var vertices = new ModelVertex[part.VertexCount];

		for ( var i = 0; i < vertices.Length; i++ )
		{
			var v = data.AsSpan( i * VertexSize, VertexSize );
			var uv = ReadVector2( v[48..] );

			vertices[i] = new ModelVertex(
				Position: ReadVector3( v ),
				Normal: ReadVector3( v[12..] ),
				Binormal: ReadVector3( v[24..] ),
				Tangent: ReadVector3( v[36..] ),
				TexCoord: uv * BaseMapScale,
				Node0: ResolveNode( part, BinaryPrimitives.ReadInt16LittleEndian( v[56..] ) ),
				Node1: ResolveNode( part, BinaryPrimitives.ReadInt16LittleEndian( v[58..] ) ),
				Weight0: BinaryPrimitives.ReadSingleLittleEndian( v[60..] ),
				Weight1: BinaryPrimitives.ReadSingleLittleEndian( v[64..] ) );
		}

		return vertices;
	}

	/// <summary>
	/// Reads a part's triangles as a plain list of vertex indices, three per triangle, wound the
	/// same way as the strip's first triangle. Degenerate triangles are dropped.
	/// </summary>
	public static List<int> ReadTriangles( CacheFile cache, ModelPart part )
	{
		var count = part.TriangleCount + 2;
		var data = cache.Read( cache.IndexDataOffset + part.TriangleOffset, count * 2 );
		var strip = new int[count];
		for ( var i = 0; i < count; i++ )
			strip[i] = BinaryPrimitives.ReadUInt16LittleEndian( data.AsSpan( i * 2 ) );

		var triangles = new List<int>( part.TriangleCount * 3 );
		for ( var i = 0; i < part.TriangleCount; i++ )
		{
			int a = strip[i], b = strip[i + 1], c = strip[i + 2];
			if ( a == b || b == c || a == c )
				continue;

			// Every other triangle in a strip has the opposite winding.
			if ( (i & 1) == 0 )
				triangles.AddRange( [a, b, c] );
			else
				triangles.AddRange( [a, c, b] );
		}

		return triangles;
	}

	static int ResolveNode( ModelPart part, int index )
	{
		if ( index < 0 ) return -1;
		if ( part.LocalNodes is null ) return index;
		return index < part.LocalNodes.Length ? part.LocalNodes[index] : -1;
	}

	static Vector2 ReadVector2( ReadOnlySpan<byte> data ) => new(
		BinaryPrimitives.ReadSingleLittleEndian( data ),
		BinaryPrimitives.ReadSingleLittleEndian( data[4..] ) );

	static Vector3 ReadVector3( ReadOnlySpan<byte> data ) => new(
		BinaryPrimitives.ReadSingleLittleEndian( data ),
		BinaryPrimitives.ReadSingleLittleEndian( data[4..] ),
		BinaryPrimitives.ReadSingleLittleEndian( data[8..] ) );

	static float DefaultOne( float value ) => value == 0 ? 1 : value;
}
