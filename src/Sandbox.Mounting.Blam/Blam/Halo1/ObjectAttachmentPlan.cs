using System;
using System.Collections.Generic;
using System.Linq;
using Blam.Halo1.Tags;

namespace Blam.Halo1;

// Inside the namespace so these win over s&box's global types when this compiles into the mount.
using Vector3 = System.Numerics.Vector3;
using Quaternion = System.Numerics.Quaternion;

/// <summary>
/// What a child of an object's attachments prefab carries.
/// </summary>
public enum AttachmentKind
{
	/// <summary>A light (point or spot), with its lens flare if it has one.</summary>
	Light,
	/// <summary>Only a lens flare: a light with radius 0, or a light's flare at another instance of its marker.</summary>
	LensFlare,
	LoopingSound,
	/// <summary>A looping effect (<c>effe</c>, its loop events repeating).</summary>
	Effect,
	ParticleSystem,
	Contrail,
	/// <summary>A light volume widget (<c>mgs2</c>), scaled by its brightness source.</summary>
	LightVolume,
}

/// <summary>
/// One child of an object's attachments prefab: one of the object's attachments (or light
/// volume widgets) at one place on its model.
/// </summary>
/// <param name="Name">The child's name, unique in the prefab: "&lt;model attachment&gt; / &lt;tag file&gt;.&lt;class&gt;", "origin / ..." for none.</param>
/// <param name="Index">Index in the object's attachments block, -1 for a widget.</param>
/// <param name="Marker">The marker it's attached to, as the tag names it ("" for the object's origin).</param>
/// <param name="ModelAttachment">The attachment of the object's model (.vmdl) it sits on, named as
/// <see cref="ModelMarkerNames"/> names them ("primary trigger", "headlight 1"); "" for the origin
/// or a marker the model doesn't have.</param>
/// <param name="FirstPersonAttachment">For weapons, the attachment of the weapon's first person
/// model (<c>_fp.vmdl</c>) with the same marker, where Halo draws it in first person; "" if none.</param>
/// <param name="Position">Where on the model, in model space at the default pose (world units).</param>
/// <param name="Rotation">The marker's orientation; its +X is the marker's forward.</param>
/// <param name="LensFlare">The lens flare the child draws (lights and lens flares), or null.</param>
/// <param name="PrimaryScale">The object function (1-4: A-D out, 0 none) that scales it.</param>
/// <param name="SecondaryScale">A second function (effects only), same numbering.</param>
/// <param name="ChangeColor">Change color (1-4: A-D, 0 none) tinting it.</param>
/// <param name="RestValue">The primary scale's value at rest (see <see cref="ObjectFunctions.RestState"/>), 1 without one.</param>
/// <param name="OnAtRest">Whether Halo draws or plays it at rest: lights while their color isn't
/// black, everything else while the primary scale is active.</param>
/// <param name="Locations">A looping effect's: where each of its locations goes on the model (see
/// <see cref="EffectLocationSite"/>); null for the other kinds.</param>
public sealed record AttachmentChild(
	string Name,
	AttachmentKind Kind,
	TagEntry Type,
	int Index,
	string Marker,
	string ModelAttachment,
	string FirstPersonAttachment,
	Vector3 Position,
	Quaternion Rotation,
	TagEntry LensFlare,
	int PrimaryScale,
	int SecondaryScale,
	int ChangeColor,
	float RestValue,
	bool OnAtRest,
	IReadOnlyList<string> Tags,
	IReadOnlyList<EffectLocationSite> Locations );

/// <summary>
/// One place an object's looping effect makes what it makes at one of its locations. Halo gives the
/// effect no marker of its own, whatever marker the attachment names: each location goes to every
/// instance (up to 16) of the object's marker named like it, to the object's origin for a location
/// without a name, and nowhere for a marker the model doesn't have. a30's beam emitter carries the
/// same effect at "beam debris" and at "beam overflow", and both make smoke at "beam flare" (the
/// plasma ball its animation carries up into the sky) and at "beam overflow" (the shaft it rises from).
/// </summary>
/// <param name="Location">The effect's location (a marker name, "" for the object's origin).</param>
/// <param name="ModelAttachment">The model attachment it's on, named as <see cref="ModelMarkerNames"/>
/// names them ("mist", "mist 1"...); "" for the object's origin.</param>
/// <param name="Position">Where on the model, in model space at the default pose (world units).</param>
/// <param name="Rotation">The marker's orientation; its +X is the marker's forward.</param>
public sealed record EffectLocationSite( string Location, string ModelAttachment, Vector3 Position, Quaternion Rotation );

/// <summary>
/// Works out an object's attachments prefab (<c>&lt;object&gt;__attachments.prefab</c>): what Halo
/// creates for its attachments block and light volume widgets, where on the model each goes, the
/// object function that scales it and its state at rest, and the tags a game finds them by.
/// </summary>
/// <remarks>
/// <para>Halo follows the attachment's primary scale function (A-D out; none is 1) every frame:
/// a light's color goes from its lower to its upper color bound by the value and a dynamic light's
/// radius by its radius modifiers, and a black light is neither drawn nor flares
/// (<see cref="LightTag.ColorAt"/>); its lens flare interpolates each reflection's radius and
/// brightness by the value. Looping sounds play, and looping effects, particle systems, contrails
/// and light volumes run, only while the function is active: above its lower bound
/// (<see cref="ObjectFunctions.RestState"/>). The plasma pistol's charge glow is its plasma rifle
/// overcharge light at "secondary trigger flare1", scaled by D out (primary charged, 0 at rest).</para>
/// <para>A light sits at the first instance of its marker; its lens flare is drawn at every
/// instance (up to 8), so further instances get a lens flare child of their own. Contrails run
/// from every instance too. Projectiles are taken in flight rather than at rest: their functions
/// follow the projectile's own inputs (time, range), so everything counts as on, at the steady
/// value of a function that only runs on time and 1 otherwise.</para>
/// <para>Tags on every child: <c>halo_attachment</c>, the kind (<c>halo_light</c>,
/// <c>halo_lens_flare</c>, <c>halo_looping_sound</c>, <c>halo_effect</c>,
/// <c>halo_particle_system</c>, <c>halo_contrail</c>, <c>halo_light_volume</c>; a light with a flare
/// has both), <c>halo_attachment_&lt;index&gt;</c> (<c>halo_widget</c> for light volumes),
/// <c>halo_marker_&lt;marker&gt;</c> (<c>halo_marker_none</c> for the origin),
/// <c>halo_scale_&lt;a|b|c|d|none&gt;</c> for the primary scale, <c>halo_scale2_...</c> for the
/// secondary, and <c>halo_off_at_rest</c> when it starts off. Tags are lower case with spaces as
/// underscores and at most 32 characters (s&amp;box's limit), so a long marker name is cut short
/// there; the child's name and <see cref="AttachmentChild.ModelAttachment"/> have it in full.</para>
/// </remarks>
public static class ObjectAttachmentPlan
{
	/// <summary>
	/// Most lens flares Halo draws for one light (one per marker instance).
	/// </summary>
	public const int MaximumLensFlaresPerLight = 8;

	static readonly TagClass LoopingSoundClass = new( "lsnd" );
	static readonly TagClass ProjectileClass = new( "proj" );

	/// <summary>
	/// Whether an object has anything for an attachments prefab: attachments of a kind the prefab
	/// builds, or light volumes.
	/// </summary>
	public static bool HasAny( CacheFile cache, TagEntry objectTag ) =>
		ObjectAttachments.Read( cache, objectTag ).Any( a => KindOf( a.Type ) is not null )
		|| ObjectAttachments.ReadWidgets( cache, objectTag ).Any( w => w.Class == LightVolumeTag.Class );

	/// <summary>
	/// Whether an object's attachments look different in the player's own first person view: it
	/// carries particle systems with types drawn in only one view, or looping effects with particles
	/// (or such particle systems) for one camera.
	/// </summary>
	public static bool DiffersInFirstPerson( CacheFile cache, TagEntry objectTag )
	{
		static bool OneView( CacheFile cache, TagEntry system ) =>
			new ParticleSystemTag( cache, system ).Types.Any( t => (t.Flags & 0x30000) != 0 );

		foreach ( var attachment in ObjectAttachments.Read( cache, objectTag ) )
		{
			var kind = KindOf( attachment.Type );
			if ( kind == AttachmentKind.ParticleSystem && OneView( cache, attachment.Type ) )
				return true;

			if ( kind != AttachmentKind.Effect )
				continue;

			var effect = new EffectTag( cache, attachment.Type );
			if ( EffectPlan.HasCameraVariants( effect ) || effect.Events.Any( e => e.Parts.Any( p => p.Type is not null && p.Type.Class == EffectTagClasses.ParticleSystem && OneView( cache, p.Type ) ) ) )
				return true;
		}

		return false;
	}

	/// <summary>
	/// The prefab's children, in the order of the attachments block, then the light volumes.
	/// </summary>
	public static List<AttachmentChild> Plan( CacheFile cache, TagEntry objectTag )
	{
		var children = new List<AttachmentChild>();
		var modelTag = ObjectTags.GetObjectModel( cache, objectTag );
		var model = modelTag is not null && modelTag.Class == TagClasses.GbxModel ? new GbxModelTag( cache, modelTag ) : null;
		var sites = model is null ? new Dictionary<string, List<(string Name, ModelMarker Marker)>>() : Sites( cache, model, TakenNames( cache, model ) );
		var firstPerson = FirstPersonSites( cache, objectTag );

		var inFlight = objectTag.Class == ProjectileClass;
		var (values, active) = ObjectFunctions.RestState( cache, objectTag );
		var functions = ObjectFunctions.Read( cache, objectTag );
		float Rest( int scale )
		{
			if ( scale is < 1 or > 4 )
				return 1;
			if ( !inFlight )
				return values[scale - 1];
			return scale <= functions.Count && functions[scale - 1].IsFreeRunning ? values[scale - 1] : 1;
		}
		bool Active( int scale ) => scale is < 1 or > 4 || inFlight || active[scale - 1];

		var names = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
		void Add( AttachmentKind kind, TagEntry type, int index, string marker, int instance, TagEntry lensFlare,
			int primary, int secondary, int changeColor, float rest, bool on )
		{
			var placed = sites.TryGetValue( marker ?? "", out var list ) && instance < list.Count ? list[instance] : default;
			var fp = firstPerson.TryGetValue( marker ?? "", out var fpList ) && instance < fpList.Count ? fpList[instance] : "";

			var at = string.IsNullOrEmpty( placed.Name ) ? (string.IsNullOrEmpty( marker ) ? "origin" : marker) : placed.Name;
			var baseName = $"{at} / {System.IO.Path.GetFileName( type.Path )}.{type.Class.ToString().TrimEnd()}";
			var name = baseName;
			for ( var n = 2; !names.Add( name ); n++ )
				name = $"{baseName} {n}";

			var tags = new List<string> { "halo_attachment", KindTag( kind ) };
			if ( kind == AttachmentKind.Light && lensFlare is not null )
				tags.Add( "halo_lens_flare" );
			tags.Add( index >= 0 ? $"halo_attachment_{index}" : "halo_widget" );
			tags.Add( Tag( "halo_marker_", string.IsNullOrEmpty( marker ) ? "none" : marker ) );
			tags.Add( $"halo_scale_{ScaleName( primary )}" );
			tags.Add( $"halo_scale2_{ScaleName( secondary )}" );
			if ( !on )
				tags.Add( "halo_off_at_rest" );

			children.Add( new AttachmentChild( name, kind, type, index, marker ?? "", placed.Name ?? "", fp,
				placed.Marker?.Position ?? Vector3.Zero, placed.Marker?.Rotation ?? Quaternion.Identity, lensFlare,
				primary, secondary, changeColor, rest, on, tags,
				kind == AttachmentKind.Effect ? LocationSites( new EffectTag( cache, type ), sites ) : null ) );
		}

		var attachments = ObjectAttachments.Read( cache, objectTag );
		for ( var i = 0; i < attachments.Count; i++ )
		{
			var attachment = attachments[i];
			var kind = KindOf( attachment.Type );
			if ( kind is null )
				continue;

			var rest = Rest( attachment.PrimaryScale );
			var on = Active( attachment.PrimaryScale );
			var instances = Math.Max( sites.TryGetValue( attachment.Marker, out var list ) ? list.Count : 1, 1 );

			if ( kind == AttachmentKind.Light )
			{
				var light = new LightTag( cache, attachment.Type );
				var flare = DrawsLensFlare( cache, light.LensFlare ) ? light.LensFlare : null;
				var lit = light.ColorAt( rest ) != Vector3.Zero;
				if ( light.Radius > 0 )
					Add( AttachmentKind.Light, attachment.Type, i, attachment.Marker, 0, flare, attachment.PrimaryScale, 0, attachment.ChangeColor, rest, lit && light.RadiusAt( rest ) > 0 );
				else if ( flare is not null )
					Add( AttachmentKind.LensFlare, attachment.Type, i, attachment.Marker, 0, flare, attachment.PrimaryScale, 0, attachment.ChangeColor, rest, lit );

				for ( var n = 1; flare is not null && n < Math.Min( instances, MaximumLensFlaresPerLight ); n++ )
					Add( AttachmentKind.LensFlare, attachment.Type, i, attachment.Marker, n, flare, attachment.PrimaryScale, 0, attachment.ChangeColor, rest, lit );
				continue;
			}

			var secondary = kind == AttachmentKind.Effect ? attachment.SecondaryScale : 0;
			Add( kind.Value, attachment.Type, i, attachment.Marker, 0, null, attachment.PrimaryScale, secondary, attachment.ChangeColor, rest, on );
			for ( var n = 1; kind == AttachmentKind.Contrail && n < instances; n++ )
				Add( kind.Value, attachment.Type, i, attachment.Marker, n, null, attachment.PrimaryScale, 0, attachment.ChangeColor, rest, on );
		}

		foreach ( var widget in ObjectAttachments.ReadWidgets( cache, objectTag ).Where( w => w.Class == LightVolumeTag.Class && !string.IsNullOrEmpty( w.Path ) ) )
		{
			var source = new LightVolumeTag( cache, widget ).BrightnessScaleSource;
			Add( AttachmentKind.LightVolume, widget, -1, new LightVolumeTag( cache, widget ).Marker, 0, null, source is >= 1 and <= 4 ? source : 0, 0, 0, Rest( source ), Active( source ) );
		}

		return children;
	}

	/// <summary>
	/// Whether a lens flare draws anything: some (a10's fluorescent glows) have no bitmap or reflections.
	/// </summary>
	public static bool DrawsLensFlare( CacheFile cache, TagEntry lensFlare ) =>
		lensFlare is not null && lensFlare.Class == EffectTagClasses.LensFlare && new LensFlareTag( cache, lensFlare ) is { Bitmap: not null, Reflections.Count: > 0 };

	/// <summary>
	/// "a out".."d out", "none".
	/// </summary>
	public static string ScaleName( int scale ) => scale is >= 1 and <= 4 ? $"{(char)('a' + scale - 1)}" : "none";

	public static string KindTag( AttachmentKind kind ) => kind switch
	{
		AttachmentKind.Light => "halo_light",
		AttachmentKind.LensFlare => "halo_lens_flare",
		AttachmentKind.LoopingSound => "halo_looping_sound",
		AttachmentKind.Effect => "halo_effect",
		AttachmentKind.ParticleSystem => "halo_particle_system",
		AttachmentKind.Contrail => "halo_contrail",
		_ => "halo_light_volume",
	};

	static AttachmentKind? KindOf( TagEntry type )
	{
		if ( type is null || string.IsNullOrEmpty( type.Path ) )
			return null;
		if ( type.Class == EffectTagClasses.Light )
			return AttachmentKind.Light;
		if ( type.Class == LoopingSoundClass )
			return AttachmentKind.LoopingSound;
		if ( type.Class == EffectTagClasses.Effect )
			return AttachmentKind.Effect;
		if ( type.Class == EffectTagClasses.ParticleSystem )
			return AttachmentKind.ParticleSystem;
		if ( type.Class == EffectTagClasses.Contrail )
			return AttachmentKind.Contrail;
		return null;
	}

	/// <summary>
	/// A tag: the prefix and the name in lower case, anything but letters and digits as
	/// underscores, cut to s&amp;box's 32 characters.
	/// </summary>
	static string Tag( string prefix, string name )
	{
		var chars = name.Trim().ToLowerInvariant().Select( c => c is >= 'a' and <= 'z' or >= '0' and <= '9' ? c : '_' ).ToArray();
		var tag = prefix + new string( chars );
		return tag.Length > 32 ? tag[..32] : tag;
	}

	/// <summary>
	/// The names the model's bones take, which its attachments can't: the node names, for models
	/// with a skeleton (see <see cref="TagData.AnimationSkeleton.HasSkeleton"/>), as the mount builds them.
	/// </summary>
	public static IEnumerable<string> TakenNames( CacheFile cache, GbxModelTag model ) =>
		TagData.AnimationSkeleton.HasSkeleton( cache, model ) ? TagData.AnimationSkeleton.UniqueNames( model.Nodes.Select( n => n.Name ) ) : [];

	/// <summary>
	/// Each marker's places on the base permutations (its model attachment name and model space
	/// frame), in order: the instances Halo finds by the marker's name.
	/// </summary>
	static Dictionary<string, List<(string Name, ModelMarker Marker)>> Sites( CacheFile cache, GbxModelTag model, IEnumerable<string> taken )
	{
		// ReadMarkers lists every instance of every marker, in the same order as the model's markers.
		var frames = ObjectAttachments.ReadMarkers( cache, model.Tag );
		var frameOf = new Dictionary<(int Marker, int Instance), ModelMarker>();
		var k = 0;
		for ( var m = 0; m < model.Markers.Count; m++ )
		{
			for ( var i = 0; i < model.Markers[m].Instances.Count && k < frames.Count; i++ )
				frameOf[(m, i)] = frames[k++];
		}

		var named = ModelMarkerNames.Name( model, taken );
		var result = new Dictionary<string, List<(string, ModelMarker)>>( StringComparer.Ordinal );
		for ( var m = 0; m < model.Markers.Count; m++ )
		{
			var marker = model.Markers[m];
			var list = new List<(string, ModelMarker)>();
			// Instances only another permutation has are named "marker:permutation"; leave them out.
			foreach ( var (name, instance) in named )
			{
				var i = IndexOf( marker.Instances, instance );
				if ( i >= 0 && !name.Contains( ':' ) )
					list.Add( (name, frameOf.GetValueOrDefault( (m, i) )) );
			}

			if ( list.Count > 0 )
				result.TryAdd( marker.Name, list );
		}

		return result;
	}

	/// <summary>Most places one location of an effect goes (one per instance of its marker).</summary>
	public const int MaximumEffectInstances = 16;

	static List<EffectLocationSite> LocationSites( EffectTag effect, Dictionary<string, List<(string Name, ModelMarker Marker)>> sites )
	{
		var result = new List<EffectLocationSite>();
		foreach ( var location in effect.Locations.Distinct() )
		{
			if ( string.IsNullOrEmpty( location ) )
			{
				result.Add( new EffectLocationSite( "", "", Vector3.Zero, Quaternion.Identity ) );
				continue;
			}

			if ( !sites.TryGetValue( location, out var list ) )
				continue;

			foreach ( var (name, marker) in list.Take( MaximumEffectInstances ) )
				result.Add( new EffectLocationSite( location, name, marker?.Position ?? Vector3.Zero, marker?.Rotation ?? Quaternion.Identity ) );
		}

		return result;
	}

	static int IndexOf( IReadOnlyList<GbxMarkerInstance> instances, GbxMarkerInstance instance )
	{
		for ( var i = 0; i < instances.Count; i++ )
		{
			if ( instances[i].Equals( instance ) )
				return i;
		}

		return -1;
	}

	/// <summary>
	/// For a weapon, each marker's attachment names on its first person model (<c>_fp.vmdl</c>):
	/// the weapon's own markers come first there, named against the view model's bone names.
	/// Markers on nodes the view model's skeleton doesn't have aren't attachments.
	/// </summary>
	static Dictionary<string, List<string>> FirstPersonSites( CacheFile cache, TagEntry objectTag )
	{
		var result = new Dictionary<string, List<string>>( StringComparer.Ordinal );
		var weapon = FirstPersonWeapon.Find( cache, objectTag );
		var skeleton = weapon is null ? null : FirstPersonSkeleton.Create( cache, weapon );
		if ( skeleton is null )
			return result;

		var bones = TagData.AnimationSkeleton.UniqueNames( skeleton.GraphNodes.Select( n => skeleton.Graph.NodeNames[n] ) );
		var model = skeleton.WeaponModel;
		foreach ( var (name, instance) in ModelMarkerNames.Name( model, bones ) )
		{
			if ( instance.Node < 0 || instance.Node >= skeleton.WeaponBones.Count || skeleton.WeaponBones[instance.Node] < 0 )
				continue;

			var marker = model.Markers.FirstOrDefault( m => IndexOf( m.Instances, instance ) >= 0 );
			if ( marker is null || name.Contains( ':' ) )
				continue;

			if ( !result.TryGetValue( marker.Name, out var list ) )
				result[marker.Name] = list = new List<string>();
			list.Add( name );
		}

		return result;
	}
}
