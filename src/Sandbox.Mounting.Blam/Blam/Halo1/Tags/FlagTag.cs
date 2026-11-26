using System.Collections.Generic;

namespace Blam.Halo1.Tags;

public enum FlagTrailingEdgeShape : short
{
	Flat = 0,
	ConcaveTriangular = 1,
	ConvexTriangular = 2,
	TrapezoidShortTop = 3,
	TrapezoidShortBottom = 4,
}

public enum FlagAttachedEdgeShape : short
{
	Flat = 0,
	ConcaveTriangular = 1,
}

/// <summary>
/// A point the cloth's attached edge is pinned to: a marker on the carrying object's model.
/// </summary>
/// <param name="HeightToNext">How many vertex rows of the attached edge run from this marker to the next one.</param>
public sealed record FlagAttachmentPoint( int HeightToNext, string Marker );

/// <summary>
/// Reader for <c>flag</c> tags: the cloth an object (the CTF flag) carries as a widget. Halo
/// simulates it as a grid of point-physics vertices, the first column pinned along the object's
/// markers, and draws it with the red or the blue shader by the owner's team.
/// </summary>
/// <remarks>
/// Offsets: flags 0x0, trailing edge shape 0x4, trailing edge shape offset 0x6, attached edge
/// shape 0x8, width 0xC and height 0xE (vertices across and down), cell width 0x10 and height
/// 0x14 (world units), red flag shader 0x18, physics 0x28 (<c>pphy</c>), wind noise 0x38, blue
/// flag shader 0x44, attachment points block 0x54 (52 bytes: height to next attachment 0x0 as
/// vertex rows, marker name 0x14).
/// </remarks>
public sealed class FlagTag
{
	public TagEntry Tag { get; }
	public uint Flags { get; }
	public FlagTrailingEdgeShape TrailingEdgeShape { get; }
	public int TrailingEdgeShapeOffset { get; }
	public FlagAttachedEdgeShape AttachedEdgeShape { get; }

	/// <summary>Vertices along the cloth, away from the attached edge.</summary>
	public int Width { get; }

	/// <summary>Vertices down the attached edge.</summary>
	public int Height { get; }

	public float CellWidth { get; }
	public float CellHeight { get; }
	public TagEntry RedShader { get; }
	public TagEntry Physics { get; }

	/// <summary>Random push on each vertex every tick, as a fraction of the physics' friction.</summary>
	public float WindNoise { get; }

	public TagEntry BlueShader { get; }
	public IReadOnlyList<FlagAttachmentPoint> AttachmentPoints { get; }

	public FlagTag( CacheFile cache, TagEntry tag )
	{
		var data = cache.TagData;
		var a = tag.Address;
		Tag = tag;
		Flags = data.U32( a );
		TrailingEdgeShape = (FlagTrailingEdgeShape)data.I16( a + 0x4 );
		TrailingEdgeShapeOffset = data.I16( a + 0x6 );
		AttachedEdgeShape = (FlagAttachedEdgeShape)data.I16( a + 0x8 );
		Width = data.I16( a + 0xC );
		Height = data.I16( a + 0xE );
		CellWidth = data.F32( a + 0x10 );
		CellHeight = data.F32( a + 0x14 );
		RedShader = cache.GetTagReference( data, a + 0x18 );
		Physics = cache.GetTagReference( data, a + 0x28 );
		WindNoise = data.F32( a + 0x38 );
		BlueShader = cache.GetTagReference( data, a + 0x44 );

		var points = new List<FlagAttachmentPoint>();
		foreach ( var p in data.Block( a + 0x54, 0x34 ) )
			points.Add( new FlagAttachmentPoint( data.I16( p ), data.CString( p + 0x14, 32 ) ) );
		AttachmentPoints = points;
	}

	/// <summary>
	/// Halo skips a flag that's too big for its fixed vertex storage or has no blue shader.
	/// </summary>
	public bool IsDrawn => Width >= 2 && Height >= 2 && Width < 40 && Width * Height < 225 && BlueShader is not null && AttachmentPoints.Count > 0;

	/// <summary>
	/// The shader for a team: red for the red team, blue for every other team (and the red
	/// one's stand-in when it's missing).
	/// </summary>
	public TagEntry ShaderFor( bool red ) => red && RedShader is not null ? RedShader : BlueShader;

	/// <summary>
	/// The first vertex row of each attachment point down the attached edge, as Halo lays them
	/// out: each point spans its height to the next one (rounded down to even, and cut off at the
	/// bottom), the last at the row where the previous one ended. Rows past the last point
	/// hang free.
	/// </summary>
	public int[] AttachmentRows()
	{
		var rows = new int[AttachmentPoints.Count];
		var y = 0;
		for ( var i = 0; i < AttachmentPoints.Count; i++ )
		{
			if ( y >= Height )
			{
				rows[i] = -1;
				continue;
			}

			rows[i] = y;
			y += System.Math.Clamp( AttachmentPoints[i].HeightToNext, 0, Height - y ) & ~1;
		}

		return rows;
	}
}
