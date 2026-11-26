using System;
using Halo;
using Halo.Combat;
using Halo.Data;

namespace HaloWeapons;

// A Halo CE weapon in Sandbox: its weapon tag (WeaponTag) from the mount's game data, fired by Halo's own rules.
// The owner runs halo1sbox's CombatSim at Halo's 30 Hz (triggers, rate of fire ramps, spread, heat, batteries, magazines,
// chambering, zoom, melee and grenade throws) and every shot is Halo projectiles (ProjectileSimulation), simulated on
// every machine from the same seed; only the host's copy deals damage.
// Keys: attack1 fire, attack2 zoom, reload, F melee, middle mouse grenade.
[Title( "Halo Weapon" ), Icon( "sports_esports" )]
public sealed partial class HaloWeapon : HaloSandboxWeapon, ICombatEvents {
	// The weapon tag, e.g. "weapons/pistol/pistol.weap".
	[Property] public string WeaponTag { get; set; } = "weapons/pistol/pistol.weap";

	// Where the third person model sits on the holder's hold bone.
	[Property, Feature( "WorldModel" )] public Vector3 HoldOffset { get; set; }
	[Property, Feature( "WorldModel" )] public Angles HoldAngles { get; set; }

	public WeaponTag Tag => HaloDataStore.Game?.Weapon( WeaponTag );

	// The magazine and battery, kept on the weapon so a dropped gun keeps them. -1 until first picked up.
	[Sync] public int Loaded { get; set; } = -1;
	[Sync] public int Reserve { get; set; } = -1;
	[Sync] public float Age { get; set; }
	[Sync] public float Heat { get; set; }

	CombatState _state = CombatState.Empty;
	readonly CombatSim _sim = new() { Data = GameCombatData.Instance };
	readonly FiringEffectCycle[] _firingEffects = { new(), new() };
	HaloViewModel _view;
	ushort _pressed;
	TimeSince _sinceFired = 100;

	// The weapon's A-D function outputs, for its gauges, glows and attachments.
	public float?[] FunctionOutputs => WeaponFunctions.Outputs( WeaponTag, Tag, new WeaponFunctions.Inputs {
		Heat = _state.Slot0.Heat,
		Age = _state.Slot0.Age,
		SinceFired = _sinceFired,
		Charge = _state.Slot0.Trigger0.Charge,
		Loaded = _state.Slot0.Loaded,
		PrimaryFiring = _sinceFired < 0.07f,
		Compass = WeaponFunctions.Compass( this, EyeAngles.Forward ),
	} );

	public HaloWeapon() {
		UsesAmmo = true;
		DeployTime = 0;
		PrimaryAutomatic = false;
	}

	// What the view model asks.
	public bool ReloadsByShell => Tag?.Magazine( 0 ) is { RoundsReloaded: > 0 } m && m.RoundsReloaded < m.RoundsLoadedMaximum;
	public bool Reloading => _state.Slot0.Magazine == MagazineState.Reloading;
	public bool Overheated => _state.Slot0.Overheated;
	public bool OverheatedExit => _state.Slot0.OverheatedExit;
	public bool Overcharged => _state.Slot0.Overcharged;
	public int ZoomLevel => _state.ZoomLevel;
	public Vector3 EyePosition => Owner?.EyeTransform.Position ?? WorldPosition;
	public Angles EyeAngles => Owner?.Controller?.EyeAngles ?? WorldRotation.Angles();
	public bool OwnerGrounded => Owner?.Controller?.IsOnGround ?? true;
	public bool OwnerMoving => OwnerGrounded && (Owner?.Controller?.Velocity.WithZ( 0 ).Length ?? 0) > HaloUnits.ToInches( 0.5f );

	// What Halo's weapon HUD (HaloWeaponHud) reads, on the owner.
	public bool WantsFire => (_state.Buttons & CombatButtons.Fire) != 0;
	public bool WantsGrenade => (_state.Buttons & CombatButtons.Grenade) != 0;
	public bool IsReady => _state.ReadyTicks <= 0;
	public bool NightVisionOn => _state.NightVision;
	public int SelectedGrenade => _state.SelectedGrenade;
	public int Frags => HaloGrenades.For( Owner )?.Frags ?? 0;
	public int Plasmas => HaloGrenades.For( Owner )?.Plasmas ?? 0;

	// The unit the weapon's autoaim is fully on (level 1), which turns Halo's reticle red.
	public IHaloUnit AimTarget() {
		var owner = Owner;
		if ( owner is null ) return null;
		var eye = owner.EyeTransform;
		var target = AutoAim.FindTarget( Scene, OwnerUnit, Tag, _state.ZoomLevel >= 0, Magnification, eye.Position, eye.Forward, out _, out var level );
		return level >= 1 ? target : null;
	}

	// The zoom's magnification, 1 unzoomed.
	public float Magnification {
		get {
			var tag = Tag;
			if ( _state.ZoomLevel < 0 || tag is not { ZoomLevels: > 0 } || tag.ZoomMagnificationRange is not { Length: > 1 } range ) return 1;
			var t = tag.ZoomLevels > 1 ? (float)_state.ZoomLevel / (tag.ZoomLevels - 1) : 0;
			return MathF.Max( 1, range[0].LerpTo( range[1], t ) );
		}
	}

	protected override void OnAwake() {
		base.OnAwake();
		HoldType = HoldTypeOf( Tag );
		SetUpAmmo();
		if ( string.IsNullOrEmpty( DisplayName ) ) DisplayName = WorldModelName();
		SetUpDropped();
	}

	// Sandbox's ammo panel shows the Halo magazine (HaloWeapon.GetReserveAmmo); set on every wake and equip so no saved or hot-reloaded weapon misses it.
	void SetUpAmmo() {
		var magazine = Tag?.Magazine( 0 );
		UsesAmmo = true;
		UsesClips = magazine is { RoundsLoadedMaximum: > 0 };
		ClipMaxSize = magazine?.RoundsLoadedMaximum ?? 0;
		if ( UsesClips && Networking.IsHost && Loaded >= 0 ) Clip1 = Loaded;
	}

	// Pistol-like weapons are held in one hand, launchers on the shoulder.
	static string HoldTypeOf( WeaponTag tag ) => tag?.Label switch {
		"pistol" or "plasma pistol" or "needler" => "Pistol",
		"rocket launcher" or "plasma cannon" or "fuel rod gun" => "RPG",
		"shotgun" => "Shotgun",
		_ => "Rifle",
	};

	string WorldModelName() {
		var name = WeaponTag?.Replace( '\\', '/' ) ?? "";
		name = name[(name.LastIndexOf( '/' ) + 1)..];
		var dot = name.LastIndexOf( '.' );
		return dot > 0 ? name[..dot] : name;
	}

	// Lying in the world it's the Halo model at multiplayer's item scale, with a box around it to land on.
	void SetUpDropped() {
		var model = HaloResources.LoadModel( Tag?.Model );
		if ( model is null ) return;

		if ( !DroppedGameObject.IsValid() ) {
			DroppedGameObject = new GameObject( GameObject, true, "dropped" );
			DroppedGameObject.Flags |= GameObjectFlags.NotNetworked;
			var renderer = DroppedGameObject.AddComponent<ModelRenderer>();
			renderer.Model = model;
		}

		var scale = Tag?.Scale is > 0 and var s ? s : 1;
		DroppedGameObject.LocalScale = scale;
		var box = GetOrAddComponent<BoxCollider>();
		var bounds = model.Bounds;
		box.Center = bounds.Center * scale;
		box.Scale = Vector3.Max( bounds.Size * scale, 2 );
		GetOrAddComponent<Rigidbody>();
	}

	protected override void OnUpdate() {
		base.OnUpdate();
		TickCombat();
		if ( !IsProxy && IsActive && Owner is not null ) Halo.UI.HaloWeaponHud.Ensure( Scene );
		if ( GetComponent<BoxCollider>( true ) is { } box ) box.Enabled = IsDropped;
		// The dropped model can be made after the weapon was already picked up, which Sandbox's dropped state change never hides.
		if ( DroppedGameObject.IsValid() ) DroppedGameObject.Enabled = IsDropped;
		PoseBody();
	}

	protected override void OnDisabled() {
		ReleaseBody();
		base.OnDisabled();
	}

	// Host: a gun picked up for the first time comes loaded with the tag's starting rounds.
	protected override void OnAdded( BaseInventoryComponent inventory ) {
		base.OnAdded( inventory );
		if ( Loaded >= 0 ) return;
		var magazine = Tag?.Magazine( 0 );
		Loaded = magazine?.RoundsLoadedMaximum ?? 0;
		Reserve = magazine is null ? 0 : Math.Max( 0, magazine.RoundsTotalInitial - magazine.RoundsLoadedMaximum );
	}

	// Equipping readies it again, as Halo readies a weapon switched to.
	protected override void OnEquipped() {
		_state = CombatState.Empty;
		SetUpAmmo();
		base.OnEquipped();
	}

	protected override void OnHolstered() {
		ReleaseBody();
		if ( !string.IsNullOrEmpty( _state.Slot0.Weapon ) ) {
			_sim.PutAway( ref _state, 0 );
			Publish();
		}
		base.OnHolstered();
	}

	// first person

	protected override void CreateViewModel() {
		DestroyViewModel();
		if ( IsProxy || Owner is null ) return;
		_view = new HaloViewModel( this );
	}

	protected override void DestroyViewModel() {
		_view?.Destroy();
		_view = null;
		base.DestroyViewModel();
	}

	// Sandbox's third person camera, or any camera that leaves out first person objects (a camera entity's view), as the engine's weapons decide.
	public static bool SeenFromOutside( PlayerController controller, Scene scene ) =>
		(controller?.ThirdPerson ?? false) || (scene.Camera.IsValid() && scene.Camera.RenderExcludeTags.Has( "firstperson" ));

	protected override void PlaceViewModel( CameraComponent camera, in CameraView view ) {
		var thirdPerson = SeenFromOutside( Owner?.Controller, Scene );
		HaloView.ThirdPerson = thirdPerson;
		_view?.Place( view, !thirdPerson );
	}

	// Zoom narrows the view by the magnification, as Halo's does.
	// The unzoomed vertical field of view: Sandbox's own, which Halo weapons keep.
	public static float VerticalFieldOfView => Screen.CreateVerticalFieldOfView( Preferences.FieldOfView, 9.0f / 16.0f );

	protected override void ModifyCamera( CameraComponent camera, ref CameraView view ) {
		// Halo's camera never rolls; Sandbox tilts it when strafing, which swung the Halo weapon drawn at it.
		view.Rotation = view.Rotation.Angles().WithRoll( 0 ).ToRotation();

		// Halo CE has no camera recoil: the kick is the fire animation plus each shot's firing damage shaking the camera.
		// It shakes in camera space on top of the final camera, and the weapon draws from the shaken camera, so it holds still on screen.
		var (shakeRotation, shakeOffset) = CameraShake.Sample();
		view.Position += view.Rotation * shakeOffset;
		view.Rotation *= shakeRotation;


		var magnification = Magnification;
		if ( magnification > 1 )
			view.FieldOfView = 2 * MathF.Atan( MathF.Tan( view.FieldOfView.DegreeToRadian() / 2 ) / magnification ).RadianToDegree();
	}

	public override void OnCameraMove( PlayerController player, ref Angles angles ) {
		var magnification = Magnification;
		if ( magnification > 1 ) angles /= magnification;
	}

	// third person

	protected override void CreateWorldModel() {
		var renderer = GetComponentInParent<PlayerController>()?.Renderer;
		var model = HaloResources.LoadModel( Tag?.Model );
		if ( renderer is null || model is null ) return;

		if ( Networking.IsHost ) IsDropped = false;

		// Placed every frame on the posed hand (PoseBody).
		var go = new GameObject( renderer.GameObject, true, "halo world model" );
		go.Flags |= GameObjectFlags.NotSaved | GameObjectFlags.NotNetworked;
		// The engine's first person body copies everything under the body except what's under the hold bones or tagged this; it still casts its shadow.
		go.Tags.Add( "firstperson-hidden" );
		var held = go.AddComponent<ModelRenderer>();
		held.Model = model;
		held.CreateAttachments = true;
		WorldModel = go;
		PlaceOnHoldBone( renderer );
	}

	// input and the combat step

	// Read every frame on the owner; presses wait for the next Halo tick so none are lost between ticks.
	protected override void OnControl() {
		_pressed |= Buttons();
	}

	static ushort _held;
	static RealTimeUntil _heldFor;

	// Holds Halo buttons (CombatButtons bits: 1 fire, 2 zoom, 4 melee, 8 grenade, 16 reload) as if pressed, for testing without a keyboard.
	[ConCmd( "halo_hold" )]
	public static void Hold( int buttons, float seconds ) {
		_held = (ushort)buttons;
		_heldFor = seconds;
	}

	// The buttons halo_hold is holding, 0 when none.
	public static ushort TestButtons => _heldFor > 0 ? _held : (ushort)0;

	static ushort Buttons() {
		ushort b = TestButtons;
		if ( Input.Down( "attack1" ) ) b |= CombatButtons.Fire;
		if ( Input.Down( "attack2" ) ) b |= CombatButtons.Zoom;
		if ( Input.Down( "reload" ) ) b |= CombatButtons.Reload;
		if ( Input.Keyboard.Down( "f" ) ) b |= CombatButtons.Melee;
		if ( Input.Keyboard.Down( "mouse3" ) ) b |= CombatButtons.Grenade;
		return b;
	}

	// One combat step per Halo tick, on the frame clock (HaloTick).
	void TickCombat() {
		if ( IsProxy || !IsActive || Owner is null || Tag is null ) return;
		for ( var i = 0; i < HaloTick.Advanced; i++ ) Step();
	}

	void Step() {
		if ( string.IsNullOrEmpty( _state.Slot0.Weapon ) ) {
			_state.Slot0 = WeaponSlotState.For( WeaponTag, Math.Max( 0, Loaded ), Math.Max( 0, Reserve ), Age );
			_state.Slot0.Heat = Heat;
			_state.ActiveSlot = _state.DesiredSlot = 0;
		}

		var grenades = HaloGrenades.For( Owner );
		if ( grenades is not null ) (_state.Frags, _state.Plasmas) = (grenades.Frags, grenades.Plasmas);
		if ( _state.SelectedGrenade == 0 && _state.Frags == 0 && _state.Plasmas > 0 ) _state.SelectedGrenade = 1;
		else if ( _state.SelectedGrenade == 1 && _state.Plasmas == 0 && _state.Frags > 0 ) _state.SelectedGrenade = 0;

		var buttons = (ushort)(_pressed | Buttons());
		_pressed = 0;
		_sim.Tick( ref _state, buttons, default, this );
		_view?.Tick();

		if ( grenades is not null && (grenades.Frags != _state.Frags || grenades.Plasmas != _state.Plasmas) )
			grenades.Set( _state.Frags, _state.Plasmas );
		Publish();
	}

	// Sandbox's ammo panel shows the magazine as the clip and the rounds in reserve, or the battery's charge in percent.
	protected override int GetReserveAmmo( BaseAmmoResource ammoType ) =>
		UsesClips ? Math.Max( 0, Reserve ) : (int)MathF.Round( Math.Clamp( 1 - Age, 0, 1 ) * 100 );

	[Rpc.Host]
	void HostSetClip( int loaded ) {
		if ( Rpc.Caller == Network.Owner ) Clip1 = loaded;
	}

	void Publish() {
		if ( UsesClips && Clip1 != _state.Slot0.Loaded ) {
			if ( Networking.IsHost ) Clip1 = _state.Slot0.Loaded;
			else HostSetClip( _state.Slot0.Loaded );
		}
		Loaded = _state.Slot0.Loaded;
		Reserve = _state.Slot0.Reserve;
		Age = _state.Slot0.Age;
		Heat = _state.Slot0.Heat;
	}

	// shots

	IHaloUnit OwnerUnit => Owner is { } player ? SandboxUnit.For( player.GameObject ) : null;

	// Projectile keys and shot seeds tell shooters apart by their connection.
	int Shooter => Math.Abs( (Network.Owner?.Id ?? Guid.Empty).GetHashCode() ) % 4096;

	void ICombatEvents.Fired( int slot, int triggerIndex, int shotCount, float error ) {
		var tag = Tag;
		var trigger = tag?.Triggers.ElementAtOrDefault( triggerIndex );
		var owner = Owner;
		if ( trigger is null || owner is null ) return;

		var aim = owner.EyeTransform.Rotation;
		var eye = owner.EyeTransform.Position;
		var origin = MuzzleOrigin( trigger, aim, eye );
		// Autoaim bends the shot toward its target, and homing projectiles steer at it.
		var target = AutoAim.FindTarget( Scene, OwnerUnit, tag, _state.ZoomLevel >= 0, Magnification, eye, aim.Forward, out var deflected, out _ );
		var tick = (int)HaloTick.Count;
		var seed = TracerSeed( CombatSim.ShotSeed( tick, Shooter, triggerIndex ), IsTracer( trigger, shotCount ) );
		var inherit = owner.Controller?.Velocity ?? Vector3.Zero;
		var targetId = target?.GameObject.Id ?? Guid.Empty;

		_sinceFired = 0;
		Shoot( triggerIndex, shotCount, origin, deflected, inherit, seed, error, targetId, tick );
		BroadcastShot( triggerIndex, shotCount, origin, deflected, inherit, seed, error, targetId, tick );
		_view?.Fired( triggerIndex );
	}

	// Every other machine fires the same pellets from the same seed; the host's copy deals the damage.
	[Rpc.Broadcast( NetFlags.Unreliable )]
	void BroadcastShot( int triggerIndex, int shotCount, Vector3 origin, Vector3 aim, Vector3 inherit, int seed, float error, Guid target, int tick ) {
		if ( Rpc.Caller == Connection.Local ) return;
		Shoot( triggerIndex, shotCount, origin, aim, inherit, seed, error, target, tick );
	}

	void Shoot( int triggerIndex, int shotCount, Vector3 origin, Vector3 aim, Vector3 inherit, int seed, float error, Guid target, int tick ) {
		var tag = Tag;
		var trigger = tag?.Triggers.ElementAtOrDefault( triggerIndex );
		if ( trigger is null ) return;

		var ownerId = Owner?.GameObject.Id ?? Guid.Empty;
		var sim = ProjectileSimulation.Get( Scene );
		foreach ( var launch in ShotLaunches.Build( tag, trigger, origin, aim, seed, error, inherit, target, ownerId, WeaponTag, ProjectileKey.Shot( Shooter, tick, triggerIndex ) ) )
			sim?.Spawn( launch, Networking.IsHost );

		_body?.Animation.Fired( triggerIndex );
		PlayFiringEffect( _firingEffects[Math.Clamp( triggerIndex, 0, 1 )].Next( trigger ), origin, aim );
		if ( !IsProxy ) CameraShake.Apply( HaloEffects.FiringDamage( trigger, shotCount ) );
		Owner?.Controller?.Renderer?.Animation.Parameters.TrySet( "b_attack", true );
	}

	// The camera, moved forward to the weapon marker's depth, plus the trigger's first person offset, as Halo fires from the camera.
	Vector3 MuzzleOrigin( TriggerTag trigger, Rotation aim, Vector3 eye ) {
		var forward = aim.Forward;
		var origin = eye;
		if ( HaloView.LocalMuzzle is { } muzzle && !HaloView.ThirdPerson )
			origin = eye + forward * MathF.Max( 0, Vector3.Dot( muzzle.Position - eye, forward ) );

		if ( trigger.FirstPersonOffset is { Length: >= 3 } o ) {
			var left = Vector3.Cross( Vector3.Up, forward );
			left = left.LengthSquared > 0.0001f ? left.Normal : Vector3.Left;
			var up = Vector3.Cross( forward, left ).Normal;
			origin += forward * HaloUnits.ToInches( o[0] ) + left * HaloUnits.ToInches( o[1] ) + up * HaloUnits.ToInches( o[2] );
		}

		// Never from inside a wall.
		var tr = Scene.Trace.Ray( eye, origin ).IgnoreGameObjectHierarchy( Owner?.GameObject ).WithoutTags( "playercontroller", "trigger", "viewmodel" ).Run();
		return tr.Hit ? tr.HitPosition - forward * 2 : origin;
	}

	// Halo draws a tracer contrail only every few rounds; the seed's low bit says which, so everyone draws the same.
	static bool IsTracer( TriggerTag trigger, int shotCount ) => shotCount <= 1 || (shotCount - 1) % (Math.Max( 0, trigger.ProjectilesBetweenContrails ) + 1) == 0;
	static int TracerSeed( int seed, bool tracer ) => (seed & ~1) | (tracer ? 1 : 0);

	// The firing effect from the drawn muzzle: the first person weapon's for the owner, the held model's for everyone else.
	void PlayFiringEffect( string effect, Vector3 origin, Vector3 forward ) {
		if ( effect is null ) return;
		var own = !IsProxy;
		var muzzle = own && !HaloView.ThirdPerson && HaloView.LocalMuzzle is { } m ? m.Position
			: WorldModel.IsValid() && WorldModel.GetComponent<ModelRenderer>()?.GetAttachmentObject( "primary trigger" ) is { } held && held.IsValid() ? held.WorldPosition
			: origin;
		HaloEffects.Play( effect, muzzle, forward, source: this, own: own );
	}

	void PlayWeaponEffect( string effect ) {
		if ( effect is null ) return;
		HaloEffects.Play( effect, EyePosition, EyeAngles.Forward, source: this, own: !IsProxy );
	}

	void ICombatEvents.DryFired( int slot, int trigger ) =>
		PlayWeaponEffect( Tag?.Triggers.ElementAtOrDefault( trigger )?.FiringEffects?.FirstOrDefault()?.EmptyEffect );

	void ICombatEvents.Overheated( bool superRecoil ) {
		_view?.Overheated( superRecoil );
		PlayWeaponEffect( Tag?.Overheated );
	}

	void ICombatEvents.ReloadStarted( int slot, bool inHand, bool empty, bool firstShell ) {
		if ( !inHand ) return;
		PlayWeaponEffect( Tag?.Magazine( 0 )?.ReloadingEffect );
		if ( firstShell ) _view?.ReloadStarted( empty );
		else _view?.ReloadShell();
		BodyAction( UnitAnimation.PrimaryReload );
		Owner?.Controller?.Renderer?.Animation.Parameters.TrySet( "b_reload", true );
	}

	void ICombatEvents.ReloadEnded( bool full ) => _view?.ReloadFinished();

	void ICombatEvents.Chambered( int slot, bool inHand ) {
		if ( inHand ) PlayWeaponEffect( Tag?.Magazine( 0 )?.ChamberingEffect );
	}

	void ICombatEvents.Readied( string weapon ) => PlayWeaponEffect( Tag?.ReadyEffect );

	// melee

	void ICombatEvents.MeleeStarted() {
		_view?.Meleed();
		BodyAction( UnitAnimation.Melee );
		Owner?.Controller?.Renderer?.Animation.Parameters.TrySet( "b_attack", true );
	}

	// The key frame: the host deals the damage, the owner hears its own hit at once.
	void ICombatEvents.MeleeStruck() {
		var owner = Owner;
		if ( owner is null ) return;
		var eye = owner.EyeTransform.Position;
		var aim = owner.EyeTransform.Forward;
		var velocity = owner.Controller?.Velocity ?? Vector3.Zero;
		var longFall = !(owner.Controller?.IsOnGround ?? true) && velocity.z < -HaloUnits.ToInches( 3 );

		if ( !Networking.IsHost ) {
			var seen = HaloCombat.MeleeTrace( Scene, OwnerUnit, eye, aim );
			HaloCombat.MeleeSound( owner.GameObject, seen.Material, MeleeDamageEffect, own: true );
		}
		HostMelee( eye, aim, velocity, longFall );
	}

	string MeleeDamageEffect => Tag?.PlayerMeleeDamage ?? HaloDataStore.Spartan.Biped?.MeleeDamage;

	[Rpc.Host]
	void HostMelee( Vector3 eye, Vector3 aim, Vector3 velocity, bool longFall ) {
		if ( Owner is not { } owner || Rpc.Caller != owner.Network.Owner && !(Rpc.Caller?.IsHost ?? true) ) return;
		var hit = HaloCombat.Melee( Scene, OwnerUnit, eye, velocity, longFall, aim, MeleeDamageEffect );
		if ( hit.Material >= 0 ) MeleeSoundEveryone( hit.Material );
	}

	[Rpc.Broadcast( NetFlags.Unreliable )]
	void MeleeSoundEveryone( int material ) {
		if ( Owner is not { } owner || (!owner.IsProxy && !Networking.IsHost) ) return;
		HaloCombat.MeleeSound( owner.GameObject, material, MeleeDamageEffect, own: !owner.IsProxy );
	}

	// grenades

	void ICombatEvents.ThrowStarted( int type ) {
		_view?.Threw();
		BodyAction( ThrowAction );
		Owner?.Controller?.Renderer?.Animation.Parameters.TrySet( "b_attack", true );
		var effect = HaloDataStore.Game?.Globals?.Grenades.ElementAtOrDefault( type )?.ThrowingEffect;
		PlayWeaponEffect( effect );
	}

	// The grenade launches from the camera offset by the globals' grenade origin, pulled back if the level is in the way,
	// at the biped's grenade velocity along the aim.
	void ICombatEvents.GrenadeReleased( int type ) {
		var globals = HaloDataStore.Game?.Globals;
		var info = globals?.Grenades.ElementAtOrDefault( type );
		if ( info is null || Owner is not { } owner ) return;

		var aim = owner.EyeTransform.Rotation;
		var eye = owner.EyeTransform.Position;
		var origin = eye;
		if ( globals.Player?.GrenadeOrigin is { Length: >= 3 } o )
			origin += aim.Forward * HaloUnits.ToInches( o[0] ) + aim.Left * HaloUnits.ToInches( o[1] ) + aim.Up * HaloUnits.ToInches( o[2] );
		if ( ProjectileSimulation.Get( Scene )?.WorldTrace( eye, origin ) is { Hit: true, StartedSolid: false } blocked )
			origin = blocked.HitPosition + blocked.Normal * ProjectileSimulation.SurfaceSkin;

		var speed = HaloUnits.PerSecondToInchesPerSecond( HaloDataStore.Spartan.Biped?.GrenadeVelocity ?? 10 );
		var tick = (int)HaloTick.Count;
		Throw( type, origin, aim.Forward * speed, tick );
		BroadcastThrow( type, origin, aim.Forward * speed, tick );
	}

	[Rpc.Broadcast]
	void BroadcastThrow( int type, Vector3 origin, Vector3 velocity, int tick ) {
		if ( Rpc.Caller == Connection.Local ) return;
		Throw( type, origin, velocity, tick );
	}

	void Throw( int type, Vector3 origin, Vector3 velocity, int tick ) {
		var info = HaloDataStore.Game?.Globals?.Grenades.ElementAtOrDefault( type );
		if ( info is null ) return;
		ProjectileSimulation.Get( Scene )?.Spawn( new ProjectileLaunch {
			Projectile = info.Projectile,
			Origin = origin,
			Velocity = velocity,
			Owner = Owner?.GameObject.Id ?? Guid.Empty,
			Weapon = info.Equipment,
			Thrown = true,
			Seed = CombatSim.ShotSeed( tick, Shooter, 4 ),
			Key = ProjectileKey.Grenade( Shooter, tick ),
		}, Networking.IsHost );
	}

	void ICombatEvents.GrenadeToHand( int type ) { }
	void ICombatEvents.ThrowEnded() { }
	void ICombatEvents.DropObjective() { }
	void ICombatEvents.Action() { }
	void ICombatEvents.Swap() { }
	void ICombatEvents.FlashlightRefused() { }
	void ICombatEvents.GunFired( int trigger, int shotCount, float error ) { }

	// HUD

	// Halo's own crosshairs draw instead (HaloWeaponHud); this plain one only stands in when the mount's HUD data can't be read.
	public override void DrawCrosshair( Painter painter, Vector2 center ) {
		if ( Halo.UI.HaloWeaponHud.DrawsCrosshair ) return;
		using var scope = painter.Scope();
		painter.BlendMode = BlendMode.Lighten;
		var color = new Color( 0.45f, 0.75f, 1f, 0.9f );
		painter.Stroke = Stroke.Solid( color, 2 );
		var radius = 10 + MathF.Max( 0, _state.Slot0.Trigger0.Error ) * 24;
		painter.Circle( center, radius );
		painter.Fill = Fill.Solid( color );
		painter.Circle( center, 1.5f );
	}

}
