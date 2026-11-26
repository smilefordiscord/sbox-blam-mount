using System.Collections.Generic;

namespace Blam.Halo3.Tags;

using Vector3 = System.Numerics.Vector3;

// An area_screen_effect (sefc): a block (+0x00, 0x84 each) of screen effects, each weighted by its falloff functions and folded into the final composite's exposure and colour.
public sealed class ScreenEffectTag
{
	public static readonly TagClass Class = new( "sefc" );

	public TagEntry Tag { get; }
	public IReadOnlyList<ScreenEffect> Effects { get; }

	public ScreenEffectTag( TagEntry tag )
	{
		Tag = tag;
		var effects = new List<ScreenEffect>();
		foreach ( var e in tag.Data.Block( 0x00, 0x84 ) )
		{
			effects.Add( new ScreenEffect
			{
				Name = e.StringId( 0x00 ),
				Flags = e.U32( 0x04 ),
				MaximumDistance = e.F32( 0x08 ),
				DistanceFalloff = new Halo3Function( e.Data( 0x0C ).ToArray() ),
				Lifetime = e.F32( 0x20 ),
				TimeFalloff = new Halo3Function( e.Data( 0x24 ).ToArray() ),
				AngleFalloff = new Halo3Function( e.Data( 0x38 ).ToArray() ),
				ExposureBoost = e.F32( 0x4C ),
				HueLeft = e.F32( 0x50 ),
				HueRight = e.F32( 0x54 ),
				Saturation = e.F32( 0x58 ),
				Desaturation = e.F32( 0x5C ),
				ContrastEnhance = e.F32( 0x60 ),
				GammaEnhance = e.F32( 0x64 ),
				GammaReduce = e.F32( 0x68 ),
				ColorFilter = e.Vec3( 0x6C ),
				ColorFloor = e.Vec3( 0x78 ),
			} );
		}
		Effects = effects;
	}

	public static bool IsScreenEffect( TagEntry tag ) => tag is not null && tag.Address != 0 && tag.Class == Class;

	// The scenario's global screen effect (scnr +0x670): always on, its falloffs ignored.
	public static ScreenEffectTag ForScenario( CacheFile cache )
	{
		var scenario = cache?.Scenario?.Data ?? default;
		if ( scenario.IsNull )
			return null;
		var tag = scenario.Reference( 0x670 );
		return IsScreenEffect( tag ) ? new ScreenEffectTag( tag ) : null;
	}
}

// One screen effect: flags (1 debug disable, 2 allow outside the radius, 4 first person only, 8 third person only), its reach in world units (and falloff over it), lifetime in seconds (0 forever, and falloff over it), falloff by angle, and what it does at full weight.
public sealed class ScreenEffect
{
	public string Name { get; init; }
	public uint Flags { get; init; }
	public float MaximumDistance { get; init; }
	public Halo3Function DistanceFalloff { get; init; }
	public float Lifetime { get; init; }
	public Halo3Function TimeFalloff { get; init; }
	public Halo3Function AngleFalloff { get; init; }

	public float ExposureBoost { get; init; }
	public float HueLeft { get; init; }
	public float HueRight { get; init; }
	public float Saturation { get; init; }
	public float Desaturation { get; init; }
	public float ContrastEnhance { get; init; }
	public float GammaEnhance { get; init; }
	public float GammaReduce { get; init; }
	public Vector3 ColorFilter { get; init; }
	public Vector3 ColorFloor { get; init; }

	public bool Disabled => (Flags & 1) != 0;
	public bool AllowOutsideRadius => (Flags & 2) != 0;
	public bool FirstPersonOnly => (Flags & 4) != 0;
	public bool ThirdPersonOnly => (Flags & 8) != 0;
}
