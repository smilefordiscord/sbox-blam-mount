namespace Blam.Halo2.Tags;

using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;

/// <summary>
/// A Halo 2 patchy fog (<c>fpch</c>): how the noise of patchy fog layers looks and moves. Where it is, its colour and
/// density come from the block that references it (<see cref="PatchyFogBlock"/>, in a sky or a planar fog).
/// </summary>
/// <remarks>Engine behaviour: scratch research notes, patchy_fog.md.</remarks>
public sealed class PatchyFogTag
{
	public static readonly TagClass Class = new( "fpch" );

	public TagEntry Tag { get; }

	/// <summary>
	/// 1 separate layer depths, 2 sort behind transparents.
	/// </summary>
	public int Flags { get; }

	public float RotationMultiplier { get; }
	public float StrafingMultiplier { get; }
	public float ZoomMultiplier { get; }
	public float NoiseMapScale { get; }
	public TagEntry NoiseMap { get; }
	public float NoiseVerticalScaleForward { get; }
	public float NoiseVerticalScaleUp { get; }
	public float NoiseOpacityScaleUp { get; }

	/// <summary>
	/// Seconds per noise image (0: no animation).
	/// </summary>
	public float AnimationPeriod { get; }

	/// <summary>
	/// The wind's speed range (world units/s) and the range of times between its changes (s).
	/// </summary>
	public Vector2 WindVelocity { get; }
	public Vector2 WindPeriod { get; }
	public float WindAccelerationWeight { get; }
	public float WindPerpendicularWeight { get; }
	public Vector3 WindConstantVelocity { get; }

	public PatchyFogTag( TagEntry tag )
	{
		Tag = tag;
		var cache = tag.Cache;
		var data = cache.TagData;
		var t = tag.Address;
		Flags = data.U16( t );
		RotationMultiplier = data.F32( t + 0x4 );
		StrafingMultiplier = data.F32( t + 0x8 );
		ZoomMultiplier = data.F32( t + 0xC );
		NoiseMapScale = data.F32( t + 0x10 );
		NoiseMap = cache.GetTagReference( t + 0x14 );
		NoiseVerticalScaleForward = data.F32( t + 0x1C );
		NoiseVerticalScaleUp = data.F32( t + 0x20 );
		NoiseOpacityScaleUp = data.F32( t + 0x24 );
		AnimationPeriod = data.F32( t + 0x28 );
		WindVelocity = data.Vec2( t + 0x2C );
		WindPeriod = data.Vec2( t + 0x34 );
		WindAccelerationWeight = data.F32( t + 0x3C );
		WindPerpendicularWeight = data.F32( t + 0x40 );
		WindConstantVelocity = data.Vec3( t + 0x44 );
	}
}

/// <summary>
/// Patchy fog as a sky or a planar fog places it: colour (rgb 0-1, gamma space), density range (the noise picks
/// between them), the view depth range it comes in over (world units), how deep the camera must be in a planar fog
/// before it starts (fraction of the fog's opaque depth), and its look (<see cref="PatchyFogTag"/>).
/// </summary>
public sealed record PatchyFogBlock( Vector3 Color, Vector2 Density, Vector2 Distance, float MinimumDepthFraction, TagEntry PatchyFog )
{
	/// <summary>
	/// A sky's block (0x50 bytes: colour 0x0, density 0x18, distance 0x20, patchy fog 0x48).
	/// </summary>
	public static PatchyFogBlock FromSky( CacheFile cache, uint address ) => new(
		cache.TagData.Vec3( address ), cache.TagData.Vec2( address + 0x18 ), cache.TagData.Vec2( address + 0x20 ), 0,
		cache.GetTagReference( address + 0x48 ) );

	/// <summary>
	/// A planar fog's block (0x34 bytes: colour 0x0, density 0x18, distance 0x20, minimum depth fraction 0x28, patchy
	/// fog 0x2C).
	/// </summary>
	public static PatchyFogBlock FromPlanarFog( CacheFile cache, uint address ) => new(
		cache.TagData.Vec3( address ), cache.TagData.Vec2( address + 0x18 ), cache.TagData.Vec2( address + 0x20 ),
		cache.TagData.F32( address + 0x28 ), cache.GetTagReference( address + 0x2C ) );
}
