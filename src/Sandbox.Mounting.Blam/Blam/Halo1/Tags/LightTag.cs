using System;

namespace Blam.Halo1.Tags;

// Inside the namespace so these win over s&box's global Vector2/Vector3 when this compiles into the mount.
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;

[Flags]
public enum LightFlags : uint
{
	/// <summary>
	/// Lights the level geometry as well as objects. Lights without it only light objects.
	/// </summary>
	Dynamic = 0x1,
	NoSpecular = 0x2,
	DontLightOwnObject = 0x4,
	SupersizeInFirstPerson = 0x8,
	FirstPersonFlashlight = 0x10,
	DontFadeActiveCamouflage = 0x20,
}

/// <summary>
/// Reader for <c>ligh</c> (light) tags: lights attached to objects, from light fixtures and
/// glowing panels to the lights on vehicles and the flashlight.
/// </summary>
/// <remarks>
/// Offsets: flags 0x0, radius 0x4 (world units; 0 makes it lens flare only), radius modifier
/// bounds 0x8/0xC, falloff and cutoff angles 0x10/0x14 (radians; 180 degrees means a point
/// light), color lower/upper bounds 0x38/0x48 (ARGB floats), gel (primary cube map) 0x64, lens
/// flare 0xAC. Lights created by
/// effects last for the duration at 0xF4 (in ticks, 30 a second) and fade from the upper color
/// bound to the lower one by the falloff function at 0xFA (0 linear, 1 early, 2 very early, 3
/// late, 4 very late, 5 cosine).
///
/// A light an object carries follows its attachment's scale function (an A-D out, 1 without
/// one): its color goes from the lower bound to the upper by the value (<see cref="ColorAt"/>),
/// and a dynamic light's radius is the radius times the modifier bounds interpolated the same way
/// (<see cref="RadiusAt"/>). A light whose color comes out black is neither drawn nor gives a
/// lens flare.
///
/// On the level Halo shades a light by its gel (a cube map looked up by the direction from the
/// light) and its distance attenuation, with no cone; on objects by the cone without the gel.
/// Every directional light with a gel (the flashlights, the warthog's and scorpion's headlights)
/// keeps its gel inside the cube map's forward face, so it is a spot light whose cone is that
/// face (<see cref="SpotCutoffAngle"/>), shaped by the face as its cookie, whatever its cutoff.
/// </remarks>
public sealed class LightTag
{
	public LightFlags Flags { get; }
	public float Radius { get; }
	public Vector2 RadiusModifier { get; }
	public float FalloffAngle { get; }
	public float CutoffAngle { get; }
	public Vector3 ColorLower { get; }
	public Vector3 ColorUpper { get; }

	/// <summary>
	/// The color bounds' alphas: how much of the interpolated color is kept, the rest being the
	/// attachment's change color (white without one). Both zero: the color is multiplied by it.
	/// </summary>
	public Vector2 ColorAlpha { get; }

	public TagEntry LensFlare { get; }

	/// <summary>The gel: a cube map the light's color is multiplied by, looked up by the direction from it.</summary>
	public TagEntry Gel { get; }

	public float Duration { get; }
	public int FalloffFunction { get; }

	/// <summary>
	/// How long a light created by an effect lasts, in seconds.
	/// </summary>
	public float EffectSeconds => Duration / 30;

	public bool IsDynamic => Flags.HasFlag( LightFlags.Dynamic );

	/// <summary>
	/// A spot light: the cutoff angle is narrower than a hemisphere, or it has a gel and isn't
	/// lit all round.
	/// </summary>
	public bool IsSpot => CutoffAngle > 0 && (CutoffAngle < MathF.PI * 0.5f || Gel is not null && CutoffAngle < MathF.PI);

	/// <summary>
	/// A spot light's cone (radians): with a gel, the gel's forward face (45 degrees) at most.
	/// </summary>
	public float SpotCutoffAngle => Gel is not null ? MathF.Min( CutoffAngle, MathF.PI * 0.25f ) : CutoffAngle;

	/// <summary>
	/// The light's color at full strength. Halo picks a color between the two bounds with a
	/// function (flicker, blink...); designers often only fill in one of them.
	/// </summary>
	public Vector3 Color => Vector3.Max( ColorLower, ColorUpper );

	public LightTag( CacheFile cache, TagEntry tag )
	{
		var data = cache.TagData;
		var a = tag.Address;

		Flags = (LightFlags)data.U32( a );
		Radius = data.F32( a + 0x4 );
		RadiusModifier = data.Vec2( a + 0x8 );
		FalloffAngle = data.F32( a + 0x10 );
		CutoffAngle = data.F32( a + 0x14 );
		ColorLower = data.Vec3( a + 0x3C );
		ColorUpper = data.Vec3( a + 0x4C );
		ColorAlpha = new Vector2( data.F32( a + 0x38 ), data.F32( a + 0x48 ) );
		Gel = cache.GetTagReference( data, a + 0x64 );
		LensFlare = cache.GetTagReference( data, a + 0xAC );
		Duration = data.F32( a + 0xF4 );
		FalloffFunction = data.U16( a + 0xFA );
	}

	/// <summary>
	/// The light's color (Halo's 0-1 units) for an attachment scale function value, with white as
	/// the change color, the way Halo works it out each frame for the lights objects carry.
	/// </summary>
	public Vector3 ColorAt( float value )
	{
		value = Math.Clamp( value, 0, 1 );
		var color = Vector3.Lerp( ColorLower, ColorUpper, value );
		if ( ColorAlpha.X > 1e-6f || ColorAlpha.Y > 1e-6f )
		{
			var alpha = ColorAlpha.X + (ColorAlpha.Y - ColorAlpha.X) * value;
			color = color * alpha + Vector3.One * (1 - alpha);
		}

		return Vector3.Clamp( color, Vector3.Zero, Vector3.One );
	}

	/// <summary>
	/// The light's radius (world units) for an attachment scale function value: dynamic lights
	/// scale it by the radius modifier bounds; Halo leaves the others at the radius.
	/// </summary>
	public float RadiusAt( float value ) =>
		IsDynamic ? Radius * (RadiusModifier.X + (RadiusModifier.Y - RadiusModifier.X) * Math.Clamp( value, 0, 1 )) : Radius;

	/// <summary>
	/// How far an effect light has faded (0 at full color, 1 at the lower bound) after a fraction
	/// of its duration, by its falloff function.
	/// </summary>
	public float Falloff( float t )
	{
		t = Math.Clamp( t, 0, 1 );
		return FalloffFunction switch
		{
			1 => MathF.Sqrt( t ),
			2 => MathF.Sqrt( MathF.Sqrt( t ) ),
			3 => t * t,
			4 => t * t * t * t,
			5 => (MathF.Sin( t * MathF.PI - MathF.PI / 2 ) + 1) / 2,
			_ => t,
		};
	}
}
