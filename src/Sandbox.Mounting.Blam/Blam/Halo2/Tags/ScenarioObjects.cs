using System;
using System.Collections.Generic;
using System.Linq;

namespace Blam.Halo2.Tags;

// Inside the namespace so these win over s&box's global vector types when this compiles into the mount.
using Vector3 = System.Numerics.Vector3;

/// <summary>
/// An object placed in a scenario (or by a multiplayer spawn).
/// </summary>
/// <param name="Kind">The placement block it comes from: "scenery", "machine", "crate"...</param>
/// <param name="Object">The object tag (any <c>obje</c> child).</param>
/// <param name="Rotation">Yaw, pitch, roll in radians (see <see cref="Forward"/>).</param>
/// <param name="Scale">1 where the scenario stores 0.</param>
/// <param name="Variant">The model variant named by the placement, or null for the object's default.</param>
/// <param name="NotAutomatically">The object only appears when a script creates it.</param>
/// <param name="ObjectId">The placement's object id (uint32 unique id, int16 origin BSP, uint8 type, uint8 source,
/// little endian), which lightmaps name the scenery they were baked with by (<see cref="SceneryObjectLighting"/>);
/// 0 for multiplayer spawns.</param>
/// <param name="Data">What else the scenario's placement holds (null for multiplayer spawns).</param>
public sealed record ObjectPlacement( string Kind, TagEntry Object, Vector3 Position, Vector3 Rotation, float Scale, string Variant, bool NotAutomatically = false, ulong ObjectId = 0,
	ScenarioPlacementData Data = null )
{
	/// <summary>The BSP the placement belongs to (from its object id), -1 for none.</summary>
	public int OriginBsp => (short)(ObjectId >> 32);

	/// <summary>The object type its object id names (<see cref="ObjectType"/>).</summary>
	public int IdType => (sbyte)(ObjectId >> 48);

	/// <summary>Where its object id says it comes from: 0 structure, 1 editor (placed), 2 dynamic, 3 legacy.</summary>
	public int IdSource => (sbyte)(ObjectId >> 56);

	/// <summary>
	/// The object's forward, from its angles the way Halo turns a placed object: about the world's Z by the
	/// yaw, then about the world's Y by the pitch (positive raises the nose), then about the world's X by
	/// the roll. The same rule as Halo CE's placements (the CE mount's ObjectPlacement).
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

	public Vector3 Left => Vector3.Cross( Up, Forward );

	/// <summary>
	/// The placement as a frame (position, rotation, scale).
	/// </summary>
	public ObjectFrame Frame
	{
		get
		{
			var f = Forward;
			var l = Left;
			var u = Up;
			var basis = new System.Numerics.Matrix4x4( f.X, f.Y, f.Z, 0, l.X, l.Y, l.Z, 0, u.X, u.Y, u.Z, 0, 0, 0, 0, 1 );
			return new ObjectFrame( Position, System.Numerics.Quaternion.Normalize( System.Numerics.Quaternion.CreateFromRotationMatrix( basis ) ), Scale );
		}
	}

	/// <summary>
	/// A point in object space (Halo world units) placed in the world.
	/// </summary>
	public Vector3 Transform( Vector3 local ) => Position + Scale * (local.X * Forward + local.Y * Left + local.Z * Up);

	/// <summary>
	/// A number in [0, 1) that's the same every time for this placement, for the choices Halo makes at
	/// random when it creates an object (permutations); <paramref name="salt"/> tells several apart.
	/// </summary>
	public float Random( int salt ) => Blam.Halo1.StableRandom.At( Position, 0x4832 + salt );
}

/// <summary>
/// The rest of a scenario placement, for creating it as MCC does (<see cref="ObjectCreationOrder"/>).
/// </summary>
/// <param name="Type">The object type of its placement block (<see cref="ObjectType"/>).</param>
/// <param name="Index">Its index in that block.</param>
/// <param name="NameIndex">Its object name (+2), -1 for none: named objects are scripts' to address.</param>
/// <param name="Flags">Placement flags (+4): 1 not automatically, 0x40 never placed.</param>
/// <param name="ManualBsps">BSPs it's placed in under manual BSP placement (+0x26, a bit per BSP).</param>
/// <param name="BspPolicy">+0x30: 0 default, 1 always placed, 2 manual BSP placement.</param>
/// <param name="MultiplayerGames">Scenery only (+0x5A): the multiplayer game engines it appears in, a bit each (0 or
/// 0x7FF: all).</param>
/// <param name="ActiveChangeColors">Blocks with permutation data (+0x38): the change colours the placement sets, a bit each.</param>
/// <param name="ChangeColors">Those colours (+0x3C, four ARGB pixels as 0-1 RGB), or null.</param>
public sealed record ScenarioPlacementData( int Type, int Index, int NameIndex, uint Flags, int ManualBsps, int BspPolicy, int MultiplayerGames,
	int ActiveChangeColors, IReadOnlyList<Vector3> ChangeColors );

/// <summary>
/// A multiplayer item or vehicle spawn: an item collection (<c>itmc</c>) or vehicle collection (<c>vehc</c>)
/// to pick from, where, and in which game types.
/// </summary>
/// <param name="Orientation">Yaw, pitch, roll in radians.</param>
/// <param name="GameTypes">Up to four game type ids (0 none, 1 CTF, 2 slayer, 3 oddball, 4 KOTH, 5 race,
/// 6 headhunter, 7 juggernaut, 8 territories, 12 all, 13 all but CTF, 14 all but CTF and race...).</param>
/// <param name="SpawnTime">Seconds, 0 = the collection's (or the game's default).</param>
public sealed record NetgameEquipment( int Flags, IReadOnlyList<int> GameTypes, int SpawnTime, Vector3 Position, Vector3 Orientation, TagEntry Collection )
{
	/// <summary>The item floats where it's placed instead of dropping.</summary>
	public bool Levitates => (Flags & 1) != 0;

	static readonly TagClass WeaponClass = new( "weap" );

	/// <summary>
	/// The object this spawn holds, as a placement: Halo picks from the collection at random by weight every
	/// time it spawns one; here the random number comes from the spawn's position, so every load places the
	/// same object (as the Halo CE mount does). Null if the spawn has no collection or it holds nothing.
	/// </summary>
	public ObjectPlacement Place()
	{
		if ( !ItemCollectionTag.IsCollection( Collection ) || Collection.Address == 0 )
			return null;

		var entry = new ItemCollectionTag( Collection ).Pick( Blam.Halo1.StableRandom.At( Position ) );
		if ( entry?.Object is null || entry.Object.Address == 0 )
			return null;

		var kind = Collection.Class == ItemCollectionTag.VehicleClass ? "vehicle" : entry.Object.Is( WeaponClass ) ? "weapon" : "equipment";
		return new ObjectPlacement( kind, entry.Object, Position, Orientation, 1, string.IsNullOrEmpty( entry.Variant ) ? null : entry.Variant );
	}
}

/// <summary>
/// A multiplayer marker: flag spawns and returns, teleporters, hills, oddball spawns...
/// </summary>
/// <param name="Type">0 CTF flag spawn, 1 CTF flag return, 2 assault bomb spawn, 3 bomb return, 4 oddball
/// spawn, 6 race checkpoint, 7/8 teleporter source/destination, 9 headhunter bin, 10 territories flag,
/// 11-18 king of the hill 0-7.</param>
/// <param name="Team">8 = neutral.</param>
public sealed record NetgameFlag( Vector3 Position, float Facing, int Type, int Team, int Identifier );

/// <summary>
/// What a Halo 2 scenario places: objects from its palettes, multiplayer item and vehicle spawns, and
/// multiplayer flags. Kept apart from <see cref="ScenarioTag"/>, which holds what building the level needs.
/// </summary>
/// <remarks>
/// Every placement block starts the same way: <c>+0 s16 palette index, +2 s16 name index, +4 u32 placement
/// flags (1 not automatically, 0x40 never placed), +8 position, +0x14 yaw/pitch/roll (radians), +0x20 scale
/// (0 = 1), +0x28 object id (8 bytes)</c>; blocks with permutation data (scenery, bipeds, vehicles, weapons, crates) have the variant
/// name at +0x34. Palette elements are 0x28 bytes with the object reference first.
/// </remarks>
public sealed class ScenarioObjects
{
	/// <summary>
	/// (kind, placement block, element size, palette block, has a variant).
	/// </summary>
	static readonly (string Kind, ObjectType Type, uint Block, int Size, uint Palette, bool Variant)[] Blocks =
	[
		("scenery", ObjectType.Scenery, 0x50, 0x5C, 0x58, true),
		("biped", ObjectType.Biped, 0x60, 0x54, 0x68, true),
		("vehicle", ObjectType.Vehicle, 0x70, 0x54, 0x78, true),
		("equipment", ObjectType.Equipment, 0x80, 0x38, 0x88, false),
		("weapon", ObjectType.Weapon, 0x90, 0x54, 0x98, true),
		("machine", ObjectType.Machine, 0xA8, 0x48, 0xB0, false),
		("control", ObjectType.Control, 0xB8, 0x44, 0xC0, false),
		("light fixture", ObjectType.LightFixture, 0xC8, 0x54, 0xD0, false),
		("sound scenery", ObjectType.SoundScenery, 0xD8, 0x50, 0xE0, false),
		("crate", ObjectType.Crate, 0x328, 0x4C, 0x330, true),
		("creature", ObjectType.Creature, 0x368, 0x34, 0x370, false),
	];

	const int PaletteSize = 0x28;
	const uint NetgameFlagsBlock = 0x118;
	const uint NetgameEquipmentBlock = 0x120;
	const int NetgameFlagSize = 0x20;
	const int NetgameEquipmentSize = 0x90;
	const uint GlobalLightingReference = 0x338;

	public IReadOnlyList<ObjectPlacement> Objects { get; }
	public IReadOnlyList<NetgameEquipment> NetgameEquipment { get; }
	public IReadOnlyList<NetgameFlag> NetgameFlags { get; }

	/// <summary>
	/// The scenario's global lighting (<c>gldf</c>), or null for the globals' default.
	/// </summary>
	public TagEntry GlobalLighting { get; }

	public ScenarioObjects( TagEntry scenario )
	{
		var cache = scenario.Cache;
		var data = cache.TagData;
		var a = scenario.Address;

		var objects = new List<ObjectPlacement>();
		foreach ( var (kind, type, block, size, paletteBlock, hasVariant) in Blocks )
		{
			var palette = data.Elements( a + paletteBlock, PaletteSize ).Select( cache.GetTagReference ).ToList();
			var elements = data.Elements( a + block, size ).ToList();
			for ( var n = 0; n < elements.Count; n++ )
			{
				var e = elements[n];
				var index = data.I16( e );
				var flags = data.U32( e + 4 );
				if ( index < 0 || index >= palette.Count || palette[index] is null || (flags & 0x40) != 0 )
					continue;

				var scale = data.F32( e + 0x20 );
				objects.Add( new ObjectPlacement(
					kind,
					palette[index],
					data.Vec3( e + 8 ),
					data.Vec3( e + 0x14 ),
					scale > 0 ? scale : 1,
					hasVariant ? NullIfEmpty( cache.GetString( data.U32( e + 0x34 ) ) ) : null,
					NotAutomatically: (flags & 1) != 0,
					ObjectId: data.U32( e + 0x28 ) | (ulong)data.U32( e + 0x2C ) << 32,
					Data: new ScenarioPlacementData( (int)type, n, data.I16( e + 2 ), flags, data.U16( e + 0x26 ), (sbyte)data.U8( e + 0x30 ),
						type == ObjectType.Scenery ? data.U16( e + 0x5A ) : 0,
						hasVariant ? data.U8( e + 0x38 ) : 0,
						hasVariant ? Enumerable.Range( 0, 4 ).Select( k => Pixel( data.U32( e + 0x3C + (uint)(k * 4) ) ) ).ToList() : null ) ) );
			}
		}

		Objects = objects;

		NetgameEquipment = data.Elements( a + NetgameEquipmentBlock, NetgameEquipmentSize ).Select( e => new NetgameEquipment(
			Flags: data.I32( e ),
			GameTypes: Enumerable.Range( 0, 4 ).Select( i => (int)data.I16( e + 4 + (uint)(i * 2) ) ).Where( t => t != 0 ).ToList(),
			SpawnTime: data.I16( e + 0xE ),
			Position: data.Vec3( e + 0x40 ),
			Orientation: data.Vec3( e + 0x4C ),
			Collection: cache.GetTagReference( e + 0x58 ) ) ).ToList();

		NetgameFlags = data.Elements( a + NetgameFlagsBlock, NetgameFlagSize ).Select( e => new NetgameFlag(
			data.Vec3( e ), data.F32( e + 0xC ), data.I16( e + 0x10 ), data.I16( e + 0x12 ), data.I16( e + 0x14 ) ) ).ToList();

		GlobalLighting = cache.GetTagReference( a + GlobalLightingReference );
	}

	static string NullIfEmpty( string s ) => string.IsNullOrEmpty( s ) ? null : s;

	// An ARGB pixel's colour, 0-1, as Halo 2 reads it.
	static Vector3 Pixel( uint argb ) => new Vector3( (argb >> 16) & 0xFF, (argb >> 8) & 0xFF, argb & 0xFF ) * (1 / 255f);
}
