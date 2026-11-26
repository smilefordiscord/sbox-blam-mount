using System.Text.Json;

namespace Blam.Halo2.TagData;

using static Blam.Halo2.TagData.TagLayouts.Options;

// The globals (matg) and the multiplayer globals (mulg).
public static partial class TagLayouts
{
	const int DifficultyValueCount = 35;
	const int DifficultyLevelCount = 4;

	static readonly TagStruct GlobalsCollisionDamage = new TagStruct( "globals collision damage", 0x48 )
		.Reference( 0x0, "collision damage" )
		.Float( 0x8, "minimum game acceleration" )
		.Float( 0xC, "maximum game acceleration" )
		.Float( 0x10, "minimum game scale" )
		.Float( 0x14, "maximum game scale" )
		.Float( 0x18, "minimum absolute acceleration" )
		.Float( 0x1C, "maximum absolute acceleration" )
		.Float( 0x20, "minimum absolute scale" )
		.Float( 0x24, "maximum absolute scale" );

	static readonly TagStruct GlobalsArmorModifier = new TagStruct( "globals armor modifier", 0x8 )
		.StringId( 0x0, "name" )
		.Float( 0x4, "damage multiplier" );

	// A damage group (a damage effect's general damage) and its multipliers by armor (a material's general armor).
	static readonly TagStruct GlobalsDamageGroup = new TagStruct( "globals damage group", 0xC )
		.StringId( 0x0, "name" )
		.Block( 0x4, "armor modifiers", GlobalsArmorModifier );

	static readonly TagStruct GlobalsDamageTable = new TagStruct( "globals damage table", 0x8 )
		.Block( 0x0, "damage groups", GlobalsDamageGroup );

	static readonly TagStruct GlobalsCamera = new TagStruct( "globals camera", 0x14 )
		.Reference( 0x0, "default unit camera track" )
		.Float( 0x8, "default change pause" )
		.Float( 0xC, "first person change pause" )
		.Float( 0x10, "following camera change pause" );

	static readonly TagStruct GlobalsLookFunction = new TagStruct( "globals look function", 0x4 )
		.Float( 0x0, "scale" );

	// How the player's controls turn into aiming: look rates, acceleration and autoleveling.
	static readonly TagStruct GlobalsPlayerControl = new TagStruct( "globals player control", 0x80 )
		.Float( 0x0, "magnetism friction" )
		.Float( 0x4, "magnetism adhesion" )
		.Float( 0x8, "inconsequential target scale" )
		.Point2( 0x18, "crosshair location" )
		.Float( 0x20, "seconds to start" )
		.Float( 0x24, "seconds to full speed" )
		.Float( 0x28, "decay rate" )
		.Float( 0x2C, "full speed multiplier" )
		.Float( 0x30, "pegged magnitude" )
		.Float( 0x34, "pegged angular threshold" )
		.Float( 0x40, "look default pitch rate" )
		.Float( 0x44, "look default yaw rate" )
		.Float( 0x48, "look peg threshold" )
		.Float( 0x4C, "look yaw acceleration time" )
		.Float( 0x50, "look yaw acceleration scale" )
		.Float( 0x54, "look pitch acceleration time" )
		.Float( 0x58, "look pitch acceleration scale" )
		.Float( 0x5C, "look autoleveling scale" )
		.Float( 0x68, "gravity scale" )
		.Int16( 0x6E, "minimum autoleveling ticks" )
		.Angle( 0x70, "minimum angle for vehicle flipping" )
		.Block( 0x74, "look function", GlobalsLookFunction )
		.Float( 0x7C, "minimum action hold time" );

	// The difficulty block holds a row of four floats (easy, normal, heroic, legendary) for each of 35 difficulty values.
	// It is written as Values: [[easy, normal, heroic, legendary], ...] in Halo's order.
	static readonly TagStruct GlobalsDifficulty = new TagStruct( "globals difficulty", 0x284 ) { Extra = WriteDifficultyValues };

	static readonly TagStruct GlobalsGrenade = new TagStruct( "globals grenade", 0x2C )
		.Int16( 0x0, "maximum count" )
		.Reference( 0x4, "throwing effect" )
		.Reference( 0x1C, "equipment" )
		.Reference( 0x24, "projectile" );

	static readonly TagStruct GlobalsInterfaceTags = new TagStruct( "globals interface tags", 0x98 )
		.Reference( 0x0, "spinner" )
		.Reference( 0x10, "screen color table" )
		.Reference( 0x18, "hud color table" )
		.Reference( 0x20, "editor color table" )
		.Reference( 0x28, "dialog color table" )
		.Reference( 0x30, "hud globals" )
		.Reference( 0x38, "motion sensor sweep bitmap" )
		.Reference( 0x40, "motion sensor sweep bitmap mask" )
		.Reference( 0x48, "multiplayer hud bitmap" )
		.Reference( 0x58, "hud digits definition" )
		.Reference( 0x60, "motion sensor blip bitmap" )
		.Reference( 0x68, "interface goo map 1" )
		.Reference( 0x70, "interface goo map 2" )
		.Reference( 0x78, "interface goo map 3" )
		.Reference( 0x80, "mainmenu ui globals" )
		.Reference( 0x88, "singleplayer ui globals" )
		.Reference( 0x90, "multiplayer ui globals" );

	static readonly TagStruct GlobalsWeapon = new TagStruct( "globals weapon", 0x8 )
		.Reference( 0x0, "weapon" );

	static readonly TagStruct GlobalsCheatPowerup = new TagStruct( "globals cheat powerup", 0x8 )
		.Reference( 0x0, "powerup" );

	static readonly TagStruct GlobalsVehicle = new TagStruct( "globals vehicle", 0x8 )
		.Reference( 0x0, "vehicle" );

	static readonly TagStruct GlobalsSound = new TagStruct( "globals sound", 0x8 )
		.Reference( 0x0, "sound" );

	static readonly TagStruct MultiplayerEventSound = new TagStruct( "multiplayer event sound permutation", 0x50 )
		.Flags16( 0x0, "sound flags", ["announcer sound"] )
		.Reference( 0x4, "english sound" )
		.Reference( 0xC, "japanese sound" )
		.Reference( 0x14, "german sound" )
		.Reference( 0x1C, "french sound" )
		.Reference( 0x24, "spanish sound" )
		.Reference( 0x2C, "italian sound" )
		.Reference( 0x34, "korean sound" )
		.Reference( 0x3C, "chinese sound" )
		.Reference( 0x44, "portuguese sound" )
		.Float( 0x4C, "probability" );

	// An announcer event of a game type: what's shown (string ids of the game's text) and said, and to whom.
	static TagStruct MultiplayerEvent( string[] events ) => new TagStruct( "multiplayer event", 0xA8 )
		.Flags16( 0x0, "flags", ["quantity message"] )
		.Enum( 0x2, "type", ["general", "flavor", "slayer", "capture the flag", "oddball", "option 5", "king of the hill", "option 7", "juggernaut", "territories", "assault"] )
		.Enum( 0x4, "event", events )
		.Enum( 0x6, "audience", ["cause player", "cause team", "effect player", "effect team", "all"] )
		.StringId( 0xC, "display string" )
		.Enum( 0x10, "required field", ["none", "cause player", "cause team", "effect player", "effect team"] )
		.Enum( 0x12, "excluded audience", ["none", "cause player", "cause team", "effect player", "effect team"] )
		.StringId( 0x14, "primary string" )
		.Int32( 0x18, "primary string duration" )
		.StringId( 0x1C, "plural display string" )
		.Float( 0x3C, "sound delay (announcer only)" )
		.Flags16( 0x40, "sound flags", ["announcer sound"] )
		.Reference( 0x44, "sound" )
		.Reference( 0x4C, "japanese sound" )
		.Reference( 0x54, "german sound" )
		.Reference( 0x5C, "french sound" )
		.Reference( 0x64, "spanish sound" )
		.Reference( 0x6C, "italian sound" )
		.Reference( 0x74, "korean sound" )
		.Reference( 0x7C, "chinese sound" )
		.Reference( 0x84, "portuguese sound" )
		.Block( 0xA0, "sound permutations", MultiplayerEventSound );

	static readonly TagStruct GeneralEvent = MultiplayerEvent( ["kill", "suicide", "kill teammate", "victory", "team victory", "option 5", "option 6", "1 min to win", "team 1 min to win", "30 secs to win", "team 30 secs to win", "player quit", "player joined", "killed by unknown", "30 minutes left", "15 minutes left", "5 minutes left", "1 minute left", "time expired", "game over", "respawn tick", "last respawn tick", "teleporter used", "player changed team", "player rejoined", "gained lead", "gained team lead", "lost lead", "lost team lead", "tied leader", "tied team leader", "round over", "30 seconds left", "10 seconds left", "kill (falling)", "kill (collision)", "kill (melee)", "sudden death", "player booted player", "kill (flag carrier)", "kill (bomb carrier)", "kill (sticky grenade)", "kill (sniper)", "kill (standard melee)", "boarded vehicle", "start team notice", "telefrag", "10 secs to win", "team 10 secs to win", "pre game over"] );
	static readonly TagStruct FlavorEvent = MultiplayerEvent( ["double kill", "triple kill", "killtacular", "killing spree", "running riot", "well placed kill", "broke killing spree", "kill frenzy", "killtrocity", "killimajaro", "15 in a row", "20 in a row", "25 in a row"] );
	static readonly TagStruct SlayerEvent = MultiplayerEvent( ["game start", "new target"] );
	static readonly TagStruct CtfEvent = MultiplayerEvent( ["game start", "flag taken", "flag dropped", "flag returned by player", "flag returned by timeout", "flag captured", "flag new defensive team", "flag return failure", "side switch tick", "side switch final tick", "side switch 30 seconds", "side switch 10 seconds", "flag contested", "flag capture failure"] );
	static readonly TagStruct OddballEvent = MultiplayerEvent( ["game start", "ball spawned", "ball picked up", "ball dropped", "ball reset", "ball tick"] );
	static readonly TagStruct KingEvent = MultiplayerEvent( ["game start", "hill controlled", "hill contested", "hill tick", "hill move", "hill controlled team", "hill contested team"] );
	static readonly TagStruct JuggernautEvent = MultiplayerEvent( ["game start", "new juggernaut", "juggernaut killed"] );
	static readonly TagStruct TerritoriesEvent = MultiplayerEvent( ["game start", "territory control gained", "territory contest lost", "all territories controlled", "team territory control gained", "team territory control lost", "team all territories controlled"] );
	static readonly TagStruct AssaultEvent = MultiplayerEvent( ["game start", "bomb taken", "bomb dropped", "bomb returned by player", "bomb returned by timeout", "bomb captured", "bomb new defensive team", "bomb return failure", "side switch tick", "side switch final tick", "side switch 30 seconds", "side switch 10 seconds", "bomb returned by defusing", "bomb placed on enemy post", "bomb arming started", "bomb arming completed", "bomb contested"] );

	static readonly TagStruct GlobalsMultiplayerInformation = new TagStruct( "globals multiplayer information", 0x98 )
		.Reference( 0x0, "flag" )
		.Reference( 0x8, "unit" )
		.Block( 0x10, "vehicles", GlobalsVehicle )
		.Reference( 0x18, "hill shader" )
		.Reference( 0x20, "flag shader" )
		.Reference( 0x28, "ball" )
		.Block( 0x30, "sounds", GlobalsSound )
		.Reference( 0x38, "in game text" )
		.Block( 0x68, "general events", GeneralEvent )
		.Block( 0x70, "slayer events", SlayerEvent )
		.Block( 0x78, "ctf events", CtfEvent )
		.Block( 0x80, "oddball events", OddballEvent )
		.Block( 0x90, "king events", KingEvent );

	// The player's movement speeds and accelerations, and how stuns slow the player.
	static readonly TagStruct GlobalsPlayerInformation = new TagStruct( "globals player information", 0x11C )
		.Float( 0x24, "walking speed" )
		.Float( 0x2C, "run forward" )
		.Float( 0x30, "run backward" )
		.Float( 0x34, "run sideways" )
		.Float( 0x38, "run acceleration" )
		.Float( 0x3C, "sneak forward" )
		.Float( 0x40, "sneak backward" )
		.Float( 0x44, "sneak sideways" )
		.Float( 0x48, "sneak acceleration" )
		.Float( 0x4C, "airborne acceleration" )
		.Point3( 0x60, "grenade origin" )
		.Float( 0x78, "stun movement penalty" )
		.Float( 0x7C, "stun turning penalty" )
		.Float( 0x80, "stun jumping penalty" )
		.Float( 0x84, "minimum stun time" )
		.Float( 0x88, "maximum stun time" )
		.FloatBounds( 0x94, "first person idle time" )
		.Float( 0x9C, "first person skip fraction" )
		.Reference( 0xB0, "coop respawn effect" )
		.Int32( 0xB8, "binoculars zoom count" )
		.FloatBounds( 0xBC, "binoculars zoom range" )
		.Reference( 0xC4, "binoculars zoom in sound" )
		.Reference( 0xCC, "binoculars zoom out sound" )
		.Reference( 0xE4, "active camouflage on" )
		.Reference( 0xEC, "active camouflage off" )
		.Reference( 0xF4, "active camouflage error" )
		.Reference( 0xFC, "active camouflage ready" )
		.Reference( 0x104, "flashlight on" )
		.Reference( 0x10C, "flashlight off" )
		.Reference( 0x114, "ice cream" );

	static readonly TagStruct GlobalsPlayerRepresentation = new TagStruct( "globals player representation", 0xBC )
		.Reference( 0x0, "first person hands" )
		.Reference( 0x8, "first person body" )
		.Reference( 0xB0, "third person unit" )
		.StringId( 0xB8, "third person variant" );

	static readonly TagStruct GlobalsFallingDamage = new TagStruct( "globals falling damage", 0x68 )
		.FloatBounds( 0x8, "harmful falling distance" )
		.Reference( 0x10, "falling damage" )
		.Float( 0x20, "maximum falling distance" )
		.Reference( 0x24, "distance damage" )
		.Reference( 0x2C, "vehicle environment collision damage effect" )
		.Reference( 0x34, "vehicle killed unit damage effect" )
		.Reference( 0x3C, "vehicle collision damage" )
		.Reference( 0x44, "flaming death damage" )
		.Float( 0x5C, "maximum falling velocity" )
		.FloatBounds( 0x60, "damage velocity bounds" );

	// A global material: what collision surfaces, projectile responses and damage name (by string id).
	static readonly TagStruct GlobalsMaterial = new TagStruct( "globals material", 0xB4 )
		.StringId( 0x0, "name" )
		.StringId( 0x4, "parent name" )
		.Flags16( 0xA, "flags", ["flammable", "biomass"] )
		.Enum( 0xC, "old material type", MaterialType )
		.StringId( 0x10, "general armor" )
		.StringId( 0x14, "specific armor" )
		.Float( 0x1C, "friction" )
		.Float( 0x20, "restitution" )
		.Float( 0x24, "density" )
		.Reference( 0x30, "breakable surface" )
		.Reference( 0x38, "sound sweetener (small)" )
		.Reference( 0x40, "sound sweetener (medium)" )
		.Reference( 0x48, "sound sweetener (large)" )
		.Reference( 0x50, "sound sweetener rolling" )
		.Reference( 0x58, "sound sweetener grinding" )
		.Reference( 0x60, "sound sweetener (melee)" )
		.Reference( 0x70, "effect sweetener (small)" )
		.Reference( 0x78, "effect sweetener (medium)" )
		.Reference( 0x80, "effect sweetener (large)" )
		.Reference( 0x88, "effect sweetener rolling" )
		.Reference( 0x90, "effect sweetener grinding" )
		.Reference( 0x98, "effect sweetener (melee)" )
		.Flags32( 0xA8, "sweetener inheritance flags", ["sound small", "sound medium", "sound large", "sound rolling", "sound grinding", "sound melee", "bit 6", "effect small", "effect medium", "effect large", "effect rolling", "effect grinding", "effect melee"] )
		.Reference( 0xAC, "material effects" );

	static readonly TagStruct Color = new TagStruct( "color", 0xC )
		.ColorRgb( 0x0, "color" );

	static readonly TagStruct GlobalsMultiplayerUi = new TagStruct( "globals multiplayer ui", 0x20 )
		.Reference( 0x0, "random player names" )
		.Block( 0x10, "team colors", Color )
		.Reference( 0x18, "team names" );

	// The globals' sound tags: Halo 2's own sounds play by the legacy sound classes, MCC's remastered ones by the others.
	static readonly TagStruct GlobalsSoundGlobals = new TagStruct( "globals sound globals", 0x2C )
		.Reference( 0x0, "remastered sound classes" )
		.Reference( 0x8, "legacy sound classes" )
		.Reference( 0x10, "sound effects" )
		.Reference( 0x18, "sound mix" )
		.Reference( 0x20, "sound combat dialogue constants" );

	// The rasterizer's function textures; sphere lights fall off by the distance attenuation volume (see LightFalloff).
	static readonly TagStruct GlobalsRasterizerData = new TagStruct( "globals rasterizer data", 0x108 )
		.Reference( 0x0, "distance attenuation" )
		.Reference( 0x8, "vector normalization" )
		.Reference( 0x10, "gradients" )
		.Reference( 0x30, "glow" );

	public static readonly TagStruct Globals = new TagStruct( "globals", 0x284 )
		.Block( 0xB8, "collision damage", GlobalsCollisionDamage )
		.Block( 0xC0, "sound globals", GlobalsSoundGlobals )
		.Block( 0xD0, "damage table", GlobalsDamageTable )
		.Block( 0xE8, "camera", GlobalsCamera )
		.Block( 0xF0, "player control", GlobalsPlayerControl )
		.Block( 0xF8, "difficulty", GlobalsDifficulty )
		.Block( 0x100, "grenades", GlobalsGrenade )
		.Block( 0x108, "rasterizer data", GlobalsRasterizerData )
		.Block( 0x110, "interface tags", GlobalsInterfaceTags )
		.Block( 0x118, "weapon list", GlobalsWeapon )
		.Block( 0x120, "cheat powerups", GlobalsCheatPowerup )
		.Block( 0x128, "multiplayer information", GlobalsMultiplayerInformation )
		.Block( 0x130, "player information", GlobalsPlayerInformation )
		.Block( 0x138, "player representation", GlobalsPlayerRepresentation )
		.Block( 0x140, "falling damage", GlobalsFallingDamage )
		.Block( 0x150, "materials", GlobalsMaterial )
		.Block( 0x158, "multiplayer ui", GlobalsMultiplayerUi )
		.Block( 0x160, "profile colors", Color )
		.Reference( 0x168, "multiplayer globals" );

	static void WriteDifficultyValues( Utf8JsonWriter json, LayoutJsonWriter writer, uint address )
	{
		json.WriteStartArray( "Values" );
		for ( var value = 0; value < DifficultyValueCount; value++ )
		{
			json.WriteStartArray();
			for ( var level = 0; level < DifficultyLevelCount; level++ )
			{
				var f = writer.Data.F32( address + (uint)((value * DifficultyLevelCount + level) * 4) );
				json.WriteNumberValue( float.IsFinite( f ) ? f : 0f );
			}
			json.WriteEndArray();
		}
		json.WriteEndArray();
	}

	static readonly TagStruct MultiplayerUniversal = new TagStruct( "multiplayer universal", 0x20 )
		.Reference( 0x0, "random player names" )
		.Reference( 0x8, "team names" )
		.Block( 0x10, "team colors", Color )
		.Reference( 0x18, "multiplayer text" );

	static readonly TagStruct MultiplayerWeapon = new TagStruct( "multiplayer weapon", 0x8 )
		.Reference( 0x0, "weapon" );

	static readonly TagStruct MultiplayerVehicle = new TagStruct( "multiplayer vehicle", 0x8 )
		.Reference( 0x0, "vehicle" );

	static readonly TagStruct MultiplayerGrenade = new TagStruct( "multiplayer grenade", 0x8 )
		.Reference( 0x0, "grenade" );

	static readonly TagStruct MultiplayerPowerup = new TagStruct( "multiplayer powerup", 0x8 )
		.Reference( 0x0, "equipment" );

	static readonly TagStruct MultiplayerSound = new TagStruct( "multiplayer sound", 0x8 )
		.Reference( 0x0, "sound" );

	// One kind of spawn influence: its inner and outer radius and its weight.
	static readonly TagStruct SpawnInfluence = new TagStruct( "spawn influence", 0x1C )
		.Float( 0x0, "inner radius" )
		.Float( 0x4, "outer radius" )
		.Float( 0x8, "weight" );

	static readonly TagStruct MultiplayerConstants = new TagStruct( "multiplayer constants", 0x160 )
		.Float( 0x0, "maximum random spawn bias" )
		.Float( 0x4, "teleporter recharge time" )
		.Float( 0x8, "grenade danger weight" )
		.Float( 0xC, "grenade danger inner radius" )
		.Float( 0x10, "grenade danger outer radius" )
		.Float( 0x14, "grenade danger lead time" )
		.Float( 0x18, "vehicle danger minimum speed" )
		.Float( 0x1C, "vehicle danger weight" )
		.Float( 0x20, "vehicle danger radius" )
		.Float( 0x24, "vehicle danger lead time" )
		.Float( 0x28, "vehicle nearby player distance" )
		.Reference( 0xC0, "hill shader" )
		.Float( 0xD8, "flag reset stop distance" )
		.Reference( 0xDC, "bomb explode effect" )
		.Reference( 0xE4, "bomb explode damage effect" )
		.Reference( 0xEC, "bomb defuse effect" )
		.StringId( 0xF4, "bomb defusal string" )
		.StringId( 0xF8, "blocked teleporter string" );

	static readonly TagStruct MultiplayerStateResponse = new TagStruct( "multiplayer state response", 0x1C )
		.Enum( 0x4, "state", ["waiting for space to clear", "observing", "respawning soon", "sitting out", "out of lives", "playing (winning)", "playing (tied)", "playing (losing)", "game over (won)", "game over (tied)", "game over (lost)", "you have flag", "enemy has flag", "flag not home", "carrying oddball", "you are juggernaut", "you control hill", "switching sides soon", "player recently started", "you have bomb", "flag contested", "bomb contested", "limited lives left (multiple)", "limited lives left (single)", "limited lives left (final)", "playing (winning, unlimited)", "playing (tied, unlimited)", "playing (losing, unlimited)"] )
		.StringId( 0x8, "free for all message" )
		.StringId( 0xC, "team message" )
		.Reference( 0x10, "unused" );

	// What multiplayer games use: the objects of each game type, the announcer, starting equipment and spawn influences.
	static readonly TagStruct MultiplayerRuntime = new TagStruct( "multiplayer runtime", 0x568 )
		.Reference( 0x0, "flag" )
		.Reference( 0x8, "ball" )
		.Reference( 0x10, "unit" )
		.Reference( 0x18, "flag shader" )
		.Reference( 0x20, "hill shader" )
		.Reference( 0x28, "head" )
		.Reference( 0x30, "juggernaut powerup" )
		.Reference( 0x38, "bomb" )
		.Block( 0x68, "weapons", MultiplayerWeapon )
		.Block( 0x70, "vehicles", MultiplayerVehicle )
		.Block( 0x78, "grenades", MultiplayerGrenade )
		.Block( 0x80, "powerups", MultiplayerPowerup )
		.Reference( 0x88, "in game text" )
		.Block( 0x90, "sounds", MultiplayerSound )
		.Block( 0x98, "general events", GeneralEvent )
		.Block( 0xA0, "flavor events", FlavorEvent )
		.Block( 0xA8, "slayer events", SlayerEvent )
		.Block( 0xB0, "ctf events", CtfEvent )
		.Block( 0xB8, "oddball events", OddballEvent )
		.Block( 0xC8, "king events", KingEvent )
		.Block( 0xD8, "juggernaut events", JuggernautEvent )
		.Block( 0xE0, "territories events", TerritoriesEvent )
		.Block( 0xE8, "assault events", AssaultEvent )
		.Reference( 0x110, "default item collection 1" )
		.Reference( 0x118, "default item collection 2" )
		.Int32( 0x120, "default frag grenade count" )
		.Int32( 0x124, "default plasma grenade count" )
		.Float( 0x150, "dynamic zone upper height" )
		.Float( 0x154, "dynamic zone lower height" )
		.Struct( 0x180, "enemy", SpawnInfluence )
		.Struct( 0x19C, "friend", SpawnInfluence )
		.Struct( 0x1B8, "enemy vehicle", SpawnInfluence )
		.Struct( 0x1D4, "friendly vehicle", SpawnInfluence )
		.Struct( 0x1F0, "empty vehicle", SpawnInfluence )
		.Struct( 0x20C, "oddball inclusion", SpawnInfluence )
		.Struct( 0x228, "oddball exclusion", SpawnInfluence )
		.Struct( 0x244, "hill inclusion", SpawnInfluence )
		.Struct( 0x260, "hill exclusion", SpawnInfluence )
		.Struct( 0x27C, "last race flag", SpawnInfluence )
		.Struct( 0x298, "dead ally", SpawnInfluence )
		.Struct( 0x2B4, "controlled territory", SpawnInfluence )
		.Block( 0x530, "multiplayer constants", MultiplayerConstants )
		.Block( 0x538, "state responses", MultiplayerStateResponse )
		.Reference( 0x540, "scoreboard hud definition" )
		.Reference( 0x548, "scoreboard emblem shader" )
		.Reference( 0x550, "scoreboard emblem bitmap" )
		.Reference( 0x558, "scoreboard dead emblem shader" )
		.Reference( 0x560, "scoreboard dead emblem bitmap" );

	public static readonly TagStruct MultiplayerGlobals = new TagStruct( "multiplayer globals", 0x10 )
		.Block( 0x0, "universal", MultiplayerUniversal )
		.Block( 0x8, "runtime", MultiplayerRuntime );
}
