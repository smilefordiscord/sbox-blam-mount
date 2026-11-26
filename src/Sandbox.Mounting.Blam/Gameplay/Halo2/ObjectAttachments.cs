using System;
using Halo2.Data;

namespace Halo2.Combat;

// What an object carries, from the mount's <object>__attachments.prefab: looping effects, flares, volumes and sounds.
// Each is under a child named after its marker instance and placed at the marker's default pose.
// Those children ride the renderer's markers of the same name, so they follow the animated model.
// Its tag's lights are added at their markers, as the mount's prefab has only their lens flares and light volumes.
// Parts an object function scales are tagged halo_function_<function>; Update shows them while it's on.
// It also scales their lens flares by it.
public sealed class ObjectAttachments {
	readonly List<GameObject> _sites = new();
	readonly List<(GameObject Part, string Function)> _scaled = new();
	bool _visible = true;
	ObjectFunctions _functions;

	const string FunctionTagPrefix = "halo_function_";

	// An object tag's attachments on a renderer of its model, or null if it has none.
	// firstPerson picks the first person view, where the mount made one.
	public static ObjectAttachments Create( string objectTag, ModelRenderer renderer, bool firstPerson ) {
		if ( string.IsNullOrEmpty( objectTag ) || !renderer.IsValid() ) return null;
		renderer.CreateAttachments = true;
		var attachments = new ObjectAttachments();

		var stem = Combat.HaloEffects.WithoutExtension( objectTag );
		var prefab = (firstPerson ? Combat.HaloEffects.Prefab( $"{stem}__attachments_fp" ) : null) ?? Combat.HaloEffects.Prefab( $"{stem}__attachments" );
		var root = prefab is null ? null : GameObject.Clone( prefab, renderer.WorldTransform );
		if ( root.IsValid() ) {
			root.NetworkMode = NetworkMode.Never;
			root.Tags.Add( LocalObjects.Tag );
			Combat.HaloSounds.Dry( root );
			Combat.ContrailAttachments.Strip( root );
			root.SetParent( renderer.GameObject, true );
			attachments._sites.Add( root );
			attachments.CollectScaled( root );
			foreach ( var site in root.Children.ToList() ) {
				if ( site.Tags.Has( "halo_effect" ) ) continue;
				var marker = renderer.GetAttachmentObject( site.Name );
				if ( !marker.IsValid() ) continue;

				site.SetParent( marker, false );
				site.LocalTransform = global::Transform.Zero;
				attachments._sites.Add( site );
			}
		}

		attachments.AddLights( objectTag, name => string.IsNullOrEmpty( name ) ? renderer.GameObject : renderer.GetAttachmentObject( name ), firstPerson );
		if ( attachments._sites.Count == 0 ) return null;

		// Objects made during fixed update would otherwise ease in from where the prefab had them.
		foreach ( var site in attachments._sites )
			site.Transform.ClearInterpolation();
		return attachments;
	}

	// A spawned prefab that is an object with its attachments already at its markers, like a projectile's.
	// lights adds the tag's lights, which a prefab taken over from another object already has.
	public static ObjectAttachments ForPrefab( GameObject root, string objectTag, bool lights ) {
		var attachments = new ObjectAttachments();
		attachments.CollectScaled( root );
		if ( lights ) attachments.AddLights( objectTag, name => string.IsNullOrEmpty( name ) ? root : root.Children.FirstOrDefault( x => x.Name == name ), firstPerson: false );
		return attachments;
	}

	// Tags carry down to children, so a part is the topmost object with its tag, enabled or not.
	void CollectScaled( GameObject root ) {
		foreach ( var part in root.GetAllObjects( false ) ) {
			var function = part.Tags.FirstOrDefault( x => x.StartsWith( FunctionTagPrefix ) );
			if ( function is not null && !(part.Parent?.Tags.Has( function ) ?? false) ) _scaled.Add( (part, function[FunctionTagPrefix.Length..]) );
		}
	}

	// The tag's lights at their markers, scaled by the object function each attachment names.
	void AddLights( string objectTag, Func<string, GameObject> marker, bool firstPerson ) {
		foreach ( var a in AttachmentsOf( objectTag ).Where( x => x.Type?.EndsWith( ".ligh" ) ?? false ) ) {
			var at = marker( a.Marker );
			if ( !at.IsValid() ) continue;
			var function = a.PrimaryScale;
			var light = Halo2Light.Attach( at, a.Type, () => _functions?.Value( function ) ?? (string.IsNullOrEmpty( function ) ? 1 : 0), firstPerson );
			if ( light is not null ) _sites.Add( light.GameObject );
		}
	}

	// The weapon, projectile or equipment tag's attachments.
	static IEnumerable<ObjectAttachmentTag> AttachmentsOf( string objectTag ) {
		var game = HaloDataStore.Game;
		return game?.Weapon( objectTag )?.Attachments ?? game?.Projectile( objectTag )?.Attachments ?? Enumerable.Empty<ObjectAttachmentTag>();
	}

	// Shows the parts whose object function is on and scales the lens flares by it; the lights read it themselves.
	// Without functions a part keeps how it was made: on unless the mount marked it off at rest.
	public void Update( ObjectFunctions functions ) {
		_functions = functions;
		if ( functions is null || !_visible ) return;
		foreach ( var (part, function) in _scaled ) {
			if ( !part.IsValid() ) continue;
			var (value, on) = functions.Get( function );
			part.Enabled = on;
			foreach ( var flare in part.GetComponents<ModelRenderer>() )
				flare.Attributes.Set( "HaloFunctionScale", value );
		}
	}

	// Hides them all, as for a third person weapon seen from first person.
	public void SetVisible( bool visible ) {
		if ( visible == _visible ) return;
		_visible = visible;
		foreach ( var site in _sites )
			if ( site.IsValid() ) site.Enabled = visible;
	}

	// The lights go at the end of the frame, after their object may be gone, so they stop reading its functions now.
	public void Destroy() {
		_functions = null;
		foreach ( var site in _sites )
			if ( site.IsValid() ) site.Destroy();
		_sites.Clear();
		_scaled.Clear();
	}
}
