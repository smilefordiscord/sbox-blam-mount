using System.Runtime.InteropServices;
using Blam.Audio;
using Blam.Halo1;
using Blam.Halo1.Tags;

namespace Sandbox.Mounting.Blam.Halo1;

/// <summary>
/// Halo CE's sounds as s&amp;box resources. Every <c>snd!</c> tag becomes a sound event,
/// <c>mount://haloce/&lt;tag path&gt;.sound</c>, that picks one of its permutations at random, and
/// each permutation is a sound file, <c>mount://haloce/&lt;tag path&gt;.vsnd</c> for the first and
/// <c>&lt;tag path&gt;_&lt;n&gt;.vsnd</c> for the rest.
/// </summary>
/// <remarks>
/// The samples come from MCC's FMOD banks (see <see cref="SoundBankIndex"/>), decoded on a worker
/// thread when first loaded. A bank's samples for a tag don't always match the tag's
/// permutations one to one (MCC re-exported them), so the bank is taken as the truth.
///
/// Looping sounds (<c>lsnd</c>) get resources of their own, made from their tracks and detail
/// sounds (see <see cref="RegisterLoopingSound"/>), so a level can play them without any code of
/// ours at runtime: mount assemblies aren't in the game's type library, so components defined
/// here would be dropped from the scene when it's saved.
/// </remarks>
sealed class HaloSounds : IDisposable
{
	readonly SoundBankIndex _index;
	readonly Dictionary<string, SoundEventLoader> _events = new( StringComparer.OrdinalIgnoreCase );
	readonly Dictionary<string, LoopingSoundResources> _loopingSounds = new( StringComparer.OrdinalIgnoreCase );
	readonly Dictionary<(string Start, string Loop, float Rate, string End, bool Once), LoopTrackFileLoader> _trackFiles = new();

	/// <summary>
	/// Sounds some looping sound uses as its loop, which are made to repeat seamlessly.
	/// </summary>
	readonly HashSet<string> _looped = new( StringComparer.OrdinalIgnoreCase );

	/// <summary>
	/// Resource names in use, in case a tag's name collides with another tag's permutation name.
	/// </summary>
	readonly HashSet<string> _fileNames = new( StringComparer.OrdinalIgnoreCase );

	/// <summary>
	/// MCC's per-tag volume changes and minimum distances (see <see cref="SoundTagParams"/>), or null.
	/// </summary>
	readonly SoundTagParams _params;

	/// <summary>
	/// Each sound tag's gain and pitch modifiers at scale 1, which cluster background sounds play at.
	/// </summary>
	readonly Dictionary<string, (float Gain, float Pitch)> _scaleOne = new( StringComparer.OrdinalIgnoreCase );

	HaloSounds( SoundBankIndex index, SoundTagParams parameters )
	{
		_index = index;
		_params = parameters;
	}

	/// <summary>
	/// Opens the FMOD banks in <c>halo1/sound/pc</c>, or returns null if they're missing.
	/// </summary>
	public static HaloSounds Open( string gameDirectory, Sandbox.Diagnostics.Logger log )
	{
		var soundDirectory = System.IO.Path.Combine( gameDirectory, "sound", "pc" );
		if ( !System.IO.Directory.Exists( soundDirectory ) )
		{
			log.Warning( $"Couldn't find {soundDirectory}, sounds won't be available" );
			return null;
		}

		try
		{
			SoundTagParams parameters = null;
			try
			{
				parameters = SoundTagParams.Load( soundDirectory );
			}
			catch ( Exception e )
			{
				log.Warning( e, $"Couldn't read MCC's sound tag parameters: {e.Message}" );
			}

			return new HaloSounds( new SoundBankIndex( soundDirectory ), parameters );
		}
		catch ( Exception e )
		{
			log.Warning( e, $"Couldn't read the sound banks in {soundDirectory}: {e.Message}" );
			return null;
		}
	}

	/// <summary>
	/// Full path to load a sound tag's sound event with.
	/// </summary>
	public static string EventPath( string ident, string tagPath ) => $"mount://{ident}/{HaloCeMount.ResourcePath( tagPath )}.sound";

	/// <summary>
	/// The resource name of one permutation of a sound tag. The first is named after the tag.
	/// </summary>
	public static string FileName( string tagPath, int index ) =>
		index == 0 ? HaloCeMount.ResourcePath( tagPath ) : $"{HaloCeMount.ResourcePath( tagPath )}_{index}";

	/// <summary>
	/// Registers the sound tags of a map that haven't been registered by an earlier map.
	/// </summary>
	public void Register( MountContext context, CacheFile cache )
	{
		foreach ( var loop in LoopingSoundTag.FindLoopedSounds( cache ) )
			_looped.Add( loop.Path );

		foreach ( var tag in cache.Tags )
		{
			if ( tag.Class != SoundTagClasses.Sound || string.IsNullOrEmpty( tag.Path ) || _events.ContainsKey( tag.Path ) )
				continue;

			var sound = new SoundTag( cache, tag );
			var rangeFiles = RegisterFiles( context, tag.Path, sound );
			if ( rangeFiles is null )
				continue;

			// Played the way Halo plays a sound without a scale: from the pitch range for its
			// pitch, at a random pitch between its bounds (times the range's playback rate). The
			// pitch modifiers at scale 0 and 1 are left to the game, which knows the playing scale.
			var (minRate, maxRate, pitchRange) = sound.GetUnscaledPlayback( withModifier: false );
			var playing = pitchRange >= 0 && pitchRange < rangeFiles.Count && rangeFiles[pitchRange].Files.Count > 0
				? pitchRange
				: rangeFiles.FindIndex( r => r.Files.Count > 0 );
			if ( playing < 0 )
				continue;

			var (minimumDistance, maximumDistance) = sound.Distances;
			var gain = sound.GainModifier > 0 ? sound.GainModifier : 1;

			// MCC turns some sounds up or down from what their tags say (tags_params.ps), and gives
			// a few a minimum distance of their own. Looping sound tracks, details and soundscapes
			// take both from this event.
			var parameters = _params?.Find( tag.Path, sound.PitchRanges[playing].Name );
			var eventLoader = new SoundEventLoader( rangeFiles[playing].Files, rangeFiles[playing].SkipFractions, minRate, maxRate,
				parameters?.MinimumDistance ?? minimumDistance, maximumDistance, gain * (parameters?.Gain ?? 1) );
			context.Add( ResourceType.GameResource, $"{HaloCeMount.ResourcePath( tag.Path )}.sound", eventLoader );
			_events[tag.Path] = eventLoader;

			// Sounds with several pitch ranges (the vehicle engines) also get an event per range,
			// <tag>__pitch<n>.sound, at that range's playback rate, for a game to cross-fade
			// between as the pitch changes the way Halo picks a range by pitch.
			if ( sound.PitchRanges.Count > 1 )
			{
				for ( var r = 0; r < rangeFiles.Count; r++ )
				{
					if ( rangeFiles[r].Files.Count == 0 )
						continue;

					// MCC can set a pitch range apart (the Warthog engine's porsche_med).
					var rangeParameters = _params?.Find( tag.Path, sound.PitchRanges[r].Name );
					var (rangeMin, rangeMax) = sound.GetRangeRates( r );
					context.Add( ResourceType.GameResource, $"{HaloCeMount.ResourcePath( tag.Path )}__pitch{r}.sound",
						new SoundEventLoader( rangeFiles[r].Files, rangeFiles[r].SkipFractions, rangeMin, rangeMax,
							rangeParameters?.MinimumDistance ?? minimumDistance, maximumDistance, gain * (rangeParameters?.Gain ?? 1) ) );
				}
			}

			_scaleOne[tag.Path] = (sound.OneGainModifier > 0 ? sound.OneGainModifier : 1, sound.OnePitchModifier > 0 ? sound.OnePitchModifier : 1);
		}

		foreach ( var tag in cache.Tags )
		{
			if ( tag.Class == SoundTagClasses.LoopingSound && !string.IsNullOrEmpty( tag.Path ) && !_loopingSounds.ContainsKey( tag.Path ) )
				RegisterLoopingSound( context, tag.Path, new LoopingSoundTag( cache, tag ) );
		}
	}

	/// <summary>
	/// Registers what a level needs to play a looping sound, named after the tag:
	/// <list type="bullet">
	/// <item><c>&lt;tag&gt;__track&lt;n&gt;.sound</c>: a track, with the track's gain and the loop's pitch.
	/// It plays one sound file, its start sound followed by a sequence of its loop's variations that
	/// repeats (see <see cref="LoopingSoundSequence"/> and <see cref="GetTrackFile"/>); for a looping
	/// sound that isn't a loop, its start, one variation of its loop and its end, once.</item>
	/// <item><c>&lt;tag&gt;__track&lt;n&gt;__alternate.sound</c> and <c>__loop.sound</c>: a track's alternate
	/// loop and its loop, each alone, for a track that has an alternate a script can switch it to.</item>
	/// <item><c>&lt;tag&gt;__track&lt;n&gt;__end.sound</c> and <c>__alternate_end.sound</c>: what the track
	/// plays when a script stops it on its loop or on its alternate loop.</item>
	/// <item><c>&lt;tag&gt;__track&lt;n&gt;.json</c>, <c>__alternate.json</c> and <c>__loop.json</c>: where
	/// the sounds in each of those files begin (see <see cref="LoopTrackFileLoader.TimingJson"/>), for
	/// a script's stop or switch to wait for the variation playing to finish, as Halo's do.</item>
	/// <item><c>&lt;tag&gt;__detail&lt;n&gt;.sound</c>: a detail sound with the detail's gain.</item>
	/// <item><c>&lt;tag&gt;.sndscape</c>: all of it as a soundscape, for cluster background sounds:
	/// the tracks around the listener and the details at random times.</item>
	/// </list>
	/// </summary>
	/// <remarks>
	/// Halo only plays the details flagged for alternates when a script switches a looping sound to
	/// its alternate, which never happens to the sounds a level places, so those are left out.
	/// </remarks>
	void RegisterLoopingSound( MountContext context, string tagPath, LoopingSoundTag lsnd )
	{
		var basePath = HaloCeMount.ResourcePath( tagPath );
		var resources = new LoopingSoundResources();

		for ( var i = 0; i < lsnd.Tracks.Count; i++ )
		{
			var track = lsnd.Tracks[i];
			var loop = track.GetLoop( alternate: false );
			if ( loop is null || !_events.TryGetValue( loop.Path, out var loopEvent ) )
				continue;

			SoundEventLoader startEvent = null;
			if ( track.Start is not null )
				_events.TryGetValue( track.Start.Path, out startEvent );

			var startPath = startEvent is null ? null : track.Start.Path;
			var file = GetTrackFile( context, startPath, loop.Path, startEvent, loopEvent, 1 );
			if ( file is null )
				continue;

			var end = TrackEvent( track.GetEnd( alternate: false ) );
			var scriptFile = lsnd.NotALoop ? GetTrackFile( context, startPath, loop.Path, startEvent, loopEvent, 1, track.End?.Path, end, once: true ) : file;

			var name = $"{basePath}__track{i}";
			var trackEvent = new SoundEventLoader( [scriptFile], null, loopEvent.MinRate, loopEvent.MaxRate, loopEvent.MinimumDistance, loopEvent.MaximumDistance, Gain( track.Gain ) * loopEvent.Gain );
			context.Add( ResourceType.GameResource, $"{name}.sound", trackEvent );
			context.Add( ResourceType.Text, $"{name}.json", new TrackTimingLoader( scriptFile ) );

			// What a script switches the track to and back from (sound_looping_set_alternate): its
			// alternate loop and its loop, each on its own, with the track's gain.
			var alternate = track.GetLoop( alternate: true );
			if ( alternate != loop && _events.TryGetValue( alternate.Path, out var alternateEvent )
				&& GetTrackFile( context, null, alternate.Path, null, alternateEvent, 1 ) is { } alternateFile
				&& GetTrackFile( context, null, loop.Path, null, loopEvent, 1 ) is { } loopFile )
			{
				context.Add( ResourceType.GameResource, $"{name}__alternate.sound", new SoundEventLoader( [alternateFile], null, alternateEvent.MinRate, alternateEvent.MaxRate,
					alternateEvent.MinimumDistance, alternateEvent.MaximumDistance, Gain( track.Gain ) * alternateEvent.Gain ) );
				context.Add( ResourceType.Text, $"{name}__alternate.json", new TrackTimingLoader( alternateFile ) );
				context.Add( ResourceType.GameResource, $"{name}__loop.sound", new SoundEventLoader( [loopFile], null, loopEvent.MinRate, loopEvent.MaxRate,
					loopEvent.MinimumDistance, loopEvent.MaximumDistance, trackEvent.Gain ) );
				context.Add( ResourceType.Text, $"{name}__loop.json", new TrackTimingLoader( loopFile ) );
			}

			// What the track plays when a script stops it (sound_looping_stop), on the loop or on
			// the alternate loop, with the track's gain.
			if ( end is not null )
				context.Add( ResourceType.GameResource, $"{name}__end.sound", WithGain( end, Gain( track.Gain ) * end.Gain ) );
			if ( track.GetEnd( alternate: true ) != track.End && TrackEvent( track.GetEnd( alternate: true ) ) is { } alternateEnd )
				context.Add( ResourceType.GameResource, $"{name}__alternate_end.sound", WithGain( alternateEnd, Gain( track.Gain ) * alternateEnd.Gain ) );

			// As a cluster's background sound Halo starts the looping sound at scale 1: the loop at
			// its gain and pitch at one. A soundscape plays the file as it is, so that pitch goes in
			// a file of its own.
			var (oneGain, onePitch) = _scaleOne.GetValueOrDefault( loop.Path, (1f, 1f) );
			var backgroundRate = loopEvent.MidRate * onePitch;
			var backgroundFile = MathF.Abs( backgroundRate - 1 ) < 0.001f ? file : GetTrackFile( context, startPath, loop.Path, startEvent, loopEvent, backgroundRate );
			resources.Tracks.Add( new LoopingTrackResource( i, file, trackEvent, backgroundFile, trackEvent.Gain * oneGain ) );
		}

		for ( var i = 0; i < lsnd.DetailSounds.Count; i++ )
		{
			var detail = lsnd.DetailSounds[i];
			if ( !detail.PlaysWith( alternate: false ) || !_events.TryGetValue( detail.Sound.Path, out var soundEvent ) )
				continue;

			var detailEvent = new SoundEventLoader( soundEvent.Files, soundEvent.SkipFractions, soundEvent.MinRate, soundEvent.MaxRate, soundEvent.MinimumDistance, soundEvent.MaximumDistance, Gain( detail.Gain ) * soundEvent.Gain );
			context.Add( ResourceType.GameResource, $"{basePath}__detail{i}.sound", detailEvent );

			// Halo waits a random period (scaled for the looping sound's scale, which is 0 here)
			// plus the detail's longest permutation, so a detail never overlaps itself.
			var scale = lsnd.DetailPeriodScaleZero > 0 ? lsnd.DetailPeriodScaleZero : 1;
			var longest = soundEvent.LongestSeconds;
			resources.Details.Add( new LoopingDetailResource( i, detailEvent,
				MathF.Max( 0, detail.PeriodMin ) * scale + longest,
				MathF.Max( detail.PeriodMin, detail.PeriodMax ) * scale + longest,
				detail ) );
		}

		if ( resources.Tracks.Count == 0 && resources.Details.Count == 0 )
			return;

		resources.Soundscape = new SoundscapeLoader( resources );
		context.Add( ResourceType.GameResource, $"{basePath}.sndscape", resources.Soundscape );
		_loopingSounds[tagPath] = resources;
	}

	/// <summary>
	/// Registers a sound tag's permutations as sound files and returns each pitch range's
	/// variations (with their skip fractions), or null if the banks have nothing for it.
	/// </summary>
	/// <remarks>
	/// MCC's banks hold a tag's samples under its path, every pitch range together, or, for
	/// sounds with named pitch ranges (the vehicle engines: warthog_7_engine's porsche_idle,
	/// porsche_low...), each range under <c>&lt;tag path&gt;\&lt;range name&gt;</c>.
	/// </remarks>
	List<(List<SoundFileLoader> Files, List<float> SkipFractions)> RegisterFiles( MountContext context, string tagPath, SoundTag sound )
	{
		var ranges = sound.PitchRanges.Select( _ => (Files: new List<SoundFileLoader>(), SkipFractions: new List<float>()) ).ToList();
		var fileIndex = 0;
		var added = new List<(string Name, SoundFileLoader Loader)>();

		SoundFileLoader AddFile( Fsb5Bank bank, List<FsbSample> chain, List<float> gains )
		{
			var name = FileName( tagPath, fileIndex++ );
			if ( !_fileNames.Add( name ) )
				return null;

			var loader = new SoundFileLoader( this, bank, chain, gains, tagPath );
			added.Add( (name, loader) );
			return loader;
		}

		var entry = _index.Find( tagPath, sound.PermutationCount );
		if ( entry is not null )
		{
			if ( !entry.Bank.CanDecode )
				return null;

			var files = new List<SoundFileLoader>();
			for ( var i = 0; i < entry.SampleCount; i++ )
			{
				var chainIndices = sound.GetBankChain( i, entry.SampleCount );
				files.Add( AddFile( entry.Bank,
					chainIndices.Select( n => entry.Bank.Samples[entry.FirstSample + n] ).ToList(),
					chainIndices.Select( n => sound.GetBankGain( n, entry.SampleCount ) ).ToList() ) );
			}

			// Each pitch range's variations; if the bank doesn't line up with the tag, all of them
			// stand for the range the sound plays at.
			var (_, _, playing) = sound.GetUnscaledPlayback( withModifier: false );
			for ( var r = 0; r < ranges.Count; r++ )
			{
				var range = sound.GetBankSamples( r, entry.SampleCount );
				if ( range is null && r != playing )
					continue;

				var (first, count) = range ?? (0, files.Count);
				for ( var i = first; i < first + count && i < files.Count; i++ )
				{
					if ( files[i] is null )
						continue;

					ranges[r].Files.Add( files[i] );
					ranges[r].SkipFractions.Add( range is not null ? sound.PitchRanges[r].Permutations[i - first].SkipFraction : 0 );
				}
			}

			foreach ( var (name, loader) in added )
				context.Add( ResourceType.Sound, name, loader );
			return ranges;
		}

		var found = false;
		for ( var r = 0; r < ranges.Count; r++ )
		{
			var pitchRange = sound.PitchRanges[r];
			var rangeEntry = _index.Find( $"{tagPath}\\{pitchRange.Name}", pitchRange.Permutations.Count );
			if ( rangeEntry is null || !rangeEntry.Bank.CanDecode )
				continue;

			found = true;
			for ( var i = 0; i < rangeEntry.SampleCount; i++ )
			{
				// Long sounds split into chained permutations: the chain within the range.
				var chainIndices = new List<int>();
				var visited = new HashSet<int>();
				for ( var n = i; n >= 0 && n < rangeEntry.SampleCount && visited.Add( n ); n = n < pitchRange.Permutations.Count ? pitchRange.Permutations[n].NextPermutation : -1 )
					chainIndices.Add( n );

				var file = AddFile( rangeEntry.Bank,
					chainIndices.Select( n => rangeEntry.Bank.Samples[rangeEntry.FirstSample + n] ).ToList(),
					chainIndices.Select( n => n < pitchRange.Permutations.Count && pitchRange.Permutations[n].Gain > 0 ? pitchRange.Permutations[n].Gain : 1f ).ToList() );

				var variations = pitchRange.ActualPermutationCount > 0 ? pitchRange.ActualPermutationCount : rangeEntry.SampleCount;
				if ( file is not null && i < variations )
				{
					ranges[r].Files.Add( file );
					ranges[r].SkipFractions.Add( i < pitchRange.Permutations.Count ? pitchRange.Permutations[i].SkipFraction : 0 );
				}
			}
		}

		foreach ( var (name, loader) in added )
			context.Add( ResourceType.Sound, name, loader );
		return found ? ranges : null;
	}

	/// <summary>
	/// The sound file of a start and loop sound pair, <c>&lt;loop tag&gt;__loop&lt;n&gt;.vsnd</c>, shared
	/// by every looping sound track that plays them (levels have many background sounds mixing the
	/// same loops at different gains).
	/// </summary>
	/// <param name="rate">Playback rate baked into the file (1 for sound events, which have their own).</param>
	/// <param name="once">The file plays the start, one variation of the loop and the end once, for a looping sound that isn't a loop.</param>
	LoopTrackFileLoader GetTrackFile( MountContext context, string startPath, string loopPath, SoundEventLoader startEvent, SoundEventLoader loopEvent, float rate,
		string endPath = null, SoundEventLoader endEvent = null, bool once = false )
	{
		var key = (startPath ?? "", loopPath, rate, endPath ?? "", once);
		if ( _trackFiles.TryGetValue( key, out var file ) )
			return file;

		var basePath = HaloCeMount.ResourcePath( loopPath );
		for ( var n = 0; ; n++ )
		{
			var name = $"{basePath}__loop{n}";
			if ( !_fileNames.Add( name ) )
				continue;

			file = new LoopTrackFileLoader( startEvent, loopEvent, rate, endEvent, once );
			context.Add( ResourceType.Sound, name, file );
			return _trackFiles[key] = file;
		}
	}

	public bool IsLooped( string tagPath ) => _looped.Contains( tagPath );

	/// <summary>The sound event of a looping sound track's sound, or null if it has none or no samples.</summary>
	SoundEventLoader TrackEvent( TagEntry sound ) => sound is not null && _events.TryGetValue( sound.Path, out var e ) ? e : null;

	static SoundEventLoader WithGain( SoundEventLoader e, float gain ) =>
		new( e.Files, e.SkipFractions, e.MinRate, e.MaxRate, e.MinimumDistance, e.MaximumDistance, gain );

	/// <summary>
	/// A looping sound's track or detail gain: 0 is unset in Halo's tags and plays at full volume.
	/// </summary>
	static float Gain( float gain ) => gain > 0 ? gain : 1;

	/// <summary>
	/// The resources made for a looping sound tag, or null if none of its sounds have samples.
	/// </summary>
	public LoopingSoundResources GetLoopingSound( string tagPath ) =>
		tagPath is not null && _loopingSounds.TryGetValue( tagPath, out var resources ) ? resources : null;

	/// <summary>
	/// Starts decoding the samples of these sound tags in parallel, so loading their sound events
	/// afterwards doesn't decode them one after another.
	/// </summary>
	public void Prefetch( IEnumerable<string> tagPaths )
	{
		foreach ( var path in tagPaths )
		{
			if ( !_events.TryGetValue( path, out var loader ) )
				continue;

			foreach ( var file in loader.Files )
				file.Prefetch();
		}
	}

	/// <summary>
	/// Whether a sound tag has samples, i.e. its sound event can be loaded.
	/// </summary>
	public bool Contains( string tagPath ) => _events.ContainsKey( tagPath );

	public void Dispose() => _index.Dispose();
}

/// <summary>
/// The resources registered for one looping sound tag.
/// </summary>
sealed class LoopingSoundResources
{
	public List<LoopingTrackResource> Tracks { get; } = new();
	public List<LoopingDetailResource> Details { get; } = new();
	public SoundscapeLoader Soundscape { get; set; }

	/// <summary>
	/// Starts decoding everything the looping sound plays, as sound points or as a cluster's
	/// background sound.
	/// </summary>
	public void Prefetch( bool background = false )
	{
		foreach ( var track in Tracks )
			(background ? track.BackgroundFile : track.File).Prefetch();

		foreach ( var detail in Details )
		{
			foreach ( var file in detail.Event.Files )
				file.Prefetch();
		}
	}
}

/// <param name="Index">The track's index in the tag.</param>
/// <param name="BackgroundFile">The track as a cluster's background sound, at the loop's pitch at scale 1.</param>
/// <param name="BackgroundVolume">Its volume: the track's gain times the loop's, and the loop's gain at scale 1.</param>
sealed record LoopingTrackResource( int Index, LoopTrackFileLoader File, SoundEventLoader Event, LoopTrackFileLoader BackgroundFile, float BackgroundVolume );

/// <param name="Index">The detail's index in the tag.</param>
/// <param name="MinPeriod">Seconds from one play to the next, lower bound.</param>
sealed record LoopingDetailResource( int Index, SoundEventLoader Event, float MinPeriod, float MaxPeriod, LoopingSoundDetail Detail );

/// <summary>
/// Where the sounds in a looping sound track's file begin, as JSON (<see cref="LoopTrackFileLoader.TimingJson"/>).
/// </summary>
sealed class TrackTimingLoader( LoopTrackFileLoader file ) : HaloCeLoader
{
	protected override object Load() => file.TimingJson();
}

/// <summary>
/// Decoded samples, and the frame they loop from (-1 if they don't loop).
/// </summary>
readonly record struct DecodedSound( PcmClip Clip, int LoopStart );

/// <summary>
/// A sound file made from samples decoded on the thread pool.
/// </summary>
abstract class DecodedSoundLoader : HaloCeLoader
{
	readonly System.Threading.Lock _decodeLock = new();
	Task<DecodedSound> _decode;
	bool _loaded;

	/// <summary>
	/// Decodes the samples. Runs on the thread pool.
	/// </summary>
	protected abstract DecodedSound Decode();

	/// <summary>
	/// Playback rate baked into the sound file, by declaring its samples at that multiple of their rate.
	/// </summary>
	protected virtual float Rate => 1;

	/// <summary>
	/// The sound file's channel count once it's made (0 before). A sound file loaded through
	/// <see cref="SoundFile.Load"/> can't report its own until the engine has it loaded.
	/// </summary>
	public int Channels { get; private set; }

	/// <summary>
	/// Starts decoding ahead of a load, unless the sound file has been made already.
	/// </summary>
	public void Prefetch()
	{
		if ( !_loaded )
			StartDecode();
	}

	/// <summary>
	/// Starts decoding on the thread pool if it hasn't started yet.
	/// </summary>
	Task<DecodedSound> StartDecode()
	{
		lock ( _decodeLock )
		{
			return _decode ??= Task.Run( Decode );
		}
	}

	protected override Task<object> LoadAsync()
	{
		// On the main thread (SoundFile.Load, GameResource.Load, scene builds) the decode is waited
		// for right here. Awaiting would make SyncContext.RunBlocking pump the main thread queue,
		// which can run other queued work, like a sky render, in the middle of a scene build.
		if ( ThreadSafe.IsMainThread )
			return Task.FromResult<object>( Create( StartDecode().GetAwaiter().GetResult() ) );

		return LoadOffMainThread();
	}

	async Task<object> LoadOffMainThread()
	{
		var decoded = await StartDecode().ConfigureAwait( false );

		// Sound files can only be created on the main thread.
		await GameTask.MainThread();
		return Create( decoded );
	}

	SoundFile Create( DecodedSound decoded )
	{
		// The decoded samples are only needed until the sound file exists.
		lock ( _decodeLock )
		{
			_decode = null;
			_loaded = true;
		}

		if ( decoded.Clip is null || decoded.Clip.Samples.Length == 0 )
			return null;

		Channels = decoded.Clip.Channels;
		return SoundFile.FromPcm( Path, MemoryMarshal.AsBytes( decoded.Clip.Samples.AsSpan() ), new SoundFile.PcmOptions
		{
			Channels = decoded.Clip.Channels,
			Rate = (uint)MathF.Round( decoded.Clip.SampleRate * Rate ),
			Bits = 16,
			LoopStart = decoded.LoopStart,
		} );
	}
}

/// <summary>
/// One permutation of a sound tag as a sound file, which looping sound tracks can also join into
/// sequences (<see cref="LoopTrackFileLoader"/>). Halo CE's are <see cref="SoundFileLoader"/>; Halo 2
/// has its own.
/// </summary>
abstract class PermutationFileLoader : DecodedSoundLoader
{
	/// <summary>
	/// Length in seconds, known without decoding.
	/// </summary>
	public abstract double Seconds { get; }

	/// <summary>
	/// Decodes the samples without making a sound file of them.
	/// </summary>
	public abstract PcmClip DecodeClip();
}

/// <summary>
/// One permutation of a sound tag as a sound file: its bank sample, or for a long sound split
/// into chained permutations, the samples of the whole chain joined end to end. Each
/// permutation's gain is baked in, as Halo multiplies a sound's gain by its permutation's.
/// </summary>
sealed class SoundFileLoader( HaloSounds sounds, Fsb5Bank bank, IReadOnlyList<FsbSample> samples, IReadOnlyList<float> gains, string tagPath ) : PermutationFileLoader
{
	public override double Seconds => samples.Sum( s => s.SampleRate > 0 ? (double)s.SampleCount / s.SampleRate : 0 );

	public override PcmClip DecodeClip()
	{
		var clips = samples.Select( ( sample, i ) => new PcmClip( Scaled( bank.Decode( sample, out var channels ), gains[i] ), channels, sample.SampleRate ) ).ToList();
		return clips.Count == 1 ? clips[0] : LoopingSoundSequence.Join( null, 1, clips, Enumerable.Range( 0, clips.Count ).ToList(), out _ );
	}

	protected override DecodedSound Decode() => new( DecodeClip(), sounds.IsLooped( tagPath ) ? 0 : -1 );

	static short[] Scaled( short[] pcm, float gain )
	{
		if ( gain <= 0 || gain == 1 )
			return pcm;

		for ( var i = 0; i < pcm.Length; i++ )
			pcm[i] = (short)Math.Clamp( MathF.Round( pcm[i] * gain ), short.MinValue, short.MaxValue );

		return pcm;
	}
}

/// <summary>
/// A looping sound track as one sound file: the start sound (one of its permutations at random),
/// then a sequence of the loop's variations, looping from the end of the start sound. A track of a
/// looping sound that isn't a loop plays its start, one variation of its loop and its end once.
/// </summary>
/// <param name="end">The end sound, which a track that plays once ends with.</param>
/// <param name="once">The looping sound isn't a loop: the file plays through once.</param>
sealed class LoopTrackFileLoader( SoundEventLoader start, SoundEventLoader loop, float rate = 1, SoundEventLoader end = null, bool once = false ) : DecodedSoundLoader
{
	protected override float Rate => rate;

	/// <summary>The permutations the file joins: the start's and end's (null for none) and the loop's in order.</summary>
	sealed record Plan( PermutationFileLoader Start, List<int> Order, PermutationFileLoader End );

	readonly System.Threading.Lock _planLock = new();
	Plan _plan;

	Plan GetPlan()
	{
		lock ( _planLock )
		{
			if ( _plan is not null )
				return _plan;

			var random = new Random();
			var variations = loop.Files;
			var order = once
				? variations.Count == 0 ? [] : [random.Next( variations.Count )]
				: LoopingSoundSequence.BuildOrder( variations.Select( f => f.Seconds ).ToList(), loop.SkipFractions, random );

			return _plan = new Plan( RandomFile( start, random ), order, once ? RandomFile( end, random ) : null );
		}
	}

	static PermutationFileLoader RandomFile( SoundEventLoader e, Random random ) => e is { Files.Count: > 0 } ? e.Files[random.Next( e.Files.Count )] : null;

	/// <summary>Playback rate of another sound of the track relative to the loop's (their pitch ranges' playback rates can differ).</summary>
	float RateOf( SoundEventLoader other ) => other is null || loop.MidRate <= 0 ? 1 : other.MidRate / loop.MidRate;

	/// <summary>
	/// Where the file's sounds begin and end, in seconds of playback: as JSON, <c>Start</c> the
	/// length of the start sound, <c>Loop</c> the length of each variation of the loop in the order
	/// they play, which repeats from the end of the start sound if <c>Loops</c>.
	/// </summary>
	public string TimingJson()
	{
		var plan = GetPlan();
		var startSeconds = plan.Start is null ? 0 : plan.Start.Seconds / RateOf( start ) / rate;
		var loops = plan.Order.Select( i => loop.Files[i].Seconds / rate );
		return System.Text.Json.JsonSerializer.Serialize( new { Start = startSeconds, Loop = loops, Loops = !once } );
	}

	protected override DecodedSound Decode()
	{
		var plan = GetPlan();
		var variations = loop.Files;

		// Decode the variations that play (and the start and end sounds) in parallel.
		var used = plan.Order.Distinct().ToList();
		var clips = new PcmClip[variations.Count];
		PcmClip startClip = null, endClip = null;
		Parallel.For( 0, used.Count + 2, i =>
		{
			if ( i < used.Count )
				clips[used[i]] = variations[used[i]].DecodeClip();
			else if ( i == used.Count )
				startClip = plan.Start?.DecodeClip();
			else
				endClip = plan.End?.DecodeClip();
		} );

		// The whole sequence plays at the loop's pitch, so a start or end sound from a pitch range
		// with a different playback rate is resampled to sound right at it.
		var clip = LoopingSoundSequence.Join( startClip, RateOf( start ), clips, plan.Order, out var loopStart, endClip, RateOf( end ) );
		return new DecodedSound( clip, once ? -1 : loopStart );
	}
}

/// <summary>
/// A sound event that plays one of a set of sound files at random.
/// </summary>
/// <param name="files">The sound files: a sound tag's variations for the pitch it plays at.</param>
/// <param name="skipFractions">Each file's skip fraction (looping sounds use them), or null.</param>
/// <param name="minRate">Lowest playback rate (Halo's random pitch times the pitch range's playback rate).</param>
/// <param name="minimumDistance">Distance in world units within which it plays at full volume.</param>
/// <param name="maximumDistance">Distance in world units at which it has faded out, or 0 for s&amp;box's default.</param>
/// <param name="gain">Volume: the sound tag's gain modifier, times a looping sound's own gain.</param>
/// <param name="maxGain">Highest volume, for a gain picked at random per play from <paramref name="gain"/> up to it
/// (Halo 2's gain variance); 0 or less for a fixed gain.</param>
sealed class SoundEventLoader( IReadOnlyList<DecodedSoundLoader> files, IReadOnlyList<float> skipFractions, float minRate, float maxRate, float minimumDistance, float maximumDistance, float gain, float maxGain = 0 ) : HaloCeLoader
{
	public float MaxGain => maxGain > gain ? maxGain : gain;

	List<PermutationFileLoader> _permutations;

	/// <summary>
	/// The files that are a sound tag's permutations (all of them, except for looping sound tracks).
	/// </summary>
	public IReadOnlyList<PermutationFileLoader> Files => _permutations ??= files.OfType<PermutationFileLoader>().ToList();

	public IReadOnlyList<float> SkipFractions => skipFractions;
	public float MinRate => minRate;
	public float MaxRate => maxRate;
	public float MidRate => (minRate + maxRate) * 0.5f;
	public float MinimumDistance => minimumDistance;
	public float MaximumDistance => maximumDistance;
	public float Gain => gain;

	/// <summary>
	/// Length of the longest permutation in seconds.
	/// </summary>
	public float LongestSeconds => Files.Count == 0 ? 0 : (float)Files.Max( f => f.Seconds );

	protected override object Load()
	{
		// Decode all permutations at once rather than one after another.
		foreach ( var file in files )
			file.Prefetch();

		var sounds = new List<SoundFile>();
		foreach ( var file in files )
		{
			var sound = SoundFile.Load( file.Path );
			if ( sound is not null )
				sounds.Add( sound );
		}

		// Picks a random permutation each time by default, like Halo.
		// s&box's simulated reverb (reflections) is on by default and turns Halo's dry sounds into
		// echoes. Halo's own room sound is the cluster's sound environment (DspVolume in levels).
		// Nor does Halo muffle sounds behind walls or dull them with distance: no occlusion (s&box
		// derives transmission from it) or air absorption.
		var soundEvent = new SoundEvent
		{
			Sounds = sounds,
			ReverbEnabled = false,
			OcclusionEnabled = false,
			AirAbsorption = false,
		};

		// A random pitch for every play, as Halo picks one for every sound it starts.
		if ( minRate != 1 || maxRate != 1 )
			soundEvent.Pitch = new RangedFloat( minRate, maxRate );

		// Halo multiplies a sound's gain by its tag's gain modifier.
		if ( maxGain > gain && gain > 0 )
			soundEvent.Volume = new RangedFloat( gain, maxGain );
		else if ( gain > 0 && gain != 1 )
			soundEvent.Volume = gain;

		if ( maximumDistance > 0 )
		{
			soundEvent.Distance = maximumDistance * HaloCeMount.WorldScale;
			soundEvent.Falloff = Falloff( Math.Clamp( minimumDistance / maximumDistance, 0, 1 ) );
		}

		return soundEvent;
	}

	/// <summary>
	/// Volume over distance (as a fraction of the maximum distance) the way Halo fades a sound:
	/// full within the minimum distance, then a straight line down to nothing at the maximum. Halo
	/// also plays 3D sounds with an inverse distance rolloff from the minimum distance (the
	/// minimum over the distance), and the two multiply.
	/// </summary>
	/// <param name="minimum">The minimum distance as a fraction of the maximum.</param>
	public static Curve Falloff( float minimum )
	{
		static Curve.Frame Frame( float time, float value ) => new( time, value ) { Mode = Curve.HandleMode.Linear };

		// s&box reads the curve at the distance over the maximum clamped to 1, so beyond the maximum
		// it plays at the curve's last value: that has to be 0. A sound whose minimum distance is its
		// maximum (b40's som, 1.5 and 1.5) plays at full volume up to the maximum, then not at all.
		var frames = new List<Curve.Frame> { Frame( 0, 1 ) };
		if ( minimum >= 1 )
			return new Curve( Frame( 0, 1 ), Frame( 0.999f, 1 ), Frame( 1, 0 ) );

		if ( minimum > 0 )
			frames.Add( Frame( minimum, 1 ) );

		const int steps = 16;
		for ( var i = 1; i <= steps; i++ )
		{
			var x = minimum + (1 - minimum) * i / steps;
			var linear = 1 - (x - minimum) / (1 - minimum);
			var inverse = minimum > 0 ? Math.Min( 1, minimum / x ) : 1;
			frames.Add( Frame( x, linear * inverse ) );
		}

		return new Curve( frames.ToArray() );
	}
}

/// <summary>
/// A looping sound as a soundscape, for cluster background sounds: Halo plays the background sound
/// of the cluster the camera is in, without a position, with its detail sounds around the listener.
/// </summary>
sealed class SoundscapeLoader( LoopingSoundResources resources ) : HaloCeLoader
{
	protected override object Load()
	{
		resources.Prefetch( background: true );

		var soundscape = new Soundscape();
		foreach ( var track in resources.Tracks )
		{
			var file = SoundFile.Load( track.BackgroundFile.Path );
			if ( file is null )
				continue;

			// MCC plays sounds without a position at half volume when they're mono; a stereo sound
			// plays as it is.
			soundscape.LoopedSounds.Add( new Soundscape.LoopedSound
			{
				SoundFile = file,
				Volume = track.BackgroundVolume * (track.BackgroundFile.Channels >= 2 ? 1 : 0.5f),
			} );
		}

		foreach ( var detail in resources.Details )
		{
			var soundEvent = GameResource.Load<SoundEvent>( detail.Event.Path );
			if ( soundEvent is null )
				continue;

			// Details of a sound without a position are offset from the listener. Every MCC map has
			// distance bounds of 0 (played at the listener), which the soundscape does at distance 0.
			soundscape.StingSounds.Add( new Soundscape.StingSound
			{
				SoundFile = soundEvent,
				InstanceCount = 1,
				RepeatTime = new RangedFloat( detail.MinPeriod, detail.MaxPeriod ),
				Distance = new RangedFloat( detail.Detail.DistanceMin * HaloCeMount.WorldScale, MathF.Max( detail.Detail.DistanceMin, detail.Detail.DistanceMax ) * HaloCeMount.WorldScale ),
			} );
		}

		return soundscape;
	}
}
