using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Blam.Halo1.TagData;

public static partial class TagExport
{
	static readonly TagClass UnitHud = new( "unhi" );
	static readonly TagClass GrenadeHud = new( "grhi" );
	static readonly TagClass WeaponHud = new( "wphi" );
	static readonly TagClass MeterClass = new( "metr" );

	/// <summary>
	/// Everything a game needs to draw Halo's HUD, from the given maps (the first map with a tag
	/// provides it):
	/// <list type="bullet">
	/// <item><c>Globals</c>: the HUD globals (hudg) the globals' interface bitmaps name.</item>
	/// <item><c>Interface</c>: those interface bitmaps (motion sensor sweep, mask and blips, the
	/// multiplayer HUD bitmap) and the HUD digits (hud#) as <c>HudDigits</c>.</item>
	/// <item><c>MultiplayerUnitHud</c> and <c>SplitscreenUnitHud</c>: the multiplayer biped's first
	/// and second HUD interfaces (Halo picks by the number of local players).</item>
	/// <item><c>UnitHuds</c>, <c>GrenadeHudInterfaces</c>, <c>WeaponHuds</c>, <c>Meters</c>: every
	/// unhi, grhi, wphi and metr by tag name; <c>GrenadeHuds</c>: the grenade HUDs by grenade type;
	/// <c>WeaponHudFor</c>: each weapon's HUD interface.</item>
	/// <item><c>Bitmaps</c>: every bitmap the above reference, with its sequences, sprites and page
	/// sizes; <c>Remaps</c>: the anniversary HUD remaps for English (combined HUD bitmap to its
	/// "_r" version), leaving out "legacy mode" targets.</item>
	/// </list>
	/// </summary>
	public static byte[] Hud( IEnumerable<CacheFile> maps )
	{
		var sections = new Dictionary<string, Section>();
		Section Get( string name ) => sections.TryGetValue( name, out var section ) ? section : sections[name] = new Section();

		var weaponHudFor = new Dictionary<string, string>();
		var remaps = new Dictionary<string, string>();
		byte[] globals = null, interfaceBitmaps = null;
		string multiplayerHud = null, splitscreenHud = null;
		List<string> grenadeHuds = null;
		var mapNames = new List<string>();

		foreach ( var cache in maps )
		{
			var matg = cache.Tags.FirstOrDefault( t => t.Class == new TagClass( "matg" ) );
			if ( matg is null )
				continue;

			mapNames.Add( cache.Name );

			var bitmaps = new Queue<TagEntry>();
			var writer = new TagJsonWriter( cache )
			{
				ReferenceWritten = tag =>
				{
					if ( tag.Class == Bitmap )
						bitmaps.Enqueue( tag );
				},
			};

			if ( globals is null )
			{
				var block = TagLayouts.Globals.Field( "interface bitmaps" );
				var first = writer.BlockElements( matg.Address + (uint)block.Offset, block.Struct.Size ).FirstOrDefault();
				TagEntry Interface( string field ) => first == 0 ? null : writer.Reference( first + (uint)block.Struct.Field( field ).Offset );

				var hudGlobals = Interface( "hud globals" );
				if ( hudGlobals is not null )
				{
					globals = WriteTag( writer, hudGlobals );
					remaps = EnglishRemaps( writer, hudGlobals );
				}

				var digits = Interface( "hud digits definition" );
				interfaceBitmaps = Write( json =>
				{
					json.WriteStartObject();
					foreach ( var (name, field) in new[]
					{
						("HudGlobals", "hud globals"),
						("MotionSensorSweepBitmap", "motion sensor sweep bitmap"),
						("MotionSensorSweepBitmapMask", "motion sensor sweep bitmap mask"),
						("MotionSensorBlipBitmap", "motion sensor blip bitmap"),
						("CustomBlipBitmap", "interface goo map1"),
						("MultiplayerHudBitmap", "multiplayer hud bitmap"),
						("HudDigitsDefinition", "hud digits definition"),
					} )
					{
						json.WritePropertyName( name );
						if ( first == 0 )
							json.WriteNullValue();
						else
							writer.WriteValue( json, block.Struct.Field( field ), first );
					}

					json.WritePropertyName( "HudDigits" );
					if ( digits is null )
						json.WriteNullValue();
					else
						writer.WriteTag( json, digits, TagLayouts.HudNumber );
					json.WriteEndObject();
				} );
			}

			// The multiplayer biped's HUD interfaces: [0] with one local player, [1] split screen.
			var unit = MultiplayerUnit( writer, matg );
			if ( multiplayerHud is null && unit is not null )
			{
				var interfaces = TagLayouts.Biped.Field( "new hud interfaces" );
				var huds = writer.BlockElements( unit.Address + (uint)interfaces.Offset, interfaces.Struct.Size ).Select( writer.Reference ).Select( TagJsonWriter.TagName ).ToList();
				if ( huds.Count > 0 )
				{
					multiplayerHud = huds[0];
					splitscreenHud = huds.Count > 1 ? huds[1] : huds[0];
				}
			}

			if ( grenadeHuds is null )
			{
				var grenades = TagLayouts.Globals.Field( "grenades" );
				var hud = grenades.Struct.Field( "hud interface" );
				grenadeHuds = writer.BlockElements( matg.Address + (uint)grenades.Offset, grenades.Struct.Size )
					.Select( g => TagJsonWriter.TagName( writer.Reference( g + (uint)hud.Offset ) ) ).ToList();
			}

			foreach ( var (name, tagClass) in new[] { ("UnitHuds", UnitHud), ("GrenadeHudInterfaces", GrenadeHud), ("WeaponHuds", WeaponHud), ("Meters", MeterClass) } )
			{
				var section = Get( name );
				foreach ( var tag in cache.Tags.Where( t => t.Class == tagClass && !string.IsNullOrEmpty( t.Path ) ) )
				{
					if ( !section.Has( tag ) )
						section.Add( tag, WriteTag( writer, tag ) );
				}
			}

			var weaponHud = TagLayouts.Weapon.Field( "hud interface" );
			foreach ( var weapon in cache.Tags.Where( t => t.Class == Weapon && !string.IsNullOrEmpty( t.Path ) ) )
				weaponHudFor.TryAdd( TagJsonWriter.TagName( weapon ), TagJsonWriter.TagName( writer.Reference( weapon.Address + (uint)weaponHud.Offset ) ) );

			var bitmapSection = Get( "Bitmaps" );
			while ( bitmaps.TryDequeue( out var bitmap ) )
			{
				if ( !bitmapSection.Has( bitmap ) )
					bitmapSection.Add( bitmap, WriteTag( writer, bitmap ) );
			}
		}

		return Write( json =>
		{
			json.WriteStartObject();
			json.WriteString( "Source", "Halo CE (MCC), read from the installed maps by the haloce mount" );
			json.WriteStartArray( "Maps" );
			foreach ( var name in mapNames )
				json.WriteStringValue( name );
			json.WriteEndArray();

			WriteRawProperty( json, "Globals", globals );
			WriteRawProperty( json, "Interface", interfaceBitmaps );
			json.WriteString( "MultiplayerUnitHud", multiplayerHud );
			json.WriteString( "SplitscreenUnitHud", splitscreenHud );

			WriteSection( json, sections, "UnitHuds" );
			json.WriteStartArray( "GrenadeHuds" );
			foreach ( var hud in grenadeHuds ?? [] )
				json.WriteStringValue( hud );
			json.WriteEndArray();
			WriteSection( json, sections, "GrenadeHudInterfaces" );
			WriteSection( json, sections, "WeaponHuds" );

			json.WriteStartObject( "WeaponHudFor" );
			foreach ( var (weapon, hud) in weaponHudFor )
				json.WriteString( weapon, hud );
			json.WriteEndObject();

			WriteSection( json, sections, "Meters" );
			WriteSection( json, sections, "Bitmaps" );

			json.WriteStartObject( "Remaps" );
			foreach ( var (source, target) in remaps )
				json.WriteString( source, target );
			json.WriteEndObject();
			json.WriteEndObject();
		} );
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

	/// <summary>
	/// The HUD globals' anniversary remaps for English: each combined HUD bitmap to the first
	/// English target that isn't "legacy mode".
	/// </summary>
	static Dictionary<string, string> EnglishRemaps( TagJsonWriter writer, TagEntry hudGlobals )
	{
		var remaps = new Dictionary<string, string>();
		var block = TagLayouts.HudGlobals.Field( "anniversary hud remaps" );
		var targets = block.Struct.Field( "targets" );
		var language = targets.Struct.Field( "language" );
		var flags = targets.Struct.Field( "flags" );

		foreach ( var remap in writer.BlockElements( hudGlobals.Address + (uint)block.Offset, block.Struct.Size ) )
		{
			var source = writer.Reference( remap + (uint)block.Struct.Field( "source bitmap" ).Offset );
			foreach ( var target in writer.BlockElements( remap + (uint)targets.Offset, targets.Struct.Size ) )
			{
				var english = writer.Data.U16( target + (uint)language.Offset ) == 0;
				var legacy = (writer.Data.U16( target + (uint)flags.Offset ) & 1) != 0;
				var bitmap = writer.Reference( target );
				if ( english && !legacy && source is not null && bitmap is not null )
				{
					remaps.TryAdd( TagJsonWriter.TagName( source ), TagJsonWriter.TagName( bitmap ) );
					break;
				}
			}
		}

		return remaps;
	}
}
