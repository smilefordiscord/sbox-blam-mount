using System;

namespace Blam;

/// <summary>
/// A four character tag group code such as <c>bitm</c> or <c>sbsp</c>. Cache files store these
/// as little-endian integers, so the characters read back to front on disk.
/// </summary>
public readonly record struct TagClass( uint Value )
{
	public static readonly TagClass None = new( 0xFFFFFFFF );

	public TagClass( string code ) : this( Parse( code ) ) { }

	public bool IsNone => Value == 0xFFFFFFFF || Value == 0;

	static uint Parse( string code )
	{
		if ( code.Length != 4 )
			throw new ArgumentException( $"Tag class must be four characters: '{code}'", nameof( code ) );

		return (uint)(code[0] << 24 | code[1] << 16 | code[2] << 8 | code[3]);
	}

	public override string ToString()
	{
		if ( IsNone ) return "none";

		Span<char> chars = [(char)(Value >> 24), (char)(Value >> 16 & 0xFF), (char)(Value >> 8 & 0xFF), (char)(Value & 0xFF)];
		return new string( chars ).TrimEnd();
	}
}
