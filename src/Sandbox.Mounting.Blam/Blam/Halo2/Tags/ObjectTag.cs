using System;
using System.Collections.Generic;
using System.Linq;

namespace Blam.Halo2.Tags;

// Inside the namespace so these win over s&box's global vector types when this compiles into the mount.
using Vector3 = System.Numerics.Vector3;

/// <summary>
/// The object type at the start of every object tag, which also picks the global lighting element that
/// lights it (bit <c>type + 1</c> of the element's flags).
/// </summary>
public enum ObjectType : short
{
	Biped = 0,
	Vehicle = 1,
	Weapon = 2,
	Equipment = 3,
	Garbage = 4,
	Projectile = 5,
	Scenery = 6,
	Machine = 7,
	Control = 8,
	LightFixture = 9,
	SoundScenery = 10,
	Crate = 11,
	Creature = 12,
}

/// <summary>
/// The fields every Halo 2 object tag (<c>obje</c> and its children: <c>scen bipd vehi weap eqip mach ctrl
/// lifi bloc crea</c>...) starts with, as far as placing and drawing it needs them.
/// </summary>
public sealed class ObjectTag
{
	public static readonly TagClass Class = new( "obje" );

	public TagEntry Tag { get; }
	public ObjectType Type { get; }

	/// <summary>
	/// 2 = search the cardinal directions for a lightmap sample when the ray down misses.
	/// </summary>
	public int Flags { get; }

	public float BoundingRadius { get; }

	/// <summary>
	/// The bounding sphere's centre relative to the object's origin (object space). Halo lights an object
	/// from there.
	/// </summary>
	public Vector3 BoundingOffset { get; }

	/// <summary>
	/// The model variant used where a placement names none (may be null or empty).
	/// </summary>
	public string DefaultVariant { get; }

	/// <summary>
	/// The object's model (<c>hlmt</c>), or null.
	/// </summary>
	public TagEntry Model { get; }

	public bool SearchesCardinalDirections => (Flags & 2) != 0;

	/// <summary>
	/// The change colours (primary to quaternary, +0xAC, 0x10 each) the shaders' change colour masks tint with:
	/// each one's initial permutations. Their functions (colours driven by object functions) aren't read.
	/// </summary>
	public IReadOnlyList<IReadOnlyList<ChangeColorPermutation>> ChangeColors { get; }

	/// <summary>
	/// A unit's (biped's, vehicle's) weapons (+0x1C0), which MCC creates with it: the Banshee's gun, the Warthog's horn.
	/// Empty for other objects.
	/// </summary>
	public IReadOnlyList<TagEntry> UnitWeapons { get; }

	public ObjectTag( TagEntry tag )
	{
		Tag = tag;
		var cache = tag.Cache;
		var data = cache.TagData;
		var a = tag.Address;

		Type = (ObjectType)data.I16( a );
		Flags = data.U16( a + 2 );
		BoundingRadius = data.F32( a + 4 );
		BoundingOffset = data.Vec3( a + 8 );
		DefaultVariant = cache.GetString( data.U32( a + 0x30 ) );
		Model = cache.GetTagReference( a + 0x34 );

		// Initial permutations (0x20): weight, lower bound, upper bound (RGB floats), variant name.
		ChangeColors = data.Elements( a + 0xAC, 0x10 )
			.Select( e => (IReadOnlyList<ChangeColorPermutation>)data.Elements( e, 0x20 )
				.Select( p => new ChangeColorPermutation( data.F32( p ), data.Vec3( p + 4 ), data.Vec3( p + 0x10 ), cache.GetString( data.U32( p + 0x1C ) ) ) )
				.ToList() )
			.ToList();

		UnitWeapons = Type is ObjectType.Biped or ObjectType.Vehicle
			? data.Elements( a + 0x1C0, 8 ).Select( cache.GetTagReference ).Where( w => w is not null ).ToList()
			: [];
	}

	/// <summary>
	/// The object's change colours as MCC sets them when it creates the object: colours the
	/// placement sets (<paramref name="placed"/>, by bit) as they are; for each other one, a permutation for the model
	/// variant (or for any) picked by weight, then a point between its bounds, both from numbers MCC hashes from the
	/// object's position (so every object of a map gets the same colours every time); white for colours without a
	/// permutation. Null for colours the object doesn't have.
	/// </summary>
	/// <param name="variant">The name of the model variant the object shows (<see cref="ModelTag.FindVariant"/>), or null
	/// if its model has none: then only permutations for any variant count.</param>
	/// <param name="position">Where the object is created (its placement or spawn), world units.</param>
	/// <param name="placedMask">Bits of the colours the placement sets.</param>
	/// <param name="placed">The placement's colours (null: none).</param>
	public System.Numerics.Vector3?[] InitialChangeColors( string variant, System.Numerics.Vector3 position, int placedMask = 0, IReadOnlyList<System.Numerics.Vector3> placed = null )
	{
		var colors = new System.Numerics.Vector3?[4];
		for ( var i = 0; i < Math.Min( ChangeColors.Count, colors.Length ); i++ )
		{
			if ( (placedMask & (1 << i)) != 0 && placed is not null && i < placed.Count )
			{
				colors[i] = placed[i];
				continue;
			}

			// MCC's numbers, in single precision as it computes them.
			var pick = Fraction( position.X * 315.89312744140625f + position.Y * 587.1294555664062f + position.Z * 744.1241455078125f + i * 431.1289367675781f );
			var t = Fraction( i * 0.7121099829673767f + position.Y );

			var candidates = ChangeColors[i]
				.Where( p => string.IsNullOrEmpty( p.Variant ) || string.Equals( p.Variant, variant, StringComparison.OrdinalIgnoreCase ) )
				.ToList();
			var total = candidates.Sum( p => p.Weight );
			var color = System.Numerics.Vector3.One;
			if ( candidates.Count > 0 && total > 0 )
			{
				var target = pick * total;
				var sum = 0f;
				foreach ( var p in candidates )
				{
					sum += p.Weight;
					if ( p == candidates[^1] || sum >= target )
					{
						color = LerpHsv( p.Lower, p.Upper, t );
						break;
					}
				}
			}

			colors[i] = System.Numerics.Vector3.Clamp( color, System.Numerics.Vector3.Zero, System.Numerics.Vector3.One );
		}

		return colors;
	}

	// fmodf( |v|, 1 ).
	static float Fraction( float v ) => MathF.Abs( v ) % 1f;

	/// <summary>
	/// Halo's colour interpolation through hue, saturation and value, the hue taking the shorter way round
	/// (as MCC blends change colour bounds).
	/// </summary>
	public static System.Numerics.Vector3 LerpHsv( System.Numerics.Vector3 a, System.Numerics.Vector3 b, float t )
	{
		var (h0, s0, v0) = ToHsv( a );
		var (h1, s1, v1) = ToHsv( b );
		if ( Math.Abs( h0 - h1 ) > 0.5f )
		{
			if ( h1 > h0 )
				h0 += 1;
			else
				h1 += 1;
		}

		var h = h0 * (1 - t) + h1 * t;
		if ( h > 1 )
			h -= 1;
		var s = s0 * (1 - t) + s1 * t;
		var v = v0 * (1 - t) + v1 * t;
		if ( s == 0 )
			return new System.Numerics.Vector3( v );

		h *= 6;
		var sector = (int)MathF.Floor( h );
		var f = h - sector;
		var p = v * (1 - s);
		var q = v * (1 - s * f);
		var u = v * (1 - s * (1 - f));
		return sector switch
		{
			0 => new( v, u, p ),
			1 => new( q, v, p ),
			2 => new( p, v, u ),
			3 => new( p, q, v ),
			4 => new( u, p, v ),
			_ => new( v, p, q ),
		};
	}

	static (float H, float S, float V) ToHsv( System.Numerics.Vector3 c )
	{
		var max = Math.Max( c.X, Math.Max( c.Y, c.Z ) );
		var min = Math.Min( c.X, Math.Min( c.Y, c.Z ) );
		var d = max - min;
		var s = max == 0 ? 0 : d / max;
		if ( s == 0 )
			return (0, 0, max);

		var h = c.X == max ? (c.Y - c.Z) / d : c.Y == max ? (c.Z - c.X) / d + 2 : (c.X - c.Y) / d + 4;
		h /= 6;
		return (h < 0 ? h + 1 : h, s, max);
	}

	/// <summary>
	/// Whether a tag is an object: one of <c>obje</c>'s children.
	/// </summary>
	public static bool IsObject( TagEntry tag ) => tag is not null && tag.Address != 0 && tag.Is( Class );
}

/// <summary>
/// One way an object's change colour can start out: picked by <paramref name="Weight"/> among those for the model
/// variant (an empty <paramref name="Variant"/> fits any), then a colour between the bounds, blended through hue,
/// saturation and value (<see cref="ObjectTag.LerpHsv"/>).
/// </summary>
public sealed record ChangeColorPermutation( float Weight, System.Numerics.Vector3 Lower, System.Numerics.Vector3 Upper, string Variant );

/// <summary>
/// A permutation choice of a model variant's region, with how likely it is to be picked.
/// </summary>
public sealed record ModelVariantPermutation( string Name, float Probability );

public sealed record ModelVariantRegion( string Name, IReadOnlyList<ModelVariantPermutation> Permutations );

/// <summary>
/// An object a model variant attaches: <paramref name="Object"/> with its <paramref name="ChildMarker"/>
/// (or origin, if empty) on the parent's <paramref name="ParentMarker"/>. The Warthog's and Scorpion's
/// turrets are attached this way.
/// </summary>
public sealed record ModelVariantObject( string ParentMarker, string ChildMarker, TagEntry Object );

/// <summary>
/// A named model variant: which permutation(s) each region shows, and the objects it attaches.
/// </summary>
public sealed record ModelVariant( string Name, IReadOnlyList<ModelVariantRegion> Regions, IReadOnlyList<ModelVariantObject> Objects );

/// <summary>
/// A Halo 2 model (<c>hlmt</c>): what an object points at for its render model, collision, animations and
/// physics, plus the variants that pick the render model's permutations.
/// </summary>
public sealed class ModelTag
{
	public static readonly TagClass Class = new( "hlmt" );

	const uint VariantsBlock = 0x50;
	const int VariantSize = 0x38;
	const int VariantRegionSize = 0x14;
	const int VariantPermutationSize = 0x20;
	const int VariantObjectSize = 0x10;

	public TagEntry Tag { get; }
	public TagEntry RenderModel { get; }
	public TagEntry CollisionModel { get; }
	public TagEntry Animations { get; }
	public TagEntry PhysicsModel { get; }
	public IReadOnlyList<ModelVariant> Variants { get; }

	/// <summary>
	/// Whether the model has damage info (+0x60, "new damage info"): MCC treats scenery with it as dynamic and creates
	/// it with the scenario's other dynamic objects (<see cref="ObjectCreationOrder"/>).
	/// </summary>
	public bool HasDamageInfo { get; }

	/// <summary>The shader a camouflaged object draws with (+0x94, opaque\active_camo_opaque on the chief and elites).</summary>
	public TagEntry ActiveCamoShader { get; }

	/// <summary>
	/// The damage info's shield and overshield shaders, drawn over the whole model (+0x60 element: +0x7C shield
	/// damaged first person shader, +0x84 shield damaged shader, +0xE8 overshield first person shader, +0xF0
	/// overshield shader); null where there's none.
	/// </summary>
	public TagEntry ShieldShader { get; }
	public TagEntry ShieldFirstPersonShader { get; }
	public TagEntry OvershieldShader { get; }
	public TagEntry OvershieldFirstPersonShader { get; }

	public ModelTag( TagEntry tag )
	{
		Tag = tag;
		var cache = tag.Cache;
		var data = cache.TagData;
		var a = tag.Address;

		RenderModel = cache.GetTagReference( a );
		CollisionModel = cache.GetTagReference( a + 0x8 );
		Animations = cache.GetTagReference( a + 0x10 );
		PhysicsModel = cache.GetTagReference( a + 0x20 );
		HasDamageInfo = data.I32( a + 0x60 ) > 0;
		ActiveCamoShader = cache.GetTagReference( a + 0x94 );

		var damage = data.Elements( a + 0x60, 0x100 ).FirstOrDefault();
		if ( damage != 0 )
		{
			ShieldFirstPersonShader = cache.GetTagReference( damage + 0x7C );
			ShieldShader = cache.GetTagReference( damage + 0x84 );
			OvershieldFirstPersonShader = cache.GetTagReference( damage + 0xE8 );
			OvershieldShader = cache.GetTagReference( damage + 0xF0 );
		}

		Variants = data.Elements( a + VariantsBlock, VariantSize ).Select( v => new ModelVariant(
			cache.GetString( data.U32( v ) ),
			data.Elements( v + 0x14, VariantRegionSize ).Select( r => new ModelVariantRegion(
				cache.GetString( data.U32( r ) ),
				data.Elements( r + 8, VariantPermutationSize )
					.Select( p => new ModelVariantPermutation( cache.GetString( data.U32( p ) ), data.F32( p + 8 ) ) )
					.ToList() ) ).ToList(),
			data.Elements( v + 0x1C, VariantObjectSize )
				.Select( o => new ModelVariantObject( cache.GetString( data.U32( o ) ), cache.GetString( data.U32( o + 4 ) ), cache.GetTagReference( o + 8 ) ) )
				.Where( o => o.Object is not null )
				.ToList() ) ).ToList();
	}

	/// <summary>
	/// How many variants a model tag has, without reading them.
	/// </summary>
	public static int VariantCount( TagEntry tag ) => tag.Cache.TagData.I32( tag.Address + VariantsBlock );

	/// <summary>
	/// The variant an object shows: the one named (a placement's variant, else the object's default), else
	/// the first, else null (no variants: every region shows its first permutation).
	/// </summary>
	public ModelVariant FindVariant( string name )
	{
		if ( !string.IsNullOrEmpty( name ) )
		{
			var named = Variants.FirstOrDefault( v => string.Equals( v.Name, name, System.StringComparison.OrdinalIgnoreCase ) );
			if ( named is not null )
				return named;
		}

		return Variants.Count > 0 ? Variants[0] : null;
	}
}
