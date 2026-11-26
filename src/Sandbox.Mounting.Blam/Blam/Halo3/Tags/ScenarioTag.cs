using System;
using System.Collections.Generic;
using System.Linq;

namespace Blam.Halo3.Tags;

// Inside the namespace so these win over s&box's global vector types when this compiles into the mount.
using Quaternion = System.Numerics.Quaternion;
using Vector3 = System.Numerics.Vector3;
using Vector4 = System.Numerics.Vector4;

// The scenario's placement blocks, in scnr order.
public enum PlacementKind
{
	Scenery,
	Biped,
	Vehicle,
	Equipment,
	Weapon,
	Machine,
	Terminal,
	Control,
	SoundScenery,
	Giant,
	EffectScenery,
	LightVolume,
	Crate,
}

// Where a placement block and its palette are in the scnr: element size, whether elements carry a variant (+0x54), the offset of their multiplayer properties (-1 none), the object type (null for light volumes, whose palette is light tags).
public sealed record PlacementBlock( PlacementKind Kind, string Name, int Block, int Size, int Palette, bool HasVariant, int Multiplayer, ObjectType? Type, string Group );

// A placement's multiplayer properties (0x34). Boundary is width or radius, box length, positive height, negative height.
public sealed record PlacementMultiplayer( int Symmetry, GameEngines GameEngines, int Team, int SpawnOrder, int QuotaMin, int QuotaMax, int SpawnFlags, short SpawnTime, short AbandonTime,
	int BoundaryShape, int TeleporterChannel, Vector4 Boundary, float RespawnWeight )
{
	public const int NeutralTeam = 8;
}

// An object placed in the scenario. ChangeColors are the placement's four colours (RGB 0..1, used where ActiveChangeColors has their bit), null for blocks without them; DevicePosition is where a device starts (0 closed, 1 open, as its position animation shows it: reversed devices already turned round), -1 for other objects.
// Angles are yaw, pitch, roll in radians; ObjectId is the u32 unique id, i16 origin BSP, u8 object type, u8 source (little endian); BspMask is the BSPs it's placed with.
// Parent is the object name index of what it's attached to (-1 none), at the parent's ParentMarker with its own ConnectionMarker there; AttachedRotation is its world rotation once attached (its Position then too), and Sky the scenario sky its chain of parents ends on (-1 none).
public sealed record ScenarioPlacement( PlacementKind Kind, int Index, TagEntry Object, string Name, uint Flags, Vector3 Position, Vector3 Angles, float Scale, ulong ObjectId,
	int BspPolicy, ushort ManualBsps, ushort BspMask, string Variant, uint ActiveChangeColors, PlacementMultiplayer Multiplayer,
	Vector3[] ChangeColors = null, float DevicePosition = -1 )
{
	public int NameIndex { get; init; } = -1;
	public int Parent { get; init; } = -1;
	public string ParentMarker { get; init; }
	public string ConnectionMarker { get; init; }
	public Quaternion? AttachedRotation { get; init; }
	public int Sky { get; init; } = -1;

	public bool IsAttached => AttachedRotation is not null;

	// Attached to a parent the scenario doesn't place (Standoff's lights of a deleted dish_02): Halo has nothing to hang it on.
	public bool Orphaned => Parent >= 0 && AttachedRotation is null;

	// Whether it's a device (machine, control, terminal), whose DevicePosition is set.
	public bool IsDevice => DevicePosition >= 0;

	public uint UniqueId => (uint)ObjectId;
	public int OriginBsp => (short)(ObjectId >> 32);
	public int IdType => (byte)(ObjectId >> 48);

	// 0 structure, 1 editor, 2 dynamic, 3 legacy, 4 sky, 5 parent.
	public int Source => (byte)(ObjectId >> 56);

	// Only scripts create it.
	public bool NotAutomatically => (Flags & 1) != 0;
	public bool NeverPlaced => (Flags & 8) != 0;

	// Whether it's placed with a BSP; objects without an attach mask go with every BSP.
	public bool InBsp( int bsp ) => BspMask == 0 || (BspMask >> bsp & 1) != 0;

	// About the world's Z by the yaw, then its Y by the pitch (positive raises the nose), then its X by the roll: Halo 2's rule (assumed for Halo 3, see scenario.md §10).
	public Vector3 Forward
	{
		get
		{
			if ( AttachedRotation is { } attached )
				return Vector3.Transform( Vector3.UnitX, attached );
			var (sy, cy) = MathF.SinCos( Angles.X );
			var (sp, cp) = MathF.SinCos( Angles.Y );
			var (sr, cr) = MathF.SinCos( Angles.Z );
			return new Vector3( cy * cp, sy * cr - cy * sp * sr, cy * sp * cr + sy * sr );
		}
	}

	public Vector3 Up
	{
		get
		{
			if ( AttachedRotation is { } attached )
				return Vector3.Transform( Vector3.UnitZ, attached );
			var (sp, cp) = MathF.SinCos( Angles.Y );
			var (sr, cr) = MathF.SinCos( Angles.Z );
			return new Vector3( -sp, -cp * sr, cp * cr );
		}
	}

	public Vector3 Left => Vector3.Cross( Up, Forward );

	public Quaternion Orientation
	{
		get
		{
			if ( AttachedRotation is { } attached )
				return attached;
			var f = Forward;
			var l = Left;
			var u = Up;
			var basis = new System.Numerics.Matrix4x4( f.X, f.Y, f.Z, 0, l.X, l.Y, l.Z, 0, u.X, u.Y, u.Z, 0, 0, 0, 0, 1 );
			return Quaternion.Normalize( Quaternion.CreateFromRotationMatrix( basis ) );
		}
	}

	// A point in object space (world units) placed in the world.
	public Vector3 Transform( Vector3 local ) => Position + Scale * (local.X * Forward + local.Y * Left + local.Z * Up);

	// The same number in [0, 1) every time for this placement, for the permutations Halo picks at random.
	public float Random( int salt ) => Blam.Halo1.StableRandom.At( Position, 0x4833 + salt );

	public override string ToString() => $"{Kind} {Index} {Object}";
}

// A sky: a scenery object around the level, shown with the BSPs in its mask.
public sealed record ScenarioSky( TagEntry Object, int NameIndex, ushort BspMask )
{
	public bool InBsp( int bsp ) => BspMask == 0 || (BspMask >> bsp & 1) != 0;
}

// BSPs loaded together. Multiplayer maps have one, holding their one BSP.
public sealed record ZoneSet( string Name, int PvsIndex, uint Flags, uint BspMask, uint RequiredDesignerZones, uint ForbiddenDesignerZones, uint CinematicZones );

public sealed record PlayerStartingLocation( Vector3 Position, float Facing, float Pitch, int Bsp, int CampaignPlayerType );

// An entry of a Forge (map variant) palette: what may be placed, how many and at what price.
public sealed record MapVariantPaletteEntry( TagEntry Object, string DisplayName, int MaximumAllowed, float Price );

// Rotation is a unit quaternion (x, y, z, w); dead entries (zero quaternion, scale 0) are left out.
public sealed record ScenarioDecal( TagEntry Decal, int Bsp, Quaternion Rotation, Vector3 Position, float Scale );

// The parts of a Halo 3 scenario (scnr) that place things: placements and their palettes, skies, zone sets, player starts, multiplayer palettes.
public sealed class ScenarioTag
{
	public static readonly IReadOnlyList<PlacementBlock> Blocks =
	[
		new( PlacementKind.Scenery, "scenery", 0x0B4, 0xB4, 0x0C0, true, 0x80, ObjectType.Scenery, "scen" ),
		new( PlacementKind.Biped, "biped", 0x0CC, 0x74, 0x0D8, true, -1, ObjectType.Biped, "bipd" ),
		new( PlacementKind.Vehicle, "vehicle", 0x0E4, 0xA8, 0x0F0, true, 0x74, ObjectType.Vehicle, "vehi" ),
		new( PlacementKind.Equipment, "equipment", 0x0FC, 0x8C, 0x108, false, 0x58, ObjectType.Equipment, "eqip" ),
		new( PlacementKind.Weapon, "weapon", 0x114, 0xA8, 0x120, true, 0x74, ObjectType.Weapon, "weap" ),
		new( PlacementKind.Machine, "machine", 0x138, 0x88, 0x144, true, -1, ObjectType.Machine, "mach" ),
		new( PlacementKind.Terminal, "terminal", 0x150, 0x78, 0x15C, true, -1, ObjectType.Terminal, "term" ),
		new( PlacementKind.Control, "control", 0x168, 0x78, 0x174, true, -1, ObjectType.Control, "ctrl" ),
		new( PlacementKind.SoundScenery, "sound scenery", 0x180, 0x70, 0x18C, false, -1, ObjectType.SoundScenery, "ssce" ),
		new( PlacementKind.Giant, "giant", 0x198, 0x84, 0x1A4, true, -1, ObjectType.Giant, "gint" ),
		new( PlacementKind.EffectScenery, "effect scenery", 0x1B0, 0x54, 0x1BC, false, -1, ObjectType.EffectScenery, "efsc" ),
		new( PlacementKind.LightVolume, "light volume", 0x1C8, 0x8C, 0x1D4, false, -1, null, "ligh" ),
		new( PlacementKind.Crate, "crate", 0x5BC, 0xB0, 0x5C8, true, 0x7C, ObjectType.Crate, "bloc" ),
	];

	public static readonly IReadOnlyList<(string Name, int Block)> MapVariantPaletteBlocks =
		[("vehicle", 0x1E0), ("weapon", 0x1EC), ("equipment", 0x1F8), ("scenery", 0x204), ("teleporter", 0x210), ("goal", 0x21C), ("spawner", 0x228)];

	public TagEntry Tag { get; }

	// 0 solo, 1 multiplayer.
	public int Type { get; }
	public int CampaignId { get; }
	public int MapId { get; }
	public float SandboxBudget { get; }

	public int StructureBspCount { get; }

	// Per structure BSP (scnr BSP element +0x42), -1 for none: then the skies' BSP masks decide.
	public IReadOnlyList<int> StructureBspDefaultSkies { get; }
	public IReadOnlyList<ScenarioSky> Skies { get; }
	public IReadOnlyList<ZoneSet> ZoneSets { get; }
	public IReadOnlyList<string> ObjectNames { get; }

	// Device groups' initial values (scnr +0x12C, 0x28: name, f32 initial value +0x20).
	public IReadOnlyList<float> DeviceGroupValues { get; }

	// Per kind, the palette's tags (null for empty entries).
	public IReadOnlyDictionary<PlacementKind, IReadOnlyList<TagEntry>> Palettes { get; }

	// Every element of every placement block, in block order, empty ones (palette index -1) included.
	public IReadOnlyList<ScenarioPlacement> Placements { get; }

	public IReadOnlyList<PlayerStartingLocation> PlayerStartingLocations { get; }
	public IReadOnlyDictionary<string, IReadOnlyList<MapVariantPaletteEntry>> MapVariantPalettes { get; }
	public IReadOnlyList<ScenarioDecal> Decals { get; }

	public TagEntry SkyParameters { get; }
	public TagEntry GlobalLighting { get; }
	public TagEntry Lightmaps { get; }

	public bool IsMultiplayer => Type == 1;

	public ScenarioTag( TagEntry tag )
	{
		Tag = tag;
		var data = tag.Data;
		Type = data.I16( 0 );
		CampaignId = data.I32( 8 );
		MapId = data.I32( 0xC );
		SandboxBudget = data.F32( 0x14 );

		StructureBspCount = data.Count( 0x18 );
		StructureBspDefaultSkies = data.Block( 0x18, 0x6C ).Select( e => (int)e.I16( 0x42 ) ).ToList();
		Skies = data.Block( 0x34, 0x14 ).Select( e => new ScenarioSky( e.Reference( 0 ), e.I16( 0x10 ), e.U16( 0x12 ) ) ).ToList();
		ZoneSets = data.Block( 0x58, 0x28 ).Select( e => new ZoneSet( e.StringId( 0 ), e.I32( 4 ), e.U32( 8 ), e.U32( 0xC ), e.U32( 0x10 ), e.U32( 0x14 ), e.U32( 0x18 ) ) ).ToList();
		ObjectNames = data.Block( 0xA8, 0x24 ).Select( e => CString( e.Bytes( 0, 32 ) ) ).ToList();

		DeviceGroupValues = data.Block( 0x12C, 0x28 ).Select( e => e.F32( 0x20 ) ).ToList();

		var palettes = new Dictionary<PlacementKind, IReadOnlyList<TagEntry>>();
		var placements = new List<ScenarioPlacement>();
		foreach ( var block in Blocks )
		{
			var palette = data.Block( block.Palette, 0x10 ).Select( e => e.Reference( 0 ) ).ToList();
			palettes[block.Kind] = palette;

			var elements = data.Block( block.Block, block.Size );
			for ( var i = 0; i < elements.Count; i++ )
				placements.Add( ReadPlacement( block, i, elements[i], palette ) );
		}

		Palettes = palettes;
		Placements = Attach( placements );

		PlayerStartingLocations = data.Block( 0x24C, 0x18 )
			.Select( e => new PlayerStartingLocation( e.Vec3( 0 ), e.F32( 0xC ), e.F32( 0x10 ), e.I16( 0x14 ), e.U16( 0x16 ) ) )
			.ToList();

		MapVariantPalettes = MapVariantPaletteBlocks.ToDictionary( b => b.Name, b => (IReadOnlyList<MapVariantPaletteEntry>)data.Block( b.Block, 0x1C )
			.Select( e => new MapVariantPaletteEntry( e.Reference( 0 ), e.StringId( 0x10 ), e.I32( 0x14 ), e.F32( 0x18 ) ) ).ToList() );

		var decalPalette = data.Block( 0x354, 0x10 ).Select( e => e.Reference( 0 ) ).ToList();
		Decals = data.Block( 0x348, 0x24 )
			.Where( e => e.F32( 0x20 ) != 0 || e.Vec4( 4 ) != Vector4.Zero )
			.Select( e => new ScenarioDecal( At( decalPalette, e.I16( 0 ) ), e.I8( 2 ), new Quaternion( e.F32( 4 ), e.F32( 8 ), e.F32( 0xC ), e.F32( 0x10 ) ), e.Vec3( 0x14 ), e.F32( 0x20 ) ) )
			.ToList();

		SkyParameters = data.Reference( 0x680 );
		GlobalLighting = data.Reference( 0x690 );
		Lightmaps = data.Reference( 0x6AC );
	}

	ScenarioPlacement ReadPlacement( PlacementBlock block, int index, TagView e, IReadOnlyList<TagEntry> palette )
	{
		var nameIndex = e.I16( 2 );
		var scale = e.F32( 0x20 );
		PlacementMultiplayer multiplayer = null;
		if ( block.Multiplayer >= 0 )
		{
			var m = e.At( block.Multiplayer );
			multiplayer = new PlacementMultiplayer( (int)m.U32( 0 ), (GameEngines)m.U16( 4 ), m.U16( 6 ), m.I8( 8 ), m.I8( 9 ), m.I8( 0xA ), m.U8( 0xB ), m.I16( 0xC ), m.I16( 0xE ),
				m.U8( 0x11 ), m.I8( 0x12 ), m.Vec4( 0x20 ), m.F32( 0x30 ) );
		}

		var variant = block.HasVariant ? e.StringId( 0x54 ) : null;

		// Colours: color32 x4 at +0x5C (b, g, r, a bytes).
		Vector3[] colors = null;
		if ( block.HasVariant )
		{
			colors = new Vector3[4];
			for ( var k = 0; k < 4; k++ )
				colors[k] = new Vector3( e.U8( 0x5C + 4 * k + 2 ), e.U8( 0x5C + 4 * k + 1 ), e.U8( 0x5C + 4 * k ) ) / 255f;
		}

		// Device data (+0x6C): i16 power group, i16 position group, u32 flags (1 initially open, 2 initially off, 8 position reversed); a position group's initial value wins over the flag.
		var devicePosition = -1f;
		if ( block.Kind is PlacementKind.Machine or PlacementKind.Control or PlacementKind.Terminal )
		{
			var flags = e.U32( 0x70 );
			var group = e.I16( 0x6E );
			devicePosition = group >= 0 && group < DeviceGroupValues.Count ? DeviceGroupValues[group] : (flags & 1) != 0 ? 1 : 0;
			devicePosition = Math.Clamp( devicePosition, 0, 1 );
			if ( (flags & 8) != 0 )
				devicePosition = 1 - devicePosition;
		}
		return new ScenarioPlacement(
			block.Kind, index, At( palette, e.I16( 0 ) ),
			nameIndex >= 0 && nameIndex < ObjectNames.Count ? ObjectNames[nameIndex] : null,
			e.U32( 4 ), e.Vec3( 8 ), e.Vec3( 0x14 ), scale > 0 ? scale : 1,
			(ulong)e.U32( 0x38 ) | (ulong)e.U32( 0x3C ) << 32,
			e.U8( 0x40 ), e.U16( 0x32 ), e.U16( 0x50 ),
			string.IsNullOrEmpty( variant ) ? null : variant,
			block.HasVariant ? e.U32( 0x58 ) : 0,
			multiplayer, colors, devicePosition )
		{
			NameIndex = nameIndex,
			Parent = e.I16( 0x46 ),
			ParentMarker = NullIfEmpty( e.StringId( 0x48 ) ),
			ConnectionMarker = NullIfEmpty( e.StringId( 0x4C ) ),
		};
	}

	static string NullIfEmpty( string s ) => string.IsNullOrEmpty( s ) ? null : s;

	// Objects attached to a parent (object datum +0x46 parent name, +0x48 its marker, +0x4C the child's connection marker) go where Halo attaches them: the child's connection marker (else its origin) on the parent's marker (else its origin), both models in their default pose.
	// The position the placement stores is stale (Standoff's dishes, hung on the sky's markers hundreds of world units out, all store one spot in the middle of the map). Skies are at the origin.
	List<ScenarioPlacement> Attach( List<ScenarioPlacement> placements )
	{
		if ( !placements.Any( p => p.Parent >= 0 ) )
			return placements;

		var byName = new Dictionary<int, int>();
		for ( var i = 0; i < placements.Count; i++ )
		{
			if ( placements[i].NameIndex >= 0 && placements[i].Object is not null )
				byName.TryAdd( placements[i].NameIndex, i );
		}

		var result = placements.ToArray();
		var state = new byte[result.Length]; // 0 unresolved, 1 resolving, 2 done

		ObjectFrame FrameOf( int i )
		{
			if ( state[i] == 0 )
			{
				state[i] = 1;
				result[i] = Resolve( result[i] );
				state[i] = 2;
			}
			var p = result[i];
			return new ObjectFrame( p.Position, p.Orientation, p.Scale );
		}

		ScenarioPlacement Resolve( ScenarioPlacement p )
		{
			if ( p.Parent < 0 || p.Object is null )
				return p;

			ObjectFrame parentFrame;
			TagEntry parentObject;
			int sky;
			var skyIndex = -1;
			for ( var k = 0; k < Skies.Count; k++ )
			{
				if ( Skies[k].NameIndex == p.Parent && Skies[k].Object is not null )
				{
					skyIndex = k;
					break;
				}
			}

			if ( skyIndex >= 0 )
			{
				parentFrame = new ObjectFrame( Vector3.Zero, Quaternion.Identity, 1 );
				parentObject = Skies[skyIndex].Object;
				sky = skyIndex;
			}
			else if ( byName.TryGetValue( p.Parent, out var parentIndex ) && state[parentIndex] != 1 )
			{
				parentFrame = FrameOf( parentIndex );
				parentObject = result[parentIndex].Object;
				sky = result[parentIndex].Sky;
			}
			else
				return p;

			var frame = parentFrame;
			if ( AttachedObjects.FindMarker( AttachedObjects.RenderModelOf( parentObject ), p.ParentMarker ) is { } marker )
				frame = frame.Then( marker.Rotation, marker.Translation );
			frame = new ObjectFrame( frame.Position, frame.Rotation, p.Scale );
			if ( AttachedObjects.FindMarker( AttachedObjects.RenderModelOf( p.Object ), p.ConnectionMarker ) is { } connection )
			{
				var inverse = Quaternion.Conjugate( connection.Rotation );
				frame = frame.Then( inverse, -Vector3.Transform( connection.Translation, inverse ) );
			}

			return p with { Position = frame.Position, AttachedRotation = frame.Rotation, Sky = sky };
		}

		for ( var i = 0; i < result.Length; i++ )
			FrameOf( i );
		return result.ToList();
	}

	static TagEntry At( IReadOnlyList<TagEntry> palette, int index ) => index >= 0 && index < palette.Count ? palette[index] : null;

	static string CString( ReadOnlySpan<byte> bytes )
	{
		var end = bytes.IndexOf( (byte)0 );
		return System.Text.Encoding.Latin1.GetString( end < 0 ? bytes : bytes[..end] );
	}

	// The placements of a kind.
	public IEnumerable<ScenarioPlacement> PlacementsOf( PlacementKind kind ) => Placements.Where( p => p.Kind == kind );

	public override string ToString() => Tag.ToString();
}
