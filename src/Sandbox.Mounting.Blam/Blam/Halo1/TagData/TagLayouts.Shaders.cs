namespace Blam.Halo1.TagData;

public static partial class TagLayouts
{
	/// <summary>
	/// shader_transparent_plasma (0x14C bytes): the object functions driving its intensity and
	/// offset. A game drawing a biped's modifier shader reads them to know when Halo draws it: with
	/// no intensity source always, else while that function is above 0.
	/// </summary>
	public static readonly TagStruct ShaderTransparentPlasma = new TagStruct( "shader_transparent_plasma", 0x14C )
		.Enum( 0x2C, "intensity source", ["none", "a out", "b out", "c out", "d out"] )
		.Float( 0x30, "intensity exponent" )
		.Enum( 0x34, "offset source", ["none", "a out", "b out", "c out", "d out"] )
		.Float( 0x38, "offset amount" )
		.Float( 0x3C, "offset exponent" );
}
