using System;
using System.Runtime.CompilerServices;
using Halo.Data;

namespace Halo.Combat;

// A Sandbox player or NPC as a Halo unit. Halo's damage reaches it as Sandbox damage.
// It's treated as a Halo multiplayer cyborg without a shield model: Halo's 75 shield and 75 body vitality are Sandbox's 100 health.
public sealed class SandboxUnit : IHaloUnit {
	public const float HealthPerDamagePoint = 100f / 150f;

	static readonly ConditionalWeakTable<GameObject, SandboxUnit> Units = new();

	readonly Component _owner;

	SandboxUnit( Component owner ) => _owner = owner;

	// The unit for a player's or NPC's object or anything under it, else null.
	public static SandboxUnit For( GameObject go ) {
		if ( !go.IsValid() ) return null;
		Component owner = go.GetComponentInParent<PlayerController>( true );
		owner ??= go.GetComponentInParent<Sandbox.Npcs.BaseNpc>( true );
		if ( owner is null ) return null;
		return Units.GetValue( owner.GameObject, _ => new SandboxUnit( owner ) );
	}

	public bool IsValid => _owner.IsValid();
	public GameObject GameObject => _owner.GameObject;
	public PlayerController Player => _owner as PlayerController;
	public Sandbox.Npcs.BaseNpc Npc => _owner as Sandbox.Npcs.BaseNpc;

	public bool IsAlive => _owner switch {
		PlayerController p => p.Health > 0,
		Sandbox.Npcs.BaseNpc n => n.Health > 0,
		_ => false,
	};

	public Transform Root => new( GameObject.WorldPosition, Rotation.FromYaw( GameObject.WorldRotation.Yaw() ) );

	public Vector3 Center => Renderer is { } r && r.IsValid() ? r.Bounds.Center : GameObject.WorldPosition + Vector3.Up * 36;

	public Vector3 Head => Player is { } p ? p.EyeTransform.Position : GameObject.WorldPosition + Vector3.Up * 64;

	public SkinnedModelRenderer Renderer => Player is { } p ? p.Controller?.Renderer : GameObject.GetComponentInChildren<SkinnedModelRenderer>();

	public SkinnedModelRenderer Corpse => null;

	// Sandbox's people are flesh: Halo's human skin, which bleeds.
	public int HitMaterial( in UnitHit hit ) => HumanSkin;

	public const int HumanSkin = 24;

	public DamageResult TakeDamage( DamageEvent e ) => Apply( GameObject, e );

	// Halo damage to any Sandbox object that takes damage, and its push to any rigidbody.
	// Returns what was dealt; MaterialType stays null so the projectile's response follows the surface hit.
	public static DamageResult Apply( GameObject target, DamageEvent e ) {
		var result = new DamageResult();
		var tag = HaloDataStore.Game?.DamageEffect( e.DamageEffect );
		if ( tag is null || !target.IsValid() ) return result;

		var random = new HaloRandom( e.Seed );
		var points = HaloDamage.Amount( tag, e.Scale, ref random ) * (e.Multiplier > 0 ? e.Multiplier : 1);
		var head = e.MaterialName == "head" && tag.HasFlag( "can cause headshots" );

		var attacker = Game.ActiveScene?.Directory.FindByGuid( e.AttackerId );
		var weapon = HaloWeaponName( e.WeaponName, attacker );
		var info = new DamageInfo( points * HealthPerDamagePoint, attacker, weapon ) {
			Position = e.Position,
			Origin = e.Position - e.Direction * 32,
		};
		if ( head ) info.Tags.Add( "head" );
		if ( e.AreaOfEffect ) {
			info.Tags.Add( "explosion" );
		}
		if ( tag.DamageCategory is { Length: > 0 } category ) info.Tags.Add( $"halo_{category.Replace( ' ', '_' )}" );

		if ( points > 0 )
			target.GetComponentInParent<Component.IDamageable>()?.OnDamage( info );

		Push( target, tag, e, points );

		result.Dealt = points > 0;
		result.Headshot = head;
		result.BodyDamageAmount = points;
		return result;
	}

	// The weapon object the damage came from, for Sandbox's kill feed: the attacker's active weapon.
	static GameObject HaloWeaponName( string weapon, GameObject attacker ) {
		if ( !attacker.IsValid() ) return null;
		return attacker.GetComponentInChildren<BaseInventoryComponent>()?.ActiveItem?.GameObject;
	}

	// The damage effect's instantaneous acceleration pushes a loose body, Halo's world units per second along the blow.
	static void Push( GameObject target, DamageEffectTag tag, DamageEvent e, float points ) {
		var body = target.GetComponentInParent<Rigidbody>();
		if ( !body.IsValid() || !body.MotionEnabled ) return;

		var acceleration = tag.DamageInstantaneousAcceleration is { Length: >= 1 } a ? a[0] : 0;
		if ( acceleration <= 0 || e.Direction.LengthSquared < 0.0001f ) return;

		var scale = e.AreaOfEffect ? e.Scale : 1;
		var velocity = e.Direction.Normal * HaloUnits.ToInches( acceleration ) * scale;
		body.ApplyImpulseAt( e.Position, velocity * body.Mass );
	}
}
