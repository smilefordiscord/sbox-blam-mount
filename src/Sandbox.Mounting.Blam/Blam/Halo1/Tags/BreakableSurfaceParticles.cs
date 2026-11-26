using System.Collections.Generic;
using System.Linq;

namespace Blam.Halo1.Tags;

// Inside the namespace so these win over s&box's global Vector2/Vector3 when this compiles into the mount.
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;

/// <summary>
/// The particles a breakable surface breaks into, by its material: the globals' materials (0x194,
/// 0x374 bytes each, in <see cref="MaterialTypes"/> order), each with its breakable surface's
/// particle effects at 0x31C (0x80 bytes: particle 0x0, flags 0x10, density 0x14, velocity scale
/// 0x18, cone angle 0x20, angular velocity 0x24, radius 0x34, tint lower 0x44 and upper 0x54).
/// </summary>
/// <remarks>
/// Halo makes them one at a time over the broken surface, a grid of them <c>density</c> world
/// units apart, each with its own velocity from the damage that broke it, a random radius, spin
/// and tint between the bounds, and a random rotation. As effect particle blocks: one particle
/// each, at the location, still (the game gives it its velocity), with the random initial angle.
/// </remarks>
public static class BreakableSurfaceParticles
{
	const int MaterialSize = 0x374;
	const int ParticleEffectSize = 0x80;
	const uint RandomInitialAngle = 0x2;

	/// <summary>Each material's breakable surface particle effects, in order, those without a particle left out.</summary>
	public static IReadOnlyList<(int Material, int Index, EffectParticle Particle)> Read( CacheFile cache )
	{
		var result = new List<(int, int, EffectParticle)>();
		var globals = cache.Tags.FirstOrDefault( t => t.Class.ToString() == "matg" );
		if ( globals is null )
			return result;

		var data = cache.TagData;
		var material = 0;
		foreach ( var entry in data.Block( globals.Address + 0x194, MaterialSize ) )
		{
			var index = 0;
			foreach ( var p in data.Block( entry + 0x31C, ParticleEffectSize ) )
			{
				var particle = cache.GetTagReference( data, p );
				if ( particle is not null && particle.Class == EffectTagClasses.Particle )
				{
					result.Add( (material, index, new EffectParticle(
						Particle: particle,
						CreateIn: 0,
						ViolenceMode: 0,
						CameraMode: 0,
						Location: 0,
						Direction: Vector3.UnitX,
						Offset: Vector3.Zero,
						Flags: RandomInitialAngle,
						Distribution: 0,
						Count: Vector2.One,
						DistributionRadius: Vector2.Zero,
						Velocity: Vector2.Zero,
						ConeAngle: 0,
						AngularVelocity: data.Vec2( p + 0x24 ),
						Radius: data.Vec2( p + 0x34 ),
						TintLower: WeatherParticleSystemTag.ReadArgb( data, p + 0x44 ),
						TintUpper: WeatherParticleSystemTag.ReadArgb( data, p + 0x54 ),
						ScaleA: 0,
						ScaleB: 0 )) );
				}
				index++;
			}
			material++;
		}
		return result;
	}
}
