using System;
using System.Linq;

namespace Blam.Halo3.Tags;

// A Halo 3 function blob (effects, particles, particle movement), evaluated as Halo 3's particle_update compute shader does.
// Header 0x20: u8 type, u8 flags (1 range, 4 clamp, 8 exclusion), u8 colour count (0 scalar), output min/max floats at 0x4/0x8 or up to four ARGB colours at 0x4..0x10, exclusion min/max at 0x14/0x18, u32 size of the curves that follow at 0x1C.
// Curves (two with a range, the range curve after the first): 0 identity, 1 constant (0, the range curve 1), 3 periodic (u8 shape, frequency, phase, min, max), 8 piecewise (u32 count, then segments: u8 kind, end x, kind 4 a x + b, kind 7 a x^3 + b x^2 + c x + d, kind 10 the cubic in (x - x0) / width with x0, width and one unused float), 9 exponent (min, max, power).
public sealed class Halo3Function
{
	public const int Identity = 0, Constant = 1, Transition = 2, Periodic = 3, Piecewise = 8, Exponent = 9;

	public static readonly Halo3Function Empty = new( [] );

	readonly byte[] _data;

	// Where each curve's values start (the range curve's at [1]).
	readonly int[] _curves = new int[2];

	public Halo3Function( byte[] data )
	{
		_data = data ?? [];
		if ( _data.Length < 0x20 )
			return;

		Type = _data[0];
		Flags = _data[1];
		ColorCount = _data[2];
		_curves[0] = 0x20;
		_curves[1] = IsRange ? 0x20 + CurveLength( 0x20 ) : 0x20;
		IsEmpty = Type is not (Identity or Constant or Transition or Periodic or Piecewise or Exponent) || _curves[1] > _data.Length;
	}

	public bool IsEmpty { get; } = true;
	public int Type { get; }
	public int Flags { get; }
	public int ColorCount { get; }
	public bool IsRange => (Flags & 0x1) != 0;
	public bool Clamps => (Flags & 0x4) != 0;
	public bool Excludes => (Flags & 0x8) != 0;
	public bool IsColor => ColorCount != 0;

	public float Minimum => F( 0x4 );
	public float Maximum => F( 0x8 );
	public float ExclusionMinimum => F( 0x14 );
	public float ExclusionMaximum => F( 0x18 );

	public string TypeName => Type switch
	{
		Identity => "identity",
		Constant => "constant",
		Transition => "transition",
		Periodic => "periodic",
		Piecewise => "curve",
		Exponent => "exponent",
		_ => $"type {Type}",
	};

	// The colour count's colours: one 0x4; two 0x4, 0x10; three 0x4, 0x8, 0x10; four 0x4..0x10 (ARGB, a byte each).
	public uint Color( int index ) => U32( 0x4 + 4 * index );

	// The colours in gradient order, as ColorAt blends them.
	public uint[] GradientStops => ColorCount switch
	{
		1 => [Color( 0 )],
		2 => [Color( 0 ), Color( 3 )],
		3 => [Color( 0 ), Color( 1 ), Color( 3 )],
		4 => [Color( 0 ), Color( 1 ), Color( 2 ), Color( 3 )],
		_ => [],
	};

	// The first curve's wave: its shape byte, the periodic frequency and phase, and the range it maps the shape into (identity 0-1, constant 0).
	public int Shape => _data.Length > 0x20 ? _data[0x20] : 0;
	public float Frequency => Type == Periodic ? F( 0x24 ) : 1;
	public float Phase => Type == Periodic ? F( 0x28 ) : 0;
	public float CurveMinimum => Type switch { Periodic => F( 0x2C ), Transition => F( 0x24 ), Exponent => F( 0x20 ), _ => 0 };
	public float CurveMaximum => Type switch { Periodic => F( 0x30 ), Transition => F( 0x28 ), Exponent => F( 0x24 ), Constant => 0, _ => 1 };

	public float F( int offset ) => offset >= 0 && offset + 4 <= _data.Length ? BitConverter.ToSingle( _data, offset ) : 0;

	uint U32( int offset ) => offset >= 0 && offset + 4 <= _data.Length ? BitConverter.ToUInt32( _data, offset ) : 0;

	static int SegmentLength( int kind ) => kind switch { 4 => 16, 7 => 24, 10 => 36, _ => 0 };

	// Bytes of one curve starting at o.
	int CurveLength( int o )
	{
		switch ( Type )
		{
			case Periodic:
				return 20;
			case Transition:
				return 12;
			case Exponent:
				return 12;
			case Piecewise:
			{
				var count = (int)U32( o );
				var length = 4;
				for ( var i = 0; i < count && o + length < _data.Length; i++ )
				{
					var segment = SegmentLength( _data[o + length] );
					if ( segment == 0 )
						break;
					length += segment;
				}
				return length;
			}
			default:
				return 0;
		}
	}

	// The output for input x and range input y: a scalar in the output range, or for a colour function the gradient position (0-1).
	public float Evaluate( float x, float y = 0 )
	{
		if ( IsEmpty )
			return 0;

		var v = Curve( x, y );
		return IsColor ? v : Minimum + (Maximum - Minimum) * v;
	}

	// The curve value (0-1 for most), the range curve blended in by y.
	public float Curve( float x, float y = 0 )
	{
		if ( IsEmpty )
			return 0;

		x = Math.Clamp( x, 0, 1 );
		var v = Finish( CurveAt( 0, x ) );
		if ( IsRange )
			v += (Finish( CurveAt( 1, x ) ) - v) * y;
		return v;
	}

	// Exclusion pushes values past its minimum up by its width; clamp to 0-1.
	float Finish( float v )
	{
		if ( Excludes && v > ExclusionMinimum )
			v += ExclusionMaximum - ExclusionMinimum;
		return Clamps ? Math.Clamp( v, 0, 1 ) : v;
	}

	float CurveAt( int curve, float x )
	{
		var o = _curves[curve];
		switch ( Type )
		{
			case Identity:
				return x;
			case Constant:
				return curve;
			case Transition:
				return Lerp( F( o + 4 ), F( o + 8 ), TransitionShape( _data[o], x ) );
			case Periodic:
				return Lerp( F( o + 12 ), F( o + 16 ), PeriodicShape( _data[o], x, F( o + 4 ), F( o + 8 ) ) );
			case Exponent:
				return Lerp( F( o ), F( o + 4 ), MathF.Pow( MathF.Max( x, 0 ), F( o + 8 ) ) );
			case Piecewise:
			{
				var count = (int)U32( o );
				var s = o + 4;
				var chosen = -1;
				for ( var i = 0; i < count; i++ )
				{
					var length = s + 4 <= _data.Length ? SegmentLength( _data[s] ) : 0;
					if ( length == 0 || s + length > _data.Length )
						break;
					chosen = s;
					if ( F( s + 4 ) >= x )
						break;
					s += length;
				}

				return chosen < 0 ? 0 : Segment( chosen, x );
			}
			default:
				return 0;
		}
	}

	float Segment( int s, float x )
	{
		switch ( _data[s] )
		{
			case 4:
				return F( s + 8 ) * x + F( s + 12 );
			case 10:
			{
				var width = F( s + 28 );
				var t = MathF.Abs( width ) > 1e-6f ? (x - F( s + 24 )) / width : 0;
				return Cubic( s + 8, t );
			}
			default:
				return Cubic( s + 8, x );
		}
	}

	float Cubic( int c, float t ) => ((F( c ) * t + F( c + 4 )) * t + F( c + 8 )) * t + F( c + 12 );

	// Transitions as the compute shader: 0 linear, 1 square root, 2 fourth root, 3 square, 4 fourth power, 5 cos 2 pi x (as the shader has it), 6 one, 7 zero.
	public static float TransitionShape( int shape, float x ) => shape switch
	{
		0 => x,
		1 => MathF.Sqrt( x ),
		2 => MathF.Sqrt( MathF.Sqrt( x ) ),
		3 => x * x,
		4 => x * x * x * x,
		5 => MathF.Cos( 2 * MathF.PI * (x + 1) ),
		6 => 1,
		_ => 0,
	};

	// Periodic waves of frac( x * frequency + phase ) as the compute shader: 0 one, 1 zero, 2/3 cosine, 4/5 diagonal wave, 6/7 slide, 8 noise, 9 jitter, 10 wander, 11 spark (the plain ramp).
	public static float PeriodicShape( int shape, float x, float frequency, float phase )
	{
		if ( shape == 0 )
			return 1;
		if ( shape == 1 )
			return 0;

		var u = x * frequency + phase;
		var t = u - MathF.Floor( u );
		switch ( shape )
		{
			case 2:
			case 3:
				return (MathF.Cos( 2 * MathF.PI * t ) + 1) * 0.5f;
			case 4:
			case 5:
				return t < 0.5f ? 2 * t : 2 - 2 * t;
			case 8:
			{
				var n = MathF.Pow( 2, t ) * 10000;
				return n - MathF.Floor( n );
			}
			case 9:
			case 10:
			{
				var a = MathF.Cos( t * MathF.PI / 2 ) * MathF.Sin( MathF.PI * t );
				var b = MathF.Cos( 8 * MathF.PI * t ) * MathF.Cos( 0.897598f * t );
				var c = MathF.Sin( t * MathF.PI / 2 ) * MathF.Cos( 14 * MathF.PI * t );
				if ( shape == 9 )
					return (a + b + c) * 0.5f + 1;
				var d = MathF.Sin( MathF.PI * t ) * MathF.Cos( 2 * MathF.PI * t );
				return (d + b * 0.5f + c * 0.5f) * 0.5f + 1;
			}
			default:
				return t;
		}
	}

	// The colour (ARGB) at gradient position f, each byte blended and rounded on its own as Halo 2's gradient; white for a scalar function.
	public uint ColorAt( float f )
	{
		f = Math.Clamp( f, 0, 1 );
		switch ( ColorCount )
		{
			case 1:
				return Color( 0 );
			case 2:
				return Blend( Color( 0 ), Color( 3 ), f );
			case 3:
				return f < 0.5f ? Blend( Color( 0 ), Color( 1 ), f * 2 ) : Blend( Color( 1 ), Color( 3 ), f * 2 - 1 );
			case 4:
			{
				var x = f * 3;
				var segment = Math.Min( (int)x, 2 );
				return Blend( Color( segment ), Color( segment + 1 ), x - segment );
			}
			default:
				return 0xFFFFFFFF;
		}
	}

	static uint Blend( uint a, uint b, float t )
	{
		uint result = 0;
		for ( var shift = 0; shift < 32; shift += 8 )
		{
			var value = (int)MathF.Round( (1 - t) * (a >> shift & 0xFF) + t * (b >> shift & 0xFF), MidpointRounding.AwayFromZero );
			result |= (uint)Math.Clamp( value, 0, 255 ) << shift;
		}

		return result;
	}

	// An ARGB colour as (r, g, b), 0-1.
	public static System.Numerics.Vector3 ToVector( uint argb ) => new( (argb >> 16 & 0xFF) / 255f, (argb >> 8 & 0xFF) / 255f, (argb & 0xFF) / 255f );

	// The highest the function's output can be (its output range ends), for how long particles may live.
	public float Highest => IsEmpty ? 0 : MathF.Max( Minimum, Maximum );

	static float Lerp( float a, float b, float t ) => a + (b - a) * t;

	public override string ToString() => IsEmpty ? "-" : IsColor
		? $"{TypeName}{(IsRange ? " range" : "")} colours [{string.Join( ", ", Enumerable.Range( 0, 4 ).Select( i => ColorAt( i / 3f ).ToString( "X8" ) ).Distinct() )}]"
		: $"{TypeName}{(IsRange ? " range" : "")} [{Minimum:0.###}..{Maximum:0.###}]";
}
