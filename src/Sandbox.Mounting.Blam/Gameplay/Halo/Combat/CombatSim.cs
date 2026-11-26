using System;
using Halo.Data;

namespace Halo.Combat;

public enum MagazineState : byte { Idle, Reloading, Unchambered, Chambering }

// One trigger's runtime state.
public struct TriggerState {
	public float RateOfFire;
	public float Error;
	public int IdleTicks;
	// Down on its last tick.
	public bool Held;
	// A "does not repeat automatically" trigger that fired waits for a release.
	public bool Latched;
	public int ShotCount;
	public float Charge;
	public float ChargedHold;
}

// One inventory slot's weapon and its runtime state.
public struct WeaponSlotState {
	public string Weapon;
	public int Loaded;
	public int Reserve;
	// Battery used, 0 full, 1 empty.
	public float Age;
	public float Heat;
	public bool Overheated;
	// Overheated, with the heat within a tick of recovering.
	public bool OverheatedExit;
	// The plasma pistol fired a charged shot since its last overheat, so the next one is the super recoil overheat.
	public bool OverheatRecoil;
	// Held fully charged last tick, so this tick it doesn't cool.
	public bool Overcharged;
	public MagazineState Magazine;
	public int MagazineTimer;
	// A reload asked for that starts once the magazine can.
	public bool NeedsReload;
	// Put away or readied: the triggers do nothing until the next tick.
	public bool TriggersUninitialized;
	// The weapon's own third person animation state and the ticks left of it.
	public int WeaponState;
	public int WeaponStateTicks;
	public TriggerState Trigger0;
	public TriggerState Trigger1;

	public static WeaponSlotState For( string weapon, int loaded, int reserve, float age ) => new() {
		Weapon = weapon,
		Loaded = loaded,
		Reserve = reserve,
		Age = age,
		Trigger0 = new TriggerState { IdleTicks = CombatSim.IdleCap },
		Trigger1 = new TriggerState { IdleTicks = CombatSim.IdleCap },
	};
}

// A vehicle gun's trigger.
public struct GunTriggerState {
	public float RateOfFire;
	public float Error;
	public int IdleTicks;
	public bool Latched;
	public int ShotCount;
}

// A vehicle's gun, as its gunner fires it.
public struct GunState {
	public float Heat;
	public bool Overheated;
	// Rounds in the magazine, -1 for a full one before the first shot.
	public int Loaded;
	// Seconds left chambering the next round, and how long that takes.
	public float Chamber;
	public float ChamberLength;
	public GunTriggerState Trigger0;
	public GunTriggerState Trigger1;

	public static GunState Fresh => new() {
		Loaded = -1,
		ChamberLength = 1,
		Trigger0 = new GunTriggerState { IdleTicks = CombatSim.IdleCap },
		Trigger1 = new GunTriggerState { IdleTicks = CombatSim.IdleCap },
	};

	public static ref GunTriggerState Trigger( ref GunState s, int index ) {
		if ( index == 0 ) return ref s.Trigger0;
		return ref s.Trigger1;
	}

	public readonly bool SameAs( in GunState o ) =>
		Heat == o.Heat && Overheated == o.Overheated && Loaded == o.Loaded && Chamber == o.Chamber && ChamberLength == o.ChamberLength &&
		Same( Trigger0, o.Trigger0 ) && Same( Trigger1, o.Trigger1 );

	static bool Same( in GunTriggerState a, in GunTriggerState b ) =>
		a.RateOfFire == b.RateOfFire && a.Error == b.Error && a.IdleTicks == b.IdleTicks && a.Latched == b.Latched && a.ShotCount == b.ShotCount;

	public readonly void Write( ref NetWriter w ) {
		w.Float( Heat );
		w.Byte( (byte)(Overheated ? 1 : 0) );
		w.Short( (short)Loaded );
		w.Float( Chamber );
		w.Float( ChamberLength );
		WriteTrigger( ref w, Trigger0 );
		WriteTrigger( ref w, Trigger1 );
	}

	public static GunState Read( ref NetReader r ) => new() {
		Heat = r.Float(),
		Overheated = r.Byte() != 0,
		Loaded = r.Short(),
		Chamber = r.Float(),
		ChamberLength = r.Float(),
		Trigger0 = ReadTrigger( ref r ),
		Trigger1 = ReadTrigger( ref r ),
	};

	static void WriteTrigger( ref NetWriter w, in GunTriggerState t ) {
		w.Float( t.RateOfFire );
		w.Float( t.Error );
		w.Short( (short)t.IdleTicks );
		w.Byte( (byte)(t.Latched ? 1 : 0) );
		w.Short( (short)Math.Min( t.ShotCount, short.MaxValue ) );
	}

	static GunTriggerState ReadTrigger( ref NetReader r ) => new() {
		RateOfFire = r.Float(),
		Error = r.Float(),
		IdleTicks = r.Short(),
		Latched = r.Byte() != 0,
		ShotCount = r.Short(),
	};
}

// Everything a Spartan's weapons, grenades, melee, zoom and flashlight hold between ticks.
// The host's copy is authoritative. The owner predicts its own and compares it field by field.
public struct CombatState {
	public WeaponSlotState Slot0;
	public WeaponSlotState Slot1;
	public int ActiveSlot;
	// The slot the unit wants in hand, readied once it isn't throwing.
	public int DesiredSlot;
	// Ready animation ticks left. The triggers stay up until it reaches 0.
	public int ReadyTicks;
	// The weapon last readied, so a weapon the host puts in hand is readied too.
	public string ReadiedPath;
	public bool PrimaryWasDown;
	public int MeleeTicks;
	public int MeleeAttackTick;
	// Ticks into the throw, -1 when not throwing.
	public int ThrowTick;
	public int ThrowFrames;
	public int ThrowKeyFrame;
	public int ThrowType;
	public bool GrenadeInHand;
	public int SelectedGrenade;
	public int Frags;
	public int Plasmas;
	public int ZoomLevel;
	// The last command's CombatButtons, for presses.
	public ushort Buttons;
	public int UseHeldTicks;
	public bool FlashlightOn;
	public bool NightVision;
	public float FlashlightBattery;
	// The vehicle gun this Spartan fires from a gunner's seat.
	public GunState Gun;

	public static CombatState Empty => new() {
		Slot0 = WeaponSlotState.For( null, 0, 0, 0 ),
		Slot1 = WeaponSlotState.For( null, 0, 0, 0 ),
		ThrowTick = -1,
		ZoomLevel = -1,
		FlashlightBattery = 1,
		Gun = GunState.Fresh,
	};

	public readonly bool Throwing => ThrowTick >= 0;
	public readonly string SlotWeapon( int slot ) => slot == 0 ? Slot0.Weapon : Slot1.Weapon;
	public readonly WeaponSlotState SlotOf( int slot ) => slot == 0 ? Slot0 : Slot1;

	public static ref WeaponSlotState Slot( ref CombatState s, int index ) {
		if ( index == 0 ) return ref s.Slot0;
		return ref s.Slot1;
	}

	public static ref TriggerState Trigger( ref WeaponSlotState s, int index ) {
		if ( index == 0 ) return ref s.Trigger0;
		return ref s.Trigger1;
	}

	public readonly bool SameAs( in CombatState o ) =>
		Same( Slot0, o.Slot0 ) && Same( Slot1, o.Slot1 ) &&
		ActiveSlot == o.ActiveSlot && DesiredSlot == o.DesiredSlot && ReadyTicks == o.ReadyTicks && ReadiedPath == o.ReadiedPath &&
		PrimaryWasDown == o.PrimaryWasDown && MeleeTicks == o.MeleeTicks && MeleeAttackTick == o.MeleeAttackTick &&
		ThrowTick == o.ThrowTick && ThrowFrames == o.ThrowFrames && ThrowKeyFrame == o.ThrowKeyFrame && ThrowType == o.ThrowType &&
		GrenadeInHand == o.GrenadeInHand && SelectedGrenade == o.SelectedGrenade && Frags == o.Frags && Plasmas == o.Plasmas &&
		ZoomLevel == o.ZoomLevel && Buttons == o.Buttons && UseHeldTicks == o.UseHeldTicks &&
		FlashlightOn == o.FlashlightOn && NightVision == o.NightVision && FlashlightBattery == o.FlashlightBattery && Gun.SameAs( o.Gun );

	static bool Same( in WeaponSlotState a, in WeaponSlotState b ) =>
		a.Weapon == b.Weapon && a.Loaded == b.Loaded && a.Reserve == b.Reserve && a.Age == b.Age && a.Heat == b.Heat &&
		a.Overheated == b.Overheated && a.OverheatedExit == b.OverheatedExit && a.OverheatRecoil == b.OverheatRecoil && a.Overcharged == b.Overcharged &&
		a.Magazine == b.Magazine && a.MagazineTimer == b.MagazineTimer && a.NeedsReload == b.NeedsReload && a.TriggersUninitialized == b.TriggersUninitialized &&
		a.WeaponState == b.WeaponState && a.WeaponStateTicks == b.WeaponStateTicks && Same( a.Trigger0, b.Trigger0 ) && Same( a.Trigger1, b.Trigger1 );

	static bool Same( in TriggerState a, in TriggerState b ) =>
		a.RateOfFire == b.RateOfFire && a.Error == b.Error && a.IdleTicks == b.IdleTicks && a.Held == b.Held && a.Latched == b.Latched &&
		a.ShotCount == b.ShotCount && a.Charge == b.Charge && a.ChargedHold == b.ChargedHold;

	// What differs, for halo_net_showerrors.
	public readonly string Differences( in CombatState o ) {
		var parts = new List<string>();
		if ( !Same( Slot0, o.Slot0 ) ) parts.Add( $"slot 0 ({Slot0.Weapon} {Slot0.Loaded}/{Slot0.Reserve} vs {o.Slot0.Weapon} {o.Slot0.Loaded}/{o.Slot0.Reserve})" );
		if ( !Same( Slot1, o.Slot1 ) ) parts.Add( $"slot 1 ({Slot1.Weapon} {Slot1.Loaded}/{Slot1.Reserve} vs {o.Slot1.Weapon} {o.Slot1.Loaded}/{o.Slot1.Reserve})" );
		if ( ActiveSlot != o.ActiveSlot || DesiredSlot != o.DesiredSlot ) parts.Add( $"slot {ActiveSlot}/{DesiredSlot} vs {o.ActiveSlot}/{o.DesiredSlot}" );
		if ( ReadyTicks != o.ReadyTicks || ReadiedPath != o.ReadiedPath ) parts.Add( "ready" );
		if ( MeleeTicks != o.MeleeTicks ) parts.Add( "melee" );
		if ( ThrowTick != o.ThrowTick || GrenadeInHand != o.GrenadeInHand ) parts.Add( "throw" );
		if ( Frags != o.Frags || Plasmas != o.Plasmas || SelectedGrenade != o.SelectedGrenade ) parts.Add( "grenades" );
		if ( ZoomLevel != o.ZoomLevel ) parts.Add( $"zoom {ZoomLevel} vs {o.ZoomLevel}" );
		if ( FlashlightOn != o.FlashlightOn || FlashlightBattery != o.FlashlightBattery || NightVision != o.NightVision ) parts.Add( "flashlight" );
		if ( !Gun.SameAs( o.Gun ) ) parts.Add( $"vehicle gun (heat {Gun.Heat} vs {o.Gun.Heat}, loaded {Gun.Loaded} vs {o.Gun.Loaded})" );
		return parts.Count > 0 ? string.Join( ", ", parts ) : "other fields";
	}

	// wire

	public readonly void Write( ref NetWriter w, IWeaponIndex index ) {
		WriteSlot( ref w, Slot0, index );
		WriteSlot( ref w, Slot1, index );
		w.Byte( (byte)ActiveSlot );
		w.Byte( (byte)DesiredSlot );
		w.Short( (short)ReadyTicks );
		w.UShort( index.IndexOf( ReadiedPath ) );
		w.Short( (short)MeleeTicks );
		w.Short( (short)MeleeAttackTick );
		w.Short( (short)ThrowTick );
		w.Short( (short)ThrowFrames );
		w.Short( (short)ThrowKeyFrame );
		w.Byte( (byte)ThrowType );
		w.Byte( (byte)SelectedGrenade );
		w.Byte( (byte)Frags );
		w.Byte( (byte)Plasmas );
		w.Byte( (byte)(sbyte)ZoomLevel );
		w.UShort( Buttons );
		w.Short( (short)Math.Min( UseHeldTicks, short.MaxValue ) );
		w.Float( FlashlightBattery );
		w.Byte( Bits( PrimaryWasDown, GrenadeInHand, FlashlightOn, NightVision ) );
		Gun.Write( ref w );
	}

	public static CombatState Read( ref NetReader r, IWeaponIndex index ) {
		var s = new CombatState {
			Slot0 = ReadSlot( ref r, index ),
			Slot1 = ReadSlot( ref r, index ),
			ActiveSlot = r.Byte() & 1,
			DesiredSlot = r.Byte() & 1,
			ReadyTicks = r.Short(),
			ReadiedPath = index.PathOf( r.UShort() ),
			MeleeTicks = r.Short(),
			MeleeAttackTick = r.Short(),
			ThrowTick = r.Short(),
			ThrowFrames = r.Short(),
			ThrowKeyFrame = r.Short(),
			ThrowType = r.Byte() & 1,
			SelectedGrenade = r.Byte() & 1,
			Frags = r.Byte(),
			Plasmas = r.Byte(),
			ZoomLevel = (sbyte)r.Byte(),
			Buttons = r.UShort(),
			UseHeldTicks = r.Short(),
			FlashlightBattery = r.Float(),
		};
		var bits = r.Byte();
		s.PrimaryWasDown = (bits & 1) != 0;
		s.GrenadeInHand = (bits & 2) != 0;
		s.FlashlightOn = (bits & 4) != 0;
		s.NightVision = (bits & 8) != 0;
		s.Gun = GunState.Read( ref r );
		return s;
	}

	static void WriteSlot( ref NetWriter w, in WeaponSlotState s, IWeaponIndex index ) {
		w.UShort( index.IndexOf( s.Weapon ) );
		w.Short( (short)s.Loaded );
		w.Short( (short)s.Reserve );
		w.Float( s.Age );
		w.Float( s.Heat );
		w.Byte( Bits( s.Overheated, s.OverheatedExit, s.OverheatRecoil, s.Overcharged, s.NeedsReload, s.TriggersUninitialized ) );
		w.Byte( (byte)s.Magazine );
		w.Short( (short)s.MagazineTimer );
		w.Byte( (byte)s.WeaponState );
		w.Short( (short)s.WeaponStateTicks );
		WriteTrigger( ref w, s.Trigger0 );
		WriteTrigger( ref w, s.Trigger1 );
	}

	static WeaponSlotState ReadSlot( ref NetReader r, IWeaponIndex index ) {
		var s = new WeaponSlotState {
			Weapon = index.PathOf( r.UShort() ),
			Loaded = r.Short(),
			Reserve = r.Short(),
			Age = r.Float(),
			Heat = r.Float(),
		};
		var bits = r.Byte();
		s.Overheated = (bits & 1) != 0;
		s.OverheatedExit = (bits & 2) != 0;
		s.OverheatRecoil = (bits & 4) != 0;
		s.Overcharged = (bits & 8) != 0;
		s.NeedsReload = (bits & 16) != 0;
		s.TriggersUninitialized = (bits & 32) != 0;
		s.Magazine = (MagazineState)(r.Byte() & 3);
		s.MagazineTimer = r.Short();
		s.WeaponState = r.Byte();
		s.WeaponStateTicks = r.Short();
		s.Trigger0 = ReadTrigger( ref r );
		s.Trigger1 = ReadTrigger( ref r );
		return s;
	}

	static void WriteTrigger( ref NetWriter w, in TriggerState t ) {
		w.Float( t.RateOfFire );
		w.Float( t.Error );
		w.Short( (short)t.IdleTicks );
		w.Byte( Bits( t.Held, t.Latched ) );
		w.Short( (short)Math.Min( t.ShotCount, short.MaxValue ) );
		w.Float( t.Charge );
		w.Float( t.ChargedHold );
	}

	static TriggerState ReadTrigger( ref NetReader r ) {
		var t = new TriggerState { RateOfFire = r.Float(), Error = r.Float(), IdleTicks = r.Short() };
		var bits = r.Byte();
		t.Held = (bits & 1) != 0;
		t.Latched = (bits & 2) != 0;
		t.ShotCount = r.Short();
		t.Charge = r.Float();
		t.ChargedHold = r.Float();
		return t;
	}

	static byte Bits( params bool[] bits ) {
		var b = 0;
		for ( var i = 0; i < bits.Length; i++ )
			if ( bits[i] ) b |= 1 << i;
		return (byte)b;
	}
}

// Weapon tag paths as small numbers on the wire: both sides list the same mount's weapons.
public interface IWeaponIndex {
	ushort IndexOf( string path );
	string PathOf( ushort index );
}

// A first person or body animation's length and key frame, in ticks.
public readonly record struct CombatTiming( int Frames, int KeyFrame );

// The tag data the combat step reads.
public interface ICombatData {
	WeaponTag Weapon( string path );

	// The weapon has a first person animation graph the mount could read.
	bool HasFirstPersonGraph( WeaponTag weapon );

	// A first person animation slot's timing ("ready", "reload-empty", "melee"), null without one.
	CombatTiming? FirstPerson( WeaponTag weapon, string slot );

	// The weapon's own third person animation's frame count ("fire-1", "chamber-1"), null without one.
	int? WeaponAnimationFrames( WeaponTag weapon, string animation );

	// The unit's third person melee for this weapon, standing in when there's no first person graph.
	CombatTiming? BodyMelee( WeaponTag weapon );

	// The unit's throw-grenade animation while holding this weapon.
	CombatTiming? Throw( WeaponTag weapon );
}

// What the host keeps that the step reads but doesn't change.
public struct CombatEnvironment {
	// The flag or oddball's weapon tag while carrying one, else null.
	public string Objective;
	public bool InVehicle;
	public bool SeatAllowsWeapons;
	public bool InfiniteGrenades;
	// The vehicle weapon this Spartan fires from its seat, else null.
	public string Gun;
	// The gunner is getting in or out of its seat: the gun's triggers stay up.
	public bool GunBusy;
	// Held by a unit that isn't a player.
	// Only a player's weapon draws reserve, ages, latches semi-auto triggers and heats past the overheat threshold.
	// So an AI's weapon never runs dry.
	public bool NotPlayerOwned;
	// The unit's animation is busy with a melee, throw, ping or impulse, so the triggers stay up.
	public bool UserBusy;
}

// What a combat tick did. The host makes the shots and damage, and every machine plays what it sees.
// A replay passes none.
public interface ICombatEvents {
	// error is the spread from 0 to 1, or CombatSim.NoError for a zoomed sniper shot.
	void Fired( int slot, int trigger, int shotCount, float error );
	void DryFired( int slot, int trigger );
	void Overheated( bool superRecoil );
	void ReloadStarted( int slot, bool inHand, bool empty, bool firstShell );
	// A shell-by-shell reload ended, with full set if the magazine is full.
	void ReloadEnded( bool full );
	void Chambered( int slot, bool inHand );
	void Readied( string weapon );
	void MeleeStarted();
	// The melee reaches its key frame: the hit.
	void MeleeStruck();
	void ThrowStarted( int type );
	void GrenadeToHand( int type );
	void GrenadeReleased( int type );
	void ThrowEnded();
	// The switch key while carrying the flag or ball drops it.
	void DropObjective();
	// The action key: get in, out, or flip a vehicle.
	void Action();
	// The action key held long enough to pick up or swap a weapon, every tick it stays held.
	void Swap();
	void FlashlightRefused();
	// The vehicle's gun fired.
	void GunFired( int trigger, int shotCount, float error );
}

// One 30 Hz tick of a Spartan's weapons, grenades, melee, zoom and flashlight, by Halo CE's rules.
// A pure step: host and owner run it on the same commands and must agree exactly.
public sealed class CombatSim {
	// Idle ticks stop counting here, far beyond any rate of fire.
	public const int IdleCap = 1000;

	// A zoomed shot from a "use error when unzoomed" trigger, like the sniper's, has no spread.
	public const float NoError = -1;

	// Ticks the action key must be held to swap weapons, from the globals' minimum weapon swap ticks.
	public const int MinimumWeaponSwapTicks = 7;

	// The grenade is in the hand from the throw's second frame.
	const int GrenadeInHandFrame = 2;

	// The weapon animation states this step uses.
	const int WeaponStateIdle = 0;
	const int WeaponStatePrimaryRecoil = 1;
	const int WeaponStateSecondaryRecoil = 2;
	const int WeaponStatePrimaryChamber = 3;

	// Flashlight: the battery drains in two minutes on, charges in 30 s off, and needs 0.2 to turn on.
	const float BatteryDrainPerTick = 1f / 3600f;
	const float BatteryChargePerTick = 1f / 900f;
	public const float MinimumFlashlightBattery = 0.2f;

	public ICombatData Data;

	// A shot's random seed from its command tick, like Source's command number seed.
	// The owner's prediction and the host's shot spread pellets the same way, and the client never picks a seed.
	public static int ShotSeed( int commandTick, int shooter, int trigger ) {
		var h = (uint)commandTick * 0x9E3779B1u ^ (uint)shooter * 0x85EBCA77u ^ (uint)(trigger + 1) * 0xC2B2AE3Du;
		h ^= h >> 15;
		h *= 0x2C1B3C6Du;
		h ^= h >> 12;
		h *= 0x297A2D39u;
		h ^= h >> 15;
		return (int)(h & 0x7FFFFFFE) | 2;
	}

	CombatState _s;
	CombatEnvironment _env;
	ICombatEvents _events;
	ushort _down;
	ushort _pressed;

	// One tick from one command's buttons. events is null for a replay.
	public void Tick( ref CombatState state, ushort buttons, in CombatEnvironment environment, ICombatEvents events ) {
		_s = state;
		_env = environment;
		_events = events;
		_down = (ushort)(buttons & CombatButtons.All);
		_pressed = (ushort)(_down & ~_s.Buttons);
		_s.Buttons = _down;

		Run();

		state = _s;
		_events = null;
	}

	// The state as it is at the moment an event is raised, mid tick.
	public ref readonly CombatState Current => ref _s;

	// The host taking a weapon out of the hand for a swap or a death.
	// It's put away, so a reload past its halfway point counts as loaded.
	public void PutAway( ref CombatState state, int slot ) {
		_s = state;
		_events = null;
		ResetWeapon( slot );
		state = _s;
	}

	bool Down( ushort bit ) => (_down & bit) != 0;
	bool Pressed( ushort bit ) => (_pressed & bit) != 0;

	ref WeaponSlotState Slot( int index ) => ref CombatState.Slot( ref _s, index );
	ref WeaponSlotState Active => ref Slot( _s.ActiveSlot );

	bool CarryingObjective => !string.IsNullOrEmpty( _env.Objective );
	string CurrentPath => CarryingObjective ? _env.Objective : _s.SlotWeapon( _s.ActiveSlot );
	WeaponTag CurrentTag => Data.Weapon( CurrentPath );
	bool InHand( int index ) => index == _s.ActiveSlot && !CarryingObjective;

	// The order Halo updates a player: its controls, the unit, the melee, then the weapon.
	void Run() {
		// In a seat without weapons, the vehicle's controls replace the weapon's and the action key gets out.
		var weaponless = _env.InVehicle && !_env.SeatAllowsWeapons;
		FlashlightTick( Pressed( CombatButtons.Flashlight ) && !weaponless );
		if ( Pressed( CombatButtons.Use ) ) _events?.Action();
		if ( Data.Weapon( _env.Gun ) is { } gun ) TickGun( gun );
		if ( weaponless ) return;

		Inventory();
		if ( Pressed( CombatButtons.SwitchGrenade ) ) _s.SelectedGrenade = 1 - _s.SelectedGrenade;
		_s.UseHeldTicks = Down( CombatButtons.Use ) ? _s.UseHeldTicks + 1 : 0;
		if ( _s.UseHeldTicks >= MinimumWeaponSwapTicks && !_env.InVehicle ) _events?.Swap();

		// Riders with weapons can shoot and reload, nothing more.
		var melee = Pressed( CombatButtons.Melee ) && !_env.InVehicle;
		var grenade = Pressed( CombatButtons.Grenade ) && !_env.InVehicle;

		if ( Pressed( CombatButtons.Switch ) ) RotateWeapon();
		if ( Pressed( CombatButtons.Zoom ) ) CycleZoom();

		// Ready the desired weapon unless a throw is in progress, putting the one in hand away at once.
		if ( _s.DesiredSlot != _s.ActiveSlot && !_s.Throwing ) ReadyDesiredWeapon();
		var switching = _s.DesiredSlot != _s.ActiveSlot;

		TickGrenade( grenade );

		// The weapon's user is busy throwing or meleeing.
		var userBusy = !switching && (_s.Throwing || _s.MeleeTicks > 0 || _env.UserBusy);
		TickMelee( melee );

		WeaponUpdate( userBusy, switching );

		// A weapon put away still updates: its heat falls, and a reload it was asked for goes on.
		for ( var i = 0; i < 2; i++ )
			if ( !InHand( i ) ) HolsteredWeaponUpdate( i );
	}

	// Follows the host's inventory changes.
	// An empty hand takes the other weapon, and an empty desired slot is forgotten.
	// A weapon put in hand by a spawn, a pickup or the flag is readied like a switch.
	void Inventory() {
		if ( string.IsNullOrEmpty( CurrentPath ) && !string.IsNullOrEmpty( _s.SlotWeapon( 1 - _s.ActiveSlot ) ) )
			_s.ActiveSlot = 1 - _s.ActiveSlot;
		if ( string.IsNullOrEmpty( _s.SlotWeapon( _s.DesiredSlot ) ) )
			_s.DesiredSlot = _s.ActiveSlot;
		if ( !string.IsNullOrEmpty( CurrentPath ) && CurrentPath != _s.ReadiedPath )
			WeaponReady();
	}

	// The key toggles night vision while zoomed with a weapon that has it, otherwise the light.
	// The light toggles only on foot, and turns on only above 0.2 battery.
	// While on, the battery drains and entering a vehicle turns it off.
	void FlashlightTick( bool control ) {
		var on = _s.FlashlightOn;
		var onFoot = !_env.InVehicle;
		if ( control || _s.FlashlightBattery <= 0 ) {
			var nightVision = _s.ZoomLevel >= 0 && (CurrentTag?.HasFlag( "enables integrated night vision" ) ?? false);
			if ( nightVision && control )
				_s.NightVision = !_s.NightVision;
			else if ( (on || _s.FlashlightBattery > MinimumFlashlightBattery) && onFoot )
				on = !on;
		}

		if ( on ) {
			_s.FlashlightBattery -= BatteryDrainPerTick;
			if ( !onFoot ) on = false;
		}
		else if ( _s.FlashlightBattery < 1 ) {
			_s.FlashlightBattery += BatteryChargePerTick;
		}

		if ( control && !on && _s.FlashlightBattery < MinimumFlashlightBattery ) _events?.FlashlightRefused();
		_s.FlashlightOn = on;
	}

	// switching and zoom

	// The switch key flips the desired slot and unzooms, so two presses during a throw cancel out.
	// Carrying the flag or ball, it drops that instead.
	void RotateWeapon() {
		if ( CarryingObjective ) {
			_events?.DropObjective();
			return;
		}

		_s.ZoomLevel = -1;
		var next = 1 - _s.DesiredSlot;
		if ( !string.IsNullOrEmpty( _s.SlotWeapon( next ) ) ) _s.DesiredSlot = next;
	}

	// The zoom key turns the zoom level over, unless the weapon is reloading.
	void CycleZoom() => _s.ZoomLevel = NextZoom( _s, CurrentTag );

	// The zoom level a press of the zoom key gives, so the owner can show it before the tick runs.
	public static int NextZoom( in CombatState s, WeaponTag tag ) {
		var slot = s.SlotOf( s.ActiveSlot );
		if ( tag is null || tag.ZoomLevels <= 0 || slot.Magazine == MagazineState.Reloading ) return s.ZoomLevel;
		var level = s.ZoomLevel + 1;
		return level >= tag.ZoomLevels ? -1 : level;
	}

	// The weapon in hand is put away at once (whatever it was doing), then the desired one readied.
	void ReadyDesiredWeapon() {
		ResetWeapon( _s.ActiveSlot );
		_s.ActiveSlot = _s.DesiredSlot;
		WeaponReady();
	}

	// Readying resets the weapon, unzooms, and holds its triggers up for the first person "ready" animation.
	// A melee, throw or reload can still start and cut the animation short.
	void WeaponReady() {
		_s.ReadiedPath = CurrentPath;
		var tag = CurrentTag;
		ResetWeapon( _s.ActiveSlot );
		_s.ReadyTicks = FirstPersonFrames( tag, "ready" );
		_s.ZoomLevel = -1;
		_s.PrimaryWasDown = true;
		if ( CurrentPath is { } path ) _events?.Readied( path );
	}

	int FirstPersonFrames( WeaponTag tag, string slot ) => tag is null ? 0 : Data.FirstPerson( tag, slot )?.Frames ?? 0;

	// the weapon in hand

	// The weapon in hand: its own animation, the heat, the magazine, then the triggers.
	void WeaponUpdate( bool userBusy, bool switching ) {
		var tag = CurrentTag;
		// Switching, the weapon gets no trigger and no reload.
		var fire = Down( CombatButtons.Fire ) && !switching;
		var reload = Pressed( CombatButtons.Reload ) && !switching;

		// The ready timer counts down before the triggers look at it, on the readying tick too.
		if ( _s.ReadyTicks > 0 ) _s.ReadyTicks--;

		// The flag and ball can only be swung. The slot's weapon updates as if put away.
		if ( tag is null || CarryingObjective ) return;

		ref var slot = ref Active;
		WeaponStateNext( ref slot );
		HeatUpdate( _s.ActiveSlot, tag );

		var magazine = tag.Magazine( 0 );
		if ( magazine is not null ) {
			// The trigger or a switch ends a shell by shell reload.
			MagazineUpdate( _s.ActiveSlot, reload, chain: !fire && !switching );
		}

		var primary = tag.Triggers.Count > 0 ? tag.Triggers[0] : null;
		if ( primary is null ) return;

		var uninitialized = slot.TriggersUninitialized;
		slot.TriggersUninitialized = false;

		var down = !userBusy && _s.ReadyTicks <= 0 && fire;
		var pressed = fire && !_s.PrimaryWasDown;
		_s.PrimaryWasDown = fire;

		// An idle trigger whose magazine can't make a shot reloads it, unless the unit is busy.
		if ( !uninitialized && !userBusy && slot.Trigger0.Charge <= 0 && primary.Magazine is int m && m == 0 && NeedsRounds( slot, primary ) )
			StartReload( _s.ActiveSlot, firstShell: true );

		// A trigger can only begin firing from an idle magazine.
		var canBegin = !uninitialized && (primary.Magazine is null || magazine is null || slot.Magazine == MagazineState.Idle);

		if ( primary.ChargingTime > 0 && tag.Triggers.Count == 1 ) {
			TickSingleCharging( _s.ActiveSlot, tag, primary, down, canBegin );
			return;
		}
		if ( primary.ChargingTime > 0 ) {
			TickCharging( _s.ActiveSlot, tag, primary, down && canBegin );
			return;
		}

		TickTrigger( _s.ActiveSlot, tag, 0, primary, down, pressed, canBegin );
	}

	// The weapon's own third person animation ran out.
	static void WeaponStateNext( ref WeaponSlotState slot ) {
		if ( slot.WeaponStateTicks > 0 && --slot.WeaponStateTicks == 0 )
			slot.WeaponState = WeaponStateIdle;
	}

	// The reload key sets a request that starts once the magazine is idle or unchambered and both triggers are idle.
	// A request made while reloading survives putting the weapon away, which is the backpack reload.
	// Then the magazine's timer and state advance.
	void MagazineUpdate( int index, bool reload, bool chain ) {
		ref var slot = ref Slot( index );
		if ( reload ) slot.NeedsReload = true;
		if ( slot.NeedsReload ) StartReload( index, firstShell: true );

		if ( slot.MagazineTimer > 0 ) slot.MagazineTimer--;
		switch ( slot.Magazine ) {
			case MagazineState.Reloading:
				if ( slot.MagazineTimer - 1 <= 0 )
					FinishReload( index, chain );
				break;

			case MagazineState.Unchambered:
				StartChamber( index );
				break;

			case MagazineState.Chambering:
				if ( slot.MagazineTimer == 0 ) slot.Magazine = MagazineState.Idle;
				break;
		}
	}

	// A carried weapon not in hand gets no trigger, but its heat falls and a pending reload still runs.
	void HolsteredWeaponUpdate( int index ) {
		ref var slot = ref Slot( index );
		var tag = Data.Weapon( slot.Weapon );
		if ( tag is null ) return;

		WeaponStateNext( ref slot );
		HeatUpdate( index, tag );
		if ( tag.Magazine( 0 ) is not null )
			MagazineUpdate( index, reload: false, chain: true );

		slot.TriggersUninitialized = false;
		var primary = tag.Triggers.Count > 0 ? tag.Triggers[0] : null;
		if ( primary is null ) return;

		// Trigger up, so rate of fire and spread wind down as they would in hand.
		if ( primary.ChargingTime > 0 && tag.Triggers.Count == 1 )
			TickSingleCharging( index, tag, primary, false, false );
		else if ( primary.ChargingTime > 0 )
			TickCharging( index, tag, primary, false );
		else
			TickTrigger( index, tag, 0, primary, false, false, false );
	}

	// At the overheated threshold the weapon overheats, with super recoil after a plasma pistol charged shot.
	// Heat falls by its loss rate, slower as the battery ages.
	// The exit bit is set within a tick of the recovery threshold, and below it the weapon recovers.
	void HeatUpdate( int index, WeaponTag tag ) {
		ref var slot = ref Slot( index );
		if ( slot.Heat <= 0 ) {
			slot.Overcharged = false;
			return;
		}

		if ( slot.Heat >= tag.OverheatedThreshold && !slot.Overheated ) {
			slot.Overheated = true;
			var superRecoil = tag.WeaponType == "plasma pistol" && slot.OverheatRecoil;
			if ( superRecoil ) slot.OverheatRecoil = false;
			if ( InHand( index ) ) _events?.Overheated( superRecoil );
		}

		var overcharged = slot.Overcharged;
		slot.Overcharged = false;
		if ( !overcharged ) {
			var loss = tag.HeatLossRate / HaloUnits.TicksPerSecond;
			if ( tag.AgeHeatRecoveryPenalty > 0 ) loss *= 1 - slot.Age * tag.AgeHeatRecoveryPenalty;
			slot.Heat = MathF.Max( 0, slot.Heat - loss );

			if ( slot.Overheated && !slot.OverheatedExit && (slot.Heat - tag.HeatRecoveryThreshold) / loss <= 1 )
				slot.OverheatedExit = true;
		}

		if ( slot.Overheated && slot.Heat < tag.HeatRecoveryThreshold ) {
			slot.Overheated = false;
			slot.OverheatedExit = false;
		}
	}

	// Too few rounds loaded to fire.
	static bool NeedsRounds( in WeaponSlotState slot, TriggerTag trigger ) =>
		(slot.Loaded < trigger.RoundsPerShot && !trigger.HasFlag( "can fire with partial ammo" )) ||
		slot.Loaded < trigger.MinimumRoundsLoaded ||
		slot.Loaded == 0;

	// A trigger fires once idle ticks + 1 reach 30 / rate of fire, checked before the idle count advances.
	// Battery age slows it by the age rate of fire penalty.
	// Rate of fire and spread ramp after the shot, so a shot uses the previous tick's rate.
	// A held trigger ramps even when it can't begin firing.
	void TickTrigger( int index, WeaponTag tag, int triggerIndex, TriggerTag trigger, bool held, bool pressed, bool canBegin ) {
		ref var slot = ref Slot( index );
		ref var rt = ref CombatState.Trigger( ref slot, triggerIndex );
		rt.Held = held;
		if ( !held ) rt.Latched = false;

		var fired = false;
		// An overheated weapon doesn't begin firing at all, so it neither clicks nor latches and fires again as it recovers.
		if ( held && !rt.Latched && canBegin && !slot.Overheated ) {
			var required = trigger.RequiredTicks( rt.RateOfFire, slot.Age, tag.AgeRateOfFirePenalty );
			if ( rt.IdleTicks + 1 >= required ) {
				if ( CanFire( slot, tag, trigger ) ) {
					fired = true;
					rt.IdleTicks = 0;
					if ( trigger.HasFlag( "does not repeat automatically" ) && !_env.NotPlayerOwned ) rt.Latched = true;
					Fire( index, tag, triggerIndex, trigger );
				}
				else if ( pressed ) {
					_events?.DryFired( index, triggerIndex );
					rt.Latched = true;
				}
			}
		}

		if ( !fired && rt.IdleTicks < IdleCap ) rt.IdleTicks++;
		if ( !held ) rt.ShotCount = 0;

		rt.RateOfFire = held
			? MathF.Min( 1, rt.RateOfFire + trigger.FiringAccelerationRate )
			: MathF.Max( 0, rt.RateOfFire - trigger.FiringDecelerationRate );

		// The spread climbs every tick the trigger is down, between shots too, and falls once it's let go.
		rt.Error = held
			? MathF.Min( 1, rt.Error + trigger.ErrorAccelerationRate )
			: MathF.Max( 0, rt.Error - trigger.ErrorDecelerationRate );
	}

	// The plasma pistol: hold to charge, release early for a normal bolt.
	// Released charged, it fires the overcharge from the second trigger.
	// Held charged past its charged time, the overcharge fires by itself.
	void TickCharging( int index, WeaponTag tag, TriggerTag trigger, bool down ) {
		ref var slot = ref Slot( index );
		ref var rt = ref slot.Trigger0;
		var dt = HaloUnits.TickInterval;
		var held = down && !slot.Overheated && slot.Age < 1;
		// A charge begins only once the trigger's rate of fire allows another shot.
		var charging = held && (rt.Charge > 0 || rt.IdleTicks + 1 >= trigger.RequiredTicks( rt.RateOfFire, slot.Age, tag.AgeRateOfFirePenalty ));

		if ( charging ) {
			rt.Charge += dt;
			if ( rt.Charge >= trigger.ChargingTime ) {
				rt.ChargedHold += dt;
				// Held charged past the tick it got there, so it doesn't cool.
				if ( rt.ChargedHold > dt * 1.5f ) slot.Overcharged = true;
				if ( trigger.ChargedTime > 0 && rt.ChargedHold >= trigger.ChargedTime )
					ReleaseCharge( index, tag, trigger );
			}
		}
		else if ( rt.Charge > 0 ) {
			ReleaseCharge( index, tag, trigger );
		}
		else if ( rt.IdleTicks < IdleCap ) {
			rt.IdleTicks++;
		}

		if ( !held ) rt.Error = MathF.Max( 0, rt.Error - dt / MathF.Max( 0.01f, trigger.ErrorDecelerationTime ) );
		rt.RateOfFire = down
			? MathF.Min( 1, rt.RateOfFire + trigger.FiringAccelerationRate )
			: MathF.Max( 0, rt.RateOfFire - trigger.FiringDecelerationRate );
	}

	// A charging weapon with one trigger, like the hunter's fuel rod gun, shoots only as a charge begins, once the trigger's rate of fire has built up.
	// Held, it charges, then at the end of its charged time discharges without a shot, and shoots as the next charge begins.
	// Let go before full charge, the charge is lost; let go charged, it recovers without a shot.
	void TickSingleCharging( int index, WeaponTag tag, TriggerTag trigger, bool down, bool canBegin ) {
		ref var slot = ref Slot( index );
		ref var rt = ref slot.Trigger0;
		var dt = HaloUnits.TickInterval;
		var charged = (int)(trigger.ChargingTime * HaloUnits.TicksPerSecond) + 0.5f;
		rt.Held = down;

		if ( rt.Charge <= 0 ) {
			var required = trigger.RequiredTicks( rt.RateOfFire, slot.Age, tag.AgeRateOfFirePenalty );
			if ( down && canBegin && !slot.Overheated && rt.IdleTicks + 1 >= required ) {
				if ( rt.RateOfFire > 0 && CanFire( slot, tag, trigger ) ) Fire( index, tag, 0, trigger );
				rt.Charge = dt;
			}
			else if ( rt.IdleTicks < IdleCap ) {
				rt.IdleTicks++;
			}
		}
		else if ( rt.Charge < charged * dt ) {
			rt.Charge += dt;
			if ( rt.Charge < charged * dt && !down ) rt.Charge = rt.RateOfFire = 0;
		}
		else if ( !down ) {
			RecoverCharge( ref rt );
		}
		else {
			slot.Overcharged = true;
			rt.ChargedHold += dt;
			var discharges = trigger.OverchargedAction == "discharge";
			if ( discharges && rt.ChargedHold >= ((int)(trigger.ChargedTime * HaloUnits.TicksPerSecond) - 0.5f) * dt )
				RecoverCharge( ref rt );
		}

		if ( !down ) rt.ShotCount = 0;
		rt.RateOfFire = down
			? MathF.Min( 1, rt.RateOfFire + trigger.FiringAccelerationRate )
			: MathF.Max( 0, rt.RateOfFire - trigger.FiringDecelerationRate );
		rt.Error = down
			? MathF.Min( 1, rt.Error + trigger.ErrorAccelerationRate )
			: MathF.Max( 0, rt.Error - trigger.ErrorDecelerationRate );
	}

	static void RecoverCharge( ref TriggerState rt ) {
		rt.Charge = rt.ChargedHold = 0;
		rt.IdleTicks = 0;
		rt.RateOfFire = 0;
	}

	void ReleaseCharge( int index, WeaponTag tag, TriggerTag trigger ) {
		ref var slot = ref Slot( index );
		var charged = slot.Trigger0.Charge >= trigger.ChargingTime;
		slot.Trigger0.Charge = 0;
		slot.Trigger0.ChargedHold = 0;
		// The trigger recovers from either shot, and a charged release also drops its rate of fire.
		slot.Trigger0.IdleTicks = 0;
		if ( charged ) slot.Trigger0.RateOfFire = 0;

		var triggerIndex = charged && tag.Triggers.Count > 1 ? 1 : 0;
		var t = tag.Triggers[triggerIndex];
		if ( CanFire( slot, tag, t ) )
			Fire( index, tag, triggerIndex, t );
	}

	static bool CanFire( in WeaponSlotState slot, WeaponTag tag, TriggerTag trigger ) {
		if ( slot.Overheated ) return false;
		if ( trigger.AgeGeneratedPerRound > 0 && slot.Age >= 1 ) return false;

		var magazine = trigger.Magazine is int m ? tag.Magazine( m ) : null;
		if ( magazine is not null && magazine.RoundsLoadedMaximum > 0 ) {
			// An empty magazine with reserve is already reloading, so this only dry fires one with none.
			var need = Math.Max( 1, trigger.RoundsPerShot );
			if ( slot.Loaded < need && !(trigger.HasFlag( "can fire with partial ammo" ) && slot.Loaded > 0) )
				return false;
		}

		return true;
	}

	// A shot takes its rounds from the magazine and adds heat and battery age.
	// A pump weapon is left unchambered.
	// The heat overheats it on the next tick, after the shot's animation has started.
	void Fire( int index, WeaponTag tag, int triggerIndex, TriggerTag trigger ) {
		ref var slot = ref Slot( index );
		ref var rt = ref CombatState.Trigger( ref slot, triggerIndex );
		rt.ShotCount++;

		var magazine = trigger.Magazine is int m ? tag.Magazine( m ) : null;
		if ( magazine is not null && magazine.RoundsLoadedMaximum > 0 ) {
			slot.Loaded = Math.Max( 0, slot.Loaded - Math.Max( 0, trigger.RoundsPerShot ) );
			if ( slot.Loaded > 0 && magazine.Flags.Contains( "every round must be chambered" ) ) {
				slot.Magazine = MagazineState.Unchambered;
				slot.MagazineTimer = 0;
			}
		}
		SetWeaponState( ref slot, tag, triggerIndex == 0 ? WeaponStatePrimaryRecoil : WeaponStateSecondaryRecoil );

		slot.Heat += trigger.HeatGeneratedPerRound;
		slot.Heat = _env.NotPlayerOwned && slot.Heat > tag.OverheatedThreshold ? tag.OverheatedThreshold : MathF.Min( 1, slot.Heat );
		if ( triggerIndex == 1 && tag.WeaponType == "plasma pistol" ) slot.OverheatRecoil = true;
		if ( !_env.NotPlayerOwned ) slot.Age = MathF.Min( 1, slot.Age + trigger.AgeGeneratedPerRound );

		var error = _s.ZoomLevel >= 0 && trigger.HasFlag( "use error when unzoomed" ) ? NoError : rt.Error;
		_events?.Fired( index, triggerIndex, rt.ShotCount, error );
	}

	// A reload starts from an idle or unchambered magazine once both triggers and the weapon state are idle.
	// It needs room and reserve, but the request is used up either way.
	// Starting one in hand unzooms.
	void StartReload( int index, bool firstShell ) {
		ref var slot = ref Slot( index );
		var tag = Data.Weapon( slot.Weapon );
		var magazine = tag?.Magazine( 0 );
		if ( magazine is null ) return;
		if ( slot.Magazine is not (MagazineState.Idle or MagazineState.Unchambered) || !MagazineStateChangeOk( slot ) ) return;

		slot.NeedsReload = false;
		if ( slot.Reserve <= 0 || slot.Loaded >= magazine.RoundsLoadedMaximum ) return;

		slot.Magazine = MagazineState.Reloading;
		slot.MagazineTimer = ReloadTicks( tag, slot.Loaded, firstShell );
		var inHand = InHand( index );
		if ( inHand ) _s.ZoomLevel = -1;
		_events?.ReloadStarted( index, inHand, slot.Loaded == 0, firstShell );
	}

	// A reload's rounds go in on its last tick, leaving the magazine unchambered.
	// A shell-by-shell magazine like the shotgun's chains straight into the next shell.
	// Holding the trigger or switching ends it after the current shell.
	void FinishReload( int index, bool chain, bool reset = false ) {
		ref var slot = ref Slot( index );
		var tag = Data.Weapon( slot.Weapon );
		var magazine = tag?.Magazine( 0 );
		if ( magazine is null ) return;

		var wastes = magazine.Flags.Contains( "wastes rounds when reloaded" );
		if ( wastes ) slot.Loaded = 0;
		var loaded = Math.Min( slot.Loaded + Math.Min( magazine.RoundsReloaded, slot.Reserve ), magazine.RoundsLoadedMaximum );
		if ( !_env.NotPlayerOwned ) slot.Reserve -= loaded - slot.Loaded;
		slot.Loaded = loaded;
		slot.Magazine = MagazineState.Unchambered;
		slot.MagazineTimer = 0;

		if ( slot.Reserve > 0 && slot.Loaded < magazine.RoundsLoadedMaximum && !wastes && chain )
			StartReload( index, firstShell: false );

		if ( !reset && InHand( index ) && slot.Magazine != MagazineState.Reloading && magazine.RoundsReloaded < magazine.RoundsLoadedMaximum )
			_events?.ReloadEnded( slot.Loaded >= magazine.RoundsLoadedMaximum );
	}

	// An unchambered magazine chambers for its chamber time, like the shotgun's pump, blocking reloads until done.
	void StartChamber( int index ) {
		ref var slot = ref Slot( index );
		if ( !MagazineStateChangeOk( slot ) ) return;

		var tag = Data.Weapon( slot.Weapon );
		var magazine = tag?.Magazine( 0 );
		if ( magazine is null ) return;

		SetWeaponState( ref slot, tag, WeaponStatePrimaryChamber );
		slot.Magazine = MagazineState.Chambering;
		slot.MagazineTimer = (int)(magazine.ChamberTime * HaloUnits.TicksPerSecond);
		_events?.Chambered( index, InHand( index ) );
	}

	static bool MagazineStateChangeOk( in WeaponSlotState slot ) =>
		!slot.TriggersUninitialized && slot.WeaponState == WeaponStateIdle && slot.Trigger0.Charge <= 0 && slot.Trigger1.Charge <= 0;

	// Putting away, readying, a melee or a throw resets the weapon and drops any plasma pistol charge unfired.
	// A reload past the halfway point of "reload-empty" loads first, and an earlier one loads nothing.
	// That's the pocket reload. The reload request stands, which is the backpack reload.
	void ResetWeapon( int index ) {
		ref var slot = ref Slot( index );
		slot.TriggersUninitialized = true;
		slot.Trigger0.Charge = slot.Trigger0.ChargedHold = 0;
		slot.Trigger1.Charge = slot.Trigger1.ChargedHold = 0;

		if ( slot.Magazine == MagazineState.Reloading ) {
			var tag = Data.Weapon( slot.Weapon );
			if ( 2 * slot.MagazineTimer < ReloadAnimationTicks( tag, "reload-empty" ) )
				FinishReload( index, chain: false, reset: true );
		}

		slot.Magazine = MagazineState.Idle;
		slot.MagazineTimer = 0;
	}

	// Sets the weapon's own animation state if it has one, like the pistol recoil or shotgun pump.
	// Only idle can be interrupted, or a recoil by a state at least as far along.
	// The state lasts until its animation is about to restart.
	void SetWeaponState( ref WeaponSlotState slot, WeaponTag tag, int state ) {
		var old = slot.WeaponState;
		var interruptable = old == WeaponStateIdle || (old <= WeaponStateSecondaryRecoil && state >= old);
		if ( !interruptable ) return;

		var animation = state switch {
			WeaponStatePrimaryRecoil => "fire-1",
			WeaponStateSecondaryRecoil => "fire-2",
			WeaponStatePrimaryChamber => "chamber-1",
			_ => null,
		};
		if ( tag is null || animation is null || Data.WeaponAnimationFrames( tag, animation ) is not { } frames ) return;

		slot.WeaponState = state;
		slot.WeaponStateTicks = Math.Max( 1, frames - 1 );
	}

	// A reload lasts the first person "reload-empty" animation, even with rounds still loaded.
	// A shotgun's first or only shell lasts its "enter" animation instead.
	int ReloadTicks( WeaponTag tag, int loaded, bool firstShell ) {
		if ( tag?.WeaponType == "shotgun" && tag.Magazine( 0 ) is { } magazine ) {
			var oneLeft = magazine.RoundsLoadedMaximum - loaded == 1;
			// Shotgun reload types: first round, last round, first and last round, or none.
			var type = firstShell ? (oneLeft ? 2 : 0) : (oneLeft ? 1 : -1);
			if ( type is 0 or 2 ) return ReloadAnimationTicks( tag, "enter" );
		}
		return ReloadAnimationTicks( tag, "reload-empty" );
	}

	// A first person animation's length, or the magazine's reload time when there's no readable first person graph.
	int ReloadAnimationTicks( WeaponTag tag, string slot ) {
		if ( tag is not null && !Data.HasFirstPersonGraph( tag ) && tag.Magazine( 0 ) is { } magazine )
			return (int)(magazine.ReloadTime * HaloUnits.TicksPerSecond);
		return FirstPersonFrames( tag, slot );
	}

	// melee

	// The melee counts down and hits when it reaches its attack tick.
	// Only a fresh press starts one, so holding the key doesn't repeat it.
	void TickMelee( bool wants ) {
		if ( _s.MeleeTicks == 0 ) {
			if ( wants ) StartMelee( CurrentTag );
			return;
		}

		if ( _s.MeleeTicks == _s.MeleeAttackTick ) _events?.MeleeStruck();
		_s.MeleeTicks--;
	}

	// No melee without a weapon, with "prevents melee attack", while a plasma pistol charges, or while zoomed.
	// It stops a reload, and readying or a throw doesn't block it.
	void StartMelee( WeaponTag tag ) {
		if ( tag is null || tag.HasFlag( "prevents melee attack" ) || _s.ZoomLevel >= 0 ) return;
		if ( Active.Trigger0.Charge > 0 ) return;

		(_s.MeleeTicks, _s.MeleeAttackTick) = MeleeTicks( tag );
		ResetWeapon( _s.ActiveSlot );
		_events?.MeleeStarted();
	}

	// From the first person melee animation: it lasts three quarters of its frames and hits at its key frame.
	// Without a first person graph, the unit's third person melee stands in.
	public (int Ticks, int AttackTick) MeleeTicks( WeaponTag tag ) {
		var timing = Data.FirstPerson( tag, "melee" );
		if ( timing is null && !Data.HasFirstPersonGraph( tag ) )
			timing = Data.BodyMelee( tag );
		if ( timing is not { } t ) return (0, 0);

		var speedup = t.Frames >> 2;
		return (t.Frames - speedup, t.Frames - t.KeyFrame - speedup);
	}

	// vehicle guns

	// A vehicle gun: fire pulls the first trigger, secondary fire the second.
	// Rate of fire and spread ramp as on a hand weapon, with heat but no battery.
	// Its magazine waits its chamber time between rounds or once empty.
	void TickGun( WeaponTag weapon ) {
		ref var gun = ref _s.Gun;
		var dt = HaloUnits.TickInterval;

		gun.Heat = MathF.Max( 0, gun.Heat - weapon.HeatLossRate / HaloUnits.TicksPerSecond );
		if ( gun.Overheated && gun.Heat <= weapon.HeatRecoveryThreshold ) gun.Overheated = false;

		var magazine = weapon.Magazine( 0 );
		if ( gun.Loaded < 0 ) gun.Loaded = magazine?.RoundsLoadedMaximum ?? 0;
		if ( gun.Chamber > 0 ) {
			gun.Chamber -= dt;
			if ( gun.Chamber <= 0 && magazine is not null && gun.Loaded <= 0 )
				gun.Loaded = magazine.RoundsLoadedMaximum;
		}

		for ( var i = 0; i < Math.Min( 2, weapon.Triggers.Count ); i++ )
			TickGunTrigger( weapon, i, !_env.GunBusy && Down( i == 0 ? CombatButtons.Fire : CombatButtons.SecondaryFire ) );
	}

	void TickGunTrigger( WeaponTag weapon, int index, bool held ) {
		var trigger = weapon.Triggers[index];
		ref var gun = ref _s.Gun;
		ref var rt = ref GunState.Trigger( ref gun, index );

		if ( !held ) rt.Latched = false;

		var fired = false;
		if ( held && !rt.Latched ) {
			var required = trigger.RequiredTicks( rt.RateOfFire, 0, weapon.AgeRateOfFirePenalty );
			if ( rt.IdleTicks + 1 >= required && CanFireGun( gun, weapon, trigger ) ) {
				FireGun( weapon, index, trigger );
				fired = true;
				rt.IdleTicks = 0;
				if ( trigger.HasFlag( "does not repeat automatically" ) ) rt.Latched = true;
			}
		}

		if ( !fired && rt.IdleTicks < IdleCap ) rt.IdleTicks++;
		if ( !held ) rt.ShotCount = 0;

		rt.RateOfFire = held
			? MathF.Min( 1, rt.RateOfFire + trigger.FiringAccelerationRate )
			: MathF.Max( 0, rt.RateOfFire - trigger.FiringDecelerationRate );
		rt.Error = held
			? MathF.Min( 1, rt.Error + trigger.ErrorAccelerationRate )
			: MathF.Max( 0, rt.Error - trigger.ErrorDecelerationRate );
	}

	static bool CanFireGun( in GunState gun, WeaponTag weapon, TriggerTag trigger ) {
		if ( gun.Overheated ) return false;
		var magazine = trigger.Magazine is int m ? weapon.Magazine( m ) : null;
		if ( magazine is null || magazine.RoundsLoadedMaximum <= 0 ) return true;
		return gun.Chamber <= 0 && gun.Loaded >= Math.Max( 1, trigger.RoundsPerShot );
	}

	// Every-round-chambered magazines, like the Scorpion cannon and Banshee fuel rod, wait this between rounds.
	// Others wait it only when they run dry.
	public static float GunChamberTime( MagazineTag magazine ) => MathF.Max( 0.25f, MathF.Max( magazine.ChamberTime, magazine.ReloadTime ) );

	void FireGun( WeaponTag weapon, int index, TriggerTag trigger ) {
		ref var gun = ref _s.Gun;
		ref var rt = ref GunState.Trigger( ref gun, index );
		rt.ShotCount++;

		gun.Heat = MathF.Min( 1, gun.Heat + trigger.HeatGeneratedPerRound );
		if ( weapon.OverheatedThreshold > 0 && gun.Heat >= weapon.OverheatedThreshold ) gun.Overheated = true;

		var magazine = trigger.Magazine is int m ? weapon.Magazine( m ) : null;
		if ( magazine is not null && magazine.RoundsLoadedMaximum > 0 ) {
			gun.Loaded = Math.Max( 0, gun.Loaded - Math.Max( 1, trigger.RoundsPerShot ) );
			if ( gun.Loaded <= 0 || magazine.Flags.Contains( "every round must be chambered" ) ) {
				gun.ChamberLength = GunChamberTime( magazine );
				gun.Chamber = gun.ChamberLength;
			}
		}

		_events?.GunFired( index, rt.ShotCount, rt.Error );
	}

	// grenades

	// The throw lasts its animation; the grenade is in the hand from its second frame and lets go on its key frame.
	void TickGrenade( bool wants ) {
		if ( !_s.Throwing ) {
			if ( wants ) StartGrenade( CurrentTag );
			return;
		}

		_s.ThrowTick++;

		if ( _s.GrenadeInHand && _s.ThrowTick == GrenadeInHandFrame && _s.ThrowTick < _s.ThrowKeyFrame )
			_events?.GrenadeToHand( _s.ThrowType );

		if ( _s.GrenadeInHand && _s.ThrowTick >= _s.ThrowKeyFrame ) {
			_s.GrenadeInHand = false;
			ReleaseGrenade();
		}

		if ( _s.ThrowTick >= _s.ThrowFrames ) {
			_s.ThrowTick = -1;
			_events?.ThrowEnded();
		}
	}

	// Switches to the other grenade type if this one is out.
	// A throw stops a reload, cancels a melee that hasn't hit, and unzooms.
	void StartGrenade( WeaponTag tag ) {
		if ( tag is null || tag.HasFlag( "prevents grenade throwing" ) ) return;
		if ( Grenades( _s.SelectedGrenade ) <= 0 ) {
			if ( Grenades( 1 - _s.SelectedGrenade ) <= 0 ) return;
			_s.SelectedGrenade = 1 - _s.SelectedGrenade;
		}

		var timing = Data.Throw( tag );
		_s.ThrowTick = 0;
		_s.ThrowFrames = timing?.Frames ?? 18;
		_s.ThrowKeyFrame = Math.Max( 0, timing?.KeyFrame ?? 7 );
		_s.ThrowType = _s.SelectedGrenade;
		_s.GrenadeInHand = true;

		ResetWeapon( _s.ActiveSlot );
		_s.MeleeTicks = 0;
		_s.ZoomLevel = -1;
		_events?.ThrowStarted( _s.ThrowType );
	}

	int Grenades( int type ) => type == 0 ? _s.Frags : _s.Plasmas;

	// The grenade leaves the hand: one used up, unless the variant has infinite grenades.
	void ReleaseGrenade() {
		var type = _s.ThrowType;
		if ( Grenades( type ) <= 0 ) return;
		if ( !_env.InfiniteGrenades ) {
			if ( type == 0 ) _s.Frags--;
			else _s.Plasmas--;
		}
		_events?.GrenadeReleased( type );
	}
}
