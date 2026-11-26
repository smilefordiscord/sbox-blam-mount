using System;
using System.Collections.Generic;
using System.Linq;
using Blam.Halo1.Tags;

namespace Blam.Halo1;

// Inside the namespace so this wins over s&box's global Vector3 when this compiles into the mount.
using Vector3 = System.Numerics.Vector3;

/// <summary>
/// One row of a flag's cloth at rest.
/// </summary>
/// <param name="Edge">Where the row meets the attached edge, model space, world units.</param>
/// <param name="Down">Direction of the attached edge there, from the first attachment point toward the last.</param>
/// <param name="Rest">Direction the row runs at rest, away from the edge (perpendicular to it).</param>
public sealed record FlagClothRow( Vector3 Edge, Vector3 Down, Vector3 Rest );

/// <summary>
/// Lays a flag tag's cloth out at rest on its carrier's markers: the first column of vertices
/// along the attachment points (each point's rows spread evenly to the next point, as Halo pins
/// and pulls them), rows past the last point hanging on below it a cell apart, and every row
/// running flat along the model's -x (backward), turned square to the edge.
/// </summary>
public static class FlagClothGrid
{
	public static IReadOnlyList<FlagClothRow> Rows( FlagTag flag, IReadOnlyList<ModelMarker> markers )
	{
		var height = flag.Height;
		var rows = flag.AttachmentRows();

		// A missing marker attaches to the model's origin, as c20 describes.
		var points = flag.AttachmentPoints
			.Select( p => markers.FirstOrDefault( m => m.Name == p.Marker )?.Position ?? Vector3.Zero )
			.ToArray();

		var edge = new Vector3[height];
		var down = new Vector3[height];
		var last = 0;
		for ( var i = 0; i < points.Length && rows[i] >= 0; i++ )
		{
			last = i;
			var end = i + 1 < points.Length && rows[i + 1] > rows[i] ? rows[i + 1] : rows[i];
			var axis = i + 1 < points.Length ? points[i + 1] - points[i] : Vector3.Zero;
			axis = axis.Length() > 1e-4f ? Vector3.Normalize( axis ) : rows[i] > 0 ? down[rows[i] - 1] : -Vector3.UnitZ;
			for ( var y = rows[i]; y <= end; y++ )
			{
				edge[y] = end == rows[i] ? points[i] : Vector3.Lerp( points[i], points[i + 1], (float)(y - rows[i]) / (end - rows[i]) );
				down[y] = axis;
			}
		}

		for ( var y = rows[last] + 1; y < height; y++ )
		{
			down[y] = down[y - 1];
			edge[y] = edge[y - 1] + down[y] * flag.CellHeight;
		}

		var result = new FlagClothRow[height];
		for ( var y = 0; y < height; y++ )
		{
			var rest = -Vector3.UnitX - down[y] * Vector3.Dot( -Vector3.UnitX, down[y] );
			rest = rest.Length() > 1e-3f ? Vector3.Normalize( rest ) : Vector3.Normalize( Vector3.Cross( down[y], Vector3.UnitY ) );
			result[y] = new FlagClothRow( edge[y], down[y], rest );
		}

		return result;
	}
}
