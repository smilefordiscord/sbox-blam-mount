namespace Blam.Halo1;

/// <summary>
/// Halo CE tag group codes.
/// </summary>
public static class TagClasses
{
	public static readonly TagClass Bitmap = new( "bitm" );
	public static readonly TagClass Scenario = new( "scnr" );
	public static readonly TagClass StructureBsp = new( "sbsp" );
	public static readonly TagClass Shader = new( "shdr" );
	public static readonly TagClass ShaderEnvironment = new( "senv" );
	public static readonly TagClass ShaderModel = new( "soso" );
	public static readonly TagClass ShaderWater = new( "swat" );
	public static readonly TagClass ShaderGlass = new( "sgla" );
	public static readonly TagClass ShaderTransparentGeneric = new( "sotr" );
	public static readonly TagClass ShaderTransparentChicago = new( "schi" );
	public static readonly TagClass ShaderTransparentChicagoExtended = new( "scex" );
	public static readonly TagClass ShaderTransparentMeter = new( "smet" );
	public static readonly TagClass ShaderTransparentPlasma = new( "spla" );
	public static readonly TagClass GbxModel = new( "mod2" );
	public static readonly TagClass Sky = new( "sky " );
	public static readonly TagClass ModelAnimations = new( "antr" );
	public static readonly TagClass Object = new( "obje" );
}
