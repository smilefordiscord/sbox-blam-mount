using System;
using System.Collections.Generic;
using System.Linq;
using Blam.Halo1.Tags;
using Blam.Halo3.Tags;

namespace Blam.Halo3;

// Inside the namespace so these win over s&box's global vector types when this compiles into the mount.
using Quaternion = System.Numerics.Quaternion;
using Vector3 = System.Numerics.Vector3;

// One animation a render model plays: the graph animation, the model node each of its nodes drives (-1 for none), whether it comes from an inherited graph, and the root height scale it inherits through.
public sealed record RenderModelAnimation( string Name, GraphAnimation Animation, int[] NodeOfAnimationNode, bool Inherited, float RootZScale );

// The animations a Halo 3 render model plays from its hlmt's graph, as Halo 2's ModelAnimationSet: the graph's own and, for names it lacks, those of the graphs it inherits (the parent graph is the first inheritance entry), each put on the model's nodes.
// Graph nodes find the model's by name; an inherited graph's nodes go through the entry's node map to this graph's nodes first, or by name when it has none (most of MCC's). Components an animation doesn't set keep the model's default pose; overlays are applied over the default pose (base * overlay, as verified live on Halo 2).
public sealed class ModelAnimationSet
{
	public RenderModelTag Model { get; }
	public AnimationGraphTag Graph { get; }
	public IReadOnlyList<RenderModelAnimation> Animations { get; }

	readonly Dictionary<string, RenderModelAnimation> _byName;
	readonly Dictionary<uint, AnimationGraphTag> _graphs;

	ModelAnimationSet( RenderModelTag model, AnimationGraphTag graph, List<RenderModelAnimation> animations, Dictionary<uint, AnimationGraphTag> graphs )
	{
		Model = model;
		Graph = graph;
		Animations = animations;
		_graphs = graphs;
		_byName = animations.ToDictionary( a => a.Name, StringComparer.OrdinalIgnoreCase );
	}

	public RenderModelAnimation Find( string name ) => name is not null && _byName.TryGetValue( name, out var a ) ? a : null;

	// The animation a label of the graph (combat:unarmed:any:idle as mode, class, type, label) points at, or null.
	public RenderModelAnimation FindLabel( string mode, string weaponClass, string weaponType, string label )
	{
		var entry = Graph.Labels.FirstOrDefault( l => !l.Animation.IsNone && Same( l.Mode, mode ) && Same( l.WeaponClass, weaponClass )
			&& Same( l.WeaponType, weaponType ) && Same( l.Label, label ) );
		return entry is null ? null : Resolve( entry.Animation );
	}

	// The model's animation a reference of the graph points at, by the name of the animation it names.
	public RenderModelAnimation Resolve( AnimationReference reference )
	{
		if ( reference.IsNone )
			return null;

		AnimationGraphTag graph = Graph;
		if ( reference.IsInherited )
		{
			if ( reference.Graph >= Graph.Inheritance.Count || Graph.Inheritance[reference.Graph].Graph is not { } tag || !_graphs.TryGetValue( tag.Id, out graph ) )
				return null;
		}

		return reference.Animation < graph.Animations.Count ? Find( graph.Animations[reference.Animation].Name ) : null;
	}

	static bool Same( string a, string b ) => string.Equals( a, b, StringComparison.OrdinalIgnoreCase );

	// The animations of a model tag's graph for its render model, or null when it has no graph.
	public static ModelAnimationSet For( RenderModelTag model, ModelTag hlmt ) => hlmt?.Animation is { } tag ? For( model, new AnimationGraphTag( tag ) ) : null;

	public static ModelAnimationSet For( RenderModelTag model, AnimationGraphTag graph )
	{
		var byName = new Dictionary<string, int>( StringComparer.OrdinalIgnoreCase );
		foreach ( var node in model.Nodes )
			byName.TryAdd( node.Name ?? "", node.Index );

		var nodeOfGraphNode = graph.Nodes.Select( n => byName.TryGetValue( n.Name ?? "", out var i ) ? i : -1 ).ToArray();
		var animations = new List<RenderModelAnimation>();
		var names = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
		var graphs = new Dictionary<uint, AnimationGraphTag>();
		Collect( graph, nodeOfGraphNode, false, 1 );
		return new ModelAnimationSet( model, graph, animations, graphs );

		void Collect( AnimationGraphTag g, int[] nodes, bool inherited, float rootZ )
		{
			if ( !graphs.TryAdd( g.Tag.Id, g ) )
				return;

			foreach ( var animation in g.Animations )
			{
				if ( !animation.HasData || string.IsNullOrEmpty( animation.Name ) || animation.NodeCount > nodes.Length || !names.Add( animation.Name ) )
					continue;

				animations.Add( new RenderModelAnimation( animation.Name, animation, nodes, inherited, rootZ ) );
			}

			foreach ( var entry in g.Inheritance )
			{
				if ( !AnimationGraphTag.IsGraph( entry.Graph ) )
					continue;

				// 324 of MCC's 450 entries have no node map: those find the model's nodes by name too (the 126 maps there are agree with the names, node for node).
				var other = new AnimationGraphTag( entry.Graph );
				var map = new int[other.Nodes.Count];
				for ( var k = 0; k < map.Length; k++ )
				{
					if ( entry.NodeMap.Count == 0 )
					{
						map[k] = byName.TryGetValue( other.Nodes[k].Name ?? "", out var i ) ? i : -1;
						continue;
					}

					var local = k < entry.NodeMap.Count ? entry.NodeMap[k] : -1;
					map[k] = local >= 0 && local < nodes.Length ? nodes[local] : -1;
				}

				Collect( other, map, true, rootZ * (entry.RootZScale > 0 ? entry.RootZScale : 1) );
			}
		}
	}

	// The model's default pose, parent relative.
	public NodePose[] DefaultPose() => Model.Nodes.Select( n => new NodePose( Quaternion.Normalize( n.Rotation ), n.Translation, 1 ) ).ToArray();

	// Each model node's pose relative to its parent at a frame (world units).
	public NodePose[] LocalPose( RenderModelAnimation entry, int frame )
	{
		var data = entry.Animation.Decode();
		var poses = DefaultPose();
		var overlay = entry.Animation.Type == AnimationType.Overlay;
		for ( var n = 0; n < Math.Min( data.NodeCount, entry.NodeOfAnimationNode.Length ); n++ )
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

	// The frame after an animation's last that closes its loop, at the speed of its last step, or null when its last frame is already its first (Zanzibar's wheel: 0 to 360 degrees).
	// Gears whose cycle doesn't close (fans: 100 frames over 0 to 178.2 degrees of a half-turn symmetric blade) need it so they turn on through the wrap.
	public NodePose[] ClosingFrame( RenderModelAnimation entry )
	{
		var frames = entry.Animation.FrameCount;
		if ( frames < 2 )
			return null;

		var first = LocalPose( entry, 0 );
		var previous = LocalPose( entry, frames - 2 );
		var last = LocalPose( entry, frames - 1 );
		var closed = true;
		for ( var i = 0; i < last.Length && closed; i++ )
		{
			var angle = 2 * MathF.Acos( Math.Clamp( MathF.Abs( Quaternion.Dot( first[i].Rotation, last[i].Rotation ) ), 0, 1 ) ) * 180 / MathF.PI;
			closed = angle < 0.5f && Vector3.Distance( first[i].Translation, last[i].Translation ) < 0.001f;
		}

		if ( closed )
			return null;

		var next = new NodePose[last.Length];
		for ( var i = 0; i < next.Length; i++ )
		{
			var step = Quaternion.Normalize( Quaternion.Inverse( previous[i].Rotation ) * last[i].Rotation );
			next[i] = new NodePose( Quaternion.Normalize( last[i].Rotation * step ), last[i].Translation * 2 - previous[i].Translation, last[i].Scale );
		}
		return next;
	}

	// Each model node's pose in model space at a frame (world units).
	public (Quaternion Rotation, Vector3 Translation)[] ModelPose( RenderModelAnimation entry, int frame ) => ToModelSpace( Model, LocalPose( entry, frame ) );

	public static (Quaternion Rotation, Vector3 Translation)[] ToModelSpace( RenderModelTag model, IReadOnlyList<NodePose> local )
	{
		var world = new (Quaternion, Vector3)[local.Count];
		for ( var i = 0; i < world.Length; i++ )
		{
			var parent = model.Nodes[i].Parent;
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
