using System.Collections.Generic;
using System.Linq;

namespace Blam.Halo2.Tags;

// Inside the namespace so these win over s&box's global vector types when this compiles into the mount.
using Vector3 = System.Numerics.Vector3;

/// <summary>
/// One of a scenario's structure BSPs and its lightmap.
/// </summary>
public sealed record ScenarioBsp( int Index, TagEntry Bsp, TagEntry Lightmap );

/// <summary>
/// A player starting location. Facing is in radians; team is 0 on multiplayer maps.
/// </summary>
public sealed record PlayerStartingLocation( Vector3 Position, float Facing, int Team, int BspIndex );

/// <summary>
/// A decal placed in the scenario. Yaw and pitch are Halo's signed bytes ([-127, 127]); the decal is null where the
/// palette entry has none.
/// </summary>
public sealed record ScenarioDecal( int Index, TagEntry Decal, int Yaw, int Pitch, Vector3 Position );

/// <summary>
/// A Halo 2 scenario (<c>scnr</c>), as far as building a level needs it so far.
/// </summary>
public sealed class ScenarioTag
{
	public static readonly TagClass Class = new( "scnr" );

	const uint SkiesBlock = 0x08;
	const uint PlayerStartingLocationsBlock = 0x100;
	const uint StructureBspsBlock = 0x210;
	const uint DecalsBlock = 0x138;
	const uint DecalPaletteBlock = 0x140;
	const uint PlanarFogPaletteBlock = 0x348;
	const uint DecoratorPaletteBlock = 0x378;
	const int DecalSize = 0x10;
	const int SkySize = 8;
	const int PlanarFogPaletteSize = 0x10;
	const int PlayerStartingLocationSize = 0x34;
	const int StructureBspSize = 0x44;

	public TagEntry Tag { get; }
	public IReadOnlyList<ScenarioBsp> StructureBsps { get; }
	public IReadOnlyList<TagEntry> Skies { get; }
	public IReadOnlyList<PlayerStartingLocation> PlayerStartingLocations { get; }

	/// <summary>
	/// The planar fogs (<see cref="FogTag"/>; null where an entry has none), which structure BSPs' fog planes index.
	/// </summary>
	public IReadOnlyList<TagEntry> PlanarFogPalette { get; }

	/// <summary>
	/// The decals placed in the level (signs, markings, stains), whichever structure BSP they're on.
	/// </summary>
	public IReadOnlyList<ScenarioDecal> Decals { get; }

	/// <summary>
	/// The decals (<see cref="DecalTag"/>) the decals place, which structure BSPs' runtime decals index (null where an
	/// entry has none).
	/// </summary>
	public IReadOnlyList<TagEntry> DecalPalette { get; }

	/// <summary>
	/// The decorator sets (<see cref="DecoratorSetTag"/>) structure BSPs' decorators index (null where an entry has none).
	/// </summary>
	public IReadOnlyList<TagEntry> DecoratorPalette { get; }

	public ScenarioTag( TagEntry tag )
	{
		Tag = tag;
		var cache = tag.Cache;
		var data = cache.TagData;

		var index = 0;
		StructureBsps = data.Elements( tag.Address + StructureBspsBlock, StructureBspSize )
			.Select( e => new ScenarioBsp( index++, cache.GetTagReference( e + 0x10 ), cache.GetTagReference( e + 0x18 ) ) )
			.ToList();

		Skies = data.Elements( tag.Address + SkiesBlock, SkySize ).Select( cache.GetTagReference ).Where( t => t is not null ).ToList();

		PlayerStartingLocations = data.Elements( tag.Address + PlayerStartingLocationsBlock, PlayerStartingLocationSize )
			.Select( e => new PlayerStartingLocation( data.Vec3( e ), data.F32( e + 0xC ), data.I16( e + 0x10 ), data.I16( e + 0x12 ) ) )
			.ToList();

		// Name 0x0, fog reference 0x4.
		PlanarFogPalette = data.Elements( tag.Address + PlanarFogPaletteBlock, PlanarFogPaletteSize )
			.Select( e => cache.GetTagReference( e + 4 ) )
			.ToList();

		// Palette index 0x0, yaw 0x2, pitch 0x3, position 0x4; the palette is plain tag references.
		var decalPalette = data.Elements( tag.Address + DecalPaletteBlock, 8 ).Select( cache.GetTagReference ).ToList();
		DecalPalette = decalPalette;
		DecoratorPalette = data.Elements( tag.Address + DecoratorPaletteBlock, 8 ).Select( cache.GetTagReference ).ToList();
		var decalIndex = 0;
		Decals = data.Elements( tag.Address + DecalsBlock, DecalSize )
			.Select( e =>
			{
				var palette = data.I16( e );
				var decal = palette >= 0 && palette < decalPalette.Count ? decalPalette[palette] : null;
				return new ScenarioDecal( decalIndex++, decal, (sbyte)data.U8( e + 2 ), (sbyte)data.U8( e + 3 ), data.Vec3( e + 4 ) );
			} )
			.ToList();
	}
}
