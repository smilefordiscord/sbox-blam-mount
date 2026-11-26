using System.Runtime.InteropServices;
using Blam.Halo2;
using Blam.Halo2.Tags;

namespace Sandbox.Mounting.Blam.Halo2;

/// <summary>
/// The vertex format of halo2_decal.shader: position, uv and the decal's colour and opacity.
/// </summary>
[StructLayout( LayoutKind.Sequential )]
struct DecalShaderVertex
{
	[VertexLayout.Position] public Vector3 Position;
	[VertexLayout.Normal] public Vector3 Normal;
	[VertexLayout.Tangent] public Vector4 Tangent;
	[VertexLayout.TexCoord( 0 )] public Vector2 TexCoord;

	/// <summary>
	/// Rgb (gamma, 0-1) and opacity.
	/// </summary>
	[VertexLayout.TexCoord( 4 )] public Vector4 Color;
}

/// <summary>
/// The vertex format of halo2_decorator.shader (see there).
/// </summary>
[StructLayout( LayoutKind.Sequential )]
struct DecoratorShaderVertex
{
	[VertexLayout.Position] public Vector3 Position;
	[VertexLayout.Normal] public Vector3 Normal;
	[VertexLayout.Tangent] public Vector4 Tangent;
	[VertexLayout.TexCoord( 0 )] public Vector2 TexCoord;
	[VertexLayout.TexCoord( 4 )] public Vector3 Anchor;
	[VertexLayout.TexCoord( 5 )] public Vector3 Color;

	/// <summary>
	/// Fade k per s&amp;box unit, kind (0 model, 1 card), card side and up offsets (s&amp;box units).
	/// </summary>
	[VertexLayout.TexCoord( 6 )] public Vector4 Decorator;
}

/// <summary>
/// Builds a Halo 2 structure BSP's decals and decorators: its placed decals laid onto the level (<see cref="DecalGeometry"/>),
/// and its grass, rocks, debris and stains (<see cref="StructureDecorators"/>).
/// </summary>
/// <remarks>
/// Decals of both kinds are one model per BSP (halo2_decal.shader), drawn in Halo's order by <see cref="SceneUpkeep"/>
/// (after the sky, before the water and everything else transparent, <see cref="SceneUpkeep.DecalTag"/>). Decorator
/// models and grass cards are opaque (halo2_decorator.shader), a model per decorator cache block (one 8 world unit cell
/// of the level) so they're culled with the view.
/// </remarks>
static class DecalModels
{
	public const string DecalShaderPath = "shaders/halo2_decal.shader";
	public const string DecoratorShaderPath = "shaders/halo2_decorator.shader";

	/// <summary>
	/// MCC's fade constant per fade distance (per world unit): 3 - k distance.
	/// </summary>
	static readonly float[] FadeK = [0.375f, 0.1875f, 0.125f];

	/// <summary>
	/// The BSP's placed decals as one model, or null if none came out.
	/// </summary>
	/// <param name="name">The BSP model's name (the decals are named after it).</param>
	/// <param name="palette">The scenario's decal palette, which the BSP's runtime decals index.</param>
	/// <param name="decorators">The BSP's decorators, whose floating decals go in too (null for none).</param>
	public static Model BuildDecals( string name, StructureBspTag bsp, IReadOnlyList<TagEntry> palette, StructureDecorators decorators )
	{
		var baseName = System.IO.Path.ChangeExtension( name, null );
		var meshes = new Dictionary<string, (Material Material, List<DecalShaderVertex> Vertices)>();

		if ( bsp.RuntimeDecals.Count > 0 )
		{
			var geometry = new DecalGeometry( bsp );
			var bitmaps = new Dictionary<TagEntry, BitmapTag>();
			BitmapTag BitmapOf( DecalTag decal )
			{
				if ( decal.Bitmap is null || decal.Bitmap.Class != BitmapTag.Class )
					return null;
				if ( !bitmaps.TryGetValue( decal.Bitmap, out var bitmap ) )
					bitmaps[decal.Bitmap] = bitmap = new BitmapTag( decal.Bitmap );
				return bitmap;
			}

			foreach ( var runtime in bsp.RuntimeDecals )
			{
				if ( runtime.Palette >= palette.Count || palette[runtime.Palette] is not { } tag )
					continue;

				foreach ( var placed in geometry.Place( new DecalTag( tag ), runtime.Position, runtime.Yaw, runtime.Pitch, BitmapOf ) )
				{
					if ( placed.Decal.Layer == DecalLayer.Error || placed.Decal.Bitmap is null )
						continue;

					var key = $"{placed.Decal.Tag.Path}_{placed.BitmapIndex}";
					if ( !meshes.TryGetValue( key, out var mesh ) )
					{
						var material = DecalMaterial( $"{baseName}/decals/{Halo2Mount.ResourcePath( placed.Decal.Tag.Path )}_{placed.BitmapIndex}.vmat",
							placed.Decal.Bitmap, placed.BitmapIndex, LayerMode( placed.Decal.Layer ), 1, true, 0 );
						if ( material is null )
							continue;
						meshes[key] = mesh = (material, new List<DecalShaderVertex>());
					}

					var color = new Vector4( placed.Color.X, placed.Color.Y, placed.Color.Z, 1 );
					foreach ( var v in placed.Triangles )
						mesh.Vertices.Add( DecalVertex( v.Position, v.TexCoord, color ) );
				}
			}
		}

		// Decorators' floating decals: decal_paint (alpha blended, tint times lightmap colour doubled) or
		// decal_paint_multiply, fading out with distance.
		foreach ( var group in decorators?.Groups.Where( g => g.Type == DecoratorType.FloatingDecal ) ?? [] )
		{
			var template = new ShaderTag( group.Shader );
			var multiply = template.TemplateName.EndsWith( "decorator_decal_paint_multiply", StringComparison.OrdinalIgnoreCase );
			var key = $"{group.Shader.Path}_{group.Fade}";
			if ( !meshes.TryGetValue( key, out var mesh ) )
			{
				var bitmap = template.GetBitmap( 0 );
				if ( bitmap?.Bitmap is null )
					continue;

				var material = DecalMaterial( $"{baseName}/decorators/{Halo2Mount.ResourcePath( group.Shader.Path )}_{group.Fade}.vmat",
					bitmap.Bitmap, Math.Max( 0, bitmap.Image ), multiply ? 2 : 0, multiply ? 1 : 2, !multiply, FadeK[group.Fade] / Halo2Mount.WorldScale );
				if ( material is null )
					continue;
				meshes[key] = mesh = (material, new List<DecalShaderVertex>());
			}

			foreach ( var index in group.Indices )
			{
				var v = group.DecalVertices[index];
				mesh.Vertices.Add( DecalVertex( v.Position, v.TexCoord, new Vector4( v.Color.X, v.Color.Y, v.Color.Z, 1 ) ) );
			}
		}

		if ( meshes.Count == 0 )
			return null;

		var builder = Model.Builder.WithName( $"{baseName}_decals.vmdl" );
		foreach ( var (material, vertices) in meshes.Values )
		{
			if ( vertices.Count < 3 )
				continue;

			var mesh = new Mesh( material );
			mesh.CreateVertexBuffer( vertices.Count, vertices );
			mesh.CreateIndexBuffer( vertices.Count, Enumerable.Range( 0, vertices.Count ).ToList() );
			mesh.Bounds = BBox.FromPoints( vertices.Select( v => v.Position ) );
			builder.AddMesh( mesh );
		}

		return builder.Create();
	}

	internal static DecalShaderVertex DecalVertex( System.Numerics.Vector3 position, System.Numerics.Vector2 texCoord, Vector4 color ) => new()
	{
		Position = (Vector3)position * Halo2Mount.WorldScale,
		Normal = Vector3.Up,
		Tangent = new Vector4( 1, 0, 0, 1 ),
		TexCoord = texCoord,
		Color = color,
	};

	/// <summary>
	/// halo2_decal.shader's mode for a decal layer: both lit alpha-blend layers are an unlit alpha blend in MCC.
	/// </summary>
	internal static int LayerMode( DecalLayer layer ) => layer switch
	{
		DecalLayer.DoubleMultiply => 1,
		DecalLayer.Multiply => 2,
		DecalLayer.Max => 3,
		DecalLayer.Add => 4,
		_ => 0,
	};

	internal static Material DecalMaterial( string name, TagEntry bitmap, int image, int mode, float colorScale, bool textureAlpha, float fadeK )
	{
		var texture = Texture.Load( $"mount://halo2/{Halo2Mount.TextureName( bitmap.Path, image )}.vtex" );
		if ( texture is null || texture.IsError )
			return null;

		var material = Material.Create( name, DecalShaderPath );
		if ( material is null )
			return null;

		material.Set( "g_tMap", texture );
		material.SetFeature( "F_DECAL_MODE", mode );
		material.Set( "g_flColorScale", colorScale );
		material.Set( "g_flTextureAlpha", textureAlpha ? 1f : 0f );
		material.Set( "g_flFadeK", fadeK );
		return material;
	}

	/// <summary>
	/// The BSP's decorator models and grass cards: a model per decorator cache block (null entries left out).
	/// </summary>
	/// <param name="name">The BSP model's name (the decorators are named after it).</param>
	public static List<Model> BuildDecorators( string name, StructureDecorators decorators )
	{
		var baseName = System.IO.Path.ChangeExtension( name, null );
		var materials = new Dictionary<TagEntry, Material>();
		var models = new List<Model>();

		Material MaterialFor( TagEntry shaderTag )
		{
			if ( materials.TryGetValue( shaderTag, out var material ) )
				return material;

			var shader = new ShaderTag( shaderTag );
			material = null;
			if ( shader.TemplateName.EndsWith( "decorator_tex", StringComparison.OrdinalIgnoreCase ) && TemplatePassParameters.Load( shader, 0 ) is { } texture )
			{
				material = Material.Create( $"{baseName}/decorators/{Halo2Mount.ResourcePath( shaderTag.Path )}.vmat", DecoratorShaderPath );
				material?.Set( "g_tMap", texture );
			}

			return materials[shaderTag] = material;
		}

		foreach ( var block in decorators.Groups.Where( g => g.Type is DecoratorType.Model or DecoratorType.AxisRotatingQuad ).GroupBy( g => g.CacheBlock ) )
		{
			var meshes = new Dictionary<Material, List<DecoratorShaderVertex>>();
			foreach ( var group in block )
			{
				if ( MaterialFor( group.Shader ) is not { } material )
					continue;

				if ( !meshes.TryGetValue( material, out var vertices ) )
					meshes[material] = vertices = new List<DecoratorShaderVertex>();

				var k = FadeK[group.Fade] / Halo2Mount.WorldScale;
				if ( group.Type == DecoratorType.Model )
					AddModels( vertices, group, k );
				else
					AddCards( vertices, group, k );
			}

			var builder = Model.Builder.WithName( $"{baseName}_decorators_{block.Key}.vmdl" );
			var count = 0;
			foreach ( var (material, vertices) in meshes )
			{
				if ( vertices.Count < 3 )
					continue;

				var mesh = new Mesh( material );
				mesh.CreateVertexBuffer( vertices.Count, vertices );
				mesh.CreateIndexBuffer( vertices.Count, Enumerable.Range( 0, vertices.Count ).ToList() );

				// Cards turn to face the camera: bound them by their reach every way.
				var bounds = BBox.FromPoints( vertices.Select( v => v.Anchor ) );
				var reach = vertices.Max( v => MathF.Max( MathF.Abs( v.Decorator.z ), MathF.Abs( v.Decorator.w ) ) );
				mesh.Bounds = bounds.AddBBox( BBox.FromPoints( vertices.Select( v => v.Position ) ) ).Grow( reach );
				builder.AddMesh( mesh );
				count++;
			}

			if ( count > 0 )
				models.Add( builder.Create() );
		}

		return models;
	}

	/// <summary>
	/// Each placement's model (a triangle list of the set's), placed by its axes, coloured by its colour times the light
	/// on each normal as MCC's decorator vertex shader lights it: colour ((1 - w) + w max( N · -L, 0 )).
	/// </summary>
	static void AddModels( List<DecoratorShaderVertex> vertices, DecoratorGroup group, float fadeK )
	{
		var set = group.Set;
		foreach ( var placement in group.Placements )
		{
			var model = placement.Permutation.Model;
			if ( model < 0 || model >= set.Models.Count )
				continue;

			var (start, count) = (set.Models[model].IndexStart, set.Models[model].IndexCount);
			var anchor = (Vector3)placement.Position * Halo2Mount.WorldScale;
			for ( var i = start; i < start + count && i < set.Indices.Length; i++ )
			{
				int index = set.Indices[i];
				if ( index >= set.Vertices.Length )
					continue;

				var v = set.Vertices[index];
				var position = placement.Position + v.Position.X * placement.Forward + v.Position.Y * placement.Left + v.Position.Z * placement.Up;
				var normal = v.Normal.X * placement.Forward + v.Normal.Y * placement.Left + v.Normal.Z * placement.Up;
				normal = normal.LengthSquared() > 0 ? System.Numerics.Vector3.Normalize( normal ) : System.Numerics.Vector3.UnitZ;
				var light = (1 - placement.LightWeight) + placement.LightWeight * MathF.Max( System.Numerics.Vector3.Dot( normal, -placement.LightDirection ), 0 );

				vertices.Add( new DecoratorShaderVertex
				{
					Position = (Vector3)position * Halo2Mount.WorldScale,
					Normal = normal,
					Tangent = new Vector4( 1, 0, 0, 1 ),
					TexCoord = v.TexCoord,
					Anchor = anchor,
					Color = (Vector3)(placement.Color * light),
					Decorator = new Vector4( fadeK, 0, 0, 0 ),
				} );
			}
		}
	}

	/// <summary>
	/// Grass cards: the tool's quads, each corner's offset from the placement along the card's side and up.
	/// </summary>
	static void AddCards( List<DecoratorShaderVertex> vertices, DecoratorGroup group, float fadeK )
	{
		foreach ( var index in group.Indices )
		{
			var v = group.SpriteVertices[index];
			var anchor = (Vector3)v.Anchor * Halo2Mount.WorldScale;
			var side = v.Offset.Y * Halo2Mount.WorldScale;
			var up = v.Offset.Z * Halo2Mount.WorldScale;
			vertices.Add( new DecoratorShaderVertex
			{
				Position = anchor + new Vector3( 0, side, up ),
				Normal = Vector3.Up,
				Tangent = new Vector4( 1, 0, 0, 1 ),
				TexCoord = v.TexCoord,
				Anchor = anchor,
				Color = (Vector3)v.Color,
				Decorator = new Vector4( fadeK, 1, side, up ),
			} );
		}
	}
}
