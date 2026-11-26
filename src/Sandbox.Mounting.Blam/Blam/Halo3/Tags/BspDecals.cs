using System.Collections.Generic;
using System.Linq;

namespace Blam.Halo3.Tags;

using Quaternion = System.Numerics.Quaternion;

// A structure BSP's runtime decals (sbsp +0x174, 0x24 each): the scenario's decals that sit in this BSP, with the palette index into the scenario's decal palette (scnr +0x354).
// They're stored RuntimeOffset off the surface along their normal (rotation's z); Read moves them back onto it, where the scenario has them.
public static class BspDecals
{
	public const float RuntimeOffset = 0.1f;

	public static List<ScenarioDecal> Read( StructureBspTag bsp, ScenarioTag scenario )
	{
		var palette = scenario.Tag.Data.Block( 0x354, 0x10 ).Select( e => e.Reference( 0 ) ).ToList();
		return bsp.Tag.Data.Block( 0x174, 0x24 )
			.Where( e => e.F32( 0x20 ) != 0 || e.Vec4( 4 ) != System.Numerics.Vector4.Zero )
			.Select( e =>
			{
				var p = e.I16( 0 );
				var rotation = new Quaternion( e.F32( 4 ), e.F32( 8 ), e.F32( 0xC ), e.F32( 0x10 ) );
				var position = e.Vec3( 0x14 ) - System.Numerics.Vector3.Transform( System.Numerics.Vector3.UnitZ, rotation ) * RuntimeOffset;
				return new ScenarioDecal( p >= 0 && p < palette.Count ? palette[p] : null, -1, rotation, position, e.F32( 0x20 ) );
			} )
			.ToList();
	}
}
