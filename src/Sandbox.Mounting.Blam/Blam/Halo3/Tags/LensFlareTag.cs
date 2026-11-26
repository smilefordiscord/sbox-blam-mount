using System;
using System.Collections.Generic;
using System.Linq;

namespace Blam.Halo3.Tags;

using Vector3 = System.Numerics.Vector3;

// One reflection of a Halo 3 lens flare (0x9C): flags (1 rotate from the screen centre, 2 radius scaled by distance, 4 radius scaled by occlusion, 8 ignore external colour, 0x80 mirror across the flare, 0x200/0x400 flip u/v, 0x800 drawn in world space, 0x1000 legacy Halo 3 flare), bitmap image or override, rotation offset (degrees), axis offset (0 on the flare, toward 1 across the screen centre), radius and x/y scales and brightness (the upper ends of their curves: an object's input is 1), colour, modulation factor, tint power.
public sealed record LensFlareReflection( int Flags, int BitmapIndex, TagEntry BitmapOverride, float RotationOffset, float AxisOffset, float Radius, float ScaleX, float ScaleY, float Brightness,
	Vector3 Color, float ModulationFactor, float TintPower );

// A Halo 3 lens flare (lens, MCC layout: Assembly's Halo3MCC plugin checked against every map's tags by BlamInspect h3 flare-survey).
// Falloff/cutoff angles +0x0/+0x4 (radians, equal: no angle fade), occlusion offset distance +0xC and direction +0x10 (0 toward the viewer, 1 marker forward, 2 none), near fade +0x14..+0x1C, far fade +0x20 (0 none), bitmap +0x24, flags +0x34 (2 no occlusion test, 4/8 first/third person only), reflections +0x44.
public sealed class LensFlareTag
{
	public static readonly TagClass Class = new( "lens" );

	public TagEntry Tag { get; }
	public float FalloffAngle { get; }
	public float CutoffAngle { get; }
	public float OcclusionOffsetDistance { get; }
	public int OcclusionOffsetDirection { get; }
	public float NearFadeDistance { get; }
	public float FarFadeDistance { get; }
	public TagEntry Bitmap { get; }
	public int Flags { get; }
	public IReadOnlyList<LensFlareReflection> Reflections { get; }

	public bool NoOcclusionTest => (Flags & 2) != 0;
	public bool FirstPersonOnly => (Flags & 4) != 0;

	public LensFlareTag( TagEntry tag )
	{
		Tag = tag;
		var d = tag.Data;
		FalloffAngle = d.F32( 0 );
		CutoffAngle = d.F32( 4 );
		OcclusionOffsetDistance = d.F32( 0xC );
		OcclusionOffsetDirection = d.I16( 0x10 );
		NearFadeDistance = d.F32( 0x1C );
		FarFadeDistance = d.F32( 0x20 );
		Bitmap = d.Reference( 0x24 );
		Flags = d.U16( 0x34 );
		Reflections = d.Block( 0x44, 0x9C ).Select( r => new LensFlareReflection( r.U16( 0 ), r.I16( 4 ), r.Reference( 8 ), r.F32( 0x18 ), r.F32( 0x1C ),
			Upper( r.Data( 0x38 ) ), Upper( r.Data( 0x4C ) ), Upper( r.Data( 0x60 ) ), Upper( r.Data( 0x74 ) ), r.Vec3( 0x88 ), r.F32( 0x94 ), r.F32( 0x98 ) ) ).ToList();
	}

	// A function's value at input 1: a constant's value (+0x4), else its output maximum (+0x8); 1 without a function.
	static float Upper( ReadOnlySpan<byte> function )
	{
		if ( function.Length < 0xC )
			return 1;
		return function[0] == 1 ? BitConverter.ToSingle( function[4..] ) : BitConverter.ToSingle( function[8..] );
	}
}
