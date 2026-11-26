using System;
using System.Collections.Generic;
using System.Linq;

namespace Blam.Halo3.Tags;

// A scenario structure BSP (scnr +0x18, 0x6C): its sbsp, the Lbsp holding its geometry and static lighting, and its baked cube maps (+0x48, a bitmap of cube images the sbsp's clusters and instances index).
public sealed record ScenarioStructureBsp( int Index, TagEntry Bsp, TagEntry Lightmap, TagEntry Cubemaps );

// A zone set (scnr +0x58, 0x28): the BSPs loaded together.
public sealed record ScenarioZoneSet( int Index, string Name, uint BspMask );

// The scenario's structure BSPs and zone sets (scenario.md §2). All BSPs share one world frame.
public sealed class ScenarioStructureBsps
{
	public IReadOnlyList<ScenarioStructureBsp> Bsps { get; }
	public IReadOnlyList<ScenarioZoneSet> ZoneSets { get; }

	// The scenario's cube map sample points (scnr +0x744, 0x10: position, u16), what the BSPs' cluster and instance cube map indices name.
	public IReadOnlyList<System.Numerics.Vector3> CubemapPoints { get; }

	public ScenarioStructureBsps( CacheFile cache )
	{
		var scenario = cache.Scenario.Data;

		// The scenario lightmap (sLdT, scnr +0x6AC) lists the Lbsp tags; each names its BSP index and the BSP's checksum (130_epilogue lists them out of order).
		var lightmaps = new List<TagEntry>();
		if ( scenario.Reference( 0x6AC ) is { Address: not 0 } scenarioLightmap )
		{
			foreach ( var e in scenarioLightmap.Data.Block( 0x4, 0x10 ) )
			{
				if ( e.Reference( 0 ) is { Address: not 0 } lightmap )
					lightmaps.Add( lightmap );
			}
		}

		var bsps = new List<ScenarioStructureBsp>();
		foreach ( var e in scenario.Block( 0x18, 0x6C ) )
		{
			var index = bsps.Count;
			var bsp = e.Reference( 0 ) is { Address: not 0 } b ? b : null;
			TagEntry lightmap = null;
			if ( bsp is not null )
			{
				var checksum = bsp.Data.U32( 0 );
				bool Matches( TagEntry t ) => t.Data.I16( 2 ) == index && t.Data.U32( 4 ) == checksum;
				lightmap = lightmaps.FirstOrDefault( Matches )
					?? cache.TagsOf( "Lbsp" ).FirstOrDefault( t => t.Address != 0 && Matches( t ) )
					?? lightmaps.FirstOrDefault( t => t.Data.I16( 2 ) == index );
			}

			bsps.Add( new ScenarioStructureBsp( index, bsp, lightmap, e.Reference( 0x48 ) is { Address: not 0 } cubemaps ? cubemaps : null ) );
		}
		Bsps = bsps;

		CubemapPoints = scenario.Block( 0x744, 0x10 ).Select( p => p.Vec3( 0 ) ).ToArray();

		var zoneSets = new List<ScenarioZoneSet>();
		foreach ( var z in scenario.Block( 0x58, 0x28 ) )
			zoneSets.Add( new ScenarioZoneSet( zoneSets.Count, z.StringId( 0 ) ?? "", z.U32( 0xC ) ) );
		ZoneSets = zoneSets;
	}

	// The BSPs a whole-map scene shows: every BSP some zone set loads (all of them in multiplayer), leaving out BSPs no zone set names (cinematic ones) and later copies of an area a BSP already shows.
	// Copies: 120_halo's 120_bsp_070 / 060 redo 010 / 020 (the escape) with their instances at the same spots, and never load with them.
	public uint SceneBspMask
	{
		get
		{
			var union = ZoneSets.Aggregate( 0u, ( m, z ) => m | z.BspMask );
			if ( union == 0 )
				return Bsps.Count >= 32 ? uint.MaxValue : (1u << Bsps.Count) - 1;

			var mask = 0u;
			var kept = new List<(int Index, HashSet<(int, int, int)> Spots)>();
			foreach ( var bsp in Bsps )
			{
				if ( bsp.Index >= 32 || (union & 1u << bsp.Index) == 0 )
					continue;

				var spots = InstanceSpots( bsp.Bsp );
				bool LoadedTogether( int other ) => ZoneSets.Any( z => (z.BspMask & 1u << bsp.Index) != 0 && (z.BspMask & 1u << other) != 0 );
				if ( spots.Count > 0 && kept.Any( k => !LoadedTogether( k.Index ) && k.Spots.Count > 0 && k.Spots.Count( spots.Contains ) > Math.Min( spots.Count, k.Spots.Count ) / 2 ) )
					continue;

				kept.Add( (bsp.Index, spots) );
				mask |= 1u << bsp.Index;
			}
			return mask;
		}
	}

	// Where a BSP's instanced geometry is placed, to the centimetre.
	static HashSet<(int, int, int)> InstanceSpots( TagEntry bsp )
	{
		var spots = new HashSet<(int, int, int)>();
		if ( bsp is null )
			return spots;
		foreach ( var i in bsp.Data.Block( 0x1C0, 0x78 ) )
		{
			var p = i.Vec3( 0x28 );
			spots.Add( ((int)MathF.Round( p.X * 100 ), (int)MathF.Round( p.Y * 100 ), (int)MathF.Round( p.Z * 100 )) );
		}
		return spots;
	}

	public IEnumerable<ScenarioStructureBsp> SceneBsps
	{
		get
		{
			var mask = SceneBspMask;
			return Bsps.Where( b => b.Index < 32 && (mask & 1u << b.Index) != 0 );
		}
	}
}
