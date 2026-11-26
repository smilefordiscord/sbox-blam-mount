using Blam.Halo1;
using Blam.Halo1.Tags;

namespace Sandbox.Mounting.Blam.Halo1;

// Effect lights: Halo's unattached lights, fading out over their duration.
sealed partial class HaloParticleWorld
{
	sealed class LightInstance
	{
		public HaloEffectLight Definition;

		// The location it sits at, followed while it's there.
		public GameObject Follow;
		public GameObject Object;
		public Light Light;
		public ModelRenderer Flare;
		public float Age;
		public float Duration;
		public float Scale;

		public void Destroy()
		{
			if ( Object.IsValid() )
				Object.Destroy();
		}
	}

	readonly List<LightInstance> _lights = new();

	// s&box divides diffuse light by pi and Halo didn't (as for object lights).
	const float LightBrightness = MathF.PI;

	// An effect's light and lens flare, fading by its falloff function over its duration.
	void StartLight( HaloEffectLight definition, GameObject location, float scale )
	{
		var light = definition.Light;
		var flare = definition.Flare is { IsError: false } model ? model : null;
		var dynamic = light.IsDynamic && light.Radius > 0;
		if ( !dynamic && flare is null )
			return;

		var instance = new LightInstance
		{
			Definition = definition,
			Follow = location,
			Duration = MathF.Max( light.EffectSeconds, Tick ),
			Scale = scale,
		};

		using ( Scene.Push() )
		{
			var go = new GameObject( true, System.IO.Path.GetFileName( light.LensFlare?.Path ?? "effect light" ) );
			go.Flags |= GameObjectFlags.Hidden | GameObjectFlags.NotSaved | GameObjectFlags.NotNetworked;
			go.WorldTransform = location.WorldTransform.WithScale( 1 );
			instance.Object = go;

			if ( dynamic )
			{
				Light component;
				if ( light.IsSpot )
				{
					var spot = go.AddComponent<SpotLight>();
					spot.ConeOuter = Math.Clamp( light.SpotCutoffAngle.RadianToDegree(), 1, 89 );
					spot.ConeInner = Math.Clamp( light.FalloffAngle.RadianToDegree(), 0, spot.ConeOuter );
					component = spot;
				}
				else
				{
					component = go.AddComponent<PointLight>();
				}

				component.Shadows = false;
				component.FogMode = Light.FogInfluence.Disabled;

				// As ObjectLights: the transmissive contribution marks it as a light of the level's.
				component.Contribution = Light.LightContribution.Diffuse | Light.LightContribution.Specular | Light.LightContribution.Transmissive;
				if ( light.Flags.HasFlag( LightFlags.NoSpecular ) )
					component.Contribution &= ~Light.LightContribution.Specular;
				go.Tags.Add( "halo_dynamic_light" );
				instance.Light = component;
			}

			if ( flare is not null )
			{
				var renderer = go.AddComponent<ModelRenderer>();
				renderer.Model = flare;
				renderer.RenderType = ModelRenderer.ShadowRenderType.Off;
				instance.Flare = renderer;
			}
		}

		_lights.Add( instance );
		Apply( instance );
	}

	void UpdateLights( float dt )
	{
		for ( var i = _lights.Count - 1; i >= 0; i-- )
		{
			var light = _lights[i];
			light.Age += dt;
			if ( light.Age > light.Duration || !light.Object.IsValid() )
			{
				light.Destroy();
				_lights.RemoveAt( i );
				continue;
			}

			if ( light.Follow.IsValid() )
				light.Object.WorldTransform = light.Follow.WorldTransform.WithScale( 1 );

			Apply( light );
		}
	}

	static void Apply( LightInstance instance )
	{
		var light = instance.Definition.Light;
		var intensity = Math.Clamp( (1 - light.Falloff( instance.Age / instance.Duration )) * instance.Scale, 0, 1 );

		if ( instance.Light is { } component )
		{
			var color = System.Numerics.Vector3.Lerp( light.ColorLower, light.ColorUpper, intensity ) * LightBrightness;
			var radius = light.RadiusAt( intensity );
			component.LightColor = new Color( color.X, color.Y, color.Z );
			if ( component is PointLight point )
				point.Radius = MathF.Max( radius, 0.01f ) * Units;
			else if ( component is SpotLight spot )
				spot.Radius = MathF.Max( radius, 0.01f ) * Units;
			component.Enabled = intensity > 0 && radius > 0;
		}

		// The flare's tint is its intensity (see halo1_lens_flare.shader).
		if ( instance.Flare is { } flare )
			flare.Tint = new Color( intensity, intensity, intensity, 1 );
	}
}
