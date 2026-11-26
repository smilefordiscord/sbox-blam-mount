using Blam.Halo1;
using Blam.Halo1.Tags;

namespace Sandbox.Mounting.Blam.Halo1;

/// <summary>
/// What an object carries, as a prefab a game parents to the object it spawns:
/// <c>mount://haloce/&lt;object tag&gt;__attachments.prefab</c> for every object (weapon, vehicle,
/// equipment, biped, scenery, machine, projectile...) with attachments or light volumes. For
/// projectiles there's also <c>__projectile.prefab</c> (withModel): the same plus
/// the projectile's model, the whole projectile in flight.
/// </summary>
/// <remarks>
/// <para>One child per attachment and place (<see cref="ObjectAttachmentPlan"/>), named
/// "&lt;model attachment&gt; / &lt;tag file&gt;.&lt;class&gt;" ("secondary trigger flare1 / overcharge.ligh",
/// "origin / ..." without a marker) and placed at that attachment of the object's model at its
/// default pose. A looping effect's locations are under it where Halo has them on the object at
/// that pose (see <see cref="EffectLocationSite"/>), which can be other markers than the child's.
/// Each is tagged with its kind, marker and scale functions (see
/// <see cref="ObjectAttachmentPlan"/>), and the object's tag document
/// (<c>tags/&lt;object&gt;.&lt;class&gt;.json</c>, <c>AttachmentsPrefab</c>) lists them with the base
/// values a game needs. On an animated object, move each child onto the model attachment it names
/// (the first person model's for a weapon held in first person: same names, see
/// <c>FirstPersonAttachment</c> in the document).</para>
/// <para>What each carries, built from engine components only:</para>
/// <list type="bullet">
/// <item>lights: a PointLight or SpotLight as the lights on placed objects
/// (<see cref="ObjectLights"/>), and the light's lens flare (<see cref="LensFlares.ForLight"/>,
/// <c>&lt;light tag&gt;__lens_flare.vmdl</c>) as a ModelRenderer (see <see cref="EffectBuilder.AddAttachedLight"/>);</item>
/// <item>looping sounds: a SoundPointComponent per track and detail sound, reverb off;</item>
/// <item>looping effects and particle systems: marked for <see cref="HaloParticleWorld"/>, which plays
/// them (<see cref="HaloEffects"/>), with an effect's locations, sounds and decals built under it;</item>
/// <item>contrails: trail renderers (tag <c>halo_contrail</c>; Halo removes a projectile's contrail
/// unless its weapon made it a tracer, so a game destroys those on the other shots);</item>
/// <item>light volumes: their model (<see cref="LightVolumeModel"/>).</item>
/// </list>
/// <para>Everything starts as Halo has it at rest (an unfired weapon, an empty vehicle; a
/// projectile in flight): at the rest value of its scale function. Lights take that value's color
/// and radius (a black or empty light's component is disabled) and their flares that scale (the
/// renderer's tint); everything else whose function is inactive at rest (a charge sound, a
/// vehicle's exhaust) is a disabled child, tagged <c>halo_off_at_rest</c>. A game drives a child
/// by its scale function's value v: lights through <c>LightColor</c> and <c>Radius</c>, flares
/// through the renderer attribute <c>HaloFunctionScale</c> (or the tint), and the rest by
/// enabling the child while the function is on (Halo: above its lower bound, i.e. v &gt; 0 for
/// most) and, for sounds, <c>Volume</c>; effects and particle systems also by the x of their
/// <see cref="HaloEffects.ControlsName"/> child's local position, which is v (the rest value to
/// start with), the scale A of <see cref="HaloEffects"/>.</para>
/// <para><c>__attachments_fp.prefab</c> (firstPerson) is the same for the player's own first person
/// view, with the same children: its looping effects and particle systems are tagged
/// <see cref="HaloEffects.FirstPersonTag"/>, so the runtime plays the effects' first person variants
/// (see <see cref="EffectView"/>) and draws the particle systems' types Halo only draws in first
/// person, leaving out those it doesn't draw there. A flamethrower's flames in first person come
/// from systems at "spawn fire", a marker only its first person model has, that aren't drawn for
/// anyone else; its third person flames leave from "defoliant spawn" and aren't drawn in first person.</para>
/// </remarks>
sealed class AttachmentsPrefabLoader( string mapPath, uint objectTagId, bool withModel = false, bool firstPerson = false ) : HaloPrefabLoader
{
	EffectView View => firstPerson ? EffectView.FirstPerson : EffectView.ThirdPerson;

	Model _model;
	List<AttachmentChild> _plan;
	readonly Dictionary<uint, Material> _contrails = new();
	readonly Dictionary<uint, Model> _lightVolumes = new();
	readonly Dictionary<uint, Model> _flares = new();

	protected override string MapPath => mapPath;

	public static bool HasAny( CacheFile cache, TagEntry tag ) => ObjectAttachmentPlan.HasAny( cache, tag );

	protected override void Prepare( CacheFile cache )
	{
		var tag = cache.GetTag( objectTagId );
		var mount = Sandbox.Mounting.Directory.Get( EffectPrefabs.Ident );
		bool Exists( string path ) => mount?.GetByPath( path ) is not null;

		_plan = ObjectAttachmentPlan.Plan( cache, tag );

		var modelTag = ObjectTags.GetObjectModel( cache, tag );
		if ( withModel && modelTag is not null && modelTag.Class == TagClasses.GbxModel && Exists( HaloCeMount.ModelPath( EffectPrefabs.Ident, modelTag.Path ) ) )
			_model = Model.Load( HaloCeMount.ModelPath( EffectPrefabs.Ident, modelTag.Path ) );

		foreach ( var child in _plan )
		{
			var type = child.Type;
			switch ( child.Kind )
			{
				case AttachmentKind.Contrail:
					var path = ContrailMaterial.FullPath( type.Path );
					if ( !_contrails.ContainsKey( type.Id ) && Exists( path ) )
						_contrails[type.Id] = Material.Load( path );
					break;

				case AttachmentKind.LightVolume:
					var volume = LightVolumeModel.FullPath( type.Path );
					if ( !_lightVolumes.ContainsKey( type.Id ) && Exists( volume ) && Model.Load( volume ) is { IsError: false } model )
						_lightVolumes[type.Id] = model;
					break;

				case AttachmentKind.Light or AttachmentKind.LensFlare when child.LensFlare is not null:
					if ( !_flares.ContainsKey( type.Id ) && EffectBuilder.LoadLensFlare( cache, type ) is { } flare )
						_flares[type.Id] = flare;
					break;
			}
		}
	}

	protected override void Build( CacheFile cache, GameObject root )
	{
		var tag = cache.GetTag( objectTagId );
		root.Tags.Add( "halo_attachments" );

		if ( _model is not null && !_model.IsError )
		{
			var renderer = root.AddComponent<ModelRenderer>();
			renderer.Model = _model;
		}

		foreach ( var child in _plan )
		{
			// Off at rest: built disabled, so nothing plays until the game turns it on. Lights stay
			// enabled: their light and flare follow the value instead.
			var lit = child.Kind is AttachmentKind.Light or AttachmentKind.LensFlare;
			var go = new GameObject( root, lit || child.OnAtRest, child.Name );
			go.LocalPosition = (Vector3)child.Position * HaloCeMount.WorldScale;
			go.LocalRotation = GbxSkeleton.ToRotation( child.Rotation );
			foreach ( var name in child.Tags )
				go.Tags.Add( name );

			var type = child.Type;
			try
			{
				switch ( child.Kind )
				{
					case AttachmentKind.Light:
					case AttachmentKind.LensFlare:
						EffectBuilder.AddAttachedLight( cache, type, go, child.RestValue, _flares.GetValueOrDefault( type.Id ), withLight: child.Kind == AttachmentKind.Light );
						break;

					case AttachmentKind.Effect:
						EffectBuilder.Build( new EffectPlan( cache, type, View, looping: true ), go, null, child.Locations );
						EffectBuilder.PlaceAttachedLocations( go, go.LocalTransform, child.Locations );
						HaloEffects.Mark( go, type, firstPerson: firstPerson, looping: true, attached: true, a: child.RestValue );
						break;

					case AttachmentKind.ParticleSystem:
						HaloEffects.Mark( go, type, firstPerson: firstPerson, looping: true, attached: true, a: child.RestValue );
						break;

					case AttachmentKind.Contrail:
						var speed = tag.Class == ProjectileTag.Class ? new ProjectileTag( cache, tag ).InitialVelocity : 0;
						var contrail = new ContrailTag( cache, type );
						EffectBuilder.AddContrail( contrail, EffectPlan.IsAdditive( contrail.BlendFunction ), go, _contrails.GetValueOrDefault( type.Id ), speed );
						break;

					case AttachmentKind.LoopingSound:
						AddLoopingSound( type, go );
						break;

					case AttachmentKind.LightVolume:
						if ( _lightVolumes.TryGetValue( type.Id, out var model ) )
							EffectBuilder.AddLightVolume( model, go );
						break;
				}
			}
			catch ( Exception e )
			{
				HaloCeMount.Current?.Logger.Warning( $"{tag}: couldn't build attachment {type}: {e.Message}" );
			}
		}

		if ( _plan.Any( c => c.Kind is AttachmentKind.Effect or AttachmentKind.ParticleSystem ) )
			HaloParticleWorld.Watch();
	}

	static void AddLoopingSound( TagEntry type, GameObject go )
	{
		var resources = HaloCeMount.Current?.Sounds?.GetLoopingSound( type.Path );
		if ( resources is null )
			return;

		AmbientSoundBuilder.AddLoopingSound( go, resources );
		foreach ( var sound in go.Components.GetAll<SoundPointComponent>() )
			EffectBuilder.Dry( sound );
	}
}

/// <summary>
/// Loads a light's lens flare model (see <see cref="LensFlares.ForLight"/>).
/// </summary>
sealed class LensFlareModelLoader( string mapPath, uint lightTagId ) : HaloCeLoader
{
	protected override object Load()
	{
		var cache = Host.GetCache( mapPath );
		var tag = cache.GetTag( lightTagId );
		return tag is null ? null : LensFlares.ForLight( Host, Path, cache, tag );
	}
}
