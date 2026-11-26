using System;
using System.Collections.Generic;
using System.Text.Json;
using Blam.IO;

namespace Blam.Halo1.TagData;

public static partial class TagExport
{
	/// <summary>
	/// Placement block, palette block and placement size of each kind of object, in the order of
	/// Halo's object types that object names refer to (biped 0, vehicle 1, weapon 2, equipment 3,
	/// scenery 6, machine 7, control 8, light fixture 9, sound scenery 11).
	/// </summary>
	static readonly (string Kind, int Type, uint Placements, uint Palette, int Size)[] CampaignObjectBlocks =
	[
		("biped", 0, 0x228, 0x234, 0x78),
		("vehicle", 1, 0x240, 0x24C, 0x78),
		("weapon", 2, 0x270, 0x27C, 0x5C),
		("equipment", 3, 0x258, 0x264, 0x28),
		("scenery", 6, 0x210, 0x21C, 0x48),
		("machine", 7, 0x294, 0x2A0, 0x40),
		("control", 8, 0x2AC, 0x2B8, 0x40),
		("light fixture", 9, 0x2C4, 0x2D0, 0x58),
		("sound scenery", 11, 0x2DC, 0x2E8, 0x28),
	];

	/// <summary>
	/// What a solo map's scripts and cinematics work on, as JSON. Positions are in world units and
	/// angles in radians, as the scenario stores them.
	/// <list type="bullet">
	/// <item><c>ObjectNames</c>: each name's object type and placement index (-1 when nothing is
	/// placed under it).</item>
	/// <item><c>Objects</c>: every placement, the ones only scripts create included, with its block
	/// index, name index, placement flags (0x1 placed by script only, 0x2/0x4/0x8 not on easy,
	/// normal or hard), and per kind its body vitality and flags (units), rounds and flags (weapons:
	/// 0x1 created at rest, 0x4 does accelerate), flags (equipment: 0x1 levitates, 0x4 doesn't
	/// orient to the ground), power
	/// and position groups and flags (devices), custom name (controls) and light (light fixtures).</item>
	/// <item><c>StartingProfiles</c>, <c>PlayerStarts</c>, <c>TriggerVolumes</c> (type 0 axis aligned,
	/// its position and extents then being the box's x, y and z bounds; type 1 oriented),
	/// <c>BspSwitchTriggerVolumes</c>, <c>CutsceneFlags</c>, <c>CameraPoints</c> (field of view in
	/// radians), <c>Titles</c>, <c>CustomObjectNames</c> (the controls' names), <c>DeviceGroups</c>, <c>RecordedAnimations</c> (event stream in
	/// base 64) and <c>StructureBsps</c>.</item>
	/// <item>The AI's actor palette, encounters, command lists, references and conversations (see
	/// <see cref="WriteCampaignAi"/>).</item>
	/// <item><c>Scripts</c>: the compiled script syntax nodes (20 bytes each) and string constants,
	/// both in base 64, the scripts with their root expressions, the globals with their initial
	/// expressions, and the tags scripts refer to.</item>
	/// </list>
	/// </summary>
	public static byte[] Campaign( CacheFile cache ) => Write( json =>
	{
		var d = cache.TagData;
		var a = cache.Scenario.Address;

		json.WriteStartObject();
		json.WriteString( "Map", cache.Name );

		json.WriteStartArray( "ObjectNames" );
		foreach ( var e in d.Block( a + 0x204, 0x24 ) )
		{
			json.WriteStartObject();
			json.WriteString( "Name", d.CString( e, 32 ) );
			json.WriteNumber( "Type", d.I16( e + 0x20 ) );
			json.WriteNumber( "Index", d.I16( e + 0x22 ) );
			json.WriteEndObject();
		}
		json.WriteEndArray();

		json.WriteStartArray( "Objects" );
		foreach ( var (kind, type, placements, palette, size) in CampaignObjectBlocks )
		{
			var paletteTags = new List<TagEntry>();
			foreach ( var entry in d.Block( a + palette, 0x30 ) )
				paletteTags.Add( cache.GetTagReference( d, entry ) );

			var index = 0;
			foreach ( var e in d.Block( a + placements, size ) )
			{
				var paletteIndex = d.I16( e );
				json.WriteStartObject();
				json.WriteString( "Kind", kind );
				json.WriteNumber( "Type", type );
				json.WriteNumber( "Index", index++ );
				json.WriteString( "Tag", paletteIndex >= 0 && paletteIndex < paletteTags.Count ? TagJsonWriter.TagName( paletteTags[paletteIndex] ) : null );
				json.WriteNumber( "Name", d.I16( e + 0x2 ) );
				json.WriteNumber( "Flags", d.U16( e + 0x4 ) );
				json.WriteNumber( "Variant", d.I16( e + 0x6 ) );
				WriteVector( json, "Position", d, e + 0x8, 3 );
				WriteVector( json, "Rotation", d, e + 0x14, 3 );
				json.WriteNumber( "BspFlags", d.U16( e + 0x20 ) );

				if ( kind is "biped" or "vehicle" )
				{
					json.WriteNumber( "BodyVitality", d.F32( e + 0x48 ) );
					json.WriteNumber( "UnitFlags", d.U32( e + 0x4C ) );
				}
				else if ( kind == "weapon" )
				{
					json.WriteNumber( "RoundsLeft", d.I16( e + 0x48 ) );
					json.WriteNumber( "RoundsLoaded", d.I16( e + 0x4A ) );
					json.WriteNumber( "WeaponFlags", d.U16( e + 0x4C ) );
				}
				else if ( kind == "equipment" )
					json.WriteNumber( "EquipmentFlags", d.U16( e + 0x22 ) );
				else if ( kind is "machine" or "control" or "light fixture" )
				{
					json.WriteNumber( "PowerGroup", d.I16( e + 0x28 ) );
					json.WriteNumber( "PositionGroup", d.I16( e + 0x2A ) );
					json.WriteNumber( "DeviceFlags", d.U32( e + 0x2C ) );
					if ( kind == "machine" )
						json.WriteNumber( "MachineFlags", d.U8( e + 0x30 ) );
					else if ( kind == "control" )
						json.WriteNumber( "ControlFlags", d.U16( e + 0x30 ) );
					WriteDevicePlacement( json, kind, d, e );
				}

				json.WriteEndObject();
			}
		}
		json.WriteEndArray();

		json.WriteStartArray( "StartingProfiles" );
		foreach ( var e in d.Block( a + 0x348, 0x68 ) )
		{
			json.WriteStartObject();
			json.WriteString( "Name", d.CString( e, 32 ) );
			json.WriteNumber( "Health", d.F32( e + 0x20 ) );
			json.WriteNumber( "Shield", d.F32( e + 0x24 ) );
			json.WriteString( "Primary", TagJsonWriter.TagName( cache.GetTagReference( d, e + 0x28 ) ) );
			json.WriteNumber( "PrimaryRoundsLoaded", d.I16( e + 0x38 ) );
			json.WriteNumber( "PrimaryRoundsTotal", d.I16( e + 0x3A ) );
			json.WriteString( "Secondary", TagJsonWriter.TagName( cache.GetTagReference( d, e + 0x3C ) ) );
			json.WriteNumber( "SecondaryRoundsLoaded", d.I16( e + 0x4C ) );
			json.WriteNumber( "SecondaryRoundsTotal", d.I16( e + 0x4E ) );
			json.WriteNumber( "FragGrenades", (sbyte)d.U8( e + 0x50 ) );
			json.WriteNumber( "PlasmaGrenades", (sbyte)d.U8( e + 0x51 ) );
			json.WriteEndObject();
		}
		json.WriteEndArray();

		json.WriteStartArray( "PlayerStarts" );
		foreach ( var e in d.Block( a + 0x354, 0x34 ) )
		{
			json.WriteStartObject();
			WriteVector( json, "Position", d, e, 3 );
			json.WriteNumber( "Facing", d.F32( e + 0xC ) );
			json.WriteNumber( "Team", d.I16( e + 0x10 ) );
			json.WriteNumber( "Bsp", d.I16( e + 0x12 ) );
			json.WriteEndObject();
		}
		json.WriteEndArray();

		json.WriteStartArray( "TriggerVolumes" );
		foreach ( var e in d.Block( a + 0x360, 0x60 ) )
		{
			json.WriteStartObject();
			json.WriteString( "Name", d.CString( e + 0x4, 32 ) );
			json.WriteNumber( "Type", d.I16( e ) );
			WriteVector( json, "Forward", d, e + 0x30, 3 );
			WriteVector( json, "Up", d, e + 0x3C, 3 );
			WriteVector( json, "Position", d, e + 0x48, 3 );
			WriteVector( json, "Extents", d, e + 0x54, 3 );
			json.WriteEndObject();
		}
		json.WriteEndArray();

		json.WriteStartArray( "RecordedAnimations" );
		foreach ( var e in d.Block( a + 0x36C, 0x40 ) )
		{
			json.WriteStartObject();
			json.WriteString( "Name", d.CString( e, 32 ) );
			json.WriteNumber( "Version", d.U8( e + 0x20 ) );
			json.WriteNumber( "RawAnimationData", (sbyte)d.U8( e + 0x21 ) );
			json.WriteNumber( "UnitControlVersion", d.U8( e + 0x22 ) );
			json.WriteNumber( "LengthTicks", d.I16( e + 0x24 ) );
			json.WriteBase64String( "EventStream", TagDataBytes( d, e + 0x2C ) );
			json.WriteEndObject();
		}
		json.WriteEndArray();

		json.WriteStartArray( "BspSwitchTriggerVolumes" );
		foreach ( var e in d.Block( a + 0x39C, 0x8 ) )
		{
			json.WriteStartObject();
			json.WriteNumber( "TriggerVolume", d.I16( e ) );
			json.WriteNumber( "Source", d.I16( e + 0x2 ) );
			json.WriteNumber( "Destination", d.I16( e + 0x4 ) );
			json.WriteNumber( "CutsceneFlag", d.I16( e + 0x6 ) );
			json.WriteEndObject();
		}
		json.WriteEndArray();

		json.WriteStartArray( "CutsceneFlags" );
		foreach ( var e in d.Block( a + 0x4E4, 0x5C ) )
		{
			json.WriteStartObject();
			json.WriteString( "Name", d.CString( e + 0x4, 32 ) );
			WriteVector( json, "Position", d, e + 0x24, 3 );
			WriteVector( json, "Facing", d, e + 0x30, 2 );
			json.WriteEndObject();
		}
		json.WriteEndArray();

		json.WriteStartArray( "CameraPoints" );
		foreach ( var e in d.Block( a + 0x4F0, 0x68 ) )
		{
			json.WriteStartObject();
			json.WriteString( "Name", d.CString( e + 0x4, 32 ) );
			json.WriteNumber( "Flags", d.U32( e ) );
			WriteVector( json, "Position", d, e + 0x28, 3 );
			WriteVector( json, "Orientation", d, e + 0x34, 3 );
			json.WriteNumber( "FieldOfView", d.F32( e + 0x40 ) );
			json.WriteEndObject();
		}
		json.WriteEndArray();

		json.WriteStartArray( "Titles" );
		foreach ( var e in d.Block( a + 0x4FC, 0x60 ) )
		{
			json.WriteStartObject();
			json.WriteString( "Name", d.CString( e + 0x4, 32 ) );
			json.WriteStartArray( "Bounds" );
			for ( var i = 0u; i < 4; i++ )
				json.WriteNumberValue( d.I16( e + 0x28 + i * 2 ) );
			json.WriteEndArray();
			json.WriteNumber( "Text", d.I16( e + 0x30 ) );
			json.WriteNumber( "Style", d.I16( e + 0x32 ) );
			json.WriteNumber( "Justification", d.I16( e + 0x34 ) );
			json.WriteNumber( "TextFlags", d.U32( e + 0x38 ) );
			json.WriteNumber( "TextColor", d.U32( e + 0x3C ) );
			json.WriteNumber( "ShadowColor", d.U32( e + 0x40 ) );
			json.WriteNumber( "FadeIn", d.F32( e + 0x44 ) );
			json.WriteNumber( "Up", d.F32( e + 0x48 ) );
			json.WriteNumber( "FadeOut", d.F32( e + 0x4C ) );
			json.WriteEndObject();
		}
		json.WriteEndArray();
		json.WriteString( "TitleText", TagJsonWriter.TagName( cache.GetTagReference( d, a + 0x584 ) ) );
		json.WriteString( "CustomObjectNames", TagJsonWriter.TagName( cache.GetTagReference( d, a + 0x574 ) ) );
		WriteHudMessages( json, cache, d, a );

		json.WriteStartArray( "DeviceGroups" );
		foreach ( var e in d.Block( a + 0x288, 0x34 ) )
		{
			json.WriteStartObject();
			json.WriteString( "Name", d.CString( e, 32 ) );
			json.WriteNumber( "InitialValue", d.F32( e + 0x20 ) );
			json.WriteNumber( "Flags", d.U32( e + 0x24 ) );
			json.WriteEndObject();
		}
		json.WriteEndArray();

		json.WriteStartArray( "StructureBsps" );
		foreach ( var e in d.Block( a + 0x5A4, 0x20 ) )
			json.WriteStringValue( TagJsonWriter.TagName( cache.GetTag( d.U32( e + 0x10 + 0xC ) ) ) );
		json.WriteEndArray();

		WriteCampaignAi( cache, json );

		json.WritePropertyName( "Scripts" );
		json.WriteStartObject();
		json.WriteBase64String( "SyntaxNodes", SyntaxNodes( d, a + 0x474 ) );
		json.WriteBase64String( "Strings", TagDataBytes( d, a + 0x488 ) );

		json.WriteStartArray( "Scripts" );
		foreach ( var e in d.Block( a + 0x49C, 0x5C ) )
		{
			json.WriteStartObject();
			json.WriteString( "Name", d.CString( e, 32 ) );
			json.WriteNumber( "Type", d.I16( e + 0x20 ) );
			json.WriteNumber( "ReturnType", d.I16( e + 0x22 ) );
			json.WriteNumber( "Root", d.I32( e + 0x24 ) );
			json.WriteEndObject();
		}
		json.WriteEndArray();

		json.WriteStartArray( "Globals" );
		foreach ( var e in d.Block( a + 0x4A8, 0x5C ) )
		{
			json.WriteStartObject();
			json.WriteString( "Name", d.CString( e, 32 ) );
			json.WriteNumber( "Type", d.I16( e + 0x20 ) );
			json.WriteNumber( "Initial", d.I32( e + 0x28 ) );
			json.WriteEndObject();
		}
		json.WriteEndArray();

		json.WriteStartArray( "References" );
		foreach ( var e in d.Block( a + 0x4B4, 0x28 ) )
			json.WriteStringValue( TagJsonWriter.TagName( cache.GetTagReference( d, e + 0x18 ) ) );
		json.WriteEndArray();
		json.WriteEndObject();

		json.WriteEndObject();
	} );

	static void WriteVector( Utf8JsonWriter json, string name, MemoryRegion d, uint address, int count )
	{
		json.WriteStartArray( name );
		for ( var i = 0u; i < count; i++ )
			json.WriteNumberValue( d.F32( address + i * 4 ) );
		json.WriteEndArray();
	}

	static ReadOnlySpan<byte> TagDataBytes( MemoryRegion d, uint address )
	{
		var size = d.I32( address );
		var pointer = d.U32( address + 0xC );
		return size > 0 && d.Contains( pointer, size ) ? d.Slice( pointer, size ) : ReadOnlySpan<byte>.Empty;
	}

	/// <summary>
	/// The script syntax nodes without the header of the array they're stored in (0x38 bytes:
	/// name, capacity at 0x20, node size at 0x22, nodes in use at 0x2E).
	/// </summary>
	static ReadOnlySpan<byte> SyntaxNodes( MemoryRegion d, uint address )
	{
		var data = TagDataBytes( d, address );
		if ( data.Length < 0x38 )
			return ReadOnlySpan<byte>.Empty;

		var nodeSize = BitConverter.ToInt16( data[0x22..] );
		var count = BitConverter.ToInt16( data[0x2E..] );
		return data.Slice( 0x38, Math.Min( count * nodeSize, data.Length - 0x38 ) );
	}
}
