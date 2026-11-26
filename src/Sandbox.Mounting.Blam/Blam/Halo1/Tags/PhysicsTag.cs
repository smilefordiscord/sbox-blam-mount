using System;
using System.Collections.Generic;
using System.Linq;

namespace Blam.Halo1.Tags;

// Inside the namespace so these win over s&box's global types when this compiles into the mount.
using Vector3 = System.Numerics.Vector3;

/// <summary>
/// A powered mass point: the hover or wheel "engine" one or more mass points share.
/// </summary>
/// <param name="Flags">Ground friction 1, water friction 2, air friction 4, water lift 8, air lift
/// 0x10, thrust 0x20, antigravity 0x40.</param>
public sealed record PoweredMassPoint( string Name, uint Flags, float AntigravStrength, float AntigravOffset, float AntigravHeight, float AntigravDampFraction, float AntigravNormalK1, float AntigravNormalK0 );

/// <summary>
/// A mass point: a sphere of the object's mass, on a model node, that touches the ground and
/// water. Vehicles are simulated as their mass points.
/// </summary>
/// <param name="PoweredMassPoint">Index into <see cref="PhysicsTag.PoweredMassPoints"/>, -1 for none.</param>
/// <param name="Node">The model node the point moves with.</param>
/// <param name="Flags">Metallic 1.</param>
/// <param name="Position">In the object's space at the default pose, world units (the warthog's tire points sit at the
/// tires, though they follow the tire nodes).</param>
/// <param name="FrictionType">Point 0, forward 1, left 2, up 3.</param>
public sealed record MassPoint( string Name, int PoweredMassPoint, int Node, uint Flags, float RelativeMass, float Mass, float RelativeDensity, float Density,
	Vector3 Position, Vector3 Forward, Vector3 Up, int FrictionType, float FrictionParallelScale, float FrictionPerpendicularScale, float Radius );

/// <summary>
/// Reader for <c>phys</c> (physics) tags: the mass points vehicles (and a few other objects)
/// are simulated with.
/// </summary>
/// <remarks>
/// Offsets: radius 0x0, moment scale 0x4, mass 0x8, center of mass 0xC, density 0x18, gravity
/// scale 0x1C, ground friction 0x20, ground depth 0x24, ground damp fraction 0x28, ground normal
/// k1 0x2C, k0 0x30, water friction 0x38, water depth 0x3C, water density 0x40, air friction 0x48,
/// xx/yy/zz moments 0x50, inertial matrix block 0x5C (two 3x3 matrices: the inertia tensor and
/// its inverse), powered mass points 0x68 (128 bytes: name, flags 0x20, antigrav strength 0x24,
/// offset 0x28, height 0x2C, damp fraction 0x30, normal k1 0x34, k0 0x38), mass points 0x74
/// (128 bytes: name, powered mass point 0x20, node 0x22, flags 0x24, relative mass 0x28, mass
/// 0x2C, relative density 0x30, density 0x34, position 0x38, forward 0x44, up 0x50, friction
/// type 0x5C, parallel/perpendicular friction scale 0x60/0x64, radius 0x68).
/// </remarks>
public sealed class PhysicsTag
{
	public TagEntry Tag { get; }
	public float Radius { get; }
	public float MomentScale { get; }
	public float Mass { get; }
	public Vector3 CenterOfMass { get; }
	public float Density { get; }
	public float GravityScale { get; }
	public float GroundFriction { get; }
	public float GroundDepth { get; }
	public float GroundDampFraction { get; }
	public float GroundNormalK1 { get; }
	public float GroundNormalK0 { get; }
	public float WaterFriction { get; }
	public float WaterDepth { get; }
	public float WaterDensity { get; }
	public float AirFriction { get; }
	public Vector3 Moments { get; }

	/// <summary>The inertia tensor and its inverse, row by row (9 floats each), if the tag has them.</summary>
	public IReadOnlyList<float[]> InertialMatrices { get; }
	public IReadOnlyList<PoweredMassPoint> PoweredMassPoints { get; }
	public IReadOnlyList<MassPoint> MassPoints { get; }

	public static readonly TagClass Class = new( "phys" );

	public PhysicsTag( CacheFile cache, TagEntry tag )
	{
		if ( tag.Class != Class )
			throw new ArgumentException( $"{tag} is not a physics tag", nameof( tag ) );

		Tag = tag;
		var data = cache.TagData;
		var a = tag.Address;

		Radius = data.F32( a );
		MomentScale = data.F32( a + 0x4 );
		Mass = data.F32( a + 0x8 );
		CenterOfMass = data.Vec3( a + 0xC );
		Density = data.F32( a + 0x18 );
		GravityScale = data.F32( a + 0x1C );
		GroundFriction = data.F32( a + 0x20 );
		GroundDepth = data.F32( a + 0x24 );
		GroundDampFraction = data.F32( a + 0x28 );
		GroundNormalK1 = data.F32( a + 0x2C );
		GroundNormalK0 = data.F32( a + 0x30 );
		WaterFriction = data.F32( a + 0x38 );
		WaterDepth = data.F32( a + 0x3C );
		WaterDensity = data.F32( a + 0x40 );
		AirFriction = data.F32( a + 0x48 );
		Moments = data.Vec3( a + 0x50 );

		InertialMatrices = data.Block( a + 0x5C, 0x24 ).Select( m => Enumerable.Range( 0, 9 ).Select( i => data.F32( m + (uint)(i * 4) ) ).ToArray() ).ToList();

		PoweredMassPoints = data.Block( a + 0x68, 0x80 ).Select( p => new PoweredMassPoint(
			data.CString( p, 32 ), data.U32( p + 0x20 ), data.F32( p + 0x24 ), data.F32( p + 0x28 ), data.F32( p + 0x2C ),
			data.F32( p + 0x30 ), data.F32( p + 0x34 ), data.F32( p + 0x38 ) ) ).ToList();

		MassPoints = data.Block( a + 0x74, 0x80 ).Select( p => new MassPoint(
			data.CString( p, 32 ), data.I16( p + 0x20 ), data.I16( p + 0x22 ), data.U32( p + 0x24 ),
			data.F32( p + 0x28 ), data.F32( p + 0x2C ), data.F32( p + 0x30 ), data.F32( p + 0x34 ),
			data.Vec3( p + 0x38 ), data.Vec3( p + 0x44 ), data.Vec3( p + 0x50 ),
			data.I16( p + 0x5C ), data.F32( p + 0x60 ), data.F32( p + 0x64 ), data.F32( p + 0x68 ) ) ).ToList();
	}
}
