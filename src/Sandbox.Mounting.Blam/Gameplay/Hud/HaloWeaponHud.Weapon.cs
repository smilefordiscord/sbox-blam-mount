using System;
using Halo;
using Halo.Combat;
using HaloWeapons;

namespace Halo.UI;

// The weapon HUD's crosshairs, its scope elements and the grenade HUD, from halo1sbox's HaloClassicHud.Weapon.
// Each weapon interface chains to a parent through its "child hud" field (master rounds, master plasma).
public sealed partial class HaloWeaponHud {
	// Crosshair types in Halo's order; the index is the crosshair state.
	static readonly string[] CrosshairStates = {
		"aim", "zoom overlay", "charge", "should reload", "flash heat", "flash total ammo", "flash battery",
		"reload overheat", "flash when firing and no ammo", "flash when throwing and no grenade",
		"low ammo and none left to reload", "should reload secondary trigger", "flash secondary total ammo",
		"flash secondary reload", "flash when firing secondary trigger with no ammo",
		"low secondary ammo and none left to reload", "primary trigger ready", "secondary trigger ready",
		"flash when firing with depleted battery",
	};

	// Element "state attached to" values in Halo's order; the index is the element state.
	static readonly string[] ElementStates = {
		"total ammo", "loaded ammo", "heat", "age", "secondary weapon total ammo", "secondary weapon loaded ammo",
		"distance to target", "elevation to target",
	};

	static readonly string[] GrenadeOverlayTypes = { "show on flashing", "show on empty", "show on default", "show always" };

	const int StateAim = 0, StateZoom = 1;

	struct MagazineState {
		public bool Reloading;
		public bool CanFire;
		public int Loaded;
		public int Remaining;
	}

	// What the weapon HUD reads from the held weapon.
	struct WeaponState {
		public float Heat;
		public float Age;
		public bool Overheated;
		public MagazineState Primary;
		public MagazineState Secondary;
		public int LoadedMaximum;
	}

	readonly float[] _crosshairReference = Enumerable.Repeat( -1f, 19 ).ToArray();
	readonly float[] _weaponFlash = Enumerable.Repeat( -1f, 8 ).ToArray();
	float _grenadeFlash = -1;

	string _hierarchyFor;
	List<WeaponHudInterfaceTag> _hierarchy = new();
	bool _hierarchyKnown;

	// A weapon's HUD interface and up to 16 parents, as in Halo. known is false when hud.json lacks the weapon.
	List<WeaponHudInterfaceTag> WeaponHierarchy( string path, out bool known ) {
		path ??= "";
		if ( path == _hierarchyFor && _hierarchy is not null ) {
			known = _hierarchyKnown;
			return _hierarchy;
		}

		_hierarchyFor = path;
		_hierarchy = new List<WeaponHudInterfaceTag>();
		_hierarchyKnown = _data.WeaponHudFor.TryGetValue( path, out var root );

		var seen = new HashSet<string>();
		while ( !string.IsNullOrEmpty( root ) && _hierarchy.Count < 16 && seen.Add( root ) ) {
			var definition = _data.WeaponHud( root );
			if ( definition is null ) break;
			_hierarchy.Add( definition );
			root = definition.ChildHud;
		}

		known = _hierarchyKnown;
		return _hierarchy;
	}

	static WeaponState BuildWeaponState( HaloWeapon weapon ) {
		var magazine = weapon.Tag?.Magazine( 0 );
		return new WeaponState {
			Heat = Math.Clamp( weapon.Heat, 0, 1 ),
			Age = Math.Clamp( weapon.Age, 0, 1 ),
			Overheated = weapon.Overheated,
			LoadedMaximum = magazine?.RoundsLoadedMaximum ?? 0,
			// Halo CE's weapons have one magazine; the secondary states read empty.
			Secondary = default,
			Primary = new MagazineState {
				Reloading = weapon.Reloading,
				CanFire = !weapon.Reloading && weapon.IsReady,
				Loaded = magazine is null ? 0 : Math.Max( 0, weapon.Loaded ),
				Remaining = magazine is null ? 0 : Math.Max( 0, weapon.Reserve ),
			},
		};
	}

	// crosshairs

	// Draws every crosshair whose state is active across the hierarchy.
	// The aim state is autoaim on an enemy; the zoom state is the zoom level.
	// "flashes when active" crosshairs take their flash color, so the reticle turns red over an enemy.
	// Latched warnings such as firing with no ammo stay up for one flash period.
	bool DrawCrosshairs( HaloWeapon weapon, CrosshairInput input, List<WeaponHudInterfaceTag> hierarchy, WeaponState state, IHaloUnit target ) {
		if ( hierarchy.Count == 0 ) return false;

		var root = hierarchy[0];
		var valid = new HashSet<string>( hierarchy.SelectMany( x => x.CrosshairTypes ?? Array.Empty<string>() ) );
		var aimRef = target is not null ? 1 : 0;
		var zoomRef = weapon.ZoomLevel >= 0 ? weapon.ZoomLevel + 1 : 0;
		var firing = input.Firing;
		var noGrenades = input.NoGrenades;
		var p = state.Primary;
		var s = state.Secondary;
		var render = new bool[CrosshairStates.Length];

		for ( var i = 0; i < CrosshairStates.Length; i++ ) {
			if ( !valid.Contains( CrosshairStates[i] ) ) continue;

			var latched = _crosshairReference[i] >= 0;
			var result = i switch {
				StateAim => aimRef > 0,
				StateZoom => true,
				2 => false,
				3 => (p.Remaining > 0 || p.Loaded > 0) && p.Remaining > 0 && p.Loaded <= root.LoadedAmmoCutoff,
				4 => state.Heat * 100 >= root.HeatCutoff,
				5 => p.Remaining <= root.TotalAmmoCutoff && !p.Reloading,
				6 => state.Age < 1 && (1 - state.Age) * 100 <= root.AgeCutoff,
				7 => p.Reloading || state.Overheated,
				8 => p.Loaded == 0 && p.Remaining == 0 && firing || latched,
				9 => noGrenades && input.ThrowingGrenade || latched,
				10 => (p.Remaining > 0 || p.Loaded > 0) && p.Remaining == 0 && p.Loaded <= root.LoadedAmmoCutoff,
				11 => (s.Remaining > 0 || s.Loaded > 0) && s.Remaining > 0 && s.Loaded <= root.LoadedAmmoCutoff,
				12 => s.Remaining <= root.TotalAmmoCutoff && !s.Reloading,
				13 => s.Reloading,
				14 => s.Loaded == 0 && s.Remaining == 0 && firing || latched,
				15 => (s.Remaining > 0 || s.Loaded > 0) && s.Remaining == 0 && s.Loaded <= root.LoadedAmmoCutoff,
				16 => p.CanFire,
				17 => s.CanFire,
				18 => state.Age >= 1 && firing || latched,
				_ => false,
			};

			render[i] = result || i == StateAim;
			if ( i > StateZoom ) {
				if ( !result ) _crosshairReference[i] = -1;
				else if ( _crosshairReference[i] < 0 ) _crosshairReference[i] = Time.Now;
			}
		}

		var drew = false;
		foreach ( var definition in hierarchy ) {
			foreach ( var element in definition.Crosshairs ) {
				if ( element is null || element.AllowedViewType == "splitscreen" ) continue;
				var index = Array.IndexOf( CrosshairStates, element.CrosshairType );
				if ( index < 0 || !render[index] ) continue;

				foreach ( var item in element.CrosshairOverlays ) {
					if ( item is null ) continue;
					var onlyZoomed = item.HasFlag( "show only when zoomed" );
					if ( onlyZoomed && zoomRef <= 0 ) continue;
					if ( item.HasFlag( "don't show when zoomed" ) && zoomRef != 0 ) continue;

					var flashes = item.HasFlag( "flashes when active" );
					var color = HudColor.FromArgb( item.DefaultColor );
					var frame = 0;

					if ( index == StateAim ) {
						if ( flashes ) { if ( aimRef > 0 ) color = FlashColor( item, null ); }
						else frame = aimRef;
					}
					else if ( index == StateZoom ) {
						if ( item.HasFlag( "one zoom level" ) ) {
							if ( zoomRef == 0 ) continue;
						}
						else {
							frame = zoomRef - (onlyZoomed ? 1 : 0);
						}

						if ( flashes && aimRef > 0 ) color = FlashColor( item, null );
					}
					else {
						var reference = _crosshairReference[index];
						if ( index is 8 or 9 or 14 or 18 ) {
							var active = index switch {
								8 => p.Loaded == 0 && p.Remaining == 0 && firing,
								9 => noGrenades && input.ThrowingGrenade,
								14 => s.Loaded == 0 && s.Remaining == 0 && firing,
								_ => state.Age >= 1 && firing,
							};
							if ( !active && reference >= 0 && Time.Now - reference >= item.FlashPeriod )
								_crosshairReference[index] = reference = -1;
							if ( reference < 0 ) continue;
						}

						if ( item.FrameRate > 0 && reference >= 0 )
							frame = (int)((Time.Now - reference) / item.FrameRate);
						if ( flashes && reference >= 0 )
							color = FlashColor( item, reference );
					}

					drew |= DrawCrosshairItem( element.CrosshairBitmap, item, Math.Max( 0, frame ), color, index == StateZoom || onlyZoomed );
				}
			}
		}

		return drew;
	}

	// "not a sprite" items draw a whole bitmap page, with the sequence index as the page.
	// Scope items (zoom overlays and "show only when zoomed" ones) use MCC's exact layout, center + offset * H/960,
	// skipping the reticle's field of view correction so they line up with the scope mask.
	bool DrawCrosshairItem( string bitmapTag, WeaponHudCrosshairOverlay item, int frame, Color color, bool scope ) {
		if ( item.SequenceIndex is not int sequence ) return false;

		Vector2 size;
		var texture = item.HasFlag( "not a sprite" )
			? PageTexture( bitmapTag, sequence, out size )
			: FrameTexture( bitmapTag, sequence, frame, out size );
		if ( texture is null ) return false;

		var canvas = scope ? new HudCanvas( Screen.Width, Screen.Height, 1, margins: false ) : _canvas;
		var point = canvas.Point( HudAnchor.Center, item.AnchorOffset );
		Blit( texture, canvas.Bounds( HudAnchor.Center, point, Scaled( size, item ) ), color );
		return true;
	}

	// scope elements

	// Only the weapon HUD's centre-anchored elements, which belong to the scope (the sniper's ladders and range readout);
	// the ammo panels and meters are Sandbox's HUD's job.
	void DrawScopeElements( HaloWeapon weapon, List<WeaponHudInterfaceTag> hierarchy, WeaponState state, IHaloUnit target ) {
		var zoomed = weapon.ZoomLevel >= 0;
		_zoomLevel = weapon.ZoomLevel;
		// Halo's aiming pitch, in radians, up positive.
		_pitch = -weapon.EyeAngles.pitch.DegreeToRadian();

		var hud = _canvas;
		_canvas = new HudCanvas( Screen.Width, Screen.Height, 1, margins: false );
		try {
			for ( var i = hierarchy.Count - 1; i >= 0; i-- )
				DrawScopeElements( hierarchy[i], ComputeElementStates( hierarchy[i], state, weapon, target ), state, zoomed );
		}
		finally { _canvas = hud; }
	}

	// Each element state flashes past its interface's cutoff and is disabled when empty.
	(bool[] Flashing, bool[] Disabled, int[] Numbers, float?[] Reals) ComputeElementStates( WeaponHudInterfaceTag definition, WeaponState state, HaloWeapon weapon, IHaloUnit target ) {
		var flashing = new bool[8];
		var disabled = new bool[8];
		var numbers = new int[8];
		var reals = new float?[8];
		var p = state.Primary;
		var batteryPercent = 100 - Math.Clamp( (int)(state.Age * 100), 0, 100 );

		flashing[0] = p.Remaining <= definition.TotalAmmoCutoff;
		disabled[0] = p.Remaining == 0;
		flashing[1] = p.Loaded <= definition.LoadedAmmoCutoff && !p.Reloading;
		flashing[2] = state.Heat * 100 >= definition.HeatCutoff;
		flashing[3] = (1 - state.Age) * 100 <= definition.AgeCutoff;
		disabled[3] = batteryPercent == 0;

		for ( var i = 0; i < 8; i++ ) {
			if ( !flashing[i] ) _weaponFlash[i] = -1;
			else if ( _weaponFlash[i] < 0 ) _weaponFlash[i] = Time.Now;
		}

		numbers[0] = p.Remaining;
		numbers[1] = p.Loaded;
		numbers[2] = (int)(state.Heat * 255);
		numbers[3] = (int)((1 - state.Age) * 100);

		// The sniper's range readout: distance and height to the autoaim target in meters, 3.048 per world unit.
		if ( target is not null ) {
			var delta = target.GameObject.WorldPosition - weapon.EyePosition;
			reals[6] = HaloUnits.ToWorldUnits( delta.Length ) * 3.048f;
			reals[7] = HaloUnits.ToWorldUnits( delta.z ) * 3.048f;
		}

		return (flashing, disabled, numbers, reals);
	}

	void DrawScopeElements( WeaponHudInterfaceTag definition, (bool[] Flashing, bool[] Disabled, int[] Numbers, float?[] Reals) set, WeaponState state, bool zoomed ) {
		var anchor = HudAnchor.Parse( definition.Anchor, HudAnchor.TopLeft );

		foreach ( var element in definition.StaticElements ) {
			if ( element is null || element.AllowedViewType == "splitscreen" ) continue;
			var elementAnchor = HudAnchor.Parse( element.Anchor, anchor );
			if ( elementAnchor.X != 0 || elementAnchor.Y != 0 ) continue;

			// The sniper's pitch ladders fade in with zoom, so there's nothing to draw unzoomed.
			if ( element.MultitextureOverlays is { Count: > 0 } && !zoomed ) continue;

			var index = StateIndex( element.StateAttachedTo );
			if ( element.MultitextureOverlays is not { Count: > 0 } overlays ) {
				DrawStatic( element, elementAnchor, set.Flashing[index], set.Disabled[index], _weaponFlash[index] );
				continue;
			}

			// Halo draws the base bitmap under the overlays, but the sniper's base is fully transparent.
			var color = set.Disabled[index] ? HudColor.FromArgb( element.DisabledColor )
				: set.Flashing[index] ? FlashColor( element, _weaponFlash[index] )
				: HudColor.FromArgb( element.DefaultColor );
			foreach ( var overlay in overlays )
				DrawMultitexture( element, elementAnchor, overlay, color );
		}

		foreach ( var number in definition.NumberElements ) {
			if ( number is null || number.AllowedViewType == "splitscreen" ) continue;
			var numberAnchor = HudAnchor.Parse( number.Anchor, anchor );
			if ( numberAnchor.X != 0 || numberAnchor.Y != 0 ) continue;
			if ( (number.Flags?.Contains( "only show when zoomed" ) ?? false) && !zoomed ) continue;

			var index = StateIndex( number.StateAttachedTo );
			var divide = (number.WeaponSpecificFlags?.Contains( "divide number by clip size" ) ?? false) && state.LoadedMaximum > 0;
			var magazineSize = divide ? state.LoadedMaximum : 1;
			int value, fraction;

			if ( number.NumberOfFractionalDigits > 0 ) {
				if ( set.Reals[index] is not float real ) continue;
				fraction = (int)(MathF.Abs( real * 10000 ) % 10000);
				value = (int)(real / magazineSize);
			}
			else {
				value = set.Numbers[index] / magazineSize;
				fraction = -1;
			}

			var color = set.Disabled[index] ? HudColor.FromArgb( number.DisabledColor )
				: set.Flashing[index] ? FlashColor( number, _weaponFlash[index] )
				: HudColor.FromArgb( number.DefaultColor );
			DrawNumber( number, numberAnchor, value, fraction, color );
		}
	}

	static int StateIndex( string name ) {
		var index = Array.IndexOf( ElementStates, name );
		return index < 0 ? 0 : index;
	}

	// multitexture overlays

	[ConVar( "halo_scope_ladders", Help = "Draw the sniper scope's pitch ladders (the weapon HUD's multitexture overlays).", Saved = true )]
	public static bool Ladders { get; set; } = true;

	int _zoomLevel = -1;
	float _pitch;

	// Up to two point-sampled maps over the element, each at uv / scale + offset, moved and faded by the effectors.
	// They combine by the zero-to-one function and draw with the framebuffer blend, tinted by the element color.
	// The sniper's ladders are a vignette mask times a tick ladder, scrolled by pitch and faded in by zoom.
	bool DrawMultitexture( HudStaticElement element, HudAnchor anchor, HudMultitextureOverlay overlay, Color color ) {
		if ( !Ladders || overlay is null || element.SequenceIndex is not int sequence ) return false;

		var primary = HudTextures.Pixels( PageTexture( overlay.Primary, 0, out _ ) );
		if ( primary is null ) return false;
		var secondary = string.IsNullOrEmpty( overlay.Secondary ) ? null : HudTextures.Pixels( PageTexture( overlay.Secondary, 0, out _ ) );

		var offset0 = Pair( overlay.PrimaryOffset, 0 );
		var offset1 = Pair( overlay.SecondaryOffset, 0 );
		float fade0 = 1, fade1 = 1;

		if ( overlay.Effectors is not null ) {
			foreach ( var effector in overlay.Effectors ) {
				if ( effector is null ) continue;

				float source = effector.Source switch {
					"player pitch" => _pitch,
					"weapon zoom level" => _zoomLevel,
					"explicit" => effector.InBounds is { Length: > 0 } explicitValue ? explicitValue[0] : 0,
					_ => 0,
				};

				var input = Pair( effector.InBounds, 0 );
				var output = Pair( effector.OutBounds, 0 );
				var value = input.x == input.y || output.x == output.y
					? output.x
					: output.x + (output.y - output.x) * Math.Clamp( (source - input.x) / (input.y - input.x), 0, 1 );

				var secondaryMap = effector.Destination == "secondary map";
				if ( effector.Destination != "primary map" && !secondaryMap ) continue;

				switch ( effector.DestinationType ) {
					case "horizontal offset":
						if ( secondaryMap ) offset1.x += value; else offset0.x += value;
						break;
					case "vertical offset":
						if ( secondaryMap ) offset1.y += value; else offset0.y += value;
						break;
					case "fade 0 1":
						if ( secondaryMap ) fade1 = Math.Clamp( value, 0, 1 ); else fade0 = Math.Clamp( value, 0, 1 );
						break;
				}
			}
		}

		// Halo's map scale divides the element uv.
		var scale0 = Inverse( Pair( overlay.PrimaryScale, 1 ) );
		var scale1 = Inverse( Pair( overlay.SecondaryScale, 1 ) );
		var wrap0 = overlay.PrimaryWrapMode != "clamp";
		var wrap1 = overlay.SecondaryWrapMode != "clamp";

		var function = overlay.ZeroToOneBlendFunction switch {
			"add" => 1,
			"subtract" => 2,
			"multiply2x" or "multiply 2x" => 3,
			_ => 0,
		};

		// The overlay covers the element's base bitmap bounds.
		if ( FrameTexture( element.InterfaceBitmap, sequence, 0, out var size ) is null ) return false;
		var point = _canvas.Point( anchor, element.AnchorOffset );
		var rect = _canvas.Bounds( anchor, point, Scaled( size, element ) );
		var blend = overlay.FramebufferBlendFunction switch {
			"add" => BlendMode.Lighten,
			"multiply" => BlendMode.Multiply,
			_ => BlendMode.Normal,
		};

		// The HUD shader samples the maps per pixel, so turning rebuilds nothing.
		if ( HudTextures.RawCopy( PageTexture( overlay.Primary, 0, out _ ) ) is not { } primaryRaw ) return false;
		var secondaryRaw = secondary is null ? null : HudTextures.RawCopy( PageTexture( overlay.Secondary, 0, out _ ) );
		if ( secondary is not null && secondaryRaw is null ) return false;

		var flags = (wrap0 ? 1 : 0) | (wrap1 ? 2 : 0) | (secondary is not null ? 4 : 0);
		_painter.SetBlendMode( blend );
		_painter.DrawEffect( primaryRaw, rect, color, new HaloHudRenderer.QuadEffect {
			Kind = HaloHudRenderer.QuadKind.Multitexture,
			Secondary = secondaryRaw,
			A = new Vector4( scale0.x, scale0.y, offset0.x, offset0.y ),
			B = new Vector4( scale1.x, scale1.y, offset1.x, offset1.y ),
			C = new Vector4( fade0, fade1, function, flags ),
			D = new Vector4( primary.Width, primary.Height, 0, 0 ),
		} );
		_painter.SetBlendMode( BlendMode.Normal );
		return true;
	}

	static Vector2 Pair( float[] values, float fallback ) => new(
		values is { Length: > 0 } ? values[0] : fallback,
		values is { Length: > 1 } ? values[1] : fallback );

	static Vector2 Inverse( Vector2 scale ) => new( scale.x == 0 ? 1 : 1 / scale.x, scale.y == 0 ? 1 : 1 / scale.y );

	// grenades

	// Set once the player first has grenades, and kept when they run out, so throwing the last one shows 0 as in MCC.
	bool _hasGrenadeType;

	// The current grenade type's count, flashing at or below the cutoff and disabled at zero.
	bool DrawGrenadeHud( HaloWeapon weapon ) {
		if ( weapon.Frags + weapon.Plasmas > 0 ) _hasGrenadeType = true;
		if ( !_hasGrenadeType ) return false;
		if ( weapon.Tag?.HasFlag( "prevents grenade throwing" ) ?? false ) return false;

		var type = Math.Clamp( weapon.SelectedGrenade, 0, 1 );
		if ( type >= _data.GrenadeHuds.Count ) return false;
		var definition = _data.GrenadeHud( _data.GrenadeHuds[type] );
		if ( definition is null ) return false;

		var count = type == 0 ? weapon.Frags : weapon.Plasmas;
		var flashing = count <= definition.FlashCutoff;
		var disabled = count == 0;
		if ( !flashing ) _grenadeFlash = -1;
		else if ( _grenadeFlash < 0 ) _grenadeFlash = Time.Now;

		var anchor = HudAnchor.Parse( definition.Anchor, HudAnchor.TopLeft );
		var drew = DrawStatic( definition.Background, anchor, flashing, disabled, _grenadeFlash );
		drew |= DrawStatic( definition.TotalGrenadesBackground, anchor, flashing, disabled, _grenadeFlash );

		var numbers = definition.TotalGrenadesNumbers;
		if ( numbers is not null && numbers.MaximumNumberOfDigits > 0 ) {
			var color = disabled ? HudColor.FromArgb( numbers.DisabledColor )
				: flashing ? FlashColor( numbers, _grenadeFlash )
				: HudColor.FromArgb( numbers.DefaultColor );
			drew |= DrawNumber( numbers, anchor, count, -1, color );
		}

		var overlayFlags = (flashing ? 1 : 0) | (disabled ? 2 : 0);
		if ( overlayFlags == 0 ) overlayFlags |= 4;
		overlayFlags |= 8;
		drew |= DrawOverlays( definition.TotalGrenadesOverlayBitmap, definition.TotalGrenadesOverlays, anchor, overlayFlags,
			GrenadeOverlayTypes, flashing, _grenadeFlash );

		return drew;
	}

	static int OverlayMask( string[] types, string[] names ) {
		var mask = 0;
		if ( types is null ) return 0;
		for ( var i = 0; i < names.Length; i++ )
			if ( types.Contains( names[i] ) ) mask |= 1 << i;
		return mask;
	}

	// Draws overlays whose type matches the flags. Flashing ones take their flash color and animate at their frame rate.
	bool DrawOverlays( string bitmapTag, List<HudOverlay> overlays, HudAnchor anchor, int flags, string[] typeNames, bool flashing, float reference ) {
		if ( overlays is null || string.IsNullOrEmpty( bitmapTag ) ) return false;

		var drew = false;
		foreach ( var item in overlays ) {
			if ( item?.SequenceIndex is not int sequence ) continue;
			if ( (OverlayMask( item.Type, typeNames ) & flags) == 0 ) continue;

			var flashes = (item.Flags?.Contains( "flashes when active" ) ?? false) && flashing;
			var color = flashes ? FlashColor( item, reference ) : HudColor.FromArgb( item.DefaultColor );
			var frame = flashes && item.FrameRate > 0 && reference >= 0 ? (int)((Time.Now - reference) / item.FrameRate) : 0;
			drew |= DrawSprite( bitmapTag, sequence, frame, anchor, item, color );
		}

		return drew;
	}
}
