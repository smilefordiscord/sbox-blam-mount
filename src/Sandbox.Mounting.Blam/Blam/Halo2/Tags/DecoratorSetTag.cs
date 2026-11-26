using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace Blam.Halo2.Tags;

using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;

/// <summary>
/// What a decorator class's placements are drawn as.
/// </summary>
public enum DecoratorType : byte
{
	/// <summary>
	/// A small model of the set's (rocks, tufts of grass, debris), placed per placement.
	/// </summary>
	Model = 0,

	/// <summary>
	/// A quad lying on the surface (stains, leaves), built by the tool in world space.
	/// </summary>
	FloatingDecal = 1,

	/// <summary>
	/// Never drawn in MCC's maps (no geometry).
	/// </summary>
	ProjectedDecal = 2,

	ScreenFacingQuad = 3,

	/// <summary>
	/// A card standing on the ground that turns about its axis (world up) to face the camera (grass).
	/// </summary>
	AxisRotatingQuad = 4,

	CrossQuad = 5,
}

/// <summary>
/// A permutation of a decorator class: its look and how its placements vary.
/// </summary>
/// <param name="Shader">Index into the set's shaders; -1 none.</param>
/// <param name="Flags">1 align to the surface normal (2 only on ground, 4 upright: editor only).</param>
/// <param name="Fade">0 close, 1 medium, 2 far: gone at 8, 16 or 24 world units.</param>
/// <param name="Model">Index into the set's models.</param>
/// <param name="Tint1">Tint colours (rgb 0-1) a placement's tint lerps between.</param>
/// <param name="BaseMapTint">How much of the placement's tint colour (the surface under it) multiplies in.</param>
/// <param name="LightmapTint">How much of the placement's lightmap colour lights it (the rest is neutral).</param>
public sealed record DecoratorPermutation( string Name, int Shader, int Flags, int Fade, int Model, float ScaleMin, float ScaleMax,
	Vector3 Tint1, Vector3 Tint2, float BaseMapTint, float LightmapTint )
{
	public bool AlignToNormal => (Flags & 1) != 0;
}

public sealed record DecoratorClass( string Name, DecoratorType Type, IReadOnlyList<DecoratorPermutation> Permutations );

/// <summary>
/// A run of a decorator set's indices (a triangle list) drawing one model.
/// </summary>
public sealed record DecoratorModel( string Name, int IndexStart, int IndexCount );

public readonly record struct DecoratorModelVertex( Vector3 Position, Vector3 Normal, Vector2 TexCoord );

/// <summary>
/// A Halo 2 decorator set (<c>DECR</c>): the classes of grass, rocks, debris and stains a structure BSP scatters over
/// its surfaces (see <see cref="StructureDecorators"/>), their shaders and the models they place.
/// </summary>
/// <remarks>
/// Layout and engine behaviour: shaders 0x0,
/// lighting minimum 0x8 and scale 0xC (1 / (maximum - minimum)), classes 0x10 (0x14: name, type, permutations 0xC),
/// models 0x18 (8: name, index start, index count), indices 0x28 (u16, a triangle list), and the vertices in a raw
/// block (0x38 pointer, size, header size, resource size, resources 0x48) of 0x38 byte vertices.
/// </remarks>
public sealed class DecoratorSetTag
{
	public static readonly TagClass Class = new( "DECR" );

	const int VertexStride = 0x38;

	public TagEntry Tag { get; }

	/// <summary>
	/// The set's shaders (null where an entry has none); permutations index them.
	/// </summary>
	public IReadOnlyList<TagEntry> Shaders { get; }

	public float LightingMinimum { get; }
	public float LightingScale { get; }
	public IReadOnlyList<DecoratorClass> Classes { get; }
	public IReadOnlyList<DecoratorModel> Models { get; }
	public ushort[] Indices { get; }
	public DecoratorModelVertex[] Vertices { get; }

	public DecoratorSetTag( TagEntry tag )
	{
		Tag = tag;
		var cache = tag.Cache;
		var data = cache.TagData;
		var a = tag.Address;

		var shaders = new List<TagEntry>();
		foreach ( var e in data.Elements( a, 8 ) )
			shaders.Add( cache.GetTagReference( e ) );
		Shaders = shaders;

		LightingMinimum = data.F32( a + 0x8 );
		LightingScale = data.F32( a + 0xC );

		// Permutation (0x28): name 0x0, shader 0x4, flags 0x8, fade 0x9, model 0xA, weight 0xB, scale 0xC-0x10,
		// tints 0x14 and 0x18 (0x00RRGGBB), base map tint 0x1C, lightmap tint 0x20, wind 0x24 (unused in MCC).
		var classes = new List<DecoratorClass>();
		foreach ( var e in data.Elements( a + 0x10, 0x14 ) )
		{
			var permutations = new List<DecoratorPermutation>();
			foreach ( var p in data.Elements( e + 0xC, 0x28 ) )
			{
				permutations.Add( new DecoratorPermutation(
					cache.GetString( data.U32( p ) ),
					(sbyte)data.U8( p + 0x4 ),
					data.U8( p + 0x8 ),
					data.U8( p + 0x9 ),
					(sbyte)data.U8( p + 0xA ),
					data.F32( p + 0xC ),
					data.F32( p + 0x10 ),
					Color( data.U32( p + 0x14 ) ),
					Color( data.U32( p + 0x18 ) ),
					data.F32( p + 0x1C ),
					data.F32( p + 0x20 ) ) );
			}

			classes.Add( new DecoratorClass( cache.GetString( data.U32( e ) ), (DecoratorType)data.U8( e + 4 ), permutations ) );
		}

		Classes = classes;

		var models = new List<DecoratorModel>();
		foreach ( var e in data.Elements( a + 0x18, 8 ) )
			models.Add( new DecoratorModel( cache.GetString( data.U32( e ) ), data.I16( e + 4 ), data.I16( e + 6 ) ) );
		Models = models;

		var indexCount = Math.Max( data.Count( a + 0x28 ), 0 );
		var indexAddress = data.U32( a + 0x2C );
		Indices = new ushort[indexCount];
		for ( var i = 0; i < indexCount; i++ )
			Indices[i] = data.U16( indexAddress + (uint)(i * 2) );

		Vertices = [];
		var resources = RawResources.Read( cache, a + 0x38, a + 0x48 );
		foreach ( var r in resources?.Entries ?? [] )
		{
			if ( r.Type != 2 || r.Primary != 0 )
				continue;

			var span = resources.Block.AsSpan( r.Offset, r.Size );
			Vertices = new DecoratorModelVertex[r.Size / VertexStride];
			for ( var i = 0; i < Vertices.Length; i++ )
			{
				var v = span[(i * VertexStride)..];
				Vertices[i] = new DecoratorModelVertex(
					new Vector3( F( v, 0 ), F( v, 4 ), F( v, 8 ) ),
					new Vector3( F( v, 0xC ), F( v, 0x10 ), F( v, 0x14 ) ),
					new Vector2( F( v, 0x30 ), F( v, 0x34 ) ) );
			}
		}
	}

	static float F( ReadOnlySpan<byte> span, int offset ) => BinaryPrimitives.ReadSingleLittleEndian( span[offset..] );

	/// <summary>
	/// A decorator colour, 0x00RRGGBB (the top byte is sometimes 0xFF; ignored).
	/// </summary>
	public static Vector3 Color( uint value ) => new( ((value >> 16) & 0xFF) / 255f, ((value >> 8) & 0xFF) / 255f, (value & 0xFF) / 255f );
}

/// <summary>
/// A raw block read with its resource table (the layout geometry sections use): <c>blkh</c>, a size, the header
/// struct, <c>rsrc</c>, the resources, <c>blkf</c>.
/// </summary>
sealed class RawResources
{
	public readonly record struct Entry( int Type, int Primary, int Secondary, int Offset, int Size );

	public byte[] Block { get; private init; }

	/// <summary>
	/// The header struct's bytes start here in <see cref="Block"/>.
	/// </summary>
	public const int HeaderOffset = 8;

	public IReadOnlyList<Entry> Entries { get; private init; }

	/// <summary>
	/// Reads the raw block described at <paramref name="info"/> (u32 pointer, size, header size, resource data size)
	/// with the resource table block at <paramref name="table"/> (0x10 each: u8 type, u8, u16, s16 primary locator,
	/// s16 secondary, u32 size, u32 offset from the rsrc marker). Null when there's no data.
	/// </summary>
	public static RawResources Read( CacheFile cache, uint info, uint table )
	{
		var data = cache.TagData;
		var pointer = data.U32( info );
		var size = (int)data.U32( info + 4 );
		var headerSize = (int)data.U32( info + 8 );
		var resourceSize = data.U32( info + 12 );
		if ( pointer == 0 || pointer == 0xFFFFFFFF || size == 0 || resourceSize == 0 )
			return null;

		var block = cache.ReadRaw( pointer, size );
		if ( BinaryPrimitives.ReadUInt32LittleEndian( block ) != 0x626C6B68 ) // 'blkh'
			throw new System.IO.InvalidDataException( $"Raw block at 0x{pointer:X8} in '{cache.Name}' doesn't start with blkh" );

		var resourceBase = HeaderOffset + headerSize;
		var entries = new List<Entry>();
		foreach ( var e in data.Elements( table, 0x10 ) )
			entries.Add( new Entry( data.U8( e ), data.I16( e + 4 ), data.I16( e + 6 ), resourceBase + (int)data.U32( e + 12 ), (int)data.U32( e + 8 ) ) );

		return new RawResources { Block = block, Entries = entries };
	}
}
