using System.Collections.Generic;

namespace Blam.Halo1.Tags;

// Inside the namespace so these win over s&box's global Vector2 when this compiles into the mount.
using Vector2 = System.Numerics.Vector2;

/// <summary>
/// What a projectile does when it hits one material.
/// </summary>
/// <param name="Material">The material type (<see cref="MaterialTypes"/>): responses are indexed by it.</param>
/// <param name="Response">0 disappear, 1 detonate, 2 reflect, 3 overpenetrate, 4 attach.</param>
/// <param name="Effect">Impact effect for the default response.</param>
/// <param name="PotentialResponse">Response used instead when the impact's angle and speed are inside the bounds (and the skip fraction allows).</param>
/// <param name="PotentialEffect">Impact effect for the potential response.</param>
/// <param name="AngleBounds">Impact angle range for the potential response (radians; 0..0 means any).</param>
/// <param name="VelocityBounds">Impact speed range for the potential response (world units per tick; 0..0 means any).</param>
/// <param name="DetonationEffect">Extra effect when the projectile detonates on this material.</param>
public sealed record ProjectileMaterialResponse(
	int Material,
	uint Flags,
	int Response,
	TagEntry Effect,
	int PotentialResponse,
	uint PotentialFlags,
	float SkipFraction,
	Vector2 AngleBounds,
	Vector2 VelocityBounds,
	TagEntry PotentialEffect,
	TagEntry DetonationEffect );

/// <summary>
/// The effects of a <c>proj</c> (projectile) tag.
/// </summary>
/// <remarks>
/// Offsets (after the shared object definition): super detonation 0x18C, detonation effect 0x1AC,
/// detonation started 0x1F4 (made when a grenade lands and starts its fuse), flyby sound 0x204,
/// attached detonation damage 0x214, impact damage 0x224, material responses 0x240 (160 bytes each,
/// one per material type: flags 0x0, response 0x2, effect 0x4, potential response 0x24, its flags
/// 0x26, skip fraction 0x28, angle bounds 0x2C, velocity bounds 0x34, potential effect 0x3C, scale
/// effects by 0x5C, angular noise 0x60, velocity noise 0x64, detonation effect 0x68, initial friction
/// 0x90, maximum distance 0x94, parallel/perpendicular friction 0x98/0x9C). Initial and final
/// velocity 0x1E4/0x1E8 are world units per tick in a built map.
/// </remarks>
public sealed class ProjectileTag
{
	public static readonly TagClass Class = new( "proj" );

	public TagEntry SuperDetonation { get; }
	public TagEntry Detonation { get; }
	public TagEntry DetonationStarted { get; }
	public TagEntry FlybySound { get; }
	public IReadOnlyList<ProjectileMaterialResponse> MaterialResponses { get; }

	/// <summary>
	/// Speed when fired, world units per second.
	/// </summary>
	public float InitialVelocity { get; }

	public ProjectileTag( CacheFile cache, TagEntry tag )
	{
		var data = cache.TagData;
		var a = tag.Address;
		SuperDetonation = cache.GetTagReference( data, a + 0x18C );
		Detonation = cache.GetTagReference( data, a + 0x1AC );
		DetonationStarted = cache.GetTagReference( data, a + 0x1F4 );
		FlybySound = cache.GetTagReference( data, a + 0x204 );
		InitialVelocity = data.F32( a + 0x1E4 ) * 30;

		var responses = new List<ProjectileMaterialResponse>();
		var material = 0;
		foreach ( var r in data.Block( a + 0x240, 0xA0 ) )
		{
			responses.Add( new ProjectileMaterialResponse(
				Material: material++,
				Flags: data.U16( r ),
				Response: data.U16( r + 0x2 ),
				Effect: cache.GetTagReference( data, r + 0x4 ),
				PotentialResponse: data.U16( r + 0x24 ),
				PotentialFlags: data.U16( r + 0x26 ),
				SkipFraction: data.F32( r + 0x28 ),
				AngleBounds: data.Vec2( r + 0x2C ),
				VelocityBounds: data.Vec2( r + 0x34 ),
				PotentialEffect: cache.GetTagReference( data, r + 0x3C ),
				DetonationEffect: cache.GetTagReference( data, r + 0x68 ) ) );
		}
		MaterialResponses = responses;
	}
}

/// <summary>
/// One trigger of a weapon: the projectile it fires and the effects it makes.
/// </summary>
/// <param name="Firing">Firing effects: (fire, misfire, empty) per entry. Most triggers have one.</param>
public sealed record WeaponTrigger( int Index, TagEntry Projectile, TagEntry ChargingEffect, IReadOnlyList<(TagEntry Fire, TagEntry Misfire, TagEntry Empty)> Firing );

/// <summary>
/// The effects of a <c>weap</c> (weapon) tag.
/// </summary>
/// <remarks>
/// Offsets: ready effect 0x33C, overheated 0x374, detonation 0x384 (weapons that blow up),
/// light power-on/off effects 0x420/0x430, triggers 0x4FC (276 bytes each: charging effect 0x5C,
/// projectile 0x94, firing effects 0x108 of 132 bytes: firing effect 0x24, misfire effect 0x34,
/// empty effect 0x44, then firing/misfire/empty damage 0x54/0x64/0x74). Firing effects are made
/// at the weapon's markers named by the effect's locations ("primary trigger", "primary ejection"...).
/// </remarks>
public sealed class WeaponEffectsTag
{
	public static readonly TagClass Class = new( "weap" );

	public TagEntry Ready { get; }
	public TagEntry Overheated { get; }
	public TagEntry Detonation { get; }
	public IReadOnlyList<WeaponTrigger> Triggers { get; }

	public WeaponEffectsTag( CacheFile cache, TagEntry tag )
	{
		var data = cache.TagData;
		var a = tag.Address;
		Ready = cache.GetTagReference( data, a + 0x33C );
		Overheated = cache.GetTagReference( data, a + 0x374 );
		Detonation = cache.GetTagReference( data, a + 0x384 );

		var triggers = new List<WeaponTrigger>();
		foreach ( var t in data.Block( a + 0x4FC, 0x114 ) )
		{
			var firing = new List<(TagEntry, TagEntry, TagEntry)>();
			foreach ( var f in data.Block( t + 0x108, 0x84 ) )
				firing.Add( (cache.GetTagReference( data, f + 0x24 ), cache.GetTagReference( data, f + 0x34 ), cache.GetTagReference( data, f + 0x44 )) );

			triggers.Add( new WeaponTrigger( triggers.Count, cache.GetTagReference( data, t + 0x94 ), cache.GetTagReference( data, t + 0x5C ), firing ) );
		}
		Triggers = triggers;
	}
}

/// <summary>
/// The damage effects of an object's collision model (<c>coll</c>, referenced at object 0x70):
/// shield hits, shields failing and recharging, and body damage.
/// </summary>
/// <remarks>
/// Offsets in the collision model: localized damage effect 0x70, area damage effect 0x84, body
/// damaged effect 0x98 (below the body damaged threshold 0x94), body depleted 0xA8, body destroyed
/// 0xBC, shield damaged effect 0x188 (made where a hit lands on the shield, above the threshold at
/// 0x184), shield depleted 0x198, shield recharging 0x1A8, shield material type 0xD2.
/// </remarks>
public sealed class CollisionModelEffects
{
	public static readonly TagClass Class = new( "coll" );

	/// <summary>
	/// The effects by the names their prefabs use.
	/// </summary>
	public IReadOnlyList<(string Name, TagEntry Effect)> Effects { get; }

	public int ShieldMaterial { get; }

	public CollisionModelEffects( CacheFile cache, TagEntry collisionModel )
	{
		var data = cache.TagData;
		var a = collisionModel.Address;
		var list = new List<(string, TagEntry)>();
		void Add( string name, uint offset )
		{
			var effect = cache.GetTagReference( data, a + offset );
			if ( effect is not null && effect.Class == EffectTagClasses.Effect )
				list.Add( (name, effect) );
		}

		Add( "localized_damage", 0x70 );
		Add( "area_damage", 0x84 );
		Add( "body_damaged", 0x98 );
		Add( "body_depleted", 0xA8 );
		Add( "body_destroyed", 0xBC );
		Add( "shield_damaged", 0x188 );
		Add( "shield_depleted", 0x198 );
		Add( "shield_recharging", 0x1A8 );
		Effects = list;
		ShieldMaterial = data.I16( a + 0xD2 );
	}

	/// <summary>
	/// The collision model an object definition uses, or null.
	/// </summary>
	public static TagEntry Of( CacheFile cache, TagEntry objectTag )
	{
		var tag = cache.GetTagReference( cache.TagData, objectTag.Address + 0x70 );
		return tag is not null && tag.Class == Class ? tag : null;
	}
}
