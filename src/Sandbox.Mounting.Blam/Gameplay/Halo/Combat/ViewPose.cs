using System;

namespace Halo.Combat;

// Halo's first person weapon sway: three springs driven by movement, turning and firing.
// Their values scale the frames of the weapon's "first-person overlays" animation.
public sealed class FirstPersonSway {
	// Drawn between the last two ticks, as MCC does, so the sway is smooth at any frame rate.
	static float Blend => HaloTick.Fraction;

	// i = forward/back input, j = left/right (left +), -1..1.
	public Vector2 Position => new( _piPrev.LerpTo( _pi, Blend ), _pjPrev.LerpTo( _pj, Blend ) );
	float _pi, _pj, _piVelocity, _pjVelocity, _piPrev, _pjPrev;

	// i = yaw (left +), j = pitch (down +), -1..1.
	public Vector2 Turning => new( _tiPrev.LerpTo( _ti, Blend ), _tjPrev.LerpTo( _tj, Blend ) );
	float _ti, _tj, _tiVelocity, _tjVelocity, _tiPrev, _tjPrev;

	// 0..1, kicked by every primary shot.
	public float PushBack => _pushPrev.LerpTo( _push, Blend );
	float _push, _pushBackVelocity, _pushPrev;

	Angles _lastFacing;
	bool _hasFacing;

	const float FiringPushBackVelocity = 0.05f;

	public void Reset() {
		_pi = _pj = _piVelocity = _pjVelocity = _piPrev = _pjPrev = 0;
		_ti = _tj = _tiVelocity = _tjVelocity = _tiPrev = _tjPrev = 0;
		_push = _pushBackVelocity = _pushPrev = 0;
		_hasFacing = false;
	}

	// A weapon switch resets only the firing push-back, as in MCC.
	// The position and turning springs carry on, so the walking lean doesn't drop out on every switch.
	public void WeaponReadied() {
		_push = _pushBackVelocity = _pushPrev = 0;
	}

	// Called on each primary trigger shot.
	public void Fired() => _pushBackVelocity += FiringPushBackVelocity;

	// All springs exactly at rest, as Halo tests before running the idle timer.
	public bool Settled => _pi == 0 && _pj == 0 && _ti == 0 && _tj == 0 && _push == 0;

	// throttle is x forward, y left. facing is the view in s&box angles: pitch down +, yaw left +.
	public void Tick( Vector2 throttle, Angles facing ) {
		if ( !_hasFacing ) {
			_lastFacing = facing;
			_hasFacing = true;
		}

		// Halo's facing change over the tick in radians; its pitch is up +, ours is down +.
		var yaw = MathX.DegreeToRadian( Angles.NormalizeAngle( facing.yaw - _lastFacing.yaw ) );
		var pitchUp = MathX.DegreeToRadian( -Angles.NormalizeAngle( facing.pitch - _lastFacing.pitch ) );
		_lastFacing = facing;

		_piPrev = _pi; _pjPrev = _pj; _tiPrev = _ti; _tjPrev = _tj; _pushPrev = _push;

		Accelerate( ref _pi, ref _piVelocity, throttle.x.Clamp( -1, 1 ), 0.08f, 0.5f, -1, 1 );
		Accelerate( ref _pj, ref _pjVelocity, throttle.y.Clamp( -1, 1 ), 0.08f, 0.5f, -1, 1 );

		Accelerate( ref _ti, ref _tiVelocity, (yaw * 30).Clamp( -1, 1 ), 0.03f, 0.2f, -1, 1 );
		Accelerate( ref _tj, ref _tjVelocity, (pitchUp * -30).Clamp( -1, 1 ), 0.03f, 0.2f, -1, 1 );

		Accelerate( ref _push, ref _pushBackVelocity, 0, 0.01f, 0.2f, 0, 1 );
		if ( _push >= 1 ) _pushBackVelocity = 0;
	}

	// Halo's spring step, with Halo's parameter names.
	// "maximumVelocity" is the most the velocity changes per tick and "acceleration" is the top speed.
	static void Accelerate( ref float position, ref float velocity, float target, float maximumVelocity, float acceleration, float min, float max ) {
		var delta = target - position;
		if ( MathF.Abs( delta - velocity ) <= MathF.Min( maximumVelocity, acceleration ) ) {
			velocity = 0;
			position = target.Clamp( min, max );
			return;
		}

		var braking = (maximumVelocity + maximumVelocity) * MathF.Abs( delta );
		var speed = braking >= acceleration * acceleration ? acceleration : MathF.Sqrt( braking );
		if ( delta < 0 ) speed = -speed;

		var step = (speed - velocity).Clamp( -maximumVelocity, maximumVelocity );
		velocity += step;
		position = (step * 0.5f + velocity + position).Clamp( min, max );
	}
}

// A Halo animation as per-frame node transforms, from mount://haloce/anims/<graph>/<animation>.json.
// Overlay values are deltas: rotation post-multiplied, position added in parent space, scale multiplied.
public sealed class AnimationClip {
	sealed class FileNode {
		public string Name { get; set; }
		public int Parent { get; set; } = -1;
	}

	sealed class FileKey {
		public int Node { get; set; }
		public float[] Rotation { get; set; }
		public float[] Position { get; set; }
		public float? Scale { get; set; }
	}

	sealed class FileFrame {
		public List<FileKey> Nodes { get; set; } = new();
	}

	sealed class FileAnimated {
		public List<int> Rotation { get; set; } = new();
		public List<int> Position { get; set; } = new();
		public List<int> Scale { get; set; } = new();
	}

	sealed class FileClip {
		public string Name { get; set; }
		public string Type { get; set; }
		public int FrameCount { get; set; }
		public int LoopFrame { get; set; }
		public bool WorldRelative { get; set; }
		public List<float[]> FrameInfo { get; set; }
		public List<FileNode> Nodes { get; set; } = new();
		public List<FileFrame> Frames { get; set; } = new();
		public FileAnimated Animated { get; set; }
	}

	public sealed class Track {
		public Rotation[] Rotation;
		public Vector3[] Position;
		public float[] Scale;
	}

	public string Name { get; private init; }
	public bool IsOverlay { get; private init; }
	public bool IsReplacement { get; private init; }
	public int FrameCount { get; private init; }

	// The frame playback returns to at the end; 0 restarts the animation.
	public int LoopFrame { get; private init; }

	// The root bone's transform is its world transform, as Halo ignores the object's position and facing while it plays (c10's x50 cutscene).
	public bool WorldRelative { get; private init; }

	// Root motion per frame: movement in the object's axes (s&box units) and turn in radians.
	// Empty if the animation doesn't move.
	public (Vector3 Move, float Turn)[] FrameMotion { get; private init; } = Array.Empty<(Vector3, float)>();

	// Bones whose rotation, position or scale the animation drives.
	// A replacement replaces only these components.
	public HashSet<string> RotationAnimated { get; } = new( StringComparer.OrdinalIgnoreCase );
	public HashSet<string> PositionAnimated { get; } = new( StringComparer.OrdinalIgnoreCase );
	public HashSet<string> ScaleAnimated { get; } = new( StringComparer.OrdinalIgnoreCase );

	// Tracks by bone name, for the bones the animation moves.
	public Dictionary<string, Track> Tracks { get; } = new( StringComparer.OrdinalIgnoreCase );

	static readonly Dictionary<string, AnimationClip> Cache = new();

	// graph is the antr tag path and animation the Halo name, e.g. "first-person overlays".
	public static AnimationClip Load( string graph, string animation ) {
		if ( string.IsNullOrEmpty( graph ) || string.IsNullOrEmpty( animation ) ) return null;
		var stem = Combat.HaloEffects.WithoutExtension( graph );
		var key = $"anims/{stem}/{animation}.json";
		if ( Cache.TryGetValue( key, out var cached ) ) return cached;

		AnimationClip clip = null;
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

	static AnimationClip FromFile( FileClip file ) {
		if ( file is null || file.Frames.Count == 0 ) return null;
		var frames = file.Frames.Count;
		var clip = new AnimationClip {
			Name = file.Name,
			IsOverlay = string.Equals( file.Type, "overlay", StringComparison.OrdinalIgnoreCase ),
			IsReplacement = string.Equals( file.Type, "replacement", StringComparison.OrdinalIgnoreCase ),
			FrameCount = frames,
			LoopFrame = file.LoopFrame,
			WorldRelative = file.WorldRelative,
			FrameMotion = file.FrameInfo?.Select( f => (new Vector3( f[0], f[1], f[2] ), f[3]) ).ToArray() ?? Array.Empty<(Vector3, float)>(),
		};

		if ( file.Animated is { } animated ) {
			foreach ( var (set, nodes) in new[] { (clip.RotationAnimated, animated.Rotation), (clip.PositionAnimated, animated.Position), (clip.ScaleAnimated, animated.Scale) } )
				foreach ( var node in nodes ?? new() )
					if ( node >= 0 && node < file.Nodes.Count ) set.Add( file.Nodes[node].Name );
		}

		for ( var f = 0; f < frames; f++ ) {
			foreach ( var k in file.Frames[f]?.Nodes ?? new() ) {
				if ( k.Node < 0 || k.Node >= file.Nodes.Count ) continue;
				var name = file.Nodes[k.Node].Name;
				if ( !clip.Tracks.TryGetValue( name, out var track ) ) {
					track = new Track {
						Rotation = new Rotation[frames],
						Position = new Vector3[frames],
						Scale = new float[frames],
					};
					Array.Fill( track.Rotation, Rotation.Identity );
					Array.Fill( track.Scale, 1f );
					clip.Tracks[name] = track;
				}

				if ( k.Rotation is { Length: 4 } r ) track.Rotation[f] = new Rotation( r[0], r[1], r[2], r[3] );
				if ( k.Position is { Length: 3 } p ) track.Position[f] = new Vector3( p[0], p[1], p[2] );
				if ( k.Scale is float s ) track.Scale[f] = s;
			}
		}
		return clip;
	}
}

// Poses the first person view model as Halo does: animation, "moving" and "ammunition" overlays, sway, then blend.
// Runs as procedural bones on top of the renderer's own animation.
// Null if the mount hasn't exported the overlays.
public sealed class ViewPose {
	[ConVar( "halo_fp_pose", Help = "Halo's first person sway and pose interpolation on the view model (needs the mount's animation export).", Saved = true )]
	public static bool Enabled { get; set; } = true;

	readonly SkinnedModelRenderer _renderer;
	readonly List<(BoneCollection.Bone Bone, GameObject Object, int Parent)> _bones = new();
	readonly AnimationClip _overlays;
	readonly AnimationClip _moving;
	readonly AnimationClip _ammunition;

	Transform[] _last;
	Transform[] _snapshot;
	Transform[] _animatedWorld;
	Transform[] _finalWorld;
	int _interpolationIndex;
	int _interpolationCount;

	bool _movingActive;
	int _movingFrame;
	int _movingDrawFrom = -1;

	public bool HasMoving => _moving is not null;

	ViewPose( SkinnedModelRenderer renderer, AnimationClip overlays, AnimationClip moving, AnimationClip ammunition ) {
		_renderer = renderer;
		_overlays = overlays;
		_moving = moving;
		_ammunition = ammunition;
	}

	public static ViewPose Create( SkinnedModelRenderer renderer, string graph ) {
		if ( !Enabled || !renderer.IsValid() || renderer.Model is null ) return null;

		var overlays = AnimationClip.Load( graph, "first-person overlays" );
		if ( overlays is null || overlays.FrameCount < 9 ) return null;

		var pose = new ViewPose( renderer, overlays,
			AnimationClip.Load( graph, "first-person moving" ),
			AnimationClip.Load( graph, "first-person ammunition" ) );

		renderer.CreateBoneObjects = true;
		var all = renderer.Model.Bones.AllBones.ToList();
		foreach ( var bone in all ) {
			var go = renderer.GetBoneObject( bone.Index );
			if ( !go.IsValid() ) return null;
			pose._bones.Add( (bone, go, bone.Parent?.Index ?? -1) );
		}

		pose._last = new Transform[all.Count];
		pose._snapshot = new Transform[all.Count];
		pose._animatedWorld = new Transform[all.Count];
		pose._finalWorld = new Transform[all.Count];
		foreach ( var b in pose._bones )
			b.Object.Flags |= GameObjectFlags.ProceduralBone;
		return pose;
	}

	public void Destroy() {
		foreach ( var b in _bones )
			if ( b.Object.IsValid() ) b.Object.Flags &= ~GameObjectFlags.ProceduralBone;
	}

	// Blends from the drawn pose over this many ticks.
	// A shorter blend doesn't cut short one already under way.
	public void StartInterpolation( int ticks ) {
		// Zero ticks leaves a running blend alone; only a ready cancels one.
		// There's nothing to blend from until the pose is drawn again after hiding.
		if ( ticks <= 0 || !_primed ) return;

		Array.Copy( _last, _snapshot, _last.Length );
		if ( ticks >= _interpolationCount - _interpolationIndex ) {
			_interpolationIndex = 0;
			_interpolationCount = ticks;
		}
	}

	// Readying a weapon snaps straight to it.
	public void CancelInterpolation() => _interpolationCount = 0;

	// Call each tick after the springs.
	public void TickInterpolation() {
		if ( _interpolationCount > 0 && ++_interpolationIndex >= _interpolationCount )
			_interpolationCount = 0;
	}

	// Call when the view model is hidden.
	// The last drawn pose goes stale, so it reappears without blending.
	public void Hidden() {
		_primed = false;
		_interpolationCount = 0;
	}

	// The "moving" overlay plays while the player moves on the ground, starting at frame 0.
	// Stopping eases out over 6 ticks if the weapon is idle.
	public void TickMoving( bool moving, bool idle ) {
		if ( _moving is null ) return;
		if ( _movingActive ) {
			_movingDrawFrom = _movingFrame;
			_movingFrame = (_movingFrame + 1) % _moving.FrameCount;
			if ( moving ) return;
			if ( idle ) StartInterpolation( 6 );
			_movingActive = false;
		}
		else if ( moving ) {
			_movingActive = true;
			_movingFrame = 0;
			_movingDrawFrom = -1;
		}
	}

	bool _primed;
	int _misses;

	// The bones have been posed at least once; until then they're in the bind pose.
	public bool Primed => _primed;
	float _nextDebug;

	[ConVar( "halo_fp_pose_debug", Help = "Log what the first person pose layer is doing, once a second." )]
	public static bool Debug { get; set; } = false;

	// The renderer never gave its animated pose, so the layer is off.
	public bool Failed { get; private set; }

	// Call after the renderer's animation updates.
	public void Apply( FirstPersonSway sway, int roundsLoaded ) {
		if ( !_renderer.IsValid() ) return;

		if ( Failed ) return;

		// Local transforms come from the animated world pose, which carries the renderer's largest scale on all axes.
		// The first person spread scales the renderer unevenly, so the root uses that same even scale.
		// The animated pose was computed where the scene model was, which can be a frame behind the object when it's placed
		// at the camera after animating (Sandbox places view models in its camera setup); taking the root from the scene model
		// keeps the root bone from shifting by however far the camera moved.
		var rendererTransform = _renderer.SceneModel.IsValid() ? _renderer.SceneModel.Transform : _renderer.WorldTransform;
		var scale = rendererTransform.Scale;
		var root = rendererTransform.WithScale( MathF.Max( scale.x, MathF.Max( scale.y, scale.z ) ) );
		_drawnRenderer = rendererTransform;
		_drawnRoot = root;
		for ( var i = 0; i < _bones.Count; i++ ) {
			var bone = _bones[i].Bone;
			if ( !_renderer.TryGetBoneTransformAnimation( bone, out var world ) ) {
				// If it never gets posed, hand the bones back rather than freezing the view model.
				if ( ++_misses > 30 ) {
					Failed = true;
					Destroy();
					Log.Warning( "First person pose layer: no animated bone transforms, falling back to plain animation." );
				}
				return;
			}
			_animatedWorld[i] = world;
		}
		_misses = 0;

		// Frames and blend are drawn between the last two ticks, as MCC does.
		var fraction = HaloTick.Fraction;
		var movingFrame = !_movingActive ? -1 : _movingDrawFrom < 0 ? _movingFrame : _movingDrawFrom + fraction;
		var ammoFrame = _ammunition is not null ? Math.Clamp( roundsLoaded, 0, _ammunition.FrameCount - 1 ) : -1;
		var interpolate = _interpolationCount > 0 ? MathF.Min( 1, (_interpolationIndex + fraction) / _interpolationCount ) : 1;

		for ( var i = 0; i < _bones.Count; i++ ) {
			var (bone, go, parent) = _bones[i];
			var parentWorld = parent >= 0 ? _animatedWorld[parent] : root;
			var local = parentWorld.ToLocal( _animatedWorld[i] );
			var name = bone.Name;

			if ( ammoFrame >= 0 ) local = ClipOverlay.Apply( local, _ammunition, name, ammoFrame, 1 );
			if ( movingFrame >= 0 ) local = OverlaySmooth( local, _moving, name, movingFrame );

			if ( sway.Position.x > 0 ) local = ClipOverlay.Apply( local, _overlays, name, 0, sway.Position.x );
			else if ( sway.Position.x < 0 ) local = ClipOverlay.Apply( local, _overlays, name, 1, -sway.Position.x );
			if ( sway.Position.y < 0 ) local = ClipOverlay.Apply( local, _overlays, name, 2, -sway.Position.y );
			else if ( sway.Position.y > 0 ) local = ClipOverlay.Apply( local, _overlays, name, 3, sway.Position.y );
			if ( sway.Turning.x > 0 ) local = ClipOverlay.Apply( local, _overlays, name, 4, sway.Turning.x );
			else if ( sway.Turning.x < 0 ) local = ClipOverlay.Apply( local, _overlays, name, 5, -sway.Turning.x );
			if ( sway.Turning.y < 0 ) local = ClipOverlay.Apply( local, _overlays, name, 6, -sway.Turning.y );
			else if ( sway.Turning.y > 0 ) local = ClipOverlay.Apply( local, _overlays, name, 7, sway.Turning.y );
			if ( sway.PushBack > 0 ) local = ClipOverlay.Apply( local, _overlays, name, 8, sway.PushBack );

			if ( interpolate < 1 && _primed ) {
				var from = _snapshot[i];
				local = new Transform(
					from.Position.LerpTo( local.Position, interpolate ),
					Rotation.Lerp( from.Rotation, local.Rotation, interpolate ),
					from.Scale.LerpTo( local.Scale, interpolate ) );
			}

			_last[i] = local;

			// The renderer reads bone objects as parent-relative and applies its own scale once to the whole skeleton.
			_finalWorld[i] = (parent >= 0 ? _finalWorld[parent] : root).ToWorld( local );
			go.LocalTransform = local;
		}
		_primed = true;

		if ( Debug && Time.Now >= _nextDebug ) {
			_nextDebug = Time.Now + 1;
			var matched = _bones.Count( b => _overlays.Tracks.ContainsKey( b.Bone.Name ) );
			var moved = 0f;
			for ( var i = 0; i < _bones.Count; i++ )
				moved = MathF.Max( moved, _renderer.TryGetBoneTransform( _bones[i].Bone, out var final ) ? final.Position.Distance( _finalWorld[i].Position ) : -1 );
			Log.Info( $"fp pose: {_bones.Count} bones, overlay tracks {_overlays.Tracks.Count} ({matched} match bone names: {string.Join( ", ", _overlays.Tracks.Keys.Take( 4 ) )}), moving {(_moving is null ? "none" : _moving.Tracks.Count + " tracks")}, " +
				$"sway pos {sway.Position} turn {sway.Turning} push {sway.PushBack:0.000}, interp {_interpolationIndex}/{_interpolationCount}, drawn-vs-ours max gap {moved:0.00}" );
		}
	}

	Transform _drawnRenderer;
	Transform _drawnRoot;

	// A model marker on this frame's drawn pose, or null before the first pose.
	// Position follows the spread weapon and rotation the renderer's axes, like the renderer's own markers.
	// The renderer's markers lag a frame behind the drawn weapon; this doesn't.
	public Transform? Marker( string name ) {
		if ( !_primed || Failed ) return null;
		var attachment = _renderer.Model?.Attachments?.Get( name );
		if ( attachment?.Bone is null ) return null;
		var slot = _bones.FindIndex( b => b.Bone.Index == attachment.Bone.Index );
		if ( slot < 0 ) return null;

		var model = _drawnRoot.ToLocal( _finalWorld[slot].ToWorld( attachment.LocalTransform ) );
		return new Transform( _drawnRenderer.PointToWorld( model.Position ), _drawnRenderer.Rotation * model.Rotation );
	}

	// A looping overlay at full strength, blended between its 30 Hz frames.
	static Transform OverlaySmooth( Transform local, AnimationClip clip, string bone, float frame ) {
		if ( !clip.Tracks.TryGetValue( bone, out var track ) ) return local;
		var a = (int)frame % clip.FrameCount;
		var b = (a + 1) % clip.FrameCount;
		var t = frame - MathF.Floor( frame );
		return new Transform(
			local.Position + track.Position[a].LerpTo( track.Position[b], t ),
			local.Rotation * Rotation.Slerp( track.Rotation[a], track.Rotation[b], t ),
			local.Scale * track.Scale[a].LerpTo( track.Scale[b], t ) );
	}
}

static class ClipOverlay {
	// Rotation post-multiplied, position added, scale multiplied. A scale below 1 applies that share of each.
	internal static Transform Apply( Transform local, AnimationClip clip, string bone, int frame, float scale = 1 ) {
		if ( frame < 0 || frame >= clip.FrameCount || !clip.Tracks.TryGetValue( bone, out var track ) ) return local;
		var rotation = scale >= 1 ? track.Rotation[frame] : Rotation.Slerp( Rotation.Identity, track.Rotation[frame], scale );
		return new Transform(
			local.Position + track.Position[frame] * scale,
			local.Rotation * rotation,
			local.Scale * (track.Scale[frame] * scale + (1 - scale)) );
	}
}
