using System.Collections.Generic;

namespace Blam.Halo1;

/// <summary>
/// Halo CE's material types: what a surface is made of, for footsteps, bullet impacts and
/// projectile responses. Every shader has one (shader base 0x22), and collision geometry gets
/// it through the shader of its collision material.
/// </summary>
public enum MaterialType
{
	Dirt,
	Sand,
	Stone,
	Snow,
	Wood,
	MetalHollow,
	MetalThin,
	MetalThick,
	Rubber,
	Glass,
	ForceField,
	Grunt,
	HunterArmor,
	HunterSkin,
	Elite,
	Jackal,
	JackalEnergyShield,
	EngineerSkin,
	EngineerForceField,
	FloodCombatForm,
	FloodCarrierForm,
	CyborgArmor,
	CyborgEnergyShield,
	HumanArmor,
	HumanSkin,
	Sentinel,
	Monitor,
	Plastic,
	Water,
	Leaves,
	EliteEnergyShield,
	Ice,
	HunterShield,
}

public static class MaterialTypes
{
	/// <summary>
	/// How many material types there are.
	/// </summary>
	public const int Count = (int)MaterialType.HunterShield + 1;

	/// <summary>
	/// Halo's own names, lowercased with underscores for spaces: <c>dirt</c>, <c>metal_thin</c>,
	/// <c>jackal_energy_shield</c>...
	/// </summary>
	public static readonly IReadOnlyList<string> Names =
	[
		"dirt", "sand", "stone", "snow", "wood", "metal_hollow", "metal_thin", "metal_thick", "rubber", "glass",
		"force_field", "grunt", "hunter_armor", "hunter_skin", "elite", "jackal", "jackal_energy_shield",
		"engineer_skin", "engineer_force_field", "flood_combat_form", "flood_carrier_form", "cyborg_armor",
		"cyborg_energy_shield", "human_armor", "human_skin", "sentinel", "monitor", "plastic", "water", "leaves",
		"elite_energy_shield", "ice", "hunter_shield",
	];

	public static string Name( MaterialType type ) => Name( (int)type );

	/// <summary>
	/// The name of a material type, or null for none (-1) or an unknown index.
	/// </summary>
	public static string Name( int type ) => IsValid( type ) ? Names[type] : null;

	/// <summary>
	/// The tag a material type is given in s&amp;box ("halo_dirt", "halo_metal_thin"), or null for none.
	/// </summary>
	public static string Tag( int type ) => Name( type ) is { } name ? $"halo_{name}" : null;

	public static bool IsValid( int type ) => type >= 0 && type < Count;

	/// <summary>
	/// A shader's material type, or null if it isn't a shader (or the value is out of range).
	/// </summary>
	public static MaterialType? OfShader( CacheFile cache, TagEntry shader )
	{
		if ( shader is null || shader.Address == 0 )
			return null;

		var type = cache.TagData.I16( shader.Address + 0x22 );
		return IsValid( type ) ? (MaterialType)type : null;
	}
}
