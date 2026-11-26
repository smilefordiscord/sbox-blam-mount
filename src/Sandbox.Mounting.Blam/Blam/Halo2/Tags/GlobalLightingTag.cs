using System;
using System.Collections.Generic;
using System.Linq;

namespace Blam.Halo2.Tags;

// Inside the namespace so these win over s&box's global vector types when this compiles into the mount.
using Vector3 = System.Numerics.Vector3;

/// <summary>
/// One "lighting variables" element of a global lighting tag: how a lightmap sample becomes an object's
/// direct, indirect and ambient light, for the object types its flags name.
/// </summary>
public sealed record GlobalLightingElement(
	uint Flags,
	float LightmapBrightnessOffset,
	Vector3 PrimaryMin, Vector3 PrimaryMax,
	float ExclusionAngle,
	MappingFunction PrimaryFunction,
	Vector3 SecondaryMin, Vector3 SecondaryMax,
	Vector3 DiffuseMin, Vector3 DiffuseMax,
	float ZAxisRotation,
	MappingFunction SecondaryFunction,
	Vector3 AmbientMin, Vector3 AmbientMax,
	MappingFunction AmbientFunction )
{
	/// <summary>
	/// Whether this element lights objects of a type: flags bit 0 (every type) or bit <c>type + 1</c>.
	/// </summary>
	public bool Applies( ObjectType type ) => (Flags & 1) != 0 || (Flags & (1u << ((int)type + 1))) != 0;
}

/// <summary>
/// A Halo 2 global lighting tag (<c>gldf</c>), which turns the lightmap sample under an object into its
/// lighting. The scenario names one (+0x338), else the globals' default (<c>matg</c> +0x180). All of MCC's
/// maps use <c>globals\global_lighting_default</c> except Zanzibar (<c>_multiplayer</c>), 04b/07a
/// (<c>_brighter_mid</c>) and 07b (<c>_dark</c>).
/// </summary>
public sealed class GlobalLightingTag
{
	public static readonly TagClass Class = new( "gldf" );

	const int ElementSize = 0x90;
	const uint DefaultGlobalLighting = 0x180;

	public TagEntry Tag { get; }
	public IReadOnlyList<GlobalLightingElement> Elements { get; }

	public GlobalLightingTag( TagEntry tag )
	{
		Tag = tag;
		var cache = tag.Cache;
		var data = cache.TagData;

		MappingFunction Function( uint address )
		{
			var size = data.I32( address );
			var pointer = data.U32( address + 4 );
			return new MappingFunction( size > 0 && data.Contains( pointer, size ) ? data.Slice( pointer, size ).ToArray() : [] );
		}

		Elements = data.Elements( tag.Address, ElementSize ).Select( e => new GlobalLightingElement(
			Flags: data.U32( e ),
			LightmapBrightnessOffset: data.F32( e + 0x4 ),
			PrimaryMin: data.Vec3( e + 0x8 ), PrimaryMax: data.Vec3( e + 0x14 ),
			ExclusionAngle: data.F32( e + 0x20 ),
			PrimaryFunction: Function( e + 0x24 ),
			SecondaryMin: data.Vec3( e + 0x2C ), SecondaryMax: data.Vec3( e + 0x38 ),
			DiffuseMin: data.Vec3( e + 0x44 ), DiffuseMax: data.Vec3( e + 0x50 ),
			ZAxisRotation: data.F32( e + 0x5C ),
			SecondaryFunction: Function( e + 0x60 ),
			AmbientMin: data.Vec3( e + 0x68 ), AmbientMax: data.Vec3( e + 0x74 ),
			AmbientFunction: Function( e + 0x80 ) ) ).ToList();
	}

	/// <summary>
	/// The global lighting a scenario's objects are lit with: its own, else the globals' default. Null if neither exists.
	/// </summary>
	public static GlobalLightingTag For( CacheFile cache, TagEntry scenarioGlobalLighting )
	{
		var tag = scenarioGlobalLighting;
		if ( (tag is null || tag.Address == 0) && cache.Globals is { } globals )
			tag = globals.Cache.GetTagReference( globals.Address + DefaultGlobalLighting );

		return tag is null || tag.Address == 0 || tag.Class != Class ? null : new GlobalLightingTag( tag );
	}

	/// <summary>
	/// The element that lights an object type: the first whose flags name it.
	/// </summary>
	public GlobalLightingElement For( ObjectType type ) => Elements.FirstOrDefault( e => e.Applies( type ) );
}
