using System.Collections.Generic;
using Blam.Halo3.Tags;

namespace Blam.Halo3;

// Inside the namespace so these win over s&box's global vector types when this compiles into the mount.
using Quaternion = System.Numerics.Quaternion;
using Vector3 = System.Numerics.Vector3;

// Where an object is: position, rotation and uniform scale, in world units.
public readonly record struct ObjectFrame( Vector3 Position, Quaternion Rotation, float Scale )
{
	public Vector3 Transform( Vector3 local ) => Position + Scale * Vector3.Transform( local, Rotation );

	// The frame of something at translation, turned by rotation, in this frame's object space.
	public ObjectFrame Then( Quaternion rotation, Vector3 translation ) => new( Transform( translation ), Quaternion.Normalize( Rotation * rotation ), Scale );

	public Vector3 Forward => Vector3.Transform( Vector3.UnitX, Rotation );
	public Vector3 Up => Vector3.Transform( Vector3.UnitZ, Rotation );
}

// An object a model variant attaches to another (a Warthog's chaingun), where it ends up and the variant it shows; Depth 1 is attached to the placed object, 2 to an attached one.
public sealed record AttachedObject( TagEntry Object, ObjectFrame Frame, string Variant, int Depth );

// Resolves the objects a placed object's model variant attaches (hlmt variant objects), as Halo2's AttachedObjects: the child's marker on the parent's marker, both models in their default pose.
public static class AttachedObjects
{
	public static List<AttachedObject> For( TagEntry objectTag, ObjectFrame frame, string variant )
	{
		var result = new List<AttachedObject>();
		Add( objectTag, frame, variant, 0, result );
		return result;
	}

	static void Add( TagEntry objectTag, ObjectFrame frame, string variantName, int depth, List<AttachedObject> result )
	{
		if ( depth >= 3 || !ObjectTag.IsObject( objectTag ) )
			return;

		var obj = new ObjectTag( objectTag );
		if ( obj.Model is null )
			return;

		var hlmt = new ObjectModel( obj.Model );
		var variant = hlmt.FindVariant( string.IsNullOrEmpty( variantName ) ? obj.DefaultVariant : variantName );
		if ( variant is null || variant.Objects.Count == 0 )
			return;

		var parentModel = RenderModel( hlmt );
		foreach ( var attached in variant.Objects )
		{
			if ( !ObjectTag.IsObject( attached.Object ) )
				continue;

			var parentMarker = FindMarker( parentModel, attached.ParentMarker ) ?? (Quaternion.Identity, Vector3.Zero);
			var childFrame = frame.Then( parentMarker.Rotation, parentMarker.Translation );

			var childObject = new ObjectTag( attached.Object );
			if ( !string.IsNullOrEmpty( attached.ChildMarker ) && childObject.Model is not null
				&& FindMarker( RenderModel( new ObjectModel( childObject.Model ) ), attached.ChildMarker ) is { } childMarker )
			{
				// The child's marker goes where the parent's is: undo the child marker's own transform.
				var inverse = Quaternion.Conjugate( childMarker.Rotation );
				childFrame = childFrame.Then( inverse, -Vector3.Transform( childMarker.Translation, inverse ) );
			}

			var childVariant = string.IsNullOrEmpty( attached.ChildVariant ) ? null : attached.ChildVariant;
			result.Add( new AttachedObject( attached.Object, childFrame, childVariant, depth + 1 ) );
			Add( attached.Object, childFrame, childVariant, depth + 1, result );
		}
	}

	// An object's render model, or null when it has none.
	public static RenderModelTag RenderModelOf( TagEntry objectTag )
	{
		if ( !ObjectTag.IsObject( objectTag ) )
			return null;
		var obj = new ObjectTag( objectTag );
		return obj.Model is null || obj.Model.Address == 0 ? null : RenderModel( new ObjectModel( obj.Model ) );
	}

	static RenderModelTag RenderModel( ObjectModel hlmt ) =>
		hlmt.RenderModel is { Address: not 0 } tag && tag.Class == new TagClass( "mode" ) ? new RenderModelTag( tag ) : null;

	// A marker in model space (the model's default pose): the first of that name, preferring one for any region and permutation.
	public static (Quaternion Rotation, Vector3 Translation)? FindMarker( RenderModelTag model, string name )
	{
		if ( model is null || string.IsNullOrEmpty( name ) )
			return null;

		RenderModelMarker found = null;
		foreach ( var marker in model.Markers )
		{
			if ( !string.Equals( marker.Name, name, System.StringComparison.OrdinalIgnoreCase ) )
				continue;
			if ( found is null || marker.Region < 0 && marker.Permutation < 0 && (found.Region >= 0 || found.Permutation >= 0) )
				found = marker;
		}

		if ( found is null )
			return null;

		var pose = model.DefaultPose();
		var rotation = Quaternion.Normalize( found.Rotation );
		if ( found.Node < 0 || found.Node >= pose.Length )
			return (rotation, found.Translation);

		var (translation, nodeRotation) = pose[found.Node];
		return (Quaternion.Normalize( nodeRotation * rotation ), translation + Vector3.Transform( found.Translation, nodeRotation ));
	}
}
