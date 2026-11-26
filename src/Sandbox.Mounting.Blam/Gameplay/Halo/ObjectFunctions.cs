using System;

namespace Halo;

// One of an object tag's functions, computing an A to D "out" value each tick from the A to D "in" values.
// Value references are 0 none, 1-4 A-D in, 5-8 A-D out, and periods are in seconds.
// Bounds modes are 0 clip, 1 clip and normalize, 2 scale to fit.
public sealed class ObjectFunctionDefinition {
	public bool Invert { get; init; }

	// The output adds to its last value, modulo 1.
	public bool Additive { get; init; }

	// Stays active at its lower bound.
	public bool AlwaysActive { get; init; }

	public float Period { get; init; } = 1;
	public int ScalePeriodBy { get; init; }
	public string Function { get; init; } = "one";
	public int ScaleFunctionBy { get; init; }
	public string WobbleFunction { get; init; } = "one";
	public float WobblePeriod { get; init; }
	public float WobbleMagnitude { get; init; }
	public float SquareWaveThreshold { get; init; }
	public int StepCount { get; init; }
	public string MapTo { get; init; } = "linear";
	public int SawtoothCount { get; init; }
	public int Add { get; init; }
	public int ScaleResultBy { get; init; }
	public int BoundsMode { get; init; }
	public float LowerBound { get; init; }
	public float UpperBound { get; init; } = 1;

	// The function (0-3) whose activity this one depends on, -1 for none.
	public int TurnOffWith { get; init; } = -1;

	public float ScaleBy { get; init; }

	// The tag's name for it, like "headlights" or "engine".
	public string Usage { get; init; }
}

// An object's function inputs, outputs and active flags, and the per-tick step that computes the outputs.
public sealed class ObjectFunctionState {
	public const int Count = 4;

	// A to D in, written by the object's exporters before Compute.
	public readonly float[] Inputs = new float[Count];

	public readonly float[] Outputs = new float[Count];

	// Whether each output is active, which looping sounds, contrails, particles and effects play by.
	public readonly bool[] Active = new bool[Count];

	// How many functions the object has; the rest stay 0 and inactive.
	public int FunctionCount { get; private set; }

	// The value at a 1-based reference: 1-4 A-D in, 5-8 A-D out.
	public float Get( int reference ) => reference switch {
		>= 1 and <= 4 => Inputs[reference - 1],
		>= 5 and <= 8 => Outputs[reference - 5],
		_ => 0,
	};

	// time is game ticks; objectOffset is the object's index, which staggers its waves by 57 ticks per index.
	public void Compute( IReadOnlyList<ObjectFunctionDefinition> functions, long time, int objectOffset ) {
		FunctionCount = Math.Min( functions?.Count ?? 0, Count );
		var seconds = (57f * objectOffset + time) * HaloUnits.TickInterval;

		for ( var index = 0; index < FunctionCount; index++ ) {
			var function = functions[index];
			var active = true;
			var period = function.Period != 0 ? 1 / function.Period : 1;

			if ( function.ScalePeriodBy != 0 ) {
				var value = Get( function.ScalePeriodBy );
				if ( value > 0 ) period /= value;
			}

			var result = HaloFunctions.Periodic( function.Function, seconds * period );

			if ( function.ScaleFunctionBy != 0 )
				result *= Get( function.ScaleFunctionBy );

			if ( function.Invert )
				result = 1 - result;

			if ( function.WobbleMagnitude != 0 ) {
				var wobble = HaloFunctions.Periodic( function.WobbleFunction, seconds * function.WobblePeriod );
				result += 2 * function.WobbleMagnitude * (wobble - 0.5f);
			}

			if ( function.SquareWaveThreshold != 0 )
				result = result > function.SquareWaveThreshold ? 1 : 0;

			if ( function.StepCount > 1 )
				result = MathF.Floor( function.StepCount * result ) * (1f / (function.StepCount - 1));

			if ( function.SawtoothCount > 0 )
				result %= 1f / function.SawtoothCount;

			if ( function.Add != 0 )
				result = MathF.Min( result + Get( function.Add ), 1 );

			if ( function.ScaleResultBy != 0 )
				result *= Get( function.ScaleResultBy );

			var output = HaloFunctions.Transition( function.MapTo, result );

			if ( function.ScaleBy > 0 )
				output *= function.ScaleBy;

			const float Epsilon = 0.0001f;
			if ( function.BoundsMode == 2 ) {
				output = output * (function.UpperBound - function.LowerBound) + function.LowerBound;
				if ( function.LowerBound + Epsilon >= output )
					active = function.AlwaysActive;
			}
			else {
				if ( function.LowerBound + Epsilon >= output ) {
					output = function.LowerBound;
					active = function.AlwaysActive;
				}

				if ( output > function.UpperBound )
					output = function.UpperBound;

				if ( function.BoundsMode == 1 ) {
					var range = function.UpperBound - function.LowerBound;
					output = range != 0 ? (output - function.LowerBound) / range : 0;
				}
			}

			if ( function.TurnOffWith >= 0 && function.TurnOffWith < Count && !Active[function.TurnOffWith] )
				active = false;

			if ( function.Additive )
				output = (output + Outputs[index]) % 1f;

			Outputs[index] = output;
			Active[index] = active;
		}
	}

	// The outputs for the attachments, null where the object has no function.
	public float?[] OutputsOrNull() {
		var values = new float?[Count];
		for ( var i = 0; i < FunctionCount; i++ ) values[i] = Outputs[i];
		return values;
	}
}
