using Blam.Halo2.Tags;

namespace Sandbox.Mounting.Blam.Halo2;

/// <summary>
/// Builds s&amp;box materials for Halo 2's plasma templates (halo2_plasma.shader): plasma_alpha (grav lifts,
/// covenant lights, powerups), plasma_alpha_active_camo and plasma_mask_offset (jackal shields, energy blade).
/// </summary>
/// <remarks>
/// Like <see cref="TransparentMaterial"/>, the program comes from the template's transparent pass and the slots
/// from the template. Object-driven curves (power, shield...) take their values for the object at rest, or 0 on
/// level geometry (<see cref="TemplatePassParameters.Apply"/>).
/// Not covered yet: plasma_shield / overshield (units' shield shells: 3D noise, a shell offset and object
/// functions), particle_plasma, one_alpha_env_plasma, plasma_light_gel.
/// </remarks>
static class PlasmaMaterial
{
	public const string ShaderPath = "shaders/halo2_plasma.shader";

	enum Program { PlasmaAlpha, MaskOffset }

	static readonly Dictionary<string, (Program Program, bool CamoFade)> Passes = new( StringComparer.OrdinalIgnoreCase )
	{
		[@"plasma\plasma_alpha"] = (Program.PlasmaAlpha, false),
		[@"plasma\plasma_alpha_active_camo"] = (Program.PlasmaAlpha, true),
		[@"transparent\plasma_mask_offset"] = (Program.MaskOffset, false),
	};

	public static ShaderTemplatePass FindPass( ShaderTag shader )
	{
		if ( shader.Template is null )
			return null;

		var pass = new ShaderTemplateTag( shader.Template ).Passes( ShaderTemplateTag.TransparentLayer ).FirstOrDefault();
		return pass is not null && Passes.ContainsKey( pass.Name ) ? pass : null;
	}

	public static bool IsSupported( ShaderTag shader ) => FindPass( shader ) is not null;

	/// <summary>Whether this draws a pass (by its name, below shaders\shader_passes\).</summary>
	public static bool Draws( ShaderTemplatePass pass ) => Passes.ContainsKey( pass.Name );

	/// <param name="onObject">On an object rather than level geometry or the sky (see <see cref="TemplatePassParameters.Apply"/>).</param>
	/// <param name="randomConstant">On an object: its <c>random_constant</c>, if known.</param>
	/// <param name="named">Findable by name (not anonymous): see <see cref="ModelMaterials.Create"/>.</param>
	/// <param name="pass">The template's pass to draw (one of its later passes, see <see cref="Halo2Materials.ExtraPasses"/>);
	/// null for its first.</param>
	public static Material Create( string name, ShaderTag shader, bool onObject, float? randomConstant = null, bool named = false, ShaderTemplatePass pass = null,
		IReadOnlyList<string> functions = null )
	{
		pass ??= FindPass( shader );
		if ( pass is null || !Passes.ContainsKey( pass.Name ) )
			return null;

		var material = Material.Create( name, ShaderPath, anonymous: !named );
		if ( material is null )
			return null;

		var (program, camoFade) = Passes[pass.Name];
		TemplatePassParameters.Apply( material, shader, pass, onObject, cubeStage: -1, blendFeature: false, randomConstant: randomConstant, functions: functions );
		material.Set( "g_flProgram", (float)program );
		material.Set( "g_flCamoFade", camoFade ? 1f : 0f );
		return material;
	}
}
