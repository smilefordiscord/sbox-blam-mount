using System;
using Halo2.Combat;
using Halo2.Data;

namespace HaloWeapons;

// A Halo 2 weapon in Sandbox: its weapon tag from the Halo 2 mount's game data, fired by Halo 2's own rules.
// The owner runs halo2sbox's CombatSim at Halo's 30 Hz on the frame clock (triggers, barrels, spread, heat, batteries, magazines,
// zoom, melee and grenades) and every shot is Halo 2 projectiles, simulated on every machine from the same seed; only the host's copy deals damage.
// The first person weapon is halo2sbox's SpartanView. Keys: attack1 fire, attack2 zoom, reload, F melee, middle mouse grenade.
[Title( "Halo 2 Weapon" ), Icon( "sports_esports" )]
public sealed partial class Halo2Weapon : HaloSandboxWeapon, ICombatEvents, IHalo2Hands {
	// The weapon tag, e.g. "objects/weapons/rifle/battle_rifle/battle_rifle.weap".
	[Property] public string WeaponTag { get; set; } = "objects/weapons/rifle/battle_rifle/battle_rifle.weap";

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
	SpartanView _view;
	ushort _pressed;
	TimeSince _sinceFired = 100;
	TimeSince _sinceReadied;
	int _lastBarrel;
	int _soundedZoom = -1;

	public Halo2Weapon() {
		UsesAmmo = true;
		DeployTime = 0;
		PrimaryAutomatic = false;
	}

	protected override void OnAwake() {
		base.OnAwake();
		HoldType = HoldTypeOf( Tag );
		SetUpAmmo();
		if ( string.IsNullOrEmpty( DisplayName ) ) DisplayName = WorldModelName();
		SetUpDropped();
	}

	void SetUpAmmo() {
		var magazine = Tag?.Magazine( 0 );
		UsesAmmo = true;
		UsesClips = magazine is { RoundsLoadedMaximum: > 0 };
		ClipMaxSize = magazine?.RoundsLoadedMaximum ?? 0;
		if ( UsesClips && Networking.IsHost && Loaded >= 0 ) Clip1 = Loaded;
	}

	// Pistols and the sword in one hand, the heavy weapons on the shoulder, by the tag's folder.
	string HoldTypeOf( WeaponTag tag ) {
		var path = WeaponTag?.Replace( '\\', '/' ) ?? "";
		if ( path.Contains( "/pistol/" ) || (tag?.MeleeOnly ?? false) ) return "Pistol";
		if ( path.Contains( "/support_" ) ) return "RPG";
		if ( path.Contains( "/shotgun/" ) ) return "Shotgun";
		return "Rifle";
	}

	string WorldModelName() {
		var name = WeaponTag?.Replace( '\\', '/' ) ?? "";
		name = name[(name.LastIndexOf( '/' ) + 1)..];
		var dot = name.LastIndexOf( '.' );
		return dot > 0 ? name[..dot] : name;
	}

	// Lying in the world it's the Halo 2 model with a box around it to land on.
	void SetUpDropped() {
		var model = HaloResources.LoadModel( Tag?.Model );
		if ( model is null ) return;

		if ( !DroppedGameObject.IsValid() ) {
			DroppedGameObject = new GameObject( GameObject, true, "dropped" );
			DroppedGameObject.Flags |= GameObjectFlags.NotNetworked;
			var renderer = DroppedGameObject.AddComponent<ModelRenderer>();
			renderer.Model = model;
		}

		var box = GetOrAddComponent<BoxCollider>();
		var bounds = model.Bounds;
		box.Center = bounds.Center;
		box.Scale = Vector3.Max( bounds.Size, 2 );
		GetOrAddComponent<Rigidbody>();
	}

	protected override void OnUpdate() {
		base.OnUpdate();
		TickCombat();
		if ( GetComponent<BoxCollider>( true ) is { } box ) box.Enabled = IsDropped;
		if ( !IsProxy && IsActive ) ZoomSounds();
		// The dropped model can be made after the weapon was already picked up, which Sandbox's dropped state change never hides.
		if ( DroppedGameObject.IsValid() ) DroppedGameObject.Enabled = IsDropped;
		PoseBody();
	}

	protected override void OnDisabled() {
		ReleaseBody();
		base.OnDisabled();
	}

	// Host: a gun picked up for the first time comes with Halo 2's starting rounds.
	protected override void OnAdded( BaseInventoryComponent inventory ) {
		base.OnAdded( inventory );
		if ( Loaded >= 0 ) return;
		var magazine = Tag?.Magazine( 0 );
		if ( magazine is null ) {
			Loaded = Reserve = 0;
			return;
		}
		Loaded = Math.Min( magazine.RoundsTotalInitial, magazine.RoundsLoadedMaximum );
		Reserve = Math.Min( magazine.RoundsTotalInitial - Loaded, magazine.RoundsInventoryMaximum );
	}

	// Equipping readies it again, as Halo 2 readies a weapon switched to.
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
		_view = new SpartanView( this );
		// A view made after the ready readies at once.
		if ( !string.IsNullOrEmpty( _state.Slot0.Weapon ) ) _view.Message( Hand.Right, "ready" );
	}

	protected override void DestroyViewModel() {
		_view?.Destroy();
		_view = null;
		base.DestroyViewModel();
	}

	protected override void PlaceViewModel( CameraComponent camera, in CameraView view ) {
		var thirdPerson = HaloWeapon.SeenFromOutside( Owner?.Controller, Scene );
		SpartanView.ThirdPerson = thirdPerson;

		// The weapon stays at the camera before camera_control moved it, as halo2sbox draws it; posing it at the moved camera fed the node's pose back into itself.
		var at = new Transform( view.Position, view.Rotation );
		if ( !thirdPerson && _cameraControl is { } c ) {
			var back = c.Rotation.Inverse;
			at = at.ToWorld( new Transform( -(back * c.Position), back ) );
		}
		_view?.Place( at, !thirdPerson );
	}

	// The camera_control pose ModifyCamera moved this frame's camera by.
	Transform? _cameraControl;

	// A pose no animation gives, like one left by a broken frame, never reaches the camera.
	static bool Usable( Transform t ) =>
		float.IsFinite( t.Position.x ) && float.IsFinite( t.Position.y ) && float.IsFinite( t.Position.z ) && t.Position.Length < 100
		&& float.IsFinite( t.Rotation.x ) && float.IsFinite( t.Rotation.y ) && float.IsFinite( t.Rotation.z ) && float.IsFinite( t.Rotation.w );

	public float Magnification => Zoom.Of( Tag, _state.ZoomLevel );

	protected override void ModifyCamera( CameraComponent camera, ref CameraView view ) {
		// Halo's camera never rolls.
		view.Rotation = view.Rotation.Angles().WithRoll( 0 ).ToRotation();

		// The firing and damage shakes, in camera space on top of the final camera.
		var (shakeRotation, shakeOffset) = CameraShake.Sample();
		view.Position += view.Rotation * shakeOffset;
		view.Rotation *= shakeRotation;

		// Halo 2's first person camera follows the weapon's camera_control node.
		_cameraControl = null;
		if ( _view?.CameraControl is { } control && Usable( control ) && !HaloWeapon.SeenFromOutside( Owner?.Controller, Scene ) ) {
			view.Position += view.Rotation * control.Position;
			view.Rotation *= control.Rotation;
			_cameraControl = control;
		}

		var magnification = Magnification;
		if ( magnification > 1 )
			view.FieldOfView = 2 * MathF.Atan( MathF.Tan( view.FieldOfView.DegreeToRadian() / 2 ) / magnification ).RadianToDegree();
	}

	public override void OnCameraMove( PlayerController player, ref Angles angles ) {
		var magnification = Magnification;
		if ( magnification > 1 ) angles /= magnification;
	}

	// Every zoom change plays the zoom in sound, scaled by depth, or the zoom out sound; a weapon without a zoom uses the binoculars'.
	void ZoomSounds() {
		var level = _state.ZoomLevel;
		if ( level == _soundedZoom ) return;
		_soundedZoom = level;
		var tag = Tag;
		var player = GameCombatData.Instance.Player;
		var own = tag?.MagnificationLevels > 0;
		var sound = own ? (level >= 0 ? tag.ZoomInSound : tag.ZoomOutSound) : level >= 0 ? player?.BinocularsZoomInSound : player?.BinocularsZoomOutSound;
		var levels = own ? tag.MagnificationLevels : player?.BinocularsZoomCount ?? 0;
		var scale = level >= 0 && levels > 1 ? (float)level / (levels - 1) : 1;
		if ( sound is not null ) HaloSounds.PlayLocal( sound, scale: scale );
	}

	// third person

	protected override void CreateWorldModel() {
		var renderer = GetComponentInParent<PlayerController>()?.Renderer;
		var model = HaloResources.LoadModel( Tag?.Model );
		if ( renderer is null || model is null ) return;

		if ( Networking.IsHost ) IsDropped = false;

		// Placed every frame on the posed hand (PoseBody).
		var go = new GameObject( renderer.GameObject, true, "halo 2 world model" );
		go.Flags |= GameObjectFlags.NotSaved | GameObjectFlags.NotNetworked;
		// The engine's first person body copies everything under the body except what's under the hold bones or tagged this; it still casts its shadow.
		go.Tags.Add( "firstperson-hidden" );
		var held = go.AddComponent<ModelRenderer>();
		held.Model = model;
		held.CreateAttachments = true;
		WorldModel = go;
		PlaceOnHoldBone( renderer );
	}

	// The held model's muzzle.
	public Vector3? HeldMuzzle => WorldModel.IsValid() && WorldModel.GetComponent<ModelRenderer>()?.GetAttachmentObject( "primary_trigger" ) is { } m && m.IsValid() ? m.WorldPosition : null;

	// input and the combat step

	protected override void OnControl() {
		_pressed |= Buttons();
	}

	// halo_hold holds buttons for testing; it presses Halo CE's bits, which are the same for fire, zoom, melee, grenade and reload.
	static ushort Buttons() {
		ushort b = HaloWeapon.TestButtons;
		if ( Input.Down( "attack1" ) ) b |= CombatButtons.Fire;
		if ( Input.Down( "attack2" ) ) b |= CombatButtons.Zoom;
		if ( Input.Down( "reload" ) ) b |= CombatButtons.Reload;
		if ( Input.Keyboard.Down( "f" ) ) b |= CombatButtons.Melee;
		if ( Input.Keyboard.Down( "mouse3" ) ) b |= CombatButtons.Grenade;
		return (ushort)(b & CombatButtons.All);
	}

	void TickCombat() {
		if ( IsProxy || !IsActive || Owner is null || Tag is null ) return;
		for ( var i = 0; i < Halo2.Combat.HaloTick.Advanced; i++ ) Step();
	}

	// A melee scores half its damage range running forward over 0.9 of the run speed, and all of it airborne more than 7 ticks.
	int _airborneTicks;

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

		_airborneTicks = IsAirborne ? _airborneTicks + 1 : 0;
		var runForward = HaloDataStore.Game?.Globals?.Player?.RunForward ?? 2.25f;
		var forward = Halo2.Combat.HaloUnits.ToWorldUnits( Vector3.Dot( Owner.Controller?.Velocity ?? default, EyeAngles.Forward ) ) / runForward;
		var environment = new CombatEnvironment {
			CommandTick = (int)Halo2.Combat.HaloTick.Count,
			Airborne = _airborneTicks > 0,
			MeleeEta = -1,
			MeleeScale = _airborneTicks > 7 ? 1 : forward > 0.9f ? 127f / 255f : 0,
		};

		var buttons = (ushort)(_pressed | Buttons());
		_pressed = 0;
		_frame = Frame;
		_ticking = true;
		_sim.Tick( ref _state, buttons, environment, this );
		_ticking = false;
		_view?.Tick();

		if ( grenades is not null && (grenades.Frags != _state.Frags || grenades.Plasmas != _state.Plasmas) )
			grenades.Set( _state.Frags, _state.Plasmas );
		Publish();
	}

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

	// the holder, as Halo 2's view and weapon functions read it

	// While a tick's events play, the step's state at that moment, as halo2sbox shows it.
	bool _ticking;
	CombatState Shown => _ticking ? _sim.Current : _state;

	Halo2Unit OwnerUnit => Owner is { } player ? Halo2Unit.For( player.GameObject ) : null;

	ShotFrame _frame;
	ShotFrame Frame => new( EyePosition, Owner?.WorldPosition ?? WorldPosition, Owner?.Controller?.Velocity ?? Vector3.Zero, _airborneTicks, -1 );

	bool IHalo2Hands.IsLocalPlayer => !IsProxy && Owner is not null;
	public bool IsAlive => Owner is { Health: > 0 };
	public bool IsAirborne => !(Owner?.Controller?.IsOnGround ?? true);
	public Vector3 EyePosition => Owner?.EyeTransform.Position ?? WorldPosition;
	public Angles EyeAngles => Owner?.Controller?.EyeAngles ?? WorldRotation.Angles();
	public Vector2 Throttle => IsProxy ? Vector2.Zero : new Vector2( Input.AnalogMove.x, Input.AnalogMove.y );
	public bool Simulated => !IsProxy;
	public int ZoomLevel => Shown.ZoomLevel;
	public bool WantsFire => (Shown.Buttons & CombatButtons.Fire) != 0;
	public bool WantsGrenade => (Shown.Buttons & CombatButtons.Grenade) != 0;
	public WeaponTag CurrentTag => Tag;
	public bool DualWielding => false;

	public string HandPath( Hand hand ) {
		var shown = Shown;
		return hand == Hand.Right ? shown.SlotWeapon( shown.ActiveSlot ) : null;
	}
	public WeaponTag HandTag( Hand hand ) => hand == Hand.Right && HandPath( hand ) is { } path ? HaloDataStore.Game?.Weapon( path ) : null;
	public WeaponSlotState HandState( Hand hand ) {
		var shown = Shown;
		return shown.SlotOf( shown.HandSlot( hand ) );
	}
	public float HandHeatLevel( Hand hand ) => HandState( hand ).Heat;
	public float HandAge( Hand hand ) => HandState( hand ).Age;
	public bool HandOverheated( Hand hand ) => HandState( hand ).Overheated;
	public bool HandPuttingAway( Hand hand ) => hand == Hand.Right ? Shown.PuttingAway : Shown.PuttingAwayLeft;
	public bool HandReloading( Hand hand ) => HandState( hand ).Magazine is MagazineState.Reloading or MagazineState.Entering or MagazineState.Continuing or MagazineState.BetweenRounds;
	public float HandSinceReadied( Hand hand ) => _sinceReadied;
	public float HandSinceFired( Hand hand ) => _sinceFired;
	public int HandLastFiredBarrel( Hand hand ) => _lastBarrel;
	public object HandSoundSource( Hand hand ) => (GameObject.Id, hand);

	// How far the first trigger is through its charging time, 1 once charged.
	public float HandChargeLevel( Hand hand ) {
		var trigger = HandTag( hand )?.Trigger( 0 );
		var slot = HandState( hand );
		if ( trigger is null || trigger.ChargingTime <= 0 ) return 0;
		return slot.Trigger0.Phase switch {
			TriggerPhase.Charged => 1,
			TriggerPhase.Charging => Math.Clamp( 1 - slot.Trigger0.Timer / (trigger.ChargingTime * Halo2.Combat.HaloUnits.TicksPerSecond), 0, 1 ),
			_ => 0,
		};
	}

	// Ticks until an overheated weapon's heat falls under its recovery threshold, as CombatSim cools it.
	public int HandOverheatTicksLeft( Hand hand ) {
		var tag = HandTag( hand );
		var slot = HandState( hand );
		if ( tag is null || !slot.Overheated ) return 0;
		var rate = tag.OverheatedHeatLossPerSecond > 0 ? tag.OverheatedHeatLossPerSecond : tag.HeatLossPerSecond;
		var loss = rate * Halo2.Combat.HaloUnits.TickInterval;
		if ( tag.AgeHeatRecoveryPenalty > 0 ) loss *= 1 - slot.Age * tag.AgeHeatRecoveryPenalty;
		return loss > 0 ? (int)MathF.Ceiling( (slot.Heat - tag.HeatRecoveryThreshold) / loss ) : int.MaxValue;
	}

	// Halo's "primary firing" function: the first barrel's rate of fire ramp while it's firing.
	public float HandPrimaryFiring( Hand hand ) {
		var b = HandState( hand ).Barrel0;
		return b.Phase == BarrelPhase.Firing ? MathF.Max( b.RateOfFire, 0.001f ) : 0;
	}

	// shots

	// Projectile keys and shot seeds tell shooters apart by their connection.
	int Shooter => Math.Abs( (Network.Owner?.Id ?? Guid.Empty).GetHashCode() ) % 4096;

	void ICombatEvents.Fired( Hand hand, int slot, int barrelIndex, int shotCount, float error, float dual, float recoil ) {
		var path = _sim.Current.SlotWeapon( slot );
		var tag = HaloDataStore.Game?.Weapon( path );
		var barrel = tag?.Barrel( barrelIndex );
		var owner = Owner;
		if ( barrel is null || owner is null ) return;

		var aim = EyeAngles.ToRotation();
		var origin = MuzzleOrigin( barrel, aim, _frame.Eye );
		var target = Halo2.Combat.AutoAim.FindTarget( OwnerUnit, tag, _sim.Current.ZoomLevel, origin, aim.Forward, RewindContext.None, out var deflected );
		var tick = (int)Halo2.Combat.HaloTick.Count;
		var seed = TracerSeed( CombatSim.ShotSeed( tick, Shooter, (int)hand * 2 + barrelIndex ), IsTracer( barrel, shotCount ) );
		var targetId = barrel.HasFlag( "tracks fired projectile" ) ? target?.GameObject.Id ?? Guid.Empty : Guid.Empty;

		_sinceFired = 0;
		_lastBarrel = barrelIndex;
		Shoot( path, barrelIndex, hand, shotCount, origin, deflected, _frame.Velocity, seed, error, dual, targetId, tick );
		BroadcastShot( path, barrelIndex, (int)hand, shotCount, origin, deflected, _frame.Velocity, seed, error, dual, targetId, tick );

		CameraShake.Impulse( HaloEffects.FiringDamage( barrel, shotCount ), -aim.Forward, EyeAngles.yaw );
		CameraShake.Apply( HaloEffects.FiringDamage( barrel, shotCount ) );
		_view?.Message( hand, barrelIndex == 0 ? "fire_1" : "fire_2" );

		// The recoil kicks the view up.
		if ( recoil != 0 && owner.Controller is { } controller ) {
			var eyes = controller.EyeAngles;
			eyes.pitch = Math.Clamp( eyes.pitch - recoil, -85.5f, 85.5f );
			controller.EyeAngles = eyes;
		}
	}

	// Every other machine fires the same pellets from the same seed; the host's copy deals the damage.
	// The hand goes as an int: the engine only sends types its type library has, and a mount gives it only components and marked classes.
	[Rpc.Broadcast( NetFlags.Unreliable )]
	void BroadcastShot( string path, int barrelIndex, int hand, int shotCount, Vector3 origin, Vector3 aim, Vector3 inherit, int seed, float error, float dual, Guid target, int tick ) {
		if ( Rpc.Caller == Connection.Local ) return;
		Shoot( path, barrelIndex, (Hand)hand, shotCount, origin, aim, inherit, seed, error, dual, target, tick );
	}

	void Shoot( string path, int barrelIndex, Hand hand, int shotCount, Vector3 origin, Vector3 aim, Vector3 inherit, int seed, float error, float dual, Guid target, int tick ) {
		var barrel = HaloDataStore.Game?.Weapon( path )?.Barrel( barrelIndex );
		if ( barrel is null ) return;
		BodyAnimation?.Fired( hand, barrelIndex );

		var ownerId = Owner?.GameObject.Id ?? Guid.Empty;
		var sim = ProjectileSimulation.Get( Scene );
		foreach ( var launch in ShotLaunches.Build( barrel, origin, aim, seed, error, dual, inherit, target, ownerId, path, ProjectileKey.Shot( Shooter, tick, hand, barrelIndex ) ) )
			sim?.Spawn( launch, Networking.IsHost, RewindContext.None );

		var entry = _firingEffects[Math.Clamp( barrelIndex, 0, 1 )].Next( barrel );
		var role = entry < 0 ? null : HaloEffects.Role( path, entry == 0 ? $"barrel{barrelIndex}_fire" : $"barrel{barrelIndex}_fire_{entry}" );
		PlayFiringEffect( role, origin, aim );
		Owner?.Controller?.Renderer?.Animation.Parameters.TrySet( "b_attack", true );
	}

	// The camera moved forward to the held weapon's trigger marker depth, plus the barrel's first person offset, as Halo 2 fires from the camera.
	Vector3 MuzzleOrigin( BarrelTag barrel, Rotation aim, Vector3 eye ) {
		var forward = aim.Forward;
		var origin = eye;
		var marker = !SpartanView.ThirdPerson && SpartanView.LocalBarrel( Hand.Right ) is { } b ? b.Position : HeldMuzzle;
		if ( marker is { } m ) origin = eye + forward * MathF.Max( 0, Vector3.Dot( m - eye, forward ) );

		if ( barrel.FirstPersonOffset is { Length: >= 3 } o ) {
			var left = Vector3.Cross( Vector3.Up, forward );
			left = left.LengthSquared > 0.0001f ? left.Normal : Vector3.Left;
			var up = Vector3.Cross( forward, left ).Normal;
			origin += forward * Halo2.Combat.HaloUnits.ToInches( o[0] ) + left * Halo2.Combat.HaloUnits.ToInches( o[1] ) + up * Halo2.Combat.HaloUnits.ToInches( o[2] );
		}

		if ( ProjectileSimulation.Get( Scene )?.WorldTrace( eye, origin ) is { Hit: true, StartedSolid: false } blocked )
			origin = blocked.HitPosition + (eye - blocked.HitPosition).Normal * Halo2.Combat.HaloUnits.ToInches( 0.01f );
		return origin;
	}

	// The first shot of a burst keeps its contrail, then one after every "rounds between tracers" shots; the seed's low bit says which.
	static bool IsTracer( BarrelTag barrel, int shotCount ) => shotCount <= 1 || (shotCount - 1) % (Math.Max( 0, barrel.RoundsBetweenTracers ) + 1) == 0;
	static int TracerSeed( int seed, bool tracer ) => (seed & ~1) | (tracer ? 1 : 0);

	bool OwnSounds => !IsProxy;

	// The firing effect from the drawn muzzle: the first person weapon's for the owner, the held model's for everyone else.
	void PlayFiringEffect( string role, Vector3 origin, Vector3 forward ) {
		if ( role is null ) return;
		var firstPerson = !IsProxy && !SpartanView.ThirdPerson;
		if ( firstPerson && SpartanView.LocalMuzzleObject( Hand.Right ) is { } muzzle && muzzle.IsValid() ) {
			var at = SpartanView.LocalMuzzle( Hand.Right ) ?? muzzle.WorldTransform;
			var fp = HaloEffects.SpawnEffect( role, at.Position, forward, "_fp", muzzle, HandSoundSource( Hand.Right ), true );
			if ( fp is not null ) {
				SpartanView.LocalViewEffects( Hand.Right ).Add( fp, name => SpartanView.LocalViewMarker( Hand.Right, name ) );
				HaloEffects.PlaceLocations( fp, name => SpartanView.LocalViewMarker( Hand.Right, name ) );
				return;
			}
		}
		var held = WorldModel.IsValid() ? WorldModel.GetComponent<ModelRenderer>()?.GetAttachmentObject( "primary_trigger" ) : null;
		if ( held.IsValid() && HaloEffects.SpawnEffect( role, held.WorldPosition, forward, parent: held, source: HandSoundSource( Hand.Right ), own: OwnSounds ) is not null ) return;
		HaloEffects.Play( role, held.IsValid() ? held.WorldPosition : origin, forward, source: HandSoundSource( Hand.Right ), own: OwnSounds );
	}

	// A weapon role's effect such as ready, reloading, chambering or empty, at the weapon.
	void PlayWeaponEffect( string role ) {
		var effect = HaloEffects.Role( WeaponTag, role );
		if ( effect is null ) return;
		var at = !IsProxy && !SpartanView.ThirdPerson && SpartanView.LocalViewObject( Hand.Right ) is { } view ? view : WorldModel;
		if ( at.IsValid() && HaloEffects.SpawnEffect( effect, at.WorldPosition, at.WorldRotation.Forward, parent: at, source: HandSoundSource( Hand.Right ), own: OwnSounds ) is not null ) return;
		HaloEffects.Play( effect, EyePosition, source: HandSoundSource( Hand.Right ), own: OwnSounds );
	}

	void ICombatEvents.DryFired( Hand hand, int slot, int barrel ) {
		if ( Tag?.Barrel( barrel )?.FiringEffects?.FirstOrDefault()?.EmptyEffect is { } click && click.EndsWith( ".snd!" ) )
			HaloSounds.Play( click, EyePosition, source: HandSoundSource( hand ), own: OwnSounds );
		else PlayWeaponEffect( $"barrel{barrel}_empty" );
	}

	// A charged plasma pistol shot's overheat sends nothing: its fire_2 ends in the overheated state.
	void ICombatEvents.Overheated( Hand hand, int slot, bool superRecoil ) {
		if ( !superRecoil ) _view?.Message( hand, "overheating" );
		BodyAction( BodyAct.Overheat, hand );
	}

	// A shotgun enters, then loads shell by shell; the rest reload at once.
	void ICombatEvents.ReloadStarted( Hand hand, int slot, MagazineState state, bool empty ) {
		PlayWeaponEffect( "magazine0_reloading" );
		var continuing = state == MagazineState.Continuing;
		var label = continuing ? (empty ? "reload_continue_empty" : "reload_continue_full")
			: Tag?.WeaponType == "shotgun" ? "reload_enter"
			: empty ? "reload_empty" : "reload_full";
		_view?.Message( hand, label );
		BodyAction( BodyAct.Reload, hand );
		Owner?.Controller?.Renderer?.Animation.Parameters.TrySet( "b_reload", true );
	}

	void ICombatEvents.ReloadEnded( Hand hand, bool full ) => _view?.Message( hand, "reload_exit" );

	void ICombatEvents.Chambered( Hand hand, int slot ) {
		PlayWeaponEffect( "magazine0_chambering" );
		BodyAction( BodyAct.Chamber, hand );
	}

	void ICombatEvents.Readied( Hand hand, string weapon ) {
		_sinceReadied = 0;
		_view?.Message( hand, HandOverheated( hand ) ? "ready_overheated" : "ready" );
		PlayWeaponEffect( "ready" );
		BodyAction( BodyAct.Ready, hand );
	}

	void ICombatEvents.PutAway( Hand hand, string weapon ) {
		_view?.Message( hand, "put_away" );
		BodyAction( BodyAct.PutAway, hand );
	}

	void ICombatEvents.LeftDropped( int slot, WeaponSlotState weapon ) { }

	// melee

	static string MeleeLabel( MeleeAnimation animation, int strike, bool airborne ) => animation switch {
		MeleeAnimation.Dash => airborne ? "melee_dash_airborne" : "melee_dash",
		MeleeAnimation.Lunge => airborne ? "melee_lunge_airborne" : "melee_lunge",
		_ => CombatSim.StrikeLabel( strike ),
	};

	void ICombatEvents.MeleeStarted( MeleeAnimation animation, int strike ) {
		var label = MeleeLabel( animation, strike, _sim.Current.MeleeAirborne );
		_view?.Message( Hand.Right, label );
		BodyAction( BodyAct.Melee, Hand.Right, label );
		Owner?.Controller?.Renderer?.Animation.Parameters.TrySet( "b_attack", true );
	}

	// A strike deals the weapon's first hit damage and a lunge its lunge damage, else its player melee damage; without any, the biped's.
	string MeleeDamageEffect( MeleeAnimation animation ) {
		var tag = Tag;
		return (animation == MeleeAnimation.Lunge ? tag?.LungeMeleeDamage : tag?.FirstHitMeleeDamage) ?? tag?.PlayerMeleeDamage ?? HaloDataStore.SpartanBiped?.MeleeDamage;
	}

	// The keyframe: the host deals the damage, the owner hears its own hit at once.
	void ICombatEvents.MeleeStruck( MeleeAnimation animation ) {
		var unit = OwnerUnit;
		if ( unit is null ) return;
		var damage = MeleeDamageEffect( animation );
		var aim = EyeAngles.Forward;
		if ( !Networking.IsHost ) {
			var seen = HaloCombat.MeleeTrace( unit, Tag, _frame.Eye, aim, RewindContext.None );
			HaloCombat.MeleeSound( unit, seen.Material, damage );
		}
		HostMelee( _frame.Eye, _frame.Feet, _frame.Velocity, aim, (int)animation, _sim.Current.MeleeScale );
	}

	[Rpc.Host]
	void HostMelee( Vector3 eye, Vector3 feet, Vector3 velocity, Vector3 aim, int animation, float scale ) {
		if ( Owner is not { } owner || Rpc.Caller != owner.Network.Owner && !(Rpc.Caller?.IsHost ?? true) ) return;
		var unit = OwnerUnit;
		if ( unit is null ) return;
		var damage = MeleeDamageEffect( (MeleeAnimation)animation );
		var hit = HaloCombat.Melee( unit, Tag, new ShotFrame( eye, feet, velocity, 0, -1 ), aim, RewindContext.None, damage, scale );
		if ( hit.Material >= 0 ) MeleeSoundEveryone( hit.Material, animation );
	}

	[Rpc.Broadcast( NetFlags.Unreliable )]
	void MeleeSoundEveryone( int material, int animation ) {
		if ( Owner is not { } owner || (!owner.IsProxy && !Networking.IsHost) || OwnerUnit is not { } unit ) return;
		HaloCombat.MeleeSound( unit, material, MeleeDamageEffect( (MeleeAnimation)animation ) );
	}

	// grenades

	void ICombatEvents.ThrowStarted( int type ) {
		_view?.Message( Hand.Right, HandOverheated( Hand.Right ) ? "throw_overheated" : "throw_grenade" );
		BodyAction( BodyAct.Throw );
		Owner?.Controller?.Renderer?.Animation.Parameters.TrySet( "b_attack", true );
		var effect = HaloDataStore.Game?.Globals?.Grenades.ElementAtOrDefault( type )?.ThrowingEffect;
		if ( !string.IsNullOrEmpty( effect ) && Owner is { } owner )
			HaloEffects.Play( effect, owner.WorldPosition, source: owner, own: OwnSounds );
	}

	void ICombatEvents.GrenadeToHand( int type ) { }

	// The grenade launches from the camera offset by the globals' grenade origin, pulled back if the level is in the way,
	// at the biped's grenade velocity along the aim, without the thrower's own velocity.
	void ICombatEvents.GrenadeReleased( int type ) {
		var globals = HaloDataStore.Game?.Globals;
		var info = globals?.Grenades.ElementAtOrDefault( type );
		if ( info is null ) return;

		var aim = EyeAngles.ToRotation();
		var eye = _frame.Eye;
		var origin = eye;
		if ( globals.Player?.GrenadeOrigin is { Length: >= 3 } o )
			origin += aim.Forward * Halo2.Combat.HaloUnits.ToInches( o[0] ) + aim.Left * Halo2.Combat.HaloUnits.ToInches( o[1] ) + aim.Up * Halo2.Combat.HaloUnits.ToInches( o[2] );
		if ( ProjectileSimulation.Get( Scene )?.WorldTrace( eye, origin ) is { Hit: true, StartedSolid: false } blocked )
			origin = blocked.HitPosition + blocked.Normal * ProjectileSimulation.SurfaceSkin;

		var speed = Halo2.Combat.HaloUnits.PerSecondToInchesPerSecond( HaloDataStore.SpartanBiped?.GrenadeVelocity ?? 10 );
		var tick = (int)Halo2.Combat.HaloTick.Count;
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
		}, Networking.IsHost, RewindContext.None );
	}

	void ICombatEvents.ThrowEnded() { }
	void ICombatEvents.Swap() { }
	void ICombatEvents.DualPickup() { }

	// HUD: Halo 2's own HUD isn't drawn yet, so a plain reticle opens with the spread.
	public override void DrawCrosshair( Painter painter, Vector2 center ) {
		if ( _state.ZoomLevel >= 0 && (Tag?.MagnificationLevels ?? 0) > 0 ) return;
		using var scope = painter.Scope();
		painter.BlendMode = BlendMode.Lighten;
		var color = new Color( 0.45f, 0.75f, 1f, 0.9f );
		painter.Stroke = Stroke.Solid( color, 2 );
		var radius = 10 + MathF.Max( 0, _state.Slot0.Barrel0.Error ) * 24;
		painter.Circle( center, radius );
		painter.Fill = Fill.Solid( color );
		painter.Circle( center, 1.5f );
	}
}
