using System;
using System.Collections.Generic;
using System.Linq;

namespace Blam.Halo2.Tags;

// The object functions shader animation follows that a game can drive while it plays.
// A model gives each function its shaders follow a slot (Slots), and the game sets the function's value on the model's renderers by that slot.
public static class ShaderFunctions
{
	// How many functions a model's shaders can follow at once.
	public const int MaxSlots = 8;

	// Functions left as at rest: campaign looks (heretic, burnt), emblems (which have attributes of their own), and constants.
	static bool Drivable( string name ) =>
		!string.IsNullOrEmpty( name )
		&& !name.StartsWith( "emblem_", StringComparison.OrdinalIgnoreCase )
		&& !(name.Equals( "change_to_heretic", StringComparison.OrdinalIgnoreCase ) || name.Equals( "elite_burnt", StringComparison.OrdinalIgnoreCase )
			|| name.Equals( "random_constant", StringComparison.OrdinalIgnoreCase ) || name.Equals( "one", StringComparison.OrdinalIgnoreCase )
			|| name.Equals( "zero", StringComparison.OrdinalIgnoreCase ));

	// The functions a shader's animation follows: the input of a curve that isn't constant, or a range function's range.
	public static IEnumerable<string> Inputs( TagEntry shader )
	{
		foreach ( var overlay in ShaderAnimation.Overlays( shader ) )
		{
			if ( !overlay.IsTime && !overlay.Function.IsEmpty && overlay.Function.Type != MappingFunction.Constant && Drivable( overlay.Input ) )
				yield return overlay.Input;
			if ( overlay.Function.IsRange && Drivable( overlay.Range ) )
				yield return overlay.Range;
		}
	}

	// A model's slots: the functions its shaders follow, in name order, at most MaxSlots.
	public static IReadOnlyList<string> Slots( IEnumerable<TagEntry> shaders ) =>
		shaders.Where( s => s is { Address: not 0 } && s.Class == ShaderTag.Class )
			.SelectMany( Inputs )
			.Distinct( StringComparer.OrdinalIgnoreCase )
			.Order( StringComparer.OrdinalIgnoreCase )
			.Take( MaxSlots )
			.ToList();
}
