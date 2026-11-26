using System;

namespace Halo.Combat;

// A unit animation graph's seats, weapon classes and weapon types, each mapping slots ("idle", "fire-1") to animations.
// Slots share animations freely, so names must come from the graph.
// Cyborg is the cyborg graph's seats as read from the tag, in tag order, which Find depends on.
public sealed partial class UnitAnimationGraph {
	// Radians per frame and frame counts either side of straight ahead, for yaw and pitch.
	public readonly record struct AimingScreen( float NegativeYawDelta, float PositiveYawDelta, int NegativeYawFrames, int PositiveYawFrames,
		float NegativePitchDelta, float PositivePitchDelta, int NegativePitchFrames, int PositivePitchFrames );

	public sealed class WeaponType {
		public string Label { get; init; }

		// Slot -> animation name.
		public Dictionary<string, string> Animations { get; init; } = new();

		public string Animation( string slot ) => Animations.TryGetValue( slot, out var name ) ? name : null;
	}

	public sealed class WeaponClass {
		public string Label { get; init; }
		public AimingScreen AimingScreen { get; init; }

		// The unit marker a weapon of this class is held at, and the weapon marker placed on it ("" for its origin).
		public string HandMarker { get; init; } = "";
		public string GripMarker { get; init; } = "";

		// Slot -> animation name.
		public Dictionary<string, string> Animations { get; init; } = new();
		public List<WeaponType> Types { get; init; } = new();

		public string Animation( string slot ) => Animations.TryGetValue( slot, out var name ) ? name : null;
	}

	public sealed class Seat {
		public string Label { get; init; }

		// The seat's "look" overlay and its looking screen bounds.
		public string Look { get; init; }
		public AimingScreen LookingScreen { get; init; }

		// The seat's dying animations: falling ("airborne-dead") and on landing ("landing-dead").
		public string AirborneDead { get; init; }
		public string LandingDead { get; init; }

		// A vehicle seat's getting in and getting out animations ("W-driver enter").
		public string Enter { get; init; }
		public string Exit { get; init; }

		// The seat's "emotions" overlay, one emotion per frame.
		public string Emotions { get; init; }

		// The seat's "talk" overlay: its first frame is the mouth wide open.
		public string Talk { get; init; }

		public List<WeaponClass> Classes { get; init; } = new();
	}

	public string Graph { get; init; }
	public List<Seat> Seats { get; init; } = new();

	// Permuted animations ("%n"): each permutation with its cumulative normalized weight, in chain order.
	public Dictionary<string, (string Name, float Weight)[]> Permutations { get; init; } = new();

	// Picks the first permutation whose cumulative weight reaches a random 0..1.
	public string ChoosePermutation( string animation ) => ChoosePermutation( animation, Game.Random.Float( 0, 1 ) );

	public string ChoosePermutation( string animation, float random ) {
		if ( animation is null || !Permutations.TryGetValue( animation, out var chain ) ) return animation;
		foreach ( var (name, weight) in chain )
			if ( random <= weight ) return name;
		return chain[^1].Name;
	}

	public Seat FindSeat( string label ) => Seats.FirstOrDefault( s => string.Equals( s.Label, label, StringComparison.OrdinalIgnoreCase ) );

	// The seat's class and type for a weapon label.
	// "unarmed" matches an unlabeled type and null matches any.
	// The last matching class wins, as in Halo: the stand seat's "ne" resolves to pistol, not rifle.
	public static bool Find( Seat seat, string weaponLabel, out WeaponClass weaponClass, out WeaponType weaponType ) {
		weaponClass = null;
		weaponType = null;
		if ( seat is null ) return false;

		foreach ( var c in seat.Classes ) {
			foreach ( var t in c.Types ) {
				if ( weaponLabel is null || weaponLabel == "unarmed" && string.IsNullOrEmpty( t.Label ) || string.Equals( weaponLabel, t.Label, StringComparison.OrdinalIgnoreCase ) ) {
					weaponClass = c;
					weaponType = t;
					break;
				}
			}
		}

		return weaponClass is not null;
	}

	// Unit damage animation types.
	public const int SoftPing = 0, HardPing = 1, SoftKill = 2, HardKill = 3;

	public const int DirectionFront = 0, DirectionLeft = 1, DirectionRight = 2, DirectionBack = 3;

	// Damage parts in order: gut, chest, head, l-arm, l-hand, l-leg, l-foot, r-arm, r-hand, r-leg, r-foot.
	public const int PartGut = 0, PartChest = 1, PartHead = 2, DamagePartCount = 11;

	// An animation or null for every type, direction and part, indexed by DamageAnimationIndex.
	public string[] UnitDamage { get; init; } = Array.Empty<string>();

	public static int DamageAnimationIndex( int type, int direction, int part ) => (type * 4 + direction) * DamagePartCount + part;

	// Null if there is none or the index is outside the block.
	public string DamageAnimation( int type, int direction, int part ) {
		var index = DamageAnimationIndex( type, direction, part );
		return index >= 0 && index < UnitDamage.Length ? UnitDamage[index] : null;
	}

	// The first permutation of a permuted animation, else the name itself.
	public string PermutationParent( string name ) {
		if ( name is null ) return null;
		foreach ( var (parent, chain) in Permutations )
			if ( chain.Any( c => c.Name == name ) ) return parent;
		return name;
	}

	// The built-in graph for an antr tag path, if there is one.
	public static UnitAnimationGraph For( string graph ) {
		var stem = Combat.HaloEffects.WithoutExtension( graph );
		return string.Equals( stem, Combat.HaloEffects.WithoutExtension( Cyborg.Graph ), StringComparison.OrdinalIgnoreCase ) ? Cyborg : null;
	}

	const float Quarter = MathF.PI / 4;
	const float Half = MathF.PI / 2;

	static readonly AimingScreen Rifle = new( Half, Half, 1, 1, Half, Half, 1, 1 );
	static readonly AimingScreen Missile = new( Half, Half, 1, 1, Quarter, Quarter, 2, 2 );
	static readonly AimingScreen CrouchFlame = new( Half, Half, 1, 1, Quarter, Half, 1, 1 );
	static readonly AimingScreen None = new( 0, 0, 0, 0, 0, 0, 0, 0 );

	// Both seats' looking screen: 70 degrees of yaw and 45 of pitch either side, one frame each.
	static readonly AimingScreen Looking = new( 1.2217305f, 1.2217305f, 1, 1, Quarter, Quarter, 1, 1 );

	// The Warthog gunner's screen: all the way round in yaw, 20 degrees down and 35 up.
	static readonly AimingScreen WarthogGun = new( Half, Half, 2, 2, 0.34906584f, 0.61086524f, 1, 1 );

	// The Warthog passenger's screens: 22 degrees down, and 30 up (45 with a rocket launcher).
	static readonly AimingScreen Passenger = new( Half, Half, 1, 1, 0.38397244f, 0.5235988f, 1, 1 );
	static readonly AimingScreen PassengerMissile = new( Half, Half, 1, 1, 0.38397244f, Quarter, 1, 1 );

	// The Scorpion riders' screen: 30 degrees down and 60 up.
	static readonly AimingScreen ScorpionRiderScreen = new( Half, Half, 1, 1, 0.5235988f, 1.0471976f, 1, 1 );

	// Idle, turns and moves share one prefix ("stand rifle"); the other slots are given.
	static WeaponClass Class( string label, AimingScreen aim, string locomotion, string airborne, string landSoft, string landHard,
		string throwGrenade, string aimStill, string aimMove, params WeaponType[] types ) {
		var animations = new Dictionary<string, string> {
			["idle"] = $"{locomotion} idle",
			["turn-left"] = $"{locomotion} turn-left",
			["turn-right"] = $"{locomotion} turn-right",
			["move-front"] = $"{locomotion} move-front",
			["move-back"] = $"{locomotion} move-back",
			["move-left"] = $"{locomotion} move-left",
			["move-right"] = $"{locomotion} move-right",
			["airborne"] = airborne,
			["land-soft"] = landSoft,
			["land-hard"] = landHard,
			["throw-grenade"] = throwGrenade,
		};
		if ( aimStill is not null ) animations["aim-still"] = aimStill;
		if ( aimMove is not null ) animations["aim-move"] = aimMove;
		return new WeaponClass { Label = label, AimingScreen = aim, Animations = animations, Types = types.ToList() };
	}

	static WeaponType Type( string label, string reload = null, string fire = null, string melee = null, string fire2 = null, string chamber = null ) {
		var animations = new Dictionary<string, string>();
		if ( reload is not null ) animations["reload-1"] = reload;
		if ( chamber is not null ) animations["chamber-1"] = chamber;
		if ( fire is not null ) animations["fire-1"] = fire;
		if ( fire2 is not null ) animations["fire-2"] = fire2;
		if ( melee is not null ) animations["melee"] = melee;
		return new WeaponType { Label = label, Animations = animations };
	}

	// The cyborg graph: stand and crouch seats plus the multiplayer vehicle seats.
	public static readonly UnitAnimationGraph Cyborg = new() {
		Graph = "characters/cyborg/cyborg.antr",
		Permutations = {
			["alert unarmed idle%1"] = new[] { ("alert unarmed idle%1", 1f / 3), ("alert unarmed idle%2", 2f / 3), ("alert unarmed idle%3", 1f) },
		},
		UnitDamage = CyborgUnitDamage(),
		Seats = {
			new Seat {
				Label = "crouch",
				Look = "stand look",
				LookingScreen = Looking,
				AirborneDead = "stand airborne-dead",
				LandingDead = "stand landing-dead",
				Classes = {
					Class( "rifle", Rifle, "crouch rifle", "crouch rifle airborne", "crouch rifle land-soft", "crouch rifle land-hard",
						"stand rifle throw-grenade", "crouch rifle aim-still", "crouch rifle aim-move",
						Type( "ar", reload: "stand rifle ar reload-1", fire: "stand rifle ar fire-1", melee: "stand rifle ar melee" ),
						Type( "sg", melee: "stand rifle sr melee" ),
						Type( "sr", reload: "stand rifle sr reload-1", melee: "stand rifle sr melee" ),
						Type( "pr", fire: "stand rifle ar fire-1", melee: "stand rifle sr melee" ),
						Type( "f", melee: "stand rifle f melee" ) ),
					Class( "pistol", Rifle, "crouch pistol", "stand unarmed airborne", "stand missile land-soft", "stand missile land-hard",
						"stand rifle throw-grenade", "crouch pistol aim-still", null,
						Type( "hp", reload: "stand pistol hp reload-1", fire: "stand pistol hp fire-1", melee: "stand pistol hp melee" ),
						Type( "pp", fire: "stand pistol hp fire-1", melee: "stand pistol hp melee" ),
						Type( "ne", reload: "stand pistol ne reload-1", melee: "stand pistol hp melee" ),
						Type( "b", melee: "stand pistol hp melee" ) ),
					Class( "missile", Missile, "crouch missile", "stand unarmed airborne", "stand missile land-soft", "stand missile land-hard",
						"stand rifle throw-grenade", "crouch missile aim-still", "crouch missile aim-move",
						Type( "rl", reload: "stand missile rl reload-1", fire: "stand missile rl fire-1", melee: "stand missile rl melee" ) ),
					Class( "plasmacannon", Rifle, "zcrouch cannon", "zstand cannon airborne", "zstand cannon land-soft", "zstand cannon land-hard",
						"zstand cannon throw-grenade", "zcrouch cannon aim-still", "zcrouch cannon aim-move",
						Type( "pc" ) ),
					Class( "flamethrower", CrouchFlame, "zcrouch flame", "zstand flame airborne", "zstand flame land-soft", "zstand flame land-hard",
						"zstand flame throw-grenade", "zcrouch flame aim-still", "zcrouch flame aim-move",
						Type( "ft", reload: "stand rifle sr reload-1" ) ),
					Class( "unarmed", None, "crouch unarmed", "stand unarmed airborne", "stand missile land-soft", "stand missile land-hard",
						"stand rifle throw-grenade", null, null,
						Type( "" ) ),
				},
			},
			new Seat {
				Label = "stand",
				Look = "stand look",
				LookingScreen = Looking,
				AirborneDead = "stand airborne-dead",
				LandingDead = "stand landing-dead",
				Classes = {
					Class( "rifle", Rifle, "stand rifle", "stand rifle airborne", "stand rifle land-soft", "stand rifle land-hard",
						"stand rifle throw-grenade", "stand rifle aim-still", "stand rifle aim-move",
						Type( "ar", reload: "stand rifle ar reload-1", fire: "stand rifle ar fire-1", fire2: "stand rifle ar fire-2", melee: "stand rifle ar melee" ),
						Type( "sg", reload: "stand rifle sg reload-1", chamber: "stand rifle sg chamber-1", fire: "stand rifle sg fire-1", melee: "stand rifle sg melee" ),
						Type( "sr", reload: "stand rifle sr reload-1", melee: "stand rifle sr melee" ),
						Type( "pr", fire: "stand rifle ar fire-1", melee: "stand rifle pr melee" ),
						Type( "pb" ),
						Type( "ne" ),
						Type( "f", melee: "stand rifle f melee" ) ),
					Class( "pistol", Rifle, "stand pistol", "stand pistol airborne", "stand pistol land-soft", "stand pistol land-hard",
						"stand rifle throw-grenade", "stand pistol aim-still", "stand pistol aim-move",
						Type( "hp", reload: "stand pistol hp reload-1", fire: "stand pistol hp fire-1", melee: "stand pistol hp melee" ),
						Type( "pp", fire: "stand pistol hp fire-1", melee: "stand pistol pp melee" ),
						Type( "ne", reload: "stand pistol ne reload-1", melee: "stand pistol ne melee" ),
						Type( "b", melee: "stand pistol b melee" ) ),
					Class( "missile", Missile, "stand missile", "stand missile airborne", "stand missile land-soft", "stand missile land-hard",
						"stand rifle throw-grenade", "stand missile aim-still", "stand missile aim-move",
						Type( "rl", reload: "stand missile rl reload-1", fire: "stand missile rl fire-1", melee: "stand missile rl melee" ) ),
					Class( "plasmacannon", Rifle, "zstand cannon", "zstand cannon airborne", "zstand cannon land-soft", "zstand cannon land-hard",
						"zstand cannon throw-grenade", "zstand cannon aim-still", "zstand cannon aim-move",
						Type( "pc", melee: "zstand cannon melee" ) ),
					Class( "flamethrower", Rifle, "zstand flame", "zstand flame airborne", "zstand flame land-soft", "zstand flame land-hard",
						"zstand flame throw-grenade", "zstand flame aim-still", "zstand flame aim-move",
						Type( "ft", reload: "stand rifle sr reload-1", melee: "zstand flame melee" ) ),
					UnarmedStand(),
				},
			},
			VehicleSeat( "W-driver", "W-driver",
				Seated( "unarmed", Rifle, "W-driver unarmed idle", "W-driver unarmed aim-still", Type( "" ) ) ),
			VehicleSeat( "W-gunner", "W-gunner",
				Seated( "fixed", WarthogGun, "W-gunner fixed idle", "W-gunner fixed aim-still", Type( "" ) ) ),
			VehicleSeat( "W-passenger", "W-passenger",
				Seated( "rifle", Passenger, "W-passenger rifle idle", "W-passenger rifle aim-still",
					Type( "ar", reload: "stand rifle ar reload-1", fire: "stand rifle ar fire-1" ),
					Type( "sg", reload: "stand rifle sg reload-1", fire: "stand rifle sg fire-1" ),
					Type( "sr", reload: "stand rifle sr reload-1", fire: "stand rifle ar fire-1" ),
					Type( "pr", fire: "stand rifle ar fire-1" ),
					Type( "f" ) ),
				Seated( "pistol", Passenger, "W-passenger pistol idle", "W-passenger pistol aim-still",
					Type( "hp", reload: "stand pistol hp reload-1", fire: "stand pistol hp fire-1" ),
					Type( "pp", fire: "stand pistol hp fire-1" ),
					Type( "ne", reload: "stand pistol ne reload-1", fire: "stand pistol hp fire-1" ),
					Type( "b" ) ),
				Seated( "missile", PassengerMissile, "W-passenger missile idle", "W-passenger missile aim-still",
					Type( "rl", reload: "stand missile rl reload-1", fire: "stand missile rl fire-1" ) ),
				Seated( "plasmacannon", Passenger, "zW-passenger cannon idle", "zW-passenger cannon aim-still", Type( "pc" ) ),
				Seated( "flamethrower", Passenger, "zW-passenger flame idle", "zW-passenger flame aim-still", Type( "ft" ) ) ),
			VehicleSeat( "G-driver", "G-driver",
				Seated( "unarmed", None, "G-driver unarmed idle", null, Type( "" ) ) ),
			VehicleSeat( "B-driver", "B-driver",
				Seated( "unarmed", None, "B-driver unarmed idle", null, Type( "" ) ) ),
			VehicleSeat( "scorpion-driver", "scorpion-driver",
				Seated( "unarmed", Rifle, "scorpion-driver unarmed idle", null, Type( "" ) ) ),
			VehicleSeat( "GT-gunner", "GT-gunner",
				Seated( "unarmed", Rifle, "GT-gunner unarmed idle", null, Type( "" ) ),
				Seated( "fixed", None, "GT-gunner fixed idle", null, Type( "" ) ) ),
			ScorpionRider( "scorpionLF" ),
			ScorpionRider( "scorpionRF" ),
			ScorpionRider( "scorpionLB" ),
			ScorpionRider( "scorpionRB" ),
		},
	};

	// Uses the stand seat's look and dying animations, plus its own "<prefix> enter" and "<prefix> exit".
	static Seat VehicleSeat( string label, string animationPrefix, params WeaponClass[] classes ) => new() {
		Label = label,
		Look = "stand look",
		LookingScreen = Looking,
		AirborneDead = "stand airborne-dead",
		LandingDead = "stand landing-dead",
		Enter = $"{animationPrefix} enter",
		Exit = $"{animationPrefix} exit",
		Classes = classes.ToList(),
	};

	// Only idle and aim-still; a seated unit never plays the other slots.
	static WeaponClass Seated( string label, AimingScreen aim, string idle, string aimStill, params WeaponType[] types ) {
		var animations = new Dictionary<string, string> { ["idle"] = idle };
		if ( aimStill is not null ) animations["aim-still"] = aimStill;
		return new WeaponClass { Label = label, AimingScreen = aim, Animations = animations, Types = types.ToList() };
	}

	// The Scorpion's four riders all use the front left one's animations.
	static Seat ScorpionRider( string label ) => VehicleSeat( label, "scorpionLF",
		Seated( "rifle", ScorpionRiderScreen, "scorpionLF rifle idle", "scorpionLF rifle aim-still",
			Type( "ar", fire: "stand rifle ar fire-1" ),
			Type( "pr", fire: "stand rifle ar fire-1" ),
			Type( "ne", fire: "stand rifle ar fire-1" ),
			Type( "sg", fire: "stand rifle sg fire-1" ),
			Type( "sr", fire: "stand rifle sg fire-1" ),
			Type( "f" ) ),
		Seated( "pistol", ScorpionRiderScreen, "scorpionLF rifle idle", "scorpionLF rifle aim-still",
			Type( "hp", fire: "stand pistol hp fire-1" ),
			Type( "pp", fire: "stand pistol hp fire-1" ),
			Type( "b" ) ),
		Seated( "missile", ScorpionRiderScreen, "scorpionLF rifle idle", "scorpionLF rifle aim-still",
			Type( "rl", fire: "stand rifle sg fire-1" ) ),
		Seated( "flamethrower", ScorpionRiderScreen, "zscorpionLF flame idle", "zscorpionLF flame aim-still", Type( "ft" ) ),
		Seated( "plasmacannon", ScorpionRiderScreen, "scorpionLF rifle idle", "scorpionLF rifle aim-still", Type( "pc" ) ) );

	// The cyborg's unit damage block, as read from MCC's bloodgulch.map.
	// No hard pings, and every soft ping plays "s-ping front gut".
	// Soft kills from the front use the chest animation for the upper body; other directions use their gut animation.
	// Hard kills have their own head animations from the front, left and right.
	static string[] CyborgUnitDamage() {
		const string ping = "s-ping front gut%0";
		string[] All( string name ) => Enumerable.Repeat( name, DamagePartCount ).ToArray();
		var none = new string[DamagePartCount];

		// gut, chest, head, l-arm, l-hand, l-leg, l-foot, r-arm, r-hand, r-leg, r-foot
		const string sGut = "s-kill front gut", sChest = "s-kill front chest";
		var softKillFront = new[] { sGut, sChest, sChest, sChest, sChest, sGut, sGut, sChest, sChest, sGut, sGut };
		string[] HardKill( string direction ) {
			var parts = All( $"h-kill {direction} gut" );
			parts[PartHead] = $"h-kill {direction} head";
			return parts;
		}

		return new[] {
			All( ping ), All( ping ), All( ping ), All( ping ),
			none, none, none, none,
			softKillFront, All( "s-kill left gut" ), All( "s-kill right gut" ), All( "s-kill back gut" ),
			HardKill( "front" ), HardKill( "left" ), HardKill( "right" ), All( "h-kill back gut" ),
		}.SelectMany( x => x ).ToArray();
	}

	// Standing unarmed takes its idle, turns and aim from the "alert" animations.
	static WeaponClass UnarmedStand() {
		var c = Class( "unarmed", Rifle, "stand unarmed", "stand unarmed airborne", "stand missile land-soft", "stand missile land-hard",
			"stand rifle throw-grenade", "alert unarmed aim-still", null, Type( "" ) );
		c.Animations["idle"] = "alert unarmed idle%1";
		c.Animations["turn-left"] = "alert unarmed turn-left";
		c.Animations["turn-right"] = "alert unarmed turn-right";
		return c;
	}
}
