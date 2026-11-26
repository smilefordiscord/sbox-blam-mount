using Blam.Halo2;
using Blam.Halo2.Tags;

namespace Sandbox.Mounting.Blam.Halo2;

/// <summary>
/// Player emblems on armour (opaque\emblem_overlay_simple: the Master Chief's and the elite's shoulder and back), drawn
/// by halo2_transparent.shader's emblem program over the armour like Halo's overlay layer.
/// </summary>
/// <remarks>
/// <para>MCC animates the shader from the player: the bitmap's image by <c>emblem_foreground_index</c> (0-63),
/// c5.w by <c>emblem_toggle_foreground</c>, c6.w by <c>emblem_active</c>, the colours from the object's tertiary and
/// quaternary change colours (MCC's DXBC: the image's blue region in the quaternary colour; its red/green region, when
/// toggled on, in the tertiary colour and covering the rest). The tags store 0 for both constants, so without a player
/// nothing showed.</para>
///
/// <para>Here a game sets them on the model's renderer attributes: <c>HaloEmblemForeground</c> (image index 0-63,
/// default 0), <c>HaloEmblemToggle</c> (0 or 1, default 1), <c>HaloEmblemActive</c> (0 hides it, default 1) and
/// <c>HaloChangeColor2</c> / <c>HaloChangeColor3</c> (the tertiary and quaternary change colours, rgb 0-1 in Halo's
/// gamma space with alpha 1; the armour's tertiary and quaternary change colours use the same attributes). Without
/// them an object's stored change colours are used, else white and steel grey. The defaults (image 0, toggled on,
/// white) are a choice, not checked against a fresh MCC profile.</para>
///
/// <para>The 64 images go to the GPU as one 8 x 8 atlas (<see cref="BitmapTextureLoader.LoadFlipbook"/>), and the
/// shader picks the cell.</para>
/// </remarks>
static class Emblems
{
	const int Columns = 8;

	static readonly Dictionary<string, (Texture Texture, int Rows, int Count)> Atlases = new();
	static Halo2Mount _atlasMount;

	/// <summary>
	/// Gives an emblem material its atlas (stage 0, the shader's first bitmap with every image) and its layout.
	/// </summary>
	public static void Apply( Material material, ShaderTag shader )
	{
		if ( Atlas( shader.GetBitmap( 0 )?.Bitmap ) is not { Texture: { } texture } atlas )
			return;

		material.Set( "g_tMap0", texture );
		material.Set( "g_vEmblemGrid", new Vector4( Math.Min( Columns, atlas.Count ), atlas.Rows, atlas.Count, 0 ) );
	}

	static (Texture Texture, int Rows, int Count)? Atlas( TagEntry tag )
	{
		if ( tag is null || tag.Class != BitmapTag.Class )
			return null;

		lock ( Atlases )
		{
			if ( _atlasMount != Halo2Mount.Current )
			{
				Atlases.Clear();
				_atlasMount = Halo2Mount.Current;
			}

			if ( !Atlases.TryGetValue( tag.Path, out var atlas ) )
			{
				var frames = new BitmapTag( tag ).Bitmaps;
				var texture = frames.Count == 0 ? null : BitmapTextureLoader.LoadFlipbook( $"{Halo2Mount.ResourcePath( tag.Path )}_emblems", frames, Columns );
				Atlases[tag.Path] = atlas = (texture, (frames.Count + Columns - 1) / Columns, frames.Count);
			}

			return atlas;
		}
	}
}
