using System.Collections.Generic;

namespace Blam.Halo3.Tags;

// How an instance is lit by its lightmap BSP: lightprobe texture index (per pixel when >= 0), per-vertex block, probe (-1 none).
public readonly record struct LightmapInstance( int LightprobeTexture, int PerVertex, int Probe );

// A scenario_lightmap_bsp_data (Lbsp) tag: the BSP's real render geometry and how each cluster and instance is lit (lighting.md §1.2).
public sealed class LightmapBspTag
{
	public TagEntry Tag { get; }
	public int BspIndex { get; }
	public uint BspChecksum { get; }

	// Per-vertex lighting blocks: the format 6 vertex buffer each one's SH is in.
	public IReadOnlyList<int> PerVertexBuffers { get; }
	public IReadOnlyList<LightmapInstance> Clusters { get; }
	public IReadOnlyList<LightmapInstance> Instances { get; }

	public LightmapBspTag( TagEntry tag )
	{
		Tag = tag;
		var d = tag.Data;
		BspIndex = d.I16( 2 );
		BspChecksum = d.U32( 4 );

		var perVertex = new List<int>();
		foreach ( var p in d.Block( 0x100, 0x10 ) )
			perVertex.Add( p.I32( 0xC ) );
		PerVertexBuffers = perVertex;

		var clusters = new List<LightmapInstance>();
		foreach ( var c in d.Block( 0x10C, 4 ) )
			clusters.Add( new LightmapInstance( c.I16( 0 ), c.I16( 2 ), -1 ) );
		Clusters = clusters;

		var instances = new List<LightmapInstance>();
		foreach ( var i in d.Block( 0x118, 8 ) )
			instances.Add( new LightmapInstance( i.I16( 0 ), i.I16( 2 ), i.I16( 4 ) ) );
		Instances = instances;
	}

	public RenderGeometry Geometry() => new( Tag.Data, RenderGeometry.LightmapBspOffset );

	// An instance probe (0x48 of half floats: direction, intensity rgb, R c0..c8, G c0..c8, B c0..c8) as per-vertex style lighting: intensity and the L1 part.
	public VertexLighting? Probe( int index )
	{
		var probe = Tag.Data.Element( 0x124, 0x48, index );
		if ( probe.IsNull )
			return null;

		float H( int k ) => (float)System.BitConverter.UInt16BitsToHalf( probe.U16( 2 * k ) );
		System.Numerics.Vector3 C( int k ) => new( H( 8 + k ), H( 17 + k ), H( 26 + k ) );
		return new VertexLighting( new System.Numerics.Vector3( H( 4 ), H( 5 ), H( 6 ) ), C( 0 ), C( 1 ), C( 2 ), C( 3 ) );
	}
}
