using System.Collections.Generic;

namespace Blam.Halo3.Tags;

// A collision BSP of a coll permutation, in its node's space.
public sealed record CollisionModelBsp( int Node, CollisionBsp Bsp );

public sealed record CollisionModelPermutation( string Name, IReadOnlyList<CollisionModelBsp> Bsps );

public sealed record CollisionModelRegion( string Name, IReadOnlyList<CollisionModelPermutation> Permutations );

// A collision_model (coll) tag: materials (model material names), regions -> permutations -> BSPs per node (geometry.md §2.4b).
public sealed class CollisionModelTag
{
	public TagEntry Tag { get; }
	public IReadOnlyList<string> Materials { get; }
	public IReadOnlyList<CollisionModelRegion> Regions { get; }

	public CollisionModelTag( TagEntry tag )
	{
		Tag = tag;
		var d = tag.Data;

		var materials = new List<string>();
		foreach ( var m in d.Block( 0x14, 4 ) )
			materials.Add( m.StringId( 0 ) ?? "" );
		Materials = materials;

		var regions = new List<CollisionModelRegion>();
		foreach ( var r in d.Block( 0x20, 0x10 ) )
		{
			var permutations = new List<CollisionModelPermutation>();
			foreach ( var p in r.Block( 4, 0x28 ) )
			{
				var bsps = new List<CollisionModelBsp>();
				foreach ( var b in p.Block( 4, 0x64 ) )
					bsps.Add( new CollisionModelBsp( b.I16( 0 ), CollisionBsp.FromTag( b.At( 4 ) ) ) );
				permutations.Add( new CollisionModelPermutation( p.StringId( 0 ) ?? "", bsps ) );
			}
			regions.Add( new CollisionModelRegion( r.StringId( 0 ) ?? "", permutations ) );
		}
		Regions = regions;
	}
}
