using System.Runtime.CompilerServices;
using Blam.Halo2;
using Blam.Halo2.Tags;
using Halo1Format = Blam.Halo1.Tags.BitmapFormat;
using NVector4 = System.Numerics.Vector4;

namespace Sandbox.Mounting.Blam.Halo2;

/// <summary>
/// Builds s&amp;box materials for Halo 2's shield shells (halo2_shield.shader): the shaders a unit's model tag names
/// in its damage info for shield damage (transparent\plasma_shield, plasma_shield_change_color) and the overshield
/// (transparent\overshield). Halo draws them over the whole model; a game does the same with a second renderer of
/// the model (bone merged onto the first) using this as its material override, and sets the object's functions on
/// that renderer's attributes: <c>HaloShieldHit</c>, <c>HaloShieldGlowSource</c>, <c>HaloOvershieldAmount</c>
/// (0-1), <c>HaloCamo</c> (fades the overshield), <c>HaloChangeColor0</c>. See halo2_shield.shader.
/// </summary>
/// <remarks>
/// Nearly every parameter of these shaders follows one of those functions through the shader's animation (colour
/// transitions, the shell offset, the noise translation). The material carries each as a curve over its function
/// (<see cref="ShieldCurves"/>), one row per value in a small float texture. Time-driven colours and noise
/// translations become time curves the shader evaluates.
/// </remarks>
static class ShieldMaterial
{
	public const string ShaderPath = "shaders/halo2_shield.shader";

	/// <summary>
	/// The renderer attributes a game sets the shield functions with, in <see cref="ShieldCurves.Functions"/>'s order
	/// (shield_hit, shield_glow_source, object_overshield_amount).
	/// </summary>
	public static readonly string[] Attributes = ["HaloShieldHit", "HaloShieldGlowSource", "HaloOvershieldAmount"];

	public static bool IsSupported( ShaderTag shader ) => ShieldCurves.IsShield( shader );

	/// <param name="named">Findable by name (not anonymous): see <see cref="ModelMaterials.Create"/>.</param>
	public static Material Create( string name, ShaderTag shader, bool named = false )
	{
		if ( !ShieldCurves.IsShield( shader ) )
			return null;

		var material = Material.Create( name, ShaderPath, anonymous: !named );
		if ( material is null )
			return null;

		var curves = new ShieldCurves( shader, ShaderInputs.ForObject( shader.Tag, null ) );
		var pass = curves.Pass;
		const int rows = (int)ShieldCurves.Row.Count, width = ShieldCurves.Width;

		var data = new float[rows * width * 4];
		for ( var r = 0; r < rows; r++ )
		for ( var k = 0; k < width; k++ )
		{
			var v = curves.Values[r, k];
			var i = (r * width + k) * 4;
			(data[i], data[i + 1], data[i + 2], data[i + 3]) = (v.X, v.Y, v.Z, v.W);
		}

		var texture = Texture.Create( width, rows, ImageFormat.RGBA32323232F )
			.WithName( $"{System.IO.Path.ChangeExtension( name, null )}_curves" )
			.WithData( new ReadOnlySpan<float>( data ) )
			.WithStaticUsage()
			.Finish();

		material.Set( "g_tShieldCurves", texture );
		material.Set( "g_flShieldCurveWidth", (float)width );
		float Driver( int r ) => r < rows ? curves.Drivers[r] : -1;
		material.Set( "g_vShieldDrivers0", new Vector4( Driver( 0 ), Driver( 1 ), Driver( 2 ), Driver( 3 ) ) );
		material.Set( "g_vShieldDrivers1", new Vector4( Driver( 4 ), Driver( 5 ), Driver( 6 ), Driver( 7 ) ) );
		material.Set( "g_vShieldDrivers2", new Vector4( Driver( 8 ), Driver( 9 ), Driver( 10 ), Driver( 11 ) ) );
		material.Set( "g_flShieldWorldScale", Halo2Mount.WorldScale );

		for ( var n = 0; n < 2; n++ )
		{
			var atlas = NoiseAtlas( shader, pass, n );
			material.Set( $"g_tNoise{n}", atlas?.Texture ?? Halo1.MaterialTextures.Grey );
			material.Set( $"g_vNoise{n}Size", atlas?.Size ?? Vector4.One );
			for ( var axis = 0; axis < 3; axis++ )
			{
				var curve = curves.RestNoise.TranslationCurves[n, axis];
				material.Set( $"g_vNoise{n}{"XYZ"[axis]}A", (Vector4)(curve?.A ?? default) );
				material.Set( $"g_vNoise{n}{"XYZ"[axis]}B", (Vector4)(curve?.B ?? default) );
			}
		}

		var animated = curves.Rest.AnimatedConstants;
		for ( var i = 0; i < ShaderPassParameters.MaxAnimatedConstants; i++ )
		{
			var constant = i < animated.Count ? animated[i] : null;
			material.Set( $"g_vAnimConst{i}A", (Vector4)(constant?.Curve.A ?? new NVector4( 0, 0, 0, 1 )) );
			material.Set( $"g_vAnimConst{i}B", (Vector4)(constant?.Curve.B ?? default) );
			material.Set( $"g_vAnimConst{i}Min", (Vector4)(constant?.Minimum ?? default) );
			material.Set( $"g_vAnimConst{i}Max", (Vector4)(constant?.Maximum ?? default) );
			material.Set( $"g_vAnimConst{i}Target", constant is null ? Vector3.Zero : new Vector3( constant.Register, constant.Component, 1 ) );
		}

		material.Set( "g_flProgram", (float)curves.Kind );
		material.SetFeature( "F_TWO_SIDED", pass.IsTwoSided ? 1 : 0 );
		return material;
	}

	/// <summary>
	/// Each 3D noise bitmap's atlas and size, per map.
	/// </summary>
	static readonly ConditionalWeakTable<CacheFile, Dictionary<uint, (Texture Texture, Vector4 Size)>> _noise = new();

	/// <summary>
	/// The 3D noise map at a stage of the pass as a 2D atlas of its top level's slices (slice k at tile (k mod tiles
	/// across, k / tiles across)), and (width, height, depth, tiles across); null without one. Volume textures made at
	/// runtime may not bind to runtime materials (as for halo1_plasma.shader), so the shader samples it by hand.
	/// </summary>
	static (Texture Texture, Vector4 Size)? NoiseAtlas( ShaderTag shader, ShaderTemplatePass pass, int stage )
	{
		var slot = pass.TextureAtStage( stage )?.BitmapSlot ?? -1;
		var tag = shader.GetBitmap( slot )?.Bitmap;
		if ( tag is null || tag.Class != BitmapTag.Class )
			return null;

		var byBitmap = _noise.GetOrCreateValue( tag.Cache );
		lock ( byBitmap )
		{
			if ( byBitmap.TryGetValue( tag.Id, out var cached ) )
				return cached;

			var bitmap = new BitmapTag( tag ).Bitmaps.FirstOrDefault();
			if ( bitmap is null || bitmap.Type != BitmapType.Texture3D || bitmap.Depth < 1 || (ushort)bitmap.Format > (ushort)Halo1Format.A8R8G8B8
				|| !global::Blam.Halo1.BitmapDecoder.CanDecode( (Halo1Format)bitmap.Format ) )
				return null;

			var pixels = Halo2Mount.Current.Textures.Read( bitmap.PixelDataOffset, bitmap.PixelDataSize );
			var (width, height, depth) = (bitmap.Width, bitmap.Height, bitmap.Depth);
			var rgba = new byte[width * height * depth * 4];
			global::Blam.Halo1.BitmapDecoder.ToRgba8888( (Halo1Format)bitmap.Format, pixels.AsSpan( 0, bitmap.GetLevelSize( 0 ) ), rgba, width * height * depth );

			var tilesX = (int)Math.Ceiling( Math.Sqrt( depth ) );
			var tilesY = (depth + tilesX - 1) / tilesX;
			var atlasWidth = width * tilesX;
			var atlas = new byte[atlasWidth * height * tilesY * 4];
			for ( var z = 0; z < depth; z++ )
			for ( var y = 0; y < height; y++ )
			{
				var row = ((z / tilesX) * height + y) * atlasWidth + (z % tilesX) * width;
				Array.Copy( rgba, (z * height + y) * width * 4, atlas, row * 4, width * 4 );
			}

			var texture = Texture.Create( atlasWidth, height * tilesY, ImageFormat.RGBA8888 )
				.WithName( $"{tag.Path}_slices" )
				.WithData( atlas )
				.WithStaticUsage()
				.Finish();

			var result = (texture, new Vector4( width, height, depth, tilesX ));
			byBitmap[tag.Id] = result;
			return result;
		}
	}
}
