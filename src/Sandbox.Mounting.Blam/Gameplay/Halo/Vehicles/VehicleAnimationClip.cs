using System;
using Halo.Combat;

namespace Halo.Vehicles;

// One vehicle graph animation from the mount's export (mount://haloce/anims/<graph>/<animation>.json).
// Vehicle graphs list no nodes, so Halo applies them by node index; the rocket Warthog plays warthog.antr.
// Base frames are full local transforms; overlay frames are deltas.
public sealed class VehicleAnimationClip {
	sealed class FileKey {
		public int Node { get; set; }
		public float[] Rotation { get; set; }
		public float[] Position { get; set; }
		public float? Scale { get; set; }
	}

	sealed class FileFrame {
		public List<FileKey> Nodes { get; set; } = new();
	}

	sealed class FileClip {
		public string Name { get; set; }
		public string Type { get; set; }
		public int FrameCount { get; set; }
		public List<FileFrame> Frames { get; set; } = new();
	}

	public sealed class Track {
		public Rotation[] Rotation;
		public Vector3[] Position;
		public float[] Scale;
	}

	public string Name { get; private init; }
	public bool IsOverlay { get; private init; }
	public int FrameCount { get; private init; }

	// By node index, only for nodes the animation has.
	public Dictionary<int, Track> Tracks { get; } = new();

	static readonly Dictionary<string, VehicleAnimationClip> Cache = new();

	public static VehicleAnimationClip Load( string graph, string animation ) {
		if ( string.IsNullOrEmpty( graph ) || string.IsNullOrEmpty( animation ) ) return null;
		var key = $"anims/{Combat.HaloEffects.WithoutExtension( graph )}/{animation}.json";
		if ( Cache.TryGetValue( key, out var cached ) ) return cached;

		VehicleAnimationClip clip = null;
		try {
			var json = Data.HaloDataStore.ReadMount( key );
			if ( !string.IsNullOrEmpty( json ) )
				clip = FromFile( Json.Deserialize<FileClip>( json ) );
		}
		catch ( Exception e ) {
			Log.Warning( $"Couldn't read {key}: {e.Message}" );
		}

		Cache[key] = clip;
		return clip;
	}

	static VehicleAnimationClip FromFile( FileClip file ) {
		if ( file is null || file.Frames.Count == 0 ) return null;
		var frames = file.Frames.Count;
		var clip = new VehicleAnimationClip {
			Name = file.Name,
			IsOverlay = string.Equals( file.Type, "overlay", StringComparison.OrdinalIgnoreCase ),
			FrameCount = frames,
		};

		for ( var f = 0; f < frames; f++ ) {
			foreach ( var k in file.Frames[f]?.Nodes ?? new() ) {
				if ( k.Node < 0 ) continue;
				if ( !clip.Tracks.TryGetValue( k.Node, out var track ) ) {
					track = new Track { Rotation = new Rotation[frames], Position = new Vector3[frames], Scale = new float[frames] };
					Array.Fill( track.Rotation, Rotation.Identity );
					Array.Fill( track.Scale, 1f );
					clip.Tracks[k.Node] = track;
				}

				if ( k.Rotation is { Length: 4 } r ) track.Rotation[f] = new Rotation( r[0], r[1], r[2], r[3] );
				if ( k.Position is { Length: 3 } p ) track.Position[f] = new Vector3( p[0], p[1], p[2] );
				if ( k.Scale is float s ) track.Scale[f] = s;
			}
		}
		return clip;
	}

	// Applying

	// A base animation frame, blended toward the next one for interpolation between ticks.
	public void ApplyBase( Transform[] locals, int frame, float fraction ) {
		if ( IsOverlay || FrameCount == 0 ) return;
		frame = Math.Clamp( frame, 0, FrameCount - 1 );
		var next = Math.Min( frame + 1, FrameCount - 1 );
		foreach ( var (node, track) in Tracks ) {
			if ( node >= locals.Length ) continue;
			var a = new Transform( track.Position[frame], track.Rotation[frame], track.Scale[frame] );
			var b = new Transform( track.Position[next], track.Rotation[next], track.Scale[next] );
			locals[node] = fraction > 0 && next != frame ? UnitAnimation.Blend( a, b, fraction ) : a;
		}
	}

	// Rotation post-multiplied, position added, scale multiplied.
	public void ApplyOverlay( Transform[] locals, int frame ) {
		if ( !IsOverlay || frame < 0 || frame >= FrameCount ) return;
		foreach ( var (node, track) in Tracks ) {
			if ( node >= locals.Length ) continue;
			var l = locals[node];
			locals[node] = new Transform( l.Position + track.Position[frame], l.Rotation * track.Rotation[frame], l.Scale * track.Scale[frame] );
		}
	}

	// An overlay frame at a weight: rotation blended from identity, position and scale by the weight.
	public void ApplyScaled( Transform[] locals, int frame, float weight ) {
		if ( !IsOverlay || frame < 0 || frame >= FrameCount ) return;
		foreach ( var (node, track) in Tracks ) {
			if ( node >= locals.Length ) continue;
			var l = locals[node];
			var rotation = UnitAnimation.Nlerp( Rotation.Identity, track.Rotation[frame], weight );
			locals[node] = new Transform( l.Position + track.Position[frame] * weight, l.Rotation * rotation, l.Scale * (track.Scale[frame] * weight + 1 - weight) );
		}
	}

	// An overlay at a fractional frame, the last frame wrapping to the first.
	// Past the end it's the last frame blended fully to the first.
	public void ApplyContinuous( Transform[] locals, float realFrame ) {
		if ( !IsOverlay || FrameCount == 0 ) return;
		var fraction = realFrame % 1f;
		var frame = (int)MathF.Floor( MathF.Abs( realFrame ) );
		if ( frame >= FrameCount ) {
			frame = FrameCount - 1;
			fraction = 1;
		}
		var next = frame == FrameCount - 1 ? 0 : frame + 1;

		foreach ( var (node, track) in Tracks ) {
			if ( node >= locals.Length ) continue;
			var l = locals[node];
			// A spherical blend turns at a constant rate between frames; the wheels' few widely spaced frames lurched with a normalised lerp
			// once drawn every frame between ticks.
			var rotation = Rotation.Slerp( track.Rotation[frame], track.Rotation[next], fraction );
			var position = track.Position[frame] * (1 - fraction) + track.Position[next] * fraction;
			var scale = track.Scale[frame] * (1 - fraction) + track.Scale[next] * fraction;
			locals[node] = new Transform( l.Position + position, l.Rotation * rotation, l.Scale * scale );
		}
	}

	// Frames form a grid with yaw across and pitch down the rows.
	// The four frames around the angle blend and apply as an overlay, rotation and position only.
	public void ApplyAimingScreen( Transform[] locals, UnitAnimationGraph.AimingScreen bounds, float yaw, float pitch ) {
		var width = bounds.NegativeYawFrames + bounds.PositiveYawFrames + 1;
		var height = bounds.NegativePitchFrames + bounds.PositivePitchFrames + 1;
		if ( !IsOverlay || FrameCount < width * height ) return;

		var (yawIndex, yawFraction) = Cell( yaw, bounds.NegativeYawDelta, bounds.PositiveYawDelta, bounds.NegativeYawFrames, bounds.PositiveYawFrames );
		var (pitchIndex, pitchFraction) = Cell( pitch, bounds.NegativePitchDelta, bounds.PositivePitchDelta, bounds.NegativePitchFrames, bounds.PositivePitchFrames );
		if ( pitchIndex < 0 || pitchIndex >= height || yawIndex < 0 || yawIndex >= width ) return;

		var nextYaw = yawIndex + 1 == width ? yawIndex : yawIndex + 1;
		var nextPitch = pitchIndex + 1 == height ? pitchIndex : pitchIndex + 1;
		var f00 = yawIndex + pitchIndex * width;
		var f10 = nextYaw + pitchIndex * width;
		var f01 = yawIndex + nextPitch * width;
		var f11 = nextYaw + nextPitch * width;

		foreach ( var (node, track) in Tracks ) {
			if ( node >= locals.Length ) continue;
			var rotation = UnitAnimation.Nlerp( UnitAnimation.Nlerp( track.Rotation[f00], track.Rotation[f10], yawFraction ), UnitAnimation.Nlerp( track.Rotation[f01], track.Rotation[f11], yawFraction ), pitchFraction );
			var position = (track.Position[f01] * (1 - yawFraction) + track.Position[f11] * yawFraction) * pitchFraction
				+ (track.Position[f00] * (1 - yawFraction) + track.Position[f10] * yawFraction) * (1 - pitchFraction);
			var l = locals[node];
			locals[node] = new Transform( l.Position + position, l.Rotation * rotation, l.Scale );
		}
	}

	static (int Index, float Fraction) Cell( float angle, float negativeDelta, float positiveDelta, int negativeFrames, int positiveFrames ) {
		var delta = angle < 0 ? negativeDelta : positiveDelta;
		var frame = delta == 0 ? 0 : angle / delta;
		var index = (int)frame;
		var fraction = frame % 1f;
		if ( fraction < 0 ) {
			fraction += 1;
			index--;
		}
		if ( index >= positiveFrames ) {
			index = positiveFrames - 1;
			fraction = 1;
		}
		if ( index < -negativeFrames ) {
			index = -negativeFrames;
			fraction = 0;
		}
		return (index + negativeFrames, fraction);
	}
}
