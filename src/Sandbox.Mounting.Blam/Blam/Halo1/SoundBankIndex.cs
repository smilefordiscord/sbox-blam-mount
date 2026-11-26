using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Blam.Audio;

namespace Blam.Halo1;

/// <summary>
/// Where MCC keeps a sound tag's samples: a run of consecutive samples in one FSB bank.
/// </summary>
public sealed record SoundBankEntry( Fsb5Bank Bank, int FirstSample, int SampleCount );

/// <summary>
/// MCC's Halo CE audio. The <c>snd!</c> tags in the maps still describe every sound, but their
/// sample data is gone: it lives in FMOD banks in <c>halo1/sound/pc</c>, and a list file per bank in
/// <c>halo1/sound/pc/lst</c> maps tag paths to the bank's samples.
/// </summary>
/// <remarks>
/// <c>&lt;bank&gt;.lst.bin</c>: version (1), entry count, then per entry the tag path's length
/// (including its terminator), the path, the index of its first sample in the bank and its sample
/// count. A tag's samples are its permutations in order, pitch range by pitch range, each named
/// after the permutation (with <c>_&lt;pitch range&gt;_&lt;permutation&gt;</c> appended in some banks).
///
/// The English banks are <c>sounds_adpcm</c> (Xbox ADPCM: the original game's effects, as
/// "old_sfx" paths), <c>sounds_debug</c> (CELT, Anniversary's remade effects, despite the name),
/// <c>sounds_dialogs</c>, <c>sounds_music</c> (both scores: Anniversary's under the tags' paths, the
/// original's as "old_music") and <c>sounds_stream</c> (CELT, long ambient loops and cutscene music).
/// The other dialog banks are translations of <c>sounds_dialogs</c>. See <see cref="ClassicPath"/>.
/// </remarks>
public sealed class SoundBankIndex : IDisposable
{
	/// <summary>
	/// Banks in the order a tag listed in several is looked up in.
	/// </summary>
	static readonly string[] BankNames = ["sounds_debug", "sounds_dialogs", "sounds_music", "sounds_stream", "sounds_adpcm"];

	readonly List<Fsb5Bank> _banks = new();
	readonly Dictionary<string, List<SoundBankEntry>> _entries = new( StringComparer.OrdinalIgnoreCase );

	public IReadOnlyList<Fsb5Bank> Banks => _banks;

	/// <summary>
	/// Every tag path the banks have samples for.
	/// </summary>
	public IEnumerable<string> Paths => _entries.Keys;

	/// <param name="soundDirectory"><c>halo1/sound/pc</c> in the MCC install.</param>
	public SoundBankIndex( string soundDirectory )
	{
		try
		{
			foreach ( var name in BankNames )
			{
				var bankPath = Path.Combine( soundDirectory, name + ".fsb" );
				var listPath = Path.Combine( soundDirectory, "lst", name + ".lst.bin" );
				if ( !File.Exists( bankPath ) || !File.Exists( listPath ) )
					continue;

				var bank = new Fsb5Bank( bankPath );
				_banks.Add( bank );

				foreach ( var (tagPath, first, count) in ReadList( listPath ) )
				{
					if ( first < 0 || count <= 0 || first + count > bank.Samples.Count )
						continue;

					if ( !_entries.TryGetValue( tagPath, out var list ) )
						_entries[tagPath] = list = new List<SoundBankEntry>();

					list.Add( new SoundBankEntry( bank, first, count ) );
				}
			}
		}
		catch
		{
			Dispose();
			throw;
		}
	}

	static IEnumerable<(string TagPath, int First, int Count)> ReadList( string path )
	{
		var data = File.ReadAllBytes( path );
		var span = data.AsSpan();
		var count = BinaryPrimitives.ReadInt32LittleEndian( span[4..] );
		var p = 8;
		var result = new List<(string, int, int)>( count );
		for ( var i = 0; i < count && p + 4 <= data.Length; i++ )
		{
			var length = BinaryPrimitives.ReadInt32LittleEndian( span[p..] );
			p += 4;
			var name = span.Slice( p, length );
			var end = name.IndexOf( (byte)0 );
			var tagPath = Encoding.ASCII.GetString( end < 0 ? name : name[..end] );
			p += length;

			var first = BinaryPrimitives.ReadInt32LittleEndian( span[p..] );
			var samples = BinaryPrimitives.ReadInt32LittleEndian( span[(p + 4)..] );
			p += 8;

			result.Add( (tagPath, first, samples) );
		}

		return result;
	}

	/// <summary>
	/// Whether sounds come from the original game's audio rather than Halo Anniversary's (MCC's
	/// classic/remastered audio choice), wherever MCC has both.
	/// </summary>
	public static bool PreferClassic { get; set; } = true;

	/// <summary>
	/// The path MCC keeps a sound's original (pre-Anniversary) version under: effects
	/// <c>sound\sfx\…</c> as <c>sound\old_sfx\…</c> (in <c>sounds_adpcm</c>, the original Xbox
	/// ADPCM), music <c>sound\music\…</c> as <c>sound\old_music\music\…</c>. Null for anything else.
	/// </summary>
	public static string ClassicPath( string tagPath )
	{
		if ( tagPath.StartsWith( @"sound\sfx\", StringComparison.OrdinalIgnoreCase ) )
			return @"sound\old_sfx\" + tagPath[@"sound\sfx\".Length..];

		if ( tagPath.StartsWith( @"sound\music\", StringComparison.OrdinalIgnoreCase ) )
			return @"sound\old_music\" + tagPath[@"sound\".Length..];

		return null;
	}

	/// <summary>
	/// Finds a sound tag's samples: its original version if there is one and
	/// <see cref="PreferClassic"/> is set, else the tag's own. A few tags are in two banks with
	/// different permutation sets; the one matching the tag's permutation count wins.
	/// </summary>
	public SoundBankEntry Find( string tagPath, int permutationCount )
	{
		if ( PreferClassic && ClassicPath( tagPath ) is { } classic && _entries.ContainsKey( classic ) )
			tagPath = classic;

		if ( !_entries.TryGetValue( tagPath, out var list ) )
			return null;

		foreach ( var entry in list )
		{
			if ( entry.SampleCount == permutationCount )
				return entry;
		}

		return list[0];
	}

	public void Dispose()
	{
		foreach ( var bank in _banks )
			bank.Dispose();

		_banks.Clear();
	}
}
