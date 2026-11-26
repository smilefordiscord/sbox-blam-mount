using System;

namespace Blam.Halo1.Tags;

// Inside the namespace so these win over s&box's global Vector2/Vector3 when this compiles into the mount.
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;

[Flags]
public enum FogFlags : uint
{
	/// <summary>The volume below the plane counts as underwater (vehicles, sounds).</summary>
	IsWater = 0x1,

	/// <summary>
	/// Draw the sky's atmospheric fog over the planar fog. Only c10's swamp fog sets it, to stop
	/// geometry popping where the atmospheric fog is opaque.
	/// </summary>
	AtmosphereDominant = 0x2,
	FogScreenOnly = 0x4,
}

/// <summary>
/// Reader for <c>fog </c> tags: the color and thickness of the planar fog below a structure
/// BSP's fog planes (murky water, swamp mist). The sky tag has the separate atmospheric fog.
/// </summary>
/// <remarks>
/// Offsets: flags 0x0, maximum density 0x58, opaque distance 0x60, opaque depth 0x68 (both in
/// world units), distance to water plane 0x74, color 0x78. Screen layers (noise drawn over the
/// view while the camera is in the fog; MCC draws them): flags 0x84, layer count 0x86, distance
/// gradient 0x88, density gradient 0x90, start distance from the fog plane 0x98, color 0xA0 (8 bit
/// ARGB, 0 = the fog color), rotation/strafing/zoom multipliers 0xA4/0xA8/0xAC, map scale 0xB8,
/// map 0xBC, animation period 0xCC, wind velocity 0xD4, wind period 0xDC, wind acceleration and
/// perpendicular weights 0xE4/0xE8.
/// </remarks>
public sealed class FogTag
{
	public FogFlags Flags { get; }

	/// <summary>The most the fog ever covers what's behind it, 0..1.</summary>
	public float MaximumDensity { get; }

	/// <summary>How far the view has to travel through the fog for it to reach maximum density.</summary>
	public float OpaqueDistance { get; }

	/// <summary>How far below the fog plane the fog reaches maximum density.</summary>
	public float OpaqueDepth { get; }

	public float DistanceToWaterPlane { get; }
	public Vector3 Color { get; }
	public ushort ScreenFlags { get; }
	public int ScreenLayerCount { get; }
	public TagEntry ScreenLayerMap { get; }
	public float ScreenNearDistance { get; }
	public float ScreenFarDistance { get; }
	public float ScreenNearDensity { get; }
	public float ScreenFarDensity { get; }
	public float ScreenStartDistanceFromPlane { get; }

	/// <summary>Screen layer color, or null to use <see cref="Color"/>.</summary>
	public Vector3? ScreenColor { get; }
	public float ScreenRotationMultiplier { get; }
	public float ScreenStrafingMultiplier { get; }
	public float ScreenZoomMultiplier { get; }
	public float ScreenMapScale { get; }
	public float ScreenAnimationPeriod { get; }
	public Vector2 ScreenWindVelocity { get; }
	public Vector2 ScreenWindPeriod { get; }

	public FogTag( CacheFile cache, TagEntry tag )
	{
		var data = cache.TagData;
		var a = tag.Address;

		Flags = (FogFlags)data.U32( a );
		MaximumDensity = data.F32( a + 0x58 );
		OpaqueDistance = data.F32( a + 0x60 );
		OpaqueDepth = data.F32( a + 0x68 );
		DistanceToWaterPlane = data.F32( a + 0x74 );
		Color = data.Vec3( a + 0x78 );
		ScreenFlags = data.U16( a + 0x84 );
		ScreenLayerCount = data.I16( a + 0x86 );
		ScreenLayerMap = cache.GetTagReference( data, a + 0xBC );
		ScreenNearDistance = data.F32( a + 0x88 );
		ScreenFarDistance = data.F32( a + 0x8C );
		ScreenNearDensity = data.F32( a + 0x90 );
		ScreenFarDensity = data.F32( a + 0x94 );
		ScreenStartDistanceFromPlane = data.F32( a + 0x98 );
		var color = data.U32( a + 0xA0 );
		ScreenColor = (color & 0xFFFFFF) == 0 ? null : new Vector3( ((color >> 16) & 0xFF) / 255f, ((color >> 8) & 0xFF) / 255f, (color & 0xFF) / 255f );
		ScreenRotationMultiplier = data.F32( a + 0xA4 );
		ScreenStrafingMultiplier = data.F32( a + 0xA8 );
		ScreenZoomMultiplier = data.F32( a + 0xAC );
		var mapScale = data.F32( a + 0xB8 );
		ScreenMapScale = mapScale == 0 ? 1 : mapScale;
		ScreenAnimationPeriod = data.F32( a + 0xCC );
		ScreenWindVelocity = data.Vec2( a + 0xD4 );
		ScreenWindPeriod = data.Vec2( a + 0xDC );
	}
}
