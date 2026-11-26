namespace Sandbox.Mounting.Blam.Halo2;

/// <summary>
/// Base for the Halo 2 mount's resource loaders. Same as <see cref="ResourceLoader{T}"/>, except
/// <see cref="Host"/> is <see cref="Halo2Mount.Current"/>, which also works behind the development reload stub.
/// </summary>
abstract class Halo2Loader : ResourceLoader<BaseGameMount>
{
	public new Halo2Mount Host => Halo2Mount.Current;
}
