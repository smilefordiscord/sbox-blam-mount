using Blam.Halo2;
using Blam.Halo2.Tags;

namespace Sandbox.Mounting.Blam.Halo2;

/// <summary>
/// Picks what a placed Halo 2 object is doing when the level starts, the way MCC shows it with nothing interacting
/// with it, like the Halo CE mount's ObjectAnimationPicker: gears turn, characters idle. Everything else stays in its
/// model's default pose.
/// </summary>
static class Halo2ObjectAnimations
{
	/// <param name="Rate">Playback speed.</param>
	public readonly record struct Choice( string Sequence, bool Looping, float Rate = 1 );

	/// <summary>
	/// Adds the renderer for a placed object: a skinned renderer playing its animation if it has one to play,
	/// otherwise a plain renderer. Units (bipeds, vehicles) with animations get a skinned renderer either way, in their
	/// rest pose for a game to pose.
	/// </summary>
	public static ModelRenderer AddRenderer( GameObject go, Model model, TagEntry objectEntry, ObjectTag objectTag, ModelTag hlmt )
	{
		Choice? choice = null;
		if ( model.AnimationCount > 0 )
		{
			try
			{
				choice = Pick( model, objectEntry, objectTag, hlmt );
			}
			catch ( Exception )
			{
				choice = null;
			}
		}

		var unit = model.AnimationCount > 0 && objectTag.Type is ObjectType.Biped or ObjectType.Vehicle;
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
			skinned.Sequence.Name = c.Sequence;
			skinned.Sequence.Looping = c.Looping;
			skinned.PlaybackRate = c.Rate;

			// A single node turning with PRT shaders: its light has to turn into the node's frame every frame.
			if ( model.BoneCount == 1 && hlmt?.RenderModel is { Address: not 0 } renderModel && renderModel.Class == RenderModelTag.Class
				&& new RenderModelTag( renderModel ).HasPrt )
				go.Tags.Add( AnimatedPrt.Tag );
		}

		return skinned;
	}

	public static Choice? Pick( Model model, TagEntry objectEntry, ObjectTag objectTag, ModelTag hlmt )
	{
		if ( hlmt?.RenderModel is not { Address: not 0 } renderModel || renderModel.Class != RenderModelTag.Class )
			return null;

		var set = ModelAnimationSet.For( new RenderModelTag( renderModel ), hlmt );
		if ( set is null )
			return null;

		var available = Enumerable.Range( 0, model.AnimationCount ).Select( model.GetAnimationName ).ToHashSet();
		Choice? Use( RenderModelAnimation animation, bool looping, float rate = 1 ) =>
			animation is not null && available.Contains( animation.Name ) ? new Choice( animation.Name, looping, rate ) : null;

		switch ( objectTag.Type )
		{
			case ObjectType.Biped:
				// Placed characters hold no weapon here: the graph's unarmed idle (MCC's chief: the sword idle).
				return Use( set.FindLabel( "combat", "unarmed", "any", "idle" ) ?? Idle( set ), true );

			case ObjectType.Machine when MachineTag.Read( objectEntry ) is { Type: MachineType.Gear, PositionRate: > 0 } machine:
			{
				// Gears run through their position animation (position 0 to 1) continuously, at the tag's rate in
				// positions per second (Zanzibar's wheel 1/30, flywheels 1/5: measured live as 30 s and 5 s turns). The
				// animation's first and last frames are the same pose (0 and 1), so a cycle is its frame count less one.
				var position = set.FindLabel( "any", "any", "any", "position" ) ?? set.Find( "device:position" );
				if ( position is null )
					return null;

				var cycle = Math.Max( position.Animation.FrameCount - 1, 1 ) / position.Animation.FrameRate;
				return Use( position, true, cycle * machine.PositionRate );
			}
		}

		return null;
	}

	/// <summary>
	/// A unit's idle for graphs without the unarmed label: the shortest named <c>...:idle</c> base animation.
	/// </summary>
	static RenderModelAnimation Idle( ModelAnimationSet set ) => set.Animations
		.Where( a => a.Animation.Type == global::Blam.Halo1.Tags.AnimationType.Base && a.Name.EndsWith( ":idle", StringComparison.OrdinalIgnoreCase ) )
		.OrderBy( a => a.Name.Length ).ThenBy( a => a.Name, StringComparer.OrdinalIgnoreCase )
		.FirstOrDefault();
}
