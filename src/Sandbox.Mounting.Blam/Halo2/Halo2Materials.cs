using Blam.Halo2.Tags;

namespace Sandbox.Mounting.Blam.Halo2;

/// <summary>
/// How a Halo 2 shader draws: which s&amp;box shader family takes it.
/// </summary>
enum Halo2MaterialKind { Unsupported, Opaque, Transparent, Plasma, Water }

/// <summary>
/// One entry point for turning a Halo 2 shader (<c>shad</c>) into an s&amp;box material, for level geometry and
/// models alike: opaque templates (halo2_opaque.shader), the transparent ones (halo2_transparent.shader),
/// plasma (halo2_plasma.shader) and water (halo2_water.shader).
/// </summary>
/// <remarks>
/// Transparent and plasma materials are translucent: they don't write depth, and s&amp;box sorts them per object
/// by distance (StructureBspModel.BuildTransparent puts the level's in objects of their own for that). On a model,
/// a game can set these renderer attributes: <c>HaloCamo</c> (0-1) fades the transparent parts of camouflaged
/// weapons (the *_active_camo templates). Object functions driving shader animation by name (health, shield,
/// power...) aren't live: materials take them for an object at rest, or 0 on level geometry and the sky as in MCC
/// (<see cref="AnimatedShader"/>); water keeps the shader's stored values.
/// </remarks>
static class Halo2Materials
{
	public static Halo2MaterialKind KindOf( ShaderTag shader )
	{
		if ( OpaqueMaterial.IsSupported( shader ) )
			return Halo2MaterialKind.Opaque;
		if ( PlasmaMaterial.IsSupported( shader ) )
			return Halo2MaterialKind.Plasma;
		if ( TransparentMaterial.IsSupported( shader ) )
			return Halo2MaterialKind.Transparent;
		if ( WaterMaterial.IsSupported( shader ) )
			return Halo2MaterialKind.Water;
		return Halo2MaterialKind.Unsupported;
	}

	/// <summary>
	/// A material for a shader, or null if its template isn't supported (water edge blend, particles, decals,
	/// shields, active camouflage, HUD...).
	/// </summary>
	/// <param name="name">The material's resource name (ends in .vmat).</param>
	/// <param name="lightmap">Opaque only: the lightmap page, or null (per-vertex lit or on an object).</param>
	/// <param name="onObject">Drawn on an object (a model placed in a level) rather than level geometry or the sky:
	/// opaque templates are lit by the object lighting, and object functions driving any template's animation take
	/// their values for the object at rest (on level geometry and the sky they are 0).</param>
	/// <param name="water">Water only: the BSP's water definition, whose constants every water shader in it gets.</param>
	/// <param name="randomConstant">On an object: its <c>random_constant</c>, if known (else one per shader).</param>
	/// <param name="named">Findable by name (not anonymous): see <see cref="ModelMaterials.Create"/>.</param>
	/// <param name="functions">On an object: its model's function slots (see <see cref="DrivenParameters"/>).</param>
	public static Material Create( string name, ShaderTag shader, Texture lightmap = null, bool onObject = false, WaterDefinition water = null, float? randomConstant = null,
		bool named = false, IReadOnlyList<string> functions = null )
	{
		return KindOf( shader ) switch
		{
			Halo2MaterialKind.Opaque => OpaqueMaterial.Create( name, shader, onObject ? null : lightmap, objectLit: onObject, randomConstant: randomConstant, named: named, functions: functions ),
			Halo2MaterialKind.Plasma => PlasmaMaterial.Create( name, shader, onObject, randomConstant, named, functions: functions ),
			Halo2MaterialKind.Transparent => TransparentMaterial.Create( name, shader, onObject, randomConstant, named, functions: functions ),
			Halo2MaterialKind.Water => WaterMaterial.Create( name, shader, water ),
			_ => null,
		};
	}

	/// <summary>The shader of a mesh's depth prepass (<see cref="ExtraPasses"/>).</summary>
	public const string DepthPrepassShaderPath = "shaders/halo2_depth_prepass.shader";

	/// <summary>
	/// What a transparent template draws besides the pass its material (<see cref="Create"/>) is for, as materials for
	/// copies of the mesh: its z prepass, which goes before it (only the nearest surface of the mesh is coloured:
	/// Cortana's eyelids cover her eyes), and its later transparent passes this draws, after it in order (Cortana's
	/// plasma traces and bumped reflection). An additive pass after a premultiplied one adds the same either way, as
	/// long as it draws after. Empty for everything else.
	/// </summary>
	public static (Material Prepass, List<Material> After) ExtraPasses( string name, ShaderTag shader, bool onObject, float? randomConstant = null, bool named = false,
		IReadOnlyList<string> functions = null )
	{
		var after = new List<Material>();
		if ( shader.Template is null || KindOf( shader ) is not (Halo2MaterialKind.Transparent or Halo2MaterialKind.Plasma) )
			return (null, after);

		var template = new ShaderTemplateTag( shader.Template );
		var baseName = System.IO.Path.ChangeExtension( name, null );
		Material prepass = null;
		// Layer 7 holds a glow-only template's own pass rather than a prepass (TransparentMaterial.GlowPass).
		if ( template.Passes( ShaderTemplateTag.DepthPrepassLayer ).Count > 0 && !TransparentMaterial.IsGlow( shader ) )
			prepass = Material.Create( $"{baseName}_prepass.vmat", DepthPrepassShaderPath, anonymous: !named );

		var passes = template.Passes( ShaderTemplateTag.TransparentLayer );
		for ( var p = 1; p < passes.Count; p++ )
		{
			var pass = passes[p];
			var material = PlasmaMaterial.Draws( pass ) ? PlasmaMaterial.Create( $"{baseName}_pass{p}.vmat", shader, onObject, randomConstant, named, pass, functions )
				: TransparentMaterial.Draws( pass ) ? TransparentMaterial.Create( $"{baseName}_pass{p}.vmat", shader, onObject, randomConstant, named, pass, functions )
				: null;
			if ( material is not null )
				after.Add( material );
		}

		return (prepass, after);
	}
}
