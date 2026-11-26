using Blam.Halo1;
using Blam.Halo1.Tags;

namespace Sandbox.Mounting.Blam.Halo1;

/// <summary>
/// Bakes a unit's aiming into its animations, for the <c>&lt;model&gt;_aim.vmdl</c> variant of a
/// biped's model: s&amp;box plays one sequence on a renderer without an animation graph, and
/// animation graphs can't be built at runtime, so Halo's aiming overlay can't be layered on top
/// of the base animation while the game runs.
/// </summary>
/// <remarks>
/// <para>Halo poses a unit with its base animation (idle, moving) and then applies the weapon
/// class's aiming overlay: "aim-still" while standing, turning or in the air, "aim-move" while
/// moving. The overlay's frames are a grid over yaw and pitch (see <see cref="AimingScreenBounds"/>;
/// the cyborg's rifle grid is 3x3 at +-90 degrees), read bilinearly at the unit's aim relative to its
/// facing, and each node's rotation and translation in it are added to the base pose.</para>
///
/// <para>For the "stand" and "crouch" seats, every weapon class with an aiming overlay gets:
/// <list type="bullet">
/// <item>"&lt;seat&gt; &lt;class&gt; idle aim p&lt;pitch&gt; y&lt;yaw&gt;": the idle with aim-still, at
/// every <see cref="PitchStep"/> degrees of pitch and <see cref="YawStep"/> of yaw within the grid
/// (at 15 frames per second: idles are slow);</item>
/// <item>"&lt;seat&gt; &lt;class&gt; move-front|back|left|right aim p&lt;pitch&gt; y+0": the moves with
/// aim-move, facing straight ahead (bodies face where they move);</item>
/// <item>"&lt;seat&gt; &lt;class&gt; airborne aim p&lt;pitch&gt; y+0": falling, with aim-still.</item>
/// </list>
/// Angles are whole degrees with a sign ("p+30 y-45", "p-15 y+0"): pitch up positive, yaw to the
/// unit's left positive, as in Halo. Seat and class are the graph's labels ("stand rifle"), and the
/// slot names are Halo's whatever the animation is called. All loop.</para>
/// </remarks>
static class AimPoses
{
	public const int PitchStep = 15;
	public const int YawStep = 45;

	static readonly string[] Seats = ["stand", "crouch"];
	static readonly string[] MoveSlots = ["move-front", "move-back", "move-left", "move-right"];

	/// <summary>
	/// The sequence name for a slot of a seat and weapon class at an aim (whole degrees).
	/// </summary>
	public static string Name( string seat, string weaponClass, string slot, int pitch, int yaw ) =>
		$"{seat} {weaponClass} {slot} aim p{pitch:+0;-0;+0} y{yaw:+0;-0;+0}";

	/// <summary>
	/// Adds the aim sequences for a graph's unit seats to a model.
	/// </summary>
	/// <param name="map">The bone each animation node drives, -1 for none.</param>
	/// <param name="bindPose">Each bone's parent-relative pose when nothing animates it.</param>
	/// <returns>The number of sequences added.</returns>
	public static int Add( ModelBuilder builder, ModelAnimationsTag graph, int[] map, Transform[] bindPose, float scale, Sandbox.Diagnostics.Logger log )
	{
		var added = 0;
		var frames = 0;
		var decoded = new Dictionary<int, NodePose[]>();

		NodePose[] Frames( ModelAnimation animation )
		{
			if ( !decoded.TryGetValue( animation.Index, out var poses ) )
				decoded[animation.Index] = poses = animation.CanDecode && animation.NodeCount == map.Length ? animation.ReadFrames() : null;
			return poses;
		}

		ModelAnimation Slot( UnitWeaponClass weaponClass, string slot )
		{
			var index = weaponClass.Animation( slot );
			return index >= 0 && index < graph.Animations.Count ? graph.Animations[index] : null;
		}

		foreach ( var seat in graph.UnitSeats.Where( s => Seats.Contains( s.Label, StringComparer.OrdinalIgnoreCase ) ) )
		{
			foreach ( var weaponClass in seat.WeaponClasses )
			{
				var grid = weaponClass.AimingScreen;
				if ( grid.Width * grid.Height < 2 )
					continue;

				var still = Slot( weaponClass, "aim-still" );
				var moving = Slot( weaponClass, "aim-move" ) ?? still;
				if ( still is null || still.Type != AnimationType.Overlay || still.FrameCount < grid.Width * grid.Height || Frames( still ) is null )
					continue;
				if ( moving.Type != AnimationType.Overlay || moving.FrameCount < grid.Width * grid.Height || Frames( moving ) is null )
					moving = still;

				var pitches = Angles( grid.MinPitch, grid.MaxPitch, PitchStep );
				var yaws = Angles( grid.MinYaw, grid.MaxYaw, YawStep );

				void Bake( string slot, ModelAnimation overlay, IEnumerable<int> yawAngles, int frameStep )
				{
					var animation = Slot( weaponClass, slot );
					if ( animation is null || animation.Type != AnimationType.Base || Frames( animation ) is not { } basePoses )
						return;

					foreach ( var pitch in pitches )
					foreach ( var yaw in yawAngles )
					{
						var name = Name( seat.Label, weaponClass.Label, slot, pitch, yaw );
						var output = builder.AddAnimation( name, animation.FrameRate / frameStep );
						output.WithLooping( true );

						var cell = grid.Locate( yaw * MathF.PI / 180, pitch * MathF.PI / 180 );
						for ( var f = 0; f < animation.FrameCount; f += frameStep )
						{
							output.AddFrame( Pose( animation, basePoses, f, overlay, Frames( overlay ), cell, map, bindPose, scale ).AsSpan() );
							frames++;
						}

						added++;
					}
				}

				Bake( "idle", still, yaws, 2 );
				foreach ( var slot in MoveSlots )
					Bake( slot, moving, [0], 1 );
				Bake( "airborne", still, [0], 2 );
			}
		}

		if ( added > 0 )
			log.Info( $"{graph.Tag}: baked {added} aim sequences ({frames} frames)" );

		return added;
	}

	/// <summary>
	/// Whole degrees from <paramref name="min"/> to <paramref name="max"/> (radians) in steps, always
	/// including 0.
	/// </summary>
	static List<int> Angles( float min, float max, int step )
	{
		var from = (int)MathF.Ceiling( min * 180 / MathF.PI / step - 1e-3f );
		var to = (int)MathF.Floor( max * 180 / MathF.PI / step + 1e-3f );
		var angles = new List<int>();
		for ( var i = Math.Min( from, 0 ); i <= Math.Max( to, 0 ); i++ )
			angles.Add( i * step );
		return angles;
	}

	/// <summary>
	/// One frame of a base animation with the aiming overlay applied at a grid cell: each node the
	/// overlay turns gets the bilinear blend of the four surrounding frames' rotations after its base
	/// rotation, and each node it moves the blend of their translations added.
	/// </summary>
	static Transform[] Pose( ModelAnimation animation, NodePose[] poses, int frame, ModelAnimation overlay, NodePose[] overlayPoses,
		(int Frame00, int Frame10, int Frame01, int Frame11, float YawFraction, float PitchFraction) cell, int[] map, Transform[] bindPose, float scale )
	{
		var result = (Transform[])bindPose.Clone();
		var nodes = animation.NodeCount;
		for ( var n = 0; n < nodes; n++ )
		{
			var bone = map[n];
			if ( bone < 0 )
				continue;

			var pose = poses[frame * nodes + n];
			var rotation = pose.Rotation;
			var translation = pose.Translation;

			if ( overlay.AnimatesRotation( n ) )
			{
				var r00 = overlayPoses[cell.Frame00 * nodes + n].Rotation;
				var r10 = overlayPoses[cell.Frame10 * nodes + n].Rotation;
				var r01 = overlayPoses[cell.Frame01 * nodes + n].Rotation;
				var r11 = overlayPoses[cell.Frame11 * nodes + n].Rotation;
				var blend = Nlerp( Nlerp( r00, r10, cell.YawFraction ), Nlerp( r01, r11, cell.YawFraction ), cell.PitchFraction );
				rotation = System.Numerics.Quaternion.Normalize( rotation * blend );
			}

			if ( overlay.AnimatesTranslation( n ) )
			{
				System.Numerics.Vector3 T( int f ) => overlayPoses[f * nodes + n].Translation;
				var row0 = System.Numerics.Vector3.Lerp( T( cell.Frame00 ), T( cell.Frame10 ), cell.YawFraction );
				var row1 = System.Numerics.Vector3.Lerp( T( cell.Frame01 ), T( cell.Frame11 ), cell.YawFraction );
				translation += System.Numerics.Vector3.Lerp( row0, row1, cell.PitchFraction );
			}

			result[bone] = new Transform( (Vector3)translation * scale, GbxSkeleton.ToRotation( rotation ), pose.Scale );
		}

		return result;
	}

	/// <summary>
	/// Normalized linear blend along the shorter arc.
	/// </summary>
	static System.Numerics.Quaternion Nlerp( System.Numerics.Quaternion a, System.Numerics.Quaternion b, float t )
	{
		if ( System.Numerics.Quaternion.Dot( a, b ) < 0 )
			b = System.Numerics.Quaternion.Negate( b );
		return System.Numerics.Quaternion.Normalize( System.Numerics.Quaternion.Lerp( a, b, t ) );
	}
}
