using Blam.Halo1;
using Blam.Halo1.Tags;

namespace Sandbox.Mounting.Blam.Halo1;

/// <summary>
/// How a Halo particle's sprite is drawn by <see cref="ParticleSprites"/>: its blend function,
/// nonlinear tint, zsprite depth map, minimum and smallest drawn sizes and random mirroring in
/// halo1_sprite.shader. Registered as
/// <c>mount://haloce/&lt;particle tag&gt;__sprite.vmat</c> for particle tags and
/// <c>&lt;particle system tag&gt;__sprite_&lt;type&gt;_&lt;particle state&gt;.vmat</c> for particle system
/// states, so effect prefabs can refer to it. The sprite's frames come from the particle's sprite
/// renderer, not the material.
/// </summary>
static class ParticleSpriteMaterial
{
	public const string ShaderPath = "shaders/halo1_sprite.shader";

	public static string ResourceName( string tagPath ) => $"{HaloCeMount.ResourcePath( tagPath )}__sprite";

	public static string ResourceName( string tagPath, int type, int state ) => $"{HaloCeMount.ResourcePath( tagPath )}__sprite_{type}_{state}";

	public static string FullPath( string tagPath ) => $"mount://{EffectPrefabs.Ident}/{ResourceName( tagPath )}.vmat";

	public static string FullPath( string tagPath, int type, int state ) => $"mount://{EffectPrefabs.Ident}/{ResourceName( tagPath, type, state )}.vmat";

	/// <param name="zmap">A zsprite's depth map, or null for a plain sprite.</param>
	/// <param name="zspriteRadiusScale">How many particle radii the depth map reaches toward the viewer (0 means 1).</param>
	/// <param name="part">The particle tag, for its minimum and smallest drawn sizes and random
	/// mirroring; null for a particle system state, which has none.</param>
	public static Material Create( HaloCeMount host, string name, FramebufferBlendFunction blend, bool nonlinearTint, TagEntry zmap, float zspriteRadiusScale, ParticleTag part = null )
	{
		var material = Material.Create( name, ShaderPath );
		if ( material is null )
			return null;

		material.SetFeature( "F_BLEND_MODE", (int)blend );
		material.Set( "g_flNonlinearTint", nonlinearTint ? 1f : 0f );

		var zmapTexture = zmap is null ? null : MaterialTextures.Load( host, material, zmap, 0 );
		material.SetFeature( "F_ZSPRITE", zmapTexture is null ? 0 : 1 );
		material.Set( "g_tZMap", zmapTexture ?? MaterialTextures.Transparent );

		// The particle's radius is taken as half its sprite's height, which it is for a sprite as
		// wide as the one the radius is measured against.
		material.Set( "g_flZSpriteScale", zmapTexture is null ? 0f : (zspriteRadiusScale > 0 ? zspriteRadiusScale : 1) / 2 );

		material.Set( "g_flHaloSpriteMinimumSize", MathF.Max( part?.MinimumSize ?? 0, 0 ) );
		material.Set( "g_vHaloSpriteFadeSize", part is null ? Vector2.Zero : new Vector2( MathF.Max( part.FadeSize.X, 0 ), MathF.Max( part.FadeSize.Y, 0 ) ) );
		material.Set( "g_vHaloSpriteMirror", new Vector2( part?.RandomHorizontalMirroring == true ? 1 : 0, part?.RandomVerticalMirroring == true ? 1 : 0 ) );
		return material;
	}
}

/// <summary>
/// A particle tag's sprite material, or with <paramref name="type"/> a particle system state's.
/// </summary>
sealed class ParticleSpriteMaterialLoader( string mapPath, uint tagId, int type = -1, int state = -1 ) : HaloCeLoader
{
	protected override object Load()
	{
		var cache = Host.GetCache( mapPath );
		var tag = cache.GetTag( tagId );
		if ( tag is null )
			return null;

		if ( type < 0 )
		{
			var part = new ParticleTag( cache, tag );
			return ParticleSpriteMaterial.Create( Host, Path, part.BlendFunction, part.NonlinearTint, part.ZSprite ? part.SecondaryBitmap : null, part.ZSpriteRadiusScale, part );
		}

		var types = new ParticleSystemTag( cache, tag ).Types;
		if ( type >= types.Count || state >= types[type].ParticleStates.Count )
			return null;

		var s = types[type].ParticleStates[state];
		return ParticleSpriteMaterial.Create( Host, Path, s.BlendFunction, s.NonlinearTint, s.ZSprite ? s.SecondaryBitmap : null, s.ZSpriteRadiusScale );
	}
}
