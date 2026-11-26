using Blam.Halo3.Tags;
using Sandbox.Rendering;

namespace Sandbox.Mounting.Blam.Halo3;

// Halo 3's post-processing on a camera: MCC's bloom, auto exposure and final composite (halo3_postfx.shader), in the order Halo 3 runs them, with Halo 3's constants.
// While it runs, Halo 3 materials write the exposed colour unclamped (Halo3Hdr), the value MCC's LDR and HDR targets both hold, at the exposure this keeps in State; the composite turns the frame into the finished display colour.
// Not ported because MCC PC doesn't show them: lightshafts (they run but add zero), bling (render_bling is 0), SSAO (ssao_enable is 0). FXAA is left to s&box's anti-aliasing.
sealed class Halo3PostFx
{
	public const string ShaderPath = "shaders/halo3_postfx.shader";

	// What halo3_shader and the other Halo 3 shaders read while the pass is on the camera.
	public const string HdrAttribute = "Halo3Hdr";
	public const string StateAttribute = "Halo3ExposureStateIndex";

	enum Pass { Bright, Downsample, Measure, State, Copy, BlurHorizontal, BlurVertical, Add, AddAlpha, Composite, MotionBlur, Underwater }

	static Material _material;

	Texture _state;
	Texture _previousState;

	// What changes from frame to frame (halo3_postfx.shader's g_tParams): the hue/saturation matrix rows, ( contrast, exposure boost, 0, 0 ), then motion blur's ( time scale, on, crosshair ), ( max, scale ), ( taps, centre falloff ).
	const int ParamTexels = 10;
	readonly float[] _paramData = new float[ParamTexels * 4];
	Texture _params;

	// The auto exposure state the materials read (texel 0's x = stops; g_exposure.x = 2^stops / 1.4938), kept from frame to frame with the shader's target history.
	public Texture State => _state;

	const int StateWidth = 64;

	// The command list for a camera, to run at Stage.BeforePostProcess; null if the shader can't be loaded. startStops: where auto exposure starts from when the state is new.
	public CommandList Build( BloomSettings bloom, float startStops, Texture exposureWeights, Halo3Distortion distortion = null )
	{
		_material ??= Material.Create( "halo3/postfx.vmat", ShaderPath );
		if ( _material is null )
			return null;

		if ( _params is null )
		{
			Update( PostFrame.Neutral );
			_params = Texture.Create( ParamTexels, 1, ImageFormat.RGBA32323232F ).WithData( new ReadOnlySpan<float>( _paramData ) ).Finish();
		}
		_state ??= Texture.CreateRenderTarget( "halo3 exposure", ImageFormat.RGBA16161616F, new Vector2( StateWidth, 1 ) );
		_previousState ??= Texture.CreateRenderTarget( "halo3 exposure last", ImageFormat.RGBA16161616F, new Vector2( StateWidth, 1 ) );

		var commands = new CommandList( "Halo 3 post-processing" ) { Flags = CommandList.Flag.PostProcess };
		var frame = commands.Attributes.GrabFrameTexture( "Halo3PostFrame" );
		var format = ImageFormat.RGBA16161616F;
		var ldr = commands.GetRenderTarget( "halo3_post_ldr", 1, format, ImageFormat.None );
		var bright = commands.GetRenderTarget( "halo3_post_4a", 4, format, ImageFormat.None );
		var work = commands.GetRenderTarget( "halo3_post_4b", 4, format, ImageFormat.None );
		var medium = commands.GetRenderTarget( "halo3_post_16a", 16, format, ImageFormat.None );
		var mediumWork = commands.GetRenderTarget( "halo3_post_16b", 16, format, ImageFormat.None );
		var large = commands.GetRenderTarget( "halo3_post_64a", 64, format, ImageFormat.None );
		var largeWork = commands.GetRenderTarget( "halo3_post_64b", 64, format, ImageFormat.None );
		var measure = commands.GetRenderTarget( "halo3_post_measure", 1, 1, ImageFormat.RGBA16161616F, ImageFormat.None );

		var exposure = bloom.Exposure;
		commands.Attributes.Set( "Halo3BloomThreshold", new Vector3( bloom.BloomPoint, bloom.InherentBloom, bloom.AntiBloom ) );
		commands.Attributes.Set( "Halo3ExposureControl", new Vector4( exposure.Auto ? Math.Clamp( startStops, exposure.Min, exposure.Max ) : exposure.Fixed, exposure.Auto ? 1 : 0, exposure.AgreeFrames, exposure.Rate ) );
		commands.Attributes.Set( "Halo3ExposureRange", new Vector3( exposure.Min, exposure.Max, exposure.TargetBrightness ) );
		commands.Attributes.Set( "Halo3ExposureMeasureMix", exposure.MeasureMix );
		commands.Attributes.Set( "Halo3HasExposureWeights", exposureWeights is not null ? 1f : 0f );
		if ( exposureWeights is not null )
			commands.Attributes.Set( "Halo3PostWeights", exposureWeights );
		commands.Attributes.Set( "Halo3PostState", _previousState );
		commands.Attributes.Set( "Halo3PostParams", _params );
		commands.Attributes.Set( "Halo3PostDebug", Debug );

		// The distortion particles' offsets, half res, from zero (MCC clears its to 128/255 and subtracts that).
		var distortionMaterial = distortion is not null ? Halo3Distortion.Material : null;
		var offsets = distortionMaterial is not null ? commands.GetRenderTarget( "halo3_post_distortion", 2, ImageFormat.RGBA16161616F, ImageFormat.None ) : default;
		if ( distortionMaterial is not null )
		{
			commands.ResourceBarrierTransition( offsets, ResourceState.RenderTarget );
			commands.SetRenderTarget( offsets );
			commands.Clear( Color.Transparent, true, false, false );
			commands.Attributes.Set( "Halo3DistortionTargetScale", 2f );
			commands.DrawInstancedIndirect( distortion.Vertices, distortionMaterial, distortion.Arguments, 0, null, Graphics.PrimitiveType.Triangles );
			commands.ResourceBarrierTransition( offsets, ResourceState.PixelShaderResource );
			commands.Attributes.Set( "Halo3PostDistortion", offsets.ColorTexture );
		}
		commands.Attributes.Set( "Halo3HasDistortion", distortionMaterial is not null ? 1f : 0f );

		// The LDR value, moved by the distortion and motion blurred (MCC does both in one pass on its LDR target after the transparent things, before bloom).
		Draw( commands, Pass.MotionBlur, frame, ldr );
		commands.Attributes.Set( "Halo3PostLdr", ldr.ColorTexture );

		// Bright pass to a quarter, then a quarter of that twice (MCC: 320x180, 80x45, 20x11 at 1280x720).
		Draw( commands, Pass.Bright, frame, bright );
		Draw( commands, Pass.Downsample, bright, medium );
		Draw( commands, Pass.Downsample, medium, large );

		// The exposure for the next frame, kept in _state (the materials' and next frame's bright pass) with a copy to read from.
		Draw( commands, Pass.Measure, large, measure );
		commands.Attributes.Set( "Halo3PostAdd", measure.ColorTexture );
		DrawTo( commands, Pass.State, _state );
		commands.Attributes.Set( "Halo3PostSource", _state );
		DrawTo( commands, Pass.Copy, _previousState );

		// The smallest level blurred and tinted, added into each larger one with its tint, each blurred again.
		Draw( commands, Pass.BlurHorizontal, large, largeWork );
		Scale( commands, bloom.Intensity * Vec( bloom.LargeColor ) );
		Draw( commands, Pass.BlurVertical, largeWork, large );

		Scale( commands, bloom.Intensity * Vec( bloom.MediumColor ) );
		commands.Attributes.Set( "Halo3PostAdd", large.ColorTexture );
		Draw( commands, Pass.Add, medium, mediumWork );
		Draw( commands, Pass.BlurHorizontal, mediumWork, medium );
		Scale( commands, Vector3.One );
		Draw( commands, Pass.BlurVertical, medium, mediumWork );

		Scale( commands, bloom.Intensity * Vec( bloom.SmallColor ) );
		commands.Attributes.Set( "Halo3PostAdd", mediumWork.ColorTexture );
		Draw( commands, Pass.AddAlpha, bright, work );
		Draw( commands, Pass.BlurHorizontal, work, bright );
		Scale( commands, Vector3.One );
		Draw( commands, Pass.BlurVertical, bright, work );

		commands.ClearRenderTarget();
		commands.Attributes.Set( "Halo3PostSource", frame.ColorTexture );
		commands.Attributes.Set( "Halo3PostBloom", work.ColorTexture );
		commands.Attributes.Set( "Halo3PostPass", (int)Pass.Composite );
		commands.Blit( _material );

		commands.ReleaseRenderTarget( ldr );
		if ( distortionMaterial is not null )
			commands.ReleaseRenderTarget( offsets );
		commands.ReleaseRenderTarget( bright );
		commands.ReleaseRenderTarget( work );
		commands.ReleaseRenderTarget( medium );
		commands.ReleaseRenderTarget( mediumWork );
		commands.ReleaseRenderTarget( large );
		commands.ReleaseRenderTarget( largeWork );
		commands.ReleaseRenderTarget( measure );
		return commands;
	}

	// MCC's underwater pass for a camera under water, to run at Stage.AfterSkybox: the opaque world and the sky fogged before the water surfaces and the transparent things draw over it (the water fogs itself, the rest isn't fogged); null until Build has run.
	public CommandList BuildUnderwater()
	{
		if ( _material is null || _params is null )
			return null;
		var commands = new CommandList( "Halo 3 underwater" );
		var frame = commands.Attributes.GrabFrameTexture( "Halo3UnderwaterFrame" );
		commands.Attributes.Set( "Halo3PostParams", _params );
		commands.Attributes.Set( "Halo3PostSource", frame.ColorTexture );
		commands.Attributes.Set( "Halo3PostPass", (int)Pass.Underwater );
		commands.Blit( _material );
		return commands;
	}

	[ConVar( "halo3_postfx_debug", Help = "Halo 3 post-processing views: 0 off, 1 the bloom alone, 2 no bloom, 4 the exposure (grey = stops from -6 to 6), 5 a linear ramp of 32 steps, 6 the frame luminance / 16, 7 the frame unmapped." )]
	public static int Debug { get; set; } = 0;

	static Vector3 Vec( System.Numerics.Vector3 v ) => new( v.X, v.Y, v.Z );

	static void Scale( CommandList commands, Vector3 scale ) => commands.Attributes.Set( "Halo3PostScale", new Vector4( scale, 1 ) );

	static void Draw( CommandList commands, Pass pass, RenderTargetHandle source, RenderTargetHandle target )
	{
		commands.ResourceBarrierTransition( target, ResourceState.RenderTarget );
		commands.SetRenderTarget( target );
		commands.Attributes.Set( "Halo3PostSource", source.ColorTexture );
		commands.Attributes.Set( "Halo3PostPass", (int)pass );
		commands.Blit( _material );
		commands.ResourceBarrierTransition( target, ResourceState.PixelShaderResource );
	}

	static void DrawTo( CommandList commands, Pass pass, Texture target )
	{
		commands.ResourceBarrierTransition( target, ResourceState.RenderTarget, 0 );
		commands.SetRenderTarget( RenderTarget.From( target, null ) );
		commands.Attributes.Set( "Halo3PostPass", (int)pass );
		commands.Blit( _material );
		commands.ResourceBarrierTransition( target, ResourceState.PixelShaderResource, 0 );
	}

	// This frame's screen effect values (MCC's area screen effects and script globals).
	public void Update( in PostFrame frame )
	{
		void Row( int texel, Vector4 v )
		{
			_paramData[texel * 4] = v.x;
			_paramData[texel * 4 + 1] = v.y;
			_paramData[texel * 4 + 2] = v.z;
			_paramData[texel * 4 + 3] = v.w;
		}

		Row( 0, frame.HueSaturation0 );
		Row( 1, frame.HueSaturation1 );
		Row( 2, frame.HueSaturation2 );
		Row( 3, new Vector4( frame.Contrast, frame.ExposureBoost, 0, 0 ) );
		var blur = frame.Blur;
		Row( 4, new Vector4( blur.TimeScale, blur.On ? 1 : 0, blur.Crosshair.x, blur.Crosshair.y ) );
		Row( 5, new Vector4( blur.Maximum.x, blur.Maximum.y, blur.Scale.x, blur.Scale.y ) );
		Row( 6, new Vector4( blur.Taps, blur.CentreFalloff, 0, 0 ) );
		Row( 7, new Vector4( frame.Underwater ? 1 : 0, frame.Murkiness, 1 / Halo3Mount.WorldScale, 0 ) );
		Row( 8, new Vector4( frame.FogColor, 0 ) );
		_params?.Update( new ReadOnlySpan<float>( _paramData ), 0, 0, ParamTexels, 1 );
	}

	public void Delete()
	{
		_params?.Dispose();
		_params = null;
		_state?.Dispose();
		_previousState?.Dispose();
		_state = null;
		_previousState = null;
	}
}

// The final composite's per-frame colour: the hue/saturation matrix (3 rows of 4, applied to ( rgb, 1 )), contrast exponent and exposure boost in stops; and the motion blur.
readonly record struct PostFrame( Vector4 HueSaturation0, Vector4 HueSaturation1, Vector4 HueSaturation2, float Contrast, float ExposureBoost, MotionBlurFrame Blur = default, bool Underwater = false, float Murkiness = 0, Vector3 FogColor = default )
{
	public static readonly PostFrame Neutral = new( new Vector4( 1, 0, 0, 0 ), new Vector4( 0, 1, 0, 0 ), new Vector4( 0, 0, 1, 0 ), 1, 0 );
}

// MCC's camera motion blur for a frame: rasg's taps, max blur, blur scale and centre falloff, expected / last frame time, on unless the camera jumped, and the crosshair in NDC.
readonly record struct MotionBlurFrame( bool On, float TimeScale, int Taps, Vector2 Maximum, Vector2 Scale, float CentreFalloff, Vector2 Crosshair );
