using Blam;
using Blam.Halo1;
using Blam.Halo1.Tags;

namespace Sandbox.Mounting.Blam.Halo1;

/// <summary>
/// Builds what of an <see cref="EffectPlan"/> is engine components: a child per location (marker)
/// the effect's tag names, <see cref="SoundPointComponent"/>s for its sounds and decal prefab
/// instances for its decals. Its particles, particle systems and lights aren't components: the
/// caller marks the root (<see cref="HaloEffects.Mark"/>) and <see cref="HaloParticleWorld"/> plays
/// them from its tags, as Halo does, at the location children it finds by name. Also builds what
/// objects carry for the attachments prefabs (lights, contrails, light volumes).
/// </summary>
/// <remarks>
/// Sounds play as the effect starts (looping effects repeat them every cycle); decals are placed
/// with it. Neither waits for its event's delay (see <see cref="EffectPlan"/>'s notes).
/// </remarks>
static class EffectBuilder
{
	const float WorldScale = HaloCeMount.WorldScale;

	/// <summary>
	/// s&amp;box divides diffuse light by pi and Halo didn't (as for object lights).
	/// </summary>
	const float LightBrightness = MathF.PI;

	/// <summary>
	/// Builds the effect's locations, sounds and decals under <paramref name="root"/>, whose +X is
	/// the effect's forward.
	/// </summary>
	/// <param name="decals">Decal prefabs by decal tag id (see <see cref="LoadDecalPrefabs"/>), or null for none.</param>
	/// <param name="sites">An object's looping effect: where its locations go on the object (<see cref="PlaceAttachedLocations"/> puts them there). Its unnamed location gets a child of its own (<see cref="HaloEffects.OriginTag"/>), and a location without a site is marked so nothing is made there (<see cref="HaloEffects.NowhereTag"/>).</param>
	public static void Build( EffectPlan plan, GameObject root, Dictionary<uint, PrefabFile> decals, IReadOnlyList<EffectLocationSite> sites = null )
	{
		var locations = new Dictionary<string, GameObject>();
		GameObject Location( string name )
		{
			name ??= "";
			if ( name == "" && sites is null )
				return root;

			if ( !locations.TryGetValue( name, out var go ) )
			{
				go = new GameObject( root, true, name == "" ? "origin" : name );
				go.Tags.Add( "halo_effect_location" );
				if ( name == "" )
					go.Tags.Add( HaloEffects.OriginTag );
				if ( sites is not null && !sites.Any( s => s.Location == name ) )
					go.Tags.Add( HaloEffects.NowhereTag );

				// Straight down: the root's -Z, which is right when the root is upright. The shot's
				// direction ("negative incident") goes into the surface; the others leave it.
				if ( name == "gravity" )
					go.LocalRotation = Rotation.LookAt( Vector3.Down, Vector3.Forward );
				else if ( name == "negative incident" )
					go.LocalRotation = Rotation.LookAt( Vector3.Backward, Vector3.Up );

				locations[name] = go;
			}

			return go;
		}

		// Every location the tag names, used or not: the runtime looks its parts' locations up by name.
		foreach ( var name in plan.Effect.Locations )
			Location( name );

		foreach ( var item in plan.Items )
		{
			if ( item.Status is not (ConversionStatus.Full or ConversionStatus.Partial) )
				continue;
			if ( sites is not null && !sites.Any( s => s.Location == (item.Location ?? "") ) )
				continue;

			try
			{
				switch ( item.Kind )
				{
					case EffectItemKind.Sound:
						AddSound( plan, item, Location( item.Location ) );
						break;
					case EffectItemKind.Decal:
						var at = (item.Part.Flags & 0x1) != 0 ? Location( "gravity" ) : Location( item.Location );
						AddDecal( item, at, decals );
						break;
				}
			}
			catch ( Exception e )
			{
				HaloCeMount.Current?.Logger.Warning( $"{plan.Tag}: couldn't build {item.Tag}: {e.Message}" );
			}
		}
	}

	// Puts an attached effect's locations (built with their sites, see Build) where Halo has them on the object at its default pose: each at its first site, a copy at each further instance of its marker. The effect's root is at rootOnModel. Returns each location object with the site it's at.
	public static List<(GameObject Location, EffectLocationSite Site)> PlaceAttachedLocations( GameObject root, Transform rootOnModel, IReadOnlyList<EffectLocationSite> sites )
	{
		var placed = new List<(GameObject, EffectLocationSite)>();
		foreach ( var location in root.Children.Where( c => c.Tags.Has( "halo_effect_location", false ) ).ToList() )
		{
			var name = location.Tags.Has( HaloEffects.OriginTag, false ) ? "" : location.Name;
			var at = sites.Where( s => s.Location == name ).ToList();
			for ( var i = 0; i < at.Count; i++ )
			{
				var go = i == 0 ? location : location.Clone( new Transform(), root, true, location.Name );
				go.LocalTransform = rootOnModel.ToLocal( OnModel( at[i] ) );
				placed.Add( (go, at[i]) );
			}
		}

		return placed;
	}

	// Where a site is on its object's model at the default pose, in s&box units.
	public static Transform OnModel( EffectLocationSite site ) =>
		new( (Vector3)site.Position * WorldScale, GbxSkeleton.ToRotation( site.Rotation ) );

	/// <summary>
	/// Loads the decal prefabs an effect's decal parts instance, before the effect's prefab scene
	/// exists. Decal tags without one are left out.
	/// </summary>
	public static Dictionary<uint, PrefabFile> LoadDecalPrefabs( EffectPlan plan )
	{
		var prefabs = new Dictionary<uint, PrefabFile>();
		var mount = Sandbox.Mounting.Directory.Get( EffectPrefabs.Ident );
		foreach ( var item in plan.Items.Where( i => i.Kind == EffectItemKind.Decal && i.Status is ConversionStatus.Full or ConversionStatus.Partial ) )
		{
			var path = $"mount://{EffectPrefabs.Ident}/{HaloCeMount.ResourcePath( item.Tag.Path )}.prefab";
			if ( prefabs.ContainsKey( item.Tag.Id ) || mount?.GetByPath( path ) is null )
				continue;

			var prefab = PrefabFile.Load( path );
			if ( prefab is null )
				continue;

			prefab.GetScene();
			prefabs[item.Tag.Id] = prefab;
		}

		return prefabs;
	}

	/// <summary>
	/// A light an object carries, at its attachment scale function's <paramref name="value"/>
	/// (see <see cref="ObjectAttachmentPlan"/>): a point or spot light, for a dynamic light only, set
	/// up like the lights on placed objects (<see cref="ObjectLights"/>), with Halo's color and radius for the value and
	/// disabled where they make it black or empty, and the light's lens flare (<paramref name="flare"/>,
	/// its <see cref="LensFlares.ForLight"/> model) as a model renderer whose tint is the value.
	/// </summary>
	/// <param name="withLight">False for a lens flare on its own (another instance of the light's marker).</param>
	/// <param name="tint">The object's change color the light is multiplied by (<see cref="LensFlares.ChangeColorTint"/>);
	/// the flare model carries its own.</param>
	/// <remarks>
	/// A game drives it by the value v (0-1): the light's <c>LightColor</c> = <see cref="LightTag.ColorAt"/>(v) x pi,
	/// its radius <see cref="LightTag.RadiusAt"/>(v) in s&amp;box units, enabled while the color isn't black; the
	/// flare's renderer attribute <c>HaloFunctionScale</c> = v (or its tint = (v, v, v)).
	/// </remarks>
	public static void AddAttachedLight( CacheFile cache, TagEntry tag, GameObject go, float value, Model flare, bool withLight = true, System.Numerics.Vector3? tint = null )
	{
		var light = new LightTag( cache, tag );
		if ( withLight && light.IsDynamic && light.Radius > 0 )
		{
			var color = light.ColorAt( value ) * (tint ?? System.Numerics.Vector3.One) * LightBrightness;
			var radius = MathF.Max( light.RadiusAt( value ), 0.01f ) * WorldScale;
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

			component.LightColor = new Color( color.X, color.Y, color.Z );
			component.Shadows = false;
			component.FogMode = Light.FogInfluence.Disabled;

			// As ObjectLights: the transmissive contribution marks it as a light of the level's.
			component.Contribution = Light.LightContribution.Diffuse | Light.LightContribution.Specular | Light.LightContribution.Transmissive;
			if ( light.Flags.HasFlag( LightFlags.NoSpecular ) )
				component.Contribution &= ~Light.LightContribution.Specular;

			component.Enabled = color != System.Numerics.Vector3.Zero && light.RadiusAt( value ) > 0;
			go.Tags.Add( "halo_dynamic_light" );
		}

		if ( flare is not null && !flare.IsError )
		{
			var renderer = go.AddComponent<ModelRenderer>();
			renderer.Model = flare;
			renderer.RenderType = ModelRenderer.ShadowRenderType.Off;
			var v = Math.Clamp( value, 0, 1 );
			renderer.Tint = new Color( v, v, v, 1 );
		}
	}

	/// <summary>
	/// The lens flare models (<see cref="LensFlares.ForLight"/>) of the lights an effect creates, by
	/// light tag id, loaded before the effect's prefab scene exists. Lights without one are left out.
	/// </summary>
	public static Dictionary<uint, Model> LoadLensFlares( CacheFile cache, EffectPlan plan )
	{
		var flares = new Dictionary<uint, Model>();
		foreach ( var item in plan.Items.Where( i => i.Kind == EffectItemKind.Light && i.Status is ConversionStatus.Full or ConversionStatus.Partial ) )
		{
			if ( !flares.ContainsKey( item.Tag.Id ) && LoadLensFlare( cache, item.Tag ) is { } model )
				flares[item.Tag.Id] = model;
		}

		return flares;
	}

	/// <summary>
	/// A light's lens flare model, or null if it has none that draws or it isn't registered.
	/// </summary>
	public static Model LoadLensFlare( CacheFile cache, TagEntry lightTag )
	{
		if ( lightTag is null || !ObjectAttachmentPlan.DrawsLensFlare( cache, new LightTag( cache, lightTag ).LensFlare ) )
			return null;

		var path = LensFlares.FullPath( lightTag.Path );
		if ( Sandbox.Mounting.Directory.Get( EffectPrefabs.Ident )?.GetByPath( path ) is null )
			return null;

		return Model.Load( path ) is { IsError: false } model ? model : null;
	}

	/// <summary>
	/// A contrail (Halo CE's or Halo 2's) as a trail renderer, with the contrail's material
	/// (<see cref="ContrailMaterial"/>, loaded from <paramref name="material"/>, or none if that isn't
	/// given): its points' widths and colors over their life (<see cref="ContrailDefinition.PointKeys"/>),
	/// <paramref name="additive"/> if it adds light, Halo's gamma space colors as
	/// they are (the material does the conversion). The trail is in the world; its object can move
	/// every frame.
	/// </summary>
	/// <remarks>
	/// The newest and oldest points are faded out unless the contrail's flags say otherwise. The
	/// texture repeats u times per Halo point and scrolls by its animation rate. Halo drops points
	/// at a rate, so with the carrier's <paramref name="speed"/> (world units per second) a repeat
	/// is a fixed distance along the trail, which stays put as the trail grows; without it the
	/// repeats are spread over the trail's length. Points don't drift (Halo's point velocity and
	/// physics aren't applied).
	/// </remarks>
	public static void AddContrail( ContrailDefinition contrail, bool additive, GameObject go, Material material = null, float speed = 0 )
	{
		var keys = contrail.PointKeys();
		if ( keys.Count == 0 )
			return;

		var life = contrail.PointLife();
		var width = new Curve();
		var color = new Gradient();
		var fadeIn = contrail.PointRate > 0 ? Math.Clamp( 1 / contrail.PointRate / life, 0.01f, 0.5f ) : 0.01f;
		foreach ( var (age, w, c) in keys )
		{
			var t = Math.Clamp( age / life, 0, 1 );
			width.AddPoint( LinearFrame( t, w * WorldScale ) );
			color.AddColor( t, new Color( Math.Clamp( c.X, 0, 1 ), Math.Clamp( c.Y, 0, 1 ), Math.Clamp( c.Z, 0, 1 ) ) );
			if ( t >= fadeIn || contrail.FirstPointUnfaded )
				color.AddAlpha( t, Math.Clamp( c.W, 0, 1 ) );
		}

		if ( !contrail.FirstPointUnfaded )
		{
			color.AddAlpha( 0, 0 );
			color.AddAlpha( fadeIn, Math.Clamp( contrail.ColorAt( fadeIn * life ).W, 0, 1 ) );
		}
		if ( !contrail.LastPointUnfaded )
			color.AddAlpha( 1, 0 );

		var trail = go.AddComponent<TrailRenderer>();
		trail.LifeTime = life;
		trail.MaxPoints = Math.Clamp( (int)MathF.Ceiling( life * 144 ) + 2, 8, 512 );
		trail.PointDistance = 4;
		trail.Width = width;
		trail.Color = color;
		trail.Opaque = false;
		trail.CastShadows = false;
		trail.BlendMode = additive ? BlendMode.Lighten : BlendMode.Normal;
		trail.Face = contrail.RenderType == ContrailRenderType.ViewerFacing ? SceneLineObject.FaceMode.Camera : SceneLineObject.FaceMode.Normal;
		if ( material is not null )
		{
			var repeats = contrail.TextureRepeats.X == 0 ? 1 : contrail.TextureRepeats.X;
			var worldSpace = speed > 0 && contrail.PointRate > 0;
			trail.Texturing = trail.Texturing with
			{
				Material = material,
				WorldSpace = worldSpace,
				UnitsPerTexture = worldSpace ? MathF.Max( speed / contrail.PointRate / MathF.Abs( repeats ) * WorldScale, 0.01f ) : 10,
				Scale = worldSpace ? 1 : -contrail.RepeatsAlongTrail(),
				Scroll = contrail.TextureAnimation.X,
				TextureAddressMode = Sandbox.Rendering.TextureAddressMode.Wrap,
			};
		}
	}

	/// <summary>
	/// A light volume (<see cref="LightVolumeModel"/>) at its marker: a model renderer with the light
	/// volume's model, loaded from <paramref name="model"/>.
	/// </summary>
	public static void AddLightVolume( Model model, GameObject go )
	{
		var renderer = go.AddComponent<ModelRenderer>();
		renderer.Model = model;
		renderer.RenderType = ModelRenderer.ShadowRenderType.Off;
	}

	static void AddSound( EffectPlan plan, EffectItem item, GameObject parent )
	{
		var soundEvent = GameResource.Load<SoundEvent>( HaloSounds.EventPath( EffectPrefabs.Ident, item.Tag.Path ) );
		if ( soundEvent is null )
			return;

		var go = new GameObject( parent, true, System.IO.Path.GetFileName( item.Tag.Path ) );
		var sound = go.AddComponent<SoundPointComponent>();
		sound.SoundEvent = soundEvent;
		sound.PlayOnStart = true;
		Dry( sound );

		if ( plan.Looping && plan.Cycle > 0.05f )
		{
			sound.Repeat = true;
			sound.MinRepeatTime = plan.Cycle;
			sound.MaxRepeatTime = plan.Cycle;
		}
	}

	/// <summary>
	/// An instance of the decal's prefab at the location, scaled by the part's radius modifier.
	/// </summary>
	static void AddDecal( EffectItem item, GameObject parent, Dictionary<uint, PrefabFile> decals )
	{
		if ( decals is null || !decals.TryGetValue( item.Tag.Id, out var prefab ) )
			return;

		// Decal prefabs face into the surface they project onto (as s&box's own impacts place them).
		// Locations that leave the surface (normal, incident, the origin) are turned around.
		// The effect's root faces out of the surface (+X), so a location whose forward agrees
		// with it leaves the surface. The clone's transform is relative to the location.
		var rotation = parent.WorldRotation;
		if ( rotation.Forward.Dot( Vector3.Forward ) > 0.01f )
			rotation = Rotation.LookAt( -rotation.Forward, rotation.Up );

		var local = new Transform( Vector3.Zero, parent.WorldRotation.Inverse * rotation );
		var go = GameObject.Clone( prefab, local, parent, true, System.IO.Path.GetFileName( item.Tag.Path ) );
		var modifier = Average( item.Part.RadiusModifier );
		if ( go is not null && modifier > 0 && modifier != 1 )
			go.LocalScale = modifier;
	}

	/// <summary>
	/// No s&amp;box reverb (reflections) on a sound: Halo's sounds are dry, and its rooms come from
	/// the level's sound environments. The sound events default to it too; this keeps it when a
	/// prefab is used with another event.
	/// </summary>
	public static void Dry( BaseSoundComponent sound )
	{
		sound.ReverbOverride = true;
		sound.ReverbEnabled = false;
	}

	static Curve.Frame LinearFrame( float time, float value ) => new( time, value ) { Mode = Curve.HandleMode.Linear };

	static float Average( System.Numerics.Vector2 range ) => (range.X + range.Y) * 0.5f;
}
