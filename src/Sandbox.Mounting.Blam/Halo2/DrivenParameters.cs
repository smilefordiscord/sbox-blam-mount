using Blam.Halo2;
using Blam.Halo2.Tags;
using Num = System.Numerics;

namespace Sandbox.Mounting.Blam.Halo2;

// Material parameters that follow a model's object functions while a game plays (ShaderFunctions).
// The game sets each function on the object's renderers as HaloFunction<slot>, 0-1; below 0 or unset, the parameter keeps its value at rest.
// The shader looks the parameter up in a table of its values over the function (Samples of them, 0 to 1), made by evaluating the shader's animation with that function there and the others at rest.
// A material has up to MaxEntries: halo2_opaque.shader's texture transforms and self-illumination constants by role, the transparent and plasma shaders' registers and texture coordinate offsets.
static class DrivenParameters
{
	public const int MaxEntries = 4;
	public const int Samples = 16;

	// Publishes a model's function slots as <model>__functions.json: {"Functions": [name of slot 0, ...]}.
	public static void RegisterSlots( MountContext context, string model, IReadOnlyList<string> slots ) =>
		context.Add( ResourceType.Text, $"{model}__functions.json", new SlotsLoader( System.Text.Json.JsonSerializer.Serialize( new { Functions = slots } ) ) );

	sealed class SlotsLoader( string json ) : Halo2Loader
	{
		protected override object Load() => json;
	}

	// The parameters that move with each of the model's functions: values gives every parameter's value for the shader animated with some inputs.
	public static List<(int Slot, TKey Key, Num.Vector4[] Table)> Find<TKey>( TagEntry shader, ShaderInputs rest, IReadOnlyList<string> slots,
		Func<ShaderInputs, IReadOnlyDictionary<TKey, Num.Vector4>> values )
	{
		var entries = new List<(int, TKey, Num.Vector4[])>();
		if ( slots is null || slots.Count == 0 || !rest.HasObject )
			return entries;

		var inputs = ShaderFunctions.Inputs( shader ).ToHashSet( StringComparer.OrdinalIgnoreCase );
		if ( inputs.Count == 0 )
			return entries;

		var atRest = values( rest );
		for ( var slot = 0; slot < slots.Count && entries.Count < MaxEntries; slot++ )
		{
			if ( !inputs.Contains( slots[slot] ) )
				continue;

			var tables = new Dictionary<TKey, Num.Vector4[]>();
			for ( var s = 0; s < Samples; s++ )
			{
				var sample = values( rest.With( new Dictionary<string, float> { [slots[slot]] = s / (float)(Samples - 1) } ) );
				foreach ( var (key, value) in sample )
				{
					if ( !tables.TryGetValue( key, out var table ) )
						tables[key] = table = new Num.Vector4[Samples];
					table[s] = value;
				}
			}

			foreach ( var (key, table) in tables )
			{
				if ( entries.Count < MaxEntries && atRest.TryGetValue( key, out var value ) && table.Any( v => Num.Vector4.DistanceSquared( v, value ) > 1e-10f ) )
					entries.Add( (slot, key, table) );
			}
		}

		return entries;
	}

	// Hands a material its entries: g_vDriven<i> = (slot, kind, target, 0), -1 for none, g_vDrivenFrame<i> a frame entry's image count and image at rest, and their tables as the rows of g_tDrivenTable.
	public static void Apply( Material material, IReadOnlyList<(int Slot, int Kind, int Target, Num.Vector4[] Table, Num.Vector4 Frame)> entries )
	{
		for ( var i = 0; i < MaxEntries; i++ )
		{
			material.Set( $"g_vDriven{i}", i < entries.Count ? new Vector4( entries[i].Slot, entries[i].Kind, entries[i].Target, 0 ) : new Vector4( -1, 0, 0, 0 ) );
			material.Set( $"g_vDrivenFrame{i}", i < entries.Count ? (Vector4)entries[i].Frame : new Vector4( 1, 0, 0, 0 ) );
		}

		if ( entries.Count == 0 )
			return;

		var data = new float[Samples * MaxEntries * 4];
		for ( var i = 0; i < entries.Count && i < MaxEntries; i++ )
		{
			for ( var s = 0; s < Samples; s++ )
			{
				var v = entries[i].Table[s];
				var at = (i * Samples + s) * 4;
				(data[at], data[at + 1], data[at + 2], data[at + 3]) = (v.X, v.Y, v.Z, v.W);
			}
		}

		material.Set( "g_tDrivenTable", Texture.Create( Samples, MaxEntries, ImageFormat.RGBA32323232F )
			.WithData( new ReadOnlySpan<float>( data ) )
			.Finish() );
	}

	// A frame entry's table: the curve's value, before Halo floors it and wraps it to the images, over the function.
	public static Num.Vector4[] FrameTable( ShaderOverlay frame, ShaderInputs rest )
	{
		var range = rest.Value( frame.Range );
		var table = new Num.Vector4[Samples];
		for ( var s = 0; s < Samples; s++ )
			table[s] = new Num.Vector4( frame.Function.Evaluate( s / (float)(Samples - 1), range ), 0, 0, 0 );
		return table;
	}

	// A bitmap's images stacked top to bottom as one texture, for a frame the shader picks, or null unless they're all 2D and the same size.
	// Images are power of two tall, so the mips stop at one pixel per image and never mix two.
	public static Texture FrameAtlas( TagEntry bitmap )
	{
		if ( bitmap is null || bitmap.Class != BitmapTag.Class )
			return null;

		var images = new BitmapTag( bitmap ).Bitmaps;
		if ( images.Count < 2 || images.Any( b => !BitmapTextureLoader.IsSupported( b ) || b.Type != BitmapType.Texture2D || b.Width != images[0].Width || b.Height != images[0].Height ) )
			return null;

		int width = images[0].Width, height = images[0].Height;
		var atlas = new byte[width * height * 4 * images.Count];
		for ( var i = 0; i < images.Count; i++ )
		{
			var rgba = new BitmapTextureLoader( images[i] ).TopLevelRgba( Halo2Mount.Current.Textures.Read( images[i].PixelDataOffset, images[i].PixelDataSize ) );
			rgba.AsSpan( 0, width * height * 4 ).CopyTo( atlas.AsSpan( i * width * height * 4 ) );
		}

		var levels = global::Blam.Halo1.SpriteCrop.MipChain( atlas, width, height * images.Count );
		levels = levels.Take( Math.Min( levels.Count, Num.BitOperations.Log2( (uint)Math.Min( width, height ) ) + 1 ) ).ToList();

		// s&box wants the smallest level first.
		var output = new byte[levels.Sum( l => l.Length )];
		var at = 0;
		for ( var level = levels.Count - 1; level >= 0; level-- )
		{
			levels[level].CopyTo( output, at );
			at += levels[level].Length;
		}

		return Texture.Create( width, height * images.Count, ImageFormat.RGBA8888 )
			.WithName( $"{Halo2Mount.ResourcePath( bitmap.Path )}_frames" )
			.WithData( output )
			.WithMips( levels.Count )
			.WithStaticUsage()
			.Finish();
	}
}
