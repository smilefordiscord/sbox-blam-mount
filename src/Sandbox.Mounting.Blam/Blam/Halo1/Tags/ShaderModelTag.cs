using System;

namespace Blam.Halo1.Tags;

// Inside the namespace so these win over s&box's global Vector2/Vector3 when this compiles into the mount.
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;

[Flags]
public enum ModelShaderFlags : ushort
{
	DetailAfterReflection = 0x1,
	TwoSided = 0x2,
	NotAlphaTested = 0x4,
	AlphaBlendedDecal = 0x8,
	TrueAtmosphericFog = 0x10,
	DisableTwoSidedCulling = 0x20,
	MultipurposeMapUsesXboxChannelOrder = 0x40,
}

/// <summary>
/// Which mask limits where a model shader's detail map applies. Each mask is a channel of the
/// multipurpose map; "inverse" uses one minus the mask.
/// </summary>
public enum ModelDetailMask : ushort
{
	None = 0,
	ReflectionMaskInverse = 1,
	ReflectionMask = 2,
	SelfIlluminationMaskInverse = 3,
	SelfIlluminationMask = 4,
	ChangeColorMaskInverse = 5,
	ChangeColorMask = 6,

	/// <summary>
	/// The auxiliary mask (the tag calls it "multipurpose map alpha", its channel in the Xbox order).
	/// </summary>
	AuxiliaryMaskInverse = 7,
	AuxiliaryMask = 8,
}

/// <summary>
/// One of an object's four change colors (A-D), or none. Shaders pick which one tints them.
/// The same enum names the object function (A-D "out") driving a texture animation.
/// </summary>
public enum ChangeColorSource : ushort
{
	None = 0,
	A = 1,
	B = 2,
	C = 3,
	D = 4,
}

/// <summary>
/// A model shader texture animation and the object function that drives it, if any.
/// </summary>
public readonly record struct ModelMapAnimation( ChangeColorSource Source, MapAnimation Animation );

/// <summary>
/// Reader for <c>soso</c> (shader_model) tags, used by objects: scenery, vehicles, weapons, characters.
/// </summary>
/// <remarks>
/// Offsets: flags 0x28, translucency 0x38, change color source 0x4C, self-illumination flags 0x6C,
/// color source 0x70, animation function 0x72, period 0x74, color bounds 0x78/0x84, map U/V scale
/// 0x9C/0xA0 (applies to every map), base map 0xA4, multipurpose map 0xBC, detail function 0xD4,
/// detail mask 0xD6, detail map scale 0xD8, detail map 0xDC, detail map V scale 0xEC, U/V/rotation
/// animations 0xFC/0x10C/0x11C (16 bytes each: source, function, period, phase, scale) with the
/// rotation center at 0x12C, reflection falloff/cutoff distance 0x13C/0x140, perpendicular
/// brightness and tint 0x144/0x148, parallel brightness and tint 0x154/0x158, reflection cube map
/// 0x164.
///
/// The base map's alpha is the alpha test mask unless the shader says otherwise. The
/// multipurpose map packs four masks into its channels, in an order that depends on a flag
/// (see <see cref="MultipurposeChannels"/>).
/// </remarks>
public sealed class ShaderModelTag
{
	public ModelShaderFlags Flags { get; }

	/// <summary>
	/// How much of the main light reaches faces turned away from it (0-1): Halo lights a vertex by
	/// the larger of N.L and -N.L times this.
	/// </summary>
	public float Translucency { get; }

	/// <summary>
	/// Which of the object's change colors tints the base map where the change color mask is set.
	/// </summary>
	public ChangeColorSource ChangeColorSource { get; }

	/// <summary>
	/// Every object starts its self-illumination animation at a random point unless this is set.
	/// </summary>
	public bool SelfIlluminationNoRandomPhase { get; }
	public Vector3 SelfIlluminationLower { get; }
	public Vector3 SelfIlluminationUpper { get; }

	/// <summary>
	/// An extra color the self-illumination is multiplied by: one of the object's change colors,
	/// or none for white.
	/// </summary>
	public ChangeColorSource SelfIlluminationSource { get; }
	public AnimationFunction SelfIlluminationFunction { get; }

	/// <summary>
	/// Seconds per cycle of the self-illumination animation.
	/// </summary>
	public float SelfIlluminationPeriod { get; }
	public Vector2 MapScale { get; }
	public TagEntry BaseMap { get; }
	public TagEntry MultipurposeMap { get; }
	public DetailMapFunction DetailFunction { get; }
	public ModelDetailMask DetailMask { get; }
	public float DetailMapScale { get; }
	public float DetailMapVScale { get; }
	public TagEntry DetailMap { get; }

	/// <summary>
	/// Texture scrolling, applied to every map. The source says which object function drives it;
	/// with no source it runs on time.
	/// </summary>
	public ModelMapAnimation UAnimation { get; }
	public ModelMapAnimation VAnimation { get; }

	/// <summary>
	/// Texture rotation about <see cref="RotationCenter"/>; its scale is in degrees.
	/// </summary>
	public ModelMapAnimation RotationAnimation { get; }
	public Vector2 RotationCenter { get; }

	/// <summary>
	/// Distance (world units) at which the reflection starts fading out, and where it's gone (0 for never).
	/// </summary>
	public float ReflectionFalloffDistance { get; }
	public float ReflectionCutoffDistance { get; }
	public float PerpendicularBrightness { get; }
	public Vector3 PerpendicularTint { get; }
	public float ParallelBrightness { get; }
	public Vector3 ParallelTint { get; }
	public TagEntry ReflectionCubeMap { get; }

	public bool IsAlphaTested => !Flags.HasFlag( ModelShaderFlags.NotAlphaTested );

	/// <summary>
	/// The multipurpose map channel (0-3 for R, G, B, A) holding each mask. Gearbox reordered the
	/// channels when porting Halo to PC, and MCC reads that order unless the shader asks for the
	/// original Xbox one:
	/// <code>
	///                     Xbox   PC/MCC
	///   reflection mask    R      B
	///   self-illumination  G      G
	///   change color mask  B      A
	///   auxiliary mask     A      R
	/// </code>
	/// </summary>
	public (int Reflection, int SelfIllumination, int ChangeColor, int Auxiliary) MultipurposeChannels =>
		Flags.HasFlag( ModelShaderFlags.MultipurposeMapUsesXboxChannelOrder ) ? (0, 1, 2, 3) : (2, 1, 3, 0);

	/// <summary>
	/// A steady approximation of the self-illumination color. The animation function picks a
	/// point between the lower and upper bound: "one" is the upper bound, "zero" the lower, and
	/// anything that moves averages out to the middle.
	/// </summary>
	public Vector3 SelfIlluminationColor => SelfIlluminationFunction switch
	{
		AnimationFunction.One => SelfIlluminationUpper,
		AnimationFunction.Zero => SelfIlluminationLower,
		_ => (SelfIlluminationLower + SelfIlluminationUpper) * 0.5f,
	};

	public ShaderModelTag( CacheFile cache, TagEntry tag )
	{
		if ( tag.Class != TagClasses.ShaderModel )
			throw new ArgumentException( $"{tag} is not a shader_model tag", nameof( tag ) );

		var data = cache.TagData;
		var a = tag.Address;

		Flags = (ModelShaderFlags)data.U16( a + 0x28 );
		Translucency = data.F32( a + 0x38 );
		ChangeColorSource = (ChangeColorSource)data.U16( a + 0x4C );
		SelfIlluminationNoRandomPhase = (data.U16( a + 0x6C ) & 0x1) != 0;
		SelfIlluminationSource = (ChangeColorSource)data.U16( a + 0x70 );
		SelfIlluminationFunction = (AnimationFunction)data.U16( a + 0x72 );
		SelfIlluminationPeriod = DefaultOne( data.F32( a + 0x74 ) );
		SelfIlluminationLower = data.Vec3( a + 0x78 );
		SelfIlluminationUpper = data.Vec3( a + 0x84 );
		MapScale = new Vector2( DefaultOne( data.F32( a + 0x9C ) ), DefaultOne( data.F32( a + 0xA0 ) ) );
		BaseMap = cache.GetTagReference( data, a + 0xA4 );
		MultipurposeMap = cache.GetTagReference( data, a + 0xBC );
		DetailFunction = (DetailMapFunction)data.U16( a + 0xD4 );
		DetailMask = (ModelDetailMask)data.U16( a + 0xD6 );
		DetailMapScale = DefaultOne( data.F32( a + 0xD8 ) );
		DetailMap = cache.GetTagReference( data, a + 0xDC );
		DetailMapVScale = DefaultOne( data.F32( a + 0xEC ) );
		UAnimation = ReadAnimation( data, a + 0xFC, 1 );
		VAnimation = ReadAnimation( data, a + 0x10C, 1 );
		RotationAnimation = ReadAnimation( data, a + 0x11C, 360 );
		RotationCenter = data.Vec2( a + 0x12C );
		ReflectionFalloffDistance = data.F32( a + 0x13C );
		ReflectionCutoffDistance = data.F32( a + 0x140 );
		PerpendicularBrightness = data.F32( a + 0x144 );
		PerpendicularTint = data.Vec3( a + 0x148 );
		ParallelBrightness = data.F32( a + 0x154 );
		ParallelTint = data.Vec3( a + 0x158 );
		ReflectionCubeMap = cache.GetTagReference( data, a + 0x164 );
	}

	static ModelMapAnimation ReadAnimation( IO.MemoryRegion data, uint address, float defaultScale )
	{
		var scale = data.F32( address + 0xC );
		return new ModelMapAnimation(
			(ChangeColorSource)data.U16( address ),
			new MapAnimation(
				(AnimationFunction)data.U16( address + 0x2 ),
				DefaultOne( data.F32( address + 0x4 ) ),
				data.F32( address + 0x8 ),
				scale == 0 ? defaultScale : scale ) );
	}

	static float DefaultOne( float value ) => value == 0 ? 1 : value;
}
