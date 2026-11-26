using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Blam.Unreal;

namespace Blam.Halo1;

/// <summary>
/// The texture set a skin draws one Halo bitmap's surfaces with: paths inside MCC's pak of the
/// texture packages (<c>.uasset</c>), null where the skin has none. All in the layout of the Halo
/// bitmap they replace, at four times its size.
/// </summary>
/// <param name="Diffuse">Base color, display values like Halo's (MCC's material raises it to 2.2).</param>
/// <param name="Multipurpose">Halo's multipurpose map for this skin, in the Halo bitmap's channel order.</param>
public sealed record WeaponSkinMaps( string Diffuse, string Metallic, string Roughness, string Normal, string Multipurpose, string Emissive, string AmbientOcclusion )
{
	/// <summary>
	/// The diffuse map of the weapon's default skin for the same bitmap (MCC's remastered default
	/// look), which every skin of the weapon is calibrated against; null if it has none.
	/// </summary>
	public string DefaultDiffuse { get; init; }
}

/// <summary>
/// One of MCC's Halo CE weapon skins (cosmetics), by the Halo bitmaps it replaces.
/// </summary>
/// <param name="Weapon">MCC's name for the weapon, e.g. <c>PISTOL</c>.</param>
/// <param name="Id">Lower case, e.g. <c>golden</c>, <c>blackcherry</c>; <c>default</c> is MCC's remastered default look.</param>
/// <param name="Name">The name MCC shows, e.g. "Black Cherry".</param>
/// <param name="Maps">By Halo bitmap tag path (backslashes, lower case).</param>
public sealed record WeaponSkin( string Weapon, string Id, string Name, IReadOnlyDictionary<string, WeaponSkinMaps> Maps );

/// <summary>
/// Finds MCC's Halo CE weapon skins in its Unreal Engine pak (<c>mcc/Content/Paks/MCC-WindowsNoEditor.pak</c>).
/// </summary>
/// <remarks>
/// MCC's customization screen shows each skin on the weapon with a physically based material
/// (<c>M_H1_Weapon</c>: diffuse, metallic, roughness and normal maps, 2048 square), from a material
/// instance per skin: <c>Characters/H1/Weapons/Skins/MI_H1S2_WS_&lt;WEAPON&gt;_&lt;SKIN&gt;</c>. The
/// textures are in <c>SharedTextures/H1/Weapons</c>, laid out like the Halo bitmaps the first person
/// models draw, with a multipurpose map for Halo's
/// shader and sometimes an emissive map next to them, by name. The fuel rod gun's instances are
/// for its world model; its first person textures are <c>H1S2_WS_FUELRODGUN_FP_&lt;SKIN&gt;</c>.
/// Parts drawn by other bitmaps (the rocket launcher's decal, the fuel rod gun's metal, the
/// shotgun's shells) have their own sets, per skin or one for all.
///
/// Only the season 2 skins are there; later seasons' skins aren't in the pak.
/// </remarks>
public static class WeaponSkinCatalog
{
	const string Root = "../../../MCC/Content/UI/Features/Customization/";
	const string InstanceFolder = Root + "Characters/H1/Weapons/Skins/";
	const string TextureFolder = Root + "SharedTextures/H1/Weapons/";

	/// <summary>
	/// The Halo bitmap each weapon's skin texture sets replace, and the set names to try for a skin
	/// (lower in the list as fallbacks), <c>{0}</c> being the skin. The weapon's main bitmap takes
	/// the skin's material instance first.
	/// </summary>
	static readonly (string Weapon, string Bitmap, bool Main, string[] Sets)[] Parts =
	[
		("ASSAULTRIFLE", @"weapons\assault rifle\fp\bitmaps\assault rifle_fp", true, []),
		("PISTOL", @"weapons\pistol\fp\bitmaps\pistol", true, []),
		("PLASMAPISTOL", @"weapons\plasma pistol\bitmaps\plasma pistol", true, []),
		("PLASMARIFLE", @"weapons\plasma rifle\fp\bitmaps\plasma rifle_fp", true, []),
		("SHOTGUN", @"weapons\shotgun\bitmaps\shotgun", true, []),
		("SHOTGUN", @"weapons\shotgun\bitmaps\shotshell", false, ["H1S2_WS_SHOTGUN_SHOTSHELL_{0}", "H1S2_WS_SHOTGUN_SHOTSHELL_DEFAULT"]),
		("ROCKETLAUNCHER", @"weapons\rocket launcher\bitmaps\rocket launcher", true, []),
		("ROCKETLAUNCHER", @"weapons\rocket launcher\bitmaps\rocket launcher decal", false,
			["H1S2_WS_ROCKETLAUNCHER_{0}__DECAL", "H1S2_WS_ROCKETLAUNCHER_{0}_DECAL", "H1S2_WS_ROCKETLAUNCHER_DECALS_{0}", "H1S2_WS_ROCKETLAUNCHER__DECAL"]),
		("SNIPERRIFLE", @"weapons\sniper rifle\bitmaps\sniper rifle", true, []),
		("FLAMETHROWER", @"weapons\flamethrower\bitmaps\flamethrower", true, []),
		("NEEDLER", @"weapons\needler\bitmaps\needler", true, []),
		("FUELRODGUN", @"weapons\plasma_cannon\fp\bitmaps\fp_plasmacannon", false, ["H1S2_WS_FUELRODGUN_FP_{0}"]),
		("FUELRODGUN", @"weapons\plasma_cannon\fp\bitmaps\fp_plasmacannon_metal", false, ["H1S2_WS_FUELRODGUN_FP_METAL_{0}", "H1S2_WS_FUELRODGUN_FP_METAL_DEFAULT"]),
	];

	/// <summary>
	/// Names MCC writes as several words (its localized names: "Magnum Skin - BLACK CHERRY").
	/// </summary>
	static readonly Dictionary<string, string> Names = new( StringComparer.OrdinalIgnoreCase )
	{
		["default"] = "Default (HD)",
		["blackcherry"] = "Black Cherry",
		["greatjourney"] = "Great Journey",
		["hazardpay"] = "Hazard Pay",
		["toysoldier"] = "Toy Soldier",
		["luckyshot"] = "Lucky Shot",
		["huntersblood"] = "Hunter's Blood",
		["dearjohn"] = "Dear John",
		["hotpepper"] = "Hot Pepper",
		["whitehot"] = "White Hot",
	};

	static readonly Regex InstanceName = new( @"^MI_H1S\d+_WS_([A-Z]+)_([A-Z]+)$", RegexOptions.IgnoreCase );

	/// <summary>
	/// Every skin in the pak with at least one texture set found, by weapon then name, each
	/// weapon's default first.
	/// </summary>
	public static List<WeaponSkin> Read( UnrealPak pak )
	{
		// Texture packages by name, e.g. H1S2_WS_PISTOL_GOLDEN_METALLIC.
		var textures = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase );
		foreach ( var entry in pak.Entries )
		{
			if ( entry.Path.StartsWith( TextureFolder, StringComparison.OrdinalIgnoreCase ) && entry.Path.EndsWith( ".uasset", StringComparison.OrdinalIgnoreCase ) )
				textures[Path.GetFileNameWithoutExtension( entry.Path )] = entry.Path;
		}

		var skins = new List<WeaponSkin>();
		foreach ( var entry in pak.Entries.Where( e => e.Path.StartsWith( InstanceFolder, StringComparison.OrdinalIgnoreCase ) && e.Path.EndsWith( ".uasset", StringComparison.OrdinalIgnoreCase ) ) )
		{
			// Only MI_H1S2_WS_<WEAPON>_<SKIN>: names with more after the skin are parts (DEFAULT_HOLO).
			var match = InstanceName.Match( Path.GetFileNameWithoutExtension( entry.Path ) );
			if ( !match.Success )
				continue;

			var weapon = match.Groups[1].Value.ToUpperInvariant();
			var skin = match.Groups[2].Value.ToUpperInvariant();
			var instance = ReadInstance( pak, entry );
			if ( instance is null || instance.Parent != "MI_H1_Weapon" )
				continue;

			var maps = new Dictionary<string, WeaponSkinMaps>( StringComparer.OrdinalIgnoreCase );
			foreach ( var part in Parts.Where( p => p.Weapon == weapon ) )
			{
				var set = part.Main ? MapsFromInstance( instance, textures ) : null;
				foreach ( var pattern in part.Sets )
				{
					set ??= MapsFromName( string.Format( CultureInfo.InvariantCulture, pattern, skin ), textures );
					if ( set is not null )
						break;
				}

				if ( set is not null )
					maps[part.Bitmap] = set;
			}

			if ( maps.Count == 0 )
				continue;

			var id = skin.ToLowerInvariant();
			skins.Add( new WeaponSkin( weapon, id, Names.GetValueOrDefault( id ) ?? CultureInfo.InvariantCulture.TextInfo.ToTitleCase( id ), maps ) );
		}

		// Every skin's parts know the weapon's default diffuse map for the same bitmap.
		var defaults = skins.Where( s => s.Id == "default" ).ToDictionary( s => s.Weapon );
		skins = skins.Select( s => s with
		{
			Maps = s.Maps.ToDictionary( x => x.Key, x => x.Value with
			{
				DefaultDiffuse = defaults.TryGetValue( s.Weapon, out var d ) && d.Maps.TryGetValue( x.Key, out var m ) ? m.Diffuse : null,
			}, StringComparer.OrdinalIgnoreCase ),
		} ).ToList();

		return skins.OrderBy( s => s.Weapon ).ThenBy( s => s.Id != "default" ).ThenBy( s => s.Name ).ToList();
	}

	sealed record Instance( string Parent, Dictionary<string, string> Textures );

	/// <summary>
	/// A material instance's parent and texture parameters (parameter name to texture name).
	/// </summary>
	static Instance ReadInstance( UnrealPak pak, UnrealPakEntry entry )
	{
		var package = ReadPackage( pak, entry.Path );
		var export = package?.FindExport( "MaterialInstanceConstant" );
		if ( export is null )
			return null;

		var properties = package.ReadProperties( export, out _ );
		var textures = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase );
		foreach ( var value in properties.Get<List<object>>( "TextureParameterValues" ) ?? [] )
		{
			if ( value is UnrealProperties parameter && parameter.Get<UnrealProperties>( "ParameterInfo" )?.Get<string>( "Name" ) is { } name && parameter.Get<string>( "ParameterValue" ) is { } texture )
				textures[name] = texture;
		}

		return new Instance( properties.Get<string>( "Parent" ), textures );
	}

	/// <summary>
	/// Reads a package from the pak: its .uasset and the .uexp beside it. Null if either is missing.
	/// </summary>
	public static UnrealPackage ReadPackage( UnrealPak pak, string uassetPath )
	{
		var header = pak.Find( uassetPath );
		var exports = pak.Find( Path.ChangeExtension( uassetPath, ".uexp" ) );
		return header is null || exports is null ? null : new UnrealPackage( pak.Read( header ), pak.Read( exports ) );
	}

	static WeaponSkinMaps MapsFromInstance( Instance instance, Dictionary<string, string> textures )
	{
		var diffuse = instance.Textures.GetValueOrDefault( "Diffuse" );
		if ( diffuse is null || !textures.TryGetValue( diffuse, out var diffusePath ) )
			return null;

		var weaponSet = Regex.Match( diffuse, @"^H1S\d+_WS_[A-Z]+", RegexOptions.IgnoreCase ).Value;
		return new WeaponSkinMaps(
			diffusePath,
			Texture( instance.Textures.GetValueOrDefault( "Metallic" ), textures ) ?? Find( textures, diffuse, "_METALLIC" ),
			Texture( instance.Textures.GetValueOrDefault( "Roughness" ), textures ) ?? Find( textures, diffuse, "_ROUGHNESS" ),
			Texture( instance.Textures.GetValueOrDefault( "Normal" ), textures ) ?? Find( textures, diffuse, "_NORMAL" ),
			Find( textures, diffuse, "_MULTIPURPOSE" ) ?? Find( textures, weaponSet, "_MULTIPURPOSE" ),
			Find( textures, diffuse, "_EMISSIVE" ),
			Find( textures, diffuse, "_AO" ) );
	}

	static WeaponSkinMaps MapsFromName( string set, Dictionary<string, string> textures )
	{
		if ( !textures.TryGetValue( set, out var diffusePath ) )
			return null;

		return new WeaponSkinMaps( diffusePath, Find( textures, set, "_METALLIC" ), Find( textures, set, "_ROUGHNESS" ), Find( textures, set, "_NORMAL" ),
			Find( textures, set, "_MULTIPURPOSE" ) ?? Find( textures, set, "_FP_MULTIPURPOSE" ), Find( textures, set, "_EMISSIVE" ), Find( textures, set, "_AO" ) );
	}

	static string Texture( string name, Dictionary<string, string> textures ) =>
		name is not null && textures.TryGetValue( name, out var path ) ? path : null;

	static string Find( Dictionary<string, string> textures, string set, string suffix ) =>
		textures.TryGetValue( set + suffix, out var path ) ? path : null;
}
