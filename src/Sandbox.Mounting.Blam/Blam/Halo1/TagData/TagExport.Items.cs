using System.Linq;
using Blam.Halo1.Tags;

namespace Blam.Halo1.TagData;

public static partial class TagExport
{
	static readonly TagClass[] ItemClasses = [new( "weap" ), new( "eqip" ), new( "garb" )];

	/// <summary>
	/// How a dropped item lies on flat ground (see <see cref="ItemRestPose"/>):
	/// <c>{ Model, GroundPoint: [x, y, z], Up: [x, y, z], Rotation: [i, j, k, w], Origin: [x, y, z] }</c>.
	/// GroundPoint and Up are the "ground point" marker in model space (world units); Rotation turns
	/// the model (yaw 0) so that marker faces up, and Origin is where the object's origin goes
	/// relative to the floor point under the marker, in world units. Halo and s&amp;box share axes, so
	/// Rotation is a ready s&amp;box rotation; a game puts its own yaw in front of it. Null for items
	/// without the marker, which keep the pose they spawned in.
	/// </summary>
	static byte[] ItemRestPoseJson( CacheFile cache, TagEntry item )
	{
		var modelTag = ObjectTags.GetObjectModel( cache, item );
		if ( modelTag is null || modelTag.Class != TagClasses.GbxModel )
			return null;

		var rest = ItemRestPose.For( new GbxModelTag( cache, modelTag ) );
		return Write( json =>
		{
			if ( rest is null )
			{
				json.WriteNullValue();
				return;
			}

			json.WriteStartObject();
			json.WriteString( "Model", TagJsonWriter.TagName( modelTag ) );
			WriteNumbers( "GroundPoint", rest.GroundPoint.X, rest.GroundPoint.Y, rest.GroundPoint.Z );
			WriteNumbers( "Up", rest.Up.X, rest.Up.Y, rest.Up.Z );
			WriteNumbers( "Rotation", rest.Rotation.X, rest.Rotation.Y, rest.Rotation.Z, rest.Rotation.W );
			WriteNumbers( "Origin", rest.Origin.X, rest.Origin.Y, rest.Origin.Z );
			json.WriteEndObject();

			void WriteNumbers( string name, params float[] values )
			{
				json.WriteStartArray( name );
				foreach ( var value in values )
					json.WriteNumberValue( System.MathF.Round( value, 5 ) );
				json.WriteEndArray();
			}
		} );
	}

	static bool IsItem( TagEntry tag ) => !string.IsNullOrEmpty( tag.Path ) && ItemClasses.Contains( tag.Class );
}
