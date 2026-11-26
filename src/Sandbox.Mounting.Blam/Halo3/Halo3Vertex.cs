using System.Runtime.InteropServices;

namespace Sandbox.Mounting.Blam.Halo3;

// The vertex the Halo 3 shaders read, for level and object geometry alike.
// Per-vertex static lighting rides along: the L1 SH coefficients c0..c3 (RGB, xyz) and the dominant light intensity I (r, g, b in Sh0.w, Sh1.w, Sh2.w), decoded from the lightmap's per-vertex buffers; zero for geometry lit per pixel or by probes.
[StructLayout( LayoutKind.Sequential )]
struct Halo3Vertex
{
	[VertexLayout.Position] public Vector3 Position;
	[VertexLayout.Normal] public Vector3 Normal;
	[VertexLayout.Tangent] public Vector4 Tangent;
	[VertexLayout.TexCoord( 0 )] public Vector2 TexCoord;
	[VertexLayout.TexCoord( 1 )] public Vector2 LightmapTexCoord;
	[VertexLayout.TexCoord( 4 )] public Vector4 Sh0;
	[VertexLayout.TexCoord( 5 )] public Vector4 Sh1;
	[VertexLayout.TexCoord( 6 )] public Vector4 Sh2;
	[VertexLayout.TexCoord( 7 )] public Vector4 Sh3;
	// Up to three pre-lighting decals' u/v where a surface is drawn again with them in its albedo (Halo3DecalBuilder): the first two, then the third.
	[VertexLayout.TexCoord( 8 )] public Vector4 DecalTexCoords;
	[VertexLayout.TexCoord( 9 )] public Vector2 DecalTexCoord2;

	public static Vector4 Tangent4( Vector3 normal, Vector3 tangent, Vector3 binormal ) => Halo1.HaloVertex.Tangent4( normal, tangent, binormal );
}
