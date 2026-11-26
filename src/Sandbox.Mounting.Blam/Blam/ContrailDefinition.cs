using System;
using System.Collections.Generic;

namespace Blam;

// Inside the namespace so these win over s&box's global Vector2/Vector4 when this compiles into the mount.
using Vector2 = System.Numerics.Vector2;
using Vector4 = System.Numerics.Vector4;

public enum ContrailRenderType : ushort
{
	VerticalOrientation = 0,
	HorizontalOrientation = 1,
	MediaMapped = 2,
	GroundMapped = 3,
	ViewerFacing = 4,
	DoubleMarkerLinked = 5,
}

// A stage of a contrail point's life: a width (world units) and RGBA color for a while, then a blend into the next.
// Durations are ranges in seconds.
public sealed record ContrailPointState( Vector2 Duration, Vector2 Transition, float Width, Vector4 ColorLower, Vector4 ColorUpper );

// What Halo CE's and Halo 2's contrail tags (cont) share: Halo 2 kept CE's contrail fields and moved only its shader part.
// A marker drops points at a fixed rate, each ages through the point states, and the ribbon is drawn through them.
public abstract class ContrailDefinition
{
	public ushort Flags { get; protected init; }

	// Points per second.
	public float PointRate { get; protected init; }

	public Vector2 PointVelocity { get; protected init; }
	public float ConeAngle { get; protected init; }
	public float InheritedVelocity { get; protected init; }
	public ContrailRenderType RenderType { get; protected init; }
	public Vector2 TextureRepeats { get; protected init; }
	public Vector2 TextureAnimation { get; protected init; }
	public float AnimationRate { get; protected init; }
	public int FirstSequence { get; protected init; }
	public int SequenceCount { get; protected init; }

	// As a particle's: 0x2 nonlinear tint.
	public ushort ShaderFlags { get; protected init; }

	public IReadOnlyList<ContrailPointState> PointStates { get; protected init; } = [];

	public bool NonlinearTint => (ShaderFlags & 0x2) != 0;
	public bool FirstPointUnfaded => (Flags & 0x1) != 0;
	public bool LastPointUnfaded => (Flags & 0x2) != 0;

	// Points age a tick at a time, and those dropped in a tick are born at its start.
	// So each point holds its first look for a tick.
	public const float Tick = 1 / 30f;

	// A point's width (world units) and gamma space RGBA color over its life, from the first tick's hold to its expiry.
	// Keys sit at each state's start and the end of its duration, and blend linearly between (Halo's transitions).
	// Colors are the states' upper bounds: the lower bounds only count for contrails scaled by density.
	public IReadOnlyList<(float Age, float Width, Vector4 Color)> PointKeys()
	{
		var keys = new List<(float, float, Vector4)>();
		if ( PointStates.Count == 0 )
			return keys;

		var age = Tick;
		keys.Add( (0, PointStates[0].Width, PointStates[0].ColorUpper) );
		for ( var i = 0; i < PointStates.Count; i++ )
		{
			var state = PointStates[i];
			keys.Add( (age, state.Width, state.ColorUpper) );
			age += (state.Duration.X + state.Duration.Y) / 2;
			keys.Add( (age, state.Width, state.ColorUpper) );
			if ( i + 1 < PointStates.Count )
				age += (state.Transition.X + state.Transition.Y) / 2;
		}

		return keys;
	}

	// Seconds a point lasts, the first tick's hold included (at least two ticks).
	public float PointLife()
	{
		var keys = PointKeys();
		return keys.Count == 0 ? 0 : Math.Max( keys[^1].Age, Tick * 2 );
	}

	// Times the texture repeats along a whole contrail: Halo steps u back by the u repeats at each point it drops.
	public float RepeatsAlongTrail() => (TextureRepeats.X == 0 ? 1 : TextureRepeats.X) * Math.Max( PointRate * PointLife(), 1 );

	// A point's gamma space RGBA color at an age in seconds.
	public Vector4 ColorAt( float age )
	{
		var keys = PointKeys();
		for ( var i = 1; i < keys.Count; i++ )
		{
			if ( age > keys[i].Age )
				continue;

			var (a0, _, c0) = keys[i - 1];
			var (a1, _, c1) = keys[i];
			return Vector4.Lerp( c0, c1, a1 > a0 ? (age - a0) / (a1 - a0) : 1 );
		}

		return keys.Count == 0 ? Vector4.Zero : keys[^1].Color;
	}
}
