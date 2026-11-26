using Blam.Halo2.Tags;

namespace Sandbox.Mounting.Blam.Halo2;

/// <summary>
/// How a render model's material is lit.
/// </summary>
enum ModelLighting
{
	/// <summary>An object placed in a level: Halo 2's object lighting (<see cref="ObjectLightingTable"/>).</summary>
	Object,

	/// <summary>The sky: not lit (opaque templates get a neutral grey light, i.e. their albedo as is).</summary>
	Unlit,

	/// <summary>
	/// A scenery object the level's lightmap was baked with (<see cref="SceneryObjectLighting"/>): opaque templates
	/// lit by the lightmap colour and incident direction in its vertices, as Halo draws it; the others by its
	/// object lighting.
	/// </summary>
	Baked,
}

/// <summary>
/// Materials for the shaders render models use, by template family.
/// </summary>
static class ModelMaterials
{
	/// <summary>
	/// The material for a render model's shader, or null for templates the mount can't draw yet (the part is
	/// left out).
	/// </summary>
	/// <param name="randomConstant">The object's <c>random_constant</c>, for a material of its own (null: one per shader).</param>
	/// <param name="named">Create it findable by name rather than anonymous, as materials in a model with material groups
	/// must be: s&amp;box matches a group's materials to the model's by name, and anonymous ones all match its first.</param>
	/// <param name="camouflaged">For a camouflaged object's model: opaque templates draw MCC's active camouflage over
	/// themselves (<see cref="OpaqueMaterial.CamoShaderPath"/>), overlays (emblems) fade with <c>HaloCamo</c>; the
	/// transparent ones are as they are (the *_active_camo templates fade with <c>HaloCamo</c>).</param>
	/// <param name="functions">The model's function slots, which parameters follow while a game plays (see <see cref="DrivenParameters"/>).</param>
	public static Material Create( string name, ShaderTag shader, ModelLighting lighting, float? randomConstant = null, bool named = false, bool camouflaged = false,
		IReadOnlyList<string> functions = null )
	{
		var material = OpaqueMaterial.IsSupported( shader )
			? OpaqueMaterial.Create( name, shader, null, perVertexLightmap: lighting == ModelLighting.Baked, objectLit: lighting == ModelLighting.Object, onObject: lighting != ModelLighting.Unlit,
				randomConstant: randomConstant, named: named, camouflaged: camouflaged, functions: functions )
			// Transparent, sky and plasma templates (visors, glass, sky layers); null for the families not drawn
			// yet (shields, active camo, water...).
			: Halo2Materials.Create( name, shader, null, onObject: lighting != ModelLighting.Unlit, randomConstant: randomConstant, named: named, functions: functions );

		// MCC draws the camouflage over the surface's overlay (the chief's emblem) by the camouflage: it fades with it.
		if ( camouflaged && TransparentMaterial.IsOverlay( shader ) )
			material?.Set( "g_flCamoFade", 2f );

		// MCC's atmospheric fog leaves the sky alone. Its layers add to each other, which the frame buffer copy a
		// gamma space add reads doesn't hold, and they render into the water's reflection cube.
		if ( lighting == ModelLighting.Unlit )
		{
			material?.Set( "g_flHaloSky", 1f );
			material?.SetFeature( "F_GAMMA_ADD", 0 );
		}

		return material;
	}
}
