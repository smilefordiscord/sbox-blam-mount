using System;
using System.Collections.Generic;
using System.Linq;
using Blam.Halo2.Tags;

namespace Blam.Halo2;

/// <summary>
/// The choices a render model region offers as a body group: permutations (indices into the region's
/// permutations) and -1 for none, the default first (what the model's default variant shows), the rest in the
/// region's order with none last.
/// </summary>
public sealed record RegionChoices( int Region, string Name, IReadOnlyList<int> Permutations )
{
	/// <summary>
	/// The choice (index into <see cref="Permutations"/>) that shows a permutation, or none (-1); the default
	/// choice for one the region can't show.
	/// </summary>
	public int IndexOf( int permutation )
	{
		for ( var i = 0; i < Permutations.Count; i++ )
		{
			if ( Permutations[i] == permutation )
				return i;
		}

		return 0;
	}
}

/// <summary>
/// Which permutation of each render model region an object shows.
/// </summary>
public static class ModelPermutations
{
	/// <summary>
	/// The permutation (index into the region's permutations) of each region of <paramref name="model"/>
	/// for a model variant. A variant's region lists the permutations it may show, each with a probability;
	/// Halo picks one at random when it creates the object, so a placement gets the same pick every time
	/// from <paramref name="random"/> (a number in [0, 1) per region). Picks naming no permutation of the
	/// region aren't drawn (-1): an empty name (Johnson's helmet, the 20% of marines without thigh packs), or
	/// one the region doesn't have (the inactive portable shield's <c>shield_off</c>). Regions the variant
	/// doesn't list show their first permutation, like regions of models without variants: every elite
	/// variant, the multiplayer elite's too, leaves out the elite's <c>body</c>. Matched by name.
	/// </summary>
	public static int[] Choose( RenderModelTag model, ModelVariant variant, Func<int, float> random )
	{
		var result = new int[model.Regions.Count];
		for ( var r = 0; r < result.Length; r++ )
		{
			var region = model.Regions[r];
			if ( variant is null )
				continue;

			var choices = variant.Regions.FirstOrDefault( v => string.Equals( v.Name, region.Name, StringComparison.OrdinalIgnoreCase ) )?.Permutations;
			if ( choices is null || choices.Count == 0 )
				continue;

			var total = choices.Sum( c => Math.Max( c.Probability, 0 ) );
			var pick = choices[0];
			if ( total > 0 )
			{
				var remaining = random( r ) * total;
				foreach ( var choice in choices )
				{
					if ( choice.Probability <= 0 )
						continue;

					pick = choice;
					remaining -= choice.Probability;
					if ( remaining < 0 )
						break;
				}
			}

			result[r] = Find( region, pick.Name );
		}

		return result;
	}

	/// <summary>
	/// What each region shows for a variant as a fixed choice rather than a random one: its likeliest pick (the
	/// first of equally likely ones), -1 for none (<see cref="Choose"/>).
	/// </summary>
	public static int[] Likeliest( RenderModelTag model, ModelVariant variant )
	{
		var result = new int[model.Regions.Count];
		if ( variant is null )
			return result;

		for ( var r = 0; r < result.Length; r++ )
		{
			var region = model.Regions[r];
			var choices = variant.Regions.FirstOrDefault( v => string.Equals( v.Name, region.Name, StringComparison.OrdinalIgnoreCase ) )?.Permutations;
			if ( choices is null || choices.Count == 0 )
				continue;

			var pick = choices[0];
			foreach ( var choice in choices )
			{
				if ( choice.Probability > pick.Probability )
					pick = choice;
			}

			result[r] = Find( region, pick.Name );
		}

		return result;
	}

	/// <summary>
	/// The regions of <paramref name="model"/> with a choice to make: more than one permutation, or one that some
	/// variant of <paramref name="hlmt"/> may pick none for. Each region's default is what
	/// <paramref name="defaultVariant"/> shows (<see cref="Likeliest"/>), so regions every variant hides (the
	/// marine's <c>comm_pack</c>) default to none.
	/// </summary>
	public static IReadOnlyList<RegionChoices> Groups( RenderModelTag model, ModelTag hlmt, ModelVariant defaultVariant )
	{
		var groups = new List<RegionChoices>();
		var defaults = Likeliest( model, defaultVariant );
		foreach ( var region in model.Regions )
		{
			if ( region.Permutations.Count == 0 )
				continue;

			var hideable = hlmt is not null && hlmt.Variants.Any( v => CanHide( v, region ) );
			if ( region.Permutations.Count < 2 && !hideable )
				continue;

			var first = defaults[region.Index];
			var choices = new List<int> { first };
			choices.AddRange( Enumerable.Range( 0, region.Permutations.Count ).Where( p => p != first ) );
			if ( hideable && first >= 0 )
				choices.Add( -1 );

			groups.Add( new RegionChoices( region.Index, region.Name, choices ) );
		}

		return groups;
	}

	/// <summary>
	/// The variant an <c>hlmt</c>'s model shows by default: the default variant of the first object using it
	/// (<see cref="ModelOwner"/>), else its first.
	/// </summary>
	public static ModelVariant DefaultVariant( ModelTag hlmt ) => hlmt.FindVariant( ModelOwner( hlmt.Tag )?.DefaultVariant );

	// Per map: the first object using each model tag, by the model's path.
	static readonly System.Runtime.CompilerServices.ConditionalWeakTable<CacheFile, Dictionary<string, TagEntry>> _owners = new();

	/// <summary>
	/// The first object (in tag order) of the map holding <paramref name="hlmt"/> whose model it is, or null.
	/// </summary>
	public static ObjectTag ModelOwner( TagEntry hlmt )
	{
		var owners = _owners.GetValue( hlmt.Cache, cache =>
		{
			var byModel = new Dictionary<string, TagEntry>( StringComparer.OrdinalIgnoreCase );
			foreach ( var tag in cache.Tags )
			{
				if ( ObjectTag.IsObject( tag ) && new ObjectTag( tag ).Model is { Path.Length: > 0 } model )
					byModel.TryAdd( model.Path, tag );
			}

			return byModel;
		} );

		return owners.TryGetValue( hlmt.Path, out var owner ) ? new ObjectTag( owner ) : null;
	}

	// Whether a variant can leave a region without a permutation.
	static bool CanHide( ModelVariant variant, RenderModelRegion region ) =>
		variant.Regions.FirstOrDefault( v => string.Equals( v.Name, region.Name, StringComparison.OrdinalIgnoreCase ) ) is { } listed
		&& listed.Permutations.Any( p => Find( region, p.Name ) < 0 );

	// A permutation of the region by name, -1 for none (an empty name, or one the region doesn't have).
	static int Find( RenderModelRegion region, string name )
	{
		if ( string.IsNullOrEmpty( name ) )
			return -1;

		var permutation = region.Permutations.FirstOrDefault( p => string.Equals( p.Name, name, StringComparison.OrdinalIgnoreCase ) );
		return permutation?.Index ?? -1;
	}
}
