namespace Blam.Halo2.TagData;

using static Blam.Halo2.TagData.TagLayouts.Options;

// Damage: damage effects, collision models, and the model tag (hlmt), which holds an object's vitality and targets.
public static partial class TagLayouts
{
	static readonly TagStruct DamageEffectPlayerResponse = new TagStruct( "damage effect player response", 0x4C )
		.Enum( 0x0, "response type", ["shielded", "unshielded", "all"] )
		.Enum( 0x4, "screen flash type", ["none", "lighten", "darken", "max", "min", "invert", "tint"] )
		.Enum( 0x6, "screen flash priority", ["low", "medium", "high"] )
		.Float( 0x8, "screen flash duration" )
		.Enum( 0xC, "screen flash fade function", TransitionFunction )
		.Float( 0x10, "screen flash maximum intensity" )
		.ColorArgb( 0x14, "screen flash color" )
		.Float( 0x24, "low frequency vibration duration" )
		.ByteData( 0x28, "low frequency vibration function" )
		.Float( 0x30, "high frequency vibration duration" )
		.ByteData( 0x34, "high frequency vibration function" )
		.StringId( 0x3C, "sound effect name" )
		.Float( 0x40, "sound effect duration" )
		.ByteData( 0x44, "sound effect scale function" );

	public static readonly TagStruct DamageEffect = new TagStruct( "damage effect", 0xC8 )
		.FloatBounds( 0x0, "radius" )
		.Float( 0x8, "cutoff scale" )
		.Flags32( 0xC, "flags", ["don't scale damage by distance", "area damage players only"] )
		.Enum( 0x10, "side effect", ["none", "harmless", "lethal to the unsuspecting", "emp"] )
		.Enum( 0x12, "category", ["none", "falling", "bullet", "grenade", "high explosive", "sniper", "melee", "flame", "mounted weapon", "vehicle", "plasma", "needle", "shotgun"] )
		.Flags32( 0x14, "damage flags", ["does not hurt owner", "can cause headshots", "pings resistant units", "does not hurt friends", "does not ping units", "detonates explosives", "only hurts shields", "causes flaming death", "damage indicators always point down", "skips shields", "only hurts one infection form", "bit 11", "infection form pop", "ignore seat scale for direct damage", "forces hard ping", "does not hurt players"] )
		.Float( 0x18, "area of effect core radius" )
		.Float( 0x1C, "damage lower bound" )
		.FloatBounds( 0x20, "damage upper bound" )
		.Angle( 0x28, "damage inner cone angle" )
		.Angle( 0x2C, "damage outer cone angle" )
		.Float( 0x30, "active camouflage damage" )
		.Float( 0x34, "stun" )
		.Float( 0x38, "maximum stun" )
		.Float( 0x3C, "stun time" )
		.Float( 0x40, "instantaneous acceleration" )
		.Float( 0x44, "rider direct damage scale" )
		.Float( 0x48, "rider maximum transfer damage scale" )
		.Float( 0x4C, "rider minimum transfer damage scale" )
		.StringId( 0x50, "general damage" )
		.StringId( 0x54, "specific damage" )
		.Float( 0x58, "ai stun radius" )
		.FloatBounds( 0x5C, "ai stun bounds" )
		.Float( 0x64, "shake radius" )
		.Float( 0x68, "emp radius" )
		.Block( 0x6C, "player responses", DamageEffectPlayerResponse )
		.Float( 0x74, "temporary camera impulse duration" )
		.Enum( 0x78, "temporary camera impulse fade function", TransitionFunction )
		.Angle( 0x7C, "temporary camera impulse rotation" )
		.Float( 0x80, "temporary camera impulse pushback" )
		.FloatBounds( 0x84, "temporary camera impulse jitter" )
		.Float( 0x8C, "camera shaking duration" )
		.Enum( 0x90, "camera shaking falloff function", TransitionFunction )
		.Float( 0x94, "camera shaking random translation" )
		.Angle( 0x98, "camera shaking random rotation" )
		.Enum( 0x9C, "camera shaking wobble function", WaveFunction )
		.Float( 0xA0, "camera shaking wobble function period" )
		.Float( 0xA4, "camera shaking wobble weight" )
		.Reference( 0xA8, "sound" )
		.Float( 0xB0, "breaking effect forward velocity" )
		.Float( 0xB4, "breaking effect forward radius" )
		.Float( 0xB8, "breaking effect forward exponent" )
		.Float( 0xBC, "breaking effect outward velocity" )
		.Float( 0xC0, "breaking effect outward radius" )
		.Float( 0xC4, "breaking effect outward exponent" );

	static readonly TagStruct CollisionMaterial = new TagStruct( "collision material", 0x4 )
		.StringId( 0x0, "name" );

	// The plane's top bit flips it; material indexes the collision model's materials.
	static readonly TagStruct CollisionBspSurface = new TagStruct( "collision bsp surface", 0x8 )
		.UInt16( 0x0, "plane" )
		.Int16( 0x2, "first edge" )
		.Flags8( 0x4, "flags", ["two sided", "invisible", "climbable", "breakable", "invalid", "conveyor"] )
		.UInt8( 0x5, "breakable surface" )
		.Index( 0x6, "material" );

	// A winged edge: the surface on its left runs start to end and goes on to the forward edge, the right one the other way.
	static readonly TagStruct CollisionBspEdge = new TagStruct( "collision bsp edge", 0xC )
		.Int16( 0x0, "start vertex" )
		.Int16( 0x2, "end vertex" )
		.Int16( 0x4, "forward edge" )
		.Int16( 0x6, "reverse edge" )
		.Int16( 0x8, "left surface" )
		.Int16( 0xA, "right surface" );

	static readonly TagStruct CollisionBspVertex = new TagStruct( "collision bsp vertex", 0x10 )
		.Point3( 0x0, "point" )
		.Int16( 0xC, "first edge" );

	// One node's BSP, in the node's space in world units, as its surfaces, edges and vertices.
	static readonly TagStruct CollisionPermutationBsp = new TagStruct( "collision permutation bsp", 0x44 )
		.Index( 0x0, "node" )
		.Block( 0x2C, "surfaces", CollisionBspSurface )
		.Block( 0x34, "edges", CollisionBspEdge )
		.Block( 0x3C, "vertices", CollisionBspVertex );

	static readonly TagStruct CollisionPermutation = new TagStruct( "collision permutation", 0x14 )
		.StringId( 0x0, "name" );

	static readonly TagStruct CollisionPermutationWithBsps = new TagStruct( "collision permutation", 0x14, CollisionPermutation )
		.Block( 0x4, "bsps", CollisionPermutationBsp );

	static readonly TagStruct CollisionPathfindingSphere = new TagStruct( "collision pathfinding sphere", 0x14 )
		.Index( 0x0, "node" )
		.Flags16( 0x2, "flags", ["remains when open", "vehicle only", "with sector"] )
		.Point3( 0x4, "center" )
		.Float( 0x10, "radius" );

	static readonly TagStruct CollisionNode = new TagStruct( "collision node", 0xC )
		.StringId( 0x0, "name" )
		.Index( 0x6, "parent node" )
		.Index( 0x8, "next sibling node" )
		.Index( 0xA, "first child node" );

	static TagStruct CollisionModelWith( TagStruct permutation ) => new TagStruct( "collision model", 0x34 )
		.Flags32( 0x10, "flags", ["contains open edges"] )
		.Block( 0x14, "materials", CollisionMaterial )
		.Block( 0x1C, "regions", new TagStruct( "collision region", 0xC )
			.StringId( 0x0, "name" )
			.Block( 0x4, "permutations", permutation ) )
		.Block( 0x24, "pathfinding spheres", CollisionPathfindingSphere )
		.Block( 0x2C, "nodes", CollisionNode );

	// A collision model's materials, regions with their permutations and each permutation's BSP per node, pathfinding spheres and nodes.
	public static readonly TagStruct CollisionModel = CollisionModelWith( CollisionPermutationWithBsps );

	// The same without the BSPs, which game.json leaves out of every collision model but the bipeds' to keep its size down.
	public static readonly TagStruct CollisionModelWithoutBsps = CollisionModelWith( CollisionPermutation );

	static readonly TagStruct ModelVariantState = new TagStruct( "model variant state", 0x18 )
		.StringId( 0x0, "permutation name" )
		.Flags8( 0x5, "property flags", ["blurred", "hella-blurred", "shielded", "battery depleted"] )
		.Enum( 0x6, "state", ModelState )
		.Reference( 0x8, "looping effect" )
		.StringId( 0x10, "looping effect marker name" )
		.Float( 0x14, "initial probability" );

	static readonly TagStruct ModelVariantPermutation = new TagStruct( "model variant permutation", 0x20 )
		.StringId( 0x0, "permutation name" )
		.Flags8( 0x5, "flags", ["copy states to all permutations"] )
		.Float( 0x8, "probability" )
		.Block( 0xC, "states", ModelVariantState );

	static readonly TagStruct ModelVariantRegion = new TagStruct( "model variant region", 0x14 )
		.StringId( 0x0, "region name" )
		.Index( 0x6, "parent variant" )
		.Block( 0x8, "permutations", ModelVariantPermutation )
		.Enum( 0x10, "sort order", ["no sorting", "-5 (closest)", "-4", "-3", "-2", "-1", "0 (same as model)", "1", "2", "3", "4", "5 (farthest)"] );

	static readonly TagStruct ModelVariantObject = new TagStruct( "model variant object", 0x10 )
		.StringId( 0x0, "parent marker" )
		.StringId( 0x4, "child marker" )
		.Reference( 0x8, "child object" );

	static readonly TagStruct ModelVariant = new TagStruct( "model variant", 0x38 )
		.StringId( 0x0, "name" )
		.Block( 0x14, "regions", ModelVariantRegion )
		.Block( 0x1C, "objects", ModelVariantObject )
		.StringId( 0x2C, "dialogue sound effect" )
		.Reference( 0x30, "dialogue" );

	static readonly TagStruct ModelMaterial = new TagStruct( "model material", 0x14 )
		.StringId( 0x0, "material name" )
		.Enum( 0x4, "material type", MaterialType )
		.Index( 0x6, "damage section" )
		.StringId( 0xC, "global material name" );

	static readonly TagStruct ModelInstantResponse = new TagStruct( "model instant response", 0x50 )
		.Enum( 0x0, "response type", ["receives all damage", "receives area effect damage", "receives local damage"] )
		.Enum( 0x2, "constraint damage type", ["none", "destroy one of group", "destroy entire group", "loosen one of group", "loosen entire group"] )
		.Flags32( 0x4, "flags", ["kills object", "inhibits melee attack", "inhibits weapon attack", "inhibits walking", "forces drop weapon", "kills weapon primary trigger", "kills weapon secondary trigger", "destroys object", "damages weapon primary trigger", "damages weapon secondary trigger", "light damage left turn", "major damage left turn", "light damage right turn", "major damage right turn", "light damage engine", "major damage engine", "kills object (no player solo)", "causes detonation", "destroy all group constraints", "kills variant objects", "force unattached effects", "fires under threshold", "triggers special death", "only on special death", "only not on special death"] )
		.Float( 0x8, "damage threshold" )
		.Reference( 0xC, "transition effect" )
		.Reference( 0x14, "transition damage effect" )
		.StringId( 0x1C, "region" )
		.Enum( 0x20, "new state", ModelState )
		.StringId( 0x24, "effect marker name" )
		.StringId( 0x28, "damage effect marker name" )
		.Float( 0x2C, "response delay" )
		.Reference( 0x30, "delay effect" )
		.StringId( 0x38, "delay effect marker name" )
		.StringId( 0x3C, "constraint/group name" )
		.StringId( 0x40, "ejecting seat label" )
		.Float( 0x44, "skip fraction" )
		.StringId( 0x48, "destroyed child object marker name" )
		.Float( 0x4C, "total damage threshold" );

	static readonly TagStruct ModelDamageSection = new TagStruct( "model damage section", 0x38 )
		.StringId( 0x0, "name" )
		.Flags32( 0x4, "flags", ["absorbs body damage", "takes full damage when object dies", "cannot die with riders", "takes full damage when object destroyed", "restored on ressurection", "bit 5", "bit 6", "headshotable", "ignores shields"] )
		.Float( 0x8, "vitality percentage" )
		.Block( 0xC, "instant responses", ModelInstantResponse )
		.Float( 0x24, "stun time" )
		.Float( 0x28, "recharge time" )
		.StringId( 0x30, "resurrection restored region name" );

	static readonly TagStruct ModelDamageSeat = new TagStruct( "model damage seat", 0x14 )
		.StringId( 0x0, "seat label" )
		.Float( 0x4, "direct damage scale" )
		.Float( 0x8, "damage transfer fall-off radius" )
		.Float( 0xC, "maximum transfer damage scale" )
		.Float( 0x10, "minimum transfer damage scale" );

	static readonly TagStruct ModelDamageConstraint = new TagStruct( "model damage constraint", 0x14 )
		.StringId( 0x0, "physics model constraint name" )
		.StringId( 0x4, "damage constraint name" )
		.StringId( 0x8, "damage constraint group name" )
		.Float( 0xC, "group probability scale" );

	// An object's body and shield vitality (damage points), stun and recharge times (seconds), and its damage sections.
	// The map build works out the recharge velocities at 0xD0 (1 / recharge time).
	static readonly TagStruct ModelDamageInfo = new TagStruct( "model damage info", 0xF8 )
		.Flags32( 0x0, "flags", ["takes shield damage for children", "takes body damage for children", "always shields friendly damage", "passes area damage to children", "parent never takes body damage for us", "only damaged by explosives", "parent never takes shield damage for us", "cannot die from damage", "passes attached damage to riders"] )
		.StringId( 0x4, "global indirect material name" )
		.Index( 0x8, "indirect damage section" )
		.Enum8( 0x10, "collision damage reporting type", DamageReportingType )
		.Enum8( 0x11, "response damage reporting type", DamageReportingType )
		.Float( 0x28, "maximum vitality" )
		.Float( 0x2C, "minimum stun damage" )
		.Float( 0x30, "stun time" )
		.Float( 0x34, "recharge time" )
		.Float( 0x38, "recharge fraction" )
		.Reference( 0x7C, "shield damaged first person shader" )
		.Reference( 0x84, "shield damaged shader" )
		.Float( 0x8C, "maximum shield vitality" )
		.StringId( 0x90, "global shield material name" )
		.Float( 0x94, "shield minimum stun damage" )
		.Float( 0x98, "shield stun time" )
		.Float( 0x9C, "shield recharge time" )
		.Float( 0xA0, "shield damaged threshold" )
		.Reference( 0xA4, "shield damaged effect" )
		.Reference( 0xAC, "shield depleted effect" )
		.Reference( 0xB4, "shield recharging effect" )
		.Block( 0xBC, "damage sections", ModelDamageSection )
		.Float( 0xD0, "shield recharge velocity" )
		.Float( 0xD4, "health recharge velocity" )
		.Block( 0xD8, "damage seats", ModelDamageSeat )
		.Block( 0xE0, "damage constraints", ModelDamageConstraint )
		.Reference( 0xE8, "overshield first person shader" )
		.Reference( 0xF0, "overshield shader" );

	static readonly TagStruct ModelTarget = new TagStruct( "model target", 0x1C )
		.StringId( 0x0, "marker name" )
		.Float( 0x4, "size" )
		.Angle( 0x8, "cone angle" )
		.Index( 0xC, "damage section" )
		.Index( 0xE, "variant" )
		.Float( 0x10, "targeting relevance" )
		.Flags32( 0x14, "lock-on flags", ["locked by human tracking", "locked by plasma tracking", "headshot", "vulnerable", "always locked by plasma tracking"] )
		.Float( 0x18, "lock-on distance" );

	static readonly TagStruct ModelCollisionPermutation = new TagStruct( "model collision permutation", 0x8 )
		.StringId( 0x0, "name" )
		.Flags8( 0x4, "flags", ["cannot be chosen randomly"] );

	static readonly TagStruct ModelCollisionRegion = new TagStruct( "model collision region", 0x10 )
		.StringId( 0x0, "name" )
		.Block( 0x8, "permutations", ModelCollisionPermutation );

	static readonly TagStruct ModelNode = new TagStruct( "model node", 0x5C )
		.StringId( 0x0, "name" )
		.Index( 0x4, "parent node" )
		.Index( 0x6, "first child node" )
		.Index( 0x8, "next sibling node" )
		.Point3( 0xC, "default translation" )
		.Quaternion( 0x18, "default rotation" );

	// The model tag: render model, collision and physics, animations, variants and damage.
	// Nodes are the render model's skeleton, translations in world units relative to the parent node.
	public static readonly TagStruct Model = new TagStruct( "model", 0xFC )
		.Reference( 0x0, "render model" )
		.Reference( 0x8, "collision model" )
		.Reference( 0x10, "animation" )
		.Reference( 0x18, "physics" )
		.Reference( 0x20, "physics model" )
		.Float( 0x28, "disappear distance" )
		.Float( 0x2C, "begin fade distance" )
		.Float( 0x34, "reduce to l1" )
		.Float( 0x38, "reduce to l2" )
		.Float( 0x3C, "reduce to l3" )
		.Float( 0x40, "reduce to l4" )
		.Float( 0x44, "reduce to l5" )
		.Block( 0x50, "variants", ModelVariant )
		.Block( 0x58, "materials", ModelMaterial )
		.Block( 0x60, "new damage info", ModelDamageInfo )
		.Block( 0x68, "targets", ModelTarget )
		.Block( 0x70, "collision regions", ModelCollisionRegion )
		.Block( 0x78, "nodes", ModelNode )
		.Reference( 0x8C, "default dialogue" )
		.Reference( 0x94, "active camo shader" )
		.Flags32( 0x9C, "flags", ["active camo always on", "active camo always merge", "active camo never merge"] )
		.StringId( 0xA0, "default dialogue effect" )
		.Reference( 0xF0, "hologram shader" )
		.StringId( 0xF8, "hologram control function" );
}
