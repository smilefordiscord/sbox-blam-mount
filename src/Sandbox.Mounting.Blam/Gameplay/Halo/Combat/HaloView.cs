namespace Halo.Combat;

// What the local player's Halo weapon shows, for the projectiles and effects that draw from it. Set each frame by HaloWeapon.
public static class HaloView {
	// The local player sees through their eyes, not Sandbox's third person camera.
	public static bool ThirdPerson { get; set; } = true;

	// A Halo first person weapon is drawn.
	public static bool LocalViewModelShown { get; set; }

	// The first person weapon's muzzle ("primary trigger" marker), where our shots are drawn from.
	public static Transform? LocalMuzzle { get; set; }
}
