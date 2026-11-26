using System;
using System.Collections.Generic;
using System.Linq;
using Blam.Halo2.Tags;

namespace Blam.Halo2;

// Inside the namespace so these win over s&box's global vector types when this compiles into the mount.
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;

/// <summary>
/// What Halo 2 reads from the level under an object: the lightmap texel (raw, 0-1, not doubled) and the
/// lightmap's incident direction there (towards the light; its length is how directional the light is).
/// </summary>
public readonly record struct ObjectLightSample( Vector3 Lightmap, Vector3 Direction );

/// <summary>
/// An object's lighting, as Halo 2's object shaders receive it (the <c>LIGHTMAP_object_*</c> externs), in
/// Halo's gamma space units. The directions point towards the light.
/// </summary>
public readonly record struct ObjectLight( Vector3 Ambient, Vector3 Direct, Vector3 ToDirect, Vector3 Indirect, Vector3 ToIndirect )
{
	/// <summary>
	/// Lighting for objects nothing was sampled for: a mid-grey level lit from above.
	/// </summary>
	public static readonly ObjectLight Default = Evaluate( null, new ObjectLightSample( new Vector3( 0.5f ), new Vector3( 0, 0, 0.5f ) ) );

	/// <summary>
	/// What an object keeps when its lightmap sample fails (nothing lit below it): the values Halo 2 gives a new
	/// render state and doesn't replace until a sample succeeds: ambient 0.09, direct 0.12 travelling
	/// down from yaw 0, pitch −72°, indirect 0.09 the opposite way, grey. Read live too, on Zanzibar's PRT blades,
	/// which MCC never samples.
	/// </summary>
	public static readonly ObjectLight Unsampled = UnsampledLight();

	static ObjectLight UnsampledLight()
	{
		var (sin, cos) = MathF.SinCos( -1.2566371f );
		var travel = new Vector3( cos, 0, sin );
		return FromStored( new Vector3( 0.09f ), new Vector3( 0.12f ), travel, new Vector3( 0.09f ), -travel );
	}

	/// <summary>
	/// Lighting as the engine stores it (its render lighting struct: ambient, shadow direction, d, shadow, direct
	/// colour, direct vector, indirect colour, indirect vector, f32 each): the vectors are the directions the light
	/// travels, so they're turned around to point towards it.
	/// </summary>
	public static ObjectLight FromStored( Vector3 ambient, Vector3 direct, Vector3 directVector, Vector3 indirect, Vector3 indirectVector ) =>
		new( ambient, direct, -directVector, indirect, -indirectVector );

	/// <summary>
	/// Halo 2's formula (its gldf path): the lightmap texel <c>lm</c> and the incident direction <c>n·d</c> become
	/// a direct light from <c>n</c>, an indirect light from the opposite side swung about Z, and an ambient,
	/// each clamped and scaled by the element's functions of <c>d</c>. Without an element, the shipped
	/// tags' values (all four identical but for the ambient minimum) are used.
	/// </summary>
	/// <param name="albedo">The surface colour under the object (Halo samples the shader's lightmap bitmap
	/// property; 0.498 grey when it has none, which is what's used here).</param>
	public static ObjectLight Evaluate( GlobalLightingElement e, ObjectLightSample sample, Vector3? albedo = null )
	{
		// A direction too short to normalize stays as it is, which the cone then lays flat.
		var d = sample.Direction.Length();
		var n = d > 1e-4f ? sample.Direction / d : sample.Direction;
		d = Math.Min( d, 1 );

		var lm = sample.Lightmap + new Vector3( e?.LightmapBrightnessOffset ?? 0 );
		var primary = Vector3.Clamp( lm, e?.PrimaryMin ?? Vector3.Zero, e?.PrimaryMax ?? Vector3.One );
		var secondary = Vector3.Clamp( lm, e?.SecondaryMin ?? Vector3.Zero, e?.SecondaryMax ?? Vector3.One );
		var diffuse = Vector3.Clamp( albedo ?? new Vector3( 0x7F / 255f ), e?.DiffuseMin ?? new Vector3( 0.25f ), e?.DiffuseMax ?? new Vector3( 0.75f ) );
		var ambient = Vector3.Clamp( lm, e?.AmbientMin ?? new Vector3( 0.06f ), e?.AmbientMax ?? Vector3.One );

		var toDirect = ConeClamp( n, (e?.ExclusionAngle ?? 0) * MathF.PI / 180 );
		var (sin, cos) = MathF.SinCos( (e?.ZAxisRotation ?? 5) * MathF.PI / 180 );
		var swung = new Vector3( toDirect.X * cos - toDirect.Y * sin, toDirect.X * sin + toDirect.Y * cos, toDirect.Z );

		var lum = 0.299f * lm.X + 0.587f * lm.Y + 0.114f * lm.Z;
		var direct = primary * (e?.PrimaryFunction.Evaluate( d ) ?? 0.57f + 0.05f * d);
		var indirect = (d * d * secondary * diffuse + (1 - d * d) * primary) * (e?.SecondaryFunction.Evaluate( d ) ?? 0.55f - 0.05f * d);
		ambient *= (e?.AmbientFunction.Evaluate( lum ) ?? 1 - 0.7f * lum) * 1.4f * (0.4f - 0.1f * d);

		return new ObjectLight( ambient, direct, toDirect, indirect, -swung );
	}

	/// <summary>
	/// Keeps a direction out of a cone of half-angle <paramref name="angle"/> around straight up and down
	/// (bipeds aren't lit from right above), and makes it unit length: its horizontal part is renormalized, or
	/// turned to +X when it has next to none.
	/// </summary>
	static Vector3 ConeClamp( Vector3 n, float angle )
	{
		var limit = MathF.Cos( angle );
		var z = MathF.Abs( n.Z ) > limit ? MathF.CopySign( limit, n.Z ) : n.Z;
		var xy = new Vector2( n.X, n.Y );
		xy = (MathF.Abs( n.X ) < 0.01f && MathF.Abs( n.Y ) < 0.01f) || xy.Length() < 1e-4f ? Vector2.UnitX : Vector2.Normalize( xy );
		xy *= MathF.Sqrt( Math.Max( 0, 1 - z * z ) );
		return new Vector3( xy.X, xy.Y, z );
	}
}

/// <summary>
/// Samples a level's lightmap under objects, the way Halo 2 does: a ray 10 world units straight down from
/// the object's bounding sphere centre against the level's collision, front faces only, past two-sided and
/// invisible surfaces (glass, grates, player clipping); the hit surface's render triangle (of its cluster or
/// instance and shader, along the same ray) gives the lightmap texture coordinate and incident direction,
/// and the cluster's (or instance's) lightmap page is point sampled.
/// </summary>
/// <remarks>
/// Halo also samples the shader's "lightmap bitmap property" texel as the albedo; that isn't read here
/// (its 0.498 grey fallback is used), nor do rays hit objects or test for the sky material, as Halo's do.
/// </remarks>
public sealed class ObjectLightingSampler
{
	/// <summary>
	/// Reads a lightmap page's texels: (width, height, B G R A bytes), or null.
	/// </summary>
	public delegate (int Width, int Height, byte[] Bgra)? PageReader( TagEntry bitmapTag, int page );

	const float CellSize = 2;

	readonly List<Vector3> _positions = new();
	readonly List<Vector2> _lightmapUvs = new();
	readonly List<Vector3> _directions = new();
	readonly List<Vector3> _colors = new();
	readonly List<(int A, int B, int C, int Page, TagEntry Bitmaps)> _triangles = new();

	// Render triangles by (BSP, instance or -1 for the structure, shader); collision triangles in a grid of their own.
	readonly Dictionary<(StructureBspTag Bsp, int Instance, TagEntry Shader), List<int>> _rendered = new();
	readonly List<(Vector3 A, Vector3 B, Vector3 C, Vector3 Normal, StructureBspTag Bsp, int Instance, TagEntry Shader)> _collision = new();
	readonly Dictionary<(int X, int Y), List<int>> _grid = new();
	readonly Dictionary<(TagEntry, int), (int Width, int Height, byte[] Bgra)?> _pages = new();
	readonly PageReader _readPage;

	public int TriangleCount => _triangles.Count;

	public ObjectLightingSampler( IEnumerable<(StructureBspTag Bsp, LightmapTag Lightmap)> bsps, PageReader readPage )
	{
		_readPage = readPage;
		foreach ( var (bsp, lightmap) in bsps )
		{
			if ( lightmap?.Bitmaps is null )
				continue;

			if ( bsp.Collision is not null )
				AddCollision( bsp, bsp.Collision, null );

			for ( var i = 0; i < bsp.Clusters.Count; i++ )
				Add( bsp, bsp.Clusters[i], null, Page( lightmap.ClusterPages, i ), lightmap.Bitmaps, default, default );

			// Instances take their incident directions from the lightmap's geometry buckets (world space already),
			// and the ones lit per vertex (no page) their light colour too, which MCC interpolates at the hit
			// instead of sampling a page (as Halo 2 does).
			foreach ( var instance in bsp.Instances )
			{
				if ( instance.Definition < 0 || instance.Definition >= bsp.Definitions.Count )
					continue;

				if ( instance.Definition < bsp.DefinitionCollision.Count && bsp.DefinitionCollision[instance.Definition] is { } collision )
					AddCollision( bsp, collision, instance );

				var definition = bsp.Definitions[instance.Definition];
				var page = Page( lightmap.InstancePages, instance.Index );
				var (incident, colors) = lightmap.InstanceLighting( instance.Index, definition.VertexCount );
				if ( page >= 0 || colors.Array is not null )
					Add( bsp, definition, instance, page, lightmap.Bitmaps, incident, page >= 0 ? default : colors );
			}
		}
	}

	static int Page( IReadOnlyList<int> pages, int index ) => index < pages.Count ? pages[index] : -1;

	/// <summary>
	/// Adds the collision surfaces a lighting ray can hit: not two-sided or invisible, each fanned into triangles.
	/// </summary>
	void AddCollision( StructureBspTag bsp, CollisionBsp collision, InstancedGeometryInstance instance )
	{
		foreach ( var surface in collision.Surfaces )
		{
			if ( surface.Corners.Length < 3 || surface.TwoSided || surface.Invisible || surface.Plane >= collision.Planes.Count )
				continue;

			var normal = collision.Planes[surface.Plane].Normal;
			if ( surface.PlaneFlipped )
				normal = -normal;
			if ( instance is not null )
				normal = instance.TransformDirection( normal );

			var shader = surface.Material >= 0 && surface.Material < bsp.CollisionMaterials.Count ? bsp.CollisionMaterials[surface.Material].Shader : null;
			Vector3 Corner( int i ) => instance is null ? collision.Vertices[surface.Corners[i]] : instance.Transform( collision.Vertices[surface.Corners[i]] );
			for ( var i = 1; i + 1 < surface.Corners.Length; i++ )
			{
				var index = _collision.Count;
				var (a, b, c) = (Corner( 0 ), Corner( i ), Corner( i + 1 ));
				_collision.Add( (a, b, c, normal, bsp, instance?.Index ?? -1, shader) );
				var min = Vector3.Min( a, Vector3.Min( b, c ) );
				var max = Vector3.Max( a, Vector3.Max( b, c ) );
				for ( var x = Cell( min.X ); x <= Cell( max.X ); x++ )
				{
					for ( var y = Cell( min.Y ); y <= Cell( max.Y ); y++ )
					{
						if ( !_grid.TryGetValue( (x, y), out var list ) )
							_grid[(x, y)] = list = new List<int>();
						list.Add( index );
					}
				}
			}
		}
	}

	/// <summary>
	/// Adds a section's drawn triangles by shader: sampled from lightmap page <paramref name="page"/>, or when it has
	/// none, lit by <paramref name="colors"/> per vertex.
	/// </summary>
	void Add( StructureBspTag bsp, GeometrySection section, InstancedGeometryInstance instance, int page, TagEntry bitmaps, ArraySegment<Vector3> incident, ArraySegment<Vector3> colors )
	{
		if ( section.IsEmpty )
			return;

		var perVertex = page < 0 && colors.Array is not null && colors.Count == section.VertexCount;
		if ( !perVertex && (page < 0 || section.LightmapTexCoords is null) )
			return;

		var first = _positions.Count;
		for ( var i = 0; i < section.VertexCount; i++ )
		{
			var p = section.Positions[i];
			Vector3 dir;
			if ( incident.Array is not null && i < incident.Count )
			{
				dir = incident[i];
			}
			else
			{
				dir = section.IncidentDirections?[i] ?? Vector3.Zero;
				if ( instance is not null )
					dir = dir.X * instance.Forward + dir.Y * instance.Left + dir.Z * instance.Up;
			}

			if ( instance is not null )
				p = instance.Transform( p );

			_positions.Add( p );
			_lightmapUvs.Add( perVertex ? Vector2.Zero : section.LightmapTexCoords[i] );
			_colors.Add( perVertex ? colors[i] : Vector3.Zero );
			_directions.Add( dir );
		}

		if ( perVertex )
			page = -1;

		foreach ( var part in section.Parts.Where( p => p.IsDrawn ) )
		{
			var shader = part.Material >= 0 && part.Material < bsp.Materials.Count ? bsp.Materials[part.Material] : null;
			var key = (bsp, instance?.Index ?? -1, shader);
			if ( !_rendered.TryGetValue( key, out var list ) )
				_rendered[key] = list = new List<int>();

			foreach ( var (a, b, c) in section.Triangles( part ) )
			{
				list.Add( _triangles.Count );
				_triangles.Add( (first + a, first + b, first + c, page, bitmaps) );
			}
		}
	}

	static int Cell( float v ) => (int)MathF.Floor( v / CellSize );

	/// <summary>
	/// The rays Halo tries when an object's definition asks it to search the cardinal directions after the
	/// ray down misses.
	/// </summary>
	static readonly Vector3[] CardinalRays = [new( -10, 0, 0 ), new( 10, 0, 0 ), new( 0, -10, 0 ), new( 0, 10, 0 ), new( 0, 0, -40 )];

	/// <summary>
	/// The lightmap sample for an object whose bounding sphere is centred at <paramref name="centre"/>
	/// (world units), or null if nothing lit is below it.
	/// </summary>
	public ObjectLightSample? Sample( Vector3 centre, bool searchCardinalDirections = false )
	{
		var sample = Cast( centre, new Vector3( 0, 0, -10 ) );
		if ( sample is null && searchCardinalDirections )
		{
			foreach ( var ray in CardinalRays )
			{
				sample = Cast( centre, ray );
				if ( sample is not null )
					break;
			}
		}

		return sample;
	}

	ObjectLightSample? Cast( Vector3 centre, Vector3 ray )
	{
		// Halo starts the ray a thousandth of its length back.
		var origin = centre - ray * 0.001f;
		IEnumerable<int> candidates = ray.X == 0 && ray.Y == 0
			? _grid.TryGetValue( (Cell( origin.X ), Cell( origin.Y )), out var list ) ? list : []
			: Enumerable.Range( 0, _collision.Count );

		var surface = -1;
		var surfaceDistance = float.MaxValue;
		foreach ( var t in candidates )
		{
			var (a, b, c, normal, _, _, _) = _collision[t];
			if ( Vector3.Dot( normal, ray ) < 0 && Intersect( origin, ray, a, b, c, out var distance, out _, out _ ) && distance < surfaceDistance )
			{
				surfaceDistance = distance;
				surface = t;
			}
		}

		if ( surface < 0 )
			return null;

		// The render triangle of the surface's shader that the ray crosses nearest the surface.
		var (_, _, _, _, bsp, instance, shader) = _collision[surface];
		if ( !_rendered.TryGetValue( (bsp, instance, shader), out var rendered ) )
			return null;

		var best = float.MaxValue;
		var hit = -1;
		var hitU = 0f;
		var hitV = 0f;
		foreach ( var t in rendered )
		{
			var (a, b, c, _, _) = _triangles[t];
			if ( Intersect( origin, ray, _positions[a], _positions[b], _positions[c], out var distance, out var u, out var v ) && MathF.Abs( distance - surfaceDistance ) < best )
			{
				best = MathF.Abs( distance - surfaceDistance );
				hit = t;
				hitU = u;
				hitV = v;
			}
		}

		if ( hit < 0 )
			return null;

		var tri = _triangles[hit];
		var w0 = 1 - hitU - hitV;
		var uv = _lightmapUvs[tri.A] * w0 + _lightmapUvs[tri.B] * hitU + _lightmapUvs[tri.C] * hitV;
		var direction = _directions[tri.A] * w0 + _directions[tri.B] * hitU + _directions[tri.C] * hitV;
		if ( tri.Page < 0 )
			return new ObjectLightSample( Vector3.Clamp( _colors[tri.A] * w0 + _colors[tri.B] * hitU + _colors[tri.C] * hitV, Vector3.Zero, Vector3.One ), direction );

		if ( !_pages.TryGetValue( (tri.Bitmaps, tri.Page), out var page ) )
			_pages[(tri.Bitmaps, tri.Page)] = page = _readPage( tri.Bitmaps, tri.Page );
		if ( page is not { } texels || texels.Width <= 0 || texels.Height <= 0 )
			return null;

		var x = Math.Clamp( (int)(uv.X * texels.Width), 0, texels.Width - 1 );
		var y = Math.Clamp( (int)(uv.Y * texels.Height), 0, texels.Height - 1 );
		var o = (y * texels.Width + x) * 4;
		var color = new Vector3( texels.Bgra[o + 2], texels.Bgra[o + 1], texels.Bgra[o] ) / 255f;
		return new ObjectLightSample( color, direction );
	}

	/// <summary>
	/// Ray against triangle, either side, within the ray's length (Möller-Trumbore).
	/// </summary>
	static bool Intersect( Vector3 origin, Vector3 ray, Vector3 a, Vector3 b, Vector3 c, out float distance, out float u, out float v )
	{
		distance = u = v = 0;
		var e1 = b - a;
		var e2 = c - a;
		var p = Vector3.Cross( ray, e2 );
		var det = Vector3.Dot( e1, p );
		if ( MathF.Abs( det ) < 1e-12f )
			return false;

		var inv = 1 / det;
		var s = origin - a;
		u = Vector3.Dot( s, p ) * inv;
		if ( u < 0 || u > 1 )
			return false;

		var q = Vector3.Cross( s, e1 );
		v = Vector3.Dot( ray, q ) * inv;
		if ( v < 0 || u + v > 1 )
			return false;

		distance = Vector3.Dot( e2, q ) * inv;
		return distance >= 0 && distance <= 1;
	}

	/// <summary>
	/// Reads lightmap pages from <c>textures.dat</c>: the first mip level of an A8R8G8B8 bitmap (MCC's pages
	/// all are; other formats give no sample).
	/// </summary>
	public static PageReader TexturePages( TextureFile textures ) => ( bitmapTag, page ) =>
	{
		var bitmaps = new BitmapTag( bitmapTag ).Bitmaps;
		if ( page < 0 || page >= bitmaps.Count )
			return null;

		var bitmap = bitmaps[page];
		if ( bitmap.Format is not (BitmapFormat.A8R8G8B8 or BitmapFormat.X8R8G8B8) || bitmap.Type != BitmapType.Texture2D )
			return null;

		var pixels = textures.Read( bitmap.PixelDataOffset, bitmap.PixelDataSize );
		return (bitmap.Width, bitmap.Height, pixels.AsSpan( 0, bitmap.GetLevelSize( 0 ) ).ToArray());
	};
}
