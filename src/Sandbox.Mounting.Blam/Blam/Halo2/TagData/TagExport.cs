using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Blam.Halo2.Tags;

namespace Blam.Halo2.TagData;

// Halo 2 tag data as JSON documents, as the Halo CE mount writes them.
// There is one document per tag, one per map (spawns and placements) and one with everything a multiplayer game needs.
public static partial class TagExport
{
	static readonly TagClass Effect = new( "effe" );
	static readonly TagClass Collision = new( "coll" );
	static readonly TagClass PhysicsModel = new( "phmo" );
	static readonly TagClass Model = new( "hlmt" );
	static readonly TagClass MaterialEffects = new( "foot" );
	static readonly TagClass Scenario = new( "scnr" );
	static readonly TagClass StructureBsp = new( "sbsp" );

	// The sections of Game that hold every tag of a class found in the maps.
	static readonly (string Section, TagClass Class)[] GameSections =
	[
		("Weapons", new( "weap" )),
		("Projectiles", new( "proj" )),
		("DamageEffects", new( "jpt!" )),
		("Equipment", new( "eqip" )),
		("ItemCollections", new( "itmc" )),
		("VehicleCollections", new( "vehc" )),
		("Garbage", new( "garb" )),
		("Bipeds", Biped),
		("Vehicles", new( "vehi" )),
		("Crates", new( "bloc" )),
	];

	// The sections of tags written only when an exported tag points at them, by class.
	static readonly (string Section, TagClass Class)[] UsedSections =
	[
		("Models", Model),
		("CollisionModels", Collision),
		("PhysicsModels", PhysicsModel),
		("MaterialEffects", MaterialEffects),
		("Effects", Effect),
	];

	// True if Tag can write this tag.
	public static bool CanExport( TagEntry tag ) =>
		!string.IsNullOrEmpty( tag.Path ) && tag.Class != Scenario && (TagLayouts.For( tag ) is not null || SoundExport.IsSound( tag ) || tag.Class == StringListTag.Class);

	// Where a tag's document goes: tags/objects/weapons/rifle/battle_rifle/battle_rifle.weap.json.
	public static string TagPath( TagEntry tag ) => $"tags/{TagJsonWriter.TagName( tag )}.json";

	// A tag as JSON, all of the fields in its layout; a sound or looping sound as the mount reads it (see SoundExport).
	// A string list is its strings in every language (see StringList).
	public static byte[] Tag( TagEntry tag )
	{
		if ( SoundExport.IsSound( tag ) )
			return SoundExport.Tag( tag );

		if ( tag.Class == StringListTag.Class )
			return StringList( tag );

		var layout = TagLayouts.For( tag ) ?? throw new ArgumentException( $"{tag} can't be exported", nameof( tag ) );
		return Write( json => new TagJsonWriter( tag.Cache ).WriteTag( json, tag, layout ) );
	}

	// A map's player starts with their game types, netgame flags, item and vehicle spawns, starting equipment and profiles.
	// Then the vehicles, weapons, equipment and crates placed in the level, its spawn data, the skies, and the structure BSPs with vehicle limits.
	// Crates[i] is the crate a map's scene tags halo_object_<i>.
	public static byte[] Map( CacheFile cache ) => Write( json =>
	{
		var scenario = cache.Scenario;
		var writer = new TagJsonWriter( cache ) { TagAddress = scenario.Address };
		var layout = TagLayouts.Scenario;
		var data = cache.TagData;

		json.WriteStartObject();
		json.WriteString( "Map", cache.Name );
		json.WriteString( "Scenario", TagJsonWriter.TagName( scenario ) );
		json.WriteString( "ScenarioType", cache.Type.ToString() );

		// Radians, as the Halo CE documents have it: the heading the motion sensor calls north.
		json.WriteNumber( "LocalNorth", data.F32( scenario.Address + (uint)layout.Field( "local north" ).Offset ) );
		foreach ( var (name, field) in new[]
		{
			("PlayerStarts", "player starting locations"),
			("PlayerStartingProfiles", "player starting profile"),
			("NetgameFlags", "netgame flags"),
			("NetgameEquipment", "netgame equipment"),
			("StartingEquipment", "starting equipment"),
			("Vehicles", "vehicles"),
			("Weapons", "weapons"),
			("Equipment", "equipment"),
			("Crates", "crates"),
		} )
		{
			var block = layout.Field( field );
			json.WritePropertyName( name );
			writer.WriteBlock( json, block.Struct, scenario.Address + (uint)block.Offset );
		}

		// The map's spawn settings, one element at most: null when it has none.
		var spawnData = layout.Field( "spawn data" );
		var spawnDataElement = writer.BlockElements( scenario.Address + (uint)spawnData.Offset, spawnData.Struct.Size ).FirstOrDefault();
		json.WritePropertyName( "SpawnData" );
		if ( spawnDataElement == 0 )
			json.WriteNullValue();
		else
			writer.WriteStruct( json, spawnData.Struct, spawnDataElement );

		// Skies (0x8: references 8 bytes apart), which a structure BSP's default sky indexes.
		json.WriteStartArray( "Skies" );
		foreach ( var sky in writer.BlockElements( scenario.Address + 0x8, 8 ) )
			json.WriteStringValue( TagJsonWriter.TagName( writer.Reference( sky ) ) );
		json.WriteEndArray();

		// Vehicles are kept between a structure BSP's vehicle floor and ceiling (world units, 0x1C and 0x20 in the BSP).
		var bsps = layout.Field( "structure bsps" );
		var bspReference = bsps.Struct.Field( "structure bsp" );
		json.WriteStartArray( "StructureBsps" );
		foreach ( var element in writer.BlockElements( scenario.Address + (uint)bsps.Offset, bsps.Struct.Size ) )
		{
			json.WriteStartObject();
			writer.WriteFields( json, bsps.Struct, element );
			var bsp = writer.Reference( element + (uint)bspReference.Offset );
			if ( bsp is { Address: not 0 } && bsp.Class == StructureBsp )
			{
				json.WriteNumber( "VehicleFloor", bsp.Cache.TagData.F32( bsp.Address + 0x1C ) );
				json.WriteNumber( "VehicleCeiling", bsp.Cache.TagData.F32( bsp.Address + 0x20 ) );
			}
			json.WriteEndObject();
		}
		json.WriteEndArray();
		json.WriteEndObject();
	} );

	// Everything a multiplayer game reads, from shared.map and the multiplayer maps, the first map with a tag providing it.
	// That is every weapon, projectile, damage effect, equipment, collection, garbage, biped, vehicle and crate, and the models,
	// collision models (only the bipeds' with their BSPs), physics models, material effects and effects (as summaries) they use.
	// Then the globals, the multiplayer globals, the multiplayer biped's footsteps (its material effects tag) and the first person bodies.
	// Last come the sound classes and the sphere lights' falloff, read from the bitmap's pixels in textures.
	public static byte[] Game( CacheFile shared, IEnumerable<CacheFile> multiplayerMaps, TextureFile textures )
	{
		var sections = new Dictionary<string, Section>();
		Section Get( string name ) => sections.TryGetValue( name, out var section ) ? section : sections[name] = new Section();

		byte[] globals = null, multiplayerGlobals = null, footsteps = null, firstPersonBodies = null, soundClasses = null, lightFalloff = null;
		var mapNames = new List<string>();
		var bipedCollisionModels = new HashSet<string>();
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
						if ( UsedSections.Any( s => s.Class == tag.Class ) )
							wanted.Enqueue( tag );
					},
				};
			}

			return writer;
		}

		// The tags something written so far points at.
		void AddWanted()
		{
			while ( wanted.TryDequeue( out var tag ) )
			{
				var section = Get( UsedSections.First( s => s.Class == tag.Class ).Section );
				if ( section.Has( tag ) || tag.Address == 0 )
					continue;

				var writer = WriterFor( tag.Cache );
				section.Add( tag, tag.Class == Effect ? Write( json => WriteEffectSummary( json, writer, tag ) )
					: tag.Class == Collision && !bipedCollisionModels.Contains( TagJsonWriter.TagName( tag ) )
						? Write( json => writer.WriteTag( json, tag, TagLayouts.CollisionModelWithoutBsps ) )
						: WriteTag( writer, tag ) );
			}
		}

		void AddSections( CacheFile cache )
		{
			foreach ( var (name, tagClass) in GameSections )
			{
				var section = Get( name );
				foreach ( var tag in cache.Tags.Where( t => t.Cache == cache && t.Class == tagClass && t.Address != 0 && !string.IsNullOrEmpty( t.Path ) ) )
				{
					if ( section.Has( tag ) )
						continue;

					section.Add( tag, WriteTag( WriterFor( cache ), tag ) );
					if ( tagClass == Biped && ObjectCollisionModel( tag ) is { } collision )
						bipedCollisionModels.Add( TagJsonWriter.TagName( collision ) );
				}
			}

			AddWanted();
		}

		AddSections( shared );
		foreach ( var cache in multiplayerMaps )
		{
			mapNames.Add( cache.Name );
			AddSections( cache );
			writers.Remove( cache );

			// Multiplayer maps take the globals from shared.map.
			if ( globals is null && cache.Globals is { } globalsTag )
			{
				var writer = WriterFor( globalsTag.Cache );
				globals = WriteTag( writer, globalsTag );
				firstPersonBodies = Write( json => WriteFirstPersonBodies( json, cache ) );

				var sncl = GlobalsReference( writer, globalsTag, "sound globals", "legacy sound classes" );
				if ( sncl is { Address: not 0 } )
					soundClasses = Write( json => WriteSoundClasses( json, WriterFor( sncl.Cache ), sncl ) );

				var attenuation = GlobalsReference( writer, globalsTag, "rasterizer data", "distance attenuation" );
				if ( attenuation is { Address: not 0 } )
					lightFalloff = Write( json => WriteLightFalloff( json, attenuation, textures ) );

				var mulg = writer.Reference( globalsTag.Address + (uint)TagLayouts.Globals.Field( "multiplayer globals" ).Offset );
				if ( mulg is not null )
				{
					multiplayerGlobals = WriteTag( WriterFor( mulg.Cache ), mulg );
					var unit = MultiplayerUnit( WriterFor( mulg.Cache ), mulg );
					var foot = unit is null ? null : WriterFor( unit.Cache ).Reference( unit.Address + (uint)TagLayouts.ObjectDefinition.Field( "material effects" ).Offset );
					if ( foot is not null )
						footsteps = Write( json => WriteFootsteps( json, WriterFor( foot.Cache ), foot ) );
				}

				AddWanted();
			}
		}

		return Write( json =>
		{
			json.WriteStartObject();
			json.WriteString( "Source", "Halo 2 (MCC), read from the installed maps by the halo2 mount" );
			json.WriteStartArray( "Maps" );
			foreach ( var name in mapNames )
				json.WriteStringValue( name );
			json.WriteEndArray();

			foreach ( var name in GameSections.Select( x => x.Section ).Concat( UsedSections.Select( x => x.Section ) ) )
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

			WriteRawProperty( json, "Globals", globals );
			WriteRawProperty( json, "MultiplayerGlobals", multiplayerGlobals );
			WriteRawProperty( json, "Footsteps", footsteps );
			WriteRawProperty( json, "FirstPersonBodies", firstPersonBodies );
			WriteRawProperty( json, "SoundClasses", soundClasses );
			WriteRawProperty( json, "LightFalloff", lightFalloff );
			json.WriteEndObject();
		} );
	}

	// Tags written so far for one section of Game, in the order they were found.
	sealed class Section
	{
		readonly HashSet<string> _keys = new();
		public List<(string Key, byte[] Value)> Entries { get; } = new();

		public bool Has( TagEntry tag ) => _keys.Contains( TagJsonWriter.TagName( tag ) );

		public void Add( TagEntry tag, byte[] value )
		{
			var key = TagJsonWriter.TagName( tag );
			if ( _keys.Add( key ) )
				Entries.Add( (key, value) );
		}
	}

	static byte[] WriteTag( TagJsonWriter writer, TagEntry tag ) => Write( json => writer.WriteTag( json, tag, TagLayouts.For( tag ) ) );

	// An object's collision model, from its model tag, or null.
	static TagEntry ObjectCollisionModel( TagEntry tag )
	{
		var model = tag.Cache.GetTagReference( tag.Address + (uint)TagLayouts.ObjectDefinition.Field( "model" ).Offset );
		return model is { Address: not 0 } && model.Class == Model
			? model.Cache.GetTagReference( model.Address + (uint)TagLayouts.Model.Field( "collision model" ).Offset )
			: null;
	}

	// The multiplayer biped, from the multiplayer globals' runtime block.
	static TagEntry MultiplayerUnit( TagJsonWriter writer, TagEntry mulg )
	{
		var runtime = TagLayouts.MultiplayerGlobals.Field( "runtime" );
		var first = writer.BlockElements( mulg.Address + (uint)runtime.Offset, runtime.Struct.Size ).FirstOrDefault();
		return first == 0 ? null : writer.Reference( first + (uint)runtime.Struct.Field( "unit" ).Offset );
	}

	// The tag a reference in the first element of one of the globals' blocks points at, or null.
	static TagEntry GlobalsReference( TagJsonWriter writer, TagEntry globals, string block, string reference )
	{
		var field = TagLayouts.Globals.Field( block );
		var first = writer.BlockElements( globals.Address + (uint)field.Offset, field.Struct.Size ).FirstOrDefault();
		return first == 0 ? null : writer.Reference( first + (uint)field.Struct.Field( reference ).Offset );
	}

	// The sound classes by the names a sound's Class gives, in slot order, each with the fields of tags/<sncl>.sncl.json.
	// The unused slots are left out, since they share one name.
	static void WriteSoundClasses( Utf8JsonWriter json, TagJsonWriter writer, TagEntry tag )
	{
		const string Unused = "unused";
		var block = TagLayouts.SoundClasses.Field( "sound classes" );
		var names = SoundExport.SoundClasses;
		var index = 0;
		json.WriteStartObject();
		foreach ( var element in writer.BlockElements( tag.Address + (uint)block.Offset, block.Struct.Size ) )
		{
			var name = index < names.Length ? names[index] : $"slot {index}";
			index++;
			if ( name == Unused )
				continue;

			json.WritePropertyName( name );
			writer.WriteStruct( json, block.Struct, element );
		}
		json.WriteEndObject();
	}

	// { Bitmap, Curve }: the distance attenuation bitmap and the sphere light falloff it gives (see LightFalloff).
	static void WriteLightFalloff( Utf8JsonWriter json, TagEntry tag, TextureFile textures )
	{
		var volume = new BitmapTag( tag ).Bitmaps.FirstOrDefault();
		var curve = volume is null ? null : LightFalloff.Curve( volume, textures.Read( volume.PixelDataOffset, volume.PixelDataSize ) );
		if ( curve is null )
		{
			json.WriteNullValue();
			return;
		}

		json.WriteStartObject();
		json.WriteString( "Bitmap", TagJsonWriter.TagName( tag ) );
		json.WriteStartArray( "Curve" );
		foreach ( var value in curve )
			json.WriteNumberValue( value );
		json.WriteEndArray();
		json.WriteEndObject();
	}

	// The player representations' first person bodies by tag name, as the globals' references name them: what a game needs beside the model.
	// Halo shows a body only while the camera's forward z is below its cosine (0: always).
	static void WriteFirstPersonBodies( Utf8JsonWriter json, CacheFile cache )
	{
		json.WriteStartObject();
		foreach ( var body in FirstPersonBody.All( cache ).DistinctBy( b => b.Body.Tag.Id ) )
		{
			json.WriteStartObject( TagJsonWriter.TagName( body.Body.Tag ) );
			json.WriteNumber( "DontDrawOverCameraCosineAngle", body.Body.DontDrawOverCameraCosine );
			json.WriteEndObject();
		}
		json.WriteEndObject();
	}

	// A material effects tag as { Tag, Effects: [{ Sounds: { material: [tag, secondary tag] }, Effects: { ... } }] }.
	static void WriteFootsteps( Utf8JsonWriter json, TagJsonWriter writer, TagEntry foot )
	{
		var effects = TagLayouts.MaterialEffects.Field( "effects" );
		json.WriteStartObject();
		json.WriteString( "Tag", TagJsonWriter.TagName( foot ) );
		json.WriteStartArray( "Effects" );
		foreach ( var effect in writer.BlockElements( foot.Address + (uint)effects.Offset, effects.Struct.Size ) )
		{
			json.WriteStartObject();
			foreach ( var (property, block) in new[] { ("Sounds", "sounds"), ("Effects", "effects") } )
			{
				var field = effects.Struct.Field( block );
				var material = field.Struct;
				json.WriteStartObject( property );
				foreach ( var entry in writer.BlockElements( effect + (uint)field.Offset, material.Size ) )
				{
					json.WriteStartArray( writer.StringId( entry + (uint)material.Field( "material name" ).Offset ) );
					writer.WriteValue( json, material.Field( "tag" ), entry );
					writer.WriteValue( json, material.Field( "secondary tag" ), entry );
					json.WriteEndArray();
				}
				json.WriteEndObject();
			}
			json.WriteEndObject();
		}
		json.WriteEndArray();
		json.WriteEndObject();
	}

	// What a game needs to play an effect: { MaximumDamageRadius, Locations, Parts: [{ Tag, Delay, Location, ... }] }.
	// The parts of every event are in one list, each with its event's delay.
	// AScalesValues and BScalesValues name the part's values the effect's A and B scales multiply.
	// With "type-specific scale" the part's tag (a light, a sound...) is created at that scale rather than at 1.
	static void WriteEffectSummary( Utf8JsonWriter json, TagJsonWriter writer, TagEntry tag )
	{
		var layout = TagLayouts.Effect;
		var locations = layout.Field( "locations" );
		var events = layout.Field( "events" );
		var parts = events.Struct.Field( "parts" );

		json.WriteStartObject();
		json.WritePropertyName( "MaximumDamageRadius" );
		writer.WriteValue( json, layout.Field( "maximum damage radius" ), tag.Address );

		json.WriteStartArray( "Locations" );
		foreach ( var location in writer.BlockElements( tag.Address + (uint)locations.Offset, locations.Struct.Size ) )
			writer.WriteValue( json, locations.Struct.Field( "marker name" ), location );
		json.WriteEndArray();

		json.WriteStartArray( "Parts" );
		foreach ( var effectEvent in writer.BlockElements( tag.Address + (uint)events.Offset, events.Struct.Size ) )
		{
			foreach ( var part in writer.BlockElements( effectEvent + (uint)parts.Offset, parts.Struct.Size ) )
			{
				// A part without a type creates nothing.
				if ( writer.Reference( part + (uint)parts.Struct.Field( "type" ).Offset ) is null )
					continue;

				json.WriteStartObject();
				json.WritePropertyName( "Tag" );
				writer.WriteValue( json, parts.Struct.Field( "type" ), part );
				json.WritePropertyName( "Delay" );
				writer.WriteValue( json, events.Struct.Field( "delay bounds" ), effectEvent );
				foreach ( var field in new[] { "location", "velocity bounds", "create in", "velocity cone angle", "angular velocity bounds", "a scales values", "b scales values" } )
				{
					var partField = parts.Struct.Field( field );
					json.WritePropertyName( partField.JsonName );
					writer.WriteValue( json, partField, part );
				}
				json.WriteEndObject();
			}
		}
		json.WriteEndArray();
		json.WriteEndObject();
	}

	static byte[] Write( Action<Utf8JsonWriter> write )
	{
		using var stream = new MemoryStream();
		using ( var json = new Utf8JsonWriter( stream, LayoutJsonWriter.Indented ) )
			write( json );

		return stream.ToArray();
	}

	// Copies a JSON value written earlier into the document, re-indented to its place.
	static void WriteRaw( Utf8JsonWriter json, byte[] value )
	{
		using var document = JsonDocument.Parse( value );
		document.RootElement.WriteTo( json );
	}

	static void WriteRawProperty( Utf8JsonWriter json, string name, byte[] value )
	{
		json.WritePropertyName( name );
		if ( value is null )
			json.WriteNullValue();
		else
			WriteRaw( json, value );
	}
}
