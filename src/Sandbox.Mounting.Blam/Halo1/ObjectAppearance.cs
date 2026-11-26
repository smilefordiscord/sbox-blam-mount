using Blam.Halo1;
using Blam.Halo1.Tags;

namespace Sandbox.Mounting.Blam.Halo1;

/// <summary>
/// Per-object looks that Halo works out when it creates an object rather than storing in the
/// level: its change colors, and its lighting from the lightmap around it.
/// </summary>
sealed class ObjectAppearance
{
	readonly Dictionary<ObjectPlacement, int> _indices = new();

	/// <summary>
	/// Works out every placed object's lighting and change colors and puts them in the
	/// <see cref="ObjectLightingTable"/>.
	/// </summary>
	/// <param name="extraPlacements">Objects placed from somewhere other than the scenario's object
	/// blocks, e.g. multiplayer item spawns.</param>
	public static ObjectAppearance Build( CacheFile cache, ScenarioTag scenario, IEnumerable<StructureBspTag> bsps, IEnumerable<ObjectPlacement> extraPlacements = null ) =>
		Install( cache, Compute( cache, scenario, bsps, extraPlacements ) );

	/// <summary>
	/// The lighting and change colors <see cref="Build"/> works out, before anything goes to the
	/// GPU: plain computation over the map's data, so a scene being prepared runs it on another
	/// thread (see <see cref="SceneBuilding"/>).
	/// </summary>
	public sealed record Computed( List<ObjectPlacement> Placements, List<ObjectLightingTable.Entry> Entries, ObjectLightingSampler Sampler, ObjectLightVolume Volume, List<StructureBspReference> Bsps );

	/// <inheritdoc cref="Computed"/>
	public static Computed Compute( CacheFile cache, ScenarioTag scenario, IEnumerable<StructureBspTag> bsps, IEnumerable<ObjectPlacement> extraPlacements = null )
	{
		var placements = (extraPlacements is null ? scenario.Objects : scenario.Objects.Concat( extraPlacements )).ToList();
		var sampler = new ObjectLightingSampler( cache, bsps );
		var requests = placements.Select( p => ObjectLightingSampler.Request( cache, p ) ).ToList();
		var lights = sampler.LightObjects( requests );

		var entries = new List<ObjectLightingTable.Entry>( placements.Count );
		for ( var i = 0; i < placements.Count; i++ )
			entries.Add( new ObjectLightingTable.Entry( lights[i], ChangeColors( cache, placements[i] ), requests[i].Center ) );

		// Objects a game spawns light themselves from this as they move (see ObjectLightingTable).
		var focus = placements.Select( p => p.Position )
			.Concat( scenario.PlayerStartingLocations.Select( p => p.Position ) )
			.Concat( scenario.NetgameFlags.Select( f => f.Position ) )
			.Concat( scenario.NetgameEquipment.Select( e => e.Position ) );
		var volume = ObjectLightVolume.Build( sampler, focus );

		return new Computed( placements, entries, sampler, volume, bsps.Select( b => b.Reference ).ToList() );
	}

	/// <summary>
	/// Puts <see cref="Compute"/>'s results in the <see cref="ObjectLightingTable"/> and
	/// <see cref="MovingObjectLights"/>. Main thread.
	/// </summary>
	public static ObjectAppearance Install( CacheFile cache, Computed computed )
	{
		var placements = computed.Placements;
		var first = ObjectLightingTable.Add( cache.FilePath, computed.Entries );
		ObjectLightingTable.SetVolume( cache.Name, computed.Volume );

		// And from the lightmap under them, eased the way Halo eases it (see MovingObjectLights).
		MovingObjectLights.SetLevel( cache.Name, computed.Sampler, computed.Volume, ObjectLightingTable.Fallback, cache, computed.Bsps );

		var appearance = new ObjectAppearance();
		// Row 0 (a full table) is never found: those objects get the average light.
		for ( var i = 0; i < placements.Count; i++ )
			appearance._indices.TryAdd( placements[i], first == 0 ? 0 : first + i );

		return appearance;
	}

	/// <summary>
	/// The tint for a placed object's renderer: its row in the <see cref="ObjectLightingTable"/>,
	/// which the model shader reads its lighting and change colors from.
	/// </summary>
	public Color TintFor( CacheFile cache, ObjectPlacement placement ) =>
		_indices.TryGetValue( placement, out var index ) ? ObjectLightingTable.Tint( index ) : ChangeColorTint( cache, placement );

	/// <summary>
	/// The object's change colors A-D, picked the way Halo picks them when it creates an object:
	/// at random from ranges in its tag (so every pine tree and boulder is a little different).
	/// Here the random numbers come from the placement, so the same object gets the same colors
	/// every time the map is built. White where the object has none.
	/// </summary>
	public static System.Numerics.Vector3[] ChangeColors( CacheFile cache, ObjectPlacement placement )
	{
		var colors = ObjectChangeColors.Read( cache, placement.Object );
		var result = new System.Numerics.Vector3[4];

		// Not HashCode.Combine: it's seeded differently every run.
		var seed = 17u;
		foreach ( var value in new[] { placement.Object.Id, BitConverter.SingleToUInt32Bits( placement.Position.X ), BitConverter.SingleToUInt32Bits( placement.Position.Y ), BitConverter.SingleToUInt32Bits( placement.Position.Z ) } )
			seed = (seed ^ value) * 16777619u;

		var random = new Random( (int)seed );
		for ( var i = 0; i < 4; i++ )
			result[i] = i >= colors.Count ? System.Numerics.Vector3.One : ObjectChangeColors.AtPosition( colors[i], i, placement.Position ) ?? ObjectChangeColors.Pick( colors[i], () => random.NextSingle() );

		return result;
	}

	/// <summary>
	/// The object's change color A as a tint, for an object that isn't in the table. The model
	/// shader falls back to reading change color A from the tint.
	/// </summary>
	public static Color ChangeColorTint( CacheFile cache, ObjectPlacement placement )
	{
		var color = ChangeColors( cache, placement )[0];
		return new Color( color.X, color.Y, color.Z );
	}
}
