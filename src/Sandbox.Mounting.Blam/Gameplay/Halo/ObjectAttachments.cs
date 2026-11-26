using System;

namespace Halo;

// An object's lights, flares, sounds, effects and particle systems from the mount's <object>__attachments.prefab.
// Each child rides its marker and is scaled by a function output A to D; at or below zero turns it off.
// Effects and particle systems are played by the mount's runtime: off stops them, and their loose particles live out.
// New particles inherit the holder's velocity; first person ones start where the view model's marker appears.
public sealed class ObjectAttachments {
	sealed class TagFile {
		public AttachmentsData AttachmentsPrefab { get; set; }
	}

	sealed class AttachmentsData {
		public string Prefab { get; set; }
		public List<ChildData> Children { get; set; } = new();
	}

	sealed class ChildData {
		public string Name { get; set; }
		public string Kind { get; set; }
		public string Marker { get; set; }
		public string ModelAttachment { get; set; }
		public string FirstPersonAttachment { get; set; }
		public string PrimaryScale { get; set; }
		public string SecondaryScale { get; set; }
		public float RestValue { get; set; }
		public bool OnAtRest { get; set; }
		// The object's change color ("a".."d") the light and its flare take, or "none".
		public string ChangeColor { get; set; }
		public LightData Light { get; set; }
	}

	sealed class LightData {
		public bool Dynamic { get; set; }
		public float[] ColorAtZero { get; set; }
		public float[] ColorAtOne { get; set; }
		// The color bounds' alphas: the share of the light's own color kept over the change color, when either is set.
		public float[] ColorAlpha { get; set; }
		public float RadiusAtZero { get; set; }
		public float RadiusAtOne { get; set; }

		// A spot light's gel bitmap, which the mount serves as a strip of 6 cube faces.
		public string Gel { get; set; }
	}

	sealed class Entry {
		public GameObject Object;
		public ChildData Data;
		public Light Light;
		public ModelRenderer Flare;
		public bool Toggles;

		// Never drawn, like a third person system on the first person weapon.
		public bool Hidden;

		// A looping effect's or particle system's "halo controls" child, whose local x is its scale A.
		public bool Plays;
		public GameObject Controls;

		// The marker it's placed at each frame instead of being parented to it.
		public GameObject Follow;
	}

	readonly List<Entry> _entries = new();
	readonly List<GameObject> _objects = new();
	bool _visible = true;
	Func<Transform, Transform> _placement;
	GameObject _carrier;
	GameObject _holder;

	// The mount gives particles this tagged object's velocity, so carried systems hang off one that follows the holder.
	const string CarrierTag = "halo_attachments";

	static readonly Dictionary<string, AttachmentsData> Cache = new();

	// s&box spot falloff is saturate(1 / (q d²) - 1 / (q r²)).
	// q = Fit / r² is the least squares fit to Halo's attenuation texture.
	// Halo's texture reads 0.98, 0.77, 0.48, 0.24 and 0.06 at 0.1, 0.3, 0.5, 0.7 and 0.9 of the radius.
	const float AttenuationFit = 7.82f;

	// An object tag's attachments on a renderer of its model, or null if the mount has none.
	// worldPlacement maps a view model transform to where the scene camera shows it, for first person particles.
	// holder is the unit carrying the object, whose velocity new particles take.
	// skipKind leaves off kinds the caller plays itself, and include picks which children to make.
	public static ObjectAttachments Create( string objectTag, ModelRenderer renderer, bool firstPerson, Func<Transform, Transform> worldPlacement = null, GameObject holder = null, Func<string, bool> skipKind = null, Func<GameObject, string, bool> include = null ) {
		if ( string.IsNullOrEmpty( objectTag ) || !renderer.IsValid() || !Combat.HaloEffects.PrefabsEnabled ) return null;

		var stem = Combat.HaloEffects.WithoutExtension( objectTag );
		var prefab = (firstPerson ? Combat.HaloEffects.Prefab( $"{stem}__attachments_fp" ) : null) ?? Combat.HaloEffects.Prefab( $"{stem}__attachments" );
		if ( prefab is null ) return null;

		var data = Load( objectTag );
		var root = GameObject.Clone( prefab, renderer.WorldTransform );
		if ( !root.IsValid() ) return null;
		root.MakeLocal();
		Combat.HaloSounds.Dry( root );
		root.SetParent( renderer.GameObject, true );

		var attachments = new ObjectAttachments { _placement = (firstPerson ? worldPlacement : null) ?? (t => t), _holder = holder ?? renderer.GameObject };
		attachments._objects.Add( root );
		renderer.CreateAttachments = true;

		foreach ( var child in root.Children.ToList() ) {
			var info = data?.Children.FirstOrDefault( x => x.Name == child.Name );
			var kind = info?.Kind;
			if ( (kind is not null && skipKind is not null && skipKind( kind )) || (include is not null && !include( child, kind )) ) {
				child.Enabled = false;
				continue;
			}
			var markerName = firstPerson && !string.IsNullOrEmpty( info?.FirstPersonAttachment ) ? info.FirstPersonAttachment : info?.ModelAttachment;
			var marker = string.IsNullOrEmpty( markerName ) ? null : renderer.GetAttachmentObject( markerName );
			var particles = kind == "particle_system";
			var looping = kind == "effect";
			// Halo makes no particles at a marker the model doesn't have.
			var hidden = firstPerson
				? (kind is "particle_system" or "effect") && string.IsNullOrEmpty( info?.FirstPersonAttachment )
				: particles && !string.IsNullOrEmpty( info?.Marker ) && string.IsNullOrEmpty( info.ModelAttachment );
			GameObject follow = null;

			if ( hidden ) {
				child.Enabled = false;
			}
			else if ( particles && marker.IsValid() ) {
				// On the carrier, placed at the marker, and in first person out of the view model so the world camera draws it.
				child.SetParent( attachments.Carrier( renderer.Scene ), false );
				child.WorldTransform = attachments._placement( marker.WorldTransform );
				follow = marker;
			}
			else if ( marker.IsValid() ) {
				// Parented to the marker, so it follows the animated weapon.
				child.SetParent( marker, false );
				child.LocalTransform = Transform.Zero;
				attachments._objects.Add( child );
			}

			// A looping effect is handled through its emitters and parts, not as one light or flare.
			var light = looping ? null : child.GetComponent<Light>( true );
			var flare = looping ? null : child.GetComponent<ModelRenderer>( true );
			if ( light is not null && flare is not null ) light = SplitLight( child, light );
			if ( light is SpotLight spot && GelCookie( info?.Light?.Gel ) is { } gel ) {
				spot.Cookie = gel.Cookie;
				spot.ConeOuter = gel.ConeOuter;
				spot.ConeInner = 0;
			}
			// On the first person weapon the gun hides the flare per pixel, like Halo's stencil.
			if ( firstPerson && flare is not null ) flare.Attributes.Set( "HaloFirstPerson", 1f );
			attachments._entries.Add( new Entry {
				Object = child,
				Data = info,
				Light = light,
				Flare = flare,
				Toggles = light is null && flare is null,
				Hidden = hidden,
				Plays = particles || looping,
				Controls = particles || looping ? child.Children.FirstOrDefault( x => x.Name == "halo controls" ) : null,
				Follow = follow,
			} );
		}

		// Objects made during fixed update would otherwise ease in from where the prefab had them.
		root.Transform.ClearInterpolation();
		foreach ( var entry in attachments._entries ) {
			entry.Object.Transform.ClearInterpolation();
			entry.Light?.GameObject.Transform.ClearInterpolation();
		}

		return attachments;
	}

	// Moves a flare's light onto its own object so the two can be placed apart.
	// The first person flashlight shines from the weapon while its flare stays on the unit's marker.
	static Light SplitLight( GameObject child, Light light ) {
		var holder = child.Clone( child.WorldTransform, child, true, "light" );
		var copy = holder.GetComponent<Light>( true );
		foreach ( var component in holder.Components.GetAll().ToList() )
			if ( component != copy ) component.Destroy();
		foreach ( var nested in holder.Children.ToList() )
			nested.Destroy();
		light.Destroy();
		return copy;
	}

	GameObject Carrier( Scene scene ) {
		if ( _carrier.IsValid() ) return _carrier;
		_carrier = scene.CreateLocalObject( "carried attachments" );
		_carrier.Tags.Add( CarrierTag );
		_carrier.WorldPosition = _holder.IsValid() ? _holder.WorldPosition : Vector3.Zero;
		_objects.Add( _carrier );
		return _carrier;
	}

	static AttachmentsData Load( string objectTag ) {
		var key = objectTag.Replace( '\\', '/' );
		if ( Cache.TryGetValue( key, out var cached ) ) return cached;

		AttachmentsData data = null;
		try {
			var json = Data.HaloDataStore.ReadMount( $"tags/{key}.json" );
			if ( !string.IsNullOrEmpty( json ) )
				data = Json.Deserialize<TagFile>( json )?.AttachmentsPrefab;
		}
		catch ( Exception e ) {
			Log.Warning( $"Couldn't read the attachments of {key}: {e.Message}" );
		}

		Cache[key] = data;
		return data;
	}

	// The attachments' own lights, excluding looping effects', for callers that tune them further.
	public IEnumerable<Light> Lights => _entries.Where( e => e.Light.IsValid() ).Select( e => e.Light );

	// Hides them all, as for a third person weapon seen from first person.
	public void SetVisible( bool visible ) {
		if ( visible == _visible ) return;
		_visible = visible;
		foreach ( var e in _entries )
			if ( e.Object.IsValid() && !visible ) e.Object.Enabled = false;
	}

	// Geometry this close in front of a flare's occlusion point, in inches, doesn't hide it.
	// 0 is Halo's test, where the object carrying the flare hides it too.
	public void SetOccluderMargin( float margin ) {
		foreach ( var e in _entries )
			if ( e.Flare.IsValid() ) e.Flare.Attributes.Set( "HaloOccluderMargin", margin );
	}

	// Scales each child by its primary scale's function output; a null output means the rest value.
	// emission scales effects and particle systems, standing in for the share of ticks the function is on in Halo.
	// active says which outputs are on for effects and particles; lights follow the value, and null means on above 0.
	// changeColors are the object's A to D, which a light and its flare take when their attachment names one.
	public void Update( float?[] outputs, float emission = 1, bool[] active = null, Color[] changeColors = null ) {
		if ( _carrier.IsValid() && _holder.IsValid() )
			_carrier.WorldPosition = _holder.WorldPosition;

		foreach ( var e in _entries ) {
			if ( !e.Object.IsValid() ) continue;
			if ( !_visible || e.Hidden ) { e.Object.Enabled = false; continue; }

			if ( e.Follow is not null ) {
				if ( !e.Follow.IsValid() || !e.Follow.Active ) { e.Object.Enabled = false; continue; }
				e.Object.WorldTransform = _placement( e.Follow.WorldTransform );
			}

			var value = Value( e.Data?.PrimaryScale, outputs, e.Data );
			var on = value > 0;
			if ( active is not null && e.Data?.PrimaryScale is { Length: 1 } scale && scale[0] is >= 'a' and <= 'd' && scale[0] - 'a' < active.Length )
				on = active[scale[0] - 'a'];

			if ( e.Plays ) {
				if ( e.Controls.IsValid() ) e.Controls.LocalPosition = e.Controls.LocalPosition.WithX( Math.Clamp( value * emission, 0, 1 ) );
				if ( e.Object.Enabled != on ) e.Object.Enabled = on;
				continue;
			}

			// A light or flare follows the value, not the function's activity.
			if ( e.Light is not null || e.Flare is not null ) on = value > 0;

			if ( e.Data?.Light is { } l && (e.Light is not null || e.Flare is not null) ) {
				var change = ChangeColor( e.Data.ChangeColor, changeColors );
				var color = LightColor( l, value, change );
				var lit = (color.r + color.g + color.b) > 0.0001f;
				if ( e.Light is not null ) {
					e.Light.LightColor = color;
					var radius = l.RadiusAtZero.LerpTo( l.RadiusAtOne, value );
					if ( e.Light is PointLight point ) point.Radius = radius;
					if ( e.Light is SpotLight spot && radius > 0 ) {
						spot.Radius = radius;
						spot.Attenuation = AttenuationFit * 10000f / (radius * radius);
					}
					e.Light.Enabled = on && l.Dynamic && lit;
				}
				if ( change is not null && e.Flare is not null )
					e.Flare.Attributes.Set( "HaloLightColor", new Vector3( color.r, color.g, color.b ) / MathF.PI );
				on = on && lit;
			}

			if ( e.Flare is not null ) {
				e.Flare.Attributes.Set( "HaloFunctionScale", value );
				e.Flare.Enabled = on;
			}

			if ( e.Toggles || e.Flare is not null || e.Light is not null )
				e.Object.Enabled = on || (e.Light is not null && e.Light.Enabled);
		}
	}

	// For when the object is gone: effects and particle systems stop and their particles live out; the next Update restarts them.
	public void StopParticles() {
		foreach ( var e in _entries )
			if ( e.Plays && e.Object.IsValid() ) e.Object.Enabled = false;
	}

	static float Value( string scale, float?[] outputs, ChildData data ) {
		if ( scale is { Length: 1 } && scale[0] is >= 'a' and <= 'd' )
			return outputs[scale[0] - 'a'] ?? data?.RestValue ?? 1;
		return data is null ? 1 : data.OnAtRest || scale is null or "none" ? MathF.Max( data.RestValue, data.OnAtRest ? 1 : 0 ) : data.RestValue;
	}

	static Color Lerp( float[] a, float[] b, float t ) {
		var from = a is { Length: >= 3 } ? new Color( a[0], a[1], a[2] ) : Color.Black;
		var to = b is { Length: >= 3 } ? new Color( b[0], b[1], b[2] ) : from;
		return Color.Lerp( from, to, t );
	}

	static Color? ChangeColor( string reference, Color[] changeColors ) {
		if ( changeColors is null || reference is not { Length: 1 } || reference[0] is < 'a' or > 'd' || reference[0] - 'a' >= changeColors.Length ) return null;
		return changeColors[reference[0] - 'a'];
	}

	// Halo's light color: the bounds by the value, times the change color.
	// With a bound alpha set, the change color instead fills the share the alphas leave, which the mount's colors hold as white.
	static Color LightColor( LightData l, float value, Color? change ) {
		var color = Lerp( l.ColorAtZero, l.ColorAtOne, value );
		if ( change is not { } c ) return color;
		if ( l.ColorAlpha is { Length: 2 } alpha && (alpha[0] > 1e-6f || alpha[1] > 1e-6f) ) {
			var share = MathF.PI * (1 - alpha[0].LerpTo( alpha[1], value ));
			color = new Color( color.r + (c.r - 1) * share, color.g + (c.g - 1) * share, color.b + (c.b - 1) * share );
		}
		else {
			color = new Color( color.r * c.r, color.g * c.g, color.b * c.b );
		}
		return new Color( Math.Clamp( color.r, 0, MathF.PI ), Math.Clamp( color.g, 0, MathF.PI ), Math.Clamp( color.b, 0, MathF.PI ) );
	}

	// A gel as a spot light cookie, and the cone half angle in degrees it fills.
	readonly record struct Gel( Texture Cookie, float ConeOuter );

	static readonly Dictionary<string, Gel?> Gels = new();

	// Gel texels no brighter than this are dark, and the cone ends past the last brighter one.
	const byte GelDark = 2;

	// Degrees the cone reaches past the gel's last lit texel.
	const float GelConeMargin = 5;

	// Halo lights by gel times distance attenuation with no cone, and a gel can reach the side faces (flashlight: 59°).
	// The cookie is the gel through a slightly wider cone, divided by s&box's cone falloff so the light is the gel's.
	// The strip holds faces +X, -X, +Y, -Y, +Z, -Z with Direct3D axes, in a light frame of x forward, y left, z up.
	static Gel? GelCookie( string bitmap ) {
		if ( string.IsNullOrEmpty( bitmap ) ) return null;
		if ( Gels.TryGetValue( bitmap, out var cached ) ) return cached;

		Gel? gel = null;
		try {
			var strip = Texture.Load( bitmap, false );
			if ( strip is not null && !strip.IsError && strip.Height > 0 && strip.Width >= strip.Height * 6 ) {
				var pixels = strip.GetPixels( 0 );
				if ( pixels is not null && pixels.Length >= strip.Width * strip.Height )
					gel = BuildCookie( pixels, strip.Width, strip.Height );
			}
		}
		catch ( Exception e ) {
			Log.Warning( $"Couldn't build the gel cookie from {bitmap}: {e.Message}" );
		}

		Gels[bitmap] = gel;
		return gel;
	}

	static Gel BuildCookie( Color32[] strip, int width, int n ) {
		var extent = 0f;
		for ( var face = 0; face < 6; face++ ) {
			for ( var t = 0; t < n; t++ ) {
				for ( var s = 0; s < n; s++ ) {
					var p = strip[t * width + face * n + s];
					if ( Math.Max( p.r, Math.Max( p.g, p.b ) ) <= GelDark ) continue;
					var d = CubeDirection( face, (s + 0.5f) / n * 2 - 1, (t + 0.5f) / n * 2 - 1 );
					extent = Math.Max( extent, MathF.Acos( d.x / d.Length ) );
				}
			}
		}

		var outer = Math.Clamp( extent.RadianToDegree() + GelConeMargin, 1, 85 );
		var tanOuter = MathF.Tan( outer.DegreeToRadian() );
		var cosOuter = MathF.Cos( outer.DegreeToRadian() );

		// Past the lit part the dark texels aren't quite black, so the divisor is floored to avoid a bright edge ring.
		var leastCone = Math.Max( (MathF.Cos( extent ) - cosOuter) / (1 - cosOuter), 0.01f );

		var size = n * 2;
		var data = new Half[size * size * 4];
		for ( var row = 0; row < size; row++ ) {
			for ( var col = 0; col < size; col++ ) {
				var left = ((col + 0.5f) / size * 2 - 1) * tanOuter;
				var up = -((row + 0.5f) / size * 2 - 1) * tanOuter;
				var cosine = 1 / MathF.Sqrt( 1 + left * left + up * up );
				var cone = (cosine - cosOuter) / (1 - cosOuter);
				var colour = cone > 0 ? SampleCube( strip, width, n, new Vector3( 1, left, up ) ) / Math.Max( cone, leastCone ) : Vector3.Zero;

				var i = (row * size + col) * 4;
				data[i] = (Half)colour.x;
				data[i + 1] = (Half)colour.y;
				data[i + 2] = (Half)colour.z;
				data[i + 3] = (Half)1;
			}
		}

		var cookie = Texture.Create( size, size, ImageFormat.RGBA16161616F ).WithData<Half>( data ).Finish();
		return new Gel( cookie, outer );
	}

	// The direction through a cube face at (s, t), each -1 to 1.
	static Vector3 CubeDirection( int face, float s, float t ) => face switch {
		0 => new Vector3( 1, -t, -s ),
		1 => new Vector3( -1, -t, s ),
		2 => new Vector3( s, 1, t ),
		3 => new Vector3( s, -1, -t ),
		4 => new Vector3( s, -t, 1 ),
		_ => new Vector3( -s, -t, -1 ),
	};

	// The cube map's colour along a direction, bilinear within its face.
	static Vector3 SampleCube( Color32[] strip, int width, int n, Vector3 d ) {
		var a = d.Abs();
		int face;
		float s, t, major;
		if ( a.x >= a.y && a.x >= a.z ) { major = a.x; face = d.x > 0 ? 0 : 1; s = d.x > 0 ? -d.z : d.z; t = -d.y; }
		else if ( a.y >= a.z ) { major = a.y; face = d.y > 0 ? 2 : 3; s = d.x; t = d.y > 0 ? d.z : -d.z; }
		else { major = a.z; face = d.z > 0 ? 4 : 5; s = d.z > 0 ? d.x : -d.x; t = -d.y; }

		var x = Math.Clamp( (s / major + 1) * 0.5f * n - 0.5f, 0, n - 1 );
		var y = Math.Clamp( (t / major + 1) * 0.5f * n - 0.5f, 0, n - 1 );
		var x0 = (int)x;
		var y0 = (int)y;
		var x1 = Math.Min( x0 + 1, n - 1 );
		var y1 = Math.Min( y0 + 1, n - 1 );
		var fx = x - x0;
		var fy = y - y0;

		Vector3 Texel( int tx, int ty ) {
			var p = strip[ty * width + face * n + tx];
			return new Vector3( p.r, p.g, p.b ) / 255f;
		}

		var top = Vector3.Lerp( Texel( x0, y0 ), Texel( x1, y0 ), fx );
		var bottom = Vector3.Lerp( Texel( x0, y1 ), Texel( x1, y1 ), fx );
		return Vector3.Lerp( top, bottom, fy );
	}

	public void Destroy() {
		foreach ( var go in _objects )
			if ( go.IsValid() ) go.Destroy();
		_objects.Clear();
		_entries.Clear();
	}
}
