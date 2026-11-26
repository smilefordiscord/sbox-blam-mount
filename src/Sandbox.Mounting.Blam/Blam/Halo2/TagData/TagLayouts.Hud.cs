using System.Linq;

namespace Blam.Halo2.TagData;

// The HUD: HUD definitions (nhdt), the HUD globals (hudg) and the bitmaps they draw from (bitm).
public static partial class TagLayouts
{
	// What a widget's inputs read: its shader's hud_input1..4 and its effects' inputs.
	// Values 0x40-0x43 are named after the scoreboard widgets that read them.
	static readonly string[] HudInput = Enumerable.Range( 0, 0x44 ).Select( i => i switch
	{
		0x00 => "zero",
		0x01 => "one",
		0x02 => "time",
		0x03 => "global hud fade",
		0x10 => "unit shield",
		0x11 => "unit body",
		0x12 => "unit autoaimed",
		0x13 => "unit has no grenades",
		0x14 => "unit frag grenade count",
		0x15 => "unit plasma grenade count",
		0x16 => "unit time on depleted shield",
		0x17 => "unit zoom fraction",
		0x18 => "unit camo value",
		0x20 => "parent shield",
		0x21 => "parent body",
		0x30 => "weapon clip ammo",
		0x31 => "weapon heat",
		0x32 => "weapon battery",
		0x33 => "weapon total ammo",
		0x34 => "weapon barrel spin",
		0x35 => "weapon overheated",
		0x36 => "weapon clip ammo fraction",
		0x37 => "weapon time on overheat",
		0x38 => "weapon battery fraction",
		0x39 => "weapon locking fraction",
		0x40 => "user score fraction",
		0x41 => "other user score fraction",
		0x42 => "user winning",
		0x43 => "bomb arming amount",
		_ => $"unused {i}",
	} ).ToArray();

	static readonly string[] HudUnitFlags =
	[
		"default", "grenade type is none", "grenade type is frag", "grenade type is plasma", "unit is single wielding",
		"unit is dual wielding", "unit is unzoomed", "unit is zoomed level 1", "unit is zoomed level 2", "grenades disabled",
		"binoculars enabled", "motion sensor enabled", "shield enabled", "dervish",
	];

	static readonly string[] HudExtraFlags = ["autoaim friendly", "autoaim plasma", "autoaim headshot", "autoaim vulnerable", "autoaim invincible"];

	static readonly string[] HudWeaponFlags =
	[
		"primary weapon", "secondary weapon", "backpack weapon", "age below cutoff", "clip below cutoff", "total below cutoff",
		"overheated", "out of ammo", "lock target available", "locking", "locked",
	];

	static readonly string[] HudGameEngineFlags =
	[
		"campaign solo", "campaign coop", "free for all", "team game", "user leading", "user not leading", "timed game",
		"untimed game", "other score valid", "other score invalid", "player is arming bomb", "player talking",
	];

	// Where a widget's offset is measured from, named by where MCC draws each anchor's widgets.
	static readonly string[] HudWidgetAnchor =
	[
		"top right", "top left", "bottom left", "bottom right", "center", "lock-on target", "top center", "bottom center", "left center",
		"right center",
	];

	// The widget state: a widget draws while any "yes" flag holds and no "no" flag does.
	static TagStruct WidgetState( TagStruct s ) => s
		.Enum8( 0x4, "input 1", HudInput )
		.Enum8( 0x5, "input 2", HudInput )
		.Enum8( 0x6, "input 3", HudInput )
		.Enum8( 0x7, "input 4", HudInput )
		.Flags16( 0x8, "yes unit flags", HudUnitFlags )
		.Flags16( 0xA, "yes extra flags", HudExtraFlags )
		.Flags16( 0xC, "yes weapon flags", HudWeaponFlags )
		.Flags16( 0xE, "yes game engine state flags", HudGameEngineFlags )
		.Flags16( 0x10, "no unit flags", HudUnitFlags )
		.Flags16( 0x12, "no extra flags", HudExtraFlags )
		.Flags16( 0x14, "no weapon flags", HudWeaponFlags )
		.Flags16( 0x16, "no game engine state flags", HudGameEngineFlags )
		.Int8( 0x18, "age cutoff" )
		.Int8( 0x19, "clip cutoff" )
		.Int8( 0x1A, "total cutoff" )
		.Enum( 0x1C, "anchor", HudWidgetAnchor );

	// A function of a widget input (hud_input1..4) or of time, as base64 and decoded (see MappingFunctionJson).
	static TagStruct WidgetFunction( TagStruct s, int offset, string name ) => s
		.StringId( offset, $"{name} input" )
		.StringId( offset + 0x4, $"{name} range input" )
		.Float( offset + 0x8, $"{name} time period" )
		.ByteData( offset + 0xC, $"{name} function" );

	// Scale, rotation (theta, radians) and offset (pixels) of a widget, each applied when its flag is set.
	// Which function is which follows the flags of the widgets that use them.
	static readonly TagStruct WidgetEffect = new TagStruct( "hud widget effect", 0x68 )
	{
		Extra = MappingFunctionJson.WriteDecoded( ("HorizontalScale", 0x10), ("VerticalScale", 0x24), ("Theta", 0x38), ("HorizontalOffset", 0x4C), ("VerticalOffset", 0x60) ),
	}
		.Flags16( 0x0, "flags", ["apply scale", "apply theta", "apply offset"] )
		.Apply( s => WidgetFunction( s, 0x4, "horizontal scale" ) )
		.Apply( s => WidgetFunction( s, 0x18, "vertical scale" ) )
		.Apply( s => WidgetFunction( s, 0x2C, "theta" ) )
		.Apply( s => WidgetFunction( s, 0x40, "horizontal offset" ) )
		.Apply( s => WidgetFunction( s, 0x54, "vertical offset" ) );

	// Sequences are indices into the bitmap's sequences, per screen size (full, half and quarter screen).
	// Offsets are pixels from the anchor, registration points fractions of the frame's size.
	// On a bitmap without sequences the widget draws its first image (the index is 0 wherever that happens).
	static readonly TagStruct BitmapWidget = new TagStruct( "hud bitmap widget", 0x64 )
		.StringId( 0x0, "name" )
		.Apply( WidgetState )
		.Flags16( 0x1E, "flags", ["flip horizontally", "flip vertically", "scope mirror horizontally", "scope mirror vertically", "scope stretch"] )
		.Reference( 0x20, "bitmap" )
		.Reference( 0x28, "shader" )
		.ByteIndex( 0x30, "fullscreen sequence index" )
		.ByteIndex( 0x31, "halfscreen sequence index" )
		.ByteIndex( 0x32, "quarterscreen sequence index" )
		.Point2Int( 0x34, "fullscreen offset" )
		.Point2Int( 0x38, "halfscreen offset" )
		.Point2Int( 0x3C, "quarterscreen offset" )
		.Point2( 0x40, "fullscreen registration point" )
		.Point2( 0x48, "halfscreen registration point" )
		.Point2( 0x50, "quarterscreen registration point" )
		.Block( 0x58, "effect", WidgetEffect )
		.Enum( 0x60, "special hud type", ["unspecial", "scoreboard player emblem", "scoreboard other player emblem", "scoreboard player score meter",
			"scoreboard other player score meter", "unit shield meter", "motion sensor", "territory meter"] );

	// The string is a string id of the HUD messages, or with "string is a number" the input whose value is written.
	static readonly TagStruct TextWidget = new TagStruct( "hud text widget", 0x54 )
		.StringId( 0x0, "name" )
		.Apply( WidgetState )
		.Flags16( 0x1E, "flags", ["string is a number", "force 2-digit number", "force 3-digit number", "talking player hack"] )
		.Reference( 0x20, "shader" )
		.StringId( 0x28, "string" )
		.Enum( 0x2C, "justification", ["left", "center", "right"] )
		.Enum8( 0x30, "fullscreen font index", ["default", "number font"] )
		.Enum8( 0x31, "halfscreen font index", ["default", "number font"] )
		.Enum8( 0x32, "quarterscreen font index", ["default", "number font"] )
		.Float( 0x34, "fullscreen scale" )
		.Float( 0x38, "halfscreen scale" )
		.Float( 0x3C, "quarterscreen scale" )
		.Point2Int( 0x40, "fullscreen offset" )
		.Point2Int( 0x44, "halfscreen offset" )
		.Point2Int( 0x48, "quarterscreen offset" )
		.Block( 0x4C, "effect", WidgetEffect );

	static readonly TagStruct ScreenEffectWidget = new TagStruct( "hud screen effect widget", 0x50 )
		.StringId( 0x0, "name" )
		.Apply( WidgetState )
		.Flags16( 0x1E, "flags", [] )
		.Reference( 0x20, "bitmap" )
		.Reference( 0x28, "fullscreen screen effect" )
		.Reference( 0x30, "halfscreen screen effect" )
		.Reference( 0x38, "quarterscreen screen effect" )
		.ByteIndex( 0x40, "fullscreen sequence index" )
		.ByteIndex( 0x41, "halfscreen sequence index" )
		.ByteIndex( 0x42, "quarterscreen sequence index" )
		.Point2Int( 0x44, "fullscreen offset" )
		.Point2Int( 0x48, "halfscreen offset" )
		.Point2Int( 0x4C, "quarterscreen offset" );

	// A HUD: a unit's (masterchief), a weapon's or a vehicle's, or the scoreboard.
	public static readonly TagStruct NewHudDefinition = new TagStruct( "new hud definition", 0x28 )
		.Block( 0x8, "bitmap widgets", BitmapWidget )
		.Block( 0x10, "text widgets", TextWidget )
		.Int16( 0x18, "low clip cutoff" )
		.Int16( 0x1A, "low ammo cutoff" )
		.Float( 0x1C, "age cutoff" )
		.Block( 0x20, "screen effect widgets", ScreenEffectWidget );

	// Colours as packed ARGB bytes, flash period and delay in seconds, the length of a flash, and the colour when disabled.
	static TagStruct FlashingColors( TagStruct s, int offset, string prefix ) => s
		.ColorArgbInt( offset, $"{prefix}default color" )
		.ColorArgbInt( offset + 0x4, $"{prefix}flashing color" )
		.Float( offset + 0x8, $"{prefix}flash period" )
		.Float( offset + 0xC, $"{prefix}flash delay" )
		.Int16( offset + 0x10, $"{prefix}number of flashes" )
		.Flags16( offset + 0x12, $"{prefix}flash flags", ["reverse default and flashing colors"] )
		.Float( offset + 0x14, $"{prefix}flash length" )
		.ColorArgbInt( offset + 0x18, $"{prefix}disabled color" );

	static readonly TagStruct HudButtonIcon = new TagStruct( "hud globals button icon", 0x10 )
		.Index( 0x0, "sequence index" )
		.Int16( 0x2, "width offset" )
		.Point2Int( 0x4, "offset from reference corner" )
		.ColorArgbInt( 0x8, "override icon color" )
		.Int8( 0xC, "frame rate" )
		.Flags8( 0xD, "flags", ["use text from string list instead", "override default color", "width offset is absolute icon width"] )
		.Index( 0xE, "text index" );

	static readonly TagStruct HudWaypointArrowStyle = new TagStruct( "hud globals waypoint arrow style", 0x68 )
		.String( 0x0, "name" )
		.ColorArgbInt( 0x28, "color" )
		.Float( 0x2C, "opacity" )
		.Float( 0x30, "translucency" )
		.Index( 0x34, "on screen sequence index" )
		.Index( 0x36, "off screen sequence index" )
		.Index( 0x38, "occluded sequence index" )
		.Flags32( 0x4C, "flags", ["dont rotate when pointing offscreen"] );

	static readonly TagStruct HudDashlight = new TagStruct( "hud globals dashlight", 0x1C )
		.Reference( 0x0, "bitmap" )
		.Reference( 0x8, "shader" )
		.Index( 0x10, "sequence index" )
		.Flags16( 0x12, "flags", ["dont scale when pulsing"] )
		.Reference( 0x14, "sound" );

	static readonly TagStruct HudWaypointArrow = new TagStruct( "hud globals waypoint arrow", 0x24 )
		.Reference( 0x0, "bitmap" )
		.Reference( 0x8, "shader" )
		.Index( 0x10, "sequence index" )
		.Float( 0x14, "smallest size" )
		.Float( 0x18, "smallest distance" )
		.Reference( 0x1C, "border bitmap" );

	static readonly TagStruct HudWaypoint = new TagStruct( "hud globals waypoint", 0x18 )
		.Reference( 0x0, "bitmap" )
		.Reference( 0x8, "shader" )
		.Index( 0x10, "onscreen sequence index" )
		.Index( 0x12, "occluded sequence index" )
		.Index( 0x14, "offscreen sequence index" );

	static readonly TagStruct HudSound = new TagStruct( "hud globals sound", 0x18 )
		.Reference( 0x0, "chief sound" )
		.Flags32( 0x8, "latched to", ["shield recharging", "shield damaged", "shield low", "shield empty", "health low", "health empty",
			"health minor damage", "health major damage", "rocket locking", "rocket locked"] )
		.Float( 0xC, "scale" )
		.Reference( 0x10, "dervish sound" );

	static readonly TagStruct HudPlayerTraining = new TagStruct( "hud globals player training", 0x1C )
		.StringId( 0x0, "display string" )
		.StringId( 0x4, "display string 2" )
		.StringId( 0x8, "display string 3" )
		.Int16( 0xC, "maximum display time" )
		.Int16( 0xE, "display count" )
		.Int16( 0x10, "disappear delay" )
		.Int16( 0x12, "redisplay delay" )
		.Float( 0x14, "display delay" )
		.Flags16( 0x18, "flags", ["not in multiplayer"] );

	// The HUD globals: messages, waypoints and dashlights, the motion sensor's range and scale, sounds and training prompts.
	// Several fields are kept from Halo CE and unused.
	public static readonly TagStruct HudGlobals = new TagStruct( "hud globals", 0x488 )
		.Enum( 0x0, "anchor", ["top left", "top right", "bottom left", "bottom right", "center", "crosshair"] )
		.Point2Int( 0x24, "anchor offset" )
		.Float( 0x28, "width scale" )
		.Float( 0x2C, "height scale" )
		.Flags16( 0x30, "scaling flags", ["dont scale offset", "dont scale size"] )
		.Reference( 0x48, "fullscreen font" )
		.Reference( 0x50, "splitscreen font" )
		.Float( 0x58, "up time" )
		.Float( 0x5C, "fade time" )
		.ColorArgb( 0x60, "icon color" )
		.ColorArgb( 0x70, "text color" )
		.Float( 0x80, "text spacing" )
		.Reference( 0x84, "item message text" )
		.Reference( 0x8C, "icon bitmap" )
		.Reference( 0x94, "alternate icon text" )
		.Block( 0x9C, "button icons", HudButtonIcon )
		.Apply( s => FlashingColors( s, 0xA4, "hud help " ) )
		.Reference( 0xC4, "hud messages" )
		.Apply( s => FlashingColors( s, 0xCC, "objective " ) )
		.Int16( 0xE8, "objective uptime ticks" )
		.Int16( 0xEA, "objective fade ticks" )
		.Float( 0xEC, "top offset" )
		.Float( 0xF0, "bottom offset" )
		.Float( 0xF4, "left offset" )
		.Float( 0xF8, "right offset" )
		.Reference( 0x11C, "arrow bitmap" )
		.Block( 0x124, "waypoint arrow styles", HudWaypointArrowStyle )
		.Float( 0x17C, "hud scale in multiplayer" )
		.Float( 0x290, "motion sensor range" )
		.Float( 0x294, "motion sensor velocity sensitivity" )
		.Float( 0x298, "motion sensor scale" )
		.Rectangle2D( 0x29C, "default chapter title bounds" )
		.Int16( 0x2D0, "hud damage top offset" )
		.Int16( 0x2D2, "hud damage bottom offset" )
		.Int16( 0x2D4, "hud damage left offset" )
		.Int16( 0x2D6, "hud damage right offset" )
		.Reference( 0x2F8, "hud damage indicator bitmap" )
		.Index( 0x300, "hud damage sequence index" )
		.Index( 0x302, "hud damage multiplayer sequence index" )
		.ColorArgbInt( 0x304, "hud damage color" )
		.Apply( s => FlashingColors( s, 0x318, "not much time left " ) )
		.Apply( s => FlashingColors( s, 0x338, "time out flash " ) )
		.Reference( 0x380, "carnage report bitmap" )
		.Index( 0x388, "loading begin text" )
		.Index( 0x38A, "loading end text" )
		.Index( 0x38C, "checkpoint begin text" )
		.Index( 0x38E, "checkpoint end text" )
		.Reference( 0x390, "checkpoint sound" )
		.Reference( 0x3F8, "hud text" )
		.Block( 0x400, "dashlights", HudDashlight )
		.Block( 0x408, "waypoint arrows", HudWaypointArrow )
		.Block( 0x410, "waypoints", HudWaypoint )
		.Block( 0x418, "hud sounds", HudSound )
		.Block( 0x420, "player training data", HudPlayerTraining )
		.Reference( 0x428, "primary message sound" )
		.Reference( 0x430, "secondary message sound" )
		.StringId( 0x438, "boot griefer string" )
		.StringId( 0x43C, "cannot boot griefer string" )
		.Reference( 0x440, "training shader" )
		.Reference( 0x448, "human training top right" )
		.Reference( 0x450, "human training top center" )
		.Reference( 0x458, "human training top left" )
		.Reference( 0x460, "human training middle" )
		.Reference( 0x468, "elite training top right" )
		.Reference( 0x470, "elite training top center" )
		.Reference( 0x478, "elite training top left" )
		.Reference( 0x480, "elite training middle" );

	static readonly TagStruct BitmapSprite = new TagStruct( "bitmap sprite", 0x20 )
		.Index( 0x0, "bitmap index" )
		.Float( 0x8, "left" )
		.Float( 0xC, "right" )
		.Float( 0x10, "top" )
		.Float( 0x14, "bottom" )
		.Point2( 0x18, "registration point" );

	static readonly TagStruct BitmapSequence = new TagStruct( "bitmap sequence", 0x3C )
		.String( 0x0, "name" )
		.Index( 0x20, "first bitmap index" )
		.Int16( 0x22, "bitmap count" )
		.Block( 0x34, "sprites", BitmapSprite );

	static readonly TagStruct BitmapData = new TagStruct( "bitmap data", 0xA8 )
		.Int16( 0x4, "width" )
		.Int16( 0x6, "height" )
		.UInt8( 0x8, "depth" )
		.Enum( 0xA, "type", ["2d texture", "3d texture", "cube map"] )
		.Enum( 0xC, "format", ["a8", "y8", "ay8", "a8y8", "unused1", "unused2", "r5g6b5", "unused3", "a1r5g5b5", "a4r4g4b4", "x8r8g8b8",
			"a8r8g8b8", "unused4", "unused5", "dxt1", "dxt3", "dxt5", "p8 bump", "p8", "argbfp32", "rgbfp32", "rgbfp16", "v8u8", "g8b8"] )
		.Point2Int( 0x10, "registration point" )
		.Int16( 0x14, "mipmap count" );

	// A bitmap's images (Halo's "bitmap data") and sequences.
	// A sprite sequence's frames are its sprites (rectangles of an image, 0-1); another's are its images from the first on.
	// Image n loads as mount://halo2/<bitmap path>[_n].vtex, frame f of sequence s as <bitmap path>/<s>_<f>.vtex.
	public static readonly TagStruct Bitmap = new TagStruct( "bitmap", 0x50 )
		.Enum( 0x0, "type", ["2d textures", "3d textures", "cube maps", "sprites", "interface bitmaps"] )
		.Enum( 0x2, "format", ["compressed with color key transparency", "compressed with explicit alpha", "compressed with interpolated alpha",
			"16-bit color", "32-bit color", "monochrome"] )
		.Enum( 0x4, "usage", ["alpha blend", "default", "height map", "detail map", "light map", "vector map", "height map blue 255", "embm",
			"height map a8l8", "height map g8b8", "height map g8b8 with alpha"] )
		.Int16( 0x34, "mipmap count" )
		.Enum( 0x36, "sprite usage", ["blend add subtract max", "multiply min", "double multiply"] )
		.Int16( 0x38, "sprite spacing" )
		.Block( 0x3C, "sequences", BitmapSequence )
		.Block( 0x44, "bitmaps", BitmapData );
}
