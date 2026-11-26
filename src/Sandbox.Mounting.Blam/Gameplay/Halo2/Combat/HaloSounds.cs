using System;
using System.Threading.Tasks;
using Halo2.Data;

namespace Halo2.Combat;

// Loads, caches and plays the mount's sound events, which the mount builds on demand.
// A sound tag's event is the sound at scale 0; a looping sound's "<lsnd>__track<n>" and "__detail<n>" are at scale 1.
// Everything the game data refers to is preloaded when a match starts.
// A sound asked for while loading still plays if it's ready within a moment.
public static class HaloSounds {
	static readonly Dictionary<string, SoundEvent> Loaded = new();
	static readonly Dictionary<string, Task<SoundEvent>> Loading = new();
	static readonly HashSet<string> Missing = new();

	// Seconds a sound may start late after being asked for while still loading.
	const float MaxLateStart = 0.3f;

	// A looping sound tag plays its first track.
	static string PathOf( string soundTag ) {
		if ( string.IsNullOrEmpty( soundTag ) ) return null;
		if ( soundTag.EndsWith( ".lsnd" ) )
			return HaloResources.SoundPath( $"{HaloEffects.WithoutExtension( soundTag )}__track0" );
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
		// An event already in the library loads at once, and a finished task left here would never reload.
		if ( !task.IsCompleted ) Loading[path] = task;
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
	// source is the object it plays from, for the per-object limit.
	// own marks the local player's unit, what it holds or what it rides, whose sounds of every class MCC plays unspatialized.
	// scale runs from 0 to 1; Halo 2 plays effect, animation and most other one shots at 1.
	public static SoundHandle Play( string soundTag, Vector3 position, float volume = 1, object source = null, bool own = false, float scale = 1 ) {
		var path = PathOf( soundTag );
		if ( path is null ) return null;
		var definition = DefinitionOf( soundTag );
		if ( RemasteredOnly( definition ) ) return null;
		var loop = soundTag.EndsWith( ".lsnd" );
		var gain = 1f;
		var pitch = 1f;
		if ( !loop && !ScaleOf( TagOf( definition ), scale, out gain, out pitch ) ) return null;
		var local = own && !Spatialized( definition );
		return Begin( path, definition, position, volume * gain, pitch, local, source, loop ? TrackFadeIn( soundTag ) : 0 );
	}

	// Unpositioned, for the local player only: HUD sounds and pickups.
	public static SoundHandle PlayLocal( string soundTag, float volume = 1, object source = null, float scale = 1 ) {
		var path = PathOf( soundTag );
		if ( path is null ) return null;
		var definition = DefinitionOf( soundTag );
		if ( RemasteredOnly( definition ) || !ScaleOf( TagOf( definition ), scale, out var gain, out var pitch ) ) return null;
		return Begin( path, definition, Vector3.Zero, volume * gain, pitch, true, source, 0 );
	}

	// An impulse sound still loading plays once loaded, if that's soon enough.
	// A looping track starts only once loaded, since callers retry and deferred starts would stack copies.
	static SoundHandle Begin( string path, string definition, Vector3 position, float volume, float pitch, bool local, object source, float fadeIn ) {
		if ( TryLoaded( path, out var e ) )
			return Start( e, definition, position, volume, pitch, local, source, fadeIn );

		if ( fadeIn > 0 ) Request( path );
		else _ = PlayWhenLoaded( path, definition, position, volume, pitch, local, source );
		return null;
	}

	static async Task PlayWhenLoaded( string path, string definition, Vector3 position, float volume, float pitch, bool local, object source ) {
		var asked = RealTime.Now;
		var e = await Request( path );
		if ( e is null || RealTime.Now - asked > MaxLateStart ) return;
		Start( e, definition, position, volume, pitch, local, source, 0 );
	}

	// The change from the mount's event, which is the sound at scale 0, to the scale asked for.
	// The pitch modifier adds cents and the gain modifier decibels to the class's gain bounds, which blend by amplitude.
	// The whole sound is skipped by its skip fraction times the scaled skip modifier.
	static bool ScaleOf( SoundTagFile tag, float scale, out float gain, out float pitch ) {
		gain = 1;
		pitch = 1;
		if ( tag is null ) return true;
		scale = Math.Clamp( scale, 0, 1 );

		var skip = tag.SkipFraction * Lerp( tag.SkipFractionModifier, scale, 1 );
		if ( skip > 0 && Game.Random.Float( 0, 1 ) < skip ) return false;

		gain = Decibels( tag.GainAt( scale ) - tag.GainAt( 0 ) );
		pitch = MathF.Pow( 2, (Lerp( tag.PitchModifier, scale, 0 ) - Lerp( tag.PitchModifier, 0, 0 )) / 1200 );
		return true;
	}

	static float Lerp( float[] bounds, float t, float fallback ) => bounds is { Length: > 1 } b ? b[0] + (b[1] - b[0]) * t : fallback;

	static float Decibels( float decibels ) => MathF.Pow( 10, decibels / 20 );

	// Halo's limits on copies of a sound

	// Every Halo sound playing, tracked on Halo's channels for its copy limits.
	static readonly SoundChannels Channels = new();

	// A canonical sound tag key: no extension, forward slashes, a looping sound as its first track's loop. Null if none.
	static string DefinitionOf( string soundTag ) {
		if ( string.IsNullOrEmpty( soundTag ) ) return null;
		if ( soundTag.EndsWith( ".lsnd" ) ) soundTag = LoopingSoundOf( HaloEffects.WithoutExtension( soundTag ) )?.Tracks?.FirstOrDefault()?.Loop;
		return HaloEffects.WithoutExtension( soundTag )?.ToLowerInvariant();
	}

	// An index into SoundClasses.Names, or -1 if the mount doesn't export the tag.
	static int ClassOf( string definition ) => TagOf( definition ) is { } tag ? SoundClasses.IndexOf( tag.Class ) : -1;

	static readonly Dictionary<string, SoundTagFile> Tags = new( StringComparer.OrdinalIgnoreCase );
	static readonly Dictionary<string, LoopingSoundFile> LoopingSounds = new( StringComparer.OrdinalIgnoreCase );
	static int _tagsGeneration;

	// The mount's tags/<path>.snd!.json, or null. Read once per tag.
	static SoundTagFile TagOf( string definition ) => definition is null ? null : ReadTag( Tags, $"tags/{definition}.snd!.json", definition );

	// The mount's tags/<path>.lsnd.json, or null.
	static LoopingSoundFile LoopingSoundOf( string stem ) => stem is null ? null : ReadTag( LoopingSounds, $"tags/{stem}.lsnd.json", stem );

	static T ReadTag<T>( Dictionary<string, T> cache, string path, string key ) where T : class {
		if ( _tagsGeneration != HaloDataStore.Generation ) {
			_tagsGeneration = HaloDataStore.Generation;
			Tags.Clear();
			LoopingSounds.Clear();
		}
		if ( cache.TryGetValue( key, out var cached ) ) return cached;

		var json = HaloDataStore.ReadMount( path );
		var tag = string.IsNullOrEmpty( json ) ? null : Json.Deserialize<T>( json );
		cache[key] = tag;
		return tag;
	}

	// Null, meaning no limits, if the mount doesn't export the tag's class.
	static SoundClassLimits? LimitsOf( string definition ) => SoundClasses.Get( ClassOf( definition ) );

	// MCC's classic audio never plays a sound only its remastered audio uses, which the mount leaves out.
	static bool RemasteredOnly( string definition ) => TagOf( definition )?.Flags?.Contains( "play only in remastered mode" ) ?? false;

	// MCC plays the local player's own sounds unspatialized, unless the sound is flagged "always spatialize".
	static bool Spatialized( string definition ) => TagOf( definition )?.Flags?.Contains( "always spatialize" ) ?? false;

	// A looping sound's first track fades in by its fade in duration if flagged "fade in at start".
	static float TrackFadeIn( string soundTag ) =>
		LoopingSoundOf( HaloEffects.WithoutExtension( soundTag ) )?.Tracks?.FirstOrDefault() is { } track && track.Flags.Contains( "fade in at start" ) ? track.FadeInDuration : 0;

	// In world units: the effective distance, the sound's own or its class's. 0 if the mount doesn't export the tag.
	public static float MaximumDistance( string soundTag ) => TagOf( DefinitionOf( soundTag ) )?.MaximumDistance ?? 0;

	// The fields of the mount's snd! document this reads. Gains are dB, pitches cents, distances world units.
	// Each modifier is [at scale 0, at scale 1].
	sealed class SoundTagFile {
		public string Class { get; set; }
		public string[] Flags { get; set; } = Array.Empty<string>();
		public float MaximumDistance { get; set; }
		public float SkipFraction { get; set; }
		public float GainBase { get; set; }
		public float GainVariance { get; set; }
		public float[] GainModifier { get; set; }
		public float[] PitchModifier { get; set; }
		public float[] SkipFractionModifier { get; set; }
		public float[] ClassGainBounds { get; set; }
		public List<SoundPitchRangeFile> PitchRanges { get; set; }

		// The quietest gain the sound plays at a scale, dB: the mount's event at scale 0 is this, up to its variance.
		public float GainAt( float scale ) {
			var at0 = (ClassGainBounds?.ElementAtOrDefault( 0 ) ?? 0) + (GainModifier?.ElementAtOrDefault( 0 ) ?? 0);
			var at1 = (ClassGainBounds?.ElementAtOrDefault( 1 ) ?? 0) + (GainModifier?.ElementAtOrDefault( 1 ) ?? 0);
			var amplitude = Decibels( at0 ) + (Decibels( at1 ) - Decibels( at0 )) * scale;
			var classGain = amplitude > 0 ? Math.Clamp( 20 * MathF.Log10( amplitude ), -64, 0 ) : -64;
			return MathF.Min( 0, GainBase + MathF.Min( 0, GainVariance ) + classGain );
		}
	}

	sealed class SoundPitchRangeFile {
		public List<SoundPermutationFile> Permutations { get; set; }
	}

	sealed class SoundPermutationFile {
		public float SkipFraction { get; set; }
	}

	sealed class LoopingSoundFile {
		public List<LoopingSoundTrack> Tracks { get; set; }
	}

	sealed class LoopingSoundTrack {
		public string[] Flags { get; set; } = Array.Empty<string>();
		public float FadeInDuration { get; set; }
		public string Loop { get; set; }
	}

	sealed class HandleSound( SoundHandle handle ) : IChannelSound {
		public bool IsPlaying => handle.IsValid && !handle.IsStopped && !handle.Finished;
		public void Stop() => handle.Stop( 0 );
	}

	// Counts as playing for a moment after assignment, since it only starts when its prefab is enabled.
	sealed class ComponentSound( BaseSoundComponent sound ) : IChannelSound {
		readonly RealTimeSince _assigned = 0;

		public bool IsPlaying => sound.IsValid() && (_assigned < 0.25f || ((Component.ITemporaryEffect)sound).IsActive);
		public void Stop() {
			if ( sound.IsValid() ) sound.StopSound();
		}
	}

	// Call before enabling a prefab, such as a projectile's. Removes the looping sound tracks Halo's copy limits wouldn't
	// play and takes channels for the rest. The mount makes a track's event at scale 1 already.
	// The local player's own sounds are played unspatialized instead.
	public static void LimitLoopingSounds( GameObject prefab, object source, bool own = false ) {
		foreach ( var sound in prefab.GetComponentsInChildren<SoundPointComponent>( true ) ) {
			if ( !sound.PlayOnStart || sound.SoundEvent is null ) continue;
			var definition = DefinitionOfTrack( sound.SoundEvent.ResourcePath );

			// The component's Force2d parks the sound at a fixed world spot, so play it listener-local instead.
			if ( own && !Spatialized( definition ) ) {
				Start( sound.SoundEvent, definition, sound.WorldPosition, 1, 1, true, source, 0 );
				sound.Destroy();
				continue;
			}
			var limits = LimitsOf( definition );
			var channel = Channels.Admit( definition, limits, source, RealTime.Now );
			if ( channel < 0 ) {
				sound.Destroy();
				continue;
			}
			Channels.Assign( channel, definition, limits, source, RealTime.Now, new ComponentSound( sound ) );
		}
	}

	// A looping sound's track event, "mount://halo2/sound/sfx/x__track0.sound", maps to the sound tag it plays.
	static string DefinitionOfTrack( string resourcePath ) {
		if ( string.IsNullOrEmpty( resourcePath ) ) return null;
		var path = resourcePath.Replace( '\\', '/' );
		var root = path.IndexOf( "://" );
		if ( root >= 0 ) {
			path = path[(root + 3)..];
			var slash = path.IndexOf( '/' );
			path = slash >= 0 ? path[(slash + 1)..] : path;
		}
		var definition = HaloEffects.WithoutExtension( path )?.ToLowerInvariant();
		if ( Suffixed( definition, "__track", out var stem, out var track ) )
			return DefinitionOf( LoopingSoundOf( stem )?.Tracks?.ElementAtOrDefault( track )?.Loop );
		return definition;
	}

	// "<stem><marker><n>" split into the stem and n.
	static bool Suffixed( string definition, string marker, out string stem, out int index ) {
		stem = null;
		index = -1;
		var mark = definition?.LastIndexOf( marker ) ?? -1;
		if ( mark < 0 || !int.TryParse( definition[(mark + marker.Length)..], out index ) ) return false;
		stem = definition[..mark];
		return true;
	}

	// s&box's default reverb makes Halo audio sound like a big hall with slapback.
	// Halo's sound environments come from the mount's soundscapes, so sounds play dry unless this is on.
	[ConVar( "halo2_sound_reverb", Help = "Let s&box add its own reverb and reflections to Halo sounds.", Saved = true )]
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

	static SoundHandle Start( SoundEvent e, string definition, Vector3 position, float volume, float pitch, bool local, object source, float fadeIn ) {
		if ( Application.IsHeadless ) return null;
		var limits = LimitsOf( definition );
		var channel = Channels.Admit( definition, limits, source, RealTime.Now );
		if ( channel < 0 ) return null;

		var file = NextPermutation( e, definition );
		var handle = PlayPermutation( e, file, fadeIn );
		if ( handle is null ) return null;
		if ( !local ) handle.Position = position;
		Channels.Assign( channel, definition, limits, source, RealTime.Now, new HandleSound( handle ) );
		Dry( handle );

		// Halo 2 gives an unspatialized sound, mono or stereo, the level of one positioned in front of the listener.
		// s&box still measures a listener-local sound's distance from the origin, so it gets none.
		if ( local ) {
			handle.ListenLocal = true;
			handle.SpacialBlend = 0;
			handle.DistanceAttenuation = false;
			handle.AirAbsorption = false;
		}
		if ( volume != 1 ) handle.Volume *= volume;
		if ( pitch != 1 ) handle.Pitch *= pitch;
		return handle;
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
		foreach ( var list in game.Footsteps?.Effects.SelectMany( x => x.Sounds.Values ) ?? Enumerable.Empty<List<string>>() )
			foreach ( var s in list.Where( x => x is not null ) )
				tags.Add( s );
		foreach ( var s in game.UnitHudSounds )
			tags.Add( s.Sound );
		tags.Add( game.Globals?.Player?.BinocularsZoomInSound );
		tags.Add( game.Globals?.Player?.BinocularsZoomOutSound );
		foreach ( var (path, w) in game.Weapons ) {
			tags.Add( w.PickupSound );
			tags.Add( w.ZoomInSound );
			tags.Add( w.ZoomOutSound );
			// First person animation sounds: readies, reloads, melees.
			foreach ( var s in FirstPersonGraph.For( path )?.Sounds ?? Enumerable.Empty<string>() )
				tags.Add( s );
		}
		foreach ( var e in game.Equipment.Values )
			tags.Add( e.PickupSound );
		foreach ( var p in game.Projectiles.Values ) {
			tags.Add( p.FlybySound );
			tags.Add( p.DetonationSound );
		}
		// Damage effect sounds, such as melee impacts, and each material's melee hit and impact sweetener sounds.
		foreach ( var d in game.DamageEffects.Values )
			tags.Add( d.Sound );
		foreach ( var m in game.Globals?.Materials ?? new() ) {
			tags.Add( m?.SoundSweetenerMelee );
			tags.Add( m?.SoundSweetenerSmall );
		}
		// Effects' sound parts, such as firing sounds.
		foreach ( var part in game.Effects.Values.SelectMany( x => x.Parts ).Where( x => x.Tag?.EndsWith( ".snd!" ) ?? false ) )
			tags.Add( part.Tag );

		var paths = tags.Where( x => !RemasteredOnly( DefinitionOf( x ) ) ).Select( PathOf ).Where( x => x is not null ).Distinct().ToList();
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
