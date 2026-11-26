using System.Text.Json;

namespace Blam.Halo2.TagData;

// fonts/<name>.json: a font's metrics and glyphs (see FontFile), with Halo CE's field names.
public static partial class TagExport
{
	// The glyphs are on one page (Page: RGBA with straight alpha, as the icons are coloured).
	// A glyph's top left goes at (pen + kerning + OriginX, baseline - OriginY).
	// Then the pen moves right by kerning + OriginX + Advance.
	// Kerning is KerningPairs' adjustment for the previous character and this one, 0 for other pairs.
	// Characters the font lacks draw MissingCharacter.
	public static byte[] Font( FontFile font, string page ) => Write( json =>
	{
		json.WriteStartObject();
		json.WriteString( "Name", font.Name );
		json.WriteNumber( "AscendingHeight", font.AscendingHeight );
		json.WriteNumber( "DescendingHeight", font.DescendingHeight );
		json.WriteNumber( "LeadingHeight", font.LeadingHeight );
		json.WriteNumber( "LeadingWidth", font.LeadingWidth );
		json.WriteString( "Page", page );
		json.WriteNumber( "PageWidth", font.Page.Width );
		json.WriteNumber( "PageHeight", font.Page.Height );

		json.WritePropertyName( "MissingCharacter" );
		WriteCharacter( json, font.MissingCharacter );
		json.WriteStartArray( "Characters" );
		foreach ( var character in font.Characters )
			WriteCharacter( json, character );
		json.WriteEndArray();

		json.WriteStartArray( "KerningPairs" );
		foreach ( var (first, second, adjustment) in font.KerningPairs )
		{
			json.WriteStartObject();
			json.WriteNumber( "First", first );
			json.WriteNumber( "Second", second );
			json.WriteNumber( "Adjustment", adjustment );
			json.WriteEndObject();
		}
		json.WriteEndArray();
		json.WriteEndObject();
	} );

	static void WriteCharacter( Utf8JsonWriter json, FontCharacter character )
	{
		json.WriteStartObject();
		json.WriteNumber( "CodePoint", character.CodePoint );
		json.WriteNumber( "Advance", character.Advance );
		json.WriteNumber( "Width", character.Width );
		json.WriteNumber( "Height", character.Height );
		json.WriteNumber( "OriginX", character.OriginX );
		json.WriteNumber( "OriginY", character.OriginY );
		json.WriteNumber( "PageX", character.PageX );
		json.WriteNumber( "PageY", character.PageY );
		json.WriteEndObject();
	}
}
