using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using Blam.IO;

namespace Blam.Halo1.TagData;

public static partial class TagExport
{
	/// <summary>
	/// The scenario's HUD messages (its <c>hmt </c> tag) for the help and objective text scripts
	/// set, as <c>HudMessages</c>: each message's name and its elements in order, each either
	/// <c>Text</c> or a button <c>Icon</c> (Halo's icon type: 0 the A button ... 18 action, 28
	/// accept, 32 the first custom icon). Indices are the scripts' hud message indices.
	/// </summary>
	static void WriteHudMessages( Utf8JsonWriter json, CacheFile cache, MemoryRegion d, uint scenario )
	{
		json.WriteStartArray( "HudMessages" );
		var tag = cache.GetTagReference( d, scenario + 0x594 );
		if ( tag is not null && tag.Class == new TagClass( "hmt " ) )
		{
			var a = tag.Address;
			var text = TagDataBytes( d, a );
			var elements = new List<uint>( d.Block( a + 0x14, 0x2 ) );
			foreach ( var e in d.Block( a + 0x20, 0x40 ) )
			{
				json.WriteStartObject();
				json.WriteString( "Name", d.CString( e, 32 ) );
				json.WriteStartArray( "Elements" );

				var position = (int)d.U16( e + 0x20 );
				var first = d.U16( e + 0x22 );
				var count = d.U8( e + 0x24 );
				for ( var i = first; i < first + count && i < elements.Count; i++ )
				{
					var type = d.U8( elements[i] );
					var data = d.U8( elements[i] + 1 );
					json.WriteStartObject();
					if ( type == 0 )
					{
						json.WriteString( "Text", Utf16( text, position, data ) );
						position += data;
					}
					else
					{
						json.WriteNumber( "Icon", data );
					}
					json.WriteEndObject();
				}

				json.WriteEndArray();
				json.WriteEndObject();
			}
		}
		json.WriteEndArray();
	}

	/// <summary>A run of UTF-16 characters, without the null that ends the last one.</summary>
	static string Utf16( ReadOnlySpan<byte> text, int start, int length )
	{
		var from = start * 2;
		var to = Math.Min( (start + length) * 2, text.Length );
		if ( from >= to ) return "";
		return Encoding.Unicode.GetString( text[from..to] ).TrimEnd( '\0' );
	}
}
