using System.Collections.Generic;
using System.Linq;

namespace Blam.Halo2.Tags;

// Inside the namespace so these win over s&box's global vector types when this compiles into the mount.
using Quaternion = System.Numerics.Quaternion;
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;

/// <summary>
/// A node (bone) of a render model, with its default pose relative to its parent.
/// </summary>
public sealed record RenderModelNode( int Index, string Name, int Parent, Vector3 Translation, Quaternion Rotation );

/// <summary>
/// One permutation of a region: the section drawn at each level of detail, lowest first (L1 super low ...
/// L6 hollywood), -1 where there's none.
/// </summary>
public sealed record RenderModelPermutation( int Index, string Name, IReadOnlyList<int> Sections )
{
	/// <summary>
	/// The section of the highest level of detail the permutation has, or -1 if it has no geometry.
	/// </summary>
	public int HighestSection => HighestLevel >= 0 ? Sections[HighestLevel] : -1;

	/// <summary>
	/// The highest level of detail (0 super low ... 5 hollywood) the permutation has a section for, or -1.
	/// </summary>
	public int HighestLevel
	{
		get
		{
			for ( var i = Sections.Count - 1; i >= 0; i-- )
			{
				if ( Sections[i] >= 0 )
					return i;
			}

			return -1;
		}
	}
}

public sealed record RenderModelRegion( int Index, string Name, IReadOnlyList<RenderModelPermutation> Permutations );

/// <summary>
/// A marker: a named point on a node. Region and permutation restrict it to that geometry (-1 = any).
/// </summary>
public sealed record RenderModelMarker( string Name, int Region, int Permutation, int Node, Vector3 Translation, Quaternion Rotation, float Scale );

/// <summary>
/// A Halo 2 render model (<c>mode</c>): regions of permutations, each a geometry section per level of detail,
/// a node skeleton, markers and materials (shaders).
/// </summary>
/// <remarks>
/// Sections are read on demand (<see cref="GetSection"/>): most permutations have up to six of them and
/// only the highest level of detail is drawn. Their compressed positions and texture coordinates are
/// decompressed with the model's compression bounds.
/// </remarks>
public sealed class RenderModelTag
{
	public static readonly TagClass Class = new( "mode" );

	const uint CompressionBlock = 0x14;
	const uint RegionsBlock = 0x1C;
	const uint SectionsBlock = 0x24;
	const uint NodesBlock = 0x48;
	const uint MarkerGroupsBlock = 0x58;
	const uint MaterialsBlock = 0x60;
	const uint PrtBlock = 0x74;
	const int CompressionSize = 0x38;
	const int RegionSize = 0x10;
	const int PermutationSize = 0x10;
	const int SectionSize = 0x5C;
	const int NodeSize = 0x60;
	const int MarkerGroupSize = 0xC;
	const int MarkerSize = 0x24;
	const int MaterialSize = 0x20;

	readonly uint[] _sectionAddresses;
	readonly GeometrySection[] _sections;
	readonly uint _prtAddress;
	RenderModelPrt _prt;
	bool _prtRead;

	public TagEntry Tag { get; }
	public string Name { get; }
	public IReadOnlyList<RenderModelRegion> Regions { get; }
	public IReadOnlyList<RenderModelNode> Nodes { get; }
	public IReadOnlyList<RenderModelMarker> Markers { get; }

	/// <summary>
	/// The shader of each material, which geometry parts index (null where a material has none).
	/// </summary>
	public IReadOnlyList<TagEntry> Materials { get; }

	/// <summary>
	/// The bounds compressed vertices are scaled into, or null if the model has none.
	/// </summary>
	public GeometryCompression Compression { get; }

	/// <summary>
	/// The node list checksum (+0x44). Halo only skins a model with another's node matrices when both have the same
	/// one and as many nodes (0 in every MCC map).
	/// </summary>
	public int NodeListChecksum { get; }

	/// <summary>
	/// A first person model's "don't draw over camera cosine angle" (+0x70): Halo only draws it while the camera's
	/// forward vector points further down than this (its z below it); 0 draws it always.
	/// </summary>
	public float DontDrawOverCameraCosine { get; }

	public int SectionCount => _sectionAddresses.Length;

	/// <summary>
	/// Whether the model has PRT data (what its <c>prt_*</c> shaders light with).
	/// </summary>
	public bool HasPrt => _prtAddress != 0;

	/// <summary>
	/// The model's PRT data, read on first use, or null.
	/// </summary>
	public RenderModelPrt Prt
	{
		get
		{
			lock ( _sections )
			{
				if ( !_prtRead )
				{
					_prt = _prtAddress == 0 ? null : RenderModelPrt.Read( Tag.Cache, _prtAddress );
					_prtRead = true;
				}

				return _prt;
			}
		}
	}

	public RenderModelTag( TagEntry tag )
	{
		Tag = tag;
		var cache = tag.Cache;
		var data = cache.TagData;
		var a = tag.Address;

		Name = cache.GetString( data.U32( a ) );
		NodeListChecksum = data.I32( a + 0x44 );
		DontDrawOverCameraCosine = data.F32( a + 0x70 );

		// x, y, z, u, v (then the secondary u, v) as min, max pairs.
		var compression = data.Elements( a + CompressionBlock, CompressionSize ).FirstOrDefault();
		if ( compression != 0 )
		{
			float F( int i ) => data.F32( compression + (uint)(i * 4) );
			Compression = new GeometryCompression(
				new Vector3( F( 0 ), F( 2 ), F( 4 ) ), new Vector3( F( 1 ), F( 3 ), F( 5 ) ),
				new Vector2( F( 6 ), F( 8 ) ), new Vector2( F( 7 ), F( 9 ) ) );
		}

		var regionIndex = 0;
		Regions = data.Elements( a + RegionsBlock, RegionSize ).Select( r =>
		{
			var permutationIndex = 0;
			var permutations = data.Elements( r + 8, PermutationSize ).Select( p => new RenderModelPermutation(
				permutationIndex++,
				cache.GetString( data.U32( p ) ),
				Enumerable.Range( 0, 6 ).Select( i => (int)data.I16( p + 4 + (uint)(i * 2) ) ).ToArray() ) ).ToList();
			return new RenderModelRegion( regionIndex++, cache.GetString( data.U32( r ) ), permutations );
		} ).ToList();

		_sectionAddresses = data.Elements( a + SectionsBlock, SectionSize ).ToArray();
		_prtAddress = data.Elements( a + PrtBlock, 0x58 ).FirstOrDefault();
		_sections = new GeometrySection[_sectionAddresses.Length];

		var nodeIndex = 0;
		Nodes = data.Elements( a + NodesBlock, NodeSize ).Select( n => new RenderModelNode(
			nodeIndex++,
			cache.GetString( data.U32( n ) ),
			data.I16( n + 4 ),
			data.Vec3( n + 0xC ),
			new Quaternion( data.F32( n + 0x18 ), data.F32( n + 0x1C ), data.F32( n + 0x20 ), data.F32( n + 0x24 ) ) ) ).ToList();

		var markers = new List<RenderModelMarker>();
		foreach ( var group in data.Elements( a + MarkerGroupsBlock, MarkerGroupSize ) )
		{
			var name = cache.GetString( data.U32( group ) );
			foreach ( var m in data.Elements( group + 4, MarkerSize ) )
			{
				markers.Add( new RenderModelMarker( name,
					(sbyte)data.U8( m ), (sbyte)data.U8( m + 1 ), (sbyte)data.U8( m + 2 ),
					data.Vec3( m + 4 ),
					new Quaternion( data.F32( m + 0x10 ), data.F32( m + 0x14 ), data.F32( m + 0x18 ), data.F32( m + 0x1C ) ),
					data.F32( m + 0x20 ) ) );
			}
		}

		Markers = markers;

		// The shader is at +8; +0 is an older reference that's null in MCC's maps.
		Materials = data.Elements( a + MaterialsBlock, MaterialSize )
			.Select( m => cache.GetTagReference( m + 8 ) ?? cache.GetTagReference( m ) )
			.ToList();
	}

	/// <summary>
	/// A geometry section, decompressed, or null for an index out of range.
	/// </summary>
	public GeometrySection GetSection( int index )
	{
		if ( index < 0 || index >= _sections.Length )
			return null;

		lock ( _sections )
			return _sections[index] ??= GeometrySection.Read( Tag.Cache, _sectionAddresses[index], renderModel: true, Compression );
	}

	/// <summary>
	/// The first marker named <paramref name="name"/> in model space (default pose), or null.
	/// </summary>
	public (Quaternion Rotation, Vector3 Translation)? FindMarker( string name )
	{
		var marker = Markers.FirstOrDefault( m => string.Equals( m.Name, name, System.StringComparison.OrdinalIgnoreCase ) );
		if ( marker is null )
			return null;

		var pose = DefaultPose();
		if ( marker.Node < 0 || marker.Node >= pose.Length )
			return (marker.Rotation, marker.Translation);

		var (rotation, translation) = pose[marker.Node];
		return (Quaternion.Normalize( rotation * marker.Rotation ), translation + Vector3.Transform( marker.Translation, rotation ));
	}

	/// <summary>
	/// Each node's default pose in model space: <c>world = parent world ∘ (rotation, translation)</c>. Nodes
	/// come after their parents. Halo stores the inverse of these too, and they match exactly.
	/// </summary>
	public (Quaternion Rotation, Vector3 Translation)[] DefaultPose()
	{
		var world = new (Quaternion, Vector3)[Nodes.Count];
		for ( var i = 0; i < world.Length; i++ )
		{
			var node = Nodes[i];
			if ( node.Parent < 0 || node.Parent >= i )
			{
				world[i] = (node.Rotation, node.Translation);
				continue;
			}

			var (parentRotation, parentTranslation) = world[node.Parent];
			world[i] = (Quaternion.Normalize( parentRotation * node.Rotation ), parentTranslation + Vector3.Transform( node.Translation, parentRotation ));
		}

		return world;
	}
}
