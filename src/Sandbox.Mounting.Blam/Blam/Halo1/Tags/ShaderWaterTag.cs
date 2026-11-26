using System;
using System.Collections.Generic;

namespace Blam.Halo1.Tags;

// Inside the namespace so these win over s&box's global Vector2/Vector3 when this compiles into the mount.
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;

[Flags]
public enum WaterShaderFlags : ushort
{
	BaseMapAlphaModulatesReflection = 0x1,
	BaseMapColorModulatesBackground = 0x2,
	AtmosphericFog = 0x4,
	DrawBeforeFog = 0x8,
}

/// <summary>
/// One scrolling ripple layer. The ripple maps are bump maps, and each layer scrolls one of them
/// in its own direction and speed.
/// </summary>
/// <param name="AnimationAngle">Scroll direction in radians.</param>
/// <param name="MapIndex">Index into the ripple maps tag's bitmaps.</param>
public sealed record WaterRipple( float ContributionFactor, float AnimationAngle, float AnimationVelocity, Vector2 MapOffset, int MapRepeats, int MapIndex );

/// <summary>
/// Reader for <c>swat</c> (shader_transparent_water) tags.
/// </summary>
/// <remarks>
/// The base map's color tints what's behind the water and its alpha scales the reflection. The
/// reflection comes from a cube map, brightened and tinted differently when looking straight
/// down at the water (perpendicular) or across it (parallel).
///
/// Offsets: flags 0x28, base map 0x4C, perpendicular brightness/tint 0x6C/0x70, parallel
/// brightness/tint 0x7C/0x80, reflection map 0x9C, ripple animation angle/velocity 0xBC/0xC0,
/// ripple scale 0xC4, ripple maps 0xC8, ripple mipmap levels 0xD8 (int16), mipmap fade factor 0xDC,
/// mipmap detail bias 0xE0, ripples block 0x124 (76 bytes each).
///
/// Halo combines the ripple layers into one bump texture (a contribution-weighted average) with
/// its own mip chain, and fades each mip toward flat so the last of the ripple mipmap levels has
/// the fade factor's flatness. Distant water is smooth because of that.
/// </remarks>
public sealed class ShaderWaterTag
{
	public WaterShaderFlags Flags { get; }
	public TagEntry BaseMap { get; }

	public float PerpendicularBrightness { get; }
	public Vector3 PerpendicularTint { get; }
	public float ParallelBrightness { get; }
	public Vector3 ParallelTint { get; }

	public TagEntry ReflectionMap { get; }

	/// <summary>
	/// Direction (radians) the combined ripple texture scrolls in, at <see cref="RippleAnimationVelocity"/>.
	/// </summary>
	public float RippleAnimationAngle { get; }
	public float RippleAnimationVelocity { get; }
	public float RippleScale { get; }
	public TagEntry RippleMaps { get; }
	public IReadOnlyList<WaterRipple> Ripples { get; }

	/// <summary>
	/// How many mip levels the combined ripple texture fades over (at least 1).
	/// </summary>
	public int RippleMipmapLevels { get; }

	/// <summary>
	/// Flatness of the last ripple mip level: 0 keeps the full ripples, 1 is flat.
	/// </summary>
	public float RippleMipmapFadeFactor { get; }
	public float RippleMipmapDetailBias { get; }

	public ShaderWaterTag( CacheFile cache, TagEntry tag )
	{
		if ( tag.Class != TagClasses.ShaderWater )
			throw new ArgumentException( $"{tag} is not a shader_transparent_water tag", nameof( tag ) );

		var data = cache.TagData;
		var a = tag.Address;

		Flags = (WaterShaderFlags)data.U16( a + 0x28 );
		BaseMap = cache.GetTagReference( data, a + 0x4C );

		PerpendicularBrightness = data.F32( a + 0x6C );
		PerpendicularTint = data.Vec3( a + 0x70 );
		ParallelBrightness = DefaultOne( data.F32( a + 0x7C ) );
		ParallelTint = data.Vec3( a + 0x80 );

		ReflectionMap = cache.GetTagReference( data, a + 0x9C );

		RippleAnimationAngle = data.F32( a + 0xBC );
		RippleAnimationVelocity = data.F32( a + 0xC0 );
		RippleScale = DefaultOne( data.F32( a + 0xC4 ) );
		RippleMaps = cache.GetTagReference( data, a + 0xC8 );
		RippleMipmapLevels = Math.Max( 1, (int)data.I16( a + 0xD8 ) );
		RippleMipmapFadeFactor = data.F32( a + 0xDC );
		RippleMipmapDetailBias = data.F32( a + 0xE0 );

		var ripples = new List<WaterRipple>();
		foreach ( var ripple in data.Block( a + 0x124, 0x4C ) )
		{
			ripples.Add( new WaterRipple(
				ContributionFactor: DefaultOne( data.F32( ripple + 0x4 ) ),
				AnimationAngle: data.F32( ripple + 0x28 ),
				AnimationVelocity: data.F32( ripple + 0x2C ),
				MapOffset: data.Vec2( ripple + 0x30 ),
				MapRepeats: Math.Max( 1, (int)data.I16( ripple + 0x38 ) ),
				MapIndex: data.I16( ripple + 0x3A ) ) );
		}

		Ripples = ripples;
	}

	static float DefaultOne( float value ) => value == 0 ? 1 : value;
}
