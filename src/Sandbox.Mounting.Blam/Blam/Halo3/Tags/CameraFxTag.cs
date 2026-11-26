using System;
using System.Collections.Generic;

namespace Blam.Halo3.Tags;

// Inside the namespace so these win over s&box's global vector types when this compiles into the mount.
using Vector3 = System.Numerics.Vector3;

// A camera_fx_settings (cfxs, 0x170) tag's exposure, bloom and colour grading; a setting flagged "use default" (bit 0 of its flags) takes the next tag's in its chain (a cluster's: the scenario's, then globals\default's).
public sealed class CameraFxTag
{
	public const string DefaultPath = @"globals\default";

	public TagEntry Tag { get; }
	public TagEntry Defaults { get; }

	// The exposure block (+0x00): flags (bit 2 auto exposure), fixed exposure, adaptation rate, range, auto exposure target brightness, delay (seconds of targets that must agree before it moves).
	public int ExposureFlags { get; }
	public float Exposure { get; }
	public float ExposureRate { get; }
	public float ExposureMin { get; }
	public float ExposureMax { get; }
	public float AutoExposureBrightness { get; }
	public float ExposureDelay { get; }
	public bool AutoExposure => (ExposureFlags & 4) != 0;

	// exposure_downsample's share of log2( mean luminance ) against the mean of log2 luminance (+0x24).
	public float ExposureMeasureMix { get; }

	public float AntiBloom { get; }
	public float BloomPoint { get; }
	public float InherentBloom { get; }
	public float BloomIntensity { get; }
	public Vector3 LargeColor { get; }
	public Vector3 MediumColor { get; }
	public Vector3 SmallColor { get; }

	// Self-illumination exposure: preferred stops and how far (0-1) it follows the scene's exposure instead.
	public float SelfIllumPreferredExposure { get; }
	public float SelfIllumExposureChange { get; }

	public float BlingIntensity { get; }
	public float BlingLength { get; }
	public int BlingSpikes { get; }

	public uint SsaoFlags { get; }
	public uint ColorGradingFlags { get; }

	// The colour grading blocks' element counts: curves, brightness/contrast, hue/saturation/lightness/vibrance, colorize, selective colour, colour balance.
	public IReadOnlyList<int> ColorGradingCounts { get; }

	// Enabled brightness/contrast elements (brightness, contrast).
	public IReadOnlyList<(float Brightness, float Contrast)> BrightnessContrast { get; }

	// Enabled hue/saturation/lightness/vibrance elements.
	public IReadOnlyList<(float Hue, float Saturation, float Lightness, float Vibrance)> HueSaturation { get; }

	public uint LightshaftFlags { get; }

	public bool ColorGradingEnabled => (ColorGradingFlags & 1) == 0 && (ColorGradingFlags & 2) != 0;

	readonly TagView[] _chain;

	CameraFxTag( TagEntry tag, TagEntry defaults, TagView[] chain )
	{
		Tag = tag;
		Defaults = defaults;
		_chain = chain;
		var d = tag.Data;

		// The first tag in the chain whose setting isn't "use default"; the last one whatever its flags.
		TagView Source( int flagsField )
		{
			for ( var i = 0; i < chain.Length - 1; i++ )
			{
				if ( (chain[i].U16( flagsField ) & 1) == 0 )
					return chain[i];
			}
			return chain[^1];
		}
		float Scalar( int flagsField, int valueField ) => Source( flagsField ).F32( valueField );

		var exposure = Source( 0 );
		ExposureFlags = exposure.U16( 0 );
		Exposure = exposure.F32( 0x04 );
		ExposureRate = exposure.F32( 0x0C );
		ExposureMin = exposure.F32( 0x10 );
		ExposureMax = exposure.F32( 0x14 );
		AutoExposureBrightness = exposure.F32( 0x18 );
		ExposureDelay = exposure.F32( 0x1C );
		ExposureMeasureMix = Scalar( 0x20, 0x24 );

		AntiBloom = Scalar( 0x28, 0x2C );
		BloomPoint = Scalar( 0x30, 0x34 );
		InherentBloom = Scalar( 0x40, 0x44 );
		BloomIntensity = Scalar( 0x50, 0x54 );
		LargeColor = Source( 0x60 ).Vec3( 0x64 );
		MediumColor = Source( 0x70 ).Vec3( 0x74 );
		SmallColor = Source( 0x80 ).Vec3( 0x84 );
		BlingIntensity = Scalar( 0x90, 0x94 );
		BlingLength = Scalar( 0xA0, 0xA4 );
		BlingSpikes = d.I16( 0xC2 );
		SelfIllumPreferredExposure = Scalar( 0xC4, 0xC8 );
		SelfIllumExposureChange = Scalar( 0xD4, 0xD8 );

		SsaoFlags = d.U32( 0xE4 );
		var grading = Source( 0xF4 );
		ColorGradingFlags = grading.U32( 0xF4 );
		ColorGradingCounts = [grading.Count( 0xFC ), grading.Count( 0x108 ), grading.Count( 0x114 ), grading.Count( 0x120 ), grading.Count( 0x12C ), grading.Count( 0x138 )];

		var bc = new List<(float, float)>();
		foreach ( var e in grading.Block( 0x108, 0xC ) )
		{
			if ( (e.U32( 0 ) & 1) != 0 )
				bc.Add( (e.F32( 4 ), e.F32( 8 )) );
		}
		BrightnessContrast = bc;

		var hsl = new List<(float, float, float, float)>();
		foreach ( var e in grading.Block( 0x114, 0x14 ) )
		{
			if ( (e.U32( 0 ) & 1) != 0 )
				hsl.Add( (e.F32( 4 ), e.F32( 8 ), e.F32( 0xC ), e.F32( 0x10 )) );
		}
		HueSaturation = hsl;

		LightshaftFlags = d.U32( 0x144 );
	}

	public static CameraFxTag Get( TagEntry tag )
	{
		if ( tag is null || tag.Data.IsNull )
			return null;
		var defaults = tag.Cache.FindTag( DefaultPath, "cfxs" );
		TagView[] chain = defaults is null || defaults.Data.IsNull || defaults == tag ? [tag.Data] : [tag.Data, defaults.Data];
		return new CameraFxTag( tag, defaults, chain );
	}

	// A cluster's camera fx, falling back on the scenario's (and its defaults) setting by setting.
	public static CameraFxTag Get( TagEntry tag, CameraFxTag parent )
	{
		if ( parent is null )
			return Get( tag );
		if ( tag is null || tag.Data.IsNull || tag == parent.Tag )
			return parent;
		return new CameraFxTag( tag, parent.Defaults, [tag.Data, .. parent._chain] );
	}

	// The scenario's camera fx (scnr +0x660), or globals\default's when it has none.
	public static CameraFxTag ForScenario( CacheFile cache )
	{
		var scenario = cache?.Scenario?.Data ?? default;
		if ( scenario.IsNull )
			return null;
		return Get( scenario.Reference( 0x660 ) ) ?? Get( cache.FindTag( DefaultPath, "cfxs" ) );
	}
}

// One scenario camera fx palette entry (scnr +0x4FC, 0x30): a cluster's cfxs (null: the scenario's) and the values it forces.
// Halo 3 reads one value at +0x18 for both a forced exposure (flag 1, auto exposure off) and a forced auto exposure target (flag 2, on), and flag 8 for both the inherent bloom and the intensity; +0x1C and flag 0x10 aren't read.
public sealed record CameraFxPaletteEntry( string Name, TagEntry CameraFx, int Flags, float ForcedExposure, float ForcedAutoExposureBrightness, float ExposureMin, float ExposureMax, float InherentBloom, float BloomIntensity )
{
	public bool ForcesExposure => (Flags & 1) != 0;
	public bool ForcesAutoExposure => (Flags & 2) != 0;
	public bool OverridesExposureBounds => (Flags & 4) != 0;
	public bool OverridesInherentBloom => (Flags & 8) != 0;
	public bool OverridesBloomIntensity => (Flags & 8) != 0;

	public static IReadOnlyList<CameraFxPaletteEntry> Read( CacheFile cache )
	{
		var list = new List<CameraFxPaletteEntry>();
		var scenario = cache?.Scenario?.Data ?? default;
		if ( scenario.IsNull )
			return list;
		foreach ( var e in scenario.Block( 0x4FC, 0x30 ) )
			list.Add( new CameraFxPaletteEntry( e.StringId( 0 ), e.Reference( 4 ), e.U8( 0x14 ), e.F32( 0x18 ), e.F32( 0x18 ), e.F32( 0x20 ), e.F32( 0x24 ), e.F32( 0x28 ), e.F32( 0x2C ) ) );
		return list;
	}
}

// What Halo 3's post-processing needs for a view: a cfxs (a cluster's falling back on the scenario's) with the palette entry's overrides on top.
public readonly record struct BloomSettings( float BloomPoint, float InherentBloom, float Intensity, Vector3 LargeColor, Vector3 MediumColor, Vector3 SmallColor, float AntiBloom, ExposureSettings Exposure )
{
	// MCC's own defaults (globals\default) when a map has no cfxs at all.
	public static readonly BloomSettings Fallback = new( 1.2f, 0.2f, 0.2f, Vector3.One, Vector3.One, Vector3.One, 0, ExposureSettings.Fallback );

	public static BloomSettings From( CameraFxTag fx, CameraFxPaletteEntry entry = null )
	{
		if ( entry?.CameraFx is { } own )
			fx = CameraFxTag.Get( own, fx );
		if ( fx is null )
			return Fallback;

		var inherent = entry is { OverridesInherentBloom: true } ? entry.InherentBloom : fx.InherentBloom;
		var intensity = entry is { OverridesBloomIntensity: true } ? entry.BloomIntensity : fx.BloomIntensity;
		return new BloomSettings( Finite( fx.BloomPoint, 1.2f ), Finite( inherent, 0.2f ), Finite( intensity, 0.2f ), fx.LargeColor, fx.MediumColor, fx.SmallColor, Finite( fx.AntiBloom, 0 ), ExposureSettings.From( fx, entry ) );
	}

	internal static float Finite( float v, float fallback ) => float.IsFinite( v ) ? v : fallback;
}

// The exposure block as Halo 3's auto exposure uses it: auto or fixed, range, target brightness, rate per frame, how many recent targets must agree (delay x 30 ticks, 1-60), and the measure's mix.
public readonly record struct ExposureSettings( bool Auto, float Fixed, float Min, float Max, float TargetBrightness, float Rate, int AgreeFrames, float MeasureMix )
{
	public static readonly ExposureSettings Fallback = new( true, 0, -6, 6, -4.8365f, 0.01f, 1, 0 );

	public static ExposureSettings From( CameraFxTag fx, CameraFxPaletteEntry entry )
	{
		var auto = fx.AutoExposure;
		var exposure = BloomSettings.Finite( fx.Exposure, 0 );
		var target = BloomSettings.Finite( fx.AutoExposureBrightness, -4.8365f );
		float min = BloomSettings.Finite( fx.ExposureMin, -6 ), max = BloomSettings.Finite( fx.ExposureMax, 6 );
		if ( entry is { ForcesExposure: true } )
			(auto, exposure) = (false, entry.ForcedExposure);
		else if ( entry is { ForcesAutoExposure: true } )
			(auto, target) = (true, entry.ForcedAutoExposureBrightness);
		if ( entry is { OverridesExposureBounds: true } )
			(min, max) = (entry.ExposureMin, entry.ExposureMax);
		if ( min > max )
			(min, max) = (max, min);
		var frames = Math.Clamp( (int)(30 * BloomSettings.Finite( fx.ExposureDelay, 0 )), 1, 60 );
		return new ExposureSettings( auto, exposure, min, max, target, Math.Clamp( BloomSettings.Finite( fx.ExposureRate, 0.01f ), 0, 1 ), frames, BloomSettings.Finite( fx.ExposureMeasureMix, 0 ) );
	}
}
