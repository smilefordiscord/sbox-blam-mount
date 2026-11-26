using System;
using System.Collections.Generic;
using System.Linq;

namespace Blam.Halo3.Tags;

// Inside the namespace so these win over s&box's global vector types when this compiles into the mount.
using Vector3 = System.Numerics.Vector3;

// One emitter property (0x20): u8 input, u8 range input, u8 output modifier, u8 its input, function data ref (+0x4: u8 type, u8 flags with bit 0 "ranged", u8 colour mode, then the output range as floats at +0x4/+0x8), runtime constant (+0x18). A constant function gives min, or lerp( min, max, range input ) when ranged.
public readonly record struct WeatherProperty( int Input, int Range, int Type, bool Ranged, float Min, float Max, float Constant )
{
	public const int ConstantType = 1;

	public static WeatherProperty Read( TagView view, int field )
	{
		var f = view.Data( field + 4 );
		if ( f.Length < 12 )
			return new WeatherProperty( view.U8( field ), view.U8( field + 1 ), -1, false, view.F32( field + 0x18 ), view.F32( field + 0x18 ), view.F32( field + 0x18 ) );
		var min = BitConverter.ToSingle( f.Slice( 4, 4 ) );
		var max = BitConverter.ToSingle( f.Slice( 8, 4 ) );
		return new WeatherProperty( view.U8( field ), view.U8( field + 1 ), f[0], (f[1] & 1) != 0, Finite( min ), Finite( max ), view.F32( field + 0x18 ) );
	}

	static float Finite( float v ) => float.IsFinite( v ) ? v : 0;

	public bool IsConstant => Type == ConstantType;

	// The value for a 0-1 random draw: ranged constants spread over their range, plain constants take min, curves their output maximum (their shape over time is not evaluated).
	public float Sample( float random ) => IsConstant ? (Ranged ? Min + (Max - Min) * random : Min) : MathF.Max( Min, Max );

	public float Largest => IsConstant && !Ranged ? Min : MathF.Max( Min, Max );

	public override string ToString() => IsConstant ? (Ranged ? $"{Min:0.####}..{Max:0.####}" : $"{Min:0.####}") : $"curve{Type} {Min:0.####}..{Max:0.####}";
}

// One effect particle system emitter (0x2F0, Assembly Halo3 effe layout; verified on the weather effects of all 38 maps).
public sealed record WeatherEmitter( string Name, int Shape, WeatherProperty EmissionRadius, WeatherProperty EmissionAngle, WeatherProperty MaximumCount, WeatherProperty Rate, WeatherProperty Lifespan,
	WeatherProperty SelfAcceleration, Vector3 AccelerationFrom, Vector3 AccelerationTo, WeatherProperty InitialVelocity, WeatherProperty RotationRate, WeatherProperty Size, WeatherProperty Scale, WeatherProperty Alpha,
	uint MovementFlags, float Gravity, float Drag )
{
	public static WeatherEmitter Read( TagView e )
	{
		// Physics movement (type 0) parameters by id: 0 gravity scale, 1 drag per 30 Hz tick (read from the weather tags' values, not documented).
		float gravity = 0, drag = 0;
		foreach ( var movement in e.Block( 0x17C, 0x18 ) )
		{
			if ( movement.U16( 0 ) != 0 )
				continue;
			foreach ( var p in movement.Block( 4, 0x24 ) )
			{
				var value = WeatherProperty.Read( p, 4 ).Largest;
				if ( p.I32( 0 ) == 0 )
					gravity = value;
				else if ( p.I32( 0 ) == 1 )
					drag = value;
			}
		}

		return new WeatherEmitter( e.StringId( 0 ), e.U8( 4 ),
			WeatherProperty.Read( e, 0x88 ), WeatherProperty.Read( e, 0xA8 ), WeatherProperty.Read( e, 0x108 ), WeatherProperty.Read( e, 0x128 ), WeatherProperty.Read( e, 0x148 ),
			WeatherProperty.Read( e, 0x188 ), e.Vec3( 0x1A8 ), e.Vec3( 0x1B4 ), WeatherProperty.Read( e, 0x1C0 ), WeatherProperty.Read( e, 0x200 ),
			WeatherProperty.Read( e, 0x220 ), WeatherProperty.Read( e, 0x240 ), WeatherProperty.Read( e, 0x280 ),
			e.U32( 0x178 ), gravity, drag );
	}
}

// A weather effect particle system (0x5C) with its particle (prt3: billboard style +0x14, appearance flags +0x10, motion blur aspect scale +0x38, render method +0x3C).
public sealed record WeatherParticleSystem( TagEntry Particle, int Flags, float NearFadeRange, float NearFadeCutoff, int Billboard, uint Appearance, float MotionBlurAspect, IReadOnlyList<WeatherEmitter> Emitters )
{
	// Billboard styles: 0 screen facing, 1 camera facing, 2 parallel to direction, 3 perpendicular, 4 vertical, 5 horizontal.
	public bool IsStreak => Billboard == 2;
}

// A weather effect (effe): every particle system of its events.
public sealed class WeatherEffectTag
{
	public TagEntry Tag { get; }
	public IReadOnlyList<WeatherParticleSystem> Systems { get; }

	public WeatherEffectTag( TagEntry tag )
	{
		Tag = tag;
		var systems = new List<WeatherParticleSystem>();
		foreach ( var ev in tag.Data.Block( 0x2C, 0x44 ) )
		{
			foreach ( var ps in ev.Block( 0x38, 0x5C ) )
			{
				var particle = ps.Reference( 4 );
				var prt3 = particle is null ? default : particle.Data;
				var emitters = ps.Block( 0x4C, 0x2F0 ).Select( WeatherEmitter.Read ).ToArray();
				systems.Add( new WeatherParticleSystem( particle, ps.U16( 0x22 ), ps.F32( 0x28 ), ps.F32( 0x2C ),
					prt3.IsNull ? 1 : prt3.U16( 0x14 ), prt3.IsNull ? 0 : prt3.U32( 0x10 ), prt3.IsNull ? 0 : prt3.F32( 0x38 ), emitters ) );
			}
		}
		Systems = systems;
	}
}
