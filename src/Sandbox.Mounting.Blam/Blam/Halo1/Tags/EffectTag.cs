using System.Collections.Generic;

namespace Blam.Halo1.Tags;

// Inside the namespace so these win over s&box's global Vector2/Vector3 when this compiles into the mount.
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;
using Vector4 = System.Numerics.Vector4;

/// <summary>
/// Particles an effect event creates.
/// </summary>
/// <param name="CreateIn">0 any environment, 1 air only, 2 water only, 3 space only.</param>
/// <param name="ViolenceMode">0 either, 1 violent mode only (blood), 2 nonviolent mode only.</param>
/// <param name="CameraMode">0 any, 1 first person only, 2 third person only, 3 first person if possible.</param>
/// <param name="Location">Index into the effect's locations (marker names).</param>
/// <param name="Direction">Initial velocity direction in the location's space (x forward).</param>
/// <param name="Offset">Initial position offset in the location's space, world units.</param>
/// <param name="Flags">0x1 stay attached to the marker, 0x2 random initial angle, 0x4 tint from the object's color.</param>
/// <param name="Distribution">How creations spread over the event: 0 all at the start, 1 all at the end, 2 evenly, 3 building up, 4 falling off, 5 both.</param>
/// <param name="Count">How many, as a range.</param>
/// <param name="DistributionRadius">Distance from the location they're created at, range.</param>
/// <param name="Velocity">Speed range, world units per second.</param>
/// <param name="ConeAngle">Velocities are spread inside this cone (radians, the largest angle from the direction).</param>
/// <param name="AngularVelocity">Spin range, radians per second.</param>
/// <param name="Radius">Particle radius range, world units.</param>
/// <param name="TintLower">RGBA.</param>
/// <param name="TintUpper">RGBA.</param>
/// <param name="ScaleA">Values the effect's A scale multiplies (0x1 velocity, 0x20 count, 0x200 radius, 0x800 tint...).</param>
/// <param name="ScaleB">Values the effect's B scale multiplies.</param>
public sealed record EffectParticle(
	TagEntry Particle,
	int CreateIn,
	int ViolenceMode,
	int CameraMode,
	int Location,
	Vector3 Direction,
	Vector3 Offset,
	uint Flags,
	int Distribution,
	Vector2 Count,
	Vector2 DistributionRadius,
	Vector2 Velocity,
	float ConeAngle,
	Vector2 AngularVelocity,
	Vector2 Radius,
	Vector4 TintLower,
	Vector4 TintUpper,
	uint ScaleA,
	uint ScaleB );

/// <summary>
/// Something other than particles that an effect event creates as its duration starts: a sound,
/// light, decal, particle system, object (debris, garbage) or area damage.
/// </summary>
/// <param name="CreateIn">0 any environment, 1 air only, 2 water only, 3 space only.</param>
/// <param name="ViolenceMode">0 either, 1 violent mode only, 2 nonviolent mode only.</param>
/// <param name="Location">Index into the effect's locations (marker names).</param>
/// <param name="Flags">0x1 face down whatever the location (decals).</param>
/// <param name="Velocity">Initial speed range along the location's forward (objects, particle systems), world units per second.</param>
/// <param name="ConeAngle">Velocities are spread inside this cone (radians).</param>
/// <param name="AngularVelocity">Spin range for objects, radians per second.</param>
/// <param name="RadiusModifier">Decal size multiplier range.</param>
/// <param name="ScaleA">Values the effect's A scale multiplies (0x1 velocity, 0x20 the type's own scale: sound gain, light and decal size...).</param>
/// <param name="ScaleB">Values the effect's B scale multiplies.</param>
public sealed record EffectPart(
	TagEntry Type,
	int CreateIn,
	int ViolenceMode,
	int Location,
	uint Flags,
	Vector2 Velocity,
	float ConeAngle,
	Vector2 AngularVelocity,
	Vector2 RadiusModifier,
	uint ScaleA,
	uint ScaleB );

/// <summary>
/// A step of an effect: after a delay its parts are created, then its particles over its duration,
/// then the next event's delay starts.
/// </summary>
/// <param name="SkipFraction">Chance the event doesn't happen (never applied to the first event).</param>
public sealed record EffectEvent( float SkipFraction, Vector2 Delay, Vector2 Duration, IReadOnlyList<EffectPart> Parts, IReadOnlyList<EffectParticle> Particles );

/// <summary>
/// Reader for <c>effe</c> (effect) tags: sequences of events that create particles, lights,
/// sounds, decals, objects and damage.
/// </summary>
/// <remarks>
/// Offsets: flags 0x0 (0x1 deleted when the attachment deactivates, 0x4 must be deterministic),
/// loop start/stop event 0x4/0x6, locations 0x28 (32 byte marker names), events 0x34 (68 bytes:
/// skip fraction 0x4, delay 0x8, duration 0x10, parts 0x2C, particles 0x38).
///
/// Parts are 104 bytes: create in 0x0, violence mode 0x2, location 0x4, flags 0x6, type 0x18
/// (its group again at 0x14: deca, jpt!, ligh, obje, snd!, pctl), velocity 0x40, cone angle 0x48,
/// angular velocity 0x4C, radius modifier 0x54, A/B scale flags 0x60/0x64.
///
/// Particles are 232 bytes: create in 0x0, violence mode 0x2, camera mode 0x4, location 0x8,
/// relative offset 0x14, relative direction vector 0x20, particle 0x54, flags 0x64, distribution
/// function 0x68, count 0x6C (two int16), distribution radius 0x70, velocity 0x84, cone angle
/// 0x8C, angular velocity 0x90, radius 0xA0, tint bounds 0xB0/0xC0 (ARGB), A/B scale flags
/// 0xE0/0xE4.
/// </remarks>
public sealed class EffectTag
{
	public uint Flags { get; }
	public int LoopStart { get; }
	public int LoopStop { get; }

	/// <summary>
	/// Marker names that parts and particles are created at. Projectile impacts offer "normal",
	/// "incident", "negative incident", "reflection" and "gravity", detonations "" and "gravity",
	/// and effects on objects their model's markers ("primary trigger" and so on).
	/// </summary>
	public IReadOnlyList<string> Locations { get; }

	public IReadOnlyList<EffectEvent> Events { get; }

	public EffectTag( CacheFile cache, TagEntry tag )
	{
		var data = cache.TagData;
		var a = tag.Address;
		Flags = data.U32( a );
		LoopStart = data.I16( a + 0x4 );
		LoopStop = data.I16( a + 0x6 );

		var locations = new List<string>();
		foreach ( var l in data.Block( a + 0x28, 0x20 ) )
			locations.Add( data.CString( l, 32 ) );
		Locations = locations;

		var events = new List<EffectEvent>();
		foreach ( var e in data.Block( a + 0x34, 0x44 ) )
		{
			var parts = new List<EffectPart>();
			foreach ( var p in data.Block( e + 0x2C, 0x68 ) )
			{
				var type = cache.GetTagReference( data, p + 0x18 );
				if ( type is null )
					continue;

				parts.Add( new EffectPart(
					Type: type,
					CreateIn: data.U16( p ),
					ViolenceMode: data.U16( p + 0x2 ),
					Location: data.I16( p + 0x4 ),
					Flags: data.U16( p + 0x6 ),
					Velocity: data.Vec2( p + 0x40 ),
					ConeAngle: data.F32( p + 0x48 ),
					AngularVelocity: data.Vec2( p + 0x4C ),
					RadiusModifier: data.Vec2( p + 0x54 ),
					ScaleA: data.U32( p + 0x60 ),
					ScaleB: data.U32( p + 0x64 ) ) );
			}

			var particles = new List<EffectParticle>();
			foreach ( var p in data.Block( e + 0x38, 0xE8 ) )
			{
				var particle = cache.GetTagReference( data, p + 0x54 );
				if ( particle is null )
					continue;

				particles.Add( new EffectParticle(
					Particle: particle,
					CreateIn: data.U16( p ),
					ViolenceMode: data.U16( p + 0x2 ),
					CameraMode: data.U16( p + 0x4 ),
					Location: data.I16( p + 0x8 ),
					Direction: data.Vec3( p + 0x20 ),
					Offset: data.Vec3( p + 0x14 ),
					Flags: data.U32( p + 0x64 ),
					Distribution: data.U16( p + 0x68 ),
					Count: new Vector2( data.I16( p + 0x6C ), data.I16( p + 0x6E ) ),
					DistributionRadius: data.Vec2( p + 0x70 ),
					Velocity: data.Vec2( p + 0x84 ),
					ConeAngle: data.F32( p + 0x8C ),
					AngularVelocity: data.Vec2( p + 0x90 ),
					Radius: data.Vec2( p + 0xA0 ),
					TintLower: WeatherParticleSystemTag.ReadArgb( data, p + 0xB0 ),
					TintUpper: WeatherParticleSystemTag.ReadArgb( data, p + 0xC0 ),
					ScaleA: data.U32( p + 0xE0 ),
					ScaleB: data.U32( p + 0xE4 ) ) );
			}

			events.Add( new EffectEvent( data.F32( e + 0x4 ), data.Vec2( e + 0x8 ), data.Vec2( e + 0x10 ), parts, particles ) );
		}
		Events = events;
	}

	/// <summary>
	/// The marker name of a location, or "" if the index is out of range.
	/// </summary>
	public string LocationName( int index ) => HasLocation( index ) ? Locations[index] : "";

	/// <summary>
	/// Whether a part's or particle block's location index names one of the locations. Halo creates
	/// nothing for one that doesn't (none, or past the last): the warthog's gun has a second muzzle
	/// flash and the scorpion's machine gun stray casings and bubbles that never appear.
	/// </summary>
	public bool HasLocation( int index ) => index >= 0 && index < Locations.Count;
}
