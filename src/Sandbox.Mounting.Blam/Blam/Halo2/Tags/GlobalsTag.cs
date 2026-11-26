using System.Collections.Generic;
using System.Linq;

namespace Blam.Halo2.Tags;

/// <summary>
/// One of the global materials, which collision surfaces (through the structure BSP's collision materials) and
/// collision models name: <c>hard_metal_thin_hum</c>, <c>soft_organic_plant_leafy</c>... Each one's parent is the
/// name with its last part removed (<c>hard_metal_thin</c>), up to <c>default_material</c>.
/// </summary>
public sealed record GlobalMaterial( int Index, string Name, string Parent );

/// <summary>
/// The Halo 2 globals (<c>matg</c>), as far as the mount needs them. Multiplayer maps use shared.map's.
/// </summary>
public sealed class GlobalsTag
{
	public static readonly TagClass Class = new( "matg" );

	const uint MaterialsBlock = 0x150;
	const int MaterialSize = 0xB4;

	public TagEntry Tag { get; }

	/// <summary>
	/// The global materials (183 in every MCC map but the main menu and the intro, which have only the default).
	/// </summary>
	public IReadOnlyList<GlobalMaterial> Materials { get; }

	public GlobalsTag( TagEntry tag )
	{
		Tag = tag;
		var cache = tag.Cache;
		var data = cache.TagData;

		var index = 0;
		Materials = data.Elements( tag.Address + MaterialsBlock, MaterialSize )
			.Select( e => new GlobalMaterial( index++, cache.GetString( data.U32( e ) ) ?? "", cache.GetString( data.U32( e + 4 ) ) ?? "" ) )
			.ToList();
	}
}
