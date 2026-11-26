using Blam.Halo3;
using Blam.Halo3.Tags;

namespace Sandbox.Mounting.Blam.Halo3;

enum BitmapTextureKind
{
	// 2D and 3D textures as they are, arrays as texture arrays, cube maps as a six face atlas.
	Default,

	// A cube map as a real s&box cube texture.
	Cube,

	// One layer of an array as a 2D texture.
	Slice,
}

// Turns one image of a Halo 3 bitmap tag into a texture, from its interop resource's pages (see BitmapUpload for the formats).
sealed class BitmapTextureLoader( string mapPath, uint tagId, int index, BitmapTextureKind kind = BitmapTextureKind.Default, int slice = 0 ) : Halo3Loader
{
	protected override object Load()
	{
		try
		{
			var pixels = Host.Read( () =>
			{
				var cache = Host.GetCache( mapPath );
				var tag = cache.GetTag( tagId ) ?? throw new InvalidDataException( $"No tag 0x{tagId:X8} in {cache.Name}" );
				return BitmapPixels.Read( new BitmapTag( tag ).Images[index] );
			} );
			return Create( pixels, kind, slice, Path );
		}
		catch ( Exception e )
		{
			Host.Logger.Warning( $"Couldn't load {Path}: {e.Message}" );
			return null;
		}
	}

	public static Texture Create( BitmapPixels pixels, BitmapTextureKind kind, int slice, string name )
	{
		var upload = (pixels.Image.Type, kind) switch
		{
			(BitmapType.CubeMap, BitmapTextureKind.Cube) => BitmapUpload.Cube( pixels ),
			(BitmapType.CubeMap, _) => BitmapUpload.CubeAtlas( pixels ),
			(BitmapType.Array, BitmapTextureKind.Slice) => BitmapUpload.Texture2D( pixels, slice ),
			(BitmapType.Array, _) => BitmapUpload.Array( pixels ),
			(BitmapType.Texture3D, _) => BitmapUpload.Volume( pixels ),
			_ => BitmapUpload.Texture2D( pixels ),
		};
		return Create( upload, name );
	}

	static Texture Create( BitmapUploadData upload, string name )
	{
		var format = upload.Format switch
		{
			UploadFormat.Dxt1 => ImageFormat.DXT1,
			UploadFormat.Dxt5 => ImageFormat.DXT5,
			UploadFormat.Ati1n => ImageFormat.ATI1N,
			UploadFormat.Ati2n => ImageFormat.ATI2N,
			UploadFormat.Rgba16F => ImageFormat.RGBA16161616F,
			_ => ImageFormat.RGBA8888,
		};

		return upload.Shape switch
		{
			UploadShape.Array => Texture.CreateArray( upload.Width, upload.Height, upload.Layers, format )
				.WithName( name ).WithData( upload.Data ).WithMips( upload.LevelCount ).WithStaticUsage().Finish(),
			UploadShape.Cube => Texture.CreateCube( upload.Width, upload.Height, format )
				.WithName( name ).WithData( upload.Data ).WithMips( upload.LevelCount ).WithStaticUsage().Finish(),
			UploadShape.Volume => Texture.CreateVolume( upload.Width, upload.Height, upload.Layers, format )
				.WithName( name ).WithData( upload.Data ).WithMips( upload.LevelCount ).WithStaticUsage().Finish(),
			_ => Texture.Create( upload.Width, upload.Height, format )
				.WithName( name ).WithData( upload.Data ).WithMips( upload.LevelCount ).WithStaticUsage().Finish(),
		};
	}
}
