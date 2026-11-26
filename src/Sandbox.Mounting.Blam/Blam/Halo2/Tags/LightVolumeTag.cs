using System.Collections.Generic;
using System.Linq;

namespace Blam.Halo2.Tags;

/// <summary>
/// One volume of a light volume (0x98): a row of glowing sprites along the marker's forward, with functions of the
/// position along the row for offset (world units), radius (world units), brightness and colour, their range input
/// the facing function of the view angle; screen-space aspect functions along and away from the axis.
/// </summary>
public sealed record LightVolume( int Flags, TagEntry Bitmap, int SpriteCount, MappingFunction Offset, MappingFunction Radius,
	MappingFunction Brightness, MappingFunction Color, MappingFunction Facing, IReadOnlyList<LightVolumeAspect> Aspects, float RadiusFractionMinimum,
	int SpriteMode, IReadOnlyList<System.Numerics.Vector2> SpriteInputs )
{
	/// <summary>
	/// Sprite <paramref name="i"/>'s input to the volume's functions at range input <paramref name="range"/> (by
	/// the tool's mode at 0x5C): 2 evenly spaced; 1 spaced as the radii grow geometrically between the
	/// radius function's ends (the smaller at least 1/256 of the larger); otherwise the tool's table (0x60) of inputs
	/// at range 0 and 1, or 0 without one.
	/// </summary>
	public float SpriteInput( int i, float range )
	{
		var n = System.Math.Max( SpriteCount, 1 );
		var t = n > 1 ? i / (float)(n - 1) : 0;
		if ( (SpriteMode & 2) != 0 )
			return t;

		if ( (SpriteMode & 1) != 0 )
		{
			var r0 = Radius.Evaluate( 0, range );
			var r1 = Radius.Evaluate( 1, range );
			if ( r0 < r1 ) r0 = System.MathF.Max( r0, r1 / 256 );
			else r1 = System.MathF.Max( r1, r0 / 256 );
			if ( System.MathF.Abs( r1 - r0 ) < 1e-6f || r0 <= 0 )
				return t;

			var r = r0 * System.MathF.Pow( r1 / r0, t );
			return (r - r0) / (r1 - r0);
		}

		if ( SpriteInputs.Count == n && i < n )
			return SpriteInputs[i].X + (SpriteInputs[i].Y - SpriteInputs[i].X) * range;

		return 0;
	}
}

/// <summary>
/// A light volume's screen-space aspect (0x1C): along-axis and away-from-axis scale functions, and the scale they
/// blend toward when the volume is seen end on (with its threshold angle in radians and exponent).
/// </summary>
public sealed record LightVolumeAspect( MappingFunction AlongAxis, MappingFunction AwayFromAxis, float ParallelScale, float ParallelThresholdAngle, float ParallelExponent );

/// <summary>
/// A Halo 2 light volume (<c>MGS2</c>): sprites along a marker's forward, faded out with distance from the camera.
/// Layout from the volume fields' use (Assembly's Halo 2 plugin as documentation): falloff 0x0 and cutoff 0x4
/// distances from the camera, volumes 0x8 (bitmap 0x4, sprite count 0xC, function data references 0x10 offset,
/// 0x18 radius, 0x20 brightness, 0x28 colour, 0x30 facing, aspect 0x38, radius fraction minimum 0x40).
/// </summary>
public sealed class LightVolumeTag
{
	public static readonly TagClass Class = new( "MGS2" );

	public TagEntry Tag { get; }
	public float FalloffDistance { get; }
	public float CutoffDistance { get; }
	public IReadOnlyList<LightVolume> Volumes { get; }

	public LightVolumeTag( TagEntry tag )
	{
		Tag = tag;
		var cache = tag.Cache;
		var data = cache.TagData;
		var t = tag.Address;

		MappingFunction Function( uint address )
		{
			var size = data.I32( address );
			var pointer = data.U32( address + 4 );
			return new MappingFunction( size > 0 && data.Contains( pointer, size ) ? data.Slice( pointer, size ).ToArray() : [] );
		}

		FalloffDistance = data.F32( t );
		CutoffDistance = data.F32( t + 0x4 );
		Volumes = data.Elements( t + 0x8, 0x98 ).Select( v => new LightVolume( data.I32( v ), cache.GetTagReference( v + 0x4 ), data.I32( v + 0xC ),
			Function( v + 0x10 ), Function( v + 0x18 ), Function( v + 0x20 ), Function( v + 0x28 ), Function( v + 0x30 ),
			data.Elements( v + 0x38, 0x1C ).Select( a => new LightVolumeAspect( Function( a ), Function( a + 0x8 ), data.F32( a + 0x10 ), data.F32( a + 0x14 ), data.F32( a + 0x18 ) ) ).ToList(),
			data.F32( v + 0x40 ), data.I32( v + 0x5C ), data.Elements( v + 0x60, 8 ).Select( data.Vec2 ).ToList() ) ).ToList();
	}

	public static bool IsLightVolume( TagEntry tag ) => tag is not null && tag.Address != 0 && tag.Class == Class;
}
