using Blam.Halo1;
using Blam.Halo1.Tags;

namespace Sandbox.Mounting.Blam.Halo1;

/// <summary>
/// Where one of a placed object's attachments ends up in the level.
/// </summary>
/// <param name="Position">World position in s&amp;box units.</param>
/// <param name="Forward">The marker's direction in the world.</param>
/// <param name="Rotation">The marker's orientation in the world (forward x, left y, up z, like Halo's).</param>
/// <param name="Kind">The placed object's kind, e.g. "scenery" or "light fixture".</param>
/// <param name="Index">The attachment's index in the object's attachments block.</param>
/// <param name="Location">A looping effect's: the one location of it made here (see
/// <see cref="EffectLocationSite"/>); null for the other kinds.</param>
sealed record AttachmentSite( ObjectAttachment Attachment, ObjectPlacement Placement, Vector3 Position, Vector3 Forward, Rotation Rotation, string Kind, int Index, string Location = null );

/// <summary>
/// Finds the attachments (lights, effects, particle systems) of the objects placed in a level
/// and where their markers put them.
/// </summary>
static class ObjectAttachmentSites
{
	/// <summary>
	/// Kinds of object whose attachments are part of the level's look. Weapons and vehicles carry
	/// muzzle flashes, flashlights and headlights that are only on while someone uses them.
	/// </summary>
	public static readonly HashSet<string> StaticKinds = ["scenery", "machine", "control", "light fixture"];

	/// <summary>
	/// Scene tag of a placed object some of whose attachments the game makes and drives itself
	/// (see <see cref="GameDriven"/>); each such attachment is tagged as well, by its index in the
	/// object's attachments block (<see cref="GameAttachmentTag"/>), the index its attachments
	/// prefab tags it <c>halo_attachment_&lt;index&gt;</c> by.
	/// </summary>
	public const string GameAttachmentsTag = "halo_game_attachments";

	public static string GameAttachmentTag( int index ) => $"halo_game_attachment_{index}";

	/// <summary>
	/// Whether a campaign game makes and drives a placed object's attachment, so the level leaves it
	/// out: a light, effect or particle system on a machine, control or light fixture, scaled by an
	/// object function (A-D out), in a single player scenario. Halo follows those functions every
	/// frame from the device's power, position, lock state and delay: a10's shield charger's field
	/// is off until it's powered, alert lights until the scripts turn them on, a50's small door
	/// lights only light while the door runs its delay. A device's light tinted by a change color
	/// that follows a function is the game's too: b30's door lock lights go from green to red as
	/// the door locks. Other unscaled ones stay in the level. Looping sounds aren't built for placed
	/// devices at all.
	/// Every such attachment of a static kind object placed "not automatically" is the game's too,
	/// since it only exists once a script creates the object: a10's looking tutorial swaps yellow
	/// lens flare scenery for green as the player finds each panel.
	/// </summary>
	public static bool GameDriven( CacheFile cache, ObjectPlacement placement, TagEntry type, int primaryScale, int changeColor ) =>
		cache.ScenarioType == ScenarioType.Singleplayer
		&& type is not null
		&& (type.Class == EffectTagClasses.Light || type.Class == EffectTagClasses.Effect || type.Class == EffectTagClasses.ParticleSystem)
		&& (placement.NotAutomatically && StaticKinds.Contains( placement.Kind )
			|| placement.Kind is "machine" or "control" or "light fixture"
				&& (primaryScale is >= 1 and <= 4 || type.Class == EffectTagClasses.Light && ChangeColorFollowsFunction( cache, placement.Object, changeColor )));

	/// <summary>
	/// Whether the object's change color (1-4 for A-D) is scaled or darkened by an object function.
	/// </summary>
	static bool ChangeColorFollowsFunction( CacheFile cache, TagEntry objectTag, int changeColor ) =>
		changeColor is >= 1 and <= 4
		&& ObjectChangeColors.Read( cache, objectTag ) is var colors
		&& changeColor <= colors.Count
		&& (colors[changeColor - 1].ScaleBy != 0 || colors[changeColor - 1].DarkenBy != 0);

	public static List<AttachmentSite> Find( CacheFile cache, ScenarioTag scenario )
	{
		var sites = new List<AttachmentSite>();
		var markerCache = new Dictionary<uint, IReadOnlyList<ModelMarker>>();
		var attachmentCache = new Dictionary<uint, IReadOnlyList<ObjectAttachment>>();

		foreach ( var placement in scenario.Objects )
		{
			if ( placement.NotAutomatically || !StaticKinds.Contains( placement.Kind ) )
				continue;

			if ( !attachmentCache.TryGetValue( placement.Object.Id, out var attachments ) )
				attachmentCache[placement.Object.Id] = attachments = ObjectAttachments.Read( cache, placement.Object );

			if ( attachments.Count == 0 )
				continue;

			var modelTag = ObjectTags.GetObjectModel( cache, placement.Object );
			var modelId = modelTag?.Id ?? 0xFFFFFFFF;
			if ( !markerCache.TryGetValue( modelId, out var markers ) )
				markerCache[modelId] = markers = ObjectAttachments.ReadMarkers( cache, modelTag );

			var (origin, rotation) = PlacementFrame( placement );

			for ( var index = 0; index < attachments.Count; index++ )
			{
				var attachment = attachments[index];
				if ( attachment.Type?.Class == EffectTagClasses.Effect )
				{
					foreach ( var location in new EffectTag( cache, attachment.Type ).Locations.Distinct() )
					{
						if ( string.IsNullOrEmpty( location ) )
						{
							sites.Add( new AttachmentSite( attachment, placement, origin, rotation.Forward, rotation, placement.Kind, index, "" ) );
							continue;
						}

						foreach ( var marker in Instances( markers, location ).Take( ObjectAttachmentPlan.MaximumEffectInstances ) )
						{
							var (position, world) = MarkerFrame( marker, origin, rotation );
							sites.Add( new AttachmentSite( attachment, placement, position, world.Forward, world, placement.Kind, index, location ) );
						}
					}
					continue;
				}

				// No marker, or one the model doesn't have: the object's origin.
				if ( string.IsNullOrEmpty( attachment.Marker ) || !markers.Any( m => m.Name == attachment.Marker ) )
				{
					sites.Add( new AttachmentSite( attachment, placement, origin, rotation.Forward, rotation, placement.Kind, index ) );
					continue;
				}

				// A marker can have several instances (three bulbs in one fixture); each gets one.
				foreach ( var marker in Instances( markers, attachment.Marker ) )
				{
					var (position, world) = MarkerFrame( marker, origin, rotation );
					sites.Add( new AttachmentSite( attachment, placement, position, world.Forward, world, placement.Kind, index ) );
				}
			}
		}

		return sites;
	}

	/// <summary>
	/// A marker's instances. They can differ per permutation: the first permutation's, like the model.
	/// </summary>
	static List<ModelMarker> Instances( IReadOnlyList<ModelMarker> markers, string name )
	{
		var instances = markers.Where( m => m.Name == name ).ToList();
		if ( instances.Count == 0 )
			return instances;

		var permutation = instances.Min( m => m.Permutation < 0 ? int.MaxValue : m.Permutation );
		return instances.Where( m => m.Permutation < 0 || m.Permutation == permutation ).ToList();
	}

	/// <summary>
	/// Where a placed object is and how it's turned, in s&amp;box units. Same orientation as the
	/// placed object itself (see ScenarioSceneLoader.PlaceObject).
	/// </summary>
	public static (Vector3 Origin, Rotation Rotation) PlacementFrame( ObjectPlacement placement )
	{
		var rotation = Rotation.LookAt( (Vector3)placement.Forward, (Vector3)placement.Up );
		return ((Vector3)placement.Position * HaloCeMount.WorldScale, rotation);
	}

	/// <summary>
	/// A model marker's place in the world, for an object at <paramref name="origin"/> turned by <paramref name="rotation"/>.
	/// </summary>
	public static (Vector3 Position, Rotation Rotation) MarkerFrame( ModelMarker marker, Vector3 origin, Rotation rotation )
	{
		var q = marker.Rotation;
		return (origin + rotation * ((Vector3)marker.Position * HaloCeMount.WorldScale), rotation * new Rotation( q.X, q.Y, q.Z, q.W ));
	}

	/// <summary>
	/// The first instance of a marker, as the game finds it by name, from the first permutation
	/// (like the model). Null if the model doesn't have it.
	/// </summary>
	public static ModelMarker FirstMarker( IReadOnlyList<ModelMarker> markers, string name ) => Instances( markers, name ).FirstOrDefault();
}
