using System;
using Halo.Data;

namespace Halo.Vehicles;

// An effect's particle counts at its A and B scales, per the tag's particle scale flags, to skip effects that make nothing.
// A flag scales a value's lower bound; its "delta" flag scales the range above it.
// Particle counts truncate, as Halo's do.
static class EffectScales {
	sealed class EffectFile {
		public List<EventFile> Events { get; set; } = new();
	}

	sealed class EventFile {
		public List<ParticleFile> Particles { get; set; } = new();
	}

	internal sealed class ParticleFile {
		public float[] Count { get; set; }
		public string[] AScalesValues { get; set; }
		public string[] BScalesValues { get; set; }
	}

	public sealed class Info {
		internal List<ParticleFile> Particles = new();
	}

	static Dictionary<string, Info> _cache;
	static Dictionary<string, Info> Cache => _cache ??= new( StringComparer.OrdinalIgnoreCase );

	// The effect's particle scales, or null when the mount's document doesn't carry them.
	public static Info For( string effectTag ) {
		if ( string.IsNullOrEmpty( effectTag ) ) return null;
		var path = effectTag.Replace( '\\', '/' );
		if ( Cache.TryGetValue( path, out var cached ) ) return cached;

		Info info = null;
		var file = HaloDataStore.Tag<EffectFile>( path );
		var particles = file?.Events?.SelectMany( e => e.Particles ?? new() ).ToList();
		if ( particles is { Count: > 0 } && particles.Any( p => p.AScalesValues is not null || p.BScalesValues is not null ) ) {
			info = new Info();
			info.Particles.AddRange( particles );
		}

		Cache[path] = info;
		return info;
	}

	// A, B, both or neither, by which scale lists carry the flag.
	static float Scale( ParticleFile p, string flag, float a, float b ) {
		var scale = 1f;
		if ( p.AScalesValues?.Contains( flag ) ?? false ) scale *= a;
		if ( p.BScalesValues?.Contains( flag ) ?? false ) scale *= b;
		return scale;
	}

	// The lower bound scales by the value's flag, the range above it by its delta flag.
	static (float Lower, float Upper) Range( ParticleFile p, float[] bounds, string flag, float a, float b ) {
		var lower = bounds is { Length: >= 1 } ? bounds[0] : 0;
		var upper = bounds is { Length: >= 2 } ? bounds[1] : lower;
		var scaledLower = lower * Scale( p, flag, a, b );
		return (scaledLower, scaledLower + (upper - lower) * Scale( p, $"{flag} delta", a, b ));
	}

	// Whether the effect makes any particles at these scales (a count that truncates to 0 makes none).
	public static bool MakesParticles( Info info, float a, float b = 1 ) {
		foreach ( var p in info.Particles )
			if ( Range( p, p.Count, "count", a, b ).Upper >= 1 ) return true;
		return false;
	}
}
