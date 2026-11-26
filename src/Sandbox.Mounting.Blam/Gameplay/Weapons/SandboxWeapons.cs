namespace HaloWeapons;

// Halo's multiplayer weapons as weapon prefabs tagged "weapon", which Sandbox lists under the game's name in its spawn menu and puts in the player's inventory, the way Facepunch's mounts expose theirs.
static class SandboxWeapons
{
	public sealed record Entry( string Tag, string Title, int Slot, int Order, int Value );

	public static readonly Entry[] HaloCe =
	[
		new( "weapons/pistol/pistol.weap", "M6D Pistol", 1, 1, 10 ),
		new( "weapons/plasma pistol/plasma pistol.weap", "Plasma Pistol", 1, 2, 10 ),
		new( "weapons/needler/mp_needler.weap", "Needler", 1, 3, 15 ),
		new( "weapons/assault rifle/assault rifle.weap", "MA5B Assault Rifle", 2, 1, 20 ),
		new( "weapons/plasma rifle/plasma rifle.weap", "Plasma Rifle", 2, 2, 20 ),
		new( "weapons/sniper rifle/sniper rifle.weap", "S2 AM Sniper Rifle", 2, 3, 30 ),
		new( "weapons/shotgun/shotgun.weap", "M90 Shotgun", 3, 1, 25 ),
		new( "weapons/flamethrower/flamethrower.weap", "M7057 Flamethrower", 3, 2, 25 ),
		new( "weapons/rocket launcher/rocket launcher.weap", "M19 SSM Rocket Launcher", 4, 1, 40 ),
		new( "weapons/plasma_cannon/plasma_cannon.weap", "Fuel Rod Gun", 4, 2, 35 ),
	];

	public static readonly Entry[] Halo2 =
	[
		new( "objects/weapons/pistol/magnum/magnum.weap", "M6C Magnum", 1, 11, 10 ),
		new( "objects/weapons/pistol/plasma_pistol/plasma_pistol.weap", "Plasma Pistol", 1, 12, 10 ),
		new( "objects/weapons/pistol/needler/needler.weap", "Needler", 1, 13, 15 ),
		new( "objects/weapons/rifle/smg/smg.weap", "M7 SMG", 2, 11, 15 ),
		new( "objects/weapons/rifle/battle_rifle/battle_rifle.weap", "BR55 Battle Rifle", 2, 12, 20 ),
		new( "objects/weapons/rifle/plasma_rifle/plasma_rifle.weap", "Plasma Rifle", 2, 13, 20 ),
		new( "objects/weapons/rifle/brute_plasma_rifle/brute_plasma_rifle.weap", "Brute Plasma Rifle", 2, 14, 20 ),
		new( "objects/weapons/rifle/covenant_carbine/covenant_carbine.weap", "Covenant Carbine", 2, 15, 25 ),
		new( "objects/weapons/rifle/sniper_rifle/sniper_rifle.weap", "S2 AM Sniper Rifle", 2, 16, 30 ),
		new( "objects/weapons/rifle/beam_rifle/beam_rifle.weap", "Beam Rifle", 2, 17, 30 ),
		new( "objects/weapons/rifle/shotgun/shotgun.weap", "M90 Shotgun", 3, 11, 25 ),
		new( "objects/weapons/melee/energy_blade/energy_blade.weap", "Energy Sword", 3, 12, 30 ),
		new( "objects/weapons/support_low/brute_shot/brute_shot.weap", "Brute Shot", 4, 11, 35 ),
		new( "objects/weapons/support_high/rocket_launcher/rocket_launcher.weap", "M19 SSM Rocket Launcher", 4, 12, 40 ),
		new( "objects/weapons/support_high/flak_cannon/flak_cannon.weap", "Fuel Rod Gun", 4, 13, 35 ),
	];

	// entities/weapons/<the weapon tag's name>.prefab
	public static string PrefabPath( string weaponTag ) => $"entities/weapons/{Name( weaponTag )}.prefab";

	// The tag's path as the cache files name it: backslashes, no class extension.
	public static string CachePath( string weaponTag ) => System.IO.Path.ChangeExtension( weaponTag, null ).Replace( '/', '\\' );

	static string Name( string weaponTag ) => System.IO.Path.GetFileNameWithoutExtension( weaponTag ).Replace( ' ', '_' );

	// A weapon outside the lists (a campaign weapon, the flag, the ball), for game code to spawn by its tag; not listed in spawn menus.
	public static Entry Unlisted( string weaponTag ) => new( weaponTag, Name( weaponTag ).Replace( '_', ' ' ), 2, 99, 0 );

	public static void Register( MountContext context, Entry entry, string previewModel, bool halo2, bool listed = true )
	{
		var loader = new WeaponPrefabLoader( entry, previewModel, halo2 );
		if ( listed ) loader.Tags.Add( "weapon" );
		context.Add( ResourceType.PrefabFile, PrefabPath( entry.Tag ), loader );
	}
}

// One weapon's prefab: a networked object with the weapon component naming its tag; the component loads the rest from the mount's game data when it wakes.
sealed class WeaponPrefabLoader( SandboxWeapons.Entry entry, string previewModel, bool halo2 ) : ResourceLoader<BaseGameMount>
{
	PrefabFile _prefab;

	protected override object Load()
	{
		var builder = new PrefabBuilder().WithName( Path ).WithMetadata( "Title", entry.Title ).WithMetadata( "PreviewModel", previewModel );
		using ( builder.Scope() )
		{
			// Nothing wakes while it's built, so the prefab holds only what's set here.
			Game.ActiveScene.Enabled = false;
			var root = new GameObject( true, System.IO.Path.GetFileNameWithoutExtension( Path ) );
			root.NetworkMode = NetworkMode.Object;
			root.Tags.Add( "removable" );

			HaloSandboxWeapon weapon = halo2 ? root.AddComponent<Halo2Weapon>() : root.AddComponent<HaloWeapon>();
			if ( weapon is Halo2Weapon halo2Weapon ) halo2Weapon.WeaponTag = entry.Tag;
			else ((HaloWeapon)weapon).WeaponTag = entry.Tag;
			weapon.DisplayName = entry.Title;
			weapon.PreferredSlot = entry.Slot;
			weapon.SlotOrder = entry.Order;
			weapon.Value = entry.Value;
		}
		return _prefab = builder.Create();
	}

	protected override void Shutdown()
	{
		PrefabBuilder.Destroy( _prefab );
		base.Shutdown();
	}
}
