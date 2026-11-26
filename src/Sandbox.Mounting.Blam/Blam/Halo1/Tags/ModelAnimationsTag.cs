using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace Blam.Halo1.Tags;

// Inside the namespace so these win over s&box's global types when this compiles into the mount.
using Quaternion = System.Numerics.Quaternion;
using Vector3 = System.Numerics.Vector3;

public enum AnimationType : ushort
{
	/// <summary>Poses the whole skeleton.</summary>
	Base = 0,
	/// <summary>Adds its movement on top of whatever base animation is playing.</summary>
	Overlay = 1,
	/// <summary>Poses only the nodes it animates, replacing the base animation there.</summary>
	Replacement = 2,
}

[Flags]
public enum AnimationFlags : ushort
{
	CompressedData = 0x1,
	WorldRelative = 0x2,
	/// <summary>Authored at 25 frames per second instead of 30.</summary>
	Pal25Hz = 0x4,
}

/// <summary>
/// The movement an animation gives its object each frame (root motion), which the game applies to
/// the object itself rather than to its nodes.
/// </summary>
public enum AnimationFrameInfoType : ushort
{
	None = 0,
	/// <summary>Movement along the object's forward and left axes.</summary>
	DxDy = 1,
	/// <summary>Movement plus a turn about the up axis.</summary>
	DxDyDyaw = 2,
	/// <summary>Movement along all three axes plus a turn about the up axis.</summary>
	DxDyDzDyaw = 3,
}

/// <summary>
/// One frame's root motion: how far the object moves over the frame (world units, in the object's
/// own axes: x forward, y left, z up) and how far it turns about its up axis (radians, positive to
/// the left). The movement is taken out of the nodes: the root node stays in place, so the
/// animation walks on the spot unless whatever plays it moves the object by these.
/// </summary>
public readonly record struct AnimationFrameInfo( Vector3 Translation, float Yaw );

/// <summary>
/// A node's pose relative to its parent. Rotations use the usual quaternion convention (see
/// <see cref="ModelAnimationsTag"/> for how Halo stores them).
/// </summary>
public readonly record struct NodePose( Quaternion Rotation, Vector3 Translation, float Scale )
{
	public static readonly NodePose Identity = new( Quaternion.Identity, Vector3.Zero, 1 );
}

/// <summary>
/// One animation of an animation graph.
/// </summary>
public sealed class ModelAnimation
{
	public int Index { get; init; }
	public string Name { get; init; }
	public AnimationType Type { get; init; }
	public int FrameCount { get; init; }
	public int NodeCount { get; init; }
	public int LoopFrameIndex { get; init; }
	public int NextAnimationIndex { get; init; }
	public AnimationFlags Flags { get; init; }

	/// <summary>The frame an action happens on (a weapon firing, a grenade leaving the hand), -1 for none.</summary>
	public int KeyFrameIndex { get; init; }
	public int SecondKeyFrameIndex { get; init; }

	/// <summary>The sound the animation plays (an index into <see cref="ModelAnimationsTag.SoundReferences"/>, -1 for none) and the frame it starts on.</summary>
	public int SoundIndex { get; init; }
	public int SoundFrameIndex { get; init; }

	/// <summary>The frames the feet touch the ground on, -1 for none.</summary>
	public int LeftFootFrameIndex { get; init; }
	public int RightFootFrameIndex { get; init; }
	public AnimationFrameInfoType FrameInfoType { get; init; }

	public bool IsCompressed => Flags.HasFlag( AnimationFlags.CompressedData );

	// The root node's transform is its world transform: Halo ignores the object's position and facing (c10's x50 cutscene).
	public bool IsWorldRelative => Flags.HasFlag( AnimationFlags.WorldRelative );
	public float FrameRate => Flags.HasFlag( AnimationFlags.Pal25Hz ) ? 25 : 30;

	internal ulong RotationFlags { get; init; }
	internal ulong TranslationFlags { get; init; }
	internal ulong ScaleFlags { get; init; }
	// Read on first use: the scene loader looks at graphs for their names and indices only.
	internal Lazy<byte[]> LazyDefaultData { get; init; }
	internal Lazy<byte[]> LazyFrameData { get; init; }
	internal Lazy<byte[]> LazyFrameInfo { get; init; }
	byte[] DefaultData => LazyDefaultData?.Value;
	byte[] FrameData => LazyFrameData?.Value;
	internal int FrameSize { get; init; }
	/// <summary>Where the compressed data starts in the frame data (0 in MCC's maps).</summary>
	internal int CompressedDataOffset { get; init; }
	CompressedAnimationReport compressedReport;

	public bool AnimatesRotation( int node ) => (RotationFlags >> node & 1) != 0;
	public bool AnimatesTranslation( int node ) => (TranslationFlags >> node & 1) != 0;
	public bool AnimatesScale( int node ) => (ScaleFlags >> node & 1) != 0;

	/// <summary>
	/// Whether the stored data is complete and in a layout this reader decodes.
	/// </summary>
	public bool CanDecode
	{
		get
		{
			if ( IsCompressed )
				return InspectCompressed().Error is null;

			if ( FrameCount <= 0 || NodeCount <= 0 || NodeCount > 64 || FrameData is null || DefaultData is null )
				return false;

			int animated = 0, still = 0;
			for ( var n = 0; n < NodeCount; n++ )
			{
				animated += (AnimatesRotation( n ) ? 8 : 0) + (AnimatesTranslation( n ) ? 12 : 0) + (AnimatesScale( n ) ? 4 : 0);
				still += (AnimatesRotation( n ) ? 0 : 8) + (AnimatesTranslation( n ) ? 0 : 12) + (AnimatesScale( n ) ? 0 : 4);
			}

			return animated == FrameSize && FrameData.Length >= FrameSize * FrameCount && DefaultData.Length >= still;
		}
	}

	/// <summary>
	/// Decodes every frame: <c>NodeCount</c> poses per frame, frame after frame. Components the
	/// animation doesn't animate come from its default data. For overlays, the animated components
	/// are changes relative to the pose underneath, each frame on its own (the first frame is not the identity in general).
	/// </summary>
	public NodePose[] ReadFrames()
	{
		if ( !CanDecode )
			throw new InvalidOperationException( $"Animation {Name} can't be decoded" );

		if ( IsCompressed )
			return CompressedAnimation.Decode( this, FrameData, CompressedDataOffset );

		// The still components, which every frame shares.
		var still = new NodePose[NodeCount];
		var offset = 0;
		for ( var n = 0; n < NodeCount; n++ )
			still[n] = ReadNode( DefaultData, ref offset, NodePose.Identity, !AnimatesRotation( n ), !AnimatesTranslation( n ), !AnimatesScale( n ) );

		var poses = new NodePose[FrameCount * NodeCount];
		for ( var f = 0; f < FrameCount; f++ )
		{
			offset = f * FrameSize;
			for ( var n = 0; n < NodeCount; n++ )
				poses[f * NodeCount + n] = ReadNode( FrameData, ref offset, still[n], AnimatesRotation( n ), AnimatesTranslation( n ), AnimatesScale( n ) );
		}

		return poses;
	}

	/// <summary>
	/// Checks the layout of a compressed animation's data (see <see cref="CompressedAnimation"/>).
	/// </summary>
	public CompressedAnimationReport InspectCompressed()
	{
		if ( !IsCompressed )
			throw new InvalidOperationException( $"Animation {Name} isn't compressed" );

		return compressedReport ??= CompressedAnimation.Inspect( this, FrameData, CompressedDataOffset );
	}

	/// <summary>
	/// The animation's root motion, one entry per frame, or an empty array if it has none (or the
	/// data doesn't cover every frame).
	/// </summary>
	public AnimationFrameInfo[] ReadFrameInfo()
	{
		var size = FrameInfoType switch
		{
			AnimationFrameInfoType.DxDy => 8,
			AnimationFrameInfoType.DxDyDyaw => 12,
			AnimationFrameInfoType.DxDyDzDyaw => 16,
			_ => 0,
		};

		var data = LazyFrameInfo?.Value;
		if ( size == 0 || data is null || FrameCount <= 0 || data.Length < size * FrameCount )
			return [];

		var frames = new AnimationFrameInfo[FrameCount];
		for ( var f = 0; f < FrameCount; f++ )
		{
			var at = f * size;
			float Read( int i ) => BinaryPrimitives.ReadSingleLittleEndian( data.AsSpan( at + 4 * i ) );

			frames[f] = FrameInfoType switch
			{
				AnimationFrameInfoType.DxDy => new AnimationFrameInfo( new Vector3( Read( 0 ), Read( 1 ), 0 ), 0 ),
				AnimationFrameInfoType.DxDyDyaw => new AnimationFrameInfo( new Vector3( Read( 0 ), Read( 1 ), 0 ), Read( 2 ) ),
				_ => new AnimationFrameInfo( new Vector3( Read( 0 ), Read( 1 ), Read( 2 ) ), Read( 3 ) ),
			};
		}

		return frames;
	}

	/// <summary>
	/// Reads the components present for one node, in the order rotation, translation, scale, over
	/// the given pose.
	/// </summary>
	static NodePose ReadNode( byte[] data, ref int offset, NodePose pose, bool rotation, bool translation, bool scale )
	{
		var span = data.AsSpan();
		var r = pose.Rotation;
		var t = pose.Translation;
		var s = pose.Scale;

		if ( rotation )
		{
			r = ModelAnimationsTag.FromHalo( new Quaternion(
				BinaryPrimitives.ReadInt16LittleEndian( span[offset..] ),
				BinaryPrimitives.ReadInt16LittleEndian( span[(offset + 2)..] ),
				BinaryPrimitives.ReadInt16LittleEndian( span[(offset + 4)..] ),
				BinaryPrimitives.ReadInt16LittleEndian( span[(offset + 6)..] ) ) );
			offset += 8;
		}

		if ( translation )
		{
			t = new Vector3(
				BinaryPrimitives.ReadSingleLittleEndian( span[offset..] ),
				BinaryPrimitives.ReadSingleLittleEndian( span[(offset + 4)..] ),
				BinaryPrimitives.ReadSingleLittleEndian( span[(offset + 8)..] ) );
			offset += 12;
		}

		if ( scale )
		{
			s = BinaryPrimitives.ReadSingleLittleEndian( span[offset..] );
			offset += 4;
		}

		return new NodePose( r, t, s );
	}
}

/// <summary>
/// The grid an aiming or looking overlay's frames are laid out on: one frame per (yaw, pitch)
/// step, yaw across (most negative first), pitch down the rows (most negative first). Angles are
/// radians, yaw positive to the left, pitch positive up, relative to the unit's facing.
/// </summary>
/// <remarks>
/// 24 bytes: negative/positive yaw per frame 0x0/0x4, negative/positive yaw frame count 0x8/0xA,
/// negative/positive pitch per frame 0xC/0x10, negative/positive pitch frame count 0x14/0x16.
/// The game reads the overlay at the unit's aim relative to its facing, bilinearly between the four
/// surrounding frames, held at the grid's edge.
/// </remarks>
public readonly record struct AimingScreenBounds( float NegativeYawDelta, float PositiveYawDelta, int NegativeYawFrames, int PositiveYawFrames,
	float NegativePitchDelta, float PositivePitchDelta, int NegativePitchFrames, int PositivePitchFrames )
{
	public int Width => NegativeYawFrames + PositiveYawFrames + 1;
	public int Height => NegativePitchFrames + PositivePitchFrames + 1;

	/// <summary>The yaw range the grid covers, radians (negative = right).</summary>
	public float MinYaw => -NegativeYawFrames * NegativeYawDelta;
	public float MaxYaw => PositiveYawFrames * PositiveYawDelta;
	public float MinPitch => -NegativePitchFrames * NegativePitchDelta;
	public float MaxPitch => PositivePitchFrames * PositivePitchDelta;

	public static AimingScreenBounds Read( Blam.IO.MemoryRegion data, uint address ) => new(
		data.F32( address ), data.F32( address + 0x4 ), data.I16( address + 0x8 ), data.I16( address + 0xA ),
		data.F32( address + 0xC ), data.F32( address + 0x10 ), data.I16( address + 0x14 ), data.I16( address + 0x16 ) );

	/// <summary>
	/// Where an aim falls on the grid: the four frames around it and the fractions between them,
	/// the way Halo picks them (clamped to the edge cells).
	/// </summary>
	public (int Frame00, int Frame10, int Frame01, int Frame11, float YawFraction, float PitchFraction) Locate( float yaw, float pitch )
	{
		var (yawIndex, yawFraction) = Axis( yaw, NegativeYawDelta, PositiveYawDelta, NegativeYawFrames, PositiveYawFrames );
		var (pitchIndex, pitchFraction) = Axis( pitch, NegativePitchDelta, PositivePitchDelta, NegativePitchFrames, PositivePitchFrames );
		var nextYaw = Math.Min( yawIndex + 1, Width - 1 );
		var nextPitch = Math.Min( pitchIndex + 1, Height - 1 );
		return (yawIndex + pitchIndex * Width, nextYaw + pitchIndex * Width, yawIndex + nextPitch * Width, nextYaw + nextPitch * Width, yawFraction, pitchFraction);
	}

	static (int Index, float Fraction) Axis( float angle, float negativeDelta, float positiveDelta, int negativeFrames, int positiveFrames )
	{
		var delta = angle < 0 ? negativeDelta : positiveDelta;
		var frame = delta == 0 ? 0 : angle / delta;
		var index = (int)MathF.Floor( frame );
		var fraction = frame - index;
		if ( index >= positiveFrames )
		{
			index = positiveFrames - 1;
			fraction = 1;
		}
		if ( index < -negativeFrames )
		{
			index = -negativeFrames;
			fraction = 0;
		}

		return (index + negativeFrames, fraction);
	}
}

/// <summary>
/// One weapon class of a unit seat ("rifle", "pistol", "unarmed" under "stand"), with its
/// aiming grid and the animation index in each of its slots (see <see cref="UnitSeat.WeaponClassSlots"/>).
/// </summary>
public sealed record UnitWeaponClass( string Label, AimingScreenBounds AimingScreen, IReadOnlyList<int> Animations )
{
	public int Animation( string slot )
	{
		var index = Array.IndexOf( UnitSeat.WeaponClassSlots, slot );
		return index >= 0 && index < Animations.Count ? Animations[index] : -1;
	}
}

/// <summary>
/// One unit seat of an animation graph ("stand", "crouch", a vehicle seat).
/// </summary>
/// <remarks>
/// Unit seats are the graph's block at 0xC (100 bytes: label, looking screen bounds 0x20,
/// animations 0x40, ik points 0x4C, weapon classes 0x58). Weapon classes are 188 bytes: label,
/// grip marker 0x20, hand marker 0x40, aiming screen bounds 0x60, animations 0x98 (an int16 animation
/// index per slot of <see cref="WeaponClassSlots"/>), ik points 0xA4, weapon types 0xB0.
/// </remarks>
public sealed record UnitSeat( string Label, AimingScreenBounds LookingScreen, IReadOnlyList<int> Animations, IReadOnlyList<UnitWeaponClass> WeaponClasses )
{
	/// <summary>
	/// The animation slots of a weapon class, in tag order (aim-still and aim-move are the overlays
	/// Halo plays on the aiming grid).
	/// </summary>
	public static readonly string[] WeaponClassSlots =
	[
		"idle", "gesture", "turn-left", "turn-right", "dive-front", "dive-back", "dive-left", "dive-right",
		"move-front", "move-back", "move-left", "move-right", "slide-front", "slide-back", "slide-left", "slide-right",
		"airborne", "land-soft", "land-hard", "unused", "throw-grenade", "disarm", "drop", "ready", "put-away",
		"aim-still", "aim-move", "surprise-front", "surprise-back", "berserk", "evade-left", "evade-right",
		"signal-move", "signal-attack", "warn", "stunned-front", "stunned-back", "stunned-left", "stunned-right",
		"melee", "celebrate", "panic", "melee-airborne", "flaming", "resurrect-front", "resurrect-back",
		"melee-continuous", "feeding", "leap-start", "leap-airborne", "leap-melee", "zapping",
	];
}

/// <summary>
/// An animation an object plays from one of its functions, e.g. trees swaying in the wind.
/// </summary>
/// <param name="Function">Which of the object's exported functions (A to D) drives it.</param>
/// <param name="ControlsFrame">Whether the function picks the frame (otherwise it scales the animation).</param>
public sealed record ObjectAnimationEntry( int AnimationIndex, int Function, bool ControlsFrame );

/// <summary>
/// Reader for <c>antr</c> (model_animations) tags: a model's animation graph.
/// </summary>
/// <remarks>
/// Offsets: object animations 0x0 (20 bytes: animation index, function, function controls),
/// units 0xC (100 bytes: label, animations 0x40), weapons 0x18, vehicles 0x24 (116 bytes:
/// animations 0x5C), devices 0x30 (96 bytes: animations 0x54), unit damage 0x3C, first person
/// weapons 0x48 (28 bytes: animations 0x10, an animation index per slot), sound references 0x54
/// (20 bytes: sound reference), flags 0x64, nodes 0x68 (64 bytes: name, next sibling 0x20, first
/// child 0x22, parent 0x24), animations 0x74 (180 bytes).
///
/// Animations: name, type 0x20, frame count 0x22, frame size 0x24, frame info type 0x26, frame
/// info 0x48, node count 0x2C, loop frame 0x2E, key frames 0x34 and 0x36, next animation 0x38,
/// flags 0x3A, sound 0x3C, sound frame 0x3E, left and right foot frames 0x40 and 0x41 (int8), and
/// three 64 bit node masks for the nodes whose translation (0x5C), rotation (0x6C) and scale
/// (0x7C) change over the animation. Default data (0x8C) and frame data (0xA0) are data
/// references (size, flags, file offset, pointer) into the tag data.
///
/// Uncompressed frame data, verified against every unit quaternion in the maps: each frame
/// stores, node by node, the rotation (four int16, scaled by 32767), translation (three floats,
/// world units) and scale (float) of each animated component, in that order. Default data stores
/// the components that don't change the same way, once for the whole animation. Rotations are
/// stored conjugated, as are the model's default node rotations: the parent-relative rotation
/// is the conjugate of the stored quaternion (checked against the model's inverse node matrices).
/// Compressed animations (flag 0x1, keyframed; only the cinematics use them) are decoded by
/// <see cref="CompressedAnimation"/>, from the frame data at the compressed data offset (0x88).
/// Frame info (0x48, a data reference, type at 0x26) holds the root motion: per frame dx, dy
/// (8 bytes), dx, dy, dyaw (12) or dx, dy, dz, dyaw (16), all floats.
/// </remarks>
public sealed class ModelAnimationsTag
{
	public TagEntry Tag { get; }
	public IReadOnlyList<string> NodeNames { get; }

	/// <summary>
	/// Each graph node's parent (an index into <see cref="NodeNames"/>), -1 for the root (the first
	/// node; Halo poses the nodes from it down, through the child and sibling links).
	/// </summary>
	public IReadOnlyList<int> NodeParents { get; }

	public IReadOnlyList<ModelAnimation> Animations { get; }
	public IReadOnlyList<ObjectAnimationEntry> ObjectAnimations { get; }

	/// <summary>
	/// The sounds the animations play (see <see cref="ModelAnimation.SoundIndex"/>), null where a
	/// reference is empty.
	/// </summary>
	public IReadOnlyList<TagEntry> SoundReferences { get; }

	/// <summary>
	/// Animation indices for each device entry; the first is the device's position animation.
	/// </summary>
	public IReadOnlyList<IReadOnlyList<int>> DeviceAnimations { get; }

	/// <summary>
	/// For a weapon's first person graph, the animation index of each first person slot (see
	/// <see cref="FirstPersonAnimations.Names"/>; -1 where the weapon has none). Empty for other graphs.
	/// </summary>
	public IReadOnlyList<int> FirstPersonAnimations { get; }

	/// <summary>
	/// The graph's unit seats, with their weapon classes and aiming grids. Empty for graphs that
	/// aren't a unit's.
	/// </summary>
	public IReadOnlyList<UnitSeat> UnitSeats { get; }

	public ModelAnimationsTag( CacheFile cache, TagEntry tag )
	{
		if ( tag.Class != TagClasses.ModelAnimations )
			throw new ArgumentException( $"{tag} is not a model_animations tag", nameof( tag ) );

		Tag = tag;
		var data = cache.TagData;
		var a = tag.Address;

		var objects = new List<ObjectAnimationEntry>();
		foreach ( var entry in data.Block( a, 0x14 ) )
			objects.Add( new ObjectAnimationEntry( data.I16( entry ), data.U16( entry + 0x2 ), data.U16( entry + 0x4 ) == 0 ) );
		ObjectAnimations = objects;

		var devices = new List<IReadOnlyList<int>>();
		foreach ( var entry in data.Block( a + 0x30, 0x60 ) )
		{
			var indices = new List<int>();
			foreach ( var index in data.Block( entry + 0x54, 2 ) )
				indices.Add( data.I16( index ) );
			devices.Add( indices );
		}
		DeviceAnimations = devices;

		// Only the first entry is used.
		var firstPerson = new List<int>();
		foreach ( var entry in data.Block( a + 0x48, 0x1C ) )
		{
			foreach ( var index in data.Block( entry + 0x10, 2 ) )
				firstPerson.Add( data.I16( index ) );
			break;
		}
		FirstPersonAnimations = firstPerson;

		static List<int> Indices( Blam.IO.MemoryRegion data, uint block )
		{
			var indices = new List<int>();
			foreach ( var index in data.Block( block, 2 ) )
				indices.Add( data.I16( index ) );
			return indices;
		}

		var seats = new List<UnitSeat>();
		foreach ( var seat in data.Block( a + 0xC, 0x64 ) )
		{
			var classes = new List<UnitWeaponClass>();
			foreach ( var weaponClass in data.Block( seat + 0x58, 0xBC ) )
				classes.Add( new UnitWeaponClass( data.CString( weaponClass, 32 ), AimingScreenBounds.Read( data, weaponClass + 0x60 ), Indices( data, weaponClass + 0x98 ) ) );
			seats.Add( new UnitSeat( data.CString( seat, 32 ), AimingScreenBounds.Read( data, seat + 0x20 ), Indices( data, seat + 0x40 ), classes ) );
		}
		UnitSeats = seats;

		var sounds = new List<TagEntry>();
		foreach ( var entry in data.Block( a + 0x54, 0x14 ) )
			sounds.Add( cache.GetTagReference( data, entry ) );
		SoundReferences = sounds;

		var nodes = new List<string>();
		var parents = new List<int>();
		foreach ( var node in data.Block( a + 0x68, 0x40 ) )
		{
			nodes.Add( data.CString( node, 32 ) );
			// The first node is the root whatever its parent says (first person graphs give it 0).
			parents.Add( nodes.Count == 1 ? -1 : data.I16( node + 0x24 ) );
		}
		NodeNames = nodes;
		NodeParents = parents;

		var animations = new List<ModelAnimation>();
		foreach ( var x in data.Block( a + 0x74, 0xB4 ) )
		{
			animations.Add( new ModelAnimation
			{
				Index = animations.Count,
				Name = data.CString( x, 32 ),
				Type = (AnimationType)data.U16( x + 0x20 ),
				FrameCount = data.I16( x + 0x22 ),
				FrameSize = data.I16( x + 0x24 ),
				NodeCount = data.I16( x + 0x2C ),
				LoopFrameIndex = data.I16( x + 0x2E ),
				NextAnimationIndex = data.I16( x + 0x38 ),
				Flags = (AnimationFlags)data.U16( x + 0x3A ),
				KeyFrameIndex = data.I16( x + 0x34 ),
				SecondKeyFrameIndex = data.I16( x + 0x36 ),
				SoundIndex = data.I16( x + 0x3C ),
				SoundFrameIndex = data.I16( x + 0x3E ),
				LeftFootFrameIndex = (sbyte)data.U8( x + 0x40 ),
				RightFootFrameIndex = (sbyte)data.U8( x + 0x41 ),
				FrameInfoType = (AnimationFrameInfoType)data.U16( x + 0x26 ),
				CompressedDataOffset = data.I32( x + 0x88 ),
				LazyFrameInfo = new Lazy<byte[]>( () => ReadData( data, x + 0x48 ) ),
				TranslationFlags = data.U32( x + 0x5C ) | (ulong)data.U32( x + 0x60 ) << 32,
				RotationFlags = data.U32( x + 0x6C ) | (ulong)data.U32( x + 0x70 ) << 32,
				ScaleFlags = data.U32( x + 0x7C ) | (ulong)data.U32( x + 0x80 ) << 32,
				LazyDefaultData = new Lazy<byte[]>( () => ReadData( data, x + 0x8C ) ),
				LazyFrameData = new Lazy<byte[]>( () => ReadData( data, x + 0xA0 ) ),
			} );
		}
		Animations = animations;
	}

	/// <summary>
	/// The number of animations in a graph, without reading them.
	/// </summary>
	public static int CountAnimations( CacheFile cache, TagEntry tag ) => cache.TagData.I32( tag.Address + 0x74 );

	/// <summary>
	/// Reads a data reference's bytes from the tag data, or null if it points elsewhere.
	/// </summary>
	static byte[] ReadData( Blam.IO.MemoryRegion data, uint address )
	{
		var size = data.I32( address );
		var pointer = data.U32( address + 0xC );
		if ( size <= 0 || !data.Contains( pointer, size ) )
			return size == 0 ? [] : null;

		return data.Slice( pointer, size ).ToArray();
	}

	/// <summary>
	/// Converts a rotation as Halo stores it (int16 or float components, conjugated) to a unit
	/// quaternion in the usual convention.
	/// </summary>
	public static Quaternion FromHalo( Quaternion stored )
	{
		var q = Quaternion.Conjugate( stored );
		var length = q.Length();
		return length > 1e-6f ? Quaternion.Multiply( q, 1 / length ) : Quaternion.Identity;
	}
}
