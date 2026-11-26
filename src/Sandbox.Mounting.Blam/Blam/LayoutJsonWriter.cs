using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Blam.IO;

namespace Blam;

/// <summary>
/// Writes tag data as JSON, following a <see cref="TagStruct"/> layout. Each game's writer says how
/// its cache stores tag references and string ids.
/// </summary>
/// <remarks>
/// Values are written as the tag stores them, in Halo's units (world units, seconds, and per tick
/// where the game converts a value when it builds the map), with these exceptions: angles are in
/// degrees; enums are the option's name; bit fields are an array of the names of the set bits
/// ("bit 5" for a bit without a name); bounds, points, vectors and colors are arrays (colors
/// <c>[a, r, g, b]</c> or <c>[r, g, b]</c>); a block index of -1 is null; a tag reference is the
/// tag's path with forward slashes and its class (<c>weapons/pistol/bullet.jpt!</c>), or null; a
/// string id is its string.
/// NaN and infinite floats, which JSON can't hold, are written as 0.
/// </remarks>
public abstract class LayoutJsonWriter
{
	/// <summary>
	/// Indented, and only escaping what JSON requires (so "doesn't" stays readable).
	/// </summary>
	public static readonly JsonWriterOptions Indented = new() { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

	/// <summary>
	/// Blocks claiming more elements than this are treated as corrupt and written empty.
	/// </summary>
	const int MaximumBlockCount = 0x10000;

	readonly TagFormat _format;

	/// <summary>
	/// The map's tag data, for <see cref="TagStruct.Extra"/> writers that read more than fields.
	/// </summary>
	public MemoryRegion Data { get; }

	/// <summary>
	/// Where the tag being written starts, for fields that look things up elsewhere in it
	/// (<see cref="FieldKind.PaletteReference"/>).
	/// </summary>
	public uint TagAddress { get; set; }

	protected LayoutJsonWriter( MemoryRegion data, TagFormat format )
	{
		Data = data;
		_format = format;
	}

	// Writes the tag reference at the address as the tag's name, or null.
	protected abstract void WriteReference( Utf8JsonWriter json, uint address );

	// The text of a string id.
	protected abstract string StringIdText( uint id );

	/// <summary>
	/// Writes one structure at <paramref name="address"/> as a JSON object.
	/// </summary>
	public void WriteStruct( Utf8JsonWriter json, TagStruct layout, uint address )
	{
		json.WriteStartObject();
		WriteFields( json, layout, address );
		layout.Extra?.Invoke( json, this, address );
		json.WriteEndObject();
	}

	/// <summary>
	/// Writes the fields of a structure as properties of the JSON object being written.
	/// </summary>
	public void WriteFields( Utf8JsonWriter json, TagStruct layout, uint address )
	{
		foreach ( var field in layout.Fields )
		{
			json.WritePropertyName( field.JsonName );
			WriteFieldValue( json, field, address + (uint)field.Offset );
		}
	}

	/// <summary>
	/// Writes one field of the structure at <paramref name="address"/> as a JSON value.
	/// </summary>
	public void WriteValue( Utf8JsonWriter json, TagField field, uint address ) => WriteFieldValue( json, field, address + (uint)field.Offset );

	/// <summary>
	/// Writes the block at <paramref name="address"/> as a JSON array of <paramref name="element"/>
	/// objects, each starting with its slot name if the block is one of slots (<see cref="TagStruct.SlotNames"/>).
	/// </summary>
	public void WriteBlock( Utf8JsonWriter json, TagStruct element, uint address )
	{
		json.WriteStartArray();
		var index = 0;
		foreach ( var entry in BlockElements( address, element.Size ) )
		{
			if ( element.SlotNames is { } slots )
			{
				json.WriteStartObject();
				json.WriteString( "Slot", index < slots.Count ? slots[index] : $"slot {index}" );
				WriteFields( json, element, entry );
				element.Extra?.Invoke( json, this, entry );
				json.WriteEndObject();
			}
			else
			{
				WriteStruct( json, element, entry );
			}

			index++;
		}
		json.WriteEndArray();
	}

	/// <summary>
	/// The addresses of a block's elements, or none if the block doesn't point into the tag data.
	/// </summary>
	public IEnumerable<uint> BlockElements( uint address, int elementSize )
	{
		var count = Data.I32( address );
		var first = Data.U32( address + 4 );
		if ( count <= 0 || count > MaximumBlockCount || !Data.Contains( first, count * elementSize ) )
			yield break;

		for ( var i = 0; i < count; i++ )
			yield return first + (uint)(i * elementSize);
	}

	void WriteFieldValue( Utf8JsonWriter json, TagField field, uint address )
	{
		var d = Data;
		switch ( field.Kind )
		{
			case FieldKind.Int8: json.WriteNumberValue( (sbyte)d.U8( address ) ); break;
			case FieldKind.UInt8: json.WriteNumberValue( d.U8( address ) ); break;
			case FieldKind.Int16: json.WriteNumberValue( d.I16( address ) ); break;
			case FieldKind.UInt16: json.WriteNumberValue( d.U16( address ) ); break;
			case FieldKind.Int32: json.WriteNumberValue( d.I32( address ) ); break;
			case FieldKind.UInt32: json.WriteNumberValue( d.U32( address ) ); break;
			case FieldKind.Float: WriteFloat( json, d.F32( address ) ); break;
			case FieldKind.Angle: WriteDegrees( json, d.F32( address ) ); break;
			case FieldKind.Int16Bounds: WriteInts( json, d.I16( address ), d.I16( address + 2 ) ); break;
			case FieldKind.Point2Int: WriteInts( json, d.I16( address ), d.I16( address + 2 ) ); break;
			case FieldKind.FloatBounds or FieldKind.Point2: WriteFloats( json, address, 2 ); break;
			case FieldKind.Point3 or FieldKind.ColorRgb: WriteFloats( json, address, 3 ); break;
			case FieldKind.ColorArgb or FieldKind.Quaternion or FieldKind.Vector4: WriteFloats( json, address, 4 ); break;
			case FieldKind.Struct: WriteStruct( json, field.Struct, address ); break;

			case FieldKind.ColorArgbInt:
				var color = d.U32( address );
				json.WriteStartArray();
				json.WriteNumberValue( color >> 24 );
				json.WriteNumberValue( (color >> 16) & 0xFF );
				json.WriteNumberValue( (color >> 8) & 0xFF );
				json.WriteNumberValue( color & 0xFF );
				json.WriteEndArray();
				break;

			case FieldKind.Rectangle2D:
				json.WriteStartArray();
				for ( var i = 0; i < 4; i++ )
					json.WriteNumberValue( d.I16( address + (uint)(i * 2) ) );
				json.WriteEndArray();
				break;
			case FieldKind.AngleBounds or FieldKind.Euler2: WriteAngles( json, address, 2 ); break;
			case FieldKind.Euler3: WriteAngles( json, address, 3 ); break;
			case FieldKind.String: json.WriteStringValue( d.CString( address, 32 ) ); break;

			case FieldKind.Index:
				var index = d.I16( address );
				if ( index < 0 )
					json.WriteNullValue();
				else
					json.WriteNumberValue( index );
				break;

			case FieldKind.ByteIndex:
				var byteIndex = d.U8( address );
				if ( byteIndex == 0xFF )
					json.WriteNullValue();
				else
					json.WriteNumberValue( byteIndex );
				break;

			case FieldKind.Enum:
				var option = field.Width switch { 8 => d.U8( address ), 32 => d.U32( address ), _ => d.U16( address ) };
				json.WriteStringValue( option < field.Options.Count ? field.Options[(int)option] : $"option {option}" );
				break;

			case FieldKind.Flags:
				var bits = field.Width switch { 8 => d.U8( address ), 16 => d.U16( address ), _ => d.U32( address ) };
				json.WriteStartArray();
				for ( var bit = 0; bit < field.Width; bit++ )
				{
					if ( (bits & (1u << bit)) != 0 )
						json.WriteStringValue( bit < field.Options.Count ? field.Options[bit] : $"bit {bit}" );
				}
				json.WriteEndArray();
				break;

			case FieldKind.Reference:
				WriteReference( json, address );
				break;

			case FieldKind.PaletteReference:
				var paletteIndex = d.I16( address );
				var palette = (uint)(TagAddress + field.Width);
				var entry = paletteIndex < 0 ? 0 : BlockElement( palette, _format.PaletteEntrySize, paletteIndex );
				if ( entry == 0 )
					json.WriteNullValue();
				else
					WriteReference( json, entry );
				break;

			case FieldKind.Block:
				WriteBlock( json, field.Struct, address );
				break;

			case FieldKind.AsciiData:
			case FieldKind.Utf16Data:
				var text = ReadText( address, field.Kind == FieldKind.Utf16Data );
				if ( text is null )
					json.WriteNullValue();
				else
					json.WriteStringValue( text );
				break;

			case FieldKind.ByteData:
				var size = d.I32( address );
				var pointer = d.U32( address + (uint)_format.DataPointerOffset );
				if ( size <= 0 || !d.Contains( pointer, size ) )
					json.WriteNullValue();
				else
					json.WriteBase64StringValue( d.Slice( pointer, size ) );
				break;

			case FieldKind.StringId:
				json.WriteStringValue( StringIdText( d.U32( address ) ) );
				break;

			default:
				throw new InvalidOperationException( $"Can't write {field.Kind}" );
		}
	}

	/// <summary>
	/// The address of element <paramref name="index"/> of a block, or 0 if it doesn't have that many.
	/// </summary>
	uint BlockElement( uint address, int elementSize, int index )
	{
		var count = Data.I32( address );
		var first = Data.U32( address + 4 );
		var element = first + (uint)(index * elementSize);
		return index < count && Data.Contains( element, elementSize ) ? element : 0;
	}

	/// <summary>
	/// Reads the string in a data reference, up to its first null.
	/// </summary>
	string ReadText( uint address, bool utf16 )
	{
		var size = Data.I32( address );
		var pointer = Data.U32( address + (uint)_format.DataPointerOffset );
		if ( size <= 0 || !Data.Contains( pointer, size ) )
			return size == 0 ? "" : null;

		var bytes = Data.Slice( pointer, size );
		if ( !utf16 )
		{
			var end = bytes.IndexOf( (byte)0 );
			return Encoding.Latin1.GetString( end < 0 ? bytes : bytes[..end] );
		}

		var length = 0;
		while ( length + 1 < bytes.Length && (bytes[length] != 0 || bytes[length + 1] != 0) )
			length += 2;

		return Encoding.Unicode.GetString( bytes[..length] );
	}

	void WriteFloats( Utf8JsonWriter json, uint address, int count )
	{
		json.WriteStartArray();
		for ( var i = 0; i < count; i++ )
			WriteFloat( json, Data.F32( address + (uint)(i * 4) ) );
		json.WriteEndArray();
	}

	void WriteAngles( Utf8JsonWriter json, uint address, int count )
	{
		json.WriteStartArray();
		for ( var i = 0; i < count; i++ )
			WriteDegrees( json, Data.F32( address + (uint)(i * 4) ) );
		json.WriteEndArray();
	}

	static void WriteInts( Utf8JsonWriter json, int a, int b )
	{
		json.WriteStartArray();
		json.WriteNumberValue( a );
		json.WriteNumberValue( b );
		json.WriteEndArray();
	}

	// Writes a float, 0 if it's NaN or infinite.
	public static void WriteFloat( Utf8JsonWriter json, float value ) => json.WriteNumberValue( float.IsFinite( value ) ? value : 0f );

	/// <summary>
	/// Radians to degrees, rounded to 1/10000 of a degree so angles typed in degrees come back
	/// as typed (6, not 6.0000005).
	/// </summary>
	public static float Degrees( float radians ) => (float)Math.Round( radians * (180.0 / Math.PI), 4 );

	static void WriteDegrees( Utf8JsonWriter json, float radians ) => WriteFloat( json, Degrees( radians ) );
}
