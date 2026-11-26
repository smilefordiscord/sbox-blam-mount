namespace Blam.Halo1.TagData;

using static Blam.Halo1.TagData.TagLayouts.Options;

// Objects: the shared object definition, items (weapons, equipment, garbage), projectiles and
// units (bipeds, vehicles). Each starts with its parent's fields at the same offsets.
public static partial class TagLayouts
{
	static readonly TagStruct ObjectAttachment = new TagStruct( "object attachment", 0x48 )
		.Reference( 0x0, "type" )
		.String( 0x10, "marker" )
		.Enum( 0x30, "primary scale", ["none", "a out", "b out", "c out", "d out"] )
		.Enum( 0x32, "secondary scale", ["none", "a out", "b out", "c out", "d out"] )
		.Enum( 0x34, "change color", ["none", "a", "b", "c", "d"] );

	static readonly TagStruct ObjectWidget = new TagStruct( "object widget", 0x20 )
		.Reference( 0x0, "reference" );

	static readonly string[] FunctionScaleBy = ["none", "a in", "b in", "c in", "d in", "a out", "b out", "c out", "d out"];

	/// <summary>
	/// One of an object's functions (A to D out, in block order): a wave or an input, shaped and
	/// bounded, that attachments, widgets, shaders and animations follow. Inputs and outputs are
	/// numbered as in <see cref="FunctionScaleBy"/>.
	/// </summary>
	static readonly TagStruct ObjectFunction = new TagStruct( "object function", 0x168 )
		.Flags32( 0x0, "flags", ["invert", "additive", "always active"] )
		.Float( 0x4, "period" )
		.Enum( 0x8, "scale period by", FunctionScaleBy )
		.Enum( 0xA, "function", WaveFunction )
		.Enum( 0xC, "scale function by", FunctionScaleBy )
		.Enum( 0xE, "wobble function", WaveFunction )
		.Float( 0x10, "wobble period" )
		.Float( 0x14, "wobble magnitude" )
		.Float( 0x18, "square wave threshold" )
		.Int16( 0x1C, "step count" )
		.Enum( 0x1E, "map to", FunctionType )
		.Int16( 0x20, "sawtooth count" )
		.Enum( 0x22, "add", FunctionScaleBy )
		.Enum( 0x24, "scale result by", FunctionScaleBy )
		.Enum( 0x26, "bounds mode", ["clip", "clip and normalize", "scale to fit"] )
		.FloatBounds( 0x28, "bounds" )
		.Index( 0x36, "turn off with" )
		.Float( 0x38, "scale by" )
		.String( 0x148, "usage" );

	static readonly TagStruct ObjectChangeColorPermutation = new TagStruct( "object change color permutation", 0x1C )
		.Float( 0x0, "weight" )
		.ColorRgb( 0x4, "color lower bound" )
		.ColorRgb( 0x10, "color upper bound" );

	/// <summary>
	/// One of an object's change colors (A to D, in block order). A new object takes a permutation
	/// and a point between its bounds; a color scaled or darkened by a function follows it after.
	/// </summary>
	static readonly TagStruct ObjectChangeColor = new TagStruct( "object change color", 0x2C )
		.Enum( 0x0, "darken by", FunctionScaleBy )
		.Enum( 0x2, "scale by", FunctionScaleBy )
		.Flags32( 0x4, "flags", ["blend in hsv", "more colors"] )
		.ColorRgb( 0x8, "color lower bound" )
		.ColorRgb( 0x14, "color upper bound" )
		.Block( 0x20, "permutations", ObjectChangeColorPermutation );

	static readonly string[] ObjectFunctionIn =
	[
		"none", "body vitality", "shield vitality", "recent body damage", "recent shield damage", "random constant",
		"umbrella shield vitality", "shield stun", "recent umbrella shield vitality", "umbrella shield stun", "region",
		"region 1", "region 2", "region 3", "region 4", "region 5", "region 6", "region 7", "alive", "compass",
	];

	static readonly string[] ItemFunctionIn = ["none"];

	static readonly string[] UnitFunctionIn =
	[
		"none", "driver seat power", "gunner seat power", "aiming change", "mouth aperture", "integrated light power",
		"can blink", "shield sapping",
	];

	static readonly string[] WeaponFunctionIn =
	[
		"none", "heat", "primary ammunition", "secondary ammunition", "primary rate of fire", "secondary rate of fire",
		"ready", "primary ejection port", "secondary ejection port", "overheated", "primary charged", "secondary charged",
		"illumination", "age", "integrated light", "primary firing", "secondary firing", "primary firing on",
		"secondary firing on",
	];

	/// <summary>
	/// The object definition every object tag starts with (0x17C bytes). Predicted resources are
	/// left out.
	/// </summary>
	public static readonly TagStruct ObjectDefinition = new TagStruct( "object", 0x17C )
		.Enum( 0x0, "object type", ["biped", "vehicle", "weapon", "equipment", "garbage", "projectile", "scenery", "device machine", "device control", "device light fixture", "placeholder", "sound scenery"] )
		.Flags16( 0x2, "flags", ["does not cast shadow", "transparent self occlusion", "brighter than it should be", "not a pathfinding obstacle", "extension of parent", "cast shadow by default", "does not have anniversary geometry"] )
		.Float( 0x4, "bounding radius" )
		.Point3( 0x8, "bounding offset" )
		.Point3( 0x14, "origin offset" )
		.Float( 0x20, "acceleration scale" )
		.Reference( 0x28, "model" )
		.Reference( 0x38, "animation graph" )
		.Reference( 0x70, "collision model" )
		.Reference( 0x80, "physics" )
		.Reference( 0x90, "modifier shader" )
		.Reference( 0xA0, "creation effect" )
		.Float( 0x104, "render bounding radius" )
		.Enum( 0x108, "a in", ObjectFunctionIn )
		.Enum( 0x10A, "b in", ObjectFunctionIn )
		.Enum( 0x10C, "c in", ObjectFunctionIn )
		.Enum( 0x10E, "d in", ObjectFunctionIn )
		.Int16( 0x13C, "hud text message index" )
		.Int16( 0x13E, "forced shader permutation index" )
		.Block( 0x140, "attachments", ObjectAttachment )
		.Block( 0x14C, "widgets", ObjectWidget )
		.Block( 0x158, "functions", ObjectFunction )
		.Block( 0x164, "change colors", ObjectChangeColor );

	/// <summary>
	/// Weapons, equipment and garbage (0x308 bytes, then the item type's own fields).
	/// </summary>
	public static readonly TagStruct Item = ItemFields( new TagStruct( "item", 0x308, ObjectDefinition ) );

	static TagStruct ItemFields( TagStruct item ) => item
		.Flags32( 0x17C, "item flags", ["always maintains z up", "destroyed by explosions", "unaffected by gravity"] )
		.Index( 0x180, "pickup text index" )
		.Int16( 0x182, "sort order" )
		.Float( 0x184, "scale" )
		.Int16( 0x188, "hud message value scale" )
		.Enum( 0x19C, "item a in", ItemFunctionIn )
		.Enum( 0x19E, "item b in", ItemFunctionIn )
		.Enum( 0x1A0, "item c in", ItemFunctionIn )
		.Enum( 0x1A2, "item d in", ItemFunctionIn )
		.Reference( 0x248, "material effects" )
		.Reference( 0x258, "collision sound" )
		.FloatBounds( 0x2E0, "detonation delay" )
		.Reference( 0x2E8, "detonating effect" )
		.Reference( 0x2F8, "detonation effect" );

	static readonly TagStruct MagazineObject = new TagStruct( "weapon magazine object", 0x1C )
		.Int16( 0x0, "rounds" )
		.Reference( 0xC, "equipment" );

	static readonly TagStruct Magazine = new TagStruct( "weapon magazine", 0x70 )
		.Flags32( 0x0, "flags", ["wastes rounds when reloaded", "every round must be chambered"] )
		.Int16( 0x4, "rounds recharged" )
		.Int16( 0x6, "rounds total initial" )
		.Int16( 0x8, "rounds reserved maximum" )
		.Int16( 0xA, "rounds loaded maximum" )
		.Float( 0x14, "reload time" )
		.Int16( 0x18, "rounds reloaded" )
		.Float( 0x1C, "chamber time" )
		.Reference( 0x38, "reloading effect" )
		.Reference( 0x48, "chambering effect" )
		.Block( 0x64, "magazine objects", MagazineObject );

	static readonly TagStruct FiringEffect = new TagStruct( "weapon trigger firing effect", 0x84 )
		.Int16( 0x0, "shot count lower bound" )
		.Int16( 0x2, "shot count upper bound" )
		.Reference( 0x24, "firing effect" )
		.Reference( 0x34, "misfire effect" )
		.Reference( 0x44, "empty effect" )
		.Reference( 0x54, "firing damage" )
		.Reference( 0x64, "misfire damage" )
		.Reference( 0x74, "empty damage" );

	/// <summary>
	/// A weapon trigger. The rates from 0xF0 on are filled in when the map is built (per tick
	/// fractions of the times above them).
	/// </summary>
	static readonly TagStruct Trigger = new TagStruct( "weapon trigger", 0x114 )
		.Flags32( 0x0, "flags", ["tracks fired projectile", "random firing effects", "can fire with partial ammo", "does not repeat automatically", "locks in on off state", "projectiles use weapon origin", "sticks when dropped", "ejects during chamber", "discharging spews", "analog rate of fire", "use error when unzoomed", "projectile vector cannot be adjusted", "projectiles have identical error", "projectile is client side only", "use original unit adjust projectile ray"] )
		.FloatBounds( 0x4, "maximum rate of fire" )
		.Float( 0xC, "acceleration time" )
		.Float( 0x10, "deceleration time" )
		.Float( 0x14, "blurred rate of fire" )
		.Index( 0x20, "magazine" )
		.Int16( 0x22, "rounds per shot" )
		.Int16( 0x24, "minimum rounds loaded" )
		.Int16( 0x26, "projectiles between contrails" )
		.Enum( 0x2C, "prediction type", ["none", "continuous", "instant"] )
		.Enum( 0x2E, "firing noise", Noise )
		.FloatBounds( 0x30, "error" )
		.Float( 0x38, "error acceleration time" )
		.Float( 0x3C, "error deceleration time" )
		.Float( 0x48, "charging time" )
		.Float( 0x4C, "charged time" )
		.Enum( 0x50, "overcharged action", ["none", "explode", "discharge"] )
		.Float( 0x54, "charged illumination" )
		.Float( 0x58, "spew time" )
		.Reference( 0x5C, "charging effect" )
		.Enum( 0x6C, "distribution function", ["point", "horizontal fan"] )
		.Int16( 0x6E, "projectiles per shot" )
		.Angle( 0x70, "distribution angle" )
		.Angle( 0x78, "minimum error" )
		.AngleBounds( 0x7C, "error angle" )
		.Point3( 0x84, "first person offset" )
		.Reference( 0x94, "projectile" )
		.Float( 0xA4, "ejection port recovery time" )
		.Float( 0xA8, "illumination recovery time" )
		.Float( 0xB8, "heat generated per round" )
		.Float( 0xBC, "age generated per round" )
		.Float( 0xC4, "overload time" )
		.Float( 0xF0, "illumination recovery rate" )
		.Float( 0xF4, "ejection port recovery rate" )
		.Float( 0xF8, "firing acceleration rate" )
		.Float( 0xFC, "firing deceleration rate" )
		.Float( 0x100, "error acceleration rate" )
		.Float( 0x104, "error deceleration rate" )
		.Block( 0x108, "firing effects", FiringEffect );

	/// <summary>
	/// <c>weap</c>, 0x508 bytes.
	/// </summary>
	public static readonly TagStruct Weapon = new TagStruct( "weapon", 0x508, Item )
		.Flags32( 0x308, "weapon flags", ["vertical heat display", "mutually exclusive triggers", "attacks automatically on bump", "must be readied", "doesn't count toward maximum", "aim assists only when zoomed", "prevents grenade throwing", "must be picked up", "holds triggers when dropped", "prevents melee attack", "detonates when dropped", "cannot fire at maximum age", "secondary trigger overrides grenades", "does not depower active camo in multilplayer", "enables integrated night vision", "ais use weapon melee damage", "prevents crouching", "uses 3rd person camera"] )
		.String( 0x30C, "label" )
		.Enum( 0x32C, "secondary trigger mode", ["normal", "slaved to primary", "inhibits primary", "loads alterate ammunition", "loads multiple primary ammunition"] )
		.Int16( 0x32E, "maximum alternate shots loaded" )
		.Enum( 0x330, "weapon a in", WeaponFunctionIn )
		.Enum( 0x332, "weapon b in", WeaponFunctionIn )
		.Enum( 0x334, "weapon c in", WeaponFunctionIn )
		.Enum( 0x336, "weapon d in", WeaponFunctionIn )
		.Float( 0x338, "ready time" )
		.Reference( 0x33C, "ready effect" )
		.Float( 0x34C, "heat recovery threshold" )
		.Float( 0x350, "overheated threshold" )
		.Float( 0x354, "heat detonation threshold" )
		.Float( 0x358, "heat detonation fraction" )
		.Float( 0x35C, "heat loss rate" )
		.Float( 0x360, "heat illumination" )
		.Reference( 0x374, "overheated" )
		.Reference( 0x384, "overheat detonation" )
		.Reference( 0x394, "player melee damage" )
		.Reference( 0x3A4, "player melee response" )
		.Reference( 0x3BC, "actor firing parameters" )
		.Float( 0x3CC, "near reticle range" )
		.Float( 0x3D0, "far reticle range" )
		.Float( 0x3D4, "intersection reticle range" )
		.Int16( 0x3DA, "zoom levels" )
		.FloatBounds( 0x3DC, "zoom magnification range" )
		.Angle( 0x3E4, "autoaim angle" )
		.Float( 0x3E8, "autoaim range" )
		.Angle( 0x3EC, "magnetism angle" )
		.Float( 0x3F0, "magnetism range" )
		.Angle( 0x3F4, "deviation angle" )
		.Enum( 0x3FC, "movement penalized", ["always", "when zoomed", "when zoomed or reloading"] )
		.Float( 0x400, "forward movement penalty" )
		.Float( 0x404, "sideways movement penalty" )
		.Float( 0x40C, "minimum target range" )
		.Float( 0x410, "looking time modifier" )
		.Float( 0x418, "light power on time" )
		.Float( 0x41C, "light power off time" )
		.Reference( 0x420, "light power on effect" )
		.Reference( 0x430, "light power off effect" )
		.Float( 0x440, "age heat recovery penalty" )
		.Float( 0x444, "age rate of fire penalty" )
		.Float( 0x448, "age misfire start" )
		.Float( 0x44C, "age misfire chance" )
		.Reference( 0x45C, "first person model" )
		.Reference( 0x46C, "first person animations" )
		.Reference( 0x480, "hud interface" )
		.Reference( 0x490, "pickup sound" )
		.Reference( 0x4A0, "zoom in sound" )
		.Reference( 0x4B0, "zoom out sound" )
		.Float( 0x4CC, "active camo ding" )
		.Float( 0x4D0, "active camo regrowth rate" )
		.Enum( 0x4E2, "weapon type", ["undefined", "shotgun", "needler", "plasma pistol", "plasma rifle", "rocket launcher"] )
		.Block( 0x4F0, "magazines", Magazine )
		.Block( 0x4FC, "triggers", Trigger );

	/// <summary>
	/// <c>eqip</c>, 0x3B0 bytes.
	/// </summary>
	public static readonly TagStruct Equipment = new TagStruct( "equipment", 0x3B0, Item )
		.Enum( 0x308, "powerup type", ["none", "double speed", "over shield", "active camouflage", "full spectrum vision", "health", "grenade"] )
		.Enum( 0x30A, "grenade type", GrenadeType )
		.Float( 0x30C, "powerup time" )
		.Reference( 0x310, "pickup sound" );

	/// <summary>
	/// <c>garb</c>: an item with nothing of its own, 0x3B0 bytes.
	/// </summary>
	public static readonly TagStruct Garbage = ItemFields( new TagStruct( "garbage", 0x3B0, ObjectDefinition ) );

	static readonly TagStruct ProjectileMaterialResponse = new TagStruct( "projectile material response", 0xA0 )
		.Flags16( 0x0, "flags", ["cannot be overpenetrated"] )
		.Enum( 0x2, "default response", ["disappear", "detonate", "reflect", "overpenetrate", "attach"] )
		.Reference( 0x4, "default effect" )
		.Enum( 0x24, "potential response", ["disappear", "detonate", "reflect", "overpenetrate", "attach"] )
		.Flags16( 0x26, "potential flags", ["only against units", "never against units"] )
		.Float( 0x28, "potential skip fraction" )
		.AngleBounds( 0x2C, "potential between" )
		.FloatBounds( 0x34, "potential and" )
		.Reference( 0x3C, "potential effect" )
		.Enum( 0x5C, "scale effects by", ["damage", "angle"] )
		.Angle( 0x60, "angular noise" )
		.Float( 0x64, "velocity noise" )
		.Reference( 0x68, "detonation effect" )
		.Float( 0x90, "initial friction" )
		.Float( 0x94, "maximum distance" )
		.Float( 0x98, "parallel friction" )
		.Float( 0x9C, "perpendicular friction" );

	/// <summary>
	/// <c>proj</c>, 0x24C bytes. Initial and final velocity are world units per tick in a built
	/// map (the tag has them per second), and the guided angular velocity per tick too.
	/// </summary>
	public static readonly TagStruct Projectile = new TagStruct( "projectile", 0x24C, ObjectDefinition )
		.Flags32( 0x17C, "projectile flags", ["oriented along velocity", "ai must use ballistic aiming", "detonation max time if attached", "has super combining explosion", "combine initial velocity with parent velocity", "random attached detonation time", "minimum unattached detonation time"] )
		.Enum( 0x180, "detonation timer starts", ["immediately", "after first bounce", "when at rest"] )
		.Enum( 0x182, "impact noise", Noise )
		.Reference( 0x18C, "super detonation" )
		.Float( 0x19C, "ai perception radius" )
		.Float( 0x1A0, "collision radius" )
		.Float( 0x1A4, "arming time" )
		.Float( 0x1A8, "danger radius" )
		.Reference( 0x1AC, "effect" )
		.FloatBounds( 0x1BC, "timer" )
		.Float( 0x1C4, "minimum velocity" )
		.Float( 0x1C8, "maximum range" )
		.Float( 0x1CC, "air gravity scale" )
		.FloatBounds( 0x1D0, "air damage range" )
		.Float( 0x1D8, "water gravity scale" )
		.FloatBounds( 0x1DC, "water damage range" )
		.Float( 0x1E4, "initial velocity" )
		.Float( 0x1E8, "final velocity" )
		.Angle( 0x1EC, "guided angular velocity" )
		.Enum( 0x1F0, "detonation noise", Noise )
		.Reference( 0x1F4, "detonation started" )
		.Reference( 0x204, "flyby sound" )
		.Reference( 0x214, "attached detonation damage" )
		.Reference( 0x224, "impact damage" )
		.Block( 0x240, "projectile material response", ProjectileMaterialResponse );

	static readonly TagStruct UnitCameraTrack = new TagStruct( "unit camera track", 0x1C )
		.Reference( 0x0, "track" );

	static readonly TagStruct UnitHudInterfaceReference = new TagStruct( "unit hud interface", 0x30 )
		.Reference( 0x0, "hud" );

	static readonly TagStruct UnitDialogueVariant = new TagStruct( "unit dialogue variant", 0x18 )
		.Int16( 0x0, "variant number" )
		.Reference( 0x8, "dialogue" );

	/// <summary>
	/// <c>udlg</c>, 0x1010 bytes: a unit's vocalizations, one sound each (idle, pain, death, hurt
	/// and killed lines, ...). A unit picks its dialogue from its dialogue variants by variant number.
	/// </summary>
	public static readonly TagStruct Dialogue = new TagStruct( "dialogue", 0x1010 )
		.Reference( 0x10, "idle noncombat" )
		.Reference( 0x20, "idle combat" )
		.Reference( 0x30, "idle flee" )
		.Reference( 0x70, "pain body minor" )
		.Reference( 0x80, "pain body major" )
		.Reference( 0x90, "pain shield" )
		.Reference( 0xA0, "pain falling" )
		.Reference( 0xB0, "scream fear" )
		.Reference( 0xC0, "scream pain" )
		.Reference( 0xD0, "maimed limb" )
		.Reference( 0xE0, "maimed head" )
		.Reference( 0xF0, "death quiet" )
		.Reference( 0x100, "death violent" )
		.Reference( 0x110, "death falling" )
		.Reference( 0x120, "death agonizing" )
		.Reference( 0x130, "death instant" )
		.Reference( 0x140, "death flying" )
		.Reference( 0x160, "damaged friend" )
		.Reference( 0x170, "damaged friend player" )
		.Reference( 0x180, "damaged enemy" )
		.Reference( 0x190, "damaged enemy cm" )
		.Reference( 0x1E0, "hurt friend" )
		.Reference( 0x1F0, "hurt friend re" )
		.Reference( 0x200, "hurt friend player" )
		.Reference( 0x210, "hurt enemy" )
		.Reference( 0x220, "hurt enemy re" )
		.Reference( 0x230, "hurt enemy cm" )
		.Reference( 0x240, "hurt enemy bullet" )
		.Reference( 0x250, "hurt enemy needler" )
		.Reference( 0x260, "hurt enemy plasma" )
		.Reference( 0x270, "hurt enemy sniper" )
		.Reference( 0x280, "hurt enemy grenade" )
		.Reference( 0x290, "hurt enemy explosion" )
		.Reference( 0x2A0, "hurt enemy melee" )
		.Reference( 0x2B0, "hurt enemy flame" )
		.Reference( 0x2C0, "hurt enemy shotgun" )
		.Reference( 0x2D0, "hurt enemy vehicle" )
		.Reference( 0x2E0, "hurt enemy mountedweapon" )
		.Reference( 0x320, "killed friend" )
		.Reference( 0x330, "killed friend cm" )
		.Reference( 0x340, "killed friend player" )
		.Reference( 0x350, "killed friend player cm" )
		.Reference( 0x360, "killed enemy" )
		.Reference( 0x370, "killed enemy cm" )
		.Reference( 0x380, "killed enemy player" )
		.Reference( 0x390, "killed enemy player cm" )
		.Reference( 0x3A0, "killed enemy covenant" )
		.Reference( 0x3B0, "killed enemy covenant cm" )
		.Reference( 0x3C0, "killed enemy floodcombat" )
		.Reference( 0x3D0, "killed enemy floodcombat cm" )
		.Reference( 0x3E0, "killed enemy floodcarrier" )
		.Reference( 0x3F0, "killed enemy floodcarrier cm" )
		.Reference( 0x400, "killed enemy sentinel" )
		.Reference( 0x410, "killed enemy sentinel cm" )
		.Reference( 0x420, "killed enemy bullet" )
		.Reference( 0x430, "killed enemy needler" )
		.Reference( 0x440, "killed enemy plasma" )
		.Reference( 0x450, "killed enemy sniper" )
		.Reference( 0x460, "killed enemy grenade" )
		.Reference( 0x470, "killed enemy explosion" )
		.Reference( 0x480, "killed enemy melee" )
		.Reference( 0x490, "killed enemy flame" )
		.Reference( 0x4A0, "killed enemy shotgun" )
		.Reference( 0x4B0, "killed enemy vehicle" )
		.Reference( 0x4C0, "killed enemy mountedweapon" )
		.Reference( 0x4D0, "killing spree" )
		.Reference( 0x510, "player kill cm" )
		.Reference( 0x520, "player kill bullet cm" )
		.Reference( 0x530, "player kill needler cm" )
		.Reference( 0x540, "player kill plasma cm" )
		.Reference( 0x550, "player kill sniper cm" )
		.Reference( 0x560, "anyone kill grenade cm" )
		.Reference( 0x570, "player kill explosion cm" )
		.Reference( 0x580, "player kill melee cm" )
		.Reference( 0x590, "player kill flame cm" )
		.Reference( 0x5A0, "player kill shotgun cm" )
		.Reference( 0x5B0, "player kill vehicle cm" )
		.Reference( 0x5C0, "player kill mountedweapon cm" )
		.Reference( 0x5D0, "player killling spree cm" )
		.Reference( 0x610, "friend died" )
		.Reference( 0x620, "friend player died" )
		.Reference( 0x630, "friend killed by friend" )
		.Reference( 0x640, "friend killed by friendly player" )
		.Reference( 0x650, "friend killed by enemy" )
		.Reference( 0x660, "friend killed by enemy player" )
		.Reference( 0x670, "friend killed by covenant" )
		.Reference( 0x680, "friend killed by flood" )
		.Reference( 0x690, "friend killed by sentinel" )
		.Reference( 0x6A0, "friend betrayed" )
		.Reference( 0x6D0, "new combat alone" )
		.Reference( 0x6E0, "new enemy recent combat" )
		.Reference( 0x6F0, "old enemy sighted" )
		.Reference( 0x700, "unexpected enemy" )
		.Reference( 0x710, "dead friend found" )
		.Reference( 0x720, "alliance broken" )
		.Reference( 0x730, "alliance reformed" )
		.Reference( 0x740, "grenade throwing" )
		.Reference( 0x750, "grenade sighted" )
		.Reference( 0x760, "grenade startle" )
		.Reference( 0x770, "grenade danger enemy" )
		.Reference( 0x780, "grenade danger self" )
		.Reference( 0x790, "grenade danger friend" )
		.Reference( 0x7C0, "new combat group re" )
		.Reference( 0x7D0, "new combat nearby re" )
		.Reference( 0x7E0, "alert friend" )
		.Reference( 0x7F0, "alert friend re" )
		.Reference( 0x800, "alert lost contact" )
		.Reference( 0x810, "alert lost contact re" )
		.Reference( 0x820, "blocked" )
		.Reference( 0x830, "blocked re" )
		.Reference( 0x840, "search start" )
		.Reference( 0x850, "search query" )
		.Reference( 0x860, "search query re" )
		.Reference( 0x870, "search report" )
		.Reference( 0x880, "search abandon" )
		.Reference( 0x890, "search group abandon" )
		.Reference( 0x8A0, "group uncover" )
		.Reference( 0x8B0, "group uncover re" )
		.Reference( 0x8C0, "advance" )
		.Reference( 0x8D0, "advance re" )
		.Reference( 0x8E0, "retreat" )
		.Reference( 0x8F0, "retreat re" )
		.Reference( 0x900, "cover" )
		.Reference( 0x950, "sighted friend player" )
		.Reference( 0x960, "shooting" )
		.Reference( 0x970, "shooting vehicle" )
		.Reference( 0x980, "shooting berserk" )
		.Reference( 0x990, "shooting group" )
		.Reference( 0x9A0, "shooting traitor" )
		.Reference( 0x9B0, "taunt" )
		.Reference( 0x9C0, "taunt re" )
		.Reference( 0x9D0, "flee" )
		.Reference( 0x9E0, "flee re" )
		.Reference( 0x9F0, "flee leader died" )
		.Reference( 0xA00, "attempted flee" )
		.Reference( 0xA10, "attempted flee re" )
		.Reference( 0xA20, "lost contact" )
		.Reference( 0xA30, "hiding finished" )
		.Reference( 0xA40, "vehicle entry" )
		.Reference( 0xA50, "vehicle exit" )
		.Reference( 0xA60, "vehicle woohoo" )
		.Reference( 0xA70, "vehicle scared" )
		.Reference( 0xA80, "vehicle collision" )
		.Reference( 0xA90, "partially sighted" )
		.Reference( 0xAA0, "nothing there" )
		.Reference( 0xAB0, "pleading" )
		.Reference( 0xB20, "surprise" )
		.Reference( 0xB30, "berserk" )
		.Reference( 0xB40, "melee attack" )
		.Reference( 0xB50, "dive" )
		.Reference( 0xB60, "uncover exclamation" )
		.Reference( 0xB70, "leap attack" )
		.Reference( 0xB80, "resurrection" )
		.Reference( 0xBD0, "celebration" )
		.Reference( 0xBE0, "check body enemy" )
		.Reference( 0xBF0, "check body friend" )
		.Reference( 0xC00, "shooting dead enemy" )
		.Reference( 0xC10, "shooting dead enemy player" )
		.Reference( 0xC60, "alone" )
		.Reference( 0xC70, "unscathed" )
		.Reference( 0xC80, "seriously wounded" )
		.Reference( 0xC90, "seriously wounded re" )
		.Reference( 0xCA0, "massacre" )
		.Reference( 0xCB0, "massacre re" )
		.Reference( 0xCC0, "rout" )
		.Reference( 0xCD0, "rout re" );

	static readonly TagStruct UnitPoweredSeat = new TagStruct( "unit powered seat", 0x44 )
		.Float( 0x4, "driver powerup time" )
		.Float( 0x8, "driver powerdown time" );

	static readonly TagStruct UnitWeapon = new TagStruct( "unit weapon", 0x24 )
		.Reference( 0x0, "weapon" );

	static readonly TagStruct UnitSeat = new TagStruct( "unit seat", 0x11C )
		.Flags32( 0x0, "flags", ["invisible", "locked", "driver", "gunner", "third person camera", "allows weapons", "third person on enter", "first person camera slaved to gun", "allow vehicle communication animations", "not valid without driver", "allow ai noncombatants"] )
		.String( 0x4, "label" )
		.String( 0x24, "marker name" )
		.Point3( 0x64, "acceleration scale" )
		.Float( 0x7C, "yaw rate" )
		.Float( 0x80, "pitch rate" )
		.String( 0x84, "camera marker name" )
		.String( 0xA4, "camera submerged marker name" )
		.Angle( 0xC4, "pitch auto level" )
		.AngleBounds( 0xC8, "pitch range" )
		.Block( 0xD0, "camera tracks", UnitCameraTrack )
		.Block( 0xDC, "unit hud interface", UnitHudInterfaceReference )
		.Index( 0xEC, "hud text message index" )
		.Angle( 0xF0, "yaw minimum" )
		.Angle( 0xF4, "yaw maximum" )
		.Reference( 0xF8, "built in gunner" );

	/// <summary>
	/// Bipeds and vehicles (0x2F0 bytes, then the unit type's own fields).
	/// </summary>
	public static readonly TagStruct Unit = UnitFields( new TagStruct( "unit", 0x2F0, ObjectDefinition ) );

	static TagStruct UnitFields( TagStruct unit ) => unit
		.Flags32( 0x17C, "unit flags", ["circular aiming", "destroyed after dying", "half speed interpolation", "fires from camera", "entrance inside bounding sphere", "unused", "causes passenger dialogue", "resists pings", "melee attack is fatal", "don't reface during pings", "has no aiming", "simple creature", "impact melee attaches to unit", "impact melee dies on shields", "cannot open doors automatically", "melee attackers cannot attach", "not instantly killed by melee", "shield sapping", "runs around flaming", "inconsequential", "special cinematic unit", "ignored by autoaiming", "shields fry infection forms", "integrated light cntrls weapon", "integrated light lasts forever"] )
		.Enum( 0x180, "default team", Team )
		.Enum( 0x182, "constant sound volume", Noise )
		.Float( 0x184, "rider damage fraction" )
		.Reference( 0x188, "integrated light toggle" )
		.Enum( 0x198, "unit a in", UnitFunctionIn )
		.Enum( 0x19A, "unit b in", UnitFunctionIn )
		.Enum( 0x19C, "unit c in", UnitFunctionIn )
		.Enum( 0x19E, "unit d in", UnitFunctionIn )
		.Angle( 0x1A0, "camera field of view" )
		.Float( 0x1A4, "camera stiffness" )
		.String( 0x1A8, "camera marker name" )
		.String( 0x1C8, "camera submerged marker name" )
		.Angle( 0x1E8, "pitch auto level" )
		.AngleBounds( 0x1EC, "pitch range" )
		.Block( 0x1F4, "camera tracks", UnitCameraTrack )
		.Point3( 0x200, "seat acceleration scale" )
		.Float( 0x218, "soft ping threshold" )
		.Float( 0x21C, "soft ping interrupt time" )
		.Float( 0x220, "hard ping threshold" )
		.Float( 0x224, "hard ping interrupt time" )
		.Float( 0x228, "hard death threshold" )
		.Float( 0x22C, "feign death threshold" )
		.Float( 0x230, "feign death time" )
		.Float( 0x234, "distance of evade anim" )
		.Float( 0x238, "distance of dive anim" )
		.Float( 0x240, "stunned movement threshold" )
		.Float( 0x244, "feign death chance" )
		.Float( 0x248, "feign repeat chance" )
		.Reference( 0x24C, "spawned actor" )
		.Int16Bounds( 0x25C, "spawned actor count" )
		.Float( 0x260, "spawned velocity" )
		.Angle( 0x264, "aiming velocity maximum" )
		.Angle( 0x268, "aiming acceleration maximum" )
		.Float( 0x26C, "casual aiming modifier" )
		.Angle( 0x270, "looking velocity maximum" )
		.Angle( 0x274, "looking acceleration maximum" )
		.Float( 0x280, "ai vehicle radius" )
		.Float( 0x284, "ai danger radius" )
		.Reference( 0x288, "melee damage" )
		.Enum( 0x298, "motion sensor blip size", ["medium", "small", "large"] )
		.Enum( 0x29C, "metagame type", MetagameType )
		.Enum( 0x29E, "metagame class", MetagameClass )
		.Block( 0x2A8, "new hud interfaces", UnitHudInterfaceReference )
		.Block( 0x2B4, "dialogue variants", UnitDialogueVariant )
		.Float( 0x2C0, "grenade velocity" )
		.Enum( 0x2C4, "grenade type", GrenadeType )
		.Int16( 0x2C6, "grenade count" )
		.Int16( 0x2C8, "soft ping interrupt ticks" )
		.Int16( 0x2CA, "hard ping interrupt ticks" )
		.Block( 0x2CC, "powered seats", UnitPoweredSeat )
		.Block( 0x2D8, "weapons", UnitWeapon )
		.Block( 0x2E4, "seats", UnitSeat );

	static readonly TagStruct BipedContactPoint = new TagStruct( "biped contact point", 0x40 )
		.String( 0x20, "marker name" );

	/// <summary>
	/// <c>bipd</c>, 0x4F4 bytes. The fields from 0x4C8 on are worked out from the ones above when
	/// the map is built (cosines and sines of the slope angles).
	/// </summary>
	public static readonly TagStruct Biped = new TagStruct( "biped", 0x4F4, Unit )
		.Angle( 0x2F0, "moving turning speed" )
		.Flags32( 0x2F4, "biped flags", ["turns without animating", "uses player physics", "flying", "physics pill centered at origin", "spherical", "passes through other bipeds", "can climb any surface", "immune to falling damage", "rotate while airborne", "uses limp body physics", "has no dying airborne", "random speed increase", "unit uses old ntsc player physics"] )
		.Angle( 0x2F8, "stationary turning threshold" )
		.Enum( 0x30C, "biped a in", ["none", "flying velocity"] )
		.Enum( 0x30E, "biped b in", ["none", "flying velocity"] )
		.Enum( 0x310, "biped c in", ["none", "flying velocity"] )
		.Enum( 0x312, "biped d in", ["none", "flying velocity"] )
		.Angle( 0x324, "bank angle" )
		.Float( 0x328, "bank apply time" )
		.Float( 0x32C, "bank decay time" )
		.Float( 0x330, "pitch ratio" )
		.Float( 0x334, "max velocity" )
		.Float( 0x338, "max sidestep velocity" )
		.Float( 0x33C, "acceleration" )
		.Float( 0x340, "deceleration" )
		.Angle( 0x344, "angular velocity maximum" )
		.Angle( 0x348, "angular acceleration maximum" )
		.Float( 0x34C, "crouch velocity modifier" )
		.Angle( 0x358, "maximum slope angle" )
		.Angle( 0x35C, "downhill falloff angle" )
		.Angle( 0x360, "downhill cutoff angle" )
		.Float( 0x364, "downhill velocity scale" )
		.Angle( 0x368, "uphill falloff angle" )
		.Angle( 0x36C, "uphill cutoff angle" )
		.Float( 0x370, "uphill velocity scale" )
		.Reference( 0x38C, "footsteps" )
		.Float( 0x3B4, "jump velocity" )
		.Float( 0x3D4, "maximum soft landing time" )
		.Float( 0x3D8, "maximum hard landing time" )
		.Float( 0x3DC, "minimum soft landing velocity" )
		.Float( 0x3E0, "minimum hard landing velocity" )
		.Float( 0x3E4, "maximum hard landing velocity" )
		.Float( 0x3E8, "death hard landing velocity" )
		.Float( 0x400, "standing camera height" )
		.Float( 0x404, "crouching camera height" )
		.Float( 0x408, "crouch transition time" )
		.Float( 0x424, "standing collision height" )
		.Float( 0x428, "crouching collision height" )
		.Float( 0x42C, "collision radius" )
		.Float( 0x458, "autoaim width" )
		.Float( 0x4C8, "cosine stationary turning threshold" )
		.Float( 0x4CC, "crouch camera velocity" )
		.Float( 0x4D0, "cosine maximum slope angle" )
		.Float( 0x4D4, "negative sine downhill falloff angle" )
		.Float( 0x4D8, "negative sine downhill cutoff angle" )
		.Float( 0x4DC, "sine uphill falloff angle" )
		.Float( 0x4E0, "sine uphill cutoff angle" )
		.Index( 0x4E4, "pelvis model node index" )
		.Index( 0x4E6, "head model node index" )
		.Block( 0x4E8, "contact point", BipedContactPoint );

	static readonly string[] VehicleFunctionIn =
	[
		"none", "speed absolute", "speed forward", "speed backward", "slide absolute", "slide left", "slide right",
		"speed slide maximum", "turn absolute", "turn left", "turn right", "crouch", "jump", "walk", "velocity air",
		"velocity water", "velocity ground", "velocity forward", "velocity left", "velocity up", "left tread position",
		"right tread position", "left tread velocity", "right tread velocity", "front left tire position",
		"front right tire position", "back left tire position", "back right tire position", "front left tire velocity",
		"front right tire velocity", "back left tire velocity", "back right tire velocity", "wingtip contrail",
		"hover", "thrust", "engine hack", "wingtip contrail new",
	];

	/// <summary>
	/// <c>vehi</c>, 0x3F0 bytes. Speeds are world units per tick, turns and slides as the tag
	/// stores them. Despite its name, the fixed gun pitch is in radians (the banshee's nose-down
	/// gun), so it's written as stored.
	/// </summary>
	public static readonly TagStruct Vehicle = new TagStruct( "vehicle", 0x3F0, Unit )
		.Flags32( 0x2F0, "vehicle flags", ["speed wakes physics", "turn wakes physics", "driver power wakes physics", "gunner power wakes physics", "control opposite speed sets brake", "slide wakes physics", "kills riders at terminal velocity", "causes collision damage", "ai weapon cannot rotate", "ai does not require driver", "ai unused", "ai driver enable", "ai driver flying", "ai driver can sidestep", "ai driver hovering", "vehicle steers directly", "unused", "has ebrake", "noncombat vehicle", "no friction with driver", "can trigger automatic opening doors", "autoaim when teamless"] )
		.Enum( 0x2F4, "vehicle type", ["human tank", "human jeep", "human boat", "human plane", "alien scout", "alien fighter", "turret"] )
		.Float( 0x2F8, "maximum forward speed" )
		.Float( 0x2FC, "maximum reverse speed" )
		.Float( 0x300, "speed acceleration" )
		.Float( 0x304, "speed deceleration" )
		.Float( 0x308, "maximum left turn" )
		.Float( 0x30C, "maximum right turn" )
		.Float( 0x310, "wheel circumference" )
		.Float( 0x314, "turn rate" )
		.Float( 0x318, "blur speed" )
		.Enum( 0x31C, "vehicle a in", VehicleFunctionIn )
		.Enum( 0x31E, "vehicle b in", VehicleFunctionIn )
		.Enum( 0x320, "vehicle c in", VehicleFunctionIn )
		.Enum( 0x322, "vehicle d in", VehicleFunctionIn )
		.Float( 0x330, "maximum left slide" )
		.Float( 0x334, "maximum right slide" )
		.Float( 0x338, "slide acceleration" )
		.Float( 0x33C, "slide deceleration" )
		.Float( 0x340, "minimum flipping angular velocity" )
		.Float( 0x344, "maximum flipping angular velocity" )
		.Float( 0x360, "fixed gun yaw" )
		.Float( 0x364, "fixed gun pitch" )
		.Float( 0x380, "ai sideslip distance" )
		.Float( 0x384, "ai destination radius" )
		.Float( 0x388, "ai avoidance distance" )
		.Float( 0x38C, "ai pathfinding radius" )
		.Float( 0x390, "ai charge repeat timeout" )
		.Float( 0x394, "ai strafing abort range" )
		.FloatBounds( 0x398, "ai oversteering bounds" )
		.Angle( 0x3A0, "ai steering maximum" )
		.Float( 0x3A4, "ai throttle maximum" )
		.Float( 0x3A8, "ai move position time" )
		.Reference( 0x3B0, "suspension sound" )
		.Reference( 0x3C0, "crash sound" )
		.Reference( 0x3D0, "material effects" )
		.Reference( 0x3E0, "effect" );

	static readonly TagStruct PoweredMassPoint = new TagStruct( "powered mass point", 0x80 )
		.String( 0x0, "name" )
		.Flags32( 0x20, "flags", ["ground friction", "water friction", "air friction", "water lift", "air lift", "thrust", "antigrav"] )
		.Float( 0x24, "antigrav strength" )
		.Float( 0x28, "antigrav offset" )
		.Float( 0x2C, "antigrav height" )
		.Float( 0x30, "antigrav damp fraction" )
		.Float( 0x34, "antigrav normal k1" )
		.Float( 0x38, "antigrav normal k0" );

	static readonly TagStruct MassPoint = new TagStruct( "mass point", 0x80 )
		.String( 0x0, "name" )
		.Index( 0x20, "powered mass point" )
		.Index( 0x22, "model node" )
		.Flags32( 0x24, "flags", ["metallic"] )
		.Float( 0x28, "relative mass" )
		.Float( 0x2C, "mass" )
		.Float( 0x30, "relative density" )
		.Float( 0x34, "density" )
		.Point3( 0x38, "position" )
		.Point3( 0x44, "forward" )
		.Point3( 0x50, "up" )
		.Enum( 0x5C, "friction type", ["point", "forward", "left", "up"] )
		.Float( 0x60, "friction parallel scale" )
		.Float( 0x64, "friction perpendicular scale" )
		.Float( 0x68, "radius" );

	/// <summary>
	/// <c>phys</c>, 0x80 bytes: a vehicle's mass point physics. Positions are in object space.
	/// The moments and the mass points' masses and densities are worked out when the map is built.
	/// </summary>
	public static readonly TagStruct Physics = new TagStruct( "physics", 0x80 )
		.Float( 0x0, "radius" )
		.Float( 0x4, "moment scale" )
		.Float( 0x8, "mass" )
		.Point3( 0xC, "center of mass" )
		.Float( 0x18, "density" )
		.Float( 0x1C, "gravity scale" )
		.Float( 0x20, "ground friction" )
		.Float( 0x24, "ground depth" )
		.Float( 0x28, "ground damp fraction" )
		.Float( 0x2C, "ground normal k1" )
		.Float( 0x30, "ground normal k0" )
		.Float( 0x38, "water friction" )
		.Float( 0x3C, "water depth" )
		.Float( 0x40, "water density" )
		.Float( 0x48, "air friction" )
		.Float( 0x50, "xx moment" )
		.Float( 0x54, "yy moment" )
		.Float( 0x58, "zz moment" )
		.Block( 0x68, "powered mass points", PoweredMassPoint )
		.Block( 0x74, "mass points", MassPoint );

	static readonly TagStruct ModelMarkerInstance = new TagStruct( "model marker instance", 0x20 )
		.ByteIndex( 0x0, "region index" )
		.ByteIndex( 0x1, "permutation index" )
		.ByteIndex( 0x2, "node index" )
		.Point3( 0x4, "translation" )
		.Quaternion( 0x10, "rotation" );

	static readonly TagStruct ModelMarker = new TagStruct( "model marker", 0x40 )
		.String( 0x0, "name" )
		.Block( 0x34, "instances", ModelMarkerInstance );

	static readonly TagStruct ModelNode = new TagStruct( "model node", 0x9C )
		.String( 0x0, "name" )
		.Index( 0x20, "next sibling node index" )
		.Index( 0x22, "first child node index" )
		.Index( 0x24, "parent node index" )
		.Point3( 0x28, "default translation" )
		.Quaternion( 0x34, "default rotation" )
		.Float( 0x44, "node distance from parent" );

	/// <summary>
	/// A region's permutation. A new object picks among those it may choose randomly by variant
	/// number: under 100 at random, or the unit's own (100 and up are unique characters' heads).
	/// </summary>
	static readonly TagStruct ModelRegionPermutation = new TagStruct( "model region permutation", 0x58 )
		.String( 0x0, "name" )
		.Flags32( 0x20, "flags", ["cannot be chosen randomly"] )
		.Int16( 0x24, "variant number" );

	static readonly TagStruct ModelRegion = new TagStruct( "model region", 0x4C )
		.String( 0x0, "name" )
		.Block( 0x40, "permutations", ModelRegionPermutation );

	/// <summary>
	/// <c>mod2</c> (gbxmodel), 0xE8 bytes: the skeleton, the markers and the regions' permutation
	/// names, not the geometry (the mount builds that into models; a region with several
	/// permutations is a body group named after it, a choice per permutation in this order). Translations are relative to the parent node (nodes) or to
	/// the node (markers), in world units; rotations are stored conjugated (see
	/// <see cref="Tags.ModelAnimationsTag.FromHalo"/>).
	/// </summary>
	public static readonly TagStruct ModelSkeleton = new TagStruct( "gbxmodel", 0xE8 )
		.Flags32( 0x0, "flags", ["blend shared normals", "parts have local nodes", "ignore skinning"] )
		.Float( 0x8, "super high detail cutoff" )
		.Float( 0xC, "high detail cutoff" )
		.Float( 0x10, "medium detail cutoff" )
		.Float( 0x14, "low detail cutoff" )
		.Float( 0x18, "super low detail cutoff" )
		.Float( 0x30, "base map u scale" )
		.Float( 0x34, "base map v scale" )
		.Block( 0xAC, "markers", ModelMarker )
		.Block( 0xC4, "regions", ModelRegion )
		.Block( 0xB8, "nodes", ModelNode );
}
