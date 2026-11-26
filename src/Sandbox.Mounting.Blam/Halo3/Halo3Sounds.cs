using Blam.Halo3;
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

namespace Sandbox.Mounting.Blam.Halo3;

// Halo 3's sounds, from MCC's FMOD banks (halo3/fmod/pc), named like Halo 2's: every snd! is a sound event mount://halo3/<tag path>.sound picking one of its permutations at random, each permutation a sound file <tag path>.vsnd (first) or <tag path>_<n>.vsnd.
// Looping sounds (lsnd) get Halo 2's resources: <tag>__track<n>.sound, <tag>__detail<n>.sound and <tag>.sndscape (see RegisterLoopingSound).
// Registering reads tags only; banks open and samples decode (on the thread pool) when a sound first loads. Dialog is English.
static class Halo3Sounds
{
	const SoundLanguage Language = SoundLanguage.English;

	static readonly System.Threading.Lock _lock = new();
	static readonly HashSet<string> _fileNames = new( StringComparer.OrdinalIgnoreCase );
	static readonly Dictionary<string, LoopableSound> _sounds = new( StringComparer.OrdinalIgnoreCase );
	static readonly HashSet<string> _looped = new( StringComparer.OrdinalIgnoreCase );
	static readonly Dictionary<string, LoopingSoundResources> _loopingSounds = new( StringComparer.OrdinalIgnoreCase );
	static readonly Dictionary<(string Start, string Loop, float Rate), LoopTrackFileLoader> _trackFiles = new();
	static SoundBanks _banks;
	static bool _missingBanks;

	// A sound tag's event and what looping sounds need to play it their way (as in Halo2Sounds).
	internal sealed record LoopableSound( SoundEventLoader Event, float ClassGain1, float TrackRate, float Skip );

	public static string EventPath( string tagPath ) => $"mount://halo3/{Halo3Mount.ResourcePath( tagPath )}.sound";

	internal static SoundBanks Banks => _banks;

	internal static LoopableSound GetSound( string tagPath ) => tagPath is not null && _sounds.TryGetValue( tagPath, out var sound ) ? sound : null;

	internal static bool IsLooped( string tagPath ) => _looped.Contains( tagPath );

	// The resources registered for a looping sound tag, or null if none of its sounds play.
	internal static LoopingSoundResources GetLoopingSound( string tagPath ) =>
		tagPath is not null && _loopingSounds.TryGetValue( tagPath, out var resources ) ? resources : null;

	public static void Register( MountContext context, CacheFile cache, HashSet<string> registered )
	{
		var mount = Halo3Mount.Current;
		if ( !OpenBanks( mount ) )
			return;

		var gestalt = SoundGestalt.ForCache( cache );
		if ( gestalt is null )
			return;

		var classes = SoundClasses.ForCache( cache );
		foreach ( var tag in cache.TagsOf( "snd!" ) )
		{
			if ( tag.Address == 0 || string.IsNullOrEmpty( tag.Path ) || !registered.Add( $"{tag.Path}.snd!" ) )
				continue;

			try
			{
				Register( context, new SoundTag( tag, gestalt ), classes );
			}
			catch ( Exception e )
			{
				mount.Logger.Warning( e, $"Skipping sound {tag.Path}: {e.Message}" );
			}
		}

		foreach ( var tag in cache.TagsOf( "lsnd" ) )
		{
			if ( tag.Address == 0 || string.IsNullOrEmpty( tag.Path ) || !registered.Add( $"{tag.Path}.lsnd" ) )
				continue;

			try
			{
				RegisterLoopingSound( context, new LoopingSoundTag( tag ) );
			}
			catch ( Exception e )
			{
				mount.Logger.Warning( e, $"Skipping looping sound {tag.Path}: {e.Message}" );
			}
		}
	}

	static bool OpenBanks( Halo3Mount mount )
	{
		lock ( _lock )
		{
			if ( _banks is not null )
				return true;
			if ( _missingBanks )
				return false;

			var dll = System.IO.Path.GetFullPath( System.IO.Path.Combine( mount.FmodDirectory, "..", "..", "halo3.dll" ) );
			var banks = new SoundBanks( mount.FmodDirectory, dll, Language );
			if ( !banks.Exists || !File.Exists( dll ) )
			{
				mount.Logger.Warning( $"Couldn't find Halo 3's FMOD banks in {mount.FmodDirectory} (or halo3.dll), sounds won't be available" );
				_missingBanks = true;
				return false;
			}

			_banks = banks;
			return true;
		}
	}

	static void Register( MountContext context, SoundTag sound, SoundClasses classes )
	{
		var tag = sound.Tag;
		var soundClass = classes?.Get( sound.SoundClass );
		var basePath = Halo3Mount.ResourcePath( tag.Path );
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
				if ( !_fileNames.Add( name ) )
					continue;

				var loader = new Halo3SoundFileLoader( tag.Path, permutation );
				context.Add( ResourceType.Sound, name, loader );
				files.Add( loader );
				skips.Add( permutation.SkipFraction );
			}

			rangeFiles.Add( files );
			skipFractions.Add( skips );
		}

		// As Halo 2 plays a sound (Halo2Sounds): gain = base + r * variance + the class's gain at the scale (at most 0 dB), pitch = random pitch + the scale's pitch from the first pitch range whose maximum gain bounds hold it. The event is the sound at scale 0.
		var playback = sound.Playback;
		var (minimumDistance, maximumDistance) = sound.GetDistances( soundClass );
		var playing = sound.ChoosePitchRange( sound.Scale.PitchAt( 0 ) + (playback.RandomPitchMin + playback.RandomPitchMax) * 0.5f, r => rangeFiles[r].Count > 0 );
		if ( playing < 0 )
			return;

		var playingRange = sound.PitchRanges[playing];

		(float Gain, float MaxGain, float MinRate, float MaxRate) At( float scale )
		{
			var classGain = sound.ClassGainAt( soundClass, scale );
			var pitch = sound.Scale.PitchAt( scale );
			return (Decibels( MathF.Min( 0, playback.GainBase + MathF.Min( 0, playback.GainVariance ) + classGain ) ),
				Decibels( MathF.Min( 0, playback.GainBase + MathF.Max( 0, playback.GainVariance ) + classGain ) ),
				playingRange.RateAt( pitch + playback.RandomPitchMin ), playingRange.RateAt( pitch + playback.RandomPitchMax ));
		}

		var (gain, maxGain, minRate, maxRate) = At( 0 );
		context.Add( ResourceType.GameResource, $"{basePath}.sound", new SoundEventLoader( rangeFiles[playing], skipFractions[playing], minRate, maxRate, minimumDistance, maximumDistance, gain, maxGain ) );

		// What looping sounds play it with: scale 1, tracks at the random pitch's lower bound (as Halo 2).
		var one = At( 1 );
		var skip = Math.Clamp( playback.SkipFraction * sound.Scale.SkipMax, 0, 0.95f );
		var loopEvent = new SoundEventLoader( rangeFiles[playing], skipFractions[playing], one.MinRate, one.MaxRate, minimumDistance, maximumDistance, one.Gain, one.MaxGain );
		lock ( _lock )
			_sounds[tag.Path] = new LoopableSound( loopEvent, sound.ClassGainAt( soundClass, 1 ), one.MinRate, skip );

		// Sounds with several pitch ranges (vehicle engines) also get an event per range, <tag>__pitch<n>.sound, at the range's natural pitch.
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

	// As Halo2Sounds.RegisterLoopingSound: tracks at the track's gain plus the loop's class gain at scale 1 and the loop's lowest random pitch, one file each (start, then the loop's permutations repeating); details as one-shots at scale 1; all of it as a soundscape for cluster background sounds. Alternate loops and end sounds are left out (placed looping sounds never switch or stop).
	static void RegisterLoopingSound( MountContext context, LoopingSoundTag lsnd )
	{
		var basePath = Halo3Mount.ResourcePath( lsnd.Tag.Path );
		var resources = new LoopingSoundResources();

		for ( var i = 0; i < lsnd.Tracks.Count; i++ )
		{
			var track = lsnd.Tracks[i];
			var loopTag = track.GetLoop( alternate: false );
			var loop = GetSound( loopTag?.Path );
			if ( loop is null || loop.Event.Files.Count == 0 )
				continue;

			lock ( _lock )
				_looped.Add( loopTag.Path );

			var start = GetSound( track.Start?.Path );
			if ( start is not null && start.Event.Files.Count == 0 )
				start = null;

			var startPath = start is null ? null : track.Start.Path;
			var file = GetTrackFile( context, startPath, loopTag.Path, start, loop, bakeRate: false );
			if ( file is null )
				continue;

			var gain = Decibels( MathF.Min( 0, track.GainDecibels + loop.ClassGain1 ) );
			var trackEvent = new SoundEventLoader( [file], null, loop.TrackRate, loop.TrackRate, loop.Event.MinimumDistance, loop.Event.MaximumDistance, gain );
			context.Add( ResourceType.GameResource, $"{basePath}__track{i}.sound", trackEvent );

			// Soundscapes play files as they are, so the background file has the track's pitch baked in.
			var backgroundFile = GetTrackFile( context, startPath, loopTag.Path, start, loop, bakeRate: true );
			resources.Tracks.Add( new LoopingTrackResource( i, file, trackEvent, backgroundFile, gain ) );
		}

		for ( var i = 0; i < lsnd.DetailSounds.Count; i++ )
		{
			var detail = lsnd.DetailSounds[i];
			var sound = GetSound( detail.Sound.Path );
			if ( !detail.PlaysWith( alternate: false ) || sound is null || sound.Event.Files.Count == 0 )
				continue;

			// Start to start, a random period within the bounds; a skipped start waits another period.
			var e = sound.Event;
			var detailEvent = new SoundEventLoader( e.Files, e.SkipFractions, e.MinRate, e.MaxRate, e.MinimumDistance, e.MaximumDistance, e.Gain, e.MaxGain );
			context.Add( ResourceType.GameResource, $"{basePath}__detail{i}.sound", detailEvent );

			var stretch = 1 / (1 - sound.Skip);
			resources.Details.Add( new LoopingDetailResource( i, detailEvent, MathF.Max( 0, detail.PeriodMin ) * stretch, MathF.Max( detail.PeriodMin, detail.PeriodMax ) * stretch,
				new CeLoopingSoundDetail( null, detail.PeriodMin, detail.PeriodMax, 1, detail.Flags,
					detail.YawMin, detail.YawMax, detail.PitchMin, detail.PitchMax, detail.DistanceMin, detail.DistanceMax ) ) );
		}

		if ( resources.Tracks.Count == 0 && resources.Details.Count == 0 )
			return;

		resources.Soundscape = new SoundscapeLoader( resources );
		context.Add( ResourceType.GameResource, $"{basePath}.sndscape", resources.Soundscape );
		lock ( _lock )
			_loopingSounds[lsnd.Tag.Path] = resources;
	}

	// The sound file of a start and loop pair, <loop tag>__loop<n>.vsnd, shared by every track that plays them.
	static LoopTrackFileLoader GetTrackFile( MountContext context, string startPath, string loopPath, LoopableSound start, LoopableSound loop, bool bakeRate )
	{
		var rate = bakeRate ? MathF.Round( loop.TrackRate, 4 ) : 1;
		var key = (startPath ?? "", loopPath, rate);
		if ( _trackFiles.TryGetValue( key, out var file ) )
			return file;

		if ( loop.Event.Files.Count == 0 )
			return null;

		var loopEvent = new SoundEventLoader( loop.Event.Files, loop.Event.SkipFractions, loop.TrackRate, loop.TrackRate, 0, 0, 1 );
		var startEvent = start is null ? null : new SoundEventLoader( start.Event.Files, null, start.TrackRate, start.TrackRate, 0, 0, 1 );

		var basePath = Halo3Mount.ResourcePath( loopPath );
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

	internal static float Decibels( float decibels ) => MathF.Pow( 10, decibels / 20 );

	public static void Shutdown()
	{
		lock ( _lock )
		{
			_banks?.Dispose();
			_banks = null;
			_missingBanks = false;
			_fileNames.Clear();
			_sounds.Clear();
			_looped.Clear();
			_loopingSounds.Clear();
			_trackFiles.Clear();
		}
	}

	// One permutation as a sound file, its gain baked in; sounds a looping sound plays as its loop loop seamlessly.
	sealed class Halo3SoundFileLoader( string tagPath, SoundPermutation permutation ) : PermutationFileLoader
	{
		public override double Seconds
		{
			get
			{
				var sample = _banks?.Find( permutation.Hash )?.Sample;
				return sample is { SampleRate: > 0 } ? (double)sample.SampleCount / sample.SampleRate : 0;
			}
		}

		public override PcmClip DecodeClip()
		{
			var banks = _banks;
			if ( banks is null || !banks.Contains( permutation.Hash ) )
				return new PcmClip( [], 1, 48000 );

			var samples = banks.Decode( permutation.Hash );
			var pcm = samples.Pcm;
			if ( permutation.GainDecibels != 0 )
			{
				var scale = Decibels( permutation.GainDecibels );
				for ( var i = 0; i < pcm.Length; i++ )
					pcm[i] = (short)Math.Clamp( MathF.Round( pcm[i] * scale ), short.MinValue, short.MaxValue );
			}

			return new PcmClip( pcm, samples.Channels, samples.SampleRate );
		}

		protected override DecodedSound Decode() => new( DecodeClip(), IsLooped( tagPath ) ? 0 : -1 );
	}
}
