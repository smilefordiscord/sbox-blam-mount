using System.Runtime.InteropServices;
using Blam.Halo1;
using Blam.Halo1.Tags;
using Blam.Halo1.TagData;

namespace Sandbox.Mounting.Blam.Halo1;

/// <summary>
/// <see cref="HaloVertex"/> plus the up to two nodes a model vertex is weighted to.
/// </summary>
[StructLayout( LayoutKind.Sequential )]
struct HaloSkinnedVertex
{
	[VertexLayout.Position] public Vector3 Position;
	[VertexLayout.Normal] public Vector3 Normal;
	[VertexLayout.Tangent] public Vector4 Tangent;
	[VertexLayout.TexCoord( 0 )] public Vector2 TexCoord;
	[VertexLayout.TexCoord( 1 )] public Vector2 LightmapTexCoord;
	[VertexLayout.BlendIndices] public Color32 BlendIndices;
	[VertexLayout.BlendWeight] public Color32 BlendWeights;
}

/// <summary>
/// Gives gbxmodels their node skeleton, skinning and the animations of the animation graph
/// their objects use.
/// </summary>
/// <remarks>
/// s&amp;box takes bones in model space (the built-in mounts all pass accumulated transforms, the
/// doc comment notwithstanding) and animation frames relative to the parent bone, which is how
/// Halo stores both. Halo's vertices are already in model space in the default pose, so skinning
/// them to that pose leaves the model exactly as it renders without a skeleton.
/// </remarks>
static class GbxSkeleton
{
	/// <summary>
	/// Adds the node weights of Halo's vertices to the vertices built for them.
	/// </summary>
	/// <param name="bones">The bone each of the model's nodes is skinned to.</param>
	public static List<HaloSkinnedVertex> Skin( List<HaloVertex> vertices, ModelVertex[] source, IReadOnlyList<int> bones )
	{
		var skinned = new List<HaloSkinnedVertex>( vertices.Count );
		for ( var i = 0; i < vertices.Count; i++ )
		{
			var v = vertices[i];
			var s = source[i];

			// The second node only counts if it has weight; weights are stored as floats summing to 1.
			var node0 = Math.Max( s.Node0, 0 );
			var node1 = s.Node1 >= 0 && s.Weight1 > 0 ? s.Node1 : node0;
			var weight1 = node1 == node0 ? 0 : Math.Clamp( (int)MathF.Round( s.Weight1 / MathF.Max( s.Weight0 + s.Weight1, 1e-6f ) * 255 ), 0, 255 );
			var bone0 = node0 < bones.Count ? bones[node0] : 0;
			var bone1 = node1 < bones.Count ? bones[node1] : bone0;

			skinned.Add( new HaloSkinnedVertex
			{
				Position = v.Position,
				Normal = v.Normal,
				Tangent = v.Tangent,
				TexCoord = v.TexCoord,
				LightmapTexCoord = v.LightmapTexCoord,
				BlendIndices = new Color32( (byte)bone0, (byte)bone1, 0, 0 ),
				BlendWeights = new Color32( (byte)(255 - weight1), (byte)weight1, 0, 0 ),
			} );
		}

		return skinned;
	}

	/// <summary>
	/// Adds the model's nodes as bones, and the animations of every graph in <paramref name="graphs"/>
	/// (named by <see cref="AnimationName"/>).
	/// </summary>
	/// <param name="vertices">Every skinned vertex of the model, to size each bone's bounds with.</param>
	/// <param name="graphs">The model's animation graphs, main graph first (see <see cref="FindAnimations"/>).</param>
	/// <param name="scale">s&amp;box units per world unit.</param>
	/// <param name="boundsPadding">Grows every bone's bounds by this much (see ObjectGlass).</param>
	/// <param name="aimPoses">Also bake the main graph's aiming into sequences (see <see cref="AimPoses"/>).</param>
	/// <returns>The number of animations added.</returns>
	public static int Build( ModelBuilder builder, GbxModelTag model, IReadOnlyList<HaloSkinnedVertex> vertices, IReadOnlyList<ModelAnimationsTag> graphs, float scale, Sandbox.Diagnostics.Logger log, float boundsPadding = 0, bool aimPoses = false )
	{
		var nodes = model.Nodes;
		var names = BoneNames( nodes.Select( n => n.Name ) );
		var local = nodes.Select( n => new Transform( (Vector3)n.Translation * scale, ToRotation( n.Rotation ) ) ).ToArray();
		var world = DefaultPose( model, scale );

		AddBones( builder, names, nodes.Select( n => n.ParentIndex ).ToArray(), world, vertices, boundsPadding );

		var added = 0;
		for ( var g = 0; g < graphs.Count; g++ )
		{
			var graph = graphs[g];
			var map = MapNodes( model, graph, names );
			var positions = graph.DeviceAnimations.Where( d => d.Count > 0 ).Select( d => d[0] ).ToHashSet();

			for ( var i = 0; i < graph.Animations.Count; i++ )
			{
				var animation = graph.Animations[i];
				if ( !animation.CanDecode || animation.NodeCount != map.Length )
					continue;

				var name = AnimationName( graph.Tag, animation.Name, g == 0 );
				if ( AddAnimation( builder, name, animation, map, local, local, scale, true, graph.Tag, log ) )
					added++;

				// A device whose position loops plays its last frame into frame 0 (see WrappedSuffix).
				if ( positions.Contains( i ) && AddAnimation( builder, name + WrappedSuffix, animation, map, local, local, scale, true, graph.Tag, log, wrap: true ) )
					added++;
			}
		}

		if ( aimPoses && graphs.Count > 0 )
			added += AimPoses.Add( builder, graphs[0], MapNodes( model, graphs[0], names ), local, scale, log );

		return added;
	}

	/// <summary>
	/// A model's nodes in their default pose, in model space (s&amp;box units).
	/// </summary>
	public static Transform[] DefaultPose( GbxModelTag model, float scale )
	{
		var world = new Transform[model.Nodes.Count];
		for ( var i = 0; i < world.Length; i++ )
		{
			var node = model.Nodes[i];
			var local = new Transform( (Vector3)node.Translation * scale, ToRotation( node.Rotation ) );
			world[i] = node.ParentIndex < 0 || node.ParentIndex >= i ? local : world[node.ParentIndex].ToWorld( local );
		}

		return world;
	}

	/// <summary>
	/// Adds the bones, parents first.
	/// </summary>
	/// <param name="parents">Each bone's parent (an index into <paramref name="names"/>), -1 for roots.</param>
	/// <param name="world">Each bone's default pose in model space, the pose its vertices were modelled in.</param>
	/// <param name="vertices">Every skinned vertex of the model, to size each bone's bounds with.</param>
	/// <param name="boundsPadding">Grows every bone's bounds by this much (see ObjectGlass).</param>
	public static void AddBones( ModelBuilder builder, IReadOnlyList<string> names, IReadOnlyList<int> parents, Transform[] world, IReadOnlyList<HaloSkinnedVertex> vertices, float boundsPadding = 0 )
	{
		// A skinned model's render bounds are its bones' bounds in their current pose, which
		// default to the bone's origin, so each bone gets the box of the vertices it moves.
		var bounds = new BBox?[names.Count];
		foreach ( var v in vertices )
		{
			Include( v.BlendIndices.r, v.BlendWeights.r, v.Position );
			Include( v.BlendIndices.g, v.BlendWeights.g, v.Position );
		}

		void Include( int bone, int weight, Vector3 position )
		{
			if ( weight == 0 || bone >= names.Count )
				return;

			var point = world[bone].PointToLocal( position );
			bounds[bone] = bounds[bone] is { } box ? box.AddPoint( point ) : new BBox( point, point );
		}

		for ( var i = 0; i < names.Count; i++ )
		{
			var parent = parents[i] < 0 ? null : names[parents[i]];
			var box = bounds[i] ?? new BBox( Vector3.Zero, Vector3.Zero );
			builder.AddBone( names[i], world[i].Position, world[i].Rotation, new BBox( box.Mins - boundsPadding, box.Maxs + boundsPadding ), parent );
		}
	}

	/// <summary>
	/// The model's name for an animation: the main graph's animations keep their names, other
	/// graphs' (a wounded marine's poses on the marine model, a cutscene's clips) are prefixed with
	/// the graph's name, since graphs reuse names like "stand unarmed idle".
	/// </summary>
	/// <summary>
	/// Added to a device position animation's name for its copy that ends on frame 0 again. A
	/// device flagged "position loops" reads its animation at position x frame count, the last
	/// frame blending into the first (a10's shield charge and the holo panels turn 90 degrees a
	/// frame, the turn from the last frame back to the first included), where a looping sequence
	/// jumps from its last frame back to its first.
	/// </summary>
	public const string WrappedSuffix = " wrapped";

	public static string AnimationName( TagEntry graph, string animation, bool mainGraph ) =>
		mainGraph ? animation : $"{System.IO.Path.GetFileName( HaloCeMount.ResourcePath( graph.Path ) )}:{animation}";

	/// <summary>
	/// The model bone each animation node drives, by name when the graph lists its nodes and by
	/// index when it doesn't (vehicle graphs often leave the list empty).
	/// </summary>
	static int[] MapNodes( GbxModelTag model, ModelAnimationsTag animations, string[] names )
	{
		if ( animations.NodeNames.Count == 0 )
			return Enumerable.Range( 0, model.Nodes.Count ).ToArray();

		var byName = new Dictionary<string, int>( StringComparer.OrdinalIgnoreCase );
		for ( var i = 0; i < model.Nodes.Count; i++ )
			byName.TryAdd( model.Nodes[i].Name, i );

		return animations.NodeNames.Select( n => byName.TryGetValue( n, out var i ) ? i : -1 ).ToArray();
	}

	/// <summary>
	/// Decodes an animation and adds it to the model. Every frame starts from <paramref name="bindPose"/>
	/// (for bones the animation doesn't drive), and overlays are applied to <paramref name="overlayBase"/>.
	/// </summary>
	/// <param name="map">The bone each animation node drives, -1 for none.</param>
	/// <param name="bindPose">Each bone's pose relative to its parent when nothing animates it.</param>
	/// <param name="overlayBase">The parent-relative pose overlays are baked onto, so they play on
	/// their own the way they look over it (usually the default pose).</param>
	/// <returns>Whether the animation could be read.</returns>
	/// <param name="wrap">End on a copy of frame 0, so a loop plays the last frame into the first
	/// instead of jumping back from it.</param>
	public static bool AddAnimation( ModelBuilder builder, string name, ModelAnimation animation, int[] map, Transform[] bindPose, Transform[] overlayBase, float scale, bool looping, TagEntry graph, Sandbox.Diagnostics.Logger log, bool wrap = false )
	{
		NodePose[] poses;
		try
		{
			poses = animation.ReadFrames();
		}
		catch ( Exception e )
		{
			log.Warning( $"{graph}: couldn't read animation {animation.Name}: {e.Message}" );
			return false;
		}

		var nodeCount = animation.NodeCount;
		var frame = new Transform[bindPose.Length];

		var output = builder.AddAnimation( name, animation.FrameRate );
		output.WithLooping( looping );

		for ( var i = 0; i < animation.FrameCount + (wrap ? 1 : 0); i++ )
		{
			var f = i % animation.FrameCount;
			bindPose.CopyTo( frame, 0 );

			for ( var n = 0; n < nodeCount; n++ )
			{
				var bone = map[n];
				if ( bone < 0 )
					continue;

				var pose = poses[f * nodeCount + n];
				if ( animation.Type == AnimationType.Overlay )
				{
					// Overlays hold changes relative to the pose underneath. Apply them to the base
					// pose, so each plays on its own the way it looks over an idle object.
					var under = overlayBase[bone];
					frame[bone] = new Transform(
						animation.AnimatesTranslation( n ) ? under.Position + (Vector3)pose.Translation * scale : under.Position,
						animation.AnimatesRotation( n ) ? under.Rotation * ToRotation( pose.Rotation ) : under.Rotation,
						animation.AnimatesScale( n ) ? under.UniformScale * pose.Scale : under.UniformScale );
				}
				else
				{
					frame[bone] = new Transform( (Vector3)pose.Translation * scale, ToRotation( pose.Rotation ), pose.Scale );
				}
			}

			output.AddFrame( frame.AsSpan() );
		}

		return true;
	}

	/// <summary>
	/// One frame of an animation as parent-relative bone poses, for bones it drives (others keep
	/// <paramref name="bindPose"/>). Overlays aren't applied to anything.
	/// </summary>
	public static Transform[] ReadPose( ModelAnimation animation, int frame, int[] map, Transform[] bindPose, float scale )
	{
		var poses = animation.ReadFrames();
		var result = (Transform[])bindPose.Clone();
		for ( var n = 0; n < animation.NodeCount; n++ )
		{
			if ( map[n] < 0 )
				continue;

			var pose = poses[frame * animation.NodeCount + n];
			result[map[n]] = new Transform( (Vector3)pose.Translation * scale, ToRotation( pose.Rotation ), pose.Scale );
		}

		return result;
	}

	/// <summary>
	/// Node names, made unique in case a model repeats one (bones are linked by name). Shared with
	/// the animation data (<see cref="AnimationExport"/>), which names bones the same way.
	/// </summary>
	public static string[] BoneNames( IEnumerable<string> nodeNames ) => AnimationSkeleton.UniqueNames( nodeNames );

	public static Rotation ToRotation( System.Numerics.Quaternion q ) => new( q.X, q.Y, q.Z, q.W );

	/// <summary>
	/// The animation graphs for a model. Graphs belong to objects rather than models, so these are
	/// the graphs of every object in the map using the model, largest first: the main graph is a
	/// character's own rather than a cutscene's few clips for the same model. After them come the
	/// graphs no object uses whose nodes are exactly the model's: cutscene clips, which scripts
	/// play on whichever unit they name (marines' clips fit every model with the marine skeleton).
	/// </summary>
	public static List<TagEntry> FindAnimations( CacheFile cache, TagEntry modelTag )
	{
		var graphs = new List<TagEntry>();
		var used = new HashSet<uint>();

		foreach ( var tag in cache.Tags )
		{
			if ( !tag.Is( TagClasses.Object ) )
				continue;

			var animations = ObjectTags.GetObjectAnimations( cache, tag );
			if ( animations is null || animations.Class != TagClasses.ModelAnimations )
				continue;

			used.Add( animations.Id );
			if ( ObjectTags.GetObjectModel( cache, tag )?.Id != modelTag.Id )
				continue;

			if ( !graphs.Any( g => g.Id == animations.Id ) )
				graphs.Add( animations );
		}

		// Stable, so equal sizes keep tag order.
		graphs = graphs.OrderByDescending( g => ModelAnimationsTag.CountAnimations( cache, g ) ).ToList();

		GbxModelTag model = null;
		foreach ( var tag in cache.Tags )
		{
			if ( tag.Class != TagClasses.ModelAnimations || used.Contains( tag.Id ) )
				continue;

			model ??= new GbxModelTag( cache, modelTag );
			if ( MatchesSkeleton( model, new ModelAnimationsTag( cache, tag ) ) )
				graphs.Add( tag );
		}

		return graphs;
	}

	/// <summary>
	/// Whether a graph's nodes are exactly the model's, by name.
	/// </summary>
	static bool MatchesSkeleton( GbxModelTag model, ModelAnimationsTag graph )
	{
		if ( graph.NodeNames.Count < 2 || graph.NodeNames.Count != model.Nodes.Count )
			return false;

		var names = model.Nodes.Select( n => n.Name ).ToHashSet( StringComparer.OrdinalIgnoreCase );
		return graph.NodeNames.All( names.Contains );
	}
}
