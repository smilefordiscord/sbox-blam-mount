using System;
using System.Collections.Generic;
using System.Linq;
using Blam.Halo1.Tags;
using Blam.Halo2.TagData;
using Blam.Halo2.Tags;

namespace Blam.Halo2;

// Inside the namespace so these win over s&box's global vector types when this compiles into the mount.
using Quaternion = System.Numerics.Quaternion;
using Vector3 = System.Numerics.Vector3;

// What Halo 2 draws for a weapon in the multiplayer player's first person view: the player's hands and the weapon's first person model, posed together by a first person graph.
// The hands are the multiplayer Spartan's player representation in the globals.
// A weapon's first person block holds a model and graph per character, the chief's first and the Arbiter's second.
// The one used is the first whose graph has every node of the hands and of the model, as a graph drives the models' nodes by name.
public sealed class FirstPersonWeapon
{
	static readonly TagClass WeaponClass = new( "weap" );
	static readonly TagClass GlobalsClass = new( "matg" );

	// The globals' player representations are the chief, the Arbiter, the multiplayer Spartan (masterchief_mp) and the multiplayer elite.
	// Campaign maps keep only their own character's, so their weapons have no multiplayer first person view.
	const int SpartanRepresentation = 2;

	public TagEntry Weapon { get; }

	// The biped whose hands they are.
	public TagEntry Unit { get; }

	// The hands, weapon model (render models) and graph.
	public TagEntry Hands { get; }
	public TagEntry Model { get; }
	public TagEntry Graph { get; }

	// The element of the weapon's first person block used (0 the chief's).
	public int Entry { get; }

	FirstPersonWeapon( TagEntry weapon, TagEntry unit, TagEntry hands, TagEntry model, TagEntry graph, int entry )
	{
		Weapon = weapon;
		Unit = unit;
		Hands = hands;
		Model = model;
		Graph = graph;
		Entry = entry;
	}

	public static bool IsWeapon( TagEntry tag ) => tag is not null && tag.Address != 0 && tag.Class == WeaponClass;

	// The multiplayer player's first person setup for a weapon, or null if the weapon has none that fits (turrets, vehicle guns).
	public static FirstPersonWeapon Find( TagEntry weapon )
	{
		if ( !IsWeapon( weapon ) || MultiplayerHands( weapon.Cache ) is not { } player )
			return null;

		var data = weapon.Cache.TagData;
		var block = TagLayouts.Weapon.Field( "first person" );
		var element = block.Struct;
		var entry = 0;
		foreach ( var address in data.Elements( weapon.Address + (uint)block.Offset, element.Size ) )
		{
			var model = weapon.Cache.GetTagReference( address + (uint)element.Field( "first person model" ).Offset );
			var graph = weapon.Cache.GetTagReference( address + (uint)element.Field( "first person animations" ).Offset );
			if ( model is { Address: not 0 } && model.Class == RenderModelTag.Class && graph is { Address: not 0 } && graph.Class == AnimationGraphTag.Class )
			{
				var names = new AnimationGraphTag( graph ).Nodes.Select( n => n.Name ).ToHashSet( StringComparer.Ordinal );
				if ( new[] { player.Hands, model }.All( m => new RenderModelTag( m ).Nodes.All( n => names.Contains( n.Name ) ) ) )
					return new FirstPersonWeapon( weapon, player.Unit, player.Hands, model, graph, entry );
			}

			entry++;
		}

		return null;
	}

	// The multiplayer Spartan's unit and first person hands from a map's globals, or null without them (campaign maps).
	public static (TagEntry Unit, TagEntry Hands)? MultiplayerHands( CacheFile cache )
	{
		var globals = cache.Tags.FirstOrDefault( t => t.Class == GlobalsClass && t.Address != 0 );
		if ( globals is null )
			return null;

		var representation = TagLayouts.Globals.Field( "player representation" );
		var element = representation.Struct;
		var address = globals.Cache.TagData.Elements( globals.Address + (uint)representation.Offset, element.Size ).ElementAtOrDefault( SpartanRepresentation );
		if ( address == 0 )
			return null;

		var unit = globals.Cache.GetTagReference( address + (uint)element.Field( "third person unit" ).Offset );
		var hands = globals.Cache.GetTagReference( address + (uint)element.Field( "first person hands" ).Offset );
		return unit is { Address: not 0 } && hands is { Address: not 0 } && hands.Class == RenderModelTag.Class ? (unit, hands) : null;
	}
}

// The skeleton of a first person weapon with its hands: one bone per node of the first person graph, parents first, with both models' nodes on them.
// The graph's root is at the camera, x forward and z up: its "camera_control" node, which neither model has, rests at the origin.
public sealed class FirstPersonSkeleton
{
	public FirstPersonWeapon Weapon { get; }
	public AnimationGraphTag Graph { get; }
	public RenderModelTag HandsModel { get; }
	public RenderModelTag WeaponModel { get; }

	// The bones as nodes: the graph node's name, the parent bone, and the default pose relative to it.
	// A bone takes its default from the hands' node of the same name, else the weapon model's, else the identity.
	public IReadOnlyList<RenderModelNode> Nodes { get; }

	// Each bone's bind pose in model space: the default pose of the model node its vertices were modelled around.
	public IReadOnlyList<(Quaternion Rotation, Vector3 Translation)> Bind { get; }

	// The bone of each node of the hands and of the weapon model.
	public IReadOnlyList<int> HandsBones { get; }
	public IReadOnlyList<int> WeaponBones { get; }

	FirstPersonSkeleton( FirstPersonWeapon weapon, AnimationGraphTag graph, RenderModelTag hands, RenderModelTag model, List<RenderModelNode> nodes,
		List<(Quaternion, Vector3)> bind, int[] handsBones, int[] weaponBones )
	{
		Weapon = weapon;
		Graph = graph;
		HandsModel = hands;
		WeaponModel = model;
		Nodes = nodes;
		Bind = bind;
		HandsBones = handsBones;
		WeaponBones = weaponBones;
	}

	// The graph's animations on the bones: its own and, for the names it lacks, its inherited graphs'.
	public ModelAnimationSet Animations() => ModelAnimationSet.For( Nodes, Graph );

	// The pose an overlay or replacement is baked over for the view model: the first frame of the idle of its weapon class.
	// Labels give the class: "any:any:any:moving" goes over "any:any:any:idle", "any:dual:any:moving" over "any:dual:any:idle".
	// Null (the default pose) when that class has no idle.
	public NodePose[] BasePose( ModelAnimationSet set, RenderModelAnimation entry )
	{
		var label = Graph.Labels.FirstOrDefault( l => set.Resolve( l.Animation ) == entry );
		var idle = label is null ? null : set.FindLabel( label.Mode, label.WeaponClass, label.WeaponType, "idle" );
		return idle is null || idle.Animation.Type != AnimationType.Base ? null : set.LocalPose( idle, 0 );
	}

	// The skeleton, or null if the graph's nodes don't form a tree.
	public static FirstPersonSkeleton Create( FirstPersonWeapon weapon )
	{
		var graph = new AnimationGraphTag( weapon.Graph );
		var hands = new RenderModelTag( weapon.Hands );
		var model = new RenderModelTag( weapon.Model );
		var count = graph.Nodes.Count;

		// Parents first, otherwise in graph order.
		var order = new List<int>();
		var placed = new bool[count];
		while ( order.Count < count )
		{
			var before = order.Count;
			for ( var n = 0; n < count; n++ )
			{
				var parent = graph.Nodes[n].Parent;
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

		var byName = new Dictionary<string, int>( StringComparer.Ordinal );
		foreach ( var n in graph.Nodes )
			byName.TryAdd( n.Name ?? "", boneOfNode[n.Index] );

		var handsBones = hands.Nodes.Select( n => byName[n.Name] ).ToArray();
		var weaponBones = model.Nodes.Select( n => byName[n.Name] ).ToArray();
		var handsPose = hands.DefaultPose();
		var weaponPose = model.DefaultPose();

		var nodes = new List<RenderModelNode>();
		var bind = new List<(Quaternion, Vector3)>();
		for ( var b = 0; b < count; b++ )
		{
			var node = graph.Nodes[order[b]];
			var parent = node.Parent < 0 ? -1 : boneOfNode[node.Parent];
			var h = Array.IndexOf( handsBones, b );
			var w = Array.IndexOf( weaponBones, b );
			var source = h >= 0 ? hands.Nodes[h] : w >= 0 ? model.Nodes[w] : null;
			nodes.Add( new RenderModelNode( b, node.Name, parent, source?.Translation ?? Vector3.Zero, source?.Rotation ?? Quaternion.Identity ) );
			bind.Add( h >= 0 ? handsPose[h] : w >= 0 ? weaponPose[w] : (Quaternion.Identity, Vector3.Zero) );
		}

		return new FirstPersonSkeleton( weapon, graph, hands, model, nodes, bind, handsBones, weaponBones );
	}
}
