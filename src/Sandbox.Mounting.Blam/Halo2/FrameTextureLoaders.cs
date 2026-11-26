using Blam.Halo2.Tags;
using SpriteCrop = Blam.Halo1.SpriteCrop;

namespace Sandbox.Mounting.Blam.Halo2;

// One sprite of a sprite page as its own texture, cropped to its rectangle when it's first loaded, as in the Halo CE mount.
// Pages with mips get a mip chain made from the crop; HUD pages have none.
sealed class SpriteFrameLoader( BitmapData page, (int X, int Y, int Width, int Height) rect ) : Halo2Loader
{
	public static bool CanCrop( BitmapData page ) => page.Type == BitmapType.Texture2D && BitmapTextureLoader.IsSupported( page );

	protected override object Load()
	{
		var rgba = new BitmapTextureLoader( page ).TopLevelRgba( Host.Textures.Read( page.PixelDataOffset, page.PixelDataSize ) );
		var crop = new byte[rect.Width * rect.Height * 4];
		for ( var y = 0; y < rect.Height; y++ )
			Array.Copy( rgba, ((rect.Y + y) * page.Width + rect.X) * 4, crop, y * rect.Width * 4, rect.Width * 4 );

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

// A frame of a sequence of whole bitmaps: the frame's bitmap texture under the frame's name.
// It returns the bitmap's own texture, so nothing is decoded twice.
sealed class FrameTextureLoader( ResourceLoader bitmap ) : Halo2Loader
{
	// Bitmap loaders are synchronous, so this normally completes inline.
	// Waiting here rather than awaiting keeps the engine from holding this loader's lock across a thread switch.
	protected override object Load() => bitmap.GetOrCreate().GetAwaiter().GetResult();
}
