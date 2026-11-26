using Sandbox.Rendering;

namespace Sandbox.Mounting.Blam.Halo2;

/// <summary>
/// Halo 2's bloom (MCC classic): a camera command list running halo2_bloom.shader's passes on the finished frame, before
/// s&amp;box's post-processing.
/// </summary>
/// <remarks>
/// <para>Halo's own chain (scratch research notes, bloom.md): a bright pass of the frame into a target 480 pixels tall,
/// bilinear copies down to 240 and 120, four 4-tap blurs between two 120-pixel targets, each reaching 1.25 times as far
/// as the last, then a screen blend of twice the blur times the brightness over the frame, all in gamma space as here.
/// Halo's targets are 8-bit; here they are 16-bit float (s&amp;box's 8-bit targets came back brightened, as if written
/// through the sRGB curve, which spread the glow far wider and brighter than Halo's).</para>
/// <para>Outside cinematics and a few campaign fog palette entries, every value is compiled into the game: threshold
/// 0.9, brightness 0.375 (a time curve reset to 0.5 and 0.5 at map load and never moved), blur amount 3.992 (four
/// passes) and box factor 0.25. Halo's other bloom input, a mask in the frame's alpha, comes only from the first
/// person view's bloom layers, which no Halo 2 scene here has.</para>
/// <para>s&amp;box sizes a command list's targets as fractions of the view, so they are half, a quarter and an eighth
/// of it; the shader takes its tap offsets from Halo's sizes.</para>
/// <para>MCC's bright pass sees its frame about 1.3 times as bright as it ends up on screen (<see cref="InputScale"/>):
/// measured live on Lockout with the threshold poked from 0.7 to 1.0 on two views, MCC's bloom matched this chain only
/// with the frame scaled by 1.3 first (by 1.0 there was next to no glow, where MCC's screen blend added 1-11 levels on
/// average, and some even at threshold 1.0). Why its frame is brighter there is unknown; flat colours on screen are
/// 0.97 times their value, so it isn't the final output.</para>
/// </remarks>
static class Bloom
{
	public const string ShaderPath = "shaders/halo2_bloom.shader";

	/// <summary>
	/// How much brighter MCC's bloom sees the frame than the screen shows it (see the remarks).
	/// </summary>
	const float InputScale = 1.3f;

	const float Threshold = 0.9f;
	const float Brightness = 0.375f;
	const int BlurPasses = 4;
	const float BoxFactor = 0.25f;

	enum Pass { Bright, Copy, Blur, Composite }

	static Material _material;

	/// <summary>
	/// The command list for a camera, to run at <see cref="Stage.BeforePostProcess"/>; null if the shader can't be loaded.
	/// </summary>
	public static CommandList Build()
	{
		_material ??= Material.Create( "halo2/bloom.vmat", ShaderPath );
		if ( _material is null )
			return null;

		// Only in views with post-processing: not in the open water's reflection cube, and off with the editor's toggle.
		var commands = new CommandList( "Halo 2 bloom" ) { Flags = CommandList.Flag.PostProcess };
		var frame = commands.Attributes.GrabFrameTexture( "HaloBloomFrame" );
		var bright = commands.GetRenderTarget( "halo2_bloom_480", 2, ImageFormat.RGBA16161616F, ImageFormat.None );
		var half = commands.GetRenderTarget( "halo2_bloom_240", 4, ImageFormat.RGBA16161616F, ImageFormat.None );
		var a = commands.GetRenderTarget( "halo2_bloom_120a", 8, ImageFormat.RGBA16161616F, ImageFormat.None );
		var b = commands.GetRenderTarget( "halo2_bloom_120b", 8, ImageFormat.RGBA16161616F, ImageFormat.None );

		commands.Attributes.Set( "HaloBloomInputScale", InputScale );
		commands.Attributes.Set( "HaloBloomThreshold", Threshold );
		commands.Attributes.Set( "HaloBloomBrightness", Brightness );

		Draw( commands, Pass.Bright, frame, bright );
		Draw( commands, Pass.Copy, bright, half );
		Draw( commands, Pass.Copy, half, a );
		for ( var k = 0; k < BlurPasses; k++ )
		{
			commands.Attributes.Set( "HaloBloomScale", MathF.Pow( 1 + BoxFactor, k ) );
			Draw( commands, Pass.Blur, k % 2 == 0 ? a : b, k % 2 == 0 ? b : a );
		}

		// Four passes end where they started.
		commands.ClearRenderTarget();
		commands.Attributes.Set( "HaloBloomSource", a.ColorTexture );
		commands.Attributes.Set( "HaloBloomPass", (int)Pass.Composite );
		commands.Blit( _material );

		commands.ReleaseRenderTarget( bright );
		commands.ReleaseRenderTarget( half );
		commands.ReleaseRenderTarget( a );
		commands.ReleaseRenderTarget( b );
		return commands;
	}

	static void Draw( CommandList commands, Pass pass, RenderTargetHandle source, RenderTargetHandle target )
	{
		commands.ResourceBarrierTransition( target, ResourceState.RenderTarget );
		commands.SetRenderTarget( target );
		commands.Attributes.Set( "HaloBloomSource", source.ColorTexture );
		commands.Attributes.Set( "HaloBloomPass", (int)pass );
		commands.Blit( _material );
		commands.ResourceBarrierTransition( target, ResourceState.PixelShaderResource );
	}
}
