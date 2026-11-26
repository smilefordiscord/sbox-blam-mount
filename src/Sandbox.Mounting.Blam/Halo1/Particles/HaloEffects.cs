using Blam.Halo1;
using Blam.Halo1.Tags;

namespace Sandbox.Mounting.Blam.Halo1;

// How prefabs ask HaloParticleWorld for Halo's particles, particle systems and effect lights, with engine components only. A root is tagged halo_effect plus its token and variant tags, and carries a TemporaryEffect so it's found the frame it spawns. Its "halo controls" child holds scales A and B (local position x, y) and the change color (local scale). An inactive root stops the effect, and a destroyed one deletes it.
static class HaloEffects
{
	public const string RootTag = "halo_effect";
	public const string FirstPersonTag = "halo_fx_fp";
	public const string LoopTag = "halo_fx_loop";
	public const string AttachedTag = "halo_fx_attached";
	public const string ViewModelTag = "halo_view_model";

	// An attached effect's location child for its unnamed location, which Halo puts at the object's origin rather than the attachment's marker.
	public const string OriginTag = "halo_effect_origin";

	// A location child the object has no marker for: the runtime makes nothing there.
	public const string NowhereTag = "halo_fx_nowhere";

	// A carried effect whose locations ride other parts of its object (MovingAttachments): the root and its locations share a group tag, and the runtime finds them under the nearest ancestor tagged as the scope.
	public const string LocatedTag = "halo_fx_located";
	public const string LocationScopeTag = "halo_fx_scope";
	const string LocationGroupPrefix = "halo_fx_located_";
	static int _locationGroups;

	// Tags root as a located effect and returns the group tag its locations take.
	public static string LocateGroup( GameObject root )
	{
		var group = $"{LocationGroupPrefix}{System.Threading.Interlocked.Increment( ref _locationGroups )}";
		root.Tags.Add( LocatedTag );
		root.Tags.Add( group );
		return group;
	}

	public static bool IsLocationGroup( string tag ) => tag.StartsWith( LocationGroupPrefix, StringComparison.Ordinal );

	// The child a game sets an effect's scales and color through (see the remarks).
	public const string ControlsName = "halo controls";

	// Marks root as an effect or particle system (tag) for the runtime to play, with its scales at a and b.
	public static void Mark( GameObject root, TagEntry tag, bool firstPerson = false, bool looping = false, bool attached = false, float a = 1, float b = 1, System.Numerics.Vector3? color = null )
	{
		root.Tags.Add( RootTag );
		root.Tags.Add( HaloEffectLibrary.Token( tag ) );
		if ( firstPerson )
			root.Tags.Add( FirstPersonTag );
		if ( looping )
			root.Tags.Add( LoopTag );
		if ( attached )
			root.Tags.Add( AttachedTag );

		if ( root.Components.Get<TemporaryEffect>( FindMode.EverythingInSelf ) is null )
		{
			// Never removes anything: it's there to be found (the scene only lists enabled components).
			var marker = root.AddComponent<TemporaryEffect>();
			marker.DestroyAfterSeconds = float.MaxValue;
			marker.WaitForChildEffects = false;
		}

		var controls = new GameObject( root, true, ControlsName );
		controls.LocalPosition = new Vector3( a, b, 0 );
		if ( color is { } c )
			controls.LocalScale = new Vector3( c.X, c.Y, c.Z );
	}

	// The longest a one-shot effect can run, with every event's longest delay and duration.
	public static float LongestRun( EffectTag effect ) =>
		effect.Events.Sum( e => MathF.Max( MathF.Max( e.Delay.X, e.Delay.Y ), 0 ) + MathF.Max( MathF.Max( e.Duration.X, e.Duration.Y ), 0 ) );
}
