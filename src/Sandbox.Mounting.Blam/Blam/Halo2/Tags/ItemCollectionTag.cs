using System.Collections.Generic;
using System.Linq;

namespace Blam.Halo2.Tags;

/// <summary>
/// One choice of an item or vehicle collection: the object, its variant and its weight.
/// </summary>
public sealed record CollectionEntry( float Weight, TagEntry Object, string Variant );

/// <summary>
/// A Halo 2 item collection (<c>itmc</c>) or vehicle collection (<c>vehc</c>), which multiplayer item and
/// vehicle spawns point at: weighted choices, 0x10 bytes each (weight, object, variant), and a spawn time.
/// Both groups have the same layout.
/// </summary>
public sealed class ItemCollectionTag
{
	public static readonly TagClass ItemClass = new( "itmc" );
	public static readonly TagClass VehicleClass = new( "vehc" );

	const int EntrySize = 0x10;

	public TagEntry Tag { get; }
	public IReadOnlyList<CollectionEntry> Entries { get; }

	/// <summary>
	/// Seconds between spawns, 0 for the game's default.
	/// </summary>
	public int SpawnTime { get; }

	public ItemCollectionTag( TagEntry tag )
	{
		Tag = tag;
		var cache = tag.Cache;
		var data = cache.TagData;

		Entries = data.Elements( tag.Address, EntrySize )
			.Select( e => new CollectionEntry( data.F32( e ), cache.GetTagReference( e + 4 ), cache.GetString( data.U32( e + 0xC ) ) ) )
			.ToList();
		SpawnTime = data.I16( tag.Address + 8 );
	}

	public static bool IsCollection( TagEntry tag ) => tag is not null && (tag.Class == ItemClass || tag.Class == VehicleClass);

	/// <summary>
	/// Picks an entry the way the game does, each with a chance in proportion to its weight. When nothing has
	/// weight the entries are equally likely: Coagulation's Banshee collection holds one entry of weight 0, and MCC
	/// spawns its Banshees. Null without entries.
	/// </summary>
	/// <param name="random">A number in [0, 1).</param>
	public CollectionEntry Pick( float random )
	{
		var total = Entries.Where( e => e.Object is not null ).Sum( e => System.Math.Max( e.Weight, 0 ) );
		if ( total <= 0 )
		{
			var any = Entries.Where( e => e.Object is not null ).ToList();
			return any.Count == 0 ? null : any[System.Math.Min( (int)(random * any.Count), any.Count - 1 )];
		}

		var remaining = random * total;
		CollectionEntry last = null;
		foreach ( var entry in Entries )
		{
			if ( entry.Object is null || entry.Weight <= 0 )
				continue;

			last = entry;
			remaining -= entry.Weight;
			if ( remaining < 0 )
				return entry;
		}

		// Rounding at the very end: the last entry that can spawn.
		return last;
	}
}
