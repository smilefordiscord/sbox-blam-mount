using System.Linq;

namespace Blam.Halo1.Tags;

// Inside the namespace so these win over s&box's global types when this compiles into the mount.
using Vector3 = System.Numerics.Vector3;

/// <summary>
/// The active camouflage settings in the globals tag's rasterizer data: how a cloaked unit bends
/// the scene behind it.
/// </summary>
/// <remarks>
/// The globals' rasterizer data is a block at matg 0x134 (0x1AC bytes, one entry). Active camouflage
/// distortion bitmap 0x50 (a cube map whose texels perturb the screen lookup), flags 0x170 (0x1 tint
/// edge density), refraction amount 0x174, distance falloff 0x178, tint color 0x17C, and the same
/// three for hyper stealth at 0x188/0x18C/0x190. Halo blends the normal and hyper stealth values by
/// the unit's camouflage "parameter" (0 in normal play).
/// </remarks>
public sealed record ActiveCamouflageGlobals( TagEntry Distortion, bool TintEdgeDensity, float RefractionAmount, float DistanceFalloff, Vector3 Tint,
	float HyperStealthRefractionAmount, float HyperStealthDistanceFalloff, Vector3 HyperStealthTint )
{
	public static ActiveCamouflageGlobals Read( CacheFile cache )
	{
		var globals = cache.Tags.FirstOrDefault( t => t.Class.ToString() == "matg" );
		if ( globals is null )
			return null;

		var data = cache.TagData;
		var entry = data.Block( globals.Address + 0x134, 0x1AC ).FirstOrDefault();
		if ( entry == 0 )
			return null;

		return new ActiveCamouflageGlobals(
			cache.GetTagReference( data, entry + 0x50 ),
			(data.U16( entry + 0x170 ) & 1) != 0,
			data.F32( entry + 0x174 ), data.F32( entry + 0x178 ), data.Vec3( entry + 0x17C ),
			data.F32( entry + 0x188 ), data.F32( entry + 0x18C ), data.Vec3( entry + 0x190 ) );
	}
}
