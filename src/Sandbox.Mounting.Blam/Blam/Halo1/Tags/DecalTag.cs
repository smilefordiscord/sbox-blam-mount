using System;

namespace Blam.Halo1.Tags;

// Inside the namespace so these win over s&box's global Vector2/Vector3 when this compiles into the mount.
using Vector3 = System.Numerics.Vector3;

/// <summary>
/// Reader for <c>deca</c> (decal) tags: painted signs, markings, scorch marks, blood.
/// </summary>
/// <remarks>
/// Offsets: flags 0x0 (bit 3 = no random rotation, bit 8 = preserve aspect), type 0x2, layer 0x4, next decal in chain 0x8, radius range 0x18 (world
/// units), intensity range 0x2C, color lower/upper bounds 0x34/0x40, animation loop frame 0x58 and speed 0x5A (int16),
/// lifetime range 0x78 and decay time range 0x80 (seconds), framebuffer blend function 0xC0 (same values as transparent
/// shaders), map 0xD8 (usually a sprite sheet), maximum sprite extent 0xFC (pixels).
/// </remarks>
public sealed class DecalTag
{
	public bool NoRandomRotation { get; }

	/// <summary>
	/// A whole bitmap keeps its aspect ratio instead of filling the decal's square; a sprite's
	/// height is scaled to its width the same way.
	/// </summary>
	public bool PreserveAspect { get; }
	public DecalType Type { get; }
	public DecalLayer Layer { get; }

	/// <summary>
	/// A decal to place along with this one (e.g. a burn mark under a scorch), or null.
	/// </summary>
	public TagEntry NextInChain { get; }

	public float RadiusMin { get; }
	public float RadiusMax { get; }
	public float IntensityMin { get; }
	public float IntensityMax { get; }

	/// <summary>
	/// Seconds a decal made at runtime stays at full strength, then fades for <see cref="DecayTimeMin"/>
	/// to <see cref="DecayTimeMax"/>. Zero means it stays until the game runs out of decals.
	/// </summary>
	public float LifetimeMin { get; }
	public float LifetimeMax { get; }
	public float DecayTimeMin { get; }
	public float DecayTimeMax { get; }

	public short AnimationLoopFrame { get; }
	public short AnimationSpeed { get; }

	/// <summary>
	/// The largest a sprite may be drawn, in pixels of its bitmap (0 for no limit).
	/// </summary>
	public float MaximumSpriteExtent { get; }
	public Vector3 ColorLower { get; }
	public Vector3 ColorUpper { get; }
	public FramebufferBlendFunction BlendFunction { get; }
	public TagEntry Map { get; }

	/// <summary>
	/// The color to tint the map with. Both bounds left at black means untinted.
	/// </summary>
	public Vector3 Color
	{
		get
		{
			var color = (ColorLower + ColorUpper) * 0.5f;
			return color == Vector3.Zero ? Vector3.One : color;
		}
	}

	public DecalTag( CacheFile cache, TagEntry tag )
	{
		var data = cache.TagData;
		var a = tag.Address;

		NoRandomRotation = (data.U16( a ) & (1 << 3)) != 0;
		PreserveAspect = (data.U16( a ) & (1 << 8)) != 0;
		Type = (DecalType)data.U16( a + 0x2 );
		Layer = (DecalLayer)data.U16( a + 0x4 );
		NextInChain = cache.GetTagReference( data, a + 0x8 );
		RadiusMin = data.F32( a + 0x18 );
		RadiusMax = MathF.Max( RadiusMin, data.F32( a + 0x1C ) );
		IntensityMin = data.F32( a + 0x2C );
		IntensityMax = MathF.Max( IntensityMin, data.F32( a + 0x30 ) );
		AnimationLoopFrame = data.I16( a + 0x58 );
		AnimationSpeed = data.I16( a + 0x5A );
		LifetimeMin = data.F32( a + 0x78 );
		LifetimeMax = MathF.Max( LifetimeMin, data.F32( a + 0x7C ) );
		DecayTimeMin = data.F32( a + 0x80 );
		DecayTimeMax = MathF.Max( DecayTimeMin, data.F32( a + 0x84 ) );
		MaximumSpriteExtent = data.F32( a + 0xFC );
		ColorLower = data.Vec3( a + 0x34 );
		ColorUpper = data.Vec3( a + 0x40 );
		BlendFunction = (FramebufferBlendFunction)data.U16( a + 0xC0 );
		Map = cache.GetTagReference( data, a + 0xD8 );
	}
}

public enum DecalType : ushort
{
	Scratch,
	Splatter,
	Burn,
	PaintedSign,
}

/// <summary>
/// How a decal is drawn over the surface: primary and secondary are the usual blended layers,
/// light is additive, alpha-tested is cut out, water lies on water.
/// </summary>
public enum DecalLayer : ushort
{
	Primary,
	Secondary,
	Light,
	AlphaTested,
	Water,
}
