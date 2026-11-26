namespace Sandbox.Mounting.Blam.Halo3;

// Base for the Halo 3 mount's resource loaders: Host is Halo3Mount.Current, which also works behind the development reload stub.
abstract class Halo3Loader : ResourceLoader<BaseGameMount>
{
	public new Halo3Mount Host => Halo3Mount.Current;
}
