using System;
using System.IO;
using System.Text.Json;

namespace Blam.Halo1.TagData;

/// <summary>
/// Writes Halo CE tag data as JSON, following a <see cref="TagStruct"/> layout (see <see cref="LayoutJsonWriter"/>).
/// </summary>
public sealed class TagJsonWriter : LayoutJsonWriter
{
	readonly CacheFile _cache;

	/// <summary>
	/// Called with every tag reference written, e.g. to also export the tags a tag uses.
	/// </summary>
	public Action<TagEntry> ReferenceWritten { get; set; }

	public TagJsonWriter( CacheFile cache ) : base( cache.TagData, TagFormat.Halo1 )
	{
		_cache = cache;
	}

	/// <summary>
	/// The path of a tag as written in JSON: <c>weapons/pistol/bullet.jpt!</c>.
	/// </summary>
	public static string TagName( TagEntry tag ) => tag is null ? null : $"{tag.Path.Replace( '\\', '/' )}.{tag.Class}";

	/// <summary>
	/// Writes a whole tag as a JSON document.
	/// </summary>
	public byte[] WriteTag( TagEntry tag, TagStruct layout )
	{
		using var stream = new MemoryStream();
		using ( var json = new Utf8JsonWriter( stream, Indented ) )
			WriteTag( json, tag, layout );

		return stream.ToArray();
	}

	/// <summary>
	/// Writes a whole tag as a JSON object.
	/// </summary>
	public void WriteTag( Utf8JsonWriter json, TagEntry tag, TagStruct layout )
	{
		TagAddress = tag.Address;
		WriteStruct( json, layout, tag.Address );
	}

	/// <summary>
	/// The tag a 16 byte tag reference points to, or null.
	/// </summary>
	public TagEntry Reference( uint address ) => _cache.GetTagReference( Data, address );

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

	protected override string StringIdText( uint id ) => throw new InvalidOperationException( "Halo CE tags have no string ids" );
}
