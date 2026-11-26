using System;

namespace Blam.Halo1.Tags;

// Inside the namespace so these win over s&box's global Vector2/Vector3 when this compiles into the mount.
using Vector3 = System.Numerics.Vector3;

/// <summary>
/// Reader for <c>spla</c> (shader_transparent_plasma) tags: the energy shells drawn over
/// characters' shields (elites, cyborgs, the monitor, sentinels).
/// </summary>
/// <remarks>
/// Offsets: intensity source 0x2C and exponent 0x30, offset source 0x34, amount 0x38 (world units
/// the shell is pushed out along the normals) and exponent 0x3C, perpendicular brightness and
/// tint 0x60/0x64, parallel brightness and tint 0x70/0x74, tint color source 0x80, primary noise
/// map animation period 0xC0, direction 0xC4, scale 0xD0 and map 0xD4, secondary noise map the
/// same at 0x108/0x10C/0x118/0x11C. 332 bytes. Exponents of 0 mean 1.
/// </remarks>
public sealed class ShaderTransparentPlasmaTag
{
	/// <summary>
	/// The object function (A-D out) driving the shell's brightness; with none it's 1.
	/// </summary>
	public ChangeColorSource IntensitySource { get; }
	public float IntensityExponent { get; }

	/// <summary>
	/// The object function driving how far the shell is pushed out from the model.
	/// </summary>
	public ChangeColorSource OffsetSource { get; }
	public float OffsetAmount { get; }
	public float OffsetExponent { get; }

	public float PerpendicularBrightness { get; }
	public Vector3 PerpendicularTint { get; }
	public float ParallelBrightness { get; }
	public Vector3 ParallelTint { get; }

	/// <summary>
	/// The object's change color (A-D) multiplying both tints, or none.
	/// </summary>
	public ChangeColorSource TintColorSource { get; }

	public float PrimaryAnimationPeriod { get; }
	public Vector3 PrimaryAnimationDirection { get; }
	public float PrimaryNoiseMapScale { get; }
	public TagEntry PrimaryNoiseMap { get; }
	public float SecondaryAnimationPeriod { get; }
	public Vector3 SecondaryAnimationDirection { get; }
	public float SecondaryNoiseMapScale { get; }
	public TagEntry SecondaryNoiseMap { get; }

	public ShaderTransparentPlasmaTag( CacheFile cache, TagEntry tag )
	{
		if ( tag.Class != TagClasses.ShaderTransparentPlasma )
			throw new ArgumentException( $"{tag} is not a shader_transparent_plasma tag", nameof( tag ) );

		var data = cache.TagData;
		var a = tag.Address;

		IntensitySource = (ChangeColorSource)data.U16( a + 0x2C );
		IntensityExponent = DefaultOne( data.F32( a + 0x30 ) );
		OffsetSource = (ChangeColorSource)data.U16( a + 0x34 );
		OffsetAmount = data.F32( a + 0x38 );
		OffsetExponent = DefaultOne( data.F32( a + 0x3C ) );
		PerpendicularBrightness = data.F32( a + 0x60 );
		PerpendicularTint = data.Vec3( a + 0x64 );
		ParallelBrightness = data.F32( a + 0x70 );
		ParallelTint = data.Vec3( a + 0x74 );
		TintColorSource = (ChangeColorSource)data.U16( a + 0x80 );
		PrimaryAnimationPeriod = DefaultOne( data.F32( a + 0xC0 ) );
		PrimaryAnimationDirection = data.Vec3( a + 0xC4 );
		PrimaryNoiseMapScale = data.F32( a + 0xD0 );
		PrimaryNoiseMap = cache.GetTagReference( data, a + 0xD4 );
		SecondaryAnimationPeriod = DefaultOne( data.F32( a + 0x108 ) );
		SecondaryAnimationDirection = data.Vec3( a + 0x10C );
		SecondaryNoiseMapScale = data.F32( a + 0x118 );
		SecondaryNoiseMap = cache.GetTagReference( data, a + 0x11C );
	}

	static float DefaultOne( float value ) => value == 0 ? 1 : value;
}
