using Blam.Halo2;
using Blam.Halo2.Tags;

namespace Sandbox.Mounting.Blam.Halo2;

/// <summary>
/// Gives a Halo 2 model the animations of its model tag's graph (<see cref="ModelAnimationSet"/>): every animation of
/// the graph and of the graphs it inherits, named as Halo names them (<c>combat:rifle:idle</c>,
/// <c>warthog_d:enter</c>), at 30 frames per second, like the Halo CE mount's models.
/// </summary>
/// <remarks>
/// Frames are bone poses relative to their parents, the way s&amp;box takes them and Halo stores them. Root motion
/// (an animation's per-frame movement) isn't baked in: the root stays put, so a walk plays on the spot, as in Halo,
/// where the movement moves the object instead. Overlays are baked over the default pose, so each plays on its own the
/// way it looks over an idle object (aiming grids are the identity at their centre frame).
/// </remarks>
static class Halo2ModelAnimations
{
	/// <summary>
	/// Whether a single node model (which would otherwise be a static mesh) needs a one bone skeleton for its
	/// animations: Zanzibar's wheel, blades and flywheels turn by animating their only node.
	/// </summary>
	public static bool NeedsSkeleton( RenderModelTag model, ModelTag hlmt )
	{
		if ( model.Nodes.Count != 1 || hlmt?.Animations is not { Address: not 0 } tag || tag.Class != AnimationGraphTag.Class )
			return false;

		try
		{
			return new AnimationGraphTag( tag ).Animations.Any( a => a.HasData && a.FrameCount > 1 );
		}
		catch ( Exception )
		{
			return false;
		}
	}

	/// <summary>
	/// Adds the animations to a model whose bones are <paramref name="model"/>'s nodes, in order.
	/// </summary>
	/// <returns>The number of animations added.</returns>
	public static int Add( ModelBuilder builder, RenderModelTag model, ModelTag hlmt, float scale )
	{
		ModelAnimationSet set;
		try
		{
			set = ModelAnimationSet.For( model, hlmt );
		}
		catch ( Exception )
		{
			return 0;
		}

		return set is null ? 0 : Add( builder, set, scale );
	}

	// Adds a set's animations to a model whose bones are the set's nodes, in order.
	// basePose gives the pose an overlay or replacement is baked over (null: the default pose).
	public static int Add( ModelBuilder builder, ModelAnimationSet set, float scale, Func<RenderModelAnimation, IReadOnlyList<global::Blam.Halo1.Tags.NodePose>> basePose = null )
	{
		var added = 0;
		var frame = new Transform[set.Nodes.Count];
		foreach ( var entry in set.Animations )
		{
			global::Blam.Halo1.Tags.NodePose[][] frames;
			try
			{
				var under = entry.Animation.Type == global::Blam.Halo1.Tags.AnimationType.Base ? null : basePose?.Invoke( entry );
				frames = new global::Blam.Halo1.Tags.NodePose[entry.Animation.FrameCount][];
				for ( var f = 0; f < frames.Length; f++ )
					frames[f] = set.LocalPose( entry, f, under );
			}
			catch ( Exception )
			{
				continue;
			}

			var output = builder.AddAnimation( entry.Name, entry.Animation.FrameRate );
			output.WithLooping( true );
			foreach ( var pose in frames )
			{
				for ( var i = 0; i < frame.Length; i++ )
				{
					var p = pose[i];
					frame[i] = new Transform( (Vector3)p.Translation * scale, new Rotation( p.Rotation.X, p.Rotation.Y, p.Rotation.Z, p.Rotation.W ), p.Scale );
				}

				output.AddFrame( frame.AsSpan() );
			}

			added++;
		}

		return added;
	}
}
