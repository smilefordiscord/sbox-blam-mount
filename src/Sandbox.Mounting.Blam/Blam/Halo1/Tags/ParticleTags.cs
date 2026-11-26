using System.Collections.Generic;
using System.Linq;

namespace Blam.Halo1.Tags;

// Inside the namespace so these win over s&box's global Vector2/Vector3 when this compiles into the mount.
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;
using Vector4 = System.Numerics.Vector4;

/// <summary>
/// Reader for <c>part</c> (particle) tags: a sprite that lives for a while and moves under its
/// point physics.
/// </summary>
/// <remarks>
/// Offsets: flags 0x0 (0x4 animation starts on a random frame, 0x8 animate once per frame, 0x10 dies
/// at rest, 0x20 dies on contact with structure, 0x40 tint from diffuse texture, 0x200
/// self-illuminated, 0x400 / 0x800 random horizontal / vertical mirroring), bitmap 0x4, physics
/// 0x14, lifespan 0x38, fade in/out time 0x40/0x44, collision effect 0x48, death effect 0x58,
/// minimum size 0x68 (pixels), radius animation 0x74 (multipliers at birth and death), animation
/// rate 0x80 (frames per second), contact deterioration 0x88, LOD falloff/cutoff 0x8C/0x90 (pixels),
/// first sequence index 0x98, initial /
/// looping / final sequence counts 0x9A/0x9C/0x9E, one over the sprite width 0xA8 (runtime, set when
/// the map was built), orientation 0xAC, shader flags 0xD8 (0x1 sort bias, 0x2 nonlinear tint),
/// framebuffer blend function 0xDA, fade mode 0xDC, secondary bitmap 0xFC, anchor 0x10C (0 with primary, 1 screen
/// space, 2 zsprite), zsprite radius scale 0x14C.
///
/// The bitmap's sequences are the particle's animation: it starts on a random one of the initial
/// sequences and plays its sprites, then plays random looping sequences until its lifespan is up,
/// then a final one. With no looping or final sequences it dies when its initial sequence ends,
/// whatever its lifespan says (though it fades by its lifespan): Halo's muzzle flashes are one-sprite
/// sequences flagged to animate once per frame, so they last a single frame. The quad is the sprite's pixel size x 2 x radius x the one over width.
/// </remarks>
public sealed class ParticleTag
{
	public uint Flags { get; }
	public TagEntry Bitmap { get; }
	public TagEntry Physics { get; }

	/// <summary>
	/// An effect or sound played where the particle hits the level, facing along its velocity, at
	/// the scale <see cref="CollisionScale"/> of its speed.
	/// </summary>
	public TagEntry CollisionEffect { get; }

	/// <summary>
	/// An effect or sound played where the particle dies: at the end of its life, on contact or at
	/// rest, though not when it dies on contact and has a <see cref="CollisionEffect"/>.
	/// </summary>
	public TagEntry DeathEffect { get; }

	/// <summary>
	/// The scale of a collision effect for a particle hitting the level at <paramref name="speed"/>
	/// world units per second: 0 up to half a unit per second, 1 from one and a half.
	/// </summary>
	public static float CollisionScale( float speed ) => System.Math.Clamp( (speed - 0.5f) / (1.5f - 0.5f), 0, 1 );
	public Vector2 Lifespan { get; }
	public float FadeInTime { get; }
	public float FadeOutTime { get; }
	public Vector2 RadiusAnimation { get; }
	public Vector2 AnimationRate { get; }
	public int FirstSequence { get; }
	public int InitialSequenceCount { get; }
	public int LoopingSequenceCount { get; }
	public int FinalSequenceCount { get; }

	/// <summary>
	/// One over the sprite width in pixels that the radius is measured against (about the widest
	/// sprite of the bitmap); 0 if unset.
	/// </summary>
	public float OneOverSpriteWidth { get; }
	public ParticleRenderMode Orientation { get; }
	public FramebufferBlendFunction BlendFunction { get; }

	/// <summary>
	/// Shader flags: 0x1 sort bias, 0x2 nonlinear tint.
	/// </summary>
	public ushort ShaderFlags { get; }

	/// <summary>
	/// How the secondary bitmap is laid over the sprite: 0 with the primary, 1 in screen space, 2
	/// as a zsprite (a depth map, so the sprite doesn't meet the level in a hard edge).
	/// </summary>
	public int Anchor { get; }

	public bool ZSprite => Anchor == 2;

	/// <summary>
	/// The secondary bitmap: for a zsprite, its depth map (see <see cref="ZSprite"/>).
	/// </summary>
	public TagEntry SecondaryBitmap { get; }

	/// <summary>
	/// A zsprite's depth map reaches this many times the particle's radius toward the viewer; 0 means 1.
	/// </summary>
	public float ZSpriteRadiusScale { get; }

	/// <summary>
	/// Shader flag 0x2: per channel the drawn color is (1 - tint) x texture^4 + tint x texture, so
	/// where the texture is bright it stays white and only its darker fringe takes the tint. The
	/// tint's alpha then counts whatever the blend function.
	/// </summary>
	public bool NonlinearTint => (ShaderFlags & 0x2) != 0;

	/// <summary>
	/// Drawn at least this many pixels high on screen; 0 for no minimum.
	/// </summary>
	public float MinimumSize { get; }

	// Seconds added to the time each sprite shows every time the particle hits something (debris that slows its tumbling as it bounces).
	public float ContactDeterioration { get; }

	// A particle whose diameter on screen is this many pixels or fewer isn't drawn.
	public float LodCutoff => FadeSize.Y;

	// The shader's framebuffer fade mode: 0 none, 1 fade when perpendicular (to the view), 2 fade when parallel. Only sprites that aren't screen facing fade.
	public int FadeMode { get; }

	/// <summary>
	/// The smallest a particle is drawn, in pixels on screen: whole from the first, fading out to
	/// nothing at the second (mostly 5 and 4).
	/// </summary>
	public Vector2 FadeSize { get; }

	/// <summary>
	/// Flags 0x400 / 0x800: each particle is mirrored horizontally / vertically at random.
	/// </summary>
	public bool RandomHorizontalMirroring => (Flags & 0x400) != 0;
	public bool RandomVerticalMirroring => (Flags & 0x800) != 0;

	// Flag 0x1: each particle plays its sequences backwards at random (half of them).
	public bool CanAnimateBackwards => (Flags & 0x1) != 0;

	// Flag 0x2: a particle at rest stops animating.
	public bool AnimationStopsAtRest => (Flags & 0x2) != 0;

	// Flags 0x80 / 0x100: removed when it's in water / in air.
	public bool DiesInWater => (Flags & 0x80) != 0;
	public bool DiesInAir => (Flags & 0x100) != 0;

	public bool RandomStartFrame => (Flags & 0x4) != 0;
	public bool AnimateOncePerFrame => (Flags & 0x8) != 0;

	/// <summary>
	/// Flag 0x20: removed when it hits the level.
	/// </summary>
	public bool DiesOnContact => (Flags & 0x20) != 0;

	/// <summary>
	/// Flag 0x10: removed once it comes to rest, bouncing until then: slower than
	/// <see cref="RestSpeed"/> while lying on ground flatter than <see cref="RestSlope"/>.
	/// </summary>
	public bool DiesAtRest => (Flags & 0x10) != 0;

	/// <summary>
	/// World units per second below which a particle on the ground is at rest.
	/// </summary>
	public const float RestSpeed = 0.25f;

	/// <summary>
	/// Ground whose normal's z is above this is level enough to rest on.
	/// </summary>
	public const float RestSlope = 0.8f;

	/// <summary>
	/// Flag 0x40: Halo tints each particle by the color of the ground under it when it's born (see
	/// <see cref="ParticleLighting"/>).
	/// </summary>
	public bool TintFromDiffuseTexture => (Flags & 0x40) != 0;

	/// <summary>
	/// Flag 0x200: drawn at its own brightness. Particles without it are lit by the lightmap under
	/// them when they're born (see <see cref="ParticleLighting"/>).
	/// </summary>
	public bool SelfIlluminated => (Flags & 0x200) != 0;

	/// <summary>
	/// Sprites per second its sequences play at: Halo's frame rate (30) for "once per frame".
	/// </summary>
	public float FrameRate => AnimateOncePerFrame ? 30 : (AnimationRate.X + AnimationRate.Y) / 2;

	/// <summary>
	/// The sequences a particle starts on (one picked at random). At least the first.
	/// </summary>
	public IEnumerable<int> InitialSequences
	{
		get
		{
			var first = System.Math.Max( FirstSequence, 0 );
			for ( var i = 0; i < System.Math.Max( InitialSequenceCount, 1 ); i++ )
				yield return first + i;
		}
	}

	/// <summary>
	/// The initial sequences in a bitmap with <paramref name="bitmapSequences"/> sequences: Halo
	/// clamps a sequence past the bitmap's last to the last (the plasma grenade's and plasma
	/// impacts' big blue flare asks for sequence 3 of a one sequence bitmap, and draws sequence 0).
	/// Empty for a bitmap without sequences, which draws nothing.
	/// </summary>
	public List<int> InitialSequencesIn( int bitmapSequences ) =>
		bitmapSequences <= 0 ? [] : InitialSequences.Select( s => System.Math.Min( s, bitmapSequences - 1 ) ).Distinct().ToList();

	/// <summary>
	/// How long a particle that starts on a sequence of <paramref name="sprites"/> sprites lives:
	/// its lifespan, or less when the sequence runs out first (no looping or final sequences).
	/// Its fades still follow its lifespan (see <see cref="FadeAt"/>).
	/// </summary>
	public Vector2 LifetimeFor( int sprites )
	{
		var lifespan = new Vector2( System.MathF.Max( Lifespan.X, 0 ), System.MathF.Max( Lifespan.Y, Lifespan.X ) );
		if ( LoopingSequenceCount > 0 || FinalSequenceCount > 0 || FrameRate <= 0 || sprites <= 0 )
			return lifespan;

		// A random start frame leaves half the sequence on average.
		var frames = RandomStartFrame ? (sprites + 1) / 2f : sprites;
		var sequence = frames / FrameRate;
		return new Vector2( System.MathF.Min( lifespan.X, sequence ), System.MathF.Min( lifespan.Y, sequence ) );
	}

	/// <summary>
	/// A particle's fade <paramref name="age"/> seconds into a lifespan of <paramref name="lifespan"/>
	/// seconds (its random pick from <see cref="Lifespan"/>): rising over the fade in time, falling
	/// over the fade out time before the lifespan's end, either of which can be longer than the
	/// lifespan (a puff that is already half faded at birth). Halo fades by the lifespan even when
	/// the particle dies sooner, at the end of its sequence: the plasma grenade's sparkle (a 0.5 to 1
	/// second lifespan, 3.5 seconds of fade out) is drawn at a fade of 0.14 to 0.29 for the quarter
	/// second its sequence lasts.
	/// </summary>
	public float FadeAt( float age, float lifespan )
	{
		var fade = 1f;
		if ( FadeInTime > 0 )
			fade *= System.Math.Clamp( age / FadeInTime, 0, 1 );
		if ( FadeOutTime > 0 )
			fade *= System.Math.Clamp( (lifespan - age) / FadeOutTime, 0, 1 );
		return fade;
	}

	public ParticleTag( CacheFile cache, TagEntry tag )
	{
		var data = cache.TagData;
		var a = tag.Address;
		Flags = data.U32( a );
		Bitmap = cache.GetTagReference( data, a + 0x4 );
		Physics = cache.GetTagReference( data, a + 0x14 );
		Lifespan = data.Vec2( a + 0x38 );
		FadeInTime = data.F32( a + 0x40 );
		FadeOutTime = data.F32( a + 0x44 );
		CollisionEffect = cache.GetTagReference( data, a + 0x48 );
		DeathEffect = cache.GetTagReference( data, a + 0x58 );
		MinimumSize = data.F32( a + 0x68 );
		ContactDeterioration = data.F32( a + 0x88 );
		FadeMode = data.U16( a + 0xDC );
		RadiusAnimation = data.Vec2( a + 0x74 );
		FadeSize = data.Vec2( a + 0x8C );
		AnimationRate = data.Vec2( a + 0x80 );
		FirstSequence = data.I16( a + 0x98 );
		InitialSequenceCount = data.I16( a + 0x9A );
		LoopingSequenceCount = data.I16( a + 0x9C );
		FinalSequenceCount = data.I16( a + 0x9E );
		OneOverSpriteWidth = data.F32( a + 0xA8 );
		Orientation = (ParticleRenderMode)data.U16( a + 0xAC );
		ShaderFlags = data.U16( a + 0xD8 );
		Anchor = data.U16( a + 0x10C );
		SecondaryBitmap = cache.GetTagReference( data, a + 0xFC );
		ZSpriteRadiusScale = data.F32( a + 0x14C );
		BlendFunction = (FramebufferBlendFunction)data.U16( a + 0xDA );
	}
}

/// <summary>
/// A state of a particle system type, e.g. a steam vent's "on" and "off", or an explosion's
/// "birth" and "death". The type plays its states one after another, each blending into the next
/// over its transition time; its multipliers apply to every particle of the type.
/// </summary>
/// <param name="Duration">Time in this state, range (seconds).</param>
/// <param name="Transition">Time blending into the next state, range (seconds).</param>
/// <param name="ScaleMultiplier">Multiplies the particles' size.</param>
/// <param name="AnimationRateMultiplier">Multiplies the particles' animation rate.</param>
/// <param name="RotationRateMultiplier">Multiplies the particles' rotation rate.</param>
/// <param name="ColorMultiplier">Multiplies the particles' color, RGBA.</param>
/// <param name="RadiusMultiplier">Multiplies the radius air drag acts on (see <see cref="ParticleSystemType.Radius"/>).</param>
/// <param name="CreationRate">Particles created per second.</param>
/// <param name="MinimumCount">While fewer particles than this are alive the state runs out faster.</param>
/// <param name="CreationPhysics">0 default, 1 explosion (constants: spread x, y and z in world units,
/// intensity per second), 2 jet (constants: velocity k, spread fraction, rotates up). A jet's
/// particle leaves at k / 30 world units per second: (1 - spread) of that along the forward and the
/// rest in a random direction, plus the velocity of whatever carries the system.</param>
public sealed record ParticleSystemState(
	string Name,
	Vector2 Duration,
	Vector2 Transition,
	float ScaleMultiplier,
	float AnimationRateMultiplier,
	float RotationRateMultiplier,
	Vector4 ColorMultiplier,
	float RadiusMultiplier,
	float CreationRate,
	int CreationPhysics,
	IReadOnlyList<float> Constants,
	float MinimumCount = 0 );

/// <summary>
/// A stage of a particle's life in a particle system: it looks a certain way, then blends into
/// the next stage over the transition time.
/// </summary>
/// <param name="Scale">Size range, world units per pixel of the sprite.</param>
/// <param name="AnimationRate">Sprites per second, range.</param>
/// <param name="RotationRate">Spin, radians per second, range.</param>
/// <param name="Color1">RGBA.</param>
/// <param name="Color2">RGBA.</param>
/// <param name="RadiusMultiplier">Multiplies the radius air drag acts on (see <see cref="ParticleSystemType.Radius"/>).</param>
/// <param name="Physics">The point physics moving it in this state; Halo blends it into the next
/// state's over the transition.</param>
/// <param name="ShaderFlags">0x1 sort bias, 0x2 nonlinear tint (as <see cref="ParticleTag.ShaderFlags"/>).</param>
/// <param name="SecondaryBitmap">A zsprite's depth map (see <see cref="ParticleTag.ZSprite"/>).</param>
/// <param name="Anchor">How the secondary bitmap is laid over the sprite: 2 as a zsprite.</param>
/// <param name="ZSpriteRadiusScale">How far a zsprite's depth map reaches toward the viewer, in particle radii; 0 means 1.</param>
/// <param name="FadeMode">The shader's framebuffer fade mode (see <see cref="ParticleTag.FadeMode"/>).</param>
/// <param name="PrimaryMapFlags">The shader's primary map flags: two states alike in these, blend function and sequence don't cross fade.</param>
public sealed record ParticleState(
	string Name,
	Vector2 Duration,
	Vector2 Transition,
	TagEntry Bitmap,
	int Sequence,
	Vector2 Scale,
	Vector2 AnimationRate,
	Vector2 RotationRate,
	Vector4 Color1,
	Vector4 Color2,
	float RadiusMultiplier,
	TagEntry Physics,
	FramebufferBlendFunction BlendFunction,
	ushort ShaderFlags,
	TagEntry SecondaryBitmap = null,
	int Anchor = 0,
	float ZSpriteRadiusScale = 0,
	int FadeMode = 0,
	ushort PrimaryMapFlags = 0 )
{
	public bool ZSprite => Anchor == 2;

	/// <summary>
	/// Shader flag 0x2: the tint only colors the texture's darker parts (see <see cref="ParticleTag.NonlinearTint"/>).
	/// </summary>
	public bool NonlinearTint => (ShaderFlags & 0x2) != 0;

	/// <summary>
	/// Whether the colors' alpha counts: Halo only uses it for alpha blended states, or with a
	/// nonlinear tint. Other states' opacity is only their cross fade.
	/// </summary>
	public bool UsesColorAlpha => BlendFunction == FramebufferBlendFunction.AlphaBlend || NonlinearTint;
}

/// <summary>
/// One kind of particle in a particle system, with its states and particle states.
/// </summary>
/// <param name="Flags">0x1 states loop, 0x4 particle states loop, 0x40 particles die on ground, 0x100
/// disabled, 0x10000/0x20000 not drawn in first/third person.</param>
/// <param name="InitialCount">Particles made when the system is.</param>
/// <param name="Rotational">Complex sprite render mode "rotational": the bitmap's sequence is seen face on, the next one edge on.</param>
/// <param name="Radius">The radius air drag acts on (world units), times the type state's and the
/// particle state's radius multipliers (see <see cref="PointPhysicsTag.DragPerSecond"/>).</param>
/// <param name="CreationPhysics">Placement of the particles made with the system: 0 default, 1 explosion,
/// 2 jet (see <see cref="ParticleSystemState.CreationPhysics"/>). The constants are the type's for all of them.</param>
/// <param name="SpriteRenderMode">How simple (not rotational) sprites face: 0 the screen, 1 along the
/// particle's axis, 2 across it (as <see cref="ParticleTag.Orientation"/>).</param>
public sealed record ParticleSystemType(
	string Name,
	uint Flags,
	int InitialCount,
	bool Rotational,
	float Radius,
	int CreationPhysics,
	IReadOnlyList<float> Constants,
	IReadOnlyList<ParticleSystemState> States,
	IReadOnlyList<ParticleState> ParticleStates,
	int SpriteRenderMode = 0 )
{
	public bool StatesLoop => (Flags & 0x1) != 0;
	public bool ParticleStatesLoop => (Flags & 0x4) != 0;

	/// <summary>
	/// Flag 0x40: a particle dies when it hits the level.
	/// </summary>
	public bool DieOnGround => (Flags & 0x40) != 0;

	/// <summary>
	/// Whether Halo draws the type in the given view: flag 0x10000 keeps it out of the player's own
	/// first person view, 0x20000 out of everyone else's.
	/// </summary>
	public bool DrawnIn( EffectView view ) => (Flags & (view == EffectView.FirstPerson ? 0x10000u : 0x20000u)) == 0;
}

/// <summary>
/// Reader for <c>pctl</c> (particle system) tags: emitters whose particles go through a series
/// of states, such as steam jets and smoke columns.
/// </summary>
/// <remarks>
/// System point physics 0x38. Particle types are a block at 0x5C (128 bytes: name, flags 0x20,
/// initial count 0x24, complex sprite render mode 0x28, sprite render mode 0x2A, radius 0x2C,
/// creation physics 0x54, constants 0x5C, states 0x68,
/// particle states 0x74). States are 192 bytes: name, duration 0x20, transition 0x28, scale /
/// animation rate / rotation rate multipliers 0x34/0x38/0x3C, color multiplier 0x40 (ARGB),
/// radius multiplier 0x50, minimum count 0x54, creation rate 0x58, creation physics 0xB0,
/// constants 0xB4. Particle
/// states are 376 bytes: name, duration 0x20, transition 0x28, bitmap 0x30, sequence 0x40, scale
/// 0x48, animation rate 0x50, rotation rate 0x58, colors 0x60/0x70 (ARGB), radius multiplier 0x80,
/// point physics 0x84, shader flags 0xE0, blend function 0xE2, fade mode 0xE4, primary map flags
/// 0xE6, secondary bitmap 0x104, anchor 0x114,
/// zsprite radius scale 0x154.
/// </remarks>
public sealed class ParticleSystemTag
{
	public IReadOnlyList<ParticleSystemType> Types { get; }

	// The point physics moving an unattached system itself (its new particles start where it is), or null for none.
	public TagEntry SystemPhysics { get; }

	public ParticleSystemTag( CacheFile cache, TagEntry tag )
	{
		var data = cache.TagData;
		List<float> Constants( uint block )
		{
			var list = new List<float>();
			foreach ( var k in data.Block( block, 4 ) )
				list.Add( data.F32( k ) );
			return list;
		}

		SystemPhysics = cache.GetTagReference( data, tag.Address + 0x38 );

		var types = new List<ParticleSystemType>();
		foreach ( var t in data.Block( tag.Address + 0x5C, 0x80 ) )
		{
			var states = new List<ParticleSystemState>();
			foreach ( var s in data.Block( t + 0x68, 0xC0 ) )
			{
				states.Add( new ParticleSystemState(
					Name: data.CString( s, 32 ),
					Duration: data.Vec2( s + 0x20 ),
					Transition: data.Vec2( s + 0x28 ),
					ScaleMultiplier: data.F32( s + 0x34 ),
					AnimationRateMultiplier: data.F32( s + 0x38 ),
					RotationRateMultiplier: data.F32( s + 0x3C ),
					ColorMultiplier: WeatherParticleSystemTag.ReadArgb( data, s + 0x40 ),
					RadiusMultiplier: data.F32( s + 0x50 ),
					CreationRate: data.F32( s + 0x58 ),
					CreationPhysics: data.U16( s + 0xB0 ),
					Constants: Constants( s + 0xB4 ),
					MinimumCount: data.F32( s + 0x54 ) ) );
			}

			var particleStates = new List<ParticleState>();
			foreach ( var s in data.Block( t + 0x74, 0x178 ) )
			{
				particleStates.Add( new ParticleState(
					Name: data.CString( s, 32 ),
					Duration: data.Vec2( s + 0x20 ),
					Transition: data.Vec2( s + 0x28 ),
					Bitmap: cache.GetTagReference( data, s + 0x30 ),
					Sequence: data.I16( s + 0x40 ),
					Scale: data.Vec2( s + 0x48 ),
					AnimationRate: data.Vec2( s + 0x50 ),
					RotationRate: data.Vec2( s + 0x58 ),
					Color1: WeatherParticleSystemTag.ReadArgb( data, s + 0x60 ),
					Color2: WeatherParticleSystemTag.ReadArgb( data, s + 0x70 ),
					RadiusMultiplier: data.F32( s + 0x80 ),
					Physics: cache.GetTagReference( data, s + 0x84 ),
					BlendFunction: (FramebufferBlendFunction)data.U16( s + 0xE2 ),
					ShaderFlags: data.U16( s + 0xE0 ),
					SecondaryBitmap: cache.GetTagReference( data, s + 0x104 ),
					Anchor: data.U16( s + 0x114 ),
					ZSpriteRadiusScale: data.F32( s + 0x154 ),
					FadeMode: data.U16( s + 0xE4 ),
					PrimaryMapFlags: data.U16( s + 0xE6 ) ) );
			}

			types.Add( new ParticleSystemType(
				Name: data.CString( t, 32 ),
				Flags: data.U32( t + 0x20 ),
				InitialCount: data.U16( t + 0x24 ),
				Rotational: data.U16( t + 0x28 ) == 1,
				Radius: data.F32( t + 0x2C ),
				CreationPhysics: data.U16( t + 0x54 ),
				Constants: Constants( t + 0x5C ),
				States: states,
				ParticleStates: particleStates,
				SpriteRenderMode: data.U16( t + 0x2A ) ) );
		}
		Types = types;
	}
}
