using System;
using System.Collections.Generic;
using System.Linq;

namespace Blam.Halo3.Tags;

// One decal of a decal system: its render method (shaders\decal), radius range (world units), how steep a surface it clamps to and culls at, and whether it draws before or after lighting.
public sealed record DecalSystemEntry( string Name, uint Flags, ShaderTag RenderMethod, float RadiusMin, float RadiusMax, float ClampAngle, float CullAngle, int RuntimePass, float SpecularMultiplier, float BitmapAspect )
{
	public bool PostLighting => RuntimePass == 1;
}

// A decal system (decs): the decals it picks from and how they're laid onto surfaces.
public sealed class DecalSystemTag
{
	public static readonly TagClass Class = new( "decs" );

	public TagEntry Tag { get; }

	// 1 random rotation, 2/4 random u/v mirror, 8 force quad, 0x10 force planar, 0x20 restrict to a single material, 0x40 primary collision only, 0x80 not on structure, 0x100 not on instances.
	public uint Flags { get; }
	public float FadeBegin { get; }
	public float FadeEnd { get; }
	public IReadOnlyList<DecalSystemEntry> Decals { get; }
	public float MaximumRadius { get; }

	public bool RandomRotation => (Flags & 1) != 0;
	public bool ForcePlanar => (Flags & 0x10) != 0;
	public bool AvoidStructure => (Flags & 0x80) != 0;
	public bool AvoidInstances => (Flags & 0x100) != 0;

	public DecalSystemTag( TagEntry tag )
	{
		Tag = tag;
		var data = tag.Data;
		Flags = data.U32( 0 );
		FadeBegin = data.F32( 0xC );
		FadeEnd = data.F32( 0x10 );
		MaximumRadius = data.F32( 0x20 );
		Decals = data.Block( 0x14, 0x74 )
			.Select( e => new DecalSystemEntry( e.StringId( 0 ), e.U32( 4 ), ShaderTag.Get( tag, e.At( 8 ) ), e.F32( 0x48 ), e.F32( 0x4C ), e.F32( 0x60 ), e.F32( 0x64 ), e.I32( 0x68 ), e.F32( 0x6C ), e.F32( 0x70 ) ) )
			.ToList();
	}
}
