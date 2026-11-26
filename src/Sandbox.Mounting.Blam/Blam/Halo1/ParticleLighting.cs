using System;
using Blam.Halo1.Tags;

namespace Blam.Halo1;

// Inside the namespace so these win over s&box's global Vector2/Vector3 when this compiles into the mount.
using Vector3 = System.Numerics.Vector3;

/// <summary>
/// The light and ground color Halo gives a particle when it's born. Particles aren't lit as they
/// fly: each takes a color from the ground under its birth position once and keeps it.
/// </summary>
/// <remarks>
/// <para>A ray goes 10 world units straight down from where the particle is born (the same ray as
/// object lighting, <see cref="ObjectLightingSampler"/>). Where it reaches a shader_environment
/// surface with a lightmap and a base map, the light L is the lightmap's nearest texel plus 0.1 per
/// channel (at most 1), and the ground color D the base map's nearest texel at a small mip level
/// (<see cref="DiffuseLevel"/>). Anywhere else (no ground within reach, another kind of shader)
/// both are a mid grey.</para>
///
/// <para>A particle tag that isn't self-illuminated multiplies its color by L; one flagged "tint
/// from diffuse texture" multiplies it by D. Alpha is left alone. A particle system (pctl) that
/// isn't attached to an object samples L once where it's created (no D), and multiplies the color
/// of its alpha blended particle states by it; attached systems are drawn at full brightness.</para>
///
/// <para>All of this is in Halo's gamma space, like the particles' own tints.</para>
/// </remarks>
public static class ParticleLighting
{
	/// <summary>
	/// The light and ground color where the ray finds no lit ground.
	/// </summary>
	public static readonly Vector3 Grey = new( 0.5f );

	/// <summary>
	/// Added to each channel of the lightmap color, so particles in deep shadow aren't black.
	/// </summary>
	public const float LightmapBoost = 0.1f;

	/// <summary>
	/// The base map mip level a ground color is read from, for a bitmap with
	/// <paramref name="mipmapCount"/> levels below its full size: 70% of the way down, rounded down.
	/// </summary>
	public static int DiffuseLevel( int mipmapCount ) => mipmapCount > 0 ? (int)(0.7f * mipmapCount) : 0;

	/// <summary>
	/// The light and ground color for a particle born at <paramref name="point"/> (world units).
	/// </summary>
	public static (Vector3 Light, Vector3 Diffuse) At( ObjectLightingSampler sampler, Vector3 point )
	{
		if ( sampler is null || !sampler.TryParticleGround( point, out var lightmap, out var diffuse ) )
			return (Grey, Grey);

		return (Vector3.Min( lightmap + new Vector3( LightmapBoost ), Vector3.One ), diffuse);
	}

	/// <summary>
	/// The same from an object's lighting (as <see cref="ObjectLightVolume"/> keeps it), for levels
	/// whose ground isn't kept: its main light is the lightmap color, and its bounce light the
	/// ground color times the lightmap's luminance.
	/// </summary>
	public static (Vector3 Light, Vector3 Diffuse) FromObjectLight( ObjectLight light )
	{
		var c = light.Light0;
		var luminance = c.X * 0.299f + c.Y * 0.587f + c.Z * 0.114f;
		var diffuse = luminance > 0.01f ? Vector3.Clamp( light.Light1 / luminance, Vector3.Zero, Vector3.One ) : Grey;
		return (Vector3.Min( c + new Vector3( LightmapBoost ), Vector3.One ), diffuse);
	}

	/// <summary>
	/// What a particle's color is multiplied by (gamma space): L unless it's self-illuminated,
	/// times D if it's tinted from the ground.
	/// </summary>
	public static Vector3 Multiplier( (Vector3 Light, Vector3 Diffuse) ground, bool lit, bool diffuse )
	{
		var m = Vector3.One;
		if ( lit )
			m *= ground.Light;
		if ( diffuse )
			m *= ground.Diffuse;
		return m;
	}

	/// <summary>
	/// Whether Halo lights a particle tag's particles at birth.
	/// </summary>
	public static bool Lit( ParticleTag part ) => !part.SelfIlluminated;

	/// <summary>
	/// Whether Halo lights a particle state of an unattached particle system: alpha blended ones.
	/// </summary>
	public static bool Lit( ParticleState state ) => state.BlendFunction == FramebufferBlendFunction.AlphaBlend;
}
