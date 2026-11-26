using Blam.Halo1;
using Blam.Halo1.Tags;

namespace Sandbox.Mounting.Blam.Halo1;

/// <summary>
/// Turns the dynamic lights attached to placed objects (light fixtures, glowing panels, fires,
/// beacons) into s&amp;box point and spot lights.
/// </summary>
/// <remarks>
/// Halo makes a light as the game runs only for a light tag flagged dynamic; the others were baked
/// into the lightmaps, and objects take them from the lightmap below them like the rest of the
/// level's light. A dynamic light lights both nearby objects and the level. s&amp;box lights only
/// reach the level through halo1_environment.shader, which adds the lights marked here by their
/// transmissive contribution and ignores the sun and any other light.
///
/// Levels can still place more of these than s&amp;box's clustered lighting holds without lights
/// popping as the camera turns, so <see cref="ObjectLightPlan"/> merges and thins them first. None
/// cast shadows: Halo's object lights never did.
/// </remarks>
static class ObjectLights
{
	/// <summary>
	/// s&amp;box divides diffuse light by pi and Halo didn't, as with the sun.
	/// </summary>
	const float Brightness = MathF.PI;

	public static int Build( CacheFile cache, IEnumerable<AttachmentSite> sites )
	{
		var parent = new GameObject( true, "object lights" );
		var lightTags = new Dictionary<uint, LightTag>();
		var sources = new List<LightSource>();

		foreach ( var site in sites )
		{
			var tag = site.Attachment.Type;
			if ( tag.Class != EffectTagClasses.Light )
				continue;

			if ( !lightTags.TryGetValue( tag.Id, out var light ) )
				lightTags[tag.Id] = light = new LightTag( cache, tag );

			var position = site.Position / HaloCeMount.WorldScale;
			sources.Add( new LightSource( tag, light, new System.Numerics.Vector3( position.x, position.y, position.z ), new System.Numerics.Vector3( site.Forward.x, site.Forward.y, site.Forward.z ) ) );
		}

		var planned = ObjectLightPlan.Plan( sources );
		foreach ( var planLight in planned )
		{
			var light = planLight.Light;
			var color = (Vector3)planLight.Color * Brightness;

			var go = new GameObject( parent, true, System.IO.Path.GetFileName( planLight.Tag.Path ) );
			go.WorldPosition = (Vector3)planLight.Position * HaloCeMount.WorldScale;
			go.WorldRotation = Rotation.LookAt( (Vector3)planLight.Forward );

			var radius = planLight.Radius * HaloCeMount.WorldScale;
			Light component;
			if ( light.IsSpot )
			{
				var spot = go.AddComponent<SpotLight>();
				spot.Radius = radius;
				spot.ConeOuter = Math.Clamp( light.SpotCutoffAngle.RadianToDegree(), 1, 89 );
				spot.ConeInner = Math.Clamp( light.FalloffAngle.RadianToDegree(), 0, spot.ConeOuter );
				component = spot;
			}
			else
			{
				var point = go.AddComponent<PointLight>();
				point.Radius = radius;
				component = point;
			}

			component.LightColor = new Color( color.x, color.y, color.z );
			component.Shadows = false;
			component.FogMode = Light.FogInfluence.Disabled;

			// The transmissive contribution marks it as a light of the level's.
			component.Contribution = Light.LightContribution.Diffuse | Light.LightContribution.Specular | Light.LightContribution.Transmissive;
			if ( light.Flags.HasFlag( LightFlags.NoSpecular ) )
				component.Contribution &= ~Light.LightContribution.Specular;

			go.Tags.Add( "halo_dynamic_light" );
		}

		return planned.Count;
	}
}
