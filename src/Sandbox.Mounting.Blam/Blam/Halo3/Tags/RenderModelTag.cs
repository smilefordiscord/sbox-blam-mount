using System.Collections.Generic;

namespace Blam.Halo3.Tags;

// Inside the namespace so these win over s&box's global Vector types when this compiles into the mount.
using Quaternion = System.Numerics.Quaternion;
using Vector3 = System.Numerics.Vector3;

// A permutation draws the meshes [Mesh, Mesh + MeshCount) of the render geometry.
public sealed record RenderModelPermutation( int Index, string Name, int Mesh, int MeshCount );

public sealed record RenderModelRegion( int Index, string Name, IReadOnlyList<RenderModelPermutation> Permutations );

public sealed record RenderModelNode( int Index, string Name, int Parent, int FirstChild, int NextSibling, Vector3 Translation, Quaternion Rotation );

// A marker on a node; region / permutation -1 = any.
public sealed record RenderModelMarker( string Name, int Region, int Permutation, int Node, Vector3 Translation, Quaternion Rotation, float Scale );

// A render_model (mode) tag (geometry.md §3).
public sealed class RenderModelTag
{
	public TagEntry Tag { get; }
	public string Name { get; }
	public IReadOnlyList<RenderModelRegion> Regions { get; }
	public IReadOnlyList<RenderModelNode> Nodes { get; }
	public IReadOnlyList<RenderModelMarker> Markers { get; }

	// Render methods (rmsh, rmtr...) by part material index; MCC's extra *_pbr ones at the end are never used by a part.
	public IReadOnlyList<TagEntry> Materials { get; }

	// The model's own L2 SH lighting (+0xF4, +0x134, +0x174: 9 floats per channel), what Halo 3 lights an object with when there is no lightmap under it, from the current sky's model.
	public LightProbe Lighting { get; }

	public RenderModelTag( TagEntry tag )
	{
		Tag = tag;
		var d = tag.Data;
		Name = d.StringId( 0 ) ?? "";

		var regions = new List<RenderModelRegion>();
		foreach ( var r in d.Block( 0x0C, 0x10 ) )
		{
			var permutations = new List<RenderModelPermutation>();
			foreach ( var p in r.Block( 4, 0x18 ) )
				permutations.Add( new RenderModelPermutation( permutations.Count, p.StringId( 0 ) ?? "", p.I16( 4 ), p.I16( 6 ) ) );
			regions.Add( new RenderModelRegion( regions.Count, r.StringId( 0 ) ?? "", permutations ) );
		}
		Regions = regions;

		var nodes = new List<RenderModelNode>();
		foreach ( var n in d.Block( 0x30, 0x60 ) )
		{
			nodes.Add( new RenderModelNode( nodes.Count, n.StringId( 0 ) ?? "", n.I16( 4 ), n.I16( 6 ), n.I16( 8 ), n.Vec3( 0xC ),
				new Quaternion( n.F32( 0x18 ), n.F32( 0x1C ), n.F32( 0x20 ), n.F32( 0x24 ) ) ) );
		}
		Nodes = nodes;

		var markers = new List<RenderModelMarker>();
		foreach ( var g in d.Block( 0x3C, 0x10 ) )
		{
			var name = g.StringId( 0 ) ?? "";
			foreach ( var m in g.Block( 4, 0x24 ) )
			{
				markers.Add( new RenderModelMarker( name, m.I8( 0 ), m.I8( 1 ), m.I8( 2 ), m.Vec3( 4 ),
					new Quaternion( m.F32( 0x10 ), m.F32( 0x14 ), m.F32( 0x18 ), m.F32( 0x1C ) ), m.F32( 0x20 ) ) );
			}
		}
		Markers = markers;

		var materials = new List<TagEntry>();
		foreach ( var m in d.Block( 0x48, 0x24 ) )
			materials.Add( m.Reference( 0 ) );
		Materials = materials;

		var sh = new Vector3[9];
		for ( var k = 0; k < 9; k++ )
			sh[k] = new Vector3( d.F32( 0xF4 + 4 * k ), d.F32( 0x134 + 4 * k ), d.F32( 0x174 + 4 * k ) );
		Lighting = LightProbe.FromCoefficients( sh );
	}

	public RenderGeometry Geometry() => new( Tag.Data, RenderGeometry.RenderModelOffset );

	// The nodes' default pose in model space: each node's rotation and translation chained through its parents.
	public (Vector3 Translation, Quaternion Rotation)[] DefaultPose()
	{
		var pose = new (Vector3, Quaternion)[Nodes.Count];
		for ( var i = 0; i < Nodes.Count; i++ )
		{
			var node = Nodes[i];
			var rotation = Quaternion.Normalize( node.Rotation );
			if ( node.Parent >= 0 && node.Parent < i )
			{
				var (pt, pr) = pose[node.Parent];
				pose[i] = (pt + Vector3.Transform( node.Translation, pr ), Quaternion.Normalize( pr * rotation ));
			}
			else
				pose[i] = (node.Translation, rotation);
		}
		return pose;
	}
}
