using System.Collections.Generic;

namespace Blam.Halo1.Tags;

/// <summary>
/// One of the items an item collection can spawn, and how likely it is relative to the others.
/// </summary>
public sealed record ItemCollectionEntry( TagEntry Item, float Weight );

/// <summary>
/// Reader for <c>itmc</c> (item collection) tags: the weapons or equipment a multiplayer item spawn
/// picks from, each time it spawns one.
/// </summary>
/// <remarks>
/// Offsets: permutations block 0x0 (84 bytes each: weight 0x20, item reference 0x24), spawn time
/// 0xC (seconds, int16; 0 means the game's default of 30).
/// </remarks>
public sealed class ItemCollectionTag
{
	public IReadOnlyList<ItemCollectionEntry> Items { get; }

	/// <summary>
	/// Seconds between spawns, or 0 for the default.
	/// </summary>
	public int SpawnTime { get; }

	public ItemCollectionTag( CacheFile cache, TagEntry tag )
	{
		var data = cache.TagData;

		var items = new List<ItemCollectionEntry>();
		foreach ( var entry in data.Block( tag.Address, 0x54 ) )
		{
			var item = cache.GetTagReference( data, entry + 0x24 );
			if ( item is not null )
				items.Add( new ItemCollectionEntry( item, data.F32( entry + 0x20 ) ) );
		}
		Items = items;
		SpawnTime = data.I16( tag.Address + 0xC );
	}

	/// <summary>
	/// Picks an item the way the game does, each with a chance in proportion to its weight.
	/// </summary>
	/// <param name="random">A number in [0, 1).</param>
	public TagEntry Pick( float random )
	{
		var total = 0f;
		foreach ( var entry in Items )
			total += entry.Weight;

		var remaining = random * total;
		foreach ( var entry in Items )
		{
			remaining -= entry.Weight;
			if ( remaining < 0 )
				return entry.Item;
		}

		// Rounding at the very end: the last item that can spawn. With no weights at all the game
		// spawns nothing.
		for ( var i = Items.Count - 1; i >= 0; i-- )
		{
			if ( Items[i].Weight > 0 )
				return Items[i].Item;
		}

		return null;
	}
}
