using Blam.Halo1;
using Blam.Halo1.Tags;

namespace Sandbox.Mounting.Blam.Halo1;

/// <summary>
/// Texture loading and fallbacks shared by the material builders.
/// </summary>
/// <remarks>
/// Materials created at runtime bind an error texture to any input that isn't set, rather than
/// the shader's declared default, so every input gets one of these when a tag has no map.
/// </remarks>
static class MaterialTextures
{
	static readonly Lazy<Texture> _white = new( () => Solid( 255, 255, 255, 255 ) );
	static readonly Lazy<Texture> _grey = new( () => Solid( 128, 128, 128, 255 ) );
	static readonly Lazy<Texture> _flatNormal = new( () => Solid( 128, 128, 255, 255 ) );
	static readonly Lazy<Texture> _transparent = new( () => Solid( 0, 0, 0, 0 ) );

	public static Texture White => _white.Value;
	public static Texture Grey => _grey.Value;
	public static Texture FlatNormal => _flatNormal.Value;
	public static Texture Transparent => _transparent.Value;

	[ThreadStatic] static CacheFile _privateCopiesFrom;

	/// <summary>
	/// Until disposed, <see cref="Load"/> on this thread makes its own copy of each texture from
	/// <paramref name="cache"/> instead of loading the shared one. Shared textures are managed by the
	/// resource system, which can hand them out before they've loaded and streams their mip levels by
	/// use, so something rendered once, right away (the sky, see <see cref="SkyMaterialLoader"/>), could
	/// get them half there. A copy is all there as soon as it's made.
	/// </summary>
	public static IDisposable PrivateCopies( CacheFile cache )
	{
		var previous = _privateCopiesFrom;
		_privateCopiesFrom = cache;
		return new Restore( () => _privateCopiesFrom = previous );
	}

	sealed class Restore( Action restore ) : IDisposable
	{
		public void Dispose() => restore();
	}

	/// <summary>
	/// Loads one bitmap of a bitmap tag. Returns null if there's no tag, and logs a warning if
	/// there is one but it fails to load.
	/// </summary>
	public static Texture Load( HaloCeMount host, Material material, TagEntry bitmap, int index )
	{
		if ( bitmap is null || string.IsNullOrEmpty( bitmap.Path ) )
			return null;

		var path = HaloCeMount.TexturePath( host.Ident, bitmap.Path, index );
		var texture = _privateCopiesFrom is { } cache ? CreateCopy( host, cache, bitmap, index, path ) : Texture.Load( path );
		if ( texture is not null && texture.IsValid() && !texture.IsError )
			return texture;

		host.Logger.Warning( $"{material.Name}: couldn't load {path}" );
		return null;
	}

	/// <summary>
	/// Loads the image a model part's shader permutation picks from a bitmap tag: Halo draws image
	/// number permutation, modulo the tag's image count, of every map of a model's shader.
	/// </summary>
	public static Texture LoadPermutation( HaloCeMount host, Material material, CacheFile cache, TagEntry bitmap, int permutation )
	{
		if ( permutation <= 0 || bitmap is null )
			return Load( host, material, bitmap, 0 );

		var count = new BitmapTag( cache, bitmap ).Bitmaps.Count;
		return Load( host, material, bitmap, count > 0 ? permutation % count : 0 );
	}

	static Texture CreateCopy( HaloCeMount host, CacheFile cache, TagEntry bitmap, int index, string name )
	{
		var bitmaps = new BitmapTag( cache, bitmap ).Bitmaps;
		var data = bitmaps.FirstOrDefault( b => b.Index == index );
		return data is not null && BitmapTextureLoader.IsSupported( data )
			? BitmapTextureLoader.Create( host, cache.FilePath, data, name )
			: null;
	}

	static Texture Solid( byte r, byte g, byte b, byte a ) =>
		Texture.Create( 1, 1 ).WithData( new[] { r, g, b, a } ).WithStaticUsage().Finish();
}
