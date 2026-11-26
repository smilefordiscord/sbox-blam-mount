using Blam.Halo1;
using Blam.Halo1.Tags;

namespace Sandbox.Mounting.Blam.Halo1;

/// <summary>
/// Turns a level's placed decals into a model per decal type (and bitmap page, for a sprite sheet
/// spread over several): a quad per decal, facing the way Halo says, sitting just off the surface
/// it's on. Each quad is drawn while its cluster is (see <see cref="DecalClusters"/>), as Halo
/// draws the decals of each cluster it draws.
/// </summary>
static class DecalModel
{
	/// <summary>
	/// How far decals sit off their surface, to avoid z-fighting with it.
	/// </summary>
	const float SurfaceOffset = 0.5f;

	/// <summary>
	/// How far from a decal's position its surface can be, in world units.
	/// </summary>
	const float SurfaceSearchDistance = 0.25f;

	/// <param name="name">The models' names start with it, without its extension.</param>
	/// <param name="bsps">The level's BSPs, whose clusters the decals are found in.</param>
	public static List<Model> Build( HaloCeMount host, string name, CacheFile cache, IReadOnlyList<DecalPlacement> decals, SurfaceFinder surfaces, IReadOnlyList<BspDrawing> bsps )
	{
		var meshes = new List<(Mesh Mesh, int[] Indices, List<(BspDrawing, int[])> Clusters, BBox Bounds)>();

		foreach ( var group in decals.Select( ( placement, index ) => (Placement: placement, Index: index) ).GroupBy( d => d.Placement.Decal.Id ) )
		{
			var tag = group.First().Placement.Decal;
			var decal = new DecalTag( cache, tag );
			if ( decal.Map is null )
				continue;

			// Decal maps are usually sprite sheets with each sequence a variation of the decal (a
			// few different scorches or cracks). Halo gives every decal the first sprite of a random
			// sequence; here the pick comes from the decal's position and index, so it's the same
			// on every load. A plain bitmap is used whole.
			var bitmap = new BitmapTag( cache, decal.Map );
			var variations = bitmap.Sequences
				.Where( s => s.Sprites.Count > 0 && s.Sprites[0].BitmapIndex >= 0 && s.Sprites[0].BitmapIndex < bitmap.Bitmaps.Count )
				.Select( s => s.Sprites[0] ).ToList();
			BitmapSprite SpriteFor( DecalPlacement placement, int index ) => variations.Count == 0
				? new BitmapSprite( 0, 0, 1, 0, 1 )
				: variations[Math.Min( (int)(StableRandom.At( placement.Position, index + 1 ) * variations.Count), variations.Count - 1 )];

			var radius = (decal.RadiusMin + decal.RadiusMax) * 0.5f * HaloCeMount.WorldScale;
			DecalExtent ExtentOf( BitmapSprite sprite ) => variations.Count == 0
				? WholeBitmapExtent( decal, bitmap, radius )
				: SpriteExtent( decal, bitmap.Bitmaps[sprite.BitmapIndex], sprite, radius );

			// The sprites are nearly always on one bitmap; each bitmap needs its own material.
			var placed = group.Select( d =>
			{
				var sprite = SpriteFor( d.Placement, d.Index );
				return (d.Placement, Sprite: sprite, Extent: ExtentOf( sprite ));
			} );
			foreach ( var page in placed.GroupBy( d => d.Sprite.BitmapIndex ) )
			{
				var path = page.Key == 0 ? tag.Path : $"{tag.Path}_{page.Key}";
				var material = TransparentMaterial.CreateDecal( host, $"{name}/{path.Replace( '\\', '/' )}.vmat", cache, decal, page.Key );
				var (mesh, indices, centers, meshBounds) = BuildMesh( material, decal, page, surfaces );

				// The cluster of the point just off the surface, else of the decal's own position.
				meshes.Add( (mesh, indices, bsps.Select( bsp => (bsp, centers.Select( c => ClusterOf( bsp, c ) ).ToArray()) ).ToList(), meshBounds) );
			}
		}

		// Each is bounded by them all, so they sort among the level's other translucent things as
		// they did as one model.
		var models = new List<Model>();
		if ( meshes.Count == 0 )
			return models;

		var bounds = meshes.Select( m => m.Bounds ).Aggregate( ( a, b ) => a.AddBBox( b ) );
		var baseName = System.IO.Path.ChangeExtension( name, null );
		foreach ( var (mesh, indices, clusters, _) in meshes )
		{
			mesh.Bounds = bounds;
			var model = Model.Builder.WithName( $"{baseName}_{models.Count}.vmdl" ).AddMesh( mesh ).Create();
			ClusterVisibility.Register( model, new DecalClusters( mesh, indices, clusters ) );
			models.Add( model );
		}

		return models;
	}

	// A quad's edges from the decal's position along its right and up axes, in s&box units.
	readonly record struct DecalExtent( float Left, float Right, float Top, float Bottom );

	// Halo sizes a sprite by its pixels, a sprite as large as the maximum sprite extent spanning the radius, around its registration point.
	static DecalExtent SpriteExtent( DecalTag decal, BitmapData page, BitmapSprite sprite, float radius )
	{
		var aspect = decal.PreserveAspect ? (sprite.Right - sprite.Left) / (sprite.Bottom - sprite.Top) * page.Height / page.Width : 1;
		var scale = radius / decal.MaximumSpriteExtent;
		var widthScale = page.Width * scale;
		var heightScale = page.Height * scale * aspect;
		return new DecalExtent(
			-sprite.RegistrationX * widthScale,
			(sprite.Right - sprite.Left - sprite.RegistrationX) * widthScale,
			sprite.RegistrationY * heightScale,
			-(sprite.Bottom - sprite.Top - sprite.RegistrationY) * heightScale );
	}

	// A plain bitmap fills the radius square, or keeps its aspect with "preserve aspect".
	static DecalExtent WholeBitmapExtent( DecalTag decal, BitmapTag bitmap, float radius )
	{
		var aspect = decal.PreserveAspect && bitmap.Bitmaps.Count > 0 ? (float)bitmap.Bitmaps[0].Height / bitmap.Bitmaps[0].Width : 1;
		return new DecalExtent( -radius, radius, aspect * radius, -aspect * radius );
	}

	static int ClusterOf( BspDrawing bsp, (Vector3 Center, Vector3 Position) decal )
	{
		var cluster = bsp.View.FindCluster( (System.Numerics.Vector3)(decal.Center / HaloCeMount.WorldScale) );
		return cluster >= 0 ? cluster : bsp.View.FindCluster( (System.Numerics.Vector3)decal.Position );
	}

	/// <summary>
	/// The quads of a decal type's placements that use one bitmap, with each quad's center (s&amp;box
	/// units) and its decal's position (world units), and their bounds.
	/// </summary>
	static (Mesh Mesh, int[] Indices, List<(Vector3 Center, Vector3 Position)> Centers, BBox Bounds) BuildMesh( Material material, DecalTag decal, IEnumerable<(DecalPlacement Placement, BitmapSprite Sprite, DecalExtent Extent)> placements, SurfaceFinder surfaces )
	{
		var vertices = new List<HaloVertex>();
		var indices = new List<int>();
		var centers = new List<(Vector3 Center, Vector3 Position)>();
		foreach ( var (placement, sprite, extent) in placements )
		{
			// The placement's yaw and pitch bytes don't reliably describe the surface (checked
			// against the level on a10, b40 and Blood Gulch: the same pitch value shows up on
			// floors and walls), so the decal lies on the nearest level surface instead, facing
			// out of it and seen looking into it. Wall decals stand upright; floor and ceiling
			// decals turn with the yaw.
			var direction = ((Vector3)placement.Direction).Normal;
			var normal = surfaces.Nearest( placement.Position, SurfaceSearchDistance ) is { } surface
				? ((Vector3)surface.Normal).Normal
				: -direction;

			var forward = -normal;
			var heading = new Vector3( direction.x, direction.y, 0 );
			var up = MathF.Abs( normal.z ) < 0.7f
				? Vector3.Up - normal * normal.z
				: heading - normal * Vector3.Dot( heading, normal );
			if ( up.LengthSquared < 1e-4f )
				up = Vector3.Cross( normal, Vector3.Forward );
			up = up.Normal;
			var right = Vector3.Cross( forward, up ).Normal;

			// Most decals (scorches, blood, bullet holes) get a random spin; signs, arrows and
			// markings are flagged to keep their placed orientation. Seeded from the position so
			// every build of the map looks the same.
			if ( !decal.NoRandomRotation )
			{
				var angle = StableRandom.At( placement.Position ) * MathF.PI * 2;
				(right, up) = (right * MathF.Cos( angle ) + up * MathF.Sin( angle ), up * MathF.Cos( angle ) - right * MathF.Sin( angle ));
			}

			var center = (Vector3)placement.Position * HaloCeMount.WorldScale + normal * SurfaceOffset;
			centers.Add( (center, (Vector3)placement.Position) );

			var start = vertices.Count;
			void Corner( float x, float y, float u, float v ) => vertices.Add( new HaloVertex
			{
				Position = center + right * x + up * y,
				Normal = normal,
				Tangent = new Vector4( right, 1 ),
				TexCoord = new Vector2( u, v ),
			} );

			Corner( extent.Left, extent.Top, sprite.Left, sprite.Top );
			Corner( extent.Right, extent.Top, sprite.Right, sprite.Top );
			Corner( extent.Right, extent.Bottom, sprite.Right, sprite.Bottom );
			Corner( extent.Left, extent.Bottom, sprite.Left, sprite.Bottom );
			indices.AddRange( [start, start + 1, start + 2, start, start + 2, start + 3] );
		}

		var mesh = new Mesh( material );
		mesh.CreateVertexBuffer( vertices.Count, vertices );
		mesh.CreateIndexBuffer( indices.Count, indices );
		return (mesh, indices.ToArray(), centers, BBox.FromPoints( vertices.Select( x => x.Position ) ));
	}
}
