namespace Sandbox.Mounting.Blam.Halo1;

/// <summary>
/// Base for the Halo CE mount's resource loaders. Same as <see cref="ResourceLoader{T}"/>, except
/// <see cref="Host"/> is <see cref="HaloCeMount.Current"/>, which also works when the mount runs
/// behind the development reload stub.
/// </summary>
abstract class HaloCeLoader : ResourceLoader<BaseGameMount>
{
	public new HaloCeMount Host => HaloCeMount.Current;
}
