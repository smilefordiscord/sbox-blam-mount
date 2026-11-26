using Halo2.Data;
using System;

namespace Halo2.Combat;

// Conversions between Halo world units (WU) at 30 ticks per second and s&box inches per second.
// The mount scales the world so the Chief's 0.62 WU camera height is 64 inches.
public static class HaloUnits {
	// Inches per world unit; must match the mount's Halo2Mount.WorldScale, which is the Halo CE mount's.
	public const float WorldScale = 64f / 0.62f;

	public const float TicksPerSecond = 30f;
	public const float TickInterval = 1f / TicksPerSecond;

	// Halo 2's physics gravity in WU per second².
	public const float GravityPerSecond = 4.17126f;

	// Gravity in inches per second².
	public static float Gravity => GravityPerSecond * WorldScale;

	public static float ToInches( float worldUnits ) => worldUnits * WorldScale;
	public static float ToWorldUnits( float inches ) => inches / WorldScale;

	public static float PerTickToInchesPerSecond( float wuPerTick ) => wuPerTick * TicksPerSecond * WorldScale;

	public static float PerSecondToInchesPerSecond( float wuPerSecond ) => wuPerSecond * WorldScale;

	public static Vector3 ToWorld( float[] haloPoint ) {
		if ( haloPoint is null || haloPoint.Length < 3 ) return Vector3.Zero;
		return new Vector3( haloPoint[0], haloPoint[1], haloPoint[2] ) * WorldScale;
	}

	// A placement's yaw, pitch and roll in degrees, turned as Halo places objects.
	// Halo turns about the world's Z by the yaw, then its Y by the pitch (positive raises the nose), then its X by the roll.
	// s&box Angles turn about the object's own axes instead, which differs once two of the angles are set.
	public static Rotation ToRotation( float[] yawPitchRoll ) {
		if ( yawPitchRoll is not { Length: >= 3 } a ) return Rotation.Identity;
		var (sy, cy) = MathF.SinCos( a[0].DegreeToRadian() );
		var (sp, cp) = MathF.SinCos( a[1].DegreeToRadian() );
		var (sr, cr) = MathF.SinCos( a[2].DegreeToRadian() );
		var forward = new Vector3( cy * cp, sy * cr - cy * sp * sr, cy * sp * cr + sy * sr );
		var up = new Vector3( -sp, -cp * sr, cp * cr );
		return Rotation.LookAt( forward, up );
	}
}
