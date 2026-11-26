using System;
using System.Collections.Generic;
using System.Linq;

namespace Blam.Halo2.Tags;

// Inside the namespace so these win over s&box's global vector types when this compiles into the mount.
using Vector3 = System.Numerics.Vector3;

/// <summary>
/// A placed copy of an instanced geometry definition. World position of a definition vertex
/// <c>v</c> is <c>Position + Scale·(v.x·Forward + v.y·Left + v.z·Up)</c>. Its world bounding sphere
/// (<see cref="BoundingCenter"/>, <see cref="BoundingRadius"/>) is what MCC sizes it by for shader LODs.
/// </summary>
public sealed record InstancedGeometryInstance(
	int Index,
	float Scale,
	Vector3 Forward,
	Vector3 Left,
	Vector3 Up,
	Vector3 Position,
	int Definition,
	string Name,
	bool PerVertexLightmap,
	Vector3 BoundingCenter = default,
	float BoundingRadius = 0 )
{
	public Vector3 Transform( Vector3 v ) => Position + Scale * (v.X * Forward + v.Y * Left + v.Z * Up);
	public Vector3 TransformDirection( Vector3 v ) => Vector3.Normalize( v.X * Forward + v.Y * Left + v.Z * Up );
}

/// <summary>
/// A collision material of a structure BSP, which its collision surfaces index: the global material (index into
/// the globals' materials, for what the surface is made of) and the shader drawn there.
/// </summary>
public sealed record CollisionMaterial( int GlobalMaterial, TagEntry Shader );

/// <summary>
/// A decal the BSP places (its compiled copy of a scenario decal): a point 0.1 world units off the surface, back along
/// the direction the decal is projected in, Halo's yaw and pitch bytes ([-127, 127]) for that direction, and the
/// scenario's decal palette index.
/// </summary>
public sealed record RuntimeDecal( Vector3 Position, int Palette, int Yaw, int Pitch );

/// <summary>
/// A convex volume weather particles die in (see <see cref="StructureBspTag.WeatherPolyhedra"/>).
/// </summary>
public sealed record WeatherPolyhedron( Vector3 Center, float Radius, IReadOnlyList<(Vector3 Normal, float Distance)> Planes );

/// <summary>
/// A structure BSP's water definition: the constants MCC hands every water shader drawn in the BSP (pools on its
/// geometry included), and, when it has a shader, the open water drawn as a plane at <see cref="Height"/> (oceans).
/// Angles are radians; colours 0-1.
/// </summary>
/// <param name="Shader">The open water's shader, or null when the definition only carries constants.</param>
/// <param name="SunHeading">Sun direction: heading in the xy plane.</param>
/// <param name="SunPitch">Sun direction: angle from straight up.</param>
/// <param name="RotateOffset">Added to the camera's heading for the bump matrices' rotation.</param>
/// <param name="Center">The ocean plane texture's centre (xy, world units).</param>
/// <param name="Extents">The ocean plane texture's half size (xy, world units); 0 when unused.</param>
/// <param name="FogNear">Linear fog toward <see cref="HorizonColor"/>, world units; both 0 for none.</param>
public sealed record WaterDefinition(
	TagEntry Shader,
	Vector3 SunSpotColor,
	Vector3 ReflectionTint,
	Vector3 RefractionTint,
	Vector3 HorizonColor,
	float SunSpecularPower,
	float ReflectionBumpScale,
	float RefractionBumpScale,
	float FresnelScale,
	float SunHeading,
	float SunPitch,
	float FieldOfView,
	float Aspect,
	float Height,
	float FarZ,
	float RotateOffset,
	System.Numerics.Vector2 Center,
	System.Numerics.Vector2 Extents,
	float FogNear,
	float FogFar,
	float DynamicHeightBias );

/// <summary>
/// A Halo 2 structure BSP (<c>sbsp</c>): the level's render geometry in clusters, plus instanced geometry
/// (definitions placed by instances), with materials (shaders) shared by both, and the collision BSPs of both.
/// </summary>
public sealed class StructureBspTag
{
	public static readonly TagClass Class = new( "sbsp" );

	const uint CollisionMaterialsBlock = 0x0C;
	const uint CollisionBspBlock = 0x14;
	const uint FogPlanesBlock = 0x64;
	const uint WeatherPaletteBlock = 0x84;
	const uint WeatherPolyhedraBlock = 0x8C;
	const uint ClustersBlock = 0x9C;
	const uint RuntimeDecalsBlock = 0xF4;
	const uint MaterialsBlock = 0xA4;
	const uint DefinitionsBlock = 0x138;
	const uint InstancesBlock = 0x140;
	const uint WaterDefinitionsBlock = 0x214;
	const int CollisionMaterialSize = 0x14;
	const int ClusterSize = 0xB0;
	const int FogPlaneSize = 0x18;
	const uint ObjectLightingBlock = 0x22C;
	const int ObjectLightingSize = 0x5C;
	const int WeatherPaletteSize = 0x88;
	const int WeatherPolyhedronSize = 0x18;
	const int MaterialSize = 0x20;
	const int DefinitionSize = 0xC8;
	const int InstanceSize = 0x58;
	const int WaterDefinitionSize = 0xAC;

	public TagEntry Tag { get; }
	public IReadOnlyList<GeometrySection> Clusters { get; }

	/// <summary>
	/// Each cluster's bounding box (world units), which MCC sizes it by for shader LODs (scratch research notes,
	/// bsp_lod.md): the box's centre and half diagonal.
	/// </summary>
	public IReadOnlyList<(Vector3 Center, float Radius)> ClusterBounds { get; }

	/// <summary>
	/// The shader of each material, which geometry parts index.
	/// </summary>
	public IReadOnlyList<TagEntry> Materials { get; }

	public IReadOnlyList<GeometrySection> Definitions { get; }

	/// <summary>
	/// Each instanced geometry definition's own collision BSP (in the definition's space), or null.
	/// </summary>
	public IReadOnlyList<CollisionBsp> DefinitionCollision { get; }

	public IReadOnlyList<InstancedGeometryInstance> Instances { get; }

	/// <summary>
	/// The level's collision, without the instanced geometry's (see <see cref="DefinitionCollision"/>), or null.
	/// </summary>
	public CollisionBsp Collision { get; }

	public IReadOnlyList<CollisionMaterial> CollisionMaterials { get; }

	/// <summary>
	/// The water definitions (at most one in every map): see <see cref="WaterDefinition"/>.
	/// </summary>
	public IReadOnlyList<WaterDefinition> WaterDefinitions { get; }

	/// <summary>
	/// The weather systems (<see cref="WeatherTag"/>; null where an entry has none), which clusters index
	/// (<see cref="ClusterWeather"/>).
	/// </summary>
	public IReadOnlyList<TagEntry> WeatherPalette { get; }

	/// <summary>
	/// Each cluster's weather palette index, -1 for none.
	/// </summary>
	public IReadOnlyList<int> ClusterWeather { get; }

	/// <summary>
	/// Each cluster's planar fog membership (cluster +0x70): the fog plane (<see cref="FogPlanes"/>) it lies in or
	/// under, whose planar fog MCC draws on its geometry, or -1 for none (fog zones, whole clusters inside a palette
	/// entry with no plane, count as none). The camera's cluster's own byte at +0x71 (the plane it sees) isn't read.
	/// </summary>
	public IReadOnlyList<int> ClusterPlanarFog { get; }

	/// <summary>
	/// Lighting worked out by the map's tools for particular placed objects (+0x22C, 0x5C each: the placement's
	/// object id, then the engine's render lighting struct), which MCC gives those objects instead of sampling the
	/// lightmap under them: Headlong's hanging plate, whose sample ray from 20 world units up finds nothing.
	/// </summary>
	public IReadOnlyList<(ulong ObjectId, ObjectLight Light)> ObjectLighting { get; }

	/// <summary>
	/// Where weather particles die (covered places): convex volumes, a point being inside when n·p - d ≤ 0.0001 for
	/// every plane, with a bounding sphere.
	/// </summary>
	public IReadOnlyList<WeatherPolyhedron> WeatherPolyhedra { get; }

	/// <summary>
	/// The fog planes: the top of a planar fog volume (the scenario's planar fog palette index, the plane n·p = d with
	/// n pointing out of the fog).
	/// </summary>
	public IReadOnlyList<(int Palette, Vector3 Normal, float Distance)> FogPlanes { get; }

	/// <summary>
	/// The decals placed on this BSP (see <see cref="RuntimeDecal"/>), which MCC creates from these rather than from
	/// the scenario's.
	/// </summary>
	public IReadOnlyList<RuntimeDecal> RuntimeDecals { get; }

	// Each collision BSP leaf's cluster (sbsp leaves +0x2C, 8 bytes: s16 cluster), -1 for none.
	public IReadOnlyList<int> LeafClusters { get; }

	// Bungie's potentially visible set ("cluster data" +0x54: size, address): two tables in Halo CE's layout, a row of cluster bits per cluster, taken together; null without one.
	public bool[][] StoredVisibility()
	{
		var data = Tag.Cache.TagData;
		var size = data.I32( Tag.Address + 0x54 );
		var address = data.U32( Tag.Address + 0x58 );
		var n = ClusterBoxes.Count;
		var rowBytes = (n + 31) / 32 * 4;
		if ( n == 0 || size < n * rowBytes || !data.Contains( address, size ) )
			return null;

		var bytes = data.Slice( address, size ).ToArray();
		var tables = Math.Min( 2, size / (n * rowBytes) );
		var rows = new bool[n][];
		for ( var a = 0; a < n; a++ )
		{
			rows[a] = new bool[n];
			for ( var t = 0; t < tables; t++ )
			{
				var row = t * n * rowBytes + a * rowBytes;
				for ( var b = 0; b < n; b++ )
					rows[a][b] |= (bytes[row + (b >> 3)] & (1 << (b & 7))) != 0;
			}
		}
		return rows;
	}

	// Each cluster's bounding box (world units).
	public IReadOnlyList<(Vector3 Min, Vector3 Max)> ClusterBoxes { get; }

	// The openings between clusters (+0x5C, 0x24 each: back and front cluster, collision plane index, centroid, radius, flags, vertices at +0x1C).
	public IReadOnlyList<(int Back, int Front, int Plane, Vector3[] Points)> Portals { get; }

	public StructureBspTag( TagEntry tag )
	{
		Tag = tag;
		var cache = tag.Cache;
		var data = cache.TagData;

		Clusters = data.Elements( tag.Address + ClustersBlock, ClusterSize ).Select( a => GeometrySection.Read( cache, a ) ).ToList();

		// x min, max, y min, max, z min, max.
		ClusterBounds = data.Elements( tag.Address + ClustersBlock, ClusterSize ).Select( a =>
		{
			var min = new Vector3( data.F32( a + 0x54 ), data.F32( a + 0x5C ), data.F32( a + 0x64 ) );
			var max = new Vector3( data.F32( a + 0x58 ), data.F32( a + 0x60 ), data.F32( a + 0x68 ) );
			var center = (min + max) / 2;
			return (center, (max - center).Length());
		} ).ToList();
		Materials = data.Elements( tag.Address + MaterialsBlock, MaterialSize ).Select( a => cache.GetTagReference( a + 8 ) ).ToList();
		Definitions = data.Elements( tag.Address + DefinitionsBlock, DefinitionSize ).Select( a => GeometrySection.Read( cache, a ) ).ToList();

		// Instanced geometry definition: the geometry section header, then at 0x70 a whole collision BSP.
		DefinitionCollision = data.Elements( tag.Address + DefinitionsBlock, DefinitionSize )
			.Select( a => data.Count( a + 0x70 + 0x28 ) > 0 ? new CollisionBsp( data, a + 0x70 ) : null )
			.ToList();

		Collision = data.Elements( tag.Address + CollisionBspBlock, CollisionBsp.Size ).Select( a => new CollisionBsp( data, a ) ).FirstOrDefault();

		// Old shader 0x0, global material 0x8, conveyor surface 0xA, shader 0xC.
		CollisionMaterials = data.Elements( tag.Address + CollisionMaterialsBlock, CollisionMaterialSize )
			.Select( e => new CollisionMaterial( data.I16( e + 8 ), cache.GetTagReference( e + 0xC ) ) )
			.ToList();

		var index = 0;
		Instances = data.Elements( tag.Address + InstancesBlock, InstanceSize ).Select( e => new InstancedGeometryInstance(
			Index: index++,
			Scale: data.F32( e ),
			Forward: data.Vec3( e + 0x4 ),
			Left: data.Vec3( e + 0x10 ),
			Up: data.Vec3( e + 0x1C ),
			Position: data.Vec3( e + 0x28 ),
			Definition: data.I16( e + 0x34 ),
			Name: cache.GetString( data.U32( e + 0x50 ) ),
			PerVertexLightmap: data.U16( e + 0x56 ) == 1,
			BoundingCenter: data.Vec3( e + 0x3C ),
			BoundingRadius: data.F32( e + 0x48 ) ) ).ToList();

		// The shader at 0x0, then the open water's projected grid (render data, not read), the constants from 0x34.
		WaterDefinitions = data.Elements( tag.Address + WaterDefinitionsBlock, WaterDefinitionSize ).Select( e => new WaterDefinition(
			Shader: cache.GetTagReference( e ),
			SunSpotColor: data.Vec3( e + 0x34 ),
			ReflectionTint: data.Vec3( e + 0x40 ),
			RefractionTint: data.Vec3( e + 0x4C ),
			HorizonColor: data.Vec3( e + 0x58 ),
			SunSpecularPower: data.F32( e + 0x64 ),
			ReflectionBumpScale: data.F32( e + 0x68 ),
			RefractionBumpScale: data.F32( e + 0x6C ),
			FresnelScale: data.F32( e + 0x70 ),
			SunHeading: data.F32( e + 0x74 ),
			SunPitch: data.F32( e + 0x78 ),
			FieldOfView: data.F32( e + 0x7C ),
			Aspect: data.F32( e + 0x80 ),
			Height: data.F32( e + 0x84 ),
			FarZ: data.F32( e + 0x88 ),
			RotateOffset: data.F32( e + 0x8C ),
			Center: new System.Numerics.Vector2( data.F32( e + 0x90 ), data.F32( e + 0x94 ) ),
			Extents: new System.Numerics.Vector2( data.F32( e + 0x98 ), data.F32( e + 0x9C ) ),
			FogNear: data.F32( e + 0xA0 ),
			FogFar: data.F32( e + 0xA4 ),
			DynamicHeightBias: data.F32( e + 0xA8 ) ) ).ToList();

		// Palette entry: name 0x0, weather system reference 0x20 (MCC reads only its id), wind 0x4C.. (not read).
		WeatherPalette = data.Elements( tag.Address + WeatherPaletteBlock, WeatherPaletteSize )
			.Select( e => cache.GetTagReference( e + 0x20 ) )
			.ToList();
		ClusterWeather = data.Elements( tag.Address + ClustersBlock, ClusterSize ).Select( a => (int)data.I16( a + 0x76 ) ).ToList();
		ObjectLighting = data.Elements( tag.Address + ObjectLightingBlock, ObjectLightingSize )
			.Select( e => (data.U32( e ) | (ulong)data.U32( e + 4 ) << 32, ObjectLight.FromStored(
				ambient: data.Vec3( e + 8 ), direct: data.Vec3( e + 0x28 ), directVector: data.Vec3( e + 0x34 ),
				indirect: data.Vec3( e + 0x40 ), indirectVector: data.Vec3( e + 0x4C ) )) )
			.ToList();
		ClusterPlanarFog = data.Elements( tag.Address + ClustersBlock, ClusterSize )
			.Select( a => data.U8( a + 0x70 ) is var b && b != 0xFF && (b & 0x80) != 0 ? b & 0x7F : -1 )
			.ToList();
		WeatherPolyhedra = data.Elements( tag.Address + WeatherPolyhedraBlock, WeatherPolyhedronSize ).Select( e => new WeatherPolyhedron(
			Center: data.Vec3( e ),
			Radius: data.F32( e + 0xC ),
			Planes: data.Elements( e + 0x10, 0x10 ).Select( p => (data.Vec3( p ), data.F32( p + 0xC )) ).ToList() ) ).ToList();
		// Position 0x0, palette index 0xC (u8), yaw 0xE, pitch 0xF.
		LeafClusters = data.Elements( tag.Address + 0x2C, 8 ).Select( a => (int)data.I16( a ) ).ToList();
		ClusterBoxes = data.Elements( tag.Address + ClustersBlock, ClusterSize )
			.Select( a => (new Vector3( data.F32( a + 0x54 ), data.F32( a + 0x5C ), data.F32( a + 0x64 ) ), new Vector3( data.F32( a + 0x58 ), data.F32( a + 0x60 ), data.F32( a + 0x68 ) )) )
			.ToList();
		Portals = data.Elements( tag.Address + 0x5C, 0x24 )
			.Select( a => ((int)data.I16( a ), (int)data.I16( a + 2 ), data.I32( a + 4 ), data.Elements( a + 0x1C, 0xC ).Select( data.Vec3 ).ToArray()) )
			.ToList();

		RuntimeDecals = data.Elements( tag.Address + RuntimeDecalsBlock, 0x10 )
			.Select( e => new RuntimeDecal( data.Vec3( e ), data.U8( e + 0xC ), (sbyte)data.U8( e + 0xE ), (sbyte)data.U8( e + 0xF ) ) )
			.ToList();
		FogPlanes = data.Elements( tag.Address + FogPlanesBlock, FogPlaneSize )
			.Select( e => ((int)data.I16( e ), data.Vec3( e + 4 ), data.F32( e + 0x10 )) )
			.ToList();
	}
}
