using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Blam.Halo1.Tags;

namespace Blam.Halo1.TagData;

/// <summary>
/// The skeleton an animation graph's frames are written for: the bones of the model the mount
/// plays the graph on, named and ordered as that model's .vmdl names them, and the bone each graph
/// node drives.
/// </summary>
/// <remarks>
/// A weapon's first person graph drives the combined hands and weapon skeleton of
/// <c>&lt;weapon tag&gt;_fp.vmdl</c> (see <see cref="FirstPersonSkeleton"/>). Any other graph
/// drives the model of the first object using it (bipeds also as <c>&lt;model&gt;_aim.vmdl</c>,
/// with the same bones), or, for graphs no object uses (cutscene clips), the first model whose
/// nodes are exactly the graph's. Graph nodes find their bones by name (ignoring case) or, when the
/// graph lists no nodes (vehicles, devices), by index, as the mount's models do. Without a model
/// the bones are the graph's own nodes.
/// </remarks>
public sealed class AnimationSkeleton
{
	public ModelAnimationsTag Graph { get; }

	/// <summary>The model the bones are those of (<c>mount://haloce/...vmdl</c>), or null if none
	/// has a skeleton for the graph.</summary>
	public string Model { get; }

	/// <summary>Whether this is a weapon's first person graph, on its <c>_fp.vmdl</c>.</summary>
	public bool FirstPerson => FirstPersonSkeleton is not null;

	/// <summary>Bone names, in the model's bone order.</summary>
	public IReadOnlyList<string> BoneNames { get; }

	/// <summary>Each bone's parent bone, -1 for roots.</summary>
	public IReadOnlyList<int> BoneParents { get; }

	/// <summary>The bone each graph node drives (by node index), -1 for nodes the model doesn't have.</summary>
	public IReadOnlyList<int> BoneOfNode { get; }

	/// <summary>The model the bones are the nodes of (null for first person graphs and graphs without a model).</summary>
	public GbxModelTag ModelTag { get; }

	/// <summary>The first person skeleton, for first person graphs.</summary>
	public FirstPersonSkeleton FirstPersonSkeleton { get; }

	AnimationSkeleton( ModelAnimationsTag graph, string model, string[] boneNames, int[] boneParents, int[] boneOfNode, GbxModelTag modelTag, FirstPersonSkeleton firstPerson )
	{
		Graph = graph;
		Model = model;
		BoneNames = boneNames;
		BoneParents = boneParents;
		BoneOfNode = boneOfNode;
		ModelTag = modelTag;
		FirstPersonSkeleton = firstPerson;
	}

	/// <summary>
	/// Node names, made unique in case a model repeats one (bones are linked by name): how the
	/// mount names the bones of its models.
	/// </summary>
	public static string[] UniqueNames( IEnumerable<string> nodeNames )
	{
		var used = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
		var names = nodeNames.ToArray();
		for ( var i = 0; i < names.Length; i++ )
		{
			var name = string.IsNullOrWhiteSpace( names[i] ) ? $"node {i}" : names[i];
			names[i] = used.Add( name ) ? name : $"{name} {i}";
			used.Add( names[i] );
		}

		return names;
	}

	/// <summary>
	/// Finds the model a graph plays on in a map (see the remarks).
	/// </summary>
	public static AnimationSkeleton Find( CacheFile cache, ModelAnimationsTag graph )
	{
		var id = graph.Tag.Id;

		// A weapon's first person graph: the hands and the weapon on the graph's nodes.
		foreach ( var tag in cache.Tags )
		{
			if ( tag.Class != FirstPersonWeapon.WeaponClass || string.IsNullOrEmpty( tag.Path ) || cache.GetTagReference( cache.TagData, tag.Address + 0x46C )?.Id != id )
				continue;

			var weapon = FirstPersonWeapon.Find( cache, tag );
			var skeleton = weapon is null ? null : FirstPersonSkeleton.Create( cache, weapon );
			if ( skeleton is null )
				continue;

			// As FirstPersonModel names the bones.
			var names = UniqueNames( skeleton.GraphNodes.Select( n => skeleton.Graph.NodeNames[n] ) );
			return new AnimationSkeleton( graph, $"{TagExport.MountRoot}{tag.Path.Replace( '\\', '/' )}_fp.vmdl", names,
				skeleton.Parents.ToArray(), skeleton.BoneOfGraphNode.ToArray(), null, skeleton );
		}

		var modelTag = OwnerModel( cache, graph );
		if ( modelTag is not null )
		{
			// As GbxSkeleton names and maps the bones.
			var model = new GbxModelTag( cache, modelTag );
			var names = UniqueNames( model.Nodes.Select( n => n.Name ) );
			int[] boneOfNode;
			if ( graph.NodeNames.Count == 0 )
			{
				boneOfNode = Enumerable.Range( 0, model.Nodes.Count ).ToArray();
			}
			else
			{
				var byName = new Dictionary<string, int>( StringComparer.OrdinalIgnoreCase );
				for ( var i = 0; i < model.Nodes.Count; i++ )
					byName.TryAdd( model.Nodes[i].Name, i );
				boneOfNode = graph.NodeNames.Select( n => byName.TryGetValue( n, out var i ) ? i : -1 ).ToArray();
			}

			return new AnimationSkeleton( graph, HasSkeleton( cache, model ) ? $"{TagExport.MountRoot}{modelTag.Path.Replace( '\\', '/' )}.vmdl" : null,
				names, model.Nodes.Select( n => n.ParentIndex ).ToArray(), boneOfNode, model, null );
		}

		// No model: the graph's own nodes.
		var count = graph.NodeNames.Count > 0 ? graph.NodeNames.Count : graph.Animations.Select( a => a.NodeCount ).DefaultIfEmpty().Max();
		return new AnimationSkeleton( graph, null,
			UniqueNames( Enumerable.Range( 0, count ).Select( n => n < graph.NodeNames.Count ? graph.NodeNames[n] : null ) ),
			Enumerable.Range( 0, count ).Select( n => n < graph.NodeParents.Count && graph.NodeParents[n] < n ? graph.NodeParents[n] : -1 ).ToArray(),
			Enumerable.Range( 0, count ).ToArray(), null, null );
	}

	/// <summary>
	/// The model of the first object using the graph, else the first model with exactly the graph's
	/// nodes if no object uses the graph (as GbxSkeleton.FindAnimations gives graphs to models).
	/// </summary>
	static TagEntry OwnerModel( CacheFile cache, ModelAnimationsTag graph )
	{
		var used = false;
		foreach ( var tag in cache.Tags )
		{
			if ( !tag.Is( TagClasses.Object ) || ObjectTags.GetObjectAnimations( cache, tag )?.Id != graph.Tag.Id )
				continue;

			used = true;
			if ( ObjectTags.GetObjectModel( cache, tag ) is { } model && model.Class == TagClasses.GbxModel && !string.IsNullOrEmpty( model.Path ) )
				return model;
		}

		if ( used || graph.NodeNames.Count < 2 )
			return null;

		var names = graph.NodeNames.ToHashSet( StringComparer.OrdinalIgnoreCase );
		foreach ( var tag in cache.Tags )
		{
			if ( tag.Class != TagClasses.GbxModel || string.IsNullOrEmpty( tag.Path ) )
				continue;

			var nodes = new GbxModelTag( cache, tag ).Nodes;
			if ( nodes.Count == graph.NodeNames.Count && nodes.All( n => names.Contains( n.Name ) ) )
				return tag;
		}

		return null;
	}

	/// <summary>
	/// Whether the mount gives the model a skeleton: 2 to 255 nodes, parents first, or one node
	/// that an animation of an object using the model moves (c10's lift, a platform of one node
	/// whose position animation lowers it). Other single node models (most scenery) stay static
	/// meshes: nothing can move relative to the object, and static meshes are cheaper to draw.
	/// </summary>
	public static bool HasSkeleton( CacheFile cache, GbxModelTag model )
	{
		if ( model.Nodes.Count is < 1 or > 255 )
			return false;

		// Bones have to come after their parents. Halo's tools always sort them that way.
		for ( var i = 0; i < model.Nodes.Count; i++ )
		{
			if ( model.Nodes[i].ParentIndex >= i )
				return false;
		}

		return model.Nodes.Count > 1 || AnimatesSingleNode( cache, model );
	}

	static readonly System.Collections.Concurrent.ConcurrentDictionary<(string Map, uint Model), bool> _singleNodeAnimated = new();

	/// <summary>
	/// Whether a graph of an object using the model animates its one node over more than one frame.
	/// </summary>
	static bool AnimatesSingleNode( CacheFile cache, GbxModelTag model )
	{
		return _singleNodeAnimated.GetOrAdd( (cache.Name, model.Tag.Id), _ =>
		{
			var graphs = new HashSet<uint>();
			foreach ( var tag in cache.Tags )
			{
				if ( !tag.Is( TagClasses.Object ) || ObjectTags.GetObjectModel( cache, tag )?.Id != model.Tag.Id )
					continue;

				var graph = ObjectTags.GetObjectAnimations( cache, tag );
				if ( graph is null || graph.Class != TagClasses.ModelAnimations || !graphs.Add( graph.Id ) )
					continue;

				if ( new ModelAnimationsTag( cache, graph ).Animations.Any( a => a.NodeCount == 1 && a.FrameCount > 1 && a.CanDecode ) )
					return true;
			}

			return false;
		} );
	}
}

/// <summary>
/// Animation graphs (<c>antr</c>) as JSON, with every frame decoded, for games that pose bones
/// themselves (first person sway, aiming, overlays on their own base pose):
/// <list type="bullet">
/// <item><c>mount://haloce/anims/&lt;graph tag path&gt;/&lt;animation name&gt;.json</c>: one animation's
/// frames (see <see cref="Animation"/>), e.g.
/// <c>anims/weapons/assault rifle/fp/fp/first-person overlays.json</c>.</item>
/// <item><c>mount://haloce/anims/&lt;graph tag path&gt;.json</c>: the graph's bones, sounds and
/// animations without their frames (see <see cref="Graph"/>).</item>
/// </list>
/// </summary>
/// <remarks>
/// Conventions, for every document: bones are the model's (<see cref="AnimationSkeleton"/>),
/// "Parent" an index into the same "Nodes" list (-1 for a root). Transforms are local to the parent
/// bone, the way s&amp;box's <c>Transform.ToWorld</c> composes them (model rotation = parent rotation
/// * rotation, model position = parent position + parent rotation * position), in s&amp;box's axes,
/// which are Halo's (x forward, y left, z up). "Position" is <c>[x, y, z]</c> in s&amp;box units
/// (Halo world units * <see cref="UnitsPerWorldUnit"/>), "Rotation" an s&amp;box <c>Rotation</c>
/// <c>[x, y, z, w]</c> (Halo stores the conjugate), "Scale" uniform: exactly what the mount's
/// .vmdl animations hold (GbxSkeleton converts the same decoded frames the same way). Numbers are
/// rounded to 1e-5.
///
/// Base and replacement animations list every bone they drive in every frame, with the full local
/// pose (components the animation doesn't change are its constant default pose).
///
/// Overlays are written as stored, not baked onto anything: each frame lists only the bones the
/// overlay changes, and only the changed components (the rest are the identity: no rotation, zero
/// position, scale 1). Halo applies them over the pose underneath: rotation post-multiplied
/// (local rotation = base rotation * rotation), position added in the parent's space (local
/// position = base position + position), scale multiplied (at a blend weight w, base scale *
/// (scale * w + 1 - w)). Nothing is subtracted: every frame, the first included, is a change from
/// the pose underneath (an aiming overlay's first frame is its grid's corner, "first-person
/// overlays" frame 0 the forward lean), not from the overlay's first frame.
/// The .vmdl's sequence for an overlay is its frames applied this way to the idle's first frame
/// (first person) or to the default pose.
/// </remarks>
public static class AnimationExport
{
	/// <summary>s&amp;box units per Halo world unit (the mount's world scale).</summary>
	public const float UnitsPerWorldUnit = 64f / 0.62f;

	const string Convention = "Transforms are local to the parent node (Parent indexes Nodes, -1 = root), composed like s&box Transform.ToWorld: "
		+ "model rotation = parent rotation * Rotation, model position = parent position + parent rotation * Position. s&box axes (= Halo's: x forward, y left, z up). "
		+ "Position [x,y,z] in s&box units (Halo world units * UnitsPerWorldUnit), Rotation [x,y,z,w] an s&box Rotation (Halo stores the conjugate), Scale uniform; "
		+ "the same values the mount's .vmdl animations use. Rounded to 1e-5.";

	const string PoseForm = "pose: every frame lists every node the animation drives with its full local transform.";

	const string OverlayForm = "raw overlay deltas, as stored, not baked: each frame lists only the nodes the overlay changes and only the changed components "
		+ "(missing = identity: Rotation [0,0,0,1], Position [0,0,0], Scale 1). Apply over the pose underneath: local rotation = base rotation * Rotation "
		+ "(post-multiplied), local position = base position + Position (parent space), scale = base scale * (Scale * w + 1 - w) at weight w. "
		+ "Nothing is subtracted: every frame, frame 0 too, is a change from the pose underneath (frame 0 is not the identity in general). The .vmdl bakes these onto the idle's first frame (first person) or the default pose.";

	static readonly JsonWriterOptions Compact = new() { Encoder = TagJsonWriter.Indented.Encoder };

	/// <summary>
	/// Where a graph's index document goes: <c>anims/weapons/pistol/fp/fp.json</c>.
	/// </summary>
	public static string GraphResource( TagEntry graph ) => $"anims/{graph.Path.Replace( '\\', '/' )}.json";

	/// <summary>
	/// Where each of a graph's animations goes: <c>anims/&lt;graph tag path&gt;/&lt;animation name&gt;.json</c>.
	/// Names keep their spaces; characters that can't be in a path (<c>/\:*?"&lt;&gt;|</c>, control
	/// characters) become <c>_</c>, and a name a graph repeats (ignoring case) gets the animation's
	/// index appended after a space for the later ones.
	/// </summary>
	public static string[] AnimationResources( ModelAnimationsTag graph )
	{
		var folder = graph.Tag.Path.Replace( '\\', '/' );
		var used = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
		var result = new string[graph.Animations.Count];
		foreach ( var animation in graph.Animations )
		{
			var name = new string( animation.Name.Select( c => c < 0x20 || "/\\:*?\"<>|".Contains( c ) ? '_' : c ).ToArray() ).Trim();
			if ( name.Length == 0 )
				name = $"animation {animation.Index}";
			if ( !used.Add( name ) )
				used.Add( name = $"{name} {animation.Index}" );

			result[animation.Index] = $"anims/{folder}/{name}.json";
		}

		return result;
	}

	/// <summary>
	/// One animation with its frames:
	/// <code>
	/// { "Name": "first-person moving", "Type": "overlay", "FrameCount": 20,
	///   "Nodes": [ { "Name": "frame bone24", "Parent": -1 }, ... ],
	///   "Frames": [ { "Nodes": [ { "Node": 3, "Rotation": [x, y, z, w], "Position": [x, y, z], "Scale": 1 }, ... ] }, ... ],
	///   ...extras }
	/// </code>
	/// "Type" is base, overlay or replacement; "Nodes" are the bones of <see cref="AnimationSkeleton.Model"/>
	/// and "Node" an index into them (see the class remarks for the conventions and the overlay
	/// form). Extras: "Index" (in the graph), "Graph", "Model", "FrameRate" (30, 25 for PAL
	/// animations), "LoopFrame", "Compressed", "WorldRelative" (the root bone's transform is its
	/// world transform, in s&amp;box world units: Halo ignores the object's position and facing while
	/// it plays), "Form", "Convention", "UnitsPerWorldUnit",
	/// "Animated" (the bones whose "Rotation", "Position" and "Scale" change) and, for animations
	/// with root motion, "FrameInfoType" and "FrameInfo" (per frame <c>[dx, dy, dz, dyaw]</c>: the
	/// object's movement over the frame in its own axes, s&amp;box units, and its turn, radians,
	/// positive left).
	/// </summary>
	/// <exception cref="InvalidOperationException">The animation can't be decoded.</exception>
	public static byte[] Animation( AnimationSkeleton skeleton, int index, float scale = UnitsPerWorldUnit )
	{
		var graph = skeleton.Graph;
		var animation = graph.Animations[index];
		var poses = animation.ReadFrames();
		var overlay = animation.Type == AnimationType.Overlay;
		var nodes = animation.NodeCount;

		// The bones the animation drives, in bone order.
		var driven = Enumerable.Range( 0, Math.Min( nodes, skeleton.BoneOfNode.Count ) )
			.Where( n => skeleton.BoneOfNode[n] >= 0 && (!overlay || animation.AnimatesRotation( n ) || animation.AnimatesTranslation( n ) || animation.AnimatesScale( n )) )
			.OrderBy( n => skeleton.BoneOfNode[n] )
			.ToArray();

		return Write( json =>
		{
			json.WriteStartObject();
			json.WriteString( "Name", animation.Name );
			json.WriteString( "Type", TypeName( animation.Type ) );
			json.WriteNumber( "FrameCount", animation.FrameCount );
			WriteNodes( json, skeleton );

			json.WriteNumber( "Index", animation.Index );
			json.WriteString( "Graph", TagJsonWriter.TagName( graph.Tag ) );
			json.WriteString( "Model", skeleton.Model );
			json.WriteNumber( "FrameRate", animation.FrameRate );
			json.WriteNumber( "LoopFrame", animation.LoopFrameIndex );
			json.WriteBoolean( "Compressed", animation.IsCompressed );
			json.WriteBoolean( "WorldRelative", animation.IsWorldRelative );
			json.WriteString( "Form", overlay ? OverlayForm : PoseForm );
			json.WriteString( "Convention", Convention );
			json.WriteNumber( "UnitsPerWorldUnit", Round( scale ) );

			json.WriteStartObject( "Animated" );
			foreach ( var (name, test) in new (string, Func<int, bool>)[] { ("Rotation", animation.AnimatesRotation), ("Position", animation.AnimatesTranslation), ("Scale", animation.AnimatesScale) } )
			{
				json.WriteStartArray( name );
				foreach ( var n in driven.Where( test ) )
					json.WriteNumberValue( skeleton.BoneOfNode[n] );
				json.WriteEndArray();
			}
			json.WriteEndObject();

			var frameInfo = animation.ReadFrameInfo();
			if ( frameInfo.Length > 0 )
			{
				json.WriteString( "FrameInfoType", FrameInfoName( animation.FrameInfoType ) );
				json.WriteStartArray( "FrameInfo" );
				foreach ( var info in frameInfo )
					WriteArray( json, info.Translation.X * scale, info.Translation.Y * scale, info.Translation.Z * scale, info.Yaw );
				json.WriteEndArray();
			}

			json.WriteStartArray( "Frames" );
			for ( var f = 0; f < animation.FrameCount; f++ )
			{
				json.WriteStartObject();
				json.WriteStartArray( "Nodes" );
				foreach ( var n in driven )
				{
					var pose = poses[f * nodes + n];
					json.WriteStartObject();
					json.WriteNumber( "Node", skeleton.BoneOfNode[n] );
					if ( !overlay || animation.AnimatesRotation( n ) )
					{
						json.WritePropertyName( "Rotation" );
						WriteArray( json, pose.Rotation.X, pose.Rotation.Y, pose.Rotation.Z, pose.Rotation.W );
					}
					if ( !overlay || animation.AnimatesTranslation( n ) )
					{
						// As GbxSkeleton: the float product, then rounded.
						var position = pose.Translation * scale;
						json.WritePropertyName( "Position" );
						WriteArray( json, position.X, position.Y, position.Z );
					}
					if ( !overlay || animation.AnimatesScale( n ) )
						json.WriteNumber( "Scale", Round( pose.Scale ) );
					json.WriteEndObject();
				}
				json.WriteEndArray();
				json.WriteEndObject();
			}
			json.WriteEndArray();
			json.WriteEndObject();
		} );
	}

	/// <summary>
	/// A graph without its frames:
	/// <code>
	/// { "Graph": "weapons/pistol/fp/fp.antr", "Model": "mount://haloce/weapons/pistol/pistol_fp.vmdl", "FirstPerson": true,
	///   "Nodes": [ { "Name", "Parent" }, ... ], "Sounds": [ "weapons/pistol/sounds/reload.snd!", ... ],
	///   "FirstPersonSlots": { "idle": 0, "moving": 3, ... },
	///   "Animations": [ { "Index", "Name", "Resource", "Type", "FrameCount", "FrameRate", "NodeCount", "FrameInfoType",
	///     "LoopFrame", "NextAnimation", "KeyFrame", "SecondKeyFrame", "Sound", "SoundFrame", "LeftFootFrame",
	///     "RightFootFrame", "Compressed", "WorldRelative", "Decodes" }, ... ] }
	/// </code>
	/// "Resource" is the animation's document under <c>mount://haloce/</c>; "NextAnimation" and
	/// "Sound" index "Animations" and "Sounds", and like the frame indices are -1 for none;
	/// "FirstPersonSlots" (first person graphs only) gives the animation index of each first person
	/// slot the weapon has; "Decodes" is false for the few animations whose data can't be read.
	/// </summary>
	public static byte[] Graph( AnimationSkeleton skeleton )
	{
		var graph = skeleton.Graph;
		var resources = AnimationResources( graph );
		return Write( json =>
		{
			json.WriteStartObject();
			json.WriteString( "Graph", TagJsonWriter.TagName( graph.Tag ) );
			json.WriteString( "Model", skeleton.Model );
			json.WriteBoolean( "FirstPerson", skeleton.FirstPerson );
			json.WriteString( "Convention", Convention );
			json.WriteNumber( "UnitsPerWorldUnit", Round( UnitsPerWorldUnit ) );
			WriteNodes( json, skeleton );

			json.WriteStartArray( "Sounds" );
			foreach ( var sound in graph.SoundReferences )
				json.WriteStringValue( TagJsonWriter.TagName( sound ) );
			json.WriteEndArray();

			if ( skeleton.FirstPerson )
			{
				json.WriteStartObject( "FirstPersonSlots" );
				var slots = graph.FirstPersonAnimations;
				for ( var s = 0; s < Math.Min( slots.Count, FirstPersonAnimations.Names.Count ); s++ )
				{
					if ( slots[s] >= 0 && slots[s] < graph.Animations.Count )
						json.WriteNumber( FirstPersonAnimations.Names[s], slots[s] );
				}
				json.WriteEndObject();
			}

			json.WriteStartArray( "Animations" );
			foreach ( var animation in graph.Animations )
			{
				json.WriteStartObject();
				json.WriteNumber( "Index", animation.Index );
				json.WriteString( "Name", animation.Name );
				json.WriteString( "Resource", resources[animation.Index] );
				json.WriteString( "Type", TypeName( animation.Type ) );
				json.WriteNumber( "FrameCount", animation.FrameCount );
				json.WriteNumber( "FrameRate", animation.FrameRate );
				json.WriteNumber( "NodeCount", animation.NodeCount );
				json.WriteString( "FrameInfoType", FrameInfoName( animation.FrameInfoType ) );
				json.WriteNumber( "LoopFrame", animation.LoopFrameIndex );
				json.WriteNumber( "NextAnimation", animation.NextAnimationIndex );
				json.WriteNumber( "KeyFrame", animation.KeyFrameIndex );
				json.WriteNumber( "SecondKeyFrame", animation.SecondKeyFrameIndex );
				json.WriteNumber( "Sound", animation.SoundIndex );
				json.WriteNumber( "SoundFrame", animation.SoundFrameIndex );
				json.WriteNumber( "LeftFootFrame", animation.LeftFootFrameIndex );
				json.WriteNumber( "RightFootFrame", animation.RightFootFrameIndex );
				json.WriteBoolean( "Compressed", animation.IsCompressed );
				json.WriteBoolean( "WorldRelative", animation.IsWorldRelative );
				json.WriteBoolean( "Decodes", animation.CanDecode );
				json.WriteEndObject();
			}
			json.WriteEndArray();
			json.WriteEndObject();
		} );
	}

	static void WriteNodes( Utf8JsonWriter json, AnimationSkeleton skeleton )
	{
		json.WriteStartArray( "Nodes" );
		for ( var b = 0; b < skeleton.BoneNames.Count; b++ )
		{
			json.WriteStartObject();
			json.WriteString( "Name", skeleton.BoneNames[b] );
			json.WriteNumber( "Parent", skeleton.BoneParents[b] );
			json.WriteEndObject();
		}
		json.WriteEndArray();
	}

	static string TypeName( AnimationType type ) => type switch
	{
		AnimationType.Overlay => "overlay",
		AnimationType.Replacement => "replacement",
		_ => "base",
	};

	static string FrameInfoName( AnimationFrameInfoType type ) => type switch
	{
		AnimationFrameInfoType.DxDy => "dx dy",
		AnimationFrameInfoType.DxDyDyaw => "dx dy dyaw",
		AnimationFrameInfoType.DxDyDzDyaw => "dx dy dz dyaw",
		_ => "none",
	};

	static void WriteArray( Utf8JsonWriter json, params ReadOnlySpan<float> values )
	{
		json.WriteStartArray();
		foreach ( var value in values )
			json.WriteNumberValue( Round( value ) );
		json.WriteEndArray();
	}

	/// <summary>
	/// Rounded to 1e-5 (finer than the int16 rotations), without negative zeros; NaN and infinity,
	/// which JSON can't hold, as 0.
	/// </summary>
	static double Round( float value )
	{
		var rounded = Math.Round( (double)value, 5 );
		return double.IsFinite( rounded ) && rounded != 0 ? rounded : 0;
	}

	static byte[] Write( Action<Utf8JsonWriter> write )
	{
		using var stream = new MemoryStream();
		using ( var json = new Utf8JsonWriter( stream, Compact ) )
			write( json );

		return stream.ToArray();
	}
}
