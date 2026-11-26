using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace Blam.Halo3.Tags;

// Inside the namespace so these win over s&box's global Vector types when this compiles into the mount.
using Quaternion = System.Numerics.Quaternion;
using Vector3 = System.Numerics.Vector3;

// One placed decorator. Rotation is unnormalised: Halo 3's decorator vertex shader rotates the model vertex by q v q* with the raw quaternion, which also scales by |q|^2 (0.3..1 on Valhalla), after decompressing the vertex with DecoratorSetTag.ModelCompression.
// Wind is byte 6 / 256 (0..1): how far the instance bends in the wind for a wind decorator, how much of the sun reaches it for a sun one (DecoratorSun); Light is the radiance it's lit with (rgb * 2^(63.75 a - 31.75)), before simple lights.
public readonly record struct DecoratorInstance( Vector3 Position, Quaternion Rotation, int Type, Vector3 Light, float Wind )
{
	public float Scale => Rotation.LengthSquared();
	public Quaternion Orientation => Rotation.LengthSquared() > 1e-12f ? Quaternion.Normalize( Rotation ) : Quaternion.Identity;
	public Vector3 Transform( Vector3 p ) => Position + Vector3.Transform( p, Orientation ) * Scale;
}

// The sun Halo 3's decorator shaders read (DecoratorsGlobalsVS): the last of the sky render model's lights (mode +0xE8, 0x1C each: direction toward it, colour, intensity), its colour times its intensity times 0.2. Zanzibar's is (0.915, 0.776, 0.601) from (-0.763, 0.067, 0.643).
public readonly record struct DecoratorSun( Vector3 Direction, Vector3 Color )
{
	public static DecoratorSun? FromSkyModel( TagEntry renderModel )
	{
		var lights = renderModel?.Data.Block( 0xE8, 0x1C );
		if ( lights is null || lights.Count == 0 )
			return null;
		var last = lights[lights.Count - 1];
		var direction = last.Vec3( 0 );
		return direction.LengthSquared() > 1e-12f ? new DecoratorSun( Vector3.Normalize( direction ), last.Vec3( 0xC ) * last.F32( 0x18 ) * 0.2f ) : null;
	}

	// A decorator vertex's light as Halo 3's decorator vertex shaders make it from the instance's, by the set's render shader (dctr +0x31): sun (3) adds the sun times the instance's sun amount times N.L; shaded (5) scales by 0.9 + 0.2 saturate(cos) of the vertex's offset from the instance origin to the sun (DecoratorsPS contrast (0.9, 0.2)); wind (0) and static (1) take it as it is.
	public Vector3 Light( int renderShader, DecoratorInstance instance, Vector3 normal, Vector3 offset )
	{
		if ( renderShader == DecoratorSetTag.SunShader && normal.LengthSquared() > 1e-12f )
			return instance.Light + Color * instance.Wind * Math.Max( Vector3.Dot( Vector3.Normalize( normal ), Direction ), 0 );
		if ( renderShader == DecoratorSetTag.ShadedShader && offset.LengthSquared() > 1e-12f )
			return instance.Light * (0.9f + 0.2f * Math.Clamp( Vector3.Dot( Vector3.Normalize( offset ), Direction ), 0, 1 ));
		return instance.Light;
	}
}

// A cluster's decorators of one set (cluster +0xE8, 0x3C), sorted by type: the instances in one of the BSP's decorator buffers.
public sealed record DecoratorGroup( int Cluster, int Set, IReadOnlyList<DecoratorInstance> Instances );

// A decorator_set (dctr) tag: a render model whose instance mesh holds one subpart per decorator type, and the bitmap and shader it's drawn with (Assembly Halo3 dctr layout, which MCC keeps).
public sealed class DecoratorSetTag( TagEntry tag )
{
	public TagEntry Tag { get; } = tag;
	public TagEntry RenderModel => Tag.Data.Reference( 0 ) is { Address: not 0 } model && model.Class == new TagClass( "mode" ) ? model : null;
	public int TypeCount => Tag.Data.I32( 0x1C );
	public TagEntry Texture => Tag.Data.Reference( 0x20 );

	// 0 default, 1 no wind, 2 static, 3 sun, 4 wavy, 5 shaded.
	public int RenderShader => Tag.Data.U8( 0x31 );
	public const int SunShader = 3;
	public const int ShadedShader = 5;

	// The distances (world units) the set starts fading at and is gone by, as the tag has them (Valhalla's grass 7..12).
	public float FadeStart => Tag.Data.F32( 0x64 );
	public float FadeEnd => Tag.Data.F32( 0x68 );

	// The distances MCC fades the set over: its shader's LOD constants give fade = saturate( (end - d) / (end - start) ), start 7.926x the tag's and the fade 11.323x as long (riverworld capture, three sets, all exact), discarding below 0.3 (the alpha test is a * fade - 0.3).
	public const float FadeCutoff = 0.3f;
	public (float Start, float End) MccFade()
	{
		var start = FadeStart * 7.926f;
		return (start, start + (FadeEnd - FadeStart) * 11.323f);
	}

	// The decorator model's vertex bounds: MCC's Position_Compression constants are exactly twice the model's compression bounds on every set (nine on Valhalla), so the model draws at 2x before the instance's |q|^2 scale.
	public static GeometryCompression ModelCompression( RenderGeometry geometry )
	{
		var c = geometry.Compression.Count > 0 ? geometry.Compression[0] : GeometryCompression.Identity;
		return new GeometryCompression( c.Min * 2, c.Max * 2, c.UvMin, c.UvMax );
	}
}

// The BSP's decorators: its decorator sets (sbsp +0x1CC) and every cluster's decorator groups, decoded from the decorator render geometry's instance buffers (format 20, 16 bytes).
// Instance (MCC's DecoratorsVS reads it as u16 x4, unorm8 x4, unorm8 x4): u16 x3 position (group offset + u16 * group scale), u8 wind amount (/256), u8 type, unorm8 x4 quaternion (x2-1), unorm8 rgb light + exponent (checked against the ground on riverworld).
public sealed class StructureBspDecorators
{
	public IReadOnlyList<TagEntry> Sets { get; }
	public IReadOnlyList<DecoratorGroup> Groups { get; }

	StructureBspDecorators( IReadOnlyList<TagEntry> sets, IReadOnlyList<DecoratorGroup> groups )
	{
		Sets = sets;
		Groups = groups;
	}

	// Null if the BSP has no decorators or their buffers are stripped.
	public static StructureBspDecorators Load( StructureBspTag bsp )
	{
		var d = bsp.Tag.Data;
		var sets = new List<TagEntry>();
		foreach ( var s in d.Block( 0x1CC, 0x10 ) )
			sets.Add( s.Reference( 0 ) is { Address: not 0 } set ? set : null );
		if ( sets.Count == 0 )
			return null;

		var geometry = new RenderGeometry( d, RenderGeometry.StructureBspDecoratorOffset );
		if ( !geometry.Load() )
			return null;

		var groups = new List<DecoratorGroup>();
		var clusters = d.Block( 0xC4, 0x118 );
		for ( var c = 0; c < clusters.Count; c++ )
		{
			foreach ( var g in clusters[c].Block( 0xE8, 0x3C ) )
			{
				int count = g.I16( 0 ), set = g.I8( 2 ), buffer = g.I8( 3 ), offset = g.I32( 4 );
				if ( count <= 0 || geometry.Buffer( buffer ) is not { Format: 20, Stride: 16 } vb || offset < 0 || offset + count * 16 > vb.Data.Length )
					continue;

				var origin = g.Vec3( 0x08 );
				var scale = g.Vec3( 0x18 );
				var data = vb.Data.Span.Slice( offset, count * 16 );
				var instances = new DecoratorInstance[count];
				for ( var i = 0; i < count; i++ )
				{
					var e = data[(i * 16)..];
					var position = origin + new Vector3( U16( e, 0 ), U16( e, 2 ), U16( e, 4 ) ) * scale;
					var rotation = new Quaternion( Snorm( e[8] ), Snorm( e[9] ), Snorm( e[10] ), Snorm( e[11] ) );
					var light = new Vector3( e[12], e[13], e[14] ) / 255f * MathF.Pow( 2, 63.75f * e[15] / 255f - 31.75f );
					instances[i] = new DecoratorInstance( position, rotation, e[7], light, e[6] / 256f );
				}
				groups.Add( new DecoratorGroup( c, set, instances ) );
			}
		}

		return new StructureBspDecorators( sets, groups );
	}

	static int U16( ReadOnlySpan<byte> b, int offset ) => BinaryPrimitives.ReadUInt16LittleEndian( b[offset..] );

	static float Snorm( byte b ) => b / 255f * 2 - 1;
}
