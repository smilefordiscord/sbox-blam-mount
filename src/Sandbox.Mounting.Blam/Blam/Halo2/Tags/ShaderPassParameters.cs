using System;
using System.Collections.Generic;
using System.Linq;

namespace Blam.Halo2.Tags;

// Inside the namespace so these win over s&box's global vector types when this compiles into the mount.
using Vector4 = System.Numerics.Vector4;

/// <summary>
/// A time curve as halo2_transparent.shader, halo2_plasma.shader and halo2_opaque.shader evaluate it
/// (AnimatedFraction): A = (type + 16 shape, period in seconds, output min, output max), B = the curve's constants.
/// </summary>
public readonly record struct ShaderTimeCurve( Vector4 A, Vector4 B )
{
	/// <summary>
	/// A time curve for the shaders, or null for curves they don't evaluate (constants, key curves, splines,
	/// object inputs). A range function's second curve is left out (the range input taken as 0).
	/// </summary>
	public static ShaderTimeCurve? Of( ShaderOverlay overlay )
	{
		var f = overlay.Function;
		if ( !overlay.IsTime || overlay.Period <= 0 || f.IsEmpty )
			return null;

		Vector4? b = f.Type switch
		{
			MappingFunction.Identity => Vector4.Zero,
			MappingFunction.Transition => new Vector4( f.F( 0x14 ), f.F( 0x18 ), 0, 0 ),
			MappingFunction.Periodic => new Vector4( f.F( 0x14 ), f.F( 0x18 ), f.F( 0x1C ), f.F( 0x20 ) ),
			MappingFunction.Linear => new Vector4( f.F( 0x24 ), f.F( 0x28 ), 0, 0 ),
			MappingFunction.Exponent => new Vector4( f.F( 0x14 ), f.F( 0x18 ), f.F( 0x1C ), 0 ),
			_ => null,
		};

		return b is null ? null : new ShaderTimeCurve( new Vector4( f.Type + 16 * f.Shape, overlay.Period, f.Minimum, f.Maximum ), b.Value );
	}
}

/// <summary>
/// One texture coordinate set: u' = dot( (u, v, 1), U ), v' = dot( (u, v, 1), V ), plus time curves added to
/// the offsets.
/// </summary>
public sealed record ShaderUvSet( System.Numerics.Vector3 U, System.Numerics.Vector3 V, ShaderTimeCurve? CurveU, ShaderTimeCurve? CurveV );

/// <summary>
/// A pixel constant that follows a time curve: the curve (output 0-1) blends <see cref="Minimum"/> to
/// <see cref="Maximum"/> into a register's component (0-3) or rgb (4).
/// </summary>
public sealed record ShaderAnimatedConstant( ShaderTimeCurve Curve, Vector4 Minimum, Vector4 Maximum, int Register, int Component );

/// <summary>
/// A shader's parameters as one pass of its template hands them to MCC's pixel shader: textures by stage, pixel
/// constant registers c1-c7, the template's 2D texture transforms per texture coordinate set, the fresnel pair,
/// and its time-driven animation as curves for the GPU. Engine independent, so BlamInspect can check it; the mount
/// copies it into materials (Halo2/TemplatePassParameters.cs).
/// </summary>
/// <remarks>
/// Texture coordinates: MCC's transparent vertex shaders give set n the rows written to c[18 + 2n] and
/// c[19 + 2n] (u' = dot( uv, row.xy ) + row.w); stencil_emissive (add_illum_detail) chains a base transform in
/// c[18] with one "simple" transform (uv * xy + zw) per set in c[19 + n]; stencil_texture (overlays, decal_simple,
/// emblems) gives set 0 the base transform in c[18], set 1 the base then c[19]'s simple transform, and sets 2 and 3
/// the base scaled by c[20].xy and c[20].zw.
///
/// Animation (prometheus.md section 1.3, shader_engine.md section 5.2): an animated vertex constant is rebuilt
/// from its bitmap's transform parts (scale, translation, rotation about the texture centre). Parts with constant
/// curves, or curves of anything but time, are evaluated here (at input 0); time curves of the translation become
/// per-set u/v curves the shader evaluates every frame (the scrolling plasma, gas, clouds and holograms). Time
/// curves of scale or rotation are frozen at time 0. The stored constants are the animation at time 0, so
/// nothing jumps. Up to <see cref="MaxAnimatedConstants"/> pixel constants following time curves (colour pulses)
/// are evaluated by the shader too; the other time curves keep their stored value.
///
/// Object functions (health, shields, power...): with <see cref="ShaderInputs"/> (the mount's transparent and
/// plasma materials), pixel constants and transform parts following them take their value for the object at rest,
/// or 0 without an object, as Halo 2 does (see <see cref="AnimatedShader"/>). Without (null), pixel constants
/// keep their stored value and transform parts are taken at input 0.
/// </remarks>
public sealed class ShaderPassParameters
{
	public const int MaxStages = 4;
	public const int MaxAnimatedConstants = 3;

	// D3D9 texture address modes.
	const int AddressClamp = 3, AddressBorder = 4;

	/// <summary>
	/// Per stage t0-t3: the shader's bitmap slot (-1 for none) and whether the pass clamps it.
	/// </summary>
	public (int BitmapSlot, bool Clamp)[] Stages { get; } = new (int, bool)[MaxStages];

	/// <summary>
	/// Pixel shader registers c0-c7 (c0 unused).
	/// </summary>
	public Vector4[] Registers { get; } = new Vector4[8];

	public List<ShaderAnimatedConstant> AnimatedConstants { get; } = new();

	public ShaderUvSet[] UvSets { get; } = new ShaderUvSet[4];

	/// <summary>
	/// The vertex shader registers c[18]..c[33] as the pass writes them (animation at time 0 included), for vertex
	/// shaders that don't read them as the transparent family's UV rows (water: c[18] and c[20] are "simple"
	/// transforms, uv * xy + zw).
	/// </summary>
	public Vector4[] VertexRegisters { get; private set; }

	/// <summary>
	/// Time curves added to a register's x (U) and y (V) translation, by register (0 = c[18]).
	/// </summary>
	public IReadOnlyDictionary<int, (ShaderTimeCurve? U, ShaderTimeCurve? V)> VertexCurves { get; private set; }

	/// <summary>
	/// The fresnel pair: rgb tint and opacity head on (v10) and at grazing angles (v11).
	/// </summary>
	public Vector4 V10 { get; private set; } = Vector4.One;
	public Vector4 V11 { get; private set; } = Vector4.One;

	/// <param name="inputs">The object functions animation reads (null: pixel constants following them keep their
	/// stored value).</param>
	public ShaderPassParameters( ShaderTag shader, ShaderTemplatePass pass, ShaderInputs inputs = null )
	{
		for ( var stage = 0; stage < MaxStages; stage++ )
		{
			var texture = pass.TextureAtStage( stage );
			Stages[stage] = (texture?.BitmapSlot ?? -1, texture is not null && texture.AddressU is AddressClamp or AddressBorder);
		}

		// Pixel constants: colours are ARGB, a byte each; component k of the register takes channel k of
		// (R, G, B, A), and rgb the colour.
		foreach ( var remap in pass.PixelConstants )
		{
			if ( remap.Source < shader.PixelConstants.Count && remap.Register is >= 1 and <= 7 )
				Write( ref Registers[remap.Register], remap.Mask, Color( shader.PixelConstants[remap.Source] ), scalar: false );
		}

		var animated = ShaderAnimation.Read( shader.Tag, pass.TemplateImplementation );
		foreach ( var parameter in animated.Where( p => p.Kind == ShaderAnimatedKind.PixelConstant ) )
		{
			var (overlay, _) = parameter.Overlays.FirstOrDefault();
			int register = parameter.Target & 0x1F, component = (parameter.Target >> 5) & 7;
			if ( overlay is null || register is < 1 or > 7 )
				continue;

			var function = overlay.Function;
			if ( function.IsEmpty )
				continue;

			// The output at curve fraction f: the colour gradient or the output range.
			Vector4 Output( float f ) => function.IsColor
				? Color( function.ColorAt( f ) )
				: new Vector4( function.Minimum + (function.Maximum - function.Minimum) * f );

			if ( function.Type == MappingFunction.Constant )
			{
				// A constant curve: its value replaces the stored one; with a range, the range input blends its two ends (the energy blade's brightness).
				Write( ref Registers[register], component, Output( function.Curve( 0, inputs?.Value( overlay.Range ) ?? 0 ) ), scalar: !function.IsColor );
			}
			else if ( !overlay.IsTime && inputs is not null )
			{
				// An object function: its value for the object at rest.
				var value = Output( function.Curve( inputs.Value( overlay.Input ), inputs.Value( overlay.Range ) ) );
				if ( !function.IsColor )
					value = new Vector4( ShaderAnimation.Modify( parameter.Modifier, value.X ) );
				Write( ref Registers[register], component, value, scalar: !function.IsColor );
			}
			else if ( overlay.IsTime && AnimatedConstants.Count < MaxAnimatedConstants && ShaderTimeCurve.Of( overlay ) is { } curve )
			{
				AnimatedConstants.Add( new ShaderAnimatedConstant( curve with { A = curve.A with { Z = 0, W = 1 } }, Output( 0 ), Output( 1 ), register, component ) );
			}
		}

		ReadVertexConstants( shader, pass, animated, inputs );
	}

	static void Write( ref Vector4 register, int component, Vector4 value, bool scalar )
	{
		if ( component >= 4 )
		{
			register = new Vector4( value.X, value.Y, value.Z, register.W );
			return;
		}

		var v = scalar ? value.X : component switch { 0 => value.X, 1 => value.Y, 2 => value.Z, _ => value.W };
		register = component switch
		{
			0 => register with { X = v },
			1 => register with { Y = v },
			2 => register with { Z = v },
			_ => register with { W = v },
		};
	}

	void ReadVertexConstants( ShaderTag shader, ShaderTemplatePass pass, IReadOnlyList<ShaderAnimatedParameter> animated, ShaderInputs inputs )
	{
		var emissive = pass.VertexShader.Equals( "stencil_emissive", StringComparison.OrdinalIgnoreCase );
		var stencil = pass.VertexShader.Equals( "stencil_texture", StringComparison.OrdinalIgnoreCase );

		// c[18]..c[33], starting as identity transforms.
		var c = new Vector4[16];
		for ( var n = 0; n < c.Length; n++ )
			c[n] = stencil && n == 2 ? Vector4.One : emissive || stencil ? new Vector4( 1, 1, 0, 0 ) : n % 2 == 0 ? new Vector4( 1, 0, 0, 0 ) : new Vector4( 0, 1, 0, 0 );

		foreach ( var remap in pass.VertexConstants )
		{
			if ( shader.GetVertexConstant( remap.Source ) is not { } v )
				continue;

			if ( remap.Bank )
			{
				if ( remap.Register < c.Length )
					c[remap.Register] = Masked( c[remap.Register], remap.Mask, v );
			}
			else if ( remap.Register == 10 )
			{
				V10 = Masked( V10, remap.Mask, v );
			}
			else if ( remap.Register == 11 )
			{
				V11 = Masked( V11, remap.Mask, v );
			}
		}

		// Animated vertex constants: rebuilt from their bitmap transform; time curves of the translation go to
		// the shader, per register (u from the x translation, v from the y).
		var curves = new Dictionary<int, (ShaderTimeCurve? U, ShaderTimeCurve? V)>();
		foreach ( var parameter in animated.Where( p => p.Kind == ShaderAnimatedKind.VertexConstant ) )
		{
			int register = parameter.Target & 0xF, mask = (parameter.Target >> 5) & 0x1F;
			if ( ((parameter.Target >> 4) & 1) == 0 || register >= c.Length )
				continue;

			float sx = 1, sy = 1, tx = 0, ty = 0, rotation = 0;
			ShaderTimeCurve? curveU = null, curveV = null;
			foreach ( var (overlay, part) in parameter.Overlays )
			{
				if ( overlay.Function.IsEmpty )
					continue;

				var timeCurve = overlay.Function.Type is not MappingFunction.Constant ? ShaderTimeCurve.Of( overlay ) : null;
				var value = overlay.IsTime || inputs is null
					? overlay.Function.Evaluate( 0, inputs?.Value( overlay.Range ) ?? 0 )
					: overlay.Function.Evaluate( inputs.Value( overlay.Input ), inputs.Value( overlay.Range ) );
				switch ( part )
				{
					case 1: sx = value; break;
					case 2: sy = value; break;
					case 4: if ( timeCurve is not null ) curveU = timeCurve; else tx = value; break;
					case 5: if ( timeCurve is not null ) curveV = timeCurve; else ty = value; break;
					case 7: rotation = value; break;
				}
			}

			var cos = MathF.Cos( rotation * MathF.Tau );
			var sin = MathF.Sin( rotation * MathF.Tau );
			var rowU = new Vector4( sx * cos, -sx * sin, 0, 0.5f * sx * (1 - cos + sin) + tx );
			var rowV = new Vector4( sy * sin, sy * cos, 0, 0.5f * sy * (1 - cos - sin) + ty );
			c[register] = mask switch
			{
				13 => new Vector4( rowU.X, rowV.Y, rowU.W, rowV.W ),
				14 => c[register] with { X = rowU.X, Y = rowU.Y, W = rowU.W },
				15 => c[register] with { X = rowV.X, Y = rowV.Y, W = rowV.W },
				_ => c[register],
			};

			var existing = curves.GetValueOrDefault( register );
			curves[register] = mask switch
			{
				13 => (curveU, curveV),
				14 => (curveU, existing.V),
				15 => (existing.U, curveV),
				_ => existing,
			};
		}

		VertexRegisters = c;
		VertexCurves = curves;

		for ( var set = 0; set < UvSets.Length; set++ )
		{
			if ( stencil )
			{
				// Base transform in c[18]; set 1 adds c[19]'s simple transform, sets 2 and 3 scale by c[20].
				var b = c[0];
				var s = set switch { 1 => c[1], 2 => new Vector4( c[2].X, c[2].Y, 0, 0 ), 3 => new Vector4( c[2].Z, c[2].W, 0, 0 ), _ => new Vector4( 1, 1, 0, 0 ) };
				var curve = curves.GetValueOrDefault( 0 );
				UvSets[set] = new ShaderUvSet( new( b.X * s.X, 0, b.Z * s.X + s.Z ), new( 0, b.Y * s.Y, b.W * s.Y + s.W ), curve.U, curve.V );
			}
			else if ( emissive )
			{
				// Base transform in c[18], then this set's simple transform.
				var b = c[0];
				var s = c[1 + set];
				var curve = curves.GetValueOrDefault( 1 + set );
				UvSets[set] = new ShaderUvSet( new( b.X * s.X, 0, b.Z * s.X + s.Z ), new( 0, b.Y * s.Y, b.W * s.Y + s.W ), curve.U, curve.V );
			}
			else
			{
				var rowU = c[2 * set];
				var rowV = c[2 * set + 1];
				UvSets[set] = new ShaderUvSet( new( rowU.X, rowU.Y, rowU.W ), new( rowV.X, rowV.Y, rowV.W ),
					curves.GetValueOrDefault( 2 * set ).U, curves.GetValueOrDefault( 2 * set + 1 ).V );
			}
		}
	}

	/// <summary>
	/// Writes a vertex constant into a register through a remap mask (prometheus.md section 1.1).
	/// </summary>
	static Vector4 Masked( Vector4 register, int mask, Vector4 v ) => mask switch
	{
		0 => register with { X = v.X },
		1 => register with { Y = v.Y },
		2 => register with { Z = v.Z },
		3 => register with { W = v.W },
		4 => new Vector4( v.X, v.Y, v.Z, register.W ),
		9 or 11 => register with { X = v.X, Y = v.Y },
		10 or 12 => register with { Z = v.X, W = v.Y },
		13 or 18 or 19 or 20 => v,
		14 or 15 => register with { X = v.X, Y = v.Y, W = v.W },
		16 or 17 => new Vector4( v.X, v.Y, v.Z, register.W ),
		_ => register,
	};

	/// <summary>
	/// An ARGB colour as (r, g, b, a), 0-1.
	/// </summary>
	public static Vector4 Color( uint argb ) => MappingFunction.ToVector( argb );
}
