using Blam.Halo1;
using Blam.Halo1.Tags;

namespace Sandbox.Mounting.Blam.Halo1;

/// <summary>
/// One sprite of a sprite page as its own texture, cropped to its rectangle when it's first loaded.
/// Registered as <c>&lt;tag path&gt;/&lt;sequence&gt;_&lt;sprite&gt;</c> (see <see cref="HaloCeMount.FrameTextureName"/>).
/// </summary>
/// <remarks>
/// Decoded to RGBA8888, so DXT pages don't need sprites on 4x4 block boundaries. Pages with mips get
/// a mip chain made from the crop (particles and decals are drawn small); HUD pages have none.
/// </remarks>
sealed class SpriteTextureLoader( string mapPath, BitmapData page, (int X, int Y, int Width, int Height) rect ) : HaloCeLoader
{
	protected override object Load()
	{
		var pixels = BitmapTextureLoader.ReadPixels( Host, mapPath, page );
		var crop = SpriteCrop.Crop( page, pixels, rect );
		var levels = page.MipmapCount > 0 ? SpriteCrop.MipChain( crop, rect.Width, rect.Height ) : [crop];

		// s&box wants the smallest level first.
		var output = new byte[levels.Sum( l => l.Length )];
		var destination = 0;
		for ( var level = levels.Count - 1; level >= 0; level-- )
		{
			levels[level].CopyTo( output, destination );
			destination += levels[level].Length;
		}

		return Texture.Create( rect.Width, rect.Height, ImageFormat.RGBA8888 )
			.WithName( Path )
			.WithData( output )
			.WithMips( levels.Count )
			.WithStaticUsage()
			.Finish();
	}
}

/// <summary>
/// A sequence frame of a 2D or interface bitmap tag, under the same <c>&lt;tag path&gt;/&lt;sequence&gt;_&lt;frame&gt;</c>
/// name as sprites, so a game finds every HUD and menu image the same way. It loads the frame's
/// whole bitmap texture and returns that same texture, so nothing is decoded twice.
/// </summary>
sealed class FrameTextureLoader( ResourceLoader bitmap ) : HaloCeLoader
{
	// Bitmap loaders are synchronous, so this normally completes inline. Waiting here rather than
	// awaiting keeps the engine from holding this loader's lock across a thread switch.
	protected override object Load() => bitmap.GetOrCreate().GetAwaiter().GetResult();
}
