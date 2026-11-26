using Blam.Halo1;
using Blam.Halo1.Tags;

namespace Sandbox.Mounting.Blam.Halo1;

/// <summary>
/// Builds s&amp;box materials for Halo CE's meter shaders (shader_transparent_meter): the Warthog's
/// dashboard gauges and the plasma weapons' heat displays.
/// </summary>
/// <remarks>
/// The meter's value, gradient, flash and brightness come from object functions. Nothing runs
/// those in a mounted scene, so they're fixed at the rest values of the first object in the map
/// whose model uses the shader: a parked Warthog's speed is 0, so its gauges are empty and draw
/// nothing (the background is black and added); the plasma rifle's display sits at its 0.1 lower
/// bound and the plasma pistol's is empty, showing the blue background. A game drives them with
/// the HaloFunctionA-D renderer attributes (see halo1_meter.shader).
/// </remarks>
static class MeterMaterial
{
	public const string ShaderPath = "shaders/halo1_meter.shader";

	public static Material Create( HaloCeMount host, string name, CacheFile cache, TagEntry shaderTag )
	{
		var shader = new ShaderTransparentMeterTag( cache, shaderTag );
		var material = Material.Create( name, ShaderPath );
		if ( material is null )
			return null;

		var functions = ObjectFunctions.RestValuesForShader( cache, shaderTag );
		var brightness = ObjectFunctions.Get( functions, shader.MeterBrightnessSource );
		var flashBrightness = ObjectFunctions.Get( functions, shader.FlashBrightnessSource );
		var value = ObjectFunctions.Get( functions, shader.ValueSource );
		var gradient = ObjectFunctions.Get( functions, shader.GradientSource );
		var flashExtension = ObjectFunctions.Get( functions, shader.FlashExtensionSource );

		var flash = (Vector3)shader.FlashColor;
		if ( shader.Flags.HasFlag( MeterShaderFlags.FlashColorIsNegative ) )
			flash = -flash;

		// Which object function each value follows, for a game's to replace the rest values.
		material.Set( "g_vMeterSources", new Vector4( (float)shader.ValueSource, (float)shader.GradientSource, (float)shader.FlashExtensionSource, (float)shader.MeterBrightnessSource ) );
		material.Set( "g_flMeterFlashSource", (float)shader.FlashBrightnessSource );
		material.Set( "g_vMeterBrightness", new Vector2( brightness, flashBrightness ) );

		material.Set( "g_tMeterMap", MaterialTextures.Load( host, material, shader.Map, 0 ) ?? MaterialTextures.White );
		material.Set( "g_flMeterUnfiltered", shader.Flags.HasFlag( MeterShaderFlags.Unfiltered ) ? 1f : 0f );
		material.Set( "g_flMeterDecal", shader.Flags.HasFlag( MeterShaderFlags.Decal ) ? 1f : 0f );
		material.Set( "g_vMeterGradientMin", (Vector3)shader.GradientMinColor );
		material.Set( "g_vMeterGradientMax", (Vector3)shader.GradientMaxColor );
		material.Set( "g_vMeterBackground", (Vector3)shader.BackgroundColor );
		material.Set( "g_vMeterFlash", flash );
		material.Set( "g_vMeterValues", new Vector3( value, gradient, flashExtension ) );

		// Halo's blend: color x brightness + background x tint color, or with tint mode 2,
		// color x tint x brightness + background x the meter's or background's transparency.
		// One blend factor for what's behind can't be a color, so the tint is averaged there.
		// What's behind is scaled in gamma space, so the factor goes to linear (^2.2).
		var tint = (Vector3)shader.TintColor;
		if ( shader.Flags.HasFlag( MeterShaderFlags.TintMode2 ) )
		{
			material.Set( "g_vMeterSourceScale", tint );
			material.Set( "g_vMeterDestScale", new Vector2( Gamma( shader.MeterTransparency ), Gamma( shader.BackgroundTransparency ) ) );
		}
		else
		{
			var behind = (Gamma( tint.x ) + Gamma( tint.y ) + Gamma( tint.z )) / 3;
			material.Set( "g_vMeterSourceScale", Vector3.One );
			material.Set( "g_vMeterDestScale", new Vector2( behind, behind ) );
		}

		material.SetFeature( "F_TWO_SIDED", shader.Flags.HasFlag( MeterShaderFlags.TwoSided ) ? 1 : 0 );
		return material;
	}

	static float Gamma( float x ) => MathF.Pow( Math.Clamp( x, 0, 1 ), 2.2f );
}
