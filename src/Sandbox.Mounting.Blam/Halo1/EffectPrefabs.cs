using Blam.Halo1;
using Blam.Halo1.Tags;

namespace Sandbox.Mounting.Blam.Halo1;

/// <summary>
/// Registers Halo's effects as prefabs a game can spawn, built from engine components and tags only
/// (sounds, decals, and the tags <see cref="HaloParticleWorld"/> plays the particles, particle
/// systems and lights by), so they also work in scenes and prefabs the game saves.
/// </summary>
/// <remarks>
/// <para><c>mount://haloce/&lt;effect tag&gt;.prefab</c> plays an effect once and removes itself
/// (<see cref="TemporaryEffect"/>, once its events are over: <see cref="HaloEffects.LongestRun"/>).
/// Its root is marked as <see cref="HaloEffects"/> describes, which is also how a game sets its
/// scales and change color; its particles outlive it as Halo's do. Its +X axis is the
/// effect's forward: the surface normal for impacts, the firing direction for muzzle flashes. Each
/// location (marker) the effect's tag names is a child named after the marker; "" is the root. Halo aims
/// impact locations itself: "normal" (the root's forward), "incident" (back along the shot),
/// "negative incident" (along the shot), "reflection", and "gravity" (straight down; defaults to
/// the root's -Z, set its world rotation to look down to be exact). Weapon effects use the weapon's
/// markers ("primary trigger", "primary ejection"): move those children onto the model attachments
/// of the same name.</para>
/// <para>Variants: <c>__fp</c> for the player's own first person view (effects with particles for
/// one camera only), <c>__loop</c> repeating the loop events forever (effects that objects carry;
/// it stays until the game removes it).</para>
/// <para>Aliases by role, so a game can find effects by name: <c>&lt;projectile&gt;__impact_&lt;material&gt;</c>,
/// <c>__impact_&lt;material&gt;_potential</c>, <c>__detonation</c>, <c>__detonation_&lt;material&gt;</c>,
/// <c>__detonation_started</c>, <c>__super_detonation</c>, <c>__projectile</c> (the projectile in
/// flight: its model, light volumes, contrails, lights, looping effects and sounds, to parent to the
/// moving projectile), <c>__attachments</c> (the same without the model, for a game that draws the
/// model itself; every object with attachments has one, see <see cref="AttachmentsPrefabLoader"/>),
/// <c>__attachments_fp</c> (its first person variant, for weapons and objects whose attachments
/// look different in first person);
/// <c>&lt;weapon&gt;__trigger&lt;n&gt;_fire</c> (and <c>_fire_fp</c>,
/// <c>_misfire</c>, <c>_empty</c>, <c>_charging</c>), <c>__ready</c>, <c>__overheated</c>,
/// <c>__detonation</c>; <c>&lt;object&gt;__shield_damaged</c> (the shield hit flare),
/// <c>__shield_depleted</c>, <c>__shield_recharging</c>, <c>__body_damaged</c>, <c>__body_depleted</c>,
/// <c>__body_destroyed</c>, <c>__area_damage</c>, <c>__localized_damage</c> from the object's
/// collision model. Materials are named as in <see cref="MaterialTypes"/>.</para>
/// <para>Decal parts are instances of the decal prefabs, <c>&lt;decal tag&gt;.prefab</c>, placed at their
/// location and scaled by the part's radius modifier.</para>
/// <para>Resources those use: <c>&lt;light volume tag&gt;__light_volume.vmdl</c> (<see cref="LightVolumeModel"/>)
/// <c>&lt;contrail tag&gt;__contrail.vmat</c> (<see cref="ContrailMaterial"/>) and <c>&lt;light tag&gt;__lens_flare.vmdl</c>
/// (<see cref="LensFlares.ForLight"/>; effect lights draw theirs fading with them), and the particles'
/// and particle system states' <see cref="ParticleSpriteMaterial"/>s, which the runtime draws them with.</para>
/// </remarks>
static class EffectPrefabs
{
	public const string Ident = "haloce";

	/// <summary>
	/// Registers a map's effects, particle sprites and the aliases above, unless an earlier
	/// map has registered them.
	/// </summary>
	public static void Register( MountContext context, CacheFile cache, HashSet<string> registered )
	{
		var mapPath = cache.FilePath;

		// The context can't be used in local functions (it's a ref struct), so loaders are collected first.
		var loaders = new List<(string Name, HaloCeLoader Loader)>();
		void Add( string name, HaloCeLoader loader )
		{
			if ( registered.Add( name ) )
				loaders.Add( (name, loader) );
		}

		void AddEffect( string name, TagEntry effect, EffectView view = EffectView.ThirdPerson, bool looping = false )
		{
			if ( effect is not null && effect.Class == EffectTagClasses.Effect && !string.IsNullOrEmpty( effect.Path ) )
				Add( $"{name}.prefab", new EffectPrefabLoader( mapPath, effect.Id, view, looping ) );
		}

		var attached = new HashSet<uint>();
		foreach ( var tag in cache.Tags.Where( t => t.Is( TagClasses.Object ) ) )
		{
			foreach ( var attachment in ObjectAttachments.Read( cache, tag ) )
				attached.Add( attachment.Type.Id );

			if ( !string.IsNullOrEmpty( tag.Path ) && ObjectAttachmentPlan.HasAny( cache, tag ) )
			{
				Add( $"{HaloCeMount.ResourcePath( tag.Path )}__attachments.prefab", new AttachmentsPrefabLoader( mapPath, tag.Id ) );

				// The first person variant, for every weapon and anything else that looks different there.
				if ( tag.Class == WeaponEffectsTag.Class || ObjectAttachmentPlan.DiffersInFirstPerson( cache, tag ) )
					Add( $"{HaloCeMount.ResourcePath( tag.Path )}__attachments_fp.prefab", new AttachmentsPrefabLoader( mapPath, tag.Id, firstPerson: true ) );
			}

			// Light volumes as models, with the function values of the first object carrying each.
			foreach ( var widget in ObjectAttachments.ReadWidgets( cache, tag ).Where( w => w.Class == LightVolumeTag.Class && !string.IsNullOrEmpty( w.Path ) ) )
				Add( $"{LightVolumeModel.ResourceName( widget.Path )}.vmdl", new LightVolumeModelLoader( mapPath, widget.Id, tag.Id ) );
		}

		var spriteBitmaps = new HashSet<uint>();
		foreach ( var tag in cache.Tags )
		{
			if ( string.IsNullOrEmpty( tag.Path ) )
				continue;

			// What the runtime plays effects and particle systems from (their tokens resolve to these).
			if ( tag.Class == EffectTagClasses.Effect || tag.Class == EffectTagClasses.ParticleSystem )
				HaloEffectLibrary.Register( cache, tag );

			var path = HaloCeMount.ResourcePath( tag.Path );
			if ( tag.Class == EffectTagClasses.Effect )
			{
				AddEffect( path, tag );
				if ( EffectPlan.HasCameraVariants( new EffectTag( cache, tag ) ) )
					AddEffect( $"{path}__fp", tag, EffectView.FirstPerson );
				if ( attached.Contains( tag.Id ) )
					AddEffect( $"{path}__loop", tag, looping: true );
			}
			else if ( tag.Class == EffectTagClasses.Light )
			{
				// Lens flares of lights that objects carry or effects create.
				if ( ObjectAttachmentPlan.DrawsLensFlare( cache, new LightTag( cache, tag ).LensFlare ) )
					Add( $"{LensFlares.ResourceName( tag.Path )}.vmdl", new LensFlareModelLoader( mapPath, tag.Id ) );
			}
			else if ( tag.Class == EffectTagClasses.Contrail )
			{
				Add( $"{ContrailMaterial.ResourceName( tag.Path )}.vmat", new ContrailMaterialLoader( mapPath, tag.Id ) );
			}
			else if ( tag.Class == EffectTagClasses.Particle )
			{
				if ( new ParticleTag( cache, tag ).Bitmap is { } bitmap )
				{
					spriteBitmaps.Add( bitmap.Id );
					Add( $"{ParticleSpriteMaterial.ResourceName( tag.Path )}.vmat", new ParticleSpriteMaterialLoader( mapPath, tag.Id ) );
				}
			}
			else if ( tag.Class == EffectTagClasses.ParticleSystem )
			{
				var types = new ParticleSystemTag( cache, tag ).Types;
				for ( var t = 0; t < types.Count; t++ )
				{
					for ( var s = 0; s < types[t].ParticleStates.Count; s++ )
					{
						if ( types[t].ParticleStates[s].Bitmap is not { } bitmap )
							continue;

						spriteBitmaps.Add( bitmap.Id );
						Add( $"{ParticleSpriteMaterial.ResourceName( tag.Path, t, s )}.vmat", new ParticleSpriteMaterialLoader( mapPath, tag.Id, t, s ) );
					}
				}
			}
			else if ( tag.Class == ProjectileTag.Class )
			{
				var projectile = new ProjectileTag( cache, tag );
				AddEffect( $"{path}__detonation", projectile.Detonation );
				AddEffect( $"{path}__detonation_started", projectile.DetonationStarted );
				AddEffect( $"{path}__super_detonation", projectile.SuperDetonation );
				foreach ( var response in projectile.MaterialResponses )
				{
					var material = MaterialTypes.Name( response.Material );
					if ( material is null )
						continue;

					AddEffect( $"{path}__impact_{material}", response.Effect );
					AddEffect( $"{path}__impact_{material}_potential", response.PotentialEffect );
					AddEffect( $"{path}__detonation_{material}", response.DetonationEffect );
				}

				// (__attachments is registered with every other object's above.)
				if ( ObjectAttachmentPlan.HasAny( cache, tag ) || ObjectTags.GetObjectModel( cache, tag ) is { } model && model.Class == TagClasses.GbxModel )
					Add( $"{path}__projectile.prefab", new AttachmentsPrefabLoader( mapPath, tag.Id, withModel: true ) );
			}
			else if ( tag.Class == WeaponEffectsTag.Class )
			{
				var weapon = new WeaponEffectsTag( cache, tag );
				AddEffect( $"{path}__ready", weapon.Ready );
				AddEffect( $"{path}__overheated", weapon.Overheated );
				AddEffect( $"{path}__detonation", weapon.Detonation );
				foreach ( var trigger in weapon.Triggers )
				{
					var name = $"{path}__trigger{trigger.Index}";
					AddEffect( $"{name}_charging", trigger.ChargingEffect );
					for ( var i = 0; i < trigger.Firing.Count; i++ )
					{
						var suffix = i == 0 ? "" : $"_{i}";
						var (fire, misfire, empty) = trigger.Firing[i];
						AddEffect( $"{name}_fire{suffix}", fire );
						if ( fire is not null && fire.Class == EffectTagClasses.Effect && EffectPlan.HasCameraVariants( new EffectTag( cache, fire ) ) )
							AddEffect( $"{name}_fire{suffix}_fp", fire, EffectView.FirstPerson );
						AddEffect( $"{name}_misfire{suffix}", misfire );
						AddEffect( $"{name}_empty{suffix}", empty );
					}
				}
			}

			if ( tag.Is( TagClasses.Object ) && CollisionModelEffects.Of( cache, tag ) is { } collision )
			{
				foreach ( var (name, effect) in new CollisionModelEffects( cache, collision ).Effects )
					AddEffect( $"{path}__{name}", effect );
			}
		}

		foreach ( var id in spriteBitmaps )
		{
			var bitmap = cache.GetTag( id );
			if ( bitmap is null || bitmap.Class != TagClasses.Bitmap || string.IsNullOrEmpty( bitmap.Path ) )
				continue;

			var sequences = Math.Max( 1, new BitmapTag( cache, bitmap ).Sequences.Count );
			for ( var i = 0; i < sequences; i++ )
				Add( EffectSpriteLoader.ResourceName( bitmap.Path, i ), new EffectSpriteLoader( mapPath, id, i ) );
		}

		foreach ( var (name, loader) in loaders )
		{
			var type = System.IO.Path.GetExtension( name ) switch
			{
				".prefab" => ResourceType.PrefabFile,
				".vmdl" => ResourceType.Model,
				".vmat" => ResourceType.Material,
				_ => ResourceType.GameResource,
			};
			context.Add( type, name.EndsWith( ".vmdl" ) || name.EndsWith( ".vmat" ) ? name[..^5] : name, loader );
		}
	}
}

/// <summary>
/// Builds a prefab with <see cref="PrefabBuilder"/>. The root is built disabled, so nothing plays
/// or simulates while it's put together (a sound would be heard), and saved enabled. Prefabs are
/// put together in a scene and load the resources they use, so they build on the main thread.
/// </summary>
abstract class HaloPrefabLoader : HaloCeLoader
{
	PrefabFile _prefab;

	protected abstract void Build( CacheFile cache, GameObject root );

	/// <summary>
	/// Loads what the prefab instances, before its scene is set up.
	/// </summary>
	protected virtual void Prepare( CacheFile cache ) { }

	protected override Task<object> LoadAsync()
	{
		// On the main thread, build right here (see GbxModelLoader).
		if ( ThreadSafe.IsMainThread )
			return Task.FromResult( Load() );

		return LoadOnMainThread();
	}

	async Task<object> LoadOnMainThread()
	{
		await GameTask.MainThread();
		return Load();
	}

	protected sealed override object Load()
	{
		if ( _prefab is not null )
			return _prefab;

		var cache = Host.GetCache( MapPath );
		Prepare( cache );

		var builder = new PrefabBuilder().WithName( Path );
		using ( builder.Scope() )
		{
			var root = new GameObject( false, Name );
			Build( cache, root );
		}

		_prefab = builder.Create();
		if ( _prefab?.RootObject is { } json )
			json["Enabled"] = true;

		return _prefab;
	}

	protected abstract string MapPath { get; }

	protected override void Shutdown()
	{
		PrefabBuilder.Destroy( _prefab );
		_prefab = null;
	}
}

sealed class EffectPrefabLoader( string mapPath, uint effectTagId, EffectView view, bool looping ) : HaloPrefabLoader
{
	EffectPlan _plan;
	Dictionary<uint, PrefabFile> _decals;

	protected override string MapPath => mapPath;

	protected override void Prepare( CacheFile cache )
	{
		_plan = new EffectPlan( cache, cache.GetTag( effectTagId ), view, looping );
		_decals = EffectBuilder.LoadDecalPrefabs( _plan );
	}

	protected override void Build( CacheFile cache, GameObject root )
	{
		var plan = _plan;
		EffectBuilder.Build( plan, root, _decals );

		// A one-shot effect's root goes once its events are over (its sounds and decals with it); a
		// looping one keeps the marker HaloEffects adds, which never removes it.
		if ( !looping )
			root.AddComponent<TemporaryEffect>().DestroyAfterSeconds = HaloEffects.LongestRun( plan.Effect ) + 0.1f;

		HaloEffects.Mark( root, plan.Tag, firstPerson: view == EffectView.FirstPerson, looping: looping );
		HaloParticleWorld.Watch();
	}
}

