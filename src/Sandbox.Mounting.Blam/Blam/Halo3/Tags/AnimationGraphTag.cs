using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Blam.Halo1.Tags;

namespace Blam.Halo3.Tags;

// Inside the namespace so these win over s&box's global vector types when this compiles into the mount.
using Quaternion = System.Numerics.Quaternion;
using Vector3 = System.Numerics.Vector3;

// Where a label points: an animation of the graph itself (Graph -1) or of the graph its inheritance list entry Graph names.
public readonly record struct AnimationReference( int Graph, int Animation )
{
	public bool IsNone => Animation < 0;
	public bool IsInherited => Graph >= 0;
}

// A node of an animation graph; graph nodes find a render model's nodes by name.
public sealed record AnimationGraphNode( int Index, string Name, int Sibling, int Child, int Parent, int ModelFlags, int JointFlags );

// An inherited graph whose animations play on this graph's nodes through NodeMap (the local node of each of its nodes, -1 for none). The parent graph is always the first entry.
// "Root z offset" is a scale, as in Halo 2: 1, or 0.9466 for the dervish's elite graph (flag 1: inherit root translation scale only).
public sealed record InheritedGraph( TagEntry Graph, IReadOnlyList<int> NodeMap, float RootZScale, int Flags );

// A labelled animation: actions and overlays of a weapon type, under a weapon class, under a mode (mode:class:type:label).
public sealed record AnimationLabel( string Mode, string WeaponClass, string WeaponType, string Label, bool Overlay, AnimationReference Animation )
{
	public string Path => $"{Mode}:{WeaponClass}:{WeaponType}:{Label}";
}

// An animation a function of the object plays (a machine's position, a light's flicker). FunctionControls: 0 the function picks the frame, 1 it scales the animation.
public sealed record ObjectOverlay( string Label, AnimationReference Animation, int FunctionControls, string Function );

public sealed record BlendScreen( string Label, AimingScreenBounds Bounds );

// One animation of a Halo 3 graph (0x88 bytes): its header and, in a model_animation_tag_resource member, its data.
// name 0x0, weight 0x4, loop frame 0x8, playback flags 0xA, blend screen 0xC (i8), node count 0xF, frame count 0x10, type 0x12, frame info type 0x13, production flags 0x14, internal flags 0x16 (2 world relative),
// node list checksum 0x18, production checksum 0x1C (= its resource member's), parent 0x24, next 0x26, resource group 0x28, group member 0x2A, frame events 0x2C, sound events 0x38, effect events 0x44, object-space parent nodes 0x5C (0x1C), foot tracking 0x68.
public sealed class GraphAnimation
{
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
	public int ResourceGroup { get; init; }
	public int ResourceMember { get; init; }
	public uint ProductionChecksum { get; init; }
	public int ObjectSpaceParentNodes { get; init; }

	// Halo 3 animations run at 30 frames per second.
	public float FrameRate => 30;

	public bool WorldRelative => (InternalFlags & 2) != 0;

	public bool HasData => FrameCount > 0 && NodeCount > 0 && ResourceGroup >= 0 && ResourceMember >= 0;

	AnimationData _data;

	// The decoded tracks (cached). Throws InvalidDataException when the data is missing or doesn't decode.
	public AnimationData Decode()
	{
		if ( _data is not null )
			return _data;

		if ( !HasData )
			throw new InvalidDataException( $"{Name} has no data" );

		// The member's node count can be larger than the graph's (020_base's Lord Hood cinematics: 53 in the graph, 116 in the data, with the checksum of the 116 node skeleton's animations).
		var member = Graph.Member( ResourceGroup, ResourceMember );
		if ( member.FrameCount != FrameCount || member.Checksum != ProductionChecksum || member.NodeCount < NodeCount )
			throw new InvalidDataException( $"{Name}: resource member (checksum {member.Checksum:X8}) has {member.FrameCount} frames of {member.NodeCount} nodes, the graph (checksum {ProductionChecksum:X8}) says {FrameCount} of {NodeCount}" );

		var blob = member.Data.Span;
		var sum = member.StaticFlagsSize + member.AnimatedFlagsSize + member.MovementSize + member.PillSize + member.StaticSize + member.UncompressedSize + member.CompressedSize;
		if ( sum > blob.Length )
			throw new InvalidDataException( $"{Name}: data sizes add up to {sum}, more than its {blob.Length} bytes" );

		var flagsAt = member.StaticSize + member.UncompressedSize + member.CompressedSize;
		var staticFlags = NodeFlags( blob.Slice( flagsAt, member.StaticFlagsSize ), member.NodeCount );
		var animatedFlags = NodeFlags( blob.Slice( flagsAt + member.StaticFlagsSize, member.AnimatedFlagsSize ), member.NodeCount );

		var still = member.StaticSize > 0 ? AnimationCodecs.Decode( blob[..member.StaticSize], 1 ) : null;
		var moving = member.CompressedSize > 0 ? AnimationCodecs.Decode( blob.Slice( member.StaticSize + member.UncompressedSize, member.CompressedSize ), FrameCount ) : null;

		var result = new AnimationData( this, member.NodeCount, staticFlags, animatedFlags, still, moving );
		result.ReadMovement( blob.Slice( flagsAt + member.StaticFlagsSize + member.AnimatedFlagsSize, member.MovementSize ), (AnimationFrameInfoType)member.MovementType );
		return _data = result;
	}

	// A node flag set's three bit fields (rotation, translation, scale) of equal size, a bit per node.
	static bool[][] NodeFlags( ReadOnlySpan<byte> bytes, int nodes )
	{
		var size = bytes.Length / 3;
		var result = new bool[3][];
		for ( var c = 0; c < 3; c++ )
		{
			result[c] = new bool[nodes];
			for ( var n = 0; n < nodes && n / 8 < size; n++ )
				result[c][n] = (bytes[c * size + n / 8] >> (n % 8) & 1) != 0;
		}

		return result;
	}

	public override string ToString() => $"{Graph.Tag.Path}:{Name}";
}

// One animation's data in a model_animation_tag_resource (0x30 per member): checksum 0x4, frame count 0x8, node count 0xA, movement type 0xB, then u8 static flags, u8 animated flags, i16 movement, i16 pill, u16 static, i32 uncompressed, i32 compressed sizes, and the data reference 0x1C.
// The data is static block | uncompressed (always empty) | animated block | static node flags | animated node flags | movement | pill, as in Halo 2.
public sealed record AnimationResourceMember( uint Checksum, int FrameCount, int NodeCount, int MovementType, int StaticFlagsSize, int AnimatedFlagsSize, int MovementSize, int PillSize,
	int StaticSize, int UncompressedSize, int CompressedSize, ReadOnlyMemory<byte> Data );

// A decoded animation: which components of which nodes it sets, and their values per frame.
public sealed class AnimationData
{
	public GraphAnimation Animation { get; }

	readonly bool[][] _static;
	readonly bool[][] _animated;

	// Per node, the track of each component: [frame] (animated) or [0] (static), null when it isn't set.
	readonly Quaternion[][] _rotations;
	readonly Vector3[][] _translations;
	readonly float[][] _scales;

	public int AnimatedCodec { get; }
	public int StaticCodec { get; }

	// Nodes the data covers (can be more than the graph animation's).
	public int NodeCount { get; }

	// Root motion per frame (empty for none).
	public AnimationFrameInfo[] Movement { get; private set; } = [];

	internal AnimationData( GraphAnimation animation, int nodes, bool[][] staticFlags, bool[][] animatedFlags, CodecTracks still, CodecTracks moving )
	{
		Animation = animation;
		NodeCount = nodes;
		_static = staticFlags;
		_animated = animatedFlags;
		StaticCodec = still?.Codec ?? 0;
		AnimatedCodec = moving?.Codec ?? 0;

		_rotations = new Quaternion[nodes][];
		_translations = new Vector3[nodes][];
		_scales = new float[nodes][];

		Assign( _rotations, 0, still?.Rotations, staticFlags, "static rotations" );
		Assign( _translations, 1, still?.Translations, staticFlags, "static translations" );
		Assign( _scales, 2, still?.Scales, staticFlags, "static scales" );
		Assign( _rotations, 0, moving?.Rotations, animatedFlags, "animated rotations" );
		Assign( _translations, 1, moving?.Translations, animatedFlags, "animated translations" );
		Assign( _scales, 2, moving?.Scales, animatedFlags, "animated scales" );

		for ( var n = 0; n < nodes; n++ )
		{
			if ( _rotations[n] is not { } track )
				continue;

			for ( var f = 0; f < track.Length; f++ )
			{
				var length = track[f].Length();
				if ( length < 0.5f || length > 1.5f )
					throw new InvalidDataException( $"{animation.Name}: node {n} frame {f} rotation of length {length}" );
				track[f] = Quaternion.Multiply( track[f], 1 / length );
			}
		}
	}

	// Gives the nodes whose flag is set the codec's tracks in node order; the counts must match.
	void Assign<T>( T[][] target, int component, T[][] tracks, bool[][] flags, string what )
	{
		var count = 0;
		for ( var n = 0; n < flags[component].Length; n++ )
		{
			if ( !flags[component][n] )
				continue;

			if ( tracks is null || count >= tracks.Length )
				throw new InvalidDataException( $"{Animation.Name}: {what}: more flagged nodes than the codec has tracks ({tracks?.Length ?? 0})" );

			target[n] = tracks[count++];
		}

		if ( tracks is not null && count != tracks.Length )
			throw new InvalidDataException( $"{Animation.Name}: {what}: {tracks.Length} tracks for {count} flagged nodes" );
	}

	internal void ReadMovement( ReadOnlySpan<byte> bytes, AnimationFrameInfoType type )
	{
		var per = type switch
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
				v[i] = BinaryPrimitives.ReadSingleLittleEndian( bytes[((f * per + i) * 4)..] );

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

	public bool SetsRotation( int node ) => node < _rotations.Length && _rotations[node] is not null;
	public bool SetsTranslation( int node ) => node < _translations.Length && _translations[node] is not null;
	public bool SetsScale( int node ) => node < _scales.Length && _scales[node] is not null;

	public bool AnimatesRotation( int node ) => IsAnimated( 0, node );
	public bool AnimatesTranslation( int node ) => IsAnimated( 1, node );
	public bool AnimatesScale( int node ) => IsAnimated( 2, node );

	public Quaternion? Rotation( int node, int frame ) => _rotations[node] is { } t ? t[Math.Min( frame, t.Length - 1 )] : null;
	public Vector3? Translation( int node, int frame ) => _translations[node] is { } t ? t[Math.Min( frame, t.Length - 1 )] : null;
	public float? Scale( int node, int frame ) => _scales[node] is { } t ? t[Math.Min( frame, t.Length - 1 )] : null;

	// A frame as parent relative node poses; components the animation doesn't set come from defaults (the render model's pose), or identity.
	public NodePose[] Frame( int frame, IReadOnlyList<NodePose> defaults = null )
	{
		var poses = new NodePose[NodeCount];
		for ( var n = 0; n < poses.Length; n++ )
		{
			var d = defaults is not null && n < defaults.Count ? defaults[n] : NodePose.Identity;
			poses[n] = new NodePose( Rotation( n, frame ) ?? d.Rotation, Translation( n, frame ) ?? d.Translation, Scale( n, frame ) ?? d.Scale );
		}

		return poses;
	}
}

// Reader for Halo 3's jmad (model_animation_graph) tags, 0x104 bytes: parent graph 0x0, inheritance flags 0x10, private flags 0x11, codec pack 0x12, skeleton nodes 0x14 (0x20: name, sibling 0x4, child 0x6, parent 0x8, model flags 0xA, joint flags 0xB),
// sounds 0x20, effects 0x2C, blend screens 0x38 (0x1C), foot markers 0x44, animations 0x50 (0x88, see GraphAnimation), modes 0x5C (0x28: label, weapon classes 0x4 (0x1C: label, weapon types 0x4 (0x34: label, actions 0x4, overlays 0x10 (8: label, i16 graph, i16 animation)))),
// vehicle suspension 0x68 (0x28), object overlays 0x74 (0x14: label, graph 0x4, animation 0x6, function controls 0xA, function 0xC), inheritance list 0x80 (0x30: graph, node map 0x10 (i16), node map flags 0x1C, root z scale 0x28, flags 0x2C),
// weapon list 0x8C, arm node masks 0x98/0xB8, additional node data 0xEC (empty: default poses come from the render model), tag resource groups 0xF8 (0xC: reference count, resource handle 0x4).
// Field names from Assembly's Halo3 jmad plugin (read as documentation), checked against the maps.
public sealed class AnimationGraphTag
{
	public static readonly TagClass Class = new( "jmad" );

	public TagEntry Tag { get; }
	public TagEntry ParentGraph { get; }
	public int CodecPack { get; }
	public IReadOnlyList<AnimationGraphNode> Nodes { get; }
	public IReadOnlyList<BlendScreen> BlendScreens { get; }
	public IReadOnlyList<GraphAnimation> Animations { get; }
	public IReadOnlyList<AnimationLabel> Labels { get; }
	public IReadOnlyList<ObjectOverlay> ObjectOverlays { get; }
	public IReadOnlyList<(string Label, AnimationReference Animation)> VehicleSuspension { get; }
	public IReadOnlyList<InheritedGraph> Inheritance { get; }
	public IReadOnlyList<uint> ResourceGroups { get; }

	readonly Dictionary<int, AnimationResourceMember[]> _groups = new();

	public static bool IsGraph( TagEntry tag ) => tag is { Address: not 0 } && tag.Class == Class;

	public AnimationGraphTag( TagEntry tag )
	{
		if ( tag.Class != Class )
			throw new ArgumentException( $"{tag} is not a jmad tag", nameof( tag ) );

		Tag = tag;
		var d = tag.Data;
		ParentGraph = IsGraph( d.Reference( 0 ) ) ? d.Reference( 0 ) : null;
		CodecPack = d.I16( 0x12 );

		var index = 0;
		Nodes = d.Block( 0x14, 0x20 ).Select( n => new AnimationGraphNode( index++, n.StringId( 0 ) ?? "", n.I16( 4 ), n.I16( 6 ), n.I16( 8 ), n.U8( 0xA ), n.U8( 0xB ) ) ).ToList();

		// Stored as right/left yaw per frame (radians), their frame counts, then down/up pitch, as Halo 2.
		BlendScreens = d.Block( 0x38, 0x1C ).Select( s => new BlendScreen( s.StringId( 0 ) ?? "", new AimingScreenBounds(
			s.F32( 0x4 ), s.F32( 0x8 ), s.I16( 0xC ), s.I16( 0xE ), s.F32( 0x10 ), s.F32( 0x14 ), s.I16( 0x18 ), s.I16( 0x1A ) ) ) ).ToList();

		var animations = new List<GraphAnimation>();
		foreach ( var e in d.Block( 0x50, 0x88 ) )
		{
			animations.Add( new GraphAnimation
			{
				Graph = this,
				Index = animations.Count,
				Name = e.StringId( 0 ) ?? "",
				Weight = e.F32( 0x4 ),
				LoopFrame = e.I16( 0x8 ),
				PlaybackFlags = e.U16( 0xA ),
				BlendScreen = e.I8( 0xC ),
				NodeCount = e.U8( 0xF ),
				FrameCount = e.I16( 0x10 ),
				Type = (AnimationType)e.U8( 0x12 ),
				FrameInfoType = (AnimationFrameInfoType)e.U8( 0x13 ),
				InternalFlags = e.U16( 0x16 ),
				ProductionChecksum = e.U32( 0x1C ),
				NextAnimation = e.I16( 0x26 ),
				ResourceGroup = e.I16( 0x28 ),
				ResourceMember = e.I16( 0x2A ),
				ObjectSpaceParentNodes = e.Count( 0x5C ),
			} );
		}
		Animations = animations;

		AnimationReference Reference( TagView at ) => new( at.I16( 0 ), at.I16( 2 ) );

		var labels = new List<AnimationLabel>();
		foreach ( var mode in d.Block( 0x5C, 0x28 ) )
		{
			var modeName = mode.StringId( 0 ) ?? "";
			foreach ( var weaponClass in mode.Block( 0x4, 0x1C ) )
			{
				var className = weaponClass.StringId( 0 ) ?? "";
				foreach ( var weaponType in weaponClass.Block( 0x4, 0x34 ) )
				{
					var typeName = weaponType.StringId( 0 ) ?? "";
					foreach ( var action in weaponType.Block( 0x4, 8 ) )
						labels.Add( new AnimationLabel( modeName, className, typeName, action.StringId( 0 ) ?? "", false, Reference( action.At( 4 ) ) ) );
					foreach ( var overlay in weaponType.Block( 0x10, 8 ) )
						labels.Add( new AnimationLabel( modeName, className, typeName, overlay.StringId( 0 ) ?? "", true, Reference( overlay.At( 4 ) ) ) );
				}
			}
		}
		Labels = labels;

		VehicleSuspension = d.Block( 0x68, 0x28 ).Select( s => (s.StringId( 0 ) ?? "", Reference( s.At( 4 ) )) ).ToList();
		ObjectOverlays = d.Block( 0x74, 0x14 ).Select( o => new ObjectOverlay( o.StringId( 0 ) ?? "", Reference( o.At( 4 ) ), o.U16( 0xA ), o.StringId( 0xC ) ?? "" ) ).ToList();
		Inheritance = d.Block( 0x80, 0x30 ).Select( i => new InheritedGraph( i.Reference( 0 ), i.Block( 0x10, 2 ).Select( m => (int)m.I16( 0 ) ).ToList(), i.F32( 0x28 ), i.I32( 0x2C ) ) ).ToList();
		ResourceGroups = d.Block( 0xF8, 0xC ).Select( g => g.U32( 4 ) ).ToList();
	}

	// The graph a reference points into: this one, or an inherited graph (null if the entry is missing).
	public AnimationGraphTag GraphOf( AnimationReference reference )
	{
		if ( !reference.IsInherited )
			return this;

		if ( reference.Graph >= Inheritance.Count || !IsGraph( Inheritance[reference.Graph].Graph ) )
			return null;

		return new AnimationGraphTag( Inheritance[reference.Graph].Graph );
	}

	// One animation's data in a resource group (read once per group). Throws InvalidDataException when the group's resource is gone.
	public AnimationResourceMember Member( int group, int member )
	{
		AnimationResourceMember[] members;
		lock ( _groups )
		{
			if ( !_groups.TryGetValue( group, out members ) )
				_groups[group] = members = ReadGroup( group );
		}

		if ( member < 0 || member >= members.Length )
			throw new InvalidDataException( $"{Tag}: resource group {group} has {members.Length} members, not {member + 1}" );
		return members[member];
	}

	AnimationResourceMember[] ReadGroup( int group )
	{
		if ( group < 0 || group >= ResourceGroups.Count )
			throw new InvalidDataException( $"{Tag}: no resource group {group} (has {ResourceGroups.Count})" );

		var resource = Tag.Cache.Resources.Read( ResourceGroups[group] )
			?? throw new InvalidDataException( $"{Tag}: resource group {group} (0x{ResourceGroups[group]:X8}) is dead" );
		if ( !resource.HasPrimary )
			throw new InvalidDataException( $"{Tag}: resource group {group} has no page data" );

		var members = new List<AnimationResourceMember>();
		foreach ( var m in resource.Block( resource.Root, 0x30 ) )
		{
			members.Add( new AnimationResourceMember( resource.U32( m + 0x4 ), (ushort)resource.I16( m + 0x8 ), resource.U8( m + 0xA ), (sbyte)resource.U8( m + 0xB ),
				resource.U8( m + 0xC ), resource.U8( m + 0xD ), resource.U16( m + 0xE ), resource.U16( m + 0x10 ), resource.U16( m + 0x12 ),
				resource.I32( m + 0x14 ), resource.I32( m + 0x18 ), resource.DataReference( m + 0x1C ) ) );
		}
		return members.ToArray();
	}
}
