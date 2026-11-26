using System.Collections.Generic;
using System.Linq;

namespace Blam.Halo3.Tags;

// How Havok moves a rigid body (phmo rigid body +0x1A): the first four simulate, keyframed bodies follow animation, fixed ones never move.
public enum PhysicsMotion
{
	Sphere = 0,
	StabilizedSphere = 1,
	Box = 2,
	StabilizedBox = 3,
	Keyframed = 4,
	Fixed = 5,
}

// A physics model (phmo) rigid body: its node, motion, mass and damping (0 to 10).
public sealed record PhysicsRigidBody( int Node, int Region, int Permutation, PhysicsMotion Motion, float Mass, float LinearDamping, float AngularDamping )
{
	public bool Simulated => Motion is not (PhysicsMotion.Keyframed or PhysicsMotion.Fixed);
}

// A physics model (phmo): the object's mass (+0x4) and its rigid bodies (+0x58, 0xC0 each).
public sealed class PhysicsModelTag
{
	public static readonly TagClass Class = new( "phmo" );

	public TagEntry Tag { get; }
	public float Mass { get; }
	public IReadOnlyList<PhysicsRigidBody> RigidBodies { get; }

	// True when Halo simulates any of its bodies: a crate that falls and can be pushed around.
	public bool Simulated => RigidBodies.Any( b => b.Simulated );

	public PhysicsModelTag( TagEntry tag )
	{
		Tag = tag;
		var data = tag.Data;
		Mass = data.F32( 4 );
		RigidBodies = data.Block( 0x58, 0xC0 ).Select( b => new PhysicsRigidBody(
			b.I16( 0 ), b.I16( 2 ), b.I16( 4 ), (PhysicsMotion)b.U16( 0x1A ), b.F32( 0x60 ), b.F32( 0x24 ), b.F32( 0x28 ) ) ).ToList();
	}

	public static PhysicsModelTag For( TagEntry tag ) => tag is { Address: not 0 } && tag.Is( Class ) ? new PhysicsModelTag( tag ) : null;
}
