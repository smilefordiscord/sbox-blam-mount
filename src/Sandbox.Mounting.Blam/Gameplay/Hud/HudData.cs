using System;

namespace Halo.UI;

// Halo's HUD tags as the mount exports them to hud.json, plus the size and sprite layout of every bitmap they draw.
// Property names match the JSON's tag field names.
// Positions are in 640x480 HUD space; colors are [A, R, G, B] bytes.
public sealed class HaloHudData {
	public HudGlobalsTag Globals { get; set; }
	public HudInterfaceBitmaps Interface { get; set; }

	// The Spartan's first unit HUD interface, used with one local player; SplitscreenUnitHud is the second.
	public string MultiplayerUnitHud { get; set; }
	public string SplitscreenUnitHud { get; set; }
	public Dictionary<string, UnitHudInterfaceTag> UnitHuds { get; set; } = new();

	// Indexed by grenade type: frag, plasma.
	public List<string> GrenadeHuds { get; set; } = new();
	public Dictionary<string, GrenadeHudInterfaceTag> GrenadeHudInterfaces { get; set; } = new();

	public Dictionary<string, WeaponHudInterfaceTag> WeaponHuds { get; set; } = new();

	// Weapon tag path -> weapon HUD interface, null for the flag, ball and gravity rifle.
	public Dictionary<string, string> WeaponHudFor { get; set; } = new();
	public Dictionary<string, MeterTag> Meters { get; set; } = new();
	public Dictionary<string, HudBitmapInfo> Bitmaps { get; set; } = new();

	// English anniversary remaps: combined HUD bitmap -> its "_r" variant.
	public Dictionary<string, string> Remaps { get; set; } = new();

	public UnitHudInterfaceTag UnitHud( string path ) => Find( UnitHuds, path );
	public WeaponHudInterfaceTag WeaponHud( string path ) => Find( WeaponHuds, path );
	public GrenadeHudInterfaceTag GrenadeHud( string path ) => Find( GrenadeHudInterfaces, path );
	public HudBitmapInfo Bitmap( string path ) => Find( Bitmaps, path );

	static T Find<T>( Dictionary<string, T> table, string path ) where T : class {
		if ( string.IsNullOrEmpty( path ) || table is null ) return null;
		return table.TryGetValue( path, out var value ) ? value : null;
	}
}

// The interface bitmaps the HUD uses directly, and the HUD digits.
public sealed class HudInterfaceBitmaps {
	public string HudGlobals { get; set; }
	public string MotionSensorSweepBitmap { get; set; }
	public string MotionSensorSweepBitmapMask { get; set; }
	public string MotionSensorBlipBitmap { get; set; }

	// The bigger blip for objectives ("interface goo map1").
	public string CustomBlipBitmap { get; set; }
	public string MultiplayerHudBitmap { get; set; }
	public string HudDigitsDefinition { get; set; }
	public HudNumberTag HudDigits { get; set; }
}

// The counter digit sprites (frames 0-9, then . : - m km) and their spacing.
public sealed class HudNumberTag {
	public string DigitsBitmap { get; set; }
	public int BitmapDigitWidth { get; set; }
	public int ScreenDigitWidth { get; set; }
	public int XOffset { get; set; }
	public int YOffset { get; set; }
	public int DecimalPointWidth { get; set; }
	public int ColonWidth { get; set; }
}

public sealed class HudGlobalsTag {
	public string Anchor { get; set; }
	public int[] AnchorOffset { get; set; }
	public float TopOffset { get; set; }
	public float BottomOffset { get; set; }
	public float LeftOffset { get; set; }
	public float RightOffset { get; set; }
	public float HudScaleInMultiplayer { get; set; }
	public string DefaultWeaponHud { get; set; }

	// World units the motion sensor covers.
	public float MotionSensorRange { get; set; }

	// Squared speed (world units per tick) a unit must exceed to show.
	public float MotionSensorVelocitySensitivity { get; set; }

	// Sensor units across its radius.
	public float MotionSensorScale { get; set; }
	public string HudDamageIndicatorBitmap { get; set; }
	public int? HudDamageSequenceIndex { get; set; }
	public int? HudDamageMultiplayerSequenceIndex { get; set; }
	public int[] HudDamageColor { get; set; }

	// How far in from each screen edge the damage indicators sit.
	public float HudDamageTopOffset { get; set; }
	public float HudDamageBottomOffset { get; set; }
	public float HudDamageLeftOffset { get; set; }
	public float HudDamageRightOffset { get; set; }

	// Nav point arrows, by the names the game engine's goals use.
	public string ArrowBitmap { get; set; }
	public List<HudWaypointArrow> WaypointArrows { get; set; } = new();

	// Timer colors while running low, and once timed out.
	public int[] NotMuchTimeLeftDefaultColor { get; set; }
	public int[] NotMuchTimeLeftFlashingColor { get; set; }
	public float NotMuchTimeLeftFlashPeriod { get; set; }
	public float NotMuchTimeLeftFlashDelay { get; set; }
	public int NotMuchTimeLeftNumberOfFlashes { get; set; }
	public float NotMuchTimeLeftFlashLength { get; set; }
	public string[] NotMuchTimeLeftFlashFlags { get; set; } = Array.Empty<string>();
	public int[] TimeOutFlashDefaultColor { get; set; }
	public int[] TimeOutFlashFlashingColor { get; set; }
	public float TimeOutFlashFlashPeriod { get; set; }
	public float TimeOutFlashFlashDelay { get; set; }
	public int TimeOutFlashNumberOfFlashes { get; set; }
	public float TimeOutFlashFlashLength { get; set; }
	public string[] TimeOutFlashFlashFlags { get; set; } = Array.Empty<string>();
}

// A nav point arrow's color and its bitmap sequences for on screen, off screen and occluded.
public sealed class HudWaypointArrow {
	public string Name { get; set; }
	public int[] Color { get; set; }
	public float Opacity { get; set; }
	public float Translucency { get; set; }
	public int? OnScreenSequenceIndex { get; set; }
	public int? OffScreenSequenceIndex { get; set; }
	public int? OccludedSequenceIndex { get; set; }
	public string[] Flags { get; set; } = Array.Empty<string>();
}

// shared element layouts

// An element's place relative to its interface's anchor, in HUD space.
// Offsets point away from the anchored corner.
public class HudPlacement {
	public int[] AnchorOffset { get; set; }
	public float WidthScale { get; set; } = 1;
	public float HeightScale { get; set; } = 1;
	public string[] ScalingFlags { get; set; } = Array.Empty<string>();
}

// An element's default, flashing and disabled colors and its flash timing.
public class HudColors : HudPlacement {
	public int[] DefaultColor { get; set; }
	public int[] FlashingColor { get; set; }
	public float FlashPeriod { get; set; }
	public float FlashDelay { get; set; }
	public int NumberOfFlashes { get; set; }
	public string[] FlashFlags { get; set; } = Array.Empty<string>();
	public float FlashLength { get; set; }
	public int[] DisabledColor { get; set; }
}

// Up to three maps combined over a static element, each with its own scale, offset and wrap, driven by effectors.
// The sniper's pitch ladders use one.
public sealed class HudMultitextureOverlay {
	public string FramebufferBlendFunction { get; set; }
	public string ZeroToOneBlendFunction { get; set; }
	public string OneToTwoBlendFunction { get; set; }
	public float[] PrimaryScale { get; set; }
	public float[] SecondaryScale { get; set; }
	public float[] PrimaryOffset { get; set; }
	public float[] SecondaryOffset { get; set; }
	public string Primary { get; set; }
	public string Secondary { get; set; }
	public string Tertiary { get; set; }
	public string PrimaryWrapMode { get; set; }
	public string SecondaryWrapMode { get; set; }
	public List<HudMultitextureEffector> Effectors { get; set; } = new();
}

// Maps a source such as player pitch or zoom level to an offset, fade or tint.
public sealed class HudMultitextureEffector {
	public string DestinationType { get; set; }
	public string Destination { get; set; }
	public string Source { get; set; }
	public float[] InBounds { get; set; }
	public float[] OutBounds { get; set; }
}

// A static element: one frame of an interface bitmap sequence.
public class HudStaticElement : HudColors {
	public string InterfaceBitmap { get; set; }
	public int? SequenceIndex { get; set; }
	public List<HudMultitextureOverlay> MultitextureOverlays { get; set; } = new();

	// Weapon HUD elements only.
	public string StateAttachedTo { get; set; }
	public string AllowedViewType { get; set; }
	public string Anchor { get; set; }
}

// An A8Y8 bitmap whose gray is the position along the bar and alpha the mask.
// Halo lights pixels below a threshold.
public class HudMeterElement : HudPlacement {
	public string MeterBitmap { get; set; }
	public int[] ColorAtMeterMinimum { get; set; }
	public int[] ColorAtMeterMaximum { get; set; }
	public int[] FlashColor { get; set; }
	public int[] EmptyColor { get; set; }
	public string[] Flags { get; set; } = Array.Empty<string>();
	public int MinimumMeterValue { get; set; }
	public int? SequenceIndex { get; set; }
	public int AlphaMultiplier { get; set; }
	public int AlphaBias { get; set; }
	public int ValueScale { get; set; }
	public float Opacity { get; set; }
	public float Translucency { get; set; }
	public int[] DisabledColor { get; set; }
	public float MinAlpha { get; set; }

	// Shield panel meter only.
	public int[] OverchargeMinimumColor { get; set; }
	public int[] OverchargeMaximumColor { get; set; }
	public int[] OverchargeFlashColor { get; set; }
	public int[] OverchargeEmptyColor { get; set; }

	// Health panel meter only.
	public int[] MediumHealthLeftColor { get; set; }
	public float MaxColorHealthFractionCutoff { get; set; }
	public float MinColorHealthFractionCutoff { get; set; }

	// Auxiliary meters only.
	public float MinimumFractionCutoff { get; set; }
	public string[] MoreFlags { get; set; } = Array.Empty<string>();

	// Weapon HUD elements only.
	public string StateAttachedTo { get; set; }
	public string AllowedViewType { get; set; }
	public string Anchor { get; set; }
}

// Drawn with the HUD digits.
public class HudNumberElement : HudColors {
	public int MaximumNumberOfDigits { get; set; }
	public string[] Flags { get; set; } = Array.Empty<string>();
	public int NumberOfFractionalDigits { get; set; }
	public string[] WeaponSpecificFlags { get; set; } = Array.Empty<string>();

	public string StateAttachedTo { get; set; }
	public string AllowedViewType { get; set; }
	public string Anchor { get; set; }
}

// A weapon or grenade HUD sprite shown by state, such as empty or flashing.
public sealed class HudOverlay : HudColors {
	public float FrameRate { get; set; }
	public int? SequenceIndex { get; set; }
	public string[] Type { get; set; } = Array.Empty<string>();
	public string[] Flags { get; set; } = Array.Empty<string>();
}

// unit HUD

public sealed class UnitHudInterfaceTag {
	public string Anchor { get; set; }
	public string CanvasSize { get; set; }
	public HudStaticElement HudBackground { get; set; }
	public HudStaticElement ShieldPanelBackground { get; set; }
	public HudMeterElement ShieldPanelMeter { get; set; }
	public HudStaticElement HealthPanelBackground { get; set; }
	public HudMeterElement HealthPanelMeter { get; set; }

	// Drawn anchored bottom left whatever Anchor says.
	public HudStaticElement MotionSensorBackground { get; set; }
	public HudStaticElement MotionSensorForeground { get; set; }

	// The blips' center, anchored bottom left.
	public HudPlacement MotionSensorCenter { get; set; }
	public string AuxiliaryOverlayAnchor { get; set; }

	// Auxiliary panels, such as the flashlight ("integrated light") meter.
	public List<UnitHudAuxiliaryPanel> Meters { get; set; } = new();
}

public sealed class UnitHudAuxiliaryPanel {
	public string Type { get; set; }
	public HudStaticElement Background { get; set; }
	public HudMeterElement Meter { get; set; }
}

// grenade HUD

public sealed class GrenadeHudInterfaceTag {
	public string Anchor { get; set; }
	public HudStaticElement Background { get; set; }
	public HudStaticElement TotalGrenadesBackground { get; set; }
	public HudNumberElement TotalGrenadesNumbers { get; set; }

	// The count flashes at or below this many grenades.
	public int FlashCutoff { get; set; }
	public string TotalGrenadesOverlayBitmap { get; set; }
	public List<HudOverlay> TotalGrenadesOverlays { get; set; } = new();
}

// weapon HUD

public sealed class WeaponHudInterfaceTag {
	// Despite the name, the parent HUD this one chains to.
	// It draws first, crosshairs included.
	public string ChildHud { get; set; }
	public string[] Flags { get; set; } = Array.Empty<string>();
	public int TotalAmmoCutoff { get; set; }
	public int LoadedAmmoCutoff { get; set; }
	public int HeatCutoff { get; set; }
	public int AgeCutoff { get; set; }
	public string Anchor { get; set; }
	public List<HudStaticElement> StaticElements { get; set; } = new();
	public List<HudMeterElement> MeterElements { get; set; } = new();
	public List<HudNumberElement> NumberElements { get; set; } = new();
	public List<WeaponHudCrosshair> Crosshairs { get; set; } = new();
	public List<WeaponHudOverlayElement> OverlayElements { get; set; } = new();
	public string[] CrosshairTypes { get; set; } = Array.Empty<string>();
	public List<WeaponHudScreenEffect> ScreenEffect { get; set; } = new();

	public bool HasFlag( string flag ) => Flags?.Contains( flag ) ?? false;
}

public sealed class WeaponHudCrosshair {
	public string CrosshairType { get; set; }
	public string AllowedViewType { get; set; }
	public string CrosshairBitmap { get; set; }
	public List<WeaponHudCrosshairOverlay> CrosshairOverlays { get; set; } = new();
}

public sealed class WeaponHudCrosshairOverlay : HudColors {
	public float FrameRate { get; set; }
	public int? SequenceIndex { get; set; }
	public string[] Flags { get; set; } = Array.Empty<string>();

	public bool HasFlag( string flag ) => Flags?.Contains( flag ) ?? false;
}

public sealed class WeaponHudOverlayElement {
	public string StateAttachedTo { get; set; }
	public string AllowedViewType { get; set; }
	public string Anchor { get; set; }
	public string OverlayBitmap { get; set; }
	public List<HudOverlay> Overlays { get; set; } = new();
}

// The scope mask, warp blur, night vision and desaturation that HaloScreenEffect draws.
public sealed class WeaponHudScreenEffect {
	public string[] MaskFlags { get; set; } = Array.Empty<string>();
	public string MaskFullscreen { get; set; }
	public string MaskSplitscreen { get; set; }

	public string[] ConvolutionFlags { get; set; } = Array.Empty<string>();

	// Vertical field of view bounds in degrees that the blur radius interpolates over.
	public float[] ConvolutionFovInBounds { get; set; }

	// Blur radius bounds, in 480-line pixels.
	public float[] ConvolutionRadiusOutBounds { get; set; }

	// Night vision flags.
	public string[] EvenMoreFlags { get; set; } = Array.Empty<string>();
	public int NightVisionScriptSource { get; set; }
	public float NightVisionIntensity { get; set; }

	public string[] DesaturationFlags { get; set; } = Array.Empty<string>();
	public int DesaturationScriptSource { get; set; }
	public float DesaturationIntensity { get; set; }

	// Desaturation tint, RGB 0..1.
	public float[] EffectTint { get; set; }

	public bool HasMaskFlag( string flag ) => MaskFlags?.Contains( flag ) ?? false;
	public bool HasConvolutionFlag( string flag ) => ConvolutionFlags?.Contains( flag ) ?? false;
	public bool HasLightEnhancementFlag( string flag ) => EvenMoreFlags?.Contains( flag ) ?? false;
	public bool HasDesaturationFlag( string flag ) => DesaturationFlags?.Contains( flag ) ?? false;
}

// meters and bitmaps

public sealed class MeterTag {
	public int? SourceSequenceIndex { get; set; }
	public string InterpolateColors { get; set; }
	public string AnchorColors { get; set; }
	public float[] EmptyColor { get; set; }
	public float[] FullColor { get; set; }
}

// Sprite sequences crop a page by 0..1 edges; plain sequences show whole bitmaps.
// "half hud scale" bitmaps are MCC's 2x art and draw at half size in HUD space.
public sealed class HudBitmapInfo {
	public string Type { get; set; }
	public string[] Flags { get; set; } = Array.Empty<string>();
	public List<HudBitmapSequence> Sequences { get; set; } = new();
	public List<HudBitmapPage> Bitmaps { get; set; } = new();

	public bool HalfScale => Flags?.Contains( "half hud scale" ) ?? false;

	// The frame's size in HUD pixels and its page, or null if missing.
	// Frames wrap, as in Halo.
	public Vector2? FrameSize( int sequence, int frame, out int page ) {
		page = -1;
		if ( sequence < 0 || sequence >= Sequences.Count ) return null;

		var seq = Sequences[sequence];
		float w, h;
		if ( seq.Sprites is { Count: > 0 } sprites ) {
			var sprite = sprites[Math.Abs( frame ) % sprites.Count];
			page = sprite.BitmapIndex ?? -1;
			if ( page < 0 || page >= Bitmaps.Count ) return null;
			w = (sprite.Right - sprite.Left) * Bitmaps[page].Width;
			h = (sprite.Bottom - sprite.Top) * Bitmaps[page].Height;
		}
		else {
			var count = Math.Max( 1, seq.BitmapCount );
			page = (seq.FirstBitmapIndex ?? 0) + Math.Abs( frame ) % count;
			if ( page < 0 || page >= Bitmaps.Count ) return null;
			w = Bitmaps[page].Width;
			h = Bitmaps[page].Height;
		}

		var scale = HalfScale ? 0.5f : 1f;
		return new Vector2( w * scale, h * scale );
	}

	// In HUD pixels, for "not a sprite" crosshairs, which index pages directly.
	public Vector2? PageSize( int page ) {
		if ( page < 0 || page >= Bitmaps.Count ) return null;
		var scale = HalfScale ? 0.5f : 1f;
		return new Vector2( Bitmaps[page].Width * scale, Bitmaps[page].Height * scale );
	}

	public int SpriteCount( int sequence ) {
		if ( sequence < 0 || sequence >= Sequences.Count ) return 0;
		var seq = Sequences[sequence];
		return seq.Sprites is { Count: > 0 } ? seq.Sprites.Count : Math.Max( 1, seq.BitmapCount );
	}
}

public sealed class HudBitmapSequence {
	public int? FirstBitmapIndex { get; set; }
	public int BitmapCount { get; set; }
	public List<HudBitmapSprite> Sprites { get; set; } = new();
}

public sealed class HudBitmapSprite {
	public int? BitmapIndex { get; set; }
	public float Left { get; set; }
	public float Right { get; set; }
	public float Top { get; set; }
	public float Bottom { get; set; }
	public float[] RegistrationPoint { get; set; }
}

public sealed class HudBitmapPage {
	public int Width { get; set; }
	public int Height { get; set; }
	public string Format { get; set; }
	public int[] RegistrationPoint { get; set; }
}

// Loads hud.json once, retrying when the mount's state changes.
// Missing data leaves Hud null so the CSS HUD stays in charge.
public static class HaloHudDataStore {
	static HaloHudData _hud;
	static int _failedState = -1;

	public static HaloHudData Hud {
		get {
			if ( _hud is not null ) return _hud;

			var state = Halo.Data.HaloMountCheck.MountState;
			if ( state == _failedState ) return null;
			_failedState = state;

			var json = Halo.Data.HaloDataStore.ReadMount( "hud.json" );
			if ( string.IsNullOrEmpty( json ) ) {
				Log.Warning( $"No Halo HUD data (mount://{Halo.Data.HaloMountCheck.Ident}/hud.json): is the Halo: Combat Evolved mount enabled?" );
				return null;
			}

			try {
				_hud = Json.Deserialize<HaloHudData>( json );
				Log.Info( $"Halo HUD data: {_hud?.WeaponHuds.Count ?? 0} weapon HUDs, {_hud?.Bitmaps.Count ?? 0} bitmaps" );
			}
			catch ( Exception e ) {
				Log.Warning( $"Halo HUD data couldn't be read: {e.Message}" );
				_hud = null;
			}

			return _hud;
		}
	}
}

// Halo colors are [A, R, G, B] byte arrays.
public static class HudColor {
	// The HUD text blue, (117, 186, 255).
	public static readonly Color Blue = new( 0.45882353f, 0.7294118f, 1f );

	public static Color FromArgb( int[] argb ) {
		if ( argb is not { Length: >= 4 } ) return Color.Transparent;
		return new Color( argb[1] / 255f, argb[2] / 255f, argb[3] / 255f, argb[0] / 255f );
	}

	public static bool IsZero( int[] argb ) => argb is null || argb.All( x => x == 0 );
}
