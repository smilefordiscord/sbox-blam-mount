using System;
using Halo;
using Halo.Combat;
using HaloWeapons;

namespace Halo.UI;

// Halo CE's own weapon HUD for the held Halo weapon, from halo1sbox's classic HUD (hud.json and the mounted bitmaps, Halo's layout and blend):
// the crosshairs with their states (red over an enemy, flashing warnings), the scope (the weapon HUD's screen effect mask,
// its centre-anchored scope elements such as the sniper's ladders and range readout), and the grenade panel.
// Ammo and health stay on Sandbox's own HUD.
[Title( "Halo Weapon HUD" ), Icon( "my_location" )]
public sealed partial class HaloWeaponHud : Component {
	[ConVar( "halo_crosshair_scale", Help = "Extra crosshair size on top of the field of view correction (1: MCC's size at its default field of view).", Saved = true )]
	public static float CrosshairScale { get; set; } = 1f;

	// Off scales the additive pass by the bitmap's alpha, dimming anti-aliased edges.
	[ConVar( "halo_hud_exact_blend", Help = "Halo's exact HUD blend (colour added unscaled by alpha). 0: colour added scaled by alpha.", Saved = true )]
	public static bool ExactBlend { get; set; } = true;

	// When the HUD last drew a crosshair for the weapon, so HaloWeapon keeps Sandbox's crosshair hidden.
	static RealTimeSince _drewCrosshair = 100;
	public static bool DrawsCrosshair => _drewCrosshair < 0.5f;

	// The scene's HUD, made the first time a Halo weapon asks for it.
	public static void Ensure( Scene scene ) {
		if ( scene is null || scene.GetAllComponents<HaloWeaponHud>().Any() ) return;
		var go = scene.CreateObject();
		go.Name = "halo weapon hud";
		go.Flags |= GameObjectFlags.NotNetworked | GameObjectFlags.NotSaved | GameObjectFlags.Hidden;
		go.AddComponent<HaloWeaponHud>();
	}

	HaloHudData _data;
	HudCanvas _canvas;
	readonly HudBatch _painter = new();
	HaloHudRenderer _renderer;
	string _lastError;

	// The local player's Halo weapon in hand, or null.
	static HaloWeapon LocalWeapon() {
		var player = PlayerController.FindLocalPlayer();
		if ( player is null || player.Health <= 0 ) return null;
		return player.GetComponent<BaseInventoryComponent>()?.ActiveItem as HaloWeapon;
	}

	protected override void OnUpdate() {
		var weapon = LocalWeapon();
		for ( var i = 0; i < HaloTick.Advanced; i++ ) TickNightVision( weapon );

		_data = HaloHudDataStore.Hud;
		var camera = Scene.Camera;
		if ( _data is null || camera is null || weapon is null || weapon.Tag is null || HaloWeapon.SeenFromOutside( weapon.Owner?.Controller, Scene ) ) {
			_screenEffect?.Clear();
			_renderer?.Clear();
			return;
		}

		try {
			_painter.Clear();
			_canvas = new HudCanvas( Screen.Width, Screen.Height );
			if ( _canvas.Scale <= 0 ) return;
			DrawFrame( weapon );
			_renderer ??= Components.GetOrCreate<HaloHudRenderer>();
			_painter.Flush( camera, _renderer );
		}
		catch ( Exception e ) {
			if ( e.Message != _lastError ) {
				_lastError = e.Message;
				Log.Warning( $"Halo HUD: {e.Message}" );
			}
		}
		finally {
			_painter.Clear();
		}
	}

	void DrawFrame( HaloWeapon weapon ) {
		var hierarchy = WeaponHierarchy( weapon.WeaponTag, out var known );

		// Halo renders the screen effect (the scope mask) under the HUD.
		DrawScreenEffect( weapon, hierarchy );
		if ( !known ) return;

		var state = BuildWeaponState( weapon );
		var input = new CrosshairInput( weapon.WantsFire, weapon.WantsGrenade, weapon.Frags <= 0 && weapon.Plasmas <= 0 );
		var target = weapon.AimTarget();

		// Crosshairs scale with field of view, not with the HUD size.
		var hudCanvas = _canvas;
		_canvas = new HudCanvas( Screen.Width, Screen.Height, CrosshairFactor( weapon ), margins: false );
		try {
			if ( DrawCrosshairs( weapon, input, hierarchy, state, target ) ) _drewCrosshair = 0;
		}
		finally { _canvas = hudCanvas; }

		DrawScopeElements( weapon, hierarchy, state, target );
		DrawGrenadeHud( weapon );
	}

	readonly record struct CrosshairInput( bool Firing, bool ThrowingGrenade, bool NoGrenades );

	// field of view

	// MCC's default field of view, 86 on its slider (55.4 degrees vertical), at which its crosshairs are authored.
	static float MccVerticalFov( float horizontal16x9 ) =>
		2 * MathF.Atan( MathF.Tan( horizontal16x9.DegreeToRadian() * 0.5f ) * (9f / 16f) ).RadianToDegree();

	// The camera's vertical field of view without the weapon's zoom: Halo's, which HaloWeapon sets while held.
	static float BaseVerticalFov( HaloWeapon weapon ) => HaloWeapon.VerticalFieldOfView;

	// Crosshairs cover a fixed angle of the view: MCC's size scaled by tan(authored half fov) / tan(ours), unzoomed so zooming doesn't resize them.
	static float CrosshairFactor( HaloWeapon weapon ) {
		var authored = MccVerticalFov( 86 );
		var ours = BaseVerticalFov( weapon );
		var scale = Math.Clamp( CrosshairScale, 0.25f, 3f );
		if ( ours <= 1 ) return scale;
		return MathF.Tan( authored.DegreeToRadian() * 0.5f ) / MathF.Tan( ours.DegreeToRadian() * 0.5f ) * scale;
	}

	// Halo's horizontal 4:3 field of view for the current (zoomed) camera, which the scope's warp blur reads.
	static float HaloFieldOfView( HaloWeapon weapon ) {
		var vertical = BaseVerticalFov( weapon );
		var magnification = MathF.Max( 1, weapon.Magnification );
		var zoomed = 2 * MathF.Atan( MathF.Tan( vertical.DegreeToRadian() / 2 ) / magnification );
		return (2 * MathF.Atan( MathF.Tan( zoomed / 2 ) / (0.75f * 0.85f) )).RadianToDegree();
	}

	// drawing primitives

	// Halo's HUD blend: out = tex.rgb * color.rgb + framebuffer * (1 - tex.a * color.a).
	// Drawn as a darken by coverage, then the color added unscaled by alpha.
	void Blit( Texture texture, Rect rect, Color color ) {
		if ( texture is null ) return;

		if ( color.a > 0.002f ) {
			_painter.SetBlendMode( BlendMode.Normal );
			_painter.DrawTexture( HudTextures.Mipped( texture ), rect, new Color( 0, 0, 0, color.a ) );
		}

		if ( color.r + color.g + color.b > 0.002f ) {
			_painter.SetBlendMode( BlendMode.Lighten );
			_painter.DrawTexture( HudTextures.Mipped( texture ), rect, new Color( color.r, color.g, color.b, 1 ), opaque: ExactBlend );
		}

		_painter.SetBlendMode( BlendMode.Normal );
	}

	// A bitmap frame's mounted texture and its size in HUD pixels, halved for MCC's "half hud scale" art.
	Texture FrameTexture( string bitmapTag, int sequence, int frame, out Vector2 hudSize ) {
		hudSize = default;
		var tag = HudTextures.Resolve( _data, bitmapTag );
		if ( tag is null || sequence < 0 ) return null;

		var info = _data.Bitmap( tag );
		var count = info?.SpriteCount( sequence ) ?? 1;
		frame = count > 0 ? Math.Abs( frame ) % count : 0;

		var page = -1;
		Vector2? size = null;
		if ( info is not null ) size = info.FrameSize( sequence, frame, out page );

		var texture = HudTextures.Frame( tag, sequence, frame ) ?? (page >= 0 ? HudTextures.Page( tag, page ) : null);
		if ( texture is null ) return null;

		hudSize = size ?? texture.Size * (info?.HalfScale == true ? 0.5f : 1f);
		return texture;
	}

	// A whole bitmap page, as "not a sprite" crosshairs use.
	Texture PageTexture( string bitmapTag, int page, out Vector2 hudSize ) {
		hudSize = default;
		var tag = HudTextures.Resolve( _data, bitmapTag );
		if ( tag is null || page < 0 ) return null;

		var info = _data.Bitmap( tag );
		var texture = HudTextures.Page( tag, page ) ?? HudTextures.Frame( tag, page, 0 );
		if ( texture is null ) return null;

		hudSize = info?.PageSize( page ) ?? texture.Size;
		return texture;
	}

	static Vector2 Scaled( Vector2 size, HudPlacement placement ) {
		if ( placement is null ) return size;
		var w = placement.WidthScale > 0 ? placement.WidthScale : 1;
		var h = placement.HeightScale > 0 ? placement.HeightScale : 1;
		return new Vector2( size.x * w, size.y * h );
	}

	bool DrawSprite( string bitmapTag, int sequence, int frame, HudAnchor anchor, HudPlacement placement, Color color ) {
		var texture = FrameTexture( bitmapTag, sequence, frame, out var size );
		if ( texture is null ) return false;

		var point = _canvas.Point( anchor, placement?.AnchorOffset );
		Blit( texture, _canvas.Bounds( anchor, point, Scaled( size, placement ) ), color );
		return true;
	}

	// A static element in its disabled, flashing or default color.
	bool DrawStatic( HudStaticElement element, HudAnchor anchor, bool flashing, bool disabled, float flashReference ) {
		if ( element is null || string.IsNullOrEmpty( element.InterfaceBitmap ) || element.SequenceIndex is not int sequence )
			return false;

		var color = disabled ? HudColor.FromArgb( element.DisabledColor )
			: flashing ? FlashColor( element, flashReference )
			: HudColor.FromArgb( element.DefaultColor );

		return DrawSprite( element.InterfaceBitmap, sequence, 0, anchor, element, color );
	}

	// Early in each period the color pulses to the flash color over (delay + length) * flashes, as a raised cosine.
	// A null reference shows the flash color outright during a flash window, as Halo's reference 0 does.
	static Color FlashColor( HudColors colors, float? reference ) {
		var baseColor = HudColor.FromArgb( colors.DefaultColor );
		var flashColor = HudColor.FromArgb( colors.FlashingColor );
		var period = colors.FlashPeriod;
		if ( period <= 0 ) return baseColor;

		var reverse = colors.FlashFlags?.Contains( "reverse default flashing colors" ) ?? false;
		var t = Time.Now - (reference ?? 0);
		var phase = ((t % period) + period) % period;
		var window = colors.FlashDelay + colors.FlashLength;
		if ( window <= 0 || phase >= colors.NumberOfFlashes * window ) return baseColor;

		phase %= window;
		if ( reference is null )
			return reverse ? baseColor : flashColor;

		if ( phase < colors.FlashLength && colors.FlashLength > 0 ) {
			var f = MathF.Sqrt( Math.Clamp( 1 - (MathF.Cos( phase / colors.FlashLength * 6.283f ) + 1) * 0.5f, 0, 1 ) );
			return reverse ? Color.Lerp( flashColor, baseColor, f ) : Color.Lerp( baseColor, flashColor, f );
		}

		return reverse ? flashColor : baseColor;
	}

	// Draws right to left, one digit width apart; frames 10-14 are . : - m km.
	// decimalValue is the fraction in ten-thousandths, or -1 for none.
	bool DrawNumber( HudNumberElement numbers, HudAnchor anchor, int value, int decimalValue, Color color ) {
		var digits = _data.Interface?.HudDigits;
		if ( numbers is null || digits is null || string.IsNullOrEmpty( digits.DigitsBitmap ) ) return false;
		if ( FrameTexture( digits.DigitsBitmap, 0, 0, out _ ) is null ) return false;

		var flags = numbers.Flags ?? Array.Empty<string>();
		var fractional = Math.Min( numbers.NumberOfFractionalDigits, 4 );
		var kilometers = value > 999;
		float digitCount = numbers.MaximumNumberOfDigits + (fractional > 0 && decimalValue >= 0 ? fractional + 1 : 0);
		float decimalPointWidth = fractional > 0 ? digits.DecimalPointWidth : 0;
		var width = digits.ScreenDigitWidth * _canvas.Scale;

		if ( flags.Contains( "draw a trailing m" ) ) {
			digitCount += 1;
			if ( kilometers ) {
				decimalValue = value * 10;
				value /= 1000;
			}
		}

		var negative = value < 0;
		value = Math.Abs( value );

		var origin = _canvas.Point( anchor, numbers.AnchorOffset );
		var cursor = anchor.X < 0 ? origin.x + ((digitCount - 2) * digits.ScreenDigitWidth + decimalPointWidth) * _canvas.Scale
			: anchor.X == 0 ? origin.x + ((digitCount - 1) * digits.ScreenDigitWidth + decimalPointWidth) * _canvas.Scale * 0.5f
			: origin.x;

		void Glyph( int frame ) {
			var texture = FrameTexture( digits.DigitsBitmap, 0, frame, out var size );
			if ( texture is not null )
				Blit( texture, _canvas.Bounds( anchor, new Vector2( cursor, origin.y ), size ), color );
		}

		if ( flags.Contains( "draw a trailing m" ) ) {
			Glyph( kilometers ? 14 : 13 );
			cursor -= width;
		}

		if ( fractional > 0 && decimalValue >= 0 ) {
			for ( var i = fractional; i < 4; i++ ) decimalValue /= 10;
			for ( var i = 0; i < fractional; i++ ) {
				Glyph( decimalValue % 10 );
				cursor -= width;
				decimalValue /= 10;
			}

			cursor += width - digits.DecimalPointWidth * _canvas.Scale;
			Glyph( 10 );
			cursor -= width;
		}

		for ( var i = 0; i < numbers.MaximumNumberOfDigits; i++ ) {
			if ( value == 0 && !flags.Contains( "show leading zeros" ) ) break;
			Glyph( value % 10 );
			cursor -= width;
			value /= 10;
		}

		if ( negative ) Glyph( 12 );
		return true;
	}

	// screen effect

	HaloScreenEffect _screenEffect;
	float _nightVision;
	float _previousNightVision;

	// Night vision power, blended between the last two ticks.
	float NightVision => Math.Clamp( _previousNightVision.LerpTo( _nightVision, HaloTick.Fraction ), 0, 1 );

	// While zoomed with night vision on, power rises 1/12 a tick; otherwise it falls 1/24 a tick. Unzooming zeroes it.
	void TickNightVision( HaloWeapon weapon ) {
		_previousNightVision = _nightVision;
		if ( weapon is null || weapon.ZoomLevel < 0 ) _nightVision = 0;
		else if ( weapon.NightVisionOn ) _nightVision = MathF.Min( 1, _nightVision + 1f / 12f );
		else _nightVision = MathF.Max( 0, _nightVision - 1f / 24f );
	}

	// The weapon HUD's first screen effect, not its parents': scope mask, warp blur, night vision and desaturation.
	// Each part shows while zoomed, or always without its "only when zoomed" flag.
	void DrawScreenEffect( HaloWeapon weapon, List<WeaponHudInterfaceTag> hierarchy ) {
		var zoomed = weapon.ZoomLevel >= 0;
		var effect = hierarchy.Count > 0 ? hierarchy[0].ScreenEffect?.FirstOrDefault() : null;
		var parameters = new HaloScreenEffect.Parameters();

		if ( effect is not null ) {
			if ( (zoomed || !effect.HasMaskFlag( "only when zoomed" )) && !string.IsNullOrEmpty( effect.MaskFullscreen ) ) {
				var maskSource = PageTexture( effect.MaskFullscreen, 0, out _ ) ?? FrameTexture( effect.MaskFullscreen, 0, 0, out _ );
				if ( maskSource is not null ) {
					var page = _data.Bitmap( HudTextures.Resolve( _data, effect.MaskFullscreen ) )?.Bitmaps?.FirstOrDefault();
					parameters.Mask = HudTextures.RawCopy( maskSource ) ?? maskSource;
					parameters.MaskSize = page is not null && page.Width > 0 && page.Height > 0
						? new Vector2( page.Width, page.Height )
						: new Vector2( maskSource.Width, maskSource.Height );
				}
			}

			if ( zoomed || !effect.HasConvolutionFlag( "only when zoomed" ) )
				parameters.WarpRadius = WarpRadius( effect, weapon );

			if ( zoomed || !effect.HasLightEnhancementFlag( "only when zoomed" ) ) {
				var intensity = effect.NightVisionIntensity;
				if ( effect.HasLightEnhancementFlag( "connect to flashlight" ) ) intensity *= NightVision;
				parameters.LightEnhancement = Math.Clamp( intensity, 0, 1 );
				parameters.LightEnhancementMasked = effect.HasLightEnhancementFlag( "masked" );
			}

			if ( zoomed || !effect.HasDesaturationFlag( "only when zoomed" ) ) {
				var intensity = effect.DesaturationIntensity;
				if ( effect.HasDesaturationFlag( "connect to flashlight" ) ) intensity *= NightVision;
				parameters.Desaturation = Math.Clamp( intensity, 0, 1 );
				parameters.DesaturationAdditive = effect.HasDesaturationFlag( "additive" );
				parameters.DesaturationMasked = effect.HasDesaturationFlag( "masked" );
				var tint = effect.EffectTint;
				parameters.DesaturationTint = tint is { Length: >= 3 } ? new Vector3( tint[0], tint[1], tint[2] ) : Vector3.One;
			}
		}

		if ( parameters.IsEmpty ) {
			_screenEffect?.Clear();
			return;
		}

		_screenEffect ??= Components.GetOrCreate<HaloScreenEffect>();
		if ( _screenEffect.IsValid() ) _screenEffect.Submit( Scene.Camera, parameters );
	}

	// In 480-line pixels: the out bounds interpolated by where Halo's vertical fov sits between the in bounds,
	// so the blur grows as zoom narrows the view.
	static float WarpRadius( WeaponHudScreenEffect effect, HaloWeapon weapon ) {
		var output = effect.ConvolutionRadiusOutBounds;
		if ( output is not { Length: >= 2 } ) return 0;

		var input = effect.ConvolutionFovInBounds;
		if ( input is not { Length: >= 2 } || input[0] == input[1] ) return MathF.Max( 0, output[1] );

		// Halo's render camera vertical fov: 2 atan(0.75 * tan(fov / 2) * 0.85).
		var fov = HaloFieldOfView( weapon ).DegreeToRadian();
		var vertical = (2 * MathF.Atan( 0.75f * 0.85f * MathF.Tan( fov * 0.5f ) )).RadianToDegree();
		var t = Math.Clamp( (vertical - input[0]) / (input[1] - input[0]), 0, 1 );
		return MathF.Max( 0, output[0] + (output[1] - output[0]) * t );
	}

	protected override void OnDisabled() {
		_screenEffect?.Clear();
		_renderer?.Clear();
	}
}

// One of Halo's HUD anchors as a direction: X -1 left, 0 center, 1 right; Y -1 top, 0 center, 1 bottom.
public readonly struct HudAnchor {
	public readonly int X;
	public readonly int Y;

	public HudAnchor( int x, int y ) {
		X = x;
		Y = y;
	}

	public static readonly HudAnchor TopLeft = new( -1, -1 );
	public static readonly HudAnchor Center = new( 0, 0 );

	// "from parent" or an unknown anchor name keeps the parent's.
	public static HudAnchor Parse( string name, HudAnchor parent ) => name switch {
		"top left" => new HudAnchor( -1, -1 ),
		"top right" => new HudAnchor( 1, -1 ),
		"bottom left" => new HudAnchor( -1, 1 ),
		"bottom right" => new HudAnchor( 1, 1 ),
		"center" => new HudAnchor( 0, 0 ),
		"top center" => new HudAnchor( 0, -1 ),
		"bottom center" => new HudAnchor( 0, 1 ),
		"left center" => new HudAnchor( -1, 0 ),
		"right center" => new HudAnchor( 1, 0 ),
		_ => parent,
	};
}

// MCC's HUD canvas, 960 lines tall; sprite sizes and tag offsets are in its units.
// Panels inset from the screen edges by MCC's margins of about 2.5%; centered elements have none.
public readonly struct HudCanvas {
	public readonly float Width;
	public readonly float Height;
	public readonly float Scale;
	public readonly float MarginX;
	public readonly float MarginY;

	// factor multiplies the height / 960 scale: the crosshair fov correction.
	public HudCanvas( float width, float height, float factor = 1, bool margins = true ) {
		Width = width;
		Height = height;
		Scale = height / 960f * factor;
		MarginX = margins ? width * 0.0266f : 0;
		MarginY = margins ? height * 0.026f : 0;
	}

	// The anchor's corner plus the offset, mirrored for right and bottom anchors so offsets point into the screen.
	public Vector2 Point( HudAnchor anchor, int[] offset ) {
		var ox = offset is { Length: > 0 } ? offset[0] : 0;
		var oy = offset is { Length: > 1 } ? offset[1] : 0;
		var x = anchor.X < 0 ? MarginX + ox * Scale
			: anchor.X > 0 ? Width - MarginX - ox * Scale
			: Width * 0.5f + ox * Scale;
		var y = anchor.Y < 0 ? MarginY + oy * Scale
			: anchor.Y > 0 ? Height - MarginY - oy * Scale
			: Height * 0.5f + oy * Scale;
		return new Vector2( x, y );
	}

	// A bitmap extends from the point away from its anchored corner, or centered on it. hudSize is in HUD pixels.
	public Rect Bounds( HudAnchor anchor, Vector2 point, Vector2 hudSize ) {
		var w = hudSize.x * Scale;
		var h = hudSize.y * Scale;
		var x = anchor.X < 0 ? point.x : anchor.X > 0 ? point.x - w : point.x - w * 0.5f;
		var y = anchor.Y < 0 ? point.y : anchor.Y > 0 ? point.y - h : point.y - h * 0.5f;
		return new Rect( x, y, w, h );
	}
}

// Records the frame's HUD draws, then submits them at once through HaloHudRenderer, in gamma space as Halo blends.
public sealed class HudBatch {
	struct Command {
		public Texture Texture;
		public Rect Rect;
		public Color Color;
		public BlendMode Mode;
#pragma warning disable CS0649
		public float Rotation;
#pragma warning restore CS0649
		public bool Opaque;
		public HaloHudRenderer.QuadEffect Effect;
	}

	readonly List<Command> _commands = new();
	BlendMode _mode = BlendMode.Normal;

	public void SetBlendMode( BlendMode mode ) => _mode = mode;

	// opaque reads the texture's alpha as 1.
	public void DrawTexture( Texture texture, Rect rect, Color color, bool opaque = false ) {
		if ( texture is null ) return;
		_commands.Add( new Command { Texture = texture, Rect = rect, Color = color, Mode = _mode, Opaque = opaque } );
	}

	// A multitexture overlay computed by the HUD shader.
	public void DrawEffect( Texture texture, Rect rect, Color color, in HaloHudRenderer.QuadEffect effect ) {
		if ( texture is null ) return;
		_commands.Add( new Command { Texture = texture, Rect = rect, Color = color, Mode = _mode, Effect = effect } );
	}

	public void Clear() {
		_commands.Clear();
		_mode = BlendMode.Normal;
	}

	readonly List<HaloHudRenderer.Quad> _quads = new();

	public void Flush( CameraComponent camera, HaloHudRenderer renderer ) {
		if ( camera is null || !renderer.IsValid() ) {
			_commands.Clear();
			return;
		}

		_quads.Clear();
		foreach ( var c in _commands )
			_quads.Add( new HaloHudRenderer.Quad( c.Texture, c.Rect, c.Color, c.Mode == BlendMode.Lighten, c.Rotation ) {
				Opaque = c.Opaque,
				Effect = c.Effect,
			} );
		renderer.Submit( camera, _quads );
		_commands.Clear();
	}
}
