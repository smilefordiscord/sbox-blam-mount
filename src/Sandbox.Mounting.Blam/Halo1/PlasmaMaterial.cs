using System.Runtime.CompilerServices;
using Blam.Halo1;
using Blam.Halo1.Tags;

namespace Sandbox.Mounting.Blam.Halo1;

/// <summary>
/// Builds s&amp;box materials for Halo CE's plasma shaders (shader_transparent_plasma): the shield
/// shells of elites, the cyborg, the monitor and sentinels, drawn by halo1_plasma.shader and driven
/// by the object's functions (the <c>HaloFunctionA-D</c> attributes). Registered per tag as
/// <c>mount://haloce/&lt;shader tag path&gt;.vmat</c> for a game to draw over a model (a second renderer
/// with the material as its override), and built for model geometry that uses one.
/// </summary>
/// <remarks>
/// The noise maps are 3D bitmaps (the stock one is 32x32x32 A8Y8). They're handed to the shader as a
/// 2D atlas of their top level's slices, which it samples itself: volume textures made at runtime
/// may not bind to runtime materials, the way cube maps don't.
/// </remarks>
static class PlasmaMaterial
{
	public const string ShaderPath = "shaders/halo1_plasma.shader";

	/// <summary>
	/// Each noise bitmap's atlas and size, per map.
	/// </summary>
	static readonly ConditionalWeakTable<CacheFile, Dictionary<uint, (Texture Texture, Vector4 Size)>> _noise = new();

	public static Material Create( HaloCeMount host, string name, CacheFile cache, TagEntry shaderTag )
	{
		var shader = new ShaderTransparentPlasmaTag( cache, shaderTag );
		var material = Material.Create( name, ShaderPath );
		if ( material is null )
			return null;

		var primary = Noise( cache, shader.PrimaryNoiseMap );
		var secondary = Noise( cache, shader.SecondaryNoiseMap );
		material.Set( "g_tPrimaryNoise", primary?.Texture ?? MaterialTextures.Grey );
		material.Set( "g_vPrimaryNoiseSize", primary?.Size ?? Vector4.One );
		material.Set( "g_tSecondaryNoise", secondary?.Texture ?? MaterialTextures.Grey );
		material.Set( "g_vSecondaryNoiseSize", secondary?.Size ?? Vector4.One );

		material.Set( "g_vPrimaryAnimation", new Vector4( (Vector3)shader.PrimaryAnimationDirection, shader.PrimaryAnimationPeriod ) );
		material.Set( "g_vSecondaryAnimation", new Vector4( (Vector3)shader.SecondaryAnimationDirection, shader.SecondaryAnimationPeriod ) );
		material.Set( "g_vNoiseScale", new Vector2( shader.PrimaryNoiseMapScale, shader.SecondaryNoiseMapScale ) );

		material.Set( "g_vPerpendicularPlasma", new Vector4( (Vector3)shader.PerpendicularTint, shader.PerpendicularBrightness ) );
		material.Set( "g_vParallelPlasma", new Vector4( (Vector3)shader.ParallelTint, shader.ParallelBrightness ) );
		material.Set( "g_vPlasmaTint", Vector3.One );

		// Without a source the shell is at full intensity and not pushed out.
		material.Set( "g_vPlasmaSources", new Vector2( (float)shader.IntensitySource, (float)shader.OffsetSource ) );
		material.Set( "g_flPlasmaIntensityExponent", shader.IntensityExponent );
		material.Set( "g_vPlasmaOffset", new Vector2( shader.OffsetAmount * HaloCeMount.WorldScale, shader.OffsetExponent ) );
		material.Set( "g_flPlasmaWorldScale", HaloCeMount.WorldScale );

		return material;
	}

	/// <summary>
	/// A 3D bitmap's top level as a 2D atlas of its slices (slice k at tile (k mod tiles across,
	/// k / tiles across)), and (width, height, depth, tiles across).
	/// </summary>
	static (Texture Texture, Vector4 Size)? Noise( CacheFile cache, TagEntry bitmapTag )
	{
		if ( bitmapTag is null )
			return null;

		var byBitmap = _noise.GetOrCreateValue( cache );
		lock ( byBitmap )
		{
			if ( byBitmap.TryGetValue( bitmapTag.Id, out var cached ) )
				return cached;

			var bitmap = new BitmapTag( cache, bitmapTag ).Bitmaps.FirstOrDefault();
			if ( bitmap is null || bitmap.Type != BitmapType.Texture3D || bitmap.Depth < 1 || !BitmapDecoder.CanDecode( bitmap.Format ) )
				return null;

			var pixels = BitmapTag.ReadPixels( cache, bitmap );
			var (width, height, depth) = (bitmap.Width, bitmap.Height, bitmap.Depth);
			var rgba = new byte[width * height * depth * 4];
			BitmapDecoder.ToRgba8888( bitmap.Format, pixels.AsSpan( 0, bitmap.GetLevelSize( 0 ) ), rgba, width * height * depth );

			var tilesX = (int)Math.Ceiling( Math.Sqrt( depth ) );
			var tilesY = (depth + tilesX - 1) / tilesX;
			var atlasWidth = width * tilesX;
			var atlas = new byte[atlasWidth * height * tilesY * 4];
			for ( var z = 0; z < depth; z++ )
			for ( var y = 0; y < height; y++ )
			{
				var row = ((z / tilesX) * height + y) * atlasWidth + (z % tilesX) * width;
				Array.Copy( rgba, ((z * height + y) * width) * 4, atlas, row * 4, width * 4 );
			}

			var texture = Texture.Create( atlasWidth, height * tilesY, ImageFormat.RGBA8888 )
				.WithName( $"{bitmapTag.Path}_slices" )
				.WithData( atlas )
				.WithStaticUsage()
				.Finish();

			var result = (texture, new Vector4( width, height, depth, tilesX ));
			byBitmap[bitmapTag.Id] = result;
			return result;
		}
	}
}

/// <summary>
/// Loads a plasma shader tag as a material.
/// </summary>
sealed class PlasmaMaterialLoader( string mapPath, uint tagId ) : HaloCeLoader
{
	protected override object Load()
	{
		var cache = Host.GetCache( mapPath );
		var tag = cache.GetTag( tagId );
		return tag is null ? null : PlasmaMaterial.Create( Host, Path, cache, tag );
	}
}
