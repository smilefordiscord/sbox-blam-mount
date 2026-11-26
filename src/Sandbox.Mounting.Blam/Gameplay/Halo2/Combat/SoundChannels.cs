using Halo2.Data;
using System;
using System.Collections.Generic;

namespace Halo2.Combat;

// A Halo 2 sound class's limits on copies of one sound, in all and per object.
// PreemptionMilliseconds is how long a copy must play before a new one may take its place.
public readonly record struct SoundClassLimits( int MaximumPerDefinition, int MaximumPerObject, int PreemptionMilliseconds, bool Speech );

// Halo 2's 54 sound classes in the order of the sound tag's class enum, with the limits of MCC's sound_classes tag.
public static class SoundClasses {
	// The class names the mount exports a sound tag's "Class" as, by index.
	public static readonly string[] Names =
	[
		"projectile impact", "projectile detonation", "projectile flyby", "unused", "weapon fire", "weapon ready", "weapon reload",
		"weapon empty", "weapon charge", "weapon overheat", "weapon idle", "weapon melee", "weapon animation", "object impacts",
		"particle impacts", "weapon fire lod", "weapon fire lod far", "lfe", "unit footsteps", "unit dialog", "unit animation", "unused",
		"vehicle collision", "vehicle engine", "vehicle animation", "unused", "device door", "unused", "device machinery",
		"device stationary", "unused", "unused", "music", "ambient nature", "ambient machinery", "unused", "huge ass", "object looping",
		"cinematic music", "reflection", "reflection lod", "reflection lod far", "unused", "unused", "unused", "cortana mission",
		"cortana cinematic", "mission dialog", "cinematic dialog", "scripted cinematic foley", "game event", "ui", "test",
		"multilingual test",
	];

	static readonly SoundClassLimits? Unused = null;

	static readonly SoundClassLimits?[] Table =
	[
		new( 6, 4, 100, false ),    // projectile impact
		new( 4, 1, 200, false ),    // projectile detonation
		new( 6, 4, 100, false ),    // projectile flyby
		Unused,
		new( 8, 2, 0, false ),      // weapon fire
		new( 4, 1, 500, false ),    // weapon ready
		new( 8, 2, 500, false ),    // weapon reload
		new( 4, 1, 60, false ),     // weapon empty
		new( 4, 1, 500, false ),    // weapon charge
		new( 4, 1, 500, false ),    // weapon overheat
		new( 4, 1, 500, false ),    // weapon idle
		new( 4, 1, 500, false ),    // weapon melee
		new( 4, 1, 500, false ),    // weapon animation
		new( 4, 1, 100, false ),    // object impacts
		new( 4, 1, 100, false ),    // particle impacts
		new( 8, 2, 0, false ),      // weapon fire lod
		new( 8, 2, 0, false ),      // weapon fire lod far
		Unused,                     // lfe
		new( 4, 2, 200, false ),    // unit footsteps
		new( 4, 1, 100, true ),     // unit dialog
		new( 8, 2, 500, false ),    // unit animation
		Unused,
		new( 4, 2, 400, false ),    // vehicle collision
		new( 8, 3, 100, false ),    // vehicle engine
		new( 10, 2, 500, false ),   // vehicle animation
		Unused,
		new( 4, 1, 100, false ),    // device door
		Unused,
		new( 4, 4, 100, false ),    // device machinery
		new( 4, 2, 100, false ),    // device stationary
		Unused, Unused,
		new( 4, 4, 100, false ),    // music
		new( 4, 4, 100, false ),    // ambient nature
		new( 4, 1, 100, false ),    // ambient machinery
		Unused,
		new( 4, 1, 100, false ),    // huge ass
		new( 4, 1, 100, false ),    // object looping
		new( 4, 4, 100, false ),    // cinematic music
		new( 8, 2, 0, false ),      // reflection
		new( 8, 2, 0, false ),      // reflection lod
		new( 8, 2, 0, false ),      // reflection lod far
		Unused, Unused, Unused,
		new( 4, 4, 100, true ),     // cortana mission
		new( 4, 4, 100, true ),     // cortana cinematic
		new( 4, 4, 100, true ),     // mission dialog
		new( 4, 4, 100, true ),     // cinematic dialog
		new( 4, 4, 100, false ),    // scripted cinematic foley
		new( 4, 1, 100, false ),    // game event
		new( 4, 1, 100, false ),    // ui
		new( 16, 16, 100, false ),  // test
		new( 16, 16, 100, false ),  // multilingual test
	];

	// A class's index from its exported name, or -1.
	public static int IndexOf( string name ) => string.IsNullOrEmpty( name ) ? -1 : Array.IndexOf( Names, name );

	// A class's limits, or null for an unused or unknown class.
	public static SoundClassLimits? Get( int index ) => index >= 0 && index < Table.Length ? Table[index] : null;
}

// A sound playing on one of SoundChannels' channels.
public interface IChannelSound {
	// Still holding its channel: not finished or stopped.
	bool IsPlaying { get; }

	void Stop();
}

// Halo 2's limit on copies of one sound, per object and per definition.
// Over the object's limit it takes the channel of that object's oldest copy past the class's preemption time.
// Over the definition's limit it takes the oldest such copy of any object's; with none, the new sound doesn't play.
// A copy on the same object started in the same millisecond is refused, and speech replaces its source's last line.
public sealed class SoundChannels {
	sealed class Channel {
		public string Definition;
		public object Source;
		public bool Speech;
		public double StartTime;
		public IChannelSound Sound;
	}

	readonly List<Channel> _channels = new();
	readonly List<int> _like = new();
	readonly List<int> _likeSource = new();

	// Channels holding a sound.
	public int Count {
		get {
			Release();
			var count = 0;
			foreach ( var c in _channels )
				if ( c is not null ) count++;
			return count;
		}
	}

	// The channel a new sound plays on, or -1 if it doesn't play; a sound it takes the channel of is stopped.
	// Call Assign with the channel once the sound has started.
	// Null limits play it on a free channel; source is compared with Equals.
	public int Admit( string definition, SoundClassLimits? limits, object source, double now ) {
		Release();
		if ( limits is not { } c ) return FreeChannel();

		if ( c.Speech ) {
			if ( source is null ) return FreeChannel();
			for ( var i = 0; i < _channels.Count; i++ ) {
				var other = _channels[i];
				if ( other is not null && other.Speech && Equals( other.Source, source ) ) {
					Take( i );
					return i;
				}
			}
			return FreeChannel();
		}

		_like.Clear();
		_likeSource.Clear();
		for ( var i = 0; i < _channels.Count; i++ ) {
			var other = _channels[i];
			if ( other is null || other.Definition != definition ) continue;
			_like.Add( i );
			if ( source is null || !Equals( other.Source, source ) ) continue;
			if ( (now - other.StartTime) * 1000.0 < 1 ) return -1;
			_likeSource.Add( i );
		}

		if ( _likeSource.Count >= c.MaximumPerObject )
			return Steal( _likeSource, c, now );
		if ( _like.Count >= c.MaximumPerDefinition )
			return Steal( _like, c, now );
		return FreeChannel();
	}

	// Records the sound started on a channel Admit gave.
	public void Assign( int channel, string definition, SoundClassLimits? limits, object source, double now, IChannelSound sound ) {
		if ( channel < 0 || sound is null ) return;
		while ( _channels.Count <= channel ) _channels.Add( null );
		_channels[channel] = new Channel {
			Definition = definition,
			Source = source,
			Speech = limits?.Speech ?? false,
			StartTime = now,
			Sound = sound,
		};
	}

	public void Clear() => _channels.Clear();

	// The oldest of the channels that has played past the class's preemption time.
	int Steal( List<int> channels, SoundClassLimits limits, double now ) {
		var oldest = -1;
		foreach ( var i in channels ) {
			var other = _channels[i];
			if ( (now - other.StartTime) * 1000.0 < limits.PreemptionMilliseconds ) continue;
			if ( oldest < 0 || other.StartTime < _channels[oldest].StartTime ) oldest = i;
		}
		if ( oldest >= 0 ) Take( oldest );
		return oldest;
	}

	void Take( int channel ) {
		var other = _channels[channel];
		_channels[channel] = null;
		other?.Sound.Stop();
	}

	int FreeChannel() {
		var index = _channels.IndexOf( null );
		if ( index >= 0 ) return index;
		_channels.Add( null );
		return _channels.Count - 1;
	}

	// Frees the channels of sounds that have finished.
	void Release() {
		for ( var i = 0; i < _channels.Count; i++ ) {
			if ( _channels[i] is { } c && !c.Sound.IsPlaying )
				_channels[i] = null;
		}
		while ( _channels.Count > 0 && _channels[^1] is null )
			_channels.RemoveAt( _channels.Count - 1 );
	}
}
