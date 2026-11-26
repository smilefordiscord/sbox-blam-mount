using System;
using System.Collections.Generic;
using System.Linq;
using Blam.Halo2.Tags;

namespace Blam.Halo2;

// Inside the namespace so these win over s&box's global vector types when this compiles into the mount.
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;

public readonly record struct DecalVertex( Vector3 Position, Vector2 TexCoord );

/// <summary>
/// A decal laid onto the level: triangles (world units) with their texture coordinates on one bitmap of the decal's
/// bitmap tag, the decal's colour (rgb 0-1), and for a decal an effect leaves, its life and fade-out (seconds).
/// </summary>
public sealed record PlacedDecal( DecalTag Decal, int BitmapIndex, Vector3 Color, IReadOnlyList<DecalVertex> Triangles, float Lifetime, float Decay );

/// <summary>
/// Lays Halo 2 decals onto a level's collision surfaces as MCC's decal system does when a cluster's placed decals are
/// created: a ray along the decal's direction finds the surface and the point,
/// the direction gives the decal's axes, a random stream seeded from the position picks the rotation, sprite, radius
/// and colour, and the decal's rectangle is clipped onto the surfaces around the point (flooding across the edges it
/// crosses), bending over corners like paper for every type but painted signs.
/// </summary>
public sealed class DecalGeometry
{
	/// <summary>
	/// One collision BSP's surfaces in world space (the structure's, or an instance's placed by its transform).
	/// </summary>
	sealed class SurfaceSet
	{
		public readonly List<Vector3> Vertices = new();
		public readonly List<Surface> Surfaces = new();
	}

	sealed class Surface
	{
		public int[] Corners;
		public Vector3 Normal;
		public float Distance;
		public byte Flags;

		/// <summary>
		/// Per edge (corner i to i + 1), the surface on the other side, or -1.
		/// </summary>
		public int[] Neighbours;
	}

	readonly List<SurfaceSet> _sets = new();

	/// <summary>
	/// Per decal type: the most a surface's normal may turn from the decal's and still take it, how far it may turn
	/// for the decal to fold onto it (degrees), how far from the decal's centre an edge may be to flood across it (in
	/// radii), and whether the decal folds at all.
	/// </summary>
	static readonly (float Angle, float FoldAngle, float Reach, bool Fold)[] TypeRules =
	[
		(40, 110, 1.5f, true), // scratch
		(40, 110, 1.5f, true), // splatter
		(40, 110, 1.5f, true), // burn
		(10, 10, 1.5f, false), // painted sign
	];

	/// <summary>
	/// Surface flags that keep a decal off: two-sided, invisible, breakable, conveyor.
	/// </summary>
	const byte RejectedFlags = 0x2B;

	const float Lift = 1f / 256;

	public DecalGeometry( StructureBspTag bsp )
	{
		if ( bsp.Collision is not null )
			_sets.Add( Build( bsp.Collision, null ) );

		foreach ( var instance in bsp.Instances )
		{
			if ( instance.Definition >= 0 && instance.Definition < bsp.DefinitionCollision.Count && bsp.DefinitionCollision[instance.Definition] is { } collision )
				_sets.Add( Build( collision, instance ) );
		}
	}

	static SurfaceSet Build( CollisionBsp collision, InstancedGeometryInstance instance )
	{
		var set = new SurfaceSet();
		foreach ( var v in collision.Vertices )
			set.Vertices.Add( instance is null ? v : instance.Transform( v ) );

		var edges = new Dictionary<(int, int), List<(int Surface, int Edge)>>();
		foreach ( var s in collision.Surfaces )
		{
			if ( s.Corners.Length < 3 )
			{
				set.Surfaces.Add( new Surface { Corners = [], Neighbours = [], Flags = 0xFF } );
				continue;
			}

			var (normal, distance) = s.Plane < collision.Planes.Count ? collision.Planes[s.Plane] : (Vector3.UnitZ, 0f);
			if ( s.PlaneFlipped )
				normal = -normal;
			if ( instance is not null )
				normal = instance.TransformDirection( normal );

			var surface = new Surface
			{
				Corners = s.Corners,
				Normal = normal,
				Distance = Vector3.Dot( normal, set.Vertices[s.Corners[0]] ),
				Flags = s.Flags,
				Neighbours = Enumerable.Repeat( -1, s.Corners.Length ).ToArray(),
			};

			for ( var i = 0; i < s.Corners.Length; i++ )
			{
				var a = s.Corners[i];
				var b = s.Corners[(i + 1) % s.Corners.Length];
				var key = (Math.Min( a, b ), Math.Max( a, b ));
				if ( !edges.TryGetValue( key, out var list ) )
					edges[key] = list = new();
				list.Add( (set.Surfaces.Count, i) );
			}

			set.Surfaces.Add( surface );
		}

		foreach ( var list in edges.Values )
		{
			foreach ( var (surface, edge) in list )
			{
				foreach ( var (other, _) in list )
				{
					if ( other != surface )
						set.Surfaces[surface].Neighbours[edge] = other;
				}
			}
		}

		return set;
	}

	/// <summary>
	/// Lays a placed decal (and the rest of its chain) onto the level. Empty if its ray misses or nothing takes it.
	/// </summary>
	/// <param name="position">The BSP's runtime decal position (just off the surface, back along the direction).</param>
	/// <param name="yaw">Halo's yaw byte.</param>
	/// <param name="pitch">Halo's pitch byte.</param>
	/// <param name="bitmapOf">A decal's bitmap (null for none).</param>
	public List<PlacedDecal> Place( DecalTag decal, Vector3 position, int yaw, int pitch, Func<DecalTag, BitmapTag> bitmapOf )
	{
		var (sy, cy) = MathF.SinCos( yaw * MathF.PI / 127 );
		var (sp, cp) = MathF.SinCos( pitch * MathF.PI / 254 );
		var direction = new Vector3( cy * cp, sy * cp, sp );

		if ( Raycast( position - direction * 0.01f, direction, 1 ) is not { } hit )
			return [];

		var set = _sets[hit.Set];
		var center = position - direction * 0.01f + direction * hit.T;
		var normal = set.Surfaces[hit.Surface].Normal;

		// MCC seeds its random stream from the position for placed decals, so they come out the same every time.
		var random = new HaloRandom( BitConverter.SingleToUInt32Bits( position.X ) ^ BitConverter.SingleToUInt32Bits( position.Y )
			^ BitConverter.SingleToUInt32Bits( position.Z ) ^ 0xDEADC0DE );

		return LayChain( decal, set, hit.Surface, center, direction, normal, 1, random, bitmapOf, null );
	}

	/// <summary>
	/// Lays a decal an effect leaves (and the rest of its chain) as MCC does: where a ray from the position along the
	/// direction, as long as the direction is (world units), first meets the level. Its picks come from the game's random
	/// stream. Empty if the ray misses or nothing takes it.
	/// </summary>
	/// <param name="scale">Scales the radius (0 means 1).</param>
	/// <param name="accept">Whether a decal of the chain may go at the hit point; a refusal ends the chain.</param>
	public List<PlacedDecal> Place( DecalTag decal, Vector3 position, Vector3 direction, float scale, HaloRandom random,
		Func<DecalTag, BitmapTag> bitmapOf, Func<DecalTag, Vector3, bool> accept )
	{
		var length = direction.Length();
		if ( length <= 0 )
			return [];

		var unit = direction / length;
		var start = position - unit * 0.01f;
		if ( Raycast( start, unit, length ) is not { } hit )
			return [];

		var set = _sets[hit.Set];
		return LayChain( decal, set, hit.Surface, start + unit * hit.T, direction, set.Surfaces[hit.Surface].Normal, scale, random, bitmapOf, accept );
	}

	List<PlacedDecal> LayChain( DecalTag decal, SurfaceSet set, int hitSurface, Vector3 center, Vector3 direction, Vector3 normal, float scale,
		HaloRandom random, Func<DecalTag, BitmapTag> bitmapOf, Func<DecalTag, Vector3, bool> accept )
	{
		var placed = new List<PlacedDecal>();
		Picks inherited = null;
		var guard = 0;
		for ( var tag = decal; tag is not null && guard < 8; guard++ )
		{
			if ( accept is not null && !accept( tag, center ) )
				break;

			var picks = inherited ?? new Picks();
			if ( Lay( tag, set, hitSurface, center, direction, normal, scale, random, picks, inherited is not null, bitmapOf ) is { } result )
				placed.Add( result );

			// "Geometry inherited by next decal": the next of the chain reuses this one's spin, sprite, size, life and colour.
			inherited = (tag.Flags & 0x1) != 0 ? picks : null;
			tag = tag.Next is null ? null : new DecalTag( tag.Next );
		}

		return placed;
	}

	/// <summary>
	/// A decal's random picks, which the next decal of its chain may reuse.
	/// </summary>
	sealed class Picks
	{
		public float Angle;
		public int Sequence;
		public float Radius;
		public float Lifetime;
		public float Decay;
		public float Color;
	}

	PlacedDecal Lay( DecalTag decal, SurfaceSet set, int hitSurface, Vector3 center, Vector3 direction, Vector3 normal, float scale,
		HaloRandom random, Picks picks, bool inherited, Func<DecalTag, BitmapTag> bitmapOf )
	{
		// Axes: A is the texture's U, B its V. Placed signs keep the in-plane part of their direction as U; everything
		// else turns at random about the normal.
		Vector3 a, b;
		var dn = Vector3.Dot( direction, normal );
		if ( decal.NoRandomRotation && dn < -1e-4f )
		{
			a = Vector3.Normalize( direction - dn * normal );
			b = Vector3.Normalize( Vector3.Cross( direction, normal ) );
		}
		else
		{
			if ( !inherited )
				picks.Angle = random.NextFraction() * MathF.PI * 2;
			var angle = picks.Angle;
			var u = Perpendicular( normal );
			var v = Vector3.Cross( normal, u );
			var (sin, cos) = MathF.SinCos( angle );
			a = cos * v - sin * u;
			b = sin * v + cos * u;
		}

		var bitmap = bitmapOf( decal );
		if ( bitmap is null || bitmap.Bitmaps.Count == 0 )
			return null;

		// The sprite: sprite 0 of a random sequence (never the last one), or the whole of bitmap 0.
		BitmapSprite sprite = null;
		if ( bitmap.Sequences.Count > 0 && (decal.Flags & 0x40) == 0 )
		{
			var n = bitmap.Sequences.Count;
			if ( !inherited )
				picks.Sequence = (int)(((n - 1) * random.Next()) >> 16);
			sprite = bitmap.Sequences[Math.Clamp( picks.Sequence, 0, n - 1 )].Sprites.FirstOrDefault();
		}

		if ( !inherited )
			picks.Radius = random.NextFraction();
		var radius = decal.RadiusMin <= 0 && decal.RadiusMax <= 0 ? 0.125f : decal.RadiusMin + (decal.RadiusMax - decal.RadiusMin) * picks.Radius;
		radius *= scale == 0 ? 1 : scale;
		if ( !inherited )
		{
			picks.Lifetime = random.NextFraction();
			picks.Decay = random.NextFraction();
			picks.Color = random.NextFraction();
		}
		var lifetime = decal.LifetimeMin + (decal.LifetimeMax - decal.LifetimeMin) * picks.Lifetime;
		var decay = decal.DecayMin + (decal.DecayMax - decal.DecayMin) * picks.Decay;
		var color = DecalColor( decal, picks.Color );

		var bitmapIndex = 0;
		float x0, x1, y0, y1;
		float u0 = 0, u1 = 1, v0 = 0, v1 = 1;
		var page = bitmap.Bitmaps[0];
		if ( sprite is not null && bitmap.Bitmaps.Count > sprite.BitmapIndex && sprite.BitmapIndex >= 0 )
		{
			// Sprites are as many pixels across as they are on their page, at the maximum sprite extent (pixels) per
			// radius, placed by their registration point.
			bitmapIndex = sprite.BitmapIndex;
			page = bitmap.Bitmaps[bitmapIndex];
			var s = radius / (decal.MaximumSpriteExtent > 0 ? decal.MaximumSpriteExtent : 16);
			var width = page.Width * s * (sprite.Right - sprite.Left);
			var height = page.Height * s * (sprite.Bottom - sprite.Top);
			if ( decal.PreserveAspect && sprite.Bottom > sprite.Top && page.Width > 0 )
				height *= (sprite.Right - sprite.Left) / (sprite.Bottom - sprite.Top) * page.Height / page.Width;
			var registration = new Vector2( page.Width * s * sprite.RegistrationX, page.Height * s * sprite.RegistrationY );
			(x0, x1, y0, y1) = (-registration.X, width - registration.X, -registration.Y, height - registration.Y);
			(u0, u1, v0, v1) = (sprite.Left, sprite.Right, sprite.Top, sprite.Bottom);
		}
		else
		{
			var aspect = decal.PreserveAspect && page.Width > 0 ? (float)page.Height / page.Width : 1;
			(x0, x1, y0, y1) = (-radius, radius, -radius * aspect, radius * aspect);
		}

		var rule = TypeRules[Math.Clamp( (int)decal.Type, 0, TypeRules.Length - 1 )];
		var frame = new Frame( center, a, b, normal, x0, x1, y0, y1, u0, u1, v0, v1 );

		var triangles = new List<DecalVertex>();
		var usedNormals = new List<Vector3>();
		var polygons = new List<List<(Vector3 Position, Vector2 TexCoord, bool Corner, Vector3 Normal)>>();
		var folds = new List<(int Surface, int Corner)>();

		// Flood from the hit surface across the edges the rectangle crosses near the centre.
		var visited = new HashSet<int>();
		var queue = new Queue<int>();
		queue.Enqueue( hitSurface );
		visited.Add( hitSurface );
		var cosAngle = MathF.Cos( rule.Angle * MathF.PI / 180 );
		var cosFold = MathF.Cos( rule.FoldAngle * MathF.PI / 180 );
		while ( queue.Count > 0 && visited.Count < 1024 )
		{
			var index = queue.Dequeue();
			var surface = set.Surfaces[index];
			if ( (surface.Flags & RejectedFlags) != 0 || surface.Corners.Length < 3 )
				continue;

			var facing = Vector3.Dot( surface.Normal, frame.Normal );
			if ( facing < cosAngle )
			{
				if ( rule.Fold && facing >= cosFold )
					folds.Add( (index, -1) );
				continue;
			}

			if ( !NearPlane( set, surface, frame, radius ) )
				continue;

			if ( Clip( set, surface, frame, out var cut ) is not { } polygon )
				continue;

			polygons.Add( polygon );
			usedNormals.Add( surface.Normal );

			for ( var e = 0; e < surface.Corners.Length; e++ )
			{
				var neighbour = surface.Neighbours[e];
				if ( !cut[e] || neighbour < 0 || visited.Contains( neighbour ) )
					continue;

				var p = set.Vertices[surface.Corners[e]];
				var q = set.Vertices[surface.Corners[(e + 1) % surface.Corners.Length]];
				if ( SegmentDistance( center, p, q ) > rule.Reach * radius )
					continue;

				visited.Add( neighbour );
				queue.Enqueue( neighbour );
			}
		}

		// Fold: the frame turns about the edge between the decal's plane and each group of surfaces it bends onto (a
		// group: surfaces within the decal's angle of each other), and the rectangle is clipped onto them as it is.
		var remaining = folds.Select( f => f.Surface ).Distinct().ToList();
		while ( remaining.Count > 0 )
		{
			var first = set.Surfaces[remaining[0]];
			var group = remaining.Where( i => Vector3.Dot( set.Surfaces[i].Normal, first.Normal ) >= cosAngle ).ToList();
			remaining = remaining.Except( group ).ToList();

			if ( FoldFrame( set, group, frame ) is not { } folded )
				continue;

			foreach ( var index in group )
			{
				var surface = set.Surfaces[index];
				if ( !NearPlane( set, surface, folded, radius ) || Clip( set, surface, folded, out _ ) is not { } polygon )
					continue;

				polygons.Add( polygon );
				usedNormals.Add( surface.Normal );
			}
		}

		if ( polygons.Count == 0 )
			return null;

		// Off the surface: rectangle corners inside a surface by its normal / 256, then everything by the average normal
		// / 256 if the surfaces roughly agree.
		var sum = usedNormals.Aggregate( Vector3.Zero, ( s, n ) => s + n );
		var average = sum.LengthSquared() > 0 ? Vector3.Normalize( sum ) : frame.Normal;
		var agree = usedNormals.All( n => MathF.Abs( n.X - usedNormals[0].X ) <= 0.5f && MathF.Abs( n.Y - usedNormals[0].Y ) <= 0.5f && MathF.Abs( n.Z - usedNormals[0].Z ) <= 0.5f );

		foreach ( var polygon in polygons )
		{
			var points = polygon.Select( p => new DecalVertex( p.Position + (p.Corner ? p.Normal * Lift : Vector3.Zero) + (agree ? average * Lift : Vector3.Zero), p.TexCoord ) ).ToList();

			// Counter-clockwise seen from the front.
			var faceNormal = polygon[0].Normal;
			var reverse = Vector3.Dot( Vector3.Cross( points[1].Position - points[0].Position, points[2].Position - points[0].Position ), faceNormal ) < 0;
			for ( var i = 1; i + 1 < points.Count; i++ )
			{
				triangles.Add( points[0] );
				triangles.Add( points[reverse ? i + 1 : i] );
				triangles.Add( points[reverse ? i : i + 1] );
			}
		}

		return new PlacedDecal( decal, bitmapIndex, color, triangles, lifetime, decay );
	}

	/// <summary>
	/// The decal's frame: centre, U and V axes, normal (its plane), the rectangle's extents along U and V and the
	/// texture rectangle they map to.
	/// </summary>
	sealed record Frame( Vector3 Center, Vector3 A, Vector3 B, Vector3 Normal, float X0, float X1, float Y0, float Y1, float U0, float U1, float V0, float V1 )
	{
		/// <summary>
		/// The axis (0 x, 1 y, 2 z) the decal is projected along: its normal's largest component.
		/// </summary>
		public int Axis => MathF.Abs( Normal.X ) >= MathF.Abs( Normal.Y ) && MathF.Abs( Normal.X ) >= MathF.Abs( Normal.Z ) ? 0
			: MathF.Abs( Normal.Y ) >= MathF.Abs( Normal.Z ) ? 1 : 2;

		public Vector3 Corner( float x, float y ) => Center + A * x + B * y;
	}

	static bool NearPlane( SurfaceSet set, Surface surface, Frame frame, float radius )
	{
		var nearest = surface.Corners.Min( c => MathF.Abs( Vector3.Dot( set.Vertices[c] - frame.Center, frame.Normal ) ) );
		return nearest <= 0.05f * radius;
	}

	static Vector2 To2D( Vector3 p, int axis ) => axis switch
	{
		0 => new Vector2( p.Y, p.Z ),
		1 => new Vector2( p.Z, p.X ),
		_ => new Vector2( p.X, p.Y ),
	};

	static float Cross2( Vector2 a, Vector2 b ) => a.X * b.Y - a.Y * b.X;

	/// <summary>
	/// A 2D point (along the frame's axis) lifted onto a plane along that axis.
	/// </summary>
	static Vector3 Lift3( Vector2 p, int axis, Vector3 normal, float distance )
	{
		switch ( axis )
		{
			case 0:
				return new Vector3( MathF.Abs( normal.X ) > 1e-6f ? (distance - normal.Y * p.X - normal.Z * p.Y) / normal.X : 0, p.X, p.Y );
			case 1:
				return new Vector3( p.Y, MathF.Abs( normal.Y ) > 1e-6f ? (distance - normal.Z * p.X - normal.X * p.Y) / normal.Y : 0, p.X );
			default:
				return new Vector3( p.X, p.Y, MathF.Abs( normal.Z ) > 1e-6f ? (distance - normal.X * p.X - normal.Y * p.Y) / normal.Z : 0 );
		}
	}

	/// <summary>
	/// The decal's rectangle clipped to a surface (in 2D along the frame's axis), lifted onto the surface's plane, with
	/// texture coordinates from where each point is on the decal's plane; null if nothing is left. <paramref name="cut"/>
	/// says which of the surface's edges cut into the rectangle.
	/// </summary>
	static List<(Vector3 Position, Vector2 TexCoord, bool Corner, Vector3 Normal)> Clip( SurfaceSet set, Surface surface, Frame frame, out bool[] cut )
	{
		var axis = frame.Axis;
		cut = new bool[surface.Corners.Length];

		// Halo projects along the decal's axis: a surface edge-on to it can't take the decal.
		var axisComponent = axis switch { 0 => surface.Normal.X, 1 => surface.Normal.Y, _ => surface.Normal.Z };
		if ( MathF.Abs( axisComponent ) < 1e-4f )
			return null;

		var polygon = new List<(Vector2 Point, bool Corner)>
		{
			(To2D( frame.Corner( frame.X0, frame.Y0 ), axis ), true),
			(To2D( frame.Corner( frame.X1, frame.Y0 ), axis ), true),
			(To2D( frame.Corner( frame.X1, frame.Y1 ), axis ), true),
			(To2D( frame.Corner( frame.X0, frame.Y1 ), axis ), true),
		};

		var corners = surface.Corners.Select( c => To2D( set.Vertices[c], axis ) ).ToArray();
		var area = 0f;
		for ( var i = 0; i < corners.Length; i++ )
			area += Cross2( corners[i], corners[(i + 1) % corners.Length] );
		if ( MathF.Abs( area ) < 1e-10f )
			return null;
		var sign = MathF.Sign( area );

		for ( var e = 0; e < corners.Length && polygon.Count >= 3; e++ )
		{
			var p = corners[e];
			var edge = corners[(e + 1) % corners.Length] - p;
			float Side( Vector2 q ) => sign * Cross2( edge, q - p );

			var output = new List<(Vector2 Point, bool Corner)>();
			for ( var i = 0; i < polygon.Count; i++ )
			{
				var current = polygon[i];
				var next = polygon[(i + 1) % polygon.Count];
				var sc = Side( current.Point );
				var sn = Side( next.Point );
				if ( sc >= 0 )
					output.Add( current );
				else
					cut[e] = true;

				if ( (sc >= 0) != (sn >= 0) )
				{
					var t = sc / (sc - sn);
					output.Add( (current.Point + (next.Point - current.Point) * t, false) );
				}
			}

			polygon = output;
		}

		if ( polygon.Count < 3 )
			return null;

		var result = new List<(Vector3, Vector2, bool, Vector3)>( polygon.Count );
		foreach ( var (point, corner) in polygon )
		{
			// Texture coordinates from the point on the decal's plane; the vertex on the surface's.
			var onPlane = Lift3( point, axis, frame.Normal, Vector3.Dot( frame.Normal, frame.Center ) );
			var s = (Vector3.Dot( onPlane - frame.Center, frame.A ) - frame.X0) / (frame.X1 - frame.X0);
			var t = (Vector3.Dot( onPlane - frame.Center, frame.B ) - frame.Y0) / (frame.Y1 - frame.Y0);
			var uv = new Vector2( Math.Clamp( frame.U0 + (frame.U1 - frame.U0) * s, 0, 1 ), Math.Clamp( frame.V0 + (frame.V1 - frame.V0) * t, 0, 1 ) );
			result.Add( (Lift3( point, axis, surface.Normal, surface.Distance ), uv, corner, surface.Normal) );
		}

		return result;
	}

	/// <summary>
	/// The frame turned onto a group of surfaces it folds onto: about their edge nearest the decal's plane, by the angle
	/// between the normals, so the rectangle carries on across the edge.
	/// </summary>
	static Frame FoldFrame( SurfaceSet set, List<int> group, Frame frame )
	{
		var target = Vector3.Normalize( group.Aggregate( Vector3.Zero, ( s, i ) => s + set.Surfaces[i].Normal ) );
		var axis = Vector3.Cross( frame.Normal, target );
		if ( axis.LengthSquared() < 1e-8f )
			return null;
		axis = Vector3.Normalize( axis );
		var angle = MathF.Acos( Math.Clamp( Vector3.Dot( frame.Normal, target ), -1, 1 ) );

		// The pivot: the group's edge (roughly along the axis) nearest the decal's plane, and of those the nearest the
		// centre.
		var best = float.MaxValue;
		var pivot = frame.Center;
		foreach ( var index in group )
		{
			var surface = set.Surfaces[index];
			for ( var e = 0; e < surface.Corners.Length; e++ )
			{
				var p = set.Vertices[surface.Corners[e]];
				var q = set.Vertices[surface.Corners[(e + 1) % surface.Corners.Length]];
				var along = q - p;
				if ( along.LengthSquared() < 1e-8f || MathF.Abs( Vector3.Dot( Vector3.Normalize( along ), axis ) ) < 0.9f )
					continue;

				var mid = (p + q) / 2;
				var score = MathF.Abs( Vector3.Dot( mid - frame.Center, frame.Normal ) ) * 1000 + SegmentDistance( frame.Center, p, q );
				if ( score < best )
				{
					best = score;
					pivot = ClosestOnSegment( frame.Center, p, q );
				}
			}
		}

		if ( best == float.MaxValue )
			return null;

		Vector3 Rotate( Vector3 v ) => Vector3.Transform( v, System.Numerics.Quaternion.CreateFromAxisAngle( axis, angle ) );
		return frame with
		{
			Center = pivot + Rotate( frame.Center - pivot ),
			A = Rotate( frame.A ),
			B = Rotate( frame.B ),
			Normal = target,
		};
	}

	static Vector3 ClosestOnSegment( Vector3 point, Vector3 a, Vector3 b )
	{
		var ab = b - a;
		var t = ab.LengthSquared() > 0 ? Math.Clamp( Vector3.Dot( point - a, ab ) / ab.LengthSquared(), 0, 1 ) : 0;
		return a + ab * t;
	}

	static float SegmentDistance( Vector3 point, Vector3 a, Vector3 b ) => Vector3.Distance( point, ClosestOnSegment( point, a, b ) );

	/// <summary>
	/// Halo 2's perpendicular: from the normal's two largest components.
	/// </summary>
	static Vector3 Perpendicular( Vector3 v )
	{
		var (x, y, z) = (MathF.Abs( v.X ), MathF.Abs( v.Y ), MathF.Abs( v.Z ));
		if ( x <= y && x <= z )
			return new Vector3( 0, v.Z, -v.Y );
		if ( y <= z )
			return new Vector3( -v.Z, 0, v.X );
		return new Vector3( v.Y, -v.X, 0 );
	}

	static Vector3 DecalColor( DecalTag decal, float t )
	{
		if ( (decal.Flags & 2) == 0 )
			return Vector3.Lerp( decal.ColorLower, decal.ColorUpper, t );

		// In HSV, the short way round the hue unless "more colors" (4) asks for the long way.
		var (h0, s0, v0) = ToHsv( decal.ColorLower );
		var (h1, s1, v1) = ToHsv( decal.ColorUpper );
		var dh = h1 - h0;
		var longWay = (decal.Flags & 4) != 0;
		if ( !longWay && MathF.Abs( dh ) > 0.5f )
			dh -= MathF.Sign( dh );
		else if ( longWay && MathF.Abs( dh ) < 0.5f && dh != 0 )
			dh -= MathF.Sign( dh );
		var h = h0 + dh * t;
		h -= MathF.Floor( h );
		return FromHsv( h, s0 + (s1 - s0) * t, v0 + (v1 - v0) * t );
	}

	static (float H, float S, float V) ToHsv( Vector3 c )
	{
		var max = MathF.Max( c.X, MathF.Max( c.Y, c.Z ) );
		var min = MathF.Min( c.X, MathF.Min( c.Y, c.Z ) );
		var delta = max - min;
		float h = 0;
		if ( delta > 0 )
		{
			if ( max == c.X ) h = (c.Y - c.Z) / delta / 6;
			else if ( max == c.Y ) h = ((c.Z - c.X) / delta + 2) / 6;
			else h = ((c.X - c.Y) / delta + 4) / 6;
		}

		return (h - MathF.Floor( h ), max > 0 ? delta / max : 0, max);
	}

	static Vector3 FromHsv( float h, float s, float v )
	{
		var i = (int)MathF.Floor( h * 6 ) % 6;
		var f = h * 6 - MathF.Floor( h * 6 );
		var p = v * (1 - s);
		var q = v * (1 - f * s);
		var t = v * (1 - (1 - f) * s);
		return i switch
		{
			0 => new Vector3( v, t, p ),
			1 => new Vector3( q, v, p ),
			2 => new Vector3( p, v, t ),
			3 => new Vector3( p, q, v ),
			4 => new Vector3( t, p, v ),
			_ => new Vector3( v, p, q ),
		};
	}

	/// <summary>
	/// The nearest collision surface a ray hits: which set, which surface, how far along.
	/// </summary>
	(int Set, int Surface, float T)? Raycast( Vector3 origin, Vector3 direction, float length )
	{
		(int, int, float)? best = null;
		for ( var s = 0; s < _sets.Count; s++ )
		{
			var set = _sets[s];
			for ( var i = 0; i < set.Surfaces.Count; i++ )
			{
				var surface = set.Surfaces[i];
				if ( surface.Corners.Length < 3 )
					continue;

				var denominator = Vector3.Dot( surface.Normal, direction );
				if ( MathF.Abs( denominator ) < 1e-6f )
					continue;

				var t = (surface.Distance - Vector3.Dot( surface.Normal, origin )) / denominator;
				if ( t < 0 || t > length || (best is { } b && t >= b.Item3) )
					continue;

				if ( Inside( set, surface, origin + direction * t ) )
					best = (s, i, t);
			}
		}

		return best;
	}

	static bool Inside( SurfaceSet set, Surface surface, Vector3 point )
	{
		var n = surface.Normal;
		var axis = MathF.Abs( n.X ) >= MathF.Abs( n.Y ) && MathF.Abs( n.X ) >= MathF.Abs( n.Z ) ? 0 : MathF.Abs( n.Y ) >= MathF.Abs( n.Z ) ? 1 : 2;
		var p = To2D( point, axis );
		var count = surface.Corners.Length;
		var positive = false;
		var negative = false;
		for ( var i = 0; i < count; i++ )
		{
			var a = To2D( set.Vertices[surface.Corners[i]], axis );
			var b = To2D( set.Vertices[surface.Corners[(i + 1) % count]], axis );
			var side = Cross2( b - a, p - a );
			if ( side > 1e-6f ) positive = true;
			else if ( side < -1e-6f ) negative = true;
			if ( positive && negative )
				return false;
		}

		return true;
	}

	/// <summary>
	/// Halo's random stream (a linear congruential generator; the upper 16 bits are the value).
	/// </summary>
	public sealed class HaloRandom( uint seed )
	{
		uint _seed = seed;

		public uint Next()
		{
			_seed = unchecked(_seed * 0x19660D + 0x3C6EF35F);
			return _seed >> 16;
		}

		public float NextFraction() => Next() / 65535f;
	}
}
