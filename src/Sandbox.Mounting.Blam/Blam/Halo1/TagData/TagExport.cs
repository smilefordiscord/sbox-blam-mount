using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Blam.Halo1.Tags;

namespace Blam.Halo1.TagData;

/// <summary>
/// Tag data as JSON documents, for games built on the mount to read gameplay values straight from
/// the maps: one document per tag, one per map (spawns and placements), and one with everything a
/// multiplayer game needs (see <see cref="Game"/>).
/// </summary>
public static partial class TagExport
{
	static readonly TagClass Bitmap = TagClasses.Bitmap;
	static readonly TagClass Weapon = new( "weap" );
	static readonly TagClass Effect = new( "effe" );
	static readonly TagClass Animations = TagClasses.ModelAnimations;
	static readonly TagClass Collision = new( "coll" );
	static readonly TagClass Vehicle = new( "vehi" );

	/// <summary>
	/// The sections of <see cref="Game"/> that hold every tag of a class found in the maps.
	/// </summary>
	/// <summary>
	/// Where the mount serves its resources, for documents that point at textures.
	/// </summary>
	public const string MountRoot = "mount://haloce/";

	static readonly (string Section, TagClass Class)[] GameSections =
	[
		("Weapons", new( "weap" )),
		("Projectiles", new( "proj" )),
		("DamageEffects", new( "jpt!" )),
		("ContinuousDamageEffects", new( "cdmg" )),
		("Equipment", new( "eqip" )),
		("ItemCollections", new( "itmc" )),
		("Garbage", new( "garb" )),
		("Bipeds", new( "bipd" )),
		("Vehicles", new( "vehi" )),
		("LoopingSounds", new( "lsnd" )),
	];

	/// <summary>
	/// Names of the effects in a biped's footsteps (material effects) tag, in order.
	/// </summary>
	static readonly string[] FootstepNames = ["Walk", "Run", "Slide", "Shuffle", "Jump", "Land"];

	/// <summary>
	/// True if <see cref="Tag"/> can write this tag.
	/// </summary>
	public static bool CanExport( TagEntry tag ) =>
		!string.IsNullOrEmpty( tag.Path ) && tag.Class != TagClasses.Scenario && (tag.Class == FontTag.Class || TagLayouts.For( tag ) is not null);

	/// <summary>
	/// Where a tag's document goes: <c>tags/weapons/pistol/pistol.weap.json</c>.
	/// </summary>
	public static string TagPath( TagEntry tag ) => $"tags/{TagJsonWriter.TagName( tag )}.json";

	/// <summary>
	/// A tag as JSON, all of the fields in its layout.
	/// </summary>
	public static byte[] Tag( CacheFile cache, TagEntry tag )
	{
		if ( tag.Class == FontTag.Class )
			return Write( json => WriteFont( json, cache, tag ) );

		var layout = TagLayouts.For( tag ) ?? throw new ArgumentException( $"{tag} can't be exported", nameof( tag ) );
		if ( !tag.Is( TagClasses.Object ) || !ObjectAttachmentPlan.HasAny( cache, tag ) )
			return new TagJsonWriter( cache ).WriteTag( tag, layout );

		// Objects with attachments also describe their attachments prefab.
		return Write( json =>
		{
			var writer = new TagJsonWriter( cache ) { TagAddress = tag.Address };
			json.WriteStartObject();
			writer.WriteFields( json, layout, tag.Address );
			layout.Extra?.Invoke( json, writer, tag.Address );
			json.WritePropertyName( "AttachmentsPrefab" );
			WriteAttachmentsPrefab( json, cache, tag );
			json.WriteEndObject();
		} );
	}

	/// <summary>
	/// An object's attachments prefab (<see cref="ObjectAttachmentPlan"/>):
	/// <c>{ Prefab, Children: [{ Name, Kind, Tag, Attachment, Marker, ModelAttachment,
	/// FirstPersonAttachment, Position, Rotation, PrimaryScale, SecondaryScale, ChangeColor,
	/// RestValue, OnAtRest, Tags, Light, LensFlare, Sounds }] }</c>. Position is in world units on the
	/// model at its default pose, Rotation i, j, k, w. Scales are "a".."d" (A-D out) or "none".
	/// <c>Light</c> (lights, and lens flares, whose light it is): <c>Type</c> point or spot, <c>Dynamic</c>,
	/// <c>ColorAtZero</c> and <c>ColorAtOne</c> (s&amp;box LightColor, Halo's color x pi, with white as the
	/// change color) and <c>RadiusAtZero</c>/<c>RadiusAtOne</c> (s&amp;box units): for a scale value v the
	/// light is lerp(at zero, at one, v), off while its color is black. <c>ColorAlpha</c> is the color
	/// bounds' alphas (see <see cref="LightTag.ColorAlpha"/>): a game that knows the object's
	/// <c>ChangeColor</c> multiplies the color by it, or where either alpha is set replaces the white
	/// share by it. <c>LensFlare</c>: the flare's model and lens tag; set the renderer attribute
	/// HaloFunctionScale to v, and HaloLightColor (Halo's 0-1 color) to the color worked out that way.
	/// <c>Sounds</c> (looping sounds): the track events, played while the scale is on.
	/// </summary>
	static void WriteAttachmentsPrefab( Utf8JsonWriter json, CacheFile cache, TagEntry tag )
	{
		const float WorldScale = WorldUnits.SboxScale;
		static string Path( TagEntry t ) => t.Path.Replace( '\\', '/' );

		json.WriteStartObject();
		json.WriteString( "Prefab", $"{MountRoot}{Path( tag )}__attachments.prefab" );
		json.WriteStartArray( "Children" );
		foreach ( var child in ObjectAttachmentPlan.Plan( cache, tag ) )
		{
			json.WriteStartObject();
			json.WriteString( "Name", child.Name );
			json.WriteString( "Kind", ObjectAttachmentPlan.KindTag( child.Kind )["halo_".Length..] );
			json.WriteString( "Tag", TagJsonWriter.TagName( child.Type ) );
			if ( child.Index >= 0 )
				json.WriteNumber( "Attachment", child.Index );
			else
				json.WriteNull( "Attachment" );
			json.WriteString( "Marker", child.Marker );
			json.WriteString( "ModelAttachment", child.ModelAttachment );
			json.WriteString( "FirstPersonAttachment", child.FirstPersonAttachment );
			WriteNumbers( json, "Position", child.Position.X, child.Position.Y, child.Position.Z );
			WriteNumbers( json, "Rotation", child.Rotation.X, child.Rotation.Y, child.Rotation.Z, child.Rotation.W );
			json.WriteString( "PrimaryScale", ObjectAttachmentPlan.ScaleName( child.PrimaryScale ) );
			json.WriteString( "SecondaryScale", ObjectAttachmentPlan.ScaleName( child.SecondaryScale ) );
			json.WriteString( "ChangeColor", child.ChangeColor is >= 1 and <= 4 ? $"{(char)('a' + child.ChangeColor - 1)}" : "none" );
			json.WriteNumber( "RestValue", child.RestValue );
			json.WriteBoolean( "OnAtRest", child.OnAtRest );
			json.WriteStartArray( "Tags" );
			foreach ( var name in child.Tags )
				json.WriteStringValue( name );
			json.WriteEndArray();

			if ( (child.Kind is AttachmentKind.Light or AttachmentKind.LensFlare) && child.Type.Class == EffectTagClasses.Light )
			{
				var light = new LightTag( cache, child.Type );
				var zero = light.ColorAt( 0 ) * MathF.PI;
				var one = light.ColorAt( 1 ) * MathF.PI;
				json.WriteStartObject( "Light" );
				json.WriteString( "Type", light.IsSpot ? "spot" : "point" );
				json.WriteBoolean( "Dynamic", light.IsDynamic );
				WriteNumbers( json, "ColorAtZero", zero.X, zero.Y, zero.Z );
				WriteNumbers( json, "ColorAtOne", one.X, one.Y, one.Z );
				WriteNumbers( json, "ColorAlpha", light.ColorAlpha.X, light.ColorAlpha.Y );
				json.WriteNumber( "RadiusAtZero", light.RadiusAt( 0 ) * WorldScale );
				json.WriteNumber( "RadiusAtOne", light.RadiusAt( 1 ) * WorldScale );
				if ( light.IsSpot )
				{
					WriteNumbers( json, "ConeInnerOuter", MathF.Min( light.FalloffAngle, light.SpotCutoffAngle ) * 180 / MathF.PI, light.SpotCutoffAngle * 180 / MathF.PI );
					if ( light.Gel is not null )
						json.WriteString( "Gel", $"{MountRoot}{Path( light.Gel )}.vtex" );
				}
				json.WriteEndObject();
			}

			if ( child.LensFlare is not null )
			{
				json.WriteStartObject( "LensFlare" );
				json.WriteString( "Model", $"{MountRoot}{Path( child.Type )}__lens_flare.vmdl" );
				json.WriteString( "Lens", TagJsonWriter.TagName( child.LensFlare ) );
				json.WriteEndObject();
			}

			if ( child.Kind == AttachmentKind.LoopingSound )
			{
				json.WriteStartArray( "Sounds" );
				var lsnd = new LoopingSoundTag( cache, child.Type );
				for ( var i = 0; i < lsnd.Tracks.Count; i++ )
					json.WriteStringValue( $"{MountRoot}{Path( child.Type )}__track{i}.sound" );
				json.WriteEndArray();
			}

			json.WriteEndObject();
		}
		json.WriteEndArray();
		json.WriteEndObject();
	}

	static void WriteFog( Utf8JsonWriter json, string name, System.Numerics.Vector3 color, float maximumDensity, float startDistance, float opaqueDistance )
	{
		json.WriteStartObject( name );
		WriteNumbers( json, "Color", color.X, color.Y, color.Z );
		json.WriteNumber( "MaximumDensity", maximumDensity );
		json.WriteNumber( "StartDistance", startDistance );
		json.WriteNumber( "OpaqueDistance", opaqueDistance );
		json.WriteEndObject();
	}

	static void WriteNumbers( Utf8JsonWriter json, string name, params float[] values )
	{
		json.WriteStartArray( name );
		foreach ( var value in values )
			json.WriteNumberValue( float.IsFinite( value ) ? value : 0 );
		json.WriteEndArray();
	}

	/// <summary>
	/// A map's scenario as JSON: player starts, netgame flags, netgame equipment (item spawns),
	/// starting equipment, the vehicles, weapons and equipment placed in the level, the skies'
	/// outdoor and indoor fogs, and each structure BSP's vehicle limits and clusters' skies.
	/// </summary>
	public static byte[] Map( CacheFile cache ) => Write( json =>
	{
		var writer = new TagJsonWriter( cache );
		var scenario = cache.Scenario;
		var layout = TagLayouts.Scenario;
		writer.TagAddress = scenario.Address;

		json.WriteStartObject();
		json.WriteString( "Map", cache.Name );
		json.WriteString( "Scenario", TagJsonWriter.TagName( scenario ) );
		json.WriteString( "ScenarioType", cache.ScenarioType.ToString() );

		// The scenario's local north (radians, 0x4C): the heading the assault rifle's compass calls north.
		json.WriteNumber( "LocalNorth", cache.TagData.F32( scenario.Address + 0x4C ) );
		foreach ( var (name, field) in new[]
		{
			("PlayerStarts", "player starting locations"),
			("NetgameFlags", "netgame flags"),
			("NetgameEquipment", "netgame equipment"),
			("StartingEquipment", "starting equipment"),
			("Vehicles", "vehicles"),
			("Weapons", "weapons"),
			("Equipment", "equipment"),
		} )
		{
			var block = layout.Field( field );
			json.WritePropertyName( name );
			writer.WriteBlock( json, block.Struct, scenario.Address + (uint)block.Offset );
		}

		// Each sky's atmospheric fogs (see SkyTag), null for a sky that isn't there. The camera's
		// cluster picks one: its sky's outdoor fog, or the first sky's indoor fog where it has none.
		var scenarioTag = new ScenarioTag( cache, scenario );
		json.WriteStartArray( "Skies" );
		foreach ( var skyEntry in scenarioTag.Skies )
		{
			if ( skyEntry is null )
			{
				json.WriteNullValue();
				continue;
			}

			var sky = new SkyTag( cache, skyEntry );
			json.WriteStartObject();
			WriteFog( json, "OutdoorFog", sky.OutdoorFogColor, sky.OutdoorFogMaximumDensity, sky.OutdoorFogStartDistance, sky.OutdoorFogOpaqueDistance );
			WriteFog( json, "IndoorFog", sky.IndoorFogColor, sky.IndoorFogMaximumDensity, sky.IndoorFogStartDistance, sky.IndoorFogOpaqueDistance );
			json.WriteEndObject();
		}
		json.WriteEndArray();

		// Each structure BSP's vehicle floor and ceiling (world units, 0x10/0x14): vehicles are kept
		// between them (Banshees can't fly out of the level). And each cluster's sky (clusters 0x134,
		// 104 bytes: sky index 0x0, -1 for none), which picks the camera's atmospheric fog.
		json.WriteStartArray( "StructureBsps" );
		foreach ( var reference in scenarioTag.StructureBsps )
		{
			json.WriteStartObject();
			json.WriteString( "Bsp", reference.Tag is null ? null : TagJsonWriter.TagName( reference.Tag ) );
			try
			{
				var bsp = new StructureBspTag( cache, reference );
				json.WriteNumber( "VehicleFloor", bsp.Data.F32( bsp.TagAddress + 0x10 ) );
				json.WriteNumber( "VehicleCeiling", bsp.Data.F32( bsp.TagAddress + 0x14 ) );
				json.WriteStartArray( "ClusterSkies" );
				foreach ( var cluster in bsp.Data.Block( bsp.TagAddress + 0x134, 0x68 ) )
					json.WriteNumberValue( bsp.Data.I16( cluster ) );
				json.WriteEndArray();
			}
			catch ( Exception )
			{
			}
			json.WriteEndObject();
		}
		json.WriteEndArray();
		json.WriteEndObject();
	} );

	/// <summary>
	/// Everything a multiplayer game reads, from the given maps (the first map with a tag
	/// provides it):
	/// <list type="bullet">
	/// <item>every weapon, projectile, damage effect, continuous damage effect, equipment, item
	/// collection, garbage, biped, vehicle and looping sound (as its tracks), by tag name;</item>
	/// <item>the collision models, effects and animation graphs those use: effects as their
	/// maximum damage radius, location (marker) names and parts (flattened across events, with the
	/// event's delay), animation graphs as their animation names; and the vehicles' physics and
	/// their models' nodes and markers;</item>
	/// <item>how each weapon, equipment and garbage item lies once dropped (ItemRestPoses, see
	/// <see cref="ItemRestPoseJson"/>);</item>
	/// <item>the globals (player and multiplayer information, grenades, falling damage, materials)
	/// of the first map;</item>
	/// <item>the multiplayer biped's footstep sounds (walk, run, slide, shuffle, jump, land; each
	/// one sound per material type) and its HUD's shield and health sounds.</item>
	/// </list>
	/// </summary>
	public static byte[] Game( IEnumerable<CacheFile> maps )
	{
		var sections = new Dictionary<string, Section>();
		Section Get( string name ) => sections.TryGetValue( name, out var section ) ? section : sections[name] = new Section();

		byte[] globals = null, footsteps = null, hudSounds = null;
		var mapNames = new List<string>();

		foreach ( var cache in maps )
		{
			mapNames.Add( cache.Name );

			// Effects, animation graphs and collision models are only wanted when something uses them.
			var wanted = new Queue<TagEntry>();
			var writer = new TagJsonWriter( cache )
			{
				ReferenceWritten = tag =>
				{
					if ( tag.Class == Effect || tag.Class == Animations || tag.Class == Collision )
						wanted.Enqueue( tag );
				},
			};

			foreach ( var (name, tagClass) in GameSections )
			{
				var section = Get( name );
				foreach ( var tag in cache.Tags.Where( t => t.Class == tagClass && !string.IsNullOrEmpty( t.Path ) ) )
				{
					if ( !section.Has( tag ) )
						section.Add( tag, tagClass == new TagClass( "lsnd" ) ? Write( json => WriteLoopingSound( json, writer, tag ) ) : WriteTag( writer, tag ) );
				}
			}

			// How each weapon, equipment and garbage item lies once dropped.
			foreach ( var tag in cache.Tags.Where( IsItem ) )
			{
				if ( !Get( "ItemRestPoses" ).Has( tag ) && ItemRestPoseJson( cache, tag ) is { } restPose )
					Get( "ItemRestPoses" ).Add( tag, restPose );
			}

			var globalsTag = cache.Tags.FirstOrDefault( t => t.Class == new TagClass( "matg" ) );
			if ( globals is null && globalsTag is not null )
			{
				globals = WriteTag( writer, globalsTag );

				var unit = MultiplayerUnit( writer, globalsTag );
				if ( unit is not null )
				{
					footsteps = Write( json => WriteFootsteps( json, writer, unit ) );
					hudSounds = Write( json => WriteHudSounds( json, writer, unit ) );
				}
			}

			// Vehicles' physics and their models' skeletons and markers (seats, weapons, cameras).
			foreach ( var vehicle in cache.Tags.Where( t => t.Class == Vehicle ) )
			{
				var physics = writer.Reference( vehicle.Address + (uint)TagLayouts.Vehicle.Field( "physics" ).Offset );
				if ( physics is not null && !Get( "Physics" ).Has( physics ) )
					Get( "Physics" ).Add( physics, WriteTag( writer, physics ) );

				var model = writer.Reference( vehicle.Address + (uint)TagLayouts.Vehicle.Field( "model" ).Offset );
				if ( model is not null && model.Class == TagClasses.GbxModel && !Get( "Models" ).Has( model ) )
					Get( "Models" ).Add( model, Write( json => WriteModelSummary( json, writer, model ) ) );
			}

			while ( wanted.TryDequeue( out var tag ) )
			{
				var section = Get( tag.Class == Effect ? "Effects" : tag.Class == Animations ? "Animations" : "CollisionModels" );
				if ( section.Has( tag ) )
					continue;

				if ( tag.Class == Effect )
					section.Add( tag, Write( json => WriteEffectSummary( json, writer, tag ) ) );
				else if ( tag.Class == Animations )
					section.Add( tag, Write( json => WriteAnimationNames( json, writer, tag ) ) );
				else
					section.Add( tag, WriteTag( writer, tag ) );
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

			foreach ( var name in GameSections.Select( x => x.Section ).Concat( ["Effects", "CollisionModels", "Animations", "Physics", "Models", "ItemRestPoses"] ) )
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
			WriteRawProperty( json, "Footsteps", footsteps );
			WriteRawProperty( json, "UnitHudSounds", hudSounds );
			json.WriteEndObject();
		} );
	}

	/// <summary>
	/// Tags written so far for one section of <see cref="Game"/>, in the order they were found.
	/// </summary>
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

	/// <summary>
	/// The multiplayer biped, from the globals' multiplayer information.
	/// </summary>
	static TagEntry MultiplayerUnit( TagJsonWriter writer, TagEntry globals )
	{
		var block = TagLayouts.Globals.Field( "multiplayer information" );
		var first = writer.BlockElements( globals.Address + (uint)block.Offset, block.Struct.Size ).FirstOrDefault();
		return first == 0 ? null : writer.Reference( first + (uint)block.Struct.Field( "unit" ).Offset );
	}

	/// <summary>
	/// <c>{ "Walk": [sound per material type], "Run": [...], ... }</c> from a biped's footsteps.
	/// </summary>
	static void WriteFootsteps( Utf8JsonWriter json, TagJsonWriter writer, TagEntry biped )
	{
		json.WriteStartObject();
		var foot = writer.Reference( biped.Address + (uint)TagLayouts.Biped.Field( "footsteps" ).Offset );
		if ( foot is not null )
		{
			var effects = TagLayouts.MaterialEffects.Field( "effects" );
			var materials = effects.Struct.Field( "materials" );
			var sound = materials.Struct.Field( "sound" );

			var index = 0;
			foreach ( var effect in writer.BlockElements( foot.Address + (uint)effects.Offset, effects.Struct.Size ) )
			{
				json.WriteStartArray( index < FootstepNames.Length ? FootstepNames[index] : $"Effect{index}" );
				foreach ( var material in writer.BlockElements( effect + (uint)materials.Offset, materials.Struct.Size ) )
					writer.WriteValue( json, sound, material );
				json.WriteEndArray();
				index++;
			}
		}
		json.WriteEndObject();
	}

	/// <summary>
	/// The sounds of a biped's (first) HUD interface: <c>[{ "Sound", "LatchedTo", "Scale" }]</c>.
	/// </summary>
	static void WriteHudSounds( Utf8JsonWriter json, TagJsonWriter writer, TagEntry biped )
	{
		var interfaces = TagLayouts.Biped.Field( "new hud interfaces" );
		var first = writer.BlockElements( biped.Address + (uint)interfaces.Offset, interfaces.Struct.Size ).FirstOrDefault();
		var hud = first == 0 ? null : writer.Reference( first );
		if ( hud is null )
		{
			json.WriteStartArray();
			json.WriteEndArray();
			return;
		}

		var sounds = TagLayouts.UnitHudInterface.Field( "sounds" );
		writer.WriteBlock( json, sounds.Struct, hud.Address + (uint)sounds.Offset );
	}

	/// <summary>
	/// A looping sound as its tracks: <c>[{ "Start", "Loop", "End", ... }]</c>.
	/// </summary>
	static void WriteLoopingSound( Utf8JsonWriter json, TagJsonWriter writer, TagEntry tag )
	{
		var tracks = TagLayouts.SoundLooping.Field( "tracks" );
		writer.WriteBlock( json, tracks.Struct, tag.Address + (uint)tracks.Offset );
	}

	/// <summary>
	/// An animation graph as its animation names.
	/// </summary>
	static void WriteAnimationNames( Utf8JsonWriter json, TagJsonWriter writer, TagEntry tag )
	{
		var animations = TagLayouts.ModelAnimations.Field( "animations" );
		var name = animations.Struct.Field( "name" );

		json.WriteStartArray();
		foreach ( var animation in writer.BlockElements( tag.Address + (uint)animations.Offset, animations.Struct.Size ) )
			writer.WriteValue( json, name, animation );
		json.WriteEndArray();
	}

	/// <summary>
	/// What a game needs to play an effect: <c>{ MaximumDamageRadius, Locations: [marker names],
	/// Parts: [{ Tag, Delay, Location, VelocityBounds, CreateIn }] }</c>, the parts of every event
	/// in one list, each with its event's delay.
	/// </summary>
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
				foreach ( var field in new[] { "location", "velocity bounds", "create in" } )
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

	/// <summary>
	/// A model's skeleton and markers: <c>{ Nodes: [{ Name, Parent, Translation, Rotation }],
	/// Markers: [{ Name, Node, Region, Permutation, Translation, Rotation }] }</c>, one marker per
	/// marker instance. Translations are relative to the parent node or the marker's node, in
	/// world units; rotations are i, j, k, w as stored (conjugated).
	/// </summary>
	static void WriteModelSummary( Utf8JsonWriter json, TagJsonWriter writer, TagEntry tag )
	{
		var layout = TagLayouts.ModelSkeleton;
		var nodes = layout.Field( "nodes" );
		var markers = layout.Field( "markers" );
		var instances = markers.Struct.Field( "instances" );

		void Property( string name, TagStruct element, string field, uint address )
		{
			json.WritePropertyName( name );
			writer.WriteValue( json, element.Field( field ), address );
		}

		json.WriteStartObject();
		json.WriteStartArray( "Nodes" );
		foreach ( var node in writer.BlockElements( tag.Address + (uint)nodes.Offset, nodes.Struct.Size ) )
		{
			json.WriteStartObject();
			Property( "Name", nodes.Struct, "name", node );
			Property( "Parent", nodes.Struct, "parent node index", node );
			Property( "Translation", nodes.Struct, "default translation", node );
			Property( "Rotation", nodes.Struct, "default rotation", node );
			json.WriteEndObject();
		}
		json.WriteEndArray();

		json.WriteStartArray( "Markers" );
		foreach ( var marker in writer.BlockElements( tag.Address + (uint)markers.Offset, markers.Struct.Size ) )
		{
			foreach ( var instance in writer.BlockElements( marker + (uint)instances.Offset, instances.Struct.Size ) )
			{
				json.WriteStartObject();
				Property( "Name", markers.Struct, "name", marker );
				Property( "Node", instances.Struct, "node index", instance );
				Property( "Region", instances.Struct, "region index", instance );
				Property( "Permutation", instances.Struct, "permutation index", instance );
				Property( "Translation", instances.Struct, "translation", instance );
				Property( "Rotation", instances.Struct, "rotation", instance );
				json.WriteEndObject();
			}
		}
		json.WriteEndArray();
		json.WriteEndObject();
	}

	/// <summary>
	/// A font's metrics and glyphs, from <see cref="FontTag"/>. The mount packs the glyphs onto one
	/// page texture (<c>Page</c>, white with coverage in alpha); each glyph's top left corner goes at
	/// (pen x - origin x, baseline - origin y), then the pen moves right by its advance. Lines start
	/// leading width in, the first baseline is ascending height down, and baselines are line
	/// height apart. No kerning.
	/// </summary>
	static void WriteFont( Utf8JsonWriter json, CacheFile cache, TagEntry tag )
	{
		var font = new FontTag( cache, tag );

		json.WriteStartObject();
		json.WriteNumber( "Flags", font.Flags );
		json.WriteNumber( "AscendingHeight", font.AscendingHeight );
		json.WriteNumber( "DescendingHeight", font.DescendingHeight );
		json.WriteNumber( "LeadingHeight", font.LeadingHeight );
		json.WriteNumber( "LeadingWidth", font.LeadingWidth );
		json.WriteNumber( "LineHeight", font.LineHeight );
		json.WriteString( "Bold", TagJsonWriter.TagName( font.Bold ) );
		json.WriteString( "Italic", TagJsonWriter.TagName( font.Italic ) );
		json.WriteString( "Condense", TagJsonWriter.TagName( font.Condense ) );
		json.WriteString( "Underline", TagJsonWriter.TagName( font.Underline ) );
		json.WriteString( "Page", $"{MountRoot}{tag.Path.Replace( '\\', '/' )}/page.vtex" );
		json.WriteNumber( "PageWidth", font.Page.Width );
		json.WriteNumber( "PageHeight", font.Page.Height );

		json.WriteStartArray( "Characters" );
		foreach ( var glyph in font.Characters )
		{
			json.WriteStartObject();
			json.WriteNumber( "CodePoint", glyph.CodePoint );
			json.WriteString( "Character", glyph.CodePoint.ToString() );
			json.WriteNumber( "Advance", glyph.Advance );
			json.WriteNumber( "Width", glyph.Width );
			json.WriteNumber( "Height", glyph.Height );
			json.WriteNumber( "OriginX", glyph.OriginX );
			json.WriteNumber( "OriginY", glyph.OriginY );
			json.WriteNumber( "PageX", glyph.PageX );
			json.WriteNumber( "PageY", glyph.PageY );
			json.WriteEndObject();
		}
		json.WriteEndArray();
		json.WriteEndObject();
	}

	static byte[] Write( Action<Utf8JsonWriter> write )
	{
		using var stream = new MemoryStream();
		using ( var json = new Utf8JsonWriter( stream, TagJsonWriter.Indented ) )
			write( json );

		return stream.ToArray();
	}

	/// <summary>
	/// Copies a JSON value written earlier into the document. Going through
	/// <see cref="JsonDocument"/> re-indents it to its place.
	/// </summary>
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
