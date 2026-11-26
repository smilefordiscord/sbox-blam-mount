using System;
using System.Collections.Generic;

namespace Blam.Halo1.Tags;

// Inside the namespace so these win over s&box's global Vector2/Vector3 when this compiles into the mount.
using Vector3 = System.Numerics.Vector3;

/// <summary>
/// How a collection's sprites turn toward the camera.
/// </summary>
public enum DetailObjectFacing : ushort
{
	/// <summary>
	/// Always face the camera, even when looked at from above.
	/// </summary>
	ScreenFacing = 0,

	/// <summary>
	/// Stand upright and spin around the vertical axis to face the camera. Used for grass.
	/// </summary>
	ViewerFacing = 1,
}

[Flags]
public enum DetailObjectTypeFlags : byte
{
	/// <summary>
	/// The random color variation between the minimum and maximum color is interpolated in HSV
	/// rather than RGB.
	/// </summary>
	InterpolateColorInHsv = 0x4,

	/// <summary>
	/// The HSV interpolation goes the long way around the hue circle.
	/// </summary>
	MoreColors = 0x8,
}

/// <summary>
/// One kind of detail object in a collection: a set of sprites from the collection's sprite
/// plate, drawn at a fixed scale.
/// </summary>
/// <param name="SequenceIndex">Sequence of the sprite plate the sprites come from.</param>
/// <param name="FirstSpriteIndex">First sprite of the sequence used (filled in when the map was built).</param>
/// <param name="SpriteCount">Number of sprites used. Each placed object picks one of them.</param>
/// <param name="ColorOverrideFactor">How much of the object's own color replaces the color of the ground it was painted on. Baked into each placed object.</param>
/// <param name="NearFadeDistance">Distance from the camera where the objects start fading out, in world units.</param>
/// <param name="FarFadeDistance">Distance where they're gone.</param>
/// <param name="Size">World units per sprite pixel.</param>
/// <param name="MinimumColor">One end of the random color range. Baked into each placed object.</param>
/// <param name="MaximumColor">The other end.</param>
/// <param name="AmbientColor">Color mixed with the lightmap's (by its alpha) when the colors were baked, as ARGB.</param>
public sealed record DetailObjectType(
	string Name,
	int SequenceIndex,
	DetailObjectTypeFlags Flags,
	int FirstSpriteIndex,
	int SpriteCount,
	float ColorOverrideFactor,
	float NearFadeDistance,
	float FarFadeDistance,
	float Size,
	Vector3 MinimumColor,
	Vector3 MaximumColor,
	uint AmbientColor );

/// <summary>
/// Reader for <c>dobc</c> (detail object collection) tags: the grass, plants and pebbles painted
/// onto a level's ground, drawn as camera-facing sprites near the player.
/// </summary>
/// <remarks>
/// Offsets: collection type 0x0, global z offset 0x4, sprite plate (bitmap reference) 0x34, types
/// block 0x44 (96 bytes each: name, sequence index 0x20, flags 0x21, first sprite 0x22, sprite
/// count 0x23, color override 0x24, near/far fade 0x30/0x34, size 0x38, minimum/maximum color
/// 0x40/0x4C, ambient color 0x58). A scenario lists its collections in a block of tag references
/// at 0x3C0; the objects themselves are stored in the structure BSPs (see <see cref="DetailObjects"/>).
/// </remarks>
public sealed class DetailObjectCollectionTag
{
	public TagEntry Tag { get; }
	public DetailObjectFacing Facing { get; }

	/// <summary>
	/// Height added to every object of the collection, in world units.
	/// </summary>
	public float GlobalZOffset { get; }

	/// <summary>
	/// The sprite sheet bitmap the types' sprites come from.
	/// </summary>
	public TagEntry SpritePlate { get; }

	public IReadOnlyList<DetailObjectType> Types { get; }

	public DetailObjectCollectionTag( CacheFile cache, TagEntry tag )
	{
		var data = cache.TagData;
		var a = tag.Address;

		Tag = tag;
		Facing = (DetailObjectFacing)data.U16( a );
		GlobalZOffset = data.F32( a + 0x4 );
		SpritePlate = cache.GetTagReference( data, a + 0x34 );

		var types = new List<DetailObjectType>();
		foreach ( var t in data.Block( a + 0x44, 0x60 ) )
		{
			types.Add( new DetailObjectType(
				Name: data.CString( t, 32 ),
				SequenceIndex: data.U8( t + 0x20 ),
				Flags: (DetailObjectTypeFlags)data.U8( t + 0x21 ),
				FirstSpriteIndex: data.U8( t + 0x22 ),
				SpriteCount: data.U8( t + 0x23 ),
				ColorOverrideFactor: data.F32( t + 0x24 ),
				NearFadeDistance: data.F32( t + 0x30 ),
				FarFadeDistance: data.F32( t + 0x34 ),
				Size: data.F32( t + 0x38 ),
				MinimumColor: data.Vec3( t + 0x40 ),
				MaximumColor: data.Vec3( t + 0x4C ),
				AmbientColor: data.U32( t + 0x58 ) ) );
		}
		Types = types;
	}

	/// <summary>
	/// The scenario's detail object collections, in palette order. Entries without a tag are null.
	/// </summary>
	public static IReadOnlyList<DetailObjectCollectionTag> ReadPalette( CacheFile cache, TagEntry scenario )
	{
		var collections = new List<DetailObjectCollectionTag>();
		foreach ( var entry in cache.TagData.Block( scenario.Address + 0x3C0, 0x10 ) )
		{
			var tag = cache.GetTagReference( cache.TagData, entry );
			collections.Add( tag is null ? null : new DetailObjectCollectionTag( cache, tag ) );
		}

		return collections;
	}
}
