using System;
using System.Text.Json;

namespace Blam.Halo2.TagData;

// Writes Halo 2 tag data as JSON, following a TagStruct layout (see LayoutJsonWriter).
// A writer reads one cache file's tag data, so a tag a multiplayer map takes from shared.map needs shared.map's writer.
public sealed class TagJsonWriter : LayoutJsonWriter
{
	readonly CacheFile _cache;

	// Called with every tag reference written, e.g. to also export the tags a tag uses.
	public Action<TagEntry> ReferenceWritten { get; set; }

	public TagJsonWriter( CacheFile cache ) : base( cache.TagData, TagFormat.Halo2 )
	{
		_cache = cache;
	}

	// The path of a tag as written in JSON: objects/weapons/rifle/battle_rifle/battle_rifle.weap.
	public static string TagName( TagEntry tag ) => tag is null ? null : $"{tag.Path.Replace( '\\', '/' )}.{tag.Class}";

	// Writes a whole tag as a JSON object. The tag's data must be in this writer's cache file.
	public void WriteTag( Utf8JsonWriter json, TagEntry tag, TagStruct layout )
	{
		if ( tag.Cache != _cache )
			throw new ArgumentException( $"{tag} is in {tag.Cache.Name}, not {_cache.Name}", nameof( tag ) );

		TagAddress = tag.Address;
		WriteStruct( json, layout, tag.Address );
	}

	// The tag an 8 byte tag reference points to, or null.
	public TagEntry Reference( uint address ) => _cache.GetTagReference( address );

	protected override void WriteReference( Utf8JsonWriter json, uint address )
	{
		var tag = Reference( address );
		if ( tag is null )
		{
			json.WriteNullValue();
			return;
		}

		json.WriteStringValue( TagName( tag ) );
		ReferenceWritten?.Invoke( tag );
	}

	// The text of the string id at the address.
	public string StringId( uint address ) => StringIdText( Data.U32( address ) );

	protected override string StringIdText( uint id ) => _cache.GetString( id ) ?? "";
}
