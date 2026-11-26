namespace Blam.Halo1.TagData;

public static partial class TagLayouts
{
	/// <summary>
	/// Scenery (0x1FC bytes): the object definition, then 0x80 unused bytes. AI pathfinding reads
	/// its flags, bounding sphere and collision model to steer around it.
	/// </summary>
	public static readonly TagStruct Scenery = new TagStruct( "scenery", 0x1FC, ObjectDefinition );
}
