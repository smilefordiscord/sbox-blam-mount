using Blam;
using Blam.Halo1;
using Blam.Halo1.Tags;

namespace Sandbox.Mounting.Blam.Halo1;

/// <summary>
/// Picks the material builder for a Halo CE shader tag, by shader type.
/// </summary>
static class HaloMaterials
{
	/// <summary>
	/// Whether surfaces with this shader are drawn. Plasma shaders (shader_transparent_plasma) only
	/// are on objects: they're the energy shells over shielded characters, driven by the object's
	/// functions. On the elite, cyborg and monitor they're the biped's modifier shader, an extra pass
	/// over the whole model rather than geometry; the sentinel's shield is geometry in its model.
	/// </summary>
	public static bool IsSupported( TagClass shader ) =>
		shader == TagClasses.ShaderEnvironment || shader == TagClasses.ShaderModel ||
		shader == TagClasses.ShaderWater || shader == TagClasses.ShaderGlass ||
		shader == TagClasses.ShaderTransparentMeter || ShaderTransparentTag.IsTransparent( shader );

	/// <summary>Whether an object's model surfaces with this shader are drawn: plasma shells too.</summary>
	public static bool IsSupportedOnObject( TagClass shader ) => IsSupported( shader ) || shader == TagClasses.ShaderTransparentPlasma;

	/// <summary>
	/// Whether surfaces with this shader should block movement. Water, transparent effects
	/// (lights, waterfalls, energy fields), meters and plasma shields don't.
	/// </summary>
	public static bool IsSolid( TagClass shader ) =>
		shader != TagClasses.ShaderWater && shader != TagClasses.ShaderTransparentMeter &&
		shader != TagClasses.ShaderTransparentPlasma && !ShaderTransparentTag.IsTransparent( shader );

	/// <param name="lightmap">Lightmap page for level geometry, null for objects.</param>
	/// <param name="onObject">Whether it's for an object's model, which Halo lit with the object's lighting (environment shaders on doors, panels) and drove with the object's functions (transparent shaders).</param>
	/// <param name="permutation">A model part's shader permutation (see <see cref="TransparentMaterial.Create"/>).</param>
	public static Material Create( HaloCeMount host, string name, CacheFile cache, TagEntry shader, Texture lightmap = null, bool onObject = false, int permutation = 0 )
	{
		if ( shader.Class == TagClasses.ShaderModel )
			return ModelMaterial.Create( host, name, cache, shader, permutation );

		if ( shader.Class == TagClasses.ShaderTransparentPlasma )
			return onObject ? PlasmaMaterial.Create( host, name, cache, shader ) : null;

		if ( shader.Class == TagClasses.ShaderWater )
			return GlassWaterMaterial.CreateWater( host, name, cache, shader );

		if ( shader.Class == TagClasses.ShaderGlass )
			return GlassWaterMaterial.CreateGlass( host, name, cache, shader, lightmap );

		if ( shader.Class == TagClasses.ShaderTransparentMeter )
			return MeterMaterial.Create( host, name, cache, shader );

		if ( ShaderTransparentTag.IsTransparent( shader.Class ) )
			return TransparentMaterial.Create( host, name, cache, shader, onObject, permutation );

		if ( shader.Class != TagClasses.ShaderEnvironment )
			return null;

		return EnvironmentMaterial.Create( host, name, cache, shader, lightmap, onObject );
	}
}
