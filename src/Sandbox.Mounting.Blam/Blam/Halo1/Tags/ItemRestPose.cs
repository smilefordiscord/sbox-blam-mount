using System;
using System.Linq;

namespace Blam.Halo1.Tags;

// Inside the namespace so these win over s&box's global Vector3 when this compiles into the mount.
using Vector3 = System.Numerics.Vector3;
using Quaternion = System.Numerics.Quaternion;

/// <summary>
/// How an item (weapon, equipment, garbage) lies once it has dropped onto the ground. Halo turns
/// a landed item by the shortest rotation that points its "ground point" marker's up axis along the
/// surface normal (for floors: normal z above 0.7071 and coming in slower than 0.05 wu per tick),
/// and puts the marker on the surface. Multiplayer items spawn unrotated (forward +X, up +Z,
/// whatever the spawn's facing) and fall, so on flat ground they end up turned by
/// <see cref="Rotation"/> with their ground point on the floor. Items without the marker (the CTF
/// flag, some powerups) keep the pose they had; items spawned "at rest" (levitating powerups)
/// never fall and keep their spawn pose.
/// </summary>
/// <param name="GroundPoint">The ground point marker in model space (world units, default pose).</param>
/// <param name="Up">The marker's up axis in model space: the side of the item that ends up facing the surface normal.</param>
/// <param name="Rotation">The rotation (model space to world, yaw 0) that lays the item on flat ground: the shortest rotation taking <see cref="Up"/> to +Z.</param>
public sealed record ItemRestPose( Vector3 GroundPoint, Vector3 Up, Quaternion Rotation )
{
	/// <summary>
	/// Where the object's origin goes relative to the point on the floor the ground point rests on,
	/// in world units, for an item rested with <see cref="Rotation"/>.
	/// </summary>
	public Vector3 Origin => -Vector3.Transform( GroundPoint, Rotation );

	/// <summary>
	/// The rest pose of an item model, or null if it has no "ground point" marker.
	/// </summary>
	public static ItemRestPose For( GbxModelTag model )
	{
		var marker = model.Markers.FirstOrDefault( m => m.Name == "ground point" );
		if ( marker is null || marker.Instances.Count == 0 )
			return null;

		var instance = marker.Instances[0];
		var pose = FirstPersonSkeleton.DefaultPose( model );
		var node = instance.Node >= 0 && instance.Node < pose.Length ? pose[instance.Node] : (Position: Vector3.Zero, Rotation: Quaternion.Identity);
		var position = node.Position + Vector3.Transform( instance.Translation, node.Rotation );
		var rotation = Quaternion.Normalize( node.Rotation * instance.Rotation );
		var up = Vector3.Normalize( Vector3.Transform( Vector3.UnitZ, rotation ) );

		return new ItemRestPose( position, up, Lay( up, Vector3.Transform( Vector3.UnitX, rotation ), Vector3.UnitZ ) );
	}

	/// <summary>
	/// The shortest rotation taking <paramref name="up"/> to <paramref name="normal"/>, as Halo
	/// builds it (half-angle quaternion). When the two are opposite, the item is turned about its
	/// forward axis projected onto the surface instead.
	/// </summary>
	public static Quaternion Lay( Vector3 up, Vector3 forward, Vector3 normal )
	{
		var s = MathF.Sqrt( 2 * (Vector3.Dot( up, normal ) + 1) );
		if ( s > 0.01f )
			return Quaternion.Normalize( new Quaternion( Vector3.Cross( up, normal ) / s, s * 0.5f ) );

		var axis = Vector3.Cross( Vector3.Cross( normal, forward ), normal );
		return axis.LengthSquared() > 1e-8f ? Quaternion.CreateFromAxisAngle( Vector3.Normalize( axis ), MathF.PI ) : Quaternion.Identity;
	}
}
