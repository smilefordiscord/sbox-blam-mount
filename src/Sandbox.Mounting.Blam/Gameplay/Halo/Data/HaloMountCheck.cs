using System;
using System.Threading.Tasks;
using Sandbox.Mounting;

namespace Halo.Data;

// What stands between this machine and a game of Halo.
public enum HaloMountStatus {
	// Mounted, and the gameplay data reads.
	Ready,

	// The Blam mount isn't installed.
	MountMissing,

	// The mount can't find MCC with Halo CE installed.
	GameMissing,

	// The mount is installed but not enabled.
	NotMounted,

	// Mounted, but mount://haloce/game.json doesn't read because the Halo CE maps weren't found.
	DataMissing,
}

// The Halo CE mount's state; every level, model, sound and gameplay value comes from it.
public static class HaloMountCheck {
	public const string Ident = "haloce";

	public static BaseGameMount Mount => Sandbox.Mounting.Directory.Get( Ident );

	// Changes when the mount appears, mounts or unmounts, so data stores know to retry a failed read.
	public static int MountState => Mount is { } m ? m.IsMounted ? 2 : 1 : 0;

	// Cheap: the data read behind it is cached.
	public static HaloMountStatus Status {
		get {
			var mount = Mount;
			if ( mount is null ) return HaloMountStatus.MountMissing;
			if ( !mount.IsMounted ) return mount.IsInstalled ? HaloMountStatus.NotMounted : HaloMountStatus.GameMissing;
			return HaloDataStore.Game is null ? HaloMountStatus.DataMissing : HaloMountStatus.Ready;
		}
	}

	public static bool IsReady => Status == HaloMountStatus.Ready;

	// Call while loading: mounts Halo CE if needed and preloads the gameplay and HUD data off the first frames of play.
	public static async Task<HaloMountStatus> Prepare( string mapName = null ) {
		var mount = Mount;
		if ( mount is { IsInstalled: true, IsMounted: false } ) {
			try {
				await Sandbox.Mounting.Directory.Mount( Ident );
			}
			catch ( Exception e ) {
				Log.Warning( $"Couldn't mount {Ident}: {e.Message}" );
			}
		}

		var status = Status;
		if ( status == HaloMountStatus.Ready ) {
			if ( !string.IsNullOrEmpty( mapName ) ) HaloDataStore.Map( mapName );
		}
		else {
			Log.Warning( $"Halo can't start: {Title( status )} {Detail( status )}" );
		}

		return status;
	}

	public static string Title( HaloMountStatus status ) => status switch {
		HaloMountStatus.MountMissing => "The Blam mount isn't installed.",
		HaloMountStatus.GameMissing => "Halo: The Master Chief Collection wasn't found.",
		HaloMountStatus.NotMounted => "Halo: Combat Evolved isn't enabled.",
		HaloMountStatus.DataMissing => "Halo: Combat Evolved's files weren't found.",
		_ => "Ready.",
	};

	public static string Detail( HaloMountStatus status ) => status switch {
		HaloMountStatus.MountMissing => "This game plays Halo CE from your own copy of Halo: The Master Chief Collection, through the Blam mount. Install the mount into s&box (mount/blam/blam.dll in the s&box folder) and restart s&box.",
		HaloMountStatus.GameMissing => "Install Halo: The Master Chief Collection on Steam, with Halo: Combat Evolved (the halo1 folder), then restart s&box.",
		HaloMountStatus.NotMounted => "Enable Halo: Combat Evolved in s&box's mount settings.",
		HaloMountStatus.DataMissing => "The mount is enabled but couldn't read the Halo CE multiplayer maps from your MCC install (halo1/maps). Check the install in Steam (Verify integrity of game files).",
		_ => "",
	};
}
