using System;
using Halo.Data;

namespace Halo.Vehicles;

// A trak tag: the follow camera's position behind its unit per pitch, from straight down to straight up.
// Points are in world units in the unit's heading frame: x forward, y left, z up.
public sealed class CameraTrackTag {
	public List<CameraTrackPointTag> ControlPoints { get; set; } = new();
}

public sealed class CameraTrackPointTag {
	public float[] Position { get; set; }
}

// An entry in a unit's or seat's camera tracks; the first is the "loose" track.
public sealed class UnitCameraTrackTag {
	public string Track { get; set; }
}

// Camera tracks by tag path, and the follow camera's offset from its unit's camera point at a pitch.
public static class CameraTracks {
	// The globals' track for a unit or seat that has none.
	public const string DefaultTrack = "globals/default unit camera track.trak";

	static readonly Dictionary<string, Vector3[]> Cache = new( StringComparer.OrdinalIgnoreCase );

	// The multiplayer units' tracks, identical across MCC's maps, for when the mount has no trak export.
	static readonly Dictionary<string, float[]> BuiltIn = new( StringComparer.OrdinalIgnoreCase ) {
		[DefaultTrack] = new[] { 0.238688f, -0.005788f, 3.886218f, -1.485796f, -0.001886f, 3.676358f, -2.860402f, 0.000363f, 2.956125f, -3.522356f, 0.00095f, 1.838378f, -3.682641f, -0.0001f, 0.575224f, -3.181317f, -0.003803f, -0.770867f, -2.072483f, -0.007042f, -1.575168f, -0.981256f, -0.007047f, -1.864835f, -0.038148f, -0.00382f, -1.979462f },
		["camera/warthog_driver_loose.trak"] = new[] { 0.597417f, 0f, 3.543103f, -0.836937f, 0f, 3.229866f, -1.917906f, 0f, 2.432549f, -2.576658f, 0f, 1.61037f, -2.752378f, 0f, 0.682699f, -2.467602f, 0f, 0.114112f, -1.878554f, 0f, -0.124401f, -1.363788f, 0f, -0.171676f, -1.020858f, 0f, -0.180858f },
		["camera/warthog_driver_tight.trak"] = new[] { -0.128202f, 0f, 1.064106f, -0.232155f, 0f, 1.012851f, -0.304235f, 0f, 0.94822f, -0.383096f, 0f, 0.827162f, -0.394715f, 0f, 0.719515f, -0.369793f, 0f, 0.671904f, -0.35502f, 0f, 0.653725f, -0.332192f, 0f, 0.645412f, -0.299909f, 0f, 0.6385f },
		["camera/scorpion_driver_loose.trak"] = new[] { 0.332777f, 0f, 4.69057f, -1.93735f, 0f, 4.56439f, -3.377555f, 0f, 4.108463f, -4.256306f, 0f, 3.283644f, -4.613002f, 0f, 2.15711f, -4.373931f, 0f, 1.020149f, -3.784127f, 0f, 0.285197f, -2.77139f, 0f, -0.065797f, -1.847104f, 0f, -0.152361f },
		["camera/banshee_driver_loose.trak"] = new[] { 1.682541f, 0f, 4.203558f, -0.333569f, 0f, 4.059885f, -1.998919f, 0f, 3.248133f, -2.66779f, 0f, 2.373455f, -2.932936f, 0f, 1.17837f, -2.776114f, 0f, 0.085579f, -2.389895f, 0f, -0.866665f, -1.712045f, 0f, -1.748441f, -0.717606f, 0f, -2.483329f },
		["camera/vehicle_small_tight.trak"] = new[] { 0.171429f, -0.005481f, 2.499183f, -1.0196f, -0.001579f, 2.265603f, -1.790453f, 0.00067f, 1.744535f, -2.081166f, 0.001257f, 1.100878f, -2.047957f, 0.000207f, 0.421911f, -1.764166f, -0.003495f, 0.119336f, -1.397376f, -0.006734f, -0.004846f, -1.124403f, -0.00674f, -0.034721f, -0.97623f, -0.003513f, -0.03264f },
		["camera/warthog_gunner_fixed.trak"] = new[] { 0.341366f, 0f, 0.164127f, 0.340094f, 0f, 0.163288f, 0.339443f, 0f, 0.161945f, 0.3397f, 0f, 0.160688f, 0.340386f, 0f, 0.159883f, 0.340386f, 0f, 0.159883f },
		["camera/biped_small_tight.trak"] = new[] { 0.1048f, 0f, 0.910952f, -0.38677f, 0f, 0.853915f, -0.800739f, 0f, 0.704265f, -1.059444f, 0f, 0.293576f, -1.066926f, 0f, -0.163176f, -0.873712f, 0f, -0.452989f, -0.57649f, 0f, -0.513794f, -0.400083f, 0f, -0.527226f, -0.287233f, 0f, -0.528429f },
	};

	// Control points in world units: the mount's export, else the built-in copy, else null.
	public static Vector3[] Get( string path ) {
		if ( string.IsNullOrEmpty( path ) ) return null;
		path = path.Replace( '\\', '/' );
		if ( Cache.TryGetValue( path, out var cached ) ) return cached;

		Vector3[] points = null;
		var tag = HaloDataStore.Tag<CameraTrackTag>( path );
		if ( tag?.ControlPoints is { Count: >= 4 } list )
			points = list.Select( x => x.Position is { Length: >= 3 } p ? new Vector3( p[0], p[1], p[2] ) : Vector3.Zero ).ToArray();
		else if ( BuiltIn.TryGetValue( path, out var raw ) ) {
			points = new Vector3[raw.Length / 3];
			for ( var i = 0; i < points.Length; i++ )
				points[i] = new Vector3( raw[i * 3], raw[i * 3 + 1], raw[i * 3 + 2] );
		}

		// Only cache a miss once the mount is up, so an early request is retried.
		if ( points is not null || HaloDataStore.IsAvailable ) Cache[path] = points;
		return points;
	}

	// Halo uses the seat's first ("loose") track, or the globals' default when there's none.
	public static Vector3[] ForSeat( VehicleSeatTag seat ) {
		var path = seat?.CameraTracks?.FirstOrDefault()?.Track;
		return Get( path ) ?? Get( DefaultTrack );
	}

	// The offset in world units at a pitch in radians, up positive.
	// Pitch maps to 0..1 across the points, and a uniform cubic through four of them is evaluated there.
	public static Vector3 Splut( Vector3[] points, float pitch ) {
		if ( points is null || points.Length < 4 ) return Vector3.Zero;

		var t = (pitch + MathF.PI / 2) * (1 / MathF.PI);
		var count = points.Length;
		var frame = (int)((count - 1) * t);
		var index = (short)frame;
		var h = 1f / (count - 1);
		while ( index > 0 && (index + 4 > count || index > (short)frame - 1) )
			index--;

		var t0 = index * h;
		return new Vector3(
			Spline( points[index].x, points[index + 1].x, points[index + 2].x, points[index + 3].x, t0, h, t ),
			Spline( points[index].y, points[index + 1].y, points[index + 2].y, points[index + 3].y, t0, h, t ),
			Spline( points[index].z, points[index + 1].z, points[index + 2].z, points[index + 3].z, t0, h, t ) );
	}

	// Newton's forward differences through four evenly spaced points.
	static float Spline( float f0, float f1, float f2, float f3, float t0, float h, float t ) {
		f3 -= f2;
		f2 -= f1;
		f1 -= f0;
		f3 -= f2;
		f2 -= f1;
		return f0 + (t - t0) / h *
			(f1 + (t - (t0 + h)) *
				(f2 + (t - (t0 + 2 * h)) * (f3 - f2) / (3 * h)) / (2 * h));
	}
}
