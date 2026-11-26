using System;
using System.Linq;
using Blam.Halo1.Tags;

namespace Blam.Halo1;

// Inside the namespace so these win over s&box's global types when this compiles into the mount.
using Vector3 = System.Numerics.Vector3;
using Quaternion = System.Numerics.Quaternion;

/// <summary>
/// What an object's tag says about where and how Halo lights it: the bounding sphere its
/// lighting rays start from, and whether it follows the object as it moves.
/// </summary>
/// <param name="Object">The object tag (vehicle, biped, weapon, scenery...).</param>
/// <param name="LocalCenter">The bounding sphere center relative to the object's model origin,
/// in world units, with the root node in its default pose: the root node's default transform
/// applied to the bounding offset. On an animated object it's <see cref="BoundingOffset"/> in the
/// root node's current space instead.</param>
/// <param name="BoundingOffset">The bounding offset, in the root node's space (world units).</param>
/// <param name="Radius">The bounding radius, in world units.</param>
/// <param name="RenderRadius">The radius of the sphere Halo tests against the view to draw the
/// object (the object definition's render bounding radius, 0x104), in world units.</param>
/// <param name="Flags">How the lighting is worked out; <see cref="ObjectLightingFlags.Moving"/>
/// for objects whose lighting follows them (see <see cref="Follows"/>).</param>
public sealed record ObjectLightingShape( TagEntry Object, Vector3 LocalCenter, Vector3 BoundingOffset, float Radius, float RenderRadius, ObjectLightingFlags Flags )
{
	static readonly TagClass Unit = new( "unit" );
	static readonly TagClass Item = new( "item" );

	/// <summary>
	/// Whether the game works this object's lighting out again as it moves: units (bipeds and
	/// vehicles), items (weapons, equipment) and elevators. Everything else (scenery, projectiles,
	/// machines) keeps the lighting it got when it was created.
	/// </summary>
	public bool Follows => Flags.HasFlag( ObjectLightingFlags.Moving );

	/// <summary>
	/// The lighting request for the object with its bounding sphere center at a point.
	/// </summary>
	public ObjectLightingRequest Request( Vector3 center ) => new( center, Radius, Flags );

	/// <summary>
	/// The shape of an object tag.
	/// </summary>
	public static ObjectLightingShape Of( CacheFile cache, TagEntry objectTag )
	{
		var offset = ObjectLightingSampler.BoundingOffset( cache, objectTag );
		var local = offset;
		if ( ObjectLightingSampler.RootNode( cache, objectTag ) is { } root )
			local = root.Translation + Vector3.Transform( offset, root.Rotation );

		var flags = ObjectLightingSampler.DefinitionFlags( cache, objectTag );
		if ( objectTag.Is( Unit ) || objectTag.Is( Item ) )
			flags |= ObjectLightingFlags.Moving;
		if ( MachineTag.IsElevator( cache, objectTag ) )
			flags |= ObjectLightingFlags.Moving | ObjectLightingFlags.RaycastSideways;

		return new ObjectLightingShape( objectTag, local, offset, ObjectLightingSampler.BoundingRadius( cache, objectTag ), cache.TagData.F32( objectTag.Address + 0x104 ), flags );
	}

	/// <summary>
	/// The shape of the object a model belongs to, for a model spawned on its own: the first unit
	/// using it, else the first item, else the first object of any kind. Null if no object uses it.
	/// Objects sharing a model nearly always share their bounding sphere too (the Master Chief's
	/// campaign and multiplayer bipeds differ by 0.02 world units).
	/// </summary>
	public static ObjectLightingShape ForModel( CacheFile cache, TagEntry modelTag )
	{
		var users = cache.Tags.Where( t => t.Is( TagClasses.Object ) && ObjectTags.GetObjectModel( cache, t )?.Id == modelTag.Id ).ToList();
		var tag = users.FirstOrDefault( t => t.Is( Unit ) ) ?? users.FirstOrDefault( t => t.Is( Item ) ) ?? users.FirstOrDefault();
		return tag is null ? null : Of( cache, tag );
	}
}

/// <summary>
/// How Halo eases a moving object's lighting. Every time the game works out an object's lighting
/// again (each frame it's drawn, for one big on screen), it doesn't take the new lighting as is:
/// each color channel, and each component of each light's direction (renormalized after), moves
/// toward the new value by at most <see cref="MaximumChange"/> per frame, so an object driving
/// into a shadow darkens over about a second instead of at once. It only moves while the object
/// does (a parked vehicle keeps what it has), and takes the new lighting at once when the object
/// is first drawn and when it comes back into view.
/// </summary>
public static class MovingObjectLighting
{
	/// <summary>
	/// The most a color channel or direction component changes in one step: 0.03 a frame, at the
	/// 30 frames a second Halo was made for.
	/// </summary>
	public const float MaximumChange = 0.03f;

	/// <summary>
	/// <see cref="MaximumChange"/> per second, for frame rates other than 30.
	/// </summary>
	public const float MaximumChangePerSecond = MaximumChange * 30;

	/// <summary>
	/// One step from <paramref name="current"/> toward <paramref name="desired"/>, each value moving
	/// by at most <paramref name="maximumChange"/>.
	/// </summary>
	/// <remarks>
	/// The game eases the reflection tint and its brightness separately; here the reflection is
	/// their product (see <see cref="ObjectLight.Reflection"/>), eased at the same rate.
	/// </remarks>
	public static ObjectLight Step( ObjectLight current, ObjectLight desired, float maximumChange ) => new(
		Toward( current.Ambient, desired.Ambient, maximumChange ),
		Toward( current.Light0, desired.Light0, maximumChange ),
		Direction( current.ToLight0, desired.ToLight0, maximumChange ),
		Toward( current.Light1, desired.Light1, maximumChange ),
		Direction( current.ToLight1, desired.ToLight1, maximumChange ),
		Toward( current.Reflection, desired.Reflection, maximumChange ) );

	static Vector3 Toward( Vector3 from, Vector3 to, float maximumChange ) =>
		from + Vector3.Clamp( to - from, new Vector3( -maximumChange ), new Vector3( maximumChange ) );

	static Vector3 Direction( Vector3 from, Vector3 to, float maximumChange )
	{
		var v = Toward( from, to, maximumChange );
		return v.LengthSquared() > 1e-12f ? Vector3.Normalize( v ) : to;
	}

	/// <summary>
	/// How far apart two lightings are: the largest difference of any color channel or direction
	/// component.
	/// </summary>
	public static float Difference( ObjectLight a, ObjectLight b )
	{
		static float Max( Vector3 v ) => MathF.Max( MathF.Abs( v.X ), MathF.Max( MathF.Abs( v.Y ), MathF.Abs( v.Z ) ) );
		return MathF.Max( MathF.Max( MathF.Max( Max( a.Ambient - b.Ambient ), Max( a.Light0 - b.Light0 ) ), MathF.Max( Max( a.ToLight0 - b.ToLight0 ), Max( a.Light1 - b.Light1 ) ) ),
			MathF.Max( Max( a.ToLight1 - b.ToLight1 ), Max( a.Reflection - b.Reflection ) ) );
	}
}
