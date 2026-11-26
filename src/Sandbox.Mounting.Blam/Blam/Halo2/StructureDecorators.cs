using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Blam.Halo2.Tags;

namespace Blam.Halo2;

// Inside the namespace so these win over s&box's global vector types when this compiles into the mount.
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;

/// <summary>
/// A decorator model placed in the level: where its model's origin goes and its axes (scaled), and its colour.
/// </summary>
/// <param name="Permutation">The placement's own permutation (its model, scale and tint), which may differ from its
/// group's (whose shader and fade it draws with).</param>
/// <param name="Color">Tint times lightmap colour (gamma, 0-1; Halo's shader doubles it).</param>
/// <param name="LightDirection">The light's direction (pointing down), which shades the model's normals by
/// <paramref name="LightWeight"/> (0 none, 1 fully).</param>
public sealed record DecoratorPlacement( DecoratorPermutation Permutation, Vector3 Position, Vector3 Forward, Vector3 Left, Vector3 Up,
	Vector3 Color, Vector3 LightDirection, float LightWeight );

/// <summary>
/// A grass card's vertex: the placement it stands on, its offset from it along the card's side (y) and axis (z),
/// already scaled, the axis it turns about, and its colour.
/// </summary>
public readonly record struct DecoratorSpriteVertex( Vector3 Anchor, Vector3 Offset, Vector3 Axis, Vector2 TexCoord, Vector3 Color );

/// <summary>
/// A floating decal's vertex, in world space, with its colour (tint times lightmap colour).
/// </summary>
public readonly record struct DecoratorDecalVertex( Vector3 Position, Vector2 TexCoord, Vector3 Color );

/// <summary>
/// A group of decorators drawn together: one class's placements in one leaf cell, with one shader and fade.
/// </summary>
/// <param name="Shader">The set's shader the group draws with.</param>
/// <param name="Fade">0 close, 1 medium, 2 far (see <see cref="StructureDecorators.FadeDistances"/>).</param>
/// <param name="Placements">Models: each placement.</param>
/// <param name="SpriteVertices">Grass cards: four vertices per placement, with <paramref name="Indices"/> (a triangle
/// list into them).</param>
/// <param name="DecalVertices">Floating decals, with <paramref name="Indices"/>.</param>
public sealed record DecoratorGroup( DecoratorSetTag Set, int SetIndex, DecoratorType Type, TagEntry Shader, int Fade, int CacheBlock,
	IReadOnlyList<DecoratorPlacement> Placements, IReadOnlyList<DecoratorSpriteVertex> SpriteVertices,
	IReadOnlyList<DecoratorDecalVertex> DecalVertices, IReadOnlyList<int> Indices );

/// <summary>
/// A structure BSP's decorators (sbsp +0x234): grass, rocks, debris and stains scattered over its surfaces, kept in
/// an octree of 8 world unit leaf cells, each leaf's placements in a cache block (a raw block) of its own.
/// </summary>
/// <remarks>
/// Layout and decoding:
/// block (0x30): grid origin (the root cell's centre) 0x0, cells per dimension 0xC, cache blocks 0x10, groups 0x18,
/// cells 0x20. A cell's child k lies at ±half/2 from its centre: x by bit 2 of k, y bit 1, z bit 0 (set = +).
/// Groups (0x18): set 0x0, class 0x1, permutation 0x2 (shader and fade), cluster 0x4, cache block 0x6, first
/// placement 0x8 and count 0xA, vertex start/count 0xC/0xE, index start/count 0x10/0x12.
/// Cache block raw data (header 0x88): placements (locator 0, 0x18 each), decal vertices (8, 0x20) and indices (16),
/// sprite vertices (72, 0x30) and indices (80).
/// </remarks>
public sealed class StructureDecorators
{
	const uint DecoratorsBlock = 0x234;
	const int CacheBlockSize = 0x34;
	const int GroupSize = 0x18;
	const int CellSize = 0x18;
	const float LeafHalfSize = 4;

	/// <summary>
	/// Where each fade distance's decorators are gone (world units): close, medium, far. They're full size (or
	/// opaque) until two thirds of it, shrinking (or fading) to nothing over the rest.
	/// </summary>
	public static readonly float[] FadeDistances = [8, 16, 24];

	public IReadOnlyList<DecoratorGroup> Groups { get; }

	sealed class CacheBlock
	{
		public byte[] Placements = [];
		public byte[] DecalVertices = [];
		public ushort[] DecalIndices = [];
		public byte[] SpriteVertices = [];
		public ushort[] SpriteIndices = [];
	}

	/// <param name="palette">The scenario's decorator palette (sets by index; null where an entry has none).</param>
	public StructureDecorators( StructureBspTag bsp, IReadOnlyList<DecoratorSetTag> palette )
	{
		var groups = new List<DecoratorGroup>();
		Groups = groups;

		var cache = bsp.Tag.Cache;
		var data = cache.TagData;
		uint block = 0;
		foreach ( var e in data.Elements( bsp.Tag.Address + DecoratorsBlock, 0x30 ) )
		{
			block = e;
			break;
		}

		if ( block == 0 )
			return;

		var origin = data.Vec3( block );
		var cellsPerDimension = data.I32( block + 0xC );

		var cacheBlocks = new List<CacheBlock>();
		foreach ( var e in data.Elements( block + 0x10, CacheBlockSize ) )
			cacheBlocks.Add( ReadCacheBlock( cache, e ) );

		var groupAddresses = new List<uint>( data.Elements( block + 0x18, GroupSize ) );

		// Every leaf's groups, with the leaf's minimum corner: compressed positions are relative to it.
		var cells = new List<uint>( data.Elements( block + 0x20, CellSize ) );
		var leaves = new List<(int Cell, Vector3 Minimum)>();
		void Walk( int index, Vector3 centre, float half, int depth )
		{
			if ( index < 0 || index >= cells.Count || depth > 16 )
				return;

			var cell = cells[index];
			if ( data.I16( cell + 0x12 ) > 0 )
				leaves.Add( (index, centre - new Vector3( LeafHalfSize )) );

			for ( var k = 0; k < 8; k++ )
			{
				var h = half / 2;
				var child = centre + new Vector3( (k & 4) != 0 ? h : -h, (k & 2) != 0 ? h : -h, (k & 1) != 0 ? h : -h );
				Walk( data.I16( cell + (uint)(k * 2) ), child, h, depth + 1 );
			}
		}

		if ( cells.Count > 0 )
			Walk( 0, origin, cellsPerDimension * LeafHalfSize, 0 );

		foreach ( var (cell, minimum) in leaves )
		{
			var groupCount = data.I16( cells[cell] + 0x12 );
			var groupStart = data.I32( cells[cell] + 0x14 );
			for ( var g = groupStart; g < groupStart + groupCount && g < groupAddresses.Count; g++ )
			{
				if ( ReadGroup( data, groupAddresses[g], minimum, palette, cacheBlocks ) is { } group )
					groups.Add( group );
			}
		}
	}

	static DecoratorGroup ReadGroup( IO.MemoryRegion data, uint g, Vector3 cellMinimum, IReadOnlyList<DecoratorSetTag> palette, List<CacheBlock> cacheBlocks )
	{
		var setIndex = (sbyte)data.U8( g );
		var classIndex = data.U8( g + 1 );
		var permutationIndex = data.U8( g + 2 );
		var cacheBlockIndex = data.I16( g + 6 );
		var start = data.I16( g + 8 );
		var count = data.I16( g + 0xA );
		var vertexStart = data.I16( g + 0xC );
		var vertexCount = data.I16( g + 0xE );
		var indexStart = data.I16( g + 0x10 );
		var indexCount = data.I16( g + 0x12 );

		if ( count <= 0 || setIndex < 0 || setIndex >= palette.Count || palette[setIndex] is not { } set )
			return null;
		if ( cacheBlockIndex < 0 || cacheBlockIndex >= cacheBlocks.Count || classIndex >= set.Classes.Count )
			return null;

		var decoratorClass = set.Classes[classIndex];
		if ( permutationIndex >= decoratorClass.Permutations.Count )
			return null;

		var permutation = decoratorClass.Permutations[permutationIndex];
		if ( permutation.Shader < 0 || permutation.Shader >= set.Shaders.Count || set.Shaders[permutation.Shader] is not { } shader )
			return null;

		var cacheBlock = cacheBlocks[cacheBlockIndex];
		var fade = Math.Clamp( permutation.Fade, 0, FadeDistances.Length - 1 );

		switch ( decoratorClass.Type )
		{
			case DecoratorType.Model:
			{
				var placements = new List<DecoratorPlacement>();
				for ( var i = start; i < start + count && (i + 1) * 0x18 <= cacheBlock.Placements.Length; i++ )
				{
					if ( ReadPlacement( set, cacheBlock.Placements.AsSpan( i * 0x18, 0x18 ), cellMinimum ) is { } placement )
						placements.Add( placement );
				}

				return placements.Count == 0 ? null
					: new DecoratorGroup( set, setIndex, decoratorClass.Type, shader, fade, cacheBlockIndex, placements, [], [], [] );
			}

			case DecoratorType.AxisRotatingQuad:
			case DecoratorType.ScreenFacingQuad:
			case DecoratorType.CrossQuad:
			{
				// Indices are absolute in the cache block; keep the group's vertices and renumber.
				var vertices = new List<DecoratorSpriteVertex>();
				var indices = new List<int>();
				var remap = new Dictionary<int, int>();
				for ( var i = indexStart; i < indexStart + indexCount && i < cacheBlock.SpriteIndices.Length; i++ )
				{
					int index = cacheBlock.SpriteIndices[i];
					if ( (index + 1) * 0x30 > cacheBlock.SpriteVertices.Length )
						continue;

					if ( !remap.TryGetValue( index, out var local ) )
					{
						var v = cacheBlock.SpriteVertices.AsSpan( index * 0x30, 0x30 );
						remap[index] = local = vertices.Count;
						vertices.Add( new DecoratorSpriteVertex( Vec3( v, 0 ), Vec3( v, 0xC ), Vec3( v, 0x18 ),
							new Vector2( F( v, 0x24 ), F( v, 0x28 ) ), DecoratorSetTag.Color( BinaryPrimitives.ReadUInt32LittleEndian( v[0x2C..] ) ) ) );
					}

					indices.Add( local );
				}

				return indices.Count < 3 ? null
					: new DecoratorGroup( set, setIndex, decoratorClass.Type, shader, fade, cacheBlockIndex, [], vertices, [], indices );
			}

			case DecoratorType.FloatingDecal:
			{
				var vertices = new List<DecoratorDecalVertex>();
				var indices = new List<int>();
				var remap = new Dictionary<int, int>();
				for ( var i = indexStart; i < indexStart + indexCount && i < cacheBlock.DecalIndices.Length; i++ )
				{
					int index = cacheBlock.DecalIndices[i];
					if ( (index + 1) * 0x20 > cacheBlock.DecalVertices.Length )
						continue;

					if ( !remap.TryGetValue( index, out var local ) )
					{
						var v = cacheBlock.DecalVertices.AsSpan( index * 0x20, 0x20 );
						remap[index] = local = vertices.Count;
						vertices.Add( new DecoratorDecalVertex( Vec3( v, 0 ), new Vector2( F( v, 0xC ), F( v, 0x10 ) ),
							DecoratorSetTag.Color( BinaryPrimitives.ReadUInt32LittleEndian( v[0x1C..] ) ) ) );
					}

					indices.Add( local );
				}

				return indices.Count < 3 ? null
					: new DecoratorGroup( set, setIndex, decoratorClass.Type, shader, fade, cacheBlockIndex, [], [], vertices, indices );
			}

			default:
				// Projected decals have no geometry in MCC's maps.
				return null;
		}
	}

	/// <summary>
	/// A model placement (0x18): u32 bits 0-2 class, 3-8 permutation, 9-12 rotation, 13-15 tint fraction, 16-19 scale
	/// fraction, 20-31 normal (4 bits each, x lowest); position (10:10:12 bits over the 8 unit cell); tint and lightmap
	/// colours (0x00RRGGBB); light direction (11:11:10); light directionality (f32).
	/// </summary>
	static DecoratorPlacement ReadPlacement( DecoratorSetTag set, ReadOnlySpan<byte> p, Vector3 cellMinimum )
	{
		var d0 = BinaryPrimitives.ReadUInt32LittleEndian( p );
		var classIndex = (int)(d0 & 7);
		var permutationIndex = (int)((d0 >> 3) & 0x3F);
		if ( classIndex >= set.Classes.Count || permutationIndex >= set.Classes[classIndex].Permutations.Count )
			return null;

		var permutation = set.Classes[classIndex].Permutations[permutationIndex];

		var packed = BinaryPrimitives.ReadUInt32LittleEndian( p[4..] );
		var position = cellMinimum + new Vector3( (packed & 0x3FF) * 8f / 1023, ((packed >> 10) & 0x3FF) * 8f / 1023, (packed >> 20) * 8f / 4095 );

		var s = ((d0 >> 16) & 0xF) / 15f;
		var scale = (1 - s) * permutation.ScaleMin + s * permutation.ScaleMax;

		// Rotation about the normal (or world up), in 15ths of a turn. Aligned to the normal, the frame comes from a
		// vector perpendicular to the unnormalised 4-bit normal, and forward and left keep its length, as the engine has
		// it.
		var angle = ((d0 >> 9) & 0xF) / 15f * MathF.PI * 2;
		var (sin, cos) = MathF.SinCos( angle );
		Vector3 forward, left, up;
		if ( permutation.AlignToNormal )
		{
			var n = new Vector3( Nibble( d0 >> 20 ), Nibble( d0 >> 24 ), Nibble( d0 >> 28 ) );
			var perpendicular = Perpendicular( n );
			var q = Vector3.Cross( n, perpendicular );
			forward = perpendicular * cos + q * sin;
			left = q * cos - perpendicular * sin;
			up = Vector3.Normalize( n );
		}
		else
		{
			forward = new Vector3( cos, sin, 0 );
			left = new Vector3( -sin, cos, 0 );
			up = Vector3.UnitZ;
		}

		// Colour: the permutation's tints lerped, times the surface's colour by the base map tint share, times the
		// lightmap colour by the lightmap tint share (else neutral 0.5).
		var t = ((d0 >> 13) & 7) / 7f;
		var baseMapTint = Math.Clamp( permutation.BaseMapTint, 0, 1 );
		var lightmapTint = Math.Clamp( permutation.LightmapTint, 0, 1 );
		var tint = Vector3.Lerp( permutation.Tint1, permutation.Tint2, t ) * (baseMapTint * DecoratorSetTag.Color( BinaryPrimitives.ReadUInt32LittleEndian( p[8..] ) ) + new Vector3( 1 - baseMapTint ));
		var lightmap = lightmapTint * DecoratorSetTag.Color( BinaryPrimitives.ReadUInt32LittleEndian( p[0xC..] ) ) + new Vector3( (1 - lightmapTint) * 0.5f );

		var packedLight = BinaryPrimitives.ReadUInt32LittleEndian( p[0x10..] );
		var light = new Vector3( (packedLight & 0x7FF) / 2047f * 2 - 1, ((packedLight >> 11) & 0x7FF) / 2047f * 2 - 1, (packedLight >> 22) / 1023f * 2 - 1 );
		if ( light.Z >= 0 )
			light = -light;
		var weight = Math.Clamp( (BinaryPrimitives.ReadSingleLittleEndian( p[0x14..] ) - set.LightingMinimum) * set.LightingScale, 0, 1 );

		return new DecoratorPlacement( permutation, position, forward * scale, left * scale, up * scale, tint * lightmap, light, weight );
	}

	static float Nibble( uint value ) => (value & 0xF) / 15f * 2 - 1;

	static Vector3 Perpendicular( Vector3 v )
	{
		var (x, y, z) = (MathF.Abs( v.X ), MathF.Abs( v.Y ), MathF.Abs( v.Z ));
		if ( y >= x && z >= x )
			return new Vector3( 0, v.Z, -v.Y );
		if ( z >= y )
			return new Vector3( -v.Z, 0, v.X );
		return new Vector3( v.Y, -v.X, 0 );
	}

	static CacheBlock ReadCacheBlock( CacheFile cache, uint e )
	{
		var result = new CacheBlock();
		var resources = RawResources.Read( cache, e, e + 0x10 );
		if ( resources is null )
			return result;

		foreach ( var r in resources.Entries )
		{
			if ( r.Type != 0 )
				continue;

			var bytes = resources.Block.AsSpan( r.Offset, r.Size );
			switch ( r.Primary )
			{
				case 0: result.Placements = bytes.ToArray(); break;
				case 8: result.DecalVertices = bytes.ToArray(); break;
				case 16: result.DecalIndices = Indices( bytes ); break;
				case 72: result.SpriteVertices = bytes.ToArray(); break;
				case 80: result.SpriteIndices = Indices( bytes ); break;
			}
		}

		return result;
	}

	static ushort[] Indices( ReadOnlySpan<byte> bytes )
	{
		var indices = new ushort[bytes.Length / 2];
		for ( var i = 0; i < indices.Length; i++ )
			indices[i] = BinaryPrimitives.ReadUInt16LittleEndian( bytes[(i * 2)..] );
		return indices;
	}

	static float F( ReadOnlySpan<byte> span, int offset ) => BinaryPrimitives.ReadSingleLittleEndian( span[offset..] );
	static Vector3 Vec3( ReadOnlySpan<byte> span, int offset ) => new( F( span, offset ), F( span, offset + 4 ), F( span, offset + 8 ) );
}
