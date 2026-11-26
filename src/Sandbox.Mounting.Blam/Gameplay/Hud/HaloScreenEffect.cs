using System;
using Sandbox.Rendering;

namespace Halo.UI;

// Halo's weapon scope effect as a full screen pass after post processing, under the HUD.
// - The mask, one mask pixel per H/960 screen pixels: color darkens the frame, alpha picks how much blur to use.
// - The warp blur, a radial zoom blur that grows as the field of view narrows.
// - The sniper's night vision and desaturation while zoomed with the flashlight on.
[Title( "Halo Screen Effect" ), Icon( "center_focus_strong" )]
public sealed class HaloScreenEffect : Component {
	[ConVar( "halo_scope_blur", Help = "Scales the scope blur radius (1 is Halo's, 0 turns the blur off).", Saved = true )]
	public static float BlurScale { get; set; } = 1f;

	[ConVar( "halo_scope_debug", Help = "Log the scope screen effect's parameters about once a second." )]
	public static bool Debug { get; set; } = false;

	public const string ShaderPath = "shaders/halo_screen_effect.shader";

	public struct Parameters {
		// Color is darkening and alpha is blur amount; null for none.
		public Texture Mask;

		// In mask pixels; it covers size * H/960 screen pixels.
		public Vector2 MaskSize;

		// In 480-line pixels; 0 for no blur.
		public float WarpRadius;

		public float LightEnhancement;
		public bool LightEnhancementMasked;
		public float Desaturation;
		public Vector3 DesaturationTint;
		public bool DesaturationAdditive;
		public bool DesaturationMasked;

		public readonly bool IsEmpty => Mask is null && WarpRadius <= 0 && LightEnhancement <= 0 && Desaturation <= 0;
	}

	static Material _material;
	static float _materialTriedAt = float.MinValue;
	static bool _materialWarned;

	// Null when the shader isn't available.
	// Retried every 10 seconds, since the editor may still be compiling it.
	static Material EffectMaterial {
		get {
			if ( _material is not null || RealTime.Now - _materialTriedAt < 10 ) return _material;
			_materialTriedAt = RealTime.Now;

			string problem = null;
			try {
				var shader = Shader.Load( ShaderPath );
				if ( shader is null || !shader.IsValid || shader.IsError ) {
					problem = "isn't available (not compiled?)";
				}
				else {
					var material = Material.FromShader( shader );
					_material = material is not null && material.IsValid && !material.IsError ? material : null;
					if ( _material is null ) problem = "didn't make a material";
				}
			}
			catch ( Exception e ) {
				_material = null;
				problem = $"failed to load ({e.Message})";
			}

			if ( problem is not null && !_materialWarned ) {
				_materialWarned = true;
				Log.Warning( $"Halo screen effect: {ShaderPath} {problem}; scopes draw no screen effect." );
			}

			return _material;
		}
	}

	CommandList _commands;
	CameraComponent _camera;
	bool _recorded;
	float _submitted = -1;
	float _lastLog;

	// Call every frame the effect should show.
	// False, drawing nothing, when the shader isn't loaded.
	public bool Submit( CameraComponent camera, in Parameters parameters ) {
		if ( EffectMaterial is null || camera is null || parameters.IsEmpty ) {
			Clear();
			return false;
		}

		Attach( camera );

		var screen = Screen.Size;
		var aspect = screen.y > 0 ? screen.x / screen.y : 16f / 9f;
		var maskSize = parameters.MaskSize;
		var hasMask = parameters.Mask is not null && maskSize.x > 0 && maskSize.y > 0;
		var radius = MathF.Max( 0, parameters.WarpRadius * Math.Clamp( BlurScale, 0, 4 ) );

		_commands.Reset();
		_commands.Attributes.GrabFrameTexture( "ColorBuffer" );
		if ( hasMask ) {
			_commands.Attributes.Set( "HaloScopeMask", parameters.Mask );
			_commands.Attributes.Set( "HaloMaskScale", new Vector2( aspect * 960f / maskSize.x, 960f / maskSize.y ) );
		}

		_commands.Attributes.Set( "HaloHasMask", hasMask ? 1f : 0f );
		_commands.Attributes.Set( "HaloWarp", new Vector2( radius / (480f * aspect), radius / 480f ) );
		_commands.Attributes.Set( "HaloLightEnhancement", parameters.LightEnhancement );
		_commands.Attributes.Set( "HaloLightEnhancementMasked", parameters.LightEnhancementMasked ? 1f : 0f );
		_commands.Attributes.Set( "HaloDesaturation", parameters.Desaturation );
		_commands.Attributes.Set( "HaloDesaturationTint", parameters.DesaturationTint );
		_commands.Attributes.Set( "HaloDesaturationAdditive", parameters.DesaturationAdditive ? 1f : 0f );
		_commands.Attributes.Set( "HaloDesaturationMasked", parameters.DesaturationMasked ? 1f : 0f );
		_commands.Blit( EffectMaterial );
		_recorded = true;
		_submitted = Time.Now;

		if ( Debug && RealTime.Now - _lastLog > 1 ) {
			_lastLog = RealTime.Now;
			Log.Info( $"Halo screen effect: mask {(hasMask ? $"{parameters.Mask.ResourcePath ?? "dynamic"} {maskSize.x}x{maskSize.y}" : "none")}, warp radius {radius:0.00} px (480-line), "
				+ $"night vision {parameters.LightEnhancement:0.00}, desaturation {parameters.Desaturation:0.00}" );
		}

		return true;
	}

	public void Clear() {
		if ( !_recorded ) return;
		_commands?.Reset();
		_recorded = false;
	}

	// A frame the HUD didn't submit draws nothing.
	protected override void OnPreRender() {
		using var perf = Halo.HaloPerf.Measure( "ScreenEffect.PreRender" );
		if ( _recorded && _submitted != Time.Now )
			Clear();
	}

	void Attach( CameraComponent camera ) {
		_commands ??= new CommandList( "Halo Screen Effect" );
		if ( _camera == camera ) return;

		Detach();
		_camera = camera;
		// First after post processing and before the HUD, so the HUD stays sharp over the blur.
		_camera.AddCommandList( _commands, Stage.AfterPostProcess, -10000 );
	}

	void Detach() {
		if ( _camera.IsValid() && _commands is not null )
			_camera.RemoveCommandList( _commands );
		_camera = null;
	}

	protected override void OnDisabled() {
		Clear();
		Detach();
	}

	protected override void OnDestroy() {
		Clear();
		Detach();
	}
}
