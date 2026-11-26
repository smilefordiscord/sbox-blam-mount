using Blam.Halo3.Tags;

namespace Sandbox.Mounting.Blam.Halo3;

// Halo 3's multiplayer objects are ordinary placements whose object tag says what they are (scenario.md §5): spawn points, items with respawn times, hills, flags and teleporters, tagged for the game to use.
static class ScenarioMultiplayer
{
	static readonly string[] Shapes = ["none", "sphere", "cylinder", "box"];

	// Tags halo3_mp, halo3_mp_<role> (halo3_mp_koth_hill_area, halo3_mp_teleporter_2way...), game_<engine> per game type it's in, and from the placement team_<n> (8 neutral), order_<n>, channel_<n>, respawn_<seconds>, shape_<boundary>.
	public static void TagObject( GameObject go, ScenarioPlacement placement, MultiplayerObject role )
	{
		go.Tags.Add( "halo3_mp" );
		go.Tags.Add( RoleTag( role.Type ) );
		foreach ( var engine in Enum.GetValues<GameEngines>() )
		{
			if ( engine != GameEngines.None && role.GameEngines.HasFlag( engine ) )
				go.Tags.Add( $"game_{engine.ToString().ToLowerInvariant()}" );
		}

		if ( placement.Multiplayer is not { } mp )
			return;

		go.Tags.Add( $"team_{mp.Team}" );
		if ( mp.SpawnOrder >= 0 && role.Type is MultiplayerObjectType.KothHillArea or MultiplayerObjectType.TerritoryArea or MultiplayerObjectType.OddballSpawnLocation )
			go.Tags.Add( $"order_{mp.SpawnOrder}" );
		if ( role.IsTeleporter )
			go.Tags.Add( $"channel_{mp.TeleporterChannel}" );

		var respawn = mp.SpawnTime > 0 ? mp.SpawnTime : role.DefaultSpawnTime;
		if ( role.Type is >= MultiplayerObjectType.Weapon and <= MultiplayerObjectType.FlyingVehicle && respawn > 0 )
			go.Tags.Add( $"respawn_{respawn}" );

		var shape = mp.BoundaryShape != 0 ? mp.BoundaryShape : role.BoundaryShape;
		if ( shape > 0 && shape < Shapes.Length )
			go.Tags.Add( $"shape_{Shapes[shape]}" );
	}

	// s&box tags are at most 32 characters: halo3_mp_juggernaut_destination_zone becomes halo3_mp_juggernaut_dest_zone.
	static string RoleTag( MultiplayerObjectType type ) => ScenarioObjectsBuilder.Halo3Tag( type.ToString() ).Replace( "halo3_", "halo3_mp_" ).Replace( "_destination_", "_dest_" );

	// A spawn point at each of the plan's spawns (initial multiplayer spawns tagged initial_spawn), and a map preview point looking out from the first at eye height (as in the other mounts).
	public static void BuildSpawns( ScenarioPlan plan )
	{
		if ( plan.Spawns.Count == 0 )
			return;

		var root = new GameObject( true, "spawns" );
		foreach ( var spawn in plan.Spawns )
		{
			var name = spawn.Placement is null ? "player start" : spawn.Initial ? $"initial spawn ({System.IO.Path.GetFileName( spawn.Placement.Object.Path )})" : "respawn point";
			var go = new GameObject( root, true, name );
			go.WorldPosition = (Vector3)spawn.Position * Halo3Mount.WorldScale;
			go.WorldRotation = Rotation.FromYaw( spawn.Yaw.RadianToDegree() );
			go.AddComponent<SpawnPoint>();
			if ( spawn.Placement is null )
				continue;

			TagObject( go, spawn.Placement, spawn.Object.Multiplayer );
			if ( spawn.Initial )
				go.Tags.Add( "initial_spawn" );
		}

		var first = plan.Spawns[0];
		var preview = new GameObject( true, "map preview" );
		preview.WorldPosition = (Vector3)first.Position * Halo3Mount.WorldScale + Vector3.Up * 64;
		preview.WorldRotation = Rotation.From( 10, first.Yaw.RadianToDegree(), 0 );
		preview.Tags.Add( "map_preview" );
	}
}
