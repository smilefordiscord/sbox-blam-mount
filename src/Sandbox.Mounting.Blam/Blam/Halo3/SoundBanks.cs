using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using Blam.Audio;

namespace Blam.Halo3;

// The languages MCC ships Halo 3 dialog banks for (halo3/fmod/pc/<language>.fsb).
public enum SoundLanguage
{
	English,
	French,
	German,
	Italian,
	Mexican,
	Spanish,
}

// A decoded bank sample: interleaved 16 bit PCM, at most stereo.
public sealed record DecodedSamples( short[] Pcm, int Channels, int SampleRate );

// MCC's Halo 3 FMOD banks: sfx.fsb plus one bank per language, each with a .fsb.info sidecar whose records (same order as the bank) start with the FMOD subsound hash that permutations store.
public sealed class SoundBanks : IDisposable
{
	const int InfoRecordSize = 0x118;

	// FMOD's two Vorbis setup headers, found in halo3.dll with 2 bytes changed each: id = CRC32, length, the bytes to restore.
	static readonly (uint Id, int Length, (int Offset, byte Value)[] Patches)[] KnownSetups =
	[
		(0x632B1FAC, 3006, [(2934, 0x00), (2962, 0x05)]),
		(0xAD11D38D, 3484, [(3405, 0x58), (3435, 0x0B)]),
	];

	readonly string _directory;
	readonly string _dllPath;
	readonly object _lock = new();
	readonly Dictionary<uint, (bool Language, int Index)> _index = new();
	readonly Dictionary<string, Fsb5Bank> _banks = new( StringComparer.OrdinalIgnoreCase );
	FsbVorbisSetups _setups;
	bool _indexed;

	public SoundLanguage Language { get; }

	// fmodDirectory is halo3/fmod/pc, dllPath halo3/halo3.dll.
	public SoundBanks( string fmodDirectory, string dllPath, SoundLanguage language = SoundLanguage.English )
	{
		_directory = fmodDirectory;
		_dllPath = dllPath;
		Language = language;
	}

	public static string LanguageBank( SoundLanguage language ) => language.ToString().ToLowerInvariant();

	public bool Exists => File.Exists( Path.Combine( _directory, "sfx.fsb" ) ) && File.Exists( Path.Combine( _directory, "sfx.fsb.info" ) );

	// The bank and sample a permutation's hash names (sfx first, else the language bank), or null.
	public (Fsb5Bank Bank, FsbSample Sample)? Find( uint hash )
	{
		EnsureIndex();
		if ( !_index.TryGetValue( hash, out var entry ) )
			return null;

		var bank = GetBank( entry.Language ? LanguageBank( Language ) : "sfx" );
		return entry.Index < bank.Samples.Count ? (bank, bank.Samples[entry.Index]) : null;
	}

	public bool Contains( uint hash )
	{
		EnsureIndex();
		return _index.ContainsKey( hash );
	}

	public DecodedSamples Decode( uint hash )
	{
		var found = Find( hash ) ?? throw new KeyNotFoundException( $"No FMOD sample with hash {hash:X8}" );
		var pcm = found.Bank.Decode( found.Sample, out var channels );
		return new DecodedSamples( pcm, channels, found.Sample.SampleRate );
	}

	public Fsb5Bank GetBank( string name )
	{
		lock ( _lock )
		{
			if ( _banks.TryGetValue( name, out var bank ) )
				return bank;

			bank = new Fsb5Bank( Path.Combine( _directory, $"{name}.fsb" ) ) { VorbisSetups = GetSetups() };
			_banks[name] = bank;
			return bank;
		}
	}

	public FsbVorbisSetups GetSetups()
	{
		lock ( _lock )
		{
			if ( _setups is not null )
				return _setups;

			var setups = new FsbVorbisSetups();
			var image = File.ReadAllBytes( _dllPath );
			foreach ( var (id, length, patches) in KnownSetups )
			{
				if ( !setups.AddFromImage( image, id, length, patches ) )
					throw new InvalidDataException( $"Couldn't find FMOD Vorbis setup header {id:X8} in {_dllPath}" );
			}

			return _setups = setups;
		}
	}

	void EnsureIndex()
	{
		lock ( _lock )
		{
			if ( _indexed )
				return;

			ReadInfo( "sfx", false );
			ReadInfo( LanguageBank( Language ), true );
			_indexed = true;
		}
	}

	void ReadInfo( string bank, bool language )
	{
		var path = Path.Combine( _directory, $"{bank}.fsb.info" );
		if ( !File.Exists( path ) )
			return;

		var info = File.ReadAllBytes( path );
		for ( var i = 0; i + InfoRecordSize <= info.Length; i += InfoRecordSize )
			_index.TryAdd( BinaryPrimitives.ReadUInt32LittleEndian( info.AsSpan( i ) ), (language, i / InfoRecordSize) );
	}

	public void Dispose()
	{
		lock ( _lock )
		{
			foreach ( var bank in _banks.Values )
				bank.Dispose();
			_banks.Clear();
		}
	}
}
