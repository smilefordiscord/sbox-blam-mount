using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Blam.Halo2.Tags;

namespace Blam.Halo2.TagData;

// hud.json: everything a game needs to draw Halo 2's HUD (see Hud).
public static partial class TagExport
{
	static readonly TagClass HudDefinition = new( "nhdt" );
	static readonly TagClass Weapon = new( "weap" );
	static readonly TagClass Biped = new( "bipd" );
	static readonly TagClass Vehicle = new( "vehi" );

	// HUD shader templates draw their single pass in this layer.
	const int HudLayer = 1;

	static readonly string[] ScalarModifiers = ["none", "one minus", "half plus half cosine", "half minus half cosine", "half plus half sine", "half minus half sine"];

	// Everything a game needs to draw the HUD, from shared.map and the multiplayer maps; the first map with a tag provides it.
	// The README's Halo 2 section describes the document.
	// Grenades have no HUD of their own: the unit HUD's grenade widgets show them.
	public static byte[] Hud( CacheFile shared, IEnumerable<CacheFile> multiplayerMaps, IReadOnlyList<string> fontTable )
	{
		var sections = new Dictionary<string, Section>();
		Section Get( string name ) => sections.TryGetValue( name, out var section ) ? section : sections[name] = new Section();

		var wanted = new Queue<TagEntry>();
		var writers = new Dictionary<CacheFile, TagJsonWriter>();
		TagJsonWriter WriterFor( CacheFile cache )
		{
			if ( !writers.TryGetValue( cache, out var writer ) )
			{
				writers[cache] = writer = new TagJsonWriter( cache )
				{
					ReferenceWritten = tag =>
					{
						if ( tag.Class == ShaderTag.Class || tag.Class == BitmapTag.Class || tag.Class == StringListTag.Class )
							wanted.Enqueue( tag );
					},
				};
			}

			return writer;
		}

		void AddWanted()
		{
			while ( wanted.TryDequeue( out var tag ) )
			{
				if ( tag.Address == 0 )
					continue;

				if ( tag.Class == ShaderTag.Class && !Get( "Shaders" ).Has( tag ) )
				{
					Get( "Shaders" ).Add( tag, Write( json => WriteHudShader( json, tag ) ) );
					foreach ( var bitmap in new ShaderTag( tag ).Bitmaps.Select( b => b.Bitmap ).Where( b => b is not null ) )
						wanted.Enqueue( bitmap );
				}
				else if ( tag.Class == BitmapTag.Class && !Get( "Bitmaps" ).Has( tag ) )
				{
					Get( "Bitmaps" ).Add( tag, WriteTag( WriterFor( tag.Cache ), tag ) );
				}
				else if ( tag.Class == StringListTag.Class && !Get( "Strings" ).Has( tag ) )
				{
					Get( "Strings" ).Add( tag, Write( json => WriteStrings( json, new StringListTag( tag ), 0 ) ) );
				}
			}
		}

		byte[] globals = null, interfaceTags = null;
		string multiplayerHud = null, scoreboardHud = null;
		var unitHuds = new Dictionary<string, List<string>>();
		var weaponHuds = new Dictionary<string, string>();
		var mapNames = new List<string>();

		void AddMap( CacheFile cache )
		{
			var writer = WriterFor( cache );
			foreach ( var tag in cache.Tags.Where( t => t.Cache == cache && t.Address != 0 && !string.IsNullOrEmpty( t.Path ) ) )
			{
				if ( tag.Class == HudDefinition )
				{
					if ( !Get( "Huds" ).Has( tag ) )
						Get( "Huds" ).Add( tag, WriteTag( writer, tag ) );
				}
				else if ( tag.Class == Weapon )
				{
					weaponHuds.TryAdd( TagJsonWriter.TagName( tag ), TagJsonWriter.TagName( writer.Reference( tag.Address + (uint)TagLayouts.Weapon.Field( "new hud interface" ).Offset ) ) );
				}
				else if ( tag.Class == Biped || tag.Class == Vehicle )
				{
					if ( !unitHuds.ContainsKey( TagJsonWriter.TagName( tag ) ) )
						unitHuds[TagJsonWriter.TagName( tag )] = UnitHuds( writer, tag );
				}
			}

			AddWanted();
		}

		AddMap( shared );
		foreach ( var cache in multiplayerMaps )
		{
			mapNames.Add( cache.Name );
			AddMap( cache );
			writers.Remove( cache );

			if ( globals is not null || cache.Globals is not { } matg )
				continue;

			var writer = WriterFor( matg.Cache );
			var block = TagLayouts.Globals.Field( "interface tags" );
			var first = writer.BlockElements( matg.Address + (uint)block.Offset, block.Struct.Size ).FirstOrDefault();
			if ( first != 0 )
			{
				interfaceTags = Write( json => writer.WriteStruct( json, block.Struct, first ) );
				var hudGlobals = writer.Reference( first + (uint)block.Struct.Field( "hud globals" ).Offset );
				if ( hudGlobals is not null )
					globals = WriteTag( WriterFor( hudGlobals.Cache ), hudGlobals );
			}

			var mulg = writer.Reference( matg.Address + (uint)TagLayouts.Globals.Field( "multiplayer globals" ).Offset );
			if ( mulg is not null )
			{
				var mulgWriter = WriterFor( mulg.Cache );
				var unit = MultiplayerUnit( mulgWriter, mulg );
				multiplayerHud = unit is null ? null : UnitHuds( WriterFor( unit.Cache ), unit ).FirstOrDefault();
				var runtime = TagLayouts.MultiplayerGlobals.Field( "runtime" );
				var runtimeElement = mulgWriter.BlockElements( mulg.Address + (uint)runtime.Offset, runtime.Struct.Size ).FirstOrDefault();
				var scoreboard = runtimeElement == 0 ? null : mulgWriter.Reference( runtimeElement + (uint)runtime.Struct.Field( "scoreboard hud definition" ).Offset );
				scoreboardHud = TagJsonWriter.TagName( scoreboard );
			}

			AddWanted();
		}

		return Write( json =>
		{
			json.WriteStartObject();
			json.WriteString( "Source", "Halo 2 (MCC), read from the installed maps by the halo2 mount" );
			json.WriteStartArray( "Maps" );
			foreach ( var name in mapNames )
				json.WriteStringValue( name );
			json.WriteEndArray();

			WriteRawProperty( json, "Globals", globals );
			WriteRawProperty( json, "Interface", interfaceTags );
			json.WriteString( "MultiplayerUnitHud", multiplayerHud );
			json.WriteString( "ScoreboardHud", scoreboardHud );

			json.WriteStartObject( "UnitHudsFor" );
			foreach ( var (unit, huds) in unitHuds.Where( u => u.Value.Count > 0 ) )
			{
				json.WriteStartArray( unit );
				foreach ( var hud in huds )
					json.WriteStringValue( hud );
				json.WriteEndArray();
			}
			json.WriteEndObject();

			json.WriteStartObject( "WeaponHudFor" );
			foreach ( var (weapon, hud) in weaponHuds )
				json.WriteString( weapon, hud );
			json.WriteEndObject();

			foreach ( var name in new[] { "Huds", "Shaders", "Bitmaps", "Strings" } )
				WriteSection( json, sections, name );

			json.WriteStartArray( "FontTable" );
			foreach ( var font in fontTable )
				json.WriteStringValue( $"fonts/{font}.json" );
			json.WriteEndArray();
			json.WriteEndObject();
		} );
	}

	// A unit's HUDs: a biped's new hud interfaces, then its seats', in order.
	static List<string> UnitHuds( TagJsonWriter writer, TagEntry unit )
	{
		var interfaces = TagLayouts.Unit.Field( "new hud interfaces" );
		var huds = writer.BlockElements( unit.Address + (uint)interfaces.Offset, interfaces.Struct.Size ).Select( writer.Reference ).ToList();

		var seats = TagLayouts.Unit.Field( "seats" );
		var seatInterfaces = seats.Struct.Field( "unit hud interface" );
		foreach ( var seat in writer.BlockElements( unit.Address + (uint)seats.Offset, seats.Struct.Size ) )
			huds.AddRange( writer.BlockElements( seat + (uint)seatInterfaces.Offset, seatInterfaces.Struct.Size ).Select( writer.Reference ) );

		return huds.Where( h => h is not null ).Select( TagJsonWriter.TagName ).Distinct().ToList();
	}

	static void WriteSection( Utf8JsonWriter json, Dictionary<string, Section> sections, string name )
	{
		json.WriteStartObject( name );
		if ( sections.TryGetValue( name, out var section ) )
		{
			foreach ( var (key, value) in section.Entries )
			{
				json.WritePropertyName( key );
				WriteRaw( json, value );
			}
		}
		json.WriteEndObject();
	}

	// A HUD shader as its template's pass draws it: textures, the constants its slots fill, and what animates them.
	static void WriteHudShader( Utf8JsonWriter json, TagEntry tag )
	{
		var shader = new ShaderTag( tag );
		json.WriteStartObject();
		json.WriteString( "Template", shader.TemplateName );
		json.WriteStartArray( "Passes" );
		foreach ( var pass in shader.Template is null ? [] : new ShaderTemplateTag( shader.Template ).Passes( HudLayer ) )
		{
			json.WriteStartObject();
			json.WriteString( "Pass", pass.Name );
			json.WriteString( "SourceBlend", BlendName( pass.SourceBlend ) );
			json.WriteString( "DestinationBlend", BlendName( pass.DestinationBlend ) );

			json.WriteStartArray( "Textures" );
			foreach ( var texture in pass.Textures )
			{
				json.WriteStartObject();
				json.WriteNumber( "Stage", texture.Stage );
				if ( texture.BitmapSlot >= 0 )
				{
					var bitmap = shader.GetBitmap( texture.BitmapSlot );
					json.WriteString( "Bitmap", TagJsonWriter.TagName( bitmap?.Bitmap ) );
					json.WriteNumber( "Image", bitmap?.Image ?? 0 );
				}
				else
				{
					json.WriteNumber( "Extern", texture.Extern );
				}
				json.WriteEndObject();
			}
			json.WriteEndArray();

			json.WriteStartObject( "Constants" );
			foreach ( var remap in pass.PixelConstants.Where( r => r.Source < shader.PixelConstants.Count ) )
			{
				var argb = shader.PixelConstants[remap.Source];
				json.WritePropertyName( RegisterName( remap.Register, remap.Mask ) );
				if ( remap.Mask >= 4 )
				{
					json.WriteStartArray();
					foreach ( var shift in new[] { 16, 8, 0 } )
						LayoutJsonWriter.WriteFloat( json, ((argb >> shift) & 0xFF) / 255f );
					json.WriteEndArray();
				}
				else
				{
					// Component k takes channel k of (r, g, b, a).
					LayoutJsonWriter.WriteFloat( json, ((argb >> new[] { 16, 8, 0, 24 }[remap.Mask]) & 0xFF) / 255f );
				}
			}
			json.WriteEndObject();

			json.WriteStartArray( "Animations" );
			foreach ( var parameter in ShaderAnimation.Read( tag, pass.TemplateImplementation ).Where( p => p.Kind == ShaderAnimatedKind.PixelConstant ) )
			{
				foreach ( var (overlay, _) in parameter.Overlays )
				{
					json.WriteStartObject();
					json.WriteString( "Target", RegisterName( parameter.Target & 0x1F, (parameter.Target >> 5) & 7 ) );
					json.WriteString( "Input", overlay.Input );
					json.WriteString( "RangeInput", overlay.Range );
					json.WritePropertyName( "Period" );
					LayoutJsonWriter.WriteFloat( json, overlay.Period );
					json.WriteString( "Modifier", parameter.Modifier < ScalarModifiers.Length ? ScalarModifiers[parameter.Modifier] : $"modifier {parameter.Modifier}" );
					json.WritePropertyName( "Function" );
					MappingFunctionJson.Write( json, overlay.Function );
					json.WriteEndObject();
				}
			}
			json.WriteEndArray();
			json.WriteEndObject();
		}
		json.WriteEndArray();
		json.WriteEndObject();
	}

	static string RegisterName( int register, int component ) => $"c{register}.{(component >= 4 ? "xyz" : "xyzw"[component].ToString())}";

	static string BlendName( int blend ) => blend switch
	{
		ShaderTemplatePass.BlendZero => "zero",
		ShaderTemplatePass.BlendOne => "one",
		ShaderTemplatePass.BlendSourceColor => "source color",
		ShaderTemplatePass.BlendSourceAlpha => "source alpha",
		ShaderTemplatePass.BlendInverseSourceAlpha => "inverse source alpha",
		ShaderTemplatePass.BlendDestinationColor => "destination color",
		_ => $"blend {blend}",
	};
}
