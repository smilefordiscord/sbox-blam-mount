using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Blam.Halo1;

/// <summary>
/// One entry of MCC's sound tag parameters: the tag (or tag and pitch range) it applies to, and the
/// values of it the mount uses.
/// </summary>
/// <param name="Name">Tag path, <c>&lt;tag path&gt;\&lt;pitch range name&gt;</c>, or a pattern with <c>*</c>.</param>
/// <param name="Volume">Volume change in dB, or null if the entry has none.</param>
/// <param name="MinDistance">Minimum distance in metres, or null.</param>
public sealed record SoundTagParam( string Name, float? Volume, float? MinDistance )
{
	/// <summary>
	/// The volume as a gain. MCC reads a missing volume as 1 dB, so naming a tag at all turns it up
	/// a little.
	/// </summary>
	public float Gain => MathF.Pow( 10, (Volume ?? 1) / 20 );

	/// <summary>
	/// The minimum distance in world units (10 feet), or null.
	/// </summary>
	public float? MinimumDistance => MinDistance / 3.048f;
}

/// <summary>
/// MCC's per-tag sound settings, <c>halo1/sound/pc/lst/tags_params.ps</c>: volume changes,
/// minimum distances and other playback options on top of what the tags say.
/// </summary>
/// <remarks>
/// The file is a list of brace blocks. Top level ones without a name are tag entries, each with a
/// <c>name = "..."</c> and settings as <c>key = value</c> lines; named blocks (<c>cinePresets</c>,
/// <c>preset_n = { }</c>) and blocks inside an entry (<c>panLevel</c>, <c>replace</c>...) hold other
/// things. Strings escape backslashes, and <c>//</c> starts a comment.
///
/// An entry for an exact name applies to that name. Otherwise the last pattern entry that matches
/// does (the file's wildcard section warns that a later one can override an earlier one).
/// </remarks>
public sealed class SoundTagParams
{
	readonly Dictionary<string, SoundTagParam> _exact = new( StringComparer.OrdinalIgnoreCase );
	readonly List<(Regex Pattern, SoundTagParam Param)> _patterns = new();

	public IReadOnlyCollection<SoundTagParam> Exact => _exact.Values;
	public IEnumerable<SoundTagParam> Patterns
	{
		get
		{
			foreach ( var (_, param) in _patterns )
				yield return param;
		}
	}

	/// <summary>
	/// Reads <c>lst/tags_params.ps</c> under <c>halo1/sound/pc</c>, or returns null if it isn't there.
	/// </summary>
	public static SoundTagParams Load( string soundDirectory )
	{
		var path = Path.Combine( soundDirectory, "lst", "tags_params.ps" );
		return File.Exists( path ) ? Parse( File.ReadAllText( path ) ) : null;
	}

	public static SoundTagParams Parse( string text )
	{
		var result = new SoundTagParams();
		var tokens = Tokenize( text );

		// Depth of nested blocks, and the settings of the tag entry being read (depth 1).
		var depth = 0;
		var entry = false;
		string name = null;
		float? volume = null, minDistance = null;

		for ( var i = 0; i < tokens.Count; i++ )
		{
			var token = tokens[i];
			if ( token.Text == "{" && !token.Quoted )
			{
				// A tag entry is a block at the top level with no name before it.
				var named = i > 0 && !tokens[i - 1].Quoted && tokens[i - 1].Text is not ("{" or "}");
				if ( depth == 0 && !named )
				{
					entry = true;
					name = null;
					volume = minDistance = null;
				}

				depth++;
				continue;
			}

			if ( token.Text == "}" && !token.Quoted )
			{
				depth = Math.Max( 0, depth - 1 );
				if ( depth == 0 && entry )
				{
					entry = false;
					if ( !string.IsNullOrEmpty( name ) )
						result.Add( new SoundTagParam( name, volume, minDistance ) );
				}

				continue;
			}

			// key = value, in the entry itself (not its inner blocks).
			if ( !entry || depth != 1 || token.Quoted || i + 2 >= tokens.Count || tokens[i + 1].Text != "=" || tokens[i + 1].Quoted )
				continue;

			var value = tokens[i + 2];
			switch ( token.Text.ToLowerInvariant() )
			{
				case "name":
					name = value.Text;
					break;
				case "volume":
					volume = Number( value.Text );
					break;
				case "mindistance":
					minDistance = Number( value.Text );
					break;
			}

			i += 2;
		}

		return result;
	}

	void Add( SoundTagParam param )
	{
		if ( !param.Name.Contains( '*' ) )
		{
			_exact[param.Name] = param;
			return;
		}

		var pattern = "^" + Regex.Escape( param.Name ).Replace( "\\*", ".*" ) + "$";
		_patterns.Add( (new Regex( pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant ), param) );
	}

	/// <summary>
	/// The entry for a tag path (or <c>&lt;tag path&gt;\&lt;pitch range name&gt;</c>), or null.
	/// </summary>
	public SoundTagParam Find( string name )
	{
		if ( string.IsNullOrEmpty( name ) )
			return null;

		if ( _exact.TryGetValue( name, out var exact ) )
			return exact;

		for ( var i = _patterns.Count - 1; i >= 0; i-- )
		{
			if ( _patterns[i].Pattern.IsMatch( name ) )
				return _patterns[i].Param;
		}

		return null;
	}

	/// <summary>
	/// The entry for a sound tag playing one of its pitch ranges: the range's own if one names it
	/// exactly, else the tag's. Patterns are for tag paths: <c>footsteps\cyborg\*</c> would also
	/// match every range of the jump sounds that <c>footsteps\*\jump</c> is meant for.
	/// </summary>
	public SoundTagParam Find( string tagPath, string pitchRangeName ) =>
		(string.IsNullOrEmpty( pitchRangeName ) ? null : _exact.GetValueOrDefault( $"{tagPath}\\{pitchRangeName}" )) ?? Find( tagPath );

	static float? Number( string text ) =>
		float.TryParse( text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value ) ? value : null;

	readonly record struct Token( string Text, bool Quoted );

	static List<Token> Tokenize( string text )
	{
		var tokens = new List<Token>();
		var i = 0;
		while ( i < text.Length )
		{
			var c = text[i];
			if ( char.IsWhiteSpace( c ) )
			{
				i++;
			}
			else if ( c == '/' && i + 1 < text.Length && text[i + 1] == '/' )
			{
				while ( i < text.Length && text[i] != '\n' )
					i++;
			}
			else if ( c is '{' or '}' or '=' )
			{
				tokens.Add( new Token( c.ToString(), false ) );
				i++;
			}
			else if ( c == '"' )
			{
				var value = new StringBuilder();
				for ( i++; i < text.Length && text[i] != '"'; i++ )
				{
					if ( text[i] == '\\' && i + 1 < text.Length )
						i++;
					value.Append( text[i] );
				}

				tokens.Add( new Token( value.ToString(), true ) );
				i++;
			}
			else
			{
				var start = i;
				while ( i < text.Length && !char.IsWhiteSpace( text[i] ) && text[i] is not ('{' or '}' or '=' or '"') && !(text[i] == '/' && i + 1 < text.Length && text[i + 1] == '/') )
					i++;
				tokens.Add( new Token( text[start..i], false ) );
			}
		}

		return tokens;
	}
}
