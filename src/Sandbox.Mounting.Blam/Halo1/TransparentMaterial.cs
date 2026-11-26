using System.Runtime.CompilerServices;
using Blam.Halo1;
using Blam.Halo1.Tags;

namespace Sandbox.Mounting.Blam.Halo1;

/// <summary>
/// Builds s&amp;box materials for Halo CE's transparent effect shaders (chicago, chicago extended
/// and generic), which share one shader.
/// </summary>
static class TransparentMaterial
{
	public const string ShaderPath = "shaders/halo1_transparent.shader";
	const int MaxMaps = 4;

	/// <summary>
	/// Change colors for each shader of a map, worked out once per map (see
	/// <see cref="ObjectChangeColors.SteadyColorsByShader"/>).
	/// </summary>
	static readonly ConditionalWeakTable<CacheFile, Dictionary<uint, System.Numerics.Vector3[]>> _changeColors = new();

	/// <param name="onObject">Whether it's for an object's model. Only objects drive a generic
	/// stage's constant color 0 with A out and change colors: Halo draws BSP transparent groups with
	/// no object animation, so there each stage's color 0 comes from its own periodic function even
	/// when "A out controls color 0" is set. Blood Gulch's teleporter fields are level geometry using the teleporter shield
	/// scenery's shader; with that object's A out of 0 they came out dim and washed out.</param>
	/// <param name="permutation">The shader permutation of the model's shader reference. Halo draws
	/// image number permutation (modulo the count) of each map's bitmap; on numeric shaders it's the
	/// digit place instead (see <see cref="SetNumeric"/>).</param>
	public static Material Create( HaloCeMount host, string name, CacheFile cache, TagEntry shaderTag, bool onObject = true, int permutation = 0 )
	{
		var shader = new ShaderTransparentTag( cache, shaderTag );
		var material = Material.Create( name, ShaderPath );
		if ( material is null )
			return null;

		var colorFunctions = new float[MaxMaps];
		var alphaFunctions = new float[MaxMaps];
		var sourcesU = new float[MaxMaps];
		var sourcesV = new float[MaxMaps];
		var mapCount = Math.Min( shader.Maps.Count, MaxMaps );
		var bitmaps = shader.Maps.Take( mapCount ).Select( m => m.Map is null ? [] : new BitmapTag( cache, m.Map ).Bitmaps.OrderBy( b => b.Index ).ToList() ).ToList();
		var numericMaps = onObject && shader.Flags.HasFlag( TransparentShaderFlags.Numeric ) && mapCount > 0 && bitmaps[0].Count > 1;
		var countdown = numericMaps && shader.NumericCountdownTimer;
		var frames = new float[MaxMaps];

		for ( var i = 0; i < MaxMaps; i++ )
		{
			var map = i < mapCount ? shader.Maps[i] : null;
			Texture texture = null;
			if ( map is not null && numericMaps && bitmaps[i].Count > 1 )
			{
				texture = BitmapTextureLoader.CreateStrip( host, cache.FilePath, bitmaps[i], $"{name}/map{i}_frames" );
				frames[i] = texture is null ? 0 : bitmaps[i].Count;
			}

			if ( map is not null && texture is null )
				texture = MaterialTextures.Load( host, material, map.Map, bitmaps[i].Count > 0 ? permutation % bitmaps[i].Count : 0 );

			material.Set( $"g_tMap{i}", texture ?? MaterialTextures.White );
			material.Set( $"g_vMap{i}Transform", map is null ? new Vector4( 1, 1, 0, 0 ) : new Vector4( map.Scale.X, map.Scale.Y, map.Offset.X, map.Offset.Y ) );
			material.Set( $"g_vMap{i}AnimU", Animation( map?.UAnimation ) );
			material.Set( $"g_vMap{i}AnimV", Animation( map?.VAnimation ) );

			colorFunctions[i] = (float)(map?.ColorFunction ?? MapFunction.Multiply);
			alphaFunctions[i] = (float)(map?.AlphaFunction ?? MapFunction.Multiply);
			sourcesU[i] = (float)(map?.UAnimationSource ?? ChangeColorSource.None);
			sourcesV[i] = (float)(map?.VAnimationSource ?? ChangeColorSource.None);
		}

		// Object functions a game sets on the renderer can drive the animations and the fade.
		material.Set( "g_vMapAnimationSourcesU", new Vector4( sourcesU[0], sourcesU[1], sourcesU[2], sourcesU[3] ) );
		material.Set( "g_vMapAnimationSourcesV", new Vector4( sourcesV[0], sourcesV[1], sourcesV[2], sourcesV[3] ) );
		material.Set( "g_vMapFrames", new Vector4( frames[0], frames[1], frames[2], frames[3] ) );
		SetNumeric( material, !numericMaps || frames[0] <= 1 ? 0 : countdown ? CountdownLimit : shader.NumericCounterLimit, bitmaps.Count > 0 ? bitmaps[0].Count : 0, permutation );
		material.Set( "g_flFadeSource", (float)shader.FadeSource );

		material.Set( "g_vColorFunctions", new Vector4( colorFunctions[0], colorFunctions[1], colorFunctions[2], colorFunctions[3] ) );
		material.Set( "g_vAlphaFunctions", new Vector4( alphaFunctions[0], alphaFunctions[1], alphaFunctions[2], alphaFunctions[3] ) );
		material.Set( "g_flMapCount", (float)Math.Max( mapCount, 1 ) );
		material.SetFeature( "F_BLEND_MODE", (int)shader.BlendFunction );

		System.Numerics.Vector3[] changeColors = null;
		if ( onObject )
			_changeColors.GetValue( cache, ObjectChangeColors.SteadyColorsByShader ).TryGetValue( shaderTag.Id, out changeColors );
		var functions = onObject ? ObjectFunctions.RestValuesForShader( cache, shaderTag ) : null;
		var stages = StageTexture( shader, functions );
		material.Set( "g_tStages", stages ?? MaterialTextures.Transparent );
		for ( var i = 0; i < 4; i++ )
			material.Set( $"g_vChangeColor{(char)('A' + i)}", changeColors is not null && i < changeColors.Length ? (Vector3)changeColors[i] : Vector3.One );
		material.Set( "g_flStageCount", stages is null ? 0f : stages.Height );
		material.Set( "g_flFadeMode", (float)shader.FadeMode );
		material.Set( "g_vTint", Vector3.One );
		material.Set( "g_flAlphaCoverage", 0f );
		material.Set( "g_flGammaAlpha", shader.Flags.HasFlag( TransparentShaderFlags.DrawBeforeWater ) ? 1f : 0f );

		return material;
	}

	/// <summary>The counter limit that marks a shader showing the scripted countdown timer.</summary>
	const int CountdownLimit = -1;

	/// <summary>
	/// Numeric shaders show a number from an object function: the assault rifle's ammo counter
	/// (limit 60, ten digit images, A out: rounds left) and its compass (limit 8, eight images, D out:
	/// heading). Halo scales the function by the limit and rounds it, then each model part shows one
	/// digit of that number in base "image count" (the first map's): its shader permutation is the
	/// digit place, 0 for ones. That digit picks the image of every map. Bitmaps with eight images
	/// follow D out, the rest A out. The images arrive side by side in one texture per map.
	/// </summary>
	/// <param name="limit">The counter limit, 0 if the shader isn't numeric, or <see cref="CountdownLimit"/>
	/// for a shader showing the scripted countdown timer, whose digit at this part's place comes from
	/// the scene's HaloCountdownMilliseconds attribute (a game sets it from numeric_countdown_timer_set).</param>
	static void SetNumeric( Material material, int limit, int frameCount, int place )
	{
		var source = frameCount == 8 ? ChangeColorSource.D : ChangeColorSource.A;
		material.Set( "g_vNumeric", new Vector4( limit, Math.Max( frameCount, 1 ), place, (float)source ) );

		// Without the function set on the renderer: a full magazine, a compass facing north.
		material.Set( "g_flNumericRest", source == ChangeColorSource.D ? 0.5f : 1f );
	}

	/// <summary>
	/// A material for a decal: one map, tinted, blended like a transparent shader.
	/// </summary>
	public static Material CreateDecal( HaloCeMount host, string name, CacheFile cache, DecalTag decal, int bitmapIndex )
	{
		var material = Material.Create( name, ShaderPath );
		if ( material is null )
			return null;

		material.Set( "g_tMap0", MaterialTextures.Load( host, material, decal.Map, bitmapIndex ) ?? MaterialTextures.White );
		for ( var i = 1; i < MaxMaps; i++ )
			material.Set( $"g_tMap{i}", MaterialTextures.White );

		for ( var i = 0; i < MaxMaps; i++ )
		{
			material.Set( $"g_vMap{i}Transform", new Vector4( 1, 1, 0, 0 ) );
			material.Set( $"g_vMap{i}AnimU", new Vector4( 0, 1, 0, 1 ) );
			material.Set( $"g_vMap{i}AnimV", new Vector4( 0, 1, 0, 1 ) );
		}

		material.Set( "g_vColorFunctions", new Vector4( 2, 2, 2, 2 ) );
		material.Set( "g_vAlphaFunctions", new Vector4( 2, 2, 2, 2 ) );
		material.Set( "g_flMapCount", 1f );
		material.Set( "g_vMapFrames", Vector4.Zero );
		SetNumeric( material, 0, 0, 0 );
		material.Set( "g_tStages", MaterialTextures.Transparent );
		material.Set( "g_flStageCount", 0f );
		material.Set( "g_flFadeMode", 0f );
		material.Set( "g_vTint", (Vector3)decal.Color );
		material.Set( "g_flAlphaCoverage", 1f );
		material.SetFeature( "F_BLEND_MODE", (int)decal.BlendFunction );
		return material;
	}

	/// <summary>
	/// Packs a generic shader's register combiner stages into a float texture for the shader to
	/// read, one row of twelve texels per stage: color inputs, color input mappings, color outputs
	/// and mapping, (AB dot, CD dot, color mux, alpha mux), alpha inputs, alpha input mappings,
	/// alpha outputs and mapping, constant color 0, constant color 1, constant color 0's lower
	/// and upper bound for a game's A out (both constant color 0 when A out doesn't control it),
	/// and (constant color 0's change color source, 1 when A out controls it, 0, 0). The shader
	/// multiplies constant color 0's rgb by that change color, the game's or the material's.
	/// Null if there are no stages.
	/// </summary>
	/// <param name="functions">Rest values of A-D out of the object using the shader, if any.
	/// Stages whose constant color 0 follows A out take it from there (an object without functions,
	/// like the powerups, has A out 0). A game's A out (the HaloFunctionA renderer attribute) picks
	/// it between the bounds in the shader instead.</param>
	static Texture StageTexture( ShaderTransparentTag shader, float[] functions )
	{
		// Halo allows up to 7 stages.
		var count = Math.Min( shader.Stages.Count, 7 );
		if ( count == 0 )
			return null;

		const int width = 12;
		var data = new float[width * 4 * count];
		for ( var row = 0; row < count; row++ )
		{
			var stage = shader.Stages[row];
			var constant0 = stage.AOutControlsConstant0 && functions is not null
				? System.Numerics.Vector4.Lerp( stage.Constant0Bounds.Lower, stage.Constant0Bounds.Upper, functions[0] )
				: stage.Constant0;
			var (lower, upper) = stage.AOutControlsConstant0 ? stage.Constant0Bounds : (constant0, constant0);

			var texels = new[]
			{
				new Vector4( stage.ColorInputs[0].Input, stage.ColorInputs[1].Input, stage.ColorInputs[2].Input, stage.ColorInputs[3].Input ),
				new Vector4( stage.ColorInputs[0].Mapping, stage.ColorInputs[1].Mapping, stage.ColorInputs[2].Mapping, stage.ColorInputs[3].Mapping ),
				new Vector4( stage.ColorOutputs.Ab, stage.ColorOutputs.Cd, stage.ColorOutputs.AbCd, stage.ColorOutputs.Mapping ),
				new Vector4( stage.ColorFunctions.AbDot ? 1 : 0, stage.ColorFunctions.CdDot ? 1 : 0, stage.ColorMux ? 1 : 0, stage.AlphaMux ? 1 : 0 ),
				new Vector4( stage.AlphaInputs[0].Input, stage.AlphaInputs[1].Input, stage.AlphaInputs[2].Input, stage.AlphaInputs[3].Input ),
				new Vector4( stage.AlphaInputs[0].Mapping, stage.AlphaInputs[1].Mapping, stage.AlphaInputs[2].Mapping, stage.AlphaInputs[3].Mapping ),
				new Vector4( stage.AlphaOutputs.Ab, stage.AlphaOutputs.Cd, stage.AlphaOutputs.AbCd, stage.AlphaOutputs.Mapping ),
				(Vector4)constant0,
				(Vector4)stage.Constant1,
				(Vector4)lower,
				(Vector4)upper,
				new Vector4( stage.Constant0Source is >= 1 and <= 4 ? stage.Constant0Source : 0, stage.AOutControlsConstant0 ? 1 : 0, 0, 0 ),
			};

			for ( var x = 0; x < width; x++ )
			{
				var i = (row * width + x) * 4;
				data[i] = texels[x].x;
				data[i + 1] = texels[x].y;
				data[i + 2] = texels[x].z;
				data[i + 3] = texels[x].w;
			}
		}

		return Texture.Create( width, count, ImageFormat.RGBA32323232F )
			.WithData( new ReadOnlySpan<float>( data ) )
			.WithStaticUsage()
			.Finish();
	}

	static Vector4 Animation( MapAnimation? animation ) => animation is { } a
		? new Vector4( (float)a.Function, a.Period, a.Phase, a.Scale )
		: new Vector4( 0, 1, 0, 1 );
}
