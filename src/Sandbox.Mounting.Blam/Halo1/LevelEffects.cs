using Blam.Halo1;
using Blam.Halo1.Tags;

namespace Sandbox.Mounting.Blam.Halo1;

/// <summary>
/// Adds a level's lights and effects to its scene: the lights on placed objects, lens flares,
/// weather, the looping effects and particle systems attached to scenery (played by
/// <see cref="HaloParticleWorld"/>) and the lightning objects carry.
/// </summary>
static class LevelEffects
{
	/// <param name="moving">Placed objects' attachments (by index) the objects carry themselves (see <see cref="MovingAttachments"/>), left out here.</param>
	/// <param name="carriedFlares">Lights (by attachment index) whose lens flares the objects carry (posed devices), left out of the flare model only.</param>
	/// <returns>Models built in code, which only exist while something holds on to them.</returns>
	public static List<Model> Build( HaloCeMount host, CacheFile cache, ScenarioTag scenario, IReadOnlySet<(ObjectPlacement Placement, int Attachment)> moving, IReadOnlySet<(ObjectPlacement Placement, int Attachment)> carriedFlares )
	{
		var models = new List<Model>();
		var sites = ObjectAttachmentSites.Find( cache, scenario );
		sites.RemoveAll( s => moving.Contains( (s.Placement, s.Index) )
			|| ObjectAttachmentSites.GameDriven( cache, s.Placement, s.Attachment.Type, s.Attachment.PrimaryScale, s.Attachment.ChangeColor ) );

		ObjectLights.Build( cache, sites );

		var bsps = new List<StructureBspEffects>();
		foreach ( var reference in scenario.StructureBsps )
		{
			try
			{
				bsps.Add( new StructureBspEffects( cache, reference ) );
			}
			catch ( Exception e )
			{
				host.Logger.Warning( $"{reference.Tag}: couldn't read effects: {e.Message}" );
			}
		}

		var flares = new List<LensFlareInstance>();
		if ( scenario.Skies.Count > 0 )
			flares.AddRange( LensFlares.FromSky( cache, scenario.Skies[0] ) );
		foreach ( var bsp in bsps )
			flares.AddRange( LensFlares.FromBsp( bsp ) );
		flares.AddRange( LensFlares.FromObjects( cache, sites.Where( s => !carriedFlares.Contains( (s.Placement, s.Index) ) ) ) );

		var flareModel = LensFlares.Build( host, $"haloce/{cache.Name}/lens_flares.vmdl", cache, flares );
		if ( flareModel is not null )
		{
			models.Add( flareModel );
			AddRenderer( "lens flares", flareModel );
		}

		var weather = Weather.Build( host, $"haloce/{cache.Name}/weather.vmdl", cache, bsps );
		if ( weather is not null )
		{
			models.Add( weather );

			// Blended after the fog and the water, as Halo drew it (PlanarFogFrame sorts it and grabs the frame before it).
			AddRenderer( "weather", weather ).Tags.Add( PlanarFogFrame.WeatherTag );
			PlanarFogFrame.Watch();
		}

		AddAmbientEffects( host, cache, sites );

		var lightning = LightningWidgets.Build( host, $"haloce/{cache.Name}/lightning.vmdl", cache, scenario );
		if ( lightning is not null )
		{
			models.Add( lightning );
			AddRenderer( "lightning", lightning );
		}

		return models;
	}

	// Places the looping effects and particle systems level objects carry, for HaloParticleWorld to play at their rest values. A looping effect is one root at its object, its locations at the object's markers of their names (see EffectLocationSite).
	static void AddAmbientEffects( HaloCeMount host, CacheFile cache, IEnumerable<AttachmentSite> sites )
	{
		GameObject parent = null;
		var plans = new Dictionary<uint, List<AttachmentChild>>();
		var effects = new Dictionary<uint, EffectPlan>();
		var placedEffects = new HashSet<(ObjectPlacement, int)>();

		foreach ( var site in sites )
		{
			var type = site.Attachment.Type;
			if ( type is null || (type.Class != EffectTagClasses.Effect && type.Class != EffectTagClasses.ParticleSystem) )
				continue;

			// An effect has a site per location: its root is placed once.
			if ( type.Class == EffectTagClasses.Effect && !placedEffects.Add( (site.Placement, site.Index) ) )
				continue;

			try
			{
				// The attachment as the object's attachments prefab has it: its rest value and state.
				var objectTag = site.Placement.Object;
				if ( !plans.TryGetValue( objectTag.Id, out var plan ) )
					plans[objectTag.Id] = plan = ObjectAttachmentPlan.Plan( cache, objectTag );

				var child = plan.FirstOrDefault( c => c.Kind is (AttachmentKind.Effect or AttachmentKind.ParticleSystem) && c.Index == site.Index );
				var locations = type.Class == EffectTagClasses.Effect ? child?.Locations : null;

				parent ??= new GameObject( true, "particles" );
				var go = new GameObject( parent, child?.OnAtRest ?? true, System.IO.Path.GetFileName( type.Path ) );
				if ( locations is not null )
				{
					var (origin, rotation) = ObjectAttachmentSites.PlacementFrame( site.Placement );
					go.WorldPosition = origin;
					go.WorldRotation = rotation;
				}
				else
				{
					go.WorldPosition = site.Position;
					go.WorldRotation = site.Rotation;
				}

				if ( type.Class == EffectTagClasses.Effect )
				{
					if ( !effects.TryGetValue( type.Id, out var effect ) )
						effects[type.Id] = effect = new EffectPlan( cache, type, EffectView.ThirdPerson, looping: true );
					EffectBuilder.Build( effect, go, null, locations );
					if ( locations is not null )
						EffectBuilder.PlaceAttachedLocations( go, Transform.Zero, locations );
				}

				HaloEffects.Mark( go, type, looping: true, attached: true, a: child?.RestValue ?? 1,
					color: LensFlares.ChangeColorTint( cache, site.Placement, site.Attachment.ChangeColor ) );
			}
			catch ( Exception e )
			{
				host.Logger.Warning( $"{site.Placement.Object}: couldn't place {type}: {e.Message}" );
			}
		}

		if ( parent is not null )
			HaloParticleWorld.Watch();
	}

	/// <summary>
	/// Effects are see-through sprites, which don't cast shadows.
	/// </summary>
	static GameObject AddRenderer( string name, Model model )
	{
		var renderer = new GameObject( true, name ).AddComponent<ModelRenderer>();
		renderer.Model = model;
		renderer.RenderType = ModelRenderer.ShadowRenderType.Off;
		return renderer.GameObject;
	}
}
