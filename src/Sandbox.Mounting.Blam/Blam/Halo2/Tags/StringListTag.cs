using System;
using System.Collections.Generic;

namespace Blam.Halo2.Tags;

// A string list (unic): a run of its map's localized strings per language (CacheFile.LocalizedStrings).
// HUD messages, prompts and menus look a string up by its name, a string id.
public sealed class StringListTag
{
	public static readonly TagClass Class = new( "unic" );

	public TagEntry Tag { get; }

	public StringListTag( TagEntry tag )
	{
		if ( tag.Class != Class )
			throw new ArgumentException( $"{tag} is not a string list", nameof( tag ) );

		Tag = tag;
	}

	// The strings in one language (an index of CacheFile.Languages), in order: each string's name and text.
	// The run's first string and count are two int16s per language at 0x10.
	public IReadOnlyList<(string Name, string Text)> Strings( int language )
	{
		var cache = Tag.Cache;
		var entry = Tag.Address + 0x10 + (uint)(language * 4);
		int first = cache.TagData.U16( entry ), count = cache.TagData.U16( entry + 2 );
		var table = cache.LocalizedStrings( language );

		var strings = new List<(string, string)>( count );
		for ( var i = first; i < first + count && i < table.Count; i++ )
			strings.Add( (cache.GetString( table[i].StringId ) ?? "", table[i].Text) );
		return strings;
	}
}
