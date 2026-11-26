using System;
using System.Text.Json.Serialization;

namespace Halo.Data;

// How an item lies at rest, from its "ground point" marker.
// Halo turns the marker's up axis to the surface normal and puts the marker on the surface.
// GroundPoint and Up are in model space and world units.
// Rotation lays the item flat at yaw 0, as an s&box rotation (i j k w).
public sealed class ItemRestPoseData {
	public string Model { get; set; }
	public float[] GroundPoint { get; set; }
	public float[] Up { get; set; }
	public float[] Rotation { get; set; }
	public float[] Origin { get; set; }

	public Vector3 GroundPointWu => GroundPoint is { Length: 3 } g ? new Vector3( g[0], g[1], g[2] ) : Vector3.Zero;
	public Vector3 UpAxis => Up is { Length: 3 } u ? new Vector3( u[0], u[1], u[2] ).Normal : Vector3.Up;
	public Rotation Flat => Rotation is { Length: 4 } r ? new Rotation( r[0], r[1], r[2], r[3] ) : global::Rotation.Identity;
}

// Halo CE gameplay tags the Blam mount reads from the MCC install (mount://haloce/game.json).
// Field names match the tag fields.
// Values keep Halo's units, which HaloUnits converts.
public sealed class HaloGameData {
	public Dictionary<string, WeaponTag> Weapons { get; set; } = new();
	public Dictionary<string, ProjectileTag> Projectiles { get; set; } = new();
	public Dictionary<string, DamageEffectTag> DamageEffects { get; set; } = new();
	public Dictionary<string, EffectTag> Effects { get; set; } = new();
	public Dictionary<string, EquipmentTag> Equipment { get; set; } = new();
	public Dictionary<string, ItemCollectionTag> ItemCollections { get; set; } = new();
	public Dictionary<string, BipedTag> Bipeds { get; set; } = new();
	public Dictionary<string, CollisionModelTag> CollisionModels { get; set; } = new();
	public Dictionary<string, VehicleTag> Vehicles { get; set; } = new();
	// Vehicles' mass point physics (phys tags).
	public Dictionary<string, PhysicsTag> Physics { get; set; } = new();
	// Vehicles' model skeletons and markers (gbxmodel tags).
	public Dictionary<string, ModelInfoTag> Models { get; set; } = new();
	public Dictionary<string, List<string>> Animations { get; set; } = new();
	// How each weapon, equipment and garbage item lies once dropped.
	// Null for items without a "ground point" marker.
	public Dictionary<string, ItemRestPoseData> ItemRestPoses { get; set; } = new();
	public GlobalsTag Globals { get; set; }

	public ItemRestPoseData RestPose( string itemTag ) => itemTag is not null && ItemRestPoses is not null && ItemRestPoses.TryGetValue( itemTag, out var pose ) ? pose : null;

	// The Spartan's Walk, Run, Shuffle, Jump and Land footsteps, each indexed by HaloMaterials type.
	public Dictionary<string, List<string>> Footsteps { get; set; } = new();

	// The HUD's shield and health sounds and what state they're latched to.
	public List<UnitHudSoundTag> UnitHudSounds { get; set; } = new();

	// Looping sound tags and the sounds of their tracks.
	public Dictionary<string, List<LoopingTrackTag>> LoopingSounds { get; set; } = new();

	// A looping sound tag's first track loop, or the path itself for a plain sound.
	public string LoopSound( string path ) {
		if ( string.IsNullOrEmpty( path ) || !path.EndsWith( ".lsnd" ) ) return path;
		return LoopTrack( path )?.Loop;
	}

	// The track LoopSound loops, or null for a plain sound.
	public LoopingTrackTag LoopTrack( string path ) => LoopTracks( path )?.FirstOrDefault();

	// A looping sound tag's tracks, from its own export if no multiplayer map has it (a mission's music).
	public List<LoopingTrackTag> LoopTracks( string path ) {
		if ( string.IsNullOrEmpty( path ) || !path.EndsWith( ".lsnd" ) ) return null;
		var found = Find( LoopingSounds, path );
		if ( found is not null || !FirstLookup( path ) ) return found;
		var tracks = HaloDataStore.Tag<LoopingSoundTagFile>( path )?.Tracks;
		if ( tracks is not null ) LoopingSounds[path] = tracks;
		return tracks;
	}

	sealed class LoopingSoundTagFile {
		public List<LoopingTrackTag> Tracks { get; set; }
	}

	public WeaponTag Weapon( string path ) => FindOrExport( Weapons, path );
	public ProjectileTag Projectile( string path ) => FindOrExport( Projectiles, path );
	public DamageEffectTag DamageEffect( string path ) => FindOrExport( DamageEffects, path );
	public EffectTag Effect( string path ) => Find( Effects, path );
	public EquipmentTag EquipmentTag( string path ) => Find( Equipment, path );
	public ItemCollectionTag ItemCollection( string path ) => Find( ItemCollections, path );
	public BipedTag Biped( string path ) => Find( Bipeds, path );
	public CollisionModelTag CollisionModel( string path ) => FindOrExport( CollisionModels, path );
	public VehicleTag Vehicle( string path ) => FindOrExport( Vehicles, path );
	public PhysicsTag PhysicsModel( string path ) => FindOrExport( Physics, path );

	public ModelInfoTag ModelInfo( string path ) {
		var found = Find( Models, path );
		if ( found is not null || !FirstLookup( path ) ) return found;
		var model = ModelInfoTag.FromTag( HaloDataStore.Tag<ModelTagFile>( path ) );
		if ( model is not null ) Models[path] = model;
		return model;
	}

	static T Find<T>( Dictionary<string, T> table, string path ) where T : class {
		if ( string.IsNullOrEmpty( path ) || table is null ) return null;
		return table.TryGetValue( path, out var value ) ? value : null;
	}

	// Paths no multiplayer map has, looked for once in the mount's tag exports.
	readonly HashSet<string> _exportLookups = new();

	bool FirstLookup( string path ) => !string.IsNullOrEmpty( path ) && _exportLookups.Add( path );

	// Falls back to the tag's own export for tags no multiplayer map has, such as campaign vehicles.
	T FindOrExport<T>( Dictionary<string, T> table, string path ) where T : class {
		var found = Find( table, path );
		if ( found is not null || table is null || !FirstLookup( path ) ) return found;
		var tag = HaloDataStore.Tag<T>( path );
		if ( tag is not null ) table[path] = tag;
		return tag;
	}
}

public sealed class ProjectileTag {
	public string Model { get; set; }
	public string[] ProjectileFlags { get; set; } = Array.Empty<string>();
	public string DetonationTimerStarts { get; set; }
	public string SuperDetonation { get; set; }
	public float CollisionRadius { get; set; }
	public float ArmingTime { get; set; }
	public string Effect { get; set; }
	public float[] Timer { get; set; }
	public float MinimumVelocity { get; set; }
	public float MaximumRange { get; set; }
	public float AirGravityScale { get; set; }
	public float[] AirDamageRange { get; set; }
	// WU per tick.
	public float InitialVelocity { get; set; }
	// WU per tick.
	public float FinalVelocity { get; set; }
	// Degrees per second.
	public float GuidedAngularVelocity { get; set; }
	public string DetonationStarted { get; set; }
	public string FlybySound { get; set; }
	public string AttachedDetonationDamage { get; set; }
	public string ImpactDamage { get; set; }
	// How loud its collisions and its detonation are to the AI (silent, medium, loud, shout, quiet).
	public string ImpactNoise { get; set; }
	public string DetonationNoise { get; set; }
	public List<ProjectileMaterialResponseTag> ProjectileMaterialResponse { get; set; } = new();

	public bool HasFlag( string flag ) => ProjectileFlags?.Contains( flag ) ?? false;
}

public sealed class ProjectileMaterialResponseTag {
	public string[] Flags { get; set; } = Array.Empty<string>();
	public string DefaultResponse { get; set; }
	public string DefaultEffect { get; set; }
	public string PotentialResponse { get; set; }
	public string[] PotentialFlags { get; set; } = Array.Empty<string>();
	public float PotentialSkipFraction { get; set; }
	public float[] PotentialBetween { get; set; }
	public float[] PotentialAnd { get; set; }
	public string PotentialEffect { get; set; }
	// Degrees of noise added to the impact angle, which also turns a reflection.
	public float AngularNoise { get; set; }
	// World units per tick taken off the impact velocity.
	public float VelocityNoise { get; set; }
	// Plays with the projectile's own effect when it detonates after hitting this material.
	public string DetonationEffect { get; set; }
	public float InitialFriction { get; set; }
	public float MaximumDistance { get; set; }
	public float ParallelFriction { get; set; }
	public float PerpendicularFriction { get; set; }
}

public sealed class DamageEffectTag {
	// Falloff radius and cutoff radius, WU.
	public float[] Radius { get; set; }
	public float CutoffScale { get; set; }
	public string Sound { get; set; }
	public string DamageSideEffect { get; set; }
	public string DamageCategory { get; set; }
	public string[] DamageFlags { get; set; } = Array.Empty<string>();
	public float DamageAoeCoreRadius { get; set; }
	// The "damage minimum": what a damage scale of 0 deals.
	public float DamageLowerBound { get; set; }
	// The random range a damage scale of 1 deals.
	public float[] DamageUpperBound { get; set; }
	public float DamageVehiclePassthroughPenalty { get; set; }
	public float DamageActiveCamouflageDamage { get; set; }
	public float DamageStun { get; set; }
	public float DamageMaximumStun { get; set; }
	public float DamageStunTime { get; set; }
	// WU per second, forward/right/up of the damage direction.
	public float[] DamageInstantaneousAcceleration { get; set; }
	// Rumble: the left (heavy) motor's strength 0-1, seconds and fade function.
	public float LowFrequencyVibrateFrequency { get; set; }
	public float LowFrequencyVibrateDuration { get; set; }
	public string LowFrequencyVibrateFadeFunction { get; set; }
	// The right (light) motor's strength 0-1, seconds and fade function.
	public float HighFrequencyVibrateFrequency { get; set; }
	public float HighFrequencyVibrateDuration { get; set; }
	public string HighFrequencyVibrateFadeFunction { get; set; }
	// Seconds.
	public float TemporaryCameraImpulseDuration { get; set; }
	// A transition function taking the camera impulse from full to nothing over its duration.
	public string TemporaryCameraImpulseFadeFunction { get; set; }
	// Degrees the camera tips away from the blow.
	public float TemporaryCameraImpulseRotation { get; set; }
	// WU the camera is pushed along the blow.
	public float TemporaryCameraImpulsePushback { get; set; }
	// The camera impulse's random sideways offset, lower and upper bound, WU.
	public float[] Jitter { get; set; }
	// Degrees the player's aim itself turns, away from the blow (the frag grenade's 4).
	public float PermanentCameraImpulseAngle { get; set; }
	public float CameraShakingDuration { get; set; }
	// A transition function ("linear", "very late", ...) taking the shake from full to nothing over its duration.
	public string CameraShakingFalloffFunction { get; set; }
	// WU.
	public float CameraShakingRandomTranslation { get; set; }
	// Degrees.
	public float CameraShakingRandomRotation { get; set; }
	// A periodic function ("one", "cosine variable period", ...) modulating the shake.
	public string CameraShakingWobbleFunction { get; set; }
	// Ticks: map building multiplies the tag's seconds by 30, so the sniper's 6 is 0.2 s.
	public float CameraShakingWobblePeriod { get; set; }
	public float CameraShakingWobbleWeight { get; set; }
	// The screen flash: none, lighten, darken, max, min, invert or tint.
	public string Type { get; set; }
	// The screen flash's priority: low, medium or high.
	public string Priority { get; set; }
	// The screen flash's duration, seconds.
	public float Duration { get; set; }
	// The screen flash's fade, a transition function.
	public string FadeFunction { get; set; }
	public float MaximumIntensity { get; set; }
	// The screen flash's colour, a r g b.
	public float[] Color { get; set; }

	// Material modifiers, one per Halo material type.
	public float Dirt { get; set; } = 1;
	public float Sand { get; set; } = 1;
	public float Stone { get; set; } = 1;
	public float Snow { get; set; } = 1;
	public float Wood { get; set; } = 1;
	public float MetalHollow { get; set; } = 1;
	public float MetalThin { get; set; } = 1;
	public float MetalThick { get; set; } = 1;
	public float Rubber { get; set; } = 1;
	public float Glass { get; set; } = 1;
	public float ForceField { get; set; } = 1;
	public float Grunt { get; set; } = 1;
	public float HunterArmor { get; set; } = 1;
	public float HunterSkin { get; set; } = 1;
	public float Elite { get; set; } = 1;
	public float Jackal { get; set; } = 1;
	public float JackalEnergyShield { get; set; } = 1;
	public float EngineerSkin { get; set; } = 1;
	public float EngineerForceField { get; set; } = 1;
	public float FloodCombatForm { get; set; } = 1;
	public float FloodCarrierForm { get; set; } = 1;
	public float CyborgArmor { get; set; } = 1;
	public float CyborgEnergyShield { get; set; } = 1;
	public float HumanArmor { get; set; } = 1;
	public float HumanSkin { get; set; } = 1;
	public float Sentinel { get; set; } = 1;
	public float Monitor { get; set; } = 1;
	public float Plastic { get; set; } = 1;
	public float Water { get; set; } = 1;
	public float Leaves { get; set; } = 1;
	public float EliteEnergyShield { get; set; } = 1;
	public float Ice { get; set; } = 1;
	public float HunterShield { get; set; } = 1;

	public bool HasFlag( string flag ) => DamageFlags?.Contains( flag ) ?? false;
	public float FalloffRadius => Radius is { Length: > 0 } r ? r[0] : 0;
	public float CutoffRadius => Radius is { Length: > 1 } r ? r[1] : FalloffRadius;

	// The modifier for a material type name as used by collision models ("cyborg armor").
	public float MaterialModifier( string materialType ) => materialType switch {
		"dirt" => Dirt,
		"sand" => Sand,
		"stone" => Stone,
		"snow" => Snow,
		"wood" => Wood,
		"metal hollow" => MetalHollow,
		"metal thin" => MetalThin,
		"metal thick" => MetalThick,
		"rubber" => Rubber,
		"glass" => Glass,
		"force field" => ForceField,
		"grunt" => Grunt,
		"hunter armor" => HunterArmor,
		"hunter skin" => HunterSkin,
		"elite" => Elite,
		"jackal" => Jackal,
		"jackal energy shield" => JackalEnergyShield,
		"engineer skin" => EngineerSkin,
		"engineer force field" => EngineerForceField,
		"flood combat form" => FloodCombatForm,
		"flood carrier form" => FloodCarrierForm,
		"cyborg armor" => CyborgArmor,
		"cyborg energy shield" => CyborgEnergyShield,
		"human armor" => HumanArmor,
		"human skin" => HumanSkin,
		"sentinel" => Sentinel,
		"monitor" => Monitor,
		"plastic" => Plastic,
		"water" => Water,
		"leaves" => Leaves,
		"elite energy shield" => EliteEnergyShield,
		"ice" => Ice,
		"hunter shield" => HunterShield,
		_ => 1,
	};
}

public sealed class EffectTag {
	public float MaximumDamageRadius { get; set; }
	public List<string> Locations { get; set; } = new();
	public List<EffectPartTag> Parts { get; set; } = new();
}

public sealed class EffectPartTag {
	public string Tag { get; set; }
	public float[] Delay { get; set; }
	public int? Location { get; set; }
	public float[] VelocityBounds { get; set; }
	public string CreateIn { get; set; }

	public bool IsSound => Tag?.EndsWith( ".snd!" ) ?? false;
	public bool IsDamage => Tag?.EndsWith( ".jpt!" ) ?? false;
}

// An effect tag's full export (mount://haloce/tags/<path>.effe.json).
// It has what EffectTag leaves out: event timing and each part's velocity cone, radius modifier and flags.
// Velocities are world units (per tick at runtime) and angles are degrees.
public sealed class EffectDefinitionTag {
	public List<EffectLocationTag> Locations { get; set; } = new();
	public List<EffectEventTag> Events { get; set; } = new();
}

public sealed class EffectLocationTag {
	public string MarkerName { get; set; }
}

public sealed class EffectEventTag {
	public float SkipFraction { get; set; }
	public float[] DelayBounds { get; set; }
	public float[] DurationBounds { get; set; }
	public List<EffectPartDefinitionTag> Parts { get; set; } = new();
}

public sealed class EffectPartDefinitionTag {
	// "any environment", "air only", "water only" or "space only".
	public string CreateIn { get; set; }

	// "either mode", "violent mode only" or "nonviolent mode only".
	public string ViolenceMode { get; set; }

	// Index into the effect's EffectDefinitionTag.Locations, null for none.
	public int? Location { get; set; }

	public List<string> Flags { get; set; } = new();
	public string Type { get; set; }
	public float[] VelocityBounds { get; set; }
	public float VelocityConeAngle { get; set; }
	public float[] RadiusModifierBounds { get; set; }
	public List<string> AScalesValues { get; set; } = new();
	public List<string> BScalesValues { get; set; } = new();

	public bool IsDecal => Type?.EndsWith( ".deca" ) ?? false;
	public bool HasFlag( string flag ) => Flags?.Contains( flag ) ?? false;
}

public sealed class EquipmentTag {
	// Object scale while not held, which multiplayer draws dropped items at.
	// 0 means 1.
	public float Scale { get; set; }

	public string Model { get; set; }
	public string PowerupType { get; set; }
	public string GrenadeType { get; set; }
	public float PowerupTime { get; set; }
	public string PickupSound { get; set; }
	public float[] DetonationDelay { get; set; }
	public string DetonationEffect { get; set; }
}

public sealed class ItemCollectionTag {
	public List<ItemCollectionPermutation> Permutations { get; set; } = new();
	public int DefaultSpawnTime { get; set; }
}

public sealed class ItemCollectionPermutation {
	public float Weight { get; set; }
	public string Item { get; set; }
}

public sealed class BipedTag {
	// Bounding sphere centre from the origin, WU in object space, where trigger volumes test the object.
	public float[] BoundingOffset { get; set; }
	// Bounding sphere radius in WU, which area damage reaches by.
	public float BoundingRadius { get; set; }
	public string Model { get; set; }
	public string AnimationGraph { get; set; }
	public string CollisionModel { get; set; }
	// The shader drawn over the whole model by its functions (cyborg_mp: the shield hit plasma).
	public string ModifierShader { get; set; }
	public string MeleeDamage { get; set; }
	public float CameraFieldOfView { get; set; } = 70;
	public float GrenadeVelocity { get; set; }
	public float JumpVelocity { get; set; }
	public float MaximumSlopeAngle { get; set; }
	public float DownhillFalloffAngle { get; set; }
	public float DownhillCutoffAngle { get; set; }
	public float DownhillVelocityScale { get; set; }
	public float UphillFalloffAngle { get; set; }
	public float UphillCutoffAngle { get; set; }
	public float UphillVelocityScale { get; set; }
	// The tag's precomputed slope values.
	public float CosineMaximumSlopeAngle { get; set; }
	public float NegativeSineDownhillFalloffAngle { get; set; }
	public float NegativeSineDownhillCutoffAngle { get; set; }
	public float SineUphillFalloffAngle { get; set; }
	public float SineUphillCutoffAngle { get; set; }
	public float StandingCameraHeight { get; set; }
	public float CrouchingCameraHeight { get; set; }
	public float CrouchTransitionTime { get; set; }
	public float StandingCollisionHeight { get; set; }
	public float CrouchingCollisionHeight { get; set; }
	public float CollisionRadius { get; set; }
	public float AutoaimWidth { get; set; }
	// The model nodes aim assist's pill runs between, null for none.
	public int? PelvisModelNodeIndex { get; set; }
	public int? HeadModelNodeIndex { get; set; }
	// The unit's motion sensor blip: small, medium or large.
	public string MotionSensorBlipSize { get; set; }
	public float MinimumSoftLandingVelocity { get; set; }
	public float MinimumHardLandingVelocity { get; set; }
	public float MaximumHardLandingVelocity { get; set; }
	public float MaximumSoftLandingTime { get; set; }
	public float MaximumHardLandingTime { get; set; }
	// The crouch amount per tick.
	public float CrouchCameraVelocity { get; set; }

	// A flying biped's bank at a full turn (degrees) and the seconds it takes to bank and to level out.
	public float BankAngle { get; set; }
	public float BankApplyTime { get; set; }
	public float BankDecayTime { get; set; }
	// How far a flying biped's vertical throttle pitches the facing it turns to.
	public float PitchRatio { get; set; }
	// A flying biped's speeds (WU/s), acceleration and deceleration.
	public float MaxVelocity { get; set; }
	public float MaxSidestepVelocity { get; set; }
	public float Acceleration { get; set; }
	public float Deceleration { get; set; }
	// A flying biped's turning limits, degrees per second and per second squared.
	public float AngularVelocityMaximum { get; set; }
	public float AngularAccelerationMaximum { get; set; }
	public float CrouchVelocityModifier { get; set; }

	// A kill is hard when the body damage is past this (0: never).
	public float HardDeathThreshold { get; set; }
	public string[] UnitFlags { get; set; } = Array.Empty<string>();
	public string[] BipedFlags { get; set; } = Array.Empty<string>();
	// Degrees per second a moving biped turns toward where it wants to face.
	public float MovingTurningSpeed { get; set; }
	// Degrees off its desired facing before a standing biped turns on the spot.
	public float StationaryTurningThreshold { get; set; }

	// The biped's contact points, the markers its footsteps come from (left foot, right foot).
	public List<ContactPointTag> ContactPoint { get; set; } = new();

	// The footsteps material effects tag (globals/hunter.foot), its effects indexed like UnitAnimation's Footstep constants.
	public string Footsteps { get; set; }

	// The unit's dialogue (udlg) by variant number.
	// cyborg_mp has variant 0, the Chief's.
	public List<DialogueVariantTag> DialogueVariants { get; set; } = new();

	// Change colors A-D, which a new object picks its own from.
	public List<ObjectChangeColorTag> ChangeColors { get; set; } = new();

	// The weapons a new unit is made holding, the first in hand (a marine's assault rifle).
	public List<UnitWeaponTag> Weapons { get; set; } = new();
}

public sealed class ObjectChangeColorTag {
	// The function the color follows between its bounds after the object picks it ("a in" .. "d out", or "none").
	public string ScaleBy { get; set; }
	// The function the color is multiplied by after that.
	public string DarkenBy { get; set; }
	// "blend in hsv" and "more colors", which goes round the hue wheel the long way.
	public string[] Flags { get; set; } = Array.Empty<string>();
	public float[] ColorLowerBound { get; set; }
	public float[] ColorUpperBound { get; set; }
	public List<ObjectChangeColorPermutationTag> Permutations { get; set; } = new();
}

// Any object tag's change colors.
public sealed class ObjectChangeColorsTag {
	public List<ObjectChangeColorTag> ChangeColors { get; set; } = new();
}

public sealed class ObjectChangeColorPermutationTag {
	// The permutation's share of the range [0, 1], counted from the first permutation's.
	public float Weight { get; set; }
	public float[] ColorLowerBound { get; set; }
	public float[] ColorUpperBound { get; set; }
}

public sealed class DialogueVariantTag {
	public int VariantNumber { get; set; }
	public string Dialogue { get; set; }
}

public sealed class ContactPointTag {
	public string MarkerName { get; set; }
}

public sealed class CollisionModelTag {
	public string[] Flags { get; set; } = Array.Empty<string>();
	public int? IndirectDamageMaterial { get; set; }
	public float MaximumBodyVitality { get; set; }
	public float BodySystemShock { get; set; }
	public float FriendlyDamageResistance { get; set; }
	// Played where a hit flagged to make a localized effect lands (a unit's continuous melee).
	public string LocalizedDamageEffect { get; set; }
	// Non-flame area damage past this on the body plays AreaDamageEffect at the object's "" marker.
	public float AreaDamageEffectThreshold { get; set; }
	public string AreaDamageEffect { get; set; }
	public float MaximumShieldVitality { get; set; }
	public string ShieldMaterialType { get; set; }
	public string ShieldFailureFunction { get; set; }
	public float ShieldFailureThreshold { get; set; }
	public float FailingShieldLeakFraction { get; set; }
	public float MinimumStunDamage { get; set; }
	public float StunTime { get; set; }
	public float RechargeTime { get; set; }
	// Shield fraction per tick.
	public float ShieldRechargeRate { get; set; }
	public float BodyDamagedThreshold { get; set; }
	public string BodyDamagedEffect { get; set; }
	public string BodyDepletedEffect { get; set; }
	public float BodyDestroyedThreshold { get; set; }
	public string BodyDestroyedEffect { get; set; }
	// Played on the object the first time its shield falls below the threshold.
	public float ShieldDamagedThreshold { get; set; }
	public string ShieldDamagedEffect { get; set; }
	// Played on the object when its shield is knocked down.
	public string ShieldDepletedEffect { get; set; }
	// Played on the object when a depleted shield starts to recharge.
	public string ShieldRechargingEffect { get; set; }
	public List<CollisionMaterialTag> Materials { get; set; } = new();

	// Its regions, matching the model's by index.
	public List<CollisionRegionTag> Regions { get; set; } = new();

	// Its nodes, one per model node by index.
	public List<CollisionNodeTag> Nodes { get; set; } = new();

	public bool HasFlag( string flag ) => Flags?.Contains( flag ) ?? false;
}

public sealed class CollisionRegionTag {
	public string Name { get; set; }
	public string[] Flags { get; set; } = Array.Empty<string>();
	public string DestroyedEffect { get; set; }
	public List<CollisionPermutationTag> Permutations { get; set; } = new();

	public bool HasFlag( string flag ) => Flags?.Contains( flag ) ?? false;
}

public sealed class CollisionPermutationTag {
	public string Name { get; set; }
}

public sealed class CollisionNodeTag {
	public string Name { get; set; }

	// Index into the collision model's regions, null for a node with no collision.
	public int? Region { get; set; }

	// The body part a hit on the node counts as, null for none.
	// Order: gut 0, chest, head, left arm, hand, leg, foot, then right arm, hand, leg, foot.
	public int? RuntimeDamagePart { get; set; }

	// One per permutation of the node's region; the first is the base permutation's.
	public List<CollisionBspTag> Bsps { get; set; } = new();
}

public sealed class CollisionBspTag {
	public List<CollisionPolygonTag> Polygons { get; set; } = new();
}

// A collision surface in node space, in world units.
public sealed class CollisionPolygonTag {
	// Index into the collision model's materials, -1 for none.
	public int Material { get; set; }

	// x, y, z of each corner in order around the polygon.
	public float[] Points { get; set; } = Array.Empty<float>();
}

public sealed class CollisionMaterialTag {
	public string Name { get; set; }
	public string[] Flags { get; set; } = Array.Empty<string>();
	public string MaterialType { get; set; }
	public float ShieldLeakPercentage { get; set; }
	public float ShieldDamageMultiplier { get; set; } = 1;
	public float BodyDamageMultiplier { get; set; } = 1;

	public bool IsHead => Flags?.Contains( "head" ) ?? false;
}

public sealed class GlobalsTag {
	public List<PlayerInformationTag> PlayerInformation { get; set; } = new();
	public List<GrenadeGlobalsTag> Grenades { get; set; } = new();
	public List<MultiplayerInformationTag> MultiplayerInformation { get; set; } = new();
	public List<FallingDamageTag> FallingDamage { get; set; } = new();
	// One per Halo material type, in Halo.HaloSurfaces.Names order.
	public List<GlobalsMaterialTag> Materials { get; set; } = new();
	public List<PlayerControlTag> PlayerControl { get; set; } = new();
	public List<DifficultyTag> Difficulty { get; set; } = new();
	public List<RasterizerDataTag> RasterizerData { get; set; } = new();

	public PlayerInformationTag Player => PlayerInformation?.FirstOrDefault();
	public MultiplayerInformationTag Multiplayer => MultiplayerInformation?.FirstOrDefault();
	public FallingDamageTag Falling => FallingDamage?.FirstOrDefault();

	// The globals' entry for a Halo material type, or null.
	public GlobalsMaterialTag Material( int materialType ) =>
		Materials is not null && materialType >= 0 && materialType < Materials.Count ? Materials[materialType] : null;
}

// The globals' rasterizer data: the bitmaps the cinematic video screen effect draws with.
public sealed class RasterizerDataTag {
	public string VideoScanlineMap { get; set; }
	public string VideoNoiseMap { get; set; }
}

// The globals' difficulty values: a row per value in Halo's order, by easy, normal, hard and impossible.
public sealed class DifficultyTag {
	public List<float[]> Values { get; set; } = new();
}

public sealed class PlayerControlTag {
	// Degrees a vehicle must tip over before a player can flip it and can no longer get in.
	public float MinimumAngleForVehicleFlipping { get; set; }

	// Aim assist's magnetism on an inconsequential unit (infection forms) is scaled by this.
	public float MagnetismInconsequentialTargetScale { get; set; } = 1;
}

public sealed class GlobalsMaterialTag {
	// Scales vehicle mass point physics on this material, 0 keeping the physics tag's value.
	// Ice's friction is 1/8.
	public float GroundFrictionScale { get; set; }
	public float GroundFrictionNormalK1Scale { get; set; }
	public float GroundFrictionNormalK0Scale { get; set; }
	public float GroundDepthScale { get; set; }
	public float GroundDampFractionScale { get; set; }

	// What a player melee that hits this material sounds like.
	public string MeleeHitSound { get; set; }

	// A breakable surface's damage to break (0: damage can't) and its breaking sound.
	public float MaximumVitality { get; set; }
	public string Sound { get; set; }
}

public sealed class PlayerInformationTag {
	// The campaign player's biped.
	public string Unit { get; set; }
	public float WalkingSpeed { get; set; }
	public float DoubleSpeedMultiplier { get; set; }
	public float RunForward { get; set; }
	public float RunBackward { get; set; }
	public float RunSideways { get; set; }
	public float RunAcceleration { get; set; }
	public float SneakForward { get; set; }
	public float SneakBackward { get; set; }
	public float SneakSideways { get; set; }
	public float SneakAcceleration { get; set; }
	public float AirborneAcceleration { get; set; }
	public float[] GrenadeOrigin { get; set; }
	public float StunMovementPenalty { get; set; }
	public float StunTurningPenalty { get; set; }
	public float MinimumStunTime { get; set; }
	public float MaximumStunTime { get; set; }
	// Seconds idle before the first person weapon poses, lower and upper bound.
	public float[] FirstPersonIdleTime { get; set; }
	// The chance an idle timeout passes without a pose.
	public float FirstPersonSkipFraction { get; set; }
	// The effect on a co-op player brought back beside a partner.
	public string CoopRespawnEffect { get; set; }
}

public sealed class GrenadeGlobalsTag {
	public int MaximumCount { get; set; }
	public int MpSpawnDefault { get; set; }
	public string ThrowingEffect { get; set; }
	public string Equipment { get; set; }
	public string Projectile { get; set; }
}

public sealed class MultiplayerInformationTag {
	public string Flag { get; set; }
	public string Unit { get; set; }
	public string Ball { get; set; }
	public string HillShader { get; set; }
	public string FlagShader { get; set; }
	// The multiplayer vehicles, which race spawns.
	// Vehicle sets use the first three: warthog, ghost and tank.
	public List<MultiplayerVehicleTag> Vehicles { get; set; } = new();
	// The announcer and UI sounds, indexed by Halo.Match.AnnouncerSound.
	public List<SoundReferenceTag> Sounds { get; set; } = new();
}

public sealed class MultiplayerVehicleTag {
	public string Vehicle { get; set; }
}

public sealed class SoundReferenceTag {
	public string Sound { get; set; }
}

public sealed class FallingDamageTag {
	public float[] HarmfulFallingDistance { get; set; }
	public string FallingDamage { get; set; }
	public float MaximumFallingDistance { get; set; }
	public string DistanceDamage { get; set; }
	// WU per tick.
	public float MaximumFallingVelocity { get; set; }
	// WU per tick.
	public float[] HarmfulFallingVelocity { get; set; }
	// Dealt to a biped a moving vehicle runs into (the splatter).
	public string VehicleCollisionDamage { get; set; }
	public string VehicleKilledUnitDamage { get; set; }
	public string VehicleEnvironmentCollisionDamage { get; set; }
}

// A vehi tag: its unit part (seats, weapons, camera) and vehicle part (speed and turning).
// Speeds are WU per tick, accelerations WU per tick squared, turns degrees.
public sealed class VehicleTag {
	public string Model { get; set; }
	public string AnimationGraph { get; set; }
	public string CollisionModel { get; set; }
	public string Physics { get; set; }
	public float BoundingRadius { get; set; }
	public float[] BoundingOffset { get; set; }
	// How hard damage pushes it (the object's acceleration scale).
	public float AccelerationScale { get; set; }
	public string[] UnitFlags { get; set; } = Array.Empty<string>();
	// How much of a direct hit on the vehicle its riders take.
	public float RiderDamageFraction { get; set; }
	public float CameraFieldOfView { get; set; } = 70;
	public string MetagameType { get; set; }
	public List<UnitWeaponTag> Weapons { get; set; } = new();
	public List<VehicleSeatTag> Seats { get; set; } = new();
	// How long the driver's seat takes to power up and down.
	public List<PoweredSeatTag> PoweredSeats { get; set; } = new();
	public string[] VehicleFlags { get; set; } = Array.Empty<string>();
	public string VehicleType { get; set; }
	public float MaximumForwardSpeed { get; set; }
	public float MaximumReverseSpeed { get; set; }
	public float SpeedAcceleration { get; set; }
	public float SpeedDeceleration { get; set; }
	public float MaximumLeftTurn { get; set; }
	public float MaximumRightTurn { get; set; }
	public float WheelCircumference { get; set; }
	public float TurnRate { get; set; }
	public float BlurSpeed { get; set; }
	public float MaximumLeftSlide { get; set; }
	public float MaximumRightSlide { get; set; }
	public float SlideAcceleration { get; set; }
	public float SlideDeceleration { get; set; }
	public float MinimumFlippingAngularVelocity { get; set; }
	public float MaximumFlippingAngularVelocity { get; set; }
	public float FixedGunYaw { get; set; }
	// Radians, despite the name: the banshee's 15 degree nose-down pitch.
	public float FixedGunPitch { get; set; }
	public string SuspensionSound { get; set; }
	public string CrashSound { get; set; }
	// The material effects (vehicles/vehicle.foot) its slipping mass points make.
	public string MaterialEffects { get; set; }
	public string Effect { get; set; }
	// The unit's seat acceleration scale (forward, left, up): its acceleration overlays' gain.
	public float[] SeatAccelerationScale { get; set; }
	// How fast the unit's aiming vector turns (degrees per second) and speeds up (degrees per second squared).
	public float AimingVelocityMaximum { get; set; }
	public float AimingAccelerationMaximum { get; set; }

	// The vehicle's own unit HUD, [0] for one local player and [1] for split screen.
	// Its driver sees it with the vehicle's vitality.
	// No multiplayer vehicle has one.
	public List<UnitHudReferenceTag> NewHudInterfaces { get; set; } = new();

	public bool HasFlag( string flag ) => VehicleFlags?.Contains( flag ) ?? false;
	public string Weapon => Weapons?.FirstOrDefault()?.Weapon;
}

public sealed class UnitWeaponTag {
	public string Weapon { get; set; }
}

public sealed class PoweredSeatTag {
	public float DriverPowerupTime { get; set; }
	public float DriverPowerdownTime { get; set; }
}

public sealed class VehicleSeatTag {
	public string[] Flags { get; set; } = Array.Empty<string>();
	// The animation prefix for the rider: "W-driver", "G-driver", "scorpionLF".
	public string Label { get; set; }
	public string MarkerName { get; set; }
	public string CameraMarkerName { get; set; }
	public float YawRate { get; set; }
	public float PitchRate { get; set; }
	public float PitchAutoLevel { get; set; }
	public float[] PitchRange { get; set; }
	// The following camera's tracks: the first ("loose") is the one Halo uses.
	public List<Halo.Vehicles.UnitCameraTrackTag> CameraTracks { get; set; } = new();
	public float YawMinimum { get; set; }
	public float YawMaximum { get; set; }

	// The seat's unit HUD, [0] for one local player and [1] for split screen.
	// The driver's HUD draws it for whoever sits here, e.g. the Warthog's gunner and passenger health panels.
	public List<UnitHudReferenceTag> UnitHudInterface { get; set; } = new();

	public bool HasFlag( string flag ) => Flags?.Contains( flag ) ?? false;
	public bool IsDriver => HasFlag( "driver" );
	public bool IsGunner => HasFlag( "gunner" );
}

// A unit or seat's unit HUD interface reference (unhi tag name).
public sealed class UnitHudReferenceTag {
	public string Hud { get; set; }
}

// A phys tag: Halo's mass point physics, with positions in object space WU.
public sealed class PhysicsTag {
	// Above zero picks Halo's old physics update.
	// Every multiplayer vehicle has -1.
	public float Radius { get; set; } = -1;
	public float MomentScale { get; set; } = 1;
	public float Mass { get; set; }
	public float[] CenterOfMass { get; set; }
	public float GravityScale { get; set; } = 1;
	public float GroundFriction { get; set; }
	public float GroundDepth { get; set; }
	public float GroundDampFraction { get; set; }
	public float GroundNormalK1 { get; set; }
	public float GroundNormalK0 { get; set; }
	public float WaterFriction { get; set; }
	public float WaterDepth { get; set; }
	public float WaterDensity { get; set; }
	public float AirFriction { get; set; }
	public float XxMoment { get; set; }
	public float YyMoment { get; set; }
	public float ZzMoment { get; set; }
	public List<PoweredMassPointTag> PoweredMassPoints { get; set; } = new();
	public List<MassPointTag> MassPoints { get; set; } = new();
}

public sealed class PoweredMassPointTag {
	public string Name { get; set; }
	public string[] Flags { get; set; } = Array.Empty<string>();
	public float AntigravStrength { get; set; }
	public float AntigravOffset { get; set; }
	public float AntigravHeight { get; set; }
	public float AntigravDampFraction { get; set; }
	public float AntigravNormalK1 { get; set; }
	public float AntigravNormalK0 { get; set; }

	public bool IsAntigrav => Flags?.Contains( "antigrav" ) ?? false;
}

public sealed class MassPointTag {
	public string Name { get; set; }
	public int? PoweredMassPoint { get; set; }
	public int? ModelNode { get; set; }
	public string[] Flags { get; set; } = Array.Empty<string>();
	public float RelativeMass { get; set; }
	public float Mass { get; set; }
	public float Density { get; set; }
	public float[] Position { get; set; }
	public float[] Forward { get; set; }
	public float[] Up { get; set; }
	// "point", "forward", "left" or "up".
	public string FrictionType { get; set; }
	public float FrictionParallelScale { get; set; } = 1;
	public float FrictionPerpendicularScale { get; set; } = 1;
	public float Radius { get; set; }
}

// A gbxmodel's skeleton and markers.
// Node translations are WU from the parent node, marker translations WU from their node.
// Rotations are Halo quaternions (i, j, k, w) as stored, the conjugate of s&box's.
public sealed class ModelInfoTag {
	public List<ModelNodeTag> Nodes { get; set; } = new();
	public List<ModelMarkerTag> Markers { get; set; } = new();

	// The skeleton and markers of a gbxmodel's own export, one marker per instance.
	public static ModelInfoTag FromTag( ModelTagFile file ) {
		if ( file is null ) return null;
		return new ModelInfoTag {
			Nodes = file.Nodes.Select( x => new ModelNodeTag { Name = x.Name, Parent = x.ParentNodeIndex, Translation = x.DefaultTranslation, Rotation = x.DefaultRotation } ).ToList(),
			Markers = file.Markers.SelectMany( m => m.Instances.Select( x => new ModelMarkerTag {
				Name = m.Name,
				Node = x.NodeIndex,
				Region = x.RegionIndex,
				Permutation = x.PermutationIndex,
				Translation = x.Translation,
				Rotation = x.Rotation,
			} ) ).ToList(),
		};
	}
}

// A gbxmodel tag's export: its nodes, and its markers with their instances.
public sealed class ModelTagFile {
	public List<NodeFile> Nodes { get; set; } = new();
	public List<MarkerFile> Markers { get; set; } = new();

	public sealed class NodeFile {
		public string Name { get; set; }
		public int? ParentNodeIndex { get; set; }
		public float[] DefaultTranslation { get; set; }
		public float[] DefaultRotation { get; set; }
	}

	public sealed class MarkerFile {
		public string Name { get; set; }
		public List<MarkerInstanceFile> Instances { get; set; } = new();
	}

	public sealed class MarkerInstanceFile {
		public int? RegionIndex { get; set; }
		public int? PermutationIndex { get; set; }
		public int? NodeIndex { get; set; }
		public float[] Translation { get; set; }
		public float[] Rotation { get; set; }
	}
}

public sealed class ModelNodeTag {
	public string Name { get; set; }
	public int? Parent { get; set; }
	public float[] Translation { get; set; }
	public float[] Rotation { get; set; }
}

public sealed class ModelMarkerTag {
	public string Name { get; set; }
	public int? Node { get; set; }
	public int? Region { get; set; }
	public int? Permutation { get; set; }
	public float[] Translation { get; set; }
	public float[] Rotation { get; set; }
}

public sealed class UnitHudSoundTag {
	public string Sound { get; set; }
	public string[] LatchedTo { get; set; } = Array.Empty<string>();
	// The scale Halo plays it at.
	public float Scale { get; set; } = 1;
}

public sealed class LoopingTrackTag {
	public string[] Flags { get; set; } = Array.Empty<string>();
	public float Gain { get; set; } = 1;
	public float FadeInDuration { get; set; }
	public float FadeOutDuration { get; set; }
	public string Start { get; set; }
	public string Loop { get; set; }
	public string End { get; set; }
	public string AlternateLoop { get; set; }
	public string AlternateEnd { get; set; }
}

// Halo's material types, the index into material effect and projectile response blocks.
public static class HaloMaterials {
	public const int Dirt = 0, Sand = 1, Stone = 2, Snow = 3, Wood = 4, MetalHollow = 5, MetalThin = 6, MetalThick = 7, Glass = 9, Water = 28, Leaves = 29;

	// What each multiplayer map is mostly made of, for a trace that hits none of the mount's Halo surfaces.
	public static int GroundFor( string map ) => map switch {
		"bloodgulch" or "timberland" or "deathisland" or "dangercanyon" => Dirt,
		"icefields" or "sidewinder" => Snow,
		"beavercreek" or "hangemhigh" or "gephyrophobia" => Stone,
		"infinity" => Dirt,
		_ => MetalThick,
	};
}

// One multiplayer map's scenario data: where players start, what spawns where.
public sealed class HaloMapData {
	public List<PlayerStartData> PlayerStarts { get; set; } = new();
	public List<NetgameFlagData> NetgameFlags { get; set; } = new();
	public List<NetgameEquipmentData> NetgameEquipment { get; set; } = new();
	public List<StartingEquipmentData> StartingEquipment { get; set; } = new();
	public List<PlacedObjectData> Vehicles { get; set; } = new();
	public List<PlacedObjectData> Weapons { get; set; } = new();
	// The scenario's structure BSPs (multiplayer maps have one).
	public List<StructureBspData> StructureBsps { get; set; } = new();

	// The scenario's local north in radians, which the compass object function measures from.
	// It's 0, pi/2 or pi on the multiplayer maps, and 0 when the mount doesn't export it.
	public float LocalNorth { get; set; }

	// The scenario's skies' atmospheric fogs, by sky index (null where a sky is missing).
	public List<SkyFogData> Skies { get; set; } = new();
}

public sealed class StructureBspData {
	public string Bsp { get; set; }
	// World units: Banshees are pushed back up below this height (0: none).
	public float VehicleFloor { get; set; }
	// World units: Banshees are pushed back down above this height (0: none).
	public float VehicleCeiling { get; set; }
	// Each cluster's sky index, -1 for an indoor cluster.
	public int[] ClusterSkies { get; set; }
}

// A sky's atmospheric fogs.
// The outdoor fog applies to views from clusters with this sky.
// The first sky's indoor fog applies to views from clusters with no sky.
public sealed class SkyFogData {
	public AtmosphericFogData OutdoorFog { get; set; }
	public AtmosphericFogData IndoorFog { get; set; }
}

// Halo's atmospheric fog: none up to the start distance, thickening to maximum density at the opaque distance.
// Distances are world units, and an opaque distance of 0 means no fog.
// The color is gamma space.
public sealed class AtmosphericFogData {
	public float[] Color { get; set; }
	public float MaximumDensity { get; set; }
	public float StartDistance { get; set; }
	public float OpaqueDistance { get; set; }
}

public sealed class PlayerStartData {
	public float[] Position { get; set; }
	public float Facing { get; set; }
	public int? TeamIndex { get; set; }
	public string Type0 { get; set; }
	public string Type1 { get; set; }
	public string Type2 { get; set; }
	public string Type3 { get; set; }

	public IEnumerable<string> Types => new[] { Type0, Type1, Type2, Type3 };
}

public sealed class NetgameFlagData {
	public float[] Position { get; set; }
	public float Facing { get; set; }
	public string Type { get; set; }
	public int UsageId { get; set; }
}

public sealed class NetgameEquipmentData {
	public string[] Flags { get; set; } = Array.Empty<string>();
	public string Type0 { get; set; }
	public string Type1 { get; set; }
	public string Type2 { get; set; }
	public string Type3 { get; set; }
	public int? TeamIndex { get; set; }
	public int SpawnTime { get; set; }
	public float[] Position { get; set; }
	public float Facing { get; set; }
	public string ItemCollection { get; set; }

	public IEnumerable<string> Types => new[] { Type0, Type1, Type2, Type3 };
}

public sealed class StartingEquipmentData {
	public string[] Flags { get; set; } = Array.Empty<string>();
	public string Type0 { get; set; }
	public string Type1 { get; set; }
	public string Type2 { get; set; }
	public string Type3 { get; set; }
	public string ItemCollection1 { get; set; }
	public string ItemCollection2 { get; set; }
	public string ItemCollection3 { get; set; }
	public string ItemCollection4 { get; set; }
	public string ItemCollection5 { get; set; }
	public string ItemCollection6 { get; set; }

	public IEnumerable<string> Types => new[] { Type0, Type1, Type2, Type3 };
	public IEnumerable<string> ItemCollections => new[] { ItemCollection1, ItemCollection2, ItemCollection3, ItemCollection4, ItemCollection5, ItemCollection6 }.Where( x => x is not null );
}

public sealed class PlacedObjectData {
	public string Tag { get; set; }
	public float[] Position { get; set; }
	// Yaw, pitch, roll in degrees.
	public float[] Rotation { get; set; }
	public int MultiplayerTeamIndex { get; set; }
	public string[] MultiplayerSpawnFlags { get; set; } = Array.Empty<string>();
}

// Loads the gameplay data the Blam mount exports from the MCC install, once.
// The resources are mount://haloce/game.json, maps/<map>.json and tags/<tag>.json.
// A failed read isn't retried until the mount's state changes.
public static class HaloDataStore {
	static HaloGameData _game;
	static int _gameFailedState = -1;
	static readonly Dictionary<string, (HaloMapData Data, int State)> _maps = new();

	public static bool IsAvailable => Game is not null;

	public static HaloGameData Game {
		get {
			if ( _game is not null ) return _game;

			var state = HaloMountCheck.MountState;
			if ( state == _gameFailedState ) return null;

			_game = Parse<HaloGameData>( ReadMount( "game.json" ) );
			if ( _game is null ) {
				_gameFailedState = state;
				Log.Warning( $"No Halo gameplay data (mount://{HaloMountCheck.Ident}/game.json): {HaloMountCheck.Title( HaloMountCheck.Status )}" );
				return null;
			}

			Log.Info( $"Halo data: {_game.Weapons.Count} weapons, {_game.Projectiles.Count} projectiles, {_game.DamageEffects.Count} damage effects, {_game.Vehicles.Count} vehicles" );
			return _game;
		}
	}

	public static HaloMapData Map( string mapName ) {
		var state = HaloMountCheck.MountState;
		if ( _maps.TryGetValue( mapName, out var cached ) && (cached.Data is not null || cached.State == state) )
			return cached.Data;

		var data = Parse<HaloMapData>( ReadMount( $"maps/{mapName}.json" ) );
		_maps[mapName] = (data, state);
		return data;
	}

	// One tag from the mount's export, e.g. Tag<WeaponTag>( "weapons/pistol/pistol.weap" ).
	public static T Tag<T>( string tagPath ) where T : class => Parse<T>( ReadMount( $"tags/{tagPath}.json" ) );

	// Every tag of one class the mount exports, campaign-only ones included, e.g. TagPaths( "weap" ).
	public static IEnumerable<string> TagPaths( string tagClass ) {
		var root = $"mount://{HaloMountCheck.Ident}/tags/";
		var suffix = $".{tagClass}.json";
		return (HaloMountCheck.Mount?.Resources ?? []).Select( r => r.Path )
			.Where( p => p.StartsWith( root, StringComparison.Ordinal ) && p.EndsWith( suffix, StringComparison.Ordinal ) )
			.Select( p => p[root.Length..^".json".Length] );
	}

	// A text resource from the mount's data export.
	// Its loader is synchronous, so the task has already finished.
	public static string ReadMount( string path ) {
		try {
			var loader = HaloMountCheck.Mount?.GetByPath( $"mount://{HaloMountCheck.Ident}/{path}" );
			return loader?.GetOrCreate().Result as string;
		}
		catch ( Exception e ) {
			Log.Warning( $"Couldn't read mount://{HaloMountCheck.Ident}/{path}: {e.Message}" );
			return null;
		}
	}

	static T Parse<T>( string json ) where T : class {
		if ( string.IsNullOrEmpty( json ) ) return null;
		try {
			return Json.Deserialize<T>( json );
		}
		catch ( Exception e ) {
			Log.Warning( $"Couldn't read Halo data ({typeof( T ).Name}): {e.Message}" );
			return null;
		}
	}

	// The multiplayer Spartan biped and its collision model (shields, health, materials).
	public static (BipedTag Biped, CollisionModelTag Collision) Spartan {
		get {
			var biped = Game?.Biped( SpartanBipedPath );
			return (biped, Game?.CollisionModel( biped?.CollisionModel ));
		}
	}

	public static string SpartanBipedPath => Game?.Globals?.Multiplayer?.Unit ?? "characters/cyborg_mp/cyborg_mp.bipd";
}
