using System.Runtime.InteropServices;

namespace Sandbox.Mounting.Blam.Halo1;

/// <summary>
/// The vertex format used for all Halo geometry: level geometry fills in the second texture
/// coordinate with its lightmap coordinates and the incident light direction, models leave them empty.
/// </summary>
[StructLayout( LayoutKind.Sequential )]
struct HaloVertex
{
	[VertexLayout.Position] public Vector3 Position;
	[VertexLayout.Normal] public Vector3 Normal;
	[VertexLayout.Tangent] public Vector4 Tangent;
	[VertexLayout.TexCoord( 0 )] public Vector2 TexCoord;
	[VertexLayout.TexCoord( 1 )] public Vector2 LightmapTexCoord;

	/// <summary>
	/// The lightmap's dominant incoming light direction at this vertex (world space), from the
	/// BSP's lightmap vertices. Its length says how directional the light is (0 to 1). Bump maps
	/// shade against it.
	/// </summary>
	[VertexLayout.TexCoord( 4 )] public Vector3 IncidentLight;

	/// <summary>
	/// Packs Halo's tangent and binormal into a tangent plus handedness sign.
	/// </summary>
	public static Vector4 Tangent4( Vector3 normal, Vector3 tangent, Vector3 binormal ) =>
		new( tangent, Vector3.Dot( Vector3.Cross( normal, tangent ), binormal ) < 0 ? -1f : 1f );
}
