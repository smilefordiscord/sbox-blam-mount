using System;
using Halo.Data;
using Halo.Combat;

namespace Halo.Vehicles;

// Builds VehicleObjectData and VehicleGraphData from the mount's tag documents.
// Reads object functions and their input sources, and the graph's object, unit and vehicle blocks.
static class VehicleTagExports {
	sealed class FunctionFile {
		public string[] Flags { get; set; }
		public float Period { get; set; } = 1;
		public string ScalePeriodBy { get; set; }
		public string Function { get; set; }
		public string ScaleFunctionBy { get; set; }
		public string WobbleFunction { get; set; }
		public float WobblePeriod { get; set; }
		public float WobbleMagnitude { get; set; }
		public float SquareWaveThreshold { get; set; }
		public int StepCount { get; set; }
		public string MapTo { get; set; }
		public int SawtoothCount { get; set; }
		public string Add { get; set; }
		public string ScaleResultBy { get; set; }
		public string BoundsMode { get; set; }
		public float[] Bounds { get; set; }
		public int? TurnOffWith { get; set; }
		public float ScaleBy { get; set; }
		public string Usage { get; set; }
	}

	sealed class ObjectFile {
		public List<FunctionFile> Functions { get; set; }
		public string AIn { get; set; }
		public string BIn { get; set; }
		public string CIn { get; set; }
		public string DIn { get; set; }
		public string UnitAIn { get; set; }
		public string UnitBIn { get; set; }
		public string UnitCIn { get; set; }
		public string UnitDIn { get; set; }
		public string VehicleAIn { get; set; }
		public string VehicleBIn { get; set; }
		public string VehicleCIn { get; set; }
		public string VehicleDIn { get; set; }
		public string WeaponAIn { get; set; }
		public string WeaponBIn { get; set; }
		public string WeaponCIn { get; set; }
		public string WeaponDIn { get; set; }
	}

	static readonly string[] References = { "none", "a in", "b in", "c in", "d in", "a out", "b out", "c out", "d out" };

	static int Reference( string name ) => Math.Max( 0, Array.IndexOf( References, name ?? "none" ) );

	// Null if the tag has no document.
	public static VehicleObjectData Object( string objectTag, string weaponTag ) {
		var file = HaloDataStore.Tag<ObjectFile>( objectTag );
		if ( file is null ) return null;
		var weapon = string.IsNullOrEmpty( weaponTag ) ? null : HaloDataStore.Tag<ObjectFile>( weaponTag );

		var functions = file.Functions.Select( f => new ObjectFunctionDefinition {
			Invert = f.Flags?.Contains( "invert" ) ?? false,
			Additive = f.Flags?.Contains( "additive" ) ?? false,
			AlwaysActive = f.Flags?.Contains( "always active" ) ?? false,
			Period = f.Period,
			ScalePeriodBy = Reference( f.ScalePeriodBy ),
			Function = f.Function ?? "one",
			ScaleFunctionBy = Reference( f.ScaleFunctionBy ),
			WobbleFunction = f.WobbleFunction ?? "one",
			WobblePeriod = f.WobblePeriod,
			WobbleMagnitude = f.WobbleMagnitude,
			SquareWaveThreshold = f.SquareWaveThreshold,
			StepCount = f.StepCount,
			MapTo = f.MapTo ?? "linear",
			SawtoothCount = f.SawtoothCount,
			Add = Reference( f.Add ),
			ScaleResultBy = Reference( f.ScaleResultBy ),
			BoundsMode = f.BoundsMode switch { "clip and normalize" => 1, "scale to fit" => 2, _ => 0 },
			LowerBound = f.Bounds is { Length: >= 1 } b ? b[0] : 0,
			UpperBound = f.Bounds is { Length: >= 2 } c ? c[1] : 1,
			TurnOffWith = f.TurnOffWith ?? -1,
			ScaleBy = f.ScaleBy,
			Usage = f.Usage,
		} ).ToList();

		// In exporter order: weapon, object, unit, vehicle; the last one set wins.
		string[] Sources( string weaponIn, string objectIn, string unitIn, string vehicleIn ) {
			var list = new List<string>();
			void Add( string kind, string mode ) {
				if ( !string.IsNullOrEmpty( mode ) && mode != "none" ) list.Add( $"{kind}:{mode}" );
			}
			Add( "weapon", weaponIn );
			Add( "object", objectIn );
			Add( "unit", unitIn );
			Add( "vehicle", vehicleIn );
			return list.ToArray();
		}

		return new VehicleObjectData {
			Functions = functions,
			Inputs = new[] {
				Sources( weapon?.WeaponAIn, file.AIn, file.UnitAIn, file.VehicleAIn ),
				Sources( weapon?.WeaponBIn, file.BIn, file.UnitBIn, file.VehicleBIn ),
				Sources( weapon?.WeaponCIn, file.CIn, file.UnitCIn, file.VehicleCIn ),
				Sources( weapon?.WeaponDIn, file.DIn, file.UnitDIn, file.VehicleDIn ),
			},
		};
	}

	// Animation graphs

	sealed class Bounds {
		public float RightYawPerFrame { get; set; }
		public float LeftYawPerFrame { get; set; }
		public int RightFrameCount { get; set; }
		public int LeftFrameCount { get; set; }
		public float DownPitchPerFrame { get; set; }
		public float UpPitchPerFrame { get; set; }
		public int DownPitchFrameCount { get; set; }
		public int UpPitchFrameCount { get; set; }

		public UnitAnimationGraph.AimingScreen Screen() {
			const float Degree = MathF.PI / 180;
			return new( RightYawPerFrame * Degree, LeftYawPerFrame * Degree, RightFrameCount, LeftFrameCount,
				DownPitchPerFrame * Degree, UpPitchPerFrame * Degree, DownPitchFrameCount, UpPitchFrameCount );
		}
	}

	sealed class SlotFile {
		public string Slot { get; set; }
		public int? Animation { get; set; }
	}

	sealed class OverlayFile {
		public int? Animation { get; set; }
		public string Function { get; set; }
		public string FunctionControls { get; set; }
		public string AnimationName { get; set; }
	}

	sealed class WeaponTypeFile {
		public string Label { get; set; }
		public List<SlotFile> Animations { get; set; } = new();
	}

	sealed class WeaponClassFile {
		public string Name { get; set; }
		public Bounds AimingScreenBounds { get; set; }
		public List<SlotFile> Animations { get; set; } = new();
		public List<WeaponTypeFile> WeaponTypes { get; set; } = new();
	}

	sealed class SeatFile {
		public string Label { get; set; }
		public List<SlotFile> Animations { get; set; } = new();
		public List<WeaponClassFile> Weapons { get; set; } = new();
	}

	sealed class SuspensionFile {
		public int? MassPointIndex { get; set; }
		public int? Animation { get; set; }
		public float FullExtensionGroundDepth { get; set; }
		public float FullCompressionGroundDepth { get; set; }
		public string AnimationName { get; set; }
	}

	sealed class VehicleFile {
		public Bounds SteeringScreenBounds { get; set; }
		public List<SlotFile> Animations { get; set; } = new();
		public List<SuspensionFile> SuspensionAnimations { get; set; } = new();
	}

	sealed class AnimationFile {
		public string Name { get; set; }
	}

	sealed class GraphFile {
		public List<OverlayFile> Objects { get; set; }
		public List<SeatFile> Units { get; set; }
		public List<VehicleFile> Vehicles { get; set; }
		public List<AnimationFile> Animations { get; set; } = new();
	}

	static readonly string[] VehicleSlots = { "steering", "roll", "throttle", "velocity", "braking", "ground-speed" };

	// The weapon class and type come from the "stand" seat by weapon label; as in Halo, the last match wins.
	// Null if the tag has no document.
	public static VehicleGraphData Graph( string graph, string weaponLabel ) {
		var file = HaloDataStore.Tag<GraphFile>( graph );
		if ( file is null ) return null;

		string Name( int? index ) => index is int i && i >= 0 && i < file.Animations.Count ? file.Animations[i].Name : null;
		string SlotAnimation( List<SlotFile> slots, string slot ) => Name( slots?.FirstOrDefault( s => s.Slot == slot )?.Animation );

		var seat = file.Units.FirstOrDefault( s => s.Label == "stand" ) ?? file.Units.FirstOrDefault();
		WeaponClassFile weaponClass = null;
		WeaponTypeFile weaponType = null;
		foreach ( var c in seat?.Weapons ?? new() ) {
			foreach ( var t in c.WeaponTypes ?? new() ) {
				if ( string.Equals( t.Label, weaponLabel ?? "", StringComparison.OrdinalIgnoreCase ) ) {
					weaponClass = c;
					weaponType = t;
				}
			}
		}
		weaponClass ??= seat?.Weapons?.FirstOrDefault();

		var acceleration = new[] { "acc-front-back", "acc-left-right", "acc-up-down" }.Select( s => SlotAnimation( seat?.Animations, s ) ).ToArray();
		var vehicle = file.Vehicles.FirstOrDefault();

		return new VehicleGraphData {
			ObjectOverlays = file.Objects
				.Where( o => o.Animation is not null )
				.Select( o => new ObjectOverlay( o.AnimationName ?? Name( o.Animation ),
					o.Function is { Length: > 0 } f ? f[0] - 'a' : 0, o.FunctionControls == "scale" ) )
				.ToList(),
			Acceleration = acceleration.Any( x => x is not null ) ? acceleration : Array.Empty<string>(),
			Opening = SlotAnimation( seat?.Animations, "opening" ),
			Closing = SlotAnimation( seat?.Animations, "closing" ),
			Idle = SlotAnimation( weaponClass?.Animations, "idle" ),
			AimStill = SlotAnimation( weaponClass?.Animations, "aim-still" ),
			AimingScreen = weaponClass?.AimingScreenBounds?.Screen() ?? default,
			Fire1 = SlotAnimation( weaponType?.Animations, "fire-1" ),
			Fire2 = SlotAnimation( weaponType?.Animations, "fire-2" ),
			SteeringScreen = vehicle?.SteeringScreenBounds?.Screen() ?? default,
			VehicleAnimations = VehicleSlots.Select( s => SlotAnimation( vehicle?.Animations, s ) ).ToArray(),
			Suspensions = (vehicle?.SuspensionAnimations ?? new())
				.Where( s => s.MassPointIndex is not null && s.Animation is not null )
				.Select( s => new VehicleSuspension( s.MassPointIndex.Value, s.AnimationName ?? Name( s.Animation ), s.FullExtensionGroundDepth, s.FullCompressionGroundDepth ) )
				.ToList(),
		};
	}
}
