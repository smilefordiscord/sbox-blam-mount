using System;
using System.Collections.Generic;

namespace Blam.Halo1.Tags;

// Inside the namespace so these win over s&box's global Vector2/Vector3 when this compiles into the mount.
using Vector3 = System.Numerics.Vector3;

/// <summary>
/// One weighted choice of color range for a change color.
/// </summary>
public sealed record ChangeColorPermutation( float Weight, Vector3 Lower, Vector3 Upper );

/// <summary>
/// One of an object's change colors (A-D).
/// </summary>
/// <param name="DarkenBy">Object function (1-4 A-D in, 5-8 A-D out, 0 none) that darkens the color.</param>
/// <param name="ScaleBy">Object function that picks the point between the bounds, instead of chance.</param>
/// <param name="BlendInHsv">Interpolate between the bounds in HSV rather than RGB.</param>
/// <param name="MoreColors">With <paramref name="BlendInHsv"/>, go the long way around the hue wheel.</param>
/// <param name="Permutations">Weighted color ranges; when empty, the range is <paramref name="Lower"/> to <paramref name="Upper"/>.</param>
public sealed record ChangeColor( int DarkenBy, int ScaleBy, bool BlendInHsv, bool MoreColors, Vector3 Lower, Vector3 Upper, IReadOnlyList<ChangeColorPermutation> Permutations );

/// <summary>
/// Reader for the change colors every object tag can have: up to four colors (A-D) that shaders
/// tint parts of the object with, e.g. Covenant purple or the Warthog's paint.
/// </summary>
/// <remarks>
/// The block is at 0x164 of the shared object definition (44 bytes each: darken by and scale by
/// object functions, flags 0x4 with 0x1 blend in HSV and 0x2 more colors, lower and upper bound
/// 0x8/0x14, permutations 0x20). Permutations are 28 bytes: weight, lower and upper bound.
///
/// When an object is created the game picks a permutation by weight and a random point between
/// its bounds, for each change color. Scenario placements don't store colors; single player
/// characters get theirs from their actor variant and multiplayer players from their team instead.
/// </remarks>
public static class ObjectChangeColors
{
	public static IReadOnlyList<ChangeColor> Read( CacheFile cache, TagEntry objectTag )
	{
		var data = cache.TagData;
		var colors = new List<ChangeColor>();

		foreach ( var entry in data.Block( objectTag.Address + 0x164, 0x2C ) )
		{
			var permutations = new List<ChangeColorPermutation>();
			foreach ( var permutation in data.Block( entry + 0x20, 0x1C ) )
				permutations.Add( new ChangeColorPermutation( data.F32( permutation ), data.Vec3( permutation + 0x4 ), data.Vec3( permutation + 0x10 ) ) );

			var flags = data.U32( entry + 0x4 );
			colors.Add( new ChangeColor( data.U16( entry ), data.U16( entry + 0x2 ), (flags & 0x1) != 0, (flags & 0x2) != 0, data.Vec3( entry + 0x8 ), data.Vec3( entry + 0x14 ), permutations ) );
		}

		return colors;
	}

	/// <summary>
	/// Picks a color the way the game does when it creates the object: a permutation by weight,
	/// then a point between its bounds. <paramref name="random"/> supplies the two numbers in [0, 1),
	/// so a placement can always get the same color.
	/// </summary>
	/// <remarks>
	/// A color scaled by an object function (door lock lights going from green to red, a
	/// Banshee's glow) sits where that function puts it rather than at random. Without the game
	/// running the functions are at rest, at 0, so it's the lower bound.
	/// </remarks>
	public static Vector3 Pick( ChangeColor color, Func<float> random )
	{
		var lower = color.Lower;
		var upper = color.Upper;

		var total = 0f;
		foreach ( var p in color.Permutations )
			total += MathF.Max( p.Weight, 0 );

		if ( color.Permutations.Count > 0 )
		{
			// Unweighted permutations are equally likely.
			var pick = random() * (total > 0 ? total : color.Permutations.Count);
			var chosen = color.Permutations[^1];
			foreach ( var p in color.Permutations )
			{
				pick -= total > 0 ? MathF.Max( p.Weight, 0 ) : 1;
				if ( pick < 0 ) { chosen = p; break; }
			}

			lower = chosen.Lower;
			upper = chosen.Upper;
		}

		var t = random();
		if ( color.ScaleBy != 0 )
			t = 0;

		return color.BlendInHsv ? LerpHsv( lower, upper, t, color.MoreColors ) : Vector3.Lerp( lower, upper, t );
	}

	// The color Halo gives an object at this position (object_choose_random_change_colors: a hash of the position, weights are cumulative, always blended in HSV), or null without permutations.
	public static Vector3? AtPosition( ChangeColor color, int index, Vector3 position )
	{
		var weight = MathF.Abs( position.X * 315.89313f + position.Y * 587.12946f + position.Z * 744.12415f + index * 431.12894f ) % 1f;
		foreach ( var p in color.Permutations )
		{
			if ( weight > p.Weight )
				continue;
			var t = color.ScaleBy != 0 ? 0 : (MathF.Abs( position.Y ) + index * 0.71211f) % 1f;
			return LerpHsv( p.Lower, p.Upper, t, longWay: false );
		}
		return null;
	}

	/// <summary>
	/// A typical color: the middle permutation by weight, the middle of its range.
	/// </summary>
	public static Vector3 Typical( ChangeColor color ) => Pick( color, () => 0.5f );

	/// <summary>
	/// Object change colors live in object tags, but shader_model tags (and so materials) are
	/// shared by models. This finds, for each model shader in the map, the typical colors A-D of
	/// the first object whose model uses it. A color the object doesn't have is white.
	/// </summary>
	public static Dictionary<uint, Vector3[]> TypicalColorsByShader( CacheFile cache ) => ColorsByShader( cache, steadyOnly: false );

	/// <summary>
	/// <see cref="TypicalColorsByShader"/>, but a color is white wherever any object using the
	/// shader has it follow an object function (scaled or darkened by one): those depend on the
	/// object's state (a hologram's power, a door's lock), at rest they'd mostly be their black lower
	/// bound, and one material can't give each object its own. b30's holo control shader is shared
	/// by the holo panels (a steady black A) and the holo controls (A scaled by a function).
	/// </summary>
	public static Dictionary<uint, Vector3[]> SteadyColorsByShader( CacheFile cache ) => ColorsByShader( cache, steadyOnly: true );

	static Dictionary<uint, Vector3[]> ColorsByShader( CacheFile cache, bool steadyOnly )
	{
		var objectClass = new TagClass( "obje" );
		var result = new Dictionary<uint, Vector3[]>();

		foreach ( var tag in cache.Tags )
		{
			if ( !tag.Is( objectClass ) || tag.Class == objectClass )
				continue;

			var modelTag = ObjectTags.GetObjectModel( cache, tag );
			if ( modelTag is null || modelTag.Class != TagClasses.GbxModel )
				continue;

			var colors = Read( cache, tag );
			if ( colors.Count == 0 )
				continue;

			var typical = new Vector3[4];
			for ( var i = 0; i < 4; i++ )
				typical[i] = i < colors.Count ? Typical( colors[i] ) : Vector3.One;

			foreach ( var shader in new GbxModelTag( cache, modelTag ).Shaders )
			{
				if ( shader.Shader is null )
					continue;

				if ( !steadyOnly )
				{
					result.TryAdd( shader.Shader.Id, typical );
					continue;
				}

				if ( !result.TryGetValue( shader.Shader.Id, out var shaderColors ) )
					result[shader.Shader.Id] = shaderColors = (Vector3[])typical.Clone();

				for ( var i = 0; i < colors.Count && i < 4; i++ )
				{
					if ( colors[i].ScaleBy != 0 || colors[i].DarkenBy != 0 )
						shaderColors[i] = Vector3.One;
				}
			}
		}

		return result;
	}

	static Vector3 LerpHsv( Vector3 a, Vector3 b, float t, bool longWay )
	{
		var ha = ToHsv( a );
		var hb = ToHsv( b );

		// Hue is a fraction of the wheel. Normally take the short way round, "more colors" the long way.
		var delta = hb.X - ha.X;
		var shortWay = delta - MathF.Round( delta );
		if ( longWay )
			delta = shortWay > 0 ? shortWay - 1 : shortWay + 1;
		else
			delta = shortWay;

		var h = ha.X + delta * t;
		h -= MathF.Floor( h );
		return FromHsv( new Vector3( h, ha.Y + (hb.Y - ha.Y) * t, ha.Z + (hb.Z - ha.Z) * t ) );
	}

	static Vector3 ToHsv( Vector3 c )
	{
		var max = MathF.Max( c.X, MathF.Max( c.Y, c.Z ) );
		var min = MathF.Min( c.X, MathF.Min( c.Y, c.Z ) );
		var d = max - min;

		float h = 0;
		if ( d > 0 )
		{
			if ( max == c.X ) h = (c.Y - c.Z) / d;
			else if ( max == c.Y ) h = (c.Z - c.X) / d + 2;
			else h = (c.X - c.Y) / d + 4;
			h /= 6;
			h -= MathF.Floor( h );
		}

		return new Vector3( h, max > 0 ? d / max : 0, max );
	}

	static Vector3 FromHsv( Vector3 hsv )
	{
		var (h, s, v) = (hsv.X * 6, hsv.Y, hsv.Z);
		var i = (int)MathF.Floor( h ) % 6;
		var f = h - MathF.Floor( h );
		var p = v * (1 - s);
		var q = v * (1 - s * f);
		var u = v * (1 - s * (1 - f));

		return i switch
		{
			0 => new Vector3( v, u, p ),
			1 => new Vector3( q, v, p ),
			2 => new Vector3( p, v, u ),
			3 => new Vector3( p, q, v ),
			4 => new Vector3( u, p, v ),
			_ => new Vector3( v, p, q ),
		};
	}
}
