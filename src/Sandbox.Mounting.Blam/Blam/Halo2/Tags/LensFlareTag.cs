using System.Collections.Generic;
using System.Linq;

namespace Blam.Halo2.Tags;

using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;

/// <summary>
/// One reflection of a lens flare (0x30): flags (1 align rotation with the screen centre, 2 radius not scaled by
/// distance, 4 radius scaled by occlusion, 8 occluded by solid objects, 0x10 ignore light colour, 0x20 not affected by
/// inner occlusion), bitmap image, position along the light-to-screen-centre line (0 on the light, 1 opposite),
/// rotation offset, radius and brightness ranges (interpolated by an external input), modulation factor and colour.
/// </summary>
public sealed record LensFlareReflection( int Flags, int BitmapIndex, float Position, float RotationOffset, Vector2 Radius, Vector2 Brightness,
	float ModulationFactor, Vector3 Color );

/// <summary>
/// A Halo 2 lens flare (<c>lens</c>). Layout (Assembly's Halo 2 plugin as documentation, every map read by BlamInspect
/// <c>h2 &lt;maps&gt; lensflares</c>): falloff/cutoff angles 0x0/0x4 (and their cosines 0x8/0xC), occlusion radius
/// 0x10, occlusion offset direction 0x14, inner radius scale 0x16, near/far fade distances 0x18/0x1C, bitmap 0x20,
/// flags 0x28 (1 sun, 2 no occlusion test, 4 only first person, 8 only third person, 0x10/0x20 fade in/out more
/// quickly, 0x40 scale by marker), rotation function 0x2C and scale 0x30, corona scale 0x34, falloff function 0x3C,
/// reflections 0x40 (0x30), animation flags 0x48, brightness/colour/rotation function blocks 0x4C/0x54/0x5C.
/// </summary>
public sealed class LensFlareTag
{
	public static readonly TagClass Class = new( "lens" );

	public TagEntry Tag { get; }
	public float FalloffAngle { get; }
	public float CutoffAngle { get; }
	public float CosineFalloff { get; }
	public float CosineCutoff { get; }
	public float OcclusionRadius { get; }
	public int OcclusionOffsetDirection { get; }
	public int OcclusionInnerRadiusScale { get; }
	public float NearFadeDistance { get; }
	public float FarFadeDistance { get; }
	public TagEntry Bitmap { get; }
	public int Flags { get; }
	public int RotationFunction { get; }
	public float RotationFunctionScale { get; }
	public Vector2 CoronaScale { get; }
	public int FalloffFunction { get; }
	public IReadOnlyList<LensFlareReflection> Reflections { get; }
	public int AnimationFlags { get; }
	public IReadOnlyList<MappingFunction> BrightnessFunctions { get; }
	public IReadOnlyList<MappingFunction> ColorFunctions { get; }
	public IReadOnlyList<MappingFunction> RotationFunctions { get; }

	public LensFlareTag( TagEntry tag )
	{
		Tag = tag;
		var cache = tag.Cache;
		var data = cache.TagData;
		var t = tag.Address;

		MappingFunction Function( uint address )
		{
			var size = data.I32( address );
			var pointer = data.U32( address + 4 );
			return new MappingFunction( size > 0 && data.Contains( pointer, size ) ? data.Slice( pointer, size ).ToArray() : [] );
		}

		FalloffAngle = data.F32( t );
		CutoffAngle = data.F32( t + 0x4 );
		CosineFalloff = data.F32( t + 0x8 );
		CosineCutoff = data.F32( t + 0xC );
		OcclusionRadius = data.F32( t + 0x10 );
		OcclusionOffsetDirection = data.I16( t + 0x14 );
		OcclusionInnerRadiusScale = data.I16( t + 0x16 );
		NearFadeDistance = data.F32( t + 0x18 );
		FarFadeDistance = data.F32( t + 0x1C );
		Bitmap = cache.GetTagReference( t + 0x20 );
		Flags = data.U16( t + 0x28 );
		RotationFunction = data.I16( t + 0x2C );
		RotationFunctionScale = data.F32( t + 0x30 );
		CoronaScale = data.Vec2( t + 0x34 );
		FalloffFunction = data.I16( t + 0x3C );
		Reflections = data.Elements( t + 0x40, 0x30 ).Select( r => new LensFlareReflection( data.U16( r ), data.I16( r + 0x4 ), data.F32( r + 0x8 ),
			data.F32( r + 0xC ), data.Vec2( r + 0x10 ), data.Vec2( r + 0x18 ), data.F32( r + 0x20 ), data.Vec3( r + 0x24 ) ) ).ToList();
		AnimationFlags = data.U16( t + 0x48 );
		BrightnessFunctions = data.Elements( t + 0x4C, 8 ).Select( Function ).ToList();
		ColorFunctions = data.Elements( t + 0x54, 8 ).Select( Function ).ToList();
		RotationFunctions = data.Elements( t + 0x5C, 8 ).Select( Function ).ToList();
	}

	public bool IsSun => (Flags & 1) != 0;

	public static bool IsLensFlare( TagEntry tag ) => tag is not null && tag.Address != 0 && tag.Class == Class;
}
