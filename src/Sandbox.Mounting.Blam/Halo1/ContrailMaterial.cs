using Blam.Halo1;
using Blam.Halo1.Tags;

namespace Sandbox.Mounting.Blam.Halo1;

/// <summary>
/// A contrail's look for s&amp;box's <see cref="TrailRenderer"/>: its bitmap, blend function and
/// nonlinear tint in halo1_contrail.shader, registered as
/// <c>mount://haloce/&lt;contrail tag&gt;__contrail.vmat</c> so prefabs can refer to it (a trail
/// renderer only takes a texture through a material).
/// </summary>
/// <remarks>
/// Halo picks a random sequence between the first sequence and the sequence count for each contrail
/// and steps through its frames at the animation rate; this uses the first frame of the first
/// sequence.
/// </remarks>
static class ContrailMaterial
{
	public const string ShaderPath = "shaders/halo1_contrail.shader";

	public static string ResourceName( string tagPath ) => $"{HaloCeMount.ResourcePath( tagPath )}__contrail";

	public static string FullPath( string tagPath ) => $"mount://{EffectPrefabs.Ident}/{ResourceName( tagPath )}.vmat";

	public static Material Create( HaloCeMount host, string name, CacheFile cache, TagEntry tag )
	{
		var contrail = new ContrailTag( cache, tag );
		var material = Material.Create( name, ShaderPath );
		if ( material is null )
			return null;

		material.SetFeature( "F_BLEND_MODE", (int)contrail.BlendFunction );
		material.Set( "g_flNonlinearTint", contrail.NonlinearTint ? 1f : 0f );
		material.Set( "g_tContrail", Texture( host, material, cache, contrail ) ?? MaterialTextures.White );
		material.Set( "g_flContrailRepeatsV", contrail.TextureRepeats.Y == 0 ? 1f : contrail.TextureRepeats.Y );
		return material;
	}

	static Texture Texture( HaloCeMount host, Material material, CacheFile cache, ContrailTag contrail )
	{
		if ( contrail.Bitmap is null )
			return null;

		var bitmap = new BitmapTag( cache, contrail.Bitmap );
		var sequence = Math.Max( contrail.FirstSequence, 0 );
		if ( sequence < bitmap.Sequences.Count && bitmap.Sequences[sequence].Sprites.Count > 0 )
		{
			// A sprite sheet: the sprite cut out on its own.
			var path = $"mount://{host.Ident}/{HaloCeMount.FrameTextureName( contrail.Bitmap.Path, sequence, 0 )}.vtex";
			var sprite = Sandbox.Texture.Load( path );
			if ( sprite is not null && sprite.IsValid() && !sprite.IsError )
				return sprite;
		}

		var index = sequence < bitmap.Sequences.Count && bitmap.Sequences[sequence].BitmapCount > 0 ? bitmap.Sequences[sequence].FirstBitmapIndex : 0;
		return MaterialTextures.Load( host, material, contrail.Bitmap, Math.Clamp( index, 0, Math.Max( bitmap.Bitmaps.Count - 1, 0 ) ) );
	}
}

sealed class ContrailMaterialLoader( string mapPath, uint tagId ) : HaloCeLoader
{
	protected override object Load()
	{
		var cache = Host.GetCache( mapPath );
		var tag = cache.GetTag( tagId );
		return tag is null ? null : ContrailMaterial.Create( Host, Path, cache, tag );
	}
}
