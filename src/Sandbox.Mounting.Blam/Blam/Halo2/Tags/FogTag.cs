namespace Blam.Halo2.Tags;

using Vector3 = System.Numerics.Vector3;

/// <summary>
/// A Halo 2 planar fog (<c>fog </c>): the fog filling a volume below a structure BSP's fog plane, thickening with
/// depth below the plane (and with view depth near the camera).
/// </summary>
/// <remarks>
/// Lockout's mountain_fog:
/// the abyss below the level, full at 35 world units below its plane, in the atmospheric fog's blue.
/// </remarks>
public sealed class FogTag
{
	public static readonly TagClass Class = new( "fog " );

	public TagEntry Tag { get; }

	/// <summary>
	/// 1 render only submerged geometry, 2 extend infinitely while visible, 0x10 do not render, 0x20 do not render
	/// unless the camera is submerged.
	/// </summary>
	public int Flags { get; }

	public float MaximumDensity { get; }

	/// <summary>
	/// The view depth (world units) at which fog reaches its maximum for a camera inside it.
	/// </summary>
	public float OpaqueDistance { get; }

	/// <summary>
	/// The depth below the plane (world units) at which fog reaches its maximum.
	/// </summary>
	public float OpaqueDepth { get; }

	/// <summary>
	/// Rgb 0-1, gamma space.
	/// </summary>
	public Vector3 Color { get; }

	/// <summary>
	/// The patchy fog the camera sees once it's deep enough inside (null for none).
	/// </summary>
	public PatchyFogBlock PatchyFog { get; }

	public FogTag( TagEntry tag )
	{
		Tag = tag;
		var data = tag.Cache.TagData;
		Flags = data.U16( tag.Address );
		MaximumDensity = data.F32( tag.Address + 0xC );
		OpaqueDistance = data.F32( tag.Address + 0x10 );
		OpaqueDepth = data.F32( tag.Address + 0x14 );
		Color = data.Vec3( tag.Address + 0x24 );
		foreach ( var a in data.Elements( tag.Address + 0x30, 0x34 ) )
		{
			PatchyFog = PatchyFogBlock.FromPlanarFog( tag.Cache, a );
			break;
		}
	}
}
