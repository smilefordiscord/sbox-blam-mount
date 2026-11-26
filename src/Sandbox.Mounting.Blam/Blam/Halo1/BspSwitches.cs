using System.Collections.Generic;
using System.Numerics;

namespace Blam.Halo1;

// Inside the namespace so it wins over s&box's global Vector3 when this compiles into the mount.
using Vector3 = System.Numerics.Vector3;

// A scenario trigger volume (scnr 0x360, 0x60 each): a box from its corner along forward, left and up by its extents (world units).
public sealed record TriggerVolume( string Name, int Type, Vector3 Forward, Vector3 Up, Vector3 Position, Vector3 Extents )
{
	public bool Contains( Vector3 point )
	{
		var offset = point - Position;
		var left = Vector3.Cross( Up, Forward );
		var f = Vector3.Dot( offset, Forward );
		var l = Vector3.Dot( offset, left );
		var u = Vector3.Dot( offset, Up );
		return f >= 0 && f <= Extents.X && l >= 0 && l <= Extents.Y && u >= 0 && u <= Extents.Z;
	}
}

// Where a campaign level switches structure BSPs: standing in the volume with the source BSP loaded loads the destination (scnr 0x39C, 8 bytes: trigger volume, source, destination, cutscene flag).
public sealed record BspSwitch( TriggerVolume Volume, int Source, int Destination );

public static class BspSwitches
{
	public static List<BspSwitch> Read( CacheFile cache )
	{
		var d = cache.TagData;
		var a = cache.Scenario.Address;
		var volumes = new List<TriggerVolume>();
		foreach ( var e in d.Block( a + 0x360, 0x60 ) )
			volumes.Add( new TriggerVolume( d.CString( e + 0x4, 32 ), d.I16( e ), d.Vec3( e + 0x30 ), d.Vec3( e + 0x3C ), d.Vec3( e + 0x48 ), d.Vec3( e + 0x54 ) ) );

		var switches = new List<BspSwitch>();
		foreach ( var e in d.Block( a + 0x39C, 0x8 ) )
		{
			var volume = d.I16( e );
			if ( volume >= 0 && volume < volumes.Count )
				switches.Add( new BspSwitch( volumes[volume], d.I16( e + 0x2 ), d.I16( e + 0x4 ) ) );
		}
		return switches;
	}
}
