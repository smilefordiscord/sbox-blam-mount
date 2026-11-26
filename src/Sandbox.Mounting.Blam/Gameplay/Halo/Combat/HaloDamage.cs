using System;
using Halo.Data;

namespace Halo.Combat;

// One application of a damage_effect tag to a target.
public struct DamageEvent {
	public string DamageEffect;

	// 0..1 between the tag's damage minimum and its random upper range.
	// Area damage scales with distance, impacts with projectile speed, melee with forward speed.
	public float Scale;

	public Vector3 Position;
	public Vector3 Direction;

	// Explosions and melee hit the target's indirect material.
	public bool AreaOfEffect;

	// The collision material hit by a direct hit, e.g. "head" or "body".
	public string MaterialName;

	// Picks the death animation. 0, the gut, for damage without a node such as area, melee or falling.
	public int DamagePart;

	public Guid AttackerId;
	public string WeaponName;

	// The attacker is behind the target.
	public bool TargetUnsuspecting;

	// An act of god that kills outright.
	public bool KillInstantly;

	// A silent act of god: no ping, no damage sound.
	public bool Silent;

	// Goes straight to the body, as when a vehicle hit passes through to its non-player driver.
	public bool BypassesShields;

	// A projectile impact, attached detonation or melee. An AI attacker's burst damage modifier scales it.
	public bool FromWeapon;

	// Seeds the random part of the damage roll so it's reproducible.
	public int Seed;

	// 0 means 1. E.g. the share of a vehicle hit that passes through to its riders.
	public float Multiplier;
}

// What a damage event did to a target, as fractions of its maximum vitality.
public struct DamageResult {
	// The hit came to some damage, so the shield, body and aftermath ran at all.
	public bool Dealt;

	public float ShieldDamage;
	public float BodyDamage;
	public bool ShieldDepleted;
	// A non-negligible hit on a shield it didn't knock down, or any hit while the overshield charges.
	public bool ShieldFlare;
	public bool Headshot;

	// Added to recent body damage. Unlike BodyDamage, a headshot kill counts only the hit's own damage.
	public float RecentBodyDamage;

	// The body's share in damage points, before material modifiers and vitality. Drives the damage sound.
	public float BodyDamageAmount;

	// The shield's share in damage points, before its material modifiers.
	public float ShieldDamageAmount;

	// The damage the shield held, as a share of its maximum.
	public float RecentShieldDamage;

	// A forced kill, or a "can cause headshots" headshot.
	public bool KilledInstantly;

	// Outside campaign, a headshot forces a hard ping, and a multiplayer headshot only when lethal.
	// With the shield depleted it makes the kill a hard one.
	public bool ForceHardPing;
	public bool Killed;
	public bool InstantKill;
	public float StunTicks;

	// The material index the hit's effects answer to: the material hit, or the shield's when it took more.
	// Null without a collision model.
	public int? MaterialType;

	// The body went past its destroyed threshold, so the object is destroyed.
	public bool BodyDestroyed;
}

// What damage knows of the game and target beyond the tags.
// The default is multiplayer: the game engine runs and difficulty scales nothing.
public struct DamageContext {
	// No game engine: headshots force no hard ping, don't kill a player's biped, and multiplayer headshots don't double.
	public bool Campaign;

	public bool PlayerBiped;

	// A multiplayer target counts as dead once no body is left.
	public bool Dead;

	// Shield and body don't go down, though the shield can still be knocked down.
	public bool CannotTakeDamage;

	// The target isn't an enemy of the damage's team.
	public bool Friendly;

	// The difficulty's enemy damage scale the hit was multiplied by, 0 for none. Friendly damage is divided back by it.
	public float EnemyDamage;

	// Difficulty scale on maximum body and shield vitality, 0 meaning 1.
	public float BodyScale, ShieldScale;

	// The shield was knocked down and hasn't begun to recharge.
	public bool ShieldDepleted;

	// An empty vehicle that's only damaged while occupied: its body takes nothing.
	public bool Unoccupied;

	public float BodyMaximum( CollisionModelTag collision ) => collision.MaximumBodyVitality * (BodyScale > 0 ? BodyScale : 1);

	public float ShieldMaximum( CollisionModelTag collision ) => collision.MaximumShieldVitality * (ShieldScale > 0 ? ShieldScale : 1);
}

// Halo's damage rules for shield and body. Vitalities are fractions of the collision model's maximum, as in Halo.
public static class HaloDamage {
	// The damage before materials: lerp(minimum, random upper range, scale).
	public static float Amount( DamageEffectTag tag, float scale, ref HaloRandom random ) {
		var upper = tag.DamageUpperBound is { Length: >= 2 } u ? random.Float( u[0], u[1] ) : tag.DamageLowerBound;
		return (1 - scale) * tag.DamageLowerBound + upper * scale;
	}

	// Rolls the damage, applies any forced kill, then damages shield and body and updates the vitalities.
	// A hit that comes to no damage does nothing.
	public static DamageResult Apply( DamageEffectTag tag, CollisionModelTag collision, DamageEvent e,
		ref float shieldVitality, ref float bodyVitality, bool shieldOvercharging, bool friendlyFire, float damageMultiplier = 1, in DamageContext context = default ) {
		var result = new DamageResult();
		if ( tag is null || collision is null ) return result;

		var random = new HaloRandom( e.Seed );
		var total = Amount( tag, e.Scale, ref random ) * damageMultiplier * (e.Multiplier > 0 ? e.Multiplier : 1);
		if ( total <= 0 ) return result;
		result.Dealt = true;

		var material = PickMaterial( collision, e );
		var dead = context.Campaign ? context.Dead : bodyVitality <= 0;
		var friendly = friendlyFire || context.Friendly;

		// An act of god or a backstab with a melee lethal to the unsuspecting empties the body first.
		// That also marks the shield depleted, so it still takes the hit but can't count as knocked down by it.
		var unsuspecting = tag.DamageSideEffect == "lethal to the unsuspecting" && e.TargetUnsuspecting && !context.CannotTakeDamage;
		var forceKill = !dead && (e.KillInstantly || unsuspecting);
		var shieldBefore = shieldVitality;
		var bodyBefore = bodyVitality;
		if ( forceKill ) bodyVitality = 0;

		if ( collision.MaximumShieldVitality > 0 && !tag.HasFlag( "skips shields" ) && !e.BypassesShields )
			DamageShield( tag, collision, material, context, friendly, shieldOvercharging, forceKill || context.ShieldDepleted, ref shieldVitality, ref total, ref result );

		if ( collision.HasFlag( "only damaged by explosives" ) && !tag.HasFlag( "detonates explosives" ) ) total = 0;
		DamageBody( tag, collision, material, e, context, friendly, dead || forceKill, total, ref bodyVitality, ref result );

		// The shield's material wins when the shield took more of the hit.
		result.MaterialType = MaterialTypeIndex( material?.MaterialType );
		var effective = result.ShieldDamageAmount > RealEpsilon || result.BodyDamageAmount > RealEpsilon;
		if ( effective && result.ShieldDamageAmount > result.BodyDamageAmount )
			result.MaterialType = MaterialTypeIndex( collision.ShieldMaterialType );

		if ( forceKill ) {
			result.KilledInstantly = true;
			result.InstantKill = true;
			result.Killed = true;
			result.BodyDamage = bodyBefore;
			result.ShieldDamage = shieldBefore;
			shieldVitality = 0;
			if ( !result.BodyDestroyed ) bodyVitality = 0;
		}

		return result;
	}

	// A standing shield takes its share minus leakage, which grows as a failing shield gives way.
	// What it can't hold goes on to the body, but an overcharging shield soaks the lot.
	// A big enough hit, or any hit with the shield down, stuns the recharge.
	static void DamageShield( DamageEffectTag tag, CollisionModelTag collision, CollisionMaterialTag material, in DamageContext context, bool friendly,
		bool overcharging, bool depletedBit, ref float shieldVitality, ref float total, ref DamageResult result ) {
		var shieldDamage = total;
		if ( shieldVitality > 0 ) {
			var maximum = context.ShieldMaximum( collision );
			var inverseMaximum = maximum > 0 ? 1 / maximum : 0;
			var totalBefore = total;
			var negligible = false;

			if ( !friendly || !collision.HasFlag( "always shields friendly damage" ) ) {
				shieldDamage = (1 - (material?.ShieldLeakPercentage ?? 0)) * total;
				var threshold = collision.ShieldFailureThreshold;
				if ( shieldVitality <= threshold && threshold > 0 ) {
					var failure = HaloFunctions.Transition( collision.ShieldFailureFunction, shieldVitality / threshold );
					shieldDamage *= (1 - collision.FailingShieldLeakFraction) * failure + collision.FailingShieldLeakFraction;
				}
			}

			if ( overcharging ) {
				shieldDamage = total;
				total = 0;
			}
			else {
				shieldDamage = MathF.Max( shieldDamage, 0 );
				total -= shieldDamage;
				if ( friendly && context.EnemyDamage > 0 ) shieldDamage /= context.EnemyDamage;

				var actual = shieldDamage * (material?.ShieldDamageMultiplier ?? 1) * tag.MaterialModifier( collision.ShieldMaterialType );
				negligible = actual < RealEpsilon;
				var normalized = actual * inverseMaximum;

				if ( normalized > shieldVitality || tag.DamageSideEffect == "emp" ) {
					// Damage past the shield bleeds through to the body.
					var excess = actual - maximum * shieldVitality;
					if ( excess > 0 ) total += excess;
					result.ShieldDamage = shieldVitality;
					shieldVitality = 0;
					result.ShieldDepleted = !depletedBit;
				}
				else {
					if ( !context.CannotTakeDamage ) shieldVitality -= normalized;
					result.ShieldDamage = normalized;
				}
			}

			if ( !negligible ) {
				result.RecentShieldDamage = (totalBefore - total) * inverseMaximum;
				result.ShieldFlare = !depletedBit && !result.ShieldDepleted;
			}
		}
		else {
			shieldDamage = 0;
			shieldVitality = 0;
		}

		result.ShieldDamageAmount = shieldDamage;
		if ( shieldDamage >= collision.MinimumStunDamage || shieldVitality == 0 )
			result.StunTicks = collision.StunTime * HaloUnits.TicksPerSecond;
	}

	// The remaining damage, through the material hit and friendly damage resistance.
	// "can cause headshots" kills on a headshot; "can cause multiplayer headshots" doubles it outside campaign.
	// A body taken past empty dies, and past its destroyed threshold is destroyed.
	static void DamageBody( DamageEffectTag tag, CollisionModelTag collision, CollisionMaterialTag material, DamageEvent e, in DamageContext context,
		bool friendly, bool dead, float total, ref float bodyVitality, ref DamageResult result ) {
		if ( tag.HasFlag( "only hurts shields" ) ) return;

		var maximum = context.BodyMaximum( collision );
		var inverseMaximum = maximum > 0 ? 1 / maximum : 0;
		var amount = context.Unoccupied ? 0 : (material?.BodyDamageMultiplier ?? 1) * total;
		result.BodyDamageAmount = amount;

		var actual = amount;
		if ( friendly ) {
			actual = (1 - collision.FriendlyDamageResistance) * amount;
			if ( context.EnemyDamage > 0 ) actual /= context.EnemyDamage;
		}
		actual *= inverseMaximum * tag.MaterialModifier( material?.MaterialType ?? "cyborg armor" );

		var before = bodyVitality;
		var headshotKill = false;
		if ( !context.CannotTakeDamage ) {
			if ( amount > 0 && material is { IsHead: true } && !e.AreaOfEffect ) {
				if ( tag.HasFlag( "can cause headshots" ) ) {
					// In campaign a player's biped survives it, and no hard ping is forced.
					if ( !context.Campaign || !context.PlayerBiped ) {
						bodyVitality = 0;
						headshotKill = true;
						result.Headshot = true;
						result.KilledInstantly = true;
						result.ForceHardPing = !context.Campaign;
					}
				}
				else if ( tag.HasFlag( "can cause multiplayer headshots" ) && !context.Campaign ) {
					actual *= 2;
					result.Headshot = true;
					result.ForceHardPing = actual > bodyVitality;
				}
			}

			bodyVitality -= actual;
		}

		// A headshot kill counts as at least the whole body.
		result.BodyDamage = headshotKill ? MathF.Max( actual, before + 1 ) : actual;
		result.RecentBodyDamage = actual;

		var left = bodyVitality * maximum;
		if ( collision.BodyDestroyedThreshold < 0 && left < collision.BodyDestroyedThreshold ) {
			result.BodyDestroyed = true;
			result.Killed = !dead;
		}
		else if ( !dead && left < 0 )
			result.Killed = true;
	}

	static int? MaterialTypeIndex( string name ) => name is not null && Array.IndexOf( HaloSurfaces.Names, name.Replace( ' ', '_' ) ) is >= 0 and var type ? type : null;

	// Halo's real epsilon. Shield damage under it is negligible.
	const float RealEpsilon = 0.0001f;

	static CollisionMaterialTag PickMaterial( CollisionModelTag collision, DamageEvent e ) {
		var materials = collision.Materials;
		if ( materials is null || materials.Count == 0 ) return null;

		if ( e.AreaOfEffect || string.IsNullOrEmpty( e.MaterialName ) ) {
			var index = collision.IndirectDamageMaterial ?? 0;
			return index >= 0 && index < materials.Count ? materials[index] : materials[0];
		}

		return materials.FirstOrDefault( m => m.Name == e.MaterialName ) ?? materials[0];
	}

	// Distance scale for area damage: full inside the falloff radius, fading to 0 at the cutoff.
	public static float AreaScale( DamageEffectTag tag, float distanceWu ) {
		if ( tag.HasFlag( "don't scale damage by distance" ) ) return 1;

		var falloff = tag.FalloffRadius;
		var cutoff = tag.CutoffRadius;
		if ( cutoff <= falloff ) return distanceWu <= cutoff ? 1 : 0;

		return Math.Clamp( 1 - (distanceWu - falloff) / (cutoff - falloff), 0, 1 );
	}

	// A fraction per tick: 1 / (recharge time in ticks).
	public static float ShieldRechargePerTick( CollisionModelTag collision ) {
		if ( collision.ShieldRechargeRate > 0 ) return collision.ShieldRechargeRate;
		return collision.RechargeTime > 0 ? 1 / (collision.RechargeTime * HaloUnits.TicksPerSecond) : 0;
	}

	// Multiplayer overshield above 1.0 drains at 1/1350 per tick (two extra shields over 90 s).
	public const float OvershieldDecayPerTick = 0.00074074074f;

	// An overshield charges at 1/30 per tick up to 3x shields.
	public const float OvershieldChargePerTick = 1f / 30f;
	public const float OvershieldMaximum = 3f;
}
