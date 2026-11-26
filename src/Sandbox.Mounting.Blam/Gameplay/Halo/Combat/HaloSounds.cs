using System;
using System.Threading.Tasks;
using Halo.Data;

namespace Halo.Combat;

// Loads, caches and plays the mount's sound events, which the mount builds on demand.
// Everything the game data refers to is preloaded when a match starts.
// A sound asked for while loading still plays if it's ready within a moment.
public static class HaloSounds {
	// A looping sound's whole first track, its loop or alternate loop alone, or the end played when a script stops it.
	public enum LoopPart { Track, Loop, Alternate, End, AlternateEnd }

	static string LoopSuffix( LoopPart part ) => part switch {
		LoopPart.Loop => "__loop",
		LoopPart.Alternate => "__alternate",
		LoopPart.End => "__end",
		LoopPart.AlternateEnd => "__alternate_end",
		_ => "",
	};

	static readonly Dictionary<string, SoundEvent> Loaded = new();
	static readonly Dictionary<string, Task<SoundEvent>> Loading = new();
	static readonly HashSet<string> Missing = new();

	// Seconds a sound may start late after being asked for while still loading.
	const float MaxLateStart = 0.3f;

	public static int LoadedCount => Loaded.Count;

	// A looping sound tag gives its first track. Null while loading or if the mount lacks it.
	public static SoundEvent Get( string soundTag ) {
		var path = PathOf( soundTag );
		if ( path is null ) return null;
		if ( TryLoaded( path, out var e ) ) return e;
		Request( path );
		return null;
	}

	// A looping sound maps to the mount's "<lsnd>__track0" event, one file that loops seamlessly.
	static string PathOf( string soundTag, LoopPart part = LoopPart.Track ) {
		if ( string.IsNullOrEmpty( soundTag ) ) return null;
		if ( soundTag.EndsWith( ".lsnd" ) )
			return HaloResources.SoundPath( $"{HaloEffects.WithoutExtension( soundTag )}__track0{LoopSuffix( part )}" );
		return HaloResources.SoundPath( soundTag );
	}

	// Joining a game resets the resource library and destroys cached events, so dead ones are dropped and reloaded.
	static bool TryLoaded( string path, out SoundEvent e ) {
		if ( !Loaded.TryGetValue( path, out e ) ) return false;
		if ( e.IsValid() ) return true;
		Loaded.Remove( path );
		e = null;
		return false;
	}

	static Task<SoundEvent> Request( string path ) {
		if ( Loading.TryGetValue( path, out var task ) ) return task;
		if ( Missing.Contains( path ) ) return Task.FromResult<SoundEvent>( null );

		task = LoadAsync( path );
		Loading[path] = task;
		return task;
	}

	static async Task<SoundEvent> LoadAsync( string path ) {
		SoundEvent e = null;
		try {
			e = await ResourceLibrary.LoadAsync<SoundEvent>( path );
		}
		catch ( Exception ex ) {
			Log.Warning( $"Couldn't load {path}: {ex.Message}" );
		}

		Loading.Remove( path );
		if ( e is null ) Missing.Add( path );
		else Loaded[path] = e;
		return e;
	}

	// Null unless it started right away: still loading, over Halo's copy limits, or skipped at this scale.
	// started is given the handle of a sound that starts late, once loaded.
	// source is the object it plays from, for the per-object limit; own unspatializes it as MCC does for its class.
	// scale runs from 0 (footsteps) to 1 (most sounds).
	// fadeIn in seconds replaces a looping sound's own fade in.
	public static SoundHandle Play( string soundTag, Vector3 position, float volume = 1, object source = null, bool own = false, float scale = 1, LoopPart part = LoopPart.Track, Action<SoundHandle> started = null, float? fadeIn = null ) {
		var path = PathOf( soundTag, part );
		if ( path is null ) return null;
		var definition = DefinitionOf( soundTag );
		if ( !ScaleOf( definition, scale, out var gain, out var pitch ) ) return null;
		var local = own && IsOwnUnspatializedClass( ClassOf( definition ) );
		return Begin( soundTag, path, definition, position, volume * gain, pitch, local, source, part, started, fadeIn );
	}

	// Unpositioned, for the local player only: HUD sounds, pickups, first person animation sounds.
	public static SoundHandle PlayLocal( string soundTag, float volume = 1, object source = null, float scale = 1, LoopPart part = LoopPart.Track, Action<SoundHandle> started = null, float? fadeIn = null ) {
		var path = PathOf( soundTag, part );
		if ( path is null ) return null;
		var definition = DefinitionOf( soundTag );
		if ( !ScaleOf( definition, scale, out var gain, out var pitch ) ) return null;
		return Begin( soundTag, path, definition, Vector3.Zero, volume * gain, pitch, true, source, part, started, fadeIn );
	}

	// An impulse sound still loading plays once loaded, if that's soon enough.
	// A looping sound starts only once loaded, since callers retry and deferred starts would stack copies.
	static SoundHandle Begin( string soundTag, string path, string definition, Vector3 position, float volume, float pitch, bool local, object source, LoopPart part, Action<SoundHandle> started, float? fadeInOverride ) {
		var loopTrack = soundTag.EndsWith( ".lsnd" );
		var fadeIn = fadeInOverride ?? (HaloDataStore.Game?.LoopTrack( soundTag ) is { } track ? LoopFadeIn( track, part ) : 0);

		if ( TryLoaded( path, out var e ) )
			return Start( e, definition, position, volume, pitch, local, source, loopTrack, fadeIn );

		if ( loopTrack ) Request( path );
		else _ = PlayWhenLoaded( path, definition, position, volume, pitch, local, source, started );
		return null;
	}

	// The whole track fades in by its fade in duration if flagged "fade in at start".
	// Switching to or from the alternate fades by the fade out duration if flagged "fade in alternate".
	static float LoopFadeIn( LoopingTrackTag track, LoopPart part ) => part switch {
		LoopPart.Track => track.Flags.Contains( "fade in at start" ) ? track.FadeInDuration : 0,
		LoopPart.Loop or LoopPart.Alternate => LoopAlternateFade( track ),
		_ => 0,
	};

	static float LoopAlternateFade( LoopingTrackTag track ) => track.Flags.Contains( "fade in alternate" ) ? track.FadeOutDuration : 0;

	// How long a looping sound's track crossfades to or from its alternate.
	public static float LoopAlternateFade( string soundTag ) => HaloDataStore.Game?.LoopTrack( soundTag ) is { } track ? LoopAlternateFade( track ) : 0;

	// Loads a looping sound part ahead so it can start at once.
	public static void Preload( string soundTag, LoopPart part ) {
		var path = PathOf( soundTag, part );
		if ( path is not null && !TryLoaded( path, out _ ) ) Request( path );
	}

	public static bool HasAlternateLoop( string soundTag ) => !string.IsNullOrEmpty( HaloDataStore.Game?.LoopTrack( soundTag )?.AlternateLoop );

	// The end a script stop plays: the alternate end while on the alternate, if any, otherwise the end. Null if none.
	public static LoopPart? LoopEnd( string soundTag, bool alternate ) => HaloDataStore.Game?.LoopTrack( soundTag ) switch {
		{ AlternateEnd: { Length: > 0 } } when alternate => LoopPart.AlternateEnd,
		{ End: { Length: > 0 } } => LoopPart.End,
		_ => null,
	};

	// Flags such as "not a loop" or "stops music".
	public static bool HasLoopFlag( string soundTag, string flag ) => LoopingSoundOf( soundTag )?.Flags?.Contains( flag ) == true;

	// Music when one of its tracks loops a music class sound.
	public static bool IsMusic( string soundTag ) =>
		HaloDataStore.Game?.LoopTracks( soundTag ) is { } tracks
		&& tracks.Any( t => ClassOf( DefinitionOf( t.Loop ) ) == SoundClasses.IndexOf( "music" ) );

	// Seconds of start sound before a track's loop begins. 0 if unknown.
	public static float LoopStartLength( string soundTag ) => TrackTimingOf( soundTag, LoopPart.Track )?.Start ?? 0;

	// Seconds until the start sound or current loop variation ends, which Halo's stop and alternate switch wait for.
	// 0 if unknown.
	public static float UntilLoopBoundary( string soundTag, LoopPart part, float elapsed ) {
		if ( TrackTimingOf( soundTag, part ) is not { } timing ) return 0;
		if ( elapsed < timing.Start ) return timing.Start - elapsed;

		var total = timing.Loop.Sum();
		if ( total <= 0 ) return 0;
		var t = elapsed - timing.Start;
		if ( t >= total && !timing.Loops ) return 0;
		t %= total;

		var end = 0f;
		foreach ( var seconds in timing.Loop ) {
			end += seconds;
			if ( end > t ) return end - t;
		}
		return 0;
	}

	static readonly Dictionary<string, TrackTiming> TrackTimings = new( StringComparer.OrdinalIgnoreCase );

	// Read from the mount's <lsnd>__track0<part>.json.
	static TrackTiming TrackTimingOf( string soundTag, LoopPart part ) {
		if ( string.IsNullOrEmpty( soundTag ) || !soundTag.EndsWith( ".lsnd" ) ) return null;
		var path = $"{HaloEffects.WithoutExtension( soundTag )}__track0{LoopSuffix( part )}.json";
		if ( TrackTimings.TryGetValue( path, out var cached ) ) return cached;

		TrackTiming timing = null;
		var json = HaloDataStore.ReadMount( path );
		if ( !string.IsNullOrEmpty( json ) ) {
			try {
				timing = Json.Deserialize<TrackTiming>( json );
			}
			catch ( Exception ex ) {
				Log.Warning( $"Couldn't read {path}: {ex.Message}" );
			}
		}
		return TrackTimings[path] = timing;
	}

	sealed class TrackTiming {
		public float Start { get; set; }
		public List<float> Loop { get; set; } = new();
		public bool Loops { get; set; }
	}

	// The fade out duration if flagged "fade out at stop", otherwise 0.
	public static float LoopFadeOut( string soundTag ) =>
		HaloDataStore.Game?.LoopTrack( soundTag ) is { } track && track.Flags.Contains( "fade out at stop" ) ? track.FadeOutDuration : 0;

	static async Task PlayWhenLoaded( string path, string definition, Vector3 position, float volume, float pitch, bool local, object source, Action<SoundHandle> started ) {
		var asked = RealTime.Now;
		var e = await Request( path );
		if ( e is null || RealTime.Now - asked > MaxLateStart ) return;
		if ( Start( e, definition, position, volume, pitch, local, source, false, 0 ) is { } handle ) started?.Invoke( handle );
	}

	// Gain and pitch from the tag's scale modifiers, lerped between "at zero" and "at one", on top of the event's own.
	// False when it doesn't play: a zero gain at scale 0, or skipped by its scaled skip fraction.
	static bool ScaleOf( string definition, float scale, out float gain, out float pitch ) {
		gain = 1;
		pitch = 1;
		if ( TagOf( definition ) is not { } tag ) return true;

		scale = Math.Clamp( scale, 0, 1 );
		if ( scale == 0 && tag.GainModifierAtZero == 0 ) return false;

		var skip = tag.SkipFraction * ScaleValue( tag.SkipFractionModifierAtZero, tag.SkipFractionModifierAtOne, scale );
		if ( skip > 0 && Game.Random.Float( 0, 1 ) <= skip ) return false;

		gain = ScaleValue( tag.GainModifierAtZero, tag.GainModifierAtOne, scale );
		pitch = ScaleValue( tag.PitchModifierAtZero, tag.PitchModifierAtOne, scale );
		if ( pitch <= 0 ) pitch = 1;
		return true;
	}

	static float ScaleValue( float atZero, float atOne, float scale ) => (atOne - atZero) * scale + atZero;

	// Halo's limits on copies of a sound

	// Every Halo sound playing, tracked on Halo's channels for its copy limits.
	static readonly SoundChannels Channels = new();

	// A canonical sound tag key: no extension, forward slashes, a looping sound as its loop's tag. Null if none.
	static string DefinitionOf( string soundTag ) {
		if ( string.IsNullOrEmpty( soundTag ) ) return null;
		if ( soundTag.EndsWith( ".lsnd" ) )
			soundTag = HaloDataStore.Game?.LoopSound( soundTag );
		return HaloEffects.WithoutExtension( soundTag )?.ToLowerInvariant();
	}

	// An index into SoundClasses.Names, or -1 if the mount doesn't export the tag.
	static int ClassOf( string definition ) => TagOf( definition ) is { } tag ? SoundClasses.IndexOf( tag.Class ) : -1;

	static readonly Dictionary<string, SoundTagFile> Tags = new( StringComparer.OrdinalIgnoreCase );

	// The mount's tags/<path>.snd!.json, or null. Read once per tag.
	static SoundTagFile TagOf( string definition ) {
		if ( definition is null ) return null;
		if ( Tags.TryGetValue( definition, out var cached ) ) return cached;

		SoundTagFile tag = null;
		var json = HaloDataStore.ReadMount( $"tags/{definition}.snd!.json" );
		if ( !string.IsNullOrEmpty( json ) ) {
			try {
				tag = Json.Deserialize<SoundTagFile>( json );
			}
			catch ( Exception ex ) {
				Log.Warning( $"Couldn't read the sound tag {definition}: {ex.Message}" );
			}
		}

		Tags[definition] = tag;
		return tag;
	}

	// Null, meaning no limits, if the mount doesn't export the tag's class.
	static SoundClassLimits? LimitsOf( string definition ) => SoundClasses.Get( ClassOf( definition ) );

	// MCC plays the local player's own weapon and footstep sounds unspatialized: at the listener, no panning or falloff.
	// "Own" means the effect's owner is the local player's unit or its weapon, plus first person animation sounds.
	// Other classes stay at their object like everyone else's sounds.
	public static bool OwnSoundUnspatialized( string soundTag ) => IsOwnUnspatializedClass( ClassOf( DefinitionOf( soundTag ) ) );

	// Weapon fire through weapon idle (4-10), and unit footsteps (18).
	static bool IsOwnUnspatializedClass( int soundClass ) => soundClass is >= 4 and <= 10 or 18;

	// In world units. 0 if the tag leaves it to its class or the mount doesn't export it.
	public static float MaximumDistance( string soundTag ) => TagOf( DefinitionOf( soundTag ) )?.MaximumDistance ?? 0;

	// 0 if unknown.
	public static float LongestPermutationMilliseconds( string soundTag ) => TagOf( DefinitionOf( soundTag ) )?.LongestPermutationLength ?? 0;

	public static bool IsScriptedDialog( string soundTag ) => SoundClasses.IsScriptedDialog( ClassOf( DefinitionOf( soundTag ) ) );

	sealed class SoundTagFile {
		public string Class { get; set; }
		public float MaximumDistance { get; set; }
		public float LongestPermutationLength { get; set; }
		public float SkipFraction { get; set; }
		public float SkipFractionModifierAtZero { get; set; } = 1;
		public float SkipFractionModifierAtOne { get; set; } = 1;
		public float GainModifierAtZero { get; set; } = 1;
		public float GainModifierAtOne { get; set; } = 1;
		public float PitchModifierAtZero { get; set; } = 1;
		public float PitchModifierAtOne { get; set; } = 1;
		public string Encoding { get; set; }
		public List<SoundPitchRangeFile> PitchRanges { get; set; }
	}

	sealed class SoundPitchRangeFile {
		public List<SoundPermutationFile> Permutations { get; set; }
	}

	sealed class SoundPermutationFile {
		public float SkipFraction { get; set; }

		// Mouth aperture at 30 samples a second, 0 shut to 255 wide open.
		public byte[] MouthData { get; set; }
	}

	// By the permutation's index in the event. Speech sounds have a single pitch range.
	static byte[] MouthData( string definition, int permutation ) =>
		TagOf( definition )?.PitchRanges is { Count: 1 } ranges ? ranges[0].Permutations?.ElementAtOrDefault( permutation )?.MouthData : null;

	const int MouthSamplesPerSecond = 30;

	// 0 to 1 for the speech line an object is saying, holding the last sample past the end.
	// Null while it says nothing with mouth data.
	public static float? MouthAperture( object source ) => Channels.SpeechOf( source ) is HandleSound { Mouth: { Length: > 0 } mouth } speech
		? mouth[Math.Clamp( (int)(speech.Time * MouthSamplesPerSecond), 0, mouth.Length - 1 )] / 255f
		: null;

	static float ListenerDistanceSquared( Vector3 position ) {
		var wu = HaloUnits.ToWorldUnits( position.Distance( Sound.Listener.Position ) );
		return wu * wu;
	}

	sealed class HandleSound( SoundHandle handle, bool local, byte[] mouth ) : IChannelSound {
		public byte[] Mouth => mouth;
		public float Time => handle.Time;
		public bool IsPlaying => handle.IsValid && !handle.IsStopped && !handle.Finished;
		public float DistanceSquared => local ? 0 : ListenerDistanceSquared( handle.Position );
		public void Stop() => handle.Stop( 0 );
	}

	// Counts as playing for a moment after assignment, since it only starts when its effect is enabled.
	sealed class ComponentSound( BaseSoundComponent sound ) : IChannelSound {
		readonly RealTimeSince _assigned = 0;

		public bool IsPlaying => sound.IsValid() && (_assigned < 0.25f || ((Component.ITemporaryEffect)sound).IsActive);
		public float DistanceSquared => !sound.IsValid() || sound.Force2d ? 0 : ListenerDistanceSquared( sound.WorldPosition );
		public void Stop() {
			if ( sound.IsValid() ) sound.StopSound();
		}
	}

	// Call before enabling the effect. Removes sounds Halo's copy limits wouldn't play and takes channels for the rest.
	// The local player's own sounds in unspatialized classes are played 2D instead.
	public static void LimitEffectSounds( GameObject effect, object source, bool own = false ) {
		if ( !effect.IsValid() ) return;
		foreach ( var sound in effect.GetComponentsInChildren<SoundPointComponent>( true ) ) {
			if ( !sound.PlayOnStart || sound.SoundEvent is null ) continue;

			var definition = DefinitionOfEvent( sound.SoundEvent.ResourcePath );
			// The component's Force2d parks the sound at a fixed world spot, so play it listener-local instead.
			if ( own && IsOwnUnspatializedClass( ClassOf( definition ) ) ) {
				Start( sound.SoundEvent, definition, sound.WorldPosition, 1, 1, true, source, false, 0 );
				sound.Destroy();
				continue;
			}
			var limits = LimitsOf( definition );
			var distance = sound.Force2d ? 0 : ListenerDistanceSquared( sound.WorldPosition );
			// An attachment's looping sound, like a plasma grenade's hum, is admitted by MCC's looping track rule.
			var loopTrack = sound.GameObject.Tags.Has( "halo_looping_sound" );
			var channel = Channels.Admit( definition, limits, source, distance, loopTrack, RealTime.Now );
			if ( channel < 0 ) {
				sound.Destroy();
				continue;
			}
			Channels.Assign( channel, definition, limits, source, RealTime.Now, new ComponentSound( sound ) );
			SoundClassGains.Route( sound, ClassOf( definition ) );
		}
	}

	// The class of a mount sound event, by resource path.
	public static int ClassOfEvent( string resourcePath ) => ClassOf( DefinitionOfEvent( resourcePath ) );

	// "mount://haloce/sound/sfx/x.sound" becomes "sound/sfx/x". A track event maps to the sound tag it loops.
	static string DefinitionOfEvent( string resourcePath ) {
		if ( string.IsNullOrEmpty( resourcePath ) ) return null;
		var path = resourcePath.Replace( '\\', '/' );
		var root = path.IndexOf( "://" );
		if ( root >= 0 ) {
			path = path[(root + 3)..];
			var slash = path.IndexOf( '/' );
			path = slash >= 0 ? path[(slash + 1)..] : path;
		}
		var definition = HaloEffects.WithoutExtension( path )?.ToLowerInvariant();
		return TrackLoop( definition ) ?? definition;
	}

	static readonly Dictionary<string, LoopingSoundFile> LoopingSoundFiles = new( StringComparer.OrdinalIgnoreCase );

	// The mount's tags/<lsnd>.lsnd.json, or null.
	static LoopingSoundFile LoopingSoundOf( string soundTag ) {
		var path = HaloEffects.WithoutExtension( soundTag );
		if ( string.IsNullOrEmpty( path ) ) return null;
		if ( LoopingSoundFiles.TryGetValue( path, out var cached ) ) return cached;

		LoopingSoundFile file = null;
		var json = HaloDataStore.ReadMount( $"tags/{path}.lsnd.json" );
		if ( !string.IsNullOrEmpty( json ) ) {
			try {
				file = Json.Deserialize<LoopingSoundFile>( json );
			}
			catch ( Exception ex ) {
				Log.Warning( $"Couldn't read the looping sound {path}: {ex.Message}" );
			}
		}
		return LoopingSoundFiles[path] = file;
	}

	static readonly Dictionary<string, string> TrackLoops = new( StringComparer.OrdinalIgnoreCase );

	// The sound tag a "<lsnd>__track<n>" event loops, or null for any other name.
	static string TrackLoop( string definition ) {
		var mark = definition?.LastIndexOf( "__track" ) ?? -1;
		if ( mark < 0 || !int.TryParse( definition[(mark + 7)..], out var track ) ) return null;
		if ( TrackLoops.TryGetValue( definition, out var cached ) ) return cached;

		string loop = null;
		var json = HaloDataStore.ReadMount( $"tags/{definition[..mark]}.lsnd.json" );
		if ( !string.IsNullOrEmpty( json ) ) {
			try {
				var tracks = Json.Deserialize<LoopingSoundFile>( json )?.Tracks;
				if ( tracks is not null && track < tracks.Count ) loop = DefinitionOf( tracks[track]?.Loop );
			}
			catch ( Exception ex ) {
				Log.Warning( $"Couldn't read the looping sound {definition[..mark]}: {ex.Message}" );
			}
		}

		TrackLoops[definition] = loop;
		return loop;
	}

	sealed class LoopingSoundFile {
		public string[] Flags { get; set; }
		public List<LoopingSoundTrack> Tracks { get; set; }
	}

	sealed class LoopingSoundTrack {
		public string Loop { get; set; }
	}

	// s&box's default reverb makes Halo audio sound like a big hall with slapback.
	// Halo's sound environments come from the mount's soundscapes, so sounds play dry unless this is on.
	[ConVar( "halo_sound_reverb", Help = "Let s&box add its own reverb and reflections to Halo sounds.", Saved = true )]
	public static bool EngineReverb { get; set; } = false;

	// Applies EngineReverb to a playing sound.
	public static void Dry( SoundHandle handle ) {
		if ( handle is null || EngineReverb ) return;
		handle.ReverbEnabled = false;
		handle.Reverb = 0;
	}

	// Applies EngineReverb to the sound components of a spawned prefab.
	public static void Dry( GameObject go ) {
		if ( !go.IsValid() || EngineReverb ) return;
		foreach ( var sound in go.GetComponentsInChildren<BaseSoundComponent>( true ) ) {
			sound.ReverbOverride = true;
			sound.ReverbEnabled = false;
		}
	}

	static SoundHandle Start( SoundEvent e, string definition, Vector3 position, float volume, float pitch, bool local, object source, bool loopTrack, float fadeIn ) {
		if ( Application.IsHeadless ) return null;
		var limits = LimitsOf( definition );
		var channel = Channels.Admit( definition, limits, source, local ? 0 : ListenerDistanceSquared( position ), loopTrack, RealTime.Now );
		if ( channel < 0 ) return null;

		var file = NextPermutation( e, definition );
		var handle = PlayPermutation( e, file, fadeIn );
		if ( handle is null ) return null;
		if ( !local ) handle.Position = position;
		var mouth = limits?.Speech == true ? MouthData( definition, e.Sounds.IndexOf( file ) ) : null;
		Channels.Assign( channel, definition, limits, source, RealTime.Now, new HandleSound( handle, local, mouth ) );
		SoundClassGains.Route( handle, ClassOf( definition ) );
		Dry( handle );

		if ( local ) {
			handle.ListenLocal = true;
			handle.SpacialBlend = 0;
			volume *= UnspatializedLevel( e, definition );
		}
		if ( volume != 1 ) handle.Volume *= volume;
		if ( pitch != 1 ) handle.Pitch *= pitch;
		return handle;
	}

	// MCC's unspatialized level relative to a positioned sound: half for mono, even for stereo.
	// Channels come from the sample, or the tag's encoding until the sample loads.
	static float UnspatializedLevel( SoundEvent e, string definition ) {
		var file = e.Sounds?.FirstOrDefault();
		var stereo = file is not null && file.IsLoaded ? file.Channels > 1 : TagOf( definition )?.Encoding == "stereo";
		return stereo ? 1 : 0.5f;
	}

	// Halo's choice of permutation

	// Permutations played since the event last ran through them all.
	sealed class PermutationHistory {
		public ulong Played;
		public int Previous = -1;
	}

	static readonly Dictionary<SoundEvent, PermutationHistory> Histories = new();

	// How many unplayed permutations Halo tries against their skip fractions before playing one anyway.
	const int MaxSkippedPermutations = 16;

	// Walks from a random permutation to the first unplayed one, so nothing repeats until all have played.
	// The last one played doesn't start the next round. An unplayed one can still be skipped by its skip fraction.
	static SoundFile NextPermutation( SoundEvent e, string definition ) {
		var count = e.Sounds?.Count ?? 0;
		if ( count <= 1 ) return e.Sounds?.FirstOrDefault();

		if ( !Histories.TryGetValue( e, out var history ) )
			Histories[e] = history = new PermutationHistory();

		var permutations = TagOf( definition )?.PitchRanges is { Count: 1 } ranges ? ranges[0].Permutations : null;
		var all = count >= 64 ? ulong.MaxValue : (1UL << count) - 1;
		var index = Game.Random.Int( 0, count - 1 );
		var attempts = 0;
		while ( true ) {
			if ( (~history.Played & all) == 0 )
				history.Played = history.Previous >= 0 ? 1UL << history.Previous : 0;

			var bit = 1UL << index;
			if ( (history.Played & bit) == 0 ) {
				history.Played |= bit;
				if ( attempts++ == MaxSkippedPermutations ) break;
				var skip = index < permutations?.Count ? permutations[index].SkipFraction : 0;
				if ( Game.Random.Float( 0, 1 ) >= skip ) break;
			}
			index = (index + 1) % count;
		}

		history.Previous = index;
		return e.Sounds[index];
	}

	// Plays a chosen file with the event's own settings.
	static SoundHandle PlayPermutation( SoundEvent e, SoundFile file, float fadeIn ) {
		if ( file is null ) return null;
		var handle = Sound.PlayFile( file, e.Volume.GetValue(), e.Pitch.GetValue(), 0, fadeIn );
		if ( handle is null || !handle.IsValid ) return null;
		handle.Distance = e.Distance;
		handle.Falloff = e.Falloff;
		handle.TargetMixer = e.DefaultMixer.Get();
		handle.DistanceAttenuation = e.DistanceAttenuation;
		handle.AirAbsorption = e.AirAbsorption;
		handle.OcclusionEnabled = e.OcclusionEnabled;
		handle.ReverbEnabled = e.ReverbEnabled;
		return handle;
	}

	// Loads every sound the game data uses, a few at a time so decoding doesn't stall a frame.
	public static async Task PreloadAll() {
		var game = HaloDataStore.Game;
		if ( game is null ) return;

		var tags = new HashSet<string>();
		foreach ( var effect in game.Effects.Values )
			foreach ( var part in effect.Parts.Where( x => x.IsSound ) )
				tags.Add( part.Tag );
		foreach ( var list in game.Footsteps.Values )
			foreach ( var s in list.Where( x => x is not null ) )
				tags.Add( s );
		foreach ( var s in game.UnitHudSounds )
			tags.Add( s.Sound );
		foreach ( var w in game.Weapons.Values ) {
			tags.Add( w.PickupSound );
			tags.Add( w.ZoomInSound );
			tags.Add( w.ZoomOutSound );
			// First person animation sounds: readies, reloads, melees.
			foreach ( var s in Halo.AnimationSounds.All( w.FirstPersonAnimations ) )
				tags.Add( s );
		}
		foreach ( var biped in game.Bipeds.Values )
			foreach ( var s in Halo.AnimationSounds.All( biped.AnimationGraph ) )
				tags.Add( s );
		foreach ( var e in game.Equipment.Values )
			tags.Add( e.PickupSound );
		foreach ( var p in game.Projectiles.Values )
			tags.Add( p.FlybySound );
		// Damage effect sounds, such as melee impacts, and each material's melee hit sound.
		foreach ( var d in game.DamageEffects.Values )
			tags.Add( d.Sound );
		foreach ( var m in game.Globals?.Materials ?? new() )
			tags.Add( m?.MeleeHitSound );

		var paths = tags.Select( x => PathOf( x ) ).Where( x => x is not null ).Distinct().ToList();
		var started = RealTime.Now;

		// The sounds you hear first go first.
		paths = paths.OrderBy( x => x.Contains( "footsteps" ) || x.Contains( "/ui/" ) || x.Contains( "weapons/" ) ? 0 : 1 ).ToList();

		const int parallel = 4;
		for ( var i = 0; i < paths.Count; i += parallel ) {
			// Task.WhenAll isn't on the s&box whitelist.
			var batch = paths.Skip( i ).Take( parallel ).Where( x => !TryLoaded( x, out _ ) ).Select( Request ).ToList();
			foreach ( var task in batch )
				await task;
		}

		Log.Info( $"Halo sounds: {Loaded.Count} loaded, {Missing.Count} missing, in {RealTime.Now - started:0.0}s" );
	}
}
