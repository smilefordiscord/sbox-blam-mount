using System.Collections.Generic;
using System.Linq;

namespace Blam.Halo2.Tags;

// A collision BSP of a collision model's permutation, in the space of the node it moves with.
public sealed record CollisionNodeBsp( int Node, CollisionBsp Bsp );

public sealed record CollisionModelPermutation( string Name, IReadOnlyList<CollisionNodeBsp> Bsps );

public sealed record CollisionModelRegion( string Name, IReadOnlyList<CollisionModelPermutation> Permutations );

// A Halo 2 collision model (coll): what projectiles and traces hit on an object.
// Regions hold permutations, each a collision BSP per node (0x44 bytes: the node, then the BSP at +4).
public sealed class CollisionModelTag
{
	public static readonly TagClass Class = new( "coll" );

	public TagEntry Tag { get; }

	public IReadOnlyList<CollisionModelRegion> Regions { get; }
	public IReadOnlyList<string> Nodes { get; }

	public CollisionModelTag( TagEntry tag )
	{
		Tag = tag;
		var cache = tag.Cache;
		var data = cache.TagData;
		var a = tag.Address;

		Nodes = data.Elements( a + 0x2C, 0xC ).Select( n => cache.GetString( data.U32( n ) ) ?? "" ).ToList();
		Regions = data.Elements( a + 0x1C, 0xC ).Select( r => new CollisionModelRegion(
			cache.GetString( data.U32( r ) ) ?? "",
			data.Elements( r + 0x4, 0x14 ).Select( p => new CollisionModelPermutation(
				cache.GetString( data.U32( p ) ) ?? "",
				data.Elements( p + 0x4, 0x44 ).Select( b => new CollisionNodeBsp( data.I16( b ), new CollisionBsp( data, b + 4 ) ) ).ToList() ) ).ToList() ) ).ToList();
	}

	public static bool IsCollisionModel( TagEntry tag ) => tag is { Address: not 0 } && tag.Class == Class;
}
