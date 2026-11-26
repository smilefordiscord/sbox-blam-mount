using Blam.Halo1;
using Blam.Halo1.Tags;

namespace Sandbox.Mounting.Blam.Halo1;

/// <summary>
/// The active camouflage material (halo1_active_camouflage.shader), registered once as
/// <c>mount://haloce/rasterizer/active_camouflage.vmat</c> from the globals of the first map.
/// </summary>
/// <remarks>
/// A game draws a camouflaged unit's model a second time with this as the material override (a
/// second renderer bone merged onto the first) and sets <c>HaloCamo</c> (0-1) in both renderers'
/// attributes; this blends over the model by it, and at 1 the model isn't drawn and this replaces
/// it. The second renderer shouldn't cast shadows (its <c>RenderType</c> off): its shadow lands
/// on the level just behind the unit, which the camouflage then bends into view as dark slivers
/// along the silhouette. Halo's camouflaged units cast none. Halo's camouflage settings are in
/// the globals' rasterizer data (see <see cref="ActiveCamouflageGlobals"/>), the same in every map.
/// </remarks>
static class ActiveCamouflageMaterial
{
	public const string ShaderPath = "shaders/halo1_active_camouflage.shader";

	/// <summary>
	/// The material's resource name.
	/// </summary>
	public const string ResourceName = "rasterizer/active_camouflage";

	public static Material Create( HaloCeMount host, string name, CacheFile cache )
	{
		var material = Material.Create( name, ShaderPath );
		if ( material is null )
			return null;

		SetGlobals( host, material, cache );
		return material;
	}

	/// <summary>
	/// Sets Halo's camouflage settings from the globals (distortion cube map, refraction, falloff,
	/// tint) on a material: this one, or a model material's camouflaged variant (see
	/// <see cref="ModelMaterial"/>).
	/// </summary>
	public static void SetGlobals( HaloCeMount host, Material material, CacheFile cache )
	{
		var camo = ActiveCamouflageGlobals.Read( cache );
		material.Set( "g_tDistortion", MaterialTextures.Load( host, material, camo?.Distortion, 0 ) ?? MaterialTextures.FlatNormal );
		if ( camo is not null )
		{
			material.Set( "g_vCamoRefraction", new Vector2( camo.RefractionAmount, camo.HyperStealthRefractionAmount ) );
			material.Set( "g_vCamoDistanceFalloff", new Vector2( camo.DistanceFalloff, camo.HyperStealthDistanceFalloff ) * HaloCeMount.WorldScale );
			material.Set( "g_vCamoTint", (Vector3)camo.Tint );
			material.Set( "g_vCamoHyperStealthTint", (Vector3)camo.HyperStealthTint );
		}
	}
}

/// <summary>
/// Loads the active camouflage material from a map's globals.
/// </summary>
sealed class ActiveCamouflageMaterialLoader( string mapPath ) : HaloCeLoader
{
	protected override object Load() => ActiveCamouflageMaterial.Create( Host, Path, Host.GetCache( mapPath ) );
}
