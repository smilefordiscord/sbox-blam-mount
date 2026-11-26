using System;
using Halo2.Data;

namespace Halo2.Combat;

// Halo 2's magazine states, in its order.
public enum MagazineState : byte { Idle, Reloading, Entering, Continuing, BetweenRounds, Unchambered, Chambering }

public enum TriggerPhase : byte { Idle, Charging, Charged }

public enum BarrelPhase : byte { Idle, Firing, Recovering, DryRecovering, SoftRecovering }

public enum MeleeAnimation : byte { None, Strike, Dash, Lunge }

// The animation the weapon's own model plays, in Halo 2's order. It only changes when the model has that animation.
public enum WeaponModelState : byte { Idle, Fire1, Fire2, Chamber1, Chamber2, Reload1, Reload2, Charged1, Charged2, Ready, PutAway }

// The hand holding a weapon. The right holds the unit's current weapon, the left one only while dual wielding.
public enum Hand : byte { Right, Left }

// Halo 2's four ways to take a ground weapon: into either hand, beside the weapons carried or swapped for one.
[Flags]
public enum PickupOptions : byte { None = 0, AddRight = 1, AddLeft = 2, SwapRight = 4, SwapLeft = 8 }

// One trigger's runtime state.
public record struct TriggerState {
	public TriggerPhase Phase;
	public short Timer;
	// Released since its last shot, so a latch trigger may fire again.
	public bool Armed;
	// Latch-autofire took its press as a tap (primary action) or a hold (secondary action).
	public bool PrimaryDone;
	public bool SecondaryDone;
	// Latch-autofire's reading of the trigger: last tick's throw, where the press started, its phase and hold count.
	public byte LastThrow;
	public byte PressStart;
	public sbyte PressPhase;
	public byte HoldTicks;
}

// One barrel's runtime state.
public record struct BarrelState {
	public BarrelPhase Phase;
	public short Timer;
	public sbyte TicksSinceShot;
	public bool FireRequest;
	// Fired dry, so it won't again until the trigger is let go.
	public bool DryFired;
	// Shots of the current fire, for bursts.
	public short Shots;
	// 0 to 1 along the rate of fire spin up, the error and the recoil kick.
	public float RateOfFire;
	public float Error;
	public float AngleChange;
	// The fractions of a tick the recovery times leave over, carried to the next recovery.
	public float RecoveryCarry;
}

// One carried weapon (one hand's weapon object) and its runtime state.
public record struct WeaponSlotState {
	public string Weapon;
	public int Loaded;
	public int Reserve;
	// Battery used, 0 full, 1 empty.
	public float Age;
	public float Heat;
	public bool Overheated;
	// The last shot was a charged plasma pistol bolt, so its overheat is the super recoil.
	public bool ChargedShot;
	// Ticks the ready or put away animation still holds the triggers.
	public int ReadyTicks;
	public MagazineState Magazine;
	public short MagazineTimer;
	// Ticks to the reload's keyframe, where the rounds go in, and its length when it started; -1 for none.
	public short MagazineKeyTimer;
	public short MagazineKeyTicks;
	public short RechargeRemainder;
	public TriggerState Trigger0;
	public TriggerState Trigger1;
	public BarrelState Barrel0;
	public BarrelState Barrel1;
	// The weapon model's own animation and the ticks it has left.
	public WeaponModelState Model;
	public short ModelTicks;

	public static WeaponSlotState For( string weapon, int loaded, int reserve, float age ) => new() {
		Weapon = weapon,
		Loaded = loaded,
		Reserve = reserve,
		Age = age,
		MagazineKeyTimer = -1,
		MagazineKeyTicks = -1,
	};
}

// Everything a Spartan's weapons, grenades, melee and zoom hold between ticks.
// The host's copy is authoritative. The owner predicts its own and compares it.
public record struct CombatState {
	// Halo 2 keeps a unit's weapons in slots and each hand holds one by its index; the rest are in the backpack.
	// Multiplayer carries at most three: right, left and backpack.
	public const int SlotCount = 3;

	public WeaponSlotState Slot0;
	public WeaponSlotState Slot1;
	public WeaponSlotState Slot2;
	// The right hand's slot, the weapon in hand.
	public int ActiveSlot;
	// The slot the unit wants in hand, readied once the one in hand is put away.
	public int DesiredSlot;
	// Putting the weapon in hand away for a switch.
	public bool PuttingAway;
	// The left hand's slot while dual wielding and the one it wants, -1 for none, as for the right hand.
	public int LeftSlot;
	public int DesiredLeft;
	public bool PuttingAwayLeft;
	// The weapon last readied, so a weapon the host puts in hand is readied too.
	public string ReadiedPath;
	// Ticks a right or a left shot still counts for the other hand's dual wield blend.
	public byte RightShotTicks;
	public byte LeftShotTicks;
	// The zoom level the player asked for; it shows whenever the unit can zoom.
	public int DesiredZoom;
	public MeleeAnimation Melee;
	public byte MeleeStrike;
	public int MeleeElapsed;
	public int MeleeDamageTick;
	// From here a new melee may start; -1 only once the animation ends.
	public int MeleeAgainTick;
	public int MeleeEndTick;
	public bool MeleeAirborne;
	public float MeleeScale;
	public int MeleeCooldown;
	public int LungeCooldown;
	// The capsule id of the target the melee moves to, 0 for none, and whether that movement starts after this tick.
	public int MeleeTarget;
	public bool MeleeMoveStart;
	// A melee key press waiting to start a melee, kept while the key stays held.
	public bool MeleeQueued;
	// Ticks into the throw, -1 when not throwing.
	public int ThrowTick;
	public int ThrowType;
	public bool GrenadeInHand;
	// A grenade key press waiting to start a throw, kept while the key stays held.
	public bool GrenadeQueued;
	public int SelectedGrenade;
	public int Frags;
	public int Plasmas;
	// The last command's CombatButtons, for presses.
	public ushort Buttons;
	public int UseHeldTicks;
	public int SwitchHeldTicks;
	// The CombatSim button groups a pickup or a left weapon drop used up, ignored until all their keys are let go.
	public byte Consumed;

	public static WeaponSlotState EmptySlot => WeaponSlotState.For( null, 0, 0, 0 );

	public static CombatState Empty => new() {
		Slot0 = EmptySlot,
		Slot1 = EmptySlot,
		Slot2 = EmptySlot,
		LeftSlot = -1,
		DesiredLeft = -1,
		ThrowTick = -1,
		DesiredZoom = -1,
		MeleeAgainTick = -1,
	};

	public readonly bool Throwing => ThrowTick >= 0;
	public readonly bool Meleeing => Melee != MeleeAnimation.None;
	public readonly string SlotWeapon( int slot ) => slot switch { 0 => Slot0.Weapon, 1 => Slot1.Weapon, 2 => Slot2.Weapon, _ => null };
	public readonly WeaponSlotState SlotOf( int slot ) => slot switch { 0 => Slot0, 1 => Slot1, 2 => Slot2, _ => EmptySlot };
	public readonly bool Holds( int slot ) => !string.IsNullOrEmpty( SlotWeapon( slot ) );

	// Halo 2's unit dual wields while both hands hold a weapon.
	public readonly bool Dual => LeftSlot >= 0 && Holds( ActiveSlot );
	public readonly int HandSlot( Hand hand ) => hand == Hand.Right ? ActiveSlot : LeftSlot;
	public readonly Hand? HandOf( int slot ) => slot == ActiveSlot ? Hand.Right : slot >= 0 && slot == LeftSlot ? Hand.Left : null;

	// The first carried weapon in neither hand, -1 for none.
	public readonly int Backpack {
		get {
			for ( var i = 0; i < SlotCount; i++ )
				if ( Holds( i ) && i != ActiveSlot && i != LeftSlot ) return i;
			return -1;
		}
	}

	public readonly int FreeSlot {
		get {
			for ( var i = 0; i < SlotCount; i++ )
				if ( !Holds( i ) ) return i;
			return -1;
		}
	}

	// The unit can't zoom while it throws, melees, switches or either hand's magazine is busy.
	// The right hand's weapon gives the zoom levels, or the helmet's binoculars for one without, dual wielding too.
	public readonly bool CanZoom => !Throwing && !Meleeing && !PuttingAway && SlotOf( ActiveSlot ).Magazine == MagazineState.Idle
		&& (LeftSlot < 0 || SlotOf( LeftSlot ).Magazine == MagazineState.Idle);
	public readonly int ZoomLevel => CanZoom ? DesiredZoom : -1;

	public static ref WeaponSlotState Slot( ref CombatState s, int index ) {
		if ( index == 0 ) return ref s.Slot0;
		if ( index == 1 ) return ref s.Slot1;
		return ref s.Slot2;
	}

	public static ref TriggerState Trigger( ref WeaponSlotState s, int index ) {
		if ( index == 0 ) return ref s.Trigger0;
		return ref s.Trigger1;
	}

	public static ref BarrelState Barrel( ref WeaponSlotState s, int index ) {
		if ( index == 0 ) return ref s.Barrel0;
		return ref s.Barrel1;
	}

	// What differs, for halo_net_showerrors.
	public readonly string Differences( in CombatState o ) {
		var parts = new List<string>();
		if ( Slot0 != o.Slot0 ) parts.Add( $"slot 0 ({Slot0.Weapon} {Slot0.Loaded}/{Slot0.Reserve} vs {o.Slot0.Weapon} {o.Slot0.Loaded}/{o.Slot0.Reserve})" );
		if ( Slot1 != o.Slot1 ) parts.Add( $"slot 1 ({Slot1.Weapon} {Slot1.Loaded}/{Slot1.Reserve} vs {o.Slot1.Weapon} {o.Slot1.Loaded}/{o.Slot1.Reserve})" );
		if ( Slot2 != o.Slot2 ) parts.Add( $"slot 2 ({Slot2.Weapon} {Slot2.Loaded}/{Slot2.Reserve} vs {o.Slot2.Weapon} {o.Slot2.Loaded}/{o.Slot2.Reserve})" );
		if ( ActiveSlot != o.ActiveSlot || DesiredSlot != o.DesiredSlot || PuttingAway != o.PuttingAway ) parts.Add( $"slot {ActiveSlot}/{DesiredSlot} vs {o.ActiveSlot}/{o.DesiredSlot}" );
		if ( LeftSlot != o.LeftSlot || DesiredLeft != o.DesiredLeft || PuttingAwayLeft != o.PuttingAwayLeft ) parts.Add( $"left {LeftSlot}/{DesiredLeft} vs {o.LeftSlot}/{o.DesiredLeft}" );
		if ( Melee != o.Melee || MeleeElapsed != o.MeleeElapsed || MeleeQueued != o.MeleeQueued || MeleeTarget != o.MeleeTarget ) parts.Add( "melee" );
		if ( ThrowTick != o.ThrowTick || GrenadeInHand != o.GrenadeInHand || GrenadeQueued != o.GrenadeQueued ) parts.Add( "throw" );
		if ( Frags != o.Frags || Plasmas != o.Plasmas || SelectedGrenade != o.SelectedGrenade ) parts.Add( "grenades" );
		if ( DesiredZoom != o.DesiredZoom ) parts.Add( $"zoom {DesiredZoom} vs {o.DesiredZoom}" );
		return parts.Count > 0 ? string.Join( ", ", parts ) : "other fields";
	}

}

// Weapon tag paths as small numbers on the wire: both sides list the same mount's weapons.
public interface IWeaponIndex {
	ushort IndexOf( string path );
	string PathOf( ushort index );
}

// An animation's length and the frames of its events, 0 for none.
// The primary keyframe is a melee's hit, the moment a reload's rounds go in and a grenade's release.
// After the secondary keyframe a melee can be started again; allow interruption ends a ready's hold on the triggers.
public readonly record struct AnimationTiming( int Frames, int PrimaryKeyFrame, int SecondaryKeyFrame, int AllowInterruption );

// The tag data the combat step reads.
public interface ICombatData {
	WeaponTag Weapon( string path );

	// The weapon's first person animation by label ("ready", "reload_empty", "melee_strike_1"), null without one.
	// Dual wielding, a label of the "dual" class wins over the single wielded one.
	AnimationTiming? FirstPerson( string weapon, string label, bool dual );

	// The unit's third person animation by label ("throw_grenade") holding the weapon, null without one.
	AnimationTiming? ThirdPerson( WeaponTag weapon, string label );

	// The globals' player information, for the binoculars.
	PlayerInformationTag Player { get; }

	// The weapon model's own animation by label ("fire_1", "reload_1"), null without one; its "dual" mode's if dual.
	AnimationTiming? WeaponModel( WeaponTag weapon, string label, bool dual );
}

// What the step reads from outside the combat state, the same on host and owner for the same command.
public struct CombatEnvironment {
	// The command's tick, which seeds the step's random picks.
	public int CommandTick;
	public bool InfiniteGrenades;
	// The capsule id of the enemy a strike now would move to, 0 for none: Halo 2's strike cone, in sight.
	public int StrikeTarget;
	// The capsule id of the aim assist's target if a melee only weapon's trigger would dash at it, 0 for none.
	public int DashTarget;
	// The ticks the melee movement expects to reach its target in this tick, -1 when the unit isn't in it.
	public int MeleeEta;
	// The unit is in the air, for the airborne dash and lunge.
	public bool Airborne;
	// What a melee started now deals between its damage bounds, from the unit's speed.
	public float MeleeScale;
}

// What a combat tick did. The host makes the shots and damage, and every machine plays what it sees.
// A replay passes none. A weapon's events name the hand holding it.
public interface ICombatEvents {
	// error is the spread from 0 to 1, or CombatSim.NoError for a zoomed sniper shot.
	// dual is how far the shot's spread and damage go toward the barrel's dual wield values, 0 to 1.
	// recoil is the shot's upward kick in degrees.
	void Fired( Hand hand, int slot, int barrel, int shotCount, float error, float dual, float recoil );
	void DryFired( Hand hand, int slot, int barrel );
	void Overheated( Hand hand, int slot, bool superRecoil );
	void ReloadStarted( Hand hand, int slot, MagazineState state, bool empty );
	// A shell by shell reload ended, with full set if the magazine is full.
	void ReloadEnded( Hand hand, bool full );
	void Chambered( Hand hand, int slot );
	// The left hand readying a weapon starts dual wielding.
	void Readied( Hand hand, string weapon );
	void PutAway( Hand hand, string weapon );
	// The left weapon left the hand to be dropped, which ends dual wielding; weapon is its state as it left.
	void LeftDropped( int slot, WeaponSlotState weapon );
	void MeleeStarted( MeleeAnimation animation, int strike );
	// The melee reaches its keyframe: the hit.
	void MeleeStruck( MeleeAnimation animation );
	void ThrowStarted( int type );
	void GrenadeToHand( int type );
	void GrenadeReleased( int type );
	void ThrowEnded();
	// The action key held long enough to pick up or swap a weapon for the right hand, every tick it stays held.
	void Swap();
	// The switch key held long enough to pick up or swap a weapon for the left hand, every tick it stays held.
	void DualPickup();
}

// One 30 Hz tick of a Spartan's weapons, grenades, melee and zoom, by Halo 2's rules.
// A pure step: host and owner run it on the same commands and must agree exactly.
public sealed class CombatSim {
	// A zoomed shot from a barrel that uses error only unzoomed, like the sniper's, has no spread.
	public const float NoError = -1;

	// Ticks the action or switch key must be held to pick up a weapon: the globals' minimum action hold time of 0.2333 s.
	public const int MinimumWeaponSwapTicks = 7;

	// A unit carries at most two weapons that count toward the maximum.
	public const int MaximumWeapons = 2;

	// Halo 2's button groups: X (reload and action keys) picks up for the right hand, Y (the switch key) for the left.
	public const byte ActionGroup = 1, SwitchGroup = 2;

	// A shot blends the other hand's error, spread and damage toward their dual wield values, fading over 0.3 s.
	const float DualShotSeconds = 0.3f;

	// A melee's hit starts a 0.5 s wait before the next.
	const int MeleeCooldownTicks = 15;

	// A lunge starts a 1.1 s wait before the next.
	const int LungeCooldownTicks = 33;

	// The sword's dash lasts at most 2 s before the lunge.
	const int DashTicks = 60;

	// The dash turns into the lunge once the melee movement expects to arrive within 0.15 s, 0.22 s in the air.
	const int DashArrivalTicks = 5;
	const int AirborneDashArrivalTicks = 7;

	// A strike's first person animations: up to four numbered ones picked at random, else the first of these it has.
	const int MaximumStrikes = 4;
	static readonly string[] StrikeFallbacks = { "melee_1sthit", "melee" };

	// Halo 2's throw counts its ticks from 1 on the tick it starts, and the grenade is in hand at 2.
	const int GrenadeInHandTick = 2;

	// The seed slot a melee's strike pick uses, apart from the barrels'.
	const int MeleeSeed = 7;

	// Latch-autofire reads a press of more than 20 of the trigger's 255 steps.
	const int AutofireSlack = 20;
	const byte FullThrow = 255;

	public ICombatData Data;

	// A shot's random seed from its command tick, like Source's command number seed.
	// The owner's prediction and the host's shot spread pellets the same way, and the client never picks a seed.
	public static int ShotSeed( int commandTick, int shooter, int barrel ) {
		var h = (uint)commandTick * 0x9E3779B1u ^ (uint)shooter * 0x85EBCA77u ^ (uint)(barrel + 1) * 0xC2B2AE3Du;
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
	ushort _released;

	// One tick from one command's buttons. events is null for a replay.
	public void Tick( ref CombatState state, ushort buttons, in CombatEnvironment environment, ICombatEvents events ) {
		_s = state;
		_env = environment;
		_events = events;
		_down = (ushort)(buttons & CombatButtons.All);
		_pressed = (ushort)(_down & ~_s.Buttons);
		_released = (ushort)(_s.Buttons & ~_down);
		_s.Buttons = _down;

		Run();

		state = _s;
		_events = null;
	}

	// The state as it is at the moment an event is raised, mid tick.
	public ref readonly CombatState Current => ref _s;

	// The host replacing the weapon in hand, which cancels its reload.
	public void PutAway( ref CombatState state, int slot ) {
		_s = state;
		_events = null;
		CancelReload( slot );
		state = _s;
	}

	// The host starting Halo 2's pickup action: both hands' switches finish, their reloads stop and a melee ends.
	public void StartPickup( ref CombatState state ) {
		_s = state;
		_events = null;
		CompleteSwitches();
		CancelReloads();
		EndMelee();
		state = _s;
	}

	// The host dropping a hand's weapon outside a tick, at death or for a pickup; returns its slot, -1 for none.
	public int Drop( ref CombatState state, Hand hand, out WeaponSlotState dropped ) {
		_s = state;
		_events = null;
		var slot = hand == Hand.Left ? DropLeft( out dropped ) : DropRight( out dropped );
		state = _s;
		return slot;
	}

	// The host putting the right weapon away at once into the backpack, for a swap while dual wielding with none there.
	public void HolsterRight( ref CombatState state ) {
		_s = state;
		_events = null;
		ForcedPutAway( _s.ActiveSlot );
		_s.PuttingAway = false;
		state = _s;
	}

	bool Down( ushort bit ) => (_down & bit) != 0;
	bool Pressed( ushort bit ) => (_pressed & bit) != 0;
	bool Released( ushort bit ) => (_released & bit) != 0;

	// A press stays kept while its key is held, until it's used.
	bool KeptPress( bool kept, ushort bit ) => Pressed( bit ) || (kept && Down( bit ));

	ref WeaponSlotState Slot( int index ) => ref CombatState.Slot( ref _s, index );
	ref WeaponSlotState Active => ref Slot( _s.ActiveSlot );

	string CurrentPath => _s.SlotWeapon( _s.ActiveSlot );
	WeaponTag CurrentTag => Data.Weapon( CurrentPath );
	WeaponTag TagOf( int slot ) => Data.Weapon( _s.SlotWeapon( slot ) );

	// A hand's weapon can't fire while its user throws, melees or puts that hand's weapon away.
	// The other hand reloading, readying or putting away doesn't hold it.
	bool HandBusy( Hand hand ) => _s.Throwing || _s.Meleeing || (hand == Hand.Right ? _s.PuttingAway : _s.PuttingAwayLeft);

	static ushort FireButton( Hand hand ) => hand == Hand.Right ? CombatButtons.Fire : CombatButtons.FireLeft;

	static bool CanDualWield( WeaponTag tag ) => tag?.CanDualWield ?? false;

	// Halo 2's order: the player's controls start a melee or a throw, then come the unit's reload request, switch and weapons.
	// The weapons update in slot order, the left hand's right after the right hand's.
	// The throw and the melee tick after the weapons, so they free the weapon on the tick after they end.
	// The cooldowns count down last.
	void Run() {
		_s.MeleeMoveStart = false;
		Inventory();
		Grenades();
		_s.UseHeldTicks = Down( CombatButtons.Use ) ? _s.UseHeldTicks + 1 : 0;
		if ( _s.UseHeldTicks >= MinimumWeaponSwapTicks && (_s.Consumed & ActionGroup) == 0 ) _events?.Swap();
		_s.SwitchHeldTicks = Down( CombatButtons.Switch ) ? _s.SwitchHeldTicks + 1 : 0;
		if ( _s.SwitchHeldTicks >= MinimumWeaponSwapTicks && (_s.Consumed & SwitchGroup) == 0 ) _events?.DualPickup();

		// Halo 2 takes the switch on the key's release, since holding it picks up a weapon for the left hand.
		// Dual wielding, the release drops the left weapon instead; a key a pickup or drop used up does neither.
		if ( Released( CombatButtons.Switch ) && (_s.Consumed & SwitchGroup) == 0 ) {
			if ( _s.Dual ) DropLeft( out _ );
			else RotateWeapon();
		}
		ReleaseGroups();
		// Halo 2 forgets the zoom on any tick the unit can't zoom, so it stays off after a throw or melee.
		if ( !_s.CanZoom ) _s.DesiredZoom = -1;
		else if ( Pressed( CombatButtons.Zoom ) ) CycleZoom();
		// A melee or grenade press waits while its key is held until it can start, and starts one.
		MeleeRequest();
		_s.GrenadeQueued = KeptPress( _s.GrenadeQueued, CombatButtons.Grenade );
		if ( _s.GrenadeQueued && StartThrow() ) _s.GrenadeQueued = false;

		// One reload press reloads both hands, each on its own.
		if ( Pressed( CombatButtons.Reload ) && (_s.Consumed & ActionGroup) == 0 ) {
			UnitReload( _s.ActiveSlot );
			if ( _s.LeftSlot >= 0 ) UnitReload( _s.LeftSlot );
		}
		UpdateSwitch();
		UpdateLeftSwitch();

		// Either hand's weapon that can't be dual wielded drops the left one.
		if ( _s.Dual && !(CanDualWield( CurrentTag ) && CanDualWield( TagOf( _s.LeftSlot ) )) ) DropLeft( out _ );

		if ( _s.RightShotTicks > 0 ) _s.RightShotTicks--;
		if ( _s.LeftShotTicks > 0 ) _s.LeftShotTicks--;
		var right = _s.ActiveSlot;
		var left = _s.LeftSlot;
		for ( var i = 0; i < CombatState.SlotCount; i++ ) {
			if ( i == left ) continue;
			WeaponUpdate( i );
			if ( i == right && left >= 0 ) WeaponUpdate( left );
		}

		UpdateThrow();
		UpdateMelee();

		if ( _s.MeleeCooldown > 0 ) _s.MeleeCooldown--;
		if ( _s.LungeCooldown > 0 ) _s.LungeCooldown--;
	}

	// A used up button group counts again once all its keys are let go.
	void ReleaseGroups() {
		if ( !Down( CombatButtons.Use ) && !Down( CombatButtons.Reload ) ) _s.Consumed &= unchecked((byte)~ActionGroup);
		if ( !Down( CombatButtons.Switch ) ) _s.Consumed &= unchecked((byte)~SwitchGroup);
	}

	// Follows the host's inventory changes.
	// An emptied right hand takes the weapon it wants, else another carried, and an empty desired slot is forgotten.
	// A weapon put in hand by a spawn, a pickup or the flag is readied like a switch.
	void Inventory() {
		if ( _s.LeftSlot >= 0 && !_s.Holds( _s.LeftSlot ) ) {
			_s.LeftSlot = -1;
			_s.PuttingAwayLeft = false;
		}
		if ( !_s.Holds( _s.DesiredLeft ) ) _s.DesiredLeft = -1;
		if ( !_s.Holds( _s.ActiveSlot ) && OtherWeapon() is var next && next >= 0 ) {
			_s.ActiveSlot = next;
			_s.PuttingAway = false;
			Ready();
		}
		if ( !_s.Holds( _s.DesiredSlot ) || _s.DesiredSlot == _s.LeftSlot )
			_s.DesiredSlot = _s.ActiveSlot;
		if ( !string.IsNullOrEmpty( CurrentPath ) && CurrentPath != _s.ReadiedPath )
			Ready();
	}

	// The weapon an emptied right hand takes: the one it wants, else the first carried that isn't in the left hand.
	int OtherWeapon() {
		if ( _s.Holds( _s.DesiredSlot ) && _s.DesiredSlot != _s.LeftSlot ) return _s.DesiredSlot;
		for ( var i = 0; i < CombatState.SlotCount; i++ )
			if ( _s.Holds( i ) && i != _s.LeftSlot ) return i;
		return -1;
	}

	// An empty grenade type gives way to the other, or the switch key turns them over, when the other has some.
	void Grenades() {
		var other = 1 - _s.SelectedGrenade;
		if ( (Pressed( CombatButtons.SwitchGrenade ) || GrenadeCount( _s.SelectedGrenade ) <= 0) && GrenadeCount( other ) > 0 )
			_s.SelectedGrenade = other;
	}

	int GrenadeCount( int type ) => type == 0 ? _s.Frags : _s.Plasmas;

	// switching and zoom

	// The switch key turns to the next weapon carried and forgets the zoom.
	void RotateWeapon() {
		for ( var i = 1; i < CombatState.SlotCount; i++ ) {
			var next = (_s.DesiredSlot + i) % CombatState.SlotCount;
			if ( !_s.Holds( next ) || next == _s.LeftSlot ) continue;
			_s.DesiredSlot = next;
			_s.DesiredZoom = -1;
			return;
		}
	}

	// pickups

	// What a player can do with a ground weapon by Halo 2's pickup rules, with the weapon's rounds and battery used.
	// A hand holding a weapon can swap it, and add one unless that weapon must be readied or its trigger is charging.
	// The left hand needs a dual wieldable candidate and right weapon; "can only be dual wielded" leaves only the left.
	// A second of a carried weapon goes only into the other hand, or swaps for a more used one.
	// An empty one is only dual wielded, and only while a carried one of its kind has reserve rounds.
	// At most two carried weapons count, leaving out the left one while the candidate dual wields.
	public static PickupOptions Pickup( in CombatState s, ICombatData data, string weapon, int loaded, int reserve, float age ) {
		var tag = data.Weapon( weapon );
		if ( tag is null ) return PickupOptions.None;

		Span<bool> add = stackalloc bool[2];
		Span<bool> swap = stackalloc bool[2];
		Span<bool> dualContext = stackalloc bool[2];
		for ( var h = 0; h < 2; h++ ) {
			add[h] = swap[h] = true;
			var slot = s.HandSlot( (Hand)h );
			if ( !s.Holds( slot ) ) swap[h] = false;
			else if ( data.Weapon( s.SlotWeapon( slot ) ).MustBeReadied || Charging( s.SlotOf( slot ) ) ) add[h] = false;
		}

		var right = s.Holds( s.ActiveSlot ) ? data.Weapon( s.SlotWeapon( s.ActiveSlot ) ) : null;
		if ( tag.CanDualWield ) {
			if ( !CanDualWield( right ) ) add[1] = swap[1] = false;
			if ( tag.HasFlag( "can only be dual wielded" ) ) add[0] = swap[0] = false;
			dualContext[0] = s.LeftSlot >= 0;
			dualContext[1] = right is not null;
		}
		else {
			add[1] = swap[1] = false;
		}

		bool SameType( string carried ) => carried == weapon || (carried is not null && carried == tag.AgedWeapon);
		var owned = false;
		var ownedReserve = 0;
		for ( var i = 0; i < CombatState.SlotCount; i++ ) {
			if ( !s.Holds( i ) || !SameType( s.SlotWeapon( i ) ) ) continue;
			owned = true;
			ownedReserve += s.SlotOf( i ).Reserve;
		}

		if ( owned ) {
			for ( var h = 0; h < 2; h++ ) {
				if ( !dualContext[h] ) add[h] = false;
				if ( !swap[h] ) continue;
				var held = s.SlotOf( s.HandSlot( (Hand)h ) );
				if ( SameType( held.Weapon ) ? !(held.Age > age) : !dualContext[h] ) add[h] = swap[h] = false;
			}
		}

		if ( tag.Magazine( 0 ) is { RoundsTotalMaximum: > 0 } && loaded == 0 && reserve == 0 ) {
			for ( var h = 0; h < 2; h++ ) {
				var held = s.SlotOf( s.HandSlot( (Hand)h ) );
				if ( ownedReserve <= 0 || !dualContext[h] || (held.Weapon == weapon && held.Reserve == ownedReserve) ) add[h] = swap[h] = false;
			}
		}

		for ( var h = 0; h < 2; h++ ) {
			var slot = s.HandSlot( (Hand)h );
			if ( s.Holds( slot ) && data.Weapon( s.SlotWeapon( slot ) ).MustBeReadied ) add[h] = swap[h] = false;
		}

		var counted = 0;
		for ( var i = 0; i < CombatState.SlotCount; i++ ) {
			if ( !s.Holds( i ) || !data.Weapon( s.SlotWeapon( i ) ).CountsTowardMaximum ) continue;
			if ( tag.CanDualWield && i == s.LeftSlot && i == s.DesiredLeft ) continue;
			counted++;
		}
		if ( counted >= MaximumWeapons ) {
			if ( s.LeftSlot >= 0 && data.Weapon( s.SlotWeapon( s.LeftSlot ) ).CountsTowardMaximum ) add[1] = false;
			if ( tag.CountsTowardMaximum ) {
				add[0] = false;
				if ( right is { CountsTowardMaximum: false } ) swap[0] = false;
			}
		}

		return (add[0] ? PickupOptions.AddRight : 0) | (add[1] ? PickupOptions.AddLeft : 0)
			| (swap[0] ? PickupOptions.SwapRight : 0) | (swap[1] ? PickupOptions.SwapLeft : 0);
	}

	// Halo 2's prompt flags for a pickup: right hand 1 pick up, else 2 swap; plus left hand 4 dual wield, else 8 swap.
	public static int PromptFlags( PickupOptions options ) =>
		((options & PickupOptions.AddRight) != 0 ? 1 : (options & PickupOptions.SwapRight) != 0 ? 2 : 0)
		| ((options & PickupOptions.AddLeft) != 0 ? 4 : (options & PickupOptions.SwapLeft) != 0 ? 8 : 0);

	// A trigger charging, which keeps its hand from taking a second weapon.
	static bool Charging( in WeaponSlotState slot ) => slot.Trigger0.Phase != TriggerPhase.Idle || slot.Trigger1.Phase != TriggerPhase.Idle;

	// The zoom key turns the zoom level over while the unit can zoom.
	void CycleZoom() => _s.DesiredZoom = NextZoom( _s, CurrentTag, Data.Player );

	// The zoom level a press of the zoom key gives, so the owner can show it before the tick runs.
	public static int NextZoom( in CombatState s, WeaponTag tag, PlayerInformationTag player ) {
		if ( !s.CanZoom ) return s.DesiredZoom;
		var level = s.DesiredZoom + 1;
		return level >= ZoomLevels( tag, player ) ? -1 : level;
	}

	// The weapon's own zoom levels, or the binoculars' for a weapon without unless it "forces no binoculars".
	public static int ZoomLevels( WeaponTag tag, PlayerInformationTag player ) {
		if ( tag?.MagnificationLevels > 0 ) return tag.MagnificationLevels;
		return tag?.HasFlag( "forces no binoculars" ) ?? false ? 0 : Math.Max( player?.BinocularsZoomCount ?? 0, 0 );
	}

	// The binocular zoom: in use while zoomed with a weapon that has no zoom of its own.
	public static bool Binoculars( WeaponTag tag, int level ) => level >= 0 && (tag?.MagnificationLevels ?? 0) <= 0;

	// A switch puts the weapon in hand away for its first person "put_away", then readies the other.
	// Only a throw holds it back; starting the put-away ends a melee and cancels both hands' reloads.
	// Switching back during the put-away just ends it: no ready, and its block and the barrels' recovery stay.
	void UpdateSwitch() {
		if ( _s.DesiredSlot == _s.ActiveSlot ) {
			_s.PuttingAway = false;
			return;
		}
		if ( _s.Throwing ) return;

		if ( !_s.PuttingAway ) {
			_s.PuttingAway = true;
			EndMelee();
			CancelReloads();
			PutAwayWeapon( _s.ActiveSlot, Hand.Right );
		}
		if ( Active.ReadyTicks > 0 ) return;

		_s.PuttingAway = false;
		_s.ActiveSlot = _s.DesiredSlot;
		Ready();
	}

	// The left hand readies the weapon a dual wield pickup gave it, after putting away one it holds the same way.
	void UpdateLeftSwitch() {
		if ( _s.DesiredLeft == _s.LeftSlot ) {
			_s.PuttingAwayLeft = false;
			return;
		}
		if ( _s.Throwing ) return;

		if ( _s.LeftSlot >= 0 ) {
			if ( !_s.PuttingAwayLeft ) {
				_s.PuttingAwayLeft = true;
				EndMelee();
				CancelReloads();
				PutAwayWeapon( _s.LeftSlot, Hand.Left );
			}
			if ( Slot( _s.LeftSlot ).ReadyTicks > 0 ) return;
		}

		_s.PuttingAwayLeft = false;
		_s.LeftSlot = _s.DesiredLeft;
		if ( _s.LeftSlot >= 0 ) ReadyLeft();
	}

	// The put-away resets the triggers, forces every barrel into a fresh hard recovery and blocks for its animation.
	void PutAwayWeapon( int index, Hand hand ) {
		ForcedPutAway( index );
		Slot( index ).ReadyTicks = AnimationBlockTicks( _s.SlotWeapon( index ), "put_away" );
		SetModel( index, WeaponModelState.PutAway, force: true );
		_events?.PutAway( hand, _s.SlotWeapon( index ) );
	}

	// A weapon leaving the hand at once, for a drop or a holster: triggers reset, every barrel in a fresh hard recovery.
	void ForcedPutAway( int index ) {
		ref var slot = ref Slot( index );
		slot.Trigger0 = default;
		slot.Trigger1 = default;
		var tag = TagOf( index );
		for ( var b = 0; b < 2; b++ )
			if ( tag?.Barrel( b ) is { } barrel ) Recover( ref CombatState.Barrel( ref slot, b ), barrel, soft: false );
	}

	// A melee, throw or reload starting during a put-away holsters the weapon at once and readies the other.
	void CompleteSwitch( Hand hand ) {
		if ( hand == Hand.Right ) {
			if ( !_s.PuttingAway ) return;
			_s.PuttingAway = false;
			_s.ActiveSlot = _s.DesiredSlot;
			Ready();
			return;
		}
		if ( !_s.PuttingAwayLeft ) return;
		_s.PuttingAwayLeft = false;
		_s.LeftSlot = _s.DesiredLeft;
		if ( _s.LeftSlot >= 0 ) ReadyLeft();
	}

	void CompleteSwitches() {
		CompleteSwitch( Hand.Right );
		CompleteSwitch( Hand.Left );
	}

	// Readying holds the triggers for the first person "ready" up to its allow interruption.
	// It leaves the barrels alone: a recovery the put-away started runs on.
	void Ready() {
		_s.ReadiedPath = CurrentPath;
		ReadyWeapon( _s.ActiveSlot, Hand.Right );
	}

	// The left weapon readies with its "dual" first person ready; the right one isn't readied again and fires on.
	void ReadyLeft() => ReadyWeapon( _s.LeftSlot, Hand.Left );

	void ReadyWeapon( int index, Hand hand ) {
		ref var slot = ref Slot( index );
		slot.Trigger0 = default;
		slot.Trigger1 = default;
		slot.Magazine = MagazineState.Idle;
		slot.MagazineTimer = 0;
		slot.MagazineKeyTimer = slot.MagazineKeyTicks = -1;
		slot.ReadyTicks = AnimationBlockTicks( _s.SlotWeapon( index ), "ready" );
		SetModel( index, WeaponModelState.Ready, force: true );
		_events?.Readied( hand, _s.SlotWeapon( index ) );
	}

	// A ready or put away holds the triggers for its frames, or up to its allow interruption event.
	int AnimationBlockTicks( string weapon, string animation ) {
		if ( weapon is null || Data.FirstPerson( weapon, animation, _s.Dual ) is not { } t ) return 0;
		return t.AllowInterruption > 0 ? Math.Min( t.Frames, t.AllowInterruption ) : t.Frames;
	}

	// dropping

	// Halo 2's left weapon drop: a same weapon in the right hand takes its rounds first.
	// Both hands' switches and reloads stop, a melee ends, and the weapon leaves the hand at once.
	// The right weapon isn't readied again. Returns the slot it left, -1 for none.
	int DropLeft( out WeaponSlotState dropped ) {
		dropped = default;
		if ( _s.LeftSlot < 0 ) return -1;
		TakeRounds( _s.ActiveSlot, _s.LeftSlot );
		CompleteSwitches();
		CancelReloads();
		EndMelee();

		var index = _s.LeftSlot;
		ForcedPutAway( index );
		dropped = Slot( index );
		Slot( index ) = CombatState.EmptySlot;
		_s.LeftSlot = _s.DesiredLeft = -1;
		_s.Consumed |= SwitchGroup;
		_events?.LeftDropped( index, dropped );
		return index;
	}

	// The right weapon's drop, the same way; the hand then takes the weapon it wants, readied.
	int DropRight( out WeaponSlotState dropped ) {
		dropped = default;
		if ( !_s.Holds( _s.ActiveSlot ) ) return -1;
		TakeRounds( _s.LeftSlot, _s.ActiveSlot );
		CompleteSwitches();
		CancelReloads();
		EndMelee();

		var index = _s.ActiveSlot;
		ForcedPutAway( index );
		dropped = Slot( index );
		Slot( index ) = CombatState.EmptySlot;
		_s.ReadiedPath = null;
		return index;
	}

	// Dropping one of two same weapons moves its rounds into the kept one's reserve, up to the inventory maximum:
	// its reserve first, then what it had loaded. The kept one takes the fresher battery.
	void TakeRounds( int keep, int drop ) {
		if ( keep < 0 || !_s.Holds( keep ) || _s.SlotWeapon( keep ) != _s.SlotWeapon( drop ) ) return;
		ref var kept = ref Slot( keep );
		ref var dropped = ref Slot( drop );
		if ( TagOf( keep ).Magazine( 0 ) is { } magazine ) {
			var take = Math.Clamp( magazine.RoundsInventoryMaximum - kept.Reserve, 0, dropped.Reserve );
			kept.Reserve += take;
			dropped.Reserve -= take;
			take = Math.Clamp( magazine.RoundsInventoryMaximum - kept.Reserve, 0, dropped.Loaded );
			kept.Reserve += take;
			dropped.Loaded -= take;
		}
		if ( kept.Age > dropped.Age ) (kept.Age, dropped.Age) = (dropped.Age, kept.Age);
	}

	// the weapon

	// Heat, the ready timer, the magazine, then the triggers and barrels.
	// A weapon not in hand gets no trigger but still cools and settles.
	// Each hand's trigger has its own key: the left one fires the left weapon.
	void WeaponUpdate( int index ) {
		var tag = TagOf( index );
		if ( tag is null ) return;
		ref var slot = ref Slot( index );
		var hand = _s.HandOf( index );

		HeatUpdate( index, tag );
		ModelUpdate( index );
		if ( slot.ReadyTicks > 0 ) slot.ReadyTicks--;
		var blocked = hand is not { } h || HandBusy( h ) || slot.ReadyTicks > 0 || slot.Overheated;
		var fire = hand is { } held && Down( FireButton( held ) ) && !tag.MeleeOnly;

		MagazineUpdate( index, tag, fire );

		for ( var t = 0; t < (tag.NewTriggers?.Count ?? 0) && t < 2; t++ )
			TriggerUpdate( index, tag, t, fire && !blocked, blocked );

		var dual = DualBlend( index, tag );
		for ( var b = 0; b < (tag.Barrels?.Count ?? 0) && b < 2; b++ )
			BarrelUpdate( index, tag, b, blocked, dual );
	}

	// How far a hand's error, spread and damage go toward their dual wield values, from 0 single wielded.
	// All the way on the tick the other hand fires, back to none over the next 0.3 s.
	// The old dual fire error code takes them fully while dual wielding.
	float DualBlend( int index, WeaponTag tag ) {
		if ( !_s.Dual || _s.HandOf( index ) is not { } hand ) return 0;
		if ( tag.HasFlag( "weapon uses old dual fire error code" ) ) return 1;
		var ticks = hand == Hand.Right ? _s.LeftShotTicks : _s.RightShotTicks;
		return MathF.Min( 1, ticks / HaloUnits.TicksPerSecond / DualShotSeconds );
	}

	// The weapon model's animation ends with its frames; a charged or put away model holds its last pose.
	void ModelUpdate( int index ) {
		ref var slot = ref Slot( index );
		if ( slot.ModelTicks <= 0 || --slot.ModelTicks > 0 ) return;
		if ( slot.Model is not (WeaponModelState.Charged1 or WeaponModelState.Charged2 or WeaponModelState.PutAway) )
			slot.Model = WeaponModelState.Idle;
	}

	// The weapon model takes an animation it has. Unless forced, only a fire_1 may become a fire_2 or start over.
	void SetModel( int index, WeaponModelState state, bool force = false ) {
		ref var slot = ref Slot( index );
		if ( !force && slot.Model != WeaponModelState.Idle && (slot.Model is not (WeaponModelState.Fire1 or WeaponModelState.Fire2) || state < slot.Model) ) return;
		if ( Data.WeaponModel( TagOf( index ), ModelLabels[(int)state], _s.Dual ) is not { } t ) return;
		slot.Model = state;
		slot.ModelTicks = (short)Math.Max( 1, t.Frames );
	}

	static readonly string[] ModelLabels = { "idle", "fire_1", "fire_2", "chamber_1", "chamber_2", "reload_1", "reload_2", "charged_1", "charged_2", "ready", "put_away" };

	// At the overheated threshold the weapon overheats.
	// Heat falls every tick, at another rate once overheated and slower as the battery ages.
	// Under the recovery threshold it recovers.
	void HeatUpdate( int index, WeaponTag tag ) {
		ref var slot = ref Slot( index );
		if ( slot.Heat <= 0 ) return;

		if ( slot.Heat >= tag.OverheatedThreshold && !slot.Overheated ) {
			slot.Overheated = true;
			var superRecoil = slot.ChargedShot;
			slot.ChargedShot = false;
			if ( _s.HandOf( index ) is { } hand ) _events?.Overheated( hand, index, superRecoil );
		}

		var rate = slot.Overheated && tag.OverheatedHeatLossPerSecond > 0 ? tag.OverheatedHeatLossPerSecond : tag.HeatLossPerSecond;
		var loss = rate * HaloUnits.TickInterval;
		if ( tag.AgeHeatRecoveryPenalty > 0 ) loss *= 1 - slot.Age * tag.AgeHeatRecoveryPenalty;
		slot.Heat = MathF.Max( slot.Heat - loss, 0 );

		if ( slot.Overheated && tag.HeatRecoveryThreshold > slot.Heat )
			slot.Overheated = false;
	}

	// triggers

	void TriggerUpdate( int index, WeaponTag tag, int triggerIndex, bool held, bool blocked ) {
		ref var slot = ref Slot( index );
		ref var t = ref CombatState.Trigger( ref slot, triggerIndex );
		var trigger = tag.Trigger( triggerIndex );
		if ( t.Timer > 0 ) t.Timer--;

		var primary = trigger.PrimaryBarrel;
		var secondary = trigger.SecondaryBarrel;

		switch ( t.Phase ) {
			case TriggerPhase.Idle:
				switch ( trigger.Behavior ) {
					case "spew":
						if ( held && CanFire( index, tag, primary ) ) RequestFire( index, primary );
						if ( !held ) DryReset( index, primary );
						break;

					case "latch":
					case "latch-rocketlauncher":
						Latch( index, tag, ref t, held, primary, secondary );
						break;

					// Zoomed, the zoom barrel fires: the BR's two are the same, so it's one burst a press either way.
					case "latch-zoom":
						Latch( index, tag, ref t, held, _s.ZoomLevel >= 0 ? secondary : primary, secondary );
						break;

					case "latch-autofire":
						if ( blocked ) {
							t.LastThrow = 0;
							t.PressPhase = 0;
						}
						else if ( held || t.LastThrow > 0 ) {
							Autofire( index, tag, ref t, trigger, held );
						}
						else {
							t.PrimaryDone = t.SecondaryDone = false;
							t.Armed = true;
							DryReset( index, primary );
							DryReset( index, secondary );
						}
						break;

					case "charge":
						if ( held ) StartCharge( index, tag, ref t, trigger );
						else DryReset( index, primary );
						break;
				}
				break;

			// Let go before it's charged, the trigger fires its primary barrel.
			case TriggerPhase.Charging:
				if ( t.Timer == 0 ) {
					t.Phase = TriggerPhase.Charged;
					t.Timer = (short)Ticks( trigger.ChargedTime );
				}
				else if ( !held ) {
					RequestFire( index, primary );
					t.Phase = TriggerPhase.Idle;
					t.Timer = 0;
				}
				break;

			// Charged, letting go fires the secondary barrel; held past its charged time, the overcharged action runs.
			case TriggerPhase.Charged:
				if ( !held && !blocked ) {
					ReleaseCharge( index, ref t, trigger );
				}
				else if ( t.Timer == 0 && trigger.OverchargedAction == "discharge" ) {
					ReleaseCharge( index, ref t, trigger );
				}
				break;
		}
	}

	// A latch fires once per press: released, it arms again.
	void Latch( int index, WeaponTag tag, ref TriggerState t, bool held, int? barrel, int? secondary ) {
		if ( held ) {
			if ( t.Armed && CanFire( index, tag, barrel ) ) {
				RequestFire( index, barrel );
				t.Armed = false;
			}
			return;
		}
		t.Armed = true;
		DryReset( index, barrel );
		DryReset( index, secondary );
	}

	// Latch-autofire, the plasma pistol: a press let go before the autofire time is a tap, the primary action.
	// Held fully past it, it's the secondary action, which charges.
	// Our trigger is a key, so a held press reads as the full throw.
	void Autofire( int index, WeaponTag tag, ref TriggerState t, TriggerTag trigger, bool held ) {
		var current = held ? FullThrow : (byte)0;
		var previous = t.LastThrow;
		var holdTicks = Ticks( trigger.AutofireTime );
		var throwLimit = (int)(trigger.AutofireThrow * FullThrow);
		bool tap = false, hold = false, release = false, holding = false;

		if ( current < previous ) {
			if ( t.PressPhase == 1 ) {
				if ( previous - t.PressStart > AutofireSlack ) tap = true;
				t.PressPhase = -1;
				t.HoldTicks = 0;
				t.PressStart = previous;
			}
		}
		else if ( current == previous ) {
			holding = t.PressPhase == 1;
		}
		else if ( t.PressPhase == -1 ) {
			if ( t.PressStart - previous > AutofireSlack ) release = true;
			t.PressStart = previous;
			t.PressPhase = 1;
			t.HoldTicks = 0;
		}
		else if ( t.PressPhase == 0 ) {
			t.PressStart = 0;
			t.PressPhase = 1;
			t.HoldTicks = 0;
		}
		else {
			holding = true;
		}

		if ( holding ) {
			t.HoldTicks = (byte)Math.Min( t.HoldTicks + 1, 255 );
			if ( t.HoldTicks >= holdTicks ) {
				if ( current - t.PressStart < throwLimit && current < 250 ) tap = true;
				else hold = true;
			}
		}

		if ( current == 0 && !tap && !hold ) release = true;
		if ( release ) {
			t.PrimaryDone = t.SecondaryDone = false;
			DryReset( index, trigger.PrimaryBarrel );
			DryReset( index, trigger.SecondaryBarrel );
		}
		else {
			if ( tap && (t.PrimaryDone || t.SecondaryDone) ) tap = false;
			if ( hold && (t.SecondaryDone || (trigger.HasFlag( "autofire single action only" ) && t.PrimaryDone)) ) hold = false;
			if ( tap ) {
				t.PrimaryDone = true;
				TriggerAction( index, tag, ref t, trigger, trigger.PrimaryAction, trigger.PrimaryBarrel, trigger.SecondaryBarrel );
			}
			if ( hold ) {
				t.SecondaryDone = true;
				TriggerAction( index, tag, ref t, trigger, trigger.SecondaryAction, trigger.SecondaryBarrel, trigger.PrimaryBarrel );
			}
		}

		t.LastThrow = current;
	}

	void TriggerAction( int index, WeaponTag tag, ref TriggerState t, TriggerTag trigger, string action, int? barrel, int? other ) {
		switch ( action ) {
			case "fire":
				if ( CanFire( index, tag, barrel ) ) RequestFire( index, barrel );
				break;
			case "fire other":
				if ( CanFire( index, tag, other ) ) RequestFire( index, other );
				break;
			case "charge":
				StartCharge( index, tag, ref t, trigger );
				break;
		}
	}

	// A charge starts while the secondary barrel is idle and the battery isn't empty.
	void StartCharge( int index, WeaponTag tag, ref TriggerState t, TriggerTag trigger ) {
		ref var slot = ref Slot( index );
		if ( trigger.SecondaryBarrel is int s && CombatState.Barrel( ref slot, s ).Phase != BarrelPhase.Idle ) return;
		if ( slot.Age >= 1 ) return;
		t.Phase = TriggerPhase.Charging;
		t.Timer = (short)Ticks( trigger.ChargingTime );
	}

	// The charged shot goes from the secondary barrel whatever its state.
	void ReleaseCharge( int index, ref TriggerState t, TriggerTag trigger ) {
		RequestFire( index, trigger.SecondaryBarrel );
		t.Phase = TriggerPhase.Idle;
		t.PrimaryDone = t.SecondaryDone = false;
	}

	void RequestFire( int index, int? barrel ) {
		if ( barrel is not int b || b < 0 || b > 1 ) return;
		CombatState.Barrel( ref Slot( index ), b ).FireRequest = true;
	}

	// Letting go of the trigger clears a dry fire, unless the barrel is mid fire.
	void DryReset( int index, int? barrel ) {
		if ( barrel is not int b || b < 0 || b > 1 ) return;
		ref var state = ref CombatState.Barrel( ref Slot( index ), b );
		if ( state.Phase != BarrelPhase.Firing ) state.DryFired = false;
	}

	// Whether a barrel can be asked to fire: not hard recovering, overheated, dry and short of rounds, or reloading.
	// The shotgun's trigger instead interrupts its reload.
	bool CanFire( int index, WeaponTag tag, int? barrelIndex ) {
		if ( barrelIndex is not int b || tag.Barrel( b ) is not { } barrel ) return false;
		ref var slot = ref Slot( index );
		ref var state = ref CombatState.Barrel( ref slot, b );
		if ( slot.Age >= 1 && tag.HasFlag( "cannot fire at maximum age" ) && state.DryFired ) return false;
		if ( state.Phase == BarrelPhase.Recovering || slot.Overheated ) return false;

		if ( tag.Magazine( barrel.Magazine ) is null ) return true;
		var partial = barrel.HasFlag( "can fire with partial ammo" );
		var shotgun = tag.WeaponType == "shotgun";
		if ( slot.Loaded < barrel.RoundsPerShot && !partial && (shotgun || state.DryFired) ) return false;
		if ( slot.Loaded < barrel.MinimumRoundsLoaded && state.DryFired ) return false;
		if ( slot.Magazine == MagazineState.Idle ) return true;
		if ( !shotgun ) return false;

		ExitReload( index, fired: true );
		return true;
	}

	// barrels

	// A requested barrel starts firing once idle, spacing its shots by its rate of fire.
	// It recovers once its shots per fire are done or the request lapses.
	// Error and the spin up climb every tick it's firing and fall otherwise; dual blends the error's rates.
	void BarrelUpdate( int index, WeaponTag tag, int barrelIndex, bool blocked, float dual ) {
		ref var slot = ref Slot( index );
		ref var b = ref CombatState.Barrel( ref slot, barrelIndex );
		var barrel = tag.Barrel( barrelIndex );
		var firing = false;
		var shot = false;
		var recoil = b.AngleChange;

		if ( b.Timer > 0 ) b.Timer--;

		switch ( b.Phase ) {
			case BarrelPhase.SoftRecovering:
				if ( b.Timer == 0 ) EndRecovery( ref b, barrel );
				break;
			case BarrelPhase.DryRecovering:
				AutoReload( index, tag, barrel );
				if ( b.Timer == 0 ) BeginSoftRecovery( ref b, barrel );
				break;
			case BarrelPhase.Recovering:
				if ( b.Timer == 0 ) BeginSoftRecovery( ref b, barrel );
				break;
		}

		if ( b.Phase == BarrelPhase.Idle ) {
			if ( blocked ) b.FireRequest = false;
			else if ( b.FireRequest ) {
				if ( !OtherBarrelStops( slot, tag, barrelIndex ) && b.Timer == 0 ) b.Phase = BarrelPhase.Firing;
			}
		}

		if ( b.Phase == BarrelPhase.Firing ) {
			if ( blocked ) {
				Recover( ref b, barrel, false );
			}
			else {
				var now = FireDecision( slot, tag, barrelIndex, barrel, ref b, out var canStop, out var mustStop, out var soft );
				if ( now ) {
					if ( canStop && !b.FireRequest ) Recover( ref b, barrel, soft );
					else {
						firing = true;
						b.FireRequest = false;
						shot = FireShot( index, tag, barrelIndex, barrel );
					}
				}
				else if ( mustStop || (canStop && !b.FireRequest) ) {
					Recover( ref b, barrel, soft );
				}
				else {
					firing = true;
					b.TicksSinceShot = (sbyte)Math.Min( b.TicksSinceShot + 1, 127 );
				}
			}
		}

		var dt = HaloUnits.TickInterval;
		b.RateOfFire = firing ? MathF.Min( dt * barrel.RateOfFireAccelerationRate + b.RateOfFire, 1 ) : MathF.Max( b.RateOfFire - dt * barrel.RateOfFireDecelerationRate, 0 );
		if ( firing ) {
			b.AngleChange = MathF.Min( dt * barrel.AngleChangeAccelerationRate + b.AngleChange, 1 );
			b.Error = MathF.Min( dt * ((1 - dual) * barrel.ErrorAccelerationRate + dual * barrel.DualErrorAccelerationRate) + b.Error, 1 );
		}
		else if ( b.Error > 0 ) {
			// The recoil only settles while the error does.
			b.AngleChange = MathF.Max( b.AngleChange - dt * barrel.AngleChangeDecelerationRate, 0 );
			b.Error = MathF.Max( b.Error - dt * ((1 - dual) * barrel.ErrorDecelerationRate + dual * barrel.DualErrorDecelerationRate), 0 );
		}

		// Projectiles leave after the weapons update, so a shot carries this tick's error.
		if ( shot && _s.HandOf( index ) is { } hand ) {
			var error = _s.ZoomLevel >= 0 && barrel.HasFlag( "use error when unzoomed" ) ? NoError : b.Error;
			_events?.Fired( hand, index, barrelIndex, b.Shots, error, dual, RecoilKick( barrel, recoil ) );
		}
	}

	// The recoil a shot kicks the view up by: the kick per shot at the recoil built so far, along its function.
	static float RecoilKick( BarrelTag barrel, float angleChange ) {
		if ( barrel.AngleChangePerShot is not { Length: > 1 } kick || (kick[0] == 0 && kick[1] == 0) ) return 0;
		var x = HaloFunctions.Transition( barrel.AngleChangeFunction, angleChange );
		return (kick[1] - kick[0]) * x + kick[0];
	}

	// The other barrel firing, or in the soft part of its recovery, holds this one, for barrels flagged so.
	static bool OtherBarrelStops( in WeaponSlotState slot, WeaponTag tag, int barrelIndex ) {
		if ( (tag.Barrels?.Count ?? 0) < 2 ) return false;
		var barrel = tag.Barrel( barrelIndex );
		var other = barrelIndex == 0 ? slot.Barrel1 : slot.Barrel0;
		return (other.Phase == BarrelPhase.Firing && barrel.HasFlag( "can't fire when others firing" ))
			|| (other.Phase == BarrelPhase.SoftRecovering && barrel.HasFlag( "can't fire when others recovering" ));
	}

	// Whether a firing barrel shoots this tick: its first shot at once, then each once enough ticks have passed.
	// Below its minimum shots per fire it can't stop, at its maximum it must.
	bool FireDecision( in WeaponSlotState slot, WeaponTag tag, int barrelIndex, BarrelTag barrel, ref BarrelState b, out bool canStop, out bool mustStop, out bool soft ) {
		soft = false;
		var ticks = barrel.TicksPerShot( b.RateOfFire, slot.Age, tag.AgeRateOfFirePenalty );
		var now = !OtherBarrelStops( slot, tag, barrelIndex ) && (b.Shots == 0 || b.TicksSinceShot + 1f >= ticks);

		var lo = barrel.ShotsPerFireMinimum;
		var hi = barrel.ShotsPerFireMaximum;
		if ( lo > 0 && b.Shots < lo ) {
			canStop = false;
			mustStop = false;
		}
		else if ( hi > 0 && b.Shots >= hi ) {
			now = false;
			canStop = false;
			mustStop = true;
		}
		else {
			canStop = true;
			mustStop = false;
		}

		if ( b.DryFired ) {
			mustStop = true;
			soft = true;
			return false;
		}
		return now;
	}

	// One shot: the rounds come out of the magazine, or it fires dry.
	// A shot adds heat and battery age, and a pump weapon is left unchambered.
	bool FireShot( int index, WeaponTag tag, int barrelIndex, BarrelTag barrel ) {
		ref var slot = ref Slot( index );
		ref var b = ref CombatState.Barrel( ref slot, barrelIndex );
		var magazine = tag.Magazine( barrel.Magazine );
		var need = barrel.RoundsPerShot;

		var dry = slot.Age >= 1 && tag.HasFlag( "cannot fire at maximum age" );
		if ( magazine is not null ) {
			if ( slot.Loaded < need && !barrel.HasFlag( "can fire with partial ammo" ) ) dry = true;
			if ( slot.Loaded < barrel.MinimumRoundsLoaded ) dry = true;
		}

		if ( dry ) {
			b.DryFired = true;
			if ( _s.HandOf( index ) is { } hand ) _events?.DryFired( hand, index, barrelIndex );
			b.TicksSinceShot = 0;
			return false;
		}

		if ( magazine is not null ) {
			slot.Loaded = Math.Max( 0, slot.Loaded - need );
			if ( magazine.HasFlag( "every round must be chambered" ) ) {
				slot.Magazine = MagazineState.Unchambered;
				slot.MagazineTimer = -1;
			}
		}

		b.Shots++;
		SetModel( index, barrelIndex == 0 ? WeaponModelState.Fire1 : WeaponModelState.Fire2 );
		slot.Heat = MathF.Min( slot.Heat + barrel.HeatGeneratedPerRound, 1 );
		slot.Age = MathF.Min( slot.Age + barrel.AgeGeneratedPerRound, 1 );
		if ( barrelIndex == 1 && tag.WeaponType == "plasma pistol" ) slot.ChargedShot = true;
		b.TicksSinceShot = 0;
		if ( index == _s.ActiveSlot ) _s.RightShotTicks = (byte)Ticks( DualShotSeconds );
		else _s.LeftShotTicks = (byte)Ticks( DualShotSeconds );
		return true;
	}

	// Recovery: the fire recovery time in whole ticks, the hard part first, carrying the leftover fractions.
	// A dry fire recovers softly.
	static void Recover( ref BarrelState b, BarrelTag barrel, bool soft ) {
		var recovery = MathF.Max( HaloUnits.TicksPerSecond * barrel.FireRecoveryTime, 1 );
		var ticks = (int)MathF.Floor( (1 - barrel.SoftRecoveryFraction) * recovery );
		b.RecoveryCarry += recovery - MathF.Floor( recovery );
		if ( b.RecoveryCarry >= 1 ) {
			var whole = MathF.Floor( b.RecoveryCarry );
			ticks += (int)whole;
			b.RecoveryCarry -= whole;
		}
		b.Phase = soft ? BarrelPhase.DryRecovering : BarrelPhase.Recovering;
		b.Timer = (short)Math.Max( ticks, 1 );
		b.TicksSinceShot = 127;
	}

	// The soft part of the recovery, where a press is queued for when it ends.
	static void BeginSoftRecovery( ref BarrelState b, BarrelTag barrel ) {
		b.Phase = BarrelPhase.SoftRecovering;
		var full = HaloUnits.TicksPerSecond * barrel.FireRecoveryTime;
		b.Timer = (short)((int)MathF.Floor( full ) - (int)MathF.Floor( (1 - barrel.SoftRecoveryFraction) * barrel.FireRecoveryTime * HaloUnits.TicksPerSecond ));
		if ( b.Timer == 0 ) EndRecovery( ref b, barrel );
	}

	static void EndRecovery( ref BarrelState b, BarrelTag barrel ) {
		b.Shots = 0;
		b.Phase = BarrelPhase.Idle;
		b.Timer = 1;
		if ( !barrel.HasFlag( "don't clear fire bit after recovering" ) ) b.FireRequest = false;
	}

	// magazines

	// Rounds a reload can take: this magazine's reserve.
	static bool NeedsRounds( in WeaponSlotState slot, BarrelTag barrel ) =>
		(slot.Loaded < barrel.RoundsPerShot && !barrel.HasFlag( "can fire with partial ammo" )) || slot.Loaded < barrel.MinimumRoundsLoaded || slot.Loaded == 0;

	// A trigger pulled on an empty magazine fires dry, and the dry recovery asks for a reload of that weapon unless its hand
	// is busy. Halo 2 has no other reload of its own: an emptied weapon waits for the reload key or a pull.
	void AutoReload( int index, WeaponTag tag, BarrelTag barrel ) {
		if ( tag.Magazine( barrel.Magazine ) is null || _s.HandOf( index ) is not { } hand || HandBusy( hand ) ) return;
		if ( NeedsRounds( Slot( index ), barrel ) ) ReloadRequest( index );
	}

	// A hand's reload request, from the reload key or a reload going on to its next shell. Nothing during a throw.
	// Otherwise it first completes that hand's switch under way, which leaves the hand empty, and ends a melee.
	// Both happen even when there is nothing to reload: the reload key's melee cancel.
	void UnitReload( int index ) {
		if ( _s.Throwing || _s.HandOf( index ) is not { } hand ) return;
		var switched = hand == Hand.Right ? _s.PuttingAway : _s.PuttingAwayLeft;
		CompleteSwitch( hand );
		EndMelee();
		if ( !switched ) ReloadRequest( index );
	}

	// The weapon's reload: from an idle or unchambered magazine, with idle triggers and model, rounds to load and room for them.
	// A shotgun enters its reload, then goes on a shell at a time; anything else plays one reload.
	// It ends every barrel's recovery but the shotgun's, so a barrel keeping its fire bit fires at once.
	// A reload that starts unzooms, and the zoom stays off until the player zooms again.
	void ReloadRequest( int index ) {
		var tag = TagOf( index );
		var magazine = tag?.Magazine( 0 );
		if ( magazine is null ) return;
		ref var slot = ref Slot( index );

		var shotgun = tag.WeaponType == "shotgun";
		var next = shotgun ? (slot.Magazine is MagazineState.Continuing or MagazineState.BetweenRounds ? MagazineState.Continuing : MagazineState.Entering) : MagazineState.Reloading;
		var allowed = slot.Magazine is MagazineState.Idle or MagazineState.Unchambered || (slot.Magazine == MagazineState.BetweenRounds && next == MagazineState.Continuing);
		if ( !allowed || slot.Trigger0.Phase != TriggerPhase.Idle || slot.Trigger1.Phase != TriggerPhase.Idle || slot.Model != WeaponModelState.Idle ) return;
		if ( slot.Reserve <= 0 || slot.Loaded >= magazine.RoundsLoadedMaximum ) return;

		slot.Trigger0.Timer = slot.Trigger1.Timer = 0;
		for ( var b = 0; b < 2; b++ ) {
			if ( tag.Barrel( b ) is not { } barrel ) continue;
			ref var state = ref CombatState.Barrel( ref slot, b );
			if ( state.Phase != BarrelPhase.Firing ) state.DryFired = false;
			if ( !shotgun ) EndRecovery( ref state, barrel );
		}

		var empty = slot.Loaded == 0;
		_s.DesiredZoom = -1;
		SetMagazine( index, tag, next );
		SetModel( index, WeaponModelState.Reload1 );
		if ( _s.HandOf( index ) is { } hand ) _events?.ReloadStarted( hand, index, next, empty );
	}

	// A reload state's timer is its first person animation; the rounds go in at its primary keyframe, else at its end.
	void SetMagazine( int index, WeaponTag tag, MagazineState state ) {
		ref var slot = ref Slot( index );
		var animation = state switch {
			MagazineState.Reloading => slot.Loaded == 0 ? "reload_empty" : "reload_full",
			MagazineState.Entering => "reload_enter",
			MagazineState.Continuing => slot.Loaded == 0 ? "reload_continue_empty" : "reload_continue_full",
			_ => null,
		};

		slot.Magazine = state;
		slot.MagazineTimer = 0;
		slot.MagazineKeyTimer = slot.MagazineKeyTicks = -1;
		if ( animation is not null && Data.FirstPerson( _s.SlotWeapon( index ), animation, _s.Dual ) is { } t ) {
			slot.MagazineTimer = (short)Math.Max( 1, t.Frames );
			if ( state != MagazineState.Entering )
				slot.MagazineKeyTimer = slot.MagazineKeyTicks = (short)Math.Max( 1, t.PrimaryKeyFrame > 0 ? t.PrimaryKeyFrame : t.Frames );
		}
		else if ( state == MagazineState.Chambering ) {
			slot.MagazineTimer = (short)Ticks( tag.Magazine( 0 ).ChamberTime );
		}
	}

	// A battery magazine recharges; a reload's rounds go in at its keyframe, and at its end it goes on or stops.
	// Stopped, the magazine chambers once its triggers are idle.
	void MagazineUpdate( int index, WeaponTag tag, bool fireHeld ) {
		var magazine = tag.Magazine( 0 );
		if ( magazine is null ) return;
		ref var slot = ref Slot( index );

		if ( magazine.RoundsRecharged > 0 && slot.Loaded < magazine.RoundsLoadedMaximum ) {
			var perTick = (int)HaloUnits.TicksPerSecond;
			slot.Loaded += magazine.RoundsRecharged / perTick;
			slot.RechargeRemainder += (short)(magazine.RoundsRecharged % perTick);
			if ( slot.RechargeRemainder >= perTick ) {
				slot.Loaded++;
				slot.RechargeRemainder -= (short)perTick;
			}
			slot.Loaded = Math.Min( slot.Loaded, magazine.RoundsLoadedMaximum );
		}

		if ( slot.MagazineTimer > 0 ) slot.MagazineTimer--;
		if ( slot.MagazineKeyTimer > 0 ) slot.MagazineKeyTimer--;

		switch ( slot.Magazine ) {
			case MagazineState.Chambering:
				if ( slot.MagazineTimer == 0 ) SetMagazine( index, tag, MagazineState.Idle );
				break;

			case MagazineState.Unchambered:
				StartChamber( index, tag );
				break;

			case MagazineState.Reloading:
			case MagazineState.Entering:
			case MagazineState.Continuing:
				if ( slot.MagazineKeyTimer > slot.MagazineTimer ) slot.MagazineKeyTimer = slot.MagazineTimer;
				if ( slot.MagazineKeyTimer == 0 ) LoadRounds( index, magazine );
				if ( slot.MagazineTimer != 0 ) break;

				var wasReload = slot.Magazine == MagazineState.Reloading;
				SetMagazine( index, tag, wasReload ? MagazineState.Idle : MagazineState.BetweenRounds );
				var holding = fireHeld && CanFireSilently( index, tag, 0 );
				if ( slot.Reserve > 0 && slot.Loaded < magazine.RoundsLoadedMaximum && !magazine.HasFlag( "wastes rounds when reloaded" ) && !holding ) {
					UnitReload( index );
					if ( Slot( index ).Magazine == MagazineState.Continuing ) break;
				}
				ExitReload( index, fired: false );
				break;
		}
	}

	bool CanFireSilently( int index, WeaponTag tag, int barrelIndex ) {
		ref var slot = ref Slot( index );
		var state = slot.Magazine;
		slot.Magazine = MagazineState.Idle;
		var ok = CanFire( index, tag, barrelIndex );
		slot.Magazine = state;
		return ok;
	}

	// The reload is over: a shell by shell reload says whether it's full, and the magazine waits to chamber.
	void ExitReload( int index, bool fired ) {
		ref var slot = ref Slot( index );
		var shellByShell = slot.Magazine is MagazineState.Continuing or MagazineState.BetweenRounds or MagazineState.Entering;
		slot.Magazine = MagazineState.Unchambered;
		slot.MagazineTimer = -1;
		slot.MagazineKeyTimer = slot.MagazineKeyTicks = -1;
		if ( !fired && shellByShell && _s.HandOf( index ) is { } hand && TagOf( index )?.Magazine( 0 ) is { } magazine )
			_events?.ReloadEnded( hand, slot.Loaded >= magazine.RoundsLoadedMaximum );
	}

	// The rounds go in, taken from the reserve, all of what was loaded first for a magazine that wastes rounds.
	void LoadRounds( int index, MagazineTag magazine ) {
		ref var slot = ref Slot( index );
		if ( magazine.HasFlag( "wastes rounds when reloaded" ) ) slot.Loaded = 0;
		var loaded = Math.Min( slot.Loaded + Math.Min( magazine.RoundsReloaded, slot.Reserve ), magazine.RoundsLoadedMaximum );
		slot.Reserve -= loaded - slot.Loaded;
		slot.Loaded = loaded;
		slot.MagazineKeyTimer = slot.MagazineKeyTicks = -1;
	}

	// An unchambered magazine chambers for its chamber time once both triggers are idle.
	void StartChamber( int index, WeaponTag tag ) {
		ref var slot = ref Slot( index );
		if ( slot.Trigger0.Phase != TriggerPhase.Idle || slot.Trigger1.Phase != TriggerPhase.Idle ) return;
		SetMagazine( index, tag, MagazineState.Chambering );
		if ( _s.HandOf( index ) is { } hand ) _events?.Chambered( hand, index );
	}

	// A reload cut short by a switch, melee or throw, shell by shell ones too, leaves the magazine idle with no chamber.
	// A plain reload still loads if more than half the time to its keyframe has passed.
	// Its key timers are -1 once the rounds are in, which passes that test too, so a reload cut after its keyframe loads again.
	void CancelReload( int index ) {
		ref var slot = ref Slot( index );
		if ( TagOf( index )?.Magazine( 0 ) is not { } magazine ) return;
		if ( slot.Magazine is not (MagazineState.Reloading or MagazineState.Entering or MagazineState.Continuing or MagazineState.BetweenRounds) ) return;
		if ( slot.Magazine == MagazineState.Reloading && 2 * slot.MagazineKeyTimer < slot.MagazineKeyTicks )
			LoadRounds( index, magazine );
		slot.Magazine = MagazineState.Idle;
		slot.MagazineTimer = -1;
		slot.MagazineKeyTimer = slot.MagazineKeyTicks = -1;
	}

	void CancelReloads() {
		CancelReload( _s.ActiveSlot );
		if ( _s.LeftSlot >= 0 ) CancelReload( _s.LeftSlot );
	}

	// melee

	// Halo 2's melee requests. A melee only weapon's held trigger strikes, or dashes at a target in full autoaim, while the
	// lunge cooldown allows; a gun's held trigger blocks the melee key unless the gun "primary trigger melee attacks".
	// A melee key press starts one strike, as soon as the cooldown allows if the key is still held.
	void MeleeRequest() {
		_s.MeleeQueued = KeptPress( _s.MeleeQueued, CombatButtons.Melee );
		var tag = CurrentTag;
		if ( tag is null ) return;

		var dash = false;
		if ( Down( CombatButtons.Fire ) ) {
			if ( tag.MeleeOnly ) {
				if ( _s.LungeCooldown > 0 || _s.PuttingAway ) return;
				dash = _env.DashTarget != 0;
			}
			else if ( !tag.HasFlag( "primary trigger melee attacks" ) ) return;
		}
		else if ( _s.MeleeQueued ) {
			if ( _s.MeleeCooldown > 0 ) return;
			if ( tag.MeleeOnly && _s.LungeCooldown > 0 ) return;
		}
		else return;

		if ( !StartMelee( tag, dash ) ) return;
		_s.MeleeQueued = false;
		if ( dash ) _s.LungeCooldown = LungeCooldownTicks;
	}

	// No melee with "prevents melee attack", while a plasma pistol charges, during a throw,
	// or during a melee before its secondary keyframe. Only the right hand's weapon counts.
	// It cancels both hands' reloads, completes their switches, drops the left weapon and unzooms: the melee is single wielded.
	// A strike moves to the strike target in sight, a dash to the aim assist's.
	bool StartMelee( WeaponTag tag, bool dash ) {
		if ( tag.HasFlag( "prevents melee attack" ) || Active.Trigger0.Phase != TriggerPhase.Idle || _s.Throwing ) return false;
		if ( _s.Meleeing && (_s.MeleeAgainTick < 0 || _s.MeleeElapsed < _s.MeleeAgainTick) ) return false;

		CancelReloads();
		CompleteSwitches();
		DropLeft( out _ );
		_s.DesiredZoom = -1;

		var strike = 0;
		if ( !dash ) {
			var strikes = 0;
			while ( strikes < MaximumStrikes && Data.FirstPerson( CurrentPath, StrikeLabel( strikes + 1 ), _s.Dual ) is not null ) strikes++;
			// A shot seed's low two bits are fixed (the tracer flag and a set bit), so the pick uses the bits above them.
			if ( strikes > 0 ) strike = (int)(((uint)ShotSeed( _env.CommandTick, 0, MeleeSeed ) >> 2) % (uint)strikes) + 1;
			for ( var i = 0; strike == 0 && i < StrikeFallbacks.Length; i++ )
				if ( Data.FirstPerson( CurrentPath, StrikeFallbacks[i], _s.Dual ) is not null ) strike = MaximumStrikes + 1 + i;
			if ( strike == 0 ) return false;
		}

		_s.MeleeScale = _env.MeleeScale;
		_s.MeleeAirborne = _env.Airborne;
		if ( dash ) SetMelee( MeleeAnimation.Dash, 0, _s.MeleeAirborne ? "melee_dash_airborne" : "melee_dash" );
		else SetMelee( MeleeAnimation.Strike, strike, StrikeLabel( strike ) );
		_s.MeleeTarget = dash ? _env.DashTarget : _env.StrikeTarget;
		_s.MeleeMoveStart = _s.MeleeTarget != 0;

		_events?.MeleeStarted( _s.Melee, _s.MeleeStrike );
		return true;
	}

	// A strike's first person animation by its number: a numbered strike, then the fallbacks.
	public static string StrikeLabel( int strike ) => strike <= MaximumStrikes ? $"melee_strike_{strike}" : StrikeFallbacks[strike - MaximumStrikes - 1];

	// A strike or lunge hits on its primary keyframe and may be followed from its secondary.
	// A dash has neither and lasts up to 2 s.
	void SetMelee( MeleeAnimation animation, int strike, string name ) {
		var timing = Data.FirstPerson( CurrentPath, name, _s.Dual );
		_s.Melee = animation;
		_s.MeleeStrike = (byte)strike;
		_s.MeleeElapsed = 0;
		if ( animation == MeleeAnimation.Dash ) {
			_s.MeleeDamageTick = -1;
			_s.MeleeAgainTick = -1;
			_s.MeleeEndTick = DashTicks;
			return;
		}
		_s.MeleeDamageTick = timing?.PrimaryKeyFrame ?? 0;
		_s.MeleeAgainTick = timing is { SecondaryKeyFrame: > 0 } t ? t.SecondaryKeyFrame : -1;
		_s.MeleeEndTick = timing?.Frames ?? 0;
	}

	// The melee hits on its damage tick, which starts the cooldown whether anything was hit or not.
	// The dash turns into the lunge once the melee movement expects to arrive soon, has stopped, or after 2 s.
	// The movement only starts on the tick after the dash does, so the dash's first tick doesn't look.
	void UpdateMelee() {
		if ( !_s.Meleeing ) return;

		if ( _s.MeleeElapsed == _s.MeleeDamageTick ) {
			_s.MeleeCooldown = MeleeCooldownTicks;
			_events?.MeleeStruck( _s.Melee );
		}

		_s.MeleeElapsed++;
		if ( _s.Melee == MeleeAnimation.Dash ) {
			var arrival = _env.Airborne ? AirborneDashArrivalTicks : DashArrivalTicks;
			if ( (_s.MeleeElapsed > 1 && _env.MeleeEta <= arrival) || _s.MeleeElapsed >= _s.MeleeEndTick ) StartLunge();
			return;
		}
		if ( _s.MeleeElapsed >= _s.MeleeEndTick ) EndMelee();
	}

	// The lunge starts like a melee of its own, with its damage scale and airborne animation picked afresh.
	// It neither retargets nor plans again: the dash's movement runs on.
	void StartLunge() {
		_s.MeleeScale = _env.MeleeScale;
		_s.MeleeAirborne = _env.Airborne;
		SetMelee( MeleeAnimation.Lunge, 0, _s.MeleeAirborne ? "melee_lunge_airborne" : "melee_lunge" );
		_events?.MeleeStarted( _s.Melee, _s.MeleeStrike );
	}

	// A melee ended early sends the first person weapon nothing, so its strike plays on until the next message.
	// The cooldown its hit started and its movement run on.
	void EndMelee() {
		_s.Melee = MeleeAnimation.None;
		_s.MeleeAgainTick = -1;
	}

	// grenades

	// A throw needs a grenade of the selected type, no throw going, and a weapon model not reloading, charged, readying or put away.
	// Only the right hand's weapon counts.
	// It cancels a melee at any point and both hands' reloads, completes their switches, drops the left weapon and unzooms.
	bool StartThrow() {
		var tag = CurrentTag;
		if ( tag is null || tag.HasFlag( "prevents grenade throwing" ) || _s.Throwing ) return false;
		if ( Active.Model is >= WeaponModelState.Reload1 and <= WeaponModelState.PutAway ) return false;
		if ( GrenadeCount( _s.SelectedGrenade ) <= 0 ) return false;

		CancelReloads();
		EndMelee();
		CompleteSwitches();
		DropLeft( out _ );
		_s.DesiredZoom = -1;

		_s.ThrowTick = 0;
		_s.ThrowType = _s.SelectedGrenade;
		_s.GrenadeInHand = false;
		_events?.ThrowStarted( _s.ThrowType );
		return true;
	}

	// The grenade is taken in hand on the second tick and leaves on the unit's throw_grenade keyframe.
	// Without a keyframe it leaves on the tick after. The throw ends with that animation.
	void UpdateThrow() {
		if ( !_s.Throwing ) return;
		_s.ThrowTick++;
		var timing = Data.ThirdPerson( CurrentTag, "throw_grenade" );

		if ( _s.ThrowTick == GrenadeInHandTick ) {
			if ( GrenadeCount( _s.ThrowType ) <= 0 ) {
				_s.ThrowTick = -1;
				_events?.ThrowEnded();
				return;
			}
			if ( !_env.InfiniteGrenades ) {
				if ( _s.ThrowType == 0 ) _s.Frags--;
				else _s.Plasmas--;
			}
			_s.GrenadeInHand = true;
			_events?.GrenadeToHand( _s.ThrowType );
		}
		else if ( _s.GrenadeInHand ) {
			if ( _s.ThrowTick < (timing?.PrimaryKeyFrame ?? 0) ) return;
			_s.GrenadeInHand = false;
			_events?.GrenadeReleased( _s.ThrowType );
		}
		else if ( _s.ThrowTick > GrenadeInHandTick && _s.ThrowTick >= (timing?.Frames ?? 0) ) {
			_s.ThrowTick = -1;
			_events?.ThrowEnded();
		}
	}

	static int Ticks( float seconds ) => (int)(HaloUnits.TicksPerSecond * seconds + 0.5f);
}
