using System;

namespace Halo.Combat;

// A weapon's A-D function outputs, matching what each weapon tag exports.
// The shader reads them for gauges, needles and muzzle glows, and they scale the weapon's attachments.
// An output left null keeps its rest value.
public static class WeaponFunctions {
	// Seconds a muzzle glow ("illumination") takes to fade after a shot.
	const float IlluminationFade = 0.1f;

	// What a weapon's functions read from the unit holding it.
	public struct Inputs {
		public float Heat;
		public float Age;
		// Seconds since the weapon last fired.
		public float SinceFired;
		// The plasma pistol's charge, 0..1.
		public float Charge;
		public int Loaded;
		public bool PrimaryFiring;
		// The compass heading, 0..1 with 0.5 at local north.
		public float Compass;
	}

	public static float?[] Outputs( string path, Data.WeaponTag tag, in Inputs inputs ) {
		var outputs = new float?[4];
		if ( string.IsNullOrEmpty( path ) ) return outputs;

		var heat = inputs.Heat;
		var illumination = MathF.Max( 0, 1 - inputs.SinceFired / IlluminationFade );
		path = path.Replace( '\\', '/' );

		if ( path.Contains( "/plasma rifle/" ) ) {
			outputs[1] = heat.Clamp( 0.1f, 1 );
		}
		else if ( path.Contains( "/plasma pistol/" ) ) {
			// A = 1 - age, B = heat, C = illumination, D = primary charged.
			outputs[0] = 1 - inputs.Age.Clamp( 0, 1 );
			outputs[1] = heat.Clamp( 0, 1 );
			outputs[2] = illumination;
			outputs[3] = inputs.Charge.Clamp( 0, 1 );
		}
		else if ( path.Contains( "/needler/" ) ) {
			var max = tag?.Magazine( 0 )?.RoundsLoadedMaximum ?? 0;
			outputs[0] = illumination;
			outputs[2] = max > 0 ? 1 - (float)inputs.Loaded / max : 0;
		}
		else if ( path.Contains( "/plasma_cannon/" ) || path.Contains( "/plasma cannon/" ) ) {
			outputs[0] = heat.Clamp( 0.1f, 1 );
			outputs[1] = inputs.Age.Clamp( 0, 1 );
		}
		else if ( path.Contains( "/flamethrower/" ) ) {
			// A and D = ammunition, B = primary firing (turns on the flames), C = inverted heat.
			var max = tag?.Magazine( 0 )?.RoundsLoadedMaximum ?? 0;
			var ammunition = max > 0 ? Math.Clamp( (float)inputs.Loaded / max, 0, 1 ) : 0;
			outputs[0] = ammunition;
			outputs[1] = inputs.PrimaryFiring ? 1 : 0;
			outputs[2] = 1 - heat.Clamp( 0, 1 );
			outputs[3] = ammunition;
		}
		else if ( path.Contains( "/assault rifle/" ) ) {
			// A = rounds loaded over magazine size, shown on the counter as round( 60 x A ).
			// B = muzzle flash, D = compass heading, shown as one of 8 frames.
			var max = tag?.Magazine( 0 )?.RoundsLoadedMaximum ?? 0;
			outputs[0] = max > 0 ? Math.Clamp( (float)inputs.Loaded / max, 0, 1 ) : 0;
			outputs[1] = illumination;
			outputs[3] = inputs.Compass;
		}
		else if ( path.Contains( "/sniper rifle/" ) ) {
			outputs[0] = illumination;
		}

		return outputs;
	}

	sealed class CompassValue {
		// The compass input's rest value.
		public float Value = 0.5f;
	}

	static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, CompassValue> _compass = new();

	// The signed angle from the map's local north to the holder's aim, as a fraction of a turn plus 0.5.
	// Within about 5.7 degrees of straight up or down it keeps its last value.
	// Halo reads the weapon's root node, which follows the aim.
	public static float Compass( object holder, Vector3 forward ) {
		var last = _compass.GetValue( holder, _ => new CompassValue() );
		if ( MathF.Abs( forward.z ) < 0.995f ) {
			const float north = 0;
			var angle = MathF.Atan2( forward.x, forward.y ) - north;
			if ( angle >= MathF.PI ) angle -= 2 * MathF.PI;
			if ( angle <= -MathF.PI ) angle += 2 * MathF.PI;
			last.Value = Math.Clamp( 0.15915494f * angle + 0.5f, 0, 1 );
		}
		return last.Value;
	}

	// Sets the outputs on a weapon model and its glass for the mount's shader.
	public static void Apply( ModelRenderer renderer, float?[] outputs ) {
		if ( !renderer.IsValid() ) return;
		for ( var i = 0; i < 4; i++ )
			if ( outputs[i] is float v ) HaloResources.SetFunction( renderer, (char)('A' + i), v );
	}

}
