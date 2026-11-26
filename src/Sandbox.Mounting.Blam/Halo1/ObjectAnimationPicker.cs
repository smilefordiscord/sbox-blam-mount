using Blam.Halo1;
using Blam.Halo1.Tags;

namespace Sandbox.Mounting.Blam.Halo1;

/// <summary>
/// Picks what a placed object is doing when the level starts, the way Halo would show it with
/// nothing interacting with it: trees sway, gears turn, characters idle, bodies lie dead, open
/// doors are open. Everything else stays in its model's default pose.
/// </summary>
static class ObjectAnimationPicker
{
	/// <summary>
	/// Object kinds whose function driven animations (the graph's object animations) play on their
	/// own. A vehicle's or weapon's are driven by gameplay, e.g. the warthog's gun spinning when it
	/// fires.
	/// </summary>
	static readonly HashSet<string> FunctionAnimatedKinds = ["scenery", "machine", "control", "light fixture"];

	/// <param name="Looping">False to play once and hold the last frame.</param>
	/// <param name="Rate">Playback speed.</param>
	/// <param name="Animation">The Halo animation the sequence is.</param>
	/// <param name="Graph">The animation graph it's in.</param>
	public readonly record struct Choice( string Sequence, bool Looping, float Rate = 1, ModelAnimation Animation = null, ModelAnimationsTag Graph = null );

	/// <summary>
	/// Adds the renderer for a placed object: a skinned renderer playing its default animation if
	/// it has one, otherwise a plain renderer. An animated unit, or a <paramref name="posable"/> object
	/// with animations, gets a skinned renderer either way, in its rest pose for a game to pose.
	/// </summary>
	public static ModelRenderer AddRenderer( GameObject go, Model model, CacheFile cache, ObjectPlacement placement, bool posable = false ) =>
		AddRenderer( go, model, cache, placement, posable, out _ );

	/// <inheritdoc cref="AddRenderer(GameObject, Model, CacheFile, ObjectPlacement, bool)"/>
	/// <param name="choice">What it plays, or null for nothing.</param>
	public static ModelRenderer AddRenderer( GameObject go, Model model, CacheFile cache, ObjectPlacement placement, bool posable, out Choice? choice )
	{
		choice = model.AnimationCount > 0 ? Pick( cache, placement, model ) : null;

		// A unit with animations but none to play yet (a10's cryotubes, which only open) stands in
		// its rest pose, skinned for the animations a mission's scripts start.
		var unit = model.AnimationCount > 0 && placement.Kind is "biped" or "vehicle";
		if ( choice is null && !unit && !(posable && model.AnimationCount > 0) )
		{
			var renderer = go.AddComponent<ModelRenderer>();
			renderer.Model = model;
			return renderer;
		}

		var skinned = go.AddComponent<SkinnedModelRenderer>();
		skinned.Model = model;
		skinned.UseAnimGraph = false;
		if ( choice is null )
			return skinned;

		skinned.Sequence.Name = choice.Value.Sequence;
		skinned.Sequence.Looping = choice.Value.Looping;
		skinned.PlaybackRate = choice.Value.Rate;
		return skinned;
	}

	/// <summary>
	/// A device's position animation, which a game poses it by (a campaign's doors opening), held
	/// rather than played; null if it has none. Whatever <see cref="Pick"/> chose to play at the start.
	/// </summary>
	public static Choice? DevicePosition( CacheFile cache, ObjectPlacement placement )
	{
		var graphTag = ObjectTags.GetObjectAnimations( cache, placement.Object );
		if ( graphTag is null || graphTag.Class != TagClasses.ModelAnimations )
			return null;

		var graph = new ModelAnimationsTag( cache, graphTag );
		if ( graph.DeviceAnimations.Count == 0 || graph.DeviceAnimations[0].Count == 0 )
			return null;

		var index = graph.DeviceAnimations[0][0];
		return index >= 0 && index < graph.Animations.Count ? new Choice( graph.Animations[index].Name, false, 1, graph.Animations[index], graph ) : null;
	}

	public static Choice? Pick( CacheFile cache, ObjectPlacement placement, Model model )
	{
		var graphTag = ObjectTags.GetObjectAnimations( cache, placement.Object );
		if ( graphTag is null || graphTag.Class != TagClasses.ModelAnimations )
			return null;

		var graph = new ModelAnimationsTag( cache, graphTag );
		var available = Enumerable.Range( 0, model.AnimationCount ).Select( model.GetAnimationName ).ToHashSet();

		// The model holds this object's graph either as its main graph or under the graph's prefix.
		Choice? Use( ModelAnimation animation, bool looping )
		{
			if ( animation is null )
				return null;

			var prefixed = GbxSkeleton.AnimationName( graphTag, animation.Name, false );
			if ( available.Contains( prefixed ) )
				return new Choice( prefixed, looping, 1, animation, graph );

			return available.Contains( animation.Name ) ? new Choice( animation.Name, looping, 1, animation, graph ) : null;
		}

		ModelAnimation ByIndex( int index ) => index >= 0 && index < graph.Animations.Count ? graph.Animations[index] : null;

		switch ( placement.Kind )
		{
			case "biped":
				return placement.Dead
					? Use( PickDeathPose( graph.Animations ), false )
					: Use( PickIdle( graph.Animations ), true );

			case "vehicle":
				return Use( PickIdle( graph.Animations ), true );
		}

		// Devices move between position 0 and 1 by playing their position animation. Gears run
		// through it continuously; anything placed open holds the end of it.
		if ( placement.Kind is "machine" or "control" && graph.DeviceAnimations.Count > 0 && graph.DeviceAnimations[0].Count > 0 )
		{
			var position = ByIndex( graph.DeviceAnimations[0][0] );
			if ( MachineTag.GetType( cache, placement.Object ) == MachineType.Gear && Use( position, true ) is { } gear )
			{
				// The animation spans positions 0 to 1, which a gear goes through in its position
				// transition time. One whose position loops spans the frames and the step from the
				// last back to the first: the wrapped copy, frame count steps long.
				var steps = position.FrameCount - 1;
				if ( MachineTag.PositionLoops( cache, placement.Object ) && available.Contains( gear.Sequence + GbxSkeleton.WrappedSuffix ) )
				{
					gear = gear with { Sequence = gear.Sequence + GbxSkeleton.WrappedSuffix };
					steps = position.FrameCount;
				}

				var seconds = MachineTag.GetPositionTransitionTime( cache, placement.Object );
				var length = Math.Max( steps, 1 ) / position.FrameRate;
				return seconds > 0 ? gear with { Rate = length / seconds } : gear;
			}

			if ( placement.InitiallyOpen )
				return Use( position, false );
		}

		if ( FunctionAnimatedKinds.Contains( placement.Kind ) )
		{
			foreach ( var entry in graph.ObjectAnimations )
			{
				if ( Use( ByIndex( entry.AnimationIndex ), true ) is { } choice )
					return choice;
			}
		}

		return null;
	}

	/// <summary>
	/// The idle a unit plays with nothing to do. Unit animations are named
	/// "&lt;stance or seat&gt; &lt;weapon class&gt; &lt;animation&gt;", with "%n" suffixes for variants.
	/// Placed bipeds carry no weapon here, so unarmed idles win, and standing beats alert or crouched.
	/// </summary>
	static ModelAnimation PickIdle( IReadOnlyList<ModelAnimation> animations )
	{
		return animations
			.Where( a => a.Type == AnimationType.Base && BaseName( a.Name ).EndsWith( "idle", StringComparison.OrdinalIgnoreCase ) )
			.OrderBy( a => IdleScore( BaseName( a.Name ) ) )
			.ThenBy( a => a.Name, StringComparer.OrdinalIgnoreCase )
			.FirstOrDefault();
	}

	static int IdleScore( string name )
	{
		var words = name.ToLowerInvariant().Split( ' ', StringSplitOptions.RemoveEmptyEntries );
		if ( words.Length < 3 )
			return 0; // Just "idle": the object's only one.

		var stance = words[0] switch { "stand" => 0, "alert" => 1, "crouch" => 2, _ => 3 };
		var weapon = words[1] switch { "unarmed" => 0, "fixed" => 0, "rifle" => 1, "pistol" => 2, _ => 3 };
		return stance * 10 + weapon;
	}

	/// <summary>
	/// A body: the end of the animation of falling dead on the ground.
	/// </summary>
	static ModelAnimation PickDeathPose( IReadOnlyList<ModelAnimation> animations )
	{
		string[] preferred = ["stand landing-dead", "landing-dead", "death_pose", "airborne-dead", "dead"];
		foreach ( var name in preferred )
		{
			var match = animations
				.Where( a => a.Type == AnimationType.Base && BaseName( a.Name ).Contains( name, StringComparison.OrdinalIgnoreCase ) )
				.OrderBy( a => a.Name, StringComparer.OrdinalIgnoreCase )
				.FirstOrDefault();
			if ( match is not null )
				return match;
		}

		return null;
	}

	/// <summary>
	/// The animation name without its variant suffix ("stand rifle idle%1" → "stand rifle idle").
	/// </summary>
	static string BaseName( string name )
	{
		var percent = name.IndexOf( '%' );
		return percent < 0 ? name : name[..percent];
	}
}
