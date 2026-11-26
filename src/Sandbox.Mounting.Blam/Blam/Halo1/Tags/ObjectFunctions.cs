using System;
using System.Collections.Generic;
using System.Linq;

namespace Blam.Halo1.Tags;

// Inside the namespace so these win over s&box's global Vector2 when this compiles into the mount.
using Vector2 = System.Numerics.Vector2;

/// <summary>
/// One of an object's exported functions (A-D out): a periodic wave, optionally driven by the
/// object's inputs, that widgets, attachments, change colors and shaders can follow.
/// </summary>
/// <param name="Flags">0x1: invert.</param>
/// <param name="Period">Seconds per cycle of the wave.</param>
/// <param name="ScalePeriodBy">Input that scales the period (0 none, 1-4 A-D in, 5-8 A-D out).</param>
/// <param name="ScaleFunctionBy">Input the wave is multiplied by (same numbering); 0 means the wave runs on its own.</param>
/// <param name="BoundsMode">0 clip, 1 clip and normalize, 2 scale to fit.</param>
/// <param name="Bounds">Output range.</param>
public sealed record ObjectFunction( uint Flags, float Period, int ScalePeriodBy, AnimationFunction Wave, int ScaleFunctionBy, int BoundsMode, Vector2 Bounds, string Usage )
{
	public bool Inverted => (Flags & 0x1) != 0;

	/// <summary>
	/// A wave that only depends on time, so it can be played back without simulating the object.
	/// </summary>
	public bool IsFreeRunning => ScalePeriodBy == 0 && ScaleFunctionBy == 0;

	/// <summary>
	/// The output as a linear map of the wave (0..1): clamp( wave * Scale + Offset, Min, Max ).
	/// Covers inverting and the bounds modes; wobble, square waves, steps, sawtooths and the
	/// transition map aren't applied.
	/// </summary>
	public (float Scale, float Offset, float Min, float Max) Mapping
	{
		get
		{
			var scale = Inverted ? -1f : 1f;
			var offset = Inverted ? 1f : 0f;
			var lo = Bounds.X;
			var hi = Bounds.Y;

			switch ( BoundsMode )
			{
				case 2:
					return (scale * (hi - lo), lo + offset * (hi - lo), MathF.Min( lo, hi ), MathF.Max( lo, hi ));

				case 1 when hi > lo:
					return (scale / (hi - lo), (offset - lo) / (hi - lo), 0, 1);

				default:
					return (scale, offset, lo, hi);
			}
		}
	}
}

/// <summary>
/// The object functions every object tag can have: up to four values (A-D "out") that shaders,
/// lights and effects read, computed each tick from the object's inputs (A-D "in": its health, a
/// vehicle's speed, a weapon's heat...). Nothing runs them in a mounted scene, so this works out
/// where they sit at rest: an undamaged, unoccupied object that isn't moving or firing.
/// </summary>
/// <remarks>
/// Inputs: the shared object definition's at 0x108 (4 x 16 bit: 1 body vitality, 2 shield vitality,
/// 5 random constant, 18 alive, 19 compass, others damage), then the type's own, which win where
/// set: unit 0x198, biped 0x30C, vehicle 0x31C (motion, all 0 at rest), weapon 0x330 (1 heat,
/// 2/3 ammunition, 6 ready...).
///
/// Functions are a block at 0x158 (360 bytes): flags 0x0 (0x1 invert), function shape 0xA,
/// scale function by 0xC, wobble magnitude 0x14, square wave threshold 0x18, step count 0x1C,
/// map to (transition) 0x1E, add 0x22, scale result by 0x24, bounds mode 0x26 (clip, clip and
/// normalize, scale to fit), bounds 0x28/0x2C, scale by 0x38, inverse sawtooth/step 0x13C/0x140
/// (computed when the map was built), usage name 0x148. Function references are 1-4 A-D in and
/// 5-8 A-D out.
/// </remarks>
public static class ObjectFunctions
{
	public static IReadOnlyList<ObjectFunction> Read( CacheFile cache, TagEntry objectTag )
	{
		var data = cache.TagData;
		var list = new List<ObjectFunction>();
		foreach ( var f in data.Block( objectTag.Address + 0x158, 0x168 ) )
		{
			list.Add( new ObjectFunction(
				Flags: data.U32( f ),
				Period: data.F32( f + 0x4 ),
				ScalePeriodBy: data.U16( f + 0x8 ),
				Wave: (AnimationFunction)data.U16( f + 0xA ),
				ScaleFunctionBy: data.U16( f + 0xC ),
				BoundsMode: data.U16( f + 0x26 ),
				Bounds: data.Vec2( f + 0x28 ),
				Usage: data.CString( f + 0x148, 32 ) ) );
		}

		return list;
	}

	public static float[] RestValues( CacheFile cache, TagEntry objectTag ) => RestState( cache, objectTag ).Values;

	/// <summary>
	/// The rest values (see <see cref="RestValues"/>) and whether each function is active at rest.
	/// Halo deactivates a function whose output is at its lower bound (unless its flag 0x4 "does not
	/// deactivate below lower bound" is set), and an inactive function turns off what follows it:
	/// looping sounds go silent, looping effects, particle systems, contrails and light volumes
	/// stop. Functions the object doesn't have are 0 and inactive.
	/// </summary>
	public static (float[] Values, bool[] Active) RestState( CacheFile cache, TagEntry objectTag )
	{
		var data = cache.TagData;
		var a = objectTag.Address;

		var inputs = new float[4];
		for ( var i = 0; i < 4; i++ )
		{
			inputs[i] = data.U16( a + 0x108 + (uint)(i * 2) ) switch
			{
				1 or 2 or 18 => 1,      // body/shield vitality, alive
				5 or 19 => 0.5f,        // random constant, compass
				_ => 0,
			};
		}

		var typeInputs = objectTag.Class.ToString() switch
		{
			"bipd" => 0x30Cu,
			"vehi" => 0x31Cu,
			"weap" => 0x330u,
			_ => 0u,
		};

		if ( objectTag.Parent.ToString() == "unit" )
			ExportType( data, a + 0x198, inputs, _ => 0 );

		if ( typeInputs != 0 )
		{
			var weapon = objectTag.Class.ToString() == "weap";

			// A weapon at rest is ready and fully loaded; nothing else moves.
			ExportType( data, a + typeInputs, inputs, mode => weapon && mode is 2 or 3 or 6 ? 1 : 0 );
		}

		var outputs = new float[4];
		var active = new bool[4];
		var turnOffWith = new int[4];
		var index = 0;
		foreach ( var f in data.Block( a + 0x158, 0x168 ) )
		{
			if ( index >= 4 )
				break;

			float Get( int reference ) => reference switch
			{
				>= 1 and <= 4 => inputs[reference - 1],
				>= 5 and <= 8 => outputs[reference - 5],
				_ => 0,
			};

			// Shapes that move average out to the middle, as do wobbles.
			var value = (AnimationFunction)data.U16( f + 0xA ) switch
			{
				AnimationFunction.One => 1f,
				AnimationFunction.Zero => 0f,
				_ => 0.5f,
			};

			var scaleFunctionBy = data.U16( f + 0xC );
			if ( scaleFunctionBy != 0 )
				value *= Get( scaleFunctionBy );

			if ( (data.U32( f ) & 0x1) != 0 )
				value = 1 - value;

			var squareWave = data.F32( f + 0x18 );
			if ( squareWave != 0 )
				value = value > squareWave ? 1 : 0;

			var steps = data.I16( f + 0x1C );
			if ( steps > 1 )
				value = MathF.Floor( steps * value ) * data.F32( f + 0x140 );

			var sawtooth = data.F32( f + 0x13C );
			if ( sawtooth > 0 )
				value %= sawtooth;

			var add = data.U16( f + 0x22 );
			if ( add != 0 )
				value = MathF.Min( value + Get( add ), 1 );

			var scaleResultBy = data.U16( f + 0x24 );
			if ( scaleResultBy != 0 )
				value *= Get( scaleResultBy );

			// The transition curves only bend the middle of the range; 0 and 1 stay put.
			var output = Math.Clamp( value, 0, 1 );

			var scaleBy = data.F32( f + 0x38 );
			if ( scaleBy > 0 )
				output *= scaleBy;

			var mode = data.U16( f + 0x26 );
			var lower = data.F32( f + 0x28 );
			var upper = data.F32( f + 0x2C );
			if ( mode == 2 )
				output = lower + output * (upper - lower);

			// At (or under) the lower bound, before normalizing: inactive, unless flagged otherwise.
			active[index] = output > lower + 1e-6f || (data.U32( f ) & 0x4) != 0;

			if ( mode != 2 )
			{
				output = Math.Clamp( output, lower, Math.Max( upper, lower ) );
				if ( mode == 1 )
					output = upper > lower ? (output - lower) / (upper - lower) : 0;
			}

			turnOffWith[index] = data.I16( f + 0x36 );
			outputs[index++] = output;
		}

		// A function is also inactive while the function it turns off with is.
		for ( var pass = 0; pass < 4; pass++ )
		{
			for ( var i = 0; i < index; i++ )
			{
				var with = turnOffWith[i];
				if ( with >= 0 && (with >= active.Length || !active[with]) )
					active[i] = false;
			}
		}

		return (outputs, active);
	}

	/// <summary>
	/// Rest values A-D out of the first object in the map whose model uses each shader, for
	/// shaders that read object functions (materials are per shader, not per object). Objects
	/// with functions win over ones without, like a50's cinematic plasma rifle scenery, which
	/// shares the weapon's model.
	/// </summary>
	public static Dictionary<uint, float[]> RestValuesByShader( CacheFile cache )
	{
		var objectClass = new TagClass( "obje" );
		var result = new Dictionary<uint, float[]>();
		var withoutFunctions = new HashSet<uint>();

		foreach ( var tag in cache.Tags )
		{
			if ( !tag.Is( objectClass ) || tag.Class == objectClass )
				continue;

			var modelTag = ObjectTags.GetObjectModel( cache, tag );
			if ( modelTag is null || modelTag.Class != TagClasses.GbxModel )
				continue;

			var hasFunctions = cache.TagData.I32( tag.Address + 0x158 ) > 0;
			float[] values = null;

			// Halo draws a weapon's first person model with the weapon's function values.
			var shaders = new GbxModelTag( cache, modelTag ).Shaders.AsEnumerable();
			if ( tag.Class == FirstPersonWeapon.WeaponClass && cache.GetTagReference( cache.TagData, tag.Address + 0x45C ) is { } firstPerson && firstPerson.Class == TagClasses.GbxModel )
				shaders = shaders.Concat( new GbxModelTag( cache, firstPerson ).Shaders );

			foreach ( var shader in shaders )
			{
				if ( shader.Shader is null || (result.ContainsKey( shader.Shader.Id ) && !(hasFunctions && withoutFunctions.Contains( shader.Shader.Id ))) )
					continue;

				result[shader.Shader.Id] = values ??= RestValues( cache, tag );
				if ( hasFunctions )
					withoutFunctions.Remove( shader.Shader.Id );
				else
					withoutFunctions.Add( shader.Shader.Id );
			}
		}

		return result;
	}

	static readonly System.Runtime.CompilerServices.ConditionalWeakTable<CacheFile, Dictionary<uint, float[]>> _restValuesByShader = new();

	/// <summary>
	/// <see cref="RestValuesByShader"/> for one shader, worked out once per map. Null if no object uses it.
	/// </summary>
	public static float[] RestValuesForShader( CacheFile cache, TagEntry shader ) =>
		_restValuesByShader.GetValue( cache, RestValuesByShader ).GetValueOrDefault( shader.Id );

	/// <summary>
	/// The value of an A-D out function source (none is 1, the default every shader uses).
	/// </summary>
	public static float Get( float[] values, ChangeColorSource source ) =>
		source == ChangeColorSource.None || values is null ? 1 : values[(int)source - 1];

	static void ExportType( IO.MemoryRegion data, uint address, float[] inputs, Func<int, float> rest )
	{
		for ( var i = 0; i < 4; i++ )
		{
			var mode = data.U16( address + (uint)(i * 2) );
			if ( mode != 0 )
				inputs[i] = rest( mode );
		}
	}
}
