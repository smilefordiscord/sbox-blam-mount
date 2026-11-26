using System;

namespace Blam.Halo1.Tags;

// Inside the namespace so these win over s&box's global Vector2/Vector3 when this compiles into the mount.
using Vector3 = System.Numerics.Vector3;

[Flags]
public enum MeterShaderFlags : ushort
{
	Decal = 0x1,
	TwoSided = 0x2,
	FlashColorIsNegative = 0x4,
	TintMode2 = 0x8,
	Unfiltered = 0x10,
}

/// <summary>
/// Reader for <c>smet</c> (shader_transparent_meter) tags: gauges on vehicles and weapons, such
/// as the Warthog's speedometer and the plasma weapons' heat meters.
/// </summary>
/// <remarks>
/// Offsets: flags 0x28, map 0x4C, gradient min/max color 0x7C/0x88, background color 0x94, flash
/// color 0xA0, tint color 0xAC (RGB), meter/background transparency 0xB8/0xBC, then the object
/// functions (A-D out) driving meter brightness 0xD8, flash brightness 0xDA, value 0xDC, gradient
/// 0xDE and flash extension 0xE0. 260 bytes.
///
/// The map's blue channel is a position along the meter; texels below the meter value are lit
/// with a gradient from the min to the max color (reaching max at the gradient value), the rest
/// with the background color. The map's alpha scales the result and texels with zero alpha are
/// discarded. With no source a value is 1 (a full meter).
/// </remarks>
public sealed class ShaderTransparentMeterTag
{
	public MeterShaderFlags Flags { get; }
	public TagEntry Map { get; }
	public Vector3 GradientMinColor { get; }
	public Vector3 GradientMaxColor { get; }
	public Vector3 BackgroundColor { get; }
	public Vector3 FlashColor { get; }

	/// <summary>
	/// Multiplies what's behind the meter where the map isn't fully transparent (unless tint mode 2
	/// is set, where it's the meter's own color).
	/// </summary>
	public Vector3 TintColor { get; }

	/// <summary>
	/// Only used with tint mode 2: how much of what's behind shows through the lit part and the
	/// background.
	/// </summary>
	public float MeterTransparency { get; }
	public float BackgroundTransparency { get; }

	public ChangeColorSource MeterBrightnessSource { get; }
	public ChangeColorSource FlashBrightnessSource { get; }

	/// <summary>
	/// The meter's fill level.
	/// </summary>
	public ChangeColorSource ValueSource { get; }

	/// <summary>
	/// Where the gradient reaches its max color.
	/// </summary>
	public ChangeColorSource GradientSource { get; }

	/// <summary>
	/// Where the flash starts; the flash color is added between it and the meter value.
	/// </summary>
	public ChangeColorSource FlashExtensionSource { get; }

	public ShaderTransparentMeterTag( CacheFile cache, TagEntry tag )
	{
		if ( tag.Class != TagClasses.ShaderTransparentMeter )
			throw new ArgumentException( $"{tag} is not a shader_transparent_meter tag", nameof( tag ) );

		var data = cache.TagData;
		var a = tag.Address;

		Flags = (MeterShaderFlags)data.U16( a + 0x28 );
		Map = cache.GetTagReference( data, a + 0x4C );
		GradientMinColor = data.Vec3( a + 0x7C );
		GradientMaxColor = data.Vec3( a + 0x88 );
		BackgroundColor = data.Vec3( a + 0x94 );
		FlashColor = data.Vec3( a + 0xA0 );
		TintColor = data.Vec3( a + 0xAC );
		MeterTransparency = data.F32( a + 0xB8 );
		BackgroundTransparency = data.F32( a + 0xBC );
		MeterBrightnessSource = (ChangeColorSource)data.U16( a + 0xD8 );
		FlashBrightnessSource = (ChangeColorSource)data.U16( a + 0xDA );
		ValueSource = (ChangeColorSource)data.U16( a + 0xDC );
		GradientSource = (ChangeColorSource)data.U16( a + 0xDE );
		FlashExtensionSource = (ChangeColorSource)data.U16( a + 0xE0 );
	}
}
