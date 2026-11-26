using System.Runtime.InteropServices;
using Blam.Halo2;
using Blam.Halo2.Tags;

namespace Sandbox.Mounting.Blam.Halo2;

/// <summary>
/// Halo 2's light volumes (<see cref="LightVolumeTag"/>, MGS2: Dune's watchtower beam, plasma bolts' glows) as models
/// drawn by halo2_light_volume.shader, <c>mount://halo2/&lt;light volume tag&gt;__light_volume.vmdl</c>: one quad per
/// volume at the origin (the marker), its +X the volume's axis; the shader places every sprite on screen.
/// </summary>
/// <remarks>
/// Halo 2's draw: each volume's functions are
/// sampled per sprite at both ends of their range input (the sprite's input per the tool's mode, see
/// <see cref="LightVolume.SpriteInput"/>) into a small float table the shader reads (see the shader's header), with
/// the facing function's curve. Without a bitmap the rasterizer globals' glow (<c>rasterizer\glow</c>); always image 0.
/// </remarks>
static class LightVolumeModels
{
	public const string ShaderPath = "shaders/halo2_light_volume.shader";

	/// <summary>
	/// Most sprites per volume the shader adds up.
	/// </summary>
	const int MaximumSprites = 32;

	public static string ResourceName( string tagPath ) => $"{Halo2Mount.ResourcePath( tagPath )}__light_volume";

	public static string FullPath( string tagPath ) => $"mount://halo2/{ResourceName( tagPath )}.vmdl";

	public static void Register( MountContext context, CacheFile cache, HashSet<string> registered )
	{
		foreach ( var tag in cache.Tags )
		{
			if ( tag.Cache != cache || tag.Class != LightVolumeTag.Class || tag.Address == 0 || string.IsNullOrEmpty( tag.Path ) )
				continue;

			if ( registered.Add( ResourceName( tag.Path ) ) )
				context.Add( ResourceType.Model, ResourceName( tag.Path ), new LightVolumeModelLoader( cache.FilePath, tag.Id ) );
		}
	}

	/// <summary>
	/// A corner of the quad: the layout halo2_light_volume.shader's common vertex input reads.
	/// </summary>
	[StructLayout( LayoutKind.Sequential )]
	struct Vertex
	{
		[VertexLayout.Position] public Vector3 Position;
		[VertexLayout.Normal] public Vector3 Normal;
		[VertexLayout.Tangent] public Vector4 Tangent;

		/// <summary>
		/// The corner, -1 to 1 on both axes.
		/// </summary>
		[VertexLayout.TexCoord( 0 )] public Vector2 Corner;

		[VertexLayout.TexCoord( 1 )] public Vector2 Unused;
	}

	static Texture _glow;

	/// <summary>
	/// A soft round spot for volumes without a bitmap.
	/// </summary>
	static Texture Glow()
	{
		if ( _glow is not null )
			return _glow;

		const int size = 64;
		var pixels = new byte[size * size * 4];
		for ( var y = 0; y < size; y++ )
		for ( var x = 0; x < size; x++ )
		{
			var dx = (x + 0.5f) / size * 2 - 1;
			var dy = (y + 0.5f) / size * 2 - 1;
			var r = MathF.Sqrt( dx * dx + dy * dy );
			var v = (byte)Math.Clamp( MathF.Round( 255 * MathF.Pow( Math.Clamp( 1 - r, 0, 1 ), 2 ) ), 0, 255 );
			var o = (y * size + x) * 4;
			pixels[o] = pixels[o + 1] = pixels[o + 2] = v;
			pixels[o + 3] = 255;
		}

		return _glow = Texture.Create( size, size, ImageFormat.RGBA8888 ).WithData( pixels ).Finish();
	}

	public static Model Build( string name, LightVolumeTag tag )
	{
		var scale = Halo2Mount.WorldScale;
		var builder = Model.Builder.WithName( name );
		var any = false;
		var index = 0;
		foreach ( var volume in tag.Volumes )
		{
			index++;
			var count = Math.Clamp( volume.SpriteCount, 1, MaximumSprites );
			var material = Material.Create( $"{name}/volume{index}.vmat", ShaderPath );
			if ( material is null )
			{
				Halo2Mount.Current?.Logger.Warning( $"{name}: couldn't create its material" );
				continue;
			}

			// The table: per sprite, per range input end (0 / 1): offset, radius, brightness, along scale; colour, away scale.
			// Per sprite and range input end (0 / 1), at the sprite's input for that end (LightVolume.SpriteInput):
			// offset, radius (mapped), brightness, along-axis scale; colour, away-from-axis scale (aspect functions at
			// range 0). Row 4: the facing function's curve (0-1, not mapped) at u = j / (width - 1).
			var aspect = volume.Aspects.FirstOrDefault();
			var width = Math.Max( count, 16 );
			var table = new float[width * 5 * 4];
			var bounds = new BBox( Vector3.Zero, Vector3.Zero );
			for ( var k = 0; k < count; k++ )
			{
				for ( var end = 0; end < 2; end++ )
				{
					var x = volume.SpriteInput( k, end );
					var offset = volume.Offset.IsEmpty ? 0 : volume.Offset.Evaluate( x, end );
					var radius = volume.Radius.IsEmpty ? 0 : volume.Radius.Evaluate( x, end );
					var brightness = volume.Brightness.IsEmpty ? 1 : volume.Brightness.Evaluate( x, end );
					var color = volume.Color.IsEmpty ? System.Numerics.Vector4.One : MappingFunction.ToVector( volume.Color.ColorAt( volume.Color.Curve( x, end ) ) );
					var along = aspect is null || aspect.AlongAxis.IsEmpty ? 1 : aspect.AlongAxis.Evaluate( x );
					var away = aspect is null || aspect.AwayFromAxis.IsEmpty ? 1 : aspect.AwayFromAxis.Evaluate( x );
					Set( table, width, k, end * 2, offset * scale, radius * scale, brightness, along );
					Set( table, width, k, end * 2 + 1, color.X, color.Y, color.Z, away );
					var reach = MathF.Abs( radius ) * scale * MathF.Max( MathF.Max( along, away ), aspect?.ParallelScale ?? 1 ) * 2;
					bounds = bounds.AddBBox( BBox.FromPositionAndSize( new Vector3( offset * scale, 0, 0 ), reach ) );
				}
			}

			for ( var j = 0; j < width; j++ )
				Set( table, width, j, 4, volume.Facing.IsEmpty ? j / (float)(width - 1) : volume.Facing.Curve( j / (float)(width - 1) ), 0, 0, 0 );

			var texture = Texture.Create( width, 5, ImageFormat.RGBA32323232F )
				.WithData( new ReadOnlySpan<float>( table ) )
				.Finish();

			material.Set( "g_tLightVolumeTable", texture );
			// Image 0 of the bitmap, or the rasterizer globals' glow (rasterizer\glow) without one.
			var bitmapPath = volume.Bitmap is { Address: not 0 } bitmap && bitmap.Class == BitmapTag.Class ? bitmap.Path : @"rasterizer\glow";
			material.Set( "g_tLightVolume", Texture.Load( $"mount://halo2/{Halo2Mount.TextureName( bitmapPath, 0 )}.vtex" ) is { IsError: false } map ? map : Glow() );
			material.Set( "g_vLightVolume", new Vector4( count, aspect?.ParallelScale ?? 1, aspect?.ParallelThresholdAngle ?? 0, aspect?.ParallelExponent ?? 1 ) );
			material.Set( "g_vLightVolumeDistance", new Vector2( tag.FalloffDistance, tag.CutoffDistance ) * scale );

			var vertices = new List<Vertex>( 4 );
			foreach ( var (x, y) in new[] { (-1f, 1f), (1f, 1f), (1f, -1f), (-1f, -1f) } )
				vertices.Add( new Vertex { Normal = Vector3.Forward, Tangent = new Vector4( 0, 1, 0, 1 ), Corner = new Vector2( x, y ) } );

			var mesh = new Mesh( material );
			mesh.CreateVertexBuffer( vertices.Count, vertices );
			mesh.CreateIndexBuffer( 6, new List<int> { 0, 1, 2, 0, 2, 3 } );
			mesh.Bounds = bounds;
			builder.AddMesh( mesh );
			any = true;
		}

		return any ? builder.Create() : null;
	}

	static void Set( float[] table, int width, int x, int row, float r, float g, float b, float a )
	{
		var o = (row * width + x) * 4;
		table[o] = r;
		table[o + 1] = g;
		table[o + 2] = b;
		table[o + 3] = a;
	}
}

sealed class LightVolumeModelLoader( string mapPath, uint tagId ) : Halo2Loader
{
	Model _model;

	protected override object Load()
	{
		if ( _model is not null )
			return _model;

		try
		{
			var tag = Host.GetCache( mapPath ).GetTag( tagId );
			_model = LightVolumeTag.IsLightVolume( tag ) ? LightVolumeModels.Build( Path, new LightVolumeTag( tag ) ) : null;
			if ( _model is null )
				Host.Logger.Warning( $"{Path}: no light volume model (main thread {ThreadSafe.IsMainThread})" );
			return _model;
		}
		catch ( Exception e )
		{
			Host.Logger.Warning( e, $"{Path}: {e.Message}" );
			return null;
		}
	}
}
