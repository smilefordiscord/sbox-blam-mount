using System;
using System.Collections.Generic;
using Blam.Halo1.Tags;

namespace Blam.Halo1;

/// <summary>
/// The stretch of a bolt from one marker to the next, split into 2^<see cref="Octaves"/>
/// pieces whose inner points are jittered off the straight line.
/// </summary>
/// <param name="Start">Index of the starting marker in the tag's markers.</param>
/// <param name="FirstPoint">Index of the segment's first point along the whole bolt.</param>
public sealed record LightningSegment( int Start, int Octaves, int FirstPoint )
{
	public int End => Start + 1;
	public int PointCount => 1 << Octaves;
}

/// <summary>
/// One bolt: a chain of segments drawn as a single ribbon.
/// </summary>
/// <param name="PointCount">Points along the whole bolt, including both ends.</param>
public sealed record LightningChain( IReadOnlyList<LightningSegment> Segments, int PointCount );

/// <summary>
/// Splits a lightning tag's markers into the bolts Halo draws through them.
/// </summary>
/// <remarks>
/// Markers chain into one bolt until a marker flagged "not connected to next marker" or the
/// last one. Every frame each segment is subdivided by repeatedly moving midpoints a random
/// distance (the markers' jitter, interpolated, halving every octave) across the bolt as seen
/// from the camera, and the ribbon's width, color and texture run along the points. Bolts with
/// two subdivisions or fewer aren't drawn. The whole set is drawn <see cref="LightningTag.Count"/>
/// times, each with its own random jitter.
/// </remarks>
public static class LightningChains
{
	/// <summary>
	/// More octaves than this would go past the most points a bolt can have (4097).
	/// </summary>
	const int MaxOctaves = 10;

	public static List<LightningChain> FromTag( LightningTag tag )
	{
		var chains = new List<LightningChain>();
		var segments = new List<LightningSegment>();
		var points = 0;

		for ( var i = 0; i < tag.Markers.Count; i++ )
		{
			var marker = tag.Markers[i];
			if ( marker.NotConnectedToNext || i == tag.Markers.Count - 1 )
			{
				if ( points > 2 )
					chains.Add( new LightningChain( segments.ToArray(), points + 1 ) );

				segments.Clear();
				points = 0;
				continue;
			}

			var segment = new LightningSegment( i, Math.Clamp( marker.OctavesToNext, 0, MaxOctaves ), points );
			segments.Add( segment );
			points += segment.PointCount;
		}

		return chains;
	}
}
