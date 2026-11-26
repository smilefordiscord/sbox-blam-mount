using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using AnimationFrameInfoType = Blam.Halo1.Tags.AnimationFrameInfoType;
using AnimationType = Blam.Halo1.Tags.AnimationType;
using Blam.Halo3.Tags;

namespace Blam.Halo3;

// A Halo 3 model tag's animations as JSON with every frame decoded, for games that pose bones themselves, in the shape of Halo 2's (Blam/Halo2/AnimationDataExport.cs, whose remarks describe the conventions):
// mount://halo3/anims/<hlmt path>.json (bones, labels, blend screens, object overlays, every animation without frames) and mount://halo3/anims/<hlmt path>/<animation name, ':' as '/'>.json (one animation's frames).
// Transforms are local to the parent in s&box axes and units, rotations as stored (the .vmdl's values); overlays are written as stored deltas (local rotation = base * Rotation, position = base + Position).
public static class AnimationDataExport
{
	// s&box units per Halo world unit (the mount's world scale).
	public const float UnitsPerWorldUnit = 64f / 0.62f;

	const string MountRoot = "mount://halo3/";

	const string Convention = "Transforms are local to the parent node (Parent indexes Nodes, -1 = root), composed like s&box Transform.ToWorld: "
		+ "model rotation = parent rotation * Rotation, model position = parent position + parent rotation * Position. s&box axes (= Halo's: x forward, y left, z up). "
		+ "Position [x,y,z] in s&box units (Halo world units * UnitsPerWorldUnit), Rotation [x,y,z,w] an s&box Rotation, Scale uniform; "
		+ "the same values the mount's .vmdl animations use. Rounded to 1e-5.";

	const string PoseForm = "pose: every frame lists every node the animation drives with its full local transform.";

	const string OverlayForm = "raw overlay deltas, as stored, not baked: each frame lists only the nodes the overlay sets and only the components it sets "
		+ "(missing = identity: Rotation [0,0,0,1], Position [0,0,0], Scale 1). Apply over the pose underneath: local rotation = base rotation * Rotation, "
		+ "local position = base position + Position (parent space), scale = base scale * Scale. Aiming grids are the identity at their centre frame. "
		+ "The .vmdl bakes these onto the default pose.";

	static readonly JsonWriterOptions Compact = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

	// Where a model tag's index document goes: anims/objects/characters/masterchief/mp_masterchief/mp_masterchief.json.
	public static string GraphResource( TagEntry hlmt ) => $"anims/{hlmt.Path.Replace( '\\', '/' )}.json";

	// Where each animation of a set goes (by index in ModelAnimationSet.Animations): the name's ':' become folders, characters a path can't hold become '_', and a name repeated (ignoring case) gets its index appended.
	public static string[] AnimationResources( TagEntry hlmt, ModelAnimationSet set )
	{
		var folder = hlmt.Path.Replace( '\\', '/' );
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

	// Whether the mount gives the model a skeleton (as RenderModelBuilder): 2 to 255 nodes, parents first, or one node its graph animates.
	public static bool HasSkeleton( RenderModelTag model, ModelAnimationSet set ) =>
		model.Nodes.Count is >= 2 and <= 255 && model.Nodes.Select( ( n, i ) => n.Parent < i ).All( x => x )
		|| model.Nodes.Count == 1 && set is not null && set.Graph.Animations.Any( a => a.HasData && a.FrameCount > 1 );

	static string ModelResource( TagEntry hlmt, RenderModelTag model, ModelAnimationSet set ) =>
		HasSkeleton( model, set ) ? $"{MountRoot}{hlmt.Path.Replace( '\\', '/' )}.vmdl" : null;

	static string TagName( TagEntry tag ) => tag is null ? null : $"{tag.Path.Replace( '\\', '/' )}.{tag.Class}";

	// One animation with its frames ({ Name, Type, FrameCount, Nodes, Frames: [ { Nodes: [ { Node, Rotation, Position, Scale } ] } ] } plus Index, Graph, Model, FrameRate, LoopFrame, BlendScreen, Form, Convention, UnitsPerWorldUnit, Animated and root motion FrameInfoType/FrameInfo), as Halo 2's.
	public static byte[] Animation( TagEntry hlmt, ModelAnimationSet set, int index, float scale = UnitsPerWorldUnit )
	{
		var entry = set.Animations[index];
		var animation = entry.Animation;
		var data = animation.Decode();
		var overlay = animation.Type == AnimationType.Overlay;
		var model = set.Model;
		var names = Blam.Halo1.TagData.AnimationSkeleton.UniqueNames( model.Nodes.Select( n => n.Name ) );

		// The animation nodes on bones, in bone order.
		var driven = Enumerable.Range( 0, Math.Min( data.NodeCount, entry.NodeOfAnimationNode.Length ) )
			.Where( n => entry.NodeOfAnimationNode[n] >= 0 && (!overlay || data.SetsRotation( n ) || data.SetsTranslation( n ) || data.SetsScale( n )) )
			.OrderBy( n => entry.NodeOfAnimationNode[n] )
			.ToArray();

		return Write( json =>
		{
			json.WriteStartObject();
			json.WriteString( "Name", entry.Name );
			json.WriteString( "Type", TypeName( animation.Type ) );
			json.WriteNumber( "FrameCount", animation.FrameCount );
			WriteNodes( json, model, names );

			json.WriteNumber( "Index", index );
			json.WriteString( "Graph", TagName( animation.Graph.Tag ) );
			json.WriteString( "Model", ModelResource( hlmt, model, set ) );
			json.WriteNumber( "FrameRate", animation.FrameRate );
			json.WriteNumber( "LoopFrame", animation.LoopFrame );
			json.WriteNumber( "BlendScreen", animation.BlendScreen );
			json.WriteString( "Form", overlay ? OverlayForm : PoseForm );
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
				var pose = overlay ? null : set.LocalPose( entry, f );
				json.WriteStartObject();
				json.WriteStartArray( "Nodes" );
				foreach ( var n in driven )
				{
					var bone = entry.NodeOfAnimationNode[n];
					json.WriteStartObject();
					json.WriteNumber( "Node", bone );
					var rotation = overlay ? data.Rotation( n, f ) : pose[bone].Rotation;
					var translation = overlay ? data.Translation( n, f ) : pose[bone].Translation;
					var nodeScale = overlay ? data.Scale( n, f ) : pose[bone].Scale;
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

	// A model tag's animations without their frames ({ ModelTag, Graph, Model, Nodes, Animations, Labels, BlendScreens, ObjectOverlays, Suspension }), as Halo 2's; labels name animations by Name.
	public static byte[] Graph( TagEntry hlmt, ModelAnimationSet set )
	{
		var model = set.Model;
		var names = Blam.Halo1.TagData.AnimationSkeleton.UniqueNames( model.Nodes.Select( n => n.Name ) );
		var resources = AnimationResources( hlmt, set );
		return Write( json =>
		{
			json.WriteStartObject();
			json.WriteString( "ModelTag", TagName( hlmt ) );
			json.WriteString( "Graph", TagName( set.Graph.Tag ) );
			json.WriteString( "Model", ModelResource( hlmt, model, set ) );
			json.WriteString( "Convention", Convention );
			json.WriteNumber( "UnitsPerWorldUnit", Round( UnitsPerWorldUnit ) );
			WriteNodes( json, model, names );

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

	static void WriteNodes( Utf8JsonWriter json, RenderModelTag model, string[] names )
	{
		json.WriteStartArray( "Nodes" );
		for ( var b = 0; b < names.Length; b++ )
		{
			json.WriteStartObject();
			json.WriteString( "Name", names[b] );
			json.WriteNumber( "Parent", model.Nodes[b].Parent );
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
