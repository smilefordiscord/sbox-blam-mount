using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Blam.Halo1;

// Inside the namespace so these win over s&box's global Vector3 when this compiles into the mount.
using Vector3 = System.Numerics.Vector3;

/// <summary>
/// Halo's object lighting worked out ahead of time on a grid over the whole level, so objects the
/// level doesn't place (a game's players, weapons, vehicles) can be lit the way Halo lights them as
/// they move: by the lightmap under their center.
/// </summary>
/// <remarks>
/// <para>Each cell holds the lighting of a moving object centered on the cell's center (see
/// <see cref="ObjectLightingSampler.TryLightPoint"/>: one ray 10 world units straight down). Cells
/// whose ray reaches no lit surface (inside rock, over the void) are empty, and the shader leaves
/// them out of the interpolation.</para>
///
/// <para>Stored as four RGBA8 images, each laid out as an atlas of the grid's horizontal slices
/// (slice k at tile (k mod <see cref="TilesX"/>, k / <see cref="TilesX"/>), x across, y down),
/// premultiplied by whether the cell is lit so filtering between cells weighs only lit ones:
/// <list type="number">
/// <item>main light color (the lightmap color C), lit (alpha);</item>
/// <item>direction toward the main light, as 0.5 + 0.5 v; lit;</item>
/// <item>bounce light color; ground normal x as 0.5 + 0.5 x;</item>
/// <item>reflection tint; ground normal y as 0.5 + 0.5 y.</item>
/// </list>
/// The ambient light is 0.4 C + 0.03, as the game derives it, and the bounce light comes from below
/// along the ground normal.</para>
/// </remarks>
public sealed class ObjectLightVolume
{
	/// <summary>
	/// The grid's corner and cell size, in world units.
	/// </summary>
	public Vector3 Min { get; private init; }
	public Vector3 CellSize { get; private init; }

	public int SizeX { get; private init; }
	public int SizeY { get; private init; }
	public int SizeZ { get; private init; }

	/// <summary>Slices per atlas row.</summary>
	public int TilesX { get; private init; }
	public int TilesY => (SizeZ + TilesX - 1) / TilesX;

	public int AtlasWidth => SizeX * TilesX;
	public int AtlasHeight => SizeY * TilesY;

	/// <summary>The four images (see the remarks), RGBA8, <see cref="AtlasWidth"/> by <see cref="AtlasHeight"/>.</summary>
	public byte[][] Images { get; private set; }

	/// <summary>How many cells are lit.</summary>
	public int LitCells { get; private set; }

	// Room for a level about 200 world units across (Blood Gulch, most multiplayer maps) at one
	// world unit per cell: at most 1.5 million cells, 24 MB over the four images, worked out in a
	// fraction of a second on a few cores.
	const int MaxCells = 1_500_000;
	const int MaxSlices = 48;
	const int MaxAtlasSize = 16384;
	const float MinCellSize = 1f;

	// How far past the outermost placed object, spawn point or flag the grid reaches (world units):
	// across, below and above.
	const float MarginAcross = 25f;
	const float MarginBelow = 10f;
	const float MarginAbove = 20f;

	/// <param name="focus">Where things go in the level (placed objects, spawn points, flags): the
	/// grid covers them with a margin rather than the whole level. Outdoor levels' collision
	/// reaches far out under the sea (Death Island and b30 span 2000-2800 world units), which would
	/// leave cells several units across. Null or empty for the whole level.</param>
	public static ObjectLightVolume Build( ObjectLightingSampler sampler, IEnumerable<Vector3> focus = null )
	{
		if ( sampler.Bounds is not { } bounds )
			return null;

		var points = focus?.ToList() ?? new List<Vector3>();
		if ( points.Count > 0 )
		{
			var min = points.Aggregate( Vector3.Min ) - new Vector3( MarginAcross, MarginAcross, MarginBelow );
			var max = points.Aggregate( Vector3.Max ) + new Vector3( MarginAcross, MarginAcross, MarginAbove );
			min = Vector3.Max( min, bounds.Min );
			max = Vector3.Min( max, bounds.Max );
			if ( min.X < max.X && min.Y < max.Y && min.Z < max.Z )
				bounds = (min, max);
		}

		var extent = Vector3.Max( bounds.Max - bounds.Min, new Vector3( MinCellSize ) );

		// Square cells across, and slices up to a world unit apart; coarser until it fits.
		var cell = MinCellSize;
		int sx, sy, sz;
		while ( true )
		{
			sx = Math.Max( 1, (int)MathF.Ceiling( extent.X / cell ) );
			sy = Math.Max( 1, (int)MathF.Ceiling( extent.Y / cell ) );
			sz = Math.Clamp( (int)MathF.Ceiling( extent.Z / cell ), 1, MaxSlices );
			var tilesX = Math.Max( 1, Math.Min( sz, MaxAtlasSize / sx ) );
			var tilesY = (sz + tilesX - 1) / tilesX;
			if ( (long)sx * sy * sz <= MaxCells && sx <= MaxAtlasSize && sy * tilesY <= MaxAtlasSize )
				break;
			cell *= 1.25f;
		}

		var volume = new ObjectLightVolume
		{
			Min = bounds.Min,
			CellSize = new Vector3( extent.X / sx, extent.Y / sy, extent.Z / sz ),
			SizeX = sx,
			SizeY = sy,
			SizeZ = sz,
			TilesX = Math.Max( 1, Math.Min( sz, MaxAtlasSize / sx ) ),
		};

		var images = new byte[4][];
		for ( var i = 0; i < images.Length; i++ )
			images[i] = new byte[volume.AtlasWidth * volume.AtlasHeight * 4];

		var lit = 0;
		Parallel.For( 0, sz * sy, row =>
		{
			var z = row / sy;
			var y = row % sy;
			var count = 0;
			for ( var x = 0; x < sx; x++ )
			{
				var point = volume.Min + new Vector3( x + 0.5f, y + 0.5f, z + 0.5f ) * volume.CellSize;
				if ( !sampler.TryLightPoint( point, out var light ) )
					continue;

				var o = volume.Texel( x, y, z );
				Write( images[0], o, light.Light0, 1 );
				Write( images[1], o, light.ToLight0 * 0.5f + new Vector3( 0.5f ), 1 );

				// The bounce light travels up out of the ground: toward it is down, along -normal.
				var normal = -light.ToLight1;
				Write( images[2], o, light.Light1, normal.X * 0.5f + 0.5f );
				Write( images[3], o, light.Reflection, normal.Y * 0.5f + 0.5f );
				count++;
			}

			System.Threading.Interlocked.Add( ref lit, count );
		} );

		volume.LitCells = lit;
		volume.Images = images;
		return volume;
	}

	/// <summary>
	/// The lighting at a point the way halo1_model.shader reads the volume: trilinear between the
	/// cells around it, counting only lit ones. False where too few of them are lit (the shader
	/// then uses the map's average light).
	/// </summary>
	public bool TryLight( Vector3 point, out ObjectLight light )
	{
		var cell = (point - Min) / CellSize - new Vector3( 0.5f );
		var z = Math.Clamp( cell.Z, 0, SizeZ - 1 );
		var z0 = (int)MathF.Floor( z );
		var z1 = Math.Min( z0 + 1, SizeZ - 1 );
		var fz = z - z0;
		var x = Math.Clamp( cell.X, 0, SizeX - 1 );
		var y = Math.Clamp( cell.Y, 0, SizeY - 1 );
		var x0 = (int)MathF.Floor( x );
		var y0 = (int)MathF.Floor( y );
		var x1 = Math.Min( x0 + 1, SizeX - 1 );
		var y1 = Math.Min( y0 + 1, SizeY - 1 );
		var fx = x - x0;
		var fy = y - y0;

		System.Numerics.Vector4 Read( int image )
		{
			System.Numerics.Vector4 At( int cx, int cy, int cz )
			{
				var o = Texel( cx, cy, cz );
				var b = Images[image];
				return new System.Numerics.Vector4( b[o], b[o + 1], b[o + 2], b[o + 3] ) / 255f;
			}

			System.Numerics.Vector4 Slice( int cz ) => System.Numerics.Vector4.Lerp(
				System.Numerics.Vector4.Lerp( At( x0, y0, cz ), At( x1, y0, cz ), fx ),
				System.Numerics.Vector4.Lerp( At( x0, y1, cz ), At( x1, y1, cz ), fx ), fy );

			return System.Numerics.Vector4.Lerp( Slice( z0 ), Slice( z1 ), fz );
		}

		var main = Read( 0 );
		var direction = Read( 1 );
		var bounce = Read( 2 );
		var reflection = Read( 3 );

		// Premultiplied by how lit the cells are, so dividing by it leaves the lit cells' average.
		var lit = main.W;
		var scale = 1 / MathF.Max( lit, 1 / 255f );
		var light0 = new Vector3( main.X, main.Y, main.Z ) * scale;
		var toLight0 = new Vector3( direction.X, direction.Y, direction.Z ) * scale * 2 - Vector3.One + new Vector3( 0, 0, 1e-4f );
		var normalX = bounce.W * scale * 2 - 1;
		var normalY = reflection.W * scale * 2 - 1;
		var toLight1 = -new Vector3( normalX, normalY, MathF.Sqrt( Math.Clamp( 1 - normalX * normalX - normalY * normalY, 0, 1 ) ) );
		light = new ObjectLight( light0 * 0.4f + new Vector3( 0.03f ), light0, Vector3.Normalize( toLight0 ), new Vector3( bounce.X, bounce.Y, bounce.Z ) * scale, toLight1,
			new Vector3( reflection.X, reflection.Y, reflection.Z ) * scale );
		return lit > 0.02f;
	}

	int Texel( int x, int y, int z ) => (((z / TilesX) * SizeY + y) * AtlasWidth + (z % TilesX) * SizeX + x) * 4;

	static void Write( byte[] image, int offset, Vector3 rgb, float a )
	{
		image[offset] = Byte( rgb.X );
		image[offset + 1] = Byte( rgb.Y );
		image[offset + 2] = Byte( rgb.Z );
		image[offset + 3] = Byte( a );
	}

	static byte Byte( float v ) => (byte)MathF.Round( Math.Clamp( v, 0, 1 ) * 255 );
}
