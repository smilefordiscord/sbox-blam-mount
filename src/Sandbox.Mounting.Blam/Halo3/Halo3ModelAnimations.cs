using Blam.Halo3;
using Blam.Halo3.Tags;

namespace Sandbox.Mounting.Blam.Halo3;

// Gives a Halo 3 model its hlmt graph's animations (ModelAnimationSet), as Halo2ModelAnimations: every animation of the graph and the graphs it inherits, named as Halo names them (combat:rifle:idle), at 30 frames per second.
// Frames are bone poses relative to their parents. Root motion isn't baked in (a walk plays on the spot); overlays are baked over the default pose.
static class Halo3ModelAnimations
{
	// Whether a single node model needs a one bone skeleton to play its graph's animations (spinning fans, radar dishes).
	public static bool NeedsSkeleton( RenderModelTag model, ModelTag hlmt )
	{
		if ( model.Nodes.Count != 1 || hlmt?.Animation is not { } tag )
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

	// Adds the animations to a model whose bones are the render model's nodes, in order; returns how many were added.
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

		if ( set is null )
			return 0;

		var added = 0;
		var frame = new Transform[model.Nodes.Count];
		foreach ( var entry in set.Animations )
		{
			global::Blam.Halo1.Tags.NodePose[][] frames;
			try
			{
				frames = new global::Blam.Halo1.Tags.NodePose[entry.Animation.FrameCount][];
				for ( var f = 0; f < frames.Length; f++ )
					frames[f] = set.LocalPose( entry, f );
			}
			catch ( Exception )
			{
				// Stripped from this map's copy, or data that doesn't decode.
				continue;
			}

			AddSequence( builder, entry.Name, entry.Animation.FrameRate, frames, frame, scale );
			added++;

			// A device held where the scenario starts it: its position animation's first frame (closed, 0) and last (open, 1), each a still sequence.
			if ( entry.Name == DevicePosition && frames.Length > 0 )
			{
				AddSequence( builder, DeviceClosed, entry.Animation.FrameRate, [frames[0], frames[0]], frame, scale );
				AddSequence( builder, DeviceOpen, entry.Animation.FrameRate, [frames[^1], frames[^1]], frame, scale );
				added += 2;
			}

			// s&box loops from the last frame to the first as if they were the same pose: a gear whose cycle doesn't close also gets a copy with the closing frame, for turning continuously.
			if ( entry.Name == DevicePosition && set.ClosingFrame( entry ) is { } closing )
			{
				AddSequence( builder, LoopingPosition, entry.Animation.FrameRate, [.. frames, closing], frame, scale );
				added++;
			}
		}

		return added;
	}

	// A device's position animation (position 0 to 1), and its copy with the frame that closes its loop when it doesn't close by itself.
	public const string DevicePosition = "device:position";
	public const string LoopingPosition = "device:position:loop";
	public const string DeviceClosed = "device:position:closed";
	public const string DeviceOpen = "device:position:open";

	static void AddSequence( ModelBuilder builder, string name, float frameRate, global::Blam.Halo1.Tags.NodePose[][] frames, Transform[] frame, float scale )
	{
		var output = builder.AddAnimation( name, frameRate );
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
	}
}
