using System;
using System.Collections.Generic;
using Blam.IO;

namespace Blam.Halo1.Tags;

// Inside the namespace so these win over s&box's global Vector2/Vector3 when this compiles into the mount.
using Vector3 = System.Numerics.Vector3;
using Vector4 = System.Numerics.Vector4;

/// <summary>
/// One detail object painted onto a level.
/// </summary>
/// <param name="Position">Where the bottom of its sprite sits, in world units (without the collection's global z offset).</param>
/// <param name="Layer">Which detail object type it is. See <see cref="DetailObjects.FindType(IReadOnlyList{DetailObjectCollectionTag}, int)"/>.</param>
/// <param name="Variant">Which of the type's sprites it shows.</param>
/// <param name="Color">The color baked into it when the level was lit (gamma space, 0-1).</param>
/// <param name="Cell">The cell it's in, in cells of <see cref="DetailObjects.CellSize"/> world units.</param>
public readonly record struct DetailObjectInstance( Vector3 Position, int Layer, int Variant, Vector3 Color, (int X, int Y, int Z) Cell );

/// <summary>
/// Reader for the detail objects stored in a structure BSP.
/// </summary>
/// <remarks>
/// <para>
/// The BSP has a block (at 0x24C, at most one 64 byte element) of four blocks: cells 0x0,
/// instances 0xC, counts 0x18 and z reference vectors 0x24. None of the packing is documented;
/// it was worked out from the MCC maps and checked against the collision geometry (every object
/// on a30 and c10 lands within a few hundredths of a world unit of the ground).
/// </para>
/// <para>
/// Cells are 8x8x8 world unit boxes (32 bytes: cell x/y/z as int16, z offset int16 0x6, valid
/// layers 0x8, first instance 0xC, first count 0x10). Each set bit of the layer mask is one
/// detail object type present in the cell; for each, in bit order, the next count element gives
/// how many instances follow, and the z reference vector with the same index gives their height.
/// </para>
/// <para>
/// An instance is 6 bytes: x, y and z bytes, a data byte (the sprite variant), and a R5G6B5 color.
/// x and y are 1/32 world unit steps inside the cell. The z byte isn't a height on its own:
/// with the layer's z reference vector (i, j, k, l), the height above the bottom of the cell is
/// <c>(z offset + i x + j y + k z + 256 l) / 32</c>, all in 1/32 world unit steps. The plane through
/// the cell's ground carries most of the height, and the z byte the rest.
/// </para>
/// </remarks>
public static class DetailObjects
{
	public const float CellSize = 8;

	/// <summary>
	/// Bytes of instance position per world unit.
	/// </summary>
	const float Steps = 32;

	/// <summary>
	/// Reads every detail object in a BSP. Returns an empty list for BSPs without any.
	/// </summary>
	public static List<DetailObjectInstance> Read( StructureBspTag bsp )
	{
		var instances = new List<DetailObjectInstance>();
		var data = bsp.Data;

		foreach ( var block in data.Block( bsp.TagAddress + 0x24C, 0x40 ) )
		{
			var instanceCount = data.I32( block + 0xC );
			var instanceBase = data.U32( block + 0x10 );
			var countCount = data.I32( block + 0x18 );
			var countBase = data.U32( block + 0x1C );
			var referenceCount = data.I32( block + 0x24 );
			var referenceBase = data.U32( block + 0x28 );

			foreach ( var cell in data.Block( block, 0x20 ) )
			{
				var coordinates = (X: (int)data.I16( cell ), Y: (int)data.I16( cell + 0x2 ), Z: (int)data.I16( cell + 0x4 ));
				var origin = new Vector3( coordinates.X, coordinates.Y, coordinates.Z ) * CellSize;
				var zOffset = data.I16( cell + 0x6 );
				var layers = data.U32( cell + 0x8 );
				var next = data.I32( cell + 0xC );
				var countIndex = data.I32( cell + 0x10 );

				for ( var layer = 0; layer < 32; layer++ )
				{
					if ( (layers & (1u << layer)) == 0 )
						continue;

					if ( countIndex >= countCount || countIndex >= referenceCount )
						break;

					var count = data.I16( countBase + (uint)(countIndex * 2) );
					var reference = referenceBase + (uint)(countIndex * 16);
					var plane = new Vector4( data.F32( reference ), data.F32( reference + 0x4 ), data.F32( reference + 0x8 ), data.F32( reference + 0xC ) );
					countIndex++;

					for ( var n = 0; n < count && next < instanceCount; n++, next++ )
					{
						var instance = instanceBase + (uint)(next * 6);
						var packed = new Vector4( data.U8( instance ), data.U8( instance + 0x1 ), data.U8( instance + 0x2 ), 256 );
						var height = (zOffset + Vector4.Dot( plane, packed )) / Steps;

						instances.Add( new DetailObjectInstance(
							Position: origin + new Vector3( packed.X / Steps, packed.Y / Steps, height ),
							Layer: layer,
							Variant: data.U8( instance + 0x3 ),
							Color: UnpackColor( data.U16( instance + 0x4 ) ),
							Cell: coordinates ) );
					}
				}
			}
		}

		return instances;
	}

	/// <summary>
	/// Finds the type an instance's layer refers to. Layers number the types of the scenario's
	/// collections one after another, in palette order. (Every MCC map with detail objects uses a
	/// single collection, so beyond the first collection this is an assumption.)
	/// </summary>
	public static (DetailObjectCollectionTag Collection, int TypeIndex) FindType( IReadOnlyList<DetailObjectCollectionTag> palette, int layer )
	{
		foreach ( var collection in palette )
		{
			if ( collection is null )
				continue;

			if ( layer < collection.Types.Count )
				return (collection, layer);

			layer -= collection.Types.Count;
		}

		return (null, -1);
	}

	static Vector3 UnpackColor( ushort color ) => new(
		((color >> 11) & 0x1F) / 31f,
		((color >> 5) & 0x3F) / 63f,
		(color & 0x1F) / 31f );
}
