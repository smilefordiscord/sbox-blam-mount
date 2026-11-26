using System;

namespace Halo.Data;

// The weapon tag fields CombatSim reads.

public sealed class WeaponTag {
	// The object scale multiplayer draws a dropped weapon at; 0 means 1.
	public float Scale { get; set; }

	public string Label { get; set; }
	public string Model { get; set; }
	public string AnimationGraph { get; set; }
	public string[] WeaponFlags { get; set; } = Array.Empty<string>();
	public string SecondaryTriggerMode { get; set; }
	public float ReadyTime { get; set; }
	public string ReadyEffect { get; set; }
	public float HeatRecoveryThreshold { get; set; }
	public float OverheatedThreshold { get; set; }
	public float HeatDetonationThreshold { get; set; }
	public float HeatDetonationFraction { get; set; }
	public float HeatLossRate { get; set; }
	// Heat loss is scaled by 1 - age x this, so a used battery cools slower (plasma rifle 0.2).
	public float AgeHeatRecoveryPenalty { get; set; }
	public string Overheated { get; set; }
	public string PlayerMeleeDamage { get; set; }
	public string PlayerMeleeResponse { get; set; }
	public int ZoomLevels { get; set; }
	public float[] ZoomMagnificationRange { get; set; }
	public float AutoaimAngle { get; set; }
	public float AutoaimRange { get; set; }
	public float MagnetismAngle { get; set; }
	public float MagnetismRange { get; set; }
	public float DeviationAngle { get; set; }
	// The actor variant whose ranged combat values AI wielders fire this weapon with, or null.
	public string ActorFiringParameters { get; set; }
	// AIs don't fire at a target nearer than this, in world units.
	public float MinimumTargetRange { get; set; }
	// Scales an AI wielder's idle looking times.
	public float LookingTimeModifier { get; set; }
	public string MovementPenalized { get; set; }
	public float ForwardMovementPenalty { get; set; }
	public float SidewaysMovementPenalty { get; set; }
	public string FirstPersonModel { get; set; }
	public string FirstPersonAnimations { get; set; }
	public string PickupSound { get; set; }
	// How much firing knocks down active camouflage.
	public float ActiveCamoDing { get; set; } = 0.65f;
	// Active camouflage regrowth per tick.
	public float ActiveCamoRegrowthRate { get; set; } = 0.01f;
	public string ZoomInSound { get; set; }
	public string ZoomOutSound { get; set; }
	public string WeaponType { get; set; }
	public float[] DetonationDelay { get; set; }
	public string DetonationEffect { get; set; }
	// How much slower a used battery fires; the plasma rifle's 0.2 is 20% more ticks between shots at age 1.
	public float AgeRateOfFirePenalty { get; set; }
	public List<MagazineTag> Magazines { get; set; } = new();
	public List<TriggerTag> Triggers { get; set; } = new();

	public bool HasFlag( string flag ) => WeaponFlags?.Contains( flag ) ?? false;
	public MagazineTag Magazine( int index ) => index >= 0 && index < (Magazines?.Count ?? 0) ? Magazines[index] : null;
}

public sealed class MagazineTag {
	public string[] Flags { get; set; } = Array.Empty<string>();
	public int RoundsRecharged { get; set; }
	public int RoundsTotalInitial { get; set; }
	public int RoundsReservedMaximum { get; set; }
	public int RoundsLoadedMaximum { get; set; }
	public float ReloadTime { get; set; }
	public int RoundsReloaded { get; set; }
	public float ChamberTime { get; set; }
	public string ReloadingEffect { get; set; }
	public string ChamberingEffect { get; set; }
	public List<MagazineObjectTag> MagazineObjects { get; set; } = new();
}

// An ammo pickup this magazine takes rounds from.
public sealed class MagazineObjectTag {
	public int Rounds { get; set; }
	public string Equipment { get; set; }
}

public sealed class TriggerTag {
	public string[] Flags { get; set; } = Array.Empty<string>();
	// How loud its shots are to the AI: silent, medium, loud, shout or quiet.
	public string FiringNoise { get; set; }
	public float[] MaximumRateOfFire { get; set; }
	public float AccelerationTime { get; set; }
	// The rate of fire fraction above which the "~primary-blur" or "~secondary-blur" permutation shows.
	public float BlurredRateOfFire { get; set; }
	public float DecelerationTime { get; set; }
	public int? Magazine { get; set; }
	public int RoundsPerShot { get; set; }
	public int MinimumRoundsLoaded { get; set; }
	public float ErrorAccelerationTime { get; set; }
	public float ErrorDecelerationTime { get; set; }
	public float ChargingTime { get; set; }
	public float ChargedTime { get; set; }
	public string OverchargedAction { get; set; }
	public float SpewTime { get; set; }
	public string ChargingEffect { get; set; }
	public string DistributionFunction { get; set; }
	public int ProjectilesPerShot { get; set; }
	public float DistributionAngle { get; set; }
	public float MinimumError { get; set; }
	public float[] ErrorAngle { get; set; }
	public float[] FirstPersonOffset { get; set; }
	public string Projectile { get; set; }
	public float HeatGeneratedPerRound { get; set; }
	public float AgeGeneratedPerRound { get; set; }
	// Shots without a contrail between tracers; 3 for the assault rifle and plasma rifle.
	public int ProjectilesBetweenContrails { get; set; }
	public List<FiringEffectTag> FiringEffects { get; set; } = new();

	public bool HasFlag( string flag ) => Flags?.Contains( flag ) ?? false;
	public float InitialRateOfFire => MaximumRateOfFire is { Length: > 0 } r ? r[0] : 0;
	public float FinalRateOfFire => MaximumRateOfFire is { Length: > 1 } r ? r[1] : InitialRateOfFire;

	// Per tick ramp rates: 1/30 over each time, or 1 for no time, bit for bit what MCC's maps hold.
	public float FiringAccelerationRate => PerTick( AccelerationTime );
	public float FiringDecelerationRate => PerTick( DecelerationTime );
	public float ErrorAccelerationRate => PerTick( ErrorAccelerationTime );
	public float ErrorDecelerationRate => PerTick( ErrorDecelerationTime );

	static float PerTick( float seconds ) => seconds <= 0 ? 1 : (1f / 30f) / seconds;

	// Ticks a shot needs: 30 over the ramped rate of fire, plus the age penalty.
	// The trigger fires once its idle ticks + 1 reach it.
	// The float operations follow MCC's order so ties match, like the plasma rifle's 7.5 per second being exactly 4 ticks.
	public float RequiredTicks( float fraction, float age, float ageRateOfFirePenalty ) {
		var rate = (FinalRateOfFire - InitialRateOfFire) * fraction + InitialRateOfFire;
		var required = rate > 0.0001f ? 30f / rate : 0f;
		if ( ageRateOfFirePenalty > 0 )
			required += age * required * ageRateOfFirePenalty;
		return required;
	}

	// The fewest ticks between shots at this age, at whichever end of the ramp is faster.
	public int FastestShotTicks( float age, float ageRateOfFirePenalty ) {
		var required = MathF.Min( RequiredTicks( 0, age, ageRateOfFirePenalty ), RequiredTicks( 1, age, ageRateOfFirePenalty ) );
		return Math.Max( 1, (int)MathF.Ceiling( required ) );
	}
}

public sealed class FiringEffectTag {
	public int ShotCountLowerBound { get; set; }
	public int ShotCountUpperBound { get; set; }
	public string FiringEffect { get; set; }
	public string MisfireEffect { get; set; }
	public string EmptyEffect { get; set; }
	// Applied to the firer for camera shake and rumble only, no damage.
	public string FiringDamage { get; set; }
	public string MisfireDamage { get; set; }
}
