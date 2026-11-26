using System.Runtime.InteropServices;
using Blam.Halo1;
using Blam.Halo1.Tags;

namespace Sandbox.Mounting.Blam.Halo1;

/// <summary>
/// Turns a structure BSP's detail objects (grass, plants) into a model per detail object type and
/// sprite sheet page: a quad per object, those of each cell together, so the cells around the
/// camera are drawn as Halo draws them (see <see cref="DetailObjectCells"/>). The quads are turned
/// toward the camera and faded out with distance by halo1_detail_object.shader.
/// </summary>
static class DetailObjectModel
{
	public const string ShaderPath = "shaders/halo1_detail_object.shader";

	/// <summary>
	/// A detail object quad corner. Every corner of a quad is at the object's anchor; the shader
	/// moves it out by <see cref="Corner"/> along the camera-facing axes. The layout matches
	/// <see cref="HaloVertex"/>, which the common shader code reads as a normal and tangent.
	/// </summary>
	[StructLayout( LayoutKind.Sequential )]
	struct DetailObjectVertex
	{
		[VertexLayout.Position] public Vector3 Anchor;

		/// <summary>
		/// Offset from the anchor along the sprite's right and up axes (z unused).
		/// </summary>
		[VertexLayout.Normal] public Vector3 Corner;

		/// <summary>
		/// The object's baked color, in gamma space.
		/// </summary>
		[VertexLayout.Tangent] public Vector4 Color;

		[VertexLayout.TexCoord( 0 )] public Vector2 TexCoord;
		[VertexLayout.TexCoord( 1 )] public Vector2 Unused;
	}

	/// <param name="name">The models' names start with it, without its extension.</param>
	public static List<Model> Build( HaloCeMount host, string name, CacheFile cache, StructureBspTag bsp, IReadOnlyList<DetailObjectCollectionTag> palette )
	{
		var models = new List<Model>();
		var instances = DetailObjects.Read( bsp );
		if ( instances.Count == 0 )
			return models;

		// Each cell's objects one after another, so a cell is a run of each mesh's indices.
		instances = instances.OrderBy( i => i.Cell.X ).ThenBy( i => i.Cell.Y ).ThenBy( i => i.Cell.Z ).ToList();

		var plates = new Dictionary<TagEntry, BitmapTag>();
		var meshes = new Dictionary<(DetailObjectCollectionTag Collection, int Type, int Bitmap), (List<DetailObjectVertex> Vertices, List<int> Indices, List<(int X, int Y, int Z, int First, int Count)> Cells)>();

		foreach ( var instance in instances )
		{
			var (collection, typeIndex) = DetailObjects.FindType( palette, instance.Layer );
			if ( collection?.SpritePlate is null )
				continue;

			if ( !plates.TryGetValue( collection.SpritePlate, out var plate ) )
				plates[collection.SpritePlate] = plate = new BitmapTag( cache, collection.SpritePlate );

			var type = collection.Types[typeIndex];
			var sprite = GetSprite( plate, type, instance.Variant );
			if ( sprite is null || sprite.BitmapIndex < 0 || sprite.BitmapIndex >= plate.Bitmaps.Count )
				continue;

			var key = (collection, typeIndex, sprite.BitmapIndex);
			if ( !meshes.TryGetValue( key, out var mesh ) )
				meshes[key] = mesh = (new List<DetailObjectVertex>(), new List<int>(), new List<(int, int, int, int, int)>());

			var cells = mesh.Cells;
			if ( cells.Count == 0 || (cells[^1].X, cells[^1].Y, cells[^1].Z) != instance.Cell )
				cells.Add( (instance.Cell.X, instance.Cell.Y, instance.Cell.Z, mesh.Indices.Count, 0) );
			cells[^1] = cells[^1] with { Count = cells[^1].Count + 6 };

			// The type's size is world units per sprite pixel, and the sprite stands on its anchor.
			var bitmap = plate.Bitmaps[sprite.BitmapIndex];
			var width = (sprite.Right - sprite.Left) * bitmap.Width * type.Size * HaloCeMount.WorldScale;
			var height = (sprite.Bottom - sprite.Top) * bitmap.Height * type.Size * HaloCeMount.WorldScale;
			var anchor = ((Vector3)instance.Position + Vector3.Up * collection.GlobalZOffset) * HaloCeMount.WorldScale;
			var color = new Vector4( instance.Color.X, instance.Color.Y, instance.Color.Z, 1 );

			var start = mesh.Vertices.Count;
			void Corner( float x, float y, float u, float v ) => mesh.Vertices.Add( new DetailObjectVertex
			{
				Anchor = anchor,
				Corner = new Vector3( x, y, 0 ),
				Color = color,
				TexCoord = new Vector2( u, v ),
			} );

			Corner( -width * 0.5f, 0, sprite.Left, sprite.Bottom );
			Corner( width * 0.5f, 0, sprite.Right, sprite.Bottom );
			Corner( width * 0.5f, height, sprite.Right, sprite.Top );
			Corner( -width * 0.5f, height, sprite.Left, sprite.Top );
			mesh.Indices.AddRange( [start, start + 1, start + 2, start, start + 2, start + 3] );
		}

		var baseName = System.IO.Path.ChangeExtension( name, null );
		foreach ( var ((collection, typeIndex, bitmapIndex), (vertices, indices, cells)) in meshes )
		{
			var type = collection.Types[typeIndex];
			var material = CreateMaterial( host, $"{name}/{HaloCeMount.ResourcePath( collection.Tag.Path )}_{typeIndex}_{bitmapIndex}.vmat", collection, type, bitmapIndex );
			if ( material is null )
				continue;

			// The quads are points until the shader spreads them out, so the bounds need room for
			// the biggest sprite around every anchor.
			var reach = vertices.Max( v => MathF.Max( MathF.Abs( v.Corner.x ), v.Corner.y ) );
			var bounds = BBox.FromPoints( vertices.Select( v => v.Anchor ) );

			var mesh = new Mesh( material );
			mesh.CreateVertexBuffer( vertices.Count, vertices );
			mesh.CreateIndexBuffer( indices.Count, indices );
			mesh.Bounds = new BBox( bounds.Mins - reach, bounds.Maxs + reach );

			var model = Model.Builder.WithName( $"{baseName}_{models.Count}.vmdl" ).AddMesh( mesh ).Create();
			ClusterVisibility.Register( model, new DetailObjectCells( mesh, indices.ToArray(), cells.ToArray() ) );
			models.Add( model );
		}

		return models;
	}

	/// <summary>
	/// The sprite an object shows: one of the type's run of sprites in its sequence of the plate.
	/// </summary>
	static BitmapSprite GetSprite( BitmapTag plate, DetailObjectType type, int variant )
	{
		if ( type.SequenceIndex >= plate.Sequences.Count )
			return null;

		var sprites = plate.Sequences[type.SequenceIndex].Sprites;
		var count = type.SpriteCount > 0 ? type.SpriteCount : sprites.Count;
		var index = type.FirstSpriteIndex + variant % Math.Max( 1, count );
		return index < sprites.Count ? sprites[index] : null;
	}

	static Material CreateMaterial( HaloCeMount host, string name, DetailObjectCollectionTag collection, DetailObjectType type, int bitmapIndex )
	{
		var material = Material.Create( name, ShaderPath );
		if ( material is null )
			return null;

		material.Set( "g_tSpritePlate", MaterialTextures.Load( host, material, collection.SpritePlate, bitmapIndex ) ?? MaterialTextures.Transparent );
		material.Set( "g_flNearFade", type.NearFadeDistance * HaloCeMount.WorldScale );
		material.Set( "g_flFarFade", type.FarFadeDistance * HaloCeMount.WorldScale );
		material.Set( "g_flFacing", collection.Facing == DetailObjectFacing.ScreenFacing ? 0f : 1f );

		return material;
	}
}
