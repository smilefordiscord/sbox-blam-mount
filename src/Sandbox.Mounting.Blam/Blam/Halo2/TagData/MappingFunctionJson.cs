using System;
using System.Collections.Generic;
using System.Text.Json;
using Blam.Halo2.Tags;

namespace Blam.Halo2.TagData;

// Mapping functions as JSON a game can evaluate without Halo's function code.
// The curve is sampled at 65 even steps of its input (x = i / 64), to interpolate linearly between samples.
// A range function blends Curve and RangeCurve by its range input.
// The output is Minimum + (Maximum - Minimum) * curve, or for a colour function a gradient through Colors at even steps.
public static class MappingFunctionJson
{
	public const int SampleCount = 65;

	public static void Write( Utf8JsonWriter json, MappingFunction function )
	{
		if ( function.IsEmpty )
		{
			json.WriteNullValue();
			return;
		}

		json.WriteStartObject();
		json.WriteString( "Type", function.TypeName );
		json.WriteBoolean( "Range", function.IsRange );
		if ( function.IsColor )
		{
			json.WriteNull( "Minimum" );
			json.WriteNull( "Maximum" );
		}
		else
		{
			json.WritePropertyName( "Minimum" );
			LayoutJsonWriter.WriteFloat( json, function.Minimum );
			json.WritePropertyName( "Maximum" );
			LayoutJsonWriter.WriteFloat( json, function.Maximum );
		}

		json.WritePropertyName( "Colors" );
		if ( function.IsColor )
		{
			json.WriteStartArray();
			foreach ( var index in GradientStops( function.ColorMode ) )
			{
				var color = function.Color( index );
				json.WriteStartArray();
				json.WriteNumberValue( color >> 24 );
				json.WriteNumberValue( (color >> 16) & 0xFF );
				json.WriteNumberValue( (color >> 8) & 0xFF );
				json.WriteNumberValue( color & 0xFF );
				json.WriteEndArray();
			}
			json.WriteEndArray();
		}
		else
		{
			json.WriteNullValue();
		}

		WriteCurve( json, "Curve", function, 0 );
		if ( function.IsRange )
			WriteCurve( json, "RangeCurve", function, 1 );
		else
			json.WriteNull( "RangeCurve" );
		json.WriteEndObject();
	}

	// Writes a "Decoded" object after a structure's fields: each named function data reference decoded (null when empty).
	public static Action<Utf8JsonWriter, LayoutJsonWriter, uint> WriteDecoded( params (string Name, int Offset)[] functions ) => ( json, writer, address ) =>
	{
		json.WriteStartObject( "Decoded" );
		foreach ( var (name, offset) in functions )
		{
			json.WritePropertyName( name );
			Write( json, new MappingFunction( Read( writer, address + (uint)offset ) ) );
		}
		json.WriteEndObject();
	};

	// The colours a gradient goes through, in order (MappingFunction.ColorAt).
	static IReadOnlyList<int> GradientStops( int colorMode ) => colorMode switch
	{
		1 => [0],
		2 => [0, 3],
		3 => [0, 1, 3],
		_ => [0, 1, 2, 3],
	};

	static void WriteCurve( Utf8JsonWriter json, string name, MappingFunction function, float range )
	{
		json.WriteStartArray( name );
		for ( var i = 0; i < SampleCount; i++ )
			LayoutJsonWriter.WriteFloat( json, MathF.Round( function.Curve( i / (float)(SampleCount - 1), range ), 5 ) );
		json.WriteEndArray();
	}

	// A Halo 2 data reference: size, then pointer.
	static byte[] Read( LayoutJsonWriter writer, uint address )
	{
		var size = writer.Data.I32( address );
		var pointer = writer.Data.U32( address + 4 );
		return size > 0 && writer.Data.Contains( pointer, size ) ? writer.Data.Slice( pointer, size ).ToArray() : [];
	}
}
