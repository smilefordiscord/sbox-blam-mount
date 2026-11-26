using System;
using Halo.Data;

namespace Halo.Combat;

public sealed partial class UnitAnimationGraph {
	static readonly Dictionary<string, UnitAnimationGraph> _fromTag = new( StringComparer.OrdinalIgnoreCase );

	// Any unit's graph from the mount's antr export, or null without one.
	public static UnitAnimationGraph FromTag( string graph ) {
		if ( string.IsNullOrEmpty( graph ) ) return null;
		graph = graph.Replace( '\\', '/' );
		if ( !graph.EndsWith( ".antr", StringComparison.OrdinalIgnoreCase ) ) graph += ".antr";
		if ( _fromTag.TryGetValue( graph, out var cached ) ) return cached;

		var file = HaloDataStore.Tag<GraphFile>( graph );
		var result = file is null ? null : Build( graph, file );
		_fromTag[graph] = result;
		return result;
	}

	static UnitAnimationGraph Build( string graph, GraphFile file ) {
		string Name( int? index ) => index is { } i && i >= 0 && i < file.Animations.Count ? file.Animations[i].Name : null;

		Dictionary<string, string> Slots( List<SlotFile> slots ) {
			var map = new Dictionary<string, string>();
			foreach ( var slot in slots )
				if ( Name( slot.Animation ) is { } name ) map[slot.Slot] = name;
			return map;
		}

		var result = new UnitAnimationGraph { Graph = graph, UnitDamage = file.UnitDamage.Select( slot => Name( slot.Animation ) ).ToArray() };
		foreach ( var unit in file.Units ) {
			var animations = Slots( unit.Animations );
			var seat = new Seat {
				Label = unit.Label,
				Look = animations.GetValueOrDefault( "look" ),
				LookingScreen = Screen( unit.LookingScreenBounds ),
				AirborneDead = animations.GetValueOrDefault( "airborne-dead" ),
				LandingDead = animations.GetValueOrDefault( "landing-dead" ),
				Enter = animations.GetValueOrDefault( "enter" ),
				Exit = animations.GetValueOrDefault( "exit" ),
				Emotions = animations.GetValueOrDefault( "emotions" ),
				Talk = animations.GetValueOrDefault( "talk" ),
			};

			foreach ( var weapon in unit.Weapons ) {
				var weaponClass = new WeaponClass {
					Label = weapon.Name,
					HandMarker = weapon.HandMarker ?? "",
					GripMarker = weapon.GripMarker ?? "",
					AimingScreen = Screen( weapon.AimingScreenBounds ),
					Animations = Slots( weapon.Animations ),
				};
				foreach ( var type in weapon.WeaponTypes )
					weaponClass.Types.Add( new WeaponType { Label = type.Label, Animations = Slots( type.Animations ) } );
				seat.Classes.Add( weaponClass );
			}
			result.Seats.Add( seat );
		}

		// A permuted animation's chain starts at the animation no other animation names as its next.
		var isNext = new HashSet<int>( file.Animations.Where( a => a.NextAnimation is >= 0 ).Select( a => a.NextAnimation.Value ) );
		for ( var i = 0; i < file.Animations.Count; i++ ) {
			var head = file.Animations[i];
			if ( isNext.Contains( i ) || head.NextAnimation is not >= 0 ) continue;

			var chain = new List<(string, float)>();
			for ( var a = i; a >= 0 && a < file.Animations.Count && chain.Count < file.Animations.Count; a = file.Animations[a].NextAnimation ?? -1 )
				chain.Add( (file.Animations[a].Name, file.Animations[a].Weight) );
			result.Permutations[head.Name] = chain.ToArray();
		}

		return result;
	}

	// Right and down are the negative sides of the frame grid.
	// The tag's angles are in degrees.
	static AimingScreen Screen( ScreenFile s ) => s is null ? default : new AimingScreen(
		s.RightYawPerFrame.DegreeToRadian(), s.LeftYawPerFrame.DegreeToRadian(), s.RightFrameCount, s.LeftFrameCount,
		s.DownPitchPerFrame.DegreeToRadian(), s.UpPitchPerFrame.DegreeToRadian(), s.DownPitchFrameCount, s.UpPitchFrameCount );

	sealed class GraphFile {
		public List<UnitFile> Units { get; set; } = new();
		public List<SlotFile> UnitDamage { get; set; } = new();
		public List<AnimationFile> Animations { get; set; } = new();
	}

	sealed class UnitFile {
		public string Label { get; set; }
		public ScreenFile LookingScreenBounds { get; set; }
		public List<SlotFile> Animations { get; set; } = new();
		public List<WeaponFile> Weapons { get; set; } = new();
	}

	sealed class WeaponFile {
		public string Name { get; set; }
		public string GripMarker { get; set; }
		public string HandMarker { get; set; }
		public ScreenFile AimingScreenBounds { get; set; }
		public List<SlotFile> Animations { get; set; } = new();
		public List<WeaponTypeFile> WeaponTypes { get; set; } = new();
	}

	sealed class WeaponTypeFile {
		public string Label { get; set; }
		public List<SlotFile> Animations { get; set; } = new();
	}

	sealed class SlotFile {
		public string Slot { get; set; }
		public int? Animation { get; set; }
	}

	sealed class ScreenFile {
		public float RightYawPerFrame { get; set; }
		public float LeftYawPerFrame { get; set; }
		public int RightFrameCount { get; set; }
		public int LeftFrameCount { get; set; }
		public float DownPitchPerFrame { get; set; }
		public float UpPitchPerFrame { get; set; }
		public int DownPitchFrameCount { get; set; }
		public int UpPitchFrameCount { get; set; }
	}

	sealed class AnimationFile {
		public string Name { get; set; }
		public float Weight { get; set; }
		public int? NextAnimation { get; set; }
	}
}
