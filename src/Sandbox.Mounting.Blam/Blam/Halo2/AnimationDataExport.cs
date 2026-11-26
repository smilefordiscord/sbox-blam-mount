using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Blam.Halo1.Tags;
using Blam.Halo2.Tags;

namespace Blam.Halo2;

/// <summary>
/// A Halo 2 model tag's animations as JSON, with every frame decoded, for games that pose bones themselves, in the
/// same shape as the Halo CE mount's animation data (Blam.Halo1.TagData.AnimationExport):
/// <list type="bullet">
/// <item><c>mount://halo2/anims/&lt;hlmt path&gt;.json</c>: the model's bones, its graph's labels (which animation
/// <c>combat:unarmed:any:idle</c> plays), blend screens, object overlays and every animation without frames.</item>
/// <item><c>mount://halo2/anims/&lt;hlmt path&gt;/&lt;animation name, ':' as '/'&gt;.json</c>: one animation's frames,
/// e.g. <c>anims/objects/characters/masterchief/masterchief_mp/combat/rifle/idle.json</c>.</item>
/// </list>
/// </summary>
/// <remarks>
/// Halo 2 graphs belong to model tags (<c>hlmt</c>), so the documents are per model tag, next to the
/// <c>mount://halo2/&lt;hlmt path&gt;.vmdl</c> whose bones and sequences they match (<see cref="ModelAnimationSet"/>:
/// the graph's own animations and, for the names it lacks, its inherited graphs', under the same names).
///
/// Conventions: bones are the render model's nodes in order, "Parent" an index into "Nodes" (-1 for a root).
/// Transforms are local to the parent, composed like s&amp;box's <c>Transform.ToWorld</c> (model rotation = parent
/// rotation * rotation, model position = parent position + parent rotation * position), in s&amp;box's axes (= Halo's:
/// x forward, y left, z up). "Position" is s&amp;box units (world units * UnitsPerWorldUnit), "Rotation" an s&amp;box
/// <c>Rotation</c> <c>[x, y, z, w]</c> (Halo 2 stores them in this convention, unlike Halo CE), "Scale" uniform:
/// the values the .vmdl's animations hold. Rounded to 1e-5.
///
/// Base animations list every bone they drive in every frame with its full local pose (components the animation
/// doesn't set are the model's default pose). Replacements list only the bones and components they set, as absolute
/// local values (what they don't set stays as the pose underneath has it). Overlays (aiming grids, accelerations, device positions)
/// are written as stored: each frame lists the bones the overlay sets and only the components it sets, as changes from
/// the pose underneath (local rotation = base rotation * Rotation, local position = base position + Position, scale
/// multiplied); an aiming grid's centre frame is the identity. The .vmdl bakes them over the default pose.
/// </remarks>
public static class AnimationDataExport
{
	/// <summary>s&amp;box units per Halo world unit (the mount's world scale).</summary>
	public const float UnitsPerWorldUnit = 64f / 0.62f;

	const string MountRoot = "mount://halo2/";

	const string Convention = "Transforms are local to the parent node (Parent indexes Nodes, -1 = root), composed like s&box Transform.ToWorld: "
		+ "model rotation = parent rotation * Rotation, model position = parent position + parent rotation * Position. s&box axes (= Halo's: x forward, y left, z up). "
		+ "Position [x,y,z] in s&box units (Halo world units * UnitsPerWorldUnit), Rotation [x,y,z,w] an s&box Rotation, Scale uniform; "
		+ "the same values the mount's .vmdl animations use. Rounded to 1e-5.";

	const string PoseForm = "pose: every frame lists every node the animation drives with its full local transform.";

	const string OverlayForm = "raw overlay deltas, as stored, not baked: each frame lists only the nodes the overlay sets and only the components it sets "
		+ "(missing = identity: Rotation [0,0,0,1], Position [0,0,0], Scale 1). Apply over the pose underneath: local rotation = base rotation * Rotation, "
		+ "local position = base position + Position (parent space), scale = base scale * Scale. Aiming grids are the identity at their centre frame. "
		+ "The .vmdl bakes these onto the default pose (a first person view model's onto the first frame of its weapon class's idle).";

	const string ReplacementForm = "replacement, as stored: each frame lists only the nodes the replacement sets and only the components it sets, as the node's absolute local "
		+ "Rotation, Position and Scale; what it doesn't list keeps the pose underneath. The .vmdl bakes these onto the default pose (a first person view model's onto the first frame "
		+ "of its weapon class's idle).";

	static readonly JsonWriterOptions Compact = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

	/// <summary>Where a model tag's index document goes: <c>anims/objects/characters/masterchief/masterchief_mp.json</c>.</summary>
	public static string GraphResource( TagEntry hlmt ) => $"anims/{hlmt.Path.Replace( '\\', '/' )}.json";

	/// <summary>
	/// Where each animation of a set goes (by index in <see cref="ModelAnimationSet.Animations"/>): the name's ':' become
	/// folders, characters a path can't hold become '_', and a name repeated (ignoring case) gets its index appended.
	/// </summary>
	public static string[] AnimationResources( TagEntry hlmt, ModelAnimationSet set ) => AnimationResources( hlmt.Path.Replace( '\\', '/' ), set );

	// The same below another folder (a first person view model's resource name).
	public static string[] AnimationResources( string folder, ModelAnimationSet set )
	{
		var used = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
		var result = new string[set.Animations.Count];
		for ( var i = 0; i < result.Length; i++ )
		{
			var name = string.Join( "/", set.Animations[i].Name.Split( ':' )
				.Select( part => new string( part.Select( c => c < 0x20 || "/\\*?\"<>|".Contains( c ) ? '_' : c ).ToArray() ).Trim() )
				.Select( part => part.Length == 0 || part is "." or ".." ? "_" : part ) );
			if ( !used.Add( name ) )
				used.Add( name = $"{name} {i}" );

			result[i] = $"anims/{folder}/{name}.json";
		}

		return result;
	}

	/// <summary>
	/// Whether the mount gives the model a skeleton (as RenderModelBuilder): 2 to 255 nodes, parents first, or one node
	/// its graph animates.
	/// </summary>
	public static bool HasSkeleton( RenderModelTag model, ModelAnimationSet set ) =>
		model.Nodes.Count is >= 2 and <= 255 && model.Nodes.Select( ( n, i ) => n.Parent < i ).All( x => x )
		|| model.Nodes.Count == 1 && set is not null && set.Graph.Animations.Any( a => a.HasData && a.FrameCount > 1 );

	static string ModelResource( TagEntry hlmt, RenderModelTag model, ModelAnimationSet set ) =>
		HasSkeleton( model, set ) ? $"{MountRoot}{hlmt.Path.Replace( '\\', '/' )}.vmdl" : null;

	static string TagName( TagEntry tag ) => tag is null ? null : $"{tag.Path.Replace( '\\', '/' )}.{tag.Class}";

	/// <summary>
	/// One animation with its frames:
	/// <code>
	/// { "Name": "combat:rifle:idle", "Type": "base", "FrameCount": 59, "Nodes": [ { "Name": "pelvis", "Parent": -1, "Flags": [] }, ... ],
	///   "Frames": [ { "Nodes": [ { "Node": 0, "Rotation": [x, y, z, w], "Position": [x, y, z], "Scale": 1 }, ... ] }, ... ], ...extras }
	/// </code>
	/// Extras: "Index" (in the set), "Graph" (the graph it comes from, an inherited one for inherited animations), "Model",
	/// "FrameRate" (30), "LoopFrame", "BlendScreen" (index into the index document's, -1 for none), the frame events (as in
	/// the index document's "Animations"), "Form", "Convention",
	/// "UnitsPerWorldUnit", "Animated" (bones whose Rotation/Position/Scale change over the frames), and with root motion
	/// "FrameInfoType" and "FrameInfo" (per frame [dx, dy, dz, dyaw]: the object's movement in its own axes, s&amp;box
	/// units, and turn, radians, positive left).
	/// </summary>
	public static byte[] Animation( TagEntry hlmt, ModelAnimationSet set, int index, float scale = UnitsPerWorldUnit ) =>
		Animation( set, index, ModelResource( hlmt, set.Model, set ), scale );

	static byte[] Animation( ModelAnimationSet set, int index, string modelResource, float scale )
	{
		var entry = set.Animations[index];
		var animation = entry.Animation;
		var data = animation.Decode();
		var overlay = animation.Type == AnimationType.Overlay;
		var replacement = animation.Type == AnimationType.Replacement;
		var partial = overlay || replacement;
		var names = Halo1.TagData.AnimationSkeleton.UniqueNames( set.Nodes.Select( n => n.Name ) );

		// The animation nodes on bones, in bone order: an overlay's or a replacement's only where it sets something.
		var driven = Enumerable.Range( 0, animation.NodeCount )
			.Where( n => entry.NodeOfAnimationNode[n] >= 0 && (!partial || data.SetsRotation( n ) || data.SetsTranslation( n ) || data.SetsScale( n )) )
			.OrderBy( n => entry.NodeOfAnimationNode[n] )
			.ToArray();

		return Write( json =>
		{
			json.WriteStartObject();
			json.WriteString( "Name", entry.Name );
			json.WriteString( "Type", TypeName( animation.Type ) );
			json.WriteNumber( "FrameCount", animation.FrameCount );
			WriteNodes( json, set, names );

			json.WriteNumber( "Index", index );
			json.WriteString( "Graph", TagName( animation.Graph.Tag ) );
			json.WriteString( "Model", modelResource );
			json.WriteNumber( "FrameRate", animation.FrameRate );
			json.WriteNumber( "LoopFrame", animation.LoopFrame );
			json.WriteNumber( "BlendScreen", animation.BlendScreen );
			WriteEvents( json, animation );
			json.WriteString( "Form", overlay ? OverlayForm : replacement ? ReplacementForm : PoseForm );
			json.WriteString( "Convention", Convention );
			json.WriteNumber( "UnitsPerWorldUnit", Round( scale ) );

			json.WriteStartObject( "Animated" );
			foreach ( var (name, test) in new (string, Func<int, bool>)[] { ("Rotation", data.AnimatesRotation), ("Position", data.AnimatesTranslation), ("Scale", data.AnimatesScale) } )
			{
				json.WriteStartArray( name );
				foreach ( var n in driven.Where( test ) )
					json.WriteNumberValue( entry.NodeOfAnimationNode[n] );
				json.WriteEndArray();
			}
			json.WriteEndObject();

			if ( data.Movement.Length > 0 )
			{
				json.WriteString( "FrameInfoType", FrameInfoName( animation.FrameInfoType ) );
				json.WriteStartArray( "FrameInfo" );
				foreach ( var info in data.Movement )
					WriteArray( json, info.Translation.X * scale, info.Translation.Y * scale, info.Translation.Z * scale, info.Yaw );
				json.WriteEndArray();
			}

			json.WriteStartArray( "Frames" );
			for ( var f = 0; f < animation.FrameCount; f++ )
			{
				var pose = partial ? null : set.LocalPose( entry, f );
				json.WriteStartObject();
				json.WriteStartArray( "Nodes" );
				foreach ( var n in driven )
				{
					var bone = entry.NodeOfAnimationNode[n];
					json.WriteStartObject();
					json.WriteNumber( "Node", bone );
					var rotation = partial ? data.Rotation( n, f ) : pose[bone].Rotation;
					var translation = partial ? data.Translation( n, f ) : pose[bone].Translation;
					var nodeScale = partial ? data.Scale( n, f ) : pose[bone].Scale;

					// A replacement's root height scales as the pose's does (see ModelAnimationSet.LocalPose).
					if ( replacement && bone == 0 && translation is { } root && entry.RootZScale != 1 )
						translation = root with { Z = root.Z * entry.RootZScale };
					if ( rotation is { } r )
					{
						json.WritePropertyName( "Rotation" );
						WriteArray( json, r.X, r.Y, r.Z, r.W );
					}
					if ( translation is { } t )
					{
						var position = t * scale;
						json.WritePropertyName( "Position" );
						WriteArray( json, position.X, position.Y, position.Z );
					}
					if ( nodeScale is { } s )
						json.WriteNumber( "Scale", Round( s ) );
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
	/// A model tag's animations without their frames:
	/// <code>
	/// { "ModelTag": "objects/characters/masterchief/masterchief_mp.hlmt", "Graph": "objects/characters/masterchief/masterchief.jmad",
	///   "Model": "mount://halo2/objects/characters/masterchief/masterchief_mp.vmdl", "Nodes": [ { "Name", "Parent", "Flags" }, ... ],
	///   "LeftArmNodes": [ "l_clavicle", ... ], "RightArmNodes": [ ... ],
	///   "Animations": [ { "Index", "Name", "Resource", "Type", "FrameCount", "FrameRate", "NodeCount", "FrameInfoType", "LoopFrame",
	///     "NextAnimation", "BlendScreen", "Weight", "Graph", "Inherited", "PlaybackFlags": [ "disable weapon aim/first person", ... ],
	///     "KeyFrame", "SecondKeyFrame", "LeftFootFrame", "RightFootFrame", "AllowInterruptionFrame", "FrameEvents": [ { "Type", "Frame" } ], "SoundEvents": [ { "Sound",
	///     "Frame", "Marker", "Flags" } ], "EffectEvents": [ { "Effect", "Frame", "Flags" } ] }, ... ],
	///   "Labels": [ { "Path": "combat:unarmed:any:idle", "Mode", "WeaponClass", "WeaponType", "Label", "Overlay", "Animation": "combat:sword:idle" }, ... ],
	///   "BlendScreens": [ { "Label", "RightYawPerFrame", "LeftYawPerFrame", "RightFrames", "LeftFrames", "DownPitchPerFrame", "UpPitchPerFrame", "DownFrames", "UpFrames" }, ... ],
	///   "ObjectOverlays": [ { "Label", "Animation", "Function", "FunctionControls" }, ... ], "Suspension": [ { "Label", "Animation" }, ... ] }
	/// </code>
	/// Labels name animations by "Name" (each one is in "Animations"; null when the label has none). Angles are radians.
	/// A node's "Flags" are its graph node's model flags ("left hand", "right hand", "left arm member"...); "LeftArmNodes" and
	/// "RightArmNodes" are the graph's arm masks, by node name.
	/// </summary>
	public static byte[] Graph( TagEntry hlmt, ModelAnimationSet set ) => Graph( set, AnimationResources( hlmt, set ), json =>
	{
		json.WriteString( "ModelTag", TagName( hlmt ) );
		json.WriteString( "Graph", TagName( set.Graph.Tag ) );
		json.WriteString( "Model", ModelResource( hlmt, set.Model, set ) );
	} );

	// A weapon's first person view model's name below the mount (objects/weapons/rifle/smg/smg_fp): its .vmdl, and its documents' folder below anims/.
	public static string FirstPersonName( TagEntry weapon ) => $"{weapon.Path.Replace( '\\', '/' )}_fp";

	// Where a first person view model's index document goes: anims/objects/weapons/rifle/smg/smg_fp.json.
	public static string FirstPersonGraphResource( TagEntry weapon ) => $"anims/{FirstPersonName( weapon )}.json";

	// The first person animations' documents, each at anims/<weapon path>_fp/<animation name, ':' as '/'>.json.
	public static string[] FirstPersonAnimationResources( FirstPersonSkeleton skeleton, ModelAnimationSet set ) =>
		AnimationResources( FirstPersonName( skeleton.Weapon.Weapon ), set );

	// A first person view model's index document, as Graph's with the first person setup in place of "ModelTag".
	// "Weapon", "Unit" (whose hands), "Hands" and "WeaponModel" (render models) and "Entry" (of the weapon's first person block) name it.
	// "UnitModel" is the unit's model tag, "ShieldMaterial" and "OvershieldMaterial" its first person shield materials (null without).
	public static byte[] FirstPersonGraph( FirstPersonSkeleton skeleton, ModelAnimationSet set )
	{
		var weapon = skeleton.Weapon;
		var unitModel = new ObjectTag( weapon.Unit ).Model;
		var hlmt = unitModel is { Address: not 0 } && unitModel.Class == ModelTag.Class ? new ModelTag( unitModel ) : null;

		string Material( TagEntry shader, string suffix ) =>
			shader is { Address: not 0 } && ShieldCurves.IsShield( new ShaderTag( shader ) ) ? $"{MountRoot}{hlmt.Tag.Path.Replace( '\\', '/' )}{suffix}.vmat" : null;

		return Graph( set, FirstPersonAnimationResources( skeleton, set ), json =>
		{
			json.WriteString( "Weapon", TagName( weapon.Weapon ) );
			json.WriteString( "Unit", TagName( weapon.Unit ) );
			json.WriteString( "Hands", TagName( weapon.Hands ) );
			json.WriteString( "WeaponModel", TagName( weapon.Model ) );
			json.WriteNumber( "Entry", weapon.Entry );
			json.WriteString( "Graph", TagName( set.Graph.Tag ) );
			json.WriteString( "Model", FirstPersonModelResource( weapon.Weapon ) );
			json.WriteString( "UnitModel", TagName( hlmt?.Tag ) );
			json.WriteString( "ShieldMaterial", Material( hlmt?.ShieldFirstPersonShader, "_shield_fp" ) );
			json.WriteString( "OvershieldMaterial", Material( hlmt?.OvershieldFirstPersonShader, "_overshield_fp" ) );
		} );
	}

	// One animation of a first person view model, in Animation's form.
	public static byte[] FirstPersonAnimation( FirstPersonSkeleton skeleton, ModelAnimationSet set, int index, float scale = UnitsPerWorldUnit ) =>
		Animation( set, index, FirstPersonModelResource( skeleton.Weapon.Weapon ), scale );

	static string FirstPersonModelResource( TagEntry weapon ) => $"{MountRoot}{FirstPersonName( weapon )}.vmdl";

	static byte[] Graph( ModelAnimationSet set, string[] resources, Action<Utf8JsonWriter> header )
	{
		var names = Halo1.TagData.AnimationSkeleton.UniqueNames( set.Nodes.Select( n => n.Name ) );
		return Write( json =>
		{
			json.WriteStartObject();
			header( json );
			json.WriteString( "Convention", Convention );
			json.WriteNumber( "UnitsPerWorldUnit", Round( UnitsPerWorldUnit ) );
			WriteNodes( json, set, names );
			WriteArmNodes( json, "LeftArmNodes", set, set.Graph.LeftArmNodes, names );
			WriteArmNodes( json, "RightArmNodes", set, set.Graph.RightArmNodes, names );

			json.WriteStartArray( "Animations" );
			for ( var i = 0; i < set.Animations.Count; i++ )
			{
				var entry = set.Animations[i];
				var a = entry.Animation;
				json.WriteStartObject();
				json.WriteNumber( "Index", i );
				json.WriteString( "Name", entry.Name );
				json.WriteString( "Resource", resources[i] );
				json.WriteString( "Type", TypeName( a.Type ) );
				json.WriteNumber( "FrameCount", a.FrameCount );
				json.WriteNumber( "FrameRate", a.FrameRate );
				json.WriteNumber( "NodeCount", a.NodeCount );
				json.WriteString( "FrameInfoType", FrameInfoName( a.FrameInfoType ) );
				json.WriteNumber( "LoopFrame", a.LoopFrame );
				json.WriteString( "NextAnimation", a.NextAnimation >= 0 && a.NextAnimation < a.Graph.Animations.Count ? a.Graph.Animations[a.NextAnimation].Name : null );
				json.WriteNumber( "BlendScreen", a.BlendScreen );
				json.WriteNumber( "Weight", Round( a.Weight ) );
				json.WriteString( "Graph", TagName( a.Graph.Tag ) );
				json.WriteBoolean( "Inherited", entry.Inherited );
				json.WriteStartArray( "PlaybackFlags" );
				for ( var bit = 0; bit < 16; bit++ )
				{
					if ( (a.PlaybackFlags & (1 << bit)) != 0 )
						json.WriteStringValue( bit < GraphAnimation.PlaybackFlagNames.Length ? GraphAnimation.PlaybackFlagNames[bit] : $"bit {bit}" );
				}
				json.WriteEndArray();
				WriteEvents( json, a );
				json.WriteEndObject();
			}
			json.WriteEndArray();

			json.WriteStartArray( "Labels" );
			foreach ( var label in set.Graph.Labels )
			{
				json.WriteStartObject();
				json.WriteString( "Path", label.Path );
				json.WriteString( "Mode", label.Mode );
				json.WriteString( "WeaponClass", label.WeaponClass );
				json.WriteString( "WeaponType", label.WeaponType );
				json.WriteString( "Label", label.Label );
				json.WriteBoolean( "Overlay", label.Overlay );
				json.WriteString( "Animation", set.Resolve( label.Animation )?.Name );
				json.WriteEndObject();
			}
			json.WriteEndArray();

			json.WriteStartArray( "BlendScreens" );
			foreach ( var screen in set.Graph.BlendScreens )
			{
				var b = screen.Bounds;
				json.WriteStartObject();
				json.WriteString( "Label", screen.Label );
				json.WriteNumber( "RightYawPerFrame", Round( b.NegativeYawDelta ) );
				json.WriteNumber( "LeftYawPerFrame", Round( b.PositiveYawDelta ) );
				json.WriteNumber( "RightFrames", b.NegativeYawFrames );
				json.WriteNumber( "LeftFrames", b.PositiveYawFrames );
				json.WriteNumber( "DownPitchPerFrame", Round( b.NegativePitchDelta ) );
				json.WriteNumber( "UpPitchPerFrame", Round( b.PositivePitchDelta ) );
				json.WriteNumber( "DownFrames", b.NegativePitchFrames );
				json.WriteNumber( "UpFrames", b.PositivePitchFrames );
				json.WriteEndObject();
			}
			json.WriteEndArray();

			json.WriteStartArray( "ObjectOverlays" );
			foreach ( var overlay in set.Graph.ObjectOverlays )
			{
				json.WriteStartObject();
				json.WriteString( "Label", overlay.Label );
				json.WriteString( "Animation", set.Resolve( overlay.Animation )?.Name );
				json.WriteString( "Function", overlay.Function );
				json.WriteNumber( "FunctionControls", overlay.FunctionControls );
				json.WriteEndObject();
			}
			json.WriteEndArray();

			json.WriteStartArray( "Suspension" );
			foreach ( var (label, reference) in set.Graph.VehicleSuspension )
			{
				json.WriteStartObject();
				json.WriteString( "Label", label );
				json.WriteString( "Animation", set.Resolve( reference )?.Name );
				json.WriteEndObject();
			}
			json.WriteEndArray();
			json.WriteEndObject();
		} );
	}

	// The frames the animation marks, under the Halo CE documents' names where it has them, -1 for none.
	// "FrameEvents" lists every event; "SoundEvents" and "EffectEvents" the sounds and effects it starts, with their reference flags.
	static void WriteEvents( Utf8JsonWriter json, GraphAnimation animation )
	{
		json.WriteNumber( "KeyFrame", animation.EventFrame( AnimationFrameEvent.PrimaryKeyframe ) );
		json.WriteNumber( "SecondKeyFrame", animation.EventFrame( AnimationFrameEvent.SecondaryKeyframe ) );
		json.WriteNumber( "LeftFootFrame", animation.EventFrame( AnimationFrameEvent.LeftFoot ) );
		json.WriteNumber( "RightFootFrame", animation.EventFrame( AnimationFrameEvent.RightFoot ) );
		json.WriteNumber( "AllowInterruptionFrame", animation.EventFrame( AnimationFrameEvent.AllowInterruption ) );

		json.WriteStartArray( "FrameEvents" );
		foreach ( var e in animation.FrameEvents )
		{
			json.WriteStartObject();
			json.WriteString( "Type", e.TypeName );
			json.WriteNumber( "Frame", e.Frame );
			json.WriteEndObject();
		}
		json.WriteEndArray();

		var graph = animation.Graph;
		json.WriteStartArray( "SoundEvents" );
		foreach ( var e in animation.SoundEvents )
		{
			// An event without a sound plays nothing (every first person throw_grenade has one at frame 0).
			if ( e.Sound < 0 || e.Sound >= graph.SoundReferences.Count || graph.SoundReferences[e.Sound].Tag is not { } sound )
				continue;

			json.WriteStartObject();
			json.WriteString( "Sound", TagName( sound ) );
			json.WriteNumber( "Frame", e.Frame );
			json.WriteString( "Marker", e.Marker );
			WriteReferenceFlags( json, graph.SoundReferences[e.Sound].Flags );
			json.WriteEndObject();
		}
		json.WriteEndArray();

		json.WriteStartArray( "EffectEvents" );
		foreach ( var e in animation.EffectEvents )
		{
			if ( e.Effect < 0 || e.Effect >= graph.EffectReferences.Count || graph.EffectReferences[e.Effect].Tag is not { } effect )
				continue;

			json.WriteStartObject();
			json.WriteString( "Effect", TagName( effect ) );
			json.WriteNumber( "Frame", e.Frame );
			WriteReferenceFlags( json, graph.EffectReferences[e.Effect].Flags );
			json.WriteEndObject();
		}
		json.WriteEndArray();
	}

	static void WriteReferenceFlags( Utf8JsonWriter json, int flags )
	{
		json.WriteStartArray( "Flags" );
		for ( var bit = 0; bit < 16; bit++ )
		{
			if ( (flags & (1 << bit)) != 0 )
				json.WriteStringValue( bit < AnimationEventReference.FlagNames.Length ? AnimationEventReference.FlagNames[bit] : $"bit {bit}" );
		}
		json.WriteEndArray();
	}

	// Each node with its graph node's model flags (the left arm and hands of a first person graph), found by name as the animations' are.
	static void WriteNodes( Utf8JsonWriter json, ModelAnimationSet set, string[] names )
	{
		var flags = new Dictionary<string, int>( StringComparer.OrdinalIgnoreCase );
		foreach ( var node in set.Graph.Nodes )
			flags.TryAdd( node.Name ?? "", node.ModelFlags );

		json.WriteStartArray( "Nodes" );
		for ( var b = 0; b < names.Length; b++ )
		{
			json.WriteStartObject();
			json.WriteString( "Name", names[b] );
			json.WriteNumber( "Parent", set.Nodes[b].Parent );
			json.WriteStartArray( "Flags" );
			for ( var bit = 0; bit < 8; bit++ )
			{
				if ( (flags.GetValueOrDefault( set.Nodes[b].Name ?? "" ) & (1 << bit)) != 0 )
					json.WriteStringValue( bit < AnimationGraphNode.FlagNames.Length ? AnimationGraphNode.FlagNames[bit] : $"bit {bit}" );
			}
			json.WriteEndArray();
			json.WriteEndObject();
		}
		json.WriteEndArray();
	}

	// A graph's arm node mask as the names of the nodes in it.
	static void WriteArmNodes( Utf8JsonWriter json, string property, ModelAnimationSet set, IReadOnlyList<int> graphNodes, string[] names )
	{
		var byName = new Dictionary<string, int>( StringComparer.OrdinalIgnoreCase );
		for ( var b = 0; b < set.Nodes.Count; b++ )
			byName.TryAdd( set.Nodes[b].Name ?? "", b );

		json.WriteStartArray( property );
		foreach ( var n in graphNodes )
		{
			if ( n < set.Graph.Nodes.Count && byName.TryGetValue( set.Graph.Nodes[n].Name ?? "", out var b ) )
				json.WriteStringValue( names[b] );
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
