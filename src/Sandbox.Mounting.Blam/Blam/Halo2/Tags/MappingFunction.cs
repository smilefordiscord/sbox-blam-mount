using System;

namespace Blam.Halo2.Tags;

/// <summary>
/// A Halo 2 mapping function, the curve blob shader animation, global lighting and effects share: a curve of an
/// input <c>x</c> (0-1, or blended with a second curve by a "range" input <c>y</c>) mapped onto an output range or
/// a colour gradient.
/// </summary>
/// <remarks>
/// Header: u8 type, u8 flags (bit 0 range, bits 4-7 colour mode), u8 shape
/// of the first and of the range curve (transition and periodic types); then four u32 at 0x4 (the output minimum
/// and maximum as floats at 0x4 / 0x8, or up to four ARGB colours), then the type's values, once per curve (twice
/// with a range). <see cref="Curve"/> is clamped to [0, 1].
///
/// Exact apart from the shapes: the engine reads transition and periodic shapes from 1024-entry tables. The
/// transitions and the plain periodic waves (one, zero, cosine, diagonal wave, slide) are closed forms that match
/// those tables; the "variable period" waves use their plain wave, and noise, jitter, wander and spark (random
/// tables) a smooth stand-in with the same average. Types 6 and 8 (multi-linear key, multi-spline) the engine
/// doesn't evaluate either: 0.
/// </remarks>
public sealed class MappingFunction
{
	public const int Identity = 0, Constant = 1, Transition = 2, Periodic = 3, Linear = 4, LinearKey = 5, MultiLinearKey = 6,
		Spline = 7, MultiSpline = 8, Exponent = 9, Spline2 = 10;

	/// <summary>
	/// Floats per curve of each type (the second curve of a range function follows the first).
	/// </summary>
	static readonly int[] CurveSize = [0, 1, 2, 4, 6, 20, 32, 12, 4, 3, 12];

	static readonly string[] TypeNames = ["identity", "constant", "transition", "periodic", "linear", "linear key",
		"multi-linear key", "spline", "multi-spline", "exponent", "spline2"];

	readonly byte[] _data;

	public MappingFunction( byte[] data )
	{
		_data = data ?? [];
		if ( _data.Length < 4 )
			return;

		Type = _data[0];
		IsRange = (_data[1] & 1) != 0;
		ColorMode = _data[1] >> 4;
		Shape = _data[2];
		RangeShape = _data[3];
	}

	/// <summary>
	/// No usable blob (shorter than its header, constants and the values its type needs).
	/// </summary>
	public bool IsEmpty => _data.Length < 0x14 || Type > Spline2 || _data.Length < 0x14 + 4 * CurveSize[Type] * (IsRange ? 2 : 1);

	/// <summary>
	/// 0 identity, 1 constant, 2 transition, 3 periodic, 4 linear, 5 linear key, 6 multi-linear key, 7 spline,
	/// 8 multi-spline, 9 exponent, 10 spline2.
	/// </summary>
	public int Type { get; }

	public string TypeName => Type < TypeNames.Length ? TypeNames[Type] : $"type {Type}";

	/// <summary>
	/// The curve is blended with a second one by the range input.
	/// </summary>
	public bool IsRange { get; }

	/// <summary>
	/// The colour gradient (<see cref="ColorAt"/>): 0 grey, 1 one colour, 2 two colours, 3 three, 4 four.
	/// </summary>
	public int ColorMode { get; }

	public bool IsColor => ColorMode != 0;

	/// <summary>
	/// The periodic or transition shape of the first curve (periodic: 0 one, 1 zero, 2 cosine, 3 cosine with
	/// variable period, 4 diagonal wave, 5 its variable version, 6 slide, 7 its variable version, 8 noise, 9 jitter,
	/// 10 wander, 11 spark; transition: 0 linear, 1 early, 2 very early, 3 late, 4 very late, 5 cosine, 6 one, 7 zero).
	/// </summary>
	public int Shape { get; }

	/// <summary>
	/// The shape of the range curve.
	/// </summary>
	public int RangeShape { get; }

	/// <summary>
	/// The scalar output range.
	/// </summary>
	public float Minimum => F( 0x4 );
	public float Maximum => F( 0x8 );

	/// <summary>
	/// One of the four ARGB colours (0x4 + 4 <paramref name="index"/>): two-colour gradients use 0 and 3, three-colour
	/// ones 0, 1 and 3.
	/// </summary>
	public uint Color( int index ) => 0x4 + 4 * index + 4 <= _data.Length ? BitConverter.ToUInt32( _data, 0x4 + 4 * index ) : 0;

	/// <summary>
	/// A float of the blob by its offset, or 0 past its end.
	/// </summary>
	public float F( int offset ) => offset >= 0 && offset + 4 <= _data.Length ? BitConverter.ToSingle( _data, offset ) : 0;

	/// <summary>
	/// The function's output at <paramref name="x"/>: the curve mapped onto the output range. An empty function is 1
	/// (it doesn't scale), what global lighting expects.
	/// </summary>
	public float Evaluate( float x, float y = 0 ) => IsEmpty ? 1 : Lerp( Minimum, Maximum, Curve( x, y ) );

	/// <summary>
	/// The curve at <paramref name="x"/> (and the range input <paramref name="y"/>), clamped to [0, 1].
	/// </summary>
	public float Curve( float x, float y = 0 )
	{
		if ( IsEmpty )
			return 0;

		var f = Type == Constant ? (IsRange ? y : 0) : CurveAt( 0, Shape, x );
		if ( IsRange && Type != Constant )
			f = Lerp( f, CurveAt( 1, RangeShape, x ), y );

		return Saturate( f );
	}

	/// <summary>
	/// One curve (0 the first, 1 the range curve), unclamped.
	/// </summary>
	float CurveAt( int curve, int shape, float x )
	{
		// The curve's values start here; the range curve's follow the first's.
		var o = 0x14 + 4 * CurveSize[Type] * curve;
		switch ( Type )
		{
			case Identity:
				return x;
			case Transition:
				return Lerp( F( o ), F( o + 4 ), TransitionShape( shape, x ) );
			case Periodic:
				return Lerp( F( o + 8 ), F( o + 12 ), PeriodicShape( shape, x * F( o ) + F( o + 4 ) ) );
			case Linear:
				// Two points, then the slope and intercept the engine uses.
				return x * F( o + 16 ) + F( o + 20 );
			case LinearKey:
				// Four points, then their x (0, x1, x2), 1 / segment widths and the steps between their y.
				return F( o + 0x40 ) + F( o + 0x44 ) * Saturate( (x - F( o + 0x24 )) * F( o + 0x34 ) )
					+ F( o + 0x48 ) * Saturate( (x - F( o + 0x28 )) * F( o + 0x38 ) )
					+ F( o + 0x4C ) * Saturate( (x - F( o + 0x2C )) * F( o + 0x3C ) );
			case Spline:
			case Spline2:
			{
				// Four points (x, y), then the cubic through the first and last with the inner points' slopes as
				// tangents: f = a x^3 + b x^2 + c x + d. Spline2 first bends x by the ratio of the end segments' lengths.
				var t = x;
				if ( Type == Spline2 )
					t = Spline2Input( o, x );

				return ((F( o + 0x20 ) * t + F( o + 0x24 )) * t + F( o + 0x28 )) * t + F( o + 0x2C );
			}
			case Exponent:
			{
				var exponent = F( o + 8 );
				if ( MathF.Abs( exponent ) < 1e-4f || (exponent < 0 && MathF.Abs( x ) < 1e-4f) )
					return 1;

				return Lerp( F( o ), F( o + 4 ), MathF.Pow( MathF.Max( x, 0 ), exponent ) );
			}
			default:
				// Constant (handled by Curve), multi-linear key and multi-spline: the engine returns 0.
				return 0;
		}
	}

	/// <summary>
	/// Spline2's input: x to the power |P0 P1| / |P2 P3| (the first and last segments of its control polygon).
	/// </summary>
	float Spline2Input( int o, float x )
	{
		float Distance2( int a, int b ) => Square( F( o + a ) - F( o + b ) ) + Square( F( o + a + 4 ) - F( o + b + 4 ) );
		var end = Distance2( 0x10, 0x18 );
		if ( end <= 1e-4f )
			return x;

		var start = Distance2( 0x0, 0x8 );
		if ( MathF.Abs( start ) < 1e-4f )
			return 1;
		if ( MathF.Abs( start - end ) < 1e-4f )
			return x;

		var t = MathF.Pow( x, MathF.Sqrt( start ) / MathF.Sqrt( end ) );
		return t < 0 ? 0 : MathF.Min( t, 1 );
	}

	/// <summary>
	/// The colour at curve value <paramref name="f"/> (0-1, see <see cref="Curve"/>), ARGB: a gradient through the
	/// function's colours, each byte blended and rounded on its own, or grey for a function without colours.
	/// </summary>
	public uint ColorAt( float f )
	{
		if ( Type == Constant && (!IsRange || ColorMode <= 1) )
			return Color( 0 );

		f = Saturate( f );
		switch ( ColorMode )
		{
			case 0:
			{
				var grey = (uint)Round( f * 255 );
				return 0xFF000000 | grey << 16 | grey << 8 | grey;
			}
			case 1:
				return Color( 0 );
			case 2:
				return Blend( Color( 0 ), Color( 3 ), f );
			case 3:
			{
				var (segment, t) = Segment( f, 2 );
				return segment == 0 ? Blend( Color( 0 ), Color( 1 ), t ) : Blend( Color( 1 ), Color( 3 ), t );
			}
			case 4:
			{
				var (segment, t) = Segment( f, 3 );
				return Blend( Color( segment ), Color( segment + 1 ), t );
			}
			default:
				return 0xFFFFFFFF;
		}
	}

	/// <summary>
	/// Which of <paramref name="count"/> gradient segments <paramref name="f"/> falls in, and where in it.
	/// </summary>
	static (int Segment, float T) Segment( float f, int count )
	{
		var x = f * count;
		var segment = (int)MathF.Floor( x );
		return segment >= count ? (count - 1, 1) : (segment, x - segment);
	}

	static uint Blend( uint a, uint b, float t )
	{
		uint result = 0;
		for ( var shift = 0; shift < 32; shift += 8 )
		{
			var value = Round( (1 - t) * (a >> shift & 0xFF) + t * (b >> shift & 0xFF) );
			result |= (uint)Math.Clamp( value, 0, 255 ) << shift;
		}

		return result;
	}

	/// <summary>
	/// Transition shapes, 0-1 to 0-1: 0 linear, 1 early (square root), 2 very early (fourth root), 3 late (square),
	/// 4 very late (fourth power), 5 cosine (ease in and out), 6 one, 7 zero. Closed forms of Halo 2's tables.
	/// </summary>
	public static float TransitionShape( int shape, float x )
	{
		x = Saturate( x );
		return shape switch
		{
			1 => MathF.Sqrt( x ),
			2 => MathF.Sqrt( MathF.Sqrt( x ) ),
			3 => x * x,
			4 => x * x * x * x,
			5 => 0.5f - 0.5f * MathF.Cos( MathF.PI * x ),
			6 => 1,
			7 => 0,
			_ => x,
		};
	}

	/// <summary>
	/// Periodic shapes, one period per unit of x, 0-1: 0 one, 1 zero, 2 cosine (1 at 0), 4 diagonal wave (0 at 0,
	/// 1 at half a period), 6 slide (0 to 1): the engine's own tables when loaded from the game install
	/// (<see cref="PeriodicTables"/>). Without, the variable-period versions (3, 5, 7) use their plain wave; noise,
	/// jitter, wander and spark (8-11, random tables) a smooth wobble. halo2_*.shader evaluate those stand-ins.
	/// </summary>
	public static float PeriodicShape( int shape, float x )
	{
		if ( PeriodicTables.Sample( shape, x ) is { } exact )
			return exact;

		var t = x - MathF.Floor( x );
		return shape switch
		{
			0 => 1,
			1 => 0,
			2 or 3 => 0.5f + 0.5f * MathF.Cos( 2 * MathF.PI * t ),
			4 or 5 => 1 - MathF.Abs( 2 * t - 1 ),
			6 or 7 => t,
			_ => 0.5f + 0.25f * MathF.Sin( 2 * MathF.PI * t ) + 0.25f * MathF.Sin( 2 * MathF.PI * 2.7f * t ),
		};
	}

	/// <summary>
	/// An ARGB colour as (r, g, b, a), 0-1.
	/// </summary>
	public static System.Numerics.Vector4 ToVector( uint argb ) =>
		new( (argb >> 16 & 0xFF) / 255f, (argb >> 8 & 0xFF) / 255f, (argb & 0xFF) / 255f, (argb >> 24 & 0xFF) / 255f );

	// Halo's float to int: rounded half away from zero.
	static int Round( float x ) => (int)(x >= 0 ? x + 0.5f : x - 0.5f);

	static float Square( float x ) => x * x;
	static float Lerp( float a, float b, float t ) => a + (b - a) * t;
	static float Saturate( float x ) => Math.Clamp( x, 0, 1 );
}
