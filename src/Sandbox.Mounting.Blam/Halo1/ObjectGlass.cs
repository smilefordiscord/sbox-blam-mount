using Blam.Halo1;
using Blam.Halo1.Tags;
using Blam.Halo1.TagData;

namespace Sandbox.Mounting.Blam.Halo1;

/// <summary>
/// Glass on placed objects (powerups, the needler, windshields), drawn like Halo's glass the way
/// level glass is (see <see cref="StructureBspModel"/>): what's behind multiplied by the tint, then
/// the reflection and diffuse over that.
/// </summary>
/// <remarks>
/// The glass in a model's own resource draws over the frame buffer copy, which is taken before
/// any translucent geometry: it painted the scene back without the powerup's orb or the needler's
/// core inside it. The two passes have to draw after everything translucent inside the object, in
/// order, and s&amp;box orders translucent draws by object, not by mesh: farthest first by the
/// distance from the camera to the object's bounds, ties by render world slot. So a placed object
/// with glass gets the model without its glass, plus two child objects with only the glass, one
/// per pass, whose bounds are the whole model's grown by 1 and 2 units: always a little nearer, so
/// they draw after the model and in pass order (unless the camera is inside all three bounds).
/// Animated objects bone merge them.
///
/// Scenes build these models per map. For games, every gbxmodel with glass (the powerups, the
/// needler and sniper ammo, the Warthog's windshield, the Pelican's canopy) is also registered as
/// a prefab set up the same way, <c>mount://haloce/&lt;gbxmodel tag path&gt;.prefab</c>: a
/// <see cref="ModelRenderer"/> drawing the body (a <see cref="SkinnedModelRenderer"/> for models
/// with a skeleton, with the glass bone merged onto it), and the two glass passes as child
/// objects. Spawn that instead of the plain <c>.vmdl</c>, whose glass draws over the frame buffer
/// copy and hides whatever is translucent inside it. The three parts are models of their own as
/// well, for a game that sets up its renderers itself: <c>&lt;path&gt;__body.vmdl</c>,
/// <c>&lt;path&gt;__glass_tint.vmdl</c> and <c>&lt;path&gt;__glass_reflect.vmdl</c>. The body has
/// the model's collision, markers and animations; the glass parts only draw.
/// </remarks>
sealed class ObjectGlass
{
	/// <summary>The resource name of the model without its glass.</summary>
	public static string BodyResourceName( string modelTagPath ) => $"{HaloCeMount.ResourcePath( modelTagPath )}__body";

	/// <summary>The resource name of the glass's tint pass.</summary>
	public static string TintResourceName( string modelTagPath ) => $"{HaloCeMount.ResourcePath( modelTagPath )}__glass_tint";

	/// <summary>The resource name of the glass's reflection pass.</summary>
	public static string ReflectionResourceName( string modelTagPath ) => $"{HaloCeMount.ResourcePath( modelTagPath )}__glass_reflect";

	/// <summary>
	/// Whether the parts the model builds for its glass (the first permutation of each region, at any
	/// level of detail) have any glass.
	/// </summary>
	public static bool HasGlass( GbxModelTag model ) => model.Regions
		.Where( r => r.Permutations.Count > 0 )
		.SelectMany( r => r.Permutations[0].Lods )
		.Distinct()
		.Where( g => g >= 0 && g < model.Geometries.Count )
		.SelectMany( g => model.Geometries[g] )
		.Any( p => p.ShaderIndex >= 0 && p.ShaderIndex < model.Shaders.Count && model.Shaders[p.ShaderIndex].Shader?.Class == TagClasses.ShaderGlass );

	/// <summary>
	/// Registers a gbxmodel's glass parts and prefab, if it has glass.
	/// </summary>
	public static void Register( MountContext context, CacheFile cache, TagEntry modelTag, Sandbox.Diagnostics.Logger log )
	{
		try
		{
			if ( !HasGlass( new GbxModelTag( cache, modelTag ) ) )
				return;
		}
		catch ( Exception e )
		{
			log.Warning( e, $"Couldn't read {modelTag.Path}: {e.Message}" );
			return;
		}

		var path = modelTag.Path;
		context.Add( ResourceType.Model, BodyResourceName( path ), new GbxModelLoader( cache.FilePath, modelTag.Id, parts: GbxModelBuilder.Parts.AllButGlass ) );
		context.Add( ResourceType.Model, TintResourceName( path ), new GbxModelLoader( cache.FilePath, modelTag.Id, parts: GbxModelBuilder.Parts.GlassTint ) );
		context.Add( ResourceType.Model, ReflectionResourceName( path ), new GbxModelLoader( cache.FilePath, modelTag.Id, parts: GbxModelBuilder.Parts.GlassReflection ) );
		context.Add( ResourceType.PrefabFile, HaloCeMount.ResourcePath( path ), new ObjectGlassPrefabLoader( cache.FilePath, modelTag.Id ) );
	}

	/// <param name="Body">The model without its glass, for the object itself.</param>
	/// <param name="Tint">The glass's tint pass.</param>
	/// <param name="Reflection">The glass's reflection and diffuse pass.</param>
	public sealed record Models( Model Body, Model Tint, Model Reflection );

	readonly HaloCeMount _host;
	readonly List<Model> _keepAlive;
	readonly Dictionary<(string Map, uint Tag), Models> _built = new();

	/// <param name="keepAlive">Where to keep the models built (models built in code only exist
	/// while something holds on to them).</param>
	public ObjectGlass( HaloCeMount host, List<Model> keepAlive )
	{
		_host = host;
		_keepAlive = keepAlive;
	}

	/// <summary>
	/// The models for an object with the gbxmodel <paramref name="modelTag"/>, or null if it has no
	/// glass. Built once per scene.
	/// </summary>
	public Models For( CacheFile cache, TagEntry modelTag )
	{
		var key = (cache.FilePath, modelTag.Id);
		if ( _built.TryGetValue( key, out var models ) )
			return models;

		try
		{
			models = Build( cache, modelTag );
		}
		catch ( Exception e )
		{
			_host.Logger.Warning( e, $"Couldn't build the glass of {modelTag.Path}: {e.Message}" );
			models = null;
		}

		_built[key] = models;
		return models;
	}

	Models Build( CacheFile cache, TagEntry modelTag )
	{
		var model = new GbxModelTag( cache, modelTag );
		if ( !model.Shaders.Any( s => s.Shader?.Class == TagClasses.ShaderGlass ) )
			return null;

		var name = $"haloce/{cache.Name}/objects/{modelTag.Path.Replace( '\\', '/' )}";
		var tint = GbxModelBuilder.Build( _host, $"{name}_glass_tint.vmdl", cache, model, animated: true, parts: GbxModelBuilder.Parts.GlassTint );
		var reflection = GbxModelBuilder.Build( _host, $"{name}_glass_reflection.vmdl", cache, model, animated: true, parts: GbxModelBuilder.Parts.GlassReflection );
		if ( tint is null || reflection is null )
			return null;

		var body = GbxModelBuilder.Build( _host, $"{name}_body.vmdl", cache, model, animated: true, parts: GbxModelBuilder.Parts.AllButGlass );
		if ( body is null )
			return null;

		_keepAlive.AddRange( [body, tint, reflection] );
		return new Models( body, tint, reflection );
	}

	/// <summary>
	/// Adds the glass passes to an object whose renderer draws <see cref="Models.Body"/>.
	/// </summary>
	public static void AddRenderers( Models models, ModelRenderer body )
	{
		Add( models.Tint, "glass tint" );
		Add( models.Reflection, "glass reflection" );

		void Add( Model model, string name )
		{
			var go = new GameObject( body.GameObject, true, name );
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

			renderer.Model = model;
			renderer.Tint = body.Tint;
			renderer.RenderType = ModelRenderer.ShadowRenderType.Off;
		}
	}
}

/// <summary>
/// A gbxmodel with glass as a prefab (see <see cref="ObjectGlass"/>): its body on the root, the
/// glass passes as children, from the registered part models.
/// </summary>
sealed class ObjectGlassPrefabLoader( string mapPath, uint modelTagId ) : HaloPrefabLoader
{
	ObjectGlass.Models _models;
	bool _skinned;

	protected override string MapPath => mapPath;

	protected override void Prepare( CacheFile cache )
	{
		var tag = cache.GetTag( modelTagId );
		_skinned = AnimationSkeleton.HasSkeleton( cache, new GbxModelTag( cache, tag ) );

		Model Part( string name )
		{
			var model = Model.Load( $"mount://{Host.Ident}/{name}.vmdl" );
			return model is null || model.IsError ? throw new InvalidOperationException( $"{Path}: couldn't load {name}" ) : model;
		}

		_models = new ObjectGlass.Models( Part( ObjectGlass.BodyResourceName( tag.Path ) ), Part( ObjectGlass.TintResourceName( tag.Path ) ), Part( ObjectGlass.ReflectionResourceName( tag.Path ) ) );
	}

	protected override void Build( CacheFile cache, GameObject root )
	{
		ModelRenderer renderer;
		if ( _skinned )
		{
			var skinned = root.AddComponent<SkinnedModelRenderer>();
			skinned.UseAnimGraph = false;
			renderer = skinned;
		}
		else
			renderer = root.AddComponent<ModelRenderer>();

		renderer.Model = _models.Body;
		ObjectGlass.AddRenderers( _models, renderer );
	}
}
