using System;
using System.Collections.Generic;
using System.Linq;
using Blam.Halo2.Tags;

namespace Blam.Halo2;

/// <summary>
/// The handles MCC gives a map's objects as it creates them at the start of a game, and the <c>random_constant</c>
/// each object's shaders read from its handle (which crate label or drum stencil it shows).
/// </summary>
/// <remarks>
/// <para>The i-th object created gets handle <c>(0xE26F + i) &lt;&lt; 16 | i</c> (Zanzibar, Headlong, Coagulation), and
/// <c>random_constant</c> is one step of Halo's random numbers from it. MCC creates:</para>
/// <list type="number">
/// <item>Every placement it creates automatically (not "not automatically", and named, or dynamic, or not a
/// static object an editor placed in one BSP), object type by type (biped 0 ... creature 12), each type's placements
/// in order.</item>
/// <item>"Placing objects on bsp": the rest of the unnamed, static ones belonging to the starting BSP (or
/// none), or placed in it by manual BSP placement, type by type again.</item>
/// <item>Multiplayer: the item spawns' objects, in pairs: spawn i, then spawn i + 60 (Zanzibar's 75 spawns: 0, 60, 1,
/// 61 ... 11, 71, 12, 13 ...), skipping those outside the game type; the player (biped, starting weapon) after the
/// first pair. Seen on Coagulation and Zanzibar, one player playing Slayer; where the player falls may depend on
/// timing.</item>
/// </list>
/// <para>Dynamic: bipeds, vehicles, weapons, equipment, garbage, machines, controls, crates, creatures, and
/// scenery whose model has damage info. Both passes skip what's never placed and, in multiplayer, scenery outside
/// the game type (placement +0x5A, a bit per game engine). Each object's children come straight after it:
/// the objects its model variant attaches, each followed by its own, then a unit's weapons.</para>
/// </remarks>
public static class ObjectCreationOrder
{
	/// <summary>Halo 2's game engine ids: Slayer.</summary>
	public const int Slayer = 2;

	/// <summary>The player's objects MCC created among the item spawns' on Coagulation (biped, starting weapon).</summary>
	const int PlayerObjects = 2;

	const int FirstSalt = 0xE26F;

	/// <summary>MCC creates item spawn i + 60 right after spawn i (Zanzibar: 0, 60, 1, 61 ... 11, 71, 12, 13 ...).</summary>
	const int SpawnPairing = 60;

	/// <summary>The handle of the <paramref name="index"/>-th object created.</summary>
	public static uint Handle( int index ) => (uint)((FirstSalt + index) & 0xFFFF) << 16 | (uint)index;

	/// <summary>The object function <c>random_constant</c> of the object with <paramref name="handle"/>, 0-1.</summary>
	public static float RandomConstant( uint handle ) => ((handle * 0x19660Du + 0x3C6EF35Fu) >> 16) / 65535f;

	/// <summary>
	/// An object MCC created: its handle, and those of the objects its model variant attaches as
	/// <see cref="AttachedObjects.For"/> lists them (null where it leaves one out).
	/// </summary>
	public sealed record Created( uint Handle, IReadOnlyList<uint?> Attached );

	/// <summary>
	/// The objects MCC creates when a game starts on the map: for each scenario placement and each item spawn's
	/// object, what it created (null if it doesn't create it then).
	/// </summary>
	/// <param name="placements">The scenario's placements (<see cref="ScenarioObjects.Objects"/>).</param>
	/// <param name="spawns">Each item spawn with its object (null placement: it spawns nothing).</param>
	/// <param name="multiplayer">A multiplayer map: game types apply.</param>
	/// <param name="gameEngine">The game engine being played (<see cref="Slayer"/>...).</param>
	/// <param name="bsp">The BSP the game starts in.</param>
	public static (Created[] Placements, Created[] Spawns) Compute( IReadOnlyList<ObjectPlacement> placements,
		IReadOnlyList<(NetgameEquipment Spawn, ObjectPlacement Placement)> spawns, bool multiplayer, int gameEngine = Slayer, int bsp = 0 )
	{
		var created = new Created[placements.Count];
		var spawned = new Created[spawns.Count];
		var next = 0;

		Created Create( ObjectPlacement placement, string variant )
		{
			var handle = Handle( next++ );
			var attached = new List<uint?>();
			AddChildren( placement.Object, variant, 0, attached, ref next );
			return new Created( handle, attached );
		}

		var byType = placements
			.Select( ( p, i ) => (Placement: p, Index: i) )
			.Where( p => p.Placement.Data is not null )
			.OrderBy( p => p.Placement.Data.Type )
			.ThenBy( p => p.Placement.Data.Index )
			.ToList();

		foreach ( var (placement, i) in byType )
		{
			if ( CreatedAutomatically( placement ) && Allowed( placement, multiplayer, gameEngine ) )
				created[i] = Create( placement, placement.Variant );
		}

		foreach ( var (placement, i) in byType )
		{
			var data = placement.Data;
			if ( created[i] is not null || data.NameIndex != -1 || (data.Flags & 1) != 0 || IsDynamic( placement ) )
				continue;

			var inBsp = data.BspPolicy == 2
				? (data.ManualBsps >> bsp & 1) != 0
				: placement.OriginBsp == -1 || placement.OriginBsp == bsp || CreatedAutomatically( placement );
			if ( inBsp && Allowed( placement, multiplayer, gameEngine ) )
				created[i] = Create( placement, placement.Variant );
		}

		// Item spawns in pairs, i then i + 60, the player after the first pair.
		var visit = new List<int>();
		for ( var s = 0; s < Math.Min( spawns.Count, SpawnPairing ); s++ )
		{
			visit.Add( s );
			if ( s + SpawnPairing < spawns.Count )
				visit.Add( s + SpawnPairing );
		}

		visit.AddRange( Enumerable.Range( 2 * SpawnPairing, Math.Max( spawns.Count - 2 * SpawnPairing, 0 ) ) );
		var playerAfter = spawns.Count > SpawnPairing ? 1 : 0;
		for ( var v = 0; v < visit.Count; v++ )
		{
			var s = visit[v];
			var (spawn, placement) = spawns[s];
			if ( placement is not null && SpawnsIn( spawn, gameEngine ) )
				spawned[s] = Create( placement, placement.Variant );

			if ( v == playerAfter )
				next += PlayerObjects;
		}

		return (created, spawned);
	}

	/// <summary>
	/// As Halo 2 decides it: whether the global pass creates the placement.
	/// </summary>
	static bool CreatedAutomatically( ObjectPlacement placement )
	{
		var data = placement.Data;
		if ( (data.Flags & 1) != 0 )
			return false;
		if ( data.NameIndex != -1 || IsDynamic( placement ) )
			return true;

		// Static objects an editor placed in a BSP wait for that BSP (types 0-4, 11, 12 never do).
		var source = placement.IdSource;
		var bspBound = source == 0 || (source == 1 && placement.OriginBsp != -1 && (1 << placement.IdType & 0x181F) == 0);
		return data.BspPolicy != 0 || !bspBound;
	}

	/// <summary>
	/// As Halo 2 decides it: bipeds, vehicles, weapons, equipment, garbage, machines, controls, crates, creatures, and
	/// scenery whose model has damage info.
	/// </summary>
	static bool IsDynamic( ObjectPlacement placement )
	{
		var type = placement.IdType;
		if ( type is < 0 or > 12 )
			return false;
		if ( (1 << type & 0x199F) != 0 )
			return true;
		if ( type != (int)ObjectType.Scenery || !ObjectTag.IsObject( placement.Object ) )
			return false;

		return new ObjectTag( placement.Object ).Model is { Address: not 0 } hlmt && hlmt.Class == ModelTag.Class && new ModelTag( hlmt ).HasDamageInfo;
	}

	/// <summary>
	/// As Halo 2 decides it: never placed objects aren't; in multiplayer, scenery outside the game type isn't.
	/// </summary>
	static bool Allowed( ObjectPlacement placement, bool multiplayer, int gameEngine )
	{
		var data = placement.Data;
		if ( (data.Flags & 0x40) != 0 )
			return false;
		if ( !multiplayer || data.Type != (int)ObjectType.Scenery || data.MultiplayerGames is 0 or 0x7FF )
			return true;

		// Game engine -> bit of the scenery's flags (engines 5 and 10 don't filter).
		int? bit = gameEngine switch
		{
			>= 1 and <= 4 => gameEngine - 1,
			6 => 10,
			>= 7 and <= 9 => gameEngine - 3,
			>= 11 and <= 13 => gameEngine - 4,
			_ => null,
		};
		return bit is not { } b || (data.MultiplayerGames >> b & 1) != 0;
	}

	/// <summary>
	/// Whether an item spawn holds something in the game type (12 all, 13 all but CTF, 14 all but CTF and race).
	/// </summary>
	static bool SpawnsIn( NetgameEquipment spawn, int gameEngine ) => spawn.GameTypes.Any( t =>
		t == gameEngine || t == 12 || (t == 13 && gameEngine != 1) || (t == 14 && gameEngine is not 1 and not 5) );

	/// <summary>
	/// The objects created with an object, in order, taking handles: its model variant's attached objects, each with
	/// its own children, then a unit's weapons. <paramref name="attached"/> gets the handles of the attached ones as
	/// <see cref="AttachedObjects.For"/> lists them (two levels deep).
	/// </summary>
	static void AddChildren( TagEntry objectEntry, string variantName, int depth, List<uint?> attached, ref int next )
	{
		if ( !ObjectTag.IsObject( objectEntry ) )
			return;

		var obj = new ObjectTag( objectEntry );
		if ( obj.Model is { Address: not 0 } hlmtTag && hlmtTag.Class == ModelTag.Class
			&& new ModelTag( hlmtTag ).FindVariant( variantName ?? obj.DefaultVariant ) is { } variant )
		{
			foreach ( var child in variant.Objects )
			{
				if ( !ObjectTag.IsObject( child.Object ) )
					continue;

				var handle = Handle( next++ );
				if ( depth < 2 )
					attached.Add( handle );
				AddChildren( child.Object, null, depth + 1, attached, ref next );
			}
		}

		foreach ( var weapon in obj.UnitWeapons )
		{
			if ( !ObjectTag.IsObject( weapon ) )
				continue;

			next++;
			AddChildren( weapon, null, 2, attached, ref next );
		}
	}
}
