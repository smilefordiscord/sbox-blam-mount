using System.Collections.Generic;

namespace Blam.Halo2.TagData;

// Layouts of the Halo 2 (MCC) tags exported as JSON, by tag class.
// They were written for the mount from Assembly's Halo 2 plugins and checked against every MCC map (h2 <maps> layouts).
// A layout only uses layouts from its own file, since static fields of a partial class initialize in file order.
// Option names shared between files are in Options, which initializes on first use.
public static partial class TagLayouts
{
	static IReadOnlyDictionary<TagClass, TagStruct> _byClass;

	public static IReadOnlyDictionary<TagClass, TagStruct> ByClass => _byClass ??= new Dictionary<TagClass, TagStruct>
	{
		[new( "weap" )] = Weapon,
		[new( "proj" )] = Projectile,
		[new( "eqip" )] = Equipment,
		[new( "garb" )] = Garbage,
		[new( "bipd" )] = Biped,
		[new( "vehi" )] = Vehicle,
		[new( "bloc" )] = Crate,
		[new( "jpt!" )] = DamageEffect,
		[new( "coll" )] = CollisionModel,
		[new( "hlmt" )] = Model,
		[new( "phmo" )] = PhysicsModel,
		[new( "itmc" )] = ItemCollection,
		[new( "vehc" )] = VehicleCollection,
		[new( "foot" )] = MaterialEffects,
		[new( "effe" )] = Effect,
		[new( "cont" )] = Contrail,
		[new( "ligh" )] = Light,
		[new( "sncl" )] = SoundClasses,
		[new( "matg" )] = Globals,
		[new( "mulg" )] = MultiplayerGlobals,
		[new( "scnr" )] = Scenario,
		[new( "nhdt" )] = NewHudDefinition,
		[new( "hudg" )] = HudGlobals,
		[new( "bitm" )] = Bitmap,
	};

	// The layout for a tag, or null if its class isn't exported.
	public static TagStruct For( TagEntry tag ) => ByClass.GetValueOrDefault( tag.Class );

	// Option names used by several tags, in the order the game numbers them.
	public static class Options
	{
		public static readonly string[] ObjectType =
		[
			"biped", "vehicle", "weapon", "equipment", "garbage", "projectile", "scenery", "machine", "control", "light fixture",
			"sound scenery", "crate", "creature",
		];

		public static readonly string[] Noise = ["silent", "medium", "loud", "shout", "quiet"];

		public static readonly string[] GrenadeType = ["human fragmentation", "covenant plasma"];

		// What a kill is credited to, for the kill feed and statistics.
		public static readonly string[] DamageReportingType =
		[
			"guardians", "falling damage", "generic collision damage", "generic melee damage", "generic explosion", "magnum pistol",
			"plasma pistol", "needler", "smg", "plasma rifle", "battle rifle", "carbine", "shotgun", "sniper rifle", "beam rifle",
			"rocket launcher", "flak cannon", "brute shot", "disintegrator", "brute plasma rifle", "energy sword", "frag grenade",
			"plasma grenade", "flag melee damage", "bomb melee damage", "bomb explosion damage", "ball melee damage", "human turret",
			"plasma turret", "banshee", "ghost", "mongoose", "scorpion", "spectre driver", "spectre gunner", "warthog driver",
			"warthog gunner", "wraith", "tank", "sentinel beam", "sentinel rpg", "teleporter", "warthog gunner gauss", "warthog gunner rocket",
		];

		public static readonly string[] FunctionType = ["linear", "early", "very early", "late", "very late", "cosine", "one", "zero"];

		public static readonly string[] TransitionFunction = ["linear", "late", "very late", "early", "very early", "cosine", "zero", "one"];

		public static readonly string[] WaveFunction =
		[
			"one", "zero", "cosine", "cosine (variable period)", "diagonal wave", "diagonal wave (variable period)", "slide",
			"slide (variable period)", "noise", "jitter", "wander", "spark",
		];

		public static readonly string[] ProjectileResponse = ["impact (detonate)", "fizzle", "overpenetrate", "attach", "bounce", "bounce (dud)", "fizzle (ricochet)"];

		public static readonly string[] Team =
		[
			"default", "player", "human", "covenant", "flood", "sentinel", "heretic", "prophet", "unused8", "unused9", "unused10",
			"unused11", "unused12", "unused13", "unused14", "unused15",
		];

		public static readonly string[] ModelState = ["default", "minor damage", "medium damage", "major damage", "destroyed"];

		// The material types of Halo CE, which Halo 2 keeps beside its named global materials.
		public static readonly string[] MaterialType =
		[
			"dirt", "sand", "stone", "snow", "wood", "metal (hollow)", "metal (thin)", "metal (thick)", "rubber", "glass", "force field",
			"grunt", "hunter armor", "hunter skin", "elite", "jackal", "jackal energy shield", "engineer skin", "engineer force field",
			"flood combat form", "flood carrier form", "cyborg armor", "cyborg energy shield", "human armor", "human skin", "sentinel",
			"monitor", "plastic", "water", "leaves", "elite energy shield", "ice", "hunter shield",
		];

		public static readonly string[] SweetenerMode = ["sweetener default", "sweetener enabled", "sweetener disabled"];

		public static readonly string[] TeamDesignator = ["red", "blue", "yellow", "green", "purple", "orange", "brown", "pink", "neutral"];

		// Which multiplayer game types a spawn point, item spawn or starting equipment is for.
		public static readonly string[] GameType =
		[
			"none", "ctf", "slayer", "oddball", "king of the hill", "race", "headhunter", "juggernaut", "territories", "stub",
			"ignored3", "ignored4", "all game types", "all except ctf", "all except ctf and race",
		];

		public static readonly string[] SpawnType = ["both", "initial spawn only", "respawn only"];
	}
}
