using System;

namespace Halo;

// Conversions between Halo world units (WU) at 30 ticks per second and s&box inches per second.
// The mount scales the world so the Chief's 0.62 WU camera height is 64 inches.
public static class HaloUnits {
	// Inches per world unit; must match the mount's HaloCeMount.WorldScale.
	public const float WorldScale = 64f / 0.62f;

	public const float TicksPerSecond = 30f;
	public const float TickInterval = 1f / TicksPerSecond;

	// Halo's gravity in WU per tick².
	public const float GravityPerTick = 0.0035651792f;

	// Gravity in inches per second².
	public static float Gravity => GravityPerTick * TicksPerSecond * TicksPerSecond * WorldScale;

	public static float ToInches( float worldUnits ) => worldUnits * WorldScale;
	public static float ToWorldUnits( float inches ) => inches / WorldScale;

	public static float PerTickToInchesPerSecond( float wuPerTick ) => wuPerTick * TicksPerSecond * WorldScale;

	public static float PerSecondToInchesPerSecond( float wuPerSecond ) => wuPerSecond * WorldScale;

	public static Vector3 ToWorld( float[] haloPoint ) {
		if ( haloPoint is null || haloPoint.Length < 3 ) return Vector3.Zero;
		return new Vector3( haloPoint[0], haloPoint[1], haloPoint[2] ) * WorldScale;
	}

	// A placed object's orientation from yaw, pitch and roll in degrees, about world Z, then Y, then X as in Halo.
	// Positive pitch raises the nose, the opposite of s&box.
	public static Rotation FromEuler( float yaw, float pitch, float roll ) {
		var (sy, cy) = MathF.SinCos( yaw.DegreeToRadian() );
		var (sp, cp) = MathF.SinCos( pitch.DegreeToRadian() );
		var (sr, cr) = MathF.SinCos( roll.DegreeToRadian() );
		var forward = new Vector3( cy * cp, sy * cr - cy * sp * sr, cy * sp * cr + sy * sr );
		var up = new Vector3( -sp, -cp * sr, cp * cr );
		return Rotation.LookAt( forward, up );
	}

	public static int SecondsToTicks( float seconds ) => (int)MathF.Round( seconds * TicksPerSecond );
}
