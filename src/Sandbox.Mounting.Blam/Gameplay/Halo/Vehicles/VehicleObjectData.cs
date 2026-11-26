using System;
using Halo.Combat;

namespace Halo.Vehicles;

// A vehicle's object functions and function inputs, from the mount's export of its tag and its weapon's.
public sealed class VehicleObjectData {
	// Functions A to D.
	public List<ObjectFunctionDefinition> Functions { get; init; } = new();

	// The sources for inputs A to D, such as "unit:driver seat power", in Halo's exporter order.
	// The weapon exports first, then object, unit and vehicle; the last source wins.
	public string[][] Inputs { get; init; } = { Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>() };

	static Dictionary<string, VehicleObjectData> _exported;
	static Dictionary<string, VehicleObjectData> Exported => _exported ??= new( StringComparer.OrdinalIgnoreCase );

	public static VehicleObjectData For( string vehicleTag, string weaponTag = null ) {
		if ( string.IsNullOrEmpty( vehicleTag ) ) return null;
		var path = vehicleTag.Replace( '\\', '/' );
		if ( !Exported.TryGetValue( path, out var exported ) ) {
			exported = VehicleTagExports.Object( path, weaponTag?.Replace( '\\', '/' ) );
			Exported[path] = exported;
		}
		return exported;
	}
}

// An animation driven by an object function.
public sealed record ObjectOverlay( string Animation, int Function, bool ControlsScale );

// A mass point and the suspension overlay it drives.
public sealed record VehicleSuspension( int MassPoint, string Animation, float FullExtensionGroundDepth, float FullCompressionGroundDepth );

// The parts of a vehicle animation graph that the vehicle code looks up.
public sealed class VehicleGraphData {
	public List<ObjectOverlay> ObjectOverlays { get; init; } = new();

	// The "stand" seat's front-back, left-right and up-down acceleration overlays, or none.
	public string[] Acceleration { get; init; } = Array.Empty<string>();

	// The "stand" seat's animations.
	public string Opening { get; init; }
	public string Closing { get; init; }

	// The "fixed" weapon class.
	public string Idle { get; init; }
	public string AimStill { get; init; }
	public UnitAnimationGraph.AimingScreen AimingScreen { get; init; }

	// Overlays of the weapon type matching the vehicle weapon's label.
	public string Fire1 { get; init; }
	public string Fire2 { get; init; }

	// Animation slots: steering, roll, throttle, velocity, braking, ground speed.
	public UnitAnimationGraph.AimingScreen SteeringScreen { get; init; }
	public string[] VehicleAnimations { get; init; } = Array.Empty<string>();
	public List<VehicleSuspension> Suspensions { get; init; } = new();

	public string VehicleAnimation( int slot ) => slot >= 0 && slot < VehicleAnimations.Length ? VehicleAnimations[slot] : null;

	static Dictionary<string, VehicleGraphData> _exported;
	static Dictionary<string, VehicleGraphData> Exported => _exported ??= new( StringComparer.OrdinalIgnoreCase );

	// weaponLabel picks the weapon class and type.
	public static VehicleGraphData For( string graph, string weaponLabel = null ) {
		if ( string.IsNullOrEmpty( graph ) ) return null;
		var path = graph.Replace( '\\', '/' );
		var key = $"{path}|{weaponLabel}";
		if ( !Exported.TryGetValue( key, out var exported ) ) {
			exported = VehicleTagExports.Graph( path, weaponLabel );
			Exported[key] = exported;
		}
		return exported;
	}
}
