using Halo2.Data;
using System;

namespace Halo2.Combat;

// One animation of a graph in the mount's anims/<graph>.json, with its frame events.
public class AnimationEntry {
	public string Name { get; set; }
	public int FrameCount { get; set; }
	// The frames of its first events of each kind, -1 for none.
	public int KeyFrame { get; set; } = -1;
	public int SecondKeyFrame { get; set; } = -1;
	public int AllowInterruptionFrame { get; set; } = -1;
	public List<AnimationSoundEvent> SoundEvents { get; set; } = new();
	public List<AnimationEffectEvent> EffectEvents { get; set; } = new();
	public List<AnimationFrameEvent> FrameEvents { get; set; } = new();

	public AnimationTiming Timing => new( FrameCount, Math.Max( KeyFrame, 0 ), Math.Max( SecondKeyFrame, 0 ), Math.Max( AllowInterruptionFrame, 0 ) );

	// Whether a frame event of this type, such as "body impact", is on the frame.
	public bool HasFrameEvent( string type, int frame ) => FrameEvents?.Any( x => x.Frame == frame && x.Type == type ) ?? false;
}

// A marked frame: "primary keyframe", "left foot", "body impact"...
public sealed class AnimationFrameEvent {
	public string Type { get; set; }
	public int Frame { get; set; }
}

// A sound an animation starts on a frame, at a marker or the object's origin.
// Flags: "allow on player", "left arm only", "right arm only", "first person only", "forward only", "reverse only".
public sealed class AnimationSoundEvent {
	public string Sound { get; set; }
	public int Frame { get; set; }
	public string Marker { get; set; }
	public string[] Flags { get; set; } = Array.Empty<string>();

	public bool HasFlag( string flag ) => Flags?.Contains( flag ) ?? false;
}

public sealed class AnimationEffectEvent {
	public string Effect { get; set; }
	public int Frame { get; set; }
	public string[] Flags { get; set; } = Array.Empty<string>();

	public bool HasFlag( string flag ) => Flags?.Contains( flag ) ?? false;
}

// Halo 2 plays an animation's events on the update its frame passes them, always at scale 1.
// The units here play forward, so events limited to playing in reverse never fire.
// Only a dual wielded first person hand plays for one arm; whole bodies never fire events limited to an arm.
public static class AnimationEvents {
	static bool Fires( string[] flags, int frame, int eventFrame, Hand? arm = null ) =>
		eventFrame == frame && !(flags?.Any( x => x switch {
			"left arm only" => arm != Hand.Left,
			"right arm only" => arm != Hand.Right,
			"reverse only" => true,
			_ => false,
		} ) ?? false);

	// A first person animation plays only its sounds, from the weapon, as the local player's own.
	// One flagged "first person only" waits for a first person camera.
	// arm is the hand of a dual wielded weapon's state, null single wielded.
	public static void FirstPerson( AnimationEntry animation, int frame, Vector3 weapon, bool firstPersonCamera, object source, Hand? arm ) {
		foreach ( var e in animation?.SoundEvents ?? Enumerable.Empty<AnimationSoundEvent>() ) {
			if ( !Fires( e.Flags, frame, e.Frame, arm ) || (e.HasFlag( "first person only" ) && !firstPersonCamera) ) continue;
			Combat.HaloSounds.Play( e.Sound, weapon, source: source, own: true );
		}
	}

	// A unit's animation plays its sounds only when flagged "allow on player", as every unit here is a player's.
	// Its effects go on the unit, their locations at its markers. own marks the local player's unit.
	public static void ThirdPerson( AnimationEntry animation, int frame, GameObject unit, Func<string, GameObject> marker, object source, bool own, bool firstPerson ) {
		if ( animation is null || !unit.IsValid() ) return;
		foreach ( var e in animation.SoundEvents ) {
			if ( !Fires( e.Flags, frame, e.Frame ) || !e.HasFlag( "allow on player" ) ) continue;
			var at = string.IsNullOrEmpty( e.Marker ) ? null : marker( e.Marker );
			Combat.HaloSounds.Play( e.Sound, at.IsValid() ? at.WorldPosition : unit.WorldPosition, source: source, own: own );
		}
		foreach ( var e in animation.EffectEvents ) {
			if ( !Fires( e.Flags, frame, e.Frame ) ) continue;
			var effect = Combat.HaloEffects.SpawnEffect( e.Effect, unit.WorldPosition, unit.WorldRotation.Forward, firstPerson ? "__fp" : null, unit, source, own );
			Combat.HaloEffects.PlaceLocations( effect, marker );
		}
	}
}
