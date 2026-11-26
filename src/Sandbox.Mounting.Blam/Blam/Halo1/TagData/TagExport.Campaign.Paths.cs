using System;
using System.Collections.Generic;
using Blam.Halo1.Tags;
using Blam.IO;

namespace Blam.Halo1.TagData;

public static partial class TagExport
{
	/// <summary>
	/// What AI pathfinding reads from one of a solo map's structure BSPs (by its index in the
	/// scenario), as JSON: its tag <c>Name</c>, its collision BSP's blocks and its pathfinding
	/// surfaces, each block's elements exactly as the tag stores them (little endian) in base 64:
	/// <list type="bullet">
	/// <item><c>Bsp3dNodes</c> (12 bytes: plane, back child, front child; a child with its top bit
	/// set is a leaf, -1 solid), <c>Planes</c> (16 bytes: normal, distance), <c>Leaves</c> (8 bytes:
	/// flags 0x0 (two-sided surfaces 1), 2D reference count 0x2, first 2D reference 0x4),
	/// <c>Bsp2dReferences</c> (8 bytes: plane with its top bit set when flipped, 2D root node),
	/// <c>Bsp2dNodes</c> (20 bytes: 2D plane i, j, d, then two children; a child with its top bit
	/// set is a surface), <c>Surfaces</c> (12 bytes: plane 0x0, first edge 0x4, flags 0x8 (two-sided
	/// 1, invisible 2, climbable 4, breakable 8), breakable surface 0x9, material 0xA),
	/// <c>Edges</c> (24 bytes: start/end vertex, forward/reverse edge, left/right surface),
	/// <c>Vertices</c> (16 bytes: point, first edge).</item>
	/// <item><c>PathfindingSurfaces</c>: a byte per collision surface (walkable 0x40, breakable 0x80,
	/// low bits the quantized width and height).</item>
	/// <item><c>BreakableSurfaceCount</c>: the BSP's breakable surfaces, and <c>BreakableSurfaces</c>
	/// (48 bytes: centroid 0x0, bounding radius 0xC, a collision surface on it 0x10).</item>
	/// <item><c>CollisionMaterials</c>, which collision surfaces' material indices index (20 bytes:
	/// shader 0x0, its material type 0x12).</item>
	/// <item>For finding a point's cluster and what it sees: <c>StructureLeaves</c>, the structure
	/// BSP's leaves (16 bytes, cluster at 0x8; the collision BSP's leaf indices index them),
	/// <c>ClusterCount</c> and <c>ClusterPvs</c> (per cluster a bit vector of the clusters it sees,
	/// in 32-bit words).</item>
	/// <item>For AI hearing: <c>ClusterSoundData</c>, the BSP's sound PAS data (a byte per pair of
	/// clusters i &lt; j at i * (count - 1) - i * (i + 1) / 2 + j - 1: top bit set where sound
	/// doesn't carry, else the low bits are the encoded distance), and <c>DeafeningClusters</c>, a
	/// byte per cluster, 1 where its background sound is flagged deafening to AIs.</item>
	/// </list>
	/// </summary>
	public static byte[] CampaignPaths( CacheFile cache, int bsp ) => Write( json =>
	{
		var reference = new ScenarioTag( cache, cache.Scenario ).StructureBsps[bsp];
		var d = new MemoryRegion( cache.Read( reference.FileOffset, reference.Size ), reference.Address );
		var tag = d.U32( reference.Address );

		json.WriteStartObject();
		json.WriteString( "Name", TagJsonWriter.TagName( reference.Tag ) );

		// A structure BSP has one collision BSP (0xB0, 96 bytes).
		var collision = d.I32( tag + 0xB0 ) > 0 ? d.U32( tag + 0xB4 ) : 0;
		json.WriteBase64String( "Bsp3dNodes", BlockBytes( d, collision, 0x0, 12 ) );
		json.WriteBase64String( "Planes", BlockBytes( d, collision, 0xC, 16 ) );
		json.WriteBase64String( "Leaves", BlockBytes( d, collision, 0x18, 8 ) );
		json.WriteBase64String( "Bsp2dReferences", BlockBytes( d, collision, 0x24, 8 ) );
		json.WriteBase64String( "Bsp2dNodes", BlockBytes( d, collision, 0x30, 20 ) );
		json.WriteBase64String( "Surfaces", BlockBytes( d, collision, 0x3C, 12 ) );
		json.WriteBase64String( "Edges", BlockBytes( d, collision, 0x48, 24 ) );
		json.WriteBase64String( "Vertices", BlockBytes( d, collision, 0x54, 16 ) );

		json.WriteBase64String( "PathfindingSurfaces", BlockBytes( d, tag, 0x1E4, 1 ) );
		json.WriteNumber( "BreakableSurfaceCount", d.I32( tag + 0x16C ) );
		json.WriteBase64String( "BreakableSurfaces", BlockBytes( d, tag, 0x16C, 48 ) );
		json.WriteBase64String( "CollisionMaterials", BlockBytes( d, tag, 0xA4, 20 ) );

		json.WriteBase64String( "StructureLeaves", BlockBytes( d, tag, 0xE0, 16 ) );
		json.WriteNumber( "ClusterCount", d.I32( tag + 0x134 ) );
		json.WriteBase64String( "ClusterPvs", TagDataBytes( d, tag + 0x140 ) );
		json.WriteBase64String( "ClusterSoundData", TagDataBytes( d, tag + 0x214 ) );
		json.WriteBase64String( "DeafeningClusters", DeafeningClusters( cache, d, tag ) );
		json.WriteEndObject();
	} );

	/// <summary>
	/// A byte per cluster: 1 where the cluster's background sound (clusters at 0x134, 0x68 bytes,
	/// palette index 0x4; background sound palette at 0x1FC, 0x74 bytes, looping sound 0x20) is a
	/// looping sound flagged deafening to AIs (its flags' bit 0).
	/// </summary>
	static byte[] DeafeningClusters( CacheFile cache, MemoryRegion d, uint tag )
	{
		var palette = new List<bool>();
		foreach ( var entry in d.Block( tag + 0x1FC, 0x74 ) )
			palette.Add( cache.GetTagReference( d, entry + 0x20 ) is { } sound && (new LoopingSoundTag( cache, sound ).Flags & 1) != 0 );

		var result = new List<byte>();
		foreach ( var cluster in d.Block( tag + 0x134, 0x68 ) )
		{
			var index = d.I16( cluster + 0x4 );
			result.Add( (byte)(index >= 0 && index < palette.Count && palette[index] ? 1 : 0) );
		}
		return result.ToArray();
	}

	/// <summary>
	/// The elements of a tag block of the struct at <paramref name="owner"/>, as stored.
	/// </summary>
	static ReadOnlySpan<byte> BlockBytes( MemoryRegion d, uint owner, uint block, int elementSize )
	{
		if ( owner == 0 )
			return ReadOnlySpan<byte>.Empty;

		var count = d.I32( owner + block );
		var first = d.U32( owner + block + 4 );
		return count > 0 && d.Contains( first, count * elementSize ) ? d.Slice( first, count * elementSize ) : ReadOnlySpan<byte>.Empty;
	}
}
