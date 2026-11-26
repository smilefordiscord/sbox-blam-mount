using System;
using System.Collections.Generic;
using System.Linq;
using Blam.Halo3.Tags;

namespace Blam.Halo3;

// Inside the namespace so it wins over s&box's global Vector3 when this compiles into the mount.
using Vector3 = System.Numerics.Vector3;

// Triangles more than one of a scene's BSPs draw at the very same place (010_jungle's 010_bsp_025 and 030 both hold one rock, 465 triangles): Halo's camera is in one BSP's cluster and its visible set leaves the other copy out, but a scene drawing every BSP draws both and they z-fight.
// What a set of BSPs shares stays with the one most of it faces into (a point just off each triangle, either side, in one of that BSP's clusters by its collision tree), else the first BSP; the others leave it out.
public static class SharedTriangles
{
	public readonly record struct Key( long A, long B, long C, long D, long E, long F, long G, long H, long I );

	public sealed record Source( int Index, StructureBspTag Bsp, RenderGeometry Geometry, CollisionBsp Tree, IReadOnlyList<InstancedGeometryDefinition> Definitions );

	// The key of a triangle by its corners in world units, rounded to a centimetre, in any order.
	public static Key KeyOf( Vector3 a, Vector3 b, Vector3 c )
	{
		static (long, long, long) R( Vector3 p ) => ((long)MathF.Round( p.X * 100 ), (long)MathF.Round( p.Y * 100 ), (long)MathF.Round( p.Z * 100 ));
		var k = new[] { R( a ), R( b ), R( c ) };
		Array.Sort( k );
		return new Key( k[0].Item1, k[0].Item2, k[0].Item3, k[1].Item1, k[1].Item2, k[1].Item3, k[2].Item1, k[2].Item2, k[2].Item3 );
	}

	// Each BSP's triangles it leaves to another BSP.
	public static Dictionary<int, HashSet<Key>> Find( IReadOnlyList<Source> bsps, out int shared )
	{
		var first = new Dictionary<Key, (int Bsp, Vector3 Centroid, Vector3 Normal)>();
		var owners = new Dictionary<Key, List<int>>();
		foreach ( var source in bsps )
		{
			foreach ( var (a, b, c) in Triangles( source ) )
			{
				var key = KeyOf( a, b, c );
				if ( !first.TryGetValue( key, out var had ) )
				{
					var normal = Vector3.Cross( b - a, c - a );
					first[key] = (source.Index, (a + b + c) / 3, normal.LengthSquared() > 0 ? Vector3.Normalize( normal ) : Vector3.UnitZ);
					continue;
				}
				if ( had.Bsp == source.Index )
					continue;
				if ( !owners.TryGetValue( key, out var list ) )
					owners[key] = list = new List<int> { had.Bsp };
				if ( !list.Contains( source.Index ) )
					list.Add( source.Index );
			}
		}

		// One owner for everything the same BSPs share (a rock kept whole, lit by one BSP's lightmap): the BSP most of those triangles face into.
		var drop = bsps.ToDictionary( b => b.Index, _ => new HashSet<Key>() );
		var byIndex = bsps.ToDictionary( b => b.Index );
		foreach ( var group in owners.GroupBy( o => string.Join( ",", o.Value.OrderBy( b => b ) ) ) )
		{
			var candidates = group.First().Value;
			var votes = candidates.ToDictionary( b => b, _ => 0 );
			foreach ( var (key, _) in group )
			{
				var (_, centroid, normal) = first[key];
				foreach ( var bsp in candidates )
				{
					var source = byIndex[bsp];
					if ( ClusterAt( source.Bsp, source.Tree, centroid + normal * 0.02f ) >= 0 )
						votes[bsp] += 2;
					else if ( ClusterAt( source.Bsp, source.Tree, centroid - normal * 0.02f ) >= 0 )
						votes[bsp] += 1;
				}
			}
			var owner = votes.OrderByDescending( v => v.Value ).ThenBy( v => v.Key ).First().Key;
			foreach ( var (key, list) in group )
			{
				foreach ( var bsp in list.Where( b => b != owner ) )
					drop[bsp].Add( key );
			}
		}
		shared = owners.Count;
		return drop;
	}

	// The cluster of a point by a BSP's collision tree and its leaves' clusters, or -1.
	public static int ClusterAt( StructureBspTag bsp, CollisionBsp tree, Vector3 point )
	{
		if ( tree is null || tree.Nodes.Count == 0 )
			return -1;
		var child = 0;
		for ( var guard = 0; guard <= tree.Nodes.Count && child >= 0 && child < tree.Nodes.Count; guard++ )
		{
			var (plane, back, front) = tree.Nodes[child];
			var index = plane & 0x7FFF;
			if ( index >= tree.Planes.Count )
				return -1;
			var (normal, distance) = tree.Planes[index];
			var d = Vector3.Dot( normal, point ) - distance;
			if ( (plane & 0x8000) != 0 )
				d = -d;
			child = d >= 0 ? front : back;
		}
		if ( child >= -1 )
			return -1;
		var leaf = child & 0x7FFFFF;
		return leaf < bsp.LeafClusters.Count ? bsp.LeafClusters[leaf] : -1;
	}

	// A BSP's drawn triangles in world units: its clusters' meshes, and its instances' placed.
	static IEnumerable<(Vector3, Vector3, Vector3)> Triangles( Source source )
	{
		var geometry = source.Geometry;
		foreach ( var cluster in source.Bsp.Clusters )
		{
			if ( cluster.Mesh >= 0 && cluster.Mesh < geometry.Meshes.Count )
			{
				foreach ( var t in Triangles( geometry, geometry.Meshes[cluster.Mesh], null, null ) )
					yield return t;
			}
		}
		if ( source.Definitions is null )
			yield break;
		foreach ( var instance in source.Bsp.Instances )
		{
			if ( instance.NotForRender || instance.Definition < 0 || instance.Definition >= source.Definitions.Count )
				continue;
			var definition = source.Definitions[instance.Definition];
			if ( definition.Mesh < 0 || definition.Mesh >= geometry.Meshes.Count )
				continue;
			var compression = definition.Compression >= 0 && definition.Compression < geometry.Compression.Count ? geometry.Compression[definition.Compression] : GeometryCompression.Identity;
			foreach ( var t in Triangles( geometry, geometry.Meshes[definition.Mesh], compression, instance ) )
				yield return t;
		}
	}

	static IEnumerable<(Vector3, Vector3, Vector3)> Triangles( RenderGeometry geometry, GeometryMesh mesh, GeometryCompression? compression, StructureInstance instance )
	{
		if ( geometry.Vertices( mesh, compression ) is not { Count: > 0 } v )
			yield break;
		foreach ( var part in mesh.Parts.Where( p => p.IsDrawn ) )
		{
			foreach ( var (a, b, c) in geometry.Triangles( mesh, part ) )
			{
				if ( a >= v.Count || b >= v.Count || c >= v.Count )
					continue;
				yield return (Place( v.Positions[a] ), Place( v.Positions[b] ), Place( v.Positions[c] ));
			}
		}

		Vector3 Place( Vector3 p ) => instance?.Transform( p ) ?? p;
	}
}
