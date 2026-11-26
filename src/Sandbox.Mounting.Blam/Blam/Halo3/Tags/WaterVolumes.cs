using System.Collections.Generic;

namespace Blam.Halo3.Tags;

using Vector3 = System.Numerics.Vector3;
using Vector4 = System.Numerics.Vector4;

// The water physics volumes the camera can be under: each BSP's structure design (scnr +0x18 bsp element +0x10, sddt) water instances (+0x34, 0x2C: i16 group, +0x14 planes as normal and distance) inside all of whose planes a point is underwater, the group (+0x28 names) picking the sky atmosphere's underwater setting (skya +0x40, 0x14: name, murkiness, colour) or Halo 3's default murkiness 0.8 and colour (0.1, 0.2, 0.12).
public sealed class WaterVolumes
{
	public sealed record Volume( IReadOnlyList<Vector4> Planes, float Murkiness, Vector3 Color );

	public const float DefaultMurkiness = 0.8f;
	public static readonly Vector3 DefaultColor = new( 0.1f, 0.2f, 0.12f );

	public IReadOnlyList<Volume> Volumes { get; }

	public WaterVolumes( CacheFile cache )
	{
		var volumes = new List<Volume>();
		var scenario = cache?.Scenario?.Data ?? default;
		if ( !scenario.IsNull )
		{
			var settings = new Dictionary<string, (float Murkiness, Vector3 Color)>();
			var skya = scenario.Reference( 0x680 );
			if ( skya is not null && !skya.Data.IsNull )
			{
				foreach ( var u in skya.Data.Block( 0x40, 0x14 ) )
					settings.TryAdd( u.StringId( 0x00 ) ?? "", (u.F32( 0x04 ), u.Vec3( 0x08 )) );
			}

			foreach ( var bsp in scenario.Block( 0x18, 0x6C ) )
			{
				var design = bsp.Reference( 0x10 );
				if ( design is null || design.Data.IsNull )
					continue;

				var groups = new List<string>();
				foreach ( var g in design.Data.Block( 0x28, 0x4 ) )
					groups.Add( g.StringId( 0x00 ) ?? "" );

				foreach ( var instance in design.Data.Block( 0x34, 0x2C ) )
				{
					var planes = new List<Vector4>();
					foreach ( var p in instance.Block( 0x14, 0x10 ) )
						planes.Add( new Vector4( p.Vec3( 0x00 ), p.F32( 0x0C ) ) );
					if ( planes.Count == 0 )
						continue;

					var group = instance.I16( 0x00 );
					var name = group >= 0 && group < groups.Count ? groups[group] : "";
					var (murkiness, color) = settings.TryGetValue( name, out var setting ) ? setting : (DefaultMurkiness, DefaultColor);
					volumes.Add( new Volume( planes, murkiness, color ) );
				}
			}
		}
		Volumes = volumes;
	}

	// The volume containing p (world units), or null.
	public Volume At( Vector3 p )
	{
		foreach ( var volume in Volumes )
		{
			var inside = true;
			foreach ( var plane in volume.Planes )
			{
				if ( plane.X * p.X + plane.Y * p.Y + plane.Z * p.Z - plane.W > 0 )
				{
					inside = false;
					break;
				}
			}
			if ( inside )
				return volume;
		}
		return null;
	}
}
