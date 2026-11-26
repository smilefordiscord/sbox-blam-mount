using System;
using Halo2.Data;

namespace Halo2.Combat;

// A Halo 2 dynamic light from the mount's tags/<light>.ligh.json, as a point or spot light, recomputed every frame.
// Its scale s comes from the object function its attachment names, or for an effect's light from its age.
// Colour and brightness blend from their lower to upper bounds by s, and the size modifier scales its reach.
// It's off without colour or size. Past its fade distance from the camera it fades out over 10 world units.
public sealed class Halo2Light : Component {
	// The ligh document's fields this reads; colours are RGB 0-1, distances world units.
	sealed class LightTag {
		public string[] Flags { get; set; } = Array.Empty<string>();
		// "sphere", "orthogonal", "projective" or "pyramid".
		public string Type { get; set; }
		public float[] SizeModifier { get; set; }
		public float Radius { get; set; }
		public float SpecularRadius { get; set; }
		// Degrees.
		public float FieldOfView { get; set; }
		public float CutoffDistance { get; set; }
		public float[] DiffuseLowerBound { get; set; }
		public float[] DiffuseUpperBound { get; set; }
		public float[] BrightnessBounds { get; set; }
		// Seconds an effect's light lasts, fading by its effect falloff function.
		public float Duration { get; set; }
		public string EffectFalloffFunction { get; set; }
		public string IlluminationFade { get; set; }
		public string GelMap { get; set; }

		public bool HasFlag( string flag ) => Flags?.Contains( flag ) ?? false;
	}

	static readonly string[] FadeNames = ["fade very far", "fade far", "fade medium", "fade close", "fade very close"];
	static readonly float[] FadeDistances = [20, 15, 10, 5, 0];

	// Halo 2 doubles a light's brightness into its shader and its light shader is taken to halve it again.
	// Kept at that level, the lights stay as subtle as the Halo look asks.
	const float Brightness = 1;

	LightTag _tag;
	Light _light;
	Func<float> _scale;
	float _partScale;
	RealTimeSince _age;

	static readonly Dictionary<string, LightTag> LightTags = new( StringComparer.OrdinalIgnoreCase );
	static int _tagsGeneration;

	static LightTag TagOf( string path ) {
		if ( _tagsGeneration != HaloDataStore.Generation ) {
			_tagsGeneration = HaloDataStore.Generation;
			LightTags.Clear();
		}
		if ( LightTags.TryGetValue( path, out var cached ) ) return cached;
		var tag = HaloDataStore.Tag<LightTag>( path );
		LightTags[path] = tag;
		return tag;
	}

	// An object's light at its attachment, scaled by the object function its attachment names.
	// firstPerson is the view the light is made for; a light only for the other view isn't made.
	public static Halo2Light Attach( GameObject parent, string lightTag, Func<float> scale, bool firstPerson ) {
		if ( TagOf( lightTag ) is not { } tag || !Shows( tag, firstPerson ) ) return null;
		var light = Make( parent, tag );
		light._scale = scale;
		return light;
	}

	// An effect's light, which goes out over its duration and is then removed, or stays at full scale without one.
	public static Halo2Light Effect( GameObject parent, string lightTag, bool firstPerson ) {
		if ( TagOf( lightTag ) is not { } tag || !Shows( tag, firstPerson ) ) return null;
		var light = Make( parent, tag );
		light._partScale = 1;
		return light;
	}

	static bool Shows( LightTag tag, bool firstPerson ) =>
		!tag.HasFlag( "no illumination" ) && !tag.HasFlag( firstPerson ? "only render in third person" : "only render in first person" );

	static Halo2Light Make( GameObject parent, LightTag tag ) {
		var go = new GameObject( parent, true, "halo light" );
		go.NetworkMode = NetworkMode.Never;
		var light = go.AddComponent<Halo2Light>();
		light._tag = tag;
		light._age = 0;
		if ( tag.Type == "sphere" ) light._light = go.AddComponent<PointLight>();
		else {
			var spot = go.AddComponent<SpotLight>();
			spot.ConeOuter = Math.Clamp( tag.Type == "orthogonal" ? 1 : tag.FieldOfView * 0.5f, 1, 89 );
			spot.ConeInner = 0;
			// The mount's Halo 2 shaders multiply the light by its gel.
			if ( tag.GelMap is { Length: > 0 } gel ) spot.Cookie = Texture.Load( HaloResources.TexturePath( gel ), false );
			light._light = spot;
		}
		light._light.Shadows = false;
		light._light.FogMode = Light.FogInfluence.Disabled;
		light._light.Contribution = tag.HasFlag( "no specular" ) ? Light.LightContribution.Diffuse : Light.LightContribution.Diffuse | Light.LightContribution.Specular;
		light.Apply();
		return light;
	}

	protected override void OnUpdate() => Apply();

	void Apply() {
		if ( _tag is null || !_light.IsValid() ) return;

		var s = _partScale;
		if ( _scale is not null ) s = _scale();
		else if ( _tag.Duration > 0 ) {
			if ( _age > _tag.Duration ) {
				GameObject.Destroy();
				return;
			}
			s = _partScale * (1 - HaloFunctions.Fade( _tag.EffectFalloffFunction, _age / _tag.Duration ));
		}
		s = Math.Clamp( s, 0, 1 );

		var size = Lerp( _tag.SizeModifier, s, 1 );
		var color = new Vector3( Channel( 0, s ), Channel( 1, s ), Channel( 2, s ) ) * Math.Clamp( Lerp( _tag.BrightnessBounds, s, 1 ), 0, 2 );
		// A sphere light's falloff reaches the larger of its two radii.
		var reach = _tag.Type == "sphere" ? MathF.Max( _tag.Radius, _tag.SpecularRadius ) : _tag.CutoffDistance;
		color *= ViewFade( reach * size ) * Brightness;

		var on = size > 0 && reach > 0 && color.LengthSquared > 0;
		_light.Enabled = on;
		if ( !on ) return;
		// The mount's shaders divide by pi, as s&box's own do.
		color *= MathF.PI;
		_light.LightColor = new Color( color.x, color.y, color.z );
		if ( _light is PointLight point ) point.Radius = HaloUnits.ToInches( reach * size );
		else if ( _light is SpotLight spot ) spot.Radius = HaloUnits.ToInches( reach * size );
	}

	float Channel( int i, float s ) {
		var lower = _tag.DiffuseLowerBound?.ElementAtOrDefault( i ) ?? 0;
		var upper = _tag.DiffuseUpperBound?.ElementAtOrDefault( i ) ?? 0;
		return Math.Clamp( lower + (upper - lower) * s, 0, 1 );
	}

	// Out past the light's fade distance from the camera, over 10 world units, measured from its reach's edge.
	float ViewFade( float reach ) {
		var camera = Scene.Camera;
		if ( !camera.IsValid() ) return 1;
		var index = Array.IndexOf( FadeNames, _tag.IlluminationFade );
		var start = index >= 0 ? FadeDistances[index] : 0;
		var distance = HaloUnits.ToWorldUnits( camera.WorldPosition.Distance( WorldPosition ) ) - reach;
		return 1 - Math.Clamp( (distance - start) / 10, 0, 1 );
	}

	static float Lerp( float[] bounds, float t, float fallback ) => bounds is { Length: > 1 } b ? b[0] + (b[1] - b[0]) * t : fallback;
}
