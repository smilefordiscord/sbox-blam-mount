using System.Collections.Generic;
using Blam.Halo2.Tags;

namespace Blam.Halo2;

// Inside the namespace so these win over s&box's global vector types when this compiles into the mount.
using Quaternion = System.Numerics.Quaternion;
using Vector3 = System.Numerics.Vector3;

/// <summary>
/// Where an object is: position, rotation and uniform scale, in world units.
/// </summary>
public readonly record struct ObjectFrame( Vector3 Position, Quaternion Rotation, float Scale )
{
	/// <summary>
	/// A point in object space placed in the world.
	/// </summary>
	public Vector3 Transform( Vector3 local ) => Position + Scale * Vector3.Transform( local, Rotation );

	/// <summary>
	/// The frame of something placed at <paramref name="translation"/>, turned by <paramref name="rotation"/>,
	/// in this frame's object space.
	/// </summary>
	public ObjectFrame Then( Quaternion rotation, Vector3 translation ) => new( Transform( translation ), Quaternion.Normalize( Rotation * rotation ), Scale );

	public Vector3 Forward => Vector3.Transform( Vector3.UnitX, Rotation );
	public Vector3 Up => Vector3.Transform( Vector3.UnitZ, Rotation );
}

/// <summary>
/// An object a model variant attaches to another (a Warthog's turret), where it ends up.
/// </summary>
public sealed record AttachedObject( TagEntry Object, ObjectFrame Frame );

/// <summary>
/// Resolves the objects a placed object's model variant attaches.
/// </summary>
public static class AttachedObjects
{
	/// <summary>
	/// The objects the variant attaches, and theirs in turn (two levels), with the child's marker on the
	/// parent's marker in both models' default pose. Attached objects show their default variant.
	/// </summary>
	public static List<AttachedObject> For( TagEntry objectTag, ObjectFrame frame, string variant )
	{
		var result = new List<AttachedObject>();
		Add( objectTag, frame, variant, 0, result );
		return result;
	}

	static void Add( TagEntry objectTag, ObjectFrame frame, string variantName, int depth, List<AttachedObject> result )
	{
		if ( depth >= 2 || !ObjectTag.IsObject( objectTag ) )
			return;

		var obj = new ObjectTag( objectTag );
		if ( obj.Model is not { Address: not 0 } hlmtTag || hlmtTag.Class != ModelTag.Class )
			return;

		var hlmt = new ModelTag( hlmtTag );
		var variant = hlmt.FindVariant( variantName ?? obj.DefaultVariant );
		if ( variant is null || variant.Objects.Count == 0 )
			return;

		var parentModel = RenderModel( hlmt );
		foreach ( var attached in variant.Objects )
		{
			if ( !ObjectTag.IsObject( attached.Object ) )
				continue;

			var parentMarker = parentModel?.FindMarker( attached.ParentMarker ) ?? (Quaternion.Identity, Vector3.Zero);
			var childFrame = frame.Then( parentMarker.Rotation, parentMarker.Translation );

			if ( !string.IsNullOrEmpty( attached.ChildMarker ) )
			{
				var child = new ObjectTag( attached.Object );
				var childModel = child.Model is { Address: not 0 } childHlmt && childHlmt.Class == ModelTag.Class ? RenderModel( new ModelTag( childHlmt ) ) : null;
				if ( childModel?.FindMarker( attached.ChildMarker ) is { } childMarker )
				{
					// The child's marker goes where the parent's is: undo the child marker's own transform.
					var inverse = Quaternion.Conjugate( childMarker.Rotation );
					childFrame = childFrame.Then( inverse, -Vector3.Transform( childMarker.Translation, inverse ) );
				}
			}

			result.Add( new AttachedObject( attached.Object, childFrame ) );
			Add( attached.Object, childFrame, null, depth + 1, result );
		}
	}

	static RenderModelTag RenderModel( ModelTag hlmt ) =>
		hlmt.RenderModel is { Address: not 0 } tag && tag.Class == RenderModelTag.Class ? new RenderModelTag( tag ) : null;
}
