using System.Collections.Generic;
using System.Linq;

namespace Blam.Halo1.TagData;

/// <summary>
/// Layouts of the Halo CE (MCC) tags that can be exported as JSON, by tag class.
/// </summary>
/// <remarks>
/// Written for this mount from the tag definitions as c20, Invader and Assembly document them, and
/// checked against MCC's maps (BlamInspect <c>layouts</c> and <c>export</c>). Field names are
/// Halo's own, as its tag editor shows them. Each tag type is in the partial file for its area:
/// objects (TagLayouts.Objects.cs), devices (TagLayouts.Devices.cs), damage (TagLayouts.Damage.cs),
/// the HUD and bitmaps (TagLayouts.Hud.cs), AI actors (TagLayouts.Ai.cs), shaders
/// (TagLayouts.Shaders.cs), and effects, sounds,
/// globals, scenarios and the rest (TagLayouts.Other.cs). Static fields of a partial class
/// initialize in an order that depends on the files, so a layout only uses layouts from its own
/// file, and option names shared between files live in <see cref="Options"/>, which initializes
/// on first use.
/// </remarks>
public static partial class TagLayouts
{
	static IReadOnlyDictionary<TagClass, TagStruct> _byClass;

	/// <summary>
	/// The layout of each exportable tag class.
	/// </summary>
	public static IReadOnlyDictionary<TagClass, TagStruct> ByClass => _byClass ??= new Dictionary<TagClass, TagStruct>
	{
		[new( "weap" )] = Weapon,
		[new( "proj" )] = Projectile,
		[new( "eqip" )] = Equipment,
		[new( "garb" )] = Garbage,
		[new( "bipd" )] = Biped,
		[new( "vehi" )] = Vehicle,
		[new( "mach" )] = Machine,
		[new( "ctrl" )] = Control,
		[new( "lifi" )] = LightFixture,
		[new( "phys" )] = Physics,
		[new( "scen" )] = Scenery,
		[TagClasses.GbxModel] = ModelSkeleton,
		[new( "jpt!" )] = DamageEffect,
		[new( "cdmg" )] = ContinuousDamageEffect,
		[new( "coll" )] = CollisionModel,
		[new( "itmc" )] = ItemCollection,
		[new( "effe" )] = Effect,
		[new( "matg" )] = Globals,
		[new( "foot" )] = MaterialEffects,
		[new( "unhi" )] = UnitHudInterface,
		[new( "wphi" )] = WeaponHudInterface,
		[new( "grhi" )] = GrenadeHudInterface,
		[new( "hudg" )] = HudGlobals,
		[new( "hud#" )] = HudNumber,
		[new( "metr" )] = Meter,
		[TagClasses.Bitmap] = Bitmap,
		[new( "snd!" )] = Sound,
		[new( "deca" )] = Decal,
		[new( "cont" )] = Contrail,
		[new( "trak" )] = CameraTrack,
		[new( "udlg" )] = Dialogue,
		[new( "lsnd" )] = SoundLooping,
		[new( "antr" )] = ModelAnimations,
		[new( "str#" )] = StringList,
		[new( "ustr" )] = UnicodeStringList,
		[new( "DeLa" )] = UiWidget,
		[TagClasses.Scenario] = Scenario,
		[new( "actr" )] = Actor,
		[new( "actv" )] = ActorVariant,
		[TagClasses.ShaderTransparentPlasma] = ShaderTransparentPlasma,
	};

	/// <summary>
	/// The layout for a tag, or null if its class isn't exported.
	/// </summary>
	public static TagStruct For( TagEntry tag ) => ByClass.GetValueOrDefault( tag.Class );

	/// <summary>
	/// Option names used by several tags, in the order the game numbers them.
	/// </summary>
	public static class Options
	{
		public static readonly string[] Noise = ["silent", "medium", "loud", "shout", "quiet"];

		public static readonly string[] FunctionType = ["linear", "early", "very early", "late", "very late", "cosine"];

		public static readonly string[] WaveFunction =
		[
			"one", "zero", "cosine", "cosine variable period", "diagonal wave", "diagonal wave variable period",
			"slide", "slide variable period", "noise", "jitter", "wander", "spark",
		];

		public static readonly string[] GrenadeType = ["human fragmentation", "covenant plasma", "grenade type 2", "grenade type 3"];

		/// <summary>
		/// What a unit counts as for Anniversary's scoring (units and actor variants).
		/// </summary>
		public static readonly string[] MetagameType =
		[
			"brute", "grunt", "jackal", "skirmisher", "marine", "spartan", "bugger", "hunter", "flood infection",
			"flood carrier", "flood combat", "flood pure", "sentinel", "elite", "engineer", "mule", "turret", "mongoose",
			"warthog", "scorpion", "hornet", "pelican", "revenant", "seraph", "shade", "watchtower", "ghost", "chopper",
			"mauler", "wraith", "banshee", "phantom", "scarab", "guntower", "tuning fork", "broadsword", "mammoth", "lich",
			"mantis", "wasp", "phaeton", "bishop", "knight", "pawn",
		];

		public static readonly string[] MetagameClass = ["infantry", "leader", "hero", "specialist", "light vehicle", "heavy vehicle", "giant vehicle", "standard vehicle"];

		/// <summary>
		/// Material types (<see cref="MaterialTypes"/>), with spaces as the tag editor shows them:
		/// what collision surfaces are made of. Also the order of the per-material blocks
		/// (projectile material responses, footsteps, globals materials) and of a damage effect's
		/// modifiers.
		/// </summary>
		public static readonly string[] MaterialTypeNames = MaterialTypes.Names.Select( x => x.Replace( '_', ' ' ) ).ToArray();

		public static readonly string[] Team =
		[
			"none", "player", "human", "covenant", "flood", "sentinel", "unused6", "unused7", "unused8", "unused9",
		];

		/// <summary>
		/// Which multiplayer game types a spawn point or item spawn is for.
		/// </summary>
		public static readonly string[] GameType =
		[
			"none", "ctf", "slayer", "oddball", "king of the hill", "race", "terminator", "stub",
			"ignored1", "ignored2", "ignored3", "ignored4", "all games", "all except ctf", "all except race and ctf",
		];
	}
}
