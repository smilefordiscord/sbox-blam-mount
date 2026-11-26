using System;
using System.Collections.Generic;
using System.Linq;

namespace Blam.Halo2.Tags;

// Inside the namespace so these win over s&box's global vector types when this compiles into the mount.
using Vector4 = System.Numerics.Vector4;

/// <summary>
/// One animation curve of a shader: a function of an input (an object function by name, or time when
/// <see cref="Input"/> is empty: time / <see cref="Period"/>, wrapped to 0-1 unless the function is periodic) and of
/// a range input (<see cref="Range"/>, the blend between a range function's two curves).
/// </summary>
public sealed record ShaderOverlay( string Input, string Range, float Period, MappingFunction Function )
{
	public bool IsTime => string.IsNullOrEmpty( Input );
}

/// <summary>
/// What an animated parameter drives.
/// </summary>
public enum ShaderAnimatedKind { BitmapTransform, RenderState, TextureState, PixelConstant, VertexConstant }

/// <summary>
/// One animated shader parameter and its curves.
/// </summary>
/// <param name="Target">Bitmap transforms: the bitmap parameter of the pass implementation (low byte; its stem
/// bitmap remap, not the shader's bitmap slot). Pixel constants: the register (flags as in a pixel remap). Vertex
/// constants: a vertex remap's flags, plus bits 10-12 = the bitmap parameter whose transform feeds it.</param>
/// <param name="Modifier">Scalar outputs: 0 none, 1 1 - v, 2 0.5 + 0.5 cos 2 pi v, 3 0.5 - 0.5 cos, 4 0.5 + 0.5 sin,
/// 5 0.5 - 0.5 sin.</param>
/// <param name="Overlays">The curves and which part each drives: for bitmap transforms 0 the bitmap (frame) index,
/// 1-3 scale x/y/z, 4-6 translate x/y/z, 7 rotation (turns), 8-10 rotation axis; -1 the value or colour.</param>
public sealed record ShaderAnimatedParameter( ShaderAnimatedKind Kind, int Target, int Modifier, IReadOnlyList<(ShaderOverlay Overlay, int Part)> Overlays );

/// <summary>
/// A shader's animation: per template implementation, which parameters animate and how (prometheus.md
/// section 1.3, shader_engine.md section 5.2).
/// </summary>
public static class ShaderAnimation
{
	/// <summary>
	/// The animated parameters of one template implementation (<see cref="ShaderTemplatePass.TemplateImplementation"/>).
	/// </summary>
	public static IReadOnlyList<ShaderAnimatedParameter> Read( TagEntry shader, int templateImplementation )
	{
		var result = new List<ShaderAnimatedParameter>();
		var cache = shader.Cache;
		var data = cache.TagData;
		var postprocess = data.Elements( shader.Address + 0x20, 0x7C ).FirstOrDefault();
		if ( postprocess == 0 )
			return result;

		int implementationCount = data.I32( postprocess + 0x34 ), overlayCount = data.I32( postprocess + 0x3C );
		if ( templateImplementation >= implementationCount || overlayCount == 0 )
			return result;

		var overlays = ReadOverlays( cache, postprocess );

		var overlayRefs = data.U32( postprocess + 0x44 + 4 );
		var parameters = data.U32( postprocess + 0x4C + 4 );
		var parameterRefs = data.U32( postprocess + 0x54 + 4 );
		var implementation = data.U32( postprocess + 0x34 + 4 ) + (uint)templateImplementation * 0xA;

		for ( var kind = 0; kind < 5; kind++ )
		{
			var (start, count) = Range( data.U16( implementation + (uint)kind * 2 ) );
			for ( var q = start; q < start + count; q++ )
			{
				var e = parameterRefs + (uint)q * 4;
				var target = data.U16( e );
				var modifier = data.U8( e + 2 );
				var parameter = data.U8( e + 3 );
				var (overlayStart, overlayCountForParameter) = Range( data.U16( parameters + (uint)parameter * 2 ) );

				var list = new List<(ShaderOverlay, int)>();
				for ( var o = overlayStart; o < overlayStart + overlayCountForParameter; o++ )
				{
					var r = overlayRefs + (uint)o * 4;
					var overlay = data.I16( r );
					if ( overlay >= 0 && overlay < overlays.Count )
						list.Add( (overlays[overlay], data.I16( r + 2 )) );
				}

				result.Add( new ShaderAnimatedParameter( (ShaderAnimatedKind)kind, target, modifier, list ) );
			}
		}

		return result;
	}

	// Every curve of the shader's animation, whichever parameters use it.
	public static IReadOnlyList<ShaderOverlay> Overlays( TagEntry shader )
	{
		var postprocess = shader.Cache.TagData.Elements( shader.Address + 0x20, 0x7C ).FirstOrDefault();
		return postprocess == 0 ? [] : ReadOverlays( shader.Cache, postprocess );
	}

	static List<ShaderOverlay> ReadOverlays( CacheFile cache, uint postprocess )
	{
		var data = cache.TagData;
		var overlays = new List<ShaderOverlay>();
		foreach ( var e in data.Elements( postprocess + 0x3C, 0x14 ) )
		{
			var size = data.I32( e + 0xC );
			var address = data.U32( e + 0x10 );
			var blob = size > 0 && data.Contains( address, size ) ? data.Slice( address, size ).ToArray() : [];
			overlays.Add( new ShaderOverlay( StringOf( cache, data.U32( e ) ), StringOf( cache, data.U32( e + 4 ) ), data.F32( e + 8 ), new MappingFunction( blob ) ) );
		}

		return overlays;
	}

	/// <summary>
	/// Whether any of the shader's animation reads the object function <paramref name="input"/> (as an input or a range).
	/// </summary>
	public static bool ReadsInput( TagEntry shader, string input )
	{
		var cache = shader.Cache;
		var data = cache.TagData;
		var postprocess = data.Elements( shader.Address + 0x20, 0x7C ).FirstOrDefault();
		return postprocess != 0 && data.Elements( postprocess + 0x3C, 0x14 ).Any( e =>
			string.Equals( StringOf( cache, data.U32( e ) ), input, StringComparison.OrdinalIgnoreCase )
			|| string.Equals( StringOf( cache, data.U32( e + 4 ) ), input, StringComparison.OrdinalIgnoreCase ) );
	}

	/// <summary>
	/// Whether the shader has any animation at all.
	/// </summary>
	public static bool HasAnimation( TagEntry shader )
	{
		var data = shader.Cache.TagData;
		var postprocess = data.Elements( shader.Address + 0x20, 0x7C ).FirstOrDefault();
		return postprocess != 0 && data.I32( postprocess + 0x3C ) > 0 && data.I32( postprocess + 0x54 ) > 0;
	}

	/// <summary>
	/// A scalar output through a parameter's modifier, clamped to [0, 1] when there is one.
	/// </summary>
	public static float Modify( int modifier, float v ) => modifier switch
	{
		1 => Math.Clamp( 1 - v, 0, 1 ),
		2 => Math.Clamp( 0.5f + 0.5f * MathF.Cos( MathF.Tau * v ), 0, 1 ),
		3 => Math.Clamp( 0.5f - 0.5f * MathF.Cos( MathF.Tau * v ), 0, 1 ),
		4 => Math.Clamp( 0.5f + 0.5f * MathF.Sin( MathF.Tau * v ), 0, 1 ),
		5 => Math.Clamp( 0.5f - 0.5f * MathF.Sin( MathF.Tau * v ), 0, 1 ),
		_ => v,
	};

	static string StringOf( CacheFile cache, uint id ) => id == 0 ? "" : cache.GetString( id ) ?? "";

	static (int Start, int Count) Range( ushort value ) => (value & 0x1FF, value >> 9);
}

/// <summary>
/// The object function values shader animation reads, for an object at rest (or for no object at all).
/// </summary>
/// <remarks>
/// Halo 2 asks the object being drawn for its function by the overlay's input name; with no object (level
/// geometry, the sky) every input is 0 (shader_engine.md section 5.2). Objects here are placed and never change, so
/// they get the values of an undamaged, idle object: full health, shields and ammunition, alive, unburnt, not
/// turned heretic, not a holiday; a stable random number for <c>random_constant</c>; everything else (motion,
/// firing, emblems, scripted functions, functions an object defines for itself) 0. Time is not an input here: see
/// <see cref="AnimatedShader"/>.
/// </remarks>
public sealed class ShaderInputs
{
	/// <summary>
	/// No object: level geometry and the sky. Every input is 0.
	/// </summary>
	public static readonly ShaderInputs None = new( false, 0 );

	/// <summary>
	/// An object at rest.
	/// </summary>
	/// <param name="randomConstant">The object's <c>random_constant</c> (0-1, stable for the object).</param>
	public static ShaderInputs AtRest( float randomConstant ) => new( true, randomConstant );

	/// <summary>
	/// An object at rest drawing <paramref name="shader"/>: <paramref name="randomConstant"/> when the object's is
	/// known (<see cref="ObjectCreationOrder.RandomConstant"/>), else a stable hash of the shader's name.
	/// </summary>
	public static ShaderInputs ForObject( TagEntry shader, float? randomConstant ) =>
		randomConstant is { } r ? AtRest( r ) : AtRest( shader.Path );

	/// <summary>
	/// An object at rest whose <c>random_constant</c> is a stable hash of <paramref name="key"/>, 0-1.
	/// </summary>
	public static ShaderInputs AtRest( string key )
	{
		// FNV-1a, then a finaliser: the same number every time the map loads.
		var h = 2166136261u;
		foreach ( var c in key ?? "" )
			h = (h ^ char.ToLowerInvariant( c )) * 16777619u;
		h ^= h >> 13;
		h *= 0x5bd1e995u;
		h ^= h >> 15;
		return AtRest( (h & 0xFFFFFF) / (float)0x1000000 );
	}

	/// <summary>
	/// Inputs that are 1 on an object at rest: its vitality and supplies are full and it is alive.
	/// </summary>
	public static readonly IReadOnlySet<string> Full = new HashSet<string>( StringComparer.OrdinalIgnoreCase )
	{
		"health", "body_vitality", "shield_vitality", "vitality", "alive", "ammo", "primary_ammunition", "secondary_ammunition",
		"total_primary_ammunition", "total_secondary_ammunition", "primary_rounds_loaded", "secondary_rounds_loaded", "charge",
	};

	public bool HasObject { get; }
	public float RandomConstant { get; }

	/// <summary>Functions with a value other than at rest (<see cref="With"/>).</summary>
	readonly IReadOnlyDictionary<string, float> _values;

	ShaderInputs( bool hasObject, float randomConstant, IReadOnlyDictionary<string, float> values = null )
	{
		HasObject = hasObject;
		RandomConstant = randomConstant;
		_values = values;
	}

	/// <summary>
	/// These inputs with some functions set to other values (an object taking damage, say: the shield shaders'
	/// <c>shield_hit</c>), the rest as they are.
	/// </summary>
	public ShaderInputs With( IReadOnlyDictionary<string, float> values ) =>
		new( true, RandomConstant, values is null ? _values : new Dictionary<string, float>( values, StringComparer.OrdinalIgnoreCase ) );

	/// <summary>
	/// The value of an object function by name (an overlay's input or range).
	/// </summary>
	public float Value( string name )
	{
		if ( !HasObject || string.IsNullOrEmpty( name ) )
			return 0;
		if ( _values is not null && _values.TryGetValue( name, out var value ) )
			return value;
		if ( name.Equals( "random_constant", StringComparison.OrdinalIgnoreCase ) )
			return RandomConstant;
		return Full.Contains( name ) ? 1 : 0;
	}
}

/// <summary>
/// How an animated parameter ends up in a material.
/// </summary>
public enum AnimatedTiming
{
	/// <summary>Driven by object functions (or a constant): evaluated once, for the object at rest.</summary>
	Static,

	/// <summary>Driven by time, and the s&amp;box shader evaluates the curve every frame.</summary>
	Shader,

	/// <summary>Driven by time in a way the s&amp;box shader doesn't evaluate: frozen at time 0.</summary>
	Frozen,

	/// <summary>Its target isn't one of the shader's slots in that pass (nothing to do).</summary>
	Unresolved,
}

/// <summary>
/// One animated parameter as it lands on a shader slot, for inspection.
/// </summary>
/// <param name="Kind">Pixel constant, vertex constant or bitmap transform (the frame index).</param>
/// <param name="Slot">The shader's pixel constant, vertex constant or bitmap slot (-1 unresolved).</param>
/// <param name="Target">Where the pass writes it, e.g. <c>c7.xyz</c>, <c>c[18] mask 13 from bitmap 1</c>.</param>
/// <param name="Curves">The overlays: part, input (at rest), function type.</param>
/// <param name="Stored">The slot's stored value; <paramref name="Rest"/> the value evaluated at rest (time 0 for time;
/// pixel constants following time keep the stored value in <see cref="AnimatedShader.Shader"/>).</param>
public sealed record AnimatedSlot( ShaderAnimatedKind Kind, int Slot, string Pass, int Implementation, string Target, string Curves,
	string Stored, string Rest, AnimatedTiming Timing );

/// <summary>
/// A pixel constant slot following a time curve: the curve (output 0-1) blends <see cref="Minimum"/> to
/// <see cref="Maximum"/> (r, g, b, a) into the slot's component (0-3 r, g, b, a) or rgb (4).
/// </summary>
public sealed record ShaderSlotCurve( int Slot, int Component, ShaderTimeCurve Curve, Vector4 Minimum, Vector4 Maximum );

/// <summary>
/// A vertex constant slot holding a 2D texture transform (scale xy, offset zw) whose offsets follow time curves,
/// added to the stored offsets (which then hold only the static part of the translation).
/// </summary>
public sealed record ShaderSlotUvCurve( int Slot, ShaderTimeCurve? U, ShaderTimeCurve? V );

/// <summary>
/// A shader with its animation applied: its parameters as an object at rest (or level geometry) shows them, and
/// what keeps moving with time.
/// </summary>
/// <remarks>
/// Animated parameters target a pass's registers (prometheus.md section 1.3); they're mapped back to the shader's
/// slots through the pass's remaps (the register's pixel or vertex remap, the bitmap parameter's bitmap remap), so
/// anything reading slots (the opaque template table) sees the animated values. The template's passes are walked at
/// LOD0, the layers drawn first (lightmap, texture accumulation, self-illumination, environment map, PRT), each
/// implementation; the first pass that animates a slot wins (they agree).
///
/// - Pixel constants: colour targets (rgb) take the function's colour gradient, the others its scalar output
///   through the parameter's modifier, stored back as a byte of the slot (as MCC's stored constants are).
/// - Vertex constants holding a bitmap's transform are rebuilt from its parts (scale, translation, rotation about the
///   texture centre, Halo 2's composition), parts without a curve at identity.
/// - Bitmap transform part 0 is the bitmap's frame (image index, wrapped to its images): emblems, ammunition
///   digits, heretic and burnt variants, random decal variants.
///
/// Object inputs are evaluated once (<see cref="ShaderInputs"/>). Time curves the s&amp;box shaders evaluate
/// (identity, transition, periodic, linear, exponent of time) become <see cref="ConstantCurves"/> (pixel constants
/// without a modifier) and <see cref="UvCurves"/> (translations of a 2D simple transform, left out of the slot).
/// Everything else driven by time (spline and key curves, scale and rotation, frames) is frozen at time 0. Pixel
/// constants following time keep their stored value: MCC's tools store the value at time 0 (checked over every
/// map: equal to the evaluation wherever the wave shape is reproduced exactly).
/// </remarks>
public sealed class AnimatedShader
{
	/// <summary>
	/// Layers in the order their passes claim slots: lightmap, texture accumulation, self-illumination, environment
	/// map, PRT, then the rest.
	/// </summary>
	static readonly int[] LayerOrder = [3, 1, 7, 8, 5, .. Enumerable.Range( 0, 25 ).Where( l => l is not (3 or 1 or 7 or 8 or 5) )];

	const int MaxImplementations = 8;

	/// <summary>
	/// The shader with its parameters at rest (the same tag when nothing animates).
	/// </summary>
	public ShaderTag Shader { get; }

	public IReadOnlyList<AnimatedSlot> Slots => _slots;
	public IReadOnlyList<ShaderSlotCurve> ConstantCurves => _constantCurves;
	public IReadOnlyList<ShaderSlotUvCurve> UvCurves => _uvCurves;

	// Bitmap slots whose frame follows an object function (the battle rifle's ammunition digits): the curve and the bitmap's image count.
	public IReadOnlyList<(int Slot, ShaderOverlay Overlay, int Count)> ObjectFrames => _objectFrames;

	readonly List<(int, ShaderOverlay, int)> _objectFrames = new();
	readonly List<AnimatedSlot> _slots = new();
	readonly List<ShaderSlotCurve> _constantCurves = new();
	readonly List<ShaderSlotUvCurve> _uvCurves = new();

	public AnimatedShader( ShaderTag shader, ShaderInputs inputs )
	{
		Shader = shader;
		if ( shader.Template is null || !ShaderAnimation.HasAnimation( shader.Tag ) )
			return;

		var pixel = shader.PixelConstants.ToArray();
		var vertex = shader.VertexConstants.ToArray();
		var bitmaps = shader.Bitmaps.ToArray();
		var claimed = new HashSet<(ShaderAnimatedKind, int, int)>();
		var implementations = new HashSet<int>();
		var template = new ShaderTemplateTag( shader.Template );

		foreach ( var layer in LayerOrder )
		{
			for ( var implementation = 0; implementation < MaxImplementations; implementation++ )
			{
				foreach ( var pass in template.Passes( layer, implementation ) )
				{
					if ( !implementations.Add( pass.TemplateImplementation ) )
						continue;

					var animated = ShaderAnimation.Read( shader.Tag, pass.TemplateImplementation );
					foreach ( var parameter in animated )
					{
						switch ( parameter.Kind )
						{
							case ShaderAnimatedKind.PixelConstant:
								PixelConstant( parameter, pass, inputs, pixel, claimed );
								break;
							case ShaderAnimatedKind.VertexConstant:
								VertexConstant( parameter, animated, pass, inputs, vertex, claimed );
								break;
							case ShaderAnimatedKind.BitmapTransform:
								BitmapFrame( parameter, pass, inputs, bitmaps, claimed );
								break;
						}
					}
				}
			}
		}

		Shader = shader.WithValues( bitmaps, pixel, vertex );
	}

	/// <summary>
	/// An overlay's input and range values: time 0 for time, the object's functions otherwise.
	/// </summary>
	static (float X, float Y) Inputs( ShaderOverlay overlay, ShaderInputs inputs ) =>
		(overlay.IsTime ? 0 : inputs.Value( overlay.Input ), inputs.Value( overlay.Range ));

	static string Describe( IEnumerable<(ShaderOverlay Overlay, int Part)> overlays, ShaderInputs inputs ) => string.Join( "; ", overlays.Select( o =>
	{
		var f = o.Overlay.Function;
		var input = o.Overlay.IsTime ? $"time/{o.Overlay.Period:0.###}s" : $"{o.Overlay.Input}={inputs.Value( o.Overlay.Input ):0.###}";
		var range = string.IsNullOrEmpty( o.Overlay.Range ) ? "" : $" range {o.Overlay.Range}={inputs.Value( o.Overlay.Range ):0.###}";
		var output = f.IsEmpty ? "empty" : f.Type == MappingFunction.Constant && !f.IsColor ? $"= {f.Minimum:0.###}" : f.IsColor ? $"colour mode {f.ColorMode}" : $"{f.Minimum:0.###}..{f.Maximum:0.###}";
		var shape = f.Type is MappingFunction.Periodic or MappingFunction.Transition ? $" shape {f.Shape}" : "";
		return $"{(o.Part >= 0 ? $"part {o.Part} " : "")}{f.TypeName}{shape}({input}{range}) {output}";
	} ) );

	/// <summary>
	/// Whether a pixel remap writing <paramref name="remapMask"/> covers the animated component (0-3, 4 rgb).
	/// </summary>
	static bool Covers( int remapMask, int component ) => remapMask == component || (remapMask == 4 && component < 3);

	static readonly int[] ByteShift = [16, 8, 0, 24];

	void PixelConstant( ShaderAnimatedParameter parameter, ShaderTemplatePass pass, ShaderInputs inputs, uint[] pixel, HashSet<(ShaderAnimatedKind, int, int)> claimed )
	{
		int register = parameter.Target & 0x1F, component = (parameter.Target >> 5) & 7;
		var target = $"c{register}.{(component >= 4 ? "xyz" : "xyzw"[component].ToString())}";
		var remap = pass.PixelConstants.FirstOrDefault( r => r.Register == register && Covers( r.Mask, component ) );
		var slot = remap?.Source ?? -1;
		var (overlay, _) = parameter.Overlays.FirstOrDefault();
		if ( slot < 0 || slot >= pixel.Length || overlay is null || overlay.Function.IsEmpty )
		{
			_slots.Add( new AnimatedSlot( parameter.Kind, -1, pass.Name, pass.TemplateImplementation, target, Describe( parameter.Overlays, inputs ), "", "", AnimatedTiming.Unresolved ) );
			return;
		}

		// Claimed per slot and component: two passes writing the same one agree.
		if ( !claimed.Add( (parameter.Kind, slot, component) ) )
			return;

		var function = overlay.Function;
		var (x, y) = Inputs( overlay, inputs );
		var stored = pixel[slot];
		Vector4 Output( float f ) => component >= 4
			? MappingFunction.ToVector( function.ColorAt( f ) )
			: new Vector4( ShaderAnimation.Modify( parameter.Modifier, Lerp( function.Minimum, function.Maximum, f ) ) );

		// Colours: the rgb (the slot keeps its alpha, the register's w comes from elsewhere). Scalars: a byte.
		var evaluated = component >= 4
			? (stored & 0xFF000000) | (function.ColorAt( function.Curve( x, y ) ) & 0x00FFFFFF)
			: (stored & ~(0xFFu << ByteShift[component])) | (uint)Math.Clamp( (int)MathF.Round( Output( function.Curve( x, y ) ).X * 255, MidpointRounding.AwayFromZero ), 0, 255 ) << ByteShift[component];

		var timing = AnimatedTiming.Static;
		if ( !overlay.IsTime || function.Type == MappingFunction.Constant )
		{
			pixel[slot] = evaluated;
		}
		else
		{
			// Time: the stored value is MCC's own value at time 0 (the same as evaluated, but for the random wave
			// shapes the tables of which aren't reproduced), so the slot keeps it.
			timing = AnimatedTiming.Frozen;
			if ( parameter.Modifier == 0 && (!function.IsRange || y == 0) && ShaderTimeCurve.Of( overlay ) is { } curve )
			{
				// The shader's curve gives 0-1; the colour or value at the ends goes with it (a gradient through
				// three or four colours is cut to its ends).
				_constantCurves.Add( new ShaderSlotCurve( slot, component, curve with { A = curve.A with { Z = 0, W = 1 } }, Output( 0 ), Output( 1 ) ) );
				timing = AnimatedTiming.Shader;
			}
		}

		_slots.Add( new AnimatedSlot( parameter.Kind, slot, pass.Name, pass.TemplateImplementation, target, Describe( parameter.Overlays, inputs ),
			$"{stored:x8}", $"{evaluated:x8} = {ColorText( evaluated, component )}", timing ) );
	}

	static string ColorText( uint argb, int component )
	{
		var v = MappingFunction.ToVector( argb );
		return component >= 4 ? $"rgb ({v.X:0.###}, {v.Y:0.###}, {v.Z:0.###})" : $"{"rgba"[component]} {(component switch { 0 => v.X, 1 => v.Y, 2 => v.Z, _ => v.W }):0.###}";
	}

	void VertexConstant( ShaderAnimatedParameter parameter, IReadOnlyList<ShaderAnimatedParameter> animated, ShaderTemplatePass pass, ShaderInputs inputs,
		Vector4[] vertex, HashSet<(ShaderAnimatedKind, int, int)> claimed )
	{
		int register = parameter.Target & 0xF, mask = (parameter.Target >> 5) & 0x1F, bitmapParameter = (parameter.Target >> 10) & 7;
		var bank = ((parameter.Target >> 4) & 1) != 0;
		var target = $"{(bank ? $"c[{18 + register}]" : $"v{register}")} mask {mask}{(mask >= 5 ? $" from bitmap {bitmapParameter}" : "")}";
		// The remap filling the components this writes (a register can take its xyz and its w from two slots).
		var remap = pass.VertexConstants.Where( r => r.Register == register && r.Bank == bank && (Written( r.Mask ) & Written( mask )) != 0 )
			.MaxBy( r => System.Numerics.BitOperations.PopCount( (uint)(Written( r.Mask ) & Written( mask )) ) );
		var slot = remap?.Source ?? -1;

		// Transforms come from the bitmap's transform parts, the other masks from the parameter's own curve.
		var overlays = parameter.Overlays;
		if ( mask >= 5 && animated.FirstOrDefault( p => p.Kind == ShaderAnimatedKind.BitmapTransform && (p.Target & 0xFF) == bitmapParameter ) is { } transform )
			overlays = transform.Overlays;

		if ( slot < 0 || slot >= vertex.Length || overlays.Count == 0 || (mask < 5 && overlays[0].Overlay.Function.IsEmpty) )
		{
			_slots.Add( new AnimatedSlot( parameter.Kind, -1, pass.Name, pass.TemplateImplementation, target, Describe( overlays, inputs ), "", "", AnimatedTiming.Unresolved ) );
			return;
		}

		if ( !claimed.Add( (parameter.Kind, slot, Written( mask )) ) )
			return;

		var stored = vertex[slot];
		var register4 = ToRegister( remap.Mask, stored );
		var timing = AnimatedTiming.Static;
		var objectDriven = false;
		ShaderTimeCurve? curveU = null, curveV = null;

		if ( mask >= 5 )
		{
			// Scale x/y/z, translate x/y/z, rotation (turns): parts without a curve stay at identity.
			var parts = new float[] { 0, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0 };
			foreach ( var (overlay, part) in overlays )
			{
				if ( part is < 1 or > 10 || overlay.Function.IsEmpty )
					continue;

				var (x, y) = Inputs( overlay, inputs );
				var timeCurve = overlay.IsTime && overlay.Function.Type != MappingFunction.Constant;
				objectDriven |= !overlay.IsTime && overlay.Function.Type != MappingFunction.Constant;
				if ( timeCurve && mask == 13 && remap.Mask == 13 && (part is 4 or 5) && (!overlay.Function.IsRange || y == 0) && ShaderTimeCurve.Of( overlay ) is { } curve )
				{
					// The shader adds the moving translation; the slot keeps the rest.
					if ( part == 4 ) curveU = curve; else curveV = curve;
					if ( timing != AnimatedTiming.Frozen ) timing = AnimatedTiming.Shader;
					continue;
				}

				if ( timeCurve )
					timing = AnimatedTiming.Frozen;
				parts[part] = overlay.Function.Evaluate( x, y );
			}

			register4 = Compose( mask, parts, register4 );
		}
		else
		{
			var (overlay, _) = overlays[0];
			var (x, y) = Inputs( overlay, inputs );
			var f = overlay.Function.Curve( x, y );
			if ( mask == 4 )
			{
				var color = MappingFunction.ToVector( overlay.Function.ColorAt( f ) );
				register4 = new Vector4( color.X, color.Y, color.Z, register4.W );
			}
			else
			{
				var v = ShaderAnimation.Modify( parameter.Modifier, Lerp( overlay.Function.Minimum, overlay.Function.Maximum, f ) );
				register4 = mask switch { 0 => register4 with { X = v }, 1 => register4 with { Y = v }, 2 => register4 with { Z = v }, _ => register4 with { W = v } };
			}

			if ( overlay.IsTime && overlay.Function.Type != MappingFunction.Constant )
				timing = AnimatedTiming.Frozen;
			objectDriven = !overlay.IsTime && overlay.Function.Type != MappingFunction.Constant;
		}

		// Frozen in time with nothing from the object: the stored value is MCC's own value at time 0 (exact, where
		// the evaluation approximates the random wave shapes).
		var evaluated = FromRegister( remap.Mask, register4, stored );
		vertex[slot] = timing == AnimatedTiming.Frozen && !objectDriven ? stored : evaluated;
		if ( curveU is not null || curveV is not null )
			_uvCurves.Add( new ShaderSlotUvCurve( slot, curveU, curveV ) );

		_slots.Add( new AnimatedSlot( parameter.Kind, slot, pass.Name, pass.TemplateImplementation, target, Describe( overlays, inputs ),
			Text( stored ), Text( evaluated ) + (curveU is not null || curveV is not null ? $" + time curves on {(curveU is not null ? "u" : "")}{(curveV is not null ? "v" : "")}" : ""), timing ) );
	}

	static string Text( Vector4 v ) => $"({v.X:0.###}, {v.Y:0.###}, {v.Z:0.###}, {v.W:0.###})";

	/// <summary>
	/// The register components a vertex remap mask writes (bits 0-3 x-w).
	/// </summary>
	static int Written( int mask ) => mask switch
	{
		0 => 1, 1 => 2, 2 => 4, 3 => 8,
		4 or 16 or 17 => 7,
		5 => 1, 6 => 2, 7 => 4, 8 => 8,
		9 or 11 => 3,
		10 or 12 => 12,
		14 or 15 => 11,
		_ => 15,
	};

	/// <summary>
	/// A bitmap's transform written through a vertex remap mask (prometheus.md section 1.1): u' = sx cos u - sx sin v
	/// + tx', v' = sy sin u + sy cos v + ty', rotating about the texture centre.
	/// </summary>
	static Vector4 Compose( int mask, float[] p, Vector4 r )
	{
		float sx = p[1], sy = p[2], sz = p[3], tx = p[4], ty = p[5], tz = p[6], rotation = p[7];
		float cos = 1, sin = 0;
		if ( MathF.Abs( rotation ) >= 1e-4f )
		{
			cos = MathF.Cos( rotation * MathF.Tau );
			sin = MathF.Sin( rotation * MathF.Tau );
		}

		var u = 0.5f * sx * (1 - cos + sin) + tx;
		var v = 0.5f * sy * (1 - cos - sin) + ty;
		return mask switch
		{
			5 => r with { X = sx * cos },
			6 => r with { Y = sx * cos },
			7 => r with { Z = sx * cos },
			8 => r with { W = sx * cos },
			9 => r with { X = sx * cos, Y = sy * cos },
			10 => r with { Z = sx * cos, W = sy * cos },
			11 => r with { X = u, Y = v },
			12 => r with { Z = u, W = v },
			13 => new Vector4( sx * cos, sy * cos, u, v ),
			14 => new Vector4( sx * cos, -sx * sin, 0, u ),
			15 => new Vector4( sy * sin, sy * cos, 0, v ),
			16 => new Vector4( sx * cos, sy * cos, sz, r.W ),
			17 => new Vector4( u, v, tz, r.W ),
			_ => r,
		};
	}

	/// <summary>
	/// The register a vertex remap fills from a slot (the components it doesn't write are 0).
	/// </summary>
	static Vector4 ToRegister( int mask, Vector4 slot ) => mask switch
	{
		0 => new Vector4( slot.X, 0, 0, 0 ),
		1 => new Vector4( 0, slot.Y, 0, 0 ),
		2 => new Vector4( 0, 0, slot.Z, 0 ),
		3 => new Vector4( 0, 0, 0, slot.W ),
		10 or 12 => new Vector4( 0, 0, slot.X, slot.Y ),
		_ => slot,
	};

	/// <summary>
	/// The slot value that fills a register through a vertex remap: the inverse of <see cref="ToRegister"/>.
	/// </summary>
	static Vector4 FromRegister( int mask, Vector4 register, Vector4 slot ) => mask switch
	{
		0 => slot with { X = register.X },
		1 => slot with { Y = register.Y },
		2 => slot with { Z = register.Z },
		3 => slot with { W = register.W },
		4 or 16 or 17 => new Vector4( register.X, register.Y, register.Z, slot.W ),
		9 or 11 => slot with { X = register.X, Y = register.Y },
		10 or 12 => slot with { X = register.Z, Y = register.W },
		14 or 15 => slot with { X = register.X, Y = register.Y, W = register.W },
		_ => register,
	};

	void BitmapFrame( ShaderAnimatedParameter parameter, ShaderTemplatePass pass, ShaderInputs inputs, ShaderBitmap[] bitmaps, HashSet<(ShaderAnimatedKind, int, int)> claimed )
	{
		// Only the frame (part 0) belongs to the bitmap; the other parts reach the pass through vertex constants.
		var frame = parameter.Overlays.Where( o => o.Part == 0 && !o.Overlay.Function.IsEmpty ).Select( o => o.Overlay ).FirstOrDefault();
		if ( frame is null )
			return;

		var bitmapParameter = parameter.Target & 0xFF;
		var target = $"bitmap {bitmapParameter} frame";
		var slot = bitmapParameter < pass.Bitmaps.Count ? pass.Bitmaps[bitmapParameter] : -1;
		if ( slot < 0 || slot >= bitmaps.Length )
		{
			_slots.Add( new AnimatedSlot( parameter.Kind, -1, pass.Name, pass.TemplateImplementation, target, Describe( parameter.Overlays, inputs ), "", "", AnimatedTiming.Unresolved ) );
			return;
		}

		if ( !claimed.Add( (parameter.Kind, slot, 0) ) )
			return;

		var (x, y) = Inputs( frame, inputs );
		var stored = bitmaps[slot];
		var value = frame.Function.Evaluate( x, y );

		// Halo 2 wraps the frame to the bitmap's images (fmod by their count, then floor), negative frames are 0:
		// a constant 1 on a one-image bitmap (the tools' default for this part) is its image 0.
		var count = stored.Bitmap is { } tag && tag.Class == BitmapTag.Class ? new BitmapTag( tag ).Bitmaps.Count : 0;
		var image = value < 0 || count <= 0 ? 0 : (int)MathF.Floor( value % count );

		bitmaps[slot] = stored with { Image = image };
		if ( !frame.IsTime && frame.Function.Type != MappingFunction.Constant && count > 1 )
			_objectFrames.Add( (slot, frame, count) );
		var timing = frame.IsTime && frame.Function.Type != MappingFunction.Constant ? AnimatedTiming.Frozen : AnimatedTiming.Static;
		_slots.Add( new AnimatedSlot( parameter.Kind, slot, pass.Name, pass.TemplateImplementation, target, Describe( parameter.Overlays.Where( o => o.Part == 0 ), inputs ),
			$"image {stored.Image}", $"image {image}", timing ) );
	}

	static float Lerp( float a, float b, float t ) => a + (b - a) * t;
}
