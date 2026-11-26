using Blam.Halo2;
using Blam.Halo2.Tags;
using MaterialTextures = Sandbox.Mounting.Blam.Halo1.MaterialTextures;

namespace Sandbox.Mounting.Blam.Halo2;

/// <summary>
/// Builds s&amp;box materials for Halo 2's opaque shader templates (halo2_opaque.shader).
/// </summary>
/// <remarks>
/// A Halo 2 shader's parameters are nameless slots; what each one does depends on its template. The template table
/// (<see cref="OpaqueTemplates"/>, generated from where every template's passes bind each slot) says which bitmap
/// is the base map, detail map and so on, and which constants hold the texture transforms and colours; the
/// template's passes say which variation of the math it is (<see cref="OpaqueSetup"/>). A template without an entry
/// (none in MCC's maps) gets its first plausible bitmap as the base map.
///
/// The shader's own fields add the lightmap specular: its type (shad 0x40: none, default, dull, shiny) and its
/// brightness (0x44, used as is: in MCC it scales the highlight linearly and reaches the pixel shader unhalved).
///
/// Shader animation (<see cref="AnimatedShader"/>) overrides the slots it targets before anything reads them:
/// what follows object functions (health, shields, heretic and burnt variants, emblems, ammunition digits, random
/// variants) as an object at rest shows it, level geometry with every input 0 as in MCC, and what follows time
/// frozen at time 0. The time curves halo2_opaque.shader can evaluate go to it by role (<see cref="SetAnimation"/>):
/// scrolling texture transforms (conveyors, treads, ammo belts, screens) and pulsing self-illumination.
///
/// The PRT templates (<see cref="OpaquePrt"/>) light objects by each object's spherical harmonic light (its row of
/// the <see cref="ObjectLightingTable"/>) through the radiance transfer in their vertices
/// (<see cref="RenderModelBuilder"/>), scaled by the template's c21.x, with a highlight off their specular cube.
///
/// Not done: change colours (objects), bloom.
/// </remarks>
static class OpaqueMaterial
{
	public const string ShaderPath = "shaders/halo2_opaque.shader";

	/// <summary>The camouflaged variant (halo2_opaque.shader with HALO2_OPAQUE_CAMO, made by tools/compile-shaders.ps1).</summary>
	public const string CamoShaderPath = "shaders/halo2_opaque_camo.shader";

	/// <summary>
	/// Whether a shader is an opaque template that draws as opaque geometry (not an overlay, decal, additive glow or
	/// active camouflage).
	/// </summary>
	public static bool IsSupported( ShaderTag shader ) =>
		shader.TemplateName.StartsWith( @"opaque\", StringComparison.OrdinalIgnoreCase ) &&
		(OpaqueTemplates.Get( shader.TemplateName ) is not { } template || template.Setup is not null);

	/// <summary>
	/// Whether the shader's albedo is tinted by its object's change colours (the <c>*_change_color</c> templates).
	/// </summary>
	public static bool UsesChangeColors( ShaderTag shader ) =>
		IsSupported( shader ) && OpaqueTemplates.Get( shader.TemplateName )?.Setup?.Albedo is OpaqueAlbedo.OneChangeColor or OpaqueAlbedo.TwoChangeColor or OpaqueAlbedo.FourChangeColor;

	/// <summary>
	/// Sets the change colours of the model variant a material is for (see halo2_opaque.shader's <c>ChangeColor</c>):
	/// <paramref name="picked"/> for a material group picked by name, over the object's own colours; otherwise the
	/// model's default variant's, used where the object has no row in the <see cref="ObjectLightingTable"/>. Colours
	/// the object doesn't have (null) are black, as in MCC.
	/// </summary>
	public static void SetVariantChangeColors( Material material, System.Numerics.Vector3?[] colors, bool picked )
	{
		for ( var k = 0; k < 4; k++ )
		{
			var color = k < colors.Length && colors[k] is { } c ? c : System.Numerics.Vector3.Zero;
			material.Set( $"g_vVariantChangeColor{k}", new Vector4( color.X, color.Y, color.Z, picked ? 2f : 1f ) );
		}
	}

	/// <param name="lightmap">The lightmap page, or null.</param>
	/// <param name="perVertexLightmap">Lit by the lightmap colour in the vertices (see <see cref="LevelVertex.LightmapColor"/>)
	/// rather than a page. Without either, the geometry is lit neutrally.</param>
	/// <param name="objectLit">For objects' render models: lit by Halo 2's object lighting, each object from
	/// its row of the <see cref="ObjectLightingTable"/>, instead of a lightmap.</param>
	/// <param name="onObject">On an object's render model (implied by <paramref name="objectLit"/>), whose shader
	/// animation runs on the object's inputs.</param>
	/// <param name="levelLod">Level geometry whose vertices carry MCC's LOD sizes (<see cref="LevelVertex.LodSize"/>):
	/// switches to the template's LOD1 where MCC would (see <see cref="SetLevelLod"/>).</param>
	/// <param name="randomConstant">On an object: its <c>random_constant</c>, if known.</param>
	/// <param name="named">Findable by name (not anonymous): see <see cref="ModelMaterials.Create"/>.</param>
	/// <param name="camouflaged">For a camouflaged object's model (<see cref="CamoShaderPath"/>): drawn after the frame
	/// buffer copy with MCC's active camouflage over it by the renderer's <c>HaloCamo</c>, refracting through the shader's
	/// camo bump map.</param>
	/// <param name="functions">On an object: its model's function slots, which parameters follow while a game plays (see <see cref="DrivenParameters"/>).</param>
	public static Material Create( string name, ShaderTag shader, Texture lightmap, bool perVertexLightmap = false, bool objectLit = false, bool onObject = false, bool levelLod = false,
		float? randomConstant = null, bool named = false, bool camouflaged = false, IReadOnlyList<string> functions = null )
	{
		var material = Material.Create( name, camouflaged ? CamoShaderPath : ShaderPath, anonymous: !named );
		if ( material is null )
			return null;

		// The camo bump slot is read before the animation (it isn't animated, and it's the stored shader's).
		if ( camouflaged )
			material.Set( "g_tCamoBump", Load( shader, shader.CamoBumpBitmap ) ?? MaterialTextures.FlatNormal );

		// The shader's slots as its animation leaves them: an object at rest (its own random_constant when given, else
		// one from the shader's name for every object sharing the material), or level geometry.
		var inputs = objectLit || onObject ? ShaderInputs.ForObject( shader.Tag, randomConstant ) : ShaderInputs.None;
		var stored = shader;
		var animation = new AnimatedShader( shader, inputs );
		shader = animation.Shader;

		var template = OpaqueTemplates.Get( shader.TemplateName ) ?? Guess( shader );
		var setup = template.Setup ?? OpaqueTemplates.Describe( ["lightmap_tex"] );

		material.Set( "g_tLightmap", lightmap ?? MaterialTextures.Grey );
		material.Set( "g_flLightmapMode", perVertexLightmap ? 2f : lightmap is null ? 0f : 1f );

		// Maps and their transforms.
		SetMap( material, shader, "g_tBaseMap", "g_vBaseTransform", template.Base, template.BaseUv, MaterialTextures.White );
		SetMap( material, shader, "g_tDetailMap", "g_vDetailTransform", template.Detail, template.DetailUv, MaterialTextures.Grey );
		SetMap( material, shader, "g_tDetailMapB", "g_vDetailBTransform", template.DetailB, template.DetailBUv, MaterialTextures.Grey );
		SetMap( material, shader, "g_tDetailMapC", "g_vDetailCTransform", template.DetailC, template.DetailCUv, MaterialTextures.Grey );
		SetMap( material, shader, "g_tBumpMap", "g_vBumpTransform", template.Bump, template.BumpUv, MaterialTextures.FlatNormal );
		SetMap( material, shader, "g_tAlphaMapA", "g_vAlphaATransform", template.AlphaMaskA, template.AlphaMaskAUv, MaterialTextures.White );
		SetMap( material, shader, "g_tAlphaMap", "g_vAlphaTransform", template.AlphaMask, template.AlphaMaskUv, MaterialTextures.White );
		SetMap( material, shader, "g_tSpecularMask", "g_vSpecularMaskTransform", template.SpecularMask, template.SpecularMaskUv, MaterialTextures.White );
		SetMap( material, shader, "g_tChangeColorMask", "g_vChangeColorMaskTransform", template.ChangeColorMask, template.ChangeColorMaskUv, MaterialTextures.Transparent );
		SetMap( material, shader, "g_tEnvMask", "g_vEnvMaskTransform", template.EnvMask, template.EnvMaskUv, MaterialTextures.White );
		SetMap( material, shader, "g_tSelfIllumMap", "g_vSelfIllumTransform", template.SelfIllum, template.SelfIllumUv, MaterialTextures.Transparent );
		SetMap( material, shader, "g_tSelfIllumMapB", "g_vSelfIllumBTransform", template.SelfIllumB, template.SelfIllumBUv, MaterialTextures.Transparent );
		SetMap( material, shader, "g_tSelfIllumDetail", "g_vSelfIllumDetailTransform", template.SelfIllumDetail, template.SelfIllumDetailUv, MaterialTextures.Grey );

		// The three channel glow's occlusion tint mask takes the second self-illumination detail's place.
		if ( template.OcclusionMask >= 0 )
			SetMap( material, shader, "g_tSelfIllumDetailB", "g_vSelfIllumDetailBTransform", template.OcclusionMask, template.OcclusionMaskUv, MaterialTextures.Transparent );
		else
			SetMap( material, shader, "g_tSelfIllumDetailB", "g_vSelfIllumDetailBTransform", template.SelfIllumDetailB, template.SelfIllumDetailBUv, MaterialTextures.Grey );

		var envMap = Load( shader, template.EnvMap );
		material.Set( "g_tEnvMap", envMap ?? MaterialTextures.Transparent );

		material.Set( "g_flClampU", setup.ClampU ? 1f : 0f );
		material.Set( "g_flAlbedoMode", (float)setup.Albedo );
		material.Set( "g_vAlbedoMultiply", Color( shader, template.AlbedoMultiply, Vector3.One ) );
		material.Set( "g_vAlbedoAdd", Color( shader, template.AlbedoAdd, Vector3.Zero ) );

		material.Set( "g_flGlossMode", (float)setup.Gloss );
		material.Set( "g_flSpecularGlossMode", setup.SpecularGloss is { } specularGloss ? (float)specularGloss : -1f );
		material.Set( "g_flGlossSquared", setup.GlossSquared ? 1f : 0f );
		material.Set( "g_flGlossGated", setup.GlossGated ? 1f : 0f );
		material.Set( "g_flBumpMode", (float)setup.Bump );

		// Objects have no lightmap to take a highlight from: theirs comes from their direct light, by the shader's
		// specular type, between the specular colour facing the eye and the grazing one.
		material.Set( "g_flLightmapType", objectLit ? 0f : (float)Math.Clamp( shader.LightmapType, 0, 3 ) );
		material.Set( "g_flObjectSpecularType", objectLit && template.SpecularColor >= 0 ? (float)Math.Clamp( shader.SpecularType, 0, 3 ) : 0f );
		material.Set( "g_flDynamicSpecularType", template.SpecularColor >= 0 ? (float)Math.Clamp( shader.SpecularType, 0, 3 ) : 0f );
		material.Set( "g_flLightmapSpecularBrightness", shader.LightmapSpecularBrightness );
		material.Set( "g_vSpecularColor", Color( shader, template.SpecularColor, Vector3.Zero ) );
		material.Set( "g_vSpecularColorGrazing", Color( shader, template.SpecularColorGrazing, Vector3.Zero ) );

		// Environment map. A layer of its own takes its colours from vertex constants, the combined one from pixel
		// constants; without a cube map there's nothing to add.
		var env = envMap is null ? OpaqueEnv.None : setup.Env;
		material.Set( "g_flEnvMode", (float)env );
		if ( env == OpaqueEnv.Combined )
		{
			material.Set( "g_vEnvPerpendicular", new Vector4( Color( shader, template.EnvTintPerpendicular, Vector3.One ), Byte( shader, template.EnvBrightnessPerpendicular, 3, 1 ) ) );
			material.Set( "g_vEnvParallel", new Vector4( Color( shader, template.EnvTintParallel, Vector3.One ), Byte( shader, template.EnvBrightnessParallel, 3, 1 ) ) );
		}
		else if ( env != OpaqueEnv.None )
		{
			material.Set( "g_vEnvPerpendicular", new Vector4( VertexColor( shader, template.EnvLayerPerpendicularColor ), VertexScalar( shader, template.EnvLayerPerpendicularBrightness ) ) );
			material.Set( "g_vEnvParallel", new Vector4( VertexColor( shader, template.EnvLayerParallelColor ), VertexScalar( shader, template.EnvLayerParallelBrightness ) ) );
			material.Set( "g_vSheenColor", Color( shader, template.SheenColor, Vector3.Zero ) );
			material.Set( "g_flSheenWidth", Byte( shader, template.SheenWidth, 3, 0 ) );
		}

		// PRT: the object's spherical harmonic light through the vertices' radiance transfer, scaled by c21.x, and a
		// highlight from the specular cube (the environment map slot) in the specular colour (c1, prt_scarab's
		// grazing colour c2). Only render models have radiance transfer in their vertices; level geometry (no PRT
		// shader is used there) would keep the lightmap.
		var prt = objectLit || (lightmap is null && !perVertexLightmap) ? setup.Prt : OpaquePrt.None;
		material.Set( "g_flPrtMode", (float)prt );
		if ( prt != OpaquePrt.None )
		{
			material.Set( "g_flPrtScale", shader.GetVertexConstant( template.PrtScale ) is { } prtScale ? prtScale.X : 1f );
		}

		material.Set( "g_flAlphaTest", (float)setup.AlphaTest );
		material.Set( "g_flAlphaTestBump", setup.AlphaTestBump ? 1f : 0f );

		// Self-illumination. Three channel glows keep each channel's intensity in one byte of its constant (red's
		// in red, green's in green, blue's in alpha) and its colour in another constant.
		material.Set( "g_flIllumMode", (float)setup.Illum );
		material.Set( "g_vSelfIllumColor", Color( shader, template.SelfIllumColor, Vector3.One ) );
		material.Set( "g_flSelfIllumDetailStrength", Byte( shader, template.SelfIllumDetailStrength, 3, 1 ) );
		material.Set( "g_vChannelR", new Vector4( Color( shader, template.ChannelRColor, Vector3.Zero ), Byte( shader, template.ChannelRIntensity, 0, 1 ) ) );
		material.Set( "g_vChannelG", new Vector4( Color( shader, template.ChannelGColor, Vector3.Zero ), Byte( shader, template.ChannelGIntensity, 1, 1 ) ) );
		material.Set( "g_vChannelB", new Vector4( Color( shader, template.ChannelBColor, Vector3.Zero ), Byte( shader, template.ChannelBIntensity, 3, 1 ) ) );
		material.Set( "g_vOcclusionTintR", Color( shader, template.OcclusionTintR, Vector3.One ) );
		material.Set( "g_vOcclusionTintG", Color( shader, template.OcclusionTintG, Vector3.One ) );
		material.Set( "g_vOcclusionTintB", Color( shader, template.OcclusionTintB, Vector3.One ) );
		material.Set( "g_flMeterValue", Byte( shader, template.MeterValue, 3, 0 ) );
		material.Set( "g_vMeterColorBelow", Color( shader, template.MeterColorBelow, Vector3.Zero ) );
		material.Set( "g_vMeterColorAbove", Color( shader, template.MeterColorAbove, Vector3.Zero ) );
		material.Set( "g_flPlasmaThreshold", Byte( shader, template.PlasmaThreshold, 3, 0 ) );
		material.Set( "g_vPlasmaBandColor", Color( shader, template.PlasmaBandColor, Vector3.Zero ) );

		SetAnimation( material, template, animation );

		// What follows the object's functions while a game plays: transforms and self-illumination constants by role.
		var driven = DrivenParameters.Find( stored.Tag, inputs, functions, values => RoleValues( template, new AnimatedShader( stored, values ).Shader ) )
			.Select( d => (d.Slot, d.Key.Kind, d.Key.Role, d.Table, Frame: System.Numerics.Vector4.Zero) ).ToList();

		// Maps whose frame follows a function (the battle rifle's ammunition digits): the bitmap's images as one atlas, the frame picked by the shader (kind 5).
		foreach ( var (slot, frame, count) in animation.ObjectFrames )
		{
			var function = functions?.ToList().FindIndex( f => string.Equals( f, frame.Input, StringComparison.OrdinalIgnoreCase ) ) ?? -1;
			var map = MapRoles.FirstOrDefault( m => m.Slot( template ) == slot );
			if ( function < 0 || map.Role == 0 || driven.Count >= DrivenParameters.MaxEntries || DrivenParameters.FrameAtlas( shader.GetBitmap( slot )?.Bitmap ) is not { } atlas )
				continue;

			material.Set( map.Texture, atlas );
			driven.Add( (function, 5, map.Role, DrivenParameters.FrameTable( frame, inputs ), new System.Numerics.Vector4( count, Math.Max( 0, shader.GetBitmap( slot ).Image ), 0, 0 )) );
		}

		DrivenParameters.Apply( material, driven );

		if ( levelLod )
		{
			material.Set( "g_flHaloLevel", 1f );
			SetLevelLod( material, shader, setup, env );
		}

		if ( objectLit )
		{
			material.Set( "g_flObjectLit", 1f );
			ObjectLightingTable.Bind( material );
		}

		return material;
	}

	/// <summary>
	/// The projected height scale of each shader LOD bias (shad +0x3C); bias 5 never leaves LOD0 (scratch research
	/// notes, bsp_lod.md).
	/// </summary>
	static readonly float[] LodBiasScales = [1, 0.25f, 0.5f, 2, 4, float.NaN, 1, 0];

	/// <summary>
	/// The template's layers a single pass port draws (as the generated table's passes): texture accumulation,
	/// lightmap, PRT, self-illumination, environment map.
	/// </summary>
	static readonly int[] DrawnLayers = [1, 3, 5, 7, 8];

	/// <summary>
	/// Level geometry's LOD switch: MCC draws a template's LOD1 (most often without its detail maps) where a part's
	/// projected size falls under the LOD0 threshold, which for parts with a small LOD magic number (big outer shells)
	/// is everywhere: Lockout's underside was blotchy with a dark detail map MCC never draws there. halo2_opaque.shader
	/// fades each part from LOD0 to LOD1 past MCC's switch (LevelLod); this hands it LOD1's modes. LOD2 (lightmap and
	/// fog only) draws as LOD1.
	/// </summary>
	/// <remarks>
	/// The environment map and self-illumination keep LOD0's kind when LOD1 has one (some templates only fold a
	/// separate layer into the lightmap pass at LOD1, with the same colours from other constants).
	/// </remarks>
	static void SetLevelLod( Material material, ShaderTag shader, OpaqueSetup lod0, OpaqueEnv env0 )
	{
		if ( shader.Template is null )
			return;

		var stem = new ShaderTemplateTag( shader.Template );
		var thresholds = stem.LodThresholds;
		var bias = shader.LodBias is >= 0 and < 8 ? LodBiasScales[shader.LodBias] : 1;
		if ( thresholds.Count < 2 || float.IsNaN( bias ) )
			return;

		var passes = DrawnLayers
			.SelectMany( layer => stem.Passes( layer, 0, 1 ) )
			.Select( p => p.Name[(p.Name.LastIndexOf( '\\' ) + 1)..] )
			.ToList();
		if ( OpaqueTemplates.Describe( passes ) is not { } lod1 )
			return;

		var env = lod1.Env == OpaqueEnv.None ? OpaqueEnv.None : env0;
		var illum = lod1.Illum == OpaqueIllum.None ? OpaqueIllum.None : lod0.Illum;
		material.Set( "g_vLod", new Vector4( thresholds[0], bias, 1, 0 ) );
		material.Set( "g_vLod1ModesA", new Vector4( (float)lod1.Albedo, (float)lod1.Gloss, lod1.SpecularGloss is { } specularGloss ? (float)specularGloss : -1f, (float)lod1.Bump ) );
		material.Set( "g_vLod1ModesB", new Vector4( (float)env, (float)illum, (float)lod1.AlphaTest, lod1.AlphaTestBump ? 1f : 0f ) );
		material.Set( "g_vLod1ModesC", new Vector4( lod1.GlossSquared ? 1f : 0f, lod1.GlossGated ? 1f : 0f, lod1.ClampU ? 1f : 0f, 0 ) );
	}

	/// <summary>
	/// halo2_opaque.shader's animation slots: texture transforms that scroll, self-illumination parameters that pulse.
	/// </summary>
	const int MaxUvCurves = 4, MaxConstantCurves = 6;

	/// <summary>
	/// The texture transforms by halo2_opaque.shader's role number (g_flUvAnim0Role): the template's transform each
	/// material transform comes from.
	/// </summary>
	static IEnumerable<(int Role, UvTransform Transform)> TransformRoles( OpaqueTemplate t ) =>
	[
		(1, t.BaseUv), (2, t.DetailUv), (3, t.DetailBUv), (4, t.DetailCUv), (5, t.BumpUv), (6, t.AlphaMaskAUv), (7, t.AlphaMaskUv),
		(8, t.SpecularMaskUv), (9, t.EnvMaskUv), (10, t.SelfIllumUv), (11, t.SelfIllumBUv), (12, t.SelfIllumDetailUv),
		(13, t.OcclusionMask >= 0 ? t.OcclusionMaskUv : t.SelfIllumDetailBUv),
	];

	/// <summary>
	/// The self-illumination parameters by halo2_opaque.shader's role number (g_vAnimConst0Target.x): colours (the
	/// slot's rgb into the parameter's rgb) and bytes (byte 0-3 r, g, b, a of the slot into one component).
	/// </summary>
	static IEnumerable<(int Role, int Slot, int Byte, int Component)> ConstantRoles( OpaqueTemplate t ) =>
	[
		(1, t.SelfIllumColor, -1, 4), (2, t.SelfIllumDetailStrength, 3, 0),
		(3, t.ChannelRColor, -1, 4), (3, t.ChannelRIntensity, 0, 3),
		(4, t.ChannelGColor, -1, 4), (4, t.ChannelGIntensity, 1, 3),
		(5, t.ChannelBColor, -1, 4), (5, t.ChannelBIntensity, 3, 3),
		(6, t.OcclusionTintR, -1, 4), (7, t.OcclusionTintG, -1, 4), (8, t.OcclusionTintB, -1, 4),
		(9, t.MeterValue, 3, 0), (10, t.MeterColorBelow, -1, 4), (11, t.MeterColorAbove, -1, 4),
		(12, t.PlasmaThreshold, 3, 0), (13, t.PlasmaBandColor, -1, 4),
	];

	/// <summary>
	/// Hands halo2_opaque.shader the time curves it evaluates: each scrolling vertex constant to every texture
	/// transform made from it (a whole 2D transform: its offsets), each pulsing pixel constant to every
	/// self-illumination parameter reading it. Curves past the shader's slots stay at time 0, as do pixel constants
	/// driving other parameters (none do in MCC's maps).
	/// </summary>
	static void SetAnimation( Material material, OpaqueTemplate template, AnimatedShader animation )
	{
		var uv = 0;
		foreach ( var curve in animation.UvCurves )
		{
			foreach ( var (role, transform) in TransformRoles( template ) )
			{
				if ( transform.Kind != UvKind.ScaleOffset || transform.Slot != curve.Slot || uv >= MaxUvCurves )
					continue;

				material.Set( $"g_vUvAnim{uv}UA", (Vector4)(curve.U?.A ?? default) );
				material.Set( $"g_vUvAnim{uv}UB", (Vector4)(curve.U?.B ?? default) );
				material.Set( $"g_vUvAnim{uv}VA", (Vector4)(curve.V?.A ?? default) );
				material.Set( $"g_vUvAnim{uv}VB", (Vector4)(curve.V?.B ?? default) );
				material.Set( $"g_flUvAnim{uv}Role", (float)role );
				uv++;
			}
		}

		var n = 0;
		foreach ( var curve in animation.ConstantCurves )
		{
			foreach ( var (role, slot, b, component) in ConstantRoles( template ) )
			{
				if ( slot != curve.Slot || n >= MaxConstantCurves )
					continue;

				// The values at the curve's ends, as the parameter takes them: a colour's rgb, or one byte's value
				// (in x; scalar curves have it in every component).
				System.Numerics.Vector4 minimum, maximum;
				if ( b < 0 && curve.Component is 4 or < 3 )
					(minimum, maximum) = (curve.Minimum, curve.Maximum);
				else if ( b >= 0 && curve.Component == b )
					(minimum, maximum) = (curve.Minimum, curve.Maximum);
				else if ( b is >= 0 and < 3 && curve.Component == 4 )
					(minimum, maximum) = (new( Channel( curve.Minimum, b ) ), new( Channel( curve.Maximum, b ) ));
				else
					continue;

				material.Set( $"g_vAnimConst{n}A", (Vector4)curve.Curve.A );
				material.Set( $"g_vAnimConst{n}B", (Vector4)curve.Curve.B );
				material.Set( $"g_vAnimConst{n}Min", (Vector4)minimum );
				material.Set( $"g_vAnimConst{n}Max", (Vector4)maximum );
				material.Set( $"g_vAnimConst{n}Target", new Vector3( role, b < 0 ? curve.Component : component, 1 ) );
				n++;
			}
		}
	}

	// The maps whose own transform halo2_opaque.shader applies (not the base map's, which every map's coordinates go through), by role.
	static readonly (int Role, Func<OpaqueTemplate, int> Slot, string Texture)[] MapRoles =
	[
		(2, t => t.Detail, "g_tDetailMap"), (3, t => t.DetailB, "g_tDetailMapB"), (4, t => t.DetailC, "g_tDetailMapC"), (5, t => t.Bump, "g_tBumpMap"),
		(6, t => t.AlphaMaskA, "g_tAlphaMapA"), (7, t => t.AlphaMask, "g_tAlphaMap"), (8, t => t.SpecularMask, "g_tSpecularMask"), (9, t => t.EnvMask, "g_tEnvMask"),
		(10, t => t.SelfIllum, "g_tSelfIllumMap"), (11, t => t.SelfIllumB, "g_tSelfIllumMapB"), (12, t => t.SelfIllumDetail, "g_tSelfIllumDetail"),
		(14, t => t.ChangeColorMask, "g_tChangeColorMask"),
	];

	// What halo2_opaque.shader's AnimatedTransform (kind 1) and AnimatedConstant (kind 2) receive by role for a shader's slots, as Create sets them.
	static Dictionary<(int Kind, int Role), System.Numerics.Vector4> RoleValues( OpaqueTemplate t, ShaderTag s )
	{
		var values = new Dictionary<(int, int), System.Numerics.Vector4>();
		foreach ( var (role, uv) in TransformRoles( t ).Append( (14, t.ChangeColorMaskUv) ) )
			values[(1, role)] = uv.Evaluate( s.VertexConstants );

		System.Numerics.Vector4 C( int slot, Vector3 fallback, float w = 0 )
		{
			var c = Color( s, slot, fallback );
			return new( c.x, c.y, c.z, w );
		}

		System.Numerics.Vector4 B( int slot, int component, float fallback ) => new( Byte( s, slot, component, fallback ), 0, 0, 0 );

		values[(2, 1)] = C( t.SelfIllumColor, Vector3.One );
		values[(2, 2)] = B( t.SelfIllumDetailStrength, 3, 1 );
		values[(2, 3)] = C( t.ChannelRColor, Vector3.Zero, Byte( s, t.ChannelRIntensity, 0, 1 ) );
		values[(2, 4)] = C( t.ChannelGColor, Vector3.Zero, Byte( s, t.ChannelGIntensity, 1, 1 ) );
		values[(2, 5)] = C( t.ChannelBColor, Vector3.Zero, Byte( s, t.ChannelBIntensity, 3, 1 ) );
		values[(2, 6)] = C( t.OcclusionTintR, Vector3.One );
		values[(2, 7)] = C( t.OcclusionTintG, Vector3.One );
		values[(2, 8)] = C( t.OcclusionTintB, Vector3.One );
		values[(2, 9)] = B( t.MeterValue, 3, 0 );
		values[(2, 10)] = C( t.MeterColorBelow, Vector3.Zero );
		values[(2, 11)] = C( t.MeterColorAbove, Vector3.Zero );
		values[(2, 12)] = B( t.PlasmaThreshold, 3, 0 );
		values[(2, 13)] = C( t.PlasmaBandColor, Vector3.Zero );
		return values;
	}

	static float Channel( System.Numerics.Vector4 v, int index ) => index switch { 0 => v.X, 1 => v.Y, 2 => v.Z, _ => v.W };

	/// <summary>
	/// For templates without an entry: the first bitmap that doesn't look like a bump, detail or utility map as the
	/// base map, untransformed.
	/// </summary>
	static OpaqueTemplate Guess( ShaderTag shader )
	{
		var baseSlot = -1;
		for ( var i = 0; i < shader.Bitmaps.Count && baseSlot < 0; i++ )
		{
			var path = shader.Bitmaps[i].Bitmap?.Path ?? "";
			if ( path.Length > 0 && !new[] { "bump", "vector", "detail", "default", "fade", "_bmp" }.Any( w => path.Contains( w, StringComparison.OrdinalIgnoreCase ) ) )
				baseSlot = i;
		}

		return new OpaqueTemplate { Passes = ["lightmap_tex"], Base = baseSlot };
	}

	static void SetMap( Material material, ShaderTag shader, string texture, string transform, int slot, UvTransform uv, Texture fallback )
	{
		material.Set( texture, Load( shader, slot ) ?? fallback );
		var t = uv.Evaluate( shader.VertexConstants );
		material.Set( transform, new Vector4( t.X, t.Y, t.Z, t.W ) );
	}

	static Texture Load( ShaderTag shader, int slot )
	{
		var bitmap = shader.GetBitmap( slot );
		if ( bitmap?.Bitmap is null || bitmap.Bitmap.Class != BitmapTag.Class )
			return null;

		var texture = Texture.Load( $"mount://halo2/{Halo2Mount.TextureName( bitmap.Bitmap.Path, Math.Max( 0, bitmap.Image ) )}.vtex" );
		return texture is null || texture.IsError ? null : texture;
	}

	/// <summary>
	/// A pixel constant's RGB (stored ARGB, a byte each) as 0-1.
	/// </summary>
	static Vector3 Color( ShaderTag shader, int slot, Vector3 fallback )
	{
		if ( slot < 0 || slot >= shader.PixelConstants.Count )
			return fallback;

		var argb = shader.PixelConstants[slot];
		return new Vector3( (argb >> 16 & 0xFF) / 255f, (argb >> 8 & 0xFF) / 255f, (argb & 0xFF) / 255f );
	}

	/// <summary>
	/// One byte of a pixel constant as 0-1: 0 red, 1 green, 2 blue, 3 alpha (the register component it lands in).
	/// </summary>
	static float Byte( ShaderTag shader, int slot, int component, float fallback )
	{
		if ( slot < 0 || slot >= shader.PixelConstants.Count )
			return fallback;

		var argb = shader.PixelConstants[slot];
		var shift = component switch { 0 => 16, 1 => 8, 2 => 0, _ => 24 };
		return (argb >> shift & 0xFF) / 255f;
	}

	static Vector3 VertexColor( ShaderTag shader, int slot ) =>
		shader.GetVertexConstant( slot ) is { } v ? new Vector3( v.X, v.Y, v.Z ) : Vector3.One;

	static float VertexScalar( ShaderTag shader, int slot ) =>
		shader.GetVertexConstant( slot ) is { } v ? v.W : 1f;
}
