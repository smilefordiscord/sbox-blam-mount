using System;
using Halo2.Data;

namespace Halo2.Combat;

// A unit model's animation graph from the mount's anims/<model>.json: its labels, animations and aiming grids.
// Labels are "mode:class:type:label"; base states come from a weapon type's actions, the rest from its overlays.
public sealed class UnitGraph {
	sealed class GraphFile {
		public List<LabelEntry> Labels { get; set; } = new();
		public List<FirstPersonGraph.Animation> Animations { get; set; } = new();
		public List<FirstPersonGraph.BlendScreen> BlendScreens { get; set; } = new();
		public List<string> LeftArmNodes { get; set; }
		public List<string> RightArmNodes { get; set; }
	}

	sealed class LabelEntry {
		public string Mode { get; set; }
		public string WeaponClass { get; set; }
		public string WeaponType { get; set; }
		public string Label { get; set; }
		public bool Overlay { get; set; }
		public string Animation { get; set; }
	}

	readonly Dictionary<string, string> _labels = new( StringComparer.OrdinalIgnoreCase );
	readonly Dictionary<string, FirstPersonGraph.Animation> _animations = new( StringComparer.OrdinalIgnoreCase );
	readonly Dictionary<string, UnitClip> _clips = new( StringComparer.OrdinalIgnoreCase );
	readonly List<FirstPersonGraph.BlendScreen> _screens = new();

	// The arm masks by node name that the hands' arm offsets and grips keep to; null until the mount exports them.
	public IReadOnlySet<string> LeftArmNodes { get; private set; }
	public IReadOnlySet<string> RightArmNodes { get; private set; }

	static readonly Dictionary<string, UnitGraph> Cache = new();
	static int _cacheGeneration;

	// The graph of a model tag, or null if the mount has none.
	public static UnitGraph For( string model ) {
		var stem = Combat.HaloEffects.WithoutExtension( model );
		if ( stem is null ) return null;
		if ( _cacheGeneration != HaloDataStore.Generation ) {
			_cacheGeneration = HaloDataStore.Generation;
			Cache.Clear();
		}
		if ( Cache.TryGetValue( stem, out var cached ) ) return cached;

		var file = HaloDataStore.ReadMount( $"anims/{stem}.json" ) is { Length: > 0 } json ? Json.Deserialize<GraphFile>( json ) : null;
		UnitGraph graph = null;
		if ( file is not null ) {
			graph = new UnitGraph();
			foreach ( var l in file.Labels.Where( x => x.Animation is not null ) )
				graph._labels.TryAdd( Key( l.Overlay, l.Mode, l.WeaponClass, l.WeaponType, l.Label ), l.Animation );
			foreach ( var a in file.Animations ) graph._animations.TryAdd( a.Name, a );
			graph._screens.AddRange( file.BlendScreens );
			graph.LeftArmNodes = file.LeftArmNodes?.ToHashSet( StringComparer.OrdinalIgnoreCase );
			graph.RightArmNodes = file.RightArmNodes?.ToHashSet( StringComparer.OrdinalIgnoreCase );
		}

		Cache[stem] = graph;
		return graph;
	}

	static string Key( bool overlay, string mode, string weaponClass, string weaponType, string label ) =>
		$"{(overlay ? "o" : "a")}:{mode}:{weaponClass}:{weaponType}:{label}";

	// The weapon's animation class and type; a unit without a weapon is "unarmed", type "any".
	public static (string Class, string Type) WeaponOf( WeaponTag weapon ) =>
		weapon is null ? ("unarmed", "any") : (Or( weapon.WeaponClass, "unarmed" ), Or( weapon.WeaponName, "any" ));

	static string Or( string value, string fallback ) => string.IsNullOrEmpty( value ) ? fallback : value;

	// Halo 2 tries the type, then any type, then any class, then any mode: the first block holding the label wins.
	// A crouching label the crouch mode lacks doesn't fall back to combat.
	public FirstPersonGraph.Animation Find( string mode, string weaponClass, string weaponType, string label, bool overlay ) {
		for ( var any = 0; any < 8; any++ ) {
			var t = (any & 1) != 0 ? "any" : weaponType;
			var c = (any & 2) != 0 ? "any" : weaponClass;
			var m = (any & 4) != 0 ? "any" : mode;
			if ( _labels.TryGetValue( Key( overlay, m, c, t, label ), out var name ) && _animations.TryGetValue( name, out var animation ) )
				return animation;
		}
		return null;
	}

	// An animation by its name, such as "s_kill:front:gut:var0"; null if the graph has none.
	public FirstPersonGraph.Animation Named( string name ) => name is not null && _animations.TryGetValue( name, out var a ) ? a : null;

	// An animation's frames, loaded once.
	public UnitClip Clip( FirstPersonGraph.Animation animation ) {
		if ( animation?.Resource is null ) return null;
		if ( _clips.TryGetValue( animation.Name, out var cached ) ) return cached;
		var clip = AnimationClip.Load( animation.Resource ) is { } frames ? new UnitClip( animation, frames, Screen( animation ) ) : null;
		_clips[animation.Name] = clip;
		return clip;
	}

	public UnitClip Clip( string mode, string weaponClass, string weaponType, string label, bool overlay ) =>
		Clip( Find( mode, weaponClass, weaponType, label, overlay ) );

	// A variant of a chain head, rolled as Halo 2 does when its weight is under 1.
	public FirstPersonGraph.Animation PickVariant( FirstPersonGraph.Animation head ) => FirstPersonGraph.PickVariant( head, Named );

	FirstPersonGraph.BlendScreen Screen( FirstPersonGraph.Animation animation ) =>
		animation.BlendScreen >= 0 && animation.BlendScreen < _screens.Count ? _screens[animation.BlendScreen] : null;

	// The combat label's animation for a weapon, as the combat step times it.
	public static AnimationEntry FindCombat( string model, WeaponTag weapon, string label ) {
		var (weaponClass, weaponType) = WeaponOf( weapon );
		var graph = For( model );
		return graph?.Find( "combat", weaponClass, weaponType, label, true ) ?? graph?.Find( "combat", weaponClass, weaponType, label, false );
	}

	// A weapon model's own animation by its label, from the "dual" class while its holder dual wields.
	public static AnimationEntry FindWeaponModel( string model, string label, bool dual ) => For( model )?.WeaponModel( label, dual );

	public FirstPersonGraph.Animation WeaponModel( string label, bool dual ) => Find( "any", dual ? DualClass : "default", "default", label, false );

	// The class a dual wielding unit animates with.
	public const string DualClass = "dual";
}

// An animation's frames with its tracks in a model's bone order, its blend screen and its root motion speed.
public sealed class UnitClip {
	public FirstPersonGraph.Animation Animation { get; }
	public AnimationClip Frames { get; }
	public FirstPersonGraph.BlendScreen Screen { get; }

	// The root motion's mean speed in world units per second, as Halo 2 measures a move to match a walk to it.
	public float Speed { get; }

	public int FrameCount => Frames.FrameCount;
	public bool Overlay => Frames.Overlay;
	public bool Replacement => Frames.Replacement;

	AnimationClip.Track[] _byBone;
	string[] _bones;

	public UnitClip( FirstPersonGraph.Animation animation, AnimationClip frames, FirstPersonGraph.BlendScreen screen ) {
		Animation = animation;
		Frames = frames;
		Screen = screen;
		var motion = frames.Motion;
		if ( motion.Length > 1 ) {
			var sum = Vector3.Zero;
			foreach ( var m in motion ) sum += m.Move;
			Speed = HaloUnits.ToWorldUnits( sum.Length ) / (frames.FrameCount - 1) * HaloUnits.TicksPerSecond;
		}
	}

	// The tracks in the order of these bone names, null for bones it doesn't drive.
	public AnimationClip.Track[] Tracks( string[] bones ) {
		if ( _bones == bones ) return _byBone;
		_bones = bones;
		_byBone = bones.Select( x => Frames.Tracks.TryGetValue( x, out var t ) ? t : null ).ToArray();
		return _byBone;
	}

	// The root turn over a frame, radians left.
	public float Yaw( int frame ) => frame >= 0 && frame < Frames.Motion.Length ? Frames.Motion[frame].Yaw : 0;
}
