using System;

namespace Halo;

// Halo's transition and periodic functions, as 1024-entry byte tables read with linear interpolation.
// The bytes carry Halo's 1/255 quantization.
// The random tables use Halo's own generator and seed, so they match the game's.
public static class HaloFunctions {
	const int TableSize = 1024;
	const int TableMask = TableSize - 1;

	static readonly string[] TransitionNames = { "linear", "early", "very early", "late", "very late", "cosine" };
	static readonly string[] PeriodicNames = {
		"one", "zero", "cosine", "cosine variable period", "diagonal wave", "diagonal wave variable period",
		"slide", "slide variable period", "noise", "jitter", "wander", "spark",
	};

	const int PeriodicOne = 0, PeriodicSlide = 6, PeriodicSlideVariablePeriod = 7;

	static byte[][] _periodic;
	static byte[][] _transition;

	// A transition function such as "very late", at 0 to 1.
	public static float Transition( string function, float value ) {
		value = Math.Clamp( value, 0, 1 );
		var type = Array.IndexOf( TransitionNames, function );
		if ( type <= 0 ) return value;

		Build();
		var table = _transition[type];
		var scaled = value * (TableSize - 1);
		var fraction = scaled % 1;
		var index = FastFtol( scaled - 0.5f );
		if ( (short)index == TableSize - 1 )
			return table[TableSize - 1] * (1f / 255f);

		var first = table[(short)index] * (1f / 255f);
		var second = table[(short)index + 1] * (1f / 255f);
		return first * (1 - fraction) + second * fraction;
	}

	// A periodic function such as "cosine", at a time in periods; returns 0 to 1.
	public static float Periodic( string function, float time ) {
		var type = Array.IndexOf( PeriodicNames, function );
		if ( type < 0 || type == PeriodicOne ) return 1;

		Build();
		time *= 25.6f;
		var fraction = time % 1;
		var index = FastFtol( time - fraction );
		var table = _periodic[type];
		index &= TableMask;
		var first = table[index] * (1f / 255f);
		index = (index + 1) & TableMask;
		var second = table[index] * (1f / 255f);

		if ( IsSlide( type ) ) {
			if ( first > 0.75f && second < 0.25f )
				second += 1;

			var result = (1 - fraction) * first + second * fraction;
			return result > 1 ? result - 1 : result;
		}

		return (1 - fraction) * first + second * fraction;
	}

	static bool IsSlide( int type ) => type is PeriodicSlide or PeriodicSlideVariablePeriod;

	// The x87's default rounding: to nearest, ties to even.
	static int FastFtol( float value ) => (int)MathF.Round( value, MidpointRounding.ToEven );

	// Tables

	static uint _seed;

	// Halo's LCG on the global seed, 0 to 1.
	static float RealRandom() {
		_seed = unchecked(_seed * 1664525u + 1013904223u);
		return (_seed >> 16) / 65535f;
	}

	static void Build() {
		if ( _periodic is not null ) return;

		_seed = 0x20F3F660;
		var periodic = new byte[PeriodicNames.Length][];
		for ( var i = 0; i < periodic.Length; i++ )
			periodic[i] = BuildPeriodic( i );

		var transition = new byte[TransitionNames.Length][];
		for ( var i = 0; i < transition.Length; i++ )
			transition[i] = BuildTransition( i );

		_transition = transition;
		_periodic = periodic;
	}

	static float[] VariablePeriodTable() {
		var table = new float[TableSize];
		var sum = 0f;
		for ( var index = 0; index < TableSize; index++ ) {
			table[index] = sum;
			sum += RealRandom() * 0.25f + 0.25f +
				((float)Math.Cos( 8.2f * MathF.PI * index / TableSize ) + 1) * RealRandom() +
				((float)Math.Cos( 10.2f * MathF.PI * index / TableSize ) + 1) * RealRandom() +
				((float)Math.Cos( 14.6f * MathF.PI * index / TableSize ) + 1) * RealRandom();
		}

		for ( var index = 0; index < TableSize; index++ )
			table[index] /= sum;

		return table;
	}

	static byte[] BuildTransition( int type ) {
		var table = new byte[TableSize];
		for ( var index = 0; index < TableSize; index++ ) {
			var value = index * (1f / (TableSize - 1));
			var result = type switch {
				1 => (float)Math.Pow( value, 0.5 ),
				2 => (float)Math.Pow( value, 0.25 ),
				3 => (float)Math.Pow( value, 2.0 ),
				4 => (float)Math.Pow( value, 4.0 ),
				5 => ((float)Math.Sin( value * 3.1415927f - 1.5707964f ) + 1) * 0.5f,
				_ => value,
			};
			table[index] = (byte)Math.Clamp( (long)(result * 255f), 0, 255 );
		}
		return table;
	}

	static byte[] BuildPeriodic( int type ) {
		var randomValues = VariablePeriodTable();
		var values = new float[TableSize];
		var minimum = float.MaxValue;
		var maximum = float.MinValue;

		for ( var index = 0; index < TableSize; index++ ) {
			var x = index * 0.027343748f;
			var randomX = randomValues[index] * 28f;
			float result;
			switch ( type ) {
				case 0: result = 1; break;
				case 1: result = 0; break;
				case 2: result = (float)Math.Cos( x * 6.2831855f ); break;
				case 3: result = (float)Math.Cos( randomX * 6.2831855f ); break;
				case 4: result = DiagonalWave( x % 1 ); break;
				case 5: result = DiagonalWave( randomX % 1 ); break;
				case 6: result = x % 1; break;
				case 7: result = randomX % 1; break;
				case 8: result = RealRandom(); break;
				case 9:
				case 10: {
					var cosineFast = (float)Math.Cos( x * 25.132742f );
					var cosineSlow = (float)Math.Cos( x * 0.89759791f );
					result = (cosineSlow * cosineFast + (float)Math.Cos( x * 43.9823f ) * (float)Math.Sin( x * 1.5707964f )) * 0.5f +
						(float)Math.Sin( x * 3.1415927f ) * (float)Math.Cos( x * 6.2831855f );
					break;
				}
				default:
					result = randomX % 1;
					result *= result;
					break;
			}

			maximum = MathF.Max( maximum, result );
			minimum = MathF.Min( minimum, result );
			values[index] = result;
		}

		var range = IsSlide( type ) ? 0 : maximum - minimum;
		var table = new byte[TableSize];
		for ( var index = 0; index < TableSize; index++ ) {
			var result = range != 0 ? (values[index] - minimum) / range : values[index];
			table[index] = (byte)Math.Clamp( (long)(result * 255f), 0, 255 );
		}
		return table;
	}

	static float DiagonalWave( float result ) => result < 0.5f ? result * 2 : 1 - (result - 0.5f) * 2;
}
