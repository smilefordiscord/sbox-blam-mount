using Blam.Halo3;
using Blam.Halo3.Tags;

namespace Sandbox.Mounting.Blam.Halo3;

// Materials for the decals of decal systems (decs): each decal's render method (shaders\decal) is embedded in the decs, so it goes through Standard here rather than Create's tag cache.
static partial class Halo3Materials
{
	// Multiply decals with a plain base map are drawn as the surface under them with the decal in its albedo rather than over the lit frame: MCC multiplies its albedo buffer with them before lighting, so the surface's specular and fog stay. Post-lighting ones too (Sandtrap's Behemoth stains, post_lighting, draw into the albedo buffer with the pre-lighting decals).
	public static bool IsAlbedoDecal( DecalSystemEntry decal ) => IsAlbedoDecal( decal.RenderMethod );

	public static bool IsAlbedoDecal( ShaderTag tag ) => tag is { Kind: "decal" } && tag.Definition is not null
		&& tag.Option( "blend_mode" ) is "multiply" or "double_multiply"
		&& tag.Option( "tinting" ) is null or "none" && !DecalAlbedo.NeedsComposite( tag ) && tag.Texture( "base_map" ) is not null;

	// A level surface's material (a copy) with such a decal as its albedo layer 0, 1 or 2 (halo3_shader's H3DecalAlbedo), pulled toward the camera so it draws over the surface itself.
	public static void AddDecalAlbedo( Material surface, int layer, DecalSystemEntry decal ) => AddDecalAlbedo( surface, layer, decal.RenderMethod );

	public static void AddDecalAlbedo( Material surface, int layer, ShaderTag tag )
	{
		SetTexture( surface, $"DecalAlbedoMap{layer}", tag.Texture( "base_map" ), tag );
		surface.Set( $"g_flDecalAlbedo{layer}", tag.Option( "blend_mode" ) == "double_multiply" ? 2f : 1f );
		// Half the pull of the decals drawn over the frame (the alpha blended cracks over Zanzibar's streak), which MCC draws after these and which z-fought them at the same pull.
		surface.Set( "g_flDecalPull", DecalProjector.DepthPull / 2 );
	}

	// A render model's multiply decal in a copy of the surface material under it: its uv in the second uv set (objects light by probe, so it's free), the surface's in the first.
	public static void AddModelDecalAlbedo( Material surface, ShaderTag decal )
	{
		AddDecalAlbedo( surface, 0, decal );
		surface.Set( "g_flDecalAlbedoUv1", 1f );
		surface.Set( "g_flDecalKill", 1f );
	}

	// Pre-lighting decals only change the albedo MCC lights the surface with, so the surface's material model, specular and diffuse coefficient stay; drawn over the lit frame here, such a decal takes them from the surface under it.
	public static bool LitAsSurface( ShaderTag decal ) => decal is { Kind: "decal" } && decal.Option( "render_pass" ) is null or "pre_lighting";

	static readonly HashSet<string> SurfaceLightingReals =
	[
		"diffuse_coefficient", "specular_coefficient", "roughness", "albedo_blend", "fresnel_color", "specular_tint", "analytical_specular_contribution", "area_specular_contribution",
		"analytical_anti_shadow_control", "normal_specular_power", "normal_specular_tint", "glancing_specular_power", "glancing_specular_tint", "fresnel_curve_steepness",
		"fresnel_coefficient", "fresnel_curve_bias", "albedo_specular_tint_blend", "rim_fresnel_coefficient", "rim_fresnel_color", "rim_fresnel_power", "rim_coefficient", "rim_tint", "rim_power",
	];

	// The surface's lighting terms on a pre-lighting decal's material (a terrain gives its first material's diffuse coefficient).
	public static void LightLike( Material material, ShaderTag decal, ShaderTag surface )
	{
		if ( !LitAsSurface( decal ) || surface?.Definition is null )
			return;
		if ( surface.Kind == "terrain" )
		{
			material.Set( "g_flDiffuseCoefficient", surface.Scalar( "diffuse_coefficient_m_0", 1 ) );
			return;
		}
		material.Set( "g_flMaterialModel", (float)MaterialModel( surface ) );
		foreach ( var (real, parameter, components) in Reals )
		{
			if ( SurfaceLightingReals.Contains( real ) && surface.Real( real ) is { } v )
				SetReal( material, parameter, components, v );
		}
	}

	// A decal's material, lit like the surface under it (its BSP material, null for the decal's own terms); null if its render method isn't drawn. Not cached: Halo3DecalBuilder shares them per scene.
	public static Material CreateDecal( string name, DecalSystemEntry decal, Halo3Lighting lighting, Halo3Lightmap lightmap, ShaderTag surface = null )
	{
		var tag = decal.RenderMethod;
		if ( tag?.Definition is null || tag.Kind != "decal" )
			return null;

		Material material;
		try
		{
			material = Standard( name, tag );
			if ( material is null )
				return null;
			LightLike( material, tag, surface );

			// Options reading more than the base map (alpha maps, palettes, vector maps) come baked into one base map.
			if ( DecalAlbedo.NeedsComposite( tag ) )
			{
				if ( DecalAlbedo.Compose( tag ) is not { } image )
					return null;
				var (data, levels) = image.MipChain();
				var texture = Texture.Create( image.Width, image.Height, ImageFormat.RGBA8888 )
					.WithName( $"{name}_albedo" ).WithData( data ).WithMips( levels ).WithStaticUsage().Finish();
				material.Set( "g_tBaseMap", texture );
				material.Set( "g_flBaseMapSrgb", image.Srgb ? 1f : 0f );
				material.Set( "g_vBaseMapXform", new Vector4( 1, 1, 0, 0 ) );
				material.Set( "g_vBaseMapScroll", Vector4.Zero );

				// alpha_mask decals tile their base (baked above) and their bump map.
				if ( tag.Option( "albedo" ) == "diffuse_plus_alpha_mask" && tag.Texture( "bump_map" ) is { } bump )
				{
					var t = bump.Transform;
					material.Set( "g_vBumpMapXform", new Vector4( t.X * tag.Scalar( "u_tiles", 1 ), t.Y * tag.Scalar( "v_tiles", 1 ), t.Z, t.W ) );
				}
			}

			// The composite isn't premultiplied: blend it as alpha.
			if ( tag.Option( "blend_mode" ) == "pre_multiplied_alpha" )
				material.SetFeature( "F_BLEND_MODE", 1 );
		}
		catch ( Exception e )
		{
			Halo3Mount.Current?.Logger.Warning( e, $"Decal material {name}: {e.Message}" );
			return null;
		}

		SetLighting( material, lighting, lightmap );
		material.Set( "g_flDecalPull", DecalProjector.DepthPull );
		material.Set( "g_flExposureStops", CameraExposure( tag.Tag.Cache ) );
		if ( (tag.Flags & 3) == 0 && Atmosphere( tag.Tag.Cache ) is { } fog )
			fog.Apply( material );
		return material;
	}
}
