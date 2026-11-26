using System;
using System.Collections.Generic;

namespace Blam.Halo1.Tags;

// Inside the namespace so these win over s&box's global Vector2/Vector3 when this compiles into the mount.
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;

/// <summary>
/// One of a scenario's structure BSPs. Each BSP's data is stored outside the tag data and loaded
/// on its own when the game switches to it.
/// </summary>
public sealed record StructureBspReference( int Index, uint FileOffset, int Size, uint Address, TagEntry Tag );

public sealed record PlayerStartingLocation( Vector3 Position, float Facing, int TeamIndex, int BspIndex );

/// <summary>
/// An object placed in the level.
/// </summary>
/// <param name="Kind">Which block it came from, e.g. "scenery" or "vehicle".</param>
/// <param name="Rotation">Yaw, pitch and roll in radians.</param>
/// <param name="NotAutomatically">The object only appears when a script creates it.</param>
/// <param name="Dead">A biped placed as a body.</param>
/// <param name="InitiallyOpen">A device (machine or control) that starts at position 1, e.g. an open door.</param>
/// <param name="Index">Its index in its kind's placement block, which script object names refer to.</param>
public sealed record ObjectPlacement( string Kind, TagEntry Object, Vector3 Position, Vector3 Rotation, int DesiredPermutation, bool NotAutomatically, bool Dead = false, bool InitiallyOpen = false, int Index = -1 )
{
	/// <summary>
	/// The object's forward, from its angles the way Halo turns a placed object: about the world's
	/// Z by the yaw, then about the world's Y by the pitch (positive raises the nose), then about
	/// the world's X by the roll.
	/// </summary>
	public Vector3 Forward
	{
		get
		{
			var (sy, cy) = MathF.SinCos( Rotation.X );
			var (sp, cp) = MathF.SinCos( Rotation.Y );
			var (sr, cr) = MathF.SinCos( Rotation.Z );
			return new Vector3( cy * cp, sy * cr - cy * sp * sr, cy * sp * cr + sy * sr );
		}
	}

	/// <summary>The object's up, turned the same way as <see cref="Forward"/>.</summary>
	public Vector3 Up
	{
		get
		{
			var (sp, cp) = MathF.SinCos( Rotation.Y );
			var (sr, cr) = MathF.SinCos( Rotation.Z );
			return new Vector3( -sp, -cp * sr, cp * cr );
		}
	}
}

public enum NetgameFlagType : ushort
{
	CtfFlag = 0,
	CtfVehicle = 1,
	OddballSpawn = 2,
	RaceTrack = 3,
	RaceVehicle = 4,
	VegasBank = 5,
	TeleportFrom = 6,
	TeleportTo = 7,
	HillFlag = 8,
}

/// <summary>
/// A multiplayer marker: flag stands, hills, oddball spawns, race checkpoints and teleporters.
/// </summary>
/// <param name="UsageId">Team for flags, order for oddball and race, the channel linking a teleporter's entrances and exits, or the hill number.</param>
public sealed record NetgameFlag( NetgameFlagType Type, int UsageId, Vector3 Position, float Facing );

/// <summary>
/// The multiplayer game types netgame equipment and starting locations are used in. The last three
/// stand for several games at once.
/// </summary>
public enum NetgameGameType : short
{
	None = 0,
	Ctf = 1,
	Slayer = 2,
	Oddball = 3,
	King = 4,
	Race = 5,
	Terminator = 6,
	Stub = 7,
	All = 12,
	AllExceptCtf = 13,
	AllExceptCtfAndRace = 14,
}

/// <summary>
/// A multiplayer item spawn: a weapon or powerup from an item collection, respawned on a timer.
/// </summary>
/// <param name="GameTypes">The game types it spawns in (up to four, None for unused slots).</param>
/// <param name="SpawnTime">Seconds between spawns, or 0 to use the collection's.</param>
/// <param name="AtRest">The "levitate" flag: the item stays where it's placed instead of dropping.</param>
public sealed record NetgameEquipment( TagEntry ItemCollection, Vector3 Position, float Facing, IReadOnlyList<NetgameGameType> GameTypes, int TeamIndex, int SpawnTime, bool AtRest, int Index = -1 )
{
	/// <summary>
	/// Whether the item spawns in a game of the given type (one of Ctf to Race), the way the game
	/// matches the four game type slots.
	/// </summary>
	public bool SpawnsIn( NetgameGameType game )
	{
		foreach ( var type in GameTypes )
		{
			if ( type == game || type == NetgameGameType.All )
				return true;
			if ( type == NetgameGameType.AllExceptCtf && game != NetgameGameType.Ctf )
				return true;
			if ( type == NetgameGameType.AllExceptCtfAndRace && game != NetgameGameType.Ctf && game != NetgameGameType.Race )
				return true;
		}

		return false;
	}
}

/// <summary>
/// A decal placed in the level.
/// </summary>
/// <param name="Direction">The direction from the decal's yaw and pitch bytes. Only its yaw is
/// used: it doesn't reliably match the surface the decal is on (see DecalModel).</param>
public sealed record DecalPlacement( TagEntry Decal, Vector3 Position, Vector3 Direction );

/// <summary>
/// Reader for the parts of <c>scnr</c> tags the mount uses.
/// </summary>
/// <remarks>
/// Skies are a block at 0x30 (16 byte tag references). Netgame flags are a block at 0x378 (148
/// bytes each: position, facing, type 0x10, usage id 0x12). Netgame equipment is a block at 0x384
/// (144 bytes each: flags 0x0 (0x1: levitate), four game types 0x4, team index 0xC, spawn time 0xE,
/// position 0x40, facing 0x4C, item collection 0x50); starting equipment at 0x390 isn't read. Decals are a block at 0x3A8 (16 bytes:
/// palette index, yaw and pitch as signed bytes scaled to +-127, position) with a palette of decal
/// tags at 0x3B4. Player starting locations are a block at
/// 0x354 (52 bytes each: position, facing in radians, team index, BSP index, game types).
/// Structure BSPs are a block at 0x5A4 (32 bytes each: file offset, size, load address, padding,
/// tag reference).
///
/// Each kind of placed object has a block of placements and a palette of object tags (48 bytes,
/// reference first). Placements start with the palette index, then placement flags at 0x4
/// (0x1: not placed automatically), desired permutation at 0x6, position at 0x8 and rotation at
/// 0x14. Bipeds have flags at 0x4C (0x1: dead), machines and controls device flags at 0x2C (0x1:
/// initially open).
/// </remarks>
public sealed class ScenarioTag
{
	/// <summary>
	/// Placement block, palette block and placement size for each kind of object read.
	/// </summary>
	static readonly (string Kind, uint Placements, uint Palette, int Size)[] ObjectBlocks =
	[
		("scenery", 0x210, 0x21C, 0x48),
		("biped", 0x228, 0x234, 0x78),
		("vehicle", 0x240, 0x24C, 0x78),
		("equipment", 0x258, 0x264, 0x28),
		("weapon", 0x270, 0x27C, 0x5C),
		("machine", 0x294, 0x2A0, 0x40),
		("control", 0x2AC, 0x2B8, 0x40),
		("light fixture", 0x2C4, 0x2D0, 0x58),
	];

	public IReadOnlyList<StructureBspReference> StructureBsps { get; }
	public IReadOnlyList<PlayerStartingLocation> PlayerStartingLocations { get; }
	public IReadOnlyList<TagEntry> Skies { get; }
	public IReadOnlyList<ObjectPlacement> Objects { get; }
	public IReadOnlyList<NetgameFlag> NetgameFlags { get; }
	public IReadOnlyList<NetgameEquipment> NetgameEquipment { get; }
	public IReadOnlyList<DecalPlacement> Decals { get; }

	public ScenarioTag( CacheFile cache, TagEntry tag )
	{
		var data = cache.TagData;

		var skies = new List<TagEntry>();
		foreach ( var entry in data.Block( tag.Address + 0x30, 0x10 ) )
		{
			var sky = cache.GetTagReference( data, entry );
			if ( sky is not null ) skies.Add( sky );
		}
		Skies = skies;

		var objects = new List<ObjectPlacement>();
		foreach ( var (kind, placements, palette, size) in ObjectBlocks )
		{
			var paletteTags = new List<TagEntry>();
			foreach ( var entry in data.Block( tag.Address + palette, 0x30 ) )
				paletteTags.Add( cache.GetTagReference( data, entry ) );

			var placementIndex = -1;
			foreach ( var entry in data.Block( tag.Address + placements, size ) )
			{
				placementIndex++;
				var index = data.I16( entry );
				if ( index < 0 || index >= paletteTags.Count || paletteTags[index] is null )
					continue;

				objects.Add( new ObjectPlacement(
					Kind: kind,
					Object: paletteTags[index],
					Position: data.Vec3( entry + 0x8 ),
					Rotation: data.Vec3( entry + 0x14 ),
					DesiredPermutation: data.I16( entry + 0x6 ),
					NotAutomatically: (data.U16( entry + 0x4 ) & 0x1) != 0,
					Dead: kind == "biped" && (data.U32( entry + 0x4C ) & 0x1) != 0,
					InitiallyOpen: kind is "machine" or "control" && (data.U32( entry + 0x2C ) & 0x1) != 0,
					Index: placementIndex ) );
			}
		}
		Objects = objects;

		var flags = new List<NetgameFlag>();
		foreach ( var entry in data.Block( tag.Address + 0x378, 0x94 ) )
			flags.Add( new NetgameFlag( (NetgameFlagType)data.U16( entry + 0x10 ), data.I16( entry + 0x12 ), data.Vec3( entry ), data.F32( entry + 0xC ) ) );
		NetgameFlags = flags;

		var equipment = new List<NetgameEquipment>();
		var equipmentIndex = -1;
		foreach ( var entry in data.Block( tag.Address + 0x384, 0x90 ) )
		{
			equipmentIndex++;
			var collection = cache.GetTagReference( data, entry + 0x50 );
			if ( collection is null )
				continue;

			var gameTypes = new NetgameGameType[4];
			for ( var i = 0; i < 4; i++ )
				gameTypes[i] = (NetgameGameType)data.I16( entry + 0x4 + (uint)(i * 2) );

			equipment.Add( new NetgameEquipment(
				ItemCollection: collection,
				Position: data.Vec3( entry + 0x40 ),
				Facing: data.F32( entry + 0x4C ),
				GameTypes: gameTypes,
				TeamIndex: data.I16( entry + 0xC ),
				SpawnTime: data.I16( entry + 0xE ),
				AtRest: (data.U32( entry ) & 0x1) != 0,
				Index: equipmentIndex ) );
		}
		NetgameEquipment = equipment;

		var decalPalette = new List<TagEntry>();
		foreach ( var entry in data.Block( tag.Address + 0x3B4, 0x10 ) )
			decalPalette.Add( cache.GetTagReference( data, entry ) );

		var decals = new List<DecalPlacement>();
		foreach ( var entry in data.Block( tag.Address + 0x3A8, 0x10 ) )
		{
			var index = data.I16( entry );
			if ( index < 0 || index >= decalPalette.Count || decalPalette[index] is null )
				continue;

			var yaw = (sbyte)data.U8( entry + 0x2 ) / 127f * MathF.PI;
			var pitch = (sbyte)data.U8( entry + 0x3 ) / 127f * MathF.PI * 0.5f;
			var direction = new Vector3( MathF.Cos( pitch ) * MathF.Cos( yaw ), MathF.Cos( pitch ) * MathF.Sin( yaw ), MathF.Sin( pitch ) );
			decals.Add( new DecalPlacement( decalPalette[index], data.Vec3( entry + 0x4 ), direction ) );
		}
		Decals = decals;

		var bsps = new List<StructureBspReference>();
		foreach ( var entry in data.Block( tag.Address + 0x5A4, 0x20 ) )
		{
			bsps.Add( new StructureBspReference(
				Index: bsps.Count,
				FileOffset: data.U32( entry ),
				Size: data.I32( entry + 0x4 ),
				Address: data.U32( entry + 0x8 ),
				Tag: cache.GetTag( data.U32( entry + 0x10 + 0xC ) ) ) );
		}

		var starts = new List<PlayerStartingLocation>();
		foreach ( var entry in data.Block( tag.Address + 0x354, 0x34 ) )
		{
			starts.Add( new PlayerStartingLocation(
				Position: data.Vec3( entry ),
				Facing: data.F32( entry + 0xC ),
				TeamIndex: data.I16( entry + 0x10 ),
				BspIndex: data.I16( entry + 0x12 ) ) );
		}

		StructureBsps = bsps;
		PlayerStartingLocations = starts;
	}
}
