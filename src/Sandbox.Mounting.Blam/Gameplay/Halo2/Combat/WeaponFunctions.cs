using System;
using System.Runtime.CompilerServices;
using Halo2.Data;
using Halo2.Combat;

namespace Halo2.Combat;

// A weapon's object functions: its tag's functions over Halo 2's weapon inputs, held or lying in the world.
// They light and show the weapon's attachments (muzzle lights, the plasma pistol's charge) and drive its shaders' gauges.
public static class WeaponFunctions {
	sealed class Held {
		public WeaponTag Tag;
		public ObjectFunctions Functions;
	}

	static readonly ConditionalWeakTable<IHalo2Hands, Held> Cache = new();
	static readonly ConditionalWeakTable<IHalo2Hands, Held> LeftCache = new();

	// A hand's weapon's functions; null without a weapon in that hand.
	public static ObjectFunctions For( IHalo2Hands spartan, Hand hand = Hand.Right ) {
		var tag = spartan?.HandTag( hand );
		if ( tag is null ) return null;
		var held = (hand == Hand.Left ? LeftCache : Cache).GetValue( spartan, _ => new Held() );
		if ( held.Tag != tag ) {
			held.Tag = tag;
			held.Functions = new ObjectFunctions( tag.Functions, name => Input( spartan, hand, tag, name ), RandomConstant( spartan.GameObject ) );
		}
		return held.Functions;
	}

	static float RandomConstant( GameObject go ) => (uint)go.Id.GetHashCode() / (float)uint.MaxValue;

	// Halo 2's weapon inputs for a held weapon, null for one it doesn't provide.
	static float? Input( IHalo2Hands spartan, Hand hand, WeaponTag tag, string name ) {
		var weapons = spartan;
		// Another machine's player sends no rounds, so its ammunition inputs are unknown.
		int? rounds = weapons.Simulated ? weapons.HandState( hand ).Loaded : null;
		var heat = weapons.HandHeatLevel( hand );
		var age = weapons.HandAge( hand );
		return name switch {
			"heat" => heat,
			"age" => age,
			// A held weapon is ready, and turned on once its turn on time has passed since it was readied.
			"ready" => 1,
			"turned_on" => weapons.HandSinceReadied( hand ) >= tag.TurnOnTime && age < 1 ? 1 : 0,
			"primary_charged" => weapons.HandChargeLevel( hand ),
			"primary_firing" => weapons.HandPrimaryFiring( hand ),
			"overheated" => weapons.HandOverheated( hand ) && tag.HeatRecoveryThreshold < 1 ? (heat - tag.HeatRecoveryThreshold) / (1 - tag.HeatRecoveryThreshold) : 0,
			"illumination" => Illumination( weapons, hand, tag ),
			_ => Ammunition( tag, rounds, name ),
		};
	}

	// The ammunition inputs from the first magazine's loaded rounds, 0 without a magazine; null for unknown rounds or another name.
	// The digit inputs are the rounds' ones and tens, each over 10, for the battle rifle's counter.
	static float? Ammunition( WeaponTag tag, int? rounds, string name ) {
		if ( rounds is not { } r || name is not ("primary_ammunition" or "primary_ammunition_ones" or "primary_ammunition_tens") ) return null;
		if ( tag.Magazine( 0 ) is not { } magazine ) return 0;
		return name switch {
			"primary_ammunition" => magazine.RoundsLoadedMaximum > 0 ? (float)r / magazine.RoundsLoadedMaximum : 0,
			"primary_ammunition_ones" => r % 10 / 10f,
			_ => r / 10 % 10 / 10f,
		};
	}

	// The brightest of the last shot's barrel (lit on firing, dimming at its recovery rate), the charge and the heat.
	static float Illumination( IHalo2Hands weapons, Hand hand, WeaponTag tag ) {
		var barrel = tag.Barrel( weapons.HandLastFiredBarrel( hand ) );
		var shot = barrel is null ? 0 : 1 - weapons.HandSinceFired( hand ) * barrel.IlluminationRecoveryRate;
		var charge = weapons.HandChargeLevel( hand ) * (tag.Trigger( 0 )?.ChargedIllumination ?? 0);
		var heat = weapons.HandHeatLevel( hand ) * tag.HeatIllumination;
		return MathF.Max( shot, MathF.Max( charge, heat ) );
	}
}
