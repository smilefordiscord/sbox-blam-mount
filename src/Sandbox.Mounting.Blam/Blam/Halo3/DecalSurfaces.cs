using System;
using System.Collections.Generic;
using System.Linq;
using Blam.Halo3.Tags;

namespace Blam.Halo3;

// Inside the namespace so these win over s&box's global vector types when this compiles into the mount.
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;

public enum DecalSurfaceLighting { PerPixel, PerVertex, Unlit }

// A run of a BSP's drawn triangles with one lighting path: a cluster, or an instance (with its index).
public sealed record DecalSurfaceGroup( int Index, DecalSurfaceLighting Lighting, int Instance, int Mesh );

// A level vertex in world units with what the level's materials light it by.
public readonly record struct DecalSurfaceVertex( Vector3 Position, Vector3 Normal, Vector3 Tangent, Vector3 Binormal, Vector2 LightmapTexCoord, VertexLighting Light, Vector2 TexCoord = default );

// A BSP's opaque render triangles in world units (clusters and placed instances, as StructureBspBuilder draws them), bucketed in a grid for decal projection.
public sealed class DecalSurfaces
{
	const float CellSize = 2;

	public List<DecalSurfaceVertex> Vertices { get; } = new();

	// Corners and group; Material is the BSP material (sbsp +0xD0) index.
	public List<(int A, int B, int C, int Group, int Material)> Triangles { get; } = new();
	public List<DecalSurfaceGroup> Groups { get; } = new();

	// The structure's collision surfaces, which MCC spreads decals over (null without them).
	public DecalCollision Collision { get; private set; }

	readonly Dictionary<(int, int, int), List<int>> _cells = new();

	// Cells near the decals (null keeps every triangle).
	HashSet<(int, int, int)> _wanted;

	// Null when the lightmap BSP's geometry is stripped. With regions (world boxes around the decals), only triangles in their cells are kept.
	public static DecalSurfaces Build( StructureBspTag bsp, LightmapBspTag lightmapBsp, RenderGeometry geometry, StructureBspResources resources, IEnumerable<(Vector3 Min, Vector3 Max)> regions = null )
	{
		if ( !geometry.IsLoaded && !geometry.Load() )
			return null;

		var surfaces = new DecalSurfaces();
		if ( resources?.Collision is { Surfaces.Count: > 0 } collision )
			surfaces.Collision = new DecalCollision( collision );
		if ( regions is not null )
		{
			surfaces._wanted = new HashSet<(int, int, int)>();
			foreach ( var (min, max) in regions )
			{
				var a = Cell( min );
				var b = Cell( max );
				if ( (long)(b.X - a.X + 1) * (b.Y - a.Y + 1) * (b.Z - a.Z + 1) > 1 << 16 )
					continue;
				for ( var x = a.X; x <= b.X; x++ )
				for ( var y = a.Y; y <= b.Y; y++ )
				for ( var z = a.Z; z <= b.Z; z++ )
					surfaces._wanted.Add( (x, y, z) );
			}
		}
		foreach ( var cluster in bsp.Clusters )
		{
			if ( cluster.Mesh < 0 || cluster.Mesh >= geometry.Meshes.Count )
				continue;
			var mesh = geometry.Meshes[cluster.Mesh];
			var uv = geometry.Buffer( mesh.VertexBuffers[GeometryMesh.SlotLightmapTexCoord] ) is { Format: 4 } b ? RenderGeometry.DecodeLightmapTexCoords( b ) : null;
			surfaces.Add( geometry, mesh, null, null, uv is null ? DecalSurfaceLighting.Unlit : DecalSurfaceLighting.PerPixel, uv, null );
		}

		if ( resources is not null )
		{
			foreach ( var instance in bsp.Instances )
			{
				if ( instance.NotForRender || instance.Definition < 0 || instance.Definition >= resources.Definitions.Count )
					continue;
				var definition = resources.Definitions[instance.Definition];
				if ( definition.Mesh < 0 || definition.Mesh >= geometry.Meshes.Count )
					continue;
				var compression = definition.Compression >= 0 && definition.Compression < geometry.Compression.Count ? geometry.Compression[definition.Compression] : GeometryCompression.Identity;
				var (lighting, uv, light) = InstanceLighting( geometry, lightmapBsp, instance );
				surfaces.Add( geometry, geometry.Meshes[definition.Mesh], compression, instance, lighting, uv, light );
			}
		}

		return surfaces;
	}

	// The same rule as StructureBspBuilder.InstanceLighting: lightmap texcoords, else per-vertex SH, else the probe as constant SH.
	static (DecalSurfaceLighting, Vector2[], VertexLighting[]) InstanceLighting( RenderGeometry geometry, LightmapBspTag lightmapBsp, StructureInstance instance )
	{
		if ( instance.Index >= lightmapBsp.Instances.Count )
			return (DecalSurfaceLighting.Unlit, null, null);

		var lit = lightmapBsp.Instances[instance.Index];
		if ( lit.LightprobeTexture >= 0 && instance.LightmapTexCoords >= 0 && instance.LightmapTexCoords < geometry.PerInstanceLightmapTexCoords.Count
			&& geometry.Buffer( geometry.PerInstanceLightmapTexCoords[instance.LightmapTexCoords] ) is { Format: 4 } uv )
			return (DecalSurfaceLighting.PerPixel, RenderGeometry.DecodeLightmapTexCoords( uv ), null);

		if ( lit.PerVertex >= 0 && lit.PerVertex < lightmapBsp.PerVertexBuffers.Count && geometry.Buffer( lightmapBsp.PerVertexBuffers[lit.PerVertex] ) is { Format: 6 } sh )
			return (DecalSurfaceLighting.PerVertex, null, RenderGeometry.DecodeVertexLighting( sh ));

		if ( lit.Probe >= 0 && lightmapBsp.Probe( lit.Probe ) is { } probe )
			return (DecalSurfaceLighting.PerVertex, null, [probe]);

		return (DecalSurfaceLighting.Unlit, null, null);
	}

	void Add( RenderGeometry geometry, GeometryMesh mesh, GeometryCompression? compression, StructureInstance instance, DecalSurfaceLighting lighting, Vector2[] uv, VertexLighting[] light )
	{
		if ( geometry.Vertices( mesh, compression ) is not { Count: > 0 } source )
			return;

		// World positions first; full vertices only for the ones a kept triangle uses.
		var positions = new Vector3[source.Count];
		for ( var i = 0; i < source.Count; i++ )
			positions[i] = instance is null ? source.Positions[i] : instance.Transform( source.Positions[i] );

		var group = Groups.Count;
		var used = new Dictionary<int, int>();
		int Vertex( int i )
		{
			if ( used.TryGetValue( i, out var k ) )
				return k;
			var normal = source.Normals[i];
			var tangent = source.Tangents?[i] ?? Vector3.UnitX;
			var binormal = source.Binormals?[i] ?? Vector3.UnitY;
			if ( instance is not null )
			{
				normal = Normalize( instance.TransformDirection( normal ) );
				tangent = Normalize( instance.TransformDirection( tangent ) );
				binormal = Normalize( instance.TransformDirection( binormal ) );
			}
			var sh = light is { Length: > 0 } ? light[light.Length == 1 ? 0 : Math.Min( i, light.Length - 1 )] : default;
			Vertices.Add( new DecalSurfaceVertex( positions[i], normal, tangent, binormal, uv is not null && i < uv.Length ? uv[i] : default, sh, source.TexCoords is { } st && i < st.Length ? st[i] : default ) );
			return used[i] = Vertices.Count - 1;
		}

		var mirrored = instance is { IsMirrored: true };
		foreach ( var part in mesh.Parts )
		{
			if ( !part.IsDrawn || part.IsTransparent || part.IsWater )
				continue;
			foreach ( var (a, b, c) in geometry.Triangles( mesh, part ) )
			{
				if ( a >= source.Count || b >= source.Count || c >= source.Count )
					continue;
				var (pa, pb, pc) = (positions[a], positions[b], positions[c]);
				var min = Cell( Vector3.Min( pa, Vector3.Min( pb, pc ) ) );
				var max = Cell( Vector3.Max( pa, Vector3.Max( pb, pc ) ) );
				if ( _wanted is not null && !Wanted( min, max ) )
					continue;

				var index = Triangles.Count;
				Triangles.Add( (Vertex( a ), Vertex( mirrored ? c : b ), Vertex( mirrored ? b : c ), group, part.Material) );
				for ( var x = min.X; x <= max.X; x++ )
				for ( var y = min.Y; y <= max.Y; y++ )
				for ( var z = min.Z; z <= max.Z; z++ )
				{
					if ( !_cells.TryGetValue( (x, y, z), out var list ) )
						_cells[(x, y, z)] = list = new List<int>();
					list.Add( index );
				}
			}
		}

		if ( used.Count > 0 )
			Groups.Add( new DecalSurfaceGroup( group, lighting, instance?.Index ?? -1, mesh.Index ) );
	}

	bool Wanted( (int X, int Y, int Z) min, (int X, int Y, int Z) max )
	{
		if ( (long)(max.X - min.X + 1) * (max.Y - min.Y + 1) * (max.Z - min.Z + 1) > 4096 )
			return true;
		for ( var x = min.X; x <= max.X; x++ )
		for ( var y = min.Y; y <= max.Y; y++ )
		for ( var z = min.Z; z <= max.Z; z++ )
		{
			if ( _wanted.Contains( (x, y, z) ) )
				return true;
		}
		return false;
	}

	static (int X, int Y, int Z) Cell( Vector3 p ) => ((int)MathF.Floor( p.X / CellSize ), (int)MathF.Floor( p.Y / CellSize ), (int)MathF.Floor( p.Z / CellSize ));

	// Triangles whose cells overlap a box (each once).
	public IEnumerable<int> Near( Vector3 min, Vector3 max )
	{
		var seen = new HashSet<int>();
		var a = Cell( min );
		var b = Cell( max );
		if ( (long)(b.X - a.X + 1) * (b.Y - a.Y + 1) * (b.Z - a.Z + 1) > 1 << 16 )
			yield break;
		for ( var x = a.X; x <= b.X; x++ )
		for ( var y = a.Y; y <= b.Y; y++ )
		for ( var z = a.Z; z <= b.Z; z++ )
		{
			if ( !_cells.TryGetValue( (x, y, z), out var list ) )
				continue;
			foreach ( var t in list )
			{
				if ( seen.Add( t ) )
					yield return t;
			}
		}
	}

	public Vector3 FaceNormal( int triangle )
	{
		var (a, b, c, _, _) = Triangles[triangle];
		return Normalize( Vector3.Cross( Vertices[b].Position - Vertices[a].Position, Vertices[c].Position - Vertices[a].Position ) );
	}

	// The nearest triangle a segment crosses (either side), and how far along.
	public (int Triangle, float T)? Raycast( Vector3 origin, Vector3 direction, float length )
	{
		var end = origin + direction * length;
		(int, float)? best = null;
		foreach ( var t in Near( Vector3.Min( origin, end ), Vector3.Max( origin, end ) ) )
		{
			var (a, b, c, _, _) = Triangles[t];
			if ( Intersect( origin, direction, Vertices[a].Position, Vertices[b].Position, Vertices[c].Position ) is { } d && d >= 0 && d <= length && (best is null || d < best.Value.Item2) )
				best = (t, d);
		}
		return best;
	}

	static float? Intersect( Vector3 origin, Vector3 direction, Vector3 a, Vector3 b, Vector3 c )
	{
		var e1 = b - a;
		var e2 = c - a;
		var p = Vector3.Cross( direction, e2 );
		var det = Vector3.Dot( e1, p );
		if ( MathF.Abs( det ) < 1e-12f )
			return null;
		var s = origin - a;
		var u = Vector3.Dot( s, p ) / det;
		if ( u < -1e-5f || u > 1 + 1e-5f )
			return null;
		var q = Vector3.Cross( s, e1 );
		var v = Vector3.Dot( direction, q ) / det;
		if ( v < -1e-5f || u + v > 1 + 1e-5f )
			return null;
		return Vector3.Dot( e2, q ) / det;
	}

	static Vector3 Normalize( Vector3 v ) => v.LengthSquared() > 1e-20f ? Vector3.Normalize( v ) : Vector3.UnitZ;
}
