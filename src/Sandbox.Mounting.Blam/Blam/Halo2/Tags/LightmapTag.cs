using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Blam.Halo2.Tags;

// Inside the namespace so these win over s&box's global vector types when this compiles into the mount.
using Vector3 = System.Numerics.Vector3;

/// <summary>
/// A lightmap geometry bucket: per-vertex lighting for instanced geometry, for every vertex of the instances that
/// use it one after the other. Either array may be null.
/// </summary>
/// <param name="IncidentDirections">The incoming light direction per vertex, in world space (unlike the vertices,
/// already turned the way the instance places them: 99% face the placed normal, little over half the definition's);
/// its length says how directional the light is.</param>
/// <param name="Colors">The light colour per vertex, 0-1 (8-bit gamma values, like a lightmap page's texels),
/// for instances lit per vertex.</param>
public sealed record LightmapBucket( Vector3[] IncidentDirections, Vector3[] Colors )
{
	public int VertexCount => IncidentDirections?.Length ?? Colors?.Length ?? 0;
}

/// <summary>
/// A lighting environment of a lightmap: the light arriving at a point, as order 3 spherical harmonics per colour
/// channel (D3DX convention, see <see cref="SphericalHarmonics"/>), and the direction the light mostly comes from
/// (its "bump direction", the same as its mean incoming direction in the maps checked). PRT objects are lit
/// by these (<see cref="PrtLight.Sample"/>).
/// </summary>
/// <summary>
/// A scenery object the lightmap was baked with: its lighting is per vertex in a geometry bucket, every section of
/// its render model (all permutations, all levels of detail) one after the other, and Halo draws it with that
/// instead of its object lighting (Turf's medical tent, Zanzibar's pillars and dome lights).
/// </summary>
/// <param name="ObjectId">The placement's object id (<see cref="ObjectPlacement.ObjectId"/>).</param>
/// <param name="SectionOffsets">Per render model section, its first vertex in the bucket.</param>
public sealed record SceneryObjectLighting( ulong ObjectId, int Bucket, IReadOnlyList<int> SectionOffsets );

public sealed record LightingEnvironment( Vector3 Point, float[] Red, float[] Green, float[] Blue, Vector3 Direction );

/// <summary>
/// A Halo 2 lightmap (<c>ltmp</c>), as far as drawing the BSP needs it: one lightmap page (a bitmap of the
/// lightmap bitmap tag) per BSP cluster and per instanced geometry instance lit per pixel, and the lighting per
/// vertex of instanced geometry (incident directions of every instance, colours of the ones lit per vertex). The
/// pages are A8R8G8B8 in MCC; the clusters' lightmap UVs and incident directions are in the BSP's own geometry.
/// </summary>
public sealed class LightmapTag
{
	public static readonly TagClass Class = new( "ltmp" );

	const uint GroupsBlock = 0x80;
	const int GroupSize = 0x68;
	const int RenderInfoSize = 4;
	const int BucketSize = 0x38;
	const int EnvironmentSize = 0xDC;
	const int InstanceBucketRefSize = 0xC;
	const int SceneryObjectInfoSize = 0xC;
	const int ResourceSize = 0x10;

	// Vertex buffer types (as in GeometrySection).
	const int IncidentDirectionStream = 0x2F;
	const int ColorStream = 0x2E;

	public TagEntry Tag { get; }

	/// <summary>
	/// The bitmap tag holding the pages.
	/// </summary>
	public TagEntry Bitmaps { get; }

	/// <summary>
	/// The page of each BSP cluster (bitmap index), or -1.
	/// </summary>
	public IReadOnlyList<int> ClusterPages { get; } = [];

	/// <summary>
	/// The page of each instanced geometry instance, or -1 for instances lit per vertex.
	/// </summary>
	public IReadOnlyList<int> InstancePages { get; } = [];

	public IReadOnlyList<LightmapBucket> Buckets { get; } = [];

	/// <summary>
	/// The lighting environments (spherical harmonic light probes) PRT objects are lit by; most BSPs have none.
	/// </summary>
	public IReadOnlyList<LightingEnvironment> Environments { get; } = [];

	/// <summary>
	/// Per instanced geometry instance, the bucket holding its per-vertex lighting and its first vertex there, or
	/// (-1, 0).
	/// </summary>
	public IReadOnlyList<(int Bucket, int FirstVertex)> InstanceBuckets { get; } = [];

	/// <summary>
	/// The scenery objects lit per vertex (none on most multiplayer maps).
	/// </summary>
	public IReadOnlyList<SceneryObjectLighting> SceneryObjects { get; } = [];

	public LightmapTag( TagEntry tag )
	{
		Tag = tag;
		var cache = tag.Cache;
		var data = cache.TagData;

		// Every MCC BSP has a single lightmap group.
		var group = data.Elements( tag.Address + GroupsBlock, GroupSize ).FirstOrDefault();
		if ( group == 0 )
			return;

		Bitmaps = cache.GetTagReference( group + 0x18 );
		ClusterPages = data.Elements( group + 0x28, RenderInfoSize ).Select( e => (int)data.I16( e ) ).ToList();
		InstancePages = data.Elements( group + 0x48, RenderInfoSize ).Select( e => (int)data.I16( e ) ).ToList();
		Buckets = data.Elements( group + 0x40, BucketSize ).Select( e => ReadBucket( cache, e ) ).ToList();

		// Lighting environment (0xDC): sample point, 9 red, 9 green, 9 blue coefficients, mean incoming direction
		// (+0x78), intensity, specular bitmap, rotation axis and speed (0 in every map), bump direction (+0xA4), tint,
		// the lightmapper's procedural settings.
		float[] Coefficients( uint at ) => Enumerable.Range( 0, 9 ).Select( i => data.F32( at + (uint)(i * 4) ) ).ToArray();
		Environments = data.Elements( group + 0x38, EnvironmentSize ).Select( e => new LightingEnvironment(
			data.Vec3( e ), Coefficients( e + 0xC ), Coefficients( e + 0x30 ), Coefficients( e + 0x54 ), data.Vec3( e + 0xA4 ) ) ).ToList();

		// Instance bucket reference: int16 flags, int16 bucket, then a block of uint16 section offsets (one per
		// section of the definition, which is always one): the instance's first vertex in the bucket.
		InstanceBuckets = data.Elements( group + 0x50, InstanceBucketRefSize ).Select( e =>
		{
			var offsets = data.Elements( e + 4, 2 ).ToList();
			return ((int)data.I16( e + 2 ), offsets.Count > 0 ? (int)data.U16( offsets[0] ) : 0);
		} ).ToList();

		// Scenery object info (0xC): the object id as the placement has it (uint32 unique id, int16 origin BSP,
		// uint8 type, uint8 source), then a uint32 (the render model's checksum?). Its bucket reference, same
		// index in the next block, is laid out like an instance's with one offset per render model section.
		var infos = data.Elements( group + 0x58, SceneryObjectInfoSize ).ToList();
		var refs = data.Elements( group + 0x60, InstanceBucketRefSize ).ToList();
		SceneryObjects = Enumerable.Range( 0, Math.Min( infos.Count, refs.Count ) ).Select( i => new SceneryObjectLighting(
			data.U32( infos[i] ) | (ulong)data.U32( infos[i] + 4 ) << 32,
			data.I16( refs[i] + 2 ),
			data.Elements( refs[i] + 4, 2 ).Select( o => (int)data.U16( o ) ).ToList() ) ).ToList();
	}

	/// <summary>
	/// The per-vertex lighting of an instance with <paramref name="vertexCount"/> vertices: its incident directions
	/// and colours (either null when its bucket has none), or nulls.
	/// </summary>
	public (ArraySegment<Vector3> IncidentDirections, ArraySegment<Vector3> Colors) InstanceLighting( int instance, int vertexCount )
	{
		if ( instance < 0 || instance >= InstanceBuckets.Count )
			return default;

		var (bucket, first) = InstanceBuckets[instance];
		return BucketLighting( bucket, first, vertexCount );
	}

	/// <summary>
	/// The per-vertex lighting of section <paramref name="section"/> (with <paramref name="vertexCount"/> vertices)
	/// of a scenery object's render model, like <see cref="InstanceLighting"/>.
	/// </summary>
	public (ArraySegment<Vector3> IncidentDirections, ArraySegment<Vector3> Colors) SceneryLighting( SceneryObjectLighting scenery, int section, int vertexCount )
	{
		if ( section < 0 || section >= scenery.SectionOffsets.Count )
			return default;

		return BucketLighting( scenery.Bucket, scenery.SectionOffsets[section], vertexCount );
	}

	(ArraySegment<Vector3> IncidentDirections, ArraySegment<Vector3> Colors) BucketLighting( int bucket, int first, int vertexCount )
	{
		if ( bucket < 0 || bucket >= Buckets.Count || first + vertexCount > Buckets[bucket].VertexCount )
			return default;

		var b = Buckets[bucket];
		return (b.IncidentDirections is null ? default : new( b.IncidentDirections, first, vertexCount ),
			b.Colors is null ? default : new( b.Colors, first, vertexCount ));
	}

	/// <summary>
	/// A bucket (0x38): uint16 flags (1 incident directions, 2 colours), then at 0xC a raw block like a geometry
	/// section's (pointer, size, struct size, resource data size, resources at 0x1C). Its struct is just the
	/// vertex buffer descriptor block, and its vertex buffers are float3 incident directions (type 0x2F) and colours
	/// (type 0x2E: red, green, blue bytes and a zero).
	/// </summary>
	static LightmapBucket ReadBucket( CacheFile cache, uint address )
	{
		var data = cache.TagData;
		var rawPointer = data.U32( address + 0xC );
		var rawSize = (int)data.U32( address + 0x10 );
		var structSize = (int)data.U32( address + 0x14 );
		if ( rawPointer == 0xFFFFFFFF || rawSize == 0 )
			return new LightmapBucket( null, null );

		var block = cache.ReadRaw( rawPointer, rawSize );
		var resourceBase = 8 + structSize;
		if ( BinaryPrimitives.ReadUInt32LittleEndian( block ) != 0x626C6B68 || BinaryPrimitives.ReadUInt32LittleEndian( block.AsSpan( resourceBase ) ) != 0x72737263 )
			throw new InvalidDataException( $"Lightmap bucket at 0x{rawPointer:X8} in '{cache.Name}' isn't a blkh/rsrc block" );

		// Resources: uint8 type (0 block, 2 vertex buffer), uint8, uint16, int16 field, int16 element size or
		// buffer index, uint32 size, uint32 offset from the rsrc marker.
		(int Offset, int Size) descriptors = default;
		var buffers = new Dictionary<int, (int Offset, int Size)>();
		foreach ( var e in data.Elements( address + 0x1C, ResourceSize ) )
		{
			var resource = (Offset: resourceBase + (int)data.U32( e + 12 ), Size: (int)data.U32( e + 8 ));
			if ( data.U8( e ) == 2 )
				buffers[data.I16( e + 6 )] = resource;
			else if ( data.I16( e + 4 ) == 0 )
				descriptors = resource;
		}

		Vector3[] directions = null, colors = null;
		for ( var b = 0; b < descriptors.Size / 0x20; b++ )
		{
			var type = block[descriptors.Offset + b * 0x20];
			if ( !buffers.TryGetValue( b, out var buffer ) )
				continue;

			var stream = block.AsSpan( buffer.Offset, buffer.Size );
			if ( type == IncidentDirectionStream )
			{
				directions = new Vector3[buffer.Size / 12];
				for ( var i = 0; i < directions.Length; i++ )
				{
					var s = stream[(i * 12)..];
					directions[i] = new Vector3( BinaryPrimitives.ReadSingleLittleEndian( s ), BinaryPrimitives.ReadSingleLittleEndian( s[4..] ), BinaryPrimitives.ReadSingleLittleEndian( s[8..] ) );
				}
			}
			else if ( type == ColorStream )
			{
				// Red first: Lockout's warm light fixtures come out orange and its cool ones blue this way round,
				// and the colours follow the lightmap pages' red/blue balance where they touch the BSP.
				colors = new Vector3[buffer.Size / 4];
				for ( var i = 0; i < colors.Length; i++ )
					colors[i] = new Vector3( stream[i * 4], stream[i * 4 + 1], stream[i * 4 + 2] ) / 255f;
			}
		}

		return new LightmapBucket( directions, colors );
	}
}
