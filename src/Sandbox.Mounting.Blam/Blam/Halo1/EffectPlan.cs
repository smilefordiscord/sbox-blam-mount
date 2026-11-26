using System;
using System.Collections.Generic;
using System.Linq;
using Blam.Halo1.Tags;

namespace Blam.Halo1;

// Inside the namespace so these win over s&box's global Vector2/Vector3 when this compiles into the mount.
using Vector2 = System.Numerics.Vector2;

/// <summary>
/// What an effect event creates.
/// </summary>
public enum EffectItemKind
{
	/// <summary>Particles (<c>part</c>) from the event's particles block.</summary>
	Particles,
	/// <summary>A particle system part (<c>pctl</c>), e.g. a grenade's fireball.</summary>
	ParticleSystem,
	Light,
	Sound,
	Decal,
	/// <summary>Area damage (<c>jpt!</c>), which the game applies itself.</summary>
	Damage,
	/// <summary>Objects (garbage, debris, projectiles).</summary>
	Object,
	Other,
}

/// <summary>
/// How much of an effect item the mount reproduces: sounds and decals as a prefab's engine
/// components, particles, particle systems and lights in its Halo particle runtime.
/// </summary>
public enum ConversionStatus
{
	Full,
	/// <summary>Converted with something missing; see <see cref="EffectItem.Missing"/>.</summary>
	Partial,
	/// <summary>Not converted.</summary>
	None,
	/// <summary>Left to the game on purpose (damage).</summary>
	Game,
	/// <summary>Not part of this variant (first or third person only, water only, nonviolent only),
	/// or not made by Halo either (no location).</summary>
	Skipped,
}

/// <summary>
/// Which camera an effect is built for. Weapon effects have particles for the player's own view
/// (on the first person model's markers) and for everyone else's.
/// </summary>
public enum EffectView
{
	ThirdPerson,
	FirstPerson,
}

/// <summary>
/// One thing an effect creates, with when it happens and whether it converts.
/// </summary>
/// <param name="Start">Seconds after the effect starts (or into the loop) at which the event's duration starts.</param>
/// <param name="Duration">The event's duration: particles are spread over it.</param>
/// <param name="Location">Marker name of the location it's created at ("" is the effect's origin).</param>
/// <param name="Count">Expected particle count range, after the event's skip fraction.</param>
public sealed record EffectItem(
	EffectItemKind Kind,
	TagEntry Tag,
	int Event,
	float Start,
	float Duration,
	string Location,
	EffectParticle Particle,
	EffectPart Part,
	Vector2 Count,
	ConversionStatus Status,
	IReadOnlyList<string> Missing );

/// <summary>
/// An effect laid out on a timeline for building its prefab: every part and particle block of its
/// events with the time it happens, and what converts.
/// </summary>
/// <remarks>
/// Halo runs an effect's events one after another: each waits its delay, creates its parts (sounds,
/// lights, decals, particle systems, objects, damage) as its duration starts, and creates its
/// particles over the duration by their distribution function. Events after the first are skipped by
/// chance (skip fraction). Effects attached to objects loop over the loop events (all of them if the
/// tag names none). Random ranges are taken at their middle here.
/// </remarks>
public sealed class EffectPlan
{
	public TagEntry Tag { get; }
	public EffectTag Effect { get; }
	public EffectView View { get; }
	public bool Looping { get; }

	/// <summary>
	/// Looping: seconds after which the events repeat, at least a tick (a looping effect whose events
	/// have no delays or durations, such as an armed plasma grenade's sparkle, restarts every tick).
	/// One-shot: when the last event's duration ends.
	/// </summary>
	public float Cycle { get; }

	/// <summary>
	/// One of Halo's game ticks, in seconds.
	/// </summary>
	public const float Tick = 1 / 30f;

	public IReadOnlyList<EffectItem> Items { get; }

	public EffectPlan( CacheFile cache, TagEntry tag, EffectView view = EffectView.ThirdPerson, bool looping = false )
	{
		Tag = tag;
		Effect = new EffectTag( cache, tag );
		View = view;
		Looping = looping;

		var events = Effect.Events;
		var first = 0;
		var last = events.Count - 1;
		if ( looping )
		{
			first = Effect.LoopStart >= 0 && Effect.LoopStart < events.Count ? Effect.LoopStart : 0;
			last = Effect.LoopStop >= first && Effect.LoopStop < events.Count ? Effect.LoopStop : events.Count - 1;
		}

		var items = new List<EffectItem>();
		var time = 0f;
		for ( var e = first; e <= last; e++ )
		{
			var ev = events[e];
			var start = time + Average( ev.Delay );
			var duration = MathF.Max( Average( ev.Duration ), 0 );
			time = start + duration;

			// The first event always happens; later ones by chance.
			var chance = e == 0 ? 1 : 1 - Math.Clamp( ev.SkipFraction, 0, 1 );

			foreach ( var part in ev.Parts )
				items.Add( PartItem( cache, part, e, start, duration, chance ) );

			foreach ( var particle in ev.Particles )
				items.Add( ParticleItem( cache, particle, e, start, duration, chance ) );
		}

		Cycle = looping ? MathF.Max( time, Tick ) : time;
		Items = items;
	}

	/// <summary>
	/// The whole effect: full if every item converts (or is left to the game or another variant),
	/// none if nothing does, skipped if it creates nothing at all (placeholder effects).
	/// </summary>
	public ConversionStatus Overall
	{
		get
		{
			var relevant = Items.Where( i => i.Status is not (ConversionStatus.Game or ConversionStatus.Skipped) ).ToList();
			if ( relevant.Count == 0 )
				return Items.Count == 0 ? ConversionStatus.Skipped : ConversionStatus.Full;

			if ( relevant.All( i => i.Status == ConversionStatus.Full ) )
				return ConversionStatus.Full;

			return relevant.All( i => i.Status == ConversionStatus.None ) ? ConversionStatus.None : ConversionStatus.Partial;
		}
	}

	/// <summary>
	/// True if any particles are only for one camera, so the effect looks different in first person.
	/// </summary>
	public static bool HasCameraVariants( EffectTag effect ) =>
		effect.Events.Any( e => e.Particles.Any( p => p.CameraMode is 1 or 2 ) );

	EffectItem PartItem( CacheFile cache, EffectPart part, int e, float start, float duration, float chance )
	{
		var missing = new List<string>();
		var location = Effect.LocationName( part.Location );
		var status = ConversionStatus.Full;
		var kind = KindOf( part.Type.Class );

		if ( !Effect.HasLocation( part.Location ) )
		{
			status = ConversionStatus.Skipped;
			missing.Add( "no location (Halo creates nothing)" );
		}
		else if ( part.CreateIn is 2 or 3 || part.ViolenceMode == 2 )
		{
			status = ConversionStatus.Skipped;
			missing.Add( part.CreateIn == 2 ? "water only" : part.CreateIn == 3 ? "space only" : "nonviolent only" );
		}
		else switch ( kind )
		{
			case EffectItemKind.Damage:
				status = ConversionStatus.Game;
				break;

			case EffectItemKind.Object:
			case EffectItemKind.Other:
				status = ConversionStatus.None;
				missing.Add( $"{part.Type.Class} parts aren't spawned" );
				break;

			case EffectItemKind.Sound:
				if ( !Looping && start > 0.01f )
					missing.Add( $"sound delay {start:0.##} s not honoured" );
				break;

			case EffectItemKind.Light:
				var light = new LightTag( cache, part.Type );
				// Made by the mount's particle runtime, with its lens flare fading with it.
				if ( light.IsSpot )
					missing.Add( "spot light drawn as a point light" );
				break;

			case EffectItemKind.Decal:
				if ( Looping )
				{
					status = ConversionStatus.None;
					missing.Add( "decals of looping effects aren't made" );
					break;
				}

				if ( start > 0.01f )
					missing.Add( $"decal delay {start:0.##} s not honoured" );
				break;

			// Particle systems play out in the mount's particle runtime as Halo's do.
		}

		if ( status == ConversionStatus.Full && missing.Count > 0 )
			status = ConversionStatus.Partial;

		return new EffectItem( kind, part.Type, e, start, duration, location, null, part, new Vector2( chance ), status, missing );
	}

	EffectItem ParticleItem( CacheFile cache, EffectParticle particle, int e, float start, float duration, float chance )
	{
		var missing = new List<string>();
		var status = ConversionStatus.Full;
		var location = Effect.LocationName( particle.Location );
		var count = particle.Count * chance;

		var wrongCamera = View == EffectView.ThirdPerson ? particle.CameraMode == 1 : particle.CameraMode == 2;
		if ( !Effect.HasLocation( particle.Location ) )
		{
			status = ConversionStatus.Skipped;
			missing.Add( "no location (Halo creates nothing)" );
		}
		else if ( wrongCamera )
		{
			status = ConversionStatus.Skipped;
			missing.Add( particle.CameraMode == 1 ? "first person only" : "third person only" );
		}
		else if ( particle.CreateIn is 2 or 3 || particle.ViolenceMode == 2 )
		{
			status = ConversionStatus.Skipped;
			missing.Add( particle.CreateIn == 2 ? "water only" : particle.CreateIn == 3 ? "space only" : "nonviolent only" );
		}
		else if ( particle.Particle.Class != EffectTagClasses.Particle )
		{
			status = ConversionStatus.None;
			missing.Add( $"{particle.Particle.Class} as particles" );
		}
		else
		{
			var part = new ParticleTag( cache, particle.Particle );
			if ( part.Bitmap is null )
			{
				status = ConversionStatus.None;
				missing.Add( "no bitmap" );
			}
			else if ( part.InitialSequencesIn( new BitmapTag( cache, part.Bitmap ).Sequences.Count ).Count == 0 )
			{
				// Halo draws nothing for it either.
				status = ConversionStatus.Skipped;
				missing.Add( "bitmap without sequences" );
			}
			else
			{
				// The mount's particle runtime makes, moves and draws them as Halo does; its sprites
				// (halo1_sprite.shader) blend as Halo's, in gamma space, except these.
				if ( part.BlendFunction is FramebufferBlendFunction.Multiply or FramebufferBlendFunction.DoubleMultiply or FramebufferBlendFunction.Subtract or FramebufferBlendFunction.ComponentMin )
					missing.Add( $"{part.BlendFunction} blended in linear space" );
			}
		}

		if ( status == ConversionStatus.Full && missing.Count > 0 )
			status = ConversionStatus.Partial;

		return new EffectItem( EffectItemKind.Particles, particle.Particle, e, start, duration, location, particle, null, count, status, missing );
	}

	public static readonly TagClass DecalClass = new( "deca" );
	public static readonly TagClass DamageClass = new( "jpt!" );
	public static readonly TagClass SoundClass = new( "snd!" );

	static readonly HashSet<string> ObjectClasses = ["obje", "garb", "proj", "weap", "eqip", "scen", "bipd", "vehi", "mach", "ctrl", "lifi", "plac", "ssce", "item", "devi", "unit"];

	static EffectItemKind KindOf( TagClass type )
	{
		if ( type == EffectTagClasses.ParticleSystem ) return EffectItemKind.ParticleSystem;
		if ( type == EffectTagClasses.Light ) return EffectItemKind.Light;
		if ( type == SoundClass ) return EffectItemKind.Sound;
		if ( type == DecalClass ) return EffectItemKind.Decal;
		if ( type == DamageClass ) return EffectItemKind.Damage;
		return ObjectClasses.Contains( type.ToString() ) ? EffectItemKind.Object : EffectItemKind.Other;
	}

	/// <summary>
	/// Halo blend functions drawn additively; the rest are drawn alpha blended.
	/// </summary>
	public static bool IsAdditive( FramebufferBlendFunction blend ) =>
		blend is FramebufferBlendFunction.Add or FramebufferBlendFunction.AlphaMultiplyAdd or FramebufferBlendFunction.ComponentMax;

	static float Average( Vector2 range ) => (range.X + range.Y) * 0.5f;
}
