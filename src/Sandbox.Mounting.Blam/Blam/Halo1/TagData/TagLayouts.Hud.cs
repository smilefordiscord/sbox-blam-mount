namespace Blam.Halo1.TagData;

using static Blam.Halo1.TagData.TagLayouts.Options;

// The HUD: unit, weapon and grenade HUD interfaces, the HUD globals and digits, meters and the
// bitmaps they draw. HUD positions are in Halo's 640x480 HUD space; colors are packed ARGB.
public static partial class TagLayouts
{
	static readonly string[] HudAnchor = ["top left", "top right", "bottom left", "bottom right", "center", "top center", "bottom center", "left center", "right center"];
	static readonly string[] HudChildAnchor = ["from parent", "top left", "top right", "bottom left", "bottom right", "center", "top center", "bottom center", "left center", "right center"];
	static readonly string[] HudCanvasSize = ["480p", "960p"];
	static readonly string[] HudScalingFlags = ["don't scale offset", "don't scale size", "use high res scale"];
	static readonly string[] HudFlashFlags = ["reverse default flashing colors"];
	static readonly string[] HudMeterFlags = ["use min max for state changes", "interpolate between min max flash colors as state changes", "interpolate color along hsv space", "more colors for hsv interpolation", "invert interpolation", "use xbox shading"];
	static readonly string[] HudNumberFlags = ["show leading zeros", "only show when zoomed", "draw a trailing m"];
	static readonly string[] HudStateAttachedTo = ["total ammo", "loaded ammo", "heat", "age", "secondary weapon total ammo", "secondary weapon loaded ammo", "distance to target", "elevation to target"];
	static readonly string[] HudViewType = ["any", "fullscreen", "splitscreen"];
	static readonly string[] HudOverlayAnchor = ["texture", "screen"];
	static readonly string[] HudBlendFunction = ["add", "subtract", "multiply", "multiply2x", "dot"];
	static readonly string[] HudWrapMode = ["clamp", "wrap"];

	static readonly string[] CrosshairTypes =
	[
		"aim", "zoom overlay", "charge", "should reload", "flash heat", "flash total ammo", "flash battery", "reload overheat",
		"flash when firing and no ammo", "flash when throwing and no grenade", "low ammo and none left to reload",
		"should reload secondary trigger", "flash secondary total ammo", "flash secondary reload",
		"flash when firing secondary trigger with no ammo", "low secondary ammo and none left to reload", "primary trigger ready",
		"secondary trigger ready", "flash when firing with depleted battery",
	];

	/// <summary>
	/// Where an element sits relative to its interface's anchor (16 bytes).
	/// </summary>
	static TagStruct Placement( TagStruct s, int o ) => s
		.Point2Int( o, "anchor offset" )
		.Float( o + 0x4, "width scale" )
		.Float( o + 0x8, "height scale" )
		.Flags16( o + 0xC, "scaling flags", HudScalingFlags );

	/// <summary>
	/// An element's colors and how it flashes (28 bytes), with an optional name prefix ("hud help").
	/// </summary>
	static TagStruct Colors( TagStruct s, int o, string prefix = "" ) => s
		.ColorArgbInt( o, $"{prefix}default color" )
		.ColorArgbInt( o + 0x4, $"{prefix}flashing color" )
		.Float( o + 0x8, $"{prefix}flash period" )
		.Float( o + 0xC, $"{prefix}flash delay" )
		.Int16( o + 0x10, $"{prefix}number of flashes" )
		.Flags16( o + 0x12, $"{prefix}flash flags", HudFlashFlags )
		.Float( o + 0x14, $"{prefix}flash length" )
		.ColorArgbInt( o + 0x18, $"{prefix}disabled color" );

	/// <summary>
	/// The weapon HUD's element header: what the element shows and where it's anchored.
	/// </summary>
	static TagStruct WeaponElementHeader( TagStruct s ) => s
		.Enum( 0x0, "state attached to", HudStateAttachedTo )
		.Enum( 0x4, "allowed view type", HudViewType )
		.Enum( 0x6, "anchor", HudChildAnchor );

	static readonly TagStruct MultitextureEffector = new TagStruct( "hud multitexture overlay effector", 0xDC )
		.Enum( 0x40, "destination type", ["tint 0 1", "horizontal offset", "vertical offset", "fade 0 1"] )
		.Enum( 0x42, "destination", ["geometry offset", "primary map", "secondary map", "tertiary map"] )
		.Enum( 0x44, "source", ["player pitch", "player pitch tangent", "player yaw", "weapon ammo total", "weapon ammo loaded", "weapon heat", "explicit uses low bound", "weapon zoom level"] )
		.FloatBounds( 0x48, "in bounds" )
		.FloatBounds( 0x50, "out bounds" )
		.ColorRgb( 0x98, "tint color lower bound" )
		.ColorRgb( 0xA4, "tint color upper bound" )
		.Enum( 0xB0, "periodic function", WaveFunction )
		.Float( 0xB4, "function period" )
		.Float( 0xB8, "function phase" );

	static readonly TagStruct MultitextureOverlay = new TagStruct( "hud multitexture overlay", 0x1E0 )
		.Int16( 0x2, "type" )
		.Enum( 0x4, "framebuffer blend function", ["alpha blend", "multiply", "double multiply", "add", "subtract", "component min", "component max", "alpha multiply add"] )
		.Enum( 0x28, "primary anchor", HudOverlayAnchor )
		.Enum( 0x2A, "secondary anchor", HudOverlayAnchor )
		.Enum( 0x2C, "tertiary anchor", HudOverlayAnchor )
		.Enum( 0x2E, "zero to one blend function", HudBlendFunction )
		.Enum( 0x30, "one to two blend function", HudBlendFunction )
		.Point2( 0x34, "primary scale" )
		.Point2( 0x3C, "secondary scale" )
		.Point2( 0x44, "tertiary scale" )
		.Point2( 0x4C, "primary offset" )
		.Point2( 0x54, "secondary offset" )
		.Point2( 0x5C, "tertiary offset" )
		.Reference( 0x64, "primary" )
		.Reference( 0x74, "secondary" )
		.Reference( 0x84, "tertiary" )
		.Enum( 0x94, "primary wrap mode", HudWrapMode )
		.Enum( 0x96, "secondary wrap mode", HudWrapMode )
		.Enum( 0x98, "tertiary wrap mode", HudWrapMode )
		.Block( 0x154, "effectors", MultitextureEffector );

	/// <summary>
	/// A static element: one frame of an interface bitmap sequence (0x68 bytes from its placement).
	/// </summary>
	static TagStruct StaticElement( TagStruct s, int o ) => Colors( Placement( s, o ).Reference( o + 0x24, "interface bitmap" ), o + 0x34 )
		.Index( o + 0x54, "sequence index" )
		.Block( o + 0x58, "multitexture overlays", MultitextureOverlay );

	/// <summary>
	/// A meter element: an A8Y8 bitmap whose gray is the position along the meter and whose alpha
	/// is the mask, lit below a threshold (0x5C bytes from its placement).
	/// </summary>
	static TagStruct MeterElement( TagStruct s, int o ) => Placement( s, o )
		.Reference( o + 0x24, "meter bitmap" )
		.ColorArgbInt( o + 0x34, "color at meter minimum" )
		.ColorArgbInt( o + 0x38, "color at meter maximum" )
		.ColorArgbInt( o + 0x3C, "flash color" )
		.ColorArgbInt( o + 0x40, "empty color" )
		.Flags8( o + 0x44, "flags", HudMeterFlags )
		.Int8( o + 0x45, "minimum meter value" )
		.Index( o + 0x46, "sequence index" )
		.Int8( o + 0x48, "alpha multiplier" )
		.Int8( o + 0x49, "alpha bias" )
		.Int16( o + 0x4A, "value scale" )
		.Float( o + 0x4C, "opacity" )
		.Float( o + 0x50, "translucency" )
		.ColorArgbInt( o + 0x54, "disabled color" )
		.Float( o + 0x58, "min alpha" );

	/// <summary>
	/// A number element, drawn with the HUD digits (0x47 bytes from its placement).
	/// </summary>
	static TagStruct NumberElement( TagStruct s, int o ) => Colors( Placement( s, o ), o + 0x24 )
		.Int8( o + 0x44, "maximum number of digits" )
		.Flags8( o + 0x45, "flags", HudNumberFlags )
		.Int8( o + 0x46, "number of fractional digits" );

	static readonly TagStruct HudStatic = StaticElement( new TagStruct( "hud static element", 0x68 ), 0 );

	static readonly TagStruct HudPlacement = Placement( new TagStruct( "hud placement", 0x10 ), 0 );

	static readonly TagStruct ShieldPanelMeter = MeterElement( new TagStruct( "shield panel meter", 0x88 ), 0 )
		.ColorArgbInt( 0x68, "overcharge minimum color" )
		.ColorArgbInt( 0x6C, "overcharge maximum color" )
		.ColorArgbInt( 0x70, "overcharge flash color" )
		.ColorArgbInt( 0x74, "overcharge empty color" );

	static readonly TagStruct HealthPanelMeter = MeterElement( new TagStruct( "health panel meter", 0x88 ), 0 )
		.ColorArgbInt( 0x68, "medium health left color" )
		.Float( 0x6C, "max color health fraction cutoff" )
		.Float( 0x70, "min color health fraction cutoff" );

	static readonly TagStruct AuxiliaryMeter = MeterElement( new TagStruct( "auxiliary meter", 0xC8 ), 0 )
		.Float( 0x68, "minimum fraction cutoff" )
		.Flags32( 0x6C, "more flags", ["show only when active", "flash once if activated while disabled"] );

	static readonly TagStruct AuxiliaryOverlay = StaticElement( new TagStruct( "unit hud auxiliary overlay", 0x84 ), 0 )
		.Enum( 0x68, "type", ["team icon"] )
		.Flags16( 0x6A, "flags", ["use team color"] );

	static readonly TagStruct AuxiliaryPanel = new TagStruct( "unit hud auxiliary panel", 0x144 )
		.Enum( 0x0, "type", ["integrated light"] )
		.Struct( 0x14, "background", HudStatic )
		.Struct( 0x7C, "meter", AuxiliaryMeter );

	static readonly TagStruct UnitHudSound = new TagStruct( "unit hud sound", 0x38 )
		.Reference( 0x0, "sound" )
		.Flags32( 0x10, "latched to", ["shield recharging", "shield damaged", "shield low", "shield empty", "health low", "health empty", "health minor damage", "health major damage"] )
		.Float( 0x14, "scale" );

	/// <summary>
	/// <c>unhi</c>, 0x56C bytes: the shield and health panels, the motion sensor, auxiliary
	/// overlays and meters (the flashlight) and the HUD sounds. The motion sensor is drawn
	/// bottom left whatever the anchor says.
	/// </summary>
	public static readonly TagStruct UnitHudInterface = new TagStruct( "unit hud interface", 0x56C )
		.Enum( 0x0, "anchor", HudAnchor )
		.Enum( 0x2, "canvas size", HudCanvasSize )
		.Struct( 0x24, "hud background", HudStatic )
		.Struct( 0x8C, "shield panel background", HudStatic )
		.Struct( 0xF4, "shield panel meter", ShieldPanelMeter )
		.Struct( 0x17C, "health panel background", HudStatic )
		.Struct( 0x1E4, "health panel meter", HealthPanelMeter )
		.Struct( 0x26C, "motion sensor background", HudStatic )
		.Struct( 0x2D4, "motion sensor foreground", HudStatic )
		.Struct( 0x35C, "motion sensor center", HudPlacement )
		.Enum( 0x380, "auxiliary overlay anchor", HudAnchor )
		.Block( 0x3A4, "overlays", AuxiliaryOverlay )
		.Block( 0x3C0, "sounds", UnitHudSound )
		.Block( 0x3CC, "meters", AuxiliaryPanel );

	static readonly TagStruct GrenadeNumbers = NumberElement( new TagStruct( "hud number element", 0x54 ), 0 );

	static readonly TagStruct GrenadeOverlay = Colors( Placement( new TagStruct( "grenade hud overlay", 0x88 ), 0 ), 0x24 )
		.Float( 0x44, "frame rate" )
		.Index( 0x48, "sequence index" )
		.Flags16( 0x4A, "type", ["show on flashing", "show on empty", "show on default", "show always"] )
		.Flags32( 0x4C, "flags", ["flashes when active"] );

	static readonly TagStruct GrenadeSound = new TagStruct( "grenade hud sound", 0x38 )
		.Reference( 0x0, "sound" )
		.Flags32( 0x10, "latched to", ["low grenade count", "no grenades left", "throw on no grenades"] )
		.Float( 0x14, "scale" );

	/// <summary>
	/// <c>grhi</c>, 0x1F8 bytes: the grenade count panel. Its messaging icon fields are left out
	/// (garbage in MCC's grenade HUDs).
	/// </summary>
	public static readonly TagStruct GrenadeHudInterface = new TagStruct( "grenade hud interface", 0x1F8 )
		.Enum( 0x0, "anchor", HudAnchor )
		.Enum( 0x2, "canvas size", HudCanvasSize )
		.Struct( 0x24, "background", HudStatic )
		.Struct( 0x8C, "total grenades background", HudStatic )
		.Struct( 0xF4, "total grenades numbers", GrenadeNumbers )
		.Int16( 0x148, "flash cutoff" )
		.Reference( 0x14C, "total grenades overlay bitmap" )
		.Block( 0x15C, "total grenades overlays", GrenadeOverlay )
		.Block( 0x168, "total grenades warning sounds", GrenadeSound );

	static readonly TagStruct WeaponStaticElement = StaticElement( WeaponElementHeader( new TagStruct( "weapon hud static element", 0xB4 ) ), 0x24 );

	static readonly TagStruct WeaponMeterElement = MeterElement( WeaponElementHeader( new TagStruct( "weapon hud meter element", 0xB4 ) ), 0x24 );

	static readonly TagStruct WeaponNumberElement = NumberElement( WeaponElementHeader( new TagStruct( "weapon hud number element", 0xA0 ) ), 0x24 )
		.Flags16( 0x78, "weapon specific flags", ["divide number by clip size"] );

	static readonly TagStruct CrosshairOverlay = Colors( Placement( new TagStruct( "weapon hud crosshair overlay", 0x6C ), 0 ), 0x24 )
		.Int16( 0x44, "frame rate" )
		.Index( 0x46, "sequence index" )
		.Flags32( 0x48, "flags", ["flashes when active", "not a sprite", "show only when zoomed", "show sniper data", "hide area outside reticle", "one zoom level", "don't show when zoomed"] );

	static readonly TagStruct Crosshair = new TagStruct( "weapon hud crosshair", 0x68 )
		.Enum( 0x0, "crosshair type", CrosshairTypes )
		.Enum( 0x4, "allowed view type", HudViewType )
		.Reference( 0x24, "crosshair bitmap" )
		.Block( 0x34, "crosshair overlays", CrosshairOverlay );

	static readonly TagStruct WeaponOverlay = Colors( Placement( new TagStruct( "weapon hud overlay", 0x88 ), 0 ), 0x24 )
		.Int16( 0x44, "frame rate" )
		.Index( 0x48, "sequence index" )
		.Flags16( 0x4A, "type", ["show on flashing", "show on empty", "show on reload overheating", "show on default", "show always"] )
		.Flags32( 0x4C, "flags", ["flashes when active"] );

	static readonly TagStruct WeaponOverlayElement = WeaponElementHeader( new TagStruct( "weapon hud overlay element", 0x68 ) )
		.Reference( 0x24, "overlay bitmap" )
		.Block( 0x34, "overlays", WeaponOverlay );

	static readonly TagStruct WeaponScreenEffect = new TagStruct( "weapon hud screen effect", 0xB8 )
		.Flags16( 0x4, "mask flags", ["only when zoomed"] )
		.Reference( 0x18, "mask fullscreen" )
		.Reference( 0x28, "mask splitscreen" )
		.Flags16( 0x40, "convolution flags", ["only when zoomed"] )
		.AngleBounds( 0x44, "convolution fov in bounds" )
		.FloatBounds( 0x4C, "convolution radius out bounds" )
		.Flags16( 0x6C, "even more flags", ["only when zoomed", "connect to flashlight", "masked"] )
		.Int16( 0x6E, "night vision script source" )
		.Float( 0x70, "night vision intensity" )
		.Flags16( 0x8C, "desaturation flags", ["only when zoomed", "connect to flashlight", "additive", "masked"] )
		.Int16( 0x8E, "desaturation script source" )
		.Float( 0x90, "desaturation intensity" )
		.ColorRgb( 0x94, "effect tint" );

	/// <summary>
	/// <c>wphi</c>, 0x17C bytes: a weapon's HUD. The child HUD is the HUD it chains to (Halo
	/// draws that one too). Messaging icon fields are left out.
	/// </summary>
	public static readonly TagStruct WeaponHudInterface = new TagStruct( "weapon hud interface", 0x17C )
		.Reference( 0x0, "child hud" )
		.Flags16( 0x10, "flags", ["use parent hud flashing parameters"] )
		.Int16( 0x14, "total ammo cutoff" )
		.Int16( 0x16, "loaded ammo cutoff" )
		.Int16( 0x18, "heat cutoff" )
		.Int16( 0x1A, "age cutoff" )
		.Enum( 0x3C, "anchor", HudAnchor )
		.Enum( 0x3E, "canvas size", HudCanvasSize )
		.Block( 0x60, "static elements", WeaponStaticElement )
		.Block( 0x6C, "meter elements", WeaponMeterElement )
		.Block( 0x78, "number elements", WeaponNumberElement )
		.Block( 0x84, "crosshairs", Crosshair )
		.Block( 0x90, "overlay elements", WeaponOverlayElement )
		.Flags32( 0x9C, "crosshair types", CrosshairTypes )
		.Block( 0xAC, "screen effect", WeaponScreenEffect );

	static readonly string[] MessagingFlags = ["use text from string list instead", "override default color", "width offset is absolute icon width"];

	static readonly TagStruct ButtonIcon = new TagStruct( "hud globals button icon", 0x10 )
		.Index( 0x0, "sequence index" )
		.Int16( 0x2, "width offset" )
		.Point2Int( 0x4, "offset from reference corner" )
		.ColorArgbInt( 0x8, "override icon color" )
		.Int8( 0xC, "frame rate" )
		.Flags8( 0xD, "flags", MessagingFlags )
		.Index( 0xE, "text index" );

	static readonly TagStruct WaypointArrow = new TagStruct( "hud globals waypoint arrow", 0x68 )
		.String( 0x0, "name" )
		.ColorArgbInt( 0x28, "color" )
		.Float( 0x2C, "opacity" )
		.Float( 0x30, "translucency" )
		.Index( 0x34, "on screen sequence index" )
		.Index( 0x36, "off screen sequence index" )
		.Index( 0x38, "occluded sequence index" )
		.Flags32( 0x4C, "flags", ["dont rotate when pointing offscreen"] );

	static readonly TagStruct RemapTarget = new TagStruct( "hud globals remap target", 0x14 )
		.Reference( 0x0, "target bitmap" )
		.Enum( 0x10, "language", ["english", "french", "spanish", "italian", "german", "traditional chinese", "japanese", "korean", "portuguese", "latam spanish", "polish", "russian", "simplified chinese"] )
		.Flags16( 0x12, "flags", ["legacy mode"] );

	static readonly TagStruct Remap = new TagStruct( "hud globals remap", 0x1C )
		.Reference( 0x0, "source bitmap" )
		.Block( 0x10, "targets", RemapTarget );

	/// <summary>
	/// <c>hudg</c>, 0x450 bytes: HUD messages, waypoint arrows, the motion sensor's range and
	/// scale, the damage indicator, the default weapon HUD and MCC's anniversary HUD remaps (the
	/// combined HUD bitmaps' "_r" variants per language).
	/// </summary>
	public static readonly TagStruct HudGlobals = Placement( new TagStruct( "hud globals", 0x450 )
			.Enum( 0x0, "anchor", HudAnchor )
			.Enum( 0x2, "canvas size", HudCanvasSize ), 0x24 )
		.Reference( 0x48, "fullscreen font" )
		.Reference( 0x58, "splitscreen font" )
		.Float( 0x68, "up time" )
		.Float( 0x6C, "fade time" )
		.ColorArgb( 0x70, "icon color" )
		.ColorArgb( 0x80, "text color" )
		.Float( 0x90, "text spacing" )
		.Reference( 0x94, "item message text" )
		.Reference( 0xA4, "icon bitmap" )
		.Reference( 0xB4, "alternate icon text" )
		.Block( 0xC4, "button icons", ButtonIcon )
		.Apply( s => Colors( s, 0xD0, "hud help " ) )
		.Reference( 0xF0, "hud messages" )
		.Apply( s => Colors( s, 0x100, "objective " ) )
		.Int16( 0x11C, "objective uptime ticks" )
		.Int16( 0x11E, "objective fade ticks" )
		.Float( 0x120, "top offset" )
		.Float( 0x124, "bottom offset" )
		.Float( 0x128, "left offset" )
		.Float( 0x12C, "right offset" )
		.Reference( 0x150, "arrow bitmap" )
		.Block( 0x160, "waypoint arrows", WaypointArrow )
		.Float( 0x1BC, "hud scale in multiplayer" )
		.Reference( 0x2C0, "default weapon hud" )
		.Float( 0x2D0, "motion sensor range" )
		.Float( 0x2D4, "motion sensor velocity sensitivity" )
		.Float( 0x2D8, "motion sensor scale" )
		.Rectangle2D( 0x2DC, "default chapter title bounds" )
		.Int16( 0x310, "hud damage top offset" )
		.Int16( 0x312, "hud damage bottom offset" )
		.Int16( 0x314, "hud damage left offset" )
		.Int16( 0x316, "hud damage right offset" )
		.Reference( 0x338, "hud damage indicator bitmap" )
		.Index( 0x348, "hud damage sequence index" )
		.Index( 0x34A, "hud damage multiplayer sequence index" )
		.ColorArgbInt( 0x34C, "hud damage color" )
		.Apply( s => Colors( s, 0x360, "not much time left " ) )
		.Apply( s => Colors( s, 0x380, "time out flash " ) )
		.Reference( 0x3C8, "carnage report bitmap" )
		.Index( 0x3D8, "loading begin text" )
		.Index( 0x3DA, "loading end text" )
		.Index( 0x3DC, "checkpoint begin text" )
		.Index( 0x3DE, "checkpoint end text" )
		.Reference( 0x3E0, "checkpoint sound" )
		.Block( 0x3F0, "anniversary hud remaps", Remap );

	/// <summary>
	/// <c>hud#</c>, 0x64 bytes: the HUD digits (frames 0-9, then . : - m km) and their spacing.
	/// </summary>
	public static readonly TagStruct HudNumber = new TagStruct( "hud number", 0x64 )
		.Reference( 0x0, "digits bitmap" )
		.Int8( 0x10, "bitmap digit width" )
		.Int8( 0x11, "screen digit width" )
		.Int8( 0x12, "x offset" )
		.Int8( 0x13, "y offset" )
		.Int8( 0x14, "decimal point width" )
		.Int8( 0x15, "colon width" );

	/// <summary>
	/// <c>metr</c>, 0xAC bytes. Only the globals' first person interface uses meters; the HUD
	/// draws its own meter elements. The encoded stencil data is left out.
	/// </summary>
	public static readonly TagStruct Meter = new TagStruct( "meter", 0xAC )
		.Flags32( 0x0, "flags", ["unused"] )
		.Reference( 0x4, "stencil bitmaps" )
		.Reference( 0x14, "source bitmap" )
		.UInt16( 0x24, "stencil sequence index" )
		.UInt16( 0x26, "source sequence index" )
		.Enum( 0x3C, "interpolate colors", ["linearly", "faster near empty", "faster near full", "through random noise"] )
		.Enum( 0x3E, "anchor colors", ["at both ends", "at empty", "at full"] )
		.ColorArgb( 0x48, "empty color" )
		.ColorArgb( 0x58, "full color" )
		.Float( 0x7C, "unmask distance" )
		.Float( 0x80, "mask distance" );

	static readonly TagStruct BitmapSprite = new TagStruct( "bitmap sprite", 0x20 )
		.Index( 0x0, "bitmap index" )
		.Float( 0x8, "left" )
		.Float( 0xC, "right" )
		.Float( 0x10, "top" )
		.Float( 0x14, "bottom" )
		.Point2( 0x18, "registration point" );

	static readonly TagStruct BitmapSequence = new TagStruct( "bitmap sequence", 0x40 )
		.String( 0x0, "name" )
		.Index( 0x20, "first bitmap index" )
		.UInt16( 0x22, "bitmap count" )
		.Block( 0x34, "sprites", BitmapSprite );

	static readonly TagStruct BitmapData = new TagStruct( "bitmap data", 0x30 )
		.UInt16( 0x4, "width" )
		.UInt16( 0x6, "height" )
		.UInt16( 0x8, "depth" )
		.Enum( 0xA, "type", ["2d texture", "3d texture", "cube map", "white"] )
		.Enum( 0xC, "format", ["a8", "y8", "ay8", "a8y8", "unused1", "unused2", "r5g6b5", "unused3", "a1r5g5b5", "a4r4g4b4", "x8r8g8b8", "a8r8g8b8", "unused4", "unused5", "dxt1", "dxt3", "dxt5", "p8 bump", "bc7"] )
		.Flags16( 0xE, "flags", ["power of two dimensions", "compressed", "palettized", "swizzled", "linear", "v16u16", "unused", "make it actually work", "external", "environment"] )
		.Point2Int( 0x10, "registration point" )
		.UInt16( 0x14, "mipmap count" );

	/// <summary>
	/// <c>bitm</c>, 0x6C bytes: the bitmaps (pages; Halo's "bitmap data") and their sequences
	/// (Halo's "bitmap group sequence") of sprites, each sprite a
	/// rectangle of one page in texture coordinates (0..1). A sprite sequence's frames are its
	/// sprites; other sequences show whole bitmaps from the first bitmap index. "Half hud scale"
	/// bitmaps are MCC's 2x HUD art, drawn at half their pixel size. Pages load as
	/// <c>mount://haloce/&lt;bitmap path&gt;_&lt;n&gt;.vtex</c> (no suffix for the first), frames
	/// as <c>&lt;bitmap path&gt;/&lt;sequence&gt;_&lt;frame&gt;.vtex</c>.
	/// </summary>
	public static readonly TagStruct Bitmap = new TagStruct( "bitmap", 0x6C )
		.Enum( 0x0, "type", ["2d textures", "3d textures", "cube maps", "sprites", "interface bitmaps"] )
		.Enum( 0x2, "encoding format", ["dxt1", "dxt3", "dxt5", "16-bit", "32-bit", "monochrome", "bc7"] )
		.Enum( 0x4, "usage", ["alpha blend", "default", "height map", "detail map", "light map", "vector map"] )
		.Flags16( 0x6, "flags", ["enable diffusion dithering", "disable height map compression", "uniform sprite sequences", "filthy sprite bug fix", "half hud scale", "invert detail fade", "use average color for detail fade", "force hud use highres scale"] )
		.Float( 0x8, "detail fade factor" )
		.Float( 0xC, "sharpen amount" )
		.Float( 0x10, "bump height" )
		.Enum( 0x14, "sprite budget size", ["32x32", "64x64", "128x128", "256x256", "512x512", "1024x1024"] )
		.UInt16( 0x16, "sprite budget count" )
		.UInt16( 0x18, "color plate width" )
		.UInt16( 0x1A, "color plate height" )
		.Float( 0x44, "blur filter size" )
		.Float( 0x48, "alpha bias" )
		.UInt16( 0x4C, "mipmap count" )
		.Enum( 0x4E, "sprite usage", ["blend add subtract max", "multiply min", "double multiply"] )
		.UInt16( 0x50, "sprite spacing" )
		.Block( 0x54, "sequences", BitmapSequence )
		.Block( 0x60, "bitmaps", BitmapData );
}
