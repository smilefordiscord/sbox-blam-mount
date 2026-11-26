using System;
using System.Collections.Generic;
using System.Linq;

namespace Blam.Halo2.Tags;

using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;
using Vector4 = System.Numerics.Vector4;

/// <summary>
/// What a particle property (<see cref="ParticleProperty"/>) is a function of.
/// </summary>
public enum ParticleInput : short
{
	/// <summary>The particle's age as a fraction of its lifespan (0-1).</summary>
	ParticleAge,
	ParticleEmitTime,
	/// <summary>Random per particle, fixed for its life (0-1).</summary>
	ParticleRandom1,
	ParticleRandom2,
	/// <summary>The emitter's age.</summary>
	EmitterAge,
	/// <summary>Random per emitter, fixed for its life (0-1).</summary>
	EmitterRandom1,
	EmitterRandom2,
	/// <summary>1 near the camera, falling toward the system's LOD distance.</summary>
	SystemLod,
	GameTime,
	EffectAScale,
	EffectBScale,
	ParticleRotation,
	ExplosionAnimation,
	ExplosionRotation,
	ParticleRandom3,
	ParticleRandom4,
	LocationRandom,
}

/// <summary>
/// A particle property: a mapping function of an input (and a range input blending its two curves), mapped onto the
/// function's output range, then optionally plus or times another input (0 none, 1 plus, 2 times). The same 0x10
/// byte struct in effects (emitters), particles (prt3) and particle movement (pmov): u16 input, u16 range input,
/// u16 output modifier, u16 modifier input, then the function blob as a data reference (size, address).
/// </summary>
public sealed record ParticleProperty( ParticleInput Input, ParticleInput Range, int Modifier, ParticleInput ModifierInput, MappingFunction Function )
{
	public const int Size = 0x10;

	public static ParticleProperty Read( CacheFile cache, uint address )
	{
		var data = cache.TagData;
		var size = data.I32( address + 8 );
		var pointer = data.U32( address + 12 );
		var blob = size > 0 && data.Contains( pointer, size ) ? data.Slice( pointer, size ).ToArray() : [];
		return new ParticleProperty( (ParticleInput)data.I16( address ), (ParticleInput)data.I16( address + 2 ), data.I16( address + 4 ),
			(ParticleInput)data.I16( address + 6 ), new MappingFunction( blob ) );
	}

	/// <summary>
	/// No function: the property isn't set.
	/// </summary>
	public bool IsEmpty => Function.IsEmpty;

	/// <summary>
	/// The value for the given inputs (a function of the input variable, 0-1). An empty property is
	/// <paramref name="empty"/>.
	/// </summary>
	public float Evaluate( Func<ParticleInput, float> inputs, float empty = 0 )
	{
		if ( Function.IsEmpty )
			return empty;

		var value = Function.Evaluate( inputs( Input ), inputs( Range ) );
		return Modifier switch
		{
			1 => value + inputs( ModifierInput ),
			2 => value * inputs( ModifierInput ),
			_ => value,
		};
	}

	/// <summary>
	/// The colour (r, g, b 0-1 in gamma space, a 0-1) of a colour function for the given inputs; white without one.
	/// </summary>
	public Vector4 EvaluateColor( Func<ParticleInput, float> inputs )
	{
		if ( Function.IsEmpty )
			return Vector4.One;

		var argb = Function.ColorAt( Function.Curve( inputs( Input ), inputs( Range ) ) );
		return MappingFunction.ToVector( argb );
	}

	/// <summary>
	/// Whether the value depends on <paramref name="input"/>.
	/// </summary>
	public bool Uses( ParticleInput input ) => !Function.IsEmpty &&
		((Input == input && Function.Type != MappingFunction.Constant) || (Range == input && Function.IsRange) || (Modifier != 0 && ModifierInput == input));

	public override string ToString() => Function.IsEmpty ? "-" : $"{Function.TypeName}{(Function.IsRange ? " range" : "")}({Input}{(Function.IsRange ? $", {Range}" : "")}) [{Function.Minimum:0.###}..{Function.Maximum:0.###}]{(Modifier == 1 ? $" + {ModifierInput}" : Modifier == 2 ? $" x {ModifierInput}" : "")}";
}

/// <summary>
/// How a particle system's emitter scatters new particles: 0 sprayer, 1 disc, 2 globe, 3 implode, 4 tube, 5 halo,
/// 6 impact contour, 7 impact area, 8 debris, 9 line.
/// </summary>
public enum EmissionShape
{
	Sprayer, Disc, Globe, Implode, Tube, Halo, ImpactContour, ImpactArea, Debris, Line,
}

/// <summary>
/// An emitter of a particle system (0xB8): its particle physics (<c>pmov</c>), the properties of the particles it
/// makes (emission rate, lifespan in seconds, velocity in world units per second, angular velocity in degrees per
/// second, size in world units, tint, alpha), the emission shape with its radius (world units) and angle (degrees),
/// an offset and a direction (yaw, pitch, radians) relative to its location.
/// </summary>
public sealed record EffectEmitter( TagEntry Physics, ParticleProperty EmissionRate, ParticleProperty Lifespan, ParticleProperty Velocity,
	ParticleProperty AngularVelocity, ParticleProperty Size, ParticleProperty Tint, ParticleProperty Alpha, EmissionShape Shape,
	ParticleProperty EmissionRadius, ParticleProperty EmissionAngle, Vector3 Offset, Vector2 Direction )
{
	public const int ElementSize = 0xB8;

	public static EffectEmitter Read( CacheFile cache, uint e )
	{
		var data = cache.TagData;
		ParticleProperty P( uint offset ) => ParticleProperty.Read( cache, e + offset );
		return new EffectEmitter( cache.GetTagReference( e ), P( 0x8 ), P( 0x18 ), P( 0x28 ), P( 0x38 ), P( 0x48 ), P( 0x58 ), P( 0x68 ),
			(EmissionShape)data.I32( e + 0x78 ), P( 0x7C ), P( 0x8C ), data.Vec3( e + 0x9C ), data.Vec2( e + 0xA8 ) );
	}
}

/// <summary>
/// A particle system (0x38, inline in effect events and particles): the particle (<c>prt3</c>), its location, the
/// coordinate system its particles move in (0 world, 1 local, 2 parent), where and when it's created (environment,
/// violence, camera mode 0 independent, 1 first person only, 2 third person only, 3 both), sort bias, flags (1 glow,
/// 2 cinematics, 4 looping particle, 0x10 inherit effect velocity, 0x20 don't render, 0x80 spread between ticks,
/// 0x100 persistent particle), its LOD fade distances (world units) and emitters.
/// </summary>
public sealed record EffectParticleSystem( TagEntry Particle, int Location, int CoordinateSystem, int Environment, int Disposition, int CameraMode,
	int SortBias, int Flags, float LodIn, float LodFeatherIn, float LodOut, float LodFeatherOut, IReadOnlyList<EffectEmitter> Emitters )
{
	public const int Size = 0x38;

	public static EffectParticleSystem Read( CacheFile cache, uint s )
	{
		var data = cache.TagData;
		return new EffectParticleSystem( cache.GetTagReference( s ), data.I16( s + 0x8 ), data.I16( s + 0xC ), data.I16( s + 0xE ), data.I16( s + 0x10 ),
			data.I16( s + 0x12 ), data.I16( s + 0x14 ), data.U16( s + 0x16 ), data.F32( s + 0x18 ), data.F32( s + 0x1C ), data.F32( s + 0x24 ),
			data.F32( s + 0x28 ), data.Elements( s + 0x30, EffectEmitter.ElementSize ).Select( e => EffectEmitter.Read( cache, e ) ).ToList() );
	}

	/// <summary>
	/// The particles move with the location (local or parent coordinates) rather than in the world.
	/// </summary>
	public bool IsLocal => CoordinateSystem != 0;

	/// <summary>
	/// The LOD fade at a distance (world units): 0 nearer than the LOD in distance, rising over its feather, 1 until
	/// the LOD out distance, falling to 0 over its feather. A LOD out distance of 0 never fades out.
	/// </summary>
	public float LodAt( float distance )
	{
		var fadeIn = LodFeatherIn > 0 ? Math.Clamp( (distance - LodIn) / LodFeatherIn, 0, 1 ) : distance >= LodIn ? 1 : 0;
		var fadeOut = LodOut <= 0 ? 1 : LodFeatherOut > 0 ? Math.Clamp( (LodOut - distance) / LodFeatherOut, 0, 1 ) : distance <= LodOut ? 1 : 0;
		return fadeIn * fadeOut;
	}
}

/// <summary>
/// One part of an effect event (0x38): what it creates (a sound, light, decal, object, damage effect, lens flare,
/// light volume...), at which location, in which environment and violence mode, its velocity range along the
/// location's forward (world units per second) and cone (radians), angular velocity range and radius modifier range.
/// </summary>
public sealed record EffectPart( int Environment, int Disposition, int Location, int Flags, TagClass BaseGroup, TagEntry Type,
	Vector2 Velocity, float VelocityCone, Vector2 AngularVelocity, Vector2 RadiusModifier, int AScales, int BScales );

/// <summary>
/// A beam of an effect event (0x3C): a shader and functions for colour, alpha, width, length (world units), yaw and pitch.
/// </summary>
public sealed record EffectBeam( TagEntry Shader, int Location, ParticleProperty Color, ParticleProperty Alpha, ParticleProperty Width,
	ParticleProperty Length, ParticleProperty Yaw, ParticleProperty Pitch );

/// <summary>
/// An effect event (0x38): chance to be skipped, delay and duration ranges (seconds), parts, beams, accelerations and
/// particle systems. Events play one after another: each starts its delay after the previous one ends.
/// </summary>
public sealed record EffectEvent( int Flags, float SkipFraction, Vector2 Delay, Vector2 Duration, IReadOnlyList<EffectPart> Parts,
	IReadOnlyList<EffectBeam> Beams, int AccelerationCount, IReadOnlyList<EffectParticleSystem> ParticleSystems );

/// <summary>
/// A Halo 2 effect (<c>effe</c>): locations (marker names), events (parts and particle systems), a looping sound.
/// Unlike Halo CE, particle systems are inline (CE had separate pctl tags) and every particle property is a
/// mapping function (<see cref="ParticleProperty"/>).
/// </summary>
/// <remarks>Layout (verified on every map, BlamInspect <c>h2 &lt;maps&gt; effects</c>): flags 0x0, loop start event
/// 0x4 (i16), damage radius 0x8, locations 0xC (4: marker sid), events 0x14 (0x38: flags, skip fraction, delay 0x8,
/// duration 0x10, parts 0x18, beams 0x20, accelerations 0x28, particle systems 0x30), looping sound 0x1C, its
/// location 0x24, always/never play distances 0x28/0x2C.</remarks>
public sealed class EffectTag
{
	public static readonly TagClass Class = new( "effe" );

	public TagEntry Tag { get; }

	/// <summary>
	/// 1: deleted when the attachment carrying it deactivates.
	/// </summary>
	public int Flags { get; }

	/// <summary>
	/// The event a looping effect returns to after its last, or -1.
	/// </summary>
	public int LoopStartEvent { get; }

	public IReadOnlyList<string> Locations { get; }
	public IReadOnlyList<EffectEvent> Events { get; }
	public TagEntry LoopingSound { get; }
	public int LoopingSoundLocation { get; }

	public EffectTag( TagEntry tag )
	{
		Tag = tag;
		var cache = tag.Cache;
		var data = cache.TagData;
		var t = tag.Address;
		Flags = data.I32( t );
		LoopStartEvent = data.I16( t + 0x4 );
		Locations = data.Elements( t + 0xC, 4 ).Select( e => cache.GetString( data.U32( e ) ) ?? "" ).ToList();
		LoopingSound = cache.GetTagReference( t + 0x1C );
		LoopingSoundLocation = data.I16( t + 0x24 );

		Events = data.Elements( t + 0x14, 0x38 ).Select( e => new EffectEvent(
			data.I32( e ), data.F32( e + 0x4 ), data.Vec2( e + 0x8 ), data.Vec2( e + 0x10 ),
			data.Elements( e + 0x18, 0x38 ).Select( p => new EffectPart( data.I16( p ), data.I16( p + 0x2 ), data.I16( p + 0x4 ), data.I16( p + 0x6 ),
				data.Class( p + 0x8 ), cache.GetTagReference( p + 0xC ), data.Vec2( p + 0x14 ), data.F32( p + 0x1C ), data.Vec2( p + 0x20 ),
				data.Vec2( p + 0x28 ), data.I32( p + 0x30 ), data.I32( p + 0x34 ) ) ).ToList(),
			data.Elements( e + 0x20, 0x3C ).Select( b => new EffectBeam( cache.GetTagReference( b ), data.I16( b + 0x8 ),
				BeamProperty( cache, b + 0xC ), BeamProperty( cache, b + 0x14 ), BeamProperty( cache, b + 0x1C ), BeamProperty( cache, b + 0x24 ),
				BeamProperty( cache, b + 0x2C ), BeamProperty( cache, b + 0x34 ) ) ).ToList(),
			data.Count( e + 0x28 ),
			data.Elements( e + 0x30, EffectParticleSystem.Size ).Select( s => EffectParticleSystem.Read( cache, s ) ).ToList() ) ).ToList();
	}

	/// <summary>
	/// Beam functions are plain data references (8 bytes) without the property header: a function of the beam's age.
	/// </summary>
	static ParticleProperty BeamProperty( CacheFile cache, uint address )
	{
		var data = cache.TagData;
		var size = data.I32( address );
		var pointer = data.U32( address + 4 );
		var blob = size > 0 && data.Contains( pointer, size ) ? data.Slice( pointer, size ).ToArray() : [];
		return new ParticleProperty( ParticleInput.ParticleAge, ParticleInput.ParticleAge, 0, ParticleInput.ParticleAge, new MappingFunction( blob ) );
	}

	// Some particle systems are only made in a local player's first person view (camera mode 1) or only outside it (2).
	public bool DiffersInFirstPerson => Events.Any( e => e.ParticleSystems.Any( s => s.CameraMode is 1 or 2 ) );

	/// <summary>
	/// The location's marker name, "" for the object's origin (or an out of range index).
	/// </summary>
	public string LocationName( int index ) => index >= 0 && index < Locations.Count ? Locations[index] : "";

	public static bool IsEffect( TagEntry tag ) => tag is not null && tag.Address != 0 && tag.Class == Class;
}

/// <summary>
/// A named shader parameter of a particle (0x28): sid name, type (0 bitmap, 1 value, 2 colour), bitmap, value.
/// </summary>
public sealed record ParticleShaderParameter( string Name, int Type, TagEntry Bitmap, float Value, Vector3 Color );

/// <summary>
/// A Halo 2 particle (<c>prt3</c>): how its sprites look (billboard style, the bitmap's sequences, a particle shader
/// template with named parameters) and change over the particle's life (colour, alpha, scale, rotation and frame
/// functions, multiplied with the emitter's), its collision and death effects and attached particle systems.
/// </summary>
/// <remarks>Layout: flags 0x0, billboard style 0x4, first sequence 0x8, sequence count 0xA, shader template 0xC (stem),
/// parameters 0x14 (0x28), colour 0x1C, alpha 0x2C, scale 0x3C, rotation 0x4C, frame index 0x5C (properties),
/// collision effect 0x6C, death effect 0x74, locations 0x7C, attached particle systems 0x84 (0x38), postprocess 0x8C
/// (shad's ppdef layout).</remarks>
public sealed class ParticleTag
{
	public static readonly TagClass Class = new( "prt3" );

	public TagEntry Tag { get; }

	/// <summary>
	/// 1 spins, 2 random u mirror, 4 random v mirror, 8 frame animation one shot, 0x10 select random sequence,
	/// 0x20 disable frame blending, 0x80 receive lightmap lighting, 0x100 tint from diffuse texture, 0x200 dies at rest,
	/// 0x400 dies on structure collision, 0x800 dies in media, 0x1000 dies in air, 0x2000 bitmap authored vertically,
	/// 0x10000 set by the tools (Assembly's plugin has them a bit lower
	/// from 0x40, which doesn't fit the tags: sparks and muzzle flashes would die in air, impact bursts wouldn't stand up).
	/// </summary>
	public int Flags { get; }

	/// <summary>
	/// 0 screen facing, 1 parallel to direction (streaks), 2 perpendicular to direction, 3 vertical, 4 horizontal.
	/// </summary>
	public int BillboardStyle { get; }

	public int FirstSequence { get; }
	public int SequenceCount { get; }

	/// <summary>
	/// The particle shader template (a <c>stem</c>: particle_alpha_blend, particle_additive, ...).
	/// </summary>
	public TagEntry ShaderTemplate { get; }

	public IReadOnlyList<ParticleShaderParameter> Parameters { get; }

	public ParticleProperty Color { get; }
	public ParticleProperty Alpha { get; }
	public ParticleProperty Scale { get; }

	/// <summary>
	/// Turns (0-1 = 0-360 degrees).
	/// </summary>
	public ParticleProperty Rotation { get; }

	/// <summary>
	/// The animation frame as a fraction of the sequence (0-1), when set.
	/// </summary>
	public ParticleProperty FrameIndex { get; }

	public TagEntry CollisionEffect { get; }
	public TagEntry DeathEffect { get; }
	public IReadOnlyList<string> Locations { get; }
	public IReadOnlyList<EffectParticleSystem> AttachedSystems { get; }

	public ParticleTag( TagEntry tag )
	{
		Tag = tag;
		var cache = tag.Cache;
		var data = cache.TagData;
		var t = tag.Address;
		Flags = data.I32( t );
		BillboardStyle = data.I16( t + 0x4 );
		FirstSequence = data.I16( t + 0x8 );
		SequenceCount = data.I16( t + 0xA );
		ShaderTemplate = cache.GetTagReference( t + 0xC );
		Parameters = data.Elements( t + 0x14, 0x28 ).Select( p => new ParticleShaderParameter( cache.GetString( data.U32( p ) ) ?? "", data.I16( p + 0x4 ),
			cache.GetTagReference( p + 0x8 ), data.F32( p + 0x10 ), data.Vec3( p + 0x14 ) ) ).ToList();
		Color = ParticleProperty.Read( cache, t + 0x1C );
		Alpha = ParticleProperty.Read( cache, t + 0x2C );
		Scale = ParticleProperty.Read( cache, t + 0x3C );
		Rotation = ParticleProperty.Read( cache, t + 0x4C );
		FrameIndex = ParticleProperty.Read( cache, t + 0x5C );
		CollisionEffect = cache.GetTagReference( t + 0x6C );
		DeathEffect = cache.GetTagReference( t + 0x74 );
		Locations = data.Elements( t + 0x7C, 4 ).Select( e => cache.GetString( data.U32( e ) ) ?? "" ).ToList();
		AttachedSystems = data.Elements( t + 0x84, EffectParticleSystem.Size ).Select( s => EffectParticleSystem.Read( cache, s ) ).ToList();
	}

	/// <summary>
	/// The template's name without its folder (particle_alpha_blend, particle_additive, particle_additive_tint, ...).
	/// </summary>
	public string TemplateName => ShaderTemplate?.Path is { } path ? path[(path.LastIndexOf( '\\' ) + 1)..] : "";

	/// <summary>
	/// The sprite bitmap: the first bitmap parameter (alpha_map_texture, or bump_map for distortion).
	/// </summary>
	public TagEntry Bitmap => Parameters.FirstOrDefault( p => p.Type == 0 && p.Bitmap is not null )?.Bitmap;

	/// <summary>
	/// A value parameter by name (modulation_factor, fade_on_edge, distortion_scale), or the default.
	/// </summary>
	public float Value( string name, float fallback ) => Parameters.FirstOrDefault( p => p.Type == 1 && p.Name == name )?.Value ?? fallback;

	public bool Spins => (Flags & 0x1) != 0;
	public bool RandomUMirror => (Flags & 0x2) != 0;
	public bool RandomVMirror => (Flags & 0x4) != 0;
	public bool OneShotAnimation => (Flags & 0x8) != 0;
	public bool RandomSequence => (Flags & 0x10) != 0;
	public bool DiesAtRest => (Flags & 0x200) != 0;
	public bool DiesOnStructureCollision => (Flags & 0x400) != 0;

	public static bool IsParticle( TagEntry tag ) => tag is not null && tag.Address != 0 && tag.Class == Class;
}

/// <summary>
/// Halo 2 particle movement (<c>pmov</c>, "particle physics"): a template, flags (1 physics, 2 collide with structure,
/// 4 media, 8 scenery, 0x10 vehicles, 0x20 bipeds, 0x40 swarm, 0x80 wind) and movements (0 physics, 1 collider,
/// 2 swarm, 3 wind), each a list of parameters by id with a property each.
/// </summary>
/// <remarks>Physics parameters (by their values across every map, unverified): 0 gravity scale (1 default; smoke
/// below 0 rises), 1 drag (0 default; explosions 4-7), 2 unknown (1 default). Collider: 0 friction-like (sparks
/// 0.98-1, stones 0.1), 1 elasticity-like (rubber 0.65, stones 0.35).</remarks>
public sealed class ParticleMovementTag
{
	public static readonly TagClass Class = new( "pmov" );

	public TagEntry Tag { get; }
	public TagEntry Template { get; }
	public int Flags { get; }
	public IReadOnlyList<(int Type, IReadOnlyDictionary<int, ParticleProperty> Parameters)> Movements { get; }

	public ParticleMovementTag( TagEntry tag )
	{
		Tag = tag;
		var cache = tag.Cache;
		var data = cache.TagData;
		var t = tag.Address;
		Template = cache.GetTagReference( t );
		Flags = data.I32( t + 0x8 );
		Movements = data.Elements( t + 0xC, 0x14 ).Select( m => ((int)data.I16( m ),
			(IReadOnlyDictionary<int, ParticleProperty>)data.Elements( m + 0x4, 0x14 )
				.GroupBy( p => data.I32( p ) ).ToDictionary( g => g.Key, g => ParticleProperty.Read( cache, g.First() + 4 ) )) ).ToList();
	}

	/// <summary>
	/// A movement's parameter, or null.
	/// </summary>
	public ParticleProperty Parameter( int type, int id ) => Movements.Where( m => m.Type == type ).Select( m => m.Parameters.GetValueOrDefault( id ) ).FirstOrDefault( p => p is not null );

	public bool Collides => (Flags & 0x2) != 0;
}

/// <summary>
/// What an object carries (obje +0x94, 0x18 each): an effect, light, looping sound, contrail, lens flare or light
/// volume at a marker ("" the origin), with its change colour and the object functions scaling it.
/// </summary>
public sealed record ObjectAttachment( TagEntry Type, string Marker, int ChangeColor, string PrimaryScale, string SecondaryScale )
{
	public static List<ObjectAttachment> Read( TagEntry objectTag )
	{
		var cache = objectTag.Cache;
		var data = cache.TagData;
		return data.Elements( objectTag.Address + 0x94, 0x18 ).Select( e => new ObjectAttachment( cache.GetTagReference( e ),
			cache.GetString( data.U32( e + 0x8 ) ) ?? "", data.I16( e + 0xC ), cache.GetString( data.U32( e + 0x10 ) ) ?? "",
			cache.GetString( data.U32( e + 0x14 ) ) ?? "" ) ).ToList();
	}
}
