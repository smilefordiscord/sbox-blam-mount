using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Blam.Visibility;

// Inside the namespace so it wins over s&box's global Vector3 when this compiles into the mount.
using Vector3 = System.Numerics.Vector3;

// The clusters a box of space reaches, as visibility cluster ids (1 up; 0 is the catch-all every view sees); false when splitting the box can't separate them (they overlap everywhere in it, as BSPs that share space do).
public interface IClusterSpace
{
	bool Reach( Vector3 min, Vector3 max, ClusterSet clusters );
}

// A set of cluster ids, cleared and refilled for each box.
public sealed class ClusterSet
{
	int[] _stamps = new int[64];
	int _stamp = 1;
	public readonly List<int> Ids = new();

	public int Count => Ids.Count;

	public void Clear()
	{
		Ids.Clear();
		if ( ++_stamp == int.MaxValue )
		{
			Array.Clear( _stamps );
			_stamp = 1;
		}
	}

	public void Add( int id )
	{
		if ( id >= _stamps.Length )
			Array.Resize( ref _stamps, Math.Max( id + 1, _stamps.Length * 2 ) );
		if ( _stamps[id] == _stamp )
			return;
		_stamps[id] = _stamp;
		Ids.Add( id );
	}
}

// Builds Source 2's voxel visibility (CVoxelVisibility, a world_visibility.vvis): an octree over a cube of space whose leaves split into 4x4x4 cells, each cell owned by the clusters reaching into it, and a row of visible clusters per cluster.
// Layout as ValveResourceFormat reads it (VoxelVisibility.cs, MIT): nodes are two words (leaf bit and child or region offset; region count and enclosed cluster list), regions a 64 bit word (cluster, leaf, cell mask index).
public sealed class VoxelVisibilityBuilder
{
	// Cell bit of a point's first and second octant within a leaf, as the engine works it out (x + 4y + 16z).
	static readonly int[] Level1 = [0, 2, 8, 10, 32, 34, 40, 42];
	static readonly int[] Level2 = [0, 1, 4, 5, 16, 17, 20, 21];

	readonly IClusterSpace _space;
	readonly float _grid;
	readonly List<(uint First, uint Second)> _nodes = new();
	readonly List<ulong> _regions = new();
	readonly List<ulong> _masks = new();
	readonly Dictionary<ulong, int> _maskIds = new();
	readonly List<(int Offset, int Count)> _lists = new();
	readonly List<ushort> _listed = new();
	readonly Dictionary<string, int> _listIds = new();
	readonly Stack<ClusterSet> _sets = new();
	int _leaves;

	public int Nodes => _nodes.Count;
	public int Regions => _regions.Count;
	public int Masks => _masks.Count;

	VoxelVisibilityBuilder( IClusterSpace space, float grid )
	{
		_space = new Nearest( space );
		_grid = grid;
	}

	// Space in no cluster (solid, or outside the level) takes the clusters nearest it, found by growing the box until it reaches some, and isn't split further: geometry there (a backdrop beyond the walkable hull) then draws with its neighbours instead of never, and a camera inside a wall sees what they see.
	sealed class Nearest( IClusterSpace space ) : IClusterSpace
	{
		public bool Reach( Vector3 min, Vector3 max, ClusterSet clusters )
		{
			var mixed = space.Reach( min, max, clusters );
			if ( clusters.Count > 0 )
				return mixed;

			var grow = MathF.Max( max.X - min.X, MathF.Max( max.Y - min.Y, max.Z - min.Z ) );
			for ( var i = 0; i < 12 && clusters.Count == 0; i++ )
			{
				min -= new Vector3( grow );
				max += new Vector3( grow );
				grow *= 2;
				space.Reach( min, max, clusters );
			}
			return false;
		}
	}

	// Builds the resource for clusters 1..clusterCount over the space between min and max (s&box units), with each cluster's row of the clusters it sees.
	public static byte[] Build( IClusterSpace space, Vector3 min, Vector3 max, float grid, int clusterCount, Func<int, IEnumerable<int>> sees, out string stats )
	{
		// Cells as fine as Source 2's (32 units) on small levels, up to the given size on huge ones, so the octree stays a few hundred thousand nodes.
		var span = MathF.Max( max.X - min.X, MathF.Max( max.Y - min.Y, max.Z - min.Z ) );
		if ( !float.IsFinite( span ) || span <= 0 || span > 1e7f )
			throw new ArgumentException( $"The space to cover isn't a usable box ({min} to {max})" );
		grid = Math.Clamp( span / 512, 32, grid );
		var builder = new VoxelVisibilityBuilder( space, grid );

		// A cube whose edge is the leaf size (4 cells) times a power of two, a cell wider than the space all round.
		var lo = min - new Vector3( grid );
		var extent = span + grid * 2;
		var size = grid * 4;
		while ( size < extent )
			size *= 2;

		builder._nodes.Add( default );
		builder.Node( 0, lo, size );

		var total = clusterCount + 1;
		var rowBytes = (total + 31) / 32 * 4;
		var rows = new byte[(total + 2) * rowBytes];
		void Set( int row, int cluster ) => rows[row * rowBytes + (cluster >> 3)] |= (byte)(1 << (cluster & 7));
		for ( var c = 0; c < total; c++ )
		{
			Set( 0, c );
			Set( total, c ); // sky
			Set( total + 1, c ); // sun
		}
		for ( var c = 1; c < total; c++ )
		{
			Set( c, 0 );
			Set( c, c );
			foreach ( var seen in sees( c ) )
			{
				if ( seen > 0 && seen < total )
					Set( c, seen );
			}
		}

		var block = new MemoryStream();
		var w = new BinaryWriter( block );
		var data = new Kv3Object
		{
			{ "m_nBaseClusterCount", (uint)total },
			{ "m_nPVSBytesPerCluster", (uint)rowBytes },
			{ "m_vMinBounds", new List<object> { lo.X, lo.Y, lo.Z } },
			{ "m_vMaxBounds", new List<object> { lo.X + size, lo.Y + size, lo.Z + size } },
			{ "m_flGridSize", grid },
			{ "m_nSkyVisibilityCluster", (uint)total },
			{ "m_nSunVisibilityCluster", (uint)(total + 1) },
		};

		void Put( string name, int count, Action write )
		{
			while ( block.Length % 8 != 0 )
				w.Write( (byte)0 );
			data.Add( name, new Kv3Object { { "m_nOffset", (uint)block.Length }, { "m_nElementCount", (uint)count } } );
			write();
		}

		Put( "m_NodeBlock", builder._nodes.Count, () => { foreach ( var (a, b) in builder._nodes ) { w.Write( a ); w.Write( b ); } } );
		Put( "m_RegionBlock", builder._regions.Count, () => { foreach ( var r in builder._regions ) w.Write( r ); } );
		Put( "m_EnclosedClusterListBlock", builder._lists.Count, () => { foreach ( var (o, n) in builder._lists ) { w.Write( o ); w.Write( n ); } } );
		Put( "m_EnclosedClustersBlock", builder._listed.Count, () => { foreach ( var c in builder._listed ) w.Write( c ); } );
		Put( "m_MasksBlock", builder._masks.Count, () => { foreach ( var m in builder._masks ) w.Write( m ); } );
		Put( "m_nVisBlocks", rows.Length, () => w.Write( rows ) );

		stats = $"{clusterCount} clusters, {size:0} unit cube, {grid:0} unit cells, {builder._nodes.Count} nodes, {builder._regions.Count} regions, {builder._masks.Count} masks, {builder._lists.Count} cluster lists, {block.Length / 1024} KB";
		return CompiledResource.Write( 7, ("DATA", Kv3Writer.Write( data )), ("VXVS", block.ToArray()) );
	}

	ClusterSet Rent()
	{
		var set = _sets.Count > 0 ? _sets.Pop() : new ClusterSet();
		set.Clear();
		return set;
	}

	void Return( ClusterSet set ) => _sets.Push( set );

	// Fills node index for the cube at min; returns the clusters under it.
	List<int> Node( int index, Vector3 min, float size )
	{
		var reached = Rent();
		var mixed = _space.Reach( min, min + new Vector3( size ), reached );
		try
		{
			if ( !mixed || reached.Count <= 1 || size <= _grid * 4 * 1.001f )
				return Leaf( index, min, size, reached, mixed );

			var children = _nodes.Count;
			for ( var o = 0; o < 8; o++ )
				_nodes.Add( default );

			var half = size / 2;
			var under = new SortedSet<int>();
			for ( var o = 0; o < 8; o++ )
			{
				var corner = min + new Vector3( (o & 1) != 0 ? half : 0, (o & 2) != 0 ? half : 0, (o & 4) != 0 ? half : 0 );
				under.UnionWith( Node( children + o, corner, half ) );
			}

			var list = under.ToList();
			_nodes[index] = ((uint)children << 1, (uint)(List( list ) << 8));
			return list;
		}
		finally
		{
			Return( reached );
		}
	}

	List<int> Leaf( int index, Vector3 min, float size, ClusterSet reached, bool mixed )
	{
		var cells = new SortedDictionary<int, ulong>();
		if ( !mixed || reached.Count == 1 )
		{
			foreach ( var id in reached.Ids )
				cells[id] = ulong.MaxValue;
		}
		else if ( reached.Count > 1 )
		{
			var half = size / 2;
			var quarter = size / 4;
			for ( var o1 = 0; o1 < 8; o1++ )
			{
				var c1 = min + new Vector3( (o1 & 1) != 0 ? half : 0, (o1 & 2) != 0 ? half : 0, (o1 & 4) != 0 ? half : 0 );
				var octant = Rent();
				var octantMixed = _space.Reach( c1, c1 + new Vector3( half ), octant );
				for ( var o2 = 0; o2 < 8; o2++ )
				{
					var bit = 1UL << (Level1[o1] + Level2[o2]);
					if ( !octantMixed || octant.Count <= 1 )
					{
						foreach ( var id in octant.Ids )
							cells[id] = cells.GetValueOrDefault( id ) | bit;
						continue;
					}

					var c2 = c1 + new Vector3( (o2 & 1) != 0 ? quarter : 0, (o2 & 2) != 0 ? quarter : 0, (o2 & 4) != 0 ? quarter : 0 );
					var cell = Rent();
					_space.Reach( c2, c2 + new Vector3( quarter ), cell );
					foreach ( var id in cell.Ids )
						cells[id] = cells.GetValueOrDefault( id ) | bit;
					Return( cell );
				}
				Return( octant );
			}
		}

		var leaf = ++_leaves;
		var first = _regions.Count;
		foreach ( var (cluster, mask) in cells )
			_regions.Add( (ulong)(uint)(cluster & 0x7FFF) | ((ulong)(uint)(leaf & 0xFFFFFF) << 16) | ((ulong)(uint)Mask( mask ) << 40) );

		var clusters = cells.Keys.ToList();
		var listed = clusters.Count > 0 ? (uint)List( clusters ) : 0xFFFFFFu;
		_nodes[index] = (1u | ((uint)first << 1), (uint)Math.Min( cells.Count, 255 ) | (listed << 8));
		return clusters;
	}

	int Mask( ulong mask )
	{
		if ( !_maskIds.TryGetValue( mask, out var id ) )
		{
			id = _masks.Count;
			_masks.Add( mask );
			_maskIds[mask] = id;
		}
		return id;
	}

	int List( List<int> clusters )
	{
		var key = string.Join( ',', clusters );
		if ( !_listIds.TryGetValue( key, out var id ) )
		{
			id = _lists.Count;
			_lists.Add( (_listed.Count, clusters.Count) );
			foreach ( var c in clusters )
				_listed.Add( (ushort)c );
			_listIds[key] = id;
		}
		return id;
	}
}

// The files of a map that only carries visibility: an empty world, its visibility and the manifest naming them.
public static class WorldVisibilityPackage
{
	// mapName is the map's path under maps/ without extension (halo/haloce_b30 for maps/halo/haloce_b30.vpk).
	public static byte[] Build( string mapName, byte[] visibility )
	{
		var folder = $"maps/{mapName}";
		var world = new Kv3Object
		{
			{ "m_builderParams", new Kv3Object
				{
					{ "m_flMinDrawVolumeSize", 256.0 },
					{ "m_bBuildBakedLighting", false },
					{ "m_vLightmapUvScale", new List<object> { 1.0, 1.0 } },
					{ "m_nCompileTimestamp", 0L },
					{ "m_nCompileFingerprint", 0UL },
				} },
			{ "m_worldNodes", new List<object>() },
			{ "m_worldLightingInfo", new Kv3Object
				{
					{ "m_nLightmapVersionNumber", 0 },
					{ "m_nLightmapGameVersionNumber", 0 },
					{ "m_vLightmapUvScale", new List<object> { 1.0, 1.0 } },
					{ "m_bHasLightmaps", false },
					{ "m_lightMaps", new List<object>() },
				} },
			{ "m_entityLumps", new List<object>() },
		};

		return VpkWriter.Write( new Dictionary<string, byte[]>
		{
			[$"{folder}/world.vwrld_c"] = CompiledResource.Write( 1, ("DATA", Kv3Writer.Write( world )) ),
			[$"{folder}/world_visibility.vvis_c"] = visibility,
			[$"{folder}/world.vrman_c"] = CompiledResource.Manifest( [$"{folder}/world.vwrld", $"{folder}/world_visibility.vvis"] ),
		} );
	}
}
