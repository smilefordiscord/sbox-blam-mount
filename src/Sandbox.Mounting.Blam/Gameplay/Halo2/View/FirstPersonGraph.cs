using Halo2.Data;
using System;

namespace Halo2.Combat;

// A weapon's first person animation graph from the mount's mount://halo2/anims/<weapon>_fp.json.
// Weapon states play labels; a label names the head of a chain of variants, each with a cumulative weight.
public sealed class FirstPersonGraph {
	sealed class IndexFile {
		public string ShieldMaterial { get; set; }
		public string OvershieldMaterial { get; set; }
		public List<Animation> Animations { get; set; } = new();
		public List<LabelEntry> Labels { get; set; } = new();
		public List<BlendScreen> BlendScreens { get; set; } = new();
		public List<NodeEntry> Nodes { get; set; } = new();
	}

	sealed class NodeEntry {
		public string Name { get; set; }
		public int Parent { get; set; } = -1;
		public string[] Flags { get; set; } = Array.Empty<string>();

		public bool Has( string flag ) => Flags?.Contains( flag ) ?? false;
	}

	sealed class LabelEntry {
		public string WeaponClass { get; set; }
		public string Label { get; set; }
		public string Animation { get; set; }
	}

	public sealed class Animation : AnimationEntry {
		public string Type { get; set; }
		public int LoopFrame { get; set; }
		public string NextAnimation { get; set; }
		public float Weight { get; set; }
		public int BlendScreen { get; set; } = -1;
		public string Resource { get; set; }
		public string[] PlaybackFlags { get; set; } = Array.Empty<string>();

		// A state playing this fades the first person overlays out.
		public bool DisablesOverlays => PlaybackFlags?.Contains( "disable weapon aim/first person" ) ?? false;
	}

	// An aiming grid: its frames step through these angles in radians, a row per pitch and a column per yaw.
	public sealed class BlendScreen {
		public float RightYawPerFrame { get; set; }
		public float LeftYawPerFrame { get; set; }
		public int RightFrames { get; set; }
		public int LeftFrames { get; set; }
		public float DownPitchPerFrame { get; set; }
		public float UpPitchPerFrame { get; set; }
		public int DownFrames { get; set; }
		public int UpFrames { get; set; }
	}

	public string ShieldMaterial { get; private init; }
	public string OvershieldMaterial { get; private init; }

	// The weapon model's root node and the hand it moves to when the weapon is the left hand's, or null.
	// Halo 2 takes the root's parent hand and moves it to the first node flagged as the other hand.
	public (string Gun, string Hand)? OtherHandGun { get; private set; }

	// The nodes flagged as left arm members, which the hands take from the left hand's state while dual wielding.
	public HashSet<string> LeftArmNodes { get; } = new();

	readonly Dictionary<string, Animation> _animations = new();
	readonly Dictionary<string, string> _labels = new();
	readonly Dictionary<string, string> _dualLabels = new();
	readonly List<BlendScreen> _screens = new();
	readonly Dictionary<string, AnimationClip> _clips = new();

	static readonly Dictionary<string, FirstPersonGraph> Cache = new();
	static int _cacheGeneration;

	// The graph next to the mount's "<weapon tag>_fp.vmdl", or null if the mount has none.
	public static FirstPersonGraph For( string weaponPath ) {
		var stem = Combat.HaloEffects.WithoutExtension( weaponPath );
		if ( stem is null ) return null;
		if ( _cacheGeneration != Data.HaloDataStore.Generation ) {
			_cacheGeneration = Data.HaloDataStore.Generation;
			Cache.Clear();
		}
		if ( Cache.TryGetValue( stem, out var cached ) ) return cached;

		var file = Data.HaloDataStore.ReadMount( $"anims/{stem}_fp.json" ) is { Length: > 0 } json ? Json.Deserialize<IndexFile>( json ) : null;
		var graph = file is null ? null : new FirstPersonGraph {
			ShieldMaterial = file.ShieldMaterial,
			OvershieldMaterial = file.OvershieldMaterial,
		};
		if ( graph is not null ) {
			foreach ( var a in file.Animations ) graph._animations.TryAdd( a.Name, a );
			// Single wielded weapons use class "any", dual wielded ones "dual".
			foreach ( var l in file.Labels.Where( x => x.WeaponClass == "any" ) ) graph._labels.TryAdd( l.Label, l.Animation );
			foreach ( var l in file.Labels.Where( x => x.WeaponClass == "dual" ) ) graph._dualLabels.TryAdd( l.Label, l.Animation );
			graph._screens.AddRange( file.BlendScreens );
			graph.OtherHandGun = OtherHand( file.Nodes );
			foreach ( var n in file.Nodes.Where( x => x.Has( "left arm member" ) ) ) graph.LeftArmNodes.Add( n.Name );
		}

		Cache[stem] = graph;
		return graph;
	}

	static (string, string)? OtherHand( List<NodeEntry> nodes ) {
		var gun = nodes.FirstOrDefault( x => x.Has( "secondary model" ) && x.Has( "local root" ) );
		if ( gun is null || gun.Parent < 0 || gun.Parent >= nodes.Count ) return null;
		var parent = nodes[gun.Parent];
		var other = parent.Has( "right hand" ) ? "left hand" : parent.Has( "left hand" ) ? "right hand" : null;
		var hand = other is null ? null : nodes.FirstOrDefault( x => x.Has( other ) );
		return hand is null ? null : (gun.Name, hand.Name);
	}

	// The label's chain head; dual wielded, the "dual" class's, else the single wielded one.
	public Animation Head( string label, bool dual = false ) {
		if ( label is null ) return null;
		if ( dual && _dualLabels.TryGetValue( label, out var dualName ) ) return Find( dualName );
		return _labels.TryGetValue( label, out var name ) ? Find( name ) : null;
	}

	// What a first person state plays for a label: its head, or "idle" when no class has the label, as Halo 2 sets them.
	public Animation Play( string label, bool dual ) => Head( label, dual ) ?? Head( "idle", dual );

	// Every sound its animations play, for preloading.
	public IEnumerable<string> Sounds => _animations.Values.SelectMany( x => x.SoundEvents ).Select( x => x.Sound );

	Animation Find( string name ) => name is not null && _animations.TryGetValue( name, out var a ) ? a : null;

	// Halo rolls once per play when the head's weight is under 1, and takes the first variant whose weight reaches the roll.
	public Animation PickVariant( Animation head ) => PickVariant( head, Find );

	public static Animation PickVariant( Animation head, Func<string, Animation> find ) {
		if ( head is null || head.Weight >= 1 ) return head;
		var roll = Game.Random.Float( 0, 1 );
		for ( var a = head; a is not null; a = find( a.NextAnimation ) )
			if ( a.Weight >= roll ) return a;
		return head;
	}

	public BlendScreen Screen( Animation animation ) => animation is { BlendScreen: >= 0 } a && a.BlendScreen < _screens.Count ? _screens[a.BlendScreen] : null;

	// A label's head animation as frames of node transforms; overlays are the only ones posed from these.
	public AnimationClip Clip( string label, bool dual ) {
		var head = Head( label, dual );
		if ( head?.Resource is null ) return null;
		if ( _clips.TryGetValue( head.Name, out var cached ) ) return cached;
		var clip = AnimationClip.Load( head.Resource );
		_clips[head.Name] = clip;
		return clip;
	}
}

// One animation's frames from the mount's per-animation JSON, as tracks by node name.
// An overlay lists only what it sets: a missing rotation is identity and a missing position zero.
// A replacement lists only the components it sets, as absolute values.
public sealed class AnimationClip {
	sealed class FileClip {
		public string Type { get; set; }
		public List<FileNode> Nodes { get; set; } = new();
		public List<FileFrame> Frames { get; set; } = new();
		public List<float[]> FrameInfo { get; set; }
	}

	sealed class FileNode {
		public string Name { get; set; }
	}

	sealed class FileFrame {
		public List<FileKey> Nodes { get; set; } = new();
	}

	sealed class FileKey {
		public int Node { get; set; }
		public float[] Rotation { get; set; }
		public float[] Position { get; set; }
		public float? Scale { get; set; }
	}

	// A replacement leaves the components it doesn't set null.
	public sealed class Track {
		public Rotation[] Rotation;
		public Vector3[] Position;
		public float[] Scale;
	}

	public int FrameCount { get; private init; }
	public bool Replacement { get; private init; }
	public bool Overlay { get; private init; }

	// The root motion of each frame: movement in the object's axes and turn in radians, left positive; empty without.
	public (Vector3 Move, float Yaw)[] Motion { get; private init; } = Array.Empty<(Vector3, float)>();
	public Dictionary<string, Track> Tracks { get; } = new( StringComparer.OrdinalIgnoreCase );

	// resource is the mount path the graph's index gives.
	public static AnimationClip Load( string resource ) {
		var prefix = $"mount://{Data.HaloMountCheck.Ident}/";
		var path = resource.StartsWith( prefix ) ? resource[prefix.Length..] : resource;
		var json = Data.HaloDataStore.ReadMount( path );
		if ( string.IsNullOrEmpty( json ) ) return null;

		var file = Json.Deserialize<FileClip>( json );
		if ( file is null || file.Frames.Count == 0 ) return null;

		var frames = file.Frames.Count;
		var clip = new AnimationClip {
			FrameCount = frames,
			Replacement = file.Type == "replacement",
			Overlay = file.Type == "overlay",
			Motion = file.FrameInfo?.Select( x => x is { Length: 4 } m ? (new Vector3( m[0], m[1], m[2] ), m[3]) : default ).ToArray() ?? Array.Empty<(Vector3, float)>(),
		};
		for ( var f = 0; f < frames; f++ ) {
			foreach ( var k in file.Frames[f].Nodes ) {
				if ( k.Node < 0 || k.Node >= file.Nodes.Count ) continue;
				var name = file.Nodes[k.Node].Name;
				if ( !clip.Tracks.TryGetValue( name, out var track ) ) {
					track = clip.Replacement ? new Track {
						Rotation = k.Rotation is not null ? new Rotation[frames] : null,
						Position = k.Position is not null ? new Vector3[frames] : null,
						Scale = k.Scale is not null ? new float[frames] : null,
					} : new Track { Rotation = new Rotation[frames], Position = new Vector3[frames] };
					if ( !clip.Replacement ) Array.Fill( track.Rotation, Rotation.Identity );
					clip.Tracks[name] = track;
				}
				if ( track.Rotation is not null && k.Rotation is { Length: 4 } r ) track.Rotation[f] = new Rotation( r[0], r[1], r[2], r[3] );
				if ( track.Position is not null && k.Position is { Length: 3 } p ) track.Position[f] = new Vector3( p[0], p[1], p[2] );
				if ( track.Scale is not null && k.Scale is { } s ) track.Scale[f] = s;
			}
		}
		return clip;
	}

	// A replacement's node at a frame blended toward the next: the components it sets replace the pose's.
	public Transform Replace( Transform local, string node, int frame, int next, float t ) {
		if ( !Tracks.TryGetValue( node, out var track ) ) return local;
		frame = Math.Clamp( frame, 0, FrameCount - 1 );
		next = Math.Clamp( next, 0, FrameCount - 1 );
		return new Transform(
			track.Position is { } p ? p[frame].LerpTo( p[next], t ) : local.Position,
			track.Rotation is { } r ? Rotation.Slerp( r[frame], r[next], t ) : local.Rotation,
			track.Scale is { } s ? Vector3.One * MathX.Lerp( s[frame], s[next], t ) : local.Scale );
	}

	// The node's delta at a frame blended toward the next: rotation slerped, position lerped.
	public (Rotation Rotation, Vector3 Position) Delta( string node, int frame, int next, float t ) {
		if ( !Tracks.TryGetValue( node, out var track ) ) return (Rotation.Identity, Vector3.Zero);
		frame = Math.Clamp( frame, 0, FrameCount - 1 );
		next = Math.Clamp( next, 0, FrameCount - 1 );
		return (Rotation.Slerp( track.Rotation[frame], track.Rotation[next], t ), track.Position[frame].LerpTo( track.Position[next], t ));
	}
}
