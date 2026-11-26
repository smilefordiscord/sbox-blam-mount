using System.Linq;
using System.Text.Json;

namespace Blam.Halo1.TagData;

using static Blam.Halo1.TagData.TagLayouts.Options;

// Damage: damage effects (instant and continuous) and what objects are made of (collision models).
public static partial class TagLayouts
{
	static readonly string[] DamageSideEffect = ["none", "harmless", "lethal to the unsuspecting", "emp"];

	static readonly string[] DamageCategory =
	[
		"none", "falling", "bullet", "grenade", "high explosive", "sniper", "melee", "flame", "mounted weapon",
		"vehicle", "plasma", "needle", "shotgun",
	];

	static readonly string[] DamageFlags =
	[
		"does not hurt owner", "can cause headshots", "pings resistant units", "does not hurt friends",
		"does not ping units", "detonates explosives", "only hurts shields", "causes flaming death",
		"damage indicators always point down", "skips shields", "only hurts one infection form",
		"can cause multiplayer headshots", "infection form pop", "ignore seat scale for dir dmg", "forces hard ping",
		"does not hurt players", "use 3d instantaneous acceleration", "allow any non-zero acceleration value",
	];

	/// <summary>
	/// The damage part shared by both damage effect tags, from the side effect on.
	/// </summary>
	static TagStruct DamageFields( TagStruct tag, int offset, bool instant )
	{
		tag.Enum( offset, "damage side effect", DamageSideEffect )
			.Enum( offset + 0x2, "damage category", DamageCategory )
			.Flags32( offset + 0x4, "damage flags", DamageFlags );

		// Continuous damage has no area of effect core or camouflage damage.
		if ( instant )
			tag.Float( offset + 0x8, "damage aoe core radius" );

		tag.Float( offset + 0xC, "damage lower bound" )
			.FloatBounds( offset + 0x10, "damage upper bound" )
			.Float( offset + 0x18, "damage vehicle passthrough penalty" );

		if ( instant )
			tag.Float( offset + 0x1C, "damage active camouflage damage" );

		return tag.Float( offset + 0x20, "damage stun" )
			.Float( offset + 0x24, "damage maximum stun" )
			.Float( offset + 0x28, "damage stun time" )
			.Point3( offset + 0x30, "damage instantaneous acceleration" );
	}

	/// <summary>
	/// How much of the damage each material takes: one float per <see cref="Options.MaterialTypeNames"/>,
	/// named after it.
	/// </summary>
	static TagStruct MaterialModifiers( TagStruct tag, int offset )
	{
		for ( var i = 0; i < MaterialTypeNames.Length; i++ )
			tag.Float( offset + i * 4, MaterialTypeNames[i] );

		return tag;
	}

	/// <summary>
	/// <c>jpt!</c>, 0x2A0 bytes.
	/// </summary>
	public static readonly TagStruct DamageEffect = MaterialModifiers( DamageFields( new TagStruct( "damage effect", 0x2A0 )
		.FloatBounds( 0x0, "radius" )
		.Float( 0x8, "cutoff scale" )
		.Flags32( 0xC, "flags", ["do not scale damage by distance"] )
		.Enum( 0x24, "type", ["none", "lighten", "darken", "max", "min", "invert", "tint"] )
		.Enum( 0x26, "priority", ["low", "medium", "high"] )
		.Float( 0x34, "duration" )
		.Enum( 0x38, "fade function", FunctionType )
		.Float( 0x44, "maximum intensity" )
		.ColorArgb( 0x4C, "color" )
		.Float( 0x5C, "low frequency vibrate frequency" )
		.Float( 0x60, "low frequency vibrate duration" )
		.Enum( 0x64, "low frequency vibrate fade function", FunctionType )
		.Float( 0x70, "high frequency vibrate frequency" )
		.Float( 0x74, "high frequency vibrate duration" )
		.Enum( 0x78, "high frequency vibrate fade function", FunctionType )
		.Float( 0x98, "temporary camera impulse duration" )
		.Enum( 0x9C, "temporary camera impulse fade function", FunctionType )
		.Angle( 0xA0, "temporary camera impulse rotation" )
		.Float( 0xA4, "temporary camera impulse pushback" )
		.FloatBounds( 0xA8, "jitter" )
		.Angle( 0xB8, "permanent camera impulse angle" )
		.Float( 0xCC, "camera shaking duration" )
		.Enum( 0xD0, "camera shaking falloff function", FunctionType )
		.Float( 0xD4, "camera shaking random translation" )
		.Angle( 0xD8, "camera shaking random rotation" )
		.Enum( 0xE8, "camera shaking wobble function", WaveFunction )
		.Float( 0xEC, "camera shaking wobble period" )
		.Float( 0xF0, "camera shaking wobble weight" )
		.Reference( 0x114, "sound" )
		.Float( 0x194, "breaking effect forward velocity" )
		.Float( 0x198, "breaking effect forward radius" )
		.Float( 0x19C, "breaking effect forward exponent" )
		.Float( 0x1AC, "breaking effect outward velocity" )
		.Float( 0x1B0, "breaking effect outward radius" )
		.Float( 0x1B4, "breaking effect outward exponent" ), 0x1C4, instant: true ), 0x200 );

	/// <summary>
	/// <c>cdmg</c>, 0x200 bytes: damage dealt every tick while inside its radius (looping sounds
	/// can carry one).
	/// </summary>
	public static readonly TagStruct ContinuousDamageEffect = MaterialModifiers( DamageFields( new TagStruct( "continuous damage effect", 0x200 )
		.FloatBounds( 0x0, "radius" )
		.Float( 0x8, "cutoff scale" )
		.Float( 0x24, "low frequency vibrate frequency" )
		.Float( 0x28, "high frequency vibrate frequency" )
		.Float( 0x44, "camera shaking random translation" )
		.Angle( 0x48, "camera shaking random rotation" )
		.Enum( 0x58, "camera shaking wobble function", WaveFunction )
		.Float( 0x5C, "camera shaking wobble period" )
		.Float( 0x60, "camera shaking wobble weight" ), 0x124, instant: false ), 0x160 );

	static readonly TagStruct CollisionMaterial = new TagStruct( "collision material", 0x48 )
		.String( 0x0, "name" )
		.Flags32( 0x20, "flags", ["head"] )
		.Enum( 0x24, "material type", MaterialTypeNames )
		.Float( 0x28, "shield leak percentage" )
		.Float( 0x2C, "shield damage multiplier" )
		.Float( 0x3C, "body damage multiplier" );

	static readonly TagStruct CollisionPermutation = new TagStruct( "collision permutation", 0x20 )
		.String( 0x0, "name" );

	static readonly TagStruct CollisionRegion = new TagStruct( "collision region", 0x54 )
		.String( 0x0, "name" )
		.Flags32( 0x20, "flags", ["lives until object dies", "forces object to die", "dies when object dies", "dies when object is damaged", "disappears when shield is off", "inhibits melee attack", "inhibits weapon attack", "inhibits walking", "forces drop weapon", "causes head maimed scream"] )
		.Float( 0x28, "damage threshold" )
		.Reference( 0x38, "destroyed effect" )
		.Block( 0x48, "permutations", CollisionPermutation );

	/// <summary>
	/// Collision modifiers have no fields in any tag definition (and no stock tag uses them).
	/// </summary>
	static readonly TagStruct CollisionModifier = new TagStruct( "collision modifier", 0x34 );

	static readonly TagStruct PathfindingSphere = new TagStruct( "collision pathfinding sphere", 0x20 )
		.Index( 0x0, "node" )
		.Point3( 0x10, "center" )
		.Float( 0x1C, "radius" );

	/// <summary>
	/// A collision node: collision node i is model node i, and its region is model region i. Its
	/// BSPs (one per permutation of the region) are written as their surfaces, for exact hit tests:
	/// <c>Bsps: [{ Bounds: [[x, y, z], [x, y, z]], Vertices, Surfaces, Convex, Polygons: [{ Material,
	/// Points: [x, y, z, ...] }] }]</c>, in node space (world units), each polygon's corners in order
	/// and its material an index into the model's materials (-1 for none). The runtime
	/// damage part (gut 0, chest, head, left arm, hand, leg, foot, right arm, hand, leg, foot) is what
	/// a hit on the node counts as, worked out when the map was built.
	/// </summary>
	static readonly TagStruct CollisionNode = new TagStruct( "collision node", 0x40 ) { Extra = WriteNodeBsps }
		.String( 0x0, "name" )
		.Index( 0x20, "region" )
		.Index( 0x22, "parent node" )
		.Index( 0x24, "next sibling node" )
		.Index( 0x26, "first child node" )
		.Index( 0x32, "runtime damage part" );

	static void WriteNodeBsps( Utf8JsonWriter json, LayoutJsonWriter writer, uint node )
	{
		json.WriteStartArray( "Bsps" );
		foreach ( var address in writer.BlockElements( node + 0x34, Tags.CollisionBsp.Size ) )
		{
			var bsp = new Tags.CollisionBsp( writer.Data, address );
			json.WriteStartObject();
			json.WriteStartArray( "Bounds" );
			if ( bsp.Vertices.Count > 0 )
			{
				foreach ( var corner in new[] { bsp.Vertices.Aggregate( System.Numerics.Vector3.Min ), bsp.Vertices.Aggregate( System.Numerics.Vector3.Max ) } )
				{
					json.WriteStartArray();
					json.WriteNumberValue( corner.X );
					json.WriteNumberValue( corner.Y );
					json.WriteNumberValue( corner.Z );
					json.WriteEndArray();
				}
			}
			json.WriteEndArray();
			json.WriteNumber( "Vertices", bsp.Vertices.Count );
			json.WriteNumber( "Surfaces", bsp.Surfaces.Count );
			json.WriteBoolean( "Convex", bsp.IsConvex() );
			json.WriteStartArray( "Polygons" );
			foreach ( var surface in bsp.Surfaces.Where( s => s.Corners.Length >= 3 ) )
			{
				json.WriteStartObject();
				json.WriteNumber( "Material", surface.Material );
				json.WriteStartArray( "Points" );
				foreach ( var corner in surface.Corners )
				{
					var point = bsp.Vertices[corner];
					json.WriteNumberValue( point.X );
					json.WriteNumberValue( point.Y );
					json.WriteNumberValue( point.Z );
				}
				json.WriteEndArray();
				json.WriteEndObject();
			}
			json.WriteEndArray();
			json.WriteEndObject();
		}
		json.WriteEndArray();
	}

	/// <summary>
	/// <c>coll</c>: an object's vitality, shields, the materials its parts are made of, its
	/// pathfinding box and spheres, and its nodes (with their BSPs' bounds; the BSPs themselves
	/// are left out). The shield recharge rate at 0x1C0 is per tick, worked out from the recharge
	/// time when the map is built.
	/// </summary>
	public static readonly TagStruct CollisionModel = new TagStruct( "collision model", 0x298 )
		.Flags32( 0x0, "flags", ["takes shield damage for children", "takes body damage for children", "always shields friendly damage", "passes area damage to children", "parent never takes body damage for us", "only damaged by explosives", "only damaged while occupied"] )
		.Index( 0x4, "indirect damage material" )
		.Float( 0x8, "maximum body vitality" )
		.Float( 0xC, "body system shock" )
		.Float( 0x44, "friendly damage resistance" )
		.Reference( 0x70, "localized damage effect" )
		.Float( 0x80, "area damage effect threshold" )
		.Reference( 0x84, "area damage effect" )
		.Float( 0x94, "body damaged threshold" )
		.Reference( 0x98, "body damaged effect" )
		.Reference( 0xA8, "body depleted effect" )
		.Float( 0xB8, "body destroyed threshold" )
		.Reference( 0xBC, "body destroyed effect" )
		.Float( 0xCC, "maximum shield vitality" )
		.Enum( 0xD2, "shield material type", MaterialTypeNames )
		.Enum( 0xEC, "shield failure function", FunctionType )
		.Float( 0xF0, "shield failure threshold" )
		.Float( 0xF4, "failing shield leak fraction" )
		.Float( 0x108, "minimum stun damage" )
		.Float( 0x10C, "stun time" )
		.Float( 0x110, "recharge time" )
		.Float( 0x184, "shield damaged threshold" )
		.Reference( 0x188, "shield damaged effect" )
		.Reference( 0x198, "shield depleted effect" )
		.Reference( 0x1A8, "shield recharging effect" )
		.Float( 0x1C0, "shield recharge rate" )
		.Block( 0x234, "materials", CollisionMaterial )
		.Block( 0x240, "regions", CollisionRegion )
		.Block( 0x24C, "modifiers", CollisionModifier )
		.FloatBounds( 0x268, "pathfinding box x" )
		.FloatBounds( 0x270, "pathfinding box y" )
		.FloatBounds( 0x278, "pathfinding box z" )
		.Block( 0x280, "pathfinding spheres", PathfindingSphere )
		.Block( 0x28C, "nodes", CollisionNode );
}
