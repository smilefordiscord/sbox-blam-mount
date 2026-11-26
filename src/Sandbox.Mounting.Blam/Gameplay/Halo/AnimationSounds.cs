using System;

namespace Halo;

// The sounds Halo's animations play, such as reloads, readies, melees and grenade throws.
// Each animation names one of its graph's sound references and a 30 fps frame to play it on.
// Read from the mount's export, mount://haloce/tags/<antr>.antr.json.
public static class AnimationSounds {
	public readonly record struct Cue( string Sound, float Time );

	sealed class GraphFile {
		public List<SoundReference> SoundReferences { get; set; } = new();
		public List<Animation> Animations { get; set; } = new();
	}

	sealed class SoundReference {
		public string Sound { get; set; }
	}

	sealed class Animation {
		public string Name { get; set; }
		public int? Sound { get; set; }
		public int SoundFrameIndex { get; set; }
	}

	static readonly Dictionary<string, Dictionary<string, Cue>> Graphs = new();

	// The sound an animation plays and when, or null.
	// graph is the antr tag path; animation matches with or without its "%n" variant suffix.
	public static Cue? For( string graph, string animation ) {
		if ( string.IsNullOrEmpty( graph ) || string.IsNullOrEmpty( animation ) ) return null;
		var cues = Load( graph );
		if ( cues is null ) return null;

		if ( cues.TryGetValue( animation, out var cue ) ) return cue;
		var percent = animation.IndexOf( '%' );
		if ( percent > 0 && cues.TryGetValue( animation[..percent], out cue ) ) return cue;
		var colon = animation.LastIndexOf( ':' );
		if ( colon >= 0 && cues.TryGetValue( animation[(colon + 1)..], out cue ) ) return cue;

		// A first person slot without its own animation, like the AR's reload-empty, uses its mapped animation's sound.
		const string firstPerson = "first-person ";
		var name = percent > 0 ? animation[..percent] : animation;
		if ( name.StartsWith( firstPerson ) &&
			AnimationGraphs.FirstPersonSlotAnimation( graph, name[firstPerson.Length..] ) is { } mapped &&
			cues.TryGetValue( mapped, out cue ) )
			return cue;
		return null;
	}

	// Every sound a graph's animations play, for preloading.
	public static IEnumerable<string> All( string graph ) =>
		string.IsNullOrEmpty( graph ) ? Enumerable.Empty<string>() : Load( graph )?.Values.Select( x => x.Sound ).Distinct().ToList() ?? Enumerable.Empty<string>();

	static Dictionary<string, Cue> Load( string graph ) {
		graph = graph.Replace( '\\', '/' );
		if ( !graph.EndsWith( ".antr" ) ) graph += ".antr";
		if ( Graphs.TryGetValue( graph, out var cached ) ) return cached;

		Dictionary<string, Cue> cues = null;
		try {
			var json = Data.HaloDataStore.ReadMount( $"tags/{graph}.json" );
			if ( !string.IsNullOrEmpty( json ) ) {
				var file = Json.Deserialize<GraphFile>( json );
				cues = new Dictionary<string, Cue>();
				foreach ( var a in file?.Animations ?? new() ) {
					if ( a?.Name is null || a.Sound is not int index || index < 0 || index >= file.SoundReferences.Count ) continue;
					var sound = file.SoundReferences[index]?.Sound;
					if ( string.IsNullOrEmpty( sound ) ) continue;
					cues.TryAdd( a.Name, new Cue( sound, a.SoundFrameIndex / HaloUnits.TicksPerSecond ) );
				}
			}
		}
		catch ( Exception e ) {
			Log.Warning( $"Couldn't read the sounds of {graph}: {e.Message}" );
		}

		Graphs[graph] = cues;
		return cues;
	}
}

// Plays an animation's sound when its playback reaches the sound's frame. One per renderer.
public sealed class AnimationSoundPlayer {
	string _graph;
	string _animation;
	string _sound;
	float _at;
	bool _local;
	object _source;
	SoundHandle _playing;
	string _playingAnimation;

	// Call when a sequence starts or restarts.
	// source is the object for Halo's per-object sound limit; first person weapon animations play on the weapon.
	public void Started( string graph, string animation, bool local, object source = null ) {
		var cue = AnimationSounds.For( graph, animation );
		_graph = graph;
		_animation = animation;
		_sound = cue?.Sound;
		_at = cue?.Time ?? 0;
		_local = local;
		_source = source;
	}

	// Call every frame with the sequence's current time.
	// A sound reaching its frame while audible is false is dropped, as Halo drops a first person weapon's sound from any other perspective.
	public void Update( float sequenceTime, Vector3 position, bool audible = true ) {
		if ( _sound is null || sequenceTime + 0.0001f < _at ) return;

		if ( audible ) {
			_playing = _local ? Combat.HaloSounds.PlayLocal( _sound, source: _source ) : Combat.HaloSounds.Play( _sound, position, source: _source );
			_playingAnimation = _animation;
		}
		_sound = null;
	}

	// Cancels a pending sound and cuts off the playing one, as Halo does when the first person weapon changes state.
	// A sound the overheating animation started plays on.
	public void Stop() {
		_sound = null;
		if ( IsOverheating( _playingAnimation ) ) return;
		_playing?.Stop();
		_playing = null;
	}

	// The first person weapon is swapped out, so its pending sound never plays.
	// The playing sound is cut off only if the graph has a put-away animation; otherwise it plays out, as in Halo.
	// Halo's per-weapon sound limit keeps those from piling up.
	public void PutAway() {
		if ( AnimationGraphs.FirstPersonSlot( _graph, "put-away" ) is not null ) Stop();
		_sound = null;
		_playing = null;
		_playingAnimation = null;
	}

	static bool IsOverheating( string animation ) =>
		animation is not null && (animation == "first-person overheating" || animation.StartsWith( "first-person overheating%" ));
}

// Animation frame counts and key frames at 30 fps, from mount://haloce/anims/<antr path>.json.
// Halo times melee from these.
public static class AnimationGraphs {
	// The foot frames are where a biped steps that foot; 0 means none, as for SecondKeyFrame.
	public readonly record struct Timing( int FrameCount, int KeyFrame, int LeftFootFrame = 0, int RightFootFrame = 0, int SecondKeyFrame = 0 ) {
		public float Seconds => FrameCount / HaloUnits.TicksPerSecond;
	}

	sealed class GraphFile {
		public List<Entry> Animations { get; set; } = new();

		// A first person graph's weapon animation types, like "reload-empty", to animation indices.
		public Dictionary<string, int> FirstPersonSlots { get; set; }
	}

	sealed class Entry {
		public int Index { get; set; } = -1;
		public string Name { get; set; }
		public int FrameCount { get; set; }
		public int KeyFrame { get; set; } = -1;
		public int SecondKeyFrame { get; set; }
		public int LeftFootFrame { get; set; }
		public int RightFootFrame { get; set; }
	}

	static readonly Dictionary<string, Dictionary<string, Timing>> Graphs = new();
	static readonly Dictionary<string, Dictionary<string, SlotEntry>> Slots = new();

	public static Timing? Find( string graph, string animation ) {
		if ( string.IsNullOrEmpty( graph ) || string.IsNullOrEmpty( animation ) ) return null;
		var timings = Load( graph );
		return timings is not null && timings.TryGetValue( animation, out var t ) ? t : null;
	}

	// The animation a first person graph plays for a weapon animation type.
	// Several types can share one, like the assault rifle's reload-empty and reload-full.
	public static Timing? FirstPersonSlot( string graph, string slot ) => FindSlot( graph, slot )?.Timing;

	public static string FirstPersonSlotAnimation( string graph, string slot ) => FindSlot( graph, slot )?.Name;

	static SlotEntry? FindSlot( string graph, string slot ) {
		if ( string.IsNullOrEmpty( graph ) || string.IsNullOrEmpty( slot ) ) return null;
		Load( graph );
		return Slots.TryGetValue( Key( graph ), out var slots ) && slots is not null && slots.TryGetValue( slot, out var entry ) ? entry : null;
	}

	readonly record struct SlotEntry( Timing Timing, string Name );

	static string Key( string graph ) {
		graph = graph.Replace( '\\', '/' );
		return graph.EndsWith( ".antr" ) ? graph : graph + ".antr";
	}

	static Dictionary<string, Timing> Load( string graph ) {
		graph = Key( graph );
		if ( Graphs.TryGetValue( graph, out var cached ) && Slots.ContainsKey( graph ) ) return cached;

		Dictionary<string, Timing> timings = null;
		Dictionary<string, SlotEntry> slots = null;
		try {
			var json = Data.HaloDataStore.ReadMount( $"anims/{graph}.json" ) ?? Data.HaloDataStore.ReadMount( $"anims/{graph[..^".antr".Length]}.json" );
			if ( !string.IsNullOrEmpty( json ) ) {
				var file = Json.Deserialize<GraphFile>( json );
				timings = new( StringComparer.OrdinalIgnoreCase );
				var byIndex = new Dictionary<int, SlotEntry>();
				foreach ( var a in file?.Animations ?? new() ) {
					if ( a?.Name is null || a.FrameCount <= 0 ) continue;
					var timing = new Timing( a.FrameCount, a.KeyFrame, Math.Max( 0, a.LeftFootFrame ), Math.Max( 0, a.RightFootFrame ), Math.Max( 0, a.SecondKeyFrame ) );
					timings.TryAdd( a.Name, timing );
					if ( a.Index >= 0 ) byIndex.TryAdd( a.Index, new SlotEntry( timing, a.Name ) );
				}

				if ( file?.FirstPersonSlots is not null ) {
					slots = new( StringComparer.OrdinalIgnoreCase );
					foreach ( var (slot, index) in file.FirstPersonSlots )
						if ( byIndex.TryGetValue( index, out var entry ) )
							slots[slot] = entry;
				}
			}
		}
		catch ( Exception e ) {
			Log.Warning( $"Couldn't read the animation index of {graph}: {e.Message}" );
		}

		Graphs[graph] = timings;
		Slots[graph] = slots;
		return timings;
	}
}
