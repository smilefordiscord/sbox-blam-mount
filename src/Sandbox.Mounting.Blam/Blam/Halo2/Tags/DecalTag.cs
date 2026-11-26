namespace Blam.Halo2.Tags;

using Vector3 = System.Numerics.Vector3;

/// <summary>
/// How a Halo 2 decal wraps onto the surfaces it's placed on.
/// </summary>
public enum DecalType : ushort
{
	Scratch = 0,
	Splatter = 1,
	Burn = 2,
	PaintedSign = 3,
}

/// <summary>
/// Which of Halo 2's decal layers a decal draws in, each with its own blend.
/// </summary>
public enum DecalLayer : ushort
{
	/// <summary>
	/// Alpha blended and lit by the lightmap, drawn before dynamic lights (which fade it).
	/// </summary>
	LitAlphaBlendPrelight = 0,

	/// <summary>
	/// Alpha blended and lit by the lightmap, drawn after dynamic lights.
	/// </summary>
	LitAlphaBlend = 1,

	/// <summary>
	/// Twice the bitmap times the frame: grey leaves it alone.
	/// </summary>
	DoubleMultiply = 2,

	/// <summary>
	/// The bitmap times the frame: white leaves it alone.
	/// </summary>
	Multiply = 3,

	/// <summary>
	/// The brighter of the bitmap and the frame: black leaves it alone.
	/// </summary>
	Max = 4,

	/// <summary>
	/// The bitmap added to the frame: black leaves it alone.
	/// </summary>
	Add = 5,

	/// <summary>
	/// Bright red, through everything.
	/// </summary>
	Error = 6,
}

/// <summary>
/// A Halo 2 decal (<c>deca</c>): a bitmap laid onto surfaces, placed in a scenario or left by effects.
/// </summary>
/// <remarks>
/// Layout as Halo 2 Xbox's (checked on MCC's Zanzibar, Warlock and Derelict decals): flags 0x0, type 0x2, layer 0x4,
/// maximum overlapping count 0x6, next decal in chain 0x8, radius range 0x10, overlapping rejection radius 0x18,
/// colour bounds 0x1C and 0x28, lifetime 0x34, decay time 0x3C, bitmap 0x88, maximum sprite extent 0xA4.
/// </remarks>
public sealed class DecalTag
{
	public static readonly TagClass Class = new( "deca" );

	const ushort NoRandomRotationFlag = 1 << 3;
	const ushort PreserveAspectFlag = 1 << 8;

	public TagEntry Tag { get; }
	public ushort Flags { get; }
	public DecalType Type { get; }
	public DecalLayer Layer { get; }

	/// <summary>
	/// The decal chain's next decal, placed with this one (null at the end).
	/// </summary>
	public TagEntry Next { get; }

	/// <summary>
	/// How many recent decals of this tag may lie close together before another is refused (0: no limit).
	/// </summary>
	public int MaximumOverlapping { get; }

	/// <summary>
	/// Radius range (world units); 0 means 0.125.
	/// </summary>
	public float RadiusMin { get; }
	public float RadiusMax { get; }

	/// <summary>
	/// The fraction of the largest radius within which recent decals count as overlapping (0 means 1).
	/// </summary>
	public float OverlapRejection { get; }

	/// <summary>
	/// Life and fade-out ranges (seconds) of a decal an effect leaves; a life of 0 lasts until it's pushed out.
	/// </summary>
	public float LifetimeMin { get; }
	public float LifetimeMax { get; }
	public float DecayMin { get; }
	public float DecayMax { get; }

	/// <summary>
	/// Colour range, rgb 0-1.
	/// </summary>
	public Vector3 ColorLower { get; }
	public Vector3 ColorUpper { get; }

	public TagEntry Bitmap { get; }

	/// <summary>
	/// How many of a sprite's pixels span one radius.
	/// </summary>
	public float MaximumSpriteExtent { get; }

	/// <summary>
	/// Whether the decal keeps its placed rotation, rather than being spun at random (signs, arrows, markings).
	/// </summary>
	public bool NoRandomRotation => (Flags & NoRandomRotationFlag) != 0;

	/// <summary>
	/// Whether the decal is as wide and tall as its bitmap (or sprite) is, rather than square.
	/// </summary>
	public bool PreserveAspect => (Flags & PreserveAspectFlag) != 0;

	public DecalTag( TagEntry tag )
	{
		Tag = tag;
		var data = tag.Cache.TagData;
		var a = tag.Address;
		Flags = data.U16( a );
		Type = (DecalType)data.U16( a + 0x2 );
		Layer = (DecalLayer)data.U16( a + 0x4 );
		MaximumOverlapping = data.I16( a + 0x6 );
		Next = tag.Cache.GetTagReference( a + 0x8 );
		RadiusMin = data.F32( a + 0x10 );
		RadiusMax = data.F32( a + 0x14 );
		OverlapRejection = data.F32( a + 0x18 );
		LifetimeMin = data.F32( a + 0x34 );
		LifetimeMax = data.F32( a + 0x38 );
		DecayMin = data.F32( a + 0x3C );
		DecayMax = data.F32( a + 0x40 );
		ColorLower = data.Vec3( a + 0x1C );
		ColorUpper = data.Vec3( a + 0x28 );
		Bitmap = tag.Cache.GetTagReference( a + 0x88 );
		MaximumSpriteExtent = data.F32( a + 0xA4 );
	}
}
