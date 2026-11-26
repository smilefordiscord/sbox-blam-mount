using System.Linq;
using System.Text.Json;

namespace Blam.Halo1.TagData;

using static Blam.Halo1.TagData.TagLayouts.Options;

// Effects, item collections, globals, material effects (footsteps), HUD sounds, looping sounds,
// animation graphs, string lists and scenarios.
public static partial class TagLayouts
{
	static readonly string[] EffectCreateIn = ["any environment", "air only", "water only", "space only"];
	static readonly string[] ViolenceMode = ["either mode", "violent mode only", "nonviolent mode only"];

	static readonly TagStruct EffectLocation = new TagStruct( "effect location", 0x20 )
		.String( 0x0, "marker name" );

	static readonly TagStruct EffectPart = new TagStruct( "effect part", 0x68 )
		.Enum( 0x0, "create in", EffectCreateIn )
		.Enum( 0x2, "violence mode", ViolenceMode )
		.Index( 0x4, "location" )
		.Flags16( 0x6, "flags", ["face down regardless of location decals", "unused", "make effect work"] )
		.Reference( 0x18, "type" )
		.FloatBounds( 0x40, "velocity bounds" )
		.Angle( 0x48, "velocity cone angle" )
		.AngleBounds( 0x4C, "angular velocity bounds" )
		.FloatBounds( 0x54, "radius modifier bounds" )
		.Flags32( 0x60, "a scales values", ["velocity", "velocity delta", "velocity cone angle", "angular velocity", "angular velocity delta", "type specific scale"] )
		.Flags32( 0x64, "b scales values", ["velocity", "velocity delta", "velocity cone angle", "angular velocity", "angular velocity delta", "type specific scale"] );

	static readonly string[] ParticleScalesValues =
	[
		"velocity", "velocity delta", "velocity cone angle", "angular velocity", "angular velocity delta", "count",
		"count delta", "distribution radius", "distribution radius delta", "particle radius", "particle radius delta", "tint",
	];

	static readonly TagStruct EffectParticle = new TagStruct( "effect particle", 0xE8 )
		.Enum( 0x0, "create in", EffectCreateIn )
		.Enum( 0x2, "violence mode", ViolenceMode )
		.Enum( 0x4, "create", ["independent of camera mode", "only in first person", "only in third person", "in first person if possible"] )
		.Index( 0x8, "location" )
		.Euler2( 0xC, "relative direction" )
		.Point3( 0x14, "relative offset" )
		.Reference( 0x54, "particle type" )
		.Flags32( 0x64, "flags", ["stay attached to marker", "random initial angle", "tint from object color", "interpolate tint as hsv", "across the long hue path"] )
		.Enum( 0x68, "distribution function", ["start", "end", "constant", "buildup", "falloff", "buildup and falloff"] )
		.Int16Bounds( 0x6C, "count" )
		.FloatBounds( 0x70, "distribution radius" )
		.FloatBounds( 0x84, "velocity" )
		.Angle( 0x8C, "velocity cone angle" )
		.AngleBounds( 0x90, "angular velocity" )
		.FloatBounds( 0xA0, "radius" )
		.ColorArgb( 0xB0, "tint lower bound" )
		.ColorArgb( 0xC0, "tint upper bound" )
		.Flags32( 0xE0, "a scales values", ParticleScalesValues )
		.Flags32( 0xE4, "b scales values", ParticleScalesValues );

	static readonly TagStruct EffectEvent = new TagStruct( "effect event", 0x44 )
		.Float( 0x4, "skip fraction" )
		.FloatBounds( 0x8, "delay bounds" )
		.FloatBounds( 0x10, "duration bounds" )
		.Block( 0x2C, "parts", EffectPart )
		.Block( 0x38, "particles", EffectParticle );

	/// <summary>
	/// <c>effe</c>, 0x40 bytes: events, each creating parts (sounds, damage, lights, decals,
	/// objects, particle systems) and particles at the effect's locations (markers).
	/// </summary>
	public static readonly TagStruct Effect = new TagStruct( "effect", 0x40 )
		.Flags32( 0x0, "flags", ["deleted when attachment deactivates", "must be deterministic xbox", "must be deterministic pc", "disabled in anniversary by blood setting"] )
		.Index( 0x4, "loop start event" )
		.Index( 0x6, "loop stop event" )
		.Float( 0x8, "maximum damage radius" )
		.Block( 0x28, "locations", EffectLocation )
		.Block( 0x34, "events", EffectEvent );

	static readonly TagStruct ItemCollectionPermutation = new TagStruct( "item collection permutation", 0x54 )
		.Float( 0x20, "weight" )
		.Reference( 0x24, "item" );

	/// <summary>
	/// <c>itmc</c>, 0x5C bytes: the items a multiplayer item spawn picks from by weight. Spawn time
	/// is in seconds, 0 for the game's default (30).
	/// </summary>
	public static readonly TagStruct ItemCollection = new TagStruct( "item collection", 0x5C )
		.Block( 0x0, "permutations", ItemCollectionPermutation )
		.Int16( 0xC, "default spawn time" );

	static readonly TagStruct GlobalsSound = new TagStruct( "globals sound", 0x10 )
		.Reference( 0x0, "sound" );

	static readonly TagStruct GlobalsGrenade = new TagStruct( "globals grenade", 0x44 )
		.Int16( 0x0, "maximum count" )
		.Int16( 0x2, "mp spawn default" )
		.Reference( 0x4, "throwing effect" )
		.Reference( 0x14, "hud interface" )
		.Reference( 0x24, "equipment" )
		.Reference( 0x34, "projectile" );

	static readonly TagStruct GlobalsWeapon = new TagStruct( "globals weapon", 0x10 )
		.Reference( 0x0, "weapon" );

	static readonly TagStruct GlobalsCheatPowerup = new TagStruct( "globals cheat powerup", 0x10 )
		.Reference( 0x0, "powerup" );

	static readonly TagStruct GlobalsVehicle = new TagStruct( "globals vehicle", 0x10 )
		.Reference( 0x0, "vehicle" );

	/// <summary>
	/// The multiplayer globals. The sounds are the announcer and the multiplayer UI sounds, in the
	/// order the game plays them by.
	/// </summary>
	static readonly TagStruct GlobalsMultiplayerInformation = new TagStruct( "globals multiplayer information", 0xA0 )
		.Reference( 0x0, "flag" )
		.Reference( 0x10, "unit" )
		.Block( 0x20, "vehicles", GlobalsVehicle )
		.Reference( 0x2C, "hill shader" )
		.Reference( 0x3C, "flag shader" )
		.Reference( 0x4C, "ball" )
		.Block( 0x5C, "sounds", GlobalsSound );

	static readonly TagStruct GlobalsPlayerInformation = new TagStruct( "globals player information", 0xF4 )
		.Reference( 0x0, "unit" )
		.Float( 0x2C, "walking speed" )
		.Float( 0x30, "double speed multiplier" )
		.Float( 0x34, "run forward" )
		.Float( 0x38, "run backward" )
		.Float( 0x3C, "run sideways" )
		.Float( 0x40, "run acceleration" )
		.Float( 0x44, "sneak forward" )
		.Float( 0x48, "sneak backward" )
		.Float( 0x4C, "sneak sideways" )
		.Float( 0x50, "sneak acceleration" )
		.Float( 0x54, "airborne acceleration" )
		.Float( 0x58, "speed multiplier" )
		.Point3( 0x68, "grenade origin" )
		.Float( 0x80, "stun movement penalty" )
		.Float( 0x84, "stun turning penalty" )
		.Float( 0x88, "stun jumping penalty" )
		.Float( 0x8C, "minimum stun time" )
		.Float( 0x90, "maximum stun time" )
		.FloatBounds( 0x9C, "first person idle time" )
		.Float( 0xA4, "first person skip fraction" )
		.Reference( 0xB8, "coop respawn effect" );

	static readonly TagStruct GlobalsFirstPersonInterface = new TagStruct( "globals first person interface", 0xC0 )
		.Reference( 0x0, "first person hands" )
		.Reference( 0x10, "base bitmap" )
		.Reference( 0x20, "shield meter" )
		.Point2Int( 0x30, "shield meter origin" )
		.Reference( 0x34, "body meter" )
		.Point2Int( 0x44, "body meter origin" )
		.Reference( 0x48, "night vision on effect" )
		.Reference( 0x58, "night vision off effect" );

	/// <summary>
	/// Falling damage. The velocities at 0x8C are worked out from the distances when the map is
	/// built, in world units per tick.
	/// </summary>
	static readonly TagStruct GlobalsFallingDamage = new TagStruct( "globals falling damage", 0x98 )
		.FloatBounds( 0x8, "harmful falling distance" )
		.Reference( 0x10, "falling damage" )
		.Float( 0x28, "maximum falling distance" )
		.Reference( 0x2C, "distance damage" )
		.Reference( 0x3C, "vehicle environment collision damage" )
		.Reference( 0x4C, "vehicle killed unit damage" )
		.Reference( 0x5C, "vehicle collision damage" )
		.Reference( 0x6C, "flaming death damage" )
		.Float( 0x8C, "maximum falling velocity" )
		.FloatBounds( 0x90, "harmful falling velocity" );

	/// <summary>
	/// Particles a breakable surface of the material breaks into, made <c>density</c> world units
	/// apart over it (see <see cref="Tags.BreakableSurfaceParticles"/>).
	/// </summary>
	static readonly TagStruct BreakableSurfaceParticleEffect = new TagStruct( "breakable surface particle effect", 0x80 )
		.Reference( 0x0, "particle" )
		.Flags32( 0x10, "flags", ["interpolate color in hsv", "...more colors"] )
		.Float( 0x14, "density" )
		.FloatBounds( 0x18, "velocity scale" )
		.Angle( 0x20, "velocity cone angle" )
		.FloatBounds( 0x24, "angular velocity" )
		.FloatBounds( 0x34, "radius" )
		.ColorArgb( 0x44, "tint lower bound" )
		.ColorArgb( 0x54, "tint upper bound" );

	/// <summary>
	/// One per <see cref="Options.MaterialTypeNames"/>, in that order.
	/// </summary>
	static readonly TagStruct GlobalsMaterial = new TagStruct( "globals material", 0x374 )
		.Float( 0x94, "ground friction scale" )
		.Float( 0x98, "ground friction normal k1 scale" )
		.Float( 0x9C, "ground friction normal k0 scale" )
		.Float( 0xA0, "ground depth scale" )
		.Float( 0xA4, "ground damp fraction scale" )
		.Float( 0x2D4, "maximum vitality" )
		.Reference( 0x2E4, "effect" )
		.Reference( 0x2F4, "sound" )
		.Block( 0x31C, "particle effects", BreakableSurfaceParticleEffect )
		.Reference( 0x364, "melee hit sound" );

	static readonly TagStruct GlobalsInterfaceBitmaps = new TagStruct( "globals interface bitmaps", 0x130 )
		.Reference( 0x0, "font system" )
		.Reference( 0x10, "font terminal" )
		.Reference( 0x20, "screen color table" )
		.Reference( 0x30, "hud color table" )
		.Reference( 0x40, "editor color table" )
		.Reference( 0x50, "dialog color table" )
		.Reference( 0x60, "hud globals" )
		.Reference( 0x70, "motion sensor sweep bitmap" )
		.Reference( 0x80, "motion sensor sweep bitmap mask" )
		.Reference( 0x90, "multiplayer hud bitmap" )
		.Reference( 0xA0, "localization" )
		.Reference( 0xB0, "hud digits definition" )
		.Reference( 0xC0, "motion sensor blip bitmap" )
		.Reference( 0xD0, "interface goo map1" )
		.Reference( 0xE0, "interface goo map2" )
		.Reference( 0xF0, "interface goo map3" );

	static readonly TagStruct GlobalsPlayerControl = new TagStruct( "globals player control", 0x80 )
		.Float( 0x8, "magnetism inconsequential target scale" )
		.Angle( 0x70, "minimum angle for vehicle flipping" );

	static readonly TagStruct GlobalsCamera = new TagStruct( "globals camera", 0x10 )
		.Reference( 0x0, "default unit camera track" );

	/// <summary>
	/// The rasterizer data's cinematic video screen effect maps (the rest of the rasterizer data is
	/// left out).
	/// </summary>
	static readonly TagStruct GlobalsRasterizerData = new TagStruct( "globals rasterizer data", 0x1AC )
		.Reference( 0x11C, "video scanline map" )
		.Reference( 0x12C, "video noise map" );

	/// <summary>
	/// <c>matg</c>, 0x1AC bytes: the gameplay blocks, the difficulty values, the camera's default unit camera track, the
	/// player control's vehicle flipping angle, the interface bitmaps (the HUD globals, motion
	/// sensor and HUD digits) and the video screen effect's maps. The rest of player control,
	/// rasterizer data and playlists are left out.
	/// </summary>
	public static readonly TagStruct Globals = new TagStruct( "globals", 0x1AC )
		.Block( 0xF8, "sounds", GlobalsSound )
		.Block( 0x104, "camera", GlobalsCamera )
		.Block( 0x110, "player control", GlobalsPlayerControl )
		.Block( 0x11C, "difficulty", GlobalsDifficulty() )
		.Block( 0x128, "grenades", GlobalsGrenade )
		.Block( 0x134, "rasterizer data", GlobalsRasterizerData )
		.Block( 0x140, "interface bitmaps", GlobalsInterfaceBitmaps )
		.Block( 0x14C, "weapon list", GlobalsWeapon )
		.Block( 0x158, "cheat powerups", GlobalsCheatPowerup )
		.Block( 0x164, "multiplayer information", GlobalsMultiplayerInformation )
		.Block( 0x170, "player information", GlobalsPlayerInformation )
		.Block( 0x17C, "first person interface", GlobalsFirstPersonInterface )
		.Block( 0x188, "falling damage", GlobalsFallingDamage )
		.Block( 0x194, "materials", GlobalsMaterial );

	static readonly TagStruct MaterialEffectsMaterial = new TagStruct( "material effects material", 0x30 )
		.Reference( 0x0, "effect" )
		.Reference( 0x10, "sound" );

	static readonly TagStruct MaterialEffectsEffect = new TagStruct( "material effects effect", 0x1C )
		.Block( 0x0, "materials", MaterialEffectsMaterial );

	/// <summary>
	/// <c>foot</c>, 0x8C bytes: an effect and a sound per <see cref="Options.MaterialTypeNames"/>, for each
	/// kind of event (a biped's footsteps: walk, run, slide, shuffle, jump, land...).
	/// </summary>
	public static readonly TagStruct MaterialEffects = new TagStruct( "material effects", 0x8C )
		.Block( 0x0, "effects", MaterialEffectsEffect );

	static readonly TagStruct CameraTrackControlPoint = new TagStruct( "camera track control point", 0x3C )
		.Point3( 0x0, "position" )
		.Quaternion( 0xC, "orientation" );

	/// <summary>
	/// <c>trak</c>, 0x30 bytes: where a unit's third person camera sits around it, as control
	/// points (the vehicles' and the default unit camera tracks).
	/// </summary>
	public static readonly TagStruct CameraTrack = new TagStruct( "camera track", 0x30 )
		.Block( 0x4, "control points", CameraTrackControlPoint );

	static readonly TagStruct ContrailPointState = new TagStruct( "contrail point state", 0x68 )
		.FloatBounds( 0x0, "duration" )
		.FloatBounds( 0x8, "transition duration" )
		.Reference( 0x10, "physics" )
		.Float( 0x40, "width" )
		.ColorArgb( 0x44, "color lower bound" )
		.ColorArgb( 0x54, "color upper bound" )
		.Flags32( 0x64, "scale flags", ["duration", "duration delta", "transition duration", "transition duration delta", "width", "color"] );

	/// <summary>
	/// <c>cont</c>, 0x144 bytes: a trail of points laid behind a projectile or object, each aging
	/// through the point states.
	/// </summary>
	public static readonly TagStruct Contrail = new TagStruct( "contrail", 0x144 )
		.Flags16( 0x0, "flags", ["first point unfaded", "last point unfaded", "points start pinned to media", "points start pinned to ground", "points always pinned to media", "points always pinned to ground", "edge effect fades slowly"] )
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
		.Int16( 0x40, "first sequence index" )
		.Int16( 0x42, "sequence count" )
		.Enum( 0xAE, "framebuffer blend function", ["alpha blend", "multiply", "double multiply", "add", "subtract", "component min", "component max", "alpha multiply add"] )
		.Enum( 0xB0, "framebuffer fade mode", ["none", "fade when perpendicular", "fade when parallel"] )
		.Block( 0x138, "point states", ContrailPointState );

	/// <summary>
	/// <c>deca</c>, 0x10C bytes. Bullet holes, burns, blood and their glows; the mount's decal
	/// prefabs are built from them (see DecalPrefabs).
	/// </summary>
	public static readonly TagStruct Decal = new TagStruct( "decal", 0x10C )
		.Flags16( 0x0, "flags", ["geometry inherited by next decal in chain", "interpolate color in hsv", "more colors", "no random rotation", "water effect", "sapien snap to axis", "sapien incremental counter", "animation loop", "preserve aspect", "disabled by blood setting"] )
		.Enum( 0x2, "type", ["scratch", "splatter", "burn", "painted sign"] )
		.Enum( 0x4, "layer", ["primary", "secondary", "light", "alpha tested", "water"] )
		.Reference( 0x8, "next decal in chain" )
		.FloatBounds( 0x18, "radius" )
		.FloatBounds( 0x2C, "intensity" )
		.ColorRgb( 0x34, "color lower bounds" )
		.ColorRgb( 0x40, "color upper bounds" )
		.Int16( 0x58, "animation loop frame" )
		.Int16( 0x5A, "animation speed" )
		.FloatBounds( 0x78, "lifetime" )
		.FloatBounds( 0x80, "decay time" )
		.Enum( 0xC0, "framebuffer blend function", ["alpha blend", "multiply", "double multiply", "add", "subtract", "component min", "component max", "alpha multiply add"] )
		.Reference( 0xD8, "map" )
		.Float( 0xFC, "maximum sprite extent" );

	static readonly string[] SoundClass =
	[
		"projectile impact", "projectile detonation", "unused", "unused1", "weapon fire", "weapon ready",
		"weapon reload", "weapon empty", "weapon charge", "weapon overheat", "weapon idle", "unused2", "unused3",
		"object impacts", "particle impacts", "slow particle impacts", "unused4", "unused5", "unit footsteps",
		"unit dialog", "unused6", "unused7", "vehicle collision", "vehicle engine", "unused8", "unused9",
		"device door", "device force field", "device machinery", "device nature", "device computers", "unused10",
		"music", "ambient nature", "ambient machinery", "ambient computers", "unused11", "unused12", "unused13",
		"first person damage", "unused14", "unused15", "unused16", "unused17", "scripted dialog player",
		"scripted effect", "scripted dialog other", "scripted dialog force unspatialized", "unused18", "unused19",
		"game event",
	];

	static readonly TagStruct SoundPermutation = new TagStruct( "sound permutation", 0x7C )
		.String( 0x0, "name" )
		.Float( 0x20, "skip fraction" )
		.Float( 0x24, "gain" )
		.Enum( 0x28, "compression", ["none", "xbox adpcm", "ima adpcm", "ogg"] )
		.Index( 0x2A, "next permutation index" )
		.ByteData( 0x54, "mouth data" );

	static readonly TagStruct SoundPitchRange = new TagStruct( "sound pitch range", 0x48 )
		.String( 0x0, "name" )
		.Float( 0x20, "natural pitch" )
		.FloatBounds( 0x24, "bend bounds" )
		.Int16( 0x2C, "actual permutation count" )
		.Block( 0x3C, "permutations", SoundPermutation );

	/// <summary>
	/// <c>snd!</c>, 0xA4 bytes. The samples themselves come from MCC's FMOD banks, not the tag.
	/// The sound class sets how many of the sound may play at once, per definition and per object.
	/// </summary>
	public static readonly TagStruct Sound = new TagStruct( "sound", 0xA4 )
		.Flags32( 0x0, "flags", ["fit to adpcm blocksize", "split long sound into permutations", "thirsty grunt"] )
		.Enum( 0x4, "class", SoundClass )
		.Enum( 0x6, "sample rate", ["22khz", "44khz"] )
		.Float( 0x8, "minimum distance" )
		.Float( 0xC, "maximum distance" )
		.Float( 0x10, "skip fraction" )
		.FloatBounds( 0x14, "random pitch bounds" )
		.Angle( 0x1C, "inner cone angle" )
		.Angle( 0x20, "outer cone angle" )
		.Float( 0x24, "outer cone gain" )
		.Float( 0x28, "gain modifier" )
		.Float( 0x2C, "maximum bend per second" )
		.Float( 0x3C, "skip fraction modifier at zero" )
		.Float( 0x40, "gain modifier at zero" )
		.Float( 0x44, "pitch modifier at zero" )
		.Float( 0x54, "skip fraction modifier at one" )
		.Float( 0x58, "gain modifier at one" )
		.Float( 0x5C, "pitch modifier at one" )
		.Enum( 0x6C, "encoding", ["mono", "stereo"] )
		.Enum( 0x6E, "compression", ["none", "xbox adpcm", "ima adpcm", "ogg"] )
		.Reference( 0x70, "promotion sound" )
		.Int16( 0x80, "promotion count" )
		.Int32( 0x84, "longest permutation length" )
		.Block( 0x98, "pitch ranges", SoundPitchRange );

	static readonly TagStruct SoundLoopingTrack =new TagStruct( "sound looping track", 0xA0 )
		.Flags32( 0x0, "flags", ["fade in at start", "fade out at stop", "fade in alternate"] )
		.Float( 0x4, "gain" )
		.Float( 0x8, "fade in duration" )
		.Float( 0xC, "fade out duration" )
		.Reference( 0x30, "start" )
		.Reference( 0x40, "loop" )
		.Reference( 0x50, "end" )
		.Reference( 0x80, "alternate loop" )
		.Reference( 0x90, "alternate end" );

	static readonly TagStruct SoundLoopingDetail = new TagStruct( "sound looping detail", 0x68 )
		.Reference( 0x0, "sound" )
		.FloatBounds( 0x10, "random period bounds" )
		.Float( 0x18, "gain" )
		.Flags32( 0x1C, "flags", ["don't play with alternate", "don't play without alternate"] )
		.AngleBounds( 0x50, "yaw bounds" )
		.AngleBounds( 0x58, "pitch bounds" )
		.FloatBounds( 0x60, "distance bounds" );

	/// <summary>
	/// <c>lsnd</c>, 0x54 bytes.
	/// </summary>
	public static readonly TagStruct SoundLooping = new TagStruct( "sound looping", 0x54 )
		.Flags32( 0x0, "flags", ["deafening to ais", "not a loop", "stops music", "siege of madrigal"] )
		.Float( 0x4, "zero detail sound period" )
		.Float( 0x10, "one detail sound period" )
		.Float( 0x20, "maximum distance" )
		.Reference( 0x2C, "continuous damage effect" )
		.Block( 0x3C, "tracks", SoundLoopingTrack )
		.Block( 0x48, "detail sounds", SoundLoopingDetail );

	static readonly TagStruct AnimationSoundReference = new TagStruct( "animation sound reference", 0x14 )
		.Reference( 0x0, "sound" );

	/// <summary>
	/// An animation's description, without its frames (the mount builds those into models).
	/// </summary>
	static readonly TagStruct Animation = new TagStruct( "animation", 0xB4 )
		.String( 0x0, "name" )
		.Enum( 0x20, "type", ["base", "overlay", "replacement"] )
		.UInt16( 0x22, "frame count" )
		.UInt16( 0x24, "frame size" )
		.Enum( 0x26, "frame info type", ["none", "dx dy", "dx dy dyaw", "dx dy dz dyaw"] )
		.UInt16( 0x2C, "node count" )
		.Index( 0x2E, "loop frame index" )
		.Float( 0x30, "weight" )
		.Index( 0x34, "key frame index" )
		.Index( 0x36, "second key frame index" )
		.Index( 0x38, "next animation" )
		.Flags16( 0x3A, "flags", ["compressed data", "world relative", "25hz pal"] )
		.Index( 0x3C, "sound" )
		.Index( 0x3E, "sound frame index" )
		.Int8( 0x40, "left foot frame index" )
		.Int8( 0x41, "right foot frame index" );

	/// <summary>
	/// The animation slots of a unit seat, in tag order ("acc-" are the acceleration overlays).
	/// </summary>
	static readonly string[] UnitSeatSlots =
	[
		"airborne-dead", "landing-dead", "acc-front-back", "acc-left-right", "acc-up-down", "push", "twist", "enter",
		"exit", "look", "talk", "emotions", "unused0", "user0", "user1", "user2", "user3", "user4", "user5", "user6",
		"user7", "user8", "user9", "flying-front", "flying-back", "flying-left", "flying-right", "opening", "closing",
		"turning",
	];

	/// <summary>
	/// The animation slots of a weapon type (a specific weapon under a weapon class), in tag order.
	/// </summary>
	static readonly string[] WeaponTypeSlots =
	[
		"reload-1", "reload-2", "chamber-1", "chamber-2", "fire-1", "fire-2", "charged-1", "charged-2", "melee", "overheat",
	];

	/// <summary>
	/// The animation slots of a vehicle, in tag order.
	/// </summary>
	static readonly string[] VehicleSlots = ["steering", "roll", "throttle", "velocity", "braking", "ground-speed", "occupied", "unoccupied"];

	/// <summary>
	/// A grid of aiming (or looking, or steering) overlay frames: how many frames there are each way
	/// from the middle, and the angle each frame covers.
	/// </summary>
	static readonly TagStruct AnimationScreenBounds = new TagStruct( "animation screen bounds", 0x18 )
		.Angle( 0x0, "right yaw per frame" )
		.Angle( 0x4, "left yaw per frame" )
		.UInt16( 0x8, "right frame count" )
		.UInt16( 0xA, "left frame count" )
		.Angle( 0xC, "down pitch per frame" )
		.Angle( 0x10, "up pitch per frame" )
		.UInt16( 0x14, "down pitch frame count" )
		.UInt16( 0x16, "up pitch frame count" );

	static TagStruct AnimationSlot( string name, string[] slots ) => new TagStruct( name, 0x2 ) { SlotNames = slots }
		.Index( 0x0, "animation" );

	/// <summary>
	/// Writes the name of the animation an int16 animation index (at <paramref name="indexOffset"/> in the
	/// structure) picks, as "AnimationName" (null for none).
	/// </summary>
	static void WriteAnimationName( Utf8JsonWriter json, LayoutJsonWriter writer, uint address, uint indexOffset )
	{
		var index = writer.Data.I16( address + indexOffset );
		var animation = index < 0 ? 0 : writer.BlockElements( writer.TagAddress + 0x74, Animation.Size ).ElementAtOrDefault( index );
		if ( animation == 0 )
			json.WriteNull( "AnimationName" );
		else
			json.WriteString( "AnimationName", writer.Data.CString( animation, 32 ) );
	}

	static readonly TagStruct AnimationObjectOverlay = new TagStruct( "animation object overlay", 0x14 ) { Extra = ( json, writer, a ) => WriteAnimationName( json, writer, a, 0x0 ) }
		.Index( 0x0, "animation" )
		.Enum( 0x2, "function", ["a out", "b out", "c out", "d out"] )
		.Enum( 0x4, "function controls", ["frame", "scale"] );

	static readonly TagStruct AnimationWeaponType = new TagStruct( "animation weapon type", 0x3C )
		.String( 0x0, "label" )
		.Block( 0x30, "animations", AnimationSlot( "weapon type animation", WeaponTypeSlots ) );

	static readonly TagStruct AnimationWeaponClass = new TagStruct( "animation weapon class", 0xBC )
		.String( 0x0, "name" )
		.String( 0x20, "grip marker" )
		.String( 0x40, "hand marker" )
		.Struct( 0x60, "aiming screen bounds", AnimationScreenBounds )
		.Block( 0x98, "animations", AnimationSlot( "weapon class animation", Blam.Halo1.Tags.UnitSeat.WeaponClassSlots ) )
		.Block( 0xB0, "weapon types", AnimationWeaponType );

	static readonly TagStruct AnimationUnitSeat = new TagStruct( "animation unit seat", 0x64 )
		.String( 0x0, "label" )
		.Struct( 0x20, "looking screen bounds", AnimationScreenBounds )
		.Block( 0x40, "animations", AnimationSlot( "unit seat animation", UnitSeatSlots ) )
		.Block( 0x58, "weapons", AnimationWeaponClass );

	static readonly TagStruct AnimationSuspension = new TagStruct( "suspension animation", 0x14 ) { Extra = ( json, writer, a ) => WriteAnimationName( json, writer, a, 0x2 ) }
		.Index( 0x0, "mass point index" )
		.Index( 0x2, "animation" )
		.Float( 0x4, "full extension ground depth" )
		.Float( 0x8, "full compression ground depth" );

	static readonly TagStruct AnimationVehicle = new TagStruct( "animation vehicle", 0x74 )
		.Struct( 0x0, "steering screen bounds", AnimationScreenBounds )
		.Block( 0x5C, "animations", AnimationSlot( "vehicle animation", VehicleSlots ) )
		.Block( 0x68, "suspension animations", AnimationSuspension );

	/// <summary>
	/// A device's slots: its position animation, from its first frame at position 0 to its last at
	/// position 1, and its power animation.
	/// </summary>
	static readonly TagStruct AnimationDevice = new TagStruct( "animation device", 0x60 )
		.Block( 0x54, "animations", AnimationSlot( "device animation", ["position", "power"] ) );

	/// <summary>
	/// <c>antr</c>, 0x80 bytes: the animations and the sounds they play, and the slots objects,
	/// units, vehicles and devices play them from. Slot blocks hold an animation index (null for none) per
	/// slot, each named as "Slot". Screen bounds give an overlay's frame grid: frames are yaw-major,
	/// from full right (or down) to full left (or up), and the angles are per frame.
	/// </summary>
	public static readonly TagStruct ModelAnimations = new TagStruct( "model animations", 0x80 )
		.Block( 0x0, "objects", AnimationObjectOverlay )
		.Block( 0xC, "units", AnimationUnitSeat )
		.Block( 0x24, "vehicles", AnimationVehicle )
		.Block( 0x30, "devices", AnimationDevice )
		.Block( 0x3C, "unit damage", AnimationUnitDamage() )
		.Block( 0x54, "sound references", AnimationSoundReference )
		.Block( 0x74, "animations", Animation );

	static readonly TagStruct StringListString = new TagStruct( "string", 0x14 )
		.AsciiData( 0x0, "string" );

	static readonly TagStruct UnicodeStringListString = new TagStruct( "unicode string", 0x14 )
		.Utf16Data( 0x0, "string" );

	/// <summary>
	/// <c>str#</c>, 0xC bytes.
	/// </summary>
	public static readonly TagStruct StringList = new TagStruct( "string list", 0xC )
		.Block( 0x0, "strings", StringListString );

	/// <summary>
	/// <c>ustr</c>, 0xC bytes.
	/// </summary>
	public static readonly TagStruct UnicodeStringList = new TagStruct( "unicode string list", 0xC )
		.Block( 0x0, "strings", UnicodeStringListString );

	static readonly string[] NotPlaced = ["automatically", "on easy", "on normal", "on hard", "use player appearance"];

	static readonly TagStruct ScenarioVehicle = new TagStruct( "scenario vehicle", 0x78 )
		.PaletteReference( 0x0, "tag", 0x24C )
		.Index( 0x2, "name" )
		.Flags16( 0x4, "not placed", NotPlaced )
		.Int16( 0x6, "desired permutation" )
		.Point3( 0x8, "position" )
		.Euler3( 0x14, "rotation" )
		.Int8( 0x24, "appearance player index" )
		.Float( 0x48, "body vitality" )
		.Flags32( 0x4C, "flags", ["dead"] )
		.Int8( 0x58, "multiplayer team index" )
		.Flags16( 0x5A, "multiplayer spawn flags", ["slayer default", "ctf default", "king default", "oddball default", "unused", "unused1", "unused2", "unused3", "slayer allowed", "ctf allowed", "king allowed", "oddball allowed", "unused4", "unused5", "unused6", "unused7"] );

	static readonly TagStruct ScenarioEquipment = new TagStruct( "scenario equipment", 0x28 )
		.PaletteReference( 0x0, "tag", 0x264 )
		.Index( 0x2, "name" )
		.Flags16( 0x4, "not placed", NotPlaced )
		.Int16( 0x6, "desired permutation" )
		.Point3( 0x8, "position" )
		.Euler3( 0x14, "rotation" )
		.Flags16( 0x22, "misc flags", ["initially at rest", "obsolete", "does accelerate"] )
		.Int8( 0x24, "appearance player index" );

	static readonly TagStruct ScenarioWeapon = new TagStruct( "scenario weapon", 0x5C )
		.PaletteReference( 0x0, "tag", 0x27C )
		.Index( 0x2, "name" )
		.Flags16( 0x4, "not placed", NotPlaced )
		.Int16( 0x6, "desired permutation" )
		.Point3( 0x8, "position" )
		.Euler3( 0x14, "rotation" )
		.Int8( 0x24, "appearance player index" )
		.Int16( 0x48, "rounds reserved" )
		.Int16( 0x4A, "rounds loaded" )
		.Flags16( 0x4C, "flags", ["initially at rest", "obsolete", "does accelerate"] );

	static readonly TagStruct ScenarioPalette = new TagStruct( "scenario palette entry", 0x30 )
		.Reference( 0x0, "name" );

	static readonly TagStruct PlayerStartingLocation = new TagStruct( "player starting location", 0x34 )
		.Point3( 0x0, "position" )
		.Angle( 0xC, "facing" )
		.Index( 0x10, "team index" )
		.Index( 0x12, "bsp index" )
		.Enum( 0x14, "type 0", GameType )
		.Enum( 0x16, "type 1", GameType )
		.Enum( 0x18, "type 2", GameType )
		.Enum( 0x1A, "type 3", GameType );

	/// <summary>
	/// Netgame flags: CTF flag and vehicle spots, oddball spawns, race checkpoints, teleporters and
	/// hills. The usage id is the team (CTF), the teleporter pair, or the checkpoint or hill number.
	/// </summary>
	static readonly TagStruct NetgameFlag = new TagStruct( "netgame flag", 0x94 )
		.Point3( 0x0, "position" )
		.Angle( 0xC, "facing" )
		.Enum( 0x10, "type", ["ctf flag", "ctf vehicle", "oddball ball spawn", "race track", "race vehicle", "vegas bank", "teleport from", "teleport to", "hill flag"] )
		.Int16( 0x12, "usage id" )
		.Reference( 0x14, "weapon group" );

	static readonly TagStruct NetgameEquipment = new TagStruct( "netgame equipment", 0x90 )
		.Flags32( 0x0, "flags", ["levitate"] )
		.Enum( 0x4, "type 0", GameType )
		.Enum( 0x6, "type 1", GameType )
		.Enum( 0x8, "type 2", GameType )
		.Enum( 0xA, "type 3", GameType )
		.Index( 0xC, "team index" )
		.Int16( 0xE, "spawn time" )
		.Point3( 0x40, "position" )
		.Angle( 0x4C, "facing" )
		.Reference( 0x50, "item collection" );

	static readonly TagStruct StartingEquipment = new TagStruct( "starting equipment", 0xCC )
		.Flags32( 0x0, "flags", ["no grenades", "plasma grenades only", "type2 grenades only", "type3 grenades only"] )
		.Enum( 0x4, "type 0", GameType )
		.Enum( 0x6, "type 1", GameType )
		.Enum( 0x8, "type 2", GameType )
		.Enum( 0xA, "type 3", GameType )
		.Reference( 0x3C, "item collection 1" )
		.Reference( 0x4C, "item collection 2" )
		.Reference( 0x5C, "item collection 3" )
		.Reference( 0x6C, "item collection 4" )
		.Reference( 0x7C, "item collection 5" )
		.Reference( 0x8C, "item collection 6" );

	/// <summary>
	/// <c>scnr</c>, 0x5B0 bytes: what a multiplayer game needs from a level. Placed vehicles,
	/// equipment and weapons get the tag from their palette entry as <c>Tag</c>.
	/// </summary>
	public static readonly TagStruct Scenario = new TagStruct( "scenario", 0x5B0 )
		.Block( 0x240, "vehicles", ScenarioVehicle )
		.Block( 0x24C, "vehicle palette", ScenarioPalette )
		.Block( 0x258, "equipment", ScenarioEquipment )
		.Block( 0x264, "equipment palette", ScenarioPalette )
		.Block( 0x270, "weapons", ScenarioWeapon )
		.Block( 0x27C, "weapon palette", ScenarioPalette )
		.Block( 0x354, "player starting locations", PlayerStartingLocation )
		.Block( 0x378, "netgame flags", NetgameFlag )
		.Block( 0x384, "netgame equipment", NetgameEquipment )
		.Block( 0x390, "starting equipment", StartingEquipment );
}
