using System;
using System.Collections.Generic;
using System.Linq;

namespace Blam.Halo1.Tags;

// Inside the namespace so these win over s&box's global types when this compiles into the mount.
using Vector3 = System.Numerics.Vector3;

/// <summary>
/// A damage material of a collision model: what a part of the object is made of, and how much
/// damage hitting it does.
/// </summary>
/// <param name="Flags">Head 1 (hits here count as headshots).</param>
/// <param name="MaterialType">Index into <see cref="MaterialTypes.Names"/>, -1 for none.</param>
public sealed record CollisionMaterial( string Name, uint Flags, int MaterialType, float ShieldLeakFraction, float ShieldDamageMultiplier, float BodyDamageMultiplier )
{
	public bool IsHead => (Flags & 1) != 0;
	public string MaterialTypeName => MaterialTypes.Name( MaterialType );
}

/// <summary>
/// A damage region of a collision model. Regions match the model's regions by index, and each
/// region's permutations pick the BSP of the collision nodes in it.
/// </summary>
/// <param name="Flags">Lives until object dies 1, forces object to die 2, dies when object dies 4,
/// dies when object is damaged 8, disappears when shield is off 0x10, inhibits melee attack 0x20,
/// inhibits weapon attack 0x40, inhibits walking 0x80, forces drop weapon 0x100, causes head-maimed
/// scream 0x200.</param>
/// <param name="DamageThreshold">The fraction of body damage the region takes before it's destroyed (0 for never).</param>
public sealed record CollisionRegion( string Name, uint Flags, float DamageThreshold, TagEntry DestroyedEffect, IReadOnlyList<string> Permutations );

/// <summary>
/// A sphere AI pathfinding steers around, on a node.
/// </summary>
public sealed record PathfindingSphere( int Node, Vector3 Center, float Radius );

/// <summary>
/// A node of a collision model: the collision geometry that moves with one of the model's nodes
/// (same index), one BSP per permutation of its region, in the node's space.
/// </summary>
/// <param name="Region">Index into <see cref="CollisionModelTag.Regions"/>, -1 for none (no collision).</param>
public sealed record CollisionNode( string Name, int Region, int Parent, int NextSibling, int FirstChild, IReadOnlyList<CollisionBsp> Bsps );

/// <summary>
/// Reader for <c>coll</c> (model collision geometry) tags: an object's damage settings and the
/// collision geometry projectiles and other objects hit.
/// </summary>
/// <remarks>
/// Offsets: flags 0x0, indirect damage material 0x4, maximum body vitality 0x8, body system shock
/// 0xC, friendly damage resistance 0x44, localized damage effect 0x70, area damage effect
/// threshold 0x80 and effect 0x84, body damaged threshold 0x94 and effect 0x98, body depleted
/// effect 0xA8, body destroyed threshold 0xB8 and effect 0xBC, maximum shield vitality 0xCC,
/// shield material type 0xD2, shield failure function 0xEC, threshold 0xF0, maximum 0xF4, minimum
/// stun damage 0x108, stun time 0x10C, recharge time 0x110, shield damaged threshold 0x184 and
/// effects 0x188 (damaged), 0x198 (depleted), 0x1A8 (recharging).
///
/// Blocks: materials 0x234 (72 bytes: name, flags 0x20, material type 0x24, shield leak fraction
/// 0x28, shield damage multiplier 0x2C, body damage multiplier 0x3C), regions 0x240 (84 bytes:
/// name, flags 0x20, damage threshold 0x28, destroyed effect 0x38, permutations 0x48 of 32 byte
/// names), modifiers 0x24C (52 bytes, unused by Halo CE), pathfinding box 0x268 (x, y, z ranges),
/// pathfinding spheres 0x280 (32 bytes: node 0x0, center 0x10, radius 0x1C), nodes 0x28C (64
/// bytes: name, region 0x20, parent 0x22, next sibling 0x24, first child 0x26, BSPs 0x34, see
/// <see cref="CollisionBsp"/>).
///
/// Halo tests each node's BSP for the permutation its region shows (clamped to the BSPs there
/// are) in the space of the object's node with the same index.
/// </remarks>
public sealed class CollisionModelTag
{
	public TagEntry Tag { get; }
	public uint Flags { get; }
	public int IndirectDamageMaterial { get; }
	public float MaximumBodyVitality { get; }
	public float BodySystemShock { get; }
	public float FriendlyDamageResistance { get; }
	public float AreaDamageEffectThreshold { get; }
	public float BodyDamagedThreshold { get; }
	public float BodyDestroyedThreshold { get; }
	public float MaximumShieldVitality { get; }
	public int ShieldMaterialType { get; }
	public float ShieldFailureThreshold { get; }
	public float MaximumShieldFailure { get; }
	public float MinimumShieldStunDamage { get; }
	public float ShieldStunTime { get; }
	public float ShieldRechargeTime { get; }
	public float ShieldDamagedThreshold { get; }

	public TagEntry LocalizedDamageEffect { get; }
	public TagEntry AreaDamageEffect { get; }
	public TagEntry BodyDamagedEffect { get; }
	public TagEntry BodyDepletedEffect { get; }
	public TagEntry BodyDestroyedEffect { get; }
	public TagEntry ShieldDamagedEffect { get; }
	public TagEntry ShieldDepletedEffect { get; }
	public TagEntry ShieldRechargingEffect { get; }

	public IReadOnlyList<CollisionMaterial> Materials { get; }
	public IReadOnlyList<CollisionRegion> Regions { get; }
	public int ModifierCount { get; }
	public Vector3 PathfindingBoxMin { get; }
	public Vector3 PathfindingBoxMax { get; }
	public IReadOnlyList<PathfindingSphere> PathfindingSpheres { get; }
	public IReadOnlyList<CollisionNode> Nodes { get; }

	public static readonly TagClass Class = new( "coll" );

	public CollisionModelTag( CacheFile cache, TagEntry tag )
	{
		if ( tag.Class != Class )
			throw new ArgumentException( $"{tag} is not a collision model tag", nameof( tag ) );

		Tag = tag;
		var data = cache.TagData;
		var a = tag.Address;

		Flags = data.U32( a );
		IndirectDamageMaterial = data.I16( a + 0x4 );
		MaximumBodyVitality = data.F32( a + 0x8 );
		BodySystemShock = data.F32( a + 0xC );
		FriendlyDamageResistance = data.F32( a + 0x44 );
		LocalizedDamageEffect = cache.GetTagReference( data, a + 0x70 );
		AreaDamageEffectThreshold = data.F32( a + 0x80 );
		AreaDamageEffect = cache.GetTagReference( data, a + 0x84 );
		BodyDamagedThreshold = data.F32( a + 0x94 );
		BodyDamagedEffect = cache.GetTagReference( data, a + 0x98 );
		BodyDepletedEffect = cache.GetTagReference( data, a + 0xA8 );
		BodyDestroyedThreshold = data.F32( a + 0xB8 );
		BodyDestroyedEffect = cache.GetTagReference( data, a + 0xBC );
		MaximumShieldVitality = data.F32( a + 0xCC );
		ShieldMaterialType = data.I16( a + 0xD2 );
		ShieldFailureThreshold = data.F32( a + 0xF0 );
		MaximumShieldFailure = data.F32( a + 0xF4 );
		MinimumShieldStunDamage = data.F32( a + 0x108 );
		ShieldStunTime = data.F32( a + 0x10C );
		ShieldRechargeTime = data.F32( a + 0x110 );
		ShieldDamagedThreshold = data.F32( a + 0x184 );
		ShieldDamagedEffect = cache.GetTagReference( data, a + 0x188 );
		ShieldDepletedEffect = cache.GetTagReference( data, a + 0x198 );
		ShieldRechargingEffect = cache.GetTagReference( data, a + 0x1A8 );

		Materials = data.Block( a + 0x234, 0x48 ).Select( m => new CollisionMaterial(
			data.CString( m, 32 ), data.U32( m + 0x20 ), data.I16( m + 0x24 ),
			data.F32( m + 0x28 ), data.F32( m + 0x2C ), data.F32( m + 0x3C ) ) ).ToList();

		Regions = data.Block( a + 0x240, 0x54 ).Select( r => new CollisionRegion(
			data.CString( r, 32 ), data.U32( r + 0x20 ), data.F32( r + 0x28 ), cache.GetTagReference( data, r + 0x38 ),
			data.Block( r + 0x48, 0x20 ).Select( p => data.CString( p, 32 ) ).ToList() ) ).ToList();

		ModifierCount = data.I32( a + 0x24C );
		PathfindingBoxMin = new Vector3( data.F32( a + 0x268 ), data.F32( a + 0x270 ), data.F32( a + 0x278 ) );
		PathfindingBoxMax = new Vector3( data.F32( a + 0x26C ), data.F32( a + 0x274 ), data.F32( a + 0x27C ) );

		PathfindingSpheres = data.Block( a + 0x280, 0x20 ).Select( s => new PathfindingSphere(
			data.I16( s ), data.Vec3( s + 0x10 ), data.F32( s + 0x1C ) ) ).ToList();

		Nodes = data.Block( a + 0x28C, 0x40 ).Select( n => new CollisionNode(
			data.CString( n, 32 ), data.I16( n + 0x20 ), data.I16( n + 0x22 ), data.I16( n + 0x24 ), data.I16( n + 0x26 ),
			data.Block( n + 0x34, CollisionBsp.Size ).Select( b => new CollisionBsp( data, b ) ).ToList() ) ).ToList();
	}

	/// <summary>
	/// The BSP Halo tests for a node when its region shows a permutation (the base permutation is 0),
	/// or null if the node has none.
	/// </summary>
	public CollisionBsp NodeBsp( int node, int permutation = 0 )
	{
		var bsps = Nodes[node].Bsps;
		if ( Nodes[node].Region < 0 || bsps.Count == 0 )
			return null;

		return bsps[Math.Clamp( permutation, 0, bsps.Count - 1 )];
	}

	// The BSP Halo tests for a node of an object showing these region permutations (by region index, missing ones 0), or null for none.
	public CollisionBsp NodeBsp( int node, IReadOnlyList<int> regionPermutations ) =>
		NodeBspIndex( node, regionPermutations ) is var index and >= 0 ? Nodes[node].Bsps[index] : null;

	// The index of that BSP in the node's BSPs, -1 for none (no region, no BSPs, or a permutation of -1 as collision_models.c skips).
	public int NodeBspIndex( int node, IReadOnlyList<int> regionPermutations )
	{
		var region = Nodes[node].Region;
		var count = Nodes[node].Bsps.Count;
		if ( region < 0 || count == 0 )
			return -1;

		var permutation = regionPermutations is not null && region < regionPermutations.Count ? regionPermutations[region] : 0;
		return permutation < 0 ? -1 : Math.Clamp( permutation, 0, count - 1 );
	}

	/// <summary>
	/// The material type of a material index, -1 for none.
	/// </summary>
	public int MaterialTypeOf( int material ) =>
		material >= 0 && material < Materials.Count && MaterialTypes.IsValid( Materials[material].MaterialType ) ? Materials[material].MaterialType : -1;

	/// <summary>
	/// A node BSP as convex pieces (see <see cref="CollisionBsp.ConvexPieces"/>), each with the
	/// material type it's made of. A BSP of one material type keeps its pieces; one mixing types is
	/// split into its solid cells (even if it's convex), each given the type with the most area on
	/// its boundary (see <see cref="CollisionBsp.DominantMaterial"/>).
	/// </summary>
	/// <param name="maximumHalfEdges">The most half-edges a piece may have (see <see cref="CollisionBsp.SolidCells"/>).</param>
	public List<(int MaterialType, Vector3[] Points)> TypedPieces( CollisionBsp bsp, int maximumHalfEdges = int.MaxValue )
	{
		var types = bsp.Surfaces.Select( s => MaterialTypeOf( s.Material ) ).Distinct().ToList();
		if ( types.Count <= 1 )
		{
			var type = types.Count == 1 ? types[0] : -1;
			return bsp.ConvexPieces( maximumHalfEdges ).Select( p => (type, p) ).ToList();
		}

		var pieces = bsp.SolidCells( maximumHalfEdges );
		if ( pieces.Count == 0 )
			pieces = bsp.ConvexPieces( maximumHalfEdges );
		return pieces.Select( p => (bsp.DominantMaterial( p, MaterialTypeOf ), p) ).ToList();
	}

	/// <summary>
	/// The material of a surface, or null if it has none.
	/// </summary>
	public CollisionMaterial Material( CollisionBspSurface surface ) =>
		surface.Material >= 0 && surface.Material < Materials.Count ? Materials[surface.Material] : null;
}
