using System.Text;
using Blam.Halo2;
using Blam.Halo2.TagData;

namespace Sandbox.Mounting.Blam.Halo2;

// The fonts of the engine's font slots, from MCC's h2_fonts folder, since Halo 2's maps have no font tags.
// Each is fonts/<name>.json (metrics, glyphs and kerning, see TagExport.Font) and fonts/<name>/page.vtex (the glyph page).
static class Halo2Fonts
{
	// The font file names of the engine's font slots 0-11 (English), or none without the fonts.
	public static IReadOnlyList<string> Table( string gameDirectory )
	{
		var table = System.IO.Path.Combine( FontsDirectory( gameDirectory ), "font_table.txt" );
		return File.Exists( table ) ? FontFile.ReadTable( table ) : [];
	}

	public static void Register( MountContext context, string gameDirectory, IReadOnlyList<string> table )
	{
		foreach ( var name in table.Distinct( StringComparer.OrdinalIgnoreCase ) )
		{
			var path = System.IO.Path.Combine( FontsDirectory( gameDirectory ), name );
			if ( !File.Exists( path ) )
				continue;

			context.Add( ResourceType.Text, $"fonts/{name}.json", new FontJsonLoader( path, $"mount://halo2/{PageName( name )}.vtex" ) );
			context.Add( ResourceType.Texture, PageName( name ), new FontPageLoader( path ) );
		}
	}

	static string FontsDirectory( string gameDirectory ) => System.IO.Path.Combine( gameDirectory, "h2_fonts" );

	static string PageName( string name ) => $"fonts/{name}/page";

	sealed class FontJsonLoader( string path, string page ) : Halo2Loader
	{
		protected override object Load() => Encoding.UTF8.GetString( TagExport.Font( new FontFile( path ), page ) );
	}

	// No mips, as the Halo CE mount's glyph pages.
	sealed class FontPageLoader( string path ) : Halo2Loader
	{
		protected override object Load()
		{
			var font = new FontFile( path );
			return Texture.Create( font.Page.Width, font.Page.Height, ImageFormat.RGBA8888 )
				.WithName( Path )
				.WithData( font.ReadPage() )
				.WithStaticUsage()
				.Finish();
		}
	}
}
