using System.Collections.Generic;
using System.Text.Json;
using Blam.Halo2.Tags;

namespace Blam.Halo2.TagData;

// String lists (unic) as JSON.
public static partial class TagExport
{
	// A string list as { name: text } in one language; a name used twice keeps its first text.
	static void WriteStrings( Utf8JsonWriter json, StringListTag list, int language )
	{
		json.WriteStartObject();
		var written = new HashSet<string>();
		foreach ( var (name, text) in list.Strings( language ) )
		{
			if ( written.Add( name ) )
				json.WriteString( name, text );
		}
		json.WriteEndObject();
	}

	// A string list in every language: { Strings: { language: { name: text } } }.
	static byte[] StringList( TagEntry tag ) => Write( json =>
	{
		var list = new StringListTag( tag );
		json.WriteStartObject();
		json.WriteStartObject( "Strings" );
		for ( var language = 0; language < CacheFile.Languages.Length; language++ )
		{
			json.WritePropertyName( CacheFile.Languages[language] );
			WriteStrings( json, list, language );
		}
		json.WriteEndObject();
		json.WriteEndObject();
	} );
}
