using System.Linq;

namespace Blam.Halo1.TagData;

// Animation graphs: the unit damage animations.
public static partial class TagLayouts
{
	/// <summary>
	/// The unit damage block: an animation slot per damage type, direction and body part, in the
	/// order Halo indexes them ((type * 4 + direction) * 11 + part), each named as the animation it
	/// usually holds ("s-ping front gut"). Built on demand, as the graph layout that uses it may be
	/// initialized before this file's fields.
	/// </summary>
	static TagStruct AnimationUnitDamage()
	{
		string[] types = ["s-ping", "h-ping", "s-kill", "h-kill"];
		string[] directions = ["front", "left", "right", "back"];
		string[] parts = ["gut", "chest", "head", "l-arm", "l-hand", "l-leg", "l-foot", "r-arm", "r-hand", "r-leg", "r-foot"];
		var slots = types.SelectMany( type => directions.SelectMany( direction => parts.Select( part => $"{type} {direction} {part}" ) ) ).ToArray();
		return AnimationSlot( "unit damage animation", slots );
	}
}
