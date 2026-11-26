using System;
using System.Linq;
using System.Collections.Generic;

namespace Blam.Halo3.Tags;

// Inside the namespace so these win over s&box's global Vector types when this compiles into the mount.
using Vector3 = System.Numerics.Vector3;

// A baked environment cube map (its position and image in the scenario's cubemaps bitmap for the BSP, ScenarioStructureBsp.Cubemaps): what the dynamic environment_mapping option reflects.
public sealed record StructureCubemap( Vector3 Position, int Index );

// A cluster: its first cube map is the one its own geometry reflects; the rest are the ones objects in it can pick from.
public sealed record StructureCluster( int Index, int Mesh, Vector3 Min, Vector3 Max, int Sky, IReadOnlyList<StructureCubemap> Cubemaps )
{
	public bool Contains( Vector3 p ) => p.X >= Min.X && p.X <= Max.X && p.Y >= Min.Y && p.Y <= Max.Y && p.Z >= Min.Z && p.Z <= Max.Z;
}

// A placement of an instanced geometry definition: world = position + scale * (x * forward + y * left + z * up).
public sealed record StructureInstance( int Index, float Scale, Vector3 Forward, Vector3 Left, Vector3 Up, Vector3 Position, int Definition, int Flags,
	int LightmapTexCoords, string Name, int LightmappingPolicy, Vector3 BoundingCenter, float BoundingRadius, IReadOnlyList<StructureCubemap> Cubemaps )
{
	// Flags: not in lightprobes 1, render only 2, doesn't block AOE 4, collidable 8, decal spacing 0x10, not for render 0x20.
	public bool RenderOnly => (Flags & 2) != 0;
	public bool NotForRender => (Flags & 0x20) != 0;
	public bool IsMirrored => Vector3.Dot( Vector3.Cross( Forward, Left ), Up ) < 0;

	public Vector3 Transform( Vector3 p ) => Position + Scale * (p.X * Forward + p.Y * Left + p.Z * Up);
	public Vector3 TransformDirection( Vector3 d ) => d.X * Forward + d.Y * Left + d.Z * Up;
}

// An instanced geometry definition (0xC4, in the BSP's tag resources): its mesh in the lightmap BSP's geometry, its compression bounds, its own collision.
public sealed record InstancedGeometryDefinition( int Index, int Mesh, int Compression, Vector3 BoundingOffset, float BoundingRadius, CollisionBsp Collision );

public sealed record StructureCollisionMaterial( TagEntry Shader, int GlobalMaterial );

// A scenario_structure_bsp (sbsp) tag (geometry.md §2). Its own render geometry resource is dead: the vertices and indices are in the matching Lbsp's.
public sealed class StructureBspTag
{
	public TagEntry Tag { get; }
	public uint Checksum { get; }
	public Vector3 WorldMin { get; }
	public Vector3 WorldMax { get; }
	public IReadOnlyList<StructureCollisionMaterial> CollisionMaterials { get; }
	public IReadOnlyList<StructureCluster> Clusters { get; }
	public IReadOnlyList<TagEntry> Materials { get; }
	public IReadOnlyList<StructureInstance> Instances { get; }

	// Each collision BSP leaf's cluster (+0x34, a signed byte each), -1 for none.
	public IReadOnlyList<int> LeafClusters { get; }

	// The openings between clusters (+0x7C, 0x28 each: back and front cluster, plane index, centroid, radius, flags, vertices at +0x1C).
	public IReadOnlyList<(int Back, int Front, Vector3[] Points)> Portals { get; }

	public StructureBspTag( TagEntry tag )
	{
		Tag = tag;
		var d = tag.Data;
		Checksum = d.U32( 0 );
		WorldMin = new Vector3( d.F32( 0x40 ), d.F32( 0x48 ), d.F32( 0x50 ) );
		WorldMax = new Vector3( d.F32( 0x44 ), d.F32( 0x4C ), d.F32( 0x54 ) );

		var collisionMaterials = new List<StructureCollisionMaterial>();
		foreach ( var m in d.Block( 0x28, 0x18 ) )
			collisionMaterials.Add( new StructureCollisionMaterial( m.Reference( 0 ), m.I16( 0x10 ) ) );
		CollisionMaterials = collisionMaterials;

		var clusters = new List<StructureCluster>();
		foreach ( var c in d.Block( 0xC4, 0x118 ) )
			clusters.Add( new StructureCluster( clusters.Count, c.I16( 0xD8 ), new Vector3( c.F32( 0 ), c.F32( 8 ), c.F32( 0x10 ) ), new Vector3( c.F32( 4 ), c.F32( 0xC ), c.F32( 0x14 ) ), c.U8( 0x18 ), Cubemaps( c, 0x10C ) ) );
		Clusters = clusters;

		LeafClusters = d.Block( 0x34, 1 ).Select( l => (int)l.I8( 0 ) ).ToList();
		Portals = d.Block( 0x7C, 0x28 ).Select( p => ((int)p.I16( 0 ), (int)p.I16( 2 ), p.Block( 0x1C, 0xC ).Select( v => v.Vec3( 0 ) ).ToArray()) ).ToList();

		var materials = new List<TagEntry>();
		foreach ( var m in d.Block( 0xD0, 0x24 ) )
			materials.Add( m.Reference( 0 ) );
		Materials = materials;

		var instances = new List<StructureInstance>();
		foreach ( var i in d.Block( 0x1C0, 0x78 ) )
		{
			instances.Add( new StructureInstance( instances.Count, i.F32( 0 ), i.Vec3( 4 ), i.Vec3( 0x10 ), i.Vec3( 0x1C ), i.Vec3( 0x28 ), i.I16( 0x34 ), i.U16( 0x36 ),
				i.I16( 0x38 ), i.StringId( 0x54 ) ?? "", i.I16( 0x5A ), i.Vec3( 0x40 ), i.F32( 0x4C ), Cubemaps( i, 0x70 ) ) );
		}
		Instances = instances;
	}

	// A cube map block (0x10: position, i16 image index twice).
	static IReadOnlyList<StructureCubemap> Cubemaps( TagView view, int field )
	{
		var block = view.Block( field, 0x10 );
		if ( block.Count == 0 )
			return Array.Empty<StructureCubemap>();
		var cubemaps = new StructureCubemap[block.Count];
		for ( var i = 0; i < block.Count; i++ )
			cubemaps[i] = new StructureCubemap( block[i].Vec3( 0 ), block[i].I16( 0xC ) );
		return cubemaps;
	}

	// The cluster whose bounds hold a point, if any.
	public StructureCluster ClusterAt( Vector3 p )
	{
		foreach ( var cluster in Clusters )
		{
			if ( cluster.Contains( p ) )
				return cluster;
		}
		return null;
	}

	public RenderGeometry Geometry => new( Tag.Data, RenderGeometry.StructureBspOffset );

	// The structure_bsp_tag_resources resource handle: collision BSP and instanced geometry definitions.
	public uint TagResourcesHandle => Tag.Data.U32( 0x38C );

	// The collision BSP and instanced geometry definitions, from the tag resources; null when they're stripped (010_jungle's 010_bsp_intro).
	public StructureBspResources LoadResources()
	{
		var data = Tag.Cache.Resources.Read( TagResourcesHandle );
		if ( data is null || !data.HasPrimary )
			return null;

		// Root: collision BSPs (0x60), large collision BSPs, instanced geometry definitions (0xC4), havok data.
		var root = data.Root;
		CollisionBsp collision = null;
		foreach ( var c in data.Block( root, CollisionBsp.Size ) )
		{
			collision = CollisionBsp.FromResource( data, c );
			break;
		}

		var definitions = new List<InstancedGeometryDefinition>();
		foreach ( var o in data.Block( root + 0x18, 0xC4 ) )
		{
			definitions.Add( new InstancedGeometryDefinition( definitions.Count, data.I16( o + 0xBC ), data.I16( o + 0xBE ),
				new Vector3( data.F32( o + 4 ), data.F32( o + 8 ), data.F32( o + 0xC ) ), data.F32( o + 0x10 ), CollisionBsp.FromResource( data, o + 0x14 ) ) );
		}

		return new StructureBspResources( collision, definitions );
	}
}

public sealed record StructureBspResources( CollisionBsp Collision, IReadOnlyList<InstancedGeometryDefinition> Definitions );
