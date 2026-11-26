namespace Blam.Halo2.TagData;

using static Blam.Halo2.TagData.TagLayouts.Options;

// Collections, material effects, effects, contrails, lights, sound classes and the scenario's multiplayer parts.
public static partial class TagLayouts
{
	static readonly TagStruct ItemPermutation = new TagStruct( "item permutation", 0x10 )
		.Float( 0x0, "weight" )
		.Reference( 0x4, "item" )
		.StringId( 0xC, "variant name" );

	// What an item spawn picks from (see ItemCollectionTag.Pick).
	// Spawn time is in seconds, 0 for the default.
	public static readonly TagStruct ItemCollection = new TagStruct( "item collection", 0xC )
		.Block( 0x0, "item permutations", ItemPermutation )
		.Int16( 0x8, "spawn time" );

	static readonly TagStruct VehiclePermutation = new TagStruct( "vehicle permutation", 0x10 )
		.Float( 0x0, "weight" )
		.Reference( 0x4, "vehicle" )
		.StringId( 0xC, "variant name" );

	public static readonly TagStruct VehicleCollection = new TagStruct( "vehicle collection", 0xC )
		.Block( 0x0, "vehicle permutations", VehiclePermutation )
		.Int16( 0x8, "spawn time" );

	static readonly TagStruct MaterialEffectOldMaterial = new TagStruct( "material effect old material", 0x1C )
		.Reference( 0x0, "effect" )
		.Reference( 0x8, "sound" )
		.StringId( 0x10, "material name" )
		.Enum8( 0x18, "sweetener mode", SweetenerMode );

	// What plays on one global material (by name).
	static readonly TagStruct MaterialEffectMaterial = new TagStruct( "material effect material", 0x18 )
		.Reference( 0x0, "tag" )
		.Reference( 0x8, "secondary tag" )
		.StringId( 0x10, "material name" )
		.Enum8( 0x16, "sweetener mode", SweetenerMode );

	static readonly TagStruct MaterialEffect = new TagStruct( "material effect", 0x18 )
		.Block( 0x0, "old materials", MaterialEffectOldMaterial )
		.Block( 0x8, "sounds", MaterialEffectMaterial )
		.Block( 0x10, "effects", MaterialEffectMaterial );

	// Effects by material: a biped's footsteps or an object's impacts.
	public static readonly TagStruct MaterialEffects = new TagStruct( "material effects", 0x8 )
		.Block( 0x0, "effects", MaterialEffect );

	static readonly TagStruct EffectLocation = new TagStruct( "effect location", 0x4 )
		.StringId( 0x0, "marker name" );

	static readonly TagStruct EffectPart = new TagStruct( "effect part", 0x38 )
		.Enum( 0x0, "create in", ["any environment", "air only", "water only", "space only"] )
		.Enum( 0x2, "violence mode", ["either mode", "violent mode only", "nonviolent mode only"] )
		.Index( 0x4, "location" )
		.Flags16( 0x6, "flags", ["face down regardless of location", "offset origin away from geometry", "never attached to object", "disabled for debugging", "draw regardless of distance"] )
		.Reference( 0xC, "type" )
		.FloatBounds( 0x14, "velocity bounds" )
		.Angle( 0x1C, "velocity cone angle" )
		.AngleBounds( 0x20, "angular velocity bounds" )
		.FloatBounds( 0x28, "radius modifier bounds" )
		.Flags32( 0x30, "a scales values", ["velocity", "velocity delta", "velocity cone angle", "angular velocity", "angular velocity delta", "type-specific scale"] )
		.Flags32( 0x34, "b scales values", ["velocity", "velocity delta", "velocity cone angle", "angular velocity", "angular velocity delta", "type-specific scale"] );

	static readonly TagStruct EffectEvent = new TagStruct( "effect event", 0x38 )
		.Float( 0x4, "skip fraction" )
		.FloatBounds( 0x8, "delay bounds" )
		.FloatBounds( 0x10, "duration bounds" )
		.Block( 0x18, "parts", EffectPart );

	// An effect's damage radius, locations (marker names) and events with their parts.
	// Beams, accelerations and particle systems are left out, since the mount's effect prefabs play those.
	public static readonly TagStruct Effect = new TagStruct( "effect", 0x30 )
		.Flags32( 0x0, "flags", ["deleted when attachment deactivates"] )
		.Index( 0x4, "loop start event" )
		.Float( 0x8, "maximum damage radius" )
		.Block( 0xC, "locations", EffectLocation )
		.Block( 0x14, "events", EffectEvent )
		.Reference( 0x1C, "looping sound" )
		.Index( 0x24, "location" )
		.Float( 0x28, "always play distance" )
		.Float( 0x2C, "never play distance" );

	static readonly TagStruct ContrailPointState = new TagStruct( "contrail point state", 0x40 )
		.FloatBounds( 0x0, "duration" )
		.FloatBounds( 0x8, "transition duration" )
		.Reference( 0x10, "physics" )
		.Float( 0x18, "width" )
		.ColorArgb( 0x1C, "color lower bound" )
		.ColorArgb( 0x2C, "color upper bound" )
		.Flags32( 0x3C, "scale flags", ["duration", "duration delta", "transition duration", "transition duration delta", "width", "color"] );

	// A trail of points laid behind a projectile or object, each aging through the point states.
	// Field names are Halo CE's, whose contrail Halo 2 kept; the shader part is Halo 2's own.
	public static readonly TagStruct Contrail = new TagStruct( "contrail", 0xF0 )
		.Flags16( 0x0, "flags", ["first point unfaded", "last point unfaded", "points start pinned to media", "points start pinned to ground", "points always pinned to media", "points always pinned to ground", "edge effect fades slowly", "don't inherit object change color"] )
		.Flags16( 0x2, "scale flags", ["point generation rate", "point velocity", "point velocity delta", "point velocity cone angle", "inherited velocity fraction", "sequence animation rate", "texture scale u", "texture scale v", "texture animation u", "texture animation v"] )
		.Float( 0x4, "point generation rate" )
		.FloatBounds( 0x8, "point velocity" )
		.Angle( 0x10, "point velocity cone angle" )
		.Float( 0x14, "inherited velocity fraction" )
		.Enum( 0x18, "render type", ["vertical orientation", "horizontal orientation", "media mapped", "ground mapped", "viewer facing", "double-marker linked"] )
		.Float( 0x1C, "texture repeats u" )
		.Float( 0x20, "texture repeats v" )
		.Float( 0x24, "texture animation u" )
		.Float( 0x28, "texture animation v" )
		.Float( 0x2C, "animation rate" )
		.Reference( 0x30, "bitmap" )
		.Int16( 0x38, "first sequence index" )
		.Int16( 0x3A, "sequence count" )
		.Flags16( 0x64, "shader flags", ["sort bias", "nonlinear tint", "don't overdraw fp weapon"] )
		.Enum( 0x66, "framebuffer blend function", ["alpha blend", "multiply", "double multiply", "add", "subtract", "component min", "component max", "alpha multiply add", "constant color blend", "inverse constant color blend", "none"] )
		.Enum( 0x68, "framebuffer fade mode", ["none", "fade when perpendicular", "fade when parallel"] )
		.Block( 0xE8, "point states", ContrailPointState );

	// Halo evaluates each animation block's first function every tick, at the game time in seconds.
	// The range input is the light's scale; unsynchronized lights each add their own offset of under a minute to the time.
	// Brightness scales the light's brightness, colour its diffuse colour, and the gel functions offset its gel map.
	static readonly TagStruct LightFunction = new TagStruct( "light function", 0x8 )
	{
		Extra = MappingFunctionJson.WriteDecoded( ("Function", 0x0) ),
	}
		.ByteData( 0x0, "function" );

	static readonly TagStruct LightGelAnimation = new TagStruct( "light gel animation", 0x10 )
	{
		Extra = MappingFunctionJson.WriteDecoded( ("DxFunction", 0x0), ("DyFunction", 0x8) ),
	}
		.ByteData( 0x0, "dx function" )
		.ByteData( 0x8, "dy function" );

	static readonly string[] LightFade = ["fade very far", "fade far", "fade medium", "fade close", "fade very close"];

	// A dynamic light: its shape, colour and brightness bounds, falloff, lens flare and light volume.
	// An object's light shows its diffuse upper bound: the colour where its function input is 1 (0 at the lower bound).
	// A light an effect creates lasts its duration, its scale diminishing by the effect falloff function.
	// The animation functions are mapping functions, written in base64 and decoded (see MappingFunctionJson).
	public static readonly TagStruct Light = new TagStruct( "light", 0xE4 )
		.Flags32( 0x0, "flags", ["no illumination", "no specular", "force cast environment shadows through portals", "no shadow", "force frustum visibility on small light", "only render in first person", "only render in third person", "don't fade when invisible", "multiplayer override", "animated gel", "only in dynamic envmap", "ignore parent object", "don't shadow parent", "ignore all parents", "march milestone hack", "force light inside world", "environment doesn't cast stencil shadows", "objects don't cast stencil shadows", "first person from camera", "texture camera gel", "light framerate killer", "allowed in split screen", "only on parent bipeds"] )
		.Enum( 0x4, "type", ["sphere", "orthogonal", "projective", "pyramid"] )
		.FloatBounds( 0x8, "size modifier" )
		.Float( 0x10, "shadow quality bias" )
		.Enum( 0x14, "shadow tap bias", ["3 tap", "unused", "1 tap"] )
		.Float( 0x18, "radius" )
		.Float( 0x1C, "specular radius" )
		.Float( 0x20, "near width" )
		.Float( 0x24, "height stretch" )
		.Angle( 0x28, "field of view" )
		.Float( 0x2C, "falloff distance" )
		.Float( 0x30, "cutoff distance" )
		.Flags32( 0x34, "interpolation flags", ["blend in hsv", "more colors"] )
		.FloatBounds( 0x38, "bloom bounds" )
		.ColorRgb( 0x40, "specular lower bound" )
		.ColorRgb( 0x4C, "specular upper bound" )
		.ColorRgb( 0x58, "diffuse lower bound" )
		.ColorRgb( 0x64, "diffuse upper bound" )
		.FloatBounds( 0x70, "brightness bounds" )
		.Reference( 0x78, "gel map" )
		.Enum( 0x80, "specular mask", ["default", "none (no mask)", "gel alpha", "gel color"] )
		.Enum( 0x88, "falloff function", ["default", "narrow", "broad", "very broad"] )
		.Enum( 0x8A, "diffuse contrast", ["default (linear)", "high", "low", "very low"] )
		.Enum( 0x8C, "specular contrast", ["default (one)", "high (linear)", "low", "very low"] )
		.Enum( 0x8E, "falloff geometry", ["default", "directional", "spherical"] )
		.Reference( 0x90, "lens flare" )
		.Float( 0x98, "bounding radius" )
		.Reference( 0x9C, "light volume" )
		.Enum( 0xA4, "default lightmap setting", ["dynamic only", "dynamic with lightmaps", "lightmaps only"] )
		.Float( 0xA8, "lightmap half life" )
		.Float( 0xAC, "lightmap light scale" )
		.Float( 0xB0, "duration" )
		.Enum( 0xB6, "effect falloff function", TransitionFunction )
		.Enum( 0xB8, "illumination fade", LightFade )
		.Enum( 0xBA, "shadow fade", LightFade )
		.Enum( 0xBC, "specular fade", LightFade )
		.Flags32( 0xC0, "animation flags", ["synchronized"] )
		.Block( 0xC4, "brightness animation", LightFunction )
		.Block( 0xCC, "color animation", LightFunction )
		.Block( 0xD4, "gel animation", LightGelAnimation )
		.Reference( 0xDC, "shader" );

	static readonly TagStruct SoundClassDucking = new TagStruct( "sound class ducking", 0x14 )
		.Enum32( 0x0, "sound class", SoundExport.SoundClasses )
		.Float( 0x4, "ducking" )
		.Float( 0x8, "ducking fade in time" )
		.Float( 0xC, "ducking sustain time" )
		.Float( 0x10, "ducking fade out time" );

	// One sound class, the slot a sound tag's class indexes: how many of its sounds play at once and how they play.
	// Preemption time is in milliseconds, gains and duckings in dB, distances in world units and times in seconds.
	static readonly TagStruct SoundClass = new TagStruct( "sound class", 0x78 )
	{
		SlotNames = SoundExport.SoundClasses,
		Extra = MappingFunctionJson.WriteDecoded( ("Function", 0x68) ),
	}
		.Int16( 0x0, "maximum sounds per tag" )
		.Int16( 0x2, "maximum sounds per object" )
		.Int32( 0x4, "preemption time" )
		.Flags16( 0x8, "internal flags", ["valid", "is speech", "scripted", "stops with object", "bit 4", "valid doppler factor", "valid obstruction factor", "multilingual"] )
		.Flags16( 0xA, "flags", ["plays during pause", "dry stereo mix", "no object obstruction", "use center speaker unspatialized", "send (mono) to lfe", "deterministic", "use huge transmission", "always use speakers", "don't strip from mainmenu", "ignore stereo headroom", "loop fade out is linear", "stop when object dies", "allow cache file editing", "bypass lfe"] )
		.Int16( 0xC, "priority" )
		.Enum( 0xE, "cache miss mode", ["discard", "postpone"] )
		.Float( 0x10, "reverb gain" )
		.Float( 0x14, "override speaker gain" )
		.FloatBounds( 0x18, "distance bounds" )
		.FloatBounds( 0x20, "gain bounds" )
		.Float( 0x28, "cutscene ducking" )
		.Float( 0x2C, "cutscene ducking fade in time" )
		.Float( 0x30, "cutscene ducking sustain time" )
		.Float( 0x34, "cutscene ducking fade out time" )
		.Float( 0x38, "scripted dialog ducking" )
		.Float( 0x3C, "scripted dialog ducking fade in time" )
		.Float( 0x40, "scripted dialog ducking sustain time" )
		.Float( 0x44, "scripted dialog ducking fade out time" )
		.Float( 0x48, "doppler factor" )
		.Enum8( 0x4C, "stereo playback type", ["first person", "ambient"] )
		.Float( 0x50, "transmission multiplier" )
		.Float( 0x54, "obstruction maximum bend" )
		.Float( 0x58, "occlusion maximum bend" )
		.Float( 0x5C, "inner silence distance" )
		.Float( 0x60, "low pass minimum distance" )
		.Float( 0x64, "low pass maximum distance" )
		.ByteData( 0x68, "function" )
		.Block( 0x70, "ext ducking", SoundClassDucking );

	// Halo 2's sound classes, one per slot of a sound tag's class.
	public static readonly TagStruct SoundClasses = new TagStruct( "sound classes", 0x8 )
		.Block( 0x0, "sound classes", SoundClass );

	// Placements share these fields; the palettes are tag references 0x28 bytes apart.
	static TagStruct ObjectPlacement( string name, int size, int palette ) => new TagStruct( name, size )
		.PaletteReference( 0x0, "type", palette )
		.Index( 0x2, "name" )
		.Flags32( 0x4, "placement flags", ["not automatically", "not on easy", "not on normal", "not on hard", "lock type to env. object", "lock transform to env. object", "never placed", "lock name to env. object", "create at rest"] )
		.Point3( 0x8, "position" )
		.Euler3( 0x14, "rotation" )
		.Float( 0x20, "scale" )
		.Flags16( 0x24, "transform flags", ["mirrored"] )
		.Flags16( 0x26, "manual bsp flags", ["bsp 0", "bsp 1", "bsp 2", "bsp 3", "bsp 4", "bsp 5", "bsp 6", "bsp 7", "bsp 8", "bsp 9", "bsp 10", "bsp 11", "bsp 12", "bsp 13", "bsp 14", "bsp 15"] )
		.Index( 0x2C, "origin bsp" )
		.Enum8( 0x2E, "object type", ObjectType )
		.Enum8( 0x2F, "source", ["structure", "editor", "dynamic", "legacy"] )
		.Enum8( 0x30, "bsp policy", ["default", "always placed", "manual bsp placement"] );

	static readonly string[] ActiveChangeColors = ["primary", "secondary", "tertiary", "quaternary"];
	static readonly string[] ItemPlacementFlags = ["initially at rest (does not fall)", "obsolete", "does accelerate (moves due to explosions)"];

	static readonly TagStruct ScenarioVehicle = ObjectPlacement( "scenario vehicle", 0x54, 0x78 )
		.StringId( 0x34, "variant name" )
		.Flags32( 0x38, "active change colors", ActiveChangeColors )
		.ColorArgbInt( 0x3C, "primary color" )
		.ColorArgbInt( 0x40, "secondary color" )
		.ColorArgbInt( 0x44, "tertiary color" )
		.ColorArgbInt( 0x48, "quaternary color" )
		.Float( 0x4C, "body vitality" )
		.Flags32( 0x50, "unit flags", ["dead", "closed", "not enterable by player"] );

	static readonly TagStruct ScenarioEquipment = ObjectPlacement( "scenario equipment", 0x38, 0x88 )
		.Flags32( 0x34, "equipment flags", ItemPlacementFlags );

	static readonly TagStruct ScenarioWeapon = ObjectPlacement( "scenario weapon", 0x54, 0x98 )
		.StringId( 0x34, "variant name" )
		.Flags32( 0x38, "active change colors", ActiveChangeColors )
		.ColorArgbInt( 0x3C, "primary color" )
		.ColorArgbInt( 0x40, "secondary color" )
		.ColorArgbInt( 0x44, "tertiary color" )
		.ColorArgbInt( 0x48, "quaternary color" )
		.Int16( 0x4C, "rounds left" )
		.Int16( 0x4E, "rounds loaded" )
		.Flags32( 0x50, "weapon flags", ItemPlacementFlags );

	static readonly TagStruct ScenarioCrate = ObjectPlacement( "scenario crate", 0x4C, 0x330 )
		.StringId( 0x34, "variant name" )
		.Flags32( 0x38, "active change colors", ActiveChangeColors )
		.ColorArgbInt( 0x3C, "primary color" )
		.ColorArgbInt( 0x40, "secondary color" )
		.ColorArgbInt( 0x44, "tertiary color" )
		.ColorArgbInt( 0x48, "quaternary color" );

	static readonly TagStruct ScenarioPlayerStartingProfile = new TagStruct( "scenario player starting profile", 0x44 )
		.String( 0x0, "name" )
		.Float( 0x20, "starting health damage" )
		.Float( 0x24, "starting shield damage" )
		.Reference( 0x28, "primary weapon" )
		.Int16( 0x30, "primary rounds loaded" )
		.Int16( 0x32, "primary rounds total" )
		.Reference( 0x34, "secondary weapon" )
		.Int16( 0x3C, "secondary rounds loaded" )
		.Int16( 0x3E, "secondary rounds total" )
		.UInt8( 0x40, "starting fragmentation grenade count" )
		.UInt8( 0x41, "starting plasma grenade count" )
		.UInt8( 0x42, "starting custom 2 grenade count" )
		.UInt8( 0x43, "starting custom 3 grenade count" );

	// A player spawn point, with the game types it is for and its spawn types.
	static readonly TagStruct ScenarioPlayerStartingLocation = new TagStruct( "scenario player starting location", 0x34 )
		.Point3( 0x0, "position" )
		.Angle( 0xC, "facing" )
		.Enum( 0x10, "team designator", TeamDesignator )
		.Index( 0x12, "bsp index" )
		.Enum( 0x14, "game type 1", GameType )
		.Enum( 0x16, "game type 2", GameType )
		.Enum( 0x18, "game type 3", GameType )
		.Enum( 0x1A, "game type 4", GameType )
		.Enum( 0x1C, "spawn type 0", SpawnType )
		.Enum( 0x1E, "spawn type 1", SpawnType )
		.Enum( 0x20, "spawn type 2", SpawnType )
		.Enum( 0x22, "spawn type 3", SpawnType )
		.StringId( 0x24, "unused name 1" )
		.StringId( 0x28, "unused name 2" )
		.Enum( 0x2C, "campaign player type", ["masterchief", "dervish", "chief multiplayer", "elite multiplayer"] );

	static readonly TagStruct ScenarioNetgameFlag = new TagStruct( "scenario netgame flag", 0x20 )
		.Point3( 0x0, "position" )
		.Angle( 0xC, "facing" )
		.Enum( 0x10, "type", ["ctf flag spawn", "ctf flag return", "assault bomb spawn", "assault bomb return", "oddball spawn", "unused", "race checkpoint", "teleporter (src)", "teleporter (dest)", "headhunter bin", "territories flag", "king hill 0", "king hill 1", "king hill 2", "king hill 3", "king hill 4", "king hill 5", "king hill 6", "king hill 7"] )
		.Enum( 0x12, "team designator", TeamDesignator )
		.Int16( 0x14, "identifier" )
		.Flags16( 0x16, "flags", ["multiple flag/bomb", "single flag/bomb", "neutral flag/bomb"] )
		.StringId( 0x18, "spawn object name" )
		.StringId( 0x1C, "spawn marker name" );

	// An item or vehicle spawn: what it spawns from (an itmc or vehc), for which game types, how often.
	static readonly TagStruct ScenarioNetgameEquipment = new TagStruct( "scenario netgame equipment", 0x90 )
		.Flags32( 0x0, "flags", ["levitate", "destroy existing on new spawn"] )
		.Enum( 0x4, "game type 1", GameType )
		.Enum( 0x6, "game type 2", GameType )
		.Enum( 0x8, "game type 3", GameType )
		.Enum( 0xA, "game type 4", GameType )
		.Int16( 0xC, "team index" )
		.Int16( 0xE, "spawn time" )
		.Int16( 0x10, "respawn on empty time" )
		.Enum( 0x12, "respawn timer starts", ["on pick up", "on body depletion"] )
		.Enum8( 0x14, "classification", ["weapon", "primary light land", "secondary light land", "primary heavy land", "primary flying", "secondary heavy land", "primary turret", "secondary turret", "grenade", "powerup"] )
		.Point3( 0x40, "position" )
		.Euler3( 0x4C, "orientation" )
		.Reference( 0x58, "item/vehicle collection" );

	static readonly TagStruct ScenarioStartingEquipment = new TagStruct( "scenario starting equipment", 0x9C )
		.Flags32( 0x0, "flags", ["no grenades", "plasma grenades"] )
		.Enum( 0x4, "game type 1", GameType )
		.Enum( 0x6, "game type 2", GameType )
		.Enum( 0x8, "game type 3", GameType )
		.Enum( 0xA, "game type 4", GameType )
		.Reference( 0x3C, "item collection 1" )
		.Reference( 0x44, "item collection 2" )
		.Reference( 0x4C, "item collection 3" )
		.Reference( 0x54, "item collection 4" )
		.Reference( 0x5C, "item collection 5" )
		.Reference( 0x64, "item collection 6" );

	static readonly TagStruct ScenarioStructureBsp = new TagStruct( "scenario structure bsp", 0x44 )
		.Reference( 0x10, "structure bsp" )
		.Reference( 0x18, "structure lightmap" )
		.Flags16( 0x3C, "flags", ["default sky enabled"] )
		.Index( 0x40, "default sky" );

	// A spawn influence the map sets in place of the multiplayer globals' one of the same type.
	static readonly TagStruct DynamicSpawnOverload = new TagStruct( "dynamic spawn overload", 0x10 )
		.Enum( 0x0, "overload type", ["enemy", "friend", "enemy vehicle", "friendly vehicle", "empty vehicle", "oddball inclusion", "oddball exclusion", "hill inclusion", "hill exclusion", "last race flag", "dead ally", "controlled territory"] )
		.Float( 0x4, "inner radius" )
		.Float( 0x8, "outer radius" )
		.Float( 0xC, "weight" );

	// Heights left at 0 use the defaults; no teams means every team, no games every game.
	static readonly TagStruct StaticSpawnZone = new TagStruct( "static spawn zone", 0x30 )
		.StringId( 0x0, "name" )
		.Flags32( 0x4, "relevant team", TeamDesignator )
		.Flags32( 0x8, "relevant games", ["all slayer", "all oddball", "all king", "all ctf", "all race", "all headhunter", "all juggernaut", "all territories"] )
		.Flags32( 0xC, "flags", ["disabled if flag home", "disabled if flag away", "disabled if bomb home", "disabled if bomb away"] )
		.Point3( 0x10, "position" )
		.Float( 0x1C, "lower height" )
		.Float( 0x20, "upper height" )
		.Float( 0x24, "inner radius" )
		.Float( 0x28, "outer radius" )
		.Float( 0x2C, "weight" );

	// Values that aren't 0 overload the multiplayer globals' dynamic spawn settings.
	static readonly TagStruct ScenarioSpawnData = new TagStruct( "scenario spawn data", 0x60 )
		.Float( 0x0, "dynamic spawn lower height" )
		.Float( 0x4, "dynamic spawn upper height" )
		.Float( 0x8, "game object reset height" )
		.Block( 0x48, "dynamic spawn overloads", DynamicSpawnOverload )
		.Block( 0x50, "static respawn zones", StaticSpawnZone )
		.Block( 0x58, "static initial spawn zones", StaticSpawnZone );

	// The parts of a scenario a multiplayer game reads (see TagExport.Map).
	public static readonly TagStruct Scenario = new TagStruct( "scenario", 0x3E0 )
		.Angle( 0x1C, "local north" )
		.Block( 0x70, "vehicles", ScenarioVehicle )
		.Block( 0x80, "equipment", ScenarioEquipment )
		.Block( 0x90, "weapons", ScenarioWeapon )
		.Block( 0xF8, "player starting profile", ScenarioPlayerStartingProfile )
		.Block( 0x100, "player starting locations", ScenarioPlayerStartingLocation )
		.Block( 0x118, "netgame flags", ScenarioNetgameFlag )
		.Block( 0x120, "netgame equipment", ScenarioNetgameEquipment )
		.Block( 0x128, "starting equipment", ScenarioStartingEquipment )
		.Block( 0x210, "structure bsps", ScenarioStructureBsp )
		.Block( 0x318, "spawn data", ScenarioSpawnData )
		.Block( 0x328, "crates", ScenarioCrate );
}
