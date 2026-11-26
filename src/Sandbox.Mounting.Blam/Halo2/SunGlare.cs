using Sandbox.Rendering;

namespace Sandbox.Mounting.Blam.Halo2;

/// <summary>
/// Halo 2's sun glare (MCC classic): the crisp disc over a level's sun, a camera command list running
/// halo2_sun_glare.shader's passes after the bloom (<see cref="Bloom"/>) and before the brightness pass.
/// </summary>
/// <remarks>
/// <para>The lens flare (<see cref="LensFlareModels.AddSuns"/>) draws the sun's corona and its ghosts; the disc is a pass
/// of its own (scratch research notes, overnight/effects/sun_glare_re.md; live: the gate byte off took the disc away on
/// Dune and left a frame like the mount's). Halo marks the pixels of a square around the sun that show sky, draws
/// rasterizer\glow's cone into them, stretches the square into a small target, blurs it four times inside the visible
/// part, and adds 16 growing copies of it over the frame.</para>
/// <para>Halo's square targets are 2/15 of the screen tall (96 pixels at 720 lines); here they are 96 whatever the view,
/// so the blur reaches as far across the disc as MCC's does at 720 lines.</para>
/// </remarks>
static class SunGlare
{
	public const string ShaderPath = "shaders/halo2_sun_glare.shader";

	/// <summary>
	/// The order among the commands before post-processing: after the bloom (0), before MCC's brightness (1000).
	/// </summary>
	public const int Order = 500;

	const int TargetSize = 96;
	const int BlurPasses = 4;

	enum Pass { Mask, Blur, Composite }

	static Material _material;

	/// <summary>
	/// The command list for a level's sun; null if the shader can't be loaded.
	/// </summary>
	public static CommandList Build( Vector3 direction, float skyFogDensity )
	{
		_material ??= Material.Create( "halo2/sun_glare.vmat", ShaderPath );
		if ( _material is null )
			return null;

		var glow = Texture.Load( $"mount://halo2/{Halo2Mount.TextureName( @"rasterizer\glow", 0 )}.vtex" );

		// Only in views with post-processing, as the bloom: not in the open water's reflection cube.
		var commands = new CommandList( "Halo 2 sun glare" ) { Flags = CommandList.Flag.PostProcess };
		commands.Attributes.Set( "HaloSunDirection", direction.Normal );
		commands.Attributes.Set( "HaloSunSkyFogClear", Math.Clamp( 1 - skyFogDensity, 0, 1 ) );
		commands.Attributes.Set( "HaloSunGlow", glow is { IsError: false } ? glow : Texture.White );

		var a = commands.GetRenderTarget( "halo2_sun_glare_a", TargetSize, TargetSize, ImageFormat.RGBA16161616F, ImageFormat.None, MultisampleAmount.MultisampleNone, 1 );
		var b = commands.GetRenderTarget( "halo2_sun_glare_b", TargetSize, TargetSize, ImageFormat.RGBA16161616F, ImageFormat.None, MultisampleAmount.MultisampleNone, 1 );

		Draw( commands, Pass.Mask, a );
		for ( var k = 0; k < BlurPasses; k++ )
		{
			// Twice the corners' mean on the first pass, about once after.
			commands.Attributes.Set( "HaloSunBlurWeight", k == 0 ? 1f : 127f / 255f );
			commands.Attributes.Set( "HaloSunSource", (k % 2 == 0 ? a : b).ColorTexture );
			Draw( commands, Pass.Blur, k % 2 == 0 ? b : a );
		}

		// Four passes end where they started.
		commands.ClearRenderTarget();
		commands.Attributes.GrabFrameTexture( "HaloSunFrame" );
		commands.Attributes.Set( "HaloSunSource", a.ColorTexture );
		commands.Attributes.Set( "HaloSunPass", (int)Pass.Composite );
		commands.Blit( _material );

		commands.ReleaseRenderTarget( a );
		commands.ReleaseRenderTarget( b );
		return commands;
	}

	static void Draw( CommandList commands, Pass pass, RenderTargetHandle target )
	{
		commands.ResourceBarrierTransition( target, ResourceState.RenderTarget );
		commands.SetRenderTarget( target );
		commands.Attributes.Set( "HaloSunPass", (int)pass );
		commands.Blit( _material );
		commands.ResourceBarrierTransition( target, ResourceState.PixelShaderResource );
	}
}
