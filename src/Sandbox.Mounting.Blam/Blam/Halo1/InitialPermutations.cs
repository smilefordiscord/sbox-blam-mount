using System;
using System.Collections.Generic;
using System.Linq;
using Blam.Halo1.Tags;
using Blam.IO;

namespace Blam.Halo1;

// Inside the namespace so this wins over s&box's global Vector3 when this compiles into the mount.
using Vector3 = System.Numerics.Vector3;

// Halo CE's global random stream (random_math.c): seed = seed * 1664525 + 1013904223, the top 16 bits are the number.
public sealed class HaloRandom( uint seed )
{
	public uint Seed = seed;

	public ushort Next() { Seed = Seed * 1664525u + 1013904223u; return (ushort)(Seed >> 16); }
	public int Range( int lower, int upper ) => lower + (int)(((uint)(upper - lower) * Next()) >> 16);

	// real_seed_random as Halo CE computes it, times 1/65535 in single precision.
	public float Real() => Next() * (1f / 65535f);
	public float Real( float lower, float upper ) => Real() * (upper - lower) + lower;
}

// The region permutations Halo gives the objects it places when a campaign map starts, drawn from its global random stream (MCC seeds it 0xDEADBEEF, as game_options_new does).
public static class InitialPermutations
{
	public const uint CampaignSeed = 0xDEADBEEF;

	// Placement block, palette block and placement size, in object_types_place_all's type order; scenery and light fixtures go last, and only those in the starting BSP.
	static readonly (string Kind, uint Placements, uint Palette, int Size, bool OnBsp)[] Types =
	[
		("biped", 0x228, 0x234, 0x78, false),
		("vehicle", 0x240, 0x24C, 0x78, false),
		("weapon", 0x270, 0x27C, 0x5C, false),
		("equipment", 0x258, 0x264, 0x28, false),
		("machine", 0x294, 0x2A0, 0x40, false),
		("control", 0x2AC, 0x2B8, 0x40, false),
		("sound scenery", 0x2DC, 0x2E8, 0x28, false),
		("scenery", 0x210, 0x21C, 0x48, true),
		("light fixture", 0x2C4, 0x2D0, 0x58, true),
	];

	public sealed record Result( Dictionary<(string Kind, int Index), int[]> Permutations, List<string> Warnings, uint FinalSeed );

	// insideBsp tells whether a point is inside the starting BSP (BspClusterTree.FindCluster != -1); the game always starts in BSP 0.
	// dialogueCounter is Halo CE's unit_dialogue_determine_variant counter: 0 on startup, never reset, one more for every unit made with variant 0 since.
	public static Result Predict( CacheFile cache, TagEntry scenarioTag, Func<Vector3, bool> insideBsp, uint seed = CampaignSeed, int dialogueCounter = 0 )
	{
		var data = cache.TagData;
		var placing = new Placing( cache, new HaloRandom( seed ), dialogueCounter );
		var result = new Dictionary<(string, int), int[]>();

		// wind_variance_initialize (scenario_initialize_for_new_map): a random_direction3d per axis of 8 control points.
		for ( var i = 0; i < 24; i++ )
			placing.Random.Next();

		var named = new HashSet<int>();
		foreach ( var (kind, placements, palette, size, onBsp) in Types )
		{
			var paletteTags = data.Block( scenarioTag.Address + palette, 0x30 ).Select( e => cache.GetTagReference( data, e ) ).ToList();
			var index = -1;
			foreach ( var entry in data.Block( scenarioTag.Address + placements, size ) )
			{
				index++;
				var paletteIndex = data.I16( entry );
				var nameIndex = data.I16( entry + 0x2 );
				if ( paletteIndex < 0 || paletteIndex >= paletteTags.Count || (data.U16( entry + 0x4 ) & 1) != 0 )
					continue;
				if ( nameIndex >= 0 && named.Contains( nameIndex ) )
					continue;
				var tag = paletteTags[paletteIndex];
				if ( tag is null )
					continue;

				if ( onBsp )
				{
					var position = data.Vec3( entry + 0x8 );
					if ( !insideBsp( position ) && !insideBsp( Transform( data.Vec3( entry + 0x14 ), position, data.Vec3( tag.Address + 0x8 ) ) ) )
						continue;
				}

				var what = $"{kind} #{index}";
				var perms = placing.ObjectNew( tag, data.I16( entry + 0x6 ), what );
				if ( perms is null )
					continue;
				if ( nameIndex >= 0 )
					named.Add( nameIndex );
				result[(kind, index)] = perms;

				if ( kind is "biped" or "vehicle" && (data.U32( entry + 0x4C ) & 1) != 0 )
					placing.UnitPlaceDead( tag, what );
			}
		}

		return new Result( result, placing.Warnings, placing.Random.Seed );
	}

	// What objects_place does to the stream, object by object.
	sealed class Placing( CacheFile cache, HaloRandom random, int dialogueCounter )
	{
		const int MaximumEffects = 256;

		readonly MemoryRegion _data = cache.TagData;
		int _dialogueCounter = dialogueCounter;
		int _effects;
		int _deterministicEffects;
		int _deterministicSlots;

		public HaloRandom Random { get; } = random;
		public List<string> Warnings { get; } = [];

		// object_new's draws in order: the type's own (a scenery's random animation; a unit's feign death chance and its starting weapons), the region permutations, the effect attachments, then the creation effect.
		public int[] ObjectNew( TagEntry tag, int variant, string what )
		{
			var a = tag.Address;
			var type = _data.I16( a );
			var animations = ObjectTags.GetObjectAnimations( cache, tag );
			var animationCount = animations is null ? 0 : _data.I32( animations.Address + 0x74 );

			List<int> dialogueVariants = null;
			if ( type is 0 or 1 )
			{
				// unit_new fails without an animation graph, and the object with it.
				if ( animations is null )
					return null;

				// unit_dialogue_determine_variant: a unit placed with variant 0 takes the next of its first 16 dialogue variants below 100.
				if ( variant == 0 )
				{
					dialogueVariants = _data.Block( a + 0x2B4, 0x18 ).Select( d => (int)_data.I16( d ) ).Where( v => v < 100 ).Take( 16 ).ToList();
					if ( dialogueVariants.Count > 0 )
						variant = dialogueVariants[_dialogueCounter++ % dialogueVariants.Count];
				}

				if ( _data.F32( a + 0x22C ) > 0 && _data.F32( a + 0x230 ) > 0 && _data.F32( a + 0x244 ) > 0 )
					Random.Next();

				// unit_add_initial_weapons: each weapon is made whole (its own attachments too) before the unit chooses its permutations.
				foreach ( var weapon in _data.Block( a + 0x2D8, 0x24 ) )
				{
					if ( cache.GetTagReference( _data, weapon ) is { } weaponTag )
						ObjectNew( weaponTag, 0, $"{what} starting weapon" );
				}
			}
			else if ( type == 6 && animationCount > 0 )
				Random.Next();

			// The counter runs for the whole session, so say where the variant it picks changes anything.
			if ( dialogueVariants is not null && dialogueVariants.Distinct().Select( v => { var r = new HaloRandom( Random.Seed ); return (string.Join( ",", ChooseRegionPermutations( cache, tag, v, r ) ), r.Seed); } ).Distinct().Count() > 1 )
				Warnings.Add( $"{what} {tag.Path}: dialogue variants [{string.Join( ",", dialogueVariants )}] follow halo1.dll's unit counter (assumed {_dialogueCounter - 1})" );

			var perms = ChooseRegionPermutations( cache, tag, variant, Random );

			// attachments_new: an effect attachment is a looping effect on the object, scaled by function primary - 1.
			foreach ( var attachment in _data.Block( a + 0x140, 0x48 ) )
			{
				if ( cache.GetTagReference( _data, attachment ) is { } effect && effect.Class == EffectTagClasses.Effect )
					NewEffect( effect, tag, true, _data.I16( attachment + 0x30 ) - 1, $"{what} {tag.Path}" );
			}

			NewEffect( cache.GetTagReference( _data, a + 0xA0 ), tag, false, -1, $"{what} {tag.Path}" );
			return perms;
		}

		// unit_place for a unit placed dead: unit_ping_animation picks a soft kill from behind (unit damage animation 121) and, if there is one, a random frame of it, then object_deplete_shield makes the shield depleted effect.
		public void UnitPlaceDead( TagEntry tag, string what )
		{
			var animations = ObjectTags.GetObjectAnimations( cache, tag );
			var damage = _data.Block( animations.Address + 0x3C, 2 ).ToList();
			var animation = 121 < damage.Count ? _data.I16( damage[121] ) : -1;
			var pick = Random.Real();
			var graph = _data.Block( animations.Address + 0x74, 0xB4 ).ToList();
			while ( animation >= 0 && animation < graph.Count && _data.F32( graph[animation] + 0x44 ) < pick )
				animation = _data.I16( graph[animation] + 0x38 );

			if ( animation < 0 || animation >= graph.Count )
			{
				if ( (_data.U32( tag.Address + 0x17C ) & 2) != 0 )
					Warnings.Add( $"{what} {tag.Path} is placed dead without a kill animation and is destroyed after dying: unit_destroy's draws aren't counted" );
				return;
			}

			Random.Next();
			if ( ObjectTags.GetObjectCollisionModel( cache, tag ) is { } collision )
				NewEffect( cache.GetTagReference( _data, collision.Address + 0x198 ), tag, false, -1, $"{what} {tag.Path}" );
		}

		// effect_allocate, then effect_update with no time passed while nothing is visible (both player PVS vectors are clear at map start): no parts, a one-shot effect is deleted at once, and only a deterministic one (flag 0x4) draws its delays, durations and skips from the global stream.
		void NewEffect( TagEntry effect, TagEntry owner, bool looping, int scaleFunction, string what )
		{
			if ( effect is null )
				return;
			var a = effect.Address;
			var flags = _data.U32( a );
			var events = _data.Block( a + 0x34, 0x44 ).ToList();
			if ( events.Count == 0 )
				return;

			// Deterministic effects take slots up from the bottom of the 256 (deleting any other effect in the way) and the rest down from the top, which isn't followed where they meet.
			var deterministic = (flags & 4) != 0;
			if ( deterministic ? ++_deterministicSlots + _effects - _deterministicEffects > MaximumEffects : _effects >= MaximumEffects )
				Warnings.Add( $"{what}: {effect.Path} may find the effects full" );

			var eventIndex = 0;
			var inDuration = false;
			var time = 0f;
			var duration = 0f;
			void SetEvent( int index )
			{
				eventIndex = index;
				inDuration = false;
				time = 0;
				if ( deterministic )
					duration = Random.Real( _data.F32( events[index] + 0x8 ), _data.F32( events[index] + 0xC ) );
			}

			SetEvent( 0 );
			if ( !looping )
				return;

			// A looping effect whose scale function is off stops (effect_stop goes to the event after the loop, if there is one) or, flagged 0x1, is deleted.
			var on = scaleFunction < 0 ? true : FunctionActive( owner, scaleFunction );
			if ( on is null && deterministic )
				Warnings.Add( $"{what}: {effect.Path} follows function {(char)('A' + scaleFunction)}, assumed on" );
			if ( on == false && (flags & 1) != 0 )
				return;

			_effects++;
			if ( !deterministic )
				return;
			_deterministicEffects++;

			var loopStart = _data.I16( a + 0x4 );
			var loopStop = _data.I16( a + 0x6 );
			var stopped = false;
			if ( on == false )
			{
				if ( loopStop >= 0 && loopStop + 1 < events.Count )
					SetEvent( loopStop + 1 );
				else
					stopped = true;
			}

			var dt = 0f;
			for ( var iteration = 0; dt >= 0 && !stopped && iteration < 8; iteration++ )
			{
				var remaining = duration - time;
				var completed = dt >= remaining;
				if ( completed )
				{
					dt -= remaining;
					time = duration;
				}
				else
				{
					time += dt;
					dt = -1;
				}

				if ( inDuration )
				{
					if ( !completed )
						continue;
					var next = eventIndex == loopStop && loopStart != -1 ? loopStart : eventIndex + 1;
					while ( next < events.Count && _data.F32( events[next] + 0x4 ) > Random.Real() )
						next++;
					if ( next >= events.Count )
						stopped = true;
					else
						SetEvent( next );
				}
				else if ( completed )
				{
					inDuration = true;
					time = 0;
					duration = Random.Real( _data.F32( events[eventIndex] + 0x10 ), _data.F32( events[eventIndex] + 0x14 ) );
				}
			}
		}

		// Whether a function is on when attachments_new runs, after object_compute_function_values ran once on the fresh object, or null where that needs the clock or the object's slot.
		bool? FunctionActive( TagEntry tag, int index )
		{
			var a = tag.Address;
			var collision = ObjectTags.GetObjectCollisionModel( cache, tag );
			var type = _data.I16( a );
			var functions = _data.Block( a + 0x158, 0x168 ).ToList();
			if ( index >= functions.Count || index >= 4 )
				return false;

			// Inputs are the object's own (vitality from its collision model, nothing damaged, alive), overridden by the type's where set, and a new device has no power or position and counts as locked.
			var inputs = new float?[4];
			for ( var i = 0; i < 4; i++ )
			{
				inputs[i] = _data.I16( a + 0x108 + (uint)(i * 2) ) switch
				{
					0 => 0,
					1 => collision is not null && _data.F32( collision.Address + 0x8 ) > 0 ? 1 : 0,
					2 => collision is not null && _data.F32( collision.Address + 0xCC ) > 0 ? 1 : 0,
					<= 0x11 => 0,
					0x12 => 1,
					_ => null,
				};

				var shared = type is 0 or 1 or >= 7 and <= 9 ? _data.I16( a + 0x198 + (uint)(i * 2) ) : 0;
				if ( shared != 0 )
					inputs[i] = type >= 7 && shared <= 6 ? (shared == 5 ? 1 : 0) : null;

				var own = type switch
				{
					0 => _data.I16( a + 0x30C + (uint)(i * 2) ),
					1 => _data.I16( a + 0x31C + (uint)(i * 2) ),
					2 => _data.I16( a + 0x330 + (uint)(i * 2) ),
					_ => 0,
				};
				if ( own != 0 )
					inputs[i] = null;
			}

			var outputs = new float?[] { 0, 0, 0, 0 };
			var active = new bool?[] { false, false, false, false };
			float? Get( int reference ) => reference switch
			{
				>= 1 and <= 4 => inputs[reference - 1],
				>= 5 and <= 8 => outputs[reference - 5],
				_ => 0,
			};

			for ( var i = 0; i <= index; i++ )
			{
				var f = functions[i];
				var functionFlags = _data.U32( f );

				// The wave: only "one" and "zero" don't depend on the clock and the object's index.
				float? value = _data.I16( f + 0xA ) switch { 0 => 1, 1 => 0, _ => null };
				if ( _data.I16( f + 0xC ) is var scaleFunctionBy and not 0 )
					value = Get( scaleFunctionBy ) == 0 ? 0 : value * Get( scaleFunctionBy );
				if ( (functionFlags & 1) != 0 )
					value = 1 - value;
				if ( _data.F32( f + 0x14 ) != 0 )
					value = null;
				if ( _data.F32( f + 0x18 ) is var squareWave and not 0 && value is { } square )
					value = square > squareWave ? 1 : 0;
				if ( _data.I16( f + 0x1C ) is var steps and > 1 && value is { } step )
					value = MathF.Floor( steps * step ) * _data.F32( f + 0x140 );
				if ( _data.F32( f + 0x13C ) is var sawtooth and > 0 && value is { } saw )
					value = saw % sawtooth;
				if ( _data.I16( f + 0x22 ) is var add and not 0 )
					value = value + Get( add ) is { } sum ? MathF.Min( 1, sum ) : null;
				if ( _data.I16( f + 0x24 ) is var scaleResultBy and not 0 )
					value *= Get( scaleResultBy );

				// map to: the transition curves clamp to 0..1 and keep the ends where they are.
				if ( value is { } clamped )
				{
					clamped = Math.Clamp( clamped, 0, 1 );
					value = _data.I16( f + 0x1E ) == 0 || clamped is 0 or 1 ? clamped : null;
				}
				if ( _data.F32( f + 0x38 ) is var scaleBy and > 0 )
					value *= scaleBy;

				// Off at (or below) its lower bound unless flagged 0x4, and off with the function it turns off with.
				var lower = _data.F32( f + 0x28 );
				var upper = _data.F32( f + 0x2C );
				bool? on = true;
				if ( value is not { } x )
					on = null;
				else if ( _data.I16( f + 0x26 ) == 2 )
				{
					value = lower + (upper - lower) * x;
					if ( lower + 0.0001f >= value )
						on = (functionFlags & 4) != 0;
				}
				else
				{
					if ( lower + 0.0001f >= x )
					{
						on = (functionFlags & 4) != 0;
						x = lower;
					}
					x = MathF.Min( upper, x );
					value = _data.I16( f + 0x26 ) == 1 ? (x - lower) * _data.F32( f + 0x138 ) : x;
				}

				var turnOffWith = _data.I16( f + 0x36 );
				if ( turnOffWith >= 0 && on != false )
					on = turnOffWith >= 4 || active[turnOffWith] == false ? false : on is null || active[turnOffWith] is null ? null : true;
				if ( (functionFlags & 2) != 0 )
					value %= 1;

				outputs[i] = value;
				active[i] = on;
			}

			return active[index];
		}
	}

	// object_choose_random_region_permutations: a random pick among a region's permutations that can be chosen (flag bit 0 clear) for the variant, or below variant 100 for none.
	public static int[] ChooseRegionPermutations( CacheFile cache, TagEntry objectTag, int variant, HaloRandom random )
	{
		var data = cache.TagData;
		var model = ObjectTags.GetObjectModel( cache, objectTag );
		if ( model is null || model.Class != TagClasses.GbxModel )
			return [];

		var regions = data.Block( model.Address + 0xC4, 0x4C ).Select( r => data.Block( r + 0x40, 0x58 ).Select( p => (Flags: data.U32( p + 0x20 ), Variant: (int)data.I16( p + 0x24 )) ).ToList() ).ToList();
		var chosen = new int[regions.Count];

		bool Select( int v )
		{
			var all = true;
			for ( var r = 0; r < regions.Count; r++ )
			{
				var available = Available( regions[r], v );
				if ( available.Count == 0 && v != -1 )
					available = Available( regions[r], 0 );
				if ( available.Count == 0 )
				{
					chosen[r] = 0;
					all = false;
					continue;
				}
				chosen[r] = available[available.Count == 1 ? 0 : random.Range( 0, available.Count )];
			}
			return all;
		}

		if ( variant <= 0 || !Select( variant ) )
		{
			Select( -1 );
			var determined = 0;
			for ( var r = 0; r < regions.Count && determined == 0; r++ )
			{
				if ( chosen[r] < regions[r].Count )
					determined = regions[r][chosen[r]].Variant;
			}
			if ( determined > 0 )
				Select( determined );
		}

		return chosen;
	}

	static List<int> Available( List<(uint Flags, int Variant)> region, int variant )
	{
		var available = new List<int>();
		for ( var i = 0; i < region.Count && available.Count < 32; i++ )
		{
			if ( (region[i].Flags & 1) == 0 && (region[i].Variant == variant || variant == -1 && region[i].Variant < 100) )
				available.Add( i );
		}
		return available;
	}

	// A point in an object's frame (forward, left, up from its yaw, pitch and roll) in world space.
	static Vector3 Transform( Vector3 angles, Vector3 position, Vector3 offset )
	{
		var placement = new ObjectPlacement( "", null, position, angles, 0, false );
		var forward = placement.Forward;
		var up = placement.Up;
		var left = Vector3.Cross( up, forward );
		return position + forward * offset.X + left * offset.Y + up * offset.Z;
	}
}
