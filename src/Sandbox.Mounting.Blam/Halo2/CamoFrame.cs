namespace Sandbox.Mounting.Blam.Halo2;

/// <summary>
/// The frame camouflaged objects refract (the <c>_camo</c> models, <see cref="CamoModelLoader"/>): grabbed by an object
/// of its own that <see cref="SceneUpkeep"/> sorts right after the sky, the water and the weather, so it holds them.
/// </summary>
/// <remarks>
/// MCC draws a camouflaged unit among the transparent things and copies the frame just before it. s&amp;box's own copy
/// for translucent readers is taken after the opaque things only, so the open water and the sky's layers (drawn as
/// transparent here) weren't in it: a cloaked chief in front of Zanzibar's sea refracted the sky's clear colour, a flat
/// teal body, where MCC shows the sea through him. Its copy is also masked by the readers' own depth (black outside
/// their silhouette). This copy is neither; halo2_opaque_camo.shader reads it as HaloCamoFrame where HaloHasCamoFrame
/// is set, and s&amp;box's copy otherwise (scenes without the upkeep).
/// </remarks>
sealed class CamoFrame : FrameGrab
{
	public const string FrameAttribute = "HaloCamoFrame";
	public const string HasFrameAttribute = "HaloHasCamoFrame";

	public CamoFrame() : base( "halo2 camo frame", FrameAttribute, HasFrameAttribute )
	{
	}

	/// <summary>
	/// Whether a renderer draws a camouflaged model.
	/// </summary>
	public static bool IsCamo( ModelRenderer renderer )
		=> renderer.Model?.Name is { } path
			&& path.StartsWith( "mount://halo2/", StringComparison.OrdinalIgnoreCase )
			&& path.EndsWith( "_camo.vmdl", StringComparison.OrdinalIgnoreCase );

	/// <summary>
	/// Whether a renderer draws a shield shell (<see cref="ShieldMaterial"/>), which adds to this copy in gamma space
	/// too: s&amp;box's copy is black for it (it writes no depth), so each vein added to black, thin and faint at its
	/// edges.
	/// </summary>
	public static bool IsShield( ModelRenderer renderer )
		=> IsShieldMaterial( renderer.MaterialOverride ) || (renderer.Model?.Materials.Any( IsShieldMaterial ) ?? false);

	static bool IsShieldMaterial( Material material )
		=> material?.Shader?.ResourcePath is { } path && path.EndsWith( System.IO.Path.GetFileName( ShieldMaterial.ShaderPath ), StringComparison.OrdinalIgnoreCase );
}
