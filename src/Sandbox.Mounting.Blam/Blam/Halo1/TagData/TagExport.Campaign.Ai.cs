using System.Text.Json;
using Blam.IO;

namespace Blam.Halo1.TagData;

public static partial class TagExport
{
	/// <summary>
	/// The scenario's AI: what it places, where it moves and what it's told to do. Numbers are as
	/// the scenario stores them (enums and flags as integers, angles in radians, block indices -1
	/// for none); tag references are tag names or null.
	/// <list type="bullet">
	/// <item><c>ActorPalette</c>: the actor variant of each palette entry, which squads and starting
	/// locations pick by index.</item>
	/// <item><c>Encounters</c>: squads (with their move positions and starting locations), platoons,
	/// firing positions and player starting locations. A squad's <c>FiringPositionGroups</c> are bit
	/// masks of firing position groups (bit 0 group a) for its attacking, attacking search,
	/// attacking guard, defending, defending search, defending guard and pursuing states.</item>
	/// <item><c>CommandLists</c>: the commands (atom and its modifier, parameters, and indices into
	/// the list's points, the AI animation, script and recording references, the list's own
	/// commands and the object names) and points of each list.</item>
	/// <item><c>AiAnimationReferences</c>, <c>AiScriptReferences</c>, <c>AiRecordingReferences</c>
	/// and <c>AiConversations</c> (participants and lines, each line's sound per variant).</item>
	/// </list>
	/// </summary>
	static void WriteCampaignAi( CacheFile cache, Utf8JsonWriter json )
	{
		var d = cache.TagData;
		var a = cache.Scenario.Address;

		json.WriteStartArray( "ActorPalette" );
		foreach ( var e in d.Block( a + 0x420, 0x10 ) )
			json.WriteStringValue( TagJsonWriter.TagName( cache.GetTagReference( d, e ) ) );
		json.WriteEndArray();

		json.WriteStartArray( "Encounters" );
		foreach ( var e in d.Block( a + 0x42C, 0xB0 ) )
			WriteEncounter( json, d, e );
		json.WriteEndArray();

		json.WriteStartArray( "CommandLists" );
		foreach ( var e in d.Block( a + 0x438, 0x60 ) )
			WriteCommandList( json, d, e );
		json.WriteEndArray();

		json.WriteStartArray( "AiAnimationReferences" );
		foreach ( var e in d.Block( a + 0x444, 0x3C ) )
		{
			json.WriteStartObject();
			json.WriteString( "Name", d.CString( e, 32 ) );
			json.WriteString( "Graph", TagJsonWriter.TagName( cache.GetTagReference( d, e + 0x20 ) ) );
			json.WriteEndObject();
		}
		json.WriteEndArray();

		json.WriteStartArray( "AiScriptReferences" );
		foreach ( var e in d.Block( a + 0x450, 0x28 ) )
			json.WriteStringValue( d.CString( e, 32 ) );
		json.WriteEndArray();

		json.WriteStartArray( "AiRecordingReferences" );
		foreach ( var e in d.Block( a + 0x45C, 0x28 ) )
			json.WriteStringValue( d.CString( e, 32 ) );
		json.WriteEndArray();

		json.WriteStartArray( "AiConversations" );
		foreach ( var e in d.Block( a + 0x468, 0x74 ) )
			WriteConversation( json, cache, d, e );
		json.WriteEndArray();
	}

	static void WriteEncounter( Utf8JsonWriter json, MemoryRegion d, uint e )
	{
		json.WriteStartObject();
		json.WriteString( "Name", d.CString( e, 32 ) );
		json.WriteNumber( "Flags", d.U32( e + 0x20 ) );
		json.WriteNumber( "Team", d.I16( e + 0x24 ) );
		json.WriteNumber( "Searching", d.I16( e + 0x28 ) );
		json.WriteNumber( "ManualBsp", d.I16( e + 0x2A ) );
		WriteVector( json, "RespawnDelay", d, e + 0x2C, 2 );
		json.WriteNumber( "PrecomputedBsp", d.I16( e + 0x7E ) );

		json.WriteStartArray( "Squads" );
		foreach ( var s in d.Block( e + 0x80, 0xE8 ) )
			WriteSquad( json, d, s );
		json.WriteEndArray();

		json.WriteStartArray( "Platoons" );
		foreach ( var p in d.Block( e + 0x8C, 0xAC ) )
		{
			json.WriteStartObject();
			json.WriteString( "Name", d.CString( p, 32 ) );
			json.WriteNumber( "Flags", d.U32( p + 0x20 ) );
			json.WriteNumber( "AttackingDefendingRule", d.I16( p + 0x30 ) );
			json.WriteNumber( "AttackingDefendingPlatoon", d.I16( p + 0x32 ) );
			json.WriteNumber( "ManeuverRule", d.I16( p + 0x3C ) );
			json.WriteNumber( "ManeuverPlatoon", d.I16( p + 0x3E ) );
			json.WriteEndObject();
		}
		json.WriteEndArray();

		json.WriteStartArray( "FiringPositions" );
		foreach ( var f in d.Block( e + 0x98, 0x18 ) )
		{
			json.WriteStartObject();
			WriteVector( json, "Position", d, f, 3 );
			json.WriteNumber( "Group", d.I16( f + 0xC ) );
			json.WriteNumber( "ClusterIndex", d.I16( f + 0xE ) );
			json.WriteNumber( "SurfaceIndex", d.I32( f + 0x14 ) );
			json.WriteEndObject();
		}
		json.WriteEndArray();

		json.WriteStartArray( "PlayerStartingLocations" );
		foreach ( var p in d.Block( e + 0xA4, 0x34 ) )
		{
			json.WriteStartObject();
			WriteVector( json, "Position", d, p, 3 );
			json.WriteNumber( "Facing", d.F32( p + 0xC ) );
			json.WriteNumber( "Team", d.I16( p + 0x10 ) );
			json.WriteEndObject();
		}
		json.WriteEndArray();

		json.WriteEndObject();
	}

	static void WriteSquad( Utf8JsonWriter json, MemoryRegion d, uint s )
	{
		json.WriteStartObject();
		json.WriteString( "Name", d.CString( s, 32 ) );
		json.WriteNumber( "ActorType", d.I16( s + 0x20 ) );
		json.WriteNumber( "Platoon", d.I16( s + 0x22 ) );
		json.WriteNumber( "InitialState", d.I16( s + 0x24 ) );
		json.WriteNumber( "ReturnState", d.I16( s + 0x26 ) );
		json.WriteNumber( "Flags", d.U32( s + 0x28 ) );
		json.WriteNumber( "UniqueLeaderType", d.I16( s + 0x2C ) );
		json.WriteNumber( "ManeuverToSquad", d.I16( s + 0x4E ) );
		json.WriteNumber( "SquadDelayTime", d.F32( s + 0x50 ) );
		json.WriteStartArray( "FiringPositionGroups" );
		for ( var i = 0u; i < 7; i++ )
			json.WriteNumberValue( d.U32( s + 0x54 + i * 4 ) );
		json.WriteEndArray();
		json.WriteNumber( "NormalCount", d.I16( s + 0x7C ) );
		json.WriteNumber( "InsaneCount", d.I16( s + 0x7E ) );
		json.WriteNumber( "MajorUpgrade", d.I16( s + 0x80 ) );
		json.WriteNumber( "RespawnMinActors", d.I16( s + 0x84 ) );
		json.WriteNumber( "RespawnMaxActors", d.I16( s + 0x86 ) );
		json.WriteNumber( "RespawnTotal", d.I16( s + 0x88 ) );
		WriteVector( json, "RespawnDelay", d, s + 0x8C, 2 );

		json.WriteStartArray( "MovePositions" );
		foreach ( var m in d.Block( s + 0xC4, 0x50 ) )
		{
			json.WriteStartObject();
			WriteVector( json, "Position", d, m, 3 );
			json.WriteNumber( "Facing", d.F32( m + 0xC ) );
			json.WriteNumber( "Weight", d.F32( m + 0x10 ) );
			WriteVector( json, "Time", d, m + 0x14, 2 );
			json.WriteNumber( "Animation", d.I16( m + 0x1C ) );
			json.WriteNumber( "SequenceId", (sbyte)d.U8( m + 0x1E ) );
			json.WriteNumber( "ClusterIndex", d.I16( m + 0x28 ) );
			json.WriteNumber( "SurfaceIndex", d.I32( m + 0x4C ) );
			json.WriteEndObject();
		}
		json.WriteEndArray();

		json.WriteStartArray( "StartingLocations" );
		foreach ( var l in d.Block( s + 0xD0, 0x1C ) )
		{
			json.WriteStartObject();
			WriteVector( json, "Position", d, l, 3 );
			json.WriteNumber( "Facing", d.F32( l + 0xC ) );
			json.WriteNumber( "ClusterIndex", d.I16( l + 0x10 ) );
			json.WriteNumber( "SequenceId", (sbyte)d.U8( l + 0x12 ) );
			json.WriteNumber( "Flags", d.U8( l + 0x13 ) );
			json.WriteNumber( "ReturnState", d.I16( l + 0x14 ) );
			json.WriteNumber( "InitialState", d.I16( l + 0x16 ) );
			json.WriteNumber( "ActorType", d.I16( l + 0x18 ) );
			json.WriteNumber( "CommandList", d.I16( l + 0x1A ) );
			json.WriteEndObject();
		}
		json.WriteEndArray();

		json.WriteEndObject();
	}

	static void WriteCommandList( Utf8JsonWriter json, MemoryRegion d, uint e )
	{
		json.WriteStartObject();
		json.WriteString( "Name", d.CString( e, 32 ) );
		json.WriteNumber( "Flags", d.U32( e + 0x20 ) );
		json.WriteNumber( "ManualBsp", d.I16( e + 0x2C ) );
		json.WriteNumber( "PrecomputedBsp", d.I16( e + 0x2E ) );

		json.WriteStartArray( "Commands" );
		foreach ( var c in d.Block( e + 0x30, 0x20 ) )
		{
			json.WriteStartObject();
			json.WriteNumber( "Atom", d.I16( c ) );
			json.WriteNumber( "Modifier", d.I16( c + 0x2 ) );
			json.WriteNumber( "Parameter1", d.F32( c + 0x4 ) );
			json.WriteNumber( "Parameter2", d.F32( c + 0x8 ) );
			json.WriteNumber( "Point1", d.I16( c + 0xC ) );
			json.WriteNumber( "Point2", d.I16( c + 0xE ) );
			json.WriteNumber( "Animation", d.I16( c + 0x10 ) );
			json.WriteNumber( "Script", d.I16( c + 0x12 ) );
			json.WriteNumber( "Recording", d.I16( c + 0x14 ) );
			json.WriteNumber( "Command", d.I16( c + 0x16 ) );
			json.WriteNumber( "ObjectName", d.I16( c + 0x18 ) );
			json.WriteEndObject();
		}
		json.WriteEndArray();

		json.WriteStartArray( "Points" );
		foreach ( var p in d.Block( e + 0x3C, 0x14 ) )
		{
			json.WriteStartObject();
			WriteVector( json, "Position", d, p, 3 );
			json.WriteNumber( "SurfaceIndex", d.I32( p + 0xC ) );
			json.WriteEndObject();
		}
		json.WriteEndArray();

		json.WriteEndObject();
	}

	static void WriteConversation( Utf8JsonWriter json, CacheFile cache, MemoryRegion d, uint e )
	{
		json.WriteStartObject();
		json.WriteString( "Name", d.CString( e, 32 ) );
		json.WriteNumber( "Flags", d.U16( e + 0x20 ) );
		json.WriteNumber( "TriggerDistance", d.F32( e + 0x24 ) );
		json.WriteNumber( "RunToPlayerDistance", d.F32( e + 0x28 ) );

		json.WriteStartArray( "Participants" );
		foreach ( var p in d.Block( e + 0x50, 0x54 ) )
		{
			json.WriteStartObject();
			json.WriteNumber( "Flags", d.U16( p + 0x2 ) );
			json.WriteNumber( "SelectionType", d.I16( p + 0x4 ) );
			json.WriteNumber( "ActorType", d.I16( p + 0x6 ) );
			json.WriteNumber( "UseThisObject", d.I16( p + 0x8 ) );
			json.WriteNumber( "SetNewName", d.I16( p + 0xA ) );
			json.WriteStartArray( "VariantNumbers" );
			for ( var i = 0u; i < 6; i++ )
				json.WriteNumberValue( d.U16( p + 0x18 + i * 2 ) );
			json.WriteEndArray();
			json.WriteString( "EncounterName", d.CString( p + 0x24, 32 ) );
			json.WriteNumber( "EncounterIndex", d.I32( p + 0x44 ) );
			json.WriteEndObject();
		}
		json.WriteEndArray();

		json.WriteStartArray( "Lines" );
		foreach ( var l in d.Block( e + 0x5C, 0x7C ) )
		{
			json.WriteStartObject();
			json.WriteNumber( "Flags", d.U16( l ) );
			json.WriteNumber( "Participant", d.I16( l + 0x2 ) );
			json.WriteNumber( "Addressee", d.I16( l + 0x4 ) );
			json.WriteNumber( "AddresseeParticipant", d.I16( l + 0x6 ) );
			json.WriteNumber( "LineDelayTime", d.F32( l + 0xC ) );
			json.WriteStartArray( "Variants" );
			for ( var i = 0u; i < 6; i++ )
				json.WriteStringValue( TagJsonWriter.TagName( cache.GetTagReference( d, l + 0x1C + i * 0x10 ) ) );
			json.WriteEndArray();
			json.WriteEndObject();
		}
		json.WriteEndArray();

		json.WriteEndObject();
	}
}
