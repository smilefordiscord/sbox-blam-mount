using System;
using Halo2.Data;

namespace Halo2.Combat;

// Halo 2's object functions: an object's named values from 0 to 1, each on or off, evaluated fresh on every query.
// A name is "one" or empty (on at 1), "zero", an engine input of the object's type, or an export of its tag's functions.
// Of the functions exporting a name, the last wins.
// A function maps its input through its curve, then another function scales it.
// It stays off under its minimum value, and another input can turn it off.
public sealed class ObjectFunctions {
	readonly List<ObjectFunctionTag> _functions;
	readonly Func<string, float?> _inputs;
	readonly float _randomConstant;
	readonly Dictionary<ObjectFunctionTag, MappingFunction> _curves = new();

	// inputs gives the engine's value for a name it knows, else null. randomConstant is the object's own, 0 to 1.
	public ObjectFunctions( List<ObjectFunctionTag> functions, Func<string, float?> inputs, float randomConstant ) {
		_functions = functions ?? new();
		_inputs = inputs;
		_randomConstant = randomConstant;
	}

	// The value, 0 when off.
	public float Value( string name ) => Get( name ).Value;

	public (float Value, bool On) Get( string name ) {
		if ( string.IsNullOrEmpty( name ) || name == "one" ) return (1, true);
		if ( name == "zero" ) return (0, false);
		if ( Input( name ) is { } input ) return input;

		for ( var i = _functions.Count - 1; i >= 0; i-- )
			if ( _functions[i].ExportName == name ) return Evaluate( _functions[i] );
		return (0, false);
	}

	// Whether the object can evaluate the name: an input it provides, or an export whose inputs and scale it provides.
	public bool Provides( string name ) {
		if ( Input( name ) is not null ) return true;
		for ( var i = _functions.Count - 1; i >= 0; i-- ) {
			var f = _functions[i];
			if ( f.ExportName != name ) continue;
			return Known( f.ImportName ) && Known( f.TurnOffWith ) && (string.IsNullOrEmpty( f.ScaleBy ) || Provides( f.ScaleBy ));
		}
		return false;
	}

	bool Known( string input ) => string.IsNullOrEmpty( input ) || input is "one" or "zero" || Input( input ) is not null;

	// An engine input clamped to 0-1 and on above 0; every object is alive and has its random constant.
	(float Value, bool On)? Input( string name ) {
		var value = name switch {
			"alive" => 1,
			"random_constant" => _randomConstant,
			_ => _inputs?.Invoke( name ),
		};
		if ( value is not { } v ) return null;
		v = Math.Clamp( v, 0, 1 );
		return (v, v > 0);
	}

	(float Value, bool On) Evaluate( ObjectFunctionTag f ) {
		var (input, on) = Input( f.ImportName ) ?? (0, false);
		if ( !_curves.TryGetValue( f, out var curve ) ) _curves[f] = curve = new MappingFunction( f.DefaultFunction );

		// A periodic curve runs on time, from the object's random offset if flagged, scaling the input.
		// Otherwise it maps the input, which also decides whether it's on unless flagged not to.
		// The range input is always 1.
		var mappingSetsOn = !f.HasFlag( "mapping does not controls active" );
		float value;
		if ( curve.IsPeriodic ) {
			var time = HaloTick.Count * HaloUnits.TickInterval + (f.HasFlag( "random time offset" ) ? _randomConstant : 0);
			value = input * curve.Evaluate( time, 1 );
		}
		else if ( on || mappingSetsOn ) {
			value = curve.Evaluate( f.HasFlag( "invert" ) ? 1 - input : input, 1 );
			if ( mappingSetsOn ) on = value > 0;
		}
		else value = input;

		if ( !string.IsNullOrEmpty( f.ScaleBy ) ) {
			var (scale, scaleOn) = Get( f.ScaleBy );
			value *= scale;
			on &= scaleOn;
		}
		if ( on && f.MinimumValue > 0 ) on = value > f.MinimumValue;
		if ( f.HasFlag( "always active" ) ) on = true;
		if ( on && !string.IsNullOrEmpty( f.TurnOffWith ) ) on = Input( f.TurnOffWith )?.On ?? false;
		return on ? (Math.Clamp( value, 0, 1 ), true) : (0, false);
	}
}
