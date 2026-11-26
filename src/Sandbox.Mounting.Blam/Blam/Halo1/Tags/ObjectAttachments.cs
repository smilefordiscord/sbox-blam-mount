using System;
using System.Collections.Generic;

namespace Blam.Halo1.Tags;

// Inside the namespace so these win over s&box's global Vector3 when this compiles into the mount.
using Vector3 = System.Numerics.Vector3;
using Quaternion = System.Numerics.Quaternion;

/// <summary>
/// Something an object carries around at one of its model's markers: a light, a looping
/// effect, a particle system, a contrail or a looping sound.
/// </summary>
/// <param name="Marker">Model marker it's attached to. Empty means the object's origin.</param>
/// <param name="PrimaryScale">Object function output (1-4: A-D out) that scales it, 0 if none. Used for blinking or power-driven lights.</param>
/// <param name="SecondaryScale">A second function output (1-4: A-D out, 0 none), which only effects use (their "B" scale).</param>
/// <param name="ChangeColor">Change color (1-4: A-D, 0 none) that tints lights and effects.</param>
public sealed record ObjectAttachment( TagEntry Type, string Marker, int PrimaryScale, int SecondaryScale = 0, int ChangeColor = 0 );

/// <summary>
/// A marker in a model: a named point (and orientation) that effects, lights and weapons attach to.
/// </summary>
/// <param name="Position">Position in the model's space, in world units.</param>
/// <param name="Rotation">Orientation in the model's space; its forward (+X) is the marker's direction.</param>
public sealed record ModelMarker( string Name, int Region, int Permutation, Vector3 Position, Quaternion Rotation );

/// <summary>
/// Readers for object attachments and the model markers they're placed at.
/// </summary>
/// <remarks>
/// Every object type starts with the shared object definition: attachments are a block at 0x140
/// (72 bytes: type reference, marker name 0x10, primary/secondary scale 0x30/0x32, change color
/// 0x34), widgets a
/// block at 0x14C (32 bytes: a reference to an antenna, flag, glow, meter or lightning tag).
///
/// In gbxmodels, markers are a block at 0xAC (64 bytes: name, instances block 0x34 of 32 byte
/// instances: region, permutation and node index as bytes, translation 0x4, rotation quaternion
/// i, j, k, w at 0x10) relative to a node. Nodes are a block at 0xB8 (156 bytes: parent 0x24,
/// default translation 0x28 and rotation 0x34) relative to their parent. Like animation data,
/// the stored rotations are inverted (conjugated) relative to the usual convention.
/// </remarks>
public static class ObjectAttachments
{
	public static IReadOnlyList<ObjectAttachment> Read( CacheFile cache, TagEntry objectTag )
	{
		var data = cache.TagData;
		var list = new List<ObjectAttachment>();
		foreach ( var entry in data.Block( objectTag.Address + 0x140, 0x48 ) )
		{
			var type = cache.GetTagReference( data, entry );
			if ( type is null )
				continue;

			list.Add( new ObjectAttachment( type, data.CString( entry + 0x10, 32 ), data.U16( entry + 0x30 ), data.U16( entry + 0x32 ), data.U16( entry + 0x34 ) ) );
		}

		return list;
	}

	/// <summary>
	/// The object's widgets: antennas, flags, glows, meters and lightning. Unlike attachments they
	/// name no marker; their tags say which markers they use.
	/// </summary>
	public static IReadOnlyList<TagEntry> ReadWidgets( CacheFile cache, TagEntry objectTag )
	{
		var data = cache.TagData;
		var list = new List<TagEntry>();
		foreach ( var entry in data.Block( objectTag.Address + 0x14C, 0x20 ) )
		{
			var type = cache.GetTagReference( data, entry );
			if ( type is not null )
				list.Add( type );
		}

		return list;
	}

	/// <summary>
	/// Reads a gbxmodel's markers in model space: each marker instance's transform composed with
	/// its node's default pose.
	/// </summary>
	public static IReadOnlyList<ModelMarker> ReadMarkers( CacheFile cache, TagEntry modelTag )
	{
		var data = cache.TagData;
		var markers = new List<ModelMarker>();
		if ( modelTag is null || modelTag.Class != TagClasses.GbxModel )
			return markers;

		// Default pose of each node in model space. Parents always come before their children.
		var nodes = new List<(Vector3 Position, Quaternion Rotation)>();
		foreach ( var node in data.Block( modelTag.Address + 0xB8, 0x9C ) )
		{
			var parent = data.I16( node + 0x24 );
			var position = data.Vec3( node + 0x28 );
			var rotation = Quaternion.Conjugate( ReadQuaternion( data, node + 0x34 ) );

			if ( parent >= 0 && parent < nodes.Count )
			{
				var p = nodes[parent];
				position = p.Position + Vector3.Transform( position, p.Rotation );
				rotation = p.Rotation * rotation;
			}

			nodes.Add( (position, rotation) );
		}

		foreach ( var marker in data.Block( modelTag.Address + 0xAC, 0x40 ) )
		{
			var name = data.CString( marker, 32 );
			foreach ( var instance in data.Block( marker + 0x34, 0x20 ) )
			{
				var nodeIndex = (sbyte)data.U8( instance + 0x2 );
				var position = data.Vec3( instance + 0x4 );
				var rotation = Quaternion.Conjugate( ReadQuaternion( data, instance + 0x10 ) );

				if ( nodeIndex >= 0 && nodeIndex < nodes.Count )
				{
					var node = nodes[nodeIndex];
					position = node.Position + Vector3.Transform( position, node.Rotation );
					rotation = node.Rotation * rotation;
				}

				markers.Add( new ModelMarker( name, (sbyte)data.U8( instance ), (sbyte)data.U8( instance + 0x1 ), position, Quaternion.Normalize( rotation ) ) );
			}
		}

		return markers;
	}

	static Quaternion ReadQuaternion( IO.MemoryRegion data, uint address )
	{
		var q = new Quaternion( data.F32( address ), data.F32( address + 4 ), data.F32( address + 8 ), data.F32( address + 12 ) );
		return q.LengthSquared() < 1e-6f ? Quaternion.Identity : Quaternion.Normalize( q );
	}
}
