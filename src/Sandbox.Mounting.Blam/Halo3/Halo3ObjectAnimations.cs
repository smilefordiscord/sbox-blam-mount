using Blam.Halo3;
using Blam.Halo3.Tags;

namespace Sandbox.Mounting.Blam.Halo3;

// The renderer for a placed object, as Halo2ObjectAnimations: a skinned renderer playing what ObjectAnimationChoice picks (units idle, scenery idles, gears turn), else a plain renderer.
// Units (bipeds, vehicles, giants) with animations get a skinned renderer either way, in their rest pose for a game to pose.
static class Halo3ObjectAnimations
{
	// devicePosition: where a placed device starts (ScenarioPlacement.DevicePosition), -1 for none.
	public static ModelRenderer AddRenderer( GameObject go, Model model, ObjectTag objectTag, ObjectModel hlmt, float devicePosition = -1 )
	{
		ObjectAnimationChoice? choice = null;
		if ( objectTag is not null && model.AnimationCount > 0 )
		{
			try
			{
				choice = Pick( model, objectTag, hlmt ) ?? DevicePose( model, devicePosition );
			}
			catch ( Exception )
			{
				choice = null;
			}
		}

		var unit = objectTag is not null && model.AnimationCount > 0 && objectTag.Type is ObjectType.Biped or ObjectType.Vehicle or ObjectType.Giant;
		if ( choice is null && !unit )
		{
			var renderer = go.AddComponent<ModelRenderer>();
			renderer.Model = model;
			return renderer;
		}

		var skinned = go.AddComponent<SkinnedModelRenderer>();
		skinned.Model = model;
		skinned.UseAnimGraph = false;
		if ( choice is { } c )
		{
			skinned.Sequence.Name = c.Animation;
			skinned.Sequence.Looping = c.Looping;
			skinned.PlaybackRate = c.Rate;
		}

		return skinned;
	}

	// A door or platform held at its starting position (0 or 1 in every scenario; in between rounds to the nearer end) by its position animation's still first or last frame.
	static ObjectAnimationChoice? DevicePose( Model model, float position )
	{
		if ( position < 0 )
			return null;

		var name = position >= 0.5f ? Halo3ModelAnimations.DeviceOpen : Halo3ModelAnimations.DeviceClosed;
		for ( var i = 0; i < model.AnimationCount; i++ )
		{
			if ( model.GetAnimationName( i ) == name )
				return new ObjectAnimationChoice( name, true, 1, false );
		}
		return null;
	}

	static ObjectAnimationChoice? Pick( Model model, ObjectTag objectTag, ObjectModel hlmt )
	{
		var modelTag = new ModelTag( hlmt.Tag );
		if ( modelTag.RenderModel is not { } renderModel )
			return null;

		if ( ObjectAnimationChoice.Pick( objectTag.Tag, objectTag.Type, ModelAnimationSet.For( new RenderModelTag( renderModel ), modelTag ) ) is not { } choice )
			return null;

		// The model's copy of the hlmt may come from another map: play only what it has.
		var available = Enumerable.Range( 0, model.AnimationCount ).Select( model.GetAnimationName ).ToHashSet();
		if ( choice.ClosesLoop && available.Contains( Halo3ModelAnimations.LoopingPosition ) )
			return choice with { Animation = Halo3ModelAnimations.LoopingPosition };

		return available.Contains( choice.Animation ) ? choice : null;
	}
}
