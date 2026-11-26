namespace Blam.Halo1;

/// <summary>
/// One entry in a Halo CE cache file's tag array.
/// </summary>
public sealed record TagEntry(
	int Index,
	uint Id,
	TagClass Class,
	TagClass Parent,
	TagClass Grandparent,
	string Path,
	uint Address,
	bool IsExternal )
{
	/// <summary>
	/// True if this tag is, or inherits from, the given class. For example every
	/// <c>shader_environment</c> tag is also a <c>shader</c>.
	/// </summary>
	public bool Is( TagClass tagClass ) => Class == tagClass || Parent == tagClass || Grandparent == tagClass;

	public override string ToString() => $"{Path}.{Class}";
}
