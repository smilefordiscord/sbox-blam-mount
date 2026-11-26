using Blam.Halo2;
using Blam.Halo2.Tags;

namespace Sandbox.Mounting.Blam.Halo2;

// A Halo 2 contrail's look in halo1_contrail.shader, mount://halo2/<contrail tag>__contrail.vmat, as Halo CE's.
// Halo 2 kept CE's contrail and its framebuffer blend functions.
// The bitmap is the first sequence's first bitmap: every multiplayer contrail's bitmap is one plain texture.
// Sequence animation and the second map aren't drawn.
// Nor are Halo 2's constant color blends and "none", which no multiplayer contrail uses.
static class ContrailMaterials
{
	public static string ResourceName( string tagPath ) => $"{Halo2Mount.ResourcePath( tagPath )}__contrail";

	public static string FullPath( string tagPath ) => $"mount://{Halo2Effects.Ident}/{ResourceName( tagPath )}.vmat";
}

sealed class ContrailMaterialLoader( string mapPath, uint tagId ) : Halo2Loader
{
	protected override object Load()
	{
		var tag = Host.GetCache( mapPath ).GetTag( tagId );
		if ( !ContrailTag.IsContrail( tag ) )
			return null;

		var contrail = new ContrailTag( tag );
		if ( !contrail.HasClassicBlend )
			return null;

		var material = Material.Create( Path, Halo1.ContrailMaterial.ShaderPath );
		if ( material is null )
			return null;

		material.SetFeature( "F_BLEND_MODE", contrail.BlendFunction );
		material.Set( "g_flNonlinearTint", contrail.NonlinearTint ? 1f : 0f );
		material.Set( "g_tContrail", Map( contrail ) ?? Texture.White );
		material.Set( "g_flContrailRepeatsV", contrail.TextureRepeats.Y == 0 ? 1f : contrail.TextureRepeats.Y );
		return material;
	}

	static Texture Map( ContrailTag contrail )
	{
		if ( contrail.Bitmap is not { Address: not 0 } bitmapTag )
			return null;

		var bitmap = new BitmapTag( bitmapTag );
		var sequence = Math.Max( contrail.FirstSequence, 0 );
		var index = sequence < bitmap.Sequences.Count ? bitmap.Sequences[sequence].FirstBitmapIndex : 0;
		index = Math.Clamp( index, 0, Math.Max( bitmap.Bitmaps.Count - 1, 0 ) );
		return Texture.Load( $"mount://{Halo2Effects.Ident}/{Halo2Mount.TextureName( bitmapTag.Path, index )}.vtex" ) is { IsError: false } map ? map : null;
	}
}
