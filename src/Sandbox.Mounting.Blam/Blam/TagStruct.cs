using System;
using System.Collections.Generic;
using System.Text;

namespace Blam;

/// <summary>
/// How a tag field is stored, and so how it's written out.
/// </summary>
public enum FieldKind
{
	Int8,
	UInt8,
	Int16,
	UInt16,
	Int32,
	UInt32,
	Float,

	/// <summary>Radians in the tag, degrees in the output.</summary>
	Angle,

	/// <summary>Two int16s, lower and upper.</summary>
	Int16Bounds,
	FloatBounds,

	/// <summary>Two angles, in degrees.</summary>
	AngleBounds,
	Point2,

	/// <summary>Two int16s (a screen position in pixels).</summary>
	Point2Int,
	Point3,

	/// <summary>Yaw and pitch, in degrees.</summary>
	Euler2,

	/// <summary>Yaw, pitch and roll, in degrees.</summary>
	Euler3,

	/// <summary>i, j, k, w as stored (Halo's rotations are conjugated, see ModelAnimationsTag.FromHalo).</summary>
	Quaternion,

	// Four floats as stored, e.g. a plane equation or a Havok vector.
	Vector4,

	/// <summary>Red, green, blue as floats.</summary>
	ColorRgb,

	/// <summary>Alpha, red, green, blue as floats, in that order.</summary>
	ColorArgb,

	/// <summary>A packed 32 bit color (0xAARRGGBB), written as <c>[a, r, g, b]</c> bytes.</summary>
	ColorArgbInt,

	/// <summary>Four int16s: top, left, bottom, right.</summary>
	Rectangle2D,

	/// <summary>Another structure inside this one (<see cref="TagField.Struct"/>), written as an object.</summary>
	Struct,

	/// <summary>A 32 byte, null-terminated ASCII string.</summary>
	String,

	/// <summary>An int16 index into a block, with -1 for none (written as null).</summary>
	Index,

	/// <summary>A byte index into a block, with 255 for none (written as null).</summary>
	ByteIndex,

	/// <summary>An option of <see cref="TagField.Width"/> bits (16 when 0), written as its name.</summary>
	Enum,

	/// <summary>A bit field of <see cref="TagField.Width"/> bits, written as the names of the set bits.</summary>
	Flags,

	/// <summary>A tag reference, written as <c>path/of/tag.class</c>.</summary>
	Reference,

	/// <summary>A block (count, pointer), written as an array of <see cref="TagField.Struct"/>.</summary>
	Block,

	/// <summary>A data reference holding a null-terminated 8 bit (Latin-1) string.</summary>
	AsciiData,

	/// <summary>A data reference holding a null-terminated UTF-16 string.</summary>
	Utf16Data,

	/// <summary>A data reference, written as its bytes in base64 (null when empty).</summary>
	ByteData,

	/// <summary>
	/// An int16 index into a palette block of the tag (at <see cref="TagField.Width"/> from the
	/// start of the tag, a tag reference at the start of each entry), written as the tag it picks.
	/// </summary>
	PaletteReference,

	// A 32 bit string id (Halo 2 on), written as its string.
	StringId,
}

// The sizes of the structures a cache format stores tag data in.
public sealed record TagFormat( int BlockSize, int ReferenceSize, int DataSize, int DataPointerOffset, int PaletteEntrySize )
{
	// Halo CE: blocks are count, pointer, definition; references class, path, path length, id; data size ... pointer.
	public static readonly TagFormat Halo1 = new( 12, 16, 20, 0xC, 0x30 );

	// Halo 2: blocks are count, pointer; references class, id; data references size, pointer.
	public static readonly TagFormat Halo2 = new( 8, 8, 8, 0x4, 0x28 );
}

/// <summary>
/// One field of a tag structure.
/// </summary>
/// <param name="Name">Halo's own name for the field, e.g. "rounds loaded maximum".</param>
/// <param name="Offset">Bytes from the start of the structure.</param>
/// <param name="Width">Bits of a <see cref="FieldKind.Flags"/> field; tag offset of a <see cref="FieldKind.PaletteReference"/>'s palette.</param>
/// <param name="Options">Names of an enum's options, or of a bit field's bits from bit 0 up.</param>
/// <param name="Struct">The element structure of a block.</param>
public sealed record TagField( string Name, int Offset, FieldKind Kind, int Width = 0, IReadOnlyList<string> Options = null, TagStruct Struct = null )
{
	/// <summary>
	/// The field's name in JSON: the tag field name in PascalCase ("rounds loaded maximum" is
	/// <c>RoundsLoadedMaximum</c>, "don't drop grenades chance" is <c>DontDropGrenadesChance</c>).
	/// </summary>
	public string JsonName { get; } = PascalCase( Name );

	/// <summary>
	/// Bytes the field takes up in its structure, in a cache of the given format.
	/// </summary>
	public int Size( TagFormat format ) => Kind switch
	{
		FieldKind.Int8 or FieldKind.UInt8 or FieldKind.ByteIndex => 1,
		FieldKind.Int16 or FieldKind.UInt16 or FieldKind.Index or FieldKind.PaletteReference => 2,
		FieldKind.Enum => Width == 0 ? 2 : Width / 8,
		FieldKind.Int32 or FieldKind.UInt32 or FieldKind.Float or FieldKind.Angle or FieldKind.Int16Bounds or FieldKind.Point2Int or FieldKind.ColorArgbInt or FieldKind.StringId => 4,
		FieldKind.FloatBounds or FieldKind.AngleBounds or FieldKind.Point2 or FieldKind.Euler2 or FieldKind.Rectangle2D => 8,
		FieldKind.Point3 or FieldKind.Euler3 or FieldKind.ColorRgb => 12,
		FieldKind.ColorArgb or FieldKind.Quaternion or FieldKind.Vector4 => 16,
		FieldKind.Block => format.BlockSize,
		FieldKind.Reference => format.ReferenceSize,
		FieldKind.AsciiData or FieldKind.Utf16Data or FieldKind.ByteData => format.DataSize,
		FieldKind.String => 32,
		FieldKind.Flags => Width / 8,
		FieldKind.Struct => Struct.Size,
		_ => throw new InvalidOperationException( $"No size for {Kind}" ),
	};

	/// <summary>
	/// Splits a field name into words at anything that isn't a letter, digit or apostrophe, and
	/// capitalizes each word.
	/// </summary>
	public static string PascalCase( string name )
	{
		var result = new StringBuilder( name.Length );
		var startOfWord = true;
		foreach ( var c in name )
		{
			if ( c == '\'' )
				continue;

			if ( !char.IsAsciiLetterOrDigit( c ) )
			{
				startOfWord = true;
				continue;
			}

			result.Append( startOfWord ? char.ToUpperInvariant( c ) : c );
			startOfWord = false;
		}

		return result.ToString();
	}
}

/// <summary>
/// The layout of a tag, or of one element of a tag block: which fields are where. Only the fields
/// worth reading are listed; anything else (padding, runtime pointers, editor-only data) is
/// skipped. A tag type that extends another (a weapon is an item is an object) starts with all of
/// its parent's fields, at the same offsets.
/// </summary>
/// <remarks>
/// Written as a builder, so a layout reads top to bottom like the tag:
/// <code>
/// new TagStruct( "magazine object", 0x1C )
///     .Int16( 0x0, "rounds" )
///     .Reference( 0xC, "equipment" );
/// </code>
/// </remarks>
public sealed class TagStruct
{
	readonly List<TagField> _fields = new();

	/// <summary>
	/// What the structure is, for messages ("weapon", "weapon trigger").
	/// </summary>
	public string Name { get; }

	/// <summary>
	/// Bytes per structure: the size of a whole tag, or of each element of a block.
	/// </summary>
	public int Size { get; }

	public IReadOnlyList<TagField> Fields => _fields;

	/// <summary>
	/// Writes properties worked out from the data (rather than read from a field) after the
	/// fields, e.g. a collision node's bounds. Given the JSON writer, the tag writer and the
	/// structure's address.
	/// </summary>
	public Action<System.Text.Json.Utf8JsonWriter, LayoutJsonWriter, uint> Extra { get; init; }

	/// <summary>
	/// For a block whose elements are fixed slots (an animation graph's "idle", "steering", ...):
	/// the name of each slot by element index, written first in each element as <c>"Slot"</c>
	/// (<c>"slot 52"</c> past the end of the list).
	/// </summary>
	public IReadOnlyList<string> SlotNames { get; init; }

	public TagStruct( string name, int size, TagStruct parent = null )
	{
		Name = name;
		Size = size;

		if ( parent is not null )
		{
			_fields.AddRange( parent.Fields );
			Extra = parent.Extra;
		}
	}

	public TagStruct Add( TagField field )
	{
		_fields.Add( field );
		return this;
	}

	public TagStruct Int8( int offset, string name ) => Add( new( name, offset, FieldKind.Int8 ) );
	public TagStruct UInt8( int offset, string name ) => Add( new( name, offset, FieldKind.UInt8 ) );
	public TagStruct Int16( int offset, string name ) => Add( new( name, offset, FieldKind.Int16 ) );
	public TagStruct UInt16( int offset, string name ) => Add( new( name, offset, FieldKind.UInt16 ) );
	public TagStruct Int32( int offset, string name ) => Add( new( name, offset, FieldKind.Int32 ) );
	public TagStruct UInt32( int offset, string name ) => Add( new( name, offset, FieldKind.UInt32 ) );
	public TagStruct Float( int offset, string name ) => Add( new( name, offset, FieldKind.Float ) );
	public TagStruct Angle( int offset, string name ) => Add( new( name, offset, FieldKind.Angle ) );
	public TagStruct Int16Bounds( int offset, string name ) => Add( new( name, offset, FieldKind.Int16Bounds ) );
	public TagStruct FloatBounds( int offset, string name ) => Add( new( name, offset, FieldKind.FloatBounds ) );
	public TagStruct AngleBounds( int offset, string name ) => Add( new( name, offset, FieldKind.AngleBounds ) );
	public TagStruct Point2( int offset, string name ) => Add( new( name, offset, FieldKind.Point2 ) );
	public TagStruct Point2Int( int offset, string name ) => Add( new( name, offset, FieldKind.Point2Int ) );
	public TagStruct Point3( int offset, string name ) => Add( new( name, offset, FieldKind.Point3 ) );
	public TagStruct Euler2( int offset, string name ) => Add( new( name, offset, FieldKind.Euler2 ) );
	public TagStruct Euler3( int offset, string name ) => Add( new( name, offset, FieldKind.Euler3 ) );
	public TagStruct Quaternion( int offset, string name ) => Add( new( name, offset, FieldKind.Quaternion ) );
	public TagStruct Vector4( int offset, string name ) => Add( new( name, offset, FieldKind.Vector4 ) );
	public TagStruct ColorRgb( int offset, string name ) => Add( new( name, offset, FieldKind.ColorRgb ) );
	public TagStruct ColorArgb( int offset, string name ) => Add( new( name, offset, FieldKind.ColorArgb ) );
	public TagStruct ColorArgbInt( int offset, string name ) => Add( new( name, offset, FieldKind.ColorArgbInt ) );
	public TagStruct Rectangle2D( int offset, string name ) => Add( new( name, offset, FieldKind.Rectangle2D ) );
	public TagStruct Struct( int offset, string name, TagStruct inner ) => Add( new( name, offset, FieldKind.Struct, Struct: inner ) );
	public TagStruct String( int offset, string name ) => Add( new( name, offset, FieldKind.String ) );
	public TagStruct Index( int offset, string name ) => Add( new( name, offset, FieldKind.Index ) );
	public TagStruct ByteIndex( int offset, string name ) => Add( new( name, offset, FieldKind.ByteIndex ) );
	public TagStruct Enum( int offset, string name, IReadOnlyList<string> options ) => Add( new( name, offset, FieldKind.Enum, Options: options ) );
	public TagStruct Enum8( int offset, string name, IReadOnlyList<string> options ) => Add( new( name, offset, FieldKind.Enum, 8, options ) );
	public TagStruct Enum32( int offset, string name, IReadOnlyList<string> options ) => Add( new( name, offset, FieldKind.Enum, 32, options ) );
	public TagStruct Flags8( int offset, string name, IReadOnlyList<string> bits ) => Add( new( name, offset, FieldKind.Flags, 8, bits ) );
	public TagStruct Flags16( int offset, string name, IReadOnlyList<string> bits ) => Add( new( name, offset, FieldKind.Flags, 16, bits ) );
	public TagStruct Flags32( int offset, string name, IReadOnlyList<string> bits ) => Add( new( name, offset, FieldKind.Flags, 32, bits ) );
	public TagStruct Reference( int offset, string name ) => Add( new( name, offset, FieldKind.Reference ) );
	public TagStruct Block( int offset, string name, TagStruct element ) => Add( new( name, offset, FieldKind.Block, Struct: element ) );
	public TagStruct AsciiData( int offset, string name ) => Add( new( name, offset, FieldKind.AsciiData ) );
	public TagStruct Utf16Data( int offset, string name ) => Add( new( name, offset, FieldKind.Utf16Data ) );
	public TagStruct ByteData( int offset, string name ) => Add( new( name, offset, FieldKind.ByteData ) );
	public TagStruct PaletteReference( int offset, string name, int paletteOffset ) => Add( new( name, offset, FieldKind.PaletteReference, paletteOffset ) );
	public TagStruct StringId( int offset, string name ) => Add( new( name, offset, FieldKind.StringId ) );

	/// <summary>
	/// Adds fields with a helper, keeping the builder chain going.
	/// </summary>
	public TagStruct Apply( Func<TagStruct, TagStruct> add ) => add( this );

	/// <summary>
	/// The field with this (Halo) name.
	/// </summary>
	public TagField Field( string name )
	{
		foreach ( var field in _fields )
		{
			if ( field.Name == name )
				return field;
		}

		throw new ArgumentException( $"{Name} has no field \"{name}\"", nameof( name ) );
	}

	/// <summary>
	/// Problems with the layout itself: fields outside the structure, fields overlapping each
	/// other or two fields with the same name, with the sizes of a cache of the given format.
	/// Checked by BlamInspect's <c>layouts</c> command.
	/// </summary>
	public IEnumerable<string> Validate( TagFormat format )
	{
		var names = new HashSet<string>();
		TagField previous = null;
		foreach ( var field in _fields )
		{
			var size = field.Size( format );
			if ( field.Offset < 0 || field.Offset + size > Size )
				yield return $"{Name}: {field.Name} at 0x{field.Offset:X} (+{size}) is outside its {Size} bytes";

			if ( previous is not null && field.Offset < previous.Offset + previous.Size( format ) )
				yield return $"{Name}: {field.Name} at 0x{field.Offset:X} overlaps {previous.Name} at 0x{previous.Offset:X} or comes before it";

			if ( !names.Add( field.JsonName ) )
				yield return $"{Name}: two fields named {field.JsonName}";

			if ( field.Kind is FieldKind.Block or FieldKind.Struct )
			{
				foreach ( var problem in field.Struct.Validate( format ) )
					yield return problem;
			}

			previous = field;
		}
	}
}
