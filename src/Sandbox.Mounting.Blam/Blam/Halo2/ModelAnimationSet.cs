using System;
using System.Collections.Generic;
using System.Linq;
using Blam.Halo1.Tags;
using Blam.Halo2.Tags;

namespace Blam.Halo2;

// Inside the namespace so these win over s&box's global vector types when this compiles into the mount.
using Quaternion = System.Numerics.Quaternion;
using Vector3 = System.Numerics.Vector3;

/// <summary>
/// One animation a render model plays: the graph animation and the model node each of its nodes drives.
/// </summary>
/// <param name="Name">The animation's name (<c>combat:rifle:idle</c>), unique in the set.</param>
/// <param name="NodeOfAnimationNode">The model node each animation node drives, -1 for none.</param>
/// <param name="Inherited">Whether it comes from an inherited graph (the elite's for the dervish).</param>
/// <param name="RootZScale">The root node's height is scaled by this (the inheritance entries' "root z offset" it comes through,
/// multiplied: 1, or 0.9466 for graphs inherited onto a shorter skeleton).</param>
public sealed record RenderModelAnimation( string Name, GraphAnimation Animation, int[] NodeOfAnimationNode, bool Inherited, float RootZScale );

/// <summary>
/// The animations a Halo 2 render model plays from its model tag's (<c>hlmt</c>) animation graph: the graph's own and,
/// for names it doesn't have, those of the graphs it inherits, each graph's nodes put on the model's nodes, and the
/// poses they give the model.
/// </summary>
/// <remarks>
/// A graph's nodes find the model's by name (every graph a model tag uses has all its nodes in its render model).
/// An inherited graph's nodes go through the inheritance entry's node map to this graph's nodes first. Components an
/// animation doesn't set keep the model's default pose. Overlays (aiming, accelerations) hold changes from the pose
/// under them, the identity at their grid's centre; posing one here applies it over the default pose.
/// </remarks>
public sealed class ModelAnimationSet
{
	// The render model, or null for a skeleton of several (a first person weapon's).
	public RenderModelTag Model { get; }

	// The nodes posed: the model's, or the skeleton's.
	public IReadOnlyList<RenderModelNode> Nodes { get; }
	public AnimationGraphTag Graph { get; }
	public IReadOnlyList<RenderModelAnimation> Animations { get; }

	readonly Dictionary<string, RenderModelAnimation> _byName;

	ModelAnimationSet( RenderModelTag model, IReadOnlyList<RenderModelNode> nodes, AnimationGraphTag graph, List<RenderModelAnimation> animations )
	{
		Model = model;
		Nodes = nodes;
		Graph = graph;
		Animations = animations;
		_byName = animations.ToDictionary( a => a.Name, StringComparer.OrdinalIgnoreCase );
	}

	public RenderModelAnimation Find( string name ) => name is not null && _byName.TryGetValue( name, out var a ) ? a : null;

	/// <summary>
	/// The animation a label of the graph (<c>combat:unarmed:any:idle</c> as mode, class, type, label) points at, or null.
	/// </summary>
	public RenderModelAnimation FindLabel( string mode, string weaponClass, string weaponType, string label )
	{
		var entry = Graph.Labels.FirstOrDefault( l => !l.Animation.IsNone && Same( l.Mode, mode ) && Same( l.WeaponClass, weaponClass )
			&& Same( l.WeaponType, weaponType ) && Same( l.Label, label ) );
		return entry is null ? null : Resolve( entry.Animation );
	}

	/// <summary>
	/// The model's animation a reference of the graph points at, by the name of the animation it names.
	/// </summary>
	public RenderModelAnimation Resolve( AnimationReference reference )
	{
		if ( reference.IsNone || Graph.GraphOf( reference ) is not { } graph || reference.Animation >= graph.Animations.Count )
			return null;

		return Find( graph.Animations[reference.Animation].Name );
	}

	static bool Same( string a, string b ) => string.Equals( a, b, StringComparison.OrdinalIgnoreCase );

	/// <summary>
	/// The animations of a model tag's graph for its render model, or null when the model tag has no graph.
	/// </summary>
	public static ModelAnimationSet For( RenderModelTag model, ModelTag hlmt ) =>
		hlmt?.Animations is { Address: not 0 } tag && tag.Class == AnimationGraphTag.Class ? For( model, new AnimationGraphTag( tag ) ) : null;

	public static ModelAnimationSet For( RenderModelTag model, AnimationGraphTag graph ) => For( model, model.Nodes, graph );

	// The animations of a graph for a skeleton of nodes that aren't one render model's.
	public static ModelAnimationSet For( IReadOnlyList<RenderModelNode> nodes, AnimationGraphTag graph ) => For( null, nodes, graph );

	static ModelAnimationSet For( RenderModelTag model, IReadOnlyList<RenderModelNode> modelNodes, AnimationGraphTag graph )
	{
		var byName = new Dictionary<string, int>( StringComparer.OrdinalIgnoreCase );
		foreach ( var node in modelNodes )
			byName.TryAdd( node.Name ?? "", node.Index );

		var nodeOfGraphNode = graph.Nodes.Select( n => byName.TryGetValue( n.Name ?? "", out var i ) ? i : -1 ).ToArray();
		var animations = new List<RenderModelAnimation>();
		var names = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
		var visited = new HashSet<uint>();
		Collect( graph, nodeOfGraphNode, false, 1 );
		return new ModelAnimationSet( model, modelNodes, graph, animations );

		void Collect( AnimationGraphTag g, int[] nodes, bool inherited, float rootZ )
		{
			if ( !visited.Add( g.Tag.Id ) )
				return;

			foreach ( var animation in g.Animations )
			{
				if ( !animation.HasData || string.IsNullOrEmpty( animation.Name ) || animation.NodeCount > nodes.Length || !names.Add( animation.Name ) )
					continue;

				animations.Add( new RenderModelAnimation( animation.Name, animation, nodes, inherited, rootZ ) );
			}

			foreach ( var entry in g.Inheritance )
			{
				if ( entry.Graph is not { Address: not 0 } tag || tag.Class != AnimationGraphTag.Class )
					continue;

				var other = new AnimationGraphTag( tag );
				var map = new int[other.Nodes.Count];
				for ( var k = 0; k < map.Length; k++ )
				{
					var local = k < entry.NodeMap.Count ? entry.NodeMap[k] : -1;
					map[k] = local >= 0 && local < nodes.Length ? nodes[local] : -1;
				}

				Collect( other, map, true, rootZ * (entry.RootZScale > 0 ? entry.RootZScale : 1) );
			}
		}
	}

	/// <summary>
	/// Each model node's pose relative to its parent at a frame (world units).
	/// </summary>
	// basePose is the pose overlays and replacements go over (default: the default pose).
	// Base animations always leave what they don't set at the default pose.
	public NodePose[] LocalPose( RenderModelAnimation entry, int frame, IReadOnlyList<NodePose> basePose = null )
	{
		var data = entry.Animation.Decode();
		var overlay = entry.Animation.Type == AnimationType.Overlay;
		var poses = basePose is not null && entry.Animation.Type != AnimationType.Base
			? basePose.ToArray()
			: Nodes.Select( n => new NodePose( n.Rotation, n.Translation, 1 ) ).ToArray();
		for ( var n = 0; n < entry.Animation.NodeCount; n++ )
		{
			var node = entry.NodeOfAnimationNode[n];
			if ( node < 0 || node >= poses.Length )
				continue;

			var under = poses[node];
			var r = data.Rotation( n, frame );
			var t = data.Translation( n, frame );
			var s = data.Scale( n, frame );
			if ( overlay )
			{
				poses[node] = new NodePose(
					r is { } dr ? Quaternion.Normalize( under.Rotation * dr ) : under.Rotation,
					t is { } dt ? under.Translation + dt : under.Translation,
					s is { } ds ? under.Scale * ds : under.Scale );
			}
			else
			{
				var translation = t ?? under.Translation;
				if ( t is not null && node == 0 && entry.RootZScale != 1 )
					translation.Z *= entry.RootZScale;
				poses[node] = new NodePose( r ?? under.Rotation, translation, s ?? under.Scale );
			}
		}

		return poses;
	}

	/// <summary>
	/// Each model node's pose in model space at a frame (world units), nodes posed from their parents.
	/// </summary>
	public (Quaternion Rotation, Vector3 Translation)[] ModelPose( RenderModelAnimation entry, int frame ) => ToModelSpace( Nodes, LocalPose( entry, frame ) );

	public static (Quaternion Rotation, Vector3 Translation)[] ToModelSpace( IReadOnlyList<RenderModelNode> nodes, IReadOnlyList<NodePose> local )
	{
		var world = new (Quaternion, Vector3)[local.Count];
		for ( var i = 0; i < world.Length; i++ )
		{
			var parent = nodes[i].Parent;
			var pose = local[i];
			if ( parent < 0 || parent >= i )
			{
				world[i] = (pose.Rotation, pose.Translation);
				continue;
			}

			var (pr, pt) = world[parent];
			world[i] = (Quaternion.Normalize( pr * pose.Rotation ), pt + Vector3.Transform( pose.Translation, pr ));
		}

		return world;
	}
}
