using Blam.Halo1.Tags;

namespace Sandbox.Mounting.Blam.Halo1;

/// <summary>
/// Gives models their gbxmodel markers as attachments ("primary trigger", "right hand", "head",
/// "driver", "gunner"...), on the bone of the marker's node, named by <see cref="ModelMarkerNames"/>:
/// a marker's instance on the base permutations keeps the marker's name, further instances there
/// are numbered ("headlights 1"), and instances only another permutation has are named for it
/// ("primary trigger:damaged").
/// </summary>
/// <remarks>
/// Attachments take their transform relative to their bone (s&amp;box reads them back as the bone's
/// transform times that), unlike bones, which take theirs in model space. Models without a
/// skeleton get model space attachments at the node's default pose.
/// </remarks>
static class ModelMarkers
{
	/// <param name="bones">The bone each of the model's nodes is on, or null for a model without a skeleton.</param>
	/// <param name="pose">Each node's default pose in model space (s&amp;box units), for models without a skeleton.</param>
	/// <param name="taken">Names already used by bones and attachments; this adds the attachments' names.</param>
	/// <returns>The number of attachments added.</returns>
	public static int Add( ModelBuilder builder, GbxModelTag model, IReadOnlyList<string> bones, Transform[] pose, float scale, HashSet<string> taken )
	{
		var added = 0;
		foreach ( var (name, marker) in ModelMarkerNames.Name( model, taken ) )
		{
			if ( marker.Node < 0 || marker.Node >= model.Nodes.Count )
				continue;

			var local = new Transform( (Vector3)marker.Translation * scale, GbxSkeleton.ToRotation( marker.Rotation ) );
			if ( bones is not null )
			{
				if ( marker.Node >= bones.Count || bones[marker.Node] is null )
					continue;

				builder.AddAttachment( name, local.Position, local.Rotation, bones[marker.Node] );
			}
			else
			{
				var world = marker.Node < pose.Length ? pose[marker.Node].ToWorld( local ) : local;
				builder.AddAttachment( name, world.Position, world.Rotation );
			}

			taken.Add( name );
			added++;
		}

		return added;
	}
}
