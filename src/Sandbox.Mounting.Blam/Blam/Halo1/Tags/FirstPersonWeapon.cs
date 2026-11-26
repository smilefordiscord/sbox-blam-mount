using System;
using System.Collections.Generic;
using System.Linq;

namespace Blam.Halo1.Tags;

// Inside the namespace so these win over s&box's global types when this compiles into the mount.
using Quaternion = System.Numerics.Quaternion;
using Vector3 = System.Numerics.Vector3;

/// <summary>
/// The first person animation slots of a weapon's first person graph, in slot order (the index
/// into <see cref="ModelAnimationsTag.FirstPersonAnimations"/>).
/// </summary>
public static class FirstPersonAnimations
{
	/// <summary>
	/// Slot names. Halo's tools fill each slot with the animation named "first-person &lt;slot&gt;",
	/// so the names come from the graphs themselves: in MCC's maps every slot but 6 and 19 holds
	/// such an animation somewhere, with the name matching its index (<c>BlamInspect &lt;map&gt;
	/// firstperson</c>). "moving", "overlays", "overcharged-jitter" and "ammunition" are overlays
	/// played over the others (walking bob, turning lag, charge shake, the needler's needle count);
	/// the rest are full poses.
	/// </summary>
	public static readonly IReadOnlyList<string> Names =
	[
		"idle", "posing", "fire-1", "moving", "overlays", "light-off", "light-on", "reload-empty",
		"reload-full", "overheated", "ready", "put-away", "overcharged", "melee", "fire-2",
		"overcharged-jitter", "throw-grenade", "ammunition", "misfire-1", "misfire-2",
		"throw-overheated", "overheating", "overheating-again", "enter", "exit-empty", "exit-full",
		"o-h-exit", "o-h-s-enter",
	];

	public const int Idle = 0;

	/// <summary>
	/// The prefix Halo's first person animations are named with ("first-person idle").
	/// </summary>
	public const string Prefix = "first-person ";

	/// <summary>
	/// Which slots loop: the idle and the overlays that run all the time. The rest are one-off
	/// actions Halo plays once before going back to the idle.
	/// </summary>
	static readonly HashSet<string> LoopingSlots = ["idle", "moving", "overcharged-jitter"];

	/// <summary>
	/// A first person graph's animations under the names a view model gives them: the slots'
	/// animations in slot order (the idle first) under their own names, then every slot whose
	/// animation is named for another (the assault rifle reloads with "first-person reload-full"
	/// whether empty or not) again as "first-person &lt;slot&gt;", then the animations no slot uses.
	/// Names are unique (a repeat gets the animation's index appended).
	/// </summary>
	public static List<(string Name, ModelAnimation Animation, bool Looping)> Named( ModelAnimationsTag graph )
	{
		var result = new List<(string Name, ModelAnimation Animation, bool Looping)>();
		var used = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
		var added = new HashSet<int>();
		var slots = graph.FirstPersonAnimations;
		var count = Math.Min( slots.Count, Names.Count );

		ModelAnimation Slot( int s ) => slots[s] >= 0 && slots[s] < graph.Animations.Count ? graph.Animations[slots[s]] : null;

		void Add( string name, ModelAnimation animation, bool looping )
		{
			var unique = used.Add( name ) ? name : $"{name} {animation.Index}";
			used.Add( unique );
			result.Add( (unique, animation, looping) );
		}

		for ( var s = 0; s < count; s++ )
		{
			if ( Slot( s ) is { } animation && added.Add( animation.Index ) )
				Add( animation.Name, animation, LoopingSlots.Contains( Names[s] ) );
		}

		for ( var s = 0; s < count; s++ )
		{
			if ( Slot( s ) is { } animation && !used.Contains( Prefix + Names[s] ) )
				Add( Prefix + Names[s], animation, LoopingSlots.Contains( Names[s] ) );
		}

		foreach ( var animation in graph.Animations )
		{
			if ( added.Add( animation.Index ) )
				Add( animation.Name, animation, false );
		}

		return result;
	}
}

/// <summary>
/// What Halo draws for a weapon in first person: the weapon's first person model and the player's
/// hands, posed together by the weapon's first person animation graph.
/// </summary>
/// <remarks>
/// The weapon tag references its first person model at 0x45C and first person animations at 0x46C.
/// The hands are the globals tag's (matg) first person interface block (0x17C, 192 bytes: hands
/// model at 0x0); Halo only uses its first element, and every MCC map has one (the cyborg's hands).
///
/// The graph's node list is the skeleton both models hang on: each model finds every one of its
/// nodes in the graph by exact name (if one is missing, Halo doesn't draw the weapon), and the
/// graph's nodes are posed from the graph's root, which Halo puts at the camera (x forward, z up).
/// </remarks>
public sealed class FirstPersonWeapon
{
	public TagEntry Weapon { get; }
	public TagEntry Model { get; }
	public TagEntry Animations { get; }
	public TagEntry Hands { get; }

	FirstPersonWeapon( TagEntry weapon, TagEntry model, TagEntry animations, TagEntry hands )
	{
		Weapon = weapon;
		Model = model;
		Animations = animations;
		Hands = hands;
	}

	public static readonly TagClass WeaponClass = new( "weap" );
	static readonly TagClass GlobalsClass = new( "matg" );

	/// <summary>
	/// The first person setup of a weapon tag, or null if it has no first person model, graph or
	/// hands (the gravity rifle, turret guns).
	/// </summary>
	public static FirstPersonWeapon Find( CacheFile cache, TagEntry weapon )
	{
		if ( weapon is null || weapon.Class != WeaponClass )
			return null;

		var model = cache.GetTagReference( cache.TagData, weapon.Address + 0x45C );
		var animations = cache.GetTagReference( cache.TagData, weapon.Address + 0x46C );
		var hands = FindHands( cache );
		if ( model?.Class != TagClasses.GbxModel || animations?.Class != TagClasses.ModelAnimations || hands?.Class != TagClasses.GbxModel )
			return null;

		return new FirstPersonWeapon( weapon, model, animations, hands );
	}

	/// <summary>
	/// The first person hands model from the map's globals tag.
	/// </summary>
	public static TagEntry FindHands( CacheFile cache )
	{
		var globals = cache.Tags.FirstOrDefault( t => t.Class == GlobalsClass );
		if ( globals is null )
			return null;

		foreach ( var entry in cache.TagData.Block( globals.Address + 0x17C, 0xC0 ) )
			return cache.GetTagReference( cache.TagData, entry );

		return null;
	}

	/// <summary>
	/// The graph node each of a model's nodes follows (by exact name, as Halo matches them), -1 for
	/// nodes the graph doesn't have.
	/// </summary>
	public static int[] MapNodes( GbxModelTag model, ModelAnimationsTag graph )
	{
		var byName = new Dictionary<string, int>( StringComparer.Ordinal );
		for ( var i = 0; i < graph.NodeNames.Count; i++ )
			byName.TryAdd( graph.NodeNames[i], i );

		return model.Nodes.Select( n => byName.TryGetValue( n.Name, out var i ) ? i : -1 ).ToArray();
	}
}

/// <summary>
/// The skeleton of a first person weapon with its hands: one bone per node of the weapon's first
/// person graph, parents first, with both models' nodes mapped onto them.
/// </summary>
public sealed class FirstPersonSkeleton
{
	public FirstPersonWeapon Weapon { get; }
	public GbxModelTag WeaponModel { get; }
	public GbxModelTag HandsModel { get; }
	public ModelAnimationsTag Graph { get; }

	/// <summary>The graph node of each bone.</summary>
	public IReadOnlyList<int> GraphNodes { get; }

	/// <summary>Each bone's parent bone, -1 for the root.</summary>
	public IReadOnlyList<int> Parents { get; }

	/// <summary>The bone of each graph node (the inverse of <see cref="GraphNodes"/>).</summary>
	public IReadOnlyList<int> BoneOfGraphNode { get; }

	/// <summary>The bone of each of the weapon model's nodes.</summary>
	public IReadOnlyList<int> WeaponBones { get; }

	/// <summary>The bone of each of the hands model's nodes.</summary>
	public IReadOnlyList<int> HandsBones { get; }

	/// <summary>
	/// Where each bone's default (bind) pose comes from: the model node the vertices skinned to
	/// it were modelled around. The hands' node where both models have one (the multiplayer fuel rod
	/// gun's model, plasma_cannon, repeats the hands' nodes), -1 for nodes neither model has.
	/// </summary>
	public IReadOnlyList<(bool Hands, int Node)> BindNodes { get; }

	FirstPersonSkeleton( FirstPersonWeapon weapon, GbxModelTag weaponModel, GbxModelTag handsModel, ModelAnimationsTag graph,
		int[] graphNodes, int[] parents, int[] boneOfNode, int[] weaponBones, int[] handsBones, (bool, int)[] bindNodes )
	{
		Weapon = weapon;
		WeaponModel = weaponModel;
		HandsModel = handsModel;
		Graph = graph;
		GraphNodes = graphNodes;
		Parents = parents;
		BoneOfGraphNode = boneOfNode;
		WeaponBones = weaponBones;
		HandsBones = handsBones;
		BindNodes = bindNodes;
	}

	/// <summary>
	/// Builds the skeleton, or returns null if either model has a node the graph doesn't (Halo
	/// doesn't draw such a weapon), the graph has more than 64 nodes, or its links aren't a tree.
	/// </summary>
	public static FirstPersonSkeleton Create( CacheFile cache, FirstPersonWeapon weapon )
	{
		var graph = new ModelAnimationsTag( cache, weapon.Animations );
		var weaponModel = new GbxModelTag( cache, weapon.Model );
		var handsModel = new GbxModelTag( cache, weapon.Hands );
		var count = graph.NodeNames.Count;
		if ( count < 1 || count > 64 )
			return null;

		var weaponNodes = FirstPersonWeapon.MapNodes( weaponModel, graph );
		var handsNodes = FirstPersonWeapon.MapNodes( handsModel, graph );
		if ( weaponNodes.Contains( -1 ) || handsNodes.Contains( -1 ) )
			return null;

		// Parents first, otherwise in graph order (which already is parents first in every map).
		var order = new List<int>();
		var placed = new bool[count];
		while ( order.Count < count )
		{
			var before = order.Count;
			for ( var n = 0; n < count; n++ )
			{
				var parent = graph.NodeParents[n];
				if ( placed[n] || (parent >= 0 && (parent >= count || !placed[parent])) )
					continue;

				placed[n] = true;
				order.Add( n );
			}

			if ( order.Count == before )
				return null;
		}

		var boneOfNode = new int[count];
		for ( var b = 0; b < count; b++ )
			boneOfNode[order[b]] = b;

		var parents = order.Select( n => graph.NodeParents[n] < 0 ? -1 : boneOfNode[graph.NodeParents[n]] ).ToArray();
		var bindNodes = order.Select( n =>
		{
			var hands = Array.IndexOf( handsNodes, n );
			return hands >= 0 ? (true, hands) : (false, Array.IndexOf( weaponNodes, n ));
		} ).ToArray();

		return new FirstPersonSkeleton( weapon, weaponModel, handsModel, graph, order.ToArray(), parents, boneOfNode,
			weaponNodes.Select( n => boneOfNode[n] ).ToArray(), handsNodes.Select( n => boneOfNode[n] ).ToArray(), bindNodes );
	}

	/// <summary>
	/// A model's nodes in its default pose, in model space (world units).
	/// </summary>
	public static (Vector3 Position, Quaternion Rotation)[] DefaultPose( GbxModelTag model )
	{
		var world = new (Vector3 Position, Quaternion Rotation)[model.Nodes.Count];
		for ( var i = 0; i < world.Length; i++ )
		{
			var node = model.Nodes[i];
			var parent = node.ParentIndex >= 0 && node.ParentIndex < i ? world[node.ParentIndex] : (Position: Vector3.Zero, Rotation: Quaternion.Identity);
			world[i] = (parent.Position + Vector3.Transform( node.Translation, parent.Rotation ), Quaternion.Normalize( parent.Rotation * node.Rotation ));
		}

		return world;
	}
}
