using Halo2.Combat;
using System;

namespace Halo2.Data;

// A Halo 2 weapon tag: its triggers read the controls and fire its barrels, which take rounds from its magazines.
public sealed class WeaponTag {
	// The scale multiplayer draws the weapon at on the ground; 0 means 1.
	public float MultiplayerOnGroundScale { get; set; }

	public string Model { get; set; }
	// The marker a hand holds the weapon by; empty for its origin.
	public string HandleNode { get; set; }
	public string[] WeaponFlags { get; set; } = Array.Empty<string>();
	public float HeatRecoveryThreshold { get; set; }
	public float OverheatedThreshold { get; set; }
	// Fraction per second.
	public float HeatLossPerSecond { get; set; }
	public float OverheatedHeatLossPerSecond { get; set; }
	// Heat loss is scaled by 1 - age x this.
	public float AgeHeatRecoveryPenalty { get; set; }
	// How much slower a used battery fires.
	public float AgeRateOfFirePenalty { get; set; }
	public string Overheated { get; set; }
	public string PlayerMeleeDamage { get; set; }
	public string PlayerMeleeResponse { get; set; }
	// A melee's rays: the yaw and pitch spread in degrees, and their reach in world units.
	public float[] DamagePyramidAngles { get; set; }
	public float DamagePyramidDepth { get; set; }
	public string FirstHitMeleeDamage { get; set; }
	public string FirstHitMeleeResponse { get; set; }
	public string LungeMeleeDamage { get; set; }
	public string LungeMeleeResponse { get; set; }
	public int MagnificationLevels { get; set; }
	public float[] MagnificationRange { get; set; }
	// Degrees and world units.
	public float AutoaimAngle { get; set; }
	public float AutoaimRange { get; set; }
	public float MagnetismAngle { get; set; }
	public float MagnetismRange { get; set; }
	public float DeviationAngle { get; set; }
	public string PickupSound { get; set; }
	// How much firing knocks down active camouflage.
	public float ActiveCamoDing { get; set; }
	public float ActiveCamoRegrowthRate { get; set; }
	public string ZoomInSound { get; set; }
	public string ZoomOutSound { get; set; }
	// "undefined", "shotgun", "needler", "plasma pistol", "plasma rifle" or "rocket launcher".
	public string WeaponType { get; set; }
	// The unit animation class ("rifle", "pistol"...) and type ("br", "smg"...) the holder's animations use.
	public string WeaponClass { get; set; }
	public string WeaponName { get; set; }
	// The first person sway springs' limits, per second; 0 leaves Halo 2's defaults.
	public float MaximumMovementAcceleration { get; set; }
	public float MaximumMovementVelocity { get; set; }
	public float MaximumTurningAcceleration { get; set; }
	public float MaximumTurningVelocity { get; set; }
	// The first person model's offset from the camera: forward, left and up, world units.
	public float[] FirstPersonWeaponOffset { get; set; }
	public List<FirstPersonTag> FirstPerson { get; set; } = new();
	public List<MagazineTag> Magazines { get; set; } = new();
	public List<TriggerTag> NewTriggers { get; set; } = new();
	public List<BarrelTag> Barrels { get; set; } = new();
	public List<ObjectFunctionTag> Functions { get; set; } = new();
	public List<ObjectAttachmentTag> Attachments { get; set; } = new();
	// Seconds after a ready before the weapon turns on, like the sword's blade.
	public float TurnOnTime { get; set; }
	// The weapon this one becomes as it ages, the sword's used up blade; pickups take it as the same kind.
	public string AgedWeapon { get; set; }
	// How much heat lights the weapon's illumination.
	public float HeatIllumination { get; set; }

	public bool HasFlag( string flag ) => WeaponFlags?.Contains( flag ) ?? false;
	public MagazineTag Magazine( int? index ) => index is int i && i >= 0 && i < (Magazines?.Count ?? 0) ? Magazines[i] : null;
	public TriggerTag Trigger( int index ) => index >= 0 && index < (NewTriggers?.Count ?? 0) ? NewTriggers[index] : null;
	public BarrelTag Barrel( int? index ) => index is int i && i >= 0 && i < (Barrels?.Count ?? 0) ? Barrels[i] : null;

	// The Spartan's first person view model and animations.
	public string FirstPersonAnimations => FirstPerson?.FirstOrDefault()?.FirstPersonAnimations;

	// A melee only weapon, like the energy sword, attacks with the trigger.
	public bool MeleeOnly => HasFlag( "melee only" );

	public bool CanDualWield => HasFlag( "can be dual wielded" ) || HasFlag( "can only be dual wielded" );

	// A hand holding one, like the flag, takes no other weapon.
	public bool MustBeReadied => HasFlag( "must be readied" );

	// A unit carries at most two weapons that count.
	public bool CountsTowardMaximum => !HasFlag( "doesn't count toward maximum" );
}

public sealed class FirstPersonTag {
	public string FirstPersonAnimations { get; set; }
}

public sealed class MagazineTag {
	public string[] Flags { get; set; } = Array.Empty<string>();
	// Rounds per second a battery magazine recharges.
	public int RoundsRecharged { get; set; }
	public int RoundsTotalInitial { get; set; }
	// A magazine with none holds no rounds at all, like the plasma rifle's.
	public int RoundsTotalMaximum { get; set; }
	public int RoundsLoadedMaximum { get; set; }
	public int RoundsInventoryMaximum { get; set; }
	public int RoundsReloaded { get; set; }
	// Seconds.
	public float ChamberTime { get; set; }
	public List<MagazineEquipmentTag> MagazineEquipment { get; set; } = new();

	public bool HasFlag( string flag ) => Flags?.Contains( flag ) ?? false;
}

// An ammo pickup this magazine takes rounds from.
public sealed class MagazineEquipmentTag {
	public int Rounds { get; set; }
	public string Equipment { get; set; }
}

public sealed class TriggerTag {
	public string[] Flags { get; set; } = Array.Empty<string>();
	// "spew", "latch", "latch-autofire", "charge", "latch-zoom" or "latch-rocketlauncher".
	public string Behavior { get; set; }
	public int? PrimaryBarrel { get; set; }
	public int? SecondaryBarrel { get; set; }
	// Seconds a press is held before latch-autofire takes it as held.
	public float AutofireTime { get; set; }
	// How far the trigger must be pressed for that, 0 to 1.
	public float AutofireThrow { get; set; }
	// "fire", "charge", "track" or "fire other".
	public string PrimaryAction { get; set; }
	public string SecondaryAction { get; set; }
	// Seconds.
	public float ChargingTime { get; set; }
	public float ChargedTime { get; set; }
	// "none", "explode" or "discharge".
	public string OverchargedAction { get; set; }
	// How much the charge lights the weapon's illumination.
	public float ChargedIllumination { get; set; }

	public bool HasFlag( string flag ) => Flags?.Contains( flag ) ?? false;
}

public sealed class BarrelTag {
	public string[] Flags { get; set; } = Array.Empty<string>();
	// Rounds per second as the barrel spins up, from the first to the second.
	public float[] RoundsPerSecond { get; set; }
	// Shots a single fire makes: the BR's three round burst.
	public int[] ShotsPerFire { get; set; }
	// Seconds after a burst before the barrel fires again.
	public float FireRecoveryTime { get; set; }
	// The share of the recovery a new press can be queued in.
	public float SoftRecoveryFraction { get; set; }
	public int? Magazine { get; set; }
	public int RoundsPerShot { get; set; }
	public int MinimumRoundsLoaded { get; set; }
	// Shots without a contrail between tracers.
	public int RoundsBetweenTracers { get; set; }
	public int ProjectilesPerShot { get; set; }
	// Degrees.
	public float MinimumError { get; set; }
	public float[] ErrorAngle { get; set; }
	// Dual wielding, the error's ramp times in seconds, the spread in degrees and the damage scale blend toward these.
	// A ramp time of 0 is instant, and an error angle bound of 0 keeps the single wielded one.
	public float DualWieldErrorAccelerationTime { get; set; }
	public float DualWieldErrorDecelerationTime { get; set; }
	public float DualWieldMinimumError { get; set; }
	public float[] DualWieldErrorAngle { get; set; }
	public float DualWieldDamageScale { get; set; }
	// Forward, left and up of the camera, world units.
	public float[] FirstPersonOffset { get; set; }
	public string Projectile { get; set; }
	public float HeatGeneratedPerRound { get; set; }
	public float AgeGeneratedPerRound { get; set; }
	// The recoil kick per shot, degrees, from the first to the second as the kick builds.
	public float[] AngleChangePerShot { get; set; }
	public string AngleChangeFunction { get; set; }
	// Per second ramps: the rate of fire, the error and the recoil kick, up while firing and down otherwise.
	public float RateOfFireAccelerationRate { get; set; }
	public float RateOfFireDecelerationRate { get; set; }
	public float ErrorAccelerationRate { get; set; }
	public float ErrorDecelerationRate { get; set; }
	public float AngleChangeAccelerationRate { get; set; }
	public float AngleChangeDecelerationRate { get; set; }
	public List<FiringEffectTag> FiringEffects { get; set; } = new();
	// Per second: a shot lights the barrel's illumination fully, then it falls at this rate.
	public float IlluminationRecoveryRate { get; set; }

	public bool HasFlag( string flag ) => Flags?.Contains( flag ) ?? false;
	public int ShotsPerFireMinimum => ShotsPerFire is { Length: > 0 } s ? s[0] : 0;
	public int ShotsPerFireMaximum => ShotsPerFire is { Length: > 1 } s ? s[1] : ShotsPerFireMinimum;

	// Halo 2's runtime dual wield error rates per second: one over the time, 1000 for none.
	public float DualErrorAccelerationRate => DualRate( DualWieldErrorAccelerationTime );
	public float DualErrorDecelerationRate => DualRate( DualWieldErrorDecelerationTime );
	static float DualRate( float seconds ) => seconds > 0 ? 1 / seconds : 1000;

	// The error angle's bounds blended toward the dual wield ones, each kept where its dual wield bound is 0.
	public (float Low, float High) ErrorAngleBounds( float dual ) {
		var e = ErrorAngle;
		var d = DualWieldErrorAngle is { Length: > 1 } angle ? angle : e;
		var dualLow = d[0] != 0 ? d[0] : e[0];
		var dualHigh = d[1] != 0 ? d[1] : e[1];
		return ((1 - dual) * e[0] + dual * dualLow, (1 - dual) * e[1] + dual * dualHigh);
	}

	// Ticks between shots at a point of the spin up: 30 over the rate rounded to whole ticks, plus the age penalty.
	// No rate means no limit, so only the shots per fire and the recovery pace the barrel.
	// The float operations follow Halo 2's order so the tick boundaries match.
	public float TicksPerShot( float rateOfFire, float age, float ageRateOfFirePenalty ) {
		var low = RoundsPerSecond is { Length: > 0 } r ? r[0] : 0;
		var high = RoundsPerSecond is { Length: > 1 } r2 ? r2[1] : low;
		var rate = (high - low) * rateOfFire + low;
		var ticks = 0f;
		if ( rate > 0.0001f ) {
			var whole = MathF.Ceiling( 30f / rate );
			var quantized = 30f / whole;
			if ( quantized > 0.0001f ) ticks = HaloUnits.TicksPerSecond / quantized;
		}
		if ( ageRateOfFirePenalty > 0 )
			ticks += ticks * age * ageRateOfFirePenalty;
		return ticks;
	}
}

public sealed class FiringEffectTag {
	public int ShotCountLowerBound { get; set; }
	public int ShotCountUpperBound { get; set; }
	public string FiringEffect { get; set; }
	// The dry fire click, which some weapons play as a sound tag rather than an effect.
	public string EmptyEffect { get; set; }
	// Applied to the firer for camera shake and rumble only, no damage.
	public string FiringDamage { get; set; }
}

// An object function: an engine input mapped through a function, then scaled and gated by others.
public sealed class ObjectFunctionTag {
	// "invert", "mapping does not controls active", "always active", "random time offset".
	public string[] Flags { get; set; } = Array.Empty<string>();
	public string ImportName { get; set; }
	public string ExportName { get; set; }
	public string TurnOffWith { get; set; }
	public float MinimumValue { get; set; }
	// A Halo 2 mapping function, base64.
	public string DefaultFunction { get; set; }
	public string ScaleBy { get; set; }

	public bool HasFlag( string flag ) => Flags?.Contains( flag ) ?? false;
}

// Something an object carries at a marker, scaled by one of its object functions.
public sealed class ObjectAttachmentTag {
	public string Type { get; set; }
	public string Marker { get; set; }
	public string PrimaryScale { get; set; }
}
