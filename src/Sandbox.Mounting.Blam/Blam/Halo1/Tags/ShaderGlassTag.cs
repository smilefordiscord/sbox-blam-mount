using System;

namespace Blam.Halo1.Tags;

// Inside the namespace so these win over s&box's global Vector2/Vector3 when this compiles into the mount.
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;

[Flags]
public enum GlassShaderFlags : ushort
{
	AlphaTested = 0x1,
	Decal = 0x2,
	TwoSided = 0x4,
	BumpMapIsSpecularMask = 0x8,
}

public enum GlassReflectionType : ushort
{
	BumpedCubeMap = 0,
	FlatCubeMap = 1,
	DynamicMirror = 2,
}

/// <summary>
/// Reader for <c>sgla</c> (shader_transparent_glass) tags.
/// </summary>
/// <remarks>
/// The background is multiplied by a tint color and tint map, then a cube map reflection is
/// added, and a diffuse map can be blended on top.
///
/// Offsets: flags 0x28, background tint color 0x54, tint map scale/reference 0x60/0x64,
/// reflection type 0x8A, perpendicular brightness/tint 0x8C/0x90, parallel brightness/tint
/// 0x9C/0xA0, reflection map 0xAC, bump map scale/reference 0xBC/0xC0, diffuse map scale/reference
/// 0x154/0x158. A scale of 0 means 1.
/// </remarks>
public sealed class ShaderGlassTag
{
	public GlassShaderFlags Flags { get; }

	public Vector3 BackgroundTint { get; }
	public float TintMapScale { get; }
	public TagEntry TintMap { get; }

	public GlassReflectionType ReflectionType { get; }
	public float PerpendicularBrightness { get; }
	public Vector3 PerpendicularTint { get; }
	public float ParallelBrightness { get; }
	public Vector3 ParallelTint { get; }
	public TagEntry ReflectionMap { get; }

	public float BumpMapScale { get; }
	public TagEntry BumpMap { get; }

	public float DiffuseMapScale { get; }
	public TagEntry DiffuseMap { get; }

	public ShaderGlassTag( CacheFile cache, TagEntry tag )
	{
		if ( tag.Class != TagClasses.ShaderGlass )
			throw new ArgumentException( $"{tag} is not a shader_transparent_glass tag", nameof( tag ) );

		var data = cache.TagData;
		var a = tag.Address;

		Flags = (GlassShaderFlags)data.U16( a + 0x28 );

		BackgroundTint = data.Vec3( a + 0x54 );
		TintMapScale = DefaultOne( data.F32( a + 0x60 ) );
		TintMap = cache.GetTagReference( data, a + 0x64 );

		ReflectionType = (GlassReflectionType)data.U16( a + 0x8A );
		PerpendicularBrightness = data.F32( a + 0x8C );
		PerpendicularTint = data.Vec3( a + 0x90 );
		ParallelBrightness = data.F32( a + 0x9C );
		ParallelTint = data.Vec3( a + 0xA0 );
		ReflectionMap = cache.GetTagReference( data, a + 0xAC );

		BumpMapScale = DefaultOne( data.F32( a + 0xBC ) );
		BumpMap = cache.GetTagReference( data, a + 0xC0 );

		DiffuseMapScale = DefaultOne( data.F32( a + 0x154 ) );
		DiffuseMap = cache.GetTagReference( data, a + 0x158 );
	}

	static float DefaultOne( float value ) => value == 0 ? 1 : value;
}
