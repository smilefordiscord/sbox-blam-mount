using System.Collections.Generic;
using System.Linq;

namespace Blam.Halo2.Tags;

using Vector3 = System.Numerics.Vector3;

/// <summary>
/// A Halo 2 weather system (<c>weat</c>): particles (snow, rain...) kept in a box in front of the camera, full-screen
/// "background plates" of scrolling textures laid over everything past a distance, and the wind that moves both.
/// </summary>
/// <remarks>
/// Layout and engine behaviour from Halo 2. Lockout's snow: 1024 flakes
/// in a 5 world unit box, one plate of three snow layers that whitens everything farther than 10-30 world units, the
/// distant sky included.
/// </remarks>
public sealed class WeatherTag
{
	public static readonly TagClass Class = new( "weat" );

	public TagEntry Tag { get; }
	public IReadOnlyList<ParticleSystem> ParticleSystems { get; }
	public IReadOnlyList<Plate> Plates { get; }

	/// <summary>
	/// The primary wind's direction (unit) and the strength range its slow random walk spans (world units/s²
	/// on a particle of mass 1), and how fast that walk goes (fraction of the range per second).
	/// </summary>
	public Vector3 WindDirection { get; }
	public float WindRateOfChange { get; }
	public float WindMinimumStrength { get; }
	public float WindMaximumStrength { get; }

	/// <summary>
	/// Gusts: a direction wandering within a cone around <see cref="GustDirection"/>, strength 0 to the maximum.
	/// </summary>
	public Vector3 GustDirection { get; }
	public float GustDirectionalRateOfChange { get; }
	public float GustStrengthRateOfChange { get; }
	public float GustConeAngle { get; }
	public float GustMaximumStrength { get; }

	/// <summary>
	/// Each particle's own push, a random value per axis in -1..1 walking at this rate, times this scale.
	/// </summary>
	public float TurbulenceRateOfChange { get; }
	public Vector3 TurbulenceScale { get; }

	public float Gravity { get; }

	/// <summary>
	/// Flakes within 0.2 world units of the camera fade by this distance to the nearest portal into a cluster with the
	/// weather (only when the camera is outside those clusters).
	/// </summary>
	public float FadeRadius { get; }

	/// <param name="Sprite">The particle bitmap (image 0 is always used).</param>
	/// <param name="BoxSize">The view-space box particles are kept in: width (right), height (up), depth (ahead).</param>
	/// <param name="ExclusionRadius">Particles closer to the camera than this die (0: off).</param>
	/// <param name="MaximumVelocity">Speed cap (world units/s).</param>
	/// <param name="Type">0 generic, 1 snow, 2 rain (streaks), 3 rain splash, 4 bugs, 5 sand storm, 6 debris, 7 bubbles.</param>
	public sealed record ParticleSystem(
		TagEntry Sprite,
		Vector3 BoxSize,
		float ExclusionRadius,
		float MaximumVelocity,
		float MinimumMass,
		float MaximumMass,
		float MinimumSize,
		float MaximumSize,
		int Count,
		Vector3 InitialVelocity,
		int Type );

	/// <summary>
	/// A background plate: three texture layers on planes in front of the camera, falling at their own speeds,
	/// combined and laid over everything from <see cref="DepthBlendNear"/> (none) to <see cref="DepthBlendFar"/> (all).
	/// </summary>
	/// <param name="Textures">The three layers' bitmaps (their alpha is what covers).</param>
	/// <param name="Positions">Where each layer's plane is between <see cref="NearZ"/> and <see cref="FarZ"/> (0-1).</param>
	/// <param name="MoveSpeeds">Each layer's initial velocity (world units/s), whose length is also its speed cap.</param>
	/// <param name="TextureScales">Texture repeats per world unit on each layer's plane.</param>
	/// <param name="Opacities">How much each layer covers.</param>
	/// <param name="Flags">1 forward motion, 2 automatic positions (0.25, 0.5, 0.75), 4 automatic scale (by plane size).</param>
	/// <param name="Tints">The layers' colours (MCC uses the first for all three).</param>
	/// <param name="Masses">How hard the wind and gravity push each layer.</param>
	public sealed record Plate(
		IReadOnlyList<TagEntry> Textures,
		IReadOnlyList<float> Positions,
		IReadOnlyList<Vector3> MoveSpeeds,
		IReadOnlyList<float> TextureScales,
		float NearZ,
		float FarZ,
		float DepthBlendNear,
		float DepthBlendFar,
		IReadOnlyList<float> Opacities,
		int Flags,
		IReadOnlyList<Vector3> Tints,
		IReadOnlyList<float> Masses );

	const int ParticleSystemSize = 0x8C;
	const int PlateSize = 0x3A8;

	public WeatherTag( TagEntry tag )
	{
		Tag = tag;
		var cache = tag.Cache;
		var data = cache.TagData;
		var t = tag.Address;

		ParticleSystems = data.Elements( t, ParticleSystemSize ).Select( a => new ParticleSystem(
			Sprite: cache.GetTagReference( a ),
			BoxSize: new Vector3( data.F32( a + 0x08 ), data.F32( a + 0x0C ), data.F32( a + 0x10 ) ),
			ExclusionRadius: data.F32( a + 0x14 ),
			MaximumVelocity: data.F32( a + 0x18 ),
			MinimumMass: data.F32( a + 0x1C ),
			MaximumMass: data.F32( a + 0x20 ),
			MinimumSize: data.F32( a + 0x24 ),
			MaximumSize: data.F32( a + 0x28 ),
			Count: data.I32( a + 0x2C ),
			InitialVelocity: data.Vec3( a + 0x30 ),
			Type: data.I16( a + 0x6C ) ) ).ToList();

		Plates = data.Elements( t + 0x08, PlateSize ).Select( a => new Plate(
			Textures: [cache.GetTagReference( a ), cache.GetTagReference( a + 0x08 ), cache.GetTagReference( a + 0x10 )],
			Positions: Floats( data, a + 0x18, 3 ),
			MoveSpeeds: [data.Vec3( a + 0x24 ), data.Vec3( a + 0x30 ), data.Vec3( a + 0x3C )],
			TextureScales: Floats( data, a + 0x48, 3 ),
			NearZ: data.F32( a + 0x78 ),
			FarZ: data.F32( a + 0x7C ),
			DepthBlendNear: data.F32( a + 0x80 ),
			DepthBlendFar: data.F32( a + 0x84 ),
			Opacities: Floats( data, a + 0x88, 3 ),
			Flags: data.I32( a + 0x94 ),
			Tints: [data.Vec3( a + 0x98 ), data.Vec3( a + 0xA4 ), data.Vec3( a + 0xB0 )],
			Masses: Floats( data, a + 0xBC, 3 ) ) ).ToList();

		WindDirection = data.Vec3( t + 0x14 );
		WindRateOfChange = data.F32( t + 0x20 );
		WindMinimumStrength = data.F32( t + 0x24 );
		WindMaximumStrength = data.F32( t + 0x28 );
		GustDirection = data.Vec3( t + 0x3C );
		GustDirectionalRateOfChange = data.F32( t + 0x48 );
		GustStrengthRateOfChange = data.F32( t + 0x4C );
		GustConeAngle = data.F32( t + 0x50 );
		GustMaximumStrength = data.F32( t + 0x54 );
		TurbulenceRateOfChange = data.F32( t + 0x8C );
		TurbulenceScale = data.Vec3( t + 0x90 );
		Gravity = data.F32( t + 0x9C );
		FadeRadius = data.F32( t + 0xAC );
	}

	static float[] Floats( Blam.IO.MemoryRegion data, uint address, int count ) =>
		Enumerable.Range( 0, count ).Select( i => data.F32( address + (uint)(i * 4) ) ).ToArray();
}
