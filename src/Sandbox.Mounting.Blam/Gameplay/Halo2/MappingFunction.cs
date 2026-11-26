using Halo2.Data;
using System;

namespace Halo2.Combat;

// A Halo 2 mapping function, the curve blob object functions and lights use, as the mount exports it in base64.
// A header holds the type, flags with bit 0 for a range, and the shapes of the curve and the range curve.
// The output minimum and maximum are at 0x4 and 0x8, then each curve's values from 0x14.
// This is its scalar output, as Halo 2 evaluates it.
public sealed class MappingFunction {
	const int Identity = 0, Constant = 1, Transition = 2, Periodic = 3, Linear = 4, LinearKey = 5, Spline = 7, Exponent = 9, Spline2 = 10;

	// Floats per curve of each type; a range function's second curve follows its first.
	static readonly int[] CurveSize = [0, 1, 2, 4, 6, 20, 32, 12, 4, 3, 12];

	readonly byte[] _data;
	readonly int _type;
	readonly bool _range;
	readonly int _shape;
	readonly int _rangeShape;

	public MappingFunction( string base64 ) {
		_data = string.IsNullOrEmpty( base64 ) ? Array.Empty<byte>() : Convert.FromBase64String( base64 );
		if ( _data.Length < 4 ) return;
		_type = _data[0];
		_range = (_data[1] & 1) != 0;
		_shape = _data[2];
		_rangeShape = _data[3];
	}

	// Shorter than its header and the values its type needs.
	public bool IsEmpty => _data.Length < 0x14 || _type > Spline2 || _data.Length < 0x14 + 4 * CurveSize[_type] * (_range ? 2 : 1);

	// The output at x, a range function blended to its second curve by y. An empty function gives 0.
	public float Evaluate( float x, float y ) {
		if ( IsEmpty ) return 0;
		var f = _type == Constant ? (_range ? y : 0) : CurveAt( 0, _shape, x );
		if ( _range && _type != Constant ) f = Lerp( f, CurveAt( 1, _rangeShape, x ), y );
		return Lerp( F( 0x4 ), F( 0x8 ), Math.Clamp( f, 0, 1 ) );
	}

	// A periodic function is driven by time rather than mapping its input.
	public bool IsPeriodic => _type == Periodic;

	float F( int offset ) => offset >= 0 && offset + 4 <= _data.Length ? BitConverter.ToSingle( _data, offset ) : 0;

	float CurveAt( int curve, int shape, float x ) {
		var o = 0x14 + 4 * CurveSize[_type] * curve;
		switch ( _type ) {
			case Identity:
				return x;
			case Transition:
				return Lerp( F( o ), F( o + 4 ), HaloFunctions.TransitionShape( shape, x ) );
			case Periodic:
				return Lerp( F( o + 8 ), F( o + 12 ), HaloFunctions.PeriodicShape( shape, x * F( o ) + F( o + 4 ) ) );
			case Linear:
				// Two points, then the slope and intercept the engine uses.
				return x * F( o + 16 ) + F( o + 20 );
			case LinearKey:
				// Four points, then their x, 1 / the segments' widths and the steps between their y.
				return F( o + 0x40 ) + F( o + 0x44 ) * Saturate( (x - F( o + 0x24 )) * F( o + 0x34 ) )
					+ F( o + 0x48 ) * Saturate( (x - F( o + 0x28 )) * F( o + 0x38 ) )
					+ F( o + 0x4C ) * Saturate( (x - F( o + 0x2C )) * F( o + 0x3C ) );
			case Spline:
			case Spline2: {
				// The cubic through the first and last points, the inner points' slopes as tangents.
				var t = _type == Spline2 ? Spline2Input( o, x ) : x;
				return ((F( o + 0x20 ) * t + F( o + 0x24 )) * t + F( o + 0x28 )) * t + F( o + 0x2C );
			}
			case Exponent: {
				var exponent = F( o + 8 );
				if ( MathF.Abs( exponent ) < 1e-4f || (exponent < 0 && MathF.Abs( x ) < 1e-4f) ) return 1;
				return Lerp( F( o ), F( o + 4 ), MathF.Pow( MathF.Max( x, 0 ), exponent ) );
			}
			default:
				// Multi-linear key and multi-spline: the engine gives 0.
				return 0;
		}
	}

	// Spline2 bends x by the ratio of its control polygon's first and last segments.
	float Spline2Input( int o, float x ) {
		float Distance2( int a, int b ) => Square( F( o + a ) - F( o + b ) ) + Square( F( o + a + 4 ) - F( o + b + 4 ) );
		var end = Distance2( 0x10, 0x18 );
		if ( end <= 1e-4f ) return x;
		var start = Distance2( 0x0, 0x8 );
		if ( MathF.Abs( start ) < 1e-4f ) return 1;
		if ( MathF.Abs( start - end ) < 1e-4f ) return x;
		var t = MathF.Pow( x, MathF.Sqrt( start ) / MathF.Sqrt( end ) );
		return t < 0 ? 0 : MathF.Min( t, 1 );
	}

	static float Square( float x ) => x * x;
	static float Lerp( float a, float b, float t ) => a + (b - a) * t;
	static float Saturate( float x ) => Math.Clamp( x, 0, 1 );
}
