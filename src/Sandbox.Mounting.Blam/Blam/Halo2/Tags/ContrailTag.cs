using System.Linq;

namespace Blam.Halo2.Tags;

using Vector4 = System.Numerics.Vector4;

// Halo 2's contrail (cont): Halo CE's contrail fields at the same offsets (ContrailDefinition), then a shorter shader part.
// Layout, checked on every multiplayer map (0xF0 bytes, point states after it).
// Bitmap 0x30, first sequence 0x38, sequence count 0x3A.
// Shader flags 0x64, framebuffer blend function 0x66, fade mode 0x68, point states 0xE8 (0x40 each).
// A point state: duration 0x0, transition 0x8, point physics 0x10, width 0x18, color bounds 0x1C and 0x2C as ARGB floats.
public sealed class ContrailTag : ContrailDefinition
{
	public static readonly TagClass Class = new( "cont" );

	public TagEntry Tag { get; }
	public TagEntry Bitmap { get; }

	// Halo CE's eight (alpha blend, multiply, double multiply, add, subtract, min, max, alpha-multiply add).
	// Then 8 constant color blend, 9 inverse constant color blend and 10 none.
	public int BlendFunction { get; }

	public ContrailTag( TagEntry tag )
	{
		Tag = tag;
		var cache = tag.Cache;
		var data = cache.TagData;
		var a = tag.Address;
		Flags = data.U16( a );
		PointRate = data.F32( a + 0x4 );
		PointVelocity = data.Vec2( a + 0x8 );
		ConeAngle = data.F32( a + 0x10 );
		InheritedVelocity = data.F32( a + 0x14 );
		RenderType = (ContrailRenderType)data.U16( a + 0x18 );
		TextureRepeats = data.Vec2( a + 0x1C );
		TextureAnimation = data.Vec2( a + 0x24 );
		AnimationRate = data.F32( a + 0x2C );
		Bitmap = cache.GetTagReference( a + 0x30 );
		FirstSequence = data.I16( a + 0x38 );
		SequenceCount = data.I16( a + 0x3A );
		ShaderFlags = data.U16( a + 0x64 );
		BlendFunction = data.U16( a + 0x66 );

		Vector4 Argb( uint at ) => new( data.F32( at + 4 ), data.F32( at + 8 ), data.F32( at + 12 ), data.F32( at ) );
		PointStates = data.Elements( a + 0xE8, 0x40 )
			.Select( s => new ContrailPointState( data.Vec2( s ), data.Vec2( s + 0x8 ), data.F32( s + 0x18 ), Argb( s + 0x1C ), Argb( s + 0x2C ) ) )
			.ToList();
	}

	// One of Halo CE's eight blend functions, which halo1_contrail.shader draws.
	public bool HasClassicBlend => BlendFunction is >= 0 and <= 7;

	// Adds light rather than covering what's behind: add, component max, alpha-multiply add.
	public bool IsAdditive => BlendFunction is 3 or 6 or 7;

	public static bool IsContrail( TagEntry tag ) => tag is not null && tag.Address != 0 && tag.Class == Class;
}
