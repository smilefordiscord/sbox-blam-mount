using Blam.Halo2;
using Blam.Halo2.TagData;
using Blam.Halo2.Tags;
using LoopingSoundResources = Sandbox.Mounting.Blam.Halo1.LoopingSoundResources;

namespace Sandbox.Mounting.Blam.Halo2;

/// <summary>
/// What objects carry (object attachments, <see cref="ObjectAttachment"/>), at each marker of the attachment's name:
/// effects playing for ever (<see cref="Halo2EffectBuilder"/>, marked for Halo2ParticleWorld as
/// <c>mount://halo2/&lt;effect&gt;__loop.prefab</c>), light volumes and lens flares as their models
/// (<see cref="LightVolumeModels"/>: Dune's watchtower beam), and in prefabs also contrails and looping sounds. Each goes
/// under a child named after its marker instance, as the object's model names its attachments ("marker", "marker_1"...;
/// "" the object's origin), placed at the marker's default pose.
/// </summary>
/// <remarks>
/// <para>In scenes (<see cref="Attach"/>): effects, light volumes and lens flares. An attachment scaled by an object
/// function shows when that function is non-zero on an object at rest (<see cref="ShaderInputs"/>: full vitality, alive;
/// a device's power on), since nothing here drives them. "Effect scenery" (Dune's smoke and sparks) has no model of its
/// own: it gets an empty object. Placed objects' looping sounds are the level's (<see cref="LevelSounds"/>).</para>
/// <para>For games, <c>mount://halo2/&lt;object tag&gt;__attachments.prefab</c> holds every one of them, each tagged
/// <c>halo_function_&lt;object function&gt;</c> when an object function scales it (a game turns those on and off: the
/// plasma pistol's charge glow on <c>primary_charged</c>). Contrails are trail renderers tagged <c>halo_contrail</c>
/// (<see cref="ContrailMaterials"/>); looping sounds are a sound point per track and detail, as the level's. A contrail
/// or looping sound whose function is off at rest (the plasma pistol's charging hum) is a disabled child tagged
/// <c>halo_off_at_rest</c>, for the game to enable while the function is on. A projectile's
/// <c>__projectile.prefab</c> is the same with its model on the root (<see cref="ObjectAttachmentsPrefabLoader"/>).</para>
/// </remarks>
static class ObjectEffects
{
	public const string Tag = "halo_effect";

	public static string PrefabName( string objectTagPath ) => $"{Halo2Mount.ResourcePath( objectTagPath )}__attachments.prefab";

	public static string FirstPersonPrefabName( string objectTagPath ) => $"{Halo2Mount.ResourcePath( objectTagPath )}__attachments_fp.prefab";

	/// <summary>
	/// The object's attachments the mount builds, or none if they can't be read.
	/// </summary>
	public static List<ObjectAttachment> Read( TagEntry objectEntry )
	{
		if ( !ObjectTag.IsObject( objectEntry ) )
			return [];

		try
		{
			return ObjectAttachment.Read( objectEntry ).Where( a => InScenes( a ) || ContrailTag.IsContrail( a.Type ) || LoopingSound( a.Type ) is not null ).ToList();
		}
		catch ( Exception e )
		{
			Halo2Mount.Current?.Logger.Warning( $"{objectEntry.Path}: couldn't read attachments: {e.Message}" );
			return [];
		}
	}

	/// <summary>
	/// Adds a placed object's attachments that show at rest. <paramref name="go"/> is the placed object, or null if it
	/// has no model; returns the object they went on (a new one for an effect-only object), or <paramref name="go"/>.
	/// </summary>
	public static GameObject Attach( TagEntry objectEntry, ObjectFrame frame, GameObject parent, GameObject go )
	{
		var attachments = Read( objectEntry ).Where( a => InScenes( a ) && ShowsAtRest( a ) ).ToList();
		if ( attachments.Count == 0 )
			return go;

		if ( go is null )
		{
			go = new GameObject( parent, true, System.IO.Path.GetFileName( objectEntry.Path ) );
			go.WorldPosition = (Vector3)frame.Position * Halo2Mount.WorldScale;
			go.WorldRotation = Rotation.LookAt( (Vector3)frame.Forward, (Vector3)frame.Up );
			if ( frame.Scale != 1 )
				go.WorldScale = frame.Scale;
		}

		Build( objectEntry, attachments, go, tagFunctions: false );
		return go;
	}

	/// <summary>
	/// Builds the attachments under <paramref name="root"/> (the object's origin).
	/// </summary>
	public static void Build( TagEntry objectEntry, IEnumerable<ObjectAttachment> attachments, GameObject root, bool tagFunctions, bool firstPerson = false )
	{
		var renderModel = RenderModel( new ObjectTag( objectEntry ) );
		var sounds = 0;
		var sites = new Dictionary<string, GameObject>( StringComparer.OrdinalIgnoreCase );
		foreach ( var attachment in attachments )
		{
			foreach ( var (name, rotation, translation) in Markers( renderModel, attachment.Marker ) )
			{
				try
				{
					// The marker instance's child, as ObjectFrame places objects: the marker's forward and up.
					if ( !sites.TryGetValue( name, out var site ) )
					{
						site = string.IsNullOrEmpty( name ) ? root : new GameObject( root, true, name );
						site.LocalPosition = string.IsNullOrEmpty( name ) ? site.LocalPosition : (Vector3)translation * Halo2Mount.WorldScale;
						if ( !string.IsNullOrEmpty( name ) )
							site.LocalRotation = Rotation.LookAt( (Vector3)System.Numerics.Vector3.Transform( System.Numerics.Vector3.UnitX, rotation ),
								(Vector3)System.Numerics.Vector3.Transform( System.Numerics.Vector3.UnitZ, rotation ) );
						sites[name] = site;
					}

					var at = new GameObject( site, true, System.IO.Path.GetFileName( attachment.Type.Path ) );
					at.Tags.Add( Tag );
					if ( tagFunctions && !string.IsNullOrEmpty( attachment.PrimaryScale ) )
						at.Tags.Add( $"halo_function_{attachment.PrimaryScale.ToLowerInvariant()}" );

					if ( ContrailTag.IsContrail( attachment.Type ) )
					{
						var contrail = new ContrailTag( attachment.Type );
						var path = ContrailMaterials.FullPath( attachment.Type.Path );
						var material = Halo2Effects.Exists( path ) ? Material.Load( path ) : null;
						Halo1.EffectBuilder.AddContrail( contrail, contrail.IsAdditive, at, material, Speed( objectEntry ) );
						at.Tags.Add( "halo_contrail" );
						OffAtRest( at, attachment );
					}
					else if ( LoopingSound( attachment.Type ) is { } sound )
					{
						LevelSounds.AddLoopingSound( at, sound, null, sounds++ );
						OffAtRest( at, attachment );
					}
					else if ( !EffectTag.IsEffect( attachment.Type ) )
					{
						// Light volumes and lens flares (a light's own ones too): their models at the marker.
						foreach ( var path in ModelsOf( attachment.Type, firstPerson ) )
						{
							var model = Model.Load( path );
							if ( model is null || model.IsError )
								Halo2Mount.Current?.Logger.Warning( $"{objectEntry.Path}: {path} didn't load ({(model is null ? "null" : "error")})" );
							if ( model is not null )
							{
								var renderer = at.AddComponent<ModelRenderer>();
								renderer.Model = model;
								renderer.RenderType = ModelRenderer.ShadowRenderType.Off;
							}
						}
					}
					else
					{
						// In scenes nothing drives the object's functions: effect scale A is the primary function's value at rest.
						var a = tagFunctions || string.IsNullOrEmpty( attachment.PrimaryScale ) || OnAtRest.Contains( attachment.PrimaryScale ) ? 1 : Math.Clamp( ShaderInputs.AtRest( 0.5f ).Value( attachment.PrimaryScale ), 0, 1 );
						Halo2EffectBuilder.Build( new EffectTag( attachment.Type ), at, looping: true, a, firstPerson: firstPerson );
					}
				}
				catch ( Exception e )
				{
					Halo2Mount.Current?.Logger.Warning( $"{objectEntry.Path}: couldn't build {attachment.Type.Path}: {e.Message}" );
				}
			}
		}
	}

	/// <summary>
	/// The models an attachment draws: a light volume's or a lens flare's, or a light's lens flare (ligh +0x90, in the
	/// light's colour: <see cref="LensFlareModels.LightFullPath"/>) and light volume (+0x9C). Its light itself is left to the game (an s&amp;box light, which halo2_opaque.shader takes with HaloDynamicLights).
	/// </summary>
	// firstPerson: for the view of the object's own player (true) or anyone else's (false), leaving out the lens flares Halo 2 doesn't draw there; null for both.
	public static List<string> ModelsOf( TagEntry type, bool? firstPerson = null )
	{
		if ( LightVolumeTag.IsLightVolume( type ) )
			return [LightVolumeModels.FullPath( type.Path )];
		if ( LensFlareTag.IsLensFlare( type ) )
			return firstPerson is { } own && (new LensFlareTag( type ).Flags & (own ? 8 : 4)) != 0 ? [] : [LensFlareModels.FullPath( type.Path )];
		if ( type is null || type.Address == 0 || type.Class.ToString() != "ligh" )
			return [];

		// A light's flare: 0x40 third person only, 0x20 first person only (ligh +0x0), as Halo 2 submits it.
		var models = new List<string>();
		var lightFlags = type.Cache.TagData.U32( type.Address );
		if ( LensFlareModels.LightLens( type ) is not null && !(firstPerson is { } ownView && (lightFlags & (ownView ? 0x40u : 0x20u)) != 0) )
			models.Add( LensFlareModels.LightFullPath( type.Path ) );
		if ( type.Cache.GetTagReference( type.Address + 0x9C ) is { } volume && LightVolumeTag.IsLightVolume( volume ) && !string.IsNullOrEmpty( volume.Path ) )
			models.Add( LightVolumeModels.FullPath( volume.Path ) );
		return models;
	}

	// Attachments placed objects show in scenes: effects, light volumes and lens flares.
	static bool InScenes( ObjectAttachment attachment ) => EffectTag.IsEffect( attachment.Type ) || ModelsOf( attachment.Type ).Count > 0;

	// A looping sound attachment's resources, or null for anything else or a looping sound without samples.
	static LoopingSoundResources LoopingSound( TagEntry type ) =>
		type is not null && type.Class == LoopingSoundTag.Class ? Halo2Mount.Current?.Sounds?.GetLoopingSound( type.Path ) : null;

	// A projectile's initial velocity (world units per second), which spaces its contrails' texture; 0 for other objects.
	static float Speed( TagEntry objectEntry ) => new ObjectTag( objectEntry ).Type == ObjectType.Projectile
		? objectEntry.Cache.TagData.F32( objectEntry.Address + (uint)TagLayouts.Projectile.Field( "initial velocity" ).Offset )
		: 0;

	// Disables a child whose object function is off at rest, for the game to turn on.
	static void OffAtRest( GameObject go, ObjectAttachment attachment )
	{
		if ( ShowsAtRest( attachment ) )
			return;

		go.Enabled = false;
		go.Tags.Add( "halo_off_at_rest" );
	}

	/// <summary>
	/// Object functions on for an object at rest that the shader inputs leave at 0: a device's power.
	/// </summary>
	static readonly HashSet<string> OnAtRest = new( StringComparer.OrdinalIgnoreCase ) { "power" };

	static bool ShowsAtRest( ObjectAttachment attachment ) =>
		string.IsNullOrEmpty( attachment.PrimaryScale ) || OnAtRest.Contains( attachment.PrimaryScale ) || ShaderInputs.AtRest( 0.5f ).Value( attachment.PrimaryScale ) > 0;

	/// <summary>
	/// Every marker of the name in model space (default pose), named as the model's attachments are (the second and
	/// later of a name numbered), or the object's origin ("") when the name is empty or the model has none.
	/// </summary>
	static IEnumerable<(string Name, System.Numerics.Quaternion Rotation, System.Numerics.Vector3 Translation)> Markers( RenderModelTag model, string name )
	{
		var markers = string.IsNullOrEmpty( name ) || model is null ? [] : model.Markers.Where( m => string.Equals( m.Name, name, StringComparison.OrdinalIgnoreCase ) ).ToList();
		if ( markers.Count == 0 )
		{
			yield return ("", System.Numerics.Quaternion.Identity, System.Numerics.Vector3.Zero);
			yield break;
		}

		var pose = model.DefaultPose();
		for ( var i = 0; i < markers.Count; i++ )
		{
			var marker = markers[i];
			var instance = i == 0 ? marker.Name : $"{marker.Name}_{i}";
			if ( marker.Node < 0 || marker.Node >= pose.Length )
			{
				yield return (instance, marker.Rotation, marker.Translation);
				continue;
			}

			var (rotation, translation) = pose[marker.Node];
			yield return (instance, System.Numerics.Quaternion.Normalize( rotation * marker.Rotation ), translation + System.Numerics.Vector3.Transform( marker.Translation, rotation ));
		}
	}

	// The object's model resource (its hlmt's, see RenderModelBuilder), or null if it has none to draw.
	public static string ModelPath( TagEntry objectEntry ) =>
		ObjectTag.IsObject( objectEntry ) && new ObjectTag( objectEntry ) is var obj && RenderModel( obj ) is not null ? Halo2Mount.ModelPath( obj.Model.Path ) : null;

	static RenderModelTag RenderModel( ObjectTag obj )
	{
		if ( obj.Model is not { Address: not 0 } hlmtTag || hlmtTag.Class != ModelTag.Class )
			return null;

		var hlmt = new ModelTag( hlmtTag );
		return hlmt.RenderModel is { Address: not 0 } tag && tag.Class == RenderModelTag.Class ? new RenderModelTag( tag ) : null;
	}
}

// An object's attachments as a prefab (ObjectEffects), mount://halo2/<object tag>__attachments.prefab.
// With its model, a projectile in flight, <projectile tag>__projectile.prefab, to parent to the moving projectile.
// First person, what the local player's own object carries in their first person view, <object tag>__attachments_fp.prefab.
sealed class ObjectAttachmentsPrefabLoader( string mapPath, uint objectTagId, bool withModel = false, bool firstPerson = false ) : Halo2PrefabLoader
{
	protected override string MapPath => mapPath;

	protected override void Build( CacheFile cache, GameObject root )
	{
		var tag = cache.GetTag( objectTagId );
		root.Tags.Add( "halo_attachments" );
		if ( withModel && ObjectEffects.ModelPath( tag ) is { } path && Model.Load( path ) is { IsError: false } model )
		{
			var renderer = root.AddComponent<ModelRenderer>();
			renderer.Model = model;
		}

		ObjectEffects.Build( tag, ObjectEffects.Read( tag ), root, tagFunctions: true, firstPerson );
	}
}
