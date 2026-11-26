using System.Collections.Generic;
using System.Linq;
using System.IO;
using Blam.IO;

namespace Blam.Halo1.Tags;

// Inside the namespace so these win over s&box's global Vector2/Vector3 when this compiles into the mount.
using Vector3 = System.Numerics.Vector3;
using Vector4 = System.Numerics.Vector4;

/// <summary>
/// A fog plane: everything behind it (below, for the usual horizontal plane facing up) is in
/// the fog of <see cref="Region"/>.
/// </summary>
/// <param name="Region">Index into <see cref="StructureBspFog.Regions"/>, or -1.</param>
/// <param name="Plane">Normal and distance (point · normal = w), the normal pointing out of the fog.</param>
/// <param name="Vertices">The plane's outline: the fog's surface, as placed in Sapien.</param>
/// <param name="Material">What's behind the plane: water for seas, rivers and coolant, else usually none.</param>
public sealed record BspFogPlane( int Region, Vector4 Plane, IReadOnlyList<Vector3> Vertices, MaterialType? Material = null );

/// <summary>
/// A part of the level that has fog: an index into <see cref="StructureBspFog.Palette"/>, or -1
/// for none.
/// </summary>
public sealed record BspFogRegion( int PaletteIndex );

/// <summary>
/// A fog used in the BSP, and the name of the scripted function (if any) that scales it.
/// </summary>
public sealed record BspFogPaletteEntry( string Name, TagEntry Fog, string ScaleFunction );

/// <summary>
/// A BSP cluster's bounding box and the fog plane it's under.
/// </summary>
/// <param name="FogPlane">Index into <see cref="StructureBspFog.Planes"/>, or -1.</param>
public sealed record BspClusterFog( int FogPlane, Vector3 Min, Vector3 Max );

/// <summary>
/// The planar fog of a structure BSP: its fog planes, the regions they belong to, the fog tags
/// those use, and which clusters (the BSP's rooms and areas) are under which plane.
/// </summary>
public sealed record StructureBspFog(
	IReadOnlyList<BspFogPlane> Planes,
	IReadOnlyList<BspFogRegion> Regions,
	IReadOnlyList<BspFogPaletteEntry> Palette,
	IReadOnlyList<BspClusterFog> Clusters )
{
	/// <summary>
	/// The fog tag a plane uses, or null if its region has none.
	/// </summary>
	public TagEntry GetFog( BspFogPlane plane )
	{
		if ( plane.Region < 0 || plane.Region >= Regions.Count )
			return null;

		var palette = Regions[plane.Region].PaletteIndex;
		return palette >= 0 && palette < Palette.Count ? Palette[palette].Fog : null;
	}
}

partial class StructureBspTag
{
	/// <summary>
	/// Reads a BSP's planar fog. Doesn't need the BSP's geometry, so it's static and only reads
	/// the BSP's own data.
	/// </summary>
	/// <remarks>
	/// Offsets relative to the tag: clusters 0x134 (104 bytes: fog plane 0x2, subclusters 0x34
	/// of 36 bytes, each starting with x, y and z bounds), fog planes 0x178 (32 bytes: front
	/// region 0x0, material type 0x2 (-1 for none), plane 0x4, vertices 0x14 of 12 bytes), fog regions 0x184 (40 bytes: fog palette
	/// index 0x24), fog palette 0x190 (136 bytes: name 0x0, fog tag 0x20, scale function 0x34).
	///
	/// A cluster's fog field is 0x8000 | the index of the fog plane it's under, or -1 (checked on
	/// every MCC map: e.g. a30's clusters use 0x8000-0x8002 for its three planes). Where the high
	/// bit is clear (only d40, whose fog region has no fog) it's taken as no fog.
	/// </remarks>
	public static StructureBspFog ReadFog( CacheFile cache, StructureBspReference reference )
	{
		var data = new MemoryRegion( cache.Read( reference.FileOffset, reference.Size ), reference.Address );
		if ( data.U32( reference.Address + 0x14 ) != SbspSignature )
			throw new InvalidDataException( $"{reference.Tag} has no 'sbsp' signature in its header" );

		var tag = data.U32( reference.Address );

		var planes = new List<BspFogPlane>();
		foreach ( var plane in data.Block( tag + 0x178, 0x20 ) )
		{
			var vertices = new List<Vector3>();
			foreach ( var vertex in data.Block( plane + 0x14, 0xC ) )
				vertices.Add( data.Vec3( vertex ) );

			var material = data.I16( plane + 0x2 );
			planes.Add( new BspFogPlane( data.I16( plane ), new Vector4( data.Vec3( plane + 0x4 ), data.F32( plane + 0x10 ) ), vertices,
				MaterialTypes.IsValid( material ) ? (MaterialType)material : null ) );
		}

		var regions = new List<BspFogRegion>();
		foreach ( var region in data.Block( tag + 0x184, 0x28 ) )
			regions.Add( new BspFogRegion( data.I16( region + 0x24 ) ) );

		var palette = new List<BspFogPaletteEntry>();
		foreach ( var entry in data.Block( tag + 0x190, 0x88 ) )
			palette.Add( new BspFogPaletteEntry( data.CString( entry, 0x20 ), cache.GetTagReference( data, entry + 0x20 ), data.CString( entry + 0x34, 0x20 ) ) );

		var clusters = new List<BspClusterFog>();
		foreach ( var cluster in data.Block( tag + 0x134, 0x68 ) )
		{
			// The cluster's bounds are the union of its subclusters'.
			var min = new Vector3( float.MaxValue );
			var max = new Vector3( float.MinValue );
			foreach ( var sub in data.Block( cluster + 0x34, 0x24 ) )
			{
				min = Vector3.Min( min, new Vector3( data.F32( sub ), data.F32( sub + 0x8 ), data.F32( sub + 0x10 ) ) );
				max = Vector3.Max( max, new Vector3( data.F32( sub + 0x4 ), data.F32( sub + 0xC ), data.F32( sub + 0x14 ) ) );
			}

			var fog = data.U16( cluster + 0x2 );
			clusters.Add( new BspClusterFog( fog != 0xFFFF && (fog & 0x8000) != 0 ? fog & 0x7FFF : -1, min, max ) );
		}

		return new StructureBspFog( planes, regions, palette, clusters );
	}

	/// <summary>
	/// The BSP's water surfaces (shader_transparent_water) as triangles, in world units. Planar
	/// fog needs to know where they are, since water draws the fog behind it itself.
	/// </summary>
	public IEnumerable<(Vector3 A, Vector3 B, Vector3 C)> ReadWaterTriangles()
	{
		foreach ( var material in Lightmaps.SelectMany( l => l.Materials ) )
		{
			if ( material.Shader?.Class != TagClasses.ShaderWater || material.SurfaceCount == 0 )
				continue;

			var vertices = ReadRenderedVertices( material );
			for ( var s = material.FirstSurface; s < material.FirstSurface + material.SurfaceCount; s++ )
			{
				int a = Surfaces[s * 3], b = Surfaces[s * 3 + 1], c = Surfaces[s * 3 + 2];
				if ( a < vertices.Length && b < vertices.Length && c < vertices.Length )
					yield return (vertices[a].Position, vertices[b].Position, vertices[c].Position);
			}
		}
	}
}
