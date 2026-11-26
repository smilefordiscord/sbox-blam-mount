using Blam.Halo2.Tags;

namespace Sandbox.Mounting.Blam.Halo2;

// Builds a Halo 2 effect (EffectTag) out of engine components: its root marked for Halo2ParticleWorld, which plays its events, particle systems and sounds, a child per location (marker), and its light volumes and lens flares as models at their locations.
static class Halo2EffectBuilder
{
	// Builds the effect under root (its +X the effect's forward) and returns how long one run of it lasts, its particles' lives included (seconds).
	// A first person effect is seen in its local player's own first person view (Halo2ParticleEffects).
	public static float Build( EffectTag effect, GameObject root, bool looping, float a = 1, float b = 1, bool firstPerson = false )
	{
		var locations = new Dictionary<string, GameObject>();
		GameObject Location( string name )
		{
			if ( string.IsNullOrEmpty( name ) )
				return root;

			if ( !locations.TryGetValue( name, out var go ) )
			{
				go = new GameObject( root, true, name );
				go.Tags.Add( "halo_effect_location" );

				// Halo 2's special locations for effects made where something hits (the root's +X is the surface normal): "gravity" straight down (the root's -Z, right when the root is upright), "up" up, "forward" (the shot's direction) into the surface; "normal", "backward" and "reflection" leave it.
				if ( name == "gravity" )
					go.LocalRotation = Rotation.LookAt( Vector3.Down, Vector3.Forward );
				else if ( name == "up" )
					go.LocalRotation = Rotation.LookAt( Vector3.Up, Vector3.Backward );
				else if ( name == "forward" )
					go.LocalRotation = Rotation.LookAt( Vector3.Backward, Vector3.Up );
				locations[name] = go;
			}

			return go;
		}

		// Every location the effect names, so a game can move each onto its model's marker.
		foreach ( var name in effect.Locations )
			Location( name );

		foreach ( var e in effect.Events )
		{
			foreach ( var part in e.Parts )
			{
				var isVolume = LightVolumeTag.IsLightVolume( part.Type );
				if ( !isVolume && !LensFlareTag.IsLensFlare( part.Type ) )
					continue;

				// A light volume or lens flare at its location for the effect's life (its fade over the event isn't modelled).
				if ( Model.Load( isVolume ? LightVolumeModels.FullPath( part.Type.Path ) : LensFlareModels.FullPath( part.Type.Path ) ) is { IsError: false } model )
				{
					var go = new GameObject( Location( effect.LocationName( part.Location ) ), true, System.IO.Path.GetFileName( part.Type.Path ) );
					var renderer = go.AddComponent<ModelRenderer>();
					renderer.Model = model;
					renderer.RenderType = ModelRenderer.ShadowRenderType.Off;
				}
			}
		}

		Halo2ParticleEffects.Mark( root, effect.Tag.Path, looping, a, b, firstPerson );

		var definition = Halo2ParticleLibrary.Effect( effect.Tag );
		return (definition?.LongestRun ?? 0) + (definition?.LongestLife ?? 0);
	}
}
