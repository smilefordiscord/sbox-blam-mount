using System;
using System.Collections.Generic;
using System.Linq;

namespace Blam.Halo3.Tags;

// Inside the namespace so these win over s&box's global vector types when this compiles into the mount.
using Vector3 = System.Numerics.Vector3;

// A placement to put in the scene: its model (null when it has none), its multiplayer role (null for ordinary objects or on campaign maps), and whether it's only a marker (Halo shows it in its editor or in some game types only).
public sealed record PlannedObject( ScenarioPlacement Placement, ObjectTag Object, ObjectModel Model, MultiplayerObject Role, bool Marker );

// A player spawn: a multiplayer spawn placement (Placement set) or a player starting location. Yaw in radians.
public sealed record PlannedSpawn( Vector3 Position, float Yaw, ScenarioPlacement Placement, ObjectTag Object, bool Initial );

// What of a scenario goes into a scene with the BSPs in a mask (engine independent, so BlamInspect checks the same choices the mount makes).
public sealed class ScenarioPlan
{
	// Sound scenery, effect scenery and light volumes have no model. Bipeds and giants play their idle (h3-animation: Blam/Halo3/ObjectAnimationChoice.cs).
	public static readonly IReadOnlySet<PlacementKind> PlacedKinds = new HashSet<PlacementKind>
	{
		PlacementKind.Scenery, PlacementKind.Vehicle, PlacementKind.Equipment, PlacementKind.Weapon, PlacementKind.Machine,
		PlacementKind.Terminal, PlacementKind.Control, PlacementKind.Crate, PlacementKind.Biped, PlacementKind.Giant,
	};

	// Weapons and equipment lying around shouldn't trip you up.
	public static readonly IReadOnlySet<PlacementKind> SolidKinds = new HashSet<PlacementKind>
	{
		PlacementKind.Scenery, PlacementKind.Vehicle, PlacementKind.Machine, PlacementKind.Terminal, PlacementKind.Control, PlacementKind.Crate, PlacementKind.Giant,
	};

	readonly Dictionary<TagEntry, ObjectTag> _objects = new();
	readonly Dictionary<TagEntry, ObjectModel> _models = new();

	public ScenarioTag Scenario { get; }
	public ushort ShownBsps { get; }
	public IReadOnlyList<PlannedObject> Objects { get; }
	public IReadOnlyList<PlannedSpawn> Spawns { get; }

	// The one sky shown, or null.
	public ScenarioSky Sky { get; }

	// Every BSP of the scenario.
	public static ushort AllBsps( ScenarioTag scenario ) =>
		scenario.StructureBspCount >= 16 ? ushort.MaxValue : (ushort)((1 << scenario.StructureBspCount) - 1);

	public ScenarioPlan( ScenarioTag scenario, ushort shownBsps )
	{
		Scenario = scenario;
		ShownBsps = shownBsps;

		Sky = SkyFor( scenario, shownBsps );
		_shownSky = Sky is null ? -1 : IndexOf( scenario.Skies, Sky );
		var skyObjects = scenario.Skies.Select( s => s.Object ).Where( o => o is not null ).ToHashSet();
		var objects = new List<PlannedObject>();
		var spawns = new List<PlannedSpawn>();
		foreach ( var placement in scenario.Placements )
		{
			if ( !PlacedKinds.Contains( placement.Kind ) || placement.NotAutomatically || placement.NeverPlaced || !Shown( placement ) )
				continue;

			// Skies are placed on their own (050_floodvoi also places its sky as scenery).
			if ( !ObjectTag.IsObject( placement.Object ) || skyObjects.Contains( placement.Object ) )
				continue;

			var objectTag = Object( placement.Object );
			var role = scenario.IsMultiplayer && objectTag.Multiplayer is { Type: not MultiplayerObjectType.Ordinary } mp ? mp : null;
			if ( role is { IsPlayerSpawn: true } )
			{
				spawns.Add( new PlannedSpawn( placement.Position, placement.Angles.X, placement, objectTag, role.ValidInitialSpawn ) );
				continue;
			}

			// Goals of particular game types (hills, flags, territories...) only appear in those games, and a scene has none.
			var marker = role is { EditorOnly: true } || role is { GameEngines: not GameEngines.None };
			objects.Add( new PlannedObject( placement, objectTag, Model( objectTag.Model ), role, marker ) );
		}

		// Initial spawns first, so the map preview looks out from one.
		spawns = spawns.OrderBy( s => s.Initial ? 0 : 1 ).ToList();
		if ( spawns.Count == 0 )
		{
			foreach ( var start in scenario.PlayerStartingLocations )
			{
				if ( start.Bsp < 0 || start.Bsp >= 16 || (shownBsps >> start.Bsp & 1) != 0 )
					spawns.Add( new PlannedSpawn( start.Position, start.Facing, null, null, true ) );
			}
		}

		Objects = objects;
		Spawns = spawns;
	}

	static int IndexOf( IReadOnlyList<ScenarioSky> skies, ScenarioSky sky )
	{
		for ( var i = 0; i < skies.Count; i++ )
		{
			if ( ReferenceEquals( skies[i], sky ) )
				return i;
		}
		return -1;
	}

	readonly int _shownSky;

	// With the shown BSPs, and not hung on a sky that isn't shown (050_floodvoi's ark over its other skies) or on nothing.
	public bool Shown( ScenarioPlacement placement ) =>
		(placement.BspMask == 0 || (placement.BspMask & ShownBsps) != 0) && (placement.Sky < 0 || placement.Sky == _shownSky) && !placement.Orphaned;

	public ObjectTag Object( TagEntry tag ) => _objects.TryGetValue( tag, out var o ) ? o : _objects[tag] = new ObjectTag( tag );

	public ObjectModel Model( TagEntry hlmt ) =>
		hlmt is null || hlmt.Address == 0 ? null : _models.TryGetValue( hlmt, out var m ) ? m : _models[hlmt] = new ObjectModel( hlmt );

	// The first shown BSP's sky: its default sky, else the first sky active on it, else one active on any shown BSP.
	static ScenarioSky SkyFor( ScenarioTag scenario, ushort shown )
	{
		var skies = scenario.Skies.Where( s => ObjectTag.IsObject( s.Object ) ).ToList();
		if ( skies.Count == 0 )
			return null;

		var first = shown == 0 ? 0 : System.Numerics.BitOperations.TrailingZeroCount( (uint)shown );
		var defaultSky = first < scenario.StructureBspDefaultSkies.Count ? scenario.StructureBspDefaultSkies[first] : -1;
		if ( defaultSky >= 0 && defaultSky < scenario.Skies.Count && ObjectTag.IsObject( scenario.Skies[defaultSky].Object ) )
			return scenario.Skies[defaultSky];

		return skies.FirstOrDefault( s => s.BspMask != 0 && s.InBsp( first ) )
			?? skies.FirstOrDefault( s => (s.BspMask & shown) != 0 )
			?? skies[0];
	}
}
