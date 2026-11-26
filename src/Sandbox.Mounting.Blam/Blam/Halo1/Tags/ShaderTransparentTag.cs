using System;
using System.Collections.Generic;
using System.Linq;

namespace Blam.Halo1.Tags;

// Inside the namespace so these win over s&box's global Vector2/Vector3 when this compiles into the mount.
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;
using Vector4 = System.Numerics.Vector4;

[Flags]
public enum TransparentShaderFlags : byte
{
	AlphaTested = 0x1,
	Decal = 0x2,
	TwoSided = 0x4,
	FirstMapIsInScreenspace = 0x8,
	DrawBeforeWater = 0x10,
	IgnoreEffect = 0x20,
	ScaleFirstMapWithDistance = 0x40,
	Numeric = 0x80,
}

public enum FirstMapType : ushort
{
	Map2D = 0,
	ReflectionCubeMap = 1,
	ObjectCenteredCubeMap = 2,
	ViewerCenteredCubeMap = 3,
}

/// <summary>
/// How a transparent shader's result is combined with what's already been drawn.
/// </summary>
public enum FramebufferBlendFunction : ushort
{
	AlphaBlend = 0,
	Multiply = 1,
	DoubleMultiply = 2,
	Add = 3,
	Subtract = 4,
	ComponentMin = 5,
	ComponentMax = 6,
	AlphaMultiplyAdd = 7,
}

public enum FramebufferFadeMode : ushort
{
	None = 0,
	FadeWhenPerpendicular = 1,
	FadeWhenParallel = 2,
}

/// <summary>
/// How a chicago map combines the result so far ("current") with the next map.
/// </summary>
public enum MapFunction : ushort
{
	Current = 0,
	NextMap = 1,
	Multiply = 2,
	DoubleMultiply = 3,
	Add = 4,
	AddSignedCurrent = 5,
	AddSignedNextMap = 6,
	SubtractCurrent = 7,
	SubtractNextMap = 8,
	BlendCurrentAlpha = 9,
	BlendCurrentAlphaInverse = 10,
	BlendNextMapAlpha = 11,
	BlendNextMapAlphaInverse = 12,
}

public enum AnimationFunction : ushort
{
	One = 0,
	Zero = 1,
	Cosine = 2,
	CosineVariablePeriod = 3,
	DiagonalWave = 4,
	DiagonalWaveVariablePeriod = 5,
	Slide = 6,
	SlideVariablePeriod = 7,
	Noise = 8,
	Jitter = 9,
	Wander = 10,
	Spark = 11,
}

/// <summary>
/// One texture coordinate animation, e.g. a scrolling U offset.
/// </summary>
public readonly record struct MapAnimation( AnimationFunction Function, float Period, float Phase, float Scale );

/// <summary>
/// One texture layer of a transparent shader.
/// </summary>
/// <param name="UAnimationSource">On an object, the U animation's amplitude is scaled by this
/// object function (A-D out); none is 1.</param>
/// <param name="VAnimationSource">The same for V.</param>
public sealed record TransparentMap(
	TagEntry Map,
	Vector2 Scale,
	Vector2 Offset,
	MapFunction ColorFunction,
	MapFunction AlphaFunction,
	MapAnimation UAnimation,
	MapAnimation VAnimation,
	ChangeColorSource UAnimationSource = ChangeColorSource.None,
	ChangeColorSource VAnimationSource = ChangeColorSource.None );

/// <summary>
/// One register combiner stage of a generic transparent shader: four color and four alpha inputs
/// read from registers, remapped, multiplied in pairs (or dotted) and summed (or muxed), then
/// scaled and written back to registers. Enum values are Halo's, see the shader for their meaning.
/// </summary>
/// <param name="ColorInputs">Input register and mapping for A, B, C, D.</param>
/// <param name="ColorOutputs">Registers written with AB, CD and AB+CD, and the output mapping.</param>
/// <param name="ColorFunctions">Whether AB and CD are dot products instead of products.</param>
/// <param name="Constant0">The stage's constant color 0 (a steady value of its animation).</param>
/// <param name="Constant0Bounds">Constant color 0's animation runs between these.</param>
/// <param name="AOutControlsConstant0">On an object, the object's A out picks constant color 0
/// between its bounds instead of the animation.</param>
/// <param name="Constant0Source">On an object, constant color 0's rgb is multiplied by the object's
/// change color A-D (1-4); 0 none.</param>
public sealed record TransparentStage(
	(int Input, int Mapping)[] ColorInputs,
	(int Ab, int Cd, int AbCd, int Mapping) ColorOutputs,
	(bool AbDot, bool CdDot) ColorFunctions,
	bool ColorMux,
	(int Input, int Mapping)[] AlphaInputs,
	(int Ab, int Cd, int AbCd, int Mapping) AlphaOutputs,
	bool AlphaMux,
	Vector4 Constant0,
	Vector4 Constant1,
	(Vector4 Lower, Vector4 Upper) Constant0Bounds,
	bool AOutControlsConstant0,
	int Constant0Source );

/// <summary>
/// Reader for the transparent effect shaders: <c>schi</c> (shader_transparent_chicago),
/// <c>scex</c> (shader_transparent_chicago_extended) and <c>sotr</c> (shader_transparent_generic).
/// </summary>
/// <remarks>
/// All three share a header: numeric counter limit 0x28 (8 bit), flags 0x29 (8 bit), first map type 0x2A, framebuffer blend function
/// 0x2C, framebuffer fade mode 0x2E, framebuffer fade source 0x30 (A-D out: on an object the
/// result fades by that function), then a block of maps at 0x54. Map animations are a source (A-D
/// out, which scales the animation on an object) followed by function, period, phase and scale.
///
/// Chicago maps are 220 bytes: color/alpha functions 0x2C/0x2E, U/V scale 0x54/0x58, U/V offset
/// 0x5C/0x60, map reference 0x6C, U animation 0xA4 (function 0xA6, period, phase, scale) and V
/// animation 0xB4. The functions say how each map combines with the next one.
///
/// Generic maps are 100 bytes: U/V scale 0x4, U/V offset 0xC, map reference 0x1C, U animation
/// 0x2C, V animation 0x3C. Generic shaders combine their maps with register combiner stages at
/// 0x60 (112 bytes: flags 0x0 (0x1 color mux, 0x2 alpha mux, 0x4 A out controls color 0), color 0
/// source 0x4 (A-D: multiplied by that change color), animation function 0x6 and bounds 0xC/0x1C, color 1 0x2C,
/// color inputs 0x3C, color outputs 0x4C, alpha inputs 0x58, alpha outputs 0x68). Colors are
/// stored alpha, red, green, blue.
/// </remarks>
public sealed class ShaderTransparentTag
{
	public TransparentShaderFlags Flags { get; }

	/// <summary>
	/// With <see cref="TransparentShaderFlags.Numeric"/>: the number an object function of 1 shows.
	/// The shader's bitmaps are then digits, one picked per model part by its digit place.
	/// </summary>
	public int NumericCounterLimit { get; }

	/// <summary>
	/// Chicago shaders that show a scripted countdown timer instead of an object function.
	/// Chicago extra flags (bit 1) follow the maps block: 0x60, or 0x6C for chicago extended.
	/// </summary>
	public bool NumericCountdownTimer { get; }

	public FirstMapType FirstMapType { get; }
	public FramebufferBlendFunction BlendFunction { get; }
	public FramebufferFadeMode FadeMode { get; }

	/// <summary>
	/// On an object, the object function (A-D out) the result fades by; none is no fade.
	/// </summary>
	public ChangeColorSource FadeSource { get; }

	public IReadOnlyList<TransparentMap> Maps { get; }

	/// <summary>
	/// Register combiner stages, only for generic shaders.
	/// </summary>
	public IReadOnlyList<TransparentStage> Stages { get; }

	public static bool IsTransparent( TagClass tagClass ) =>
		tagClass == TagClasses.ShaderTransparentChicago ||
		tagClass == TagClasses.ShaderTransparentChicagoExtended ||
		tagClass == TagClasses.ShaderTransparentGeneric;

	/// <summary>
	/// Whether a shader is left out of its object's effect (<see cref="TransparentShaderFlags.IgnoreEffect"/>).
	/// Only generic and chicago shaders can be; chicago extended ones never are.
	/// </summary>
	public static bool IgnoresEffect( CacheFile cache, TagEntry shader ) =>
		(shader.Class == TagClasses.ShaderTransparentGeneric || shader.Class == TagClasses.ShaderTransparentChicago) &&
		new ShaderTransparentTag( cache, shader ).Flags.HasFlag( TransparentShaderFlags.IgnoreEffect );

	public ShaderTransparentTag( CacheFile cache, TagEntry tag )
	{
		if ( !IsTransparent( tag.Class ) )
			throw new ArgumentException( $"{tag} is not a transparent shader tag", nameof( tag ) );

		var data = cache.TagData;
		var a = tag.Address;

		NumericCounterLimit = data.U8( a + 0x28 );
		Flags = (TransparentShaderFlags)data.U8( a + 0x29 );
		if ( tag.Class == TagClasses.ShaderTransparentChicago )
			NumericCountdownTimer = (data.U32( a + 0x60 ) & 0x2) != 0;
		else if ( tag.Class == TagClasses.ShaderTransparentChicagoExtended )
			NumericCountdownTimer = (data.U32( a + 0x6C ) & 0x2) != 0;
		FirstMapType = (FirstMapType)data.U16( a + 0x2A );
		BlendFunction = (FramebufferBlendFunction)data.U16( a + 0x2C );
		FadeMode = (FramebufferFadeMode)data.U16( a + 0x2E );
		FadeSource = (ChangeColorSource)data.U16( a + 0x30 );

		var generic = tag.Class == TagClasses.ShaderTransparentGeneric;
		var maps = new List<TransparentMap>();

		foreach ( var map in data.Block( a + 0x54, generic ? 0x64 : 0xDC ) )
		{
			if ( generic )
			{
				maps.Add( new TransparentMap(
					Map: cache.GetTagReference( data, map + 0x1C ),
					Scale: new Vector2( DefaultOne( data.F32( map + 0x4 ) ), DefaultOne( data.F32( map + 0x8 ) ) ),
					Offset: data.Vec2( map + 0xC ),
					ColorFunction: MapFunction.Multiply,
					AlphaFunction: MapFunction.Multiply,
					UAnimation: ReadAnimation( data, map + 0x2C ),
					VAnimation: ReadAnimation( data, map + 0x3C ),
					UAnimationSource: (ChangeColorSource)data.U16( map + 0x2C ),
					VAnimationSource: (ChangeColorSource)data.U16( map + 0x3C ) ) );
			}
			else
			{
				maps.Add( new TransparentMap(
					Map: cache.GetTagReference( data, map + 0x6C ),
					Scale: new Vector2( DefaultOne( data.F32( map + 0x54 ) ), DefaultOne( data.F32( map + 0x58 ) ) ),
					Offset: data.Vec2( map + 0x5C ),
					ColorFunction: (MapFunction)data.U16( map + 0x2C ),
					AlphaFunction: (MapFunction)data.U16( map + 0x2E ),
					UAnimation: ReadAnimation( data, map + 0xA4 ),
					VAnimation: ReadAnimation( data, map + 0xB4 ),
					UAnimationSource: (ChangeColorSource)data.U16( map + 0xA4 ),
					VAnimationSource: (ChangeColorSource)data.U16( map + 0xB4 ) ) );
			}
		}

		Maps = maps;

		var stages = new List<TransparentStage>();
		if ( generic )
		{
			foreach ( var stage in data.Block( a + 0x60, 0x70 ) )
			{
				var flags = data.U16( stage );
				(int, int)[] Inputs( uint at ) => Enumerable.Range( 0, 4 ).Select( i => ((int)data.U16( at + (uint)(i * 4) ), (int)data.U16( at + (uint)(i * 4) + 2 )) ).ToArray();

				var function = (AnimationFunction)data.U16( stage + 0x6 );
				var lower = ReadArgb( data, stage + 0xC );
				var upper = ReadArgb( data, stage + 0x1C );
				var constant0 = function switch
				{
					AnimationFunction.One => upper,
					AnimationFunction.Zero => lower,
					_ => (lower + upper) * 0.5f,
				};

				stages.Add( new TransparentStage(
					ColorInputs: Inputs( stage + 0x3C ),
					ColorOutputs: (data.U16( stage + 0x4C ), data.U16( stage + 0x50 ), data.U16( stage + 0x54 ), data.U16( stage + 0x56 )),
					ColorFunctions: (data.U16( stage + 0x4E ) == 1, data.U16( stage + 0x52 ) == 1),
					ColorMux: (flags & 0x1) != 0,
					AlphaInputs: Inputs( stage + 0x58 ),
					AlphaOutputs: (data.U16( stage + 0x68 ), data.U16( stage + 0x6A ), data.U16( stage + 0x6C ), data.U16( stage + 0x6E )),
					AlphaMux: (flags & 0x2) != 0,
					Constant0: constant0,
					Constant1: ReadArgb( data, stage + 0x2C ),
					Constant0Bounds: (lower, upper),
					AOutControlsConstant0: (flags & 0x4) != 0,
					Constant0Source: data.I16( stage + 0x4 ) ) );
			}
		}
		Stages = stages;
	}

	/// <summary>
	/// Reads a color stored alpha, red, green, blue as (r, g, b, a).
	/// </summary>
	static Vector4 ReadArgb( IO.MemoryRegion data, uint address ) =>
		new( data.F32( address + 4 ), data.F32( address + 8 ), data.F32( address + 12 ), data.F32( address ) );

	/// <summary>
	/// Reads an animation's function, period, phase and scale (its source is read with the map).
	/// </summary>
	static MapAnimation ReadAnimation( IO.MemoryRegion data, uint address ) => new(
		(AnimationFunction)data.U16( address + 0x2 ),
		DefaultOne( data.F32( address + 0x4 ) ),
		data.F32( address + 0x8 ),
		DefaultOne( data.F32( address + 0xC ) ) );

	static float DefaultOne( float value ) => value == 0 ? 1 : value;
}
