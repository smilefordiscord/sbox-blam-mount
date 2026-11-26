using System;
using System.Collections.Generic;
using System.Linq;

namespace Blam.Halo3.Tags;

// Inside the namespace so these win over s&box's global vector types when this compiles into the mount.
using Vector3 = System.Numerics.Vector3;
using Vector4 = System.Numerics.Vector4;

// Halo 3's object types (object tag +0, placement object id +0x3E); not Halo 2's numbering.
public enum ObjectType : short
{
	Biped = 0,
	Vehicle = 1,
	Weapon = 2,
	Equipment = 3,
	Terminal = 4,
	Projectile = 5,
	Scenery = 6,
	Machine = 7,
	Control = 8,
	SoundScenery = 9,
	Crate = 10,
	Creature = 11,
	Giant = 12,
	EffectScenery = 13,
}

// What an object is to multiplayer (object multiplayer block +2): spawns, goals and teleporters are ordinary placements of objects of these types.
public enum MultiplayerObjectType : byte
{
	Ordinary = 0,
	Weapon = 1,
	Grenade = 2,
	Projectile = 3,
	Powerup = 4,
	Equipment = 5,
	LightLandVehicle = 6,
	HeavyLandVehicle = 7,
	FlyingVehicle = 8,
	Teleporter2Way = 9,
	TeleporterSender = 10,
	TeleporterReceiver = 11,
	PlayerSpawnLocation = 12,
	PlayerRespawnZone = 13,
	OddballSpawnLocation = 14,
	CtfFlagSpawnLocation = 15,
	TargetSpawnLocation = 16,
	CtfFlagReturnArea = 17,
	KothHillArea = 18,
	InfectionSafeArea = 19,
	TerritoryArea = 20,
	VipInfluenceArea = 21,
	VipDestinationZone = 22,
	JuggernautDestinationZone = 23,
}

// Game engine bits of an object's (and a placement's) multiplayer properties.
[Flags]
public enum GameEngines : ushort
{
	None = 0,
	Ctf = 1 << 0,
	Slayer = 1 << 1,
	Oddball = 1 << 2,
	Koth = 1 << 3,
	Juggernaut = 1 << 4,
	Territories = 1 << 5,
	Assault = 1 << 6,
	Vip = 1 << 7,
	Infection = 1 << 8,
	TargetTraining = 1 << 9,
}

// An object tag's multiplayer block (0xC4). Boundary is width or radius, box length, positive height, negative height.
public sealed record MultiplayerObject( GameEngines GameEngines, MultiplayerObjectType Type, int TeleporterPassability, ushort Flags, int BoundaryShape, int SpawnTimerType,
	short DefaultSpawnTime, short DefaultAbandonTime, Vector4 Boundary, TagEntry SpawnedObject )
{
	public bool EditorOnly => (Flags & 1) != 0;
	public bool ValidInitialSpawn => (Flags & 2) != 0;

	public bool IsTeleporter => Type is MultiplayerObjectType.Teleporter2Way or MultiplayerObjectType.TeleporterSender or MultiplayerObjectType.TeleporterReceiver;
	public bool IsPlayerSpawn => Type == MultiplayerObjectType.PlayerSpawnLocation;
}

// The fields every Halo 3 object tag (obje and its children: scen bipd vehi weap eqip mach ctrl term bloc ssce efsc gint crea) starts with, as far as placing it needs them.
public sealed class ObjectTag
{
	public static readonly TagClass Class = new( "obje" );

	public TagEntry Tag { get; }
	public ObjectType Type { get; }
	public ushort Flags { get; }
	public float BoundingRadius { get; }
	public Vector3 BoundingOffset { get; }

	// May be null or empty.
	public string DefaultVariant { get; }

	// The hlmt, or null (sound scenery, effect scenery).
	public TagEntry Model { get; }

	public MultiplayerObject Multiplayer { get; }

	// Change colours (obje +0xD4, 0x18): per colour its initial permutations (0x20: weight, lower and upper colour, variant name, empty for any).
	public IReadOnlyList<IReadOnlyList<ChangeColorPermutation>> ChangeColors { get; }

	public static bool IsObject( TagEntry tag ) => tag is not null && tag.Address != 0 && tag.Is( Class );

	public ObjectTag( TagEntry tag )
	{
		Tag = tag;
		var data = tag.Data;
		Type = (ObjectType)data.I16( 0 );
		Flags = data.U16( 2 );
		BoundingRadius = data.F32( 4 );
		BoundingOffset = data.Vec3( 8 );
		DefaultVariant = data.StringId( 0x30 );
		Model = data.Reference( 0x34 ) is { } model && model.Class == ObjectModel.Class ? model : null;

		ChangeColors = data.Block( 0xD4, 0x18 )
			.Select( c => (IReadOnlyList<ChangeColorPermutation>)c.Block( 0, 0x20 ).Select( p => new ChangeColorPermutation( p.F32( 0 ), p.Vec3( 4 ), p.Vec3( 0x10 ), p.StringId( 0x1C ) ) ).ToList() )
			.ToList();

		var multiplayer = data.Block( 0xE0, 0xC4 );
		if ( multiplayer.Count > 0 )
		{
			var e = multiplayer[0];
			Multiplayer = new MultiplayerObject( (GameEngines)e.U16( 0 ), (MultiplayerObjectType)e.U8( 2 ), e.U8( 3 ), e.U16( 4 ), e.U8( 6 ), e.U8( 7 ),
				e.I16( 8 ), e.I16( 0xA ), e.Vec4( 0xC ), e.Reference( 0x30 ) );
		}
	}

	// An object's four change colours when created at position showing variant (null: only permutations for any variant count), as Halo 2's ObjectTag.InitialChangeColors (MCC's pick and HSV blend, assumed unchanged in Halo 3); colours a placement sets (bits of placedMask) win.
	// Null where neither the placement nor an initial permutation for the variant gives one: the material keeps its own. Colours are as stored (gamma, 0..1).
	public Vector3?[] InitialChangeColors( string variant, Vector3 position, uint placedMask = 0, IReadOnlyList<Vector3> placed = null )
	{
		var colors = new Vector3?[4];
		for ( var i = 0; i < colors.Length; i++ )
		{
			if ( (placedMask & (1u << i)) != 0 && placed is not null && i < placed.Count )
			{
				colors[i] = placed[i];
				continue;
			}

			if ( i >= ChangeColors.Count )
				continue;

			var pick = Fraction( position.X * 315.89312744140625f + position.Y * 587.1294555664062f + position.Z * 744.1241455078125f + i * 431.1289367675781f );
			var t = Fraction( i * 0.7121099829673767f + position.Y );
			var candidates = ChangeColors[i].Where( p => string.IsNullOrEmpty( p.Variant ) || string.Equals( p.Variant, variant, StringComparison.OrdinalIgnoreCase ) ).ToList();
			var total = candidates.Sum( p => p.Weight );
			if ( candidates.Count == 0 || total <= 0 )
				continue;

			var target = pick * total;
			var sum = 0f;
			foreach ( var p in candidates )
			{
				sum += p.Weight;
				if ( p == candidates[^1] || sum >= target )
				{
					colors[i] = Vector3.Clamp( Blam.Halo2.Tags.ObjectTag.LerpHsv( p.Lower, p.Upper, t ), Vector3.Zero, Vector3.One );
					break;
				}
			}
		}

		return colors;
	}

	static float Fraction( float v ) => MathF.Abs( v ) % 1f;

	public override string ToString() => Tag.ToString();
}

public sealed record ChangeColorPermutation( float Weight, Vector3 Lower, Vector3 Upper, string Variant );

// A model tag (hlmt): its render, collision, animation and physics models and its variants, whose region and permutation indices into the render model are stored precomputed.
public sealed class ObjectModel
{
	public static readonly TagClass Class = new( "hlmt" );

	public TagEntry Tag { get; }
	public TagEntry RenderModel { get; }
	public TagEntry CollisionModel { get; }
	public TagEntry Animation { get; }
	public TagEntry PhysicsModel { get; }
	public TagEntry LodRenderModel { get; }
	public IReadOnlyList<Variant> Variants { get; }

	// RenderModelPermutation -1: the render model has no permutation of that name, so the region shows nothing.
	public sealed record Permutation( string Name, int RenderModelPermutation, int Flags, float Probability );

	// RenderModelRegion -1: the render model has no region of that name.
	public sealed record Region( string Name, int RenderModelRegion, int Flags, int ParentVariant, IReadOnlyList<Permutation> Permutations );

	// An object the variant attaches at a marker (a Warthog's turret).
	public sealed record Attachment( string ParentMarker, string ChildMarker, string ChildVariant, TagEntry Object );

	public sealed record Variant( string Name, IReadOnlyList<Region> Regions, IReadOnlyList<Attachment> Objects, int InstanceGroup );

	public ObjectModel( TagEntry tag )
	{
		Tag = tag;
		var data = tag.Data;
		RenderModel = data.Reference( 0x00 );
		CollisionModel = data.Reference( 0x10 );
		Animation = data.Reference( 0x20 );
		PhysicsModel = data.Reference( 0x30 );
		LodRenderModel = data.Reference( 0x54 );

		Variants = data.Block( 0x64, 0x30 ).Select( v => new Variant(
			v.StringId( 0 ),
			v.Block( 0x14, 0x14 ).Select( r => new Region( r.StringId( 0 ), r.I8( 4 ), r.U8( 5 ), r.I16( 6 ),
				r.Block( 8, 0x18 ).Select( p => new Permutation( p.StringId( 0 ), p.I8( 4 ), p.U8( 5 ), p.F32( 8 ) ) ).ToList() ) ).ToList(),
			v.Block( 0x20, 0x1C ).Select( o => new Attachment( o.StringId( 0 ), o.StringId( 4 ), o.StringId( 8 ), o.Reference( 0xC ) ) ).ToList(),
			v.I32( 0x2C ) ) ).ToList();
	}

	// The variant by name; the first one when the name is empty or stale (020_base shelves ask for variants their model doesn't have), null for a model without variants.
	public Variant FindVariant( string name )
	{
		if ( !string.IsNullOrEmpty( name ) )
		{
			foreach ( var variant in Variants )
			{
				if ( string.Equals( variant.Name, name, StringComparison.OrdinalIgnoreCase ) )
					return variant;
			}
		}

		return Variants.Count > 0 ? Variants[0] : null;
	}

	// The render model permutation each listed region of the variant shows, picked by probability with random (a number in [0, 1) per region index); -1 shows nothing. Regions the variant doesn't list keep their first permutation.
	public static IReadOnlyList<(string Region, int RenderModelRegion, int RenderModelPermutation)> Choose( Variant variant, Func<int, float> random )
	{
		var result = new List<(string, int, int)>();
		if ( variant is null )
			return result;

		foreach ( var region in variant.Regions )
		{
			if ( region.RenderModelRegion < 0 || region.Permutations.Count == 0 )
				continue;

			var pick = region.Permutations[0];
			var total = region.Permutations.Sum( p => Math.Max( p.Probability, 0 ) );
			if ( total > 0 )
			{
				var remaining = random( region.RenderModelRegion ) * total;
				foreach ( var permutation in region.Permutations )
				{
					if ( permutation.Probability <= 0 )
						continue;

					pick = permutation;
					remaining -= permutation.Probability;
					if ( remaining < 0 )
						break;
				}
			}

			result.Add( (region.Name, region.RenderModelRegion, pick.RenderModelPermutation) );
		}

		return result;
	}

	public override string ToString() => Tag.ToString();
}
