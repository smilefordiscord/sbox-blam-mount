using Blam.Halo1.Tags;

namespace Sandbox.Mounting.Blam.Halo1;

/// <summary>
/// A font tag's glyph page: every character packed onto one RGBA8888 texture, white with the
/// glyph coverage as alpha. Where each character is on the page, and how to place it, comes from
/// <see cref="FontTag"/> (<see cref="FontGlyph.PageX"/>, <see cref="FontGlyph.PageY"/>, sizes and origins).
/// </summary>
/// <remarks>
/// No mips: Halo draws text at its pixel size, and glyphs are only one pixel apart.
/// </remarks>
sealed class FontTextureLoader( string mapPath, uint tagId ) : HaloCeLoader
{
	protected override object Load()
	{
		var cache = Host.GetCache( mapPath );
		var font = new FontTag( cache, cache.GetTag( tagId ) );
		var (width, height) = font.Page;

		return Texture.Create( width, height, ImageFormat.RGBA8888 )
			.WithName( Path )
			.WithData( font.ReadPage( cache ) )
			.WithStaticUsage()
			.Finish();
	}
}
