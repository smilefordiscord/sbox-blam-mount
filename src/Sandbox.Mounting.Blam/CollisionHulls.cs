using Sandbox.Mounting.Blam.Halo1;

namespace Sandbox.Mounting.Blam;

// Collision BSPs as physics shapes, as the Halo CE mount makes them (ModelCollision): their convex pieces as hulls, so a body made of them can move and land on a level.
// A BSP that splits into more than ModelCollision.MaximumHulls pieces (a big rock) becomes a triangle mesh instead, which only collides on a static or keyframed body.
static class CollisionHulls
{
	public const int MaximumHalfEdges = ModelCollision.MaximumHullHalfEdges;

	// Adds a BSP's pieces (its ConvexPieces( MaximumHalfEdges )) to a body, each point through toBody; mesh gives its triangles for the fallback as positions in the body's space, indices and each triangle's surface slot.
	public static void Add( PhysicsBodyBuilder body, List<System.Numerics.Vector3[]> pieces, Func<System.Numerics.Vector3, Vector3> toBody,
		Func<(Vector3[] Vertices, uint[] Indices, byte[] Surfaces)> mesh )
	{
		if ( pieces.Count > 0 && pieces.Count <= ModelCollision.MaximumHulls )
		{
			foreach ( var piece in pieces.Where( p => p.Length >= 4 ) )
				body.AddHull( piece.Select( toBody ).ToArray() );
			return;
		}

		var (vertices, indices, surfaces) = mesh();
		if ( vertices.Length >= 3 && indices.Length >= 3 )
			body.AddMesh( vertices, indices, surfaces );
	}
}
