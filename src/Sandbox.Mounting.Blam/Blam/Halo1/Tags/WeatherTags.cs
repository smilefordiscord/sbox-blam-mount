using System;
using System.Collections.Generic;

namespace Blam.Halo1.Tags;

// Inside the namespace so these win over s&box's global Vector2/Vector3 when this compiles into the mount.
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;
using Vector4 = System.Numerics.Vector4;

public enum ParticleRenderMode : ushort
{
	ScreenFacing = 0,
	ParallelToDirection = 1,
	PerpendicularToDirection = 2,
}

/// <summary>
/// One kind of particle in a weather system, e.g. the rain drops or the bugs of c10's swamp.
/// </summary>
/// <param name="FadeDistance">Fade in start/end and fade out start/end distances from the camera, in world units.</param>
/// <param name="ParticleCount">How many particles fill the space around the camera.</param>
/// <param name="Radius">Particle radius range in world units.</param>
/// <param name="AccelerationMagnitude">Random acceleration range (world units/s²) that makes particles wander.</param>
/// <param name="ColorLower">ARGB.</param>
/// <param name="ColorUpper">ARGB.</param>
/// <param name="AnimationRate">Sprite frames per second, range.</param>
/// <param name="Physics">Point physics: density and friction decide how fast particles fall.</param>
/// <param name="FadeMode">The shader's framebuffer fade mode (see <see cref="ParticleTag.FadeMode"/>): c10's rain is 1, fading as it's seen end on.</param>
public sealed record WeatherParticleType(
	string Name,
	uint Flags,
	Vector4 FadeDistance,
	Vector2 ParticleCount,
	TagEntry Physics,
	Vector2 AccelerationMagnitude,
	Vector2 Radius,
	Vector2 AnimationRate,
	Vector4 ColorLower,
	Vector4 ColorUpper,
	TagEntry SpriteBitmap,
	ParticleRenderMode RenderMode,
	FramebufferBlendFunction BlendFunction,
	int FadeMode );

/// <summary>
/// Reader for <c>rain</c> (weather particle system) tags: rain, snow and other particles that
/// fill the air around the camera in a level's outdoor areas.
/// </summary>
/// <remarks>
/// Particle types are a block at 0x24 (604 bytes each): name, flags 0x20, fade distances
/// 0x24-0x30, fade heights 0x34-0x40, particle count 0xA4, physics 0xAC, acceleration magnitude
/// 0xCC, particle radius 0xFC, animation rate 0x104, color bounds 0x134/0x144 (ARGB), sprite bitmap
/// 0x194, render mode 0x1A4, framebuffer blend function 0x1D2, framebuffer fade mode 0x1D4.
/// </remarks>
public sealed class WeatherParticleSystemTag
{
	public IReadOnlyList<WeatherParticleType> Types { get; }

	public WeatherParticleSystemTag( CacheFile cache, TagEntry tag )
	{
		var data = cache.TagData;
		var types = new List<WeatherParticleType>();
		foreach ( var t in data.Block( tag.Address + 0x24, 0x25C ) )
		{
			types.Add( new WeatherParticleType(
				Name: data.CString( t, 32 ),
				Flags: data.U32( t + 0x20 ),
				FadeDistance: new Vector4( data.F32( t + 0x24 ), data.F32( t + 0x28 ), data.F32( t + 0x2C ), data.F32( t + 0x30 ) ),
				ParticleCount: data.Vec2( t + 0xA4 ),
				Physics: cache.GetTagReference( data, t + 0xAC ),
				AccelerationMagnitude: data.Vec2( t + 0xCC ),
				Radius: data.Vec2( t + 0xFC ),
				AnimationRate: data.Vec2( t + 0x104 ),
				ColorLower: ReadArgb( data, t + 0x134 ),
				ColorUpper: ReadArgb( data, t + 0x144 ),
				SpriteBitmap: cache.GetTagReference( data, t + 0x194 ),
				RenderMode: (ParticleRenderMode)data.U16( t + 0x1A4 ),
				BlendFunction: (FramebufferBlendFunction)data.U16( t + 0x1D2 ),
				FadeMode: data.U16( t + 0x1D4 ) ) );
		}
		Types = types;
	}

	/// <summary>
	/// Reads an ARGB float color as (R, G, B, A).
	/// </summary>
	internal static Vector4 ReadArgb( IO.MemoryRegion data, uint address ) =>
		new( data.F32( address + 4 ), data.F32( address + 8 ), data.F32( address + 12 ), data.F32( address ) );
}

/// <summary>
/// Reader for <c>pphy</c> (point physics) tags: how particles fall and slow down.
/// </summary>
/// <remarks>
/// Flags 0x0 (0x20: no gravity), mass per radius cubed 0x4 and air gravity scale 0xC (both worked
/// out when the map was built from the density; the scale is -1 for points that fall like a
/// stone, positive for ones that rise), density 0x20 (g/ml; air is 0.0011), air friction 0x24, surface friction 0x2C, elasticity 0x30.
/// Flags 0x2: collides with structures.
/// </remarks>
public sealed class PointPhysicsTag
{
	public uint Flags { get; }
	public float MassOverRadiusCubed { get; }

	// Gravity in water, times the world's (negative: rises).
	public float WaterGravityScale { get; }
	public float AirGravityScale { get; }
	public float Density { get; }
	public float AirFriction { get; }
	public float WaterFriction { get; }

	public float SurfaceFriction { get; }
	public float Elasticity { get; }

	public bool NoGravity => (Flags & 0x20) != 0;
	public bool CollidesWithStructures => (Flags & 0x2) != 0;
	public bool CollidesWithWater => (Flags & 0x4) != 0;

	// Flags 0x8 / 0x10: the wind without its local variation / damped by the wind's damping.
	public bool SimpleWind => (Flags & 0x8) != 0;
	public bool DampedWind => (Flags & 0x10) != 0;

	/// <summary>
	/// Density of air in the same units (g/ml).
	/// </summary>
	public const float AirDensity = 0.0011f;

	/// <summary>
	/// How fast air slows a point of this radius (world units) down, per second: each update pulls
	/// the velocity toward the wind by friction x radius^2 / mass, where the mass is (density +
	/// the air's density) x 118613 x radius^3 (the tag's runtime mass at 0x4 is density x 118613).
	/// So a big smoke puff (friction 200, density 0.0004, radius 1) slows by about 1.1 per second, a
	/// stone (friction 900, density 2.4, radius 0.02) by 0.16.
	/// </summary>
	public float DragPerSecond( float radius )
	{
		var mass = (Density + AirDensity) * 118613.34f * MathF.Max( radius, 0.001f );
		return mass > 0 ? AirFriction / mass : 0;
	}

	public PointPhysicsTag( CacheFile cache, TagEntry tag )
	{
		var data = cache.TagData;
		Flags = data.U32( tag.Address );
		MassOverRadiusCubed = data.F32( tag.Address + 0x4 );
		WaterGravityScale = data.F32( tag.Address + 0x8 );
		AirGravityScale = data.F32( tag.Address + 0xC );
		Density = data.F32( tag.Address + 0x20 );
		AirFriction = data.F32( tag.Address + 0x24 );
		WaterFriction = data.F32( tag.Address + 0x28 );
		SurfaceFriction = data.F32( tag.Address + 0x2C );
		Elasticity = data.F32( tag.Address + 0x30 );
	}
}
