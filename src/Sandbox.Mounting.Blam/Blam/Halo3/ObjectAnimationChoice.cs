using System;
using System.Linq;
using AnimationType = Blam.Halo1.Tags.AnimationType;
using Blam.Halo3.Tags;

namespace Blam.Halo3;

// What a placed object plays when the level starts with nothing interacting with it, as Halo 2's Halo2ObjectAnimations picks: units idle, scenery and other objects play their idle, gears turn; doors and platforms stay in their default pose.
// Rate is the playback speed; ClosesLoop means the gear's cycle needs the model's copy with a closing frame (Halo3ModelAnimations.LoopingPosition).
public readonly record struct ObjectAnimationChoice( string Animation, bool Looping, float Rate, bool ClosesLoop )
{
	// Units hold no weapon here: the graph's unarmed idle first, then any weapon class's, the patrol idle (jackals), the any idle (Cortana, the Scarab's combat:any:any:idle), else the shortest ...:idle base animation.
	static readonly (string Mode, string Class, string Type, string Label)[] UnitIdles =
	[
		("combat", "unarmed", "any", "idle"),
		("combat", "any", "any", "idle"),
		("patrol", "unarmed", "any", "idle"),
		("any", "any", "any", "idle"),
	];

	public static ObjectAnimationChoice? Pick( TagEntry objectTag, ObjectType type, ModelAnimationSet set )
	{
		if ( set is null )
			return null;

		switch ( type )
		{
			case ObjectType.Biped or ObjectType.Giant or ObjectType.Creature:
			{
				var idle = UnitIdles.Select( l => set.FindLabel( l.Mode, l.Class, l.Type, l.Label ) ).FirstOrDefault( a => a is not null ) ?? ShortestIdle( set );
				return idle is null ? null : new ObjectAnimationChoice( idle.Name, true, 1, false );
			}

			case ObjectType.Machine when MachineTag.Read( objectTag ) is { } machine:
			{
				if ( machine.Type != MachineType.Gear || machine.PositionRate <= 0 )
					return null;

				// A gear runs through its position animation (0 to 1) over and over at the tag's rate in positions per second; a cycle is its frame count less one when its last frame is its first, else one more step back round to it.
				var position = set.FindLabel( "any", "any", "any", "position" ) ?? set.Find( "device:position" );
				if ( position is null || position.Animation.FrameCount < 2 )
					return null;

				var closes = set.ClosingFrame( position ) is not null;
				var steps = closes ? position.Animation.FrameCount : position.Animation.FrameCount - 1;
				return new ObjectAnimationChoice( position.Name, true, steps / position.Animation.FrameRate * machine.PositionRate, closes );
			}

			case ObjectType.Vehicle:
				// Vehicles stand in their rest pose, as Halo 2's, for a game to drive.
				return null;

			default:
			{
				// Scenery, crates, weapons and equipment play the graph's any idle (Blackout's windsock, Heretic's capital ships, a weapon's resting pose).
				var idle = set.FindLabel( "any", "any", "any", "idle" );
				return idle is null || idle.Animation.Type == AnimationType.Overlay ? null : new ObjectAnimationChoice( idle.Name, true, 1, false );
			}
		}
	}

	static RenderModelAnimation ShortestIdle( ModelAnimationSet set ) => set.Animations
		.Where( a => a.Animation.Type == AnimationType.Base && a.Name.EndsWith( ":idle", StringComparison.OrdinalIgnoreCase ) )
		.OrderBy( a => a.Name.Length ).ThenBy( a => a.Name, StringComparer.OrdinalIgnoreCase )
		.FirstOrDefault();
}
