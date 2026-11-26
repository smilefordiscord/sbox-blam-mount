using Halo2.Combat;
using System;

namespace Halo2.Data;

// Halo 2 gameplay tags the Blam mount reads from the MCC install (mount://halo2/game.json).
// Field names match the mount's tag export, values keep Halo 2's units: world units, seconds, degrees.
public sealed class HaloGameData {
	public Dictionary<string, WeaponTag> Weapons { get; set; } = new();
	public Dictionary<string, ProjectileTag> Projectiles { get; set; } = new();
	public Dictionary<string, DamageEffectTag> DamageEffects { get; set; } = new();
	public Dictionary<string, EquipmentTag> Equipment { get; set; } = new();
	public Dictionary<string, ItemCollectionTag> ItemCollections { get; set; } = new();
	public Dictionary<string, BipedTag> Bipeds { get; set; } = new();
	public Dictionary<string, ModelTag> Models { get; set; } = new();
	public Dictionary<string, CollisionModelTag> CollisionModels { get; set; } = new();
	public Dictionary<string, MaterialEffectsTag> MaterialEffects { get; set; } = new();
	public Dictionary<string, EffectTag> Effects { get; set; } = new();

	// The scenario objects Halo 2 simulates as Havok bodies, by crate tag.
	public Dictionary<string, CrateTag> Crates { get; set; } = new();
	public GlobalsTag Globals { get; set; }
	public MultiplayerGlobalsTag MultiplayerGlobals { get; set; }

	// The Spartan's footstep sounds by global material name.
	public FootstepsData Footsteps { get; set; }

	// The player representations' first person bodies by render model tag.
	public Dictionary<string, FirstPersonBodyTag> FirstPersonBodies { get; set; } = new();

	// Not exported by the Halo 2 mount yet: the HUD's shield sounds.
	public List<UnitHudSoundTag> UnitHudSounds { get; set; } = new();

	public WeaponTag Weapon( string path ) => Find( Weapons, path );
	public ProjectileTag Projectile( string path ) => Find( Projectiles, path );
	public DamageEffectTag DamageEffect( string path ) => Find( DamageEffects, path );
	public EquipmentTag EquipmentTag( string path ) => Find( Equipment, path );
	public ItemCollectionTag ItemCollection( string path ) => Find( ItemCollections, path );
	public BipedTag Biped( string path ) => Find( Bipeds, path );
	public ModelTag Model( string path ) => Find( Models, path );
	public CollisionModelTag CollisionModel( string path ) => Find( CollisionModels, path );
	public EffectTag Effect( string path ) => Find( Effects, path );
	public CrateTag Crate( string path ) => Find( Crates, path );

	public MaterialEffectsEntry MaterialEffectsEntry( string path, int kind ) => Find( MaterialEffects, path )?.Effects?.ElementAtOrDefault( kind );

	// A material effects tag's effect and sound of one kind for a global material, or its nearest parent that has them.
	public (MaterialEffectPart Effect, MaterialEffectPart Sound) MaterialEffect( string path, int kind, int globalMaterial ) {
		var entry = MaterialEffectsEntry( path, kind );
		if ( entry is null ) return default;

		MaterialEffectPart effect = null, sound = null;
		for ( var material = Globals?.Material( globalMaterial ); material is not null && (effect is null || sound is null); material = Globals.Material( Globals.MaterialIndex( material.ParentName ) ) ) {
			effect ??= entry.Effects?.FirstOrDefault( x => x.MaterialName == material.Name && x.Plays );
			sound ??= entry.Sounds?.FirstOrDefault( x => x.MaterialName == material.Name && x.Plays );
		}
		return (effect, sound);
	}

	static T Find<T>( Dictionary<string, T> table, string path ) where T : class {
		if ( string.IsNullOrEmpty( path ) || table is null ) return null;
		return table.TryGetValue( path, out var value ) ? value : null;
	}

	// The multiplayer Spartan: the globals' third person unit for the multiplayer Spartan representation.
	public string SpartanBipedPath => Globals?.PlayerRepresentation?.Select( x => x.ThirdPersonUnit ).FirstOrDefault( x => x?.EndsWith( "_mp.bipd" ) ?? false );

	public BipedTag SpartanBiped => Biped( SpartanBipedPath );

	// The first person body of the Spartan's representation.
	public string SpartanFirstPersonBodyPath => Globals?.PlayerRepresentation?.FirstOrDefault( x => x.ThirdPersonUnit == SpartanBipedPath )?.FirstPersonBody;

	public FirstPersonBodyTag FirstPersonBody( string path ) => Find( FirstPersonBodies, path );

	// The Spartan's damage info: vitality, shields, recharge and its materials.
	public ModelTag SpartanModel => Model( SpartanBiped?.Model );
}

// A material effects tag (foot): for each kind of contact, an effect and a sound per global material.
// Kind 10 is an object's impact and kind 15 a projectile's, as the weapons' and projectiles' tags fill them.
// A biped's kinds 0 to 5 are its footsteps and 7 its body falling.
public sealed class MaterialEffectsTag {
	public const int Walk = 0, Run = 1, Shuffle = 3, Jump = 4, Land = 5;
	public const int BodyFall = 7;
	public const int ObjectImpact = 10;
	public const int ProjectileImpact = 15;

	public List<MaterialEffectsEntry> Effects { get; set; } = new();
}

public sealed class MaterialEffectsEntry {
	public List<MaterialEffectPart> Sounds { get; set; } = new();
	public List<MaterialEffectPart> Effects { get; set; } = new();

	public bool IsEmpty => (Sounds?.Count ?? 0) == 0 && (Effects?.Count ?? 0) == 0;
}

// A material's effect or sound: the tag and a secondary one, which Halo 2 plays together.
// With the sweetener on, the hit material's small sweetener plays with them.
public sealed class MaterialEffectPart {
	public string Tag { get; set; }
	public string SecondaryTag { get; set; }
	public string MaterialName { get; set; }
	public string SweetenerMode { get; set; }

	public bool Plays => Tag is not null || SecondaryTag is not null || Sweetened;
	public bool Sweetened => SweetenerMode == "sweetener enabled";
}

// An effect tag's parts by location; only the light and sound parts are read.
public sealed class EffectTag {
	public List<string> Locations { get; set; } = new();
	public List<EffectPartTag> Parts { get; set; } = new();
}

public sealed class EffectPartTag {
	public string Tag { get; set; }
	public int Location { get; set; }
}

public sealed class FootstepsData {
	public string Tag { get; set; }
	public List<FootstepEffectsData> Effects { get; set; } = new();
}

public sealed class FootstepEffectsData {
	// Sound tags by global material name, each with its remastered pair.
	public Dictionary<string, List<string>> Sounds { get; set; } = new();
}

public sealed class ProjectileTag {
	public string Model { get; set; }
	// Its impacts' effects and sounds by material.
	public string MaterialEffects { get; set; }
	public string[] ProjectileFlags { get; set; } = Array.Empty<string>();
	// "immediately", "after first bounce off floor", "when at rest" or "after first bounce off any surface".
	public string DetonationTimerStarts { get; set; }
	public float ArmingTime { get; set; }
	// Projected 0.75 s ahead, the spawn system keeps players away from armed grenades within this.
	public float DangerRadius { get; set; }
	public float[] Timer { get; set; }
	// World units per second.
	public float MinimumVelocity { get; set; }
	public float MaximumRange { get; set; }
	// Over a Needler's count of these stuck to a biped, the next one super combines.
	public int SuperDetonationProjectileCount { get; set; }
	public string DetonationEffectAirborne { get; set; }
	public string DetonationEffectGround { get; set; }
	public string DetonationDamage { get; set; }
	public string AttachedDetonationDamage { get; set; }
	public string SuperDetonation { get; set; }
	public string SuperDetonationDamage { get; set; }
	public string SuperAttachedDetonationDamage { get; set; }
	// Below the detonation, the ground within this picks the ground effect over the airborne one.
	public float MaterialEffectRadius { get; set; }
	public string FlybySound { get; set; }
	// Played at every detonation, along with the detonation effect.
	public string DetonationSound { get; set; }
	public string ImpactDamage { get; set; }
	public float AirGravityScale { get; set; }
	// Only the upper bound is used: the distance for "damage scales based on distance".
	public float[] AirDamageRange { get; set; }
	// World units per second.
	public float InitialVelocity { get; set; }
	public float FinalVelocity { get; set; }
	// Degrees per second, picked at random between the two at launch.
	public float GuidedAngularVelocityLower { get; set; }
	public float GuidedAngularVelocityUpper { get; set; }
	// World units flown over which the speed goes from initial to final.
	public float[] AccelerationRange { get; set; }
	public float TargetedLeadingFraction { get; set; }
	public List<ProjectileMaterialResponseTag> MaterialResponses { get; set; } = new();
	public List<ObjectFunctionTag> Functions { get; set; } = new();
	public List<ObjectAttachmentTag> Attachments { get; set; } = new();

	public bool HasFlag( string flag ) => ProjectileFlags?.Contains( flag ) ?? false;
	public float DamageRange => AirDamageRange is { Length: > 1 } r ? r[1] : 0;
	public float TimerMaximum => Timer is { Length: > 1 } t ? t[1] : 0;
}

public sealed class ProjectileMaterialResponseTag {
	public string[] Flags { get; set; } = Array.Empty<string>();
	public string DefaultResponse { get; set; }
	// The global material this answers for, and through it every material descended from it.
	public string MaterialName { get; set; }
	public string PotentialResponse { get; set; }
	public string[] ResponseFlags { get; set; } = Array.Empty<string>();
	public float ChanceFraction { get; set; }
	// Degrees from the surface, 0 grazing to 90 head-on.
	public float[] Between { get; set; }
	// World units per second.
	public float[] And { get; set; }
	// Degrees.
	public float AngularNoise { get; set; }
	// World units per second.
	public float VelocityNoise { get; set; }
	// The speed an overpenetration loses, as a fraction.
	public float InitialFriction { get; set; }
	public float ParallelFriction { get; set; }
	public float PerpendicularFriction { get; set; }
}

public sealed class DamageEffectTag {
	// Falloff radius and cutoff radius, world units.
	public float[] Radius { get; set; }
	public string[] Flags { get; set; } = Array.Empty<string>();
	public string SideEffect { get; set; }
	public string[] DamageFlags { get; set; } = Array.Empty<string>();
	// What a damage scale of 0 deals.
	public float DamageLowerBound { get; set; }
	// The random range a damage scale of 1 deals.
	public float[] DamageUpperBound { get; set; }
	public float ActiveCamouflageDamage { get; set; }
	// World units per second along the damage.
	public float InstantaneousAcceleration { get; set; }
	// The damage table's group for this damage, and a more specific one.
	public string GeneralDamage { get; set; }
	public string SpecificDamage { get; set; }
	public List<PlayerResponseTag> PlayerResponses { get; set; } = new();
	// Seconds.
	public float TemporaryCameraImpulseDuration { get; set; }
	// A transition function taking the camera impulse from full to nothing over its duration.
	public string TemporaryCameraImpulseFadeFunction { get; set; }
	// Degrees the camera tips away from the blow.
	public float TemporaryCameraImpulseRotation { get; set; }
	// World units the camera is pushed along the blow.
	public float TemporaryCameraImpulsePushback { get; set; }
	// The camera impulse's random sideways offset, lower and upper bound, world units.
	public float[] TemporaryCameraImpulseJitter { get; set; }
	public float CameraShakingDuration { get; set; }
	// A transition function ("linear", "early", ...) taking the shake from full to nothing over its duration.
	public string CameraShakingFalloffFunction { get; set; }
	// World units.
	public float CameraShakingRandomTranslation { get; set; }
	// Degrees.
	public float CameraShakingRandomRotation { get; set; }
	// A periodic function ("one", "jitter", ...) modulating the shake.
	public string CameraShakingWobbleFunction { get; set; }
	public float CameraShakingWobbleFunctionPeriod { get; set; }
	public float CameraShakingWobbleWeight { get; set; }
	public string Sound { get; set; }

	public bool HasFlag( string flag ) => DamageFlags?.Contains( flag ) ?? false;
	public bool HasEffectFlag( string flag ) => Flags?.Contains( flag ) ?? false;
	public float FalloffRadius => Radius is { Length: > 0 } r ? r[0] : 0;
	public float CutoffRadius => Radius is { Length: > 1 } r ? r[1] : FalloffRadius;

	// The response for a player whose shield is up ("shielded") or down ("unshielded"), else the one for both.
	public PlayerResponseTag Response( bool shielded ) =>
		PlayerResponses?.FirstOrDefault( x => x.ResponseType == (shielded ? "shielded" : "unshielded") ) ?? PlayerResponses?.FirstOrDefault();
}

// What a damage effect does to the player it hits: a screen flash, rumble and sound.
public sealed class PlayerResponseTag {
	public string ResponseType { get; set; }
	// None, lighten, darken, max, min, invert or tint.
	public string ScreenFlashType { get; set; }
	// Low, medium or high.
	public string ScreenFlashPriority { get; set; }
	public float ScreenFlashDuration { get; set; }
	public string ScreenFlashFadeFunction { get; set; }
	public float ScreenFlashMaximumIntensity { get; set; }
	// A r g b.
	public float[] ScreenFlashColor { get; set; }
	public float LowFrequencyVibrationDuration { get; set; }
	public float HighFrequencyVibrationDuration { get; set; }
}

// An effect tag's export (mount://halo2/tags/<path>.effe.json): its events' timing in seconds.
public sealed class EffectDefinitionTag {
	public List<EffectEventTag> Events { get; set; } = new();
}

public sealed class EffectEventTag {
	public float SkipFraction { get; set; }
	public float[] DelayBounds { get; set; }
	public float[] DurationBounds { get; set; }
}

public sealed class EquipmentTag {
	// The scale multiplayer draws the item at on the ground; 0 means 1.
	public float MultiplayerOnGroundScale { get; set; }

	public string Model { get; set; }
	// "grenade", "overshield", "active camouflage", "health" or "none".
	public string PowerupType { get; set; }
	public string GrenadeType { get; set; }
	// Seconds.
	public float PowerupTime { get; set; }
	public string PickupSound { get; set; }
}

public sealed class ItemCollectionTag {
	public List<ItemCollectionPermutation> ItemPermutations { get; set; } = new();
	// Seconds; 0 leaves it to the netgame equipment entry.
	public int SpawnTime { get; set; }
}

public sealed class ItemCollectionPermutation {
	public float Weight { get; set; }
	public string Item { get; set; }
}

public sealed class BipedTag {
	public string Model { get; set; }
	// The markers the hands' weapons attach at by their origin, and the one Halo prefers for the right hand's.
	public string RightHandNode { get; set; }
	public string LeftHandNode { get; set; }
	public string PreferredGunNode { get; set; }
	public string[] UnitFlags { get; set; } = Array.Empty<string>();
	// "use player physics" for a player's biped.
	public string[] PhysicsFlags { get; set; } = Array.Empty<string>();
	public string MeleeDamage { get; set; }
	// World units per second.
	public float GrenadeVelocity { get; set; }
	// World units per second.
	public float JumpVelocity { get; set; }
	public float MaximumSoftLandingTime { get; set; }
	public float MaximumHardLandingTime { get; set; }
	// World units per second.
	public float MinimumSoftLandingVelocity { get; set; }
	public float MinimumHardLandingVelocity { get; set; }
	public float MaximumHardLandingVelocity { get; set; }
	public float StandingCameraHeight { get; set; }
	public float CrouchingCameraHeight { get; set; }
	public float CrouchTransitionTime { get; set; }
	// The first person camera's move toward the body's head: degrees down it starts and ends, its scales and exclusion (wu).
	public float CameraInterpolationStart { get; set; }
	public float CameraInterpolationEnd { get; set; }
	public float CameraForwardMovementScale { get; set; }
	public float CameraSideMovementScale { get; set; }
	public float CameraVerticalMovementScale { get; set; }
	public float CameraExclusionDistance { get; set; }
	// Degrees per second, and the degrees the view gets ahead of a standing body before it turns.
	public float MovingTurningSpeed { get; set; }
	public float StationaryTurningThreshold { get; set; }
	public float AutoaimWidth { get; set; }
	// The Havok capsule's mass.
	public float Mass { get; set; }
	// The physics pill, world units.
	public float HeightStanding { get; set; }
	public float HeightCrouching { get; set; }
	public float Radius { get; set; }
	// Degrees.
	public float MaximumSlopeAngle { get; set; }
	public float DownhillFalloffAngle { get; set; }
	public float DownhillCutoffAngle { get; set; }
	public float UphillFalloffAngle { get; set; }
	public float UphillCutoffAngle { get; set; }
	public float DownhillVelocityScale { get; set; }
	public float UphillVelocityScale { get; set; }
	public string AreaDamageEffect { get; set; }
	// Scales the knockback damage gives it.
	public float AccelerationScale { get; set; }
	// The spheres a dead biped rests on before it ragdolls, smallest first.
	public List<DeadSphereTag> DeadSphereShapes { get; set; } = new();
	// The material effects its footsteps and body falls play.
	public string MaterialEffects { get; set; }
	// The markers its footsteps play at: the left foot, then the right.
	public List<ContactPointTag> ContactPoints { get; set; } = new();
	// small, medium or large: which of the material effects' object impacts its Havok impacts play.
	public string SweetenerSize { get; set; }
	// Scales the kick a headshot kill gives its ragdoll.
	public float HeadshotAccelerationScale { get; set; }
	// Its model's ragdoll, which a dead biped becomes once it settles.
	public CratePhysicsTag Physics { get; set; }

	public bool HasUnitFlag( string flag ) => UnitFlags?.Contains( flag ) ?? false;
}

public sealed class ContactPointTag {
	public string MarkerName { get; set; }
}

// One dead sphere: its radius and centre above the biped's origin, world units.
public sealed class DeadSphereTag {
	public float Radius { get; set; }
	public float[] Translation { get; set; }
}

// A model tag (hlmt): its parts, its materials and its damage info.
public sealed class ModelTag {
	public string Animation { get; set; }
	public string CollisionModel { get; set; }
	public List<ModelMaterialTag> Materials { get; set; } = new();
	public List<DamageInfoTag> NewDamageInfo { get; set; } = new();

	public DamageInfoTag DamageInfo => NewDamageInfo?.FirstOrDefault();
}

// A collision model (coll): each region's permutations hold a BSP per node, which is what shots hit.
public sealed class CollisionModelTag {
	public List<CollisionRegionTag> Regions { get; set; } = new();
	public List<CollisionNodeTag> Nodes { get; set; } = new();
}

public sealed class CollisionRegionTag {
	public string Name { get; set; }
	public List<CollisionPermutationTag> Permutations { get; set; } = new();
}

public sealed class CollisionPermutationTag {
	public string Name { get; set; }
	public List<CollisionBspTag> Bsps { get; set; } = new();
}

// A BSP in the space of the node it moves with, in world units, as its winged edge surfaces.
public sealed class CollisionBspTag {
	public int? Node { get; set; }
	public List<CollisionSurfaceTag> Surfaces { get; set; } = new();
	public List<CollisionEdgeTag> Edges { get; set; } = new();
	public List<CollisionVertexTag> Vertices { get; set; } = new();
}

// Material indexes the model's materials.
public sealed class CollisionSurfaceTag {
	public int FirstEdge { get; set; }
	public int? Material { get; set; }
}

public sealed class CollisionEdgeTag {
	public int StartVertex { get; set; }
	public int EndVertex { get; set; }
	public int ForwardEdge { get; set; }
	public int ReverseEdge { get; set; }
	public int LeftSurface { get; set; }
	public int RightSurface { get; set; }
}

public sealed class CollisionVertexTag {
	public float[] Point { get; set; }
}

public sealed class CollisionNodeTag {
	public string Name { get; set; }
}

// One of the model's materials: the collision material by name, its damage section and its global material.
public sealed class ModelMaterialTag {
	public string MaterialName { get; set; }
	public int? DamageSection { get; set; }
	public string GlobalMaterialName { get; set; }
}

public sealed class DamageInfoTag {
	public string[] Flags { get; set; } = Array.Empty<string>();
	// The global material and section area damage hits.
	public string GlobalIndirectMaterialName { get; set; }
	public int? IndirectDamageSection { get; set; }
	public float MaximumVitality { get; set; }
	public float MinimumStunDamage { get; set; }
	// Seconds the body waits after damage before it recharges.
	public float StunTime { get; set; }
	// How far the body recharges, as a fraction.
	public float RechargeFraction { get; set; }
	public float MaximumShieldVitality { get; set; }
	public string GlobalShieldMaterialName { get; set; }
	public float ShieldMinimumStunDamage { get; set; }
	// Seconds the shield waits after damage before it recharges.
	public float ShieldStunTime { get; set; }
	public List<DamageSectionTag> DamageSections { get; set; } = new();
	// Fraction per second: one over the recharge time.
	public float ShieldRechargeVelocity { get; set; }
	public float HealthRechargeVelocity { get; set; }

	public bool HasFlag( string flag ) => Flags?.Contains( flag ) ?? false;

	public DamageSectionTag Section( int? index ) => index is int i && i >= 0 && i < (DamageSections?.Count ?? 0) ? DamageSections[i] : null;
}

public sealed class DamageSectionTag {
	public string Name { get; set; }
	public string[] Flags { get; set; } = Array.Empty<string>();
	// Its share of the body's maximum vitality.
	public float VitalityPercentage { get; set; }
	public List<InstantResponseTag> InstantResponses { get; set; } = new();
	public float StunTime { get; set; }

	public bool HasFlag( string flag ) => Flags?.Contains( flag ) ?? false;
}

// What a damage section does once its damage reaches the threshold, a fraction of its vitality left (0 when used up).
// Effects play at their markers; the delay holds the rest back that many seconds after the delay effect.
public sealed class InstantResponseTag {
	public string ResponseType { get; set; }
	public string[] Flags { get; set; } = Array.Empty<string>();
	public float DamageThreshold { get; set; }
	public string TransitionEffect { get; set; }
	public string TransitionDamageEffect { get; set; }
	public string EffectMarkerName { get; set; }
	public string DamageEffectMarkerName { get; set; }
	public float ResponseDelay { get; set; }
	public string DelayEffect { get; set; }
	public string DelayEffectMarkerName { get; set; }
	public float SkipFraction { get; set; }

	public bool HasFlag( string flag ) => Flags?.Contains( flag ) ?? false;
}

public sealed class GlobalsTag {
	public List<DamageTableTag> DamageTable { get; set; } = new();
	public List<PlayerControlTag> PlayerControl { get; set; } = new();
	public List<GrenadeGlobalsTag> Grenades { get; set; } = new();
	public List<PlayerInformationTag> PlayerInformation { get; set; } = new();
	public List<PlayerRepresentationTag> PlayerRepresentation { get; set; } = new();
	public List<FallingDamageTag> FallingDamage { get; set; } = new();
	// The global materials, which material responses and effects are found by.
	public List<GlobalsMaterialTag> Materials { get; set; } = new();
	// The colors a player's profile picks its armor from.
	public List<ColorEntryTag> ProfileColors { get; set; } = new();

	public PlayerInformationTag Player => PlayerInformation?.FirstOrDefault();
	public FallingDamageTag Falling => FallingDamage?.FirstOrDefault();

	// The globals' material at an index, or null.
	public GlobalsMaterialTag Material( int index ) =>
		Materials is not null && index >= 0 && index < Materials.Count ? Materials[index] : null;

	// A material's index by name, or -1.
	public int MaterialIndex( string name ) => string.IsNullOrEmpty( name ) || Materials is null ? -1 : Materials.FindIndex( x => x.Name == name );
}

// Multipliers by damage group and armor: a damage effect's groups times a material's armors.
public sealed class DamageTableTag {
	public List<DamageGroupTag> DamageGroups { get; set; } = new();
}

public sealed class DamageGroupTag {
	public string Name { get; set; }
	public List<ArmorModifierTag> ArmorModifiers { get; set; } = new();
}

public sealed class ArmorModifierTag {
	public string Name { get; set; }
	public float DamageMultiplier { get; set; }
}

public sealed class ColorEntryTag {
	public float[] Color { get; set; }

	public Color Value => Color is { Length: >= 3 } c ? new Color( c[0], c[1], c[2] ) : global::Color.White;
}

public sealed class PlayerControlTag {
	// How much aim assist's magnetism slows a controller's look over a target, and how much it follows it.
	public float MagnetismFriction { get; set; }
	public float MagnetismAdhesion { get; set; }
}

public sealed class PlayerRepresentationTag {
	public string ThirdPersonUnit { get; set; }
	// The render model of the legs and body the player sees of themselves.
	public string FirstPersonBody { get; set; }
}

public sealed class FirstPersonBodyTag {
	// Halo draws the body only while the camera's forward z is below this; 0 draws it always.
	public float DontDrawOverCameraCosineAngle { get; set; }
}

public sealed class GlobalsMaterialTag {
	// The material's name, which the mount names its surfaces after.
	public string Name { get; set; }
	public string ParentName { get; set; }
	// The armors the damage table knows the material by.
	public string GeneralArmor { get; set; }
	public string SpecificArmor { get; set; }
	// What a melee that hits this material sounds like.
	public string SoundSweetenerMelee { get; set; }
	// Played with a projectile's impact sound when its material effect has the sweetener on.
	public string SoundSweetenerSmall { get; set; }
}

public sealed class PlayerInformationTag {
	// World units per second.
	public float RunForward { get; set; }
	public float RunBackward { get; set; }
	public float RunSideways { get; set; }
	// World units per second squared.
	public float RunAcceleration { get; set; }
	public float SneakForward { get; set; }
	public float SneakBackward { get; set; }
	public float SneakSideways { get; set; }
	public float SneakAcceleration { get; set; }
	public float AirborneAcceleration { get; set; }
	// Forward, left and up of the camera, world units.
	public float[] GrenadeOrigin { get; set; }
	// Seconds idle before the first person weapon poses, lower and upper bound.
	public float[] FirstPersonIdleTime { get; set; }
	// The chance an idle timeout passes without a pose.
	public float FirstPersonSkipFraction { get; set; }
	// The helmet zoom a weapon without its own zoom uses: its levels, magnification bounds and sounds.
	public int BinocularsZoomCount { get; set; }
	public float[] BinocularsZoomRange { get; set; }
	public string BinocularsZoomInSound { get; set; }
	public string BinocularsZoomOutSound { get; set; }
}

public sealed class GrenadeGlobalsTag {
	public int MaximumCount { get; set; }
	public string ThrowingEffect { get; set; }
	public string Equipment { get; set; }
	public string Projectile { get; set; }
}

public sealed class FallingDamageTag {
	public string FallingDamage { get; set; }
	// World units per second: the landing speeds of those two falls.
	public float[] DamageVelocityBounds { get; set; }
}

// The multiplayer globals (mulg): team colors, the weapons a variant can name, and the spawn influences.
public sealed class MultiplayerGlobalsTag {
	public List<MultiplayerUniversalTag> Universal { get; set; } = new();
	public List<MultiplayerRuntimeTag> Runtime { get; set; } = new();

	public MultiplayerRuntimeTag Rules => Runtime?.FirstOrDefault();
	public List<ColorEntryTag> TeamColors => Universal?.FirstOrDefault()?.TeamColors;
}

public sealed class MultiplayerUniversalTag {
	// One per multiplayer team, red first.
	public List<ColorEntryTag> TeamColors { get; set; } = new();
}

public sealed class MultiplayerRuntimeTag {
	// Spawn influences reach this far below and above a start, world units.
	public float DynamicZoneUpperHeight { get; set; }
	public float DynamicZoneLowerHeight { get; set; }
	public SpawnInfluenceTag Enemy { get; set; }
	public SpawnInfluenceTag Friend { get; set; }
	public SpawnInfluenceTag DeadAlly { get; set; }
	public List<MultiplayerConstantsTag> MultiplayerConstants { get; set; } = new();

	public MultiplayerConstantsTag Constants => MultiplayerConstants?.FirstOrDefault();
}

// Full weight inside the inner radius, fading to none at the outer one.
public sealed class SpawnInfluenceTag {
	public float InnerRadius { get; set; }
	public float OuterRadius { get; set; }
	public float Weight { get; set; }
}

public sealed class MultiplayerConstantsTag {
	// The random part of a start's score, up to this.
	public float MaximumRandomSpawnBias { get; set; }
	public float GrenadeDangerWeight { get; set; }
	public float GrenadeDangerInnerRadius { get; set; }
	public float GrenadeDangerOuterRadius { get; set; }
	// Seconds a grenade is projected ahead.
	public float GrenadeDangerLeadTime { get; set; }
}

public sealed class UnitHudSoundTag {
	public string Sound { get; set; }
	// The scale Halo plays it at.
	public float Scale { get; set; } = 1;
}

// One multiplayer map's scenario data: where players start, what spawns where.
public sealed class HaloMapData {
	public List<PlayerStartData> PlayerStarts { get; set; } = new();

	// The scenario's crate block: entry i is the scene object tagged halo_object_<i>.
	public List<CratePlacementData> Crates { get; set; } = new();
	public List<NetgameFlagData> NetgameFlags { get; set; } = new();
	public List<NetgameEquipmentData> NetgameEquipment { get; set; } = new();
	public List<StartingEquipmentData> StartingEquipment { get; set; } = new();

	// The scenario's local north in radians, which the compass object function measures from.
	public float LocalNorth { get; set; }
	// The scenario's spawn data, null without: its overloads replace the multiplayer globals' spawn influences.
	public SpawnDataTag SpawnData { get; set; }
}

// A scenario's spawn data. Heights are world units, 0 meaning the multiplayer globals' value.
public sealed class SpawnDataTag {
	public float DynamicSpawnLowerHeight { get; set; }
	public float DynamicSpawnUpperHeight { get; set; }
	public List<SpawnOverloadTag> DynamicSpawnOverloads { get; set; } = new();
	public List<SpawnZoneTag> StaticRespawnZones { get; set; } = new();
	public List<SpawnZoneTag> StaticInitialSpawnZones { get; set; } = new();
}

// "enemy", "friend", "dead ally"... with the influence that replaces the multiplayer globals' for it.
public sealed class SpawnOverloadTag {
	public string OverloadType { get; set; }
	public float InnerRadius { get; set; }
	public float OuterRadius { get; set; }
	public float Weight { get; set; }
}

// A static zone pulls spawns in or pushes them out like an influence, for the teams and game types it names.
public sealed class SpawnZoneTag {
	public string[] RelevantTeam { get; set; } = Array.Empty<string>();
	public string[] RelevantGames { get; set; } = Array.Empty<string>();
	public string[] Flags { get; set; } = Array.Empty<string>();
	public float[] Position { get; set; }
	public float LowerHeight { get; set; }
	public float UpperHeight { get; set; }
	public float InnerRadius { get; set; }
	public float OuterRadius { get; set; }
	public float Weight { get; set; }
}

public sealed class CratePlacementData {
	// The crate tag.
	public string Type { get; set; }
	public string[] PlacementFlags { get; set; } = Array.Empty<string>();

	// Halo leaves a crate created at rest asleep until something touches it.
	public bool CreatedAtRest => PlacementFlags?.Contains( "create at rest" ) ?? false;
}

public sealed class PlayerStartData {
	public float[] Position { get; set; }
	// Degrees.
	public float Facing { get; set; }
}

public sealed class NetgameFlagData {
	public float[] Position { get; set; }
	public float Facing { get; set; }
	// "teleporter (src)", "teleporter (dest)", "ctf flag spawn", ...
	public string Type { get; set; }
	// The teleporter channel.
	public int Identifier { get; set; }
}

// The game types a scenario entry is for, by Halo 2's game type names.
public interface IGameTypes {
	string GameType1 { get; }
	string GameType2 { get; }
	string GameType3 { get; }
	string GameType4 { get; }
}

public sealed class NetgameEquipmentData : IGameTypes {
	// "levitate", "destroy existing on new spawn".
	public string[] Flags { get; set; } = Array.Empty<string>();
	public string GameType1 { get; set; }
	public string GameType2 { get; set; }
	public string GameType3 { get; set; }
	public string GameType4 { get; set; }
	// Seconds; 0 leaves it to the item collection.
	public int SpawnTime { get; set; }
	// "on pick up" or "on body depletion".
	public string RespawnTimerStarts { get; set; }
	public float[] Position { get; set; }
	// Yaw, pitch and roll, degrees.
	public float[] Orientation { get; set; }
	public string ItemVehicleCollection { get; set; }

	public bool HasFlag( string flag ) => Flags?.Contains( flag ) ?? false;
}

// What players spawn with: the first two different weapons its item collections pick, and its grenade rule.
public sealed class StartingEquipmentData : IGameTypes {
	// "no grenades", "plasma grenades".
	public string[] Flags { get; set; } = Array.Empty<string>();
	public string GameType1 { get; set; }
	public string GameType2 { get; set; }
	public string GameType3 { get; set; }
	public string GameType4 { get; set; }
	public string ItemCollection1 { get; set; }
	public string ItemCollection2 { get; set; }
	public string ItemCollection3 { get; set; }
	public string ItemCollection4 { get; set; }
	public string ItemCollection5 { get; set; }

	public bool HasFlag( string flag ) => Flags?.Contains( flag ) ?? false;
	public IEnumerable<string> ItemCollections => new[] { ItemCollection1, ItemCollection2, ItemCollection3, ItemCollection4, ItemCollection5 };
}

// A crate tag: its push scale and the physics the mount rebuilds from its model's physics model, in s&box units.
public sealed class CrateTag {
	public float AccelerationScale { get; set; }
	// The model tag, whose damage info makes it destructible.
	public string Model { get; set; }
	public CratePhysicsTag Physics { get; set; }
}

// A crate's or a biped's physics model.
public sealed class CratePhysicsTag {
	// The model's _physics.vmdl, whose physics body i is Bodies[i].
	public string Model { get; set; }
	public List<CrateBodyTag> Bodies { get; set; } = new();
	public List<CrateJointTag> Joints { get; set; } = new();
	// How each model node follows a ragdoll's bodies.
	public List<PhysicsNodeTag> Nodes { get; set; } = new();
}

// A ragdoll's node: "body" is a body's own, "default" rides Body at a fixed Position and Rotation in that body's space,
// and "animation" keeps its last animated place under its parent.
public sealed class PhysicsNodeTag {
	public string Node { get; set; }
	public string Pose { get; set; }
	public int? Body { get; set; }
	public float[] Position { get; set; }
	public float[] Rotation { get; set; }
}

// A rigid body in the model's space at its bind pose; its shapes, centre of mass and inertia are in its own space.
public sealed class CrateBodyTag {
	// The bone it moves, whose frame is the body's.
	public string Bone { get; set; }
	// sphere, stabilized sphere, box, stabilized box, keyframed or fixed.
	public string MotionType { get; set; }
	public float[] Position { get; set; }
	// x, y, z, w.
	public float[] Rotation { get; set; }
	public float Mass { get; set; }
	public float[] CenterOfMass { get; set; }
	// kg·in² about the principal axes, which InertiaRotation turns to.
	public float[] Inertia { get; set; }
	public float[] InertiaRotation { get; set; }
	public float LinearDamping { get; set; }
	public float AngularDamping { get; set; }
	public List<CrateShapeTag> Shapes { get; set; } = new();

	public bool Fixed => MotionType == "fixed";
}

// hull (Points), box (Center, Rotation, HalfExtents), sphere (Center, Radius) or capsule (A, B, Radius).
public sealed class CrateShapeTag {
	public string Type { get; set; }
	public List<float[]> Points { get; set; } = new();
	public float[] Center { get; set; }
	public float[] Rotation { get; set; }
	public float[] HalfExtents { get; set; }
	public float[] A { get; set; }
	public float[] B { get; set; }
	public float Radius { get; set; }
}

// A joint between two bodies, each frame in its body's space, z the twist or hinge axis.
public sealed class CrateJointTag {
	// "ragdoll" or "limited hinge".
	public string Type { get; set; }
	// B is null for a joint to the fixed world.
	public int BodyA { get; set; }
	public int? BodyB { get; set; }
	public float[] PositionA { get; set; }
	public float[] RotationA { get; set; }
	public float[] PositionB { get; set; }
	public float[] RotationB { get; set; }
	// Degrees: B's twist or hinge angle from A's about z.
	public float[] Limit { get; set; }
	// Degrees: a ragdoll joint's cone bounds, the larger of which Havok takes as its round cone.
	public float[] Cone { get; set; }
}
