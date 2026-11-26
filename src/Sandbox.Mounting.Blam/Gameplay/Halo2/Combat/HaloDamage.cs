using System;
using Halo2.Data;

namespace Halo2.Combat;

// One application of a damage effect tag to a target.
public struct DamageEvent {
	public string DamageEffect;

	// 0..1 between the tag's damage lower bound and its random upper bound.
	// Area damage scales with distance, the shotgun's pellets with range, a melee with the attacker's speed.
	public float Scale;

	public Vector3 Position;
	// The way the damage travels.
	public Vector3 Direction;

	// Explosions hit the target's indirect material and section.
	public bool AreaOfEffect;

	// The model material a direct hit struck, e.g. "head" or "body", and the node it struck.
	public string MaterialName;
	public string Node;

	public Guid AttackerId;
	public string WeaponName;

	// An act of god that kills outright.
	public bool KillInstantly;

	// Seeds the random part of the damage roll so it's reproducible.
	public int Seed;
}

// A unit's vitality as Halo 2 keeps it: shield and body as fractions of their maximums, and their stuns in ticks.
public struct Vitality {
	public float Shield;
	public float Body;
	public int ShieldStunTicks;
	public int BodyStunTicks;
	// The overshield powerup charging the shield to 3; it takes every hit meanwhile.
	public bool Overcharging;
	public bool ShieldCharging;
	public bool ShieldDepleted;

	public static Vitality Full( float shield ) => new() { Shield = shield, Body = 1 };
}

// What a damage event did to a target.
public struct DamageResult {
	// The hit came to some damage, so the shield, body and aftermath ran at all.
	public bool Dealt;

	// Fractions of the maximum vitalities taken.
	public float ShieldDamage;
	public float BodyDamage;
	public bool ShieldDepleted;
	public bool Headshot;
	public bool Killed;

	// The global material the hit answered with: the shield's while it took the hit, else the body's.
	public string GlobalMaterial;
}

// What a vitality tick did, for its effects.
public struct VitalityTick {
	public bool ShieldRechargeStarted;
}

// Halo 2's damage and vitality rules for a unit's model.
public static class HaloDamage {
	// Halo's real epsilon: body damage under it doesn't stun a down shield.
	const float RealEpsilon = 0.0001f;

	// The overshield charges at 1/30 a tick up to three shields.
	public const float OvershieldMaximum = 3f;
	const float OvershieldChargePerTick = 2f / (2f * HaloUnits.TicksPerSecond);

	// Above a multiplayer unit's shield level, the shield drains two shields over 90 seconds.
	const float OvershieldDecayPerTick = 2f / (90f * HaloUnits.TicksPerSecond);

	// The damage before materials: lerp(lower bound, random upper bound, scale).
	public static float Amount( DamageEffectTag tag, float scale, ref HaloRandom random ) {
		var upper = tag.DamageLowerBound;
		if ( tag.DamageUpperBound is { Length: >= 2 } u )
			upper = u[0] == u[1] ? u[0] : random.Float( u[0], u[1] );
		return (1 - scale) * tag.DamageLowerBound + scale * upper;
	}

	// The damage table's multiplier: the effect's damage groups times the material's armors, 1 where none is listed.
	public static float TableMultiplier( GlobalsTag globals, DamageEffectTag tag, string globalMaterial ) {
		var table = globals?.DamageTable?.FirstOrDefault()?.DamageGroups;
		var material = globals?.Materials?.FirstOrDefault( x => x.Name == globalMaterial );
		if ( table is null || material is null ) return 1;

		var multiplier = 1f;
		foreach ( var damage in new[] { tag.GeneralDamage, tag.SpecificDamage } ) {
			if ( string.IsNullOrEmpty( damage ) || table.FirstOrDefault( x => x.Name == damage ) is not { } group ) continue;
			foreach ( var armor in new[] { material.GeneralArmor, material.SpecificArmor } ) {
				if ( string.IsNullOrEmpty( armor ) ) continue;
				if ( group.ArmorModifiers?.FirstOrDefault( x => x.Name == armor ) is { } modifier ) multiplier *= modifier.DamageMultiplier;
			}
		}
		return multiplier;
	}

	// Rolls the damage, then the shield takes the whole hit and only what it can't hold goes on to the body.
	// The multiplier is the game's: friendly fire, spawn protection.
	// unsuspecting: a "lethal to the unsuspecting" effect kills outright, a melee from behind.
	public static DamageResult Apply( GlobalsTag globals, DamageEffectTag tag, ModelTag model, DamageEvent e, ref Vitality v, float multiplier, bool unsuspecting ) {
		var result = new DamageResult();
		var info = model?.DamageInfo;
		if ( tag is null || info is null ) return result;

		var random = new HaloRandom( e.Seed );
		var damage = Amount( tag, e.Scale, ref random ) * multiplier;
		if ( damage <= 0 ) return result;
		result.Dealt = true;

		var hit = e.AreaOfEffect ? null : model.Materials?.FirstOrDefault( x => x.MaterialName == e.MaterialName );
		var bodyMaterial = hit?.GlobalMaterialName ?? info.GlobalIndirectMaterialName;
		var section = info.Section( hit is not null ? hit.DamageSection : info.IndirectDamageSection );
		result.GlobalMaterial = bodyMaterial;
		var wasAlive = v.Body > 0;

		var canDie = !info.HasFlag( "cannot die from damage" );
		if ( canDie && (e.KillInstantly || (unsuspecting && tag.SideEffect == "lethal to the unsuspecting")) ) {
			result.ShieldDamage = v.Shield;
			result.BodyDamage = v.Body;
			result.ShieldDepleted = !v.ShieldDepleted && v.Shield > 0;
			v.Shield = 0;
			v.ShieldDepleted = true;
			v.Body = 0;
			result.Killed = wasAlive;
			return result;
		}

		var remaining = damage;
		var skipsShield = tag.HasFlag( "skips shields" ) || (section?.HasFlag( "ignores shields" ) ?? false);
		if ( !skipsShield && info.MaximumShieldVitality > 0 )
			remaining = DamageShield( globals, tag, info, damage, ref v, ref result );

		if ( !tag.HasFlag( "only hurts shields" ) && info.MaximumVitality > 0 ) {
			var body = remaining * TableMultiplier( globals, tag, bodyMaterial );
			if ( body > 0 ) {
				// A headshot empties the body first, so any damage reaching the head kills.
				if ( (section?.HasFlag( "headshotable" ) ?? false) && tag.HasFlag( "can cause headshots" ) ) {
					result.BodyDamage = v.Body;
					v.Body = 0;
					result.Headshot = true;
				}
				v.Body -= body / info.MaximumVitality;
				result.BodyDamage += body / info.MaximumVitality;
				if ( !canDie ) v.Body = MathF.Max( v.Body, RealEpsilon );
				if ( body > info.MinimumStunDamage ) v.BodyStunTicks = StunTicks( info.StunTime );
			}
		}

		result.Killed = wasAlive && info.MaximumVitality * v.Body <= 0;
		return result;
	}

	// The shield soaks the hit through its material; past empty, the rest goes back to raw damage for the body.
	// An overcharging shield takes every hit. Any real hit stuns the recharge, as does body damage with the shield down.
	// Returns the damage left for the body.
	static float DamageShield( GlobalsTag globals, DamageEffectTag tag, DamageInfoTag info, float damage, ref Vitality v, ref DamageResult result ) {
		var raw = v.Shield > 0 ? MathF.Max( damage, 0 ) : 0;
		if ( v.Overcharging ) {
			result.GlobalMaterial = info.GlobalShieldMaterialName;
			return 0;
		}

		var remaining = damage - raw;
		var multiplier = raw > 0 ? TableMultiplier( globals, tag, info.GlobalShieldMaterialName ) : 1;
		var before = v.Shield;
		v.Shield -= raw * multiplier / info.MaximumShieldVitality;
		if ( raw > 0 ) result.GlobalMaterial = info.GlobalShieldMaterialName;

		if ( v.Shield <= 0 ) {
			if ( v.Shield < 0 ) {
				remaining += -v.Shield * info.MaximumShieldVitality / multiplier;
				v.Shield = 0;
			}
			if ( !v.ShieldDepleted ) {
				v.ShieldDepleted = true;
				result.ShieldDepleted = true;
			}
		}
		result.ShieldDamage = before - v.Shield;

		if ( raw * multiplier > info.ShieldMinimumStunDamage || (v.Shield == 0 && remaining > RealEpsilon) )
			v.ShieldStunTicks = StunTicks( info.ShieldStunTime );
		return remaining;
	}

	static int StunTicks( float seconds ) => (int)(HaloUnits.TicksPerSecond * seconds + 0.5f);

	// One tick of the shield and body.
	// The overshield charges to 3, then drains back to the unit's shield level without counting the stun down.
	// Below that level a stunned shield waits, then recharges at its velocity; the body recharges the same way alone.
	// maxLevel is the shield a multiplayer unit has: 1 with normal shields, 0 with none, 3 with overshields.
	public static VitalityTick Update( DamageInfoTag info, ref Vitality v, float maxLevel ) {
		var tick = new VitalityTick();
		var dt = HaloUnits.TickInterval;

		if ( info.MaximumShieldVitality > 0 ) {
			if ( v.Overcharging ) {
				v.Shield += OvershieldChargePerTick;
				if ( v.Shield >= OvershieldMaximum ) {
					v.Shield = OvershieldMaximum;
					v.Overcharging = false;
					v.ShieldCharging = false;
				}
				else {
					v.ShieldCharging = true;
				}
			}
			else if ( v.Shield > maxLevel ) {
				v.ShieldCharging = false;
				// The excess is measured from one shield, whatever the unit's level.
				v.Shield = v.Shield - 1 < OvershieldDecayPerTick ? maxLevel : v.Shield - OvershieldDecayPerTick;
			}
			else if ( v.Shield < maxLevel ) {
				if ( v.ShieldStunTicks == 0 ) {
					v.ShieldDepleted = false;
					tick.ShieldRechargeStarted = !v.ShieldCharging;
					v.ShieldCharging = true;
					v.Shield += info.ShieldRechargeVelocity * dt;
					if ( v.Shield > maxLevel ) {
						v.Shield = maxLevel;
						v.ShieldCharging = false;
					}
				}
				else {
					v.ShieldStunTicks--;
					v.ShieldCharging = false;
				}
			}
			else {
				v.ShieldCharging = false;
			}
		}

		if ( info.MaximumVitality > 0 && info.RechargeFraction > v.Body && info.HealthRechargeVelocity > 0 ) {
			if ( v.BodyStunTicks == 0 )
				v.Body = MathF.Min( v.Body + info.HealthRechargeVelocity * dt, info.RechargeFraction );
			else
				v.BodyStunTicks--;
		}

		return tick;
	}

	// The overshield powerup, taken only at or under one shield, charges it from the next tick, an empty one from 0.01.
	public static bool Overshield( ref Vitality v ) {
		if ( v.Shield > 1 ) return false;
		v.Overcharging = true;
		if ( v.Shield == 0 ) v.Shield = 0.01f;
		v.ShieldStunTicks = 0;
		return true;
	}

	// Area damage's distance scale: full within the falloff radius, down to the lower bound at the cutoff, none past it.
	public static float AreaScale( DamageEffectTag tag, float distanceWu ) {
		var falloff = tag.FalloffRadius;
		var cutoff = tag.CutoffRadius;
		if ( distanceWu > cutoff ) return 0;
		if ( tag.HasEffectFlag( "don't scale damage by distance" ) || cutoff <= falloff ) return 1;
		return Math.Clamp( 1 - (distanceWu - falloff) / (cutoff - falloff), 0, 1 );
	}
}
