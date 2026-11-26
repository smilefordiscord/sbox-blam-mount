using System;
using System.Collections.Generic;
using System.Linq;

namespace Blam.Halo1.Tags;

// Inside the namespace so these win over s&box's global types when this compiles into the mount.
using Vector3 = System.Numerics.Vector3;

/// <summary>
/// Unique names for every place a model's markers are, for models that can only have one
/// attachment per name.
/// </summary>
/// <remarks>
/// The base permutation (the first of each region, which the mount's models show) comes first: a
/// marker's first instance there keeps the marker's name ("primary trigger"), further ones there
/// are numbered from 1 ("primary trigger 1"). Instances only other permutations have are named
/// for the permutation ("primary trigger:damaged", then "primary trigger:damaged 1"). A marker
/// with no instance on a base permutation gives its plain name to its first instance. Instances on
/// another permutation at the same node and place as one already named are left out (most
/// models repeat their markers for every permutation). Names are compared ignoring case, and a
/// name already taken (by a bone, say) gets " marker" appended.
/// </remarks>
public static class ModelMarkerNames
{
	public static List<(string Name, GbxMarkerInstance Instance)> Name( GbxModelTag model, IEnumerable<string> takenNames = null )
	{
		var result = new List<(string Name, GbxMarkerInstance Instance)>();
		var used = new HashSet<string>( takenNames ?? [], StringComparer.OrdinalIgnoreCase );

		foreach ( var marker in model.Markers )
		{
			var markerName = string.IsNullOrWhiteSpace( marker.Name ) ? "marker" : marker.Name;
			var placed = new List<GbxMarkerInstance>();
			var ordered = marker.Instances.Where( i => i.Permutation == 0 ).Concat( marker.Instances.Where( i => i.Permutation != 0 ) );
			var counts = new Dictionary<string, int>( StringComparer.OrdinalIgnoreCase );

			foreach ( var instance in ordered )
			{
				if ( placed.Any( p => SamePlace( p, instance ) ) )
					continue;

				var name = placed.Count == 0 || instance.Permutation == 0
					? markerName
					: $"{markerName}:{PermutationName( model, instance )}";
				var number = counts.GetValueOrDefault( name );
				counts[name] = number + 1;
				if ( number > 0 )
					name = $"{name} {number}";

				while ( !used.Add( name ) )
					name += " marker";

				placed.Add( instance );
				result.Add( (name, instance) );
			}
		}

		return result;
	}

	static string PermutationName( GbxModelTag model, GbxMarkerInstance instance )
	{
		var permutations = instance.Region >= 0 && instance.Region < model.Regions.Count ? model.Regions[instance.Region].Permutations : null;
		var name = permutations is not null && instance.Permutation >= 0 && instance.Permutation < permutations.Count ? permutations[instance.Permutation].Name : null;
		return string.IsNullOrWhiteSpace( name ) ? $"permutation {instance.Permutation}" : name;
	}

	static bool SamePlace( GbxMarkerInstance a, GbxMarkerInstance b ) =>
		a.Node == b.Node
		&& Vector3.DistanceSquared( a.Translation, b.Translation ) < 1e-8f
		&& MathF.Abs( System.Numerics.Quaternion.Dot( a.Rotation, b.Rotation ) ) > 0.99999f;
}
