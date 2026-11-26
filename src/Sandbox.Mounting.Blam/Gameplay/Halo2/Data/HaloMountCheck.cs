using Halo2.Combat;
using System;
using System.Threading.Tasks;
using Sandbox.Mounting;

namespace Halo2.Data;

// What stands between this machine and a game of Halo 2.
public enum HaloMountStatus {
	// Mounted, with the starting map's scene.
	Ready,

	// The Blam mount isn't installed.
	MountMissing,

	// The mount can't find MCC with Halo 2 installed.
	GameMissing,

	// The mount is installed but not enabled.
	NotMounted,

	// Mounted, but the starting map's scene isn't there because the Halo 2 maps weren't found.
	DataMissing,
}

// The Halo 2 mount's state; every level, model, sound and gameplay value comes from it.
public static class HaloMountCheck {
	public const string Ident = "halo2";

	public static BaseGameMount Mount => Sandbox.Mounting.Directory.Get( Ident );

	// Changes when the mount appears, mounts or unmounts, so data stores know to retry a failed read.
	public static int MountState => Mount is { } m ? m.IsMounted ? 2 : 1 : 0;

	// Whether this machine can play the map.
	public static HaloMountStatus Status( string mapName ) {
		var mount = Mount;
		if ( mount is null ) return HaloMountStatus.MountMissing;
		if ( !mount.IsMounted ) return mount.IsInstalled ? HaloMountStatus.NotMounted : HaloMountStatus.GameMissing;
		return HaloMountStatus.Ready;
	}

	// Call while loading: mounts Halo 2 if needed and reads the gameplay data off the first frames of play.
	public static async Task<HaloMountStatus> Prepare( string mapName ) {
		var mount = Mount;
		if ( mount is { IsInstalled: true, IsMounted: false } ) {
			try {
				await Sandbox.Mounting.Directory.Mount( Ident );
			}
			catch ( Exception e ) {
				Log.Warning( $"Couldn't mount {Ident}: {e.Message}" );
			}
		}

		var status = Status( mapName );
		if ( status == HaloMountStatus.Ready ) {
			_ = HaloDataStore.Game;
			HaloDataStore.Map( mapName );
		}
		else {
			Log.Warning( $"Halo 2 can't start: {Title( status )} {Detail( status )}" );
		}

		return status;
	}

	public static string Title( HaloMountStatus status ) => status switch {
		HaloMountStatus.MountMissing => "The Blam mount isn't installed.",
		HaloMountStatus.GameMissing => "Halo: The Master Chief Collection wasn't found.",
		HaloMountStatus.NotMounted => "Halo 2 isn't enabled.",
		HaloMountStatus.DataMissing => "Halo 2's maps weren't found.",
		_ => "Ready.",
	};

	public static string Detail( HaloMountStatus status ) => status switch {
		HaloMountStatus.MountMissing => "This game plays Halo 2 from your own copy of Halo: The Master Chief Collection, through the Blam mount. Install the mount into s&box (mount/blam/blam.dll in the s&box folder) and restart s&box.",
		HaloMountStatus.GameMissing => "Install Halo: The Master Chief Collection on Steam, with Halo 2 (the halo2 folder), then restart s&box.",
		HaloMountStatus.NotMounted => "Enable Halo 2 in s&box's mount settings.",
		HaloMountStatus.DataMissing => "The mount is enabled but couldn't read the Halo 2 multiplayer maps from your MCC install (halo2/h2_maps_win64_dx11). Check the install in Steam (Verify integrity of game files).",
		_ => "",
	};
}
