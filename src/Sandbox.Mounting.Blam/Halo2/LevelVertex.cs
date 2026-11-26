using System.Runtime.InteropServices;

namespace Sandbox.Mounting.Blam.Halo2;

/// <summary>
/// The vertex format of Halo 2 level geometry (halo2_opaque.shader): Halo CE's level vertex
/// (<see cref="Halo1.HaloVertex"/>) plus the lightmap colour of geometry lit per vertex.
/// </summary>
[StructLayout( LayoutKind.Sequential )]
struct LevelVertex
{
	[VertexLayout.Position] public Vector3 Position;
	[VertexLayout.Normal] public Vector3 Normal;
	[VertexLayout.Tangent] public Vector4 Tangent;
	[VertexLayout.TexCoord( 0 )] public Vector2 TexCoord;
	[VertexLayout.TexCoord( 1 )] public Vector2 LightmapTexCoord;

	/// <summary>
	/// The lightmap's incoming light direction at this vertex (world space); its length says how directional the
	/// light is (0 to 1). Bump maps shade against it.
	/// </summary>
	[VertexLayout.TexCoord( 4 )] public Vector3 IncidentLight;

	/// <summary>
	/// For instanced geometry lit per vertex, the lightmap colour (0-1, gamma values like a lightmap page's texels)
	/// that stands in for the lightmap page.
	/// </summary>
	[VertexLayout.TexCoord( 5 )] public Vector3 LightmapColor;

	/// <summary>
	/// The centre of the bounding sphere MCC sizes this vertex's cluster or instance by for shader LODs (s&amp;box
	/// units; see halo2_opaque.shader's LevelLod).
	/// </summary>
	[VertexLayout.TexCoord( 6 )] public Vector3 LodCenter;

	/// <summary>
	/// That sphere's radius (s&amp;box units, 0 for no LOD switching) and the part's LOD magic number (x, y), and 1 if
	/// the geometry is in its BSP's planar fog (z: MCC fogs only clusters that are, see halo2_opaque.shader).
	/// </summary>
	[VertexLayout.TexCoord( 7 )] public Vector3 LodSize;

	/// <summary>
	/// Packs Halo's tangent and binormal into a tangent plus handedness sign.
	/// </summary>
	public static Vector4 Tangent4( Vector3 normal, Vector3 tangent, Vector3 binormal ) => Halo1.HaloVertex.Tangent4( normal, tangent, binormal );
}
