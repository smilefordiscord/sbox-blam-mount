using System.Collections.Generic;
using System.Linq;
using Blam.Halo2.TagData;
using Blam.Halo2.Tags;

namespace Blam.Halo2;

// A role alias: its name (tag path with forward slashes, "__" and the role) and the effect or sound it stands for.
// FirstPerson marks the effect's first person view.
public sealed record EffectRole( string Name, TagEntry Tag, bool FirstPerson = false );

// The effects and sounds an object plays in its roles, so a game can ask for "the effect for X" without reading tags.
// Names follow Halo CE's aliases where the meaning matches (README, Halo 2 section).
public static class EffectRoles
{
	// Material effects entries 15 on are the projectile responses in this order, going by what the maps put in them.
	const int FirstResponseEntry = 15;

	static readonly string[] Responses = ["impact", "fizzle", "overpenetrate", "attach", "bounce", "bounce_dud", "fizzle_ricochet"];

	static readonly TagClass MaterialEffectsClass = new( "foot" );

	// The object's roles: its weapon, item and projectile effects, and its model's shield and damage section effects.
	public static List<EffectRole> For( TagEntry objectTag )
	{
		var roles = new List<EffectRole>();
		if ( !ObjectTag.IsObject( objectTag ) )
			return roles;

		var stem = objectTag.Path.Replace( '\\', '/' );

		// Effects held or worn in first person also get the first person view, where their particle systems differ.
		void Add( string role, TagEntry target, bool firstPerson = false )
		{
			if ( !EffectTag.IsEffect( target ) && !IsSound( target ) )
				return;

			roles.Add( new EffectRole( $"{stem}__{role}", target ) );
			if ( firstPerson && EffectTag.IsEffect( target ) && new EffectTag( target ).DiffersInFirstPerson )
				roles.Add( new EffectRole( $"{stem}__{role}_fp", target, FirstPerson: true ) );
		}

		var own = new Fields( objectTag, objectTag.Address );
		var type = new ObjectTag( objectTag ).Type;
		if ( type is ObjectType.Weapon or ObjectType.Equipment or ObjectType.Garbage )
		{
			Add( "item_detonating", own.Reference( TagLayouts.Item, "detonating effect" ), true );
			Add( "item_detonation", own.Reference( TagLayouts.Item, "detonation effect" ), true );
		}

		if ( type == ObjectType.Weapon )
		{
			var weapon = TagLayouts.Weapon;
			Add( "ready", own.Reference( weapon, "ready effect" ), true );
			Add( "overheated", own.Reference( weapon, "overheated" ), true );
			Add( "detonation", own.Reference( weapon, "detonation" ), true );
			Add( "power_on", own.Reference( weapon, "weapon power-on effect" ), true );
			Add( "power_off", own.Reference( weapon, "weapon power-off effect" ), true );
			Add( "age", own.Reference( weapon, "age effect" ), true );

			foreach ( var (i, trigger, layout) in own.Elements( weapon, "new triggers" ) )
				Add( $"trigger{i}_charging", trigger.Reference( layout, "charging effect" ), true );

			foreach ( var (i, magazine, layout) in own.Elements( weapon, "magazines" ) )
			{
				Add( $"magazine{i}_reloading", magazine.Reference( layout, "reloading effect" ), true );
				Add( $"magazine{i}_chambering", magazine.Reference( layout, "chambering effect" ), true );
			}

			// A barrel's firing effects after the first get a number; Halo picks one by the shots fired, or at random.
			foreach ( var (b, barrel, barrelLayout) in own.Elements( weapon, "barrels" ) )
			{
				foreach ( var (i, firing, layout) in barrel.Elements( barrelLayout, "firing effects" ) )
				{
					var suffix = i == 0 ? "" : $"_{i}";
					Add( $"barrel{b}_fire{suffix}", firing.Reference( layout, "firing effect" ), true );
					Add( $"barrel{b}_misfire{suffix}", firing.Reference( layout, "misfire effect" ), true );
					Add( $"barrel{b}_empty{suffix}", firing.Reference( layout, "empty effect" ), true );
				}
			}
		}

		if ( type == ObjectType.Projectile )
		{
			var projectile = TagLayouts.Projectile;
			Add( "detonation_started", own.Reference( projectile, "detonation started" ) );
			Add( "detonation_airborne", own.Reference( projectile, "detonation effect (airborne)" ) );
			Add( "detonation_ground", own.Reference( projectile, "detonation effect (ground)" ) );
			Add( "super_detonation", own.Reference( projectile, "super detonation" ) );
			Add( "detonation_sound", own.Reference( projectile, "detonation sound" ) );
			Add( "flyby", own.Reference( projectile, "flyby sound" ) );
			Add( "impact", own.Reference( projectile, "impact effect" ) );

			// What each response plays on each global material, from the projectile's material effects.
			// That's an effect and a sound, each with a secondary.
			if ( own.Reference( TagLayouts.ObjectDefinition, "material effects" ) is { Address: not 0 } foot && foot.Class == MaterialEffectsClass )
			{
				foreach ( var (entry, effect, layout) in new Fields( foot, foot.Address ).Elements( TagLayouts.MaterialEffects, "effects" ) )
				{
					var response = entry - FirstResponseEntry;
					if ( response < 0 || response >= Responses.Length )
						continue;

					foreach ( var (block, suffix) in new[] { ("effects", ""), ("sounds", "_sound") } )
					{
						foreach ( var (_, material, materialLayout) in effect.Elements( layout, block ) )
						{
							var name = material.Name( materialLayout, "material name" );
							if ( string.IsNullOrEmpty( name ) )
								continue;

							Add( $"{Responses[response]}_{name}{suffix}", material.Reference( materialLayout, "tag" ) );
							Add( $"{Responses[response]}_{name}{suffix}_secondary", material.Reference( materialLayout, "secondary tag" ) );
						}
					}
				}
			}
		}

		// The model's damage info: its shield effects, and the effects its damage sections' instant responses play.
		if ( new ObjectTag( objectTag ).Model is { Address: not 0 } model && model.Class == ModelTag.Class )
		{
			foreach ( var (_, damage, layout) in new Fields( model, model.Address ).Elements( TagLayouts.Model, "new damage info" ).Take( 1 ) )
			{
				Add( "shield_damaged", damage.Reference( layout, "shield damaged effect" ), true );
				Add( "shield_depleted", damage.Reference( layout, "shield depleted effect" ), true );
				Add( "shield_recharging", damage.Reference( layout, "shield recharging effect" ), true );

				foreach ( var (_, section, sectionLayout) in damage.Elements( layout, "damage sections" ) )
				{
					var name = section.Name( sectionLayout, "name" );
					foreach ( var (r, response, responseLayout) in section.Elements( sectionLayout, "instant responses" ) )
					{
						Add( $"section_{name}_response{r}", response.Reference( responseLayout, "transition effect" ), true );
						Add( $"section_{name}_response{r}_delay", response.Reference( responseLayout, "delay effect" ), true );
					}
				}
			}
		}

		return roles;
	}

	// A sound classic Halo 2 plays: MCC's remastered-only sounds (some impacts) have no classic samples.
	public static bool IsSound( TagEntry tag ) => tag is not null && tag.Address != 0 && tag.Class == SoundTag.Class && !new SoundTag( tag ).IsRemasteredOnly;

	// A structure of a tag's data, read through layouts in the cache file holding the tag (shared.map for shared tags).
	readonly record struct Fields( TagEntry Tag, uint Address )
	{
		public TagEntry Reference( TagStruct layout, string field ) => Tag.Cache.GetTagReference( Address + (uint)layout.Field( field ).Offset );

		// A string id as part of a resource name.
		public string Name( TagStruct layout, string field ) =>
			Tag.Cache.GetString( Tag.Cache.TagData.U32( Address + (uint)layout.Field( field ).Offset ) )?.Trim().Replace( ' ', '_' );

		public IEnumerable<(int Index, Fields Element, TagStruct Layout)> Elements( TagStruct layout, string field )
		{
			var block = layout.Field( field );
			var tag = Tag;
			return Tag.Cache.TagData.Elements( Address + (uint)block.Offset, block.Struct.Size ).Select( ( e, i ) => (i, new Fields( tag, e ), block.Struct) ).ToList();
		}
	}
}
