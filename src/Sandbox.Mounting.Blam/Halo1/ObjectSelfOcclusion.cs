using Blam.Halo1;
using Blam.Halo1.Tags;

namespace Sandbox.Mounting.Blam.Halo1;

/// <summary>
/// Objects whose transparency self-occludes (Cortana; see <see cref="ObjectTags.TransparencySelfOccludes"/>):
/// Halo draws the depth of all their transparent parts right before their colour, so only their
/// nearest surface is coloured and her eyelids cover her eyes. Parts whose shader ignores the
/// object's effect (her hair) are left out of the depth, but still drawn against it.
/// </summary>
/// <remarks>
/// s&amp;box orders translucent draws by object, not by mesh (see <see cref="ObjectGlass"/>), so the
/// depth is a model of its own, <c>&lt;path&gt;__prepass.vmdl</c>, on a child object (bone merged
/// when animated). Its bounds are the whole model's, and the object's own model is bounded 1 unit
/// bigger (see <see cref="GbxModelBuilder.Build"/>), so the depth sorts a little farther and draws
/// first, unless the camera is inside both bounds.
/// </remarks>
static class ObjectSelfOcclusion
{
	/// <summary>The shader drawing the depth alone, the colour blended away.</summary>
	public const string PrepassShaderPath = "shaders/halo2_depth_prepass.shader";

	/// <summary>The resource name of a model's depth prepass.</summary>
	public static string PrepassResourceName( string modelTagPath ) => $"{HaloCeMount.ResourcePath( modelTagPath )}__prepass";

	/// <summary>
	/// Whether a part with this shader writes the depth: every transparent shader but those that
	/// ignore the object's effect. Model and environment shaders aren't drawn as transparents.
	/// </summary>
	public static bool Occludes( CacheFile cache, TagEntry shader ) =>
		shader.Class != TagClasses.ShaderModel && shader.Class != TagClasses.ShaderEnvironment &&
		!ShaderTransparentTag.IgnoresEffect( cache, shader );

	/// <summary>
	/// Registers the depth prepass of every gbxmodel an object with self-occluding transparency uses.
	/// </summary>
	public static void Register( MountContext context, CacheFile cache, HashSet<string> registered )
	{
		var models = cache.Tags
			.Where( t => t.Is( TagClasses.Object ) && ObjectTags.TransparencySelfOccludes( cache, t ) )
			.Select( t => ObjectTags.GetObjectModel( cache, t ) )
			.Where( m => m is not null && m.Class == TagClasses.GbxModel && !string.IsNullOrEmpty( m.Path ) );

		foreach ( var model in models )
		{
			var path = PrepassResourceName( model.Path );
			if ( registered.Add( path ) )
				context.Add( ResourceType.Model, path, new GbxModelLoader( cache.FilePath, model.Id, parts: GbxModelBuilder.Parts.DepthPrepass ) );
		}
	}

	/// <summary>
	/// Adds the depth prepass to an object drawing a self-occluding object's model.
	/// </summary>
	public static void AddRenderer( Model prepass, ModelRenderer body )
	{
		var go = new GameObject( body.GameObject, true, "depth prepass" );
		ModelRenderer renderer;
		if ( body is SkinnedModelRenderer skinned )
		{
			var merged = go.AddComponent<SkinnedModelRenderer>();
			merged.UseAnimGraph = false;
			merged.BoneMergeTarget = skinned;
			renderer = merged;
		}
		else
			renderer = go.AddComponent<ModelRenderer>();

		renderer.Model = prepass;
		renderer.RenderType = ModelRenderer.ShadowRenderType.Off;
	}
}
