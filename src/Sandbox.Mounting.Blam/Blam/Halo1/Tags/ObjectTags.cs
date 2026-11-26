namespace Blam.Halo1.Tags;

// Inside the namespace so these win over s&box's global Vector2/Vector3 when this compiles into the mount.
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// Readers for the parts of object and sky tags the mount uses.
/// </summary>
public static class ObjectTags
{
	/// <summary>
	/// The model of any object tag (scenery, vehicle, weapon, biped...). Every object type starts
	/// with the shared object definition, which has the model reference at 0x28.
	/// </summary>
	public static TagEntry GetObjectModel( CacheFile cache, TagEntry objectTag ) =>
		cache.GetTagReference( cache.TagData, objectTag.Address + 0x28 );

	/// <summary>
	/// The animation graph (<c>antr</c>) of any object tag, at 0x38 after the model reference.
	/// </summary>
	public static TagEntry GetObjectAnimations( CacheFile cache, TagEntry objectTag ) =>
		cache.GetTagReference( cache.TagData, objectTag.Address + 0x38 );

	/// <summary>
	/// The collision model (<c>coll</c>) of any object tag, at 0x70, or null for objects nothing
	/// collides with.
	/// </summary>
	public static TagEntry GetObjectCollisionModel( CacheFile cache, TagEntry objectTag ) =>
		cache.GetTagReference( cache.TagData, objectTag.Address + 0x70 );

	/// <summary>
	/// The physics (<c>phys</c>) of any object tag, at 0x80: the mass points vehicles are simulated with.
	/// </summary>
	public static TagEntry GetObjectPhysics( CacheFile cache, TagEntry objectTag ) =>
		cache.GetTagReference( cache.TagData, objectTag.Address + 0x80 );

	/// <summary>
	/// Whether an object's transparent parts hide its own farther surfaces ("transparent self
	/// occlusion", bit 0x2 of the object flags at 0x2): Cortana's eyelids cover her eyes.
	/// </summary>
	public static bool TransparencySelfOccludes( CacheFile cache, TagEntry objectTag ) =>
		(cache.TagData.U16( objectTag.Address + 0x2 ) & 0x2) != 0;

	/// <summary>
	/// Whether an object never casts a shadow ("does not cast shadow", bit 0x1 of the object flags at 0x2).
	/// </summary>
	public static bool DoesNotCastShadow( CacheFile cache, TagEntry objectTag ) =>
		(cache.TagData.U16( objectTag.Address + 0x2 ) & 0x1) != 0;

	static readonly TagClass ActorVariant = new( "actv" );

	/// <summary>
	/// Every model some object or actor variant draws with a forced shader permutation, and that
	/// permutation: the object definition's at 0x13E, and an actor variant's at 0x20C for its unit
	/// (at 0x14). Every part of the model then draws that image of its shader's bitmaps in place
	/// of its own permutation's (the elite ranks' colored reflections). Zero forces none.
	/// </summary>
	public static IEnumerable<(TagEntry Model, int Permutation)> ForcedShaderPermutations( CacheFile cache )
	{
		var data = cache.TagData;
		foreach ( var tag in cache.Tags )
		{
			TagEntry objectTag;
			int permutation;
			if ( tag.Is( TagClasses.Object ) )
			{
				objectTag = tag;
				permutation = data.I16( tag.Address + 0x13E );
			}
			else if ( tag.Class == ActorVariant )
			{
				objectTag = cache.GetTagReference( data, tag.Address + 0x14 );
				permutation = data.I16( tag.Address + 0x20C );
			}
			else
				continue;

			if ( permutation > 0 && objectTag is not null && GetObjectModel( cache, objectTag ) is { } model && model.Class == TagClasses.GbxModel )
				yield return (model, permutation);
		}
	}

	/// <summary>
	/// Whether any object using the model has <see cref="TransparencySelfOccludes"/>.
	/// </summary>
	public static bool ModelSelfOccludes( CacheFile cache, TagEntry modelTag ) =>
		cache.Tags.Any( t => t.Is( TagClasses.Object ) && GetObjectModel( cache, t )?.Id == modelTag.Id && TransparencySelfOccludes( cache, t ) );

	/// <summary>
	/// The collision model to give a model: the one the most objects using the model have (ties
	/// go to the first in tag order), or null if none of them has one. Objects sharing a model
	/// nearly always share its collision too (<c>BlamInspect &lt;map&gt; collision</c>).
	/// </summary>
	public static TagEntry FindCollisionModel( CacheFile cache, TagEntry modelTag )
	{
		var counts = new Dictionary<uint, (TagEntry Tag, int Count, int Order)>();
		foreach ( var tag in cache.Tags )
		{
			if ( !tag.Is( TagClasses.Object ) || GetObjectModel( cache, tag )?.Id != modelTag.Id )
				continue;

			if ( GetObjectCollisionModel( cache, tag ) is not { } collision || collision.Class != CollisionModelTag.Class )
				continue;

			counts[collision.Id] = counts.TryGetValue( collision.Id, out var c ) ? (c.Tag, c.Count + 1, c.Order) : (collision, 1, counts.Count);
		}

		return counts.Values.OrderByDescending( c => c.Count ).ThenBy( c => c.Order ).Select( c => c.Tag ).FirstOrDefault();
	}
}

/// <summary>
/// A light in the sky (the sun) that lit the level when its lightmaps were made.
/// </summary>
/// <param name="Direction">Yaw and pitch in radians, pointing from the level toward the light.</param>
public sealed record SkyLight( Vector3 Color, float Power, Vector2 Direction );

/// <summary>
/// Reader for <c>sky</c> tags: the sky model, drawn around the camera at infinite distance, and
/// the atmospheric fog it gives the level: the outdoor fog while the camera's cluster has this sky,
/// the indoor fog while it has none.
/// </summary>
/// <remarks>
/// Offsets: model 0x0, outdoor ambient color/power 0x48/0x54, outdoor fog color 0x58, maximum
/// density 0x6C, start distance 0x70, opaque distance 0x74, indoor fog the same from 0x78
/// (distances in world units), lights block 0xC4 (116 bytes: color 0x50, power 0x5C, direction
/// 0x68). A maximum density of 0 is taken as 1.
/// </remarks>
public sealed class SkyTag
{
	public TagEntry Model { get; }
	public Vector3 OutdoorAmbientColor { get; }
	public float OutdoorAmbientPower { get; }
	public IReadOnlyList<SkyLight> Lights { get; }
	public Vector3 OutdoorFogColor { get; }
	public float OutdoorFogMaximumDensity { get; }
	public float OutdoorFogStartDistance { get; }
	public float OutdoorFogOpaqueDistance { get; }
	public Vector3 IndoorFogColor { get; }
	public float IndoorFogMaximumDensity { get; }
	public float IndoorFogStartDistance { get; }
	public float IndoorFogOpaqueDistance { get; }

	public SkyTag( CacheFile cache, TagEntry tag )
	{
		var data = cache.TagData;
		var a = tag.Address;

		Model = cache.GetTagReference( data, a );
		OutdoorAmbientColor = data.Vec3( a + 0x48 );
		OutdoorAmbientPower = data.F32( a + 0x54 );

		var lights = new List<SkyLight>();
		foreach ( var light in data.Block( a + 0xC4, 0x74 ) )
			lights.Add( new SkyLight( data.Vec3( light + 0x50 ), data.F32( light + 0x5C ), data.Vec2( light + 0x68 ) ) );
		Lights = lights;
		OutdoorFogColor = data.Vec3( a + 0x58 );
		var density = data.F32( a + 0x6C );
		OutdoorFogMaximumDensity = density == 0 ? 1 : density;
		OutdoorFogStartDistance = data.F32( a + 0x70 );
		OutdoorFogOpaqueDistance = data.F32( a + 0x74 );
		IndoorFogColor = data.Vec3( a + 0x78 );
		density = data.F32( a + 0x8C );
		IndoorFogMaximumDensity = density == 0 ? 1 : density;
		IndoorFogStartDistance = data.F32( a + 0x90 );
		IndoorFogOpaqueDistance = data.F32( a + 0x94 );
	}
}
