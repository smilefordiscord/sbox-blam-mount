using Blam.Halo2.Tags;
using MaterialTextures = Sandbox.Mounting.Blam.Halo1.MaterialTextures;

namespace Sandbox.Mounting.Blam.Halo2;

/// <summary>
/// Hands a shader's parameters to halo2_transparent.shader or halo2_plasma.shader the way one pass of its
/// template hands them to MCC's pixel shader (worked out by <see cref="ShaderPassParameters"/>): textures by
/// stage (g_tMap0-3), pixel constants by register (g_vC1-7), texture coordinate rows per set (g_vUvNU/V), the
/// fresnel pair (g_vV10/11), and the time curves the shaders evaluate (g_vAnimN*, g_vAnimConstN*).
/// </summary>
/// <remarks>
/// Where each nameless slot goes is read from the template (<see cref="ShaderTemplateTag"/>), so every template
/// on a pass these shaders implement gets the right slots without a table of its own.
/// </remarks>
static class TemplatePassParameters
{
	/// <summary>
	/// The blend mode feature (F_BLEND_MODE) for the pass's blend factors: 0 premultiplied (ONE / INVSRCALPHA),
	/// 1 additive (ONE / ONE), 2 alpha blend (SRCALPHA / INVSRCALPHA), 3 SRCALPHA / ONE, 4 double multiply
	/// (DESTCOLOR / SRCCOLOR: twice the source times the frame, grey leaves it as is); null for anything else.
	/// </summary>
	public static int? BlendMode( ShaderTemplatePass pass ) => (pass.SourceBlend, pass.DestinationBlend) switch
	{
		(ShaderTemplatePass.BlendOne, ShaderTemplatePass.BlendInverseSourceAlpha) => 0,

		// alpha_detail_masked blends with a constant blend factor; MCC's value isn't known, taken as 1.
		(ShaderTemplatePass.BlendFactor, ShaderTemplatePass.BlendInverseSourceAlpha) => 0,
		(ShaderTemplatePass.BlendOne, ShaderTemplatePass.BlendOne) => 1,
		(ShaderTemplatePass.BlendSourceAlpha, ShaderTemplatePass.BlendInverseSourceAlpha) => 2,
		(ShaderTemplatePass.BlendSourceAlpha, ShaderTemplatePass.BlendOne) => 3,
		(ShaderTemplatePass.BlendDestinationColor, ShaderTemplatePass.BlendSourceColor) => 4,
		_ => null,
	};

	/// <param name="onObject">Drawn on an object (a model placed in a level): object functions driving the shader's
	/// animation take their values for the object at rest. Level geometry and the sky have no object: every such
	/// input is 0, as in MCC (<see cref="AnimatedShader"/>).</param>
	/// <param name="cubeStage">The stage holding a cube map (the reflection), -1 for none: unbound it's black.</param>
	/// <param name="blendFeature">Whether the shader picks its blending with F_BLEND_MODE (halo2_plasma.shader only adds).</param>
	/// <param name="randomConstant">On an object: its <c>random_constant</c>, if known (else one per shader).</param>
	public static void Apply( Material material, ShaderTag shader, ShaderTemplatePass pass, bool onObject, int cubeStage, bool blendFeature = true, float? randomConstant = null,
		IReadOnlyList<string> functions = null )
	{
		if ( blendFeature )
			material.SetFeature( "F_BLEND_MODE", BlendMode( pass ) ?? 0 );
		material.SetFeature( "F_TWO_SIDED", pass.IsTwoSided ? 1 : 0 );

		// The shader's animation at rest: slots (bitmap frames included) for the object, or for no object; then the
		// pass's registers from them, with the time curves the shader evaluates.
		var inputs = onObject ? ShaderInputs.ForObject( shader.Tag, randomConstant ) : ShaderInputs.None;
		var stored = shader;
		var animation = new AnimatedShader( shader, inputs );
		shader = animation.Shader;
		var parameters = new ShaderPassParameters( shader, pass, inputs );

		for ( var stage = 0; stage < ShaderPassParameters.MaxStages; stage++ )
		{
			var texture = Load( shader, parameters.Stages[stage].BitmapSlot );
			material.Set( $"g_tMap{stage}", texture ?? (stage == cubeStage ? MaterialTextures.Transparent : MaterialTextures.White) );
		}

		var clamp = parameters.Stages.Select( s => s.Clamp ? 1f : 0f ).ToArray();
		material.Set( "g_vMapClamp", new Vector4( clamp[0], clamp[1], clamp[2], clamp[3] ) );

		for ( var r = 1; r <= 7; r++ )
			material.Set( $"g_vC{r}", (Vector4)parameters.Registers[r] );

		for ( var i = 0; i < parameters.AnimatedConstants.Count; i++ )
		{
			var constant = parameters.AnimatedConstants[i];
			material.Set( $"g_vAnimConst{i}A", (Vector4)constant.Curve.A );
			material.Set( $"g_vAnimConst{i}B", (Vector4)constant.Curve.B );
			material.Set( $"g_vAnimConst{i}Min", (Vector4)constant.Minimum );
			material.Set( $"g_vAnimConst{i}Max", (Vector4)constant.Maximum );
			material.Set( $"g_vAnimConst{i}Target", new Vector3( constant.Register, constant.Component, 1 ) );
		}

		for ( var set = 0; set < parameters.UvSets.Length; set++ )
		{
			var uv = parameters.UvSets[set];
			material.Set( $"g_vUv{set}U", new Vector4( (Vector3)uv.U, 0 ) );
			material.Set( $"g_vUv{set}V", new Vector4( (Vector3)uv.V, 0 ) );
			material.Set( $"g_vAnim{set}UA", (Vector4)(uv.CurveU?.A ?? default) );
			material.Set( $"g_vAnim{set}UB", (Vector4)(uv.CurveU?.B ?? default) );
			material.Set( $"g_vAnim{set}VA", (Vector4)(uv.CurveV?.A ?? default) );
			material.Set( $"g_vAnim{set}VB", (Vector4)(uv.CurveV?.B ?? default) );
		}

		material.Set( "g_vV10", (Vector4)parameters.V10 );
		material.Set( "g_vV11", (Vector4)parameters.V11 );

		// What follows the object's functions while a game plays: registers (kind 3) and texture coordinate offsets (kind 4).
		var driven = DrivenParameters.Find( stored.Tag, inputs, functions, values => Registers( new ShaderPassParameters( new AnimatedShader( stored, values ).Shader, pass, values ) ) );
		var entries = driven.Select( d => (d.Slot, d.Key.Kind, d.Key.Index, d.Table, Frame: System.Numerics.Vector4.Zero) ).ToList();

		// Bitmaps whose frame follows a function (the battle rifle's ammunition digits): the stage reading it gets the images as one atlas, the frame picked by the shader (kind 5).
		foreach ( var (slot, frame, count) in animation.ObjectFrames )
		{
			var function = functions?.ToList().FindIndex( f => string.Equals( f, frame.Input, StringComparison.OrdinalIgnoreCase ) ) ?? -1;
			var stage = Array.FindIndex( parameters.Stages, s => s.BitmapSlot == slot );
			if ( function < 0 || stage < 0 || entries.Count >= DrivenParameters.MaxEntries || DrivenParameters.FrameAtlas( shader.GetBitmap( slot )?.Bitmap ) is not { } atlas )
				continue;

			material.Set( $"g_tMap{stage}", atlas );
			entries.Add( (function, 5, stage, DrivenParameters.FrameTable( frame, inputs ), new System.Numerics.Vector4( count, Math.Max( 0, shader.GetBitmap( slot ).Image ), 0, 0 )) );
		}

		DrivenParameters.Apply( material, entries );
	}

	// The registers c1-c7 and each texture coordinate set's offsets (u, v), as Apply sets them.
	static Dictionary<(int Kind, int Index), System.Numerics.Vector4> Registers( ShaderPassParameters parameters )
	{
		var values = new Dictionary<(int, int), System.Numerics.Vector4>();
		for ( var r = 1; r <= 7; r++ )
			values[(3, r)] = parameters.Registers[r];
		for ( var set = 0; set < parameters.UvSets.Length; set++ )
			values[(4, set)] = new( parameters.UvSets[set].U.Z, parameters.UvSets[set].V.Z, 0, 0 );
		return values;
	}

	/// <summary>
	/// A bitmap slot's texture (cube maps arrive as a six-face atlas), or null.
	/// </summary>
	public static Texture Load( ShaderTag shader, int slot )
	{
		var bitmap = shader.GetBitmap( slot );
		if ( bitmap?.Bitmap is null || bitmap.Bitmap.Class != BitmapTag.Class )
			return null;

		var texture = Texture.Load( $"mount://halo2/{Halo2Mount.TextureName( bitmap.Bitmap.Path, Math.Max( 0, bitmap.Image ) )}.vtex" );
		return texture is null || texture.IsError ? null : texture;
	}
}
