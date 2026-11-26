using System;
using System.Collections.Generic;

namespace Blam.Halo1.Tags;

// Inside the namespace so these win over s&box's global Vector2/Vector3 when this compiles into the mount.
using Vector2 = System.Numerics.Vector2;
using Vector4 = System.Numerics.Vector4;

[Flags]
public enum LensFlareReflectionFlags : ushort
{
	AlignRotationWithScreenCenter = 0x1,
	RadiusNotScaledByDistance = 0x2,
	RadiusScaledByOcclusion = 0x4,
	OccludedBySolidObjects = 0x8,
}

/// <summary>
/// What scales a reflection's brightness, by angle (falling off between the flare's falloff and
/// cutoff angles).
/// </summary>
public enum LensFlareScaleFunction : ushort
{
	None = 0,
	/// <summary>How directly the camera looks against the light's direction.</summary>
	LightDirection = 1,
	/// <summary>How directly the light faces the camera.</summary>
	LightToCamera = 2,
	/// <summary>How close the light is to the center of the view.</summary>
	CameraDirection = 3,
}

/// <summary>
/// One sprite of a lens flare. The first is usually the corona around the light itself, the
/// rest are reflections strung along the line from the light through the screen center.
/// </summary>
/// <param name="Position">Where along that line: 0 is on the light, 0.5 the screen center, 1 opposite the light.</param>
/// <param name="Radius">Radius range, interpolated by the light's scale. World units at the light, unless <see cref="LensFlareReflectionFlags.RadiusNotScaledByDistance"/>.</param>
/// <param name="Brightness">Brightness range, interpolated by the light's scale.</param>
/// <param name="Tint">ARGB tint. Alpha blends between modulating (0) and tinting (1); all zero means the light's color.</param>
/// <param name="BrightnessScale">What scales the brightness. The first reflection's final brightness also scales every later one.</param>
public sealed record LensFlareReflection(
	LensFlareReflectionFlags Flags,
	int BitmapIndex,
	float Position,
	float RotationOffset,
	Vector2 Radius,
	Vector2 Brightness,
	Vector4 Tint,
	LensFlareScaleFunction BrightnessScale );

/// <summary>
/// Reader for <c>lens</c> (lens flare) tags: the glows and streaks drawn on top of bright lights
/// and the sun.
/// </summary>
/// <remarks>
/// Offsets: falloff and cutoff angles 0x0/0x4 (radians, how far off-axis a directional flare
/// fades), occlusion radius 0x10 (world units), occlusion offset direction 0x14 (0 toward the
/// viewer, 1 marker forward, 2 none), near/far fade distances 0x18/0x1C, bitmap 0x20
/// (one bitmap per reflection), flags 0x30 (0x1: sun), rotation function 0x80, corona
/// horizontal/vertical scale 0xA0/0xA4, reflections block 0xC4 (128 bytes each: flags 0x0,
/// bitmap index 0x4, position 0x1C, rotation offset 0x20, radius 0x28, radius scale function 0x30,
/// brightness 0x34, brightness scale function 0x3C, tint 0x40 ARGB).
/// </remarks>
public sealed class LensFlareTag
{
	public float FalloffAngle { get; }
	public float CutoffAngle { get; }
	public float OcclusionRadius { get; }

	/// <summary>
	/// Where the occlusion test point goes from the light: 0 the occlusion radius toward the viewer
	/// (along the camera's forward), 1 the radius x sqrt 2 along the marker's forward, 2 on the light.
	/// </summary>
	public int OcclusionOffsetDirection { get; }
	public float NearFadeDistance { get; }
	public float FarFadeDistance { get; }
	public TagEntry Bitmap { get; }
	public bool IsSun { get; }
	public int RotationFunction { get; }
	public Vector2 CoronaScale { get; }
	public IReadOnlyList<LensFlareReflection> Reflections { get; }

	public LensFlareTag( CacheFile cache, TagEntry tag )
	{
		var data = cache.TagData;
		var a = tag.Address;

		FalloffAngle = data.F32( a );
		CutoffAngle = data.F32( a + 0x4 );
		OcclusionRadius = data.F32( a + 0x10 );
		OcclusionOffsetDirection = data.I16( a + 0x14 );
		NearFadeDistance = data.F32( a + 0x18 );
		FarFadeDistance = data.F32( a + 0x1C );
		Bitmap = cache.GetTagReference( data, a + 0x20 );
		IsSun = (data.U16( a + 0x30 ) & 0x1) != 0;
		RotationFunction = data.U16( a + 0x80 );

		// Zero defaults to 1.
		var horizontal = data.F32( a + 0xA0 );
		var vertical = data.F32( a + 0xA4 );
		CoronaScale = new Vector2( horizontal == 0 ? 1 : horizontal, vertical == 0 ? 1 : vertical );

		var reflections = new List<LensFlareReflection>();
		foreach ( var r in data.Block( a + 0xC4, 0x80 ) )
		{
			reflections.Add( new LensFlareReflection(
				Flags: (LensFlareReflectionFlags)data.U16( r ),
				BitmapIndex: data.I16( r + 0x4 ),
				Position: data.F32( r + 0x1C ),
				RotationOffset: data.F32( r + 0x20 ),
				Radius: data.Vec2( r + 0x28 ),
				Brightness: data.Vec2( r + 0x34 ),
				Tint: new Vector4( data.F32( r + 0x40 ), data.F32( r + 0x44 ), data.F32( r + 0x48 ), data.F32( r + 0x4C ) ),
				BrightnessScale: (LensFlareScaleFunction)data.U16( r + 0x3C ) ) );
		}
		Reflections = reflections;
	}
}
