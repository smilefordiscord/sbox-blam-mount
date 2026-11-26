using Blam.Halo2;
using Blam.Halo2.Tags;
using MaterialTextures = Sandbox.Mounting.Blam.Halo1.MaterialTextures;

namespace Sandbox.Mounting.Blam.Halo2;

/// <summary>
/// Builds s&amp;box materials for Halo 2's water shader templates (halo2_water.shader): pools on level geometry and
/// the open water of a BSP's water definition (<see cref="WaterDefinition"/>), which also supplies the constants
/// every water shader in the BSP gets (bump scales, sun, horizon colour, fog).
/// </summary>
/// <remarks>
/// The textures are routed by the template's pass: stage 0 the bump flipbook (all of the bump bitmap's frames as
/// one atlas, <see cref="BitmapTextureLoader.LoadFlipbook"/>), stage 1 the reflection bitmap or Halo's
/// water_reflection target, stage 2 the static texture or Halo's water_refraction target (the frame buffer copy),
/// stage 3 the fresnel bitmap. The shore's edge blend (water_edge_blend strips writing frame buffer alpha) isn't
/// drawn; the shader fades open water by its depth instead (<see cref="ShoreDepth"/>).
/// </remarks>
static class WaterMaterial
{
	public const string ShaderPath = "shaders/halo2_water.shader";

	/// <summary>
	/// How deep (world units) open water gets before it's fully drawn: just enough to soften the line where the plane
	/// meets the shore. Zanzibar's edge strips cover only a few stretches; elsewhere Halo leaves that line to the
	/// breaking waves drawn over it, and MCC's shallows show no sand through the water.
	/// </summary>
	const float ShoreDepth = 0.05f;

	/// <summary>
	/// The programs of halo2_water.shader (g_flProgram).
	/// </summary>
	enum Program { Pool, PoolCube, PoolStatic, PoolStaticCube, WaterStatic, Ocean, OceanBlend }

	/// <summary>
	/// Template (below shaders\shader_templates\) → program.
	/// </summary>
	static readonly Dictionary<string, Program> Templates = new( StringComparer.OrdinalIgnoreCase )
	{
		[@"water\water_pool"] = Program.Pool,
		[@"water\water_pool_cube"] = Program.PoolCube,
		[@"water\water_pool_static"] = Program.PoolStatic,
		[@"water\water_pool_static_cube"] = Program.PoolStaticCube,
		[@"water\water_static"] = Program.WaterStatic,
		[@"water\ocean_static"] = Program.Ocean,
		[@"water\ocean_static_opaque"] = Program.Ocean,
		[@"water\ocean_static_opaque_blend"] = Program.OceanBlend,
	};

	// Bump flipbooks by bitmap tag, built once per mount.
	static readonly Dictionary<string, Texture> Flipbooks = new( StringComparer.OrdinalIgnoreCase );
	static Halo2Mount _flipbookMount;

	/// <summary>
	/// The template's water pass (pools draw in the transparent layer, open water in the lightmap layer), or null.
	/// </summary>
	static ShaderTemplatePass FindPass( ShaderTag shader )
	{
		if ( shader.Template is null || !Templates.ContainsKey( shader.TemplateName ) )
			return null;

		var template = new ShaderTemplateTag( shader.Template );
		return template.Passes( ShaderTemplateTag.TransparentLayer ).Concat( template.Passes( 3 ) )
			.FirstOrDefault( p => p.Name.StartsWith( @"water\", StringComparison.OrdinalIgnoreCase ) );
	}

	public static bool IsSupported( ShaderTag shader ) => FindPass( shader ) is not null;

	/// <summary>
	/// Whether the shader is open water (drawn on a water definition's plane, reflecting the scene).
	/// </summary>
	public static bool IsOpenWater( ShaderTag shader ) =>
		Templates.TryGetValue( shader.TemplateName ?? "", out var program ) && program >= Program.WaterStatic;

	/// <param name="water">The BSP's water definition (its constants), or null for Halo's defaults.</param>
	/// <param name="depthWrite">Write depth (the open water plane, see <see cref="StructureBspModel.BuildOpenWater"/>).</param>
	/// <param name="refraction">What open water shows through it instead of the frame (rgb 0-1, gamma), or null for the
	/// frame. MCC's water_refraction for open water holds no scene, only the sky's clear colour: with the fresnel bitmap
	/// swapped for alpha 0, Zanzibar's sea is one flat colour, the clear colour plus the sun glint, and no seabed shows
	/// even in the shallows.</param>
	public static Material Create( string name, ShaderTag shader, WaterDefinition water, bool depthWrite = false, Vector3? refraction = null )
	{
		var pass = FindPass( shader );
		if ( pass is null )
			return null;

		var material = Material.Create( name, ShaderPath );
		if ( material is null )
			return null;

		var program = Templates[shader.TemplateName];
		material.SetFeature( "F_TWO_SIDED", pass.IsTwoSided ? 1 : 0 );
		material.SetFeature( "F_DEPTH_WRITE", depthWrite ? 1 : 0 );
		material.Set( "g_flProgram", (float)program );
		material.Set( "g_flWorldScale", Halo2Mount.WorldScale );

		var parameters = new ShaderPassParameters( shader, pass );
		var bumpSlot = parameters.Stages[0].BitmapSlot;
		var bump = bumpSlot >= 0 ? shader.GetBitmap( bumpSlot ) : null;
		var frames = bump?.Bitmap is { } bumpTag && bumpTag.Class == BitmapTag.Class ? new BitmapTag( bumpTag ).Bitmaps : [];
		material.Set( "g_tBump", Flipbook( bump?.Bitmap, frames ) ?? MaterialTextures.White );
		material.Set( "g_vBumpFormat", frames.FirstOrDefault()?.Format switch
		{
			BitmapFormat.V8U8 => new Vector3( 1, 0, 0 ),
			BitmapFormat.G8B8 => new Vector3( 1, 0, 1 ),
			_ => Vector3.Zero,
		} );

		// The flipbook: the bump bitmap's transform part 0 (its frame) follows a time curve.
		var frame = ShaderAnimation.Read( shader.Tag, pass.TemplateImplementation )
			.Where( p => p.Kind == ShaderAnimatedKind.BitmapTransform )
			.SelectMany( p => p.Overlays )
			.FirstOrDefault( o => o.Part == 0 && o.Overlay.IsTime && o.Overlay.Function.Type != 1 && o.Overlay.Period > 0 );
		material.Set( "g_vFlipbook", frame.Overlay is { } overlay
			? new Vector4( Math.Max( frames.Count, 1 ), overlay.Period, overlay.Function.Minimum, overlay.Function.Maximum )
			: new Vector4( Math.Max( frames.Count, 1 ), 0, 0, 0 ) );

		// Stage 1: a reflection bitmap (pools); open water reads its mirror instead. Stage 2: the static texture
		// (static pools, the ocean plane). Stage 3: the fresnel bitmap.
		material.Set( "g_tReflection", TemplatePassParameters.Load( shader, parameters.Stages[1].BitmapSlot ) ?? MaterialTextures.Transparent );
		material.Set( "g_tStatic", TemplatePassParameters.Load( shader, parameters.Stages[2].BitmapSlot ) ?? MaterialTextures.White );
		material.Set( "g_tFresnel", TemplatePassParameters.Load( shader, parameters.Stages[3].BitmapSlot ) ?? MaterialTextures.White );

		// c20 (bump, from world units) and c18 (the static pools' mesh UV), with their time curves.
		SetXform( material, "Bump", parameters, 2 );
		SetXform( material, "Static", parameters, 0 );

		water ??= new WaterDefinition( null, default, default, default, default, 32, 0.5f, 0.05f, 0.15f, 0, 0, 0, 0, 0, 0, 0, default, default, 0, 0, 0 );
		material.Set( "g_vBumpScales", new Vector4( water.ReflectionBumpScale, water.RefractionBumpScale, water.FresnelScale, water.RotateOffset ) );
		material.Set( "g_vSunDirection", new Vector3(
			MathF.Cos( water.SunHeading ) * MathF.Sin( water.SunPitch ),
			MathF.Sin( water.SunHeading ) * MathF.Sin( water.SunPitch ),
			MathF.Cos( water.SunPitch ) ) );
		material.Set( "g_vSunColor", (Vector3)water.SunSpotColor );
		material.Set( "g_vHorizonColor", (Vector3)water.HorizonColor );

		var fogRange = water.FogFar - water.FogNear;
		material.Set( "g_vFog", water.FarZ < 0.0001f || water.FogNear < 0.0001f || water.FogFar < 0.0001f || fogRange < 0.0001f
			? Vector2.Zero
			: new Vector2( 1 / fogRange, -water.FogNear / fogRange ) );

		// Halo's c22: the plane texture's centre (y negated) and inverse half size; an unused size stays 0.
		material.Set( "g_vOceanPlane", new Vector4( water.Center.X, -water.Center.Y,
			water.Extents.X < 0.0001f ? water.Extents.X : 1 / water.Extents.X,
			water.Extents.Y < 0.0001f ? water.Extents.Y : 1 / water.Extents.Y ) );
		material.Set( "g_flShoreDepth", ShoreDepth );
		material.Set( "g_vRefractionColor", new Vector4( refraction ?? Vector3.Zero, refraction is null ? 0 : 1 ) );
		return material;
	}

	static void SetXform( Material material, string name, ShaderPassParameters parameters, int register )
	{
		material.Set( $"g_v{name}Xform", (Vector4)parameters.VertexRegisters[register] );
		var (u, v) = parameters.VertexCurves.GetValueOrDefault( register );
		material.Set( $"g_v{name}AnimUA", (Vector4)(u?.A ?? default) );
		material.Set( $"g_v{name}AnimUB", (Vector4)(u?.B ?? default) );
		material.Set( $"g_v{name}AnimVA", (Vector4)(v?.A ?? default) );
		material.Set( $"g_v{name}AnimVB", (Vector4)(v?.B ?? default) );
	}

	static Texture Flipbook( TagEntry tag, IReadOnlyList<BitmapData> frames )
	{
		if ( tag is null || frames.Count == 0 )
			return null;

		lock ( Flipbooks )
		{
			if ( _flipbookMount != Halo2Mount.Current )
			{
				Flipbooks.Clear();
				_flipbookMount = Halo2Mount.Current;
			}

			if ( !Flipbooks.TryGetValue( tag.Path, out var texture ) )
				Flipbooks[tag.Path] = texture = BitmapTextureLoader.LoadFlipbook( $"{Halo2Mount.ResourcePath( tag.Path )}_flipbook", frames );

			return texture;
		}
	}
}
