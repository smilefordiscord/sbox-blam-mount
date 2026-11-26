using System.Collections.Generic;

namespace Blam.Halo1.Tags;

/// <summary>
/// Reader for Halo CE's <c>cont</c> (contrail) tags (see <see cref="ContrailDefinition"/>).
/// </summary>
/// <remarks>
/// Offsets: flags 0x0 (0x1 first point unfaded, 0x2 last point unfaded), point generation rate
/// 0x4 (points per second), point velocity 0x8 (range, world units per second), cone angle 0x10,
/// inherited velocity fraction 0x14, render type 0x18, texture repeats u/v 0x1C/0x20, texture
/// animation u/v 0x24/0x28 (repeats per second), animation rate 0x2C, bitmap 0x30, first sequence
/// 0x40, sequence count 0x42, shader flags 0xAC (0x2 nonlinear tint), framebuffer blend function 0xAE, point states 0x138 (104 bytes:
/// duration 0x0, transition 0x8, point physics 0x10, width 0x40, color bounds 0x44/0x54 ARGB).
/// </remarks>
public sealed class ContrailTag : ContrailDefinition
{
	public TagEntry Bitmap { get; }
	public FramebufferBlendFunction BlendFunction { get; }

	// Each point state's point physics.
	public IReadOnlyList<TagEntry> PointPhysics { get; }

	public ContrailTag( CacheFile cache, TagEntry tag )
	{
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
		Bitmap = cache.GetTagReference( data, a + 0x30 );
		FirstSequence = data.I16( a + 0x40 );
		SequenceCount = data.I16( a + 0x42 );
		BlendFunction = (FramebufferBlendFunction)data.U16( a + 0xAE );
		ShaderFlags = data.U16( a + 0xAC );

		var states = new List<ContrailPointState>();
		var physics = new List<TagEntry>();
		foreach ( var s in data.Block( a + 0x138, 0x68 ) )
		{
			states.Add( new ContrailPointState(
				Duration: data.Vec2( s ),
				Transition: data.Vec2( s + 0x8 ),
				Width: data.F32( s + 0x40 ),
				ColorLower: WeatherParticleSystemTag.ReadArgb( data, s + 0x44 ),
				ColorUpper: WeatherParticleSystemTag.ReadArgb( data, s + 0x54 ) ) );
			physics.Add( cache.GetTagReference( data, s + 0x10 ) );
		}
		PointStates = states;
		PointPhysics = physics;
	}
}
