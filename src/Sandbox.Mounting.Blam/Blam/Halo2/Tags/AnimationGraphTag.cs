using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Blam.Halo1.Tags;

namespace Blam.Halo2.Tags;

// Inside the namespace so these win over s&box's global vector types when this compiles into the mount.
using Quaternion = System.Numerics.Quaternion;
using Vector3 = System.Numerics.Vector3;

/// <summary>
/// Where a graph's label points: an animation of the graph itself (<see cref="Graph"/> -1) or of the graph its
/// inheritance list entry <see cref="Graph"/> names.
/// </summary>
public readonly record struct AnimationReference( int Graph, int Animation )
{
	public bool IsNone => Animation < 0;
	public bool IsInherited => Graph >= 0;
}

/// <summary>
/// A node of an animation graph. Graph nodes find a render model's nodes by name.
/// ModelFlags are its model flags (FlagNames from bit 0), which mark a graph's hands and left arm.
/// </summary>
public sealed record AnimationGraphNode( int Index, string Name, int Sibling, int Child, int Parent, int ModelFlags )
{
	public static readonly string[] FlagNames = ["primary model", "secondary model", "local root", "left hand", "right hand", "left arm member"];
}

/// <summary>
/// An inherited graph (dervish ← elite): its animations play on this graph's nodes through <see cref="NodeMap"/>
/// (the local node each of its nodes drives, -1 for none).
/// </summary>
/// <remarks>
/// The field the community's docs call "root z offset" is a scale: about 1 in 94 of MCC's 107 entries, 0.9466 in the other 13
/// (flag 1: "inherit root translation scale only"). Applied to the root node's height (unverified against MCC).
/// </remarks>
public sealed record InheritedGraph( TagEntry Graph, IReadOnlyList<int> NodeMap, float RootZScale, int Flags );

/// <summary>
/// A labelled animation: actions and overlays of a weapon type, under a weapon class, under a mode. Its path is
/// <c>mode:class:type:label</c> (<c>combat:rifle:any:move_front</c>).
/// </summary>
public sealed record AnimationLabel( string Mode, string WeaponClass, string WeaponType, string Label, bool Overlay, AnimationReference Animation )
{
	public string Path => $"{Mode}:{WeaponClass}:{WeaponType}:{Label}";
}

/// <summary>
/// An animation a function of the object plays (a machine's position, a light's flicker): the graph's object
/// overlays.
/// </summary>
/// <param name="FunctionControls">0 the function picks the frame, 1 it scales the animation.</param>
public sealed record ObjectOverlay( string Label, AnimationReference Animation, int FunctionControls, string Function );

/// <summary>
/// An aiming grid (a blend screen) as the CE mount's <see cref="AimingScreenBounds"/> describes it: yaw left positive,
/// pitch up positive, radians per frame and frame counts each way.
/// </summary>
public sealed record BlendScreen( string Label, AimingScreenBounds Bounds );

// A frame an animation marks for the game; Type indexes Types.
public readonly record struct AnimationFrameEvent( int Type, int Frame )
{
	// The event types in tag order: the primary key frame is a melee's hit and a reload's rounds going in.
	public static readonly string[] Types =
	[
		"primary keyframe", "secondary keyframe", "left foot", "right foot", "allow interruption", "transition a", "transition b",
		"transition c", "transition d", "both-feet shuffle", "body impact",
	];

	public const int PrimaryKeyframe = 0, SecondaryKeyframe = 1, LeftFoot = 2, RightFoot = 3, AllowInterruption = 4;

	public string TypeName => Type >= 0 && Type < Types.Length ? Types[Type] : $"type {Type}";
}

// A sound an animation starts at a frame: Sound indexes its graph's sound references, Marker is where it plays (empty for the object).
public readonly record struct AnimationSoundEvent( int Sound, int Frame, string Marker );

// An effect an animation starts at a frame: Effect indexes its graph's effect references.
public readonly record struct AnimationEffectEvent( int Effect, int Frame );

// A sound or effect a graph's animations play, with the flags limiting where (allow on player, first person only...).
public sealed record AnimationEventReference( TagEntry Tag, int Flags )
{
	public static readonly string[] FlagNames = ["allow on player", "left arm only", "right arm only", "first person only", "forward only", "reverse only"];
}

/// <summary>
/// One animation of a Halo 2 animation graph: its header and its data, decoded on demand.
/// </summary>
/// <remarks>
/// <para>0x60 bytes: name sid 0x0, type 0x10 (0 base, 1 overlay, 2 replacement), frame info type 0x11 (root motion,
/// as Halo CE's), blend screen 0x12 (i8), node count 0x13, frame count 0x14, internal flags 0x16 (2 world relative),
/// production flags 0x17, playback flags 0x18, weight 0x1C, loop frame 0x20, parent 0x22 and next animation 0x24,
/// data 0x28 (i32 size, tag address: all of it in the tag data), data sizes 0x30: u8 static node flags, u8 animated
/// node flags, i16 movement, i16 pill offset, i16 static (default) data, i32 uncompressed data, i32 compressed data
/// (they add up to the data's size in every animation of every map), frame events 0x40 (4: i16 type, i16 frame), sound
/// events 0x48 (8: i16 sound, i16 frame, marker sid), effect events 0x50 (4: i16 effect, i16 frame).</para>
///
/// <para>The data is <c>static block | uncompressed block | compressed (animated) block | static node flags | animated
/// node flags | movement | pill offset</c> (the uncompressed block is always empty). Each node flag set is three bit
/// fields (rotation, translation, scale) of the same size, a bit per node. A component of a node takes its one value from
/// the static block if its static bit is set, its value per frame from the animated block if its animated bit is set,
/// and is left at the model's default pose otherwise. Movement is the root motion per frame: dx dy, dx dy dyaw or
/// dx dy dz dyaw floats.</para>
///
/// <para>Rotations are stored as the render model stores its nodes' default rotations (parent relative, the usual
/// quaternion convention), so a node's static rotation equals its model default where the animation leaves it be.</para>
/// </remarks>
public sealed class GraphAnimation
{
	public static readonly string[] PlaybackFlagNames = ["disable interpolation in", "disable interpolation out", "disable mode ik",
		"disable weapon ik", "disable weapon aim/first person", "disable look screen", "disable transition adjustment"];

	public AnimationGraphTag Graph { get; init; }
	public int Index { get; init; }
	public string Name { get; init; }
	public AnimationType Type { get; init; }
	public AnimationFrameInfoType FrameInfoType { get; init; }
	public int BlendScreen { get; init; }
	public int NodeCount { get; init; }
	public int FrameCount { get; init; }
	public int InternalFlags { get; init; }
	public int PlaybackFlags { get; init; }
	public float Weight { get; init; }
	public int LoopFrame { get; init; }
	public int NextAnimation { get; init; }
	public IReadOnlyList<AnimationFrameEvent> FrameEvents { get; init; } = [];
	public IReadOnlyList<AnimationSoundEvent> SoundEvents { get; init; } = [];
	public IReadOnlyList<AnimationEffectEvent> EffectEvents { get; init; } = [];

	// The frame of the first event of a type (AnimationFrameEvent's constants), or -1 without one.
	public int EventFrame( int type ) => FrameEvents.FirstOrDefault( e => e.Type == type, new AnimationFrameEvent( type, -1 ) ).Frame;

	/// <summary>Halo 2 animations run at 30 frames per second.</summary>
	public float FrameRate => 30;

	public bool WorldRelative => (InternalFlags & 2) != 0;

	internal uint DataAddress { get; init; }
	internal int DataSize { get; init; }
	internal int StaticFlagsSize { get; init; }
	internal int AnimatedFlagsSize { get; init; }
	internal int MovementSize { get; init; }
	internal int StaticSize { get; init; }
	internal int UncompressedSize { get; init; }
	internal int CompressedSize { get; init; }

	AnimationData _data;

	/// <summary>Whether the animation has data at all (some graphs list names only).</summary>
	public bool HasData => DataSize > 0 && FrameCount > 0 && NodeCount > 0;

	/// <summary>
	/// The animation's decoded tracks (cached).
	/// </summary>
	/// <exception cref="InvalidDataException">The data doesn't decode.</exception>
	public AnimationData Decode()
	{
		if ( _data is not null )
			return _data;

		if ( !HasData )
			throw new InvalidDataException( $"{Name} has no data" );

		var data = Graph.Tag.Cache.TagData;
		if ( !data.Contains( DataAddress, DataSize ) )
			throw new InvalidDataException( $"{Name}: data 0x{DataAddress:X8} (+{DataSize}) is outside the tag data" );

		var blob = data.Slice( DataAddress, DataSize );
		var sum = StaticFlagsSize + AnimatedFlagsSize + MovementSize + StaticSize + UncompressedSize + CompressedSize;
		if ( sum > DataSize )
			throw new InvalidDataException( $"{Name}: data sizes add up to {sum}, more than its {DataSize} bytes" );

		var flagsAt = StaticSize + UncompressedSize + CompressedSize;
		var staticFlags = NodeFlags( blob.Slice( flagsAt, StaticFlagsSize ) );
		var animatedFlags = NodeFlags( blob.Slice( flagsAt + StaticFlagsSize, AnimatedFlagsSize ) );

		var still = StaticSize > 0 ? AnimationCodecs.Decode( blob[..StaticSize], 1 ) : null;
		var moving = CompressedSize > 0 ? AnimationCodecs.Decode( blob.Slice( StaticSize + UncompressedSize, CompressedSize ), FrameCount ) : null;

		var result = new AnimationData( this, staticFlags, animatedFlags, still, moving );

		var movementAt = flagsAt + StaticFlagsSize + AnimatedFlagsSize;
		result.ReadMovement( blob.Slice( movementAt, MovementSize ) );
		return _data = result;
	}

	/// <summary>
	/// The three bit fields (rotation, translation, scale) of a node flag set, as a bool per node.
	/// </summary>
	bool[][] NodeFlags( ReadOnlySpan<byte> bytes )
	{
		var size = bytes.Length / 3;
		var result = new bool[3][];
		for ( var c = 0; c < 3; c++ )
		{
			result[c] = new bool[NodeCount];
			for ( var n = 0; n < NodeCount && n / 8 < size; n++ )
				result[c][n] = (bytes[c * size + n / 8] >> (n % 8) & 1) != 0;
		}

		return result;
	}

	public override string ToString() => $"{Graph.Tag.Path}:{Name}";
}

/// <summary>
/// A decoded animation: which components of which nodes it sets, and their values per frame.
/// </summary>
public sealed class AnimationData
{
	public GraphAnimation Animation { get; }

	readonly bool[][] _static;
	readonly bool[][] _animated;

	// Per node, the track of each component: [frame] (animated) or [0] (static), null when it isn't set.
	readonly Quaternion[][] _rotations;
	readonly Vector3[][] _translations;
	readonly float[][] _scales;

	/// <summary>The codec of the animated block, or 0 when nothing moves.</summary>
	public int AnimatedCodec { get; }

	/// <summary>The codec of the static block, or 0 when it has none.</summary>
	public int StaticCodec { get; }

	/// <summary>Root motion per frame (empty for none).</summary>
	public AnimationFrameInfo[] Movement { get; private set; } = [];

	internal AnimationData( GraphAnimation animation, bool[][] staticFlags, bool[][] animatedFlags, CodecTracks still, CodecTracks moving )
	{
		Animation = animation;
		_static = staticFlags;
		_animated = animatedFlags;
		StaticCodec = still?.Codec ?? 0;
		AnimatedCodec = moving?.Codec ?? 0;

		var nodes = animation.NodeCount;
		_rotations = new Quaternion[nodes][];
		_translations = new Vector3[nodes][];
		_scales = new float[nodes][];

		Assign( _rotations, 0, still?.Rotations, staticFlags, "static rotations", r => r );
		Assign( _translations, 1, still?.Translations, staticFlags, "static translations", t => t );
		Assign( _scales, 2, still?.Scales, staticFlags, "static scales", s => s );
		Assign( _rotations, 0, moving?.Rotations, animatedFlags, "animated rotations", r => r );
		Assign( _translations, 1, moving?.Translations, animatedFlags, "animated translations", t => t );
		Assign( _scales, 2, moving?.Scales, animatedFlags, "animated scales", s => s );

		for ( var n = 0; n < nodes; n++ )
		{
			if ( _rotations[n] is { } track )
			{
				for ( var f = 0; f < track.Length; f++ )
				{
					var length = track[f].Length();
					if ( length < 0.5f || length > 1.5f )
						throw new InvalidDataException( $"{animation.Name}: node {n} frame {f} rotation of length {length}" );
					track[f] = Quaternion.Multiply( track[f], 1 / length );
				}
			}
		}
	}

	/// <summary>
	/// Gives the nodes whose flag is set the codec's tracks in node order. The codec's track counts must match.
	/// </summary>
	void Assign<T>( T[][] target, int component, T[][] tracks, bool[][] flags, string what, Func<T[], T[]> copy )
	{
		var count = 0;
		for ( var n = 0; n < flags[component].Length; n++ )
		{
			if ( !flags[component][n] )
				continue;

			if ( tracks is null || count >= tracks.Length )
				throw new InvalidDataException( $"{Animation.Name}: {what}: more flagged nodes than the codec has tracks ({tracks?.Length ?? 0})" );

			target[n] = copy( tracks[count++] );
		}

		if ( tracks is not null && count != tracks.Length )
			throw new InvalidDataException( $"{Animation.Name}: {what}: {tracks.Length} tracks for {count} flagged nodes" );
	}

	internal void ReadMovement( ReadOnlySpan<byte> bytes )
	{
		var per = Animation.FrameInfoType switch
		{
			AnimationFrameInfoType.DxDy => 2,
			AnimationFrameInfoType.DxDyDyaw => 3,
			AnimationFrameInfoType.DxDyDzDyaw => 4,
			_ => 0,
		};

		if ( per == 0 || bytes.Length < per * 4 * Animation.FrameCount )
			return;

		var frames = new AnimationFrameInfo[Animation.FrameCount];
		Span<float> v = stackalloc float[4];
		for ( var f = 0; f < frames.Length; f++ )
		{
			for ( var i = 0; i < per; i++ )
				v[i] = System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian( bytes[((f * per + i) * 4)..] );

			frames[f] = per switch
			{
				2 => new AnimationFrameInfo( new Vector3( v[0], v[1], 0 ), 0 ),
				3 => new AnimationFrameInfo( new Vector3( v[0], v[1], 0 ), v[2] ),
				_ => new AnimationFrameInfo( new Vector3( v[0], v[1], v[2] ), v[3] ),
			};
		}

		Movement = frames;
	}

	public bool IsStatic( int component, int node ) => node < _static[component].Length && _static[component][node];
	public bool IsAnimated( int component, int node ) => node < _animated[component].Length && _animated[component][node];

	/// <summary>Whether the animation sets the node's rotation (statically or per frame).</summary>
	public bool SetsRotation( int node ) => node < _rotations.Length && _rotations[node] is not null;
	public bool SetsTranslation( int node ) => node < _translations.Length && _translations[node] is not null;
	public bool SetsScale( int node ) => node < _scales.Length && _scales[node] is not null;

	/// <summary>Whether the node's rotation changes over the animation.</summary>
	public bool AnimatesRotation( int node ) => IsAnimated( 0, node );
	public bool AnimatesTranslation( int node ) => IsAnimated( 1, node );
	public bool AnimatesScale( int node ) => IsAnimated( 2, node );

	public Quaternion? Rotation( int node, int frame ) => _rotations[node] is { } t ? t[Math.Min( frame, t.Length - 1 )] : null;
	public Vector3? Translation( int node, int frame ) => _translations[node] is { } t ? t[Math.Min( frame, t.Length - 1 )] : null;
	public float? Scale( int node, int frame ) => _scales[node] is { } t ? t[Math.Min( frame, t.Length - 1 )] : null;

	/// <summary>
	/// A frame as parent relative node poses; components the animation doesn't set come from <paramref name="defaults"/>
	/// (the graph nodes' default pose, from the render model), or identity.
	/// </summary>
	public NodePose[] Frame( int frame, IReadOnlyList<NodePose> defaults = null )
	{
		var poses = new NodePose[Animation.NodeCount];
		for ( var n = 0; n < poses.Length; n++ )
		{
			var d = defaults is not null && n < defaults.Count ? defaults[n] : NodePose.Identity;
			poses[n] = new NodePose( Rotation( n, frame ) ?? d.Rotation, Translation( n, frame ) ?? d.Translation, Scale( n, frame ) ?? d.Scale );
		}

		return poses;
	}
}

/// <summary>
/// Reader for Halo 2's <c>jmad</c> (model_animation_graph) tags.
/// </summary>
/// <remarks>
/// Layout (MCC classic; field names from the community's plugin documentation, checked against the maps): parent graph
/// 0x0, inheritance flags 0x8, private flags 0x9, codec pack 0xA, skeleton nodes 0xC (0x20: name sid, sibling 0x4,
/// child 0x6, parent 0x8, model flags 0xA, joint flags 0xB, base vector 0xC, vector range 0x18, z 0x1C), sound
/// references 0x14 (0xC: sound, flags 0x8), effect references 0x1C (0xC: effect, flags 0x8), blend screens 0x24
/// (0x1C), animations 0x2C (0x60, see <see cref="GraphAnimation"/>), modes 0x34 (0x14: label, weapon classes 0x4 (0x14: label, weapon types 0x4 (0x34:
/// label, actions 0x4 and overlays 0xC (8: label, i16 graph, i16 animation), death and damage 0x14, transitions
/// 0x1C))), vehicle suspension 0x3C (0x28: label, i16 graph 0x4, i16 animation 0x6...), object overlays 0x44 (0x14:
/// label, graph 0x4, animation 0x6, function controls 0xA, function sid 0xC), inheritance list 0x4C (0x20: graph ref,
/// node map 0x8 (i16 local node per inherited node), node map flags 0x10, root z scale 0x18, flags 0x1C), weapon
/// list 0x54, arm node masks 0x5C/0x7C, additional node data 0xA4 (empty in the maps: default poses come from the
/// render model).
/// </remarks>
public sealed class AnimationGraphTag
{
	public static readonly TagClass Class = new( "jmad" );

	public TagEntry Tag { get; }
	public TagEntry ParentGraph { get; }
	public int CodecPack { get; }
	public IReadOnlyList<AnimationGraphNode> Nodes { get; }
	public IReadOnlyList<BlendScreen> BlendScreens { get; }
	public IReadOnlyList<GraphAnimation> Animations { get; }
	public IReadOnlyList<AnimationEventReference> SoundReferences { get; }
	public IReadOnlyList<AnimationEventReference> EffectReferences { get; }
	public IReadOnlyList<AnimationLabel> Labels { get; }
	public IReadOnlyList<ObjectOverlay> ObjectOverlays { get; }
	public IReadOnlyList<(string Label, AnimationReference Animation)> VehicleSuspension { get; }
	public IReadOnlyList<InheritedGraph> Inheritance { get; }

	// The nodes of the left and right arms, as graph node indices.
	public IReadOnlyList<int> LeftArmNodes { get; }
	public IReadOnlyList<int> RightArmNodes { get; }

	public AnimationGraphTag( TagEntry tag )
	{
		if ( tag.Class != Class )
			throw new ArgumentException( $"{tag} is not a jmad tag", nameof( tag ) );

		Tag = tag;
		var cache = tag.Cache;
		var data = cache.TagData;
		var a = tag.Address;

		ParentGraph = cache.GetTagReference( a );
		CodecPack = data.I16( a + 0xA );

		var index = 0;
		Nodes = data.Elements( a + 0xC, 0x20 ).Select( n => new AnimationGraphNode( index++, cache.GetString( data.U32( n ) ),
			data.I16( n + 4 ), data.I16( n + 6 ), data.I16( n + 8 ), data.U8( n + 0xA ) ) ).ToList();

		// 256 node bits each, 8 u32 words from the lowest node up.
		List<int> NodeMask( uint at ) => Enumerable.Range( 0, 256 ).Where( n => (data.U32( at + (uint)(n / 32 * 4) ) & (1u << (n % 32))) != 0 ).ToList();
		LeftArmNodes = NodeMask( a + 0x5C );
		RightArmNodes = NodeMask( a + 0x7C );

		SoundReferences = data.Elements( a + 0x14, 0xC ).Select( r => new AnimationEventReference( cache.GetTagReference( r ), data.U16( r + 8 ) ) ).ToList();
		EffectReferences = data.Elements( a + 0x1C, 0xC ).Select( r => new AnimationEventReference( cache.GetTagReference( r ), data.U16( r + 8 ) ) ).ToList();

		BlendScreens = data.Elements( a + 0x24, 0x1C ).Select( s =>
		{
			// Stored right yaw/left yaw per frame (radians) and counts, then down/up pitch; CE's bounds are
			// negative (right) and positive (left) yaw, negative (down) and positive (up) pitch.
			var bounds = new AimingScreenBounds(
				data.F32( s + 0x4 ), data.F32( s + 0x8 ), data.I16( s + 0xC ), data.I16( s + 0xE ),
				data.F32( s + 0x10 ), data.F32( s + 0x14 ), data.I16( s + 0x18 ), data.I16( s + 0x1A ) );
			return new BlendScreen( cache.GetString( data.U32( s ) ), bounds );
		} ).ToList();

		var animations = new List<GraphAnimation>();
		foreach ( var e in data.Elements( a + 0x2C, 0x60 ) )
		{
			animations.Add( new GraphAnimation
			{
				Graph = this,
				Index = animations.Count,
				Name = cache.GetString( data.U32( e ) ),
				Type = (AnimationType)data.U8( e + 0x10 ),
				FrameInfoType = (AnimationFrameInfoType)data.U8( e + 0x11 ),
				BlendScreen = (sbyte)data.U8( e + 0x12 ),
				NodeCount = data.U8( e + 0x13 ),
				FrameCount = data.I16( e + 0x14 ),
				InternalFlags = data.U8( e + 0x16 ),
				PlaybackFlags = data.U16( e + 0x18 ),
				Weight = data.F32( e + 0x1C ),
				LoopFrame = data.I16( e + 0x20 ),
				NextAnimation = data.I16( e + 0x24 ),
				DataSize = data.I32( e + 0x28 ),
				DataAddress = data.U32( e + 0x2C ),
				StaticFlagsSize = data.U8( e + 0x30 ),
				AnimatedFlagsSize = data.U8( e + 0x31 ),
				MovementSize = data.U16( e + 0x32 ),
				StaticSize = data.U16( e + 0x36 ),
				UncompressedSize = data.I32( e + 0x38 ),
				CompressedSize = data.I32( e + 0x3C ),
				FrameEvents = data.Elements( e + 0x40, 4 ).Select( f => new AnimationFrameEvent( data.I16( f ), data.I16( f + 2 ) ) ).ToList(),
				SoundEvents = data.Elements( e + 0x48, 8 ).Select( f => new AnimationSoundEvent( data.I16( f ), data.I16( f + 2 ), cache.GetString( data.U32( f + 4 ) ) ?? "" ) ).ToList(),
				EffectEvents = data.Elements( e + 0x50, 4 ).Select( f => new AnimationEffectEvent( data.I16( f ), data.I16( f + 2 ) ) ).ToList(),
			} );
		}
		Animations = animations;

		AnimationReference Reference( uint at ) => new( data.I16( at ), data.I16( at + 2 ) );

		var labels = new List<AnimationLabel>();
		foreach ( var mode in data.Elements( a + 0x34, 0x14 ) )
		{
			var modeName = cache.GetString( data.U32( mode ) );
			foreach ( var weaponClass in data.Elements( mode + 0x4, 0x14 ) )
			{
				var className = cache.GetString( data.U32( weaponClass ) );
				foreach ( var weaponType in data.Elements( weaponClass + 0x4, 0x34 ) )
				{
					var typeName = cache.GetString( data.U32( weaponType ) );
					foreach ( var action in data.Elements( weaponType + 0x4, 8 ) )
						labels.Add( new AnimationLabel( modeName, className, typeName, cache.GetString( data.U32( action ) ), false, Reference( action + 4 ) ) );
					foreach ( var overlay in data.Elements( weaponType + 0xC, 8 ) )
						labels.Add( new AnimationLabel( modeName, className, typeName, cache.GetString( data.U32( overlay ) ), true, Reference( overlay + 4 ) ) );
				}
			}
		}
		Labels = labels;

		VehicleSuspension = data.Elements( a + 0x3C, 0x28 ).Select( s => (cache.GetString( data.U32( s ) ), Reference( s + 4 )) ).ToList();

		ObjectOverlays = data.Elements( a + 0x44, 0x14 ).Select( o => new ObjectOverlay( cache.GetString( data.U32( o ) ), Reference( o + 4 ),
			data.U16( o + 0xA ), cache.GetString( data.U32( o + 0xC ) ) ) ).ToList();

		Inheritance = data.Elements( a + 0x4C, 0x20 ).Select( i => new InheritedGraph( cache.GetTagReference( i ),
			data.Elements( i + 0x8, 2 ).Select( m => (int)data.I16( m ) ).ToList(), data.F32( i + 0x18 ), data.I32( i + 0x1C ) ) ).ToList();
	}

	/// <summary>
	/// The graph an animation reference points into: this one, or an inherited graph (null if the entry is missing).
	/// </summary>
	public AnimationGraphTag GraphOf( AnimationReference reference )
	{
		if ( !reference.IsInherited )
			return this;

		if ( reference.Graph >= Inheritance.Count || Inheritance[reference.Graph].Graph is not { Address: not 0 } graph || graph.Class != Class )
			return null;

		return new AnimationGraphTag( graph );
	}
}
