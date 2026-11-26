using Blam.Halo2;
using Blam.Halo2.Tags;
using CeLoopingSoundDetail = global::Blam.Halo1.Tags.LoopingSoundDetail;
using DecodedSound = Sandbox.Mounting.Blam.Halo1.DecodedSound;
using DecodedSoundLoader = Sandbox.Mounting.Blam.Halo1.DecodedSoundLoader;
using LoopingDetailResource = Sandbox.Mounting.Blam.Halo1.LoopingDetailResource;
using LoopingSoundResources = Sandbox.Mounting.Blam.Halo1.LoopingSoundResources;
using LoopingTrackResource = Sandbox.Mounting.Blam.Halo1.LoopingTrackResource;
using LoopTrackFileLoader = Sandbox.Mounting.Blam.Halo1.LoopTrackFileLoader;
using PcmClip = global::Blam.Halo1.PcmClip;
using PermutationFileLoader = Sandbox.Mounting.Blam.Halo1.PermutationFileLoader;
using SoundEventLoader = Sandbox.Mounting.Blam.Halo1.SoundEventLoader;
using SoundscapeLoader = Sandbox.Mounting.Blam.Halo1.SoundscapeLoader;

namespace Sandbox.Mounting.Blam.Halo2;

/// <summary>
/// Halo 2's classic sounds as s&amp;box resources, the way the Halo CE mount has its own: every <c>snd!</c> tag becomes a
/// sound event, <c>mount://halo2/&lt;tag path&gt;.sound</c>, that picks one of its permutations at random, and each
/// permutation a sound file, <c>&lt;tag path&gt;.vsnd</c> for the first and <c>&lt;tag path&gt;_&lt;n&gt;.vsnd</c> for the rest.
/// Looping sounds (<c>lsnd</c>) get the Halo CE mount's resources for them (see <see cref="RegisterLoopingSound"/>).
/// </summary>
/// <remarks>
/// Samples come from <c>sounds_*.dat</c> (<see cref="SoundSamples"/>), decoded on a worker thread when first loaded.
/// Sounds only MCC's remastered audio plays are left out, as are the two WMA main menu themes. Dialog is English.
/// </remarks>
sealed class Halo2Sounds : IDisposable
{
	const SoundLanguage Language = SoundLanguage.English;

	readonly SoundSamples _samples;
	readonly Dictionary<string, bool> _sampleFiles = new( StringComparer.OrdinalIgnoreCase );
	readonly HashSet<string> _registered = new( StringComparer.OrdinalIgnoreCase );
	readonly Dictionary<string, LoopableSound> _sounds = new( StringComparer.OrdinalIgnoreCase );

	// Each sound tag's one-shot event, for aliases.
	readonly Dictionary<string, SoundEventLoader> _events = new( StringComparer.OrdinalIgnoreCase );
	readonly Dictionary<string, LoopingSoundResources> _loopingSounds = new( StringComparer.OrdinalIgnoreCase );
	readonly HashSet<string> _registeredLoopingSounds = new( StringComparer.OrdinalIgnoreCase );
	readonly Dictionary<(string Start, string Loop, float Rate), LoopTrackFileLoader> _trackFiles = new();

	/// <summary>
	/// Sounds some looping sound plays as its loop, whose files loop seamlessly.
	/// </summary>
	readonly HashSet<string> _looped = new( StringComparer.OrdinalIgnoreCase );

	/// <summary>
	/// Resource names in use, in case a tag's name collides with another tag's permutation name.
	/// </summary>
	readonly HashSet<string> _fileNames = new( StringComparer.OrdinalIgnoreCase );

	Halo2Sounds( SoundSamples samples ) => _samples = samples;

	/// <summary>
	/// Opens the sample files beside the maps, or returns null if they're missing.
	/// </summary>
	public static Halo2Sounds Open( string mapsDirectory, Sandbox.Diagnostics.Logger log )
	{
		if ( !File.Exists( System.IO.Path.Combine( mapsDirectory, "sounds_neutral.dat" ) ) )
		{
			log.Warning( $"Couldn't find sounds_neutral.dat in {mapsDirectory}, sounds won't be available" );
			return null;
		}

		return new Halo2Sounds( new SoundSamples( mapsDirectory ) );
	}

	/// <summary>
	/// Full path to load a sound tag's sound event with.
	/// </summary>
	public static string EventPath( string tagPath ) => $"mount://halo2/{Halo2Mount.ResourcePath( tagPath )}.sound";

	/// <summary>
	/// Registers the sound and looping sound tags a map holds that an earlier map hasn't registered.
	/// </summary>
	public void Register( MountContext context, CacheFile cache, Sandbox.Diagnostics.Logger log )
	{
		try
		{
			foreach ( var loop in LoopingSoundTag.FindLoopedSounds( cache ) )
				_looped.Add( loop.Path );
		}
		catch ( Exception e )
		{
			log.Warning( e, $"{cache.Name}: couldn't read the looping sounds: {e.Message}" );
		}

		var classes = SoundClassesTag.ForCache( cache );
		foreach ( var tag in cache.Tags )
		{
			if ( tag.Class != SoundTag.Class || tag.Cache != cache || string.IsNullOrEmpty( tag.Path ) || !_registered.Add( tag.Path ) )
				continue;

			try
			{
				Register( context, tag, classes );
			}
			catch ( Exception e )
			{
				log.Warning( e, $"Skipping sound {tag.Path}: {e.Message}" );
			}
		}

		foreach ( var tag in cache.Tags )
		{
			if ( tag.Class != LoopingSoundTag.Class || tag.Cache != cache || tag.Address == 0 || string.IsNullOrEmpty( tag.Path ) || !_registeredLoopingSounds.Add( tag.Path ) )
				continue;

			try
			{
				RegisterLoopingSound( context, new LoopingSoundTag( tag ) );
			}
			catch ( Exception e )
			{
				log.Warning( e, $"Skipping looping sound {tag.Path}: {e.Message}" );
			}
		}
	}

	void Register( MountContext context, TagEntry tag, SoundClassesTag classes )
	{
		var sound = new SoundTag( tag, Language );
		if ( sound.IsRemasteredOnly || !SoundSamples.CanDecode( sound.Compression, sound.Channels ) )
			return;

		var soundClass = classes?.Get( sound.SoundClass );
		var fileName = SoundSamples.FileName( sound, soundClass?.Multilingual ?? false, Language );
		if ( fileName is null || !SampleFileExists( fileName ) )
			return;

		var basePath = Halo2Mount.ResourcePath( tag.Path );
		var fileIndex = 0;
		var rangeFiles = new List<List<DecodedSoundLoader>>();
		var skipFractions = new List<List<float>>();
		foreach ( var range in sound.PitchRanges )
		{
			var files = new List<DecodedSoundLoader>();
			var skips = new List<float>();
			foreach ( var permutation in range.Permutations )
			{
				var name = fileIndex == 0 ? basePath : $"{basePath}_{fileIndex}";
				fileIndex++;
				if ( permutation.Chunks.Count == 0 || !_fileNames.Add( name ) )
					continue;

				var loader = new Halo2SoundFileLoader( this, tag.Path, _samples, fileName, sound.Compression, sound.Channels, sound.PlaybackSampleRate, permutation );
				context.Add( ResourceType.Sound, name, loader );
				files.Add( loader );
				skips.Add( permutation.SkipFraction );
			}

			rangeFiles.Add( files );
			skipFractions.Add( skips );
		}

		// As Halo 2 plays a sound: distances from the playback
		// parameters or the sound class (snd! flags 0x1000/0x2000); gain = gain base + r * variance (r in [0, 1)) + the
		// sound class's gain at the scale (its gain bounds are the gain at scale 0 and 1) + the permutation's gain (baked
		// into the files), at most 0 dB; pitch = random pitch + the scale's pitch modifier, from the first pitch range
		// whose maximum gain bounds hold it, at 2^((pitch - natural) / 1200). The event is the sound played without a
		// scale (0), as effects and scripts start most sounds.
		var playback = sound.Playback;
		var (minimumDistance, maximumDistance) = sound.GetDistances( soundClass );
		var playing = sound.ChoosePitchRange( sound.Scale.PitchAt( 0 ) + (playback.RandomPitchMin + playback.RandomPitchMax) * 0.5f, r => rangeFiles[r].Count > 0 );
		if ( playing < 0 )
			return;

		var playingRange = sound.PitchRanges[playing];

		// The variance can be negative (gulls: -3 dB down to -33 dB).
		(float Gain, float MaxGain, float MinRate, float MaxRate) At( float scale )
		{
			var classGain = sound.ClassGainAt( soundClass, scale );
			var pitch = sound.Scale.PitchAt( scale );
			return (Decibels( MathF.Min( 0, playback.GainBase + MathF.Min( 0, playback.GainVariance ) + classGain ) ),
				Decibels( MathF.Min( 0, playback.GainBase + MathF.Max( 0, playback.GainVariance ) + classGain ) ),
				playingRange.RateAt( pitch + playback.RandomPitchMin ), playingRange.RateAt( pitch + playback.RandomPitchMax ));
		}

		var (gain, maxGain, minRate, maxRate) = At( 0 );
		var soundEvent = new SoundEventLoader( rangeFiles[playing], skipFractions[playing], minRate, maxRate, minimumDistance, maximumDistance, gain, maxGain );
		context.Add( ResourceType.GameResource, $"{basePath}.sound", soundEvent );
		_events[tag.Path] = soundEvent;

		// What looping sounds play it with. MCC plays sound scenery and cluster background sounds at scale 1 (live:
		// every looping sound instance on Zanzibar has scale 1.0), and details go through the one-shot path at their
		// looping sound's scale; tracks take the track's gain instead of the gain base and variance, and the random
		// pitch's lower bound (live: zanzibar\loop plays at -400 cents, -24 dB = the track gain).
		var one = At( 1 );
		var skip = Math.Clamp( playback.SkipFraction * sound.Scale.SkipMax, 0, 0.95f );
		var loopEvent = new SoundEventLoader( rangeFiles[playing], skipFractions[playing], one.MinRate, one.MaxRate, minimumDistance, maximumDistance, one.Gain, one.MaxGain );
		_sounds[tag.Path] = new LoopableSound( loopEvent, sound.ClassGainAt( soundClass, 1 ), one.MinRate, skip );

		// Sounds with several pitch ranges (vehicle engines) also get an event per range, <tag>__pitch<n>.sound, at the
		// range's natural pitch, for a game to cross-fade between as its pitch changes.
		if ( sound.PitchRanges.Count > 1 )
		{
			for ( var r = 0; r < sound.PitchRanges.Count; r++ )
			{
				if ( rangeFiles[r].Count == 0 )
					continue;

				var range = sound.PitchRanges[r];
				context.Add( ResourceType.GameResource, $"{basePath}__pitch{r}.sound", new SoundEventLoader( rangeFiles[r], skipFractions[r],
					range.RateAt( range.NaturalPitch + playback.RandomPitchMin ), range.RateAt( range.NaturalPitch + playback.RandomPitchMax ),
					minimumDistance, maximumDistance, gain, maxGain ) );
			}
		}
	}

	/// <summary>
	/// A sound tag's event and what looping sounds need to play it their way.
	/// </summary>
	/// <param name="Event">The sound as a one-shot at scale 1 (a looping sound's detail): its files, distances, gain range
	/// and random pitch. Not registered as a resource itself.</param>
	/// <param name="ClassGain1">The sound class's gain (dB) at scale 1, with the sound's scale modifier.</param>
	/// <param name="TrackRate">Playback rate as a looping sound track (scale 1, random pitch's lower bound).</param>
	/// <param name="Skip">Chance the whole sound is dropped when started at scale 1 (playback skip fraction times the
	/// scale's skip modifier).</param>
	sealed record LoopableSound( SoundEventLoader Event, float ClassGain1, float TrackRate, float Skip );

	/// <summary>
	/// Registers what a level needs to play a looping sound, named after the tag, as the Halo CE mount does:
	/// <list type="bullet">
	/// <item><c>&lt;tag&gt;__track&lt;n&gt;.sound</c>: a track at the track's gain plus its loop's class gain (scale 1), the
	/// loop's lowest random pitch and its distances. One sound file: the start ("in") sound, then a sequence of the
	/// loop's permutations that repeats.</item>
	/// <item><c>&lt;tag&gt;__detail&lt;n&gt;.sound</c>: a detail sound as a one-shot at scale 1.</item>
	/// <item><c>&lt;tag&gt;.sndscape</c>: all of it as a soundscape, for cluster background sounds.</item>
	/// </list>
	/// Remastered-only looping sounds are left out (the classic one is the tag levels reference; its remastered twin
	/// is at +0x0). Details flagged for the alternate loop only play once a script switches to it, so they're left
	/// out, as are end sounds and alternate loops (placed looping sounds never stop or switch).
	/// </summary>
	void RegisterLoopingSound( MountContext context, LoopingSoundTag lsnd )
	{
		if ( lsnd.IsRemasteredOnly )
			return;

		var basePath = Halo2Mount.ResourcePath( lsnd.Tag.Path );
		var resources = new LoopingSoundResources();

		for ( var i = 0; i < lsnd.Tracks.Count; i++ )
		{
			var track = lsnd.Tracks[i];
			var loopTag = track.GetLoop( alternate: false );
			if ( loopTag is null || !_sounds.TryGetValue( loopTag.Path, out var loop ) || loop.Event.Files.Count == 0 )
				continue;

			LoopableSound start = null;
			if ( track.Start is not null && _sounds.TryGetValue( track.Start.Path, out start ) && start.Event.Files.Count == 0 )
				start = null;

			// Halo 2 plays a track with the track's gain instead of the sound's gain base and variance, plus
			// the sound class's gain at the looping sound's scale (1), at the random pitch's lower bound.
			var startPath = start is null ? null : track.Start.Path;
			var file = GetTrackFile( context, startPath, loopTag.Path, start, loop, bakeRate: false );
			if ( file is null )
				continue;

			var gain = Decibels( MathF.Min( 0, track.GainDecibels + loop.ClassGain1 ) );
			var trackEvent = new SoundEventLoader( [file], null, loop.TrackRate, loop.TrackRate, loop.Event.MinimumDistance, loop.Event.MaximumDistance, gain );
			context.Add( ResourceType.GameResource, $"{basePath}__track{i}.sound", trackEvent );

			// Cluster background sounds (scale 1 unless the palette entry overrides it, not done) play through a soundscape,
			// which plays the file as it is, so the track's pitch goes into a file of its own.
			var backgroundFile = GetTrackFile( context, startPath, loopTag.Path, start, loop, bakeRate: true );
			resources.Tracks.Add( new LoopingTrackResource( i, file, trackEvent, backgroundFile, gain ) );
		}

		for ( var i = 0; i < lsnd.DetailSounds.Count; i++ )
		{
			// A detail plays like any sound (one-shot gain, random pitch, its own distances unless a placement overrides
			// them); Halo 2 never reads the detail's gain field (+0x14).
			var detail = lsnd.DetailSounds[i];
			if ( !detail.PlaysWith( alternate: false ) || !_sounds.TryGetValue( detail.Sound.Path, out var sound ) || sound.Event.Files.Count == 0 )
				continue;

			var e = sound.Event;
			var detailEvent = new SoundEventLoader( e.Files, e.SkipFractions, e.MinRate, e.MaxRate, e.MinimumDistance, e.MaximumDistance, e.Gain, e.MaxGain );
			context.Add( ResourceType.GameResource, $"{basePath}__detail{i}.sound", detailEvent );

			// Start to start, a random period within the bounds; the sound's length isn't added. A start the
			// sound's skip fraction drops waits another period, which on average stretches the period by 1 / (1 - skip).
			var stretch = 1 / (1 - sound.Skip);
			resources.Details.Add( new LoopingDetailResource( i, detailEvent, MathF.Max( 0, detail.PeriodMin ) * stretch, MathF.Max( detail.PeriodMin, detail.PeriodMax ) * stretch,
				new CeLoopingSoundDetail( null, detail.PeriodMin, detail.PeriodMax, 1, detail.Flags,
					detail.YawMin, detail.YawMax, detail.PitchMin, detail.PitchMax, detail.DistanceMin, detail.DistanceMax ) ) );
		}

		if ( resources.Tracks.Count == 0 && resources.Details.Count == 0 )
			return;

		resources.Soundscape = new SoundscapeLoader( resources );
		context.Add( ResourceType.GameResource, $"{basePath}.sndscape", resources.Soundscape );
		_loopingSounds[lsnd.Tag.Path] = resources;
	}

	/// <summary>
	/// The sound file of a start and loop sound pair, <c>&lt;loop tag&gt;__loop&lt;n&gt;.vsnd</c>, shared by every track
	/// that plays them.
	/// </summary>
	/// <param name="bakeRate">False for a track's sound event, which has the track's pitch itself; true for a soundscape,
	/// which plays files as they are, so the track's pitch is baked in.</param>
	LoopTrackFileLoader GetTrackFile( MountContext context, string startPath, string loopPath, LoopableSound start, LoopableSound loop, bool bakeRate )
	{
		var rate = bakeRate ? MathF.Round( loop.TrackRate, 4 ) : 1;
		var key = (startPath ?? "", loopPath, rate);
		if ( _trackFiles.TryGetValue( key, out var file ) )
			return file;

		if ( loop.Event.Files.Count == 0 )
			return null;

		// The sequence plays at the loop's track pitch; LoopTrackFileLoader resamples the start sound by the ratio of the
		// two events' rates, so they carry the tracks' rates.
		var loopEvent = new SoundEventLoader( loop.Event.Files, loop.Event.SkipFractions, loop.TrackRate, loop.TrackRate, 0, 0, 1 );
		var startEvent = start is null ? null : new SoundEventLoader( start.Event.Files, null, start.TrackRate, start.TrackRate, 0, 0, 1 );

		var basePath = Halo2Mount.ResourcePath( loopPath );
		for ( var n = 0; ; n++ )
		{
			var name = $"{basePath}__loop{n}";
			if ( !_fileNames.Add( name ) )
				continue;

			file = new LoopTrackFileLoader( startEvent, loopEvent, rate );
			context.Add( ResourceType.Sound, name, file );
			return _trackFiles[key] = file;
		}
	}

	// Registers a sound tag's event under another name too (an effect role, see EffectRoles); false if the sound has none.
	public bool Alias( MountContext context, string name, string tagPath )
	{
		if ( !_events.TryGetValue( tagPath, out var e ) )
			return false;

		context.Add( ResourceType.GameResource, name, new SoundEventLoader( e.Files, e.SkipFractions, e.MinRate, e.MaxRate, e.MinimumDistance, e.MaximumDistance, e.Gain, e.MaxGain ) );
		return true;
	}

	/// <summary>
	/// The resources registered for a looping sound tag, or null if none of its sounds have samples.
	/// </summary>
	public LoopingSoundResources GetLoopingSound( string tagPath ) =>
		tagPath is not null && _loopingSounds.TryGetValue( tagPath, out var resources ) ? resources : null;

	public bool IsLooped( string tagPath ) => _looped.Contains( tagPath );

	bool SampleFileExists( string fileName )
	{
		if ( !_sampleFiles.TryGetValue( fileName, out var exists ) )
			_sampleFiles[fileName] = exists = _samples.Exists( fileName );

		return exists;
	}

	internal static float Decibels( float decibels ) => MathF.Pow( 10, decibels / 20 );

	public void Dispose() => _samples.Dispose();

	/// <summary>
	/// One permutation of a sound tag as a sound file, its gain baked in (Halo multiplies a sound's gain by its
	/// permutation's). Sounds a looping sound plays as its loop loop seamlessly.
	/// </summary>
	sealed class Halo2SoundFileLoader( Halo2Sounds sounds, string tagPath, SoundSamples samples, string fileName, SoundCompression compression, int channels, int sampleRate, SoundPermutation permutation ) : PermutationFileLoader
	{
		public override double Seconds => channels > 0 && sampleRate > 0 ? (double)permutation.PcmBytes / (2 * channels) / sampleRate : 0;

		public override PcmClip DecodeClip()
		{
			var pcm = samples.Decode( fileName, compression, channels, permutation.Chunks );
			if ( permutation.GainDecibels != 0 )
			{
				var gain = Decibels( permutation.GainDecibels );
				for ( var i = 0; i < pcm.Length; i++ )
					pcm[i] = (short)Math.Clamp( MathF.Round( pcm[i] * gain ), short.MinValue, short.MaxValue );
			}

			return new PcmClip( pcm, channels, sampleRate );
		}

		protected override DecodedSound Decode() => new( DecodeClip(), sounds.IsLooped( tagPath ) ? 0 : -1 );
	}
}
