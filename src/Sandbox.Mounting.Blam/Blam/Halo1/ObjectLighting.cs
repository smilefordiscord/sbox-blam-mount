using System;
using System.Collections.Generic;
using System.Linq;
using Blam.Halo1.Tags;

namespace Blam.Halo1;

// Inside the namespace so these win over s&box's global Vector2/Vector3 when this compiles into the mount.
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;

/// <summary>
/// An object's lighting, in Halo's (gamma space, 0-1) units. The light reaching a surface with
/// normal n is <c>saturate(Ambient + Light0 * saturate(dot(n, ToLight0)) + Light1 * saturate(dot(n, ToLight1)))</c>.
/// </summary>
/// <param name="Light0">The main light: the lightmap color under the object.</param>
/// <param name="ToLight0">Unit vector toward the main light (the lightmap's incident direction).</param>
/// <param name="Light1">Light bounced off the ground, which arrives from below.</param>
/// <param name="ToLight1">Unit vector toward the bounce light (down into the ground).</param>
/// <param name="Reflection">Scales the object's cube map reflections (the tint times its brightness).</param>
public readonly record struct ObjectLight( Vector3 Ambient, Vector3 Light0, Vector3 ToLight0, Vector3 Light1, Vector3 ToLight1, Vector3 Reflection );

/// <summary>
/// Per-object switches that change how Halo lights an object.
/// </summary>
[Flags]
public enum ObjectLightingFlags
{
	None = 0,

	/// <summary>Elevators: four horizontal rays instead of one down.</summary>
	RaycastSideways = 1,

	/// <summary>
	/// Objects that work their lighting out again as they move (items, elevators): only the ray
	/// from the center, no extra rays around it when it misses.
	/// </summary>
	Moving = 2,

	/// <summary>The object definition's "brighter than it should be" flag.</summary>
	Brighter = 4,

}

/// <param name="Center">The object's bounding sphere center: its root node transform applied to the definition's bounding offset.</param>
public readonly record struct ObjectLightingRequest( Vector3 Center, float Radius, ObjectLightingFlags Flags );

/// <summary>
/// What a lighting ray found: where it hit, and the values Halo reads there.
/// </summary>
/// <param name="Lightmap">Lightmap color (0-1, gamma space).</param>
/// <param name="Diffuse">The ground's base map color, from a small mip level.</param>
/// <param name="Normal">The ground's (interpolated vertex) normal.</param>
/// <param name="Incident">Unit vector toward the lightmap's dominant light.</param>
/// <param name="Directionality">How directional the light is (the interpolated length of the lightmap's incident vectors).</param>
/// <param name="LightmapPage">Index of the lightmap page in the BSP's lightmap bitmap tag.</param>
/// <param name="LightmapUv">Where on that page the lightmap was read.</param>
public sealed record GroundSample( int BspIndex, Vector3 Point, Vector3 Lightmap, Vector3 Diffuse, Vector3 Normal, Vector3 Incident, float Directionality, int LightmapPage, Vector2 LightmapUv, string Shader );

/// <summary>
/// Lights objects the way Halo does when it creates them. Nothing about object lighting is
/// stored in the level: the game casts a ray from the object into the lightmapped level and
/// derives the object's lights from what it hits.
/// </summary>
/// <remarks>
/// <para>A ray goes 10 world units straight down from the object's bounding sphere center
/// (elevators instead cast four rays sideways) against the BSP's collision geometry. Front
/// facing surfaces stop it, and two-sided ones from either side. The hit is matched to the
/// lightmapped render triangle in the same plane that contains it; a two-sided collision surface
/// with none (glass, fences) lets the ray continue past it. Only <c>shader_environment</c> surfaces
/// light objects. At the hit, interpolated across the triangle: the lightmap color C (nearest
/// texel), the ground's base map color D (nearest texel of a small mip), the surface normal N, and
/// the lightmap vertices' incident vectors (direction I, length k).</para>
///
/// <para>The lighting: ambient 0.4 C + 0.03, a main light of color C from I, and a bounce light of
/// lum(C) D arriving from below along N. Reflections are scaled by sat(3 D + 0.5) sat(2 C + 0.25)
/// times sat(1.5 lum(C) + 0.25). Objects marked "brighter than it should be" have each color
/// brightened.</para>
///
/// <para>Four more rays start at (±0.707, ±0.707) times the bounding radius around the center,
/// and the lighting of every ray that hits, the center's included, is averaged (objects that move,
/// like items, only use the center). If none hit, the BSP's default lighting is used, or the
/// radiosity's defaults where the BSP has none. That's the Xbox game's behavior; MCC's PC-derived
/// build skips the corner rays when the center hits, which leaves a boulder or tree trunk lit only
/// by its own baked shadow.</para>
/// </remarks>
public sealed class ObjectLightingSampler
{
	/// <summary>
	/// The length of a lighting ray, in world units.
	/// </summary>
	public const float RayLength = 10f;

	// The game's object_light_ambient_base, object_light_ambient_scale and
	// object_light_secondary_scale.
	const float AmbientBase = 0.03f;
	const float AmbientScale = 0.4f;
	const float SecondaryScale = 1f;

	// Mip level picked for the ground's base map: round((1 - DiffuseDetail) * mipmap count).
	const float DiffuseDetail = 0.3f;

	const float Diagonal = 0.707107f;
	const float CellSize = 4f;

	/// <summary>
	/// A lighting result as the game keeps it, before it's reduced to <see cref="ObjectLight"/>.
	/// Directions are the ones the light travels in.
	/// </summary>
	struct Lighting
	{
		public Vector3 Ambient, Color0, Direction0, Color1, Direction1, ReflectionColor;
		public float ReflectionBrightness;

		public static Lighting operator +( Lighting a, Lighting b ) => new()
		{
			Ambient = a.Ambient + b.Ambient,
			Color0 = a.Color0 + b.Color0,
			Direction0 = a.Direction0 + b.Direction0,
			Color1 = a.Color1 + b.Color1,
			Direction1 = a.Direction1 + b.Direction1,
			ReflectionColor = a.ReflectionColor + b.ReflectionColor,
			ReflectionBrightness = a.ReflectionBrightness + b.ReflectionBrightness,
		};

		public ObjectLight ToObjectLight() => new( Ambient, Color0, -Direction0, Color1, -Direction1, ReflectionColor * ReflectionBrightness );
	}

	// The radiosity's defaults, for a BSP whose own default lighting is empty.
	static readonly Lighting RadiosityDefaults = new()
	{
		Ambient = new Vector3( 0.2f ),
		Color0 = Vector3.One,
		Direction0 = new Vector3( -0.577f ),
		Color1 = new Vector3( 0.4f, 0.4f, 0.5f ),
		Direction1 = Vector3.UnitZ,
		ReflectionColor = Vector3.One,
		ReflectionBrightness = 0.5f,
	};

	sealed class Texels
	{
		public int Width, Height;
		public byte[] Rgba;
	}

	sealed class Collision
	{
		public CollisionSurface Surface;
		public Vector3 Min, Max;
	}

	sealed class Triangle
	{
		public Vector3 A, B, C;
		public Vector3 NormalA, NormalB, NormalC;
		public Vector2 UvA, UvB, UvC;
		public Vector2 LightmapUvA, LightmapUvB, LightmapUvC;
		public Vector3 IncidentA, IncidentB, IncidentC;
		public Vector3 Plane;
		public float PlaneDistance;
		public Texels Lightmap;
		public int LightmapPage;
		public string Shader;

		/// <summary>The base map of the triangle's shader_environment, or null for other shaders or none.</summary>
		public Texels BaseMap;

		/// <summary>The same base map at the mip level particles are tinted from.</summary>
		public Texels ParticleBaseMap;
		public bool Environment;
	}

	sealed class Bsp
	{
		public int Index;
		public Vector3 Min, Max;
		public Lighting Defaults;
		public readonly List<Collision> Collisions = new();
		public readonly Grid CollisionGrid = new();
		public readonly List<Triangle> Triangles = new();
		public readonly Grid TriangleGrid = new();
	}

	readonly List<Bsp> _bsps = new();

	public ObjectLightingSampler( CacheFile cache, IEnumerable<StructureBspTag> bsps )
	{
		var baseMaps = new Dictionary<(uint, int, int), Texels>();
		var index = 0;
		foreach ( var tag in bsps )
			_bsps.Add( Load( cache, tag, index++, baseMaps ) );
	}

	static Bsp Load( CacheFile cache, StructureBspTag tag, int index, Dictionary<(uint, int, int), Texels> baseMaps )
	{
		var data = tag.Data;
		var t = tag.TagAddress;
		var bsp = new Bsp { Index = index, Min = tag.WorldBoundsMin, Max = tag.WorldBoundsMax };

		// The BSP's default lighting (a render_lighting at 0x2C), unless its ambient is empty.
		bsp.Defaults = data.F32( t + 0x2C ) == 0 ? RadiosityDefaults : new Lighting
		{
			Ambient = data.Vec3( t + 0x2C ),
			Color0 = data.Vec3( t + 0x3C ),
			Direction0 = data.Vec3( t + 0x48 ),
			Color1 = data.Vec3( t + 0x54 ),
			Direction1 = data.Vec3( t + 0x60 ),
			ReflectionBrightness = data.F32( t + 0x78 ),
			ReflectionColor = data.Vec3( t + 0x7C ),
		};

		foreach ( var surface in tag.ReadCollisionSurfaces() )
		{
			if ( surface.Corners.Length < 3 )
				continue;

			var collision = new Collision
			{
				Surface = surface,
				Min = surface.Corners.Aggregate( Vector3.Min ),
				Max = surface.Corners.Aggregate( Vector3.Max ),
			};
			bsp.CollisionGrid.Add( bsp.Collisions.Count, collision.Min, collision.Max );
			bsp.Collisions.Add( collision );
		}

		// Lightmap pages are R5G6B5, read at full size.
		var pages = new Dictionary<int, Texels>();
		var lightmapBitmaps = tag.LightmapBitmaps is null ? null : new BitmapTag( cache, tag.LightmapBitmaps ).Bitmaps;

		var lightmapIndex = 0;
		foreach ( var lightmapEntry in data.Block( t + 0x104, 0x20 ) )
		{
			var lightmap = tag.Lightmaps[lightmapIndex++];
			if ( lightmap.BitmapIndex < 0 || lightmapBitmaps is null || lightmap.BitmapIndex >= lightmapBitmaps.Count )
				continue;

			if ( !pages.TryGetValue( lightmap.BitmapIndex, out var page ) )
				pages[lightmap.BitmapIndex] = page = ReadLevel( cache, lightmapBitmaps[lightmap.BitmapIndex], 0 );
			if ( page is null )
				continue;

			var materialIndex = 0;
			foreach ( var materialEntry in data.Block( lightmapEntry + 0x14, 0x100 ) )
			{
				var material = lightmap.Materials[materialIndex++];
				var lightmapVertices = tag.ReadLightmapVertices( material );
				if ( lightmapVertices is null )
					continue;

				var environment = material.Shader?.Class == TagClasses.ShaderEnvironment;
				var permutation = data.I16( materialEntry + 0x10 );
				var baseMap = environment ? BaseMap( cache, material.Shader, permutation, baseMaps, particles: false ) : null;
				var particleBaseMap = environment ? BaseMap( cache, material.Shader, permutation, baseMaps, particles: true ) : null;

				var vertices = tag.ReadRenderedVertices( material );
				for ( var s = 0; s < material.SurfaceCount; s++ )
				{
					var i0 = tag.Surfaces[(material.FirstSurface + s) * 3];
					var i1 = tag.Surfaces[(material.FirstSurface + s) * 3 + 1];
					var i2 = tag.Surfaces[(material.FirstSurface + s) * 3 + 2];
					if ( i0 >= vertices.Length || i1 >= vertices.Length || i2 >= vertices.Length )
						continue;

					var tri = new Triangle
					{
						A = vertices[i0].Position, B = vertices[i1].Position, C = vertices[i2].Position,
						NormalA = vertices[i0].Normal, NormalB = vertices[i1].Normal, NormalC = vertices[i2].Normal,
						UvA = vertices[i0].TexCoord, UvB = vertices[i1].TexCoord, UvC = vertices[i2].TexCoord,
						LightmapUvA = lightmapVertices[i0].TexCoord, LightmapUvB = lightmapVertices[i1].TexCoord, LightmapUvC = lightmapVertices[i2].TexCoord,
						IncidentA = lightmapVertices[i0].Normal, IncidentB = lightmapVertices[i1].Normal, IncidentC = lightmapVertices[i2].Normal,
						Lightmap = page,
						LightmapPage = lightmap.BitmapIndex,
						Shader = material.Shader?.Path,
						BaseMap = baseMap,
						ParticleBaseMap = particleBaseMap,
						Environment = environment,
					};

					var cross = Vector3.Cross( tri.B - tri.A, tri.C - tri.A );
					if ( cross.LengthSquared() < 1e-12f )
						continue;
					tri.Plane = Vector3.Normalize( cross );
					tri.PlaneDistance = Vector3.Dot( tri.Plane, tri.A );

					bsp.TriangleGrid.Add( bsp.Triangles.Count, Vector3.Min( tri.A, Vector3.Min( tri.B, tri.C ) ), Vector3.Max( tri.A, Vector3.Max( tri.B, tri.C ) ) );
					bsp.Triangles.Add( tri );
				}
			}
		}

		return bsp;
	}

	/// <summary>
	/// A shader_environment's base map, at the mip level the game samples for object lighting (or,
	/// with <paramref name="particles"/>, for tinting particles). The material's shader permutation
	/// picks the bitmap (modulo the bitmap count).
	/// </summary>
	static Texels BaseMap( CacheFile cache, TagEntry shader, int permutation, Dictionary<(uint, int, int), Texels> cacheByBitmap, bool particles )
	{
		var tag = cache.GetTagReference( cache.TagData, shader.Address + 0x88 );
		if ( tag is null )
			return null;

		var bitmaps = new BitmapTag( cache, tag ).Bitmaps;
		if ( bitmaps.Count == 0 )
			return null;

		var index = ((permutation % bitmaps.Count) + bitmaps.Count) % bitmaps.Count;
		var bitmap = bitmaps[index];
		var level = 0;
		if ( particles )
			level = ParticleLighting.DiffuseLevel( bitmap.MipmapCount );
		else if ( DiffuseDetail < 1 && bitmap.MipmapCount > 0 )
			level = (int)((1 - DiffuseDetail) * bitmap.MipmapCount + 0.5f);

		if ( cacheByBitmap.TryGetValue( (tag.Id, index, level), out var texels ) )
			return texels;

		texels = ReadLevel( cache, bitmap, level );
		cacheByBitmap[(tag.Id, index, level)] = texels;
		return texels;
	}

	/// <summary>
	/// One mip level of a 2D bitmap's first face, as RGBA8888. Null if the format can't be read here.
	/// </summary>
	static Texels ReadLevel( CacheFile cache, BitmapData bitmap, int level )
	{
		level = Math.Clamp( level, 0, bitmap.MipmapCount );
		var width = Math.Max( 1, bitmap.Width >> level );
		var height = Math.Max( 1, bitmap.Height >> level );

		var offset = 0;
		for ( var l = 0; l < level; l++ )
			offset += bitmap.GetLevelSize( l );

		var pixels = BitmapTag.ReadPixels( cache, bitmap );
		var size = bitmap.GetLevelSize( level );
		if ( offset + size > pixels.Length )
			return null;

		var source = pixels.AsSpan( offset, size );
		var rgba = new byte[width * height * 4];
		if ( bitmap.Format is BitmapFormat.Dxt1 or BitmapFormat.Dxt3 or BitmapFormat.Dxt5 )
			BitmapDecoder.DxtColorToRgba8888( bitmap.Format, source, rgba, width, height );
		else if ( BitmapDecoder.CanDecode( bitmap.Format ) )
			BitmapDecoder.ToRgba8888( bitmap.Format, source, rgba, width * height );
		else
			return null;

		return new Texels { Width = width, Height = height, Rgba = rgba };
	}

	/// <summary>
	/// Indices of things (collision surfaces, triangles) by the columns of <see cref="CellSize"/>
	/// world units they reach into, to find what a ray may hit.
	/// </summary>
	/// <remarks>
	/// Things that reach into more than <see cref="LargeCells"/> columns (outdoor levels' sea floor,
	/// hundreds of world units across) are kept in one list instead, which every lookup goes
	/// through: listed in every column, Death Island's took 2.3 million entries in 870,000 columns
	/// (about 100 MB). They're merged in by index, so lookups give the same things in the same
	/// order as if they were in every column.
	/// </remarks>
	sealed class Grid
	{
		const int LargeCells = 256;

		readonly Dictionary<long, List<int>> _cells = new();
		readonly List<int> _large = new();

		public int CellCount => _cells.Count;
		public long EntryCount => _cells.Values.Sum( l => (long)l.Count ) + _large.Count;

		public void Add( int index, Vector3 min, Vector3 max )
		{
			if ( (long)(Cell( max.X ) - Cell( min.X ) + 1) * (Cell( max.Y ) - Cell( min.Y ) + 1) > LargeCells )
			{
				_large.Add( index );
				return;
			}

			for ( var x = Cell( min.X ); x <= Cell( max.X ); x++ )
			for ( var y = Cell( min.Y ); y <= Cell( max.Y ); y++ )
			{
				if ( !_cells.TryGetValue( Key( x, y ), out var list ) )
					_cells[Key( x, y )] = list = new List<int>();
				list.Add( index );
			}
		}

		public IEnumerable<int> Candidates( Vector3 from, Vector3 to )
		{
			var min = Vector3.Min( from, to );
			var max = Vector3.Max( from, to );
			var seen = new HashSet<int>();
			for ( var x = Cell( min.X ); x <= Cell( max.X ); x++ )
			for ( var y = Cell( min.Y ); y <= Cell( max.Y ); y++ )
			{
				_cells.TryGetValue( Key( x, y ), out var list );
				var a = 0;
				var b = 0;
				var count = list?.Count ?? 0;
				while ( a < count || b < _large.Count )
				{
					var i = b >= _large.Count || (a < count && list[a] < _large[b]) ? list[a++] : _large[b++];
					if ( seen.Add( i ) )
						yield return i;
				}
			}
		}

		static int Cell( float v ) => (int)MathF.Floor( v / CellSize );
		static long Key( int x, int y ) => ((long)x << 32) ^ (uint)y;
	}

	/// <summary>
	/// Roughly how much memory the sampler holds (bytes): its triangles, collision surfaces,
	/// lookup grids and texels.
	/// </summary>
	public long ApproximateSize
	{
		get
		{
			var texels = _bsps.SelectMany( b => b.Triangles ).SelectMany( t => new[] { t.Lightmap, t.BaseMap, t.ParticleBaseMap } ).Where( t => t is not null ).Distinct().Sum( t => (long)t.Rgba.Length );
			return texels + _bsps.Sum( b => b.Triangles.Count * 300L + b.Collisions.Count * 200L
				+ (b.CollisionGrid.EntryCount + b.TriangleGrid.EntryCount) * 4 + (b.CollisionGrid.CellCount + b.TriangleGrid.CellCount) * 100L );
		}
	}

	/// <summary>
	/// Lighting for each object.
	/// </summary>
	public ObjectLight[] LightObjects( IReadOnlyList<ObjectLightingRequest> objects )
	{
		var result = new ObjectLight[objects.Count];

		// Each object only reads the sampler, so they're worked out in parallel.
		System.Threading.Tasks.Parallel.For( 0, objects.Count, i => result[i] = Light( objects[i] ) );
		return result;
	}

	/// <summary>
	/// Lighting for one object.
	/// </summary>
	/// <remarks>
	/// The game lights an object against the BSP loaded when it's created. Here every BSP whose
	/// bounds contain the object is tried in order, and the first one that lights it is used.
	/// </remarks>
	public ObjectLight Light( ObjectLightingRequest request )
	{
		if ( _bsps.Count == 0 )
			return RadiosityDefaults.ToObjectLight();

		// The BSPs holding the center, or every BSP if none does.
		var anyHolds = false;
		foreach ( var bsp in _bsps )
			anyHolds |= Contains( bsp, request.Center );

		Lighting? first = null;
		foreach ( var bsp in _bsps )
		{
			if ( anyHolds && !Contains( bsp, request.Center ) )
				continue;
			if ( LightInBsp( bsp, request, out var lighting ) )
				return lighting.ToObjectLight();
			first ??= lighting;
		}

		return first.Value.ToObjectLight();
	}

	static bool Contains( Bsp bsp, Vector3 p ) =>
		p.X >= bsp.Min.X && p.Y >= bsp.Min.Y && p.Z >= bsp.Min.Z && p.X <= bsp.Max.X && p.Y <= bsp.Max.Y && p.Z <= bsp.Max.Z;

	/// <summary>
	/// The bounds of every BSP's collision geometry together (world units), or null if there is
	/// none. Tighter than the BSPs' world bounds, which on the big outdoor maps (Death Island, b30)
	/// reach far out over the sea.
	/// </summary>
	public (Vector3 Min, Vector3 Max)? Bounds
	{
		get
		{
			var collisions = _bsps.SelectMany( b => b.Collisions ).ToList();
			return collisions.Count == 0 ? null
				: (collisions.Select( c => c.Min ).Aggregate( Vector3.Min ), collisions.Select( c => c.Max ).Aggregate( Vector3.Max ));
		}
	}

	/// <summary>
	/// The lighting of a moving object whose bounding sphere center is at <paramref name="point"/>:
	/// the ray from the point only (no extra rays around it), in the first BSP containing the point
	/// whose ray reaches a lit surface. False if none does.
	/// </summary>
	public bool TryLightPoint( Vector3 point, out ObjectLight light )
	{
		foreach ( var bsp in _bsps )
		{
			if ( Contains( bsp, point ) && Prepare( bsp, point, ObjectLightingFlags.Moving, out var lighting ) )
			{
				light = lighting.ToObjectLight();
				return true;
			}
		}

		light = default;
		return false;
	}

	/// <summary>
	/// The game's object lighting in one BSP. Returns whether any ray hit; the lighting is the
	/// BSP's defaults if none did.
	/// </summary>
	static bool LightInBsp( Bsp bsp, ObjectLightingRequest request, out Lighting lighting )
	{
		var hit = Prepare( bsp, request.Center, request.Flags, out lighting );
		if ( request.Flags.HasFlag( ObjectLightingFlags.Moving ) )
			return hit;

		// The center sample (if it hit) and every corner sample that hits are averaged.
		var sum = hit ? lighting : new Lighting();
		var count = hit ? 1 : 0;
		var last = lighting;
		for ( var s = 0; s < 4; s++ )
		{
			var offset = new Vector3( ((s & 1) != 0 ? Diagonal : -Diagonal) * request.Radius, ((s & 2) != 0 ? Diagonal : -Diagonal) * request.Radius, 0 );
			if ( Prepare( bsp, request.Center + offset, request.Flags, out var sample ) )
			{
				sum += sample;
				count++;
			}
			last = sample;
		}

		if ( count > 1 )
		{
			var scale = 1f / count;
			sum = new Lighting
			{
				Ambient = sum.Ambient * scale,
				Color0 = sum.Color0 * scale,
				Direction0 = SafeNormalize( sum.Direction0 * scale ),
				Color1 = sum.Color1 * scale,
				Direction1 = SafeNormalize( sum.Direction1 * scale ),
				ReflectionColor = sum.ReflectionColor * scale,
				ReflectionBrightness = sum.ReflectionBrightness * scale,
			};
		}

		lighting = count > 0 ? sum : last;
		return count > 0;
	}

	static Vector3 SafeNormalize( Vector3 v ) => v.LengthSquared() > 0 ? Vector3.Normalize( v ) : v;

	// The rays for one sample point: straight down, or for elevators the four horizontal ones.
	static readonly Vector3[] DownRay = [new( 0, 0, -RayLength )];
	static readonly Vector3[] SidewaysRays = [new( -RayLength, 0, 0 ), new( RayLength, 0, 0 ), new( 0, -RayLength, 0 ), new( 0, RayLength, 0 )];

	/// <summary>
	/// Lighting from one sample point: the first ray that reaches a lit shader_environment surface.
	/// Otherwise the BSP's defaults, and false.
	/// </summary>
	static bool Prepare( Bsp bsp, Vector3 point, ObjectLightingFlags flags, out Lighting lighting )
	{
		lighting = bsp.Defaults;
		foreach ( var ray in flags.HasFlag( ObjectLightingFlags.RaycastSideways ) ? SidewaysRays : DownRay )
		{
			if ( !Cast( bsp, point, ray, out var tri, out var u, out var v ) )
				continue;

			// The first surface the ray reaches decides; one that doesn't light objects ends the search.
			if ( !tri.Environment || tri.BaseMap is null )
				return false;

			var sample = SampleTriangle( bsp, tri, point, u, v );
			lighting = FromGround( sample, flags );
			return true;
		}

		return false;
	}

	/// <summary>
	/// The ground under a point: the first lightmapped surface a ray reaches, in the first BSP that
	/// has one, whatever its shader. Null if there's none.
	/// </summary>
	public GroundSample Sample( Vector3 origin, Vector3 vector )
	{
		foreach ( var bsp in _bsps )
		{
			if ( Cast( bsp, origin, vector, out var tri, out var u, out var v ) )
				return SampleTriangle( bsp, tri, origin, u, v );
		}

		return null;
	}

	/// <summary>
	/// The ground colors a particle born at <paramref name="point"/> is lit and tinted by (see
	/// <see cref="ParticleLighting"/>): the lightmap and base map texels where a ray 10 world units
	/// straight down first reaches a lightmapped surface, in the first BSP containing the point
	/// where it reaches one. False if it reaches none, or the surface isn't a shader_environment
	/// with a base map.
	/// </summary>
	public bool TryParticleGround( Vector3 point, out Vector3 lightmap, out Vector3 diffuse )
	{
		lightmap = diffuse = default;
		foreach ( var bsp in _bsps.Where( b => Contains( b, point ) ).Concat( _bsps.Where( b => !Contains( b, point ) ) ) )
		{
			if ( !Cast( bsp, point, DownRay[0], out var tri, out var u, out var v ) )
				continue;
			if ( !tri.Environment || tri.ParticleBaseMap is null )
				return false;

			lightmap = Texel( tri.Lightmap, tri.LightmapUvA + (tri.LightmapUvB - tri.LightmapUvA) * u + (tri.LightmapUvC - tri.LightmapUvA) * v );
			diffuse = Texel( tri.ParticleBaseMap, tri.UvA + (tri.UvB - tri.UvA) * u + (tri.UvC - tri.UvA) * v );
			return true;
		}

		return false;
	}

	/// <summary>
	/// What the game reads at a point on a triangle (u and v weight its second and third corners).
	/// </summary>
	static GroundSample SampleTriangle( Bsp bsp, Triangle tri, Vector3 origin, float u, float v )
	{
		Vector2 Lerp2( Vector2 a, Vector2 b, Vector2 c ) => a + (b - a) * u + (c - a) * v;
		Vector3 Lerp3( Vector3 a, Vector3 b, Vector3 c ) => a + (b - a) * u + (c - a) * v;
		float Lerp1( float a, float b, float c ) => a + (b - a) * u + (c - a) * v;

		var lightmapUv = Lerp2( tri.LightmapUvA, tri.LightmapUvB, tri.LightmapUvC );
		var lightmap = Texel( tri.Lightmap, lightmapUv );
		var diffuse = tri.BaseMap is null ? Vector3.One : Texel( tri.BaseMap, Lerp2( tri.UvA, tri.UvB, tri.UvC ) );
		var normal = SafeNormalize( Lerp3( tri.NormalA, tri.NormalB, tri.NormalC ) );

		// Each corner's incident vector is split into its direction and length, which are
		// interpolated separately.
		var la = tri.IncidentA.Length();
		var lb = tri.IncidentB.Length();
		var lc = tri.IncidentC.Length();
		var incident = SafeNormalize( Lerp3( SafeNormalize( tri.IncidentA ), SafeNormalize( tri.IncidentB ), SafeNormalize( tri.IncidentC ) ) );

		return new GroundSample( bsp.Index, Lerp3( tri.A, tri.B, tri.C ), lightmap, diffuse, normal, incident, Lerp1( la, lb, lc ), tri.LightmapPage, lightmapUv, tri.Shader );
	}

	/// <summary>
	/// The game's lighting for an object above a ground sample.
	/// </summary>
	static Lighting FromGround( GroundSample ground, ObjectLightingFlags flags )
	{
		var c = ground.Lightmap;
		var d = ground.Diffuse;
		var luminance = c.X * 0.299f + c.Y * 0.587f + c.Z * 0.114f;

		var lighting = new Lighting
		{
			Ambient = c * AmbientScale + new Vector3( AmbientBase ),
			Color0 = c,
			Direction0 = -ground.Incident,
			Color1 = d * (SecondaryScale * luminance),
			Direction1 = ground.Normal,
			ReflectionColor = Saturate( d * 3 + new Vector3( 0.5f ) ) * Saturate( c * 2 + new Vector3( 0.25f ) ),
			ReflectionBrightness = Math.Clamp( luminance * 1.5f + 0.25f, 0, 1 ),
		};

		if ( flags.HasFlag( ObjectLightingFlags.Brighter ) )
		{
			lighting.Ambient = Brighten( lighting.Ambient, 0.2f );
			lighting.Color0 = Brighten( lighting.Color0, 0.3f );
			lighting.Color1 = Brighten( lighting.Color1, 0.2f );
			lighting.ReflectionColor = Brighten( lighting.ReflectionColor, 0.5f );
			lighting.ReflectionBrightness = 1;
		}

		return lighting;
	}

	static Vector3 Saturate( Vector3 v ) => Vector3.Clamp( v, Vector3.Zero, Vector3.One );

	/// <summary>
	/// Scales a color so its brightest channel goes up by <paramref name="amount"/> times itself,
	/// to at least <paramref name="amount"/> and at most 1.
	/// </summary>
	static Vector3 Brighten( Vector3 color, float amount )
	{
		var max = MathF.Max( color.X, MathF.Max( color.Y, color.Z ) );
		if ( max == 0 )
			return color;

		var bright = (1 + amount) * max;
		if ( bright > 1 )
			return color / max;
		if ( amount > bright )
			return color * (amount / max);
		return color * (1 + amount);
	}

	/// <summary>
	/// Nearest texel, wrapping.
	/// </summary>
	static Vector3 Texel( Texels texels, Vector2 uv )
	{
		static int Coordinate( float t, int size )
		{
			var x = size * t - 0.5f;
			var i = (int)(x + (x >= 0 ? 0.5f : -0.5f));
			return (size & (size - 1)) == 0 ? i & (size - 1) : ((i % size) + size) % size;
		}

		var o = (Coordinate( uv.Y, texels.Height ) * texels.Width + Coordinate( uv.X, texels.Width )) * 4;
		return new Vector3( texels.Rgba[o], texels.Rgba[o + 1], texels.Rgba[o + 2] ) / 255f;
	}

	/// <summary>
	/// The game's lighting ray: the collision geometry decides where it stops, and the lightmapped
	/// render triangle at that spot is what gets sampled. A two-sided collision surface without one
	/// lets the ray go on (starting again just past it, at full length).
	/// </summary>
	static bool Cast( Bsp bsp, Vector3 origin, Vector3 vector, out Triangle triangle, out float u, out float v )
	{
		triangle = null;
		u = v = 0;
		var start = origin;

		for ( var guard = 0; guard < 64; guard++ )
		{
			if ( !FirstCollision( bsp, start, vector, out var surface, out var t ) )
				return false;

			var point = start + vector * t;
			if ( RenderTriangleAt( bsp, point, surface, out triangle, out u, out v ) )
				return true;
			if ( !surface.TwoSided )
				return false;

			start = point + vector * (1f / 4096);
		}

		return false;
	}

	/// <summary>
	/// The first collision surface along a segment that faces it, or any two-sided one.
	/// </summary>
	static bool FirstCollision( Bsp bsp, Vector3 start, Vector3 vector, out CollisionSurface surface, out float t )
	{
		surface = null;
		t = float.MaxValue;

		foreach ( var i in bsp.CollisionGrid.Candidates( start, start + vector ) )
		{
			var collision = bsp.Collisions[i];
			var s = collision.Surface;
			var facing = Vector3.Dot( s.Normal, vector );
			if ( facing == 0 || (facing > 0 && !s.TwoSided) )
				continue;

			var hit = (s.Distance - Vector3.Dot( s.Normal, start )) / facing;
			if ( hit < 0 || hit > 1 || hit >= t )
				continue;

			var point = start + vector * hit;
			if ( !InPolygon( s, point ) )
				continue;

			t = hit;
			surface = s;
		}

		return surface is not null;
	}

	static bool InPolygon( CollisionSurface s, Vector3 point )
	{
		const float Tolerance = 1e-4f;
		var corners = s.Corners;
		for ( var i = 0; i < corners.Length; i++ )
		{
			var a = corners[i];
			var b = corners[(i + 1) % corners.Length];
			if ( Vector3.Dot( Vector3.Cross( b - a, point - a ), s.Normal ) < -Tolerance * Vector3.Distance( a, b ) )
				return false;
		}

		return true;
	}

	/// <summary>
	/// The lightmapped render triangle lying in a collision surface's plane that contains a point,
	/// with the point's barycentric weights for its second and third corners.
	/// </summary>
	static bool RenderTriangleAt( Bsp bsp, Vector3 point, CollisionSurface surface, out Triangle triangle, out float u, out float v )
	{
		const float PlaneTolerance = 0.01f;
		const float Tolerance = 1e-4f;

		foreach ( var i in bsp.TriangleGrid.Candidates( point, point ) )
		{
			var tri = bsp.Triangles[i];
			if ( MathF.Abs( Vector3.Dot( tri.Plane, surface.Normal ) ) < 0.999f )
				continue;
			if ( MathF.Abs( Vector3.Dot( tri.Plane, point ) - tri.PlaneDistance ) > PlaneTolerance )
				continue;

			// Barycentric weights in the triangle's plane.
			var e1 = tri.B - tri.A;
			var e2 = tri.C - tri.A;
			var p = point - tri.A;
			var d11 = Vector3.Dot( e1, e1 );
			var d12 = Vector3.Dot( e1, e2 );
			var d22 = Vector3.Dot( e2, e2 );
			var p1 = Vector3.Dot( p, e1 );
			var p2 = Vector3.Dot( p, e2 );
			var denominator = d11 * d22 - d12 * d12;
			if ( denominator == 0 )
				continue;

			var bu = (d22 * p1 - d12 * p2) / denominator;
			var bv = (d11 * p2 - d12 * p1) / denominator;
			if ( bu < -Tolerance || bv < -Tolerance || bu + bv > 1 + Tolerance )
				continue;

			triangle = tri;
			u = bu;
			v = bv;
			return true;
		}

		triangle = null;
		u = v = 0;
		return false;
	}

	/// <summary>
	/// What the game lights a placed object from: its bounding sphere (the definition's bounding
	/// offset turned with the object), and the switches its kind and definition set. Items and
	/// elevators redo their lighting as they move, so they only use the ray from the center;
	/// elevators cast theirs sideways.
	/// </summary>
	public static ObjectLightingRequest Request( CacheFile cache, ObjectPlacement placement )
	{
		var forward = placement.Forward;
		var up = placement.Up;
		var left = Vector3.Cross( up, forward );
		var rotation = new System.Numerics.Matrix4x4(
			forward.X, forward.Y, forward.Z, 0,
			left.X, left.Y, left.Z, 0,
			up.X, up.Y, up.Z, 0,
			0, 0, 0, 1 );
		// The game transforms the bounding offset by the root node's matrix, which includes the
		// model's default pose for it: b30's boulder_doublewide root sits 0.29 wu up, and without
		// it the corner rays started inside the sand, missed, and left a rock lit only by its own
		// baked shadow. (Origin offset and a physics center of mass also go into that matrix;
		// not applied yet.)
		var local = BoundingOffset( cache, placement.Object );
		if ( RootNode( cache, placement.Object ) is { } root )
			local = root.Translation + Vector3.Transform( local, root.Rotation );
		var center = placement.Position + Vector3.TransformNormal( local, rotation );

		var flags = DefinitionFlags( cache, placement.Object );
		if ( placement.Kind is "weapon" or "equipment" )
			flags |= ObjectLightingFlags.Moving;
		if ( MachineTag.IsElevator( cache, placement.Object ) )
			flags |= ObjectLightingFlags.Moving | ObjectLightingFlags.RaycastSideways;

		return new ObjectLightingRequest( center, BoundingRadius( cache, placement.Object ), flags );
	}

	/// <summary>
	/// The default pose of an object's model's root node, or null if it has no gbxmodel.
	/// </summary>
	internal static ModelNode RootNode( CacheFile cache, TagEntry objectTag )
	{
		var model = ObjectTags.GetObjectModel( cache, objectTag );
		if ( model is null || model.Class != TagClasses.GbxModel )
			return null;

		var roots = RootNodes.GetOrCreateValue( cache );
		lock ( roots )
		{
			if ( !roots.TryGetValue( model.Id, out var root ) )
				roots[model.Id] = root = new GbxModelTag( cache, model ).Nodes.FirstOrDefault();
			return root;
		}
	}

	// Per map, so a closed map's entries go with it.
	static readonly System.Runtime.CompilerServices.ConditionalWeakTable<CacheFile, Dictionary<uint, ModelNode>> RootNodes = new();

	/// <summary>
	/// An object's bounding radius (world units), at 0x4 of the shared object definition.
	/// </summary>
	public static float BoundingRadius( CacheFile cache, TagEntry objectTag ) => cache.TagData.F32( objectTag.Address + 0x4 );

	/// <summary>
	/// The offset of an object's bounding sphere center from its origin, in the object's own
	/// space, at 0x8 of the shared object definition.
	/// </summary>
	public static Vector3 BoundingOffset( CacheFile cache, TagEntry objectTag ) => cache.TagData.Vec3( objectTag.Address + 0x8 );

	/// <summary>
	/// The lighting flags an object definition sets: "brighter than it should be" (bit 2 of the
	/// flags at 0x2).
	/// </summary>
	public static ObjectLightingFlags DefinitionFlags( CacheFile cache, TagEntry objectTag )
	{
		var flags = ObjectLightingFlags.None;
		if ( (cache.TagData.U16( objectTag.Address + 0x2 ) & 0x4) != 0 )
			flags |= ObjectLightingFlags.Brighter;
		return flags;
	}
}
