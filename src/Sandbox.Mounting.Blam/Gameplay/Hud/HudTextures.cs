using System;

namespace Halo.UI;

// HUD bitmaps from the mount: frames as <tag>/<sequence>_<frame>.vtex and pages as <tag>.vtex or <tag>_<n>.vtex.
// Every lookup is cached, misses included, so a missing texture costs one failed load.
public static class HudTextures {
	static readonly Dictionary<string, Texture> _textures = new();
	// Keyed by texture, since a mount texture's ResourcePath isn't always unique or set.
	static readonly Dictionary<Texture, HudPixels> _pixels = new();

	// Off by default, using the tags' own hi-res combined bitmaps.
	[ConVar( "halo_hud_remap", Help = "Draw the Halo HUD with the hud globals' anniversary remap (\"_r\") bitmaps instead of the ones the HUD tags reference.", Saved = true )]
	public static bool UseRemaps { get; set; } = false;

	// Applies the remap ConVar.
	public static string Resolve( HaloHudData data, string bitmapTag ) {
		if ( string.IsNullOrEmpty( bitmapTag ) ) return null;
		if ( UseRemaps && data?.Remaps is not null && data.Remaps.TryGetValue( bitmapTag, out var remapped ) && data.Bitmap( remapped ) is not null )
			return remapped;
		return bitmapTag;
	}

	static string Stem( string tagPath ) {
		var path = tagPath.Replace( '\\', '/' );
		var dot = path.LastIndexOf( '.' );
		if ( dot > path.LastIndexOf( '/' ) )
			path = path[..dot];
		return $"mount://haloce/{path}";
	}

	// frame must already be wrapped to the sequence's frame count.
	public static Texture Frame( string bitmapTag, int sequence, int frame ) {
		if ( string.IsNullOrEmpty( bitmapTag ) || sequence < 0 || frame < 0 ) return null;
		return Load( $"{Stem( bitmapTag )}/{sequence}_{frame}.vtex" );
	}

	// <tag>_<n>.vtex, or <tag>.vtex for page 0.
	public static Texture Page( string bitmapTag, int page ) {
		if ( string.IsNullOrEmpty( bitmapTag ) || page < 0 ) return null;
		var stem = Stem( bitmapTag );
		if ( page == 0 )
			return Load( $"{stem}.vtex" ) ?? Load( $"{stem}_0.vtex" );
		return Load( $"{stem}_{page}.vtex" );
	}

	static Texture Load( string path ) {
		if ( _textures.TryGetValue( path, out var cached ) ) return cached;

		Texture texture = null;
		try {
			texture = Texture.Load( path, false );
			if ( texture is null || texture.IsError || !texture.IsValid || texture.Width <= 0 || texture.Height <= 0 )
				texture = null;
		}
		catch ( Exception ) {
			texture = null;
		}

		_textures[path] = texture;
		return texture;
	}

	// Read back once and cached; null when the texture is missing or unreadable.
	public static HudPixels Pixels( Texture texture ) {
		if ( texture is null ) return null;

		if ( _pixels.TryGetValue( texture, out var cached ) ) return cached;
		var key = Describe( texture );

		HudPixels result = null;
		try {
			var pixels = texture.GetPixels( 0 );
			if ( pixels is not null && pixels.Length >= texture.Width * texture.Height && pixels.Length > 0 )
				result = new HudPixels( texture.Width, texture.Height, pixels );
		}
		catch ( Exception e ) {
			Log.Warning( $"Halo HUD: can't read the pixels of {key} ({e.Message}); what needs them (meters, scope masks) falls back." );
		}

		_pixels[texture] = result;
		return result;
	}

	static readonly Dictionary<(Texture, string), Texture> _derived = new();

	// Re-uploaded as plain RGBA8888 so a shader reads Halo's bytes as they are, whatever the mount's format.
	public static Texture RawCopy( Texture source ) => Derived( source, "raw", p => p );

	// Adds a mip chain, since mount HUD bitmaps load with one and skip texel rows when drawn small.
	// Without it, thin details like team_background's top border vanish.
	public static Texture Mipped( Texture source ) => source is null || source.Mips > 1 ? source : Derived( source, "mipped", p => p ) ?? source;

	static Texture Derived( Texture source, string kind, Func<Color32, Color32> map ) {
		if ( source is null ) return null;

		if ( _derived.TryGetValue( (source, kind), out var cached ) ) return cached;
		var key = Describe( source );

		Texture result = null;
		var pixels = Pixels( source );
		if ( pixels is not null ) {
			try {
				// A full mip chain so copies drawn small filter.
				var data = new byte[pixels.Width * pixels.Height * 4];
				for ( var i = 0; i < pixels.Width * pixels.Height; i++ ) {
					var p = map( pixels.Data[i] );
					data[i * 4] = p.r;
					data[i * 4 + 1] = p.g;
					data[i * 4 + 2] = p.b;
					data[i * 4 + 3] = p.a;
				}

				result = Texture.Create( pixels.Width, pixels.Height, ImageFormat.RGBA8888 ).WithData( data ).WithMips().Finish();
			}
			catch ( Exception e ) {
				Log.Warning( $"Halo HUD: couldn't build the {kind} copy of {key} ({e.Message})" );
				result = null;
			}
		}

		_derived[(source, kind)] = result;
		return result;
	}

	// For log messages.
	public static string Describe( Texture texture ) =>
		texture is null ? "(none)" : $"{(string.IsNullOrEmpty( texture.ResourcePath ) ? "dynamic texture" : texture.ResourcePath)} {texture.Width}x{texture.Height}";
}

// A texture's pixels on the CPU. For A8Y8 HUD bitmaps the gray is in RGB and the mask in A.
public sealed class HudPixels {
	public readonly int Width;
	public readonly int Height;
	public readonly Color32[] Data;

	public HudPixels( int width, int height, Color32[] data ) {
		Width = width;
		Height = height;
		Data = data;
	}

	// Nearest sample at uv, or border outside 0..1.
	public Color32 Sample( float u, float v, Color32 border ) {
		if ( u < 0 || v < 0 || u >= 1 || v >= 1 ) return border;
		var x = Math.Clamp( (int)(u * Width), 0, Width - 1 );
		var y = Math.Clamp( (int)(v * Height), 0, Height - 1 );
		return Data[y * Width + x];
	}
}

// Halo's meter rule: gray below the lit threshold is the gradient, below the flash threshold flash, else empty.
// The HUD renderer applies it per texel on the GPU.
public static class HudMeter {
	// Thresholds are 0..255 bytes; color alpha is ignored, as in Halo's shader.
	public static HaloHudRenderer.QuadEffect Effect( int litBelow, int flashBelow, Color minColor, Color maxColor, Color flashColor, Color emptyColor ) {
		static Vector4 Bytes( Color c ) {
			var b = (Color32)c.WithAlpha( 1 );
			return new Vector4( b.r, b.g, b.b, 255 );
		}

		return new HaloHudRenderer.QuadEffect {
			Kind = HaloHudRenderer.QuadKind.Meter,
			A = new Vector4( litBelow, flashBelow, 0, 0 ),
			B = Bytes( minColor ),
			C = Bytes( maxColor ),
			D = Bytes( flashColor ),
			E = Bytes( emptyColor ),
		};
	}
}
