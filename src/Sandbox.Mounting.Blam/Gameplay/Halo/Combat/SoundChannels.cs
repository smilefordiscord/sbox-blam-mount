using System;
using System.Collections.Generic;

namespace Halo.Combat;

// A Halo sound class's limits on copies of one sound, in all and per object.
// PreemptionMilliseconds is how long a copy must play before a new one may take its place.
public readonly record struct SoundClassLimits( int MaximumPerDefinition, int MaximumPerObject, int PreemptionMilliseconds, bool Speech );

// Halo's sound classes, in the order of the tag's class enum, with MCC's limits.
// MCC lowers projectile impact to 3/2 and the three impact classes to 2/1 from the Xbox's 6/4 and 4/1.
public static class SoundClasses {
	// The class names the mount exports a sound tag's "Class" as, by index.
	public static readonly string[] Names =
	[
		"projectile impact", "projectile detonation", "unused", "unused1",
		"weapon fire", "weapon ready", "weapon reload", "weapon empty",
		"weapon charge", "weapon overheat", "weapon idle", "unused2", "unused3",
		"object impacts", "particle impacts", "slow particle impacts", "unused4", "unused5",
		"unit footsteps", "unit dialog", "unused6", "unused7", "vehicle collision",
		"vehicle engine", "unused8", "unused9", "device door", "device force field",
		"device machinery", "device nature", "device computers", "unused10", "music",
		"ambient nature", "ambient machinery", "ambient computers", "unused11", "unused12", "unused13",
		"first person damage", "unused14", "unused15", "unused16", "unused17", "scripted dialog player",
		"scripted effect", "scripted dialog other",
		"scripted dialog force unspatialized", "unused18", "unused19", "game event",
	];

	static readonly SoundClassLimits? Unused = null;

	static readonly SoundClassLimits?[] Table =
	[
		new( 3, 2, 100, false ),    // projectile impact
		new( 4, 1, 200, false ),    // projectile detonation
		Unused, Unused,
		new( 4, 1, 0, false ),      // weapon fire
		new( 4, 1, 500, false ),    // weapon ready
		new( 4, 1, 500, false ),    // weapon reload
		new( 4, 1, 60, false ),     // weapon empty
		new( 4, 1, 500, false ),    // weapon charge
		new( 4, 1, 500, false ),    // weapon overheat
		new( 4, 1, 500, false ),    // weapon idle
		Unused, Unused,
		new( 2, 1, 100, false ),    // object impacts
		new( 2, 1, 100, false ),    // particle impacts
		new( 2, 1, 1000, false ),   // slow particle impacts
		Unused, Unused,
		new( 4, 1, 200, false ),    // unit footsteps
		new( 4, 1, 100, true ),     // unit dialog
		Unused, Unused,
		new( 4, 2, 400, false ),    // vehicle collision
		new( 4, 2, 100, false ),    // vehicle engine
		Unused, Unused,
		new( 4, 1, 100, false ),    // device door
		new( 4, 1, 100, false ),    // device force field
		new( 4, 1, 100, false ),    // device machinery
		new( 4, 1, 100, false ),    // device nature
		new( 4, 1, 100, false ),    // device computers
		Unused,
		new( 4, 4, 100, false ),    // music
		new( 4, 1, 100, false ),    // ambient nature
		new( 4, 1, 100, false ),    // ambient machinery
		new( 4, 1, 100, false ),    // ambient computers
		Unused, Unused, Unused,
		new( 4, 1, 100, false ),    // first person damage
		Unused, Unused, Unused, Unused,
		new( 4, 4, 100, true ),     // scripted dialog player
		new( 4, 4, 100, false ),    // scripted effect
		new( 4, 4, 100, true ),     // scripted dialog other
		new( 4, 4, 100, true ),     // scripted dialog force unspatialized
		Unused, Unused,
		new( 4, 1, 100, false ),    // game event
	];

	// A class's index from its exported name, or -1.
	public static int IndexOf( string name ) => string.IsNullOrEmpty( name ) ? -1 : Array.IndexOf( Names, name );

	// A class's limits, or null for an unused or unknown class.
	public static SoundClassLimits? Get( int index ) => index >= 0 && index < Table.Length ? Table[index] : null;

	// Scripted dialog to the player, to others, or forced unspatialized.
	public static bool IsScriptedDialog( int index ) => index is 44 or 46 or 47;
}

// A sound playing on one of SoundChannels' channels.
public interface IChannelSound {
	// Still holding its channel: not finished or stopped.
	bool IsPlaying { get; }

	// Squared distance to the listener in world units, 0 for a sound without a position.
	float DistanceSquared { get; }

	void Stop();
}

// Halo's limit on copies of one sound, per object and per definition.
// Over a limit, it steals the first like channel past its preemption time and no farther away, or doesn't play.
// A looping track steals only from a sound more than a squared world unit farther, as MCC does.
// Speech replaces its source's last line; channel numbers match Halo's so "first" picks the same channel.
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
	public int Admit( string definition, SoundClassLimits? limits, object source, float distanceSquared, bool loopTrack, double now ) {
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
			if ( source is not null && Equals( other.Source, source ) )
				_likeSource.Add( i );
		}

		if ( _likeSource.Count >= c.MaximumPerObject )
			return FindLikeChannel( _likeSource, c, distanceSquared, loopTrack, now );
		if ( _like.Count >= c.MaximumPerDefinition )
			return FindLikeChannel( _like, c, distanceSquared, loopTrack, now );
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

	// The line an object is saying, or null.
	public IChannelSound SpeechOf( object source ) {
		if ( source is null ) return null;
		foreach ( var c in _channels )
			if ( c is not null && c.Speech && c.Sound.IsPlaying && Equals( c.Source, source ) ) return c.Sound;
		return null;
	}

	public void Clear() => _channels.Clear();

	int FindLikeChannel( List<int> channels, SoundClassLimits limits, float distanceSquared, bool loopTrack, double now ) {
		foreach ( var i in channels ) {
			var other = _channels[i];
			if ( (now - other.StartTime) * 1000.0 < limits.PreemptionMilliseconds ) continue;

			var otherDistanceSquared = other.Sound.DistanceSquared;
			var takes = loopTrack ? otherDistanceSquared - distanceSquared > 1f : distanceSquared - otherDistanceSquared < 1f;
			if ( !takes ) continue;

			Take( i );
			return i;
		}
		return -1;
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
