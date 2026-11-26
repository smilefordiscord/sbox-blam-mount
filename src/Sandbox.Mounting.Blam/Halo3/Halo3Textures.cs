using Blam.Halo3;
using Blam.Halo3.Tags;

namespace Sandbox.Mounting.Blam.Halo3;

// Registers every bitmap of a map's bitmap tags as mount://halo3/<tag path>[_<index>].vtex (Halo3Mount.TextureName).
// Cube maps are a six face atlas, arrays texture arrays; BitmapTextureLoader.Create still makes a real cube or one array layer on request.
static class Halo3Textures
{
	// Reads tag data only (bitm, zone and play tags), never a page: mounting runs this over every map.
	public static void Register( MountContext context, CacheFile cache, HashSet<string> registered )
	{
		var availability = BitmapAvailability.For( cache );
		foreach ( var tag in cache.TagsOf( "bitm" ) )
		{
			if ( string.IsNullOrEmpty( tag.Path ) || tag.Address == 0 )
				continue;

			foreach ( var image in new BitmapTag( tag ).Images )
			{
				if ( image.Width <= 0 || image.Height <= 0 )
					continue;

				// Images whose pixels this map doesn't have are left for a later map that does.
				var state = availability.Check( image, out var interop );
				if ( state is not (BitmapState.Available or BitmapState.MissingHighRes) || !BitmapUpload.IsSupported( interop.Type, interop.Format ) )
					continue;

				var name = Halo3Mount.TextureName( tag.Path, image.Index );
				if ( !registered.Add( $"{name}.vtex" ) )
					continue;

				context.Add( ResourceType.Texture, name, new BitmapTextureLoader( cache.FilePath, tag.Id, image.Index ) );
			}
		}
	}

	// The registered texture of one image of a bitmap tag; an error texture if no map has its pixels.
	public static Texture Load( TagEntry bitmapTag, int index = 0 ) => Texture.Load( Halo3Mount.TexturePath( bitmapTag.Path, index ) );

	// Whether MCC samples the image through an sRGB view (s&box leaves that to the shader: SrgbRead, or decode in the shader).
	public static bool IsSrgb( TagEntry bitmapTag, int index = 0 )
	{
		var images = new BitmapTag( bitmapTag ).Images;
		return index >= 0 && index < images.Count && images[index].IsSrgb;
	}

	// Decodes one image straight from its cache file, outside the resource system (e.g. a map's own copy of a tag).
	public static Texture Create( TagEntry bitmapTag, int index = 0, string name = null ) =>
		BitmapTextureLoader.Create( BitmapPixels.Read( new BitmapTag( bitmapTag ).Images[index] ), BitmapTextureKind.Default, 0, name ?? Halo3Mount.TextureName( bitmapTag.Path, index ) );
}
