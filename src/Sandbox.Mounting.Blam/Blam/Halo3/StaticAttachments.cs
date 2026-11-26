using System;
using System.Collections.Generic;
using System.Linq;
using Blam.Halo3.Tags;

namespace Blam.Halo3;

// Inside the namespace so these win over s&box's global vector types when this compiles into the mount.
using Quaternion = System.Numerics.Quaternion;
using Vector3 = System.Numerics.Vector3;

// A lens flare that stays where it is: world position (world units), the marker's forward, what put it there.
public sealed record StaticFlare( LensFlareTag Lens, Vector3 Position, Vector3 Forward, string Source );

// A light volume system that stays where it is: world position, orientation (x forward), scale.
public sealed record StaticLightVolume( TagEntry LightVolume, Vector3 Position, Quaternion Rotation, float Scale, string Source );

// The lens flares and light volumes the scenario's placed objects carry for as long as they exist: each object's attachments (obje +0xBC, 0x20: type, marker, primary scale function +0x18) that are a lens flare, a light with one (ligh +0x84), a light volume, or a looping effect (effe +0x18 loop start event >= 0) whose parts are any of those.
// Objects: the ScenarioPlan's (what the scene places), the sky, and the effect scenery placements (s3d_edge and s3d_waterfall put their suns there). Markers come from the object's render model in its default pose; a missing marker is the object's origin.
public sealed class StaticAttachments
{
	static readonly TagClass Lens = new( "lens" );
	static readonly TagClass Light = new( "ligh" );
	static readonly TagClass LightVolume = new( "ltvl" );
	static readonly TagClass Effect = new( "effe" );

	public List<StaticFlare> Flares { get; } = new();
	public List<StaticLightVolume> LightVolumes { get; } = new();

	// One-shot effects attached to placed objects, left out (counted for the survey).
	public int OneShotEffects { get; private set; }

	// Attachments an object function scales on items (a weapon's flashlight, a plasma pistol's charge, a vehicle's tail lights), left out: an item at rest has them off. Level objects (scenery, machines, controls, crates, effect scenery, the sky) keep theirs: their functions run by themselves (shrine's beacons, Sandbox's tower).
	public int FunctionScaled { get; private set; }

	public static StaticAttachments Gather( ScenarioPlan plan )
	{
		var result = new StaticAttachments();
		var markers = new Dictionary<TagEntry, Dictionary<string, (Vector3 Position, Quaternion Rotation)>>();

		foreach ( var planned in plan.Objects )
		{
			if ( planned.Marker )
				continue;
			result.AddObject( planned.Placement.Object, planned.Model, Frame( planned.Placement ), planned.Placement.Scale, markers, planned.Object.Type is ObjectType.Scenery or ObjectType.Machine or ObjectType.Control or ObjectType.Crate );
		}

		if ( plan.Sky?.Object is { Address: not 0 } sky )
		{
			var model = new ObjectTag( sky ).Model is { Address: not 0 } hlmt ? new ObjectModel( hlmt ) : null;
			result.AddObject( sky, model, (Vector3.Zero, Quaternion.Identity), 1, markers, true );
		}

		foreach ( var placement in plan.Scenario.Placements )
		{
			if ( placement.Kind != PlacementKind.EffectScenery || placement.Object is not { Address: not 0 } || placement.NotAutomatically || placement.NeverPlaced || !plan.Shown( placement ) )
				continue;
			var model = new ObjectTag( placement.Object ).Model is { Address: not 0 } hlmt ? new ObjectModel( hlmt ) : null;
			result.AddObject( placement.Object, model, Frame( placement ), placement.Scale, markers, true );
		}

		return result;
	}

	static (Vector3, Quaternion) Frame( ScenarioPlacement placement ) => (placement.Position, placement.Orientation);

	void AddObject( TagEntry objectTag, ObjectModel model, (Vector3 Position, Quaternion Rotation) frame, float scale, Dictionary<TagEntry, Dictionary<string, (Vector3, Quaternion)>> markerCache, bool levelObject )
	{
		Dictionary<string, (Vector3, Quaternion)> markers = null;
		(Vector3 Position, Quaternion Rotation) At( string marker )
		{
			markers ??= Markers( model?.RenderModel, markerCache );
			var local = !string.IsNullOrEmpty( marker ) && markers.TryGetValue( marker, out var m ) ? m : (Vector3.Zero, Quaternion.Identity);
			return (frame.Position + Vector3.Transform( local.Item1 * scale, frame.Rotation ), Quaternion.Normalize( frame.Rotation * local.Item2 ));
		}

		foreach ( var a in objectTag.Data.Block( 0xBC, 0x20 ) )
		{
			var type = a.Reference( 0 );
			if ( type is null || type.Address == 0 )
				continue;
			if ( !levelObject && !string.IsNullOrEmpty( a.StringId( 0x18 ) ) )
			{
				FunctionScaled++;
				continue;
			}

			var marker = a.StringId( 0x10 );
			var source = $"{objectTag.Path}@{marker}";

			if ( type.Class == Effect )
			{
				var effect = type.Data;
				if ( effect.I16( 0x18 ) < 0 )
				{
					OneShotEffects++;
					continue;
				}

				// The looping events (from the loop start on), each part at its location turned by its relative orientation (yaw, then pitch raising the forward); "up" and "gravity" point the part along the world's +z / -z.
				var locations = effect.Block( 0x20, 0xC ).Select( l => l.StringId( 0 ) ).ToList();
				var events = effect.Block( 0x2C, 0x44 );
				for ( var ev = effect.I16( 0x18 ); ev < events.Count; ev++ )
				{
					foreach ( var part in events[ev].Block( 0x20, 0x60 ) )
					{
						var partType = part.Reference( 0x10 );
						if ( partType is null || partType.Address == 0 )
							continue;
						var location = part.I16( 4 );
						var locationMarker = location >= 0 && location < locations.Count ? locations[location] : null;
						var (position, rotation) = At( locationMarker is null or "" or "root" or "up" or "gravity" ? marker : locationMarker );
						if ( locationMarker is "up" or "gravity" )
							rotation = Quaternion.CreateFromAxisAngle( Vector3.UnitY, locationMarker == "up" ? -MathF.PI / 2 : MathF.PI / 2 );
						position += Vector3.Transform( part.Vec3( 0x44 ) * scale, rotation );
						var (yaw, pitch) = (part.F32( 0x50 ), part.F32( 0x54 ));
						rotation = Quaternion.Normalize( rotation * Quaternion.CreateFromAxisAngle( Vector3.UnitZ, yaw ) * Quaternion.CreateFromAxisAngle( Vector3.UnitY, -pitch ) );
						Add( partType, position, rotation, scale, $"{source} {type.Path}" );
					}
				}
				continue;
			}

			var (p, r) = At( marker );
			Add( type, p, r, scale, source );
		}
	}

	void Add( TagEntry type, Vector3 position, Quaternion rotation, float scale, string source )
	{
		if ( type.Class == Lens && new LensFlareTag( type ).FirstPersonOnly )
			return;
		if ( type.Class == Lens )
			Flares.Add( new StaticFlare( new LensFlareTag( type ), position, Vector3.Transform( Vector3.UnitX, rotation ), source ) );
		else if ( type.Class == Light && type.Data.Reference( 0x84 ) is { Address: not 0 } lens && lens.Class == Lens )
			Flares.Add( new StaticFlare( new LensFlareTag( lens ), position, Vector3.Transform( Vector3.UnitX, rotation ), $"{source} {type.Path}" ) );
		else if ( type.Class == LightVolume )
			LightVolumes.Add( new StaticLightVolume( type, position, rotation, scale, source ) );
	}

	// A render model's markers in model space (default pose), by name (the first of a name).
	static Dictionary<string, (Vector3, Quaternion)> Markers( TagEntry renderModel, Dictionary<TagEntry, Dictionary<string, (Vector3, Quaternion)>> cache )
	{
		if ( renderModel is null || renderModel.Address == 0 )
			return new Dictionary<string, (Vector3, Quaternion)>();
		if ( cache.TryGetValue( renderModel, out var known ) )
			return known;

		var result = new Dictionary<string, (Vector3, Quaternion)>( StringComparer.OrdinalIgnoreCase );
		var model = new RenderModelTag( renderModel );
		var pose = model.DefaultPose();
		foreach ( var marker in model.Markers )
		{
			if ( string.IsNullOrEmpty( marker.Name ) || result.ContainsKey( marker.Name ) )
				continue;
			var (t, r) = marker.Node >= 0 && marker.Node < pose.Length ? pose[marker.Node] : (Vector3.Zero, Quaternion.Identity);
			result[marker.Name] = (t + Vector3.Transform( marker.Translation, r ), Quaternion.Normalize( r * marker.Rotation ));
		}
		return cache[renderModel] = result;
	}
}
