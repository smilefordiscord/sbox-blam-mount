using System;

namespace Blam.Halo1.Tags;

// Inside the namespace so these win over s&box's global Vector2/Vector3 when this compiles into the mount.
using Vector3 = System.Numerics.Vector3;

public enum EnvironmentShaderType : ushort
{
	/// <summary>
	/// The secondary detail map's alpha blends between the primary and secondary detail maps.
	/// </summary>
	Normal = 0,

	/// <summary>
	/// The base map's alpha blends between the primary and secondary detail maps.
	/// </summary>
	Blended = 1,

	/// <summary>
	/// Like <see cref="Blended"/>, but the specular mask comes from the base map.
	/// </summary>
	BlendedBaseSpecular = 2,
}

public enum DetailMapFunction : ushort
{
	/// <summary>
	/// <c>color * detail * 2</c>
	/// </summary>
	DoubleBiasedMultiply = 0,

	/// <summary>
	/// <c>color * detail</c>
	/// </summary>
	Multiply = 1,

	/// <summary>
	/// <c>color + 2 * detail - 1</c>
	/// </summary>
	DoubleBiasedAdd = 2,
}

[Flags]
public enum EnvironmentShaderFlags : ushort
{
	/// <summary>
	/// Holes are cut where the bump map's alpha is low (grates, foliage).
	/// </summary>
	AlphaTested = 0x1,

	/// <summary>
	/// The reflection is masked by the bump map's alpha instead of the diffuse maps' alpha.
	/// </summary>
	BumpMapIsSpecularMask = 0x2,
	TrueAtmosphericFog = 0x4,
}

[Flags]
public enum EnvironmentSpecularFlags : ushort
{
	Overbright = 0x1,
	ExtraShiny = 0x2,
	LightmapIsSpecular = 0x4,
}

public enum EnvironmentReflectionType : ushort
{
	/// <summary>
	/// The bump map bends the reflection and the fresnel falloff.
	/// </summary>
	BumpedCubeMap = 0,

	/// <summary>
	/// The reflection follows the surface normal; the bump map only affects the fresnel falloff.
	/// </summary>
	FlatCubeMap = 1,

	/// <summary>
	/// Unfinished in the original game, which draws no reflection for it.
	/// </summary>
	BumpedRadiosity = 2,
}

/// <summary>
/// One of the three self-illumination effects: a color while the shader is on, one while it's
/// off, and an animation (a periodic function of time) that modulates the on color.
/// </summary>
public readonly record struct SelfIlluminationEffect( Vector3 OnColor, Vector3 OffColor, AnimationFunction Function, float Period, float Phase );

/// <summary>
/// Reader for <c>senv</c> (shader_environment) tags, which most level geometry uses.
/// </summary>
/// <remarks>
/// Offsets: flags 0x28, type 0x2A, base map 0x88, detail map function 0xB0, primary detail map
/// scale/reference 0xB4/0xB8, secondary 0xC8/0xCC, micro detail function 0xF4, scale/reference
/// 0xF8/0xFC, material color 0x10C, bump map scale/reference 0x124/0x128. A scale of 0 means 1.
///
/// Texture scrolling: U animation function/period/scale 0x150/0x154/0x158, V 0x15C/0x160/0x164.
///
/// Self-illumination: flags 0x180, then three effects (primary 0x19C, secondary 0x1D8, plasma
/// 0x214), each an on and an off color (RGB floats), function (+0x18), period (+0x1C, 0 means 1)
/// and phase (+0x20). Map scale/reference 0x250/0x254.
///
/// Specular: flags 0x27C, brightness 0x290 (highlights from dynamic lights), perpendicular and
/// parallel color 0x2A8/0x2B4. Reflection: flags 0x2D0, type 0x2D2, lightmap brightness scale
/// 0x2D4, perpendicular/parallel brightness 0x2F4/0x2F8, cube map 0x324.
/// </remarks>
public sealed class ShaderEnvironmentTag
{
	public EnvironmentShaderFlags Flags { get; }
	public EnvironmentShaderType Type { get; }

	public TagEntry BaseMap { get; }

	public DetailMapFunction DetailMapFunction { get; }
	public float PrimaryDetailMapScale { get; }
	public TagEntry PrimaryDetailMap { get; }
	public float SecondaryDetailMapScale { get; }
	public TagEntry SecondaryDetailMap { get; }

	public DetailMapFunction MicroDetailMapFunction { get; }
	public float MicroDetailMapScale { get; }
	public TagEntry MicroDetailMap { get; }

	/// <summary>
	/// Multiplies the lightmap (not self-illumination or reflections). All zero in tags that
	/// never set it, which the game treats as white.
	/// </summary>
	public Vector3 MaterialColor { get; }

	public float BumpMapScale { get; }
	public TagEntry BumpMap { get; }

	/// <summary>
	/// Scrolls every 2D map. The scale is in base map repeats.
	/// </summary>
	public MapAnimation UAnimation { get; }
	public MapAnimation VAnimation { get; }

	public bool SelfIlluminationUnfiltered { get; }
	public SelfIlluminationEffect PrimarySelfIllumination { get; }
	public SelfIlluminationEffect SecondarySelfIllumination { get; }
	public SelfIlluminationEffect PlasmaSelfIllumination { get; }
	public float SelfIlluminationMapScale { get; }

	/// <summary>
	/// Red masks the primary effect, green the secondary, blue the plasma; alpha is the plasma
	/// animation reference.
	/// </summary>
	public TagEntry SelfIlluminationMap { get; }

	public EnvironmentSpecularFlags SpecularFlags { get; }
	public float SpecularBrightness { get; }
	public Vector3 PerpendicularColor { get; }
	public Vector3 ParallelColor { get; }

	public bool DynamicMirror { get; }
	public EnvironmentReflectionType ReflectionType { get; }
	public float LightmapBrightnessScale { get; }
	public float PerpendicularBrightness { get; }
	public float ParallelBrightness { get; }
	public TagEntry ReflectionCubeMap { get; }

	/// <summary>
	/// Whether the shader draws a cube map reflection: it needs a cube map and some brightness.
	/// </summary>
	public bool HasReflection => ReflectionCubeMap is not null
		&& ReflectionType != EnvironmentReflectionType.BumpedRadiosity
		&& (PerpendicularBrightness > 0 || ParallelBrightness > 0);

	/// <summary>
	/// Whether any self-illumination effect can produce light.
	/// </summary>
	public bool HasSelfIllumination => SelfIlluminationMap is not null
		&& (Lights( PrimarySelfIllumination ) || Lights( SecondarySelfIllumination ) || Lights( PlasmaSelfIllumination ));

	public ShaderEnvironmentTag( CacheFile cache, TagEntry tag )
	{
		if ( tag.Class != TagClasses.ShaderEnvironment )
			throw new ArgumentException( $"{tag} is not a shader_environment tag", nameof( tag ) );

		var data = cache.TagData;
		var a = tag.Address;

		Flags = (EnvironmentShaderFlags)data.U16( a + 0x28 );
		Type = (EnvironmentShaderType)data.U16( a + 0x2A );
		BaseMap = cache.GetTagReference( data, a + 0x88 );

		DetailMapFunction = (DetailMapFunction)data.U16( a + 0xB0 );
		PrimaryDetailMapScale = Scale( data.F32( a + 0xB4 ) );
		PrimaryDetailMap = cache.GetTagReference( data, a + 0xB8 );
		SecondaryDetailMapScale = Scale( data.F32( a + 0xC8 ) );
		SecondaryDetailMap = cache.GetTagReference( data, a + 0xCC );

		MicroDetailMapFunction = (DetailMapFunction)data.U16( a + 0xF4 );
		MicroDetailMapScale = Scale( data.F32( a + 0xF8 ) );
		MicroDetailMap = cache.GetTagReference( data, a + 0xFC );

		var materialColor = data.Vec3( a + 0x10C );
		MaterialColor = materialColor == Vector3.Zero ? Vector3.One : materialColor;

		BumpMapScale = Scale( data.F32( a + 0x124 ) );
		BumpMap = cache.GetTagReference( data, a + 0x128 );

		// Unlike transparent shader maps these have no phase, and the scale isn't defaulted: a
		// scale of 0 doesn't move.
		UAnimation = new MapAnimation( (AnimationFunction)data.U16( a + 0x150 ), Scale( data.F32( a + 0x154 ) ), 0, data.F32( a + 0x158 ) );
		VAnimation = new MapAnimation( (AnimationFunction)data.U16( a + 0x15C ), Scale( data.F32( a + 0x160 ) ), 0, data.F32( a + 0x164 ) );

		SelfIlluminationUnfiltered = (data.U16( a + 0x180 ) & 1) != 0;
		PrimarySelfIllumination = ReadSelfIllumination( data, a + 0x19C );
		SecondarySelfIllumination = ReadSelfIllumination( data, a + 0x1D8 );
		PlasmaSelfIllumination = ReadSelfIllumination( data, a + 0x214 );
		SelfIlluminationMapScale = Scale( data.F32( a + 0x250 ) );
		SelfIlluminationMap = cache.GetTagReference( data, a + 0x254 );

		SpecularFlags = (EnvironmentSpecularFlags)data.U16( a + 0x27C );
		SpecularBrightness = data.F32( a + 0x290 );
		PerpendicularColor = data.Vec3( a + 0x2A8 );
		ParallelColor = data.Vec3( a + 0x2B4 );

		DynamicMirror = (data.U16( a + 0x2D0 ) & 1) != 0;
		ReflectionType = (EnvironmentReflectionType)data.U16( a + 0x2D2 );
		LightmapBrightnessScale = data.F32( a + 0x2D4 );
		PerpendicularBrightness = data.F32( a + 0x2F4 );
		ParallelBrightness = data.F32( a + 0x2F8 );
		ReflectionCubeMap = cache.GetTagReference( data, a + 0x324 );
	}

	static SelfIlluminationEffect ReadSelfIllumination( IO.MemoryRegion data, uint address ) => new(
		data.Vec3( address ),
		data.Vec3( address + 0xC ),
		(AnimationFunction)data.U16( address + 0x18 ),
		Scale( data.F32( address + 0x1C ) ),
		data.F32( address + 0x20 ) );

	static bool Lights( SelfIlluminationEffect effect ) =>
		effect.OnColor != Vector3.Zero || effect.OffColor != Vector3.Zero;

	static float Scale( float value ) => value == 0 ? 1 : value;
}
