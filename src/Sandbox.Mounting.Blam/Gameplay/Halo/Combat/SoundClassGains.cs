using System;
using Sandbox.Audio;

namespace Halo.Combat;

// Halo's scripted sound class gains, eased linearly over the ticks the script gave.
// Each class's sounds play through a child mixer whose volume is the gain.
// While a scripted dialog line plays, every other class is held down so the line is heard.
// Only active during a campaign mission.
public static class SoundClassGains {
	public struct State {
		public float[] Gain;
		public float[] Desired;
		public int[] Ticks;

		public State Copy() => new() { Gain = Gain.ToArray(), Desired = Desired.ToArray(), Ticks = Ticks.ToArray() };
	}

	static State _state = Fresh();

	// What the other classes drop to under dialog, reached at 0.03 a tick and let back up at 0.007.
	const float GainUnderDialog = 0.7f;
	const float DuckRate = 0.03f;
	const float RecoverRate = 0.007f;
	static float _nondialog = 1;

	public static bool Active { get; set; }

	static readonly Dictionary<(Mixer Parent, int Class), Mixer> _mixers = new();

	static State Fresh() {
		var count = SoundClasses.Names.Length;
		var state = new State { Gain = new float[count], Desired = new float[count], Ticks = new int[count] };
		Array.Fill( state.Gain, 1f );
		Array.Fill( state.Desired, 1f );
		return state;
	}

	public static void Reset() {
		_state = Fresh();
		_nondialog = 1;
		Apply();
	}

	public static State Save() => _state.Copy();

	public static void Restore( State state ) {
		_state = state.Copy();
		Apply();
	}

	// Every class whose name contains name heads for the gain, clamped to 0..1, over the ticks.
	// Scripts spell class names with underscores, as in "ambient_nature".
	public static void Set( string name, float gain, int ticks ) {
		if ( name is null ) return;
		var spelled = name.Replace( '_', ' ' );
		for ( var i = 0; i < SoundClasses.Names.Length; i++ ) {
			if ( SoundClasses.Get( i ) is null || !SoundClasses.Names[i].Contains( spelled, StringComparison.Ordinal ) ) continue;
			_state.Desired[i] = Math.Clamp( gain, 0, 1 );
			_state.Ticks[i] = Math.Max( ticks, 0 );
		}
	}

	public static void Tick( bool dialogPlaying ) {
		for ( var i = 0; i < _state.Gain.Length; i++ ) {
			if ( _state.Ticks[i] > 1 ) {
				_state.Gain[i] += (_state.Desired[i] - _state.Gain[i]) / _state.Ticks[i];
				_state.Ticks[i]--;
			}
			else {
				_state.Gain[i] = _state.Desired[i];
				_state.Ticks[i] = 0;
			}
		}

		var target = dialogPlaying ? GainUnderDialog : 1;
		var rate = dialogPlaying ? DuckRate : RecoverRate;
		_nondialog += Math.Clamp( target - _nondialog, -rate, rate );
		Apply();
	}

	static void Apply() {
		foreach ( var ((_, soundClass), mixer) in _mixers )
			mixer.Volume = Volume( soundClass );
	}

	static float Volume( int soundClass ) => _state.Gain[soundClass] * (SoundClasses.IsScriptedDialog( soundClass ) ? 1 : _nondialog);

	// Puts a sound through its class's mixer; soundClass indexes SoundClasses.Names.
	public static void Route( SoundHandle handle, int soundClass ) {
		if ( !Active || handle is null || MixerFor( handle.TargetMixer ?? Mixer.Default, soundClass ) is not { } mixer ) return;
		handle.TargetMixer = mixer;
	}

	// Routes every sound the component starts from now on through its class's mixer.
	public static void Route( BaseSoundComponent sound, int soundClass ) {
		if ( !Active || !sound.IsValid() ) return;
		var parent = sound.TargetMixer.Get( sound.SoundEvent?.DefaultMixer.Get() ?? Mixer.Default );
		if ( MixerFor( parent, soundClass ) is { } mixer ) sound.TargetMixer = mixer;
	}

	// A class's mixer under a parent, created with the parent's settings on first use.
	static Mixer MixerFor( Mixer parent, int soundClass ) {
		if ( parent is null || soundClass < 0 || soundClass >= _state.Gain.Length ) return null;
		if ( _mixers.TryGetValue( (parent, soundClass), out var mixer ) && Mixer.FindMixerByGuid( mixer.Id ) is not null ) return mixer;

		mixer = parent.AddChild();
		mixer.Name = $"{parent.Name} {SoundClasses.Names[soundClass]}";
		mixer.Spatializing = parent.Spatializing;
		mixer.DistanceAttenuation = parent.DistanceAttenuation;
		mixer.Occlusion = parent.Occlusion;
		mixer.Reverb = parent.Reverb;
		mixer.AirAbsorption = parent.AirAbsorption;
		mixer.MaxVoices = parent.MaxVoices;
		mixer.Volume = Volume( soundClass );
		_mixers[(parent, soundClass)] = mixer;
		return mixer;
	}
}
