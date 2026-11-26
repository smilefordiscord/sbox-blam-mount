using System;
using System.Runtime.CompilerServices;
using Halo2.Data;

namespace Halo2.Combat;

// A Sandbox player or NPC as a Halo 2 unit, standing in for halo2sbox's Spartan.
// Sandbox's 100 health is Halo 2's multiplayer Spartan's shield and body together: each hit runs Halo 2's damage rules
// (shield first, its material and table multipliers, headshots on an empty shield) on a vitality made from that health.
public sealed class Halo2Unit : IValid {
	static readonly ConditionalWeakTable<GameObject, Halo2Unit> Units = new();

	readonly Component _owner;

	Halo2Unit( Component owner ) => _owner = owner;

	// The unit for a player's or NPC's object or anything under it, else null.
	public static Halo2Unit For( GameObject go ) {
		if ( !go.IsValid() ) return null;
		Component owner = go.GetComponentInParent<PlayerController>( true );
		owner ??= go.GetComponentInParent<Sandbox.Npcs.BaseNpc>( true );
		if ( owner is null ) return null;
		return Units.GetValue( owner.GameObject, _ => new Halo2Unit( owner ) );
	}

	public static Halo2Unit Find( Guid id ) => id == Guid.Empty ? null : For( Game.ActiveScene?.Directory.FindByGuid( id ) );

	// The local player, who hears flybys.
	public static Halo2Unit Local => PlayerController.FindLocalPlayer() is { } p ? For( p.GameObject ) : null;

	// Every living player and NPC in the scene.
	public static IEnumerable<Halo2Unit> All( Scene scene ) {
		if ( scene is null ) yield break;
		foreach ( var p in scene.GetAllComponents<PlayerController>() ) if ( For( p.GameObject ) is { IsAlive: true } u ) yield return u;
		foreach ( var n in scene.GetAllComponents<Sandbox.Npcs.BaseNpc>() ) if ( For( n.GameObject ) is { IsAlive: true } u ) yield return u;
	}

	public bool IsValid => _owner.IsValid();
	public GameObject GameObject => _owner.GameObject;
	public Scene Scene => _owner.Scene;
	public PlayerController Player => _owner as PlayerController;

	public bool IsAlive => _owner switch {
		PlayerController p => p.Health > 0,
		Sandbox.Npcs.BaseNpc n => n.Health > 0,
		_ => false,
	};

	public bool IsLocalPlayer => Player is { } p && !p.IsProxy;

	public Vector3 WorldPosition => GameObject.WorldPosition;
	public Transform Root => new( GameObject.WorldPosition, Rotation.FromYaw( GameObject.WorldRotation.Yaw() ) );

	public Vector3 EyePosition => Player is { } p ? p.EyeTransform.Position : GameObject.WorldPosition + Vector3.Up * 64;
	public Angles EyeAngles => Player?.Controller?.EyeAngles ?? GameObject.WorldRotation.Angles();

	public Vector3 MoveVelocity => Player?.Controller?.Velocity ?? GameObject.GetComponent<Rigidbody>()?.Velocity ?? Vector3.Zero;

	public SkinnedModelRenderer Renderer => Player is { } p ? p.Controller?.Renderer : GameObject.GetComponentInChildren<SkinnedModelRenderer>();

	// The left hand, where a grenade sits during a throw.
	public GameObject LeftHand => Renderer is { } r && r.IsValid() ? r.GetBoneObject( "hand_L" ) ?? GameObject : GameObject;

	// The held Halo 2 weapon's muzzle, null without one.
	public Vector3? HeldMuzzle => GameObject.GetComponentsInChildren<HaloWeapons.Halo2Weapon>().FirstOrDefault( w => w.Active )?.HeldMuzzle;

	// The centre of its body, where homing shots steer and area damage measures to.
	public Vector3 Center => Renderer is { } r && r.IsValid() ? r.Bounds.Center : GameObject.WorldPosition + Vector3.Up * 36;
	public Vector3 Head => EyePosition;

	// What a shot strikes before the damage says otherwise: the Spartan's indirect material.
	public int HitMaterial => HaloSurfaces.MaterialIndex( HaloDataStore.SpartanDamage?.GlobalIndirectMaterialName );

	float Health {
		get => _owner switch { PlayerController p => p.Health, Sandbox.Npcs.BaseNpc n => n.Health, _ => 0 };
	}

	float MaximumHealth => _owner switch { PlayerController p => p.MaxHealth > 0 ? p.MaxHealth : 100, _ => 100 };

	// Host only.
	public DamageResult TakeDamage( DamageEvent e ) {
		var game = HaloDataStore.Game;
		var tag = game?.DamageEffect( e.DamageEffect );
		var model = game?.SpartanModel;
		var info = model?.DamageInfo;
		if ( tag is null || info is null || !IsAlive ) return default;

		var shieldMaximum = MathF.Max( 0, info.MaximumShieldVitality );
		var bodyMaximum = MathF.Max( 1, info.MaximumVitality );
		var total = shieldMaximum + bodyMaximum;
		var points = Math.Clamp( Health / MaximumHealth, 0, 1 ) * total;
		var vitality = new Vitality {
			Shield = shieldMaximum > 0 ? MathF.Max( 0, points - bodyMaximum ) / shieldMaximum : 0,
			Body = MathF.Min( points, bodyMaximum ) / bodyMaximum,
		};
		vitality.ShieldDepleted = vitality.Shield <= 0;

		var result = HaloDamage.Apply( game.Globals, tag, model, e, ref vitality, 1, false );
		if ( !result.Dealt ) return result;

		var left = MathF.Max( 0, vitality.Shield ) * shieldMaximum + MathF.Max( 0, vitality.Body ) * bodyMaximum;
		var dealt = (points - left) / total * MaximumHealth;
		if ( result.Killed ) dealt = MathF.Max( dealt, Health + 1 );
		if ( dealt > 0 ) {
			var info2 = Halo2Damage.Info( dealt, e, tag );
			if ( result.Headshot ) info2.Tags.Add( "head" );
			GameObject.GetComponentInParent<Component.IDamageable>()?.OnDamage( info2 );
		}
		Halo2Damage.Push( GameObject, tag, e );
		return result;
	}
}

// halo2sbox rewinds shots to what the shooter saw; Sandbox has no lag compensation, so every test is against the present.
public sealed class RewindContext {
	public static readonly RewindContext None = new();
}

// Where a ray met a unit's body.
public struct HitboxHit {
	public float Distance;
	public Vector3 Position;
	public Vector3 Normal;
	// The model material struck, "head" for a head hit box, else "body".
	public string Material;
	public string Node;
}

public struct UnitHit {
	public Halo2Unit Unit;
	public HitboxHit Hit;
	public Transform TestedRoot;
	public GameObject Object;
}

// Rays against Sandbox players' and NPCs' hit boxes.
public static class UnitTrace {
	public static SceneTrace Trace( Scene scene, Vector3 start, Vector3 end ) => scene.Trace.Ray( start, end )
		.UseHitboxes()
		.WithoutTags( "playercontroller", "trigger", "projectile", "debris", "ragdoll", "viewmodel" );

	// The nearest living unit along a ray, unless the level is in the way.
	public static bool Ray( Scene scene, Vector3 start, Vector3 direction, float distance, RewindContext rewind, Halo2Unit ignore, out UnitHit result ) {
		result = default;
		var trace = Trace( scene, start, start + direction * distance );
		if ( ignore is { IsValid: true } ) trace = trace.IgnoreGameObjectHierarchy( ignore.GameObject );
		var tr = trace.Run();
		if ( !tr.Hit || tr.StartedSolid ) return false;

		var unit = Halo2Unit.For( tr.GameObject );
		if ( unit is null || !unit.IsAlive || unit == ignore ) return false;

		var head = tr.Hitbox?.Tags?.Has( "head" ) == true;
		result = new UnitHit {
			Unit = unit,
			Hit = new HitboxHit { Distance = tr.Distance, Position = tr.HitPosition, Normal = tr.Normal, Material = head ? "head" : "body", Node = tr.Hitbox?.Bone?.Name },
			TestedRoot = unit.Root,
			Object = tr.GameObject,
		};
		return true;
	}

	public static Vector3 Center( Halo2Unit unit, RewindContext rewind ) => unit?.Center ?? Vector3.Zero;
}

// Halo 2 damage reaching Sandbox: its damage info, and its push on loose bodies.
public static class Halo2Damage {
	public static DamageInfo Info( float health, in DamageEvent e, DamageEffectTag tag ) {
		var attacker = Game.ActiveScene?.Directory.FindByGuid( e.AttackerId );
		var weapon = attacker.IsValid() ? attacker.GetComponentInChildren<BaseInventoryComponent>()?.ActiveItem?.GameObject : null;
		var info = new DamageInfo( health, attacker, weapon ) { Position = e.Position, Origin = e.Position - e.Direction * 32 };
		if ( e.AreaOfEffect ) {
			info.Tags.Add( "explosion" );
		}
		if ( tag?.GeneralDamage is { Length: > 0 } category ) info.Tags.Add( $"halo2_{category.Replace( ' ', '_' )}" );
		return info;
	}

	// The damage effect's instantaneous acceleration, world units per second along the blow, scaled like area damage.
	public static void Push( GameObject target, DamageEffectTag tag, in DamageEvent e ) {
		var body = target?.GetComponentInParent<Rigidbody>();
		if ( !body.IsValid() || !body.MotionEnabled || tag is null ) return;
		var acceleration = tag.InstantaneousAcceleration;
		if ( acceleration <= 0 || e.Direction.LengthSquared < 0.0001f ) return;
		var scale = e.AreaOfEffect ? e.Scale : 1;
		body.ApplyImpulseAt( e.Position, e.Direction.Normal * HaloUnits.ToInches( acceleration ) * scale * body.Mass );
	}

	// Anything that isn't a unit: Sandbox props and other damageable objects take Halo 2's raw damage, two thirds per point as for CE.
	public static void Object( GameObject target, in DamageEvent e ) {
		if ( !target.IsValid() || Halo2Unit.For( target ) is not null ) return;
		var tag = HaloDataStore.Game?.DamageEffect( e.DamageEffect );
		if ( tag is null ) return;
		var random = new HaloRandom( e.Seed );
		var points = HaloDamage.Amount( tag, e.Scale, ref random );
		if ( points > 0 ) target.GetComponentInParent<Component.IDamageable>()?.OnDamage( Info( points * (100f / 150f), e, tag ) );
		Push( target, tag, e );
	}
}

// halo2sbox's physics world in a Sandbox map: a shot at the level damages and pushes what it hit.
public static class HaloPhysics {
	public static void HostProjectileHit( Scene scene, GameObject hit, Vector3 position, Vector3 direction, DamageEvent e, int material ) => Halo2Damage.Object( hit, e );

	public static void HostMeleeHit( Scene scene, GameObject hit, Vector3 position, Vector3 direction, DamageEvent e, int material ) {
		if ( !hit.IsValid() ) return;
		e.Position = hit.WorldPosition;
		Halo2Damage.Object( hit, e );
	}

	// Every prop within the blast's cutoff takes its damage and push by distance, as Halo 2's objects do; the source is left out.
	public static void HostAreaDamage( Scene scene, DamageEffectTag tag, Vector3 position, DamageEvent e, GameObject source ) {
		var cutoff = HaloUnits.ToInches( tag.CutoffRadius );
		if ( cutoff <= 0 ) return;
		var done = new HashSet<GameObject>();
		foreach ( var collider in scene.FindInPhysics( new Sphere( position, cutoff ) ) ) {
			var go = collider.GetComponentInParent<Rigidbody>()?.GameObject ?? (collider.GetComponentInParent<Component.IDamageable>() as Component)?.GameObject;
			if ( !go.IsValid() || go == source || !done.Add( go ) || Halo2Unit.For( go ) is not null ) continue;
			var closest = go.GetComponent<Rigidbody>() is { PhysicsBody: { } physics } ? physics.FindClosestPoint( position ) : go.WorldPosition;
			var distance = HaloUnits.ToWorldUnits( closest.Distance( position ) );
			if ( distance > tag.CutoffRadius ) continue;
			var away = go.WorldPosition - position;
			var blast = e;
			blast.Scale = HaloDamage.AreaScale( tag, distance );
			blast.Position = closest;
			blast.Direction = away.LengthSquared > 0.01f ? away.Normal : Vector3.Up;
			blast.AreaOfEffect = true;
			Halo2Damage.Object( go, blast );
		}
	}
}

// Where a shot or melee starts: the shooter's eyes, feet and velocity at that tick.
public readonly record struct ShotFrame( Vector3 Eye, Vector3 Feet, Vector3 Velocity, int AirborneTicks, int MeleeEta );

// The host's broadcasts for Halo 2 shots: a damaging detonation plays only from the host, as in halo2sbox.
public static class Halo2Net {
	[Rpc.Broadcast( NetFlags.HostOnly )]
	public static void Effect( string effect, string sound, Vector3 position, Vector3 normal, int shooter, int tick, int index ) {
		if ( effect is not null ) HaloEffects.Play( effect, position, normal );
		HaloSounds.Play( sound, position );
		Game.ActiveScene?.GetSystem<ProjectileSimulation>()?.HostDetonated( new ProjectileKey( shooter, tick, index ) );
	}
}
