using System.Collections.Generic;

namespace Blam.Halo3.Tags;

// The scenario's potentially visible sets (scnr +0x40, 0x2C each, one per BSP combination a zone set loads): the BSPs it covers (+0x0) and per BSP (+0x14, 0x54 each) per cluster (+0x0, 0xC each) a bit vector over each BSP's clusters (+0x0, 0xC each, bits +0x0 as 32-bit words). +0xC holds the same with doors closed.
public sealed class ZoneSetVisibility
{
	public uint BspMask { get; }

	// Sees[bsp][cluster][otherBsp] = bits over otherBsp's clusters (null where absent).
	public IReadOnlyList<IReadOnlyList<IReadOnlyList<uint[]>>> Sees { get; }

	ZoneSetVisibility( TagView pvs )
	{
		BspMask = pvs.U32( 0 );
		var bsps = new List<IReadOnlyList<IReadOnlyList<uint[]>>>();
		foreach ( var bsp in pvs.Block( 0x14, 0x54 ) )
		{
			var clusters = new List<IReadOnlyList<uint[]>>();
			foreach ( var cluster in bsp.Block( 0x0, 0xC ) )
			{
				var perBsp = new List<uint[]>();
				foreach ( var other in cluster.Block( 0x0, 0xC ) )
				{
					var words = other.Block( 0x0, 0x4 );
					var bits = new uint[words.Count];
					for ( var i = 0; i < bits.Length; i++ )
						bits[i] = words[i].U32( 0 );
					perBsp.Add( bits );
				}
				clusters.Add( perBsp );
			}
			bsps.Add( clusters );
		}
		Sees = bsps;
	}

	public static List<ZoneSetVisibility> Read( CacheFile cache )
	{
		var sets = new List<ZoneSetVisibility>();
		foreach ( var pvs in cache.Scenario.Data.Block( 0x40, 0x2C ) )
			sets.Add( new ZoneSetVisibility( pvs ) );
		return sets;
	}

	// Whether a cluster of one BSP sees a cluster of another (or the same).
	public bool Visible( int bsp, int cluster, int otherBsp, int otherCluster )
	{
		if ( bsp < 0 || bsp >= Sees.Count || cluster < 0 || cluster >= Sees[bsp].Count )
			return false;
		var perBsp = Sees[bsp][cluster];
		if ( otherBsp < 0 || otherBsp >= perBsp.Count )
			return false;
		var bits = perBsp[otherBsp];
		var word = otherCluster >> 5;
		return word < bits.Length && (bits[word] & (1u << (otherCluster & 31))) != 0;
	}
}
