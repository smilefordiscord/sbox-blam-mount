using System;
using System.Collections.Generic;
using System.Linq;

namespace Blam.Halo2.Tags;

// Inside the namespace so these win over s&box's global vector types when this compiles into the mount.
using Vector3 = System.Numerics.Vector3;
using Vector4 = System.Numerics.Vector4;

/// <summary>
/// A shield shell shader's parameters (transparent\plasma_shield, plasma_shield_change_color, overshield:
/// shader_templates_other.md section 7) as curves over the object functions that drive them, for a GPU shader to
/// evaluate per object (halo2_shield.shader).
/// </summary>
/// <remarks>
/// Nearly every parameter of these shaders follows an object function through the shader's animation: elite
/// shields' colours, shell offset and noise translation are transitions of <c>shield_glow_source</c>, the Master
/// Chief's fresnel colours and offset of <c>shield_hit</c>, the overshield's fresnel colour a spline of
/// <c>object_overshield_amount</c> (every curve of every such shader on every map follows one of the three, or
/// time). Each value is the shader's animation evaluated by the mount's evaluator (<see cref="AnimatedShader"/>,
/// <see cref="ShaderPassParameters"/>) at <see cref="Width"/> function values from 0 to 1, only that function
/// changed (the others 0); a value no function moves keeps its value at 0. Time-driven colours (pixel constants)
/// stay in <see cref="Rest"/>'s animated constants and time-driven noise translations in
/// <see cref="ShieldNoise.TranslationCurves"/>, for the shader to evaluate.
/// </remarks>
public sealed class ShieldCurves
{
	public enum Program { Shield, ShieldChangeColor, Overshield }

	static readonly Dictionary<string, Program> Programs = new( StringComparer.OrdinalIgnoreCase )
	{
		[@"plasma\plasma_shield"] = Program.Shield,
		[@"plasma\plasma_shield_change_color"] = Program.ShieldChangeColor,
		[@"plasma\overshield"] = Program.Overshield,
	};

	/// <summary>
	/// The object functions the shield shaders read, in the order of the shader's drivers.
	/// </summary>
	public static readonly string[] Functions = ["shield_hit", "shield_glow_source", "object_overshield_amount"];

	/// <summary>Columns per curve: function values 0, 1/32 ... 1.</summary>
	public const int Width = 33;

	/// <summary>The curve rows (ROW_* in halo2_shield.shader).</summary>
	public enum Row { C2, C3, C4, C6, V10, V11, Offset, Noise0Scale, Noise0Translate, Noise1Scale, Noise1Translate, Count }

	public ShaderTemplatePass Pass { get; }
	public Program Kind { get; }

	/// <summary>The function index (<see cref="Functions"/>) each row follows, -1 for none.</summary>
	public int[] Drivers { get; }

	/// <summary>[row, column]: the row's value at function value column / (Width - 1).</summary>
	public Vector4[,] Values { get; }

	/// <summary>The pass parameters with every function at 0 (textures, time curves of colours).</summary>
	public ShaderPassParameters Rest { get; }

	/// <summary>The noise transforms with every function at 0 (time curves of the translation).</summary>
	public ShieldNoise RestNoise { get; }

	/// <summary>
	/// The template's shield pass (its first transparent pass), or null if it isn't a shield template.
	/// </summary>
	public static ShaderTemplatePass FindPass( ShaderTag shader )
	{
		if ( shader.Template is null )
			return null;

		var pass = new ShaderTemplateTag( shader.Template ).Passes( ShaderTemplateTag.TransparentLayer ).FirstOrDefault();
		return pass is not null && Programs.ContainsKey( pass.Name ) ? pass : null;
	}

	public static bool IsShield( ShaderTag shader ) => FindPass( shader ) is not null;

	/// <param name="atRest">The object's inputs at rest (its random constant); the shield functions are set on top.</param>
	public ShieldCurves( ShaderTag shader, ShaderInputs atRest )
	{
		Pass = FindPass( shader ) ?? throw new ArgumentException( $"{shader.Tag.Path} isn't a shield shader" );
		Kind = Programs[Pass.Name];

		ShaderInputs With( int function, float value ) =>
			atRest.With( Functions.Select( ( f, k ) => (f, v: k == function ? value : 0f) ).ToDictionary( p => p.f, p => p.v ) );

		var rest = Evaluate( shader, Pass, With( -1, 0 ), out var restParameters, out var restNoise );
		Rest = restParameters;
		RestNoise = restNoise;

		Drivers = Enumerable.Repeat( -1, (int)Row.Count ).ToArray();
		Values = new Vector4[(int)Row.Count, Width];
		for ( var r = 0; r < (int)Row.Count; r++ )
		for ( var k = 0; k < Width; k++ )
			Values[r, k] = rest[r];

		for ( var f = 0; f < Functions.Length; f++ )
		{
			var columns = Enumerable.Range( 0, Width ).Select( k => Evaluate( shader, Pass, With( f, k / (float)(Width - 1) ), out _, out _ ) ).ToArray();
			for ( var r = 0; r < (int)Row.Count; r++ )
			{
				if ( Drivers[r] >= 0 || !columns.Any( c => Vector4.DistanceSquared( c[r], rest[r] ) > 1e-10f ) )
					continue;

				Drivers[r] = f;
				for ( var k = 0; k < Width; k++ )
					Values[r, k] = columns[k][r];
			}
		}
	}

	/// <summary>
	/// One column of every row: the values the pass hands MCC's shaders for an object with these functions.
	/// </summary>
	static Vector4[] Evaluate( ShaderTag shader, ShaderTemplatePass pass, ShaderInputs inputs, out ShaderPassParameters parameters, out ShieldNoise noise )
	{
		var animated = new AnimatedShader( shader, inputs ).Shader;
		parameters = new ShaderPassParameters( animated, pass, inputs );
		noise = new ShieldNoise( shader, pass, parameters, inputs );

		var rows = new Vector4[(int)Row.Count];
		rows[(int)Row.C2] = parameters.Registers[2];
		rows[(int)Row.C3] = parameters.Registers[3];
		rows[(int)Row.C4] = parameters.Registers[4];
		rows[(int)Row.C6] = parameters.Registers[6];
		rows[(int)Row.V10] = parameters.V10;
		rows[(int)Row.V11] = parameters.V11;
		rows[(int)Row.Offset] = new Vector4( parameters.VertexRegisters[26 - 18].W, 0, 0, 0 );
		rows[(int)Row.Noise0Scale] = new Vector4( noise.Scale[0], 0 );
		rows[(int)Row.Noise0Translate] = new Vector4( noise.Translation[0], 0 );
		rows[(int)Row.Noise1Scale] = new Vector4( noise.Scale[1], 0 );
		rows[(int)Row.Noise1Translate] = new Vector4( noise.Translation[1], 0 );
		return rows;
	}
}

/// <summary>
/// The two 3D noise transforms of a shield pass (MCC's VS reads c[18]-c[20] for the first map, c[21]-c[23] for the
/// second: noise = bind pose position in world units * scale + translation, per axis, the translation in noise
/// lengths), from the shader's stored rows and its bitmap transforms' scale (parts 1-3) and translation (parts 4-6)
/// curves: object functions evaluated for the inputs, time curves left for the shader (<see cref="TranslationCurves"/>,
/// that axis' translation then 0). Rotation (part 7) is constant 0 on every shield shader and left out.
/// </summary>
public sealed class ShieldNoise
{
	public Vector3[] Scale { get; } = new Vector3[2];
	public Vector3[] Translation { get; } = new Vector3[2];

	/// <summary>[noise map, axis]: a time curve added to that translation, or null.</summary>
	public ShaderTimeCurve?[,] TranslationCurves { get; } = new ShaderTimeCurve?[2, 3];

	public ShieldNoise( ShaderTag shader, ShaderTemplatePass pass, ShaderPassParameters parameters, ShaderInputs inputs )
	{
		var c = parameters.VertexRegisters;
		for ( var n = 0; n < 2; n++ )
		{
			Vector4 x = c[3 * n], y = c[3 * n + 1], z = c[3 * n + 2];
			Scale[n] = new( x.X, y.Y, z.Z );
			Translation[n] = new( x.W, y.W, z.W );
		}

		foreach ( var parameter in ShaderAnimation.Read( shader.Tag, pass.TemplateImplementation ) )
		{
			var n = parameter.Target & 0xFF;
			if ( parameter.Kind != ShaderAnimatedKind.BitmapTransform || n > 1 )
				continue;

			foreach ( var (overlay, part) in parameter.Overlays )
			{
				if ( part is < 1 or > 6 || overlay.Function.IsEmpty )
					continue;

				var axis = (part - 1) % 3;
				if ( part >= 4 && overlay.IsTime && overlay.Function.Type != MappingFunction.Constant && ShaderTimeCurve.Of( overlay ) is { } curve )
				{
					TranslationCurves[n, axis] = curve;
					Translation[n] = With( Translation[n], axis, 0 );
					continue;
				}

				var value = overlay.Function.Evaluate( overlay.IsTime ? 0 : inputs.Value( overlay.Input ), inputs.Value( overlay.Range ) );
				if ( part <= 3 )
					Scale[n] = With( Scale[n], axis, value );
				else
					Translation[n] = With( Translation[n], axis, value );
			}
		}
	}

	static Vector3 With( Vector3 v, int axis, float value ) =>
		axis == 0 ? v with { X = value } : axis == 1 ? v with { Y = value } : v with { Z = value };
}
