namespace Blam.Halo2;

/// <summary>
/// One tag of a Halo 2 cache file. <see cref="Cache"/> is the map that holds its data, which for the tags a
/// multiplayer map takes from shared.map is shared.map.
/// </summary>
public sealed record TagEntry(
	CacheFile Cache,
	int Index,
	uint Id,
	TagClass Class,
	TagClass Parent,
	TagClass Grandparent,
	string Path,
	uint Address,
	uint Size )
{
	/// <summary>
	/// True if this tag is, or inherits from, the given class.
	/// </summary>
	public bool Is( TagClass tagClass ) => Class == tagClass || Parent == tagClass || Grandparent == tagClass;

	public override string ToString() => $"{Path}.{Class}";
}
